using Avalonia.Controls;
using Avalonia.Interactivity;
using ManeFfmpeg.App.ViewModels;

namespace ManeFfmpeg.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = new SettingsViewModel();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
