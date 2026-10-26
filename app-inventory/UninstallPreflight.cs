using System.IO;
using System.Security;
using Microsoft.Win32;
using UninstallTools;

namespace AppInventory;

internal sealed record UninstallRegistration(string RegistryPath, string Command, string QuietCommand, string Kind,
    bool Protected, string ReadError = "");

internal sealed record UninstallPreview(string Status, string EntryKind, string Scope, string RegistryPath,
    string Command, string QuietCommand)
{
    public string Boundary => "此处仅展示系统登记信息。点击卸载后还需重新核验并单独确认；关联候选不作为删除依据。";
}

internal static class UninstallPreflight
{
    internal static UninstallRegistration Read(ApplicationUninstallerEntry entry)
    {
        try
        {
            using var key = entry.OpenRegKey(false);
            if (key == null) return new(entry.RegistryPath ?? "", "", "", entry.UninstallerKind.ToString(), entry.IsProtected, "安装记录已不存在或无法读取，请刷新清单。");
            // BCU 会模糊匹配并回退到静默命令；预检只展示原始字段，避免混淆入口来源。
            string Command(string name) => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            return new(entry.RegistryPath ?? "", Command("UninstallString"), Command("QuietUninstallString"),
                entry.UninstallerKind.ToString(), entry.IsProtected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return new(entry.RegistryPath ?? "", "", "", entry.UninstallerKind.ToString(), entry.IsProtected, "卸载字段无法读取，请刷新或检查当前读取权限。");
        }
    }

    internal static UninstallPreview Create(AppRow row)
    {
        if (row.IsPortableCandidate)
            return Empty("便携 / 未注册候选：没有可靠卸载记录，不能自动删除程序目录。", "便携候选");
        if (row.Source == "商店 / MSIX 应用")
            return Empty("商店应用需要独立的包管理卸载流程，当前未启用。", "商店 / MSIX");
        var entry = row.UninstallRegistration;
        if (entry == null) return Empty("尚无本次读取的卸载信息；历史快照不能用于执行，请刷新清单。", "未核实");
        var scope = entry.RegistryPath.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase)
            ? "计算机级记录 · 可能需要管理员权限，尚未检查"
            : entry.RegistryPath.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase)
                ? "当前用户记录 · 厂商卸载器仍可能要求管理员权限" : "记录范围未核实";
        var status = entry.ReadError.Length > 0 ? entry.ReadError
            : entry.Protected ? "安装记录设置了 NoRemove，阻止卸载。"
            : string.IsNullOrWhiteSpace(entry.Command)
                ? string.IsNullOrWhiteSpace(entry.QuietCommand) ? "未提供标准卸载入口，阻止执行。" : "仅提供静默卸载入口；不自动回退，阻止执行。"
                : "已读取标准卸载入口；预检仅展示，打开卸载计划后继续核验。";
        return new(status, entry.Kind, scope, Display(entry.RegistryPath), Display(entry.Command), Display(entry.QuietCommand));
    }

    private static UninstallPreview Empty(string status, string kind) => new(status, kind, "未核实", "未提供", "未提供", "未提供");
    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "未提供" : value;
}
