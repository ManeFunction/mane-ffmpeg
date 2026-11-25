using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Ffmpeg;

/// <summary>What the app was started to convert: one preset, several files.</summary>
public sealed record ConversionRequest(string PresetId, IReadOnlyList<string> Files);

/// <summary>
/// Converting whole files with a preset, outside the editor (Finder's Quick Actions): each file is planned as a project
/// holding that one video as a single clip, so it goes through the same export as an edit would, and is saved next to it.
/// </summary>
public static class FileConversion
{
    /// <summary>Starts the app as a converter: <c>--quick-action &lt;preset id&gt; &lt;files…&gt;</c>.</summary>
    public const string CommandLineFlag = "--quick-action";

    /// <summary>The conversion asked for on the command line, or null when the app was started normally.</summary>
    public static ConversionRequest? Parse(IReadOnlyList<string> args)
    {
        var at = args.ToList().IndexOf(CommandLineFlag);
        if (at < 0 || at + 1 >= args.Count)
            return null;
        return new ConversionRequest(args[at + 1], [.. args.Skip(at + 2).Where(a => a.Length > 0).Distinct()]);
    }

    /// <summary>A project of <paramref name="path"/> alone, as one clip from start to end, with its source's id.</summary>
    public static (ProjectState State, string SourceId) ProjectOf(string path, MediaInfo info)
    {
        var added = ProjectEditor.AddProbed(ProjectState.Empty, [new ProbedFile(path, info, null)], AddRouting.ByContent,
            p => new SourceMedia { Id = "source", Path = p, FileSize = info.Stamp.Size, LastWriteUtc = info.Stamp.LastWriteUtc });
        if (added.Videos.Count == 0)
            throw new ExportPlanException($"{Path.GetFileName(path)} has no video stream.");
        return (added.State, added.Videos[0]);
    }

    /// <summary>
    /// Where the converted file goes: next to the source, named like it, with the preset's extension (else the source's
    /// for video, or the audio codec's). An existing file is never replaced: "-2", "-3"… are added instead.
    /// </summary>
    public static string OutputPathFor(string path, Preset preset, ProjectState state, IReadOnlyDictionary<string, MediaInfo> media)
    {
        var extension = preset.Extension?.Trim().TrimStart('.') is { Length: > 0 } own ? own
            : preset.Kind == PresetKind.Audio ? ExportPlanner.AudioExtensionFor(state, media, preset) ?? "m4a"
            : Path.GetExtension(path).TrimStart('.');
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = extension.Length > 0 ? "." + extension : "";
        var candidate = Path.Combine(dir, name + ext);
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
            candidate = Path.Combine(dir, $"{name}-{n}{ext}");
        return candidate;
    }

    /// <summary>The export that converts <paramref name="path"/> (probed as <paramref name="info"/>) with <paramref name="preset"/>.</summary>
    public static async Task<ExportPlan> PlanAsync(string path, MediaInfo info, Preset preset, KeyframeService keyframes, string workDirectory)
    {
        var (state, sourceId) = ProjectOf(path, info);
        var media = new Dictionary<string, MediaInfo> { [sourceId] = info };
        var index = new Dictionary<string, KeyframeIndex>();
        if (preset.Kind == PresetKind.Video && PresetAnalysis.Of(preset).VideoCopy)
            index[sourceId] = await keyframes.GetAsync(path, info);
        return ExportPlanner.Plan(new ExportJob
        {
            State = state,
            Media = media,
            Preset = preset,
            OutputPath = OutputPathFor(path, preset, state, media),
            WorkDirectory = workDirectory,
            Keyframes = index,
        });
    }
}
