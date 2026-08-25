using System.Collections.Immutable;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.Core.Services;

/// <summary>事件规则阈值。默认值面向桌面场景，测试可通过构造参数注入极端值。</summary>
public sealed record PerformanceEventEngineOptions
{
    public double CpuPercentThreshold { get; init; } = 85;
    public int CpuSustainSeconds { get; init; } = 10;

    /// <summary>可用内存低于总量的该比例、或低于绝对下限，视为内存压力。</summary>
    public double AvailableMemoryFraction { get; init; } = 0.10;
    public long AvailableMemoryFloorBytes { get; init; } = 512L * 1024 * 1024;
    public int MemorySustainSeconds { get; init; } = 10;

    /// <summary>全机进程读写合计速率阈值（MB/s）。进程 I/O 计数不等同于磁盘延迟，结论必须是“疑似”。</summary>
    public double TotalIoMBPerSecondThreshold { get; init; } = 80;
    public int IoSustainSeconds { get; init; } = 10;

    public int NetworkDownSustainSeconds { get; init; } = 10;

    /// <summary>同类事件关闭后的冷却时间，防止事件风暴。</summary>
    public int CooldownSeconds { get; init; } = 120;

    public int MaxContributors { get; init; } = 5;
}

/// <summary>
/// 性能事件规则引擎：CPU 争用、内存压力、I/O 压力、网络退化四条规则。
/// 每条规则运行 Normal -> Suspected -> Capturing -> Cooldown 状态机：
/// 阈值条件持续满足才创建事件（Capturing），条件消失即关闭（Closed）并进入冷却。
/// 证据在条件持续期间逐秒累积（保留峰值与最后快照），关闭时输出完整证据。
/// 所有输出结论使用“可能/疑似”语义并附带证据与可信度。
/// </summary>
public sealed class PerformanceEventEngine : IPerformanceEventEngine
{
    private readonly PerformanceEventEngineOptions _options;
    private readonly Dictionary<PerformanceEventType, RuleState> _states;
    private DateTimeOffset? _lastUserMarkAt;

    public PerformanceEventEngine(PerformanceEventEngineOptions? options = null)
    {
        _options = options ?? new PerformanceEventEngineOptions();
        _states = new Dictionary<PerformanceEventType, RuleState>
        {
            [PerformanceEventType.CpuContention] = new(),
            [PerformanceEventType.MemoryPressure] = new(),
            [PerformanceEventType.DiskIoPressure] = new(),
            [PerformanceEventType.NetworkDegradation] = new()
        };
    }

    /// <summary>登记一次用户标记时间，用于提升邻近进程在贡献分中的权重。</summary>
    public void NoteUserMark(DateTimeOffset markedAt) => _lastUserMarkAt = markedAt;

    public ValueTask<IReadOnlyList<PerformanceEvent>> EvaluateAsync(
        SystemPerformanceSample system,
        ImmutableArray<ProcessPerformanceSample> processes,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = new List<PerformanceEvent>();
        var accessible = processes.Where(p => p.IsAccessible).ToImmutableArray();

        EvaluateCpu(system, accessible, now, output);
        EvaluateMemory(system, accessible, now, output);
        EvaluateIo(system, accessible, now, output);
        EvaluateNetwork(system, now, output);

        return ValueTask.FromResult<IReadOnlyList<PerformanceEvent>>(output);
    }

