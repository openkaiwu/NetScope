using System.Collections.Immutable;
using NetScope.Core.Models;

namespace NetScope.Core.Abstractions;

/// <summary>进程性能原始读数提供方（Windows 实现位于 NetScope.Windows）。</summary>
public interface IProcessPerformanceProvider
{
    ValueTask<ImmutableArray<ProcessPerformanceReading>> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>系统性能原始读数提供方（Windows 实现位于 NetScope.Windows）。</summary>
public interface ISystemPerformanceProvider
{
    ValueTask<SystemPerformanceReading> ReadAsync(CancellationToken cancellationToken = default);
}

public interface ICollectorSelfMonitor
{
    CollectorUsageReading Read();
}

/// <summary>性能历史存储契约（V0.2 阶段 C 由 SQLite 实现；App 与 Collector 只依赖本接口）。</summary>
public interface IPerformanceHistoryStore : IAsyncDisposable
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask AppendSystemSampleAsync(SystemPerformanceSample sample, CancellationToken cancellationToken = default);
    ValueTask AppendProcessSampleAsync(ProcessPerformanceSample sample, CancellationToken cancellationToken = default);
    ValueTask AppendEventAsync(PerformanceEvent evt, CancellationToken cancellationToken = default);
    ValueTask AppendCollectorHealthAsync(CollectorHealthSnapshot sample, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    ValueTask<IReadOnlyList<CollectorHealthSnapshot>> QueryCollectorHealthAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<CollectorHealthSnapshot>>([]);
    ValueTask<IReadOnlyList<ProcessHistoryPoint>> QueryProcessSeriesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<ProcessHistoryPoint>>([]);
    ValueTask<IReadOnlyList<PortActivitySummary>> QueryPortActivityAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<PortActivitySummary>>([]);
    ValueTask<IReadOnlyList<SystemPerformanceSample>> QuerySystemAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    /// <summary>分桶系统序列（周/月报告）：窗口聚合到至多 maxPoints 个桶的平均值。</summary>
    ValueTask<IReadOnlyList<SystemPerformanceSample>> QuerySystemSeriesAsync(DateTimeOffset from, DateTimeOffset to, int maxPoints = 720, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<SystemPerformanceSample>>([]);
    /// <summary>时间窗内的系统与进程采样条数，用于报告的数据完整性说明。</summary>
    ValueTask<HistoryCoverage> QueryCoverageAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) => ValueTask.FromResult(new HistoryCoverage(0, 0));
    /// <summary>记录一次用户显式发起的干预动作（本地审计，不保存命令行/环境变量/文档内容）。</summary>
    ValueTask AppendInterventionAsync(InterventionEvent evt, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    ValueTask<IReadOnlyList<InterventionEvent>> QueryInterventionsAsync(DateTimeOffset from, DateTimeOffset to, int limit = 200, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<InterventionEvent>>([]);
    ValueTask<IReadOnlyList<ProcessPerformanceSample>> QueryProcessAsync(ProcessInstanceKey process, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<PerformanceEvent>> QueryEventsAsync(DateTimeOffset from, DateTimeOffset to, int limit = 200, CancellationToken cancellationToken = default);
    /// <summary>记录一条已结束的端口占用会话。</summary>
    ValueTask AppendConnectionAsync(TcpConnectionRecord connection, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<TcpConnectionRecord>> QueryConnectionsAsync(ConnectionQuery query, CancellationToken cancellationToken = default);
    ValueTask AppendPortSessionAsync(PortSessionRecord session, CancellationToken cancellationToken = default);
    /// <summary>查询某端口在时间窗内的占用聚合（按端口+协议+进程名分组，按时长降序）。</summary>
    ValueTask<IReadOnlyList<PortUsageSummary>> QueryPortUsageAsync(int port, PortProtocol protocol, DateTimeOffset from, DateTimeOffset to, int limit = 20, CancellationToken cancellationToken = default);
    /// <summary>是否处于可用状态。数据库损坏或写入失败时为 false，实时功能应继续工作。</summary>
    bool IsUsable { get; }
}

/// <summary>性能事件规则引擎：输入系统与进程采样，输出候选事件。规则必须带冷却，避免事件风暴。</summary>
public interface IPerformanceEventEngine
{
    ValueTask<IReadOnlyList<PerformanceEvent>> EvaluateAsync(
        SystemPerformanceSample system,
        ImmutableArray<ProcessPerformanceSample> processes,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>登记一次用户标记时间，用于提升邻近进程在贡献分中的权重。</summary>
    void NoteUserMark(DateTimeOffset markedAt);
}

/// <summary>用户“刚才卡了”标记服务：创建用户反馈事件并锁定现场上下文。</summary>
public interface IUserMarkerService
{
    ValueTask<PerformanceEvent> MarkLagAsync(CancellationToken cancellationToken = default);
}

/// <summary>Collector IPC 客户端（App 使用）。连接失败时返回空结果，由调用方决定降级。</summary>
public interface ICollectorClient : IAsyncDisposable
{
    ValueTask<CollectorHealthSnapshot?> GetCollectorHealthAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<CollectorHealthSnapshot?>(null);
    ValueTask<IReadOnlyList<InsightItem>> GetInsightsAsync(InsightQuery query, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<InsightItem>>([]);
    ValueTask<IReadOnlyList<TcpConnectionRecord>> QueryConnectionsAsync(ConnectionQuery query, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<TcpConnectionRecord>>([]);
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
    ValueTask<ImmutableArray<PortBindingSnapshot>> GetPortSnapshotAsync(CancellationToken cancellationToken = default);
    ValueTask<SystemPerformanceSample?> GetSystemSampleAsync(CancellationToken cancellationToken = default);
    ValueTask<ImmutableArray<ProcessPerformanceSample>> GetProcessSamplesAsync(CancellationToken cancellationToken = default);
    ValueTask<bool> MarkLagAsync(CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<PerformanceEvent>> GetRecentEventsAsync(int limit = 100, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<SystemPerformanceSample>> QuerySystemHistoryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ProcessPerformanceSample>> QueryProcessHistoryAsync(ProcessInstanceKey process, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    /// <summary>某端口在时间窗内的占用史（谁占过、几次、累计多久）。</summary>
    ValueTask<IReadOnlyList<PortUsageSummary>> QueryPortUsageAsync(int port, PortProtocol protocol, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
    /// <summary>某进程名在时间窗内关联的性能事件（总数 + 最新若干条）。</summary>
    ValueTask<ProcessEventsSummary> QueryProcessEventsAsync(string processName, int days = 7, int limit = 10, CancellationToken cancellationToken = default);
    /// <summary>过去 N 天的影响排行：谁最可能拖慢电脑（只排序证据，不下因果结论）。</summary>
    ValueTask<IReadOnlyList<ImpactRankEntry>> GetImpactRankingAsync(int days = 7, int limit = 10, CancellationToken cancellationToken = default);
    /// <summary>生成周报或月报（本地汇总，不上传）。Collector 不可用或分析失败时返回 null。</summary>
    ValueTask<PerformanceReport?> GetReportAsync(ReportRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult<PerformanceReport?>(null);
    /// <summary>把一次干预动作写入本地审计历史（经 Collector 落盘；不含终止能力本身）。失败返回 false。</summary>
    ValueTask<bool> AppendInterventionAsync(InterventionEvent evt, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
}
