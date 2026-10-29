using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using UninstallKeyManager.Models;
using UninstallKeyManager.Services;

namespace UninstallKeyManager
{
    /// <summary>
    /// Answer to the "need elevation?" question.
    /// </summary>
    internal enum ElevationPromptResult
    {
        /// <summary>Carry on; either nothing needs elevation or the user accepted the risk.</summary>
        Proceed,

        /// <summary>The user cancelled, so the operation must not run.</summary>
        Abort,

        /// <summary>An elevated copy was started and this process is shutting down.</summary>
        Restarted
    }

    /// <summary>
    /// Main window of the Control Panel hiding manager.
    /// </summary>
    public sealed partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly IRegistryService _registryService;

        private List<UninstallEntry> _allEntries = new();
        private ScanResult _scanResult = new();

        /// <summary>
        /// Guards <see cref="HideSwitch_Toggled"/> against the Toggled events that
        /// the programmatic switch updates raise while a write is in flight.
        /// </summary>
        private bool _isApplying;

        /// <summary>
        /// Becomes true once every named control exists. Without it the filter
        /// handlers run while the XAML parser is still creating the sibling
        /// controls, and dereference fields that are still null.
        /// </summary>
        private bool _isReady;

        /// <summary>
        /// The dialog currently on screen, if any. WinUI refuses to open a second
        /// one, and that exception surfaces on a dispatcher continuation where no
        /// caller can catch it.
        /// </summary>
        private ContentDialog? _openDialog;

        // Context menu elements
        private MenuFlyout _contextMenu = null!;
        private MenuFlyoutItem _setSystemComponentItem = null!;
        private MenuFlyoutItem _unsetSystemComponentItem = null!;

        // ── INotifyPropertyChanged ─────────────────────────────────

        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Raises <see cref="PropertyChanged"/> for the given property.
        /// </summary>
        /// <param name="propertyName">Property name; defaults to the caller's.</param>
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // ── Properties referenced by x:Bind ───────────────────────

        private bool _isDetailOpen;

        /// <summary>
        /// Whether the detail pane is open.
        /// </summary>
        public bool IsDetailOpen
        {
            get => _isDetailOpen;
            set
            {
                if (_isDetailOpen != value)
                {
                    _isDetailOpen = value;
                    OnPropertyChanged();
                }
            }
        }

        private UninstallEntry? _selectedItem;

