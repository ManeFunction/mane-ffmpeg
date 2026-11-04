using ManeFfmpeg.UI.Controls;
using ManeFfmpeg.UI.ViewModels;
using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.UI.Tests;

/// <summary>Timeline gestures against a fake host, in timeline terms (100 pixels per second).</summary>
public class TimelineGestureTests
{
    private sealed class Host(Timeline timeline) : ITimelineGestureHost
    {
        public UndoStack<Timeline> History { get; } = new(timeline);
        public Timeline Timeline => History.Current;
        public HashSet<string> Selection { get; } = [];
        IReadOnlySet<string> ITimelineGestureHost.Selection => Selection;
        public double Zoom => 100;
        public List<(TimeSpan Time, bool Scrubbing)> Seeks { get; } = [];
        public bool ScrubEnded { get; private set; }
        public TimeSpan AudioDrag { get; private set; }
        public CutGrid? Grid { get; init; }

        public void Select(string clipId, bool toggle, bool range)
        {
            if (!toggle)
                Selection.Clear();
            Selection.Add(clipId);
        }

        public void ClearSelection() => Selection.Clear();
        public void Seek(TimeSpan time, bool scrubbing) => Seeks.Add((time, scrubbing));
        public void EndScrub() => ScrubEnded = true;
        public void BeginGesture() => History.BeginGesture();
        public void PreviewTimeline(Timeline timeline) => History.ReplaceCurrent(timeline);
        public void EndGesture() => History.EndGesture();
        public void MoveSelection(int insertIndex) => History.Commit(TimelineEditor.Move(Timeline, Selection, insertIndex));
        public CutGrid? GridOf(string sourceId) => Grid;
        public void PreviewAudioDrag(TimeSpan delta) => AudioDrag = delta;

        /// <summary>Where each clip's audio ends, by clip id; clips not listed have no audio.</summary>
        public Dictionary<string, TimeSpan> AudioEnds { get; } = [];
        public TimeSpan? AudioEnd(Clip clip) => AudioEnds.TryGetValue(clip.Id, out var end) ? end : null;
    }

    private static Clip C(string id, double inS, double outS) =>
        new() { Id = id, SourceId = "s", In = TimeSpan.FromSeconds(inS), Out = TimeSpan.FromSeconds(outS) };

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static Host ThreeClips(CutGrid? grid = null) => new(new Timeline([C("a", 0, 2), C("b", 10, 12), C("c", 20, 22)])) { Grid = grid };

