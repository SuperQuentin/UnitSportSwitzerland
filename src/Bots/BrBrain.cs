using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnitSport.BattleRoyale;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Vehicles;

namespace UnitSport.Bots;

/// <summary>
/// A Battle Royale bot's mind (#837, phase 2). Each thought it looks round (<see cref="BotSenses"/>),
/// sums up what it knows (<see cref="BrSituation"/>: its health and pack, who it sees and hears, the
/// zone, a crate in view), lets <see cref="BrTactics"/> choose the goal, and carries the goal out with
/// the shared skills (<see cref="BotSkills"/>, <see cref="BotHands"/>):
/// <list type="bullet">
/// <item><b>Fight</b>: face the nearest enemy, wait out its reaction time, fire with its profile's aim
/// error (<see cref="BrTactics.AimError"/>), close in to its gun's band or rush with a blade, strafe
/// when close; shot at by someone it cannot see, it turns to where the shot came from.</item>
/// <item><b>Finish</b> a downed enemy; <b>Heal</b> and put a <b>Vest</b> on when it feels safe.</item>
/// <item><b>Loot</b> the nearest crate in view it has not emptied yet, taking what it wants.</item>
/// <item><b>Zone</b>: into the safe circle, running; <b>Investigate</b> a shot; else <b>Roam</b> inside the circle.</item>
/// </list>
/// It reads the match only through <see cref="BrManager"/>'s state (the zone, who is hostile) and the
/// crates' server list, as a player reads the HUD and the world.
/// </summary>
public sealed class BrBrain : IBotBrain
{
    private readonly BrManager _br;
    private readonly BrCrates? _crates;
    private readonly Random _rng;
    private readonly HashSet<long> _emptied = new();
    private double _lastEnemyAt = -999;
    private Vector3? _roam;
    private long _target;
    private double _strafeUntil;
    private int _strafeSide = 1;
    public BrGoal Goal { get; private set; }
    private string _status = "";
    public string Status => _status;
    /// <summary><c>--brbotsharmless</c> on a server: bots play the match but never strike (checks only).</summary>
    private static readonly bool Harmless = Core.CmdArgs.Has("--brbotsharmless");
    /// <summary>Where it means to land (world), chosen when it boarded.</summary>
    public Vector3? DropTarget { get; set; }
    /// <summary>
    /// A squad's human (#837 phase 4): this bot jumps <see cref="JumpDelay"/> s after they do and flies after
    /// them, instead of at <see cref="JumpAt"/> to <see cref="DropTarget"/>. 0: none.
    /// </summary>
    public long JumpWith { get; set; }
    public double JumpDelay { get; set; }
    private double _withJumpedAt;
    /// <summary>The server clock at which it jumps: the plane's nearest pass to <see cref="DropTarget"/>.</summary>
    public double JumpAt { get; set; }

    /// <summary>The canopy opens this high over the ground (it takes a second or two to slow), or lower once over the spot.</summary>
    private const float OpenAt = 300f, OpenOverSpot = 180f;
    /// <summary>
    /// The last metres under the canopy are flown on the brakes: a soft touchdown (5 m/s, under the
    /// canopy's 9 m/s crash speed). From 30 m: the height it reads is the coarse ground or the 100 m
    /// lattice far from its collision, metres off (seen: a bot landing at 9.4 m/s, flared too late).
    /// </summary>
    private const float FlareAt = 30f;

    public BrBrain(BrManager br, BrCrates? crates, int seed)
    {
        _br = br;
        _crates = crates;
        _rng = new Random(seed);
    }

    /// <summary>The match timeline, simulated seconds like every stamp in the match state (#855).</summary>
    private static double SimNow => Net.ClockSync.SimNow;

    public double Think(Bot bot, double now)
    {
        var body = bot.Body;
        bot.Intent.FireAt = null;
        if (body.Eliminated || body.Down != 0)
        {
            bot.Intent = BotIntent.Stand;
            // down in a squad (#837 phase 4): crawl to the nearest standing mate, who can pick it up
            if (body.Downed && _br.State.Find(bot.Id) is { Team: not 0 } downedMe && TeamView(bot, downedMe).Nearest is { } helper)
            {
                BotSkills.MoveTo(bot, helper.GlobalPosition, run: false, 2.5f, bot.World.Space, now);
                return 0.3;
            }
            return 0.5;
        }
        // the drop first: the hold, then the wingsuit and the canopy (#837 phase 2)
        if (Drop(bot)) return 0.1;
        bot.Intent.Canopy = false;
        bot.Intent.Steer = Vector2.Zero;
        var space = bot.World.Space;
        bot.Senses.Look(bot, bot.World.Bodies(), space, now);
        // at the wheel (#837 phase 3): drive on, or brake and get out (arrived, stuck, or an enemy close)
        if (BotDriving.Mounted(bot)) return Driving(bot, now);
        // a car or a bike seen on the way is remembered, as a player remembers where one stood
        if (now >= _lookCarAt)
        {
            _lookCarAt = now + 1.5;
            if (BotDriving.Spot(bot, BotSenses.SightRange) is { } seen
                && (_knownCar is not { } known || !GodotObject.IsInstanceValid(known)
                    || seen.GlobalPosition.DistanceTo(bot.Body.GlobalPosition) < known.GlobalPosition.DistanceTo(bot.Body.GlobalPosition)))
                _knownCar = seen;
        }
        var s = Situation(bot, now, out var enemy, out var downed, out var crate, out var zone);
        var team = _br.State.Find(bot.Id) is { Team: not 0 } myEntry ? TeamView(bot, myEntry) : default;
        if (team.Squad) s = Squad(bot, s, team, enemy, now);
        _status = FormattableString.Invariant($"goal {Goal} inside {zone.Inside:F2} out {s.OutsideBy:F0} run {s.ZoneRunNeeded:F0} enemy {s.EnemyDistance:F0} crate {s.CrateDistance:F0} stuck {bot.Nav.Stuck} open {bot.Nav.LastOpen:F1} route {(_route == null ? "-" : $"{_routeNext}/{_route.Count}")} roads {(_br.Roads != null)} follow {bot.Nav.Following} car {(_car != null && GodotObject.IsInstanceValid(_car) ? _car.Name : "-")}");
        var goal = BrTactics.Choose(s, Goal);
        if (goal != Goal) GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" {Goal} -> {goal}");
        Goal = goal;
        if (goal is not (BrGoal.Fight or BrGoal.Finish)) bot.Intent.FaceYaw = null;
        if (goal != BrGoal.Zone) _zoneGoal = null;
        if (goal != BrGoal.Revive) _reviveWho = 0;

