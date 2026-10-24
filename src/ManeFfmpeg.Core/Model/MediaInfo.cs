using System.Collections.Immutable;

namespace ManeFfmpeg.Core.Model;

/// <summary>What ffprobe told us about a source file. Times are 0-based from the file start.</summary>
public sealed record MediaInfo
{
    public required TimeSpan Duration { get; init; }

    /// <summary>Container start_time; file timestamps = 0-based time + StartTime.</summary>
    public TimeSpan StartTime { get; init; }

    public string FormatName { get; init; } = "";

    public VideoStreamInfo? Video { get; init; }

    public ImmutableArray<AudioStreamInfo> AudioStreams { get; init; } = [];

    public AudioStreamInfo? Audio(int audioIndex) =>
        audioIndex >= 0 && audioIndex < AudioStreams.Length ? AudioStreams[audioIndex] : null;

    /// <summary>Where <paramref name="stream"/> ends; the container duration when the stream has none of its own.</summary>
    public TimeSpan AudioEnd(AudioStreamInfo stream) => stream.Duration ?? Duration;

    /// <summary>
    /// Containers whose timestamp seeking can land after the wanted keyframe (MPEG-TS/PS, AVI, FLV, raw
    /// streams). Exports seek a few seconds early there and trim precisely in the filter graph.
    /// </summary>
    public bool HasUnreliableSeeking =>
        FormatName.Split(',').Any(f => f is "mpegts" or "mpeg" or "mpegvideo" or "avi" or "flv" or "asf" or "h264" or "hevc" or "m4v");
}

public sealed record VideoStreamInfo
{
    /// <summary>Absolute stream index in the file (for "-map 0:N").</summary>
    public required int Index { get; init; }
    public required string Codec { get; init; }
    public string? Profile { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required Rational FrameRate { get; init; }
    public string PixelFormat { get; init; } = "";
    public Rational SampleAspectRatio { get; init; } = new(1, 1);

    /// <summary>When the first video frame starts, relative to the container start (0-based time).</summary>
    public TimeSpan StartOffset { get; init; }

    /// <summary>Display rotation in degrees (from the display matrix), e.g. 90 for portrait phone video.</summary>
    public int Rotation { get; init; }

    private bool Rotated => Math.Abs(Rotation) % 180 == 90;

    private int SarWidth => SampleAspectRatio.IsValid && SampleAspectRatio.Num != SampleAspectRatio.Den
        ? (int)Math.Round(Width * SampleAspectRatio.Value)
        : Width;

    /// <summary>Width as shown after square-pixel correction and rotation, rounded to even.</summary>
    public int DisplayWidth => Even(Rotated ? Height : SarWidth);

    public int DisplayHeight => Even(Rotated ? SarWidth : Height);

    private static int Even(int value) => Math.Max(2, value / 2 * 2);
}

public sealed record AudioStreamInfo
{
    /// <summary>Absolute stream index in the file.</summary>
    public required int Index { get; init; }
    public required string Codec { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public string ChannelLayout { get; init; } = "";
    public string? Language { get; init; }
    public string? Title { get; init; }
    public TimeSpan? Duration { get; init; }

    /// <summary>A layout name ffmpeg's aformat accepts, even when ffprobe reported none.</summary>
    public string EffectiveLayout => !string.IsNullOrEmpty(ChannelLayout) && !ChannelLayout.Contains('(')
        ? ChannelLayout
        : Channels switch
        {
            1 => "mono",
            2 => "stereo",
            6 => "5.1",
            8 => "7.1",
            _ => $"{Math.Max(1, Channels)}c",
        };

    public string Describe()
    {
        var parts = new List<string> { Codec, $"{Channels}ch", $"{SampleRate / 1000.0:0.#} kHz" };
        if (!string.IsNullOrEmpty(Language) && Language != "und")
            parts.Insert(0, Language);
        if (!string.IsNullOrEmpty(Title))
            parts.Add(Title);
        return string.Join(" · ", parts);
    }
}
