using Avalonia;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.UI.Controls;

namespace ManeFfmpeg.UI.ViewModels;

/// <summary>Which edges of the crop rectangle a point grabs.</summary>
[Flags]
public enum CropEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
}

/// <summary>
/// Dragging the crop rectangle on the preview: moving it (drag inside), resizing it (drag an edge or corner) or
/// drawing a new one (drag outside it), kept inside the frame and at least <see cref="CropRect.MinSize"/>. Works in
/// screen coordinates of the overlay; the overlay only forwards pointer events and draws <see cref="Live"/>.
/// </summary>
public sealed class CropDragGesture
{
    /// <summary>How far outside or inside an edge, in pixels, still grabs it.</summary>
    public const double Grab = 9;

    private enum Kind
    {
        None,
        Move,
        Resize,
    }

    private Kind _kind;
    private CropEdges _edges;
    private Rect _frame;
    private Point _pressPoint;
    private Rect _pressRect;
    private double _pressAspect;
    private double _aspectLock;

    /// <summary>A drag is on.</summary>
    public bool IsDragging => _kind != Kind.None;

    /// <summary>The rectangle while it is dragged; null when no drag is on.</summary>
    public Rect? Live { get; private set; }

    /// <summary>
    /// The button went down at <paramref name="point"/>, with the output frame at <paramref name="frame"/> and the crop
    /// shown at <paramref name="shown"/>. <paramref name="aspectLock"/> is the width over height to keep, or 0.
    /// </summary>
    public void Press(Point point, Rect frame, Rect shown, double aspectLock)
    {
        _frame = frame;
        _aspectLock = aspectLock;
        _pressPoint = point;
        _pressRect = shown;
        _pressAspect = shown.Height > 0 ? shown.Width / shown.Height : 1;
        _edges = HitEdges(shown, point);
        if (_edges != CropEdges.None)
            _kind = Kind.Resize;
        else if (shown.Contains(point))
            _kind = Kind.Move;
        else
        {
            // Outside the rectangle: draw a new one from here, as if dragging the corner of an empty one.
            var start = Clamp(point, frame);
            _pressRect = new Rect(start, start);
            _edges = CropEdges.Right | CropEdges.Bottom;
            _kind = Kind.Resize;
            _pressAspect = aspectLock > 0 ? aspectLock : _pressAspect;
        }
        Live = shown;
    }

    /// <summary>The pointer moved; <paramref name="keepShape"/> (Shift) holds the shape the rectangle had.</summary>
    public void Move(Point point, bool keepShape)
    {
        if (_kind == Kind.None)
            return;
        var aspect = _aspectLock > 0 ? _aspectLock : keepShape ? _pressAspect : 0;
        Live = _kind == Kind.Move ? Moved(point) : Resized(point, aspect);
    }

    /// <summary>
    /// The button went up: the crop made, as fractions of the frame, or null when nothing was dragged (a click doesn't
    /// collapse the crop to a dot).
    /// </summary>
    public CropRect? Release(Point point)
    {
        var (live, dragging) = (Live, IsDragging);
        Cancel();
        var moved = Math.Abs(point.X - _pressPoint.X) + Math.Abs(point.Y - _pressPoint.Y) > 1;
        return dragging && moved && live is { } rect ? PreviewGeometry.ToCrop(_frame, rect) : null;
    }

    /// <summary>The pointer capture was lost: the drag ends without a crop.</summary>
    public void Cancel()
    {
        _kind = Kind.None;
        Live = null;
    }

    /// <summary>Which edges a point grabs: near a corner both, near an edge one, else none.</summary>
    public static CropEdges HitEdges(Rect rect, Point p)
    {
        var inX = p.X >= rect.X - Grab && p.X <= rect.Right + Grab;
        var inY = p.Y >= rect.Y - Grab && p.Y <= rect.Bottom + Grab;
        if (!inX || !inY)
            return CropEdges.None;
        // A small rectangle still leaves room in the middle to move it.
        var grabX = Math.Min(Grab, rect.Width / 4);
        var grabY = Math.Min(Grab, rect.Height / 4);
        var edges = CropEdges.None;
        if (Math.Abs(p.X - rect.X) <= grabX || (p.X < rect.X && p.X >= rect.X - Grab))
            edges |= CropEdges.Left;
        else if (Math.Abs(p.X - rect.Right) <= grabX || (p.X > rect.Right && p.X <= rect.Right + Grab))
            edges |= CropEdges.Right;
        if (Math.Abs(p.Y - rect.Y) <= grabY || (p.Y < rect.Y && p.Y >= rect.Y - Grab))
            edges |= CropEdges.Top;
        else if (Math.Abs(p.Y - rect.Bottom) <= grabY || (p.Y > rect.Bottom && p.Y <= rect.Bottom + Grab))
            edges |= CropEdges.Bottom;
        return edges;
    }

