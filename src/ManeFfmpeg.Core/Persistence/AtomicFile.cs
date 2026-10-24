namespace ManeFfmpeg.Core.Persistence;

/// <summary>Writes a file through a temporary sibling and a rename, so a crash never leaves it half written.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }

    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, contents, ct);
        File.Move(temp, path, overwrite: true);
    }
}
