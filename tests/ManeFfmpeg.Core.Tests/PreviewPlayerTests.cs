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
        else if (MpvLibrary.RelaunchEnvironment(null) is { } fallback)
            Skip = $"libmpv needs DYLD_FALLBACK_LIBRARY_PATH={fallback.Split(':')[0]} for the test run";
        else if (!MpvLibrary.TryLoad(null))
            Skip = $"libmpv not found: {MpvLibrary.LoadError}";
    }
}

/// <summary>The preview player against a headless libmpv: tracks, position across reloads, compact mode, seeks.</summary>
public class PreviewPlayerTests(FfmpegFixture media) : IClassFixture<FfmpegFixture>
{
    private static bool Near(double? seconds, double expected) => seconds is { } s && Math.Abs(s - expected) < 0.05;

    private async Task<string> TimelineUrl(params (double In, double Out)[] clips)
    {
        var info = await media.ProbeAsync(media.Sync30);
        var state = new ProjectState([new SourceMedia { Id = "s", Path = media.Sync30 }],
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
}
