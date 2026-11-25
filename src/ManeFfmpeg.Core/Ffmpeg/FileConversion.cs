using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Ffmpeg;

/// <summary>What the app was started to convert: one preset, several files.</summary>
public sealed record ConversionRequest(string PresetId, IReadOnlyList<string> Files);

/// <summary>
/// Where a conversion is: file <paramref name="Index"/> of <paramref name="Count"/>, its output once planned, and
/// what it's doing before ffmpeg starts (<paramref name="Preparing"/>) or ffmpeg's progress.
/// </summary>
public sealed record ConversionStatus(int Index, int Count, string File, string? Output, string? Preparing, ExportProgress? Progress)
{
    /// <summary>How much of all the files is done, 0 to 1.</summary>
    public double Fraction => (Index + (Progress?.Fraction ?? 0)) / Count;
}

/// <summary>The files a conversion saved, and why each failed one failed ("name: reason").</summary>
public sealed record ConversionReport(IReadOnlyList<string> Outputs, IReadOnlyList<string> Failures);

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

    /// <summary>
    /// Converts the files one after another in <paramref name="session"/>, until it's cancelled. A file that fails
    /// doesn't stop the others.
    /// </summary>
    public static async Task<ConversionReport> ConvertAsync(IReadOnlyList<string> files, Preset preset, FfmpegTools tools,
        KeyframeService keyframes, ExportSession session, IProgress<ConversionStatus>? status)
    {
        var outputs = new List<string>();
        var failures = new List<string>();
        for (var i = 0; i < files.Count && !session.IsCancelled; i++)
        {
            var file = files[i];
            var index = i;
            void Report(string? output, string? preparing, ExportProgress? progress) =>
                status?.Report(new ConversionStatus(index, files.Count, file, output, preparing, progress));
            Report(null, "Preparing…", null);
            try
            {
                var info = await new Ffprobe(tools).ProbeAsync(file, session.Token);
                var plan = await PlanAsync(file, info, preset, keyframes, ExportSession.NewWorkDirectory("convert"), s => Report(null, s, null));
                Report(plan.OutputPath, "Starting…", null);
                var result = await session.RunAsync(tools, plan, new Relay<ExportProgress>(p => Report(plan.OutputPath, null, p)));
                if (result.Outcome == ExportOutcome.Succeeded)
                    outputs.Add(plan.OutputPath);
                else if (result.Outcome == ExportOutcome.Failed)
                    failures.Add($"{Path.GetFileName(file)}: {result.Message}");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is ExportPlanException or FfprobeException)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return new ConversionReport(outputs, failures);
    }

    /// <summary>Passes reports on at once, on the reporting thread (the caller's <see cref="Progress{T}"/> moves them to its own).</summary>
    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>The export that converts <paramref name="path"/> (probed as <paramref name="info"/>) with <paramref name="preset"/>.</summary>
    /// <param name="status">Told what planning is waiting for ("Finding keyframes…").</param>
    public static async Task<ExportPlan> PlanAsync(string path, MediaInfo info, Preset preset, KeyframeService keyframes, string workDirectory,
        Action<string>? status = null)
    {
        var (state, sourceId) = ProjectOf(path, info);
        var media = new Dictionary<string, MediaInfo> { [sourceId] = info };
        var index = new Dictionary<string, KeyframeIndex>();
        if (preset.Kind == PresetKind.Video && PresetAnalysis.Of(preset).VideoCopy)
        {
            status?.Invoke("Finding keyframes…");
            index[sourceId] = await keyframes.GetAsync(path, info);
        }
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
