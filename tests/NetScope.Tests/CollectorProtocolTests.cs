using System.Collections.Immutable;
using NetScope.Core.Models;
using NetScope.Windows.Ipc;

namespace NetScope.Tests;

public sealed class CollectorProtocolTests
{
    [Fact]
    public void PortSnapshotDtoRoundTrips()
    {
        var now = DateTimeOffset.Parse("2026-08-24T12:00:00+08:00");
        var bindings = new[]
        {
            new PortBindingSnapshot(new(PortProtocol.Tcp, IpAddressFamily.IPv4, "0.0.0.0", 443, 1234, "Listen"), now)
        }.ToImmutableArray();

        var json = CollectorProtocol.Serialize(CollectorDtos.ToDto(bindings));
        var dto = CollectorProtocol.Deserialize<PortsSnapshotDto>(json)!;
        var restored = CollectorDtos.ToModels(dto);

        var binding = Assert.Single(restored);
        Assert.Equal(443, binding.Port);
        Assert.Equal(1234, binding.ProcessId);
        Assert.Equal(PortProtocol.Tcp, binding.Protocol);
        Assert.Equal("Listen", binding.State);
    }

    [Fact]
    public void ProcessSampleDtoRoundTrips()
    {
        var sample = new ProcessPerformanceSample(
            new(42, DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch.AddSeconds(1), "app.exe",
            12.5, 100_000, 80_000, 1000, 2000, 30, 40, true, null);

        var json = CollectorProtocol.Serialize(CollectorDtos.ToDto(sample));
        var dto = CollectorProtocol.Deserialize<ProcessSampleDto>(json)!;
        var restored = CollectorDtos.ToModel(dto);

        Assert.Equal(42, restored.Process.ProcessId);
        Assert.Equal(12.5, restored.CpuPercent, precision: 2);
        Assert.Equal(100_000, restored.WorkingSetBytes);
        Assert.Equal(30, restored.ReadOperationsPerSecond);
        Assert.True(restored.IsAccessible);
    }

    [Fact]
    public void SystemSampleDtoRoundTrips()
    {
        var sample = new SystemPerformanceSample(DateTimeOffset.UnixEpoch.AddSeconds(2), 37.5, 8_000_000_000, 16_000_000_000, 500_000, 200_000);

        var json = CollectorProtocol.Serialize(CollectorDtos.ToDto(sample));
        var dto = CollectorProtocol.Deserialize<SystemSampleDto>(json)!;
        var restored = CollectorDtos.ToModel(dto);

        Assert.Equal(37.5, restored.CpuPercent, precision: 2);
        Assert.Equal(8_000_000_000, restored.AvailableMemoryBytes);
        Assert.Equal(500_000, restored.NetworkReceivedBytesPerSecond);
    }

    [Fact]
    public void EnvelopeSerializesAndDeserializes()
    {
        var envelope = new IpcEnvelope(CollectorProtocol.OpPing, "req-1", true, null, "pong");
        var json = CollectorProtocol.Serialize(envelope);
        var restored = CollectorProtocol.Deserialize<IpcEnvelope>(json)!;

        Assert.Equal(CollectorProtocol.OpPing, restored.Op);
        Assert.Equal("req-1", restored.RequestId);
        Assert.True(restored.Ok);
        Assert.Equal("pong", restored.Json);
    }

    [Fact]
    public void V06HealthAndInsightsRoundTripWithoutLosingEvidence()
    {
        Assert.Equal(3, CollectorProtocol.ProtocolVersion);
        Assert.Equal("1.1.0", CollectorProtocol.ServerVersion); // 版本随发布提升，协议兼容性由 ProtocolVersion 保证
        Assert.Contains("v4", CollectorProtocol.PipeName, StringComparison.Ordinal);

        var at = DateTimeOffset.Parse("2026-08-31T12:00:00+08:00");
        var health = new CollectorHealthSnapshot(at, 0.25, 40_000_000, 100, 200, 15,
            new(SamplingMode.Normal, 1000, 2000, 25), ["证据"], 25_000_000);
        var restoredHealth = CollectorProtocol.Deserialize<CollectorHealthSnapshot>(CollectorProtocol.Serialize(health));
        Assert.NotNull(restoredHealth);
        Assert.Equal("证据", Assert.Single(restoredHealth.Reasons));
        Assert.Equal(25_000_000, restoredHealth.PrivateBytes);

        var sourceId = Guid.NewGuid();
        var insight = new InsightItem(Guid.NewGuid(), InsightKind.LagSummary, "标题", "摘要", 85,
            at.AddDays(-1), at, "app.exe", ["证据一", "证据二"], [sourceId], 42, at.AddHours(-3), 8080, PortProtocol.Tcp);
        var restored = Assert.Single(CollectorProtocol.Deserialize<InsightItem[]>(CollectorProtocol.Serialize(new[] { insight }))!);
        Assert.Equal(InsightKind.LagSummary, restored.Kind);
        Assert.Equal(2, restored.Evidence.Count);
        Assert.Equal(sourceId, Assert.Single(restored.SourceEventIds));
        Assert.Equal(42, restored.ProcessId);
        Assert.Equal(at.AddHours(-3), restored.ProcessStartedAt);
        Assert.Equal(8080, restored.Port);
        Assert.Equal(PortProtocol.Tcp, restored.Protocol);
    }
}
