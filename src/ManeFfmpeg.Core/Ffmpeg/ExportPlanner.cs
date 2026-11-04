using System.Diagnostics;
using System.Globalization;
using System.Text;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

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

    /// <summary>The commands as a copyable shell snippet.</summary>
    public string CommandText(string ffmpeg)
    {
        var sb = new StringBuilder();
        foreach (var file in Files)
            sb.Append("# ").Append(file.Path).Append(":\n# ").Append(file.Content.TrimEnd().Replace("\n", "\n# ")).Append("\n\n");
        foreach (var step in Steps)
        {
            if (step.Shift is { } shift)
                sb.Append("# ").Append(ConcatShift.Placeholder).Append(": minus the start time of ").Append(shift.ListPath)
                  .Append(", probed before this runs\n");
            sb.Append(PresetArguments.ShellQuote(ffmpeg)).Append(' ')
              .AppendJoin(' ', step.Arguments.Select(PresetArguments.ShellQuote)).Append("\n\n");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Turns a timeline and a preset into ffmpeg commands. Pure, apart from resolving symbolic links.</summary>
public static class ExportPlanner
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan AudioPreSeek = TimeSpan.FromSeconds(3);

    /// <summary>How far before a cut to seek in containers with unreliable seeking.</summary>
    private static readonly TimeSpan PreRoll = TimeSpan.FromSeconds(10);

    private static readonly string[] Prefix = [.. FfmpegTools.Quiet, "-nostats", "-stats_period", "0.5", "-progress", "pipe:1", "-y"];

    public static ExportPlan Plan(ExportJob job)
    {
        var clips = job.State.Timeline.Clips;
        if (clips.Count == 0)
            throw new ExportPlanException("The timeline is empty.");

        var analysis = PresetAnalysis.Of(job.Preset);
        if (!analysis.IsValid)
            throw new ExportPlanException($"Preset \"{job.Preset.Name}\": {analysis.Errors[0]}");
        var args = analysis.Arguments!;
        var fallback = analysis.Fallback;

        var items = clips.Select(c => Resolve(job, c, args)).ToList();
        foreach (var item in items)
        foreach (var file in new[] { item.Source, job.State.AudioSourceOf(item.Clip) }.OfType<SourceMedia>().Distinct())
        {
            if (SameFile(file.Path, job.OutputPath))
                throw new ExportPlanException(
                    $"The output file is one of the {(file == item.Source ? "source videos" : "audio files")}. Choose another name.");
        }

        var ctx = new Context(job, args, fallback, items);
        var plan = items.Count == 1 && IsWholeFile(items[0])
            ? Passthrough(ctx)
            : args.VideoCopy
                ? StreamCopy(ctx)
                : items.Count == 1 && args.AudioCopy && items[0].Audio is not null
                    ? SingleClipAudioCopy(ctx) ?? Encode(ctx)
                    : Encode(ctx);
        return plan with { Notes = [.. args.Warnings, .. plan.Notes], OutputPath = job.OutputPath, WorkDirectory = job.WorkDirectory };
    }

    /// <param name="Heard">What the clip plays (an audio file dropped on it, or <paramref name="Source"/>), or null for silence.</param>
    private sealed record Item(Clip Clip, SourceMedia Source, MediaInfo Media, VideoStreamInfo Video, HeardAudio? Heard)
    {
        /// <summary>The heard stream, or null for silence.</summary>
        public AudioStreamInfo? Audio => Heard?.Stream;

        /// <summary>
        /// The clip plays its video's own audio (none dropped on it). Replaced audio is always re-encoded: the audio
        /// file's codec may not fit the output container.
        /// </summary>
        public bool PlaysOwnAudio => Clip.ReplacedAudio is null;

        /// <summary>The clip plays its own audio, not slipped: the audio can be copied along with the video.</summary>
        public bool OwnAudioInSync => PlaysOwnAudio && Clip.AudioOffset == TimeSpan.Zero;
    }

    private sealed record Context(ExportJob Job, PresetArguments Args, IReadOnlyList<string> Fallback, List<Item> Items)
    {
        public string Out => Job.OutputPath;
        public bool AnyAudio => Items.Any(i => i.Audio is not null);
    }

    private static Item Resolve(ExportJob job, Clip clip, PresetArguments args)
    {
        var source = job.State.Source(clip.SourceId)
            ?? throw new ExportPlanException("A clip refers to a source that is no longer in the project.");
        if (!job.Media.TryGetValue(source.Id, out var media))
            throw new ExportPlanException($"\"{source.DisplayName}\" is missing or hasn't been analyzed yet.");
        var video = media.Video ?? throw new ExportPlanException($"\"{source.DisplayName}\" has no video stream.");
        if (clip.ReplacedAudio is not { } replaced)
            return new Item(clip, source, media, video,
                !args.NoAudio && media.Audio(source.AudioStreamIndex) is { } own ? new HeardAudio(source, media, own) : null);
        var file = job.State.AudioFile(replaced.FileId)
            ?? throw new ExportPlanException("A clip plays an audio file that is no longer in the project.");
        if (!job.Media.TryGetValue(file.Id, out var fileMedia))
            throw new ExportPlanException($"\"{file.DisplayName}\" is missing or hasn't been analyzed yet.");
        var stream = fileMedia.Audio(file.AudioStreamIndex);
        if (stream is null && !args.NoAudio)
            throw new ExportPlanException($"\"{file.DisplayName}\" has no audio stream.");
        return new Item(clip, source, media, video, args.NoAudio ? null : new HeardAudio(file, fileMedia, stream!));
    }

    private static bool IsWholeFile(Item item) =>
        item.Clip.In <= Tolerance && item.Clip.Out >= item.Media.Duration - Tolerance && item.OwnAudioInSync;

    // ---- Passthrough -----------------------------------------------------------------------------------

    private static ExportPlan Passthrough(Context ctx)
    {
        var item = ctx.Items[0];
        var a = new List<string>(Prefix) { "-i", item.Source.Path, "-map", $"0:{item.Video.Index}" };
        if (item.Audio is not null)
            a.AddRange(["-map", $"0:{item.Audio.Index}"]);
        if (ctx.Args.VideoFilter is not null)
            a.AddRange(["-vf", ctx.Args.VideoFilter]);
        if (ctx.Args.AudioFilter is not null && item.Audio is not null)
            a.AddRange(["-af", ctx.Args.AudioFilter]);
        a.AddRange(ctx.Args.OutputTokens);
        a.AddRange(ContainerFlags(ctx, IsHevc(ctx.Args, item)));
        a.Add(ctx.Out);
        return new ExportPlan(ExportMode.Passthrough, [new ExportStep("Exporting", a, item.Media.Duration, true)], [], item.Media.Duration,
            ["Whole file, no edits: the preset runs unchanged."]);
    }

    // ---- Encode ----------------------------------------------------------------------------------------

    private static ExportPlan Encode(Context ctx)
    {
        var notes = new List<string>();
        var inputs = new List<string>();
        var graph = new StringBuilder();
        var reference = ctx.Items[0].Video;
        var norm = VideoNormalization(ctx.Items);
        var audioFormat = AudioFormat(ctx.Items);
        var n = ctx.Items.Count;

        for (var i = 0; i < n; i++)
        {
            var item = ctx.Items[i];
            var trim = Input(inputs, item, item.Clip.In, item.Clip.Duration);
            graph.Append($"[{i}:{item.Video.Index}]trim={trim}duration={TimeFormat.Seconds(item.Clip.Duration)},setpts=PTS-STARTPTS{norm}[v{i}];");
        }

        var audioInput = n;
        if (ctx.AnyAudio)
        {
            for (var i = 0; i < n; i++)
            {
                var item = ctx.Items[i];
                var chain = AudioChain(item, item.Clip.In, item.Clip.Duration, audioFormat!.Value, ref audioInput, inputs);
                graph.Append(chain).Append($"[a{i}];");
            }
        }

        AppendConcat(graph, n, video: true, audio: ctx.AnyAudio, ctx.Args);

        var a = new List<string>(Prefix);
        a.AddRange(inputs);
        a.AddRange(["-filter_complex", graph.ToString(), "-map", "[vout]"]);
        if (ctx.AnyAudio)
            a.AddRange(["-map", "[aout]"]);
        a.AddRange(AudioTokens(ctx, ctx.AnyAudio, notes, AudioReencodeReason(ctx, otherwise: "the edit has cuts")));
        a.AddRange(ContainerFlags(ctx, IsHevc(ctx.Args, ctx.Items[0])));
        a.Add(ctx.Out);

        if (norm.Length > 0)
            notes.Add($"Clips differ in size, frame rate or pixel format: all are converted to {reference.DisplayWidth}×{reference.DisplayHeight} like the first clip.");
        var duration = ctx.Items.Aggregate(TimeSpan.Zero, (s, it) => s + it.Clip.Duration);
        return new ExportPlan(ExportMode.Encode, [new ExportStep("Exporting", a, duration, true)], [], duration, notes);
    }

    // ---- Stream copy ------------------------------------------------------------------------------------

    private static ExportPlan StreamCopy(Context ctx)
    {
        var notes = new List<string>();
        var first = ctx.Items[0];
        foreach (var item in ctx.Items.Skip(1))
        {
            var v = item.Video;
            if (v.Codec != first.Video.Codec || v.Width != first.Video.Width || v.Height != first.Video.Height
                || v.PixelFormat != first.Video.PixelFormat || v.Index != first.Video.Index)
                throw new ExportPlanException(
                    $"Stream copy can only join videos with the same format, but \"{item.Source.DisplayName}\" " +
                    $"({v.Codec} {v.Width}×{v.Height}) differs from \"{first.Source.DisplayName}\" " +
                    $"({first.Video.Codec} {first.Video.Width}×{first.Video.Height}). Use an encoding preset.");
        }

        // Effective in points: the keyframe at or before each in point.
        var starts = new List<TimeSpan>();
        var maxShift = TimeSpan.Zero;
        var shifted = 0;
        foreach (var item in ctx.Items)
        {
            if (!ctx.Job.Keyframes.TryGetValue(item.Source.Id, out var keyframes) || keyframes.Times.Count == 0)
                throw new ExportPlanException($"Keyframes of \"{item.Source.DisplayName}\" aren't known yet.");
            var start = KeyframeAtOrBefore(keyframes.Times, item.Clip.In);
            if (item.Clip.In - start > Tolerance)
            {
                shifted++;
                maxShift = TimeSpan.FromTicks(Math.Max(maxShift.Ticks, (item.Clip.In - start).Ticks));
            }
            starts.Add(start);
        }
        if (shifted > 0)
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Stream copy cuts at keyframes: {shifted} clip(s) start earlier, by up to {maxShift.TotalSeconds:0.###} s. Turn on keyframe snapping to see exact cuts."));

        var allHaveAudio = ctx.Items.All(i => i.Audio is not null);
        var sameAudio = allHaveAudio && ctx.Items.All(i =>
            i.Audio!.Codec == first.Audio!.Codec && i.Audio.SampleRate == first.Audio.SampleRate
            && i.Audio.Channels == first.Audio.Channels && i.Audio.Index == first.Audio.Index);
        var copyAudio = ctx.AnyAudio && ctx.Args.AudioCopy && sameAudio && ctx.Items.All(i => i.OwnAudioInSync);

        // Where the container seeks unreliably (MPEG-TS…), the concat demuxer's inpoint lands early, so each
        // clip is first cut into an exact temporary segment and the segments are joined.
        var segmented = ctx.Items.Any(i => i.Media.HasUnreliableSeeking);
        // How long a segment runs before its keyframe: with copied audio, it starts at the keyframe's decode
        // time, the reorder delay earlier, but no earlier than the file. A clip from the file's first keyframe
        // therefore has a shorter lead; the durations in the list make up for it, so clips join without a gap.
        var leads = ctx.Items.Select((item, i) =>
        {
            var delay = ctx.Job.Keyframes[item.Source.Id].ReorderDelay;
            return segmented && copyAudio ? (starts[i] < delay ? starts[i] : delay) : TimeSpan.Zero;
        }).ToList();
        var steps = new List<ExportStep>();
        var list = new StringBuilder("ffconcat version 1.0\n");
        for (var i = 0; i < ctx.Items.Count; i++)
        {
            var item = ctx.Items[i];
            var length = item.Clip.Out - starts[i];
            var delay = ctx.Job.Keyframes[item.Source.Id].ReorderDelay;
            var toEnd = item.Clip.Out >= item.Media.Duration - Tolerance;
            if (segmented)
            {
                var segment = Path.Combine(ctx.Job.WorkDirectory, $"segment{i:D3}.mkv");
                var seek = starts[i] > PreRoll ? starts[i] - PreRoll : TimeSpan.Zero;
                var cut = new List<string>(Prefix) { "-ss", TimeFormat.Seconds(seek), "-i", item.Source.Path, "-map", $"0:{item.Video.Index}" };
                // Output-side seek and length compare decode timestamps; the keyframe decodes "delay" early.
                var cutStart = starts[i] - delay - seek;
                var cutLength = length;
                if (copyAudio)
                    cut.AddRange(["-map", $"0:{item.Audio!.Index}"]);
                else
                {
                    // With video alone, ffmpeg counts output time from the video's first timestamp rather than
                    // the container start, and a seek even slightly past the keyframe drops its whole GOP.
                    // Seek by that difference and half a frame earlier; the frame before is no keyframe and
                    // isn't copied.
                    var margin = TimeFormat.FrameDuration(item.Video.FrameRate) / 2;
                    cutStart -= item.Video.StartOffset + margin;
                    cutLength += margin;
                }
                cut.AddRange(["-c", "copy", "-ss", TimeFormat.Seconds(cutStart)]);
                if (!toEnd)
                    cut.AddRange(["-t", TimeFormat.Seconds(cutLength)]);
                cut.AddRange(["-f", "matroska", segment]);
                steps.Add(new ExportStep($"Cutting clip {i + 1} of {ctx.Items.Count}", cut, length, false));
                list.Append("file ").Append(ConcatQuote(segment)).Append('\n');
            }
            else
            {
                list.Append("file ").Append(ConcatQuote(item.Source.Path)).Append('\n')
                    .Append("inpoint ").Append(TimeFormat.Seconds(starts[i] + item.Media.StartTime)).Append('\n');
                // The concat demuxer stops at a *decode* timestamp; with B-frames the next GOP's keyframe is
                // decoded before the cut, so stop earlier by the reorder delay. A clip ending at the file end
                // simply runs to the end.
                if (!toEnd)
                    list.Append("outpoint ").Append(TimeFormat.Seconds(item.Clip.Out - delay + item.Media.StartTime)).Append('\n');
            }
            // Explicit length, so the next segment starts at the real cut and not at the earlier outpoint.
            var nextLead = i + 1 < ctx.Items.Count ? leads[i + 1] : leads[i];
            list.Append("duration ").Append(TimeFormat.Seconds(length + leads[i] - nextLead)).Append('\n');
        }
        var listPath = Path.Combine(ctx.Job.WorkDirectory, "segments.ffconcat");
        var videoMap = segmented ? "0:0" : $"0:{first.Video.Index}";
        var audioMap = segmented ? "0:1" : $"0:{first.Audio?.Index}";

        // Audio that isn't copied goes through a filter graph, shifted like the concat input (see ConcatShift).
        var filteredAudio = !copyAudio && ctx.AnyAudio;
        var inputs = new List<string>();
        var graph = new StringBuilder();
        var outputTokens = ctx.Args.OutputTokens;
        if (filteredAudio)
        {
            var audioFormat = AudioFormat(ctx.Items)!.Value;
            var audioInput = 1;
            for (var i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.Items[i];
                graph.Append(AudioChain(item, starts[i], item.Clip.Out - starts[i], audioFormat, ref audioInput, inputs)).Append($"[a{i}];");
            }
            AppendConcat(graph, ctx.Items.Count, video: false, audio: true, ctx.Args, audioOut: "ashift");
            outputTokens = AudioTokens(ctx, true, notes,
                AudioReencodeReason(ctx, otherwise: allHaveAudio ? "the clips' audio formats differ" : "some clips have no audio"));
        }

        IReadOnlyList<string> JoinArguments(string shift)
        {
            var a = new List<string>(Prefix) { "-f", "concat", "-safe", "0", "-i", listPath };
            if (filteredAudio)
            {
                a.AddRange(inputs);
                a.AddRange(["-filter_complex", $"{graph};[ashift]asetpts=PTS+{shift}/TB[aout]", "-map", videoMap, "-map", "[aout]"]);
            }
            else
            {
                a.AddRange(["-map", videoMap]);
                if (copyAudio)
                    a.AddRange(["-map", audioMap]);
            }
            a.AddRange(outputTokens);
            a.AddRange(ContainerFlags(ctx, IsHevc(ctx.Args, first)));
            a.Add(ctx.Out);
            return a;
        }

        var duration = TimeSpan.Zero;
        for (var i = 0; i < ctx.Items.Count; i++)
            duration += ctx.Items[i].Clip.Out - starts[i];
        steps.Add(new ExportStep(segmented ? "Joining" : "Copying", JoinArguments(ConcatShift.Placeholder), duration, true)
        {
            Shift = filteredAudio ? new ConcatShift(listPath, JoinArguments) : null,
        });
        return new ExportPlan(ExportMode.StreamCopy, steps, [new PlannedFile(listPath, list.ToString())], duration, notes);
    }

    public static TimeSpan KeyframeAtOrBefore(IReadOnlyList<TimeSpan> keyframes, TimeSpan time)
    {
        var best = keyframes[0];
        foreach (var k in keyframes)
        {
            if (k > time + Tolerance)
                break;
            best = k;
        }
        return best > time ? time : best;
    }

    // ---- Single clip, lossless audio -------------------------------------------------------------------

    private static ExportPlan? SingleClipAudioCopy(Context ctx)
    {
        var item = ctx.Items[0];
        var heard = item.Heard!;
        var clip = item.Clip;
        if (!item.PlaysOwnAudio)
            return null;
        if (heard.Layout(clip) is not AudioLayout.Placed { Placement: { IsSilent: false } placement })
            return null;
        var (lead, readFrom, readLength, _) = placement;

        var preSeek = readFrom > AudioPreSeek ? readFrom - AudioPreSeek : TimeSpan.Zero;
        var tmp = Path.Combine(ctx.Job.WorkDirectory, "audio.mka");

        var extract = new List<string>(Prefix)
        {
            "-ss", TimeFormat.Seconds(preSeek), "-i", heard.File.Path, "-map", $"0:{heard.Stream.Index}", "-c", "copy",
            "-ss", TimeFormat.Seconds(readFrom - preSeek), "-t", TimeFormat.Seconds(readLength), "-f", "matroska", tmp,
        };

        var mux = new List<string>(Prefix);
        var trim = Input(mux, item, clip.In, clip.Duration);
        if (lead > TimeSpan.Zero)
            mux.AddRange(["-itsoffset", TimeFormat.Seconds(lead)]);
        mux.AddRange(["-i", tmp, "-map", $"0:{item.Video.Index}", "-map", "1:a:0"]);
        var videoFilters = new List<string>();
        if (trim.Length > 0)
            videoFilters.Add($"trim={trim}duration={TimeFormat.Seconds(clip.Duration)},setpts=PTS-STARTPTS");
        if (ctx.Args.VideoFilter is not null)
            videoFilters.Add(ctx.Args.VideoFilter);
        if (videoFilters.Count > 0)
            mux.AddRange(["-vf", string.Join(',', videoFilters)]);
        mux.AddRange(ctx.Args.OutputTokens);
        mux.AddRange(ContainerFlags(ctx, IsHevc(ctx.Args, item)));
        mux.AddRange(["-t", TimeFormat.Seconds(clip.Duration), ctx.Out]);

        return new ExportPlan(ExportMode.SingleClipAudioCopy,
            [new ExportStep("Cutting audio", extract, readLength, false), new ExportStep("Exporting", mux, clip.Duration, true)],
            [], clip.Duration, ["Audio is copied without re-encoding (cut separately, then muxed)."]);
    }

    // ---- Shared pieces ---------------------------------------------------------------------------------

    /// <summary>
    /// Audio for one clip: the slipped range of the heard file, padded with silence where it runs past the file
    /// (or looped, for replaced audio that loops), and trimmed to exactly <paramref name="length"/> so clips never
    /// drift. Adds an input when audio is read.
    /// </summary>
    private static string AudioChain(Item item, TimeSpan videoStart, TimeSpan length, (int Rate, string Layout) format,
        ref int nextInput, List<string> inputs)
    {
        var fmt = $"aformat=sample_rates={format.Rate}:channel_layouts={format.Layout}";
        var len = TimeFormat.Seconds(length);
        if (item.Heard is not { } heard)
            return $"anullsrc=r={format.Rate}:cl={format.Layout},atrim=duration={len},asetpts=N/SR/TB";

        var layout = heard.Layout(item.Clip, videoStart, length);
        if (layout is AudioLayout.Looped { Loop: { Wraps: true } loop })
        {
            var pieces = loop.Pieces().ToList();
            return pieces.Count <= MaxLoopPieces || Samples(loop.Period, format.Rate) > int.MaxValue
                ? LoopByPieces(heard, pieces, fmt, format.Rate, ref nextInput, inputs)
                : LoopByRepeating(heard, loop, length, fmt, format.Rate, ref nextInput, inputs);
        }

        // Looped audio that doesn't wrap is one plain read inside some repetition of the file.
        var placement = layout switch
        {
            AudioLayout.Looped repeated => repeated.Loop.AsPlacement(),
            AudioLayout.Placed placed => placed.Placement,
            _ => throw new UnreachableException(),
        };
        if (placement.IsSilent)
            return $"anullsrc=r={format.Rate}:cl={format.Layout},atrim=duration={len},asetpts=N/SR/TB";

        var trim = Input(inputs, heard.File.Path, heard.Info, placement.ReadFrom, placement.ReadLength);
        var chain = new StringBuilder($"[{nextInput}:{heard.Stream.Index}]");
        if (trim.Length > 0)
            chain.Append($"atrim={trim}duration={TimeFormat.Seconds(placement.ReadLength)},asetpts=PTS-STARTPTS,");
        chain.Append(fmt);
        nextInput++;
        if (placement.Lead > TimeSpan.Zero)
        {
            var samples = Samples(placement.Lead, format.Rate);
            chain.Append(CultureInfo.InvariantCulture, $",adelay=delays={samples}S:all=1");
        }
        chain.Append($",apad=whole_dur={len},atrim=duration={len},asetpts=N/SR/TB");
        return chain.ToString();
    }

    /// <summary>
    /// Looped audio that repeats at most this many times is read piece by piece; shorter files that repeat more often
    /// are repeated in the graph, which holds one whole file of decoded audio in memory.
    /// </summary>
    private const int MaxLoopPieces = 16;

    /// <summary>
    /// Each piece of the loop read on its own and cut to the samples between its start and end in the stretch, then
    /// joined: sample-exact seams that never drift, and memory that doesn't grow with the file's length.
    /// </summary>
    private static string LoopByPieces(HeardAudio heard, List<(TimeSpan ReadFrom, TimeSpan Length)> pieces, string fmt, int rate,
        ref int nextInput, List<string> inputs)
    {
        var graph = new StringBuilder();
        var labels = new StringBuilder();
        var at = TimeSpan.Zero;
        foreach (var (readFrom, length) in pieces)
        {
            var samples = Samples(at + length, rate) - Samples(at, rate);
            at += length;
            var trim = Input(inputs, heard.File.Path, heard.Info, readFrom, length);
            graph.Append($"[{nextInput}:{heard.Stream.Index}]");
            if (trim.Length > 0)
                graph.Append($"atrim={trim}duration={TimeFormat.Seconds(length)},asetpts=PTS-STARTPTS,");
            graph.Append(fmt).Append(CultureInfo.InvariantCulture, $",apad=whole_len={samples},atrim=end_sample={samples}[lp{nextInput}];");
            labels.Append($"[lp{nextInput}]");
            nextInput++;
        }
        return graph.Append(labels).Append($"concat=n={pieces.Count}:v=0:a=1,asetpts=N/SR/TB").ToString();
    }

    /// <summary>One whole period of the file, padded or cut to exactly that many samples, repeated sample-exactly.</summary>
    private static string LoopByRepeating(HeardAudio heard, AudioLoop loop, TimeSpan length, string fmt, int rate,
        ref int nextInput, List<string> inputs)
    {
        var period = Samples(loop.Period, rate);
        var phase = Samples(loop.Phase, rate);
        var loopTrim = Input(inputs, heard.File.Path, heard.Info, TimeSpan.Zero, loop.Period);
        var looped = new StringBuilder($"[{nextInput}:{heard.Stream.Index}]");
        if (loopTrim.Length > 0)
            looped.Append($"atrim={loopTrim}duration={TimeFormat.Seconds(loop.Period)},asetpts=PTS-STARTPTS,");
        looped.Append(fmt).Append(CultureInfo.InvariantCulture,
            $",apad=whole_dur={TimeFormat.Seconds(loop.Period)},atrim=end_sample={period},aloop=loop=-1:size={period}");
        looped.Append(CultureInfo.InvariantCulture,
            $",atrim=start_sample={phase}:end_sample={phase + Samples(length, rate)},asetpts=N/SR/TB");
        nextInput++;
        return looped.ToString();
    }

    private static long Samples(TimeSpan time, int rate) => (long)Math.Round(time.TotalSeconds * rate);

    /// <summary>
    /// Adds an input reading [start, start+length) of the clip's file. Where seeking is unreliable it seeks
    /// <see cref="PreRoll"/> early and returns the "start=…:" part of a trim that cuts exactly.
    /// </summary>
    private static string Input(List<string> inputs, Item item, TimeSpan start, TimeSpan length) =>
        Input(inputs, item.Source.Path, item.Media, start, length);

    /// <summary>
    /// Like the clip's file, for any file. Audio-only files are read from their start and trimmed in the graph:
    /// seeking in MP3 or ADTS lands on estimated timestamps, and decoding audio up to the cut is cheap.
    /// </summary>
    private static string Input(List<string> inputs, string path, MediaInfo media, TimeSpan start, TimeSpan length)
    {
        if (media.Video is null)
        {
            inputs.AddRange(["-t", TimeFormat.Seconds(start + length), "-i", path]);
            return start > TimeSpan.Zero ? $"start={TimeFormat.Seconds(start)}:" : "";
        }
        if (!media.HasUnreliableSeeking)
        {
            inputs.AddRange(["-ss", TimeFormat.Seconds(start), "-t", TimeFormat.Seconds(length), "-i", path]);
            return "";
        }
        var seek = start > PreRoll ? start - PreRoll : TimeSpan.Zero;
        inputs.AddRange(["-ss", TimeFormat.Seconds(seek), "-t", TimeFormat.Seconds(start - seek + length), "-i", path]);
        return $"start={TimeFormat.Seconds(start - seek)}:";
    }

    private static void AppendConcat(StringBuilder graph, int n, bool video, bool audio, PresetArguments args, string audioOut = "aout")
    {
        for (var i = 0; i < n; i++)
        {
            if (video)
                graph.Append($"[v{i}]");
            if (audio)
                graph.Append($"[a{i}]");
        }
        var vLabel = args.VideoFilter is null ? "vout" : "vcat";
        var aLabel = args.AudioFilter is null ? audioOut : "acat";
        graph.Append($"concat=n={n}:v={(video ? 1 : 0)}:a={(audio ? 1 : 0)}");
        if (video)
            graph.Append($"[{vLabel}]");
        if (audio)
            graph.Append($"[{aLabel}]");
        if (video && args.VideoFilter is not null)
            graph.Append($";[vcat]{args.VideoFilter}[vout]");
        if (audio && args.AudioFilter is not null)
            graph.Append($";[acat]{args.AudioFilter}[{audioOut}]");
    }

    /// <summary>Why audio can't be copied: replaced or slipped audio first, else <paramref name="otherwise"/>.</summary>
    private static string AudioReencodeReason(Context ctx, string otherwise) =>
        ctx.Items.Any(i => !i.PlaysOwnAudio) ? "clips have replaced audio"
        : ctx.Items.Any(i => !i.OwnAudioInSync) ? "clips have audio offsets"
        : otherwise;

    /// <summary>Preset output options, with "-c:a copy" swapped for the fallback when audio is filtered.</summary>
    private static IReadOnlyList<string> AudioTokens(Context ctx, bool audioFiltered, List<string> notes, string reason)
    {
        if (!audioFiltered || !ctx.Args.AudioCopy)
            return ctx.Args.OutputTokens;
        notes.Add($"Audio is re-encoded with \"{ctx.Job.Preset.AudioFallbackArguments}\" because {reason} (stream copy isn't possible).");
        return ctx.Args.WithAudioCodecReplaced(ctx.Fallback);
    }

    /// <summary>Filters that make every clip match the first one, or "" when they already match.</summary>
    private static string VideoNormalization(List<Item> items)
    {
        var r = items[0].Video;
        var sizeDiffers = items.Any(i => i.Video.DisplayWidth != r.DisplayWidth || i.Video.DisplayHeight != r.DisplayHeight
                                         || !i.Video.SampleAspectRatio.Equivalent(r.SampleAspectRatio));
        var rateDiffers = items.Any(i => !i.Video.FrameRate.Equivalent(r.FrameRate));
        var formatDiffers = items.Any(i => i.Video.PixelFormat != r.PixelFormat) && r.PixelFormat.Length > 0;
        var sb = new StringBuilder();
        if (rateDiffers)
            sb.Append(",fps=").Append(r.FrameRate.ToString());
        if (sizeDiffers)
            sb.Append($",scale={r.DisplayWidth}:{r.DisplayHeight}:force_original_aspect_ratio=decrease,pad={r.DisplayWidth}:{r.DisplayHeight}:(ow-iw)/2:(oh-ih)/2,setsar=1");
        if (formatDiffers)
            sb.Append(",format=").Append(r.PixelFormat);
        return sb.ToString();
    }

    /// <summary>
    /// The format every clip's audio is converted to: the highest sample rate and the widest channel layout among the
    /// clips, so a low-rate or mono audio file on one clip never downgrades the others.
    /// </summary>
    private static (int Rate, string Layout)? AudioFormat(List<Item> items)
    {
        var streams = items.Select(i => i.Audio).OfType<AudioStreamInfo>().ToList();
        if (streams.Count == 0)
            return null;
        var widest = streams.MaxBy(a => a.Channels)!;
        return (streams.Max(a => a.SampleRate), widest.EffectiveLayout);
    }

    private static IEnumerable<string> ContainerFlags(Context ctx, bool hevc)
    {
        var ext = Path.GetExtension(ctx.Out).TrimStart('.').ToLowerInvariant();
        if (ext is not ("mp4" or "m4v" or "mov"))
            yield break;
        if (!ctx.Args.SetsMovFlags)
        {
            yield return "-movflags";
            yield return "+faststart";
        }
        if (hevc && !ctx.Args.SetsVideoTag)
        {
            yield return "-tag:v";
            yield return "hvc1";
        }
    }

    private static bool IsHevc(PresetArguments args, Item first) =>
        args.VideoCodec is { } codec
            ? codec == "copy" ? first.Video.Codec == "hevc" : codec.Contains("265") || codec.Contains("hevc")
            : false;

    /// <summary>
    /// Whether two paths name the same file: compared after resolving symbolic links, and ignoring case on macOS
    /// and Windows, whose usual file systems treat "clip.mp4" and "clip.MP4" as one file.
    /// </summary>
    private static bool SameFile(string a, string b) =>
        string.Equals(RealPath(a), RealPath(b),
            OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            return File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName ?? full;
        }
        catch (IOException)
        {
            return full;
        }
    }

    private static string ConcatQuote(string path) => "'" + path.Replace("'", "'\\''") + "'";
}
