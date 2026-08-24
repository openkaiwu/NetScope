using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NetScope.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public MainViewModel(PortViewModel port, DiagnosticViewModel diagnostic, SettingsViewModel settings)
    {
        Port = port;
        Diagnostic = diagnostic;
        Settings = settings;
        CurrentPage = port;
        SelectedNavigation = "端口";
    }

    public PortViewModel Port { get; }
    public DiagnosticViewModel Diagnostic { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private object _currentPage;
    [ObservableProperty] private string _selectedNavigation;

    [RelayCommand] private void ShowPorts() { CurrentPage = Port; SelectedNavigation = "端口"; }
    [RelayCommand] private void ShowDiagnostic() { CurrentPage = Diagnostic; SelectedNavigation = "诊断"; }
    [RelayCommand] private void ShowSettings() { CurrentPage = Settings; SelectedNavigation = "设置"; }
}
