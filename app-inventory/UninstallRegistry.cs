using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UninstallTools;
using UninstallTools.Factory;
using UninstallTools.Junk.Containers;
using UninstallTools.Junk.Confidence;
using UninstallTools.Junk.Finders.Registry;

namespace AppInventory;

internal sealed partial class UninstallWorkflow
{
    internal IReadOnlyList<RegistryResidual> RegistryOperations => State.Registry ?? (IReadOnlyList<RegistryResidual>)Array.Empty<RegistryResidual>();

    internal IReadOnlyList<RegistryResidual> PreviewRegistry(CancellationToken cancellationToken)
    {
        using var execution = AcquireGlobal();
        using var lease = AcquireTask();
        RequireDesktopResiduals(); RequireVerified();
        if (string.IsNullOrWhiteSpace(State.Plan.Installation.InstallPath)) throw new InvalidOperationException("没有系统提供的安装目录，不能建立配置归属依据。");
        cancellationToken.ThrowIfCancellationRequested();
        var target = new ApplicationUninstallerEntry { DisplayName = State.Plan.Installation.Name, Publisher = State.Plan.Installation.Publisher,
            InstallLocation = State.Plan.Installation.InstallPath };
        var installed = new RegistryFactory(Array.Empty<Guid>()).GetUninstallerEntries(_ => { }).ToList();
        installed.Add(target);
        var scanner = new SoftwareRegKeyScanner(); scanner.Setup(installed);
        var aliases = SoftwareDescriptions.Aliases(new AppRow { Name = target.DisplayName }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<RegistryResidual>();
        foreach (var item in scanner.FindJunk(target).OfType<RegistryKeyJunk>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Confidence.GetRawConfidence() < 5 || !item.Confidence.Any(part => part.Equals(ConfidenceRecords.ExplicitConnection))
                || !item.FullRegKeyPath.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase)) continue;
            var leaf = item.FullRegKeyPath[(item.FullRegKeyPath.LastIndexOf('\\') + 1)..];
            if (!aliases.Contains(SoftwareDescriptions.Normalize(leaf))) continue;
            try
            {
                RegistryResiduals.SubKey(item.FullRegKeyPath);
                var tree = RegistryResiduals.Read(item.FullRegKeyPath, cancellationToken);
                var counts = RegistryResiduals.Count(tree);
                candidates.Add(new(Guid.NewGuid().ToString("N"), item.FullRegKeyPath, RegistryResiduals.Hash(tree), counts.Keys, counts.Values, "候选未选择"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        var previous = RegistryOperations.Where(item => item.Status != "候选未选择").ToList();
        var paths = previous.Where(item => item.Status is "准备配置隔离" or "配置已隔离" or "准备配置恢复")
            .Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        previous.AddRange(candidates.Where(item => !paths.Contains(item.Path)).DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase));
        _state = _state with { Registry = previous };
        Record("配置候选预览", $"读取 {candidates.Count} 个符合当前用户、名称、路径与权限条件的配置候选；没有选择或修改键。");
        return RegistryOperations;
    }

    internal void IsolateRegistry(string id, bool acknowledged)
    {
        if (!RegistryResiduals.WritesEnabled && !_fixture) throw new InvalidOperationException("注册表写入正在进行兼容性核验，当前版本仅提供只读预览。");
        using var execution = AcquireGlobal();
        using var lease = AcquireTask();
        RequireDesktopResiduals(); RequireVerified();
        if (!acknowledged) throw new InvalidOperationException("必须单独确认所选配置键可能包含个人设置。");
        var item = RegistryOperations.SingleOrDefault(item => item.Id == id) ?? throw new InvalidOperationException("没有本任务的配置候选。");
        if (item.Status != "候选未选择") throw new InvalidOperationException("该配置键已进入隔离流程。");
        var tree = RegistryResiduals.Read(item.Path);
        if (RegistryResiduals.Hash(tree) != item.Hash) throw new InvalidOperationException("配置内容或权限已改变，请重新预览。");
        var destination = RegistryResiduals.Quarantine(item.Path, id);
        if (RegistryResiduals.Exists(destination)) throw new InvalidOperationException("配置隔离目标已存在，拒绝覆盖。");
        SaveRegistryVault(new(1, State.Id, id, item.Path, item.Hash, tree));
        UpdateRegistry(item with { Status = "准备配置隔离" }); Save();
        RegistryResiduals.Rename(item.Path, destination);
        UpdateRegistry(item with { Status = "配置已隔离" }); Record("配置已隔离", "已备份并重命名一个当前用户配置键；未永久删除，备份值不进入明文报告。");
    }

    internal void RestoreRegistry(string id)
    {
        if (!RegistryResiduals.WritesEnabled && !_fixture) throw new InvalidOperationException("注册表写入正在进行兼容性核验，请保留现有备份与隔离配置，暂不执行恢复。");
        using var execution = AcquireGlobal();
        using var lease = AcquireTask();
        RequireDesktopResiduals();
        var item = RegistryOperations.SingleOrDefault(item => item.Id == id) ?? throw new InvalidOperationException("没有对应配置记录。");
        if (item.Status is not "准备配置隔离" and not "配置已隔离" and not "准备配置恢复") throw new InvalidOperationException("没有可恢复的配置隔离。");
        if (RegistryResiduals.Exists(item.Path)) throw new InvalidOperationException("原配置键已存在，保留两侧数据，不覆盖。");
        var vault = ReadRegistryVault(item);
        var source = RegistryResiduals.Quarantine(item.Path, id);
        var exists = RegistryResiduals.Exists(source);
        if (exists && RegistryResiduals.Hash(RegistryResiduals.Read(source)) != item.Hash)
            throw new InvalidOperationException("隔离配置已变化，保留当前数据与备份，拒绝覆盖恢复。");
        UpdateRegistry(item with { Status = "准备配置恢复" }); Save();
        if (exists) RegistryResiduals.Rename(source, item.Path);
        else RegistryResiduals.RestoreBackup(item.Path, id, vault.Tree);
        if (RegistryResiduals.Hash(RegistryResiduals.Read(item.Path)) != item.Hash) throw new InvalidOperationException("配置恢复后核验不一致，请保留备份检查。");
        UpdateRegistry(item with { Status = "配置已恢复" }); Record("配置已恢复", "已恢复配置值类型与权限，不重新安装程序；原有目标存在时不覆盖。");
    }

    private void UpdateRegistry(RegistryResidual changed) => _state = _state with
    { Registry = RegistryOperations.Select(item => item.Id == changed.Id ? changed : item).ToList() };

    private string RegistryVaultPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("配置备份标识无效。");
        var path = Path.Combine(Root, "registry-" + id + ".bin"); UninstallLab.CheckAncestors(path); return path;
    }

    private void SaveRegistryVault(RegistryVault vault)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(vault, CatalogStorage.JsonOptions);
        if (json.Length > 15 * 1024 * 1024) throw new InvalidOperationException("配置备份超过大小限制，未修改原键。");
        var data = ProtectedData.Protect(json, Encoding.UTF8.GetBytes(State.Id), DataProtectionScope.CurrentUser);
        var path = RegistryVaultPath(vault.Id);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(data); stream.Flush(true);
    }

