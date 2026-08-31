using NetScope.Core.Models;
using NetScope.Windows.History;

namespace NetScope.Tests;

public sealed class SqlitePortSessionTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netscope-portsession-test-" + Guid.NewGuid().ToString("N"));
    private SqliteHistoryStore _store = null!;

    private string DbPath => Path.Combine(_root, "netscope.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = new SqliteHistoryStore(DbPath);
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    private static PortSessionRecord Session(int port, string name, DateTimeOffset start, double seconds) =>
        new(port, PortProtocol.Tcp, name, 1000, start, start.AddSeconds(seconds));

    [Fact]
    public async Task AppendAndQuery_AggregatesByProcessName()
    {
        var t0 = new DateTimeOffset(2026, 8, 31, 9, 0, 0, TimeSpan.FromHours(8));
        await _store.AppendPortSessionAsync(Session(8080, "python.exe", t0, 3600));
        await _store.AppendPortSessionAsync(Session(8080, "python.exe", t0.AddHours(2), 1800));
        await _store.AppendPortSessionAsync(Session(8080, "java.exe", t0.AddHours(3), 600));
        await _store.FlushNowAsync();

        var usage = await _store.QueryPortUsageAsync(8080, PortProtocol.Tcp, t0.AddDays(-1), t0.AddDays(1));
        Assert.Equal(2, usage.Count);
        Assert.Equal("python.exe", usage[0].ProcessName); // 按累计时长降序
        Assert.Equal(2, usage[0].SessionCount);
        Assert.Equal(5400, usage[0].TotalSeconds, 0);
        Assert.Equal("java.exe", usage[1].ProcessName);
    }

    [Fact]
    public async Task Query_RespectsProtocolAndWindow()
    {
        var t0 = new DateTimeOffset(2026, 8, 31, 9, 0, 0, TimeSpan.FromHours(8));
        await _store.AppendPortSessionAsync(Session(53, "dns.exe", t0, 60));
        await _store.AppendPortSessionAsync(new PortSessionRecord(53, PortProtocol.Udp, "dns.exe", 1, t0, t0.AddSeconds(60)));
        await _store.FlushNowAsync();

        var tcpOnly = await _store.QueryPortUsageAsync(53, PortProtocol.Tcp, t0.AddDays(-1), t0.AddDays(1));
        var udpOnly = await _store.QueryPortUsageAsync(53, PortProtocol.Udp, t0.AddDays(-1), t0.AddDays(1));
        var outOfWindow = await _store.QueryPortUsageAsync(53, PortProtocol.Tcp, t0.AddDays(2), t0.AddDays(3));

        Assert.Single(tcpOnly);
        Assert.Single(udpOnly);
        Assert.Empty(outOfWindow);
    }

    [Fact]
    public async Task RetentionCleanup_DeletesExpiredSessions()
    {
        _store.ConfigureRetention(1);
        var old = DateTimeOffset.UtcNow.AddDays(-3);
        var fresh = DateTimeOffset.UtcNow.AddHours(-1);
        await _store.AppendPortSessionAsync(Session(8080, "old.exe", old, 60));
        await _store.AppendPortSessionAsync(Session(8080, "fresh.exe", fresh, 60));
        await _store.FlushNowAsync();
        await _store.CompactNowAsync();

        var usage = await _store.QueryPortUsageAsync(8080, PortProtocol.Tcp, old.AddDays(-1), DateTimeOffset.UtcNow);
        Assert.Single(usage);
        Assert.Equal("fresh.exe", usage[0].ProcessName);
    }

    [Fact]
    public async Task Query_WithoutUsableStore_ReturnsEmpty()
    {
        var broken = new SqliteHistoryStore(Path.Combine(_root, "never-initialized", "sub", "db.sqlite"));
        var usage = await broken.QueryPortUsageAsync(8080, PortProtocol.Tcp, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
        Assert.Empty(usage);
        await broken.DisposeAsync();
    }
}
