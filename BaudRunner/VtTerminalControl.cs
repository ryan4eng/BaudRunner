using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace BaudRunner;

/// <summary>
/// A compact VT100-compatible terminal surface for interactive serial sessions.
/// It is a run list, not a cell grid: colour, CR overwrite, backspace and erase-line
/// are modelled, cursor addressing is not. Sequences that are not modelled are
/// consumed whole so they never leak into the visible text.
/// </summary>
public sealed class VtTerminalControl : UserControl
{
    // Retained scrollback cap: the whole document is re-shaped on every change, so
    // the retained text must stay bounded to keep appends fast under streaming load.
    private const int MaxChars = 60_000;
    private const int TrimTargetChars = 48_000;
    private const int RunMergeLimit = 1_024;
    private const int MaxPausedBytes = 4 * 1024 * 1024;

    private readonly SelectableTextBlock _screen = new() { TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"), FontSize = 13, Padding = new Avalonia.Thickness(8) };
    private readonly AnsiScanner _scanner = new();
    private readonly SgrState _sgr = new();
    private readonly StringBuilder _pending = new();
    private readonly List<byte> _textBytes = new();
    private readonly List<byte[]> _pausedChunks = new();
    private readonly SolidColorBrush _defaultForeground = new(Color.Parse("#D6E2F0"));
    private readonly SolidColorBrush _backgroundBrush = new(Color.Parse("#0B0E12"));
    private Decoder? _decoder;
    private char[] _charBuffer = new char[512];
    private bool _pendingCr;
    private int _charCount;
    private int _pausedBytes;

    public event Action<ReadOnlyMemory<byte>>? SendBytes;

    /// <summary>Raised with each chunk of text added to the screen, so the file log records what was displayed.</summary>
    public event Action<string>? TextAppended;

    /// <summary>Bytes sent for the Enter key. CR is what a shell or AT device expects.</summary>
    public LineEnding EnterEnding { get; set; } = LineEnding.Cr;

    public ReceiveEncoding Encoding { get; set; } = ReceiveEncoding.Latin1;

    public bool HasSelection => !string.IsNullOrEmpty(_screen.SelectedText);
    public string SelectedText => _screen.SelectedText ?? "";
    public string AllText => string.Concat((_screen.Inlines ?? new InlineCollection()).OfType<Run>().Select(run => run.Text));
    public void Copy() => _screen.Copy();

    public void SetContextMenu(ContextMenu menu)
    {
        ContextMenu = menu;
        _screen.ContextMenu = menu;
    }

    public VtTerminalControl()
    {
        _screen.Inlines ??= new InlineCollection();
        _screen.Foreground = _defaultForeground;
        Background = _backgroundBrush;
        Content = _screen;
        Focusable = true;
        // Tunnelling, so the key is translated before the SelectableTextBlock gets
        // it: otherwise Ctrl+A both went to the device as 0x01 and highlighted the
        // whole screen, and Ctrl+C copied as well as sending 0x03.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnTextInput, handledEventsToo: true);
        PointerPressed += (_, _) => Focus();
    }

    /// <summary>
    /// While paused the byte stream is retained and replayed on resume, so the parser
    /// state and the colours stay consistent rather than resuming mid-sequence.
    /// </summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            if (!_paused) ReplayPaused();
        }
    }
    private bool _paused;

    public void SetTheme(bool light)
    {
        // Mutating the shared brush instead of replacing it repaints every Run already
        // in the document; assigning a new brush left all existing text on the old one.
        _backgroundBrush.Color = Color.Parse(light ? "#FFFFFF" : "#0B0E12");
        _defaultForeground.Color = Color.Parse(light ? "#1F2937" : "#D6E2F0");
    }

    public void ProcessBytes(ReadOnlySpan<byte> bytes)
    {
        if (_paused)
        {
            if (_pausedBytes < MaxPausedBytes)
            {
                _pausedChunks.Add(bytes.ToArray());
                _pausedBytes += bytes.Length;
            }
            return;
        }
        Consume(bytes);
    }

    private void ReplayPaused()
    {
        var chunks = _pausedChunks.ToArray();
        _pausedChunks.Clear();
        _pausedBytes = 0;
        foreach (var chunk in chunks) Consume(chunk);
    }

    private void Consume(ReadOnlySpan<byte> bytes)
    {
        // Printable text accumulates and is flushed as one Run append per colour
        // change or control action, instead of one append per byte.
        foreach (var value in bytes)
        {
            if (_pendingCr)
            {
                _pendingCr = false;
                // CR then LF is an ordinary line break; a bare CR means "back to
                // column 0", which for a run list is: erase the line being drawn.
                if (value == 0x0A) { _pending.Append('\n'); continue; }
                FlushText(); FlushPending(); EraseCurrentLine();
            }

            if (_scanner.Feed(value, out var csi))
            {
                if (csi.Final != '\0') HandleCsi(csi);
                continue;
            }

            switch (value)
            {
                case 0x08:
                    Backspace();
                    break;
                case 0x0D:
                    FlushText();
                    _pendingCr = true;
                    break;
                case 0x0A:
                    FlushText();
                    _pending.Append('\n');
                    break;
                case 0x09:
                    _textBytes.Add(value);
                    break;
                default:
                    if (value >= 0x20 && value != 0x7F) _textBytes.Add(value);
                    break;
            }
        }
        FlushText();
        FlushPending();
    }

    /// <summary>A user clear: everything goes, including buffered paused input and the parser state.</summary>
    public void Clear()
    {
        ClearScreen();
        _pausedChunks.Clear();
        _pausedBytes = 0;
        _scanner.Reset();
        _sgr.Reset();
        _decoder?.Reset();
    }

    /// <summary>
    /// ESC[2J from the device: the text goes, the attributes stay. Resetting the SGR
    /// state here too made "set red, clear screen, print" come out in the default
    /// colour, which is not what a real terminal does.
    /// </summary>
    private void ClearScreen()
    {
        _screen.Inlines?.Clear();
        _pending.Clear();
        _textBytes.Clear();
        _charCount = 0;
        _pendingCr = false;
    }

    private void FlushText()
    {
        if (_textBytes.Count == 0) return;
        var source = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_textBytes);
        if (Encoding == ReceiveEncoding.Utf8)
        {
            // A Decoder is stateful, so a character split across two reads still
            // arrives as one character rather than two replacement glyphs.
            _decoder ??= new UTF8Encoding(false, false).GetDecoder();
            var required = _decoder.GetCharCount(source, false);
            if (_charBuffer.Length < required) _charBuffer = new char[Math.Max(required, _charBuffer.Length * 2)];
            var written = _decoder.GetChars(source, _charBuffer, false);
            _pending.Append(_charBuffer, 0, written);
        }
        else
        {
            foreach (var value in source) _pending.Append((char)value);
        }
        _textBytes.Clear();
    }

    private void FlushPending()
    {
        if (_pending.Length == 0) return;
        AppendText(_pending.ToString());
        _pending.Clear();
    }

    private void HandleCsi(in AnsiCsi csi)
    {
        switch (csi.Final)
        {
            case 'm':
                FlushText(); FlushPending();
                _sgr.Apply(csi);
                break;
            case 'J':
                // ED's omitted parameter means 0 (erase from the cursor to the end of
                // the display), not 2. Without a cursor/grid model a partial erase
                // cannot be honoured, so only an explicit ESC[2J clears.
                if (csi.HasParameter(0) && csi[0] == 2) ClearScreen();
                break;
            case 'K':
                // Likewise for EL: an omitted parameter is 0 (erase to end of line),
                // which must not destroy text already written.
                if (csi.HasParameter(0) && csi[0] == 2) { FlushText(); FlushPending(); EraseCurrentLine(); }
                break;
        }
    }

    private IBrush CurrentForeground => _sgr.EffectiveForeground ?? _defaultForeground;

    private void AppendText(string value)
    {
        if (_screen.Inlines is not { } inlines) return;
        var foreground = CurrentForeground;

        // Keep contiguous text in one Run (up to a limit so merge copies stay
        // cheap and front trimming stays granular). Adding one Inline per byte
        // makes every edit re-layout the entire terminal.
        if (inlines.Count > 0 && inlines[^1] is Run last && ReferenceEquals(last.Foreground, foreground) && (last.Text?.Length ?? 0) < RunMergeLimit)
            last.Text += value;
        else
            inlines.Add(new Run(value) { Foreground = foreground });
        _charCount += value.Length;
        TextAppended?.Invoke(value);
        TrimScrollback();
    }

    /// <summary>
    /// Text from the application rather than the device: open errors, a lost
    /// connection, a reconnect. It bypasses the parser, so nothing in it is
    /// interpreted, and it is not reported through <see cref="TextAppended"/>
    /// because the caller already writes it to the file log.
    /// </summary>
    public void AppendNotice(string text, IBrush? brush)
    {
        if (_screen.Inlines is not { } inlines) return;
        FlushText(); FlushPending();
        _pendingCr = false;
        var value = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (value.Length == 0) return;
        inlines.Add(new Run(value) { Foreground = brush ?? _defaultForeground });
        _charCount += value.Length;
        TrimScrollback();
    }

    private void TrimScrollback()
    {
        if (_charCount <= MaxChars || _screen.Inlines is not { } inlines) return;
        var excess = _charCount - TrimTargetChars;
        var removed = 0;
        while (removed < excess && inlines.Count > 0)
        {
            if (inlines[0] is not Run run || run.Text is not { Length: > 0 } runText) { inlines.RemoveAt(0); continue; }
            if (runText.Length <= excess - removed) { inlines.RemoveAt(0); removed += runText.Length; }
            else { run.Text = runText[(excess - removed)..]; removed = excess; }
        }
        _charCount -= removed;

        // Keep an active selection anchored to the same text as old content
        // scrolls out of the retained buffer.
        if (removed > 0 && (_screen.SelectionStart != 0 || _screen.SelectionEnd != 0))
        {
            _screen.SelectionStart = Math.Max(0, _screen.SelectionStart - removed);
            _screen.SelectionEnd = Math.Max(0, _screen.SelectionEnd - removed);
        }
    }

    private void Backspace()
    {
        // BS at column 0 is a no-op on a real terminal; deleting the newline would
        // join this line onto the previous one.
        if (_textBytes.Count > 0) { _textBytes.RemoveAt(_textBytes.Count - 1); return; }
        if (_pending.Length > 0)
        {
            if (_pending[^1] is '\n' or '\r') return;
            _pending.Length--;
            return;
        }
        RemoveLastCharacter();
    }

    private void RemoveLastCharacter()
    {
        if (_screen.Inlines is not { Count: > 0 } inlines) return;

        // Remove empty runs left by an earlier backspace so the inline tree
        // stays compact and Avalonia has less work to do on each edit.
        while (inlines.Count > 0 && inlines[^1] is Run empty && string.IsNullOrEmpty(empty.Text))
            inlines.RemoveAt(inlines.Count - 1);

        if (inlines.Count == 0 || inlines[^1] is not Run run || string.IsNullOrEmpty(run.Text)) return;
        if (run.Text[^1] is '\n' or '\r') return;
        run.Text = run.Text[..^1];
        _charCount = Math.Max(0, _charCount - 1);
    }

    /// <summary>
    /// Erases the display line currently being written. It can span colour runs and
    /// be only part of a run, so this walks back to the last newline, not to a run edge.
    /// </summary>
    private void EraseCurrentLine()
    {
        if (_screen.Inlines is not { Count: > 0 } inlines) return;
        while (inlines.Count > 0)
        {
            if (inlines[^1] is not Run run || string.IsNullOrEmpty(run.Text)) { inlines.RemoveAt(inlines.Count - 1); continue; }
            var text = run.Text!;
            var lastBreak = text.LastIndexOf('\n');
            if (lastBreak >= 0)
            {
                var removeCount = text.Length - (lastBreak + 1);
                if (removeCount > 0) { run.Text = text[..(lastBreak + 1)]; _charCount = Math.Max(0, _charCount - removeCount); }
                return;
            }
            _charCount = Math.Max(0, _charCount - text.Length);
            inlines.RemoveAt(inlines.Count - 1);
        }
    }

    /* ---------------- keyboard ---------------- */

    /// <summary>
    /// The gestures a focused terminal claims for the device - Escape and
    /// Ctrl+letter - so that application shortcuts on the same keys can stand aside.
    /// Ctrl+Shift combinations, Ctrl+End and Ctrl+digit are not claimed.
    /// </summary>
    public static bool ConsumesKey(Key key, KeyModifiers modifiers)
    {
        if (modifiers == KeyModifiers.None) return key == Key.Escape;
        return modifiers == KeyModifiers.Control && key is >= Key.A and <= Key.Z;
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        Send(EncodeOutgoing(e.Text));
    }

    private byte[] EncodeOutgoing(string text)
    {
        if (Encoding == ReceiveEncoding.Utf8) return System.Text.Encoding.UTF8.GetBytes(text);
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++) bytes[i] = text[i] <= 0xFF ? (byte)text[i] : (byte)'?';
        return bytes;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Copy has to keep a gesture: Ctrl+C itself must reach the device as 0x03.
        if (ctrl && shift && e.Key == Key.C) { Copy(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.A) { _screen.SelectAll(); e.Handled = true; return; }

        var bytes = Translate(e.Key, ctrl);
        if (bytes.Length == 0) return;
        Send(bytes);
        e.Handled = true;
    }

    private byte[] Translate(Key key, bool ctrl)
    {
        if (ctrl)
        {
            // The platform filters characters below 0x20 out of TextInput, so without
            // this there is no path at all for Ctrl+C to reach a running program.
            if (key is >= Key.A and <= Key.Z) return new[] { (byte)(key - Key.A + 1) };
            switch (key)
            {
                case Key.Space: return new byte[] { 0x00 };
                case Key.OemOpenBrackets: return new byte[] { 0x1B };
                case Key.OemBackslash: case Key.OemPipe: return new byte[] { 0x1C };
                case Key.OemCloseBrackets: return new byte[] { 0x1D };
            }
            return Array.Empty<byte>();
        }

        switch (key)
        {
            case Key.Enter: return CommandSlot.EndingBytes(EnterEnding) is { Length: > 0 } ending ? ending : new byte[] { 0x0D };
            case Key.Back: return new byte[] { 0x08 };
            case Key.Tab: return new byte[] { 0x09 };
            case Key.Escape: return new byte[] { 0x1B };
            case Key.Up: return Csi("A");
            case Key.Down: return Csi("B");
            case Key.Right: return Csi("C");
            case Key.Left: return Csi("D");
            case Key.Home: return Csi("H");
            case Key.End: return Csi("F");
            case Key.Insert: return Csi("2~");
            case Key.Delete: return Csi("3~");
            case Key.PageUp: return Csi("5~");
            case Key.PageDown: return Csi("6~");
            case Key.F1: return Ss3("P");
            case Key.F2: return Ss3("Q");
            case Key.F3: return Ss3("R");
            case Key.F4: return Ss3("S");
            case Key.F5: return Csi("15~");
            case Key.F6: return Csi("17~");
            case Key.F7: return Csi("18~");
            case Key.F8: return Csi("19~");
            case Key.F9: return Csi("20~");
            case Key.F10: return Csi("21~");
            case Key.F11: return Csi("23~");
            case Key.F12: return Csi("24~");
        }
        return Array.Empty<byte>();
    }

    private static byte[] Csi(string tail) => System.Text.Encoding.ASCII.GetBytes("\x1B[" + tail);
    private static byte[] Ss3(string tail) => System.Text.Encoding.ASCII.GetBytes("\x1BO" + tail);

    private void Send(byte[] bytes)
    {
        if (bytes.Length > 0) SendBytes?.Invoke(bytes);
    }
}
