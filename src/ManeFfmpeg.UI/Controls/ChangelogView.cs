using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ManeFfmpeg.UI.Controls;

/// <summary>
/// Shows CHANGELOG.md sections: release and group headings, bullet lists (wrapped and nested), **bold**, *italic*
/// and `code`. Links show their text only. That's all the Markdown the changelog uses.
/// </summary>
public sealed partial class ChangelogView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<ChangelogView, string?>(nameof(Markdown));

    private static readonly FontFamily CodeFont = new("Menlo, DejaVu Sans Mono, monospace");

    public ChangelogView() => Spacing = 4;

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
            Build(Markdown ?? "");
    }

    private void Build(string markdown)
    {
        Children.Clear();
        SelectableTextBlock? bullet = null;
        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                bullet = null;
                continue;
            }
            var indent = line.Length - line.TrimStart().Length;
            var text = line.Trim();
            if (text.StartsWith("## ", StringComparison.Ordinal))
            {
                bullet = null;
                var block = Text(ReleaseTitle(text[3..]), 15, FontWeight.SemiBold);
                block.Margin = new Thickness(0, Children.Count == 0 ? 0 : 12, 0, 0);
                Children.Add(block);
            }
            else if (text.StartsWith("### ", StringComparison.Ordinal))
            {
                bullet = null;
                var block = Text(text[4..], 13, FontWeight.SemiBold);
                block.Opacity = 0.75;
                block.Margin = new Thickness(0, 4, 0, 0);
                Children.Add(block);
            }
            else if (text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal))
            {
                bullet = Text(text[2..]);
                var level = indent / 2;
                var row = new DockPanel { Margin = new Thickness(4 + level * 16, 0, 0, 0) };
                var mark = new TextBlock { Text = level == 0 ? "•" : "◦", Width = 14, FontSize = 13 };
                DockPanel.SetDock(mark, Dock.Left);
                row.Children.Add(mark);
                row.Children.Add(bullet);
                Children.Add(row);
            }
            else if (bullet is not null && indent > 0)
            {
                // A wrapped bullet continues on an indented line.
                bullet.Inlines!.Add(new Run(" "));
                AddInlines(bullet, text);
            }
            else
            {
                bullet = null;
                Children.Add(Text(text));
            }
        }
    }

    /// <summary>"[0.9.1] - 2026-11-02" → "0.9.1 · 2026-11-02".</summary>
    private static string ReleaseTitle(string heading)
    {
        var parts = heading.Replace("[", "").Replace("]", "").Split(" - ", 2);
        return parts.Length == 2 ? $"{parts[0]} · {parts[1]}" : parts[0];
    }

    private static SelectableTextBlock Text(string markdown, double size = 13, FontWeight weight = FontWeight.Normal)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = weight };
        AddInlines(block, markdown);
        return block;
    }

    private static void AddInlines(SelectableTextBlock block, string markdown)
    {
        var inlines = block.Inlines!;
        var at = 0;
        foreach (Match m in InlineMarkup().Matches(markdown))
        {
            if (m.Index > at)
                inlines.Add(new Run(markdown[at..m.Index]));
            if (m.Groups["bold"].Success)
                inlines.Add(new Run(m.Groups["bold"].Value) { FontWeight = FontWeight.SemiBold });
            else if (m.Groups["italic"].Success)
                inlines.Add(new Run(m.Groups["italic"].Value) { FontStyle = FontStyle.Italic });
            else if (m.Groups["code"].Success)
                inlines.Add(new Run(m.Groups["code"].Value) { FontFamily = CodeFont, FontSize = block.FontSize - 1 });
            else
                inlines.Add(new Run(m.Groups["link"].Value));
            at = m.Index + m.Length;
        }
        if (at < markdown.Length)
            inlines.Add(new Run(markdown[at..]));
    }

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|`(?<code>[^`]+)`|(?<![\w*])\*(?<italic>[^*\s][^*]*?)\*(?![\w*])|\[(?<link>[^\]]+)\]\([^)]*\)")]
    private static partial Regex InlineMarkup();
}
