using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace BaudRunner;

/// <summary>A compact VT100-compatible terminal surface for interactive serial sessions.</summary>
public sealed class VtTerminalControl : UserControl
{
    // Retained scrollback cap: the whole document is re-shaped on every change, so
    // the retained text must stay bounded to keep appends fast under streaming load.
    private const int MaxChars = 200_000;
    private const int TrimTargetChars = 160_000;
    private const int RunMergeLimit = 8_192;
    private static readonly IBrush[] _standardPalette = new[] { "#000000", "#E57373", "#81C784", "#FFF176", "#64B5F6", "#BA68C8", "#4DD0E1", "#EEEEEE" }.Select(color => (IBrush)new SolidColorBrush(Color.Parse(color))).ToArray();
    private static readonly IBrush[] _brightPalette = new[] { "#757575", "#EF5350", "#66BB6A", "#FFEE58", "#42A5F5", "#AB47BC", "#26C6DA", "#FFFFFF" }.Select(color => (IBrush)new SolidColorBrush(Color.Parse(color))).ToArray();
    private readonly SelectableTextBlock _screen = new() { TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"), FontSize = 13, Padding = new Avalonia.Thickness(8) };
    private readonly StringBuilder _escape = new();
    private readonly StringBuilder _pending = new();
    private bool _inEscape;
    private bool _inCsi;
    private IBrush _defaultForeground = new SolidColorBrush(Color.Parse("#D6E2F0"));
    private IBrush _foreground;
    private int _charCount;

    public event Action<ReadOnlyMemory<byte>>? SendBytes;

    public bool HasSelection => !string.IsNullOrEmpty(_screen.SelectedText);
    public void Copy() => _screen.Copy();
    public void SetContextMenu(ContextMenu menu)
    {
        ContextMenu = menu;
        _screen.ContextMenu = menu;
    }

    public VtTerminalControl()
    {
        _screen.Inlines ??= new InlineCollection();
        _foreground = _defaultForeground;
        Background = new SolidColorBrush(Color.Parse("#0B0E12"));
        Content = _screen;
        Focusable = true;
        AddHandler(KeyDownEvent, OnKeyDown, handledEventsToo: true);
        AddHandler(TextInputEvent, OnTextInput, handledEventsToo: true);
        PointerPressed += (_, _) => Focus();
    }

    public void SetTheme(bool light)
    {
        Background = new SolidColorBrush(Color.Parse(light ? "#FFFFFF" : "#0B0E12"));
        var wasDefault = ReferenceEquals(_foreground, _defaultForeground);
        _defaultForeground = new SolidColorBrush(Color.Parse(light ? "#1F2937" : "#D6E2F0"));
        _screen.Foreground = _defaultForeground;
        if (wasDefault) _foreground = _defaultForeground;
    }

    public void ProcessBytes(ReadOnlySpan<byte> bytes)
    {
        // Printable text accumulates in _pending and is flushed as one Run append
        // per colour change / control action, instead of one append per byte.
        foreach (var value in bytes)
        {
            var ch = (char)value;
            if (_inEscape)
            {
                if (!_inCsi && ch == '[') { _inCsi = true; _escape.Clear(); continue; }
                if (_inCsi && (ch is >= '0' and <= '9' || ch == ';' || ch == '?')) { _escape.Append(ch); continue; }
                if (_inCsi) { HandleCsi(_escape.ToString(), ch); _inEscape = false; _inCsi = false; _escape.Clear(); continue; }
                _inEscape = false; continue;
            }
            if (value == 0x1B) { FlushPending(); _inEscape = true; _inCsi = false; continue; }
            if (value == 0x08) { if (_pending.Length > 0) _pending.Length--; else RemoveLastCharacter(); continue; }
            if (value == 0x0D) { _pending.Append('\r'); continue; }
            if (value == 0x0A) { _pending.Append('\n'); continue; }
            if (value >= 0x20 || value == 0x09) _pending.Append(ch);
        }
        FlushPending();
    }

    public void Clear() { _screen.Inlines?.Clear(); _pending.Clear(); _charCount = 0; }

    private void FlushPending()
    {
        if (_pending.Length == 0) return;
        AppendText(_pending.ToString());
        _pending.Clear();
    }

    private void HandleCsi(string parameters, char command)
    {
        var values = parameters.TrimStart('?').Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        if (command == 'm')
        {
            foreach (var value in values.Length == 0 ? new[] { 0 } : values)
            {
                if (value == 0 || value == 39) _foreground = _defaultForeground;
                else if (value is >= 30 and <= 37) _foreground = _standardPalette[value - 30];
                else if (value is >= 90 and <= 97) _foreground = _brightPalette[value - 90];
            }
        }
        // ED's omitted parameter means 0 (erase from the cursor to the end
        // of the display), not 2 (erase the entire display). We do not yet
        // model a cursor/grid, so leave partial erases alone rather than
        // destroying the complete scrollback. Only an explicit ESC[2J is a
        // safe full-screen clear.
        else if (command == 'J' && values.Length > 0 && values[0] == 2) Clear();
        else if (command == 'K' && (values.Length == 0 || values[0] == 2)) RemoveLastLine();
    }

    private void AppendText(string value)
    {
        if (_screen.Inlines is not { } inlines) return;

        // Keep contiguous text in one Run (up to a limit so merge copies stay
        // cheap and front trimming stays granular). Adding one Inline per byte
        // makes every edit re-layout the entire terminal.
        if (inlines.Count > 0 && inlines[^1] is Run last && Equals(last.Foreground, _foreground) && (last.Text?.Length ?? 0) < RunMergeLimit)
            last.Text += value;
        else
            inlines.Add(new Run(value) { Foreground = _foreground });
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

    private void RemoveLastCharacter()
    {
        if (_screen.Inlines is not { Count: > 0 } inlines) return;

        // Remove empty runs left by an earlier backspace so the inline tree
        // stays compact and Avalonia has less work to do on each edit.
        while (inlines.Count > 0 && inlines[^1] is Run empty && string.IsNullOrEmpty(empty.Text))
            inlines.RemoveAt(inlines.Count - 1);

        if (inlines.Count == 0 || inlines[^1] is not Run run || string.IsNullOrEmpty(run.Text)) return;
        run.Text = run.Text[..^1];
        _charCount = Math.Max(0, _charCount - 1);
    }
    private void RemoveLastLine()
    {
        // ESC[K erases the current display line, which can span colour runs and be
        // only part of a run — walk backwards to the last newline, not run edges.
        FlushPending();
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
    private void OnTextInput(object? sender, TextInputEventArgs e) { if (!string.IsNullOrEmpty(e.Text)) SendBytes?.Invoke(Encoding.ASCII.GetBytes(e.Text)); }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var bytes = e.Key switch { Key.Enter => new byte[] { 13 }, Key.Back => new byte[] { 8 }, Key.Tab => new byte[] { 9 }, Key.Escape => new byte[] { 27 }, Key.Up => Encoding.ASCII.GetBytes("\x1B[A"), Key.Down => Encoding.ASCII.GetBytes("\x1B[B"), Key.Right => Encoding.ASCII.GetBytes("\x1B[C"), Key.Left => Encoding.ASCII.GetBytes("\x1B[D"), _ => Array.Empty<byte>() };
        if (bytes.Length > 0) { SendBytes?.Invoke(bytes); e.Handled = true; }
    }
}
