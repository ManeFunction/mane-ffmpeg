namespace ManeFfmpeg.Core.Model;

/// <summary>Short random ids for sources, clips and presets.</summary>
public static class Ids
{
    public static string New() => Guid.NewGuid().ToString("N")[..12];
}
