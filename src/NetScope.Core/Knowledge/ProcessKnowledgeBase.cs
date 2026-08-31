namespace NetScope.Core.Knowledge;

/// <summary>
/// Windows 常见系统进程知识库（静态内置，不联网）。
/// 键为去掉目录与 .exe 扩展名的小写进程名，与性能采样（Toolhelp 快照）和
/// 端口进程识别（Process.ProcessName）给出的进程名口径一致。
/// 未收录的第三方进程由调用方走可执行文件元数据与签名缓存。
/// </summary>
public static class ProcessKnowledgeBase
{
    private static readonly IReadOnlyDictionary<string, ProcessKnowledgeEntry> Map = CreateEntries();

    /// <summary>收录的进程条数。</summary>
    public static int Count => Map.Count;

    /// <summary>全部条目（按收录顺序），供测试与后续检索 UI 使用。</summary>
    public static IReadOnlyList<ProcessKnowledgeEntry> All { get; } = [.. Map.Values];

    /// <summary>按进程名查找；自动去路径、去 .exe 扩展名、忽略大小写。</summary>
    public static bool TryLookup(string? executableName, out ProcessKnowledgeEntry? entry)
    {
        entry = null;
        var key = NormalizeKey(executableName);
        if (key.Length == 0) return false;
        return Map.TryGetValue(key, out entry) && entry is not null;
    }

