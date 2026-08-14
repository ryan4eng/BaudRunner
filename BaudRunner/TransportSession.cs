using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace BaudRunner;

public enum TransportKind { Serial, TcpClient, TcpServer, UdpClient, UdpServer }

public sealed class TransportSession : IAsyncDisposable
{
    private SerialPort? _serial;
    private TcpClient? _tcp;
    private TcpListener? _listener;
    private NetworkStream? _stream;
    private UdpClient? _udp;
    private CancellationTokenSource? _stop;
    private Task? _reader;
    private Task? _lineStatusReader;
    private IPEndPoint? _udpPeer;
    private readonly Dictionary<int, (TcpClient Client, Task Reader)> _serverClients = new();
    private int _nextClientId;

    public TransportKind Kind { get; }
    public bool IsOpen { get; private set; }
    public event Action<ReadOnlyMemory<byte>>? BytesReceived;
    public event Action<string>? Status;
    public event Action? ConnectionLost;
    public event Action<bool, bool>? LineStatusChanged;
    public event Action<IReadOnlyList<TcpClientInfo>>? TcpClientsChanged;
    public event Action<TcpClientInfo>? TcpClientConnected;
    public event Action<TcpClientInfo>? TcpClientDisconnected;

    public TransportSession(TransportKind kind) => Kind = kind;

