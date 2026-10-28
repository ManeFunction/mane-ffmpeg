using Avalonia.Controls;
using Avalonia.Interactivity;
using ManeFfmpeg.UI.Services;
using ManeFfmpeg.UI.ViewModels;

namespace ManeFfmpeg.UI.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        var vm = new SettingsViewModel();
        DataContext = vm;
        EditMenu.Attach(this);
        this.CloseOnEscape();
        Closed += (_, _) => vm.DiscardThemePreview();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close();

    private async void OnApplyClicked(object? sender, RoutedEventArgs e)
    {
        await ((SettingsViewModel)DataContext!).ApplyAsync();
        Close();
    }

    private async void OnInstallClicked(object? sender, RoutedEventArgs e)
    {
        await new SetupWindow().ShowDialog(this);
        ((SettingsViewModel)DataContext!).RefreshStatus();
    }
}
