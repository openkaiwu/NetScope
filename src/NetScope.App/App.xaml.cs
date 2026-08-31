using System.Windows;
using NetScope.App.Services;
using NetScope.App.ViewModels;
using NetScope.Core.Abstractions;
using NetScope.Core.Services;
using NetScope.Windows.Ipc;
using NetScope.Windows.Logging;
using NetScope.Windows.Metadata;
using NetScope.Windows.Network;
using NetScope.Windows.Ports;
using NetScope.Windows.Settings;

namespace NetScope.App;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstance;
    private TrayService? _tray;
    private MainWindow? _window;
    private ICollectorClient? _collectorClient;
    private bool _allowExit;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new SingleInstanceCoordinator("NetScope.Desktop.V01");
        if (!_singleInstance.IsPrimary)
        {
            await _singleInstance.ForwardAsync(e.Args);
            Shutdown();
            return;
        }

        var logger = new RollingFileLogger();
        var settingsStore = new JsonSettingsStore();
        var settings = await settingsStore.LoadAsync();
        ThemePalette.Apply(settings.Theme);
        var catalog = new PackagedPortCatalog();
        ICollectorClient? collectorClient = null;
        if (!e.Args.Contains("--no-collector", StringComparer.OrdinalIgnoreCase))
        {
            collectorClient = new CollectorClient();
            _collectorClient = collectorClient;
            _ = CollectorLauncher.EnsureRunningAsync(collectorClient);
        }
        IPortTableProvider portTable = collectorClient is not null
            ? new CollectorPortTableProvider(collectorClient, new WindowsPortTableProvider())
            : new WindowsPortTableProvider();
        var processResolver = new WindowsProcessMetadataResolver();
        var fileMetadata = new CachedProcessMetadataProvider();
        var availability = new WindowsPortAvailabilityProbe();
        var systemRanges = new WindowsPortSystemRangeProvider();
        var networkSnapshot = new SystemNetworkSnapshotProvider();
        IDiagnosticProbe[] probes =
        [
            new LocalDiagnosticProbe(), new AdapterDiagnosticProbe(), new IpDhcpDiagnosticProbe(),
            new GatewayDiagnosticProbe(), new DnsDiagnosticProbe(), new InternetDiagnosticProbe(), new TargetDiagnosticProbe()
        ];
        var engine = new DiagnosticEngine(networkSnapshot, probes);
        var performanceTester = new HttpNetworkPerformanceTester();

        var port = new PortViewModel(portTable, processResolver, catalog, availability, systemRanges, new PortSnapshotDiffer(), new PortSearchEngine(), settings, fileMetadata);
        var performance = new PerformanceViewModel(collectorClient ?? new NullCollectorClient(), settings, fileMetadata);
        var diagnostic = new DiagnosticViewModel(engine, networkSnapshot, performanceTester, settings);
        var settingsVm = new SettingsViewModel(settingsStore, new StartupRegistration(), settings);
        var main = new MainViewModel(port, performance, diagnostic, settingsVm);

        _window = new MainWindow(main);
        MainWindow = _window;
        _window.Closing += (_, args) =>
        {
            if (!_allowExit && settingsVm.CloseToTray)
            {
                args.Cancel = true;
                _window.Hide();
                _tray?.ShowInfo("NetScope 已在后台运行");
                MemoryTrimmer.Trim();
            }
        };
        _window.Closed += (_, _) => { port.Dispose(); performance.Dispose(); fileMetadata.Dispose(); };

        if (!e.Args.Contains("--no-tray", StringComparer.OrdinalIgnoreCase))
        {
            _tray = new TrayService(
                show: ShowMainWindow,
                togglePause: () => port.IsPaused = !port.IsPaused,
                refresh: () => port.RefreshCommand.Execute(null),
                exit: ExitApplication);
        }

        _singleInstance.StartListening(args => Dispatcher.Invoke(ShowMainWindow));
        if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase)) ShowMainWindow();
        else _window.Hide();
        await port.StartAsync();
        await diagnostic.LoadSummaryAsync();
        await logger.WriteAsync("INFO", "NetScope started without telemetry");
        _ = Task.Run(async () => { await Task.Delay(3000); MemoryTrimmer.Trim(); });
    }

    private void ShowMainWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    private void ExitApplication()
    {
        _allowExit = true;
        _tray?.Dispose();
        _singleInstance?.Dispose();
        if (_collectorClient is not null) _ = _collectorClient.DisposeAsync();
        _window?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        if (_collectorClient is not null) _ = _collectorClient.DisposeAsync();
        base.OnExit(e);
    }
}
