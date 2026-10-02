using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Godot;
using UnitSport.Core;

namespace UnitSport.Net;

/// <summary>What a server says about itself in reply to a status query.</summary>
public sealed record ServerStatus(string Name, int Port, int Players, int Max, string Version, string World)
{
    public int Proto { get; init; } = ServerQuery.Proto;

    /// <summary>The game's wire protocol (<see cref="Handshake.Protocol"/>); 0 from a server older than the check.</summary>
    public int Wire { get; init; }
}

/// <summary>
/// The server side of the status query (<c>docs/notes/net/server-query.md</c>): answers
/// <c>USQ1 + nonce</c> on UDP <i>game port + 1</i> with <c>USR1 + nonce + JSON</c>. A query can be
/// broadcast, which is how the Multiplayer screen finds servers on the LAN without any mDNS
/// responder, and unicast, which is how it shows whether a saved server is up, how full, and
/// its ping.
///
/// <para>
/// The socket runs on a background task and replies from a byte snapshot the main thread swaps
/// once a second, so a flood of queries never touches the scene tree. Replies are rate-limited
/// per source and overall, so the port cannot be used to amplify traffic at someone else.
/// </para>
/// </summary>
public partial class QueryResponder : Node
{
    private readonly int _port;
    private readonly IPAddress _bind;
    private readonly Func<ServerStatus> _status;
    private ServerStatus? _last;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private byte[] _snapshot = Array.Empty<byte>();
    private double _sinceSnapshot = 10;
    private readonly ConcurrentDictionary<IPAddress, (long Second, int Count)> _perSource = new();
    private long _globalSecond;
    private int _globalCount;

    private const int PerSourcePerSecond = 10, GlobalPerSecond = 200;

    public QueryResponder(int port, Func<ServerStatus> status, IPAddress? bind = null)
    {
        _port = port;
        _bind = bind ?? IPAddress.Any;
        _status = status;
        Name = "QueryResponder";
    }

    public override void _Ready()
    {
        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.EnableBroadcast = true;
            _udp.Client.Bind(new IPEndPoint(_bind, _port));
        }
        catch (SocketException e)
        {
            GD.PushWarning($"[query] status port {_port} unavailable ({e.Message}); the server will not show up on LAN lists");
            _udp?.Dispose();
            _udp = null;
            return;
        }
        Refresh();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(() => Loop(_udp, token));
        GD.Print($"[query] answering status queries on UDP {_port}");
    }

    public override void _Process(double delta)
    {
        _sinceSnapshot += delta;
        if (_sinceSnapshot >= 1) Refresh();
    }

    private void Refresh()
    {
        _sinceSnapshot = 0;
        // a record of scalars: equal means the same JSON, so only a change is serialised (#221)
        var status = _status();
        if (status == _last) return;
        _last = status;
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(status, ServerQuery.Json);
        Volatile.Write(ref _snapshot, json);
    }

    private Task Loop(UdpClient udp, CancellationToken token) =>
        Udp.ReceiveLoop(udp, token, (p, from) =>
        {
            if (p.Length != 8 || !ServerQuery.Matches(p, ServerQuery.QueryMagic)) return;
            if (!Allow(from.Address)) return;

            byte[] body = Volatile.Read(ref _snapshot);
            var reply = new byte[8 + body.Length];
            Encoding.ASCII.GetBytes(ServerQuery.ReplyMagic, 0, 4, reply, 0);
            Buffer.BlockCopy(p, 4, reply, 4, 4);   // the nonce, echoed
            Buffer.BlockCopy(body, 0, reply, 8, body.Length);
            udp.Send(reply, reply.Length, from);   // a send error (the asker went away) is swallowed by the loop
        });

    private bool Allow(IPAddress from)
    {
        long second = System.Environment.TickCount64 / 1000;
        if (second != _globalSecond) { _globalSecond = second; _globalCount = 0; }
        if (++_globalCount > GlobalPerSecond) return false;
        var (s, n) = _perSource.GetOrAdd(from, (second, 0));
        n = s == second ? n + 1 : 1;
        _perSource[from] = (second, n);
        if (_perSource.Count > 4096) _perSource.Clear();
        return n <= PerSourcePerSecond;
    }

    public override void _ExitTree()
    {
        _cts?.Cancel();
        _udp?.Dispose();
    }

    /// <summary>"--query-port N" (default: game port + 1), or null for "--no-query".</summary>
    public static int? ParsePort(int gamePort)
    {
        var args = CmdArgs.All;
        if (CmdArgs.Has("--no-query")) return null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--query-port" && int.TryParse(args[i + 1], out int p)) return p;
        return gamePort + 1;
    }

    /// <summary>"--query-bind &lt;ip&gt;": answer status queries on that address only (127.0.0.1 hides the server from the LAN).</summary>
    public static IPAddress? ParseBind()
    {
        var args = CmdArgs.All;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--query-bind" && IPAddress.TryParse(args[i + 1], out var ip)) return ip;
        return null;
    }

    /// <summary>"--server-name &lt;name&gt;", else the machine's name.</summary>
    public static string ParseServerName()
    {
        if (CmdArgs.Value("--server-name") is { } name) return name;
        string host = System.Environment.MachineName;
        return host.Length > 0 ? $"{host}" : "UnitSport server";
    }
}