    private RegistryVault ReadRegistryVault(RegistryResidual item)
    {
        var path = RegistryVaultPath(item.Id);
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidOperationException("配置备份过大。");
        var json = ProtectedData.Unprotect(File.ReadAllBytes(path), Encoding.UTF8.GetBytes(State.Id), DataProtectionScope.CurrentUser);
        var vault = JsonSerializer.Deserialize<RegistryVault>(json, CatalogStorage.JsonOptions);
        if (vault == null || vault.Schema != 1 || vault.Id != item.Id || vault.TaskId != State.Id || vault.Path != item.Path
            || vault.Hash != item.Hash || RegistryResiduals.Hash(vault.Tree) != item.Hash) throw new InvalidOperationException("配置备份与本任务不匹配。");
        return vault;
    }

    private void RecheckRegistryCore()
    {
        foreach (var item in RegistryOperations.Where(item => item.Status is "准备配置隔离" or "准备配置恢复").ToArray())
        {
            var destination = RegistryResiduals.Quarantine(item.Path, item.Id);
            var original = RegistryResiduals.Exists(item.Path); var isolated = RegistryResiduals.Exists(destination);
            if (!original && isolated && RegistryResiduals.Hash(RegistryResiduals.Read(destination)) == item.Hash)
                UpdateRegistry(item with { Status = "配置已隔离" });
            else if (original && !isolated && RegistryResiduals.Hash(RegistryResiduals.Read(item.Path)) == item.Hash)
                UpdateRegistry(item with { Status = item.Status == "准备配置恢复" ? "配置已恢复" : "隔离未发生" });
        }
    }
}
