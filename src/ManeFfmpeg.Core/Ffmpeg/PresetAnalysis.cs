using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Ffmpeg;

/// <summary>
/// Whether a preset can be used and what it does, worked out once for the export planner and the preset
/// editor. Never throws: problems that stop an export are <see cref="Errors"/>, the rest <see cref="Warnings"/>.
/// </summary>
public sealed record PresetAnalysis
{
    private PresetAnalysis() { }

    /// <summary>The parsed options; null when they can't be read (see <see cref="Errors"/>).</summary>
    public PresetArguments? Arguments { get; private init; }

    /// <summary>Audio options used instead of "-c:a copy" when audio has to go through filters.</summary>
    public IReadOnlyList<string> Fallback { get; private init; } = [];

    public IReadOnlyList<string> Errors { get; private init; } = [];
    public IReadOnlyList<string> Warnings { get; private init; } = [];

    public bool IsValid => Errors.Count == 0;

    public bool VideoCopy => Arguments?.VideoCopy == true;

    public static PresetAnalysis Of(Preset preset) => Of(preset.Arguments, preset.AudioFallbackArguments);

    public static PresetAnalysis Of(string arguments, string audioFallback)
    {
        var errors = new List<string>();
        PresetArguments? args = null;
        IReadOnlyList<string> fallback = [];
        try
        {
            args = PresetArguments.Parse(arguments);
        }
        catch (FormatException ex)
        {
            errors.Add(ex.Message);
        }
        try
        {
            fallback = PresetArguments.Tokenize(audioFallback);
        }
        catch (FormatException ex)
        {
            errors.Add($"Audio fallback: {ex.Message}");
        }
        if (args is { NoVideo: true })
            errors.Add("The preset disables video (-vn).");
        if (args is { VideoCopy: true, VideoFilter: not null })
            errors.Add("Video filters can't be used while copying video (-c:v copy).");
        if (args is { AudioCopy: true, AudioFilter: not null })
            errors.Add("Audio filters can't be used while copying audio (-c:a copy).");
        return new PresetAnalysis { Arguments = args, Fallback = fallback, Errors = errors, Warnings = args?.Warnings ?? [] };
    }

    /// <summary>How the export treats video, in the planner's terms.</summary>
    public string? VideoSummary => Arguments is not { } args ? null
        : args.VideoCopy ? "Video: copied without re-encoding. Cuts snap to keyframes; all clips must share one format."
        : $"Video: encoded with {args.VideoCodec ?? "ffmpeg's default for the container"}.";

    /// <summary>How the export treats audio, in the planner's terms (see <see cref="ExportPlanner"/>).</summary>
    public string? AudioSummary(string fallbackText) => Arguments is not { } args ? null
        : args.NoAudio ? "Audio: dropped (-an)."
        : !args.AudioCopy ? $"Audio: encoded with {args.AudioCodec ?? "ffmpeg's default for the container"}."
        : args.VideoCopy
            ? $"Audio: copied when all clips' audio shares one format and no clip has an audio offset; otherwise encoded with “{fallbackText}”."
            : $"Audio: copied for a single clip, even with an audio offset. Several clips are encoded with “{fallbackText}”.";
}
