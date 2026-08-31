using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetScope.Core.Abstractions;
using NetScope.Core.Knowledge;
using NetScope.Core.Models;
using NetScope.Core.Services;

namespace NetScope.App.ViewModels;

public sealed partial class PortRowViewModel : ObservableObject
{
    public PortRowViewModel(PortBindingSnapshot snapshot, DateTimeOffset changedAt, bool isNew)
    {
        Snapshot = snapshot;
        ChangedAt = changedAt;
        IsNew = isNew;
    }

    public PortBindingSnapshot Snapshot { get; }
    public int Port => Snapshot.Port;
    public string Protocol => Snapshot.Protocol.ToString().ToUpperInvariant();
    public string Address => Snapshot.LocalAddress;
    public string State => Snapshot.State;
    public int Pid => Snapshot.ProcessId;
    public string ProcessName => Snapshot.Process?.Name ?? "正在识别…";
    public string ProcessPath => Snapshot.Process?.Path ?? Snapshot.Process?.StatusMessage ?? "路径不可用";
    public string Purpose => Snapshot.CatalogEntry?.ChineseDescription ?? "未登记用途";
    public string Service => Snapshot.CatalogEntry?.Service ?? "—";
    public string ChangedText => ChangedAt.ToString("HH:mm:ss");
    public DateTimeOffset ChangedAt { get; }
    [ObservableProperty] private bool _isNew;
}

public sealed class PortCatalogRowViewModel
{
    public PortCatalogRowViewModel(PortCatalogEntry entry, IReadOnlyList<PortBindingSnapshot> occupied)
    {
        Entry = entry;
        var bindings = occupied.Where(x => entry.Contains(x.Port, x.Protocol)).ToArray();
        IsOccupied = bindings.Length > 0;
        OccupancyText = IsOccupied
            ? string.Join("、", bindings.Select(x => $"{x.Process?.Name ?? "未知进程"} (PID {x.ProcessId})").Distinct().Take(3))
            : "当前未占用";
    }

    public PortCatalogEntry Entry { get; }
    public string PortRange => Entry.PortStart == Entry.PortEnd ? Entry.PortStart.ToString() : $"{Entry.PortStart}–{Entry.PortEnd}";
    public string Protocol => Entry.Protocol?.ToString().ToUpperInvariant() ?? "TCP/UDP";
    public string Service => Entry.Service;
    public string Description => string.IsNullOrWhiteSpace(Entry.ChineseDescription) ? "IANA 已登记，暂无中文人工说明" : Entry.ChineseDescription;
    public string Category => string.IsNullOrWhiteSpace(Entry.Category) ? "标准服务" : Entry.Category;
    public string RiskText => Entry.IsHighRisk ? "高风险/谨慎暴露" : Entry.PortStart < 1024 ? "标准服务端口" : "按需开放";
    public string ExposureAdvice => Entry.IsHighRisk
        ? "不建议直接暴露到公网；确认服务身份并限制防火墙来源。"
        : Entry.PortStart < 1024
            ? "这是常见标准服务端口。开放前确认实际进程、监听地址与访问控制。"
            : "端口用途不等于实际流量内容；请结合当前占用进程判断。";
    public string CommonSoftware => Entry.Service.ToLowerInvariant() switch
    {
        "ssh" => "OpenSSH、Git、SFTP/SCP",
        "http" => "IIS、Nginx、Apache、开发服务器",
        "https" => "IIS、Nginx、Apache、反向代理",
        "domain" => "Windows DNS、BIND、dnsmasq",
        "mysql" => "MySQL、MariaDB",
        "ms-sql-s" => "Microsoft SQL Server",
        "postgresql" => "PostgreSQL",
        "redis" => "Redis",
        "mongodb" => "MongoDB",
        "rdp" or "ms-wbt-server" => "Windows 远程桌面",
        _ => "具体软件取决于实际监听进程"
    };
    public bool IsOccupied { get; }
    public string OccupancyText { get; }
}

public sealed class PortRangeRowViewModel
{
    public PortRangeRowViewModel(string range, string protocol, string kind, string status, string description, bool recommended = false)
    {
        Range = range; Protocol = protocol; Kind = kind; Status = status; Description = description; IsRecommended = recommended;
    }
    public string Range { get; }
    public string Protocol { get; }
    public string Kind { get; }
    public string Status { get; }
    public string Description { get; }
    public bool IsRecommended { get; }
}

