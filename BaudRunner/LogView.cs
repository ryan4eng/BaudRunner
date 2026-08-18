using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;

namespace BaudRunner;

/// <summary>
/// A virtualized colour-capable log surface. The log is kept as a plain data model
/// (lines of text with colour spans) and only the visible lines are shaped and
/// drawn, so append cost is independent of scrollback size and render cost is
/// bounded by the viewport. Supports mouse selection and copy; selection is
/// tracked by absolute line ids so it stays anchored while old lines are trimmed.
/// </summary>
public sealed class LogView : Control, ILogicalScrollable
{
    private const double FontSizePx = 13;
    private const double Pad = 8;
    private const int MaxLines = 5000;
    private const int TrimChunk = 500;
    private const int MaxLineChars = 1024;
    private const int LayoutCacheLimit = 240;
    private const int LayoutCacheKeepMargin = 40;

    private sealed class Line
    {
        public readonly StringBuilder Text = new();
        public readonly List<(int Start, IBrush? Brush)> Spans = new();
        public int Revision;
    }

    private readonly List<Line> _lines = new() { new Line() };
    private long _firstLineId;
    private readonly Dictionary<long, (int Revision, TextLayout Layout)> _layoutCache = new();
    private readonly Dictionary<IBrush, GenericTextRunProperties> _runProperties = new();
    private readonly Typeface _typeface = new(new FontFamily("Cascadia Mono,Consolas,monospace"));
    private readonly IBrush _selectionBrush = new SolidColorBrush(Color.FromArgb(90, 59, 130, 246));
    private IBrush _background = new SolidColorBrush(Color.Parse("#0B0E12"));
    private IBrush _defaultForeground = new SolidColorBrush(Color.Parse("#D6E2F0"));
    private double _lineHeight;
    private double _charWidth;
    private int _maxObservedLineLength;
    private double _maxObservedWidthPx;
    private bool _lastCharWasCr;
    private bool _extentUpdateQueued;

    private Size _extent;
    private Vector _offset;
    private Size _viewport;
    private EventHandler? _scrollInvalidated;

    private (long Line, int Col)? _selAnchor;
    private (long Line, int Col)? _selCaret;
    private bool _selecting;

    public LogView()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public void SetTheme(bool light)
    {
        _background = new SolidColorBrush(Color.Parse(light ? "#FFFFFF" : "#0B0E12"));
        _defaultForeground = new SolidColorBrush(Color.Parse(light ? "#1F2937" : "#D6E2F0"));

        // Cached layouts and run properties bake in the default foreground brush.
        _runProperties.Clear();
        ClearLayoutCache();
        InvalidateVisual();
    }

    public void Append(string text, IBrush? brush)
    {
        AppendCore(text, brush);
        FinishAppend();
    }

    public void AppendSegments(IReadOnlyList<(string Text, IBrush? Brush)> segments)
    {
        foreach (var (text, brush) in segments) AppendCore(text, brush);
        FinishAppend();
    }

    public void Clear()
    {
        _lines.Clear();
        _lines.Add(new Line());
        _firstLineId = 0;
        _selAnchor = _selCaret = null;
        _lastCharWasCr = false;
        _maxObservedLineLength = 0;
        _maxObservedWidthPx = 0;
        ClearLayoutCache();
        UpdateExtent();
        InvalidateVisual();
    }

    public bool HasSelection
    {
        get
        {
            var selection = NormalizedSelection();
            return selection is { } s && (s.Start.Line != s.End.Line || s.Start.Col != s.End.Col);
        }
    }

    public string SelectedText
    {
        get
        {
            if (NormalizedSelection() is not { } s || (s.Start.Line == s.End.Line && s.Start.Col == s.End.Col)) return "";
            var sb = new StringBuilder();
            for (var id = s.Start.Line; id <= s.End.Line; id++)
            {
                var index = (int)(id - _firstLineId);
                if (index < 0 || index >= _lines.Count) continue;
                var line = _lines[index];
                var from = id == s.Start.Line ? Math.Min(s.Start.Col, line.Text.Length) : 0;
                var to = id == s.End.Line ? Math.Min(s.End.Col, line.Text.Length) : line.Text.Length;
                if (id > s.Start.Line) sb.Append(Environment.NewLine);
                if (to > from) sb.Append(line.Text.ToString(from, to - from));
            }
            return sb.ToString();
        }
    }

