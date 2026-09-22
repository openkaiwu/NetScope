using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.Tests;

/// <summary>V1.0 周/月报告验收：环比、诚实降级、最可能拖慢电脑排序、长期行为与数据完整性说明。</summary>
public sealed class V10ReportGeneratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly ProcessInstanceKey Hog = new(9001, Now.AddDays(-5));

    private static PerformanceEvent Cpu(DateTimeOffset at, double score = 70, string name = "hog.exe") => new(
        Guid.NewGuid(), PerformanceEventType.CpuContention, PerformanceEventStatus.Closed,
        at, at.AddSeconds(40), 80, "CPU 争用", $"{name} CPU 高",
        [$"{name} CPU {score:0}"], [], new ProcessInstanceKey(9001, at), name,
        [new PerformanceEventContributor(Hog, name, score)]);

    private static PerformanceEvent Mark(DateTimeOffset at) => new(
        Guid.NewGuid(), PerformanceEventType.UserMarkedLag, PerformanceEventStatus.Confirmed,
        at, at.AddSeconds(30), 100, "用户标记卡顿", "分析",
        ["用户标记"], [], new ProcessInstanceKey(9001, at), "hog.exe",
        [new PerformanceEventContributor(new ProcessInstanceKey(9001, at), "hog.exe", 60)]);

    [Fact]
    public void WeekReportComparesAgainstPreviousPeriod()
    {
        var current = new List<PerformanceEvent>
        {
            Cpu(Now.AddDays(-1)), Cpu(Now.AddDays(-2)), Cpu(Now.AddDays(-4)), Cpu(Now.AddDays(-6)),
            Mark(Now.AddDays(-1).AddSeconds(30)), Mark(Now.AddDays(-3)), Mark(Now.AddDays(-5))
        };
        var previous = new List<PerformanceEvent> { Cpu(Now.AddDays(-9)), Mark(Now.AddDays(-10)) };

        var report = ReportGenerator.Generate(new ReportInput
        {
            Period = ReportPeriod.Week, Now = Now,
            Events = current, PreviousPeriodEvents = previous,
            SystemSampleCount = 20_000, ProcessSampleCount = 5_000
        });

        Assert.Equal(ReportPeriod.Week, report.Period);
        Assert.Equal(Now.AddDays(-7), report.From);
        var overview = report.Sections.Single(s => s.Title == "概览");
        Assert.Equal("3 次", overview.Metrics[0].Value);                       // 标记卡顿
        Assert.Equal("较上期 +2 次", overview.Metrics[0].Delta);
        Assert.Equal("4 次", overview.Metrics[1].Value);                      // 自动事件
        Assert.Equal("较上期 +3 次", overview.Metrics[1].Delta);
        Assert.Contains("hog.exe", report.Headline);
        Assert.Contains("# NetScope 周报", report.Markdown);
        Assert.Contains("最可能拖慢电脑的软件", report.Markdown);
        Assert.Contains("较上期 +2 次", report.Markdown);
    }

    [Fact]
    public void MonthReportNotesMissingComparisonWhenRetentionTooShort()
    {
        var report = ReportGenerator.Generate(new ReportInput
        {
            Period = ReportPeriod.Month, Now = Now,
            Events = [Cpu(Now.AddDays(-2)), Mark(Now.AddDays(-1))],
            PreviousPeriodEvents = [],
            RetentionDays = 30
        });
        Assert.Contains(report.Notes, n => n.Contains("60 天"));
        var overview = report.Sections.Single(s => s.Title == "概览");
        Assert.Equal("上期无可比数据", overview.Metrics[0].Delta);
        Assert.Contains("上期无可比数据", report.Markdown);
    }

    [Fact]
    public void EmptyWindowProducesHonestReportInsteadOfFiller()
    {
        var report = ReportGenerator.Generate(new ReportInput
        {
            Period = ReportPeriod.Month, Now = Now, RetentionDays = 30
        });
        Assert.Contains("没有记录到", report.Headline);
        Assert.Contains("没有可用采样", report.Summary);
        var offenders = report.Sections.Single(s => s.Title == "最可能拖慢电脑的软件");
        Assert.Empty(offenders.Metrics);
        Assert.Contains(offenders.Bullets, b => b.Contains("需要开启性能历史"));
        Assert.Contains(report.Notes, n => n.Contains("没有系统采样"));
        Assert.InRange(report.Confidence, 30, 50);
    }

    [Fact]
    public void TopOffendersRankByEvidenceAndShowLagOverlap()
    {
        var events = new List<PerformanceEvent>
        {
            Cpu(Now.AddDays(-1), 70, "hog.exe"), Cpu(Now.AddDays(-2), 70, "hog.exe"),
            Cpu(Now.AddDays(-3), 70, "hog.exe"), Cpu(Now.AddDays(-4), 70, "hog.exe"),
            Cpu(Now.AddDays(-1).AddMinutes(5), 30, "sync.exe"), Cpu(Now.AddDays(-2).AddMinutes(5), 30, "sync.exe"),
            Mark(Now.AddDays(-1).AddSeconds(20)) // 距 hog 的 -1 天事件 20 秒：重合
        };
        var report = ReportGenerator.Generate(new ReportInput
        {
            Period = ReportPeriod.Week, Now = Now, Events = events,
            SystemSampleCount = 20_000, ProcessSampleCount = 5_000
        });

        var offenders = report.Sections.Single(s => s.Title == "最可能拖慢电脑的软件");
        Assert.Equal("hog.exe", offenders.Metrics[0].Label);
        Assert.Contains("重合 1 次", offenders.Metrics[0].Note);
        Assert.Contains("事件 4 次", offenders.Metrics[0].Value);
    }

    [Fact]
    public void LongTermBehaviorsAndPortsAppearInSections()
    {
        var behaviors = new List<ProcessBehaviorFinding>
        {
            new(ProcessBehaviorKind.MemoryGrowth, "cloudsync.exe", Now.AddDays(-10), Now, 85,
                "cloudsync.exe 内存呈持续增长趋势", "观察期内私有内存约增加 486 MB",
                ["线性趋势 1.0 MB/分钟"], 9460, Now.AddDays(-2))
        };
        var ports = new List<PortActivitySummary>
        {
            new(8080, PortProtocol.Tcp, "server.exe", 6, 3600, Now.AddHours(-1))
        };
        var connections = new List<TcpConnectionRecord>
        {
            new(Guid.NewGuid(), 28440, Now.AddHours(-2), "msedge.exe", IpAddressFamily.IPv4,
                "192.0.2.10", 51432, "203.0.113.20", 443, "Established", Now.AddHours(-2), Now.AddHours(-1)),
            new(Guid.NewGuid(), 28440, Now.AddHours(-2), "msedge.exe", IpAddressFamily.IPv4,
                "192.0.2.10", 51433, "203.0.113.21", 443, "Established", Now.AddHours(-2), Now.AddHours(-1)),
            new(Guid.NewGuid(), 28440, Now.AddHours(-2), "msedge.exe", IpAddressFamily.IPv4,
                "192.0.2.10", 51434, "203.0.113.22", 443, "Established", Now.AddHours(-2), Now.AddHours(-1)),
            new(Guid.NewGuid(), 28440, Now.AddHours(-2), "msedge.exe", IpAddressFamily.IPv4,
                "192.0.2.10", 51435, "203.0.113.23", 443, "Established", Now.AddHours(-2), Now.AddHours(-1)),
            new(Guid.NewGuid(), 28440, Now.AddHours(-2), "msedge.exe", IpAddressFamily.IPv4,
                "192.0.2.10", 51436, "203.0.113.24", 443, "Established", Now.AddHours(-2), Now.AddHours(-1))
        };

        var report = ReportGenerator.Generate(new ReportInput
        {
            Period = ReportPeriod.Week, Now = Now,
            Events = [Cpu(Now.AddDays(-1)), Mark(Now.AddDays(-2))],
            Behaviors = behaviors, Ports = ports, Connections = connections,
            SystemSampleCount = 20_000, ProcessSampleCount = 5_000
        });

        var longTerm = report.Sections.Single(s => s.Title == "长期行为");
        Assert.Contains(longTerm.Metrics, m => m.Label == "cloudsync.exe");
        Assert.Contains("内存持续增长", longTerm.Metrics[0].Value);
        var portSection = report.Sections.Single(s => s.Title == "端口与连接");
        Assert.Contains(portSection.Metrics, m => m.Label.Contains("server.exe"));
        Assert.Contains(portSection.Metrics, m => m.Label.Contains("msedge.exe"));
        Assert.Contains("cloudsync.exe", report.Markdown);
        Assert.Contains("数据说明", report.Markdown);
    }
}
