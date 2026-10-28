using Avalonia.Controls;
using Avalonia.Threading;
using ManeFfmpeg.UI.Services;
using ManeFfmpeg.UI.ViewModels;

namespace ManeFfmpeg.UI.Views;

public partial class SetupWindow : Window
{
    private bool _closeConfirmed;

    public SetupWindow() : this(new SetupViewModel())
    {
    }

    public SetupWindow(SetupViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose = Close;
        // The log follows brew's output.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SetupViewModel.Log))
                Dispatcher.UIThread.Post(() => LogBox.CaretIndex = LogBox.Text?.Length ?? 0, DispatcherPriority.Background);
        };
        EditMenu.Attach(this);
        this.CloseOnEscape();
        Closing += async (_, e) =>
        {
            if (_closeConfirmed || !vm.IsInstalling)
                return;
            e.Cancel = true;
            if (await Dialogs.AskAsync(this, "Stop installing?",
                    "Homebrew is still installing. You can install again later from Settings.", "Stop", "Keep Installing") != 0)
                return;
            _closeConfirmed = true;
            Close();
        };
        Closed += (_, _) => vm.Dispose();
    }
}
