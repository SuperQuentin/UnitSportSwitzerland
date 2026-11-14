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
                Add(new Leg { Kind = LegKind.Drive, Goal = short_, What = "to the EVA" });
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
                Add(new Leg { Kind = LegKind.Drive, Goal = RoverSlot(_rover) ?? way.Outside, What = "back to its slot" });
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
            bot.Intent.Wish = to.Normalized() * Pace;
            NoHeadway(bot, now, leg);
            return false;
        }
        return true;
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
        var p = BotDriving.Across(bot, World(bot, leg.Goal), DriveSpeed, now);
        if (p == BotDriving.Progress.Driving) return false;
        Log(bot, $"drove {leg.What}: {p} ({bot.Drive.Why})");
        return true;
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

    /// <summary>No headway on an inside walk for 4 s: logged once (a body caught on a jamb), for the check to see.</summary>
    private void NoHeadway(Bot bot, double now, Leg leg)
    {
        if (now - _headwayAt < 4) return;
        var pos = bot.Body.GlobalPosition;
        if ((pos - _headwayFrom).Length() < 0.3f)
        {
            var here = _host.ToLocal(bot.Body.FromFrame(_host.Origin, pos) is var w ? w : pos);
            var p = leg.At < leg.Points.Length ? leg.Points[leg.At] : default;
            Log(bot, FormattableString.Invariant(
                $"no headway on {leg.Kind} {leg.What}, point {leg.At}/{leg.Points.Length} ({p.X:F2}, {p.Y:F2}, {p.Z:F2}) from ({here.X:F2}, {here.Y:F2}, {here.Z:F2}), floor {bot.Body.IsOnFloor()}{Hits(bot.Body)}"));
        }
        _headwayFrom = pos;
        _headwayAt = now;
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
