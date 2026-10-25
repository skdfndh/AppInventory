using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AppInventory;

public partial class UninstallLabWindow : Window
{
    private UninstallLab _session;
    private LabPlan _plan;
    private bool _busy, _closeRequested;
    private CancellationTokenSource _operation;
    private IReadOnlyList<RelatedLocation> _preview = Array.Empty<RelatedLocation>();

    internal UninstallLabWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        Resources = owner.Resources;
        RefreshSessions();
        UpdateView();
        Closing += (_, e) =>
        {
            if (!_busy) return;
            _closeRequested = true;
            _operation?.Cancel();
            e.Cancel = true;
            LabStatus.Text = "正在停止本会话的专用测试进程…";
        };
    }

    private void RefreshSessions()
    {
        try
        {
            UninstallLab.CheckAncestors(UninstallLab.LabRoot);
            SessionBox.ItemsSource = Directory.Exists(UninstallLab.LabRoot)
                ? Directory.GetDirectories(UninstallLab.LabRoot).Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _))
                    .OrderByDescending(Directory.GetLastWriteTimeUtc).Select(Path.GetFileName).Take(100).ToList() : new List<string>();
            SessionBox.SelectedIndex = 0;
        }
        catch (Exception ex) { LabStatus.Text = "测试会话目录未能读取：" + ex.Message; }
    }

    private string Scenario => (ScenarioBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "success";

    private void UpdateView()
    {
        CreateButton.IsEnabled = !_busy;
        ScenarioBox.IsEnabled = !_busy;
        ExecuteButton.IsEnabled = !_busy && _plan != null;
        CancelButton.IsEnabled = _busy;
        PreviewButton.IsEnabled = !_busy && _session?.LastResult?.Verified == true && Directory.Exists(_session.AppDirectory);
        IsolateButton.IsEnabled = !_busy && _preview.Count > 0 && _session != null && Directory.Exists(_session.AppDirectory);
        RestoreButton.IsEnabled = !_busy && _session != null && Directory.Exists(_session.Quarantine);
        ExportLabButton.IsEnabled = !_busy && _session != null;
        LoadButton.IsEnabled = !_busy;
        SessionBox.IsEnabled = !_busy;
        if (_session != null) EventsText.Text = string.Join("\n\n", _session.Events.Select(item => $"{item.Time.ToLocalTime():HH:mm:ss} · {item.Action}\n{item.Detail}"));
    }

    private void UpdatePlan()
    {
        _plan = null;
        if (_session != null)
        {
            var summary = $"测试会话：{_session.Token}\n\n测试程序：{_session.Target}\n\n应保留数据：{_session.RetainedData}\n\n隔离区：{_session.Quarantine}";
            try
            {
                _plan = _session.Plan(Scenario);
                PlanText.Text = summary + $"\n\n卸载命令：{_plan.Command}\n\n辅助程序 SHA256：{_plan.ExecutableHash}\n\n本计划仅授权这个测试会话；执行前会再次检查标记、路径和哈希。";
            }
            catch (Exception ex) { PlanText.Text = summary + "\n\n当前不可再次执行卸载：" + ex.Message; }
        }
        UpdateView();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _session = UninstallLab.Create();
            _preview = Array.Empty<RelatedLocation>();
            ResidualText.Text = "已创建独立测试应用。卸载成功后再预览测试缓存。";
            LabStatus.Text = "已创建测试应用，请核对目标和卸载计划。";
            RefreshSessions();
            UpdatePlan();
        }
        catch (Exception ex) { LabStatus.Text = "创建未完成：" + ex.Message; }
    }

    private void Scenario_Changed(object sender, SelectionChangedEventArgs e) { if (PlanText != null && CreateButton != null) UpdatePlan(); }

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null || _plan == null || _busy) return;
        _operation = new CancellationTokenSource();
        _busy = true;
        _preview = Array.Empty<RelatedLocation>();
        LabStatus.Text = "正在运行专用测试卸载；可停止本会话测试进程…";
        UpdateView();
        try
        {
            var result = await _session.RunAsync(_plan, _operation.Token);
            LabStatus.Text = result.Status + " · 退出码：" + (result.ExitCode?.ToString() ?? "未取得") + " · " + result.Message;
        }
        catch (Exception ex) { LabStatus.Text = "测试没有完成确认：" + ex.Message; }
        finally
        {
            _busy = false;
            _operation.Dispose();
            _operation = null;
            UpdatePlan();
            if (_closeRequested) Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _preview = _session.Preview();
            ResidualText.Text = string.Join("\n\n", _preview.Select(item => item.Path + "\n" + item.SizeDisplay + " · " + item.Evidence));
            LabStatus.Text = "已预览测试残留，尚未移动或删除。";
        }
        catch (Exception ex) { _preview = Array.Empty<RelatedLocation>(); LabStatus.Text = "残留预览被阻止：" + ex.Message; }
        UpdateView();
    }

    private void Isolate_Click(object sender, RoutedEventArgs e)
    {
        try { _session.Isolate(); _preview = Array.Empty<RelatedLocation>(); ResidualText.Text = "测试缓存已隔离到：\n" + _session.Quarantine; LabStatus.Text = "测试残留已隔离，可恢复；未永久删除。"; }
        catch (Exception ex) { LabStatus.Text = "隔离未完成：" + ex.Message; }
        UpdateView();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        try { _session.Restore(); _preview = Array.Empty<RelatedLocation>(); ResidualText.Text = "测试缓存已恢复到：\n" + _session.AppDirectory; LabStatus.Text = "测试缓存已恢复，应保留数据完整。"; }
        catch (Exception ex) { LabStatus.Text = "恢复未完成：" + ex.Message; }
        UpdateView();
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        if (SessionBox.SelectedItem is not string token) return;
        try
        {
            _session = UninstallLab.Open(token);
            _preview = Array.Empty<RelatedLocation>();
            UpdatePlan();
            LabStatus.Text = _session.LastResult is { } result ? "已载入测试会话 · " + result.Status + " · " + result.Message
                : "已载入测试会话，尚无保存的执行结果。";
        }
        catch (Exception ex) { LabStatus.Text = "载入被阻止：" + ex.Message; }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Title = "导出专用测试报告", Filter = "JSON 报告 (*.json)|*.json", FileName = "卸载测试-" + _session.Token[..8] + ".json" };
        if (picker.ShowDialog(this) != true) return;
        try { _session.Export(picker.FileName); LabStatus.Text = "已导出本会话测试报告。"; }
        catch (Exception ex) { LabStatus.Text = "报告导出未完成：" + ex.Message; }
    }
}
