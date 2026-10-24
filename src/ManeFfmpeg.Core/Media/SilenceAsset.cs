using CliWrap;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Media;

/// <summary>A long silent audio file used to fill audio gaps in preview EDLs.</summary>
public static class SilenceAsset
{
    public static readonly TimeSpan Length = TimeSpan.FromMinutes(10);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string FilePath => Path.Combine(AppPaths.CacheDirectory, "silence-48k-stereo.mka");

    public static async Task<string> EnsureAsync(FfmpegTools tools, CancellationToken ct = default)
    {
        var path = FilePath;
        if (File.Exists(path))
            return path;
        await Gate.WaitAsync(ct);
        try
        {
            if (File.Exists(path))
                return path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp.mka";
            await Cli.Wrap(tools.Ffmpeg)
                .WithArguments([.. FfmpegTools.Quiet, "-y", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                    "-t", ((int)Length.TotalSeconds).ToString(), "-c:a", "flac", temp])
                .ExecuteAsync(ct);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        finally
        {
            Gate.Release();
        }
    }
}
