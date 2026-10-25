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

    /// <summary>A new clip of <paramref name="sourceId"/> from <paramref name="in"/> to <paramref name="out"/>, with a fresh id.</summary>
    public static Clip Create(string sourceId, TimeSpan @in, TimeSpan @out) => new() { Id = Ids.New(), SourceId = sourceId, In = @in, Out = @out };

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

    private ImmutableArray<TimeSpan>? _bounds;

    public Timeline(ImmutableList<Clip> clips) => Clips = clips;

    public ImmutableList<Clip> Clips
    {
        get;
        init
        {
            field = value;
            // "with" copies the cache along; a new clip list starts without one.
            _bounds = null;
        }
    }

    /// <summary>Where each clip starts, plus the end of the last one (Count + 1 entries), worked out once.</summary>
    public ImmutableArray<TimeSpan> Bounds => _bounds ??= ComputeBounds(Clips);

    public TimeSpan Duration => Bounds[^1];

    public int IndexOf(string clipId) => Clips.FindIndex(c => c.Id == clipId);

    public TimeSpan StartOf(int index) => Bounds[Math.Clamp(index, 0, Clips.Count)];

    /// <summary>Finds the clip playing at <paramref name="time"/>; the end of the timeline maps to the last clip.</summary>
    public (int Index, TimeSpan ClipStart)? Locate(TimeSpan time)
    {
        if (Clips.Count == 0)
            return null;
        var bounds = Bounds;
        // The last clip starting at or before the time; clips of zero length are passed over.
        var index = bounds.BinarySearch(0, Clips.Count, time, null);
        index = index >= 0 ? index : ~index - 1;
        while (index < Clips.Count - 1 && time >= bounds[index + 1])
            index++;
        index = Math.Clamp(index, 0, Clips.Count - 1);
        return (index, bounds[index]);
    }

    private static ImmutableArray<TimeSpan> ComputeBounds(ImmutableList<Clip> clips)
    {
        var bounds = ImmutableArray.CreateBuilder<TimeSpan>(clips.Count + 1);
        var start = TimeSpan.Zero;
        bounds.Add(start);
        foreach (var clip in clips)
        {
            start += clip.Duration;
            bounds.Add(start);
        }
        return bounds.MoveToImmutable();
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
