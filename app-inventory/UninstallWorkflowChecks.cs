using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AppInventory;

internal static class UninstallWorkflowChecks
{
    private sealed class Fixture : IDisposable
    {
        internal string Token { get; } = Guid.NewGuid().ToString("N");
        internal string Root { get; }
        internal string DirectoryPath { get; }
        internal string MainPath => Path.Combine(DirectoryPath, "TestApplication.exe");
        internal string Uninstaller => Path.Combine(DirectoryPath, "RegisteredUninstaller.exe");
        internal string KeyPath => InstallIdentity.Root + "AppInventoryFixture-" + Token;
        internal string Command { get; }
        internal string ConfigParent => @"Software\AppInventoryResidue-" + Token;
        internal string ConfigPath => ConfigParent + "\\" + Row.Name;
        internal AppRow Row => new() { Name = "应用清单专用注册测试 " + Token[..8], Version = "1.0", InstallPath = DirectoryPath,
            ExecutablePath = MainPath, UninstallRegistration = new("HKEY_CURRENT_USER\\" + KeyPath, Command, "", "Unknown", false) };

        internal Fixture(string scenario)
        {
            Root = Path.Combine(AppContext.BaseDirectory, "read-only-fixtures", "中文 " + Token);
            DirectoryPath = Path.Combine(Root, "AppInventoryFixture-" + Token);
            Command = "\"" + Uninstaller + "\" " + scenario;
            var source = Path.Combine(AppContext.BaseDirectory, "测试组件", "RegisteredUninstaller.exe");
            var approved = typeof(UninstallWorkflow).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == "RegisteredFixtureSHA256").Value;
            if (UninstallFiles.Hash(source) != approved) throw new InvalidDataException("注册测试组件摘要不匹配。");
            UninstallLab.CheckAncestors(Root);
            if (Directory.Exists(Root)) throw new IOException("测试目录已存在，拒绝覆盖。");
            Directory.CreateDirectory(DirectoryPath);
            File.Copy(source, Uninstaller);
            File.Copy(UninstallLab.SourceBinary, MainPath);
            File.WriteAllText(Path.Combine(DirectoryPath, "fixture-token.txt"), Token);
            File.WriteAllText(Path.Combine(DirectoryPath, "cache.dat"), "专用缓存 " + Token);
            File.WriteAllText(Path.Combine(Root, "keep.txt"), "必须保留 " + Token);
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            if (hive.OpenSubKey(KeyPath) != null) throw new IOException("专用测试安装记录已存在。");
            using var key = hive.CreateSubKey(KeyPath, true);
            key.SetValue("DisplayName", Row.Name); key.SetValue("DisplayVersion", "1.0"); key.SetValue("InstallLocation", DirectoryPath);
            key.SetValue("DisplayIcon", MainPath); key.SetValue("UninstallString", Command); key.SetValue("AppInventoryFixtureToken", Token);
        }

