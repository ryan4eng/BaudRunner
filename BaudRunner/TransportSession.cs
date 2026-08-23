using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace BaudRunner;

public enum TransportKind { Serial, TcpClient, TcpServer, UdpClient, UdpServer }

/// <summary>
/// Severity carried by the event itself. The alternative - guessing from the wording
/// of an OS exception message - paints "Read error: the port is not open" green.
/// </summary>
public enum StatusLevel { Info, Success, Warning, Error }

public sealed class SerialSettings
{
    public int Baud { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Handshake Handshake { get; set; } = Handshake.None;
}

public sealed class TransportSession : IAsyncDisposable
{
    private const int ReadBufferSize = 8192;
    private const int SerialDriverBufferSize = 65536;
    private const int MaxServerClients = 5;

    private SerialPort? _serial;
    private TcpClient? _tcp;
    private TcpListener? _listener;
    private NetworkStream? _stream;
    private UdpClient? _udp;
    private CancellationTokenSource? _stop;
    private Task? _reader;
    private Task? _lineStatusReader;
    private volatile IPEndPoint? _udpPeer;

    // One gate for the whole open/close lifetime. Without it a second Open - from a
    // double click, or from the reconnect loop racing the button - runs CloseAsync
    // against the first attempt's half-built transport.
    private readonly SemaphoreSlim _lifetime = new(1, 1);

    // Neither NetworkStream nor SerialStream supports concurrent writers, and key
    // auto-repeat on F1-F12 issues overlapping sends without this.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private readonly object _clientsLock = new();
    private readonly Dictionary<int, TcpClient> _serverClients = new();
    private int _nextClientId;
    private long _bytesReceived;
    private long _bytesSent;
    private bool _lastCts, _lastDsr, _haveLineStatus;

    public TransportKind Kind { get; }

    /// <summary>Volatile: written by reader threads and read from the UI thread.</summary>
    public bool IsOpen { get => _isOpen; private set => _isOpen = value; }
    private volatile bool _isOpen;

    public long BytesReceivedCount => Interlocked.Read(ref _bytesReceived);
    public long BytesSentCount => Interlocked.Read(ref _bytesSent);
    public int ClientCount { get { lock (_clientsLock) { return _serverClients.Count; } } }
    public static int MaxClients => MaxServerClients;

    public event Action<ReadOnlyMemory<byte>>? BytesReceived;
    public event Action<StatusLevel, string>? Status;

    /// <summary>
    /// Raised from a reader when the link drops. Handlers must not block: they are
    /// expected to marshal to the UI thread and then call <see cref="CloseAsync"/>.
    /// </summary>
    public event Action<string>? ConnectionLost;
    public event Action<bool, bool>? LineStatusChanged;
    public event Action<IReadOnlyList<TcpClientInfo>>? TcpClientsChanged;
    public event Action<SerialError>? SerialErrorReceived;

    public TransportSession(TransportKind kind) => Kind = kind;

    public async Task OpenAsync(string address, int port, SerialSettings serial)
    {
        await _lifetime.WaitAsync();
        try
        {
            await CloseCoreAsync();
            var stop = new CancellationTokenSource();
            _stop = stop;
            try
            {
                switch (Kind)
                {
                    case TransportKind.Serial: OpenSerial(address, serial, stop.Token); break;
                    case TransportKind.TcpClient: await OpenTcpClientAsync(address, port, stop.Token); break;
                    case TransportKind.TcpServer: OpenTcpServer(port, stop.Token); break;
                    case TransportKind.UdpClient: await OpenUdpClientAsync(address, port, stop.Token); break;
                    case TransportKind.UdpServer: OpenUdpServer(port, stop.Token); break;
                }
                Status?.Invoke(StatusLevel.Success, "Connection open");
            }
            catch
            {
                IsOpen = false;
                await CloseCoreAsync();
                throw;
            }
        }
        finally { _lifetime.Release(); }
    }

    private void OpenSerial(string address, SerialSettings settings, CancellationToken token)
    {
        var serial = new SerialPort(address, settings.Baud, settings.Parity, settings.DataBits, settings.StopBits)
        {
            Handshake = settings.Handshake,
            // The driver default is 4 KB - about 45 ms of headroom at 921600 baud, after
            // which the UART silently overruns if the UI thread is busy.
            ReadBufferSize = SerialDriverBufferSize,
            WriteBufferSize = SerialDriverBufferSize,
            // Without this a send blocks forever when RTS/CTS pacing holds CTS low.
            WriteTimeout = 5000,
        };
        serial.ErrorReceived += (_, e) => SerialErrorReceived?.Invoke(e.EventType);
        serial.Open();
        _serial = serial;

        // IsOpen must be true before the readers start: their terminal check is
        // `if (IsOpen)`, so a port that fails instantly would otherwise never report it.
        IsOpen = true;
        _reader = PumpAsync(serial.BaseStream, token, "The serial port");
        _lineStatusReader = PollLineStatusAsync(token);
    }

