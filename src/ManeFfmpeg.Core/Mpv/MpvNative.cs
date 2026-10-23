using System.Runtime.InteropServices;

namespace ManeFfmpeg.Core.Mpv;

/// <summary>
/// The slice of libmpv's client and render API this app uses. The library is located at runtime by
/// <see cref="MpvLibrary"/>, which maps the "libmpv" import name to the file it found.
/// </summary>
internal static unsafe partial class MpvNative
{
    public const string Library = "libmpv";

    public const int FormatNone = 0;
    public const int FormatString = 1;
    public const int FormatFlag = 3;
    public const int FormatInt64 = 4;
    public const int FormatDouble = 5;

    public const int EventIdNone = 0;
    public const int EventIdShutdown = 1;
    public const int EventIdLogMessage = 2;
    public const int EventIdStartFile = 6;
    public const int EventIdEndFile = 7;
    public const int EventIdFileLoaded = 8;
    public const int EventIdPlaybackRestart = 21;
    public const int EventIdPropertyChange = 22;

    public const int RenderParamInvalid = 0;
    public const int RenderParamApiType = 1;
    public const int RenderParamSwSize = 17;
    public const int RenderParamSwFormat = 18;
    public const int RenderParamSwStride = 19;
    public const int RenderParamSwPointer = 20;
    public const ulong RenderUpdateFrame = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserdata;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventProperty
    {
        public nint Name;
        public int Format;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventEndFile
    {
        public int Reason;
        public int Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventLogMessage
    {
        public nint Prefix;
        public nint Level;
        public nint Text;
        public int LogLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenderParam
    {
        public int Type;
        public void* Data;
    }

    [LibraryImport(Library, EntryPoint = "mpv_client_api_version")]
    public static partial uint ClientApiVersion();

    [LibraryImport(Library, EntryPoint = "mpv_create")]
    public static partial nint Create();

    [LibraryImport(Library, EntryPoint = "mpv_initialize")]
    public static partial int Initialize(nint handle);

    [LibraryImport(Library, EntryPoint = "mpv_terminate_destroy")]
    public static partial void TerminateDestroy(nint handle);

    [LibraryImport(Library, EntryPoint = "mpv_error_string")]
    public static partial nint ErrorString(int error);

    [LibraryImport(Library, EntryPoint = "mpv_free")]
    public static partial void Free(nint data);

    [LibraryImport(Library, EntryPoint = "mpv_set_option_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetOptionString(nint handle, string name, string value);

    [LibraryImport(Library, EntryPoint = "mpv_set_property_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetPropertyString(nint handle, string name, string value);

    [LibraryImport(Library, EntryPoint = "mpv_set_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetProperty(nint handle, string name, int format, void* data);

    [LibraryImport(Library, EntryPoint = "mpv_get_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GetProperty(nint handle, string name, int format, void* data);

    [LibraryImport(Library, EntryPoint = "mpv_get_property_string", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetPropertyString(nint handle, string name);

    [LibraryImport(Library, EntryPoint = "mpv_observe_property", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int ObserveProperty(nint handle, ulong replyUserdata, string name, int format);

    [LibraryImport(Library, EntryPoint = "mpv_command")]
    public static partial int Command(nint handle, nint* args);

    [LibraryImport(Library, EntryPoint = "mpv_command_async")]
    public static partial int CommandAsync(nint handle, ulong replyUserdata, nint* args);

    [LibraryImport(Library, EntryPoint = "mpv_request_log_messages", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int RequestLogMessages(nint handle, string minLevel);

    [LibraryImport(Library, EntryPoint = "mpv_wait_event")]
    public static partial Event* WaitEvent(nint handle, double timeout);

    [LibraryImport(Library, EntryPoint = "mpv_wakeup")]
    public static partial void Wakeup(nint handle);

    [LibraryImport(Library, EntryPoint = "mpv_render_context_create")]
    public static partial int RenderContextCreate(nint* context, nint handle, RenderParam* parameters);

    [LibraryImport(Library, EntryPoint = "mpv_render_context_set_update_callback")]
    public static partial void RenderContextSetUpdateCallback(nint context, delegate* unmanaged[Cdecl]<nint, void> callback, nint callbackContext);

    [LibraryImport(Library, EntryPoint = "mpv_render_context_update")]
    public static partial ulong RenderContextUpdate(nint context);

    [LibraryImport(Library, EntryPoint = "mpv_render_context_render")]
    public static partial int RenderContextRender(nint context, RenderParam* parameters);

    [LibraryImport(Library, EntryPoint = "mpv_render_context_free")]
    public static partial void RenderContextFree(nint context);

    public static string Error(int code) => Marshal.PtrToStringUTF8(ErrorString(code)) ?? $"error {code}";
}
