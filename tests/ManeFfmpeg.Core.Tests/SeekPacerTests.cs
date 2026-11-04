using ManeFfmpeg.Core.Mpv;

namespace ManeFfmpeg.Core.Tests;

/// <summary>The preview's seek pacing without mpv: a fake mpv, timer and clock.</summary>
public class SeekPacerTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private sealed class Fake
    {
        public List<TimeSpan> Sent { get; } = [];
        public bool MpvSeeking { get; set; }
        public bool Armed { get; private set; }
        public DateTime Now { get; set; } = new(2026, 1, 1);
        public SeekPacer Pacer { get; }

        public Fake() => Pacer = new SeekPacer(Sent.Add, () => MpvSeeking, armed => Armed = armed, () => Now);

        /// <summary>The armed check fires (as the timer would).</summary>
        public void Tick()
        {
            Assert.True(Armed, "no check was armed");
            Armed = false;
            Pacer.Check();
        }
    }

    [Fact]
    public void Seeks_during_a_seek_collapse_into_the_latest_which_goes_when_it_finishes()
    {
        var f = new Fake();
        f.Pacer.Seek(S(1));
        f.Pacer.Seek(S(2));
        f.Pacer.Seek(S(3));
        Assert.Equal([S(1)], f.Sent);
        f.Pacer.Restarted();
        Assert.Equal([S(1), S(3)], f.Sent);
        f.Pacer.Restarted();
        Assert.False(f.Armed);
        f.Pacer.Seek(S(4));
        Assert.Equal([S(1), S(3), S(4)], f.Sent);
    }

    [Fact]
    public void Coming_back_to_the_time_under_way_costs_no_second_seek()
    {
        var f = new Fake();
        f.Pacer.Seek(S(1));
        f.Pacer.Seek(S(2));
        f.Pacer.Seek(S(1));
        f.Pacer.Restarted();
        Assert.Equal([S(1)], f.Sent);
    }

    [Fact]
    public void A_slow_seek_keeps_the_next_waiting_and_a_lost_one_lets_it_go()
    {
        var f = new Fake();
        f.Pacer.Seek(S(1));
        f.Pacer.Seek(S(2));
        f.MpvSeeking = true;
        f.Tick();
        f.Tick();
        Assert.Equal([S(1)], f.Sent);
        f.MpvSeeking = false;
        f.Tick();
        Assert.Equal([S(1), S(2)], f.Sent);
    }

    [Fact]
    public void A_reset_drops_the_seeks_and_the_next_goes_at_once()
    {
        var f = new Fake();
        f.Pacer.Seek(S(1));
        f.Pacer.Seek(S(2));
        f.Pacer.Reset();
        f.Pacer.Restarted();
        Assert.Equal([S(1)], f.Sent);
        f.Pacer.Seek(S(5));
        Assert.Equal([S(1), S(5)], f.Sent);
    }

    [Fact]
    public void Positions_echo_while_a_seek_waits_or_was_just_sent()
    {
        var f = new Fake();
        Assert.False(f.Pacer.MayEcho);
        f.Pacer.Seek(S(1));
        Assert.True(f.Pacer.MayEcho);
        f.Pacer.Seek(S(2));
        f.Now += SeekPacer.EchoWindow * 2;
        Assert.True(f.Pacer.MayEcho);
        f.Pacer.Restarted();
        Assert.True(f.Pacer.MayEcho);
        f.Now += SeekPacer.EchoWindow * 2;
        Assert.False(f.Pacer.MayEcho);
        f.Pacer.Restarted();
        f.Pacer.Seek(S(3));
        Assert.True(f.Pacer.MayEcho);
        f.Pacer.ForgetLastSeek();
        Assert.False(f.Pacer.MayEcho);
    }

    [Fact]
    public void Nothing_is_sent_or_armed_once_disposed()
    {
        var f = new Fake();
        f.Pacer.Seek(S(1));
        f.Pacer.Seek(S(2));
        f.Pacer.Dispose();
        f.Pacer.Restarted();
        f.Pacer.Check();
        Assert.Equal([S(1)], f.Sent);
    }
}
