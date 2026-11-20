using System.Collections.Immutable;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Editing;

/// <summary>What the timeline's selection holds; never more than one kind at a time.</summary>
public enum SelectionKind
{
    None,

    /// <summary>Clips on V1 (with their sound on A1).</summary>
    TimelineClips,

    /// <summary>Clips on A2.</summary>
    TrackClips,

    /// <summary>A whole audio track, selected by its label: only its volume is edited.</summary>
    WholeTrack,
}

/// <summary>
/// The timeline's selection: V1 clips, A2 clips or a whole audio track, never a mix, so what the inspector shows and
/// what the edits act on is always clear. Immutable; each change returns a new selection.
/// </summary>
public sealed record TimelineSelection
{
    public static readonly TimelineSelection None = new(SelectionKind.None, ImmutableHashSet<string>.Empty, null);

    private TimelineSelection(SelectionKind kind, ImmutableHashSet<string> ids, MixTrack? track)
    {
        Kind = kind;
        Ids = ids;
        Track = track;
    }

    public SelectionKind Kind { get; }

    /// <summary>The selected clips' ids: V1 or A2 clips, as <see cref="Kind"/> says; empty for a whole track.</summary>
    public ImmutableHashSet<string> Ids { get; }

    /// <summary>The selected track, with <see cref="SelectionKind.WholeTrack"/>.</summary>
    public MixTrack? Track { get; }

    public bool IsEmpty => Kind == SelectionKind.None;

    public bool Contains(string clipId) => Ids.Contains(clipId);

    public static TimelineSelection OfTrack(MixTrack track) => new(SelectionKind.WholeTrack, ImmutableHashSet<string>.Empty, track);

    /// <summary>These clips, V1 or A2 ones as the project says (ids of the other kind, or unknown ones, are dropped).</summary>
    public static TimelineSelection OfClips(ProjectState state, IEnumerable<string> ids)
    {
        var list = ids.ToList();
        var kind = list.Count > 0 && state.TrackClip(list[0]) is not null ? SelectionKind.TrackClips : SelectionKind.TimelineClips;
        return Of(state, kind, list);
    }

    /// <summary>Every V1 clip.</summary>
    public static TimelineSelection AllClips(ProjectState state) => Of(state, SelectionKind.TimelineClips, state.Timeline.Clips.Select(c => c.Id));

    /// <summary>
    /// A click on a clip: alone, toggled (Cmd/Ctrl) or a range from the first selected V1 clip (Shift). A clip of the
    /// other kind than the selection starts a new one; A2 clips have no ranges, as they aren't in a row.
    /// </summary>
    public TimelineSelection Click(ProjectState state, string clipId, bool toggle, bool range)
    {
        var kind = state.TrackClip(clipId) is not null ? SelectionKind.TrackClips : SelectionKind.TimelineClips;
        var ids = Kind == kind ? Ids : ImmutableHashSet<string>.Empty;
        if (range && kind == SelectionKind.TimelineClips && !ids.IsEmpty)
        {
            var clips = state.Timeline.Clips;
            var anchor = clips.FindIndex(c => ids.Contains(c.Id));
            var target = state.Timeline.IndexOf(clipId);
            if (anchor >= 0 && target >= 0)
                return Of(state, kind, clips.Skip(Math.Min(anchor, target)).Take(Math.Abs(anchor - target) + 1).Select(c => c.Id));
            return this;
        }
        if (toggle)
            return Of(state, kind, ids.Contains(clipId) ? ids.Remove(clipId) : ids.Add(clipId));
        // A click on a clip that is the only one selected keeps it (it may be the start of a drag).
        return ids.Count == 1 && ids.Contains(clipId) ? this : Of(state, kind, [clipId]);
    }

    /// <summary>The selection without clips that are gone (after undo or delete); a whole track stays.</summary>
    public TimelineSelection Pruned(ProjectState state) =>
        Kind is SelectionKind.TimelineClips or SelectionKind.TrackClips && Ids.Any(id => !Exists(state, Kind, id))
            ? Of(state, Kind, Ids)
            : this;

    private static TimelineSelection Of(ProjectState state, SelectionKind kind, IEnumerable<string> ids)
    {
        var kept = ids.Where(id => Exists(state, kind, id)).ToImmutableHashSet();
        return kept.IsEmpty ? None : new TimelineSelection(kind, kept, null);
    }

    private static bool Exists(ProjectState state, SelectionKind kind, string id) =>
        kind == SelectionKind.TrackClips ? state.TrackClip(id) is not null : state.Timeline.IndexOf(id) >= 0;

    public bool Equals(TimelineSelection? other) =>
        other is not null && Kind == other.Kind && Track == other.Track && Ids.SetEquals(other.Ids);

    public override int GetHashCode() => HashCode.Combine(Kind, Track, Ids.Count);
}
