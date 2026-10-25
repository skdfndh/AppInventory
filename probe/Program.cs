using System.Reflection;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Klocman.Tools;
using UninstallTools;
using UninstallTools.Factory.InfoAdders;
using UninstallTools.Junk;
using UninstallTools.Junk.Containers;
using UninstallTools.Junk.Finders;
using UninstallTools.Junk.Finders.Drive;
using UninstallTools.Uninstaller;

internal static class Program
{
    private static readonly List<string> Passed = new();

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--fixture-uninstall")
                return UninstallFixture(args[1]);
            if (args.Length == 2 && args[0] == "--fixture-helper-fail")
            {
                ValidateMarker(FixturePath(args[1]), args[1]);
                return 23;
            }
            if (args.Length == 2 && args[0] == "--fixture-helper-output")
            {
                ValidateMarker(FixturePath(args[1]), args[1]);
                Console.OutputEncoding = Encoding.Unicode;
                Console.WriteLine("fixture:" + args[1]);
                return 0;
            }
            if (args.Length != 0)
                throw new ArgumentException("验证程序不接受任意卸载目标。");

            RunChecks();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "probe-results.json"),
                JsonSerializer.Serialize(new { passed = Passed.Count, checks = Passed },
                    new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"全部通过：{Passed.Count} 项。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void RunChecks()
    {
        var core = typeof(ApplicationUninstallerEntry).Assembly;
        var references = core.GetReferencedAssemblies().Select(x => x.Name).ToArray();
        Check(!references.Contains("ObjectListView"), "核心程序集没有 ObjectListView 引用");
        Check(core.GetType("UninstallTools.Dialogs.StartupManagerWindow") == null,
            "依赖第三方控件的启动项窗口未编入核心库");
        Check(!core.GetManifestResourceNames().Any(x => x.Contains("StartupManagerWindow")),
            "启动项窗口资源未编入核心库");
        Check(!typeof(Klocman.IO.FileSize).Assembly.GetReferencedAssemblies()
            .Any(x => x.Name == "ObjectListView"), "工具库没有 ObjectListView 引用");
        Check(!references.Any(x => x != null && x.Contains("Scripting")), "没有构建时生成的 Scripting 互操作程序集引用");
        Check(!core.GetTypes().Any(x => x.Namespace == "UninstallTools.Controls" || x.Namespace == "UninstallTools.Dialogs"),
            "核心库未编入自带窗口及控件类型");
        Check(!references.Contains("System.Windows.Forms"), "核心程序集没有直接 Windows Forms 引用");

        var token = Guid.NewGuid().ToString("N");
        var root = FixturePath(token);
        if (Directory.Exists(root)) throw new IOException("测试目录已存在，拒绝覆盖。");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".fixture-token"), token);
        Console.WriteLine("测试目录：" + root);

        var appDir = Path.Combine(root, "app sample");
        Directory.CreateDirectory(appDir);
        var payload = Path.Combine(appDir, "test-app.payload");
        File.WriteAllText(payload, "BCU-TEST:" + token);
        File.WriteAllText(Path.Combine(appDir, "leftover.log"), "仅用于残留清理验证");
        var keep = Path.Combine(root, "retained-user-data.txt");
        File.WriteAllText(keep, "必须保留的测试数据");

        var sizeDir = Path.Combine(root, "size sample");
        Directory.CreateDirectory(Path.Combine(sizeDir, "nested"));
        File.WriteAllBytes(Path.Combine(sizeDir, "a.bin"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(sizeDir, "nested", "b.bin"), new byte[3072]);
        var sizeEntry = new ApplicationUninstallerEntry { DisplayName = "大小统计测试", InstallLocation = sizeDir };
        new FastSizeGenerator().AddMissingInformation(sizeEntry);
        Check(sizeEntry.EstimatedSize.GetKbSize() == 5, "运行时 COM 调用正确统计两层目录的 5 KiB 测试文件");

        var executable = Path.Combine(AppContext.BaseDirectory, "CoreProbe.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("缺少测试程序宿主。", executable);
        var entry = new ApplicationUninstallerEntry
        {
            DisplayName = "专用测试应用 " + token,
            DisplayVersion = "1.0-test",
            Publisher = "BCU Core Spike",
            InstallLocation = appDir,
            UninstallString = $"\"{executable}\" --fixture-uninstall {token}",
            UninstallerKind = UninstallerType.Unknown
        };

        using (var json = JsonDocument.Parse(ApplicationEntrySerializer.SerializeApplicationEntriesToJson(new[] { entry })))
            Check(json.RootElement[0].GetProperty("InstallLocation").GetString() == appDir,
                "应用元数据可独立导出为 JSON，并保留中文及空格路径");

        entry.RunUninstaller(false, true);
        Check(File.Exists(payload), "模拟卸载没有改动专用测试程序");
        using (var process = entry.RunUninstaller(false, false))
        {
            if (process == null || !process.WaitForExit(15000))
                throw new TimeoutException("专用测试程序卸载没有按时完成。");
            Check(process.ExitCode == 0, "BCU 成功调用专用测试程序的卸载命令");
        }
        Check(!File.Exists(payload) && File.Exists(Path.Combine(root, "uninstalled.ok")),
            "测试程序主体已移除并留下成功标记");

        var scanner = new InstallLocationScanner();
        scanner.Setup(new[] { entry });
        var candidates = scanner.FindJunk(entry).OfType<FileSystemJunk>().ToArray();
        Check(candidates.Length == 1 && SamePath(candidates[0].Path.FullName, appDir),
            "定向残留扫描只返回专用测试程序安装目录");
        Check(File.Exists(Path.Combine(appDir, "leftover.log")), "预览残留结果没有触发删除");

        var sharedDir = Path.Combine(root, "shared sample");
        Directory.CreateDirectory(Path.Combine(sharedDir, "other-app"));
        var sharedSentinel = Path.Combine(sharedDir, "other-app", "keep.txt");
        File.WriteAllText(sharedSentinel, "其他测试应用仍在使用");
        var sharedTarget = new ApplicationUninstallerEntry { DisplayName = "共享目录测试", InstallLocation = sharedDir };
        var other = new ApplicationUninstallerEntry { DisplayName = "其他测试应用", InstallLocation = Path.GetDirectoryName(sharedSentinel) };
        scanner.Setup(new[] { sharedTarget });
        var standaloneScore = scanner.FindJunk(sharedTarget).Single().Confidence.GetRawConfidence();
        scanner.Setup(new[] { sharedTarget, other });
        var sharedScore = scanner.FindJunk(sharedTarget).Single().Confidence.GetRawConfidence();
        Check(sharedScore < standaloneScore, "提供完整应用清单后，共享目录的清理置信度降低");
        Check(JunkCreatorBase.CheckIfDirIsStillUsed(sharedDir, new[] { other.InstallLocation }),
            "能识别目录仍被其他测试应用使用");

        var rejected = false;
        try { ValidateCleanupTarget(sharedDir, root, token); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "清理边界拒绝非白名单目录，包括共享目录");

        CheckBackgroundErrors(entry, executable, root, token);

        ValidateCleanupTarget(candidates[0].Path.FullName, root, token);
        var missingHandlerRejected = false;
        try { candidates[0].Delete(); }
        catch (InvalidOperationException) { missingHandlerRejected = true; }
        Check(missingHandlerRejected && Directory.Exists(appDir), "宿主未提供文件回收处理器时拒绝清理");

        var previousHandler = UninstallToolsGlobalConfig.FileDeletionHandler;
        var quarantine = Path.Combine(root, "quarantine");
        try
        {
            UninstallToolsGlobalConfig.FileDeletionHandler = _ => throw new IOException("模拟宿主回收失败");
            var hostFailureRaised = false;
            try { candidates[0].Delete(); }
            catch (IOException) { hostFailureRaised = true; }
            Check(hostFailureRaised && Directory.Exists(appDir), "宿主回收失败时异常传回调用方且不删除文件");

            UninstallToolsGlobalConfig.FileDeletionHandler = path =>
            {
                ValidateCleanupTarget(path.FullName, root, token);
                if (Directory.Exists(quarantine)) throw new IOException("测试隔离区已存在，拒绝覆盖。");
                Directory.Move(path.FullName, quarantine);
            };
            candidates[0].Delete();
            Check(!Directory.Exists(appDir) && File.Exists(Path.Combine(quarantine, "leftover.log")),
                "宿主通过回收处理器将测试残留移入隔离区");

            ValidateMarker(root, token);
            AssertNoLinks(quarantine);
            if (Directory.Exists(appDir)) throw new IOException("恢复目标已存在，拒绝覆盖。");
            Directory.Move(quarantine, appDir);
            Check(File.ReadAllText(Path.Combine(appDir, "leftover.log")) == "仅用于残留清理验证",
                "隔离区中的测试残留可恢复且内容完整");
            candidates[0].Delete();
        }
        finally { UninstallToolsGlobalConfig.FileDeletionHandler = previousHandler; }
        Check(File.ReadAllText(keep) == "必须保留的测试数据" && File.Exists(sharedSentinel),
            "保留数据和共享目录哨兵完整");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(x => x.GetName().Name == "ObjectListView"),
            "实际运行全程未加载 ObjectListView");
    }

    private static void CheckBackgroundErrors(ApplicationUninstallerEntry entry, string executable, string root, string token)
    {
        var previousCallback = UninstallToolsGlobalConfig.ErrorCallback;
        var previousErrorOutput = Console.Error;
        using var capturedErrorOutput = new StringWriter();
        var errors = new List<Exception>();
        try
        {
            Console.SetError(capturedErrorOutput);
            UninstallToolsGlobalConfig.ErrorCallback = errors.Add;
            var setupFailure = new ProbeScanner(new IOException("模拟扫描器初始化失败"), null);
            var scanFailure = new ProbeScanner(null, new IOException("模拟残留扫描失败"));
            var healthy = new ProbeScanner(null, null);
            var results = JunkManager.FindJunk(new[] { entry }, new[] { entry }, _ => { },
                new IJunkCreator[] { setupFailure, scanFailure, healthy }).ToArray();
            Check(errors.Count == 2, "扫描器初始化及扫描异常均由宿主错误回调接收");
            Check(!setupFailure.Visited && healthy.Visited && results.Length == 0,
                "跳过初始化失败的扫描器，其他扫描器仍可完成");

            var afterCancel = new ProbeScanner(null, null);
            var cancelled = false;
            try
            {
                JunkManager.FindJunk(new[] { entry }, new[] { entry }, _ => { },
                    new IJunkCreator[] { new ProbeScanner(null, new OperationCanceledException()), afterCancel }).ToArray();
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !afterCancel.Visited && errors.Count == 2,
                "取消扫描会传回宿主并停止后续扫描器");

            var noUninstallerRaised = false;
            try { new ApplicationUninstallerEntry().RunUninstaller(false, false); }
            catch (NoWayToUninstallException) { noUninstallerRaised = true; }
            Check(noUninstallerRaised, "没有卸载命令时返回异常，不显示核心错误窗口");

            var helper = new RunProcessJunk(entry, null,
                new ProcessStartCommand(executable, "--fixture-helper-fail " + token), "专用失败测试辅助程序");
            var failureRaised = false;
            try { helper.Delete(); }
            catch (IOException ex) { failureRaised = ex.Message.Contains("23"); }
            Check(failureRaised && errors.Count == 3, "辅助程序非零退出码会报告给宿主并抛出清理失败");

            var readHelper = typeof(ApplicationUninstallerEntry).Assembly
                .GetType("UninstallTools.Factory.FactoryTools")!
                .GetMethod("StartHelperAndReadOutput", BindingFlags.NonPublic | BindingFlags.Static)!;
            var failedOutput = readHelper.Invoke(null, new object[] { executable, "--fixture-helper-fail " + token });
            Check(failedOutput == null && errors.Count == 4,
                "应用发现辅助程序非零退出时返回失败并通知宿主");

            var invalidHelper = Path.Combine(root, "invalid-helper.exe");
            File.WriteAllText(invalidHelper, "BCU-TEST-INVALID:" + token);
            var invalidOutput = readHelper.Invoke(null, new object[] { invalidHelper, string.Empty });
            Check(invalidOutput == null && errors.Count == 5,
                "应用发现辅助程序无法启动时异常也由宿主接收");

            var validOutput = readHelper.Invoke(null, new object[] { executable, "--fixture-helper-output " + token });
            Check(validOutput is string text && text.Trim() == "fixture:" + token && errors.Count == 5,
                "应用发现辅助程序成功时可读取 Unicode 输出并确认退出");

            UninstallToolsGlobalConfig.ErrorCallback = null;
            var noCallbackScanner = new ProbeScanner(null, new IOException("无回调测试"));
            using var traceOutput = new StringWriter();
            using var listener = new TextWriterTraceListener(traceOutput);
            Trace.Listeners.Add(listener);
            try
            {
                JunkManager.FindJunk(new[] { entry }, new[] { entry }, _ => { },
                    new[] { noCallbackScanner }).ToArray();
                Trace.Flush();
                Check(noCallbackScanner.Visited && traceOutput.ToString().Contains("无回调测试"),
                    "宿主未订阅错误时仍可返回，并通过 Trace 诊断通道保留错误");
            }
            finally { Trace.Listeners.Remove(listener); }

            var selfProtected = JunkManager.FindJunk(new[] { entry }, new[] { entry }, _ => { },
                new IJunkCreator[] { new InstallLocationScanner() }).ToArray();
            Check(selfProtected.Length == 0 && File.Exists(Path.Combine(root, "app sample", "leftover.log")),
                "完整残留管理器过滤引擎自身目录下的候选项且不自动删除");
            Check(capturedErrorOutput.ToString().Length == 0, "受控后台扫描不向标准错误输出写入异常堆栈");
        }
        finally
        {
            Console.SetError(previousErrorOutput);
            UninstallToolsGlobalConfig.ErrorCallback = previousCallback;
        }
    }

    private sealed class ProbeScanner : IJunkCreator
    {
        private readonly Exception _setupError;
        private readonly Exception _scanError;
        public bool Visited { get; private set; }
        public string CategoryName => "受控测试扫描器";

        public ProbeScanner(Exception setupError, Exception scanError)
        {
            _setupError = setupError;
            _scanError = scanError;
        }

        public void Setup(ICollection<ApplicationUninstallerEntry> allUninstallers)
        {
            if (_setupError != null) throw _setupError;
        }

        public IEnumerable<IJunkResult> FindJunk(ApplicationUninstallerEntry target)
        {
            Visited = true;
            if (_scanError != null) throw _scanError;
            return Array.Empty<IJunkResult>();
        }
    }

    private static int UninstallFixture(string token)
    {
        var root = FixturePath(token);
        ValidateMarker(root, token);
        var payload = Path.Combine(root, "app sample", "test-app.payload");
        AssertNoLinks(Path.GetDirectoryName(payload));
        if (File.ReadAllText(payload) != "BCU-TEST:" + token)
            throw new InvalidOperationException("测试程序内容不匹配，拒绝执行。");
        File.Delete(payload);
        File.WriteAllText(Path.Combine(root, "uninstalled.ok"), token);
        return 0;
    }

    private static string FixturePath(string token)
    {
        if (!Guid.TryParseExact(token, "N", out _))
            throw new ArgumentException("只允许随机生成的测试标识。");
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures", token));
    }

    private static void ValidateCleanupTarget(string path, string root, string token)
    {
        ValidateMarker(root, token);
        // 测试清理只允许这个精确目录，不能把上游扫描结果直接当作删除授权。
        if (!SamePath(path, Path.Combine(root, "app sample")))
            throw new InvalidOperationException("拒绝清理白名单以外的路径。");
        AssertNoLinks(path);
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("测试清理拒绝目录联接和符号链接。");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(item);
            }
        }
    }

    private static void ValidateMarker(string root, string token)
    {
        if (!SamePath(root, FixturePath(token)))
            throw new InvalidOperationException("测试根目录不匹配。");
        AssertNoLinks(root);
        if (File.ReadAllText(Path.Combine(root, ".fixture-token")) != token)
            throw new InvalidOperationException("缺少有效测试标记。");
    }

    private static void AssertNoLinks(string path)
    {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("测试路径包含目录联接或符号链接。");
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void Check(bool success, string description)
    {
        if (!success) throw new InvalidOperationException("验证失败：" + description);
        Passed.Add(description);
        Console.WriteLine("[通过] " + description);
    }
}
