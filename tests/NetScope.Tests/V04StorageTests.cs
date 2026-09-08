using Microsoft.Data.Sqlite;
using NetScope.Core.Models;
using NetScope.Windows.History;

namespace NetScope.Tests;

public sealed class V04StorageTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "netscope-v04-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_directory, "history.db");
    public Task InitializeAsync() { Directory.CreateDirectory(_directory); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(_directory, true); return Task.CompletedTask; }

    [Fact]
    public async Task MigratesV03WithoutLosingSamplesAndPersistsNewMetrics()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var old = new SqliteConnection($"Data Source={Db};Pooling=False"))
        {
            await old.OpenAsync();
            await using var command = old.CreateCommand();
            command.CommandText = """
                CREATE TABLE SystemSamples (Id INTEGER PRIMARY KEY AUTOINCREMENT, Timestamp INTEGER NOT NULL, CpuPercent REAL NOT NULL,
                AvailableMemoryBytes INTEGER NOT NULL, TotalMemoryBytes INTEGER NOT NULL, NetworkReceivedBps INTEGER NOT NULL, NetworkSentBps INTEGER NOT NULL,
                NetworkLinkUp INTEGER NOT NULL DEFAULT 1, NetworkAdapterName TEXT NOT NULL DEFAULT '');
                INSERT INTO SystemSamples (Timestamp,CpuPercent,AvailableMemoryBytes,TotalMemoryBytes,NetworkReceivedBps,NetworkSentBps) VALUES (@ts,10,1000,2000,0,0);
                """;
            command.Parameters.AddWithValue("@ts", now.UtcTicks);
            await command.ExecuteNonQueryAsync();
        }
        await using var store = new SqliteHistoryStore(Db);
        await store.InitializeAsync();
        var previous = Assert.Single(await store.QuerySystemAsync(now.AddMinutes(-1), now.AddMinutes(1)));
        Assert.Null(previous.Disk);
        Assert.Null(previous.Responsiveness);
        await store.AppendSystemSampleAsync(previous with { Timestamp = now.AddSeconds(1), Disk = new(2, 3, 1, 40), Responsiveness = new(91, "较流畅", ["测试证据"]) });
        await store.FlushNowAsync();
        var rows = await store.QuerySystemAsync(now.AddMinutes(-1), now.AddMinutes(1));
        Assert.Equal(2, rows.Count);
        Assert.Equal(3, rows[1].Disk!.WriteLatencyMs);
        Assert.Equal(91, rows[1].Responsiveness!.Score);
    }

    [Fact]
    public async Task ConnectionUpsertSearchRestartAndRetention()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new TcpConnectionRecord(Guid.NewGuid(), 12, now.AddMinutes(-5), "测试.exe", IpAddressFamily.IPv6,
            "::1", 50000, "::1", 443, "Established", now.AddMinutes(-2), now);
        await using (var store = new SqliteHistoryStore(Db))
        {
            await store.InitializeAsync();
            await store.AppendConnectionAsync(record);
            await store.AppendConnectionAsync(record with { State = "CloseWait" });
            await store.AppendConnectionAsync(record with { Id = Guid.NewGuid(), FirstSeenAt = now.AddDays(-10), LastSeenAt = now.AddDays(-9) });
            await store.FlushNowAsync();
            var found = Assert.Single(await store.QueryConnectionsAsync(new(now.AddDays(-1), now.AddMinutes(1), "443")));
            Assert.Equal("CloseWait", found.State);
            Assert.Empty(await store.QueryConnectionsAsync(new(now.AddDays(-1), now.AddMinutes(1), "missing")));
            await store.CompactNowAsync();
            Assert.Single(await store.QueryConnectionsAsync(new(now.AddDays(-30), now.AddMinutes(1))));
        }
        await using var reopened = new SqliteHistoryStore(Db);
        await reopened.InitializeAsync();
        var interrupted = Assert.Single(await reopened.QueryConnectionsAsync(new(now.AddDays(-1), now.AddMinutes(1))));
        Assert.Equal(now, interrupted.EndedAt);
        Assert.Contains("重启", interrupted.EndReason);
    }
}
