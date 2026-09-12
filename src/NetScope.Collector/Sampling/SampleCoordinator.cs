using System.Collections.Immutable;
using System.Diagnostics;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;
using NetScope.Core.Services;
using NetScope.Windows.Logging;

namespace NetScope.Collector.Sampling;

/// <summary>
/// 后台采样协调器：以 1 秒为基准采样系统与进程指标，每 2 秒刷新端口快照。
/// 进程读数按 (PID, 启动时间) 键控做差量，避免 PID 复用串扰。
/// 事件或用户标记触发后进入最长 30 秒的 500ms 突发采样；历史以 5 秒粒度批量落盘（突发期间提高到 500ms）。
/// </summary>
public sealed class SampleCoordinator : IAsyncDisposable
{
    private const int SystemBufferCapacity = 60 * 60;       // 1 秒精度，1 小时
    private const int ProcessBufferCapacity = 60 * 60 * 8;  // 覆盖并发进程的采样
    private static readonly TimeSpan BurstInterval = TimeSpan.FromMilliseconds(500);

    private readonly TcpConnectionTracker _connections = new();
    private readonly Func<bool> _historyEnabled;
    private readonly Func<int, ProcessIdentity?>? _connectionIdentity;
    private readonly ICollectorSelfMonitor? _selfMonitor;
    private readonly SelfImpactGuard _selfGuard;
    private bool _wasHistoryEnabled;
    private DateTimeOffset _lastConnectionWrite;
    private DateTimeOffset? _lastMark;
    private IReadOnlyList<TcpConnectionRecord> _currentConnections = [];
    public IReadOnlyList<TcpConnectionRecord> Connections { get { lock (_stateLock) return _currentConnections; } }
    private readonly ISystemPerformanceProvider _systemProvider;
    private readonly IProcessPerformanceProvider _processProvider;
    private readonly INetworkSnapshotProvider _networkProvider;
    private readonly IPortTableProvider _portProvider;
    private readonly IPerformanceEventEngine? _eventEngine;
    private readonly IPerformanceHistoryStore? _historyStore;
    private readonly Func<int?>? _foregroundPid;
    private readonly Func<bool> _performanceEnabled;
    private readonly Action<PerformanceEvent>? _onEvent;
    private readonly PortSessionTracker? _portSessions;
    private readonly Func<int, string?>? _processNameResolver;
    private readonly RollingFileLogger? _logger;
    private readonly CancellationTokenSource _lifetime = new();

    private readonly object _stateLock = new();
    private SystemPerformanceReading? _previousSystem;
    private readonly Dictionary<ProcessInstanceKey, ProcessPerformanceReading> _previousProcess = new();
    private readonly Dictionary<ProcessInstanceKey, ProcessPerformanceSample> _latestProcessSamples = new();
    private SystemPerformanceSample? _currentSystem;
    private ImmutableArray<ProcessPerformanceSample> _currentProcesses = [];
    private ImmutableArray<PortBindingSnapshot> _lastPorts = [];
    private Task? _loop;
    private bool _started;
    private DateTimeOffset _burstUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHistoryWrite = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHealthWrite = DateTimeOffset.MinValue;
    private CollectorHealthSnapshot? _currentHealth;
    private SamplingProfile _samplingProfile = SelfImpactGuard.Profile(SamplingMode.Normal);

    public SampleCoordinator(
        ISystemPerformanceProvider systemProvider,
        IProcessPerformanceProvider processProvider,
        INetworkSnapshotProvider networkProvider,
        IPortTableProvider portProvider,
        RollingFileLogger? logger = null,
        IPerformanceEventEngine? eventEngine = null,
        IPerformanceHistoryStore? historyStore = null,
        Func<int?>? foregroundPidProvider = null,
        Action<PerformanceEvent>? onEvent = null,
        Func<bool>? performanceEnabled = null,
        PortSessionTracker? portSessions = null,
        Func<int, string?>? processNameResolver = null, Func<bool>? historyEnabled = null,
        Func<int, ProcessIdentity?>? connectionIdentity = null,
        ICollectorSelfMonitor? selfMonitor = null,
        SelfImpactGuard? selfImpactGuard = null)
    {
        _historyEnabled = historyEnabled ?? (() => true);
        _connectionIdentity = connectionIdentity;
        _selfMonitor = selfMonitor;
        _selfGuard = selfImpactGuard ?? new();
        _systemProvider = systemProvider;
        _processProvider = processProvider;
        _networkProvider = networkProvider;
        _portProvider = portProvider;
        _logger = logger;
        _eventEngine = eventEngine;
        _historyStore = historyStore;
        _foregroundPid = foregroundPidProvider;
        _onEvent = onEvent;
        _performanceEnabled = performanceEnabled ?? (() => true);
        _portSessions = portSessions;
        _processNameResolver = processNameResolver;
    }

