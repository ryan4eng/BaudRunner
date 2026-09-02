using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace BaudRunner.Tests;

/// <summary>
/// Exercises the real transport over loopback. These are the paths that used to
/// leak sockets, spuriously report disconnects, or race an overlapping Open.
/// </summary>
public class TransportSessionTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [Fact]
    public async Task Tcp_server_receives_from_a_client_and_can_reply()
    {
        var port = FreePort();
        await using var server = new TransportSession(TransportKind.TcpServer);
        var received = new List<byte>();
        server.BytesReceived += bytes => { lock (received) received.AddRange(bytes.ToArray()); };
        var clients = Array.Empty<TcpClientInfo>();
        server.TcpClientsChanged += list => clients = list.ToArray();

        await server.OpenAsync("", port, new SerialSettings());
        Assert.True(server.IsOpen);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        Assert.True(await WaitFor(() => clients.Length == 1));

        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("ping"));
        Assert.True(await WaitFor(() => { lock (received) return received.Count == 4; }));
        Assert.Equal("ping", Encoding.ASCII.GetString(received.ToArray()));
        Assert.Equal(4, server.BytesReceivedCount);

        await server.SendAsync(Encoding.ASCII.GetBytes("pong"), clients[0].Id);
        var buffer = new byte[16];
        var count = await client.GetStream().ReadAsync(buffer);
        Assert.Equal("pong", Encoding.ASCII.GetString(buffer, 0, count));
        Assert.Equal(4, server.BytesSentCount);
    }

    [Fact]
    public async Task Tcp_server_refuses_to_send_when_the_selected_client_is_gone()
    {
        var port = FreePort();
        await using var server = new TransportSession(TransportKind.TcpServer);
        await server.OpenAsync("", port, new SerialSettings());

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.SendAsync(new byte[] { 1 }, selectedClientId: 999));
    }

    [Fact]
    public async Task Tcp_client_reports_the_reason_when_the_peer_closes()
    {
        var port = FreePort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        await using var session = new TransportSession(TransportKind.TcpClient);
        string? reason = null;
        session.ConnectionLost += r => reason = r;

        await session.OpenAsync("127.0.0.1", port, new SerialSettings());
        var accepted = await listener.AcceptTcpClientAsync();
        accepted.Close();

        Assert.True(await WaitFor(() => reason is not null));
        Assert.False(session.IsOpen);
        listener.Stop();
    }

    [Fact]
    public async Task Opening_twice_concurrently_does_not_leave_a_half_built_session()
    {
        var port = FreePort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(async () => { while (true) { var c = await listener.AcceptTcpClientAsync(); _ = c; } });

        await using var session = new TransportSession(TransportKind.TcpClient);
        var settings = new SerialSettings();
        // The lifetime semaphore serialises these; without it the second Open tears
        // down the first one's cancellation source and stream mid-flight.
        await Task.WhenAll(
            session.OpenAsync("127.0.0.1", port, settings),
            session.OpenAsync("127.0.0.1", port, settings));

        Assert.True(session.IsOpen);
        await session.SendAsync(Encoding.ASCII.GetBytes("still alive"));
        listener.Stop();
    }

    [Fact]
    public async Task Close_is_idempotent_and_leaves_the_session_reusable()
    {
        var port = FreePort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(async () => { while (true) { var c = await listener.AcceptTcpClientAsync(); _ = c; } });

        await using var session = new TransportSession(TransportKind.TcpClient);
        await session.OpenAsync("127.0.0.1", port, new SerialSettings());
        await session.CloseAsync();
        await session.CloseAsync();
        Assert.False(session.IsOpen);

        await session.OpenAsync("127.0.0.1", port, new SerialSettings());
        Assert.True(session.IsOpen);
        listener.Stop();
    }

    [Fact]
    public async Task A_failed_open_reports_the_error_and_leaves_the_session_closed()
    {
        await using var session = new TransportSession(TransportKind.TcpClient);
        // Port 1 on loopback with nothing listening.
        await Assert.ThrowsAnyAsync<Exception>(() => session.OpenAsync("127.0.0.1", 1, new SerialSettings()));
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task Close_aborts_a_connect_that_is_still_in_progress()
    {
        await using var session = new TransportSession(TransportKind.TcpClient);
        // A non-routable address: the SYN goes nowhere and the connect would sit in
        // the OS timeout, which Close used to have to wait out.
        var open = session.OpenAsync("10.255.255.1", 9, new SerialSettings());
        await Task.Delay(200);

        var close = session.CloseAsync();
        var settled = Task.WhenAll(close, open.ContinueWith(t => _ = t.Exception));
        Assert.Same(settled, await Task.WhenAny(settled, Task.Delay(5000)));
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task Sending_before_open_throws_rather_than_dereferencing_null()
    {
        await using var session = new TransportSession(TransportKind.TcpClient);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task Udp_client_resolves_a_host_name_rather_than_demanding_a_literal_address()
    {
        await using var session = new TransportSession(TransportKind.UdpClient);
        await session.OpenAsync("localhost", FreePort(), new SerialSettings());
        Assert.True(session.IsOpen);
    }

    [Fact]
    public async Task Udp_server_receives_a_datagram_and_can_reply_to_the_sender()
    {
        var port = FreePort();
        await using var server = new TransportSession(TransportKind.UdpServer);
        var received = new List<byte>();
        server.BytesReceived += bytes => { lock (received) received.AddRange(bytes.ToArray()); };
        await server.OpenAsync("", port, new SerialSettings());

        using var client = new UdpClient(0);
        await client.SendAsync(Encoding.ASCII.GetBytes("hi"), new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(await WaitFor(() => { lock (received) return received.Count == 2; }));

        await server.SendAsync(Encoding.ASCII.GetBytes("yo"));
        var reply = await client.ReceiveAsync();
        Assert.Equal("yo", Encoding.ASCII.GetString(reply.Buffer));
    }

    [Fact]
    public async Task Udp_server_survives_an_icmp_port_unreachable_from_an_earlier_send()
    {
        var port = FreePort();
        await using var server = new TransportSession(TransportKind.UdpServer);
        var lost = false;
        server.ConnectionLost += _ => lost = true;
        var received = new List<byte>();
        server.BytesReceived += bytes => { lock (received) received.AddRange(bytes.ToArray()); };
        await server.OpenAsync("", port, new SerialSettings());

        // Make the server reply to a socket that is then closed: Windows surfaces the
        // resulting ICMP as WSAECONNRESET on the next receive, which used to kill the loop.
        var client = new UdpClient(0);
        var clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        await client.SendAsync(Encoding.ASCII.GetBytes("a"), new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(await WaitFor(() => { lock (received) return received.Count == 1; }));
        client.Close();

        await server.SendAsync(Encoding.ASCII.GetBytes("orphan"));
        await Task.Delay(300);

        using var second = new UdpClient(clientPort);
        await second.SendAsync(Encoding.ASCII.GetBytes("b"), new IPEndPoint(IPAddress.Loopback, port));
        Assert.True(await WaitFor(() => { lock (received) return received.Count == 2; }));
        Assert.False(lost);
        Assert.True(server.IsOpen);
    }

    [Fact]
    public async Task Tcp_server_caps_the_client_count_and_keeps_serving()
    {
        var port = FreePort();
        await using var server = new TransportSession(TransportKind.TcpServer);
        var clients = Array.Empty<TcpClientInfo>();
        server.TcpClientsChanged += list => clients = list.ToArray();
        await server.OpenAsync("", port, new SerialSettings());

        var connected = new List<TcpClient>();
        for (var i = 0; i < TransportSession.MaxClients + 2; i++)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            connected.Add(client);
        }
        Assert.True(await WaitFor(() => clients.Length == TransportSession.MaxClients));
        Assert.True(server.IsOpen);
        foreach (var client in connected) client.Close();
    }
}
