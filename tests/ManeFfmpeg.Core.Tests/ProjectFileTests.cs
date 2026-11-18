using System.Runtime.Versioning;
using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Persistence;
using ManeFfmpeg.Core.Platforms.Linux;

namespace ManeFfmpeg.Core.Tests;

public class ProjectFileTests
{
    private const string Header = "\"format\": \"mane-ffmpeg-project\", \"version\": 1";

    [Fact]
    public void Missing_lists_and_entries_load_as_empty()
    {
        var empty = ProjectFile.Deserialize($"{{ {Header}, \"sources\": null, \"clips\": null }}", "/p/x.mfvp");
        Assert.Empty(empty.State.Sources);
        Assert.Empty(empty.State.Timeline.Clips);

        var partial = ProjectFile.Deserialize($$"""
            { {{Header}},
              "sources": [null, { "id": "s", "path": "/m/a.mp4" }, { "id": null, "path": "/m/b.mp4" }],
              "clips": [null, { "id": "c", "source": "s", "in": "00:00:01", "out": "00:00:02" }, { "id": "d", "source": null }] }
            """, "/p/x.mfvp");
        Assert.Equal("s", Assert.Single(partial.State.Sources).Id);
        Assert.Equal("c", Assert.Single(partial.State.Timeline.Clips).Id);
    }

    [Fact]
    public void Any_unreadable_file_is_a_project_file_error()
    {
        Assert.Throws<ProjectFileException>(() => ProjectFile.Deserialize($"{{ {Header}, \"clips\": [{{ \"in\": \"soon\" }}] }}", "/p/x.mfvp"));
        Assert.Throws<ProjectFileException>(() => ProjectFile.Deserialize(
            $"{{ {Header}, \"sources\": [{{ \"id\": \"s\", \"path\": \"/gone.mp4\", \"relativePath\": \"a\\u0000b\" }}] }}", "/p/x.mfvp"));
    }

