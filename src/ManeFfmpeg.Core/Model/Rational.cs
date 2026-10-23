using System.Globalization;

namespace ManeFfmpeg.Core.Model;

/// <summary>An exact fraction such as ffprobe's "30000/1001" frame rate.</summary>
public readonly record struct Rational(long Num, long Den)
{
    public static readonly Rational Zero = new(0, 1);

    public bool IsValid => Num > 0 && Den > 0;

    public double Value => Den == 0 ? 0 : (double)Num / Den;

    public static Rational Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Zero;
        var parts = text.Split('/', ':');
        if (parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num)
            && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var den))
            return new Rational(num, den);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
            return new Rational((long)Math.Round(value * 1000), 1000);
        return Zero;
    }

    public bool Equivalent(Rational other) =>
        IsValid && other.IsValid && Num * other.Den == other.Num * Den;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Num}/{Den}");
}
