using System.Collections.Immutable;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Editing;

/// <summary>A file to add with its probe result, or why probing failed.</summary>
public sealed record ProbedFile(string Path, MediaInfo? Info, string? Error);

/// <summary>Where added files go.</summary>
public enum AddRouting
{
    /// <summary>Videos are listed and appended as whole clips; files with only sound go to the audio files.</summary>
    ByContent,

    /// <summary>Every file with sound goes to the audio files, videos too (their sound can replace a clip's).</summary>
    AllAsAudio,
}

/// <summary>The state after adding files, with the ids of the videos and audio files added and what was skipped.</summary>
public sealed record AddedFiles(ProjectState State, IReadOnlyList<string> Videos, IReadOnlyList<string> AudioFiles, IReadOnlyList<string> Skipped);

/// <summary>
/// Pure edits of a project's files together with the clips that use them. Each keeps the state whole: every clip's
/// video is in <see cref="ProjectState.Sources"/>, and every replaced sound's file is in <see cref="ProjectState.AudioFiles"/>.
/// </summary>
public static class ProjectEditor
{
    /// <summary>The state with <paramref name="file"/> listed, and its id; a path already listed keeps its entry (and id).</summary>
    public static (ProjectState State, string Id) AddFile(ProjectState state, FileKind kind, SourceMedia file)
    {
        var files = state.Files(kind);
        if (files.Find(f => f.Path == file.Path) is { } existing)
            return (state, existing.Id);
        return (state.WithFiles(kind, files.Add(file)), file.Id);
    }

    /// <summary>Appends a clip of video <paramref name="sourceId"/>; unchanged when that video isn't listed.</summary>
    public static ProjectState AppendClip(ProjectState state, string sourceId, TimeSpan @in, TimeSpan @out) =>
        state.Source(sourceId) is null
            ? state
            : state with { Timeline = TimelineEditor.Append(state.Timeline, Clip.Create(sourceId, @in, @out)) };

    /// <summary>How many clips use the file: clips of a video, or clips whose sound an audio file replaces.</summary>
    public static int ClipsUsing(ProjectState state, FileKind kind, string id) =>
        state.Timeline.Clips.Count(c => kind == FileKind.Audio ? c.ReplacedAudio?.FileId == id : c.SourceId == id);

    /// <summary>
    /// Removes a file with what depends on it: a video's clips go too, and clips playing an audio file go back to their
    /// own sound.
    /// </summary>
    public static ProjectState RemoveFile(ProjectState state, FileKind kind, string id)
    {
        var timeline = kind == FileKind.Audio
            ? TimelineEditor.RemoveAudioFile(state.Timeline, id)
            : TimelineEditor.RemoveSource(state.Timeline, id);
        return state.WithFiles(kind, state.Files(kind).RemoveAll(f => f.Id == id)) with { Timeline = timeline };
    }

    /// <summary>
    /// Plays audio file <paramref name="fileId"/> instead of the clip's sound. Returns the same state instance when the
    /// clip or the audio file isn't there.
    /// </summary>
    public static ProjectState ReplaceClipAudio(ProjectState state, string clipId, string fileId) =>
        state.Timeline.IndexOf(clipId) < 0 || state.AudioFile(fileId) is null
            ? state
            : state with { Timeline = TimelineEditor.ReplaceAudio(state.Timeline, clipId, fileId) };

    /// <summary>
    /// Drops what refers to files that aren't listed (a damaged or hand-edited project): clips of unknown videos go,
    /// and clips whose replacement audio file is unknown play their own sound.
    /// </summary>
    public static ProjectState DropOrphans(ProjectState state)
    {
        var videos = state.Sources.Select(s => s.Id).ToHashSet();
        var audio = state.AudioFiles.Select(s => s.Id).ToHashSet();
        var clips = state.Timeline.Clips
            .Where(c => videos.Contains(c.SourceId))
            .Select(c => c.ReplacedAudio is { } r && !audio.Contains(r.FileId) ? c with { ReplacedAudio = null } : c)
            .ToImmutableList();
        return state with { Timeline = state.Timeline with { Clips = clips } };
    }

    /// <summary>
    /// Adds probed files as <paramref name="routing"/> says, each path once (a listed path keeps its entry).
    /// <paramref name="newFile"/> makes the entry for a path that isn't listed yet.
    /// </summary>
    public static AddedFiles AddProbed(ProjectState state, IEnumerable<ProbedFile> files, AddRouting routing, Func<string, SourceMedia> newFile)
    {
        List<string> videos = [], audio = [], skipped = [];
        foreach (var (path, info, error) in files)
        {
            var hasSound = info is { AudioStreams.Length: > 0 };
            if (routing == AddRouting.AllAsAudio ? hasSound : info is { Video: null } && hasSound)
            {
                (state, var id) = AddFile(state, FileKind.Audio, newFile(path));
                audio.Add(id);
            }
            else if (routing == AddRouting.ByContent && info is { Video: not null })
            {
                (state, var id) = AddFile(state, FileKind.Video, newFile(path));
                state = AppendClip(state, id, TimeSpan.Zero, info.Duration);
                videos.Add(id);
            }
            else
            {
                var missing = routing == AddRouting.AllAsAudio ? "no audio stream" : "no video or audio stream";
                skipped.Add($"{System.IO.Path.GetFileName(path)}: {error ?? missing}");
            }
        }
        return new AddedFiles(state, videos, audio, skipped);
    }
}
