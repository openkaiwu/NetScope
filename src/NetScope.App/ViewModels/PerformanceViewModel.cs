using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetScope.Core.Abstractions;
using NetScope.Core.Knowledge;
using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.App.ViewModels;

/// <summary>进程中心行：以影响分排序，仅代表证据强度，不代表“导致卡顿”。</summary>
public sealed partial class ProcessImpactRowViewModel : ObservableObject
{
    public ProcessImpactRowViewModel(ProcessPerformanceSample sample, double impactScore, int listeningPorts)
    {
        Sample = sample;
        ImpactScore = impactScore;
        ListeningPorts = listeningPorts;
    }

    public ProcessPerformanceSample Sample { get; }
    public string Name => Sample.Name;
    public int Pid => Sample.Process.ProcessId;
    public double ImpactScore { get; }
    public string ImpactText => ImpactScore <= 0 ? "—" : ImpactScore.ToString("0");
    public string CpuText => $"{Sample.CpuPercent:0}%";
    public string MemoryText => FormatBytes(Sample.WorkingSetBytes);
    public string IoText => FormatBytes(Sample.ReadBytesPerSecond + Sample.WriteBytesPerSecond) + "/s";
    public string ForegroundText => Sample.IsForeground ? "前台" : "";
    public int ListeningPorts { get; }
    public string PortsText => ListeningPorts > 0 ? $"{ListeningPorts} 个监听端口" : "";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B"
    };
}

/// <summary>时间线事件卡片。</summary>
public sealed partial class EventCardViewModel : ObservableObject
{
    public EventCardViewModel(PerformanceEvent evt)
    {
        Event = evt;
    }

    public PerformanceEvent Event { get; }

    public string TypeText => Event.Type switch
    {
        PerformanceEventType.CpuContention => "CPU 争用",
        PerformanceEventType.MemoryPressure => "内存压力",
        PerformanceEventType.DiskIoPressure => "I/O 压力",
        PerformanceEventType.NetworkDegradation => "网络退化",
        PerformanceEventType.UserMarkedLag => "用户标记",
        _ => "未知"
    };

    public string TypeGlyph => Event.Type switch
    {
        PerformanceEventType.CpuContention => "\uE950",
        PerformanceEventType.MemoryPressure => "\uEA86",
        PerformanceEventType.DiskIoPressure => "\uEDA2",
        PerformanceEventType.NetworkDegradation => "\uE868",
        PerformanceEventType.UserMarkedLag => "\uE7BA",
        _ => "\uE7BA"
    };

    public string TimeText => Event.StartedAt.ToString("HH:mm:ss");
    public string DurationText => Event.EndedAt is { } ended
        ? $"持续 {Math.Max(1, (int)(ended - Event.StartedAt).TotalSeconds)} 秒"
        : "进行中";

    public string StatusText => Event.Status switch
    {
        PerformanceEventStatus.Capturing => "捕获中",
        PerformanceEventStatus.Confirmed => "已确认",
        PerformanceEventStatus.Closed => "已结束",
        _ => "已结束"
    };

    public string Summary => Event.Summary;
    public string Cause => Event.MostLikelyCause;
    public string ConfidenceText => $"可信度 {Event.Confidence}";
    public IReadOnlyList<string> Evidence => Event.Evidence;
    public IReadOnlyList<string> Recommendations => Event.Recommendations;
    public string ContributorsText => Event.Contributors is { Count: > 0 }
        ? string.Join("、", Event.Contributors.Select(c => $"{c.ProcessName}（{c.ImpactScore:0} 分）"))
        : "无关联进程";
    public string PrimaryProcessText => string.IsNullOrEmpty(Event.PrimaryProcessName) ? "" : $"主关联进程：{Event.PrimaryProcessName}（PID {Event.PrimaryProcess?.ProcessId}）";
}

/// <summary>7 天影响排行行：聚合证据（事件次数/累计时长/卡顿重合）合成的排序，不代表因果定责。</summary>
public sealed class ImpactRankRowViewModel(ImpactRankEntry entry, int rank)
{
    public int Rank { get; } = rank;
    public string RankText => $"#{Rank}";
    public string ProcessName { get; } = entry.ProcessName;
    public int Score { get; } = entry.Score;
    public string ScoreText => Score.ToString();
    public string EventsText => $"事件 {entry.EventCount} 次";
    public string DurationText => $"累计 {FormatDuration(entry.TotalSeconds)}";
    public string LagText => entry.LagRelatedCount > 0 ? $"卡顿相关 {entry.LagRelatedCount} 次" : "无卡顿重合";

