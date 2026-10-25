using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Media;

public enum SourceStatus
{
    Analyzing,
    Ready,
    Missing,
    Failed,
}

/// <summary>The analyzers shared by every project (the app's, or real ones over test media).</summary>
public interface IMediaAnalyzers
{
    /// <summary>Probes a file; implementations cache per file version.</summary>
    Task<MediaInfo> ProbeAsync(string path);

    /// <summary>Null while ffmpeg isn't available.</summary>
    WaveformService? Waveforms { get; }

    /// <summary>Null while ffmpeg isn't available.</summary>
    KeyframeService? Keyframes { get; }
}

/// <summary>
/// What a project knows about each of its sources, by source id: whether it's ready, its probe result, the
/// chosen audio stream, its waveform and (on demand) its keyframes. It analyzes again by itself when a
/// source's path or audio stream changes and when the tools change, and hands the preview and the export a
/// snapshot of the probe results. <see cref="Changed"/> fires on the thread that drives it.
/// </summary>
public sealed class SourceCatalog(IMediaAnalyzers analyzers)
{
    private sealed class Entry(SourceMedia source)
    {
        public SourceMedia Source { get; set; } = source;
        public SourceStatus Status { get; set; } = SourceStatus.Analyzing;
        public MediaInfo? Info { get; set; }
        public string? Error { get; set; }

        /// <summary>Bumped whenever the entry is analyzed again, so late results of an earlier run are dropped.</summary>
        public int Generation { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries = [];
    private bool _wantKeyframes;

    /// <summary>A source's status, probe result, waveform or keyframes changed (its id).</summary>
    public event Action<string>? Changed;

    /// <summary>Scan keyframes of every ready source (snapping to them, or stream copy).</summary>
    public bool WantKeyframes
    {
        get => _wantKeyframes;
        set
        {
            if (_wantKeyframes == value)
                return;
            _wantKeyframes = value;
            if (value)
                foreach (var entry in _entries.Values.Where(e => e.Status == SourceStatus.Ready))
                    _ = LoadAnalysisAsync(entry, entry.Generation);
        }
    }

    /// <summary>Probe results of the ready sources, by source id.</summary>
    public IReadOnlyDictionary<string, MediaInfo> Media =>
        _entries.Where(e => e.Value.Info is not null).ToDictionary(e => e.Key, e => e.Value.Info!);

    public SourceStatus Status(string sourceId) => _entries.TryGetValue(sourceId, out var e) ? e.Status : SourceStatus.Missing;

    public string? Error(string sourceId) => _entries.GetValueOrDefault(sourceId)?.Error;

    public MediaInfo? Info(string sourceId) => _entries.GetValueOrDefault(sourceId)?.Info;

    /// <summary>The source's chosen audio stream, or null when it has none (or isn't analyzed yet).</summary>
    public AudioStreamInfo? Audio(SourceMedia source) => Info(source.Id)?.Audio(source.AudioStreamIndex);

    public Waveform? Waveform(SourceMedia source) =>
        Info(source.Id) is { } info && info.Audio(source.AudioStreamIndex) is { } audio
            ? analyzers.Waveforms?.TryGet(source.Path, info, audio.Index)
            : null;

    public KeyframeIndex? Keyframes(SourceMedia source) =>
        Info(source.Id) is { } info ? analyzers.Keyframes?.TryGet(source.Path, info) : null;

    /// <summary>Scans the source's keyframes if that hasn't happened yet (stream-copy planning needs them).</summary>
    public async Task<KeyframeIndex> KeyframesAsync(SourceMedia source)
    {
        if (Info(source.Id) is not { } info || analyzers.Keyframes is not { } service)
            throw new InvalidOperationException($"\"{source.DisplayName}\" hasn't been analyzed.");
        var keyframes = await service.GetAsync(source.Path, info);
        Changed?.Invoke(source.Id);
        return keyframes;
    }

    /// <summary>
    /// Brings the catalog in line with the project's sources (after edits, undo or loading): new sources are
    /// analyzed, removed ones forgotten, a moved file is probed again and a new audio stream gets its waveform.
    /// </summary>
    public void Sync(IEnumerable<SourceMedia> sources)
    {
        var current = sources.ToList();
        var ids = current.Select(s => s.Id).ToHashSet();
        foreach (var id in _entries.Keys.Where(id => !ids.Contains(id)).ToList())
            _entries.Remove(id);
        foreach (var source in current)
        {
            if (!_entries.TryGetValue(source.Id, out var entry))
            {
                entry = _entries[source.Id] = new Entry(source);
                _ = ProbeAsync(entry);
            }
            else if (entry.Source != source)
            {
                var moved = entry.Source.Path != source.Path;
                var otherAudio = entry.Source.AudioStreamIndex != source.AudioStreamIndex;
                entry.Source = source;
                if (moved)
                    _ = ProbeAsync(entry);
                else if (otherAudio && entry.Status == SourceStatus.Ready)
                    _ = LoadAnalysisAsync(entry, entry.Generation);
            }
        }
    }

    /// <summary>The tools changed: sources that failed get another try, ready ones reload their analyses.</summary>
    public void Reanalyze()
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.Status != SourceStatus.Ready)
                _ = ProbeAsync(entry);
            else
                _ = LoadAnalysisAsync(entry, entry.Generation);
        }
    }

    private async Task ProbeAsync(Entry entry)
    {
        var generation = ++entry.Generation;
        var path = entry.Source.Path;
        if (!File.Exists(path))
        {
            Update(entry, generation, SourceStatus.Missing, null, null);
            return;
        }
        Update(entry, generation, SourceStatus.Analyzing, null, null);
        try
        {
            var info = await analyzers.ProbeAsync(path);
            if (info.Video is null)
            {
                Update(entry, generation, SourceStatus.Failed, info, "No video stream");
                return;
            }
            Update(entry, generation, SourceStatus.Ready, info, null);
            await LoadAnalysisAsync(entry, generation);
        }
        catch (Exception ex)
        {
            Update(entry, generation, SourceStatus.Failed, null, ex.Message);
        }
    }

    /// <summary>The waveform of the chosen audio stream (always) and the keyframes (when wanted), in the background.</summary>
    private async Task LoadAnalysisAsync(Entry entry, int generation)
    {
        if (entry.Info is not { } info)
            return;
        try
        {
            var tasks = new List<Task>();
            if (info.Audio(entry.Source.AudioStreamIndex) is { } audio && analyzers.Waveforms is { } waveforms)
                tasks.Add(waveforms.GetAsync(entry.Source.Path, info, audio.Index));
            if (_wantKeyframes && analyzers.Keyframes is { } keyframes)
                tasks.Add(keyframes.GetAsync(entry.Source.Path, info));
            foreach (var task in tasks)
            {
                await task;
                if (IsCurrent(entry, generation))
                    Changed?.Invoke(entry.Source.Id);
            }
        }
        catch (Exception)
        {
            // Waveforms and keyframes are optional helpers.
        }
    }

    private void Update(Entry entry, int generation, SourceStatus status, MediaInfo? info, string? error)
    {
        if (!IsCurrent(entry, generation))
            return;
        entry.Status = status;
        entry.Info = info;
        entry.Error = error;
        Changed?.Invoke(entry.Source.Id);
    }

    private bool IsCurrent(Entry entry, int generation) =>
        entry.Generation == generation && _entries.TryGetValue(entry.Source.Id, out var live) && live == entry;
}