    public void Copy()
    {
        var text = SelectedText;
        if (text.Length > 0) _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    public void SelectAll()
    {
        _selAnchor = (_firstLineId, 0);
        _selCaret = (_firstLineId + _lines.Count - 1, _lines[^1].Text.Length);
        InvalidateVisual();
    }

    /* ---------------- append model ---------------- */

    private void AppendCore(string text, IBrush? brush)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\n')
            {
                // \r\n counts as the single break already taken for the \r.
                if (!_lastCharWasCr) NewLine();
                _lastCharWasCr = false;
                i++;
                continue;
            }
            if (c == '\r')
            {
                NewLine();
                _lastCharWasCr = true;
                i++;
                continue;
            }
            _lastCharWasCr = false;
            var start = i;
            while (i < text.Length && text[i] != '\r' && text[i] != '\n') i++;
            AppendToLastLine(text.AsSpan(start, i - start), brush);
        }
    }

    private void AppendToLastLine(ReadOnlySpan<char> text, IBrush? brush)
    {
        while (text.Length > 0)
        {
            var line = _lines[^1];
            var room = MaxLineChars - line.Text.Length;
            if (room <= 0) { NewLine(); continue; }
            var take = Math.Min(room, text.Length);
            if (line.Spans.Count == 0 || !ReferenceEquals(line.Spans[^1].Brush, brush)) line.Spans.Add((line.Text.Length, brush));
            line.Text.Append(text[..take]);
            line.Revision++;
            if (line.Text.Length > _maxObservedLineLength) _maxObservedLineLength = line.Text.Length;
            text = text[take..];
        }
    }

    private void NewLine()
    {
        _lines.Add(new Line());
        if (_lines.Count <= MaxLines + TrimChunk) return;

        var remove = _lines.Count - MaxLines;
        _lines.RemoveRange(0, remove);
        _firstLineId += remove;

        // Keep the viewport and any selection anchored to the same content as
        // old lines scroll out of the retained buffer.
        if (_offset.Y > 0) _offset = new Vector(_offset.X, Math.Max(0, _offset.Y - remove * _lineHeight));
        if (_selAnchor is { } anchor && anchor.Line < _firstLineId) _selAnchor = (_firstLineId, 0);
        if (_selCaret is { } caret && caret.Line < _firstLineId) _selCaret = (_firstLineId, 0);
        if (_selAnchor is { } a && _selCaret is { } b && a.Line == b.Line && a.Col == b.Col && a.Line <= _firstLineId) { _selAnchor = _selCaret = null; }
    }

    private void FinishAppend()
    {
        UpdateExtent();
        InvalidateVisual();
    }

    /* ---------------- layout & rendering ---------------- */

    private void EnsureMetrics()
    {
        if (_lineHeight > 0) return;
        var sample = new TextLayout("MMMMMMMMMM", _typeface, FontSizePx, _defaultForeground);
        _lineHeight = Math.Ceiling(sample.Height);
        _charWidth = sample.WidthIncludingTrailingWhitespace / 10;
    }

    private TextLayout GetLayout(int index)
    {
        var id = _firstLineId + index;
        var line = _lines[index];
        if (_layoutCache.TryGetValue(id, out var cached) && cached.Revision == line.Revision) return cached.Layout;

        IReadOnlyList<ValueSpan<TextRunProperties>>? overrides = null;
        if (line.Spans.Any(span => span.Brush is not null))
        {
            var list = new List<ValueSpan<TextRunProperties>>(line.Spans.Count);
            for (var s = 0; s < line.Spans.Count; s++)
            {
                var (start, brush) = line.Spans[s];
                if (brush is null) continue;
                var end = s + 1 < line.Spans.Count ? line.Spans[s + 1].Start : line.Text.Length;
                if (end > start) list.Add(new ValueSpan<TextRunProperties>(start, end - start, GetRunProperties(brush)));
            }
            overrides = list;
        }
        var layout = new TextLayout(line.Text.ToString(), _typeface, FontSizePx, _defaultForeground, textWrapping: TextWrapping.NoWrap, textStyleOverrides: overrides);
        _layoutCache[id] = (line.Revision, layout);
        if (layout.WidthIncludingTrailingWhitespace > _maxObservedWidthPx)
        {
            // GetLayout runs during Render; raising scroll invalidation here would
            // invalidate arrange mid-render-pass, which Avalonia treats as fatal.
            _maxObservedWidthPx = layout.WidthIncludingTrailingWhitespace;
            ScheduleExtentUpdate();
        }
        return layout;
    }

    private GenericTextRunProperties GetRunProperties(IBrush brush)
    {
        if (!_runProperties.TryGetValue(brush, out var properties))
        {
            properties = new GenericTextRunProperties(_typeface, fontRenderingEmSize: FontSizePx, foregroundBrush: brush);
            _runProperties[brush] = properties;
        }
        return properties;
    }

    private void ClearLayoutCache()
    {
        _layoutCache.Clear();
    }

    private void ScheduleExtentUpdate()
    {
        if (_extentUpdateQueued) return;
        _extentUpdateQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { _extentUpdateQueued = false; UpdateExtent(); });
    }

    private void EvictLayoutCache(long firstVisibleId, long lastVisibleId)
    {
        if (_layoutCache.Count <= LayoutCacheLimit) return;
        var stale = _layoutCache.Keys.Where(id => id < firstVisibleId - LayoutCacheKeepMargin || id > lastVisibleId + LayoutCacheKeepMargin).ToList();
        foreach (var id in stale) _layoutCache.Remove(id);
    }

    public override void Render(DrawingContext context)
    {
        EnsureMetrics();
        context.FillRectangle(_background, new Rect(Bounds.Size));

        var top = _offset.Y;
        var first = Math.Max(0, (int)((top - Pad) / _lineHeight));
        var last = Math.Min(_lines.Count - 1, (int)((top + Bounds.Height - Pad) / _lineHeight) + 1);
        if (first > last) return;
        var selection = NormalizedSelection();

        for (var i = first; i <= last; i++)
        {
            var layout = GetLayout(i);
            var origin = new Point(Pad - _offset.X, Pad + i * _lineHeight - top);
            if (selection is { } s)
            {
                var id = _firstLineId + i;
                if (id >= s.Start.Line && id <= s.End.Line)
                {
                    var lineLength = _lines[i].Text.Length;
                    var from = id == s.Start.Line ? Math.Min(s.Start.Col, lineLength) : 0;
                    var to = id == s.End.Line ? Math.Min(s.End.Col, lineLength) : lineLength;
                    if (to > from)
                    {
                        foreach (var rect in layout.HitTestTextRange(from, to - from))
                            context.FillRectangle(_selectionBrush, new Rect(rect.X + origin.X, origin.Y, rect.Width, _lineHeight));
                    }
                    else if (id < s.End.Line)
                    {
                        // Fully-selected empty line: show a sliver so the user sees it.
                        context.FillRectangle(_selectionBrush, new Rect(origin.X, origin.Y, _charWidth * 0.6, _lineHeight));
                    }
                }
            }
            layout.Draw(context, origin);
        }
        EvictLayoutCache(_firstLineId + first, _firstLineId + last);
    }

    /* ---------------- selection input ---------------- */

    private ((long Line, int Col) Start, (long Line, int Col) End)? NormalizedSelection()
    {
        if (_selAnchor is not { } a || _selCaret is not { } b) return null;
        return a.Line < b.Line || (a.Line == b.Line && a.Col <= b.Col) ? (a, b) : (b, a);
    }

    private (long Line, int Col) HitTestPosition(Point point)
    {
        EnsureMetrics();
        var index = Math.Clamp((int)((point.Y + _offset.Y - Pad) / _lineHeight), 0, _lines.Count - 1);
        var layout = GetLayout(index);
        var hit = layout.HitTestPoint(new Point(point.X + _offset.X - Pad, 0));
        var col = Math.Clamp(hit.TextPosition + (hit.IsTrailing ? 1 : 0), 0, _lines[index].Text.Length);
        return (_firstLineId + index, col);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        _selAnchor = _selCaret = HitTestPosition(e.GetPosition(this));
        _selecting = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting) return;
        var position = e.GetPosition(this);

        // Nudge the viewport when dragging past an edge so selections can extend
        // beyond the visible region.
        var dy = position.Y < 0 ? position.Y : position.Y > Bounds.Height ? position.Y - Bounds.Height : 0;
        if (dy != 0) ((IScrollable)this).Offset = new Vector(_offset.X, _offset.Y + Math.Clamp(dy, -_lineHeight * 3, _lineHeight * 3));
        _selCaret = HitTestPosition(position);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_selecting) return;
        _selecting = false;
        e.Pointer.Capture(null);
        if (_selAnchor is { } a && _selCaret is { } b && a.Line == b.Line && a.Col == b.Col) { _selAnchor = _selCaret = null; InvalidateVisual(); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if ((ctrl && e.Key == Key.C) || (e.Key == Key.Insert && ctrl)) { Copy(); e.Handled = true; }
        else if (ctrl && e.Key == Key.A) { SelectAll(); e.Handled = true; }
    }

    /* ---------------- ILogicalScrollable ---------------- */

    private void UpdateExtent()
    {
        EnsureMetrics();
        var width = Pad * 2 + Math.Max(_maxObservedWidthPx, _maxObservedLineLength * _charWidth);
        var height = Pad * 2 + _lines.Count * _lineHeight;
        var extent = new Size(width, height);
        if (extent == _extent) return;
        _extent = extent;
        _offset = ClampOffset(_offset);
        _scrollInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private Vector ClampOffset(Vector value) => new(
        Math.Clamp(value.X, 0, Math.Max(0, _extent.Width - _viewport.Width)),
        Math.Clamp(value.Y, 0, Math.Max(0, _extent.Height - _viewport.Height)));

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureMetrics();
        UpdateExtent();
        var width = double.IsInfinity(availableSize.Width) ? _extent.Width : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? _extent.Height : availableSize.Height;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_viewport != finalSize)
        {
            _viewport = finalSize;
            _offset = ClampOffset(_offset);
            _scrollInvalidated?.Invoke(this, EventArgs.Empty);
        }
        return finalSize;
    }

    Size IScrollable.Extent => _extent;
    Size IScrollable.Viewport => _viewport;
    Vector IScrollable.Offset
    {
        get => _offset;
        set
        {
            var clamped = ClampOffset(value);
            if (clamped == _offset) return;
            _offset = clamped;
            _scrollInvalidated?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    bool ILogicalScrollable.CanHorizontallyScroll { get; set; } = true;
    bool ILogicalScrollable.CanVerticallyScroll { get; set; } = true;
    bool ILogicalScrollable.IsLogicalScrollEnabled => true;
    Size ILogicalScrollable.ScrollSize => new(16, _lineHeight > 0 ? _lineHeight : 16);
    Size ILogicalScrollable.PageScrollSize => new(_viewport.Width, Math.Max(_viewport.Height - _lineHeight, _lineHeight));
    event EventHandler? ILogicalScrollable.ScrollInvalidated { add => _scrollInvalidated += value; remove => _scrollInvalidated -= value; }
    bool ILogicalScrollable.BringIntoView(Control target, Rect targetRect) => false;
    Control? ILogicalScrollable.GetControlInDirection(NavigationDirection direction, Control? from) => null;
    void ILogicalScrollable.RaiseScrollInvalidated(EventArgs e) => _scrollInvalidated?.Invoke(this, e);
}
