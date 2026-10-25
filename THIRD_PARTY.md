# 第三方来源

本项目原创应用代码采用根目录 MIT 许可。以下第三方组件不因根目录许可证而变更其原许可。

| 组件 | 来源与版本 | 许可及本地说明 |
| --- | --- | --- |
| BCU UninstallTools、KlocTools | [官方仓库](https://github.com/Klocman/Bulk-Crap-Uninstaller)，提交 `30da609384c98ba6e35c6530129541ea4ff3970a` | [原 Apache-2.0 许可证](https://github.com/Klocman/Bulk-Crap-Uninstaller/blob/30da609384c98ba6e35c6530129541ea4ff3970a/Licence.txt)；初始化保留原许可证和 NOTICE，修改完整记录在 `bcu-core.patch` |
| TaskScheduler | BCU 引用的 NuGet 依赖 2.12.2 | [原许可证副本](app-inventory/TaskScheduler-LICENSE.md) |
| System.Management、Microsoft .NET 运行时及其他 NuGet 依赖 | 以还原结果和自包含发行包为准 | 打包脚本复制对应包与运行时的许可、NOTICE 及包元数据 |

BCU 代码及其修改继续遵循原许可；本项目是独立宿主，原作者不负责此非官方修改版本。核心修改和使用边界见[第三方修改说明](app-inventory/第三方修改说明.md)。

Git 仓库不提交整个 BCU 克隆或二进制产物。`Initialize-Dependencies.ps1` 固定源码提交并校验补丁，避免混入任意上游更新或覆盖本地修改。自包含发行目录附带本项目 MIT 许可、BCU 许可证与 NOTICE、依赖及运行时声明。
