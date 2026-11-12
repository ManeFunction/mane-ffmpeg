using Avalonia;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.UI.Controls;

namespace ManeFfmpeg.UI.Tests;

/// <summary>Where the preview draws the picture and the crop, shared by the video view and the crop overlay.</summary>
public class PreviewGeometryTests
{
    private const double Wide = 16.0 / 9;

    private static void Near(Rect expected, Rect actual)
    {
        Assert.True(Math.Abs(expected.X - actual.X) < 1e-6 && Math.Abs(expected.Y - actual.Y) < 1e-6
            && Math.Abs(expected.Width - actual.Width) < 1e-6 && Math.Abs(expected.Height - actual.Height) < 1e-6,
            $"expected {expected} but got {actual}");
    }

    [Fact]
    public void Fits_and_centers()
    {
        Near(new Rect(0, 50, 800, 450), PreviewGeometry.Fit(Wide, new Rect(0, 0, 800, 550)));
        Near(new Rect(298.75, 0, 202.5, 360), PreviewGeometry.Fit(9 / 16.0, new Rect(0, 0, 800, 360)));
        Near(new Rect(0, 0, 800, 550), PreviewGeometry.Fit(0, new Rect(0, 0, 800, 550)));
    }

    [Fact]
    public void The_frame_of_a_clip_with_another_shape_pads_it_like_an_export()
    {
        // A square clip in a 16:9 output is pillarboxed: the frame is as tall as the picture and wider.
        var picture = new Rect(100, 0, 360, 360);
        Near(new Rect(280 - 320, 0, 640, 360), PreviewGeometry.FrameAround(picture, 1, Wide));
        // A clip already of the frame's shape is the frame.
        Near(picture, PreviewGeometry.FrameAround(picture, 1, 1));
    }

    [Fact]
    public void Shows_the_whole_frame_as_mpv_rendered_it()
    {
        var (source, destination) = PreviewGeometry.Placement(new Size(800, 450), new Rect(0, 0, 800, 450), Wide, Wide, CropRect.Full);
        Near(new Rect(0, 0, 800, 450), source);
        Near(new Rect(0, 0, 800, 450), destination);
    }

    [Fact]
    public void A_crop_is_cut_from_the_picture_and_fills_the_view()
    {
        // mpv drew a 16:9 picture letterboxed into a 1600×1100 buffer; the left half, shown in an 800×450 view.
        var (source, destination) = PreviewGeometry.Placement(new Size(1600, 1100), new Rect(0, 0, 800, 450), Wide, Wide,
            new CropRect(0, 0, 0.5, 1));
        Near(new Rect(0, 100, 800, 900), source);
        Near(new Rect(200, 0, 400, 450), destination);
    }

    [Fact]
    public void Padding_outside_the_rendered_picture_stays_black()
    {
        // A square clip in a 16:9 frame, rendered into a square buffer: the frame's sides lie outside it, so only
        // the picture is drawn, where it sits in the frame.
        var (source, destination) = PreviewGeometry.Placement(new Size(400, 400), new Rect(0, 0, 640, 360), 1, Wide, CropRect.Full);
        Near(new Rect(0, 0, 400, 400), source);
        Near(new Rect(140, 0, 360, 360), destination);
    }

    [Fact]
    public void Renders_bigger_for_a_small_crop_and_never_smaller()
    {
        var bounds = new Rect(0, 0, 800, 450);
        Assert.Equal(1, PreviewGeometry.RenderScale(bounds, Wide, Wide, null));
        Assert.Equal(1, PreviewGeometry.RenderScale(bounds, Wide, Wide, CropRect.Full), 6);
        // A quarter of the frame, of the same shape, is shown 4× wider than it is in the full picture.
        Assert.Equal(4, PreviewGeometry.RenderScale(bounds, Wide, Wide, new CropRect(0.375, 0.375, 0.25, 0.25)), 6);
    }

    [Fact]
    public void Converts_between_crops_and_screen_rectangles()
    {
        var frame = new Rect(10, 20, 800, 450);
        var crop = new CropRect(0.25, 0.1, 0.5, 0.6);
        Near(new Rect(210, 65, 400, 270), PreviewGeometry.Inside(frame, crop));
        var back = PreviewGeometry.ToCrop(frame, PreviewGeometry.Inside(frame, crop));
        Assert.Equal((crop.X, crop.Y, crop.Width, crop.Height), (Math.Round(back.X, 9), Math.Round(back.Y, 9), Math.Round(back.Width, 9), Math.Round(back.Height, 9)));
        Assert.Equal(16.0 / 9 * 0.5 / 0.6, crop.AspectIn(Wide), 9);
    }
}
