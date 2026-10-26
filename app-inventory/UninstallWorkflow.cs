using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using UninstallTools;
using UninstallTools.Factory;
using UninstallTools.Junk.Containers;
using UninstallTools.Junk.Finders.Drive;

namespace AppInventory;

internal enum UninstallStatus { Prepared, Running, Cancelled, Failed, RebootRequired, Unconfirmed, Verified }
internal sealed record WorkflowPlan(InstalledSnapshot Installation, UninstallCommand Command, string ExecutableHash,
    string Signature, string MainExecutable, string MainHash);
internal sealed record WorkflowResult(UninstallStatus Status, int? ExitCode, string Message)
{
    public string Label => Status switch
    {
        UninstallStatus.Prepared => "等待确认", UninstallStatus.Running => "卸载器仍在运行", UninstallStatus.Cancelled => "已取消",
        UninstallStatus.Failed => "卸载失败", UninstallStatus.RebootRequired => "等待重启", UninstallStatus.Verified => "已核验卸载", _ => "结果待确认"
    };
}
internal sealed record ResidualFile(string RelativePath, long Bytes, string Hash);
internal sealed record WorkflowState(int Schema, string Id, WorkflowPlan Plan, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt,
    int? ProcessId, long? ProcessStartTicks, WorkflowResult Result, List<LabEvent> Events, List<ResidualFile> Residuals, string Isolation);

