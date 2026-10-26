using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

[assembly: AssemblyTitle("应用清单专用注册卸载器")]
[assembly: AssemblyVersion("1.0.0.0")]
internal static class RegisteredUninstaller
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1 || (args[0] != "normal" && args[0] != "fail" && args[0] != "noop" && args[0] != "delay" && args[0] != "child")) return 27;
            var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var token = File.ReadAllText(Path.Combine(directory, "fixture-token.txt"));
            Guid parsed;
            if (!Guid.TryParseExact(token, "N", out parsed) || new DirectoryInfo(directory).Name != "AppInventoryFixture-" + token) return 27;
            for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return 27;
            var subkey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AppInventoryFixture-" + token;
            using (var key = Registry.CurrentUser.OpenSubKey(subkey, false))
                if (key == null || (string)key.GetValue("InstallLocation") != directory || (string)key.GetValue("AppInventoryFixtureToken") != token) return 27;
            if (args[0] == "fail") return 23;
            if (args[0] == "noop") return 0;
            if (args[0] == "child")
            {
                Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "delay") { UseShellExecute = false, CreateNoWindow = true });
                return 0;
            }
            if (args[0] == "delay") System.Threading.Thread.Sleep(2500);
            File.Move(Path.Combine(directory, "TestApplication.exe"), Path.Combine(Directory.GetParent(directory).FullName, "program.backup"));
            // 只移除本次新建且标识与精确目录一致的测试记录，不接受外部目标参数。
            Registry.CurrentUser.DeleteSubKey(subkey, true);
            return 0;
        }
        catch { return 27; }
    }
}
