using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Tests;

public sealed class PortSessionTrackerTests
{
    private static PortBindingSnapshot Binding(int port, PortProtocol protocol, int pid, string state = "Listen") =>
        new(new PortBindingKey(protocol, IpAddressFamily.IPv4, "0.0.0.0", port, pid, state), DateTimeOffset.Now);

    [Fact]
    public void Feed_RequiresTwoConsecutiveSightings_BeforeOpeningSession()
    {
        var tracker = new PortSessionTracker();
        var t0 = new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);

        Assert.Empty(tracker.Feed([Binding(8080, PortProtocol.Tcp, 100)], t0, _ => "a.exe"));
        var closed = tracker.Feed([], t0.AddSeconds(2), _ => "a.exe");
        Assert.Empty(closed); // 候选期消失：静默丢弃，不产生会话
    }

    [Fact]
    public void Feed_EmitsSession_WhenBindingDisappears()
    {
        var tracker = new PortSessionTracker();
        var t0 = new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);

        tracker.Feed([Binding(8080, PortProtocol.Tcp, 100)], t0, _ => "a.exe");
        Assert.Empty(tracker.Feed([Binding(8080, PortProtocol.Tcp, 100)], t0.AddSeconds(2), _ => "a.exe")); // 达到阈值，会话开启

        var closed = tracker.Feed([], t0.AddSeconds(62));
        var session = Assert.Single(closed);
        Assert.Equal(8080, session.Port);
        Assert.Equal(PortProtocol.Tcp, session.Protocol);
        Assert.Equal("a.exe", session.ProcessName);
        Assert.Equal(100, session.ProcessId);
        Assert.Equal(t0, session.StartedAt);
        Assert.Equal(t0.AddSeconds(62), session.EndedAt);
        Assert.Equal(62, session.DurationSeconds, 0);
    }

    [Fact]
    public void Feed_EmitsSession_WhenOwnerChanges()
    {
        var tracker = new PortSessionTracker();
        var t0 = new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);

        tracker.Feed([Binding(8080, PortProtocol.Tcp, 100)], t0, _ => "a.exe");
        tracker.Feed([Binding(8080, PortProtocol.Tcp, 100)], t0.AddSeconds(2), _ => "a.exe");

        var closed = tracker.Feed([Binding(8080, PortProtocol.Tcp, 200)], t0.AddSeconds(30), _ => "b.exe");
        var session = Assert.Single(closed);
        Assert.Equal("a.exe", session.ProcessName);
        Assert.Equal(t0.AddSeconds(30), session.EndedAt);
    }

    [Fact]
    public void Feed_IgnoresNonListenTcp_ButCountsUdp()
    {
        var tracker = new PortSessionTracker();
        var t0 = new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);

        tracker.Feed([Binding(5353, PortProtocol.Tcp, 100, "Established")], t0);
        tracker.Feed([Binding(5353, PortProtocol.Tcp, 100, "Established")], t0.AddSeconds(2));
        Assert.Empty(tracker.Feed([], t0.AddSeconds(4))); // 非 Listen 的 TCP 连接不算会话

        tracker.Feed([Binding(53, PortProtocol.Udp, 200, "Bound")], t0, _ => "dns.exe");
        var closed = tracker.Feed([Binding(53, PortProtocol.Udp, 200, "Bound")], t0.AddSeconds(2), _ => "dns.exe");
        Assert.Empty(closed); // UDP 连续两次出现：会话开启

        closed = tracker.Feed([], t0.AddSeconds(10));
        var session = Assert.Single(closed);
        Assert.Equal(PortProtocol.Udp, session.Protocol);
        Assert.Equal("dns.exe", session.ProcessName);
    }

    [Fact]
    public void Feed_FallsBackToPidLabel_WhenNameUnresolvable()
    {
        var tracker = new PortSessionTracker();
        var t0 = new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);

        tracker.Feed([Binding(8080, PortProtocol.Tcp, 4242)], t0, _ => null);
        tracker.Feed([Binding(8080, PortProtocol.Tcp, 4242)], t0.AddSeconds(2), _ => null);
        var closed = tracker.Feed([], t0.AddSeconds(4));
        Assert.Equal("PID 4242", Assert.Single(closed).ProcessName);
    }

    [Fact]
    public void CloseAll_EndsOpenSessions_AndClearsState()
    {
        var tracker = new PortSessionTracker();
        var t0 = new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);

        tracker.Feed([Binding(3000, PortProtocol.Tcp, 100), Binding(8080, PortProtocol.Tcp, 100)], t0, _ => "a.exe");
        tracker.Feed([Binding(3000, PortProtocol.Tcp, 100), Binding(8080, PortProtocol.Tcp, 100)], t0.AddSeconds(2), _ => "a.exe");

        var closed = tracker.CloseAll(t0.AddSeconds(100));
        Assert.Equal(2, closed.Count);
        Assert.All(closed, s => Assert.Equal(t0.AddSeconds(100), s.EndedAt));
        Assert.Empty(tracker.Feed([], t0.AddSeconds(102))); // 状态已清空
    }
}
