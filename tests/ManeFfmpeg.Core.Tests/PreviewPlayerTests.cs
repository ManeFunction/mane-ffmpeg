using System.Collections.Concurrent;
using CliWrap;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Mpv;

namespace ManeFfmpeg.Core.Tests;

/// <summary>
/// Skips the test unless libmpv can be loaded here. IINA's bundled copy only works when the test run starts
/// with DYLD_FALLBACK_LIBRARY_PATH pointing at its Frameworks folder (the app relaunches itself for that).
/// </summary>
public sealed class MpvFactAttribute : FactAttribute
{
    public MpvFactAttribute()
    {
        if (FfmpegFixture.Tools is null)
            Skip = "ffmpeg/ffprobe not found";
        else if (MpvLibrary.RelaunchEnvironment(null) is { } environment)
            Skip = $"libmpv needs {string.Join(" ", environment.Select(e => $"{e.Key}={e.Value.Split(':')[0]}"))} for the test run";
        else if (!MpvLibrary.TryLoad(null))
            Skip = $"libmpv not found: {MpvLibrary.LoadError}";
    }
}

/// <summary>The preview player against a headless libmpv: tracks, position across reloads, compact mode, seeks.</summary>
public class PreviewPlayerTests(FfmpegFixture media) : IClassFixture<FfmpegFixture>
{
    private static bool Near(double? seconds, double expected) => seconds is { } s && Math.Abs(s - expected) < 0.05;

    private Task<string> TimelineUrl(params (double In, double Out)[] clips) => TimelineUrl(media.Sync30, clips);

    /// <summary>A timeline of <paramref name="path"/>, described with the probe of the sync file (so it may be missing).</summary>
    private async Task<string> TimelineUrl(string path, params (double In, double Out)[] clips)
    {
        var info = await media.ProbeAsync(media.Sync30);
        var state = new ProjectState([new SourceMedia { Id = "s", Path = path }],
            new Timeline([.. clips.Select(c => Clip.Create("s", TimeSpan.FromSeconds(c.In), TimeSpan.FromSeconds(c.Out)))]));
        return TimelineEdl.Build(state, new Dictionary<string, MediaInfo> { ["s"] = info }, "/nonexistent/silence.mka")!;
    }

    [MpvFact]
    public async Task Timeline_plays_its_titled_tracks_and_keeps_the_position_across_reloads()
    {
        using var player = PreviewPlayer.Create(hardwareDecoding: false, headless: true);
        player.LoadTimeline(await TimelineUrl((1, 3), (5, 8)), TimeSpan.FromSeconds(2.5));
        await Wait.Until(() => player.Mpv.GetString("current-tracks/audio/title") == TimelineEdl.AudioTitle, "the timeline audio track");
        Assert.Equal(TimelineEdl.VideoTitle, player.Mpv.GetString("current-tracks/video/title"));
        await Wait.Until(() => Near(player.Mpv.GetDouble("time-pos"), 2.5), "the start position");
        Assert.True(player.IsTimelineLoaded);

        player.LoadTimeline(await TimelineUrl((1, 3), (6, 8)), TimeSpan.FromSeconds(3.25));
        await Wait.Until(() => Near(player.Mpv.GetDouble("time-pos"), 3.25), "the position after reloading");

        player.Compact = true;
        await Wait.Until(() => player.Mpv.GetString("vid") == "no", "video off in compact mode");
        Assert.Equal(TimelineEdl.AudioTitle, player.Mpv.GetString("current-tracks/audio/title"));
    }

    [MpvFact]
    public async Task Compact_mode_switched_before_the_timeline_loads_still_lets_it_play()
    {
        using var player = PreviewPlayer.Create(hardwareDecoding: false, headless: true);
        // While a project is still being analyzed nothing is loaded yet.
        player.Compact = true;
        player.LoadTimeline(await TimelineUrl((1, 3), (5, 8)), TimeSpan.FromSeconds(1));
        await Wait.Until(() => player.Mpv.GetString("current-tracks/audio/title") == TimelineEdl.AudioTitle, "the timeline audio track");
        Assert.Equal("no", player.Mpv.GetString("vid"));

        player.Compact = false;
        await Wait.Until(() => player.Mpv.GetString("current-tracks/video/title") == TimelineEdl.VideoTitle, "the timeline video track");
    }

