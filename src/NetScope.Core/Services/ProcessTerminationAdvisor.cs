using NetScope.Core.Models;

namespace NetScope.Core.Services;

/// <summary>
/// 终止建议纯规则引擎（V1.1）：输入只读事实快照，输出分级建议。
///
/// 决策顺序固定（与设计文档 §5 一致）：
/// 硬性禁止 → 未知阻断 → 系统/服务高风险 → 数据与功能中断风险 → 普通用户应用。
/// 任何未知字段都向更保守等级收敛；签名有效只证明发布者与文件完整性，
/// 不能直接得出“可以结束”；高 CPU 或影响排行只解释动机，不降低风险等级。
/// </summary>
public static class ProcessTerminationAdvisor
{
    public static TerminationAssessment Assess(TerminationFacts facts)
    {
        var evidence = new List<string>();
        var unknowns = new List<string>();
        var alternatives = new List<string>(facts.PolicyAlternatives);

        // —— 硬性禁止：不接受用户绕过 ——
        if (facts.IsNetScopeProcess)
            return Blocked(facts, "NetScope 自身（App 或后台采集器）不允许被本功能结束。", ["重启 NetScope 请使用托盘菜单退出"], evidence);
        if (facts.Process.ProcessId is 0 or 4)
            return Blocked(facts, "系统保留进程（System/空闲占位），不能结束。", [], evidence);
        if (facts.IsCritical && facts.CriticalFlagKnown)
            return Blocked(facts, "Windows 标记的关键进程，结束会直接导致系统崩溃。", ["高占用应定位具体来源（驱动、内核线程）"], evidence);

        // —— 未知阻断：任何关键字段缺失都先记入未知项 ——
        if (!facts.CriticalFlagKnown) unknowns.Add("无法确认关键进程标志（查询失败）");
        if (string.IsNullOrEmpty(facts.OwnerSid)) unknowns.Add("无法读取进程所有者");
        if (facts.SessionId is null) unknowns.Add("无法读取会话 ID");
        if (!facts.ServicesKnown) unknowns.Add("无法查询服务归属");
        if (string.IsNullOrEmpty(facts.ImagePath)) unknowns.Add("无法读取可执行文件路径");

        // —— 系统/服务高风险 ——
        if (facts.HostedServices.Count > 0)
            return Discouraged(facts, $"该进程承载 {facts.HostedServices.Count} 个 Windows 服务，结束会中断这些服务。",
                [$"承载的服务：{string.Join("、", facts.HostedServices.Take(4))}", ..evidence], alternatives);
        if (facts.SessionId == 0)
            return Discouraged(facts, "目标位于系统会话（Session 0），不是当前用户会话进程。",
                [..evidence, "会话 ID 0（服务/系统会话）"], alternatives);
        if (!string.IsNullOrEmpty(facts.OwnerSid) && !facts.OwnerIsCurrentUser)
            return Discouraged(facts, "目标属于其他用户，不属于当前用户会话。",
                [..evidence, "所有者 SID 与当前用户不同"], alternatives);

        // —— 知识库策略：仅在身份相符（路径/签名）时生效，防同名伪装 ——
        if (facts.Policy != TerminationPolicy.None)
        {
            if (!facts.IdentityMatchesPolicy)
                return Unknown(facts,
                    "进程名与内置系统进程条目相同，但路径与签名不符合系统进程特征（可能同名伪装），无法给出可靠建议。",
                    [..evidence, $"进程名命中知识库策略 {facts.Policy}，但路径不在 Windows 目录且签名无效或未验证"],
                    ["以数字签名和发布者进一步核实身份"], alternatives);
            if (facts.Policy == TerminationPolicy.Critical)
                return Blocked(facts, "内置知识库标记的系统关键进程，结束会导致系统崩溃。",
                    [..evidence, "知识库证据：关键进程（csrss/lsass/winlogon 等）"], alternatives);
            if (facts.Policy is TerminationPolicy.SystemComponent or TerminationPolicy.ServiceHost or TerminationPolicy.Security)
                return Discouraged(facts, $"{facts.PolicyImpact}建议不要直接结束。",
                    [..evidence, $"知识库策略：{PolicyName(facts.Policy)}"], alternatives);
        }

        // —— 未知项收敛：关键字段缺失时不允许强制结束 ——
        if (unknowns.Count > 0)
            return Unknown(facts, "关键证据缺失，无法验证目标身份，不允许强制结束。", evidence, unknowns, alternatives);

        // —— 到这里：当前用户、当前会话、非关键、非服务、路径可读 ——
        evidence.Add("当前用户进程");
        evidence.Add("非 Windows 关键进程（已查询关键标志）");
        evidence.Add($"位于当前会话（Session {facts.SessionId}）");
        if (facts.HostedServices.Count == 0 && facts.ServicesKnown) evidence.Add("不承载 Windows 服务");
        if (facts.Signature is { } signature)
            evidence.Add(signature switch
            {
                SignatureState.Valid => $"签名有效：{facts.Publisher ?? "未知发布者"}",
                SignatureState.Missing => "未签名（不影响结束的系统安全性，仅提示身份验证程度）",
                _ => "签名无法验证"
            });

        var level = TerminationRiskLevel.UsuallyTerminable;
        if (facts.Policy == TerminationPolicy.UserShell || facts.Policy == TerminationPolicy.BackgroundUtility)
            level = TerminationRiskLevel.Caution;
        if (facts.HasMainWindow)
        {
            level = TerminationRiskLevel.Caution;
            evidence.Add("存在主窗口，未保存内容可能丢失");
        }
        if (facts.ListeningPortCount > 0 || facts.ActiveConnectionCount > 0)
        {
            level = TerminationRiskLevel.Caution;
            evidence.Add(facts.ListeningPortCount > 0
                ? $"监听 {facts.ListeningPortCount} 个端口、{facts.ActiveConnectionCount} 条活动连接，结束会中断这些服务"
                : $"{facts.ActiveConnectionCount} 条活动 TCP 连接，结束会中断这些会话");
        }

        var conclusion = level == TerminationRiskLevel.Caution
            ? $"可以结束，但{(facts.Policy == TerminationPolicy.None ? "" : facts.PolicyImpact)}可能中断功能或丢失未保存数据。"
            : "通常可以结束：当前证据下未发现系统崩溃风险，但不排除丢失未保存数据或被软件自动拉起。";
        return new TerminationAssessment(level,
            AllowClose: true,
            AllowTerminate: true,
            conclusion, evidence, unknowns, alternatives, facts);
    }

