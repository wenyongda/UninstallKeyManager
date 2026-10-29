# UninstallKeyManager

**控制面板隐藏管理器** — 查看并管理 Windows「程序和功能」（`appwiz.cpl`）中已安装程序的 `SystemComponent` 注册表项，让选中的应用在控制面板里隐藏或重新显示，而**不影响软件本身**。

WinUI 3 桌面应用，单项目 MSIX 打包。

---

## 功能特性

- **扫描卸载项**：读取 `HKLM`（64 位 + 32 位视图）与 `HKCU`（64 位视图）下的 `SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` 全部子键。
- **隐藏 / 显示**：向注册表写入 `SystemComponent`（DWORD）= 1 隐藏应用，删除该值则恢复显示。
- **32/64 位去重**：同一键名同时存在于两个视图时合并为一条记录，隐藏时**两个视图都会写入**，避免应用仍从另一视图露出。
- **写入校验**：每次写入后回读该值，确认与预期一致，失败会给出原因。
- **筛选**：按来源（全部 / 系统级 HKLM / 用户级 HKCU）、显示状态（全部 / 已隐藏 / 未隐藏）以及名称、发布者、键名搜索。
- **批量操作**：列表支持多选，一键「批量隐藏」或「批量显示」。
- **右键菜单**：对单条记录直接隐藏 / 重新显示。
- **详情抽屉**：单选时右侧展开基本信息、显示状态开关、路径与完整注册表信息。
- **状态栏**：显示总数、已隐藏数量、筛选结果数，以及扫描合并/跳过统计。

### 徽章

| 徽章 | 含义 |
| --- | --- |
| `系统` / `用户` | 条目来自 `HKLM` / `HKCU` |
| `32 位` | 条目来自 32 位注册表视图（WOW6432Node）；64 位为常见情况，不作标记 |
| `已隐藏` | `SystemComponent` 非零，控制面板中不显示 |

---

## 工作原理

「程序和功能」会列出 `SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` 下**所有带 `DisplayName` 的子键**，但跳过 `SystemComponent` 非零的项。因此：

- 隐藏 = 写入 `SystemComponent` = 1（DWORD）
- 显示 = 删除 `SystemComponent` 值（不存在即「未隐藏」，写 0 亦可）

没有 `DisplayName` 的子键会被跳过（控制面板同样不列出，因此也无法隐藏）。

### 注册表视图

32 位程序在 64 位系统上写入相同逻辑路径，WOW64 会重定向到 `WOW6432Node`。只读一个视图会静默漏掉另一种位数的程序，所以扫描覆盖：

| Hive | 视图 | 说明 |
| --- | --- | --- |
| `HKLM` | 64 位 | 优先，决定重复项的胜出来源 |
| `HKLM` | 32 位 | 与 64 位同名键合并 |
| `HKCU` | 64 位 | `HKCU\Software` 不被 WOW64 重定向，32/64 视图内容相同，只扫一次 |

重复项合并逻辑见 `Services/RegistryService.cs` 的 `ScanAsync`：以 `(来源, 键名)` 为键把 32/64 位视图折叠成一条，同时把每个视图记录进 `UninstallEntry.Views`，写入时遍历全部视图。

### 权限模型

- **`HKCU` 写入**：普通用户权限即可。
- **`HKLM` 写入**：需要管理员令牌。选中项中包含 HKLM 条目且当前未提权时，应用会**先询问一次**：
  - Unpackaged 模式：提示「以管理员身份重启」，通过 `runas` 重启提权副本后退出当前进程（`Services/AdminService.cs` 的 `TryRestartElevated`）。
  - **MSIX 打包模式**：打包应用由包激活，无法自行提权，只能提示改用 Unpackaged 启动配置（HKLM 项会失败，HKCU 项不受影响）。

### MSIX 注册表写入虚拟化

MSIX 默认把 `HKEY_CURRENT_USER` 写入重定向到包私有 hive（`%LOCALAPPDATA%\Packages\<PFN>\SystemAppData\Helium\User.dat`），读取时与真实 `HKCU` 合并——于是**写入回读校验仍然成功，但控制面板永远看不到改动**，失败完全静默。

因此 `Package.appxmanifest` 中必须保留：

```xml
<desktop6:RegistryWriteVirtualization>disabled</desktop6:RegistryWriteVirtualization>
<rescap:Capability Name="unvirtualizedResources" />
```

