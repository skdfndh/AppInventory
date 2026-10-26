using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace AppInventory;

internal static class SelfTest
{
    private static string _progressPath;

    internal static void Progress(string stage)
    {
        if (_progressPath == null) return;
        var line = JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, stage }) + Environment.NewLine;
        // 先落盘再执行下一阶段，中断后仍可分辨最后开始与完成的检查。
        using var stream = new FileStream(_progressPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(System.Text.Encoding.UTF8.GetBytes(line)); stream.Flush(true);
    }

    public static int Run(string resultPath)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        {
            Write(resultPath, new { passed = 0, error = "完整自检包含安装、卸载与注册表写入，现仅在独立 CI 环境运行；本机已阻止执行。" });
            return 2;
        }
        var checks = new List<string>();
        _progressPath = resultPath + ".progress.jsonl";
        Progress("开始：隔离环境完整自检");
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("验证失败：" + name);
            checks.Add(name);
            Progress("通过：" + name);
        }
        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new MainWindow(false);
            var small = new AppRow { Name = "编辑器 Alpha", Version = "2.0", Publisher = "测试发布者", InstallPath = @"D:\测试 软件\Alpha", ReportedBytes = 900L * 1024 * 1024 };
            var large = new AppRow { Name = "工具 Beta", Version = "10.0", InstallPath = @"D:\Tools\Beta", ReportedBytes = 2L * 1024 * 1024 * 1024 };
            var unknown = new AppRow { Name = "大小未知", InstallPath = "" };
            small.SetDescription("用于验证只读来源链接的测试说明。", "测试来源", "https://code.visualstudio.com/docs");
            window.SetApps(new List<AppRow> { large, unknown, small });
            var grid = (DataGrid)window.FindName("AppsGrid");
            var search = (TextBox)window.FindName("SearchBox");
            var view = (ListCollectionView)grid.ItemsSource;
            Check(grid.IsReadOnly && !grid.CanUserAddRows && !grid.CanUserDeleteRows, "表格禁止编辑、新增和删除");
            Check(grid.Columns.Count == 5, "清单包含名称、关联位置、版本、路径与占用空间");
            search.Text = "alpha";
            Check(view.Count == 1 && ReferenceEquals(view.GetItemAt(0), small), "名称搜索不区分大小写");
            search.Text = "编辑器 2.0";
            Check(view.Count == 1, "多关键词可同时匹配名称和版本");
            search.Text = "测试 软件";
            Check(view.Count == 1, "可搜索中文及含空格的安装路径");
            search.Text = "测试发布者";
            Check(view.Count == 1, "可按发布者搜索");
            search.Text = "不存在的应用关键词";
            Check(view.Count == 0 && ((FrameworkElement)window.FindName("EmptyState")).Visibility == Visibility.Visible, "无搜索结果时显示空状态");
            search.Text = "   ";
            Check(view.Count == 3, "空白搜索恢复全部记录");
            window.Sort(nameof(AppRow.SizeBytes), true);
            Check(ReferenceEquals(view.GetItemAt(0), large) && ReferenceEquals(view.GetItemAt(2), unknown), "空间按实际数值降序，未知值置后");
            window.Sort(nameof(AppRow.SizeBytes), false);
            Check(ReferenceEquals(view.GetItemAt(0), small) && ReferenceEquals(view.GetItemAt(2), unknown), "空间按实际数值升序，未知值仍置后");
            window.Sort(nameof(AppRow.Version), false);
            Check(view.IndexOf(small) < view.IndexOf(large), "可解析的版本按版本号排序，2.0 排在 10.0 前");
            grid.SelectedItem = small;
            Check(ReferenceEquals(((FrameworkElement)window.FindName("SelectionPanel")).DataContext, small), "选中应用后详情区显示同一条记录");
            Check(unknown.SizeDisplay == "未提供" && unknown.VersionDisplay == "未提供", "未知空间和版本不显示为零");

            var fixture = Path.Combine(AppContext.BaseDirectory, "read-only-fixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(fixture, "nested"));
            var first = Path.Combine(fixture, "one.bin");
            File.WriteAllBytes(first, new byte[2048]);
            File.WriteAllBytes(Path.Combine(fixture, "nested", "two.bin"), new byte[3072]);
            var measured = DirectorySize.Measure(fixture, CancellationToken.None);
            Check(measured.Bytes == 5120 && measured.Files == 2 && measured.Skipped == 0, "只读目录测量正确统计两层目录的 5120 字节");
            Check(new FileInfo(first).Length == 2048 && Directory.GetFiles(fixture, "*", SearchOption.AllDirectories).Length == 2,
                "测量完成后测试文件内容和数量未变");
            var cancelled = false;
            try { DirectorySize.Measure(fixture, new CancellationToken(true)); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "目录测量支持取消");
            var sharedRootRejected = false;
            try { DirectorySize.Measure(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), CancellationToken.None); }
            catch (ArgumentException) { sharedRootRejected = true; }
            Check(sharedRootRejected, "拒绝把整个 Program Files 目录当作单个应用测量");
            var driveRootRejected = false;
            try { DirectorySize.Measure(Path.GetPathRoot(fixture), CancellationToken.None); }
            catch (ArgumentException) { driveRootRejected = true; }
            Check(driveRootRejected, "拒绝测量整个盘符根目录，且不会把根目录误解析为当前目录");
            small.SetMeasurement(new DirectoryMeasurement(5120, 2, 1));
            Check(small.SizeBytes == 5120 && small.SizeDisplay.StartsWith("≥ ") && small.SizeSource.Contains("部分扫描"),
                "包含跳过条目的测量值明确标记为下限");
            Check(typeof(UninstallTools.UninstallToolsGlobalConfig).GetProperty("FileDeletionHandler").GetValue(null) == null,
                "只读界面未配置文件删除处理器");
            CheckPortableScanning(Check);
            CheckWorkbench(window, Check);
            CheckReleaseReadiness(window, Check);
            UninstallPreflightChecks.Run(window, Check);
            Progress("开始：专用卸载实验室检查"); UninstallLabChecks.Run(Check);
            Progress("开始：桌面 EXE 正式卸载流程检查"); UninstallWorkflowChecks.Run(window, Check);
            Progress("开始：专用 x86/x64 MSI 检查"); MsiFixtureChecks.Run(Check);
            Progress("开始：专用 MSIX 检查"); MsixFixtureChecks.Run(Check);
            Progress("开始：注册表只读预览与备份检查"); UninstallWorkflowChecks.RunRegistry(window, Check);
            window.Close();
            application.Shutdown();
            Write(resultPath, new { passed = checks.Count, checks });
            return 0;
        }
        catch (Exception ex) { Progress("失败：" + ex.GetType().Name); Write(resultPath, new { passed = checks.Count, checks, error = ex.ToString() }); return 1; }
    }

    public static int ReadInstalled(string resultPath)
    {
        try
        {
            var result = Task.Run(() => ReadOnlyCatalog.Load(CancellationToken.None)).GetAwaiter().GetResult();
            Write(resultPath, new
            {
                count = result.Apps.Count,
                desktop = result.Apps.Count(x => x.Source == "桌面应用"),
                store = result.Apps.Count(x => x.Source != "桌面应用"),
                withPath = result.Apps.Count(x => x.InstallPath.Length > 0),
                withReportedSize = result.Apps.Count(x => x.ReportedBytes.HasValue),
                withDescription = result.Apps.Count(x => !string.IsNullOrWhiteSpace(x.Description)),
                officialDescriptions = result.Apps.Count(x => x.DescriptionUrl.Length > 0),
                awaitingPurposeVerification = result.Apps.Count(x => x.DescriptionSource.Contains("用途待核实")),
                withUninstallRegistration = result.Apps.Count(x => x.UninstallRegistration != null),
                uninstallReadErrors = result.Apps.Count(x => x.UninstallRegistration?.ReadError.Length > 0),
                withStandardUninstallCommand = result.Apps.Count(x => !string.IsNullOrWhiteSpace(x.UninstallRegistration?.Command)),
                quietOnlyUninstallEntries = result.Apps.Count(x => x.UninstallRegistration is { } entry
                    && string.IsNullOrWhiteSpace(entry.Command) && !string.IsNullOrWhiteSpace(entry.QuietCommand)),
                warnings = result.Warnings,
                deletionHandlerConfigured = UninstallTools.UninstallToolsGlobalConfig.FileDeletionHandler != null
            });
            return result.Apps.Count > 0 ? 0 : 1;
        }
        catch (Exception ex) { Write(resultPath, new { error = ex.ToString() }); return 1; }
    }

    private static void Write(string path, object value) => File.WriteAllText(path,
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    private static void CheckPortableScanning(Action<bool, string> check)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "portable-fixtures", Guid.NewGuid().ToString("N"));
        var a = Path.Combine(fixture, "drive-a");
        var b = Path.Combine(fixture, "drive-b");
        var app = Path.Combine(a, "PortableWidget");
        var registered = Path.Combine(a, "RegisteredApp");
        var excluded = Path.Combine(a, "IgnoredApp");
        var loose = Path.Combine(a, "Tools");
        var data = Path.Combine(b, "BaiduNetdiskDownload");
        var temporary = new[] { "BaiduNetdiskTmp", "BaiduNetdisk_Temp", "BaiduNetdisk临时", "BaiduNetdisk临时文件" }.Select(name => Path.Combine(b, name)).ToArray();
        var unrelated = Path.Combine(b, "NotBaiduNetdiskTmp");
        var installerCache = Path.Combine(a, "Package Cache", "fixture-package", "BaiduNetdisk");
        var cacheLookalike = Path.Combine(a, "Package Cache Tools");
        foreach (var directory in new[] { app, registered, excluded, loose, data, unrelated, installerCache, cacheLookalike, Path.Combine(app, "helpers"), Path.Combine(a, "Installers"), Path.Combine(a, "NotAnApp") }.Concat(temporary)) Directory.CreateDirectory(directory);
        var source = Path.Combine(AppContext.BaseDirectory, "AppInventory.exe");
        foreach (var target in new[] { Path.Combine(app, "Widget.exe"), Path.Combine(app, "updater.exe"), Path.Combine(app, "helpers", "worker.exe"),
                     Path.Combine(registered, "Registered.exe"), Path.Combine(excluded, "Ignored.exe"), Path.Combine(loose, "Alpha.exe"), Path.Combine(loose, "Beta.exe"), Path.Combine(a, "Installers", "setup.exe"),
                     Path.Combine(installerCache, "CachedProgram.exe"), Path.Combine(cacheLookalike, "PortableTool.exe") }) File.Copy(source, target);
        File.WriteAllText(Path.Combine(a, "NotAnApp", "pretend.exe"), "这不是一个可执行文件");
        File.WriteAllText(Path.Combine(data, "user-file.txt"), "必须保留的个人文件测试数据");
        foreach (var directory in temporary) File.WriteAllBytes(Path.Combine(directory, "retained.bin"), new byte[128]);
        var installed = new List<AppRow>
        {
            new() { Name = "注册测试软件", InstallPath = registered },
            new() { Name = "百度网盘", InstallPath = @"Z:\Applications\BaiduNetdisk" }
        };
        var scan = PortableScanner.Scan(new ScanScope(new[] { a, b, app }, new[] { excluded }), installed, null, CancellationToken.None);
        check(scan.Roots.Count == 2, "扫描范围自动去除重叠的子目录");
        check(scan.Candidates.Count(row => row.InstallPath == app) == 1, "程序主目录与辅助程序合并为一个便携候选");
        check(scan.Candidates.Count(row => row.InstallPath == loose && row.IsSingleFileCandidate) == 2, "工具容器中的两个独立程序分别识别为单文件候选");
        check(!scan.Candidates.Any(row => PortableScanner.IsWithin(row.InstallPath, Path.Combine(a, "Package Cache"))), "安装缓存中的有效 EXE 不新增为便携候选");
        check(scan.Candidates.Any(row => row.InstallPath == cacheLookalike), "安装缓存判断使用完整目录名称，不误过滤相似名称的程序目录");
        var cacheScan = PortableScanner.Scan(new ScanScope(new[] { installerCache }, Array.Empty<string>()), installed, null, CancellationToken.None);
        check(cacheScan.Candidates.Count == 0 && cacheScan.Folders.ContainsKey("baidunetdisk"), "直接扫描安装缓存子目录也不识别便携软件，同时保留关联索引");
        check(!scan.Candidates.Any(row => row.InstallPath == registered), "已注册应用目录不重复识别为便携软件");
        check(!scan.Candidates.Any(row => row.InstallPath == excluded), "排除目录及其程序不进入扫描结果");
        check(!scan.Candidates.Any(row => row.InstallPath.EndsWith("NotAnApp") || row.InstallPath.EndsWith("Installers")), "假 EXE 和单独安装器不识别为便携软件");
        check(!PortableScanner.IsMainExecutableName("BaiduYunguanjia Installer Application")
              && !PortableScanner.IsMainExecutableName("软件安装向导"), "安装器的产品元数据同样参与过滤，避免名称不含 setup 的安装包误报");
        var expectedSize = Directory.GetFiles(app, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
        check(scan.Candidates.Single(row => row.InstallPath == app).SizeBytes == expectedSize, "便携软件空间包括同目录辅助文件且只计一次");
        var merged = PortableScanner.Merge(installed, scan);
        check(merged.Count == installed.Count + scan.Candidates.Count, "便携软件与原有安装记录合并，保留原有记录");
        var baidu = merged.Single(row => row.Name == "百度网盘");
        check(baidu.RelatedLocations.Any(location => location.Path == data && location.OtherDrive && location.Kind.Contains("数据")), "可通过百度网盘别名发现其他盘下载目录候选");
        check(temporary.All(path => baidu.RelatedLocations.Any(location => location.Path == path && location.OtherDrive
            && location.Kind == "临时 / 缓存目录候选" && location.Bytes == 128)), "Tmp、Temp 和中文临时目录可关联，并显示实际大小及候选类别");
        check(!baidu.RelatedLocations.Any(location => location.Path == unrelated), "临时目录关联遵守完整名称匹配，不接收仅包含软件名的无关目录");
        check(baidu.RelatedLocations.Any(location => location.Path == installerCache && location.Bytes == new FileInfo(source).Length
            && location.Kind == "名称匹配目录候选"), "过滤安装缓存中的程序候选后仍保留目录关联和空间统计");
        check(temporary.All(path => new FileInfo(Path.Combine(path, "retained.bin")).Length == 128)
            && File.Exists(Path.Combine(installerCache, "CachedProgram.exe")), "新增的临时目录及安装缓存检查没有删除或改写测试数据");
        check(File.ReadAllText(Path.Combine(data, "user-file.txt")) == "必须保留的个人文件测试数据", "关联位置检查保留所有测试用户数据");
        var deduplicated = PortableScanner.Merge(installed.Concat(new[] { new AppRow { Name = "补充安装记录", InstallPath = app } }).ToList(), scan);
        check(!deduplicated.Any(row => row.IsPortableCandidate && row.InstallPath == app), "新安装记录覆盖同一路径便携候选，避免重复");
        check(!PortableScanner.IsWithin(@"D:\tools2", @"D:\tools"), "目录包含判断遵守路径边界，不误排除同名前缀目录");
        var cancelled = false;
        try { PortableScanner.Scan(new ScanScope(new[] { a }, Array.Empty<string>()), installed, null, new CancellationToken(true)); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "便携扫描支持取消，不产生本轮合并结果");
        SoftwareDescriptions.Apply(baidu);
        check(baidu.Description.Contains("同步") && baidu.DescriptionUrl == "https://yun.baidu.com/disk/help", "百度网盘说明有已核对的官方来源");
        var unknown = new AppRow { Name = "没有已知用途的测试应用" };
        SoftwareDescriptions.Apply(unknown);
        check(unknown.Description.Length > 0 && unknown.DescriptionSource.Contains("待核实"), "未知软件也提供事实说明，未核实用途明确标注");
        check(SoftwareDescriptions.Find("GitHub CLI")?.Url != "https://git-scm.com/" && SoftwareDescriptions.Find("CodeArtsSpace") == null, "软件说明不会把 GitHub CLI 当 Git 或把 CodeArts 当 VS Code");
        check(merged.All(row => row.RelatedSummary != "未扫描"), "每条软件记录均标明关联检查状态及扫描范围");
    }

    private static void CheckReleaseReadiness(MainWindow window, Action<bool, string> check)
    {
        bool Rejected(Action action)
        {
            try { action(); return false; }
            catch (InvalidDataException) { return true; }
        }
        var installed = new AppRow { Name = "刷新测量验证", Version = "1.0", InstallPath = @"D:\FixtureApp" };
        window.UpdateInstalled(new() { installed });
        var scan = new PortableScanResult(new(), new(StringComparer.OrdinalIgnoreCase), 0, 0, 0, new() { @"D:\" });
        window.UpdateScan(scan, new(scan.Roots, Array.Empty<string>()));
        installed.SetMeasurement(new(4096, 2, 0));
        var refreshed = new AppRow { Name = installed.Name, Version = installed.Version, InstallPath = installed.InstallPath, Publisher = "新发布者" };
        window.UpdateInstalled(new() { refreshed });
        check(refreshed.SizeBytes == 4096 && refreshed.Publisher == "新发布者" && refreshed.RelatedCount == 0,
            "完成目录扫描后刷新安装记录仍保留手动测量，并更新安装元数据与关联状态");
        var updated = new AppRow { Name = installed.Name, Version = "2.0", InstallPath = installed.InstallPath };
        window.UpdateInstalled(new() { updated });
        check(updated.Measurement == null, "有目录扫描快照时版本更新同样不沿用旧测量");

        var fixture = Path.Combine(AppContext.BaseDirectory, "read-only-fixtures", Guid.NewGuid().ToString("N"));
        var jsonPath = Path.Combine(fixture, "limited.json");
        CatalogStorage.Save(jsonPath, "原有完整数据");
        check(Rejected(() => CatalogStorage.Save(jsonPath, new string('a', 32 * 1024 * 1024)))
            && CatalogStorage.Read<string>(jsonPath) == "原有完整数据", "超过 JSON 读取上限的写入被拒绝，原文件仍完整可读");
        var invalid = new AppRow { Name = "无效大小", ReportedBytes = -1 };
        var snapshot = CatalogStorage.Capture(new[] { invalid }, null, Array.Empty<string>());
        check(Rejected(() => CatalogStorage.Restore(snapshot)), "历史快照中的负数系统空间值被拒绝");
        var valid = new AppRow { Name = "无效测量验证" };
        snapshot = CatalogStorage.Capture(new[] { valid }, null, Array.Empty<string>());
        check(new[] { new DirectoryMeasurement(-1, 0, 0), new(0, -1, 0), new(0, 0, -1) }.All(measurement =>
            Rejected(() => CatalogStorage.Restore(snapshot with { Apps = new() { snapshot.Apps[0] with { Measurement = measurement } } }))),
            "历史快照中的负数测量字节数、文件数和跳过数均被拒绝");
    }

    private static void CheckWorkbench(MainWindow window, Action<bool, string> check)
    {
        var desktop = new AppRow { Name = "桌面 Alpha", Version = "2.0", InstallPath = @"D:\Apps\Alpha", Publisher = "原发布者" };
        var store = new AppRow { Name = "商店 Beta", Version = "10.0", Source = "商店 / MSIX 应用" };
        var portable = new AppRow { Name = "便携 Gamma", Version = "15.beta", Source = "便携 / 未注册候选", IsPortableCandidate = true,
            InstallPath = @"E:\Portable\Gamma", ExecutablePath = @"E:\Portable\Gamma\gamma.exe", IsSingleFileCandidate = true };
        SoftwareDescriptions.Apply(desktop);
        store.SetDescription("商店应用的自报用途说明，用于筛选验证。", "应用自身说明");
        SoftwareDescriptions.Apply(portable);
        desktop.SetRelated(new[] { new RelatedLocation(@"E:\AlphaTmp", 128, true, "临时目录候选", "测试依据", true) }, "已检查合成范围。");
        desktop.SetMeasurement(new DirectoryMeasurement(1024, 2, 1));
        portable.SetMeasurement(new DirectoryMeasurement(512, 1, 0), true);
        window.SetApps(new List<AppRow> { desktop, store, portable });
        var grid = (DataGrid)window.FindName("AppsGrid");
        var view = (ListCollectionView)grid.ItemsSource;
        var source = (ComboBox)window.FindName("SourceFilter");
        var related = (CheckBox)window.FindName("RelatedOnly");
        var unknown = (CheckBox)window.FindName("UnknownOnly");
        var search = (TextBox)window.FindName("SearchBox");
        source.SelectedIndex = 2;
        check(view.Count == 1 && ReferenceEquals(view.GetItemAt(0), store), "来源筛选可单独显示商店应用");
        source.SelectedIndex = 3;
        check(view.Count == 1 && ReferenceEquals(view.GetItemAt(0), portable), "来源筛选可单独显示便携候选");
        source.SelectedIndex = 0;
        related.IsChecked = true;
        check(view.Count == 1 && ReferenceEquals(view.GetItemAt(0), desktop), "仅有关联位置筛选不包含未扫描或没有候选的记录");
        related.IsChecked = false;
        unknown.IsChecked = true;
        check(view.Count == 2, "用途待核实筛选保留明确未知的记录");
        unknown.IsChecked = false;
        search.Text = "E:\\AlphaTmp";
        check(view.Count == 1 && ReferenceEquals(view.GetItemAt(0), desktop), "搜索可匹配主安装目录之外的关联路径");
        search.Text = "gamma.exe";
        check(view.Count == 1 && ReferenceEquals(view.GetItemAt(0), portable), "搜索可匹配主程序文件名");
        search.Clear();
        window.Sort(nameof(AppRow.RelatedCount), true);
        check(ReferenceEquals(view.GetItemAt(0), desktop), "关联数量可按实际数值排序，未扫描记录置后");
        var versions = new[] { desktop, store, portable, new AppRow { Name = "自定义版本", Version = "custom" }, new AppRow { Name = "未知版本" } };
        foreach (var descending in new[] { false, true })
        {
            var comparer = new AppRowComparer(nameof(AppRow.Version), descending);
            check(versions.All(a => versions.All(b => versions.All(c => comparer.Compare(a, b) > 0 || comparer.Compare(b, c) > 0 || comparer.Compare(a, c) <= 0))),
                "混合数字与自定义版本排序保持传递性：" + (descending ? "降序" : "升序"));
            check(versions.Take(4).All(row => comparer.Compare(row, versions[4]) < 0), "未知版本始终位于末尾：" + (descending ? "降序" : "升序"));
        }

        var fixture = Path.Combine(AppContext.BaseDirectory, "read-only-fixtures", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(fixture, "settings.json");
        CatalogStorage.Save(settingsPath, new InventorySettings(false, new() { @"D:\Tools", @"D:\Tools\Child" }, new() { @"D:\Private" }));
        var settings = CatalogStorage.LoadSettings(settingsPath);
        check(!settings.AllFixed && settings.Roots.Count == 1 && settings.Exclusions.Single() == @"D:\Private", "扫描设置可保存恢复并规范化重叠范围");
        CatalogStorage.Save(settingsPath, settings with { AllFixed = true });
        check(CatalogStorage.LoadSettings(settingsPath).AllFixed, "重新保存配置可完整替换旧值");
        var snapshotPath = Path.Combine(fixture, "snapshot.json");
        var time = DateTimeOffset.Now;
        var snapshot = CatalogStorage.Capture(new[] { desktop, store, portable }, time, new[] { @"D:\", @"E:\" }, new[] { @"D:\Private" });
        CatalogStorage.Save(snapshotPath, snapshot);
        var loaded = CatalogStorage.Read<SavedCatalog>(snapshotPath);
        var restored = CatalogStorage.Restore(loaded);
        check(restored.Count == 3 && loaded.ScannedAt == time && loaded.Exclusions.Single() == @"D:\Private", "本地快照保留记录、扫描时间和实际排除范围");
        check(restored[0].SizeBytes == 1024 && restored[0].SizeDisplay.StartsWith("≥ ") && restored[0].RelatedLocations.Single().Partial,
            "快照恢复后测量下限及关联大小的不完整标记保留");
        check(restored[0].RelatedLocations.Single().Path == @"E:\AlphaTmp" && !restored[1].RelatedCount.HasValue,
            "快照区分已扫描记录与未扫描记录");
        check(restored[1].Description == store.Description && restored[2].SizeSource.StartsWith("单个可执行文件"), "快照保留说明来源及单文件空间依据");
        var fresh = new AppRow { Name = desktop.Name, Version = desktop.Version, InstallPath = desktop.InstallPath, Publisher = "更新后的发布者" };
        var refreshed = CatalogStorage.MergeHistory(new() { fresh }, restored);
        check(refreshed.Count == 2 && fresh.Publisher == "更新后的发布者" && fresh.RelatedLocations.Count == 1, "刷新安装记录保留匹配历史关联及便携记录，同时使用最新安装元数据");
        var updated = new AppRow { Name = desktop.Name, Version = "3.0", InstallPath = desktop.InstallPath };
        CatalogStorage.MergeHistory(new() { updated }, restored);
        check(!updated.RelatedCount.HasValue && !updated.SizeBytes.HasValue, "版本改变后的记录不套用旧版本测量和关联");
        var registered = new AppRow { Name = portable.Name, InstallPath = portable.InstallPath };
        check(CatalogStorage.MergeHistory(new() { registered }, restored).Count == 1, "历史便携记录出现安装记录时不重复显示");
        var invalidSettings = Path.Combine(fixture, "invalid-settings.json");
        CatalogStorage.Save(invalidSettings, new InventorySettings(true, new() { "relative" }, new()));
        var rejected = false;
        try { CatalogStorage.LoadSettings(invalidSettings); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "无效的相对扫描目录配置被拒绝");
        rejected = false;
        try { CatalogStorage.Restore(loaded with { Schema = 99 }); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "不支持的历史快照版本被拒绝");
        var corruptPath = Path.Combine(fixture, "corrupt.json");
        File.WriteAllText(corruptPath, "{broken");
        rejected = false;
        try { CatalogStorage.Read<SavedCatalog>(corruptPath); } catch (JsonException) { rejected = true; }
        check(rejected, "损坏 JSON 被报告为读取失败，不当作有效空快照");
        var dangerous = new AppRow { Name = "=1+1", Publisher = "@SUM(1)", InstallPath = "D:\\中文目录,含逗号" };
        dangerous.SetDescription("第一行\n第二行含\"引号\"", "测试说明");
        var csv = CatalogStorage.Csv(new[] { dangerous }, time);
        check(csv.Contains("\"'=1+1\"") && csv.Contains("\"'@SUM(1)\""), "CSV 转义可能被电子表格执行的软件自报公式");
        check(csv.Contains("\"D:\\中文目录,含逗号\"") && csv.Contains("第一行\n第二行含\"\"引号\"\""), "CSV 正确引用中文路径、逗号、换行和引号");
        var csvPath = Path.Combine(fixture, "export.csv");
        CatalogStorage.WriteText(csvPath, csv);
        check(File.ReadAllBytes(csvPath).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }) && File.ReadAllText(csvPath) == csv,
            "CSV 使用带 BOM 的 UTF-8 保存，中文内容完整");
        source.SelectedIndex = 2;
        var selectedJsonPath = Path.Combine(fixture, "selected.json");
        var exported = window.WriteExportAsync(selectedJsonPath, true).GetAwaiter().GetResult();
        var selectedReport = CatalogStorage.Read<SavedCatalog>(selectedJsonPath);
        check(exported == 1 && selectedReport.Apps.Single().Data.Name == store.Name, "界面导出通路只写入当前来源筛选后的记录");
        source.SelectedIndex = 0;
        window.Sort(nameof(AppRow.Name), true);
        var selectedCsvPath = Path.Combine(fixture, "selected.csv");
        exported = window.WriteExportAsync(selectedCsvPath, false).GetAwaiter().GetResult();
        var exportedNames = File.ReadAllLines(selectedCsvPath).Skip(1).Select(line => line.Split(',')[0].Trim('"')).ToArray();
        check(exported == view.Count && exportedNames.SequenceEqual(view.Cast<AppRow>().Select(row => row.Name)), "界面 CSV 导出通路保留当前显示顺序和记录数量");
    }
}
