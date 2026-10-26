using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace AppInventory;

internal static class InnoFixtureChecks
{
    internal static void Run(Action<bool, string> check)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") throw new InvalidOperationException("Inno 专用夹具仅允许独立 CI 环境。");
        var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Inno Setup 6", "ISCC.exe");
        if (!File.Exists(compiler)) throw new IOException("隔离环境没有预装 Inno 编译器；未下载或安装额外工具。");
        var token = Guid.NewGuid().ToString("N");
        var root = Path.Combine(AppContext.BaseDirectory, "read-only-fixtures", "inno", token);
        var installed = Path.Combine(root, "installed");
        var name = "AppInventory Inno Fixture " + token[..8] + " Fixture";
        var key = InstallIdentity.Root + "AiInnoFixture" + token + "_is1";
        UninstallLab.CheckAncestors(root);
        if (Directory.Exists(root)) throw new IOException("Inno 测试目录已存在，拒绝覆盖。");
        Directory.CreateDirectory(root);
        var identity = new InstallIdentity(RegistryHive.CurrentUser, RegistryView.Registry32, key);
        if (identity.Read() != null) throw new InvalidOperationException("随机 Inno 测试身份已存在。");
        var payload = Path.Combine(root, "TestApplication.exe");
        File.Copy(UninstallLab.SourceBinary, payload);
        File.WriteAllText(Path.Combine(root, "keep.txt"), token);
        if (root.Contains('"') || root.Any(char.IsControl)) throw new InvalidOperationException("专用夹具路径存在脚本歧义。");
        var script = Path.Combine(root, "fixture.iss");
        File.WriteAllText(script, $$"""
            [Setup]
            AppId=AiInnoFixture{{token}}
            AppName={{name}}
            AppVersion=1.0
            AppPublisher=AppInventory contributors
            DefaultDirName={{installed}}
            PrivilegesRequired=lowest
            DisableProgramGroupPage=yes
            UninstallDisplayIcon={app}\TestApplication.exe
            UninstallDisplayName={{name}}
            OutputDir={{root}}
            OutputBaseFilename=FixtureSetup
            Compression=lzma
            SolidCompression=yes
            [Files]
            Source: "{{payload}}"; DestDir: "{app}"; Flags: ignoreversion
            """, new UTF8Encoding(true));
        var stalled = false;
        void Invoke(string executable, params string[] arguments)
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new IOException("无法启动 Inno 专用夹具。");
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90000)) { stalled = true; throw new IOException("Inno 专用夹具超时，未强制终止进程，也不并发回收。"); }
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new IOException("Inno 专用夹具失败：" + process.ExitCode + " · " + error.Result + output.Result);
        }
        var uninstaller = Path.Combine(installed, "unins000.exe");
        try
        {
            SelfTest.Progress("开始：云端编译及安装随机身份 Inno 专用包");
            Invoke(compiler, "/Qp", script);
            Invoke(Path.Combine(root, "FixtureSetup.exe"), "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-");
            var snapshot = identity.Read();
            SelfTest.Progress($"Inno 安装核对：记录存在={snapshot != null}，名称匹配={snapshot?.Name == name}，目录匹配={snapshot?.InstallPath.TrimEnd('\\') == installed}，主程序存在={File.Exists(Path.Combine(installed, "TestApplication.exe"))}");
            check(snapshot?.Name == name && snapshot.InstallPath.TrimEnd('\\') == installed && File.Exists(Path.Combine(installed, "TestApplication.exe")),
                "Inno 实际安装随机身份用户级专用包，精确身份与主程序一致");
            // 仅修改本夹具的新记录以适配无桌面 CI；普通应用仍使用原始标准入口。
            using (var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32))
            using (var registration = hive.OpenSubKey(key, true))
            {
                if ((string)registration?.GetValue("DisplayName") != name || identity.Read()?.InstallPath.TrimEnd('\\') != installed)
                    throw new InvalidOperationException("专用 Inno 身份改变，不改写入口。");
                registration.SetValue("UninstallString", "\"" + uninstaller + "\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART");
            }
            var row = new AppRow { Name = name, Version = "1.0", InstallPath = installed, ExecutablePath = Path.Combine(installed, "TestApplication.exe"),
                UninstallRegistration = new("HKEY_CURRENT_USER\\" + key, identity.Read().Command, "", "Inno", false) };
            var task = UninstallWorkflow.Prepare(row, true);
            SelfTest.Progress("开始：通过正式流程卸载 Inno 专用包");
            var result = task.ExecuteAsync(task.State.Plan.Name, true, false, default).GetAwaiter().GetResult();
            check(result.Status == UninstallStatus.Verified && identity.Read() == null && !File.Exists(row.ExecutablePath),
                "正式流程实际卸载 Inno 专用包，退出结果、精确记录和主程序证据一致");
            check(File.ReadAllText(Path.Combine(root, "keep.txt")) == token, "Inno 专用卸载保留安装目录外的数据哨兵");
        }
        finally
        {
            var remaining = identity.Read();
            if (!stalled && remaining?.Name == name && remaining.InstallPath.TrimEnd('\\') == installed && File.Exists(uninstaller))
                Invoke(uninstaller, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART");
        }
    }
}
