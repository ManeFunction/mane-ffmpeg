using System.Text.Json;
using System.Text.Json.Serialization;

namespace ManeFfmpeg.Core.Persistence;

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Settable rather than init-only: the source-generated deserializer fills init-only properties through one object
/// initializer, so a key missing from an older settings.json would come back as false/null instead of these defaults.
/// </summary>
public sealed record AppSettings
{
    public string? FfmpegPath { get; set; }
    public string? FfprobePath { get; set; }
    public string? LibMpvPath { get; set; }
    public bool HardwareDecoding { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public ThemeChoice Theme { get; set; } = ThemeChoice.Dark;
    /// <summary>Offer to install missing ffmpeg/libmpv with Homebrew when the app starts (macOS).</summary>
    public bool AskToInstallAtStartup { get; set; } = true;
    /// <summary>Offer to make this app open .mfvp projects on double-click when the app starts.</summary>
    public bool AskToRegisterProjectFiles { get; set; } = true;
    /// <summary>Look for a newer release on GitHub when the app starts.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>A release the user asked not to be reminded about ("1.2.0"); a newer one is offered again.</summary>
    public string? SkippedUpdateVersion { get; set; }
    public List<string> RecentProjects { get; set; } = [];
    public string? LastMediaFolder { get; set; }
    public string? LastProjectFolder { get; set; }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The settings still apply for this session.
            AppLog.Write($"Couldn't save the settings: {ex.Message}");
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
