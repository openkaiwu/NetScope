using NetScope.Core.Models;
using NetScope.Windows.History;

namespace NetScope.Tests;

/// <summary>V1.1 本地审计验收：干预事件落库往返、保留期清理。</summary>
public sealed class V11StorageTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "netscope-v11-" + Guid.NewGuid().ToString("N"));
    private SqliteHistoryStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _store = new SqliteHistoryStore(Path.Combine(_directory, "history.db"));
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task InterventionRoundTripsWithAllAuditFields()
    {
        var at = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var evt = new InterventionEvent(
            Guid.NewGuid(), at, new ProcessInstanceKey(9001, at.AddHours(-2)), "hog.exe",
            @"C:\Program Files\Hog\hog.exe",
            TerminationRiskLevel.Caution, TerminationKind.Terminate,
            TerminationOutcome.Exited, "目标已被强制结束（跳过应用清理）并确认退出。", 0);
        await _store.AppendInterventionAsync(evt);
        await _store.FlushNowAsync();

        var restored = Assert.Single(await _store.QueryInterventionsAsync(at.AddMinutes(-1), at.AddMinutes(1)));
        Assert.Equal(evt.Id, restored.Id);
        Assert.Equal("hog.exe", restored.ProcessName);
        Assert.Equal(evt.Process, restored.Process);
        Assert.Equal(@"C:\Program Files\Hog\hog.exe", restored.ImagePath);
        Assert.Equal(TerminationRiskLevel.Caution, restored.Assessment);
        Assert.Equal(TerminationKind.Terminate, restored.Requested);
        Assert.Equal(TerminationOutcome.Exited, restored.Outcome);
        Assert.Equal(evt.Message, restored.Message);
        Assert.Equal(0, restored.Win32Error);
    }

    [Fact]
    public async Task RepeatedAppendOfSameAuditIdIsIgnored()
    {
        var now = DateTimeOffset.Now;
        var evt = new InterventionEvent(Guid.NewGuid(), now, new ProcessInstanceKey(1, now), "app.exe",
            null, TerminationRiskLevel.UsuallyTerminable, TerminationKind.RequestClose,
            TerminationOutcome.CloseRequested, "仍在运行。", 0);
        await _store.AppendInterventionAsync(evt);
        await _store.FlushNowAsync();
        await _store.AppendInterventionAsync(evt);
        await _store.FlushNowAsync();

        Assert.Single(await _store.QueryInterventionsAsync(now.AddMinutes(-1), now.AddMinutes(1)));
    }

    [Fact]
    public async Task RetentionDeletesExpiredInterventions()
    {
        var old = DateTimeOffset.Now.AddDays(-10);
        var fresh = DateTimeOffset.Now.AddMinutes(-1);
        foreach (var at in new[] { old, fresh })
        {
            await _store.AppendInterventionAsync(new(Guid.NewGuid(), at, new ProcessInstanceKey(1, at), "app.exe",
                null, TerminationRiskLevel.UsuallyTerminable, TerminationKind.Terminate,
                TerminationOutcome.Exited, "done", 0));
        }
        await _store.FlushNowAsync();

        _store.ConfigureRetention(7);
        await _store.CompactNowAsync();

        var rows = await _store.QueryInterventionsAsync(old.AddHours(-1), fresh.AddHours(1));
        Assert.Single(rows);
        Assert.Equal(fresh, rows[0].At, TimeSpan.FromSeconds(1));
    }
}
