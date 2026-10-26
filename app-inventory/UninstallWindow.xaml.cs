using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace AppInventory;

public partial class UninstallWindow : Window
{
    private UninstallWorkflow _workflow;
    private bool _busy, _prepared, _previewed, _executing;
    private CancellationTokenSource _wait;
    private string _notice = "";
    internal bool Executed { get; private set; }

    internal UninstallWindow(Window owner, UninstallWorkflow workflow = null)
    {
        InitializeComponent();
        if (owner.IsVisible) Owner = owner;
        Resources = owner.Resources;
        _workflow = workflow; _prepared = workflow != null;
        RefreshHistory(); UpdateView();
        Closed += (_, _) => _wait?.Cancel();
    }

    private void RefreshHistory()
    {
        try { WorkflowHistory.ItemsSource = UninstallWorkflow.ListTasks(); WorkflowHistory.SelectedIndex = 0; }
        catch (Exception ex) { WorkflowStatus.Text = "历史任务无法读取：" + ex.Message; }
    }

    private void UpdateView()
    {
        if (RunWorkflowButton == null) return;
        var state = _workflow?.State;
        RunWorkflowButton.IsEnabled = !_busy && _prepared && AcceptRisk.IsChecked == true && ConfirmName.Text == state?.Plan.Installation.Name;
        StopWaitButton.IsEnabled = _busy && _wait != null;
        StopWaitButton.Content = _executing ? "停止等待（不终止卸载器）" : "取消文件检查";
        AcceptRisk.IsEnabled = ConfirmName.IsEnabled = ElevateRequest.IsEnabled = !_busy && _prepared;
        RefreshTaskButton.IsEnabled = LoadWorkflowButton.IsEnabled = !_busy;
        PreviewResidualButton.IsEnabled = !_busy && state?.Result.Status == UninstallStatus.Verified && !_workflow.IsRunning
            && state.Isolation is "未隔离" or "已恢复";
        IsolateResidualButton.IsEnabled = !_busy && _previewed && AcceptResidual.IsChecked == true && !_workflow.IsRunning;
        RestoreResidualButton.IsEnabled = !_busy && state?.Isolation is "准备隔离" or "已隔离" or "准备恢复";
        ExportWorkflowButton.IsEnabled = !_busy && state != null && !_workflow.IsRunning;
        if (state == null) { WorkflowPlanText.Text = "载入历史任务以查看结果或恢复隔离文件。"; return; }
        var plan = state.Plan;
        WorkflowPlanText.Text = $"应用：{plan.Installation.Name}\n版本：{plan.Installation.Version}\n发布者：{plan.Installation.Publisher}\n\n记录：{plan.Installation.Identity.Hive} / {plan.Installation.Identity.View} / {plan.Installation.Identity.SubKey}\n安装目录：{plan.Installation.InstallPath}\n\n执行文件：{plan.Command.Executable}\n参数：{plan.Command.Arguments}\nSHA256：{plan.ExecutableHash}\n{plan.Signature}\n\n独立核验：{(plan.Command.IsMsi ? "Windows Installer 产品状态" : plan.MainExecutable.Length > 0 ? plan.MainExecutable : "缺少独立主程序，退出后只能标记结果待确认，不能清理")}\n\n任务：{state.Id}";
        WorkflowEvents.Text = string.Join("\n\n", state.Events.Select(item => $"{item.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {item.Action}\n{item.Detail}"));
        WorkflowStatus.Text = state.Result.Label + " · " + state.Result.Message + " · 隔离：" + state.Isolation
            + (_notice.Length > 0 ? "\n" + _notice : "");
    }

    private void Confirmation_Changed(object sender, RoutedEventArgs e) => UpdateView();

