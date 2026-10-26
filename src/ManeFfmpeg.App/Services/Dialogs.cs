using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ManeFfmpeg.Core.Persistence;

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
    public static Task<int> AskAsync(Window owner, string title, string message, params string[] buttons) =>
        AskAsync(owner, title, message, null, buttons);

    /// <summary>Same, with an extra control (e.g. a link) between the message and the buttons.</summary>
    public static async Task<int> AskAsync(Window owner, string title, string message, Control? extra, params string[] buttons)
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
        var content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 },
            },
        };
        if (extra != null)
            content.Children.Add(extra);
        content.Children.Add(buttonRow);
        dialog.Content = content;
        await dialog.ShowDialog(owner);
        return result;
    }

    public static Task InfoAsync(Window owner, string title, string message) => AskAsync(owner, title, message, "OK");

    /// <summary>An info box with a clickable link under the message.</summary>
    public static Task InfoAsync(Window owner, string title, string message, string linkText, string url) =>
        AskAsync(owner, title, message, Link(owner, linkText, url), "OK");

    /// <summary>A flat, underlined button that opens <paramref name="url"/> in the default browser.</summary>
    private static Button Link(Window owner, string text, string url)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, TextDecorations = TextDecorations.Underline, Foreground = Brushes.CornflowerBlue },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(button, url);
        button.Click += async (_, _) =>
        {
            try { await owner.Launcher.LaunchUriAsync(new Uri(url)); }
            catch (Exception ex) { AppLog.Write($"Couldn't open {url}: {ex.Message}"); }
        };
        return button;
    }
}