    private static TerminationAssessment Blocked(TerminationFacts facts, string conclusion,
        IReadOnlyList<string> evidence, IReadOnlyList<string> alternatives) =>
        new(TerminationRiskLevel.Blocked, false, false, conclusion, evidence, [], alternatives, facts);

    private static TerminationAssessment Discouraged(TerminationFacts facts, string conclusion,
        IReadOnlyList<string> evidence, IReadOnlyList<string> alternatives)
    {
        var list = new List<string>(evidence) { "替代操作优先于结束进程" };
        return new(TerminationRiskLevel.StronglyDiscouraged, false, false, conclusion, list, [], alternatives, facts);
    }

    private static TerminationAssessment Unknown(TerminationFacts facts, string conclusion,
        IReadOnlyList<string> evidence, IReadOnlyList<string> unknowns, IReadOnlyList<string> alternatives) =>
        new(TerminationRiskLevel.Unknown, true, false, conclusion, evidence, unknowns, alternatives, facts);

    internal static string PolicyName(TerminationPolicy policy) => policy switch
    {
        TerminationPolicy.Critical => "系统关键进程",
        TerminationPolicy.SystemComponent => "系统组件",
        TerminationPolicy.ServiceHost => "服务宿主",
        TerminationPolicy.Security => "安全软件",
        TerminationPolicy.UserShell => "桌面外壳",
        TerminationPolicy.BackgroundUtility => "后台工具",
        TerminationPolicy.UserApplication => "普通用户应用",
        _ => "未收录"
    };
}