        /// <summary>
        /// The currently selected uninstall entry.
        /// </summary>
        public UninstallEntry? SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (_selectedItem != value)
                {
                    _selectedItem = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _summaryLine = string.Empty;

        /// <summary>
        /// The application / hidden counts shown in the action row.
        /// </summary>
        public string SummaryLine
        {
            get => _summaryLine;
            private set
            {
                if (_summaryLine != value)
                {
                    _summaryLine = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _statusLine = string.Empty;

        /// <summary>
        /// The filter summary shown in the status bar.
        /// </summary>
        public string StatusLine
        {
            get => _statusLine;
            private set
            {
                if (_statusLine != value)
                {
                    _statusLine = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _scanNote = string.Empty;

        /// <summary>
        /// What the scan merged or skipped, shown in the status bar.
        /// </summary>
        public string ScanNote
        {
            get => _scanNote;
            private set
            {
                if (_scanNote != value)
                {
                    _scanNote = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// The entries displayed in the list, after filtering.
        /// </summary>
        public ObservableCollection<UninstallEntry> Entries { get; } = new();

        // ── Constructor ────────────────────────────────────────────

        /// <summary>
        /// Initializes a new instance of the <see cref="MainWindow"/> class.
        /// </summary>
        public MainWindow()
        {
            _registryService = new RegistryService();
            InitializeComponent();
            SetupContextMenu();

            // Select the defaults here rather than with SelectedIndex in XAML:
            // in markup it fires SelectionChanged while the parser is still
            // creating the sibling controls, and the handler then reads null.
            SourceFilterComboBox.SelectedIndex = 0;
            StatusFilterComboBox.SelectedIndex = 0;

            _isReady = true;

            ((FrameworkElement)Content).Loaded += MainWindow_Loaded;
        }

        // ── Data loading ───────────────────────────────────────────

        /// <summary>
        /// Starts the first scan once the window content is ready.
        /// </summary>
        /// <param name="sender">The loaded element.</param>
        /// <param name="e">Event data.</param>
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await RunGuardedAsync(LoadEntriesAsync);
        }

        /// <summary>
        /// Scans the registry and rebuilds the list.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task LoadEntriesAsync()
        {
            SetBusy(true);

            try
            {
                var result = await _registryService.ScanAsync();
                DispatcherQueue.TryEnqueue(() =>
                {
                    _scanResult = result;
                    _allEntries = result.Entries;
                    ApplyFilters();
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    await ShowErrorDialogAsync($"加载注册表数据失败：{ex.Message}");
                });
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ── Filtering ──────────────────────────────────────────────

        /// <summary>
        /// Applies the source, hidden-state and search filters to the entry list.
        /// </summary>
        private void ApplyFilters()
        {
            if (!_isReady)
            {
                return;
            }

            var sourceIndex = SourceFilterComboBox.SelectedIndex;
            var statusIndex = StatusFilterComboBox.SelectedIndex;
            var searchText = SearchBox.Text?.Trim() ?? string.Empty;

            var filtered = _allEntries.Where(entry =>
            {
                // Source filter: 0 = all, 1 = HKLM, 2 = HKCU.
                if (sourceIndex == 1 && entry.Source != RegistrySource.HKLM) return false;
                if (sourceIndex == 2 && entry.Source != RegistrySource.HKCU) return false;

                // Hidden-state filter: 0 = all, 1 = hidden, 2 = visible.
                if (statusIndex == 1 && !entry.IsSystemComponent) return false;
                if (statusIndex == 2 && entry.IsSystemComponent) return false;

                // Search filter: case-insensitive match on name, publisher or key name.
                if (searchText.Length > 0)
                {
                    var matchesName = entry.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase);
                    var matchesPublisher = entry.Publisher.Contains(searchText, StringComparison.OrdinalIgnoreCase);
                    var matchesKey = entry.KeyName.Contains(searchText, StringComparison.OrdinalIgnoreCase);
                    if (!matchesName && !matchesPublisher && !matchesKey) return false;
                }

                return true;
            }).ToList();

            Entries.Clear();
            foreach (var entry in filtered)
            {
                Entries.Add(entry);
            }

            UpdateStatusBar(filtered.Count);
        }

        /// <summary>
        /// Refreshes the summary line and the status bar after a scan or a write.
        /// </summary>
        /// <param name="filteredCount">The number of entries currently listed, if known.</param>
        private void UpdateStatusBar(int? filteredCount = null)
        {
            var total = _allEntries.Count;
            var hidden = _allEntries.Count(entry => entry.IsSystemComponent);

            SummaryLine = $"{total} 个应用 · {hidden} 个已从控制面板隐藏";

            var isFiltered = SourceFilterComboBox.SelectedIndex != 0
                          || StatusFilterComboBox.SelectedIndex != 0
                          || SearchBox.Text?.Trim().Length > 0;

            StatusLine = isFiltered && filteredCount is int count
                ? $"筛选出 {count} / {total} 项"
                : $"共 {total} 项";

            EmptyStateText.Visibility = (filteredCount ?? 0) == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            var notes = new List<string>();
            if (_scanResult.MergedDuplicates > 0)
            {
                notes.Add($"合并 {_scanResult.MergedDuplicates} 个 32/64 位重复项");
            }

            if (_scanResult.SkippedWithoutDisplayName > 0)
            {
                notes.Add($"跳过 {_scanResult.SkippedWithoutDisplayName} 个无名称项");
            }

            ScanNote = notes.Count > 0 ? string.Join(" · ", notes) : string.Empty;
        }

        // ── Event handlers ─────────────────────────────────────────

        /// <summary>
        /// Handles the source filter change.
        /// </summary>
        /// <param name="sender">The filter ComboBox.</param>
        /// <param name="e">Event data.</param>
        private void SourceFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        /// <summary>
        /// Handles the hidden-state filter change.
        /// </summary>
        /// <param name="sender">The filter ComboBox.</param>
        /// <param name="e">Event data.</param>
        private void StatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        /// <summary>
        /// Handles search text changes.
        /// </summary>
        /// <param name="sender">The search box.</param>
        /// <param name="e">Event data.</param>
        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
        {
            ApplyFilters();
        }

        /// <summary>
        /// Opens the detail pane when exactly one row is selected.
        /// </summary>
        /// <param name="sender">The list view.</param>
        /// <param name="e">Event data.</param>
        private void EntriesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListView listView)
            {
                return;
            }

            if (listView.SelectedItems.Count == 1 && listView.SelectedItems[0] is UninstallEntry entry)
            {
                SelectedItem = entry;
                IsDetailOpen = true;
            }
        }

        /// <summary>
        /// Closes the detail pane.
        /// </summary>
        /// <param name="sender">The close button.</param>
        /// <param name="e">Event data.</param>
        private void CloseDetail_Click(object sender, RoutedEventArgs e)
        {
            IsDetailOpen = false;
            SelectedItem = null;
            EntriesListView.SelectedItem = null;
        }

        /// <summary>
        /// Rescans the registry.
        /// </summary>
        /// <param name="sender">The refresh button.</param>
        /// <param name="e">Event data.</param>
        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await RunGuardedAsync(LoadEntriesAsync);
        }

        // ── Context menu ───────────────────────────────────────────

        /// <summary>
        /// Builds the right-click menu used to toggle a single entry.
        /// </summary>
        private void SetupContextMenu()
        {
            _contextMenu = new MenuFlyout();

            _setSystemComponentItem = new MenuFlyoutItem
            {
                Text = "从控制面板隐藏"
            };
            _setSystemComponentItem.Click += SetSystemComponent_Click;

            _unsetSystemComponentItem = new MenuFlyoutItem
            {
                Text = "重新显示到控制面板"
            };
            _unsetSystemComponentItem.Click += UnsetSystemComponent_Click;

            _contextMenu.Items.Add(_setSystemComponentItem);
            _contextMenu.Items.Add(_unsetSystemComponentItem);

            EntriesListView.ContextFlyout = _contextMenu;
            EntriesListView.RightTapped += EntriesListView_RightTapped;
        }

        /// <summary>
        /// Shows the context menu for the right-clicked row.
        /// </summary>
        /// <param name="sender">The list view.</param>
        /// <param name="e">Event data.</param>
        private void EntriesListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not ListView listView)
            {
                return;
            }

            if (e.OriginalSource is not FrameworkElement element)
            {
                return;
            }

            if (element.DataContext is not UninstallEntry entry)
            {
                return;
            }

            // A right-click on a row that is already part of the selection keeps the
            // whole selection, so the batch actions still apply to every row.
            if (!listView.SelectedItems.Contains(entry))
            {
                listView.SelectedItem = entry;
                SelectedItem = entry;
            }

            _setSystemComponentItem.Visibility = entry.IsSystemComponent
                ? Visibility.Collapsed
                : Visibility.Visible;
            _unsetSystemComponentItem.Visibility = entry.IsSystemComponent
                ? Visibility.Visible
                : Visibility.Collapsed;

            _contextMenu.ShowAt(element, e.GetPosition(element));
        }

        /// <summary>
        /// Handles the "hide" menu item.
        /// </summary>
        /// <param name="sender">The menu item.</param>
        /// <param name="e">Event data.</param>
        private async void SetSystemComponent_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem is not null)
            {
                await RunGuardedAsync(() => ApplyToEntriesAsync(new[] { SelectedItem }, hide: true));
            }
        }

        /// <summary>
        /// Handles the "show" menu item.
        /// </summary>
        /// <param name="sender">The menu item.</param>
        /// <param name="e">Event data.</param>
        private async void UnsetSystemComponent_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedItem is not null)
            {
                await RunGuardedAsync(() => ApplyToEntriesAsync(new[] { SelectedItem }, hide: false));
            }
        }

        // ── Detail pane toggle ─────────────────────────────────────

        /// <summary>
        /// Applies the switch position to the selected entry.
        /// </summary>
        /// <param name="sender">The switch.</param>
        /// <param name="e">Event data.</param>
        private async void HideSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isApplying || SelectedItem is not UninstallEntry entry)
            {
                return;
            }

