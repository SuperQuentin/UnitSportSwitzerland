using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Bots;
using UnitSport.Terrain.Bodies;
using UnitSport.Vehicles;

namespace UnitSport.World.Moon;

/// <summary>
/// A Moon colonist as a bot (#1976): it plays its <see cref="ColonyCrew"/> day through the intents a player uses, never
/// by moving its body. Each window of the crew's clock gives it a spot; it goes there as a route of legs and then stands
/// facing that spot's work:
/// <list type="bullet">
/// <item>inside, it walks the colony's own path (<see cref="ColonyCrew.Path(int, int)"/>: through the doors, along the
/// tubes) point by point (<c>WalkControls</c>);</item>
/// <item>out on an EVA, it walks to the nearest airlock, cycles it in and out (<see cref="ColonyAirlocks.Request"/>, what E
/// at a shut door does), goes down the stairs and across the ground (<see cref="BotSkills.MoveTo"/>), and back the same
/// way;</item>
/// <item>a far EVA spot is driven to in a colony rover (<see cref="BotDriving.GetIn"/>, <see cref="BotDriving.Across"/>
/// through <c>RideControls</c>, <see cref="BotDriving.GetOut"/>), brought back to its slot on the way home.</item>
/// </list>
/// A route always ends at a spot, so a new window waits for the route under way. Asleep it walks to its bunk, where the
/// colony takes its body away while nobody sees (<see cref="ColonyColonists"/>). It reads the world as a player would:
/// its colony's plan and airlocks, and the rovers parked there.
/// </summary>
public sealed class ColonistBrain : IBotBrain
{
    /// <summary>An EVA spot this far from its airlock's stairs is driven to, metres.</summary>
    public const double RoverM = 60;

    /// <summary>A rover's top speed on a colonist's drive, m/s (the open rover's 20 km/h, less).</summary>
    public const float DriveSpeed = 4.5f;

    /// <summary>A rover stops this far short of an EVA spot, metres: the last steps on foot.</summary>
    public const double ParkShort = 7;

    /// <summary>Walking pace indoors and out: the wish's length (1 is a full walk).</summary>
    private const float Pace = 0.75f;

    /// <summary>A waypoint of an inside walk counts as reached this near, metres (flat).</summary>
    private const float PointReach = 0.3f;

    /// <summary>A cycle waiting longer than this asks again (another colonist's cycle in between), seconds.</summary>
    private const double CycleRetry = 12;

    private readonly ColonyColonists _host;
    private readonly int _member;

    private enum LegKind { Walk, Cycle, Outdoors, ToRover, Drive, LeaveRover }

    private sealed class Leg
    {
        public LegKind Kind;
        public (double X, double Y, double Z)[] Points = [];
        public int At;
        public int Lock;
        public bool Outward;
        public (double X, double Y, double Z) Goal;
        public float Arrive = 0.6f;
        public double Since = -1;
        public double AskedAt = -1;
        public int Stalls;
        /// <summary>A drive's top speed, m/s (inside the hangar it creeps: at full pace it rolled 10 m past its slot).</summary>
        public float Speed = DriveSpeed;
        public string What = "";
    }

    private readonly Queue<Leg> _legs = new();
    private long _window = long.MinValue;
    private int _target = -1;
    private CrewActivity _activity;
    private string? _rover;
    private Vector3 _headwayFrom;
    private double _headwayAt;

    /// <summary>The spot it stands at (or last left); outside when that spot is.</summary>
    public int Spot { get; private set; }
    public bool Outside { get; private set; }
    /// <summary>On a route now (walking, cycling, driving).</summary>
    public bool Moving => _legs.Count > 0;
    /// <summary>At its bunk with its shift asleep: the colony may take the body away while nobody sees.</summary>
    public bool Asleep => _legs.Count == 0 && _activity == CrewActivity.Asleep && _target == Spot;
    /// <summary>In a rover now.</summary>
    public bool Driving { get; private set; }
    /// <summary>The window's plan is forced to its EVA, by rover (the check's: <c>--colonisteva</c>).</summary>
    public bool ForceRoverEva;

    /// <summary>The forced EVA goes by the pressurised rover from the hangar (the check's: <c>--colonisteva cabin</c>).</summary>
    public bool ForceCabin;

