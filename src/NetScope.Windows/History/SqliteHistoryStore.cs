using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;
using NetScope.Windows.Logging;

namespace NetScope.Windows.History;

/// <summary>
/// SQLite 性能历史存储：
/// - WAL 模式 + 有界队列批量写入，绝不逐采样落盘；
/// - 写入失败或库损坏时保留损坏副本并自动重建，实时功能不受影响；
/// - 周期性降采样（超过 24 小时的采样压缩为 30 秒粒度）与保留期清理。
/// </summary>
public sealed class SqliteHistoryStore : IPerformanceHistoryStore
{
    private static readonly long TicksPer30Seconds = TimeSpan.FromSeconds(30).Ticks;
    private static readonly long TicksPerDay = TimeSpan.FromDays(1).Ticks;

    private readonly string _databasePath;
    private readonly RollingFileLogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<WriteWorkItem> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();

    private SqliteConnection? _connection;
    private Task? _flushLoop;
    private DateTimeOffset _lastCompaction = DateTimeOffset.MinValue;
    private volatile bool _initialized;
    private int _retentionDays = 7;
    private int _disposed;

    public bool IsUsable => _initialized && _connection is not null;

    /// <param name="databasePath">数据库文件路径；默认 %LocalAppData%\NetScope\data\netscope.db。</param>
    /// <param name="logger">脱敏滚动日志，可为 null（测试场景）。</param>
    public SqliteHistoryStore(string? databasePath = null, RollingFileLogger? logger = null)
    {
        _databasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetScope", "data", "netscope.db");
        _logger = logger;
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                await OpenOrCreateAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // 启动时文件已损坏：走恢复路径（备份 + 重建），不让实时功能挂掉
                await LogAsync("WARN", $"打开历史库失败，尝试恢复: {ex.Message}");
                await RecoverCoreAsync(cancellationToken);
            }
            _flushLoop ??= Task.Run(() => FlushLoopAsync(_lifetime.Token));
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ConfigureRetention(int retentionDays) => _retentionDays = Math.Clamp(retentionDays, 1, 30);

    /// <summary>立即把待写队列落盘。常规路径由后台 2 秒批量循环负责，测试与退出前需要立即可见。</summary>
    public Task FlushNowAsync(CancellationToken cancellationToken = default) => FlushAsync(cancellationToken);

    /// <summary>立即执行保留期清理与降采样。常规路径每 5 分钟一次，测试需要立即生效。</summary>
    public Task CompactNowAsync(CancellationToken cancellationToken = default) => CompactAsync(cancellationToken);

    public ValueTask AppendSystemSampleAsync(SystemPerformanceSample sample, CancellationToken cancellationToken = default)
    {
        Enqueue(new WriteWorkItem(System: sample));
        return ValueTask.CompletedTask;
    }

    public ValueTask AppendProcessSampleAsync(ProcessPerformanceSample sample, CancellationToken cancellationToken = default)
    {
        Enqueue(new WriteWorkItem(Process: sample));
        return ValueTask.CompletedTask;
    }

    public ValueTask AppendEventAsync(PerformanceEvent evt, CancellationToken cancellationToken = default)
    {
        Enqueue(new WriteWorkItem(Event: evt));
        return ValueTask.CompletedTask;
    }

