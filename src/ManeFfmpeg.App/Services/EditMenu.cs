using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;

namespace ManeFfmpeg.App.Services;

/// <summary>
/// The standard Edit menu (Undo, Redo, Cut, Copy, Paste, Select All). On macOS the clipboard shortcuts must
/// exist as menu key equivalents, otherwise Cmd+C/V/X never reach text fields. Each item acts on the focused
/// text field; Undo, Redo and Select All fall back to the given window actions when no text field has focus.
/// </summary>
public static class EditMenu
{
    public static KeyModifiers CommandModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    public static NativeMenu Create(Window window, Action? undo = null, Action? redo = null, Action? selectAll = null)
    {
        var cmd = CommandModifier;
        return new NativeMenu
        {
            Item("Undo", new KeyGesture(Key.Z, cmd), () =>
            {
                if (Focused(window) is TextBox box)
                    box.Undo();
                else
                    undo?.Invoke();
            }),
            Item("Redo", new KeyGesture(Key.Z, cmd | KeyModifiers.Shift), () =>
            {
                if (Focused(window) is TextBox box)
                    box.Redo();
                else
                    redo?.Invoke();
            }),
            new NativeMenuItemSeparator(),
            Item("Cut", new KeyGesture(Key.X, cmd), () =>
            {
                if (Focused(window) is TextBox { IsReadOnly: false } box)
                    box.Cut();
                else if (Focused(window) is TextBox readOnly)
                    readOnly.Copy();
            }),
            Item("Copy", new KeyGesture(Key.C, cmd), () =>
            {
                switch (Focused(window))
                {
                    case TextBox box:
                        box.Copy();
                        break;
                    case SelectableTextBlock block:
                        block.Copy();
                        break;
                }
            }),
            Item("Paste", new KeyGesture(Key.V, cmd), () =>
            {
                if (Focused(window) is TextBox { IsReadOnly: false } box)
                    box.Paste();
            }),
            Item("Select All", new KeyGesture(Key.A, cmd), () =>
            {
                switch (Focused(window))
                {
                    case TextBox box:
                        box.SelectAll();
                        break;
                    case SelectableTextBlock block:
                        block.SelectAll();
                        break;
                    default:
                        selectAll?.Invoke();
                        break;
                }
            }),
        };
    }

    /// <summary>Gives a dialog window an Edit menu of its own (shown in the macOS menu bar while it's active).</summary>
    public static void Attach(Window window)
    {
        NativeMenu.SetMenu(window, new NativeMenu { new NativeMenuItem("Edit") { Menu = Create(window) } });
        BindShortcuts(window);
    }

    /// <summary>
    /// Outside macOS the menu is drawn inside the window, and Avalonia shows the items' shortcuts there without
    /// acting on them. Binds each item's shortcut to its command on the window, after the menu is built. Keys a
    /// text field handles itself (Ctrl+C in a text box) never reach these bindings.
    /// </summary>
    public static void BindShortcuts(Window window)
    {
        if (OperatingSystem.IsMacOS() || NativeMenu.GetMenu(window) is not { } menu)
            return;
        foreach (var item in Items(menu))
            if (item is { Gesture: { } gesture, Command: { } command })
                window.KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = command });
    }

    private static IEnumerable<NativeMenuItem> Items(NativeMenu menu) =>
        menu.Items.OfType<NativeMenuItem>().SelectMany(item => item.Menu is { } sub ? Items(sub).Prepend(item) : [item]);

    private static object? Focused(Window window) => window.FocusManager?.GetFocusedElement();

    private static NativeMenuItem Item(string header, KeyGesture gesture, Action action) =>
        new(header) { Gesture = gesture, Command = new RelayCommand(action) };
}
