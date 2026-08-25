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
}