    /// <summary>The trip under way uses the hangar's pressurised rover (back the same way, through the hangar).</summary>
    private bool _cabin;

    /// <summary>An EVA spot this far from the hangar's door goes by the pressurised rover, for every other colonist, metres.</summary>
    public const double CabinM = 120;

    private bool _forceHome;

    /// <summary>The check's: back to its work spot inside once the route under way has ended (an EVA forced, then ended).</summary>
    public void GoHome()
    {
        ForceRoverEva = false;
        _forceHome = true;
        _window = long.MinValue;
    }

    public string Status => _legs.Count > 0 ? $"{_legs.Peek().Kind} {_legs.Peek().What} to spot {_target}" : $"at spot {Spot} ({_activity})";

    public ColonistBrain(ColonyColonists host, int member, int spot)
    {
        _host = host;
        _member = member;
        Spot = spot;
        Outside = spot >= 0 && host.Crew.Spots[spot].Module < 0;
    }

    public double Think(Bot bot, double now)
    {
        var body = bot.Body;
        bot.Intent = BotIntent.Stand;
        MoonSuit.Dress(body);
        if (body.Eliminated || body.KnockedOut || !GodotObject.IsInstanceValid(_host) || !_host.IsInsideTree()) return 0.5;
        var crew = _host.Crew;
        var who = crew.Crew[_member];
        // outside the air, off the ground the body's square holds (it follows the camera, 512 m): the body waits, held
        // still, until the ground is there again (a player walking off from the colony while a colonist is on an EVA)
        if (!BotDriving.Mounted(bot) && body.Placed && body.Terrain?.HasCollisionAt(body.GlobalPosition) == false
            && !ColonyAirlocks.Pressurised(body.GlobalPosition + Vector3.Up * 1.6f))
            body.RequestReplacement();
        if (!body.Placed) return 0.5;

        // a new window: its spot, and the route there once the one under way has ended at its spot
        double t = _host.CrewClock;
        long w = (long)Math.Floor(t / ColonyCrew.Window);
        if (w != _window && _legs.Count == 0)
        {
            _window = w;
            int target;
            if (_forceHome)
            {
                target = who.Work;
                _activity = CrewActivity.Work;
            }
            else if (ForceRoverEva && who.Eva >= 0)
            {
                target = who.Eva;
                _activity = CrewActivity.Eva;
            }
            else if (crew.Plan(who, w, _host.Hour) is { } plan)
            {
                target = plan.Spot;
                _activity = plan.Activity;
            }
            else
            {
                target = who.Bunk;
                _activity = CrewActivity.Asleep;
            }
            if (target >= 0 && target != Spot) Route(target, bot);
            _target = target;
        }

        if (_legs.Count == 0)
        {
            Stand(bot);
            return 0.5;
        }
        var leg = _legs.Peek();
        if (leg.Since < 0)
        {
            leg.Since = now;
            _headwayFrom = body.GlobalPosition;
            _headwayAt = now;
        }
        bool done = leg.Kind switch
        {
            LegKind.Walk => Walk(bot, leg, now),
            LegKind.Cycle => Cycle(bot, leg, now),
            LegKind.Outdoors => BotSkills.MoveTo(bot, World(bot, leg.Goal), run: false, leg.Arrive, body.Space, now),
            LegKind.ToRover => ToRover(bot, leg, now),
            LegKind.Drive => Drive(bot, leg, now),
            LegKind.LeaveRover => LeaveRover(bot),
            _ => true,
        };
        if (leg.Kind == LegKind.Outdoors && bot.Intent.Wish != Vector3.Zero) bot.Intent.Wish = bot.Intent.Wish.Normalized() * Pace;
        if (done)
        {
            _legs.Dequeue();
            if (_legs.Count == 0)
            {
                Spot = _target;
                Outside = crew.Spots[Spot].Module < 0;
                Log(bot, $"at spot {Spot} ({_activity}{(Outside ? ", outside" : "")})");
            }
            return 0.05;
        }
        return leg.Kind == LegKind.Walk ? 0.1 : 0.2;
    }

    // ---- the route ---------------------------------------------------------------------------------------------------