    [MpvFact]
    public async Task A_timeline_that_fails_to_load_is_tried_again_next_time()
    {
        using var player = PreviewPlayer.Create(hardwareDecoding: false, headless: true);
        var failures = 0;
        player.LoadFailed += _ => Interlocked.Increment(ref failures);
        var missing = await TimelineUrl(Path.Combine(media.Directory, "missing.mp4"), (1, 3));

        player.LoadTimeline(missing, TimeSpan.Zero);
        await Wait.Until(() => failures == 1, "the load failing");
        Assert.False(player.IsTimelineLoaded);

        player.LoadTimeline(missing, TimeSpan.Zero);
        await Wait.Until(() => failures == 2, "the second try failing");
        player.LoadTimeline(await TimelineUrl((1, 3)), TimeSpan.Zero);
        await Wait.Until(() => player.Mpv.GetString("current-tracks/audio/title") == TimelineEdl.AudioTitle, "the playable timeline");
        Assert.True(player.IsTimelineLoaded);
    }

    [MpvFact]
    public async Task The_timeline_plays_A2_mixed_in_at_the_clips_volumes()
    {
        var info = await media.ProbeAsync(media.Sync30);
        var tone = await media.ProbeAsync(media.Tone);
        // Two clips: 0.5–2.5 s muted (its beep at 2 s would sound at 1.5), 2.5–4.5 s as it is (its beep at 4 s sounds at 3.5).
        var muted = Clip.Create("s", TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2.5)) with { Volume = 0 };
        var clip = Clip.Create("s", TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(4.5));
        var state = new ProjectState([new SourceMedia { Id = "s", Path = media.Sync30 }], new Timeline([muted, clip]),
            [new SourceMedia { Id = "t", Path = media.Tone }])
        {
            AudioTrack = [TrackClip.Create("t", TimeSpan.FromSeconds(2), TimeSpan.Zero, TimeSpan.FromSeconds(3))],
        };
        var silence = await ManeFfmpeg.Core.Media.SilenceAsset.EnsureAsync(FfmpegFixture.Tools!);
        var url = TimelineEdl.Build(state, new Dictionary<string, MediaInfo> { ["s"] = info, ["t"] = tone }, silence)!;
        var wav = Path.Combine(media.Directory, $"preview-{Guid.NewGuid():N}.wav");

        using var player = PreviewPlayer.Create(hardwareDecoding: false, headless: true);
        // Whatever mpv plays is written to a file, so it can be measured like an export.
        player.Mpv.SetString("ao", "pcm");
        player.Mpv.SetString("ao-pcm-file", wav);
        player.LoadTimeline(url, TimeSpan.Zero, TimelineMix.For(state));
        // Without a synchronization context the player's events run on mpv's thread: let each graph settle (and its
        // seek back to the start finish) before the next change.
        async Task Settled(string what) =>
            await Wait.Until(() => player.Mpv.GetString("seeking") == "no" && Near(player.Mpv.GetDouble("time-pos"), 0), what);
        await Wait.Until(() => player.Mpv.GetString("lavfi-complex")?.Contains("amix") == true, "the mix graph");
        await Settled("the start after the graph is set");
        Assert.Contains("volume='0+1*gte(t,2)'[a1]", player.Mpv.GetString("lavfi-complex"));

        // The same timeline with another volume changes only the graph, in place.
        player.ApplyMix(TimelineMix.For(state with { Timeline = new Timeline([muted with { Volume = 0.5 }, clip]) }));
        Assert.Contains("volume='0.5+0.5*gte(t,2)'[a1]", player.Mpv.GetString("lavfi-complex"));
        player.LoadTimeline(PreviewTimeline.For(state, new Dictionary<string, MediaInfo> { ["s"] = info, ["t"] = tone }, silence)!, TimeSpan.Zero);
        await Settled("the start after the graph changed back");

