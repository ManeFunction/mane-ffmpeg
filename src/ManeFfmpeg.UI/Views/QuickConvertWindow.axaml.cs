using Avalonia.Controls;
using ManeFfmpeg.UI.ViewModels;

namespace ManeFfmpeg.UI.Views;

/// <summary>The progress window of a Finder Quick Action (see <see cref="QuickConvertViewModel"/>).</summary>
public partial class QuickConvertWindow : Window
{
    public QuickConvertWindow()
    {
        InitializeComponent();
    }

    public QuickConvertWindow(QuickConvertViewModel vm) : this()
    {
        DataContext = vm;
        var closing = false;
        vm.RequestClose = () =>
        {
            closing = true;
            Close();
        };
        // Closing the window cancels the conversion; it closes once ffmpeg has stopped and the partial file is gone.
        Closing += (_, e) =>
        {
            if (closing || !vm.IsRunning)
                return;
            e.Cancel = true;
            vm.Cancel();
        };
        Opened += async (_, _) => await vm.RunAsync();
    }
}
