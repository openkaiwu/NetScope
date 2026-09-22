using NetScope.Core.Models;
using NetScope.Core.Services;
using NetScope.Windows.History;

namespace NetScope.Tests;

/// <summary>
/// V1.0 端到端验收（加速时间轴）：模拟 30 天历史 → 用户点“刚才卡了” → 打开事件 →
/// 归因链给出主相关进程、事件前后指标可回放、30 天内同类行为次数与标记重合次数、
/// 月报与 Insights 用同一份落库证据得出一致结论。真实 24 小时常驻验收另行执行。
/// </summary>
public sealed class V10EndToEndTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "netscope-v10e2e-" + Guid.NewGuid().ToString("N"));
    private SqliteHistoryStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _store = new SqliteHistoryStore(Path.Combine(_directory, "history.db"));
        await _store.InitializeAsync();
        _store.ConfigureRetention(30);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task MarkLagScenarioEndsInPrimaryProcessComparisonAndRecurrence()
    {
        var now = DateTimeOffset.Now;
        var hog = new ProcessInstanceKey(9001, now.AddHours(-2));
        var other = new ProcessInstanceKey(9002, now.AddHours(-8));

        // 30 天内 6 次 hog.exe 的 CPU 争用事件；3 次用户标记，其中一次与 -1 天的事件重合 45 秒
        foreach (var days in new[] { -1, -3, -7, -12, -20, -28 })
        {
            var at = now.AddDays(days);
            await _store.AppendEventAsync(new PerformanceEvent(
                Guid.NewGuid(), PerformanceEventType.CpuContention, PerformanceEventStatus.Closed,
                at, at.AddSeconds(40), 80, "可能存在 CPU 争用", "hog.exe CPU 显著高于基线",
                ["系统 CPU 峰值 94%", "hog.exe 平均 CPU 62%"], [],
                new ProcessInstanceKey(9001, at), "hog.exe",
                [new PerformanceEventContributor(new ProcessInstanceKey(9001, at), "hog.exe", 70)]));
        }
        await _store.AppendEventAsync(Mark(now.AddDays(-1).AddSeconds(45), hog, "hog.exe"));
        await _store.AppendEventAsync(Mark(now.AddDays(-10), other, "other.exe", includeContributor: false));
        await _store.AppendEventAsync(Mark(now.AddDays(-25), other, "other.exe", includeContributor: false));

        // 标记窗口内的系统与进程采样：归因链与回放曲线的数据来源
        var markAt = now.AddSeconds(-30);
        for (var i = 0; i <= 120; i++)
        {
            var at = markAt.AddSeconds(-90 + i);
            await _store.AppendSystemSampleAsync(new(at, 20 + i % 60, 8_000_000_000, 16_000_000_000, 1024, 2048));
            await _store.AppendProcessSampleAsync(new(hog, at, "hog.exe", 5 + i % 80,
                400 * 1024 * 1024, 400 * 1024 * 1024, 1024, 2048, 0, 0));
        }
        await _store.FlushNowAsync();

        // “刚才卡了”：与 CollectorHost.MarkLagAsync 相同的事件结构（前 60 秒窗口 + 贡献者排序）
        var lag = new PerformanceEvent(
            Guid.NewGuid(), PerformanceEventType.UserMarkedLag, PerformanceEventStatus.Confirmed,
            markAt.AddSeconds(-60), markAt, 100,
            "用户反馈的响应迟缓事件", "标记时刻的分析：前 60 秒系统 CPU 平均 47%",
            ["用户手动点击“刚才卡了”", "前 60 秒系统采样 61 条", "影响分最高：hog.exe（CPU 62%）"],
            ["查看下方事件前后的 CPU、内存与 I/O 曲线"],
            hog, "hog.exe",
            [new PerformanceEventContributor(hog, "hog.exe", 70), new PerformanceEventContributor(other, "other.exe", 5)]);
        await _store.AppendEventAsync(lag);
        await _store.FlushNowAsync();

        // 打开事件：重新从历史库读回，主相关进程仍在
        var events = await _store.QueryEventsAsync(now.AddDays(-30), now, 5000);
        var loaded = events.Single(e => e.Id == lag.Id);
        Assert.Equal("hog.exe", loaded.PrimaryProcessName);
        Assert.Equal(2, loaded.Contributors!.Count);

        // 归因链：五步齐全、首候选是 hog、原始窗口可回放
        var windowStart = loaded.StartedAt.AddSeconds(-30);
        var windowEnd = (loaded.EndedAt ?? loaded.StartedAt.AddSeconds(30)).AddSeconds(30);
        var systemWindow = await _store.QuerySystemAsync(windowStart, windowEnd);
        var chain = AttributionChainBuilder.Build(loaded, new(true, systemWindow.Count, 0, now.AddDays(-30)));
        Assert.True(chain.RawSamplesAvailable);
        Assert.Equal(5, chain.Steps.Count);
        var candidates = chain.Steps.Single(s => s.Stage == AttributionStage.Candidates);
        Assert.Contains("hog.exe", candidates.Items[0]);
        var hypothesis = chain.Steps.Single(s => s.Stage == AttributionStage.Hypothesis);
        Assert.Contains("hog.exe", hypothesis.Detail);
        Assert.Contains("疑似", hypothesis.Detail);

        // 前后指标对比：事件窗口的系统与进程曲线都能回放
        Assert.True(systemWindow.Count > 0);
        var processWindow = await _store.QueryProcessAsync(hog, windowStart, windowEnd);
        Assert.True(processWindow.Count > 0);
        Assert.All(processWindow, sample => Assert.Equal(hog, sample.Process));

        // 30 天内同类行为次数与标记重合次数（事件详情的复发统计口径）
        var summary = ProcessEventsQuery.Summarize(events, "hog.exe", 30, 10, now);
        Assert.Equal(8, summary.TotalCount);              // 6 次 CPU 争用 + 2 次以 hog 为主的标记
        Assert.Equal(1, summary.LagRelatedCount);        // 仅 -1 天的 CPU 事件与标记重合 45 秒
        Assert.Equal(30, summary.WindowDays);

        // 月报：同一份证据，最可能拖慢电脑的软件是 hog.exe
        var previous = await _store.QueryEventsAsync(now.AddDays(-60), now.AddDays(-30), 5000);
        var coverage = await _store.QueryCoverageAsync(now.AddDays(-30), now);
        var report = ReportGenerator.Generate(new ReportInput
        {
            Period = ReportPeriod.Month, Now = now,
            Events = events, PreviousPeriodEvents = previous,
            SystemSampleCount = coverage.SystemSampleCount,
            ProcessSampleCount = coverage.ProcessSampleCount,
            RetentionDays = 30
        });
        Assert.Contains("hog.exe", report.Headline);
        var offenders = report.Sections.Single(s => s.Title == "最可能拖慢电脑的软件");
        Assert.Equal("hog.exe", offenders.Metrics[0].Label);
        Assert.Contains("事件 6 次", offenders.Metrics[0].Value);
        Assert.Contains("重合 1 次", offenders.Metrics[0].Note);
        var overview = report.Sections.Single(s => s.Title == "概览");
        Assert.Equal("4 次", overview.Metrics[0].Value);  // 4 次用户标记
        Assert.Equal("6 次", overview.Metrics[1].Value);  // 6 次自动事件

        // Insights 卡顿统计与报告一致：同一份标记数据
        var insights = InsightGenerator.Generate(new InsightQuery(30, "", null, 100), now, events, [], [], []);
        var lagSummary = insights.Single(i => i.Kind == InsightKind.LagSummary);
        Assert.Contains("4 次", lagSummary.Title);
        Assert.Contains("hog.exe", lagSummary.Summary);
        Assert.Equal(4, lagSummary.SourceEventIds.Count);
    }

    private static PerformanceEvent Mark(DateTimeOffset at, ProcessInstanceKey primary, string name,
        bool includeContributor = true) => new(
        Guid.NewGuid(), PerformanceEventType.UserMarkedLag, PerformanceEventStatus.Confirmed,
        at.AddSeconds(-60), at, 100, "用户标记卡顿", $"{name} 影响分最高",
        ["用户手动标记"], [], primary, name,
        includeContributor ? [new PerformanceEventContributor(primary, name, 60)] : []);
}
