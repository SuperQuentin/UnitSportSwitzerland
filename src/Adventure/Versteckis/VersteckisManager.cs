using System.Text.Json;
using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Versteckis;

/// <summary>
/// Versteckis (#1268): hide-and-seek as village props, at <c>World/Versteckis</c> on the server and every client (RPCs
/// route by path). Next to the Battle Royale, not inside it: a server runs up to four games at once, each in its own
/// village 600 m from the others, each a <see cref="VsState"/> the server owns and sends whole on every change. The
/// server decides roles, catches, points, objectives and bounds; transforms stay the owners' and the server checks
/// what they publish. Rounds go through <see cref="Rounds"/> (the Adventure layer is off for an entrant), the client
/// is lent a round kit as BR lends its pack, and every human who played is paid the same way.
/// Docs: <c>docs/notes/adventure/versteckis.md</c>; plan <c>docs/plans/adventure-versteckis.md</c>.
/// </summary>
public partial class VersteckisManager : Node
{
    public const string NodeName = "Versteckis";
    /// <summary>The invite kind: to a game's lobby, keyed by its id.</summary>
    public const string InviteId = "versteckis";
    /// <summary>The <see cref="Rounds"/> mode's name.</summary>
    public const string ModeName = "versteckis";
    private const string HistoryFile = "user://versteckis/history.json";

    private bool _server;
    private WorldOrigin _origin = null!;

    /// <summary>The server's node (for the bots' brains and the checks), null on a client.</summary>
    public static VersteckisManager? Server { get; private set; }

    // ---- server ----------------------------------------------------------------------------
    private ChatManager? _chat;
    private Node3D? _players;
    private Func<Terrain.IChunkSource?> _source = () => null;
    private IReadOnlyList<VsTown> _towns = Array.Empty<VsTown>();
    private readonly List<Game> _games = new();
    private int _nextId = 1;
    private readonly Random _rng = new();

    /// <summary>A game on the server: its state, its area once read, and what the server keeps of each entrant.</summary>
    private sealed class Game
    {
        public VsState S = new();
        public VsGround? Site;
        public Task<VsGround>? Loading;
        /// <summary>Candidates still to try when the area is rejected (a nearest pick).</summary>
        public Queue<VsCandidate> Next = new();
        public VsCandidate? Candidate;
        public int? AskedRounds;
        public List<long> Order = new();
        public readonly Dictionary<long, Run> Runs = new();
        public double NextCheck;
        public int NextDecoy = 1;
        public bool Released;
        public Node3D? Solid;
        public Run RunOf(long peer) => Runs.TryGetValue(peer, out var r) ? r : Runs[peer] = new Run();
    }

    /// <summary>What the server keeps of an entrant beyond the state: its last checked position, the clocks of its holds and checks.</summary>
    private sealed class Run
    {
        public GlobalPos? Last;
        public double LastAt;
        public double SpeedBadUntil;
        public double OutSince = -1;
        public double HoldFrom = -1;
        public int HoldSite = -1;
        public double CarryMetres;
        public double TagReadyAt;
        public double SprintUntil;
        public double HiddenSince;
        public double LastAsk;
        public int Asks;
    }

    public static VersteckisManager CreateServer(ChatManager chat, Node3D players, WorldOrigin origin, Func<Terrain.IChunkSource?> source,
        IReadOnlyList<Place> places)
    {
        var m = new VersteckisManager { Name = NodeName, _server = true, _chat = chat, _players = players, _origin = origin, _source = source };
        var towns = places.Where(p => p.Kind == PlaceKind.Town).Select(p => new VsTown(p.Name, p.E, p.N, p.Buildings)).ToList();
        // a fixture course has no places.json: its hamlet is a town of its own at the start (the fixture chapel is near)
        if (Systems.FixtureCourse != null) towns.Add(new VsTown("Fixture hamlet", PoiCatalogue.FixtureE, PoiCatalogue.FixtureN, 20));
        m._towns = towns;
        chat.Invites?.Register(new InviteKind
        {
            Id = InviteId, Label = "Versteckis", Seconds = 120, TellAccepted = false,
            Accept = (peer, invite) => m.Join(peer, (int)invite.Key),
        });
        return m;
    }

