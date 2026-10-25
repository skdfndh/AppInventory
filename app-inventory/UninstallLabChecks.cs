using System.IO;
using System.Security.Cryptography;

namespace AppInventory;

internal static class UninstallLabChecks
{
    internal static void Run(Action<bool, string> check)
    {
        bool Rejected(Action action)
        {
            try { action(); return false; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or ArgumentException) { return true; }
        }
        string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
        var success = UninstallLab.Create(true);
        var originalHash = Hash(success.Target);
        var retained = File.ReadAllText(success.RetainedData);
        check(File.Exists(success.Target) && originalHash == Hash(UninstallLab.SourceBinary), "测试模式创建真实可执行的专用应用，副本哈希与随包组件一致");
        check(PortableScanner.IsWithin(success.Root, Path.Combine(AppContext.BaseDirectory, "read-only-fixtures")), "自动卸载验证使用独立夹具目录，不写正式设置或快照");
        var plan = success.Plan("success");
        check(plan.Token == success.Token && plan.Target == success.Target && plan.FixtureMode && !string.IsNullOrWhiteSpace(plan.ExecutableHash), "卸载计划绑定会话、精确目标和辅助程序哈希");
        check(Rejected(() => success.RunAsync(plan with { Executable = @"C:\Windows\System32\cmd.exe" }, CancellationToken.None).GetAwaiter().GetResult())
            && File.Exists(success.Target), "篡改后的卸载计划被拒绝，不能替换成任意命令");
        var result = success.RunAsync(plan, CancellationToken.None).GetAwaiter().GetResult();
        check(result.Verified && result.ExitCode == 0 && result.Status == "已验证卸载", "BCU 实际调用测试卸载器，并核对退出码及文件状态");
        check(!File.Exists(success.Target) && File.Exists(success.Backup) && Hash(success.Backup) == originalHash, "专用测试程序已移出安装目录且保留完整备份");
        var preview = success.Preview();
        check(preview.Count == 1 && preview[0].Path == success.AppDirectory && File.Exists(Path.Combine(success.AppDirectory, "cache.log")), "BCU 测试残留预览只返回本会话目录且不移动数据");
        var cache = File.ReadAllText(Path.Combine(success.AppDirectory, "cache.log"));
        success.Isolate();
        check(!Directory.Exists(success.AppDirectory) && File.ReadAllText(Path.Combine(success.Quarantine, "cache.log")) == cache, "测试残留移入隔离区且内容保持完整");
        var reopened = UninstallLab.Open(success.Token, true);
        check(reopened.LastResult is { Verified: true, ExitCode: 0 }, "重新载入会话仍保留已确认的执行结果，后续操作继续核对实际文件状态");
        reopened.Restore();
        check(Directory.Exists(success.AppDirectory) && !Directory.Exists(success.Quarantine)
            && File.ReadAllText(Path.Combine(success.AppDirectory, "cache.log")) == cache, "重新打开测试会话后可以恢复隔离缓存");
        check(File.ReadAllText(success.RetainedData) == retained && !File.Exists(success.Target), "隔离及恢复均保留用户数据哨兵，恢复不会重新安装程序");
        var reportPath = Path.Combine(success.Root, "lab-report.json");
        success.Export(reportPath);
        var report = CatalogStorage.Read<LabReport>(reportPath);
        check(report.Schema == 1 && report.Token == success.Token && report.Target == success.Target
            && report.Result is { Verified: true, ExitCode: 0 } && report.Events.Any(item => item.Action == "已验证卸载")
            && report.Limitation.Contains("仅验证") && report.ExportedAt > DateTimeOffset.MinValue,
            "测试报告的顶层会话、目标、结果、导出时间和验证边界均完整保存");
        check(report.Events.Any(item => item.Action == "已恢复"), "较早打开的会话导出时仍包含其他实例新写入的恢复记录");
        success.Preview();
        var combinedEvents = UninstallLab.Open(success.Token, true).Events;
        check(combinedEvents.Any(item => item.Action == "已恢复") && combinedEvents.Last().Action == "残留预览",
            "不同实例先后操作同一会话时追加日志，不覆盖其他实例已保存的事件");

        var failed = UninstallLab.Create(true);
        var failureResult = failed.RunAsync(failed.Plan("fail"), CancellationToken.None).GetAwaiter().GetResult();
        check(failureResult.ExitCode == 23 && !failureResult.Verified && File.Exists(failed.Target), "非零退出码按失败处理并保留测试程序");
        check(Rejected(() => failed.Preview()) && Rejected(failed.Isolate), "卸载失败时禁止残留预览授权与隔离");

        var noop = UninstallLab.Create(true);
        var noopResult = noop.RunAsync(noop.Plan("noop"), CancellationToken.None).GetAwaiter().GetResult();
        check(noopResult.ExitCode == 0 && !noopResult.Verified && noopResult.Status == "结果不符" && File.Exists(noop.Target), "退出码为零但程序仍在时不误报卸载成功");
        check(Rejected(() => noop.Preview()) && Rejected(noop.Isolate), "未实际卸载时不能隔离残留");

        var timeout = UninstallLab.Create(true);
        var timedOut = timeout.RunAsync(timeout.Plan("slow"), CancellationToken.None, TimeSpan.FromMilliseconds(350)).GetAwaiter().GetResult();
        check(timedOut.Status == "测试超时" && !timedOut.Verified && File.Exists(timeout.Target) && !File.Exists(timeout.Backup), "测试超时只终止专用辅助进程，程序和数据保持原位");
        var cancelled = UninstallLab.Create(true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var cancelledResult = cancelled.RunAsync(cancelled.Plan("slow"), cancellation.Token).GetAwaiter().GetResult();
        check(cancelledResult.Status == "已取消测试" && !cancelledResult.Verified && File.Exists(cancelled.Target), "主动取消测试不产生成功结论，测试应用仍被保留");
        check(Rejected(() => cancelled.Preview()), "取消后不会允许隔离操作");

        var interrupted = UninstallLab.Create(true);
        File.Move(interrupted.Target, interrupted.Backup, false);
        var incomplete = UninstallLab.Open(interrupted.Token, true);
        check(incomplete.LastResult is { Verified: false, Status: "上次执行未确认" }, "程序已备份但未保存结果的中断会话明确标记为未确认");
        check(Rejected(() => incomplete.Preview()) && Rejected(incomplete.Isolate)
            && File.Exists(incomplete.Backup) && File.Exists(incomplete.RetainedData), "中断会话不授权隔离，程序备份和保留数据仍在");

        var changed = UninstallLab.Create(true);
        changed.RunAsync(changed.Plan("success"), CancellationToken.None).GetAwaiter().GetResult();
        File.Move(changed.Backup, changed.Target, false);
        var rechecked = UninstallLab.Open(changed.Token, true);
        check(rechecked.LastResult is { Verified: false, Status: "状态已改变" }
            && Rejected(() => rechecked.Preview()), "历史成功结论与当前文件不符时降为未确认并阻止残留预览");
        var changedReport = Path.Combine(changed.Root, "changed-report.json");
        changed.Export(changedReport);
        check(CatalogStorage.Read<LabReport>(changedReport).Result is { Verified: false, Status: "状态已改变" }
            && File.Exists(changed.Target), "较早实例导出同样复核文件状态，不继续报告已验证卸载");

        var foreign = UninstallLab.Create(true);
        var foreignPath = Path.Combine(foreign.AppDirectory, "personal-file.txt");
        File.WriteAllText(foreignPath, "必须保留的额外数据");
        check(Rejected(() => foreign.Plan("success")) && File.ReadAllText(foreignPath) == "必须保留的额外数据", "测试目录混入额外文件后阻止操作且保留文件");
        var damaged = UninstallLab.Create(true);
        var markerPath = Path.Combine(damaged.Root, "session.json");
        var marker = CatalogStorage.Read<LabMarker>(markerPath);
        CatalogStorage.Save(markerPath, marker with { Token = Guid.NewGuid().ToString("N") });
        check(Rejected(() => damaged.Plan("success")) && File.Exists(damaged.Target), "会话标记不匹配时拒绝执行");
        var forged = UninstallLab.Create(true);
        var forgedMarkerPath = Path.Combine(forged.Root, "session.json");
        var forgedMarker = CatalogStorage.Read<LabMarker>(forgedMarkerPath);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "AppInventory.exe"), forged.Target, true);
        CatalogStorage.Save(forgedMarkerPath, forgedMarker with { BinaryHash = Hash(forged.Target) });
        check(Rejected(() => forged.Plan("success")), "同时替换测试程序和标记哈希也不能绕过构建时固定摘要");
        var locked = UninstallLab.Create(true);
        var lockedPlan = locked.Plan("success");
        using (var lease = new FileStream(Path.Combine(locked.Root, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            check(Rejected(() => UninstallLab.Open(locked.Token, true).RunAsync(lockedPlan, CancellationToken.None).GetAwaiter().GetResult())
                && File.Exists(locked.Target), "同一测试会话被其他实例占用时阻止重复执行");
        check(UninstallLab.RunWorker("not-a-token", "success", "fixture") == 27
            && UninstallLab.RunWorker(success.Token, "arbitrary", "fixture") == 27
            && UninstallLab.RunWorker(success.Token, "success", @"D:\OtherApps") == 27, "辅助入口拒绝无效会话、未知场景和任意根目录");

        var conflict = UninstallLab.Create(true);
        conflict.RunAsync(conflict.Plan("success"), CancellationToken.None).GetAwaiter().GetResult();
        conflict.Isolate();
        Directory.CreateDirectory(conflict.AppDirectory);
        var conflictFile = Path.Combine(conflict.AppDirectory, "keep.txt");
        File.WriteAllText(conflictFile, "恢复目标中的现有数据");
        check(Rejected(conflict.Restore) && Directory.Exists(conflict.Quarantine)
            && File.ReadAllText(conflictFile) == "恢复目标中的现有数据", "恢复目标已存在时不覆盖，隔离区和目标数据都保留");
        check(UninstallTools.UninstallToolsGlobalConfig.FileDeletionHandler == null, "受控测试没有开启 BCU 的通用删除处理器");

        var linked = UninstallLab.Create(true);
        var moved = Path.Combine(linked.Root, "original-app");
        if (!PortableScanner.IsWithin(linked.AppDirectory, linked.Root) || !PortableScanner.IsWithin(moved, linked.Root))
            throw new InvalidOperationException("链接检查的移动目标不在测试范围。");
        UninstallLab.CheckAncestors(linked.AppDirectory);
        UninstallLab.CheckAncestors(moved);
        Directory.Move(linked.AppDirectory, moved);
        var linkCommand = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = $"/d /c mklink /J \"{linked.AppDirectory}\" \"{moved}\"", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (var linkProcess = System.Diagnostics.Process.Start(linkCommand))
        {
            if (linkProcess == null || !linkProcess.WaitForExit(5000) || linkProcess.ExitCode != 0)
                throw new InvalidOperationException("无法创建测试目录联接，路径防护未完成验证。");
        }
        check(Rejected(() => linked.Plan("success")) && File.Exists(Path.Combine(moved, "TestApplication.exe")), "真实目录联接被拒绝，不沿链接运行或移动测试程序");
        var nested = Path.Combine(moved, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "retained.bin"), new byte[17]);
        check(Rejected(() => DirectorySize.Measure(Path.Combine(linked.AppDirectory, "nested"), CancellationToken.None))
            && new FileInfo(Path.Combine(nested, "retained.bin")).Length == 17, "目录测量拒绝父目录中的联接，保留链接目标数据");
    }
}