    private async void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (!RunWorkflowButton.IsEnabled) return;
        var name = ConfirmName.Text; var accepted = AcceptRisk.IsChecked == true; var elevate = ElevateRequest.IsChecked == true;
        _busy = true; _executing = true; _notice = ""; _wait = new(); UpdateView();
        WorkflowStatus.Text = "正在启动已确认的卸载器…";
        try { Executed = true; var result = await _workflow.ExecuteAsync(name, accepted, elevate, _wait.Token); _notice = result.Label + " · " + result.Message; }
        catch (Exception ex) { _notice = "未完成执行：" + ex.Message; }
        finally { _prepared = false; _busy = false; _executing = false; _wait.Dispose(); _wait = null; RefreshHistory(); UpdateView(); }
    }

    private void StopWait_Click(object sender, RoutedEventArgs e) => _wait?.Cancel();
    private async void RefreshTask_Click(object sender, RoutedEventArgs e)
    {
        if (_workflow == null) return;
        _busy = true; UpdateView();
        try { _workflow = await Task.Run(() => { var task = UninstallWorkflow.Open(_workflow.State.Id); if (!task.IsRunning) task.Recheck(); return task; }); _prepared = false; _notice = ""; }
        catch (Exception ex) { _notice = "状态无法读取：" + ex.Message; }
        finally { _busy = false; UpdateView(); }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        _busy = true; _previewed = false; _wait = new(); AcceptResidual.IsChecked = false; UpdateView();
        try
        {
            var files = await Task.Run(() => _workflow.PreviewResiduals(_wait.Token));
            ResidualFilesText.Text = "目录：" + _workflow.InstallPath + "\n条目：" + files.Count + " · " + AppRow.FormatSize(files.Sum(file => file.Bytes))
                + "\n" + string.Join("\n", files.Take(100).Select(file => file.RelativePath)) + (files.Count > 100 ? "\n更多条目见导出的任务报告。" : "");
            _previewed = files.Count > 0;
        }
        catch (Exception ex) { ResidualFilesText.Text = "残留预览被阻止：" + ex.Message; }
        finally { _busy = false; _wait.Dispose(); _wait = null; UpdateView(); }
    }

    private async void Isolate_Click(object sender, RoutedEventArgs e)
    {
        if (!IsolateResidualButton.IsEnabled) return;
        var accepted = AcceptResidual.IsChecked == true;
        _busy = true; _wait = new(); UpdateView();
        try { await Task.Run(() => _workflow.Isolate(accepted, _wait.Token)); _previewed = false; AcceptResidual.IsChecked = false; ResidualFilesText.Text = "隔离目录：" + _workflow.Quarantine; }
        catch (Exception ex) { ResidualFilesText.Text = "隔离未完成：" + ex.Message; }
        finally { _busy = false; _wait.Dispose(); _wait = null; UpdateView(); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        _busy = true; _wait = new(); UpdateView();
        try { await Task.Run(() => _workflow.Restore(_wait.Token)); _previewed = false; AcceptResidual.IsChecked = false; ResidualFilesText.Text = "已恢复至：" + _workflow.InstallPath; }
        catch (Exception ex) { ResidualFilesText.Text = "恢复未完成：" + ex.Message; }
        finally { _busy = false; _wait.Dispose(); _wait = null; UpdateView(); }
    }

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        if (WorkflowHistory.SelectedItem is not string id) return;
        _busy = true; UpdateView();
        try { _workflow = await Task.Run(() => { var task = UninstallWorkflow.Open(id); if (!task.IsRunning) task.Recheck(); return task; }); _prepared = false; _previewed = false; _notice = ""; AcceptRisk.IsChecked = AcceptResidual.IsChecked = false; ConfirmName.Clear(); ResidualFilesText.Clear(); }
        catch (Exception ex) { _notice = "载入失败：" + ex.Message; }
        finally { _busy = false; UpdateView(); }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Title = "导出任务报告（包含本地路径和卸载命令，请勿公开上传）", Filter = "JSON 报告 (*.json)|*.json", FileName = "卸载任务-" + _workflow.State.Id[..8] + ".json" };
        if (picker.ShowDialog(this) != true) return;
        try { _workflow.Export(picker.FileName); WorkflowStatus.Text = "任务报告已导出，包含本地路径和命令。"; }
        catch (Exception ex) { WorkflowStatus.Text = "报告导出失败：" + ex.Message; }
    }

    private void Lab_Click(object sender, RoutedEventArgs e) { if (!_busy) new UninstallLabWindow(this).ShowDialog(); }
}
