using System.IO.Ports;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace BaudRunner;

public sealed class MainWindow : Window
{
    private static string FullVersion => typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "v2.0.0";
    private static string AppVersion => FullVersion.Split('+', 2)[0];
    private readonly Dictionary<TabItem, TerminalView> _views = new();
    private readonly Dictionary<TerminalView, CancellationTokenSource> _reconnects = new();
    private readonly string _logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BaudRunner", "logs");
    private readonly AppConfig _config;
    private readonly Dictionary<TextBlock, LogWriter> _logWriters = new();
    private Menu? _mainMenu;
    private TabControl? _tabs;
    private bool _skipConfigSave;

    public MainWindow()
    {
        _config = AppConfig.Load();
        Application.Current!.RequestedThemeVariant = string.Equals(_config.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Light : ThemeVariant.Dark;
        Title = $"BaudRunner {AppVersion} - native serial & network terminal";
        Width = 1380; Height = 880; MinWidth = 900; MinHeight = 600;
        Background = new SolidColorBrush(Color.Parse(IsLightTheme ? "#F3F5F7" : "#11151B"));
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://BaudRunner/Icon.png"))); } catch { }
        Content = BuildShell();
        Closing += async (_, _) => await ShutdownAsync();
    }

    private Control BuildShell()
    {
        var tabs = new TabControl { Margin = new Thickness(12) };
        _tabs = tabs;
        AddTerminal(tabs, "Serial", TransportKind.Serial);
        AddTerminal(tabs, "TCP Client", TransportKind.TcpClient);
        AddTerminal(tabs, "TCP Server", TransportKind.TcpServer);
        AddTerminal(tabs, "UDP Client", TransportKind.UdpClient);
        AddTerminal(tabs, "UDP Server", TransportKind.UdpServer);

        var menu = new Menu { Background = new SolidColorBrush(Color.Parse(IsLightTheme ? "#E3E7EB" : "#191F27")) };
        _mainMenu = menu;
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(MenuButton("Clear active log", (_, _) => { if (tabs.SelectedItem is TabItem t && _views.TryGetValue(t, out var view)) ClearView(view); }));
        file.Items.Add(new Separator());
        file.Items.Add(MenuButton("E_xit", (_, _) => Close()));
        file.Items.Add(MenuButton("Exit without save", (_, _) => { _skipConfigSave = true; Close(); }));
        var help = new MenuItem { Header = "_Help" };
        help.Items.Add(MenuButton("About BaudRunner", (_, _) => _ = ShowMessage("About BaudRunner", $"BaudRunner {AppVersion}\r\nBuild: {FullVersion}\r\nA native C# Avalonia terminal for Windows and Linux.")));
        var viewMenu = new MenuItem { Header = "_View" };
        var light = new MenuItem { Header = "Light mode" }; light.Click += (_, _) => SetTheme(ThemeVariant.Light);
        var dark = new MenuItem { Header = "Dark mode" }; dark.Click += (_, _) => SetTheme(ThemeVariant.Dark);
        viewMenu.Items.Add(light); viewMenu.Items.Add(dark);
        menu.Items.Add(file); menu.Items.Add(viewMenu); menu.Items.Add(help);
        KeyDown += HandleFunctionKey;
        DockPanel.SetDock(menu, Dock.Top);
        return new DockPanel { Children = { menu, tabs } };
    }

    private async void HandleFunctionKey(object? sender, KeyEventArgs e)
    {
        if (e.Key < Key.F1 || e.Key > Key.F12 || _tabs?.SelectedItem is not TabItem tab || !_views.TryGetValue(tab, out var view)) return;
        var index = (int)e.Key - (int)Key.F1;
        if (index >= view.Rows.Count || view.Rows[index].Send is not { } send) return;
        e.Handled = true;
        await send();
    }

    private void AddTerminal(TabControl tabs, string title, TransportKind kind)
    {
        var view = BuildTerminal(title, kind);
        _views.Add(view.Tab, view);
        tabs.Items.Add(view.Tab);
    }

    private TerminalView BuildTerminal(string title, TransportKind kind)
    {
        var saved = _config.Terminals.TryGetValue(title, out var value) ? value : new TerminalConfig();
        var session = new TransportSession(kind);
        var log = new TextBlock { TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"), FontSize = 13, Background = new SolidColorBrush(Color.Parse(IsLightTheme ? "#FFFFFF" : "#0B0E12")), Foreground = new SolidColorBrush(Color.Parse(IsLightTheme ? "#1F2937" : "#D6E2F0")), Padding = new Thickness(8) };
        ScrollViewer.SetVerticalScrollBarVisibility(log, ScrollBarVisibility.Auto); ScrollViewer.SetHorizontalScrollBarVisibility(log, ScrollBarVisibility.Auto);
        var display = kind == TransportKind.Serial ? Combo("Normal", "Hex (all bytes)", "Hex (except CR/LF)", "ASCII only", "VT1xx terminal") : Combo("Normal", "Hex (all bytes)", "Hex (except CR/LF)", "ASCII only"); display.SelectedIndex = Math.Clamp(saved.DisplayMode, 0, kind == TransportKind.Serial ? 4 : 3);
        var modeLabel = new TextBlock { Text = $"Mode: {display.SelectedItem}  |  Timestamps: {(saved.Timestamp ? "On" : "Off")}  |  Paused: {(saved.Pause ? "Yes" : "No")}", FontSize = 12, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse(IsLightTheme ? "#52606D" : "#B8C2CC")) };
        var tab = new TabItem { Header = title };
        var rows = new List<CommandRow>();
        var address = new TextBox { Text = saved.Address, Width = 160 };
        var remotePort = new TextBox { Text = saved.Port, Width = 88, Watermark = "5800" };
        var listenPort = new TextBox { Text = string.IsNullOrWhiteSpace(saved.Port) ? "5800" : saved.Port, Width = 88, Watermark = "5800" };
        var baud = Combo("9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600"); SelectOrDefault(baud, saved.Baud, "115200");
        var dataBits = Combo("5", "6", "7", "8"); SelectOrDefault(dataBits, saved.DataBits, "8");
        var parity = Combo("None", "Odd", "Even", "Mark", "Space"); SelectOrDefault(parity, saved.Parity, "None");
        var stopBits = Combo("One", "OnePointFive", "Two"); SelectOrDefault(stopBits, saved.StopBits, "One");
        var open = new Button { Content = "Open", Classes = { "accent" }, MinWidth = 72 };
        var close = new Button { Content = "Close", IsEnabled = false, MinWidth = 72 };
        var clear = new Button { Content = "Clear", MinWidth = 72 };
        var autoReconnect = new CheckBox { Content = "Auto-reconnect", IsVisible = kind == TransportKind.Serial, IsChecked = saved.AutoReconnect };
        var rts = new CheckBox { Content = "RTS", IsVisible = kind == TransportKind.Serial, IsChecked = saved.Rts };
        var dtr = new CheckBox { Content = "DTR", IsVisible = kind == TransportKind.Serial, IsChecked = saved.Dtr };
        var cts = SignalIndicator("CTS", kind == TransportKind.Serial);
        var dsr = SignalIndicator("DSR", kind == TransportKind.Serial);
        ComboBox? portList = null;
        ListBox? tcpClients = null;
        Button? disconnectClient = null;
        if (kind == TransportKind.Serial)
        {
            portList = new ComboBox { Width = 250, PlaceholderText = "Select a port" };
            RefreshSerialPorts(portList, saved.Address);
            portList.DropDownOpened += (_, _) => RefreshSerialPorts(portList, (portList.SelectedItem as SerialPortOption)?.Name);
        }

        var topControls = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6), VerticalAlignment = VerticalAlignment.Bottom };
        var bottomControls = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10), VerticalAlignment = VerticalAlignment.Bottom };
        var signalControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 10), VerticalAlignment = VerticalAlignment.Center, IsVisible = kind == TransportKind.Serial };
        signalControls.Children.Add(rts); signalControls.Children.Add(dtr); signalControls.Children.Add(cts); signalControls.Children.Add(dsr);
        topControls.Children.Add(new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 18, 14, 0), Children = { clear } });
        if (kind == TransportKind.Serial)
        {
            topControls.Children.Add(Field("Port", portList!));
            bottomControls.Children.Add(Field("Baud", baud)); bottomControls.Children.Add(Field("Data bits", dataBits));
            bottomControls.Children.Add(Field("Parity", parity)); bottomControls.Children.Add(Field("Stop bits", stopBits));
        }
        else if (kind == TransportKind.TcpServer || kind == TransportKind.UdpServer)
        {
            topControls.Children.Add(Field("Listen port", listenPort));
        }
        else
        {
            topControls.Children.Add(Field("Address", address)); topControls.Children.Add(Field("Remote port", remotePort));
        }
        topControls.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(10, 18, 0, 0), Children = { open, close, autoReconnect } });

        if (kind == TransportKind.TcpServer)
        {
            tcpClients = new ListBox { MinHeight = 105, MaxHeight = 140, SelectionMode = SelectionMode.Single };
            disconnectClient = new Button { Content = "Disconnect selected", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left };
        }

        var vtTerminal = new VtTerminalControl(); vtTerminal.SetTheme(IsLightTheme);
        var tabView = new TerminalView { Tab = tab, Session = session, Log = log, VtTerminal = vtTerminal, Display = display, ModeLabel = modeLabel, Address = address, Port = kind == TransportKind.TcpServer || kind == TransportKind.UdpServer ? listenPort : remotePort, PortList = portList, Baud = baud, DataBits = dataBits, Parity = parity, StopBits = stopBits, AutoReconnect = autoReconnect, Rts = rts, Dtr = dtr, Rows = rows, Kind = kind, VtMode = kind == TransportKind.Serial && display.SelectedIndex == 4, TimestampEnabled = saved.Timestamp, PauseDisplay = saved.Pause, TcpClients = tcpClients, DisconnectClient = disconnectClient, Signals = new[] { cts, dsr } };
        session.BytesReceived += bytes => Dispatcher.UIThread.Post(() => { if (tabView.VtMode) vtTerminal.ProcessBytes(bytes.Span); else AppendBytes(log, display.SelectedIndex, bytes.Span); });
        vtTerminal.SendBytes += bytes => _ = session.SendAsync(bytes);
        session.Status += message => Dispatcher.UIThread.Post(() => AppendStatus(log, message));
        session.LineStatusChanged += (ctsState, dsrState) => Dispatcher.UIThread.Post(() => { SetSignal(cts, ctsState); SetSignal(dsr, dsrState); });
        session.TcpClientsChanged += clients => Dispatcher.UIThread.Post(() => UpdateTcpClients(tabView, clients));
        session.ConnectionLost += () => Dispatcher.UIThread.Post(() => { open.IsEnabled = true; close.IsEnabled = false; if (portList is not null) portList.IsEnabled = true; EndLogging(log); AppendText(log, "\r\n[Connection closed]\r\n", "#E57373"); if (tabView.OpenRequested && tabView.AutoReconnect.IsChecked == true) { if (portList is not null) portList.IsEnabled = false; StartReconnect(tabView, Connect); } });
        var commands = BuildCommands(session, log, rows, () => tabView.SelectedClientId, kind == TransportKind.TcpServer, kind == TransportKind.Serial, () => tabView.VtMode);
        RestoreRows(rows, saved.Commands);
        var logContextMenu = BuildLogContextMenu(tabView);
        log.ContextMenu = logContextMenu;
        vtTerminal.ContextMenu = logContextMenu;
        var rightPane = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(topControls, Dock.Top); DockPanel.SetDock(bottomControls, Dock.Top); DockPanel.SetDock(signalControls, Dock.Top);
        rightPane.Children.Add(topControls); rightPane.Children.Add(bottomControls); rightPane.Children.Add(signalControls);
        if (kind == TransportKind.TcpServer)
        {
            var clientPanel = new StackPanel { Spacing = 6 };
            clientPanel.Children.Add(new TextBlock { Text = "Connected TCP clients (select a target):" });
            clientPanel.Children.Add(tcpClients!);
            clientPanel.Children.Add(disconnectClient!);
            var clientBorder = new Border { BorderBrush = new SolidColorBrush(Color.Parse("#59636F")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 10), Child = clientPanel };
            DockPanel.SetDock(clientBorder, Dock.Top);
            rightPane.Children.Add(clientBorder);
        }
        rightPane.Children.Add(commands);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*"), ColumnSpacing = 12 };
        var logScroll = new ScrollViewer { Content = log, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var vtScroll = new ScrollViewer { Content = vtTerminal, IsVisible = tabView.VtMode, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var logFooter = new Border { Background = new SolidColorBrush(Color.Parse(IsLightTheme ? "#E8EDF1" : "#191F27")), Padding = new Thickness(8, 5), Child = modeLabel };
        var viewHost = new Grid(); viewHost.Children.Add(logScroll); viewHost.Children.Add(vtScroll);
        var logPane = new DockPanel(); DockPanel.SetDock(logFooter, Dock.Bottom); logPane.Children.Add(logFooter); logPane.Children.Add(viewHost);
        grid.Children.Add(logPane); Grid.SetColumn(rightPane, 1); grid.Children.Add(rightPane); tab.Content = grid;

        async Task Connect()
        {
            var selectedAddress = kind == TransportKind.Serial ? (portList?.SelectedItem as SerialPortOption)?.Name ?? "" : address.Text ?? "";
            var portText = tabView.Port.Text ?? "";
            if (kind != TransportKind.Serial && (!int.TryParse(portText, out var port) || port is < 1 or > 65535)) throw new InvalidOperationException("Enter a valid port from 1 to 65535.");
            var serialBaud = int.Parse(baud.SelectedItem?.ToString() ?? "115200"); var bits = int.Parse(dataBits.SelectedItem?.ToString() ?? "8"); var parityValue = (Parity)parity.SelectedIndex; var stop = stopBits.SelectedIndex switch { 1 => StopBits.OnePointFive, 2 => StopBits.Two, _ => StopBits.One };
            await session.OpenAsync(selectedAddress, kind == TransportKind.Serial ? 0 : int.Parse(portText), serialBaud, bits, parityValue, stop); open.IsEnabled = false; close.IsEnabled = true; if (portList is not null) portList.IsEnabled = false; BeginLogging(log); if (kind == TransportKind.Serial) { session.SetRts(rts.IsChecked == true); session.SetDtr(dtr.IsChecked == true); }
        }
        open.Click += async (_, _) => { tabView.OpenRequested = true; try { await Connect(); } catch (Exception ex) { AppendText(log, $"\r\n[Open error: {ex.Message}]\r\n", "#E57373"); if (autoReconnect.IsChecked == true) StartReconnect(tabView, Connect); } };
        close.Click += async (_, _) => { tabView.OpenRequested = false; StopReconnect(tabView); await session.CloseAsync(); EndLogging(log); if (portList is not null) portList.IsEnabled = true; AppendText(log, "\r\n[Connection closed]\r\n", "#E57373"); open.IsEnabled = true; close.IsEnabled = false; };
        clear.Click += (_, _) => ClearLog(log);
        display.SelectionChanged += (_, _) => { tabView.VtMode = kind == TransportKind.Serial && display.SelectedIndex == 4; UpdateDisplayStatus(tabView); logScroll.IsVisible = !tabView.VtMode; vtScroll.IsVisible = tabView.VtMode; if (tabView.VtMode) vtTerminal.Focus(); };
        rts.IsCheckedChanged += (_, _) => session.SetRts(rts.IsChecked == true); dtr.IsCheckedChanged += (_, _) => session.SetDtr(dtr.IsChecked == true);
        if (tcpClients is not null) tcpClients.SelectionChanged += (_, _) => { tabView.SelectedClientId = (tcpClients.SelectedItem as TcpClientInfo)?.Id; if (disconnectClient is not null) disconnectClient.IsEnabled = tabView.SelectedClientId is not null; };
        if (disconnectClient is not null) disconnectClient.Click += (_, _) => { if (tabView.SelectedClientId is int id) session.DisconnectTcpClient(id); };
        return tabView;
    }

    private static ComboBox Combo(params string[] items) { var combo = new ComboBox(); foreach (var item in items) combo.Items.Add(item); return combo; }
    private static void SelectOrDefault(ComboBox combo, string? value, string fallback) => combo.SelectedItem = combo.Items.Cast<object>().Any(item => string.Equals(item.ToString(), value, StringComparison.OrdinalIgnoreCase)) ? value : fallback;
    private bool IsLightTheme => Application.Current?.RequestedThemeVariant == ThemeVariant.Light;
    private Border Field(string label, Control control) => new() { Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(0, 0, 0, 2), Child = new StackPanel { Spacing = 3, Children = { new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(IsLightTheme ? "#52606D" : "#8BA4BB")) }, control } } };
    private Border SignalIndicator(string label, bool visible) => new() { IsVisible = visible, Tag = false, Background = new SolidColorBrush(Color.Parse(IsLightTheme ? "#E1E5E9" : "#303640")), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3), Child = new TextBlock { Text = $"{label}: off", Foreground = new SolidColorBrush(Color.Parse(IsLightTheme ? "#52606D" : "#B8C2CC")) } };
    private void SetSignal(Border indicator, bool asserted) { indicator.Tag = asserted; indicator.Background = new SolidColorBrush(Color.Parse(asserted ? (IsLightTheme ? "#B7E4C7" : "#246B45") : (IsLightTheme ? "#E1E5E9" : "#303640"))); if (indicator.Child is TextBlock text) { var label = text.Text?.Split(':')[0] ?? "Signal"; text.Text = $"{label}: {(asserted ? "on" : "off")}"; text.Foreground = new SolidColorBrush(Color.Parse(asserted ? (IsLightTheme ? "#14532D" : "#D8FFE8") : (IsLightTheme ? "#52606D" : "#B8C2CC"))); } }
    private static MenuItem MenuButton(string header, Action<object?, EventArgs> handler) { var item = new MenuItem { Header = header }; item.Click += (_, _) => handler(item, EventArgs.Empty); return item; }
    private static void UpdateDisplayStatus(TerminalView view) => view.ModeLabel.Text = $"Mode: {view.Display.SelectedItem}  |  Timestamps: {(view.TimestampEnabled ? "On" : "Off")}  |  Paused: {(view.PauseDisplay ? "Yes" : "No")}";
    private void SetTheme(ThemeVariant variant)
    {
        Application.Current!.RequestedThemeVariant = variant; _config.Theme = variant == ThemeVariant.Light ? "Light" : "Dark";
        Background = new SolidColorBrush(Color.Parse(IsLightTheme ? "#F3F5F7" : "#11151B"));
        if (_mainMenu is not null) _mainMenu.Background = new SolidColorBrush(Color.Parse(IsLightTheme ? "#E3E7EB" : "#191F27"));
        foreach (var view in _views.Values) { view.Log.Background = new SolidColorBrush(Color.Parse(IsLightTheme ? "#FFFFFF" : "#0B0E12")); view.Log.Foreground = new SolidColorBrush(Color.Parse(IsLightTheme ? "#1F2937" : "#D6E2F0")); view.VtTerminal.SetTheme(IsLightTheme); foreach (var signal in view.Signals) SetSignal(signal, signal.Tag is true); }
    }

    private ContextMenu BuildLogContextMenu(TerminalView view)
    {
        var menu = new ContextMenu();
        var display = new MenuItem { Header = "Display format" };
        var displayOptions = new List<(string Name, int Index)> { ("Normal", 0), ("Hex (all bytes)", 1), ("Hex (except CR/LF)", 2), ("ASCII only", 3) };
        if (view.Kind == TransportKind.Serial) displayOptions.Add(("VT1xx terminal", 4));
        foreach (var (option, index) in displayOptions)
        {
            var item = new MenuItem { Header = option, ToggleType = MenuItemToggleType.CheckBox };
            item.Click += (_, _) => view.Display.SelectedIndex = index;
            display.Items.Add(item);
        }
        menu.Items.Add(display);
        var timestamp = new MenuItem { Header = "Timestamp received data", ToggleType = MenuItemToggleType.CheckBox, IsChecked = view.TimestampEnabled };
        timestamp.Click += (_, _) => { view.TimestampEnabled = !view.TimestampEnabled; timestamp.IsChecked = view.TimestampEnabled; UpdateDisplayStatus(view); };
        var pause = new MenuItem { Header = "Pause display", ToggleType = MenuItemToggleType.CheckBox, IsChecked = view.PauseDisplay };
        pause.Click += (_, _) => { view.PauseDisplay = !view.PauseDisplay; pause.IsChecked = view.PauseDisplay; UpdateDisplayStatus(view); };
        menu.Items.Add(timestamp); menu.Items.Add(pause); menu.Items.Add(new Separator());
        menu.Items.Add(MenuButton("Clear log", (_, _) => ClearView(view)));
        menu.Opening += (_, _) =>
        {
            foreach (var item in display.Items.OfType<MenuItem>()) item.IsChecked = displayOptions.First(option => option.Name == item.Header?.ToString()).Index == view.Display.SelectedIndex;
            timestamp.IsChecked = view.TimestampEnabled;
            pause.IsChecked = view.PauseDisplay;
            UpdateDisplayStatus(view);
        };
        return menu;
    }

    private static void RefreshSerialPorts(ComboBox list, string? preferred)
    {
        var current = preferred ?? (list.SelectedItem as SerialPortOption)?.Name;
        var ports = GetSerialPorts();
        if (!string.IsNullOrWhiteSpace(current) && ports.All(port => !string.Equals(port.Name, current, StringComparison.OrdinalIgnoreCase))) ports.Insert(0, new SerialPortOption(current, current));
        list.Items.Clear(); foreach (var port in ports) list.Items.Add(port);
        if (ports.Count > 0) list.SelectedItem = ports.FirstOrDefault(port => string.Equals(port.Name, current, StringComparison.OrdinalIgnoreCase)) ?? ports[0];
    }

    private static List<SerialPortOption> GetSerialPorts()
    {
        var names = SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
                foreach (ManagementObject device in searcher.Get())
                {
                    var name = device["Name"]?.ToString();
                    var match = System.Text.RegularExpressions.Regex.Match(name ?? "", @"\((COM\d+)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success && !string.IsNullOrWhiteSpace(name)) descriptions[match.Groups[1].Value] = name[..name.LastIndexOf(" (", StringComparison.Ordinal)];
                }
            }
            catch { }
        }
        return names.Select(name => new SerialPortOption(name, descriptions.TryGetValue(name, out var description) ? $"{name} <{description}>" : name)).ToList();
    }

    private static void UpdateTcpClients(TerminalView view, IReadOnlyList<TcpClientInfo> clients)
    {
        if (view.TcpClients is null) return;
        var selected = view.SelectedClientId;
        view.TcpClients.Items.Clear();
        foreach (var client in clients) view.TcpClients.Items.Add(client);
        view.SelectedClientId = clients.Any(client => client.Id == selected) ? selected : clients.FirstOrDefault()?.Id;
        view.TcpClients.SelectedItem = clients.FirstOrDefault(client => client.Id == view.SelectedClientId);
        if (view.DisconnectClient is not null) view.DisconnectClient.IsEnabled = view.SelectedClientId is not null;
    }

    private Control BuildCommands(TransportSession session, TextBlock log, List<CommandRow> rows, Func<int?> selectedClient, bool serverTargeted = false, bool defaultLf = false, Func<bool>? isVtMode = null)
    {
        var panel = new StackPanel { Spacing = 5 }; panel.Children.Add(new TextBlock { Text = "Quick commands", FontSize = 16, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 0) });
        if (serverTargeted) panel.Children.Add(new TextBlock { Text = "Send to selected client", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#8BA4BB")), Margin = new Thickness(0, 0, 0, 4) });
        for (var i = 0; i < 12; i++)
        {
            var rowData = new CommandRow(); rows.Add(rowData); var text = rowData.Text = new TextBox { Watermark = $"Command {i + 1}", MinWidth = 190, HorizontalAlignment = HorizontalAlignment.Stretch }; var hex = rowData.Hex = new CheckBox { Content = "HEX", VerticalAlignment = VerticalAlignment.Center }; var lf = rowData.Lf = new CheckBox { Content = "LF", IsChecked = defaultLf, VerticalAlignment = VerticalAlignment.Center }; var send = new Button { Content = "Send", Width = 58 };
            async Task SendCommand()
            {
                try { var command = new CommandSlot { Text = text.Text ?? "", Hex = hex.IsChecked == true, AppendLineFeed = lf.IsChecked == true }; await session.SendAsync(command.ToBytes(), selectedClient()); if (isVtMode?.Invoke() != true) AppendText(log, $"\r\n> {command.Text}\r\n", "#C792EA"); }
                catch (Exception ex) { AppendText(log, $"\r\n[Send error: {ex.Message}]\r\n", "#E57373"); }
            }
            rowData.Send = SendCommand;
            send.Click += async (_, _) => await SendCommand();
            text.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SendCommand(); } };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,70,55,70"), ColumnSpacing = 8, Margin = new Thickness(0, 0, 6, 0) }; row.Children.Add(text); row.Children.Add(hex); row.Children.Add(lf); row.Children.Add(send); Grid.SetColumn(hex, 1); Grid.SetColumn(lf, 2); Grid.SetColumn(send, 3); panel.Children.Add(row);
        }
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static void RestoreRows(List<CommandRow> rows, List<CommandConfig> saved)
    {
        for (var i = 0; i < Math.Min(rows.Count, saved.Count); i++) { rows[i].Text!.Text = saved[i].Text; rows[i].Hex!.IsChecked = saved[i].Hex; rows[i].Lf!.IsChecked = saved[i].Lf; }
    }

    private void AppendBytes(TextBlock log, int mode, ReadOnlySpan<byte> bytes)
    {
        var view = _views.Values.FirstOrDefault(candidate => ReferenceEquals(candidate.Log, log)); if (view?.PauseDisplay == true) return; var sb = new StringBuilder(); if (view?.TimestampEnabled == true) sb.Append($"[{DateTime.Now:HH:mm:ss.fff}] ");
        if (sb.Length > 0) AppendText(log, sb.ToString());
        foreach (var b in bytes)
        {
            if (mode == 1 || (mode == 2 && b is not (10 or 13))) AppendText(log, $"{{{b:X2}}}", "#9E9E9E");
            else if (mode == 3 && (b < 32 || b > 126) && b is not (10 or 13)) { }
            else if (b is 9 or 10 or 13 || b is >= 0x20 and <= 0x7E) AppendText(log, ((char)b).ToString());
            else AppendText(log, $"{{{b:X2}}}", "#9E9E9E");
        }
    }

    private void BeginLogging(TextBlock log) { if (_logWriters.ContainsKey(log)) return; Directory.CreateDirectory(_logDirectory); _logWriters[log] = new LogWriter(Path.Combine(_logDirectory, $"BaudRunner-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log")); }
    private void EndLogging(TextBlock log) { if (_logWriters.Remove(log, out var writer)) writer.Dispose(); }
    private void AppendText(TextBlock log, string text, string? color = null) { log.Inlines ??= new InlineCollection(); log.Inlines.Add(new Run(text) { Foreground = new SolidColorBrush(Color.Parse(color ?? (IsLightTheme ? "#1F2937" : "#D6E2F0"))) }); if (_logWriters.TryGetValue(log, out var writer)) writer.Write(text); }
    private void AppendStatus(TextBlock log, string message) { var color = message.Contains("open", StringComparison.OrdinalIgnoreCase) || message.Contains("connect", StringComparison.OrdinalIgnoreCase) ? "#2E7D32" : message.Contains("error", StringComparison.OrdinalIgnoreCase) || message.Contains("close", StringComparison.OrdinalIgnoreCase) || message.Contains("disconnect", StringComparison.OrdinalIgnoreCase) ? "#C62828" : null; AppendText(log, $"\r\n[{message}]\r\n", color); }
    private static void ClearLog(TextBlock log) => log.Inlines?.Clear();
    private static void ClearView(TerminalView view) { ClearLog(view.Log); view.VtTerminal.Clear(); }

    private void StartReconnect(TerminalView view, Func<Task> connect)
    {
        if (_reconnects.ContainsKey(view)) return;
        var cancellation = new CancellationTokenSource(); _reconnects[view] = cancellation;
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
                try { await connect(); AppendText(view.Log, "\r\n[Reconnected]\r\n"); break; }
                catch (Exception ex) { AppendText(view.Log, $"\r\n[Reconnect failed: {ex.Message}]\r\n"); }
            }
        }
        catch (OperationCanceledException) { }
        finally { if (_reconnects.TryGetValue(view, out var current) && current.Token == token) { current.Dispose(); _reconnects.Remove(view); } }
    }

    private void StopReconnect(TerminalView view)
    {
        if (_reconnects.Remove(view, out var cancellation)) cancellation.Cancel();
    }

    private async Task ShutdownAsync()
    {
        // Capture and write UI state before awaiting transport shutdown. This is
        // important for the window X button, where the desktop lifetime is already
        // beginning to close while asynchronous cleanup is running.
        if (!_skipConfigSave)
        {
            foreach (var view in _views.Values) SaveView(view);
            _config.Save();
        }
        foreach (var view in _views.Values) { StopReconnect(view); view.OpenRequested = false; await view.Session.CloseAsync(); EndLogging(view.Log); }
        foreach (var writer in _logWriters.Values) writer.Dispose(); _logWriters.Clear();
    }

    private void SaveView(TerminalView view)
    {
        var state = _config.Terminals[view.Tab.Header?.ToString() ?? "Terminal"] = new TerminalConfig { Address = (view.PortList?.SelectedItem as SerialPortOption)?.Name ?? view.Address.Text ?? "", Port = view.Port.Text ?? "5800", Baud = view.Baud.SelectedItem?.ToString() ?? "115200", DataBits = view.DataBits.SelectedItem?.ToString() ?? "8", Parity = view.Parity.SelectedItem?.ToString() ?? "None", StopBits = view.StopBits.SelectedItem?.ToString() ?? "One", DisplayMode = view.Display.SelectedIndex, Timestamp = view.TimestampEnabled, Pause = view.PauseDisplay, AutoReconnect = view.AutoReconnect.IsChecked == true, Rts = view.Rts.IsChecked == true, Dtr = view.Dtr.IsChecked == true };
        state.Commands = view.Rows.Select(row => new CommandConfig { Text = row.Text?.Text ?? "", Hex = row.Hex?.IsChecked == true, Lf = row.Lf?.IsChecked == true }).ToList();
    }

    private async Task ShowMessage(string title, string message) { var box = new Window { Title = title, Width = 460, Height = 190, Content = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right } } } }; ((Button)((StackPanel)box.Content!).Children[1]).Click += (_, _) => box.Close(); await box.ShowDialog(this); }

    private sealed class TerminalView
    {
        public required TabItem Tab; public required TransportSession Session; public required TextBlock Log; public required VtTerminalControl VtTerminal; public required ComboBox Display; public required TextBlock ModeLabel; public required TextBox Address; public required TextBox Port; public ComboBox? PortList; public required ComboBox Baud; public required ComboBox DataBits; public required ComboBox Parity; public required ComboBox StopBits; public required CheckBox AutoReconnect; public required CheckBox Rts; public required CheckBox Dtr; public required List<CommandRow> Rows; public required TransportKind Kind; public ListBox? TcpClients; public Button? DisconnectClient; public int? SelectedClientId; public required Border[] Signals; public bool VtMode; public bool TimestampEnabled; public bool PauseDisplay; public bool OpenRequested;
    }
    private sealed class CommandRow { public TextBox? Text; public CheckBox? Hex; public CheckBox? Lf; public Func<Task>? Send; }
    private sealed class SerialPortOption
    {
        public string Name { get; }
        private string Display { get; }
        public SerialPortOption(string name, string display) { Name = name; Display = display; }
        public override string ToString() => Display;
    }

    private sealed class LogWriter : IDisposable
    {
        private const int FlushSize = 4096;
        private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
        private readonly StreamWriter _writer;
        private readonly StringBuilder _pending = new();
        private DateTime _lastFlush = DateTime.UtcNow;

        public LogWriter(string path) => _writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = false };

        public void Write(string text)
        {
            _pending.Append(text);
            if (_pending.Length >= FlushSize || DateTime.UtcNow - _lastFlush >= FlushInterval) Flush();
        }

        private void Flush()
        {
            if (_pending.Length == 0) return;
            _writer.Write(_pending.ToString()); _pending.Clear(); _writer.Flush(); _lastFlush = DateTime.UtcNow;
        }

        public void Dispose() { Flush(); _writer.Dispose(); }
    }
}

public sealed class AppConfig
{
    public Dictionary<string, TerminalConfig> Terminals { get; set; } = new();
    public string Theme { get; set; } = "Dark";
    private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BaudRunner", "config.json");
    public static AppConfig Load() { try { return File.Exists(PathName) ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(PathName)) ?? new AppConfig() : new AppConfig(); } catch { return new AppConfig(); } }
    public void Save() { try { Directory.CreateDirectory(Path.GetDirectoryName(PathName)!); File.WriteAllText(PathName, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); } catch { } }
}

public sealed class TerminalConfig
{
    public string Address { get; set; } = ""; public string Port { get; set; } = "5800"; public string Baud { get; set; } = "115200"; public string DataBits { get; set; } = "8"; public string Parity { get; set; } = "None"; public string StopBits { get; set; } = "One"; public int DisplayMode { get; set; } public bool Timestamp { get; set; } public bool Pause { get; set; } public bool AutoReconnect { get; set; } public bool Rts { get; set; } public bool Dtr { get; set; } public List<CommandConfig> Commands { get; set; } = new();
}

public sealed class CommandConfig { public string Text { get; set; } = ""; public bool Hex { get; set; } public bool Lf { get; set; } }
