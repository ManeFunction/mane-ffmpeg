using System.Collections.Concurrent;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Mpv;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.App.Services;

/// <summary>App-wide state shared by every window: settings, presets, located tools and media caches.</summary>
public sealed class AppServices
{
    private readonly ConcurrentDictionary<string, (long Size, DateTime Write, Task<MediaInfo> Task)> _probes = new();

    public static AppServices Instance { get; } = new();

    public SettingsStore Settings { get; } = new();
    public PresetStore Presets { get; } = new();

    public FfmpegTools? Tools { get; private set; }
    public string? ToolsError { get; private set; }
    public KeyframeService? Keyframes { get; private set; }
    public WaveformService? Waveforms { get; private set; }

    public bool MpvAvailable => MpvLibrary.IsLoaded;

    /// <summary>Raised on the thread that called <see cref="LocateToolsAsync"/> once tools are (re)located.</summary>
    public event EventHandler? ToolsChanged;

    public async Task LocateToolsAsync()
    {
        var s = Settings.Current;
        var tools = await FfmpegLocator.LocateAsync(s.FfmpegPath, s.FfprobePath);
        var changed = tools != Tools;
        Tools = tools;
        ToolsError = tools is null
            ? OperatingSystem.IsMacOS()
                ? "ffmpeg wasn't found. Install it (brew install ffmpeg) or set its path in Settings."
                : "ffmpeg wasn't found. Install it with your package manager or set its path in Settings."
            : tools.IsTooOld ? $"{tools.VersionText} is older than 5.0; some features may fail." : null;
        // Applying settings locates the tools again; the same tools keep what they've already analyzed.
        if (changed)
        {
            Keyframes = tools is null ? null : new KeyframeService(tools);
            Waveforms = tools is null ? null : new WaveformService(tools);
            _probes.Clear();
        }
        ToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void LoadMpv() => MpvLibrary.TryLoad(Settings.Current.LibMpvPath);

    /// <summary>Probes a file once per (size, modification time).</summary>
    public Task<MediaInfo> ProbeAsync(string path)
    {
        if (Tools is null)
            return Task.FromException<MediaInfo>(new FfprobeException(ToolsError ?? "ffprobe isn't available."));
        var info = new FileInfo(path);
        if (!info.Exists)
            return Task.FromException<MediaInfo>(new FileNotFoundException("File not found.", path));
        if (_probes.TryGetValue(path, out var entry) && entry.Size == info.Length && entry.Write == info.LastWriteTimeUtc
            && !entry.Task.IsFaulted)
            return entry.Task;
        var task = new Ffprobe(Tools).ProbeAsync(path);
        _probes[path] = (info.Length, info.LastWriteTimeUtc, task);
        return task;
    }
}
