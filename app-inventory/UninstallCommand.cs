using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AppInventory;

internal sealed record InstallIdentity(RegistryHive Hive, RegistryView View, string SubKey)
{
    internal const string Root = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";
    internal static InstallIdentity Parse(string path)
    {
        var split = path.IndexOf('\\');
        if (split < 0) throw new InvalidOperationException("缺少精确安装记录身份。");
        var hive = path[..split].ToUpperInvariant() switch
        {
            "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            _ => throw new InvalidOperationException("只支持当前用户或计算机级安装记录。")
        };
        var subKey = path[(split + 1)..];
        var view = RegistryView.Registry64;
        const string redirected = @"Software\Wow6432Node\";
        if (subKey.StartsWith(redirected, StringComparison.OrdinalIgnoreCase))
        {
            view = RegistryView.Registry32;
            subKey = @"Software\" + subKey[redirected.Length..];
        }
        if (!subKey.StartsWith(Root, StringComparison.OrdinalIgnoreCase) || subKey[Root.Length..].Length == 0
            || subKey[Root.Length..].Contains('\\')) throw new InvalidOperationException("不是独立应用的标准卸载记录。");
        return new(hive, view, subKey);
    }

    internal InstalledSnapshot Read()
    {
        using var root = RegistryKey.OpenBaseKey(Hive, View);
        using var key = root.OpenSubKey(SubKey, false);
        if (key == null) return null;
        string Text(string name) => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        bool Flag(string name) => Convert.ToInt64(key.GetValue(name, 0), System.Globalization.CultureInfo.InvariantCulture) != 0;
        return new(this, Text("DisplayName"), Text("DisplayVersion"), Text("Publisher"), Text("InstallLocation"),
            Text("UninstallString"), Flag("NoRemove"), Flag("SystemComponent"), Text("ParentKeyName"), Flag("WindowsInstaller"), Text("ReleaseType"));
    }
}

internal sealed record InstalledSnapshot(InstallIdentity Identity, string Name, string Version, string Publisher,
    string InstallPath, string Command, bool NoRemove, bool SystemComponent, string ParentKeyName, bool WindowsInstaller, string ReleaseType);
internal sealed record UninstallCommand(string Executable, string Arguments, string ProductCode)
{
    internal bool IsMsi => ProductCode.Length > 0;
    internal static UninstallCommand Parse(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Any(char.IsControl) || command.Length > 32760)
            throw new InvalidOperationException("卸载命令为空、过长或含控制字符。");
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        string executable, arguments;
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end < 0 || end + 1 < command.Length && !char.IsWhiteSpace(command[end + 1]))
                throw new InvalidOperationException("卸载路径引号存在歧义。");
            executable = command[1..end];
            arguments = command[(end + 1)..].Trim();
        }
        else
        {
            var end = command.IndexOfAny(new[] { ' ', '\t' });
            executable = end < 0 ? command : command[..end];
            arguments = end < 0 ? "" : command[(end + 1)..].Trim();
        }
        var fileName = Path.GetFileName(executable);
        if (fileName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || executable.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            var systemMsi = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            if (executable.Contains('\\') && !Path.GetFullPath(executable).Equals(systemMsi, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("MSI 必须使用系统 msiexec，不能使用同名副本。");
            var match = Regex.Match(arguments, @"^/[ix]\s*(\{[0-9a-fA-F-]{36}\})(?:\s+/norestart)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success || !Guid.TryParseExact(match.Groups[1].Value, "B", out var product))
                throw new InvalidOperationException("仅支持带精确产品代码的 MSI 入口，不接受附加属性、脚本或包文件。");
            var code = product.ToString("B").ToUpperInvariant();
            return new(systemMsi, "/x " + code + " /passive /norestart REBOOT=ReallySuppress", code);
        }
        if (!Regex.IsMatch(executable, @"^[a-zA-Z]:\\") || !Path.IsPathFullyQualified(executable)
            || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) || executable.Contains('"'))
            throw new InvalidOperationException("只支持无歧义的本地完整 EXE 路径；含空格的路径必须加引号。");
        var hosts = new[] { "cmd", "powershell", "pwsh", "rundll32", "regsvr32", "wscript", "cscript", "mshta", "reg", "schtasks", "sc", "dism", "wusa", "conhost", "explorer", "installutil", "msbuild" };
        if (hosts.Contains(Path.GetFileNameWithoutExtension(executable), StringComparer.OrdinalIgnoreCase)
            || PortableScanner.IsWithin(executable, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
            throw new InvalidOperationException("脚本宿主、系统工具和未知系统入口不支持直接卸载。");
        var fullPath = Path.GetFullPath(executable);
        if (fullPath.Length >= 260) throw new InvalidOperationException("卸载器路径超过当前进程启动支持范围（须短于 260 字符），未执行。");
        return new(fullPath, arguments, "");
    }
}

internal static class UninstallFiles
{
    internal static string Hash(string path, CancellationToken cancellationToken = default)
    {
        UninstallLab.CheckAncestors(path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int length;
        while ((length = stream.Read(buffer, 0, buffer.Length)) > 0) { cancellationToken.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, length); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void ValidateDirectory(string path)
    {
        if (!Regex.IsMatch(path ?? "", @"^[a-zA-Z]:\\") || !Path.IsPathFullyQualified(path)) throw new InvalidOperationException("需要本地完整应用目录。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var denied = new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData };
        if (new DirectoryInfo(full).Parent?.Parent == null || denied.Any(folder => string.Equals(full,
            Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(folder)), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("系统、用户或共享根目录不能作为应用残留目录。");
        var personal = new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.Windows };
        if (personal.Any(folder => { var root = Environment.GetFolderPath(folder); return root.Length > 0 && PortableScanner.IsWithin(full, root); })
            || PortableScanner.IsWithin(full, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"))
            || PortableScanner.IsWithin(AppContext.BaseDirectory, full) || PortableScanner.IsWithin(CatalogStorage.DataDirectory, full))
            throw new InvalidOperationException("个人目录、系统目录和本工具的数据目录不参与残留隔离。");
        if (new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" }.Select(Environment.GetEnvironmentVariable)
            .Any(root => !string.IsNullOrWhiteSpace(root) && PortableScanner.IsWithin(full, root)))
            throw new InvalidOperationException("同步盘目录不参与残留隔离。");
        UninstallLab.CheckAncestors(full);
    }
}

internal static class InstallerNative
{
    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    internal static extern int MsiQueryProductState(string product);

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, [In] ref Guid action, ref TrustData data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr File, Subject; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustData
    {
        public uint Size; public IntPtr Policy, Sip; public uint UI, Revocation, Choice; public IntPtr File;
        public uint StateAction; public IntPtr State, Url; public uint Flags, Context;
    }
    internal static string Signature(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, memory, false);
            // 禁止联网取证书或弹窗，离线失败必须保留为未核验，不能冒充可信签名。
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UI = 2, Choice = 1, File = memory, Flags = 0x1000 };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            var status = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            if (status != 0) return "签名未通过离线核验（0x" + status.ToString("X8") + "）";
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return "签名通过本机离线核验 · " + certificate.GetNameInfo(X509NameType.SimpleName, false);
        }
        finally { Marshal.DestroyStructure<TrustFile>(memory); Marshal.FreeHGlobal(memory); }
    }
}
