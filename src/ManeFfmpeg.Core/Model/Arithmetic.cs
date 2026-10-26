using System.Globalization;

namespace ManeFfmpeg.Core.Model;

/// <summary>
/// Evaluates simple arithmetic typed into number fields: + − × ÷ and parentheses, e.g. "0-700" or "2*(40+1.5)".
/// Accepts "." or "," as the decimal separator and the typographic minus, times and division signs.
/// </summary>
public static class Arithmetic
{
    public static bool TryEvaluate(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parser = new Parser(text);
        try
        {
            value = parser.Expression();
            return parser.AtEnd && double.IsFinite(value);
        }
        catch (FormatException)
        {
            value = 0;
            return false;
        }
    }

    private sealed class Parser(string text)
    {
        private int _pos;

        public bool AtEnd
        {
            get
            {
                SkipSpaces();
                return _pos == text.Length;
            }
        }

        // expression := term (('+' | '-') term)*
        public double Expression()
        {
            var value = Term();
            while (true)
            {
                if (Accept('+'))
                    value += Term();
                else if (Accept('-', '−', '–'))
                    value -= Term();
                else
                    return value;
            }
        }

        // term := factor (('*' | '/') factor)*
        private double Term()
        {
            var value = Factor();
            while (true)
            {
                if (Accept('*', '×', '·'))
                    value *= Factor();
                else if (Accept('/', '÷'))
                    value /= Factor();
                else
                    return value;
            }
        }

        // factor := ('+' | '-') factor | '(' expression ')' | number
        private double Factor()
        {
            if (Accept('+'))
                return Factor();
            if (Accept('-', '−', '–'))
                return -Factor();
            if (Accept('('))
            {
                var value = Expression();
                if (!Accept(')'))
                    throw new FormatException("Missing closing parenthesis.");
                return value;
            }
            return Number();
        }

        private double Number()
        {
            SkipSpaces();
            var start = _pos;
            while (_pos < text.Length && (char.IsAsciiDigit(text[_pos]) || text[_pos] is '.' or ','))
                _pos++;
            var number = text[start.._pos].Replace(',', '.');
            if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"Not a number at {start}.");
            return value;
        }

        private bool Accept(params ReadOnlySpan<char> symbols)
        {
            SkipSpaces();
            if (_pos >= text.Length || !symbols.Contains(text[_pos]))
                return false;
            _pos++;
            return true;
        }

        private void SkipSpaces()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
                _pos++;
        }
    }
}
