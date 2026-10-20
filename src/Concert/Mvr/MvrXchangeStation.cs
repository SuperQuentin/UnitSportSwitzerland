using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using static UnitSport.Concert.Mvr.MvrXchange;

namespace UnitSport.Concert.Mvr;

/// <summary>
/// An MVR-xchange station (#960), TCP mode: a listener for other stations' JOIN, COMMIT, REQUEST and
/// LEAVE, a client that sends ours (a connection per message, as libMVRgdtf and BlenderDMX do; ours
/// also serves several on one), mDNS discovery (<see cref="Mdns"/>) that JOINs every station of the
/// group it hears, and WebSocket mode to a server's URL. Pure .NET: every socket on a task, never the
/// caller's thread; what the game must hear arrives in <see cref="Events"/>, drained on the main
/// thread. A pulled file is written to <see cref="SaveFolder"/> here, on the network task.
/// </summary>
public sealed class MvrXchangeStation : IDisposable
{
    /// <summary>A station of the group: known from mDNS, a direct join, its JOIN or a commit.</summary>
    public sealed record Peer(string Uuid, string Name, string Provider, IPEndPoint? EndPoint, bool Joined, bool ViaWebSocket)
    {
        public long TriedAt { get; init; } = long.MinValue;
    }

    /// <summary>A revision another station offers, and where to ask for it.</summary>
    public sealed record Offer(Commit Commit, string StationName, IPEndPoint? From, bool ViaWebSocket);

    /// <summary>A line for the player; <see cref="Saved"/> the path of a file pulled.</summary>
    public sealed record Event(string Line, string? Saved = null, Offer? Pulled = null);

