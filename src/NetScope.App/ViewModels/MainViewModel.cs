using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NetScope.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public MainViewModel(PortViewModel port, PerformanceViewModel performance, DiagnosticViewModel diagnostic, SettingsViewModel settings)
    {
        Port = port;
        Performance = performance;
        Diagnostic = diagnostic;
        Settings = settings;
        CurrentPage = port;
        SelectedNavigation = "端口";
    }

    public PortViewModel Port { get; }
    public PerformanceViewModel Performance { get; }
    public DiagnosticViewModel Diagnostic { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private string _selectedNavigation;

    [RelayCommand] private void ShowPorts() { SwitchPage(Port, "端口"); }
    [RelayCommand] private void ShowPerformance() { SwitchPage(Performance, "性能"); }
    [RelayCommand] private void ShowDiagnostic() { SwitchPage(Diagnostic, "诊断"); }
    [RelayCommand] private void ShowSettings() { SwitchPage(Settings, "设置"); }

    private void SwitchPage(object page, string navigation)
    {
        if (ReferenceEquals(CurrentPage, Performance) && !ReferenceEquals(page, Performance))
            Performance.OnHidden();
        CurrentPage = page;
        SelectedNavigation = navigation;
        if (ReferenceEquals(page, Performance)) Performance.OnShown();
    }
}