    /// <summary>进程名归一化为知识库键：取文件名、去 .exe 扩展名、转小写。</summary>
    public static string NormalizeKey(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName)) return string.Empty;
        var fileName = Path.GetFileName(executableName.Trim());
        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) fileName = fileName[..^4];
        return fileName.ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, ProcessKnowledgeEntry> CreateEntries()
    {
        ProcessKnowledgeEntry[] entries =
        [
            new("svchost", "服务宿主", "服务宿主", "Microsoft Corporation",
                "Windows 服务的通用宿主进程。系统同时运行多个 svchost 实例，每个实例承载一组系统服务，属于正常现象。",
                "某个实例占用较高时，通常是它承载的某个服务在工作（如更新、遥测、DHCP），可在“服务”管理器中按服务定位来源。",
                "不建议直接结束，可能中断系统服务；应先定位其承载的具体服务再决定。"),
            new("system", "内核系统进程", "系统关键进程", "Microsoft Corporation",
                "Windows 内核的系统进程，PID 通常固定为 4，承载内核线程（如缓存管理、I/O 完成线程），无磁盘上的可执行文件。",
                "持续高占用通常与驱动或存储子系统相关，可用线程级工具进一步定位内核线程来源。",
                "无法也不应结束。"),
            new("registry", "注册表配置单元进程", "系统关键进程", "Microsoft Corporation",
                "Windows 10 起注册表配置单元由独立进程承载，避免会话与内核访问竞争。",
                "持续高占用多与大量注册表读写（某些软件反复查询）有关。",
                "无法也不应结束。"),
            new("memory compression", "内存压缩进程", "系统组件", "Microsoft Corporation",
                "内存不足时把不活跃内存页压缩存放，相当于用少量 CPU 换取更多可用内存，是 Windows 的正常机制。",
                "占用高说明内存压力大：系统在用压缩换空间，可关注哪些进程的内存持续增长。",
                "无法结束；应关注内存压力的来源进程。"),
            new("system idle process", "空闲进程", "系统组件", "Microsoft Corporation",
                "该进程的 CPU 读数显示的是“剩余空闲容量”而非占用：数值越高说明系统越空闲。",
                "数值高是正常现象；数值持续接近 0 才说明 CPU 被占满。",
                "无法也不应结束。"),
            new("vmmem", "虚拟机内存代理进程", "系统组件", "Microsoft Corporation",
                "Hyper-V / WSL2 等虚拟机的内存代理进程，显示虚拟机整体内存占用，宿主上的虚拟机管理器。",
                "占用高说明虚拟机（或 WSL 发行版）内部内存使用多，应在虚拟机内或 WSL 内定位。",
                "结束它等于强制断电虚拟机；应先在虚拟机内部排查或正常关闭。"),
            new("vmmemwsl", "WSL 虚拟机内存代理进程", "系统组件", "Microsoft Corporation",
                "WSL2 轻量虚拟机的内存代理进程，占用反映 WSL 内 Linux 侧的内存使用。",
                "占用持续偏高通常是 WSL 内进程持有内存；可用 wsl --shutdown 释放并重新进入。",
                "结束它等于强制关闭 WSL；建议用 wsl --shutdown 正常回收。"),
            new("dwm", "桌面窗口管理器", "系统组件", "Microsoft Corporation",
                "负责窗口合成与显示（透明、圆角、动画、截屏都经过它），会话内常驻。",
                "占用偏高常见于大量透明效果/动画、高分屏多窗口或显卡驱动问题。",
                "不建议结束；会黑屏并自动重启，应从显示效果与驱动入手。"),
            new("explorer", "Windows 资源管理器", "系统组件", "Microsoft Corporation",
                "桌面、任务栏、开始菜单与文件窗口的宿主进程，即通常所说的“桌面”。",
                "高占用多与缩略图生成、shell 扩展（第三方右键菜单等）或大量文件窗口相关。",
                "结束后桌面会消失；可通过任务管理器“运行新任务 explorer”恢复。"),
            new("csrss", "客户端/服务器运行时子系统", "系统关键进程", "Microsoft Corporation",
                "Win32 子系统的用户模式核心，进程与控制台的基础设施，会话内关键进程。",
                "持续异常高占用属罕见情况，多与系统损坏相关。",
                "无法结束；结束会触发系统崩溃（蓝屏）。"),
            new("wininit", "Windows 启动初始化", "系统关键进程", "Microsoft Corporation",
                "系统启动早期初始化进程，负责启动服务控制管理器与本地安全机构，之后常驻。",
                "不应出现持续高占用；若 CPU 异常建议排查系统完整性。",
                "无法结束；结束会触发系统崩溃。"),
            new("winlogon", "Windows 登录进程", "系统关键进程", "Microsoft Corporation",
                "处理登录/注销、锁屏与安全注意序列（Ctrl+Alt+Del）的会话关键进程。",
                "正常情况下几乎不占用资源。",
                "无法结束；结束会触发系统崩溃。"),
            new("services", "服务控制管理器", "系统关键进程", "Microsoft Corporation",
                "管理 Windows 服务的启动、停止与依赖关系，svchost 实例由它派生。",
                "持续高占用较少见，多与服务的反复重启有关，可在事件查看器中查服务崩溃记录。",
                "无法结束；结束会触发系统崩溃。"),
            new("lsass", "本地安全机构子系统", "系统关键进程", "Microsoft Corporation",
                "处理本地登录认证、凭据保护与安全策略，系统关键进程。",
                "正常占用很低；若 CPU/网络异常且本进程路径不在 System32，应警惕伪装进程。",
                "无法结束；结束会触发系统崩溃。"),
            new("smss", "会话管理器", "系统关键进程", "Microsoft Corporation",
                "会话 0 的创建者，派生 csrss 与 winlogon，系统启动即存在。",
                "正常情况下无可观测占用。",
                "无法结束；结束会触发系统崩溃。"),
            new("fontdrvhost", "字体驱动宿主", "系统组件", "Microsoft Corporation",
                "用户模式字体驱动宿主，负责字体光栅化与渲染。",
                "占用偏高多与大量字体渲染（设计/排版软件）或损坏字体有关。",
                "不建议结束；应排查异常字体。"),
            new("audiodg", "Windows 音频设备隔离进程", "系统组件", "Microsoft Corporation",
                "承载音频引擎与音效处理（DSP、增强、采样率转换），所有声音都经过它。",
                "占用偏高常见于音频增强效果、高采样率设备或某些应用的异常音频流。",
                "不建议结束；结束后声音中断，且系统会重新拉起。"),
            new("conhost", "控制台窗口宿主", "系统组件", "Microsoft Corporation",
                "命令行程序（cmd、python、node 等）的窗口宿主，由命令行程序自动派生。",
                "随命令行程序出现与退出，本身不应有高占用。",
                "无需结束；关闭对应命令行窗口即可。"),
            new("dllhost", "COM 代理（COM Surrogate）", "系统组件", "Microsoft Corporation",
                "以代理方式运行 COM 组件（常见如资源管理器的缩略图、视频解码），避免宿主崩溃。",
                "短暂峰值属正常（生成缩略图）；若反复占用可关注对应 shell 扩展组件。",
                "可结束但会中断其代理的操作（如缩略图生成），系统会按需再次拉起。"),
            new("sihost", "Shell 基础结构主机", "系统组件", "Microsoft Corporation",
                "承载 Shell 的基础组件（如 UWP 应用磁贴、系统托盘图标宿主）。",
                "偶发偏高多与开始菜单/托盘刷新相关。",
                "不建议结束。"),
            new("taskhostw", "后台任务宿主", "系统组件", "Microsoft Corporation",
                "按需运行计划任务与 COM 后台任务的宿主进程，出现又消失属正常。",
                "周期性占用往往对应计划任务，可在任务计划程序中按时间点对照。",
                "结束后对应任务中断；应通过任务计划程序调整。"),
            new("ctfmon", "文本输入服务", "系统组件", "Microsoft Corporation",
                "输入法与高级文字服务（语音、手写、输入法切换）的常驻进程。",
                "占用通常很低；异常时可重启输入法相关组件。",
                "不建议结束；结束后输入法切换可能失效，系统会重新拉起。"),
            new("runtimebroker", "UWP 权限代理", "系统组件", "Microsoft Corporation",
                "商店应用（UWP）访问系统资源（摄像头、文件、位置）时的权限代理，随应用启停出现。",
                "短暂占用正常；持续偏高可与具体商店应用的权限请求对照。",
                "无需结束，会自行退出。"),
            new("applicationframehost", "UWP 窗口框架宿主", "系统组件", "Microsoft Corporation",
                "承载商店应用窗口的框架（边框、标题栏、生命周期），常驻。",
                "占用与打开的商店应用数量相关，一般很低。",
                "不建议结束；会关闭商店应用窗口框架。"),
            new("startmenuexperiencehost", "开始菜单体验宿主", "系统组件", "Microsoft Corporation",
                "开始菜单（磁贴、搜索框、推荐列表）的宿主进程。",
                "偶发偏高多发生在开始菜单索引刷新时。",
                "结束后开始菜单暂时不可用，系统会重新拉起。"),
            new("shellexperiencehost", "Shell 体验宿主", "系统组件", "Microsoft Corporation",
                "磁贴、通知中心等 Shell 体验组件的宿主进程。",
                "偶发偏高与磁贴刷新、通知批量处理相关。",
                "结束后部分 Shell 体验暂时失效，系统会重新拉起。"),
            new("searchindexer", "Windows 搜索索引", "系统服务", "Microsoft Corporation",
                "为开始菜单/文件搜索建立与维护索引，对文档内容做后台抓取。",
                "磁盘 I/O 与 CPU 偏高多发生在重建索引或大量文件变更后；可在“索引选项”中缩小范围。",
                "不建议结束（会中断索引且自动重启）；应调整索引范围与计划。"),
            new("searchprotocolhost", "搜索协议宿主", "系统服务", "Microsoft Corporation",
                "索引器的子进程：按协议（文件、Outlook 等）枚举待索引项，短暂出现。",
                "随索引器工作出现，本身高占用指向索引范围过大。",
                "无需单独处理，随索引器停止。"),
            new("searchfilterhost", "搜索筛选宿主", "系统服务", "Microsoft Corporation",
                "索引器的子进程：对文件内容做过滤与分词，短暂出现。",
                "同上，指向索引工作量而非自身异常。",
                "无需单独处理。"),
            new("msmpeng", "Windows Defender 反恶意软件引擎", "系统服务", "Microsoft Corporation",
                "Defender 的扫描与实时防护引擎（也含快速学习的机器学习模型），后台扫描时的主力进程。",
                "扫描/更新期间 CPU 与磁盘升高属正常工作状态；频繁占用可考虑调整扫描计划或排除可信开发目录。",
                "不建议结束；结束实时防护会降低系统安全性。"),
            new("nissrv", "Defender 网络检查服务", "系统服务", "Microsoft Corporation",
                "Defender 的网络流量检查引擎，处理入站/出站流量的安全检测。",
                "网络密集（下载、流媒体）时占用随之升高。",
                "不建议结束。"),
            new("wmiprvse", "WMI 提供程序宿主", "服务宿主", "Microsoft Corporation",
                "按需运行 WMI 查询的宿主进程，监控系统读取硬件/系统信息时短暂出现。",
                "反复高占用多与频繁的 WMI 查询（某些软件或脚本）相关。",
                "结束后对应查询失败；应定位查询来源。"),
            new("wudfhost", "用户模式驱动框架宿主", "服务宿主", "Microsoft Corporation",
                "运行用户模式驱动（常见为传感器、指纹、USB 外设类驱动）的宿主进程。",
                "占用与对应设备活动相关；异常时可关注设备驱动。",
                "不建议结束，可能影响对应外设。"),
            new("spoolsv", "打印后台处理服务", "系统服务", "Microsoft Corporation",
                "管理打印队列与打印作业渲染，有打印机/PDF 虚拟打印时常驻。",
                "打印任务积压时 CPU/内存升高；任务卡住时可清空打印队列。",
                "可通过“重启打印服务”清理；直接结束会中断当前打印。"),
            new("onedrive", "Microsoft OneDrive 同步客户端", "Microsoft 预装软件", "Microsoft Corporation",
                "OneDrive 文件夹的同步与按需下载客户端，随登录启动。",
                "同步大文件或初次建立索引时磁盘/网络占用升高。",
                "可通过暂停同步或退出登录停止，不必结束进程。"),
            new("widgets", "Windows 小组件", "Microsoft 预装软件", "Microsoft Corporation",
                "任务栏小组件面板的宿主进程（Windows 11）。",
                "刷新资讯/天气时短暂联网与渲染。",
                "可在任务栏设置中关闭小组件，无需结束进程。"),
        ];

        var map = new Dictionary<string, ProcessKnowledgeEntry>(entries.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var key = NormalizeKey(entry.ExecutableName);
            if (map.ContainsKey(key)) throw new InvalidOperationException($"知识库键重复: {key}");
            map[key] = entry;
        }
        return map;
    }
}
