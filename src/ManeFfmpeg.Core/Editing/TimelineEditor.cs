using System.Collections.Immutable;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Editing;

/// <summary>Pure edit operations; each returns a new timeline (or the same one when nothing changes).</summary>
public static class TimelineEditor
{
    /// <summary>Shortest clip an edit may leave behind.</summary>
    public static readonly TimeSpan MinClipDuration = TimeSpan.FromMilliseconds(40);

    /// <summary>Largest audio slip in either direction.</summary>
    public static readonly TimeSpan MaxAudioOffset = TimeSpan.FromSeconds(60);

    public static Timeline Append(Timeline timeline, Clip clip) => timeline with { Clips = timeline.Clips.Add(clip) };

    public static Timeline Insert(Timeline timeline, int index, Clip clip) =>
        timeline with { Clips = timeline.Clips.Insert(Math.Clamp(index, 0, timeline.Clips.Count), clip) };

    /// <summary>
    /// Splits the clip playing at timeline time <paramref name="at"/>, at the nearest in point of its source's grid
    /// (the cut starts the right half). Returns the id of the right half, or null when the cut is too close to an edge.
    /// </summary>
    public static (Timeline Timeline, string? RightId) Split(Timeline timeline, TimeSpan at, Func<string, CutGrid?> grids)
    {
        if (timeline.Locate(at) is not { } hit)
            return (timeline, null);
        var clip = timeline.Clips[hit.Index];
        var cut = Grid(clip, grids).SnapInPoint(clip.In + (at - hit.ClipStart));
        if (cut - clip.In < MinClipDuration || clip.Out - cut < MinClipDuration)
            return (timeline, null);
        var left = clip with { Out = cut };
        var right = clip with { Id = Ids.New(), In = cut };
        var clips = timeline.Clips.SetItem(hit.Index, left).Insert(hit.Index + 1, right);
        return (timeline with { Clips = clips }, right.Id);
    }

    public static Timeline Delete(Timeline timeline, IReadOnlySet<string> clipIds) =>
        timeline with { Clips = timeline.Clips.RemoveAll(c => clipIds.Contains(c.Id)) };

    /// <summary>
    /// Moves the given clips (keeping their order) so they start at <paramref name="insertIndex"/>, an index into
    /// the current list (as if the moved clips were still in place).
    /// </summary>
    public static Timeline Move(Timeline timeline, IReadOnlySet<string> clipIds, int insertIndex)
    {
        var moving = timeline.Clips.Where(c => clipIds.Contains(c.Id)).ToList();
        if (moving.Count == 0)
            return timeline;
        var before = timeline.Clips.Take(Math.Clamp(insertIndex, 0, timeline.Clips.Count)).Count(c => clipIds.Contains(c.Id));
        var rest = timeline.Clips.RemoveAll(c => clipIds.Contains(c.Id));
        var target = Math.Clamp(insertIndex - before, 0, rest.Count);
        var result = rest.InsertRange(target, moving);
        return result.SequenceEqual(timeline.Clips) ? timeline : timeline with { Clips = result };
    }

    /// <summary>Moves a clip's in point (ripple), snapped to its source's grid and clamped to the source and min duration.</summary>
    public static Timeline TrimIn(Timeline timeline, string clipId, TimeSpan newIn, Func<string, CutGrid?> grids)
    {
        var index = timeline.IndexOf(clipId);
        if (index < 0)
            return timeline;
        var clip = timeline.Clips[index];
        newIn = Clamp(Grid(clip, grids).SnapInPoint(newIn), TimeSpan.Zero, clip.Out - MinClipDuration);
        return newIn == clip.In ? timeline : timeline with { Clips = timeline.Clips.SetItem(index, clip with { In = newIn }) };
    }

    /// <summary>Moves a clip's out point (ripple), snapped to its source's grid and clamped to the source's end.</summary>
    public static Timeline TrimOut(Timeline timeline, string clipId, TimeSpan newOut, Func<string, CutGrid?> grids)
    {
        var index = timeline.IndexOf(clipId);
        if (index < 0)
            return timeline;
        var clip = timeline.Clips[index];
        var grid = Grid(clip, grids);
        newOut = Clamp(grid.SnapOutPoint(newOut), clip.In + MinClipDuration, grid.Duration);
        return newOut == clip.Out ? timeline : timeline with { Clips = timeline.Clips.SetItem(index, clip with { Out = newOut }) };
    }

    public static Timeline SetAudioOffset(Timeline timeline, IReadOnlySet<string> clipIds, TimeSpan offset) =>
        Map(timeline, clipIds, c => c with { AudioOffset = Clamp(offset, -MaxAudioOffset, MaxAudioOffset) });

    public static Timeline NudgeAudioOffset(Timeline timeline, IReadOnlySet<string> clipIds, TimeSpan delta) =>
        Map(timeline, clipIds, c => c with { AudioOffset = Clamp(c.AudioOffset + delta, -MaxAudioOffset, MaxAudioOffset) });

    /// <summary>Gives every clip of <paramref name="sourceId"/> the same audio offset.</summary>
    public static Timeline SetAudioOffsetForSource(Timeline timeline, string sourceId, TimeSpan offset)
    {
        var ids = timeline.Clips.Where(c => c.SourceId == sourceId).Select(c => c.Id).ToHashSet();
        return SetAudioOffset(timeline, ids, offset);
    }

    public static Timeline RemoveSource(Timeline timeline, string sourceId) =>
        timeline with { Clips = timeline.Clips.RemoveAll(c => c.SourceId == sourceId) };

    /// <summary>Replaces a clip's in/out in one step (inspector edits), snapped and clamped like trims.</summary>
    public static Timeline SetRange(Timeline timeline, string clipId, TimeSpan newIn, TimeSpan newOut, Func<string, CutGrid?> grids)
    {
        var index = timeline.IndexOf(clipId);
        if (index < 0)
            return timeline;
        var clip = timeline.Clips[index];
        var grid = Grid(clip, grids);
        newIn = Clamp(grid.SnapInPoint(newIn), TimeSpan.Zero, grid.Duration - MinClipDuration);
        newOut = Clamp(grid.SnapOutPoint(newOut), newIn + MinClipDuration, grid.Duration);
        return newIn == clip.In && newOut == clip.Out
            ? timeline
            : timeline with { Clips = timeline.Clips.SetItem(index, clip with { In = newIn, Out = newOut }) };
    }

    private static Timeline Map(Timeline timeline, IReadOnlySet<string> clipIds, Func<Clip, Clip> change)
    {
        var builder = timeline.Clips.ToBuilder();
        var changed = false;
        for (var i = 0; i < builder.Count; i++)
        {
            if (!clipIds.Contains(builder[i].Id))
                continue;
            var updated = change(builder[i]);
            if (updated == builder[i])
                continue;
            builder[i] = updated;
            changed = true;
        }
        return changed ? timeline with { Clips = builder.ToImmutable() } : timeline;
    }

    private static CutGrid Grid(Clip clip, Func<string, CutGrid?> grids) => grids(clip.SourceId) ?? CutGrid.Unknown(clip);

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}
