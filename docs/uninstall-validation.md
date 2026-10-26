# 卸载验证与隔离环境

本版不替换 v1.0.0。完整自检、安装、卸载与专用配置准备只在 GitHub 托管的临时 Windows 环境执行，本机入口拒绝运行；不要手动设置 CI 环境变量绕过保护。

## 当前检查

自动工作流在 Windows Server 2022/2025 分别运行：

- 合成界面逻辑、搜索排序、便携识别、目录大小和跨盘关联。
- 固定摘要的专用 EXE 卸载，包括失败、假成功、派生子进程提前退出、停止等待、命令或文件替换及互斥。
- 随机产品代码的用户级和机器级 x86/x64 MSI，核对产品状态、精确记录、程序文件和保留哨兵。
- 预装 Inno 编译器生成的随机身份用户级包，正式流程卸载后核对精确记录、主程序和哨兵。仅夹具的新标准入口加入无界面及禁止重启参数，普通应用命令不改写。
- 随机身份签名 MSIX 的实际安装与当前用户卸载，回收自己新建的临时信任证书；不在开发电脑导入证书。
- 随机 HKCU 配置键的只读定位、原始值类型及权限读取、加密备份、重载核对、变更与篡改拒绝；不执行注册表重命名、隔离或自动恢复。

`self-test.json.progress.jsonl` 在检查完成及关键阶段开始时刷新落盘。CI 仅保留此合成阶段记录，失败时输出最后阶段；不上传真实软件清单、配置值、诊断转储或历史构建。每次结果以 [PR #1](https://github.com/skdfndh/AppInventory/pull/1) 当前提交的完整矩阵为准，编译成功不算全量验收通过。

历史机器级 MSI 与专用 MSIX 曾在 [运行 40314341766](https://github.com/skdfndh/AppInventory/actions/runs/40314341766) 通过，但不能代替新版本回归。旧版注册表隔离/恢复曾有专用测试记录，后因兼容性问题撤除实现，不作为当前可用能力。

## 尚未验证

服务器 CI 不提供等同于用户桌面的 UAC/视觉验收。真实 Windows 10/11 桌面、真实重启、常见厂商安装器以及更多权限/占用组合仍待独立测试环境验证。重启时序使用合成任务记录，不能写成真实重启通过。注册表写入不在当前发布范围。

## 沙箱与 Docker

Windows 沙箱适合一次性隔离测试，但家庭版不受官方支持。`Prepare-Sandbox.ps1` 仅准备指定包、摘要及隔离配置，不自动启用系统功能或启动沙箱；输入只读、结果单独映射，不映射整个项目或个人目录。当前版本的完整自检只允许 CI，不能把生成 `.wsb` 或 XML 校验通过记为沙箱执行通过。

沙箱会话关闭后，内部 DPAPI 环境失效，不能替代跨重启或持久化验收。Docker Desktop 在家庭版只能运行 Linux 容器；Windows 容器没有交互式桌面，不能替代本项目 WPF/UAC 的完整验收。

## 本机开发

只进行源码审查、脚本语法和编译检查，不在日常电脑复现崩溃或执行卸载夹具。保持稳定版、旧备份及隔离记录；源代码与公开文档之外的数据放在已忽略的 logs/artifacts。

## 官方依据

- [Windows 沙箱要求](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox)
- [沙箱共享目录配置](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)
- [Docker Desktop Windows 要求](https://docs.docker.com/desktop/setup/install/windows-install/)
- [Windows 容器桌面限制](https://learn.microsoft.com/en-us/virtualization/windowscontainers/quick-start/lift-shift-to-containers)
- [MsiQueryProductState](https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msiqueryproductstatew)
