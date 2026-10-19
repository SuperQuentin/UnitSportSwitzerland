using Godot;
using UnitSport.Bots;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.BattleRoyale;

/// <summary>
/// Bots in a match (#837, server). At GO the field is topped up to <see cref="BrState.Bots"/> with
/// bots the server runs (<c>Bots/BotDirector</c>); each is an entrant like anyone, whose id is its
/// negative NPC id. What a client reports for its own player (death and its box, down, the zone's
/// damage) the server does here for its bots, through the same methods. Phase 1: bots appear on the
/// ground inside the first circle and stroll (<see cref="WanderBrain"/>); <c>BrBrain</c> (phase 2) makes
/// them board, loot, fight and run from the zone. Docs: <c>docs/notes/bots/</c>.
/// </summary>
public partial class BrManager
{
    /// <summary>
    /// The field a match is topped up to when nobody says otherwise (#837). <c>/br bots n|off</c> or
    /// <c>--brbots n</c> (0 = off) changes it.
    /// </summary>
    public const int DefaultBots = 20;
    /// <summary>
    /// Bots land within this share of the first circle's radius: the zone's first shrink closes faster
    /// than anyone runs, and a bot landed on the far edge of the circle dies in it (seen at every pace).
    /// </summary>
    private const float BotEdgeMargin = 0.5f;

    private int _botTarget = Core.CmdArgs.Int("--brbots") is int b && b >= 0 ? Math.Min(b, BotDirector.MaxBots) : DefaultBots;
    private BotDifficulty _botLevel = BotDifficulty.Normal;
    private bool _botsHooked;
    private double _botZoneTick;

    private static BotDirector? Director => BotDirector.Instance;

    private FootPlayer? BodyOf(long id) => _players?.GetNodeOrNull<FootPlayer>(PlayerReplication.NodeName(id));

    /// <summary>An entrant's body on the server (a player's proxy or a bot), for the bots' brains.</summary>
    public FootPlayer? BodyOfPeer(long id) => BodyOf(id);

    /// <summary>A bot carrying no dogtag yet, in a team with a mate out and not yet recalled, while recalls are open.</summary>
    private bool WantsTag(Bot bot) =>
        RecallOpen && _state.Find(bot.Id) is { Team: not 0 } me && !bot.Inventory.Contains(Items.ItemId.Dogtag)
        && _state.Entrants.Any(m => m.Team == me.Team && !m.Alive && !m.Recalled);

    /// <summary>
    /// The nearest Postauto stop to <paramref name="at"/> (world, on the stop's ground) worth a bot's walk with a
    /// dogtag, while recalls are open: the walk there and on into the circle it must reach (at <see cref="StopWalk"/>
    /// m/s, plus a margin), and any stretch past the current circle, cost it no more zone than it can take, keeping
    /// <see cref="StopKeep"/> health (the time past the circle's closing at the zone's damage, 1.5 × for safety), as
    /// a player runs for a stop through a little zone. A stop it could not afford sent it out and the zone sent it
    /// back, to and fro; a stop only inside the next circle was usually none (the four are spread over the first).
    /// Null when none can be had.
    /// </summary>
    public Vector3? NearestStop(Vector3 at, float health = 100f)
    {
        if (!RecallOpen || Origin == null) return null;
        var (e, n) = Origin.ToLv95(at);
        var here = new Vector2((float)(e - _state.AreaE), (float)(n - _state.AreaN));
        var z = _state.Zone().At(Now - _state.Started);
        var centre = z.Phase > 0 ? z.NextCentre : z.Centre;
        float radius = (z.Phase > 0 ? z.NextRadius : z.Radius) * 0.8f;
        // the time before the circle it must be in closes: the wait left, then about a minute of shrink
        double left = z.Phase == 0 ? double.PositiveInfinity : z.Left + (z.Shrinking ? 0 : 60);
        float dps = Math.Max(z.Dps, 3f) * 1.5f;
        // the zone it would stand in: out past the circle now and back (a stop outside it), and whatever of
        // the walk there and on into the circle it must reach runs past that circle's closing
        double ZoneSeconds((Vector2 At, float Alt) s)
        {
            double past = Math.Max(0f, s.At.DistanceTo(z.Centre) - z.Radius) * 2 / StopWalk;
            double walk = (s.At.DistanceTo(here) + Math.Max(0f, s.At.DistanceTo(centre) - radius)) / StopWalk + StopMargin;
            return past + Math.Max(0.0, walk - left);
        }
        var stops = Stops().Where(s => health - ZoneSeconds(s) * dps > StopKeep).ToList();
        if (stops.Count == 0) return null;
        var stop = stops.OrderBy(s => s.At.DistanceTo(here)).First();
        return Origin.ToWorld(_state.AreaE + stop.At.X, _state.AreaN + stop.At.Y, stop.Alt);
    }

