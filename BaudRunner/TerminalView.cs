using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace BaudRunner;

/// <summary>One quick-command row: the controls plus the send action bound to them.</summary>
public sealed class CommandRow
{
    public TextBox? Text;
    public CheckBox? Hex;
    public ComboBox? Ending;
    public Button? SendButton;
    public Func<Task>? Send;
}

/// <summary>
/// Everything one transport tab owns: its controls, its session, its formatting
/// state and its receive queue. Held by MainWindow, which does the wiring.
/// </summary>
public sealed class TerminalView
{
    public required string Title { get; init; }
    public required TabItem Tab { get; init; }
    public required TransportKind Kind { get; init; }
    public required TransportSession Session { get; init; }
    public required DisplayFormatter Formatter { get; init; }

    public required LogView Log { get; init; }
    public required ScrollViewer LogScroll { get; init; }
    public required VtTerminalControl Vt { get; init; }
    public required ScrollViewer VtScroll { get; init; }

    public required ComboBox Display { get; init; }
    public required TextBlock ModeLabel { get; init; }
    public required TextBlock FollowStatus { get; init; }
    public required TextBlock Counters { get; init; }
    public required Button JumpToLive { get; init; }
    public required Border Footer { get; init; }

    public required TextBox Address { get; init; }
    public required TextBox Port { get; init; }
    public ComboBox? PortList { get; init; }
    public required AutoCompleteBox Baud { get; init; }
    public required ComboBox DataBits { get; init; }
    public required ComboBox Parity { get; init; }
    public required ComboBox StopBits { get; init; }
    public required ComboBox FlowControl { get; init; }
    public required CheckBox AutoReconnect { get; init; }
    public required CheckBox Rts { get; init; }
    public required CheckBox Dtr { get; init; }
    public required Border[] Signals { get; init; }
    public required Button OpenButton { get; init; }
    public required Button CloseButton { get; init; }
    public Button? BreakButton { get; init; }
    public Button? ResetButton { get; init; }

    public required List<CommandRow> Rows { get; init; }
    public required TextBox SendBox { get; init; }
    public required Button SendButton { get; init; }
    public required ComboBox SendEnding { get; init; }
    public required ToggleButton RepeatToggle { get; init; }
    public required TextBox RepeatInterval { get; init; }

    public required Border FindBar { get; init; }
    public required TextBox FindBox { get; init; }
    public required TextBlock FindStatus { get; init; }
    public required ToggleButton FindCase { get; init; }
    public required ToggleButton FindRegex { get; init; }

    public ListBox? TcpClients { get; init; }
    public Button? DisconnectClient { get; init; }
    public TextBlock? ClientCountLabel { get; init; }
    public int? SelectedClientId;

    public bool VtMode;
    public bool PauseDisplay;
    public bool LocalEcho = true;
    public bool OpenRequested;
    public bool Connecting;
    public bool Reconnecting;

    public LogWriter? Writer;
    public string? LastLogPath;
    public DispatcherTimer? RepeatTimer;

    public readonly List<string> History = new();
    public int HistoryCursor = -1;
    public string HistoryDraft = "";

    // Receive queue: filled from reader threads, drained on the UI thread.
    public readonly object PendingLock = new();
    public readonly List<ReadOnlyMemory<byte>> PendingChunks = new();
    public int PendingBytes;
    public long DroppedBytes;
    public bool DrainScheduled;
    public DateTime LastDrainAt = DateTime.UtcNow;

    /// <summary>Follow state for the VT surface, which scrolls through a ScrollViewer rather than owning its offset.</summary>
    public bool VtFollowing = true;

    public long LastRxSample;
    public long LastTxSample;
    public DateTime LastSampleAt = DateTime.UtcNow;
    public double RxRate;
    public double TxRate;
    public long FramingErrors;
    public long OverrunErrors;
    public long ParityErrors;

    public ScrollViewer ActiveScroll => VtMode ? VtScroll : LogScroll;

    /// <summary>Adds a sent command to the recall ring, most recent last, without adjacent duplicates.</summary>
    public void RememberCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (History.Count > 0 && History[^1] == text) { HistoryCursor = -1; return; }
        History.Add(text);
        if (History.Count > 100) History.RemoveRange(0, History.Count - 100);
        HistoryCursor = -1;
    }
}
