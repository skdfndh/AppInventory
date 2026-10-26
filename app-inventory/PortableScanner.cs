using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Security;

namespace AppInventory;

internal sealed record ScanScope(IReadOnlyList<string> Roots, IReadOnlyList<string> Exclusions);
internal sealed record ScanProgress(int Directories, int Files, string CurrentPath);
internal sealed record FolderSnapshot(string Path, long Bytes, int Files, int Skipped);
internal sealed record PortableScanResult(List<AppRow> Candidates, Dictionary<string, List<FolderSnapshot>> Folders, int Directories, int Files, int Skipped, List<string> Roots);
internal sealed record ExecutableFacts(string Path, string Name, string Version, string Publisher, string Description, long Bytes)
{
    public static ExecutableFacts Read(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            if (pe.PEHeaders.PEHeader == null) return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            var name = string.IsNullOrWhiteSpace(info.ProductName) ? info.FileDescription : info.ProductName;
            return new ExecutableFacts(path, string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileNameWithoutExtension(path) : name.Trim(),
                info.ProductVersion?.Trim() ?? info.FileVersion?.Trim() ?? "", info.CompanyName?.Trim() ?? "",
                string.IsNullOrWhiteSpace(info.Comments) ? info.FileDescription ?? "" : info.Comments, stream.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or BadImageFormatException or ArgumentException) { return null; }
    }
}

