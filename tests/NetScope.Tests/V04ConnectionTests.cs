using System.Net;
using System.Net.Sockets;
using NetScope.Core.Models;
using NetScope.Core.Services;
using NetScope.Windows.Ports;

namespace NetScope.Tests;

public sealed class V04ConnectionTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
    private static PortBindingSnapshot Row(int remotePort = 443, string state = "Established") =>
        new(new(PortProtocol.Tcp, IpAddressFamily.IPv4, "127.0.0.1", 50000, 123, state), Start, RemoteAddress: "127.0.0.1", RemotePort: remotePort);
    private static ProcessIdentity Identity(DateTimeOffset? started = null) => new(123, started ?? Start, "test.exe", null, true, false);

    [Fact]
    public void StateChangesKeepSessionWhileDifferentRemotesAndPidReuseDoNot()
    {
        var tracker = new TcpConnectionTracker();
        var first = Assert.Single(tracker.Feed([Row()], Start, _ => Identity()));
        var changed = Assert.Single(tracker.Feed([Row(state: "CloseWait")], Start.AddSeconds(2), _ => Identity()));
        Assert.Equal(first.Id, changed.Id);
        Assert.Equal("CloseWait", changed.State);
        var extra = tracker.Feed([Row(state: "CloseWait"), Row(444)], Start.AddSeconds(4), _ => Identity());
        Assert.Single(extra);
        Assert.Equal(2, tracker.Active.Count);
        var replaced = tracker.Feed([Row()], Start.AddSeconds(6), _ => Identity(Start.AddSeconds(5)));
        Assert.Equal(2, replaced.Count(c => c.EndedAt is not null));
        Assert.NotEqual(first.Id, Assert.Single(tracker.Active).Id);
    }

    [Fact]
    public void ListenAndUdpAreExcludedAndShutdownUsesLastObservation()
    {
        var tracker = new TcpConnectionTracker();
        Assert.Empty(tracker.Feed([Row(state: "Listen"), Row() with { Key = Row().Key with { Protocol = PortProtocol.Udp } }], Start, _ => Identity()));
        tracker.Feed([Row()], Start, _ => Identity());
        var closed = Assert.Single(tracker.Stop("采集器退出"));
        Assert.Equal(Start, closed.EndedAt);
        Assert.Equal("采集器退出", closed.EndReason);
        Assert.Empty(tracker.Active);
    }

    [Fact]
    public async Task WindowsProviderCapturesRealLoopbackRemoteEndpoint()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await client.ConnectAsync(IPAddress.Loopback, port);
            using var server = await listener.AcceptTcpClientAsync();
            var snapshot = await new WindowsPortTableProvider().CaptureAsync();
            Assert.Contains(snapshot, b => b.ProcessId == Environment.ProcessId && b.RemotePort == port && b.RemoteAddress == "127.0.0.1" && b.State == "Established");
        }
        finally { listener.Stop(); }
    }
}
