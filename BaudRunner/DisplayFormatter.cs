using System.Text;
using Avalonia.Media;

namespace BaudRunner;

public enum DisplayMode { Normal, HexAll, HexExceptCrLf, AsciiOnly, Vt100 }

/// <summary>What to do with ANSI escape sequences in the plain log view.</summary>
public enum AnsiMode { Interpret, Strip, Raw }

public enum TimestampMode { Off, Wall, SinceOpen, Delta }

public enum ReceiveEncoding { Latin1, Utf8 }

public readonly record struct LogSegment(string Text, IBrush? Brush);

/// <summary>
/// Turns received bytes into coloured text segments. All of the stream state that
/// must survive a read boundary lives here - the escape scanner, the SGR colour,
/// the UTF-8 decoder and the at-start-of-line flag - because a device's output is
/// split into reads at arbitrary points, not at line or sequence boundaries.
/// </summary>
public sealed class DisplayFormatter
{
    private const string HexDigits = "0123456789ABCDEF";

    private readonly AnsiScanner _scanner = new();
    private readonly SgrState _sgr = new();
    private readonly StringBuilder _run = new(1024);
    private readonly List<byte> _textBytes = new(1024);
    private char[] _charBuffer = new char[1024];
    private Decoder? _decoder;

    private IBrush? _runBrush;
    private bool _hasRun;
    private bool _atLineStart = true;
    private bool _pendingCr;
    private DateTime _openedAt;
    private DateTime _previousLineAt;
    private bool _havePreviousLine;

    public DisplayMode Mode { get; set; } = DisplayMode.Normal;
    public AnsiMode Ansi { get; set; } = AnsiMode.Interpret;
    public TimestampMode Timestamps { get; set; } = TimestampMode.Off;
    public ReceiveEncoding Encoding { get; set; } = ReceiveEncoding.Latin1;
    public IBrush? HexBrush { get; set; }
    public IBrush? TimestampBrush { get; set; }

    /// <summary>Overridable so tests do not depend on the wall clock.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    public DisplayFormatter() => _openedAt = DateTime.Now;

    /// <summary>
    /// Drops all mid-stream state. Call when the log is cleared or a connection opens.
    /// Clearing the log passes <paramref name="restartClock"/> false: the "since
    /// connect" origin belongs to the connection, not to the display.
    /// </summary>
    public void ResetStream(bool restartClock = true)
    {
        _scanner.Reset(); _sgr.Reset(); _decoder?.Reset();
        _textBytes.Clear(); _run.Clear();
        _hasRun = false; _runBrush = null;
        _atLineStart = true; _pendingCr = false;
        if (restartClock) _openedAt = Clock();
        _havePreviousLine = false;
    }

    public void Format(ReadOnlySpan<byte> bytes, List<LogSegment> output)
    {
        var interpretEscapes = Ansi != AnsiMode.Raw && Mode is DisplayMode.Normal or DisplayMode.AsciiOnly;

        foreach (var value in bytes)
        {
            if (interpretEscapes && _scanner.Feed(value, out var csi))
            {
                if (csi.Final == 'm' && Ansi == AnsiMode.Interpret) { FlushText(output); _sgr.Apply(csi); }
                continue;
            }

            switch (Classify(value))
            {
                case ByteKind.Skip:
                    break;
                case ByteKind.Hex:
                    FlushText(output);
                    EmitHex(value, output);
                    break;
                default:
                    _textBytes.Add(value);
                    break;
            }
        }
        FlushText(output);
        FlushRun(output);
    }

    private enum ByteKind { Text, Hex, Skip }

