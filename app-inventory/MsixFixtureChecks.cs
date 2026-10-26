using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Windows.Management.Deployment;

namespace AppInventory;

internal sealed record MsixFixture(string Token, string Name, string Publisher, string Thumbprint, string Package);

internal static class MsixFixtureChecks
{
    private static ProcessStartInfo PowerShell()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // 中间进程继承的 PowerShell 7 模块路径会破坏 5.1 的证书提供程序加载。
        info.Environment.Remove("PSModulePath");
        return info;
    }

    private static void Invoke(ProcessStartInfo info)
    {
        using var process = Process.Start(info) ?? throw new IOException("无法启动专用 PowerShell 检查。");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(90000)) { process.Kill(true); process.WaitForExit(); throw new IOException("专用 PowerShell 检查超时。"); }
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException("专用 PowerShell 检查失败：" + error.Result + output.Result);
    }

    internal static void Run(Action<bool, string> check)
    {
        var rejectedIdentity = false;
        try { PackageUninstall.Read(""); } catch (InvalidOperationException) { rejectedIdentity = true; }
        check(rejectedIdentity, "商店卸载必须有本次读取的完整包身份，不能使用名称或空身份");
        var probe = PowerShell();
        foreach (var argument in new[] { "-NoProfile", "-Command", "Import-Module Microsoft.PowerShell.Security -ErrorAction Stop; if (-not (Get-PSDrive -Name Cert -ErrorAction Stop)) { exit 1 }" }) probe.ArgumentList.Add(argument);
        Invoke(probe);
        check(true, "5.1 子进程独立加载证书提供程序，仅查询驱动，不导入或创建证书");
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") return;
        var token = Guid.NewGuid().ToString("N");
        var root = Path.Combine(AppContext.BaseDirectory, "read-only-fixtures", "msix", token);
        UninstallLab.CheckAncestors(root);
        if (Directory.Exists(root)) throw new IOException("MSIX 测试目录已存在，拒绝覆盖。");
        Directory.CreateDirectory(Path.Combine(root, "payload"));
        File.Copy(UninstallLab.SourceBinary, Path.Combine(root, "payload", "TestApplication.exe"));
        var helper = Path.Combine(AppContext.BaseDirectory, "测试组件", "Create-MsixFixture.ps1");
        var approved = typeof(MsixFixtureChecks).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == "MsixFixtureScriptSHA256").Value;
        if (UninstallFiles.Hash(helper) != approved) throw new InvalidDataException("MSIX 夹具脚本摘要不匹配。");
        void FixtureScript(string mode)
        {
            var info = PowerShell();
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", helper, "-Root", root, "-Token", token, "-Mode", mode }) info.ArgumentList.Add(argument);
            Invoke(info);
        }
        MsixFixture fixture = null;
        string fullName = null;
        try
        {
            FixtureScript("Build");
            fixture = JsonSerializer.Deserialize<MsixFixture>(File.ReadAllText(Path.Combine(root, "msix-fixture.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("MSIX 夹具标记缺失。");
            if (fixture.Token != token || fixture.Name != "AiFixture." + token || fixture.Publisher != "CN=AiFixture." + token)
                throw new InvalidDataException("MSIX 夹具身份不匹配。");
            var manager = new PackageManager();
            if (manager.FindPackagesForUser(string.Empty).Any(package => package.Id.Name == fixture.Name)) throw new InvalidOperationException("随机 MSIX 测试包已存在。");
            var deployed = manager.AddPackageAsync(new Uri(fixture.Package), null, DeploymentOptions.None).AsTask().GetAwaiter().GetResult();
            if (deployed.ExtendedErrorCode != null && deployed.ExtendedErrorCode.HResult != 0) throw new IOException(deployed.ErrorText);
            var added = manager.FindPackagesForUser(string.Empty).Single(package => package.Id.Name == fixture.Name && package.Id.Publisher == fixture.Publisher);
            fullName = added.Id.FullName;
            check(fullName.Length > 0 && PackageUninstall.Read(fullName).FamilyName == added.Id.FamilyName, "临时 Windows 虚拟机实际安装专用签名 MSIX，核对完整身份与包族");
            var row = new AppRow { Name = added.DisplayName, Source = "商店 / MSIX 应用", PackageFullName = fullName };
            var task = UninstallWorkflow.Prepare(row, true);
            var result = task.ExecuteAsync(task.State.Plan.Name, true, false, default).GetAwaiter().GetResult();
            check(result.Status == UninstallStatus.Verified && PackageUninstall.Find(fullName) == null, "通过正式流程实际移除当前用户的专用 MSIX，完整身份不再登记");
            var rejected = false;
            try { task.PreviewResiduals(default); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "商店卸载完成后不手动隔离 WindowsApps 或包用户数据");
            check(UninstallWorkflow.Open(task.State.Id, true).State.Plan.Package.FullName == fullName, "MSIX 任务记录保留精确包身份，历史任务仍不能重新执行");
        }
        finally
        {
            try
            {
                if (fixture != null && fullName == null)
                    fullName = new PackageManager().FindPackagesForUser(string.Empty)
                        .SingleOrDefault(package => package.Id.Name == fixture.Name && package.Id.Publisher == fixture.Publisher)?.Id.FullName;
                if (fullName != null && PackageUninstall.Find(fullName) != null) PackageUninstall.RemoveAsync(fullName).GetAwaiter().GetResult();
            }
            finally { FixtureScript("Cleanup"); }
            if (fixture != null)
            {
                using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine); store.Open(OpenFlags.ReadOnly);
                check(store.Certificates.Find(X509FindType.FindByThumbprint, fixture.Thumbprint, false).Count == 0,
                    "隔离测试结束后移除本次新建的信任证书，不保留临时信任");
            }
        }
    }
}
