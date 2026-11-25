using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Tests;

/// <summary>Exported files' names, the same for the editor's export and Quick Actions.</summary>
public class OutputNamingTests
{
    private static readonly Dictionary<string, MediaInfo> NoMedia = [];

    [Fact]
    public void The_extension_is_the_presets_else_the_sources_or_for_audio_m4a()
    {
        Assert.Equal("mkv", OutputNaming.Extension(Preset.Share with { Extension = " .mkv " }, PresetKind.Video, ProjectState.Empty, NoMedia, "/v/a.mov"));
        Assert.Equal("mov", OutputNaming.Extension(Preset.PlaneCopy, PresetKind.Video, ProjectState.Empty, NoMedia, "/v/a.mov"));
        Assert.Equal("", OutputNaming.Extension(null, PresetKind.Video, ProjectState.Empty, NoMedia, "/v/noext"));
        Assert.Equal("m4a", OutputNaming.Extension(null, PresetKind.Audio, ProjectState.Empty, NoMedia, "/v/a.mov"));
    }

    [Fact]
    public void A_free_path_skips_files_folders_and_names_spoken_for()
    {
        var dir = Directory.CreateTempSubdirectory("mane-naming").FullName;
        try
        {
            Assert.Equal(Path.Combine(dir, "a.mp4"), OutputNaming.FreePath(dir, "a", "mp4"));
            File.WriteAllText(Path.Combine(dir, "a.mp4"), "");
            Directory.CreateDirectory(Path.Combine(dir, "a-2.mp4"));
            Assert.Equal(Path.Combine(dir, "a-4.mp4"), OutputNaming.FreePath(dir, "a", "mp4", p => p.EndsWith("a-3.mp4")));
            Assert.Equal(Path.Combine(dir, "a"), OutputNaming.FreePath(dir, "a", ""));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