        player.Paused = false;
        await Wait.Until(() => player.Mpv.GetDouble("time-pos") > 3.9, "playing to the end");
        player.Unload();
        await Wait.Until(() => File.Exists(wav) && new FileInfo(wav).Length > 0, "the recorded audio");
        await Task.Delay(300);
        // The muted clip's beep is gone; the A2 tone beeps 0.3 s after it starts at 2, 1.2 s before the second clip's.
        // (Under load mpv pads the start of the recording with silence while video catches up, so only the gap between
        // the beeps is compared; Settled checks that setting the graph didn't move the position.)
        var (beeps, _) = await media.MeasureAudioAsync(wav);
        Assert.True(beeps.Count == 2 && Math.Abs(beeps[1] - beeps[0] - 1.2) < 0.05, $"beeps at {string.Join(", ", beeps)}");
    }

    [MpvFact]
    public async Task Reloading_a_mixed_timeline_keeps_the_position_even_far_from_a_keyframe()
    {
        // One keyframe every 10 s: mpv's own restart after a graph change would land on the one at 10 s.
        var path = Path.Combine(media.Directory, "gop10.mp4");
        await Cli.Wrap(FfmpegFixture.Tools!.Ffmpeg)
            .WithArguments([.. FfmpegTools.Quiet, "-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-f", "lavfi", "-i", "sine",
                "-t", "14", "-c:v", "libx264", "-preset", "veryfast", "-g", "300", "-pix_fmt", "yuv420p", "-c:a", "aac", path])
            .ExecuteAsync();
        var media2 = new Dictionary<string, MediaInfo> { ["s"] = await media.ProbeAsync(path), ["t"] = await media.ProbeAsync(media.Tone) };
        var silence = await ManeFfmpeg.Core.Media.SilenceAsset.EnsureAsync(FfmpegFixture.Tools!);
        ProjectState At(double a2) => new([new SourceMedia { Id = "s", Path = path }], new Timeline([Clip.Create("s", TimeSpan.Zero, TimeSpan.FromSeconds(14))]),
            [new SourceMedia { Id = "t", Path = media.Tone }]) { AudioTrack = [TrackClip.Create("t", TimeSpan.FromSeconds(a2), TimeSpan.Zero, TimeSpan.FromSeconds(3))] };

        using var player = PreviewPlayer.Create(hardwareDecoding: false, headless: true);
        var first = At(3);
        player.LoadTimeline(TimelineEdl.Build(first, media2, silence)!, TimeSpan.FromSeconds(5.2), TimelineMix.For(first));
        await Wait.Until(() => player.Mpv.GetString("lavfi-complex")?.Contains("amix") == true, "the mix graph");
        await Wait.Until(() => player.Mpv.GetString("seeking") == "no" && Near(player.Mpv.GetDouble("time-pos"), 5.2), "the start position");

        // An A2 clip moved: a new EDL, reloaded at the playhead.
        var moved = At(7);
        player.LoadTimeline(TimelineEdl.Build(moved, media2, silence)!, TimeSpan.FromSeconds(5.2), TimelineMix.For(moved));
        await Wait.Until(() => player.Mpv.GetString("lavfi-complex")?.Contains("amix") == true && player.Mpv.GetString("seeking") == "no", "the reload");
        await Task.Delay(500);
        Assert.True(Near(player.Mpv.GetDouble("time-pos"), 5.2), $"position after the reload: {player.Mpv.GetDouble("time-pos")}");
    }

    [MpvFact]
    public async Task A2_stays_in_time_with_A1_after_loading_or_seeking_and_nothing_plays_twice()
    {
        // After a seek each input of the mix arrives from wherever its file's seek landed; in a downloaded WebM, mpv
        // read A1 from up to a minute before the target while A2 came on time, and amix (which joins by arrival)
        // played A2 that much early. The graph therefore cuts both inputs at the position; this checks the cut and that
        // A2 lands between A1's beeps. (This WebM seeks exactly; the cut is what makes it independent of that.) Starting
        // playback after a paused seek rebuilds the graph, and playback waits for the seek back, so no audio repeats.
        var path = Path.Combine(media.Directory, "nocues.webm");
        await using (var webm = File.Create(path))
            await Cli.Wrap(FfmpegFixture.Tools!.Ffmpeg)
                .WithArguments([.. FfmpegTools.Quiet, "-f", "lavfi", "-i", "color=c=black:s=320x180:r=30", "-f", "lavfi",
                    "-i", "aevalsrc='if(lt(mod(t\\,2)\\,0.1)\\,0.5*sin(2*PI*1000*t)\\,0)':s=48000:c=stereo",
                    "-t", "20", "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-g", "300", "-c:a", "libopus", "-f", "webm", "pipe:1"])
                .WithStandardOutputPipe(PipeTarget.ToStream(webm))
                .ExecuteAsync();
        var media2 = new Dictionary<string, MediaInfo> { ["s"] = await media.ProbeAsync(path), ["t"] = await media.ProbeAsync(media.Tone) };
        var silence = await ManeFfmpeg.Core.Media.SilenceAsset.EnsureAsync(FfmpegFixture.Tools!);
        // The tone on A2 from 15 s beeps at 15.3 s, between A1's beeps at 14 and 16 s.
        var state = new ProjectState([new SourceMedia { Id = "s", Path = path }], new Timeline([Clip.Create("s", TimeSpan.Zero, TimeSpan.FromSeconds(20))]),
            [new SourceMedia { Id = "t", Path = media.Tone }]) { AudioTrack = [TrackClip.Create("t", TimeSpan.FromSeconds(15), TimeSpan.Zero, TimeSpan.FromSeconds(3))] };
        var url = TimelineEdl.Build(state, media2, silence)!;

        foreach (var seekFirst in new[] { false, true })
        {
            var wav = Path.Combine(media.Directory, $"aligned-{Guid.NewGuid():N}.wav");
            using var player = PreviewPlayer.Create(hardwareDecoding: false, headless: true);
            player.Mpv.SetString("ao", "pcm");
            player.Mpv.SetString("ao-pcm-file", wav);
            player.LoadTimeline(url, TimeSpan.FromSeconds(seekFirst ? 2 : 13.2), TimelineMix.For(state));
            await Wait.Until(() => player.Mpv.GetString("lavfi-complex")?.Contains("amix") == true, "the mix graph");
            if (seekFirst)
            {
                // A click on the ruler while paused, then play.
                await Wait.Until(() => player.Mpv.GetString("seeking") == "no" && Near(player.Mpv.GetDouble("time-pos"), 2), "the first position");
                player.Seek(TimeSpan.FromSeconds(13.2));
            }
            await Wait.Until(() => player.Mpv.GetString("seeking") == "no" && Near(player.Mpv.GetDouble("time-pos"), 13.2), "13.2 s");
            player.Paused = false;
            await Wait.Until(() => player.Mpv.GetString("lavfi-complex")?.Contains("atrim=start=13.2,") == true, "the mix cut at 13.2 s");
            await Wait.Until(() => player.Mpv.GetDouble("time-pos") > 16.6, "playing past 16.6 s");
            player.Unload();
            await Wait.Until(() => File.Exists(wav) && new FileInfo(wav).Length > 0, "the recorded audio");
            await Task.Delay(300);
            // A1 at 14, A2 at 15.3, A1 at 16: compared as gaps from the first beep.
            var (beeps, _) = await media.MeasureAudioAsync(wav);
            var gaps = beeps.Select(b => b - beeps.FirstOrDefault()).ToList();
            Assert.True(gaps.Count == 3 && Math.Abs(gaps[1] - 1.3) < 0.05 && Math.Abs(gaps[2] - 2) < 0.05,
                $"{(seekFirst ? "after a seek" : "from the start")}: beeps at {string.Join(", ", beeps)}");
        }
    }

    /// <summary>Holds posted callbacks until <see cref="RunAll"/>, like a busy UI thread.</summary>
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public void RunAll()
        {
            while (_queue.TryDequeue(out var item))
                item.Callback(item.State);
        }
    }

    [MpvFact]
    public async Task Positions_still_queued_from_the_replaced_timeline_are_not_reported()
    {
        var context = new QueuedContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        PreviewPlayer created;
        try
        {
            created = PreviewPlayer.Create(hardwareDecoding: false, headless: true);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        using var player = created;
        var reported = new List<double>();
        player.PositionChanged += p => reported.Add(p.TotalSeconds);
        player.LoadTimeline(await TimelineUrl((0, 10)), TimeSpan.FromSeconds(6));
        player.Paused = false;
        await Wait.Until(() => player.Mpv.GetDouble("time-pos") > 6.2, "the first timeline playing");
        context.RunAll();
        Assert.Contains(reported, p => p > 6);

        // Positions from the first timeline queue up, then the second one replaces it before they are handled.
        var queuedFrom = player.Mpv.GetDouble("time-pos");
        await Wait.Until(() => player.Mpv.GetDouble("time-pos") > queuedFrom + 0.2, "positions queuing up");
        player.LoadTimeline(await TimelineUrl((0, 4)), TimeSpan.FromSeconds(1));
        reported.Clear();
        await Wait.Until(() =>
        {
            context.RunAll();
            return reported.Count > 0;
        }, "a position from the second timeline");
        Assert.All(reported, p => Assert.InRange(p, 0.9, 3));
    }

    [MpvFact]
    public async Task Source_preview_plays_the_chosen_audio_stream_and_rapid_seeks_land_on_the_last()
    {
        var path = Path.Combine(media.Directory, "two-audio.mkv");
        await Cli.Wrap(FfmpegFixture.Tools!.Ffmpeg)
            .WithArguments([.. FfmpegTools.Quiet, "-y", "-i", media.Sync30, "-map", "0:v", "-map", "0:a", "-map", "0:a", "-c", "copy", path])
            .ExecuteAsync();
        using var player = PreviewPlayer.Create(hardwareDecoding: false, headless: true);
        player.LoadSource(path, audioStreamIndex: 2);
        await Wait.Until(() => player.Mpv.GetInt("current-tracks/audio/ff-index") == 2, "the second audio stream");
        Assert.False(player.IsTimelineLoaded);

        foreach (var seconds in new[] { 1.0, 2.0, 3.0, 4.5 })
            player.Seek(TimeSpan.FromSeconds(seconds));
        await Wait.Until(() => Near(player.Mpv.GetDouble("time-pos"), 4.5), "the last seek");
    }

    [MpvFact]
    public async Task Scrubbing_a_long_gop_timeline_shows_frames_while_the_pointer_still_moves()
    {
        // One GOP for the whole file, so each exact seek decodes from its start (and brings the audio along), which
        // takes far longer than the pointer takes to move again.
        var path = Path.Combine(media.Directory, "long-gop.mp4");
        await Cli.Wrap(FfmpegFixture.Tools!.Ffmpeg)
            .WithArguments([.. FfmpegTools.Quiet, "-y", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30", "-f", "lavfi", "-i", "sine",
                "-t", "10", "-c:v", "libx264", "-preset", "veryfast", "-g", "300", "-pix_fmt", "yuv420p", "-c:a", "aac", path])
            .ExecuteAsync();
        var state = new ProjectState([new SourceMedia { Id = "s", Path = path }],
            new Timeline([Clip.Create("s", TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(9.5))]));
        var url = TimelineEdl.Build(state, new Dictionary<string, MediaInfo> { ["s"] = await media.ProbeAsync(path) }, "/nonexistent/silence.mka")!;
        using var player = PreviewPlayer.Create(hardwareDecoding: false, headless: true);
        player.LoadTimeline(url, TimeSpan.Zero);
        await Wait.Until(() => player.Mpv.GetString("current-tracks/audio/title") == TimelineEdl.AudioTitle, "the timeline");
        await Wait.Until(() => Near(player.Mpv.GetDouble("time-pos"), 0), "the first frame");

        var shown = new ConcurrentQueue<double>();
        var dragging = true;
        player.Mpv.PlaybackRestarted += () =>
        {
            if (dragging && player.Mpv.GetDouble("time-pos") is { } position)
                shown.Enqueue(position);
        };
        // Drag back and forth between 1 s and 8 s, a seek per pointer move, until three frames have been shown.
        // (Each frame takes as long as its decode, which depends on what else the machine is doing.)
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (shown.Count(p => p is > 1.2 and < 8) < 3 && clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            var t = clock.Elapsed.TotalSeconds * 3.5 % 14;
            player.Seek(TimeSpan.FromSeconds(1 + (t < 7 ? t : 14 - t)));
            await Task.Delay(8);
        }
        dragging = false;
        player.Seek(TimeSpan.FromSeconds(8.5));

        Assert.True(shown.Count(p => p is > 1.2 and < 8) >= 3, $"frames shown during a {clock.Elapsed.TotalSeconds:F0} s drag: {string.Join(", ", shown)}");
        await Wait.Until(() => Near(player.Mpv.GetDouble("time-pos"), 8.5), "the frame under the released pointer");
    }
}