    public async Task OpenAsync(string address, int port, int baud, int dataBits, Parity parity, StopBits stopBits)
    {
        await CloseAsync();
        _stop = new CancellationTokenSource();
        try
        {
            switch (Kind)
            {
                case TransportKind.Serial:
                    _serial = new SerialPort(address, baud, parity, dataBits, stopBits) { ReadTimeout = 250 };
                    _serial.Open();
                    _reader = ReadSerialAsync(_serial.BaseStream, _stop.Token);
                    _lineStatusReader = ReadLineStatusAsync(_stop.Token);
                    break;
                case TransportKind.TcpClient:
                    _tcp = new TcpClient();
                    await _tcp.ConnectAsync(address, port, _stop.Token);
                    _stream = _tcp.GetStream();
                    _reader = ReadStreamAsync(_stream, _stop.Token);
                    break;
                case TransportKind.TcpServer:
                    _listener = new TcpListener(IPAddress.Any, port);
                    _listener.Start();
                    Status?.Invoke($"Listening on port {port}; waiting for a client...");
                    _reader = AcceptTcpServerAsync(_stop.Token);
                    break;
                case TransportKind.UdpClient:
                    _udp = new UdpClient();
                    _udpPeer = new IPEndPoint(IPAddress.Parse(address), port);
                    _udp.Connect(_udpPeer);
                    _reader = ReadUdpAsync(_udp, _stop.Token);
                    break;
                case TransportKind.UdpServer:
                    _udp = new UdpClient(port);
                    _reader = ReadUdpAsync(_udp, _stop.Token);
                    break;
            }
            IsOpen = true;
            Status?.Invoke("Connection open");
        }
        catch
        {
            await CloseAsync();
            throw;
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, int? selectedClientId = null)
    {
        if (!IsOpen) throw new InvalidOperationException("The connection is not open.");
        switch (Kind)
        {
            case TransportKind.Serial: _serial!.Write(data.ToArray(), 0, data.Length); break;
            case TransportKind.TcpClient: await _stream!.WriteAsync(data); await _stream.FlushAsync(); break;
            case TransportKind.TcpServer:
                if (selectedClientId is null || !_serverClients.TryGetValue(selectedClientId.Value, out var selected)) throw new InvalidOperationException("Select a connected TCP client first.");
                await selected.Client.GetStream().WriteAsync(data); await selected.Client.GetStream().FlushAsync(); break;
            case TransportKind.UdpClient: await _udp!.SendAsync(data); break;
            case TransportKind.UdpServer:
                if (_udpPeer is null) throw new InvalidOperationException("No UDP client has sent a packet yet.");
                await _udp!.SendAsync(data, _udpPeer); break;
        }
    }

    public void SetRts(bool value) { if (_serial is not null && _serial.IsOpen) _serial.RtsEnable = value; }
    public void SetDtr(bool value) { if (_serial is not null && _serial.IsOpen) _serial.DtrEnable = value; }
    public void DisconnectTcpClient(int id) { if (_serverClients.TryGetValue(id, out var client)) client.Client.Close(); }

    private async Task ReadSerialAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        try { while (!token.IsCancellationRequested) { var count = await stream.ReadAsync(buffer, token); if (count == 0) break; BytesReceived?.Invoke(buffer.AsMemory(0, count).ToArray()); } }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Status?.Invoke($"Read error: {ex.Message}"); }
        if (IsOpen && !token.IsCancellationRequested) { IsOpen = false; ConnectionLost?.Invoke(); }
    }

    private async Task ReadLineStatusAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _serial is not null && _serial.IsOpen)
            {
                LineStatusChanged?.Invoke(_serial.CtsHolding, _serial.DsrHolding);
                await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ReadStreamAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        try { while (!token.IsCancellationRequested) { var count = await stream.ReadAsync(buffer, token); if (count == 0) break; BytesReceived?.Invoke(buffer.AsMemory(0, count).ToArray()); } }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status?.Invoke($"Read error: {ex.Message}"); }
        if (IsOpen && !token.IsCancellationRequested) { IsOpen = false; ConnectionLost?.Invoke(); }
    }

    private async Task AcceptTcpServerAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(token);
                if (_serverClients.Count >= 5) { Status?.Invoke("TCP client rejected: maximum of 5 clients reached"); client.Close(); continue; }
                var id = ++_nextClientId;
                var info = new TcpClientInfo(id, client.Client.RemoteEndPoint?.ToString() ?? "Unknown");
                var reader = ReadServerClientAsync(id, client, token);
                _serverClients[id] = (client, reader);
                TcpClientConnected?.Invoke(info); PublishClients();
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Status?.Invoke($"Accept error: {ex.Message}"); }
    }

    private async Task ReadServerClientAsync(int id, TcpClient client, CancellationToken token)
    {
        var buffer = new byte[8192];
        try { var stream = client.GetStream(); while (!token.IsCancellationRequested) { var count = await stream.ReadAsync(buffer, token); if (count == 0) break; BytesReceived?.Invoke(buffer.AsMemory(0, count).ToArray()); } }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status?.Invoke($"TCP client read error: {ex.Message}"); }
        finally { client.Close(); if (_serverClients.Remove(id, out _)) { var info = new TcpClientInfo(id, "Disconnected"); TcpClientDisconnected?.Invoke(info); PublishClients(); } }
    }

    private void PublishClients() => TcpClientsChanged?.Invoke(_serverClients.Select(pair => new TcpClientInfo(pair.Key, pair.Value.Client.Client.RemoteEndPoint?.ToString() ?? "Unknown")).ToArray());

    private async Task ReadUdpAsync(UdpClient udp, CancellationToken token)
    {
        try { while (!token.IsCancellationRequested) { var result = await udp.ReceiveAsync(token); _udpPeer = result.RemoteEndPoint; BytesReceived?.Invoke(result.Buffer); } }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Status?.Invoke($"Receive error: {ex.Message}"); if (IsOpen) { IsOpen = false; ConnectionLost?.Invoke(); } }
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    public async Task CloseAsync()
    {
        IsOpen = false;
        if (_stop is not null) { _stop.Cancel(); _stop.Dispose(); _stop = null; }
        _serial?.Close(); _serial?.Dispose(); _serial = null;
        _stream?.Dispose(); _stream = null;
        _tcp?.Close(); _tcp = null;
        _listener?.Stop(); _listener = null;
        _udp?.Dispose(); _udp = null; _udpPeer = null;
        foreach (var client in _serverClients.Values) client.Client.Close(); _serverClients.Clear(); PublishClients();
        try { if (_reader is not null) await _reader; } catch { }
        _reader = null;
        try { if (_lineStatusReader is not null) await _lineStatusReader; } catch { }
        _lineStatusReader = null;
    }
}

public sealed class TcpClientInfo
{
    public int Id { get; }
    public string Endpoint { get; }
    public TcpClientInfo(int id, string endpoint) { Id = id; Endpoint = endpoint; }
    public override string ToString() => Endpoint;
}
