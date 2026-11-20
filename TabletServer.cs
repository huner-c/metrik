using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Metrik;

internal sealed class TabletServer : IDisposable
{
    public const int Port = 47937;
    private readonly UdpClient _socket = new(AddressFamily.InterNetwork);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly byte[] _request;
    private readonly TabletFeed _feed;
    private volatile LanAddress[] _networks = [];
    private volatile string? _error;
    private int _disposed;

    public string Addresses => string.Join(" / ", _networks.Select(static network => network.Address));
    public string? Error => _error;

    public TabletServer(string pin, TabletFeed feed)
    {
        _request = Encoding.ASCII.GetBytes(TabletFeed.Protocol + pin);
        _feed = feed;
        try
        {
            _socket.ExclusiveAddressUse = true;
            _socket.Ttl = 1;
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
        }
        catch
        {
            _socket.Dispose();
            _stop.Dispose();
            throw;
        }
        _worker = Task.Run(() => Task.WhenAll(Receive(_stop.Token), RefreshNetworks(_stop.Token)));
    }

    private async Task RefreshNetworks(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                _networks = FindNetworks();
            } while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task Receive(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var packet = await _socket.ReceiveAsync(stop).ConfigureAwait(false);
                    if (!packet.Buffer.AsSpan().SequenceEqual(_request) || !IsOnLocalNetwork(packet.RemoteEndPoint.Address) ||
                        _feed.Read() is not { } payload) continue;
                    await _socket.SendAsync(payload, packet.RemoteEndPoint, stop).ConfigureAwait(false);
                }
                catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable or SocketError.MessageSize) { }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        catch (SocketException e)
        {
            if (!stop.IsCancellationRequested) _error = e.Message;
        }
    }

    private bool IsOnLocalNetwork(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (!IsPrivate(bytes)) return false;
        uint ip = Value(bytes);
        return Array.Exists(_networks, network => (ip & network.Mask) == network.Subnet);
    }

    private static LanAddress[] FindNetworks()
    {
        try
        {
            var networks = new List<LanAddress>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var entry in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (entry.Address.AddressFamily != AddressFamily.InterNetwork || entry.IPv4Mask is not { } mask) continue;
                    byte[] bytes = entry.Address.GetAddressBytes();
                    if (!IsPrivate(bytes)) continue;
                    uint bits = Value(mask.GetAddressBytes());
                    if (bits != 0) networks.Add(new(entry.Address, bits, Value(bytes) & bits));
                }
            }
            return networks.DistinctBy(static network => network.Address).ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }

    private static bool IsPrivate(byte[] ip) => ip.Length == 4 &&
        (ip[0] == 10 || ip[0] == 172 && ip[1] is >= 16 and <= 31 || ip[0] == 192 && ip[1] == 168);

    private static uint Value(byte[] ip) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip);
    private readonly record struct LanAddress(IPAddress Address, uint Mask, uint Subnet);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _socket.Dispose();
        _ = _worker.ContinueWith(completed =>
        {
            _ = completed.Exception;
            _stop.Dispose();
        }, TaskScheduler.Default);
    }
}