    public override void _EnterTree()
    {
        if (_server)
        {
            Server = this;
            // one "in a round" registry (#1268): the Battle Royale registered first and answers first
            Rounds.Register(new Rounds.Mode(ModeName, "a game of Versteckis", InRound, Playing)
            {
                NoTeleport = "No teleporting out of a game of Versteckis. /vs leave to give up.",
                ChatScope = ChatScope,
            });
            AdventureService.Instance?.Provides("versteckis");
        }
        else Instance = this;
        FootPlayer.PropWalk = PropWalkOf;
    }

    public override void _ExitTree()
    {
        if (_server)
        {
            // a restart ends every game: each player is paid for the rounds already played
            foreach (var g in _games.ToList()) Close(g, "The server is stopping.");
            Rounds.Unregister(ModeName);
            if (Server == this) Server = null;
        }
        if (Instance == this) Instance = null;
        if (Server == null && Instance == null) FootPlayer.PropWalk = null;
        ClientExit();
    }

    /// <summary>Players a game without bots needs to start: four, or <c>--vsmin n</c> on the server (the two-client check).</summary>
    private static readonly int MinNoBots = CmdArgs.Int("--vsmin") is int m && m >= VsRules.MinPlayers ? m : VsRules.MinPlayersNoBots;

    /// <summary>Game time: <see cref="ClockSync.SimNow"/>, so <c>/speed</c> and <c>--checkspeed</c> pace a game.</summary>
    private static double Now => ClockSync.SimNow;

    public override void _Process(double delta)
    {
        if (_server) ServerTick();
        else ClientTick(delta);
    }

    // ==== server: the registry's questions =====================================================

    private Game? GameOf(long peer)
    {
        foreach (var g in _games)
            if (g.S.Find(peer) is { Left: false }) return g;
        return null;
    }

    /// <summary>Server: an entrant of a lobby or a game, until it is released (the Adventure layer is off for it).</summary>
    public bool InRound(long peer) => GameOf(peer) != null;

    /// <summary>Server: playing a round now (Gather to RoundEnd): no teleporting, no admin tools on it.</summary>
    public bool Playing(long peer) => GameOf(peer) is { } g && g.S.Running;

    /// <summary>Server: a caught hider talks to its game's spectators only while a round runs (nobody calls out spots).</summary>
    private string? ChatScope(long peer) =>
        GameOf(peer) is { S: { Phase: VsPhase.Hide or VsPhase.Seek } s } g && s.Find(peer) is { Caught: true } ? $"vs:{s.Id}" : null;

    /// <summary>Server: the peers that hear a scoped line (the game's caught hiders and its seeker... the spectators).</summary>
    public IEnumerable<long> ScopeMembers(string scope)
    {
        foreach (var g in _games)
            if ($"vs:{g.S.Id}" == scope)
                foreach (var e in g.S.Entrants)
                    if (!e.Bot && !e.Left && e.Caught) yield return e.Peer;
    }

    /// <summary>Server: two peers the interest service shows each other (everyone in a game's Seek and RoundEnd: the area is under 450 m).</summary>
    public bool Together(long a, long b) =>
        GameOf(a) is { S.Phase: VsPhase.Seek or VsPhase.RoundEnd or VsPhase.Gather } g && g.S.Find(b) is { Left: false };

    /// <summary>
    /// Server: <paramref name="target"/> is withheld from <paramref name="viewer"/> (#1268, <c>InterestService.Withheld</c>): in
    /// Hide every hider's body is despawned on its game's seeker, so a hacked client that skips the black screen sees an
    /// empty village.
    /// </summary>
    public bool Withheld(long viewer, long target)
    {
        foreach (var g in _games)
            if (g.S.Phase == VsPhase.Hide && g.S.Seeker == viewer && g.S.Find(target) is { Role: VsRole.Hider, Left: false })
            {
                if (_withheldLogged.Add((g.S.Id, g.S.Round, target)))
                    GD.Print($"[vs] withheld {Who(g.S, target)} from the seeker {Who(g.S, viewer)} (game {g.S.Id}, round {g.S.Round})");
                return true;
            }
        return false;
    }

    private readonly HashSet<(int, int, long)> _withheldLogged = new();

    private static string Who(VsState s, long peer) => s.Find(peer)?.Name ?? peer.ToString();

    // ==== server: the console (/vs …), the same path the Activities buttons use ================

