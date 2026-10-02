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

    /// <summary>Client-side only: where <c>/spawn</c> puts items, and the towns <c>/city</c> offline looks in.</summary>
    public Items.Inventory? Inventory { get; set; }

    /// <summary>Adds a stack, dropping what does not fit on the ground; returns how many went nowhere. Falls back to a plain add.</summary>
    public Func<Items.ItemStack, int>? GiveOrDrop { get; set; }
    public PlaceSearchUi? PlaceSearch { get; set; }

    /// <summary>
    /// Client-side: nobody to ask — no server connection, so commands run here, on this machine,
    /// with the rights of someone who owns the world. A server's own node never is.
    /// </summary>
    private bool IsLocal => _registry is null && !Permissions.Online;

    /// <summary>Client-side: player names as last sent by the server, for completion.</summary>
    public IReadOnlyList<string> PlayerNames => _playerNames;

    private string[] _playerNames = [];

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

    public override void _Ready()
    {
        if (_registry != null) PlayerInfo.AdminChanged += OnAdminChanged;
    }

    public override void _ExitTree()
    {
        PlayerInfo.AdminChanged -= OnAdminChanged;
        if (_registry == null) Permissions.Reset();
    }

    /// <summary>Server: tells a peer it gained or lost operator rights, so its menus can follow.</summary>
    private void OnAdminChanged(PlayerInfo player)
    {
        if (player.PeerId == ConsolePeerId || _registry?.Find(player.PeerId) == null) return;
        RpcId(player.PeerId, MethodName.AdminStatus, player.IsAdmin);
    }

    /// <summary>
    /// Client: the server's word on whether this player is an operator. Only ever shapes what the
    /// menus offer; every privileged action is still decided on the server.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AdminStatus(bool isAdmin)
    {
        Permissions.SetAdmin(isAdmin);
        LineReceived?.Invoke(isAdmin
            ? "You are an admin: vehicles can be spawned from the travel menu."
            : "You are no longer an admin.", ChatKind.System);
    }

    /// <summary>Server: a peer's name was set or changed — the key of its bank account.</summary>
    public event Action<long>? NameAssigned;

    /// <summary>Server: operator rights for the other server-side systems (vehicle spawning).</summary>
    public bool IsAdminPeer(long peerId) => IsAdmin(peerId);

    // ---- client -> server ------------------------------------------------------------

    /// <summary>Sends a line of chat, or a command when it starts with '/'.</summary>
    public void Send(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (LocalClock(text.Trim()) is { } clock)
        {
            LineReceived?.Invoke(clock, ChatKind.Private);
            return;
        }
        // the visual style is this screen's alone: never a server's business
        if (Styles.StyleCommand.Run(text.Trim()) is { } style)
        {
            LineReceived?.Invoke(style, ChatKind.Private);
            return;
        }
        // the catalogue is a panel on this screen; what it gives still goes through /spawn
        if (text.Trim().ToLowerInvariant() is "/catalogue" or "/catalog" or "/items")
        {
            if (!CanUseCatalogue) LineReceived?.Invoke("The item catalogue is for admins on a server.", ChatKind.Error);
            else if (CatalogueRequested is null) LineReceived?.Invoke("No catalogue here.", ChatKind.Error);
            else CatalogueRequested();
            return;
        }
        if (IsLocal)
        {
            text = text.Trim();
            HandleLocal(text.Length > MaxMessageLength ? text[..MaxMessageLength] : text);
            return;
        }

        RpcId(1, MethodName.SubmitLine, text);
    }

    /// <summary>
    /// Asks the server who is online, so names can be completed. The answer lands in
    /// <see cref="PlayerNames"/>. Completion asks on every keystroke, so this sends at most once a second.
    /// </summary>
    public void RequestPlayerNames()
    {
        if (IsLocal) return;
        ulong now = Time.GetTicksMsec();
        if (_namesAskedAt != 0 && now - _namesAskedAt < 1000) return;
        _namesAskedAt = now;
        RpcId(1, MethodName.SubmitNamesRequest);
    }

    private ulong _namesAskedAt;

    /// <summary>Client: the server's list of names changed; completions can be refreshed.</summary>
    public event Action? NamesReceived;

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitNamesRequest()
    {
        if (_registry is null) return;
        RpcId(Multiplayer.GetRemoteSenderId(), MethodName.ReceiveNames,
            _registry.Players.Select(p => p.Name).ToArray());
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveNames(string[] names)
    {
        bool changed = !names.SequenceEqual(_playerNames);
        _playerNames = names;
        if (changed) NamesReceived?.Invoke();
    }

    /// <summary>Client: the server granted items (<c>/spawn</c>, an admin's choice); they go in this machine's inventory.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void GrantItem(int item, int count)
    {
        if (!Enum.IsDefined((Items.ItemId)item) || Items.ItemDefs.Get((Items.ItemId)item) is not { } def) return;
        count = Math.Clamp(count, 1, Items.ItemLookup.MaxSpawnFrancs);
        LineReceived?.Invoke(Give(def, count), ChatKind.Admin);
    }

    /// <summary>Client: <c>/catalogue</c> was typed by someone allowed it; the item catalogue opens.</summary>
    public event Action? CatalogueRequested;

    /// <summary>Client: the catalogue is offered alone, or to an admin. The server re-checks every <c>/spawn</c> it sends.</summary>
    public bool CanUseCatalogue => IsLocal || Permissions.IsAdmin;

    /// <summary>Client: an admin emptied this inventory (<c>/clear</c>).</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClearInventory(string by) => LineReceived?.Invoke(Clear(by.Length == 0 ? null : by), ChatKind.Admin);

    /// <summary>Client: an admin put cash in this pocket, or took it (<c>/money</c>).</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void GrantCash(int amount) =>
        LineReceived?.Invoke(Cash(Math.Clamp(amount, -MaxCashGrant, MaxCashGrant)), ChatKind.Admin);

    /// <summary>Most one <c>/money</c> moves, either way.</summary>
    public const int MaxCashGrant = 1_000_000;

    private string Clear(string? by)
    {
        if (Inventory is null) return "No inventory to clear.";
        Inventory.Clear();
        return by is null ? "Inventory cleared." : $"{by} cleared your inventory.";
    }

    private string Cash(int amount)
    {
        if (Inventory is null) return "No pocket to put that in.";
        if (amount >= 0) Inventory.Add(Items.ItemId.Francs, amount);
        else Inventory.TakeCash(Math.Min(-amount, Inventory.Cash));
        return $"{(amount >= 0 ? "+" : "-")}{Math.Abs(amount)} CHF cash, {Inventory.Cash} CHF in your pocket.";
    }

    /// <summary>Puts items in the inventory and says what happened.</summary>
    private string Give(Items.ItemDef def, int count)
    {
        if (Inventory is null) return "No inventory to put that in.";

        int room = Math.Min(count, Inventory.Room(def.Id));
        int left = GiveOrDrop?.Invoke(new Items.ItemStack(def.Id, count)) ?? Inventory.Add(def.Id, count);
        return left == 0
            ? room < count ? $"Spawned {count} x {def.Name}; {count - room} dropped at your feet." : $"Spawned {count} x {def.Name}."
            : left == count
                ? $"No room for {def.Name}."
                : $"Spawned {count - left} x {def.Name}; {left} did not fit.";
    }

    /// <summary>
    /// A line typed with no server to send it to: commands run here. Only what makes sense alone
    /// works; the rest says it needs a server rather than silently doing nothing.
    /// </summary>
    private void HandleLocal(string text)
    {
        void Show(string line, ChatKind kind) => LineReceived?.Invoke(line, kind);

        if (!text.StartsWith('/'))
        {
            Show($"You: {Scrub(text)}", ChatKind.Say);
            return;
        }

        string[] parts = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        string verb = parts[0].ToLowerInvariant();
        string rest = string.Join(' ', parts[1..]);

        switch (verb)
        {
            case "help":
                Show("/help  /who  /me <action>  /city <town>  /spawn <item> [count]  /catalogue  /clear  /money <amount>  "
                    + "/bank [set|add|take <amount>]  /occasion  /time  /style  — Tab completes.", ChatKind.Private);
                Show("Offline: the server commands (/race, /tp, /kick ...) need a multiplayer game.", ChatKind.Private);
                return;

            case "who":
                Show("1 online: you", ChatKind.Private);
                return;

            case "me":
                if (rest.Length > 0) Show($"* {Scrub(rest)}", ChatKind.System);
                return;

            case "city":
                if (rest.Length == 0) Show("Usage: /city <town>", ChatKind.Error);
                else if (PlaceSearch is null || Teleporter is null) Show("No place index here.", ChatKind.Error);
                else if (PlaceSearch.Search(rest, 1) is not [var place]) Show($"No town matching '{rest}'.", ChatKind.Error);
                else
                {
                    Teleporter.TeleportTo(place.E, place.N, place.Name);
                    Show($"Teleported to {place.Name}", ChatKind.System);
                }
                return;

            case "spawn":
                if (Items.ItemLookup.TryParse(rest, out var def, out int count, out string error)) Show(Give(def, count), ChatKind.Admin);
                else Show(error, ChatKind.Error);
                return;

            case "time":
                Show(LocalTime(parts[1..], out bool failed), failed ? ChatKind.Error : ChatKind.Admin);
                return;

            case "give":
                Show("Offline you are the only player: /spawn <item> [count], or /catalogue.", ChatKind.Error);
                return;

            case "clear":
                Show(Clear(null), ChatKind.Admin);
                return;

            case "money":
                if (parts.Length == 2 && AdminArgs.TryAmount(parts[1], out long cash))
                    Show(Cash((int)Math.Clamp(cash, -MaxCashGrant, MaxCashGrant)), ChatKind.Admin);
                else Show("Usage: /money <amount>  (negative takes)", ChatKind.Error);
                return;

            case "bank":
                if (Items.Bank.Instance is not { } bank) Show("No bank here.", ChatKind.Error);
                else if (!AdminArgs.TryBank(parts[1..], out _, out long? set, out long add, out string usage)) Show(usage, ChatKind.Error);
                else Show($"Account: {bank.AdminAdjust(null, set, add)} CHF", ChatKind.Admin);
                return;

            case "occasion" or "occasions":
                if (Occasions.OccasionManager.Instance is not { } occasions) Show("Occasions are not running.", ChatKind.Error);
                else
                    foreach (string line in occasions.RunCommand(parts[1..], isAdmin: true))
                        Show(line, ChatKind.Private);
                return;

            case "name" or "login" or "stream" or "race" or "say" or "admin" or "tp" or "bring" or "tpall" or "kick" or "pvp" or "br":
                Show($"'/{verb}' needs a multiplayer game.", ChatKind.Error);
                return;

            default:
                Show($"Unknown command '/{verb}'. Try /help.", ChatKind.Error);
                return;
        }
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
        NameAssigned?.Invoke(sender);
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

    /// <summary>A player's display name, for the other server-side systems (races).</summary>
    public string NameOfPeer(long peerId) => NameOf(peerId);

    /// <summary>Races (<c>/race</c>), wired by ServerWorld.</summary>
    public World.RaceManager? Race { get; set; }

    /// <summary>Server: the Battle Royale mode, for /br (#177).</summary>
    public BattleRoyale.BrManager? BattleRoyale { get; set; }

    /// <summary>For races: the place index (air courses to a town), a player by name, a private line.</summary>
    public PlaceIndex? Places => _places;
    public long PeerByName(string name) => _registry?.FindByName(name)?.PeerId ?? -1;
    public void Tell(long peerId, string line, ChatKind kind) => ReplyTo(peerId, line, kind);

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
            case "stream": ReportStreaming(sender); return;
            case "who": SendWho(sender); return;
            case "name": if (RequiresAvatar(sender, verb)) CommandName(sender, rest); return;
            case "city": if (RequiresAvatar(sender, verb)) CommandCity(sender, rest); return;
            case "login": if (RequiresAvatar(sender, verb)) CommandLogin(sender, rest); return;
            case "me":
                if (rest.Length > 0) Broadcast($"* {NameOf(sender)} {Scrub(rest)}", ChatKind.System);
                return;
            // anyone may ask; set/add/speed are checked inside, against the same IsAdmin
            case "time": CommandTime(sender, parts[1..]); return;
            // anyone may list; start/stop/auto are checked inside, against the same IsAdmin
            case "occasion" or "occasions":
                if (Occasions.OccasionManager.Instance is not { } occasions)
                    ReplyTo(sender, "Occasions are not running on this server.", ChatKind.Error);
                else
                    foreach (string line in occasions.RunCommand(parts[1..], IsAdmin(sender)))
                        ReplyTo(sender, line, ChatKind.Private);
                return;
            // how standing passengers feel a vehicle move (#162): anyone may ask, an admin may change it
            case "inertia" when parts.Length == 1:
                ReplyTo(sender, $"Standing passengers: {Vehicles.PassengerService.Inertia.ToString().ToLowerInvariant()} (steady, sway or full).", ChatKind.Private);
                return;
            // your own inventory is yours to empty; someone else's is an admin's (checked inside)
            case "clear": CommandClear(sender, rest); return;
            case "br":
                if (BattleRoyale == null) ReplyTo(sender, "Battle Royale is not available on this server.", ChatKind.Error);
                else ReplyTo(sender, BattleRoyale.Command(sender, rest, IsAdmin(sender)), ChatKind.Private);
                return;
            case "race":
                if (Race == null) ReplyTo(sender, "Races are not available on this server.", ChatKind.Error);
                else if (sender == ConsolePeerId && !rest.StartsWith("cancel") && !rest.StartsWith("list")) ReplyTo(sender, "'/race' needs a player.", ChatKind.Error);
                else ReplyTo(sender, Race.Command(sender, rest), ChatKind.Private);
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

            case "inertia":
                if (!Vehicles.PassengerService.TryParseInertia(rest, out var inertia) || Vehicles.PassengerService.Instance is not { } passengers)
                    ReplyTo(sender, "Usage: /inertia steady|sway|full", ChatKind.Error);
                else
                {
                    passengers.SetInertia(inertia);
                    Broadcast($"[server] Standing passengers now feel the vehicles: {rest.ToLowerInvariant()}.", ChatKind.Admin);
                }
                return;

            case "admin": CommandAdmin(sender, parts); return;
            case "tp": if (RequiresAvatar(sender, verb)) CommandTeleportToPlayer(sender, rest); return;
            case "bring": if (RequiresAvatar(sender, verb)) CommandBring(sender, rest); return;
            case "tpall": CommandTeleportEveryone(sender, rest); return;
            case "kick": CommandKick(sender, parts); return;
            case "spawn": if (RequiresAvatar(sender, verb)) CommandSpawn(sender, rest); return;
            case "pvp": CommandPvp(sender, rest); return;
            case "give": CommandGive(sender, parts); return;
            case "money": CommandMoney(sender, parts); return;
            case "bank": CommandBank(sender, parts); return;

            default:
                ReplyTo(sender, $"Unknown command '/{verb}'. Try /help.", ChatKind.Error);
                return;
        }
    }

    /// <summary>/pvp on|off: whether foot weapons hurt players (#178). Bare /pvp says which.</summary>
    private void CommandPvp(long sender, string rest)
    {
        switch (rest.Trim().ToLowerInvariant())
        {
            case "":
                ReplyTo(sender, $"PvP is {(Combat.PvpRules.Enabled ? "on" : "off")}.", ChatKind.Private);
                return;
            case "on" or "off":
                Combat.PvpRules.Enabled = rest.Trim().ToLowerInvariant() == "on";
                Broadcast(Combat.PvpRules.Enabled
                    ? "PvP is ON: weapons hurt players."
                    : "PvP is off: weapons no longer hurt players.", ChatKind.System);
                return;
            default:
                ReplyTo(sender, "Usage: /pvp on|off", ChatKind.Error);
                return;
        }
    }

    private void SendHelp(long sender)
    {
        ReplyTo(sender, "/help  /who  /name <name>  /city <town>  /me <action>  /stream  /race start|duel|join|leave|list|npc  /br join|leave|status  /occasion  /time  /clear", ChatKind.Private);

        if (_registry?.LoginEnabled == true && !IsAdmin(sender))
            ReplyTo(sender, "/login <password>  — become an operator", ChatKind.Private);

        if (IsAdmin(sender))
            ReplyTo(sender,
                "admin: /say <text>  /tp <player>  /bring <player>  /tpall <town>  "
                + "/kick <player> [reason]  /admin list|add <name>|remove <name>  "
                + "/occasion start|stop <id>|auto  /spawn <item> [count]  /catalogue  "
                + "/give <player> <item> [count]  /clear [player]  /money <amount> [player]  "
                + "/bank <player> [set|add|take <amount>]  "
                + "/time set <hh:mm|noon|night...>|add <h>|speed <min>  — Tab completes",
                ChatKind.Private);
    }

    /// <summary>Client-side streaming stats, injected by ClientWorld.</summary>
    public Func<string>? StreamStatus { get; set; }

    /// <summary>
    /// Reports what terrain streaming is doing. Exists because the failure mode is silence —
    /// a client that cannot stream looks exactly like a client standing somewhere empty.
    /// </summary>
    private void ReportStreaming(long sender)
    {
        // The server answers with what it is serving; the client prints its own side locally,
        // since only it knows its cache and its tile count.
        int queued = 0;
        if (_streamer is not null) queued = _streamer.QueuedTransfers(sender);

        ReplyTo(sender,
            $"server: {(_streamer is null ? "streaming disabled" : $"{queued} transfer(s) queued for you")}",
            ChatKind.Private);
    }

    /// <summary>Set on the server so /stream can report queue depth.</summary>
    public ChunkStreamer? Streamer { get => _streamer; set => _streamer = value; }

    private ChunkStreamer? _streamer;

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
        NameAssigned?.Invoke(sender);

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
        if (BattleRoyale?.Playing(sender) == true)
        {
            ReplyTo(sender, "No teleporting out of a Battle Royale. /br leave to give up.", ChatKind.Error);
            return;
        }
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

    // ---- /time --------------------------------------------------------------------------

    /// <summary>
    /// Server: the world's clock once an admin has set it — the hour at <c>At</c> (this process's
    /// seconds) and how fast it runs. Null until then, and each client keeps its own clock.
    /// </summary>
    private (double Hour, double At, float MinutesPerDay)? _worldClock;

    private static double Now => Time.GetTicksMsec() / 1000.0;

    private static double WorldHour((double Hour, double At, float MinutesPerDay) c) =>
        World.TimeCommand.Advance(c.Hour, Now - c.At, c.MinutesPerDay);

    /// <summary>
    /// Client: <c>/time</c> or <c>/time query</c> is answered from the clock on this screen, which
    /// is the one the player is asking about. Null when it is another command, or there is no clock.
    /// </summary>
    private static string? LocalClock(string text)
    {
        if (!text.StartsWith('/') || World.DayNight.Instance is not { } clock) return null;
        string[] parts = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("time", StringComparison.OrdinalIgnoreCase)) return null;
        if (!World.TimeCommand.TryParse(parts[1..], out var op, out _, out _) || op != World.TimeOp.Query) return null;
        return $"It is {clock.Clock} ({World.TimeCommand.DescribeSpeed(clock.MinutesPerDay)}).";
    }

    /// <summary>Offline <c>/time</c>: this machine's clock, with operator rights. The reply, and whether it failed.</summary>
    private static string LocalTime(string[] args, out bool failed)
    {
        failed = true;
        if (World.DayNight.Instance is not { } clock) return "There is no clock here.";
        if (!World.TimeCommand.TryParse(args, out var op, out double value, out string error)) return error;

        failed = false;
        switch (op)
        {
            case World.TimeOp.Set: clock.Hour = value; break;
            case World.TimeOp.Add: clock.Hour = World.TimeCommand.Wrap(clock.Hour + value); break;
            case World.TimeOp.Speed: clock.DayLengthOverride = (float)value; break;
        }
        return op == World.TimeOp.Speed
            ? $"The clock runs at {World.TimeCommand.DescribeSpeed(clock.MinutesPerDay)}."
            : $"Time set to {clock.Clock}.";
    }

    /// <summary>
    /// Server <c>/time</c>. Changing it is an admin's, and it changes it for everyone: the server
    /// keeps the clock from then on and sends it to each client, and to whoever joins later
    /// (<see cref="SendWorldTimeTo"/>). The first change starts from the server's own day length.
    /// </summary>
    private void CommandTime(long sender, string[] args)
    {
        if (!World.TimeCommand.TryParse(args, out var op, out double value, out string error))
        {
            ReplyTo(sender, error, ChatKind.Error);
            return;
        }

        if (op == World.TimeOp.Query)
        {
            // a player's own client answers this; this is the console's, or a client with no clock
            ReplyTo(sender, _worldClock is { } c
                ? $"It is {World.TimeCommand.Format(WorldHour(c))} ({World.TimeCommand.DescribeSpeed(c.MinutesPerDay)})."
                : "Nobody has set the time on this server: each player keeps their own clock.",
                ChatKind.Private);
            return;
        }

        if (!IsAdmin(sender))
        {
            ReplyTo(sender, "Changing the time is an admin command.", ChatKind.Error);
            return;
        }

        if (_worldClock is null && op != World.TimeOp.Set)
        {
            ReplyTo(sender, "Nobody has set the time on this server yet: /time set <hour> first.", ChatKind.Error);
            return;
        }

        double hour = _worldClock is { } now ? WorldHour(now) : 0;
        float speed = _worldClock?.MinutesPerDay ?? GameSettings.Current.DayLengthMinutes;
        switch (op)
        {
            case World.TimeOp.Set: hour = value; break;
            case World.TimeOp.Add: hour = World.TimeCommand.Wrap(hour + value); break;
            case World.TimeOp.Speed: speed = (float)value; break;
        }
        _worldClock = (hour, Now, speed);
        Rpc(MethodName.WorldTime, hour, speed);
        GD.Print($"[admin] {NameOf(sender)} set the clock to {World.TimeCommand.Format(hour)}, {World.TimeCommand.DescribeSpeed(speed)}");

        string who = NameOf(sender);
        Broadcast(op != World.TimeOp.Speed ? $"{who} set the time to {World.TimeCommand.Format(hour)}"
            : speed > 0 ? $"{who} set the day to {World.TimeCommand.DescribeSpeed(speed)}"
            : $"{who} stopped the clock at {World.TimeCommand.Format(hour)}", ChatKind.Admin);
    }

    /// <summary>Server: tells a newly connected peer the world's time, if an admin has set one.</summary>
    public void SendWorldTimeTo(long peerId)
    {
        if (_worldClock is { } c) RpcId(peerId, MethodName.WorldTime, WorldHour(c), c.MinutesPerDay);
    }

    /// <summary>
    /// Client: the world's time, now. Sent as the current hour rather than a timestamp: a day of
    /// 24 minutes moves a game minute a second, so the trip over the wire does not show.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void WorldTime(double hour, float minutesPerDay)
    {
        if (World.DayNight.Instance is not { } clock) return;
        clock.Hour = World.TimeCommand.Wrap(hour);
        clock.DayLengthOverride = Math.Clamp(minutesPerDay, 0f, World.TimeCommand.MaxMinutesPerDay);
        GD.Print($"[time] the server's clock: {clock.Clock}, {World.TimeCommand.DescribeSpeed(clock.MinutesPerDay)}");
    }

    /// <summary>
    /// <c>/spawn &lt;item&gt; [count]</c>: the server decides the sender may and what they get; the item
    /// lands in that client's own inventory, which is local to it (see <see cref="Items.Inventory"/>).
    /// </summary>
    private void CommandSpawn(long sender, string rest)
    {
        if (!Items.ItemLookup.TryParse(rest, out var def, out int count, out string error))
        {
            ReplyTo(sender, error, ChatKind.Error);
            return;
        }

        RpcId(sender, MethodName.GrantItem, (int)def.Id, count);
        GD.Print($"[admin] {NameOf(sender)} spawned {count} x {def.Name}");
    }

    /// <summary>A player named in an admin command; "me" is the sender. Says why when there is none.</summary>
    private PlayerInfo? Target(long sender, string name)
    {
        if (name.Equals("me", StringComparison.OrdinalIgnoreCase) && sender != ConsolePeerId
            && _registry?.Find(sender) is { } self) return self;
        if (_registry?.FindByName(name) is { } found) return found;
        ReplyTo(sender, $"No player matching '{name}'.", ChatKind.Error);
        return null;
    }

    /// <summary><c>/give &lt;player&gt; &lt;item&gt; [count]</c>: <c>/spawn</c> into someone else's inventory.</summary>
    private void CommandGive(long sender, string[] parts)
    {
        if (parts.Length < 3)
        {
            ReplyTo(sender, "Usage: /give <player> <item> [count]", ChatKind.Error);
            return;
        }
        if (Target(sender, parts[1]) is not { } target) return;
        if (!Items.ItemLookup.TryParse(string.Join(' ', parts[2..]), out var def, out int count, out string error))
        {
            ReplyTo(sender, error, ChatKind.Error);
            return;
        }

        RpcId(target.PeerId, MethodName.GrantItem, (int)def.Id, count);
        if (target.PeerId != sender)
        {
            ReplyTo(target.PeerId, $"{NameOf(sender)} gave you {count} x {def.Name}.", ChatKind.Admin);
            ReplyTo(sender, $"Gave {target.Name} {count} x {def.Name}.", ChatKind.Admin);
        }
        GD.Print($"[admin] {NameOf(sender)} gave {target.Name} {count} x {def.Name}");
    }

    /// <summary><c>/clear [player]</c>: empties an inventory, cash aside. Anyone may clear their own.</summary>
    private void CommandClear(long sender, string name)
    {
        long peer = sender;
        if (name.Length > 0 && !name.Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsAdmin(sender))
            {
                ReplyTo(sender, "Clearing someone else's inventory is an admin command.", ChatKind.Error);
                return;
            }
            if (Target(sender, name) is not { } target) return;
            peer = target.PeerId;
        }
        else if (sender == ConsolePeerId)
        {
            ReplyTo(sender, "Usage: /clear <player>", ChatKind.Error);
            return;
        }

        RpcId(peer, MethodName.ClearInventory, peer == sender ? "" : NameOf(sender));
        if (peer != sender) ReplyTo(sender, $"Cleared {NameOf(peer)}'s inventory.", ChatKind.Admin);
        GD.Print($"[admin] {NameOf(sender)} cleared {NameOf(peer)}'s inventory");
    }

    /// <summary><c>/money &lt;amount&gt; [player]</c>: cash into a pocket, or out of it when negative.</summary>
    private void CommandMoney(long sender, string[] parts)
    {
        if (parts.Length is < 2 or > 3 || !AdminArgs.TryAmount(parts[1], out long amount) || amount == 0)
        {
            ReplyTo(sender, "Usage: /money <amount> [player]  (negative takes, 2k = 2000)", ChatKind.Error);
            return;
        }
        if (parts.Length == 2 && sender == ConsolePeerId)
        {
            ReplyTo(sender, "Usage: /money <amount> <player>", ChatKind.Error);
            return;
        }
        long peer = sender;
        if (parts.Length == 3)
        {
            if (Target(sender, parts[2]) is not { } target) return;
            peer = target.PeerId;
        }

        int cash = (int)Math.Clamp(amount, -MaxCashGrant, MaxCashGrant);
        RpcId(peer, MethodName.GrantCash, cash);
        if (peer != sender) ReplyTo(sender, $"{(cash > 0 ? "Gave" : "Took")} {NameOf(peer)} {Math.Abs(cash)} CHF cash.", ChatKind.Admin);
        GD.Print($"[admin] {NameOf(sender)} moved {cash} CHF cash to {NameOf(peer)}");
    }

    /// <summary>
    /// <c>/bank [player] [set|add|take &lt;amount&gt;]</c>: reads or edits a server-kept account. The
    /// player need not be online: accounts are keyed by name.
    /// </summary>
    private void CommandBank(long sender, string[] parts)
    {
        if (Items.Bank.Instance is not { } bank)
        {
            ReplyTo(sender, "This server has no bank.", ChatKind.Error);
            return;
        }
        if (!AdminArgs.TryBank(parts[1..], out string? who, out long? set, out long add, out string usage))
        {
            ReplyTo(sender, usage, ChatKind.Error);
            return;
        }
        if (who is null && sender == ConsolePeerId)
        {
            ReplyTo(sender, "Usage: /bank <player> [set|add|take <amount>]", ChatKind.Error);
            return;
        }

        // an online player by prefix, else the name as written: the account of someone not here
        var online = who is null ? _registry?.Find(sender)
            : who.Equals("me", StringComparison.OrdinalIgnoreCase) ? _registry?.Find(sender) : _registry?.FindByName(who);
        string account = online?.Name ?? PlayerRegistry.Sanitize(who ?? "");
        if (account.Length == 0)
        {
            ReplyTo(sender, usage, ChatKind.Error);
            return;
        }

        long balance = bank.AdminAdjust(account, set, add);
        if (online != null) bank.SendBalance(online.PeerId);
        ReplyTo(sender, $"{account}'s account: {balance} CHF{(online == null ? " (not online)" : "")}", ChatKind.Admin);
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