    internal static string FormatDuration(double seconds) => seconds switch
    {
        < 90 => $"{seconds:0} 秒",
        < 3600 => $"{seconds / 60:0} 分钟",
        _ => $"{seconds / 3600:0.0} 小时"
    };
}

/// <summary>
/// 性能工作区：概览当前负载、Top 影响进程、事件时间线与“刚才卡了”。
/// 数据全部来自后台 Collector 的 IPC，仅轮询本机命名管道。
/// </summary>
public sealed partial class PerformanceViewModel : ObservableObject, IDisposable
{
    private const int MaxCpuHistory = 450; // 约 15 分钟（2 秒一条）

    private readonly ICollectorClient _client;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _slowTimer;
    private readonly Services.ProcessPathLookup _pathLookup;
    private readonly IProcessFileMetadataProvider? _fileMetadata;
    private int _tick;

    [ObservableProperty] private bool _collectorConnected;
    [ObservableProperty] private string _collectorStatus = "正在连接后台记录…";
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private string _memoryText = "—";
    [ObservableProperty] private double _memoryUsedFraction;
    [ObservableProperty] private string _networkText = "—";
    [ObservableProperty] private string _lastUpdateText = "尚未更新";
    [ObservableProperty] private string _markStatus = "";
    [ObservableProperty] private string _eventContextStatus = "";
    [ObservableProperty] private EventCardViewModel? _selectedEvent;
    [ObservableProperty] private ProcessImpactRowViewModel? _selectedProcess;
    [ObservableProperty] private string _processSearchText = "";
    [ObservableProperty] private string _processDetailStatus = "";
    [ObservableProperty] private string _processIdentityTitle = "";
    [ObservableProperty] private string _processIdentityPath = "";
    [ObservableProperty] private string _processIdentityPublisher = "";
    [ObservableProperty] private string _processIdentityProduct = "";
    [ObservableProperty] private string _processIdentitySignature = "";
    [ObservableProperty] private string _processIdentityPurpose = "";
    [ObservableProperty] private string _processIdentityAdvice = "";
    [ObservableProperty] private string _processWeekSummary = "";
    [ObservableProperty] private string _impactRankingStatus = "";
    [ObservableProperty] private string _historyNotice;
    [ObservableProperty] private bool _isOverviewSelected = true;
    [ObservableProperty] private bool _isEventsSelected;
    [ObservableProperty] private bool _isProcessesSelected;

    public ObservableCollection<ProcessImpactRowViewModel> TopProcesses { get; } = [];
    public ObservableCollection<EventCardViewModel> RecentEvents { get; } = [];
    public ObservableCollection<double> CpuHistory { get; } = [];
    public ObservableCollection<double> MemoryHistory { get; } = [];
    public ObservableCollection<double> NetworkHistory { get; } = [];
    public ObservableCollection<double> EventCpuHistory { get; } = [];
    public ObservableCollection<double> EventMemoryHistory { get; } = [];
    public ObservableCollection<double> EventNetworkHistory { get; } = [];
    public ObservableCollection<double> ProcessCpuHistory { get; } = [];
    public ObservableCollection<EventCardViewModel> ProcessEvents { get; } = [];
    public ObservableCollection<EventCardViewModel> ProcessWeekEvents { get; } = [];
    public ObservableCollection<ImpactRankRowViewModel> ImpactRanking { get; } = [];

    /// <summary>身份识别卡片是否可见：任一身份字段非空即显示。</summary>
    public bool HasProcessIdentity =>
        !string.IsNullOrEmpty(ProcessIdentityTitle) || !string.IsNullOrEmpty(ProcessIdentityPublisher) ||
        !string.IsNullOrEmpty(ProcessIdentityPurpose);

