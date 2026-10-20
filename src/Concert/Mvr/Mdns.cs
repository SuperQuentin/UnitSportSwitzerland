using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace UnitSport.Concert.Mvr;

/// <summary>
/// The few DNS messages MVR-xchange's discovery needs (#960, RFC 1035 / 6762 / 6763): a PTR query for
/// <c>_mvrxchange._tcp.local</c>, the answer a station gives (PTR, SRV, TXT with StationName and
/// StationUUID, A), and the reader for others' answers. Ours is the instance
/// <c>&lt;station&gt;.&lt;group&gt;._mvrxchange._tcp.local</c>, pointed at from the service and from
/// the group's "sub service" <c>&lt;group&gt;._mvrxchange._tcp.local</c> (as Perastage, tested with
/// grandMA3): libMVRgdtf (Vectorworks) and BlenderDMX name theirs <c>&lt;group&gt;._mvrxchange…</c>
/// alone, so every station of a group shares one name and a cache (python-zeroconf) keeps one of
/// them. Readers take the label just before the service as the group, which reads both. Names are
/// written uncompressed; read with compression, bounds and pointer loops checked.
/// </summary>
public static class DnsSd
{
    public const ushort A = 1, Ptr = 12, Txt = 16, Srv = 33, Any = 255;
    private const ushort ClassIn = 1;

    public sealed record Question(string Name, ushort Type);

    /// <summary>A record read: PTR and SRV carry <see cref="Target"/>, SRV <see cref="Port"/>, A <see cref="Address"/>, TXT <see cref="Texts"/>.</summary>
    public sealed record Record(string Name, ushort Type, uint Ttl)
    {
        public string Target { get; init; } = "";
        public int Port { get; init; }
        public IPAddress? Address { get; init; }
        public string[] Texts { get; init; } = Array.Empty<string>();
    }

    public sealed record Message(bool Response, List<Question> Questions, List<Record> Records);

    // ---- writing -------------------------------------------------------------------------------------

    private sealed class Writer
    {
        private readonly MemoryStream _m = new();
        public void U16(int v) { _m.WriteByte((byte)(v >> 8)); _m.WriteByte((byte)v); }
        public void U32(uint v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
        public void Bytes(ReadOnlySpan<byte> b) => _m.Write(b);

        public void Name(string name)
        {
            foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var b = Encoding.UTF8.GetBytes(label);
                if (b.Length > 63) b = b[..63];
                _m.WriteByte((byte)b.Length);
                _m.Write(b);
            }
            _m.WriteByte(0);
        }

        /// <summary>A record whose data is written by <paramref name="data"/>, its length filled in after.</summary>
        public void Record(string name, ushort type, uint ttl, Action<Writer> data, bool flush = false)
        {
            Name(name);
            U16(type);
            U16(ClassIn | (flush ? 0x8000 : 0));
            U32(ttl);
            long at = _m.Position;
            U16(0);
            data(this);
            long end = _m.Position;
            _m.Position = at;
            U16((int)(end - at - 2));
            _m.Position = end;
        }

        public byte[] ToArray() => _m.ToArray();
    }

    public static byte[] Query(params (string Name, ushort Type)[] questions)
    {
        var w = new Writer();
        w.U16(0); w.U16(0); w.U16(questions.Length); w.U16(0); w.U16(0); w.U16(0);
        foreach (var (name, type) in questions)
        {
            w.Name(name);
            w.U16(type);
            w.U16(ClassIn);
        }
        return w.ToArray();
    }

    /// <summary>
    /// A station's announcement: PTR service → instance and PTR group service → instance, SRV
    /// instance → host:port, TXT, A host → address. TTL 0 says goodbye.
    /// </summary>
    public static byte[] Announce(string service, string group, string instance, string host, int port, IPAddress address, IEnumerable<string> texts, uint ttl = 120)
    {
        var w = new Writer();
        w.U16(0); w.U16(0x8400); w.U16(0); w.U16(5); w.U16(0); w.U16(0);
        w.Record(service, Ptr, ttl == 0 ? 0u : 4500u, x => x.Name(instance));
        w.Record($"{group}.{service}", Ptr, ttl == 0 ? 0u : 4500u, x => x.Name(instance));
        w.Record(instance, Srv, ttl, x => { x.U16(0); x.U16(0); x.U16(port); x.Name(host); }, flush: true);
        w.Record(instance, Txt, ttl == 0 ? 0u : 4500u, flush: true, data: x =>
        {
            foreach (var t in texts)
            {
                var b = Encoding.UTF8.GetBytes(t);
                if (b.Length > 255) b = b[..255];
                x.Bytes(new[] { (byte)b.Length });
                x.Bytes(b);
            }
        });
        w.Record(host, A, ttl, x => x.Bytes(address.MapToIPv4().GetAddressBytes()), flush: true);
        return w.ToArray();
    }

    // ---- reading -------------------------------------------------------------------------------------

