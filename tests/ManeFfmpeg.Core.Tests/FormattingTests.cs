using System.Globalization;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Tests;

public class TimeFormatTests
{
    [Theory]
    [InlineData(12.3456789, "12.345678")]
    [InlineData(0, "0.000000")]
    [InlineData(3600.5, "3600.500000")]
    [InlineData(-0.12, "-0.120000")]
    public void Seconds_floors_to_microseconds_with_invariant_culture(double seconds, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            Assert.Equal(expected, TimeFormat.Seconds(TimeSpan.FromTicks((long)Math.Round(seconds * 10_000_000))));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("1:02:03.5", 3723.5)]
    [InlineData("02:03,25", 123.25)]
    [InlineData("42", 42)]
    [InlineData("-0.12", -0.12)]
    public void TryParse_accepts_common_forms(string text, double seconds)
    {
        Assert.True(TimeFormat.TryParse(text, out var time));
        Assert.Equal(seconds, time.TotalSeconds, 6);
    }

    [Fact]
    public void SnapToFrame_lands_on_frame_starts_that_ffmpeg_keeps()
    {
        var ntsc = new Rational(30000, 1001);
        var snapped = TimeFormat.SnapToFrame(TimeSpan.FromSeconds(10.02), ntsc);
        var exact = 300 * 1001 / 30000.0;
        Assert.True(snapped.TotalSeconds >= exact);
        Assert.True(double.Parse(TimeFormat.Seconds(snapped), CultureInfo.InvariantCulture) <= exact + 1e-6);
    }
}

public class ArithmeticTests
{
    [Theory]
    [InlineData("0-700", -700)]
    [InlineData(" -700 ", -700)]
    [InlineData("−120", -120)]
    [InlineData("100 + 20.5", 120.5)]
    [InlineData("1,5*2", 3)]
    [InlineData("2*(40+1)", 82)]
    [InlineData("1000/3*3", 1000)]
    [InlineData("10-2-3", 5)]
    [InlineData("-(5-8)", 3)]
    [InlineData("--4", 4)]
    [InlineData("3 × 4 ÷ 2", 6)]
    public void Evaluates_simple_arithmetic(string text, double expected)
    {
        Assert.True(Arithmetic.TryEvaluate(text, out var value));
        Assert.Equal(expected, value, 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0-")]
    [InlineData("abc")]
    [InlineData("(1+2")]
    [InlineData("1+2)")]
    [InlineData("1..2")]
    [InlineData("5/0")]
    [InlineData("12 ms")]
    public void Refuses_what_is_not_a_finite_number(string text) =>
        Assert.False(Arithmetic.TryEvaluate(text, out _));
}

public class ProgressParserTests
{
    [Fact]
    public void Emits_snapshot_per_progress_line()
    {
        var parser = new ProgressParser();
        var seen = new List<FfmpegProgress>();
        parser.Updated += seen.Add;
        foreach (var line in "frame=10\nout_time_us=1500000\nspeed=2.5x\nprogress=continue\nout_time_us=N/A\nspeed=N/A\nprogress=end".Split('\n'))
            parser.Feed(line);
        Assert.Equal(2, seen.Count);
        Assert.Equal(TimeSpan.FromSeconds(1.5), seen[0].Position);
        Assert.Equal(2.5, seen[0].Speed);
        Assert.True(seen[1].Finished);
        Assert.Null(seen[1].Speed);
    }
}
