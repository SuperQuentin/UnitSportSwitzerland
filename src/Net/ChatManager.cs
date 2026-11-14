using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Stadiums;

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

    /// <summary>A private message from one player to another.</summary>
    Whisper = 5,
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

    /// <summary>Longest message accepted, to keep one client from flooding the others. Shared with <see cref="ChatUi"/>.</summary>
    public const int MaxMessageLength = 240;

    /// <summary>
    /// How many lines one player may send at once, and how fast that refills. The one thing a client
    /// fully controls is how often it talks, so this is checked before anything else is parsed.
    /// </summary>
    private const double FloodBurst = 5, FloodPerSecond = 0.6, FloodWarnSeconds = 6;

    /// <summary>How many recent lines a report carries, and how often one player may report.</summary>
    private const int RecentLines = 80, ReportCooldownSeconds = 30;

    private sealed class FloodGate
    {
        public double Tokens = FloodBurst;
        public double Last;
        public double WarnAt;
        public double QuietUntil;
        public int Strikes;
    }

    /// <summary>
    /// A test run's server lets every line through (#1857): the net probes speak their steps in the chat, faster
    /// than the gate's refill, and it dropped them without a word. <c>--chat-flood</c> keeps it on.
    /// </summary>
    private static bool FloodGateOff => GameSettings.TestRun && !CmdArgs.Has("--chat-flood");

    /// <summary>Server only: one bucket per peer, dropped when it leaves.</summary>
    private readonly Dictionary<long, FloodGate> _flood = new();

    /// <summary>Server only: the last lines, for <c>/report</c> to attach context to.</summary>
    private readonly List<(string At, string Line)> _recent = new();

    /// <summary>Server: keeps a line for a report's context, stamped with the wall clock.</summary>
    private void Remember(string line)
    {
        _recent.Add((System.DateTime.Now.ToString("HH:mm:ss"), line));
        if (_recent.Count > RecentLines) _recent.RemoveAt(0);
    }

    /// <summary>Server only: peers that may not speak, and until when (real time).</summary>
    private readonly Dictionary<long, (string By, double Until)> _muted = new();

    /// <summary>Server only: when a muted peer was last told why (so a spammer cannot amplify it).</summary>
    private readonly Dictionary<long, double> _muteToldAt = new();

    /// <summary>Server only: who whispered to whom, for <c>/r</c>.</summary>
    private readonly Dictionary<long, long> _lastWhisper = new();

    /// <summary>Server only: when each peer last sent a report.</summary>
    private readonly Dictionary<long, double> _reportedAt = new();

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

    /// <summary>
    /// Where a peer's body is in this server's world frame: from its Earth position, so it is right
    /// whichever region the body's own physics is in (#1456).
    /// </summary>
    private Vector3? WorldOf(long peer) =>
        _origin != null && _players?.GetNodeOrNull<Player.FootPlayer>(peer.ToString()) is { } body ? _origin.ToWorld(body.Global) : null;
    private PlaceIndex? _places;

    /// <summary>Client-side only: where a forced teleport is applied.</summary>
    public Teleporter? Teleporter { get; set; }

    /// <summary>Client-side only: where <c>/spawn</c> puts items, and the towns <c>/city</c> offline looks in.</summary>
    public Items.Inventory? Inventory { get; set; }

    /// <summary>Adds a stack, dropping what does not fit on the ground; returns how many went nowhere. Falls back to a plain add.</summary>
    public Func<Items.ItemStack, int>? GiveOrDrop { get; set; }
    public PlaceSearchUi? PlaceSearch { get; set; }

    /// <summary>Client-side: <c>/orbit &lt;km&gt;</c> for the own player's saucer (#1430 H5); the line to show.</summary>
    public Func<double, string>? PutInOrbit { get; set; }

    /// <summary><c>/goto &lt;body&gt; &lt;lat&gt; &lt;lon&gt; [km]</c> offline (#1850): the fly camera there; the line to show.</summary>
    public Func<string, string>? GoTo { get; set; }

    /// <summary>Client-side: where this player is, for the nearest venue offline (<c>/stadium</c>).</summary>
    public Func<GlobalPos?>? Here { get; set; }

    /// <summary>
    /// Client-side: nobody to ask — no server connection, so commands run here, on this machine,
    /// with the rights of someone who owns the world. A server's own node never is.
    /// </summary>
    private bool IsLocal => _registry is null && !Permissions.Online;

    /// <summary>Client-side: player names as last sent by the server, for completion.</summary>
    public IReadOnlyList<string> PlayerNames => _playerNames;

    private string[] _playerNames = [];

    /// <summary>
    /// Client: every player's display name by peer id, as last sent by the server, for the tags over
    /// their heads (<see cref="Player.NameTag"/>, #1136). Empty offline.
    /// </summary>
    public IReadOnlyDictionary<long, string> TagNames => _tagNames;

    private readonly Dictionary<long, string> _tagNames = new();

    /// <summary>Client: <see cref="TagNames"/> changed.</summary>
    public event Action? TagNamesReceived;

    /// <summary>Raised on the client for every line to display.</summary>
    public event Action<string, ChatKind>? LineReceived;

    /// <summary>
    /// Raised on the client when a player said something in the chat (not a command, not a system
    /// line): who (their peer id; offline, <see cref="OfflineSelf"/>) and what, for the speech
    /// bubble over their head (#862, <see cref="Ui.Bubbles.ChatBubbles"/>).
    /// </summary>
    public event Action<long, string>? Spoken;

    /// <summary>The speaker of <see cref="Spoken"/> offline: oneself.</summary>
    public const long OfflineSelf = -1;

    /// <summary>Raised on the client when the server closes the connection deliberately.</summary>
    public event Action<string>? Kicked;

    /// <summary>
    /// Client: <c>/help [command]</c> opens this screen's command browser rather than printing lines
    /// into the chat. The argument is a filter (empty: everything). Nothing is sent to the server.
    /// </summary>
    public event Action<string>? HelpRequested;

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
        if (player.Bodiless) RemoteLine?.Invoke(player.PeerId, player.IsAdmin ? "You are an admin." : "You are no longer an admin.");
        else
        {
            RpcId(player.PeerId, MethodName.AdminStatus, player.IsAdmin);
            // an admin's /movespeed (#1485) goes with the rights
            if (!player.IsAdmin) SetMoveSpeed(player.PeerId, 1f);
        }
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

    /// <summary>Server: a peer's name was set or changed (shown only: saved data is keyed by its id, <see cref="PlayerIds"/>).</summary>
    public event Action<long>? NameAssigned;

    /// <summary>Server: operator rights for the other server-side systems (vehicle spawning).</summary>
    public bool IsAdminPeer(long peerId) => IsAdmin(peerId);

    // ---- client -> server ------------------------------------------------------------

    /// <summary>Sends a line of chat, or a command when it starts with '/'.</summary>
    public void Send(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = Scrub(text);
        // the command browser and the ignore list live on this screen: neither is the server's business
        string trimmed = text.Trim();
        if (trimmed.StartsWith('/'))
        {
            string[] head = trimmed[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (head.Length > 0)
            {
                string verb = head[0].ToLowerInvariant();
                string rest = head.Length > 1 ? head[1].Trim() : "";
                if (verb == "help" && HelpRequested != null)
                {
                    HelpRequested(rest.TrimStart('/'));
                    return;
                }
                if (verb is "ignore" or "unignore")
                {
                    IgnoreCommand(verb, rest);
                    return;
                }
            }
        }
        if (LocalClock(text.Trim()) is { } clock)
        {
            LineReceived?.Invoke(clock, ChatKind.Private);
            return;
        }
        // the own stamina meter (#1225): the owner's, never replicated
        if (Player.FootPlayer.StaminaCommand(text.Trim()) is { } stamina)
        {
            LineReceived?.Invoke(stamina, ChatKind.Private);
            return;
        }
        // tracking a point and showing it on the map are this screen's alone (#1277)
        if (World.Locate.LocateClient.Run(text) is var (located, locateFailed))
        {
            if (located.Length > 0) LineReceived?.Invoke(located, locateFailed ? ChatKind.Error : ChatKind.Private);
            return;
        }
        // a cinema hall's video (#1298): this client knows the hall it is in, and asks the server as the panel does
        if (Interiors.CinemaBooths.RunLocal(text.Trim()) is { } cinema)
        {
            LineReceived?.Invoke(cinema, ChatKind.Private);
            return;
        }
        // the visual style is this screen's alone: never a server's business
        if (Styles.StyleCommand.Run(text.Trim()) is { } style)
        {
            LineReceived?.Invoke(style, ChatKind.Private);
            return;
        }
        // a concert operator's show input and patch (#885): this machine's ports and files
        if (Concert.ConcertService.RunLocal(text.Trim(), line => LineReceived?.Invoke(line, ChatKind.Private)) is { } show)
        {
            LineReceived?.Invoke(show, ChatKind.Private);
            return;
        }
        // a feature clip films this screen (#487)
        if (Core.ClipRecorder.Run(text.Trim(), line => LineReceived?.Invoke(line, ChatKind.Private)) is { } clip)
        {
            LineReceived?.Invoke(clip, ChatKind.Private);
            return;
        }
        // the catalogue is a panel on this screen; what it gives still goes through /spawn
        if (text.Trim().ToLowerInvariant() is "/catalogue" or "/catalog" or "/items")
        {
            if (!CanUseCatalogue) LineReceived?.Invoke(Permissions.InMatch ? NotInMatch : "The item catalogue is for admins on a server.", ChatKind.Error);
            else if (CatalogueRequested is null) LineReceived?.Invoke("No catalogue here.", ChatKind.Error);
            else CatalogueRequested();
            return;
        }
        // the debug menu draws on this screen only; a server never hears of it (#339)
        // "/debug <tools>" (#1743): the --debugview words switched on (or "off" for all), the panel left as it is
        if (text.Trim().ToLowerInvariant() is var debug && (debug == "/debug" || debug.StartsWith("/debug ")))
        {
            string tools = debug.Length > 6 ? debug[7..].Trim() : "";
            if (!DebugMenu.Allowed) LineReceived?.Invoke(Permissions.InMatch ? NotInMatch : "The debug menu is for admins on a server.", ChatKind.Error);
            else if (DebugRequested is null) LineReceived?.Invoke("No debug menu here.", ChatKind.Error);
            else if (tools.Length == 0) DebugRequested();
            else if (DebugToolsRequested is null) LineReceived?.Invoke("No debug tools here.", ChatKind.Error);
            else DebugToolsRequested(tools);
            return;
        }
#if PLAYTEST
        // a playtest note (#844): stamped on this screen, never sent
        if (text.Trim() is var noteLine && (noteLine.Equals("/note", StringComparison.OrdinalIgnoreCase) || noteLine.StartsWith("/note ", StringComparison.OrdinalIgnoreCase)))
        {
            if (NoteRequested is null) LineReceived?.Invoke("Notes need a recorded playtest (--playtest-record).", ChatKind.Error);
            else NoteRequested(noteLine[5..].Trim());
            return;
        }
#endif
        if (IsLocal)
        {
            HandleLocal(ClampText(text.Trim()));
            return;
        }

        RpcId(1, MethodName.SubmitLine, ClampText(text));
    }

    /// <summary>
    /// <c>/ignore &lt;name&gt;</c> and <c>/unignore &lt;name&gt;</c>: hide a player's lines and bubbles on
    /// this screen, saved with the settings. No argument lists who is ignored. A local preference:
    /// the server is never told, and nothing is hidden from anyone else.
    /// </summary>
    private void IgnoreCommand(string verb, string name)
    {
        var ignored = GameSettings.Current.ChatIgnore;
        if (name.Length == 0)
        {
            LineReceived?.Invoke(ignored.Count == 0
                ? "Nobody is ignored. /ignore <player> hides their chat and bubbles."
                : $"Ignored: {string.Join(", ", ignored)}. /unignore <player> to see them again.", ChatKind.Private);
            return;
        }

        string clean = PlayerRegistry.Sanitize(name);
        if (clean.Length == 0)
        {
            LineReceived?.Invoke("Usage: /ignore <player>", ChatKind.Error);
            return;
        }
        bool already = ignored.Any(n => n.Equals(clean, StringComparison.OrdinalIgnoreCase));
        if (verb == "ignore")
        {
            if (already)
            {
                LineReceived?.Invoke($"{clean} is already ignored.", ChatKind.Private);
                return;
            }
            ignored.Add(clean);
            LineReceived?.Invoke($"{clean} is ignored: their chat and speech bubbles are hidden. /unignore {clean} brings them back.", ChatKind.Private);
        }
        else
        {
            if (!already)
            {
                LineReceived?.Invoke($"{clean} was not ignored.", ChatKind.Private);
                return;
            }
            ignored.RemoveAll(n => n.Equals(clean, StringComparison.OrdinalIgnoreCase));
            LineReceived?.Invoke($"{clean} is no longer ignored.", ChatKind.Private);
        }
        GameSettings.Current.Commit();
    }

    /// <summary>Whether <paramref name="name"/> is on this screen's ignore list.</summary>
    public static bool Ignores(string name) =>
        name.Length > 0 && GameSettings.Current.ChatIgnore.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The speaker of a chat line, for the ignore list and the display: <c>Name: text</c>,
    /// <c>Name whispers: text</c>, or <c>* Name action</c>. Null for a line that names nobody.
    /// </summary>
    public static string? SpeakerOf(string line, ChatKind kind)
    {
        if (kind == ChatKind.Whisper)
        {
            // "Alice whispers: text" to the receiver, "You whisper to Alice: text" to the sender
            int whisper = line.IndexOf(" whispers: ", StringComparison.Ordinal);
            if (whisper > 0) return line[..whisper];
        }
        if (kind is ChatKind.Say or ChatKind.Whisper)
        {
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            return colon > 0 ? line[..colon] : null;
        }
        if (kind == ChatKind.System && line.StartsWith("* ", StringComparison.Ordinal))
        {
            int space = line.IndexOf(' ', 2);
            return space > 2 ? line[2..space] : null;
        }
        return null;
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
            _registry.InWorld.Select(p => p.Name).ToArray());
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveNames(string[] names)
    {
        bool changed = !names.SequenceEqual(_playerNames);
        _playerNames = names;
        if (changed) NamesReceived?.Invoke();
    }

    /// <summary>Server: sends everyone the name of every player in the world, for their name tags (#1136).</summary>
    private void SendTagNames()
    {
        if (_registry is null) return;
        var players = _registry.InWorld.ToArray();
        Rpc(MethodName.ReceiveTagNames, players.Select(p => p.PeerId).ToArray(), players.Select(p => p.Name).ToArray());
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveTagNames(long[] peers, string[] names)
    {
        _tagNames.Clear();
        for (int i = 0; i < peers.Length && i < names.Length; i++) _tagNames[peers[i]] = names[i];
        TagNamesReceived?.Invoke();
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

    /// <summary>Client: <c>/debug</c> was typed by someone allowed it (alone, or an admin); the debug menu opens.</summary>
    public event Action? DebugRequested;

    /// <summary>Client: <c>/debug &lt;tools&gt;</c> (the <c>--debugview</c> words, or <c>off</c>) typed by someone allowed the menu.</summary>
    public event Action<string>? DebugToolsRequested;
#if PLAYTEST
    /// <summary>Client: <c>/note &lt;text&gt;</c>, a playtest note (#844), for the recorder to stamp.</summary>
    public event Action<string>? NoteRequested;
#endif

    /// <summary>Client: the catalogue is offered alone, or to an admin. The server re-checks every <c>/spawn</c> it sends.</summary>
    public bool CanUseCatalogue => !Permissions.InMatch && (IsLocal || Permissions.IsAdmin);

    /// <summary>Client: an admin emptied this inventory (<c>/clear</c>).</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ClearInventory(string by) => LineReceived?.Invoke(Clear(by.Length == 0 ? null : by), ChatKind.Admin);

    /// <summary>
    /// Client: <c>/transfer</c> (#649) gives this inventory to <paramref name="to"/>: everything
    /// <c>/clear</c> empties plus the pocket's cash goes to the server, and leaves here.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void HandOver(string by, string to)
    {
        var ids = new List<int>();
        var counts = new List<int>();
        var data = new List<string>();
        int cash = 0;
        if (Inventory is { InMatch: false } inv)
        {
            var (slots, carried) = inv.Snapshot();
            foreach (var st in slots.Take(Items.Inventory.FirstWearSlot).Append(carried))
            {
                if (st.IsEmpty) continue;
                ids.Add((int)st.Id);
                counts.Add(st.Count);
                data.Add(st.Data ?? "");
            }
            cash = inv.Cash;
            inv.Clear();
            inv.TakeCash(cash);
        }
        RpcId(1, MethodName.HandedOver, ids.ToArray(), counts.ToArray(), data.ToArray(), cash);
        LineReceived?.Invoke(by.Length == 0 ? $"You gave your inventory to {to}." : $"{by} gave your inventory to {to}.", ChatKind.Admin);
    }

    /// <summary>Client: another player's inventory arrives (#649); what does not fit drops at our feet.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveInventory(int[] ids, int[] counts, string[] data, int cash, string from)
    {
        if (Inventory is null) return;
        int stacks = 0;
        for (int i = 0; i < ids.Length && i < counts.Length && i < data.Length; i++)
        {
            if (!Enum.IsDefined((Items.ItemId)ids[i]) || counts[i] <= 0) continue;
            var st = new Items.ItemStack((Items.ItemId)ids[i], counts[i], data[i].Length == 0 ? null : data[i]);
            if (GiveOrDrop is { } give) give(st);
            else Inventory.Add(st);
            stacks++;
        }
        if (cash > 0) Inventory.Add(Items.ItemId.Francs, cash);
        LineReceived?.Invoke($"{from}'s inventory is now yours: {stacks} stack(s), {cash} CHF.", ChatKind.Admin);
    }

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

    /// <summary><c>/locate</c> offline (#1277): this machine's own <see cref="World.Locate.LocateService"/>.</summary>
    private async void LocateLocal(string rest)
    {
        if (World.Locate.LocateService.Instance is not { } locate)
        {
            LineReceived?.Invoke("No map data to search here.", ChatKind.Error);
            return;
        }
        var (lines, failed) = await locate.Answer(rest, Here?.Invoke());
        if (!IsInsideTree()) return;
        foreach (string line in lines) LineReceived?.Invoke(line, failed ? ChatKind.Error : ChatKind.Private);
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
            Spoken?.Invoke(OfflineSelf, Scrub(text));
            return;
        }

        string[] parts = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        string verb = parts[0].ToLowerInvariant();
        string rest = string.Join(' ', parts[1..]);

        switch (verb)
        {
            case "help":
                if (rest.Length > 0)
                {
                    Show(ChatCompleter.HelpFor(rest, online: false, isAdmin: true) ?? $"No command '/{rest.TrimStart('/')}' offline. /help lists them.", ChatKind.Private);
                    return;
                }
                foreach (string line in ChatCompleter.Help(online: false, isAdmin: true)) Show(line, ChatKind.Private);
                Show("  Offline: races, Battle Royale, parties and the other player commands need a multiplayer game.", ChatKind.Private);
                return;

            case "who":
                Show("1 online: you", ChatKind.Private);
                return;

            case "me":
                if (rest.Length > 0) Show($"* {Scrub(rest)}", ChatKind.System);
                return;

            case "travel" or "pin":
                if (Adventure.Navigation.AdventureTravel.Instance is not { } travel) Show("The adventure is not running here.", ChatKind.Error);
                else foreach (string line in travel.Command(Adventure.AdventureService.LocalPeer, verb, rest, admin: true)) Show(line, ChatKind.Private);
                return;

            case "city" when !Permissions.CanTeleport:
                Show("You chose the travel network (Settings > Gameplay > Travel): travel from a station sign, or with the GA.", ChatKind.Error);
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

            case "locate" or "find":
                LocateLocal(rest);
                return;

            case "go":
                if (!World.Locate.LocateCoords.TryParse(rest, out double goE, out double goN)) Show("Usage: /go @E,N (LV95 metres, e.g. /go @2533450,1152300)", ChatKind.Error);
                else if (Teleporter?.TeleportTo(goE, goN, World.Locate.LocateCoords.Format(goE, goN)) == true) Show($"Teleported to {World.Locate.LocateCoords.Format(goE, goN)}", ChatKind.System);
                else Show("Nothing to teleport here.", ChatKind.Error);
                return;

            case "stadium" or "venue":
            {
                var (lines, venue, refused) = StadiumReply(rest, Here?.Invoke());
                foreach (string line in lines) Show(line, refused ? ChatKind.Error : ChatKind.Private);
                if (venue is not null)
                {
                    if (Teleporter?.TeleportTo(venue.E, venue.N, venue.Name) == true) Show($"Teleported to {venue.Name}", ChatKind.System);
                    else Show("Nothing to teleport here.", ChatKind.Error);
                }
                return;
            }

            // straight into a building, no door (#1244): as --enter
            case "enter":
                if (rest.Length == 0) Show("/enter <venue name | building key>, e.g. /enter bowlingland or /enter 2510_1140_26", ChatKind.Error);
                else if (Interiors.InteriorManager.Instance is not { } enterIn) Show("There are no interiors here.", ChatKind.Error);
                else
                {
                    enterIn.Enter(rest, enterIn.LocalPlayer?.Invoke());
                    Show($"Going inside {rest}...", ChatKind.System);
                }
                return;

            case "tp":
                if (!AdminArgs.TryLv95(rest, out double tpE, out double tpN))
                    Show("Offline: /tp <E> <N> (LV95, e.g. /tp 2600000 1200000); '/tp <player>' needs a multiplayer game.", ChatKind.Error);
                else if (Teleporter?.TeleportTo(tpE, tpN) == true) Show($"Teleported to {tpE:F0} {tpN:F0}", ChatKind.System);
                else Show("Nothing to teleport here.", ChatKind.Error);
                return;

            case "orbit":
                Show(OrbitLine(rest), ChatKind.Admin);
                return;

            // the fly camera to a place on any body (#1850)
            case "goto":
                Show(GoTo?.Invoke(rest) ?? "No camera to move here.", ChatKind.Admin);
                return;

            case "spawn":
                if (Items.ItemLookup.TryParse(rest, out var def, out int count, out string error)) Show(Give(def, count), ChatKind.Admin);
                else Show(error, ChatKind.Error);
                return;

            case "time":
                Show(LocalTime(parts[1..], out bool failed), failed ? ChatKind.Error : ChatKind.Admin);
                return;

            // the player's own movement speed (#1485): offline the player is the admin
            case "movespeed":
                if (parts.Length == 1) Show($"Your movement speed: {Player.MoveSpeedCommand.Describe(Player.FootPlayer.OwnSpeedFactor)}.", ChatKind.Private);
                else if (parts.Length > 2) Show("Offline you are the only player: /movespeed <factor>.", ChatKind.Error);
                else if (Player.MoveSpeedCommand.TryParse(parts[1], out float moveFactor, out string moveError))
                {
                    Player.FootPlayer.SetOwnSpeedFactor(moveFactor);
                    Show($"Your movement speed: {Player.MoveSpeedCommand.Describe(moveFactor)}.", ChatKind.Admin);
                }
                else Show(moveError, ChatKind.Error);
                return;

            case "speed":
                if (parts.Length == 1) Show($"The simulation runs at {Core.SimClock.Describe(Core.SimClock.Scale)}.", ChatKind.Private);
                else if (Core.SimClock.TryParse(rest, out double simScale, out string speedError))
                {
                    // offline there is no RPC to wait for, but it goes through the same scheduled
                    // path so there is one way a speed change ever takes effect (ClockSync.Tick)
                    Core.SimClock.Schedule(simScale, ClockSync.ServerNow);
                    Show($"The simulation runs at {Core.SimClock.Describe(simScale)}.", ChatKind.Admin);
                }
                else Show(speedError, ChatKind.Error);
                return;

            case "water":
                Show(World.WaterField.Command(rest, GetViewport().GetCamera3D()?.GlobalPosition), ChatKind.Private);
                return;

            case "weather":
                if (World.Snow.SnowService.Instance is not { } snowHere) Show("There is no ski weather in this world.", ChatKind.Error);
                else Show(snowHere.Command(rest, mayChange: true, out bool weatherSet), weatherSet ? ChatKind.Admin : ChatKind.Private);
                return;

            case "cd" or "cds":
                Show(Audio.Cd.CdLibrary.Instance?.Command(rest, mayChange: true) ?? "There are no CDs here.", ChatKind.Private);
                return;

            case "seastate":
                if (parts.Length == 1) Show($"The sea is {World.SeaStateCommand.Describe(World.WaterField.SeaState)}.", ChatKind.Private);
                else if (World.SeaStateCommand.TryParse(rest, out float sea, out string seaError))
                {
                    World.WaterField.SetSeaState(sea);
                    Show($"Sea state set to {World.SeaStateCommand.Describe(sea)}.", ChatKind.Admin);
                }
                else Show(seaError, ChatKind.Error);
                return;

            case "give":
                Show("Offline you are the only player: /spawn <item> [count], or /catalogue.", ChatKind.Error);
                return;

            case "clear":
                Show(Clear(null), ChatKind.Admin);
                return;

            case "despawn":
            {
                var (line, refused) = Despawn(parts[1..], GetViewport().GetCamera3D()?.GlobalPosition);
                Show(line, refused ? ChatKind.Error : ChatKind.Admin);
                return;
            }

            case "transfer":
                Show("Offline you are the only player: nobody to transfer to.", ChatKind.Error);
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

            case "concert" when IsConcertJoin(rest):
            {
                var (line, venue) = ConcertJoinReply(Concert.ConcertService.Instance?.HostedVenues(Here?.Invoke()));
                Show(line, venue is null ? ChatKind.Error : ChatKind.Private);
                if (venue is not null)
                {
                    if (Teleporter?.TeleportTo(venue.E, venue.N, venue.Name) == true) Show($"Teleported to {venue.Name}", ChatKind.System);
                    else Show("Nothing to teleport here.", ChatKind.Error);
                }
                return;
            }

            case "concert":
                Show(Concert.ConcertService.Instance?.LocalCommand(string.Join(' ', parts[1..])) ?? "Concerts are not running.", ChatKind.Private);
                return;

            case "trial":
                Show(Adventure.Rewards.RewardService.Instance?.Command(Adventure.AdventureService.LocalPeer, string.Join(' ', parts[1..])) ?? "Trials are not running.", ChatKind.Private);
                return;

            // a hall's lights and film by its number (#1306); the films' words below
            case "cinema" when parts.Length > 1 && int.TryParse(parts[1], out _):
                if (Interiors.CinemaBooths.Instance is not { } hallBooths) Show("There are no cinemas here.", ChatKind.Error);
                else Show(hallBooths.Command(parts[1..], admin: true, Interiors.InteriorManager.Instance?.Current?.Key), ChatKind.Private);
                return;
            // the films a cinema can be showing (#1298): offline the player is the admin
            case "cinema":
                Show(Interiors.CinemaBooths.Instance?.PoolCommand(1, string.Join(' ', parts[1..]), true, line => Show(line, ChatKind.Private))
                     ?? "No cinemas here.", ChatKind.Private);
                return;
            // pinball (#1325): the venue's machines; step up to one, a robot at the flippers, the top view
            case "pinball":
                Show(Interiors.Pinball.PinballTables.Instance?.Command(rest, Interiors.InteriorManager.Instance?.Current?.Key, Interiors.InteriorManager.Instance?.LocalPlayer?.Invoke()) ?? "Pinball is not running.", ChatKind.Private);
                return;
            // pool, darts, table football, the arcade's games (#1324): list, step up, a bot, an action by numbers
            case "games":
                Show(Interiors.Games.TableGames.Instance?.Command(rest, Interiors.InteriorManager.Instance?.LocalPlayer?.Invoke()) ?? "The games are not running.", ChatKind.Private);
                return;
            // bowling (#1323): sign in, take a ball, roll by numbers, in the centre the player is in
            case "bowl":
                Show(Interiors.BowlingLanes.Instance?.Command(1, rest, Interiors.InteriorManager.Instance?.Current?.Key, "You") ?? "Bowling is not running.", ChatKind.Private);
                return;
            // playing together (#1266): the rope and the shared pot
            case "alcohol":
                Show(Adventure.Alcohol.AlcoholService.Instance?.Command(Adventure.AdventureService.LocalPeer, rest) ?? "Alcohol needs the Adventure layer.", ChatKind.Private);
                return;

            case "birthday":
                // offline (#1423): only this player; their day, or when it is
                Show(Occasions.Birthdays.MyBirthday.IsToday ? Occasions.Birthdays.MyBirthday.Greeting(GameSettings.Current.PlayerName)!
                    : Occasions.Birthdays.MyBirthday.Date.IsSet ? "It is not your birthday today." : "Enter your birthday on the Account page, from the title screen.",
                    ChatKind.Private);
                return;

            case "rope" or "fondue":
                Show(Adventure.Together.TogetherService.Instance?.Command(Adventure.AdventureService.LocalPeer, verb, rest) is { } said
                    ? said.Length > 0 ? said : "Asked." : "Playing together needs the Adventure layer.", ChatKind.Private);
                return;

            // a training dummy for the sword (#1371): offline the one process is its own server
            case "dummy":
                Show(Combat.SwordService.Instance?.Command(Adventure.AdventureService.LocalPeer, rest) ?? "[dummy] Not available here.", ChatKind.Private);
                return;

            case "name" or "login" or "stream" or "race" or "fight" or "aura" or "say" or "admin" or "bring" or "tpall" or "kick" or "pvp" or "br" or "update"
                or "invites" or "accept" or "decline" or "tpa" or "tphere" or "party" or "vs" or "versteckis"
                or "w" or "whisper" or "tell" or "msg" or "r" or "reply" or "report" or "mute" or "unmute":
                // Versteckis has no offline game (#1268): its bots need a server; Host a game from the menu
                Show(verb is "vs" or "versteckis" ? "Versteckis needs a multiplayer game: Host a game from the menu, and bots fill it." : $"'/{verb}' needs a multiplayer game.", ChatKind.Error);
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

    /// <summary>Hosting from the menu: proves to the server this client is its host.</summary>
    public void ClaimHost(string token) => RpcId(1, MethodName.SubmitHostToken, token);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitHostToken(string token)
    {
        if (_registry is null) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (!_registry.TryClaimHost(sender, token))
            GD.PushWarning($"[admin] wrong host token from peer {sender} ({NameOf(sender)})");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitLine(string text)
    {
        if (_registry is null) return;   // only the server acts on this
        long sender = Multiplayer.GetRemoteSenderId();

        // How often a client talks is the one thing it fully controls: the bucket is checked before
        // any parsing, so neither chat nor commands can be used to flood the server.
        // A test run lets every line through (#1857): a probe talks faster than a person, and a dropped step
        // marker or command failed checks at random; --chat-flood keeps the gate for a check of the gate.
        string? slow = null;
        if (!FloodGateOff && !AcceptLine(sender, out slow)) return;
        if (slow != null) ReplyTo(sender, slow, ChatKind.Error);

        // Trim and scrub before anything else: length and control characters are a client's to choose.
        text = ClampText(Scrub(text).Trim());
        if (text.Length == 0) return;

        if (text.StartsWith('/')) HandleCommand(sender, text[1..]);
        else Say(sender, text);
    }

    /// <summary>
    /// Server: whether <paramref name="peer"/> may send a line now. A token bucket (a short burst,
    /// then a slow refill) with an escalating silence for someone who keeps hammering; <paramref name="warning"/>
    /// is set only when it is worth telling them, so a flood cannot amplify itself through error lines.
    /// </summary>
    internal bool AcceptLine(long peer, out string? warning)
    {
        warning = null;
        double now = Core.RealClock.Now;
        if (!_flood.TryGetValue(peer, out var gate))
            _flood[peer] = gate = new FloodGate { Last = now };

        if (now < gate.QuietUntil)
        {
            if (now >= gate.WarnAt)
            {
                gate.WarnAt = now + FloodWarnSeconds;
                warning = $"Chat is paused for you: wait {Math.Ceiling(gate.QuietUntil - now):0} s.";
            }
            return false;
        }

        gate.Tokens = Math.Min(FloodBurst, gate.Tokens + (now - gate.Last) * FloodPerSecond);
        gate.Last = now;
        if (gate.Tokens >= 1)
        {
            gate.Tokens -= 1;
            gate.Strikes = 0;
            return true;
        }

        // Over the line: a short silence that doubles if they keep going (up to a minute).
        gate.Strikes = Math.Min(gate.Strikes + 1, 5);
        double quiet = Math.Min(60, 3 * Math.Pow(2, gate.Strikes - 1));
        gate.QuietUntil = now + quiet;
        gate.WarnAt = now + FloodWarnSeconds;
        warning = $"Slow down: chat is paused for {quiet:0} s.";
        return false;
    }

    /// <summary>
    /// Server: <paramref name="peer"/> says <paramref name="text"/> in the chat: the line to
    /// everyone, and who said it, for the bubble over their head. A server-run player (a bot) talks
    /// through here too.
    /// </summary>
    public void Say(long peer, string text)
    {
        text = ClampText(Scrub(text).Trim());
        if (text.Length == 0) return;
        if (Muted(peer, out double mutedSeconds))
        {
            // tell them at most every few seconds: a muted flood must not become a reply flood
            double now = Core.RealClock.Now;
            if (!_muteToldAt.TryGetValue(peer, out double told) || now - told >= FloodWarnSeconds)
            {
                _muteToldAt[peer] = now;
                ReplyTo(peer, $"You are muted for another {Math.Ceiling(mutedSeconds / 60):0} min.", ChatKind.Error);
            }
            return;
        }
        // a round's spectators talk among themselves while it runs (#1268, Core/Rounds): nobody calls out the spots
        if (Core.Rounds.ChatScope(peer) is { } scope && Versteckis != null)
        {
            foreach (long member in Versteckis.ScopeMembers(scope)) ReplyTo(member, $"{NameOf(peer)} (spectating): {text}", ChatKind.Say);
            return;
        }
        Broadcast($"{NameOf(peer)}: {text}", ChatKind.Say);
        Rpc(MethodName.Said, peer, text);
    }

    /// <summary>Server: whether <paramref name="peer"/> is muted now, and for how much longer.</summary>
    private bool Muted(long peer, out double seconds)
    {
        seconds = 0;
        if (!_muted.TryGetValue(peer, out var mute)) return false;
        seconds = mute.Until - Core.RealClock.Now;
        if (seconds > 0) return true;
        _muted.Remove(peer);
        return false;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Said(long peer, string text)
    {
        // an ignored player's lines are hidden here too, so no bubble appears over their head
        if (_tagNames.TryGetValue(peer, out string? name) && Ignores(name)) return;
        Spoken?.Invoke(peer, text);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitName(string name)
    {
        if (_registry is null) return;
        long sender = Multiplayer.GetRemoteSenderId();

        string assigned = _registry.Rename(sender, name);
        NameAssigned?.Invoke(sender);
        SendTagNames();
        Broadcast($"{assigned} joined", ChatKind.System);
        ReplyTo(sender, "Type /help for commands.", ChatKind.Private);
    }

    // ---- server -> client ------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Deliver(string line, int kind)
    {
        var k = (ChatKind)kind;

        // a player this screen ignores: neither their speech nor their whisper is shown
        if (k is ChatKind.Say or ChatKind.Whisper or ChatKind.System
            && SpeakerOf(line, k) is { } speaker && Ignores(speaker)) return;

        // the join/leave ticker can be turned off without losing the rest of the world's news
        if (k == ChatKind.System && !GameSettings.Current.ChatShowJoins
            && (line.EndsWith(" joined", StringComparison.Ordinal) || line.EndsWith(" left", StringComparison.Ordinal))) return;

        LineReceived?.Invoke(line, k);
    }

    /// <summary>Server: sends <paramref name="peer"/> to a ground point given in LV95 notation, on the wire as ECEF (#1503).</summary>
    private void SendTeleport(long peer, double lv95E, double lv95N, string label)
    {
        var (x, y, z) = GlobalPos.FromLv95(lv95E, lv95N, 0).ToWire();
        RpcId(peer, MethodName.ForceTeleport, x, y, z, label);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ForceTeleport(double x, double y, double z, string label)
    {
        // a ground point, as ECEF at sea level (#1503): the teleport finds the ground there
        var (lv95E, lv95N, _) = GlobalPos.FromWire(x, y, z).Lv95;
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

    /// <summary>Client side: an admin's <c>/orbit</c> (#1430 H5), for the saucer this client flies.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void OrbitForced(double km) => LineReceived?.Invoke(OrbitLine(km.ToString(System.Globalization.CultureInfo.InvariantCulture)), ChatKind.Admin);

    /// <summary>
    /// <c>/orbit &lt;km&gt;</c> on this client (#1430 H5): the own saucer onto a circular orbit. Offline free,
    /// online an admin's (the server checks, then sends <see cref="OrbitForced"/>).
    /// </summary>
    private string OrbitLine(string arg)
    {
        if (!double.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double km))
            return "Usage: /orbit <km>, e.g. /orbit 400";
        return PutInOrbit?.Invoke(km) ?? "No player to put in orbit.";
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NotifyKicked(string reason) => Kicked?.Invoke(reason);

    /// <summary>
    /// Server: a line for a player who joined through the server console (<see cref="PlayerInfo.Bodiless"/>,
    /// #1088): <see cref="ConsoleLink"/> sends it, since that game may not share this node's RPCs.
    /// </summary>
    public event Action<long, string>? RemoteLine;

    private bool IsBodiless(long peerId) => _registry?.Find(peerId) is { Bodiless: true };

    /// <summary>Sends a line to everyone. Server only.</summary>
    public void Broadcast(string line, ChatKind kind)
    {
        GD.Print($"[chat] {line}");
        Remember(line);
        _capture?.Add(line);   // what everyone was told answers a website command too (#1211)
        Rpc(MethodName.Deliver, line, (int)kind);
        if (RemoteLine != null && _registry != null)
            foreach (var player in _registry.Players)
                if (player.Bodiless) RemoteLine(player.PeerId, line);
    }

    /// <summary>Sends a line to one peer. Server only.</summary>
    private void ReplyTo(long peerId, string line, ChatKind kind)
    {
        // The console is not a peer; its replies go to the server's own log (and to the website's console, #1211)
        if (peerId == ConsolePeerId)
        {
            _capture?.Add(line);
            if (!_quietCapture) GD.Print($"[console] {line}");
            return;
        }
        if (IsBodiless(peerId))
        {
            RemoteLine?.Invoke(peerId, line);
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

    /// <summary>
    /// Server: a line from a player joined through the server console (<see cref="ConsoleLink"/>, #1088), as
    /// <c>SubmitLine</c> takes one from a game: a command with '/', else said to everyone. The first one joins
    /// that peer as <paramref name="name"/>, with no body, and the rights a join under that name gets
    /// (<c>admins.json</c>, or <c>/login</c> after); <paramref name="joined"/> gets it then. A peer that joined
    /// the game is not taken here.
    /// </summary>
    public void SubmitFromConsole(long peer, string name, string text, Action<PlayerInfo> joined)
    {
        if (_registry is null) return;
        if (_registry.Find(peer) is not { } player)
        {
            player = _registry.Add(peer, bodiless: true);
            _registry.Rename(peer, name);
            joined(player);
            Broadcast($"{player.Name} joined the server console", ChatKind.System);
        }
        else if (!player.Bodiless) return;   // a game's peer: it talks through SubmitLine

        text = ClampText(Scrub(text).Trim());
        if (text.Length == 0) return;
        GD.Print($"[console] {player.Name}: {text}");
        if (text.StartsWith('/')) HandleCommand(peer, text[1..]);
        else Say(peer, text);
    }

    // ---- server-side lifecycle --------------------------------------------------------

    /// <summary>Called by <see cref="ServerWorld"/> when a peer drops.</summary>
    public void ReportDisconnect(long peerId)
    {
        _transfers.Remove(peerId);
        _flood.Remove(peerId);
        _muted.Remove(peerId);
        _muteToldAt.Remove(peerId);
        _lastWhisper.Remove(peerId);
        _reportedAt.Remove(peerId);
        if (_registry?.Find(peerId) is not { } player) return;
        _registry.Remove(peerId);
        Broadcast($"{player.Name} left", ChatKind.System);
        SendTagNames();
    }

    // ---- commands ---------------------------------------------------------------------

    /// <summary>A player's display name, for the other server-side systems (races).</summary>
    public string NameOfPeer(long peerId) => NameOf(peerId);

    /// <summary>Races (<c>/race</c>), wired by ServerWorld.</summary>
    public World.RaceManager? Race { get; set; }

    /// <summary>Concerts (<c>/concert</c>, #885), wired by ServerWorld.</summary>
    public Concert.ConcertService? Concerts { get; set; }

    /// <summary>Fist fights (<c>/fight</c>, #495), wired by ServerWorld.</summary>
    public Combat.FightManager? Fight { get; set; }

    /// <summary>Aura battles (<c>/aura</c>, #907), wired by ServerWorld.</summary>
    public Combat.AuraManager? Aura { get; set; }

    /// <summary>Schwingen (<c>/schwing</c>, #1269), wired by ServerWorld.</summary>
    public Combat.SchwingManager? Schwing { get; set; }

    /// <summary>Server: the Battle Royale mode, for /br (#177).</summary>
    public BattleRoyale.BrManager? BattleRoyale { get; set; }
    /// <summary>Versteckis (#1268): <c>/vs …</c>, and the spectators' chat scope.</summary>
    public Adventure.Versteckis.VersteckisManager? Versteckis { get; set; }

    /// <summary>Server: the spawn and teleport rule (#799); <c>/city</c> asks it.</summary>
    public Arrival? Arrival { get; set; }

    /// <summary>For races: the place index (air courses to a town), a player by name, a private line.</summary>
    public PlaceIndex? Places => _places;
    public long PeerByName(string name) => _registry?.FindByName(name)?.PeerId ?? -1;
    public void Tell(long peerId, string line, ChatKind kind) => ReplyTo(peerId, line, kind);

    private string NameOf(long peerId) => peerId == ConsolePeerId
        ? _consoleAs ?? "Console"
        : _registry?.Find(peerId)?.Name ?? $"Rider{peerId}";

    /// <summary>
    /// The server's own console is always an operator — it is the process that owns the
    /// game, and it is how the first admin gets granted on a fresh server.
    /// </summary>
    private bool IsAdmin(long peerId) =>
        peerId == ConsolePeerId || (_registry?.Find(peerId)?.IsAdmin ?? false);

    /// <summary>Why an admin tool is refused to a Battle Royale entrant (#425).</summary>
    public const string NotInMatch = "Not in a Battle Royale match: everyone plays it on equal terms.";

    /// <summary>
    /// <paramref name="peer"/> is a living Battle Royale entrant (#425): the admin commands that would
    /// move, arm or pay one are refused, and <paramref name="sender"/> is told why.
    /// </summary>
    private bool RefusedInMatch(long sender, long peer)
    {
        // every round mode (#1268, Core/Rounds): the Battle Royale answers first, in its own words
        if (Core.Rounds.PlayingIn(peer) is not { } mode) return false;
        ReplyTo(sender, peer == sender ? (mode.Name == "br" ? NotInMatch : $"Not in {mode.Label}: everyone plays it on equal terms.")
            : $"{NameOf(peer)} is in {mode.Label}: everyone plays it on equal terms.", ChatKind.Error);
        return true;
    }

    /// <summary>
    /// Removes anything that would let one player's message forge another's, or break the display:
    /// newlines, control characters, and the Unicode format characters (right-to-left overrides,
    /// zero-width spaces, invisible separators) that can reverse or hide text. Emoji survive,
    /// joiners and all: this walks code points, not UTF-16 units.
    /// </summary>
    internal static string Scrub(string text)
    {
        var clean = new System.Text.StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            // keep the joiner and the variation selectors: they build emoji families, flags and
            // coloured glyphs, and cannot reverse a line; the rest of the format characters go
            bool keepsEmoji = rune.Value is 0x200D or 0xFE0E or 0xFE0F;
            bool bad = !keepsEmoji && (category is System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.Surrogate
                or System.Globalization.UnicodeCategory.PrivateUse
                or System.Globalization.UnicodeCategory.OtherNotAssigned
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator);
            clean.Append(bad ? ' ' : rune.ToString());
        }
        return clean.ToString();
    }

    /// <summary>Cuts a line to <paramref name="max"/> characters without splitting an emoji or a surrogate pair.</summary>
    internal static string ClampText(string text, int max = MaxMessageLength)
    {
        if (text.Length <= max) return text;
        var clean = new System.Text.StringBuilder(max);
        int n = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (n++ >= max) break;
            clean.Append(rune.ToString());
        }
        return clean.ToString();
    }

    /// <summary>
    /// Rejects commands that only make sense for someone with a body in the world. The
    /// server console has no avatar, so it cannot teleport itself or be brought anywhere.
    /// </summary>
    private bool RequiresAvatar(long sender, string verb)
    {
        if (sender == ConsolePeerId)
        {
            ReplyTo(sender, $"'/{verb}' needs a player; the console has no avatar.", ChatKind.Error);
            return false;
        }
        // a player joined through the server console has rights but no body: /login yes, /tp no
        if (verb != "login" && IsBodiless(sender))
        {
            ReplyTo(sender, $"'/{verb}' needs a body in the world; join the game for it.", ChatKind.Error);
            return false;
        }
        return true;
    }

    private void HandleCommand(long sender, string command)
    {
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        string verb = parts[0].ToLowerInvariant();
        string rest = parts.Length > 1 ? string.Join(' ', parts[1..]) : string.Empty;

        switch (verb)
        {
            case "help": SendHelp(sender, rest); return;
            // private messages: /w <player> <text>, and /r for the last one
            case "w" or "whisper" or "tell" or "msg": CommandWhisper(sender, rest, reply: false); return;
            case "r" or "reply": CommandWhisper(sender, rest, reply: true); return;
            // a player reports another; the server writes the context down where an admin can read it
            case "report": CommandReport(sender, rest); return;
            // ignoring is a preference on the player's own screen (ChatUi); a hand-written client is told so
            case "ignore" or "unignore":
                ReplyTo(sender, "Ignoring players is a setting on your own screen: /ignore <player> there.", ChatKind.Private);
                return;
            // invites (#840): answering them, teleport requests anyone may send, parties
            case "invites" or "accept" or "decline" or "tpa" or "tphere" or "party": InviteCommand(sender, verb, rest); return;
            case "stream": ReportStreaming(sender); return;
            case "who": SendWho(sender); return;
            case "name": if (RequiresAvatar(sender, verb)) CommandName(sender, rest); return;
            case "city": if (RequiresAvatar(sender, verb)) CommandCity(sender, rest); return;
            // the adventure's travel network and pins (#1226)
            case "travel" or "pin": if (RequiresAvatar(sender, verb)) TravelCommand(sender, verb, rest); return;
            // the venues (#994): anyone lists them; going to one is a teleport like /city
            case "stadium" or "venue": CommandStadium(sender, rest); return;
            case "locate" or "find": CommandLocate(sender, rest); return;
            case "go": if (RequiresAvatar(sender, verb)) CommandGo(sender, rest); return;
            case "login": if (RequiresAvatar(sender, verb)) CommandLogin(sender, rest); return;
            case "me":
                if (rest.Length == 0) return;
                if (Muted(sender, out double actionMuted))
                {
                    ReplyTo(sender, $"You are muted for another {Math.Ceiling(actionMuted / 60):0} min.", ChatKind.Error);
                    return;
                }
                Broadcast($"* {NameOf(sender)} {Scrub(rest)}", ChatKind.System);
                return;
            // anyone may ask; set/add/speed are checked inside, against the same IsAdmin
            case "time": CommandTime(sender, parts[1..]); return;
            case "speed": CommandSpeed(sender, rest); return;
            // anyone may ask; setting it is an admin's (checked inside)
            case "seastate": CommandSeaState(sender, rest); return;
            // CD analysis versions (#1153): anyone may ask, re-analysing is an admin's
            case "cd" or "cds":
                ReplyTo(sender, Audio.Cd.CdLibrary.Instance?.Command(rest, IsAdmin(sender)) ?? "There are no CDs on this server.", ChatKind.Private);
                return;
            // anyone may ask; changing it is an admin's (#887)
            case "weather": CommandWeather(sender, rest); return;
            // the water at a point as the server sees it (#299): a debug line, two peers compare it
            case "water":
                ReplyTo(sender, World.WaterField.Command(rest, WorldOf(sender)), ChatKind.Private);
                return;
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
            // your own inventory is yours to give; moving someone else's is an admin's (checked inside)
            case "transfer": CommandTransfer(sender, parts); return;
            case "br":
                if (BattleRoyale == null) ReplyTo(sender, "Battle Royale is not available on this server.", ChatKind.Error);
                else ReplyTo(sender, BattleRoyale.Command(sender, rest, IsAdmin(sender)), ChatKind.Private);
                return;
            // Versteckis (#1268): anyone opens, joins, leaves; the opener starts; an admin closes any
            case "vs" or "versteckis":
                if (Versteckis == null) ReplyTo(sender, "Versteckis is not available on this server.", ChatKind.Error);
                else ReplyTo(sender, Versteckis.Command(sender, rest, IsAdmin(sender)), ChatKind.Private);
                return;
            // a cinema hall's lights and film by its number (#1306), an admin's (checked inside); the films' words below
            case "cinema" when parts.Length > 1 && int.TryParse(parts[1], out _):
                if (Interiors.CinemaBooths.Instance is not { } hallBooths) ReplyTo(sender, "There are no cinemas here.", ChatKind.Error);
                else ReplyTo(sender, hallBooths.Command(parts[1..], IsAdmin(sender),
                    Interiors.InteriorManager.Instance is { } im ? im.SpaceOf(sender) is { Length: > 0 } space ? space : im.Current?.Key : null), ChatKind.Private);
                return;
            case "bot" or "bots":
                if (Bots.BotDirector.Instance is not { } bots) ReplyTo(sender, "Bots are not available on this server.", ChatKind.Error);
                else ReplyTo(sender, bots.Command(sender, rest, IsAdmin(sender),
                    id => _players?.GetNodeOrNull<Player.FootPlayer>(id.ToString())), ChatKind.Private);
                return;
            // the films a server's cinemas can be showing (#1298): anyone lists, an admin changes
            case "cinema":
                if (Interiors.CinemaBooths.Instance is not { } booths) ReplyTo(sender, "No cinemas on this server.", ChatKind.Error);
                else ReplyTo(sender, booths.PoolCommand(sender, rest, IsAdmin(sender), line => ReplyTo(sender, line, ChatKind.Private)), ChatKind.Private);
                return;
            // bowling (#1323): sign in, take a ball, roll by numbers, in the centre the sender is in
            case "bowl":
                if (Interiors.BowlingLanes.Instance is not { } bowling) ReplyTo(sender, "Bowling is not available on this server.", ChatKind.Error);
                else ReplyTo(sender, bowling.Command(sender, rest, Interiors.InteriorManager.Instance?.SpaceOf(sender), NameOf(sender)), ChatKind.Private);
                return;
            // playing together (#1266): the rope and the shared pot
            case "alcohol":
                if (Adventure.Alcohol.AlcoholService.Instance is not { } alcohol) ReplyTo(sender, "Alcohol is not available here.", ChatKind.Error);
                else ReplyTo(sender, alcohol.Command(sender, rest), ChatKind.Private);
                return;

            case "birthday":
                // who celebrates today, and the wishes of one's own (#1423)
                ReplyTo(sender, Occasions.Birthdays.BirthdayService.Server?.Command(sender) ?? "Birthdays are not kept here.", ChatKind.Private);
                return;
            case "rope" or "fondue":
                if (Adventure.Together.TogetherService.Instance is not { } together) ReplyTo(sender, "Playing together is not available here.", ChatKind.Error);
                else if (together.Command(sender, verb, rest) is { Length: > 0 } reply) ReplyTo(sender, reply, ChatKind.Private);
                return;
            // an adventure trial (#1227): leave the one you run
            case "trial":
                if (Adventure.Rewards.RewardService.Instance is not { } rewards) ReplyTo(sender, "Trials are not available here.", ChatKind.Error);
                else ReplyTo(sender, rewards.Command(sender, rest), ChatKind.Private);
                return;
            case "race":
                if (Race == null) ReplyTo(sender, "Races are not available on this server.", ChatKind.Error);
                else if (sender == ConsolePeerId && !rest.StartsWith("cancel") && !rest.StartsWith("list")) ReplyTo(sender, "'/race' needs a player.", ChatKind.Error);
                else ReplyTo(sender, Race.Command(sender, rest), ChatKind.Private);
                return;
            case "concert":
                if (Concerts == null) ReplyTo(sender, "Concerts are not available on this server.", ChatKind.Error);
                else if (IsConcertJoin(rest)) CommandConcertJoin(sender);
                else ReplyTo(sender, Concerts.Command(sender, rest, IsAdmin(sender),
                    GetNodeOrNull<Player.FootPlayer>("../Players/" + sender)?.Global), ChatKind.Private);
                return;
            case "fight":
                if (Fight == null) ReplyTo(sender, "Fights are not available on this server.", ChatKind.Error);
                else if (sender == ConsolePeerId) ReplyTo(sender, "'/fight' needs a player.", ChatKind.Error);
                else ReplyTo(sender, Fight.Command(sender, rest), ChatKind.Private);
                return;

            case "dummy":
                if (sender == ConsolePeerId) ReplyTo(sender, "'/dummy' needs a body to put it in front of.", ChatKind.Error);
                else ReplyTo(sender, GetNodeOrNull<Combat.SwordService>("../" + Combat.SwordService.NodeName)?.Command(sender, rest) ?? "[dummy] Not available here.", ChatKind.Private);
                return;

            case "schwing":
                if (Schwing == null) ReplyTo(sender, "Schwingen is not available on this server.", ChatKind.Error);
                else if (sender == ConsolePeerId) ReplyTo(sender, "'/schwing' needs a player.", ChatKind.Error);
                else ReplyTo(sender, Schwing.Command(sender, rest), ChatKind.Private);
                return;

            case "aura":
                if (Aura == null) ReplyTo(sender, "Aura battles are not available on this server.", ChatKind.Error);
                else if (sender == ConsolePeerId) ReplyTo(sender, "'/aura' needs a player.", ChatKind.Error);
                else ReplyTo(sender, Aura.Command(sender, rest), ChatKind.Private);
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

            case "movespeed": CommandMoveSpeed(sender, parts); return;

            case "admin": CommandAdmin(sender, parts); return;
            case "tp": if (RequiresAvatar(sender, verb)) CommandTeleportToPlayer(sender, rest); return;
            case "bring": if (RequiresAvatar(sender, verb)) CommandBring(sender, rest); return;
            case "tpall": CommandTeleportEveryone(sender, rest); return;
            // the pilot's client flies its saucer (#1430 H5): the orbit is put there
            case "orbit":
                if (!RequiresAvatar(sender, verb)) return;
                if (!double.TryParse(rest, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double orbitKm))
                    ReplyTo(sender, "Usage: /orbit <km>, e.g. /orbit 400", ChatKind.Error);
                else RpcId(sender, MethodName.OrbitForced, orbitKm);
                return;
            // another body online is C3b's (#1852, after D2): offline only for now
            case "goto": ReplyTo(sender, "/goto is offline only until online play reaches other bodies.", ChatKind.Error); return;
            case "kick": CommandKick(sender, parts); return;
            case "mute": CommandMute(sender, parts); return;
            case "unmute": CommandUnmute(sender, parts); return;
            case "update": CommandUpdate(sender); return;
            case "spawn": if (RequiresAvatar(sender, verb)) CommandSpawn(sender, rest); return;
            case "pvp": CommandPvp(sender, rest); return;
            case "give": CommandGive(sender, parts); return;
            case "money": CommandMoney(sender, parts); return;
            case "bank": CommandBank(sender, parts); return;
            case "despawn":
            {
                var (line, refused) = Despawn(parts[1..], WorldOf(sender));
                if (!refused) GD.Print($"[despawn] {NameOf(sender)}: {line}");
                ReplyTo(sender, line, refused ? ChatKind.Error : ChatKind.Admin);
                return;
            }

            default:
                ReplyTo(sender, $"Unknown command '/{verb}'. Try /help.", ChatKind.Error);
                return;
        }
    }

    /// <summary>
    /// <c>/despawn &lt;vehicles | items | all&gt; [radius m | everywhere]</c> (#1200), offline or on the
    /// server: parked vehicles and dropped items within the radius of <paramref name="at"/> taken out of
    /// the world. Without a body to measure from (the server console), only <c>everywhere</c>. The line
    /// for whoever asked, and whether it is a refusal.
    /// </summary>
    private static (string Line, bool Refused) Despawn(string[] args, Vector3? at)
    {
        if (!AdminArgs.TryDespawn(args, out bool vehicles, out bool items, out float? radius))
            return ($"{AdminArgs.DespawnUsage}, radius up to {AdminArgs.MaxDespawnRadius:F0} m", true);
        if (radius is { } && at == null) return ("Nowhere to measure from: /despawn <what> everywhere.", true);
        Vector3? centre = radius is null ? null : at;
        float r = radius ?? 0;
        int v = vehicles ? Vehicles.VehicleManager.Instance?.Despawn(centre, r) ?? 0 : 0;
        int i = items ? Items.DroppedItems.Instance?.Despawn(centre, r) ?? 0 : 0;
        var gone = new List<string>();
        if (vehicles) gone.Add(v == 1 ? "1 vehicle" : $"{v} vehicles");
        if (items) gone.Add(i == 1 ? "1 item" : $"{i} items");
        string where = radius is { } m ? FormattableString.Invariant($"within {m:0.#} m") : "everywhere";
        return ($"Despawned {string.Join(" and ", gone)} {where}.", false);
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

    /// <summary>
    /// <c>/help [command]</c>: the commands this sender may run, grouped, or one command's arguments,
    /// from the completer's table (#994, #1173), so a new command shows here the day it is added.
    /// </summary>
    private void SendHelp(long sender, string command)
    {
        if (command.Length > 0)
        {
            ReplyTo(sender, ChatCompleter.HelpFor(command, online: true, IsAdmin(sender))
                ?? $"No command '/{command.TrimStart('/')}' for you. /help lists them.", ChatKind.Private);
            return;
        }
        foreach (string line in ChatCompleter.Help(online: true, IsAdmin(sender)))
            ReplyTo(sender, line, ChatKind.Private);
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
        SendTagNames();

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
                // by the player's id (#1359): a name only finds them
                if (_registry.GrantAdmin(name) is { } notGranted) ReplyTo(sender, notGranted, ChatKind.Error);
                else Broadcast($"{PlayerRegistry.Sanitize(name)} is now an operator", ChatKind.Admin);
                return;

            case "remove" when name.Length > 0:
                if (_registry.RevokeAdmin(name) is { } notRevoked) ReplyTo(sender, notRevoked, ChatKind.Error);
                else Broadcast($"{PlayerRegistry.Sanitize(name)} is no longer an operator", ChatKind.Admin);
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

    /// <summary>Whether a player may teleport themselves (<c>/city</c>, <c>/stadium</c>); tells them why not.</summary>
    private bool MayTeleport(long sender)
    {
        if (Core.Rounds.PlayingIn(sender) is { } mode)
        {
            ReplyTo(sender, mode.NoTeleport.Length > 0 ? mode.NoTeleport : $"No teleporting out of {mode.Label}.", ChatKind.Error);
            return false;
        }
        if (Arrival is { FreeTeleport: false } && !IsAdmin(sender))
        {
            ReplyTo(sender, "This server does not allow teleporting. Walk, ride or fly there.", ChatKind.Error);
            return false;
        }
        return true;
    }

    /// <summary><c>/travel</c> and <c>/pin</c> (#1226): the travel network's reply lines, the admin forms for operators.</summary>
    private void TravelCommand(long sender, string verb, string rest)
    {
        if (Adventure.Navigation.AdventureTravel.Instance is not { } travel)
        {
            ReplyTo(sender, "The adventure is not running here.", ChatKind.Error);
            return;
        }
        foreach (string line in travel.Command(sender, verb, rest, IsAdmin(sender), PeerByName))
            ReplyTo(sender, line, ChatKind.Private);
    }

    private void CommandCity(long sender, string query)
    {
        if (!MayTeleport(sender)) return;
        if (query.Length == 0)
        {
            ReplyTo(sender, "Usage: /city <town>", ChatKind.Error);
            return;
        }

        if (!TryFindPlace(sender, query, out var place)) return;

        SendTeleport(sender, place.E, place.N, place.Name);
    }

    /// <summary>
    /// <c>/locate &lt;thing&gt; [+ selector ...] [in &lt;town&gt;]</c> (#1277): the nearest things the server's map data
    /// knows, from where the asker stands; each line ends in its <c>@E,N</c>, which the chat turns into buttons.
    /// </summary>
    private async void CommandLocate(long sender, string rest)
    {
        if (World.Locate.LocateService.Instance is not { } locate)
        {
            ReplyTo(sender, "This server has no map data to search.", ChatKind.Error);
            return;
        }
        var (lines, failed) = await locate.Answer(rest, _players?.GetNodeOrNull<Player.FootPlayer>(sender.ToString())?.Global);
        if (!IsInsideTree()) return;
        foreach (string line in lines) ReplyTo(sender, line, failed ? ChatKind.Error : ChatKind.Private);
    }

    /// <summary>
    /// <c>/go @E,N</c> (#1277): a teleport to a point, what the chat's Teleport button sends, on the same
    /// terms as <c>/city</c> (<see cref="MayTeleport"/>), where <c>/tp E N</c> stays an admin's.
    /// </summary>
    private void CommandGo(long sender, string rest)
    {
        if (!World.Locate.LocateCoords.TryParse(rest, out double e, out double n))
        {
            ReplyTo(sender, "Usage: /go @E,N (LV95 metres, e.g. /go @2533450,1152300)", ChatKind.Error);
            return;
        }
        if (!MayTeleport(sender)) return;
        SendTeleport(sender, e, n, World.Locate.LocateCoords.Format(e, n));
    }

    /// <summary>
    /// <c>/stadium [list [kind] | &lt;name|town|#n&gt;]</c> (#994): the nearest venue and what plays there,
    /// the numbered list, or a teleport to one, on the same terms as <c>/city</c>.
    /// </summary>
    private void CommandStadium(long sender, string rest)
    {
        var (lines, venue, failed) = StadiumReply(rest, _players?.GetNodeOrNull<Player.FootPlayer>(sender.ToString())?.Global);
        foreach (string line in lines) ReplyTo(sender, line, failed ? ChatKind.Error : ChatKind.Private);
        if (venue is null || !RequiresAvatar(sender, "stadium") || !MayTeleport(sender)) return;
        SendTeleport(sender, venue.E, venue.N, venue.Name);
    }

    private static bool IsConcertJoin(string rest) => rest.Trim().Equals("join", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>/concert join</c> (#1173): a teleport to the venue where a set is hosted, the nearest if
    /// several, on the same terms as <c>/stadium &lt;name&gt;</c>.
    /// </summary>
    private void CommandConcertJoin(long sender)
    {
        var (line, venue) = ConcertJoinReply(Concerts?.HostedVenues(_players?.GetNodeOrNull<Player.FootPlayer>(sender.ToString())?.Global));
        ReplyTo(sender, line, venue is null ? ChatKind.Error : ChatKind.Private);
        if (venue is null || !RequiresAvatar(sender, "concert join") || !MayTeleport(sender)) return;
        SendTeleport(sender, venue.E, venue.N, venue.Name);
    }

    /// <summary><c>/concert join</c>'s answer, the same on a server and offline: the line to show and the venue to go to (null: none).</summary>
    private static (string Line, StadiumSpec? Go) ConcertJoinReply(List<StadiumSpec>? hosted)
    {
        if (hosted is not [var go, ..]) return ("No set is hosted right now. /stadium shows what plays tonight.", null);
        string also = hosted.Count > 1 ? " Also hosted: " + string.Join(", ", hosted.Skip(1).Select(v => $"#{StadiumLookup.Number(v)} {v.Name}")) + " (/stadium <name>)." : "";
        return ($"To {StadiumLookup.Label(go)}: {Concert.ConcertService.ShowAt(go)}.{also}", go);
    }

    /// <summary>
    /// <c>/stadium</c>'s answer, the same on a server and offline: the lines to show, the venue to go
    /// to (null: none), and whether it is a refusal. <paramref name="at"/> is where the asker stands.
    /// </summary>
    private static (List<string> Lines, StadiumSpec? Go, bool Failed) StadiumReply(string rest, GlobalPos? at)
    {
        string[] words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && words[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            if (words.Length == 1) return ([.. StadiumLookup.List()], null, false);
            if (StadiumLookup.TryKind(string.Concat(words[1..]), out var kind)) return ([.. StadiumLookup.List(kind)], null, false);
            return (["Usage: /stadium list [stadium | arena | openair]"], null, true);
        }

        if (words.Length == 0)
        {
            if (at is not { } here) return ([.. StadiumLookup.List(), "/stadium <name | town | #n> to go to one."], null, false);
            var (near, metres) = StadiumLookup.Nearest(here.Lv95.E, here.Lv95.N);
            return ([$"Nearest venue: #{StadiumLookup.Number(near)} {StadiumLookup.Label(near)}, {StadiumLookup.Distance(metres)} away; "
                + $"{Concert.ConcertService.ShowAt(near)}.", "/stadium list for all of them, /stadium <name | town | #n> to go to one."], null, false);
        }

        var found = StadiumLookup.Find(rest);
        return found.Count switch
        {
            0 => ([$"No venue matching '{rest}'. /stadium list shows them all."], null, true),
            1 => ([$"{StadiumLookup.Label(found[0])}: {Concert.ConcertService.ShowAt(found[0])}."], found[0], false),
            _ => ([$"'{rest}' could be: " + string.Join(", ", found.Select(v => $"#{StadiumLookup.Number(v)} {StadiumLookup.Label(v)}"))], null, true),
        };
    }

    private void CommandTeleportEveryone(long sender, string query)
    {
        if (!TryFindPlace(sender, query, out var place)) return;

        // a Battle Royale is left alone (#425), and every other round (#1268): its entrants stay where the game has them
        foreach (int peer in Multiplayer.GetPeers())
            if (!Core.Rounds.Playing(peer)) SendTeleport(peer, place.E, place.N, place.Name);
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

        if (_players.GetNodeOrNull<Player.FootPlayer>(found.PeerId.ToString()) is not { } node)
        {
            ReplyTo(sender, $"{found.Name} has no position yet.", ChatKind.Error);
            return false;
        }

        // what the player published, exact: not the server's own world, far from its origin (#185)
        (e, n) = (node.Global.Lv95.E, node.Global.Lv95.N);
        return true;
    }

    private void CommandTeleportToPlayer(long sender, string name)
    {
        if (name.Length == 0)
        {
            ReplyTo(sender, "Usage: /tp <player> | <E> <N>", ChatKind.Error);
            return;
        }

        // an LV95 point (#994): what the F3 overlay and the swisstopo map show
        if (AdminArgs.TryLv95(name, out double toE, out double toN))
        {
            if (RefusedInMatch(sender, sender)) return;
            SendTeleport(sender, toE, toN, $"{toE:F0} {toN:F0}");
            return;
        }

        if (!TryLocate(sender, name, out var target, out double e, out double n)) return;
        if (RefusedInMatch(sender, sender) || RefusedInMatch(sender, target.PeerId)) return;
        if (target.PeerId == sender)
        {
            ReplyTo(sender, "You are already there.", ChatKind.Private);
            return;
        }

        SendTeleport(sender, e, n, target.Name);
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
        if (RefusedInMatch(sender, sender) || RefusedInMatch(sender, target.PeerId)) return;

        if (!TryLocate(sender, me.Name, out _, out double e, out double n)) return;

        SendTeleport(target.PeerId, e, n, me.Name);
        Broadcast($"{me.Name} brought {target.Name} to them", ChatKind.Admin);
    }

    // ---- /time --------------------------------------------------------------------------

    /// <summary>
    /// Client: <c>/time</c>, <c>/time query</c> or <c>/time [query] &lt;zone&gt;</c> is answered from the
    /// clock on this screen, which is the one the player is asking about, in the zone they are in
    /// unless they name one (#1801). Null when it is another command, or there is no clock.
    /// </summary>
    private static string? LocalClock(string text)
    {
        if (!text.StartsWith('/') || World.DayNight.Instance is not { } clock) return null;
        string[] parts = text[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("time", StringComparison.OrdinalIgnoreCase)) return null;
        if (!World.TimeCommand.TryParse(parts[1..], out World.TimeRequest request, out _) || request.Op != World.TimeOp.Query) return null;
        var date = World.SunPath.Today();
        var zone = request.Zone ?? World.LocalHours.ZoneAt(clock.Place, World.DayNight.RealSun);
        string at = zone.IsSwiss ? clock.Clock : World.TimeCommand.Describe(clock.Hour, zone, date);
        return $"It is {at} ({World.TimeCommand.DescribeSpeed(clock.MinutesPerDay)}).";
    }

    /// <summary>Offline <c>/time</c>: this machine's clock, with operator rights. The reply, and whether it failed.</summary>
    private static string LocalTime(string[] args, out bool failed)
    {
        failed = true;
        if (World.DayNight.Instance is not { } clock) return "There is no clock here.";
        if (!World.TimeCommand.TryParse(args, out World.TimeRequest request, out string error)) return error;

        // the named hours and the zone are read where the sky is: the camera's place (#1801)
        var date = World.SunPath.Today();
        var zone = World.ClockZone.Swiss;
        switch (request.Op)
        {
            case World.TimeOp.Query:
                failed = false;
                return LocalClock("/time " + string.Join(' ', args)) ?? $"It is {clock.Clock}.";
            case World.TimeOp.Set:
                if (!World.TimeCommand.Resolve(request, clock.Place, date, World.DayNight.RealSun,
                        out double hour, out zone, out error))
                    return error;
                clock.Hour = hour;
                break;
            case World.TimeOp.Add: clock.Hour = World.TimeCommand.Wrap(clock.Hour + request.Value); break;
            case World.TimeOp.Speed: clock.DayLengthOverride = (float)request.Value; break;
        }
        failed = false;
        return request.Op == World.TimeOp.Speed
            ? $"The clock runs at {World.TimeCommand.DescribeSpeed(clock.MinutesPerDay)}."
            : zone.IsSwiss ? $"Time set to {clock.Clock}."
            : $"Time set to {World.TimeCommand.Describe(clock.Hour, zone, date)}.";
    }

    /// <summary>
    /// Server <c>/time</c>. Changing it is an admin's, and it changes it for everyone: the server
    /// always keeps the world's clock (<see cref="World.WorldClock"/>, #452), re-bases it here and
    /// sends it to each client, as it does to whoever joins (<see cref="SendWorldTimeTo"/>). A named
    /// hour or a typed hour is read where the admin stands, from the position their client published
    /// (#1801); the console, with no place, reads Swiss time.
    /// </summary>
    private void CommandTime(long sender, string[] args)
    {
        if (!World.TimeCommand.TryParse(args, out World.TimeRequest request, out string error))
        {
            ReplyTo(sender, error, ChatKind.Error);
            return;
        }

        var date = World.SunPath.Today();
        bool realSun = World.DayNight.RealSun;
        var place = PlaceOf(sender);
        if (request.Op == World.TimeOp.Query)
        {
            // a player's own client answers this; this is the console's, or a client with no clock
            var qzone = request.Zone ?? World.LocalHours.ZoneAt(place ?? World.LocalHours.Bern, realSun);
            ReplyTo(sender, $"It is {World.TimeCommand.Describe(World.WorldClock.Hour, qzone, date)} ({World.TimeCommand.DescribeSpeed(World.WorldClock.MinutesPerDay)}).",
                ChatKind.Private);
            return;
        }

        if (!IsAdmin(sender))
        {
            ReplyTo(sender, "Changing the time is an admin command.", ChatKind.Error);
            return;
        }

        // set/add turn the sky by moving HourShift; speed rebases the monotonic counter. Neither
        // ever winds WorldClock.EnvNow back, so nothing growing in environment time goes backwards.
        var zone = World.ClockZone.Swiss;
        switch (request.Op)
        {
            case World.TimeOp.Set:
                if (!World.TimeCommand.Resolve(request, place, date, realSun, out double target, out zone, out error))
                {
                    ReplyTo(sender, error, ChatKind.Error);
                    return;
                }
                World.WorldClock.SetHour(target);
                break;
            case World.TimeOp.Add: World.WorldClock.AddHours(request.Value); break;
            case World.TimeOp.Speed: World.WorldClock.Rebase((float)request.Value); break;
        }
        double hour = World.WorldClock.Hour;
        float speed = World.WorldClock.MinutesPerDay;
        World.WorldClock.Save();
        Rpc(MethodName.WorldTime, World.WorldClock.Env0, World.WorldClock.EnvEpoch,
            World.WorldClock.HourShift, World.WorldClock.MinutesPerDay);
        string shown = World.TimeCommand.Describe(hour, zone, date);
        GD.Print($"[admin] {NameOf(sender)} set the clock to {shown}, {World.TimeCommand.DescribeSpeed(speed)}");

        string who = NameOf(sender);
        bool away = realSun && place is { } p && !World.LocalHours.IsHome(p);
        string named = request.Named is { } nm && away ? $", {nm} where they stand" : "";
        Broadcast(request.Op != World.TimeOp.Speed ? $"{who} set the time to {shown}{named}"
            : speed > 0 ? $"{who} set the day to {World.TimeCommand.DescribeSpeed(speed)}"
            : $"{who} stopped the clock at {shown}", ChatKind.Admin);
    }

    /// <summary>
    /// Where a player stands on the Earth, from the position their client published and the server already
    /// keeps (#1801); null for the console or a player with no body yet.
    /// </summary>
    private GlobalPos? PlaceOf(long sender) =>
        _players?.GetNodeOrNull<Player.FootPlayer>(sender.ToString()) is { } node ? node.Global : null;

    // ---- /weather (#887) ----------------------------------------------------------------------

    /// <summary>Server <c>/weather</c>: the ski day's weather; an admin changes it for everyone.</summary>
    private void CommandWeather(long sender, string arg)
    {
        if (World.Snow.SnowService.Instance is not { } snow)
        {
            ReplyTo(sender, "There is no ski weather on this server.", ChatKind.Error);
            return;
        }
        string reply = snow.Command(arg, IsAdmin(sender), out bool changed);
        if (!changed)
        {
            ReplyTo(sender, reply, ChatKind.Private);
            return;
        }
        GD.Print($"[admin] {NameOf(sender)} set the weather: {arg}");
        Broadcast($"{NameOf(sender)} changed the weather. {reply}", ChatKind.Admin);
    }

    // ---- /seastate (#299) ---------------------------------------------------------------------

    /// <summary>
    /// Server <c>/seastate</c>: anyone may ask, an admin sets it for everyone. The waves are a
    /// function of the server clock and this number, so it is all that crosses the wire.
    /// </summary>
    private void CommandSeaState(long sender, string arg)
    {
        if (arg.Length == 0)
        {
            ReplyTo(sender, $"The sea is {World.SeaStateCommand.Describe(World.WaterField.SeaState)}.", ChatKind.Private);
            return;
        }
        if (!World.SeaStateCommand.TryParse(arg, out float sea, out string error))
        {
            ReplyTo(sender, error, ChatKind.Error);
            return;
        }
        if (!IsAdmin(sender))
        {
            ReplyTo(sender, "Changing the sea state is an admin command.", ChatKind.Error);
            return;
        }
        World.WaterField.SetSeaState(sea);
        Rpc(MethodName.SeaState, sea);
        GD.Print($"[admin] {NameOf(sender)} set the sea state to {World.SeaStateCommand.Describe(sea)}");
        Broadcast($"{NameOf(sender)} set the sea to {World.SeaStateCommand.Describe(sea)}", ChatKind.Admin);
    }

    /// <summary>Server: tells a newly connected peer the sea state (always: a client may have set its own offline).</summary>
    public void SendSeaStateTo(long peerId) => RpcId(peerId, MethodName.SeaState, World.WaterField.SeaState);

    /// <summary>Client: the server's sea state, on join and on every change.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SeaState(float seaState)
    {
        World.WaterField.SetSeaState(seaState);
        GD.Print($"[water] the server's sea state: {World.SeaStateCommand.Describe(World.WaterField.SeaState)}");
    }

    /// <summary>
    /// Server <c>/speed</c>: the simulation's pace, which carries environment time with it (#579).
    /// Scheduled a moment ahead so every peer flips at the same instant on the server's clock
    /// rather than whenever its RPC happened to land.
    /// </summary>
    private void CommandSpeed(long sender, string arg)
    {
        if (arg.Length == 0)
        {
            ReplyTo(sender, $"The simulation runs at {Core.SimClock.Describe(Core.SimClock.Scale)}.", ChatKind.Private);
            return;
        }
        if (!Core.SimClock.TryParse(arg, out double scale, out string error))
        {
            ReplyTo(sender, error, ChatKind.Error);
            return;
        }
        if (!IsAdmin(sender))
        {
            ReplyTo(sender, "Changing the simulation speed is an admin command.", ChatKind.Error);
            return;
        }

        double applyAt = ClockSync.ServerNow + SpeedApplyDelay;
        Core.SimClock.Schedule(scale, applyAt);
        Rpc(MethodName.WorldSpeed, Core.SimClock.Sim0, Core.SimClock.Epoch, Core.SimClock.Scale, scale, applyAt);
        GD.Print($"[admin] {NameOf(sender)} set the simulation to {Core.SimClock.Describe(scale)}");
        Broadcast($"{NameOf(sender)} set the simulation to {Core.SimClock.Describe(scale)}", ChatKind.Admin);
    }

    /// <summary>
    /// Long enough for the change to reach every peer before it takes effect, short enough that an
    /// admin does not notice the wait. A peer whose RPC arrives after the instant still lands on the
    /// same numbers; it just applies them a frame late (<c>Core.SimClock.Tick</c>).
    /// </summary>
    private const double SpeedApplyDelay = 0.3;

    /// <summary>Server: tells a newly connected peer the simulation's pace, and any change already scheduled.</summary>
    public void SendWorldSpeedTo(long peerId) =>
        RpcId(peerId, MethodName.WorldSpeed, Core.SimClock.Sim0, Core.SimClock.Epoch, Core.SimClock.Scale,
            Core.SimClock.HasPending ? Core.SimClock.PendingScale : Core.SimClock.Scale,
            Core.SimClock.HasPending ? Core.SimClock.PendingAt : 0.0);

    /// <summary>
    /// Client: the simulation's pace (#579). The three numbers in force now, plus a change waiting
    /// for the server's clock to reach <paramref name="pendingAt"/> (0 = none). Environment time is
    /// derived from this, so this one message also moves the sun and the traffic lights.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void WorldSpeed(double sim0, double epoch, double scale, double pendingScale, double pendingAt)
    {
        Core.SimClock.Set(sim0, epoch, scale);
        Engine.TimeScale = Core.SimClock.Scale;
        if (pendingAt > 0) Core.SimClock.Schedule(pendingScale, pendingAt);
        GD.Print($"[speed] the server's simulation: {Core.SimClock.Describe(Core.SimClock.Scale)}"
            + (pendingAt > 0 ? $", going to {Core.SimClock.Describe(pendingScale)}" : ""));
    }

    /// <summary>Server: tells a newly connected peer the world's time.</summary>
    public void SendWorldTimeTo(long peerId)
    {
        if (World.WorldClock.Active)
            RpcId(peerId, MethodName.WorldTime, World.WorldClock.Env0, World.WorldClock.EnvEpoch,
                World.WorldClock.HourShift, World.WorldClock.MinutesPerDay);
    }

    /// <summary>
    /// Client: the world's environment clock (#452, keyed onto the simulation clock in #579): the
    /// environment seconds at a moment of the *simulation* clock, what <c>/time set</c> has turned
    /// the sky by, and the day length. <see cref="World.DayNight"/> reads the hour from it from then
    /// on, so every screen shows the same sky and nothing drifts, and because it is keyed to
    /// simulated time the sun follows a <c>/speed</c> with no message of its own. Kept even without
    /// a sky (<c>--systems</c>): the birds and the dawn chorus read it too.
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void WorldTime(double env0, double envEpoch, double hourShift, float minutesPerDay)
    {
        World.WorldClock.Set(env0, envEpoch, hourShift, minutesPerDay);
        GD.Print($"[time] the server's clock: {World.TimeCommand.Format(World.WorldClock.Hour)} at sim {envEpoch.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} s, {World.TimeCommand.DescribeSpeed(World.WorldClock.MinutesPerDay)}");
    }

    /// <summary>
    /// <c>/spawn &lt;item&gt; [count]</c>: the server decides the sender may and what they get; the item
    /// lands in that client's own inventory, which is local to it (see <see cref="Items.Inventory"/>).
    /// </summary>
    private void CommandSpawn(long sender, string rest)
    {
        if (RefusedInMatch(sender, sender)) return;
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

    // ---- /movespeed (#1485) -------------------------------------------------------------

    /// <summary>
    /// <c>/movespeed [&lt;factor&gt; | normal] [player]</c>, admin: the sender's own movement speed, or a
    /// player's or a server-run bot's; bare, the sender's current factor. No upper cap. Refused for
    /// anyone playing a round, where everyone plays on equal terms.
    /// </summary>
    private void CommandMoveSpeed(long sender, string[] parts)
    {
        long peer = sender;
        string who = "Your";
        bool bot = false;
        if (parts.Length > 2 && string.Join(' ', parts[2..]) is var name && !name.Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            if (_registry?.FindByName(name) is { } player) (peer, who) = (player.PeerId, player.Name + "'s");
            else if (Bots.BotDirector.Instance?.Named(name) is var (botId, botName)) (peer, who, bot) = (botId, botName + "'s", true);
            else
            {
                ReplyTo(sender, $"No player or bot matching '{name}'.", ChatKind.Error);
                return;
            }
        }
        if (peer == sender && !RequiresAvatar(sender, "movespeed")) return;

        if (parts.Length == 1)
        {
            ReplyTo(sender, $"{who} movement speed: {Player.MoveSpeedCommand.Describe(BodyOf(peer)?.SpeedFactor ?? 1f)}.", ChatKind.Private);
            return;
        }
        if (!Player.MoveSpeedCommand.TryParse(parts[1], out float factor, out string error))
        {
            ReplyTo(sender, error, ChatKind.Error);
            return;
        }
        if (RefusedInMatch(sender, peer)) return;
        if (!SetMoveSpeed(peer, factor))
        {
            ReplyTo(sender, "No body to set the speed of.", ChatKind.Error);
            return;
        }
        string said = Player.MoveSpeedCommand.Describe(factor);
        ReplyTo(sender, $"{who} movement speed: {said}.", ChatKind.Admin);
        if (peer != sender && !bot) ReplyTo(peer, $"{NameOf(sender)} set your movement speed: {said}.", ChatKind.Admin);
        GD.Print($"[admin] {NameOf(sender)} set {(peer == sender ? "their" : who)} movement speed to {said}");
    }

    private Player.FootPlayer? BodyOf(long peer) => _players?.GetNodeOrNull<Player.FootPlayer>(PlayerReplication.NodeName(peer));

    /// <summary>
    /// Server (#1485): a body's movement speed factor (<see cref="Player.FootPlayer.SpeedFactor"/>), the path
    /// <c>/movespeed</c> and a bot's brain take. A server-run bot's body moves at it here; a player's body is
    /// its owner's, so the owner is told (<see cref="SpeedFactorSet"/>) and the server keeps it on its copy.
    /// False when there is no such body or player.
    /// </summary>
    public bool SetMoveSpeed(long peer, float factor)
    {
        var body = BodyOf(peer);
        if (body is { Bot: true } or { Npc: true })
        {
            body.SpeedFactor = factor;
            return true;
        }
        if (_registry?.Find(peer) is not { Bodiless: false }) return false;
        if (body != null) body.SpeedFactor = factor;
        RpcId(peer, MethodName.SpeedFactorSet, factor);
        return true;
    }

    /// <summary>Client: the server set this player's movement speed (<c>/movespeed</c>, an admin's choice).</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SpeedFactorSet(float factor) => Player.FootPlayer.SetOwnSpeedFactor(factor);

    /// <summary><c>/give &lt;player&gt; &lt;item&gt; [count]</c>: <c>/spawn</c> into someone else's inventory.</summary>
    private void CommandGive(long sender, string[] parts)
    {
        if (parts.Length < 3)
        {
            ReplyTo(sender, "Usage: /give <player> <item> [count]", ChatKind.Error);
            return;
        }
        if (Target(sender, parts[1]) is not { } target || RefusedInMatch(sender, target.PeerId)) return;
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

    /// <summary>Server: the <c>/transfer</c>s waiting for the giver's inventory, by the giver's peer id (#649).</summary>
    private readonly Dictionary<long, (long To, string ToName, long By)> _transfers = new();

    /// <summary>The most stacks one hand-over may carry: more than any inventory holds.</summary>
    private const int MaxTransferStacks = 128;

    /// <summary>
    /// <c>/transfer &lt;from&gt; &lt;to&gt;</c> (#649): every item and the pocket's cash of one player go to
    /// another. Anyone may give their own; someone else's is an admin's. Inventories live on the
    /// clients, so the giver's hands it over (<see cref="HandOver"/>) and the server passes it on.
    /// </summary>
    private void CommandTransfer(long sender, string[] parts)
    {
        if (parts.Length != 3)
        {
            ReplyTo(sender, "Usage: /transfer <from> <to>  (me for yourself)", ChatKind.Error);
            return;
        }
        if (Target(sender, parts[1]) is not { } from || Target(sender, parts[2]) is not { } to) return;
        if (from.PeerId == to.PeerId)
        {
            ReplyTo(sender, "That is the same player.", ChatKind.Error);
            return;
        }
        if (from.PeerId != sender && !IsAdmin(sender))
        {
            ReplyTo(sender, "Moving someone else's inventory is an admin command.", ChatKind.Error);
            return;
        }
        if (RefusedInMatch(sender, from.PeerId) || RefusedInMatch(sender, to.PeerId)) return;
        if (_transfers.ContainsKey(from.PeerId))
        {
            ReplyTo(sender, $"{from.Name}'s inventory is already on its way.", ChatKind.Error);
            return;
        }

        _transfers[from.PeerId] = (to.PeerId, to.Name, sender);
        RpcId(from.PeerId, MethodName.HandOver, from.PeerId == sender ? "" : NameOf(sender), to.Name);
        GD.Print($"[transfer] {NameOf(sender)} moves {from.Name}'s inventory to {to.Name}");
    }

    /// <summary>Server: the giver's inventory, for the <c>/transfer</c> it was asked for; on to the receiver.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void HandedOver(int[] ids, int[] counts, string[] data, int cash)
    {
        long from = Multiplayer.GetRemoteSenderId();
        // only an answer to a transfer the server asked for
        if (!_transfers.Remove(from, out var t)) return;
        int n = Math.Min(Math.Min(ids.Length, counts.Length), Math.Min(data.Length, MaxTransferStacks));
        ids = ids[..n];
        counts = counts[..n].Select(c => Math.Clamp(c, 0, Items.ItemLookup.MaxSpawnFrancs)).ToArray();
        data = data[..n].Select(d => d.Length > 256 ? "" : d).ToArray();
        cash = Math.Clamp(cash, 0, MaxCashGrant);
        string fromName = NameOf(from);

        // the receiver left meanwhile: it all goes back
        bool there = _registry?.Find(t.To) != null;
        long dest = there ? t.To : from;
        RpcId(dest, MethodName.ReceiveInventory, ids, counts, data, cash, there ? fromName : t.ToName + " left, so your own");
        string what = $"{n} stack(s) and {cash} CHF";
        if (t.By != from && t.By != dest)
            ReplyTo(t.By, there ? $"Moved {fromName}'s inventory to {t.ToName}: {what}." : $"{t.ToName} left; {fromName} got the inventory back.",
                there ? ChatKind.Admin : ChatKind.Error);
        GD.Print($"[transfer] {fromName} -> {(there ? t.ToName : fromName + " (back)")}: {what}");
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
            if (Target(sender, name) is not { } target || RefusedInMatch(sender, target.PeerId)) return;
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
        if (RefusedInMatch(sender, peer)) return;

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

    /// <summary>
    /// <c>/w &lt;player&gt; &lt;text&gt;</c> (also <c>/whisper</c>, <c>/tell</c>, <c>/msg</c>) and
    /// <c>/r &lt;text&gt;</c> for the last person whispered with. Only the two see it; a muted player
    /// may not whisper, since that would be a way round the mute.
    /// </summary>
    private void CommandWhisper(long sender, string rest, bool reply)
    {
        if (_registry is null) return;
        if (sender == ConsolePeerId)
        {
            ReplyTo(sender, "Whispering needs a player.", ChatKind.Error);
            return;
        }
        if (Muted(sender, out double muted))
        {
            ReplyTo(sender, $"You are muted for another {Math.Ceiling(muted / 60):0} min.", ChatKind.Error);
            return;
        }

        PlayerInfo? target;
        string text;
        if (reply)
        {
            text = rest;
            target = _lastWhisper.TryGetValue(sender, out long other) ? _registry.Find(other) : null;
            if (target == null)
            {
                ReplyTo(sender, "Nobody to reply to: /w <player> <text> starts a whisper.", ChatKind.Error);
                return;
            }
        }
        else
        {
            string[] split = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length < 2)
            {
                ReplyTo(sender, "Usage: /w <player> <text>", ChatKind.Error);
                return;
            }
            if (_registry.FindByName(split[0]) is not { } found)
            {
                ReplyTo(sender, $"No player matching '{split[0]}'.", ChatKind.Error);
                return;
            }
            (target, text) = (found, split[1]);
        }

        if (target.PeerId == sender)
        {
            ReplyTo(sender, "You are already there.", ChatKind.Error);
            return;
        }
        text = ClampText(Scrub(text).Trim());
        if (text.Length == 0)
        {
            ReplyTo(sender, reply ? "Usage: /r <text>" : "Usage: /w <player> <text>", ChatKind.Error);
            return;
        }

        ReplyTo(target.PeerId, $"{NameOf(sender)} whispers: {text}", ChatKind.Whisper);
        ReplyTo(sender, $"You whisper to {target.Name}: {text}", ChatKind.Whisper);
        _lastWhisper[sender] = target.PeerId;
        _lastWhisper[target.PeerId] = sender;
    }

    /// <summary>
    /// <c>/report &lt;player&gt; [reason]</c>: writes what happened, who says so and the last lines of
    /// chat to <c>user://chat-reports.log</c>, so an operator has context later. Anyone may report;
    /// one report every <see cref="ReportCooldownSeconds"/> seconds.
    /// </summary>
    private void CommandReport(long sender, string rest)
    {
        if (_registry is null) return;
        if (sender == ConsolePeerId)
        {
            ReplyTo(sender, "Reports need a player.", ChatKind.Error);
            return;
        }
        string[] split = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (split.Length == 0)
        {
            ReplyTo(sender, "Usage: /report <player> [what happened]", ChatKind.Error);
            return;
        }
        if (_registry.FindByName(split[0]) is not { } target)
        {
            ReplyTo(sender, $"No player matching '{split[0]}'.", ChatKind.Error);
            return;
        }

        double now = Core.RealClock.Now;
        if (_reportedAt.TryGetValue(sender, out double at) && now - at < ReportCooldownSeconds)
        {
            ReplyTo(sender, $"You reported someone {now - at:0} s ago; wait {ReportCooldownSeconds - (now - at):0} s.", ChatKind.Error);
            return;
        }
        _reportedAt[sender] = now;

        string reason = split.Length > 1 ? ClampText(Scrub(split[1]).Trim(), 200) : "";
        string when = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        try
        {
            using var file = Godot.FileAccess.Open("user://chat-reports.log", Godot.FileAccess.ModeFlags.ReadWrite);
            if (file != null)
            {
                file.SeekEnd();
                file.StoreString($"==== {when} ====\n"
                    + $"reporter: {NameOf(sender)} (peer {sender})\n"
                    + $"target: {target.Name} (peer {target.PeerId})\n"
                    + $"reason: {(reason.Length == 0 ? "(no reason given)" : reason)}\n"
                    + "recent chat:\n");
                foreach (var (stamp, line) in _recent)
                    file.StoreString($"  {stamp}  {line}\n");
                file.StoreString("\n");
            }
        }
        catch (System.Exception e)
        {
            GD.PushError($"[report] cannot write chat-reports.log: {e.Message}");
        }

        GD.Print($"[report] {NameOf(sender)} reported {target.Name}: {(reason.Length == 0 ? "(no reason)" : reason)}");
        ReplyTo(sender, $"Thank you: your report about {target.Name} was written down for the operators.", ChatKind.Private);
    }

    /// <summary>
    /// <c>/mute &lt;player&gt; [minutes]</c>, an operator's: stops a player's chat, <c>/me</c> and
    /// whispers for a while (10 minutes by default). The player is told, and everyone sees why.
    /// </summary>
    private void CommandMute(long sender, string[] parts)
    {
        if (_registry is null || parts.Length < 2)
        {
            ReplyTo(sender, "Usage: /mute <player> [minutes]", ChatKind.Error);
            return;
        }
        if (_registry.FindByName(parts[1]) is not { } target)
        {
            ReplyTo(sender, $"No player matching '{parts[1]}'.", ChatKind.Error);
            return;
        }
        if (target.IsAdmin)
        {
            ReplyTo(sender, $"{target.Name} is an operator; revoke that first.", ChatKind.Error);
            return;
        }
        int minutes = 10;
        if (parts.Length > 2 && (!int.TryParse(parts[2], out minutes) || minutes <= 0))
        {
            ReplyTo(sender, "Usage: /mute <player> [minutes]", ChatKind.Error);
            return;
        }
        minutes = Math.Clamp(minutes, 1, 1440);

        _muted[target.PeerId] = (NameOf(sender), Core.RealClock.Now + minutes * 60);
        ReplyTo(target.PeerId, $"{NameOf(sender)} muted you for {minutes} min.", ChatKind.Admin);
        Broadcast($"{target.Name} was muted by {NameOf(sender)} for {minutes} min.", ChatKind.Admin);
        GD.Print($"[admin] {NameOf(sender)} muted {target.Name} for {minutes} min");
    }

    /// <summary><c>/unmute &lt;player&gt;</c>, an operator's: lets a muted player speak again.</summary>
    private void CommandUnmute(long sender, string[] parts)
    {
        if (_registry is null || parts.Length < 2)
        {
            ReplyTo(sender, "Usage: /unmute <player>", ChatKind.Error);
            return;
        }
        if (_registry.FindByName(parts[1]) is not { } target)
        {
            ReplyTo(sender, $"No player matching '{parts[1]}'.", ChatKind.Error);
            return;
        }
        if (!_muted.Remove(target.PeerId))
        {
            ReplyTo(sender, $"{target.Name} is not muted.", ChatKind.Private);
            return;
        }
        ReplyTo(target.PeerId, $"{NameOf(sender)} unmuted you.", ChatKind.Admin);
        Broadcast($"{target.Name} was unmuted by {NameOf(sender)}.", ChatKind.Admin);
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

        KickPeer(target.PeerId, reason);
        Broadcast($"{target.Name} was kicked by {NameOf(sender)} ({reason})", ChatKind.Admin);
    }

    private readonly ServerUpdater _updater = new();
    private bool _restarting;

    /// <summary>Server: the release updater, shared by <c>/update</c> and the automatic check (<see cref="ServerAutoUpdate"/>).</summary>
    public ServerUpdater Updater => _updater;

    /// <summary>Server: <c>/update</c> came while a check was already running: restart as soon as it has a release.</summary>
    public bool RestartRequested { get; set; }

    /// <summary>Server: the connected players who are admins, for the update news (#1088).</summary>
    public IEnumerable<long> AdminPeers => _registry?.Players.Where(p => p.IsAdmin).Select(p => p.PeerId) ?? [];

    /// <summary>Server: how many players are connected (bots are not players).</summary>
    public int PlayerCount => _registry?.InWorld.Count() ?? 0;

    /// <summary>
    /// /update (#730): installs the newest GitHub release on a server started by <c>start-server.sh</c>.
    /// The download runs while everyone plays; once it is unpacked, <see cref="RestartForUpdate"/>.
    /// </summary>
    private void CommandUpdate(long sender)
    {
        if (ServerUpdater.Script is not { } script)
        {
            ReplyTo(sender, "This server cannot update itself: only one started by tools/deploy-linux.sh can.", ChatKind.Error);
            return;
        }
        if (_restarting)
        {
            ReplyTo(sender, "The server is already restarting for an update.", ChatKind.Error);
            return;
        }
        if (_updater.Busy)
        {
            // the automatic check (#1088) is looking or downloading: it restarts the server once it has the release
            RestartRequested = true;
            ReplyTo(sender, "A check is already under way: the server restarts as soon as the release is ready.", ChatKind.Private);
            return;
        }

        string current = ServerUpdater.RunningVersion;
        ReplyTo(sender, "Looking for a newer release...", ChatKind.Private);
        _updater.Start(script, current, CmdArgs.Value("--updatefeed", notFlag: true) ?? UpdateInfo.ReleasesUrl, quiet: false,
            (line, everyone) =>
            {
                if (everyone) Broadcast(line, ChatKind.Admin);
                else if (sender == ConsolePeerId || _registry?.Find(sender) != null) ReplyTo(sender, line, ChatKind.Private);
            },
            tag => { if (tag != null) RestartForUpdate(tag); });
    }

    /// <summary>The new release is unpacked: everyone out, then quit; <c>start-server.sh</c> starts it.</summary>
    public void RestartForUpdate(string tag)
    {
        if (_restarting) return;
        _restarting = true;
        GD.Print($"[update] {tag} ready: kicking everyone and quitting");
        foreach (var player in _registry?.Players.ToList() ?? new())
            KickPeer(player.PeerId, $"The server is updating to {tag}. Update your game, then reconnect in a minute.");
        // the kicks need their 0.2 s to land; the disconnects then save every sleeper
        GetTree().CreateTimer(2).Timeout += () => GetTree().Quit(0);
    }

    /// <summary>
    /// Server: tells a peer why, then disconnects it. Also how a client too old for the version
    /// check is turned away (<see cref="Handshake"/>): <c>NotifyKicked</c> is in every version, so
    /// this node's RPCs must not change either, or an old client is shown nothing.
    /// </summary>
    public void KickPeer(long peerId, string reason)
    {
        // a player at the server console reads it through the link: its game may lack this node's RPCs
        if (IsBodiless(peerId)) RemoteLine?.Invoke(peerId, $"Disconnected by the server: {reason}");
        else RpcId(peerId, MethodName.NotifyKicked, reason);
        // Give the notification a moment to reach them before the socket closes under it.
        GetTree().CreateTimer(0.2).Timeout += () =>
        {
            if (Multiplayer.MultiplayerPeer is ENetMultiplayerPeer peer)
                peer.DisconnectPeer((int)peerId);
        };
    }
}
