using Godot;
using UnitSport.Net;

namespace UnitSport.Interiors.Games;

/// <summary>
/// Every venue's playable pieces but bowling and pinball (#1324, #1326): pool tables, dartboards, table football,
/// air hockey, claw machines, cabinets. A node at <c>World/TableGames</c> on the server and on every client (RPCs
/// go by node path).
///
/// <para>
/// <b>The server owns each game</b> (<see cref="TableGame"/>, keyed by the venue's plan key and the piece's
/// furniture index): who sits where, whose turn, the score, the balls. Every action is a <c>Serve*</c> method
/// taking the peer, which the RPC only forwards and a bot calls with its own id. A change goes, packed, to the
/// peers inside the venue (<see cref="InteriorManager.SpaceOf"/>); a client asks for the venue's games on walking
/// in and forgets them on leaving. A live game is stepped here at 120 Hz while it is awake, its seats' inputs
/// arriving 30 times a second, its poses sent out 20 times a second.
/// </para>
/// <para>
/// <b>Bots</b> (<see cref="BotBase"/> and below) are seats with no body and no client: a player alone adds one as an
/// opponent; the server plays it through the game's <see cref="TableGame.Bot"/>, and it leaves with the last human.
/// </para>
/// </summary>
public partial class TableGames : Node
{
    public const string NodeName = "TableGames";
    /// <summary>Bot seats' peer ids: this and below (bowling's partners are -900 000 and down, a few at most).</summary>
    public const long BotBase = -2_000_000;
    /// <summary>A live game's server step [s], how often its poses go out, how often a client sends its controls.</summary>
    public const double LiveStep = 1.0 / 120, FrameEvery = 0.05, InputEvery = 1.0 / 30;

    private static readonly string[] BotNames = { "Ueli", "Vreni", "Housi", "Mägi", "Fritz", "Rösli", "Sepp", "Trudi", "Beat", "Heidi" };

    public static TableGames? Instance { get; private set; }

    /// <summary>A refusal or a word for this player ("wait your turn", "you win"), for the HUD.</summary>
    public event Action<string>? Told;

    private readonly Dictionary<(string Key, int Index), TableGame> _games = new();
    private readonly Random _rng = new();
    private long _nextBot;
    private double _housekeeping, _liveAcc, _frameAt;
    private string _asked = "";

