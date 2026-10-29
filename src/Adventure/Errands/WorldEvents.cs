using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Errands;

/// <summary>
/// The world events (#1339, <c>docs/plans/adventure-errands.md</c> part 6, <c>docs/notes/adventure/world-events.md</c>), at
/// <c>World/WorldEvents</c> on the server and every client (RPCs route by path), made with <see cref="ErrandService"/>. Small
/// emergencies come up near adventuring players and anyone nearby helps: a runaway cow to drive home, a lost hiker to bring to a
/// hut, an avalanche burial to dig out, a small forest fire to beat out. Nothing costs health: smoke dims the view.
///
/// <para>
/// <b>The authority plans and plays</b> (the dedicated server, or the game offline): every 120 s slot it rolls each 2 km cell an
/// adventuring player stands in (<see cref="WorldEventRules.Roll"/>), finds the anchor of the first kind that fits the place,
/// the season and the hour, runs the event's simulation at 10 Hz (<see cref="CowSim"/> and the others, pure) and sends the plan
/// once to the peers within 3 km, the moving parts at 4 Hz and the state on change. A client only draws, and asks to help
/// (<see cref="Help"/> is the one server path: a client's request, or a bot's call). Off in a Battle Royale round and in a
/// Versteckis game (<see cref="Busy"/>); bots count as helpers by standing near, and may act, but earn nothing.
/// </para>
/// </summary>
public partial class WorldEvents : Node3D, IOriginContainer
{
    public const string NodeName = "WorldEvents";
    private const double TickS = 0.1, MoveEveryS = 0.25, CardHikerLifeS = 1800, CardUnheldS = 30;

    public static WorldEvents? Instance { get; private set; }

    /// <summary><c>--world-events off|rare|normal</c> (normal by default).</summary>
    public static WorldEventMode Mode { get; set; } = WorldEventRules.ParseMode(CmdArgs.Value("--world-events"));

    private bool _server, _networked;
    private AdventureService _adventure = null!;

    public bool Authority => _server || !_networked;
    private static bool Visual => !NetworkManager.DedicatedServer;

    // ---- what the world gives it -------------------------------------------------------------------------

    /// <summary>The terrain height at an LV95 point on this peer, null where it is not loaded.</summary>
    public Func<double, double, float?>? GroundAt { get; set; }
    /// <summary>Whether a point is forest cover on this peer (the fire spreads through it); null where unknown.</summary>
    public Func<double, double, bool?>? ForestAt { get; set; }
    /// <summary>True for a peer in a game that holds its adventure (Versteckis): no event comes near it.</summary>
    public Func<long, bool>? Busy { get; set; }
    public Func<WorldOrigin?>? Origin { get; set; }
    public Func<FootPlayer?>? Me { get; set; }
    public Action<string>? Toast { get; set; }

    // ---- the authority's side ----------------------------------------------------------------------------

    private readonly struct Hold
    {
        public readonly EventAct Act;
        public readonly int Arg;
        public Hold(EventAct act, int arg) { Act = act; Arg = arg; }
    }

    private sealed class Live
    {
        public required int Id;
        public required WorldEventKind Kind;
        public required double Start;
        public required double E, N;
        public float Ground;
        public string Canton = "";
        public uint Seed;
        /// <summary>The Postkarte card this hiker belongs to, "" for a planned event.</summary>
        public string Card = "";
        public string Near = "";
        /// <summary>The name of the hut a card's hiker is brought to (the escort step's area).</summary>
        public string Goal = "";
        public CowSim? Cow;
        public HikerSim? Hiker;
        public AvalancheSim? Avalanche;
        public FireSim? Fire;
        public (int E, int N) Cell;
        public readonly HashSet<long> Acted = new();
        public readonly Dictionary<long, Hold> Holds = new();
        public readonly HashSet<long> Informed = new();
        public bool Dirty;
        /// <summary>A check's shorter life for a staged event; 0 = the rule's.</summary>
        public double LifeS;
        /// <summary>The last time somebody ran this card's hiker card: a hiker nobody holds for 30 s goes home.</summary>
        public double Held;
        public double LengthS => LifeS > 0 ? LifeS : Card.Length > 0 ? CardHikerLifeS : WorldEventRules.LengthS(Kind);
        public (double E, double N) At => Cow is { } c ? (c.E, c.N) : Hiker is { } h ? (h.E, h.N) : (E, N);
    }

    private readonly Dictionary<int, Live> _live = new();
    private readonly Dictionary<(int, int), double> _cool = new();
    private readonly List<Spot> _spots = new();
    private readonly Dictionary<long, Spot> _where = new();
    private readonly List<(long Peer, GlobalPos At)> _humans = new();
    private readonly List<int> _drop = new();
    private double _acc, _moveIn, _planIn, _informIn;
    private long _slot = long.MinValue;