    /// <summary><c>/vs …</c> from <paramref name="sender"/>: the private reply.</summary>
    public string Command(long sender, string args, bool admin)
    {
        var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = words.Length > 0 ? words[0].ToLowerInvariant() : "list";
        bool player = sender != ChatManager.ConsolePeerId;
        switch (verb)
        {
            case "list": return List();
            case "stats": return Stats(words.Length > 1 ? string.Join(' ', words[1..]) : _chat?.NameOfPeer(sender) ?? "");
            case "open":
                if (!player) return "'/vs open' needs a player.";
                return Open(sender, words[1..]);
            case "join":
                if (!player) return "'/vs join' needs a player.";
                return Join(sender, words.Length > 1 && int.TryParse(words[1], out int id) ? id : 0);
            case "leave":
                if (!player) return "'/vs leave' needs a player.";
                return Leave(sender, tell: false);
            case "start":
                if (!player) return "'/vs start' needs a player.";
                return Start(sender);
            case "invite":
                if (!player) return "'/vs invite' needs a player.";
                return InviteFriend(sender, string.Join(' ', words[1..]));
            case "close":
                if (!admin && !(player && _games.Any(g => g.S.Opener == sender && g.S.Phase == VsPhase.Lobby)))
                    return "'/vs close' is an admin's (or the opener's, in the lobby).";
                return CloseCommand(sender, words.Length > 1 && int.TryParse(words[1], out int cid) ? cid : 0);
            default:
                return "Usage: /vs open [here|nearest|random|<village>] [rounds] [bots off|n] [easy|normal|hard] · join [id] · leave · start · invite <player> · close <id> · list · stats [name]";
        }
    }

    private string List()
    {
        if (_games.Count == 0) return "No game of Versteckis. /vs open starts one in the village nearest you.";
        return "Versteckis:\n" + string.Join("\n", _games.Select(g =>
            $"#{g.S.Id} {g.S.Label}: {g.S.Phase.ToString().ToLowerInvariant()}, {g.S.Playing.Count()} players" + (g.S.Round > 0 ? $", round {g.S.Round}/{g.S.Rounds}" : "")));
    }

    private string Stats(string who)
    {
        long peer = _chat?.PeerByName(who) ?? -1;
        if (peer <= 0) return $"No player matching '{who}' online.";
        var p = AdventureService.Instance?.Progress(peer)?.Get(VersteckisProgress.Section) ?? SessionRecord(peer);
        return $"{_chat!.NameOfPeer(peer)}: {p.Games} games, {p.Rounds} rounds, {p.Catches} caught, {p.Survived} rounds survived, "
            + $"{p.Bells} bells, {p.Fires} fires, {p.Cans} cans, {p.Cantons.Count} cantons.";
    }

    private Vector3? BodyWorld(long peer) => BodyOf(peer) is { } b ? b.GlobalPosition : null;

    private FootPlayer? BodyOf(long peer) => _players?.GetNodeOrNull<FootPlayer>(PlayerReplication.NodeName(peer));

    /// <summary>Server: an entrant's position as the server knows it (a proxy's published one, a bot's own).</summary>
    public GlobalPos? PositionOf(long peer) => BodyOf(peer) is { } b ? b.Global : null;