public sealed partial class PortViewModel : ObservableObject, IDisposable
{
    private readonly IPortTableProvider _provider;
    private readonly IProcessMetadataResolver _processResolver;
    private readonly IProcessFileMetadataProvider? _fileMetadata;
    private readonly IPortCatalog _catalog;
    private readonly PortRecommendationService _recommendation;
    private readonly IPortSystemRangeProvider _systemRangeProvider;
    private readonly PortSnapshotDiffer _differ;
    private readonly PortSearchEngine _search;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, PortRowViewModel> _rowsByIdentity = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ProcessIdentity> _processCache = new();
    private readonly Dictionary<int, DateTimeOffset> _processVerified = new();
    private ImmutableArray<PortBindingSnapshot> _last = [];
    private int _refreshing;

    public PortViewModel(IPortTableProvider provider, IProcessMetadataResolver processResolver, IPortCatalog catalog,
        IPortAvailabilityProbe availability, IPortSystemRangeProvider systemRangeProvider,
        PortSnapshotDiffer differ, PortSearchEngine search, AppSettings settings,
        IProcessFileMetadataProvider? fileMetadata = null)
    {
        _provider = provider;
        _processResolver = processResolver;
        _fileMetadata = fileMetadata;
        _catalog = catalog;
        _recommendation = new(catalog, availability);
        _systemRangeProvider = systemRangeProvider;
        _differ = differ;
        _search = search;
        _settings = settings;
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = FilterRow;
        CatalogRowsView = CollectionViewSource.GetDefaultView(CatalogRows);
        CatalogRowsView.Filter = FilterCatalogRow;
        AvailabilityRowsView = CollectionViewSource.GetDefaultView(AvailabilityRows);
        AvailabilityRowsView.Filter = FilterAvailabilityRow;
        RefreshCatalogRows();
    }

    public ObservableCollection<PortRowViewModel> Rows { get; } = [];
    public ObservableCollection<PortAvailabilityResult> Recommendations { get; } = [];
    public ObservableCollection<PortCatalogRowViewModel> CatalogRows { get; } = [];
    public ObservableCollection<PortRangeRowViewModel> AvailabilityRows { get; } = [];
    public System.ComponentModel.ICollectionView RowsView { get; }
    public System.ComponentModel.ICollectionView CatalogRowsView { get; }
    public System.ComponentModel.ICollectionView AvailabilityRowsView { get; }
    public IReadOnlyList<int> OccupiedTcpPorts => Rows.Where(x => x.Snapshot.Protocol == PortProtocol.Tcp).Select(x => x.Port).Distinct().ToArray();
    public IReadOnlyList<int> OccupiedUdpPorts => Rows.Where(x => x.Snapshot.Protocol == PortProtocol.Udp).Select(x => x.Port).Distinct().ToArray();
    public IReadOnlyList<int> SpectrumPorts => ProtocolFilter == "UDP" ? OccupiedUdpPorts : OccupiedTcpPorts;
    public string SpectrumProtocolLabel => ProtocolFilter == "UDP" ? "UDP" : "TCP";
    public string RecommendationProtocolLabel => RecommendationProtocol.ToString().ToUpperInvariant();
    public IReadOnlyList<int> RecommendedPorts => Recommendations.Where(x => x.Protocol == RecommendationProtocol && x.IsRecommended).Select(x => x.Port).ToArray();
    public IReadOnlyList<int> AvailabilityOccupiedPorts => RecommendationProtocol == PortProtocol.Udp ? OccupiedUdpPorts : OccupiedTcpPorts;
    public SystemPortRangeSnapshot SystemPortRanges { get; private set; } = SystemPortRangeSnapshot.Default;
    public string RangeSourceText => SystemPortRanges.UsedDefaultDynamicRange
        ? "部分系统范围读取失败，动态范围使用 Windows 默认判断"
        : $"已读取 Windows 当前动态与排除范围 · {SystemPortRanges.CapturedAt:HH:mm:ss}";
    public string CatalogResultText => $"显示 {CatalogRows.Count} 条知识库结果";

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _protocolFilter = "全部";
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "正在建立端口快照";
    [ObservableProperty] private int _listeningCount;
    [ObservableProperty] private int _udpCount;
    [ObservableProperty] private int _processCount;
    [ObservableProperty] private int _changeCount;
    [ObservableProperty] private PortRowViewModel? _selectedRow;
    [ObservableProperty] private string _selectedProcessIdentity = "";
    [ObservableProperty] private string _selectedProcessSignature = "";
    [ObservableProperty] private PortProtocol _recommendationProtocol = PortProtocol.Tcp;
    [ObservableProperty] private string _catalogSearchText = string.Empty;
    [ObservableProperty] private string _catalogProtocolFilter = "全部";
    [ObservableProperty] private PortCatalogRowViewModel? _selectedCatalogRow;
    [ObservableProperty] private string _availabilityFilter = "全部";

