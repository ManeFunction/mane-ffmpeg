using Avalonia.Controls;
using ManeFfmpeg.App.Services;
using ManeFfmpeg.App.ViewModels;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.App.Views;

public partial class PresetsWindow : Window
{
    private bool _closeConfirmed;

    public PresetsWindow()
    {
        InitializeComponent();
        var vm = new PresetsViewModel();
        vm.AskAboutEdits = () => Dialogs.AskAsync(this, $"Save changes to “{vm.Selected?.Name}”?",
            "Your edits to this preset will be lost if you don't save them.", "Save", "Don't save", "Cancel");
        vm.ConfirmRestore = async () => await Dialogs.AskAsync(this, "Restore the built-in presets?",
            $"The built-in presets {string.Join(", ", Preset.BuiltIns.Select(p => $"“{p.Name}”"))} go back to their original settings, and deleted ones come back. Presets you added stay as they are.",
            "Restore", "Cancel") == 0;
        DataContext = vm;
        // After the user keeps editing, the list's two-way binding doesn't follow the view model back; set it here.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PresetsViewModel.ListSelection) && !ReferenceEquals(PresetList.SelectedItem, vm.ListSelection))
                PresetList.SelectedItem = vm.ListSelection;
        };
        EditMenu.Attach(this);
        this.CloseOnEscape();
        Closing += async (_, e) =>
        {
            if (_closeConfirmed || !vm.IsModified)
                return;
            e.Cancel = true;
            if (!await vm.ResolveEditsAsync())
                return;
            _closeConfirmed = true;
            Close();
        };
    }
}
