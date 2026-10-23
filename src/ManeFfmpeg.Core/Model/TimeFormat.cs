using System.Globalization;

namespace ManeFfmpeg.Core.Model;

/// <summary>Time helpers shared by command builders and the UI.</summary>
public static class TimeFormat
{
    /// <summary>
    /// Seconds for ffmpeg/mpv arguments: invariant culture, floored to whole microseconds so
    /// a cut placed exactly on a frame boundary never pulls in the neighbouring frame.
    /// </summary>
    public static string Seconds(TimeSpan time)
    {
        var micros = time.Ticks / 10;
        if (time.Ticks < 0 && time.Ticks % 10 != 0)
            micros--;
        var sign = micros < 0 ? "-" : "";
        micros = Math.Abs(micros);
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{micros / 1_000_000}.{micros % 1_000_000:D6}");
    }

    public static TimeSpan FromSeconds(double seconds) =>
        TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));

    /// <summary>"1:02:03.456" / "02:03.456" style display timecode.</summary>
    public static string Display(TimeSpan time, bool forceHours = false)
    {
        var sign = time < TimeSpan.Zero ? "-" : "";
        time = time.Duration();
        var ms = (long)Math.Floor(time.TotalMilliseconds);
        var h = ms / 3_600_000;
        var m = ms / 60_000 % 60;
        var s = ms / 1000 % 60;
        var f = ms % 1000;
        return h > 0 || forceHours
            ? string.Create(CultureInfo.InvariantCulture, $"{sign}{h}:{m:D2}:{s:D2}.{f:D3}")
            : string.Create(CultureInfo.InvariantCulture, $"{sign}{m:D2}:{s:D2}.{f:D3}");
    }

    /// <summary>Signed milliseconds for offsets, e.g. "+120 ms".</summary>
    public static string Offset(TimeSpan offset)
    {
        var ms = offset.TotalMilliseconds;
        return ms switch
        {
            > 0 => string.Create(CultureInfo.InvariantCulture, $"+{ms:0.#} ms"),
            < 0 => string.Create(CultureInfo.InvariantCulture, $"−{-ms:0.#} ms"),
            _ => "0 ms",
        };
    }

    /// <summary>Parses "1:02:03.5", "02:03.5", "123.5" or "123,5".</summary>
    public static bool TryParse(string? text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        text = text.Trim().Replace(',', '.');
        var negative = text.StartsWith('-') || text.StartsWith('−');
        if (negative)
            text = text[1..];
        var parts = text.Split(':');
        if (parts.Length > 3)
            return false;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
                return false;
            total = total * 60 + value;
        }
        time = FromSeconds(negative ? -total : total);
        return true;
    }

    /// <summary>Rounds a time to the nearest frame of the given rate; frames start at <paramref name="phase"/>.</summary>
    public static TimeSpan SnapToFrame(TimeSpan time, Rational frameRate, TimeSpan phase = default)
    {
        if (!frameRate.IsValid)
            return time;
        var frame = Math.Round((time - phase).Ticks * (double)frameRate.Num / (frameRate.Den * (double)TimeSpan.TicksPerSecond));
        var snapped = FrameStart((long)frame, frameRate) + phase;
        return snapped < TimeSpan.Zero ? TimeSpan.Zero : snapped;
    }

    /// <summary>Start time of frame <paramref name="frame"/>, rounded up to whole ticks.</summary>
    public static TimeSpan FrameStart(long frame, Rational frameRate)
    {
        var ticks = (decimal)frame * frameRate.Den * TimeSpan.TicksPerSecond / frameRate.Num;
        return TimeSpan.FromTicks((long)Math.Ceiling(ticks));
    }

    public static TimeSpan FrameDuration(Rational frameRate) =>
        frameRate.IsValid ? TimeSpan.FromTicks(TimeSpan.TicksPerSecond * frameRate.Den / frameRate.Num) : TimeSpan.FromMilliseconds(40);
}
