using System.Collections.Immutable;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Tests;

public sealed class DiagnosticEngineTests
{
    [Fact]
    public async Task ReportsFirstDegradedStageWithoutTurningIcpmOnlyFailureIntoFault()
    {
        var engine = new DiagnosticEngine(new FakeSnapshot(),
        [
            new FakeProbe(DiagnosticStage.Gateway, DiagnosticStatus.Degraded, "网关可能屏蔽 ICMP", .6),
            new FakeProbe(DiagnosticStage.Dns, DiagnosticStatus.Healthy, "DNS 正常", .95),
            new FakeProbe(DiagnosticStage.Internet, DiagnosticStatus.Healthy, "HTTPS 正常", .98)
        ]);
        var run = await engine.RunAsync([new("Test", "example.com")], TimeSpan.FromSeconds(2));
        Assert.Equal(DiagnosticStatus.Degraded, run.OverallStatus);
        Assert.DoesNotContain("断网", run.Summary);
    }

    [Fact]
    public async Task ReportsEachCompletedStageInOrder()
    {
        var received = new List<DiagnosticStageResult>();
        var engine = new DiagnosticEngine(new FakeSnapshot(),
        [
            new FakeProbe(DiagnosticStage.Local, DiagnosticStatus.Healthy, "本机正常", .99),
            new FakeProbe(DiagnosticStage.Dns, DiagnosticStatus.Healthy, "DNS 正常", .95),
            new FakeProbe(DiagnosticStage.Internet, DiagnosticStatus.Degraded, "公网退化", .8)
        ]);

        var run = await engine.RunWithProgressAsync([new("Test", "example.com")], TimeSpan.FromSeconds(2), new ImmediateProgress(received.Add));

        Assert.Equal([DiagnosticStage.Local, DiagnosticStage.Dns, DiagnosticStage.Internet], received.Select(x => x.Stage));
        Assert.Equal(DiagnosticStatus.Degraded, run.OverallStatus);
    }

    [Fact]
    public async Task StopsActiveProbingAfterAdapterFaultAndMarksDownstreamUntested()
    {
        var downstream = new CountingProbe(DiagnosticStage.Dns);
        var engine = new DiagnosticEngine(new FakeSnapshot(),
        [
            new FakeProbe(DiagnosticStage.Adapter, DiagnosticStatus.Fault, "无可用网卡", .99),
            downstream
        ]);

        var run = await engine.RunAsync([], TimeSpan.FromSeconds(2));

        Assert.Equal(0, downstream.Count);
        Assert.Equal(DiagnosticStatus.NotTested, run.Stages.Single(x => x.Stage == DiagnosticStage.Dns).Status);
    }

    private sealed class FakeSnapshot : INetworkSnapshotProvider
    {
        public ValueTask<NetworkSnapshot> CaptureAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new NetworkSnapshot(DateTimeOffset.UtcNow, true, ImmutableArray<NetworkAdapterSnapshot>.Empty, false, true, true, null));
    }

    private sealed class FakeProbe(DiagnosticStage stage, DiagnosticStatus status, string cause, double confidence) : IDiagnosticProbe
    {
        public DiagnosticStage Stage => stage;
        public ValueTask<DiagnosticStageResult> RunAsync(NetworkSnapshot snapshot, IReadOnlyList<DiagnosticTarget> targets, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new DiagnosticStageResult(stage, status, now, now,
                ImmutableDictionary<string, string>.Empty, [cause], cause, confidence, ["建议"]));
        }
    }

    private sealed class ImmediateProgress(Action<DiagnosticStageResult> report) : IProgress<DiagnosticStageResult>
    {
        public void Report(DiagnosticStageResult value) => report(value);
    }

    private sealed class CountingProbe(DiagnosticStage stage) : IDiagnosticProbe
    {
        public int Count { get; private set; }
        public DiagnosticStage Stage => stage;
        public ValueTask<DiagnosticStageResult> RunAsync(NetworkSnapshot snapshot, IReadOnlyList<DiagnosticTarget> targets, CancellationToken cancellationToken)
        {
            Count++;
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new DiagnosticStageResult(stage, DiagnosticStatus.Healthy, now, now,
                ImmutableDictionary<string, string>.Empty, ["ok"], "ok", 1, []));
        }
    }
}
