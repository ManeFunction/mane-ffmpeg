using System.Text.RegularExpressions;

namespace ManeFfmpeg.Core.Updates;

/// <summary>Reads CHANGELOG.md, written in the Keep a Changelog format (<c>## [1.2.0] - 2026-10-26</c> per release).</summary>
public static partial class Changelog
{
    /// <summary>
    /// The release sections newer than <paramref name="after"/> up to and including <paramref name="upTo"/>, newest
    /// first as in the file, or null when there are none. "Unreleased" and the link references at the end are left out.
    /// </summary>
    public static string? Between(string markdown, Version after, Version upTo)
    {
        var lines = new List<string>();
        var taking = false;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var version = ReleaseHeading().Match(line) is { Success: true } m ? AppVersion.Parse(m.Groups[1].Value) : null;
                taking = version is not null && version > after && version <= upTo;
            }
            if (taking && !LinkReference().IsMatch(line))
                lines.Add(line.TrimEnd());
        }
        var text = string.Join('\n', lines).Trim('\n');
        return text.Length > 0 ? text : null;
    }

    [GeneratedRegex(@"^##\s+\[?v?([0-9][^\]\s]*)\]?")]
    private static partial Regex ReleaseHeading();

    [GeneratedRegex(@"^\[[^\]]+\]:\s")]
    private static partial Regex LinkReference();
}
