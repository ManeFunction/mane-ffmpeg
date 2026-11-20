using System.Globalization;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Tests;

/// <summary>
/// Exports with clip volumes and A2 through real ffmpeg: where the A2 beep lands among the clips' beeps, and how loud
/// a turned-down clip comes out.
/// </summary>
public partial class MixIntegrationTests(FfmpegFixture media) : IClassFixture<FfmpegFixture>
{
    private const double Tolerance = 0.03;

    private static readonly Preset FastEncode = new()
    {
        Id = "test-encode", Name = "fast encode", Arguments = "-c:v libx264 -preset ultrafast -crf 30 -c:a copy", Extension = "mp4",
    };

    private static Clip C(double inS, double outS, double volume = 1) =>
        Clip.Create("v", TimeSpan.FromSeconds(inS), TimeSpan.FromSeconds(outS)) with { Volume = volume };

    /// <summary>The fixture's tone (a beep 0.3 s in) on A2 from <paramref name="start"/>.</summary>
    private static TrackClip Tone(double start, double volume = 1) =>
        TrackClip.Create("tone", TimeSpan.FromSeconds(start), TimeSpan.Zero, TimeSpan.FromSeconds(3)) with { Volume = volume };

    private async Task<(ExportPlan Plan, string Output)> ExportAsync(Preset preset, string extension, string video, TrackClip[] track, params Clip[] clips)
    {
        var info = await media.ProbeAsync(video);
        var infos = new Dictionary<string, MediaInfo> { ["v"] = info, ["tone"] = await media.ProbeAsync(media.Tone) };
        var output = Path.Combine(media.Directory, $"mix-{Guid.NewGuid():N}.{extension}");
        var plan = ExportPlanner.Plan(new ExportJob
        {
            State = new ProjectState([new SourceMedia { Id = "v", Path = video }], new Timeline([.. clips]),
                [new SourceMedia { Id = "tone", Path = media.Tone }]) { AudioTrack = [.. track] },
            Media = infos,
            Preset = preset,
            OutputPath = output,
            WorkDirectory = Path.Combine(media.Directory, "work-" + Guid.NewGuid().ToString("N")),
            Keyframes = new Dictionary<string, KeyframeIndex> { ["v"] = await new KeyframeService(FfmpegFixture.Tools!).GetAsync(video, info) },
        });
        var result = await new ExportRunner(FfmpegFixture.Tools!).RunAsync(plan, null, CancellationToken.None);
        Assert.True(result.Outcome == ExportOutcome.Succeeded, $"{result.Message}\n{result.Log}\n{plan.CommandText("ffmpeg")}");
        return (plan, output);
    }

    private static void AssertTimes(double[] expected, IReadOnlyList<double> actual, string what, double shift = 0)
    {
        var message = $"{what}: expected [{string.Join(", ", expected)}] (+{shift:0.000}) but got [{string.Join(", ", actual)}]";
        Assert.True(expected.Length == actual.Count, message);
        for (var i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] + shift - actual[i]) <= Tolerance, message);
    }

    [FfmpegFact]
    public async Task A2_is_heard_where_it_was_placed_over_a_muted_clip()
    {
        var (plan, output) = await ExportAsync(FastEncode, "mp4", media.Sync30, [Tone(2.0)], C(0.5, 6.5, volume: 0));
        Assert.Equal(ExportMode.Encode, plan.Mode);
        var (flashes, beeps, duration) = await media.MeasureAsync(output);
        AssertTimes([1.5, 3.5, 5.5], flashes, "flashes");
        // The clip's own beeps (1.5, 3.5, 5.5) are muted; the tone's beep is 0.3 s into it.
        AssertTimes([2.3], beeps, "beeps");
        Assert.InRange(duration, 5.95, 6.1);
    }

    [FfmpegFact]
    public async Task A2_adds_to_the_clips_own_sound_and_stops_with_the_timeline()
    {
        // The tone at 5.9 would beep at 6.2, after the 6 s timeline ends.
        var (_, output) = await ExportAsync(FastEncode, "mp4", media.Sync30, [Tone(2.5), Tone(5.9)], C(0.5, 6.5));
        var (_, beeps, duration) = await media.MeasureAsync(output);
        AssertTimes([1.5, 2.8, 3.5, 5.5], beeps, "beeps");
        Assert.InRange(duration, 5.95, 6.1);
    }

    [FfmpegFact]
    public async Task Stream_copy_mixes_A2_in_step_with_the_copied_clips()
    {
        // Keyframes every second: the clips start exactly at their in points. Timeline: 1–3 s then 5–6.5 s of the
        // source, so its beeps (at 2 and 6 s) land at 1 and 3; the tone on A2 from 2.2 beeps at 2.5.
        var (plan, output) = await ExportAsync(Preset.PlaneCopy, "mp4", media.Sync30, [Tone(2.2)], C(1, 3), C(5, 6.5));
        Assert.Equal(ExportMode.StreamCopy, plan.Mode);
        var (flashes, beeps, _) = await media.MeasureAsync(output);
        Assert.NotEmpty(flashes);
        // Stream copy keeps a few hundredths of a second before the first keyframe; everything shifts by it.
        var shift = flashes[0] - 1;
        Assert.InRange(shift, -0.01, 0.12);
        AssertTimes([1, 2.5, 3], beeps, "beeps", shift);
    }

    [FfmpegFact]
    public async Task Audio_presets_save_the_mix_and_a_turned_down_clip_is_that_much_quieter()
    {
        var (plan, output) = await ExportAsync(Preset.ExtractAudio, "flac", media.Sync30, [Tone(4.5)], C(0, 6, volume: 0.5));
        Assert.Equal(ExportMode.AudioEncode, plan.Mode);
        var (beeps, duration) = await media.MeasureAudioAsync(output);
        AssertTimes([2, 4, 4.8], beeps, "beeps");
        Assert.InRange(duration, 5.95, 6.05);

        // Only the clip, at half volume: about 6 dB below the source.
        var (_, quiet) = await ExportAsync(Preset.ExtractAudio, "flac", media.Sync30, [], C(0, 6, volume: 0.5));
        var drop = await MaxVolumeAsync(media.Sync30) - await MaxVolumeAsync(quiet);
        Assert.InRange(drop, 5.5, 6.5);
    }

    private static async Task<double> MaxVolumeAsync(string path)
    {
        var result = await Cli.Wrap(FfmpegFixture.Tools!.Ffmpeg)
            .WithArguments(["-nostdin", "-hide_banner", "-i", path, "-map", "0:a:0", "-t", "6", "-af", "volumedetect", "-f", "null", "-"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        return double.Parse(MaxVolume().Match(result.StandardError).Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"max_volume: (-?[0-9.]+) dB")]
    private static partial Regex MaxVolume();
}
