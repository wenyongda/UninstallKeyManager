# UninstallKeyManager

WinUI 3 desktop application for managing Windows uninstall registry keys.

## Tech Stack

- **Framework**: .NET 10.0, Windows App SDK 2.2.0
- **UI**: WinUI 3 with XAML
- **Target**: Windows 10 1809+ (min version 10.0.17763.0)
- **Platforms**: x86, x64, ARM64
- **Packaging**: MSIX (single-project packaging)
- **Nullable**: Enabled globally

## Build Commands

```bash
# Restore NuGet packages (required before first build)
dotnet restore

# Build (default: Debug, platform from launch profile)
dotnet build

# Build for specific platform
dotnet build -c Release -p:Platform=x64

# Publish self-contained (ReadyToRun + Trimmed in Release)
dotnet publish -c Release -p:Platform=x64
```

## Project Structure

```
├── App.xaml(.cs)           # Entry point, creates MainWindow in OnLaunched()
├── MainWindow.xaml(.cs)    # Main window with MicaBackdrop
├── Package.appxmanifest    # MSIX package manifest
├── app.manifest            # OS compatibility, DPI awareness (PerMonitorV2)
├── UninstallKeyManager.csproj  # SDK-style project, .NET 10.0
├── UninstallKeyManager.slnx    # New XML solution format (not legacy .sln)
└── Properties/
    ├── launchSettings.json     # Launch profiles (Package/Unpackaged)
    └── PublishProfiles/        # Per-platform publish configs (win-x86/x64/arm64)
```

## Key Configuration

- **Solution format**: `.slnx` (new XML format, not legacy `.sln`)
- **Target framework**: `net10.0-windows10.0.19041.0`
- **Capabilities**: `runFullTrust`, `unvirtualizedResources`, `systemAIModels`
- **Registry write virtualization**: MUST stay disabled. `desktop6:RegistryWriteVirtualization=disabled` in `Package.appxmanifest` (with the `unvirtualizedResources` restricted capability) is what lets the packaged build write to the real `HKCU`. Without it MSIX redirects HKCU writes into `%LOCALAPPDATA%\Packages\<PFN>\SystemAppData\Helium\User.dat`, the app's own read-back still succeeds, and Control Panel never sees the change.
- **Publish**: SelfContained=True, PublishReadyToRun=True (Release), PublishTrimmed=True (Release)

## WinUI 3 Conventions

- XAML namespace: `Microsoft.UI.Xaml` (not `Windows.UI.Xaml`)
- Window backdrop: `MicaBackdrop` (Windows 11 material)
- Use `XamlControlsResources` for WinUI controls styling
- Entry point: `App.OnLaunched()` creates and activates `MainWindow`

## Development Notes

- **No test project** - this is a single-project solution without tests
- **Unpackaged mode** available via launch profile (no MSIX required for dev)
- MSIX packaging requires Windows 10 1899+ for sideloading
- Self-contained publish includes .NET runtime in output
- Run `dotnet restore` if packages are missing