    private string Open(long sender, string[] words)
    {
        if (GameOf(sender) != null) return "You are in a game already (/vs leave first).";
        if (Rounds.InRound(sender)) return "You are in another game.";
        if (_games.Any(g => g.S.Opener == sender)) return "You have a game open already.";
        if (_games.Count >= VsRules.MaxGames) return $"This server runs {VsRules.MaxGames} games already.";
        if (PositionOf(sender) is not { } at) return "Nowhere to open a game from yet.";
        string where = "nearest";
        int? rounds = null;
        int bots = VsRules.DefaultBots, level = 1;
        var name = new List<string>();
        for (int i = 0; i < words.Length; i++)
        {
            string w = words[i].ToLowerInvariant();
            if (w is "here" or "nearest" or "random") where = w;
            else if (w == "bots" && i + 1 < words.Length)
            {
                string b = words[++i].ToLowerInvariant();
                if (b == "off") bots = 0;
                else if (int.TryParse(b, out int nb)) bots = Math.Clamp(nb, 0, VsRules.MaxPlayers);
                else return "Bots: off or a number up to 8.";
            }
            else if (Bots.BotProfile.ParseDifficulty(w) is { } d) level = (int)d;
            else if (int.TryParse(w, out int r)) rounds = r;
            else name.Add(words[i]);
        }
        var candidates = VsArea.Candidates(PoiCatalogueHost.Current.OfKind(PoiKind.Church).Concat(PoiCatalogueHost.Current.OfKind(PoiKind.Chapel))
            .Select(p => (p.Id, p.Name, p.Kind, p.Canton, p.E, p.N)), _towns);
        var taken = _games.Select(g => (g.S.E, g.S.N)).ToList();
        candidates = candidates.Where(c => VsArea.FarFromOthers(c.E, c.N, taken)).ToList();
        var queue = new Queue<VsCandidate>();
        if (name.Count > 0)
        {
            if (VsArea.Named(candidates, string.Join(' ', name), at.E, at.N) is not { } named) return $"No village with a church called '{string.Join(' ', name)}'.";
            queue.Enqueue(named);
        }
        else if (where == "here")
        {
            var here = VsArea.Here(candidates, _towns, at.E, at.N);
            if (!VsArea.FarFromOthers(here.E, here.N, taken)) return "Another game is played within 600 m.";
            queue.Enqueue(here);
        }
        else if (where == "random")
        {
            if (VsArea.Random(candidates, _rng, LoadHistory()) is { } pick) queue.Enqueue(pick);
        }
        else foreach (var c in VsArea.Nearest(candidates, at.E, at.N).Take(VsArea.Tries)) queue.Enqueue(c);
        if (queue.Count == 0) return "No village to hide in near here.";

        var game = new Game { Next = queue, AskedRounds = rounds };
        game.S = new VsState
        {
            Id = _nextId++, Opener = sender, Bots = bots, BotLevel = level, Phase = VsPhase.Lobby,
            PhaseStarted = Now, PhaseEnds = Now + VsRules.LobbySeconds,
        };
        _games.Add(game);
        TryNext(game);
        AddEntrant(game, sender);
        Broadcast($"{_chat?.NameOfPeer(sender)} opened a game of Versteckis at {game.S.Label} (#{game.S.Id}). /vs join {game.S.Id}");
        InviteNear(game, sender);
        Push();
        GD.Print($"[vs] game {game.S.Id} opened by {_chat?.NameOfPeer(sender)} at {game.S.Label}");
        return $"Game #{game.S.Id} open at {game.S.Label}. Start it when your friends are in (/vs start).";
    }

    /// <summary>Reads the next candidate's area; a rejected one moves on to the next.</summary>
    private void TryNext(Game g)
    {
        if (!g.Next.TryDequeue(out var c))
        {
            Close(g, "No village to hide in near here.");
            return;
        }
        g.Candidate = c;
        var s = g.S;
        s.Place = c.Name;
        s.Canton = c.Canton;
        s.E = c.E;
        s.N = c.N;
        s.Seed = (int)(HidePlan.Hash((uint)Math.Round(c.E), (uint)Math.Round(c.N), 0x7E57) & 0x7FFFFFFF);
        // the radius for a full game of eight: the area is read once; the radius shrinks to the head count at Gather
        g.Site = null;
        g.Loading = VsGround.LoadAsync(_source(), c.E, c.N, VsRules.MaxRadius, s.Seed);
    }

    private void AddEntrant(Game g, long peer)
    {
        g.S.Entrants.Add(new VsEntrant { Peer = peer, Name = _chat?.NameOfPeer(peer) ?? peer.ToString(), Order = g.S.Entrants.Count });
        _chat?.Invites?.Dismiss(InviteId, g.S.Id, peer);
    }

    /// <summary>The opener's party and every player within 1.5 km of the area are invited.</summary>
    private void InviteNear(Game g, long from)
    {
        var invites = _chat?.Invites;
        if (invites == null || _chat == null) return;
        var asked = new HashSet<long>();
        if (Parties.Server?.Of(from) is { } party)
            foreach (long m in party.Members) if (m != from) asked.Add(m);
        foreach (long peer in _chat.OnlinePeers)
            if (peer != from && PositionOf(peer) is { } p && (p.E - g.S.E) * (p.E - g.S.E) + (p.N - g.S.N) * (p.N - g.S.N) <= 1500 * 1500)
                asked.Add(peer);
        foreach (long peer in asked)
            if (!Rounds.InRound(peer))
                invites.Send(InviteId, from, peer, $"{_chat.NameOfPeer(from)} invites you to Versteckis", LobbyLine(g), out _, key: g.S.Id);
    }

    private static string LobbyLine(Game g) => $"{g.S.Label} · {g.S.Playing.Count()} joined" + (g.S.Bots > 0 ? $" · bots fill to {g.S.Bots}" : "");

