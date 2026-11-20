using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Tests;

public class ClipDetectorTests
{
    [Fact]
    public void Clipped_frames_close_together_make_one_range_and_far_apart_two()
    {
        var detector = new ClipDetector(1000, 2);
        var samples = new float[2 * 2000];
        // Right channel over at 0.100 s and 0.200 s (one range, 100 ms apart), left at exactly full scale at 1.5 s.
        samples[2 * 100 + 1] = 1.4f;
        samples[2 * 200 + 1] = -1.1f;
        samples[2 * 1500] = 1f;
        samples[2 * 1700] = 0.99f; // below −0.01 dBFS
        detector.Add(samples.AsSpan(0, 2 * 700));
        detector.Add(samples.AsSpan(2 * 700));
        var report = detector.Finish();

        Assert.Equal(2, report.Ranges.Count);
        Assert.Equal(TimeSpan.FromSeconds(0.1), report.Ranges[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(0.201), report.Ranges[0].End);
        Assert.Equal(20 * Math.Log10(1.4), report.Ranges[0].PeakDb, 3);
        Assert.Equal(TimeSpan.FromSeconds(1.5), report.Ranges[1].Start);
        Assert.Equal(0, report.Ranges[1].PeakDb, 3);
        Assert.Equal(20 * Math.Log10(1.4), report.PeakDb, 3);
        Assert.Equal(TimeSpan.FromSeconds(2), report.Scanned);
    }

    [Fact]
    public void Quiet_audio_has_no_ranges()
    {
        var detector = new ClipDetector(48000, 1);
        detector.Add(Enumerable.Repeat(0.5f, 48000).ToArray());
        var report = detector.Finish();
        Assert.False(report.Clips);
        Assert.Equal(20 * Math.Log10(0.5), report.PeakDb, 3);
    }
}

/// <summary>Scans through real ffmpeg: the fixture's beeps (0.5 peak) clip only when the volumes push them past full scale.</summary>
public class ClippingScanTests(FfmpegFixture media) : IClassFixture<FfmpegFixture>
{
    private async Task<ClippingReport> ScanAsync(ProjectState state)
    {
        var infos = new Dictionary<string, MediaInfo> { ["v"] = await media.ProbeAsync(media.Sync30), ["tone"] = await media.ProbeAsync(media.Tone) };
        var plan = ExportPlanner.AudioScan(state, infos)!;
        Assert.Contains("-vn", plan.Arguments);
        return await new ClippingScanner(FfmpegFixture.Tools!).ScanAsync(plan, null, CancellationToken.None);
    }

    private ProjectState State(double clipVolume, double a1 = 1, params TrackClip[] track) =>
        new ProjectState([new SourceMedia { Id = "v", Path = media.Sync30 }],
            new Timeline([Clip.Create("v", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(7)) with { Volume = clipVolume }]),
            [new SourceMedia { Id = "tone", Path = media.Tone }]) { AudioTrack = [.. track], A1Volume = a1 };

    [FfmpegFact]
    public async Task Untouched_volumes_dont_clip()
    {
        var report = await ScanAsync(State(1));
        Assert.False(report.Clips);
        Assert.InRange(report.PeakDb, -7.5, -5);
        Assert.InRange(report.Scanned.TotalSeconds, 5.99, 6.01);
    }

    [FfmpegFact]
    public async Task Clip_and_track_volumes_together_clip_every_beep_at_its_timeline_time()
    {
        // Source beeps at 2, 4 and 6 s land at 1, 3 and 5 on the timeline; 2 × 1.5 = 3× the 0.5 peak.
        var report = await ScanAsync(State(2, a1: 1.5));
        Assert.Equal(3, report.Ranges.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.InRange(report.Ranges[i].Start.TotalSeconds, 1 + 2 * i - 0.03, 1 + 2 * i + 0.03);
            Assert.InRange(report.Ranges[i].End.TotalSeconds, 1.1 + 2 * i - 0.03, 1.1 + 2 * i + 0.03);
        }
        Assert.InRange(report.PeakDb, 3, 4);
    }

    [FfmpegFact]
    public async Task A_loud_A2_clip_clips_where_it_plays()
    {
        // The tone beeps 0.3 s in, at 0.5 × 2 × 1.5 (its own volume, then A2's).
        var tone = TrackClip.Create("tone", TimeSpan.FromSeconds(2), TimeSpan.Zero, TimeSpan.FromSeconds(3)) with { Volume = 2 };
        var report = await ScanAsync(State(1, 1, tone) with { A2Volume = 1.5 });
        var range = Assert.Single(report.Ranges);
        Assert.InRange(range.Start.TotalSeconds, 2.27, 2.33);
    }
}
