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

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public static Timeline Append(Timeline timeline, Clip clip) => timeline with { Clips = timeline.Clips.Add(clip) };

    public static Timeline Insert(Timeline timeline, int index, Clip clip) =>
        timeline with { Clips = timeline.Clips.Insert(Math.Clamp(index, 0, timeline.Clips.Count), clip) };

    /// <summary>
    /// Splits the clip under <paramref name="at"/> (timeline time) at source time <paramref name="sourceTime"/>,
    /// which the caller has already snapped to a frame or keyframe. Returns the id of the right half, or null.
    /// </summary>
    public static (Timeline Timeline, string? RightId) Split(Timeline timeline, int clipIndex, TimeSpan sourceTime)
    {
        if (clipIndex < 0 || clipIndex >= timeline.Clips.Count)
            return (timeline, null);
        var clip = timeline.Clips[clipIndex];
        if (sourceTime - clip.In < MinClipDuration || clip.Out - sourceTime < MinClipDuration)
            return (timeline, null);
        var left = clip with { Out = sourceTime };
        var right = clip with { Id = NewId(), In = sourceTime };
        var clips = timeline.Clips.SetItem(clipIndex, left).Insert(clipIndex + 1, right);
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

    /// <summary>Moves a clip's in point (ripple); <paramref name="newIn"/> is clamped to the source and min duration.</summary>
    public static Timeline TrimIn(Timeline timeline, string clipId, TimeSpan newIn)
    {
        var index = timeline.IndexOf(clipId);
        if (index < 0)
            return timeline;
        var clip = timeline.Clips[index];
        newIn = Clamp(newIn, TimeSpan.Zero, clip.Out - MinClipDuration);
        return newIn == clip.In ? timeline : timeline with { Clips = timeline.Clips.SetItem(index, clip with { In = newIn }) };
    }

    /// <summary>Moves a clip's out point (ripple), clamped to <paramref name="sourceDuration"/>.</summary>
    public static Timeline TrimOut(Timeline timeline, string clipId, TimeSpan newOut, TimeSpan sourceDuration)
    {
        var index = timeline.IndexOf(clipId);
        if (index < 0)
            return timeline;
        var clip = timeline.Clips[index];
        newOut = Clamp(newOut, clip.In + MinClipDuration, sourceDuration);
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

    /// <summary>Replaces a clip's in/out in one step (inspector edits).</summary>
    public static Timeline SetRange(Timeline timeline, string clipId, TimeSpan newIn, TimeSpan newOut, TimeSpan sourceDuration)
    {
        var index = timeline.IndexOf(clipId);
        if (index < 0)
            return timeline;
        var clip = timeline.Clips[index];
        newIn = Clamp(newIn, TimeSpan.Zero, sourceDuration - MinClipDuration);
        newOut = Clamp(newOut, newIn + MinClipDuration, sourceDuration);
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

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}
