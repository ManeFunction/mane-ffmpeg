using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Tests;

/// <summary>The export planner on synthetic media, without ffmpeg: mode choice, refusals and command snapshots.</summary>
public class ExportPlannerTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static MediaInfo Media(double seconds, string format = "mov,mp4,m4a,3gp,3g2,mj2", double start = 0, string codec = "h264") => new()
    {
        Duration = S(seconds), StartTime = S(start), FormatName = format,
        Video = new VideoStreamInfo { Index = 0, Codec = codec, Width = 1920, Height = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p" },
        AudioStreams = [new AudioStreamInfo { Index = 1, Codec = "aac", SampleRate = 48000, Channels = 2, ChannelLayout = "stereo", Duration = S(seconds) }],
    };

    private static readonly KeyframeIndex EverySecond = new([.. Enumerable.Range(0, 20).Select(i => S(i))], TimeSpan.FromTicks(TimeSpan.TicksPerSecond * 2 / 30));

    private static Clip C(string source, double inS, double outS, double offsetMs = 0) =>
        Clip.Create(source, S(inS), S(outS)) with { AudioOffset = TimeSpan.FromMilliseconds(offsetMs) };

    private static ExportJob Job(Preset preset, IReadOnlyDictionary<string, MediaInfo> media, params Clip[] clips) => new()
    {
        State = new ProjectState([.. media.Keys.Select(id => new SourceMedia { Id = id, Path = $"/m/{id}.src" })], new Timeline([.. clips])),
        Media = media,
        Preset = preset,
        OutputPath = "/out/edit.mp4",
        WorkDirectory = "/work",
        Keyframes = media.Keys.ToDictionary(id => id, _ => EverySecond),
    };

    private static readonly Preset Encode = new() { Id = "e", Name = "encode", Arguments = "-c:v libx264 -crf 20 -c:a copy", Extension = "mp4" };

    [Theory]
    [InlineData("whole", "encode", ExportMode.Passthrough)]
    [InlineData("trimmed", "encode", ExportMode.SingleClipAudioCopy)]
    [InlineData("silent", "encode", ExportMode.Encode)]
    [InlineData("two", "encode", ExportMode.Encode)]
    [InlineData("two", "copy", ExportMode.StreamCopy)]
    [InlineData("whole", "copy", ExportMode.Passthrough)]
    public void Picks_the_mode_from_the_edit_and_the_preset(string edit, string preset, ExportMode expected)
    {
        var media = new Dictionary<string, MediaInfo> { ["a"] = Media(10) };
        Clip[] clips = edit switch
        {
            "whole" => [C("a", 0, 10)],
            "trimmed" => [C("a", 1, 4, 200)],
            // Audio slipped past the whole clip: nothing to copy, so it's silence from the filter graph.
            "silent" => [C("a", 1, 4, -20000)],
            _ => [C("a", 1, 3), C("a", 5, 6)],
        };
        var plan = ExportPlanner.Plan(Job(preset == "copy" ? Preset.PlaneCopy : Encode, media, clips));
        Assert.Equal(expected, plan.Mode);
        Assert.Equal("/out/edit.mp4", plan.OutputPath);
        Assert.Equal("/work", plan.WorkDirectory);
    }

    [Fact]
    public void Stream_copy_copies_matching_audio_and_shifts_filtered_audio()
    {
        var media = new Dictionary<string, MediaInfo> { ["a"] = Media(10) };
        var copied = ExportPlanner.Plan(Job(Preset.PlaneCopy, media, C("a", 1, 3), C("a", 5, 6)));
        Assert.Null(copied.Steps[^1].Shift);
        Assert.Contains("0:1", copied.Steps[^1].Arguments);

        var shifted = ExportPlanner.Plan(Job(Preset.PlaneCopy, media, C("a", 1, 3), C("a", 5, 6, 40)));
        var shift = shifted.Steps[^1].Shift!;
        Assert.Equal("/work/segments.ffconcat", shift.ListPath);
        Assert.Contains(shifted.Steps[^1].Arguments, a => a.Contains("PTS+$SHIFT/TB"));
        Assert.Contains(shift.ArgumentsFor("-0.077000"), a => a.Contains("PTS+-0.077000/TB"));
        Assert.Contains(shifted.Notes, n => n.Contains("offsets"));
    }

    [Fact]
    public void Refuses_what_it_cannot_export()
    {
        var media = new Dictionary<string, MediaInfo> { ["a"] = Media(10), ["b"] = Media(10, codec: "hevc") };
        string Refusal(ExportJob job) => Assert.Throws<ExportPlanException>(() => ExportPlanner.Plan(job)).Message;

        Assert.Contains("empty", Refusal(Job(Encode, media)));
        Assert.Contains("same format", Refusal(Job(Preset.PlaneCopy, media, C("a", 1, 3), C("b", 1, 3))));
        Assert.Contains("analyzed", Refusal(Job(Encode, new Dictionary<string, MediaInfo> { ["a"] = Media(10) }, C("missing", 1, 3)) with
        {
            State = new ProjectState([new SourceMedia { Id = "missing", Path = "/m/missing.src" }], new Timeline([C("missing", 1, 3)])),
        }));
        Assert.Contains("Keyframes", Refusal(Job(Preset.PlaneCopy, media, C("a", 1, 3)) with { Keyframes = new Dictionary<string, KeyframeIndex>() }));
    }

    [Fact]
    public void Encode_snapshot()
    {
        var media = new Dictionary<string, MediaInfo> { ["a"] = Media(10) };
        var plan = ExportPlanner.Plan(Job(Encode, media, C("a", 1, 3), C("a", 5, 6, 120)));
        Assert.Equal("""
            ffmpeg -nostdin -hide_banner -loglevel error -nostats -stats_period 0.5 -progress pipe:1 -y -ss 1.000000 -t 2.000000 -i /m/a.src -ss 5.000000 -t 1.000000 -i /m/a.src -ss 1.000000 -t 2.000000 -i /m/a.src -ss 4.880000 -t 1.000000 -i /m/a.src -filter_complex '[0:0]trim=duration=2.000000,setpts=PTS-STARTPTS[v0];[1:0]trim=duration=1.000000,setpts=PTS-STARTPTS[v1];[2:1]aformat=sample_rates=48000:channel_layouts=stereo,apad=whole_dur=2.000000,atrim=duration=2.000000,asetpts=N/SR/TB[a0];[3:1]aformat=sample_rates=48000:channel_layouts=stereo,apad=whole_dur=1.000000,atrim=duration=1.000000,asetpts=N/SR/TB[a1];[v0][a0][v1][a1]concat=n=2:v=1:a=1[vout][aout]' -map '[vout]' -map '[aout]' -c:v libx264 -crf 20 -c:a aac -b:a 256k -movflags +faststart /out/edit.mp4
            """, plan.CommandText("ffmpeg"));
    }

    [Fact]
    public void Transport_stream_copy_with_an_offset_snapshot()
    {
        // Video-only segments cut half a frame before each keyframe's decode time; audio is filtered and shifted.
        var media = new Dictionary<string, MediaInfo> { ["t"] = Media(10, "mpegts", start: 1.4) };
        var plan = ExportPlanner.Plan(Job(Preset.PlaneCopy, media, C("t", 0, 3), C("t", 5.5, 8, 40)));
        Assert.Equal("""
            # /work/segments.ffconcat:
            # ffconcat version 1.0
            # file '/work/segment000.mkv'
            # duration 3.000000
            # file '/work/segment001.mkv'
            # duration 3.000000

            ffmpeg -nostdin -hide_banner -loglevel error -nostats -stats_period 0.5 -progress pipe:1 -y -ss 0.000000 -i /m/t.src -map 0:0 -c copy -ss -0.083334 -t 3.016666 -f matroska /work/segment000.mkv

            ffmpeg -nostdin -hide_banner -loglevel error -nostats -stats_period 0.5 -progress pipe:1 -y -ss 0.000000 -i /m/t.src -map 0:0 -c copy -ss 4.916666 -t 3.016666 -f matroska /work/segment001.mkv

            # $SHIFT: minus the start time of /work/segments.ffconcat, probed before this runs
            ffmpeg -nostdin -hide_banner -loglevel error -nostats -stats_period 0.5 -progress pipe:1 -y -f concat -safe 0 -i /work/segments.ffconcat -ss 0.000000 -t 3.000000 -i /m/t.src -ss 0.000000 -t 7.960000 -i /m/t.src -filter_complex '[1:1]atrim=start=0.000000:duration=3.000000,asetpts=PTS-STARTPTS,aformat=sample_rates=48000:channel_layouts=stereo,apad=whole_dur=3.000000,atrim=duration=3.000000,asetpts=N/SR/TB[a0];[2:1]atrim=start=4.960000:duration=3.000000,asetpts=PTS-STARTPTS,aformat=sample_rates=48000:channel_layouts=stereo,apad=whole_dur=3.000000,atrim=duration=3.000000,asetpts=N/SR/TB[a1];[a0][a1]concat=n=2:v=0:a=1[ashift];[ashift]asetpts=PTS+$SHIFT/TB[aout]' -map 0:0 -map '[aout]' -c:v copy -c:a aac -b:a 256k -movflags +faststart /out/edit.mp4
            """, plan.CommandText("ffmpeg"));
    }
}
