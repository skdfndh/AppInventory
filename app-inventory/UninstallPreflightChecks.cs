using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AppInventory;

internal static class UninstallPreflightChecks
{
    internal static void Run(MainWindow window, Action<bool, string> check)
    {
        var registration = new UninstallRegistration(@"HKEY_CURRENT_USER\Software\AppInventorySynthetic",
            "\"D:\\合成 App\\uninstall.exe\" /interactive", "\"D:\\合成 App\\uninstall.exe\" /silent", "Unknown", false);
        var desktop = new AppRow { Name = "合成卸载预检", Version = "1.0", InstallPath = @"D:\Synthetic App", UninstallRegistration = registration };
        var preview = desktop.UninstallPreview;
        check(preview.Command == registration.Command && preview.QuietCommand == registration.QuietCommand,
            "卸载预检保留中文及空格命令边界，标准与静默入口分别展示且不改写参数");
        check(preview.Scope.StartsWith("当前用户") && preview.Status.Contains("预检仅展示"), "用户级卸载预检明确范围且不直接执行");
        var machine = new AppRow { UninstallRegistration = registration with { RegistryPath = @"HKEY_LOCAL_MACHINE\Software\AppInventorySynthetic", Protected = true } };
        check(machine.UninstallPreview.Status.Contains("NoRemove") && machine.UninstallPreview.Scope.Contains("可能需要管理员"),
            "受保护记录阻止卸载，计算机级记录不声称已获得管理员权限");
        var quiet = new AppRow { UninstallRegistration = registration with { Command = "" } };
        check(quiet.UninstallPreview.Command == "未提供" && quiet.UninstallPreview.Status.Contains("不自动回退"),
            "仅静默入口时阻止执行，不把静默命令替代标准入口");
        var missing = new AppRow { UninstallRegistration = registration with { Command = "", QuietCommand = "" } };
        check(missing.UninstallPreview.Status.Contains("未提供标准卸载入口"), "缺失卸载命令时不按安装目录推断删除行为");
        var unavailable = new AppRow { UninstallRegistration = registration with { ReadError = "安装记录无法读取" } };
        check(unavailable.UninstallPreview.Status == "安装记录无法读取", "卸载信息读取失败时显示阻断原因");
        var portable = new AppRow { IsPortableCandidate = true, UninstallRegistration = registration };
        var store = new AppRow { Source = "商店 / MSIX 应用", UninstallRegistration = registration };
        check(portable.UninstallPreview.Command == "未提供" && store.UninstallPreview.Command == "未提供",
            "便携和商店记录不会沿用桌面卸载命令");

        window.SetApps(new() { desktop });
        // 无窗口检查也要等待数据上下文传播，才能核对最终显示内容。
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        var commandText = (TextBox)window.FindName("UninstallCommandText");
        var quietText = (TextBox)window.FindName("QuietUninstallCommandText");
        check(commandText.IsReadOnly && quietText.IsReadOnly && commandText.Text == registration.Command && quietText.Text == registration.QuietCommand,
            "详情区显示选中软件的两个只读卸载字段");
        var json = JsonSerializer.Serialize(CatalogStorage.Capture(new[] { desktop }, null, Array.Empty<string>()), CatalogStorage.JsonOptions);
        check(!json.Contains("UninstallRegistration") && !json.Contains("/interactive") && !json.Contains("/silent"),
            "历史快照和 JSON 导出不保存卸载命令或卸载授权信息");
        var defaultJson = JsonSerializer.Serialize(desktop);
        check(!defaultJson.Contains("/interactive") && !defaultJson.Contains("/silent"),
            "默认 JSON 序列化同样不会从预检显示属性泄漏卸载命令");
        var fixture = Path.Combine(AppContext.BaseDirectory, "read-only-fixtures", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(fixture, "preflight-snapshot.json");
        CatalogStorage.WriteText(path, json);
        var restored = CatalogStorage.Restore(CatalogStorage.Read<SavedCatalog>(path));
        check(restored.Single().UninstallRegistration == null && restored.Single().UninstallPreview.Status.Contains("请刷新清单"),
            "恢复历史清单后要求重新读取系统卸载信息");
        var stale = new AppRow { Name = desktop.Name, Version = desktop.Version, InstallPath = desktop.InstallPath,
            UninstallRegistration = registration with { Command = "stale command" } };
        var merged = CatalogStorage.MergeHistory(new() { desktop }, new[] { stale });
        check(merged.Single().UninstallPreview.Command == registration.Command,
            "合并历史测量与关联时保留本次卸载入口，不覆盖为旧命令");
        var forged = JsonSerializer.Deserialize<AppRow>("{\"Name\":\"合成快照\",\"UninstallRegistration\":{\"Command\":\"forged command\"}}", CatalogStorage.JsonOptions);
        check(forged.UninstallRegistration == null, "导入 JSON 无法注入卸载入口");
        var forgedPackage = JsonSerializer.Deserialize<AppRow>("{\"Name\":\"合成商店快照\",\"Source\":\"商店 / MSIX 应用\",\"PackageFullName\":\"forged-package\"}", CatalogStorage.JsonOptions);
        check(forgedPackage.PackageFullName.Length == 0, "历史 JSON 不能注入商店应用的完整卸载身份");
    }
}