    partial void OnSearchTextChanged(string value)
    {
        RowsView.Refresh();
        if (value.StartsWith("pid:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("proc:", StringComparison.OrdinalIgnoreCase)) return;
        CatalogSearchText = value.StartsWith("port:", StringComparison.OrdinalIgnoreCase) ? value[5..].Trim() : value;
    }
    partial void OnProtocolFilterChanged(string value)
    {
        RowsView.Refresh();
        OnPropertyChanged(nameof(SpectrumPorts));
        OnPropertyChanged(nameof(SpectrumProtocolLabel));
    }

    partial void OnRecommendationProtocolChanged(PortProtocol value)
    {
        OnPropertyChanged(nameof(RecommendationProtocolLabel));
        OnPropertyChanged(nameof(AvailabilityOccupiedPorts));
    }
    partial void OnCatalogSearchTextChanged(string value) => RefreshCatalogRows();
    partial void OnCatalogProtocolFilterChanged(string value) => CatalogRowsView.Refresh();
    partial void OnAvailabilityFilterChanged(string value) => AvailabilityRowsView.Refresh();
    partial void OnIsPausedChanged(bool value) => StatusText = value ? "数据已冻结" : "实时监测中";

    partial void OnSelectedRowChanged(PortRowViewModel? value) => _ = LoadSelectedProcessIdentityAsync(value);

    /// <summary>
    /// 端口详情的身份识别：系统进程走内置知识库（含用途与结束建议）；
    /// 第三方进程读文件元数据与签名（有磁盘缓存，重复选择同一进程不再验证）。
    /// </summary>
    private async Task LoadSelectedProcessIdentityAsync(PortRowViewModel? row)
    {
        SelectedProcessIdentity = "";
        SelectedProcessSignature = "";
        if (row?.Snapshot.Process is not { } process) return;

        var exeName = Path.GetFileName(process.Path ?? process.Name);
        if (ProcessKnowledgeBase.TryLookup(exeName, out var entry) && entry is not null)
        {
            SelectedProcessIdentity = $"{entry.DisplayName} · {entry.Purpose}";
            SelectedProcessSignature = entry.TerminationAdvice;
            return;
        }

        if (string.IsNullOrEmpty(process.Path) || _fileMetadata is null) return;
        var metadata = await _fileMetadata.ResolveAsync(process.Path);
        if (metadata is null) return;
        SelectedProcessIdentity = metadata.FileDescription is { } description
            ? $"{description}" + (metadata.CompanyName is { } company ? $" · {company}" : "")
            : metadata.CompanyName ?? "";
        SelectedProcessSignature = metadata.SignatureState switch
        {
            SignatureState.Valid => "数字签名有效",
            SignatureState.Missing => "可执行文件未签名",
            SignatureState.Invalid => "数字签名无法验证",
            _ => ""
        };
    }

    public async Task StartAsync()
    {
        await RefreshAsync();
        await LoadSystemRangesAsync();
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_settings.ForegroundRefreshMilliseconds));
            try
            {
                while (await timer.WaitForNextTickAsync(_lifetime.Token))
                    if (!IsPaused) await RefreshAsync();
            }
            catch (OperationCanceledException) { }
        });
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0) return;
        try
        {
            IsBusy = true;
            var captured = (await _provider.CaptureAsync(_lifetime.Token))
                .Where(x => x.Protocol == PortProtocol.Udp || x.State.Equals("Listen", StringComparison.OrdinalIgnoreCase))
                .ToImmutableArray();
            var verificationCutoff = DateTimeOffset.Now.AddSeconds(-30);
            var pids = captured.Select(x => x.ProcessId).Where(x => x >= 0 &&
                (!_processVerified.TryGetValue(x, out var verified) || verified < verificationCutoff)).Distinct().Take(512).ToArray();
            await Parallel.ForEachAsync(pids, new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = _lifetime.Token }, async (pid, token) =>
            {
                var process = await _processResolver.ResolveAsync(pid, token);
                lock (_processCache)
                {
                    _processCache[pid] = process;
                    _processVerified[pid] = DateTimeOffset.Now;
                }
            });

            var enriched = captured.Select(x => x with
            {
                Process = _processCache.GetValueOrDefault(x.ProcessId),
                CatalogEntry = _catalog.Find(x.Port, x.Protocol)
            }).ToImmutableArray();
            var now = DateTimeOffset.Now;
            var diff = _differ.Compare(_last, enriched, now);
            _last = enriched;
            await Application.Current.Dispatcher.InvokeAsync(() => ApplyDiff(diff, now));
        }
        catch (OperationCanceledException) { }
        finally { IsBusy = false; Interlocked.Exchange(ref _refreshing, 0); }
    }

    private void ApplyDiff(PortDiff diff, DateTimeOffset now)
    {
        if (diff.Changes.Length == 0)
        {
            StatusText = IsPaused ? "数据已冻结" : $"实时监测中 · {now:HH:mm:ss} 更新";
            return;
        }
        foreach (var change in diff.Changes.Where(x => x.Kind == PortChangeKind.Removed && x.Before is not null))
        {
            if (_rowsByIdentity.Remove(change.Before!.Key.Identity, out var row)) Rows.Remove(row);
        }
        foreach (var change in diff.Changes.Where(x => x.After is not null))
        {
            var snapshot = change.After!;
            if (_rowsByIdentity.Remove(snapshot.Key.Identity, out var old)) Rows.Remove(old);
            var row = new PortRowViewModel(snapshot, now, change.Kind == PortChangeKind.Added);
            _rowsByIdentity[snapshot.Key.Identity] = row;
            Rows.Add(row);
            if (row.IsNew)
            {
                _ = Task.Delay(1200).ContinueWith(_ => Application.Current.Dispatcher.Invoke(() => row.IsNew = false));
            }
        }

        ListeningCount = diff.Current.Count(x => x.Protocol == PortProtocol.Tcp && x.State.Equals("Listen", StringComparison.OrdinalIgnoreCase));
        UdpCount = diff.Current.Count(x => x.Protocol == PortProtocol.Udp);
        ProcessCount = diff.Current.Select(x => x.ProcessId).Distinct().Count();
        ChangeCount = diff.Changes.Length;
        StatusText = IsPaused ? "数据已冻结" : $"实时监测中 · {now:HH:mm:ss} 更新";
        if (SelectedRow is null || !Rows.Contains(SelectedRow)) SelectedRow = Rows.FirstOrDefault();
        OnPropertyChanged(nameof(OccupiedTcpPorts));
        OnPropertyChanged(nameof(OccupiedUdpPorts));
        OnPropertyChanged(nameof(SpectrumPorts));
        OnPropertyChanged(nameof(AvailabilityOccupiedPorts));
        RefreshCatalogRows();
        RebuildAvailabilityRows();
        RowsView.Refresh();
    }

    [RelayCommand]
    private void SetFilter(string? filter) => ProtocolFilter = string.IsNullOrWhiteSpace(filter) ? "全部" : filter;

    [RelayCommand]
    private void SetRecommendationProtocol(string? protocol)
    {
        RecommendationProtocol = string.Equals(protocol, "UDP", StringComparison.OrdinalIgnoreCase)
            ? PortProtocol.Udp
            : PortProtocol.Tcp;
        Recommendations.Clear();
        OnPropertyChanged(nameof(RecommendedPorts));
        RebuildAvailabilityRows();
    }

    [RelayCommand]
    private async Task RefreshRecommendationsAsync()
    {
        var results = await _recommendation.RecommendAsync(RecommendationProtocol, _last, 20, _lifetime.Token, SystemPortRanges.Ranges);
        Recommendations.Clear();
        foreach (var item in results) Recommendations.Add(item);
        OnPropertyChanged(nameof(RecommendedPorts));
        RebuildAvailabilityRows();
    }

    [RelayCommand]
    private void SetCatalogProtocolFilter(string? filter) => CatalogProtocolFilter = string.IsNullOrWhiteSpace(filter) ? "全部" : filter;

    [RelayCommand]
    private void SetAvailabilityFilter(string? filter) => AvailabilityFilter = string.IsNullOrWhiteSpace(filter) ? "全部" : filter;

    [RelayCommand]
    private async Task ReloadSystemRangesAsync() => await LoadSystemRangesAsync();

    private bool FilterRow(object item)
    {
        if (item is not PortRowViewModel row) return false;
        if (ProtocolFilter == "TCP" && row.Snapshot.Protocol != PortProtocol.Tcp) return false;
        if (ProtocolFilter == "UDP" && row.Snapshot.Protocol != PortProtocol.Udp) return false;
        return string.IsNullOrWhiteSpace(SearchText) || _search.Search([row.Snapshot], SearchText).Count > 0;
    }

    private bool FilterCatalogRow(object item)
    {
        if (item is not PortCatalogRowViewModel row) return false;
        if (CatalogProtocolFilter == "TCP" && row.Entry.Protocol != PortProtocol.Tcp) return false;
        if (CatalogProtocolFilter == "UDP" && row.Entry.Protocol != PortProtocol.Udp) return false;
        if (CatalogProtocolFilter == "高风险" && !row.Entry.IsHighRisk) return false;
        return true;
    }

    private bool FilterAvailabilityRow(object item)
    {
        if (item is not PortRangeRowViewModel row) return false;
        return AvailabilityFilter == "全部" || row.Kind == AvailabilityFilter;
    }

    private void RefreshCatalogRows()
    {
        IEnumerable<PortCatalogEntry> entries;
        if (string.IsNullOrWhiteSpace(CatalogSearchText))
        {
            var commonPorts = new[] { 22, 25, 53, 67, 68, 80, 110, 123, 135, 137, 138, 139, 143, 161, 389, 443, 445, 465, 587, 636, 993, 995, 1433, 1521, 1883, 2049, 2375, 3000, 3306, 3389, 5432, 5672, 5900, 6379, 8080, 8443, 9092, 27017 };
            entries = commonPorts.SelectMany(port => new[] { _catalog.Find(port, PortProtocol.Tcp), _catalog.Find(port, PortProtocol.Udp) })
                .OfType<PortCatalogEntry>().DistinctBy(x => (x.PortStart, x.PortEnd, x.Protocol));
        }
        else entries = _catalog.Search(CatalogSearchText, 300);

        var rows = entries
            .Select(x => new PortCatalogRowViewModel(x, _last))
            .ToArray();
        CatalogRows.Clear();
        foreach (var row in rows) CatalogRows.Add(row);
        CatalogRowsView.Refresh();
        SelectedCatalogRow = rows.FirstOrDefault();
        OnPropertyChanged(nameof(CatalogResultText));
    }

    private async Task LoadSystemRangesAsync()
    {
        SystemPortRanges = await _systemRangeProvider.CaptureAsync(_lifetime.Token);
        OnPropertyChanged(nameof(SystemPortRanges));
        OnPropertyChanged(nameof(RangeSourceText));
        RebuildAvailabilityRows();
    }

    private void RebuildAvailabilityRows()
    {
        AvailabilityRows.Clear();
        var protocol = RecommendationProtocol;
        var protocolText = protocol.ToString().ToUpperInvariant();
        AvailabilityRows.Add(new("0–1023", protocolText, "标准服务", "IANA 管理范围", "知名系统与标准服务端口；使用前确认权限、服务身份和暴露风险。"));
        AvailabilityRows.Add(new("1024–49151", protocolText, "候选范围", "尚未逐端口验证", "应用可选范围；会继续排除已登记、占用、高风险和 Windows 系统范围。"));
        foreach (var highRisk in new[] { "1900", "4444", "5555", "6660–7000", "31337" })
            AvailabilityRows.Add(new(highRisk, protocolText, "高风险端口", "内置风险规则", "常被遗留服务、远程控制或恶意工具使用；不作为自动推荐候选。"));
        foreach (var range in SystemPortRanges.Ranges.Where(x => x.Protocol == protocol).OrderBy(x => x.Start))
        {
            var kind = range.Kind == PortRangeKind.Dynamic ? "动态端口" : "系统排除";
            AvailabilityRows.Add(new(range.Display, protocolText, kind, range.Source, range.Description));
        }
        foreach (var binding in _last.Where(x => x.Protocol == protocol).DistinctBy(x => x.Port).OrderBy(x => x.Port).Take(80))
            AvailabilityRows.Add(new(binding.Port.ToString(), protocolText, "当前占用", binding.Process?.Name ?? $"PID {binding.ProcessId}", "当前存在监听或绑定。"));
        foreach (var item in Recommendations.Where(x => x.Protocol == protocol && x.IsRecommended))
            AvailabilityRows.Insert(0, new(item.Port.ToString(), protocolText, "当前推荐", "IPv4/IPv6 独占绑定通过", item.Reason, true));
        AvailabilityRowsView.Refresh();
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
