using Avalonia.Controls;
using Avalonia.Input;

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
    public static void Attach(Window window) =>
        NativeMenu.SetMenu(window, new NativeMenu { new NativeMenuItem("Edit") { Menu = Create(window) } });

    private static object? Focused(Window window) => window.FocusManager?.GetFocusedElement();

    private static NativeMenuItem Item(string header, KeyGesture gesture, Action action)
    {
        var item = new NativeMenuItem(header) { Gesture = gesture };
        item.Click += (_, _) => action();
        return item;
    }
}