    public PerformanceViewModel(ICollectorClient client, AppSettings settings,
        IProcessFileMetadataProvider? fileMetadata = null, Services.ProcessPathLookup? pathLookup = null)
    {
        _client = client;
        _fileMetadata = fileMetadata;
        _pathLookup = pathLookup ?? new Services.ProcessPathLookup();
        _historyNotice = $"性能历史仅保存在本机（%LocalAppData%\\NetScope\\data），默认保留 {settings.HistoryRetentionDays} 天，可在设置中调整或关闭。";
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await PollAsync();
        _slowTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(8) };
        _slowTimer.Tick += async (_, _) => await PollEventsAsync();
    }

    /// <summary>页面可见时开始轮询；离开页面停止，减少空闲 IPC。</summary>
    public void OnShown()
    {
        _timer.Start();
        _slowTimer.Start();
        _ = PollAsync();
        _ = PollEventsAsync();
    }

    public void OnHidden()
    {
        _timer.Stop();
        _slowTimer.Stop();
    }

    private async Task PollAsync()
    {
        try
        {
            var connected = await _client.IsAvailableAsync();
            if (connected != CollectorConnected)
            {
                CollectorConnected = connected;
                CollectorStatus = connected ? "后台记录运行中" : "后台记录未连接（端口页不受影响）";
            }
            if (!connected)
            {
                if (TopProcesses.Count > 0) TopProcesses.Clear();
                return;
            }

            var system = await _client.GetSystemSampleAsync();
            if (system is not null)
            {
                CpuPercent = system.CpuPercent;
                CpuText = $"{system.CpuPercent:0}%";
                var used = system.TotalMemoryBytes - system.AvailableMemoryBytes;
                MemoryText = $"{FormatBytes(used)} / {FormatBytes(system.TotalMemoryBytes)}";
                MemoryUsedFraction = system.TotalMemoryBytes > 0 ? (double)used / system.TotalMemoryBytes : 0;
                NetworkText = system.NetworkLinkUp
                    ? $"↓ {FormatBytes(system.NetworkReceivedBytesPerSecond)}/s  ↑ {FormatBytes(system.NetworkSentBytesPerSecond)}/s"
                    : "网卡断开";
                LastUpdateText = $"更新于 {DateTime.Now:HH:mm:ss}";

                CpuHistory.Add(Math.Round(system.CpuPercent, 1));
                MemoryHistory.Add(Math.Round(MemoryUsedFraction * 100, 1));
                NetworkHistory.Add(Math.Round((system.NetworkReceivedBytesPerSecond + system.NetworkSentBytesPerSecond) / 1024.0, 1));
                while (CpuHistory.Count > MaxCpuHistory) CpuHistory.RemoveAt(0);
                while (MemoryHistory.Count > MaxCpuHistory) MemoryHistory.RemoveAt(0);
                while (NetworkHistory.Count > MaxCpuHistory) NetworkHistory.RemoveAt(0);
            }

            if (_tick % 2 == 0) await RefreshProcessesAsync();
            _tick++;
        }
        catch (Exception)
        {
            // 轮询失败按未连接处理，下一轮重试
        }
    }

    private async Task RefreshProcessesAsync()
    {
        var processes = await _client.GetProcessSamplesAsync();
        if (processes.IsDefaultOrEmpty)
        {
            TopProcesses.Clear();
            return;
        }

        var ports = await _client.GetPortSnapshotAsync();
        var portCounts = ports
            .Where(p => p.State.Contains("Listen", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.ProcessId)
            .ToDictionary(g => g.Key, g => g.Count());

        var filter = ProcessSearchText?.Trim();
        IEnumerable<ProcessPerformanceSample> ranked = ImpactScoreCalculator.Rank(processes);
        if (!string.IsNullOrEmpty(filter))
            ranked = ranked.Where(p =>
                p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                p.Process.ProcessId.ToString().Contains(filter, StringComparison.Ordinal));

        var rows = ranked.Take(30)
            .Select(p => new ProcessImpactRowViewModel(p, ImpactScoreCalculator.Compute(p), portCounts.GetValueOrDefault(p.Process.ProcessId)))
            .ToList();

        TopProcesses.Clear();
        foreach (var row in rows) TopProcesses.Add(row);
    }

    private async Task PollEventsAsync()
    {
        try
        {
            var events = await _client.GetRecentEventsAsync(100);
            var selectedId = SelectedEvent?.Event.Id;
            RecentEvents.Clear();
            foreach (var evt in events.OrderByDescending(e => e.StartedAt))
                RecentEvents.Add(new EventCardViewModel(evt));
            if (selectedId is { } id && RecentEvents.FirstOrDefault(x => x.Event.Id == id) is { } restored)
                SelectedEvent = restored;
            if (SelectedEvent is not null) await LoadEventContextAsync(SelectedEvent);

            await LoadImpactRankingAsync();
        }
        catch (Exception)
        {
            // 事件轮询失败静默重试
        }
    }

    /// <summary>7 天影响排行：聚合历史事件（频率/时长/卡顿重合），随事件轮询一起低频刷新。</summary>
    private async Task LoadImpactRankingAsync()
    {
        var ranking = await _client.GetImpactRankingAsync(7, 5);
        ImpactRanking.Clear();
        var rank = 1;
        foreach (var entry in ranking)
            ImpactRanking.Add(new ImpactRankRowViewModel(entry, rank++));
        ImpactRankingStatus = ranking.Count > 0
            ? ""
            : "暂无数据：需要开启性能历史并积累事件（含用户“刚才卡了”标记）";
    }

    partial void OnSelectedEventChanged(EventCardViewModel? value) => _ = LoadEventContextAsync(value);

    private async Task LoadEventContextAsync(EventCardViewModel? card)
    {
        if (card is null)
        {
            EventCpuHistory.Clear();
            EventMemoryHistory.Clear();
            EventNetworkHistory.Clear();
            EventContextStatus = "";
            return;
        }
        var start = card.Event.StartedAt.AddSeconds(-30);
        var end = (card.Event.EndedAt ?? card.Event.StartedAt.AddSeconds(30)).AddSeconds(30);
        var samples = await _client.QuerySystemHistoryAsync(start, end);
        EventCpuHistory.Clear();
        EventMemoryHistory.Clear();
        EventNetworkHistory.Clear();
        if (samples.Count == 0)
        {
            EventContextStatus = "该时段暂无历史采样（历史可能已关闭或超出保留期）";
            return;
        }
        EventContextStatus = $"事件前 30 秒 → 事件中 → 事件后 30 秒，共 {samples.Count} 条系统采样";
        foreach (var sample in samples)
        {
            EventCpuHistory.Add(Math.Round(sample.CpuPercent, 1));
            EventMemoryHistory.Add(sample.TotalMemoryBytes > 0 ? Math.Round((sample.TotalMemoryBytes - sample.AvailableMemoryBytes) * 100.0 / sample.TotalMemoryBytes, 1) : 0);
            EventNetworkHistory.Add(Math.Round((sample.NetworkReceivedBytesPerSecond + sample.NetworkSentBytesPerSecond) / 1024.0, 1));
        }
    }

    partial void OnSelectedProcessChanged(ProcessImpactRowViewModel? value) => _ = LoadProcessDetailAsync(value);

    private async Task LoadProcessDetailAsync(ProcessImpactRowViewModel? row)
    {
        ProcessCpuHistory.Clear();
        ProcessEvents.Clear();
        ProcessWeekEvents.Clear();
        ProcessWeekSummary = "";
        ClearProcessIdentity();
        if (row is null) return;

        var samples = await _client.QueryProcessHistoryAsync(row.Sample.Process, DateTimeOffset.Now.AddMinutes(-15), DateTimeOffset.Now);
        foreach (var sample in samples) ProcessCpuHistory.Add(Math.Round(sample.CpuPercent, 1));
        ProcessDetailStatus = samples.Count > 0
            ? $"{row.Name}（PID {row.Pid}）最近 15 分钟采样 {samples.Count} 条"
            : $"{row.Name}（PID {row.Pid}）暂无历史采样（仅保留影响分 Top 进程的历史）";

        foreach (var card in RecentEvents)
        {
            if (card.Event.Contributors?.Any(c => c.Process == row.Sample.Process) == true ||
                card.Event.PrimaryProcess == row.Sample.Process)
                ProcessEvents.Add(card);
        }

        await LoadProcessIdentityAsync(row);
        await LoadProcessWeekEventsAsync(row.Name);
    }

    /// <summary>该进程名过去 7 天关联的性能事件：总数 + 最新若干条（跨 PID 实例按进程名聚合）。</summary>
    private async Task LoadProcessWeekEventsAsync(string processName)
    {
        var summary = await _client.QueryProcessEventsAsync(processName, 7, 8);
        ProcessWeekEvents.Clear();
        foreach (var evt in summary.Events)
            ProcessWeekEvents.Add(new EventCardViewModel(evt));
        ProcessWeekSummary = summary.TotalCount > 0
            ? $"过去 7 天关联性能事件 {summary.TotalCount} 次" +
              (summary.Events.Count < summary.TotalCount ? $"，显示最新 {summary.Events.Count} 条" : "")
            : "过去 7 天无关联性能事件";
    }

    private void ClearProcessIdentity()
    {
        ProcessIdentityTitle = "";
        ProcessIdentityPath = "";
        ProcessIdentityPublisher = "";
        ProcessIdentityProduct = "";
        ProcessIdentitySignature = "";
        ProcessIdentityPurpose = "";
        ProcessIdentityAdvice = "";
        OnPropertyChanged(nameof(HasProcessIdentity));
    }

    private void SetProcessIdentityVisible() => OnPropertyChanged(nameof(HasProcessIdentity));

    /// <summary>
    /// 身份识别：进程知识库（系统进程）优先；第三方进程读取可执行文件元数据与签名。
    /// 全部在后台线程完成，仅命中知识库或拿到元数据后填充 UI 字段。
    /// </summary>
    private async Task LoadProcessIdentityAsync(ProcessImpactRowViewModel row)
    {
        var name = row.Name;
        if (ProcessKnowledgeBase.TryLookup(name, out var entry) && entry is not null)
        {
            ProcessIdentityTitle = entry.DisplayName;
            ProcessIdentityPath = "";
            ProcessIdentityPublisher = $"发布者：{entry.Publisher} · 分类：{entry.Category}";
            ProcessIdentityProduct = "";
            ProcessIdentitySignature = "";
            ProcessIdentityPurpose = entry.Purpose;
            ProcessIdentityAdvice = entry.HighUsageHint + " " + entry.TerminationAdvice;
            SetProcessIdentityVisible();
            return;
        }

        // 第三方进程：经 PID 解析可执行路径，再读元数据（异步线程，签名验证可能耗时数十毫秒）
        var path = await Task.Run(() => _pathLookup.Resolve(row.Pid));
        if (string.IsNullOrEmpty(path) || _fileMetadata is null)
        {
            ProcessIdentityTitle = "";
            ProcessIdentityPath = "";
            ProcessIdentityPublisher = "未收录进程";
            ProcessIdentityProduct = "";
            ProcessIdentitySignature = "";
            ProcessIdentityPurpose = "该进程不在内置知识库中；路径需要更高权限时无法进一步识别。";
            ProcessIdentityAdvice = "";
            SetProcessIdentityVisible();
            return;
        }

        var metadata = await _fileMetadata.ResolveAsync(path);
        ProcessIdentityTitle = metadata?.FileDescription ?? name;
        ProcessIdentityPath = "路径：" + path;
        ProcessIdentityPublisher = metadata?.CompanyName is { } company ? $"发布者：{company}" : "发布者：未知";
        ProcessIdentityProduct = metadata?.ProductName is { } product ? $"产品：{product}" + (metadata.FileVersion is { } v ? $" · {v}" : "") : "";
        ProcessIdentitySignature = metadata?.SignatureState switch
        {
            SignatureState.Valid => "数字签名：有效",
            SignatureState.Missing => "数字签名：未签名",
            SignatureState.Invalid => "数字签名：无法验证",
            _ => ""
        };
        ProcessIdentityPurpose = "";
        ProcessIdentityAdvice = "";
        SetProcessIdentityVisible();
    }

    [RelayCommand]
    private async Task MarkLagAsync()
    {
        MarkStatus = "正在记录现场…";
        try
        {
            var accepted = await _client.MarkLagAsync();
            MarkStatus = accepted
                ? $"已记录 {DateTime.Now:HH:mm:ss} 前的现场，正在合成分析"
                : "后台记录未连接，无法标记";
            await PollEventsAsync();
            await PollAsync();
        }
        catch (Exception)
        {
            MarkStatus = "标记失败，请稍后重试";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await PollAsync();
        await PollEventsAsync();
    }

    [RelayCommand]
    private async Task SearchProcessesAsync() => await RefreshProcessesAsync();

    [RelayCommand] private void ShowOverview() { IsOverviewSelected = true; IsEventsSelected = false; IsProcessesSelected = false; }
    [RelayCommand] private void ShowEvents() { IsOverviewSelected = false; IsEventsSelected = true; IsProcessesSelected = false; }
    [RelayCommand] private void ShowProcesses() { IsOverviewSelected = false; IsEventsSelected = false; IsProcessesSelected = true; }

    public void Dispose()
    {
        _timer.Stop();
        _slowTimer.Stop();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B"
    };
}
