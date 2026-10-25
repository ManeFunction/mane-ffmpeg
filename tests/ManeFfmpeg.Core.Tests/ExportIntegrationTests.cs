using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Tests;

/// <summary>Exports real timelines with ffmpeg and checks durations and A/V sync of the result.</summary>
public class ExportIntegrationTests(FfmpegFixture media) : IClassFixture<FfmpegFixture>
{
    private const double FrameTolerance = 0.045;
    private const double BeepTolerance = 0.03;

    private static readonly Preset FastEncode = new()
    {
        Id = "test-encode",
        Name = "fast encode",
        Arguments = "-c:v libx264 -preset ultrafast -crf 30 -c:a copy",
        Extension = "mp4",
    };

    private static Clip C(string source, double inS, double outS, double offsetMs = 0) => new()
    {
        Id = Guid.NewGuid().ToString("N"), SourceId = source,
        In = TimeSpan.FromSeconds(inS), Out = TimeSpan.FromSeconds(outS), AudioOffset = TimeSpan.FromMilliseconds(offsetMs),
    };

    private async Task<(ExportPlan Plan, ExportResult Result, string Output)> ExportAsync(
        Preset preset, string extension, IEnumerable<(string Id, string Path)> sources, params Clip[] clips)
    {
        var infos = new Dictionary<string, MediaInfo>();
        var keyframes = new Dictionary<string, KeyframeIndex>();
        var list = new List<SourceMedia>();
        var kf = new KeyframeService(FfmpegFixture.Tools!);
        foreach (var (id, path) in sources)
        {
            list.Add(new SourceMedia { Id = id, Path = path });
            infos[id] = await media.ProbeAsync(path);
            keyframes[id] = await kf.GetAsync(path, infos[id]);
        }
        var output = Path.Combine(media.Directory, $"out-{Guid.NewGuid():N}.{extension}");
        var work = Path.Combine(media.Directory, "work-" + Guid.NewGuid().ToString("N"));
        var plan = ExportPlanner.Plan(new ExportJob
        {
            State = new ProjectState([.. list], new Timeline([.. clips])),
            Media = infos,
            Preset = preset,
            OutputPath = output,
            WorkDirectory = work,
            Keyframes = keyframes,
        });
        var result = await new ExportRunner(FfmpegFixture.Tools!).RunAsync(plan, output, work, null, CancellationToken.None);
        Assert.True(result.Outcome == ExportOutcome.Succeeded, $"{result.Message}\n{result.Log}\n{plan.CommandText("ffmpeg")}");
        Assert.False(Directory.Exists(work));
        return (plan, result, output);
    }

    /// <summary>
    /// Stream copy keeps a few hundredths of a second before the first keyframe (B-frame delay, AAC priming),
    /// shifting everything equally; compare after removing that common shift.
    /// </summary>
    private static void AssertCopyTimes(double[] expectedFlashes, double[] expectedBeeps, List<double> flashes, List<double> beeps)
    {
        Assert.NotEmpty(flashes);
        var shift = flashes[0] - expectedFlashes[0];
        Assert.InRange(shift, -0.01, 0.12);
        AssertTimes(expectedFlashes.Select(f => f + shift), flashes, FrameTolerance, $"flashes (shift {shift:0.000})");
        AssertTimes(expectedBeeps.Select(b => b + shift), beeps, BeepTolerance, $"beeps (shift {shift:0.000})");
    }

    private static void AssertTimes(IEnumerable<double> expected, IReadOnlyList<double> actual, double tolerance, string what)
    {
        var e = expected.ToList();
        Assert.True(e.Count == actual.Count, $"{what}: expected [{string.Join(", ", e)}] but got [{string.Join(", ", actual)}]");
        for (var i = 0; i < e.Count; i++)
            Assert.True(Math.Abs(e[i] - actual[i]) <= tolerance, $"{what}: expected [{string.Join(", ", e)}] but got [{string.Join(", ", actual)}]");
    }