        internal void Set(string name, object value)
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = hive.OpenSubKey(KeyPath, true) ?? throw new IOException("测试记录不存在。");
            key.SetValue(name, value);
        }

        internal void CreateConfig()
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using (var previous = root.OpenSubKey(ConfigParent, false)) if (previous != null) throw new IOException("专用配置父键已存在。");
            using (var parent = root.CreateSubKey(ConfigParent, true)) parent.SetValue("FixtureToken", Token);
            using (var key = root.CreateSubKey(ConfigPath, true))
            {
                key.SetValue("InstallDir", DirectoryPath);
                key.SetValue("SyntheticPrivateValue", "APPINVENTORY-SYNTHETIC-PRIVATE-" + Token);
                key.SetValue("Expandable", "%TEMP%\\AppInventory", RegistryValueKind.ExpandString);
                key.SetValue("Multi", new[] { "第一项", "second" }, RegistryValueKind.MultiString);
                key.SetValue("Binary", new byte[] { 0, 1, 254 }, RegistryValueKind.Binary);
                key.SetValue("None", new byte[] { 9, 0 }, RegistryValueKind.None);
                key.SetValue("Dword", -1, RegistryValueKind.DWord);
                key.SetValue("Qword", long.MaxValue, RegistryValueKind.QWord);
            }
            using (var child = root.CreateSubKey(ConfigPath + "\\Nested", true)) child.SetValue("Value", "嵌套合成值");
            foreach (var path in new[] { ConfigParent, ConfigPath, ConfigPath + "\\Nested" })
            {
                using var secured = root.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, System.Security.AccessControl.RegistryRights.FullControl);
                RegistryResiduals.SetFixtureOwner(secured);
            }
        }

        public void Dispose()
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using (var config = hive.OpenSubKey(ConfigParent, false))
            {
                if (config != null)
                {
                    if ((string)config.GetValue("FixtureToken") != Token) throw new InvalidOperationException("配置夹具标识改变，不清理。");
                    RegistryResiduals.Read("HKEY_CURRENT_USER\\" + ConfigParent);
                    hive.DeleteSubKeyTree(ConfigParent, false);
                }
            }
            using (var key = hive.OpenSubKey(KeyPath, false))
            {
                if (key == null) return;
                if ((string)key.GetValue("AppInventoryFixtureToken") != Token || (string)key.GetValue("InstallLocation") != DirectoryPath)
                    throw new InvalidOperationException("测试记录已改变，不移除不匹配的记录。");
            }
            hive.DeleteSubKey(KeyPath, false);
        }
    }

    internal static void RunRegistry(MainWindow owner, Action<bool, string> check)
    {
        bool Rejected(Action action)
        {
            try { action(); return false; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or CryptographicException or System.ComponentModel.Win32Exception) { return true; }
        }
        using var fixture = new Fixture("normal");
        fixture.CreateConfig();
        var path = "HKEY_CURRENT_USER\\" + fixture.ConfigPath;
        var before = RegistryResiduals.Read(path);
        var originalHash = RegistryResiduals.Hash(before);
        check(before.Values.Single(value => value.Name == "Expandable").Data == "%TEMP%\\AppInventory" && before.Children.Count == 1,
            "专用注册表备份读取原始环境变量、值类型和嵌套结构，不展开配置值");
        check(Rejected(() => RegistryResiduals.SubKey(@"HKEY_LOCAL_MACHINE\Software\Synthetic"))
            && Rejected(() => RegistryResiduals.SubKey(@"HKEY_CURRENT_USER\Software\Microsoft\Windows")), "注册表写入范围拒绝机器级与系统配置根");
        var task = UninstallWorkflow.Prepare(fixture.Row, true);
        check(Rejected(() => task.PreviewRegistry(default)), "卸载尚未核验时不能预览可隔离配置键");
        var result = task.ExecuteAsync(task.State.Plan.Name, true, false, default).GetAwaiter().GetResult();
        if (result.Status != UninstallStatus.Verified) throw new InvalidOperationException("配置测试应用未完成卸载。");
        var candidates = task.PreviewRegistry(default);
        var selected = candidates.SingleOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        check(selected != null && selected.Keys == 2 && selected.Values == 9 && RegistryResiduals.Hash(RegistryResiduals.Read(path)) == originalHash,
            "BCU 名称及路径证据定位专用配置键，预览没有修改任何值");
        var cancelled = false;
        try { task.PreviewRegistry(new CancellationToken(true)); } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && RegistryResiduals.Hash(RegistryResiduals.Read(path)) == originalHash, "取消配置预览不会修改原配置");
        var dialog = new UninstallWindow(owner, task);
        check(((ComboBox)dialog.FindName("RegistryCandidateBox")).SelectedItem == null
            && ((CheckBox)dialog.FindName("AcceptRegistry")).IsChecked != true
            && !((Button)dialog.FindName("IsolateRegistryButton")).IsEnabled, "配置候选默认不选择、不确认，界面不能直接隔离");
        dialog.Close();
        check(Rejected(() => task.IsolateRegistry(selected.Id, false)) && RegistryResiduals.Exists(path), "未单独确认配置候选时禁止隔离");
        using (var key = Registry.CurrentUser.OpenSubKey(fixture.ConfigPath, true)) key.SetValue("Changed", "后续合成变更");
        check(Rejected(() => task.IsolateRegistry(selected.Id, true)), "配置预览后值变化必须重新预览");
        selected = task.PreviewRegistry(default).Single(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && item.Status == "候选未选择");
        var hash = selected.Hash;
        task.IsolateRegistry(selected.Id, true);
        var quarantine = RegistryResiduals.Quarantine(path, selected.Id);
        check(!RegistryResiduals.Exists(path) && RegistryResiduals.Exists(quarantine)
            && RegistryResiduals.Hash(RegistryResiduals.Read(quarantine)) == hash, "选中配置加密备份后在同一父键内原子重命名，数据与权限不变");
        var report = Path.Combine(fixture.Root, "registry-report.json");
        task.Export(report);
        check(!File.ReadAllText(report).Contains("APPINVENTORY-SYNTHETIC-PRIVATE-") && !File.ReadAllText(report).Contains("SyntheticPrivateValue")
            && task.RegistryOperations.Single().Values == 10,
            "明文任务报告只含配置路径、计数与摘要，不包含配置值和备份内容");
        var opened = UninstallWorkflow.Open(task.State.Id, true);
        var vaultPath = Path.Combine(opened.Root, "registry-" + selected.Id + ".bin");
        var encryptedVault = File.ReadAllBytes(vaultPath);
        var damagedVault = encryptedVault.ToArray(); damagedVault[^1] ^= 1; File.WriteAllBytes(vaultPath, damagedVault);
        check(Rejected(() => opened.RestoreRegistry(selected.Id)) && RegistryResiduals.Exists(quarantine) && !RegistryResiduals.Exists(path),
            "加密配置备份被篡改时拒绝恢复，保留隔离副本");
        File.WriteAllBytes(vaultPath, encryptedVault);
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryResiduals.SubKey(quarantine), true)) key.SetValue("IsolationChanged", "合成变化");
        check(Rejected(() => opened.RestoreRegistry(selected.Id)) && !RegistryResiduals.Exists(path), "隔离配置内容变化后不覆盖恢复");
        using (var key = Registry.CurrentUser.OpenSubKey(RegistryResiduals.SubKey(quarantine), true)) key.DeleteValue("IsolationChanged");
        opened.RestoreRegistry(selected.Id);
        check(RegistryResiduals.Exists(path) && !RegistryResiduals.Exists(quarantine) && RegistryResiduals.Hash(RegistryResiduals.Read(path)) == hash,
            "重新载入任务后恢复配置键和值类型，保留原始权限");
        var next = opened.PreviewRegistry(default).Single(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && item.Status == "候选未选择");
        opened.IsolateRegistry(next.Id, true);
        using (var key = Registry.CurrentUser.CreateSubKey(fixture.ConfigPath, true)) key.SetValue("Conflict", "保留冲突数据");
        using (var secured = Registry.CurrentUser.OpenSubKey(fixture.ConfigPath, RegistryKeyPermissionCheck.ReadWriteSubTree, System.Security.AccessControl.RegistryRights.FullControl)) RegistryResiduals.SetFixtureOwner(secured);
        check(Rejected(() => opened.RestoreRegistry(next.Id)), "恢复配置时目标已存在则拒绝覆盖，保留新配置与隔离副本");
        var conflict = "HKEY_CURRENT_USER\\" + fixture.ConfigParent + "\\conflict-retained";
        RegistryResiduals.Rename(path, conflict);
        opened.RestoreRegistry(next.Id);
        next = opened.PreviewRegistry(default).Single(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && item.Status == "候选未选择");
        opened.IsolateRegistry(next.Id, true);
        quarantine = RegistryResiduals.Quarantine(path, next.Id);
        // 仅在本检查刚新建的随机配置父键内模拟隔离副本丢失，验证独立加密备份恢复。
        RegistryResiduals.Read(quarantine);
        Registry.CurrentUser.DeleteSubKeyTree(RegistryResiduals.SubKey(quarantine), false);
        opened.RestoreRegistry(next.Id);
        check(RegistryResiduals.Hash(RegistryResiduals.Read(path)) == next.Hash && RegistryResiduals.Exists(conflict),
            "隔离配置副本丢失时仍可从加密备份分阶段恢复，原有冲突数据保持完整");
        var resumeId = Guid.NewGuid().ToString("N");
        var temporary = path[..(path.LastIndexOf('\\') + 1)] + ".AppInventory-Restore-" + resumeId;
        var restoredTree = RegistryResiduals.Read(path);
        RegistryResiduals.Rename(path, temporary);
        RegistryResiduals.RestoreBackup(path, resumeId, restoredTree);
        check(RegistryResiduals.Hash(RegistryResiduals.Read(path)) == next.Hash && !RegistryResiduals.Exists(temporary),
            "完整暂存配置在恢复中断后可继续原子恢复，不重复覆盖");
    }

    internal static void Run(MainWindow owner, Action<bool, string> check)
    {
        bool Rejected(Action action)
        {
            try { action(); return false; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or CryptographicException or ArgumentException) { return true; }
        }
        WorkflowResult Execute(UninstallWorkflow workflow, CancellationToken token = default) => workflow.ExecuteAsync(workflow.State.Plan.Installation.Name, true, false, token).GetAwaiter().GetResult();
        var code = Guid.NewGuid().ToString("B");
        var msi = UninstallCommand.Parse("MsiExec.exe /I" + code);
        check(msi.Executable == Path.Combine(Environment.SystemDirectory, "msiexec.exe") && msi.Arguments.StartsWith("/x ") && msi.Arguments.Contains("REBOOT=ReallySuppress"),
            "MSI 维护入口转换为精确产品卸载并禁止自动重启，不保留任意附加参数");
        check(new[] { "cmd.exe /c anything", "powershell.exe -Command test", "relative.exe /u", "D:\\Unquoted App\\uninstall.exe /u",
            "\"D:\\bad.exe\"suffix", "\\\\server\\app.exe /u", "msiexec /x " + code + " PROPERTY=arbitrary", "msiexec /x {wrong}", "\"D:\\x.exe\"\n/u", "D:\\" + new string('a', 260) + ".exe" }
            .All(value => Rejected(() => UninstallCommand.Parse(value))), "脚本宿主、相对路径、未引用空格、网络路径、控制字符与 MSI 附加属性均被拒绝");
        var identity = InstallIdentity.Parse(@"HKEY_LOCAL_MACHINE\Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Synthetic");
        check(identity.View == RegistryView.Registry32 && identity.Hive == RegistryHive.LocalMachine && identity.SubKey == InstallIdentity.Root + "Synthetic",
            "安装身份包含注册表根、32 位视图与精确子键");
        check(Rejected(() => InstallIdentity.Parse(@"HKEY_CURRENT_USER\Software\Unrelated")), "非标准卸载键不能授权执行");
        check(UninstallWorkflow.EvaluateExit(3010, true).Status == UninstallStatus.RebootRequired
            && UninstallWorkflow.EvaluateExit(1641, true).Status == UninstallStatus.RebootRequired
            && UninstallWorkflow.EvaluateExit(1602, true).Status == UninstallStatus.Cancelled,
            "需要重启和用户取消单独处理，即使文件证据一致也不授权清理");

        using var success = new Fixture("normal");
        var workflow = UninstallWorkflow.Prepare(success.Row, true);
        check(Rejected(() => workflow.ExecuteAsync("错误名称", true, false, default).GetAwaiter().GetResult())
            && Rejected(() => workflow.ExecuteAsync(success.Row.Name, false, false, default).GetAwaiter().GetResult()) && File.Exists(success.MainPath),
            "未输入精确名称或未确认风险时不启动卸载器");
        var dialog = new UninstallWindow(owner, workflow);
        check(!((Button)dialog.FindName("RunWorkflowButton")).IsEnabled && !((CheckBox)dialog.FindName("AcceptResidual")).IsChecked.GetValueOrDefault(),
            "卸载窗口默认不能执行，残留目录默认不选择");
        ((CheckBox)dialog.FindName("AcceptRisk")).IsChecked = true;
        ((TextBox)dialog.FindName("ConfirmName")).Text = "不匹配的名称";
        check(!((Button)dialog.FindName("RunWorkflowButton")).IsEnabled, "风险确认后名称不一致仍不能从界面执行");
        ((TextBox)dialog.FindName("ConfirmName")).Text = workflow.State.Plan.Installation.Name;
        check(((Button)dialog.FindName("RunWorkflowButton")).IsEnabled && !((CheckBox)dialog.FindName("ElevateRequest")).IsChecked.GetValueOrDefault(),
            "界面在名称与风险确认一致后才开放执行，管理员权限默认关闭");
        dialog.Close();
        var result = Execute(workflow);
        check(result.Status == UninstallStatus.Verified && !File.Exists(success.MainPath) && File.Exists(Path.Combine(success.Root, "program.backup"))
            && workflow.State.Plan.Installation.Identity.Read() == null, "实际 EXE 卸载器移出专用程序并移除专用安装记录，独立核验通过");
        check(File.ReadAllText(Path.Combine(success.Root, "keep.txt")) == "必须保留 " + success.Token, "真实卸载流程保留安装目录外的测试用户数据");
        check(Rejected(() => Execute(workflow)) && Rejected(() => Execute(UninstallWorkflow.Open(workflow.State.Id, true))),
            "执行过或重新载入的历史计划不能再次启动卸载器");
        var files = workflow.PreviewResiduals(default);
        check(files.Any(file => file.RelativePath == "cache.dat") && files.All(file => !file.RelativePath.Contains("..")), "BCU 精确目录残留预览记录相对路径和完整文件摘要");
        check(Rejected(() => workflow.Isolate(false)) && Directory.Exists(success.DirectoryPath), "未单独确认残留时不移动目录");
        File.AppendAllText(Path.Combine(success.DirectoryPath, "cache.dat"), "changed");
        check(Rejected(() => workflow.Isolate(true)), "残留预览后内容改变会阻止隔离");
        workflow.PreviewResiduals(default);
        workflow.Isolate(true);
        check(!Directory.Exists(success.DirectoryPath) && Directory.Exists(workflow.Quarantine), "确认后仅同盘移动安装目录残留，不永久删除");
        var reopened = UninstallWorkflow.Open(workflow.State.Id, true);
        Directory.CreateDirectory(success.DirectoryPath);
        File.WriteAllText(Path.Combine(success.DirectoryPath, "new-user-data.txt"), "保留恢复冲突数据");
        check(Rejected(reopened.Restore) && File.Exists(Path.Combine(success.DirectoryPath, "new-user-data.txt")), "恢复目标存在时保留两侧数据，不覆盖新文件");
        Directory.Move(success.DirectoryPath, Path.Combine(success.Root, "restore-conflict-retained"));
        reopened.Restore();
        check(Directory.Exists(success.DirectoryPath) && !Directory.Exists(workflow.Quarantine) && File.Exists(Path.Combine(success.DirectoryPath, "cache.dat")),
            "重新载入加密任务后恢复隔离文件，不重新安装已卸载的程序");
        using (var shared = new Fixture("normal"))
        {
            shared.Set("InstallLocation", success.DirectoryPath);
            try { check(Rejected(() => reopened.PreviewResiduals(default)), "目录与另一条安装记录重叠时拒绝残留隔离授权"); }
            finally { shared.Set("InstallLocation", shared.DirectoryPath); }
        }
        reopened.PreviewResiduals(default);
        Directory.CreateDirectory(Path.Combine(success.DirectoryPath, "new-empty-directory"));
        check(Rejected(() => reopened.Isolate(true)), "预览后新增空目录同样触发内容变化保护");
        reopened.PreviewResiduals(default);
        using (var lease = new FileStream(Path.Combine(reopened.Root, "task.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            check(Rejected(() => UninstallWorkflow.Open(reopened.State.Id, true).Isolate(true)), "多个实例不能同时隔离或恢复同一任务");
        var cancelled = false;
        try { reopened.PreviewResiduals(new CancellationToken(true)); } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && Directory.Exists(success.DirectoryPath), "残留预览支持取消，不移动原目录");
        check(new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") }
            .All(path => Rejected(() => UninstallFiles.ValidateDirectory(path))), "拒绝系统、共享根目录和个人下载目录的残留隔离");

        void SaveSyntheticState(UninstallWorkflow task, WorkflowState state)
        {
            if (!PortableScanner.IsWithin(task.Root, Path.Combine(AppContext.BaseDirectory, "read-only-fixtures"))) throw new InvalidOperationException("只允许修改新建测试任务。");
            var encoded = ProtectedData.Protect(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state, CatalogStorage.JsonOptions), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(Path.Combine(task.Root, "task.bin"), encoded);
        }
        reopened.Isolate(true);
        check(Rejected(() => reopened.PreviewResiduals(default)) && reopened.State.Residuals.Count > 0, "隔离后再次预览不会清空用于恢复的文件清单");
        SaveSyntheticState(reopened, reopened.State with { Isolation = "准备隔离" });
        var interrupted = UninstallWorkflow.Open(reopened.State.Id, true);
        interrupted.Recheck();
        check(interrupted.State.Isolation == "已隔离", "隔离移动后保存结果中断时根据原清单核对实际位置");
        interrupted.Restore();
        SaveSyntheticState(interrupted, interrupted.State with { Isolation = "准备恢复" });
        interrupted = UninstallWorkflow.Open(reopened.State.Id, true); interrupted.Recheck();
        check(interrupted.State.Isolation == "已恢复" && Directory.Exists(success.DirectoryPath), "恢复移动后保存结果中断时复核内容，不重复覆盖");
        SaveSyntheticState(interrupted, interrupted.State with { Result = UninstallWorkflow.EvaluateExit(3010, true), StartedAt = DateTimeOffset.UtcNow });
        interrupted = UninstallWorkflow.Open(reopened.State.Id, true); interrupted.Recheck();
        check(interrupted.State.Result.Status == UninstallStatus.RebootRequired, "尚未实际重启时不解除等待重启限制");
        SaveSyntheticState(interrupted, interrupted.State with { StartedAt = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64) - TimeSpan.FromMinutes(1) });
        interrupted = UninstallWorkflow.Open(reopened.State.Id, true); interrupted.Recheck();
        check(interrupted.State.Result.Status == UninstallStatus.Verified, "模拟重启时序后仍需精确安装身份和独立文件证据才能通过复核");
        var encrypted = Path.Combine(reopened.Root, "task.bin");
        var bytes = File.ReadAllBytes(encrypted);
        var changed = bytes.ToArray(); changed[^1] ^= 1; File.WriteAllBytes(encrypted, changed);
        check(Rejected(() => UninstallWorkflow.Open(reopened.State.Id, true)), "篡改 DPAPI 任务记录无法伪造隔离恢复授权");
        File.WriteAllBytes(encrypted, bytes);
        File.Copy(Path.Combine(success.Root, "program.backup"), success.MainPath);
        var staleSuccess = UninstallWorkflow.Open(reopened.State.Id, true); staleSuccess.Recheck();
        check(staleSuccess.State.Result.Status == UninstallStatus.Unconfirmed && Rejected(() => staleSuccess.PreviewResiduals(default)),
            "历史成功任务发现程序重新出现时降为未确认，不继续授权清理");
        SaveSyntheticState(staleSuccess, staleSuccess.State with { Result = new(UninstallStatus.Running, null, "模拟中断"), ProcessId = int.MaxValue, ProcessStartTicks = 1 });
        var stopped = UninstallWorkflow.Open(staleSuccess.State.Id, true); stopped.Recheck();
        check(stopped.State.Result.Status == UninstallStatus.Unconfirmed, "没有保存退出结果的中断进程不补报成功");

        using var changedEntry = new Fixture("normal");
        var pending = UninstallWorkflow.Prepare(changedEntry.Row, true);
        changedEntry.Set("UninstallString", changedEntry.Command + " changed");
        check(Rejected(() => Execute(pending)) && File.Exists(changedEntry.MainPath), "预检后安装记录或命令变化必须重新确认");
        changedEntry.Set("UninstallString", changedEntry.Command);
        using var changedFile = new Fixture("normal");
        pending = UninstallWorkflow.Prepare(changedFile.Row, true);
        File.AppendAllText(changedFile.Uninstaller, "changed");
        check(Rejected(() => Execute(pending)) && File.Exists(changedFile.MainPath), "预检后替换卸载器被摘要检查阻止");
        using var protectedFixture = new Fixture("normal"); protectedFixture.Set("NoRemove", 1);
        check(Rejected(() => UninstallWorkflow.Prepare(protectedFixture.Row, true)), "正式流程也拒绝 NoRemove，不仅在界面提示");

        foreach (var scenario in new[] { "fail", "noop" })
        {
            using var fixture = new Fixture(scenario);
            var task = UninstallWorkflow.Prepare(fixture.Row, true);
            var outcome = Execute(task);
            check(outcome.Status == (scenario == "fail" ? UninstallStatus.Failed : UninstallStatus.Unconfirmed)
                && File.Exists(fixture.MainPath) && Rejected(() => task.PreviewResiduals(default)), "失败或假成功不授权残留处理：" + scenario);
        }
        using var readyFixture = new Fixture("normal");
        var readyTask = UninstallWorkflow.Prepare(readyFixture.Row, true);
        if (Execute(readyTask).Status != UninstallStatus.Verified) throw new InvalidOperationException("并发隔离检查夹具未完成卸载。");
        readyTask.PreviewResiduals(default);
        using var slow = new Fixture("delay");
        var running = UninstallWorkflow.Prepare(slow.Row, true);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var waiting = Execute(running, stop.Token);
        check(waiting.Status == UninstallStatus.Running && running.IsRunning && File.Exists(slow.MainPath), "取消等待不会强杀专用厂商卸载器，也不提前宣称成功");
        check(Rejected(() => readyTask.Isolate(true)) && Directory.Exists(readyFixture.DirectoryPath),
            "另一卸载器运行时也禁止已成功旧任务隔离残留，全局锁覆盖文件操作");
        using var blocked = new Fixture("normal");
        var other = UninstallWorkflow.Prepare(blocked.Row, true);
        check(Rejected(() => Execute(other)) && File.Exists(blocked.MainPath), "卸载器仍运行时全局互斥阻止第二个卸载任务");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (running.IsRunning && DateTime.UtcNow < deadline) Thread.Sleep(100);
        check(running.State.Result.Status == UninstallStatus.Verified && !File.Exists(slow.MainPath), "停止等待后后台继续核验并保存真实结果");
        using var spawned = new Fixture("child");
        var child = UninstallWorkflow.Prepare(spawned.Row, true);
        check(Execute(child).Status == UninstallStatus.Unconfirmed && Rejected(() => child.PreviewResiduals(default)), "卸载器派生子进程后提前退出时不误报完成或授权清理");
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (File.Exists(spawned.MainPath) && DateTime.UtcNow < deadline) Thread.Sleep(100);
        check(!File.Exists(spawned.MainPath) && child.State.Result.Status == UninstallStatus.Unconfirmed, "子进程完成后仍保留待确认结论，不自动扩大清理授权");
    }
}