    /// <summary>Why <see cref="NearestStop"/> has none (for <c>--botlog</c>): recalls, the zone, each stop's walk and the time left.</summary>
    public string StopReport(Vector3 at)
    {
        if (Origin == null) return "no origin";
        var (e, n) = Origin.ToLv95(at);
        var here = new Vector2((float)(e - _state.AreaE), (float)(n - _state.AreaN));
        var z = _state.Zone().At(Now - _state.Started);
        var centre = z.Phase > 0 ? z.NextCentre : z.Centre;
        float radius = (z.Phase > 0 ? z.NextRadius : z.Radius) * 0.8f;
        double left = z.Phase == 0 ? double.PositiveInfinity : z.Left + (z.Shrinking ? 0 : 60);
        return FormattableString.Invariant($"recall open {RecallOpen}, phase {z.Phase}, radius {z.Radius:F0}, left {left:F0} s, stops: ")
            + string.Join("; ", Stops().Select(s => FormattableString.Invariant(
                $"{s.At.DistanceTo(here):F0} m off, {Math.Max(0f, s.At.DistanceTo(z.Centre) - z.Radius):F0} m out, walk {(s.At.DistanceTo(here) + Math.Max(0f, s.At.DistanceTo(centre) - radius)) / StopWalk + StopMargin:F0} s")));
    }

    /// <summary>A bot's pace to and from a stop (it runs at 5.8 m/s; walls and detours on the way), and the margin, s.</summary>
    private const float StopWalk = 4.5f, StopMargin = 20f;
    /// <summary>The health a bot keeps when it walks through zone to a stop.</summary>
    private const float StopKeep = 45f;

    /// <summary>The last ping a human of <paramref name="team"/> made (world point, when, by whom), for the team's bots.</summary>
    public (Vector3 At, double When, long From)? HumanPing(int team) => _humanPings.TryGetValue(team, out var p) ? p : null;
    private readonly Dictionary<int, (Vector3 At, double When, long From)> _humanPings = new();

    /// <summary>
    /// GO, once teams are drawn (#837 phase 4): a squad's bots land as a group. A bot-only team flies to its
    /// leader's spot (the lowest id), a few metres apart, the mates jumping one after another; a team with a
    /// human jumps after the human and flies after them. Then every bot's moment is spaced again.
    /// </summary>
    private void PlanTeamDrops()
    {
        if (_state.TeamSize <= 1 || Director is not { } director || _state.Flight is not { } flight) return;
        var all = new List<BrBrain>();
        foreach (var team in _state.Entrants.Where(e => e.Team != 0).GroupBy(e => e.Team))
        {
            var bots = team.Where(e => e.Bot).OrderBy(e => -e.Peer).Select(e => director.Get(e.Peer)?.Brain as BrBrain)
                .Where(b => b != null).Cast<BrBrain>().ToList();
            if (bots.Count == 0) continue;
            if (team.FirstOrDefault(e => !e.Bot) is { } human)
            {
                for (int k = 0; k < bots.Count; k++)
                {
                    bots[k].JumpWith = human.Peer;
                    bots[k].JumpDelay = 0.8 * (k + 1);
                }
                continue;
            }
            var lead = bots[0];
            for (int k = 1; k < bots.Count; k++)
            {
                double a = k * Math.Tau / Math.Max(1, bots.Count - 1);
                if (lead.DropTarget is { } spot) bots[k].DropTarget = spot + new Vector3((float)Math.Cos(a), 0, (float)Math.Sin(a)) * 12f;
                bots[k].JumpAt = lead.JumpAt + k * MinJumpGap;
            }
            all.AddRange(bots);
        }
        // every bot jumping on its own clock (not after a human): never two together, as at boarding
        all.AddRange(_state.Entrants.Where(e => e.Bot && e.Team == 0).Select(e => director.Get(e.Peer)?.Brain as BrBrain).Where(b => b != null).Cast<BrBrain>());
        Space(all, flight.OpensAt + 0.2, flight.ClosesAt - 0.2);
    }