    private async Task OpenTcpClientAsync(string address, int port, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        _tcp = client;
        await client.ConnectAsync(address, port, token);
        _stream = client.GetStream();
        IsOpen = true;
        _reader = PumpAsync(_stream, token, "The connection");
    }

    private void OpenTcpServer(int port, CancellationToken token)
    {
        _listener = CreateListener(port);
        _listener.Start();
        IsOpen = true;
        Status?.Invoke(StatusLevel.Info, $"Listening on port {port}; waiting for a client...");
        _reader = AcceptTcpServerAsync(token);
    }

    /// <summary>Binds dual-stack where the host has IPv6, so an IPv6 client is not silently unreachable.</summary>
    private static TcpListener CreateListener(int port)
    {
        if (Socket.OSSupportsIPv6)
        {
            try
            {
                var listener = new TcpListener(IPAddress.IPv6Any, port);
                listener.Server.DualMode = true;
                return listener;
            }
            catch (SocketException) { }
            catch (NotSupportedException) { }
        }
        return new TcpListener(IPAddress.Any, port);
    }

    private async Task OpenUdpClientAsync(string address, int port, CancellationToken token)
    {
        // The TCP arm resolves host names; this one used to demand a literal address.
        var target = IPAddress.TryParse(address, out var literal)
            ? literal
            : (await Dns.GetHostAddressesAsync(address, token)).FirstOrDefault()
              ?? throw new InvalidOperationException($"'{address}' could not be resolved.");

        var udp = new UdpClient(target.AddressFamily);
        SuppressUdpConnectionReset(udp);
        _udp = udp;
        _udpPeer = new IPEndPoint(target, port);
        udp.Connect(_udpPeer);
        IsOpen = true;
        _reader = ReadUdpAsync(udp, token);
    }

    private void OpenUdpServer(int port, CancellationToken token)
    {
        UdpClient udp;
        if (Socket.OSSupportsIPv6)
        {
            udp = new UdpClient(AddressFamily.InterNetworkV6);
            try { udp.Client.DualMode = true; } catch (SocketException) { } catch (NotSupportedException) { }
            udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        }
        else
        {
            udp = new UdpClient(port);
        }
        SuppressUdpConnectionReset(udp);
        _udp = udp;
        IsOpen = true;
        _reader = ReadUdpAsync(udp, token);
    }

    /// <summary>
    /// Windows reports an ICMP port-unreachable from a previous send as a
    /// WSAECONNRESET on the *next* receive. For a connectionless socket that is not
    /// an error, and .NET does not disable it for us.
    /// </summary>
    private static void SuppressUdpConnectionReset(UdpClient udp)
    {
        if (!OperatingSystem.IsWindows()) return;
        const int SioUdpConnReset = unchecked((int)0x9800000C);
        try { udp.Client.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, int? selectedClientId = null, CancellationToken token = default)
    {
        if (!IsOpen) throw new InvalidOperationException("The connection is not open.");
        await _sendGate.WaitAsync(token);
        try
        {
            switch (Kind)
            {
                case TransportKind.Serial:
                    await (_serial ?? throw new InvalidOperationException("The serial port is not open.")).BaseStream.WriteAsync(data, token);
                    break;
                case TransportKind.TcpClient:
                    var stream = _stream ?? throw new InvalidOperationException("The connection is not open.");
                    await stream.WriteAsync(data, token);
                    await stream.FlushAsync(token);
                    break;
                case TransportKind.TcpServer:
                    TcpClient? selected = null;
                    if (selectedClientId is { } clientId) { lock (_clientsLock) { _serverClients.TryGetValue(clientId, out selected); } }
                    if (selected is null) throw new InvalidOperationException("Select a connected TCP client first.");
                    var clientStream = selected.GetStream();
                    await clientStream.WriteAsync(data, token);
                    await clientStream.FlushAsync(token);
                    break;
                case TransportKind.UdpClient:
                    await (_udp ?? throw new InvalidOperationException("The socket is not open.")).SendAsync(data, token);
                    break;
                case TransportKind.UdpServer:
                    var peer = _udpPeer ?? throw new InvalidOperationException("No UDP client has sent a packet yet.");
                    await (_udp ?? throw new InvalidOperationException("The socket is not open.")).SendAsync(data, peer, token);
                    break;
            }
            Interlocked.Add(ref _bytesSent, data.Length);
        }
        finally { _sendGate.Release(); }
    }

    /// <summary>The endpoint a UDP server reply would go to, for display.</summary>
    public string? UdpPeerDescription => _udpPeer?.ToString();

    public void SetRts(bool value)
    {
        // Setting RtsEnable throws when the handshake already owns the line.
        if (_serial is not { IsOpen: true } serial || HandshakeOwnsRts) return;
        try { serial.RtsEnable = value; } catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException) { }
    }

