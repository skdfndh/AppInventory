using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.Win32;

namespace AppInventory;

public partial class MainWindow : Window
{
    private List<AppRow> _apps = new();
    private ListCollectionView _view;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _measurement;
    private string _sortMember = nameof(AppRow.Name);
    private bool _descending;
    private bool _loading;
    private List<AppRow> _installedApps = new();
    private PortableScanResult _lastScan;
    private CancellationTokenSource _directoryScan;
    private List<string> _scanRoots = new(), _scanExclusions = new();
    private bool _allFixed = true;
    private readonly bool _persist;
    private bool _exporting;
    private bool _uninstallPlanning;
    private List<AppRow> _history = new();
    private DateTimeOffset? _scannedAt;
    private List<string> _scannedRoots = new();
    private List<string> _scannedExclusions = new();
    private string _storageWarning = "";

    public MainWindow() : this(true) { }

    internal MainWindow(bool loadInstalled)
    {
        _persist = loadInstalled;
        InitializeComponent();
        Title = "应用清单 " + FileVersionInfo.GetVersionInfo(typeof(MainWindow).Assembly.Location).ProductVersion?.Split('+')[0] + " · 本地软件管理";
        SetApps(new List<AppRow>());
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => { SearchBox.Focus(); SearchBox.SelectAll(); }));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find, Key.F, ModifierKeys.Control));
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { SearchBox.Clear(); e.Handled = true; }
            if (e.Key == Key.F5 && !_loading && _directoryScan == null && _measurement == null) { _ = RefreshAsync(); e.Handled = true; }
        };
        Closed += (_, _) => { _lifetime.Cancel(); _measurement?.Cancel(); _directoryScan?.Cancel(); };
        if (loadInstalled) Loaded += async (_, _) => await LoadAsync();
        else LoadingBar.Visibility = Visibility.Collapsed;
    }

    internal void SetApps(List<AppRow> apps)
    {
        var selected = AppsGrid.SelectedItem as AppRow;
        _apps = apps;
        _view = new ListCollectionView(_apps) { Filter = item => MatchesFilters((AppRow)item) };
        _view.CustomSort = new AppRowComparer(_sortMember, _descending);
        AppsGrid.ItemsSource = _view;
        AppsGrid.SelectedItem = selected != null && apps.Contains(selected) ? selected : _view.Cast<AppRow>().FirstOrDefault();
        UpdateCounts();
        UpdateButtons();
    }

    private bool MatchesFilters(AppRow row) => row.Matches(SearchBox.Text)
        && (SourceFilter.SelectedIndex <= 0 || SourceFilter.SelectedIndex == 1 && row.Source == "桌面应用"
            || SourceFilter.SelectedIndex == 2 && row.Source == "商店 / MSIX 应用" || SourceFilter.SelectedIndex == 3 && row.IsPortableCandidate)
        && (RelatedOnly.IsChecked != true || row.RelatedCount > 0)
        && (UnknownOnly.IsChecked != true || row.DescriptionSource.Contains("用途待核实"));

    private async Task LoadAsync()
    {
        _loading = true;
        UpdateButtons();
        var data = await Task.Run(() =>
        {
            var settings = new InventorySettings(true, new(), new());
            SavedCatalog snapshot = null;
            var rows = new List<AppRow>();
            var warnings = new List<string>();
            try { settings = CatalogStorage.LoadSettings(Path.Combine(CatalogStorage.DataDirectory, "settings.json")); }
            catch (Exception ex) { warnings.Add("扫描设置未能读取：" + ex.Message); }
            try
            {
                snapshot = CatalogStorage.Read<SavedCatalog>(Path.Combine(CatalogStorage.DataDirectory, "snapshot.json"));
                rows = CatalogStorage.Restore(snapshot);
            }
            catch (Exception ex) { snapshot = null; warnings.Add("历史快照未能读取：" + ex.Message); }
            return (settings, snapshot, rows, warnings);
        });
        if (_lifetime.IsCancellationRequested) return;
        _allFixed = data.settings.AllFixed;
        _scanRoots = data.settings.Roots;
        _scanExclusions = data.settings.Exclusions;
        _history = data.rows;
        _scannedAt = data.snapshot?.ScannedAt;
        _scannedRoots = data.snapshot?.Roots ?? new();
        _scannedExclusions = data.snapshot?.Exclusions ?? new();
        _storageWarning = string.Join("；", data.warnings);
        if (_history.Count > 0) SetApps(_history);
        _loading = false;
        UpdateSnapshotText();
        await RefreshAsync();
    }

    private void UpdateSnapshotText()
    {
        SnapshotText.Text = _scannedAt.HasValue ? $"目录快照：{_scannedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm} · 历史结果，重新扫描可更新"
            : "尚未扫描目录 · 安装记录不包含所有便携软件";
        SnapshotText.ToolTip = (_scannedRoots.Count > 0 ? "上次实际扫描范围：" + string.Join("、", _scannedRoots) + "\n" : "")
            + (_scannedExclusions.Count > 0 ? "上次自定义排除：" + string.Join("、", _scannedExclusions) + "\n" : "")
            + "本地保存设置与最近一次清单，不自动联网。导出文件可能包含私人路径。\n本地数据：" + CatalogStorage.DataDirectory
            + (_storageWarning.Length > 0 ? "\n" + _storageWarning : "");
        if (_storageWarning.Length > 0) SnapshotText.Text += " · 本地记录读写有提示";
    }

    private async Task SaveSnapshotAsync()
    {
        _history = _apps.ToList();
        if (!_persist) return;
        var snapshot = CatalogStorage.Capture(_history, _scannedAt, _scannedRoots, _scannedExclusions);
        try { await Task.Run(() => CatalogStorage.Save(Path.Combine(CatalogStorage.DataDirectory, "snapshot.json"), snapshot)); }
        catch (Exception ex) { _storageWarning = "清单未能保存，下次启动需重新扫描：" + ex.Message; }
        if (!_lifetime.IsCancellationRequested) UpdateSnapshotText();
    }

    private async Task RefreshAsync()
    {
        if (_loading || _directoryScan != null || _measurement != null || _exporting) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        UpdateButtons();
        LoadingBar.Visibility = Visibility.Visible;
        StatusText.Text = "正在读取桌面和商店应用的安装信息…";
        try
        {
            var result = await Task.Run(() => ReadOnlyCatalog.Load(_lifetime.Token), _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            if (result.Apps.Count == 0 && result.Warnings.Count > 0)
                throw new InvalidOperationException(string.Join("；", result.Warnings));
            UpdateInstalled(result.Apps);
            await SaveSnapshotAsync();
            StatusText.Text = result.Warnings.Count == 0
                ? $"更新于 {DateTime.Now:HH:mm:ss} · 仅显示当前用户可读取的安装记录。"
                : $"已读取可用记录；{result.Warnings.Count} 项读取提示：" + string.Join("；", result.Warnings.Take(3));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = "读取失败，已保留上次结果。可点击刷新重试：" + ex.Message; }
        finally
        {
            _loading = false;
            if (!_lifetime.IsCancellationRequested)
            {
                UpdateButtons();
                LoadingBar.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    internal void UpdateInstalled(List<AppRow> installed)
    {
        var merged = CatalogStorage.MergeHistory(installed, _apps);
        _installedApps = merged.Where(row => !row.IsPortableCandidate).ToList();
        SetApps(_lastScan == null ? merged : PortableScanner.Merge(_installedApps, _lastScan));
    }

    internal void UpdateScan(PortableScanResult result, ScanScope scope)
    {
        _lastScan = result;
        _scannedAt = DateTimeOffset.Now;
        _scannedRoots = result.Roots;
        _scannedExclusions = scope.Exclusions.ToList();
        SetApps(PortableScanner.Merge(_installedApps, result));
    }

    private void UninstallHistory_Click(object sender, RoutedEventArgs e) => new UninstallWindow(this).ShowDialog();

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_uninstallPlanning || AppsGrid.SelectedItem is not AppRow row) return;
        _uninstallPlanning = true; UpdateButtons();
        StatusText.Text = "正在重新核对安装身份、卸载命令与文件摘要，尚未执行卸载…";
        try
        {
            var workflow = await Task.Run(() => UninstallWorkflow.Prepare(row));
            if (_lifetime.IsCancellationRequested) return;
            var dialog = new UninstallWindow(this, workflow);
            dialog.ShowDialog();
            if (dialog.Executed) await RefreshAsync();
            StatusText.Text = "卸载任务已保存，可在卸载记录中查看结果或恢复隔离文件。";
        }
        catch (Exception ex) { StatusText.Text = "卸载预检被阻止：" + ex.Message; }
        finally { _uninstallPlanning = false; if (!_lifetime.IsCancellationRequested) UpdateButtons(); }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_view == null) return;
        _view.Refresh();
        if (AppsGrid.SelectedItem == null) AppsGrid.SelectedItem = _view.Cast<AppRow>().FirstOrDefault();
        UpdateCounts();
        UpdateButtons();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => Search_Changed(sender, null);

    private void UpdateCounts()
    {
        var visible = _view.Count;
        CountText.Text = $"{visible:N0} / {_apps.Count:N0} 个应用";
        EmptyState.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SelectionPanel == null) return;
        var row = AppsGrid.SelectedItem as AppRow;
        SelectionPanel.DataContext = row;
        SelectionPanel.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
        NoSelection.Visibility = row == null ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        Sort(e.Column.SortMemberPath, e.Column.SortDirection == ListSortDirection.Ascending);
    }

    internal void Sort(string member, bool descending)
    {
        _sortMember = member;
        _descending = descending;
        _view.CustomSort = new AppRowComparer(member, descending);
        foreach (var column in AppsGrid.Columns)
            column.SortDirection = column.SortMemberPath == member
                ? (descending ? ListSortDirection.Descending : ListSortDirection.Ascending) : null;
    }

    private void UpdateButtons()
    {
        if (MeasureButton == null) return;
        if (UninstallButton != null) UninstallButton.IsEnabled = !_loading && !_exporting && !_uninstallPlanning && _directoryScan == null
            && _measurement == null && AppsGrid.SelectedItem is AppRow selected && !selected.IsPortableCandidate
            && selected.Source == "桌面应用" && selected.UninstallRegistration != null;
        RefreshButton.IsEnabled = !_loading && _directoryScan == null && _measurement == null && !_exporting;
        ScanButton.IsEnabled = !_loading && _measurement == null && !_exporting;
        ExportButton.IsEnabled = !_loading && _directoryScan == null && _measurement == null && !_exporting && _view?.Count > 0;
        MeasureButton.IsEnabled = !_loading && _directoryScan == null && !_exporting && (_measurement != null
            || (AppsGrid.SelectedItem is AppRow row && !row.IsSingleFileCandidate && !string.IsNullOrWhiteSpace(row.InstallPath)));
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_directoryScan != null) { _directoryScan.Cancel(); return; }
        var picker = new ScanScopeWindow(this, _scanRoots, _scanExclusions, _allFixed);
        if (picker.ShowDialog() != true) return;
        _scanRoots = picker.ExtraRoots.ToList();
        _scanExclusions = picker.Exclusions.ToList();
        _allFixed = picker.AllFixed;
        var scope = picker.Scope;
        _directoryScan = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ScanButton.Content = "停止扫描";
        LoadingBar.Visibility = Visibility.Visible;
        UpdateButtons();
        try
        {
            if (_persist)
            {
                try
                {
                    var settings = new InventorySettings(_allFixed, _scanRoots, _scanExclusions);
                    await Task.Run(() => CatalogStorage.Save(Path.Combine(CatalogStorage.DataDirectory, "settings.json"), settings));
                }
                catch (Exception ex) { _storageWarning = "扫描设置未能保存：" + ex.Message; }
            }
            var token = _directoryScan.Token;
            var progress = new Progress<ScanProgress>(state =>
            {
                if (_directoryScan != null && !_lifetime.IsCancellationRequested)
                    StatusText.Text = $"已读 {state.Directories:N0} 个目录、{state.Files:N0} 个文件 · {state.CurrentPath}";
            });
            var result = await Task.Run(() => PortableScanner.Scan(scope, _installedApps, progress, token), token);
            if (_lifetime.IsCancellationRequested) return;
            UpdateScan(result, scope);
            await SaveSnapshotAsync();
            StatusText.Text = $"扫描完成 · {result.Candidates.Count:N0} 个程序候选，跳过 {result.Skipped:N0} 处排除项、链接或不可访问位置。";
        }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) StatusText.Text = "已停止扫描，保留上一次完整清单；本轮未完成结果未合并。"; }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) StatusText.Text = "扫描失败，原清单未变：" + ex.Message; }
        finally
        {
            _directoryScan.Dispose();
            _directoryScan = null;
            if (!_lifetime.IsCancellationRequested)
            {
                ScanButton.Content = "扫描范围…";
                LoadingBar.Visibility = Visibility.Collapsed;
                UpdateButtons();
            }
        }
    }

    private void Source_Navigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (e.Uri.IsAbsoluteUri && e.Uri.Scheme == Uri.UriSchemeHttps)
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { StatusText.Text = "无法打开说明来源：" + ex.Message; }
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Title = "导出当前筛选结果（包含本地路径）", Filter = "CSV 表格 (*.csv)|*.csv|JSON 详细报告 (*.json)|*.json",
            FileName = "应用清单-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), AddExtension = true, DefaultExt = ".csv", OverwritePrompt = true };
        if (picker.ShowDialog(this) != true) return;
        var path = picker.FileName;
        var asJson = picker.FilterIndex == 2;
        _exporting = true;
        UpdateButtons();
        try
        {
            var count = await WriteExportAsync(path, asJson);
            if (!_lifetime.IsCancellationRequested) StatusText.Text = $"已导出 {count:N0} 条当前筛选结果。关联候选和大小均不代表可删除空间。";
        }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) StatusText.Text = "导出未完成：" + ex.Message; }
        finally { _exporting = false; if (!_lifetime.IsCancellationRequested) UpdateButtons(); }
    }

    internal Task<int> WriteExportAsync(string path, bool asJson)
    {
        var rows = _view.Cast<AppRow>().ToList();
        var scannedAt = _scannedAt;
        var snapshot = CatalogStorage.Capture(rows, scannedAt, _scannedRoots, _scannedExclusions);
        return Task.Run(() =>
        {
            if (asJson) CatalogStorage.Save(path, snapshot);
            else CatalogStorage.WriteText(path, CatalogStorage.Csv(rows, scannedAt));
            return rows.Count;
        });
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (AppsGrid.SelectedItem is not AppRow row || string.IsNullOrWhiteSpace(row.InstallPath)) return;
        try { Clipboard.SetText(row.InstallPath); StatusText.Text = "已复制选中应用的路径。"; }
        catch (Exception ex) { StatusText.Text = "无法复制路径：" + ex.Message; }
    }

    private async void Measure_Click(object sender, RoutedEventArgs e)
    {
        if (_measurement != null) { _measurement.Cancel(); return; }
        if (AppsGrid.SelectedItem is not AppRow row) return;
        _measurement = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        UpdateButtons();
        MeasureButton.Content = "停止测量";
        StatusText.Text = "正在只读测量：" + row.Name + "。可随时停止。";
        try
        {
            var token = _measurement.Token;
            var result = await Task.Run(() => DirectorySize.Measure(row.InstallPath, token), token);
            if (_lifetime.IsCancellationRequested) return;
            row.SetMeasurement(result);
            _view.Refresh();
            await SaveSnapshotAsync();
            StatusText.Text = "目录测量完成：" + row.Name + " · " + row.SizeSource;
        }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) StatusText.Text = "已停止测量，原来的大小信息保持不变。"; }
        catch (Exception ex) { StatusText.Text = "无法完成目录测量：" + ex.Message; }
        finally
        {
            _measurement.Dispose();
            _measurement = null;
            if (!_lifetime.IsCancellationRequested)
            {
                MeasureButton.Content = "测量目录大小";
                UpdateButtons();
            }
        }
    }
}
