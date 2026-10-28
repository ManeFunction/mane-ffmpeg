using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace ManeFfmpeg.UI.Services;

/// <summary>
/// Moves and resizes a macOS window in one native call. Avalonia moves a window at once but resizes it on
/// the next layout pass, so setting both shows a frame with the window moved but not yet resized.
/// </summary>
internal static partial class MacWindowFrame
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    /// <summary>
    /// Moves the top edge down by <paramref name="topDown"/> and grows the height by <paramref name="grow"/>,
    /// both in points. False when the window isn't a native macOS one.
    /// </summary>
    public static bool TryChange(Window window, double topDown, double grow)
    {
        if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle() is not { HandleDescriptor: "NSWindow", Handle: var handle } || handle == 0)
            return false;
        var frame = Frame(handle);
        // Cocoa puts the origin at the bottom-left corner, with y growing upwards.
        var top = frame.Y + frame.Height - topDown;
        frame.Height += grow;
        frame.Y = top - frame.Height;
        SetFrame(handle, Selector("setFrame:display:"), frame, true);
        return true;
    }

    private static CGRect Frame(nint window)
    {
        var selector = Selector("frame");
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return GetFrame(window, selector);
        GetFrameStret(out var frame, window, selector);
        return frame;
    }

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    // A struct result comes back through objc_msgSend on arm64 but needs objc_msgSend_stret on x86-64.
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial CGRect GetFrame(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend_stret")]
    private static partial void GetFrameStret(out CGRect frame, nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SetFrame(nint receiver, nint selector, CGRect frame, [MarshalAs(UnmanagedType.I1)] bool display);
}