    private string InviteFriend(long sender, string name)
    {
        if (GameOf(sender) is not { S.Phase: VsPhase.Lobby } g) return "Open or join a game's lobby first.";
        if (_chat == null) return "No chat here.";
        long peer = _chat.PeerByName(name);
        if (peer <= 0) return $"No player matching '{name}'.";
        if (g.S.Find(peer) is { Left: false }) return $"{_chat.NameOfPeer(peer)} is already in.";
        return _chat.Invites?.Send(InviteId, sender, peer, $"{_chat.NameOfPeer(sender)} invites you to Versteckis", LobbyLine(g), out _, key: g.S.Id)
            ?? "Invites are not running.";
    }

    /// <summary>Server: <paramref name="peer"/> joins game <paramref name="id"/> (0: the only lobby, or the nearest one).</summary>
    public string Join(long peer, int id)
    {
        if (GameOf(peer) is { } mine) return mine.S.Id == id || id == 0 ? "You are in already." : "You are in another game (/vs leave first).";
        if (Rounds.InRound(peer)) return "You are in another game.";
        var lobbies = _games.Where(g => g.S.Phase == VsPhase.Lobby).ToList();
        Game? game = id > 0 ? _games.FirstOrDefault(g => g.S.Id == id) : null;
        if (id == 0 && lobbies.Count > 0)
        {
            var at = PositionOf(peer);
            game = at is { } p ? lobbies.OrderBy(g => (g.S.E - p.E) * (g.S.E - p.E) + (g.S.N - p.N) * (g.S.N - p.N)).First() : lobbies[0];
        }
        if (game == null) return id > 0 ? $"No game #{id}." : "No game is open: /vs open starts one.";
        if (game.S.Phase != VsPhase.Lobby) return "That game is under way: join the next one.";
        if (game.S.Humans >= VsRules.MaxPlayers) return "That game is full.";
        AddEntrant(game, peer);
        Broadcast($"{_chat?.NameOfPeer(peer)} joined Versteckis at {game.S.Label} ({game.S.Humans}).");
        Push();
        return $"You are in: {game.S.Label}. The opener starts it.";
    }

    private string Start(long sender)
    {
        if (GameOf(sender) is not { } g) return "You are in no game.";
        if (g.S.Opener != sender) return "The opener starts the game.";
        if (g.S.Phase != VsPhase.Lobby) return "It has started.";
        if (g.Site == null) return "The village is still being read; a moment.";
        int humans = g.S.Humans;
        if (g.S.Bots > 0 ? humans + Math.Max(0, g.S.Bots - humans) < VsRules.MinPlayers : humans < MinNoBots)
            return g.S.Bots > 0 ? $"At least {VsRules.MinPlayers} players." : $"At least {MinNoBots} players without bots.";
        Gather(g);
        return "Starting.";
    }

    /// <summary>Server: <paramref name="peer"/> leaves (the command, a disconnect). A seeker leaving ends the round at once.</summary>
    public string Leave(long peer, bool tell)
    {
        if (GameOf(peer) is not { } g || g.S.Find(peer) is not { } e) return "You are in no game.";
        if (g.S.Phase == VsPhase.Lobby)
        {
            g.S.Entrants.Remove(e);
            if (g.S.Humans == 0) Close(g, "Everyone left the lobby.");
            else
            {
                if (g.S.Opener == peer) g.S.Opener = g.S.Entrants.First(x => !x.Bot).Peer;
                Push();
            }
            return "You left the lobby.";
        }
        e.Left = true;
        e.Holding = -1;
        DropCarry(g, peer);
        // its rounds count for its reward; it is out of the round now
        ReleasePeer(g, e);
        if (g.S.Seeker == peer && g.S.Phase is VsPhase.Hide or VsPhase.Seek) EndRound(g, seekerLeft: true);
        else if (g.S.Humans == 0) Close(g, "Every player left.");
        else Push();
        return "You left the game. Your rounds count.";
    }

    private string CloseCommand(long sender, int id)
    {
        var g = id > 0 ? _games.FirstOrDefault(x => x.S.Id == id) : _games.FirstOrDefault(x => x.S.Opener == sender);
        if (g == null) return id > 0 ? $"No game #{id}." : "No game to close.";
        Close(g, $"Game #{g.S.Id} was closed.");
        return "Closed.";
    }

    // ==== server: the tick =======================================================================

