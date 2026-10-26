using System.IO;
using System.Security;
using System.Xml;
using System.Xml.Linq;
using UninstallTools.Factory;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace AppInventory;

internal sealed record CatalogResult(List<AppRow> Apps, List<string> Warnings);

internal static class ReadOnlyCatalog
{
    public static CatalogResult Load(CancellationToken cancellationToken)
    {
        var apps = new List<AppRow>();
        var warnings = new List<string>();
        cancellationToken.ThrowIfCancellationRequested();
        // 仅使用已核对为只读的注册表工厂，避免完整发现流程启动缓存、启动项或其他扫描。
        try
        {
            var entries = new RegistryFactory(Array.Empty<Guid>()).GetUninstallerEntries(_ => { });
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.SystemComponent || entry.IsUpdate || string.IsNullOrWhiteSpace(entry.DisplayName)) continue;
                var path = entry.InstallLocation?.Trim().Trim('"') ?? "";
                var pathSource = path.Length > 0 ? "安装记录提供" : "未提供安装路径";
                if (path.Length == 0 && !string.IsNullOrWhiteSpace(entry.DisplayIcon))
                {
                    var icon = entry.DisplayIcon.Trim();
                    var comma = icon.LastIndexOf(',');
                    if (comma >= 0 && int.TryParse(icon[(comma + 1)..].Trim(), out _)) icon = icon[..comma];
                    icon = Environment.ExpandEnvironmentVariables(icon.Trim().Trim('"'));
                    try
                    {
                        if (Path.IsPathFullyQualified(icon) && File.Exists(icon))
                        {
                            var parent = Path.GetDirectoryName(icon);
                            if (!string.Equals(parent, Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase))
                            {
                                path = parent ?? "";
                                pathSource = "图标所在目录推断 · 可能不是完整安装目录";
                            }
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
                }
                var kb = entry.EstimatedSize.GetKbSize();
                var executable = ReadIconExecutable(entry.DisplayIcon);
                var row = new AppRow
                {
                    Name = entry.DisplayName, Version = entry.DisplayVersion ?? "", Publisher = entry.Publisher ?? "",
                    InstallPath = path.Length == 0 ? "" : Environment.ExpandEnvironmentVariables(path), PathSource = pathSource,
                    ExecutablePath = executable,
                    UninstallRegistration = UninstallPreflight.Read(entry),
                    ReportedBytes = kb > 0 && kb <= long.MaxValue / 1024 ? kb * 1024 : null
                };
                SoftwareDescriptions.Apply(row, string.IsNullOrWhiteSpace(entry.Comment) ? ExecutableFacts.Read(executable)?.Description : entry.Comment,
                    string.IsNullOrWhiteSpace(entry.Comment) ? "程序文件版本元数据" : "软件安装记录说明");
                apps.Add(row);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("桌面应用读取失败：" + ex.Message); }

        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (package.IsFramework || package.IsResourcePackage || package.IsBundle || package.SignatureKind == PackageSignatureKind.System) continue;
                    var version = package.Id.Version;
                    var path = package.InstalledLocation?.Path ?? "";
                    var row = new AppRow
                    {
                        Name = string.IsNullOrWhiteSpace(package.DisplayName) ? package.Id.Name : package.DisplayName,
                        Version = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}",
                        Publisher = package.PublisherDisplayName ?? "", InstallPath = path,
                        ProductId = package.Id.Name, PathSource = "Windows 包管理接口提供", Source = "商店 / MSIX 应用"
                    };
                    SoftwareDescriptions.Apply(row, ReadPackageDescription(package, path, cancellationToken), "应用包显示说明 / 清单说明");
                    apps.Add(row);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("一个应用包的部分信息不可访问：" + ex.Message); }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add("商店应用读取失败：" + ex.Message); }
        return new CatalogResult(apps.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), warnings);
    }

    internal static string ReadIconExecutable(string icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return "";
        icon = icon.Trim();
        var comma = icon.LastIndexOf(',');
        if (comma >= 0 && int.TryParse(icon[(comma + 1)..].Trim(), out _)) icon = icon[..comma];
        icon = Environment.ExpandEnvironmentVariables(icon.Trim().Trim('"'));
        return Path.IsPathFullyQualified(icon) && Path.GetExtension(icon).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(icon) ? icon : "";
    }

    private static string ReadPackageDescription(Package package, string directory, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            var entries = package.GetAppListEntriesAsync().AsTask(timeout.Token).GetAwaiter().GetResult();
            var description = entries.Select(entry => entry.DisplayInfo.Description).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
            if (!string.IsNullOrWhiteSpace(description)) return description;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException) { }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var reader = XmlReader.Create(Path.Combine(directory, "AppxManifest.xml"), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000
            });
            return XDocument.Load(reader).Descendants().FirstOrDefault(element => element.Name.LocalName == "Description"
                && element.Parent?.Name.LocalName == "Properties")?.Value ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException) { return ""; }
    }
}

internal sealed record DirectoryMeasurement(long Bytes, int Files, int Skipped);

internal static class DirectorySize
{
    public static DirectoryMeasurement Measure(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("应用没有可测量的完整目录路径。");
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("该目录不存在或当前用户无权访问。");
        var boundaries = new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.CommonApplicationData };
        if (new DirectoryInfo(path).Parent == null || boundaries.Any(folder => string.Equals(path,
                Environment.GetFolderPath(folder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("此路径是系统或共享根目录，不能作为单个应用目录测量。");
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("此路径或其父目录包含目录链接，请使用实际安装目录。");

        long bytes = 0;
        var files = 0;
        var skipped = 0;
        var directories = new Stack<string>();
        directories.Push(path);
        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var item in Directory.EnumerateFileSystemEntries(directories.Pop()))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(item);
                        // 不跟随目录联接，避免把其他应用或整个磁盘重复计入当前应用。
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                        if ((attributes & FileAttributes.Directory) != 0) directories.Push(item);
                        else { bytes = checked(bytes + new FileInfo(item).Length); files++; }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { skipped++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { skipped++; }
        }
        return new DirectoryMeasurement(bytes, files, skipped);
    }
}
