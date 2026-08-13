using System.Globalization;
using System.Text;

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
        else result = Encoding.ASCII.GetBytes(Text);
        return AppendLineFeed ? result.Concat(new byte[] { 0x0A }).ToArray() : result;
    }
}