    [FfmpegFact]
    public async Task Encode_reorders_mixed_sources_and_keeps_offsets_in_sync()
    {
        // a: 3.5–7.0 (flashes at 4, 6), b: 1.0–3.5 with audio 120 ms late, c (720p25): 2.5–5.1 with audio 80 ms early.
        var (plan, _, output) = await ExportAsync(FastEncode, "mp4", [("a", media.Sync30), ("c", media.Sync25)],
            C("a", 3.5, 7.0), C("a", 1.0, 3.5, +120), C("c", 2.5, 5.1, -80));

        Assert.Equal(ExportMode.Encode, plan.Mode);
        Assert.Contains(plan.Notes, n => n.Contains("re-encoded"));
        var (flashes, beeps, duration) = await media.MeasureAsync(output);
        Assert.InRange(duration, 8.55, 8.65);
        AssertTimes([0.5, 2.5, 4.5, 7.5], flashes, FrameTolerance, "flashes");
        AssertTimes([0.5, 2.5, 4.62, 7.42], beeps, BeepTolerance, "beeps");
        var info = await media.ProbeAsync(output);
        Assert.Equal(640, info.Video!.Width);
    }

    [FfmpegFact]
    public async Task Built_in_for_storage_preset_encodes_hevc_with_quicktime_tag()
    {
        var (plan, _, output) = await ExportAsync(Preset.ForStorage, "mp4", [("a", media.Sync30)], C("a", 3.5, 6.0), C("a", 1.0, 2.5));
        Assert.Contains("hvc1", plan.Steps[0].Arguments);
        var info = await media.ProbeAsync(output);
        Assert.Equal("hevc", info.Video!.Codec);
        Assert.Equal("yuv420p10le", info.Video.PixelFormat);
        Assert.InRange(info.Duration.TotalSeconds, 3.95, 4.05);
    }

    [FfmpegFact]
    public async Task Plane_copy_cuts_on_keyframes_and_copies_audio_without_offsets()
    {
        var (plan, _, output) = await ExportAsync(Preset.PlaneCopy, "mp4", [("a", media.Sync30)], C("a", 3.0, 7.0), C("a", 1.0, 3.5));
        Assert.Equal(ExportMode.StreamCopy, plan.Mode);
        Assert.Contains("0:1", plan.Steps[0].Arguments);
        var (flashes, beeps, duration) = await media.MeasureAsync(output);
        Assert.InRange(duration, 6.45, 6.65);
        AssertCopyTimes([1.0, 3.0, 5.0], [1.0, 3.0, 5.0], flashes, beeps);
    }

    [FfmpegFact]
    public async Task Plane_copy_with_offsets_reencodes_audio_only()
    {
        var (plan, _, output) = await ExportAsync(Preset.PlaneCopy, "mp4", [("a", media.Sync30)],
            C("a", 3.0, 7.0), C("a", 1.0, 3.5, +120), C("a", 5.0, 7.5, -80));
        Assert.Equal(ExportMode.StreamCopy, plan.Mode);
        Assert.Contains(plan.Notes, n => n.Contains("offsets"));
        var (flashes, beeps, duration) = await media.MeasureAsync(output);
        Assert.InRange(duration, 8.95, 9.15);
        AssertCopyTimes([1.0, 3.0, 5.0, 7.5], [1.0, 3.0, 5.12, 7.42], flashes, beeps);
        var info = await media.ProbeAsync(output);
        Assert.Equal("h264", info.Video!.Codec);
    }

    [FfmpegFact]
    public async Task Plane_copy_snaps_in_points_to_keyframes()
    {
        var (plan, _, output) = await ExportAsync(Preset.PlaneCopy, "mp4", [("a", media.Sync30)], C("a", 3.5, 7.0));
        Assert.Contains(plan.Notes, n => n.Contains("keyframes"));
        var info = await media.ProbeAsync(output);
        Assert.InRange(info.Duration.TotalSeconds, 3.95, 4.15);
    }

    [FfmpegFact]
    public async Task Single_clip_keeps_audio_lossless_with_offset()
    {
        var (plan, _, output) = await ExportAsync(FastEncode, "mp4", [("a", media.Sync30)], C("a", 3.5, 8.0, +120));
        Assert.Equal(ExportMode.SingleClipAudioCopy, plan.Mode);
        var (flashes, beeps, _) = await media.MeasureAsync(output);
        AssertTimes([0.5, 2.5], flashes, FrameTolerance, "flashes");
        AssertTimes([0.62, 2.62], beeps, BeepTolerance, "beeps");
        Assert.Equal("aac", (await media.ProbeAsync(output)).AudioStreams[0].Codec);
    }

