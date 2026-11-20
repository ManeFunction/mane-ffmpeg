namespace ManeFfmpeg.Core.Model;

/// <summary>
/// A piece of an audio file on the A2 track, mixed over the timeline's own sound: the file's <c>[In, Out)</c> plays from
/// timeline time <see cref="Start"/>. A2 clips don't move with V1 edits; they stay at their timeline time.
/// </summary>
public sealed record TrackClip
{
    public required string Id { get; init; }

    /// <summary>An id in <see cref="ProjectState.AudioFiles"/>.</summary>
    public required string FileId { get; init; }

    /// <summary>Timeline time where the clip starts.</summary>
    public required TimeSpan Start { get; init; }

    public required TimeSpan In { get; init; }
    public required TimeSpan Out { get; init; }

    /// <summary>Gain (1 = unchanged), between 0 and <see cref="Gain.Max"/>.</summary>
    public double Volume { get; init; } = 1;

    public TimeSpan Duration => Out - In;

    public TimeSpan End => Start + Duration;

    public static TrackClip Create(string fileId, TimeSpan start, TimeSpan @in, TimeSpan @out) =>
        new() { Id = Ids.New(), FileId = fileId, Start = start, In = @in, Out = @out };
}

/// <summary>The two audio tracks of the mix: A1 (what the V1 clips play) and A2 (the clips mixed over them).</summary>
public enum MixTrack
{
    A1,
    A2,
}
