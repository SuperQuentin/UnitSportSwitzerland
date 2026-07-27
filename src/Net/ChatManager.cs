using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Net;

/// <summary>How a chat line should be shown.</summary>
public enum ChatKind
{
    /// <summary>A player talking.</summary>
    Say = 0,

    /// <summary>Join, leave, teleport — anything the world says about itself.</summary>
    System = 1,

    /// <summary>A server announcement or an admin action.</summary>
    Admin = 2,

    /// <summary>A reply only the requesting player sees.</summary>
    Private = 3,

    /// <summary>A refused command.</summary>
    Error = 4,
}

/// <summary>
/// In-game chat and the command console behind it.
///
/// The node sits at the same path on both sides (<c>World/Chat</c>) because Godot's
/// high-level multiplayer matches RPC targets by node path. Both roles run this same class
/// and branch on <see cref="MultiplayerApi.IsServer"/>: clients only ever submit text and
/// display what comes back, and every decision — who may run a command, what a name is, where
/// somebody gets teleported to — is taken on the server.
///
/// That split matters for the admin commands. A client-side permission check would be a
/// permission check the client can edit.
/// </summary>
public partial class ChatManager : Node
{
    /// <summary>Node name, which must match on server and client for RPC routing.</summary>
    public const string NodeName = "Chat";

    /// <summary>Longest message accepted, to keep one client from flooding the others.</summary>
    private const int MaxMessageLength = 240;

    /// <summary>
    /// Pseudo peer id for the server's own console. Real ENet peer ids are never 0, so this
    /// cannot collide with a client, and it lets the console reuse the whole command path
    /// instead of duplicating it.
    /// </summary>
    public const long ConsolePeerId = 0;

    /// <summary>Server-side only. Null on clients.</summary>
    private PlayerRegistry? _registry;

    private Node3D? _players;
    private WorldOrigin? _origin;
    private PlaceIndex? _places;

    /// <summary>Client-side only: where a forced teleport is applied.</summary>
    public Teleporter? Teleporter { get; set; }

    /// <summary>Raised on the client for every line to display.</summary>
    public event Action<string, ChatKind>? LineReceived;

    /// <summary>Raised on the client when the server closes the connection deliberately.</summary>
    public event Action<string>? Kicked;

    /// <summary>Builds the server half, which owns the registry and answers commands.</summary>
    public static ChatManager CreateServer(
        PlayerRegistry registry, Node3D players, WorldOrigin origin, PlaceIndex? places) => new()
    {
        Name = NodeName,
        _registry = registry,
        _players = players,
        _origin = origin,
        _places = places,
    };

    /// <summary>Builds the client half, which submits text and displays replies.</summary>
    public static ChatManager CreateClient() => new() { Name = NodeName };

    // ---- client -> server ------------------------------------------------------------

    /// <summary>Sends a line of chat, or a command when it starts with '/'.</summary>
    public void Send(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        RpcId(1, MethodName.SubmitLine, text);
    }

    /// <summary>Tells the server what this client would like to be called.</summary>
    public void AnnounceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        RpcId(1, MethodName.SubmitName, name);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitLine(string text)
    {
        if (_registry is null) return;   // only the server acts on this
        long sender = Multiplayer.GetRemoteSenderId();

        // Trim before anything else: length is the one thing a client fully controls.
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > MaxMessageLength) text = text[..MaxMessageLength];

        if (text.StartsWith('/')) HandleCommand(sender, text[1..]);
        else Broadcast($"{NameOf(sender)}: {Scrub(text)}", ChatKind.Say);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitName(string name)
    {
        if (_registry is null) return;
        long sender = Multiplayer.GetRemoteSenderId();

        string assigned = _registry.Rename(sender, name);
        Broadcast($"{assigned} joined", ChatKind.System);
        ReplyTo(sender, "Type /help for commands.", ChatKind.Private);
    }

