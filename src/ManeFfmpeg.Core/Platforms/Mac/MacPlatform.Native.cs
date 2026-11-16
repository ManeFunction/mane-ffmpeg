using System.Runtime.InteropServices;
using System.Text;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Platforms.Mac;

/// <summary>
/// The parts that call macOS frameworks: power assertions (IOKit), the .mfvp handler (Launch Services) and window frames
/// (the Objective-C runtime).
/// </summary>
public sealed partial class MacPlatform
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const uint CFStringEncodingUtf8 = 0x08000100;

    // Power: an IOKit assertion, which the system drops if the app dies. It shows in `pmset -g assertions`.

    private const uint AssertionLevelOn = 255;

    public override IDisposable KeepAwake(string reason)
    {
        try
        {
            var type = CFStringCreateWithCString(0, "PreventUserIdleSystemSleep", CFStringEncodingUtf8);
            var name = CFStringCreateWithCString(0, reason, CFStringEncodingUtf8);
            try
            {
                var result = IOPMAssertionCreateWithName(type, AssertionLevelOn, name, out var id);
                if (result == 0)
                    return new PowerAssertion(id);
                AppLog.Write($"Can't keep the computer awake: IOPMAssertionCreateWithName returned 0x{result:x8}");
            }
            finally
            {
                CFRelease(name);
                CFRelease(type);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Can't keep the computer awake: {ex.Message}");
        }
        return NoOp.Instance;
    }

    private sealed class PowerAssertion(uint id) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
                return;
            _released = true;
            IOPMAssertionRelease(id);
        }
    }

    // .mfvp files: the app bundle declares the type (scripts/package-macos.sh); this sets Launch Services' default handler.

    private const uint RolesAll = 0xFFFFFFFF;
    private const string ContentType = "com.manefunction.mfvp";

    /// <summary>Opens .mfvp files after unregistering when nothing else opens JSON.</summary>
    private const string FallbackHandler = "com.apple.TextEdit";

    private static readonly Lazy<string?> BundleId = new(FindBundleId);

    public override bool CanRegisterProjectFiles => BundleId.Value is not null;

    public override Task<bool> IsProjectFileHandlerAsync() =>
        Task.FromResult(CanRegisterProjectFiles
            && string.Equals(DefaultHandler(ContentType), BundleId.Value, StringComparison.OrdinalIgnoreCase));

    public override Task RegisterProjectFilesAsync()
    {
        // Copied apps aren't known to Launch Services until Finder sees them, and their type declaration with them.
        RegisterBundle(BundleAround(Environment.ProcessPath)!);
        SetDefaultHandler(ContentType, BundleId.Value!);
        return Task.CompletedTask;
    }

    public override Task UnregisterProjectFilesAsync()
    {
        // The bundle's claim stays in its Info.plist, so an explicit default is the only way to override it.
        var json = DefaultHandler("public.json");
        SetDefaultHandler(ContentType,
            json is null || string.Equals(json, BundleId.Value, StringComparison.OrdinalIgnoreCase) ? FallbackHandler : json);
        return Task.CompletedTask;
    }

    private static string? FindBundleId()
    {
        try
        {
            return BundleAround(Environment.ProcessPath) is not null ? ToManaged(CFBundleGetIdentifier(CFBundleGetMainBundle())) : null;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Couldn't read the bundle identifier: {ex.Message}");
            return null;
        }
    }

    private static string? DefaultHandler(string contentType)
    {
        var type = CFStringCreateWithCString(0, contentType, CFStringEncodingUtf8);
        try
        {
            var handler = LSCopyDefaultRoleHandlerForContentType(type, RolesAll);
            try
            {
                return ToManaged(handler);
            }
            finally
            {
                if (handler != 0)
                    CFRelease(handler);
            }
        }
        finally
        {
            CFRelease(type);
        }
    }

    private static void SetDefaultHandler(string contentType, string bundleId)
    {
        var type = CFStringCreateWithCString(0, contentType, CFStringEncodingUtf8);
        var handler = CFStringCreateWithCString(0, bundleId, CFStringEncodingUtf8);
        try
        {
            var status = LSSetDefaultRoleHandlerForContentType(type, RolesAll, handler);
            if (status != 0)
                throw new InvalidOperationException($"Launch Services refused to set {bundleId} as the default (error {status}).");
        }
        finally
        {
            CFRelease(handler);
            CFRelease(type);
        }
    }

    private static void RegisterBundle(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var url = CFURLCreateFromFileSystemRepresentation(0, bytes, bytes.Length, true);
        try
        {
            var status = LSRegisterURL(url, true);
            if (status != 0)
                throw new InvalidOperationException($"Launch Services refused to register {path} (error {status}).");
        }
        finally
        {
            CFRelease(url);
        }
    }

    private static string? ToManaged(nint cfString)
    {
        if (cfString == 0)
            return null;
        var buffer = new byte[1024];
        return CFStringGetCString(cfString, buffer, buffer.Length, CFStringEncodingUtf8)
            ? Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0))
            : null;
    }

    // Window frames: Avalonia moves a window at once but resizes it on the next layout pass, so setting both shows a
    // frame with the window moved but not yet resized. setFrame:display: does both at once.

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    public override bool TryChangeWindowFrame(nint handle, string? handleDescriptor, double topDown, double grow)
    {
        if (handleDescriptor != "NSWindow" || handle == 0)
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

    [LibraryImport(IOKit)]
    private static partial int IOPMAssertionCreateWithName(nint type, uint level, nint name, out uint id);

    [LibraryImport(IOKit)]
    private static partial int IOPMAssertionRelease(uint id);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string text, uint encoding);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(nint text, [Out] byte[] buffer, nint size, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFURLCreateFromFileSystemRepresentation(nint allocator, byte[] path, nint length,
        [MarshalAs(UnmanagedType.U1)] bool isDirectory);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFBundleGetMainBundle();

    [LibraryImport(CoreFoundation)]
    private static partial nint CFBundleGetIdentifier(nint bundle);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);

    // Deprecated since macOS 12 in favor of NSWorkspace's block-based API, but still working.
    [LibraryImport(CoreServices)]
    private static partial nint LSCopyDefaultRoleHandlerForContentType(nint contentType, uint roles);

    [LibraryImport(CoreServices)]
    private static partial int LSSetDefaultRoleHandlerForContentType(nint contentType, uint roles, nint bundleId);

    [LibraryImport(CoreServices)]
    private static partial int LSRegisterURL(nint url, [MarshalAs(UnmanagedType.U1)] bool update);

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
