using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Immutable;
using NetScope.Core.Abstractions;
using NetScope.Core.Models;
using NetScope.Windows.Settings;
using NetScope.App.Services;

namespace NetScope.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly StartupRegistration _startup;

    public SettingsViewModel(ISettingsStore store, StartupRegistration startup, AppSettings settings)
    {
        _store = store;
        _startup = startup;
        Theme = settings.Theme;
        ForegroundRefreshMilliseconds = settings.ForegroundRefreshMilliseconds;
        TrayRefreshMilliseconds = settings.TrayRefreshMilliseconds;
        CloseToTray = settings.CloseToTray;
        StartWithWindows = startup.IsEnabled;
        TargetsText = string.Join(Environment.NewLine, settings.DiagnosticTargets.Select(x => x.Host));
        HistoryEnabled = settings.HistoryEnabled;
        HistoryRetentionDays = settings.HistoryRetentionDays;
        BackgroundRecording = settings.BackgroundRecording;
    }

    [ObservableProperty] private AppTheme _theme;
    [ObservableProperty] private int _foregroundRefreshMilliseconds;
    [ObservableProperty] private int _trayRefreshMilliseconds;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _targetsText;
    [ObservableProperty] private bool _historyEnabled;
    [ObservableProperty] private int _historyRetentionDays;
    [ObservableProperty] private bool _backgroundRecording;
    [ObservableProperty] private string _saveStatus = "设置保存在本机，不包含遥测";

    [RelayCommand]
    private async Task SaveAsync()
    {
        var targets = TargetsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).Select((host, index) => new DiagnosticTarget($"目标 {index + 1}", host)).ToImmutableArray();
        var settings = new AppSettings
        {
            Theme = Theme,
            ForegroundRefreshMilliseconds = ForegroundRefreshMilliseconds,
            TrayRefreshMilliseconds = TrayRefreshMilliseconds,
            CloseToTray = CloseToTray,
            StartWithWindows = StartWithWindows,
            DiagnosticTargets = targets,
            HistoryEnabled = HistoryEnabled,
            HistoryRetentionDays = HistoryRetentionDays,
            BackgroundRecording = BackgroundRecording,
            TelemetryEnabled = false
        }.Normalize();
        await _store.SaveAsync(settings);
        ThemePalette.Apply(settings.Theme);
        if (Environment.ProcessPath is { } path) _startup.SetEnabled(StartWithWindows, path);
        SaveStatus = $"已保存 · {DateTime.Now:HH:mm:ss}";
    }
}
