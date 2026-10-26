using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AppInventory;

internal sealed record RegistryDatum(string Name, RegistryValueKind Kind, string Data);
internal sealed record RegistryTree(string Security, List<RegistryDatum> Values, Dictionary<string, RegistryTree> Children);
internal sealed record RegistryResidual(string Id, string Path, string Hash, int Keys, int Values, string Status)
{
    public string Label => Path + " · " + Keys + " 个键 / " + Values + " 个值 · " + Status;
}
internal sealed record RegistryVault(int Schema, string TaskId, string Id, string Path, string Hash, RegistryTree Tree);

internal static class RegistryResiduals
{
    private const uint SecurityFlags = 5;
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegRenameKey(SafeRegistryHandle key, string name, string newName);
    [DllImport("advapi32.dll")] private static extern int RegGetKeySecurity(SafeRegistryHandle key, uint info, byte[] data, ref uint length);
    [DllImport("advapi32.dll")] private static extern int RegSetKeySecurity(SafeRegistryHandle key, uint info, byte[] data);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegCreateKeyEx(SafeRegistryHandle parent, string name, int reserved,
        string className, uint options, int rights, IntPtr security, out SafeRegistryHandle result, out uint disposition);
    [DllImport("ntdll.dll")] private static extern int NtQueryKey(SafeRegistryHandle key, int informationClass, IntPtr data, uint length, out uint required);
    private static readonly string UserSid = ReadUserSid();
    private static string ReadUserSid() { using var user = WindowsIdentity.GetCurrent(); return user.User.Value; }

