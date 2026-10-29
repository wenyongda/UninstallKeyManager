namespace UninstallKeyManager.Models;

/// <summary>
/// Represents the Windows registry root source
/// </summary>
public enum RegistrySource
{
    /// <summary>
    /// HKEY_LOCAL_MACHINE - System-wide settings (requires admin to write)
    /// </summary>
    HKLM,

    /// <summary>
    /// HKEY_CURRENT_USER - User-specific settings
    /// </summary>
    HKCU
}