    // ---- server -> client ------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Deliver(string line, int kind) =>
        LineReceived?.Invoke(line, (ChatKind)kind);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ForceTeleport(double lv95E, double lv95N, string label)
    {
        // Client side: run the same teleport the Tab search uses, so the ground-settling and
        // the walking-versus-flying arrival height are handled identically.
        if (Teleporter is null)
        {
            GD.PushWarning("[chat] teleport ordered but no Teleporter is wired up");
            return;
        }

        Teleporter.TeleportTo(lv95E, lv95N, label);
        LineReceived?.Invoke($"Teleported to {label}", ChatKind.System);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyKicked(string reason) => Kicked?.Invoke(reason);

    /// <summary>Sends a line to everyone. Server only.</summary>
    public void Broadcast(string line, ChatKind kind)
    {
        GD.Print($"[chat] {line}");
        Rpc(MethodName.Deliver, line, (int)kind);
    }

    /// <summary>Sends a line to one peer. Server only.</summary>
    private void ReplyTo(long peerId, string line, ChatKind kind)
    {
        // The console is not a peer; its replies go to the server's own log.
        if (peerId == ConsolePeerId)
        {
            GD.Print($"[console] {line}");
            return;
        }

        RpcId(peerId, MethodName.Deliver, line, (int)kind);
    }

    /// <summary>
    /// Runs a line typed at the dedicated server's own console, with operator rights.
    /// A leading '/' is optional there — everything typed at a server console is a command.
    /// </summary>
    public void RunConsoleCommand(string line)
    {
        if (_registry is null) return;

        line = line.Trim();
        if (line.Length == 0) return;

        if (line.StartsWith('/')) line = line[1..];
        HandleCommand(ConsolePeerId, line);
    }

    // ---- server-side lifecycle --------------------------------------------------------

    /// <summary>Called by <see cref="ServerWorld"/> when a peer drops.</summary>
    public void ReportDisconnect(long peerId)
    {
        if (_registry?.Find(peerId) is not { } player) return;
        _registry.Remove(peerId);
        Broadcast($"{player.Name} left", ChatKind.System);
    }

    // ---- commands ---------------------------------------------------------------------

    private string NameOf(long peerId) => peerId == ConsolePeerId
        ? "Console"
        : _registry?.Find(peerId)?.Name ?? $"Rider{peerId}";

    /// <summary>
    /// The server's own console is always an operator — it is the process that owns the
    /// game, and it is how the first admin gets granted on a fresh server.
    /// </summary>
    private bool IsAdmin(long peerId) =>
        peerId == ConsolePeerId || (_registry?.Find(peerId)?.IsAdmin ?? false);

    /// <summary>
    /// Removes anything that would let one player's message forge another's, or break the
    /// display. Newlines would let a message fake a system line.
    /// </summary>
    private static string Scrub(string text)
    {
        var clean = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
            clean.Append(char.IsControl(c) ? ' ' : c);
        return clean.ToString();
    }

    /// <summary>
    /// Rejects commands that only make sense for someone with a body in the world. The
    /// server console has no avatar, so it cannot teleport itself or be brought anywhere.
    /// </summary>
    private bool RequiresAvatar(long sender, string verb)
    {
        if (sender != ConsolePeerId) return true;

        ReplyTo(sender, $"'/{verb}' needs a player; the console has no avatar.", ChatKind.Error);
        return false;
    }

    private void HandleCommand(long sender, string command)
    {
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        string verb = parts[0].ToLowerInvariant();
        string rest = parts.Length > 1 ? string.Join(' ', parts[1..]) : string.Empty;

        switch (verb)
        {
            case "help": SendHelp(sender); return;
            case "who": SendWho(sender); return;
            case "name": if (RequiresAvatar(sender, verb)) CommandName(sender, rest); return;
            case "city": if (RequiresAvatar(sender, verb)) CommandCity(sender, rest); return;
            case "login": if (RequiresAvatar(sender, verb)) CommandLogin(sender, rest); return;
            case "me":
                if (rest.Length > 0) Broadcast($"* {NameOf(sender)} {Scrub(rest)}", ChatKind.System);
                return;
        }

        // Everything past this point is privileged. One check, in one place.
        if (!IsAdmin(sender))
        {
            ReplyTo(sender, $"'/{verb}' is an admin command.", ChatKind.Error);
            return;
        }

        switch (verb)
        {
            case "say":
                if (rest.Length > 0) Broadcast($"[server] {Scrub(rest)}", ChatKind.Admin);
                return;

            case "admin": CommandAdmin(sender, parts); return;
            case "tp": if (RequiresAvatar(sender, verb)) CommandTeleportToPlayer(sender, rest); return;
            case "bring": if (RequiresAvatar(sender, verb)) CommandBring(sender, rest); return;
            case "tpall": CommandTeleportEveryone(sender, rest); return;
            case "kick": CommandKick(sender, parts); return;

            default:
                ReplyTo(sender, $"Unknown command '/{verb}'. Try /help.", ChatKind.Error);
                return;
        }
    }

    private void SendHelp(long sender)
    {
        ReplyTo(sender, "/help  /who  /name <name>  /city <town>  /me <action>", ChatKind.Private);

        if (_registry?.LoginEnabled == true && !IsAdmin(sender))
            ReplyTo(sender, "/login <password>  — become an operator", ChatKind.Private);

        if (IsAdmin(sender))
            ReplyTo(sender,
                "admin: /say <text>  /tp <player>  /bring <player>  /tpall <town>  "
                + "/kick <player> [reason]  /admin list|add <name>|remove <name>",
                ChatKind.Private);
    }

    private void SendWho(long sender)
    {
        if (_registry is null) return;

        var players = _registry.Players.OrderBy(p => p.Name).ToList();
        ReplyTo(sender, $"{players.Count} online: {string.Join(", ", players)}", ChatKind.Private);
    }

    private void CommandName(long sender, string requested)
    {
        if (_registry is null) return;
        if (requested.Length == 0)
        {
            ReplyTo(sender, "Usage: /name <name>", ChatKind.Error);
            return;
        }

        string previous = NameOf(sender);
        string assigned = _registry.Rename(sender, requested);
        if (assigned == previous) return;

        Broadcast($"{previous} is now {assigned}", ChatKind.System);
    }

    private void CommandLogin(long sender, string password)
    {
        if (_registry is null) return;

        if (!_registry.LoginEnabled)
        {
            ReplyTo(sender, "This server has no admin password set.", ChatKind.Error);
            return;
        }

        if (IsAdmin(sender))
        {
            ReplyTo(sender, "You are already an operator.", ChatKind.Private);
            return;
        }

        if (_registry.TryLogin(sender, password))
        {
            ReplyTo(sender, "You are now an operator.", ChatKind.Admin);
            GD.Print($"[admin] {NameOf(sender)} logged in");
        }
        else
        {
            ReplyTo(sender, "Wrong password.", ChatKind.Error);
            GD.PushWarning($"[admin] failed /login from peer {sender} ({NameOf(sender)})");
        }
    }

    private void CommandAdmin(long sender, string[] parts)
    {
        if (_registry is null) return;

        string action = parts.Length > 1 ? parts[1].ToLowerInvariant() : "list";
        string name = parts.Length > 2 ? parts[2] : string.Empty;

        switch (action)
        {
            case "list":
                ReplyTo(sender,
                    _registry.PersistentAdmins.Count == 0
                        ? "No persisted admins."
                        : "Admins: " + string.Join(", ", _registry.PersistentAdmins),
                    ChatKind.Private);
                return;

            case "add" when name.Length > 0:
                if (_registry.GrantAdmin(name))
                    Broadcast($"{PlayerRegistry.Sanitize(name)} is now an operator", ChatKind.Admin);
                else
                    ReplyTo(sender, $"{name} is already an operator.", ChatKind.Error);
                return;

            case "remove" when name.Length > 0:
                if (_registry.RevokeAdmin(name))
                    Broadcast($"{PlayerRegistry.Sanitize(name)} is no longer an operator", ChatKind.Admin);
                else
                    ReplyTo(sender, $"{name} was not an operator.", ChatKind.Error);
                return;

            default:
                ReplyTo(sender, "Usage: /admin list | add <name> | remove <name>", ChatKind.Error);
                return;
        }
    }

    /// <summary>Resolves a place name against the same index the Tab search uses.</summary>
    private bool TryFindPlace(long sender, string query, out Place place)
    {
        place = null!;

        if (_places is null || _places.Places.Count == 0)
        {
            ReplyTo(sender,
                "This server has no place index. Run the preprocessor with --places.",
                ChatKind.Error);
            return false;
        }

        var matches = _places.Search(query, limit: 1);
        if (matches.Count == 0)
        {
            ReplyTo(sender, $"No town matching '{query}'.", ChatKind.Error);
            return false;
        }

        place = matches[0];
        return true;
    }

    private void CommandCity(long sender, string query)
    {
        if (query.Length == 0)
        {
            ReplyTo(sender, "Usage: /city <town>", ChatKind.Error);
            return;
        }

        if (!TryFindPlace(sender, query, out var place)) return;

        RpcId(sender, MethodName.ForceTeleport, place.E, place.N, place.Name);
    }

    private void CommandTeleportEveryone(long sender, string query)
    {
        if (!TryFindPlace(sender, query, out var place)) return;

        Rpc(MethodName.ForceTeleport, place.E, place.N, place.Name);
        Broadcast($"{NameOf(sender)} moved everyone to {place.Name}", ChatKind.Admin);
    }

    /// <summary>
    /// Where a player currently is, in LV95.
    /// <para>
    /// Transforms are client-authoritative and relayed, so the server's copy is whatever that
    /// client last sent. Good enough to teleport to; it would not be good enough to validate
    /// anything with.
    /// </para>
    /// </summary>
    private bool TryLocate(long sender, string name, out PlayerInfo target, out double e, out double n)
    {
        target = null!;
        e = n = 0;

        if (_registry is null || _origin is null || _players is null) return false;

        if (_registry.FindByName(name) is not { } found)
        {
            ReplyTo(sender, $"No player matching '{name}'.", ChatKind.Error);
            return false;
        }

        target = found;

        if (_players.GetNodeOrNull<Node3D>(found.PeerId.ToString()) is not { } node)
        {
            ReplyTo(sender, $"{found.Name} has no position yet.", ChatKind.Error);
            return false;
        }

        (e, n) = _origin.ToLv95(node.GlobalPosition);
        return true;
    }

    private void CommandTeleportToPlayer(long sender, string name)
    {
        if (name.Length == 0)
        {
            ReplyTo(sender, "Usage: /tp <player>", ChatKind.Error);
            return;
        }

        if (!TryLocate(sender, name, out var target, out double e, out double n)) return;
        if (target.PeerId == sender)
        {
            ReplyTo(sender, "You are already there.", ChatKind.Private);
            return;
        }

        RpcId(sender, MethodName.ForceTeleport, e, n, target.Name);
        ReplyTo(target.PeerId, $"{NameOf(sender)} teleported to you", ChatKind.System);
    }

    private void CommandBring(long sender, string name)
    {
        if (name.Length == 0)
        {
            ReplyTo(sender, "Usage: /bring <player>", ChatKind.Error);
            return;
        }

        if (_registry?.Find(sender) is not { } me) return;
        if (_registry.FindByName(name) is not { } target)
        {
            ReplyTo(sender, $"No player matching '{name}'.", ChatKind.Error);
            return;
        }

        if (!TryLocate(sender, me.Name, out _, out double e, out double n)) return;

        RpcId(target.PeerId, MethodName.ForceTeleport, e, n, me.Name);
        Broadcast($"{me.Name} brought {target.Name} to them", ChatKind.Admin);
    }

    private void CommandKick(long sender, string[] parts)
    {
        if (_registry is null || parts.Length < 2)
        {
            ReplyTo(sender, "Usage: /kick <player> [reason]", ChatKind.Error);
            return;
        }

        if (_registry.FindByName(parts[1]) is not { } target)
        {
            ReplyTo(sender, $"No player matching '{parts[1]}'.", ChatKind.Error);
            return;
        }

        if (target.PeerId == sender)
        {
            ReplyTo(sender, "You cannot kick yourself.", ChatKind.Error);
            return;
        }

        if (target.IsAdmin)
        {
            ReplyTo(sender, $"{target.Name} is an operator; revoke that first.", ChatKind.Error);
            return;
        }

        string reason = parts.Length > 2 ? Scrub(string.Join(' ', parts[2..])) : "no reason given";

        RpcId(target.PeerId, MethodName.NotifyKicked, reason);
        Broadcast($"{target.Name} was kicked by {NameOf(sender)} ({reason})", ChatKind.Admin);

        // Give the notification a moment to reach them before the socket closes under it.
        var peerId = target.PeerId;
        GetTree().CreateTimer(0.2).Timeout += () =>
        {
            if (Multiplayer.MultiplayerPeer is ENetMultiplayerPeer peer)
                peer.DisconnectPeer((int)peerId);
        };
    }
}
