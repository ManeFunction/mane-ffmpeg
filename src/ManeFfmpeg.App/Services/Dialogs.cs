using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ManeFfmpeg.App.Services;

/// <summary>Small modal message boxes, built in code (Avalonia has no stock message box).</summary>
public static class Dialogs
{
    /// <summary>
    /// Makes Esc close the window, like Cancel. Handled on bubbling, so an open dropdown inside the window
    /// closes first and the next Esc closes the window.
    /// </summary>
    public static void CloseOnEscape(this Window window) =>
        window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || e.Handled)
                return;
            e.Handled = true;
            window.Close();
        };

    /// <summary>Shows a message with buttons; returns the index of the pressed button, or -1 if closed.</summary>
    public static async Task<int> AskAsync(Window owner, string title, string message, params string[] buttons)
    {
        var result = -1;
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        // Esc closes like the window's close button: the result stays -1 (cancel).
        dialog.CloseOnEscape();
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var button = new Button { Content = buttons[i], MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == 0)
            {
                button.IsDefault = true;
                button.Classes.Add("accent");
            }
            if (i == buttons.Length - 1 && buttons.Length > 1)
                button.IsCancel = true;
            button.Click += (_, _) =>
            {
                result = index;
                dialog.Close();
            };
            buttonRow.Children.Add(button);
        }
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 },
                buttonRow,
            },
        };
        await dialog.ShowDialog(owner);
        return result;
    }

    public static Task InfoAsync(Window owner, string title, string message) => AskAsync(owner, title, message, "OK");
}
