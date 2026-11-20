using ManeFfmpeg.UI.Controls;

namespace ManeFfmpeg.UI.Tests;

public class MeterScaleTests
{
    [Fact]
    public void The_meters_are_as_wide_as_their_channels_need()
    {
        Assert.True(MeterScale.WidthFor(1) < MeterScale.WidthFor(2));
        Assert.True(MeterScale.WidthFor(2) < MeterScale.WidthFor(6));
        Assert.Equal(41, MeterScale.WidthFor(2));
    }

    [Fact]
    public void The_scale_rises_steadily_to_full_scale_at_the_top()
    {
        Assert.Equal(1, MeterScale.Fraction(0));
        Assert.Equal(1, MeterScale.Fraction(3));
        Assert.Equal(0, MeterScale.Fraction(double.NegativeInfinity));
        var last = -1.0;
        for (var db = -80.0; db <= 0; db += 0.5)
        {
            var f = MeterScale.Fraction(db);
            Assert.True(f >= last, $"{db} dB");
            last = f;
        }
    }

    [Fact]
    public void Channels_are_named_like_their_layout()
    {
        Assert.Equal("M", MeterScale.ChannelName(0, 1));
        Assert.Equal(["L", "R"], [MeterScale.ChannelName(0, 2), MeterScale.ChannelName(1, 2)]);
        Assert.Equal("6", MeterScale.ChannelName(5, 6));
    }
}