    [FfmpegFact]
    public async Task Single_clip_offset_past_file_start_adds_leading_silence()
    {
        var (plan, _, output) = await ExportAsync(FastEncode, "mp4", [("a", media.Sync30)], C("a", 0.0, 4.0, +500));
        Assert.Equal(ExportMode.SingleClipAudioCopy, plan.Mode);
        var (_, beeps, _) = await media.MeasureAsync(output);
        AssertTimes([0.5, 2.5], beeps, BeepTolerance, "beeps");
    }

    [FfmpegFact]
    public async Task Whole_file_runs_preset_unchanged()
    {
        var (plan, _, output) = await ExportAsync(Preset.PlaneCopy, "mp4", [("a", media.Sync30)], C("a", 0, 12));
        Assert.Equal(ExportMode.Passthrough, plan.Mode);
        Assert.Contains("copy", plan.Steps[0].Arguments);
        Assert.InRange((await media.ProbeAsync(output)).Duration.TotalSeconds, 11.9, 12.1);
    }

    [FfmpegFact]
    public async Task Clips_without_audio_get_silence()
    {
        var (_, _, output) = await ExportAsync(FastEncode, "mkv", [("a", media.Sync30), ("n", media.NoAudio)],
            C("n", 1.0, 3.0), C("a", 3.5, 5.0));
        var (flashes, beeps, duration) = await media.MeasureAsync(output);
        Assert.InRange(duration, 3.45, 3.6);
        AssertTimes([1.0, 2.5], flashes, FrameTolerance, "flashes");
        AssertTimes([2.5], beeps, BeepTolerance, "beeps");
    }

    [FfmpegFact]
    public async Task Transport_stream_with_nonzero_start_time_cuts_correctly()
    {
        var info = await media.ProbeAsync(media.TransportStream);
        Assert.True(info.StartTime > TimeSpan.Zero);
        var (encodePlan, _, encoded) = await ExportAsync(FastEncode, "mp4", [("t", media.TransportStream)], C("t", 1.5, 4.5));
        var (flashes, _, _) = await media.MeasureAsync(encoded);
        AssertTimes([0.5, 2.5], flashes, FrameTolerance, "encoded flashes\n" + encodePlan.CommandText("ffmpeg"));
        Assert.Equal(ExportMode.SingleClipAudioCopy, encodePlan.Mode);

        // TS video starts ~21 ms after the container start; this file has a keyframe at 1.1213 s.
        var (copyPlan, _, copied) = await ExportAsync(Preset.PlaneCopy, "ts", [("t", media.TransportStream)], C("t", 1.13, 4.5));
        Assert.Equal(2, copyPlan.Steps.Count);
        var (copiedFlashes, copiedBeeps, _) = await media.MeasureAsync(copied);
        AssertCopyTimes([0.9, 2.9], [0.9, 2.9], copiedFlashes, copiedBeeps);
    }

    [FfmpegFact]
    public async Task Transport_stream_plane_copy_with_offset_keeps_the_first_gop()
    {
        // The offset forces filtered audio, so each segment is cut from the video stream alone.
        var (plan, _, output) = await ExportAsync(Preset.PlaneCopy, "mkv", [("t", media.TransportStream)], C("t", 1.13, 4.5, +120));
        Assert.Equal(ExportMode.StreamCopy, plan.Mode);
        var (flashes, beeps, duration) = await media.MeasureAsync(output);
        Assert.InRange(duration, 3.3, 3.5);
        AssertCopyTimes([0.9, 2.9], [1.02, 3.02], flashes, beeps);
    }

