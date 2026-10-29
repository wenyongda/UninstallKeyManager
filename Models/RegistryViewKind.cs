namespace UninstallKeyManager.Models;

/// <summary>
/// The registry view (bitness) an uninstall entry was read from.
/// A 32-bit application installed on 64-bit Windows writes to the same logical
/// path, but the WOW64 layer redirects it under <c>WOW6432Node</c>. Reading only
/// one view silently hides every application of the other bitness.
/// </summary>
public enum RegistryViewKind
{
    /// <summary>
    /// The 64-bit registry view (<see cref="Microsoft.Win32.RegistryView.Registry64"/>).
    /// </summary>
    Registry64,

    /// <summary>
    /// The 32-bit registry view (<see cref="Microsoft.Win32.RegistryView.Registry32"/>).
    /// </summary>
    Registry32
}
