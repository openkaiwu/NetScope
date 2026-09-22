using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetScope.App;
using NetScope.App.Services;
using NetScope.App.ViewModels;
using NetScope.App.Views;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;
using NetScope.Core.Services;
using NetScope.Windows.Ipc;
using NetScope.Windows.Ports;
using NetScope.Windows.Settings;

namespace NetScope.VisualQa;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var application = new NetScope.App.App();
        application.InitializeComponent();
        ThemePalette.Apply(AppTheme.Light);

        var viewModel = new DiagnosticViewModel(new NoopEngine(), new SampleSnapshotProvider(), new NoopPerformanceTester(), new AppSettings());
        viewModel.LoadSummaryAsync().GetAwaiter().GetResult();
        Populate(viewModel);

        var output = args.FirstOrDefault() ?? Path.Combine(Environment.CurrentDirectory, "design", "qa");
        Directory.CreateDirectory(output);
        viewModel.SelectedWorkspace = DiagnosticWorkspace.NetworkDiagnostic;
        Render(viewModel, 972, 700, Path.Combine(output, "diagnostic-network-workspace-v013.png"));
        viewModel.SelectedWorkspace = DiagnosticWorkspace.SpeedTest;
        Render(viewModel, 972, 700, Path.Combine(output, "diagnostic-speed-workspace-v013.png"));
        Render(viewModel, 712, 560, Path.Combine(output, "diagnostic-speed-compact-v013.png"));
        RenderDefaultPortShell(viewModel, output);
        RenderPerformancePage(output);
    }

    private static void RenderDefaultPortShell(DiagnosticViewModel diagnosticViewModel, string output)
    {
        var settings = new AppSettings();
        var catalog = new PackagedPortCatalog();
        var port = new PortViewModel(
            new SamplePortTableProvider(),
            new SampleProcessResolver(),
            catalog,
            new SampleAvailabilityProbe(),
            new SamplePortSystemRangeProvider(),
            new PortSnapshotDiffer(),
            new PortSearchEngine(),
            settings,
            null,
            new SampleCollectorClient());
        var now = DateTimeOffset.Now;
        foreach (var snapshot in SamplePortTableProvider.CreateRows())
        {
            var process = SampleProcessResolver.Create(snapshot.ProcessId);
            var enriched = snapshot with { Process = process, CatalogEntry = catalog.Find(snapshot.Port, snapshot.Protocol) };
            port.Rows.Add(new PortRowViewModel(enriched, now, false));
        }
        port.ListeningCount = port.Rows.Count(row => row.Snapshot.Protocol == PortProtocol.Tcp);
        port.UdpCount = port.Rows.Count(row => row.Snapshot.Protocol == PortProtocol.Udp);
        port.ProcessCount = port.Rows.Select(row => row.Pid).Distinct().Count();
        port.ChangeCount = port.Rows.Count;
        port.StatusText = $"实时监测中 · {now:HH:mm:ss} 更新";
        port.SelectedRow = port.Rows.FirstOrDefault();

        var performance = new PerformanceViewModel(new NullCollectorClient(), settings);
        var settingsVm = new SettingsViewModel(new JsonSettingsStore(Path.Combine(Path.GetTempPath(), "netscope-visualqa-settings.json")), new StartupRegistration(), settings);
        var main = new MainViewModel(port, performance, diagnosticViewModel, settingsVm);
        if (main.SelectedNavigation != "端口" || !ReferenceEquals(main.CurrentPage, port))
            throw new InvalidOperationException("NetScope default page must be the port workspace");

        var window = new MainWindow(main)
        {
            Width = 1180,
            Height = 760,
            Left = -20_000,
            Top = -20_000,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.Show();
        window.UpdateLayout();

        var bitmap = new RenderTargetBitmap(1180, 760, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(output, "netscope-default-port-v060.png")))
            encoder.Save(stream);

        window.Close();
        port.Dispose();
        performance.Dispose();
    }

    private static void RenderPerformancePage(string output)
    {
        var performance = new PerformanceViewModel(new SampleCollectorClient(), new AppSettings());
        PopulatePerformance(performance);

        Render(performance, 1140, 720, Path.Combine(output, "netscope-performance-v060.png"));

        performance.IsOverviewSelected = false;
        performance.IsEventsSelected = true;
        performance.SelectedEvent = performance.RecentEvents.FirstOrDefault();
        Render(performance, 1140, 720, Path.Combine(output, "netscope-performance-events-v060.png"));

        // V1.0：事件详情的归因链 + 回放原始证据曲线
        performance.ReplayEventEvidenceCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        if (performance.EventChainSteps.Count != 5 || !performance.HasEventProcessTrend || performance.EventProcessCpuHistory.Count == 0)
            throw new InvalidOperationException("Attribution chain or replay curves did not load");
        Render(performance, 1140, 720, Path.Combine(output, "netscope-performance-attribution-v100.png"));

        performance.IsEventsSelected = false;
        performance.IsProcessesSelected = true;
        // 选 svchost：知识库命中 + 7 天事件在同步路径完成，保证截图前数据已就位
        performance.SelectedProcess = performance.TopProcesses.FirstOrDefault(x => x.Name == "svchost.exe")
                                      ?? performance.TopProcesses.FirstOrDefault();
        // V1.1 结束建议卡片（svchost：强烈不建议，只显示说明与替代建议）
        performance.TerminationLevelText = "结束建议：强烈不建议";
        performance.TerminationConclusion = "svchost.exe 承载 Windows 服务，结束会中断这些服务。建议不要直接结束。";
        performance.TerminationEvidence.Add("当前用户进程");
        performance.TerminationEvidence.Add("非 Windows 关键进程（已查询关键标志）");
        performance.TerminationEvidence.Add("签名有效：Microsoft Corporation");
        performance.TerminationEvidence.Add("承载的服务：WSearch、wuauserv、Dhcp");
        performance.TerminationAlternatives.Add("在服务管理器中定位该实例承载的服务");
        performance.TerminationAlternatives.Add("重启对应服务而不是结束宿主");
        performance.TerminationStatus = "该进程不提供结束操作，请参考替代建议。";
        performance.HasTerminationAdvice = true;
        if (!performance.HasTerminationAdvice || performance.TerminationEvidence.Count < 4)
            throw new InvalidOperationException("Termination advice card did not populate");
        Render(performance, 1140, 720, Path.Combine(output, "netscope-performance-processes-v060.png"));

        // V1.1：可结束进程的建议卡片（谨慎级，显示双按钮）
        performance.TerminationLevelText = "结束建议：可以结束，但可能中断功能或丢失数据";
        performance.TerminationConclusion = "存在主窗口，未保存内容可能丢失。";
        performance.TerminationEvidence.Clear();
        performance.TerminationEvidence.Add("当前用户进程");
        performance.TerminationEvidence.Add("非 Windows 关键进程（已查询关键标志）");
        performance.TerminationEvidence.Add("位于当前会话（Session 1）");
        performance.TerminationEvidence.Add("签名有效：Microsoft Corporation");
        performance.TerminationEvidence.Add("监听 2 个端口、5 条活动连接，结束会中断这些服务");
        performance.TerminationStatus = "";
        performance.AllowClose = true;
        performance.AllowTerminate = true;
        Render(performance, 1140, 720, Path.Combine(output, "netscope-performance-termination-v110.png"));
        performance.AllowClose = false;
        performance.AllowTerminate = false;

        performance.ShowOverviewCommand.Execute(null);
        Render(performance, 860, 580, Path.Combine(output, "netscope-performance-compact-v060.png"));
        performance.IsOverviewSelected = false;
        performance.IsInsightsSelected = true;
        Render(performance, 1140, 720, Path.Combine(output, "netscope-insights-v060.png"));
        Render(performance, 860, 580, Path.Combine(output, "netscope-insights-compact-v060.png"));

        var trendInsight = performance.Insights.First(x => x.Item.Kind == InsightKind.MemoryGrowth);
        performance.OpenInsightSourceCommand.ExecuteAsync(trendInsight).GetAwaiter().GetResult();
        if (!performance.ProcessHistoryTitle.StartsWith("洞察原始窗口", StringComparison.Ordinal) ||
            performance.ProcessMemoryHistory.Count is 0 or > 900)
            throw new InvalidOperationException("Trend insight did not reopen its exact process history window");
        Render(performance, 1140, 720, Path.Combine(output, "netscope-insight-process-evidence-v060.png"));

        performance.IsProcessesSelected = false;
        performance.IsInsightsSelected = true;
        var portInsight = performance.Insights.First(x => x.Item.Kind == InsightKind.PortActivity);
        performance.OpenInsightSourceCommand.ExecuteAsync(portInsight).GetAwaiter().GetResult();
        if (!performance.HasInsightSource || !performance.InsightSourceText.Contains("Tcp/135", StringComparison.Ordinal))
            throw new InvalidOperationException("Port insight did not reopen its source records");
        Render(performance, 1140, 720, Path.Combine(output, "netscope-insight-port-evidence-v060.png"));

        performance.IsInsightsSelected = false;
        performance.ShowConnectionsCommand.Execute(null);
        var now = DateTimeOffset.Now;
        performance.Connections.Add(new(new TcpConnectionRecord(Guid.NewGuid(), 28440, now.AddHours(-1), "msedge.exe", IpAddressFamily.IPv4,
            "192.0.2.10", 51432, "203.0.113.20", 443, "Established", now.AddMinutes(-2), now)));
        performance.Connections.Add(new(new TcpConnectionRecord(Guid.NewGuid(), 9460, now.AddHours(-1), "mysqld.exe", IpAddressFamily.IPv6,
            "::1", 3306, "::1", 53120, "CloseWait", now.AddMinutes(-3), now.AddMinutes(-1), now.AddMinutes(-1), "后续快照中消失")));
        performance.ConnectionStatus = "过去 7 天，示例数据：当前连接与历史连接";
        performance.ConnectionView.Refresh();
        Render(performance, 1140, 720, Path.Combine(output, "netscope-connections-v060.png"));
        performance.ConnectionGroup = "进程";
        Render(performance, 860, 580, Path.Combine(output, "netscope-connections-compact-v060.png"));

        // V1.0：周/月报告页
        performance.IsConnectionsSelected = false;
        performance.IsEventsSelected = false;
        performance.IsReportsSelected = true;
        performance.ReportPeriodFilter = "周报";
        performance.LoadReportCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        if (!performance.HasReport || performance.ReportSections.Count == 0)
            throw new InvalidOperationException("Report page did not load any sections");
        Render(performance, 1140, 720, Path.Combine(output, "netscope-performance-report-v100.png"));
        Render(performance, 860, 580, Path.Combine(output, "netscope-performance-report-compact-v100.png"));
        performance.ReportPeriodFilter = "月报";
        performance.LoadReportCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        if (!performance.ReportHeadline.Contains("本月"))
            throw new InvalidOperationException("Month report did not switch");
        Render(performance, 1140, 720, Path.Combine(output, "netscope-performance-report-month-v100.png"));
        performance.Dispose();
    }

    private static void PopulatePerformance(PerformanceViewModel vm)
    {
        var now = DateTimeOffset.Now;
        vm.CollectorConnected = true;
        vm.CollectorStatus = "后台记录运行中";
        vm.CollectorHealthText = "自监控：Normal · CPU 0.18% · 私有内存 38.6 MB";
        vm.CollectorHealthDetail = "工作集 72.4 MB；读 2.1 KB/s；写 1.0 KB/s；周期耗时 18 ms；当前未触发自动降频";
        vm.LastUpdateText = $"更新于 {now:HH:mm:ss}";
        vm.ResponsivenessText = "响应性 88/100 · 较流畅";
        vm.DiskText = "磁盘合计 · 读 2.3 ms / 写 4.1 ms · 队列 0.20 · 活跃 24%";
        vm.CpuPercent = 37;
        vm.CpuText = "37%";
        vm.MemoryText = "12.6 GB / 31.9 GB";
        vm.NetworkText = "↓ 1.2 MB/s  ↑ 860 KB/s";
        vm.MarkStatus = $"已记录 {now.AddSeconds(-25):HH:mm:ss} 前的现场，正在合成分析";

        var random = new Random(42);
        for (var i = 0; i < 60; i++)
        {
            var spike = i is >= 42 and <= 48;
            vm.CpuHistory.Add(Math.Round(Math.Min(99, 24 + random.NextDouble() * 22 + (spike ? 34 + random.NextDouble() * 12 : 0)), 1));
            vm.MemoryHistory.Add(Math.Round(34 + random.NextDouble() * 6, 1));
            vm.NetworkHistory.Add(Math.Round(120 + random.NextDouble() * 700 + (spike ? 600 : 0), 1));
        }

        var key = (int pid) => new ProcessInstanceKey(pid, now.AddMinutes(-10));
        var procs = new[]
        {
            SampleProc(key(28440), "msedge.exe", 42.0, 2_200_000_000, 3_800_000, 1_200_000, true),
            SampleProc(key(11484), "NetScope.Collector.exe", 18.0, 160_000_000, 120_000, 40_000, false),
            SampleProc(key(3916), "devenv.exe", 11.0, 1_400_000_000, 90_000, 210_000, false),
            SampleProc(key(9460), "mysqld.exe", 4.0, 980_000_000, 2_400_000, 5_100_000, false),
            SampleProc(key(1260), "svchost.exe", 2.0, 210_000_000, 12_000, 8_000, false)
        };
        foreach (var sample in procs)
            vm.TopProcesses.Add(new ProcessImpactRowViewModel(sample, ImpactScoreCalculator.Compute(sample), 3));

        // 7 天影响排行（概览截图不走轮询，直接填充与 SampleCollectorClient 一致的数据）
        var ranking = new[]
        {
            new ImpactRankEntry("msedge.exe", 14, 3600 * 1.8, 3, 78),
            new ImpactRankEntry("mysqld.exe", 6, 3600 * 2.4, 1, 61),
            new ImpactRankEntry("devenv.exe", 4, 3600 * 0.6, 1, 42),
            new ImpactRankEntry("svchost.exe", 3, 900, 0, 24),
            new ImpactRankEntry("NetScope.Collector.exe", 1, 120, 0, 9),
        };
        var rank = 1;
        foreach (var entry in ranking)
            vm.ImpactRanking.Add(new ImpactRankRowViewModel(entry, rank++));

        var contributors = new[]
        {
            new PerformanceEventContributor(key(28440), "msedge.exe", 42),
            new PerformanceEventContributor(key(11484), "NetScope.Collector.exe", 18),
            new PerformanceEventContributor(key(3916), "devenv.exe", 11)
        };
        vm.RecentEvents.Add(new EventCardViewModel(new PerformanceEvent(
            Guid.NewGuid(), PerformanceEventType.CpuContention, PerformanceEventStatus.Confirmed,
            now.AddMinutes(-6), now.AddMinutes(-6).AddSeconds(12), 80,
            "系统 CPU 连续 12 秒超过 85%，疑似资源争用",
            "msedge.exe（PID 28440）在事件期间 CPU 与内存均显著抬升",
            new[] { "系统 CPU 峰值 94%，持续 12 秒", "msedge.exe 平均 CPU 42%，明显高于基线", "事件发生在最近一次用户标记前 30 秒" },
            new[] { "检查浏览器后台标签与扩展数量", "如反复出现，可重启该进程后再观察" },
            key(28440), "msedge.exe", contributors)));
        vm.RecentEvents.Add(new EventCardViewModel(new PerformanceEvent(
            Guid.NewGuid(), PerformanceEventType.UserMarkedLag, PerformanceEventStatus.Confirmed,
            now.AddSeconds(-25), now.AddSeconds(-20), 100,
            "您标记了一次卡顿，已记录现场并进入高频采样",
            "等待归因结果",
            new[] { "您于此刻点击「刚才卡了」", "已自动进入 500ms 高频采样" },
            new[] { "30–60 秒后查看归因结果与关联进程" },
            null, null, Array.Empty<PerformanceEventContributor>())));

        vm.Insights.Add(new(new InsightItem(Guid.NewGuid(), InsightKind.LagSummary,
            "30 天内标记卡顿 7 次", "其中 5 次包含 msedge.exe 的相关证据", 85,
            now.AddDays(-30), now, "msedge.exe",
            ["统计窗口为最近 30 天", "用户标记事件 7 条", "关联表示资源证据重合，不代表确定因果"],
            Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray())));
        vm.Insights.Add(new(new InsightItem(Guid.NewGuid(), InsightKind.MemoryGrowth,
            "cloudsync.exe 内存呈持续增长趋势", "观察期内私有内存约增加 486 MB，可能存在泄漏或持续缓存", 88,
            now.AddHours(-8), now, "cloudsync.exe",
            ["线性趋势 1.0 MB/分钟，拟合度 R²=0.91", "非下降采样占比 82%，样本 96 条", "趋势不能单独证明内存泄漏"], [],
            18420, now.AddDays(-2))));
        vm.Insights.Add(new(new InsightItem(Guid.NewGuid(), InsightKind.PeriodicActivity,
            "updater.exe 出现周期性后台活动", "检测到 9 次活动，平均约每 20 分钟一次", 82,
            now.AddHours(-3), now, "updater.exe",
            ["周期离散系数 0.08（越低越规律）", "活动阈值 CPU 8.1% 或 I/O 1.0 MB/s"], [],
            20516, now.AddHours(-12))));
        vm.Insights.Add(new(new InsightItem(Guid.NewGuid(), InsightKind.PortActivity,
            "svchost.exe 的监听端口活动较多", "记录 7 个占用会话，最常见 Tcp/135", 62,
            now.AddDays(-7), now, "svchost.exe",
            ["端口会话 7 个", "端口出现频繁不代表风险或异常"], [], Port: 135, Protocol: PortProtocol.Tcp)));
        vm.InsightStatus = "基于最近 30 天本地历史生成 4 条；结论可回查，不代表确定因果";
    }

    private static ProcessPerformanceSample SampleProc(ProcessInstanceKey key, string name, double cpu, long ws, long readBps, long writeBps, bool foreground)
        => new(key, DateTimeOffset.Now, name, cpu, ws, ws, readBps, writeBps, 0, 0, true, null, foreground);

    /// <summary>返回确定性的示例采样数据，让性能页各子页在截图中显示真实内容。</summary>
    private sealed class SampleCollectorClient : ICollectorClient
    {
        private readonly DateTimeOffset _now = DateTimeOffset.Now;
        private readonly Random _random = new(7);

        public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

        public ValueTask<ImmutableArray<PortBindingSnapshot>> GetPortSnapshotAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SamplePortTableProvider.CreateRows());

        public ValueTask<SystemPerformanceSample?> GetSystemSampleAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SystemPerformanceSample?>(new SystemPerformanceSample(_now, 37, 20_000_000_000, 34_296_963_072, 1_200_000, 860_000, true, "以太网"));

        public ValueTask<ImmutableArray<ProcessPerformanceSample>> GetProcessSamplesAsync(CancellationToken cancellationToken = default)
        {
            var key = (int pid) => new ProcessInstanceKey(pid, _now.AddMinutes(-10));
            return ValueTask.FromResult(ImmutableArray.Create(
                SampleProc(key(28440), "msedge.exe", 42.0, 2_200_000_000, 3_800_000, 1_200_000, true),
                SampleProc(key(11484), "NetScope.Collector.exe", 18.0, 160_000_000, 120_000, 40_000, false),
                SampleProc(key(3916), "devenv.exe", 11.0, 1_400_000_000, 90_000, 210_000, false),
                SampleProc(key(9460), "mysqld.exe", 4.0, 980_000_000, 2_400_000, 5_100_000, false),
                SampleProc(key(1260), "svchost.exe", 2.0, 210_000_000, 12_000, 8_000, false)));
        }

        public ValueTask<bool> MarkLagAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

        public ValueTask<IReadOnlyList<PerformanceEvent>> GetRecentEventsAsync(int limit = 100, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<PerformanceEvent>>(BuildEvents());

        public ValueTask<IReadOnlyList<SystemPerformanceSample>> QuerySystemHistoryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            var list = new List<SystemPerformanceSample>();
            for (var t = from; t <= to; t = t.AddSeconds(1))
            {
                var cpu = 24 + _random.NextDouble() * 22;
                if (t > to.AddSeconds(-22) && t < to.AddSeconds(-10)) cpu += 34 + _random.NextDouble() * 10;
                list.Add(new SystemPerformanceSample(t, Math.Round(Math.Min(99, cpu), 1), 20_000_000_000, 34_296_963_072, 1_200_000, 860_000, true, "以太网"));
            }
            return ValueTask.FromResult<IReadOnlyList<SystemPerformanceSample>>(list);
        }

        public ValueTask<IReadOnlyList<ProcessPerformanceSample>> QueryProcessHistoryAsync(ProcessInstanceKey process, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            var list = new List<ProcessPerformanceSample>();
            var spike = process.ProcessId == 28440;
            var name = spike ? "msedge.exe" : "NetScope.Collector.exe";
            for (var t = from; t <= to; t = t.AddSeconds(15))
            {
                var cpu = spike ? 8 + _random.NextDouble() * 30 : 3 + _random.NextDouble() * 10;
                if (spike && t > to.AddMinutes(-8) && t < to.AddMinutes(-6)) cpu += 40;
                list.Add(new ProcessPerformanceSample(process, t, name, Math.Round(Math.Min(99, cpu), 1),
                    2_200_000_000, 2_000_000_000, 3_800_000, 1_200_000, 0, 0, true, null, spike));
            }
            return ValueTask.FromResult<IReadOnlyList<ProcessPerformanceSample>>(list);
        }

        public ValueTask<IReadOnlyList<PortUsageSummary>> QueryPortUsageAsync(int port, PortProtocol protocol, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<PortUsageSummary>>(port == 135
                ?
                [
                    new PortUsageSummary(135, PortProtocol.Tcp, "svchost", 6, 5 * 24 * 3600 + 3600, _now.AddMinutes(-3)),
                    new PortUsageSummary(135, PortProtocol.Tcp, "SpoolerService", 1, 3600 * 2.5, _now.AddDays(-4)),
                ]
                : []);

        public ValueTask<ProcessEventsSummary> QueryProcessEventsAsync(string processName, int days = 7, int limit = 10, CancellationToken cancellationToken = default)
        {
            var known = processName.StartsWith("msedge", StringComparison.OrdinalIgnoreCase) ||
                        processName.StartsWith("svchost", StringComparison.OrdinalIgnoreCase);
            return ValueTask.FromResult(new ProcessEventsSummary(known ? 3 : 0,
                known ? BuildEvents().Where(e => e.Type != PerformanceEventType.UserMarkedLag).Take(limit).ToList() : []));
        }

        public ValueTask<IReadOnlyList<ImpactRankEntry>> GetImpactRankingAsync(int days = 7, int limit = 10, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ImpactRankEntry>>(
            [
                new ImpactRankEntry("msedge.exe", 14, 3600 * 1.8, 3, 78),
                new ImpactRankEntry("mysqld.exe", 6, 3600 * 2.4, 1, 61),
                new ImpactRankEntry("devenv.exe", 4, 3600 * 0.6, 1, 42),
                new ImpactRankEntry("svchost.exe", 3, 900, 0, 24),
                new ImpactRankEntry("NetScope.Collector.exe", 1, 120, 0, 9),
            ]);

        public ValueTask<PerformanceReport?> GetReportAsync(ReportRequest request, CancellationToken cancellationToken = default)
        {
            // 用真实 ReportGenerator 走完整生成路径，保证截图内容与产品行为一致
            var period = request.Period;
            var now = DateTimeOffset.Now;
            var days = period == ReportPeriod.Month ? 30 : 7;
            var events = new List<PerformanceEvent>();
            var marks = new List<PerformanceEvent>();
            for (var i = 0; i < (period == ReportPeriod.Month ? 6 : 3); i++)
            {
                var at = now.AddDays(-(i * 2 + 1));
                events.Add(new PerformanceEvent(
                    Guid.NewGuid(), PerformanceEventType.CpuContention, PerformanceEventStatus.Closed,
                    at, at.AddSeconds(45), 78, "可能存在 CPU 争用", "msedge.exe 在事件期间 CPU 显著抬升",
                    ["系统 CPU 峰值 92%", "msedge.exe 平均 CPU 46%"],
                    ["检查浏览器后台标签与扩展数量"],
                    new ProcessInstanceKey(28440, at), "msedge.exe",
                    [new PerformanceEventContributor(new(28440, at), "msedge.exe", 72),
                     new PerformanceEventContributor(new(3916, at), "devenv.exe", 21)]));
                if (i < 2)
                {
                    var markAt = at.AddSeconds(30);
                    marks.Add(new PerformanceEvent(
                        Guid.NewGuid(), PerformanceEventType.UserMarkedLag, PerformanceEventStatus.Confirmed,
                        markAt.AddSeconds(-60), markAt, 100, "用户标记卡顿", "msedge.exe 影响分最高",
                        ["用户手动标记"], [], new ProcessInstanceKey(28440, at), "msedge.exe",
                        [new PerformanceEventContributor(new(28440, at), "msedge.exe", 68)]));
                }
            }
            if (period == ReportPeriod.Month)
                events.Add(new PerformanceEvent(
                    Guid.NewGuid(), PerformanceEventType.MemoryPressure, PerformanceEventStatus.Closed,
                    now.AddDays(-4), now.AddDays(-4).AddSeconds(90), 70, "可能存在内存压力", "mysqld.exe 工作集占用最大",
                    ["可用内存低于阈值"], [], new ProcessInstanceKey(9460, now.AddDays(-4)), "mysqld.exe",
                    [new PerformanceEventContributor(new(9460, now.AddDays(-4)), "mysqld.exe", 55)]));
            var input = new ReportInput
            {
                Period = period,
                Now = now,
                Events = [.. events, .. marks],
                PreviousPeriodEvents = [],
                Behaviors =
                [
                    new ProcessBehaviorFinding(ProcessBehaviorKind.MemoryGrowth, "cloudsync.exe",
                        now.AddDays(-8), now, 84, "cloudsync.exe 内存呈持续增长趋势",
                        "观察期内私有内存约增加 402 MB",
                        ["线性趋势 0.8 MB/分钟，拟合度 R²=0.88"], 18420, now.AddDays(-2)),
                ],
                Ports =
                [
                    new PortActivitySummary(8080, PortProtocol.Tcp, "server.exe", 9, 3600 * 30, now.AddHours(-1)),
                    new PortActivitySummary(3306, PortProtocol.Tcp, "mysqld.exe", 6, 3600 * 48, now.AddHours(-2)),
                ],
                Connections = Enumerable.Range(0, 6).Select(i => new TcpConnectionRecord(
                    Guid.NewGuid(), 28440, now.AddHours(-i - 1), "msedge.exe", IpAddressFamily.IPv4,
                    "192.0.2.10", 51432 + i, $"203.0.113.{20 + i}", 443, "Established",
                    now.AddHours(-i - 1), now.AddMinutes(-i))).ToList(),
                SystemSampleCount = 18_000,
                ProcessSampleCount = 4_600,
                RetentionDays = 30
            };
            return ValueTask.FromResult<PerformanceReport?>(ReportGenerator.Generate(input));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private IReadOnlyList<PerformanceEvent> BuildEvents()
        {
            var key = (int pid) => new ProcessInstanceKey(pid, _now.AddMinutes(-10));
            return new List<PerformanceEvent>
            {
                new(Guid.NewGuid(), PerformanceEventType.CpuContention, PerformanceEventStatus.Confirmed,
                    _now.AddMinutes(-6), _now.AddMinutes(-6).AddSeconds(12), 80,
                    "系统 CPU 连续 12 秒超过 85%，疑似资源争用",
                    "msedge.exe（PID 28440）在事件期间 CPU 与内存均显著抬升",
                    new[] { "系统 CPU 峰值 94%，持续 12 秒", "msedge.exe 平均 CPU 42%，明显高于基线", "事件发生在最近一次用户标记前 30 秒" },
                    new[] { "检查浏览器后台标签与扩展数量", "如反复出现，可重启该进程后再观察" },
                    key(28440), "msedge.exe",
                    new[] { new PerformanceEventContributor(key(28440), "msedge.exe", 42), new PerformanceEventContributor(key(11484), "NetScope.Collector.exe", 18), new PerformanceEventContributor(key(3916), "devenv.exe", 11) }),
                new(Guid.NewGuid(), PerformanceEventType.UserMarkedLag, PerformanceEventStatus.Confirmed,
                    _now.AddSeconds(-25), _now.AddSeconds(-20), 100,
                    "您标记了一次卡顿，已记录现场并进入高频采样",
                    "等待归因结果",
                    new[] { "您于此刻点击「刚才卡了」", "已自动进入 500ms 高频采样" },
                    new[] { "30–60 秒后查看归因结果与关联进程" },
                    null, null, Array.Empty<PerformanceEventContributor>())
            };
        }
    }

    private static void Render(PerformanceViewModel viewModel, double width, double height, string path)
    {
        var view = new PerformanceView
        {
            DataContext = viewModel,
            Width = width,
            Height = height,
            Background = (Brush)Application.Current.Resources["CanvasBrush"]
        };
        Render(view, width, height, path);
    }

    private static void Render(FrameworkElement view, double width, double height, string path)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        // RenderTargetBitmap 没有真实窗口的滚动视口，DataGrid 行虚拟化可能把可见行也延迟创建。
        // 截图夹具只关闭这次离屏渲染的虚拟化；产品 XAML 仍保持回收式虚拟化。
        DisableDataGridVirtualization(view);
        view.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void DisableDataGridVirtualization(DependencyObject root)
    {
        if (root is System.Windows.Controls.DataGrid grid)
        {
            grid.EnableRowVirtualization = false;
            grid.EnableColumnVirtualization = false;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            DisableDataGridVirtualization(VisualTreeHelper.GetChild(root, index));
    }

    private static void Populate(DiagnosticViewModel viewModel)
    {
        viewModel.Summary = "本地链路稳定，DNS 正常；公网 TLS 响应略慢";
        viewModel.OverallStatus = DiagnosticStatus.Degraded;
        viewModel.ConfidenceText = "可信度：高 · 93%";
        viewModel.LatencyText = "2.4 ms";
        viewModel.PacketLossText = "0%";
        viewModel.JitterText = "0.8 ms";
        viewModel.GatewayStatsText = "平均 2.4 ms · P95 4.1 ms · 抖动 0.8 ms · 丢包 0%";
        foreach (var sample in new double[] { 1.8, 2.1, 1.9, 2.5, 4.1, 2.8, 2.2, 1.7, 2.4, 2.0 })
            viewModel.GatewayLatencySamples.Add(sample);
        viewModel.ProbeTimings.Add(new("DNS · Microsoft", "DNS", 12));
        viewModel.ProbeTimings.Add(new("DNS · Cloudflare", "DNS", 18));
        viewModel.ProbeTimings.Add(new("TCP · Cloudflare", "TCP", 36));
        viewModel.ProbeTimings.Add(new("TLS · Cloudflare", "TLS", 84));
        viewModel.ProbeTimings.Add(new("目标 · 百度", "目标 TCP", 52));

        var titles = new[] { "本机正常", "活动网卡已识别", "IP 与 DHCP 正常", "本地网关可达", "DNS 响应正常", "公网 TLS 略慢", "目标可达" };
        for (var index = 0; index < viewModel.Stages.Count; index++)
        {
            var stage = viewModel.Stages[index];
            stage.Status = index == 5 ? DiagnosticStatus.Degraded : DiagnosticStatus.Healthy;
            stage.Headline = titles[index];
            stage.Duration = index switch { 3 => "612 ms", 4 => "44 ms", 5 => "286 ms", _ => "<1 ms" };
            stage.Metric = index == 5 ? "平均 TCP 36 ms · 平均 TLS 84 ms" : "真实探针已完成";
            stage.Confidence = index == 5 ? .86 : .96;
            stage.Evidence = index == 5 ? ["3/3 个目标完成 TLS 握手", "TLS 耗时高于本地网关和 DNS"] : ["系统实时证据检查通过"];
            stage.Suggestions = index == 5 ? ["比较其他网络环境", "若单站点较慢，检查目标服务器"] : ["继续观察即可"];
        }
        viewModel.SelectedStage = viewModel.Stages[5];

        viewModel.PerformanceProgress = 100;
        viewModel.PerformanceStatusText = "真实测速完成";
        viewModel.PerformanceSummary = "到测速节点的吞吐正常；负载下延迟增加 18 ms，Bufferbloat 等级 B";
        viewModel.DownloadSpeedText = "286.4 Mbps";
        viewModel.UploadSpeedText = "52.7 Mbps";
        viewModel.IdleLatencyText = "24.6 ms";
        viewModel.LoadedLatencyText = "↓ 38 ms · ↑ 43 ms";
        viewModel.BufferbloatText = "B · +18 ms";
        viewModel.PerformanceTrafficText = "本次传输约 43.6 MB · NetScope 不上传测速结果";
    }

    private static void Render(DiagnosticViewModel viewModel, double width, double height, string path)
    {
        var view = new DiagnosticView
        {
            DataContext = viewModel,
            Width = width,
            Height = height,
            Background = (Brush)Application.Current.Resources["CanvasBrush"]
        };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class SampleSnapshotProvider : INetworkSnapshotProvider
    {
        public ValueTask<NetworkSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
        {
            var adapter = new NetworkAdapterSnapshot("sample", "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet", true, false,
                100_000_000_000, null, null, ["172.24.64.1"], ["172.24.64.254"], ["1.1.1.1"], 0, 0,
                IsVirtual: true, MediaType: "以太网");
            return ValueTask.FromResult(new NetworkSnapshot(DateTimeOffset.Now, true, [adapter], false, true, true, "sample"));
        }
    }

    private sealed class NoopEngine : IDiagnosticEngine
    {
        public ValueTask<DiagnosticRun> RunAsync(IReadOnlyList<DiagnosticTarget> targets, TimeSpan timeout, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<DiagnosticRun> RunWithProgressAsync(IReadOnlyList<DiagnosticTarget> targets, TimeSpan timeout, IProgress<DiagnosticStageResult> progress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoopPerformanceTester : INetworkPerformanceTester
    {
        public ValueTask<NetworkPerformanceResult> RunAsync(NetworkPerformanceTestOptions options, IProgress<NetworkPerformanceProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SamplePortTableProvider : IPortTableProvider
    {
        public static ImmutableArray<PortBindingSnapshot> CreateRows()
        {
            var now = DateTimeOffset.Now;
            var rows = new (int Port, PortProtocol Protocol, int Pid, string State)[]
            {
                (135, PortProtocol.Tcp, 1260, "Listen"),
                (445, PortProtocol.Tcp, 4, "Listen"),
                (3000, PortProtocol.Tcp, 12884, "Listen"),
                (3306, PortProtocol.Tcp, 9460, "Listen"),
                (5432, PortProtocol.Tcp, 17320, "Listen"),
                (6379, PortProtocol.Tcp, 20840, "Listen"),
                (8080, PortProtocol.Tcp, 12884, "Listen"),
                (53, PortProtocol.Udp, 3016, "Bound"),
                (5353, PortProtocol.Udp, 3016, "Bound")
            };
            return rows.Select(row => new PortBindingSnapshot(
                new PortBindingKey(row.Protocol, IpAddressFamily.IPv4, "0.0.0.0", row.Port, row.Pid, row.State),
                now)).ToImmutableArray();
        }

        public ValueTask<ImmutableArray<PortBindingSnapshot>> CaptureAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateRows());
    }

    private sealed class SampleProcessResolver : IProcessMetadataResolver
    {
        public static ProcessIdentity Create(int processId)
        {
            var name = processId switch
            {
                4 => "System",
                1260 => "svchost.exe",
                12884 => "dotnet.exe",
                9460 => "mysqld.exe",
                17320 => "postgres.exe",
                20840 => "redis-server.exe",
                _ => "mDNSResponder.exe"
            };
            return new ProcessIdentity(processId, DateTimeOffset.Now.AddMinutes(-20), name,
                $"C:\\Program Files\\Sample\\{name}", true, false);
        }

        public ValueTask<ProcessIdentity> ResolveAsync(int processId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Create(processId));
    }

    private sealed class SampleAvailabilityProbe : IPortAvailabilityProbe
    {
        public ValueTask<PortAvailabilityResult> ProbeAsync(int port, PortProtocol protocol, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new PortAvailabilityResult(port, protocol, true, true, true, "IPv4/IPv6 独占绑定通过"));
    }

    private sealed class SamplePortSystemRangeProvider : IPortSystemRangeProvider
    {
        public ValueTask<SystemPortRangeSnapshot> CaptureAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(SystemPortRangeSnapshot.Default with { CapturedAt = DateTimeOffset.Now });
    }
}
