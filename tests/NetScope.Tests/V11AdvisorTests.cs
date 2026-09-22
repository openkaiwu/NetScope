using NetScope.Core.Knowledge;
using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Tests;

/// <summary>
/// V1.1 终止建议规则引擎验收：五个等级的判定、未知字段向保守收敛、
/// 同名伪装阻断、硬性保护不接受用户绕过。
/// </summary>
public sealed class V11AdvisorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
    private const string Sid = "S-1-5-21-1000";

    private static TerminationFacts Facts(
        string name = "app.exe",
        int pid = 9001,
        bool isCritical = false,
        bool criticalKnown = true,
        string? ownerSid = Sid,
        int? session = 1,
        IReadOnlyList<string>? services = null,
        bool servicesKnown = true,
        string? path = @"C:\Program Files\App\app.exe",
        SignatureState? signature = SignatureState.Valid,
        string? publisher = "Vendor Inc.",
        bool hasWindow = false,
        int ports = 0,
        int connections = 0,
        TerminationPolicy? policy = null,
        string? policyImpact = null,
        IReadOnlyList<string>? alternatives = null,
        bool isNetScope = false)
    {
        // 与 ProcessSafetyInspector 相同：按名称解析内置终止知识，测试可显式覆盖
        NetScope.Core.Knowledge.TerminationKnowledge.TryLookup(name, out var knowledge);
        return new(
            new(pid, Now), name, path, publisher, signature,
            isCritical, criticalKnown, ownerSid, Sid, session, 1,
            services ?? [], servicesKnown, hasWindow, ports, connections,
            isNetScope, policy ?? knowledge.Policy,
            policyImpact ?? knowledge.Impact,
            alternatives ?? knowledge.Alternatives);
    }

    [Fact]
    public void CriticalFlagBlocksUnconditionally()
    {
        var assessment = ProcessTerminationAdvisor.Assess(Facts(isCritical: true, criticalKnown: true));
        Assert.Equal(TerminationRiskLevel.Blocked, assessment.Level);
        Assert.False(assessment.AllowClose);
        Assert.False(assessment.AllowTerminate);
        Assert.Contains("关键进程", assessment.Conclusion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ReservedPidsBlock(int pid)
    {
        var assessment = ProcessTerminationAdvisor.Assess(Facts(pid: pid));
        Assert.Equal(TerminationRiskLevel.Blocked, assessment.Level);
        Assert.False(assessment.AllowTerminate);
    }

    [Fact]
    public void NetScopeItselfIsBlocked()
    {
        var assessment = ProcessTerminationAdvisor.Assess(Facts(name: "NetScope.exe", isNetScope: true));
        Assert.Equal(TerminationRiskLevel.Blocked, assessment.Level);
    }

    [Fact]
    public void ServiceHostIsStronglyDiscouragedWithoutButtons()
    {
        var assessment = ProcessTerminationAdvisor.Assess(Facts(name: "svchost.exe",
            path: @"C:\Windows\System32\svchost.exe",
            services: ["WSearch", "wuauserv"]));
        Assert.Equal(TerminationRiskLevel.StronglyDiscouraged, assessment.Level);
        Assert.False(assessment.AllowClose);
        Assert.False(assessment.AllowTerminate);
        Assert.Contains("WSearch", assessment.Evidence[0]);
    }

    [Theory]
    [InlineData(0)]   // Session 0
    [InlineData(null)] // 会话未知
    public void SessionZeroOrUnknownIsRefused(int? session)
    {
        var assessment = ProcessTerminationAdvisor.Assess(Facts(session: session));
        Assert.Equal(session == 0 ? TerminationRiskLevel.StronglyDiscouraged : TerminationRiskLevel.Unknown, assessment.Level);
        Assert.False(assessment.AllowTerminate);
    }

    [Fact]
    public void OtherUserProcessIsRefused()
    {
        var assessment = ProcessTerminationAdvisor.Assess(Facts(ownerSid: "S-1-5-21-9999"));
        Assert.Equal(TerminationRiskLevel.StronglyDiscouraged, assessment.Level);
        Assert.False(assessment.AllowClose);
        Assert.False(assessment.AllowTerminate);
    }

    [Fact]
    public void SystemNameImpersonationFallsToUnknown()
    {
        // 恶意程序伪装系统进程名：路径不在 Windows 目录且签名无效
        var assessment = ProcessTerminationAdvisor.Assess(Facts(name: "csrss.exe",
            path: @"C:\Users\evil\csrss.exe", signature: SignatureState.Invalid));
        Assert.Equal(TerminationRiskLevel.Unknown, assessment.Level);
        Assert.False(assessment.AllowTerminate);
        Assert.Contains(assessment.Unknowns.Concat(new[] { assessment.Conclusion }), item => item.Contains("伪装") || item.Contains("无法给出"));
    }

    [Fact]
    public void KnowledgeCriticalWithWindowsPathBlocks()
    {
        var assessment = ProcessTerminationAdvisor.Assess(Facts(name: "csrss.exe",
            path: @"C:\Windows\System32\csrss.exe", signature: SignatureState.Valid));
        Assert.Equal(TerminationRiskLevel.Blocked, assessment.Level);
    }

    [Fact]
    public void KnowledgeSystemComponentIsDiscouraged()
    {
        var assessment = ProcessTerminationAdvisor.Assess(Facts(name: "dwm.exe",
            path: @"C:\Windows\System32\dwm.exe"));
        Assert.Equal(TerminationRiskLevel.StronglyDiscouraged, assessment.Level);
        Assert.False(assessment.AllowClose);
    }

    [Theory]
    [InlineData(false)]   // 关键标志查询失败
    [InlineData(true)]    // 所有者读取失败
    public void UnknownKeyFieldsConvergeToUnknown(bool criticalFlagUnknown)
    {
        var assessment = ProcessTerminationAdvisor.Assess(criticalFlagUnknown
            ? Facts(criticalKnown: false)
            : Facts(ownerSid: null));
        Assert.Equal(TerminationRiskLevel.Unknown, assessment.Level);
        Assert.False(assessment.AllowTerminate);
        Assert.NotEmpty(assessment.Unknowns);
    }

    [Fact]
    public void MissingPathIsUnknown()
    {
        var assessment = ProcessTerminationAdvisor.Assess(Facts(path: null));
        Assert.Equal(TerminationRiskLevel.Unknown, assessment.Level);
        Assert.False(assessment.AllowTerminate);
    }

    [Fact]
    public void WindowOrPortsRaiseToCaution()
    {
        var window = ProcessTerminationAdvisor.Assess(Facts(hasWindow: true));
        Assert.Equal(TerminationRiskLevel.Caution, window.Level);
        Assert.Contains(window.Evidence, e => e.Contains("主窗口"));

        var ports = ProcessTerminationAdvisor.Assess(Facts(ports: 3, connections: 12));
        Assert.Equal(TerminationRiskLevel.Caution, ports.Level);
        Assert.Contains(ports.Evidence, e => e.Contains("3 个端口"));
    }

    [Fact]
    public void CleanCurrentUserAppIsUsuallyTerminable()
    {
        var assessment = ProcessTerminationAdvisor.Assess(Facts());
        Assert.Equal(TerminationRiskLevel.UsuallyTerminable, assessment.Level);
        Assert.True(assessment.AllowClose);
        Assert.True(assessment.AllowTerminate);
        Assert.Contains(assessment.Evidence, e => e.Contains("当前用户进程"));
        Assert.Contains(assessment.Evidence, e => e.Contains("非 Windows 关键进程"));
    }

    [Fact]
    public void KnowledgeShellPolicyIsCautionWithAlternatives()
    {
        var assessment = ProcessTerminationAdvisor.Assess(Facts(name: "explorer.exe",
            path: @"C:\Windows\explorer.exe",
            policy: TerminationPolicy.UserShell, policyImpact: "桌面、任务栏与文件窗口宿主，",
            alternatives: ["用任务管理器“运行新任务 explorer”恢复"]));
        Assert.Equal(TerminationRiskLevel.Caution, assessment.Level);
        Assert.Contains("explorer", assessment.Alternatives[0]);
    }

    [Fact]
    public void UnsignedThirdPartyStaysTerminableWithNote()
    {
        var assessment = ProcessTerminationAdvisor.Assess(Facts(signature: SignatureState.Missing, publisher: null));
        Assert.Equal(TerminationRiskLevel.UsuallyTerminable, assessment.Level);
        Assert.Contains(assessment.Evidence, e => e.Contains("未签名"));
    }

    [Fact]
    public void HighImpactDoesNotLowerSecurityLevel()
    {
        // 高 CPU / 影响排行只解释动机：等级与证据不因此变化（设计 §5）
        var assessment = ProcessTerminationAdvisor.Assess(Facts(name: "svchost.exe",
            path: @"C:\Windows\System32\svchost.exe", services: ["WSearch"]));
        Assert.Equal(TerminationRiskLevel.StronglyDiscouraged, assessment.Level);
    }

    [Fact]
    public void TerminationKnowledgeCoversAllPolicies()
    {
        Assert.True(TerminationKnowledge.TryLookup("csrss", out var critical));
        Assert.Equal(TerminationPolicy.Critical, critical.Policy);
        Assert.True(TerminationKnowledge.TryLookup("explorer", out var shell));
        Assert.Equal(TerminationPolicy.UserShell, shell.Policy);
        Assert.False(TerminationKnowledge.TryLookup("some-random-vendor", out var none));
        Assert.Equal(TerminationPolicy.None, none.Policy);
    }
}
