using Godot;
using UnitSport.Core;

namespace UnitSport.Net;

/// <summary>
/// Joining a server for its chat and commands only, whatever version either side runs (#1088), at
/// <c>World/ConsoleLink</c>. A client of another protocol is refused by <see cref="Handshake"/> before it can type
/// anything, so an admin whose game was newer than the server could not reach it to <c>/update</c> it. This link
/// skips the handshake: the client sends its name with every line, and the first one joins it as a player with no
/// body (<see cref="PlayerInfo.Bodiless"/>), with the rights a join under that name gets (<c>admins.json</c>, or
/// <c>/login</c> after). Its lines run as that player's chat (<see cref="ChatManager.SubmitFromConsole"/>), and
/// everything the chat sends that player comes back through <c>Reply</c>. No world, no player node.
///
/// <para>
/// Godot numbers a node's RPCs by their names, so these two (<c>Command</c>, <c>Reply</c>) must never change,
/// and no RPC may ever be added to this node: they are what two versions of the game still share.
/// </para>
/// </summary>
public partial class ConsoleLink : Node
{
    public const string NodeName = "ConsoleLink";

    private const int MaxLine = 500;

    private bool _server;
    private ChatManager? _chat;
    private Handshake? _handshake;
    private string _serverName = "";

    /// <summary>Client: a line from the server's chat.</summary>
    public event Action<string>? LineReceived;

    public static ConsoleLink CreateServer(ChatManager chat, Handshake handshake, string serverName) =>
        new() { Name = NodeName, _server = true, _chat = chat, _handshake = handshake, _serverName = serverName };

    public static ConsoleLink CreateClient() => new() { Name = NodeName };

    public override void _Ready()
    {
        if (_chat != null) _chat.RemoteLine += Send;
    }

    public override void _ExitTree()
    {
        if (_chat != null) _chat.RemoteLine -= Send;
    }

    /// <summary>Client: <paramref name="line"/> typed as <paramref name="name"/>; an empty line only joins.</summary>
    public void Submit(string name, string line) => RpcId(1, MethodName.Command, name, line);

    private void Send(long peer, string line) => RpcId(peer, MethodName.Reply, line);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Command(string name, string line)
    {
        if (!_server || _chat == null) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (line.Length > MaxLine) line = line[..MaxLine];
        _chat.SubmitFromConsole(peer, name, line, player =>
        {
            _handshake?.Exempt(peer);   // it will never say hello, and is not kicked for it
            string version = ServerUpdater.RunningVersion;
            GD.Print($"[console] {player.Name} joined the server console (peer {peer})");
            Send(peer, $"Joined {_serverName} ({(version.Length > 0 ? "v" + version : "a local build")}, protocol {Handshake.Protocol}, "
                + $"{_chat.PlayerCount} playing) as {player.Name}{(player.IsAdmin ? ", an admin" : "")}. "
                + "Type as in the chat: /help lists the commands, anything else is said to everyone.");
        });
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Reply(string line)
    {
        if (!_server) LineReceived?.Invoke(line);
    }
}
