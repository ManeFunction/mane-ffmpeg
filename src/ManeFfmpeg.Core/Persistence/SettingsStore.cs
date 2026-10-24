using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManeFfmpeg.Core.Persistence;

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

public sealed record AppSettings
{
    public string? FfmpegPath { get; init; }
    public string? FfprobePath { get; init; }
    public string? LibMpvPath { get; init; }
    public bool HardwareDecoding { get; init; } = true;
    public bool Notifications { get; init; } = true;
    public ThemeChoice Theme { get; init; } = ThemeChoice.Dark;
    public List<string> RecentProjects { get; init; } = [];
    public string? LastMediaFolder { get; init; }
    public string? LastProjectFolder { get; init; }
}

/// <summary>App-wide settings in settings.json.</summary>
public sealed class SettingsStore
{
    private const int MaxRecent = 10;
    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.ConfigDirectory, "settings.json");
        Current = Load(_path) ?? new AppSettings();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler? Changed;

    public void Update(Func<AppSettings, AppSettings> change)
    {
        Current = change(Current);
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings));
        }
        catch (IOException)
        {
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AddRecentProject(string path) => Update(s => s with
    {
        RecentProjects = [path, .. s.RecentProjects.Where(p => p != path).Take(MaxRecent - 1)],
        LastProjectFolder = Path.GetDirectoryName(path),
    });

    private static AppSettings? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.AppSettings) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