    /// <summary>The moments in their order, at least <see cref="MinJumpGap"/> apart; spread over the window when they do not fit.</summary>
    private static void Space(List<BrBrain> brains, double from, double to)
    {
        if (brains.Count * MinJumpGap > to - from) { SpreadJumps(brains, from, to); return; }
        double last = double.NegativeInfinity;
        foreach (var b in brains.OrderBy(b => b.JumpAt))
        {
            b.JumpAt = Math.Max(Math.Max(b.JumpAt, from), last + MinJumpGap);
            last = b.JumpAt;
        }
        if (last > to) SpreadJumps(brains, from, to);
    }

    /// <summary><c>/br bots [n|off] [easy|normal|hard]</c> (admin): the field bots top a match up to.</summary>
    private string BotsCommand(string[] words)
    {
        if (words.Length == 0)
            return _botTarget == 0 ? "No bots: /br bots <field size> [easy|normal|hard] tops matches up with bots."
                : $"Matches are topped up with {_botLevel.ToString().ToLowerInvariant()} bots to {_botTarget} players.";
        foreach (var w in words)
        {
            if (w.Equals("off", StringComparison.OrdinalIgnoreCase)) _botTarget = 0;
            else if (int.TryParse(w, out int n) && n >= 0) _botTarget = Math.Min(n, BotDirector.MaxBots);
            else if (BotProfile.ParseDifficulty(w) is { } d) _botLevel = d;
            else return "Usage: /br bots [field size|off] [easy|normal|hard]";
        }
        if (!_state.Running && _state.Phase != BrPhase.Idle)
        {
            _state.Bots = _botTarget;
            _state.BotLevel = (int)_botLevel;
            Push();
        }
        return _botTarget == 0 ? "Bots off." : $"Matches will be topped up with {_botLevel.ToString().ToLowerInvariant()} bots to {_botTarget} players.";
    }

    /// <summary>
    /// The match rules the server answers for the bodies it runs (#837): a bot entrant stays down once
    /// out, never regenerates, and goes down rather than out while a team-mate stands, as a client
    /// answers for its own player in <c>EnterMatch</c>. Everyone else's answer is the default.
    /// </summary>
    private void HookBots()
    {
        if (_botsHooked || Director is not { } director) return;
        _botsHooked = true;
        FootPlayer.StayDown = p => BotEntrant(p) is { Alive: true };
        FootPlayer.Regenerates = p => BotEntrant(p) is not { Alive: true };
        FootPlayer.CanBeDowned = p => BotEntrant(p) is { Alive: true } e && _state.MateStanding(e.Peer);
        // a fallen mate's dogtag, while it can still bring them back (#837 phase 4)
        BotSkills.AlsoWants = (bot, s) => s.Id == Items.ItemId.Dogtag && WantsTag(bot);
        director.Died += OnBotDied;
        director.Downed += OnBotDowned;
    }

    private BrEntrant? BotEntrant(FootPlayer p) =>
        p.Bot && _state.Phase == BrPhase.Playing && FootPlayer.NetId(p.Name) is long id ? _state.Find(id) : null;

    /// <summary>
    /// GO, before the zone is drawn: how many bots join, so the first circle is sized for the whole
    /// field. They are only spawned once the circle is known (<see cref="PlaceBots"/>).
    /// </summary>
    private int BotsAtGo() => Director == null ? 0 : Math.Min(BrState.BotsToAdd(_state.Entrants.Count, _state.Bots),
        BotDirector.MaxBots - Director.Count);

