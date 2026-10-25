using System.Collections.Concurrent;
using System.Globalization;
using CliWrap;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Media;

/// <summary>
/// Keyframe times (0-based) of a video stream, plus its reorder delay: how much earlier than its display
/// time a frame is decoded (B-frames). Stream-copy cuts need both.
/// </summary>
public sealed record KeyframeIndex(IReadOnlyList<TimeSpan> Times, TimeSpan ReorderDelay);

/// <summary>Keyframe (seek point) times of a file's video stream, from ffprobe packet flags. Cached per file version.</summary>
public sealed class KeyframeService(FfmpegTools tools)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<KeyframeIndex>>> _cache = new();

    /// <summary>The keyframes of <paramref name="path"/> as probed in <paramref name="media"/>, if already scanned.</summary>
    public KeyframeIndex? TryGet(string path, MediaInfo media) =>
        _cache.TryGetValue(Key(path, media), out var lazy) && lazy.Value.IsCompletedSuccessfully ? lazy.Value.Result : null;

    public Task<KeyframeIndex> GetAsync(string path, MediaInfo media)
    {
        var key = Key(path, media);
        var lazy = _cache.GetOrAdd(key, _ => new Lazy<Task<KeyframeIndex>>(() => ScanAsync(path, media)));
        var task = lazy.Value;
        if (task.IsFaulted || task.IsCanceled)
            _cache.TryRemove(key, out _);
        return task;
    }

    // A file replaced in place gets a new stamp when probed again, so it is scanned again.
    private static string Key(string path, MediaInfo media) => $"{path}\n{media.Stamp}";

    private async Task<KeyframeIndex> ScanAsync(string path, MediaInfo media)
    {
        var video = media.Video ?? throw new InvalidOperationException("No video stream.");
        var result = new List<TimeSpan>();
        TimeSpan? delay = null;
        await Cli.Wrap(tools.Ffprobe)
            .WithArguments(["-v", "error", "-select_streams", video.Index.ToString(CultureInfo.InvariantCulture),
                "-show_entries", "packet=pts_time,dts_time,flags", "-of", "csv=p=0", path])
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
            {
                // "pts_time,dts_time,flags"
                var parts = line.Split(',');
                if (parts.Length < 3 || !parts[2].Contains('K')
                    || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pts))
                    return;
                result.Add(TimeFormat.FromSeconds(pts) - media.StartTime);
                if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dts))
                {
                    var d = TimeFormat.FromSeconds(pts - dts);
                    if (d >= TimeSpan.Zero && (delay is null || d < delay))
                        delay = d;
                }
            }))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync();
        result.Sort();
        if (result.Count == 0)
            result.Add(TimeSpan.Zero);
        return new KeyframeIndex(result, delay ?? TimeSpan.Zero);
    }
}
