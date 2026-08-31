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
        using (var stream = File.Create(Path.Combine(output, "netscope-default-port-v030.png")))
            encoder.Save(stream);

        window.Close();
        port.Dispose();
        performance.Dispose();
    }

    private static void RenderPerformancePage(string output)
    {
        var performance = new PerformanceViewModel(new SampleCollectorClient(), new AppSettings());
        PopulatePerformance(performance);

        Render(performance, 1140, 720, Path.Combine(output, "netscope-performance-v030.png"));

        performance.IsOverviewSelected = false;
        performance.IsEventsSelected = true;
        performance.SelectedEvent = performance.RecentEvents.FirstOrDefault();
        Render(performance, 1140, 720, Path.Combine(output, "netscope-performance-events-v030.png"));

        performance.IsEventsSelected = false;
        performance.IsProcessesSelected = true;
        // 选 svchost：知识库命中 + 7 天事件在同步路径完成，保证截图前数据已就位
        performance.SelectedProcess = performance.TopProcesses.FirstOrDefault(x => x.Name == "svchost.exe")
                                      ?? performance.TopProcesses.FirstOrDefault();
        Render(performance, 1140, 720, Path.Combine(output, "netscope-performance-processes-v030.png"));

        performance.Dispose();
    }

    private static void PopulatePerformance(PerformanceViewModel vm)
    {
        var now = DateTimeOffset.Now;
        vm.CollectorConnected = true;
        vm.CollectorStatus = "后台记录运行中";
        vm.LastUpdateText = $"更新于 {now:HH:mm:ss}";
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

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
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
