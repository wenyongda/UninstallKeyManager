using System.Collections.Generic;

namespace UninstallKeyManager.Services;

/// <summary>
/// Outcome of a registry scan across every hive and registry view.
/// </summary>
public sealed class ScanResult
{
    /// <summary>
    /// The uninstall entries that Control Panel would list, one per application.
    /// Entries without a <c>DisplayName</c> are skipped, and the 32-bit / 64-bit
    /// duplicates of a single application are merged.
    /// </summary>
    public List<Models.UninstallEntry> Entries { get; } = new();

    /// <summary>
    /// Number of subkeys skipped because they carry no <c>DisplayName</c>. They are
    /// not listed by Control Panel either, so they cannot be hidden or shown.
    /// </summary>
    public int SkippedWithoutDisplayName { get; set; }

    /// <summary>
    /// Number of duplicate key names collapsed by merging the 32-bit and the
    /// 64-bit view of the same application.
    /// </summary>
    public int MergedDuplicates { get; set; }

    /// <summary>
    /// Number of entries coming from HKEY_LOCAL_MACHINE.
    /// </summary>
    public int HklmCount { get; set; }

    /// <summary>
    /// Number of entries coming from HKEY_CURRENT_USER.
    /// </summary>
    public int HkcuCount { get; set; }
}

/// <summary>
/// Outcome of a single <c>SystemComponent</c> write.
/// </summary>
public sealed class RegistryWriteResult
{
    /// <summary>
    /// Whether the value is now in the requested state.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Whether the failure was caused by missing elevation. HKLM writes need an
    /// administrator token; HKCU writes never do.
    /// </summary>
    public bool RequiresElevation { get; init; }

    /// <summary>
    /// Human-readable failure reason, when the write did not succeed.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <returns>A result with <see cref="Success"/> set.</returns>
    public static RegistryWriteResult Ok() => new() { Success = true };

    /// <summary>
    /// Creates a result describing a permission failure that elevation would fix.
    /// </summary>
    /// <returns>A result with <see cref="RequiresElevation"/> set.</returns>
    public static RegistryWriteResult Denied() => new() { Success = false, RequiresElevation = true };

    /// <summary>
    /// Creates a result describing a non-permission failure.
    /// </summary>
    /// <param name="message">Reason to surface to the user.</param>
    /// <returns>A failed result.</returns>
    public static RegistryWriteResult Failed(string message) => new() { Success = false, Message = message };
}
