using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace UnitSport.Net;

/// <summary>A dedicated server found on the local network.</summary>
public sealed record LanServer(string Name, string Address, int Port, string Version)
{
    /// <summary>What the menu's server field and <see cref="NetworkManager.ParseEndpoint"/> take.</summary>
    public string Endpoint => Address.Contains(':') ? $"[{Address}]:{Port}" : $"{Address}:{Port}";
}

/// <summary>
/// Finds dedicated servers on the LAN over mDNS: the server box advertises
/// <c>_unitsport._udp</c> with avahi (<c>tools/deploy/unitsport.service.xml</c>), this browses it.
///
/// <para>
/// Queries go out from an ephemeral port, never 5353. That makes them "legacy unicast" queries
/// (RFC 6762 §6.7): the responder answers straight back to the sender, so there is no multicast
/// group to join and no clash with the mDNS responder Windows and macOS already run on 5353.
/// One socket per IPv4 interface, because a multicast send only leaves on one interface and a
/// PC with WSL, a VPN or Tailscale has several.
/// </para>
///
/// <para>
/// Sockets and parsing live on background tasks; results cross to the main thread through a
/// queue that <see cref="Poll"/> drains, so nothing here touches the scene tree.
/// </para>
/// </summary>
public sealed class LanDiscovery : IDisposable
{
    public const string ServiceType = "_unitsport._udp.local";
    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);
    private const int QueryIntervalMs = 3000;
    private const double ExpireSeconds = 15;

    private const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeAaaa = 28, TypeSrv = 33;

    private readonly ConcurrentQueue<(LanServer Server, bool Gone)> _events = new();
    private readonly Dictionary<string, (LanServer Server, double Seen)> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UdpClient> _sockets = new();
    private CancellationTokenSource? _cts;

    /// <summary>Servers heard from recently, by name.</summary>
    public IReadOnlyList<LanServer> Servers => _servers.Values.Select(v => v.Server).OrderBy(s => s.Name).ToList();

    public bool Running => _cts != null;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        foreach (var address in LocalIPv4())
        {
            try
            {
                var udp = new UdpClient(new IPEndPoint(address, 0));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                _sockets.Add(udp);
                var token = _cts.Token;
                Task.Run(() => ReceiveLoop(udp, token));
            }
            catch (SocketException e)
            {
                Godot.GD.PushWarning($"[discovery] no socket on {address}: {e.Message}");
            }
        }
        var t = _cts.Token;
        Task.Run(() => QueryLoop(t));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        foreach (var s in _sockets) s.Dispose();
        _sockets.Clear();
        _servers.Clear();
        _events.Clear();
    }

    public void Dispose() => Stop();

    /// <summary>Main thread: applies what the sockets heard and drops silent servers. True when the list changed.</summary>
    public bool Poll()
    {
        double now = Environment.TickCount64 / 1000.0;
        bool changed = false;
        while (_events.TryDequeue(out var e))
        {
            if (e.Gone) changed |= _servers.Remove(e.Server.Name);
            else
            {
                changed |= !_servers.TryGetValue(e.Server.Name, out var old) || old.Server != e.Server;
                _servers[e.Server.Name] = (e.Server, now);
            }
        }
        foreach (var name in _servers.Where(kv => now - kv.Value.Seen > ExpireSeconds).Select(kv => kv.Key).ToList())
            changed |= _servers.Remove(name);
        return changed;
    }

    private static IEnumerable<IPAddress> LocalIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct();

    private async Task QueryLoop(CancellationToken token)
    {
        byte[] browse = Query((ServiceType.Split('.'), TypePtr));
        while (!token.IsCancellationRequested)
        {
            Send(browse);
            try { await Task.Delay(QueryIntervalMs, token); } catch (OperationCanceledException) { return; }
        }
    }

    private void Send(byte[] packet)
    {
        foreach (var s in _sockets.ToArray())
        {
            try { s.Send(packet, packet.Length, Group); }
            catch (Exception) { /* interface went away or was disposed; the next round retries */ }
        }
    }

    private async Task ReceiveLoop(UdpClient udp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult got;
            try { got = await udp.ReceiveAsync(token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }   // e.g. ICMP unreachable reported on the next receive (Windows)
            try { Handle(got.Buffer, got.RemoteEndPoint.Address); }
            catch (Exception) { /* malformed packet: ignore it */ }
        }
    }

    /// <summary>
    /// One response: PTR names the instances, SRV gives the port (and host), A the address.
    /// A responder that sends the PTR without the rest is asked for the SRV and TXT directly.
    /// Without an A record the sender's own address is used: the box that answers is the server.
    /// </summary>
    private void Handle(byte[] p, IPAddress from)
    {
        if (p.Length < 12 || (p[2] & 0x80) == 0) return;   // not a response
        int qd = U16(p, 4), records = U16(p, 6) + U16(p, 8) + U16(p, 10);
        int o = 12;
        for (int i = 0; i < qd; i++) { ReadName(p, ref o); o += 4; }

        var ic = StringComparer.OrdinalIgnoreCase;   // DNS names compare without case
        var instances = new Dictionary<string, bool>(ic);   // name -> alive (ttl > 0)
        var srv = new Dictionary<string, (string Target, int Port)>(ic);
        var txt = new Dictionary<string, string>(ic);
        var hosts = new Dictionary<string, string>(ic);
        for (int i = 0; i < records; i++)
        {
            string name = ReadName(p, ref o);
            ushort type = U16(p, o);
            uint ttl = (uint)(U16(p, o + 4) << 16 | U16(p, o + 6));
            int len = U16(p, o + 8);
            int data = o + 10;
            o = data + len;
            if (o > p.Length) return;
            switch (type)
            {
                case TypePtr when Same(name, ServiceType):
                    int d = data;
                    instances[ReadName(p, ref d)] = ttl > 0;
                    break;
                case TypeSrv:
                    int t = data + 6;
                    srv[name] = (ReadName(p, ref t), U16(p, data + 4));
                    break;
                case TypeTxt:
                    txt[name] = ReadTxt(p, data, len, "version");
                    break;
                case TypeA when len == 4:
                    hosts[name] = new IPAddress(p.AsSpan(data, 4)).ToString();
                    break;
                case TypeAaaa when len == 16:
                    hosts.TryAdd(name, new IPAddress(p.AsSpan(data, 16)).ToString());
                    break;
            }
        }
        // a direct SRV answer (to our follow-up query) names its instance without a PTR
        foreach (string key in srv.Keys) if (key.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase)) instances.TryAdd(key, true);

        foreach (var (instance, alive) in instances)
        {
            string label = instance.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase)
                ? instance[..^(ServiceType.Length + 1)] : instance;
            if (!alive) { _events.Enqueue((new LanServer(label, "", 0, ""), true)); continue; }
            if (!srv.TryGetValue(instance, out var s))
            {
                // the instance label may itself hold dots ("Server v1.2"): it is one label on the wire
                string[] name = new[] { label }.Concat(ServiceType.Split('.')).ToArray();
                Send(Query((name, TypeSrv), (name, TypeTxt)));
                continue;
            }
            string address = hosts.TryGetValue(s.Target, out var a) ? a : from.ToString();
            _events.Enqueue((new LanServer(label, address, s.Port, txt.GetValueOrDefault(instance, "")), false));
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static ushort U16(byte[] p, int o) => (ushort)(p[o] << 8 | p[o + 1]);

    /// <summary>A DNS name, following compression pointers (bounded, so a looping packet cannot hang us).</summary>
    private static string ReadName(byte[] p, ref int o)
    {
        var labels = new List<string>();
        int at = o, jumps = 0;
        bool jumped = false;
        while (true)
        {
            int len = p[at];
            if (len == 0) { at++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (++jumps > 16) throw new FormatException("name pointer loop");
                if (!jumped) o = at + 2;
                jumped = true;
                at = (len & 0x3F) << 8 | p[at + 1];
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(p, at + 1, len));
            at += 1 + len;
        }
        if (!jumped) o = at;
        return string.Join('.', labels);
    }

    private static string ReadTxt(byte[] p, int o, int len, string key)
    {
        for (int end = o + len; o < end;)
        {
            int n = p[o];
            string kv = Encoding.UTF8.GetString(p, o + 1, Math.Min(n, end - o - 1));
            o += 1 + n;
            if (kv.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return kv[(key.Length + 1)..];
        }
        return "";
    }

    private static byte[] Query(params (string[] Labels, ushort Type)[] questions)
    {
        var b = new List<byte> { 0, 0, 0, 0, 0, (byte)questions.Length, 0, 0, 0, 0, 0, 0 };
        foreach (var (labels, type) in questions)
        {
            foreach (string label in labels)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(label);
                b.Add((byte)bytes.Length);
                b.AddRange(bytes);
            }
            b.Add(0);
            b.AddRange(new byte[] { (byte)(type >> 8), (byte)type, 0, 1 });   // class IN
        }
        return b.ToArray();
    }
}
