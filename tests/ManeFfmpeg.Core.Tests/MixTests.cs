using System.Collections.Immutable;
using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Mpv;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Tests;

/// <summary>Clip volumes and the A2 track: editing, layout, the preview's mix, saving and export plans.</summary>
public class MixTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static TrackClip T(string id, double start, double inS, double outS, double volume = 1) =>
        new() { Id = id, FileId = "f", Start = S(start), In = S(inS), Out = S(outS), Volume = volume };

    private static ImmutableList<TrackClip> Track(params TrackClip[] clips) => [.. clips];

    // ---- Editing ------------------------------------------------------------------------------------

    [Fact]
    public void A_dropped_file_fills_at_most_the_gap_it_lands_in()
    {
        var track = Track(T("m", 10, 0, 5));
        var (added, id) = TrackEditor.Add(track, "g", S(4), S(60));
        Assert.NotNull(id);
        Assert.Equal([S(4), S(10)], added.Select(c => c.Start));
        Assert.Equal((S(0), S(6)), (added[0].In, added[0].Out));

        Assert.Null(TrackEditor.Add(track, "g", S(12), S(60)).Id);
        var (after, _) = TrackEditor.Add(track, "g", S(15), S(60));
        Assert.Equal(S(75), after[1].End);
        Assert.Equal(S(0), TrackEditor.Add([], "g", S(-3), S(1)).Track[0].Start);
    }

    [Fact]
    public void Moves_and_trims_stop_at_the_neighbours_and_the_file()
    {
        var track = Track(T("a", 0, 0, 2), T("b", 5, 10, 12), T("c", 9, 0, 1));
        Assert.Equal(S(2), TrackEditor.Move(track, "b", S(1))[1].Start);
        Assert.Equal(S(7), TrackEditor.Move(track, "b", S(8))[1].Start);
        Assert.Equal(S(0), TrackEditor.Move(track, "a", S(-5))[0].Start);
        Assert.Same(track, TrackEditor.Move(track, "a", S(0)));

        var trimmedIn = TrackEditor.TrimIn(track, "b", S(1))[1];
        Assert.Equal((S(2), S(7)), (trimmedIn.Start, trimmedIn.In));
        Assert.Equal(S(12) - TimelineEditor.MinClipDuration, TrackEditor.TrimIn(track, "b", S(20))[1].In);
        Assert.Equal(S(0), TrackEditor.TrimIn(track, "a", S(-1))[0].In);

        Assert.Equal(S(14), TrackEditor.TrimOut(track, "b", S(30), fileEnd: null)[1].Out);
        Assert.Equal(S(13), TrackEditor.TrimOut(track, "b", S(30), fileEnd: S(13))[1].Out);
        Assert.Equal(S(100), TrackEditor.TrimOut(track, "c", S(100), fileEnd: null)[2].Out);
    }

    [Fact]
    public void Splitting_keeps_the_sound_in_place()
    {
        var track = Track(T("a", 2, 10, 20), T("b", 30, 0, 5));
        var split = TrackEditor.Split(track, new HashSet<string> { "a", "b" }, S(6));
        Assert.Equal(3, split.Count);
        Assert.Equal((S(2), S(10), S(14)), (split[0].Start, split[0].In, split[0].Out));
        Assert.Equal((S(6), S(14), S(20)), (split[1].Start, split[1].In, split[1].Out));
        Assert.NotEqual("a", split[1].Id);
        Assert.Same(track, TrackEditor.Split(track, new HashSet<string> { "b" }, S(6)));
    }

    [Fact]
    public void A_damaged_track_is_put_in_order_without_overlaps()
    {
        var track = TrackEditor.Normalize([T("b", 5, 0, 10), T("a", -1, -2, 3, volume: 9), T("c", 6, 0, 1), T("d", 14, 0, 0.01)]);
        Assert.Equal(["a", "b"], track.Select(c => c.Id));
        Assert.Equal((S(0), S(0), Gain.Max), (track[0].Start, track[0].In, track[0].Volume));
        Assert.Equal(S(5), track[1].Start);
    }

    [Fact]
    public void Removing_an_audio_file_takes_its_A2_clips_along()
    {
        var state = new ProjectState([], Timeline.Empty, [new SourceMedia { Id = "f", Path = "/m/song.m4a" }])
        {
            AudioTrack = Track(T("a", 0, 0, 2), T("b", 5, 0, 2)),
        };
        Assert.Equal(2, ProjectEditor.ClipsUsing(state, FileKind.Audio, "f"));
        Assert.Empty(ProjectEditor.RemoveFile(state, FileKind.Audio, "f").AudioTrack);
        Assert.Empty(ProjectEditor.DropOrphans(state with { AudioFiles = [] }).AudioTrack);
    }

    [Fact]
    public void Volume_edits_clamp_and_apply_to_a_whole_video()
    {
        var timeline = new Timeline([Clip.Create("v", S(0), S(2)), Clip.Create("v", S(4), S(6)), Clip.Create("w", S(0), S(1))]);
        var one = TimelineEditor.SetVolume(timeline, new HashSet<string> { timeline.Clips[0].Id }, 5);
        Assert.Equal(Gain.Max, one.Clips[0].Volume);
        var all = TimelineEditor.SetVolumeForSource(timeline, "v", 0.5);
        Assert.Equal([0.5, 0.5, 1], all.Clips.Select(c => c.Volume));

        // A clip of the video whose sound an audio file replaces keeps its volume.
        var replaced = timeline with { Clips = timeline.Clips.SetItem(1, timeline.Clips[1] with { ReplacedAudio = new AudioReplacement { FileId = "f" } }) };
        Assert.Equal([0.5, 1, 1], TimelineEditor.SetVolumeForSource(replaced, "v", 0.5).Clips.Select(c => c.Volume));
        Assert.Equal("80 %", Gain.Percent(0.8));
        Assert.Equal("−6.0 dB", Gain.Decibels(0.501187));
        Assert.Equal("−∞ dB", Gain.Decibels(0));
    }

    [Fact]
    public void An_audio_files_volume_goes_everywhere_it_plays_on_A1_and_A2()
    {
        var timeline = new Timeline([
            Clip.Create("v", S(0), S(2)) with { ReplacedAudio = new AudioReplacement { FileId = "f" } },
            Clip.Create("v", S(2), S(4)),
            Clip.Create("v", S(4), S(6)) with { ReplacedAudio = new AudioReplacement { FileId = "g" } },
        ]);
        var state = new ProjectState([], timeline) { AudioTrack = Track(T("a", 0, 0, 2), T("b", 5, 0, 2) with { FileId = "g" }) };
        var next = ProjectEditor.SetAudioFileVolume(state, "f", 0.3);
        Assert.Equal([0.3, 1, 1], next.Timeline.Clips.Select(c => c.Volume));
        Assert.Equal([0.3, 1], next.AudioTrack.Select(c => c.Volume));
    }

    // ---- Layout -------------------------------------------------------------------------------------

    [Fact]
    public void Track_spans_cover_the_range_with_silence_between_clips_and_past_the_file()
    {
        var track = Track(T("a", 1, 10, 13), T("b", 6, 0, 4));
        var spans = TrackLayout.Spans(track, S(-1), S(8), c => c.Id == "a" ? S(12) : null).ToList();
        Assert.Equal(
            [(S(2), null, S(0)), (S(2), "a", S(10)), (S(1), null, S(0)), (S(2), null, S(0)), (S(2), "b", S(0))],
            spans.Select(s => (s.Length, s.Clip?.Id, s.ReadFrom)));
        Assert.Equal(S(9), spans.Aggregate(TimeSpan.Zero, (t, s) => t + s.Length));

        var inside = TrackLayout.Spans(track, S(2), S(3), _ => null).Single();
        Assert.Equal((S(1), "a", S(11)), (inside.Length, inside.Clip?.Id, inside.ReadFrom));
    }

    // ---- Preview mix --------------------------------------------------------------------------------

    [Fact]
    public void The_preview_mixes_only_when_there_is_something_to_mix()
    {
        var clips = new Timeline([Clip.Create("v", S(0), S(2)), Clip.Create("v", S(4), S(7)), Clip.Create("v", S(0), S(1))]);
        Assert.Null(TimelineMix.For(new ProjectState([], clips)));

        var quieter = clips with { Clips = clips.Clips.SetItem(1, clips.Clips[1] with { Volume = 0.25 }) };
        var mix = TimelineMix.For(new ProjectState([], quieter))!;
        Assert.Equal("[aid2]volume=eval=frame:volume='1-0.75*gte(t,2)+0.75*gte(t,5)'[ao]", mix.Graph("2", null));

        var withTrack = TimelineMix.For(new ProjectState([], clips) { AudioTrack = Track(T("a", 3, 0, 1, 0.5)) })!;
        Assert.Equal("[aid2]volume=1[a1];[aid3]volume=0.5[a2];[a1][a2]amix=inputs=2:normalize=0:duration=first:dropout_transition=0[ao]",
            withTrack.Graph("2", "3"));
        // Without its A2 track (an EDL still loading), A1 plays alone.
        Assert.Equal("[aid2]volume=1[ao]", withTrack.Graph("2", null));
        // From a position, both inputs are cut to start there before they are mixed (they arrive from wherever their
        // files' seeks landed); A1 alone needs no cut, mpv trims it on its own timestamps.
        Assert.Equal("[aid2]atrim=start=83.253,volume=1[a1];[aid3]atrim=start=83.253,volume=0.5[a2];" +
                     "[a1][a2]amix=inputs=2:normalize=0:duration=first:dropout_transition=0[ao]", withTrack.Graph("2", "3", S(83.253)));
        Assert.Equal("[aid2]volume=1[ao]", withTrack.Graph("2", null, S(83.253)));
    }

    [Fact]
    public void Track_volumes_multiply_every_clip_gain_in_the_preview()
    {
        var clips = new Timeline([Clip.Create("v", S(0), S(2)), Clip.Create("v", S(4), S(7)) with { Volume = 0.5 }]);
        var state = new ProjectState([], clips) { AudioTrack = Track(T("a", 3, 0, 1, 1.5)), A1Volume = 0.5, A2Volume = 2 };
        Assert.Equal("[aid2]volume=eval=frame:volume='0.5-0.25*gte(t,2)'[a1];[aid3]volume=3[a2];" +
                     "[a1][a2]amix=inputs=2:normalize=0:duration=first:dropout_transition=0[ao]", TimelineMix.For(state)!.Graph("2", "3"));

        // A track volume alone is enough to mix.
        var plain = new ProjectState([], new Timeline([Clip.Create("v", S(0), S(2))]));
        Assert.Null(TimelineMix.For(plain));
        Assert.Equal("[aid2]volume=0[ao]", TimelineMix.For(plain with { A1Volume = 0 })!.Graph("2", null));
        Assert.Equal(2, (plain with { A1Volume = 5 }).A1Volume);
        // The stored state keeps the volumes apart; only the heard state multiplies them.
        Assert.Equal(0.5, state.Timeline.Clips[1].Volume);
        Assert.Same(plain, plain.WithTrackVolumesApplied());
    }

    [Fact]
    public void The_preview_EDL_carries_A2_padded_to_the_timeline()
    {
        var video = new MediaInfo
        {
            Duration = S(10), FormatName = "mov,mp4,m4a,3gp,3g2,mj2",
            Video = new VideoStreamInfo { Index = 0, Codec = "h264", Width = 640, Height = 360, FrameRate = new Rational(30, 1) },
        };
        var song = new MediaInfo
        {
            Duration = S(30), FormatName = "mp3",
            AudioStreams = [new AudioStreamInfo { Index = 0, Codec = "mp3", SampleRate = 44100, Channels = 2, Duration = S(30) }],
        };
        var state = new ProjectState([new SourceMedia { Id = "v", Path = "/m/v.mp4" }], new Timeline([Clip.Create("v", S(0), S(10))]),
            [new SourceMedia { Id = "f", Path = "/m/song.mp3" }]) { AudioTrack = Track(T("a", 2, 5, 25)) };
        var url = TimelineEdl.Build(state, new Dictionary<string, MediaInfo> { ["v"] = video, ["f"] = song }, "/s.mka")!;
        // The video has no sound, so A1 is silence, there for A2 to be mixed into.
        Assert.EndsWith("!new_stream;!track_meta,title=mane-audio;%6%/s.mka,0.000000,10.000000;" +
                        "!new_stream;!track_meta,title=mane-a2;%6%/s.mka,0.000000,2.000000;%11%/m/song.mp3,5.000000,8.000000", url);
    }

    // ---- Saving -------------------------------------------------------------------------------------

    [Fact]
    public void Volumes_and_A2_are_saved_as_format_4_and_read_back()
    {
        var dir = Directory.CreateTempSubdirectory("mane-mix").FullName;
        try
        {
            var path = Path.Combine(dir, "p.mfvp");
            var plain = new ProjectState([new SourceMedia { Id = "v", Path = "/m/v.mp4" }], new Timeline([Clip.Create("v", S(0), S(10))]),
                [new SourceMedia { Id = "f", Path = "/m/song.mp3" }]);
            Assert.Contains("\"version\": 2", ProjectFile.Serialize(new Project { State = plain }, path));
            // Only the preview's own volume (in the layout): unchanged clips leave theirs out.
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(ProjectFile.Serialize(new Project { State = plain }, path), "\"volume\""));

            var mixed = plain with
            {
                Timeline = new Timeline([plain.Timeline.Clips[0] with { Volume = 0.4 }]),
                AudioTrack = Track(T("a", 2, 5, 25, 1.5)),
            };
            var json = ProjectFile.Serialize(new Project { State = mixed }, path);
            Assert.Contains("\"version\": 4", json);
            var read = ProjectFile.Deserialize(json, path).State;
            Assert.Equal(0.4, read.Timeline.Clips[0].Volume);
            Assert.Equal(mixed.AudioTrack, read.AudioTrack);
            Assert.Equal(mixed, read);

            // A track volume alone needs format 4 too.
            var louder = plain with { A2Volume = 1.25 };
            var louderJson = ProjectFile.Serialize(new Project { State = louder }, path);
            Assert.Contains("\"version\": 4", louderJson);
            Assert.Contains("\"a2Volume\": 1.25", louderJson);
            Assert.DoesNotContain("a1Volume", louderJson);
            Assert.Equal(louder, ProjectFile.Deserialize(louderJson, path).State);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- Export plans -------------------------------------------------------------------------------

    private static MediaInfo Video(double seconds) => new()
    {
        Duration = S(seconds), FormatName = "mov,mp4,m4a,3gp,3g2,mj2",
        Video = new VideoStreamInfo { Index = 0, Codec = "h264", Width = 1920, Height = 1080, FrameRate = new Rational(30, 1), PixelFormat = "yuv420p" },
        AudioStreams = [new AudioStreamInfo { Index = 1, Codec = "aac", SampleRate = 48000, Channels = 2, ChannelLayout = "stereo", Duration = S(seconds) }],
    };

    private static readonly MediaInfo Song = new()
    {
        Duration = S(30), FormatName = "mp3",
        AudioStreams = [new AudioStreamInfo { Index = 0, Codec = "mp3", SampleRate = 44100, Channels = 2, ChannelLayout = "stereo", Duration = S(30) }],
    };

    private static readonly KeyframeIndex EverySecond = new([.. Enumerable.Range(0, 20).Select(i => S(i))], TimeSpan.FromTicks(TimeSpan.TicksPerSecond * 2 / 30));

    private static ExportJob Job(Preset preset, IEnumerable<TrackClip> track, params Clip[] clips) => new()
    {
        State = new ProjectState([new SourceMedia { Id = "v", Path = "/m/v.mp4" }], new Timeline([.. clips]),
            [new SourceMedia { Id = "f", Path = "/m/song.mp3" }]) { AudioTrack = [.. track] },
        Media = new Dictionary<string, MediaInfo> { ["v"] = Video(10), ["f"] = Song },
        Preset = preset,
        OutputPath = preset.Kind == PresetKind.Audio ? "/out/edit.m4a" : "/out/edit.mp4",
        WorkDirectory = "/work",
        Keyframes = new Dictionary<string, KeyframeIndex> { ["v"] = EverySecond },
    };

    private static readonly Preset Encode = new() { Id = "e", Name = "encode", Arguments = "-c:v libx264 -crf 20 -c:a copy", Extension = "mp4" };

    private static string Graph(ExportPlan plan)
    {
        var args = plan.Steps[^1].Arguments.ToList();
        return args[args.IndexOf("-filter_complex") + 1];
    }

    [Fact]
    public void A_changed_volume_is_encoded_where_audio_would_be_copied()
    {
        var whole = Clip.Create("v", S(0), S(10));
        Assert.Equal(ExportMode.Passthrough, ExportPlanner.Plan(Job(Encode, [], whole)).Mode);

        var quiet = ExportPlanner.Plan(Job(Encode, [], whole with { Volume = 0.5 }));
        Assert.Equal(ExportMode.Encode, quiet.Mode);
        Assert.Contains(",volume=0.5[a0]", Graph(quiet));
        Assert.Contains(quiet.Notes, n => n.Contains("volume changed"));

        var copied = ExportPlanner.Plan(Job(Preset.PlaneCopy, [], Clip.Create("v", S(1), S(3)), Clip.Create("v", S(5), S(6)) with { Volume = 2 }));
        Assert.Equal(ExportMode.StreamCopy, copied.Mode);
        Assert.Contains(",volume=2[a1]", Graph(copied));

        var audioOnly = ExportPlanner.Plan(Job(Preset.ExtractAudio, [], whole with { Volume = 0.5 }) with { OutputPath = "/out/edit.flac" });
        Assert.Equal(ExportMode.AudioEncode, audioOnly.Mode);
    }

    [Fact]
    public void Track_volumes_are_exported_as_part_of_each_clip_gain()
    {
        var whole = Clip.Create("v", S(0), S(10));
        var job = Job(Encode, [T("a", 2, 5, 25, 2)], whole with { Volume = 2 });
        var graph = Graph(ExportPlanner.Plan(job with { State = job.State with { A1Volume = 2, A2Volume = 0.25 } }));
        // 200 % on a 200 % track is 400 %; 200 % on a 25 % track is 50 %.
        Assert.Contains(",volume=4[a0]", graph);
        Assert.Contains("channel_layouts=stereo,volume=0.5,apad", graph);

        // An A1 volume alone stops the whole file from being copied.
        var plain = Job(Encode, [], whole);
        var quieter = ExportPlanner.Plan(plain with { State = plain.State with { A1Volume = 0.5 } });
        Assert.Equal(ExportMode.Encode, quieter.Mode);
        Assert.Contains(",volume=0.5[a0]", Graph(quieter));
        Assert.Equal(ExportMode.Passthrough, ExportPlanner.Plan(plain with { State = plain.State with { A2Volume = 0.5 } }).Mode);
    }

    [Fact]
    public void A2_is_mixed_into_the_joined_audio_with_its_volumes()
    {
        var plan = ExportPlanner.Plan(Job(Encode, [T("a", 2, 5, 25, 0.5)], Clip.Create("v", S(0), S(10))));
        Assert.Equal(ExportMode.Encode, plan.Mode);
        var graph = Graph(plan);
        // 2 s of silence, then 8 s of the song from 5 s (read from the file's start, as audio files are), at half volume.
        Assert.Contains("anullsrc=r=48000:cl=stereo,atrim=end_sample=96000[t0];", graph);
        Assert.Contains("[2:0]atrim=start=5.000000:duration=8.000000,asetpts=PTS-STARTPTS,aformat=sample_rates=48000:channel_layouts=stereo,volume=0.5,apad=whole_len=384000,atrim=end_sample=384000[t1];", graph);
        Assert.Contains("[t0][t1]concat=n=2:v=0:a=1,asetpts=N/SR/TB[atrack];", graph);
        Assert.EndsWith("concat=n=1:v=1:a=1[vout][amain];[amain][atrack]amix=inputs=2:normalize=0:duration=first:dropout_transition=0[aout]", graph);
        Assert.Contains(plan.Steps[^1].Arguments.ToList(), a => a == "/m/song.mp3");
        Assert.Contains(plan.Notes, n => n.Contains("A2 is mixed in: 1 clip(s)."));
        Assert.Contains(plan.Notes, n => n.Contains("because A2 is mixed in"));
    }

    [Fact]
    public void A2_alone_gives_a_silent_video_its_sound_and_is_saved_by_audio_presets()
    {
        var silent = Video(10) with { AudioStreams = [] };
        var job = Job(Encode, [T("a", 0, 0, 30)], Clip.Create("v", S(0), S(10)));
        job = job with { Media = new Dictionary<string, MediaInfo> { ["v"] = silent, ["f"] = Song } };
        var plan = ExportPlanner.Plan(job);
        Assert.Equal(ExportMode.Encode, plan.Mode);
        Assert.Contains("anullsrc=r=44100:cl=stereo,atrim=duration=10.000000,asetpts=N/SR/TB[a0]", Graph(plan));
        Assert.Contains("-map", plan.Steps[^1].Arguments);
        Assert.Contains("[aout]", plan.Steps[^1].Arguments);

        var audio = ExportPlanner.Plan(Job(Preset.ExtractAudio, [T("a", 0, 0, 30)], Clip.Create("v", S(0), S(10))) with { OutputPath = "/out/edit.flac" });
        Assert.Equal(ExportMode.AudioEncode, audio.Mode);
        Assert.Contains("amix", Graph(audio));
    }

    [Fact]
    public void Stream_copy_mixes_A2_under_each_clip_from_its_keyframe()
    {
        var plan = ExportPlanner.Plan(Job(Preset.PlaneCopy, [T("a", 0, 0, 30)], Clip.Create("v", S(1.5), S(3)), Clip.Create("v", S(5), S(6))));
        Assert.Equal(ExportMode.StreamCopy, plan.Mode);
        var graph = Graph(plan);
        // The first clip starts at the keyframe 0.5 s before its in point: A2 from 0.5 s before the timeline, which is
        // silence, then 1.5 s of the song; the second clip's 1 s from 1.5 s into the timeline.
        Assert.Contains("anullsrc=r=48000:cl=stereo,atrim=end_sample=24000[t0];", graph);
        Assert.Contains("[3:0]aformat=sample_rates=48000:channel_layouts=stereo,apad=whole_len=72000,atrim=end_sample=72000[t1];", graph);
        Assert.Contains("[4:0]atrim=start=1.500000:duration=1.000000,asetpts=PTS-STARTPTS,", graph);
        Assert.Contains("[a0][a1]concat=n=2:v=0:a=1[amain];[amain][atrack]amix=inputs=2:normalize=0:duration=first:dropout_transition=0[ashift]", graph);
    }

    [Fact]
    public void A2_clips_after_the_timeline_ends_are_left_out()
    {
        var whole = Clip.Create("v", S(0), S(10));
        var late = T("late", 12, 0, 5) with { FileId = "gone" };
        var job = Job(Encode, [late], whole);
        job = job with { State = job.State with { AudioFiles = job.State.AudioFiles.Add(new SourceMedia { Id = "gone", Path = "/m/gone.mp3" }) } };
        // Its file isn't analyzed (or is missing), and the whole file is still passed through untouched.
        var plan = ExportPlanner.Plan(job);
        Assert.Equal(ExportMode.Passthrough, plan.Mode);
        Assert.Contains(plan.Notes, n => n.Contains("1 A2 clip(s) start after the timeline ends"));
        Assert.DoesNotContain(plan.Notes, n => n.Contains("A2 is mixed in"));

        var mixed = ExportPlanner.Plan(Job(Encode, [T("a", 2, 0, 5), T("b", 10, 0, 5)], whole));
        Assert.Contains(mixed.Notes, n => n == "A2 is mixed in: 1 clip(s).");
        Assert.DoesNotContain(Graph(mixed), "[4:0]");

        // The audio is still copied as it is, in its own file type.
        var audio = Job(Preset.ExtractAudio, [late], whole);
        Assert.Equal(ExportMode.AudioCopy, ExportPlanner.Plan(audio).Mode);
        Assert.Equal("m4a", ExportPlanner.AudioExtensionFor(audio.State, audio.Media, Preset.ExtractAudio));
    }

    [Fact]
    public void The_A2_files_are_never_overwritten_and_presets_without_audio_ignore_A2()
    {
        var job = Job(Encode, [T("a", 0, 0, 30)], Clip.Create("v", S(0), S(10))) with { OutputPath = "/m/song.mp3" };
        Assert.Contains("audio files", Assert.Throws<ExportPlanException>(() => ExportPlanner.Plan(job)).Message);

        var mute = new Preset { Id = "m", Name = "mute", Arguments = "-c:v libx264 -an", Extension = "mp4" };
        var plan = ExportPlanner.Plan(Job(mute, [T("a", 0, 0, 30)], Clip.Create("v", S(0), S(10))));
        Assert.DoesNotContain(plan.Steps[^1].Arguments, a => a.Contains("amix") || a == "/m/song.mp3");
    }
}