    [Fact]
    public void Round_trips_edits_and_layout_with_relative_paths()
    {
        var dir = Directory.CreateTempSubdirectory("mfvp");
        try
        {
            var media = Path.Combine(dir.FullName, "media", "clip 'one'.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(media)!);
            File.WriteAllText(media, "x");
            var project = new Project
            {
                State = new ProjectState(
                    [new SourceMedia { Id = "s1", Path = media, FileSize = 1, AudioStreamIndex = 1 }],
                    new Timeline([new Clip { Id = "c1", SourceId = "s1", In = TimeSpan.FromSeconds(1.5), Out = TimeSpan.FromSeconds(4), AudioOffset = TimeSpan.FromMilliseconds(-80) }])),
                Export = new ExportSettings { PresetId = Preset.ForStorage.Id, PresetSnapshot = Preset.ForStorage, OutputPath = "/tmp/out.mp4" },
                Layout = new ProjectLayout { TimelineZoom = 42.5, Playhead = TimeSpan.FromSeconds(3), Window = new WindowBounds(10, 20, 1200, 800, false) },
            };
            var path = Path.Combine(dir.FullName, "a.mfvp");
            var json = ProjectFile.Serialize(project, path);
            Assert.Contains("\"relativePath\": \"media/clip 'one'.mp4\"", json);

            // Move the whole folder: the relative path still finds the media.
            var moved = Path.Combine(Path.GetTempPath(), "mfvp-moved-" + Guid.NewGuid().ToString("N"));
            Directory.Move(dir.FullName, moved);
            dir = new DirectoryInfo(moved);
            var loaded = ProjectFile.Deserialize(json, Path.Combine(moved, "a.mfvp"));
            Assert.Equal(Path.Combine(moved, "media", "clip 'one'.mp4"), loaded.State.Sources[0].Path);
            Assert.Equal(project.State.Timeline.Clips[0], loaded.State.Timeline.Clips[0]);
            Assert.Equal(1, loaded.State.Sources[0].AudioStreamIndex);
            Assert.Equal(project.Export, loaded.Export);
            Assert.Equal(project.Layout, loaded.Layout);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Rejects_other_json_and_newer_versions()
    {
        Assert.Throws<ProjectFileException>(() => ProjectFile.Deserialize("{\"hello\":1}", "/tmp/x.mfvp"));
        Assert.Throws<ProjectFileException>(() => ProjectFile.Deserialize("{\"format\":\"mane-ffmpeg-project\",\"version\":99}", "/tmp/x.mfvp"));
    }
}

/// <summary>The Linux association's text rules, which are plain strings, so they're checked everywhere.</summary>
[UnsupportedOSPlatform("windows")]
public class LinuxProjectFileTests
{
    [Theory]
    [InlineData("/opt/mane/ManeFfmpeg", "\"/opt/mane/ManeFfmpeg\"")]
    [InlineData("/home/a b/Mane FFmpeg/ManeFfmpeg", "\"/home/a b/Mane FFmpeg/ManeFfmpeg\"")]
    [InlineData("/x/$HOME`id`/100%/ManeFfmpeg", "\"/x/\\\\$HOME\\\\`id\\\\`/100%%/ManeFfmpeg\"")]
    [InlineData("/x/a\\b\"c", "\"/x/a\\\\\\\\b\\\\\"c\"")]
    public void Quotes_the_executable_for_a_desktop_entry(string path, string expected) =>
        Assert.Equal(expected, LinuxPlatform.ExecArgument(path));

    [Fact]
    public void Removes_only_the_project_type_from_mimeapps_list()
    {
        const string list = "[Default Applications]\nvideo/mp4=vlc.desktop\napplication/x-mane-ffmpeg-project=mane-ffmpeg.desktop\n\n" +
                            "[Added Associations]\napplication/x-mane-ffmpeg-project=mane-ffmpeg.desktop;\napplication/x-mane-ffmpeg-project-old=x.desktop;\n";
        Assert.Equal("[Default Applications]\nvideo/mp4=vlc.desktop\n\n[Added Associations]\napplication/x-mane-ffmpeg-project-old=x.desktop;\n",
            LinuxPlatform.WithoutAssociations(list, "application/x-mane-ffmpeg-project"));
    }
}

/// <summary>Edits of a project's files that keep its clips pointing at listed files.</summary>
public class ProjectEditorTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static readonly SourceMedia Video = new() { Id = "v", Path = "/m/a.mp4" };
    private static readonly SourceMedia Song = new() { Id = "f", Path = "/m/song.m4a" };

    private static Clip Plays(string id, string? fileId) =>
        Clip.Create("v", S(0), S(2)) with { Id = id, ReplacedAudio = fileId is null ? null : new AudioReplacement { FileId = fileId } };

    private static ProjectState State(params Clip[] clips) => new([Video], new Timeline([.. clips]), [Song]);

    [Fact]
    public void A_path_is_listed_once_and_keeps_its_id()
    {
        var (state, id) = ProjectEditor.AddFile(State(), FileKind.Audio, new SourceMedia { Id = "new", Path = Song.Path });
        Assert.Equal("f", id);
        Assert.Single(state.AudioFiles);
        (state, id) = ProjectEditor.AddFile(state, FileKind.Video, new SourceMedia { Id = "w", Path = "/m/b.mp4" });
        Assert.Equal(("w", 2), (id, state.Sources.Count));
    }

    [Fact]
    public void Removing_an_audio_file_gives_its_clips_their_own_sound_back()
    {
        var state = ProjectEditor.RemoveFile(State(Plays("a", "f"), Plays("b", null)), FileKind.Audio, "f");
        Assert.Empty(state.AudioFiles);
        Assert.All(state.Timeline.Clips, c => Assert.Null(c.ReplacedAudio));
        Assert.Equal(2, state.Timeline.Clips.Count);
    }

    [Fact]
    public void Removing_a_video_removes_its_clips()
    {
        var before = State(Plays("a", "f"));
        Assert.Equal(1, ProjectEditor.ClipsUsing(before, FileKind.Video, "v"));
        Assert.Equal(1, ProjectEditor.ClipsUsing(before, FileKind.Audio, "f"));
        var state = ProjectEditor.RemoveFile(before, FileKind.Video, "v");
        Assert.Empty(state.Sources);
        Assert.Empty(state.Timeline.Clips);
        Assert.Single(state.AudioFiles);
    }

    [Fact]
    public void Replacing_a_clip_s_audio_needs_both_the_clip_and_the_file()
    {
        var state = State(Plays("a", null));
        Assert.Equal("f", ProjectEditor.ReplaceClipAudio(state, "a", "f").Timeline.Clips[0].ReplacedAudio!.FileId);
        Assert.Same(state, ProjectEditor.ReplaceClipAudio(state, "a", "gone"));
        Assert.Same(state, ProjectEditor.ReplaceClipAudio(state, "gone", "f"));
    }

    [Fact]
    public void Appending_needs_a_listed_video()
    {
        Assert.Single(ProjectEditor.AppendClip(State(), "v", S(1), S(3)).Timeline.Clips);
        Assert.Empty(ProjectEditor.AppendClip(State(), "gone", S(1), S(3)).Timeline.Clips);
    }

    private static readonly MediaInfo Movie = new()
    {
        Duration = S(5),
        Video = new VideoStreamInfo { Index = 0, Codec = "h264", Width = 640, Height = 360, FrameRate = new Rational(30, 1) },
        AudioStreams = [new AudioStreamInfo { Index = 1, Codec = "aac", SampleRate = 48000, Channels = 2 }],
    };

    private static readonly MediaInfo Tune = new() { Duration = S(3), AudioStreams = [new AudioStreamInfo { Index = 0, Codec = "mp3", SampleRate = 44100, Channels = 2 }] };

    private static readonly ProbedFile[] Dropped =
    [
        new("/in/movie.mp4", Movie, null), new("/in/tune.mp3", Tune, null),
        new("/in/notes.txt", new MediaInfo { Duration = S(0) }, null), new("/in/broken.mov", null, "Invalid data"),
    ];

    private static SourceMedia New(string path) => new() { Id = "id:" + path, Path = path };

    [Fact]
    public void Added_files_go_where_their_content_belongs()
    {
        var added = ProjectEditor.AddProbed(State(), Dropped, AddRouting.ByContent, New);
        Assert.Equal(["id:/in/movie.mp4"], added.Videos);
        Assert.Equal(["id:/in/tune.mp3"], added.AudioFiles);
        Assert.Equal(["notes.txt: no video or audio stream", "broken.mov: Invalid data"], added.Skipped);
        Assert.Equal(S(5), added.State.Timeline.Clips.Single().Duration);
        Assert.Equal(2, added.State.AudioFiles.Count);
    }

    [Fact]
    public void Added_as_audio_every_file_with_sound_is_an_audio_file()
    {
        var added = ProjectEditor.AddProbed(State(), Dropped, AddRouting.AllAsAudio, New);
        Assert.Empty(added.Videos);
        Assert.Equal(["id:/in/movie.mp4", "id:/in/tune.mp3"], added.AudioFiles);
        Assert.Equal(["notes.txt: no audio stream", "broken.mov: Invalid data"], added.Skipped);
        Assert.Empty(added.State.Timeline.Clips);
    }

    [Fact]
    public void Orphans_are_dropped_or_fall_back_to_their_own_sound()
    {
        var orphan = Plays("x", null) with { SourceId = "gone" };
        var state = ProjectEditor.DropOrphans(State(Plays("a", "gone"), Plays("b", "f"), orphan));
        Assert.Equal(["a", "b"], state.Timeline.Clips.Select(c => c.Id));
        Assert.Null(state.Timeline.Clips[0].ReplacedAudio);
        Assert.Equal("f", state.Timeline.Clips[1].ReplacedAudio!.FileId);
    }
}
