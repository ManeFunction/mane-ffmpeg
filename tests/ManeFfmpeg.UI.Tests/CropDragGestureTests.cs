using Avalonia;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.UI.ViewModels;

namespace ManeFfmpeg.UI.Tests;

/// <summary>Moving, resizing and drawing the crop rectangle, in screen points of an 800×450 frame.</summary>
public class CropDragGestureTests
{
    private static readonly Rect Frame = new(0, 0, 800, 450);

    // The middle quarter of the frame: 200,112.5 to 600,337.5.
    private static readonly Rect Middle = new(200, 112.5, 400, 225);

    private static CropRect? Drag(Point from, Point to, Rect? shown = null, double aspectLock = 0, bool shift = false)
    {
        var drag = new CropDragGesture();
        drag.Press(from, Frame, shown ?? Middle, aspectLock);
        for (var i = 1; i <= 4; i++)
            drag.Move(from + (to - from) * (i / 4.0), shift);
        return drag.Release(to);
    }

    private static void Near(CropRect expected, CropRect? actual)
    {
        Assert.NotNull(actual);
        Assert.True(Math.Abs(expected.X - actual.X) < 1e-6 && Math.Abs(expected.Y - actual.Y) < 1e-6
            && Math.Abs(expected.Width - actual.Width) < 1e-6 && Math.Abs(expected.Height - actual.Height) < 1e-6,
            $"expected {expected} but got {actual}");
    }

    [Fact]
    public void Dragging_inside_moves_it_and_keeps_it_in_the_frame()
    {
        Near(new CropRect(0.375, 0.25, 0.5, 0.5), Drag(new Point(400, 225), new Point(500, 225)));
        // Pushed past the right edge it stops there.
        Near(new CropRect(0.5, 0.25, 0.5, 0.5), Drag(new Point(400, 225), new Point(900, 225)));
    }

    [Fact]
    public void Dragging_a_corner_or_an_edge_resizes_it()
    {
        // The bottom-right corner to 700,400: the top-left corner stays.
        Near(new CropRect(0.25, 0.25, 0.625, 0.25 + 400 / 450.0 - 0.5), Drag(new Point(600, 337.5), new Point(700, 400)));
        // The left edge, grabbed just inside it, to x = 100: only the width changes.
        Near(new CropRect(0.125, 0.25, 0.625, 0.5), Drag(new Point(205, 225), new Point(100, 225)));
    }

    [Fact]
    public void A_locked_shape_holds_while_resizing_and_still_fits_the_frame()
    {
        // 1:1 on screen; the corner drag follows the side that moved most (500 px wide), shrunk to the 337.5 px of
        // room below the top edge.
        var square = Drag(new Point(600, 337.5), new Point(700, 350), aspectLock: 1)!;
        Assert.Equal(1, square.Width * Frame.Width / (square.Height * Frame.Height), 6);
        Assert.Equal(337.5 / 800, square.Width, 6);
        Assert.Equal(1, square.Bottom, 6);
        // An edge dragged towards the frame's edge grows the other side around the centre, until it hits the frame.
        var tall = Drag(new Point(600, 225), new Point(800, 225), aspectLock: 1)!;
        Assert.Equal(1, tall.Width * Frame.Width / (tall.Height * Frame.Height), 6);
        Assert.True(tall.Y >= 0 && tall.Bottom <= 1 + 1e-9, tall.ToString());
        Assert.Equal(1, tall.Height, 6);
        // Shift keeps the shape it had when pressed.
        var shifted = Drag(new Point(600, 337.5), new Point(700, 337.5), shift: true)!;
        Assert.Equal(Middle.Width / Middle.Height, shifted.Width * Frame.Width / (shifted.Height * Frame.Height), 6);
    }

    [Fact]
    public void It_never_gets_smaller_than_the_minimum()
    {
        var tiny = Drag(new Point(600, 337.5), new Point(150, 50))!;
        // Past the opposite corner it flips, then stops at the minimum size.
        var crushed = Drag(new Point(600, 337.5), new Point(200.5, 112.6))!;
        Assert.True(tiny.Width >= CropRect.MinSize - 1e-9 && tiny.Height >= CropRect.MinSize - 1e-9, tiny.ToString());
        Assert.Equal(CropRect.MinSize, crushed.Width, 6);
        Assert.Equal(CropRect.MinSize, crushed.Height, 6);
    }

    [Fact]
    public void Dragging_outside_draws_a_new_one()
    {
        Near(new CropRect(0.0125, 0.02, 0.1125, 0.2), Drag(new Point(10, 9), new Point(100, 99)));
        // Drawn with a lock, it has that shape from the start.
        var wide = Drag(new Point(10, 9), new Point(100, 99), aspectLock: 16.0 / 9)!;
        Assert.Equal(16.0 / 9, wide.Width * Frame.Width / (wide.Height * Frame.Height), 6);
    }

    [Fact]
    public void A_click_or_a_lost_capture_changes_nothing()
    {
        var drag = new CropDragGesture();
        drag.Press(new Point(20, 20), Frame, Middle, 0);
        Assert.True(drag.IsDragging);
        Assert.Null(drag.Release(new Point(20.5, 20)));
        Assert.False(drag.IsDragging);
        Assert.Null(drag.Live);

        drag.Press(new Point(400, 225), Frame, Middle, 0);
        drag.Move(new Point(500, 300), false);
        Assert.NotNull(drag.Live);
        drag.Cancel();
        Assert.Null(drag.Live);
        Assert.Null(drag.Release(new Point(500, 300)));
    }

    [Fact]
    public void Edges_are_grabbed_from_either_side_and_small_rectangles_can_still_be_moved()
    {
        Assert.Equal(CropEdges.Left | CropEdges.Top, CropDragGesture.HitEdges(Middle, new Point(195, 110)));
        Assert.Equal(CropEdges.Right, CropDragGesture.HitEdges(Middle, new Point(606, 225)));
        Assert.Equal(CropEdges.Bottom, CropDragGesture.HitEdges(Middle, new Point(400, 332)));
        Assert.Equal(CropEdges.None, CropDragGesture.HitEdges(Middle, new Point(400, 225)));
        Assert.Equal(CropEdges.None, CropDragGesture.HitEdges(Middle, new Point(100, 225)));
        // A 20 px rectangle grabs only 5 px inside each edge, leaving its middle to move it.
        Assert.Equal(CropEdges.None, CropDragGesture.HitEdges(new Rect(100, 100, 20, 20), new Point(110, 110)));
    }
}