    private void Route(int target, Bot bot)
    {
        var crew = _host.Crew;
        var to = crew.Spots[target];
        bool fromOut = Outside, toOut = to.Module < 0;
        if (!fromOut && !toOut)
        {
            Add(new Leg { Kind = LegKind.Walk, Points = crew.Path(Spot, target), What = "inside" });
            return;
        }
        if (!fromOut && toOut && CabinFor(target) is { } cabin && Hangar() is { } hw)
        {
            // by the pressurised rover (decision 5, #1938): through the hangar, a vehicle airlock, and back the same way
            _rover = cabin;
            _cabin = true;
            var s = crew.Spots[target];
            Add(new Leg { Kind = LegKind.Walk, Points = ToDoor(Spot, hw.Module, hw.Inner, hw.InnerOut, hw.Wait), What = "to the hangar" });
            Add(new Leg { Kind = LegKind.Cycle, Lock = hw.Index, Outward = false, What = "the hangar's inner door" });
            Add(new Leg { Kind = LegKind.Walk, Points = [hw.Wait, hw.Inner, hw.Landing], What = "into the hangar" });
            Add(new Leg { Kind = LegKind.ToRover, What = cabin });
            Add(new Leg { Kind = LegKind.Drive, Points = [hw.LaneIn], What = "to the rolling door" });
            Add(new Leg { Kind = LegKind.Cycle, Lock = hw.Index, Outward = true, What = "the rolling door" });
            var out_ = Ring(hw.LaneFar, Short(hw.LaneFar, (s.X, s.Y, s.Z), ParkShort));
            Add(new Leg { Kind = LegKind.Drive, Points = [hw.LaneOut, hw.LaneFar, .. out_], What = "to the EVA" });
            Add(new Leg { Kind = LegKind.LeaveRover });
            Add(new Leg { Kind = LegKind.Outdoors, Goal = (to.X, to.Y, to.Z), Arrive = 0.8f, What = "to the EVA spot" });
            Log(bot, $"out on an EVA to spot {target} by the pressurised rover {cabin}, through the hangar");
            return;
        }
        if (fromOut && !toOut && _cabin && _rover != null && Hangar() is { } hb)
        {
            var from = crew.Spots[Spot];
            Add(new Leg { Kind = LegKind.ToRover, What = _rover });
            Add(new Leg { Kind = LegKind.Drive, Points = [.. Ring((from.X, from.Y, from.Z), hb.LaneFar), hb.LaneOut], What = "back to the hangar" });
            Add(new Leg { Kind = LegKind.Cycle, Lock = hb.Index, Outward = true, What = "the rolling door, from outside" });
            Add(new Leg { Kind = LegKind.Drive, Points = [hb.LaneIn, hb.LaneDeep, hb.Slot], Speed = 1.5f, What = "into its lane" });
            Add(new Leg { Kind = LegKind.LeaveRover });
            Add(new Leg { Kind = LegKind.Cycle, Lock = hb.Index, Outward = false, What = "the hangar back to air" });
            // out of the cab onto the hangar's middle line first, then to its inner door: straight from the cab's side it
            // clipped the parked rover's rear corner
            // (the left lane: the mirror of the slot, clear of the cab however near its slot it stopped)
            var mid = _host.Plan.Modules[hb.Module];
            Add(new Leg { Kind = LegKind.Walk, Points = [(2 * mid.X - hb.Slot.X, 0.6, 2 * mid.Z - hb.Slot.Z), .. crew.Path(hb.Landing, hb.Module, (crew.Spots[target].X, crew.Spots[target].Y, crew.Spots[target].Z), crew.Spots[target].Module)], What = "inside" });
            Log(bot, $"back in from spot {Spot} by the pressurised rover {_rover}, through the hangar");
            _rover = null;
            _cabin = false;
            return;
        }
        if (!fromOut && toOut)
        {
            int i = crew.LockFor(target);
            if (i < 0) return;
            var way = crew.Lock(i);
            Add(new Leg { Kind = LegKind.Walk, Points = crew.ToLock(Spot, i), What = $"to airlock {i}" });
            Add(new Leg { Kind = LegKind.Cycle, Lock = i, Outward = false, What = "in" });
            Add(new Leg { Kind = LegKind.Walk, Points = [way.Wait, way.Inner, way.Centre], What = "into the chamber" });
            Add(new Leg { Kind = LegKind.Cycle, Lock = i, Outward = true, What = "out" });
            Add(new Leg { Kind = LegKind.Walk, Points = [way.Centre, way.Outer, way.Outside], What = "down the stairs" });
            if (RoverFor(target, way.Outside) is { } rover)
            {
                _rover = rover;
                var s = crew.Spots[target];
                var short_ = Short(way.Outside, (s.X, s.Y, s.Z), ParkShort);
                Add(new Leg { Kind = LegKind.ToRover, What = rover });
                var from = RoverSlot(rover) ?? way.Outside;
                Add(new Leg { Kind = LegKind.Drive, Points = Ring(from, short_), What = "to the EVA" });
                Add(new Leg { Kind = LegKind.LeaveRover });
            }
            Add(new Leg { Kind = LegKind.Outdoors, Goal = (to.X, to.Y, to.Z), Arrive = 0.8f, What = "to the EVA spot" });
            Log(bot, $"out on an EVA to spot {target} through airlock {i}{(_rover != null ? $", by rover {_rover}" : "")}");
            return;
        }
        if (fromOut && !toOut)
        {
            var from = crew.Spots[Spot];
            int i = crew.LockFor(Spot);
            if (i < 0) return;
            var way = crew.Lock(i);
            if (_rover != null)
            {
                Add(new Leg { Kind = LegKind.ToRover, What = _rover });
                Add(new Leg { Kind = LegKind.Drive, Points = Ring((from.X, from.Y, from.Z), RoverSlot(_rover) ?? way.Outside), What = "back to its slot" });
                Add(new Leg { Kind = LegKind.LeaveRover });
                _rover = null;
            }
            Add(new Leg { Kind = LegKind.Outdoors, Goal = way.Outside, Arrive = 0.6f, What = $"back to airlock {i}" });
            Add(new Leg { Kind = LegKind.Cycle, Lock = i, Outward = true, What = "open from outside" });
            Add(new Leg { Kind = LegKind.Walk, Points = [way.Outside, way.Outer, way.Centre], What = "up into the chamber" });
            Add(new Leg { Kind = LegKind.Cycle, Lock = i, Outward = false, What = "in" });
            Add(new Leg { Kind = LegKind.Walk, Points = crew.FromLock(i, target), What = "inside" });
            Log(bot, $"back in from spot {Spot} ({from.X:F0}, {from.Z:F0}) through airlock {i}");
            return;
        }
        // outside to outside: one EVA spot to another (a colonist has one: never yet)
        Add(new Leg { Kind = LegKind.Outdoors, Goal = (to.X, to.Y, to.Z), Arrive = 0.8f, What = "across" });
    }

