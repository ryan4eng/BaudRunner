using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.Utilities;
using Avalonia.VisualTree;

namespace BaudRunner;

/// <summary>
/// A virtualized colour-capable log surface. The log is kept as a plain data model
/// (lines of text with colour spans) and only the visible lines are shaped and
/// drawn, so append cost is independent of scrollback size and render cost is
/// bounded by the viewport. Supports mouse selection, copy and incremental search;
/// selection is tracked by absolute line ids so it stays anchored while old lines
/// are trimmed. The control also owns "follow the tail" behaviour, because routing
/// it through the host ScrollViewer makes user scrolling and programmatic scrolling
/// indistinguishable.
/// </summary>
public sealed class LogView : Control, ILogicalScrollable
{
    private const double FontSizePx = 13;
    private const double Pad = 8;
    private const int TrimChunk = 500;
    private const int MaxLineChars = 1024;
    private const int LayoutCacheLimit = 240;
    private const int LayoutCacheKeepMargin = 40;
    private const double DragScrollIntervalMs = 50;

    private sealed class Line
    {
        public readonly StringBuilder Text = new();
        public readonly List<(int Start, IBrush? Brush)> Spans = new();
        public int Revision;
    }

    private readonly record struct SearchMatch(long Line, int Start, int Length);

    private readonly List<Line> _lines = new() { new Line() };
    private long _firstLineId;
    private readonly Dictionary<long, (int Revision, TextLayout Layout)> _layoutCache = new();
    private readonly Dictionary<IBrush, GenericTextRunProperties> _runProperties = new();
    private readonly Typeface _typeface = new(new FontFamily("Cascadia Mono,Consolas,monospace"));
    private readonly IBrush _selectionBrush = new SolidColorBrush(Color.FromArgb(90, 59, 130, 246));
    private readonly IBrush _matchBrush = new SolidColorBrush(Color.FromArgb(110, 250, 204, 21));
    private readonly IBrush _currentMatchBrush = new SolidColorBrush(Color.FromArgb(170, 249, 115, 22));
    private IBrush _background = new SolidColorBrush(Color.Parse("#0B0E12"));
    private IBrush _defaultForeground = new SolidColorBrush(Color.Parse("#D6E2F0"));
    private double _lineHeight;
    private double _charWidth;
    private int _maxObservedLineLength;
    private double _maxObservedWidthPx;
    private bool _lastCharWasCr;
    private bool _extentUpdateQueued;
    private int _maxLines = 5000;

    private Size _extent;
    private Vector _offset;
    private Size _viewport;
    private EventHandler? _scrollInvalidated;

    private (long Line, int Col)? _selAnchor;
    private (long Line, int Col)? _selCaret;
    private bool _selecting;
    private Point _lastPointer;
    private DispatcherTimer? _dragScrollTimer;
    private double _dragScrollDelta;

    private readonly List<SearchMatch> _matches = new();
    private string _searchText = "";
    private bool _searchCaseSensitive;
    private Regex? _searchRegex;
    private long _searchScannedId = long.MinValue;
    private int _currentMatch = -1;

    public LogView()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>Retained scrollback in lines. Raising it costs only memory; lowering it trims immediately.</summary>
    public int MaxLines
    {
        get => _maxLines;
        set
        {
            _maxLines = Math.Clamp(value, 200, 1_000_000);
            TrimIfNeeded();
            UpdateExtent();
            InvalidateVisual();
        }
    }

    /// <summary>True while the view sticks to the tail of the log.</summary>
    public bool AutoScroll { get; private set; } = true;

    public event EventHandler? AutoScrollChanged;
    public event EventHandler? SearchResultsChanged;

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

    public void AppendSegments(IReadOnlyList<LogSegment> segments)
    {
        for (var i = 0; i < segments.Count; i++) AppendCore(segments[i].Text, segments[i].Brush);
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
        ResetSearchMatches();
        ClearLayoutCache();
        SetAutoScroll(true);
        UpdateExtent();
        InvalidateVisual();
    }

    public int LineCount => _lines.Count;

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

