using System.Diagnostics;
using System.Globalization;
using CliWrap;
using ManeFfmpeg.Core.Mpv;

namespace ManeFfmpeg.Core.Tests;

public class LevelMeterTests
{
    [Fact]
    public void Peaks_are_read_per_channel_in_order_with_silence_as_minus_infinity()
    {
        var reading = LevelMeter.Parse("""{"lavfi.astats.2.Peak_level":"-inf","lavfi.astats.1.Peak_level":"-18.063656","lavfi.astats.3.Peak_level":"0.000000"}""")!;
        Assert.Equal(3, reading.Channels);
        Assert.Equal(-18.063656, reading.PeaksDb[0], 6);
        Assert.Equal(double.NegativeInfinity, reading.PeaksDb[1]);
        Assert.False(reading.Clipped(0));
        Assert.True(reading.Clipped(2));
        Assert.Null(LevelMeter.Parse("{}"));
    }

    [Fact]
    public void Readings_are_held_back_by_the_latency_and_none_is_lost_between_takes()
    {
        var metadata = "";
        var second = Stopwatch.Frequency;
        using var meter = new LevelMeter(() => metadata, TimeSpan.FromSeconds(0.2), polls: false);
        meter.Running = true;
        var channels = 0;
        meter.ChannelsChanged += c => channels = c;

        metadata = Peaks(-20, -30);
        meter.Poll(0);
        metadata = Peaks(-3, -40);
        meter.Poll(second / 100);
        metadata = Peaks(-10, -35);
        meter.Poll(second / 50);
        // A steady sound: the same peaks, but a new frame.
        metadata = Peaks(-10, -35, 3);
        meter.Poll(second / 40);
        Assert.Equal(2, channels);

        Assert.Null(meter.Take(second / 10));
        var heard = meter.Take(second / 5 + second / 100)!;
        Assert.Equal([-3, -30], heard.PeaksDb);
        Assert.Equal([-10, -35], meter.Take(second)!.PeaksDb);
        // The same frame read again is not a new one.
        meter.Poll(second);
        Assert.Null(meter.Take(2 * second));
        Assert.Null(meter.Take(3 * second));
    }

    [Fact]
    public void A_pause_or_a_seek_drops_what_was_not_heard_yet()
    {
        var metadata = Peaks(-6, -6);
        using var meter = new LevelMeter(() => metadata, TimeSpan.FromSeconds(0.2), polls: false);
        meter.Running = true;
        // What the filter last measured before playback started was measured already.
        meter.Poll(0);
        Assert.Null(meter.Take(Stopwatch.Frequency));

        metadata = Peaks(-1, -1);
        meter.Poll(0);
        meter.Reset();
        Assert.Null(meter.Take(Stopwatch.Frequency));

        metadata = Peaks(-2, -2);
        meter.Poll(0);
        meter.Running = false;
        meter.Poll(0);
        Assert.Null(meter.Take(Stopwatch.Frequency));
    }

    private static string Peaks(double left, double right, int frame = 0) =>
        "{\"lavfi.astats.Overall.Number_of_samples\":\"" + frame * 1600 + "\",\"lavfi.astats.1.Peak_level\":\"" + left.ToString("0.000000", CultureInfo.InvariantCulture)
        + "\",\"lavfi.astats.2.Peak_level\":\"" + right.ToString("0.000000", CultureInfo.InvariantCulture) + "\"}";
}

/// <summary>The meter against a headless libmpv playing a file with known levels.</summary>
public class LevelMeterPlaybackTests(FfmpegFixture media) : IClassFixture<FfmpegFixture>
{
    [MpvFact]
    public async Task Playback_is_measured_per_channel_and_a_full_scale_channel_clips()
    {
        // Left: a sine at full scale (stored as 16 bits, its top is −0.0003 dB); right: a sine at −18 dB.
        var path = Path.Combine(media.Directory, "levels.mka");
        await Cli.Wrap(FfmpegFixture.Tools!.Ffmpeg).WithArguments(["-nostdin", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "sine=f=440:d=4:sample_rate=48000", "-f", "lavfi", "-i", "sine=f=660:d=4:sample_rate=48000",
            "-filter_complex", "[0]volume=8[l];[l][1]join=inputs=2:channel_layout=stereo[a]", "-map", "[a]", "-c:a", "pcm_s16le", path]).ExecuteAsync();
        using var player = PreviewPlayer.Create(hardwareDecoding: false, headless: true);
        Assert.Equal(LevelMeter.Filter.Split(':')[0], player.Mpv.GetString("af")?.Split(':')[0]);
        player.LoadSource(path, null);
        await Wait.Until(() => player.Mpv.GetDouble("duration") is > 3, "the file");
        player.Paused = false;

        LevelReading? loudest = null;
        await Wait.Until(() =>
        {
            if (player.Levels.Take() is { } reading)
                loudest = loudest is null ? reading : new LevelReading([.. reading.PeaksDb.Zip(loudest.PeaksDb, Math.Max)]);
            return loudest is { Channels: 2 } l && l.Clipped(0);
        }, "a clipping left channel");
        Assert.Equal(2, player.Levels.Channels);
        Assert.False(loudest!.Clipped(1));
        Assert.InRange(loudest.PeaksDb[1], -18.5, -17.5);
    }
}