    private void Add(Leg l) => _legs.Enqueue(l);

    /// <summary>
    /// The drive given up: out of the rover where it stands, then on foot to the airlock nearest the spot it was going to
    /// and in. The rover stays parked there, free for anyone.
    /// </summary>
    private void WalkHome(Bot bot)
    {
        var crew = _host.Crew;
        int target = _target;
        _legs.Clear();
        // the placeholder the caller dequeues, then out
        Add(new Leg { Kind = LegKind.Walk });
        Add(new Leg { Kind = LegKind.LeaveRover });
        _rover = null;
        _cabin = false;
        if (target < 0 || crew.Spots[target].Module < 0) return;
        int i = crew.LockFor(target);
        if (i < 0) return;
        var way = crew.Lock(i);
        Add(new Leg { Kind = LegKind.Outdoors, Goal = way.Outside, Arrive = 0.6f, What = $"on foot to airlock {i}" });
        Add(new Leg { Kind = LegKind.Cycle, Lock = i, Outward = true, What = "open from outside" });
        Add(new Leg { Kind = LegKind.Walk, Points = [way.Outside, way.Outer, way.Centre], What = "up into the chamber" });
        Add(new Leg { Kind = LegKind.Cycle, Lock = i, Outward = false, What = "in" });
        Add(new Leg { Kind = LegKind.Walk, Points = crew.FromLock(i, target), What = "inside" });
    }

