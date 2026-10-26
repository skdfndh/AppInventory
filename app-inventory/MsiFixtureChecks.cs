using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace AppInventory;

internal static class MsiFixtureChecks
{
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiOpenDatabase(string path, IntPtr mode, out uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiDatabaseOpenView(uint database, string sql, out uint view);
    [DllImport("msi.dll")] private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(uint handle);
    [DllImport("msi.dll")] private static extern uint MsiDatabaseCommit(uint database);
    [DllImport("msi.dll")] private static extern uint MsiCreateRecord(uint fields);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiRecordSetString(uint record, uint field, string text);
    [DllImport("msi.dll")] private static extern uint MsiRecordSetInteger(uint record, uint field, int number);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiGetSummaryInformation(uint database, string path, uint count, out uint summary);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiSummaryInfoSetProperty(uint summary, uint property, uint type, int number, IntPtr time, string text);
    [DllImport("msi.dll")] private static extern uint MsiSummaryInfoPersist(uint summary);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] private static extern uint MsiGetProductInfo(string product, string property, StringBuilder value, ref uint length);

    private static void Require(uint error, string operation)
    {
        if (error != 0) throw new IOException("专用 MSI 夹具失败：" + operation + "，代码 " + error);
    }

    internal static void Run(Action<bool, string> check)
    {
        foreach (var x64 in new[] { false, true }) RunCase((condition, name) => check(condition, name + (x64 ? "（x64）" : "（x86）")), x64, false);
        // 托管 CI 的临时 Windows 虚拟机补充机器级验证，本机默认不申请管理员权限。
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("临时 CI 虚拟机没有机器级测试权限，未自动提权。");
            foreach (var x64 in new[] { false, true }) RunCase((condition, name) => check(condition, name + (x64 ? "（机器级 x64）" : "（机器级 x86）")), x64, true);
        }
    }

    private static void RunCase(Action<bool, string> check, bool x64, bool allUsers)
    {
        var token = Guid.NewGuid().ToString("N");
        var product = Guid.NewGuid().ToString("B").ToUpperInvariant();
        if (InstallerNative.MsiQueryProductState(product) != -1) throw new InvalidOperationException("新建 MSI 产品标识已存在，拒绝继续。");
        var root = Path.Combine(AppContext.BaseDirectory, "read-only-fixtures", "msi-uninstall", token);
        var source = Path.Combine(root, "source");
        var installed = Path.Combine(root, "installed");
        UninstallLab.CheckAncestors(root);
        if (Directory.Exists(root)) throw new IOException("专用 MSI 测试目录已存在，拒绝覆盖。");
        Directory.CreateDirectory(source);
        File.Copy(UninstallLab.SourceBinary, Path.Combine(source, "TestApplication.exe"));
        File.WriteAllText(Path.Combine(root, "keep.txt"), "MSI 专用数据哨兵 " + token);
        var package = Path.Combine(source, "AppInventoryFixture.msi");
        var name = "AppInventory Dedicated MSI Fixture " + token[..8];
        Build(package, installed, name, product, x64, allUsers);
        var installer = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        int Invoke(string arguments)
        {
            using var process = Process.Start(new ProcessStartInfo(installer, arguments) { UseShellExecute = false, CreateNoWindow = true })
                ?? throw new IOException("无法启动专用 MSI 安装测试。");
            if (!process.WaitForExit(60000)) throw new IOException("专用 MSI 安装超过 60 秒；未终止 Windows Installer，请查看夹具日志。");
            return process.ExitCode;
        }
        try
        {
            var exit = Invoke("/i \"" + package + "\" /qn /norestart REBOOT=ReallySuppress /l*v \"" + Path.Combine(root, "install.log") + "\"");
            check(exit == 0 && InstallerNative.MsiQueryProductState(product) == 5 && File.Exists(Path.Combine(installed, "TestApplication.exe")),
                "Windows Installer 实际安装随机产品代码的专用" + (allUsers ? "计算机级" : "用户级") + " MSI，不修改现有产品");
            var assignment = new StringBuilder(8); uint length = 8;
            Require(MsiGetProductInfo(product, "AssignmentType", assignment, ref length), "读取专用产品安装上下文");
            check(assignment.ToString() == (allUsers ? "1" : "0"), "系统报告的 MSI 安装上下文与本次用户级/机器级测试请求一致");
            InstalledSnapshot snapshot = null;
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                var candidate = new InstallIdentity(hive, view, InstallIdentity.Root + product).Read();
                if (candidate?.Name == name) { snapshot = candidate; break; }
            }
            if (snapshot == null) throw new IOException("没有找到专用 MSI 的精确当前用户安装记录。");
            var fullPath = (snapshot.Identity.Hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER\\" : "HKEY_LOCAL_MACHINE\\")
                + (snapshot.Identity.View == RegistryView.Registry32 ? @"Software\Wow6432Node\" + snapshot.Identity.SubKey[@"Software\".Length..] : snapshot.Identity.SubKey);
            var row = new AppRow { Name = name, Version = snapshot.Version, InstallPath = installed, ExecutablePath = Path.Combine(installed, "TestApplication.exe"),
                UninstallRegistration = new(fullPath, snapshot.Command, "", "Msiexec", false) };
            var workflow = UninstallWorkflow.Prepare(row, true);
            var result = workflow.ExecuteAsync(name, true, false, default).GetAwaiter().GetResult();
            check(result.Status == UninstallStatus.Verified && InstallerNative.MsiQueryProductState(product) == -1
                && !File.Exists(row.ExecutablePath) && snapshot.Identity.Read() == null,
                "通过正式流程实际卸载专用 MSI，产品状态、精确安装记录和程序文件一致消失");
            check(File.ReadAllText(Path.Combine(root, "keep.txt")) == "MSI 专用数据哨兵 " + token, "专用 MSI 卸载保留安装目录外的数据哨兵");
        }
        finally
        {
            // 只回收本检查刚创建的随机产品；不按名称或全盘枚举卸载。
            if (InstallerNative.MsiQueryProductState(product) == 5)
                Invoke("/x " + product + " /qn /norestart REBOOT=ReallySuppress /l*v \"" + Path.Combine(root, "fixture-cleanup.log") + "\"");
        }
    }

    private static void Build(string package, string installed, string name, string product, bool x64, bool allUsers)
    {
        Require(MsiOpenDatabase(package, new IntPtr(3), out var database), "创建数据库");
        try
        {
            void Sql(string sql, params object[] values)
            {
                Require(MsiDatabaseOpenView(database, sql, out var view), "SQL 预备：" + sql);
                var record = values.Length == 0 ? 0 : MsiCreateRecord((uint)values.Length);
                try
                {
                    for (var index = 0; index < values.Length; index++)
                        Require(values[index] is int number ? MsiRecordSetInteger(record, (uint)index + 1, number)
                            : MsiRecordSetString(record, (uint)index + 1, (string)values[index]), "绑定 MSI 数据");
                    Require(MsiViewExecute(view, record), "SQL 执行：" + sql);
                }
                finally { if (record != 0) MsiCloseHandle(record); MsiCloseHandle(view); }
            }
            Sql("CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)");
            foreach (var pair in new Dictionary<string, string> { ["ProductCode"] = product, ["ProductName"] = name, ["ProductVersion"] = "1.0.0", ["Manufacturer"] = "AppInventory contributors",
                ["ProductLanguage"] = "1033", ["UpgradeCode"] = Guid.NewGuid().ToString("B"), ["INSTALLDIR"] = installed + "\\", ["ARPINSTALLLOCATION"] = installed, ["REBOOT"] = "ReallySuppress" })
                Sql("INSERT INTO `Property` (`Property`,`Value`) VALUES (?,?)", pair.Key, pair.Value);
            if (allUsers) Sql("INSERT INTO `Property` (`Property`,`Value`) VALUES (?,?)", "ALLUSERS", "1");
            Sql("CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)");
            Sql("INSERT INTO `Directory` (`Directory`,`DefaultDir`) VALUES (?,?)", "TARGETDIR", "SourceDir");
            Sql("INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (?,?,?)", "INSTALLDIR", "TARGETDIR", ".");
            Sql("CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)");
            Sql("INSERT INTO `Component` (`Component`,`ComponentId`,`Directory_`,`Attributes`,`KeyPath`) VALUES (?,?,?,?,?)", "MainComponent", Guid.NewGuid().ToString("B"), "INSTALLDIR", x64 ? 256 : 0, "MainFile");
            Sql("CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)");
            Sql("INSERT INTO `Feature` (`Feature`,`Title`,`Display`,`Level`,`Directory_`,`Attributes`) VALUES (?,?,?,?,?,?)", "Main", "Dedicated Fixture", 1, 1, "INSTALLDIR", 0);
            Sql("CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`,`Component_`)");
            Sql("INSERT INTO `FeatureComponents` (`Feature_`,`Component_`) VALUES (?,?)", "Main", "MainComponent");
            Sql("CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` SHORT NOT NULL PRIMARY KEY `File`)");
            Sql("INSERT INTO `File` (`File`,`Component_`,`FileName`,`FileSize`,`Version`,`Attributes`,`Sequence`) VALUES (?,?,?,?,?,?,?)",
                "MainFile", "MainComponent", "test.exe|TestApplication.exe", checked((int)new FileInfo(Path.Combine(Path.GetDirectoryName(package)!, "TestApplication.exe")).Length), "1.0.0.0", 8192, 1);
            Sql("CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` SHORT NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)");
            Sql("INSERT INTO `Media` (`DiskId`,`LastSequence`) VALUES (?,?)", 1, 1);
            Sql("CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)");
            foreach (var pair in new Dictionary<string, int> { ["ValidateProductID"] = 700, ["CostInitialize"] = 800, ["FileCost"] = 900, ["CostFinalize"] = 1000,
                ["InstallValidate"] = 1400, ["InstallInitialize"] = 1500, ["ProcessComponents"] = 1600, ["UnpublishFeatures"] = 1800, ["RemoveFiles"] = 3500,
                ["InstallFiles"] = 4000, ["RegisterUser"] = 6000, ["RegisterProduct"] = 6100, ["PublishFeatures"] = 6300, ["PublishProduct"] = 6400, ["InstallFinalize"] = 6600 })
                Sql("INSERT INTO `InstallExecuteSequence` (`Action`,`Sequence`) VALUES (?,?)", pair.Key, pair.Value);
            Require(MsiGetSummaryInformation(database, null, 8, out var summary), "摘要");
            try
            {
                foreach (var pair in new Dictionary<uint, string> { [2] = "AppInventory Dedicated Fixture", [7] = x64 ? "x64;1033" : "Intel;1033", [9] = Guid.NewGuid().ToString("B"), [18] = "AppInventory" })
                    Require(MsiSummaryInfoSetProperty(summary, pair.Key, 30, 0, IntPtr.Zero, pair.Value), "摘要文本");
                Require(MsiSummaryInfoSetProperty(summary, 14, 3, 200, IntPtr.Zero, null), "版本摘要");
                Require(MsiSummaryInfoSetProperty(summary, 15, 3, 0, IntPtr.Zero, null), "源文件摘要");
                Require(MsiSummaryInfoPersist(summary), "保存摘要");
            }
            finally { MsiCloseHandle(summary); }
            Require(MsiDatabaseCommit(database), "提交数据库");
        }
        finally { MsiCloseHandle(database); }
    }
}