        switch (goal)
        {
            case BrGoal.Zone:
                // a steady goal: Safe follows the bot (the nearest spot well inside), and a goal that moves
                // every thought restarted MoveTo's headway clock, so a bot circling in a courtyard was never
                // found stuck (seen: three bots running round 5-10 m patches until the zone killed them)
                if (_zoneGoal is not { } zg || Flat(zg - zone.Safe) > 40f) _zoneGoal = zone.Safe;
                GoTo(bot, _zoneGoal.Value, run: true, 4f, space, now);
                return 0.3;
            case BrGoal.Fight:
                Fight(bot, enemy, s, space, now);
                return 0.12;
            case BrGoal.Finish when downed is { } d:
                Engage(bot, d, now, space, rush: true);
                return 0.12;
            case BrGoal.Heal:
                bot.Intent.Wish = Vector3.Zero;
                if (bot.UseUntil <= 0)
                    BotHands.Use(bot, body.Health < 35f && bot.Inventory.CountPlain(ItemId.FirstAidKit) > 0 ? ItemId.FirstAidKit
                        : bot.Inventory.CountPlain(ItemId.Bandage) > 0 ? ItemId.Bandage : ItemId.FirstAidKit);
                return 0.3;
            case BrGoal.Vest:
                bot.Intent.Wish = Vector3.Zero;
                if (bot.UseUntil <= 0) BotHands.Use(bot, ItemId.ArmorVest);
                return 0.3;
            case BrGoal.Loot when crate is { } c && _crates != null:
                Loot(bot, c, space, now);
                return 0.25;
            case BrGoal.Investigate when bot.Senses.Heard is { } heard:
                if (GoTo(bot, heard.At, run: false, 6f, space, now)) bot.Senses.Heard = null;
                return 0.3;
            case BrGoal.Regroup when team.LeaderBody is { } leader:
                GoTo(bot, leader.GlobalPosition, run: true, 12f, space, now);
                return 0.3;
            case BrGoal.Assist when team.Fight is { } fight:
                GoTo(bot, fight, run: true, 20f, space, now);
                return 0.25;
            case BrGoal.Revive when team.DownedBody is { } hurt && team.Downed is { } who:
                Revive(bot, hurt, who.Peer, space, now);
                return 0.2;
            case BrGoal.Ping when _pingAt is { } ping:
                // there: a look round for a few seconds (the human pointed at something), then back to the team
                if (GoTo(bot, ping, run: true, 6f, space, now))
                {
                    if (_pingHereUntil == 0) _pingHereUntil = now + PingLinger;
                    else if (now >= _pingHereUntil) { _pingAt = null; _pingHereUntil = 0; }
                }
                return 0.3;
            case BrGoal.Recall when _br.NearestStop(body.GlobalPosition, body.Health) is { } stop:
                if (GoTo(bot, stop, run: true, 3.5f, space, now) && _br.ServerRecall(bot.Id))
                    GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" used a dogtag at a Postauto stop");
                return 0.3;
            default:
                // a follower keeps near its leader, loot and fights aside: a short walk back from 25 m
                if (team.LeaderBody is { } lead && lead.GlobalPosition.DistanceTo(body.GlobalPosition) > 25f)
                    GoTo(bot, lead.GlobalPosition, run: false, 10f, space, now);
                else Roam(bot, zone, space, now);
                return 0.4;
        }
    }

    // ---- squads (#837 phase 4) ---------------------------------------------------------------------

    /// <summary>
    /// What a squad bot knows of its team: the leader (the standing human first, else the standing bot with the
    /// lowest number; never itself), the nearest downed mate, a mate in a fight (a bot fighting, a player
    /// shot at in the last 5 s), the nearest standing mate, and whether a human is in it.
    /// </summary>
    private readonly record struct TeamSight(bool Squad, FootPlayer? LeaderBody, BrEntrant? Downed, FootPlayer? DownedBody,
        Vector3? Fight, FootPlayer? Nearest, bool HasHuman);

    private TeamSight TeamView(Bot bot, BrEntrant me)
    {
        var pos = bot.Body.GlobalPosition;
        BrEntrant? leader = null;
        BrEntrant? downed = null;
        FootPlayer? downedBody = null, nearest = null, leaderBody = null;
        Vector3? fight = null;
        float downedD = float.MaxValue, nearestD = float.MaxValue, fightD = float.MaxValue;
        bool human = false;
        foreach (var m in _br.State.Entrants)
        {
            if (m.Team != me.Team || !m.Alive) continue;
            if (!m.Bot) human = true;
            // the leader among the standing, itself included: a human, else the lowest-numbered bot (the id nearest 0)
            if (!m.Downed && (leader == null || (!m.Bot && leader.Bot) || (m.Bot == leader.Bot && (m.Bot ? m.Peer > leader.Peer : m.Peer < leader.Peer))))
                leader = m;
            if (m.Peer == me.Peer || _br.BodyOfPeer(m.Peer) is not { } b) continue;
            float d = b.GlobalPosition.DistanceTo(pos);
            if (m.Downed) { if (d < downedD) (downed, downedBody, downedD) = (m, b, d); continue; }
            if (d < nearestD) (nearest, nearestD) = (b, d);
            bool fighting = m.Bot
                ? BotDirector.Instance?.Get(m.Peer)?.Brain is BrBrain { Goal: BrGoal.Fight or BrGoal.Finish }
                : Core.GameClock.Now - b.LastAttackedAt < 5;
            if (fighting && d < fightD) (fight, fightD) = (b.GlobalPosition, d);
        }
        if (leader != null && leader.Peer != me.Peer) leaderBody = _br.BodyOfPeer(leader.Peer);
        return new TeamSight(true, leaderBody, downed, downedBody, fight, nearest, human);
    }

    private Vector3? _pingAt;
    private double _pingSeen;
    private long _reviveWho;
    private double _reviveFrom;
    private double _pingOutAt;
    private double _stopReportAt;
    private double _pingHereUntil;
    /// <summary>Seconds a bot stays at a human's ping once there.</summary>
    private const double PingLinger = 8;
    private readonly Dictionary<long, double> _pingedEnemy = new();
    /// <summary>A human's ping is followed this long, unless reached.</summary>
    private const double PingFollow = 60;

    /// <summary>The squad's part of what it knows; and, with a human in the team, it pings an enemy it sees for them.</summary>
    private BrSituation Squad(Bot bot, BrSituation s, TeamSight team, Sighting? enemy, double now)
    {
        var pos = bot.Body.GlobalPosition;
        // a human's new ping: go and look
        if (_br.State.Find(bot.Id) is { } me && _br.HumanPing(me.Team) is { } ping && ping.When > _pingSeen)
        {
            _pingSeen = ping.When;
            _pingAt = ping.At;
            _pingHereUntil = 0;
        }
        if (_pingAt != null && SimNow - _pingSeen > PingFollow) _pingAt = null;
        // for a human mate: an enemy seen for the first time in a while, at most one ping every 10 s
        if (team.HasHuman && enemy is { } e && e.Distance < 250f && now - _pingOutAt > 10
            && (!_pingedEnemy.TryGetValue(e.Id, out double at) || now - at > 30) && bot.Body.Origin is { } origin)
        {
            var (pe, pn) = origin.ToLv95(e.At);
            if (_br.ServerPing(bot.Id, pe, pn, e.At.Y))
            {
                _pingOutAt = now;
                _pingedEnemy[e.Id] = now;
                GD.Print($"[bots] {bot.Body.Name} \"{bot.Profile.Name}\" pinged an enemy {e.Distance:F0} m off");
            }
        }
        float Dist(Vector3? p) => p is { } v ? v.DistanceTo(pos) : float.PositiveInfinity;
        if (bot.Inventory.Contains(ItemId.Dogtag) && _br.NearestStop(pos, bot.Body.Health) == null && now >= _stopReportAt)
        {
            _stopReportAt = now + 10;
            GD.Print($"[bots] {bot.Body.Name} carries a dogtag, no stop: {_br.StopReport(pos)}");
        }
        return s with
        {
            LeaderDistance = team.LeaderBody is { } l ? l.GlobalPosition.DistanceTo(pos) : float.PositiveInfinity,
            MateDownedDistance = team.DownedBody is { } d ? d.GlobalPosition.DistanceTo(pos) : float.PositiveInfinity,
            MateFightDistance = Dist(team.Fight),
            CarriesTag = bot.Inventory.Contains(ItemId.Dogtag),
            StopDistance = Dist(_br.NearestStop(pos, bot.Body.Health)),
            PingDistance = Dist(_pingAt),
        };
    }

    /// <summary>To the downed mate, then the hold: <see cref="BrManager.ReviveSeconds"/> beside them, still, and the server stands them up.</summary>
    private void Revive(Bot bot, FootPlayer hurt, long who, PhysicsDirectSpaceState3D space, double now)
    {
        if (!GoTo(bot, hurt.GlobalPosition, run: true, 2.2f, space, now)) { _reviveWho = 0; return; }
        bot.Intent.Wish = Vector3.Zero;
        bot.Intent.FaceYaw = Mathf.Atan2(-(hurt.GlobalPosition.X - bot.Body.GlobalPosition.X), -(hurt.GlobalPosition.Z - bot.Body.GlobalPosition.Z));
        if (_reviveWho != who) { _reviveWho = who; _reviveFrom = now; }
        if (now - _reviveFrom < BrManager.ReviveSeconds) return;
        _reviveWho = 0;
        if (_br.ServerRevive(bot.Id, who)) GD.Print($"[bots] {bot.Body.Name} \"{bot.Profile.Name}\" revived a mate");
    }

    // ---- the drop ---------------------------------------------------------------------------------

    /// <summary>
    /// Aboard: jumps at its time. In the wingsuit: faces its landing spot and keeps trim, dives when it
    /// would overshoot, opens the canopy high (<see cref="OpenAt"/>, lower once over the spot). Under the
    /// canopy: turns towards the spot, brakes the last metres. False once it is on its feet.
    /// </summary>
    private bool Drop(Bot bot)
    {
        var body = bot.Body;
        var me = _br.State.Find(bot.Id);
        if (me is { Jumped: false })
        {
            bot.Intent = BotIntent.Stand;
            if (JumpWith != 0)
            {
                // after its human, a beat apart from the other mates (pushed out at the doors if they never go)
                if (_withJumpedAt == 0 && _br.State.Find(JumpWith) is { Jumped: true }) _withJumpedAt = SimNow;
                if (_withJumpedAt > 0 && SimNow >= _withJumpedAt + JumpDelay) _br.BotJump(bot);
            }
            else if (SimNow >= JumpAt) _br.BotJump(bot);
            return true;
        }
        var ride = body.Ride;
        if (ride is not (Player.RideKind.Wingsuit or Player.RideKind.Parachute)) return false;
        bot.Intent.Wish = Vector3.Zero;
        bot.Intent.FireAt = null;
        var pos = body.GlobalPosition;
        // after its human: where they are now, a few metres to the side (they choose where the team lands)
        if (JumpWith != 0 && _br.BodyOfPeer(JumpWith) is { } human && _br.State.Find(JumpWith) is { Alive: true })
            DropTarget = human.GlobalPosition + new Vector3(Mathf.Cos((float)JumpDelay * 2.1f), 0, Mathf.Sin((float)JumpDelay * 2.1f)) * 10f;
        var spot = DropTarget ?? pos;
        var to = new Vector3(spot.X - pos.X, 0, spot.Z - pos.Z);
        float dist = to.Length();
        float desired = dist > 1f ? Mathf.Atan2(-to.X, -to.Z) : body.Flight.Yaw;
        // its height over the ground: the loaded ground under it, else the lattice (no tile loaded that far)
        float lattice = _br.GroundAt(pos) is { } g ? pos.Y - g : 999f;
        float height = Mathf.Min(body.Clearance < 900f ? body.Clearance : 999f, lattice);

        if (ride == Player.RideKind.Wingsuit)
        {
            bot.Intent.FaceYaw = desired;
            // trim glides ~2.7:1; well past the spot's glide, dive to lose height faster
            bot.Intent.Pitch = dist < height * 1.2f ? -0.9f : -0.35f;
            bot.Intent.Steer = Vector2.Zero;
            if (height < OpenAt || (dist < 150f && height < OpenAt + 250f)) bot.Intent.Canopy = true;
            return true;
        }
        // the canopy: turn towards the spot (the stick's x turns right, lowering the yaw), brake to land
        bot.Intent.Canopy = true;
        bot.Intent.FaceYaw = null;
        float turn = Mathf.Clamp(-Mathf.AngleDifference(body.Flight.Yaw, desired) * 1.5f, -1f, 1f);
        float brake = height < FlareAt ? 1f : dist < 20f && height > 60f ? 0.6f : 0f;   // over the spot: slow, drop
        // low, over ground whose collision is not built yet (the server builds it only round bots, and late):
        // circle on the brakes until it is, or it lands inside a building whose walls appear round it
        if (height < 60f && body.Terrain is { } terrain && !(terrain.HasCollisionAt(pos) && terrain.BuildingCollisionDoneAt(pos)))
        {
            bot.Intent.Steer = new Vector2(1f, 1f);
            return true;
        }
        bot.Intent.Steer = new Vector2(dist > 8f ? turn : 0f, brake);
        return true;
    }

    // ---- what it knows ----------------------------------------------------------------------------

    /// <param name="Safe">A point well inside the circle it must reach (world).</param>
    /// <param name="Inside">How far inside that circle it stands, as a share of its radius (negative outside).</param>
    private readonly record struct ZoneView(Vector3 Safe, Vector2 Centre, float Radius, float Inside, Vector3 Roam);

    private BrSituation Situation(Bot bot, double now, out Sighting? enemy, out Sighting? downed, out Crate? crate, out ZoneView zone)
    {
        var body = bot.Body;
        var state = _br.State;
        var me = state.Find(bot.Id);
        bool Hostile(long id) => me != null && state.Find(id) is { Alive: true } e && BrState.Hostile(me, e);

        // the nearest enemy standing, and the nearest one down (#475): plain loops, every thought
        enemy = null;
        downed = null;
        foreach (var x in bot.Senses.Seen)
        {
            if (!Hostile(x.Id)) continue;
            if (!x.Downed) { if (enemy is not { } e0 || x.Distance < e0.Distance) enemy = x; }
            else if (x.Body.Down == 2 && (downed is not { } d0 || x.Distance < d0.Distance)) downed = x;
        }
        if (enemy != null) _lastEnemyAt = now;

        var gun = BotHands.Pick(bot) is var (_, w) && !w.Melee ? w : null;
        int rounds = gun == null ? 0 : gun.Ammo == ItemId.None ? 99 : bot.Inventory.CountPlain(gun.Ammo);
        int meds = bot.Inventory.CountPlain(ItemId.Bandage) + bot.Inventory.CountPlain(ItemId.FirstAidKit);

        // the zone, in this match's metres east/north of the region's centre
        zone = new ZoneView(body.GlobalPosition, Vector2.Zero, float.MaxValue, 1f, body.GlobalPosition);
        float outsideBy = 0f, runNeeded = 0f;
        if (body.Origin is { } origin && state.Phase == BrPhase.Playing)
        {
            var z = state.Zone().At(SimNow - state.Started);
            var (pe, pn) = origin.ToLv95(body.GlobalPosition);
            var at = new Vector2((float)(pe - state.AreaE), (float)(pn - state.AreaN));
            // the circle it must be in: the next one once shown, else the current one
            var centre = z.Phase > 0 ? z.NextCentre : z.Centre;
            float radius = z.Phase > 0 ? z.NextRadius : z.Radius;
            var safeAt = centre + (at - centre).LimitLength(radius * 0.6f);
            // somewhere to wander to, always inside the circle it must be in; while looting (phase 0),
            // in its inner half: the first circle shown next is anywhere inside it
            double a = _rng.NextDouble() * Math.Tau, r = radius * (z.Phase == 0 ? 0.45 : 0.7) * Math.Sqrt(_rng.NextDouble());
            var roamAt = centre + new Vector2((float)(Math.Cos(a) * r), (float)(Math.Sin(a) * r));
            float inside = radius > 1f ? 1f - at.DistanceTo(centre) / radius : 1f;
            zone = new ZoneView(origin.ToWorld(state.AreaE + safeAt.X, state.AreaN + safeAt.Y, body.GlobalPosition.Y), z.Centre, z.Radius,
                inside, origin.ToWorld(state.AreaE + roamAt.X, state.AreaN + roamAt.Y, body.GlobalPosition.Y));
            if (z.Dps > 0f && z.Outside(at)) outsideBy = -z.DistanceToEdge(at);
            if (z.Phase > 0) runNeeded = BrTactics.ZoneRunNeeded(at.DistanceTo(centre) - radius, (float)z.Left + (z.Shrinking ? 0f : 60f));
            // once on its way to the circle it keeps going until well inside (30 % of the radius), or it
            // stops at the very edge, wanders back out and turns round again
            if (Goal == BrGoal.Zone && inside < 0.3f) runNeeded = Math.Max(runNeeded, 1f);
        }

        // a crate in view it has not emptied, landed, not locked: within a few steps, or with nothing
        // solid between its eyes and the crate (the nearest few are tested, a ray each)
        crate = null;
        float crateDistance = float.PositiveInfinity;
        bool crateHasTag = false;
        if (_crates != null)
        {
            _near.Clear();
            foreach (var c in _crates.All)
            {
                if (_emptied.Contains(c.Id) || c.Locked || SimNow < c.LandsAt || _crates.WorldOf(c, body.GlobalPosition.Y) is not { } at) continue;
                float d = new Vector2(at.X - body.GlobalPosition.X, at.Z - body.GlobalPosition.Z).Length();
                if (d <= BrTactics.CrateDraw) _near.Add((c, at, d));
            }
            _near.Sort((x, y) => x.D.CompareTo(y.D));
            // a fallen mate's box with their dogtag (squads, #837 phase 4): the team knows where they fell
            if (BotSkills.AlsoWants?.Invoke(bot, new ItemStack(ItemId.Dogtag, 1)) == true)
                foreach (var c in _crates.All)
                    if (c.Style == CrateStyle.DeathBox && !_emptied.Contains(c.Id) && c.Stacks().Any(st => st.Id == ItemId.Dogtag)
                        && _crates.WorldOf(c, body.GlobalPosition.Y) is { } tagAt && Flat(tagAt - body.GlobalPosition) is var td && td <= BrTactics.TagDraw
                        && td < crateDistance)
                    {
                        crate = c;
                        crateDistance = td;
                        crateHasTag = true;
                    }
            // the crate it is already walking to stays its crate, seen or not, until it is emptied
            foreach (var (c, _, d) in _near)
                if (crate == null && c.Id == _lootCrate) { crate = c; crateDistance = d; }
            for (int i = 0; crate == null && i < Math.Min(_near.Count, 3); i++)
            {
                var (c, at, d) = _near[i];
                if (d > CrateKnown && !InSight(bot, at + Vector3.Up * 0.6f)) continue;
                crate = c;
                crateDistance = d;
            }
            _lootCrate = crate?.Id ?? 0;
        }

        float heard = bot.Senses.Heard is { } h && now - h.When < 20 && Hostile(h.Who) ? body.GlobalPosition.DistanceTo(h.At) : float.PositiveInfinity;
        return new BrSituation(
            Health: body.Health, Armor: body.Armor, HasGun: gun != null, Rounds: rounds, GunRange: gun?.Range ?? 0f, Meds: meds,
            HasVest: bot.Inventory.Contains(ItemId.ArmorVest),
            EnemyDistance: enemy?.Distance ?? float.PositiveInfinity, DownedEnemyDistance: downed?.Distance ?? float.PositiveInfinity,
            HeardDistance: heard, HurtRecently: Core.GameClock.Now - body.LastAttackedAt < 3 && Hostile(body.LastAttacker),
            SecondsSinceEnemy: (float)(now - _lastEnemyAt), OutsideBy: outsideBy, ZoneRunNeeded: runNeeded,
            CrateDistance: crateDistance, Boldness: bot.Profile.Boldness, LootGreed: bot.Profile.LootGreed, CrateHasTag: crateHasTag);
    }

    /// <summary>A crate this close is noticed whatever stands between (it walked past it, it heard it).</summary>
    private const float CrateKnown = 25f;
    private readonly List<(Crate C, Vector3 At, float D)> _near = new();
    private long _lootCrate;

    /// <summary>Nothing solid between its eyes and <paramref name="at"/> (the server's collision round it).</summary>
    private static bool InSight(Bot bot, Vector3 at)
    {
        return BotRays.Clear(bot.World.Space, bot.Body.EyePosition, at, bot.Body.GetRid());
    }

    // ---- what it does -----------------------------------------------------------------------------

    private void Fight(Bot bot, Sighting? enemy, in BrSituation s, PhysicsDirectSpaceState3D space, double now)
    {
        if (enemy is { } e)
        {
            Engage(bot, e, now, space, rush: !s.HasGun || s.Rounds == 0);
            return;
        }
        // shot at by someone it cannot see: turn to where it came from, and go there
        if (bot.Senses.Memory.TryGetValue(bot.Body.LastAttacker, out var seen))
        {
            Face(bot, seen.At + Vector3.Up * 1.2f);
            BotSkills.MoveTo(bot, seen.At, run: true, 8f, space, now);
        }
        else bot.Intent.Wish = Vector3.Zero;
    }

    /// <summary>Faces the target, closes to its weapon's band (or rushes with a blade), strafes when close, fires once it has reacted.</summary>
    private void Engage(Bot bot, Sighting target, double now, PhysicsDirectSpaceState3D space, bool rush)
    {
        var body = bot.Body;
        var chest = target.Body.GlobalPosition + Vector3.Up * 1.2f;
        var weapon = BotHands.Pick(bot)?.Weapon;
        if (target.Id != _target)
        {
            _target = target.Id;
            _strafeUntil = 0;
        }
        Face(bot, chest);

        // where to stand: inside the weapon's full-damage band, a blade at arm's length
        float want = weapon is { Melee: false } w && !rush ? Mathf.Clamp(w.FalloffFrom * 0.8f, 8f, 60f) : 1.6f;
        if (target.Distance > want + 2f) BotSkills.MoveTo(bot, target.Body.GlobalPosition, run: true, want, space, now);
        else if (want > 4f)
        {
            // close enough: sidestep left and right, never standing still
            if (now >= _strafeUntil)
            {
                _strafeSide = _rng.Next(2) == 0 ? -1 : 1;
                _strafeUntil = now + 0.8 + _rng.NextDouble() * 1.2;
            }
            var to = (target.Body.GlobalPosition - body.GlobalPosition) with { Y = 0 };
            bot.Intent.Wish = to.LengthSquared() > 0.01f ? to.Normalized().Cross(Vector3.Up) * _strafeSide : Vector3.Zero;
            bot.Intent.Run = false;
        }
        else bot.Intent.Wish = Vector3.Zero;

        // --brbotsharmless (a check of the match's plumbing, not of a duel's luck): it never strikes
        if (Harmless) return;
        // it fires once it has had time to react, with its error
        double seenFor = bot.Senses.FirstSeen.TryGetValue(target.Id, out double first) ? now - first : 0;
        if (seenFor < bot.Profile.ReactionSeconds || weapon == null) return;
        if (weapon.Melee && target.Distance > weapon.Range + 0.4f) return;
        float error = BrTactics.AimError(bot.Profile.AimErrorDeg, target.Distance, target.Body.NetVel.Length(), (float)seenFor);
        var eye = body.EyePosition;
        var aim = (chest - eye).Normalized();
        var axis = aim.Cross(Vector3.Up).Normalized();
        if (axis.LengthSquared() < 0.5f) axis = Vector3.Right;
        aim = aim.Rotated(axis, Mathf.DegToRad(error * (float)(_rng.NextDouble() * 2 - 1)))
                 .Rotated(Vector3.Up, Mathf.DegToRad(error * (float)(_rng.NextDouble() * 2 - 1)));
        bot.Intent.FireAt = eye + aim * target.Distance;
    }

    private static void Face(Bot bot, Vector3 at)
    {
        var to = at - bot.Body.EyePosition;
        if (to.LengthSquared() < 0.01f) return;
        bot.Intent.FaceYaw = Mathf.Atan2(-to.X, -to.Z);
        bot.Intent.Pitch = Mathf.Asin(Mathf.Clamp(to.Normalized().Y, -1f, 1f));
    }

    private void Loot(Bot bot, Crate c, PhysicsDirectSpaceState3D space, double now)
    {
        if (_crates!.WorldOf(c, bot.Body.GlobalPosition.Y) is not { } at) { _emptied.Add(c.Id); return; }
        if (!_crates.Reachable(c, bot.Body.GlobalPosition))
        {
            bool there = GoTo(bot, at, run: true, 1.2f, space, now);
            // a crate it cannot get to (on a roof, behind a fence) is given up after a while; and one it stands
            // under or over and still cannot reach (another floor) at once: seen, a bot squad mate stood 1 m from
            // such a crate for the rest of a match, never stuck since it had arrived
            if (there || bot.Nav.Stuck >= 4) { _emptied.Add(c.Id); bot.Nav.Stuck = 0; }
            return;
        }
        bot.Intent.Wish = Vector3.Zero;
        if (!BotSkills.TakeOne(bot, _crates, c)) _emptied.Add(c.Id);
    }

    // ---- vehicles (#837 phase 3) ---------------------------------------------------------------------

    /// <summary>
    /// A trip longer than this is worth a car or a bike, when one is in sight within <see cref="CarSpot"/>
    /// (a match has a car or two a square kilometre: at 70 m no bot ever saw one at the right moment).
    /// </summary>
    private const float DriveFrom = 300f, CarSpot = 150f;
    /// <summary>An enemy this close and it gets out to fight (nobody shoots from the wheel).</summary>
    private const float BailFrom = 80f;
    private VehicleBody? _car;
    private double _carSince;
    /// <summary>Seconds it goes after a car before it gives up on it, plus one per 4 m it was off.</summary>
    private const double CarPatience = 25;
    private double _carPatience;
    /// <summary>A car further off than this share of the trip is no shortcut.</summary>
    private const float CarShare = 0.4f;
    private Vector3? _travel;
    /// <summary>Where it runs to while the zone is the goal, kept until <c>ZoneView.Safe</c> is 40 m off it.</summary>
    private Vector3? _zoneGoal;
    private double _noCarUntil;
    /// <summary>The nearest car or bike it has seen (<see cref="BotDriving.Spot"/> every 1.5 s on foot).</summary>
    private VehicleBody? _knownCar;
    private double _lookCarAt;
    /// <summary>A remembered car is worth this walk, and not the wrong way.</summary>
    private const float CarDetour = 300f;

    /// <summary>The remembered car, when it is still there, within <see cref="CarDetour"/> and not behind it on the way to <paramref name="target"/>.</summary>
    private VehicleBody? Remembered(Bot bot, Vector3 target)
    {
        if (_knownCar is not { } car || !GodotObject.IsInstanceValid(car) || VehicleManager.Instance?.Enterable(car) != true) return _knownCar = null;
        var pos = bot.Body.GlobalPosition;
        var toCar = (car.GlobalPosition - pos) with { Y = 0 };
        var toGoal = (target - pos) with { Y = 0 };
        if (toCar.Length() > CarDetour) return null;
        return toCar.Length() < 40f || toCar.Normalized().Dot(toGoal.Normalized()) > -0.2f ? car : null;
    }

    /// <summary>In a vehicle: towards where it was going; out on arrival, when stuck, or with an enemy close.</summary>
    private double Driving(Bot bot, double now)
    {
        var state = _br.State;
        var me = state.Find(bot.Id);
        bool enemyClose = false;
        foreach (var x in bot.Senses.Seen)
            if (!x.Downed && x.Distance < BailFrom && me != null && state.Find(x.Id) is { Alive: true } e && BrState.Hostile(me, e)) enemyClose = true;
        bot.Intent.FaceYaw = null;
        bot.Intent.FireAt = null;
        if (_travel is not { } goal || enemyClose || bot.Drive.Stopping)
        {
            if (BotDriving.GetOut(bot)) _travel = null;
            _status = $"goal Drive out{(enemyClose ? " (enemy)" : "")}";
            return 0.15;
        }
        var progress = BotDriving.Towards(bot, goal, now);
        if (progress is BotDriving.Progress.Arrived or BotDriving.Progress.Failed)
        {
            bot.Drive.Stopping = true;
            if (progress == BotDriving.Progress.Failed) _noCarUntil = now + 30;   // walk for a while
        }
        _status = $"goal Drive {progress} speed {bot.Body.RideSpeed:F0}";
        return 0.15;
    }

    // ---- the streets --------------------------------------------------------------------------------

    /// <summary>Further than this, a bot walks the streets (<see cref="BrManager.Roads"/>) rather than straight.</summary>
    private const float StreetsFrom = 80f;
    private Vector3? _routeGoal;
    private List<Vector3>? _route;
    private int _routeNext;
    private int _routeFails;
    private double _routeAt;

    /// <summary>
    /// To <paramref name="target"/>: along the streets when it is far and there are streets, waypoint by
    /// waypoint (skipping ahead when a later one is nearer), else straight (<see cref="BotSkills.MoveTo"/>,
    /// which still goes round walls). True once there.
    /// </summary>
    private bool GoTo(Bot bot, Vector3 target, bool run, float arrive, PhysicsDirectSpaceState3D space, double now)
    {
        var pos = bot.Body.GlobalPosition;
        float straight = new Vector2(target.X - pos.X, target.Z - pos.Z).Length();
        // far, and a car or a bike in sight or remembered not far off the way: go and take it, then drive there
        if (straight > DriveFrom && now >= _noCarUntil)
        {
            if (_car == null || !GodotObject.IsInstanceValid(_car))
            {
                _car = BotDriving.Spot(bot, CarSpot) ?? Remembered(bot, target);
                // a car not much nearer than the goal is no shortcut
                if (_car != null && _car.GlobalPosition.DistanceTo(pos) > straight * CarShare) _car = null;
                _carSince = now;
                // patience for the walk there too: a remembered car can be 100 m off along the streets
                if (_car != null) _carPatience = CarPatience + _car.GlobalPosition.DistanceTo(pos) / 4f;
                if (_car != null) GD.Print(FormattableString.Invariant($"[bots] {bot.Body.Name} \"{bot.Profile.Name}\" goes for {_car.Name} ({_car.Kind}) {_car.GlobalPosition.DistanceTo(pos):F0} m off, {straight:F0} m to go"));
            }
            // a car it cannot get to (behind a fence, across a river) is given up, and it walks a while:
            // without this, bots stood against a wall going for a car while the zone closed on them
            if (_car != null && (now - _carSince > _carPatience || bot.Nav.Stuck >= 3))
            {
                GD.Print($"[bots] {bot.Body.Name} \"{bot.Profile.Name}\" gives up {_car.Name} (stuck {bot.Nav.Stuck})");
                if (_knownCar == _car) _knownCar = null;
                _car = null;
                _noCarUntil = now + 60;
                bot.Nav.Stuck = 0;
            }
            if (_car != null)
            {
                var carAt = _car.GlobalPosition;
                // far from it: along the streets (GoTo's own), close: to its door and in
                bool walked = new Vector2(carAt.X - pos.X, carAt.Z - pos.Z).Length() > 30f && Streets(bot, carAt, space, now);
                if (!walked && BotDriving.GetIn(bot, _car, space, now)) { _travel = target; _car = null; }
                // at its door and still not in: someone was quicker, or it cannot be claimed; walk
                else if (!walked && bot.Intent.Wish == Vector3.Zero) { _car = null; _noCarUntil = now + 10; }
                if (_car != null && (!GodotObject.IsInstanceValid(_car) || VehicleManager.Instance?.Enterable(_car) != true)) _car = null;
                return false;
            }
        }
        else _car = null;   // a short trip now (the goal changed, or it came close): no car needed
        if (straight > StreetsFrom && Streets(bot, target, space, now, run)) return false;
        return BotSkills.MoveTo(bot, target, run, arrive, space, now);
    }

    /// <summary>One step along the streets to <paramref name="target"/>; false when there is no street route (walk straight).</summary>
    private bool Streets(Bot bot, Vector3 target, PhysicsDirectSpaceState3D space, double now, bool run = true)
    {
        var pos = bot.Body.GlobalPosition;
        if (_br.Roads is { } roads && bot.Body.Origin is { } origin)
        {
            // a new goal, or a stale route: plan again (at most every 10 s for the same goal)
            if (_routeGoal is not { } g || g.DistanceTo(target) > 40f || (_route == null && now - _routeAt > 10))
            {
                _routeGoal = target;
                _routeAt = now;
                var (fe, fn) = origin.ToLv95(pos);
                var (te, tn) = origin.ToLv95(target);
                _route = roads.Path(fe, fn, te, tn)?.Select(p => origin.ToWorld(p.E, p.N, pos.Y)).ToList();
                _routeNext = 0;
                // a detour, not a way: streets that do not go there (seen: 197 points away from a ping 100 m
                // off, until it was given up). Straight it is, round the walls as ever
                if (_route is { Count: > 1 } r && RouteLength(pos, r) > Flat(target - pos) * DetourFactor + 100f)
                {
                    GD.Print(FormattableString.Invariant($"[bots] {bot.Body.Name} street route {RouteLength(pos, r):F0} m for {Flat(target - pos):F0} m: straight instead"));
                    _route = null;
                }
                if (_route == null && _routeFails++ < 3)
                    GD.Print(FormattableString.Invariant($"[bots] {bot.Body.Name} no street route {fe:F0}/{fn:F0} -> {te:F0}/{tn:F0}: {roads.Near(fe, fn, 150, 3).Count} roads near it, {roads.Near(te, tn, 150, 3).Count} near the goal"));
            }
            if (_route is { Count: > 0 } route && _routeNext < route.Count)
            {
                // a later waypoint already nearer: on to it
                for (int k = route.Count - 1; k > _routeNext; k--)
                    if (Flat(route[k] - pos) < Flat(route[_routeNext] - pos) && Flat(route[k] - pos) < 25f) { _routeNext = k; break; }
                if (BotSkills.MoveTo(bot, route[_routeNext], run, 4f, space, now)) _routeNext++;
                return true;
            }
        }
        return false;
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    /// <summary>A street route this many times the straight distance (plus 100 m) is a detour: walk straight.</summary>
    private const float DetourFactor = 2.5f;

    private static float RouteLength(Vector3 from, List<Vector3> route)
    {
        float len = Flat(route[0] - from);
        for (int i = 1; i < route.Count; i++) len += Flat(route[i] - route[i - 1]);
        return len;
    }

    private void Roam(Bot bot, ZoneView zone, PhysicsDirectSpaceState3D space, double now)
    {
        var body = bot.Body;
        if (_roam is not { } goal || GoTo(bot, goal, run: false, 5f, space, now) || bot.Nav.Stuck >= 3)
        {
            // somewhere else inside the circle it must be in, a few hundred metres off at most
            var next = zone.Roam;
            var hop = (next - body.GlobalPosition) with { Y = 0 };
            _roam = hop.Length() > 300f ? body.GlobalPosition + hop.Normalized() * 300f : next;
            bot.Nav.Stuck = 0;
        }
    }
}