    /// <summary>A point <paramref name="back"/> metres short of <paramref name="to"/> on the way from <paramref name="from"/>.</summary>
    private static (double X, double Y, double Z) Short((double X, double Y, double Z) from, (double X, double Y, double Z) to, double back)
    {
        double dx = to.X - from.X, dz = to.Z - from.Z, d = Math.Sqrt(dx * dx + dz * dz);
        if (d <= back + 1) return from;
        return (to.X - dx / d * back, to.Y, to.Z - dz / d * back);
    }

    /// <summary>A free open rover of the colony for an EVA spot far from the airlock (or any, forced): its name, or null.</summary>
    private string? RoverFor(int spot, (double X, double Y, double Z) outside)
    {
        var s = _host.Crew.Spots[spot];
        double d = Math.Sqrt((s.X - outside.X) * (s.X - outside.X) + (s.Z - outside.Z) * (s.Z - outside.Z));
        if (d < RoverM && !ForceRoverEva) return null;
        if (VehicleManager.Instance is not { } vehicles) return null;
        var slots = MoonRoverDepot.Slots(_host.Colony, _host.Plan);
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Kind != Player.MoonRovers.Open.Kind) continue;
            string name = $"{MoonRoverDepot.Prefix}{_host.Colony.Id}_{i}";
            if (vehicles.GetNodeOrNull(name) is VehicleBody v && vehicles.Enterable(v) && !ColonyColonists.RoverTaken(name, this)) return name;
        }
        return null;
    }

    /// <summary>The hangar's pressurised rover for a far EVA spot (every other colonist, or forced): its name, or null.</summary>
    private string? CabinFor(int spot)
    {
        if (Hangar() is not { } hw || VehicleManager.Instance is not { } vehicles) return null;
        var s = _host.Crew.Spots[spot];
        double d = Math.Sqrt((s.X - hw.LaneOut.X) * (s.X - hw.LaneOut.X) + (s.Z - hw.LaneOut.Z) * (s.Z - hw.LaneOut.Z));
        var who = _host.Crew.Crew[_member];
        bool wants = ForceCabin || !ForceRoverEva && d >= CabinM && who.Index % 2 == 1;
        if (!wants) return null;
        var slots = MoonRoverDepot.Slots(_host.Colony, _host.Plan);
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i].Kind != Player.MoonRovers.Cabin.Kind) continue;
            string name = $"{MoonRoverDepot.Prefix}{_host.Colony.Id}_{i}";
            if (vehicles.GetNodeOrNull(name) is VehicleBody v && vehicles.Enterable(v) && !ColonyColonists.RoverTaken(name, this)) return name;
        }
        return null;
    }

    /// <summary>
    /// The hangar's way for a colonist (colony frame): its airlock index, the inner door and its way out, the wait in the
    /// tube before it, the landing inside it, and the pressurised rover's lane: the slot, a point before the rolling door,
    /// one past the ramp.
    /// </summary>
    private readonly record struct HangarWay(int Index, int Module, (double X, double Y, double Z) Inner, double InnerOut,
        (double X, double Y, double Z) Wait, (double X, double Y, double Z) Landing, (double X, double Y, double Z) Slot,
        (double X, double Y, double Z) LaneIn, (double X, double Y, double Z) LaneOut, (double X, double Y, double Z) LaneFar,
        (double X, double Y, double Z) LaneDeep);

    private HangarWay? Hangar()
    {
        var plan = _host.Plan;
        if (plan.Hangar is not { } h) return null;
        var slots = MoonRoverDepot.Slots(_host.Colony, plan);
        int cabin = slots.FindIndex(sl => sl.Kind == Player.MoonRovers.Cabin.Kind);
        if (cabin < 0) return null;
        var inner = plan.Doors[h.Inner];
        var outer = plan.Doors[h.Outer];
        var (ix, iz) = ColonyPlan.Along(inner.Out);
        var sl = slots[cabin];
        double fx = sl.Facing.X, fz = sl.Facing.Z;
        // the way in and out runs on the rolling door's centre line (3 m clear of each jamb for the cabin rover); only the
        // parking slot is in the right-hand lane. On the lane's own line, a rover coming back at a slant caught the jamb
        return new HangarWay(plan.Airlocks.Count, h.Module, (inner.X, 1.55, inner.Z), inner.Out,
            (inner.X + ix * ColonyCrew.WaitBack, 1.55, inner.Z + iz * ColonyCrew.WaitBack), (inner.X - ix * 2.4, 0.6, inner.Z - iz * 2.4),
            (sl.At.X, sl.At.Y, sl.At.Z), (outer.X - fx * 3, 0.6, outer.Z - fz * 3),
            (outer.X + fx * 13, 0, outer.Z + fz * 13),
            // well past the open rovers parked either side of the road (8 m off it, 23 m out): the way on and off the lane,
            // straight between them. Joined nearer, the arc back crossed one and the 6 t cab shoved it into the hangar
            (outer.X + fx * 60, 0, outer.Z + fz * 60),
            // well inside on the same line before it turns for its slot: turning at the doorway caught the jamb
            (outer.X - fx * 12, 0.6, outer.Z - fz * 12));
    }

    /// <summary>From an inside spot to the wait before a module's door (the hangar's inner one), in the tube.</summary>
    private (double X, double Y, double Z)[] ToDoor(int from, int module, (double X, double Y, double Z) door, double doorOut, (double X, double Y, double Z) wait)
    {
        var crew = _host.Crew;
        var a = crew.Spots[from];
        var m = _host.Plan.Modules[module];
        var through = crew.Path((a.X, a.Y, a.Z), a.Module, (m.X, 1.55, m.Z), module);
        var pts = new List<(double X, double Y, double Z)>();
        foreach (var p in through)
        {
            if (Math.Abs(p.X - door.X) < 1e-6 && Math.Abs(p.Z - door.Z) < 1e-6) break;
            pts.Add(p);
        }
        pts.Add(wait);
        return pts.ToArray();
    }

    /// <summary>A depot rover's slot (colony frame), to bring it back.</summary>
    private (double X, double Y, double Z)? RoverSlot(string name)
    {
        var slots = MoonRoverDepot.Slots(_host.Colony, _host.Plan);
        for (int i = 0; i < slots.Count; i++)
            if (name == $"{MoonRoverDepot.Prefix}{_host.Colony.Id}_{i}")
                return (slots[i].At.X, slots[i].At.Y, slots[i].At.Z);
        return null;
    }

    // ---- the legs ----------------------------------------------------------------------------------------------------

    /// <summary>Point by point, straight (never <c>MoveTo</c> inside: its wall rays meet a tube's side at every door).</summary>
    private bool Walk(Bot bot, Leg leg, double now)
    {
        var body = bot.Body;
        var pos = body.GlobalPosition;
        while (leg.At < leg.Points.Length)
        {
            var to = World(bot, leg.Points[leg.At]) - pos;
            to.Y = 0;
            // the next point nearer than this one (a corner cut inside the room it turns in): go on to it
            if (to.Length() < PointReach) { leg.At++; continue; }
            // its own spot taken by another body (two at one bench): it stands beside them
            if (leg.At == leg.Points.Length - 1 && to.Length() < 1.2f && Taken(bot, pos + to)) { leg.At++; continue; }
            bot.Intent.Wish = Avoid(bot, to.Normalized()) * Pace;
            if (NoHeadway(bot, now, leg))
            {
                // blocked within a metre of where it was going to stand (a bench's corner, another body): it stands there;
                // blocked twice on a point on the way: on to the next one, rather than stuck for good
                if (leg.At == leg.Points.Length - 1 && to.Length() < 1.0f) { leg.At++; continue; }
                if (++leg.Stalls >= 2) { leg.At++; leg.Stalls = 0; }
            }
            return false;
        }
        return true;
    }

    /// <summary>Within this of another body ahead, a walker steps aside round it (bots collide where figures overlapped), metres.</summary>
    private const float Personal = 1.4f;

    /// <summary>
    /// A walk's direction bent round the bodies just ahead (a colonist standing on the aisle, one coming the other way in a
    /// tube): away from each across the way, the more the nearer; one dead ahead is passed on the right.
    /// </summary>
    private static Vector3 Avoid(Bot bot, Vector3 dir)
    {
        var pos = bot.Body.GlobalPosition;
        var push = Vector3.Zero;
        foreach (var other in bot.World.Bodies())
        {
            if (other == bot.Body || !GodotObject.IsInstanceValid(other)) continue;
            var d = other.GlobalPosition - pos;
            d.Y = 0;
            float dist = d.Length();
            float ahead = d.Dot(dir);
            if (dist > Personal || dist < 0.01f || ahead < 0) continue;
            var across = d - dir * ahead;
            var away = across.Length() < 0.1f ? dir.Cross(Vector3.Up) : -across.Normalized();
            push += away * (1 - dist / Personal) * 1.6f;
        }
        return push == Vector3.Zero ? dir : (dir + push).Normalized();
    }

    /// <summary>Whether another body stands within 0.7 m of a point.</summary>
    private static bool Taken(Bot bot, Vector3 at)
    {
        foreach (var other in bot.World.Bodies())
        {
            if (other == bot.Body || !GodotObject.IsInstanceValid(other)) continue;
            var d = other.GlobalPosition - at;
            d.Y = 0;
            if (d.Length() < 0.7f) return true;
        }
        return false;
    }

    /// <summary>Asks the airlock for the door it wants open (E at that door), and waits for it, facing it.</summary>
    private bool Cycle(Bot bot, Leg leg, double now)
    {
        if (_host.Airlocks is not { } locks) return true;
        if (locks.Opening(leg.Lock, leg.Outward) >= 0.98f && locks.Wanted(leg.Lock, leg.Outward)) return true;
        if (leg.AskedAt < 0 || now - leg.AskedAt > CycleRetry)
        {
            if (locks.Request(leg.Lock, leg.Outward)) Log(bot, $"cycles airlock {leg.Lock} {(leg.Outward ? "out" : "in")} ({leg.What})");
            leg.AskedAt = now;
        }
        var door = locks.DoorAt(leg.Lock, leg.Outward);
        var at = bot.Body.FromFrame(_host.Origin, door) - bot.Body.GlobalPosition;
        bot.Intent.FaceYaw = Mathf.Atan2(-at.X, -at.Z);
        return false;
    }

    private bool ToRover(Bot bot, Leg leg, double now)
    {
        if (BotDriving.Mounted(bot)) return true;
        if (VehicleManager.Instance?.GetNodeOrNull(leg.What) is not VehicleBody car)
        {
            // gone (someone took it): the rest on foot
            Log(bot, $"rover {leg.What} gone: on foot");
            DropDriving();
            return true;
        }
        ColonyColonists.TakeRover(leg.What, this);
        if (!BotDriving.GetIn(bot, car, bot.Body.Space, now)) return false;
        Driving = true;
        Log(bot, $"got into rover {leg.What}");
        return true;
    }

    private bool Drive(Bot bot, Leg leg, double now)
    {
        if (!BotDriving.Mounted(bot)) return true;
        var p = BotDriving.Across(bot, World(bot, leg.Points[leg.At]), leg.Speed, now);
        if (p == BotDriving.Progress.Driving) return false;
        if (p == BotDriving.Progress.Arrived && leg.At + 1 < leg.Points.Length)
        {
            leg.At++;
            return false;
        }
        // stuck on the way: again from where it stands, twice; then it parks and walks home through an airlock (the
        // next drive leg would set off straight across from here, through the modules)
        if (p == BotDriving.Progress.Failed)
        {
            if (++leg.Stalls < 3)
            {
                Log(bot, $"drove {leg.What}: {bot.Drive.Why}, point {leg.At + 1}/{leg.Points.Length}: again");
                return false;
            }
            Log(bot, $"drove {leg.What}: {bot.Drive.Why}: parks here and walks");
            WalkHome(bot);
            return false;
        }
        Log(bot, $"drove {leg.What}: {p} ({bot.Drive.Why}), point {leg.At + 1}/{leg.Points.Length}");
        return true;
    }

    /// <summary>
    /// A drive's way round the colony (#1976: straight across, a rover met its modules): when the line from
    /// <paramref name="from"/> to <paramref name="to"/> passes within the modules' reach (plus a margin) of the middle, out to
    /// that ring and round it on the shorter side, a point every 30°, then on to the goal.
    /// </summary>
    private (double X, double Y, double Z)[] Ring((double X, double Y, double Z) from, (double X, double Y, double Z) to)
    {
        double ring = 0;
        foreach (var m in _host.Plan.Modules) ring = Math.Max(ring, Math.Sqrt(m.X * m.X + m.Z * m.Z) + Math.Max(m.Length, m.Width) / 2);
        ring += 14;
        // the line's nearest point to the middle
        double dx = to.X - from.X, dz = to.Z - from.Z, len2 = dx * dx + dz * dz;
        double t = len2 < 1e-6 ? 0 : Math.Clamp(-(from.X * dx + from.Z * dz) / len2, 0, 1);
        double nx = from.X + dx * t, nz = from.Z + dz * t;
        if (nx * nx + nz * nz >= ring * ring) return [to];
        double a0 = Math.Atan2(from.X, from.Z), a1 = Math.Atan2(to.X, to.Z);
        double turn = Math.IEEERemainder(a1 - a0, Math.Tau);
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(turn) / (Math.PI / 6)));
        var pts = new List<(double X, double Y, double Z)>();
        for (int k = 0; k <= steps; k++)
        {
            double a = a0 + turn * k / steps;
            pts.Add((Math.Sin(a) * ring, 0, Math.Cos(a) * ring));
        }
        pts.Add(to);
        return pts.ToArray();
    }

    private bool LeaveRover(Bot bot)
    {
        if (!BotDriving.GetOut(bot)) return false;
        Driving = false;
        if (_rover == null) ColonyColonists.ReleaseRovers(this);
        return true;
    }

    private void DropDriving()
    {
        var rest = new List<Leg>();
        foreach (var l in _legs) if (l.Kind is not (LegKind.ToRover or LegKind.Drive or LegKind.LeaveRover)) rest.Add(l);
        _legs.Clear();
        // the leg under way (the one asking) is dequeued by the caller: keep a placeholder first
        _legs.Enqueue(new Leg { Kind = LegKind.Walk });
        foreach (var l in rest) _legs.Enqueue(l);
        _rover = null;
    }

    /// <summary>Stands at its spot, facing its work.</summary>
    private void Stand(Bot bot)
    {
        if (Spot < 0) return;
        var s = _host.Crew.Spots[Spot];
        var dir = _host.Direction(s.Facing);
        bot.Intent.FaceYaw = Mathf.Atan2(-dir.X, -dir.Z);
        bot.Intent.Pitch = 0;
    }

    /// <summary>No headway on an inside walk for 4 s: logged (a body caught on a jamb), for the check to see; true then.</summary>
    private bool NoHeadway(Bot bot, double now, Leg leg)
    {
        if (now - _headwayAt < 4) return false;
        var pos = bot.Body.GlobalPosition;
        bool stuck = (pos - _headwayFrom).Length() < 0.3f;
        if (stuck)
        {
            var here = _host.ToLocal(bot.Body.FromFrame(_host.Origin, pos) is var w ? w : pos);
            var p = leg.At < leg.Points.Length ? leg.Points[leg.At] : default;
            Log(bot, FormattableString.Invariant(
                $"no headway on {leg.Kind} {leg.What}, point {leg.At}/{leg.Points.Length} ({p.X:F2}, {p.Y:F2}, {p.Z:F2}) from ({here.X:F2}, {here.Y:F2}, {here.Z:F2}), floor {bot.Body.IsOnFloor()}{Hits(bot.Body)}"));
        }
        _headwayFrom = pos;
        _headwayAt = now;
        return stuck;
    }

    /// <summary>What the body is pressed against (not the floor), for the no-headway line.</summary>
    private string Hits(Player.FootPlayer body)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < body.GetSlideCollisionCount(); i++)
        {
            var c = body.GetSlideCollision(i);
            if (c.GetNormal().Y > 0.7f) continue;
            var at = _host.ToLocal(c.GetPosition());
            sb.Append(FormattableString.Invariant($" hit {(c.GetCollider() as Node)?.Name} at ({at.X:F2}, {at.Y:F2}, {at.Z:F2})"));
        }
        return sb.ToString();
    }

    /// <summary>A point of the colony's frame in the bot's world now.</summary>
    private Vector3 World(Bot bot, (double X, double Y, double Z) p) =>
        bot.Body.FromFrame(_host.Origin, _host.ToGlobal(new Vector3((float)p.X, (float)p.Y, (float)p.Z)));

    private void Log(Bot bot, string what) => GD.Print($"[colonists] {_host.Colony.Id} {bot.Body.Name} \"{bot.Profile.Name}\": {what}");
}
