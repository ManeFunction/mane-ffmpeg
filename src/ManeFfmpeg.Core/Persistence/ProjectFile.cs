using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    public const int CurrentVersion = 1;

    public static async Task SaveAsync(Project project, string path, CancellationToken ct = default)
    {
        var json = Serialize(project, path);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, json, ct);
        File.Move(temp, path, overwrite: true);
    }

    public static async Task<Project> LoadAsync(string path, CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(path, ct);
        return Deserialize(json, path);
    }

    public static string Serialize(Project project, string projectPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var dto = new ProjectDto
        {
            Format = FormatName,
            Version = CurrentVersion,
            Sources = [.. project.State.Sources.Select(s => new SourceDto
            {
                Id = s.Id,
                Path = s.Path,
                RelativePath = Path.GetRelativePath(dir, s.Path),
                FileSize = s.FileSize,
                LastWriteUtc = s.LastWriteUtc,
                AudioStream = s.AudioStreamIndex,
            })],
            Clips = [.. project.State.Timeline.Clips.Select(c => new ClipDto
            {
                Id = c.Id,
                Source = c.SourceId,
                In = c.In,
                Out = c.Out,
                AudioOffset = c.AudioOffset,
            })],
            Export = project.Export,
            Layout = project.Layout,
        };
        return JsonSerializer.Serialize(dto, ProjectJsonContext.Readable.ProjectDto);
    }

    public static Project Deserialize(string json, string projectPath)
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
        var sources = dto.Sources.Select(s => new SourceMedia
        {
            Id = s.Id,
            Path = ResolvePath(s, dir),
            FileSize = s.FileSize,
            LastWriteUtc = s.LastWriteUtc,
            AudioStreamIndex = Math.Max(0, s.AudioStream),
        }).ToImmutableList();
        var known = sources.Select(s => s.Id).ToHashSet();
        var clips = dto.Clips
            .Where(c => known.Contains(c.Source) && c.Out > c.In)
            .Select(c => new Clip { Id = c.Id, SourceId = c.Source, In = c.In, Out = c.Out, AudioOffset = c.AudioOffset })
            .ToImmutableList();

        return new Project
        {
            State = new ProjectState(sources, new Timeline(clips)),
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
        public List<SourceDto> Sources { get; set; } = [];
        public List<ClipDto> Clips { get; set; } = [];
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
