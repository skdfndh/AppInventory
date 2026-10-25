using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UninstallTools;
using UninstallTools.Junk.Containers;
using UninstallTools.Junk.Finders.Drive;
using UninstallTools.Uninstaller;

namespace AppInventory;

internal sealed record LabMarker(int Schema, string Token, string BinaryHash);
internal sealed record LabPlan(string Token, string Scenario, string Executable, string ExecutableHash, string Command, string Target, bool FixtureMode);
internal sealed record LabResult(string Status, int? ExitCode, bool Verified, string Message);
internal sealed record LabEvent(DateTimeOffset Time, string Action, string Detail);
internal sealed record LabReport(int Schema, DateTimeOffset ExportedAt, string Token, string Target, string Quarantine, string Retained,
    LabResult Result, List<LabEvent> Events, string Limitation);

internal sealed class UninstallLab
{
    internal static string LabRoot => Path.Combine(CatalogStorage.DataDirectory, "uninstall-lab");
    internal static string SourceBinary => Path.Combine(AppContext.BaseDirectory, "测试组件", "TestApplication.exe");
    private static string ApprovedBinaryHash => typeof(UninstallLab).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "LabFixtureSHA256").Value;
    internal string Token { get; }
    private bool FixtureMode { get; }
    internal string Root => Path.Combine(FixtureMode ? Path.Combine(AppContext.BaseDirectory, "read-only-fixtures", "uninstall-lab") : LabRoot, Token);
    internal string AppDirectory => Path.Combine(Root, "test-app");
    internal string Target => Path.Combine(AppDirectory, "TestApplication.exe");
    internal string Quarantine => Path.Combine(Root, "quarantine");
    internal string Backup => Path.Combine(Root, "uninstalled-program.exe");
    internal string RetainedData => Path.Combine(Root, "user-data", "keep.txt");
    internal List<LabEvent> Events { get; } = new();
    internal LabResult LastResult { get; private set; }
    private Process _activeProcess;

    private UninstallLab(string token, bool fixtureMode = false)
    {
        if (!Guid.TryParseExact(token, "N", out _)) throw new ArgumentException("测试会话标识无效。");
        Token = token;
        FixtureMode = fixtureMode;
    }

    internal static UninstallLab Create(bool fixtureMode = false)
    {
        var session = new UninstallLab(Guid.NewGuid().ToString("N"), fixtureMode);
        CheckAncestors(Path.GetDirectoryName(session.Root)!);
        CheckAncestors(SourceBinary);
        var sourceHash = Hash(SourceBinary);
        if (sourceHash != ApprovedBinaryHash) throw new InvalidDataException("随包测试组件与构建时摘要不匹配，停止创建。");
        if (Directory.Exists(session.Root)) throw new IOException("测试目录已存在，拒绝覆盖。");
        Directory.CreateDirectory(session.AppDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(session.RetainedData)!);
        CheckAncestors(session.AppDirectory);
        File.Copy(SourceBinary, session.Target, false);
        File.WriteAllText(Path.Combine(session.AppDirectory, "cache.log"), session.CacheText);
        File.WriteAllText(session.RetainedData, session.RetainedText);
        CatalogStorage.Save(Path.Combine(session.Root, "session.json"), new LabMarker(1, session.Token, sourceHash));
        session.Record("创建", "已创建独立可执行测试应用、缓存及应保留数据，未注册系统安装记录。");
        return session;
    }

    internal static UninstallLab Open(string token, bool fixtureMode = false)
    {
        var session = new UninstallLab(token, fixtureMode);
        session.ValidateRoot();
        session.LoadState();
        return session;
    }

    internal LabPlan Plan(string scenario)
    {
        EnsureIdle();
        ValidateScenario(scenario);
        ValidateRoot();
        if (File.Exists(Backup) || File.Exists(Path.Combine(Root, "uninstalled.ok")) || Directory.Exists(Quarantine))
            throw new InvalidOperationException("此测试会话已执行过卸载，请新建测试应用。");
        ValidateApp(AppDirectory, true);
        var executable = Path.Combine(AppContext.BaseDirectory, "AppInventory.exe");
        CheckAncestors(executable);
        return new LabPlan(Token, scenario, executable, Hash(executable), $"\"{executable}\" --lab-uninstall {Token} {scenario} {(FixtureMode ? "fixture" : "local")}", Target, FixtureMode);
    }

    internal async Task<LabResult> RunAsync(LabPlan plan, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var lease = AcquireOperation();
        var current = Plan(plan.Scenario);
        if (current != plan) throw new InvalidOperationException("卸载计划已改变，必须重新预览。");
        cancellationToken.ThrowIfCancellationRequested();
        Record("计划", plan.Command);
        // 只启动刚复制并核对哈希的专用测试程序，验证其可运行性。
        using (var testApp = Process.Start(new ProcessStartInfo(Target) { UseShellExecute = false, CreateNoWindow = true }))
        {
            if (testApp == null) throw new IOException("无法运行专用测试应用。");
            try { await testApp.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (!testApp.HasExited) { testApp.Kill(true); await testApp.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                LastResult = new("已取消测试", null, false, "测试应用运行检查已停止，未启动卸载辅助进程。");
                Record(LastResult.Status, LastResult.Message);
                SaveResult();
                return LastResult;
            }
            catch { if (!testApp.HasExited) testApp.Kill(true); throw; }
            if (testApp.ExitCode != 0) throw new IOException("专用测试应用未能正常运行。");
        }
        ValidateApp(AppDirectory, true);
        if (Hash(plan.Executable) != plan.ExecutableHash) throw new InvalidOperationException("测试辅助程序已改变，停止执行。");
        var entry = Entry(plan.Command);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
        LastResult = null;
        try
        {
            _activeProcess = entry.RunUninstaller(false, false, true) ?? throw new IOException("BCU 未返回测试卸载进程。");
            Record("执行", "已通过 BCU 启动专用测试卸载辅助进程。");
            await _activeProcess.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var exitCode = _activeProcess.ExitCode;
            var verified = exitCode == 0 && IsUninstalled();
            LastResult = new(exitCode == 0 ? verified ? "已验证卸载" : "结果不符" : "卸载失败", exitCode, verified,
                verified ? "测试程序已移出安装目录；缓存和应保留数据仍在。" : "没有同时满足退出码与文件状态检查，禁止隔离残留。");
        }
        catch (OperationCanceledException)
        {
            // 这里的进程只可能是本程序新启动的测试辅助进程，不用于终止厂商卸载器。
            if (_activeProcess != null && !_activeProcess.HasExited)
            {
                _activeProcess.Kill(true);
                await _activeProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            LastResult = new(cancellationToken.IsCancellationRequested ? "已取消测试" : "测试超时", _activeProcess?.HasExited == true ? _activeProcess.ExitCode : null,
                false, "已停止专用测试辅助进程，禁止根据此次结果隔离残留。");
        }
        finally
        {
            if (_activeProcess?.HasExited == true) { _activeProcess.Dispose(); _activeProcess = null; }
        }
        Record(LastResult.Status, LastResult.Message + " 退出码：" + LastResult.ExitCode);
        SaveResult();
        return LastResult;
    }

    internal static int RunWorker(string token, string scenario, string mode)
    {
        try
        {
            ValidateScenario(scenario);
            if (mode is not "fixture" and not "local") return 27;
            var session = Open(token, mode == "fixture");
            session.ValidateApp(session.AppDirectory, true);
            if (File.Exists(session.Backup) || File.Exists(Path.Combine(session.Root, "uninstalled.ok"))) return 29;
            if (scenario == "fail") return 23;
            if (scenario == "noop") return 0;
            if (scenario == "slow") Thread.Sleep(20_000);
            session.ValidateApp(session.AppDirectory, true);
            File.Move(session.Target, session.Backup, false);
            File.WriteAllText(Path.Combine(session.Root, "uninstalled.ok"), token);
            return 0;
        }
        catch { return 27; }
    }

    internal IReadOnlyList<RelatedLocation> Preview()
    {
        using var lease = AcquireOperation();
        return PreviewCore();
    }

    private IReadOnlyList<RelatedLocation> PreviewCore()
    {
        EnsureIdle();
        if (LastResult?.Verified != true || !IsUninstalled()) throw new InvalidOperationException("没有已确认的本次卸载成功结果，不能预览可隔离残留。");
        ValidateApp(AppDirectory, false);
        var entry = Entry("");
        var scanner = new InstallLocationScanner();
        scanner.Setup(new[] { entry });
        var candidates = scanner.FindJunk(entry).OfType<FileSystemJunk>().ToArray();
        if (candidates.Length != 1 || !SamePath(candidates[0].Path.FullName, AppDirectory))
            throw new InvalidOperationException("BCU 返回的测试残留位置不符合本会话的精确白名单。");
        var file = new FileInfo(Path.Combine(AppDirectory, "cache.log"));
        Record("残留预览", "BCU 定向扫描与会话白名单一致，仅包含已知测试缓存。");
        return new[] { new RelatedLocation(AppDirectory, file.Length, false, "专用测试缓存", "BCU 安装目录扫描；精确路径与测试文件内容均已核对。", false) };
    }

    internal void Isolate()
    {
        using var lease = AcquireOperation();
        PreviewCore();
        CheckAncestors(Quarantine);
        if (Directory.Exists(Quarantine) || File.Exists(Quarantine)) throw new IOException("隔离目标已存在，拒绝覆盖。");
        Directory.Move(AppDirectory, Quarantine);
        Record("已隔离", "仅移动测试缓存目录到本会话隔离区，未永久删除。");
    }

    internal void Restore()
    {
        using var lease = AcquireOperation();
        if (!IsUninstalled()) throw new InvalidOperationException("测试会话的卸载证据不完整，拒绝恢复操作。");
        ValidateApp(Quarantine, false);
        if (Directory.Exists(AppDirectory) || File.Exists(AppDirectory)) throw new IOException("恢复目标已存在，拒绝覆盖。");
        Directory.Move(Quarantine, AppDirectory);
        Record("已恢复", "已恢复测试缓存，应保留数据完整；没有重新安装测试程序。");
    }

    internal bool IsUninstalled()
    {
        ValidateRoot();
        CheckAncestors(Target);
        CheckAncestors(Backup);
        CheckAncestors(Path.Combine(Root, "uninstalled.ok"));
        var marker = ReadMarker();
        return !File.Exists(Target) && File.Exists(Backup) && Hash(Backup) == marker.BinaryHash
            && File.Exists(Path.Combine(Root, "uninstalled.ok")) && File.ReadAllText(Path.Combine(Root, "uninstalled.ok")) == Token;
    }

    internal void Export(string path)
    {
        using var lease = AcquireOperation();
        CatalogStorage.Save(path, new LabReport(1, DateTimeOffset.Now, Token, Target, Quarantine,
            RetainedData, LastResult, Events, "仅验证本工具创建的测试应用，不证明真实 MSI、NSIS、商店应用卸载兼容性。"));
    }

    private ApplicationUninstallerEntry Entry(string command) => new()
    {
        DisplayName = "应用清单专用测试应用 " + Token[..8], DisplayVersion = "1.0-test", Publisher = "App Inventory Lab",
        InstallLocation = AppDirectory, UninstallString = command, UninstallerKind = UninstallerType.Unknown
    };

    private string CacheText => "APP-INVENTORY-LAB-CACHE:" + Token;
    private string RetainedText => "APP-INVENTORY-LAB-KEEP:" + Token;
    private LabMarker ReadMarker() => CatalogStorage.Read<LabMarker>(Path.Combine(Root, "session.json"))
        ?? throw new InvalidDataException("缺少测试会话标记。");

    private void ValidateRoot()
    {
        CheckAncestors(Root);
        CheckAncestors(Path.Combine(Root, "session.json"));
        var marker = ReadMarker();
        if (marker.Schema != 1 || marker.Token != Token || marker.BinaryHash != ApprovedBinaryHash)
            throw new InvalidDataException("测试会话标记不匹配。");
        CheckAncestors(RetainedData);
        if (File.ReadAllText(RetainedData) != RetainedText) throw new InvalidDataException("应保留测试数据已改变，停止操作。");
    }

    private void ValidateApp(string path, bool installed)
    {
        ValidateRoot();
        if (!SamePath(path, AppDirectory) && !SamePath(path, Quarantine)) throw new InvalidOperationException("目标不在会话白名单内。");
        CheckAncestors(path);
        var files = Directory.GetFileSystemEntries(path);
        var allowed = installed ? new[] { "TestApplication.exe", "cache.log" } : new[] { "cache.log" };
        if (files.Length != allowed.Length || files.Any(file => !allowed.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("测试目录含有额外或缺失的文件，停止操作。");
        foreach (var file in files) CheckAncestors(file);
        if (File.ReadAllText(Path.Combine(path, "cache.log")) != CacheText) throw new InvalidDataException("测试缓存内容不匹配。");
        if (installed && Hash(Path.Combine(path, "TestApplication.exe")) != ReadMarker().BinaryHash)
            throw new InvalidDataException("专用测试程序哈希不匹配。");
    }

    private void EnsureIdle()
    {
        if (_activeProcess != null && !_activeProcess.HasExited) throw new InvalidOperationException("本会话的测试进程仍在运行，请等待。");
    }

    private FileStream AcquireOperation()
    {
        EnsureIdle();
        ValidateRoot();
        var lockPath = Path.Combine(Root, "operation.lock");
        CheckAncestors(lockPath);
        var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            // 获得互斥后再刷新，避免较早打开的实例覆盖其他实例刚保存的记录。
            LoadState();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private void LoadState()
    {
        CheckAncestors(Path.Combine(Root, "events.json"));
        CheckAncestors(Path.Combine(Root, "result.json"));
        var events = CatalogStorage.Read<List<LabEvent>>(Path.Combine(Root, "events.json"));
        var result = CatalogStorage.Read<LabResult>(Path.Combine(Root, "result.json"));
        if (result != null && (result.Status == null || result.Message == null || result.Verified && result.ExitCode != 0))
            throw new InvalidDataException("测试会话的执行结论格式无效。");
        if (result?.Verified == true && !IsUninstalled())
            result = new("状态已改变", result.ExitCode, false, "历史卸载成功结论与当前文件状态不符，禁止隔离残留；请检查本会话的程序和备份。");
        else if (result == null)
        {
            var completionPath = Path.Combine(Root, "uninstalled.ok");
            CheckAncestors(Backup);
            CheckAncestors(completionPath);
            if (File.Exists(Backup) || File.Exists(completionPath))
                result = new("上次执行未确认", null, false, "发现卸载文件变动，但没有保存的执行结果；保留现有文件，禁止根据本会话隔离残留。");
        }
        Events.Clear();
        if (events != null) Events.AddRange(events.TakeLast(1000));
        LastResult = result;
    }

    private void Record(string action, string detail)
    {
        ValidateRoot();
        var path = Path.Combine(Root, "events.json");
        CheckAncestors(path);
        Events.Add(new(DateTimeOffset.Now, action, detail));
        CatalogStorage.Save(path, Events);
    }

    private void SaveResult()
    {
        ValidateRoot();
        var path = Path.Combine(Root, "result.json");
        CheckAncestors(path);
        CatalogStorage.Save(path, LastResult);
    }

    private static void ValidateScenario(string scenario)
    {
        if (scenario is not "success" and not "fail" and not "noop" and not "slow") throw new ArgumentException("测试场景无效。");
    }

    internal static void CheckAncestors(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("测试路径包含链接或目录联接，拒绝操作。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
