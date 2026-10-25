using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CliWrap;
using CliWrap.Buffered;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Ffmpeg;

public sealed class FfprobeException(string message) : Exception(message);

/// <summary>Reads stream and format information with ffprobe.</summary>
public sealed class Ffprobe(FfmpegTools tools)
{
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        // Stamped before probing: a file changing meanwhile gets probed again next time.
        var stamp = FileStamp.Of(path);
        var result = await Cli.Wrap(tools.Ffprobe)
            .WithArguments(["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        if (result.ExitCode != 0)
            throw new FfprobeException(FirstLine(result.StandardError) ?? $"ffprobe exited with code {result.ExitCode}");
        return Parse(result.StandardOutput) with { Stamp = stamp };
    }

    /// <summary>Parses ffprobe's JSON output (separate for unit tests).</summary>
    public static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("format", out var format))
            throw new FfprobeException("Not a media file.");

        VideoStreamInfo? video = null;
        var audio = ImmutableArray.CreateBuilder<AudioStreamInfo>();
        TimeSpan streamDuration = TimeSpan.Zero;

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "codec_type");
                var index = s.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
                var duration = Seconds(Str(s, "duration"));
                if (duration > streamDuration)
                    streamDuration = duration;

                if (type == "video" && video is null && !IsAttachedPicture(s))
                {
                    var r = Rational.Parse(Str(s, "r_frame_rate"));
                    var avg = Rational.Parse(Str(s, "avg_frame_rate"));
                    var rate = r.IsValid && r.Value is >= 1 and <= 240 ? r : avg.IsValid ? avg : new Rational(30, 1);
                    var sar = Rational.Parse(Str(s, "sample_aspect_ratio"));
                    video = new VideoStreamInfo
                    {
                        Index = index,
                        Codec = Str(s, "codec_name") ?? "unknown",
                        Profile = Str(s, "profile"),
                        Width = Int(s, "width"),
                        Height = Int(s, "height"),
                        FrameRate = rate,
                        PixelFormat = Str(s, "pix_fmt") ?? "",
                        SampleAspectRatio = sar.IsValid ? sar : new Rational(1, 1),
                        Rotation = Rotation(s),
                        StartOffset = SignedSeconds(Str(s, "start_time")),
                    };
                }
                else if (type == "audio")
                {
                    var tags = s.TryGetProperty("tags", out var t) ? t : default;
                    audio.Add(new AudioStreamInfo
                    {
                        Index = index,
                        Codec = Str(s, "codec_name") ?? "unknown",
                        SampleRate = int.TryParse(Str(s, "sample_rate"), out var sr) ? sr : 48000,
                        Channels = Int(s, "channels"),
                        ChannelLayout = Str(s, "channel_layout") ?? "",
                        Language = tags.ValueKind == JsonValueKind.Object ? Str(tags, "language") : null,
                        Title = tags.ValueKind == JsonValueKind.Object ? Str(tags, "title") : null,
                        Duration = duration > TimeSpan.Zero ? duration : null,
                    });
                }
            }
        }

        var formatDuration = Seconds(Str(format, "duration"));
        var startTime = SignedSeconds(Str(format, "start_time"));
        if (video is not null)
            video = video with { StartOffset = video.StartOffset - startTime < TimeSpan.Zero ? TimeSpan.Zero : video.StartOffset - startTime };
        return new MediaInfo
        {
            Duration = formatDuration > TimeSpan.Zero ? formatDuration : streamDuration,
            StartTime = startTime,
            FormatName = Str(format, "format_name") ?? "",
            Video = video,
            AudioStreams = audio.ToImmutable(),
        };
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var d)
        && d.TryGetProperty("attached_pic", out var a)
        && a.ValueKind == JsonValueKind.Number && a.GetInt32() == 1;

    private static int Rotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var list))
            foreach (var item in list.EnumerateArray())
                if (item.TryGetProperty("rotation", out var rot) && rot.ValueKind == JsonValueKind.Number)
                    return (int)Math.Round(rot.GetDouble());
        if (stream.TryGetProperty("tags", out var tags) && int.TryParse(Str(tags, "rotate"), out var tagRotation))
            return tagRotation;
        return 0;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static TimeSpan Seconds(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0
            ? TimeFormat.FromSeconds(s)
            : TimeSpan.Zero;

    private static TimeSpan SignedSeconds(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? TimeFormat.FromSeconds(s) : TimeSpan.Zero;

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
}
