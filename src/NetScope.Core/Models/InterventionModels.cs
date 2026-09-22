namespace NetScope.Core.Models;

/// <summary>
/// V1.1 终止风险等级。等级只代表“当前可验证证据下的建议”，
/// 不是“安全结束”的承诺；界面永远不显示“不会崩溃”一类绝对表述。
/// </summary>
public enum TerminationRiskLevel
{
    /// <summary>禁止结束：关键进程、PID 0/4、NetScope 自身、身份不一致。</summary>
    Blocked,

    /// <summary>强烈不建议：服务宿主、安全软件、系统关键组件、其他用户或 Session 0 进程。</summary>
    StronglyDiscouraged,

    /// <summary>可以结束，但可能中断功能或丢失数据。</summary>
    Caution,

    /// <summary>通常可以结束：当前证据下未发现系统崩溃风险，不代表不会丢数据。</summary>
    UsuallyTerminable,

    /// <summary>无法判断：关键证据缺失或权限不足，不允许强制结束。</summary>
    Unknown
}

public enum TerminationKind
{
    /// <summary>请求关闭：向主窗口发送正常关闭请求，允许应用保存或拒绝。</summary>
    RequestClose,

    /// <summary>强制结束：TerminateProcess，跳过应用清理。</summary>
    Terminate
}

public enum TerminationOutcome
{
    /// <summary>目标已退出（等待确认后）。</summary>
    Exited,

    /// <summary>关闭请求已发送但目标仍在运行（应用可拒绝关闭）。</summary>
    CloseRequested,

    /// <summary>拒绝执行：PID 相同但创建时间已变化（PID 复用）。</summary>
    RefusedIdentityChanged,

    /// <summary>拒绝执行：关键进程保护、其他会话/所有者或服务归属等硬性规则。</summary>
    RefusedProtection,

    /// <summary>拒绝执行：关键证据缺失，身份无法验证。</summary>
    RefusedUnknownIdentity,

    /// <summary>权限不足（未提权、不重试）。</summary>
    AccessDenied,

    /// <summary>目标已自行退出。</summary>
    AlreadyExited,

    /// <summary>执行失败（含等待超时与 Win32 错误）。</summary>
    Failed
}

/// <summary>结束建议的知识库策略：仅作为解释证据与等级上限，不能单独作为安全结论。</summary>
public enum TerminationPolicy
{
    None = 0,
    Critical,
    SystemComponent,
    ServiceHost,
    Security,
    UserShell,
    BackgroundUtility,
    UserApplication
}

/// <summary>
/// 评估用只读事实快照（由 Windows 侧 ProcessSafetyInspector 采集）。
/// 可空/未知字段必须显式表达，未知不等于安全。
/// </summary>
public sealed record TerminationFacts(
    ProcessInstanceKey Process,
    string Name,
    string? ImagePath,
    string? Publisher,
    SignatureState? Signature,
    bool IsCritical,
    bool CriticalFlagKnown,
    string? OwnerSid,
    string? CurrentUserSid,
    int? SessionId,
    int CurrentSessionId,
    IReadOnlyList<string> HostedServices,
    bool ServicesKnown,
    bool HasMainWindow,
    int ListeningPortCount,
    int ActiveConnectionCount,
    bool IsNetScopeProcess,
    TerminationPolicy Policy,
    string PolicyImpact,
    IReadOnlyList<string> PolicyAlternatives)
{
    public bool OwnerIsCurrentUser => OwnerSid is { Length: > 0 } && string.Equals(OwnerSid, CurrentUserSid, StringComparison.OrdinalIgnoreCase);
    public bool InCurrentSession => SessionId is { } session && session == CurrentSessionId && session != 0;
    /// <summary>身份与内置知识库条目相符：路径在 Windows 目录或签名有效，否则视为可能的同名伪装。</summary>
    public bool IdentityMatchesPolicy => Policy == TerminationPolicy.None
        || (ImagePath?.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase) == true)
        || Signature == SignatureState.Valid;
}

/// <summary>结束建议评估结果：等级、允许的操作、结论、证据、未知项与替代建议。</summary>
public sealed record TerminationAssessment(
    TerminationRiskLevel Level,
    bool AllowClose,
    bool AllowTerminate,
    string Conclusion,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> Alternatives,
    TerminationFacts Facts);

/// <summary>执行结果：以执行器实测为准；Win32Error 为 0 表示无相关错误码。</summary>
public sealed record TerminationResult(TerminationOutcome Outcome, string Message, int Win32Error = 0)
{
    public bool Success => Outcome == TerminationOutcome.Exited;
}

/// <summary>本地审计事件：用户显式发起的每一次干预动作及结果。不保存命令行、环境变量或文档内容。</summary>
public sealed record InterventionEvent(
    Guid Id,
    DateTimeOffset At,
    ProcessInstanceKey Process,
    string ProcessName,
    string? ImagePath,
    TerminationRiskLevel Assessment,
    TerminationKind Requested,
    TerminationOutcome Outcome,
    string Message,
    int Win32Error);
