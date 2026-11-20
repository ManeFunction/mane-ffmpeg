using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Tests;

/// <summary>The timeline's selection: V1 clips, A2 clips or a whole track, never a mix.</summary>
public class SelectionTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static readonly ProjectState State = new([new SourceMedia { Id = "v", Path = "/m/v.mp4" }],
        new Timeline([.. new[] { "c1", "c2", "c3", "c4" }.Select(id => Clip.Create("v", S(0), S(2)) with { Id = id })]),
        [new SourceMedia { Id = "f", Path = "/m/song.mp3" }])
    {
        AudioTrack =
        [
            new TrackClip { Id = "t1", FileId = "f", Start = S(0), In = S(0), Out = S(1) },
            new TrackClip { Id = "t2", FileId = "f", Start = S(3), In = S(0), Out = S(1) },
        ],
    };

    private static TimelineSelection Click(TimelineSelection selection, string id, bool toggle = false, bool range = false) =>
        selection.Click(State, id, toggle, range);

    [Fact]
    public void Clicks_select_toggle_and_range_V1_clips()
    {
        var one = Click(TimelineSelection.None, "c2");
        Assert.Equal(SelectionKind.TimelineClips, one.Kind);
        Assert.Equal(["c2"], one.Ids);
        Assert.Same(one, Click(one, "c2"));

        var range = Click(one, "c4", range: true);
        Assert.True(range.Ids.SetEquals(["c2", "c3", "c4"]));
        var toggled = Click(range, "c3", toggle: true);
        Assert.True(toggled.Ids.SetEquals(["c2", "c4"]));
        Assert.Equal(["c1"], Click(toggled, "c1").Ids);
        Assert.Equal(TimelineSelection.None, Click(one, "c2", toggle: true));
    }

    [Fact]
    public void V1_and_A2_clips_are_never_selected_together()
    {
        var v1 = Click(Click(TimelineSelection.None, "c1"), "c2", toggle: true);
        var a2 = Click(v1, "t1", toggle: true);
        Assert.Equal(SelectionKind.TrackClips, a2.Kind);
        Assert.Equal(["t1"], a2.Ids);
        // A2 clips aren't in a row: a range click selects just the clip.
        Assert.True(Click(a2, "t2", toggle: true).Ids.SetEquals(["t1", "t2"]));
        Assert.Equal(["t2"], Click(a2, "t2", range: true).Ids);
        Assert.Equal(SelectionKind.TimelineClips, Click(a2, "c3", range: true).Kind);

        Assert.Equal(SelectionKind.TrackClips, TimelineSelection.OfClips(State, ["t2"]).Kind);
        Assert.Equal(SelectionKind.TimelineClips, TimelineSelection.OfClips(State, ["c1", "t2"]).Kind);
        Assert.Equal(["c1"], TimelineSelection.OfClips(State, ["c1", "t2"]).Ids);
    }

    [Fact]
    public void A_whole_track_is_selected_alone()
    {
        var track = TimelineSelection.OfTrack(MixTrack.A2);
        Assert.Equal((SelectionKind.WholeTrack, MixTrack.A2), (track.Kind, track.Track));
        Assert.Empty(track.Ids);
        var clip = Click(track, "c1", range: true);
        Assert.Equal((SelectionKind.TimelineClips, (MixTrack?)null), (clip.Kind, clip.Track));
        Assert.Equal(4, TimelineSelection.AllClips(State).Ids.Count);
    }

    [Fact]
    public void Edits_of_the_selection_go_to_the_track_it_is_on()
    {
        var v1 = TimelineSelection.OfClips(State, ["c1"]);
        var a2 = TimelineSelection.OfClips(State, ["t2"]);

        var louder = ProjectEditor.SetVolume(State, v1, 1.5);
        Assert.Equal([1.5, 1, 1, 1], louder.Timeline.Clips.Select(c => c.Volume));
        Assert.All(louder.AudioTrack, c => Assert.Equal(1, c.Volume));
        Assert.Equal([1, 0.5], ProjectEditor.SetVolume(State, a2, 0.5).AudioTrack.Select(c => c.Volume));
        Assert.Equal(0.25, ProjectEditor.SetVolume(State, TimelineSelection.OfTrack(MixTrack.A2), 0.25).A2Volume);
        Assert.Same(State, ProjectEditor.SetVolume(State, TimelineSelection.None, 2));

        // Deleting a V1 clip before the playhead pulls it earlier; deleting an A2 clip moves nothing.
        var (noFirst, playhead) = ProjectEditor.DeleteSelection(State, v1, S(5));
        Assert.Equal(["c2", "c3", "c4"], noFirst.Timeline.Clips.Select(c => c.Id));
        Assert.Equal(S(3), playhead);
        var (noT2, still) = ProjectEditor.DeleteSelection(State, a2, S(5));
        Assert.Equal(["t1"], noT2.AudioTrack.Select(c => c.Id));
        Assert.Equal((4, S(5)), (noT2.Timeline.Clips.Count, still));

        // Splitting A2 needs a selected A2 clip under the playhead; V1 splits the clip under it and selects its left half.
        Assert.Null(ProjectEditor.Split(State, a2, S(1), _ => null));
        Assert.Equal(3, ProjectEditor.Split(State, a2, S(3.5), _ => null)!.Value.State.AudioTrack.Count);
        var (splitV1, left) = ProjectEditor.Split(State, TimelineSelection.None, S(3), _ => null)!.Value;
        Assert.Equal(5, splitV1.Timeline.Clips.Count);
        Assert.Equal("c2", left);
    }

    [Fact]
    public void Clips_that_are_gone_drop_out()
    {
        var both = TimelineSelection.OfClips(State, ["c1", "c2"]);
        var after = State with { Timeline = new Timeline([State.Timeline.Clips[1]]) };
        Assert.Equal(["c2"], both.Pruned(after).Ids);
        Assert.Equal(TimelineSelection.None, both.Pruned(ProjectState.Empty));
        var track = TimelineSelection.OfTrack(MixTrack.A1);
        Assert.Same(track, track.Pruned(ProjectState.Empty));
    }
}
