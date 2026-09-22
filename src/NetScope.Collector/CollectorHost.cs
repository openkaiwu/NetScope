using System.Collections.Immutable;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;
using NetScope.Core.Services;
using NetScope.Windows.History;
using NetScope.Windows.Ipc;
using NetScope.Windows.Logging;
using NetScope.Windows.Network;
using NetScope.Windows.Performance;
using NetScope.Windows.Ports;
using NetScope.Windows.Settings;
using NetScope.Collector.Sampling;

namespace NetScope.Collector;

/// <summary>组装采样、事件引擎、历史存储与 IPC 服务端，并对 App 请求返回当前状态。</summary>
public sealed class CollectorHost : IAsyncDisposable
{
    private const int MemoryEventCapacity = 500;

    private readonly RollingFileLogger _logger;
    private readonly SampleCoordinator _coordinator;
    private readonly CollectorIpcServer _server;
    private readonly IPerformanceEventEngine _eventEngine;
    private readonly ConditionalHistoryStore _historyStore;
    private readonly JsonSettingsStore _settingsStore;
    private readonly PortSessionTracker _portSessions = new();
    private readonly WindowsProcessMetadataResolver _processResolver = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _eventLock = new();
    private readonly List<PerformanceEvent> _recentEvents = [];
    private readonly ProcessBehaviorAnalyzer _behaviorAnalyzer = new();
    private Task? _settingsLoop;
    private volatile AppSettings _settings = new();
    private bool _started;

    public CollectorHost(RollingFileLogger? logger = null)
    {
        _logger = logger ?? new RollingFileLogger();
        _settingsStore = new JsonSettingsStore();
        _eventEngine = new PerformanceEventEngine();
        _historyStore = new ConditionalHistoryStore(
            new SqliteHistoryStore(logger: _logger),
            () => _settings.HistoryEnabled && _settings.BackgroundRecording,
            () => _settings.HistoryRetentionDays);
        _coordinator = new SampleCoordinator(
            new SystemPerformanceProvider(),
            new ProcessPerformanceProvider(),
            new SystemNetworkSnapshotProvider(),
            new WindowsPortTableProvider(),
            _logger,
            _eventEngine,
            _historyStore,
            new ForegroundProcessProvider().GetForegroundProcessId,
            onEvent: AddRecentEvent,
            performanceEnabled: () => _settings.BackgroundRecording,
            portSessions: _portSessions,
            processNameResolver: ResolveProcessName,
            historyEnabled: () => _settings.HistoryEnabled && _settings.BackgroundRecording,
            connectionIdentity: pid => _processResolver.ResolveAsync(pid).GetAwaiter().GetResult(),
            selfMonitor: new CollectorSelfMonitor());
        _server = new CollectorIpcServer(HandleAsync, _logger);
    }

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;

        try
        {
            _settings = (await _settingsStore.LoadAsync()).Normalize();
        }
        catch (Exception ex)
        {
            await _logger.WriteAsync("WARN", $"读取设置失败，使用默认设置: {ex.Message}");
            _settings = new AppSettings().Normalize();
        }

        _historyStore.Inner.ConfigureRetention(_settings.HistoryRetentionDays);
        await _historyStore.Inner.InitializeAsync();
        _settingsLoop = Task.Run(() => SettingsReloadLoopAsync(_lifetime.Token));