    [Fact]
    public void A_click_that_barely_moves_selects_and_seeks()
    {
        var host = ThreeClips();
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Clip, S(2.5), "b"), command: false, shift: false);
        gestures.Move(S(2.53), shift: false);
        Assert.False(gestures.IsReordering);
        gestures.Release(S(2.53));
        Assert.Equal(["b"], host.Selection);
        Assert.Equal((S(2.53), false), host.Seeks[^1]);
        Assert.False(host.History.CanUndo);
    }

    [Fact]
    public void A_slip_drag_is_one_undo_step()
    {
        var host = ThreeClips();
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Audio, S(1.0), "a"), command: false, shift: false);
        gestures.Move(S(1.05), shift: false);
        gestures.Move(S(1.10), shift: false);
        gestures.Move(S(1.12), shift: false);
        Assert.Equal(TimeSpan.FromMilliseconds(120), host.AudioDrag);
        gestures.Release(S(1.12));

        Assert.Equal(TimeSpan.FromMilliseconds(120), host.Timeline.Clips[0].AudioOffset);
        Assert.Equal(TimeSpan.Zero, host.AudioDrag);
        host.History.Undo();
        Assert.Equal(TimeSpan.Zero, host.Timeline.Clips[0].AudioOffset);
        Assert.False(host.History.CanUndo);
    }

    [Fact]
    public void Slipping_replaced_audio_moves_the_replacement_in_one_undo_step()
    {
        var host = new Host(TimelineEditor.ReplaceAudio(ThreeClips().Timeline, "a", "f"));
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Audio, S(1.0), "a"), command: false, shift: false);
        gestures.Move(S(1.2), shift: false);
        gestures.Move(S(1.5), shift: false);
        gestures.Release(S(1.5));

        Assert.Equal(TimeSpan.FromMilliseconds(500), host.Timeline.Clips[0].ReplacedAudio!.Offset);
        Assert.Equal(TimeSpan.Zero, host.Timeline.Clips[0].AudioOffset);
        host.History.Undo();
        Assert.Equal(TimeSpan.Zero, host.Timeline.Clips[0].ReplacedAudio!.Offset);
    }

    [Fact]
    public void Shift_slips_at_a_tenth_of_the_speed()
    {
        var host = ThreeClips();
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Audio, S(1.0), "a"), command: false, shift: false);
        gestures.Move(S(1.05), shift: true);
        gestures.Move(S(1.12), shift: true);
        gestures.Release(S(1.12));
        Assert.Equal(TimeSpan.FromMilliseconds(12), host.Timeline.Clips[0].AudioOffset);
    }

    [Fact]
    public void Trimming_snaps_to_frames_in_one_undo_step()
    {
        var host = ThreeClips(new CutGrid(S(30), new Rational(30, 1)));
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.TrimOut, S(2.0), "a"), command: false, shift: false);
        gestures.Move(S(2.3), shift: false);
        gestures.Move(S(2.513), shift: false);
        gestures.Release(S(2.513));
        Assert.Equal(S(2.5), host.Timeline.Clips[0].Out);
        host.History.Undo();
        Assert.Equal(S(2), host.Timeline.Clips[0].Out);
    }

    [Fact]
    public void Reordering_drops_at_the_nearest_cut()
    {
        var host = ThreeClips();
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Clip, S(1.0), "a"), command: false, shift: false);
        gestures.Move(S(5.1), shift: false);
        Assert.True(gestures.IsReordering);
        Assert.Equal(3, gestures.InsertIndex);
        gestures.Release(S(5.1));
        Assert.Equal("bca", string.Concat(host.Timeline.Clips.Select(c => c.Id)));
    }

    [Fact]
    public void Pressing_an_empty_track_clears_the_selection_and_scrubs_but_the_ruler_keeps_it()
    {
        var host = ThreeClips();
        host.Selection.Add("a");
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Ruler, S(3)), command: false, shift: false);
        gestures.Release(S(3));
        Assert.Equal(["a"], host.Selection);

        gestures.Press(new TimelineHit(TimelineZone.Empty, S(3)), command: false, shift: false);
        gestures.Move(S(3.5), shift: false);
        gestures.Release(S(3.5));
        Assert.Empty(host.Selection);
        Assert.Equal([(S(3), true), (S(3), true), (S(3.5), true)], host.Seeks);
        Assert.True(host.ScrubEnded);
    }

    [Fact]
    public void Shift_scrubbing_snaps_to_clip_edges_within_eight_pixels()
    {
        var host = ThreeClips();
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Ruler, S(1.95)), command: false, shift: true);
        gestures.Move(S(2.5), shift: true);
        gestures.Move(S(3.9), shift: true);
        gestures.Move(S(3.96), shift: true);
        gestures.Move(S(3.96), shift: false);
        gestures.Release(S(3.96));
        Assert.Equal([(S(2), true), (S(2.5), true), (S(3.9), true), (S(4), true), (S(3.96), true)], host.Seeks);
        Assert.True(host.ScrubEnded);
    }

    [Fact]
    public void Pressing_or_releasing_shift_mid_scrub_snaps_or_lets_go_at_once()
    {
        var host = ThreeClips();
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Empty, S(5)), command: false, shift: false);
        gestures.Move(S(5.95), shift: false);
        gestures.ShiftChanged(true);
        gestures.ShiftChanged(false);
        gestures.Release(S(5.95));
        gestures.ShiftChanged(true);
        Assert.Equal([(S(5), true), (S(5.95), true), (S(6), true), (S(5.95), true)], host.Seeks);
    }

    [Fact]
    public void Shift_scrubbing_snaps_to_where_audio_starts_or_runs_out_inside_a_clip()
    {
        // a: its audio is heard 0.5 s late, so it starts 0.5 s into the clip.
        // b: the file's audio ends at 11 s, a second into the clip (source 10–12), so at 3 s on the timeline.
        var host = new Host(new Timeline([C("a", 0, 2) with { AudioOffset = S(0.5) }, C("b", 10, 12), C("c", 20, 22)]));
        host.AudioEnds["a"] = S(30);
        host.AudioEnds["b"] = S(11);
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Ruler, S(0.45)), command: false, shift: true);
        gestures.Move(S(3.05), shift: true);
        gestures.Move(S(3.05), shift: false);
        gestures.Release(S(3.05));
        Assert.Equal([(S(0.5), true), (S(3), true), (S(3.05), true)], host.Seeks);
    }

    [Fact]
    public void Shift_scrubbing_snaps_to_where_looped_audio_starts_again()
    {
        var timeline = TimelineEditor.ReplaceAudio(ThreeClips().Timeline, "c", "f");
        var host = new Host(TimelineEditor.SetAudioLoop(timeline, new HashSet<string> { "c" }, loop: true));
        host.AudioEnds["c"] = S(0.75);
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Ruler, S(4.8)), command: false, shift: true);
        gestures.Move(S(5.45), shift: true);
        gestures.Move(S(5.3), shift: true);
        gestures.Release(S(5.3));
        Assert.Equal([(S(4.75), true), (S(5.5), true), (S(5.3), true)], host.Seeks);
    }

    [Fact]
    public void Hit_testing_finds_edges_bodies_and_audio()
    {
        var timeline = new Timeline([C("a", 0, 2), C("b", 10, 12)]);
        TimelineZone Zone(double x, double y) => TimelineLayout.HitTest(timeline, 100, 0, x, y).Zone;
        var x0 = TimelineLayout.X(100, 0, S(2));
        Assert.Equal(TimelineZone.Ruler, Zone(x0, TimelineLayout.RulerCenter));
        Assert.Equal(TimelineZone.TrimOut, Zone(x0 - 2, TimelineLayout.VideoCenter));
        Assert.Equal(TimelineZone.TrimIn, Zone(x0 + 2, TimelineLayout.VideoCenter));
        Assert.Equal(TimelineZone.Clip, Zone(x0 + 50, TimelineLayout.VideoCenter));
        Assert.Equal(TimelineZone.Audio, Zone(x0 + 50, TimelineLayout.AudioCenter));
        Assert.Equal(TimelineZone.Empty, Zone(TimelineLayout.X(100, 0, S(5)), TimelineLayout.VideoCenter));
        Assert.Equal("b", TimelineLayout.HitTest(timeline, 100, 0, x0 + 50, TimelineLayout.AudioCenter).ClipId);
    }
}
