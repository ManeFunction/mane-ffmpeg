using System.Globalization;

namespace ManeFfmpeg.Core.Ffmpeg;

/// <summary>How a running export and its times are shown (the export bar, the Quick Action window, notifications).</summary>
public static class ExportText
{
    /// <summary>"42s", "3m 05s", "1h 02m".</summary>
    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:D2}s"
        : $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))}s";

    /// <summary>
    /// "Paused · 40% · Encoding · 2m 10s left · 1.5×": the progress once ffmpeg reports it, else what the export is
    /// doing before that (<paramref name="preparing"/>). Time left and speed are left out while paused.
    /// </summary>
    public static string Progress(ExportProgress? progress, bool paused, string? preparing, bool showStep = true)
    {
        var parts = new List<string>();
        if (paused)
            parts.Add("Paused");
        if (progress is { } p)
        {
            parts.Add($"{p.Fraction * 100:0}%");
            if (showStep)
                parts.Add(p.StepTitle);
            if (!paused && p.Remaining is { } left && p.Fraction > 0.01)
                parts.Add($"{Duration(left)} left");
            if (!paused && p.Speed is { } speed)
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{speed:0.0#}×"));
        }
        else if (preparing is not null)
            parts.Add(preparing);
        return string.Join(" · ", parts);
    }
}