        _coordinator.Start();
        _server.Start();
        await _logger.WriteAsync("INFO", $"NetScope Collector {CollectorProtocol.ServerVersion} started, protocol v{CollectorProtocol.ProtocolVersion}, history={_settings.HistoryEnabled}, retention={_settings.HistoryRetentionDays}d");
    }

    /// <summary>设置热重载：App 保存设置后 Collector 在下一个周期自动生效，无需重启。</summary>
    private async Task SettingsReloadLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await timer.WaitForNextTickAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                var loaded = (await _settingsStore.LoadAsync()).Normalize();
                if (loaded != _settings)
                {
                    _settings = loaded;
                    _historyStore.Inner.ConfigureRetention(loaded.HistoryRetentionDays);
                    await _logger.WriteAsync("INFO", $"设置已重载: history={loaded.HistoryEnabled}, retention={loaded.HistoryRetentionDays}d, recording={loaded.BackgroundRecording}");
                }
            }
            catch (Exception ex)
            {
                await _logger.WriteAsync("WARN", $"重载设置失败: {ex.Message}");
            }
        }
    }

    private void AddRecentEvent(PerformanceEvent evt)
    {
        lock (_eventLock)
        {
            // 同一事件关闭时会以相同 Id 再次出现，替换旧状态
            var existing = _recentEvents.FindIndex(x => x.Id == evt.Id);
            if (existing >= 0) _recentEvents[existing] = evt;
            else
            {
                _recentEvents.Add(evt);
                _recentEvents.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
                if (_recentEvents.Count > MemoryEventCapacity) _recentEvents.RemoveRange(MemoryEventCapacity, _recentEvents.Count - MemoryEventCapacity);
            }
        }
    }

    private async ValueTask<string?> HandleAsync(string op, string? payloadJson, CancellationToken cancellationToken)
    {
        switch (op)
        {
            case CollectorProtocol.OpConnections:
                {
                    var request = payloadJson is null ? null : CollectorProtocol.Deserialize<ConnectionQuery>(payloadJson);
                    if (request is null) return "[]";
                    var query = request with { Limit = Math.Clamp(request.Limit, 1, 500), Search = (request.Search ?? "")[..Math.Min(request.Search?.Length ?? 0, 200)] };
                    var stored = await _historyStore.Inner.QueryConnectionsAsync(query, cancellationToken);
                    var live = _coordinator.Connections;
                    var liveIds = live.Select(c => c.Id).ToHashSet();
                    stored = stored.Select(c => c.EndedAt is null && !liveIds.Contains(c.Id)
                        ? c with { EndedAt = c.LastSeenAt, EndReason = "观察已中断或记录暂停，后续状态未知" } : c).ToArray();
                    var active = live.Where(c => c.FirstSeenAt <= query.To && c.LastSeenAt >= query.From &&
                        $"{c.ProcessName} {c.ProcessId} {c.LocalAddress} {c.LocalPort} {c.RemoteAddress} {c.RemotePort}".Contains(query.Search, StringComparison.OrdinalIgnoreCase));
                    return CollectorProtocol.Serialize(active.Concat(stored).DistinctBy(c => c.Id).OrderByDescending(c => c.LastSeenAt).Take(query.Limit).ToArray());
                }

            case CollectorProtocol.OpHealth:
                return _coordinator.CurrentHealth is { } health ? CollectorProtocol.Serialize(health) : null;

            case CollectorProtocol.OpInsights:
                {
                    var request = payloadJson is null ? null : CollectorProtocol.Deserialize<InsightQuery>(payloadJson);
                    request ??= new InsightQuery();
                    request = request with
                    {
                        Days = Math.Clamp(request.Days, 1, 30),
                        Limit = Math.Clamp(request.Limit, 1, 200),
                        ProcessName = (request.ProcessName ?? "")[..Math.Min(request.ProcessName?.Length ?? 0, 200)]
                    };
                    var now = DateTimeOffset.Now;
                    var from = now.AddDays(-request.Days);
                    var events = _historyStore.Inner.IsUsable
                        ? await _historyStore.Inner.QueryEventsAsync(from, now, 1000, cancellationToken)
                        : await QueryEventsAsync(1000, cancellationToken);
                    var points = await _historyStore.Inner.QueryProcessSeriesAsync(from, now, cancellationToken);
                    var behaviors = _behaviorAnalyzer.Analyze(points);
                    var ports = await _historyStore.Inner.QueryPortActivityAsync(from, now, cancellationToken);
                    var connections = await _historyStore.Inner.QueryConnectionsAsync(new(from, now, "", 500), cancellationToken);
                    var insights = InsightGenerator.Generate(request, now, events, behaviors, ports, connections);
                    return CollectorProtocol.Serialize(insights);
                }

            case CollectorProtocol.OpPing:
                return "pong";

            case CollectorProtocol.OpPorts:
                return CollectorProtocol.Serialize(CollectorDtos.ToDto(_coordinator.LastPorts));

            case CollectorProtocol.OpSystem:
                {
                    var sample = _coordinator.CurrentSystem;
                    return sample is null ? null : CollectorProtocol.Serialize(CollectorDtos.ToDto(sample));
                }

            case CollectorProtocol.OpProcesses:
                {
                    var samples = _coordinator.CurrentProcesses;
                    return CollectorProtocol.Serialize(samples.Select(CollectorDtos.ToDto).ToImmutableArray());
                }

            case CollectorProtocol.OpEvents:
                {
                    EventsRequest? request = null;
                    if (payloadJson is not null)
                    {
                        try { request = CollectorProtocol.Deserialize<EventsRequest>(payloadJson); }
                        catch { request = null; }
                    }
                    var limit = Math.Clamp(request?.Limit ?? 100, 1, 1000);
                    var events = await QueryEventsAsync(limit, cancellationToken);
                    return CollectorProtocol.Serialize(events.Select(CollectorDtos.ToDto).ToArray());
                }

            case CollectorProtocol.OpSystemHistory:
                {
                    HistoryRequest? request = null;
                    if (payloadJson is not null)
                    {
                        try { request = CollectorProtocol.Deserialize<HistoryRequest>(payloadJson); }
                        catch { request = null; }
                    }
                    if (request is null) return "[]";
                    var samples = _historyStore.Inner.IsUsable
                        ? await _historyStore.Inner.QuerySystemAsync(request.From, request.To, cancellationToken)
                        : [];
                    return CollectorProtocol.Serialize(samples.Select(CollectorDtos.ToDto).ToArray());
                }

            case CollectorProtocol.OpProcessHistory:
                {
                    ProcessHistoryRequest? request = null;
                    if (payloadJson is not null)
                    {
                        try { request = CollectorProtocol.Deserialize<ProcessHistoryRequest>(payloadJson); }
                        catch { request = null; }
                    }
                    if (request is null) return "[]";
                    var key = new ProcessInstanceKey(request.ProcessId, request.StartedAt);
                    var samples = _historyStore.Inner.IsUsable
                        ? await _historyStore.Inner.QueryProcessAsync(key, request.From, request.To, cancellationToken)
                        : [];
                    return CollectorProtocol.Serialize(samples.Select(CollectorDtos.ToDto).ToArray());
                }

            case CollectorProtocol.OpMarkLag:
                {
                    var evt = await MarkLagAsync(cancellationToken);
                    return CollectorProtocol.Serialize(new MarkLagDto(evt.StartedAt, true));
                }

            case CollectorProtocol.OpPortHistory:
                {
                    PortHistoryRequest? request = null;
                    if (payloadJson is not null)
                    {
                        try { request = CollectorProtocol.Deserialize<PortHistoryRequest>(payloadJson); }
                        catch { request = null; }
                    }
                    if (request is null) return "[]";
                    var usage = _historyStore.Inner.IsUsable
                        ? await _historyStore.Inner.QueryPortUsageAsync(
                            Math.Clamp(request.Port, 0, 65535), (PortProtocol)Math.Clamp(request.Protocol, 0, 1),
                            request.From, request.To, 20, cancellationToken)
                        : [];
                    return CollectorProtocol.Serialize(usage.Select(CollectorDtos.ToDto).ToArray());
                }

            case CollectorProtocol.OpProcessEvents:
                {
                    ProcessEventsRequest? request = null;
                    if (payloadJson is not null)
                    {
                        try { request = CollectorProtocol.Deserialize<ProcessEventsRequest>(payloadJson); }
                        catch { request = null; }
                    }
                    if (request is null || string.IsNullOrWhiteSpace(request.ProcessName))
                        return CollectorProtocol.Serialize(new ProcessEventsDto(0, [], 0, 7));

                    var days = Math.Clamp(request.Days, 1, 30);
                    var events = await QueryEventsAsync(5000, cancellationToken);
                    var summary = ProcessEventsQuery.Summarize(events, request.ProcessName, days, request.Limit, DateTimeOffset.Now);
                    var limited = summary.Events.Select(CollectorDtos.ToDto).ToArray();
                    return CollectorProtocol.Serialize(new ProcessEventsDto(summary.TotalCount, limited, summary.LagRelatedCount, summary.WindowDays));
                }

            case CollectorProtocol.OpReport:
                {
                    ReportRequestDto? request = null;
                    if (payloadJson is not null)
                    {
                        try { request = CollectorProtocol.Deserialize<ReportRequestDto>(payloadJson); }
                        catch { request = null; }
                    }
                    var period = request?.Period == 1 ? ReportPeriod.Month : ReportPeriod.Week;
                    var report = await BuildReportAsync(period, cancellationToken);
                    return report is null ? null : CollectorProtocol.Serialize(report);
                }

            case CollectorProtocol.OpIntervention:
                {
                    // 只写本地审计记录：这个操作不携带任何终止能力（执行器只存在于前台 App）
                    if (payloadJson is null) return "false";
                    try
                    {
                        var dto = CollectorProtocol.Deserialize<InterventionEventDto>(payloadJson);
                        if (dto is null || string.IsNullOrWhiteSpace(dto.Id)) return "false";
                        var evt = new InterventionEvent(
                            Guid.Parse(dto.Id), dto.At,
                            new ProcessInstanceKey(dto.ProcessId, dto.StartedAt),
                            dto.ProcessName[..Math.Min(dto.ProcessName.Length, 200)],
                            dto.ImagePath is null ? null : dto.ImagePath[..Math.Min(dto.ImagePath.Length, 512)],
                            (TerminationRiskLevel)Math.Clamp(dto.Assessment, 0, 4),
                            (TerminationKind)Math.Clamp(dto.Requested, 0, 1),
                            (TerminationOutcome)Math.Clamp(dto.Outcome, 0, 8),
                            dto.Message[..Math.Min(dto.Message.Length, 1000)],
                            dto.Win32Error);
                        await _historyStore.Inner.AppendInterventionAsync(evt, cancellationToken);
                        return "true";
                    }
                    catch (Exception ex)
                    {
                        await _logger.WriteAsync("WARN", $"写入干预审计失败: {ex.Message}");
                        return "false";
                    }
                }

            case CollectorProtocol.OpImpactRanking:
                {
                    ImpactRankingRequest? request = null;
                    if (payloadJson is not null)
                    {
                        try { request = CollectorProtocol.Deserialize<ImpactRankingRequest>(payloadJson); }
                        catch { request = null; }
                    }
                    var days = Math.Clamp(request?.Days ?? 7, 1, 30);
                    var events = await QueryEventsAsync(1000, cancellationToken);
                    var ranking = ImpactRankingCalculator.Rank(events, days)
                        .Take(Math.Clamp(request?.Limit ?? 10, 1, 50));
                    return CollectorProtocol.Serialize(ranking.Select(CollectorDtos.ToDto).ToArray());
                }

            default:
                throw new InvalidOperationException($"未知操作: {op}");
        }
    }

    /// <summary>按 PID 解析进程名用于端口会话记录；解析失败返回 null，由追踪器回退到 PID 标识。</summary>
    private string? ResolveProcessName(int processId)
    {
        try
        {
            var identity = _processResolver.ResolveAsync(processId).GetAwaiter().GetResult();
            return identity is { IsAccessible: true } && !string.IsNullOrWhiteSpace(identity.Name)
                ? identity.Name
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>优先从历史库查询事件；数据库不可用时回退内存事件列表，保证时间线始终可用。</summary>
    private async Task<IReadOnlyList<PerformanceEvent>> QueryEventsAsync(int limit, CancellationToken cancellationToken)
    {
        if (_historyStore.Inner.IsUsable)
        {
            var from = DateTimeOffset.Now.AddDays(-_settings.HistoryRetentionDays);
            var stored = await _historyStore.Inner.QueryEventsAsync(from, DateTimeOffset.Now, limit, cancellationToken);
            if (stored.Count > 0) return stored;
        }
        lock (_eventLock) return _recentEvents.Take(limit).ToList();
    }

    /// <summary>
    /// 汇总生成本机周报或月报：读取当前周期与上一周期的事件、分桶进程序列、端口会话、
    /// 连接记录与采样覆盖度，全部来自本机历史库，不涉及网络。
    /// </summary>
    private async Task<PerformanceReport?> BuildReportAsync(ReportPeriod period, CancellationToken cancellationToken)
    {
        if (!_historyStore.Inner.IsUsable) return null;

        var windowDays = period == ReportPeriod.Week ? 7 : 30;
        var now = DateTimeOffset.Now;
        var from = now.AddDays(-windowDays);
        var previousFrom = from.AddDays(-windowDays);
        var usable = _historyStore.Inner.IsUsable;

        var events = usable
            ? await _historyStore.Inner.QueryEventsAsync(from, now, 5000, cancellationToken)
            : await QueryEventsAsync(5000, cancellationToken);
        // 上一周期事件只在历史库内查询；保留期不足两个周期时返回空，报告会明确说明无可比数据
        var previous = usable
            ? await _historyStore.Inner.QueryEventsAsync(previousFrom, from, 5000, cancellationToken)
            : [];
        var points = await _historyStore.Inner.QueryProcessSeriesAsync(from, now, cancellationToken);
        var behaviors = _behaviorAnalyzer.Analyze(points);
        var ports = await _historyStore.Inner.QueryPortActivityAsync(from, now, cancellationToken);
        var connections = await _historyStore.Inner.QueryConnectionsAsync(new(from, now, "", 500), cancellationToken);
        var coverage = await _historyStore.Inner.QueryCoverageAsync(from, now, cancellationToken);

        var input = new ReportInput
        {
            Period = period,
            Now = now,
            Events = events,
            PreviousPeriodEvents = previous,
            Behaviors = behaviors,
            Ports = ports,
            Connections = connections,
            SystemSampleCount = coverage.SystemSampleCount,
            ProcessSampleCount = coverage.ProcessSampleCount,
            RetentionDays = _settings.HistoryRetentionDays
        };
        return ReportGenerator.Generate(input);
    }

    /// <summary>
    /// 用户标记“刚才卡了”：创建用户反馈事件，锁定前 60 秒现场（内存环形 + 高精度历史），
    /// 对相关进程进入 30 秒突发采样，并合成“当时最可能发生的情况”初步分析。
    /// </summary>
    private async ValueTask<PerformanceEvent> MarkLagAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        _coordinator.NoteUserMark(now);
        _coordinator.RequestBurst(TimeSpan.FromSeconds(30));

        var windowStart = now.AddSeconds(-60);
        var systemWindow = _coordinator.SystemBuffer.Snapshot()
            .Where(s => s.Timestamp >= windowStart && s.Timestamp <= now).ToList();
        var processWindow = _coordinator.ProcessBuffer.Snapshot()
            .Where(p => p.Timestamp >= windowStart && p.Timestamp <= now).ToList();

        var avgCpu = systemWindow.Count > 0 ? systemWindow.Average(s => s.CpuPercent) : 0;
        var peakCpu = systemWindow.Count > 0 ? systemWindow.Max(s => s.CpuPercent) : 0;
        var minAvailable = systemWindow.Count > 0 ? systemWindow.Min(s => s.AvailableMemoryBytes) : 0;
        var totalMemory = systemWindow.Count > 0 ? systemWindow.Max(s => s.TotalMemoryBytes) : 0;
        var topProcess = ImpactScoreCalculator.Rank(processWindow, now).FirstOrDefault();
        var linkedEvents = new List<PerformanceEvent>();
        lock (_eventLock)
            linkedEvents.AddRange(_recentEvents.Where(e => e.StartedAt >= windowStart && e.StartedAt <= now && e.Type != PerformanceEventType.UserMarkedLag).Take(5));

        var causeParts = new List<string>();
        if (systemWindow.Count > 0)
        {
            causeParts.Add($"前 60 秒系统 CPU 平均 {avgCpu:0}%（峰值 {peakCpu:0}%）");
            if (totalMemory > 0 && minAvailable > 0)
                causeParts.Add($"可用内存最低 {minAvailable / 1024.0 / 1024 / 1024:0.0} GB");
        }
        if (topProcess is not null)
            causeParts.Add($"{topProcess.Name} 影响分最高（CPU {topProcess.CpuPercent:0}%，读写 {(topProcess.ReadBytesPerSecond + topProcess.WriteBytesPerSecond) / 1024.0 / 1024:0} MB/s）");
        foreach (var linked in linkedEvents)
            causeParts.Add($"期间已记录到“{linked.Summary}”");

        var contributors = ImpactScoreCalculator.Rank(processWindow, now).Take(5)
            .Select(p => new PerformanceEventContributor(p.Process, p.Name, ImpactScoreCalculator.Compute(p, now)))
            .ToList();

        var evt = new PerformanceEvent(
            Guid.NewGuid(), PerformanceEventType.UserMarkedLag, PerformanceEventStatus.Confirmed,
            windowStart, now, 100,
            "用户反馈的响应迟缓事件",
            causeParts.Count > 0 ? $"标记时刻的分析：{string.Join("；", causeParts)}" : "标记前 60 秒内未采集到足够样本",
            [.. BuildEvidence()],
            ["查看下方事件前后 60 秒的 CPU、内存、I/O 与网络曲线",
             "结合 Top 影响进程的监听端口与连接变化判断",
             "用户标记代表主观感受，不等同于自动确认的故障"],
            topProcess?.Process, topProcess?.Name, contributors);

        AddRecentEvent(evt);
        if (_historyStore.IsEnabled()) await _historyStore.Inner.AppendEventAsync(evt, cancellationToken);
        await _logger.WriteAsync("INFO", $"用户标记卡顿事件 {evt.Id:N} 已记录（关联进程 {(topProcess?.Name ?? "无")}）");
        return evt;

        IEnumerable<string> BuildEvidence()
        {
            yield return "用户手动点击“刚才卡了”";
            if (systemWindow.Count > 0)
            {
                yield return $"前 60 秒系统采样 {systemWindow.Count} 条：CPU 平均 {avgCpu:0}%，峰值 {peakCpu:0}%";
                if (totalMemory > 0)
                    yield return $"可用内存最低 {minAvailable / 1024.0 / 1024 / 1024:0.0} GB / 共 {totalMemory / 1024.0 / 1024 / 1024:0.0} GB";
                yield return $"网络接收峰值 {systemWindow.Max(s => s.NetworkReceivedBytesPerSecond) / 1024.0:0} KB/s，发送峰值 {systemWindow.Max(s => s.NetworkSentBytesPerSecond) / 1024.0:0} KB/s";
            }
            else
            {
                yield return "标记前 60 秒无系统采样（后台记录可能刚开启）";
            }
            if (processWindow.Count > 0 && topProcess is not null)
                yield return $"影响分最高：{topProcess.Name}（PID {topProcess.Process.ProcessId}，CPU {topProcess.CpuPercent:0}%）";
            foreach (var linked in linkedEvents)
                yield return $"同时段自动事件：{linked.Summary}（可信度 {linked.Confidence}）";
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _coordinator.DisposeAsync();
        await _server.DisposeAsync();
        if (_settingsLoop is not null)
        {
            try { await _settingsLoop; }
            catch (Exception) { }
        }
        await _historyStore.DisposeAsync();
        _lifetime.Dispose();
    }

    /// <summary>按设置动态启用/停用历史写入的内嵌包装。停用时丢弃写入，读取仍委托内部库。</summary>
    private sealed class ConditionalHistoryStore : IPerformanceHistoryStore
    {
        public ConditionalHistoryStore(SqliteHistoryStore inner, Func<bool> isEnabled, Func<int> retentionDays)
        {
            Inner = inner;
            _isEnabled = isEnabled;
            _retentionDays = retentionDays;
        }

        private readonly Func<bool> _isEnabled;
        private readonly Func<int> _retentionDays;
        public SqliteHistoryStore Inner { get; }
        public bool IsEnabled() => _isEnabled();
        public bool IsUsable => Inner.IsUsable;

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        {
            Inner.ConfigureRetention(_retentionDays());
            return Inner.InitializeAsync(cancellationToken);
        }

        public ValueTask AppendSystemSampleAsync(SystemPerformanceSample sample, CancellationToken cancellationToken = default) =>
            _isEnabled() ? Inner.AppendSystemSampleAsync(sample, cancellationToken) : ValueTask.CompletedTask;

        public ValueTask AppendProcessSampleAsync(ProcessPerformanceSample sample, CancellationToken cancellationToken = default) =>
            _isEnabled() ? Inner.AppendProcessSampleAsync(sample, cancellationToken) : ValueTask.CompletedTask;

        public ValueTask AppendEventAsync(PerformanceEvent evt, CancellationToken cancellationToken = default) =>
            _isEnabled() ? Inner.AppendEventAsync(evt, cancellationToken) : ValueTask.CompletedTask;

        public ValueTask AppendCollectorHealthAsync(CollectorHealthSnapshot sample, CancellationToken cancellationToken = default) =>
            _isEnabled() ? Inner.AppendCollectorHealthAsync(sample, cancellationToken) : ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<CollectorHealthSnapshot>> QueryCollectorHealthAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Inner.QueryCollectorHealthAsync(from, to, cancellationToken);

        public ValueTask<IReadOnlyList<ProcessHistoryPoint>> QueryProcessSeriesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Inner.QueryProcessSeriesAsync(from, to, cancellationToken);

        public ValueTask<IReadOnlyList<PortActivitySummary>> QueryPortActivityAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Inner.QueryPortActivityAsync(from, to, cancellationToken);

        public ValueTask AppendConnectionAsync(TcpConnectionRecord connection, CancellationToken cancellationToken = default) =>
            _isEnabled() ? Inner.AppendConnectionAsync(connection, cancellationToken) : ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<TcpConnectionRecord>> QueryConnectionsAsync(ConnectionQuery query, CancellationToken cancellationToken = default) =>
            Inner.QueryConnectionsAsync(query, cancellationToken);

        public ValueTask AppendPortSessionAsync(PortSessionRecord session, CancellationToken cancellationToken = default) =>
            _isEnabled() ? Inner.AppendPortSessionAsync(session, cancellationToken) : ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<PortUsageSummary>> QueryPortUsageAsync(int port, PortProtocol protocol, DateTimeOffset from, DateTimeOffset to, int limit = 20, CancellationToken cancellationToken = default) =>
            Inner.QueryPortUsageAsync(port, protocol, from, to, limit, cancellationToken);

        public ValueTask<IReadOnlyList<SystemPerformanceSample>> QuerySystemAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Inner.QuerySystemAsync(from, to, cancellationToken);

        public ValueTask<IReadOnlyList<SystemPerformanceSample>> QuerySystemSeriesAsync(DateTimeOffset from, DateTimeOffset to, int maxPoints = 720, CancellationToken cancellationToken = default) =>
            Inner.QuerySystemSeriesAsync(from, to, maxPoints, cancellationToken);

        public ValueTask<HistoryCoverage> QueryCoverageAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Inner.QueryCoverageAsync(from, to, cancellationToken);

        public ValueTask<IReadOnlyList<ProcessPerformanceSample>> QueryProcessAsync(ProcessInstanceKey process, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Inner.QueryProcessAsync(process, from, to, cancellationToken);

        public ValueTask<IReadOnlyList<PerformanceEvent>> QueryEventsAsync(DateTimeOffset from, DateTimeOffset to, int limit = 200, CancellationToken cancellationToken = default) =>
            Inner.QueryEventsAsync(from, to, limit, cancellationToken);

        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }
}
