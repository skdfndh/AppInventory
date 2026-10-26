using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;

namespace AppInventory;

internal sealed class AppRow : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string InstallPath { get; init; } = "";
    public string PathSource { get; init; } = "未提供安装路径";
    public string Source { get; init; } = "桌面应用";
    public string ExecutablePath { get; init; } = "";
    public string ProductId { get; init; } = "";
    [JsonIgnore]
    public string PackageFullName { get; init; } = "";
    // 卸载入口只来自本次系统读取，不能由历史快照重新授权。
    [JsonIgnore]
    public UninstallRegistration UninstallRegistration { get; init; }
    [JsonIgnore]
    public UninstallPreview UninstallPreview => UninstallPreflight.Create(this);
    public bool IsPortableCandidate { get; init; }
    public bool IsSingleFileCandidate { get; init; }
    public string RecognitionEvidence { get; init; } = "来自 Windows 安装记录。";
    public string Subtitle => Source + " · " + PublisherDisplay;
    public string Description => _description.Length > 0 ? _description : $"{Name} 是一条{Source}记录。当前没有可核实的用途说明。";
    public string DescriptionSource => _descriptionSource;
    public string DescriptionUrl => _descriptionUrl;
    public IReadOnlyList<RelatedLocation> RelatedLocations => _relatedLocations;
    public string RelatedSummary => _associationScanned ? $"{_relatedLocations.Count} 个候选" : "未扫描";
    public int? RelatedCount => _associationScanned ? _relatedLocations.Count : null;
    public string AssociationNote => _associationNote;
    public long? ReportedBytes { get; init; }
    public string Initial => Name.Length == 0 ? "·" : StringInfo.GetNextTextElement(Name).ToUpperInvariant();
    public string VersionDisplay => string.IsNullOrWhiteSpace(Version) ? "未提供" : Version;
    public string PublisherDisplay => string.IsNullOrWhiteSpace(Publisher) ? "发布者未提供" : Publisher;
    public string PathDisplay => string.IsNullOrWhiteSpace(InstallPath) ? "未提供安装路径" : InstallPath;
    public long? SizeBytes => _measuredBytes ?? ReportedBytes;
    public string SizeDisplay => (_partial ? "≥ " : "") + FormatSize(SizeBytes);
    public string ReportedSizeDisplay => FormatSize(ReportedBytes);
    public string SizeSource => _measuredBytes.HasValue
        ? (_partial ? _sizeOrigin + " · 部分扫描" : _sizeOrigin)
        : (ReportedBytes.HasValue ? "Windows 安装记录报告值" : "系统未提供大小，可测量目录");
    public string MeasurementNote => _measurementNote;
    internal DirectoryMeasurement Measurement => _measuredBytes.HasValue ? new(_measuredBytes.Value, _measuredFiles, _measuredSkipped) : null;
    private int _measuredFiles, _measuredSkipped;
    private long? _measuredBytes;
    private bool _partial;
    private string _measurementNote = "目录大小不等于可释放空间，共享文件与目录外缓存不在统计范围内。";
    private string _sizeOrigin = "目录逻辑大小 · 不含目录外数据";
    private string _description = "", _descriptionSource = "本机记录 · 用途待核实", _descriptionUrl = "";
    private IReadOnlyList<RelatedLocation> _relatedLocations = Array.Empty<RelatedLocation>();
    private bool _associationScanned;
    private string _associationNote = "尚未扫描其他位置。候选目录可能包含程序副本、缓存或个人文件。";
    public event PropertyChangedEventHandler PropertyChanged;

    public bool Matches(string query) => query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
        .All(term => new[] { Name, Version, Publisher, InstallPath, ExecutablePath, Source, Description, DescriptionSource }
            .Any(value => value.Contains(term, StringComparison.OrdinalIgnoreCase))
            || _relatedLocations.Any(location => location.Path.Contains(term, StringComparison.OrdinalIgnoreCase)));

    public void SetMeasurement(DirectoryMeasurement result, bool singleFile = false)
    {
        _sizeOrigin = singleFile ? "单个可执行文件大小 · 未推断相邻文件" : "目录逻辑大小 · 不含目录外数据";
        _measuredBytes = result.Bytes;
        _partial = result.Skipped > 0;
        _measuredFiles = result.Files;
        _measuredSkipped = result.Skipped;
        _measurementNote = $"已读取 {result.Files:N0} 个文件。"
            + (result.Skipped > 0 ? $"跳过 {result.Skipped:N0} 个不可访问条目或链接，当前值仅为下限。" : "已跳过目录外数据。")
            + "此数值不代表卸载后可释放的空间。";
        foreach (var property in new[] { nameof(SizeBytes), nameof(SizeDisplay), nameof(SizeSource), nameof(MeasurementNote) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public static string FormatSize(long? bytes)
    {
        if (!bytes.HasValue) return "未提供";
        var value = (double)bytes.Value;
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "N0" : "N1", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public void SetDescription(string description, string source, string url = "")
    {
        _description = description;
        _descriptionSource = source;
        _descriptionUrl = url;
        foreach (var name in new[] { nameof(Description), nameof(DescriptionSource), nameof(DescriptionUrl) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void SetRelated(IReadOnlyList<RelatedLocation> locations, string note)
    {
        _relatedLocations = locations;
        _associationScanned = true;
        _associationNote = note;
        foreach (var name in new[] { nameof(RelatedLocations), nameof(RelatedSummary), nameof(RelatedCount), nameof(AssociationNote) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

internal sealed record RelatedLocation(string Path, long Bytes, bool Partial, string Kind, string Evidence, bool OtherDrive)
{
    public string SizeDisplay => (Partial ? "≥ " : "") + AppRow.FormatSize(Bytes);
    public string Label => (OtherDrive ? "其他盘 · " : "其他位置 · ") + Kind + " · " + SizeDisplay;
}

internal sealed class AppRowComparer(string member, bool descending) : IComparer
{
    public int Compare(object x, object y)
    {
        var left = (AppRow)x;
        var right = (AppRow)y;
        int result;
        if (member == nameof(AppRow.SizeBytes) || member == nameof(AppRow.RelatedCount))
        {
            // 未提供大小的应用始终放在末尾，数值排序不依赖显示单位。
            var aValue = member == nameof(AppRow.SizeBytes) ? left.SizeBytes : left.RelatedCount;
            var bValue = member == nameof(AppRow.SizeBytes) ? right.SizeBytes : right.RelatedCount;
            if (!aValue.HasValue || !bValue.HasValue)
                return aValue.HasValue ? -1 : bValue.HasValue ? 1 : CompareNames(left, right);
            result = aValue.Value.CompareTo(bValue.Value);
        }
        else if (member == nameof(AppRow.Version))
        {
            if (string.IsNullOrWhiteSpace(left.Version) || string.IsNullOrWhiteSpace(right.Version))
                return string.IsNullOrWhiteSpace(left.Version) ? string.IsNullOrWhiteSpace(right.Version) ? CompareNames(left, right) : 1 : -1;
            var aParsed = System.Version.TryParse(left.Version, out var a);
            var bParsed = System.Version.TryParse(right.Version, out var b);
            // 混合数字版本和自定义版本时先分组，避免比较规则产生循环顺序。
            if (aParsed != bParsed) return aParsed ? -1 : 1;
            result = aParsed ? a.CompareTo(b) : StringComparer.CurrentCultureIgnoreCase.Compare(left.Version, right.Version);
        }
        else
        {
            var aText = member == nameof(AppRow.InstallPath) ? left.InstallPath : member == nameof(AppRow.Version) ? left.Version : left.Name;
            var bText = member == nameof(AppRow.InstallPath) ? right.InstallPath : member == nameof(AppRow.Version) ? right.Version : right.Name;
            result = StringComparer.CurrentCultureIgnoreCase.Compare(aText, bText);
        }
        return result == 0 ? CompareNames(left, right) : descending ? -Math.Sign(result) : Math.Sign(result);
    }

    private static int CompareNames(AppRow left, AppRow right) => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
}