    /// <summary><c>--worldevent cow,hiker,avalanche,fire</c> (checks): one of each in turn, near the first player, season and slot aside.</summary>
    private readonly List<WorldEventKind> _forced = new();
    private int _forcedCount;
    /// <summary><c>--worldevent-players n</c> (net checks): the forced events wait until n adventuring players are there.</summary>
    private readonly int _forcedPlayers = int.TryParse(CmdArgs.Value("--worldevent-players"), out int n) && n > 0 ? n : 1;

    /// <summary>A check's stand-in for the cover raster: which points burn.</summary>
    public Func<double, double, bool>? ForestOverride { get; set; }

    public static WorldEvents Create(AdventureService adventure, bool server, bool networked)
    {
        var s = new WorldEvents { Name = NodeName, _server = server, _networked = networked, _adventure = adventure };
        adventure.GetParent().AddChild(s);
        return s;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        ExitDraw();
    }

    public override void _Ready()
    {
        foreach (string word in (CmdArgs.Value("--worldevent") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (ParseKind(word.Trim()) is { } kind) _forced.Add(kind);
        // a net check's server has no forest raster on its flat fixture: a checkerboard of 16 m squares burns instead
        if (CmdArgs.Has("--worldevent-forest")) ForestOverride = (e, n) => ((long)Math.Floor(e / 16) + (long)Math.Floor(n / 16)) % 2 == 0;
        if (Authority) _adventure.Loaded += peer => _informIn = 0;
        GD.Print($"[worldevents] {(_server ? "server" : _networked ? "client" : "offline")}: mode {Mode}");
        if (Visual) ReadyDraw();
    }

    public static WorldEventKind? ParseKind(string s) => s.ToLowerInvariant() switch
    {
        "cow" or "runawaycow" => WorldEventKind.RunawayCow,
        "hiker" or "losthiker" => WorldEventKind.LostHiker,
        "avalanche" => WorldEventKind.Avalanche,
        "fire" or "forestfire" => WorldEventKind.ForestFire,
        _ => null,
    };

    /// <summary>An adventuring peer that no game holds: events come near it and it may be paid.</summary>
    private bool Eligible(long peer) => _adventure.Active(peer) && Busy?.Invoke(peer) != true;

    // ---- queries (bots, checks) ----------------------------------------------------------------------------

    /// <summary>The events running now with where they are: a bot's sense (an event is announced to bots as to players).</summary>
    public IEnumerable<(int Id, WorldEventKind Kind, double E, double N)> Running
    {
        get
        {
            foreach (var ev in _live.Values) yield return (ev.Id, ev.Kind, ev.At.E, ev.At.N);
        }
    }

    public int RunningCount => _live.Count;

    // ---- the tick -------------------------------------------------------------------------------------------

    public override void _PhysicsProcess(double delta)
    {
        if (!Authority) return;
        _acc += delta;
        if (_acc < TickS) return;
        double dt = _acc;
        _acc = 0;
        double now = GameClock.Now;
        Gather();
        _planIn -= dt;
        if (_planIn <= 0) { _planIn = 0.5; Plan(now); }
        foreach (var ev in _live.Values) Run(ev, dt, now);
        _moveIn -= dt;
        bool move = _moveIn <= 0;
        if (move) _moveIn = MoveEveryS;
        _informIn -= dt;
        bool inform = _informIn <= 0;
        if (inform) _informIn = 1;
        _drop.Clear();
        foreach (var ev in _live.Values)
        {
            if (inform) Inform(ev);
            if (move) SendMoved(ev);
            if (ev.Dirty) { ev.Dirty = false; SendState(ev); }
            if (ev.Cow == null && ev.Hiker == null && ev.Avalanche == null && ev.Fire == null) _drop.Add(ev.Id);
        }
        foreach (int id in _drop) _live.Remove(id);
    }

    /// <summary>The bodies near now: every player and bot as a helper's spot, the adventuring humans apart.</summary>
    private void Gather()
    {
        _spots.Clear();
        _where.Clear();
        _humans.Clear();
        if (!IsInsideTree()) return;
        foreach (var s in PlayerSnapshot.Of(GetTree()))
        {
            var p = s.Player;
            if (p.Origin == null && p.NetGlobal.E == 0) continue;
            var g = p.Global;
            var spot = new Spot(g.E, g.N);
            _spots.Add(spot);
            long peer = p.GetMultiplayerAuthority();
            _where[peer] = spot;
            if (!p.Npc && Eligible(peer)) _humans.Add((peer, g));
        }
    }

    // ---- planning ---------------------------------------------------------------------------------------------

    private void Plan(double now)
    {
        if (_humans.Count == 0) return;
        bool forcedDue = _forcedCount < _forced.Count && _live.Count == 0 && _humans.Count >= _forcedPlayers;
        if (forcedDue)
        {
            var who = _humans[0];
            var (ce, cn) = WorldEventRules.Cell(who.At.E, who.At.N);
            if (TryPlan(_forced[_forcedCount], (uint)(now * 1000) ^ 0xF0F0u, who.At, ce, cn, now, forced: true) is { } forced)
            {
                _forcedCount++;
                Start(forced, now);
            }
            return;   // the ground or the herd may not be in yet: again in half a second
        }
        if (Mode == WorldEventMode.Off) return;
        long slot = WorldEventRules.Slot(now);
        if (slot == _slot) return;
        _slot = slot;
        var seen = new HashSet<(int, int)>();
        foreach (var (peer, at) in _humans)
        {
            var (ce, cn) = WorldEventRules.Cell(at.E, at.N);
            if (!seen.Add((ce, cn)) || _live.Count >= WorldEventRules.MaxRunning) continue;
            if (_cool.TryGetValue((ce, cn), out double until) && now < until) continue;
            if (_live.Values.Any(l => l.Cell == (ce, cn))) continue;
            if (!WorldEventRules.Roll(ce, cn, slot, Mode, out uint seed)) continue;
            double hour = World.WorldClock.CurrentHour;
            foreach (var kind in WorldEventRules.Order(seed))
            {
                if (!WorldEventRules.InSeason(kind, ErrandService.Today / 100 % 100, at.Alt) || !WorldEventRules.Passes(kind, hour, seed)) continue;
                if (TryPlan(kind, seed, at, ce, cn, now, forced: false) is not { } ev) continue;
                Start(ev, now);
                break;
            }
        }
    }

    private void Start(Live ev, double now)
    {
        _live[ev.Id] = ev;
        _cool[ev.Cell] = now + WorldEventRules.CellCooldownS;
        GD.Print($"[worldevents] {ev.Kind} {ev.Id} at LV95 {ev.E:F0}/{ev.N:F0} ({ev.Canton}){(ev.Card.Length > 0 ? " for " + ev.Card : "")}");
        Inform(ev);
    }

    private Live? TryPlan(WorldEventKind kind, uint seed, GlobalPos near, int ce, int cn, double now, bool forced)
    {
        int id = WorldEventRules.Id(ce, cn, forced ? _forcedCount + 1000 : WorldEventRules.Slot(now), kind);
        if (_live.ContainsKey(id)) return null;
        double U(int salt) => ErrandHash.Unit(ErrandHash.Of(seed, salt, 0xE7));
        Live Make(double e, double n, float ground) => new()
        {
            Id = id, Kind = kind, Start = now, E = e, N = n, Ground = ground, Seed = seed, Cell = (ce, cn), Canton = CantonAt(e, n),
        };
        switch (kind)
        {
            case WorldEventKind.RunawayCow:
            {
                if (Farming.Herds.Instance?.CowNear(near.E, near.N, 600f, SimNow) is not { } cow) return null;
                var ev = Make(cow.E, cow.N, cow.Ground);
                ev.Cow = new CowSim(cow.E, cow.N, U(1));
                return ev;
            }
            case WorldEventKind.LostHiker:
            {
                var pois = PoiCatalogueHost.Current;
                var list = new List<Poi>();
                Poi? best = null;
                ulong bestScore = ulong.MaxValue;
                foreach (var k in new[] { PoiKind.Peak, PoiKind.Pass, PoiKind.SummitCross })
                {
                    pois.Near(near.E, near.N, 2000, list, k);
                    foreach (var p in list)
                    {
                        double d = Math.Sqrt((p.E - near.E) * (p.E - near.E) + (p.N - near.N) * (p.N - near.N));
                        if (d < 600 || p.Elevation < near.Alt + 150 || p.Name.Length == 0) continue;
                        ulong score = ErrandHash.Of(seed, (long)p.Hash);
                        if (score < bestScore) { bestScore = score; best = p; }
                    }
                }
                if (best == null || HikerGoal(pois, best) is not { } goal) return null;
                var (he, hn) = WorldEventRules.Offset(best.E, best.N, U(2), 20 + 60 * U(3));
                var ev = Make(he, hn, GroundOf(he, hn) ?? best.Elevation);
                ev.Hiker = new HikerSim(he, hn, goal.E, goal.N);
                ev.Near = best.Name;
                return ev;
            }
            case WorldEventKind.Avalanche:
            {
                for (int i = 0; i < 24; i++)
                {
                    double e = near.E + Math.Sin(U(10 + i * 2) * Math.Tau) * (300 + 600 * U(11 + i * 2)), n = near.N + Math.Cos(U(10 + i * 2) * Math.Tau) * (300 + 600 * U(11 + i * 2));
                    if (GroundOf(e, n) is not { } g || g < 1500 && !forced) continue;
                    if (!forced && (SlopeDeg(e, n) is not { } slope || slope is < 28 or > 45)) continue;
                    var ev = Make(e, n, g);
                    ev.Avalanche = new AvalancheSim(e, n, seed);
                    return ev;
                }
                return null;
            }
            default:
            {
                for (int i = 0; i < 24; i++)
                {
                    double e = near.E + Math.Sin(U(30 + i * 2) * Math.Tau) * (200 + 600 * U(31 + i * 2)), n = near.N + Math.Cos(U(30 + i * 2) * Math.Tau) * (200 + 600 * U(31 + i * 2));
                    if (GroundOf(e, n) is not { } g || g > 1800 && !forced || !Forest(e, n)) continue;
                    // a forest edge: some neighbour 8 m away is not forest
                    if (Forest(e + 8, n) && Forest(e - 8, n) && Forest(e, n + 8) && Forest(e, n - 8)) continue;
                    var ev = Make(e, n, g);
                    ev.Fire = new FireSim(e, n, seed, (a, b) => Forest(e + a * FireSim.CellM, n + b * FireSim.CellM));
                    return ev;
                }
                return null;
            }
        }
    }

    /// <summary>The nearest hut, bus stop or station within 3 km of the hiker's peak: where the helper brings them.</summary>
    private static (double E, double N)? HikerGoal(PoiCatalogue pois, Poi by)
    {
        var list = new List<Poi>();
        (double E, double N)? best = null;
        double bd = 3000.0 * 3000;
        foreach (var kind in new[] { PoiKind.Hut, PoiKind.BusStop, PoiKind.RailStation })
        {
            pois.Near(by.E, by.N, 3000, list, kind);
            foreach (var p in list)
            {
                double d = (p.E - by.E) * (p.E - by.E) + (p.N - by.N) * (p.N - by.N);
                if (d < bd) { bd = d; best = (p.E, p.N); }
            }
        }
        return best;
    }

    private bool Forest(double e, double n) => ForestOverride?.Invoke(e, n) ?? ForestAt?.Invoke(e, n) ?? false;

    private float? GroundOf(double e, double n) => GroundAt?.Invoke(e, n);

    private float? SlopeDeg(double e, double n)
    {
        if (GroundOf(e + 10, n) is not { } a || GroundOf(e - 10, n) is not { } b || GroundOf(e, n + 10) is not { } c || GroundOf(e, n - 10) is not { } d) return null;
        double gx = (a - b) / 20.0, gz = (c - d) / 20.0;
        return (float)(Math.Atan(Math.Sqrt(gx * gx + gz * gz)) * 180 / Math.PI);
    }

    private static string CantonAt(double e, double n)
    {
        var list = new List<Poi>();
        PoiCatalogueHost.Current.Near(e, n, 5000, list);
        return list.Count > 0 ? list[0].Canton : "";
    }

    /// <summary>The shared simulation clock (the herds' cows are placed on it).</summary>
    private static double SimNow => SimClock.SimAt(ClockSync.ServerNow);

    // ---- a Postkarte's hiker ----------------------------------------------------------------------------------

    /// <summary>
    /// A lost hiker for a taken card (the lead of <see cref="ErrandService.Accept"/>): sits where the card says until found, and is
    /// the same hiker for everyone who holds the card. The card pays, not the event.
    /// </summary>
    public void HostCard(string cardId, HikerSpot spot, string canton, string goalName)
    {
        if (!Authority) return;
        int id = WorldEventRules.CardId(cardId);
        if (_live.ContainsKey(id)) return;
        var (ce, cn) = WorldEventRules.Cell(spot.E, spot.N);
        var ev = new Live
        {
            Id = id, Kind = WorldEventKind.LostHiker, Start = GameClock.Now, E = spot.E, N = spot.N, Card = cardId, Canton = canton,
            Seed = (uint)(ErrandHash.Of(cardId) >> 32), Cell = (ce, cn), Near = spot.Near, Goal = goalName, Ground = GroundOf(spot.E, spot.N) ?? 0,
        };
        ev.Hiker = new HikerSim(spot.E, spot.N, spot.GoalE, spot.GoalN);
        Start(ev, GameClock.Now);
    }

    // ---- checks ---------------------------------------------------------------------------------------------------

    /// <summary>A check's event of <paramref name="kind"/> staged at an LV95 point now (anchors, season and slot aside). Returns its id.</summary>
    public int Stage(WorldEventKind kind, double e, double n, uint seed = 1, double goalE = double.NaN, double goalN = double.NaN, double lifeS = 0)
    {
        var (ce, cn) = WorldEventRules.Cell(e, n);
        int id = WorldEventRules.Id(ce, cn, 900000 + _live.Count + _forcedCount, kind) ^ (int)seed;
        var ev = new Live { Id = id, Kind = kind, Start = GameClock.Now, E = e, N = n, Seed = seed, Cell = (ce, cn), Canton = CantonAt(e, n), Ground = GroundOf(e, n) ?? 0, LifeS = lifeS };
        switch (kind)
        {
            case WorldEventKind.RunawayCow: ev.Cow = new CowSim(e, n, ErrandHash.Unit(ErrandHash.Of(seed, 1))); break;
            case WorldEventKind.LostHiker: ev.Hiker = new HikerSim(e, n, double.IsNaN(goalE) ? e + 150 : goalE, double.IsNaN(goalN) ? n : goalN); ev.Near = "the check"; break;
            case WorldEventKind.Avalanche: ev.Avalanche = new AvalancheSim(e, n, seed); break;
            default: ev.Fire = new FireSim(e, n, seed, (a, b) => Forest(e + a * FireSim.CellM, n + b * FireSim.CellM)); break;
        }
        Start(ev, GameClock.Now);
        return id;
    }

    /// <summary>A check asks for the next planned event to be of this kind (the <c>--worldevent</c> list, later).</summary>
    public void Force(WorldEventKind kind) => _forced.Add(kind);

    /// <summary>What a check reads of a running event; null when it is over.</summary>
    public readonly record struct Probe(WorldEventKind Kind, double E, double N, bool Found, int Count, float Progress);

    public Probe? ProbeOf(int id)
    {
        if (!_live.TryGetValue(id, out var ev)) return null;
        var (e, n) = ev.At;
        return new Probe(ev.Kind, e, n, ev.Hiker?.Found ?? false, ev.Fire?.Count ?? ev.Avalanche?.DugCount ?? 0, ev.Avalanche?.Progress[0] ?? 0f);
    }

    /// <summary>The burning cells of a fire and the buried spots of an avalanche (LV95), for a check to walk to.</summary>
    public List<(int Key, double E, double N)> Targets(int id)
    {
        var list = new List<(int, double, double)>();
        if (!_live.TryGetValue(id, out var ev)) return list;
        if (ev.Fire is { } fire) foreach (int key in fire.Burning) { var (e, n) = fire.CellAt(key); list.Add((key, e, n)); }
        if (ev.Avalanche is { } av) for (int i = 0; i < av.Buried; i++) if (!av.Dug(i)) list.Add((i, av.Spots[i].E, av.Spots[i].N));
        return list;
    }

    // ---- running an event ---------------------------------------------------------------------------------------

    private void Run(Live ev, double dt, double now)
    {
        if (ev.Cow == null && ev.Hiker == null && ev.Avalanche == null && ev.Fire == null) return;
        double age = now - ev.Start;
        if (ev.Card.Length > 0)
        {
            bool held = false;
            foreach (var (peer, _) in _humans)
                if (_adventure.Progress(peer)?.Get(QuestsProgress.Section).Active.ContainsKey(ev.Card) == true) { held = true; break; }
            if (held || ev.Held == 0) ev.Held = now;
            else if (now - ev.Held > CardUnheldS) { End(ev, false); return; }
        }
        switch (ev.Kind)
        {
            case WorldEventKind.RunawayCow when ev.Cow is { } cow:
                if (cow.Step(dt, _spots)) Credit(ev, cow.E, cow.N, CowSim.PushM);
                if (cow.IsHome) End(ev, true);
                else if (age >= ev.LengthS) End(ev, false);
                break;
            case WorldEventKind.LostHiker when ev.Hiker is { } hiker:
                hiker.Step(dt, _spots);
                if (hiker.Walking) Credit(ev, hiker.E, hiker.N, 10);
                if (hiker.AtGoal && hiker.Found) End(ev, true);
                else if (age >= ev.LengthS) End(ev, false);
                break;
            case WorldEventKind.Avalanche when ev.Avalanche is { } av:
                DoHolds(ev, dt);
                if (av.Done) End(ev, true);
                else if (age >= ev.LengthS) End(ev, false);
                break;
            case WorldEventKind.ForestFire when ev.Fire is { } fire:
                DoHolds(ev, dt);
                if (fire.Step(dt, (a, b) => Forest(fire.E + a * FireSim.CellM, fire.N + b * FireSim.CellM))) ev.Dirty = true;
                if (fire.IsOut) End(ev, true);
                else if (fire.Overrun || age >= ev.LengthS) End(ev, false);
                break;
        }
    }

    /// <summary>Everyone within <paramref name="within"/> m of a moving cow or hiker is helping: a push counts as an act.</summary>
    private void Credit(Live ev, double e, double n, double within)
    {
        foreach (var (peer, spot) in _where)
            if (Dist(spot, new Spot(e, n)) <= within) ev.Acted.Add(peer);
    }

    private readonly Dictionary<int, int> _tally = new();

    /// <summary>The helpers' holds, timed here: a dig or a beating counts while the body stays within reach.</summary>
    private void DoHolds(Live ev, double dt)
    {
        if (ev.Holds.Count == 0) return;
        _tally.Clear();
        foreach (long actor in ev.Holds.Keys.ToList())
        {
            var hold = ev.Holds[actor];
            if (!_where.TryGetValue(actor, out var at)) { ev.Holds.Remove(actor); continue; }
            double reach = WorldEventRules.ActReachM + WorldEventRules.ActSlackM;
            if (hold.Act == EventAct.DigStart && ev.Avalanche is { } av)
            {
                if (hold.Arg < 0 || hold.Arg >= av.Buried || av.Dug(hold.Arg) || Dist(at, av.Spots[hold.Arg]) > reach) { ev.Holds.Remove(actor); continue; }
                _tally[hold.Arg] = _tally.GetValueOrDefault(hold.Arg) + 1;
            }
            else if (hold.Act == EventAct.BeatStart && ev.Fire is { } fire)
            {
                var (ce, cn) = fire.Burning.Contains(hold.Arg) ? fire.CellAt(hold.Arg) : (double.NaN, double.NaN);
                if (double.IsNaN(ce) || Dist(at, new Spot(ce, cn)) > reach) { ev.Holds.Remove(actor); continue; }
                _tally[hold.Arg] = _tally.GetValueOrDefault(hold.Arg) + 1;
            }
            else ev.Holds.Remove(actor);
        }
        foreach (var (target, n) in _tally)
        {
            if (ev.Avalanche is { } av)
            {
                bool wasDug = av.Dug(target);
                int pct = (int)(av.Progress[target] * 20);
                av.Dig(target, n, dt);
                if (av.Dug(target) != wasDug || (int)(av.Progress[target] * 20) != pct) ev.Dirty = true;
            }
            else if (ev.Fire is { } fire && fire.Beat(target, n, dt)) ev.Dirty = true;
        }
    }

    private static double Dist(Spot a, Spot b) => Math.Sqrt((a.E - b.E) * (a.E - b.E) + (a.N - b.N) * (a.N - b.N));

    // ---- helping, the one server path ---------------------------------------------------------------------------

    /// <summary>
    /// A helper acts at an event: the RPC's request for a player, a bot's call (<paramref name="at"/> is the body's own position,
    /// the server's, never a client's word). Null when taken, else why not. <see cref="EventAct.Talk"/> finds the hiker;
    /// the dig and beat acts start and stop a hold the server times itself, for as long as the body stays within reach.
    /// </summary>
    public string? Help(long actor, GlobalPos at, int eventId, EventAct act, int arg)
    {
        if (!Authority) return "Not here.";
        if (!_live.TryGetValue(eventId, out var ev)) return "There is nothing going on here.";
        var me = new Spot(at.E, at.N);
        double reach = WorldEventRules.ActReachM + WorldEventRules.ActSlackM;
        switch (act)
        {
            case EventAct.Talk when ev.Hiker is { } hiker:
                if (Dist(me, new Spot(hiker.E, hiker.N)) > reach) return "Go closer.";
                if (!hiker.Found)
                {
                    hiker.Found = true;
                    ev.Dirty = true;
                    GD.Print($"[worldevents] {ev.Id}: the hiker was found by {actor}");
                }
                ev.Acted.Add(actor);
                if (ev.Card.Length > 0)
                    foreach (long holder in HoldersNear(ev, hiker.E, hiker.N, actor))
                        if (_adventure.Progress(holder) is { } p) _adventure.Events.Raise(new Interacted(holder, p.Key, WorldEventRules.HikerEntity(ev.Card)));
                Tell(actor, "Hiker: \"Thank you! I will follow you.\"");
                return null;
            case EventAct.DigStart when ev.Avalanche is { } av:
                if (arg < 0 || arg >= av.Buried || av.Dug(arg) || Dist(me, av.Spots[arg]) > reach) return "Nobody is buried there.";
                ev.Holds[actor] = new Hold(act, arg);
                ev.Acted.Add(actor);
                _where[actor] = me;
                return null;
            case EventAct.BeatStart when ev.Fire is { } fire:
                if (!fire.Burning.Contains(arg)) return "That is not burning.";
                var (ce, cn) = fire.CellAt(arg);
                if (Dist(me, new Spot(ce, cn)) > reach) return "Go closer.";
                ev.Holds[actor] = new Hold(act, arg);
                ev.Acted.Add(actor);
                _where[actor] = me;
                return null;
            case EventAct.DigStop or EventAct.BeatStop:
                ev.Holds.Remove(actor);
                return null;
            default:
                return "That does not help here.";
        }
    }

    /// <summary>The adventuring peers within 150 m of a point who run the Postkarte card of a hiker (and the actor, who may hold it too).</summary>
    private IEnumerable<long> HoldersNear(Live ev, double e, double n, long actor)
    {
        foreach (var (peer, at) in _humans)
        {
            if (peer != actor && Dist(new Spot(at.E, at.N), new Spot(e, n)) > WorldEventRules.PaidNearM) continue;
            if (_adventure.Progress(peer)?.Get(QuestsProgress.Section).Active.ContainsKey(ev.Card) == true) yield return peer;
        }
    }

    // ---- the end ------------------------------------------------------------------------------------------------------

    private void End(Live ev, bool success)
    {
        GD.Print($"[worldevents] {ev.Kind} {ev.Id} ends: {(success ? "helped through" : "the world solved it")}, {ev.Acted.Count} helped");
        var (e, n) = ev.At;
        if (success) Pay(ev, e, n);
        // the card's escort step: everyone near who runs the card, whether or not they did an act
        if (success && ev.Card.Length > 0)
            foreach (long holder in HoldersNear(ev, e, n, -1))
                if (_adventure.Progress(holder) is { } hp) _adventure.Events.Raise(new Escorted(holder, hp.Key, WorldEventRules.HikerEntity(ev.Card), "area:" + ev.Goal));
        foreach (long peer in ev.Informed) SendEnded(peer, ev.Id, success, success ? WorldEventRules.SuccessLine(ev.Kind) : WorldEventRules.TimeoutLine(ev.Kind));
        ev.Cow = null; ev.Hiker = null; ev.Avalanche = null; ev.Fire = null;
    }

    /// <summary>Rewards for everyone who did an act and stands within 150 m: cash, reputation in the canton, the count and the title.</summary>
    private void Pay(Live ev, double e, double n)
    {
        foreach (long actor in ev.Acted)
        {
            if (!Eligible(actor) || _adventure.Progress(actor) is not { } p) continue;
            if (!_where.TryGetValue(actor, out var at) || Dist(at, new Spot(e, n)) > WorldEventRules.PaidNearM) continue;
            var errands = p.Get(ErrandsProgress.Section);
            bool card = ev.Card.Length > 0;
            int chf = card ? 0 : WorldEventRules.Chf(ev.Kind);
            if (chf > 0) _adventure.Grant(actor, new Cash(chf), "event:" + ev.Kind);
            if (!card && ev.Canton.Length == 2) ErrandService.Instance?.AddRep(actor, p, ev.Canton, WorldEventRules.RepPoints);
            if (WorldEventTally.Add(errands, ev.Kind)) _adventure.Grant(actor, new Title(WorldEventRules.Title(ev.Kind)), "event-title:" + ev.Kind);
            p.Touch(ErrandsProgress.Section);
            _adventure.Events.Raise(new WorldEventHelped(actor, p.Key, ev.Id, ev.Kind, ev.Canton, true));
            Tell(actor, chf > 0 ? $"{WorldEventRules.SuccessLine(ev.Kind)} CHF {chf}{(ev.Canton.Length == 2 ? $", +{WorldEventRules.RepPoints} {ev.Canton}" : "")}" : WorldEventRules.SuccessLine(ev.Kind));
        }
    }

    // ---- sending ---------------------------------------------------------------------------------------------------------

    private bool Connected(long peer) => peer > 0 && Multiplayer.MultiplayerPeer != null && Multiplayer.GetPeers().Contains((int)peer);

    /// <summary>Sends the plan to every adventuring peer within 3 km that has not got it yet (a joiner, a walker who came near).</summary>
    private void Inform(Live ev)
    {
        var (e, n) = ev.At;
        foreach (var (peer, at) in _humans)
        {
            if (ev.Informed.Contains(peer) || (at.E - e) * (at.E - e) + (at.N - n) * (at.N - n) > WorldEventRules.InformM * WorldEventRules.InformM) continue;
            ev.Informed.Add(peer);
            if (Connected(peer))
            {
                RpcId(peer, MethodName.PlanOut, ev.Id, (int)ev.Kind, ev.E, ev.N, ev.Ground, ev.Canton, (long)ev.Seed, ev.Near, ev.Card);
                RpcId(peer, MethodName.StateOut, ev.Id, StateOf(ev));
                RpcId(peer, MethodName.MovedOut, ev.Id, e, n, YawOf(ev), false);
            }
            else if (!_networked)
            {
                OnPlan(ev.Id, (int)ev.Kind, ev.E, ev.N, ev.Ground, ev.Canton, ev.Seed, ev.Near, ev.Card);
                OnState(ev.Id, StateOf(ev));
                OnMoved(ev.Id, e, n, YawOf(ev), false);
            }
        }
    }

    private static float YawOf(Live ev) => ev.Cow?.Yaw ?? ev.Hiker?.Yaw ?? 0f;

    private void SendMoved(Live ev)
    {
        if ((ev.Cow ?? (object?)ev.Hiker) == null) return;
        var (e, n) = ev.At;
        bool walking = ev.Hiker?.Walking ?? ev.Cow != null;
        foreach (long peer in ev.Informed)
        {
            if (Connected(peer)) RpcId(peer, MethodName.MovedOut, ev.Id, e, n, YawOf(ev), walking);
            else if (!_networked) OnMoved(ev.Id, e, n, YawOf(ev), walking);
        }
    }

    private void SendState(Live ev)
    {
        var data = StateOf(ev);
        foreach (long peer in ev.Informed)
        {
            if (Connected(peer)) RpcId(peer, MethodName.StateOut, ev.Id, data);
            else if (!_networked) OnState(ev.Id, data);
        }
    }

    /// <summary>What a client needs of an event's state: the hiker found; the dug mask and the digging per cent; the burning cells.</summary>
    private static int[] StateOf(Live ev)
    {
        if (ev.Hiker is { } h) return new[] { h.Found ? 1 : 0 };
        if (ev.Avalanche is { } a)
        {
            int mask = 0;
            var data = new int[1 + a.Buried];
            for (int i = 0; i < a.Buried; i++)
            {
                if (a.Dug(i)) mask |= 1 << i;
                data[1 + i] = (int)(a.Progress[i] * 100);
            }
            data[0] = mask;
            return data;
        }
        return ev.Fire?.ToArray() ?? Array.Empty<int>();
    }

    private void SendEnded(long peer, int id, bool success, string line)
    {
        if (Connected(peer)) RpcId(peer, MethodName.EndedOut, id, success, line);
        else if (!_networked) OnEnded(id, success, line);
    }

    private void Tell(long peer, string line)
    {
        if (Connected(peer)) RpcId(peer, MethodName.Told, line);
        else if (!_networked && peer == AdventureService.LocalPeer) Toast?.Invoke(line);
    }

    // ---- the client's asks ----------------------------------------------------------------------------------------------

    /// <summary>Client: asks to act at an event (the server checks where the body stands).</summary>
    public void AskHelp(int eventId, EventAct act, int arg)
    {
        if (Authority)
        {
            if (Me?.Invoke() is { } me && Help(AdventureService.LocalPeer, me.Global, eventId, act, arg) is { } why) Toast?.Invoke(why);
        }
        else RpcId(1, MethodName.RequestHelp, eventId, (int)act, arg);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestHelp(int eventId, int act, int arg)
    {
        if (!_server || !Enum.IsDefined((EventAct)act)) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (!Eligible(peer) || !_where.TryGetValue(peer, out var spot)) return;
        var at = GetNodeOrNull<FootPlayer>("../Players/" + peer)?.NetGlobal ?? new GlobalPos(spot.E, spot.N, 0);
        if (Help(peer, at, eventId, (EventAct)act, arg) is { } why) Tell(peer, why);
    }

    // ---- server -> client ---------------------------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PlanOut(int id, int kind, double e, double n, float ground, string canton, long seed, string near, string card) =>
        OnPlan(id, kind, e, n, ground, canton, (uint)seed, near, card);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void MovedOut(int id, double e, double n, float yaw, bool walking) => OnMoved(id, e, n, yaw, walking);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void StateOut(int id, int[] data) => OnState(id, data);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void EndedOut(int id, bool success, string line) => OnEnded(id, success, line);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Told(string line) => Toast?.Invoke(line);
}