    public void SetDtr(bool value)
    {
        if (_serial is not { IsOpen: true } serial) return;
        try { serial.DtrEnable = value; } catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException) { }
    }

    public bool HandshakeOwnsRts => _serial is { } serial && serial.Handshake is Handshake.RequestToSend or Handshake.RequestToSendXOnXOff;

    /// <summary>Asserts a serial BREAK, the documented attention signal for U-Boot and much industrial gear.</summary>
    public async Task SendBreakAsync(int milliseconds = 250)
    {
        if (_serial is not { IsOpen: true } serial) throw new InvalidOperationException("The serial port is not open.");
        serial.BreakState = true;
        try { await Task.Delay(milliseconds); }
        finally { try { serial.BreakState = false; } catch (Exception ex) when (ex is InvalidOperationException or IOException) { } }
    }

    /// <summary>The DTR/RTS sequence that resets an ESP32 or Arduino-style board, restoring the caller's line state afterwards.</summary>
    public async Task PulseResetAsync(bool restoreRts, bool restoreDtr)
    {
        if (_serial is not { IsOpen: true } serial) throw new InvalidOperationException("The serial port is not open.");
        SetDtr(false);
        SetRts(true);
        await Task.Delay(120);
        SetRts(false);
        await Task.Delay(50);
        SetRts(restoreRts);
        SetDtr(restoreDtr);
    }

    public void DisconnectTcpClient(int id)
    {
        TcpClient? client;
        lock (_clientsLock) { _serverClients.TryGetValue(id, out client); }
        try { client?.Close(); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    private async Task PumpAsync(Stream stream, CancellationToken token, string what)
    {
        var buffer = new byte[ReadBufferSize];
        string? reason = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                int count;
                // A finite ReadTimeout surfaces here on some platforms; an idle link is
                // not a disconnect, so keep waiting rather than tearing the session down.
                try { count = await stream.ReadAsync(buffer, token); }
                catch (TimeoutException) { continue; }

                if (count == 0) { reason = $"{what} was closed by the other end"; break; }
                Interlocked.Add(ref _bytesReceived, count);
                BytesReceived?.Invoke(buffer.AsMemory(0, count).ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { reason = ex.Message; }
        RaiseLost(reason, token);
    }

    private void RaiseLost(string? reason, CancellationToken token)
    {
        if (!IsOpen || token.IsCancellationRequested) return;
        IsOpen = false;
        ConnectionLost?.Invoke(reason ?? "the connection was closed");
    }

    private async Task PollLineStatusAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Copied: CloseAsync nulls _serial from another thread.
                var serial = _serial;
                if (serial is null || !serial.IsOpen) break;

                bool cts, dsr;
                // A removed USB adapter fails GetCommModemStatus with ACCESS_DENIED,
                // which arrives here as UnauthorizedAccessException, not one of the
                // three types the old code caught - so the poller used to just die.
                try { cts = serial.CtsHolding; dsr = serial.DsrHolding; }
                catch (Exception) { break; }

                if (!_haveLineStatus || cts != _lastCts || dsr != _lastDsr)
                {
                    _lastCts = cts; _lastDsr = dsr; _haveLineStatus = true;
                    LineStatusChanged?.Invoke(cts, dsr);
                }
                await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task AcceptTcpServerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) { return; }
            catch (SocketException ex)
            {
                // A client that resets between SYN-ACK and accept fails just this
                // accept; the listener is still good, so keep serving.
                Status?.Invoke(StatusLevel.Warning, $"Accept failed: {ex.Message}");
                continue;
            }

            TcpClientInfo? info = null;
            lock (_clientsLock)
            {
                if (_serverClients.Count < MaxServerClients)
                {
                    var id = ++_nextClientId;
                    _serverClients[id] = client;
                    info = new TcpClientInfo(id, client.Client.RemoteEndPoint?.ToString() ?? "Unknown");
                }
            }
            if (info is null)
            {
                Status?.Invoke(StatusLevel.Warning, $"TCP client rejected: maximum of {MaxServerClients} clients reached");
                client.Close();
                continue;
            }
            _ = ReadServerClientAsync(info.Id, info.Endpoint, client, token);
            PublishClients();
        }
    }

