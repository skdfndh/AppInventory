using System.IO;
using System.Text;
using System.Text.Json;

namespace AppInventory;

internal sealed record InventorySettings(bool AllFixed, List<string> Roots, List<string> Exclusions);
internal sealed record SavedApp(AppRow Data, DirectoryMeasurement Measurement, string Description, string DescriptionSource,
    string DescriptionUrl, List<RelatedLocation> Related, bool Scanned, string AssociationNote);
internal sealed record SavedCatalog(int Schema, DateTimeOffset SavedAt, DateTimeOffset? ScannedAt, List<string> Roots, List<string> Exclusions, List<SavedApp> Apps);

internal static class CatalogStorage
{
    private const int MaximumJsonBytes = 32 * 1024 * 1024;
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppInventory");
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, IgnoreReadOnlyProperties = true };

    internal static T Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        if (new FileInfo(path).Length > MaximumJsonBytes) throw new InvalidDataException("本地记录超过 32 MiB 大小限制，已跳过读取。");
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, JsonOptions) ?? throw new InvalidDataException("本地记录为空。");
    }

    internal static InventorySettings LoadSettings(string path)
    {
        var settings = Read<InventorySettings>(path);
        if (settings == null) return new(true, new(), new());
        if (settings.Roots == null || settings.Exclusions == null
            || settings.Roots.Concat(settings.Exclusions).Any(value => string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)))
            throw new InvalidDataException("保存的扫描范围无效，请重新选择范围。");
        return new(settings.AllFixed, PortableScanner.NormalizeRoots(settings.Roots), PortableScanner.NormalizeRoots(settings.Exclusions));
    }

    internal static SavedCatalog Capture(IEnumerable<AppRow> rows, DateTimeOffset? scannedAt, IEnumerable<string> roots, IEnumerable<string> exclusions = null) => new(1,
        DateTimeOffset.Now, scannedAt, roots.ToList(), exclusions?.ToList() ?? new(), rows.Select(row => new SavedApp(row, row.Measurement, row.Description,
            row.DescriptionSource, row.DescriptionUrl, row.RelatedLocations.ToList(), row.RelatedCount.HasValue, row.AssociationNote)).ToList());

    internal static List<AppRow> Restore(SavedCatalog snapshot)
    {
        if (snapshot == null) return new();
        if (snapshot.Schema != 1 || snapshot.Apps == null || snapshot.Roots == null || snapshot.Exclusions == null || snapshot.Apps.Count > 100_000)
            throw new InvalidDataException("本地快照格式不受支持，已跳过读取。");
        foreach (var saved in snapshot.Apps)
        {
            var row = saved?.Data;
            if (row == null || row.Name == null || row.Version == null || row.InstallPath == null || row.ExecutablePath == null
                || row.Publisher == null || row.Source == null || row.ProductId == null || saved.Related == null
                || saved.Related.Any(location => location == null || location.Path == null || location.Bytes < 0)
                || row.ReportedBytes is < 0 || saved.Measurement is { Bytes: < 0 } or { Files: < 0 } or { Skipped: < 0 }
                || saved.Description == null || saved.DescriptionSource == null)
                throw new InvalidDataException("本地快照包含无效记录，已跳过读取。");
            row.SetDescription(saved.Description, saved.DescriptionSource, saved.DescriptionUrl ?? "");
            if (saved.Measurement != null) row.SetMeasurement(saved.Measurement, row.IsSingleFileCandidate);
            if (saved.Scanned) row.SetRelated(saved.Related, saved.AssociationNote ?? "历史扫描结果，请重新扫描核实。");
        }
        return snapshot.Apps.Select(saved => saved.Data).ToList();
    }

    internal static List<AppRow> MergeHistory(List<AppRow> installed, IReadOnlyList<AppRow> history)
    {
        string Key(AppRow row) => string.Join('\0', row.Source, row.Name, row.Version, row.InstallPath);
        var previous = history.GroupBy(Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in installed)
            if (previous.TryGetValue(Key(row), out var saved))
            {
                if (saved.RelatedCount.HasValue) row.SetRelated(saved.RelatedLocations, saved.AssociationNote);
                if (saved.Measurement != null) row.SetMeasurement(saved.Measurement);
            }
        return installed.Concat(history.Where(row => row.IsPortableCandidate && !installed.Any(current =>
            Path.IsPathFullyQualified(current.InstallPath) && (string.Equals(current.InstallPath, row.InstallPath, StringComparison.OrdinalIgnoreCase)
                || (Path.IsPathFullyQualified(row.ExecutablePath) && PortableScanner.IsWithin(row.ExecutablePath, current.InstallPath)
                    && string.Equals(current.Name, row.Name, StringComparison.OrdinalIgnoreCase)))))).ToList();
    }

    internal static void Save<T>(string path, T data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        // 写入与读取共用上限，避免成功保存一份下次无法恢复的记录。
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes - 3)
            throw new InvalidDataException("JSON 记录超过 32 MiB 限制，原文件未改动。请缩小筛选范围，或使用 CSV 导出。");
        WriteText(path, json);
    }

    internal static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // 先写入独立文件，写入中断时保留上一份完整结果。
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(true))) writer.Write(text);
        File.Move(temporary, path, true);
    }

    internal static string Csv(IEnumerable<AppRow> rows, DateTimeOffset? scannedAt)
    {
        string Cell(string text)
        {
            text ??= "";
            // 软件自报名称也可能包含公式，避免在电子表格中打开时被执行。
            if (text.Length > 0 && (text[0] is '\t' or '\r' or '\n' || text.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@')) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        var csv = new StringBuilder("名称,版本,发布者,来源,路径,主程序,空间字节数,空间显示,空间依据,说明,说明来源,官方链接,关联数量,关联详情,目录扫描时间\r\n");
        foreach (var row in rows)
            csv.AppendLine(string.Join(',', new[] { row.Name, row.Version, row.Publisher, row.Source, row.InstallPath, row.ExecutablePath,
                row.SizeBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", row.SizeDisplay, row.SizeSource, row.Description,
                row.DescriptionSource, row.DescriptionUrl, row.RelatedCount?.ToString() ?? "未扫描",
                string.Join("\n", row.RelatedLocations.Select(location => location.Path + " | " + location.Label + " | " + location.Evidence)),
                scannedAt?.ToString("O") ?? "未扫描" }.Select(Cell)));
        return csv.ToString();
    }
}
