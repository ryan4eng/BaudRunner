using System.IO.Ports;
using System.Diagnostics;
using System.Text;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace BaudRunner;

public sealed class MainWindow : Window
{
    private const string HexByteColor = "#9E9E9E";
    private const string ErrorColor = "#E57373";
    private const string EchoColor = "#C792EA";
    private const int MaxPendingBytes = 8 * 1024 * 1024;
    private const double MinDrainIntervalMs = 20;

    private static readonly Dictionary<string, IBrush> _brushCache = new();
    private static string FullVersion => typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "v2.0.0";
    private static string AppVersion => FullVersion.Split('+', 2)[0];

    private readonly List<TerminalView> _views = new();
    private readonly Dictionary<TerminalView, CancellationTokenSource> _reconnects = new();
    private readonly string _logDirectory = Path.Combine(AppConfig.Directory, "logs");
    private readonly AppConfig _config;
    private readonly DispatcherTimer _configSaveTimer;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _portScanTimer;
    private string[] _knownPorts = Array.Empty<string>();
    private Menu? _mainMenu;
    private TabControl? _tabs;
    private bool _skipConfigSave;
    private bool _shuttingDown;
    private readonly string? _configWarning;

    public MainWindow()
    {
        _config = AppConfig.Load(out _configWarning);
        Application.Current!.RequestedThemeVariant = string.Equals(_config.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Light : ThemeVariant.Dark;
        Title = $"BaudRunner {AppVersion} - native serial & network terminal";
        MinWidth = 900; MinHeight = 600;
        RestoreGeometry();
        Background = new SolidColorBrush(Color.Parse(IsLightTheme ? "#F3F5F7" : "#11151B"));
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://BaudRunner/AppIcon.png"))); } catch { }
        Content = BuildShell();
        Closing += OnClosing;

        _configSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _configSaveTimer.Tick += (_, _) => { _configSaveTimer.Stop(); PersistConfig(); };
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => { foreach (var view in _views) UpdateCounters(view); };
        _statsTimer.Start();
        _portScanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _portScanTimer.Tick += (_, _) => ScanForPortChanges();
        _portScanTimer.Start();

        Opened += (_, _) =>
        {
            ClampToVisibleScreen();
            var pruned = LogWriter.Prune(_logDirectory, TimeSpan.FromDays(Math.Max(1, _config.LogRetentionDays)));
            if (pruned > 0) AppendText(_views[0], $"[Removed {pruned} log file(s) older than {_config.LogRetentionDays} days]\r\n", null);
            if (_configWarning is not null) _ = ShowMessage("Settings", _configWarning);

            // Test/automation hook: `BaudRunner --auto-open` opens the serial tab's saved
            // connection on startup; `--auto-open="TCP Client"` opens that tab instead.
            if (Environment.GetCommandLineArgs().FirstOrDefault(argument => argument.StartsWith("--auto-open", StringComparison.OrdinalIgnoreCase)) is { } hook)
            {
                var wanted = hook.Contains('=') ? hook[(hook.IndexOf('=') + 1)..].Trim('"') : "Serial";
                if (_views.FirstOrDefault(view => string.Equals(view.Title, wanted, StringComparison.OrdinalIgnoreCase)) is { } target)
                {
                    if (_tabs is not null) _tabs.SelectedItem = target.Tab;
                    target.OpenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }
        };
    }

    /* ---------------- shell ---------------- */

    private Control BuildShell()
    {
        var tabs = new TabControl { Margin = new Thickness(12) };
        _tabs = tabs;
        AddTerminal(tabs, "Serial", TransportKind.Serial);
        AddTerminal(tabs, "TCP Client", TransportKind.TcpClient);
        AddTerminal(tabs, "TCP Server", TransportKind.TcpServer);
        AddTerminal(tabs, "UDP Client", TransportKind.UdpClient);
        AddTerminal(tabs, "UDP Server", TransportKind.UdpServer);
        var savedTab = _views.FirstOrDefault(view => view.Title == _config.SelectedTab);
        if (savedTab is not null) tabs.SelectedItem = savedTab.Tab;
        tabs.SelectionChanged += (_, _) => { UpdateWindowTitle(); RequestConfigSave(); };

        var menu = new Menu { Background = new SolidColorBrush(Color.Parse(IsLightTheme ? "#E3E7EB" : "#191F27")) };
        _mainMenu = menu;

        var file = new MenuItem { Header = "_File" };
        file.Items.Add(MenuButton("_Open connection", OpenActive, new KeyGesture(Key.O, KeyModifiers.Control)));
        file.Items.Add(MenuButton("_Close connection", CloseActive, new KeyGesture(Key.W, KeyModifiers.Control)));
        file.Items.Add(new Separator());
        file.Items.Add(MenuButton("C_lear active log", () => { if (Active is { } view) ClearView(view); }, new KeyGesture(Key.L, KeyModifiers.Control)));
        file.Items.Add(MenuButton("_Save log as...", () => { if (Active is { } view) _ = SaveTextAsync(view, view.VtMode ? view.Vt.AllText : view.Log.AllText(), "log"); }, new KeyGesture(Key.S, KeyModifiers.Control)));
        file.Items.Add(new Separator());
        file.Items.Add(MenuButton("E_xit", Close));
        file.Items.Add(MenuButton("Exit without saving settings", () => { _skipConfigSave = true; Close(); }));

        var viewMenu = new MenuItem { Header = "_View" };
        var light = new MenuItem { Header = "Light mode" }; light.Click += (_, _) => SetTheme(ThemeVariant.Light);
        var dark = new MenuItem { Header = "Dark mode" }; dark.Click += (_, _) => SetTheme(ThemeVariant.Dark);
        viewMenu.Items.Add(light); viewMenu.Items.Add(dark);
        viewMenu.Items.Add(new Separator());
        viewMenu.Items.Add(MenuButton("_Find in log...", () => { if (Active is { } view) ShowFind(view); }, new KeyGesture(Key.F, KeyModifiers.Control)));
        viewMenu.Items.Add(MenuButton("Jump to live output", () => { if (Active is { } view) FollowLiveOutput(view); }, new KeyGesture(Key.End, KeyModifiers.Control)));
        var scrollback = new MenuItem { Header = "Scrollback" };
        foreach (var size in new[] { 1000, 5000, 20000, 100000 })
        {
            var item = new MenuItem { Header = $"{size:N0} lines", ToggleType = MenuItemToggleType.Radio, IsChecked = _config.ScrollbackLines == size };
            item.Click += (_, _) => { _config.ScrollbackLines = size; foreach (var v in _views) v.Log.MaxLines = size; RequestConfigSave(); };
            scrollback.Items.Add(item);
        }
        viewMenu.Items.Add(scrollback);

        var help = new MenuItem { Header = "_Help" };
        help.Items.Add(MenuButton("Keyboard shortcuts", () => _ = ShowMessage("Keyboard shortcuts", ShortcutHelp())));
        help.Items.Add(MenuButton("About BaudRunner", () => _ = ShowMessage("About BaudRunner", $"BaudRunner {AppVersion}\r\nBuild: {FullVersion}\r\nA native C# Avalonia terminal for Windows and Linux.\r\n\r\nSettings: {AppConfig.Directory}\r\nLogs: {_logDirectory}")));

        menu.Items.Add(file); menu.Items.Add(viewMenu); menu.Items.Add(help);

        KeyDown += HandleFunctionKey;

        // MenuItem.InputGesture only *renders* the shortcut next to the item; the
        // gesture has to be registered here for it to actually do anything. They are
        // dispatched from a tunnelling KeyDown handler rather than Window.KeyBindings:
        // Avalonia matches KeyBindings before the focused control sees the key at all,
        // which is what kept Escape and Ctrl+letter from ever reaching the VT100
        // terminal - Ctrl+W to delete a word in a shell closed the port instead.
        AddHandler(KeyDownEvent, HandleShortcut, RoutingStrategies.Tunnel);
        Bind(Key.O, KeyModifiers.Control, OpenActive);
        Bind(Key.W, KeyModifiers.Control, CloseActive);
        Bind(Key.L, KeyModifiers.Control, () => { if (Active is { } view) ClearView(view); });
        Bind(Key.S, KeyModifiers.Control, () => { if (Active is { } view) _ = SaveTextAsync(view, view.VtMode ? view.Vt.AllText : view.Log.AllText(), "log"); });
        Bind(Key.F, KeyModifiers.Control, () => { if (Active is { } view) ShowFind(view); });
        Bind(Key.End, KeyModifiers.Control, () => { if (Active is { } view) FollowLiveOutput(view); });
        Bind(Key.Escape, KeyModifiers.None, () => { if (Active is { } view) { if (view.FindBar.IsVisible) HideFind(view); else FollowLiveOutput(view); } });
        for (var i = 0; i < 5; i++)
        {
            var index = i;
            Bind(Key.D1 + index, KeyModifiers.Control, () => { if (index < _views.Count) tabs.SelectedItem = _views[index].Tab; });
        }

        DockPanel.SetDock(menu, Dock.Top);
        return new DockPanel { Children = { menu, tabs } };
    }

    private readonly List<(KeyGesture Gesture, Action Action)> _shortcuts = new();

    private void Bind(Key key, KeyModifiers modifiers, Action action) => _shortcuts.Add((new KeyGesture(key, modifiers), action));

    private void HandleShortcut(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        // In VT100 mode a focused terminal owns Escape and Ctrl+letter: those bytes
        // belong to the device, so the application shortcuts on them stand aside.
        if (Active is { VtMode: true } terminal && terminal.Vt.IsKeyboardFocusWithin && VtTerminalControl.ConsumesKey(e.Key, e.KeyModifiers)) return;
        foreach (var (gesture, action) in _shortcuts)
        {
            if (!gesture.Matches(e)) continue;
            e.Handled = true;
            action();
            return;
        }
    }

    // Raising Click on a disabled button still runs its handlers, so the menu and
    // shortcut paths check what the button itself would allow: Ctrl+W with nothing
    // open printed "[Connection closed]", and Ctrl+O during a reconnect wait ran a
    // second connect attempt alongside the retry loop.
    private void OpenActive() { if (Active is { OpenButton.IsEnabled: true } view) view.OpenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private void CloseActive() { if (Active is { CloseButton.IsEnabled: true } view) view.CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }

    private static string ShortcutHelp() => string.Join("\r\n", new[]
    {
        "Ctrl+O / Ctrl+W    Open / close the active connection",
        "Ctrl+L             Clear the active log",
        "Ctrl+S             Save the active log to a file",
        "Ctrl+F             Find in log (Enter = next, Shift+Enter = previous)",
        "Ctrl+End           Jump back to live output",
        "Esc                Close find, or jump back to live output",
        "Ctrl+1 .. Ctrl+5   Select a transport tab",
        "F1 .. F12          Send quick command 1..12",
        "Up / Down          Recall previous commands in the send box",
        "Ctrl+C / Ctrl+A    Copy selection / select all in the log",
        "In VT100 mode Esc and Ctrl+letter go to the device; Ctrl+Shift+C copies, Ctrl+Shift+V or Shift+Insert pastes.",
    });

    private TerminalView? Active => _tabs?.SelectedItem is TabItem tab ? _views.FirstOrDefault(view => ReferenceEquals(view.Tab, tab)) : null;

    private async void HandleFunctionKey(object? sender, KeyEventArgs e)
    {
        if (e.Key < Key.F1 || e.Key > Key.F12 || Active is not { } view) return;
        var index = (int)e.Key - (int)Key.F1;
        if (!view.Session.IsOpen || index >= view.Rows.Count || view.Rows[index].Send is not { } send) return;
        e.Handled = true;
        try { await send(); }
        catch (Exception ex) { AppendText(view, $"\r\n[Send error: {ex.Message}]\r\n", ErrorColor); }
    }

    private void AddTerminal(TabControl tabs, string title, TransportKind kind)
    {
        var view = BuildTerminal(title, kind);
        _views.Add(view);
        tabs.Items.Add(view.Tab);
    }

    /* ---------------- one transport tab ---------------- */

    private TerminalView BuildTerminal(string title, TransportKind kind)
    {
        var saved = _config.Terminals.TryGetValue(title, out var value) ? value : new TerminalConfig();
        var session = new TransportSession(kind);
        var isSerial = kind == TransportKind.Serial;

        var log = new LogView { MaxLines = _config.ScrollbackLines };
        log.SetTheme(IsLightTheme);
        var vt = new VtTerminalControl();
        vt.SetTheme(IsLightTheme);

        var formatter = new DisplayFormatter
        {
            HexBrush = GetBrush(HexByteColor),
            TimestampBrush = GetBrush(IsLightTheme ? "#6B7280" : "#7C8CA0"),
            Ansi = saved.Ansi,
            Timestamps = saved.TimestampMode,
            Encoding = saved.Encoding,
        };

        var display = Combo(isSerial
            ? new[] { "Normal", "Hex (all bytes)", "Hex (except CR/LF)", "ASCII only", "VT100 terminal" }
            : new[] { "Normal", "Hex (all bytes)", "Hex (except CR/LF)", "ASCII only" });
        display.SelectedIndex = Math.Clamp(saved.DisplayMode, 0, isSerial ? 4 : 3);
        formatter.Mode = (DisplayMode)Math.Min(display.SelectedIndex, 4);

        var tab = new TabItem { Header = title };
        var rows = new List<CommandRow>();
        var mutedLabels = new List<TextBlock>();
        var address = new TextBox { Text = saved.Address, Width = 160 };
        var remotePort = new TextBox { Text = saved.Port, Width = 88, Watermark = "5800" };
        var listenPort = new TextBox { Text = string.IsNullOrWhiteSpace(saved.Port) ? "5800" : saved.Port, Width = 88, Watermark = "5800" };

        // Free text with suggestions: 250000 (Marlin), 500000 and 1000000 (FTDI/CP210x
        // native) and 31250 (MIDI) are all rates the driver accepts but a fixed list refuses.
        var baud = new AutoCompleteBox
        {
            Width = 110,
            MinimumPrefixLength = 0,
            FilterMode = AutoCompleteFilterMode.StartsWith,
            ItemsSource = new[] { "1200", "2400", "4800", "9600", "19200", "31250", "38400", "57600", "115200", "230400", "250000", "460800", "500000", "921600", "1000000", "2000000" },
            Text = string.IsNullOrWhiteSpace(saved.Baud) ? "115200" : saved.Baud,
        };
        var dataBits = Combo(new[] { "5", "6", "7", "8" }); SetComboValue(dataBits, saved.DataBits, "8");
        var parity = Combo(new[] { "None", "Odd", "Even", "Mark", "Space" }); SetComboValue(parity, saved.Parity, "None");
        var stopBits = Combo(new[] { "One", "OnePointFive", "Two" }); SetComboValue(stopBits, saved.StopBits, "One");
        var flowControl = Combo(new[] { "None", "RTS/CTS", "XON/XOFF", "RTS/CTS + XON/XOFF" }); SetComboValue(flowControl, saved.Handshake, "None");

        var open = new Button { Content = "Open", Classes = { "accent" }, MinWidth = 72 };
        var close = new Button { Content = "Close", IsEnabled = false, MinWidth = 72 };
        var clear = new Button { Content = "Clear", MinWidth = 72 };
        var autoReconnect = new CheckBox { Content = "Auto-reconnect", IsVisible = kind is TransportKind.Serial or TransportKind.TcpClient, IsChecked = saved.AutoReconnect };
        var rts = new CheckBox { Content = "RTS", IsVisible = isSerial };
        var dtr = new CheckBox { Content = "DTR", IsVisible = isSerial };
        var cts = SignalIndicator("CTS", isSerial);
        var dsr = SignalIndicator("DSR", isSerial);
        var breakButton = isSerial ? new Button { Content = "Break", IsEnabled = false, MinWidth = 64 } : null;
        var resetButton = isSerial ? new Button { Content = "Reset pulse", IsEnabled = false, MinWidth = 86 } : null;

        ComboBox? portList = null;
        if (isSerial)
        {
            portList = new ComboBox { Width = 260, PlaceholderText = "Select a port" };
            RefreshSerialPorts(portList, saved.Address);
            portList.DropDownOpened += (_, _) => RefreshSerialPorts(portList, SelectedPortName(portList));
        }

        var topControls = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), VerticalAlignment = VerticalAlignment.Bottom };
        var bottomControls = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10), VerticalAlignment = VerticalAlignment.Bottom };
        var signalControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10), VerticalAlignment = VerticalAlignment.Center, IsVisible = isSerial };
        if (isSerial)
        {
            signalControls.Children.Add(rts); signalControls.Children.Add(dtr);
            signalControls.Children.Add(cts); signalControls.Children.Add(dsr);
            signalControls.Children.Add(breakButton!); signalControls.Children.Add(resetButton!);
        }

        topControls.Children.Add(new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 18, 14, 0), Children = { clear } });
        if (isSerial)
        {
            topControls.Children.Add(Field("Port", portList!, mutedLabels));
            bottomControls.Children.Add(Field("Baud", baud, mutedLabels)); bottomControls.Children.Add(Field("Data bits", dataBits, mutedLabels));
            bottomControls.Children.Add(Field("Parity", parity, mutedLabels)); bottomControls.Children.Add(Field("Stop bits", stopBits, mutedLabels));
            bottomControls.Children.Add(Field("Flow control", flowControl, mutedLabels));
        }
        else if (kind is TransportKind.TcpServer or TransportKind.UdpServer)
        {
            topControls.Children.Add(Field("Listen port", listenPort, mutedLabels));
        }
        else
        {
            topControls.Children.Add(Field("Address", address, mutedLabels)); topControls.Children.Add(Field("Remote port", remotePort, mutedLabels));
        }
        topControls.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(10, 18, 0, 0), Children = { open, close, autoReconnect } });

        ListBox? tcpClients = null;
        Button? disconnectClient = null;
        TextBlock? clientCountLabel = null;
        if (kind == TransportKind.TcpServer)
        {
            tcpClients = new ListBox { MinHeight = 105, MaxHeight = 140, SelectionMode = SelectionMode.Single };
            disconnectClient = new Button { Content = "Disconnect selected", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left };
            clientCountLabel = new TextBlock { Text = $"Connected TCP clients 0/{TransportSession.MaxClients} (select a target):" };
        }

        var logScroll = new ScrollViewer { Content = log, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var vtScroll = new ScrollViewer { Content = vt, IsVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var modeLabel = new TextBlock { FontSize = 12, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse(IsLightTheme ? "#52606D" : "#B8C2CC")) };
        var followStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var counters = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), FontSize = 12, Foreground = GetBrush(MutedColor) };
        var jumpToLive = new Button
        {
            Content = "Jump to live output", IsVisible = false, Margin = new Thickness(12, 0, 0, 0),
            FontSize = 12, Padding = new Thickness(8, 2), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center,
        };

        var leftStatus = new StackPanel { Orientation = Orientation.Horizontal, Children = { modeLabel, followStatus, jumpToLive } };
        var footerContent = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(counters, Dock.Right);
        footerContent.Children.Add(counters);
        footerContent.Children.Add(leftStatus);
        var footer = new Border { Background = new SolidColorBrush(Color.Parse(IsLightTheme ? "#E8EDF1" : "#191F27")), Padding = new Thickness(8, 5), Child = footerContent };

        var findBox = new TextBox { Watermark = "Find", MinWidth = 220 };
        var findCase = new ToggleButton { Content = "Aa", MinWidth = 34 };
        var findRegex = new ToggleButton { Content = ".*", MinWidth = 34 };
        var findPrevious = new Button { Content = "↑", MinWidth = 32 };
        var findNext = new Button { Content = "↓", MinWidth = 32 };
        var findStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 90 };
        var findClose = new Button { Content = "✕", MinWidth = 30 };
        var findBar = new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(Color.Parse(IsLightTheme ? "#E8EDF1" : "#191F27")),
            Padding = new Thickness(8, 5),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { findBox, findCase, findRegex, findPrevious, findNext, findStatus, findClose } },
        };

        var sendBox = new TextBox { Watermark = "Type a command and press Enter", HorizontalAlignment = HorizontalAlignment.Stretch };
        var sendEnding = Combo(new[] { "No line ending", "LF", "CR", "CR+LF" });
        sendEnding.SelectedIndex = (int)saved.SendEnding;
        sendEnding.MinWidth = 130;
        var sendButton = new Button { Content = "Send", MinWidth = 64 };
        var repeatToggle = new ToggleButton { Content = "Repeat", MinWidth = 68 };
        var repeatInterval = new TextBox { Text = "1000", Width = 68, Watermark = "ms" };
        var sendGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), ColumnSpacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        sendGrid.Children.Add(sendBox);
        sendGrid.Children.Add(sendEnding); Grid.SetColumn(sendEnding, 1);
        sendGrid.Children.Add(sendButton); Grid.SetColumn(sendButton, 2);
        sendGrid.Children.Add(repeatToggle); Grid.SetColumn(repeatToggle, 3);
        sendGrid.Children.Add(repeatInterval); Grid.SetColumn(repeatInterval, 4);

        var view = new TerminalView
        {
            Title = title, Tab = tab, Kind = kind, Session = session, Formatter = formatter,
            Log = log, LogScroll = logScroll, Vt = vt, VtScroll = vtScroll,
            Display = display, ModeLabel = modeLabel, FollowStatus = followStatus, Counters = counters, JumpToLive = jumpToLive, Footer = footer,
            Address = address, Port = kind is TransportKind.TcpServer or TransportKind.UdpServer ? listenPort : remotePort,
            PortList = portList, Baud = baud, DataBits = dataBits, Parity = parity, StopBits = stopBits, FlowControl = flowControl,
            AutoReconnect = autoReconnect, Rts = rts, Dtr = dtr, Signals = new[] { cts, dsr },
            OpenButton = open, CloseButton = close, BreakButton = breakButton, ResetButton = resetButton,
            Rows = rows, SendBox = sendBox, SendButton = sendButton, SendEnding = sendEnding, RepeatToggle = repeatToggle, RepeatInterval = repeatInterval,
            FindBar = findBar, FindBox = findBox, FindStatus = findStatus, FindCase = findCase, FindRegex = findRegex,
            FindPreviousButton = findPrevious, FindNextButton = findNext, FindCloseButton = findClose,
            MutedLabels = mutedLabels,
            TcpClients = tcpClients, DisconnectClient = disconnectClient, ClientCountLabel = clientCountLabel,
            VtMode = isSerial && display.SelectedIndex == 4,
            PauseDisplay = saved.Pause,
            LocalEcho = saved.LocalEcho,
        };
        view.History.AddRange(saved.History);
        vt.EnterEnding = (LineEnding)sendEnding.SelectedIndex;
        vt.Encoding = saved.Encoding;
        vt.Paused = view.PauseDisplay;
        logScroll.IsVisible = !view.VtMode;
        vtScroll.IsVisible = view.VtMode;

        var commands = BuildCommands(view, saved);
        UpdateDisplayStatus(view);
        UpdateFollowStatus(view);
        UpdateCounters(view);

        log.AutoScrollChanged += (_, _) => UpdateFollowStatus(view);
        log.SearchResultsChanged += (_, _) => UpdateFindStatus(view);
        jumpToLive.Click += (_, _) => FollowLiveOutput(view);
        clear.Click += (_, _) => ClearView(view);
        vtScroll.ScrollChanged += (_, e) =>
        {
            if (!view.VtMode) return;
            // ScrollChanged is raised after layout, which is the first moment the
            // extent reflects the bytes just appended. Pinning the tail here, rather
            // than in the drain where Extent was still the old height, is what makes
            // the last chunk of a reply visible instead of one drain behind; the
            // offset change it causes comes back through here and updates the state.
            if (view.VtFollowing && (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)) { ScrollVtToEnd(view); return; }
            UpdateVtFollow(view);
        };

        session.BytesReceived += bytes => EnqueueBytes(view, bytes);
        session.Status += (level, message) => Dispatcher.UIThread.Post(() => AppendStatus(view, level, message));
        session.LineStatusChanged += (ctsState, dsrState) => Dispatcher.UIThread.Post(() => { SetSignal(cts, ctsState); SetSignal(dsr, dsrState); });
        session.TcpClientsChanged += clients => Dispatcher.UIThread.Post(() => UpdateTcpClients(view, clients));
        session.SerialErrorReceived += error => Dispatcher.UIThread.Post(() => RecordSerialError(view, error));
        vt.SendBytes += bytes => Dispatcher.UIThread.Post(() => _ = SendRawAsync(view, bytes));
        vt.TextAppended += text => view.Writer?.Write(text);

        var rightPane = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(topControls, Dock.Top); DockPanel.SetDock(bottomControls, Dock.Top); DockPanel.SetDock(signalControls, Dock.Top);
        rightPane.Children.Add(topControls); rightPane.Children.Add(bottomControls); rightPane.Children.Add(signalControls);
        if (kind == TransportKind.TcpServer)
        {
            var clientPanel = new StackPanel { Spacing = 6 };
            clientPanel.Children.Add(clientCountLabel!);
            clientPanel.Children.Add(tcpClients!);
            clientPanel.Children.Add(disconnectClient!);
            var clientBorder = new Border { BorderBrush = new SolidColorBrush(Color.Parse("#59636F")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 10), Child = clientPanel };
            DockPanel.SetDock(clientBorder, Dock.Top);
            rightPane.Children.Add(clientBorder);
        }
        rightPane.Children.Add(commands);

        var viewHost = new Grid(); viewHost.Children.Add(logScroll); viewHost.Children.Add(vtScroll);
        var logPane = new DockPanel();
        DockPanel.SetDock(findBar, Dock.Top);
        DockPanel.SetDock(sendGrid, Dock.Bottom);
        logPane.Children.Add(findBar); logPane.Children.Add(sendGrid); logPane.Children.Add(viewHost);

        // Draggable split: how much room the log deserves against the command list is
        // a per-session judgement, not something to hard-code.
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,6,3*") };
        var splitter = new GridSplitter { Width = 6, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        grid.Children.Add(logPane);
        Grid.SetColumn(splitter, 1); grid.Children.Add(splitter);
        Grid.SetColumn(rightPane, 2); grid.Children.Add(rightPane);

        var content = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(footer);
        content.Children.Add(grid);
        tab.Content = content;

        var logContextMenu = BuildLogContextMenu(view, () => view.VtMode ? view.Vt.HasSelection : view.Log.HasSelection, () => { if (view.VtMode) view.Vt.Copy(); else view.Log.Copy(); });
        log.ContextMenu = logContextMenu;
        vt.SetContextMenu(BuildLogContextMenu(view, () => view.Vt.HasSelection, view.Vt.Copy, paste: () => _ = view.Vt.PasteAsync()));

        WireBehaviour(view, saved);
        SetConnectionState(view, false);
        return view;
    }

    /* ---------------- behaviour wiring ---------------- */

    private void WireBehaviour(TerminalView view, TerminalConfig saved)
    {
        var session = view.Session;
        var isSerial = view.Kind == TransportKind.Serial;

        async Task Connect()
        {
            var selectedAddress = isSerial ? SelectedPortName(view.PortList) ?? "" : view.Address.Text ?? "";
            if (isSerial && string.IsNullOrWhiteSpace(selectedAddress)) throw new InvalidOperationException("Select a serial port first.");

            var port = 0;
            if (!isSerial)
            {
                if (!int.TryParse(view.Port.Text, out port) || port is < 1 or > 65535) throw new InvalidOperationException("Enter a valid port from 1 to 65535.");
            }
            var settings = ReadSerialSettings(view);
            BeginLogging(view);
            await session.OpenAsync(selectedAddress, port, settings);
            if (isSerial) { session.SetRts(view.Rts.IsChecked == true); session.SetDtr(view.Dtr.IsChecked == true); }
            view.Formatter.ResetStream();
            SetConnectionState(view, true);
        }

        view.OpenButton.Click += async (_, _) =>
        {
            // Disabled before the first await: the button used to stay live for the
            // whole connect, and a second click reran OpenAsync against the first
            // attempt's half-built transport.
            if (view.Connecting || session.IsOpen) return;
            view.OpenRequested = true;
            view.Connecting = true;
            SetConnectionState(view, false);
            try
            {
                await Connect();
            }
            catch (Exception ex)
            {
                AppendText(view, $"\r\n[Open error: {ex.Message}]\r\n", ErrorColor);
                EndLogging(view);
                // Close during the attempt clears OpenRequested; a retry loop must not start then.
                if (view.OpenRequested && view.AutoReconnect.IsChecked == true) StartReconnect(view, Connect);
            }
            finally
            {
                view.Connecting = false;
                SetConnectionState(view, session.IsOpen);
            }
        };

        view.CloseButton.Click += async (_, _) =>
        {
            view.OpenRequested = false;
            StopReconnect(view);
            StopRepeat(view);
            await CloseSessionAsync(view, "Connection closed");
        };

        session.ConnectionLost += reason => Dispatcher.UIThread.Post(async void () =>
        {
            try
            {
                // Drain first: the receive queue is serviced at Background priority, so
                // the tail of the stream would otherwise be dropped by EndLogging.
                DrainPending(view);
                AppendText(view, $"\r\n[Connection lost: {reason}]\r\n", ErrorColor);
                StopRepeat(view);
                // The transport is only flagged as lost by its reader; without this the
                // port or socket stays open with no enabled control that would free it.
                await session.CloseAsync();
                EndLogging(view);
                SetConnectionState(view, false);
                if (view.OpenRequested && view.AutoReconnect.IsChecked == true) StartReconnect(view, Connect);
            }
            catch (Exception ex) { AppendText(view, $"\r\n[Shutdown error: {ex.Message}]\r\n", ErrorColor); }
        });

        view.Display.SelectionChanged += (_, _) =>
        {
            view.VtMode = view.Kind == TransportKind.Serial && view.Display.SelectedIndex == 4;
            view.Formatter.Mode = (DisplayMode)Math.Min(view.Display.SelectedIndex, 4);
            view.Log.ScrollToEnd();
            UpdateDisplayStatus(view);
            UpdateFollowStatus(view);
            view.LogScroll.IsVisible = !view.VtMode;
            view.VtScroll.IsVisible = view.VtMode;
            if (view.VtMode)
            {
                // Find is closed first: HideFind focuses the plain log, which must not win over the terminal.
                if (view.FindBar.IsVisible) HideFind(view);
                view.VtFollowing = true;
                ScrollVtToEnd(view);
                view.Vt.Focus();
            }
            else if (view.Vt.IsKeyboardFocusWithin)
            {
                // The hidden terminal would otherwise keep translating keystrokes into device bytes.
                view.Log.Focus();
            }
            RequestConfigSave();
        };

        view.Rts.IsCheckedChanged += (_, _) => session.SetRts(view.Rts.IsChecked == true);
        view.Dtr.IsCheckedChanged += (_, _) => session.SetDtr(view.Dtr.IsChecked == true);
        view.AutoReconnect.IsCheckedChanged += (_, _) => { if (view.AutoReconnect.IsChecked != true) StopReconnect(view); RequestConfigSave(); };
        foreach (var control in new[] { view.DataBits, view.Parity, view.StopBits, view.FlowControl, view.Display })
            control.SelectionChanged += (_, _) => RequestConfigSave();
        view.Baud.LostFocus += (_, _) => RequestConfigSave();
        view.Address.LostFocus += (_, _) => RequestConfigSave();
        view.Port.LostFocus += (_, _) => RequestConfigSave();

        if (view.BreakButton is { } breakButton)
        {
            breakButton.Click += async (_, _) =>
            {
                try { AppendText(view, "\r\n[Break asserted]\r\n", null); await session.SendBreakAsync(); }
                catch (Exception ex) { AppendText(view, $"\r\n[Break error: {ex.Message}]\r\n", ErrorColor); }
            };
        }
        if (view.ResetButton is { } resetButton)
        {
            resetButton.Click += async (_, _) =>
            {
                try { AppendText(view, "\r\n[Reset pulse]\r\n", null); await session.PulseResetAsync(view.Rts.IsChecked == true, view.Dtr.IsChecked == true); }
                catch (Exception ex) { AppendText(view, $"\r\n[Reset error: {ex.Message}]\r\n", ErrorColor); }
            };
        }

        if (view.TcpClients is { } clients)
        {
            clients.SelectionChanged += (_, _) =>
            {
                view.SelectedClientId = (clients.SelectedItem as TcpClientInfo)?.Id;
                if (view.DisconnectClient is not null) view.DisconnectClient.IsEnabled = view.SelectedClientId is not null;
            };
        }
        if (view.DisconnectClient is { } disconnect)
            disconnect.Click += (_, _) => { if (view.SelectedClientId is int id) session.DisconnectTcpClient(id); };

        WireSendBar(view);
        WireFindBar(view);
    }

    private void WireSendBar(TerminalView view)
    {
        async Task SendCurrent()
        {
            var text = view.SendBox.Text ?? "";
            if (text.Length == 0) return;
            if (await SendCommandAsync(view, new CommandSlot { Text = text, Hex = false, Ending = (LineEnding)view.SendEnding.SelectedIndex }))
            {
                view.RememberCommand(text);
                view.SendBox.Text = "";
                RequestConfigSave();
            }
        }

        view.SendBox.KeyDown += async (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    await SendCurrent();
                    break;
                case Key.Up:
                    e.Handled = true;
                    RecallHistory(view, -1);
                    break;
                case Key.Down:
                    e.Handled = true;
                    RecallHistory(view, 1);
                    break;
            }
        };
        view.SendEnding.SelectionChanged += (_, _) =>
        {
            view.Vt.EnterEnding = (LineEnding)view.SendEnding.SelectedIndex;
            RequestConfigSave();
        };

        view.SendButton.Click += async (_, _) => await SendCurrent();

        view.RepeatToggle.IsCheckedChanged += (_, _) =>
        {
            if (view.RepeatToggle.IsChecked == true) StartRepeat(view, SendCurrentRepeat);
            else StopRepeat(view);
        };

        async Task SendCurrentRepeat()
        {
            var text = view.SendBox.Text ?? "";
            if (text.Length == 0) { StopRepeat(view); return; }
            await SendCommandAsync(view, new CommandSlot { Text = text, Hex = false, Ending = (LineEnding)view.SendEnding.SelectedIndex });
        }
    }

    private static void RecallHistory(TerminalView view, int direction)
    {
        if (view.History.Count == 0) return;
        if (view.HistoryCursor < 0)
        {
            if (direction > 0) return;
            view.HistoryDraft = view.SendBox.Text ?? "";
            view.HistoryCursor = view.History.Count;
        }
        var next = view.HistoryCursor + direction;
        if (next < 0) next = 0;
        if (next >= view.History.Count)
        {
            view.HistoryCursor = -1;
            view.SendBox.Text = view.HistoryDraft;
            view.SendBox.CaretIndex = view.SendBox.Text?.Length ?? 0;
            return;
        }
        view.HistoryCursor = next;
        view.SendBox.Text = view.History[next];
        view.SendBox.CaretIndex = view.SendBox.Text?.Length ?? 0;
    }

    private void StartRepeat(TerminalView view, Func<Task> send)
    {
        StopRepeat(view);
        if (!int.TryParse(view.RepeatInterval.Text, out var interval) || interval < 20)
        {
            AppendText(view, "\r\n[Repeat interval must be at least 20 ms]\r\n", ErrorColor);
            view.RepeatToggle.IsChecked = false;
            return;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(interval) };
        timer.Tick += async (_, _) =>
        {
            if (!view.Session.IsOpen) { StopRepeat(view); return; }
            await send();
        };
        view.RepeatTimer = timer;
        timer.Start();
    }

    private void StopRepeat(TerminalView view)
    {
        view.RepeatTimer?.Stop();
        view.RepeatTimer = null;
        if (view.RepeatToggle.IsChecked == true) view.RepeatToggle.IsChecked = false;
    }

    /* ---------------- find ---------------- */

    private void WireFindBar(TerminalView view)
    {
        void Apply()
        {
            var ok = view.Log.SetSearch(view.FindBox.Text, view.FindCase.IsChecked == true, view.FindRegex.IsChecked == true);
            view.FindPatternInvalid = !ok;
            view.FindStatus.Text = ok ? MatchSummary(view) : "invalid pattern";
            view.FindStatus.Foreground = GetBrush(ok ? MutedColor : ErrorColor);
        }

        view.FindBox.TextChanged += (_, _) => Apply();
        view.FindCase.IsCheckedChanged += (_, _) => Apply();
        view.FindRegex.IsCheckedChanged += (_, _) => Apply();
        view.FindBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; view.Log.FindNext(!e.KeyModifiers.HasFlag(KeyModifiers.Shift)); UpdateFindStatus(view); }
            else if (e.Key == Key.Escape) { e.Handled = true; HideFind(view); }
        };

        // Wired by reference, not by position: ToggleButton derives from Button, so
        // picking "the first three Buttons" out of the bar found the Aa and .* toggles,
        // which left the up arrow closing the bar and the down arrow and X doing nothing.
        view.FindPreviousButton.Click += (_, _) => { view.Log.FindNext(false); UpdateFindStatus(view); };
        view.FindNextButton.Click += (_, _) => { view.Log.FindNext(true); UpdateFindStatus(view); };
        view.FindCloseButton.Click += (_, _) => HideFind(view);
    }

    private void ShowFind(TerminalView view)
    {
        if (view.VtMode)
        {
            AppendText(view, "\r\n[Find is available in the normal log view, not in VT100 mode]\r\n", ErrorColor);
            return;
        }
        view.FindBar.IsVisible = true;
        view.FindBox.Focus();
        view.FindBox.SelectAll();
        UpdateFindStatus(view);
    }

    private void HideFind(TerminalView view)
    {
        view.FindBar.IsVisible = false;
        view.Log.SetSearch("", false, false);
        view.Log.Focus();
    }

    private static string MatchSummary(TerminalView view)
    {
        var count = view.Log.MatchCount;
        if (count == 0) return view.FindBox.Text is { Length: > 0 } ? "no matches" : "";
        var index = view.Log.CurrentMatchIndex;
        return index >= 0 ? $"{index + 1} of {count}" : $"{count} matches";
    }

    private void UpdateFindStatus(TerminalView view)
    {
        if (!view.FindBar.IsVisible) return;
        view.FindStatus.Text = MatchSummary(view);
    }

    /* ---------------- quick commands ---------------- */

    private Control BuildCommands(TerminalView view, TerminalConfig saved)
    {
        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(new TextBlock { Text = "Quick commands", FontSize = 16, FontWeight = FontWeight.Bold });
        if (view.Kind == TransportKind.TcpServer) panel.Children.Add(Hint(view, "Send to selected client", 12));
        panel.Children.Add(Hint(view, "F1 - F12 send the matching row.", 11));

        for (var i = 0; i < 12; i++)
        {
            var savedRow = i < saved.Commands.Count ? saved.Commands[i] : new CommandConfig();
            var rowData = new CommandRow();
            view.Rows.Add(rowData);

            var text = rowData.Text = new TextBox { Watermark = $"Command {i + 1}", MinWidth = 170, HorizontalAlignment = HorizontalAlignment.Stretch, Text = savedRow.Text };
            var hex = rowData.Hex = new CheckBox { Content = "HEX", VerticalAlignment = VerticalAlignment.Center, IsChecked = savedRow.Hex };
            var ending = rowData.Ending = Combo(new[] { "None", "LF", "CR", "CR+LF" });
            ending.SelectedIndex = (int)savedRow.Ending;
            ending.MinWidth = 86;
            var send = new Button { Content = "Send", Width = 58 };

            async Task SendCommand()
            {
                await SendCommandAsync(view, new CommandSlot { Text = text.Text ?? "", Hex = hex.IsChecked == true, Ending = (LineEnding)ending.SelectedIndex });
            }
            rowData.Send = SendCommand;
            rowData.SendButton = send;

            send.Click += async (_, _) => await SendCommand();
            text.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SendCommand(); } };
            // Live validation: an unparsable hex row is obvious before it is sent.
            // ClearValue, not null: a local null Foreground overrides the theme brush and the text renders invisible.
            void Validate()
            {
                if (CommandSlot.Validate(text.Text ?? "", hex.IsChecked == true) is null) text.ClearValue(TextBox.ForegroundProperty);
                else text.Foreground = GetBrush(ErrorColor);
            }
            text.TextChanged += (_, _) => { Validate(); RequestConfigSave(); };
            hex.IsCheckedChanged += (_, _) => { Validate(); RequestConfigSave(); };
            ending.SelectionChanged += (_, _) => RequestConfigSave();
            Validate();

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,58,90,64"), ColumnSpacing = 6, Margin = new Thickness(0, 0, 6, 0) };
            row.Children.Add(text);
            row.Children.Add(hex); Grid.SetColumn(hex, 1);
            row.Children.Add(ending); Grid.SetColumn(ending, 2);
            row.Children.Add(send); Grid.SetColumn(send, 3);
            panel.Children.Add(row);
        }
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>A secondary label in the muted colour, registered so a theme switch recolours it.</summary>
    private TextBlock Hint(TerminalView view, string text, double fontSize)
    {
        var label = new TextBlock { Text = text, FontSize = fontSize, Foreground = GetBrush(MutedColor), Margin = new Thickness(0, 0, 0, 4) };
        view.MutedLabels.Add(label);
        return label;
    }

    private async Task<bool> SendCommandAsync(TerminalView view, CommandSlot command)
    {
        if (!view.Session.IsOpen) { AppendText(view, "\r\n[Not connected]\r\n", ErrorColor); return false; }
        byte[] payload;
        try { payload = command.ToBytes(); }
        catch (FormatException ex) { AppendText(view, $"\r\n[Command error: {ex.Message}]\r\n", ErrorColor); return false; }

        if (!await SendRawAsync(view, payload)) return false;
        if (view.LocalEcho && !view.VtMode) EchoSent(view, payload);
        return true;
    }

    /// <summary>
    /// Local echo shows the bytes exactly as they went out, line ending included and
    /// in the current display mode, so where the reply lands relative to the command
    /// is visible. The old "> text" banner forced its own line breaks around it.
    /// </summary>
    private void EchoSent(TerminalView view, ReadOnlySpan<byte> payload)
    {
        _segments.Clear();
        view.Formatter.FormatEcho(payload, GetBrush(EchoColor), _segments);
        if (_segments.Count == 0) return;
        if (view.Writer is { } writer) foreach (var segment in _segments) writer.Write(segment.Text);
        view.Log.AppendSegments(_segments);
    }

    private async Task<bool> SendRawAsync(TerminalView view, ReadOnlyMemory<byte> payload)
    {
        try
        {
            await view.Session.SendAsync(payload, view.SelectedClientId);
            return true;
        }
        catch (Exception ex)
        {
            AppendText(view, $"\r\n[Send error: {ex.Message}]\r\n", ErrorColor);
            return false;
        }
    }

    /* ---------------- receive pipeline ---------------- */

    private void EnqueueBytes(TerminalView view, ReadOnlyMemory<byte> bytes)
    {
        // Coalesce receive chunks off the UI thread and drain them on a bounded
        // cadence. Posting one UI update per chunk cannot keep up at high baud
        // rates; batching keeps layout cost per frame, not per read.
        bool schedule;
        lock (view.PendingLock)
        {
            view.PendingChunks.Add(bytes);
            view.PendingBytes += bytes.Length;

            // Bounded: if the UI thread stalls (a modal, a drag, a slow paint) the
            // queue used to grow at line rate with no ceiling.
            while (view.PendingBytes > MaxPendingBytes && view.PendingChunks.Count > 1)
            {
                view.PendingBytes -= view.PendingChunks[0].Length;
                view.DroppedBytes += view.PendingChunks[0].Length;
                view.PendingChunks.RemoveAt(0);
            }
            schedule = !view.DrainScheduled;
            view.DrainScheduled = true;
        }
        if (schedule) ScheduleDrain(view);
    }

    private void ScheduleDrain(TerminalView view)
    {
        var since = (DateTime.UtcNow - view.LastDrainAt).TotalMilliseconds;
        if (since >= MinDrainIntervalMs) Dispatcher.UIThread.Post(() => DrainPending(view), DispatcherPriority.Background);
        else DispatcherTimer.RunOnce(() => DrainPending(view), TimeSpan.FromMilliseconds(MinDrainIntervalMs - since), DispatcherPriority.Background);
    }

    private void DrainPending(TerminalView view)
    {
        ReadOnlyMemory<byte>[] chunks;
        long dropped;
        lock (view.PendingLock)
        {
            view.DrainScheduled = false;
            dropped = view.DroppedBytes;
            view.DroppedBytes = 0;
            if (view.PendingChunks.Count == 0) return;
            chunks = view.PendingChunks.ToArray();
            view.PendingChunks.Clear();
            view.PendingBytes = 0;
        }
        view.LastDrainAt = DateTime.UtcNow;

        if (dropped > 0) AppendText(view, $"\r\n[{dropped:N0} bytes dropped - the display could not keep up]\r\n", ErrorColor);

        if (view.VtMode)
        {
            // Following is handled from the ScrollChanged event of the ScrollViewer,
            // once the new text has been measured; a scroll here would use the old
            // extent and would also ignore a user who has scrolled up to read.
            foreach (var chunk in chunks) view.Vt.ProcessBytes(chunk.Span);
            return;
        }

        var total = 0;
        foreach (var chunk in chunks) total += chunk.Length;
        if (total == 0) return;
        if (chunks.Length == 1) { AppendBytes(view, chunks[0].Span); return; }

        var combined = new byte[total];
        var offset = 0;
        foreach (var chunk in chunks) { chunk.Span.CopyTo(combined.AsSpan(offset)); offset += chunk.Length; }
        AppendBytes(view, combined);
    }

    private readonly List<LogSegment> _segments = new();

    private void AppendBytes(TerminalView view, ReadOnlySpan<byte> bytes)
    {
        _segments.Clear();
        view.Formatter.Format(bytes, _segments);
        if (_segments.Count == 0) return;

        // The file log always records received data; pausing only stops the display.
        if (view.Writer is { } writer)
        {
            foreach (var segment in _segments) writer.Write(segment.Text);
        }
        if (view.PauseDisplay) return;
        view.Log.AppendSegments(_segments);
    }

    private void AppendText(TerminalView view, string text, string? color)
    {
        var brush = color is null ? null : GetBrush(color);
        view.Log.Append(text, brush);
        // The plain log is hidden in VT100 mode; without this an open error, a lost
        // connection or a send failure was invisible there.
        if (view.VtMode) view.Vt.AppendNotice(text, brush);
        view.Writer?.Write(text);
    }

    private void AppendStatus(TerminalView view, StatusLevel level, string message)
    {
        var color = level switch
        {
            StatusLevel.Success => "#2E7D32",
            StatusLevel.Warning => "#B26A00",
            StatusLevel.Error => "#C62828",
            _ => null,
        };
        AppendText(view, $"\r\n[{message}]\r\n", color);
    }

    private void RecordSerialError(TerminalView view, SerialError error)
    {
        switch (error)
        {
            case SerialError.Frame: view.FramingErrors++; break;
            case SerialError.Overrun: case SerialError.RXOver: view.OverrunErrors++; break;
            case SerialError.RXParity: view.ParityErrors++; break;
        }
        UpdateCounters(view);
    }

    private void UpdateCounters(TerminalView view)
    {
        var rx = view.Session.BytesReceivedCount;
        var tx = view.Session.BytesSentCount;
        var now = DateTime.UtcNow;
        var elapsed = (now - view.LastSampleAt).TotalSeconds;
        if (elapsed >= 0.5)
        {
            view.RxRate = (rx - view.LastRxSample) / elapsed;
            view.TxRate = (tx - view.LastTxSample) / elapsed;
            view.LastRxSample = rx; view.LastTxSample = tx; view.LastSampleAt = now;
        }

        var errors = view.FramingErrors + view.OverrunErrors + view.ParityErrors;
        var text = $"RX {Human(rx)} ({Human(view.RxRate)}/s)   TX {Human(tx)}";
        // A UDP server replies to whichever host last sent a datagram; unlike the TCP
        // server tab there is no client list, so name the target rather than leave it implicit.
        if (view.Kind == TransportKind.UdpServer && view.Session.IsOpen)
            text += $"   reply to: {view.Session.UdpPeerDescription ?? "(nothing received yet)"}";
        // An unexplained framing-error count is the diagnostic for a wrong baud rate.
        if (errors > 0) text += $"   errors: {view.FramingErrors} framing, {view.OverrunErrors} overrun, {view.ParityErrors} parity";
        view.Counters.Text = text;
        view.Counters.Foreground = GetBrush(errors > 0 ? "#B26A00" : MutedColor);
    }

    private static string Human(double value) => value switch
    {
        >= 1024 * 1024 => $"{value / (1024 * 1024):0.0} MB",
        >= 1024 => $"{value / 1024:0.0} kB",
        _ => $"{value:0} B",
    };

    /* ---------------- follow / scroll ---------------- */

    private static void FollowLiveOutput(TerminalView view)
    {
        if (view.VtMode) { view.VtFollowing = true; ScrollVtToEnd(view); }
        else view.Log.ScrollToEnd();
        UpdateFollowStatus(view);
    }

    private static void ScrollVtToEnd(TerminalView view)
    {
        var scroll = view.VtScroll;
        // Vertical only: ScrollToEnd() also slams the horizontal offset back to 0,
        // which makes the right-hand side of long lines unreadable while data flows.
        scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
    }

    private static void UpdateVtFollow(TerminalView view)
    {
        var scroll = view.VtScroll;
        var distance = scroll.Extent.Height - scroll.Offset.Y - scroll.Viewport.Height;
        view.VtFollowing = distance <= 16;
        UpdateFollowStatus(view);
    }

    private static void UpdateFollowStatus(TerminalView view)
    {
        var following = view.VtMode ? view.VtFollowing : view.Log.AutoScroll;
        view.FollowStatus.Text = following ? "● Following live output" : "‖ Auto-scroll paused";
        view.FollowStatus.Foreground = GetBrush(following ? "#2E7D32" : "#B26A00");
        view.JumpToLive.IsVisible = !following;
    }

    /* ---------------- connection state ---------------- */

    private static SerialSettings ReadSerialSettings(TerminalView view)
    {
        var baudText = ComboValue(view.Baud);
        if (!int.TryParse(baudText, out var baud) || baud <= 0) throw new InvalidOperationException($"'{baudText}' is not a valid baud rate.");
        return new SerialSettings
        {
            Baud = baud,
            DataBits = int.TryParse(ComboValue(view.DataBits), out var bits) ? bits : 8,
            Parity = view.Parity.SelectedIndex switch { 1 => Parity.Odd, 2 => Parity.Even, 3 => Parity.Mark, 4 => Parity.Space, _ => Parity.None },
            StopBits = view.StopBits.SelectedIndex switch { 1 => StopBits.OnePointFive, 2 => StopBits.Two, _ => StopBits.One },
            Handshake = view.FlowControl.SelectedIndex switch { 1 => Handshake.RequestToSend, 2 => Handshake.XOnXOff, 3 => Handshake.RequestToSendXOnXOff, _ => Handshake.None },
        };
    }

    private async Task CloseSessionAsync(TerminalView view, string message)
    {
        DrainPending(view);
        await view.Session.CloseAsync();
        AppendText(view, $"\r\n[{message}]\r\n", ErrorColor);
        EndLogging(view);
        SetConnectionState(view, false);
    }

    private void SetConnectionState(TerminalView view, bool connected)
    {
        var busy = view.Connecting || view.Reconnecting;
        view.OpenButton.IsEnabled = !connected && !busy;
        // Close stays live while reconnecting so the retry loop can be abandoned.
        view.CloseButton.IsEnabled = connected || busy;
        if (view.PortList is not null) view.PortList.IsEnabled = !connected && !busy;
        foreach (var control in new Control[] { view.Baud, view.DataBits, view.Parity, view.StopBits, view.FlowControl, view.Address, view.Port })
            control.IsEnabled = !connected && !busy;

        foreach (var row in view.Rows) if (row.SendButton is not null) row.SendButton.IsEnabled = connected;
        view.SendBox.IsEnabled = connected;
        view.SendButton.IsEnabled = connected;
        view.RepeatToggle.IsEnabled = connected;
        if (view.BreakButton is not null) view.BreakButton.IsEnabled = connected;
        if (view.ResetButton is not null) view.ResetButton.IsEnabled = connected;
        // Setting RtsEnable throws once the handshake owns the line.
        view.Rts.IsEnabled = !connected || !view.Session.HandshakeOwnsRts;
        // CTS/DSR are only polled while open; without this they kept showing the last
        // reading after the port closed, as if the far end were still asserting them.
        if (!connected) foreach (var signal in view.Signals) SetSignal(signal, false);

        var marker = connected ? "● " : view.Reconnecting ? "○ " : "";
        view.Tab.Header = marker + view.Title;
        UpdateWindowTitle();
    }

    private void UpdateWindowTitle()
    {
        var view = Active;
        if (view is null || !view.Session.IsOpen) { Title = $"BaudRunner {AppVersion} - native serial & network terminal"; return; }
        var where = view.Kind == TransportKind.Serial
            ? $"{SelectedPortName(view.PortList)} @ {ComboValue(view.Baud)}"
            : $"{(view.Kind is TransportKind.TcpServer or TransportKind.UdpServer ? "port " : "")}{(view.Kind is TransportKind.TcpServer or TransportKind.UdpServer ? view.Port.Text : $"{view.Address.Text}:{view.Port.Text}")}";
        Title = $"{where} - {view.Title} - BaudRunner {AppVersion}";
    }

    private void StartReconnect(TerminalView view, Func<Task> connect)
    {
        if (_reconnects.ContainsKey(view)) return;
        var cancellation = new CancellationTokenSource();
        _reconnects[view] = cancellation;
        view.Reconnecting = true;
        SetConnectionState(view, false);
        _ = ReconnectLoop(view, connect, cancellation.Token);
    }

    private async Task ReconnectLoop(TerminalView view, Func<Task> connect, CancellationToken token)
    {
        try
        {
            while (view.OpenRequested && view.AutoReconnect.IsChecked == true && !token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                if (!view.OpenRequested || view.Session.IsOpen) break;

                // For serial, wait for the port name to come back rather than spamming
                // failures at a board that is mid-reflash.
                if (view.Kind == TransportKind.Serial && SelectedPortName(view.PortList) is { } name && !PortIsPresent(name)) continue;

                try { await connect(); AppendText(view, "\r\n[Reconnected]\r\n", "#2E7D32"); break; }
                catch (Exception ex) { AppendText(view, $"\r\n[Reconnect failed: {ex.Message}]\r\n", ErrorColor); }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Only the loop that still owns the registration may clear the flag. A
            // StopReconnect followed by a fresh StartReconnect inside the two-second
            // delay otherwise had the old loop re-enable Open underneath the new one.
            if (!_reconnects.TryGetValue(view, out var current) || current.Token == token)
            {
                if (current is not null) { _reconnects.Remove(view); current.Dispose(); }
                view.Reconnecting = false;
                SetConnectionState(view, view.Session.IsOpen);
            }
        }
    }

    /// <summary>Enumeration can fail transiently; that reads as "not yet", which keeps the loop waiting instead of killing it.</summary>
    private static bool PortIsPresent(string name)
    {
        try { return SerialPort.GetPortNames().Contains(name, StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private void StopReconnect(TerminalView view)
    {
        // Cancel and dispose here: removing the entry first made the loop's cleanup
        // miss its own registration and leak the token source.
        if (!_reconnects.Remove(view, out var cancellation)) return;
        view.Reconnecting = false;
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        cancellation.Dispose();
    }

    /* ---------------- logging ---------------- */

    private void BeginLogging(TerminalView view)
    {
        if (view.Writer is not null) return;
        try
        {
            var encoding = new UTF8Encoding(false);
            var writer = new LogWriter(_logDirectory, view.Title, encoding);
            if (writer.HasFailed)
            {
                // The Failed event raised inside the constructor fired before anything could listen.
                writer.Dispose();
                AppendText(view, $"\r\n[Log error: no writable log file under {_logDirectory}. Logging is off; the session continues.]\r\n", ErrorColor);
                return;
            }
            writer.Failed += reason => Dispatcher.UIThread.Post(() =>
            {
                AppendText(view, $"\r\n[Log error: {reason}. Logging has stopped; the session continues.]\r\n", ErrorColor);
                view.Writer = null;
            });
            writer.Rolled += path => Dispatcher.UIThread.Post(() => { view.LastLogPath = path; AppendText(view, $"\r\n[Log rolled to {Path.GetFileName(path)}]\r\n", null); });
            view.Writer = writer;
            view.LastLogPath = writer.FilePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendText(view, $"\r\n[Log error: {ex.Message}]\r\n", ErrorColor);
        }
    }

    private void EndLogging(TerminalView view)
    {
        var writer = view.Writer;
        view.Writer = null;
        try { writer?.Dispose(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private async Task SaveTextAsync(TerminalView view, string text, string what)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = $"Save {what}",
                SuggestedFileName = $"BaudRunner-{view.Title}-{DateTime.Now:yyyyMMdd-HHmmss}.log",
                DefaultExtension = "log",
            });
            if (file is null) return;
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(text);
            AppendText(view, $"\r\n[Saved {what} to {file.Name}]\r\n", "#2E7D32");
        }
        catch (Exception ex)
        {
            await ShowMessage("Save failed", ex.Message);
        }
    }

    private void OpenLogFile(TerminalView view)
    {
        // Driven by the last known path, not by "a writer is currently open", so the
        // item still works after the connection closes - which is when it is wanted.
        if (view.LastLogPath is not { } path || !File.Exists(path)) return;
        try
        {
            if (OperatingSystem.IsWindows()) StartExternal(FindNotepadPlusPlus() ?? "notepad.exe", path);
            else if (OperatingSystem.IsMacOS()) StartExternal("open", path);
            else StartExternal("xdg-open", path);
        }
        catch (Exception ex) { _ = ShowMessage("Open log", $"Could not open the log file:\r\n{ex.Message}"); }
    }

    private void RevealLogFile(TerminalView view)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_logDirectory);
            var logPath = view.LastLogPath is { } path && File.Exists(path) ? path : null;
            if (OperatingSystem.IsWindows())
            {
                // explorer wants /select,"<path>" as one raw argument string: passing it
                // through ArgumentList quotes the whole token and explorer ignores it.
                if (logPath is not null) Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{logPath}\"", UseShellExecute = false });
                else StartExternal("explorer.exe", _logDirectory);
            }
            else if (OperatingSystem.IsLinux())
            {
                if (logPath is not null) RevealLinuxFile(logPath);
                else StartExternal("xdg-open", _logDirectory);
            }
            else StartExternal("open", _logDirectory);
        }
        catch (Exception ex) { _ = ShowMessage("Open log folder", $"Could not open the log folder:\r\n{ex.Message}"); }
    }

    private static void RevealLinuxFile(string path)
    {
        var fileManager = new[] { "nautilus", "dolphin", "nemo", "thunar" }.Select(ResolveCommand).FirstOrDefault(command => command is not null);
        if (fileManager is null) { StartExternal("xdg-open", Path.GetDirectoryName(path)!); return; }
        if (fileManager.EndsWith("nautilus", StringComparison.OrdinalIgnoreCase) || fileManager.EndsWith("dolphin", StringComparison.OrdinalIgnoreCase))
            StartExternal(fileManager, "--select", path);
        else StartExternal(fileManager, path);
    }

    private static string? FindNotepadPlusPlus()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Notepad++", "notepad++.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Notepad++", "notepad++.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Notepad++", "notepad++.exe"),
            ResolveCommand("notepad++.exe"),
        };
        return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
    }

    private static string? ResolveCommand(string command)
    {
        if (Path.IsPathRooted(command)) return File.Exists(command) ? command : null;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, command))
            .FirstOrDefault(File.Exists);
    }

    private static void StartExternal(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        Process.Start(startInfo);
    }

    /* ---------------- serial port enumeration ---------------- */

    private static string? SelectedPortName(ComboBox? list) => (list?.SelectedItem as SerialPortOption)?.Name;

    private void ScanForPortChanges()
    {
        string[] current;
        try { current = SerialPort.GetPortNames(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        Array.Sort(current, StringComparer.OrdinalIgnoreCase);
        if (current.SequenceEqual(_knownPorts, StringComparer.OrdinalIgnoreCase)) return;
        _knownPorts = current;
        foreach (var view in _views)
        {
            if (view.PortList is { IsDropDownOpen: false } list && !view.Session.IsOpen) RefreshSerialPorts(list, SelectedPortName(list));
        }
    }

    private static async void RefreshSerialPorts(ComboBox list, string? preferred)
    {
        // Populate immediately from the cheap port-name enumeration plus cached
        // descriptions; the WMI query is slow (hundreds of ms) so it runs in the
        // background and re-applies only when it learned something new.
        try
        {
            ApplySerialPorts(list, preferred);
            if (!OperatingSystem.IsWindows()) return;
            var fresh = await Task.Run(SerialPortDescriptions.QueryWindows);
            if (!SerialPortDescriptions.Merge(fresh)) return;
            // Rebuilding Items under an open drop-down resets the highlighted entry.
            if (list.IsDropDownOpen) return;
            ApplySerialPorts(list, SelectedPortName(list));
        }
        catch (Exception) { }
    }

    private static void ApplySerialPorts(ComboBox list, string? preferred)
    {
        var current = preferred ?? SelectedPortName(list);
        var ports = SerialPortDescriptions.Enumerate();
        // A saved port that is not present is offered, but labelled, so a failed Open
        // is not the first sign that the adapter is unplugged.
        if (!string.IsNullOrWhiteSpace(current) && ports.All(port => !string.Equals(port.Name, current, StringComparison.OrdinalIgnoreCase)))
            ports.Insert(0, new SerialPortOption(current, $"{current}  (not connected)"));

        list.Items.Clear();
        foreach (var port in ports) list.Items.Add(port);
        if (ports.Count > 0) list.SelectedItem = ports.FirstOrDefault(port => string.Equals(port.Name, current, StringComparison.OrdinalIgnoreCase)) ?? ports[0];
    }

    private static void UpdateTcpClients(TerminalView view, IReadOnlyList<TcpClientInfo> clients)
    {
        if (view.TcpClients is null) return;
        var selected = view.SelectedClientId;
        view.TcpClients.Items.Clear();
        foreach (var client in clients) view.TcpClients.Items.Add(client);

        // Never silently re-target: sending a command to a different board because the
        // previous one dropped is worse than refusing to send.
        var stillPresent = clients.FirstOrDefault(client => client.Id == selected);
        view.SelectedClientId = stillPresent?.Id;
        view.TcpClients.SelectedItem = stillPresent;
        if (view.DisconnectClient is not null) view.DisconnectClient.IsEnabled = view.SelectedClientId is not null;
        if (view.ClientCountLabel is not null) view.ClientCountLabel.Text = $"Connected TCP clients {clients.Count}/{TransportSession.MaxClients} (select a target):";
    }

    /* ---------------- context menu ---------------- */

    private ContextMenu BuildLogContextMenu(TerminalView view, Func<bool> hasSelection, Action copySelection, Action? paste = null)
    {
        var menu = new ContextMenu();
        // Clear first: on a live terminal it is the item reached for most often.
        var clearItem = new MenuItem { Header = "Clear log", InputGesture = new KeyGesture(Key.L, KeyModifiers.Control) };
        clearItem.Click += (_, _) => ClearView(view);
        menu.Items.Add(clearItem); menu.Items.Add(new Separator());

        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) => copySelection();
        var saveSelection = new MenuItem { Header = "Save selection as..." };
        saveSelection.Click += (_, _) => _ = SaveTextAsync(view, view.VtMode ? view.Vt.SelectedText : view.Log.SelectedText, "selection");
        var copySeparator = new Separator();
        menu.Items.Add(copy); menu.Items.Add(saveSelection); menu.Items.Add(copySeparator);

        // Only the terminal pastes: the plain log is read-only, and there the send box is the place to type.
        MenuItem? pasteItem = null;
        if (paste is not null)
        {
            pasteItem = new MenuItem { Header = "Paste", InputGesture = new KeyGesture(Key.V, KeyModifiers.Control | KeyModifiers.Shift) };
            pasteItem.Click += (_, _) => paste();
            menu.Items.Add(pasteItem); menu.Items.Add(new Separator());
        }

        var display = new MenuItem { Header = "Display format" };
        var displayOptions = new List<(string Name, int Index)> { ("Normal", 0), ("Hex (all bytes)", 1), ("Hex (except CR/LF)", 2), ("ASCII only", 3) };
        if (view.Kind == TransportKind.Serial) displayOptions.Add(("VT100 terminal", 4));
        foreach (var (option, index) in displayOptions)
        {
            var item = new MenuItem { Header = option, ToggleType = MenuItemToggleType.Radio };
            item.Click += (_, _) => view.Display.SelectedIndex = index;
            display.Items.Add(item);
        }
        menu.Items.Add(display);

        var ansi = new MenuItem { Header = "ANSI colour" };
        foreach (var (name, mode) in new[] { ("Interpret", AnsiMode.Interpret), ("Strip", AnsiMode.Strip), ("Show raw", AnsiMode.Raw) })
        {
            var item = new MenuItem { Header = name, ToggleType = MenuItemToggleType.Radio };
            item.Click += (_, _) => { view.Formatter.Ansi = mode; UpdateDisplayStatus(view); RequestConfigSave(); };
            ansi.Items.Add(item);
        }
        menu.Items.Add(ansi);

        var stamps = new MenuItem { Header = "Timestamps" };
        foreach (var (name, mode) in new[] { ("Off", TimestampMode.Off), ("Time of day", TimestampMode.Wall), ("Since connect", TimestampMode.SinceOpen), ("Delta between lines", TimestampMode.Delta) })
        {
            var item = new MenuItem { Header = name, ToggleType = MenuItemToggleType.Radio };
            item.Click += (_, _) => { view.Formatter.Timestamps = mode; UpdateDisplayStatus(view); RequestConfigSave(); };
            stamps.Items.Add(item);
        }
        menu.Items.Add(stamps);

        var encoding = new MenuItem { Header = "Text encoding" };
        foreach (var (name, mode) in new[] { ("Latin-1 / raw bytes", ReceiveEncoding.Latin1), ("UTF-8", ReceiveEncoding.Utf8) })
        {
            var item = new MenuItem { Header = name, ToggleType = MenuItemToggleType.Radio };
            item.Click += (_, _) => { view.Formatter.Encoding = mode; view.Vt.Encoding = mode; UpdateDisplayStatus(view); RequestConfigSave(); };
            encoding.Items.Add(item);
        }
        menu.Items.Add(encoding);

        var pause = new MenuItem { Header = "Pause display", ToggleType = MenuItemToggleType.CheckBox };
        pause.Click += (_, _) => { view.PauseDisplay = !view.PauseDisplay; view.Vt.Paused = view.PauseDisplay; UpdateDisplayStatus(view); RequestConfigSave(); };
        var echo = new MenuItem { Header = "Echo sent commands", ToggleType = MenuItemToggleType.CheckBox };
        echo.Click += (_, _) => { view.LocalEcho = !view.LocalEcho; RequestConfigSave(); };
        var find = new MenuItem { Header = "Find in log...", InputGesture = new KeyGesture(Key.F, KeyModifiers.Control) };
        find.Click += (_, _) => ShowFind(view);
        var openLog = new MenuItem { Header = "Open log file" };
        openLog.Click += (_, _) => OpenLogFile(view);
        var revealLog = new MenuItem { Header = "Open log folder" };
        revealLog.Click += (_, _) => RevealLogFile(view);

        menu.Items.Add(pause); menu.Items.Add(echo); menu.Items.Add(new Separator());
        menu.Items.Add(find); menu.Items.Add(openLog); menu.Items.Add(revealLog);

        menu.Opening += (_, _) =>
        {
            copy.IsVisible = hasSelection();
            saveSelection.IsVisible = hasSelection();
            copySeparator.IsVisible = hasSelection();
            if (pasteItem is not null) pasteItem.IsEnabled = view.Session.IsOpen;
            Check(display, item => displayOptions.FirstOrDefault(option => option.Name == item).Index == view.Display.SelectedIndex && displayOptions.Any(option => option.Name == item));
            Check(ansi, item => item == view.Formatter.Ansi switch { AnsiMode.Interpret => "Interpret", AnsiMode.Strip => "Strip", _ => "Show raw" });
            Check(stamps, item => item == view.Formatter.Timestamps switch { TimestampMode.Off => "Off", TimestampMode.Wall => "Time of day", TimestampMode.SinceOpen => "Since connect", _ => "Delta between lines" });
            Check(encoding, item => item == (view.Formatter.Encoding == ReceiveEncoding.Utf8 ? "UTF-8" : "Latin-1 / raw bytes"));
            pause.IsChecked = view.PauseDisplay;
            echo.IsChecked = view.LocalEcho;
            openLog.IsEnabled = view.LastLogPath is { } path && File.Exists(path);
            echo.IsEnabled = !view.VtMode;
            find.IsEnabled = !view.VtMode;
            UpdateDisplayStatus(view);
        };
        return menu;
    }

    private static void Check(MenuItem parent, Func<string, bool> isChecked)
    {
        foreach (var item in parent.Items.OfType<MenuItem>()) item.IsChecked = isChecked(item.Header?.ToString() ?? "");
    }

    private static void UpdateDisplayStatus(TerminalView view)
    {
        var stamps = view.Formatter.Timestamps switch
        {
            TimestampMode.Off => "Off",
            TimestampMode.Wall => "Time",
            TimestampMode.SinceOpen => "Since connect",
            _ => "Delta",
        };
        view.ModeLabel.Text = $"Mode: {view.Display.SelectedItem}  |  ANSI: {view.Formatter.Ansi}  |  Timestamps: {stamps}  |  Paused: {(view.PauseDisplay ? "Yes" : "No")}";
    }

    private void ClearView(TerminalView view)
    {
        view.Log.Clear();
        view.Vt.Clear();
        view.Formatter.ResetStream(restartClock: false);
        UpdateFollowStatus(view);
    }

    /* ---------------- small helpers ---------------- */

    private static ComboBox Combo(string[] items)
    {
        var combo = new ComboBox();
        foreach (var item in items) combo.Items.Add(item);
        return combo;
    }

    private static string ComboValue(ComboBox combo) => combo.SelectedItem?.ToString() ?? "";
    private static string ComboValue(AutoCompleteBox box) => (box.Text ?? "").Trim();

    private static void SetComboValue(ComboBox combo, string? value, string fallback)
    {
        var match = combo.Items.Cast<object>().FirstOrDefault(item => string.Equals(item.ToString(), value, StringComparison.OrdinalIgnoreCase));
        combo.SelectedItem = match
            ?? combo.Items.Cast<object>().FirstOrDefault(item => string.Equals(item.ToString(), fallback, StringComparison.OrdinalIgnoreCase))
            ?? combo.Items.Cast<object>().FirstOrDefault();
    }

    private bool IsLightTheme => Application.Current?.RequestedThemeVariant == ThemeVariant.Light;

    /// <summary>Secondary text: field captions, hints, counters.</summary>
    private string MutedColor => IsLightTheme ? "#52606D" : "#8BA4BB";

    private Border Field(string label, Control control, List<TextBlock> register)
    {
        var caption = new TextBlock { Text = label, FontSize = 11, Foreground = GetBrush(MutedColor) };
        register.Add(caption);
        return new Border
        {
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(0, 0, 0, 2),
            Child = new StackPanel { Spacing = 3, Children = { caption, control } },
        };
    }

    private Border SignalIndicator(string label, bool visible) => new()
    {
        IsVisible = visible,
        Tag = false,
        Background = new SolidColorBrush(Color.Parse(IsLightTheme ? "#E1E5E9" : "#303640")),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8, 3),
        Child = new TextBlock { Text = $"{label}: off", Foreground = new SolidColorBrush(Color.Parse(IsLightTheme ? "#52606D" : "#B8C2CC")) },
    };

    private void SetSignal(Border indicator, bool asserted)
    {
        indicator.Tag = asserted;
        indicator.Background = new SolidColorBrush(Color.Parse(asserted ? (IsLightTheme ? "#B7E4C7" : "#246B45") : (IsLightTheme ? "#E1E5E9" : "#303640")));
        if (indicator.Child is not TextBlock text) return;
        var label = text.Text?.Split(':')[0] ?? "Signal";
        text.Text = $"{label}: {(asserted ? "on" : "off")}";
        text.Foreground = new SolidColorBrush(Color.Parse(asserted ? (IsLightTheme ? "#14532D" : "#D8FFE8") : (IsLightTheme ? "#52606D" : "#B8C2CC")));
    }

    private static MenuItem MenuButton(string header, Action handler, KeyGesture? gesture = null)
    {
        var item = new MenuItem { Header = header };
        if (gesture is not null) item.InputGesture = gesture;
        item.Click += (_, _) => handler();
        return item;
    }

    private static IBrush GetBrush(string color)
    {
        if (!_brushCache.TryGetValue(color, out var brush)) { brush = new SolidColorBrush(Color.Parse(color)); _brushCache[color] = brush; }
        return brush;
    }

    private void SetTheme(ThemeVariant variant)
    {
        Application.Current!.RequestedThemeVariant = variant;
        _config.Theme = variant == ThemeVariant.Light ? "Light" : "Dark";
        Background = new SolidColorBrush(Color.Parse(IsLightTheme ? "#F3F5F7" : "#11151B"));
        if (_mainMenu is not null) _mainMenu.Background = new SolidColorBrush(Color.Parse(IsLightTheme ? "#E3E7EB" : "#191F27"));
        foreach (var view in _views)
        {
            view.Log.SetTheme(IsLightTheme);
            view.Vt.SetTheme(IsLightTheme);
            view.Formatter.TimestampBrush = GetBrush(IsLightTheme ? "#6B7280" : "#7C8CA0");
            view.Footer.Background = GetBrush(IsLightTheme ? "#E8EDF1" : "#191F27");
            view.FindBar.Background = GetBrush(IsLightTheme ? "#E8EDF1" : "#191F27");
            view.ModeLabel.Foreground = GetBrush(IsLightTheme ? "#52606D" : "#B8C2CC");
            // Captions and hints used to keep the colour of whichever theme built them.
            foreach (var label in view.MutedLabels) label.Foreground = GetBrush(MutedColor);
            view.FindStatus.Foreground = GetBrush(view.FindPatternInvalid ? ErrorColor : MutedColor);
            foreach (var signal in view.Signals) SetSignal(signal, signal.Tag is true);
            UpdateCounters(view);
        }
        RequestConfigSave();
    }

    private async Task ShowMessage(string title, string message)
    {
        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        var box = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 12,
                Children = { new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, ok },
            },
        };
        ok.Click += (_, _) => box.Close();
        await box.ShowDialog(this);
    }

    /* ---------------- geometry, config, shutdown ---------------- */

    private void RestoreGeometry()
    {
        Width = Math.Max(MinWidth, _config.Window.Width);
        Height = Math.Max(MinHeight, _config.Window.Height);
        if (!double.IsNaN(_config.Window.X) && !double.IsNaN(_config.Window.Y))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)_config.Window.X, (int)_config.Window.Y);
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (_config.Window.Maximized) WindowState = Avalonia.Controls.WindowState.Maximized;
    }

    private void ClampToVisibleScreen()
    {
        // An unplugged second monitor would otherwise leave the window off-screen.
        if (WindowStartupLocation != WindowStartupLocation.Manual) return;
        var bounds = new PixelRect(Position, new PixelSize((int)Math.Max(1, Width), (int)Math.Max(1, Height)));
        if (Screens.All.Any(screen => screen.WorkingArea.Intersects(bounds))) return;
        var primary = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (primary is null) return;
        Position = new PixelPoint(
            primary.WorkingArea.X + Math.Max(0, (primary.WorkingArea.Width - (int)Width) / 2),
            primary.WorkingArea.Y + Math.Max(0, (primary.WorkingArea.Height - (int)Height) / 2));
    }

    private void RequestConfigSave()
    {
        // Debounced: the settings used to be written only at Closing, so a crash or a
        // taskkill discarded every command typed during the session.
        _configSaveTimer.Stop();
        _configSaveTimer.Start();
    }

    private void PersistConfig()
    {
        if (_skipConfigSave) return;
        CaptureState();
        _config.Save();
    }

    private void CaptureState()
    {
        foreach (var view in _views) _config.Terminals[view.Title] = ToConfig(view);
        _config.SelectedTab = Active?.Title ?? _config.SelectedTab;
        if (WindowState == Avalonia.Controls.WindowState.Normal)
        {
            _config.Window.Width = Width; _config.Window.Height = Height;
            _config.Window.X = Position.X; _config.Window.Y = Position.Y;
        }
        _config.Window.Maximized = WindowState == Avalonia.Controls.WindowState.Maximized;
    }

    private static TerminalConfig ToConfig(TerminalView view) => new()
    {
        Address = view.Kind == TransportKind.Serial ? SelectedPortName(view.PortList) ?? "" : view.Address.Text ?? "",
        Port = view.Port.Text ?? "5800",
        Baud = ComboValue(view.Baud),
        DataBits = ComboValue(view.DataBits),
        Parity = ComboValue(view.Parity),
        StopBits = ComboValue(view.StopBits),
        Handshake = ComboValue(view.FlowControl),
        DisplayMode = view.Display.SelectedIndex,
        Ansi = view.Formatter.Ansi,
        TimestampMode = view.Formatter.Timestamps,
        Encoding = view.Formatter.Encoding,
        Pause = view.PauseDisplay,
        AutoReconnect = view.AutoReconnect.IsChecked == true,
        LocalEcho = view.LocalEcho,
        SendEnding = (LineEnding)view.SendEnding.SelectedIndex,
        History = view.History.ToList(),
        Commands = view.Rows.Select(row => new CommandConfig
        {
            Text = row.Text?.Text ?? "",
            Hex = row.Hex?.IsChecked == true,
            Ending = (LineEnding)(row.Ending?.SelectedIndex ?? 0),
        }).ToList(),
    };

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shuttingDown) return;
        // Window.Closing is synchronous: an async handler returns at its first await
        // and the window closes, so the teardown has to cancel the close and re-issue it.
        e.Cancel = true;
        _shuttingDown = true;
        try { await ShutdownAsync(); }
        catch (Exception) { }
        Close();
    }

    private async Task ShutdownAsync()
    {
        _configSaveTimer.Stop();
        _statsTimer.Stop();
        _portScanTimer.Stop();
        if (!_skipConfigSave) { CaptureState(); _config.Save(); }

        foreach (var view in _views)
        {
            try
            {
                StopReconnect(view);
                StopRepeat(view);
                view.OpenRequested = false;
                DrainPending(view);
                await view.Session.CloseAsync();
                if (view.Writer is not null) AppendText(view, "\r\n[Connection closed]\r\n", ErrorColor);
                EndLogging(view);
            }
            catch (Exception) { }
        }
    }

}

public sealed class SerialPortOption
{
    public string Name { get; }
    private string Display { get; }
    public SerialPortOption(string name, string display) { Name = name; Display = display; }
    public override string ToString() => Display;
}
