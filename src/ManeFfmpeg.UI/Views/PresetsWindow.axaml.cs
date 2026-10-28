using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ManeFfmpeg.UI.Services;
using ManeFfmpeg.UI.ViewModels;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.UI.Views;

public partial class PresetsWindow : Window
{
    /// <summary>How far the pointer moves before a press on a preset becomes a drag.</summary>
    private const double DragThreshold = 4;

    private readonly PresetsViewModel _vm;
    private bool _closeConfirmed;

    private Preset? _pressed;
    private Point _pressPoint;
    private Control? _dragged;

    /// <summary>While dragging: the gap the preset would drop into (0 = above the first preset).</summary>
    private int? _dropSlot;

    public PresetsWindow()
    {
        InitializeComponent();
        var vm = _vm = new PresetsViewModel();
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
        // The list handles presses itself (to select), so listen to handled events too.
        PresetList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        PresetList.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        PresetList.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        PresetList.PointerCaptureLost += (_, _) => EndDrag();
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

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PresetList).Properties.IsLeftButtonPressed)
            return;
        _pressed = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as Preset;
        _pressPoint = e.GetPosition(PresetList);
    }

    private void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is null)
            return;
        var point = e.GetPosition(PresetList);
        if (_dropSlot is null)
        {
            var moved = point - _pressPoint;
            if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold)
                return;
            // Only the selected preset is dragged: pressing another one with unsaved edits asks about them
            // first, and the selection stays where it was until the user answers.
            if (_pressed.Id != _vm.Selected?.Id)
            {
                _pressed = null;
                return;
            }
            e.Pointer.Capture(PresetList);
            _dragged = PresetList.ContainerFromItem(_vm.Selected);
            if (_dragged is not null)
                _dragged.Opacity = 0.5;
        }
        _dropSlot = DropSlotAt(point.Y);
        ShowDropLine(_dropSlot.Value);
        e.Handled = true;
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressed = null;
        if (_dropSlot is not { } slot || _vm.Selected is not { } preset)
            return;
        var from = _vm.Presets.IndexOf(preset);
        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
        // The gaps around the dragged preset itself leave it where it is.
        var to = slot > from ? slot - 1 : slot;
        if (to != from)
            _vm.MoveSelectedTo(to);
    }

    private void EndDrag()
    {
        _dropSlot = null;
        _pressed = null;
        DropLine.IsVisible = false;
        if (_dragged is not null)
            _dragged.Opacity = 1;
        _dragged = null;
    }

    /// <summary>The gap nearest to <paramref name="y"/> (in list coordinates): past an item's middle means below it.</summary>
    private int DropSlotAt(double y)
    {
        var slot = 0;
        for (var i = 0; i < _vm.Presets.Count; i++)
        {
            if (PresetList.ContainerFromIndex(i) is { } item
                && item.TranslatePoint(new Point(0, item.Bounds.Height / 2), PresetList) is { } middle && y > middle.Y)
                slot = i + 1;
        }
        return slot;
    }

    private void ShowDropLine(int slot)
    {
        var overlay = (Visual)DropLine.GetVisualParent()!;
        var last = _vm.Presets.Count - 1;
        var y = slot <= last
            ? PresetList.ContainerFromIndex(slot)?.TranslatePoint(default, overlay)?.Y
            : PresetList.ContainerFromIndex(last) is { } item ? item.TranslatePoint(new Point(0, item.Bounds.Height), overlay)?.Y : null;
        if (y is not { } top)
            return;
        DropLine.Margin = new Thickness(6, Math.Max(0, top - DropLine.Height / 2), 6, 0);
        DropLine.IsVisible = true;
    }
}
