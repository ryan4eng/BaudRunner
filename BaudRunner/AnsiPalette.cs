using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace BaudRunner;

/// <summary>Brushes for SGR colour, including the xterm 256-colour cube and 24-bit colour.</summary>
public static class AnsiPalette
{
    private static readonly IBrush[] _standard = Build("#000000", "#CC5555", "#5FAF5F", "#C8B900", "#5599DD", "#B26FC8", "#3FB8C8", "#C8CDD3");
    private static readonly IBrush[] _bright = Build("#7A7A7A", "#EF5350", "#66BB6A", "#FFEE58", "#42A5F5", "#AB47BC", "#26C6DA", "#FFFFFF");
    private static readonly IBrush?[] _cube = new IBrush?[256];
    private static readonly Dictionary<int, IBrush> _trueColor = new();

    // Immutable: SolidColorBrush is an AvaloniaObject with UI-thread affinity, and this
    // type's static ctor can first run on a worker thread (the test host does exactly that).
    private static IBrush[] Build(params string[] colors) => colors.Select(color => (IBrush)new ImmutableSolidColorBrush(Color.Parse(color))).ToArray();

    public static IBrush Standard(int index) => _standard[index & 7];
    public static IBrush Bright(int index) => _bright[index & 7];

    /// <summary>xterm palette entry: 0-7 standard, 8-15 bright, 16-231 the 6x6x6 cube, 232-255 greyscale.</summary>
    public static IBrush Indexed(int index)
    {
        index &= 0xFF;
        if (index < 8) return _standard[index];
        if (index < 16) return _bright[index - 8];
        if (_cube[index] is { } cached) return cached;

        Color color;
        if (index < 232)
        {
            var n = index - 16;
            color = Color.FromRgb(Level(n / 36 % 6), Level(n / 6 % 6), Level(n % 6));
        }
        else
        {
            var grey = (byte)(8 + (index - 232) * 10);
            color = Color.FromRgb(grey, grey, grey);
        }
        var brush = new ImmutableSolidColorBrush(color);
        _cube[index] = brush;
        return brush;
    }

    private static byte Level(int step) => (byte)(step == 0 ? 0 : 55 + step * 40);

    public static IBrush Rgb(int r, int g, int b)
    {
        var key = (Clamp(r) << 16) | (Clamp(g) << 8) | Clamp(b);
        if (!_trueColor.TryGetValue(key, out var brush))
        {
            // Bounded so a device streaming truecolour cannot grow this without limit.
            if (_trueColor.Count > 4096) _trueColor.Clear();
            brush = new ImmutableSolidColorBrush(Color.FromRgb((byte)(key >> 16), (byte)(key >> 8), (byte)key));
            _trueColor[key] = brush;
        }
        return brush;
    }

    private static int Clamp(int value) => value < 0 ? 0 : value > 255 ? 255 : value;
}

/// <summary>
/// The subset of SGR (Select Graphic Rendition) this application renders: foreground
/// colour, plus bold and inverse because firmware log backends use them for severity.
/// </summary>
public sealed class SgrState
{
    public IBrush? Foreground { get; private set; }
    public IBrush? Background { get; private set; }
    public bool Bold { get; private set; }
    public bool Faint { get; private set; }
    public bool Inverse { get; private set; }

    public void Reset() { Foreground = null; Background = null; Bold = false; Faint = false; Inverse = false; }

    /// <summary>The brush to draw with, or null to use the view's default foreground.</summary>
    public IBrush? EffectiveForeground => Inverse ? Background : Foreground;

    public void Apply(in AnsiCsi csi)
    {
        if (csi.Count == 0) { Reset(); return; }

        // Index-based, not foreach: 38/48 consume their operands, and letting those
        // operands fall through to the plain-colour branches paints a colour derived
        // from a channel value instead of the requested one.
        for (var i = 0; i < csi.Count; i++)
        {
            var code = csi[i];
            switch (code)
            {
                case 0: Reset(); break;
                case 1: Bold = true; break;
                case 2: Faint = true; break;
                case 7: Inverse = true; break;
                case 21: case 22: Bold = false; Faint = false; break;
                case 27: Inverse = false; break;
                case 39: Foreground = null; break;
                case 49: Background = null; break;
                case 38: Foreground = ReadExtended(csi, ref i) ?? Foreground; break;
                case 48: Background = ReadExtended(csi, ref i) ?? Background; break;
                default:
                    if (code is >= 30 and <= 37) Foreground = Bold ? AnsiPalette.Bright(code - 30) : AnsiPalette.Standard(code - 30);
                    else if (code is >= 90 and <= 97) Foreground = AnsiPalette.Bright(code - 90);
                    else if (code is >= 40 and <= 47) Background = AnsiPalette.Standard(code - 40);
                    else if (code is >= 100 and <= 107) Background = AnsiPalette.Bright(code - 100);
                    break;
            }
        }
    }

    private static IBrush? ReadExtended(in AnsiCsi csi, ref int i)
    {
        if (i + 1 >= csi.Count) { i = csi.Count; return null; }
        var kind = csi[++i];
        switch (kind)
        {
            case 5:
                if (i + 1 >= csi.Count) { i = csi.Count; return null; }
                return AnsiPalette.Indexed(csi[++i]);
            case 2:
                if (i + 3 >= csi.Count) { i = csi.Count; return null; }
                var r = csi[++i]; var g = csi[++i]; var b = csi[++i];
                return AnsiPalette.Rgb(r, g, b);
            default:
                // 0 (implementation defined), 1 (transparent), 3/4 (CMY/CMYK): skip
                // the operands we know about so they cannot be re-read as attributes.
                i = Math.Min(csi.Count, i + (kind == 3 ? 3 : kind == 4 ? 4 : 0));
                return null;
        }
    }
}
