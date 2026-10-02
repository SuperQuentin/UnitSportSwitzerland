using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace UnitSport.Net;

/// <summary>A server that answered a status query, and how fast.</summary>
public sealed record QueryResult(string Endpoint, ServerStatus Status, int PingMs, bool Local);

/// <summary>
/// The client side of the status query (<see cref="QueryResponder"/>): broadcasts on every LAN
/// interface to find servers nobody typed in, and probes saved servers one by one for their
/// player count and ping.
///
/// <para>
/// One UDP socket on a background receive loop; results cross to the main thread through a
/// queue that <see cref="Poll"/> drains, the same pattern as <see cref="LanDiscovery"/>. Nothing
/// here touches the scene tree.
/// </para>
/// </summary>
public sealed class ServerQuery : IDisposable
{
    public const int Proto = 1;
    public const string QueryMagic = "USQ1", ReplyMagic = "USR1";
    private const double ExpireSeconds = 8;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentQueue<(string? ProbeKey, QueryResult Result)> _replies = new();
    // nonce -> (probe key or null for a broadcast round, when it was sent)
    private readonly ConcurrentDictionary<uint, (string? Key, long SentMs)> _pending = new();
    private readonly Dictionary<string, (QueryResult Result, double Seen)> _lan = new();
    private readonly Dictionary<string, (QueryResult Result, double Seen)> _probed = new();
    private readonly Dictionary<string, List<int>> _pings = new();
    private readonly HashSet<IPAddress> _localAddresses = new();
    private uint _nextNonce = (uint)Random.Shared.Next();

    public bool Running => _udp != null;

    /// <summary>Servers found by broadcast, by endpoint.</summary>
    public IReadOnlyList<QueryResult> Lan => _lan.Values.Select(v => v.Result).OrderBy(r => r.Status.Name).ToList();

    /// <summary>The last answer of a probed server, or null when it has not answered (recently).</summary>
    public QueryResult? Probed(string endpoint) => _probed.TryGetValue(Key(endpoint), out var v) ? v.Result : null;

