using System;
using System.Diagnostics;
using System.Security.Principal;

namespace UninstallKeyManager.Services;

/// <summary>
/// Detects whether the process can write HKEY_LOCAL_MACHINE, and offers a way to
/// restart with an administrator token.
/// </summary>
/// <remarks>
/// HKLM writes need an elevated token. HKCU writes never do, so only the entries
/// whose source is HKLM can end up here.
/// </remarks>
public static class AdminService
{
    /// <summary>
    /// Checks whether the current process carries an administrator token.
    /// </summary>
    /// <returns>True when the process is elevated.</returns>
    public static bool IsUserAnAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Checks whether the application is running from an MSIX package.
    /// </summary>
    /// <returns>True when packaged.</returns>
    /// <remarks>
    /// A packaged application is activated by the package and cannot elevate
    /// itself, so an HKLM write has to be reported as impossible rather than
    /// retried.
    /// </remarks>
    public static bool IsPackaged()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether the MSIX package this process runs from still redirects
    /// HKEY_CURRENT_USER writes into the package's private hive.
    /// </summary>
    /// <returns>
    /// True when packaged and the manifest does not disable registry write
    /// virtualization; false when unpackaged or when the opt-out is declared.
    /// </returns>
    /// <remarks>
    /// A virtualized write is invisible to every other process, Control Panel
    /// included, yet reads merge the private hive with the real HKCU, so the
    /// write-back check in <see cref="RegistryService"/> still passes. That makes
    /// the failure completely silent, which is why the UI warns about it instead
    /// of trusting the manifest to stay correct.
    /// </remarks>
    public static bool IsRegistryWriteVirtualized()
    {
        try
        {
            var installed = Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
            var manifest = System.IO.Path.Combine(installed, "AppxManifest.xml");
            if (!System.IO.File.Exists(manifest))
            {
                return false;
            }

            var text = System.IO.File.ReadAllText(manifest);
            return !text.Contains("RegistryWriteVirtualization>disabled", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Not packaged: nothing redirects the writes.
            return false;
        }
    }

    /// <summary>
    /// Starts a second copy of this executable with an administrator token.
    /// </summary>
    /// <returns>True when the elevated process was started.</returns>
    /// <remarks>
    /// The caller is expected to exit the current process right after, so the
    /// elevated copy becomes the only instance.
    /// </remarks>
    public static bool TryRestartElevated()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                Verb = "runas"
            };

            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception)
        {
            // The user dismissing the UAC prompt lands here.
            return false;
        }
    }
}
