using System.Collections.Immutable;

namespace ManeFfmpeg.Core.Model;

/// <summary>A loaded file. Probe results live in a separate catalog so edits stay cheap to snapshot.</summary>
public sealed record SourceMedia
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public long FileSize { get; init; }
    public DateTime LastWriteUtc { get; init; }

    /// <summary>Which audio stream to use, as an index among the file's audio streams ("a:N").</summary>
    public int AudioStreamIndex { get; init; }

    public string DisplayName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// One piece of a source on the timeline. <see cref="AudioOffset"/> slips the clip's audio:
/// positive means the audio is heard later, so it is read from <c>[In − offset, Out − offset]</c>.
/// </summary>
public sealed record Clip
{
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required TimeSpan In { get; init; }
    public required TimeSpan Out { get; init; }
    public TimeSpan AudioOffset { get; init; }

    public TimeSpan Duration => Out - In;

    public TimeSpan AudioIn => In - AudioOffset;

    public TimeSpan AudioOut => Out - AudioOffset;

    /// <summary>Where the whole clip's audio comes from, given where its file's audio stream ends.</summary>
    public AudioPlacement AudioPlacement(TimeSpan audioEnd) => Model.AudioPlacement.For(In, Duration, AudioOffset, audioEnd);
}

/// <summary>Clips played back to back, without gaps.</summary>
public sealed record Timeline
{
    public static readonly Timeline Empty = new([]);

    public Timeline(ImmutableList<Clip> clips) => Clips = clips;

    public ImmutableList<Clip> Clips { get; init; }

    public TimeSpan Duration => Clips.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Duration);

    public int IndexOf(string clipId) => Clips.FindIndex(c => c.Id == clipId);

    public TimeSpan StartOf(int index)
    {
        var start = TimeSpan.Zero;
        for (var i = 0; i < index && i < Clips.Count; i++)
            start += Clips[i].Duration;
        return start;
    }

    /// <summary>Finds the clip playing at <paramref name="time"/>; the end of the timeline maps to the last clip.</summary>
    public (int Index, TimeSpan ClipStart)? Locate(TimeSpan time)
    {
        var start = TimeSpan.Zero;
        for (var i = 0; i < Clips.Count; i++)
        {
            var end = start + Clips[i].Duration;
            if (time < end || i == Clips.Count - 1)
                return (i, start);
            start = end;
        }
        return null;
    }

    public bool Equals(Timeline? other) =>
        other is not null && Clips.SequenceEqual(other.Clips);

    public override int GetHashCode() => Clips.Count;
}

/// <summary>The undoable part of a project.</summary>
public sealed record ProjectState(ImmutableList<SourceMedia> Sources, Timeline Timeline)
{
    public static readonly ProjectState Empty = new([], Timeline.Empty);

    public SourceMedia? Source(string id) => Sources.Find(s => s.Id == id);

    public bool Equals(ProjectState? other) =>
        other is not null && Sources.SequenceEqual(other.Sources) && Timeline.Equals(other.Timeline);

    public override int GetHashCode() => HashCode.Combine(Sources.Count, Timeline.Clips.Count);
}