    private void ServerTick()
    {
        double now = Now;
        for (int i = _games.Count - 1; i >= 0; i--)
        {
            var g = _games[i];
            var s = g.S;
            if (g.Loading is { IsCompleted: true } loading)
            {
                g.Loading = null;
                AreaRead(g, loading.IsCompletedSuccessfully ? loading.Result : null);
                if (!_games.Contains(g)) continue;
            }
            switch (s.Phase)
            {
                case VsPhase.Lobby when now >= s.PhaseEnds:
                    if (g.Site != null && Start(s.Opener) == "Starting.") break;
                    Close(g, "Nobody started the game in time.");
                    break;
                case VsPhase.Gather when now >= s.PhaseEnds:
                    StartRound(g);
                    break;
                case VsPhase.Hide:
                    Check(g, now);
                    if (now >= s.PhaseEnds) StartSeek(g);
                    break;
                case VsPhase.Seek:
                    Check(g, now);
                    SeekTick(g, now);
                    if (s.HidersLeft == 0 || now >= s.PhaseEnds) EndRound(g, seekerLeft: false);
                    break;
                case VsPhase.RoundEnd when now >= s.PhaseEnds:
                    if (s.Round >= s.Rounds || s.Humans == 0) Results(g);
                    else StartRound(g);
                    break;
                case VsPhase.Results when now >= s.PhaseEnds:
                    Close(g, null);
                    break;
            }
        }
        BotTick(now);
    }

    /// <summary>The area's tiles are read: rejected (try the next candidate) or ready.</summary>
    private void AreaRead(Game g, VsGround? site)
    {
        string? why = site == null ? "its tiles could not be read" : VsArea.Reject(site.Facts);
        if (why != null)
        {
            GD.Print($"[vs] game {g.S.Id}: {g.S.Label} rejected ({why})");
            TryNext(g);
            Push();
            return;
        }
        g.Site = site;
        g.S.DoorE = site!.Door.E;
        g.S.DoorN = site.Door.N;
        g.S.Ground = site.GroundAt(g.S.E, g.S.N) ?? 0;
        g.S.Kinds = site.Kinds;
        GD.Print(FormattableString.Invariant($"[vs] game {g.S.Id}: {g.S.Label} ready, {site.Buildings.Count} buildings, {site.Decor.Count} decor, kinds {string.Join(",", site.Kinds)}"));
        Push();
    }

    // ==== server: sending ========================================================================

    /// <summary>Every game to every peer: the Activities list, each client's own game, every disguise to draw.</summary>
    private void Push()
    {
        if (!_server) return;
        string json = VsState.ListJson(_games.Select(g => g.S));
        foreach (long peer in Multiplayer.GetPeers())
            RpcId(peer, MethodName.SetGames, json);
    }

    /// <summary>Server: a joining peer gets every game (it draws the disguises of the bodies it meets).</summary>
    public void SendTo(long peer) => RpcId(peer, MethodName.SetGames, VsState.ListJson(_games.Select(g => g.S)));

    /// <summary>Server: a peer left the game.</summary>
    public void PeerLeft(long peer)
    {
        if (GameOf(peer) != null) Leave(peer, tell: false);
        _chat?.Invites?.WithdrawInvolving(InviteId, peer);
    }

    private void Broadcast(string line) => _chat?.Broadcast(line, ChatKind.System);

    private void Tell(VsState s, string line)
    {
        foreach (var e in s.Entrants)
            if (!e.Bot && !e.Left && Connected(e.Peer)) _chat?.Tell(e.Peer, line, ChatKind.System);
    }

    private bool Connected(long peer) => peer > 0 && Multiplayer.GetPeers().Contains((int)peer);

    private static List<(double E, double N)> LoadHistory()
    {
        try
        {
            if (!Godot.FileAccess.FileExists(HistoryFile)) return new();
            using var f = Godot.FileAccess.Open(HistoryFile, Godot.FileAccess.ModeFlags.Read);
            var list = JsonSerializer.Deserialize<List<double[]>>(f.GetAsText()) ?? new();
            return list.Where(p => p.Length == 2).Select(p => (p[0], p[1])).ToList();
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[vs] {HistoryFile}: {ex.Message}");
            return new();
        }
    }

    private static void SaveHistory(double e, double n)
    {
        var list = VsArea.Remember(LoadHistory(), e, n);
        JsonStore.SaveAsync(HistoryFile, list.Select(p => new[] { p.E, p.N }).ToList(), JsonStore.Indented, ex => GD.PushWarning($"[vs] history: {ex.Message}"));
    }
}
