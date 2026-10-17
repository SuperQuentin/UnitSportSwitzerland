using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnitSport.BattleRoyale;
using UnitSport.Items;

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
    /// <summary>Where it means to land (world), chosen when it boarded.</summary>
    public Vector3? DropTarget { get; init; }
    /// <summary>The server clock at which it jumps: the plane's nearest pass to <see cref="DropTarget"/>.</summary>
    public double JumpAt { get; init; }

    /// <summary>The canopy opens this high over the ground (it takes a second or two to slow), or lower once over the spot.</summary>
    private const float OpenAt = 300f, OpenOverSpot = 180f;
    /// <summary>The last metres under the canopy are flown on the brakes: a soft touchdown.</summary>
    private const float FlareAt = 15f;

    public BrBrain(BrManager br, BrCrates? crates, int seed)
    {
        _br = br;
        _crates = crates;
        _rng = new Random(seed);
    }

    private static double ServerNow => Net.ClockSync.ServerNow;

    public double Think(Bot bot, double now)
    {
        var body = bot.Body;
        bot.Intent.FireAt = null;
        if (body.Eliminated || body.Down != 0)
        {
            bot.Intent = BotIntent.Stand;
            return 0.5;
        }
        // the drop first: the hold, then the wingsuit and the canopy (#837 phase 2)
        if (Drop(bot)) return 0.1;
        bot.Intent.Canopy = false;
        bot.Intent.Steer = Vector2.Zero;
        var space = bot.World.Space;
        bot.Senses.Look(bot, bot.World.Bodies(), space, now);
        var s = Situation(bot, now, out var enemy, out var downed, out var crate, out var zone);
        _status = FormattableString.Invariant($"goal {Goal} inside {zone.Inside:F2} out {s.OutsideBy:F0} run {s.ZoneRunNeeded:F0} enemy {s.EnemyDistance:F0} crate {s.CrateDistance:F0} stuck {bot.Nav.Stuck} open {bot.Nav.LastOpen:F1}");
        var goal = BrTactics.Choose(s, Goal);
        if (goal != Goal) GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" {Goal} -> {goal}");
        Goal = goal;
        if (goal is not (BrGoal.Fight or BrGoal.Finish)) bot.Intent.FaceYaw = null;

        switch (goal)
        {
            case BrGoal.Zone:
                BotSkills.MoveTo(bot, zone.Safe, run: true, 4f, space, now);
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
                if (BotSkills.MoveTo(bot, heard.At, run: false, 6f, space, now)) bot.Senses.Heard = null;
                return 0.3;
            default:
                Roam(bot, zone, space, now);
                return 0.4;
        }
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
            if (ServerNow >= JumpAt) _br.BotJump(bot);
            return true;
        }
        var ride = body.Ride;
        if (ride is not (Player.RideKind.Wingsuit or Player.RideKind.Parachute)) return false;
        bot.Intent.Wish = Vector3.Zero;
        bot.Intent.FireAt = null;
        var pos = body.GlobalPosition;
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
            var z = state.Zone().At(ServerNow - state.Started);
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
        if (_crates != null)
        {
            _near.Clear();
            foreach (var c in _crates.All)
            {
                if (_emptied.Contains(c.Id) || c.Locked || ServerNow < c.LandsAt || _crates.WorldOf(c, body.GlobalPosition.Y) is not { } at) continue;
                float d = new Vector2(at.X - body.GlobalPosition.X, at.Z - body.GlobalPosition.Z).Length();
                if (d <= BrTactics.CrateDraw) _near.Add((c, at, d));
            }
            _near.Sort((x, y) => x.D.CompareTo(y.D));
            // the crate it is already walking to stays its crate, seen or not, until it is emptied
            foreach (var (c, _, d) in _near)
                if (c.Id == _lootCrate) { crate = c; crateDistance = d; }
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
            CrateDistance: crateDistance, Boldness: bot.Profile.Boldness, LootGreed: bot.Profile.LootGreed);
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
            BotSkills.MoveTo(bot, at, run: true, 1.2f, space, now);
            // a crate it cannot get to (on a roof, behind a fence) is given up after a while
            if (bot.Nav.Stuck >= 4) { _emptied.Add(c.Id); bot.Nav.Stuck = 0; }
            return;
        }
        bot.Intent.Wish = Vector3.Zero;
        if (!BotSkills.TakeOne(bot, _crates, c)) _emptied.Add(c.Id);
    }

    private void Roam(Bot bot, ZoneView zone, PhysicsDirectSpaceState3D space, double now)
    {
        var body = bot.Body;
        if (_roam is not { } goal || BotSkills.MoveTo(bot, goal, run: false, 5f, space, now) || bot.Nav.Stuck >= 3)
        {
            // somewhere else inside the circle it must be in, a few hundred metres off at most
            var next = zone.Roam;
            var hop = (next - body.GlobalPosition) with { Y = 0 };
            _roam = hop.Length() > 300f ? body.GlobalPosition + hop.Normalized() * 300f : next;
            bot.Nav.Stuck = 0;
        }
    }
}
