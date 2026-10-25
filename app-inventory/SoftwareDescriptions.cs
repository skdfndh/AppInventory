using System.Text;
using System.Text.RegularExpressions;

namespace AppInventory;

internal sealed record KnownSoftware(string[] Aliases, string Description, string Url);

internal static class SoftwareDescriptions
{
    // 说明均为官方资料的简短转述，不请求联网，也不上传用户的软件清单或本地路径。
    private static readonly KnownSoftware[] Known =
    {
        new(new[] { "百度网盘", "BaiduNetdisk", "BaiduYun", "BaiduNetdiskDownload", "BaiduYunDownload" }, "百度网盘用于云端文件存储、备份、同步和分享。电脑上的同名下载或同步目录可能存放个人文件，其内容不能仅凭目录名称认定为可清理残留。", "https://yun.baidu.com/disk/help"),
        new(new[] { "腾讯会议", "Tencent Meeting", "wemeetapp" }, "腾讯会议用于线上音视频会议和协作。会议配置、下载文件和录制资料可能不在程序安装目录中，关联位置只提供查找线索。", "https://meeting.tencent.com/"),
        new(new[] { "必剪", "Bcut", "BcutBilibili" }, "必剪是哔哩哔哩提供的剪辑工具，用于视频创作。项目素材、草稿和导出视频可能位于其他目录，不能把它们当作普通程序残留。", "https://bcut.bilibili.cn/"),
        new(new[] { "剪映专业版", "剪映", "JianyingPro", "CapCut" }, "剪映用于视频编辑和内容创作。草稿、素材和导出文件可能独立于安装目录，跨盘发现的同名目录需要区分程序与创作数据。", "https://www.capcut.cn/"),
        new(new[] { "夸克网盘", "QuarkCloudDrive", "QuarkPan" }, "夸克网盘用于云端文件存储、备份、传输和多设备访问。下载、备份目录可能保存用户文件，其大小不代表可以释放的空间。", "https://pan.quark.cn/"),
        new(new[] { "火绒安全软件", "火绒安全", "Huorong", "HipsMain" }, "火绒安全是面向 Windows 的终端安全产品。本工具只展示其软件记录和可能关联目录，不修改防护状态、服务或安全设置。", "https://www.huorong.cn/"),
        new(new[] { "Microsoft OneDrive", "OneDrive" }, "OneDrive 提供云端文件存储、同步与备份。同步目录可能位于其他盘，并可能包含用户的实际文档和照片，不能视为无用残留。", "https://www.microsoft.com/microsoft-365/onedrive/online-cloud-storage"),
        new(new[] { "Microsoft Office", "Microsoft 365", "Office" }, "Microsoft Office 是办公应用套件，通常用于文字处理、电子表格和演示文稿制作。具体包含哪些应用取决于安装版本，文档不属于程序缓存。", "https://www.microsoft.com/microsoft-365"),
        new(new[] { "Microsoft Visio", "Visio" }, "Visio 用于绘制流程图、组织结构图等图表。用户保存的图纸与软件安装文件属于不同的数据范围。", "https://www.microsoft.com/microsoft-365/visio/flowchart-software"),
        new(new[] { "cloudflared" }, "cloudflared 是 Cloudflare Tunnel 的连接程序，用于建立到 Cloudflare 网络的出站连接。此说明只介绍工具用途，不读取其认证资料或启动连接。", "https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/"),
        new(new[] { "NVIDIA 图形驱动程序", "NVIDIA Graphics Driver" }, "该记录对应 NVIDIA 图形驱动软件，用于让操作系统和应用使用相应图形硬件。本清单只展示记录，不判断驱动是否可以移除。", "https://www.nvidia.com/en-us/drivers/"),
        new(new[] { "NVIDIA FrameView SDK", "NVIDIA FrameView" }, "FrameView 相关工具用于测量图形帧率、帧时间和功耗等指标。SDK 组件可能被其他软件使用，不能仅凭存在多个目录就认定为多余。", "https://www.nvidia.com/en-us/geforce/technologies/frameview/"),
        new(new[] { "Microsoft Visual Studio Code", "Visual Studio Code", "VSCode", "Code" }, "Visual Studio Code 是代码编辑器，提供代码编辑、扩展、调试和版本控制集成，常用于多种编程语言的开发。", "https://code.visualstudio.com/docs"),
        new(new[] { "7-Zip", "7zFM", "7zG" }, "7-Zip 是压缩和解压缩工具，支持 7z、ZIP 等格式，并可读取多种归档格式，提供图形界面和命令行工具。", "https://www.7-zip.org/"),
        new(new[] { "Geek Uninstaller", "GeekUninstaller", "geek" }, "Geek Uninstaller 用于卸载 Windows 软件并检查关联残留。本应用只识别和展示它，不调用其卸载或强制移除功能。", "https://geekuninstaller.com/"),
        new(new[] { "Obsidian" }, "Obsidian 是基于本地 Markdown 文件的笔记和知识管理应用，可通过链接组织笔记。它的资料库通常是用户文档，不能作为普通程序缓存处理。", "https://obsidian.md/help/obsidian"),
        new(new[] { "Zotero" }, "Zotero 用于收集、整理、批注和引用研究资料，可管理文献条目并生成参考文献。资料库和附件目录可能包含个人研究数据。", "https://www.zotero.org/"),
        new(new[] { "OBS Studio", "obs64", "obs32" }, "OBS Studio 用于视频录制和直播，支持将窗口、图像、摄像头和声音等来源组合为场景。录像文件通常与程序目录分开保存。", "https://obsproject.com/"),
        new(new[] { "Docker Desktop", "Docker" }, "Docker Desktop 为桌面电脑提供容器开发环境，用于构建和运行容器化应用。镜像、容器和卷的数据可能保存在程序目录之外。", "https://www.docker.com/products/docker-desktop/"),
        new(new[] { "Git" }, "Git 是分布式版本控制系统，用于记录项目文件的变更历史并协作开发。项目中的 .git 目录保存仓库历史，不是软件安装残留。", "https://git-scm.com/"),
        new(new[] { "GitHub CLI", "gh" }, "GitHub CLI 是 GitHub 的命令行工具，可在终端中处理仓库、议题和拉取请求等工作。它与 Git 版本控制程序是不同的工具。", "https://cli.github.com/"),
        new(new[] { "IntelliJ IDEA", "idea64" }, "IntelliJ IDEA 是面向 Java 和 Kotlin 等开发工作的集成开发环境，提供代码分析、运行和调试等功能。项目目录和 IDE 缓存可能与安装目录分开。", "https://www.jetbrains.com/idea/"),
        new(new[] { "Miniconda3", "Miniconda" }, "Miniconda 提供精简的 conda 和 Python 安装，用于创建和管理环境及软件包。不同环境可能保存在其他目录，不能仅按目录名称判断是否可移除。", "https://docs.anaconda.com/miniconda/"),
        new(new[] { "Postman" }, "Postman 用于设计、发送、测试和管理 API 请求，可组织接口集合并查看响应。工作区和配置数据可能不在程序安装目录中。", "https://www.postman.com/product/"),
        new(new[] { "MySQL Server", "MySQL", "mysqld" }, "MySQL 是数据库系统，用于保存和查询结构化数据。数据库数据目录与服务程序目录可能分开，跨盘目录可能是实际业务数据。", "https://www.mysql.com/"),
        new(new[] { "Redis on Windows", "Redis", "redis-server" }, "Redis 是以内存访问为核心的数据存储系统，常用于缓存等场景。Windows 移植版本与上游版本可能不同，本说明仅介绍产品用途，不确认此安装包的维护状态。", "https://redis.io/about/"),
        new(new[] { "nginx" }, "nginx 可作为 HTTP 服务器、反向代理、内容缓存和负载均衡器。配置、网站内容和日志可能分布在程序目录之外。", "https://nginx.org/en/"),
        new(new[] { "Node.js", "node" }, "Node.js 是 JavaScript 运行时，常用于网络应用、开发工具和脚本。不同项目可以各自包含依赖，项目依赖目录不等同于运行时本体。", "https://nodejs.org/en/about"),
        new(new[] { "Python", "pythonw" }, "Python 是通用编程语言及其运行环境，常用于脚本、数据处理和应用开发。虚拟环境及项目文件可能独立于解释器安装目录存在。", "https://www.python.org/about/"),
        new(new[] { "Google Chrome", "Chrome" }, "Google Chrome 是网页浏览器，用于访问网站和运行网页应用。用户配置、扩展和下载内容可能保存在程序目录之外。", "https://www.google.com/chrome/"),
        new(new[] { "Steam" }, "Steam 是游戏分发和社区平台，提供游戏安装、更新和社区功能。游戏库可以位于其他磁盘，不能将库目录视为无用残留。", "https://store.steampowered.com/about/"),
        new(new[] { "SumatraPDF", "Sumatra PDF" }, "SumatraPDF 是 Windows 文档阅读器，支持 PDF、电子书和漫画等格式，并提供便携版本。阅读的文档通常不属于程序本身。", "https://www.sumatrapdfreader.org/free-pdf-reader"),
        new(new[] { "Typora" }, "Typora 是 Markdown 编辑器和阅读器，提供实时预览式写作界面，支持常见文档元素。用户编写的笔记和文档不应与程序目录混为一谈。", "https://typora.io/"),
        new(new[] { "DBeaver" }, "DBeaver 是数据库管理工具，可连接和浏览多种数据库，并提供 SQL 编辑和数据查看功能。工作区及连接配置可能位于安装目录之外。", "https://dbeaver.io/"),
        new(new[] { "LibreOffice" }, "LibreOffice 是办公套件，包含文字处理、电子表格和演示文稿等工具。用户文档与软件安装目录属于不同的数据范围。", "https://www.libreoffice.org/"),
        new(new[] { "Microsoft .NET SDK", "Microsoft Windows Desktop Runtime", "Microsoft .NET Runtime" }, ".NET SDK 用于开发和构建 .NET 应用，运行时用于执行相应应用。不同版本可以并存，本界面只展示记录，不判断某个版本是否可删除。", "https://learn.microsoft.com/en-us/dotnet/core/introduction"),
        new(new[] { "Microsoft Visual C++" }, "Visual C++ 可再发行组件为使用相应工具链构建的软件提供运行库。不同架构和版本可能同时被不同程序使用，不能仅凭名称相似就合并或认定为多余。", "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist")
    };

