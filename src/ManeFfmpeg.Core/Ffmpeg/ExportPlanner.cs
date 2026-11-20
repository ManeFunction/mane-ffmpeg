using System.Diagnostics;
using System.Globalization;
using System.Text;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Mpv;
using ManeFfmpeg.Core.Platforms;

namespace ManeFfmpeg.Core.Ffmpeg;

/// <summary>Turns a timeline and a preset into ffmpeg commands. Pure, apart from resolving symbolic links.</summary>
public static partial class ExportPlanner
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan AudioPreSeek = TimeSpan.FromSeconds(3);

    /// <summary>How far before a cut to seek in containers with unreliable seeking.</summary>
    private static readonly TimeSpan PreRoll = TimeSpan.FromSeconds(10);

    private static readonly string[] Prefix = [.. FfmpegTools.Quiet, "-nostats", "-stats_period", "0.5", "-progress", "pipe:1", "-y"];

    public static ExportPlan Plan(ExportJob job)
    {
        var ctx = Prepare(job);
        foreach (var item in ctx.Items)
        foreach (var file in new[] { item.Source, ctx.Job.State.AudioSourceOf(item.Clip) }.OfType<SourceMedia>().Distinct())
        {
            if (SameFile(file.Path, job.OutputPath))
                throw new ExportPlanException(
                    $"The output file is one of the {(file == item.Source ? "source videos" : "audio files")}. Choose another name.");
        }
        if (ctx.Track.Select(t => t.Heard.File).Distinct().FirstOrDefault(f => SameFile(f.Path, job.OutputPath)) is not null)
            throw new ExportPlanException("The output file is one of the audio files. Choose another name.");

        if (job.Preset.Kind == PresetKind.Audio)
        {
            var audio = AudioOnly(ctx);
            return audio with
            {
                Notes = [.. ctx.Analysis.Warnings, .. audio.Notes, .. TrackNotes(ctx)],
                OutputPath = job.OutputPath,
                WorkDirectory = job.WorkDirectory,
            };
        }

        var crop = ctx.Job.State.Crop;
        if (crop is not null && ctx.Args.VideoCopy)
            throw new ExportPlanException(
                $"Preset \"{job.Preset.Name}\" copies the video, and a copied picture can't be cropped. Choose an encoding preset or reset the crop.");
        ctx = ctx with { Crop = crop };
        var items = ctx.Items;
        var plan = items.Count == 1 && IsWholeFile(items[0]) && ctx.CopyBlocker is null
            ? Passthrough(ctx)
            : ctx.Args.VideoCopy
                ? StreamCopy(ctx)
                : items.Count == 1 && ctx.Args.AudioCopy && items[0].Audio is not null && ctx.CutBlocker is null
                    ? SingleClipAudioCopy(ctx) ?? Encode(ctx)
                    : Encode(ctx);
        IEnumerable<string> cropped = ctx.CropPixels is { } p
            ? [$"Cropped to {p.Width}×{p.Height}, from {p.X},{p.Y} of the {ctx.Frame.Width}×{ctx.Frame.Height} frame."]
            : [];
        return plan with
        {
            Notes = [.. ctx.Args.Warnings, .. plan.Notes, .. TrackNotes(ctx), .. cropped],
            OutputPath = job.OutputPath,
            WorkDirectory = job.WorkDirectory,
        };
    }

    /// <summary>
    /// What every plan starts from, and what <see cref="AudioExtensionFor"/> asks the same questions of: the track
    /// volumes folded into the clips' gains, the preset's arguments, and each clip and A2 clip with the audio it plays.
    /// </summary>
    private static Context Prepare(ExportJob job)
    {
        // Track volumes become part of each clip's gain, so everything below only looks at clip volumes.
        job = job with { State = job.State.WithTrackVolumesApplied() };
        var clips = job.State.Timeline.Clips;
        if (clips.Count == 0)
            throw new ExportPlanException("The timeline is empty.");

        var analysis = PresetAnalysis.Of(job.Preset);
        if (!analysis.IsValid)
            throw new ExportPlanException($"Preset \"{job.Preset.Name}\": {analysis.Errors[0]}");
        var args = analysis.Arguments!;
        var items = clips.Select(c => Resolve(job.State, job.Media, c, args)).ToList();
        return new Context(job, analysis, items, OutputFrame.Of([.. items.Select(i => i.Video)]))
        {
            Track = ResolveTrack(job.State, job.Media, args),
        };
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
    }

    /// <summary>An A2 clip with the file it plays.</summary>
    private sealed record TrackItem(TrackClip Clip, HeardAudio Heard);

    /// <param name="Frame">The joined picture, which the crop is a part of.</param>
    private sealed record Context(ExportJob Job, PresetAnalysis Analysis, List<Item> Items, OutputFrame Frame)
    {
        public string Out => Job.OutputPath;

        public PresetArguments Args => Analysis.Arguments!;

        /// <summary>The preset's audio fallback, used where it copies audio that has to be encoded.</summary>
        public IReadOnlyList<string> Fallback => Analysis.Fallback;

        /// <summary>The part of the frame to keep, or null for all of it (and for audio alone).</summary>
        public CropRect? Crop { get; init; }

        /// <summary>The A2 clips mixed over the clips' audio; empty when A2 has none or the preset drops audio.</summary>
        public IReadOnlyList<TrackItem> Track { get; init; } = [];

        public bool HasTrack => Track.Count > 0;

        /// <summary>The clips have sound of their own, or A2 adds some.</summary>
        public bool AnyAudio => Items.Any(i => i.Audio is not null) || HasTrack;

        /// <summary>
        /// Why the clips' audio can't be copied along with the video, or null when it can: every clip plays its own
        /// sound, in sync and at its own volume, with nothing on A2. This is the one place that decides it; every mode
        /// and every note asks here.
        /// </summary>
        public string? CopyBlocker => Blocker(offsets: true);

        /// <summary>
        /// Like <see cref="CopyBlocker"/>, for audio cut on its own (one clip's, then muxed or saved): a slipped clip
        /// can still be cut without re-encoding, so offsets don't block it.
        /// </summary>
        public string? CutBlocker => Blocker(offsets: false);

        private string? Blocker(bool offsets) =>
            Items.Any(i => !i.PlaysOwnAudio) ? "clips have replaced audio"
            : offsets && Items.Any(i => i.Clip.AudioOffset != TimeSpan.Zero) ? "clips have audio offsets"
            : Items.Any(i => !Gain.IsUnity(i.Clip.Volume)) ? "clips or A1 have their volume changed"
            : HasTrack ? "A2 is mixed in"
            : null;

        /// <summary>The format all audio is converted to before it's joined and mixed; null without audio.</summary>
        public (int Rate, string Layout)? AudioFormat =>
            ExportPlanner.AudioFormat([.. Items.Select(i => i.Audio), .. Track.Select(t => t.Heard.Stream)]);

        /// <summary>What the crop keeps of the frame, or null without one.</summary>
        public PixelCrop? CropPixels => Crop is null ? null : Frame.Pixels(Crop);

        /// <summary>The filters for the finished picture: the crop, then the preset's own -vf. Null when there are none.</summary>
        public string? VideoFilter => CropPixels is { } pixels
            ? Args.VideoFilter is { } preset ? $"{pixels.Filter},{preset}" : pixels.Filter
            : Args.VideoFilter;
    }

    private static Item Resolve(ProjectState state, IReadOnlyDictionary<string, MediaInfo> mediaById, Clip clip, PresetArguments args)
    {
        var source = state.Source(clip.SourceId)
            ?? throw new ExportPlanException("A clip refers to a source that is no longer in the project.");
        if (!mediaById.TryGetValue(source.Id, out var media))
            throw new ExportPlanException($"\"{source.DisplayName}\" is missing or hasn't been analyzed yet.");
        var video = media.Video ?? throw new ExportPlanException($"\"{source.DisplayName}\" has no video stream.");
        if (clip.ReplacedAudio is not { } replaced)
            return new Item(clip, source, media, video,
                !args.NoAudio && media.Audio(source.AudioStreamIndex) is { } own ? new HeardAudio(source, media, own) : null);
        var file = state.AudioFile(replaced.FileId)
            ?? throw new ExportPlanException("A clip plays an audio file that is no longer in the project.");
        if (!mediaById.TryGetValue(file.Id, out var fileMedia))
            throw new ExportPlanException($"\"{file.DisplayName}\" is missing or hasn't been analyzed yet.");
        var stream = fileMedia.Audio(file.AudioStreamIndex);
        if (stream is null && !args.NoAudio)
            throw new ExportPlanException($"\"{file.DisplayName}\" has no audio stream.");
        return new Item(clip, source, media, video, args.NoAudio ? null : new HeardAudio(file, fileMedia, stream!));
    }

    /// <summary>The A2 clips heard on the timeline, with their files; none when the preset drops audio.</summary>
    private static List<TrackItem> ResolveTrack(ProjectState state, IReadOnlyDictionary<string, MediaInfo> mediaById, PresetArguments args)
    {
        var track = new List<TrackItem>();
        if (args.NoAudio)
            return track;
        // What starts after the timeline ends isn't heard, so its file doesn't matter.
        foreach (var clip in state.AudioTrack.Where(c => c.Start < state.Timeline.Duration))
        {
            var file = state.AudioFile(clip.FileId)
                ?? throw new ExportPlanException("An A2 clip plays an audio file that is no longer in the project.");
            if (!mediaById.TryGetValue(file.Id, out var media))
                throw new ExportPlanException($"\"{file.DisplayName}\" is missing or hasn't been analyzed yet.");
            var stream = media.Audio(file.AudioStreamIndex)
                ?? throw new ExportPlanException($"\"{file.DisplayName}\" has no audio stream.");
            track.Add(new TrackItem(clip, new HeardAudio(file, media, stream)));
        }
        return track;
    }

    private static IEnumerable<string> TrackNotes(Context ctx)
    {
        if (ctx.HasTrack)
            yield return $"A2 is mixed in: {ctx.Track.Count} clip(s).";
        var left = ctx.Args.NoAudio ? 0 : ctx.Job.State.AudioTrack.Count - ctx.Track.Count;
        if (left > 0)
            yield return $"{left} A2 clip(s) start after the timeline ends and are left out.";
    }

    private static bool IsWholeFile(Item item) =>
        item.Clip.In <= Tolerance && item.Clip.Out >= item.Media.Duration - Tolerance;

    // ---- Passthrough -----------------------------------------------------------------------------------

    private static ExportPlan Passthrough(Context ctx)
    {
        var item = ctx.Items[0];
        var a = new List<string>(Prefix) { "-i", item.Source.Path, "-map", $"0:{item.Video.Index}" };
        if (item.Audio is not null)
            a.AddRange(["-map", $"0:{item.Audio.Index}"]);
        if (ctx.VideoFilter is { } videoFilter)
            a.AddRange(["-vf", videoFilter]);
        if (ctx.Args.AudioFilter is not null && item.Audio is not null)
            a.AddRange(["-af", ctx.Args.AudioFilter]);
        a.AddRange(ctx.Args.OutputTokens);
        a.AddRange(ContainerFlags(ctx, IsHevc(ctx.Args, item)));
        a.Add(ctx.Out);
        var note = ctx.Crop is null ? "Whole file, no edits: the preset runs unchanged." : "Whole file, cropped: the preset runs on it.";
        return new ExportPlan(ExportMode.Passthrough, [new ExportStep("Exporting", a, item.Media.Duration, true)], [], item.Media.Duration, [note]);
    }

    // ---- Encode ----------------------------------------------------------------------------------------

    private static ExportPlan Encode(Context ctx)
    {
        var notes = new List<string>();
        var inputs = new List<string>();
        var graph = new StringBuilder();
        var reference = ctx.Items[0].Video;
        var norm = VideoNormalization(ctx);
        var audioFormat = ctx.AudioFormat;
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
            AppendTrack(graph, ctx, [(TimeSpan.Zero, ctx.Job.State.Timeline.Duration)], audioFormat!.Value, ref audioInput, inputs);
        }

        AppendConcat(graph, n, video: true, audio: ctx.AnyAudio, ctx.VideoFilter, ctx.Args.AudioFilter, track: ctx.HasTrack);

        var a = new List<string>(Prefix);
        a.AddRange(inputs);
        a.AddRange(["-filter_complex", graph.ToString(), "-map", "[vout]"]);
        if (ctx.AnyAudio)
            a.AddRange(["-map", "[aout]"]);
        a.AddRange(AudioTokens(ctx, ctx.AnyAudio, notes, AudioReencodeReason(ctx, otherwise: "the edit has cuts")));
        a.AddRange(ContainerFlags(ctx, IsHevc(ctx.Args, ctx.Items[0])));
        a.Add(ctx.Out);

        if (norm.Length > 0)
            notes.Add($"Clips differ in size, frame rate or pixel format: all are converted to {ctx.Frame.Width}×{ctx.Frame.Height} like the first clip.");
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
        var copyAudio = ctx.AnyAudio && ctx.Args.AudioCopy && sameAudio && ctx.CopyBlocker is null;

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
            var audioFormat = ctx.AudioFormat!.Value;
            var audioInput = 1;
            for (var i = 0; i < ctx.Items.Count; i++)
            {
                var item = ctx.Items[i];
                graph.Append(AudioChain(item, starts[i], item.Clip.Out - starts[i], audioFormat, ref audioInput, inputs)).Append($"[a{i}];");
            }
            // Each clip starts at its keyframe, a little before its in point, so A2 under it starts that much earlier too.
            var bounds = ctx.Job.State.Timeline.Bounds;
            AppendTrack(graph, ctx, [.. ctx.Items.Select((item, i) => (bounds[i] - (item.Clip.In - starts[i]), bounds[i + 1]))],
                audioFormat, ref audioInput, inputs);
            AppendConcat(graph, ctx.Items.Count, video: false, audio: true, null, ctx.Args.AudioFilter, audioOut: "ashift", track: ctx.HasTrack);
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

    /// <summary>
    /// The container the audio is cut into: Matroska carries every codec. AAC goes into NUT instead, because since ffmpeg 9
    /// Matroska stores AAC's encoder delay (priming) as CodecDelay even for a cut from mid-file, and reading it back drops
    /// the first packet and plays the audio a packet early. NUT can't carry every codec (ALAC comes back as an unknown
    /// codec), so it's only for AAC.
    /// </summary>
    internal static (string Extension, string Format) AudioCutContainer(string codec) =>
        codec == "aac" ? ("nut", "nut") : ("mka", "matroska");

    private static ExportPlan? SingleClipAudioCopy(Context ctx)
    {
        var item = ctx.Items[0];
        var heard = item.Heard!;
        var clip = item.Clip;
        if (heard.Layout(clip) is not AudioLayout.Placed { Placement: { IsSilent: false } placement })
            return null;
        var (lead, readFrom, readLength, _) = placement;

        var preSeek = readFrom > AudioPreSeek ? readFrom - AudioPreSeek : TimeSpan.Zero;
        var (extension, format) = AudioCutContainer(heard.Stream.Codec);
        var tmp = Path.Combine(ctx.Job.WorkDirectory, "audio." + extension);

        var extract = new List<string>(Prefix)
        {
            "-ss", TimeFormat.Seconds(preSeek), "-i", heard.File.Path, "-map", $"0:{heard.Stream.Index}", "-c", "copy",
            "-ss", TimeFormat.Seconds(readFrom - preSeek), "-t", TimeFormat.Seconds(readLength), "-f", format, tmp,
        };

        var mux = new List<string>(Prefix);
        var trim = Input(mux, item, clip.In, clip.Duration);
        // The copied cut starts at the first whole packet at or after readFrom, and its timestamps say how far after
        // (up to a packet: 21 ms of AAC, 85 ms of ALAC). ffmpeg moves an input's start to zero, which would play the
        // audio that much early; "-seek_timestamp 1 -ss 0" keeps its timestamps as they are.
        mux.AddRange(["-seek_timestamp", "1", "-ss", "0"]);
        if (lead > TimeSpan.Zero)
            mux.AddRange(["-itsoffset", TimeFormat.Seconds(lead)]);
        mux.AddRange(["-i", tmp, "-map", $"0:{item.Video.Index}", "-map", "1:a:0"]);
        var notes = new List<string> { "Audio is copied without re-encoding (cut separately, then muxed)." };
        var videoFilters = new List<string>();
        if (trim.Length > 0)
            videoFilters.Add($"trim={trim}duration={TimeFormat.Seconds(clip.Duration)},setpts=PTS-STARTPTS");
        if (ctx.VideoFilter is { } videoFilter)
            videoFilters.Add(videoFilter);
        if (videoFilters.Count > 0)
            mux.AddRange(["-vf", string.Join(',', videoFilters)]);
        mux.AddRange(ctx.Args.OutputTokens);
        mux.AddRange(ContainerFlags(ctx, IsHevc(ctx.Args, item)));
        mux.AddRange(["-t", TimeFormat.Seconds(clip.Duration), ctx.Out]);

        return new ExportPlan(ExportMode.SingleClipAudioCopy,
            [new ExportStep("Cutting audio", extract, readLength, false), new ExportStep("Exporting", mux, clip.Duration, true)],
            [], clip.Duration, notes);
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
        var chain = HeardChain(item, videoStart, length, format, ref nextInput, inputs);
        return item.Heard is null || Gain.IsUnity(item.Clip.Volume) ? chain : chain + VolumeFilter(item.Clip.Volume);
    }

    private static string HeardChain(Item item, TimeSpan videoStart, TimeSpan length, (int Rate, string Layout) format,
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

    /// <summary>
    /// Joins the clips' [vN] and [aN], mixes [atrack] (the A2 track) into the joined audio when <paramref name="track"/> is
    /// set, then runs <paramref name="videoFilter"/> and <paramref name="audioFilter"/> on the result.
    /// </summary>
    private static void AppendConcat(StringBuilder graph, int n, bool video, bool audio, string? videoFilter, string? audioFilter,
        string audioOut = "aout", bool track = false)
    {
        for (var i = 0; i < n; i++)
        {
            if (video)
                graph.Append($"[v{i}]");
            if (audio)
                graph.Append($"[a{i}]");
        }
        var vLabel = videoFilter is null ? "vout" : "vcat";
        var aLabel = audioFilter is null ? audioOut : "acat";
        graph.Append($"concat=n={n}:v={(video ? 1 : 0)}:a={(audio ? 1 : 0)}");
        if (video)
            graph.Append($"[{vLabel}]");
        if (audio)
            graph.Append(track ? "[amain]" : $"[{aLabel}]");
        if (audio && track)
            graph.Append(';').Append(TimelineMix.Mix("amain", "atrack", aLabel));
        if (video && videoFilter is not null)
            graph.Append($";[vcat]{videoFilter}[vout]");
        if (audio && audioFilter is not null)
            graph.Append($";[acat]{audioFilter}[{audioOut}]");
    }

    /// <summary>Why audio can't be copied: <see cref="Context.CopyBlocker"/>, else <paramref name="otherwise"/>.</summary>
    private static string AudioReencodeReason(Context ctx, string otherwise) => ctx.CopyBlocker ?? otherwise;

    /// <summary>Preset output options, with "-c:a copy" swapped for the fallback when audio is filtered.</summary>
    private static IReadOnlyList<string> AudioTokens(Context ctx, bool audioFiltered, List<string> notes, string reason)
    {
        if (!audioFiltered || !ctx.Args.AudioCopy)
            return ctx.Args.OutputTokens;
        notes.Add($"Audio is re-encoded with \"{ctx.Job.Preset.AudioFallbackArguments}\" because {reason} (stream copy isn't possible).");
        return ctx.Args.WithAudioCodecReplaced(ctx.Fallback);
    }

    /// <summary>Filters that make every clip match the first one, or "" when they already match.</summary>
    private static string VideoNormalization(Context ctx)
    {
        var items = ctx.Items;
        var r = items[0].Video;
        var rateDiffers = items.Any(i => !i.Video.FrameRate.Equivalent(r.FrameRate));
        var formatDiffers = items.Any(i => i.Video.PixelFormat != r.PixelFormat) && r.PixelFormat.Length > 0;
        var sb = new StringBuilder();
        if (rateDiffers)
            sb.Append(",fps=").Append(r.FrameRate.ToString());
        if (ctx.Frame.Normalized)
            sb.Append(',').Append(ctx.Frame.FitFilter);
        if (formatDiffers)
            sb.Append(",format=").Append(r.PixelFormat);
        return sb.ToString();
    }

    /// <summary>
    /// The format every clip's audio (and A2's) is converted to: the highest sample rate and the widest channel layout
    /// among them, so a low-rate or mono audio file never downgrades the others.
    /// </summary>
    private static (int Rate, string Layout)? AudioFormat(IEnumerable<AudioStreamInfo?> heard)
    {
        var streams = heard.OfType<AudioStreamInfo>().ToList();
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
    /// Whether two paths name the same file: compared after resolving symbolic links, and ignoring case where the usual
    /// file systems treat "clip.mp4" and "clip.MP4" as one file (<see cref="IPlatform.PathComparison"/>).
    /// </summary>
    private static bool SameFile(string a, string b) =>
        string.Equals(RealPath(a), RealPath(b), Platform.Current.PathComparison);

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
