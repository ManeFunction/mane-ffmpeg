using ManeFfmpeg.Core.Mpv;

namespace ManeFfmpeg.Core.Tests;

/// <summary>When the preview's mix graph is built, realigned and seeked back, without mpv.</summary>
public class MixGraphTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private sealed class Fake
    {
        public readonly List<string> Graphs = [];
        public readonly List<TimeSpan> Seeks = [];
        public TimeSpan? Position = TimeSpan.Zero;
        public readonly MixGraph Mix;

        public Fake(TimelineMix? mix)
        {
            Mix = new MixGraph(Graphs.Add, Seeks.Add, () => Position) { Mix = mix };
        }
    }

    private static readonly TimelineMix WithA2 = new([new GainStep(TimeSpan.Zero, 1)], [new GainStep(TimeSpan.Zero, 0.5)]);
    private static readonly TimelineMix A1Only = new([new GainStep(TimeSpan.Zero, 1), new GainStep(S(2), 0.5)], null);

    [Fact]
    public void A_loaded_timeline_is_mixed_from_where_it_starts()
    {
        var f = new Fake(WithA2);
        f.Mix.OnLoad(S(3));
        Assert.True(f.Mix.OnTracks(("1", "3")));
        Assert.Equal([WithA2.Graph("1", "3", S(3))], f.Graphs);
        Assert.Equal([S(3)], f.Seeks);
        // The same tracks again (a compact toggle) change nothing.
        Assert.True(f.Mix.OnTracks(("1", "3")));
        Assert.Single(f.Graphs);
    }

    [Fact]
    public void Paused_seeks_realign_once_when_play_is_pressed()
    {
        var f = new Fake(WithA2);
        f.Mix.OnLoad(S(0));
        f.Mix.OnTracks(("1", "3"));
        f.Graphs.Clear();
        f.Seeks.Clear();

        f.Mix.OnSeek(S(4), paused: true);
        f.Mix.OnSeek(S(5), paused: true);
        Assert.Empty(f.Graphs);
        Assert.Equal(S(5), f.Mix.OnPlay());
        Assert.Equal([WithA2.Graph("1", "3", S(5))], f.Graphs);
        Assert.Equal([S(5)], f.Seeks);
        // Aligned now: play again starts at once.
        Assert.Null(f.Mix.OnPlay());

        // Playing, a seek realigns at once; the caller's own seek goes there, so no seek back.
        f.Mix.OnSeek(S(8), paused: false);
        Assert.Equal(WithA2.Graph("1", "3", S(8)), f.Graphs[^1]);
        Assert.Single(f.Seeks);
    }

    [Fact]
    public void A_frame_step_realigns_where_mpv_landed()
    {
        var f = new Fake(WithA2);
        f.Mix.OnLoad(S(0));
        f.Mix.OnTracks(("1", "3"));
        f.Mix.OnFrameStep();
        f.Position = S(1.04);
        Assert.Equal(S(1.04), f.Mix.OnPlay());
        Assert.Equal(WithA2.Graph("1", "3", S(1.04)), f.Graphs[^1]);
    }

    [Fact]
    public void A_mix_without_A2_needs_no_alignment()
    {
        var f = new Fake(A1Only);
        f.Mix.OnLoad(S(0));
        f.Mix.OnTracks(("1", null));
        f.Mix.OnSeek(S(4), paused: true);
        Assert.Null(f.Mix.OnPlay());
        Assert.Equal([A1Only.Graph("1", null)], f.Graphs);
    }

    [Fact]
    public void No_mix_takes_the_graph_down_and_keeps_the_position()
    {
        var f = new Fake(WithA2);
        f.Mix.OnLoad(S(0));
        f.Mix.OnTracks(("1", "3"));
        f.Mix.Mix = null;
        f.Position = S(6);
        Assert.False(f.Mix.OnTracks(("1", "3")));
        Assert.Equal("", f.Graphs[^1]);
        Assert.Equal(S(6), f.Seeks[^1]);
        Assert.Equal("", f.Mix.Graph);

        // A new file drops the old graph without a seek, which would hold up the new file's first one.
        f.Mix.Mix = WithA2;
        f.Mix.OnTracks(("1", "3"));
        var seeks = f.Seeks.Count;
        f.Mix.OnLoad(S(2));
        Assert.Equal(("", seeks), (f.Graphs[^1], f.Seeks.Count));
    }
}
