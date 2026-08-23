using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BaudRunner;

/// <summary>
/// Terminator appended to a sent command. AT-command modems need CR and answer
/// silence to LF, so this is a per-command choice rather than a single flag.
/// </summary>
public enum LineEnding { None, Lf, Cr, CrLf }

public sealed class CommandSlot
{
    private static readonly char[] _hexSeparators = { ' ', '\t', '\r', '\n', ',', ';', '-', '_' };

    // Latin-1 maps U+0000..U+00FF to the identical byte, which is what serial work
    // expects; anything above that has no byte representation and is reported rather
    // than silently replaced with '?' the way Encoding.ASCII does.
    private static readonly Encoding _textEncoding = Encoding.GetEncoding(28591, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);

    // {NN} is the same form the log uses to show a non-printable byte, so a value
    // copied out of the display can be pasted straight back into a command.
    private static readonly Regex _tokenPattern = new(@"<(CR|LF|TAB|ESC|NUL|BEL|BS|0[xX][0-9A-Fa-f]{1,2})>|\{([0-9A-Fa-f]{2})\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Text { get; set; } = "";
    public bool Hex { get; set; }
    public LineEnding Ending { get; set; } = LineEnding.None;

    public byte[] ToBytes()
    {
        var body = Hex ? ParseHex(Text) : ParseText(Text);
        var terminator = EndingBytes(Ending);
        if (terminator.Length == 0) return body;
        var result = new byte[body.Length + terminator.Length];
        body.CopyTo(result, 0);
        terminator.CopyTo(result, body.Length);
        return result;
    }

    public static byte[] EndingBytes(LineEnding ending) => ending switch
    {
        LineEnding.Lf => new byte[] { 0x0A },
        LineEnding.Cr => new byte[] { 0x0D },
        LineEnding.CrLf => new byte[] { 0x0D, 0x0A },
        _ => Array.Empty<byte>(),
    };

    /// <summary>Expands the readable control tokens, then encodes as Latin-1.</summary>
    public static byte[] ParseText(string text)
    {
        var expanded = _tokenPattern.Replace(text, match =>
        {
            if (match.Groups[2].Success)
                return ((char)byte.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString();
            var token = match.Groups[1].Value;
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ((char)byte.Parse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString();
            return token.ToUpperInvariant() switch
            {
                "CR" => "\r", "LF" => "\n", "TAB" => "\t",
                "ESC" => "\x1B", "NUL" => "\0", "BEL" => "\a", "BS" => "\b",
                _ => match.Value,
            };
        });
        try { return _textEncoding.GetBytes(expanded); }
        catch (EncoderFallbackException ex) { throw new FormatException($"'{ex.CharUnknown}' cannot be sent as a byte; use HEX mode or a <0xNN> token."); }
    }

    /// <summary>
    /// Accepts the forms people actually paste: "01 03 00 6B", "01,03", "0x01 0x03",
    /// "01-03", and unseparated "0103006B". A run of more than two digits must have an
    /// even length, because "001" is far more likely to be a typo than the byte 0x01.
    /// </summary>
    public static byte[] ParseHex(string text)
    {
        var bytes = new List<byte>();
        foreach (var rawToken in text.Split(_hexSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken;
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) token = token[2..];
            else if (token.StartsWith("$", StringComparison.Ordinal)) token = token[1..];
            // A bare prefix with no digits is a typo, not an empty byte list.
            if (token.Length == 0) throw new FormatException($"'{rawToken}' is not a hexadecimal byte.");

            foreach (var c in token)
            {
                if (!Uri.IsHexDigit(c)) throw new FormatException($"'{rawToken}' is not a hexadecimal byte.");
            }
            if (token.Length <= 2)
            {
                bytes.Add(byte.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                continue;
            }
            if (token.Length % 2 != 0) throw new FormatException($"'{rawToken}' has an odd number of hexadecimal digits.");
            for (var i = 0; i < token.Length; i += 2)
                bytes.Add(byte.Parse(token.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        return bytes.ToArray();
    }

    /// <summary>Null when the text would send successfully, otherwise the reason. Used for live validation.</summary>
    public static string? Validate(string text, bool hex)
    {
        try { _ = hex ? ParseHex(text) : ParseText(text); return null; }
        catch (FormatException ex) { return ex.Message; }
    }
}