internal static class PortableScanner
{
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "Recovery", "$Windows.~BT", "$Windows.~WS", ".git", ".svn", "node_modules", ".venv", "venv", "__pycache__", "portable-fixtures", "read-only-fixtures"
    };
    private static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase)
    {
        "tools", "apps", "programs", "portable", "portableapps", "software", "downloads", "软件", "工具", "应用软件", "下载", "桌面", "desktop"
    };
    private static readonly string[] DataSuffixes = { "downloads", "download", "backup", "cache", "data", "old", "portable", "tmp", "temp", "备份", "缓存", "下载", "旧版", "临时", "临时文件" };

    public static List<string> FixedDrives() => DriveInfo.GetDrives()
        .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed).Select(drive => drive.RootDirectory.FullName).ToList();

    public static bool IsWithin(string path, string parent)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static List<string> NormalizeRoots(IEnumerable<string> roots)
    {
        var result = new List<string>();
        foreach (var root in roots.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length))
            if (!result.Any(parent => IsWithin(root, parent))) result.Add(root);
        return result;
    }

    public static PortableScanResult Scan(ScanScope scope, IReadOnlyList<AppRow> installed, IProgress<ScanProgress> progress, CancellationToken token)
    {
        var roots = NormalizeRoots(scope.Roots);
        var exclusions = NormalizeRoots(scope.Exclusions.Concat(new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), UninstallLab.LabRoot, UninstallWorkflow.TaskRoot }).Where(path => !string.IsNullOrWhiteSpace(path)));
        var candidates = new List<AppRow>();
        var folders = new Dictionary<string, List<FolderSnapshot>>(StringComparer.OrdinalIgnoreCase);
        var installedPaths = installed.Where(row => !row.IsPortableCandidate && Path.IsPathFullyQualified(row.InstallPath))
            .Select(row => Path.TrimEndingDirectorySeparator(Path.GetFullPath(row.InstallPath))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directories = 0;
        var files = 0;
        var skipped = 0;
        var lastReport = Stopwatch.StartNew();

        FolderSnapshot Visit(string path, bool coveredByApp, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > 512 || exclusions.Any(exclusion => IsWithin(path, exclusion))) { skipped++; return new(path, 0, 0, 1); }
            var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            if (ExcludedNames.Contains(folderName) || folderName.StartsWith(".AppInventory-Quarantine-", StringComparison.OrdinalIgnoreCase)) { skipped++; return new(path, 0, 0, 1); }
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { skipped++; return new(path, 0, 0, 1); }
            }
            catch (Exception ex) when (IsAccessError(ex)) { skipped++; return new(path, 0, 0, 1); }

            directories++;
            if (lastReport.ElapsedMilliseconds > 150)
            {
                progress?.Report(new ScanProgress(directories, files, path));
                lastReport.Restart();
            }
            // 安装缓存仍需统计和关联，但其中的安装资源不能作为独立便携软件新增。
            coveredByApp |= installedPaths.Contains(Path.TrimEndingDirectorySeparator(path))
                || folderName.Equals("Package Cache", StringComparison.OrdinalIgnoreCase);
            long bytes = 0;
            var count = 0;
            var missed = 0;
            var children = new List<string>();
            var executables = new List<string>();
            try
            {
                foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        // 枚举结果已有属性和大小，复用它们以减少全盘检查中的重复元数据查询。
                        var attributes = item.Attributes;
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { missed++; skipped++; continue; }
                        if ((attributes & FileAttributes.Directory) != 0) children.Add(item.FullName);
                        else
                        {
                            bytes = checked(bytes + ((FileInfo)item).Length);
                            count++; files++;
                            if (!coveredByApp && item.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && IsMainExecutableName(item.Name)) executables.Add(item.FullName);
                        }
                    }
                    catch (Exception ex) when (IsAccessError(ex)) { missed++; skipped++; }
                }
            }
            catch (Exception ex) when (IsAccessError(ex)) { missed++; skipped++; }

            var identified = executables.Select(ExecutableFacts.Read).Where(info => info != null && IsMainExecutableName(info.Name)).ToList();
            var container = new DirectoryInfo(path).Parent == null || Containers.Contains(folderName);
            var ownRows = new List<AppRow>();
            if (!coveredByApp && identified.Count > 0)
            {
                var chosen = container ? identified : identified.OrderBy(info =>
                    SoftwareDescriptions.Normalize(Path.GetFileNameWithoutExtension(info.Path)) == SoftwareDescriptions.Normalize(folderName) ? 0 : 1).Take(1);
                foreach (var info in chosen)
                {
                    var row = new AppRow
                    {
                        Name = info.Name, Version = info.Version, Publisher = info.Publisher, InstallPath = path,
                        ExecutablePath = info.Path, Source = "便携 / 未注册候选", IsPortableCandidate = true, IsSingleFileCandidate = container,
                        PathSource = container ? "扫描到的单文件程序所在目录" : "扫描到的程序目录",
                        RecognitionEvidence = "已读取有效 PE 文件头及版本元数据，未运行程序。未发现安装记录不等于已确认便携；同目录辅助程序按主程序候选合并。"
                    };
                    SoftwareDescriptions.Apply(row, info.Description, "可执行文件版本元数据");
                    if (container) row.SetMeasurement(new DirectoryMeasurement(info.Bytes, 1, 0), true);
                    ownRows.Add(row);
                    candidates.Add(row);
                }
            }
            foreach (var child in children)
            {
                var nested = Visit(child, coveredByApp || (!container && ownRows.Count > 0), depth + 1);
                bytes = checked(bytes + nested.Bytes); count += nested.Files; missed += nested.Skipped;
            }
            var snapshot = new FolderSnapshot(path, bytes, count, missed);
            var key = SoftwareDescriptions.Normalize(folderName);
            if (key.Length >= 2)
            {
                if (!folders.TryGetValue(key, out var indexed)) folders[key] = indexed = new List<FolderSnapshot>();
                indexed.Add(snapshot);
            }
            if (!container) foreach (var row in ownRows) row.SetMeasurement(new DirectoryMeasurement(bytes, count, missed));
            return snapshot;
        }

        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            // 显式根目录也不跟随链接，避免把范围外的数据带入扫描。
            var hasLink = false;
            try
            {
                for (var parent = new DirectoryInfo(root); parent != null; parent = parent.Parent)
                    if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) { hasLink = true; break; }
            }
            catch (Exception ex) when (IsAccessError(ex)) { hasLink = true; }
            if (hasLink) { skipped++; continue; }
            Visit(root, installedPaths.Any(parent => IsWithin(root, parent)) || IsInstallerCachePath(root), 0);
        }
        progress?.Report(new ScanProgress(directories, files, "扫描完成"));
        return new PortableScanResult(candidates, folders, directories, files, skipped, roots);
    }

    public static List<AppRow> Merge(IReadOnlyList<AppRow> installed, PortableScanResult scan)
    {
        var result = installed.ToList();
        var addedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var executableIndex = new Dictionary<string, List<AppRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in scan.Candidates)
        {
            if (!addedFiles.Add(candidate.ExecutablePath)) continue;
            foreach (var alias in new[] { SoftwareDescriptions.Normalize(candidate.Name), SoftwareDescriptions.Normalize(Path.GetFileNameWithoutExtension(candidate.ExecutablePath)) }.Distinct())
            {
                if (!executableIndex.TryGetValue(alias, out var indexed)) executableIndex[alias] = indexed = new List<AppRow>();
                indexed.Add(candidate);
            }
            var registered = installed.Any(row => Path.IsPathFullyQualified(row.InstallPath)
                && (string.Equals(row.InstallPath.TrimEnd('\\'), candidate.InstallPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    || (IsWithin(candidate.ExecutablePath, row.InstallPath) && SoftwareDescriptions.Normalize(row.Name) == SoftwareDescriptions.Normalize(candidate.Name))));
            if (!registered) result.Add(candidate);
        }
        foreach (var row in result)
        {
            var related = new Dictionary<string, RelatedLocation>(StringComparer.OrdinalIgnoreCase);
            foreach (var alias in SoftwareDescriptions.Aliases(row))
            {
                if (executableIndex.TryGetValue(alias, out var copies))
                {
                    foreach (var copy in copies)
                    {
                        if (Path.IsPathFullyQualified(row.InstallPath) && IsWithin(copy.ExecutablePath, row.InstallPath)) continue;
                        var path = copy.IsSingleFileCandidate ? copy.ExecutablePath : copy.InstallPath;
                        var otherDrive = Path.IsPathFullyQualified(row.InstallPath) && !string.Equals(Path.GetPathRoot(path), Path.GetPathRoot(row.InstallPath), StringComparison.OrdinalIgnoreCase);
                        related[path] = new RelatedLocation(path, copy.SizeBytes ?? 0, copy.SizeDisplay.StartsWith("≥ "), "程序副本候选",
                            "可执行文件的产品名或文件名匹配该软件；已读取版本元数据，未运行或验证完整功能。", otherDrive);
                    }
                }
                foreach (var key in new[] { alias }.Concat(DataSuffixes.Select(suffix => alias + suffix)))
                {
                    if (!scan.Folders.TryGetValue(key, out var matches)) continue;
                    foreach (var folder in matches)
                    {
                        if (Path.IsPathFullyQualified(row.InstallPath) && (IsWithin(folder.Path, row.InstallPath) || IsWithin(row.InstallPath, folder.Path))) continue;
                        var suffix = key[alias.Length..];
                        var isTemporary = suffix is "tmp" or "temp" or "临时" or "临时文件";
                        var otherDrive = Path.IsPathFullyQualified(row.InstallPath) && !string.Equals(Path.GetPathRoot(folder.Path), Path.GetPathRoot(row.InstallPath), StringComparison.OrdinalIgnoreCase);
                        if (related.ContainsKey(folder.Path)) continue;
                        related[folder.Path] = new RelatedLocation(folder.Path, folder.Bytes, folder.Skipped > 0,
                            isTemporary ? "临时 / 缓存目录候选" : suffix.Length > 0 ? "下载 / 数据目录候选" : "名称匹配目录候选",
                            $"目录名称匹配软件标识“{alias}”；归属尚未验证，可能包含个人文件。", otherDrive);
                    }
                }
            }
            row.SetRelated(related.Values.OrderByDescending(location => location.OtherDrive).ThenBy(location => location.Path).ToList(),
                $"已检查 {scan.Roots.Count} 个范围、{scan.Directories:N0} 个目录，跳过 {scan.Skipped:N0} 处排除项、链接或不可访问位置。仅为名称关联，未发现候选不代表不存在；目录大小不应直接相加。 ");
        }
        return result.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal static bool IsMainExecutableName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return !new[] { "unins", "uninst", "setup", "installer", "updater", "crashpad", "crashreport", "qtwebengine", "vcredist", "vc_redist", "安装程序", "安装向导", "安装包", "卸载程序", "更新程序" }.Any(name.Contains)
            && name is not "update" and not "install" and not "7z";
    }

    private static bool IsInstallerCachePath(string path) => Path.GetFullPath(path)
        .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part.Equals("Package Cache", StringComparison.OrdinalIgnoreCase));

    private static bool IsAccessError(Exception error) => error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException;
}