    /// <summary>
    /// GO, once the plane's line is known: <paramref name="count"/> bots aboard the cargo plane, each with
    /// the match kit, the BR brain and a landing spot inside the first circle (off water where the
    /// lattice tells) with the moment the plane passes nearest it. Clients hide them while they are aboard
    /// (an entrant not <c>Jumped</c>), as they hide players. A body is carried from its first frame: one
    /// spawned in the air with nothing holding it would wait for ground that is never built up there.
    /// </summary>
    private void BoardBots(int count)
    {
        if (count <= 0 || Director is not { } director || _state.Flight is not { } flight || Origin == null) return;
        HookBots();
        LoadRoads();
        var zone = _state.Zone();
        var centre = zone.CentreOf(0);
        float radius = zone.RadiusOf(0) * BotEdgeMargin;
        // --brbotspread m (checks): every landing spot within m of the circle's centre, so the bots meet
        if (Core.CmdArgs.Float("--brbotspread") is float spread && spread > 0) radius = Math.Min(radius, spread);
        var horizon = Horizon;
        var rng = new Random(_state.Seed ^ 0x5bd1e995);
        var taken = new HashSet<string>(_state.Entrants.Select(e => e.Name));
        var plane = PlaneFrame(flight, Now);
        var jumps = new List<double>();
        var brains = new List<BrBrain>();
        for (int i = 0; i < count; i++)
        {
            double e = 0, n = 0;
            for (int tries = 0; tries < 12; tries++)
            {
                double a = rng.NextDouble() * Math.Tau, r = radius * Math.Sqrt(rng.NextDouble());
                e = _state.AreaE + centre.X + Math.Cos(a) * r;
                n = _state.AreaN + centre.Y + Math.Sin(a) * r;
                if (!BrMapImage.Wet(horizon, e, n)) break;
            }
            var profile = BotProfile.For(_state.Seed, i, _botLevel, taken);
            if (director.Add(Origin.ToGlobal(plane.At + Vector3.Down * 1.2f), plane.Yaw, profile) is not { } bot) break;
            long id = bot.Id;
            _aboardBots.Add(id);
            bot.Body.Carrier = () => BotHold(id);
            bot.Inventory.Put(0, new Items.ItemStack(Items.ItemId.Knife, 1));
            bot.Inventory.Add(Items.ItemId.Bandage, 3);
            // where it lands, and when to jump for it: the plane's nearest pass, give or take a second
            var land = new Vector2((float)(e - _state.AreaE), (float)(n - _state.AreaN));
            double jumpAt = flight.OpensAt;
            float best = float.MaxValue;
            for (double t = flight.OpensAt; t <= flight.ClosesAt; t += 0.5)
                if (flight.At(t).DistanceTo(land) is var d && d < best) { best = d; jumpAt = t; }
            jumpAt = Math.Clamp(jumpAt + rng.NextDouble() * 2 - 1, flight.OpensAt + 0.2, flight.ClosesAt - 0.2);
            // never two out of the ramp together: at the plane's speed, bodies leaving as one hit each other
            // and crash in the air. Each takes the nearest free moment, MinJumpGap from every other.
            jumpAt = FreeMoment(jumps, jumpAt, flight.OpensAt + 0.2, flight.ClosesAt - 0.2);
            jumps.Add(jumpAt);
            var target = Origin.ToWorld(e, n, (float)BrMapImage.Height(horizon, e, n));
            var brain = new BrBrain(this, _crates, _state.Seed ^ (i * 7919)) { DropTarget = target, JumpAt = jumpAt };
            bot.Brain = brain;
            brains.Add(brain);
            _state.Entrants.Add(new BrEntrant { Peer = id, Name = profile.Name, Bot = true });
        }
        SpreadJumps(brains, flight.OpensAt + 0.2, flight.ClosesAt - 0.2);
        GD.Print($"[br] bots: {_state.Entrants.Count(x => x.Bot)} aboard ({_botLevel})");
    }

    /// <summary>Seconds between two bots leaving the ramp (55 m at cruise, more at a test pace).</summary>
    private const double MinJumpGap = 0.7;

    /// <summary>
    /// A field the doors cannot space <see cref="MinJumpGap"/> apart (99 bots into 48 s): FreeMoment gave a third of
    /// them the moment they wanted, together, and 18 crashed into each other in the air. Then the bots leave in the
    /// order they wanted, evenly over the doors' window (0.48 s apart for 99, 26 m at cruise, with the lateral spread).
    /// </summary>
    private static void SpreadJumps(List<BrBrain> brains, double from, double to)
    {
        if (brains.Count * MinJumpGap <= to - from) return;
        double step = (to - from) / brains.Count;
        int k = 0;
        foreach (var brain in brains.OrderBy(b => b.JumpAt)) brain.JumpAt = from + (k++ + 0.5) * step;
    }

