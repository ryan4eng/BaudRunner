using System.IO.Ports;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace BaudRunner;

public sealed class MainWindow : Window
{
    private readonly Dictionary<TabItem, TerminalView> _views = new();
    private readonly Dictionary<TerminalView, CancellationTokenSource> _reconnects = new();
    private readonly string _logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BaudRunner", "logs");
    private readonly AppConfig _config;
    private readonly Dictionary<TextBlock, StreamWriter> _logWriters = new();

    public MainWindow()
    {
        _config = AppConfig.Load();
        Application.Current!.RequestedThemeVariant = string.Equals(_config.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? ThemeVariant.Light : ThemeVariant.Dark;
        Title = "BaudRunner - native serial & network terminal";
        Width = 1380; Height = 880; MinWidth = 900; MinHeight = 600;
        Background = new SolidColorBrush(Color.Parse("#11151B"));
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://BaudRunner/Icon.png"))); } catch { }
        Content = BuildShell();
        Closing += async (_, _) => await ShutdownAsync();
    }

    private Control BuildShell()
    {
        var tabs = new TabControl { Margin = new Thickness(12) };
        AddTerminal(tabs, "Serial", TransportKind.Serial);
        AddTerminal(tabs, "TCP Client", TransportKind.TcpClient);
        AddTerminal(tabs, "TCP Server", TransportKind.TcpServer);
        AddTerminal(tabs, "UDP Client", TransportKind.UdpClient);
        AddTerminal(tabs, "UDP Server", TransportKind.UdpServer);

        var menu = new Menu { Background = new SolidColorBrush(Color.Parse("#191F27")) };
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(MenuButton("Clear active log", (_, _) => { if (tabs.SelectedItem is TabItem t && _views.TryGetValue(t, out var view)) ClearLog(view.Log); }));
        file.Items.Add(new Separator()); file.Items.Add(MenuButton("E_xit", (_, _) => Close()));
        var help = new MenuItem { Header = "_Help" };
        help.Items.Add(MenuButton("About BaudRunner", (_, _) => _ = ShowMessage("BaudRunner", "A native C# Avalonia terminal for Windows and Linux.")));
        var viewMenu = new MenuItem { Header = "_View" };
        var light = new MenuItem { Header = "Light mode" }; light.Click += (_, _) => SetTheme(ThemeVariant.Light);
        var dark = new MenuItem { Header = "Dark mode" }; dark.Click += (_, _) => SetTheme(ThemeVariant.Dark);
        viewMenu.Items.Add(light); viewMenu.Items.Add(dark);
        menu.Items.Add(file); menu.Items.Add(viewMenu); menu.Items.Add(help);
        DockPanel.SetDock(menu, Dock.Top);
        return new DockPanel { Children = { menu, tabs } };
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
        var log = new TextBlock { TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas,monospace"), FontSize = 13, Background = new SolidColorBrush(Color.Parse("#0B0E12")), Foreground = new SolidColorBrush(Color.Parse("#D6E2F0")), Padding = new Thickness(8) };
        ScrollViewer.SetVerticalScrollBarVisibility(log, ScrollBarVisibility.Auto); ScrollViewer.SetHorizontalScrollBarVisibility(log, ScrollBarVisibility.Auto);
        var display = Combo("Normal", "Hex (all bytes)", "Hex (except CR/LF)", "ASCII only"); display.SelectedIndex = Math.Clamp(saved.DisplayMode, 0, 3);
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
            portList = new ComboBox { Width = 160, PlaceholderText = "Select a port" };
            RefreshSerialPorts(portList, saved.Address);
            portList.DropDownOpened += (_, _) => RefreshSerialPorts(portList, portList.SelectedItem?.ToString());
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

        var tabView = new TerminalView { Tab = tab, Session = session, Log = log, Display = display, Address = address, Port = kind == TransportKind.TcpServer || kind == TransportKind.UdpServer ? listenPort : remotePort, PortList = portList, Baud = baud, DataBits = dataBits, Parity = parity, StopBits = stopBits, AutoReconnect = autoReconnect, Rts = rts, Dtr = dtr, Rows = rows, Kind = kind, TimestampEnabled = saved.Timestamp, PauseDisplay = saved.Pause, TcpClients = tcpClients, DisconnectClient = disconnectClient };
        session.BytesReceived += bytes => Dispatcher.UIThread.Post(() => AppendBytes(log, display.SelectedIndex, bytes.Span));
        session.Status += message => Dispatcher.UIThread.Post(() => AppendStatus(log, message));
        session.LineStatusChanged += (ctsState, dsrState) => Dispatcher.UIThread.Post(() => { SetSignal(cts, ctsState); SetSignal(dsr, dsrState); });
        session.TcpClientsChanged += clients => Dispatcher.UIThread.Post(() => UpdateTcpClients(tabView, clients));
        session.ConnectionLost += () => Dispatcher.UIThread.Post(() => { open.IsEnabled = true; close.IsEnabled = false; EndLogging(log); AppendText(log, "\r\n[Connection closed]\r\n", "#E57373"); if (tabView.OpenRequested && tabView.AutoReconnect.IsChecked == true) StartReconnect(tabView, Connect); });
        RestoreRows(rows, saved.Commands);
        var commands = BuildCommands(session, log, rows, () => tabView.SelectedClientId, kind == TransportKind.TcpServer);
        log.ContextMenu = BuildLogContextMenu(tabView);
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
        grid.Children.Add(logScroll); Grid.SetColumn(rightPane, 1); grid.Children.Add(rightPane); tab.Content = grid;

        async Task Connect()
        {
            var selectedAddress = kind == TransportKind.Serial ? portList?.SelectedItem?.ToString() ?? "" : address.Text ?? "";
            var portText = tabView.Port.Text ?? "";
            if (kind != TransportKind.Serial && (!int.TryParse(portText, out var port) || port is < 1 or > 65535)) throw new InvalidOperationException("Enter a valid port from 1 to 65535.");
            var serialBaud = int.Parse(baud.SelectedItem?.ToString() ?? "115200"); var bits = int.Parse(dataBits.SelectedItem?.ToString() ?? "8"); var parityValue = (Parity)parity.SelectedIndex; var stop = stopBits.SelectedIndex switch { 1 => StopBits.OnePointFive, 2 => StopBits.Two, _ => StopBits.One };
            await session.OpenAsync(selectedAddress, kind == TransportKind.Serial ? 0 : int.Parse(portText), serialBaud, bits, parityValue, stop); open.IsEnabled = false; close.IsEnabled = true; BeginLogging(log); if (kind == TransportKind.Serial) { session.SetRts(rts.IsChecked == true); session.SetDtr(dtr.IsChecked == true); }
        }
        open.Click += async (_, _) => { tabView.OpenRequested = true; try { await Connect(); } catch (Exception ex) { AppendText(log, $"\r\n[Open error: {ex.Message}]\r\n", "#E57373"); if (autoReconnect.IsChecked == true) StartReconnect(tabView, Connect); } };
        close.Click += async (_, _) => { tabView.OpenRequested = false; StopReconnect(tabView); await session.CloseAsync(); EndLogging(log); AppendText(log, "\r\n[Connection closed]\r\n", "#E57373"); open.IsEnabled = true; close.IsEnabled = false; };
        clear.Click += (_, _) => ClearLog(log);
        rts.IsCheckedChanged += (_, _) => session.SetRts(rts.IsChecked == true); dtr.IsCheckedChanged += (_, _) => session.SetDtr(dtr.IsChecked == true);
        if (tcpClients is not null) tcpClients.SelectionChanged += (_, _) => { tabView.SelectedClientId = (tcpClients.SelectedItem as TcpClientInfo)?.Id; if (disconnectClient is not null) disconnectClient.IsEnabled = tabView.SelectedClientId is not null; };
        if (disconnectClient is not null) disconnectClient.Click += (_, _) => { if (tabView.SelectedClientId is int id) session.DisconnectTcpClient(id); };
        return tabView;
    }

    private static ComboBox Combo(params string[] items) { var combo = new ComboBox(); foreach (var item in items) combo.Items.Add(item); return combo; }
    private static void SelectOrDefault(ComboBox combo, string? value, string fallback) => combo.SelectedItem = combo.Items.Cast<object>().Any(item => string.Equals(item.ToString(), value, StringComparison.OrdinalIgnoreCase)) ? value : fallback;
    private static Border Field(string label, Control control) => new() { Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(0, 0, 0, 2), Child = new StackPanel { Spacing = 3, Children = { new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#8BA4BB")) }, control } } };
    private static Border SignalIndicator(string label, bool visible) => new() { IsVisible = visible, Background = new SolidColorBrush(Color.Parse("#303640")), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 3), Child = new TextBlock { Text = $"{label}: off", Foreground = new SolidColorBrush(Color.Parse("#B8C2CC")) } };
    private static void SetSignal(Border indicator, bool asserted) { indicator.Background = new SolidColorBrush(Color.Parse(asserted ? "#246B45" : "#303640")); if (indicator.Child is TextBlock text) { var label = text.Text?.Split(':')[0] ?? "Signal"; text.Text = $"{label}: {(asserted ? "on" : "off")}"; text.Foreground = new SolidColorBrush(Color.Parse(asserted ? "#D8FFE8" : "#B8C2CC")); } }
    private static MenuItem MenuButton(string header, Action<object?, EventArgs> handler) { var item = new MenuItem { Header = header }; item.Click += (_, _) => handler(item, EventArgs.Empty); return item; }
    private void SetTheme(ThemeVariant variant) { Application.Current!.RequestedThemeVariant = variant; _config.Theme = variant == ThemeVariant.Light ? "Light" : "Dark"; }

    private ContextMenu BuildLogContextMenu(TerminalView view)
    {
        var menu = new ContextMenu();
        var display = new MenuItem { Header = "Display format" };
        foreach (var option in new[] { "Normal", "Hex (all bytes)", "Hex (except CR/LF)", "ASCII only" })
        {
            var item = new MenuItem { Header = option };
            item.Click += (_, _) => { view.Display.SelectedIndex = Array.IndexOf(new[] { "Normal", "Hex (all bytes)", "Hex (except CR/LF)", "ASCII only" }, option); };
            display.Items.Add(item);
        }
        menu.Items.Add(display);
        var timestamp = new MenuItem { Header = "Timestamp received data", IsChecked = view.TimestampEnabled };
        timestamp.Click += (_, _) => { timestamp.IsChecked = !timestamp.IsChecked; view.TimestampEnabled = timestamp.IsChecked; };
        var pause = new MenuItem { Header = "Pause display", IsChecked = view.PauseDisplay };
        pause.Click += (_, _) => { pause.IsChecked = !pause.IsChecked; view.PauseDisplay = pause.IsChecked; };
        menu.Items.Add(timestamp); menu.Items.Add(pause); menu.Items.Add(new Separator());
        menu.Items.Add(MenuButton("Clear log", (_, _) => ClearLog(view.Log)));
        return menu;
    }

    private static void RefreshSerialPorts(ComboBox list, string? preferred)
    {
        var current = preferred ?? list.SelectedItem?.ToString(); var ports = SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (!string.IsNullOrWhiteSpace(current) && !ports.Contains(current, StringComparer.OrdinalIgnoreCase)) ports.Insert(0, current);
        list.Items.Clear(); foreach (var port in ports) list.Items.Add(port);
        if (ports.Count > 0) list.SelectedItem = ports.FirstOrDefault(p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase)) ?? ports[0];
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

    private Control BuildCommands(TransportSession session, TextBlock log, List<CommandRow> rows, Func<int?> selectedClient, bool serverTargeted = false)
    {
        var panel = new StackPanel { Spacing = 5 }; panel.Children.Add(new TextBlock { Text = "Quick commands", FontSize = 16, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 0) });
        if (serverTargeted) panel.Children.Add(new TextBlock { Text = "Send to selected client", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#8BA4BB")), Margin = new Thickness(0, 0, 0, 4) });
        for (var i = 0; i < 12; i++)
        {
            var rowData = new CommandRow(); rows.Add(rowData); var text = rowData.Text = new TextBox { Watermark = $"Command {i + 1}", MinWidth = 190, HorizontalAlignment = HorizontalAlignment.Stretch }; var hex = rowData.Hex = new CheckBox { Content = "HEX", VerticalAlignment = VerticalAlignment.Center }; var lf = rowData.Lf = new CheckBox { Content = "LF", VerticalAlignment = VerticalAlignment.Center }; var send = new Button { Content = "Send", Width = 58 };
            send.Click += async (_, _) => { try { var command = new CommandSlot { Text = text.Text ?? "", Hex = hex.IsChecked == true, AppendLineFeed = lf.IsChecked == true }; await session.SendAsync(command.ToBytes(), selectedClient()); AppendText(log, $"\r\n> {command.Text}\r\n", "#C792EA"); } catch (Exception ex) { AppendText(log, $"\r\n[Send error: {ex.Message}]\r\n", "#E57373"); } };
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
        if (sb.Length > 0) AppendText(log, sb.ToString(), "#D6E2F0");
        foreach (var b in bytes)
        {
            if (mode == 1 || (mode == 2 && b is not (10 or 13))) AppendText(log, $"{{{b:X2}}}", "#9E9E9E");
            else if (mode == 3 && (b < 32 || b > 126) && b is not (10 or 13)) { }
            else if (b is 9 or 10 or 13 || b is >= 0x20 and <= 0x7E) AppendText(log, ((char)b).ToString(), "#D6E2F0");
            else AppendText(log, $"{{{b:X2}}}", "#9E9E9E");
        }
    }

    private void BeginLogging(TextBlock log) { if (_logWriters.ContainsKey(log)) return; Directory.CreateDirectory(_logDirectory); _logWriters[log] = new StreamWriter(Path.Combine(_logDirectory, $"BaudRunner-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log"), append: true, Encoding.UTF8) { AutoFlush = true }; }
    private void EndLogging(TextBlock log) { if (_logWriters.Remove(log, out var writer)) writer.Dispose(); }
    private void AppendText(TextBlock log, string text, string color = "#D6E2F0") { log.Inlines ??= new InlineCollection(); log.Inlines.Add(new Run(text) { Foreground = new SolidColorBrush(Color.Parse(color)) }); if (_logWriters.TryGetValue(log, out var writer)) writer.Write(text); }
    private void AppendStatus(TextBlock log, string message) { var color = message.Contains("open", StringComparison.OrdinalIgnoreCase) || message.Contains("connect", StringComparison.OrdinalIgnoreCase) ? "#81C784" : message.Contains("error", StringComparison.OrdinalIgnoreCase) || message.Contains("close", StringComparison.OrdinalIgnoreCase) || message.Contains("disconnect", StringComparison.OrdinalIgnoreCase) ? "#E57373" : "#D6E2F0"; AppendText(log, $"\r\n[{message}]\r\n", color); }
    private static void ClearLog(TextBlock log) => log.Inlines?.Clear();

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
        foreach (var view in _views.Values) { SaveView(view); StopReconnect(view); view.OpenRequested = false; await view.Session.CloseAsync(); EndLogging(view.Log); }
        foreach (var writer in _logWriters.Values) writer.Dispose(); _logWriters.Clear(); _config.Save();
    }

    private void SaveView(TerminalView view)
    {
        var state = _config.Terminals[view.Tab.Header?.ToString() ?? "Terminal"] = new TerminalConfig { Address = view.PortList?.SelectedItem?.ToString() ?? view.Address.Text ?? "", Port = view.Port.Text ?? "5800", Baud = view.Baud.SelectedItem?.ToString() ?? "115200", DataBits = view.DataBits.SelectedItem?.ToString() ?? "8", Parity = view.Parity.SelectedItem?.ToString() ?? "None", StopBits = view.StopBits.SelectedItem?.ToString() ?? "One", DisplayMode = view.Display.SelectedIndex, Timestamp = view.TimestampEnabled, Pause = view.PauseDisplay, AutoReconnect = view.AutoReconnect.IsChecked == true, Rts = view.Rts.IsChecked == true, Dtr = view.Dtr.IsChecked == true };
        state.Commands = view.Rows.Select(row => new CommandConfig { Text = row.Text?.Text ?? "", Hex = row.Hex?.IsChecked == true, Lf = row.Lf?.IsChecked == true }).ToList();
    }

    private async Task ShowMessage(string title, string message) { var box = new Window { Title = title, Width = 460, Height = 190, Content = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right } } } }; ((Button)((StackPanel)box.Content!).Children[1]).Click += (_, _) => box.Close(); await box.ShowDialog(this); }

    private sealed class TerminalView
    {
        public required TabItem Tab; public required TransportSession Session; public required TextBlock Log; public required ComboBox Display; public required TextBox Address; public required TextBox Port; public ComboBox? PortList; public required ComboBox Baud; public required ComboBox DataBits; public required ComboBox Parity; public required ComboBox StopBits; public required CheckBox AutoReconnect; public required CheckBox Rts; public required CheckBox Dtr; public required List<CommandRow> Rows; public required TransportKind Kind; public ListBox? TcpClients; public Button? DisconnectClient; public int? SelectedClientId; public bool TimestampEnabled; public bool PauseDisplay; public bool OpenRequested;
    }
    private sealed class CommandRow { public TextBox? Text; public CheckBox? Hex; public CheckBox? Lf; }
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
