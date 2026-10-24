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

/// <summary>One ffmpeg invocation. <see cref="Duration"/> is the media time it produces, for progress.</summary>
public sealed record ExportStep(string Title, IReadOnlyList<string> Arguments, TimeSpan Duration, bool WritesOutput)
{
    /// <summary>
    /// A concat list whose start time must be probed before running: ffmpeg shifts that input by its
    /// (often negative) start time, and <see cref="ExportPlanner.ConcatShiftToken"/> in the arguments
    /// is replaced by the same shift so filtered audio stays aligned with copied video.
    /// </summary>
    public string? ConcatShiftProbe { get; init; }
}

public sealed record PlannedFile(string Path, string Content);

public sealed record ExportPlan(
    ExportMode Mode,
    IReadOnlyList<ExportStep> Steps,
    IReadOnlyList<PlannedFile> Files,
    TimeSpan Duration,
    IReadOnlyList<string> Notes)
{
    /// <summary>The commands as a copyable shell snippet.</summary>
    public string CommandText(string ffmpeg)
    {
        var sb = new StringBuilder();
        foreach (var file in Files)
            sb.Append("# ").Append(file.Path).Append(":\n# ").Append(file.Content.TrimEnd().Replace("\n", "\n# ")).Append("\n\n");
        foreach (var step in Steps)
            sb.Append(PresetArguments.ShellQuote(ffmpeg)).Append(' ')
              .AppendJoin(' ', step.Arguments.Select(PresetArguments.ShellQuote)).Append("\n\n");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Turns a timeline and a preset into ffmpeg commands. Pure: no I/O.</summary>
public static class ExportPlanner
{
    public const string ConcatShiftToken = "$CONCAT_SHIFT$";

    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan AudioPreSeek = TimeSpan.FromSeconds(3);

    /// <summary>How far before a cut to seek in containers with unreliable seeking.</summary>
    private static readonly TimeSpan PreRoll = TimeSpan.FromSeconds(10);

    private static readonly string[] Prefix =
        ["-nostdin", "-hide_banner", "-loglevel", "error", "-nostats", "-stats_period", "0.5", "-progress", "pipe:1", "-y"];

    public static ExportPlan Plan(ExportJob job)
    {
        var clips = job.State.Timeline.Clips;
        if (clips.Count == 0)
            throw new ExportPlanException("The timeline is empty.");

        PresetArguments args;
        IReadOnlyList<string> fallback;
        try
        {
            args = PresetArguments.Parse(job.Preset.Arguments);
            fallback = PresetArguments.Tokenize(job.Preset.AudioFallbackArguments);
        }
        catch (FormatException ex)
        {
            throw new ExportPlanException($"Preset \"{job.Preset.Name}\": {ex.Message}");
        }
        if (args.NoVideo)
            throw new ExportPlanException("The preset disables video (-vn).");
        if (args.VideoCopy && args.VideoFilter is not null)
            throw new ExportPlanException("The preset copies video (-c:v copy) but also sets a video filter.");
        if (args.AudioCopy && args.AudioFilter is not null)
            throw new ExportPlanException("The preset copies audio (-c:a copy) but also sets an audio filter.");

        var items = clips.Select(c => Resolve(job, c, args)).ToList();
        foreach (var item in items)
            if (string.Equals(Path.GetFullPath(item.Source.Path), Path.GetFullPath(job.OutputPath), StringComparison.Ordinal))
                throw new ExportPlanException("The output file is one of the source videos. Choose another name.");

        var ctx = new Context(job, args, fallback, items);
        var plan = items.Count == 1 && IsWholeFile(items[0])
            ? Passthrough(ctx)
            : args.VideoCopy
                ? StreamCopy(ctx)
                : items.Count == 1 && args.AudioCopy && items[0].Audio is not null
                    ? SingleClipAudioCopy(ctx) ?? Encode(ctx)
                    : Encode(ctx);
        return plan with { Notes = [.. args.Warnings, .. plan.Notes] };
    }

    private sealed record Item(Clip Clip, SourceMedia Source, MediaInfo Media, VideoStreamInfo Video, AudioStreamInfo? Audio);

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
        var audio = args.NoAudio ? null : media.Audio(source.AudioStreamIndex);
        return new Item(clip, source, media, video, audio);
    }

    private static bool IsWholeFile(Item item) =>
        item.Clip.In <= Tolerance && item.Clip.Out >= item.Media.Duration - Tolerance && item.Clip.AudioOffset == TimeSpan.Zero;

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
        a.AddRange(AudioTokens(ctx, ctx.AnyAudio, notes, "the edit has cuts or audio offsets"));
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
        var noOffsets = ctx.Items.All(i => i.Clip.AudioOffset == TimeSpan.Zero);
        var copyAudio = ctx.AnyAudio && ctx.Args.AudioCopy && sameAudio && noOffsets;

        // Where the container seeks unreliably (MPEG-TS…), the concat demuxer's inpoint lands early, so each
        // clip is first cut into an exact temporary segment and the segments are joined.
        var segmented = ctx.Items.Any(i => i.Media.HasUnreliableSeeking);
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
            list.Append("duration ").Append(TimeFormat.Seconds(length)).Append('\n');
        }
        var listPath = Path.Combine(ctx.Job.WorkDirectory, "segments.ffconcat");
        var videoMap = segmented ? "0:0" : $"0:{first.Video.Index}";
        var audioMap = segmented ? "0:1" : $"0:{first.Audio?.Index}";

        var a = new List<string>(Prefix) { "-f", "concat", "-safe", "0", "-i", listPath };
        if (copyAudio || !ctx.AnyAudio)
        {
            a.AddRange(["-map", videoMap]);
            if (copyAudio)
                a.AddRange(["-map", audioMap]);
            a.AddRange(ctx.Args.OutputTokens);
        }
        else
        {
            var inputs = new List<string>();
            var graph = new StringBuilder();
            var audioFormat = AudioFormat(ctx.Items)!.Value;
            var audioInput = 1;
            for (var i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.Items[i];
                graph.Append(AudioChain(item, starts[i], item.Clip.Out - starts[i], audioFormat, ref audioInput, inputs)).Append($"[a{i}];");
            }
            AppendConcat(graph, ctx.Items.Count, video: false, audio: true, ctx.Args, audioOut: "ashift");
            graph.Append($";[ashift]asetpts=PTS+{ConcatShiftToken}/TB[aout]");
            a.AddRange(inputs);
            a.AddRange(["-filter_complex", graph.ToString(), "-map", videoMap, "-map", "[aout]"]);
            var reason = !noOffsets ? "clips have audio offsets"
                : !allHaveAudio ? "some clips have no audio"
                : "the clips' audio formats differ";
            a.AddRange(AudioTokens(ctx, true, notes, reason));
        }
        a.AddRange(ContainerFlags(ctx, IsHevc(ctx.Args, first)));
        a.Add(ctx.Out);

        var duration = TimeSpan.Zero;
        for (var i = 0; i < ctx.Items.Count; i++)
            duration += ctx.Items[i].Clip.Out - starts[i];
        steps.Add(new ExportStep(segmented ? "Joining" : "Copying", a, duration, true)
        {
            ConcatShiftProbe = copyAudio || !ctx.AnyAudio ? null : listPath,
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
        var audio = item.Audio!;
        var clip = item.Clip;
        var placement = clip.AudioPlacement(item.Media.AudioEnd(audio));
        if (placement.IsSilent)
            return null;
        var (lead, readFrom, readLength, _) = placement;

        var preSeek = readFrom > AudioPreSeek ? readFrom - AudioPreSeek : TimeSpan.Zero;
        var tmp = Path.Combine(ctx.Job.WorkDirectory, "audio.mka");

        var extract = new List<string>(Prefix)
        {
            "-ss", TimeFormat.Seconds(preSeek), "-i", item.Source.Path, "-map", $"0:{audio.Index}", "-c", "copy",
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
    /// Audio for one clip: the slipped source range, padded with silence where it runs past the file, and
    /// trimmed to exactly <paramref name="length"/> so clips never drift. Adds an input when audio is read.
    /// </summary>
    private static string AudioChain(Item item, TimeSpan videoStart, TimeSpan length, (int Rate, string Layout) format,
        ref int nextInput, List<string> inputs)
    {
        var fmt = $"aformat=sample_rates={format.Rate}:channel_layouts={format.Layout}";
        var len = TimeFormat.Seconds(length);
        if (item.Audio is null)
            return $"anullsrc=r={format.Rate}:cl={format.Layout},atrim=duration={len},asetpts=N/SR/TB";

        var placement = AudioPlacement.For(videoStart, length, item.Clip.AudioOffset, item.Media.AudioEnd(item.Audio));
        if (placement.IsSilent)
            return $"anullsrc=r={format.Rate}:cl={format.Layout},atrim=duration={len},asetpts=N/SR/TB";

        var trim = Input(inputs, item, placement.ReadFrom, placement.ReadLength);
        var chain = new StringBuilder($"[{nextInput}:{item.Audio.Index}]");
        if (trim.Length > 0)
            chain.Append($"atrim={trim}duration={TimeFormat.Seconds(placement.ReadLength)},asetpts=PTS-STARTPTS,");
        chain.Append(fmt);
        nextInput++;
        if (placement.Lead > TimeSpan.Zero)
        {
            var samples = (long)Math.Round(placement.Lead.TotalSeconds * format.Rate);
            chain.Append(CultureInfo.InvariantCulture, $",adelay=delays={samples}S:all=1");
        }
        chain.Append($",apad=whole_dur={len},atrim=duration={len},asetpts=N/SR/TB");
        return chain.ToString();
    }

    /// <summary>
    /// Adds an input reading [start, start+length) of the clip's file. Where seeking is unreliable it seeks
    /// <see cref="PreRoll"/> early and returns the "start=…:" part of a trim that cuts exactly.
    /// </summary>
    private static string Input(List<string> inputs, Item item, TimeSpan start, TimeSpan length)
    {
        if (!item.Media.HasUnreliableSeeking)
        {
            inputs.AddRange(["-ss", TimeFormat.Seconds(start), "-t", TimeFormat.Seconds(length), "-i", item.Source.Path]);
            return "";
        }
        var seek = start > PreRoll ? start - PreRoll : TimeSpan.Zero;
        inputs.AddRange(["-ss", TimeFormat.Seconds(seek), "-t", TimeFormat.Seconds(start - seek + length), "-i", item.Source.Path]);
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

    /// <summary>Sample rate and layout of the first clip that has audio; everything is converted to it.</summary>
    private static (int Rate, string Layout)? AudioFormat(List<Item> items) =>
        items.FirstOrDefault(i => i.Audio is not null)?.Audio is { } a ? (a.SampleRate, a.EffectiveLayout) : null;

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

    private static string ConcatQuote(string path) => "'" + path.Replace("'", "'\\''") + "'";
}
