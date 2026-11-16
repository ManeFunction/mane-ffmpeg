using System.Reflection;
using System.Runtime.InteropServices;
using ManeFfmpeg.Core.Platforms;

namespace ManeFfmpeg.Core.Mpv;

/// <summary>
/// Finds and loads libmpv: the explicit path, then the platform's candidates (<see cref="IPlatform.LibMpvCandidates"/>),
/// then the system loader. IINA's libmpv on macOS needs an environment variable set before the process starts, so the
/// app relaunches itself once with it (see <see cref="RelaunchEnvironment"/>).
/// </summary>
public static class MpvLibrary
{
    /// <summary>Oldest client API with the software render API (mpv 0.33).</summary>
    private const uint MinimumApi = (1u << 16) | 107;

    private static readonly object Gate = new();
    private static nint _handle;
    private static bool _resolverSet;

    public static string? LoadedPath { get; private set; }
    public static string? LoadError { get; private set; }
    public static bool IsLoaded => _handle != 0;

    public static string? ApiVersion => IsLoaded
        ? $"{MpvNative.ClientApiVersion() >> 16}.{MpvNative.ClientApiVersion() & 0xffff}"
        : null;

    /// <summary>Candidate files, most preferred first.</summary>
    public static IEnumerable<string> Candidates(string? explicitPath) => Platform.Current.LibMpvCandidates(explicitPath);

    /// <summary>The file that <see cref="TryLoad"/> would pick, or null.</summary>
    public static string? Find(string? explicitPath) =>
        Candidates(explicitPath).FirstOrDefault(File.Exists);

    /// <summary>
    /// The environment the process must be restarted with for the chosen libmpv to load (IINA's on macOS), or null when
    /// it loads as it is.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? RelaunchEnvironment(string? explicitPath) =>
        Find(explicitPath) is { } path ? Platform.Current.LibMpvRelaunchEnvironment(path) : null;

    public static bool TryLoad(string? explicitPath)
    {
        lock (Gate)
        {
            if (_handle != 0)
                return true;
            var errors = new List<string>();
            foreach (var candidate in Candidates(explicitPath).Where(File.Exists))
            {
                if (!NativeLibrary.TryLoad(candidate, out var handle))
                {
                    errors.Add($"{candidate}: could not be loaded");
                    continue;
                }
                _handle = handle;
                LoadedPath = candidate;
                break;
            }
            if (_handle == 0)
            {
                foreach (var name in Platform.Current.LibMpvLoaderNames)
                {
                    if (NativeLibrary.TryLoad(name, out var handle))
                    {
                        _handle = handle;
                        LoadedPath = name;
                        break;
                    }
                }
            }
            if (_handle == 0)
            {
                LoadError = errors.Count > 0 ? string.Join("; ", errors) : "libmpv was not found.";
                return false;
            }

            if (!_resolverSet)
            {
                NativeLibrary.SetDllImportResolver(typeof(MpvLibrary).Assembly, Resolve);
                _resolverSet = true;
            }

            var api = MpvNative.ClientApiVersion();
            if (api < MinimumApi)
            {
                LoadError = $"libmpv is too old (client API {api >> 16}.{api & 0xffff}); mpv 0.33 or newer is needed.";
                _handle = 0;
                return false;
            }
            LoadError = null;
            return true;
        }
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path) =>
        name == MpvNative.Library ? _handle : 0;

    /// <summary>A hint for installing libmpv on this platform.</summary>
    public static string InstallHint => Platform.Current.LibMpvInstallHint;
}