    private void EvaluateCpu(SystemPerformanceSample system, ImmutableArray<ProcessPerformanceSample> processes, DateTimeOffset now, List<PerformanceEvent> output)
    {
        var state = _states[PerformanceEventType.CpuContention];
        var condition = system.CpuPercent >= _options.CpuPercentThreshold;

        if (condition)
        {
            state.PeakMetric = Math.Max(state.PeakMetric, system.CpuPercent);
            TrackContributors(state, processes);
            var top = processes.OrderByDescending(p => p.CpuPercent).ToList();
            state.LatestEvidence = BuildEvidence();
            state.LatestConfidence = ComputeConfidence(65, state, top.FirstOrDefault()?.CpuPercent ?? 0, now);
            state.LatestPrimary = top.FirstOrDefault();
            state.LatestContributors = BuildContributors(state);
            state.LatestCause = top.Count > 0
                ? $"系统 CPU 持续高于 {_options.CpuPercentThreshold:0}%，{top[0].Name} 等进程贡献显著"
                : $"系统 CPU 持续高于 {_options.CpuPercentThreshold:0}%";

            IReadOnlyList<string> BuildEvidence() =>
            [
                $"系统 CPU 高于 {_options.CpuPercentThreshold:0}%（当前 {system.CpuPercent:0}%，期间峰值 {state.PeakMetric:0}%）",
                ..TopLine(top.Take(_options.MaxContributors), p => $"{p.Name} {p.CpuPercent:0}%")
            ];
        }

        Step(state, PerformanceEventType.CpuContention, condition, now, output,
            () => Create(state, PerformanceEventType.CpuContention, now, "可能存在 CPU 争用",
                ["在进程中心查看高 CPU 进程的历史曲线与监听端口",
                 "保存工作后可重启相关应用",
                 "若长期高位，检查后台任务（索引、扫描、更新）是否在运行"]),
            (active, seconds) => Close(active, now, seconds, $"系统 CPU 连续 {seconds} 秒高于 {_options.CpuPercentThreshold:0}%（峰值 {state.PeakMetric:0}%）"));
    }

    private void EvaluateMemory(SystemPerformanceSample system, ImmutableArray<ProcessPerformanceSample> processes, DateTimeOffset now, List<PerformanceEvent> output)
    {
        var state = _states[PerformanceEventType.MemoryPressure];
        var condition = system.AvailableMemoryBytes < system.TotalMemoryBytes * _options.AvailableMemoryFraction
                        || system.AvailableMemoryBytes < _options.AvailableMemoryFloorBytes;

        if (condition)
        {
            state.PeakMetric = Math.Max(state.PeakMetric, 100.0 - system.AvailableMemoryBytes * 100.0 / Math.Max(1, system.TotalMemoryBytes));
            TrackContributors(state, processes);
            var top = processes.OrderByDescending(p => p.WorkingSetBytes).ToList();
            state.LatestEvidence = BuildEvidence();
            state.LatestConfidence = ComputeConfidence(60, state, top.FirstOrDefault()?.WorkingSetBytes / 1024.0 / 1024 / 1024 * 20 ?? 0, now);
            state.LatestPrimary = top.FirstOrDefault();
            state.LatestContributors = BuildContributors(state);
            state.LatestCause = top.Count > 0
                ? $"可用内存持续处于低位，{top[0].Name} 的工作集占用最大"
                : "可用内存持续处于低位";

            IReadOnlyList<string> BuildEvidence()
            {
                var totalGb = system.TotalMemoryBytes / 1024.0 / 1024 / 1024;
                var availGb = system.AvailableMemoryBytes / 1024.0 / 1024 / 1024;
                return
                [
                    $"可用内存低于阈值（当前 {availGb:0.0} GB / 共 {totalGb:0.0} GB，期间占用峰值 {state.PeakMetric:0}%）",
                    ..TopLine(top.Take(_options.MaxContributors), p => $"{p.Name} {p.WorkingSetBytes / 1024.0 / 1024:0} MB 工作集")
                ];
            }
        }

        Step(state, PerformanceEventType.MemoryPressure, condition, now, output,
            () => Create(state, PerformanceEventType.MemoryPressure, now, "可能存在内存压力",
                ["在进程中心查看内存占用最大的进程",
                 "关闭暂不使用的应用后观察可用内存变化",
                 "内存压力也可能来自缓存回收延迟，可稍后复查"]),
            (active, seconds) => Close(active, now, seconds, $"可用内存连续 {seconds} 秒低于阈值"));
    }

