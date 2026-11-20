namespace ManeFfmpeg.Core.Ffmpeg;

/// <summary>
/// Which audio codecs the usual audio file types hold, so a plan never writes audio its output file can't take
/// (ffmpeg then stops with a bare "Conversion failed!"). Checked against ffmpeg 9.0.2's muxers.
/// </summary>
internal static class AudioContainer
{
    private static readonly string[] Pcm = ["pcm_u8", "pcm_s16le", "pcm_s24le", "pcm_s32le", "pcm_f32le", "pcm_f64le", "pcm_alaw", "pcm_mulaw"];

    private static readonly Dictionary<string, string[]> Holds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m4a"] = ["aac", "alac", "ac3"],
        ["mp3"] = ["mp3"],
        ["flac"] = ["flac"],
        ["ogg"] = ["vorbis", "opus", "flac"],
        ["oga"] = ["vorbis", "opus", "flac"],
        ["opus"] = ["opus", "flac"],
        ["webm"] = ["opus", "vorbis"],
        ["wav"] = [.. Pcm, "aac", "mp3", "flac", "ac3", "eac3"],
        ["aiff"] = ["pcm_s16be", "pcm_s24be", "pcm_s32be", "pcm_s16le"],
        ["aif"] = ["pcm_s16be", "pcm_s24be", "pcm_s32be", "pcm_s16le"],
        ["ac3"] = ["ac3"],
        ["eac3"] = ["eac3"],
        ["aac"] = ["aac"],
    };

    /// <summary>
    /// Whether a file with this extension holds audio in <paramref name="codec"/> (an ffprobe codec name or an ffmpeg
    /// encoder name): null when it isn't known, for files that hold most codecs (.mka, .mkv, .mp4…), for an unknown
    /// codec and for ffmpeg's default encoder (null), which always suits the file.
    /// </summary>
    public static bool? Accepts(string extension, string? codec)
    {
        if (codec is null || !Holds.TryGetValue(extension.TrimStart('.'), out var codecs))
            return null;
        return Normalize(codec) is { } name ? codecs.Contains(name) : null;
    }

    /// <summary>The codec an ffmpeg encoder writes, as ffprobe names it; null for an unknown name.</summary>
    internal static string? Normalize(string codec) => codec switch
    {
        "aac" or "libfdk_aac" or "aac_at" => "aac",
        "alac" or "alac_at" => "alac",
        "mp3" or "libmp3lame" or "libshine" => "mp3",
        "opus" or "libopus" => "opus",
        "vorbis" or "libvorbis" => "vorbis",
        "flac" or "ac3" or "eac3" or "dts" => codec,
        _ when codec.StartsWith("pcm_", StringComparison.Ordinal) => codec,
        _ => null,
    };
}
