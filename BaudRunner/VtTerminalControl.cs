using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace BaudRunner;

/// <summary>A compact VT100-compatible terminal surface for interactive serial sessions.</summary>
public sealed class VtTerminalControl : UserControl
{
    private readonly SelectableTextBlock _screen = new() { TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"), FontSize = 13, Padding = new Avalonia.Thickness(8) };
    private readonly StringBuilder _escape = new();
    private bool _inEscape;
    private bool _inCsi;
    private IBrush _foreground = new SolidColorBrush(Color.Parse("#D6E2F0"));

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
        _screen.Foreground = new SolidColorBrush(Color.Parse(light ? "#1F2937" : "#D6E2F0"));
    }

    public void ProcessBytes(ReadOnlySpan<byte> bytes)
    {
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
            if (value == 0x1B) { _inEscape = true; _inCsi = false; continue; }
            if (value == 0x08) { RemoveLastCharacter(); continue; }
            if (value == 0x0D) { AppendText("\r"); continue; }
            if (value == 0x0A) { AppendText("\n"); continue; }
            if (value >= 0x20 || value == 0x09) AppendText(ch.ToString());
        }
    }

    public void Clear() => _screen.Inlines?.Clear();

    private void HandleCsi(string parameters, char command)
    {
        var values = parameters.TrimStart('?').Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        if (command == 'm')
        {
            foreach (var value in values.Length == 0 ? new[] { 0 } : values)
            {
                if (value == 0 || value == 39) _foreground = new SolidColorBrush(Color.Parse("#D6E2F0"));
                else if (value is >= 30 and <= 37) _foreground = new SolidColorBrush(Color.Parse(new[] { "#000000", "#E57373", "#81C784", "#FFF176", "#64B5F6", "#BA68C8", "#4DD0E1", "#EEEEEE" }[value - 30]));
                else if (value is >= 90 and <= 97) _foreground = new SolidColorBrush(Color.Parse(new[] { "#757575", "#EF5350", "#66BB6A", "#FFEE58", "#42A5F5", "#AB47BC", "#26C6DA", "#FFFFFF" }[value - 90]));
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

        // Keep contiguous text in one Run. Adding one Inline per byte makes
        // every edit (notably backspace) re-layout the entire terminal.
        if (inlines.Count > 0 && inlines[^1] is Run last && Equals(last.Foreground, _foreground))
        {
            last.Text += value;
            return;
        }

        inlines.Add(new Run(value) { Foreground = _foreground });
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
    }
    private void RemoveLastLine() { if (_screen.Inlines is { Count: > 0 } inlines) inlines.RemoveAt(inlines.Count - 1); }
    private void OnTextInput(object? sender, TextInputEventArgs e) { if (!string.IsNullOrEmpty(e.Text)) SendBytes?.Invoke(Encoding.ASCII.GetBytes(e.Text)); }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var bytes = e.Key switch { Key.Enter => new byte[] { 13 }, Key.Back => new byte[] { 8 }, Key.Tab => new byte[] { 9 }, Key.Escape => new byte[] { 27 }, Key.Up => Encoding.ASCII.GetBytes("\x1B[A"), Key.Down => Encoding.ASCII.GetBytes("\x1B[B"), Key.Right => Encoding.ASCII.GetBytes("\x1B[C"), Key.Left => Encoding.ASCII.GetBytes("\x1B[D"), _ => Array.Empty<byte>() };
        if (bytes.Length > 0) { SendBytes?.Invoke(bytes); e.Handled = true; }
    }
}
