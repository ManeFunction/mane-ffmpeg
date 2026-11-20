using System.Collections.Immutable;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Editing;

/// <summary>
/// Pure edits of the A2 track. Each returns a new track (or the same one when nothing changes) that is still ordered by
/// start, with no clip overlapping another: moves and trims stop at the neighbours, and a new clip fills at most the gap
/// it lands in.
/// </summary>
public static class TrackEditor
{
    /// <summary>
    /// Places <paramref name="length"/> of file <paramref name="fileId"/> (from its start) at timeline time
    /// <paramref name="start"/>, cut short where the next clip begins. Returns the new clip's id, or null when the time is
    /// already taken by a clip (or there is no room).
    /// </summary>
    public static (ImmutableList<TrackClip> Track, string? Id) Add(ImmutableList<TrackClip> track, string fileId, TimeSpan start, TimeSpan length)
    {
        if (start < TimeSpan.Zero)
            start = TimeSpan.Zero;
        if (track.Any(c => c.Start <= start && start < c.End))
            return (track, null);
        var next = track.FirstOrDefault(c => c.Start >= start);
        if (next is not null && next.Start - start < length)
            length = next.Start - start;
        if (length < TimelineEditor.MinClipDuration)
            return (track, null);
        var clip = TrackClip.Create(fileId, start, TimeSpan.Zero, length);
        return (Sorted(track.Add(clip)), clip.Id);
    }

    /// <summary>
    /// Moves a clip to start at <paramref name="start"/>, as far as the gap it is in allows: it never jumps over or
    /// into a neighbour, nor before the timeline's start.
    /// </summary>
    public static ImmutableList<TrackClip> Move(ImmutableList<TrackClip> track, string id, TimeSpan start)
    {
        var index = track.FindIndex(c => c.Id == id);
        if (index < 0)
            return track;
        var clip = track[index];
        var min = index > 0 ? track[index - 1].End : TimeSpan.Zero;
        var max = index + 1 < track.Count ? track[index + 1].Start - clip.Duration : TimeSpan.MaxValue;
        start = TimelineEditor.Clamp(start, min, max < min ? min : max);
        return start == clip.Start ? track : track.SetItem(index, clip with { Start = start });
    }

    /// <summary>Moves a clip's left edge: its in point and start move together, so the rest of it stays in place.</summary>
    public static ImmutableList<TrackClip> TrimIn(ImmutableList<TrackClip> track, string id, TimeSpan newIn)
    {
        var index = track.FindIndex(c => c.Id == id);
        if (index < 0)
            return track;
        var clip = track[index];
        var earliestStart = index > 0 ? track[index - 1].End : TimeSpan.Zero;
        // In can't go before the file's start, nor the start before the previous clip.
        var minIn = Max(TimeSpan.Zero, clip.In - (clip.Start - earliestStart));
        newIn = TimelineEditor.Clamp(newIn, minIn, clip.Out - TimelineEditor.MinClipDuration);
        return newIn == clip.In ? track : track.SetItem(index, clip with { In = newIn, Start = clip.Start + (newIn - clip.In) });
    }

    /// <summary>Moves a clip's right edge, up to the file's end (when known) and the next clip.</summary>
    public static ImmutableList<TrackClip> TrimOut(ImmutableList<TrackClip> track, string id, TimeSpan newOut, TimeSpan? fileEnd)
    {
        var index = track.FindIndex(c => c.Id == id);
        if (index < 0)
            return track;
        var clip = track[index];
        var max = index + 1 < track.Count ? clip.Out + (track[index + 1].Start - clip.End) : TimeSpan.MaxValue;
        if (fileEnd is { } end && end < max)
            max = end;
        newOut = TimelineEditor.Clamp(newOut, clip.In + TimelineEditor.MinClipDuration, Max(max, clip.In + TimelineEditor.MinClipDuration));
        return newOut == clip.Out ? track : track.SetItem(index, clip with { Out = newOut });
    }

    /// <summary>
    /// Cuts the selected clips under timeline time <paramref name="at"/> in two; the right halves get new ids. Clips the time
    /// isn't inside (or only within the shortest clip length of an edge) are left alone.
    /// </summary>
    public static ImmutableList<TrackClip> Split(ImmutableList<TrackClip> track, IReadOnlySet<string> ids, TimeSpan at)
    {
        var builder = track.ToBuilder();
        for (var i = builder.Count - 1; i >= 0; i--)
        {
            var clip = builder[i];
            if (!ids.Contains(clip.Id) || at - clip.Start < TimelineEditor.MinClipDuration || clip.End - at < TimelineEditor.MinClipDuration)
                continue;
            var cut = clip.In + (at - clip.Start);
            builder[i] = clip with { Out = cut };
            builder.Insert(i + 1, clip with { Id = Ids.New(), In = cut, Start = at });
        }
        return builder.Count == track.Count ? track : builder.ToImmutable();
    }

    public static ImmutableList<TrackClip> Delete(ImmutableList<TrackClip> track, IReadOnlySet<string> ids)
    {
        var kept = track.RemoveAll(c => ids.Contains(c.Id));
        return kept.Count == track.Count ? track : kept;
    }

    public static ImmutableList<TrackClip> SetVolume(ImmutableList<TrackClip> track, IReadOnlySet<string> ids, double gain)
    {
        gain = Gain.Clamp(gain);
        var builder = track.ToBuilder();
        var changed = false;
        for (var i = 0; i < builder.Count; i++)
        {
            if (!ids.Contains(builder[i].Id) || builder[i].Volume == gain)
                continue;
            builder[i] = builder[i] with { Volume = gain };
            changed = true;
        }
        return changed ? builder.ToImmutable() : track;
    }

    /// <summary>Gives every A2 clip of file <paramref name="fileId"/> the same volume.</summary>
    public static ImmutableList<TrackClip> SetVolumeForFile(ImmutableList<TrackClip> track, string fileId, double gain) =>
        SetVolume(track, track.Where(c => c.FileId == fileId).Select(c => c.Id).ToHashSet(), gain);

    /// <summary>The clips of a removed audio file go too.</summary>
    public static ImmutableList<TrackClip> RemoveFile(ImmutableList<TrackClip> track, string fileId)
    {
        var kept = track.RemoveAll(c => c.FileId == fileId);
        return kept.Count == track.Count ? track : kept;
    }

    /// <summary>
    /// A track as it should be (a damaged or hand-edited project): clips too short dropped, ordered by start, and each
    /// clip cut short where the next one starts.
    /// </summary>
    public static ImmutableList<TrackClip> Normalize(IEnumerable<TrackClip> clips)
    {
        var ordered = clips.Where(c => c.Duration >= TimelineEditor.MinClipDuration)
            .Select(c => c with { Start = Max(c.Start, TimeSpan.Zero), In = Max(c.In, TimeSpan.Zero), Volume = Gain.Clamp(c.Volume) })
            .OrderBy(c => c.Start).ToList();
        var result = ImmutableList.CreateBuilder<TrackClip>();
        foreach (var clip in ordered)
        {
            var kept = clip;
            if (result.Count > 0 && result[^1].End > kept.Start)
            {
                var overlap = result[^1].End - kept.Start;
                if (kept.Duration - overlap < TimelineEditor.MinClipDuration)
                    continue;
                kept = kept with { Start = kept.Start + overlap, In = kept.In + overlap };
            }
            result.Add(kept);
        }
        return result.ToImmutable();
    }

    private static ImmutableList<TrackClip> Sorted(ImmutableList<TrackClip> track) =>
        track.Sort((a, b) => a.Start.CompareTo(b.Start));

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