    /// <summary>The moment nearest <paramref name="wanted"/> in [from, to] at least <see cref="MinJumpGap"/> from each taken one.</summary>
    private static double FreeMoment(List<double> taken, double wanted, double from, double to)
    {
        for (int k = 0; k < 200; k++)
            foreach (double t in new[] { wanted + k * MinJumpGap * 0.5, wanted - k * MinJumpGap * 0.5 })
                if (t >= from && t <= to && taken.All(x => Math.Abs(x - t) >= MinJumpGap)) return t;
        return wanted;   // more bots than the doors have moments: as wanted, with the lateral spread
    }

    /// <summary>
    /// The match area's streets as a graph (#837): bots crossing a town walk them (<c>BrBrain</c>), loaded
    /// off the main thread at GO from the same road tiles everyone draws. Null until it is in.
    /// </summary>
    public Gpx.RoadNetwork? Roads => _roads is { IsCompletedSuccessfully: true } done ? done.Result : null;
    private Task<Gpx.RoadNetwork>? _roads;

    private void LoadRoads()
    {
        if (_source == null) return;
        double h = _state.Side * 0.5;
        var source = _source;
        double e0 = _state.AreaE - h, n0 = _state.AreaN - h, e1 = _state.AreaE + h, n1 = _state.AreaN + h;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _roads = Task.Run(async () =>
        {
            var net = await Gpx.RoadNetwork.LoadAsync(source, e0, n0, e1, n1);
            net.BuildWalkGraph();
            GD.Print($"[br] bots: the streets are in, {net.Edges.Count} edges in {clock.ElapsedMilliseconds} ms");
            return net;
        });
    }

    /// <summary>The bots still in the plane's hold.</summary>
    private readonly HashSet<long> _aboardBots = new();

    /// <summary>Where the hold keeps a bot: as <see cref="Hold"/> keeps the local player, while it is aboard.</summary>
    private (Vector3 At, float Yaw, Vector3 Velocity)? BotHold(long id)
    {
        if (!_aboardBots.Contains(id) || _state.Flight is not { } flight || Origin == null) return null;
        var f = PlaneFrame(flight, Now);
        return (f.At + Vector3.Down * 1.2f, f.Yaw, f.Velocity);
    }

    /// <summary>
    /// A bot jumps (its brain's call, or pushed out when the doors close): out of the ramp into a
    /// wingsuit, as a player's <see cref="JumpOut"/>, a little to one side so twenty never leave as one.
    /// </summary>
    public bool BotJump(Bot bot, bool pushed = false)
    {
        if (!_aboardBots.Contains(bot.Id) || _state.Flight is not { } flight || Origin == null) return false;
        double now = Now;
        if (!pushed && !flight.DoorsOpen(now)) return false;
        var f = PlaneFrame(flight, now);
        // one side of the ramp, then the other, drifting outwards: a bot slows in the wingsuit at once, and the
        // next one out half a second later at plane speed caught it up from behind (air crashes in a 100 field)
        _jumpSide = -_jumpSide;
        var right = new Basis(Vector3.Up, f.Yaw) * Vector3.Right;
        var exit = f.At + new Basis(Vector3.Up, f.Yaw) * BrPlane.Ramp + Vector3.Down * 2f + right * _jumpSide * (float)(4 + _rng.NextDouble() * 4);
        var forward = f.Velocity.LengthSquared() > 1f ? f.Velocity.Normalized() : new Basis(Vector3.Up, f.Yaw) * Vector3.Forward;
        _aboardBots.Remove(bot.Id);
        bot.Body.Leap(exit, forward * Math.Min(flight.Speed, ExitSpeed) + right * _jumpSide * 2f + Vector3.Down * 4f, RideKind.Wingsuit);
        if (_state.Find(bot.Id) is { } e) e.Jumped = true;
        _pushSoon = true;   // one state message for every bot that jumped this frame
        GD.Print($"[br] {bot.Body.Name} \"{bot.Profile.Name}\" {(pushed ? "pushed out" : "jumped")} {flight.ClosesAt - now:F0} s before the doors close");
        return true;
    }