    private ByteKind Classify(byte value)
    {
        switch (Mode)
        {
            case DisplayMode.HexAll:
                return ByteKind.Hex;
            case DisplayMode.HexExceptCrLf:
                return value is 10 or 13 ? ByteKind.Text : ByteKind.Hex;
            case DisplayMode.AsciiOnly:
                return value is 10 or 13 || value is >= 0x20 and <= 0x7E ? ByteKind.Text : ByteKind.Skip;
            default:
                if (value is 9 or 10 or 13) return ByteKind.Text;
                if (value is >= 0x20 and <= 0x7E) return ByteKind.Text;
                // A UTF-8 lead or continuation byte is text; in Latin-1 mode a byte
                // outside printable ASCII is shown as its hex value, as before.
                if (Encoding == ReceiveEncoding.Utf8 && value >= 0x80) return ByteKind.Text;
                return ByteKind.Hex;
        }
    }

    /// <summary>Decodes the staged text bytes and emits them one character at a time.</summary>
    private void FlushText(List<LogSegment> output)
    {
        if (_textBytes.Count == 0) return;
        var source = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_textBytes);

        if (Encoding == ReceiveEncoding.Utf8)
        {
            _decoder ??= new UTF8Encoding(false, false).GetDecoder();
            var required = _decoder.GetCharCount(source, false);
            if (_charBuffer.Length < required) _charBuffer = new char[Math.Max(required, _charBuffer.Length * 2)];
            var written = _decoder.GetChars(source, _charBuffer, false);
            for (var i = 0; i < written; i++) EmitChar(_charBuffer[i], output);
        }
        else
        {
            foreach (var value in source) EmitChar((char)value, output);
        }
        _textBytes.Clear();
    }

    private void EmitChar(char c, List<LogSegment> output)
    {
        if (c == '\n')
        {
            // Between a CR and its LF the line has not really restarted, so no stamp.
            if (!_pendingCr) StampIfLineStart(output);
            Append(c, TextBrush, output);
            _atLineStart = true; _pendingCr = false;
            return;
        }
        if (c == '\r')
        {
            StampIfLineStart(output);
            Append(c, TextBrush, output);
            _atLineStart = true; _pendingCr = true;
            return;
        }
        StampIfLineStart(output);
        Append(c, TextBrush, output);
        _atLineStart = false; _pendingCr = false;
    }

    private IBrush? TextBrush => Ansi == AnsiMode.Interpret && Mode == DisplayMode.Normal ? _sgr.EffectiveForeground : null;

    private void EmitHex(byte value, List<LogSegment> output)
    {
        StampIfLineStart(output);
        Append('{', HexBrush, output);
        Append(HexDigits[value >> 4], HexBrush, output);
        Append(HexDigits[value & 0xF], HexBrush, output);
        Append('}', HexBrush, output);
        _atLineStart = false; _pendingCr = false;
    }

    private void StampIfLineStart(List<LogSegment> output)
    {
        if (!_atLineStart || Timestamps == TimestampMode.Off) return;
        _atLineStart = false;

        var now = Clock();
        var text = Timestamps switch
        {
            TimestampMode.SinceOpen => $"[{FormatSpan(now - _openedAt)}] ",
            TimestampMode.Delta => $"[+{FormatSpan(_havePreviousLine ? now - _previousLineAt : TimeSpan.Zero)}] ",
            _ => $"[{now:HH:mm:ss.fff}] ",
        };
        _previousLineAt = now; _havePreviousLine = true;
        foreach (var c in text) Append(c, TimestampBrush, output);
    }

    private static string FormatSpan(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalSeconds < 60
            ? span.TotalSeconds.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
            : $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds:000}";
    }

    private void Append(char c, IBrush? brush, List<LogSegment> output)
    {
        if (_hasRun && !ReferenceEquals(brush, _runBrush)) FlushRun(output);
        _runBrush = brush; _hasRun = true;
        _run.Append(c);
    }

    private void FlushRun(List<LogSegment> output)
    {
        if (!_hasRun || _run.Length == 0) { _hasRun = false; _run.Clear(); return; }
        output.Add(new LogSegment(_run.ToString(), _runBrush));
        _run.Clear(); _hasRun = false;
    }
}