    private void EvaluateIo(SystemPerformanceSample system, ImmutableArray<ProcessPerformanceSample> processes, DateTimeOffset now, List<PerformanceEvent> output)
    {
        var state = _states[PerformanceEventType.DiskIoPressure];
        var totalIoMBs = processes.Sum(p => p.ReadBytesPerSecond + p.WriteBytesPerSecond) / 1024.0 / 1024;
        var condition = totalIoMBs >= _options.TotalIoMBPerSecondThreshold;

        if (condition)
        {
            state.PeakMetric = Math.Max(state.PeakMetric, totalIoMBs);
            TrackContributors(state, processes);
            var top = processes.OrderByDescending(p => p.ReadBytesPerSecond + p.WriteBytesPerSecond).ToList();
            state.LatestEvidence = BuildEvidence();
            state.LatestConfidence = ComputeConfidence(55, state, top.FirstOrDefault() is { } first ? (first.ReadBytesPerSecond + first.WriteBytesPerSecond) / 1024.0 / 1024 : 0, now);
            state.LatestPrimary = top.FirstOrDefault();
            state.LatestContributors = BuildContributors(state);
            state.LatestCause = top.Count > 0
                ? $"全机进程读写持续偏高（{totalIoMBs:0} MB/s），可能与 {top[0].Name} 的 I/O 活动有关"
                : $"全机进程读写持续偏高（{totalIoMBs:0} MB/s）";

            IReadOnlyList<string> BuildEvidence() =>
            [
                $"全机进程读写合计 {totalIoMBs:0} MB/s，超过 {_options.TotalIoMBPerSecondThreshold:0} MB/s 阈值（期间峰值 {state.PeakMetric:0} MB/s）",
                ..TopLine(top.Take(_options.MaxContributors).Where(p => p.ReadBytesPerSecond + p.WriteBytesPerSecond > 0), p => $"{p.Name} {(p.ReadBytesPerSecond + p.WriteBytesPerSecond) / 1024.0 / 1024:0} MB/s")
            ];
        }

        Step(state, PerformanceEventType.DiskIoPressure, condition, now, output,
            () => Create(state, PerformanceEventType.DiskIoPressure, now, "疑似磁盘 I/O 压力",
                ["在进程中心查看 I/O 读写最高的进程",
                 "进程 I/O 计数包含文件、网络与设备读写，不等同于磁盘延迟；结论需结合磁盘负载判断",
                 "若伴随卡顿，可在“刚才卡了”后回看该时段证据"]),
            (active, seconds) => Close(active, now, seconds, $"全机进程读写合计连续 {seconds} 秒高于 {_options.TotalIoMBPerSecondThreshold:0} MB/s（峰值 {state.PeakMetric:0} MB/s）"));
    }

    private void EvaluateNetwork(SystemPerformanceSample system, DateTimeOffset now, List<PerformanceEvent> output)
    {
        var state = _states[PerformanceEventType.NetworkDegradation];
        var condition = !system.NetworkLinkUp;

        if (condition)
        {
            state.LatestEvidence =
            [
                "活动网卡处于断开或不可用状态",
                ..(string.IsNullOrEmpty(system.NetworkAdapterName)
                    ? Array.Empty<string>()
                    : [$"受影响网卡：{system.NetworkAdapterName}"]),
                "判定来源为系统网卡被动状态，未主动发起网络探测"
            ];
            state.LatestConfidence = 70;
            state.LatestPrimary = null;
            state.LatestContributors = [];
            state.LatestCause = string.IsNullOrEmpty(system.NetworkAdapterName)
                ? "活动网卡持续处于断开或不可用状态"
                : $"活动网卡“{system.NetworkAdapterName}”持续处于断开或不可用状态";
        }

        Step(state, PerformanceEventType.NetworkDegradation, condition, now, output,
            () => Create(state, PerformanceEventType.NetworkDegradation, now, "网络链路可能退化",
                ["运行“网络诊断”查看本机、网卡、网关、DNS 与公网的分项证据",
                 "检查网线或 Wi-Fi 连接状态",
                 "本规则仅基于网卡被动状态，不主动发起探测"]),
            (active, seconds) => Close(active, now, seconds, $"活动网卡连续 {seconds} 秒处于断开状态"));
    }

    private PerformanceEvent Create(RuleState state, PerformanceEventType type, DateTimeOffset now, string summary, IReadOnlyList<string> recommendations) => new(
        Guid.NewGuid(), type, PerformanceEventStatus.Capturing,
        state.ConditionSince!.Value, null,
        state.LatestConfidence, summary, state.LatestCause,
        state.LatestEvidence, recommendations,
        state.LatestPrimary?.Process, state.LatestPrimary?.Name, state.LatestContributors);

    private PerformanceEvent Close(PerformanceEvent active, DateTimeOffset now, int sustainedSeconds, string headline) => active with
    {
        EndedAt = now,
        Status = PerformanceEventStatus.Closed,
        Evidence = [headline, ..active.Evidence],
        Confidence = Math.Max(active.Confidence, Math.Min(90, active.Confidence + Math.Min(10, sustainedSeconds / 10)))
    };

