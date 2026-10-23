using System.Reflection;
using System.Runtime.InteropServices;

namespace ManeFfmpeg.Core.Mpv;

/// <summary>
/// Finds and loads libmpv. Order: explicit path, Homebrew, MacPorts, IINA's bundled copy (macOS), then the
/// system loader (Linux sonames). IINA's libmpv resolves its dependencies through @rpath, which only works
/// when DYLD_FALLBACK_LIBRARY_PATH points at IINA's Frameworks folder before the process starts, so the
/// app relaunches itself once with that variable set (see <see cref="RelaunchEnvironment"/>).
/// </summary>
public static class MpvLibrary
{
    public const string IinaFrameworks = "/Applications/IINA.app/Contents/Frameworks";

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
    public static IEnumerable<string> Candidates(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            yield return explicitPath;
        if (OperatingSystem.IsMacOS())
        {
            foreach (var dir in new[] { "/opt/homebrew/lib", "/usr/local/lib", "/opt/local/lib", IinaFrameworks })
            {
                yield return Path.Combine(dir, "libmpv.2.dylib");
                yield return Path.Combine(dir, "libmpv.dylib");
            }
        }
        else
        {
            foreach (var dir in new[] { "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/lib64", "/usr/lib", "/usr/local/lib" })
            {
                yield return Path.Combine(dir, "libmpv.so.2");
                yield return Path.Combine(dir, "libmpv.so.1");
                yield return Path.Combine(dir, "libmpv.so");
            }
        }
    }

    /// <summary>The file that <see cref="TryLoad"/> would pick, or null.</summary>
    public static string? Find(string? explicitPath) =>
        Candidates(explicitPath).FirstOrDefault(File.Exists);

    /// <summary>
    /// When the chosen libmpv is IINA's and the variable isn't set yet, returns the
    /// DYLD_FALLBACK_LIBRARY_PATH value to relaunch with; otherwise null.
    /// </summary>
    public static string? RelaunchEnvironment(string? explicitPath)
    {
        if (!OperatingSystem.IsMacOS())
            return null;
        var path = Find(explicitPath);
        if (path is null)
            return null;
        // Only copies bundled inside an .app use @rpath; Homebrew/MacPorts dylibs have absolute install names.
        var dir = Path.GetDirectoryName(path)!;
        if (!dir.EndsWith("/Contents/Frameworks", StringComparison.Ordinal))
            return null;
        var current = Environment.GetEnvironmentVariable("DYLD_FALLBACK_LIBRARY_PATH") ?? "";
        if (current.Split(':').Contains(dir))
            return null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaults = current.Length > 0 ? current : $"{home}/lib:/usr/local/lib:/usr/lib";
        return $"{dir}:{defaults}";
    }

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
            if (_handle == 0 && OperatingSystem.IsLinux())
            {
                foreach (var soname in new[] { "libmpv.so.2", "libmpv.so.1" })
                {
                    if (NativeLibrary.TryLoad(soname, out var handle))
                    {
                        _handle = handle;
                        LoadedPath = soname;
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
    public static string InstallHint => OperatingSystem.IsMacOS()
        ? "Install IINA (brew install --cask iina) or mpv (brew install mpv), or set the libmpv path in Settings."
        : "Install libmpv (Debian/Ubuntu: sudo apt install libmpv2, Fedora: sudo dnf install mpv-libs, Arch: sudo pacman -S mpv).";
}
