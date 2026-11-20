using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Mpv;

/// <summary>
/// What the preview plays for a project: the EDL (<see cref="TimelineEdl"/>) and the mix over its audio streams
/// (<see cref="TimelineMix"/>), built together so they always agree on what the EDL holds (whether A2 is there).
/// </summary>
public sealed record PreviewTimeline(string Url, TimelineMix? Mix)
{
    /// <summary>The project's preview, or null while it can't be played (no clips, or videos not analyzed yet).</summary>
    public static PreviewTimeline? For(ProjectState state, IReadOnlyDictionary<string, MediaInfo> media, string silencePath) =>
        TimelineEdl.Build(state, media, silencePath) is { } url ? new PreviewTimeline(url, TimelineMix.For(state)) : null;
}
