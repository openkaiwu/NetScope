using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;

namespace NetScope.App.ViewModels;

public enum DiagnosticWorkspace
{
    NetworkDiagnostic,
    SpeedTest
}

public sealed partial class DiagnosticStageItemViewModel : ObservableObject
{
    public DiagnosticStageItemViewModel(DiagnosticStage stage, string title)
    {
        Stage = stage;
        Title = title;
    }

    public DiagnosticStage Stage { get; }
    public string Title { get; }
    [ObservableProperty] private DiagnosticStatus _status = DiagnosticStatus.NotTested;
    [ObservableProperty] private string _headline = "等待检测";
    [ObservableProperty] private string _duration = "—";
    [ObservableProperty] private string _metric = "尚无数据";
    [ObservableProperty] private double _confidence;
    [ObservableProperty] private IReadOnlyList<string> _evidence = [];
    [ObservableProperty] private IReadOnlyList<string> _suggestions = [];
    [ObservableProperty] private bool _timedOut;
    [ObservableProperty] private bool _isRunning;
    public string StatusDisplay => IsRunning ? "检测中" : Status switch
    {
        DiagnosticStatus.Healthy => "正常",
        DiagnosticStatus.Degraded => "退化",
        DiagnosticStatus.Fault => "故障",
        _ => "未检测"
    };

    partial void OnStatusChanged(DiagnosticStatus value) => OnPropertyChanged(nameof(StatusDisplay));
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(StatusDisplay));
}

public sealed record ProbeTimingItemViewModel(string Label, string Kind, double Milliseconds)
{
    public string ValueText => Milliseconds < 1 ? "<1 ms" : $"{Milliseconds:0} ms";
    public double BarValue => Math.Clamp(Milliseconds, 0, 1000);
}

public sealed partial class DiagnosticViewModel : ObservableObject
{
    private readonly IDiagnosticEngine _engine;
    private readonly INetworkSnapshotProvider _snapshotProvider;
    private readonly INetworkPerformanceTester _performanceTester;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _performanceCts;
    private readonly HashSet<DiagnosticStage> _reportedStages = [];
    private bool _activeAdapterIsVirtual;
    private bool _hasVpn;
    private bool _hasProxy;
    private int? _activeSignalQuality;

    public DiagnosticViewModel(IDiagnosticEngine engine, INetworkSnapshotProvider snapshotProvider,
        INetworkPerformanceTester performanceTester, AppSettings settings)
    {
        _engine = engine;
        _snapshotProvider = snapshotProvider;
        _performanceTester = performanceTester;
        _settings = settings;
        Stages =
        [
            new(DiagnosticStage.Local, "本机"), new(DiagnosticStage.Adapter, "网卡 / Wi-Fi"),
            new(DiagnosticStage.IpDhcp, "IP / DHCP"), new(DiagnosticStage.Gateway, "网关"),
            new(DiagnosticStage.Dns, "DNS"), new(DiagnosticStage.Internet, "互联网"), new(DiagnosticStage.Target, "目标")
        ];
        SelectedStage = Stages[4];
    }

    public ObservableCollection<DiagnosticStageItemViewModel> Stages { get; }
    public ObservableCollection<double> GatewayLatencySamples { get; } = [];
    public ObservableCollection<ProbeTimingItemViewModel> ProbeTimings { get; } = [];
    [ObservableProperty] private DiagnosticStageItemViewModel _selectedStage;
    [ObservableProperty] private DiagnosticWorkspace _selectedWorkspace = DiagnosticWorkspace.NetworkDiagnostic;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPerformanceRunning;
    [ObservableProperty] private string _networkName = "正在读取网络";
    [ObservableProperty] private string _networkStatus = "检测前摘要";
    [ObservableProperty] private string _summary = "点击“开始诊断”生成真实链路结论";
    [ObservableProperty] private string _confidenceText = "可信度：等待检测";
    [ObservableProperty] private DiagnosticStatus _overallStatus = DiagnosticStatus.NotTested;
    [ObservableProperty] private string _latencyText = "—";
    [ObservableProperty] private string _packetLossText = "—";
    [ObservableProperty] private string _jitterText = "—";
    [ObservableProperty] private string _gatewayStatsText = "运行快速诊断后显示 10 次网关样本";
    [ObservableProperty] private string _linkSpeedText = "—";
    [ObservableProperty] private string _linkSpeedNote = "系统报告的本地链路能力，不等于公网网速";