            var target = HideSwitch.IsOn;

            // The same binding also moves this switch when the model changes, for
            // example right after a batch write or after a failed write. When the
            // switch and the model already agree there is nothing left to do.
            if (target == entry.IsSystemComponent)
            {
                return;
            }

            // Put the switch back while the write runs, so the UI never claims a
            // state the registry does not have yet. This raises Toggled again, and
            // the comparison above turns that echo into a no-op.
            HideSwitch.IsOn = entry.IsSystemComponent;

            try
            {
                await RunGuardedAsync(() => ApplyToEntriesAsync(new[] { entry }, target));
            }
            finally
            {
                HideSwitch.IsOn = entry.IsSystemComponent;
            }
        }

        // ── Batch operations ───────────────────────────────────────

        /// <summary>
        /// Hides every selected entry.
        /// </summary>
        /// <param name="sender">The button.</param>
        /// <param name="e">Event data.</param>
        private async void BatchHide_Click(object sender, RoutedEventArgs e)
        {
            await RunGuardedAsync(() => ApplyToSelectionAsync(hide: true));
        }

        /// <summary>
        /// Shows every selected entry.
        /// </summary>
        /// <param name="sender">The button.</param>
        /// <param name="e">Event data.</param>
        private async void BatchShow_Click(object sender, RoutedEventArgs e)
        {
            await RunGuardedAsync(() => ApplyToSelectionAsync(hide: false));
        }

        /// <summary>
        /// Applies one hidden-state change to the whole current selection.
        /// </summary>
        /// <param name="hide">True to hide, false to show.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task ApplyToSelectionAsync(bool hide)
        {
            var selected = EntriesListView.SelectedItems
                .OfType<UninstallEntry>()
                .ToList();

            if (selected.Count == 0)
            {
                await ShowMessageDialogAsync("请先在列表中选中一个或多个应用。", hide ? "批量隐藏" : "批量显示");
                return;
            }

            await ApplyToEntriesAsync(selected, hide);
        }

        /// <summary>
        /// Writes one hidden-state change for each entry and reports the outcome.
        /// </summary>
        /// <param name="targets">The entries to change.</param>
        /// <param name="hide">True to hide, false to show.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task ApplyToEntriesAsync(IReadOnlyList<UninstallEntry> targets, bool hide)
        {
            // Refuse a second write while one is still running: interleaving two
            // batches would make the read-back verification meaningless.
            if (_isApplying)
            {
                return;
            }

            _isApplying = true;
            SetBusy(true);

            try
            {
                // Ask once, up front, instead of failing halfway through the batch.
                var decision = await PromptForElevationIfNeededAsync(targets);
                if (decision != ElevationPromptResult.Proceed)
                {
                    return;
                }

                var succeeded = 0;
                var failed = 0;
                string firstFailure = string.Empty;

                foreach (var entry in targets)
                {
                    var result = await _registryService.SetSystemComponentAsync(entry, hide);

                    if (result.Success)
                    {
                        succeeded++;
                        entry.IsSystemComponent = hide;
                        continue;
                    }

                    failed++;

                    if (firstFailure.Length == 0)
                    {
                        firstFailure = result.Message ?? "未知错误";
                    }
                }

                ApplyFilters();

                var title = hide ? "批量隐藏" : "批量显示";
                var message = hide
                    ? $"已隐藏 {succeeded} 个应用。"
                    : $"已恢复显示 {succeeded} 个应用。";

                if (failed > 0)
                {
                    message += $"\n\n失败 {failed} 个。";
                    if (firstFailure.Length > 0)
                    {
                        message += $"\n原因：{firstFailure}";
                    }
                }

                await ShowMessageDialogAsync(message, title);
            }
            finally
            {
                SetBusy(false);
                _isApplying = false;
            }
        }

        /// <summary>
        /// Asks for elevation once when a change touches HKEY_LOCAL_MACHINE.
        /// </summary>
        /// <param name="targets">The entries that are about to be changed.</param>
        /// <returns>The user's decision.</returns>
        private async Task<ElevationPromptResult> PromptForElevationIfNeededAsync(
            IReadOnlyList<UninstallEntry> targets)
        {
            var hklmCount = targets.Count(entry => entry.Source == RegistrySource.HKLM);
            if (hklmCount == 0 || AdminService.IsUserAnAdmin())
            {
                return ElevationPromptResult.Proceed;
            }

            var packaged = AdminService.IsPackaged();
            var content = packaged
                ? $"选中的 {targets.Count} 个应用中有 {hklmCount} 个来自系统级 (HKLM)，修改它们需要管理员权限。\n\n"
                  + "MSIX 打包应用无法自行提权，请改用「Unpackaged」启动配置运行本程序。\n\n"
                  + "仍要继续吗？（HKLM 项将失败，HKCU 项不受影响）"
                : $"选中的 {targets.Count} 个应用中有 {hklmCount} 个来自系统级 (HKLM)，修改它们需要管理员权限。\n\n"
                  + "是否以管理员身份重新启动本程序？";

            var dialog = new ContentDialog
            {
                Title = "需要管理员权限",
                Content = content,
                PrimaryButtonText = packaged ? "继续" : "以管理员身份重启",
                CloseButtonText = "取消",
                XamlRoot = (Content as UIElement)?.XamlRoot
            };

            var choice = await TryShowDialogAsync(dialog);
            if (choice != ContentDialogResult.Primary)
            {
                return ElevationPromptResult.Abort;
            }

            if (packaged || !AdminService.TryRestartElevated())
            {
                return ElevationPromptResult.Proceed;
            }

            Application.Current.Exit();
            return ElevationPromptResult.Restarted;
        }

        // ── Helpers ────────────────────────────────────────────────

        /// <summary>
        /// Runs a UI action and turns an unexpected failure into a status message.
        /// </summary>
        /// <remarks>
        /// The callers are async void event handlers. An exception escaping one of
        /// those is rethrown on a dispatcher continuation, where no caller can catch
        /// it, and WinUI answers by terminating the process.
        /// </remarks>
        /// <param name="action">The action to run.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task RunGuardedAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                StatusLine = $"操作失败：{ex.Message}";
            }
        }

        /// <summary>
        /// Enables or disables the actions that would start a registry write.
        /// </summary>
        /// <param name="busy">True while a scan or a write is running.</param>
        private void SetBusy(bool busy)
        {
            RefreshButton.IsEnabled = !busy;
            BatchHideButton.IsEnabled = !busy;
            BatchShowButton.IsEnabled = !busy;
            HideSwitch.IsEnabled = !busy;
        }

        /// <summary>
        /// Shows an error dialog.
        /// </summary>
        /// <param name="message">The message to display.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task ShowErrorDialogAsync(string message)
        {
            await ShowMessageDialogAsync(message, "错误");
        }

        /// <summary>
        /// Shows a message dialog.
        /// </summary>
        /// <param name="message">The message to display.</param>
        /// <param name="title">The dialog title.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task ShowMessageDialogAsync(string message, string title)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "确定",
                XamlRoot = (Content as UIElement)?.XamlRoot
            };

            await TryShowDialogAsync(dialog);
        }

        /// <summary>
        /// Shows a dialog unless one is already open.
        /// </summary>
        /// <param name="dialog">The dialog to show.</param>
        /// <returns>The user's choice, or null when another dialog was open.</returns>
        private async Task<ContentDialogResult?> TryShowDialogAsync(ContentDialog dialog)
        {
            if (_openDialog is not null)
            {
                return null;
            }

            _openDialog = dialog;

            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                _openDialog = null;
            }
        }
    }
}
