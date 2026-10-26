using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace AppInventory;

internal sealed record PackageSnapshot(string FullName, string FamilyName, string Name, string Version, string Publisher,
    string Location, PackageSignatureKind SignatureKind);

internal static class PackageUninstall
{
    internal static Package Find(string fullName) => new PackageManager().FindPackagesForUser(string.Empty)
        .SingleOrDefault(package => package.Id.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase));

    internal static PackageSnapshot Read(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) throw new InvalidOperationException("没有本次系统读取的完整包身份，请刷新清单。");
        var package = Find(fullName) ?? throw new InvalidOperationException("此用户的应用包已消失，请刷新清单。");
        if (package.IsFramework || package.IsResourcePackage || package.IsBundle || package.SignatureKind is PackageSignatureKind.System or PackageSignatureKind.None)
            throw new InvalidOperationException("系统包、框架、资源、组合包或未签名包不支持此卸载入口。");
        if (new PackageManager().FindPackagesForUser(string.Empty).Any(other => !other.IsResourcePackage && !other.IsBundle
            && !other.Id.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase)
            && other.Dependencies.Any(dependency => dependency.Id.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("其他应用仍依赖此包，禁止卸载。");
        var version = package.Id.Version;
        return new(package.Id.FullName, package.Id.FamilyName, string.IsNullOrWhiteSpace(package.DisplayName) ? package.Id.Name : package.DisplayName,
            $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}", package.Id.Publisher,
            package.InstalledLocation?.Path ?? "", package.SignatureKind);
    }

    internal static WorkflowPlan Prepare(AppRow row)
    {
        var package = Read(row.PackageFullName);
        return new(null, new("", "Windows PackageManager.RemovePackageAsync（当前用户）", ""), "",
            "Windows 已登记包签名类型：" + package.SignatureKind, "", "", package);
    }

    internal static async Task RemoveAsync(string fullName)
    {
        // 不使用 AllUsers 或批量名称匹配，卸载对象只可能是刚确认的完整包标识。
        var result = await new PackageManager().RemovePackageAsync(fullName, RemovalOptions.None).AsTask().ConfigureAwait(false);
        if (result.ExtendedErrorCode != null && result.ExtendedErrorCode.HResult != 0)
            throw new InvalidOperationException("Windows 包部署失败：" + result.ErrorText, result.ExtendedErrorCode);
    }
}
