using System.Globalization;
using System.Runtime.InteropServices;

namespace ManeFfmpeg.Core.Mpv;

public sealed class MpvException(string message) : Exception(message);

/// <summary>
/// One libmpv instance with its own event thread. Events are raised on that thread; callers marshal
/// to the UI themselves. libmpv's client API is thread-safe, so commands may come from any thread.
/// </summary>
public sealed unsafe class MpvPlayer : IDisposable
{
    private readonly Thread _eventThread;
    private readonly Dictionary<ulong, string> _observed = [];
    private nint _handle;
    private volatile bool _stopping;

    private MpvPlayer(nint handle)
    {
        _handle = handle;
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
    }

    public nint Handle => _handle;

    /// <summary>Raised for observed properties: (name, value) with double, bool or string values (null = unavailable).</summary>
    public event Action<string, object?>? PropertyChanged;

    public event Action? FileLoaded;

    /// <summary>(reason, error): reason 0 = EOF, 2 = stopped, 4 = error.</summary>
    public event Action<int, int>? EndFile;

    public event Action? PlaybackRestarted;

    public event Action<string, string>? LogMessage;

    /// <param name="headless">No video or audio output, for tests; otherwise video goes to a render context.</param>
    public static MpvPlayer Create(bool hardwareDecoding, bool headless = false)
    {
        var handle = MpvNative.Create();
        if (handle == 0)
            throw new MpvException("mpv_create failed.");
        var player = new MpvPlayer(handle);
        var options = new (string, string)[]
        {
            ("config", "no"), ("terminal", "no"), ("vo", headless ? "null" : "libmpv"), ("idle", "yes"), ("keep-open", "always"),
            ("pause", "yes"), ("hr-seek", "yes"), ("hwdec", hardwareDecoding ? "auto-copy" : "no"),
            ("input-default-bindings", "no"), ("input-vo-keyboard", "no"), ("load-scripts", "no"), ("ytdl", "no"),
            ("osc", "no"), ("sub-auto", "no"), ("audio-file-auto", "no"), ("audio-client-name", "Mane FFmpeg"),
            ("load-unsafe-playlists", "yes"), ("audio-display", "no"),
            // No built-in Lua scripts (stats, console, menus…): the preview needs none of them.
            ("load-stats-overlay", "no"), ("load-console", "no"), ("load-auto-profiles", "no"), ("load-select", "no"),
            ("load-positioning", "no"), ("load-commands", "no"), ("load-context-menu", "no"),
        };
        foreach (var (name, value) in options)
            MpvNative.SetOptionString(handle, name, value);
        if (headless)
            MpvNative.SetOptionString(handle, "ao", "null");
        var init = MpvNative.Initialize(handle);
        if (init < 0)
        {
            MpvNative.TerminateDestroy(handle);
            throw new MpvException($"mpv_initialize failed: {MpvNative.Error(init)}");
        }
        MpvNative.RequestLogMessages(handle, "warn");
        player.Observe("time-pos", MpvNative.FormatDouble);
        player.Observe("duration", MpvNative.FormatDouble);
        player.Observe("pause", MpvNative.FormatFlag);
        player.Observe("eof-reached", MpvNative.FormatFlag);
        player._eventThread.Start();
        return player;
    }

    private void Observe(string name, int format)
    {
        var id = (ulong)_observed.Count + 1;
        _observed[id] = name;
        MpvNative.ObserveProperty(_handle, id, name, format);
    }

    public void Command(params string[] args) => Run(args, async: false);

    public void CommandAsync(params string[] args) => Run(args, async: true);

    private void Run(string[] args, bool async)
    {
        if (_handle == 0)
            return;
        var ptrs = stackalloc nint[args.Length + 1];
        for (var i = 0; i < args.Length; i++)
            ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
        ptrs[args.Length] = 0;
        try
        {
            var result = async ? MpvNative.CommandAsync(_handle, 0, ptrs) : MpvNative.Command(_handle, ptrs);
            if (result < 0)
                LogMessage?.Invoke("error", $"{args[0]}: {MpvNative.Error(result)}");
        }
        finally
        {
            for (var i = 0; i < args.Length; i++)
                Marshal.FreeCoTaskMem(ptrs[i]);
        }
    }

    public void SetString(string name, string value)
    {
        if (_handle != 0)
            MpvNative.SetPropertyString(_handle, name, value);
    }

    public void SetFlag(string name, bool value)
    {
        if (_handle == 0)
            return;
        var v = value ? 1 : 0;
        MpvNative.SetProperty(_handle, name, MpvNative.FormatFlag, &v);
    }

    public void SetDouble(string name, double value)
    {
        if (_handle != 0)
            MpvNative.SetProperty(_handle, name, MpvNative.FormatDouble, &value);
    }

    public double? GetDouble(string name)
    {
        double value;
        return _handle != 0 && MpvNative.GetProperty(_handle, name, MpvNative.FormatDouble, &value) >= 0 ? value : null;
    }

    public string? GetString(string name)
    {
        if (_handle == 0)
            return null;
        var ptr = MpvNative.GetPropertyString(_handle, name);
        if (ptr == 0)
            return null;
        try
        {
            return Marshal.PtrToStringUTF8(ptr);
        }
        finally
        {
            MpvNative.Free(ptr);
        }
    }

    public long? GetInt(string name) =>
        long.TryParse(GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private void EventLoop()
    {
        while (!_stopping)
        {
            var ev = MpvNative.WaitEvent(_handle, -1);
            if (_stopping || ev->EventId == MpvNative.EventIdShutdown)
                break;
            try
            {
                Dispatch(ev);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke("error", ex.Message);
            }
        }
    }

    private void Dispatch(MpvNative.Event* ev)
    {
        switch (ev->EventId)
        {
            case MpvNative.EventIdPropertyChange when ev->Data != 0:
            {
                var prop = (MpvNative.EventProperty*)ev->Data;
                var name = Marshal.PtrToStringUTF8(prop->Name) ?? "";
                object? value = prop->Format switch
                {
                    MpvNative.FormatDouble when prop->Data != 0 => *(double*)prop->Data,
                    MpvNative.FormatFlag when prop->Data != 0 => *(int*)prop->Data != 0,
                    MpvNative.FormatString when prop->Data != 0 => Marshal.PtrToStringUTF8(*(nint*)prop->Data),
                    _ => null,
                };
                PropertyChanged?.Invoke(name, value);
                break;
            }
            case MpvNative.EventIdFileLoaded:
                FileLoaded?.Invoke();
                break;
            case MpvNative.EventIdEndFile when ev->Data != 0:
            {
                var end = (MpvNative.EventEndFile*)ev->Data;
                EndFile?.Invoke(end->Reason, end->Error);
                break;
            }
            case MpvNative.EventIdPlaybackRestart:
                PlaybackRestarted?.Invoke();
                break;
            case MpvNative.EventIdLogMessage when ev->Data != 0:
            {
                var msg = (MpvNative.EventLogMessage*)ev->Data;
                LogMessage?.Invoke(Marshal.PtrToStringUTF8(msg->Level) ?? "", (Marshal.PtrToStringUTF8(msg->Text) ?? "").TrimEnd());
                break;
            }
        }
    }

    /// <summary>Stops the event thread and destroys the instance. Free any render context first.</summary>
    public void Dispose()
    {
        if (_handle == 0)
            return;
        _stopping = true;
        MpvNative.Wakeup(_handle);
        if (Thread.CurrentThread != _eventThread)
            _eventThread.Join(TimeSpan.FromSeconds(2));
        var handle = _handle;
        _handle = 0;
        MpvNative.TerminateDestroy(handle);
    }
}
