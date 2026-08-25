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
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _eventLock = new();
    private readonly List<PerformanceEvent> _recentEvents = [];
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
            performanceEnabled: () => _settings.BackgroundRecording);
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
                var limit = Math.Clamp(request?.Limit ?? 100, 1, 500);
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

            default:
                throw new InvalidOperationException($"未知操作: {op}");
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
            [..BuildEvidence()],
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

        public ValueTask<IReadOnlyList<SystemPerformanceSample>> QuerySystemAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Inner.QuerySystemAsync(from, to, cancellationToken);

        public ValueTask<IReadOnlyList<ProcessPerformanceSample>> QueryProcessAsync(ProcessInstanceKey process, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Inner.QueryProcessAsync(process, from, to, cancellationToken);

        public ValueTask<IReadOnlyList<PerformanceEvent>> QueryEventsAsync(DateTimeOffset from, DateTimeOffset to, int limit = 200, CancellationToken cancellationToken = default) =>
            Inner.QueryEventsAsync(from, to, limit, cancellationToken);

        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }
}
