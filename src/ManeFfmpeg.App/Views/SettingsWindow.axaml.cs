using Avalonia.Controls;
using Avalonia.Interactivity;
using ManeFfmpeg.App.Services;
using ManeFfmpeg.App.ViewModels;

namespace ManeFfmpeg.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = new SettingsViewModel();
        EditMenu.Attach(this);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
