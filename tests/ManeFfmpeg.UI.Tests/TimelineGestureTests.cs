using ManeFfmpeg.UI.Controls;
using ManeFfmpeg.UI.ViewModels;
using ManeFfmpeg.Core.Editing;
using System.Collections.Immutable;
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
        public void BeginGesture()
        {
            History.BeginGesture();
            TrackHistory.BeginGesture();
        }
        public void PreviewTimeline(Timeline timeline) => History.ReplaceCurrent(timeline);
        public void EndGesture()
        {
            History.EndGesture();
            TrackHistory.EndGesture();
        }
        public void MoveSelection(int insertIndex) => History.Commit(TimelineEditor.Move(Timeline, Selection, insertIndex));
        public CutGrid? GridOf(string sourceId) => Grid;
        public void PreviewAudioDrag(TimeSpan delta) => AudioDrag = delta;

        /// <summary>Where each clip's audio ends, by clip id; clips not listed have no audio.</summary>
        public Dictionary<string, TimeSpan> AudioEnds { get; } = [];
        public TimeSpan? AudioEnd(Clip clip) => AudioEnds.TryGetValue(clip.Id, out var end) ? end : null;

        public UndoStack<ImmutableList<TrackClip>> TrackHistory { get; init; } = new([]);
        public ImmutableList<TrackClip> Track => TrackHistory.Current;
        public void PreviewTrack(ImmutableList<TrackClip> track) => TrackHistory.ReplaceCurrent(track);
        public TimeSpan? FileEnd(TrackClip clip) => S(60);
    }

    private static Clip C(string id, double inS, double outS) =>
        new() { Id = id, SourceId = "s", In = TimeSpan.FromSeconds(inS), Out = TimeSpan.FromSeconds(outS) };

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static Host ThreeClips(CutGrid? grid = null) => new(new Timeline([C("a", 0, 2), C("b", 10, 12), C("c", 20, 22)])) { Grid = grid };

    [Fact]
    public void A_click_on_a_clip_that_barely_moves_selects_it_and_leaves_the_playhead()
    {
        var host = ThreeClips();
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Clip, S(2.5), "b"), command: false, shift: false);
        gestures.Move(S(2.53), shift: false);
        Assert.False(gestures.IsReordering);
        gestures.Release();
        Assert.Equal(["b"], host.Selection);

        gestures.Press(new TimelineHit(TimelineZone.Audio, S(20.5), "c"), command: false, shift: false);
        gestures.Release();
        Assert.Equal(["c"], host.Selection);
        Assert.Empty(host.Seeks);
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
        gestures.Release();

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
        gestures.Release();

        Assert.Equal(TimeSpan.FromMilliseconds(500), host.Timeline.Clips[0].ReplacedAudio!.Offset);
        Assert.Equal(TimeSpan.Zero, host.Timeline.Clips[0].AudioOffset);
        host.History.Undo();
        Assert.Equal(TimeSpan.Zero, host.Timeline.Clips[0].ReplacedAudio!.Offset);
    }

    [Fact]
    public void Alt_slips_at_a_tenth_of_the_speed()
    {
        var host = ThreeClips();
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Audio, S(1.0), "a"), command: false, shift: false);
        gestures.Move(S(1.05), shift: false, alt: true);
        gestures.Move(S(1.12), shift: false, alt: true);
        gestures.Release();
        Assert.Equal(TimeSpan.FromMilliseconds(12), host.Timeline.Clips[0].AudioOffset);
    }

    [Fact]
    public void Alt_pressed_or_released_mid_slip_changes_speed_from_where_the_audio_is()
    {
        var host = ThreeClips();
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Audio, S(1.0), "a"), command: false, shift: false);
        gestures.Move(S(1.5), shift: false);
        Assert.Equal(S(0.5), host.Timeline.Clips[0].AudioOffset);
        gestures.Move(S(1.7), shift: false, alt: true);
        Assert.Equal(S(0.52), host.Timeline.Clips[0].AudioOffset);
        gestures.AltChanged(false);
        gestures.Move(S(1.8), shift: false);
        Assert.Equal(S(0.62), host.Timeline.Clips[0].AudioOffset);
        gestures.AltChanged(true);
        gestures.Move(S(0.8), shift: false, alt: true);
        Assert.Equal(S(0.52), host.Timeline.Clips[0].AudioOffset);
        gestures.Release();
        Assert.Equal(S(0.52), host.Timeline.Clips[0].AudioOffset);
        host.History.Undo();
        Assert.Equal(TimeSpan.Zero, host.Timeline.Clips[0].AudioOffset);
    }

    [Fact]
    public void Shift_snaps_the_slipped_audio_start_to_the_clip_end()
    {
        var host = ThreeClips();
        host.AudioEnds["a"] = S(2);
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Audio, S(0.1), "a"), command: false, shift: false);
        gestures.Move(S(1.0), shift: true);
        Assert.Equal(S(0.9), host.Timeline.Clips[0].AudioOffset);
        gestures.Move(S(2.05), shift: true);
        Assert.Equal(S(2), host.Timeline.Clips[0].AudioOffset);
        gestures.Release();
        Assert.Equal(S(2), host.Timeline.Clips[0].AudioOffset);
    }

    [Fact]
    public void Shift_snaps_the_slipped_audio_end_to_the_clip_start()
    {
        var host = new Host(new Timeline([C("a", 0, 2), C("b", 10, 13)]));
        host.AudioEnds["b"] = S(14);
        var gestures = new TimelineGestures(host);
        // b's file runs out at 14 s (source time), 4 s after its in point: a slip of −4 s puts that at b's start.
        gestures.Press(new TimelineHit(TimelineZone.Audio, S(4.5), "b"), command: false, shift: false);
        gestures.Move(S(0.55), shift: true);
        Assert.Equal(S(-4), host.Timeline.Clips[1].AudioOffset);
        gestures.Move(S(0.55), shift: false);
        Assert.Equal(S(-3.95), host.Timeline.Clips[1].AudioOffset);
        gestures.ShiftChanged(true);
        Assert.Equal(S(-4), host.Timeline.Clips[1].AudioOffset);
        gestures.Release();
    }

    [Fact]
    public void Shift_snaps_a_loop_seam_to_the_clip_start()
    {
        var replaced = TimelineEditor.ReplaceAudio(ThreeClips().Timeline, "a", "f");
        var host = new Host(TimelineEditor.SetAudioLoop(replaced, new HashSet<string> { "a" }, true));
        host.AudioEnds["a"] = S(0.5);
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Audio, S(1.0), "a"), command: false, shift: false);
        gestures.Move(S(1.46), shift: true);
        Assert.Equal(S(0.5), host.Timeline.Clips[0].ReplacedAudio!.Offset);
        gestures.Release();
    }

    [Fact]
    public void Trimming_snaps_to_frames_in_one_undo_step()
    {
        var host = ThreeClips(new CutGrid(S(30), new Rational(30, 1)));
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.TrimOut, S(2.0), "a"), command: false, shift: false);
        gestures.Move(S(2.3), shift: false);
        gestures.Move(S(2.513), shift: false);
        gestures.Release();
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
        gestures.Release();
        Assert.Equal("bca", string.Concat(host.Timeline.Clips.Select(c => c.Id)));
    }

    [Fact]
    public void The_ruler_scrubs_and_keeps_the_selection()
    {
        var host = ThreeClips();
        host.Selection.Add("a");
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Ruler, S(3)), command: false, shift: false);
        gestures.Move(S(3.5), shift: false);
        gestures.Release();
        Assert.Equal(["a"], host.Selection);
        Assert.Equal([(S(3), true), (S(3.5), true)], host.Seeks);
        Assert.True(host.ScrubEnded);
    }

    [Fact]
    public void Pressing_or_dragging_on_an_empty_track_clears_the_selection_without_seeking()
    {
        var host = ThreeClips();
        host.Selection.Add("a");
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Empty, S(3)), command: false, shift: true);
        Assert.True(gestures.IsActive);
        gestures.Move(S(3.5), shift: false);
        gestures.ShiftChanged(true);
        gestures.Release();
        Assert.False(gestures.IsActive);
        Assert.Empty(host.Selection);
        Assert.Empty(host.Seeks);
        Assert.False(host.ScrubEnded);
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
        gestures.Release();
        Assert.Equal([(S(2), true), (S(2.5), true), (S(3.9), true), (S(4), true), (S(3.96), true)], host.Seeks);
        Assert.True(host.ScrubEnded);
    }

    [Fact]
    public void Pressing_or_releasing_shift_mid_scrub_snaps_or_lets_go_at_once()
    {
        var host = ThreeClips();
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.Ruler, S(5)), command: false, shift: false);
        gestures.Move(S(5.95), shift: false);
        gestures.ShiftChanged(true);
        gestures.ShiftChanged(false);
        gestures.Release();
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
        gestures.Release();
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
        gestures.Release();
        Assert.Equal([(S(4.75), true), (S(5.5), true), (S(5.3), true)], host.Seeks);
    }

    [Fact]
    public void Hit_testing_finds_edges_bodies_and_audio()
    {
        var timeline = new Timeline([C("a", 0, 2), C("b", 10, 12)]);
        var state = new ProjectState([], timeline) { AudioTrack = [T("m", 3, 0, 5)] };
        TimelineZone Zone(double x, double y) => TimelineLayout.HitTest(state, 100, 0, x, y).Zone;
        var x0 = TimelineLayout.X(100, 0, S(2));
        Assert.Equal(TimelineZone.Ruler, Zone(x0, TimelineLayout.RulerCenter));
        Assert.Equal(TimelineZone.TrimOut, Zone(x0 - 2, TimelineLayout.VideoCenter));
        Assert.Equal(TimelineZone.TrimIn, Zone(x0 + 2, TimelineLayout.VideoCenter));
        Assert.Equal(TimelineZone.Clip, Zone(x0 + 50, TimelineLayout.VideoCenter));
        Assert.Equal(TimelineZone.Audio, Zone(x0 + 50, TimelineLayout.AudioCenter));
        Assert.Equal(TimelineZone.Empty, Zone(TimelineLayout.X(100, 0, S(5)), TimelineLayout.VideoCenter));
        Assert.Equal("b", TimelineLayout.HitTest(state, 100, 0, x0 + 50, TimelineLayout.AudioCenter).ClipId);

        var m0 = TimelineLayout.X(100, 0, S(3));
        Assert.Equal(TimelineZone.TrackTrimIn, Zone(m0 + 2, TimelineLayout.TrackCenter));
        Assert.Equal(TimelineZone.TrackClip, Zone(m0 + 100, TimelineLayout.TrackCenter));
        Assert.Equal(TimelineZone.TrackTrimOut, Zone(TimelineLayout.X(100, 0, S(8)) - 2, TimelineLayout.TrackCenter));
        Assert.Equal("m", TimelineLayout.HitTest(state, 100, 0, m0 + 100, TimelineLayout.TrackCenter).ClipId);
        Assert.Equal(TimelineZone.Empty, Zone(TimelineLayout.X(100, 0, S(1)), TimelineLayout.TrackCenter));
    }

    [Fact]
    public void Following_the_playhead_pages_forward_only_while_clips_lie_past_the_view()
    {
        // 10 s on screen of a 25 s timeline.
        double Follow(double scroll, double playhead, double duration = 25) =>
            TimelineLayout.FollowScroll(scroll, 10, S(playhead), S(duration));
        Assert.Equal(0, Follow(0, 9));
        Assert.Equal(9, Follow(0, 10));
        // The last page ends at the timeline's end instead of leaving it near the left edge.
        Assert.Equal(15, Follow(9, 19.6));
        Assert.Equal(15, Follow(15, 24.8));
        // The whole timeline on screen: no paging at the end.
        Assert.Equal(0, Follow(0, 9.8, duration: 10));
        // Scrolled past the end by hand: left where it is.
        Assert.Equal(18, Follow(18, 27.8));
        // Back before the view (a loop or a jump): pages back.
        Assert.Equal(0, Follow(15, 0.5));
    }

    private static TrackClip T(string id, double start, double inS, double outS) =>
        new() { Id = id, FileId = "f", Start = S(start), In = S(inS), Out = S(outS) };

    private static Host WithTrack() => new(new Timeline([C("a", 0, 4), C("b", 10, 14)]))
    {
        TrackHistory = new([T("m", 1, 0, 2), T("n", 5, 10, 12)]),
    };

    [Fact]
    public void An_A2_clip_drags_along_the_timeline_but_not_into_its_neighbour_and_snaps_only_with_Shift()
    {
        var host = WithTrack();
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.TrackClip, S(1.5), "m"), command: false, shift: false);
        gestures.Move(S(1.53), shift: false);
        Assert.Equal(S(1), host.Track[0].Start);
        // Its end lands at 3.97 s, 3 px from V1's cut at 4 s: no snap without Shift…
        gestures.Move(S(2.47), shift: false);
        Assert.Equal(S(1.97), host.Track[0].Start);
        // …and with it the end meets the cut.
        gestures.Move(S(2.47), shift: true);
        Assert.Equal((S(2), S(4)), (host.Track[0].Start, host.Track[0].End));
        gestures.ShiftChanged(false);
        Assert.Equal(S(1.97), host.Track[0].Start);
        // With Shift the start snaps too: 0.04 s from the timeline's start.
        gestures.Move(S(0.54), shift: true);
        Assert.Equal(S(0), host.Track[0].Start);
        // Not into "n" (5–7 s): it stops where it meets it.
        gestures.Move(S(9), shift: false);
        Assert.Equal(S(5), host.Track[0].End);
        gestures.Release();
        Assert.Equal(["m"], host.Selection);
        Assert.True(host.TrackHistory.CanUndo);
        host.TrackHistory.Undo();
        Assert.Equal(S(1), host.Track[0].Start);
    }

    [Fact]
    public void With_Shift_an_A2_clip_edge_snaps_where_the_playhead_would()
    {
        var host = WithTrack();
        var gestures = new TimelineGestures(host);
        // "n" ends at 7 s; V1's cut at 4 s… is out of reach, but the end of the timeline (8 s) is 6 px from 7.94 s.
        gestures.Press(new TimelineHit(TimelineZone.TrackTrimOut, S(7), "n"), command: false, shift: false);
        gestures.Move(S(7.94), shift: false);
        Assert.Equal(S(7.94), host.Track[1].End);
        gestures.Move(S(7.94), shift: true);
        Assert.Equal(S(8), host.Track[1].End);
        // Letting go of Shift with the pointer still lets the edge go back to it.
        gestures.ShiftChanged(false);
        Assert.Equal(S(7.94), host.Track[1].End);
        gestures.Release();

        // The left edge snaps to V1's cut at 4 s.
        gestures.Press(new TimelineHit(TimelineZone.TrackTrimIn, S(5), "n"), command: false, shift: false);
        gestures.Move(S(4.05), shift: true);
        Assert.Equal((S(4), S(9)), (host.Track[1].Start, host.Track[1].In));
        gestures.Release();
    }

    [Fact]
    public void A2_clip_edges_trim_within_the_file_and_the_gap()
    {
        var host = WithTrack();
        var gestures = new TimelineGestures(host);
        gestures.Press(new TimelineHit(TimelineZone.TrackTrimIn, S(5), "n"), command: false, shift: false);
        gestures.Move(S(4), shift: false);
        Assert.Equal((S(4), S(9)), (host.Track[1].Start, host.Track[1].In));
        // Not over "m", which ends at 3 s.
        gestures.Move(S(1), shift: false);
        Assert.Equal((S(3), S(8)), (host.Track[1].Start, host.Track[1].In));
        gestures.Release();

        gestures.Press(new TimelineHit(TimelineZone.TrackTrimOut, S(3), "m"), command: false, shift: false);
        gestures.Move(S(10), shift: false);
        // Up to where "n" now starts.
        Assert.Equal(S(3), host.Track[0].End);
        gestures.Release();
        Assert.Equal(["m"], host.Selection);
    }

    [Fact]
    public void Fitting_a_range_fills_the_width_with_padding_split_around_it()
    {
        var (zoom, scroll) = TimelineLayout.FitRange(824, S(10), S(18), 0.02, 1200);
        Assert.Equal(100, zoom, 6);
        Assert.Equal(10 - TimelineLayout.FitPadding / 2 / 100, scroll, 6);
    }

    [Fact]
    public void Fitting_a_range_at_the_start_does_not_scroll_before_zero()
    {
        var (zoom, scroll) = TimelineLayout.FitRange(824, S(0), S(8), 0.02, 1200);
        Assert.Equal(100, zoom, 6);
        Assert.Equal(0, scroll);
    }

    [Fact]
    public void Fitting_a_range_too_short_for_the_max_zoom_centers_it()
    {
        var (zoom, scroll) = TimelineLayout.FitRange(1000, S(5), S(5.1), 0.02, 1200);
        Assert.Equal(1200, zoom);
        Assert.Equal(5.05 - 1000.0 / 1200 / 2, scroll, 6);
    }
}