    /// <summary>单条规则的状态机步进：条件持续满足创建事件，条件消失关闭事件并进入冷却。</summary>
    private void Step(
        RuleState state,
        PerformanceEventType type,
        bool condition,
        DateTimeOffset now,
        List<PerformanceEvent> output,
        Func<PerformanceEvent> createEvent,
        Func<PerformanceEvent, int, PerformanceEvent> closeEvent)
    {
        // 冷却期内忽略新触发，防止事件风暴
        if (state.CooldownUntil > now) return;

        if (condition)
        {
            state.ConditionSince ??= now;
            var sustained = (int)(now - state.ConditionSince.Value).TotalSeconds;
            if (state.ActiveEvent is null && sustained >= SustainSeconds(type))
            {
                state.ActiveEvent = createEvent();
                output.Add(state.ActiveEvent);
            }
        }
        else
        {
            if (state.ActiveEvent is { } active)
            {
                var seconds = (int)Math.Max(1, (now - state.ConditionSince!.Value).TotalSeconds);
                output.Add(closeEvent(active, seconds));
                state.ActiveEvent = null;
                state.CooldownUntil = now.AddSeconds(_options.CooldownSeconds);
                Reset(state);
            }
            state.ConditionSince = null;
        }
    }

    private int SustainSeconds(PerformanceEventType type) => type switch
    {
        PerformanceEventType.CpuContention => _options.CpuSustainSeconds,
        PerformanceEventType.MemoryPressure => _options.MemorySustainSeconds,
        PerformanceEventType.DiskIoPressure => _options.IoSustainSeconds,
        PerformanceEventType.NetworkDegradation => _options.NetworkDownSustainSeconds,
        _ => 10
    };

    /// <summary>可信度 = 基础分 + 持续时间加分 + 主证据强度加分，夹在 40–90，不承诺精确归因。</summary>
    private int ComputeConfidence(int baseConfidence, RuleState state, double topMetric, DateTimeOffset now)
    {
        var confidence = baseConfidence;
        if (state.ConditionSince is { } since)
            confidence += (int)Math.Min(20, (now - since).TotalSeconds / 2);
        if (topMetric >= 50) confidence += 10;
        else if (topMetric >= 30) confidence += 5;
        return Math.Clamp(confidence, 40, 90);
    }

    /// <summary>把当前采样进程按影响分合入规则的持续观察集合（保留每个进程的峰值分）。</summary>
    private void TrackContributors(RuleState state, IEnumerable<ProcessPerformanceSample> processes)
    {
        foreach (var process in processes)
        {
            var score = ImpactScoreCalculator.Compute(process, _lastUserMarkAt);
            if (score <= 0) continue;
            if (state.Contributors.TryGetValue(process.Process, out var existing))
            {
                if (score > existing.Score) state.Contributors[process.Process] = (process.Name, score);
            }
            else
            {
                state.Contributors[process.Process] = (process.Name, score);
            }
        }
    }

    private IReadOnlyList<PerformanceEventContributor> BuildContributors(RuleState state) =>
        state.Contributors
            .OrderByDescending(kv => kv.Value.Score)
            .Take(_options.MaxContributors)
            .Select(kv => new PerformanceEventContributor(kv.Key, kv.Value.Name, kv.Value.Score))
            .ToList();

    private static void Reset(RuleState state)
    {
        state.PeakMetric = 0;
        state.Contributors.Clear();
    }

    private static IEnumerable<string> TopLine<T>(IEnumerable<T> items, Func<T, string> format)
    {
        var top = items.Take(3).Select(format).ToList();
        return top.Count > 0 ? [$"Top 进程：{string.Join("、", top)}"] : [];
    }

    private sealed class RuleState
    {
        public DateTimeOffset? ConditionSince;
        public PerformanceEvent? ActiveEvent;
        public DateTimeOffset CooldownUntil;
        public double PeakMetric;
        public Dictionary<ProcessInstanceKey, (string Name, double Score)> Contributors { get; } = new();
        public IReadOnlyList<string> LatestEvidence { get; set; } = [];
        public IReadOnlyList<PerformanceEventContributor> LatestContributors { get; set; } = [];
        public ProcessPerformanceSample? LatestPrimary;
        public int LatestConfidence;
        public string LatestCause = string.Empty;
    }
}
