namespace BaudRunner;

/// <summary>A parsed CSI sequence: <c>ESC [ &lt;private&gt; &lt;params&gt; &lt;intermediates&gt; &lt;final&gt;</c>.</summary>
public readonly struct AnsiCsi
{
    public char Private { get; }
    public char Final { get; }
    public string Intermediates { get; }
    private readonly int[] _parameters;

    public AnsiCsi(char privateMarker, int[] parameters, string intermediates, char final)
    {
        Private = privateMarker; _parameters = parameters; Intermediates = intermediates; Final = final;
    }

    public int Count => _parameters.Length;

    /// <summary>Parameter <paramref name="index"/>, or <paramref name="fallback"/> when it was omitted.</summary>
    public int this[int index, int fallback = 0] => index >= 0 && index < _parameters.Length && _parameters[index] >= 0 ? _parameters[index] : fallback;

    /// <summary>True when the parameter was written out rather than defaulted. ECMA-48 gives omitted parameters a per-command meaning.</summary>
    public bool HasParameter(int index) => index >= 0 && index < _parameters.Length && _parameters[index] >= 0;
}

/// <summary>
/// A byte-at-a-time ECMA-48 escape sequence scanner. It recognises the whole
/// grammar (CSI, OSC/DCS/APC strings, charset designators, single-byte finals)
/// so that sequences the application does not act on are still consumed whole
/// rather than leaking their tail into the visible text. State is retained
/// across calls, so a sequence may be split across reads.
/// </summary>
public sealed class AnsiScanner
{
    // A real terminal accepts 16 parameters; the caps exist so malformed or
    // binary input cannot make the scanner latch and swallow the stream.
    private const int MaxParameters = 32;
    private const int MaxParameterChars = 96;
    private const int MaxStringChars = 4096;

    private enum State { Ground, Escape, CsiParameter, CsiIntermediate, StringSequence, StringEscape, Charset }

    private State _state = State.Ground;
    private readonly List<int> _parameters = new();
    private readonly System.Text.StringBuilder _intermediates = new();
    private char _private;
    private int _current = -1;
    private int _parameterChars;
    private int _stringChars;

    /// <summary>True while a partially-received sequence is buffered.</summary>
    public bool InSequence => _state != State.Ground;

    public void Reset()
    {
        _state = State.Ground;
        _parameters.Clear(); _intermediates.Clear();
        _private = '\0'; _current = -1; _parameterChars = 0; _stringChars = 0;
    }

    /// <summary>
    /// Feeds one byte. Returns true when the byte belonged to an escape sequence
    /// and must not be displayed. <paramref name="csi"/> is set on the byte that
    /// completes a CSI sequence.
    /// </summary>
    public bool Feed(byte value, out AnsiCsi csi)
    {
        csi = default;
        switch (_state)
        {
            case State.Ground:
                if (value != 0x1B) return false;
                BeginEscape();
                return true;

            case State.Escape:
                return FeedEscape(value);

            case State.CsiParameter:
                if (value == 0x1B) { BeginEscape(); return true; }
                if (value is >= 0x30 and <= 0x3F) { AppendParameter((char)value); return true; }
                if (value is >= 0x20 and <= 0x2F) { _state = State.CsiIntermediate; _intermediates.Append((char)value); return true; }
                if (value is >= 0x40 and <= 0x7E) { csi = CompleteCsi((char)value); return true; }
                // A C0 control inside a sequence aborts it; hand the byte back so
                // a stray CR/LF still breaks the line instead of vanishing.
                Reset();
                return false;

            case State.CsiIntermediate:
                if (value == 0x1B) { BeginEscape(); return true; }
                if (value is >= 0x20 and <= 0x2F) { if (_intermediates.Length < 8) _intermediates.Append((char)value); return true; }
                if (value is >= 0x40 and <= 0x7E) { csi = CompleteCsi((char)value); return true; }
                Reset();
                return false;

            case State.StringSequence:
                // OSC/DCS/APC run until BEL or ST (ESC \).
                if (value == 0x07) { Reset(); return true; }
                if (value == 0x1B) { _state = State.StringEscape; return true; }
                if (++_stringChars > MaxStringChars) Reset();
                return true;

            case State.StringEscape:
                if (value == 0x5C) { Reset(); return true; }
                if (value == 0x1B) return true;
                _state = State.StringSequence;
                return true;

            case State.Charset:
                Reset();
                return true;
        }
        return false;
    }

    private void BeginEscape()
    {
        Reset();
        _state = State.Escape;
    }

    private bool FeedEscape(byte value)
    {
        switch (value)
        {
            case (byte)'[':
                _state = State.CsiParameter;
                return true;
            case (byte)']': case (byte)'P': case (byte)'X': case (byte)'^': case (byte)'_':
                _state = State.StringSequence;
                return true;
            case (byte)'(': case (byte)')': case (byte)'*': case (byte)'+': case (byte)'#': case (byte)'%': case (byte)' ':
                _state = State.Charset;
                return true;
            case 0x1B:
                return true;
        }
        // Everything else is a complete two-byte sequence (ESC 7, ESC =, ESC M ...).
        Reset();
        return true;
    }

    private void AppendParameter(char c)
    {
        if (++_parameterChars > MaxParameterChars) { Reset(); return; }
        if (c == ';')
        {
            if (_parameters.Count < MaxParameters) _parameters.Add(_current);
            _current = -1;
            return;
        }
        if (c is >= '0' and <= '9')
        {
            var digit = c - '0';
            _current = _current < 0 ? digit : Math.Min(_current * 10 + digit, 65535);
            return;
        }
        // ':' subparameters are kept as separators; '<' '=' '>' '?' are private markers.
        if (c == ':') { if (_parameters.Count < MaxParameters) _parameters.Add(_current); _current = -1; return; }
        if (_private == '\0' && _parameters.Count == 0 && _current < 0) _private = c;
    }

    private AnsiCsi CompleteCsi(char final)
    {
        if (_current >= 0 || _parameters.Count > 0) _parameters.Add(_current);
        var result = new AnsiCsi(_private, _parameters.ToArray(), _intermediates.ToString(), final);
        Reset();
        return result;
    }
}
