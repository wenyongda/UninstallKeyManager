using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace UninstallKeyManager.Models;

/// <summary>
/// Represents a Windows uninstall registry entry as shown — or hidden — in
/// "Programs and Features" (appwiz.cpl).
/// </summary>
/// <remarks>
/// "Programs and Features" lists every subkey under
/// <c>SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall</c> that has a
/// <c>DisplayName</c>, except those carrying a non-zero <c>SystemComponent</c>
/// value. That value is the only supported switch for hiding an entry from the
/// list while leaving the application installed.
/// </remarks>
public class UninstallEntry : INotifyPropertyChanged
{
    // Badge colours. Brushes are built lazily on first read so that they are
    // created on the UI thread, which is where the bindings that consume them
    // are evaluated.
    private static readonly Windows.UI.Color HklmColor = Windows.UI.Color.FromArgb(255, 66, 156, 227);
    private static readonly Windows.UI.Color HkcuColor = Windows.UI.Color.FromArgb(255, 91, 183, 91);
    private static readonly Windows.UI.Color DefaultColor = Windows.UI.Color.FromArgb(255, 128, 128, 128);
    private static readonly Windows.UI.Color HiddenColor = Windows.UI.Color.FromArgb(255, 196, 99, 40);
    private static readonly Windows.UI.Color View32Color = Windows.UI.Color.FromArgb(255, 139, 92, 246);

    private static SolidColorBrush? _hklmBrush;
    private static SolidColorBrush? _hkcuBrush;
    private static SolidColorBrush? _defaultBrush;
    private static SolidColorBrush? _hiddenBrush;
    private static SolidColorBrush? _view32Brush;

    private bool _isSystemComponent;

    /// <summary>
    /// Display name of the application, exactly as Control Panel renders it.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Publisher/manufacturer name.
    /// </summary>
    public string Publisher { get; set; } = string.Empty;

    /// <summary>
    /// Version string displayed to the user.
    /// </summary>
    public string DisplayVersion { get; set; } = string.Empty;

    /// <summary>
    /// Installation directory path.
    /// </summary>
    public string InstallLocation { get; set; } = string.Empty;

    /// <summary>
    /// Command used to uninstall the application.
    /// </summary>
    public string UninstallString { get; set; } = string.Empty;

    /// <summary>
    /// Whether this entry is marked as a system component, which removes it from
    /// the "Programs and Features" list. Raises <see cref="PropertyChanged"/> so
    /// compiled bindings can refresh the row and the detail pane.
    /// </summary>
    public bool IsSystemComponent
    {
        get => _isSystemComponent;
        set
        {
            if (_isSystemComponent != value)
            {
                _isSystemComponent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SystemComponentDisplay));
                OnPropertyChanged(nameof(HiddenBadgeVisibility));
            }
        }
    }

    /// <summary>
    /// Registry source (HKLM for machine-wide, HKCU for the current user).
    /// </summary>
    public RegistrySource Source { get; set; }

    /// <summary>
    /// The registry view (bitness) this entry was read from.
    /// </summary>
    public RegistryViewKind ViewKind { get; set; }

    /// <summary>
    /// Every registry view in which this key name exists.
    /// </summary>
    /// <remarks>
    /// An installer can write the same key name to both the 32-bit and the 64-bit
    /// view. Control Panel reads both, so hiding the entry has to set
    /// <c>SystemComponent</c> in every view that contains the key, or the entry
    /// stays visible through the other one.
    /// </remarks>
    public List<RegistryViewKind> Views { get; } = new();

    /// <summary>
    /// Full registry path of this entry, relative to the hive root.
    /// </summary>
    public string RegistryPath { get; set; } = string.Empty;

    /// <summary>
    /// Registry key name (the subkey name under the Uninstall key). The same key
    /// name can exist in both the 32-bit and the 64-bit view for one application.
    /// </summary>
    public string KeyName { get; set; } = string.Empty;

    /// <summary>
    /// Source display name for the UI.
    /// </summary>
    public string SourceDisplay => Source switch
    {
        RegistrySource.HKLM => "系统",
        RegistrySource.HKCU => "用户",
        _ => "未知"
    };

    /// <summary>
    /// Whether the entry came from the 32-bit (WOW6432Node) view.
    /// </summary>
    public string ViewKindDisplay => ViewKind == RegistryViewKind.Registry32 ? "32 位" : "64 位";

    /// <summary>
    /// The views this entry lives in, spelled out for the detail pane.
    /// </summary>
    public string ViewsDisplay
    {
        get
        {
            if (Views.Count <= 1)
            {
                return ViewKindDisplay;
            }

            var names = Views
                .Distinct()
                .OrderBy(view => view)
                .Select(view => view == RegistryViewKind.Registry64 ? "64 位" : "32 位");

            return string.Join(" + ", names);
        }
    }

    /// <summary>
    /// Hidden-state text shown in the detail pane.
    /// </summary>
    public string SystemComponentDisplay => IsSystemComponent ? "已隐藏" : "未隐藏";

    /// <summary>
    /// Visibility of the "已隐藏" badge in the list.
    /// </summary>
    public Visibility HiddenBadgeVisibility => IsSystemComponent ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Visibility of the 32-bit view badge in the list. The 64-bit view is the
    /// common case, so it is left unmarked to keep the rows uncluttered.
    /// </summary>
    public Visibility View32BadgeVisibility => ViewKind == RegistryViewKind.Registry32 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Background brush for the source badge.
    /// </summary>
    public Brush SourceBadgeBrush => Source switch
    {
        RegistrySource.HKLM => _hklmBrush ??= new SolidColorBrush(HklmColor),
        RegistrySource.HKCU => _hkcuBrush ??= new SolidColorBrush(HkcuColor),
        _ => _defaultBrush ??= new SolidColorBrush(DefaultColor)
    };

    /// <summary>
    /// Background brush for the "已隐藏" badge.
    /// </summary>
    public Brush HiddenBadgeBrush => _hiddenBrush ??= new SolidColorBrush(HiddenColor);

    /// <summary>
    /// Background brush for the 32-bit view badge.
    /// </summary>
    public Brush View32BadgeBrush => _view32Brush ??= new SolidColorBrush(View32Color);

    /// <summary>
    /// Returns the display name, which is what assistive technology reads for a
    /// list row. Without this the automation name of every row is the type name.
    /// </summary>
    /// <returns>The application name shown in the list.</returns>
    public override string ToString() => DisplayName;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises <see cref="PropertyChanged"/> for the given property.
    /// </summary>
    /// <param name="propertyName">Property name; defaults to the caller's.</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