    /// <summary>A DNS message, or null when it is not one (cut short, a bad name, more than 256 entries).</summary>
    public static Message? Parse(byte[] p)
    {
        try
        {
            if (p.Length < 12) return null;
            int flags = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2));
            int qd = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(4));
            int rr = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6)) + BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(8)) + BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(10));
            if (qd + rr > 256) return null;
            int pos = 12;
            var questions = new List<Question>(qd);
            for (int i = 0; i < qd; i++)
            {
                string name = ReadName(p, ref pos);
                questions.Add(new Question(name, U16(p, pos)));
                pos += 4;
            }
            var records = new List<Record>(rr);
            for (int i = 0; i < rr; i++)
            {
                string name = ReadName(p, ref pos);
                ushort type = U16(p, pos);
                uint ttl = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(pos + 4, 4));
                int len = U16(p, pos + 8);
                pos += 10;
                if (pos + len > p.Length) return null;
                int at = pos;
                var r = new Record(name, type, ttl);
                switch (type)
                {
                    case Ptr: r = r with { Target = ReadName(p, ref at) }; break;
                    case Srv when len >= 7:
                        at += 4;
                        int port = U16(p, at);
                        at += 2;
                        r = r with { Port = port, Target = ReadName(p, ref at) };
                        break;
                    case A when len == 4: r = r with { Address = new IPAddress(p.AsSpan(pos, 4)) }; break;
                    case Txt:
                        var texts = new List<string>();
                        for (int t = pos; t < pos + len;)
                        {
                            int n = p[t];
                            if (t + 1 + n > pos + len) break;
                            texts.Add(Encoding.UTF8.GetString(p, t + 1, n));
                            t += 1 + n;
                        }
                        r = r with { Texts = texts.ToArray() };
                        break;
                }
                records.Add(r);
                pos += len;
            }
            return new Message((flags & 0x8000) != 0, questions, records);
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidDataException) { return null; }
    }

    private static ushort U16(byte[] p, int at) => BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(at, 2));

    private static string ReadName(byte[] p, ref int pos)
    {
        var sb = new StringBuilder();
        int at = pos, jumps = 0;
        bool jumped = false;
        while (true)
        {
            int len = p[at];
            if (len == 0) { at++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (++jumps > 16) throw new InvalidDataException("a name pointer loop");
                int to = ((len & 0x3F) << 8) | p[at + 1];
                if (!jumped) pos = at + 2;
                jumped = true;
                at = to;
                continue;
            }
            if (len > 63) throw new InvalidDataException("a bad label");
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(p, at + 1, len));
            if (sb.Length > 255) throw new InvalidDataException("a name too long");
            at += 1 + len;
        }
        if (!jumped) pos = at;
        return sb.ToString();
    }

    public static bool SameName(string a, string b) => string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The group of an instance name: the label just before the service (null when it is not one of the service's).</summary>
    public static string? GroupOf(string instance, string service)
    {
        string i = instance.TrimEnd('.'), s = "." + service.TrimEnd('.');
        if (!i.EndsWith(s, StringComparison.OrdinalIgnoreCase) || i.Length == s.Length) return null;
        string head = i[..^s.Length];
        return head[(head.LastIndexOf('.') + 1)..];
    }

    /// <summary>
    /// The stations a message announces (PTR → SRV → A, with their TXT), the A from the sender when
    /// the host's is missing; and the instances named without an SRV, to ask for.
    /// </summary>
    public static List<MdnsService> Services(Message m, IPAddress from, string service, List<string>? missing = null)
    {
        var list = new List<MdnsService>();
        // PTRs of the service and of a group's sub service (<group>.<service>)
        var instances = m.Records.Where(r => r.Type == Ptr && (SameName(r.Name, service) || GroupOf(r.Name, service) != null)).Select(r => r.Target)
            .Concat(m.Records.Where(r => r.Type == Srv && GroupOf(r.Name, service) != null).Select(r => r.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in instances)
        {
            if (GroupOf(instance, service) is not { } group) continue;
            var srv = m.Records.FirstOrDefault(r => r.Type == Srv && SameName(r.Name, instance));
            if (srv == null) { missing?.Add(instance); continue; }
            var txt = m.Records.Where(r => r.Type == Txt && SameName(r.Name, instance)).SelectMany(r => r.Texts).ToArray();
            var a = m.Records.FirstOrDefault(r => r.Type == A && SameName(r.Name, srv.Target) && r.Address != null)?.Address ?? from;
            list.Add(new MdnsService(instance, group, Value(txt, "StationName"), Value(txt, "StationUUID"), a, srv.Port, srv.Ttl == 0));
        }
        return list;
    }

    private static string Value(string[] texts, string key)
    {
        foreach (var t in texts)
            if (t.Length > key.Length && t[key.Length] == '=' && t.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                return t[(key.Length + 1)..];
        return "";
    }
}

/// <summary>A station heard on the LAN; <see cref="Gone"/> when it said goodbye (TTL 0).</summary>
public sealed record MdnsService(string Instance, string Group, string StationName, string StationUuid, IPAddress Address, int Port, bool Gone = false);

/// <summary>
/// The mDNS side of an MVR-xchange station (#960): UDP 5353 shared with the system's own responder
/// (address reuse), 224.0.0.251 joined on every IPv4 interface. It announces this station on each
/// interface with that interface's address, answers queries for its names, asks for
/// <c>_mvrxchange._tcp.local</c> at once, after 1 s and 3 s, then every 30 s, and hands every
/// station it hears to <see cref="Found"/> on its receive thread. Goodbye (TTL 0) on dispose.
/// </summary>
public sealed class Mdns : IDisposable
{
    public static readonly IPAddress Multicast = IPAddress.Parse("224.0.0.251");
    public const int Port = 5353;

    /// <summary>A station heard, on the receive thread.</summary>
    public event Action<MdnsService>? Found;

    private Socket? _sock;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _send = new();
    private List<IPAddress> _ifaces = new();
    private string _service = MvrXchange.Service, _group = "", _instance = "", _host = "";
    private int _port;
    private string[] _texts = Array.Empty<string>();
    private long _answeredAt;

    /// <summary>Joins the group and starts announcing; the error when the port could not be shared.</summary>
    public string? Start(string group, string stationName, string stationUuid, int tcpPort)
    {
        // a label of our own (no dots: they would read as more labels), the name itself in the TXT
        string id = stationUuid.Replace("-", "");
        string label = $"UnitSport-{id[..Math.Min(8, id.Length)]}";
        _group = group;
        _instance = $"{label}.{group}.{_service}";
        _host = $"{label}.local";
        _port = tcpPort;
        _texts = new[] { $"StationName={stationName}", $"StationUUID={stationUuid}" };
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.Bind(new IPEndPoint(IPAddress.Any, Port));
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
            _ifaces = Interfaces();
            foreach (var a in _ifaces)
                try { s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Multicast, a)); }
                catch (SocketException) { /* an interface without multicast: the others still hear */ }
            if (_ifaces.Count == 0) s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Multicast));
            _sock = s;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            _sock?.Dispose();
            _sock = null;
            return $"mDNS could not share UDP {Port}: {e.Message}";
        }
        var token = _stop.Token;
        Task.Run(() => ReceiveLoop(token));
        Task.Run(async () =>
        {
            int[] waits = { 0, 1000, 2000 };
            try
            {
                for (int i = 0; !token.IsCancellationRequested; i++)
                {
                    await Task.Delay(i < waits.Length ? waits[i] : 30000, token);
                    if (i < 2) Announce(120);
                    Query();
                }
            }
            catch (OperationCanceledException) { }
        });
        return null;
    }

    /// <summary>Asks the LAN for MVR-xchange stations.</summary>
    public void Query() => Send(_ => DnsSd.Query((_service, DnsSd.Ptr)));

    private void Announce(uint ttl) => Send(a => DnsSd.Announce(_service, _group, _instance, _host, _port, a, _texts, ttl));

    private static List<IPAddress> Interfaces()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || !ni.SupportsMulticast) continue;
                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(u.Address);
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }

    /// <summary>Sends a packet out of every interface (built per interface: an announcement carries its address).</summary>
    private void Send(Func<IPAddress, byte[]> packet)
    {
        if (_sock is not { } s) return;
        var to = new IPEndPoint(Multicast, Port);
        lock (_send)
        {
            var ifaces = _ifaces.Count > 0 ? _ifaces : new List<IPAddress> { IPAddress.Loopback };
            foreach (var a in ifaces)
                try
                {
                    if (_ifaces.Count > 0) s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, a.GetAddressBytes());
                    s.SendTo(packet(a), to);
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[9000];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested && _sock is { } s)
        {
            SocketReceiveFromResult r;
            try { r = await s.ReceiveFromAsync(buf, SocketFlags.None, any, ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }   // an ICMP unreachable reported on Windows: keep listening
            var from = ((IPEndPoint)r.RemoteEndPoint).Address;
            if (DnsSd.Parse(buf[..r.ReceivedBytes]) is not { } m) continue;
            if (!m.Response)
            {
                // a query for any of our names: answer, at most once a second
                if (m.Questions.Any(q => DnsSd.SameName(q.Name, _service) || DnsSd.SameName(q.Name, $"{_group}.{_service}") || DnsSd.SameName(q.Name, _instance) || DnsSd.SameName(q.Name, _host))
                    && Environment.TickCount64 - Interlocked.Read(ref _answeredAt) > 1000)
                {
                    Interlocked.Exchange(ref _answeredAt, Environment.TickCount64);
                    Announce(120);
                }
                continue;
            }
            var missing = new List<string>();
            foreach (var found in DnsSd.Services(m, from, _service, missing))
                try { Found?.Invoke(found); }
                catch (Exception e) { Console.Error.WriteLine($"[mvrxchange] {e.Message}"); }
            // a PTR without its SRV: ask the instance itself
            if (missing.Count > 0) Send(_ => DnsSd.Query(missing.Take(4).Select(i => (i, DnsSd.Any)).ToArray()));
        }
    }

    public void Dispose()
    {
        if (_sock == null) return;
        Announce(0);
        _stop.Cancel();
        _sock.Dispose();
        _sock = null;
    }
}