    private async Task ReadServerClientAsync(int id, string endpoint, TcpClient client, CancellationToken token)
    {
        var buffer = new byte[ReadBufferSize];
        try
        {
            var stream = client.GetStream();
            while (!token.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(buffer, token);
                if (count == 0) break;
                Interlocked.Add(ref _bytesReceived, count);
                BytesReceived?.Invoke(buffer.AsMemory(0, count).ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Status?.Invoke(StatusLevel.Warning, $"TCP client {endpoint} read error: {ex.Message}"); }
        finally
        {
            try { client.Close(); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            bool removed;
            lock (_clientsLock) { removed = _serverClients.Remove(id); }
            if (removed && !token.IsCancellationRequested)
            {
                Status?.Invoke(StatusLevel.Warning, $"TCP client {endpoint} disconnected");
                PublishClients();
            }
        }
    }

    private void PublishClients()
    {
        TcpClientInfo[] clients;
        lock (_clientsLock)
        {
            clients = _serverClients.Select(pair => new TcpClientInfo(pair.Key, Describe(pair.Value))).ToArray();
        }
        TcpClientsChanged?.Invoke(clients);
    }

    private static string Describe(TcpClient client)
    {
        try { return client.Client.RemoteEndPoint?.ToString() ?? "Unknown"; }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return "Unknown"; }
    }

    private async Task ReadUdpAsync(UdpClient udp, CancellationToken token)
    {
        string? reason = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(token);
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.MessageSize)
                {
                    // Stale ICMP for an earlier datagram, or an oversized one. UDP has no
                    // connection to lose, so log it and keep receiving.
                    Status?.Invoke(StatusLevel.Warning, $"Datagram error ignored: {ex.SocketErrorCode}");
                    continue;
                }
                _udpPeer = result.RemoteEndPoint;
                Interlocked.Add(ref _bytesReceived, result.Buffer.Length);
                BytesReceived?.Invoke(result.Buffer);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { reason = ex.Message; }
        RaiseLost(reason, token);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _lifetime.Dispose();
        _sendGate.Dispose();
    }

    public async Task CloseAsync()
    {
        await _lifetime.WaitAsync();
        try { await CloseCoreAsync(); }
        finally { _lifetime.Release(); }
    }

    /// <summary>
    /// Every step is individually guarded: SerialPort.Dispose throws
    /// UnauthorizedAccessException when the USB adapter has been yanked, and letting
    /// that escape used to abandon every remaining socket and task in this method.
    /// </summary>
    private async Task CloseCoreAsync()
    {
        IsOpen = false;
        var stop = _stop;
        Try(() => stop?.Cancel());

        Try(() => { _serial?.Close(); _serial?.Dispose(); });
        _serial = null;
        Try(() => _stream?.Dispose());
        _stream = null;
        Try(() => _tcp?.Close());
        _tcp = null;
        Try(() => _listener?.Stop());
        _listener = null;
        Try(() => _udp?.Dispose());
        _udp = null;
        _udpPeer = null;
        _haveLineStatus = false;

        List<TcpClient> serverClients;
        lock (_clientsLock) { serverClients = _serverClients.Values.ToList(); _serverClients.Clear(); }
        foreach (var client in serverClients) Try(client.Close);
        if (serverClients.Count > 0) PublishClients();

        await AwaitReader(_reader);
        _reader = null;
        await AwaitReader(_lineStatusReader);
        _lineStatusReader = null;

        // Only now: the readers held this token, and disposing it earlier makes their
        // next ReadAsync registration throw ObjectDisposedException.
        Try(() => stop?.Dispose());
        if (ReferenceEquals(_stop, stop)) _stop = null;
    }

    /// <summary>
    /// Bounded wait. A handler that responds to ConnectionLost by calling CloseAsync is
    /// the normal path, and a driver that never completes a pending read must not be
    /// able to hang the close.
    /// </summary>
    private static async Task AwaitReader(Task? task)
    {
        if (task is null) return;
        try { await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2))); }
        catch { }
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SocketException or ObjectDisposedException or InvalidOperationException) { }
    }
}

public sealed class TcpClientInfo
{
    public int Id { get; }
    public string Endpoint { get; }
    public TcpClientInfo(int id, string endpoint) { Id = id; Endpoint = endpoint; }
    public override string ToString() => Endpoint;
}
