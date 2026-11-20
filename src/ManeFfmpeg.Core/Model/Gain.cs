namespace ManeFfmpeg.Core.Model;

/// <summary>Volume as a gain factor, shown as a percentage.</summary>
public static class Gain
{
    /// <summary>The loudest a clip can be made: 200 %, about +6 dB.</summary>
    public const double Max = 2;

    /// <summary>The loudest anything is played: a clip at <see cref="Max"/> on a track at <see cref="Max"/>.</summary>
    public const double MaxCombined = Max * Max;

    public static double Clamp(double gain) => double.IsNaN(gain) ? 1 : Math.Clamp(gain, 0, Max);

    /// <summary>
    /// The widest a stored gain can be: a clip's gain with its track's folded in. Clip volumes are clamped to this when
    /// set, so no gain read from anywhere can be negative or NaN; the editors clamp to <see cref="Max"/>.
    /// </summary>
    public static double ClampHeard(double gain) => double.IsNaN(gain) ? 1 : Math.Clamp(gain, 0, MaxCombined);

    /// <summary>Whether a gain leaves the sound as it is (within rounding of a slider).</summary>
    public static bool IsUnity(double gain) => Math.Abs(gain - 1) < 1e-4;

    /// <summary>"80 %", "100 %", "0 %".</summary>
    public static string Percent(double gain) => $"{Math.Round(gain * 100):0} %";

    /// <summary>"−1.9 dB", "+6.0 dB", "−∞ dB".</summary>
    public static string Decibels(double gain) => gain <= 0
        ? "−∞ dB"
        : (20 * Math.Log10(gain)).ToString("+0.0 dB;−0.0 dB;0.0 dB", System.Globalization.CultureInfo.InvariantCulture);
}