    /// <summary>The whole retained buffer, for "save log as".</summary>
    public string AllText()
    {
        var sb = new StringBuilder();
        foreach (var line in _lines) { sb.Append(line.Text); sb.Append(Environment.NewLine); }
        return sb.ToString();
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

    /* ---------------- follow / scrolling ---------------- */

    public void ScrollToEnd()
    {
        SetAutoScroll(true);
        ApplyOffset(new Vector(_offset.X, MaxOffsetY));
    }

    private double MaxOffsetY => Math.Max(0, _extent.Height - _viewport.Height);

    private void SetAutoScroll(bool value)
    {
        if (AutoScroll == value) return;
        AutoScroll = value;
        AutoScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the viewport without treating the move as user intent.</summary>
    private void ApplyOffset(Vector value)
    {
        var clamped = ClampOffset(value);
        if (clamped == _offset) return;
        _offset = clamped;
        _scrollInvalidated?.Invoke(this, EventArgs.Empty);
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
            // A line longer than MaxLineChars is hard-wrapped into real lines, so a
            // device that never sends a newline cannot make one unbounded line.
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
        TrimIfNeeded();
    }

    private void TrimIfNeeded()
    {
        if (_lines.Count <= _maxLines + TrimChunk) return;

        var remove = _lines.Count - _maxLines;
        _lines.RemoveRange(0, remove);
        _firstLineId += remove;

        // The cache is keyed by absolute id, so entries for trimmed lines would
        // otherwise sit there holding shaped-text buffers until eviction happened
        // to notice them.
        DropLayoutsBefore(_firstLineId);
        DropMatchesBefore(_firstLineId);

        // A wide line that has now scrolled out should not keep the horizontal
        // scrollbar stretched for the rest of the session.
        _maxObservedLineLength = 0;
        foreach (var line in _lines) if (line.Text.Length > _maxObservedLineLength) _maxObservedLineLength = line.Text.Length;
        _maxObservedWidthPx = 0;

        // Keep the viewport and any selection anchored to the same content as
        // old lines scroll out of the retained buffer.
        if (_offset.Y > 0 && _lineHeight > 0) _offset = new Vector(_offset.X, Math.Max(0, _offset.Y - remove * _lineHeight));
        if (_selAnchor is { } anchor && anchor.Line < _firstLineId) _selAnchor = (_firstLineId, 0);
        if (_selCaret is { } caret && caret.Line < _firstLineId) _selCaret = (_firstLineId, 0);
        if (_selAnchor is { } a && _selCaret is { } b && a.Line == b.Line && a.Col == b.Col && a.Line <= _firstLineId) { _selAnchor = _selCaret = null; }
    }

    private void FinishAppend()
    {
        UpdateExtent();
        if (AutoScroll) ApplyOffset(new Vector(_offset.X, MaxOffsetY));
        if (_searchText.Length > 0) SearchResultsChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /* ---------------- search ---------------- */

    /// <summary>Sets the incremental search. An invalid regular expression is reported by returning false.</summary>
    public bool SetSearch(string? text, bool caseSensitive, bool useRegex)
    {
        var query = text ?? "";
        _searchCaseSensitive = caseSensitive;
        _searchText = query;
        _searchRegex = null;
        ResetSearchMatches();

        if (query.Length > 0 && useRegex)
        {
            try
            {
                _searchRegex = new Regex(query, (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException)
            {
                _searchText = "";
                InvalidateVisual();
                return false;
            }
        }
        InvalidateVisual();
        SearchResultsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public int MatchCount { get { EnsureMatches(); return _matches.Count; } }
    public int CurrentMatchIndex => _currentMatch;

    /// <summary>Moves to the next or previous match, selects it and brings it into view.</summary>
    public bool FindNext(bool forward)
    {
        EnsureMatches();
        if (_matches.Count == 0) return false;

        if (_currentMatch < 0)
        {
            // Start from whatever is on screen rather than from the top of a 5000-line buffer.
            var anchorLine = _firstLineId + (long)Math.Max(0, _offset.Y / Math.Max(_lineHeight, 1));
            var index = _matches.FindIndex(m => m.Line >= anchorLine);
            _currentMatch = forward ? (index < 0 ? 0 : index) : (index <= 0 ? _matches.Count - 1 : index - 1);
        }
        else
        {
            _currentMatch = forward
                ? (_currentMatch + 1) % _matches.Count
                : (_currentMatch - 1 + _matches.Count) % _matches.Count;
        }

        var match = _matches[_currentMatch];
        _selAnchor = (match.Line, match.Start);
        _selCaret = (match.Line, match.Start + match.Length);
        SetAutoScroll(false);
        EnsureLineVisible(match.Line);
        InvalidateVisual();
        SearchResultsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void EnsureLineVisible(long id)
    {
        EnsureMetrics();
        var index = (int)(id - _firstLineId);
        if (index < 0 || index >= _lines.Count) return;
        var top = Pad + index * _lineHeight;
        if (top < _offset.Y) ApplyOffset(new Vector(_offset.X, Math.Max(0, top - _lineHeight)));
        else if (top + _lineHeight > _offset.Y + _viewport.Height) ApplyOffset(new Vector(_offset.X, top + _lineHeight - _viewport.Height + Pad));
    }

    private void ResetSearchMatches()
    {
        _matches.Clear();
        _searchScannedId = long.MinValue;
        _currentMatch = -1;
    }

    private void DropMatchesBefore(long id)
    {
        if (_matches.Count == 0) return;
        var keep = _matches.FindIndex(m => m.Line >= id);
        if (keep < 0) _matches.Clear();
        else if (keep > 0) _matches.RemoveRange(0, keep);
        if (_searchScannedId < id) _searchScannedId = long.MinValue;
        _currentMatch = -1;
    }

    /// <summary>
    /// Incremental: only lines added since the last scan are searched, and only the
    /// tail line is re-scanned, so keeping a search active during a live stream costs
    /// work proportional to new content rather than to the whole scrollback.
    /// </summary>
    private void EnsureMatches()
    {
        if (_searchText.Length == 0) { if (_matches.Count > 0) ResetSearchMatches(); return; }

        var lastId = _firstLineId + _lines.Count - 1;
        var from = _searchScannedId == long.MinValue ? _firstLineId : Math.Max(_firstLineId, _searchScannedId);
        if (from > lastId) return;

        // The tail line may have grown since it was scanned; drop and redo it.
        var tailIndex = _matches.FindIndex(m => m.Line >= from);
        if (tailIndex >= 0) _matches.RemoveRange(tailIndex, _matches.Count - tailIndex);

        for (var id = from; id <= lastId; id++)
        {
            var line = _lines[(int)(id - _firstLineId)];
            if (line.Text.Length == 0) continue;
            var text = line.Text.ToString();
            if (_searchRegex is { } regex)
            {
                foreach (Match match in regex.Matches(text))
                {
                    if (match.Length > 0) _matches.Add(new SearchMatch(id, match.Index, match.Length));
                }
            }
            else
            {
                var comparison = _searchCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                var start = 0;
                while (start <= text.Length - _searchText.Length)
                {
                    var found = text.IndexOf(_searchText, start, comparison);
                    if (found < 0) break;
                    _matches.Add(new SearchMatch(id, found, _searchText.Length));
                    start = found + _searchText.Length;
                }
            }
        }
        _searchScannedId = lastId;
        if (_currentMatch >= _matches.Count) _currentMatch = -1;
    }

    private void CollectMatchesOnLine(long id, List<SearchMatch> into)
    {
        into.Clear();
        var low = 0;
        var high = _matches.Count - 1;
        var first = -1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (_matches[mid].Line >= id) { first = mid; high = mid - 1; }
            else low = mid + 1;
        }
        if (first < 0) return;
        for (var i = first; i < _matches.Count && _matches[i].Line == id; i++) into.Add(_matches[i]);
    }

    /* ---------------- layout & rendering ---------------- */

    private void EnsureMetrics()
    {
        if (_lineHeight > 0) return;
        using var sample = new TextLayout("MMMMMMMMMM", _typeface, FontSizePx, _defaultForeground);
        _lineHeight = Math.Ceiling(sample.Height);
        _charWidth = sample.WidthIncludingTrailingWhitespace / 10;
    }

    private TextLayout GetLayout(int index)
    {
        var id = _firstLineId + index;
        var line = _lines[index];
        if (_layoutCache.TryGetValue(id, out var cached))
        {
            if (cached.Revision == line.Revision) return cached.Layout;
            // TextLayout owns native text blobs and pooled glyph buffers; the
            // superseded one has to be released explicitly.
            cached.Layout.Dispose();
        }

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
        foreach (var entry in _layoutCache.Values) entry.Layout.Dispose();
        _layoutCache.Clear();
    }

    private void DropLayoutsBefore(long id)
    {
        if (_layoutCache.Count == 0) return;
        List<long>? stale = null;
        foreach (var key in _layoutCache.Keys)
        {
            if (key < id) (stale ??= new List<long>()).Add(key);
        }
        if (stale is null) return;
        foreach (var key in stale) { _layoutCache[key].Layout.Dispose(); _layoutCache.Remove(key); }
    }

    private void ScheduleExtentUpdate()
    {
        if (_extentUpdateQueued) return;
        _extentUpdateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _extentUpdateQueued = false;
            // The control may have been detached between posting and running.
            if (this.GetVisualRoot() is null) return;
            UpdateExtent();
        });
    }

    private void EvictLayoutCache(long firstVisibleId, long lastVisibleId)
    {
        if (_layoutCache.Count <= LayoutCacheLimit) return;
        List<long>? stale = null;
        foreach (var key in _layoutCache.Keys)
        {
            if (key < firstVisibleId - LayoutCacheKeepMargin || key > lastVisibleId + LayoutCacheKeepMargin) (stale ??= new List<long>()).Add(key);
        }
        if (stale is null) return;
        foreach (var key in stale) { _layoutCache[key].Layout.Dispose(); _layoutCache.Remove(key); }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopDragScroll();
        ClearLayoutCache();
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
        var hasSearch = _searchText.Length > 0;
        if (hasSearch) EnsureMatches();
        var lineMatches = hasSearch ? new List<SearchMatch>() : null;
        var currentMatch = _currentMatch >= 0 && _currentMatch < _matches.Count ? _matches[_currentMatch] : default;

        for (var i = first; i <= last; i++)
        {
            var layout = GetLayout(i);
            var origin = new Point(Pad - _offset.X, Pad + i * _lineHeight - top);
            var id = _firstLineId + i;
            var lineLength = _lines[i].Text.Length;

            if (lineMatches is not null)
            {
                CollectMatchesOnLine(id, lineMatches);
                foreach (var match in lineMatches)
                {
                    var brush = _currentMatch >= 0 && match == currentMatch ? _currentMatchBrush : _matchBrush;
                    var to = Math.Min(match.Start + match.Length, lineLength);
                    if (to <= match.Start) continue;
                    foreach (var rect in layout.HitTestTextRange(match.Start, to - match.Start))
                        context.FillRectangle(brush, new Rect(rect.X + origin.X, origin.Y, rect.Width, _lineHeight));
                }
            }

            if (selection is { } s && id >= s.Start.Line && id <= s.End.Line)
            {
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
        var position = HitTestPosition(e.GetPosition(this));

        if (e.ClickCount >= 3) { SelectLine(position.Line); return; }
        if (e.ClickCount == 2) { SelectWord(position); return; }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _selAnchor is not null) _selCaret = position;
        else _selAnchor = _selCaret = position;

        _selecting = true;
        _lastPointer = e.GetPosition(this);
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    private void SelectLine(long id)
    {
        var index = (int)(id - _firstLineId);
        if (index < 0 || index >= _lines.Count) return;
        _selAnchor = (id, 0);
        _selCaret = (id, _lines[index].Text.Length);
        InvalidateVisual();
    }

    private void SelectWord((long Line, int Col) position)
    {
        var index = (int)(position.Line - _firstLineId);
        if (index < 0 || index >= _lines.Count) return;
        var text = _lines[index].Text;
        if (text.Length == 0) return;
        var col = Math.Clamp(position.Col, 0, text.Length - 1);
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '-' or ':' or '/' or '\\';

        var start = col;
        var end = col;
        if (IsWord(text[col]))
        {
            while (start > 0 && IsWord(text[start - 1])) start--;
            while (end < text.Length && IsWord(text[end])) end++;
        }
        else end = col + 1;

        _selAnchor = (position.Line, start);
        _selCaret = (position.Line, end);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { EndDrag(); return; }

        var position = e.GetPosition(this);
        // Dragging a selection out of the live tail is an explicit "stop following".
        if (Math.Abs(position.Y - _lastPointer.Y) > 2 || Math.Abs(position.X - _lastPointer.X) > 2) SetAutoScroll(false);
        _lastPointer = position;

        // Nudge the viewport when dragging past an edge so selections can extend
        // beyond the visible region, and keep nudging while the pointer is held there.
        _dragScrollDelta = position.Y < 0 ? position.Y : position.Y > Bounds.Height ? position.Y - Bounds.Height : 0;
        if (_dragScrollDelta != 0) StartDragScroll(); else StopDragScroll();

        _selCaret = HitTestPosition(position);
        InvalidateVisual();
    }

    private void StartDragScroll()
    {
        if (_dragScrollTimer is not null) return;
        _dragScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DragScrollIntervalMs) };
        _dragScrollTimer.Tick += (_, _) =>
        {
            if (!_selecting || _dragScrollDelta == 0) { StopDragScroll(); return; }
            ApplyOffset(new Vector(_offset.X, _offset.Y + Math.Clamp(_dragScrollDelta, -_lineHeight * 3, _lineHeight * 3)));
            _selCaret = HitTestPosition(_lastPointer);
            InvalidateVisual();
        };
        _dragScrollTimer.Start();
    }

    private void StopDragScroll()
    {
        _dragScrollTimer?.Stop();
        _dragScrollTimer = null;
        _dragScrollDelta = 0;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        EndDrag();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // Without this the control stays in drag-select mode with no button held and
        // the selection rubber-bands after every later mouse move.
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_selecting) return;
        _selecting = false;
        StopDragScroll();
        if (_selAnchor is { } a && _selCaret is { } b && a.Line == b.Line && a.Col == b.Col) { _selAnchor = _selCaret = null; InvalidateVisual(); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        EnsureMetrics();
        switch (e.Key)
        {
            case Key.C when ctrl:
            case Key.Insert when ctrl:
                Copy(); e.Handled = true; break;
            case Key.A when ctrl:
                SelectAll(); e.Handled = true; break;
            case Key.End when ctrl:
                ScrollToEnd(); e.Handled = true; break;
            case Key.Home when ctrl:
                SetAutoScroll(false); ApplyOffset(new Vector(_offset.X, 0)); e.Handled = true; break;
            case Key.PageUp:
                SetAutoScroll(false); ApplyOffset(new Vector(_offset.X, _offset.Y - Math.Max(_viewport.Height - _lineHeight, _lineHeight))); e.Handled = true; break;
            case Key.PageDown:
                ApplyOffset(new Vector(_offset.X, _offset.Y + Math.Max(_viewport.Height - _lineHeight, _lineHeight)));
                SetAutoScroll(MaxOffsetY - _offset.Y <= _lineHeight * 0.5); e.Handled = true; break;
            case Key.Up:
                SetAutoScroll(false); ApplyOffset(new Vector(_offset.X, _offset.Y - _lineHeight)); e.Handled = true; break;
            case Key.Down:
                ApplyOffset(new Vector(_offset.X, _offset.Y + _lineHeight));
                SetAutoScroll(MaxOffsetY - _offset.Y <= _lineHeight * 0.5); e.Handled = true; break;
        }
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
            if (AutoScroll) _offset = new Vector(_offset.X, MaxOffsetY);
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
            var movedVertically = Math.Abs(clamped.Y - _offset.Y) > 0.5;
            _offset = clamped;

            // Everything that reaches this setter comes from the host ScrollViewer,
            // i.e. the wheel, the scrollbar or a keyboard gesture it handled. Follow
            // mode is therefore decided purely by where the user put the viewport -
            // no "is a programmatic scroll pending" flag, which is what previously
            // made it impossible to scroll away from the tail during a live stream.
            if (movedVertically) SetAutoScroll(MaxOffsetY - clamped.Y <= Math.Max(_lineHeight, 1) * 0.5);

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