    public void Start()
    {
        if (_udp != null) return;
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        }
        catch (SocketException e)
        {
            Godot.GD.PushWarning($"[query] no socket: {e.Message}");
            return;
        }
        foreach (var a in LocalIPv4().Select(x => x.Address)) _localAddresses.Add(a);
        _localAddresses.Add(IPAddress.Loopback);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var udp = _udp;
        Task.Run(() => Udp.ReceiveLoop(udp, token, (p, from) => Handle(p, from.Address)));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _udp?.Dispose();
        _udp = null;
        _pending.Clear();
    }

    public void Dispose() => Stop();

    /// <summary>
    /// One broadcast round: every interface's directed broadcast, the limited broadcast and the
    /// loopback (a server on this very machine may not hear its own broadcast on Windows).
    /// </summary>
    public void Broadcast()
    {
        if (_udp == null) return;
        uint nonce = NextNonce(null);
        var packet = Packet(nonce);
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        foreach (var (address, mask) in LocalIPv4())
        {
            if (mask == null) continue;
            var a = address.GetAddressBytes();
            var m = mask.GetAddressBytes();
            var b = new byte[4];
            for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
            targets.Add(new IPAddress(b));
        }
        // the default port and the next few: a second server on one machine, or one hosted on
        // a port of its own, is found too
        foreach (var t in targets)
            for (int port = NetworkManager.DefaultPort + 1; port <= NetworkManager.DefaultPort + 1 + BroadcastPorts; port++)
                SendTo(packet, new IPEndPoint(t, port));
    }

    /// <summary>How many game ports past the default a LAN broadcast also asks (7777..7787).</summary>
    public const int BroadcastPorts = 10;

    /// <summary>Asks one server (by its game endpoint) for its status; the answer lands in <see cref="Probed"/>.</summary>
    public void Probe(string endpoint)
    {
        if (_udp == null) return;
        var (host, port) = NetworkManager.ParseEndpoint(endpoint);
        string key = Key(endpoint);
        uint nonce = NextNonce(key);
        var packet = Packet(nonce);
        if (IPAddress.TryParse(host, out var ip)) { SendTo(packet, new IPEndPoint(ip, port + 1)); return; }
        _ = Task.Run(async () =>
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host);
                var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                if (v4 != null) SendTo(packet, new IPEndPoint(v4, port + 1));
            }
            catch (Exception) { /* unknown host: it simply never answers */ }
        });
    }

    /// <summary>Main thread: applies the replies, drops servers gone quiet. True when anything changed.</summary>
    public bool Poll()
    {
        double now = Environment.TickCount64 / 1000.0;
        bool changed = false;
        while (_replies.TryDequeue(out var r))
        {
            var result = r.Result;
            if (r.ProbeKey is { } key)
            {
                result = result with { PingMs = SmoothPing(key, result.PingMs) };
                _probed[key] = (result, now);
            }
            else
            {
                string k = Key(result.Endpoint);
                result = result with { PingMs = SmoothPing("lan:" + k, result.PingMs) };
                _lan[k] = (result, now);
            }
            changed = true;
        }
        foreach (var dict in new[] { _lan, _probed })
            foreach (var k in dict.Where(kv => now - kv.Value.Seen > ExpireSeconds).Select(kv => kv.Key).ToList())
            {
                dict.Remove(k);
                changed = true;
            }
        // a nonce never answered is forgotten after a while
        long nowMs = Environment.TickCount64;
        foreach (var n in _pending.Where(kv => nowMs - kv.Value.SentMs > 10_000).Select(kv => kv.Key).ToList())
            _pending.TryRemove(n, out _);
        return changed;
    }

    /// <summary>The median of the last three, so one slow packet does not make a server look far away.</summary>
    private int SmoothPing(string key, int ms)
    {
        if (!_pings.TryGetValue(key, out var list)) _pings[key] = list = new List<int>();
        list.Add(ms);
        if (list.Count > 3) list.RemoveAt(0);
        var sorted = list.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }

    private static string Key(string endpoint)
    {
        var (host, port) = NetworkManager.ParseEndpoint(endpoint);
        return $"{host.ToLowerInvariant()}:{port}";
    }

    private uint NextNonce(string? key)
    {
        uint n = unchecked(++_nextNonce);
        _pending[n] = (key, Environment.TickCount64);
        return n;
    }

    private static byte[] Packet(uint nonce)
    {
        var p = new byte[8];
        Encoding.ASCII.GetBytes(QueryMagic, 0, 4, p, 0);
        BitConverter.TryWriteBytes(p.AsSpan(4), nonce);
        return p;
    }

    internal static bool Matches(byte[] p, string magic) =>
        p.Length >= 4 && p[0] == magic[0] && p[1] == magic[1] && p[2] == magic[2] && p[3] == magic[3];

    private void SendTo(byte[] packet, IPEndPoint to)
    {
        try { _udp?.Send(packet, packet.Length, to); }
        catch (Exception) { /* an interface without a route: the others still go */ }
    }

    private void Handle(byte[] p, IPAddress from)
    {
        if (p.Length < 9 || !Matches(p, ReplyMagic)) return;
        uint nonce = BitConverter.ToUInt32(p, 4);
        if (!_pending.TryGetValue(nonce, out var sent)) return;
        // a probe's nonce is answered once; a broadcast round's by every server that hears it
        if (sent.Key != null) _pending.TryRemove(nonce, out _);
        var status = JsonSerializer.Deserialize<ServerStatus>(p.AsSpan(8), Json);
        if (status == null || status.Port <= 0) return;
        int ping = (int)Math.Max(0, Environment.TickCount64 - sent.SentMs);
        if (from.IsIPv4MappedToIPv6) from = from.MapToIPv4();
        bool local = _localAddresses.Contains(from) || IPAddress.IsLoopback(from);
        // this machine answers on every interface it has; list it once, as itself
        string endpoint = local ? $"127.0.0.1:{status.Port}" : $"{from}:{status.Port}";
        _replies.Enqueue((sent.Key, new QueryResult(endpoint, status, ping, local)));
    }

    private static IEnumerable<(IPAddress Address, IPAddress? Mask)> LocalIPv4()
    {
        IEnumerable<UnicastIPAddressInformation> all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).ToList();
        }
        catch (Exception) { yield break; }
        foreach (var u in all)
            if (u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address))
            {
                IPAddress? mask = null;
                try { mask = u.IPv4Mask; } catch (Exception) { }
                yield return (u.Address, mask is { } m && !m.Equals(IPAddress.Any) ? m : null);
            }
    }
}