    private bool Online => NetLink.Online(this);
    private bool Headless => DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase);
    private bool IsServer => !Online || Multiplayer.IsServer();
    public long MyId => Multiplayer.MultiplayerPeer is { } p and not OfflineMultiplayerPeer ? Multiplayer.GetUniqueId() : 1;
    private static double Clock => ClockSync.SimNow;

    public static TableGames Create(Node world)
    {
        var node = new TableGames { Name = NodeName };
        world.AddChild(node);
        Instance = node;
        return node;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public static bool IsBot(long peer) => peer <= BotBase;

    /// <summary>A piece's frame in its plan: turned, on its floor.</summary>
    public static Transform3D PieceFrame(InteriorLayout l, FurniturePlan f) =>
        new(new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2), new Vector3(f.X, l.FloorY(f.Floor) + f.Lift, f.Z));

    /// <summary>The game at (<paramref name="key"/>, <paramref name="index"/>) as this peer knows it.</summary>
    public TableGame? GameAt(string key, int index) => _games.GetValueOrDefault((key, index));

    /// <summary>The games of venue <paramref name="key"/> this peer knows (the check's view).</summary>
    public IEnumerable<(int Index, TableGame Game)> GamesIn(string key)
    {
        foreach (var ((k, i), g) in _games)
            if (k == key) yield return (i, g);
    }

    /// <summary>The name a seat shows: a bot's own, else the chat name.</summary>
    public string NameOf(long peer)
    {
        if (peer == 0) return "";
        if (IsBot(peer)) return BotNames[(int)((BotBase - peer) % BotNames.Length)] + " (bot)";
        return GetParent()?.GetNodeOrNull<ChatManager>(ChatManager.NodeName)?.NameOfPeer(peer) ?? $"Player {peer}";
    }

    // ---- the actions (server, and offline) -----------------------------------------------------

    /// <summary>
    /// <paramref name="peer"/> takes a seat at piece <paramref name="index"/> of venue <paramref name="key"/> (the one
    /// wanted, or the first free); again at a seat they hold, nothing. The game starts when it has its players.
    /// </summary>
    public bool ServeSit(long peer, string key, int index, int wanted = -1)
    {
        if (Piece(peer, key, index) is not { } at) return false;
        var g = SessionOf(key, index, at.Spec);
        if (g.SeatOf(peer) >= 0) return true;
        if (!IsBot(peer) && !Near(peer, at.Plan, index, at.Spec)) return Refuse(peer, $"Step up to the {at.Spec.Title.ToLowerInvariant()}.");
        // a seat held by a bot goes to a human who asks for it
        int seat = g.FreeSeat(wanted);
        if (seat < 0 && wanted >= 0 && wanted < g.Seats.Length && IsBot(g.Seats[wanted])) { g.Stand(g.Seats[wanted]); seat = wanted; }
        if (seat < 0)
            for (int i = 0; i < g.Seats.Length && seat < 0; i++)
                if (IsBot(g.Seats[i])) { g.Stand(g.Seats[i]); seat = i; }
        if (seat < 0) return Refuse(peer, "Every place here is taken.");
        if (g.Sit(peer, seat) is { } why) return Refuse(peer, why);
        GD.Print($"[games] {NameOf(peer)} sits at {at.Spec.Title} {key} #{index}, seat {seat}");
        Changed(key, index, g);
        return true;
    }

    /// <summary><paramref name="peer"/> leaves their seat; their bots leave with the last human.</summary>
    public bool ServeLeave(long peer, string key, int index)
    {
        if (!_games.TryGetValue((key, index), out var g) || g.SeatOf(peer) < 0) return false;
        g.Stand(peer);
        if (!g.AnyHuman)
            foreach (long s in g.Seats.ToArray())
                if (IsBot(s)) g.Stand(s);
        GD.Print($"[games] {NameOf(peer)} leaves {key} #{index}");
        Changed(key, index, g);
        return true;
    }

    /// <summary><paramref name="peer"/>, seated, asks for a bot in a free seat: an opponent with a skill spread.</summary>
    public bool ServeAddBot(long peer, string key, int index)
    {
        if (!_games.TryGetValue((key, index), out var g) || g.SeatOf(peer) < 0) return Refuse(peer, "Take a place first.");
        if (!g.Bots) return Refuse(peer, "No bots here.");
        int seat = g.FreeSeat();
        if (seat < 0) return Refuse(peer, "Every place here is taken.");
        long bot = BotBase - _nextBot++;
        if (g.Sit(bot, seat) is { } why) return Refuse(peer, why);
        GD.Print($"[games] {NameOf(peer)} adds {NameOf(bot)} at {key} #{index}, seat {seat}");
        Changed(key, index, g);
        return true;
    }

    /// <summary>
    /// <paramref name="peer"/> does a game's action (<see cref="TableGame.Act"/>: a shot, a throw, a coin) at piece
    /// <paramref name="index"/>. A bot calls this with its own id, with what its <see cref="TableGame.Bot"/> chose.
    /// </summary>
    public bool ServeAct(long peer, string key, int index, int action, int[] args)
    {
        if (!_games.TryGetValue((key, index), out var g)) return false;
        int seat = g.SeatOf(peer);
        if (seat < 0) return Refuse(peer, "Take a place first.");
        if (!IsBot(peer) && (InteriorManager.Instance?.PlanOf(key) is not { } l || GameCatalog.Of(g.Kind) is not { } spec || !Near(peer, l, index, spec)))
            return Refuse(peer, "Step up to it first.");
        if (g.Act(seat, action, args, Clock, _rng) is { } why) return Refuse(peer, why);
        Changed(key, index, g);
        return true;
    }

    /// <summary>A live game's controls from a seated peer: kept until the next arrive.</summary>
    public void ServeInput(long peer, string key, int index, byte[] input)
    {
        if (!_games.TryGetValue((key, index), out var g) || !g.Live) return;
        int seat = g.SeatOf(peer);
        if (seat >= 0) g.Input(seat, input);
    }

    // ---- the server's loop: live games stepped, bots played, the gone unseated -----------------

    public override void _Process(double delta)
    {
        if (IsServer) ServerProcess(delta);
        ClientProcess(delta);
    }

    private void ServerProcess(double delta)
    {
        double now = Clock;
        _liveAcc = Math.Min(_liveAcc + delta, 0.1);
        bool frame = (_frameAt -= delta) <= 0;
        if (frame) _frameAt = FrameEvery;
        while (_liveAcc >= LiveStep)
        {
            _liveAcc -= LiveStep;
            foreach (var ((key, index), g) in _games)
                if (g.Live && g.Awake && g.Step(LiveStep, now)) Changed(key, index, g);
        }
        if (frame)
            foreach (var ((key, index), g) in _games)
                if (g.Live && g.Awake) SendFrame(key, index, g);

        _housekeeping -= delta;
        if (_housekeeping > 0) return;
        _housekeeping = 0.1;
        List<(string, int, long)>? gone = null;
        List<(string, int, long, int, int[])>? moves = null;
        foreach (var ((key, index), g) in _games)
            for (int s = 0; s < g.Seats.Length; s++)
            {
                long peer = g.Seats[s];
                if (peer == 0) continue;
                if (!IsBot(peer))
                {
                    if (!Present(peer, key)) (gone ??= new()).Add((key, index, peer));
                    continue;
                }
                if (g.Bot(s, now, _rng) is { } move) (moves ??= new()).Add((key, index, peer, move.Action, move.Args));
            }
        if (gone != null)
            foreach (var (key, index, peer) in gone) ServeLeave(peer, key, index);
        if (moves != null)
            foreach (var (key, index, peer, action, args) in moves) ServeAct(peer, key, index, action, args);
    }

    // ---- where people stand --------------------------------------------------------------------

    /// <summary>The venue's plan and the piece's spec, if <paramref name="peer"/> is inside (bots are where their game is).</summary>
    private (InteriorLayout Plan, GameSpec Spec)? Piece(long peer, string key, int index)
    {
        if (InteriorManager.Instance is not { } im || im.PlanOf(key) is not { } l || index < 0 || index >= l.Furniture.Count) return null;
        if (GameCatalog.Of(l.Furniture[index].Type) is not { } spec) return null;
        if (Online && !IsBot(peer) && im.SpaceOf(peer) != key) { GD.Print($"[games] peer {peer} is not in {key}: refused"); return null; }
        return (l, spec);
    }

    private TableGame SessionOf(string key, int index, GameSpec spec)
    {
        if (!_games.TryGetValue((key, index), out var g) || g.Kind != spec.Kind) _games[(key, index)] = g = spec.Make();
        return g;
    }

    private bool Near(long peer, InteriorLayout l, int index, GameSpec spec)
    {
        if (InteriorManager.Instance?.BodyLocal(peer, l) is not { } at) return !Online;
        // interiors stand under the world: offline a body outside the building is no body to judge by
        if (!Online && Mathf.Abs(at.Y) > 50f) return true;
        var f = l.Furniture[index];
        return spec.UseFrom(PieceFrame(l, f).AffineInverse() * at, f);
    }

    private bool Present(long peer, string key) =>
        !Online ? peer == MyId : peer == MyId || Multiplayer.GetPeers().Contains((int)peer) && InteriorManager.Instance?.SpaceOf(peer) == key;

    private bool Refuse(long peer, string why)
    {
        GD.Print($"[games] {NameOf(peer)}: {why}");
        Tell(peer, why);
        return false;
    }

    /// <summary>A word for one player (a refusal, a win): on their HUD.</summary>
    public void Tell(long peer, string text)
    {
        if (IsBot(peer)) return;
        if (peer == MyId) Told?.Invoke(text);
        else if (Online && peer > 0) RpcId(peer, MethodName.Hear, text);
    }

    // ---- sending -------------------------------------------------------------------------------

    private IEnumerable<long> PeersInside(string key)
    {
        if (InteriorManager.Instance is not { } im || !Online) yield break;
        foreach (int peer in Multiplayer.GetPeers())
            if (im.SpaceOf(peer) == key) yield return peer;
    }

    /// <summary>A game changed: packed to everyone inside; here, its rig told.</summary>
    private void Changed(string key, int index, TableGame g)
    {
        if (Online)
        {
            var bytes = g.Pack();
            foreach (long peer in PeersInside(key))
                if (peer != MyId) RpcId(peer, MethodName.State, key, index, (byte)g.Kind, bytes);
        }
        if (!Headless) RigOf(key, index)?.OnState(g, Clock);
    }

    private void SendFrame(string key, int index, TableGame g)
    {
        if (!Online) return;
        byte[]? bytes = null;
        foreach (long peer in PeersInside(key))
            if (peer != MyId) RpcId(peer, MethodName.Frame, key, index, bytes ??= g.PackPoses());
    }

    private static TableRig? RigOf(string key, int index) => InteriorManager.Instance?.BuiltNode(key)?.TableRigOf(index);

    // ---- a client's side -----------------------------------------------------------------------

    public void AskSit(string key, int index, int seat = -1)
    {
        if (!IsServer) RpcId(1, MethodName.RequestSit, key, index, seat);
        else ServeSit(MyId, key, index, seat);
    }

    public void AskLeave(string key, int index)
    {
        if (!IsServer) RpcId(1, MethodName.RequestLeave, key, index);
        else ServeLeave(MyId, key, index);
    }

    public void AskBot(string key, int index)
    {
        if (!IsServer) RpcId(1, MethodName.RequestBot, key, index);
        else ServeAddBot(MyId, key, index);
    }

    public void AskAct(string key, int index, int action, params int[] args)
    {
        if (!IsServer) RpcId(1, MethodName.RequestAct, key, index, action, args);
        else ServeAct(MyId, key, index, action, args);
    }

    public void SendInput(string key, int index, byte[] input)
    {
        if (!IsServer) RpcId(1, MethodName.RequestInput, key, index, input);
        else ServeInput(MyId, key, index, input);
    }

    private void ClientProcess(double delta)
    {
        if (Headless && IsServer) return;
        var im = InteriorManager.Instance;
        if (im?.Current is not { } here)
        {
            if (_asked.Length > 0 && !IsServer) _games.Clear();
            _asked = "";
            return;
        }
        if (_asked != here.Key)
        {
            if (!IsServer)
            {
                _games.Clear();
                RpcId(1, MethodName.RequestState, here.Key);
            }
            _asked = here.Key;
        }
        if (im.BuiltNode(here.Key) is not { } node) return;
        double now = Clock;
        foreach (var (index, rig) in node.TableRigs)
            rig.Show(_games.GetValueOrDefault((here.Key, index)), now, delta);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void State(string key, int index, byte kind, byte[] bytes)
    {
        if (GameCatalog.Of((GameKind)kind) is not { } spec) return;
        if (!_games.TryGetValue((key, index), out var g) || g.Kind != spec.Kind) _games[(key, index)] = g = spec.Make();
        g.Unpack(bytes);
        RigOf(key, index)?.OnState(g, Clock);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void Frame(string key, int index, byte[] poses)
    {
        if (_games.TryGetValue((key, index), out var g)) g.UnpackPoses(poses);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Hear(string text) => Told?.Invoke(text);

    // ---- client RPC entry points ---------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestSit(string key, int index, int seat)
    {
        if (Multiplayer.IsServer()) ServeSit(Multiplayer.GetRemoteSenderId(), key, index, seat);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestLeave(string key, int index)
    {
        if (Multiplayer.IsServer()) ServeLeave(Multiplayer.GetRemoteSenderId(), key, index);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestBot(string key, int index)
    {
        if (Multiplayer.IsServer()) ServeAddBot(Multiplayer.GetRemoteSenderId(), key, index);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestAct(string key, int index, int action, int[] args)
    {
        if (Multiplayer.IsServer()) ServeAct(Multiplayer.GetRemoteSenderId(), key, index, action, args);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void RequestInput(string key, int index, byte[] input)
    {
        if (Multiplayer.IsServer()) ServeInput(Multiplayer.GetRemoteSenderId(), key, index, input);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestState(string key)
    {
        if (!Multiplayer.IsServer()) return;
        long peer = Multiplayer.GetRemoteSenderId();
        foreach (var ((k, index), g) in _games)
            if (k == key) RpcId(peer, MethodName.State, key, index, (byte)g.Kind, g.Pack());
    }
}
