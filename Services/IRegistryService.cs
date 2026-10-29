using System.Collections.Generic;
using System.Threading.Tasks;
using UninstallKeyManager.Models;

namespace UninstallKeyManager.Services;

/// <summary>
/// Service for reading and writing the registry values that decide whether an
/// application is listed in "Programs and Features".
/// </summary>
public interface IRegistryService
{
    /// <summary>
    /// Scans every hive and registry view for uninstall entries, one per
    /// application, in the order Control Panel would list them.
    /// </summary>
    /// <returns>The scan result, including what was merged or skipped.</returns>
    Task<ScanResult> ScanAsync();

    /// <summary>
    /// Sets or clears the <c>SystemComponent</c> value of one entry and reads it
    /// back to prove the change.
    /// </summary>
    /// <param name="entry">The entry to modify.</param>
    /// <param name="isSystemComponent">
    /// True to write DWORD 1 and hide the entry; false to delete the value and
    /// show it again.
    /// </param>
    /// <returns>The outcome, including whether elevation is needed.</returns>
    Task<RegistryWriteResult> SetSystemComponentAsync(UninstallEntry entry, bool isSystemComponent);
}
