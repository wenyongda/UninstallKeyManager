using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using UninstallKeyManager.Models;

namespace UninstallKeyManager.Services;

/// <summary>
/// Reads and writes the <c>SystemComponent</c> value that decides whether an
/// application is listed in "Programs and Features".
/// </summary>
public class RegistryService : IRegistryService
{
    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// The hive / view pairs scanned. The order decides which view wins when one
    /// application is present in both the 32-bit and the 64-bit view: the 64-bit
    /// view of HKLM comes first, then its 32-bit twin.
    /// </summary>
    /// <remarks>
    /// HKEY_CURRENT_USER\Software is not redirected by WOW64, so reading HKCU with
    /// <see cref="RegistryView.Registry32"/> returns exactly the same keys as
    /// <see cref="RegistryView.Registry64"/> does. Scanning it twice would report
    /// every per-user application as a 32/64-bit duplicate and make the app write
    /// the same value to the same key twice.
    /// </remarks>
    private static readonly (RegistryHive Hive, RegistryView View, RegistrySource Source, RegistryViewKind Kind)[] ScanTargets =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64, RegistrySource.HKLM, RegistryViewKind.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32, RegistrySource.HKLM, RegistryViewKind.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Registry64, RegistrySource.HKCU, RegistryViewKind.Registry64)
    };

    /// <inheritdoc/>
    public Task<ScanResult> ScanAsync()
    {
        return Task.Run(() =>
        {
            var result = new ScanResult();
            var collected = new List<UninstallEntry>();

            foreach (var target in ScanTargets)
            {
                var (entries, skippedWithoutName) = ReadEntriesFromHive(target.Hive, target.View, target.Source, target.Kind);
                collected.AddRange(entries);
                result.SkippedWithoutDisplayName += skippedWithoutName;
                if (target.Source == RegistrySource.HKLM)
                {
                    result.HklmCount += entries.Count;
                }
                else
                {
                    result.HkcuCount += entries.Count;
                }
            }

            // One application is registered under the same key name in both the
            // 32-bit and the 64-bit view. Collapse those pairs so the list matches
            // what Control Panel shows, and keep every view the key lives in so a
            // single toggle hides the application everywhere.
            var merged = new List<UninstallEntry>(collected.Count);
            var slotByKey = new Dictionary<(RegistrySource Source, string KeyName), int>();

            foreach (var entry in collected)
            {
                var key = (entry.Source, entry.KeyName);

                if (!slotByKey.TryGetValue(key, out var slot))
                {
                    slotByKey[key] = merged.Count;
                    entry.Views.Add(entry.ViewKind);
                    merged.Add(entry);
                    continue;
                }

                result.MergedDuplicates++;
                merged[slot].Views.Add(entry.ViewKind);
            }

            merged.Sort((left, right) =>
                string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase));

            result.Entries.AddRange(merged);
            return result;
        });
    }

    /// <inheritdoc/>
    public Task<RegistryWriteResult> SetSystemComponentAsync(UninstallEntry entry, bool isSystemComponent)
    {
        return Task.Run(() =>
        {
            var views = entry.Views.Count > 0
                ? (IReadOnlyList<RegistryViewKind>)entry.Views
                : new RegistryViewKind[] { entry.ViewKind };
            var failure = RegistryWriteResult.Ok();
            var failed = false;

            foreach (var kind in views)
            {
                var result = WriteSystemComponent(entry, kind, isSystemComponent);
                if (result.Success)
                {
                    continue;
                }

                failed = true;
                if (!failure.Success)
                {
                    // Keep the first reason, which is the one that explains the cause.
                    continue;
                }

                failure = result;
            }

            return failed ? failure : RegistryWriteResult.Ok();
        });
    }

    /// <summary>
    /// Writes and verifies the <c>SystemComponent</c> value in one registry view.
    /// </summary>
    /// <param name="entry">The entry to change.</param>
    /// <param name="kind">The registry view to write.</param>
    /// <param name="isSystemComponent">True to hide, false to show.</param>
    /// <returns>The outcome of the write.</returns>
    private static RegistryWriteResult WriteSystemComponent(
        UninstallEntry entry, RegistryViewKind kind, bool isSystemComponent)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(ToHive(entry.Source), ToView(kind));
            using var key = baseKey.OpenSubKey(entry.RegistryPath, writable: true);
            if (key is null)
            {
                return RegistryWriteResult.Failed($"找不到注册表键：{entry.RegistryPath}");
            }

            if (isSystemComponent)
            {
                // DWORD(32 位) = 1, the value appwiz.cpl tests for.
                key.SetValue("SystemComponent", 1, RegistryValueKind.DWord);
            }
            else
            {
                // No value at all is the "not hidden" state; a 0 would work too.
                key.DeleteValue("SystemComponent", throwOnMissingValue: false);
            }

            // Read the value back: the write must be provable, not assumed.
            if (ReadSystemComponent(key) != isSystemComponent)
            {
                return RegistryWriteResult.Failed("写入后校验失败，注册表值与预期不一致。");
            }

            return RegistryWriteResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return RegistryWriteResult.Denied();
        }
        catch (System.Security.SecurityException)
        {
            return RegistryWriteResult.Denied();
        }
        catch (Exception ex)
        {
            return RegistryWriteResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Reads every subkey of the Uninstall key in one hive / view pair.
    /// </summary>
    /// <param name="hive">The hive to read.</param>
    /// <param name="view">The registry view (bitness) to read.</param>
    /// <param name="source">The source shown in the UI.</param>
    /// <param name="kind">The view kind stored on each entry.</param>
    /// <returns>
    /// The readable entries, and how many subkeys were skipped for lacking a
    /// <c>DisplayName</c>. Control Panel skips them too.
    /// </returns>
    private static (List<UninstallEntry> Entries, int SkippedWithoutDisplayName) ReadEntriesFromHive(
        RegistryHive hive, RegistryView view, RegistrySource source, RegistryViewKind kind)
    {
        var entries = new List<UninstallEntry>();
        var skipped = 0;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(UninstallKeyPath);
            if (uninstallKey is null)
            {
                return (entries, skipped);
            }

            foreach (var keyName in uninstallKey.GetSubKeyNames())
            {
                try
                {
                    using var subKey = uninstallKey.OpenSubKey(keyName);
                    if (subKey is null)
                    {
                        continue;
                    }

                    var displayName = subKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        skipped++;
                        continue;
                    }

                    entries.Add(new UninstallEntry
                    {
                        DisplayName = displayName,
                        Publisher = subKey.GetValue("Publisher") as string ?? string.Empty,
                        DisplayVersion = subKey.GetValue("DisplayVersion") as string ?? string.Empty,
                        InstallLocation = subKey.GetValue("InstallLocation") as string ?? string.Empty,
                        UninstallString = subKey.GetValue("UninstallString") as string ?? string.Empty,
                        IsSystemComponent = ReadSystemComponent(subKey),
                        Source = source,
                        ViewKind = kind,
                        RegistryPath = $@"{UninstallKeyPath}\{keyName}",
                        KeyName = keyName
                    });
                }
                catch (Exception)
                {
                    // An unreadable subkey is not an entry Control Panel can list.
                    continue;
                }
            }
        }
        catch (Exception)
        {
            // The hive or view may not exist; an empty list is the honest answer.
        }

        return (entries, skipped);
    }

    /// <summary>
    /// Reads the <c>SystemComponent</c> value as a boolean.
    /// </summary>
    /// <param name="key">The open uninstall subkey.</param>
    /// <returns>True when the value is present and non-zero.</returns>
    private static bool ReadSystemComponent(RegistryKey key)
    {
        var value = key.GetValue("SystemComponent", null, RegistryValueOptions.DoNotExpandEnvironmentNames);

        return value switch
        {
            int i => i != 0,
            uint u => u != 0,
            long l => l != 0,
            ulong ul => ul != 0,
            byte[] b => b.Length > 0 && b.Any(x => x != 0),
            string s => !string.IsNullOrWhiteSpace(s)
                       && !s.Trim().Equals("0", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    /// <summary>
    /// Maps a <see cref="RegistrySource"/> onto a <see cref="RegistryHive"/>.
    /// </summary>
    /// <param name="source">The source to map.</param>
    /// <returns>The matching hive.</returns>
    private static RegistryHive ToHive(RegistrySource source)
    {
        return source == RegistrySource.HKLM ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
    }

    /// <summary>
    /// Maps a <see cref="RegistryViewKind"/> onto a <see cref="RegistryView"/>.
    /// </summary>
    /// <param name="kind">The view kind to map.</param>
    /// <returns>The matching registry view.</returns>
    private static RegistryView ToView(RegistryViewKind kind)
    {
        return kind == RegistryViewKind.Registry64 ? RegistryView.Registry64 : RegistryView.Registry32;
    }
}