    public ValueTask AppendCollectorHealthAsync(CollectorHealthSnapshot sample, CancellationToken cancellationToken = default)
    {
        Enqueue(new WriteWorkItem(Health: sample));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IReadOnlyList<CollectorHealthSnapshot>> QueryCollectorHealthAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        if (!IsUsable) return [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT Json FROM CollectorHealth WHERE Timestamp >= @from AND Timestamp <= @to ORDER BY Timestamp";
            command.Parameters.AddWithValue("@from", from.UtcTicks);
            command.Parameters.AddWithValue("@to", to.UtcTicks);
            var result = new List<CollectorHealthSnapshot>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (JsonSerializer.Deserialize<CollectorHealthSnapshot>(reader.GetString(0)) is { } item) result.Add(item);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<IReadOnlyList<ProcessHistoryPoint>> QueryProcessSeriesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        if (!IsUsable || to <= from) return [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // At most about 720 buckets per process; GROUP BY lower(Name) intentionally aggregates multiple instances of one application.
            var bucket = Math.Max(TimeSpan.FromSeconds(30).Ticks, (to - from).Ticks / 720);
            await using var command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT Name, ProcessId, StartedAt, (Timestamp / @bucket) * @bucket AS Bucket,
                       AVG(CpuPercent), AVG(PrivateBytes), AVG(ReadBps + WriteBps), COUNT(*)
                FROM ProcessSamples WHERE Timestamp >= @from AND Timestamp <= @to
                GROUP BY ProcessId, StartedAt, lower(Name), Bucket
                ORDER BY lower(Name), StartedAt, Bucket LIMIT 50000
                """;
            command.Parameters.AddWithValue("@bucket", bucket);
            command.Parameters.AddWithValue("@from", from.UtcTicks);
            command.Parameters.AddWithValue("@to", to.UtcTicks);
            var result = new List<ProcessHistoryPoint>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new(reader.GetString(0), new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero).ToLocalTime(),
                    reader.GetDouble(4), (long)reader.GetDouble(5), (long)reader.GetDouble(6), (int)reader.GetInt64(7),
                    (int)reader.GetInt64(1), new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero).ToLocalTime()));
            return result;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<IReadOnlyList<PortActivitySummary>> QueryPortActivityAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        if (!IsUsable) return [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT Port, Protocol, ProcessName, COUNT(*), SUM(MIN(EndedAt, @to) - MAX(StartedAt, @from)), MAX(EndedAt)
                FROM PortSessions WHERE StartedAt <= @to AND EndedAt >= @from
                GROUP BY Port, Protocol, ProcessName ORDER BY COUNT(*) DESC LIMIT 1000
                """;
            command.Parameters.AddWithValue("@from", from.UtcTicks);
            command.Parameters.AddWithValue("@to", to.UtcTicks);
            var result = new List<PortActivitySummary>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new((int)reader.GetInt64(0), (PortProtocol)reader.GetInt64(1), reader.GetString(2),
                    (int)reader.GetInt64(3), reader.GetInt64(4) / (double)TimeSpan.TicksPerSecond,
                    new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero).ToLocalTime()));
            return result;
        }
        finally { _gate.Release(); }
    }

    public ValueTask AppendConnectionAsync(TcpConnectionRecord connection, CancellationToken cancellationToken = default)
    {
        Enqueue(new WriteWorkItem(Connection: connection));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IReadOnlyList<TcpConnectionRecord>> QueryConnectionsAsync(ConnectionQuery query, CancellationToken cancellationToken = default)
    {
        if (!IsUsable) return [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT Json FROM TcpConnections WHERE FirstSeen <= @to AND LastSeen >= @from AND (@search = '' OR instr(lower(SearchText), lower(@search)) > 0) ORDER BY LastSeen DESC LIMIT @limit";
            command.Parameters.AddWithValue("@from", query.From.UtcTicks);
            command.Parameters.AddWithValue("@to", query.To.UtcTicks);
            command.Parameters.AddWithValue("@search", query.Search ?? "");
            command.Parameters.AddWithValue("@limit", Math.Clamp(query.Limit, 1, 500));
            var result = new List<TcpConnectionRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (JsonSerializer.Deserialize<TcpConnectionRecord>(reader.GetString(0)) is { } record) result.Add(record);
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task WriteConnectionAsync(TcpConnectionRecord record, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = _connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO TcpConnections (Id, FirstSeen, LastSeen, SearchText, Json) VALUES (@id, @first, @last, @search, @json) ON CONFLICT(Id) DO UPDATE SET LastSeen=@last, SearchText=@search, Json=@json";
        command.Parameters.AddWithValue("@id", record.Id.ToString());
        command.Parameters.AddWithValue("@first", record.FirstSeenAt.UtcTicks);
        command.Parameters.AddWithValue("@last", record.LastSeenAt.UtcTicks);
        command.Parameters.AddWithValue("@search", $"{record.ProcessName} {record.ProcessId} {record.LocalAddress} {record.LocalPort} {record.RemoteAddress} {record.RemotePort}");
        command.Parameters.AddWithValue("@json", JsonSerializer.Serialize(record));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public ValueTask AppendPortSessionAsync(PortSessionRecord session, CancellationToken cancellationToken = default)
    {
        Enqueue(new WriteWorkItem(PortSession: session));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<IReadOnlyList<SystemPerformanceSample>> QuerySystemAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        if (!IsUsable) return [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT Timestamp, CpuPercent, AvailableMemoryBytes, TotalMemoryBytes, NetworkReceivedBps, NetworkSentBps, NetworkLinkUp, NetworkAdapterName, AnalysisJson
                FROM SystemSamples WHERE Timestamp >= @from AND Timestamp <= @to ORDER BY Timestamp
                """;
            command.Parameters.AddWithValue("@from", from.UtcTicks);
            command.Parameters.AddWithValue("@to", to.UtcTicks);

            var result = new List<SystemPerformanceSample>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new SystemPerformanceSample(
                    new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero).ToLocalTime(),
                    reader.GetDouble(1), reader.GetInt64(2), reader.GetInt64(3),
                    reader.GetInt64(4), reader.GetInt64(5),
                    reader.GetInt64(6) != 0, reader.GetString(7),
                    ReadAnalysis(reader, 8)?.Disk, ReadAnalysis(reader, 8)?.Responsiveness));
            }
            return result;
        }
        catch (Exception ex)
        {
            await LogAsync("WARN", $"查询系统历史失败: {ex.Message}");
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ProcessPerformanceSample>> QueryProcessAsync(ProcessInstanceKey process, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        if (!IsUsable) return [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT Timestamp, Name, CpuPercent, WorkingSetBytes, PrivateBytes, ReadBps, WriteBps, ReadOps, WriteOps, IsForeground
                FROM ProcessSamples WHERE ProcessId = @pid AND StartedAt = @started AND Timestamp >= @from AND Timestamp <= @to ORDER BY Timestamp
                """;
            command.Parameters.AddWithValue("@pid", process.ProcessId);
            command.Parameters.AddWithValue("@started", process.StartedAt.UtcTicks);
            command.Parameters.AddWithValue("@from", from.UtcTicks);
            command.Parameters.AddWithValue("@to", to.UtcTicks);

            var result = new List<ProcessPerformanceSample>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new ProcessPerformanceSample(
                    process, new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero).ToLocalTime(), reader.GetString(1),
                    reader.GetDouble(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
                    reader.GetInt64(7), reader.GetInt64(8), true, null, reader.GetInt64(9) != 0));
            }
            return result;
        }
        catch (Exception ex)
        {
            await LogAsync("WARN", $"查询进程历史失败: {ex.Message}");
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<PerformanceEvent>> QueryEventsAsync(DateTimeOffset from, DateTimeOffset to, int limit = 200, CancellationToken cancellationToken = default)
    {
        if (!IsUsable) return [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT Id, Type, Status, StartedAt, EndedAt, Confidence, Summary, MostLikelyCause, Evidence, Recommendations, PrimaryProcessId, PrimaryStartedAt, PrimaryProcessName
                FROM PerformanceEvents WHERE StartedAt >= @from AND StartedAt <= @to ORDER BY StartedAt DESC LIMIT @limit
                """;
            command.Parameters.AddWithValue("@from", from.UtcTicks);
            command.Parameters.AddWithValue("@to", to.UtcTicks);
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));

            var events = new List<PerformanceEvent>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = Guid.Parse(reader.GetString(0));
                var contributors = await LoadContributorsAsync(id, cancellationToken);
                events.Add(new PerformanceEvent(
                    id,
                    (PerformanceEventType)reader.GetInt64(1),
                    (PerformanceEventStatus)reader.GetInt64(2),
                    new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero).ToLocalTime(),
                    reader.IsDBNull(4) ? null : new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero).ToLocalTime(),
                    (int)reader.GetInt64(5), reader.GetString(6), reader.GetString(7),
                    DeserializeList(reader.GetString(8)), DeserializeList(reader.GetString(9)),
                    reader.IsDBNull(10) ? null : new ProcessInstanceKey((int)reader.GetInt64(10), new DateTimeOffset(reader.GetInt64(11), TimeSpan.Zero).ToLocalTime()),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    contributors));
            }
            return events;
        }
        catch (Exception ex)
        {
            await LogAsync("WARN", $"查询事件历史失败: {ex.Message}");
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>查询某端口在时间窗内的占用聚合（按端口+协议+进程名分组，按累计时长降序）。</summary>
    public async ValueTask<IReadOnlyList<PortUsageSummary>> QueryPortUsageAsync(int port, PortProtocol protocol, DateTimeOffset from, DateTimeOffset to, int limit = 20, CancellationToken cancellationToken = default)
    {
        if (!IsUsable) return [];
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT ProcessName, COUNT(*) AS Sessions, SUM(EndedAt - StartedAt) AS Ticks, MAX(EndedAt) AS LastSeen
                FROM PortSessions
                WHERE Port = @port AND Protocol = @protocol AND StartedAt <= @to AND EndedAt >= @from
                GROUP BY Port, Protocol, ProcessName
                ORDER BY Ticks DESC
                LIMIT @limit
                """;
            command.Parameters.AddWithValue("@port", port);
            command.Parameters.AddWithValue("@protocol", (int)protocol);
            command.Parameters.AddWithValue("@from", from.UtcTicks);
            command.Parameters.AddWithValue("@to", to.UtcTicks);
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 100));

            var result = new List<PortUsageSummary>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new PortUsageSummary(
                    port, protocol, reader.GetString(0),
                    (int)reader.GetInt64(1),
                    reader.GetInt64(2) / 10_000_000.0,
                    new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero).ToLocalTime()));
            }
            return result;
        }
        catch (Exception ex)
        {
            await LogAsync("WARN", $"查询端口占用历史失败: {ex.Message}");
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<PerformanceEventContributor>> LoadContributorsAsync(Guid eventId, CancellationToken cancellationToken)
    {
        await using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT ProcessId, StartedAt, ProcessName, ImpactScore FROM EventContributors WHERE EventId = @id ORDER BY ImpactScore DESC";
        command.Parameters.AddWithValue("@id", eventId.ToString());
        var contributors = new List<PerformanceEventContributor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            contributors.Add(new PerformanceEventContributor(
                new ProcessInstanceKey((int)reader.GetInt64(0), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero).ToLocalTime()),
                reader.GetString(2), reader.GetDouble(3)));
        return contributors;
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await timer.WaitForNextTickAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                await FlushAsync(cancellationToken);
                if (DateTimeOffset.UtcNow - _lastCompaction > TimeSpan.FromMinutes(5))
                {
                    await CompactAsync(cancellationToken);
                    _lastCompaction = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                await LogAsync("ERROR", $"历史写入失败，尝试恢复: {ex.Message}");
                await TryRecoverAsync(cancellationToken);
            }
        }
    }

    /// <summary>把队列中的全部待写项合并进单个事务。</summary>
    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!IsUsable || _pending.IsEmpty) return;

        List<WriteWorkItem> batch = [];
        while (_pending.TryDequeue(out var item))
        {
            batch.Add(item);
            if (batch.Count >= 5000) break;
        }
        if (batch.Count == 0) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = (SqliteTransaction)await _connection!.BeginTransactionAsync(cancellationToken);
            foreach (var item in batch)
            {
                if (item.System is { } system) await WriteSystemAsync(system, transaction, cancellationToken);
                else if (item.Process is { } process) await WriteProcessAsync(process, transaction, cancellationToken);
                else if (item.Event is { } evt) await WriteEventAsync(evt, transaction, cancellationToken);
                else if (item.Health is { } health) await WriteHealthAsync(health, transaction, cancellationToken);
                else if (item.Connection is { } connection) await WriteConnectionAsync(connection, transaction, cancellationToken);
                else if (item.PortSession is { } session) await WritePortSessionAsync(session, transaction, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteHealthAsync(CollectorHealthSnapshot sample, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = _connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO CollectorHealth (Timestamp, Mode, Json) VALUES (@ts, @mode, @json)";
        command.Parameters.AddWithValue("@ts", sample.Timestamp.UtcTicks);
        command.Parameters.AddWithValue("@mode", (int)sample.Profile.Mode);
        command.Parameters.AddWithValue("@json", JsonSerializer.Serialize(sample));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task WritePortSessionAsync(PortSessionRecord session, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = _connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO PortSessions (Port, Protocol, ProcessName, ProcessId, StartedAt, EndedAt)
            VALUES (@port, @protocol, @name, @pid, @started, @ended)
            """;
        command.Parameters.AddWithValue("@port", session.Port);
        command.Parameters.AddWithValue("@protocol", (int)session.Protocol);
        command.Parameters.AddWithValue("@name", session.ProcessName);
        command.Parameters.AddWithValue("@pid", session.ProcessId);
        command.Parameters.AddWithValue("@started", session.StartedAt.UtcTicks);
        command.Parameters.AddWithValue("@ended", session.EndedAt.UtcTicks);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task WriteSystemAsync(SystemPerformanceSample sample, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = _connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO SystemSamples (Timestamp, CpuPercent, AvailableMemoryBytes, TotalMemoryBytes, NetworkReceivedBps, NetworkSentBps, NetworkLinkUp, NetworkAdapterName, AnalysisJson)
            VALUES (@ts, @cpu, @avail, @total, @rx, @tx, @link, @adapter, @analysis)
            """;
        command.Parameters.AddWithValue("@ts", sample.Timestamp.UtcTicks);
        command.Parameters.AddWithValue("@cpu", sample.CpuPercent);
        command.Parameters.AddWithValue("@avail", sample.AvailableMemoryBytes);
        command.Parameters.AddWithValue("@total", sample.TotalMemoryBytes);
        command.Parameters.AddWithValue("@rx", sample.NetworkReceivedBytesPerSecond);
        command.Parameters.AddWithValue("@tx", sample.NetworkSentBytesPerSecond);
        command.Parameters.AddWithValue("@link", sample.NetworkLinkUp ? 1 : 0);
        command.Parameters.AddWithValue("@adapter", sample.NetworkAdapterName ?? string.Empty);
        command.Parameters.AddWithValue("@analysis", JsonSerializer.Serialize(new AnalysisData(sample.Disk, sample.Responsiveness)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record AnalysisData(DiskPerformanceSample? Disk, ResponsivenessAssessment? Responsiveness);
    private static AnalysisData? ReadAnalysis(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : JsonSerializer.Deserialize<AnalysisData>(reader.GetString(index));

    private async Task WriteProcessAsync(ProcessPerformanceSample sample, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var instance = _connection!.CreateCommand();
        instance.Transaction = transaction;
        instance.CommandText = """
            INSERT INTO ProcessInstances (ProcessId, StartedAt, Name, FirstSeen, LastSeen) VALUES (@pid, @started, @name, @ts, @ts)
            ON CONFLICT(ProcessId, StartedAt) DO UPDATE SET LastSeen = @ts
            """;
        instance.Parameters.AddWithValue("@pid", sample.Process.ProcessId);
        instance.Parameters.AddWithValue("@started", sample.Process.StartedAt.UtcTicks);
        instance.Parameters.AddWithValue("@name", sample.Name);
        instance.Parameters.AddWithValue("@ts", sample.Timestamp.UtcTicks);
        await instance.ExecuteNonQueryAsync(cancellationToken);

        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ProcessSamples (ProcessId, StartedAt, Timestamp, Name, CpuPercent, WorkingSetBytes, PrivateBytes, ReadBps, WriteBps, ReadOps, WriteOps, IsForeground)
            VALUES (@pid, @started, @ts, @name, @cpu, @ws, @private, @rbps, @wbps, @rops, @wops, @fg)
            """;
        command.Parameters.AddWithValue("@pid", sample.Process.ProcessId);
        command.Parameters.AddWithValue("@started", sample.Process.StartedAt.UtcTicks);
        command.Parameters.AddWithValue("@ts", sample.Timestamp.UtcTicks);
        command.Parameters.AddWithValue("@name", sample.Name);
        command.Parameters.AddWithValue("@cpu", sample.CpuPercent);
        command.Parameters.AddWithValue("@ws", sample.WorkingSetBytes);
        command.Parameters.AddWithValue("@private", sample.PrivateBytes);
        command.Parameters.AddWithValue("@rbps", sample.ReadBytesPerSecond);
        command.Parameters.AddWithValue("@wbps", sample.WriteBytesPerSecond);
        command.Parameters.AddWithValue("@rops", sample.ReadOperationsPerSecond);
        command.Parameters.AddWithValue("@wops", sample.WriteOperationsPerSecond);
        command.Parameters.AddWithValue("@fg", sample.IsForeground ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task WriteEventAsync(PerformanceEvent evt, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = _connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO PerformanceEvents (Id, Type, Status, StartedAt, EndedAt, Confidence, Summary, MostLikelyCause, Evidence, Recommendations, PrimaryProcessId, PrimaryStartedAt, PrimaryProcessName)
            VALUES (@id, @type, @status, @started, @ended, @conf, @summary, @cause, @evidence, @recs, @ppid, @pstarted, @pname)
            ON CONFLICT(Id) DO UPDATE SET Status=@status, EndedAt=@ended, Confidence=@conf, Summary=@summary, MostLikelyCause=@cause, Evidence=@evidence, Recommendations=@recs, PrimaryProcessId=@ppid, PrimaryStartedAt=@pstarted, PrimaryProcessName=@pname
            """;
        command.Parameters.AddWithValue("@id", evt.Id.ToString());
        command.Parameters.AddWithValue("@type", (int)evt.Type);
        command.Parameters.AddWithValue("@status", (int)evt.Status);
        command.Parameters.AddWithValue("@started", evt.StartedAt.UtcTicks);
        command.Parameters.AddWithValue("@ended", (object?)evt.EndedAt?.UtcTicks ?? DBNull.Value);
        command.Parameters.AddWithValue("@conf", evt.Confidence);
        command.Parameters.AddWithValue("@summary", evt.Summary);
        command.Parameters.AddWithValue("@cause", evt.MostLikelyCause);
        command.Parameters.AddWithValue("@evidence", JsonSerializer.Serialize(evt.Evidence));
        command.Parameters.AddWithValue("@recs", JsonSerializer.Serialize(evt.Recommendations));
        command.Parameters.AddWithValue("@ppid", (object?)evt.PrimaryProcess?.ProcessId ?? DBNull.Value);
        command.Parameters.AddWithValue("@pstarted", (object?)evt.PrimaryProcess?.StartedAt.UtcTicks ?? DBNull.Value);
        command.Parameters.AddWithValue("@pname", (object?)evt.PrimaryProcessName ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (evt.Contributors is { Count: > 0 })
        {
            await using var clear = _connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM EventContributors WHERE EventId = @id";
            clear.Parameters.AddWithValue("@id", evt.Id.ToString());
            await clear.ExecuteNonQueryAsync(cancellationToken);

            foreach (var contributor in evt.Contributors)
            {
                await using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT OR REPLACE INTO EventContributors (EventId, ProcessId, StartedAt, ProcessName, ImpactScore) VALUES (@id, @pid, @started, @name, @score)";
                insert.Parameters.AddWithValue("@id", evt.Id.ToString());
                insert.Parameters.AddWithValue("@pid", contributor.Process.ProcessId);
                insert.Parameters.AddWithValue("@started", contributor.Process.StartedAt.UtcTicks);
                insert.Parameters.AddWithValue("@name", contributor.ProcessName);
                insert.Parameters.AddWithValue("@score", contributor.ImpactScore);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    /// <summary>保留期清理与降采样：超过 24 小时的采样压缩为 30 秒粒度，超期数据删除。</summary>
    private async Task CompactAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            await using var command = _connection!.CreateCommand();
            command.CommandText = """
                DELETE FROM SystemSamples WHERE Timestamp < @cutoff;
                DELETE FROM ProcessSamples WHERE Timestamp < @cutoff;
                DELETE FROM PerformanceEvents WHERE StartedAt < @cutoff;
                DELETE FROM EventContributors WHERE EventId NOT IN (SELECT Id FROM PerformanceEvents);
                DELETE FROM ProcessInstances WHERE LastSeen < @cutoff;
                DELETE FROM PortSessions WHERE EndedAt < @cutoff;
                DELETE FROM TcpConnections WHERE LastSeen < @cutoff;
                DELETE FROM CollectorHealth WHERE Timestamp < @cutoff;
                DELETE FROM SystemSamples WHERE Timestamp < @day AND Id NOT IN (SELECT MIN(Id) FROM SystemSamples WHERE Timestamp < @day GROUP BY Timestamp / @bucket);
                DELETE FROM ProcessSamples WHERE Timestamp < @day AND Id NOT IN (SELECT MIN(Id) FROM ProcessSamples WHERE Timestamp < @day GROUP BY ProcessId, StartedAt, Timestamp / @bucket);
                """;
            command.Parameters.AddWithValue("@cutoff", now.AddDays(-_retentionDays).UtcTicks);
            command.Parameters.AddWithValue("@day", now.AddDays(-1).UtcTicks);
            command.Parameters.AddWithValue("@bucket", TicksPer30Seconds);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            await LogAsync("WARN", $"历史压缩失败: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OpenOrCreateAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await TryOpenAsync(cancellationToken);
        _initialized = true;
    }

    private async Task TryOpenAsync(CancellationToken cancellationToken)
    {
        _connection?.Dispose();
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        await _connection.OpenAsync(cancellationToken);

        await using var pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);

        await using var schema = _connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS SystemSamples (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp INTEGER NOT NULL,
                CpuPercent REAL NOT NULL,
                AvailableMemoryBytes INTEGER NOT NULL,
                TotalMemoryBytes INTEGER NOT NULL,
                NetworkReceivedBps INTEGER NOT NULL,
                NetworkSentBps INTEGER NOT NULL,
                NetworkLinkUp INTEGER NOT NULL DEFAULT 1,
                NetworkAdapterName TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS IX_SystemSamples_Timestamp ON SystemSamples(Timestamp);

            CREATE TABLE IF NOT EXISTS ProcessInstances (
                ProcessId INTEGER NOT NULL,
                StartedAt INTEGER NOT NULL,
                Name TEXT NOT NULL,
                FirstSeen INTEGER NOT NULL,
                LastSeen INTEGER NOT NULL,
                PRIMARY KEY (ProcessId, StartedAt)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS ProcessSamples (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProcessId INTEGER NOT NULL,
                StartedAt INTEGER NOT NULL,
                Timestamp INTEGER NOT NULL,
                Name TEXT NOT NULL,
                CpuPercent REAL NOT NULL,
                WorkingSetBytes INTEGER NOT NULL,
                PrivateBytes INTEGER NOT NULL,
                ReadBps INTEGER NOT NULL,
                WriteBps INTEGER NOT NULL,
                ReadOps INTEGER NOT NULL,
                WriteOps INTEGER NOT NULL,
                IsForeground INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS IX_ProcessSamples_Identity_Time ON ProcessSamples(ProcessId, StartedAt, Timestamp);

            CREATE TABLE IF NOT EXISTS PerformanceEvents (
                Id TEXT PRIMARY KEY,
                Type INTEGER NOT NULL,
                Status INTEGER NOT NULL,
                StartedAt INTEGER NOT NULL,
                EndedAt INTEGER,
                Confidence INTEGER NOT NULL,
                Summary TEXT NOT NULL,
                MostLikelyCause TEXT NOT NULL,
                Evidence TEXT NOT NULL,
                Recommendations TEXT NOT NULL,
                PrimaryProcessId INTEGER,
                PrimaryStartedAt INTEGER,
                PrimaryProcessName TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_PerformanceEvents_StartedAt ON PerformanceEvents(StartedAt);

            CREATE TABLE IF NOT EXISTS EventContributors (
                EventId TEXT NOT NULL,
                ProcessId INTEGER NOT NULL,
                StartedAt INTEGER NOT NULL,
                ProcessName TEXT NOT NULL,
                ImpactScore REAL NOT NULL,
                PRIMARY KEY (EventId, ProcessId, StartedAt)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS TcpConnections (Id TEXT PRIMARY KEY, FirstSeen INTEGER NOT NULL, LastSeen INTEGER NOT NULL, SearchText TEXT NOT NULL, Json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_TcpConnections_LastSeen ON TcpConnections(LastSeen);
            CREATE TABLE IF NOT EXISTS CollectorHealth (Id INTEGER PRIMARY KEY AUTOINCREMENT, Timestamp INTEGER NOT NULL, Mode INTEGER NOT NULL, Json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_CollectorHealth_Timestamp ON CollectorHealth(Timestamp);
            CREATE TABLE IF NOT EXISTS PortSessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Port INTEGER NOT NULL,
                Protocol INTEGER NOT NULL,
                ProcessName TEXT NOT NULL,
                ProcessId INTEGER NOT NULL,
                StartedAt INTEGER NOT NULL,
                EndedAt INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_PortSessions_Port ON PortSessions(Port, Protocol, StartedAt);
            CREATE INDEX IF NOT EXISTS IX_PortSessions_EndedAt ON PortSessions(EndedAt);
            """;
        await schema.ExecuteNonQueryAsync(cancellationToken);
        // Additive migration: preserve all V0.3 samples and leave unknown new metrics null.
        await using var columns = _connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(SystemSamples)";
        var hasAnalysis = false;
        await using (var reader = await columns.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) hasAnalysis |= reader.GetString(1) == "AnalysisJson";
        if (!hasAnalysis)
        {
            await using var migrate = _connection.CreateCommand();
            migrate.CommandText = "ALTER TABLE SystemSamples ADD COLUMN AnalysisJson TEXT";
            await migrate.ExecuteNonQueryAsync(cancellationToken);
        }
        // A previous collector may have stopped without a final snapshot. Never present these as live.
        await using var interrupted = _connection.CreateCommand();
        interrupted.CommandText = "UPDATE TcpConnections SET Json = json_set(Json, '$.EndedAt', json_extract(Json, '$.LastSeenAt'), '$.EndReason', '采集器重启，后续状态未知') WHERE json_extract(Json, '$.EndedAt') IS NULL";
        await interrupted.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>数据库损坏或写入失败时的恢复：保留损坏副本，重建空库。实时采样不受影响。</summary>
    private async Task TryRecoverAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await RecoverCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>恢复核心逻辑。调用方必须已持有 _gate（SemaphoreSlim 不可重入）。</summary>
    private async Task RecoverCoreAsync(CancellationToken cancellationToken)
    {
        _initialized = false;
        try
        {
            await using var check = _connection?.CreateCommand();
            if (check is not null)
            {
                check.CommandText = "PRAGMA quick_check;";
                var result = await check.ExecuteScalarAsync(cancellationToken);
                if (result is "ok") return; // 写入失败但库完好，无需重建
            }
        }
        catch
        {
            // 快速检查失败视为损坏
        }

        _connection?.Dispose();
        _connection = null;
        var backup = $"{_databasePath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
        try
        {
            if (File.Exists(_databasePath)) File.Copy(_databasePath, backup, overwrite: true);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(_databasePath + suffix)) File.Delete(_databasePath + suffix);
            }
            File.Delete(_databasePath);
            await LogAsync("WARN", $"数据库损坏，已备份为 {Path.GetFileName(backup)} 并重建");
        }
        catch (Exception ex)
        {
            await LogAsync("ERROR", $"备份数据库失败: {ex.Message}");
        }

        await TryOpenAsync(cancellationToken);
        _initialized = true;
    }

    private void Enqueue(WriteWorkItem item)
    {
        // 有界队列：积压超过上限时丢弃最旧项，优先保证 Collector 不因写盘阻塞
        _pending.Enqueue(item);
        while (_pending.Count > 20_000 && _pending.TryDequeue(out _)) { }
    }

    private async Task LogAsync(string level, string message)
    {
        if (_logger is not null) await _logger.WriteAsync(level, message);
    }

    private static IReadOnlyList<string> DeserializeList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 可重入：测试与宿主都可能调用一次以上
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _lifetime.Cancel();
        if (_flushLoop is not null)
        {
            try { await _flushLoop; }
            catch (Exception) { }
        }
        // 退出前尽力落盘剩余队列
        try { await FlushAsync(CancellationToken.None); }
        catch (Exception) { }
        await _gate.WaitAsync();
        try { _connection?.Dispose(); _connection = null; _initialized = false; }
        finally { _gate.Release(); }
        _gate.Dispose();
        _lifetime.Dispose();
    }

    private readonly record struct WriteWorkItem(
        SystemPerformanceSample? System = null,
        ProcessPerformanceSample? Process = null,
        PerformanceEvent? Event = null,
        PortSessionRecord? PortSession = null,
        TcpConnectionRecord? Connection = null,
        CollectorHealthSnapshot? Health = null);
}
