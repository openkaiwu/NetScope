using System.Collections.Immutable;
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
    private const int HistoryProcessTopN = 25;              // 每轮落盘的影响分 Top 进程
    private static readonly TimeSpan NormalInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BurstInterval = TimeSpan.FromMilliseconds(500);

    private readonly ISystemPerformanceProvider _systemProvider;
    private readonly IProcessPerformanceProvider _processProvider;
    private readonly INetworkSnapshotProvider _networkProvider;
    private readonly IPortTableProvider _portProvider;
    private readonly IPerformanceEventEngine? _eventEngine;
    private readonly IPerformanceHistoryStore? _historyStore;
    private readonly Func<int?>? _foregroundPid;
    private readonly Func<bool> _performanceEnabled;
    private readonly Action<PerformanceEvent>? _onEvent;
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
        Func<bool>? performanceEnabled = null)
    {
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
    }

    public MemoryRingBuffer<SystemPerformanceSample> SystemBuffer { get; } = new(SystemBufferCapacity);
    public MemoryRingBuffer<ProcessPerformanceSample> ProcessBuffer { get; } = new(ProcessBufferCapacity);

    /// <summary>请求进入突发采样（500ms）一段时间；事件触发与用户标记共用。</summary>
    public void RequestBurst(TimeSpan duration) => _burstUntil = DateTimeOffset.Now + duration;

    /// <summary>登记用户标记时间，事件引擎据此提升邻近进程的贡献权重。</summary>
    public void NoteUserMark(DateTimeOffset markedAt) => _eventEngine?.NoteUserMark(markedAt);

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

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var tick = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var performanceOn = _performanceEnabled();
                if (performanceOn)
                {
                    await SampleSystemAsync(cancellationToken);
                    await SampleProcessesAsync(cancellationToken);
                    await EvaluateEventsAsync(cancellationToken);
                    await WriteHistoryAsync(cancellationToken);
                }
                if (tick % 2 == 0) await SamplePortsAsync(cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (_logger is not null)
                    await _logger.WriteAsync("ERROR", $"采样周期失败: {ex.Message}");
            }

            tick++;
            var interval = DateTimeOffset.Now < _burstUntil ? BurstInterval : NormalInterval;
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
                    SystemBuffer.Add(sample);
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

        var seen = new HashSet<int>(readings.Length);
        lock (_stateLock)
        {
            foreach (var reading in readings)
            {
                seen.Add(reading.Process.ProcessId);
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
            foreach (var key in _latestProcessSamples.Keys.Where(k => !seen.Contains(k.ProcessId)).ToList())
                _latestProcessSamples.Remove(key);
            foreach (var key in _previousProcess.Keys.Where(k => !seen.Contains(k.ProcessId)).ToList())
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
        foreach (var process in ImpactScoreCalculator.Rank(processes).Take(HistoryProcessTopN))
            await _historyStore.AppendProcessSampleAsync(process, cancellationToken);
    }

    private async Task SamplePortsAsync(CancellationToken cancellationToken)
    {
        var ports = await _portProvider.CaptureAsync(cancellationToken);
        lock (_stateLock) _lastPorts = ports;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_loop is not null)
        {
            try { await _loop; }
            catch (Exception) { }
        }
        _lifetime.Dispose();
    }
}
