using Avalonia.Controls;
using ManeFfmpeg.App.Services;
using ManeFfmpeg.App.ViewModels;

namespace ManeFfmpeg.App.Views;

public partial class PresetsWindow : Window
{
    public PresetsWindow()
    {
        InitializeComponent();
        DataContext = new PresetsViewModel();
        EditMenu.Attach(this);
        this.CloseOnEscape();
    }
}