    public static KnownSoftware Find(string name)
    {
        return Known.FirstOrDefault(software => software.Aliases.Any(alias =>
            name.Equals(alias, StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith(alias, StringComparison.OrdinalIgnoreCase) && name.Length > alias.Length
                && (char.IsWhiteSpace(name[alias.Length]) || char.IsDigit(name[alias.Length]) || name[alias.Length] == '('))));
    }

    public static IEnumerable<string> Aliases(AppRow row)
    {
        var names = new List<string> { row.Name };
        if (!string.IsNullOrWhiteSpace(row.ProductId)) names.Add(row.ProductId);
        if (!string.IsNullOrWhiteSpace(row.ExecutablePath)) names.Add(System.IO.Path.GetFileNameWithoutExtension(row.ExecutablePath));
        if (!string.IsNullOrWhiteSpace(row.InstallPath)) names.Add(System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(row.InstallPath)));
        var known = Find(row.Name);
        if (known != null) names.AddRange(known.Aliases);
        return names.Select(Normalize).Where(IsSpecific).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static string Normalize(string value)
    {
        value = Regex.Replace(value ?? "", @"[\s_\-]+(?:v?\d+(?:\.\d+)+|x64|x86|portable|便携版).*?$", "", RegexOptions.IgnoreCase);
        return new string(value.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static bool IsSpecific(string value) => value.Length >= 2 && !new[]
    {
        "microsoft", "windows", "programs", "programfiles", "programfilesx86", "programdata", "application", "applications", "portable", "software", "download", "downloads", "cache", "data", "update", "setup", "install", "tools", "bin"
    }.Contains(value);

    public static void Apply(AppRow row, string localDescription = "", string localSource = "应用自身元数据")
    {
        var text = (localDescription ?? "").Trim();
        var known = Find(row.Name) ?? Find(System.IO.Path.GetFileNameWithoutExtension(row.ExecutablePath));
        if (text.Length >= 18 && !text.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) && !text.Equals(row.Name, StringComparison.OrdinalIgnoreCase))
            row.SetDescription(text, localSource + " · 软件自报，未独立核实");
        else if (known != null)
            row.SetDescription(known.Description, "官方介绍摘要 · 按名称匹配 · 核对于 2026-10-25", known.Url);
        else
        {
            var details = text.Length > 0 && !text.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) ? $"本机自报说明：{text}。" : "本机没有提供具体用途说明。";
            row.SetDescription($"{row.Name} 是一条{row.Source}记录，版本为{row.VersionDisplay}，{row.PublisherDisplay}。{details}用途尚待核实，不能仅根据文件名判断其功能。", "本机安装记录 / 文件元数据 · 用途待核实");
        }
    }
}
