# 应用清单 AppInventory

当前安全限制：1.1 预览代码的注册表写入兼容性尚未通过，配置隔离与恢复已关闭，只保留候选只读预览。完整自检入口仅允许独立 CI 环境，自动工作流当前也已暂停动态验收和发行包上传，仅编译。日常电脑请使用已发布的 1.0 清单版，暂不运行历史 1.1 预览构建或专用卸载测试。

查看 Windows 已安装软件和便携程序候选，汇总名称、版本、路径、占用空间、说明来源，以及其他磁盘上的可能关联目录。

稳定版 **1.0.0** 已发布；当前源码为 **1.1.0-preview.1**，面向 Windows 10/11 x64。清单表格保持只读；预览版增加独立确认的桌面 EXE/MSI、当前用户商店应用卸载，以及目录和配置键的可恢复隔离。现有软件未用于卸载测试。

## 能做什么

- 读取桌面与商店/MSIX 应用，搜索、筛选并按版本、空间和关联数量排序。
- 扫描已就绪固定磁盘或指定目录，可排除目录、整盘及停止扫描；识别便携候选和跨盘关联。
- 展示系统报告空间或目录逻辑大小、说明来源和识别依据；没有可靠用途时明确标注待核实。
- 保存设置与历史快照，导出当前筛选和排序后的 CSV/JSON。
- 使用专用测试应用验证卸载调用、结果确认、残留预览、缓存隔离与恢复。

卸载流程、支持范围和未验收项目见[卸载实施计划](docs/uninstall-roadmap.md)。需要精确系统身份、本次预检、风险确认及输入应用名称。结果未确认、受保护记录、共享/个人目录不开放隔离；恢复不覆盖既有数据。配置隔离仅支持满足名称、安装路径和所有权条件的当前用户键，先加密备份；商店包由 Windows 管理。机器级配置、便携目录删除和永久删除未开放。此改动不替换已发布的 v1.0.0。

关联目录可能包含个人文件；名称匹配不证明归属或可删除，空间也不等于可释放容量。扫描不会运行发现的程序，不自动联网查询或上传软件与路径信息。

## 使用发行包

解压完整的 Windows x64 发行目录，运行 `AppInventory.exe`。主程序自带 .NET 8 运行时；不要只复制一个 EXE。

首次启动读取安装记录。点击“扫描范围…”选择固定磁盘、额外目录或排除项；扫描完成后合并便携候选。完整操作及数据边界见[使用说明](app-inventory/README.md)。

源代码：[skdfndh/AppInventory](https://github.com/skdfndh/AppInventory)。Windows CI 生成的 ZIP 与 SHA256 可在成功运行的 Actions 记录中获取。

## 从源码运行

需要 Windows x64、Git 和能够构建 `net8.0-windows` 的 .NET SDK，推荐 .NET 8 SDK。专用测试组件由 Windows 的 .NET Framework 4.x 编译器构建，需要系统已有该组件；脚本不会自动安装系统功能。

克隆源码后在仓库根目录运行：

```powershell
git clone https://github.com/skdfndh/AppInventory.git
Set-Location AppInventory
.\Initialize-Dependencies.ps1
.\Verify-Inventory.ps1
.\Start-Inventory.ps1 -Development
```

初始化脚本下载 [BCU](https://github.com/Klocman/Bulk-Crap-Uninstaller) 的固定提交 `30da609384c98ba6e35c6530129541ea4ff3970a`，应用 [bcu-core.patch](bcu-core.patch)，再核对实际差异。已有不同版本或其他本地改动时停止，不覆盖源码。首次初始化和 NuGet 还原需要联网。

已完成初始化后，修改代码可直接执行：

```powershell
dotnet build .\app-inventory\AppInventory.csproj -c Release
```

## 测试和打包

```powershell
.\Verify-Inventory.ps1
.\Build-Release.ps1
```

`artifacts/release/` 下每次生成独立时间戳目录，包含自包含 ZIP 和 SHA256 文件。核对最新发布包：

```powershell
$latest = Get-ChildItem .\artifacts\release -Directory | Sort-Object Name -Descending | Select-Object -First 1
$package = Get-ChildItem $latest.FullName -Filter '*.zip' -File | Select-Object -First 1
.\Verify-Release.ps1 -PackagePath $package.FullName
```

验证会从 ZIP 解压副本运行自动检查和真实只读枚举。测试仅运行随包专用组件，新建随机身份的 EXE、x86/x64 MSI 与合成配置键，验证卸载、加密备份、隔离及恢复；不卸载或清理现有软件。GitHub Actions 的临时 Windows 环境额外执行机器级 MSI 与专用签名 MSIX，并回收自己创建的测试证书。本机不导入证书或安装 MSIX。结果保留在 `logs/` 和 `artifacts/package-check/`，不会提交到 Git。

在支持并已启用 Windows 沙箱的电脑上，可先运行 `Prepare-Sandbox.ps1 -PackagePath <ZIP路径>` 生成隔离复验配置。脚本仅准备文件，不启用系统功能或启动沙箱；家庭版和 Docker 限制见[隔离验证说明](docs/uninstall-validation.md)。

Windows CI 运行相同流程并上传 ZIP 与摘要文件。远程 CI 需要在推送到 GitHub 后实际运行，本地构建通过不代表已验证所有 Windows 配置。

## 目录

| 路径 | 用途 |
| --- | --- |
| `app-inventory/` | WPF 应用、清单与扫描逻辑、自动检查 |
| `lab-fixture/` | 专用测试 EXE 源码 |
| `probe/` | 核心库独立验证工具 |
| `bcu-core.patch` | BCU 核心库修改的完整差异 |
| `upstream/` | 初始化后生成的固定版本 BCU 工作副本，不提交 |
| `artifacts/`、`logs/` | 本地构建、验收数据及日志，不提交 |

个人历史验收文档仍可留在本地根目录，但默认不提交。用户设置与快照位于当前 Windows 用户的 `%LOCALAPPDATA%\AppInventory`，不在源码仓库中。JSON 上限为 32 MiB，超限时保留原文件；大清单可以筛选后导出或使用 CSV。

## 参与开发

贡献流程见 [CONTRIBUTING.md](CONTRIBUTING.md)。

## 许可证

本项目原创应用代码使用 [MIT](LICENSE)，版权署名为 AppInventory contributors。BCU 及其修改使用原 Apache-2.0 许可，其他依赖保留各自许可；详情见 [THIRD_PARTY.md](THIRD_PARTY.md)。
