using NetScope.Core.Models;

namespace NetScope.Core.Services;

public sealed record SelfImpactGuardOptions
{
    public double CpuHighPercent { get; init; } = 1.0;
    public double CpuSeverePercent { get; init; } = 3.0;
    public long MemoryHighBytes { get; init; } = 50L * 1024 * 1024;
    public long MemorySevereBytes { get; init; } = 100L * 1024 * 1024;
    public long WriteHighBytesPerSecond { get; init; } = 4 * 1024;
    public long WriteSevereBytesPerSecond { get; init; } = 32 * 1024;
    public double CycleHighMilliseconds { get; init; } = 500;
    public double CycleSevereMilliseconds { get; init; } = 1500;
    public int BreachSamples { get; init; } = 10;
    public int RecoverySamples { get; init; } = 30;
}

/// <summary>Collector 自身影响保护。采用连续样本与滞回，避免瞬时 JIT/GC 导致频率振荡。</summary>
public sealed class SelfImpactGuard(SelfImpactGuardOptions? options = null)
{
    private readonly SelfImpactGuardOptions _options = options ?? new();
    private int _highCount;
    private int _severeCount;
    private int _healthyCount;
    public SamplingMode Mode { get; private set; }
    public IReadOnlyList<string> LastReasons { get; private set; } = [];

    public SamplingProfile Evaluate(double cpuPercent, long privateBytes, long writeBytesPerSecond,
        double cycleDurationMilliseconds)
    {
        var high = cpuPercent > _options.CpuHighPercent || privateBytes > _options.MemoryHighBytes ||
                   writeBytesPerSecond > _options.WriteHighBytesPerSecond || cycleDurationMilliseconds > _options.CycleHighMilliseconds;
        var severe = cpuPercent > _options.CpuSeverePercent || privateBytes > _options.MemorySevereBytes ||
                     writeBytesPerSecond > _options.WriteSevereBytesPerSecond || cycleDurationMilliseconds > _options.CycleSevereMilliseconds;
        var reasons = new List<string>();
        if (cpuPercent > _options.CpuHighPercent) reasons.Add($"CPU {cpuPercent:0.00}% 超过 {_options.CpuHighPercent:0.00}% 预算");
        if (privateBytes > _options.MemoryHighBytes) reasons.Add($"私有内存 {privateBytes / 1024.0 / 1024:0.0} MB 超过 {_options.MemoryHighBytes / 1024.0 / 1024:0} MB 预算");
        if (writeBytesPerSecond > _options.WriteHighBytesPerSecond) reasons.Add($"磁盘写入 {writeBytesPerSecond / 1024.0:0.0} KB/s 超过预算");
        if (cycleDurationMilliseconds > _options.CycleHighMilliseconds) reasons.Add($"采样周期耗时 {cycleDurationMilliseconds:0} ms 超过预算");
        LastReasons = reasons;

        _highCount = high ? _highCount + 1 : 0;
        _severeCount = severe ? _severeCount + 1 : 0;
        _healthyCount = high ? 0 : _healthyCount + 1;

        if (_severeCount >= _options.BreachSamples)
        {
            Mode = SamplingMode.Minimal;
            _highCount = 0;
            _severeCount = 0;
        }
        else if (_highCount >= _options.BreachSamples && Mode == SamplingMode.Normal)
        {
            Mode = SamplingMode.Reduced;
            _highCount = 0;
        }
        else if (_highCount >= _options.BreachSamples && Mode == SamplingMode.Reduced)
        {
            Mode = SamplingMode.Minimal;
            _highCount = 0;
        }
        else if (_healthyCount >= _options.RecoverySamples)
        {
            Mode = Mode switch { SamplingMode.Minimal => SamplingMode.Reduced, SamplingMode.Reduced => SamplingMode.Normal, _ => Mode };
            _healthyCount = 0;
        }
        return Profile(Mode);
    }

    public static SamplingProfile Profile(SamplingMode mode) => mode switch
    {
        SamplingMode.Reduced => new(mode, 2000, 5000, 15),
        SamplingMode.Minimal => new(mode, 5000, 10000, 8),
        _ => new(mode, 1000, 2000, 25)
    };
}