    private Rect Moved(Point point)
    {
        var x = Within(_pressRect.X + point.X - _pressPoint.X, _frame.X, _frame.Right - _pressRect.Width);
        var y = Within(_pressRect.Y + point.Y - _pressPoint.Y, _frame.Y, _frame.Bottom - _pressRect.Height);
        return new Rect(x, y, _pressRect.Width, _pressRect.Height);
    }

    /// <summary>
    /// The pressed rectangle with the dragged edges moved to <paramref name="point"/> and the others held, kept inside
    /// the frame and at least the minimum size; with an <paramref name="aspect"/>, of that shape.
    /// </summary>
    private Rect Resized(Point point, double aspect)
    {
        var frame = _frame;
        var p = Clamp(point, frame);
        var r = _pressRect;
        var minW = CropRect.MinSize * frame.Width;
        var minH = CropRect.MinSize * frame.Height;
        var horizontal = (_edges & (CropEdges.Left | CropEdges.Right)) != 0;
        var vertical = (_edges & (CropEdges.Top | CropEdges.Bottom)) != 0;

        // The fixed side on each dragged axis, and which way the dragged side lies from it.
        var anchorX = (_edges & CropEdges.Left) != 0 ? r.Right : r.X;
        var anchorY = (_edges & CropEdges.Top) != 0 ? r.Bottom : r.Y;
        var dirX = horizontal ? (p.X >= anchorX ? 1 : -1) : 0;
        var dirY = vertical ? (p.Y >= anchorY ? 1 : -1) : 0;
        var width = horizontal ? Math.Abs(p.X - anchorX) : r.Width;
        var height = vertical ? Math.Abs(p.Y - anchorY) : r.Height;

        // Room on each axis: from the anchor to the frame edge it grows towards, or around the centre it keeps.
        var roomX = dirX > 0 ? frame.Right - anchorX : dirX < 0 ? anchorX - frame.X : 2 * Math.Min(r.Center.X - frame.X, frame.Right - r.Center.X);
        var roomY = dirY > 0 ? frame.Bottom - anchorY : dirY < 0 ? anchorY - frame.Y : 2 * Math.Min(r.Center.Y - frame.Y, frame.Bottom - r.Center.Y);

        if (aspect > 0)
        {
            // Follow whichever dragged side went furthest; an edge drag sizes the other axis from it.
            if (horizontal && vertical)
            {
                if (width < height * aspect)
                    width = height * aspect;
                else
                    height = width / aspect;
            }
            else if (horizontal)
                height = width / aspect;
            else
                width = height * aspect;
            var fit = Math.Min(1, Math.Min(roomX / Math.Max(width, 1e-9), roomY / Math.Max(height, 1e-9)));
            width *= fit;
            height *= fit;
            var grow = Math.Max(1, Math.Max(minW / Math.Max(width, 1e-9), minH / Math.Max(height, 1e-9)));
            width *= grow;
            height *= grow;
        }
        width = Within(width, Math.Min(minW, roomX), Math.Max(roomX, minW));
        height = Within(height, Math.Min(minH, roomY), Math.Max(roomY, minH));

        var x = dirX > 0 ? anchorX : dirX < 0 ? anchorX - width : r.Center.X - width / 2;
        var y = dirY > 0 ? anchorY : dirY < 0 ? anchorY - height : r.Center.Y - height / 2;
        x = Within(x, frame.X, frame.Right - width);
        y = Within(y, frame.Y, frame.Bottom - height);
        return new Rect(x, y, width, height);
    }

    /// <summary>
    /// Like Math.Clamp, but a <paramref name="max"/> below <paramref name="min"/> (a rectangle as big as the frame, give
    /// or take rounding) gives <paramref name="min"/> instead of throwing.
    /// </summary>
    private static double Within(double value, double min, double max) => Math.Max(min, Math.Min(value, max));

    private static Point Clamp(Point point, Rect area) =>
        new(Math.Clamp(point.X, area.X, area.Right), Math.Clamp(point.Y, area.Y, area.Bottom));
}