    private bool _pushSoon;
    private int _jumpSide = 1;

    /// <summary>ServerTick: the doors have closed, out with every bot still aboard; the jumps' state goes out once.</summary>
    private void BotPlane()
    {
        if (_state.Flight is { } flight && Now >= flight.ClosesAt && _aboardBots.Count > 0 && Director is { } director)
            foreach (long id in _aboardBots.ToList())
                if (director.Get(id) is { } bot) BotJump(bot, pushed: true);
                else _aboardBots.Remove(id);
        if (_pushSoon)
        {
            _pushSoon = false;
            Push();
        }
    }

    /// <summary>The ground's height under a world point from the terrain lattice, for a bot opening its canopy over unloaded ground.</summary>
    public float? GroundAt(Vector3 world)
    {
        if (Origin == null || Horizon is not { } horizon) return null;
        var (e, n) = Origin.ToLv95(world);
        return (float)BrMapImage.Height(horizon, e, n);
    }

    private HorizonIndex? Horizon => _horizon is { IsCompletedSuccessfully: true } done ? done.Result : null;

    /// <summary>The match is over: its bots go for everyone.</summary>
    private void RemoveBots(BrState was)
    {
        _aboardBots.Clear();
        _roads = null;
        if (Director is not { } director) return;
        foreach (var e in was.Entrants.Where(e => e.Bot)) director.Remove(e.Peer);
    }

    /// <summary>What a client's <c>OnLocalDied</c> reports for its player, for a bot: its box, then out.</summary>
    private void OnBotDied(Bot bot, long killer, DamageCause cause)
    {
        if (_state.Phase != BrPhase.Playing || _state.Find(bot.Id) is not { Alive: true } e) return;
        var stacks = Enumerable.Range(0, Items.Inventory.Size).Select(i => bot.Inventory[i])
            .Where(s => !s.IsEmpty && s.Data == null).ToList();
        DeathBox(e, stacks.Select(s => (int)s.Id).ToArray(), stacks.Select(s => s.Count).ToArray());
        bot.Inventory.Clear();
        Eliminate(e, killer, OutOf(cause, killer));
    }

    private void OnBotDowned(Bot bot, long attacker)
    {
        if (_state.Phase == BrPhase.Playing && _state.Find(bot.Id) is { Alive: true, Downed: false } e) Downed(e, attacker);
    }

    /// <summary>Why someone left the match, from what their body died of (the client's mapping, shared).</summary>
    public static BrOut OutOf(DamageCause cause, long killer) => cause switch
    {
        DamageCause.Zone => BrOut.Zone,
        DamageCause.Fall or DamageCause.Crash => BrOut.Fell,
        _ => killer != 0 ? BrOut.Killed : BrOut.Other,
    };

    /// <summary>The zone hurts the bots too, every half second, as each client hurts its own player.</summary>
    private void BotZone(ZoneSchedule zone, double delta)
    {
        _botZoneTick += delta;
        if (_botZoneTick < 0.5) return;
        double dt = _botZoneTick;
        _botZoneTick = 0;
        if (Origin == null || zone.At(Now - _state.Started) is not { Dps: > 0 } z) return;
        foreach (var e in _state.Entrants.Where(e => e.Bot && e.Alive))
            if (BodyOf(e.Peer) is { Eliminated: false } body)
            {
                var (pe, pn) = Origin.ToLv95(body.GlobalPosition);
                if (z.Outside(new Vector2((float)(pe - _state.AreaE), (float)(pn - _state.AreaN))))
                    body.TakeDamage(z.Dps * (float)dt, 0, DamageCause.Zone);
            }
    }

    /// <summary>
    /// No player of the match is connected any more (#837): nobody is left to play or watch, so the bots
    /// do not play on. With a player out but still here, they do: <c>BrBrain</c> fights, and the match
    /// plays out to a winner while the players spectate (decided on #837).
    /// </summary>
    private bool NoPlayerLeft => _state.Entrants.Any(e => !e.Bot)
        && !_state.Entrants.Any(e => !e.Bot && Multiplayer.GetPeers().Contains((int)e.Peer));
}