应用启动时还会用 `AdminService.IsRegistryWriteVirtualized()` 直接读取已安装包的 `AppxManifest.xml` 做运行时校验，若发现虚拟化仍启用，状态栏会显示 `⚠ 打包模式仍启用注册表虚拟化，修改不会写入真实注册表`。

> `unvirtualizedResources` 是受限能力：侧载和 Visual Studio 部署可用，提交 Microsoft Store 需要额外说明理由。

---

## 环境要求

- **操作系统**：Windows 10 1809（10.0.17763.0）及以上
- **MSIX 侧载**：需要 Windows 10 1899 及以上
- **SDK**：.NET 10 SDK + Windows App SDK 2.2.0
- **架构**：x86 / x64 / ARM64

---

## 构建

```bash
# 首次构建前还原 NuGet 包
dotnet restore

# 调试构建（平台取自启动配置）
dotnet build

# 指定平台发布（Release 下启用 ReadyToRun + Trimmed，自包含 .NET 运行时）
dotnet publish -c Release -p:Platform=x64
```

解决方案使用 `.slnx`（新 XML 格式），目标框架 `net10.0-windows10.0.19041.0`。

### 启动配置

| 配置 | 说明 |
| --- | --- |
| `UninstallKeyManager (Package)` | MSIX 打包运行；**无法对 HKLM 提权**，适合 HKCU 项 |
| `UninstallKeyManager (Unpackaged)` | 直接运行项目输出，无 MSIX 依赖；支持 UAC 提权改 HKLM |

---

## 项目结构

```
├── App.xaml(.cs)               # 入口，OnLaunched() 创建并激活 MainWindow
├── MainWindow.xaml(.cs)        # 主窗口，MicaBackdrop，筛选/列表/详情/状态栏
├── Models/
│   ├── UninstallEntry.cs       # 卸载项模型 + x:Bind 徽章/可见性计算属性
│   ├── RegistrySource.cs       # HKLM / HKCU
│   └── RegistryViewKind.cs     # Registry64 / Registry32
├── Services/
│   ├── IRegistryService.cs     # 扫描 + 写入契约
│   ├── RegistryService.cs      # 扫描、去重、写入并回读校验
│   ├── AdminService.cs         # 提权检测、打包检测、虚拟化检测、UAC 重启
│   └── ScanResult.cs           # ScanResult + RegistryWriteResult
├── Package.appxmanifest        # MSIX 清单（禁用注册表写入虚拟化）
├── app.manifest                # 操作系统兼容性、PerMonitorV2 DPI
├── UninstallKeyManager.csproj  # SDK 风格项目，启用 WindowsAppSDKSelfContained
└── Properties/
    ├── launchSettings.json     # Package / Unpackaged 启动配置
    └── PublishProfiles/        # win-x86 / win-x64 / win-arm64
```

### 关键配置项

- `WindowsAppSDKSelfContained=true`（`UninstallKeyManager.csproj`）：把 Windows App SDK 运行时复制进输出。否则 `Microsoft.ui.xaml.dll` 不会出现在输出中，Unpackaged 构建在启动时因 `REGDB_E_CLASSNOTREG` 崩溃。
- `PublishReadyToRun` / `PublishTrimmed`：Release 下为 `True`，Debug 下为 `False`。
- 全局 `Nullable=enable`，无测试项目（单项目解决方案）。

---

## 注意事项

- **隐藏 ≠ 卸载**。应用只是从「程序和功能」列表中消失，仍可通过 `UninstallString` 正常卸载，文件与注册表项均未被删除。
- **同一键名存在于 32 位和 64 位视图时，两个视图都会被写入**，否则应用仍会从另一个视图显示。
- 修改 `Package.appxmanifest` 中的虚拟化设置后，需重新部署包才能生效；应用会自行检测并提示。
- 本项目为个人工具，未包含自动化测试。

---

## 技术栈

- .NET 10.0 / Windows App SDK 2.2.0
- WinUI 3（`Microsoft.UI.Xaml`）+ XAML 编译绑定（`x:Bind`）
- 单项目 MSIX 打包（`EnableMsixTooling`）
- `Microsoft.Win32.Registry` 原生访问注册表

---

## 许可证

未指定。仓库地址：<https://github.com/wenyongda/UninstallKeyManager>