    [FfmpegFact]
    public async Task Cancel_removes_partial_output()
    {
        var infos = new Dictionary<string, MediaInfo> { ["a"] = await media.ProbeAsync(media.Sync30) };
        var output = Path.Combine(media.Directory, "cancelled.mp4");
        var work = Path.Combine(media.Directory, "work-cancel");
        var slow = Preset.ForStorage with { Arguments = "-c:v libx265 -preset veryslow -c:a copy" };
        var plan = ExportPlanner.Plan(new ExportJob
        {
            State = new ProjectState([new SourceMedia { Id = "a", Path = media.Sync30 }], new Timeline([C("a", 0.5, 11.0), C("a", 0, 0.4)])),
            Media = infos, Preset = slow, OutputPath = output, WorkDirectory = work,
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        var progress = new List<ExportProgress>();
        var result = await new ExportRunner(FfmpegFixture.Tools!).RunAsync(plan, output, work, new Progress<ExportProgress>(progress.Add), cts.Token);
        Assert.Equal(ExportOutcome.Cancelled, result.Outcome);
        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(work));
    }

    [FfmpegFact]
    public async Task Refuses_to_overwrite_a_source()
    {
        var infos = new Dictionary<string, MediaInfo> { ["a"] = await media.ProbeAsync(media.Sync30) };
        void Refused(string output)
        {
            var ex = Assert.Throws<ExportPlanException>(() => ExportPlanner.Plan(new ExportJob
            {
                State = new ProjectState([new SourceMedia { Id = "a", Path = media.Sync30 }], new Timeline([C("a", 1, 2)])),
                Media = infos, Preset = FastEncode, OutputPath = output, WorkDirectory = "/tmp/x",
            }));
            Assert.Contains("source", ex.Message);
        }
        Refused(media.Sync30);
        var link = Path.Combine(media.Directory, "link-to-source.mp4");
        File.CreateSymbolicLink(link, media.Sync30);
        Refused(link);
        if (OperatingSystem.IsMacOS())
            Refused(Path.ChangeExtension(media.Sync30, ".MP4"));
    }

    [FfmpegFact]
    public async Task A_file_replaced_in_place_is_analyzed_again()
    {
        var path = Path.Combine(media.Directory, "replaced.mp4");
        File.Copy(media.Sync30, path, overwrite: true);
        var keyframes = new KeyframeService(FfmpegFixture.Tools!);
        var waveforms = new WaveformService(FfmpegFixture.Tools!);
        var before = await media.ProbeAsync(path);
        Assert.Equal(12, (await keyframes.GetAsync(path, before)).Times.Count);
        Assert.NotNull(await waveforms.GetAsync(path, before, before.AudioStreams[0].Index));

        File.Copy(media.NoAudio, path, overwrite: true);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        var after = await media.ProbeAsync(path);
        Assert.NotEqual(before.Stamp, after.Stamp);
        Assert.Null(keyframes.TryGet(path, after));
        var fresh = await new KeyframeService(FfmpegFixture.Tools!).GetAsync(media.NoAudio, await media.ProbeAsync(media.NoAudio));
        Assert.Equal(fresh.Times, (await keyframes.GetAsync(path, after)).Times);
        Assert.Equal(12, keyframes.TryGet(path, before)!.Times.Count);
    }

    [FfmpegFact]
    public async Task Waveform_and_keyframes_are_read()
    {
        var info = await media.ProbeAsync(media.Sync30);
        var keyframes = await new KeyframeService(FfmpegFixture.Tools!).GetAsync(media.Sync30, info);
        Assert.Equal(12, keyframes.Times.Count);
        Assert.Equal(TimeSpan.Zero, keyframes.Times[0]);
        Assert.InRange(keyframes.Times[5].TotalSeconds, 4.99, 5.01);
        Assert.InRange(keyframes.ReorderDelay.TotalSeconds, 0.03, 0.1);

        var waveform = await new WaveformService(FfmpegFixture.Tools!).GetAsync(media.Sync30, info, info.AudioStreams[0].Index);
        Assert.NotNull(waveform);
        Assert.InRange(waveform!.Duration.TotalSeconds, 11.9, 12.1);
        Assert.True(waveform.Max(TimeSpan.FromSeconds(2.0), TimeSpan.FromSeconds(2.1)) > 150);
        Assert.True(waveform.Max(TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(3.5)) < 10);
    }
}