    public const int MaxConnections = 16, MaxOffers = 256, MaxOurs = 8;
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4), ReplyTimeout = TimeSpan.FromSeconds(10),
        FileTimeout = TimeSpan.FromMinutes(2), IdleTimeout = TimeSpan.FromSeconds(60);

    public string Group { get; }
    public string StationName { get; }
    public string StationUuid { get; }
    /// <summary>The TCP port listened on (what mDNS advertises).</summary>
    public int Port { get; private set; }
    public bool Listening => _listener != null;
    public string? MdnsError { get; private set; }
    public Uri? WebSocketUrl { get; private set; }
    public bool WebSocketOpen => _ws?.State == WebSocketState.Open;
    /// <summary>Where pulled files are written (made if missing).</summary>
    public string SaveFolder { get; init; } = Path.GetTempPath();

    public readonly ConcurrentQueue<Event> Events = new();

    private readonly object _lock = new();
    private readonly Dictionary<string, Peer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Offer> _offers = new();
    private readonly List<(Commit Commit, byte[] File)> _ours = new();
    private readonly Dictionary<string, MdnsService> _lan = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Mdns? _mdns;
    private int _connections;
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _wsSend = new(1, 1), _wsPulling = new(1, 1);
    private TaskCompletionSource<(byte[]? File, string Why)>? _wsPull;

    public MvrXchangeStation(string group, string stationName, string stationUuid)
    {
        Group = group;
        StationName = stationName;
        StationUuid = stationUuid;
    }

    /// <summary>A group name as stations write it: letters, digits, '-' and '_' (libMVRgdtf strips the rest).</summary>
    public static string CleanGroup(string group)
    {
        string g = string.Concat(group.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        return g.Length == 0 ? "Default" : g.Length > 63 ? g[..63] : g;
    }

    private void Say(string line) => Events.Enqueue(new Event(line));

    // ---- start and stop ------------------------------------------------------------------------------

    /// <summary>Listens on <paramref name="bind"/> (port 0: any free one) and, with <paramref name="discovery"/>, joins mDNS; the line for the chat.</summary>
    public string Start(IPAddress bind, int port = 0, bool discovery = true)
    {
        try
        {
            _listener = new TcpListener(bind, port);
            _listener.Start(MaxConnections);
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }
        catch (SocketException e)
        {
            _listener = null;
            return $"MVR-xchange could not listen on TCP {port}: {e.Message}";
        }
        var token = _stop.Token;
        Task.Run(() => AcceptLoop(_listener, token));
        if (!discovery) return $"MVR-xchange group {Group}: listening on TCP {Port}, no discovery.";
        _mdns = new Mdns();
        _mdns.Found += OnFound;
        MdnsError = _mdns.Start(Group, StationName, StationUuid, Port);
        return MdnsError == null
            ? $"MVR-xchange group {Group} as \"{StationName}\": listening on TCP {Port}, looking for stations (mDNS)."
            : $"MVR-xchange group {Group}: listening on TCP {Port}; {MdnsError}. Join a station with /concert xchange join <host:port>.";
    }

    /// <summary>LEAVEs the group (on a task, briefly), then closes every socket.</summary>
    public void Stop()
    {
        Peer[] joined;
        lock (_lock) joined = _peers.Values.Where(p => p.Joined && p.EndPoint != null && !p.ViaWebSocket).ToArray();
        _mdns?.Dispose();
        _mdns = null;
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
        var ws = _ws;
        Task.Run(async () =>
        {
            var leave = LeaveMessage(StationUuid);
            var sends = joined.Select(p => Exchange(p.EndPoint!, leave, MaxJson, TimeSpan.FromSeconds(2))).ToList();
            if (ws?.State == WebSocketState.Open) sends.Add(WsSend(ws, leave).ContinueWith(_ => (Received?)null));
            try { await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
            if (ws != null)
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "leave", new CancellationTokenSource(1000).Token); } catch (Exception) { }
            _stop.Cancel();
        });
    }

    public void Dispose() => Stop();

    // ---- what the game shows -------------------------------------------------------------------------

    public Offer[] Offers() { lock (_lock) return _offers.ToArray(); }
    public Peer[] Peers() { lock (_lock) return _peers.Values.ToArray(); }
    public MdnsService[] Lan() { lock (_lock) return _lan.Values.ToArray(); }
    public Commit[] Ours() { lock (_lock) return _ours.Select(o => o.Commit).ToArray(); }

    // ---- discovery -----------------------------------------------------------------------------------

    private void OnFound(MdnsService s)
    {
        if (Same(s.StationUuid, StationUuid)) return;
        string key = $"{s.Group}|{s.Address}:{s.Port}";
        IPEndPoint? join = null;
        lock (_lock)
        {
            if (s.Gone) { _lan.Remove(key); return; }
            _lan[key] = s;
            if (!s.Group.Equals(Group, StringComparison.OrdinalIgnoreCase)) return;
            string id = s.StationUuid.Length > 0 ? s.StationUuid : $"{s.Address}:{s.Port}";
            var ep = new IPEndPoint(s.Address, s.Port);
            var p = _peers.GetValueOrDefault(id) ?? new Peer(id, s.StationName, "", ep, false, false);
            long now = Environment.TickCount64;
            p = p with { EndPoint = ep, Name = s.StationName.Length > 0 ? s.StationName : p.Name };
            // join each station once, again after 30 s when it did not answer
            if (!p.Joined && now - p.TriedAt > 30000) { p = p with { TriedAt = now }; join = ep; }
            _peers[id] = p;
        }
        if (join != null) Task.Run(() => JoinAsync(join, quiet: true));
    }

    /// <summary>A direct join to a station by its address (no mDNS needed).</summary>
    public void Join(IPEndPoint ep) => Task.Run(() => JoinAsync(ep, quiet: false));

    private async Task JoinAsync(IPEndPoint ep, bool quiet)
    {
        Commit[] ours;
        lock (_lock) ours = _ours.Select(o => o.Commit).ToArray();
        try
        {
            var r = await Exchange(ep, JoinMessage(StationName, StationUuid, ours), MaxJson, ReplyTimeout);
            if (r?.Json is not { } m || TypeOf(m) != JoinRet) { Say($"MVR-xchange: {ep} did not answer the JOIN."); return; }
            if (m.ContainsKey("OK") && !Bool(m, "OK")) { Say($"MVR-xchange: {ep} refused the JOIN: {Str(m, "Message")}"); return; }
            var (peer, added) = Register(m, ep, false);
            if (peer != null) Say($"MVR-xchange: joined {peer.Name} ({peer.Provider}) at {ep}: {added} file(s) offered.");
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or InvalidDataException)
        {
            if (!quiet || !_stop.IsCancellationRequested) Say($"MVR-xchange: could not join {ep}: {Reason(e)}");
        }
    }

    private static string Reason(Exception e) => e is OperationCanceledException ? "no answer in time" : e.Message;

    /// <summary>Takes a JOIN or JOIN_RET's station and commits; the peer and how many offers it added.</summary>
    private (Peer? Peer, int Added) Register(JsonObject m, IPEndPoint? ep, bool viaWs)
    {
        string uuid = Str(m, "StationUUID");
        if (uuid.Length == 0 || Same(uuid, StationUuid)) return (null, 0);
        var commits = CommitsOf(m);
        lock (_lock)
        {
            // a station joined by address is keyed by it until it says its UUID
            if (ep != null && _peers.Remove($"{ep.Address}:{ep.Port}", out var byAddress) && !_peers.ContainsKey(uuid)) _peers[uuid] = byAddress;
            var old = _peers.GetValueOrDefault(uuid);
            var p = new Peer(uuid, Str(m, "StationName") is { Length: > 0 } n ? n : old?.Name ?? "?", Str(m, "Provider") is { Length: > 0 } pr ? pr : old?.Provider ?? "",
                ep ?? old?.EndPoint, true, viaWs) { TriedAt = old?.TriedAt ?? long.MinValue };
            _peers[uuid] = p;
            int added = commits.Count(c => AddOffer(c, p));
            return (p, added);
        }
    }

    /// <summary>Adds (or refreshes) an offer, under the lock; false when it is ours or not for us.</summary>
    private bool AddOffer(Commit c, Peer from)
    {
        if (Same(c.StationUuid, StationUuid) || !c.For(StationUuid)) return false;
        string name = _peers.GetValueOrDefault(c.StationUuid)?.Name ?? from.Name;
        int at = _offers.FindIndex(o => Same(o.Commit.FileUuid, c.FileUuid));
        var offer = new Offer(c, name, from.EndPoint, from.ViaWebSocket);
        if (at >= 0) _offers[at] = offer;
        else _offers.Add(offer);
        if (_offers.Count > MaxOffers) _offers.RemoveAt(0);
        return true;
    }

    // ---- the client side -----------------------------------------------------------------------------

    /// <summary>One message to a station and its one answer, on a connection of its own.</summary>
    private async Task<Received?> Exchange(IPEndPoint ep, JsonObject message, long maxFile, TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        cts.CancelAfter(timeout);
        using var tcp = new TcpClient();
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
        {
            connect.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(ep, connect.Token);
        }
        var s = tcp.GetStream();
        await WriteAsync(s, message, cts.Token);
        return await ReadAsync(s, cts.Token, maxFile);
    }

    /// <summary>Offers a file to the group: kept to answer REQUESTs, a COMMIT sent to every joined station.</summary>
    public string CommitFile(byte[] file, string fileName, string comment)
    {
        if (file.Length == 0 || file.Length > MaxFile) return $"MVR-xchange: a {file.Length}-byte file cannot be committed.";
        var c = new Commit(NewUuid(), StationUuid, file.Length, fileName, comment);
        Peer[] to;
        lock (_lock)
        {
            _ours.Add((c, file));
            if (_ours.Count > MaxOurs) _ours.RemoveAt(0);
            to = _peers.Values.Where(p => p.Joined && p.EndPoint != null && !p.ViaWebSocket).ToArray();
        }
        var message = c.ToJson();
        foreach (var p in to)
            Task.Run(async () =>
            {
                try
                {
                    var r = await Exchange(p.EndPoint!, message, MaxJson, ReplyTimeout);
                    if (r?.Json is { } m && m.ContainsKey("OK") && !Bool(m, "OK")) Say($"MVR-xchange: {p.Name} refused the commit: {Str(m, "Message")}");
                }
                catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or InvalidDataException)
                {
                    Say($"MVR-xchange: {p.Name} did not take the commit: {Reason(e)}");
                }
            });
        if (_ws is { State: WebSocketState.Open } ws) Task.Run(() => WsSend(ws, message));
        int n = to.Length + (WebSocketOpen ? 1 : 0);
        return $"MVR-xchange: committed {fileName} ({file.Length / 1024} kB) to {n} station(s){(WebSocketOpen ? " and the WebSocket server" : "")}.";
    }

    /// <summary>Asks the station offering it for a file; it is saved, then announced in <see cref="Events"/>.</summary>
    public void Pull(Offer o) => Task.Run(() => PullAsync(o));

    private async Task PullAsync(Offer o)
    {
        var c = o.Commit;
        if (c.FileSize > MaxFile) { Say($"MVR-xchange: {c.FileName} is over {MaxFile >> 20} MB."); return; }
        byte[]? file = null;
        string why = "";
        try
        {
            if (o.ViaWebSocket)
                (file, why) = await WsRequest(c);
            else
            {
                IPEndPoint? ep;
                lock (_lock) ep = _peers.GetValueOrDefault(c.StationUuid)?.EndPoint ?? o.From;
                if (ep == null) { Say($"MVR-xchange: no address for {o.StationName}; it has to be heard on mDNS or joined directly."); return; }
                var r = await Exchange(ep, RequestMessage(c.FileUuid, c.StationUuid), MaxFile, FileTimeout);
                if (r?.Type == TypeFile) file = r.Payload;
                else why = r?.Json is { } m ? Str(m, "Message") : "no answer";
            }
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or InvalidDataException or WebSocketException)
        {
            why = Reason(e);
        }
        if (file is not { Length: > 0 }) { Say($"MVR-xchange: {o.StationName} did not send {Name(c)}: {(why.Length > 0 ? why : "an empty file")}"); return; }
        try
        {
            Directory.CreateDirectory(SaveFolder);
            string path = Path.Combine(SaveFolder, FileNameFor(o));
            await File.WriteAllBytesAsync(path, file);
            Events.Enqueue(new Event($"MVR-xchange: {Name(c)} from {o.StationName} ({file.Length / 1024} kB) saved to {path}.", path, o));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say($"MVR-xchange: could not save {Name(c)}: {e.Message}"); }
    }

    private static string Name(Commit c) => c.FileName.Length > 0 ? c.FileName : c.Comment.Length > 0 ? $"\"{c.Comment}\"" : c.FileUuid;

    /// <summary>A safe file name for a pulled file: the commit's own (no folders), else the station's and the UUID's.</summary>
    public static string FileNameFor(Offer o)
    {
        string name = Path.GetFileName(o.Commit.FileName.Replace('\\', '/'));
        if (name.EndsWith(".mvr", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Trim('.', ' ').Length == 0) name = $"{o.StationName} {o.Commit.FileUuid[..Math.Min(8, o.Commit.FileUuid.Length)]}";
        var bad = Path.GetInvalidFileNameChars();
        name = string.Concat(name.Select(ch => bad.Contains(ch) || ch < ' ' ? '_' : ch)).Trim('.', ' ');
        if (name.Length > 100) name = name[..100];
        return name + ".mvr";
    }

    // ---- the server side -----------------------------------------------------------------------------

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException) { return; }
            if (Interlocked.Increment(ref _connections) > MaxConnections)
            {
                Interlocked.Decrement(ref _connections);
                client.Dispose();
                continue;
            }
            _ = Task.Run(() => Serve(client, ct));
        }
    }

    /// <summary>A station's connection: its messages answered in turn until it closes, goes quiet or sends garbage.</summary>
    private async Task Serve(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                var s = client.GetStream();
                while (!ct.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(IdleTimeout);
                    var m = await ReadAsync(s, idle.Token, MaxJson);
                    if (m?.Json is not { } json) return;   // closed, a file nobody asked for, or not JSON
                    var (reply, file) = Handle(json, false);
                    idle.CancelAfter(FileTimeout);
                    if (file != null) await WriteAsync(s, TypeFile, file, idle.Token);
                    else if (reply != null) await WriteAsync(s, reply, idle.Token);
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or InvalidDataException or ObjectDisposedException) { }
        finally { Interlocked.Decrement(ref _connections); }
    }

    /// <summary>Another station's message: the answer to send (JSON, or a file for a REQUEST we can serve).</summary>
    private (JsonObject? Reply, byte[]? File) Handle(JsonObject m, bool viaWs)
    {
        switch (TypeOf(m))
        {
            case MvrXchange.Join:
            {
                var (peer, added) = Register(m, null, viaWs);
                if (peer != null) Say($"MVR-xchange: {peer.Name} ({peer.Provider}) joined the group: {added} file(s) offered.");
                return (JoinMessage(StationName, StationUuid, Ours(), answer: true), null);
            }
            case MvrXchange.Leave:
            {
                string who = LeaverOf(m);
                lock (_lock)
                    if (_peers.TryGetValue(who, out var p))
                    {
                        _peers[who] = p with { Joined = false };
                        Say($"MVR-xchange: {p.Name} left the group.");
                    }
                return (Ret(LeaveRet, true), null);
            }
            case CommitType:
            {
                if (Commit.From(m) is not { } c) return (Ret(CommitRet, false, "No FileUUID."), null);
                int n = 0;
                string name;
                lock (_lock)
                {
                    var p = _peers.GetValueOrDefault(c.StationUuid) ?? new Peer(c.StationUuid, "?", "", null, true, viaWs);
                    _peers[c.StationUuid] = p;
                    name = p.Name;
                    if (AddOffer(c, p)) n = _offers.FindIndex(o => Same(o.Commit.FileUuid, c.FileUuid)) + 1;
                }
                if (n > 0) Say($"MVR-xchange: {name} committed {Name(c)} ({c.FileSize / 1024} kB){(c.Comment.Length > 0 && c.FileName.Length > 0 ? $": {c.Comment}" : "")}. /concert xchange pull {n}");
                return (Ret(CommitRet, true), null);
            }
            case Request:
            {
                string file = Str(m, "FileUUID");
                (Commit Commit, byte[] File) hit;
                lock (_lock) hit = file.Length == 0 ? _ours.LastOrDefault() : _ours.FirstOrDefault(o => Same(o.Commit.FileUuid, file));
                if (hit.File == null) return (Ret(RequestRet, false, "The MVR is not available on this client"), null);
                Say($"MVR-xchange: sent {hit.Commit.FileName} ({hit.File.Length / 1024} kB).");
                return (null, hit.File);
            }
            case NewSessionHost:
                return (Ret(NewSessionHostRet, false, "UnitSport does not move sessions: join the new one by hand."), null);
            default:
                return (null, null);   // answers, and types we do not know
        }
    }

    // ---- WebSocket mode ------------------------------------------------------------------------------

    /// <summary>Connects to a WebSocket MVR-xchange server and JOINs it; its COMMITs and REQUESTs then come over it.</summary>
    public string ConnectWebSocket(Uri url)
    {
        if (url.Scheme is not ("ws" or "wss")) return "MVR-xchange: a WebSocket URL starts with ws:// or wss://.";
        WebSocketUrl = url;
        Task.Run(() => WsLoop(url, _stop.Token));
        return $"MVR-xchange: connecting to {url}...";
    }

    private async Task WsSend(ClientWebSocket ws, JsonObject message) => await WsSend(ws, Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text);

    private async Task WsSend(ClientWebSocket ws, byte[] data, WebSocketMessageType type)
    {
        await _wsSend.WaitAsync(_stop.Token);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            cts.CancelAfter(FileTimeout);
            await ws.SendAsync(data, type, true, cts.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { _wsSend.Release(); }
    }

    private async Task<(byte[]? File, string Why)> WsRequest(Commit c)
    {
        if (_ws is not { State: WebSocketState.Open } ws) return (null, "the WebSocket is closed");
        // one at a time: the binary answer says nothing of which request it answers
        await _wsPulling.WaitAsync(_stop.Token);
        try
        {
            var wait = new TaskCompletionSource<(byte[]?, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _wsPull = wait;
            await WsSend(ws, RequestMessage(c.FileUuid, c.StationUuid));
            return await wait.Task.WaitAsync(FileTimeout, _stop.Token);
        }
        finally
        {
            _wsPull = null;
            _wsPulling.Release();
        }
    }

    private async Task WsLoop(Uri url, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(ReplyTimeout);
                await ws.ConnectAsync(url, connect.Token);
            }
            _ws = ws;
            await WsSend(ws, JoinMessage(StationName, StationUuid, Ours()));
            var buf = new byte[64 << 10];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var msg = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close) { Say($"MVR-xchange: {url} closed the session."); return; }
                    msg.Write(buf, 0, r.Count);
                    if (msg.Length > (r.MessageType == WebSocketMessageType.Text ? MaxJson : MaxFile)) throw new InvalidDataException("a WebSocket message over the cap");
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Binary)
                {
                    _wsPull?.TrySetResult((msg.ToArray(), ""));
                    continue;
                }
                if (Parse(msg.ToArray()) is not { } m) continue;
                switch (TypeOf(m))
                {
                    case JoinRet:
                        if (m.ContainsKey("OK") && !Bool(m, "OK")) { Say($"MVR-xchange: {url} refused the JOIN: {Str(m, "Message")}"); break; }
                        var (peer, added) = Register(m, null, true);
                        Say($"MVR-xchange: joined {peer?.Name ?? url.ToString()} over WebSocket: {added} file(s) offered.");
                        break;
                    case RequestRet:
                        _wsPull?.TrySetResult((null, Str(m, "Message")));
                        break;
                    case LeaveRet or CommitRet or NewSessionHostRet:
                        break;
                    default:
                        var (reply, file) = Handle(m, true);
                        if (file != null) await WsSend(ws, file, WebSocketMessageType.Binary);
                        else if (reply != null) await WsSend(ws, reply);
                        break;
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or InvalidDataException or IOException)
        {
            if (!ct.IsCancellationRequested) Say($"MVR-xchange: the WebSocket to {url} failed: {Reason(e)}");
        }
        finally
        {
            _wsPull?.TrySetResult((null, "the WebSocket closed"));
            if (_ws == ws) _ws = null;
            ws.Dispose();
        }
    }
}
