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
using NetScope.Windows.Ports;

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
            settings);
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

        var main = new MainViewModel(port, diagnosticViewModel, null!);
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
        using (var stream = File.Create(Path.Combine(output, "netscope-default-port-v015.png")))
            encoder.Save(stream);

        window.Close();
        port.Dispose();
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
