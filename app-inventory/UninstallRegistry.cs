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
            var leaf = item.FullRegKeyPath[(item.FullRegKeyPath.LastIndexOf('\\') + 1)..];
            var nameMatches = aliases.Contains(SoftwareDescriptions.Normalize(leaf));
            if (_fixture && nameMatches) SelfTest.Progress($"专用配置筛选：置信分={item.Confidence.GetRawConfidence()}，路径证据={item.Confidence.Any(part => part.Equals(ConfidenceRecords.ExplicitConnection))}，当前用户={item.FullRegKeyPath.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase)}");
            if (item.Confidence.GetRawConfidence() < 5 || !item.Confidence.Any(part => part.Equals(ConfidenceRecords.ExplicitConnection))
                || !item.FullRegKeyPath.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase)) continue;
            if (!nameMatches) continue;
            try
            {
                RegistryResiduals.SubKey(item.FullRegKeyPath);
                var tree = RegistryResiduals.Read(item.FullRegKeyPath, cancellationToken);
                var counts = RegistryResiduals.Count(tree);
                candidates.Add(new(Guid.NewGuid().ToString("N"), item.FullRegKeyPath, RegistryResiduals.Hash(tree), counts.Keys, counts.Values, "候选未选择"));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { if (_fixture) SelfTest.Progress("专用配置被读取保护阻止：" + ex.GetType().Name + (ex is InvalidOperationException ? " · " + ex.Message : "")); }
        }
        var previous = RegistryOperations.Where(item => item.Status != "候选未选择").ToList();
        var paths = previous.Where(item => item.Status is "准备配置隔离" or "配置已隔离" or "准备配置恢复" or "已加密备份")
            .Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        previous.AddRange(candidates.Where(item => !paths.Contains(item.Path)).DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase));
        _state = _state with { Registry = previous };
        Record("配置候选预览", $"读取 {candidates.Count} 个符合当前用户、名称、路径与权限条件的配置候选；没有选择或修改键。");
        return RegistryOperations;
    }

    internal void IsolateRegistry(string id, bool acknowledged) => throw new InvalidOperationException("注册表隔离已移除，当前版本仅提供只读预览与加密备份。");

    internal void RestoreRegistry(string id) => throw new InvalidOperationException("注册表自动恢复已移除，请保留已有备份与隔离配置。");

    internal void BackupRegistry(string id, bool acknowledged)
    {
        using var execution = AcquireGlobal();
        using var lease = AcquireTask();
        RequireDesktopResiduals(); RequireVerified();
        if (!acknowledged) throw new InvalidOperationException("必须单独确认将所选配置保存为本机加密备份。");
        var item = RegistryOperations.SingleOrDefault(item => item.Id == id) ?? throw new InvalidOperationException("没有本任务的配置候选。");
        if (item.Status != "候选未选择") throw new InvalidOperationException("配置记录已处理，请先核对已有备份。");
        var tree = RegistryResiduals.Read(item.Path);
        if (RegistryResiduals.Hash(tree) != item.Hash) throw new InvalidOperationException("配置内容或权限已改变，请重新预览。");
        SaveRegistryVault(new(1, State.Id, id, item.Path, item.Hash, tree));
        ReadRegistryVault(item);
        UpdateRegistry(item with { Status = "已加密备份" });
        Record("配置已备份", "已将所选配置完整保存为当前用户加密备份并核对；原注册表键和值均未修改。");
    }

    internal void VerifyRegistryBackup(string id)
    {
        using var lease = AcquireTask();
        var item = RegistryOperations.SingleOrDefault(item => item.Id == id) ?? throw new InvalidOperationException("没有本任务的配置记录。");
        ReadRegistryVault(item);
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
