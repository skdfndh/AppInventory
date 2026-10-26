# 卸载验证记录与隔离环境

源码版本：1.1.0-preview.1。正式发布的 v1.0.0 未替换。本轮测试只操作随机标识的专用应用和 MSI，不卸载现有软件。

现有 GitHub Actions 的临时 Windows 虚拟机可以补充自动化隔离测试。检测到托管 CI 环境且已具有管理员令牌时，额外运行计算机级 x86/x64 专用 MSI，核对 Windows Installer 报告的安装上下文；不在本机自动提权，也不替代真实桌面 UAC 或商店验收。具体云端通过情况以 PR 的 CI 结果为准。

商店卸载后端已加入待验收代码，普通入口仍关闭。专用 MSIX 测试仅在临时 CI 中运行：脚本摘要固定在主程序构建中，使用新建随机身份与临时自签名证书，测试结束后移除自己创建的包与信任证书。本机不执行证书导入或 MSIX 安装。该测试通过前不能把商店能力标为可用。

## 已实际执行

- Windows 11 x64 本机：构建、合成界面逻辑、只读枚举、原有 BCU 实验室与新桌面流程回归。
- 专用 EXE：在中文及空格路径下新建当前用户安装记录，实际执行、移出程序并保留备份、移除自己的记录；安装目录外哨兵保留。
- 自建 x86/x64 MSI：随机产品和组件代码，Windows Installer 实际安装与卸载，同时核对精确记录、产品状态和程序文件。仅移除自己创建的产品。
- 失败、退出码 0 但未卸载、派生子进程提前退出、停止等待后后台继续、互斥、命令/文件替换、NoRemove、恢复冲突、DPAPI 记录篡改、共享目录、空目录变化、历史证据变化与中断复核。
- 重启时序采用合成任务记录检查，不能写成真实系统重启通过；新窗口仅做无显示的控件逻辑检查，没有进行桌面自动操作或视觉验收。

构建和验证输出存于已忽略的 logs/artifacts，不公开实际清单、日志、任务记录或历史构建。自动枚举仅输出统计。

## Windows 沙箱

沙箱适合隔离执行一次性的 EXE/MSI 和注册表测试。当前主机是 Windows 11 家庭版，未安装沙箱入口；微软官方不支持家庭版。当前没有启用系统功能、安装家庭版补丁或启动沙箱。

支持并已启用沙箱的电脑，可运行：

```powershell
.\Prepare-Sandbox.ps1 -PackagePath <自包含ZIP路径>
```

脚本核对配套 SHA256 后，在 `artifacts/sandbox-check/<随机标识>/` 下生成配置和输入副本，不自动启动。输入仅包含指定 ZIP、摘要及专用验证脚本，以只读方式映射；结果仅映射到单独的新建 results 目录。网络、剪贴板、音视频和打印机共享关闭；不会映射整个项目、磁盘或个人目录。

在支持沙箱的原电脑上打开生成的 `.wsb`，会在沙箱自身磁盘解压并运行专用组件检查，结果写入专用 results。若搬到其他电脑，须在那台电脑运行准备脚本重新生成绝对路径。`.wsb` 生成与 XML 检查通过不等于沙箱实际运行通过。

沙箱关闭后，内部应用与 DPAPI 用户环境不再可用。隔离恢复应在同一次沙箱会话内验证；跨重启、多系统版本或持久化测试仍应安排可恢复快照的 Windows 虚拟机。商店和 UAC 交互单列验收，自动脚本不宣称覆盖。

## Docker 范围

家庭版 Docker Desktop 只能运行 Linux 容器；Windows 容器不提供交互式桌面，不能替代本项目 WPF、UAC 和商店部署的完整验收。把完整 Windows 虚拟机包装到 Docker 内，实质上仍需要虚拟机环境，不能消除这些前提。

## 未完成的发布门槛

- Windows 10/其他 Windows 11 构建和干净环境的真实界面验收。
- 常见厂商安装器、计算机级提权、真实 UAC 取消及重启后的复核。
- 商店/MSIX 包身份与依赖保护、专用包的实际卸载测试。
- 注册表残留的精确备份/恢复及隔离验证；本版不写注册表残留。
- 权限不足、文件占用、异常中断等在隔离系统中的验证。

## 官方依据

- [Windows 沙箱的版本与许可要求](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox)
- [Windows 沙箱配置文件与共享目录](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)
- [Docker Desktop 的 Windows 要求](https://docs.docker.com/desktop/setup/install/windows-install/)
- [Windows 容器的桌面限制](https://learn.microsoft.com/en-us/virtualization/windowscontainers/quick-start/lift-shift-to-containers)
- [MsiQueryProductState 的状态含义](https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msiqueryproductstatew)
