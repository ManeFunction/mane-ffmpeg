using System.Globalization;

namespace ManeFfmpeg.Core.Ffmpeg;

/// <summary>A block of ffmpeg "-progress" output.</summary>
public readonly record struct FfmpegProgress(TimeSpan Position, double? Speed, bool Finished);

/// <summary>Turns "-progress pipe:1" key=value lines into snapshots, one per "progress=" line.</summary>
public sealed class ProgressParser
{
    private TimeSpan _position;
    private double? _speed;

    public event Action<FfmpegProgress>? Updated;

    public void Feed(string line)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0)
            return;
        var key = line.AsSpan(0, eq).Trim();
        var value = line.AsSpan(eq + 1).Trim();

        switch (key)
        {
            // out_time_ms is (historically) also microseconds.
            case "out_time_us" or "out_time_ms":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                    _position = TimeSpan.FromTicks(us * 10);
                break;
            case "speed":
                var number = value.TrimEnd('x');
                _speed = double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : null;
                break;
            case "progress":
                Updated?.Invoke(new FfmpegProgress(_position, _speed, value.SequenceEqual("end")));
                break;
        }
    }
}