    public MemoryRingBuffer<SystemPerformanceSample> SystemBuffer { get; } = new(SystemBufferCapacity);
    public MemoryRingBuffer<ProcessPerformanceSample> ProcessBuffer { get; } = new(ProcessBufferCapacity);

    /// <summary>请求进入突发采样（500ms）一段时间；事件触发与用户标记共用。</summary>
    public void RequestBurst(TimeSpan duration) => _burstUntil = DateTimeOffset.Now + duration;

    /// <summary>登记用户标记时间，事件引擎据此提升邻近进程的贡献权重。</summary>
    public void NoteUserMark(DateTimeOffset markedAt) { lock (_stateLock) _lastMark = markedAt; _eventEngine?.NoteUserMark(markedAt); }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _loop = Task.Run(() => RunAsync(_lifetime.Token));
    }

    public SystemPerformanceSample? CurrentSystem
    {
        get { lock (_stateLock) return _currentSystem; }
    }

    public ImmutableArray<ProcessPerformanceSample> CurrentProcesses
    {
        get { lock (_stateLock) return _currentProcesses; }
    }

    public ImmutableArray<PortBindingSnapshot> LastPorts
    {
        get { lock (_stateLock) return _lastPorts; }
    }

    public CollectorHealthSnapshot? CurrentHealth
    {
        get { lock (_stateLock) return _currentHealth; }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var nextPortSample = DateTimeOffset.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            var cycleStarted = Stopwatch.GetTimestamp();
            var modeBefore = _samplingProfile.Mode;
            try
            {
                var performanceOn = _performanceEnabled();
                if (performanceOn)
                {
                    await SampleSystemAsync(cancellationToken);
                    await SampleProcessesAsync(cancellationToken);
                    lock (_stateLock)
                    {
                        if (_currentSystem is { } current)
                        {
                            _currentSystem = current with { Responsiveness = ResponsivenessCalculator.Compute(current, _currentProcesses, _lastMark) };
                            SystemBuffer.Add(_currentSystem);
                        }
                    }
                    await EvaluateEventsAsync(cancellationToken);
                    await WriteHistoryAsync(cancellationToken);
                }
                else
                {
                    lock (_stateLock)
                    {
                        _previousSystem = null;
                        _previousProcess.Clear();
                        _latestProcessSamples.Clear();
                        _currentProcesses = [];
                        _currentSystem = null;
                    }
                }
                if (DateTimeOffset.Now >= nextPortSample)
                {
                    nextPortSample = DateTimeOffset.Now.AddMilliseconds(_samplingProfile.PortIntervalMilliseconds);
                    await SamplePortsAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (_logger is not null)
                    await _logger.WriteAsync("ERROR", $"采样周期失败: {ex.Message}");
            }

            try
            {
                if (_selfMonitor is not null)
                {
                    var usage = _selfMonitor.Read();
                    var cycleMs = Stopwatch.GetElapsedTime(cycleStarted).TotalMilliseconds;
                    var privateBytes = usage.PrivateBytes > 0 ? usage.PrivateBytes : usage.WorkingSetBytes;
                    _samplingProfile = _selfGuard.Evaluate(usage.CpuPercent, privateBytes, usage.WriteBytesPerSecond, cycleMs);
                    var health = new CollectorHealthSnapshot(usage.Timestamp, usage.CpuPercent, usage.WorkingSetBytes,
                        usage.ReadBytesPerSecond, usage.WriteBytesPerSecond, cycleMs, _samplingProfile, _selfGuard.LastReasons,
                        usage.PrivateBytes);
                    lock (_stateLock) _currentHealth = health;
                    if (_historyStore is not null && _historyEnabled() &&
                        (health.Timestamp - _lastHealthWrite >= TimeSpan.FromSeconds(30) || modeBefore != _samplingProfile.Mode))
                    {
                        await _historyStore.AppendCollectorHealthAsync(health, cancellationToken);
                        _lastHealthWrite = health.Timestamp;
                    }
                    if (modeBefore != _samplingProfile.Mode && _logger is not null)
                        await _logger.WriteAsync("WARN", $"Self Impact Guard: {modeBefore} -> {_samplingProfile.Mode}; {string.Join("; ", health.Reasons)}");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (_logger is not null) await _logger.WriteAsync("ERROR", $"Collector 自监控失败: {ex.Message}");
            }

            var interval = _samplingProfile.Mode == SamplingMode.Normal && DateTimeOffset.Now < _burstUntil
                ? BurstInterval : TimeSpan.FromMilliseconds(_samplingProfile.PerformanceIntervalMilliseconds);
            try { await Task.Delay(interval, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SampleSystemAsync(CancellationToken cancellationToken)
    {
        var reading = await _systemProvider.ReadAsync(cancellationToken);
        NetworkAdapterSnapshot? active = null;
        var networkAvailable = true;
        try
        {
            var network = await _networkProvider.CaptureAsync(cancellationToken);
            active = network.ActiveAdapter;
            networkAvailable = network.IsNetworkAvailable;
        }
        catch (Exception ex)
        {
            if (_logger is not null)
                await _logger.WriteAsync("WARN", $"网络快照失败: {ex.Message}");
        }

        var withNetwork = reading with
        {
            NetworkReceivedBytes = active?.BytesReceived ?? 0,
            NetworkSentBytes = active?.BytesSent ?? 0
        };

        lock (_stateLock)
        {
            if (_previousSystem is { } previous)
            {
                var elapsed = (withNetwork.Timestamp - previous.Timestamp).TotalSeconds;
                if (elapsed > 0)
                {
                    var sample = PerformanceMath.ToSample(previous, withNetwork, elapsed) with
                    {
                        // 活动网卡掉线判定退化：无活动网卡时回退到“任意网卡可用”，避免探测盲区误报
                        NetworkLinkUp = active?.IsUp ?? networkAvailable,
                        NetworkAdapterName = active?.Name ?? string.Empty
                    };
                    _currentSystem = sample;
                }
            }
            _previousSystem = withNetwork;
        }
    }

    private async Task SampleProcessesAsync(CancellationToken cancellationToken)
    {
        var readings = await _processProvider.ReadAsync(cancellationToken);
        var foregroundPid = _foregroundPid?.Invoke();

        var seen = new HashSet<ProcessInstanceKey>(readings.Length);
        lock (_stateLock)
        {
            foreach (var reading in readings)
            {
                seen.Add(reading.Process);
                if (_previousProcess.TryGetValue(reading.Process, out var previous))
                {
                    var elapsed = (reading.Timestamp - previous.Timestamp).TotalSeconds;
                    if (elapsed > 0)
                    {
                        var sample = PerformanceMath.ToSample(previous, reading, elapsed) with
                        {
                            IsForeground = foregroundPid == reading.Process.ProcessId
                        };
                        ProcessBuffer.Add(sample);
                        _latestProcessSamples[reading.Process] = sample;
                    }
                }
                else
                {
                    // 首次出现尚无前值，输出零速率占位
                    _latestProcessSamples[reading.Process] = new ProcessPerformanceSample(
                        reading.Process, reading.Timestamp, reading.Name,
                        0, reading.WorkingSetBytes, reading.PrivateBytes, 0, 0, 0, 0,
                        reading.IsAccessible, reading.StatusMessage, foregroundPid == reading.Process.ProcessId);
                }
                _previousProcess[reading.Process] = reading;
            }

            // 清理已退出进程的上一轮读数与最新采样（按 PID 已不在枚举中判断）
            foreach (var key in _latestProcessSamples.Keys.Where(k => !seen.Contains(k)).ToList())
                _latestProcessSamples.Remove(key);
            foreach (var key in _previousProcess.Keys.Where(k => !seen.Contains(k)).ToList())
                _previousProcess.Remove(key);

            _currentProcesses = _latestProcessSamples.Values.ToImmutableArray();
        }
    }

    private async Task EvaluateEventsAsync(CancellationToken cancellationToken)
    {
        if (_eventEngine is null) return;
        SystemPerformanceSample? system;
        ImmutableArray<ProcessPerformanceSample> processes;
        lock (_stateLock)
        {
            system = _currentSystem;
            processes = _currentProcesses;
        }
        if (system is null) return;

        var events = await _eventEngine.EvaluateAsync(system, processes, DateTimeOffset.Now, cancellationToken);
        foreach (var evt in events)
        {
            _onEvent?.Invoke(evt);
            if (_historyStore is not null)
                await _historyStore.AppendEventAsync(evt, cancellationToken);
        }
        if (events.Count > 0) RequestBurst(TimeSpan.FromSeconds(30));
    }

    /// <summary>历史落盘：常规 5 秒粒度；突发期间 500ms，保证事件现场高精度保留。</summary>
    private async Task WriteHistoryAsync(CancellationToken cancellationToken)
    {
        if (_historyStore is null) return;

        SystemPerformanceSample? system;
        ImmutableArray<ProcessPerformanceSample> processes;
        lock (_stateLock)
        {
            system = _currentSystem;
            processes = _currentProcesses;
        }
        if (system is null) return;

        var now = DateTimeOffset.Now;
        var inBurst = now < _burstUntil;
        var minInterval = inBurst ? BurstInterval : TimeSpan.FromSeconds(5);
        if (now - _lastHistoryWrite < minInterval) return;
        _lastHistoryWrite = now;

        await _historyStore.AppendSystemSampleAsync(system, cancellationToken);
        foreach (var process in ImpactScoreCalculator.Rank(processes).Take(_samplingProfile.ProcessHistoryTopN))
            await _historyStore.AppendProcessSampleAsync(process, cancellationToken);
    }

    private async Task SamplePortsAsync(CancellationToken cancellationToken)
    {
        var ports = await _portProvider.CaptureAsync(cancellationToken);
        lock (_stateLock) _lastPorts = ports;

        var now = DateTimeOffset.Now;
        var enabled = _historyEnabled();
        if (enabled != _wasHistoryEnabled)
        {
            _connections.Stop("历史记录设置改变，观察区间结束");
            _portSessions?.CloseAll(now); // 丢弃跨开关边界的会话，避免把暂停时段计入历史时长。
            _wasHistoryEnabled = enabled;
        }
        var identities = CurrentProcesses.GroupBy(p => p.Process.ProcessId).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Process.StartedAt).First());
        var changes = _connections.Feed(ports, now, pid => _connectionIdentity is not null ? _connectionIdentity(pid) : identities.TryGetValue(pid, out var p)
            ? new ProcessIdentity(pid, p.Process.StartedAt, p.Name, null, p.IsAccessible, false)
            : null);
        lock (_stateLock) _currentConnections = _connections.Active;
        if (enabled && _historyStore is not null)
        {
            foreach (var connection in changes) await _historyStore.AppendConnectionAsync(connection, cancellationToken);
            if (now - _lastConnectionWrite >= TimeSpan.FromSeconds(30))
            {
                foreach (var connection in _connections.Active) await _historyStore.AppendConnectionAsync(connection, cancellationToken);
                _lastConnectionWrite = now;
            }
        }

        // 端口占用会话：对快照差分，结束的会话写入历史（受 HistoryEnabled 门控）
        if (_portSessions is not null)
        {
            var closed = _portSessions.Feed(ports, DateTimeOffset.Now, _processNameResolver);
            if (closed.Count > 0 && _historyStore is not null)
            {
                foreach (var session in closed)
                    await _historyStore.AppendPortSessionAsync(session, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_loop is not null)
        {
            try { await _loop; }
            catch (Exception) { }
        }

        // 退出前把未结束的端口会话收尾落盘，避免长会话丢失
        if (_portSessions is not null && _historyStore is not null)
        {
            try
            {
                foreach (var session in _portSessions.CloseAll(DateTimeOffset.Now))
                    await _historyStore.AppendPortSessionAsync(session);
            }
            catch (Exception) { }
        }
        if (_historyStore is not null)
            foreach (var connection in _connections.Stop("采集器退出，后续状态未知"))
                await _historyStore.AppendConnectionAsync(connection);
        (_systemProvider as IDisposable)?.Dispose();
        (_selfMonitor as IDisposable)?.Dispose();
        _lifetime.Dispose();
    }
}
