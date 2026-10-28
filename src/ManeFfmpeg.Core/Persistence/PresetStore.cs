using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Persistence;

/// <summary>The user's export presets, shared by all windows and saved to presets.json.</summary>
public sealed class PresetStore
{
    private const int CurrentVersion = 3;

    private readonly string _path;
    private List<Preset> _presets;

    public PresetStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.ConfigDirectory, "presets.json");
        var (presets, version) = Load(_path);
        _presets = presets ?? [.. Preset.BuiltIns];
        if (presets is not null && version < CurrentVersion && Migrate(_presets, version))
            Persist();
    }

    /// <summary>
    /// Version 2 made "for storage" keep more detail and kept the old settings as "harsh": an unedited
    /// "for storage" gets the new arguments, and "harsh" is added next to it. Version 3 added "share".
    /// </summary>
    private static bool Migrate(List<Preset> presets, int version)
    {
        if (version < 2)
        {
            var storage = presets.FindIndex(p => p.Id == Preset.ForStorage.Id);
            if (storage >= 0 && presets[storage].Arguments.Trim() == Preset.ForStorageV1Arguments)
                presets[storage] = presets[storage] with { Arguments = Preset.ForStorage.Arguments };
            if (presets.All(p => p.Id != Preset.Harsh.Id))
                presets.Insert(storage >= 0 ? storage + 1 : 0, Preset.Harsh);
        }
        if (presets.All(p => p.Id != Preset.Share.Id))
        {
            var harsh = presets.FindIndex(p => p.Id == Preset.Harsh.Id);
            presets.Insert(harsh >= 0 ? harsh + 1 : presets.Count, Preset.Share);
        }
        return true;
    }

    public IReadOnlyList<Preset> Presets => _presets;

    /// <summary>Why the last save failed (full disk, read-only folder…), or null. Changes stay in memory either way.</summary>
    public string? SaveError { get; private set; }

    public event EventHandler? Changed;

    public Preset? Find(string? id) => id is null ? null : _presets.Find(p => p.Id == id);

    public void Save(Preset preset)
    {
        var index = _presets.FindIndex(p => p.Id == preset.Id);
        if (index >= 0)
            _presets[index] = preset;
        else
            _presets.Add(preset);
        Persist();
    }

    /// <summary>Moves a preset to another place in the list (and in the export bar's menu).</summary>
    public void Move(string id, int index)
    {
        var from = _presets.FindIndex(p => p.Id == id);
        index = Math.Clamp(index, 0, _presets.Count - 1);
        if (from < 0 || from == index)
            return;
        var preset = _presets[from];
        _presets.RemoveAt(from);
        _presets.Insert(index, preset);
        Persist();
    }

    public void Delete(string id)
    {
        if (_presets.RemoveAll(p => p.Id == id) > 0)
            Persist();
    }

    /// <summary>Puts the built-in presets back (replacing edited copies with the same id).</summary>
    public void RestoreBuiltIns()
    {
        foreach (var builtIn in Preset.BuiltIns.Reverse())
        {
            var index = _presets.FindIndex(p => p.Id == builtIn.Id);
            if (index >= 0)
                _presets[index] = builtIn;
            else
                _presets.Insert(0, builtIn);
        }
        Persist();
    }

    private void Persist()
    {
        var file = new PresetFile { Version = CurrentVersion, Presets = _presets };
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(file, PresetJsonContext.Readable.PresetFile));
            SaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = $"Couldn't save the presets: {ex.Message}";
            AppLog.Write(SaveError);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static (List<Preset>? Presets, int Version) Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return (null, CurrentVersion);
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), PresetJsonContext.Readable.PresetFile);
            return (file?.Presets.Where(p => !string.IsNullOrWhiteSpace(p.Id)).Select(p => p.WithDefaults()).ToList(), file?.Version ?? 0);
        }
        catch (Exception)
        {
            return (null, CurrentVersion);
        }
    }

    internal sealed class PresetFile
    {
        public int Version { get; set; }
        public List<Preset> Presets { get; set; } = [];
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PresetStore.PresetFile))]
internal sealed partial class PresetJsonContext : JsonSerializerContext
{
    /// <summary>Keeps quotes and non-ASCII (e.g. Cyrillic paths) readable instead of \\u-escapes.</summary>
    private static PresetJsonContext? _readable;

    public static PresetJsonContext Readable => _readable ??= new(new JsonSerializerOptions(Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
