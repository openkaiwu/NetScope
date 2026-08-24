using System.Collections.Immutable;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.Core.Services;

public sealed class DiagnosticEngine(INetworkSnapshotProvider snapshotProvider, IEnumerable<IDiagnosticProbe> probes) : IDiagnosticEngine
{
    private readonly IDiagnosticProbe[] _probes = probes.OrderBy(p => p.Stage).ToArray();

    public async ValueTask<DiagnosticRun> RunAsync(IReadOnlyList<DiagnosticTarget> targets, TimeSpan timeout, CancellationToken cancellationToken = default)
        => await RunCoreAsync(targets, timeout, null, cancellationToken);

    public async ValueTask<DiagnosticRun> RunWithProgressAsync(IReadOnlyList<DiagnosticTarget> targets, TimeSpan timeout,
        IProgress<DiagnosticStageResult> progress, CancellationToken cancellationToken = default)
        => await RunCoreAsync(targets, timeout, progress, cancellationToken);

    private async ValueTask<DiagnosticRun> RunCoreAsync(IReadOnlyList<DiagnosticTarget> targets, TimeSpan timeout,
        IProgress<DiagnosticStageResult>? progress, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var results = ImmutableArray.CreateBuilder<DiagnosticStageResult>();

        try
        {
            var snapshot = await snapshotProvider.CaptureAsync(linkedCts.Token);
            for (var index = 0; index < _probes.Length; index++)
            {
                linkedCts.Token.ThrowIfCancellationRequested();
                var probe = _probes[index];
                var result = await probe.RunAsync(snapshot, targets, linkedCts.Token);
                results.Add(result);
                progress?.Report(result);
                if (result.Status == DiagnosticStatus.Fault && result.Stage is DiagnosticStage.Adapter or DiagnosticStage.IpDhcp)
                {
                    foreach (var remaining in _probes.Skip(index + 1))
                    {
                        var now = DateTimeOffset.Now;
                        var skipped = new DiagnosticStageResult(remaining.Stage, DiagnosticStatus.NotTested, now, now,
                            ImmutableDictionary<string, string>.Empty, [$"上游阶段“{result.Stage}”故障，未继续主动探测"],
                            "上游链路尚未恢复", .98, ["先处理首个故障阶段，再重新运行诊断"]);
                        results.Add(skipped);
                        progress?.Report(skipped);
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            var timeoutResult = new DiagnosticStageResult(DiagnosticStage.Target, DiagnosticStatus.NotTested, now, now,
                ImmutableDictionary<string, string>.Empty, ["快速诊断达到 15 秒总时限"], "检测超时",
                .95, ["检查本地链路后重试，或减少自定义目标"], "诊断总超时", true);
            results.Add(timeoutResult);
            progress?.Report(timeoutResult);
        }
        catch (OperationCanceledException)
        {
            return CreateRun(started, results.ToImmutable(), true);
        }

        return CreateRun(started, results.ToImmutable(), false);
    }

    private static DiagnosticRun CreateRun(DateTimeOffset started, ImmutableArray<DiagnosticStageResult> stages, bool cancelled)
    {
        var tested = stages.Where(x => x.Status != DiagnosticStatus.NotTested).ToArray();
        var overall = tested.Any(x => x.Status == DiagnosticStatus.Fault) ? DiagnosticStatus.Fault :
            tested.Any(x => x.Status == DiagnosticStatus.Degraded) ? DiagnosticStatus.Degraded :
            tested.Length > 0 ? DiagnosticStatus.Healthy : DiagnosticStatus.NotTested;
        var failing = stages.FirstOrDefault(x => x.Status == DiagnosticStatus.Fault) ?? stages.FirstOrDefault(x => x.Status == DiagnosticStatus.Degraded);
        var summary = cancelled ? "诊断已取消" : failing is null ? "网络链路工作正常" : failing.MostLikelyCause;
        var confidence = tested.Length == 0 ? 0 : Math.Clamp(tested.Average(x => x.Confidence), 0, 1);
        return new(Guid.NewGuid(), started, DateTimeOffset.Now, stages, summary, overall, confidence, cancelled);
    }
}
