using NetScope.Core.Models;

namespace NetScope.Core.Knowledge;

/// <summary>
/// 内置进程终止策略（静态，不联网）：按知识库键给出策略等级、功能影响与替代操作。
/// 策略只作为解释证据与等级上限参与评估；恶意程序可以伪装进程名，
/// 最终结论必须结合路径、签名、所有者、会话、服务归属与关键标志共同判断。
/// </summary>
public static class TerminationKnowledge
{
    public sealed record Entry(TerminationPolicy Policy, string Impact, IReadOnlyList<string> Alternatives);

    private static readonly IReadOnlyDictionary<string, Entry> Map = CreateEntries();

    public static bool TryLookup(string? executableName, out Entry entry)
    {
        entry = new Entry(TerminationPolicy.None, "", []);
        if (string.IsNullOrWhiteSpace(executableName)) return false;
        if (!Map.TryGetValue(ProcessKnowledgeBase.NormalizeKey(executableName), out var found) || found is null)
            return false;
        entry = found;
        return found.Policy != TerminationPolicy.None;
    }

    private static IReadOnlyDictionary<string, Entry> CreateEntries() => new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
    {
        // 关键进程：结束即蓝屏或系统不可行
        ["system"] = new Entry(TerminationPolicy.Critical, "内核系统进程，结束会直接导致系统崩溃。", ["无需处理；高占用应定位内核线程来源（如驱动）"]),
        ["registry"] = new Entry(TerminationPolicy.Critical, "注册表配置单元进程，结束会直接导致系统崩溃。", ["重启电脑是唯一恢复方式"]),
        ["csrss"] = new Entry(TerminationPolicy.Critical, "Win32 子系统核心，结束会触发蓝屏。", ["系统文件检查（sfc /scannow）排查异常占用"]),
        ["wininit"] = new Entry(TerminationPolicy.Critical, "系统初始化关键进程，结束会触发蓝屏。", []),
        ["winlogon"] = new Entry(TerminationPolicy.Critical, "登录管理关键进程，结束会触发蓝屏。", []),
        ["smss"] = new Entry(TerminationPolicy.Critical, "会话管理器，结束会触发蓝屏。", []),
        ["services"] = new Entry(TerminationPolicy.Critical, "服务控制管理器，结束会破坏全部 Windows 服务。", ["在服务管理器中定位具体服务"]),
        ["lsass"] = new Entry(TerminationPolicy.Critical, "本地安全授权子系统，受系统保护，结束会触发蓝屏。", []),
        ["memory compression"] = new Entry(TerminationPolicy.Critical, "内存压缩机制，无法结束。", ["关注内存压力来源进程"]),
        ["system idle process"] = new Entry(TerminationPolicy.Critical, "空闲占位进程，无法结束。", []),

        // 系统组件：强烈不建议
        ["dwm"] = new Entry(TerminationPolicy.SystemComponent, "桌面窗口管理器，结束会导致黑屏（随后自动重启）。", ["关闭动画与透明效果", "更新或回滚显卡驱动"]),
        ["audiodg"] = new Entry(TerminationPolicy.SystemComponent, "音频设备图宿主，结束会中断全部声音直到重新播放。", ["在声音设置中排查异常音频设备"]),
        ["fontdrvhost"] = new Entry(TerminationPolicy.SystemComponent, "字体驱动宿主，结束会影响字体渲染。", ["排查第三方字体"]),
        ["dllhost"] = new Entry(TerminationPolicy.SystemComponent, "COM 代理进程，结束会中断其承载的 shell 扩展与组件功能。", ["排查近期安装的 shell 扩展"]),
        ["sihost"] = new Entry(TerminationPolicy.SystemComponent, "Shell 基础设施宿主，结束会影响开始菜单等桌面功能。", []),
        ["taskhostw"] = new Entry(TerminationPolicy.SystemComponent, "后台任务宿主，结束会中断计划的后台任务。", []),
        ["ctfmon"] = new Entry(TerminationPolicy.SystemComponent, "输入法与文本框架，结束会中断输入。", ["重启输入法相关应用恢复"]),
        ["runtimebroker"] = new Entry(TerminationPolicy.SystemComponent, "UWP 激活代理，结束会影响应用商店应用启动。", []),
        ["applicationframehost"] = new Entry(TerminationPolicy.SystemComponent, "UWP 窗口宿主，结束会关闭全部应用商店应用的窗口。", []),
        ["startmenuexperiencehost"] = new Entry(TerminationPolicy.SystemComponent, "开始菜单宿主，结束后开始菜单不可用直到自动重启。", []),
        ["shellexperiencehost"] = new Entry(TerminationPolicy.SystemComponent, "Shell 体验宿主，结束会影响系统 UI 组件。", []),

        // 服务宿主 / 安全软件：强烈不建议
        ["svchost"] = new Entry(TerminationPolicy.ServiceHost, "Windows 服务通用宿主，结束会中断其承载的一组系统服务。", ["在服务管理器中定位该实例承载的服务", "重启对应服务而不是结束宿主"]),
        ["wmiprvse"] = new Entry(TerminationPolicy.ServiceHost, "WMI 提供程序宿主，结束会中断 WMI 查询与依赖它的管理工具。", ["稍后观察；实例通常按需退出"]),
        ["wudfhost"] = new Entry(TerminationPolicy.ServiceHost, "用户模式驱动框架宿主，结束会中断其承载的外设驱动。", ["重新插拔设备恢复"]),
        ["spoolsv"] = new Entry(TerminationPolicy.ServiceHost, "打印后台服务，结束会中断全部打印任务。", ["清空打印队列后重启打印服务"]),
        ["msmpeng"] = new Entry(TerminationPolicy.Security, "Windows Defender 防病毒引擎，结束会中断实时防护。", ["在 Windows 安全中心调整扫描计划", "为开发目录添加排除项（谨慎）"]),
        ["nissrv"] = new Entry(TerminationPolicy.Security, "Defender 网络检测引擎，结束会中断网络防护。", []),

        // 谨慎：可能中断功能或丢失数据
        ["explorer"] = new Entry(TerminationPolicy.UserShell, "桌面、任务栏与文件窗口宿主，结束后桌面消失（可重新拉起）。", ["用任务管理器“运行新任务 explorer”恢复", "先关闭占用高的文件窗口"]),
        ["vmmem"] = new Entry(TerminationPolicy.BackgroundUtility, "虚拟机内存代理，结束等于对虚拟机强制断电，客户机数据可能丢失。", ["在虚拟机内部正常关机", "用虚拟机管理器正常关闭"]),
        ["vmmemwsl"] = new Entry(TerminationPolicy.BackgroundUtility, "WSL2 内存代理，结束等于强制关闭 WSL，未保存工作丢失。", ["执行 wsl --shutdown 正常回收"]),
        ["conhost"] = new Entry(TerminationPolicy.BackgroundUtility, "控制台窗口宿主，结束会同时关闭该控制台中的程序，未保存输出丢失。", ["在控制台内用 Ctrl+C 或 exit 正常退出"]),
        ["searchindexer"] = new Entry(TerminationPolicy.BackgroundUtility, "Windows 搜索索引服务，结束会中断索引直到下次自动重启。", ["暂停索引：设置 → 搜索 Windows", "等待索引完成自然回落"]),
        ["searchprotocolhost"] = new Entry(TerminationPolicy.BackgroundUtility, "搜索协议宿主，与索引器配套。", ["同搜索索引器的处理方式"]),
        ["searchfilterhost"] = new Entry(TerminationPolicy.BackgroundUtility, "搜索过滤宿主，与索引器配套。", ["同搜索索引器的处理方式"]),
        ["onedrive"] = new Entry(TerminationPolicy.BackgroundUtility, "OneDrive 同步客户端，结束会中断正在进行的同步。", ["从托盘菜单暂停同步", "从 OneDrive 设置中正常退出"]),

        // 通常可以结束
        ["widgets"] = new Entry(TerminationPolicy.UserApplication, "小组件宿主，结束后可从任务栏重新打开。", ["从任务栏设置关闭小组件"]),
    };
}
