using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BaudRunner;

public sealed class CommandSlot
{
    public string Text { get; set; } = "";
    public bool Hex { get; set; }
    public bool AppendLineFeed { get; set; }

    public byte[] ToBytes()
    {
        byte[] result;
        if (Hex)
        {
            var tokens = Text.Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            result = tokens.Select(t => byte.Parse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        }
        else
        {
            // Keep control characters readable in saved command text while
            // still sending their actual byte values. Tokens are case-insensitive.
            var expanded = Regex.Replace(Text, @"<(CR|LF)>", match => match.Groups[1].Value.Equals("CR", StringComparison.OrdinalIgnoreCase) ? "\r" : "\n", RegexOptions.IgnoreCase);
            result = Encoding.ASCII.GetBytes(expanded);
        }
        return AppendLineFeed ? result.Concat(new byte[] { 0x0A }).ToArray() : result;
    }
}