internal sealed class UninstallWorkflow
{
    internal static string TaskRoot => Path.Combine(CatalogStorage.DataDirectory, "uninstall-tasks");
    private static string StorageRoot(bool fixture) => fixture ? Path.Combine(AppContext.BaseDirectory, "read-only-fixtures", "uninstall-tasks") : TaskRoot;
    private readonly bool _fixture;
    private readonly object _gate = new();
    private WorkflowState _state;
    private bool _livePlan;
    private Task<WorkflowResult> _completion;
    internal WorkflowState State { get { lock (_gate) return _state; } }
    internal string Root => Path.Combine(StorageRoot(_fixture), State.Id);
    internal string InstallPath => State.Plan.Installation.InstallPath.Trim().Trim('"');
    internal string Quarantine => Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(InstallPath))!, ".AppInventory-Quarantine-" + State.Id);
    internal bool IsRunning => _completion is { IsCompleted: false } || ProcessMatches(State);

    private UninstallWorkflow(WorkflowState state, bool fixture, bool livePlan) { _state = state; _fixture = fixture; _livePlan = livePlan; }

    internal static UninstallWorkflow Prepare(AppRow row, bool fixture = false)
    {
        if (row.IsPortableCandidate || row.Source != "桌面应用" || row.UninstallRegistration == null)
            throw new InvalidOperationException("本次仅支持有当前系统卸载记录的桌面应用。商店及便携候选不执行。");
        var identity = InstallIdentity.Parse(row.UninstallRegistration.RegistryPath);
        var entry = identity.Read() ?? throw new InvalidOperationException("安装记录已消失，请刷新清单。");
        if (entry.NoRemove || entry.SystemComponent || entry.ParentKeyName.Length > 0 || entry.Name.Length == 0
            || entry.ReleaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) || entry.ReleaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("受保护记录、系统组件、更新或子组件不能直接卸载。");
        if (entry.Command != row.UninstallRegistration.Command)
            throw new InvalidOperationException("标准卸载入口已改变，请刷新清单。");
        var command = UninstallCommand.Parse(entry.Command);
        if (command.IsMsi && (!entry.WindowsInstaller || !Guid.TryParse(Path.GetFileName(entry.Identity.SubKey), out var product)
            || product != Guid.Parse(command.ProductCode) || InstallerNative.MsiQueryProductState(command.ProductCode) != 5))
            throw new InvalidOperationException("MSI 产品代码、安装身份或当前安装状态未核对一致。");
        if (!File.Exists(command.Executable)) throw new FileNotFoundException("卸载器不存在。", command.Executable);
        var main = row.ExecutablePath;
        if (!Path.IsPathFullyQualified(main) || !File.Exists(main) || main.Equals(command.Executable, StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathFullyQualified(entry.InstallPath) || !PortableScanner.IsWithin(main, entry.InstallPath)) main = "";
        var plan = new WorkflowPlan(entry, command, UninstallFiles.Hash(command.Executable), InstallerNative.Signature(command.Executable),
            main, main.Length == 0 ? "" : UninstallFiles.Hash(main));
        var state = new WorkflowState(1, Guid.NewGuid().ToString("N"), plan, DateTimeOffset.UtcNow, null, null, null,
            new(UninstallStatus.Prepared, null, "请核对目标、命令、摘要和签名；确认后调用厂商卸载器。"), new(), new(), "未隔离");
        var workflow = new UninstallWorkflow(state, fixture, true);
        workflow.Save();
        return workflow;
    }

    internal static UninstallWorkflow Open(string id, bool fixture = false)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("任务标识无效。");
        var path = Path.Combine(StorageRoot(fixture), id, "task.bin");
        UninstallLab.CheckAncestors(path);
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException("任务记录过大。");
        var json = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        var state = JsonSerializer.Deserialize<WorkflowState>(json, CatalogStorage.JsonOptions);
        if (state == null || state.Schema != 1 || state.Id != id || state.Plan?.Installation == null || state.Result == null
            || state.Events == null || state.Residuals == null || state.Isolation == null) throw new InvalidDataException("任务记录不完整。");
        return new(state, fixture, false);
    }

    internal static IReadOnlyList<string> ListTasks(bool fixture = false)
    {
        var root = StorageRoot(fixture);
        UninstallLab.CheckAncestors(root);
        return !Directory.Exists(root) ? Array.Empty<string>() : Directory.GetDirectories(root)
            .Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)).OrderByDescending(Directory.GetLastWriteTimeUtc)
            .Select(Path.GetFileName).ToList();
    }

    internal async Task<WorkflowResult> ExecuteAsync(string typedName, bool accepted, bool elevate, CancellationToken stopWaiting)
    {
        Task<WorkflowResult> completion;
        lock (_gate)
        {
            if (!_livePlan || _completion != null || State.Result.Status != UninstallStatus.Prepared)
                throw new InvalidOperationException("此计划已使用或来自历史记录，必须重新预检。");
            if (!accepted || typedName != State.Plan.Installation.Name) throw new InvalidOperationException("必须确认风险并准确输入本次应用名称。");
            stopWaiting.ThrowIfCancellationRequested();
            var lease = AcquireGlobal();
            try
            {
                var plan = State.Plan;
                if (plan.Installation.Identity.Read() != plan.Installation || UninstallCommand.Parse(plan.Installation.Command) != plan.Command
                    || plan.Command.IsMsi && InstallerNative.MsiQueryProductState(plan.Command.ProductCode) != 5
                    || UninstallFiles.Hash(plan.Command.Executable) != plan.ExecutableHash
                    || plan.MainExecutable.Length > 0 && UninstallFiles.Hash(plan.MainExecutable) != plan.MainHash)
                    throw new InvalidOperationException("安装记录、卸载器或主程序已改变，必须重新预检和确认。");
                _livePlan = false;
                _completion = Task.Run(() => RunAsync(elevate, lease));
                completion = _completion;
            }
            catch { lease.Dispose(); throw; }
        }
        try { return await completion.WaitAsync(stopWaiting); }
        catch (OperationCanceledException)
        {
            return new(UninstallStatus.Running, null, "已停止等待；没有终止厂商卸载器。任务仍占用执行锁，不能清理残留。可在任务记录中查看结果。");
        }
    }

    private async Task<WorkflowResult> RunAsync(bool elevate, FileStream lease)
    {
        // 先让出调用锁，后续进程等待与磁盘核验不占用界面线程。
        await Task.Yield();
        using (lease)
        {
            try
            {
                var plan = State.Plan;
                var info = new ProcessStartInfo(plan.Command.Executable, plan.Command.Arguments)
                {
                    UseShellExecute = elevate, Verb = elevate ? "runas" : "", CreateNoWindow = !elevate,
                    WorkingDirectory = Path.GetDirectoryName(plan.Command.Executable)!
                };
                using var pinned = File.Open(plan.Command.Executable, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (Convert.ToHexString(SHA256.HashData(pinned)) != plan.ExecutableHash) throw new InvalidOperationException("启动前卸载器摘要改变。");
                if (plan.Installation.Identity.Read() != plan.Installation) throw new InvalidOperationException("启动前安装身份改变，必须重新确认。");
                using var process = Process.Start(info) ?? throw new IOException("没有取得卸载进程句柄。");
                pinned.Dispose();
                Exception storageError = null;
                try
                {
                    lock (_gate)
                    {
                        _state = _state with { StartedAt = DateTimeOffset.UtcNow, ProcessId = process.Id,
                            ProcessStartTicks = process.StartTime.ToUniversalTime().Ticks, Result = new(UninstallStatus.Running, null, "正在等待厂商卸载器结束，不会强制结束进程。") };
                        Record("开始执行", "已启动本次确认的卸载器。权限请求：" + elevate);
                    }
                }
                catch (Exception ex) { storageError = ex; }
                await process.WaitForExitAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    var result = storageError == null ? EvaluateExit(process.ExitCode, VerifyEvidence())
                        : new WorkflowResult(UninstallStatus.Unconfirmed, process.ExitCode, "执行期间任务记录未能保存，禁止清理：" + storageError.Message);
                    _state = _state with { Result = result };
                    Record(result.Label, result.Message + " 退出码：" + process.ExitCode);
                    return result;
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    var cancelled = ex is Win32Exception { NativeErrorCode: 1223 };
                    var result = new WorkflowResult(cancelled ? UninstallStatus.Cancelled : UninstallStatus.Unconfirmed, null,
                        cancelled ? "用户取消了权限请求，未确认卸载。" : "任务未完成核验：" + ex.Message);
                    _state = _state with { Result = result };
                    Record(result.Label, result.Message);
                    return result;
                }
            }
        }
    }

    internal static WorkflowResult EvaluateExit(int code, bool evidence)
    {
        if (code is 3010 or 1641) return new(UninstallStatus.RebootRequired, code, "卸载器报告需要重启；暂不授权残留处理，不自动重启。");
        if (code == 1602) return new(UninstallStatus.Cancelled, code, "卸载器报告用户取消，禁止清理残留。");
        if (code != 0) return new(UninstallStatus.Failed, code, "卸载器报告失败，禁止清理残留。");
        return evidence ? new(UninstallStatus.Verified, code, "标准入口已消失，独立产品/主程序证据一致。残留仍需单独预览和确认。")
            : new(UninstallStatus.Unconfirmed, code, "卸载器退出成功，但安装身份与独立证据尚未同时消失，禁止清理；可能仍有子进程或未完成卸载。");
    }

    internal bool VerifyEvidence()
    {
        var plan = State.Plan;
        if (plan.Installation.Identity.Read() != null) return false;
        if (plan.Command.IsMsi) return InstallerNative.MsiQueryProductState(plan.Command.ProductCode) == -1;
        if (plan.MainExecutable.Length == 0) return false;
        UninstallLab.CheckAncestors(plan.MainExecutable);
        return !File.Exists(plan.MainExecutable) && !Directory.Exists(plan.MainExecutable);
    }

    internal IReadOnlyList<ResidualFile> PreviewResiduals(CancellationToken cancellationToken)
    {
        using var execution = AcquireGlobal();
        using var lease = AcquireTask();
        RequireVerified();
        if (State.Isolation is not "未隔离" and not "已恢复") throw new InvalidOperationException("目录已进入隔离流程；请先处理恢复，保留原预览清单。");
        UninstallFiles.ValidateDirectory(InstallPath);
        if (!Directory.Exists(InstallPath)) { _state = _state with { Residuals = new() }; Record("无目录残留", "当前安装目录不存在。"); return Array.Empty<ResidualFile>(); }
        CheckSharedDirectory();
        var entry = new ApplicationUninstallerEntry { DisplayName = State.Plan.Installation.Name, InstallLocation = InstallPath };
        var scanner = new InstallLocationScanner();
        scanner.Setup(new[] { entry });
        if (!scanner.FindJunk(entry).OfType<FileSystemJunk>().Any(candidate => SamePath(candidate.Path.FullName, InstallPath)))
            throw new InvalidOperationException("BCU 未确认精确安装目录为残留候选，不开放隔离。");
        var files = SnapshotFiles(InstallPath, cancellationToken);
        _state = _state with { Residuals = files };
        Record("残留预览", $"已读取 {files.Count} 个文件的完整摘要，仅限原安装目录；未移动数据。");
        return files;
    }

    internal void Isolate(bool acknowledged, CancellationToken cancellationToken = default)
    {
        using var execution = AcquireGlobal();
        using var lease = AcquireTask();
        if (!acknowledged) throw new InvalidOperationException("必须单独确认所选安装目录残留可能包含个人数据。");
        RequireVerified();
        UninstallFiles.ValidateDirectory(InstallPath);
        CheckSharedDirectory();
        if (State.Isolation is not "未隔离" and not "已恢复") throw new InvalidOperationException("此任务已经进入隔离流程。");
        if (State.Residuals.Count == 0 || !SnapshotFiles(InstallPath, cancellationToken).SequenceEqual(State.Residuals))
            throw new InvalidOperationException("残留文件已改变或尚未预览，请重新核对。");
        UninstallLab.CheckAncestors(Quarantine);
        if (Directory.Exists(Quarantine) || File.Exists(Quarantine)) throw new IOException("隔离目标已存在，拒绝覆盖。");
        _state = _state with { Isolation = "准备隔离" }; Save();
        Directory.Move(InstallPath, Quarantine);
        _state = _state with { Isolation = "已隔离" }; Record("已隔离", "原安装目录移动到同盘独立隔离目录，未永久删除；恢复不代表重新安装。");
    }

    internal void Restore() => Restore(CancellationToken.None);

    internal void Restore(CancellationToken cancellationToken)
    {
        using var execution = AcquireGlobal();
        using var lease = AcquireTask();
        if (State.Isolation is not "准备隔离" and not "已隔离" and not "准备恢复") throw new InvalidOperationException("此任务没有可恢复的隔离记录。");
        UninstallFiles.ValidateDirectory(InstallPath);
        UninstallLab.CheckAncestors(Quarantine);
        if (!SnapshotFiles(Quarantine, cancellationToken).SequenceEqual(State.Residuals)) throw new InvalidOperationException("隔离区内容已改变，拒绝恢复。");
        if (Directory.Exists(InstallPath) || File.Exists(InstallPath)) throw new IOException("恢复目标已存在，拒绝覆盖；保留两边数据。");
        _state = _state with { Isolation = "准备恢复" }; Save();
        Directory.Move(Quarantine, InstallPath);
        _state = _state with { Isolation = "已恢复" }; Record("已恢复", "已还原隔离文件，没有重新安装应用或回滚厂商卸载操作。");
    }

    private void RequireVerified()
    {
        if (IsRunning || State.Result.Status != UninstallStatus.Verified || !VerifyEvidence())
            throw new InvalidOperationException("当前没有同时满足的卸载完成证据，禁止隔离残留。");
    }

    private void CheckSharedDirectory()
    {
        var entries = new RegistryFactory(Array.Empty<Guid>()).GetUninstallerEntries(_ => { });
        foreach (var entry in entries)
        {
            var path = entry.InstallLocation?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(path))
            {
                var executable = ReadOnlyCatalog.ReadIconExecutable(entry.DisplayIcon);
                if (executable.Length > 0) path = Path.GetDirectoryName(executable);
            }
            if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && (PortableScanner.IsWithin(path, InstallPath) || PortableScanner.IsWithin(InstallPath, path)))
                throw new InvalidOperationException("此目录与另一条安装记录重叠，保留为共享目录，不允许隔离。");
        }
    }

    private static List<ResidualFile> SnapshotFiles(string root, CancellationToken cancellationToken)
    {
        UninstallLab.CheckAncestors(root);
        var files = new List<ResidualFile>();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                UninstallLab.CheckAncestors(path);
                if (files.Count >= 20000) throw new IOException("残留条目过多，停止隔离预检。");
                if (Directory.Exists(path))
                {
                    files.Add(new(Path.GetRelativePath(root, path) + Path.DirectorySeparatorChar, 0, ""));
                    pending.Push(path);
                }
                else
                {
                    if (new FileInfo(path).Length > 2L * 1024 * 1024 * 1024) throw new IOException("单个残留文件超过 2 GiB，停止隔离预检。");
                    files.Add(new(Path.GetRelativePath(root, path), new FileInfo(path).Length, UninstallFiles.Hash(path, cancellationToken)));
                }
            }
        return files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList();
    }

    internal void Export(string path)
    {
        using var lease = AcquireTask();
        RecheckCore();
        CatalogStorage.Save(path, State);
    }

    internal void Recheck()
    {
        using var lease = AcquireTask();
        RecheckCore();
    }

    private void RecheckCore()
    {
        var status = State.Result.Status;
        if (status == UninstallStatus.Running && !ProcessMatches(State))
            _state = _state with { Result = new(UninstallStatus.Unconfirmed, null, "原进程已结束，但没有已保存的退出结果，禁止清理。") };
        else if (status == UninstallStatus.Verified && !VerifyEvidence())
            _state = _state with { Result = new(UninstallStatus.Unconfirmed, State.Result.ExitCode, "历史成功结论与当前安装状态不符，禁止清理。") };
        else if (status == UninstallStatus.RebootRequired && State.StartedAt.HasValue
            && DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64) > State.StartedAt.Value && VerifyEvidence())
            _state = _state with { Result = new(UninstallStatus.Verified, State.Result.ExitCode, "重启后精确安装记录与独立产品/主程序证据均确认已卸载。") };
        if (State.Isolation is "准备隔离" or "准备恢复")
        {
            UninstallFiles.ValidateDirectory(InstallPath); UninstallLab.CheckAncestors(Quarantine);
            if (!Directory.Exists(InstallPath) && Directory.Exists(Quarantine) && SnapshotFiles(Quarantine, default).SequenceEqual(State.Residuals))
                _state = _state with { Isolation = "已隔离" };
            else if (Directory.Exists(InstallPath) && !Directory.Exists(Quarantine) && SnapshotFiles(InstallPath, default).SequenceEqual(State.Residuals))
                _state = _state with { Isolation = State.Isolation == "准备恢复" ? "已恢复" : "未隔离" };
        }
        Save();
    }

    private FileStream AcquireTask()
    {
        if (IsRunning) throw new InvalidOperationException("卸载器仍在运行，请等待，不能进行残留操作。");
        UninstallLab.CheckAncestors(Root);
        var path = Path.Combine(Root, "task.lock");
        UninstallLab.CheckAncestors(path);
        var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            _state = Open(State.Id, _fixture).State;
            if (ProcessMatches(State)) throw new InvalidOperationException("卸载器仍在运行，不能进行残留操作。");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private FileStream AcquireGlobal()
    {
        var root = StorageRoot(_fixture);
        UninstallLab.CheckAncestors(root);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "execution.lock"); UninstallLab.CheckAncestors(path);
        var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            foreach (var id in ListTasks(_fixture))
                if (ProcessMatches(Open(id, _fixture).State)) throw new InvalidOperationException("卸载任务仍在运行，禁止并发卸载或残留操作。");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static bool ProcessMatches(WorkflowState state)
    {
        if (state.ProcessId == null || state.ProcessStartTicks == null || state.Result.Status != UninstallStatus.Running) return false;
        try
        {
            using var process = Process.GetProcessById(state.ProcessId.Value);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == state.ProcessStartTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; }
    }

    private void Record(string action, string detail)
    {
        var events = State.Events.TakeLast(999).ToList(); events.Add(new(DateTimeOffset.UtcNow, action, detail));
        _state = _state with { Events = events }; Save();
    }

    private void Save()
    {
        UninstallLab.CheckAncestors(Root);
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, "task.bin"); UninstallLab.CheckAncestors(path);
        var json = JsonSerializer.SerializeToUtf8Bytes(State, CatalogStorage.JsonOptions);
        if (json.Length > 15 * 1024 * 1024) throw new InvalidDataException("任务记录超过大小限制，保留上次记录。");
        var data = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
        var temporary = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(temporary, data); File.Move(temporary, path, true);
    }

    private static bool SamePath(string a, string b) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(a))
        .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
