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
        void ShowUpdateBusy() => vm.UpdateCheckBusy = Updater.Instance.IsBusy;
        Updater.Instance.BusyChanged += ShowUpdateBusy;
        ShowUpdateBusy();
        Closed += (_, _) =>
        {
            Updater.Instance.BusyChanged -= ShowUpdateBusy;
            vm.DiscardThemePreview();
        };
    }

    /// <summary>The same check as the menu's "Check for Updates…"; the update window opens over Settings.</summary>
    private void OnCheckNowClicked(object? sender, RoutedEventArgs e) =>
        _ = Updater.Instance.CheckAndOfferAsync(manual: true, owner: this);

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