    [ObservableProperty] private double _performanceProgress;
    [ObservableProperty] private string _performanceStatusText = "尚未开始真实测速";
    [ObservableProperty] private string _performanceSummary = "手动启动后测量到 Cloudflare 边缘节点的吞吐和负载延迟";
    [ObservableProperty] private string _downloadSpeedText = "—";
    [ObservableProperty] private string _uploadSpeedText = "—";
    [ObservableProperty] private string _idleLatencyText = "—";
    [ObservableProperty] private string _loadedLatencyText = "—";
    [ObservableProperty] private string _bufferbloatText = "—";
    [ObservableProperty] private string _performanceTrafficText = "最多约 62 MB · 仅在手动确认后运行";

    public bool IsBusy => IsRunning || IsPerformanceRunning;
    public bool IsNetworkDiagnosticSelected => SelectedWorkspace == DiagnosticWorkspace.NetworkDiagnostic;
    public bool IsSpeedTestSelected => SelectedWorkspace == DiagnosticWorkspace.SpeedTest;

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(IsBusy));
    partial void OnIsPerformanceRunningChanged(bool value) => OnPropertyChanged(nameof(IsBusy));
    partial void OnSelectedWorkspaceChanged(DiagnosticWorkspace value)
    {
        OnPropertyChanged(nameof(IsNetworkDiagnosticSelected));
        OnPropertyChanged(nameof(IsSpeedTestSelected));
    }

    [RelayCommand]
    private void ShowNetworkDiagnostic() => SelectedWorkspace = DiagnosticWorkspace.NetworkDiagnostic;

    [RelayCommand]
    private void ShowSpeedTest() => SelectedWorkspace = DiagnosticWorkspace.SpeedTest;

    public async Task LoadSummaryAsync()
    {
        var snapshot = await _snapshotProvider.CaptureAsync();
        var active = snapshot.ActiveAdapter;
        _activeAdapterIsVirtual = active?.IsVirtual == true;
        _hasVpn = snapshot.HasVpnAdapter;
        _hasProxy = snapshot.HasProxyConfigured;
        _activeSignalQuality = active?.SignalQuality;
        NetworkName = active is null ? "未连接网络" : active.IsWireless && !string.IsNullOrWhiteSpace(active.SsidLabel) ? active.SsidLabel! : active.Name;
        NetworkStatus = active is null ? "没有活动网卡" : $"已连接 · {active.MediaType}";
        if (active is null)
        {
            LinkSpeedText = "—";
            LinkSpeedNote = "没有活动网卡";
            return;
        }

        if (active.IsWireless && (active.ReceiveLinkSpeedBitsPerSecond > 0 || active.TransmitLinkSpeedBitsPerSecond > 0))
        {
            var tx = active.TransmitLinkSpeedBitsPerSecond > 0 ? active.TransmitLinkSpeedBitsPerSecond : active.LinkSpeedBitsPerSecond;
            LinkSpeedText = FormatLinkSpeed(tx);
            LinkSpeedNote = $"接收 {FormatLinkSpeed(active.ReceiveLinkSpeedBitsPerSecond)} · 发送 {FormatLinkSpeed(tx)}";
        }
        else
        {
            LinkSpeedText = FormatLinkSpeed(active.LinkSpeedBitsPerSecond);
            LinkSpeedNote = active.IsVirtual
                ? $"虚拟链路 · 不代表公网速度 · {active.Name}"
                : $"{active.Name} · {active.MediaType}链路";
        }
    }

    [RelayCommand]
    private async Task RunDiagnosticAsync()
    {
        if (IsBusy) return;
        _runCts = new CancellationTokenSource();
        IsRunning = true;
        ResetRunState();
        MarkStageRunning(0);
        try
        {
            var progress = new Progress<DiagnosticStageResult>(ApplyStageProgress);
            var run = await _engine.RunWithProgressAsync(_settings.DiagnosticTargets, TimeSpan.FromSeconds(15), progress, _runCts.Token);
            ApplyCompletion(run);
        }
        finally
        {
            IsRunning = false;
            foreach (var stage in Stages) stage.IsRunning = false;
        }
    }

    [RelayCommand]
    private async Task RunPerformanceTestAsync()
    {
        if (IsBusy) return;
        _performanceCts = new CancellationTokenSource();
        IsPerformanceRunning = true;
        ResetPerformanceState();
        try
        {
            using var totalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_performanceCts.Token, totalTimeout.Token);
            var progress = new Progress<NetworkPerformanceProgress>(ApplyPerformanceProgress);
            var result = await _performanceTester.RunAsync(NetworkPerformanceTestOptions.CloudflareDefault, progress, linked.Token);
            if (result.WasCancelled && totalTimeout.IsCancellationRequested && !_performanceCts.IsCancellationRequested)
                result = result with { Summary = "真实测速达到 65 秒总时限", Error = "当前连接过慢或测速节点响应不完整" };
            ApplyPerformanceResult(result);
        }
        finally { IsPerformanceRunning = false; }
    }

    [RelayCommand]
    private void Cancel()
    {
        _runCts?.Cancel();
        _performanceCts?.Cancel();
    }

    [RelayCommand] private void SelectStage(DiagnosticStageItemViewModel? stage) { if (stage is not null) SelectedStage = stage; }

    private void ResetRunState()
    {
        foreach (var stage in Stages)
        {
            stage.Status = DiagnosticStatus.NotTested;
            stage.Headline = "等待检测";
            stage.Duration = "—";
            stage.Metric = "尚无数据";
            stage.Confidence = 0;
            stage.Evidence = [];
            stage.Suggestions = [];
            stage.TimedOut = false;
            stage.IsRunning = false;
        }
        GatewayLatencySamples.Clear();
        ProbeTimings.Clear();
        _reportedStages.Clear();
        LatencyText = PacketLossText = JitterText = "—";
        GatewayStatsText = "正在采集网关样本";
        OverallStatus = DiagnosticStatus.NotTested;
        ConfidenceText = "可信度：检测中";
    }

    private void ResetPerformanceState()
    {
        PerformanceProgress = 0;
        PerformanceStatusText = "正在准备真实测速";
        PerformanceSummary = "测速期间会产生下载和上传流量，请保持当前网络连接";
        DownloadSpeedText = UploadSpeedText = IdleLatencyText = LoadedLatencyText = BufferbloatText = "—";
        PerformanceTrafficText = "正在计算实际传输量";
    }

    private void MarkStageRunning(int index)
    {
        if (index < 0 || index >= Stages.Count) return;
        var stage = Stages[index];
        stage.IsRunning = true;
        stage.Headline = "正在检测…";
        SelectedStage = stage;
        Summary = $"正在检测：{stage.Title}";
    }

    private void ApplyStageProgress(DiagnosticStageResult result)
    {
        var index = Stages.ToList().FindIndex(x => x.Stage == result.Stage);
        if (index < 0) return;
        var stage = Stages[index];
        _reportedStages.Add(result.Stage);
        stage.IsRunning = false;
        stage.Status = result.Status;
        stage.Headline = result.MostLikelyCause;
        stage.Duration = $"{result.Duration.TotalMilliseconds:0} ms";
        stage.Metric = result.Metrics.Count == 0 ? "无附加指标" : string.Join(" · ", result.Metrics.Take(4).Select(x => $"{x.Key} {x.Value}"));
        stage.Confidence = result.Confidence;
        stage.Evidence = result.Evidence;
        stage.Suggestions = result.Suggestions;
        stage.TimedOut = result.TimedOut;

        if (result.Stage == DiagnosticStage.Gateway)
        {
            GatewayLatencySamples.Clear();
            foreach (var sample in result.NetworkLatencySamples) GatewayLatencySamples.Add(Math.Clamp(sample, 0, 1000));
            if (result.Metrics.TryGetValue("平均延迟", out var latency)) LatencyText = latency;
            if (result.Metrics.TryGetValue("丢包", out var loss)) PacketLossText = loss;
            if (result.Metrics.TryGetValue("抖动", out var jitter)) JitterText = jitter;
            var p95 = result.Metrics.GetValueOrDefault("P95", "—");
            GatewayStatsText = $"平均 {LatencyText} · P95 {p95} · 抖动 {JitterText} · 丢包 {PacketLossText}";
        }

        foreach (var timing in result.NetworkTimings.Where(x => x.Kind != NetworkTimingKind.GatewayIcmp).Take(12))
            ProbeTimings.Add(new ProbeTimingItemViewModel(timing.Label, TimingKindText(timing.Kind), timing.Milliseconds));

        if (index + 1 < Stages.Count) MarkStageRunning(index + 1);
    }

    private void ApplyCompletion(DiagnosticRun run)
    {
        foreach (var result in run.Stages)
        {
            var stage = Stages.FirstOrDefault(x => x.Stage == result.Stage);
            if (stage is null || _reportedStages.Contains(result.Stage)) continue;
            ApplyStageProgress(result);
        }

        Summary = run.Summary;
        OverallStatus = run.OverallStatus;
        ConfidenceText = $"可信度：{(run.Confidence >= .9 ? "高" : run.Confidence >= .7 ? "中" : "低")} · {run.Confidence:P0}";
        SelectedStage = Stages.FirstOrDefault(x => x.Status is DiagnosticStatus.Fault or DiagnosticStatus.Degraded) ?? Stages[^1];
    }

    private void ApplyPerformanceProgress(NetworkPerformanceProgress progress)
    {
        PerformanceProgress = progress.Progress * 100;
        PerformanceStatusText = progress.Message;
        if (progress.BytesTransferred > 0) PerformanceTrafficText = $"已传输约 {FormatBytes(progress.BytesTransferred)}";
    }

    private void ApplyPerformanceResult(NetworkPerformanceResult result)
    {
        PerformanceProgress = result.Succeeded ? 100 : PerformanceProgress;
        PerformanceStatusText = result.WasCancelled ? (result.Error is null ? "测速已取消" : "测速超时") : result.Succeeded ? "真实测速完成" : "测速未完成";
        PerformanceSummary = result.Error is null ? CorrelatePerformanceConclusion(result) : $"{result.Summary}：{result.Error}";
        if (result.Succeeded)
        {
            DownloadSpeedText = $"{result.DownloadMegabitsPerSecond:0.#} Mbps";
            UploadSpeedText = $"{result.UploadMegabitsPerSecond:0.#} Mbps";
            IdleLatencyText = FormatLatency(result.IdleLatencyMilliseconds);
            LoadedLatencyText = $"↓ {FormatLatency(result.DownloadLoadedLatencyMilliseconds)} · ↑ {FormatLatency(result.UploadLoadedLatencyMilliseconds)}";
            BufferbloatText = $"{result.BufferbloatGrade} · +{result.BufferbloatDeltaMilliseconds:0} ms";
        }
        PerformanceTrafficText = $"本次传输约 {FormatBytes(result.DownloadBytes + result.UploadBytes)} · NetScope 不上传测速结果";
    }

    private static string TimingKindText(NetworkTimingKind kind) => kind switch
    {
        NetworkTimingKind.DnsLookup => "DNS",
        NetworkTimingKind.TcpConnect => "TCP",
        NetworkTimingKind.TlsHandshake => "TLS",
        NetworkTimingKind.TargetTcp => "目标 TCP",
        NetworkTimingKind.HttpLatency => "HTTP RTT",
        _ => "ICMP"
    };

    private string CorrelatePerformanceConclusion(NetworkPerformanceResult result)
    {
        var adapter = Stages.First(x => x.Stage == DiagnosticStage.Adapter);
        var gateway = Stages.First(x => x.Stage == DiagnosticStage.Gateway);
        var dns = Stages.First(x => x.Stage == DiagnosticStage.Dns);
        if (adapter.Status == DiagnosticStatus.Degraded && _activeSignalQuality is { } signal)
            return $"最可能的瓶颈在 Wi-Fi 到路由器之间：信号 {signal}%。{result.Summary}";
        if (gateway.Status == DiagnosticStatus.Degraded)
            return $"最可能的瓶颈在本机到网关之间：{gateway.Headline}。{result.Summary}";
        if (dns.Status == DiagnosticStatus.Degraded)
            return $"DNS 响应可能拖慢网页首次打开，但不直接限制大文件吞吐。{result.Summary}";
        if (_activeAdapterIsVirtual || _hasVpn || _hasProxy)
            return $"当前流量经过虚拟接口、VPN 或代理，测速包含其开销；建议关闭后对照复测。{result.Summary}";
        return result.Summary;
    }

    private static string FormatLinkSpeed(long bitsPerSecond) => bitsPerSecond switch
    {
        <= 0 => "—",
        >= 1_000_000_000 => $"{bitsPerSecond / 1_000_000_000d:0.#} Gbps",
        _ => $"{bitsPerSecond / 1_000_000d:0.#} Mbps"
    };

    private static string FormatLatency(double milliseconds) => milliseconds switch
    {
        <= 0 => "—",
        < 1 => "<1 ms",
        _ => $"{milliseconds:0.#} ms"
    };

    private static string FormatBytes(long bytes) => bytes >= 1_000_000 ? $"{bytes / 1_000_000d:0.#} MB" : $"{bytes / 1_000d:0} KB";
}
