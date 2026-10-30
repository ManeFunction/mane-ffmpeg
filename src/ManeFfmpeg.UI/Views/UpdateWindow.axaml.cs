using Avalonia.Controls;
using ManeFfmpeg.Core.Persistence;
using ManeFfmpeg.UI.Services;
using ManeFfmpeg.UI.ViewModels;

namespace ManeFfmpeg.UI.Views;

public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        InitializeComponent();
    }

    public UpdateWindow(UpdateViewModel vm) : this()
    {
        DataContext = vm;
        vm.RequestClose = Close;
        vm.OpenPage = async uri =>
        {
            try { await Launcher.LaunchUriAsync(uri); }
            catch (Exception ex) { AppLog.Write($"Couldn't open {uri}: {ex.Message}"); }
        };
        EditMenu.Attach(this);
        this.CloseOnEscape();
        Closed += (_, _) => vm.Dispose();
    }
}