    internal static string SubKey(string fullPath)
    {
        const string root = "HKEY_CURRENT_USER\\";
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("注册表隔离仅支持当前用户。");
        var subkey = fullPath[root.Length..];
        var parts = subkey.Split('\\');
        var index = parts.Length > 1 && parts[1].Equals("Wow6432Node", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        var forbidden = new[] { "Microsoft", "Windows", "Classes", "Policies", "Clients", "RegisteredApplications" };
        if (!parts[0].Equals("Software", StringComparison.OrdinalIgnoreCase) || parts.Length <= index || parts.Length > 8
            || parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains('/') || part.Any(char.IsControl))
            || forbidden.Contains(parts[index], StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("系统、共享根或不明确的注册表范围不能隔离。");
        return subkey;
    }

    internal static bool Exists(string fullPath)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(SubKey(fullPath), false);
        if (key == null) return false;
        CheckCanonical(key, SubKey(fullPath));
        return true;
    }

    private static void CheckCanonical(RegistryKey key, string subkey)
    {
        NtQueryKey(key.Handle, 3, IntPtr.Zero, 0, out var required);
        if (required < 4 || required > 65536) throw new InvalidOperationException("无法核对注册表键的实际位置。");
        var memory = Marshal.AllocHGlobal((int)required);
        try
        {
            if (NtQueryKey(key.Handle, 3, memory, required, out _) != 0) throw new InvalidOperationException("无法核对注册表键的实际位置。");
            var length = Marshal.ReadInt32(memory);
            if (length < 0 || length > required - 4) throw new InvalidOperationException("注册表位置查询返回无效长度。");
            var actual = Marshal.PtrToStringUni(memory + 4, length / 2);
            if (!string.Equals(actual, "\\REGISTRY\\USER\\" + UserSid + "\\" + subkey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("注册表键经过链接或重定向，拒绝写入。");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    internal static RegistryTree Read(string fullPath, CancellationToken cancellationToken = default)
    {
        var subkey = SubKey(fullPath);
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(subkey, false) ?? throw new InvalidOperationException("配置键已消失。");
        var count = 0;
        long bytes = 0;
        RegistryTree Visit(RegistryKey current, string path, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 16 || ++count > 5000) throw new InvalidOperationException("配置树过大，拒绝隔离。");
            CheckCanonical(current, path);
            uint length = 0; var code = RegGetKeySecurity(current.Handle, SecurityFlags, null, ref length);
            if (code != 122 || length > 65536) throw new InvalidOperationException("无法完整读取配置键权限。");
            var security = new byte[length];
            if (RegGetKeySecurity(current.Handle, SecurityFlags, security, ref length) != 0) throw new InvalidOperationException("无法完整读取配置键权限。");
            var descriptor = new RawSecurityDescriptor(security, 0);
            bytes += security.Length;
            if (bytes > 8 * 1024 * 1024) throw new InvalidOperationException("配置权限数据过大，拒绝隔离。");
            if (descriptor.Owner?.Value != UserSid) throw new InvalidOperationException("配置键不由当前用户所有，保留只读，不隔离。");
            var values = new List<RegistryDatum>();
            foreach (var name in current.GetValueNames().OrderBy(name => name, StringComparer.Ordinal))
            {
                if (++count > 5000) throw new InvalidOperationException("配置值过多，拒绝隔离。");
                var kind = current.GetValueKind(name);
                var value = current.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var data = kind switch
                {
                    RegistryValueKind.String or RegistryValueKind.ExpandString => (string)value,
                    RegistryValueKind.MultiString => JsonSerializer.Serialize((string[])value),
                    RegistryValueKind.Binary or RegistryValueKind.None => Convert.ToBase64String((byte[])value),
                    RegistryValueKind.DWord => ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    RegistryValueKind.QWord => ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => throw new InvalidOperationException("包含不支持的配置值类型，拒绝隔离。")
                };
                if (data == null || data.Length > 1024 * 1024) throw new InvalidOperationException("配置值无效或过大，拒绝隔离。");
                bytes += Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(data);
                if (bytes > 8 * 1024 * 1024) throw new InvalidOperationException("配置总量过大，拒绝隔离。");
                values.Add(new(name, kind, data));
            }
            var children = new Dictionary<string, RegistryTree>(StringComparer.Ordinal);
            foreach (var name in current.GetSubKeyNames().OrderBy(name => name, StringComparer.Ordinal))
            {
                if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\\') || name.Any(char.IsControl))
                    throw new InvalidOperationException("子键名称存在歧义，保留原配置。");
                using var child = current.OpenSubKey(name, false) ?? throw new InvalidOperationException("配置子键无法完整读取。");
                children.Add(name, Visit(child, path + "\\" + name, depth + 1));
            }
            return new(Convert.ToBase64String(security), values, children);
        }
        return Visit(key, subkey, 0);
    }

    internal static string Hash(RegistryTree tree) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(tree, CatalogStorage.JsonOptions)));
    internal static (int Keys, int Values) Count(RegistryTree tree)
    {
        var keys = 1; var values = tree.Values.Count;
        foreach (var child in tree.Children.Values) { var count = Count(child); keys += count.Keys; values += count.Values; }
        return (keys, values);
    }

    internal static string Quarantine(string fullPath, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("配置隔离标识无效。");
        SubKey(fullPath);
        return fullPath[..(fullPath.LastIndexOf('\\') + 1)] + ".AppInventory-Quarantine-" + id;
    }

    internal static void Rename(string from, string to)
    {
        var source = SubKey(from); var destination = SubKey(to);
        var split = source.LastIndexOf('\\'); var parent = source[..split];
        if (split < 0 || !destination[..destination.LastIndexOf('\\')].Equals(parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("配置隔离只能在同一父键内重命名。");
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = root.OpenSubKey(parent, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.FullControl)
            ?? throw new InvalidOperationException("无法取得配置父键权限。");
        CheckCanonical(key, parent);
        using (var current = key.OpenSubKey(source[(split + 1)..], false))
        {
            if (current == null) throw new InvalidOperationException("配置源键不存在。");
            CheckCanonical(current, source);
        }
        if (Exists(to)) throw new InvalidOperationException("配置目标键已存在，拒绝覆盖。");
        var code = RegRenameKey(key.Handle, source[(split + 1)..], destination[(destination.LastIndexOf('\\') + 1)..]);
        if (code != 0) throw new Win32Exception(code, "配置重命名未完成，未执行永久删除。");
    }

    internal static void RestoreBackup(string original, string id, RegistryTree tree)
    {
        if (Exists(original)) throw new InvalidOperationException("配置恢复目标已存在，拒绝覆盖。");
        var temporary = original[..(original.LastIndexOf('\\') + 1)] + ".AppInventory-Restore-" + id;
        var subkey = SubKey(temporary);
        if (Exists(temporary))
        {
            if (Hash(Read(temporary)) != Hash(tree)) throw new InvalidOperationException("配置恢复暂存键尚未完整，保留备份与暂存数据供核对。");
            Rename(temporary, original);
            return;
        }
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        var split = subkey.LastIndexOf('\\'); var parentPath = subkey[..split];
        using var parent = root.OpenSubKey(parentPath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.FullControl)
            ?? throw new InvalidOperationException("配置恢复父键不存在。");
        CheckCanonical(parent, parentPath);
        void Create(RegistryKey owner, string name, string path, RegistryTree node)
        {
            if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\\') || name.Any(char.IsControl))
                throw new InvalidOperationException("备份子键名称无效。");
            var created = RegCreateKeyEx(owner.Handle, name, 0, null, 0, (int)RegistryRights.FullControl, IntPtr.Zero, out var handle, out var disposition);
            if (created != 0) { handle?.Dispose(); throw new Win32Exception(created, "无法创建配置恢复暂存键。"); }
            using var key = RegistryKey.FromHandle(handle, RegistryView.Registry64);
            if (disposition != 1) throw new InvalidOperationException("配置暂存位置已存在，拒绝覆盖。");
            CheckCanonical(key, path);
            foreach (var value in node.Values)
                {
                    object data = value.Kind switch
                    {
                        RegistryValueKind.String or RegistryValueKind.ExpandString => value.Data,
                        RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(value.Data),
                        RegistryValueKind.Binary or RegistryValueKind.None => Convert.FromBase64String(value.Data),
                        RegistryValueKind.DWord => int.Parse(value.Data, System.Globalization.CultureInfo.InvariantCulture),
                        RegistryValueKind.QWord => long.Parse(value.Data, System.Globalization.CultureInfo.InvariantCulture),
                        _ => throw new InvalidOperationException("备份值类型无效。")
                    };
                    key.SetValue(value.Name, data, value.Kind);
                }
            foreach (var child in node.Children) Create(key, child.Key, path + "\\" + child.Key, child.Value);
            var code = RegSetKeySecurity(key.Handle, SecurityFlags, Convert.FromBase64String(node.Security));
            if (code != 0) throw new Win32Exception(code, "暂存配置权限未能完整恢复，保留备份与暂存键。");
        }
        Create(parent, subkey[(split + 1)..], subkey, tree);
        if (Hash(Read(temporary)) != Hash(tree)) throw new InvalidOperationException("暂存配置与备份不一致，保留现有数据。");
        Rename(temporary, original);
    }

    internal static void SetFixtureOwner(RegistryKey key)
    {
        var descriptor = new RawSecurityDescriptor("O:" + UserSid);
        var data = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(data, 0);
        var code = RegSetKeySecurity(key.Handle, 1, data);
        if (code != 0) throw new Win32Exception(code, "测试配置所有者设置失败。");
    }
}
