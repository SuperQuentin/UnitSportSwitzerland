using Godot;

namespace UnitSport.Net;

/// <summary>
/// The server's console from a game client, whatever version either runs (#1088), at <c>World/ConsoleLink</c>.
/// A client of another protocol is refused by <see cref="Handshake"/> before it can type anything, so an admin
/// whose game is newer than the server could not reach it to <c>/update</c> it. This link skips the handshake:
/// the client never says hello, sends the admin password with every line, and the server runs the line as its
/// own console (<see cref="ChatManager.RunConsoleCommand"/>, operator rights) and sends back every line its
/// console shows while the link is open. No player, no world.
///
/// <para>
/// Godot numbers a node's RPCs by their names, so these two (<c>Command</c>, <c>Reply</c>) must never change,
/// and no RPC may ever be added to this node: they are what two versions of the game still share. Needs
/// <c>--admin-password</c>; a wrong one disconnects, three in a row lock the address out for a minute.
/// </para>
/// </summary>
public partial class ConsoleLink : Node
{
    public const string NodeName = "ConsoleLink";

    private const int MaxLine = 500;
    private const int FailuresBeforeLock = 3;
    private const double LockSeconds = 60;

    private bool _server;
    private ChatManager? _chat;
    private Handshake? _handshake;
    private PlayerRegistry? _registry;
    private string _serverName = "";
    private readonly HashSet<long> _linked = new();
    private readonly Dictionary<string, (int Fails, double Until)> _failures = new();

    /// <summary>Client: a line from the server's console.</summary>
    public event Action<string>? LineReceived;

    public static ConsoleLink CreateServer(ChatManager chat, Handshake handshake, PlayerRegistry registry, string serverName) =>
        new() { Name = NodeName, _server = true, _chat = chat, _handshake = handshake, _registry = registry, _serverName = serverName };

    public static ConsoleLink CreateClient() => new() { Name = NodeName };

    private static double Now => Core.RealClock.Now;   // lockouts are a person's minute

    public override void _Ready()
    {
        if (_chat != null) _chat.ConsoleOutput += Forward;
    }

    public override void _ExitTree()
    {
        if (_chat != null) _chat.ConsoleOutput -= Forward;
    }

    /// <summary>Server: a peer left, linked or not.</summary>
    public void PeerLeft(long peer) => _linked.Remove(peer);

    /// <summary>Client: runs <paramref name="line"/> at the server's console; an empty line only opens the link.</summary>
    public void Send(string password, string line) => RpcId(1, MethodName.Command, password, line);

    private void Forward(string line)
    {
        foreach (long peer in _linked) RpcId(peer, MethodName.Reply, line);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Command(string password, string line)
    {
        if (!_server || _registry == null || _chat == null) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (!_linked.Contains(peer))
        {
            string address = AddressOf(peer);
            if (_failures.TryGetValue(address, out var f) && f.Until > Now)
            {
                Refuse(peer, "Too many wrong passwords: try again in a minute.");
                return;
            }
            if (!_registry.LoginEnabled)
            {
                Refuse(peer, "This server has no admin password (--admin-password), so its remote console is off.");
                return;
            }
            if (!_registry.PasswordMatches(password))
            {
                int fails = (_failures.TryGetValue(address, out var g) ? g.Fails : 0) + 1;
                _failures[address] = (fails, fails >= FailuresBeforeLock ? Now + LockSeconds : 0);
                GD.Print($"[console] remote console: wrong password from {address} ({fails})");
                Refuse(peer, "Wrong password.");
                return;
            }
            _failures.Remove(address);
            _linked.Add(peer);
            _handshake?.Exempt(peer);   // it will never say hello, and is not kicked for it
            GD.Print($"[console] remote console opened from {address} (peer {peer})");
            string version = ServerUpdater.RunningVersion;
            RpcId(peer, MethodName.Reply, $"Connected to {_serverName} ({(version.Length > 0 ? "v" + version : "a local build")}, "
                + $"protocol {Handshake.Protocol}, {_chat.PlayerCount} playing). Commands run as the server console: help lists them.");
        }

        line = line.Trim();
        if (line.Length == 0) return;
        if (line.Length > MaxLine) line = line[..MaxLine];
        GD.Print($"[console] remote: {line}");
        _chat.RunConsoleCommand(line);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Reply(string line)
    {
        if (!_server) LineReceived?.Invoke(line);
    }

    /// <summary>Server: tells a peer why, then disconnects it.</summary>
    private void Refuse(long peer, string why)
    {
        RpcId(peer, MethodName.Reply, why);
        var tree = GetTree();
        tree.CreateTimer(0.3).Timeout += () =>
        {
            if (tree.Root.Multiplayer.MultiplayerPeer is ENetMultiplayerPeer enet) enet.DisconnectPeer((int)peer);
        };
    }

    private string AddressOf(long peer) =>
        Multiplayer.MultiplayerPeer is ENetMultiplayerPeer enet && enet.GetPeer((int)peer) is { } p ? p.GetRemoteAddress() : $"peer {peer}";
}
