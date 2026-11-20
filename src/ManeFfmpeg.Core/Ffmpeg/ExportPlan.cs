using System.Text;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Platforms;

namespace ManeFfmpeg.Core.Ffmpeg;

public sealed class ExportPlanException(string message) : Exception(message);

public enum ExportMode
{
    /// <summary>One untouched clip: the preset runs on the whole file, exactly like a hand-written command.</summary>
    Passthrough,

    /// <summary>Video is encoded; every clip is trimmed and joined in a filter graph.</summary>
    Encode,

    /// <summary>Video is stream-copied through the concat demuxer, cut at keyframes.</summary>
    StreamCopy,

    /// <summary>One trimmed or offset clip whose audio is copied: audio is cut losslessly first, then muxed.</summary>
    SingleClipAudioCopy,

    /// <summary>Audio alone (an audio preset): one clip's own audio, cut without re-encoding.</summary>
    AudioCopy,

    /// <summary>Audio alone (an audio preset): every clip's audio trimmed and joined in a filter graph, then encoded.</summary>
    AudioEncode,
}

public sealed record ExportJob
{
    public required ProjectState State { get; init; }

    /// <summary>Probe results by source id.</summary>
    public required IReadOnlyDictionary<string, MediaInfo> Media { get; init; }

    public required Preset Preset { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>Directory for list files and intermediates; the runner creates and deletes it.</summary>
    public required string WorkDirectory { get; init; }

    /// <summary>Keyframe indexes by source id; required for stream copy.</summary>
    public IReadOnlyDictionary<string, KeyframeIndex> Keyframes { get; init; } = new Dictionary<string, KeyframeIndex>();
}

/// <summary>
/// One ffmpeg invocation. <see cref="Duration"/> is the media time it produces, for progress. With a
/// <see cref="Shift"/>, <see cref="Arguments"/> show the shift as <see cref="ConcatShift.Placeholder"/>.
/// </summary>
public sealed record ExportStep(string Title, IReadOnlyList<string> Arguments, TimeSpan Duration, bool WritesOutput)
{
    public ConcatShift? Shift { get; init; }
}

/// <summary>
/// ffmpeg shifts a concat input by minus its (often negative) start time, which is only known once the list's
/// segments exist. Filtered audio has to be shifted the same way to stay aligned with the copied video, so the
/// runner probes <see cref="ListPath"/> first and runs <see cref="ArgumentsFor"/> that shift (in seconds).
/// </summary>
public sealed record ConcatShift(string ListPath, Func<string, IReadOnlyList<string>> ArgumentsFor)
{
    public const string Placeholder = "$SHIFT";
}

public sealed record PlannedFile(string Path, string Content);

public sealed record ExportPlan(
    ExportMode Mode,
    IReadOnlyList<ExportStep> Steps,
    IReadOnlyList<PlannedFile> Files,
    TimeSpan Duration,
    IReadOnlyList<string> Notes)
{
    /// <summary>The file the last step writes.</summary>
    public string OutputPath { get; init; } = "";

    /// <summary>Where the planned files and intermediates go; the runner creates and deletes it.</summary>
    public string WorkDirectory { get; init; } = "";

    /// <summary>The commands as a snippet to copy into a terminal: this platform's (sh, or PowerShell on Windows) by default.</summary>
    public string CommandText(string ffmpeg, IPlatform? platform = null)
    {
        platform ??= Platform.Current;
        var sb = new StringBuilder();
        foreach (var file in Files)
            sb.Append("# ").Append(file.Path).Append(":\n# ").Append(file.Content.TrimEnd().Replace("\n", "\n# ")).Append("\n\n");
        foreach (var step in Steps)
        {
            if (step.Shift is { } shift)
                sb.Append("# ").Append(ConcatShift.Placeholder).Append(": minus the start time of ").Append(shift.ListPath)
                  .Append(", probed before this runs\n");
            sb.Append(platform.CommandLinePrefix).Append(platform.QuoteCommandArgument(ffmpeg)).Append(' ')
              .AppendJoin(' ', step.Arguments.Select(platform.QuoteCommandArgument)).Append("\n\n");
        }
        return sb.ToString().TrimEnd();
    }
}
