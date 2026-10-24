namespace ManeFfmpeg.Core.Model;

/// <summary>
/// Where the audio under a stretch of a clip's video comes from, once the clip's audio is slipped:
/// <see cref="Lead"/> of silence, then <see cref="ReadLength"/> of the file read from <see cref="ReadFrom"/>,
/// then <see cref="Tail"/> of silence, <see cref="Length"/> in all. The preview, every export mode and the
/// timeline's silence hatching place audio through this, so they always agree.
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
