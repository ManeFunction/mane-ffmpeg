namespace ManeFfmpeg.Core.Model;

/// <summary>
/// Where the audio under a stretch of a clip's video comes from, once the clip's audio is slipped:
/// <see cref="Lead"/> of silence, then <see cref="ReadLength"/> of the file read from <see cref="ReadFrom"/>,
/// then <see cref="Tail"/> of silence, <see cref="Length"/> in all. Clips' audio is placed through
/// <see cref="AudioLayout"/>, which uses this for audio that isn't looped.
/// </summary>
public readonly record struct AudioPlacement(TimeSpan Lead, TimeSpan ReadFrom, TimeSpan ReadLength, TimeSpan Length)
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(1);

    public TimeSpan Tail => Length - Lead - ReadLength;

    /// <summary>True when (almost) none of the file's audio falls in the range: it is all silence.</summary>
    public bool IsSilent => ReadLength <= Tolerance;

    /// <summary>
    /// Audio under the video <c>[videoStart, videoStart + length)</c> of a clip whose audio is slipped by
    /// <paramref name="offset"/>, from a file whose audio stream ends at <paramref name="audioEnd"/>.
    /// </summary>
    public static AudioPlacement For(TimeSpan videoStart, TimeSpan length, TimeSpan offset, TimeSpan audioEnd)
    {
        var start = videoStart - offset;
        var readFrom = start < TimeSpan.Zero ? TimeSpan.Zero : start;
        var lead = readFrom - start < length ? readFrom - start : length;
        var end = start + length < audioEnd ? start + length : audioEnd;
        var readLength = end > readFrom ? end - readFrom : TimeSpan.Zero;
        return new AudioPlacement(lead, readFrom, readLength, length);
    }
}

/// <summary>
/// A stretch of a looped audio file: <see cref="Length"/> of the file read from <see cref="Phase"/>, starting again
/// from its beginning every <see cref="Period"/>. <see cref="AudioLayout"/> uses this for looped audio.
/// </summary>
public readonly record struct AudioLoop(TimeSpan Phase, TimeSpan Period, TimeSpan Length)
{
    /// <summary>Files shorter than this play once instead of looping.</summary>
    public static readonly TimeSpan MinPeriod = TimeSpan.FromMilliseconds(100);

    /// <summary>The stretch runs past the file's end, so the file repeats in it.</summary>
    public bool Wraps => Phase + Length > Period;

    /// <summary>Whether a file whose audio ends at <paramref name="audioEnd"/> can loop.</summary>
    public static bool CanLoop(TimeSpan audioEnd) => audioEnd >= MinPeriod;

    /// <summary>The looped audio under the video <c>[videoStart, videoStart + length)</c> of a clip slipped by <paramref name="slip"/>.</summary>
    public static AudioLoop For(TimeSpan videoStart, TimeSpan length, TimeSpan slip, TimeSpan audioEnd)
    {
        var start = (videoStart - slip).Ticks % audioEnd.Ticks;
        if (start < 0)
            start += audioEnd.Ticks;
        return new AudioLoop(TimeSpan.FromTicks(start), audioEnd, length);
    }

    /// <summary>
    /// The stretch's first piece as a plain read from <see cref="Phase"/>: all of it for a loop that doesn't wrap,
    /// followed by silence for one that does.
    /// </summary>
    public AudioPlacement AsPlacement() => new(TimeSpan.Zero, Phase, Wraps ? Period - Phase : Length, Length);

    /// <summary>The pieces of the file to play in order: from the phase to the end, whole files, then the rest.</summary>
    public IEnumerable<(TimeSpan ReadFrom, TimeSpan Length)> Pieces()
    {
        var left = Length;
        var from = Phase;
        while (left > TimeSpan.Zero)
        {
            var piece = Period - from < left ? Period - from : left;
            yield return (from, piece);
            left -= piece;
            from = TimeSpan.Zero;
        }
    }
}
