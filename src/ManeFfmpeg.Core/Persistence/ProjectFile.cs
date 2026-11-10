using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Persistence;

public sealed class ProjectFileException(string message) : Exception(message);

/// <summary>
/// Reads and writes .mfvp project files: indented JSON with edits and layout only, no media.
/// Source paths are stored absolute and relative to the project, so a folder can be moved as a whole.
/// </summary>
public static class ProjectFile
{
    public const string Extension = "mfvp";
    public const string FormatName = "mane-ffmpeg-project";
    /// <summary>
    /// Version 2 adds audio files and replaced clip audio, version 3 the output crop. A project is written with the
    /// lowest version that holds it, so one without audio files or a crop is still 1.
    /// </summary>
    public const int CurrentVersion = 3;

    public static async Task SaveAsync(Project project, string path, CancellationToken ct = default)
    {
        await AtomicFile.WriteAllTextAsync(path, Serialize(project, path), ct);
    }

    public static async Task<Project> LoadAsync(string path, CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(path, ct);
        return Deserialize(json, path);
    }

    public static string Serialize(Project project, string projectPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        SourceDto Source(SourceMedia s) => new()
        {
            Id = s.Id,
            Path = s.Path,
            RelativePath = Path.GetRelativePath(dir, s.Path),
            FileSize = s.FileSize,
            LastWriteUtc = s.LastWriteUtc,
            AudioStream = s.AudioStreamIndex,
        };
        var hasAudioFiles = project.State.AudioFiles.Count > 0;
        var crop = project.State.Crop;
        var dto = new ProjectDto
        {
            Format = FormatName,
            // An older app would silently drop replaced audio or the crop, so it is told this file is newer.
            Version = crop is not null ? 3 : hasAudioFiles ? 2 : 1,
            Sources = [.. project.State.Sources.Select(Source)],
            AudioFiles = hasAudioFiles ? [.. project.State.AudioFiles.Select(Source)] : null,
            Clips = [.. project.State.Timeline.Clips.Select(c => new ClipDto
            {
                Id = c.Id,
                Source = c.SourceId,
                In = c.In,
                Out = c.Out,
                AudioOffset = c.AudioOffset,
                ReplacedAudio = c.ReplacedAudio is { } r
                    ? new ReplacementDto { File = r.FileId, Anchor = r.Anchor, Offset = r.Offset, Loop = r.Loop }
                    : null,
            })],
            Crop = crop is null ? null : new CropDto { X = crop.X, Y = crop.Y, Width = crop.Width, Height = crop.Height },
            Export = project.Export,
            Layout = project.Layout,
        };
        return JsonSerializer.Serialize(dto, ProjectJsonContext.Readable.ProjectDto);
    }

    /// <summary>Reads a project; anything wrong with the file is a <see cref="ProjectFileException"/>.</summary>
    public static Project Deserialize(string json, string projectPath)
    {
        try
        {
            return Read(json, projectPath);
        }
        catch (Exception ex) when (ex is not ProjectFileException)
        {
            throw new ProjectFileException($"The project file is damaged: {ex.Message}");
        }
    }

    private static Project Read(string json, string projectPath)
    {
        ProjectDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, ProjectJsonContext.Readable.ProjectDto);
        }
        catch (JsonException ex)
        {
            throw new ProjectFileException($"The project file is damaged: {ex.Message}");
        }
        if (dto is null || dto.Format != FormatName)
            throw new ProjectFileException("This isn't a Mane FFmpeg project file.");
        if (dto.Version > CurrentVersion)
            throw new ProjectFileException($"This project was saved by a newer version (format {dto.Version}). Please update the app.");

        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        // Hand-edited or damaged files may leave out lists or entries; skip what can't be used.
        ImmutableList<SourceMedia> Sources(List<SourceDto?>? list) =>
            (list ?? []).OfType<SourceDto>().Where(s => s is { Id.Length: > 0, Path.Length: > 0 }).Select(s => new SourceMedia
            {
                Id = s.Id,
                Path = ResolvePath(s, dir),
                FileSize = s.FileSize,
                LastWriteUtc = s.LastWriteUtc,
                AudioStreamIndex = Math.Max(0, s.AudioStream),
            }).ToImmutableList();
        var sources = Sources(dto.Sources);
        var audioFiles = Sources(dto.AudioFiles);
        var clips = (dto.Clips ?? []).OfType<ClipDto>()
            .Where(c => c is { Id.Length: > 0, Source.Length: > 0 } && c.Out > c.In)
            .Select(c => new Clip
            {
                Id = c.Id, SourceId = c.Source, In = c.In, Out = c.Out, AudioOffset = c.AudioOffset,
                ReplacedAudio = c.ReplacedAudio is { File: { } file } r
                    ? new AudioReplacement { FileId = file, Anchor = r.Anchor, Offset = r.Offset, Loop = r.Loop }
                    : null,
            })
            .ToImmutableList();

        return new Project
        {
            // Clips of a video that isn't listed are dropped; a replacement whose audio file is gone leaves the clip
            // with its own audio.
            State = ProjectEditor.DropOrphans(new ProjectState(sources, new Timeline(clips), audioFiles))
                .WithCrop(dto.Crop is { } c ? new CropRect(c.X, c.Y, c.Width, c.Height) : null),
            Export = dto.Export ?? new ExportSettings(),
            Layout = dto.Layout ?? new ProjectLayout(),
        };
    }

    /// <summary>The stored absolute path if it still exists, else the project-relative one if that exists.</summary>
    private static string ResolvePath(SourceDto source, string projectDir)
    {
        if (File.Exists(source.Path))
            return source.Path;
        if (!string.IsNullOrEmpty(source.RelativePath))
        {
            var relative = Path.GetFullPath(Path.Combine(projectDir, source.RelativePath));
            if (File.Exists(relative))
                return relative;
        }
        return source.Path;
    }

    internal sealed class ProjectDto
    {
        public string Format { get; set; } = "";
        public int Version { get; set; }
        public List<SourceDto?>? Sources { get; set; } = [];
        public List<SourceDto?>? AudioFiles { get; set; }
        public List<ClipDto?>? Clips { get; set; } = [];
        public CropDto? Crop { get; set; }
        public ExportSettings? Export { get; set; }
        public ProjectLayout? Layout { get; set; }
    }

    internal sealed class SourceDto
    {
        public string Id { get; set; } = "";
        public string Path { get; set; } = "";
        public string? RelativePath { get; set; }
        public long FileSize { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public int AudioStream { get; set; }
    }

    internal sealed class ClipDto
    {
        public string Id { get; set; } = "";
        public string Source { get; set; } = "";
        public TimeSpan In { get; set; }
        public TimeSpan Out { get; set; }
        public TimeSpan AudioOffset { get; set; }
        public ReplacementDto? ReplacedAudio { get; set; }
    }

    /// <summary>The kept part of the output frame, as fractions of its width and height.</summary>
    internal sealed class CropDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 1;
        public double Height { get; set; } = 1;
    }

    internal sealed class ReplacementDto
    {
        public string? File { get; set; }
        public TimeSpan Anchor { get; set; }
        public TimeSpan Offset { get; set; }
        public bool Loop { get; set; }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProjectFile.ProjectDto))]
internal sealed partial class ProjectJsonContext : JsonSerializerContext
{
    /// <summary>Keeps quotes and non-ASCII (e.g. Cyrillic paths) readable instead of \\u-escapes.</summary>
    private static ProjectJsonContext? _readable;

    public static ProjectJsonContext Readable => _readable ??= new(new JsonSerializerOptions(Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
