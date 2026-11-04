namespace ManeFfmpeg.Core.Model;

/// <summary>
/// The file a clip plays (an audio file dropped on it, or its own video), with its probe results and chosen stream.
/// </summary>
public sealed record HeardAudio(SourceMedia File, MediaInfo Info, AudioStreamInfo Stream)
{
    /// <summary>Where the heard stream ends.</summary>
    public TimeSpan End => Info.AudioEnd(Stream);

    /// <summary>The sound under the video <c>[videoStart, videoStart + length)</c> of <paramref name="clip"/>.</summary>
    public AudioLayout Layout(Clip clip, TimeSpan videoStart, TimeSpan length) => AudioLayout.For(clip, videoStart, length, End);

    /// <summary>The sound under the whole clip.</summary>
    public AudioLayout Layout(Clip clip) => Layout(clip, clip.In, clip.Duration);
}

/// <summary>
/// <see cref="Length"/> of sound starting <see cref="At"/> into a stretch: the heard file read from
/// <see cref="ReadFrom"/>, or silence when that is null.
/// </summary>
public readonly record struct AudioSpan(TimeSpan At, TimeSpan Length, TimeSpan? ReadFrom)
{
    public bool IsSilence => ReadFrom is null;
}

/// <summary>
/// What a stretch of a clip sounds like: the heard file played once where the slipped audio falls
/// (<see cref="Placed"/>, silence around it), or repeated (<see cref="Looped"/>, for replaced audio that loops).
/// This is the one place that chooses between them; the preview, every export mode, the timeline drawing and
/// snapping all render its answer, so they always agree.
/// </summary>
public abstract record AudioLayout
{
    private AudioLayout() { }

    /// <summary>The sound under the video <c>[videoStart, videoStart + length)</c> of a clip whose heard file ends at <paramref name="audioEnd"/>.</summary>
    public static AudioLayout For(Clip clip, TimeSpan videoStart, TimeSpan length, TimeSpan audioEnd) =>
        clip.LoopsAudio && AudioLoop.CanLoop(audioEnd)
            ? new Looped(AudioLoop.For(videoStart, length, clip.AudioSlip, audioEnd))
            : new Placed(AudioPlacement.For(videoStart, length, clip.AudioSlip, audioEnd), videoStart - clip.AudioSlip);

    public abstract TimeSpan Length { get; }

    /// <summary>The stretch from start to end, as file reads and silences in order.</summary>
    public abstract IEnumerable<AudioSpan> Spans();

    /// <summary>
    /// Where the sound starts, runs out or starts again, strictly inside the stretch and within
    /// <c>[from, to]</c>, in order: the marks the timeline draws and the playhead snaps to.
    /// </summary>
    public abstract IEnumerable<TimeSpan> Edges(TimeSpan from, TimeSpan to);

    /// <summary>The time in the heard file playing at <paramref name="local"/> into the stretch (outside the file where it is silent).</summary>
    public abstract TimeSpan FileTime(TimeSpan local);

    /// <summary>The file played once, slipped: silence where the slipped read runs past the file.</summary>
    /// <param name="FileStart">The file time at the stretch's start, before it is clamped to the file.</param>
    public sealed record Placed(AudioPlacement Placement, TimeSpan FileStart) : AudioLayout
    {
        public override TimeSpan Length => Placement.Length;

        public override IEnumerable<AudioSpan> Spans()
        {
            if (Placement.IsSilent)
            {
                yield return new AudioSpan(TimeSpan.Zero, Length, null);
                yield break;
            }
            if (Placement.Lead > TimeSpan.Zero)
                yield return new AudioSpan(TimeSpan.Zero, Placement.Lead, null);
            yield return new AudioSpan(Placement.Lead, Placement.ReadLength, Placement.ReadFrom);
            if (Placement.Tail > TimeSpan.Zero)
                yield return new AudioSpan(Placement.Lead + Placement.ReadLength, Placement.Tail, null);
        }

        public override IEnumerable<TimeSpan> Edges(TimeSpan from, TimeSpan to)
        {
            var lead = Placement.Lead;
            if (lead > TimeSpan.Zero && lead < Length && lead >= from && lead <= to)
                yield return lead;
            var end = lead + Placement.ReadLength;
            if (Placement.ReadLength > TimeSpan.Zero && Placement.Tail > TimeSpan.Zero && end >= from && end <= to)
                yield return end;
        }

        public override TimeSpan FileTime(TimeSpan local) => FileStart + local;
    }

    /// <summary>The file repeated with no silence, from the loop's phase.</summary>
    public sealed record Looped(AudioLoop Loop) : AudioLayout
    {
        public override TimeSpan Length => Loop.Length;

        public override IEnumerable<AudioSpan> Spans()
        {
            var at = TimeSpan.Zero;
            foreach (var (readFrom, length) in Loop.Pieces())
            {
                yield return new AudioSpan(at, length, readFrom);
                at += length;
            }
        }

        /// <summary>The seams: the file starts again at Period − Phase into the stretch, then every period.</summary>
        public override IEnumerable<TimeSpan> Edges(TimeSpan from, TimeSpan to)
        {
            var first = Loop.Period - Loop.Phase;
            var skip = from > first ? (long)Math.Ceiling((from - first) / Loop.Period) : 0;
            for (var seam = first + Loop.Period * skip; seam <= to && seam < Length; seam += Loop.Period)
                if (seam > TimeSpan.Zero)
                    yield return seam;
        }

        public override TimeSpan FileTime(TimeSpan local)
        {
            var ticks = (Loop.Phase + local).Ticks % Loop.Period.Ticks;
            return TimeSpan.FromTicks(ticks < 0 ? ticks + Loop.Period.Ticks : ticks);
        }
    }
}
