using CliWrap;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Tests;

/// <summary>A project's source catalog over real ffmpeg analyses of the fixture media.</summary>
public class SourceCatalogTests(FfmpegFixture media) : IClassFixture<FfmpegFixture>
{
    private sealed class Analyzers(FfmpegTools tools) : IMediaAnalyzers
    {
        public Task<MediaInfo> ProbeAsync(string path) => new Ffprobe(tools).ProbeAsync(path);
        public WaveformService? Waveforms { get; } = new(tools);
        public KeyframeService? Keyframes { get; } = new(tools);
    }

    private static SourceCatalog Catalog() => new(new Analyzers(FfmpegFixture.Tools!));

    [FfmpegFact]
    public async Task A_missing_file_is_reported_and_relinking_analyzes_it()
    {
        var catalog = Catalog();
        var changed = new List<string>();
        catalog.Changed += changed.Add;
        var missing = new SourceMedia { Id = "s", Path = Path.Combine(media.Directory, "gone.mp4") };
        catalog.Sync([missing]);
        Assert.Equal(SourceStatus.Missing, catalog.Status("s"));
        Assert.Empty(catalog.Media);

        catalog.Sync([missing with { Path = media.Sync30 }]);
        await Wait.Until(() => catalog.Status("s") == SourceStatus.Ready, "the relinked file to be analyzed");
        Assert.Equal(12, catalog.Info("s")!.Duration.TotalSeconds, 0);
        Assert.Contains("s", catalog.Media.Keys);
        Assert.All(changed, id => Assert.Equal("s", id));
    }

    [FfmpegFact]
    public async Task Choosing_another_audio_stream_loads_its_waveform()
    {
        // Stream 1 beeps, stream 2 is silent.
        var path = Path.Combine(media.Directory, "beep-and-silence.mkv");
        await Cli.Wrap(FfmpegFixture.Tools!.Ffmpeg)
            .WithArguments([.. FfmpegTools.Quiet, "-y", "-i", media.Sync30, "-f", "lavfi", "-t", "12", "-i", "anullsrc=r=48000:cl=stereo",
                "-map", "0:v", "-map", "0:a", "-map", "1:a", "-c:v", "copy", "-c:a", "flac", path])
            .ExecuteAsync();
        var catalog = Catalog();
        var source = new SourceMedia { Id = "s", Path = path, AudioStreamIndex = 0 };
        catalog.Sync([source]);
        await Wait.Until(() => catalog.Waveform(source) is not null, "the first stream's waveform");
        Assert.True(catalog.Waveform(source)!.Max(TimeSpan.FromSeconds(2.0), TimeSpan.FromSeconds(2.1)) > 150);

        var second = source with { AudioStreamIndex = 1 };
        catalog.Sync([second]);
        Assert.Equal(2, catalog.Audio(second)!.Index);
        await Wait.Until(() => catalog.Waveform(second) is not null, "the second stream's waveform");
        Assert.True(catalog.Waveform(second)!.Max(TimeSpan.FromSeconds(2.0), TimeSpan.FromSeconds(2.1)) < 10);
    }

    [FfmpegFact]
    public async Task Keyframes_are_scanned_when_wanted_or_asked_for()
    {
        var catalog = Catalog();
        var source = new SourceMedia { Id = "s", Path = media.Sync30 };
        catalog.Sync([source]);
        await Wait.Until(() => catalog.Waveform(source) is not null, "the analysis");
        Assert.Null(catalog.Keyframes(source));

        catalog.WantKeyframes = true;
        await Wait.Until(() => catalog.Keyframes(source) is not null, "keyframes once wanted");
        Assert.Equal(12, (await catalog.KeyframesAsync(source)).Times.Count);

        var other = new SourceMedia { Id = "n", Path = media.NoAudio };
        var lazy = Catalog();
        lazy.Sync([other]);
        await Wait.Until(() => lazy.Status("n") == SourceStatus.Ready, "the second catalog");
        Assert.NotEmpty((await lazy.KeyframesAsync(other)).Times);
        Assert.NotNull(lazy.Keyframes(other));
    }

    [FfmpegFact]
    public async Task A_removed_source_stays_forgotten_when_its_probe_finishes_late()
    {
        var catalog = Catalog();
        catalog.Sync([new SourceMedia { Id = "s", Path = media.Sync25 }]);
        catalog.Sync([]);
        await Task.Delay(1500);
        Assert.Empty(catalog.Media);
        Assert.Null(catalog.Info("s"));
    }
}
