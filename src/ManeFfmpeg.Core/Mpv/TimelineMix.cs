using System.Globalization;
using System.Text;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Mpv;

/// <summary>A gain that holds from timeline time <see cref="At"/> until the next one.</summary>
public readonly record struct GainStep(TimeSpan At, double Gain);

/// <summary>
/// What the preview mixes on top of the EDL's audio streams (<see cref="TimelineEdl"/>): each clip's volume on A1, and
/// the A2 track with its clips' volumes. mpv plays it through a "lavfi-complex" graph whose volume filters follow the
/// timeline time, so one graph serves the whole timeline and seeking needs no rebuild.
/// </summary>
/// <param name="A1">The A1 gain over time, starting at 0.</param>
/// <param name="A2">The A2 gain over time, or null when A2 has no clips.</param>
public sealed record TimelineMix(IReadOnlyList<GainStep> A1, IReadOnlyList<GainStep>? A2)
{
    /// <summary>The mix for a project, or null when there is nothing to mix (no A2, every volume unchanged).</summary>
    public static TimelineMix? For(ProjectState state)
    {
        var clips = state.Timeline.Clips;
        if (state.AudioTrack.Count == 0 && clips.All(c => Gain.IsUnity(c.Volume)))
            return null;
        var bounds = state.Timeline.Bounds;
        var a1 = Steps(clips.Select((c, i) => new GainStep(bounds[i], c.Volume)));
        // Between A2 clips there is only silence, so a gap takes the next clip's gain and needs no step of its own.
        var a2 = state.AudioTrack.Count == 0 ? null : Steps(state.AudioTrack.Select((c, i) => new GainStep(i == 0 ? TimeSpan.Zero : c.Start, c.Volume)));
        return new TimelineMix(a1, a2);
    }

    /// <summary>
    /// The lavfi-complex graph that plays mpv audio track <paramref name="a1"/> (and <paramref name="a2"/>, mixed in,
    /// when A2 has clips and the track is there) at these gains.
    /// </summary>
    /// <param name="from">
    /// Where playback (re)starts. amix joins its inputs sample by sample, not by timestamp, and after a seek each input
    /// starts where its own file's seek landed: a WebM's audio can land a minute early, A2 a few seconds. mpv then trims
    /// the mix up to the target by A1's timestamps, which takes the same amount off A2, so A2 played early. Each input is
    /// therefore cut to start here (atrim, on timestamps) before they are mixed. Nothing is padded: mpv rebuilds the graph
    /// whenever an input changes format (A2 going from the silence to an MP3), and a rebuilt graph must not pad again
    /// from this point; atrim only drops what is before it, which a rebuilt graph never sees.
    /// </param>
    public string Graph(string a1, string? a2, TimeSpan? from = null)
    {
        if (A2 is null || a2 is null)
            return $"[aid{a1}]{Volume(A1)}[ao]";
        var align = from is { } t ? string.Create(CultureInfo.InvariantCulture, $"atrim=start={t.TotalSeconds:0.######},") : "";
        // normalize=0: a sum, like the export, rather than each input at half volume.
        return $"[aid{a1}]{align}{Volume(A1)}[a1];[aid{a2}]{align}{Volume(A2)}[a2];[a1][a2]amix=inputs=2:normalize=0:duration=first:dropout_transition=0[ao]";
    }
    /// <summary>
    /// A volume filter for the gains: a constant, or an expression evaluated per audio frame on the timeline time
    /// <c>t</c>. The expression is a flat sum of steps, <c>g0+(g1−g0)*gte(t,T1)+…</c>, because ffmpeg limits how deeply
    /// expressions may nest.
    /// </summary>
    internal static string Volume(IReadOnlyList<GainStep> steps)
    {
        if (steps.Count <= 1)
            return $"volume={Number(steps.Count == 0 ? 1 : steps[0].Gain)}";
        var sb = new StringBuilder("volume=eval=frame:volume='").Append(Number(steps[0].Gain));
        for (var i = 1; i < steps.Count; i++)
        {
            var change = steps[i].Gain - steps[i - 1].Gain;
            sb.Append(change < 0 ? '-' : '+').Append(Number(Math.Abs(change)))
              .Append("*gte(t,").Append(steps[i].At.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture)).Append(')');
        }
        return sb.Append('\'').ToString();
    }

    /// <summary>The steps without repeats: a step only where the gain changes.</summary>
    private static List<GainStep> Steps(IEnumerable<GainStep> steps)
    {
        var result = new List<GainStep>();
        foreach (var step in steps)
            if (result.Count == 0 || Math.Abs(result[^1].Gain - step.Gain) > 1e-6)
                result.Add(step);
        return result;
    }

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public bool Equals(TimelineMix? other) =>
        other is not null && A1.SequenceEqual(other.A1) && (A2 is null ? other.A2 is null : other.A2 is not null && A2.SequenceEqual(other.A2));

    public override int GetHashCode() => HashCode.Combine(A1.Count, A2?.Count);
}
