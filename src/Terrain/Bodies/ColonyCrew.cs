using System;
using System.Collections.Generic;

namespace UnitSport.Terrain.Bodies;

// Plain C# (linked into the unit tests).

/// <summary>What a colonist is doing (#1945).</summary>
public enum CrewActivity : byte { Asleep, Work, Eat, Exercise, Visit, Eva }

/// <summary>
/// A place a colonist stands (#1945): in the colony's frame (x east, z south; Y the floor's height over the plateau), the
/// way it faces (radians from north), its module (or -1 outside), and what it is for.
/// </summary>
public readonly record struct CrewSpot(double X, double Y, double Z, double Facing, int Module, CrewActivity For);

/// <summary>A colonist: their number, their look's seed, where they work, sleep and walk out to, their sleep shift.</summary>
public readonly record struct Colonist(int Index, int Look, int Work, int Bunk, int Eva, double SleepFromHour, bool Walker);

/// <summary>Where a colonist is at a moment: walking a path (its waypoints, how far along) or standing at a spot; or out of sight.</summary>
public readonly record struct CrewMoment(CrewActivity Activity, int Spot, (double X, double Y, double Z)[]? Path, double Along, bool Shown, bool Outside);

/// <summary>
/// A staffed colony's crew and their day (#1945, docs/plans/moon-surface.md section 8): pure from the colony's record and
/// plan, the same on every peer. <b>Spots</b> are read off the plan (the aisle of a can, the ring of the hub's consoles, a
/// dome's middle aisle, the solar field and the digger's pit outside). <b>The day</b> is cut into windows of
/// <see cref="Window"/> seconds of the shared clock; in each a colonist works at their own room, eats in a galley, uses the
/// gym, visits another room, or (one in four) goes out on an EVA; for eight hours of the colony's day (UTC) they sleep, in
/// a shift of their own. Between two inside spots they walk the colony's own path (<see cref="Path"/>: through the doors
/// and along the tubes, never through a wall) at <see cref="WalkSpeed"/>. A server bot follows the same moment through its
/// intents (<c>WalkControls</c> along the path, the airlock's <c>Request</c> on the way out); offline the figures play it.
/// </summary>
public sealed class ColonyCrew
{
    /// <summary>A window's length, seconds of the shared clock.</summary>
    public const double Window = 300;

    /// <summary>A colonist's walk, m/s (the Moon's loping walk inside, unhurried).</summary>
    public const double WalkSpeed = 1.1;

    public readonly List<CrewSpot> Spots = new();
    public readonly List<Colonist> Crew = new();
    private readonly ColonyPlan _plan;
    private readonly int _seed;
    private readonly List<int>[] _byActivity;
    private readonly List<int>[] _links;   // module -> the links it takes part in

    public ColonyCrew(MoonColony c, ColonyPlan plan)
    {
        _plan = plan;
        _seed = c.Seed;
        _byActivity = new List<int>[Enum.GetValues<CrewActivity>().Length];
        for (int i = 0; i < _byActivity.Length; i++) _byActivity[i] = new List<int>();
        _links = new List<int>[plan.Modules.Count];
        for (int i = 0; i < _links.Length; i++) _links[i] = new List<int>();
        for (int i = 0; i < plan.Links.Count; i++)
        {
            _links[plan.Doors[plan.Links[i].DoorA].Module].Add(i);
            _links[plan.Doors[plan.Links[i].DoorB].Module].Add(i);
        }
        if (!c.Staffed) return;
        foreach (var m in plan.Modules) AddSpots(m, plan.Rooms[m.Id]);
        AddOutside(plan);
        var rng = new Random(c.Seed * 31 + 7);
        int n = c.Theme == ColonyTheme.MoonVillage ? 10 : 8;
        var work = new List<int>();
        foreach (var k in new[] { CrewActivity.Work }) work.AddRange(_byActivity[(int)k]);
        var bunks = _byActivity[(int)CrewActivity.Asleep];
        var eva = _byActivity[(int)CrewActivity.Eva];
        for (int i = 0; i < n && work.Count > 0; i++)
        {
            Crew.Add(new Colonist(i, c.Seed * 101 + i * 17, work[(i * 5 + rng.Next(work.Count)) % work.Count],
                bunks.Count > 0 ? bunks[i % bunks.Count] : -1, eva.Count > 0 && i % 4 == 3 ? eva[i % eva.Count] : -1,
                // three sleep shifts, eight hours each, staggered
                (i % 3) * 8 + rng.NextDouble() * 2, Walker: true));
        }
    }

    /// <summary>A point in a module's node frame (x across, z along its axis toward its back: the node's +Z) in the colony's frame.</summary>
    public static (double X, double Z) Local(ColonyModule m, double lx, double lz)
    {
        double c = Math.Cos(m.Yaw), s = Math.Sin(m.Yaw);
        return (m.X + lx * c - lz * s, m.Z + lx * s + lz * c);
    }

    /// <summary>A facing (radians from north) from a direction in a module's node frame.</summary>
    private static double FacingOf(ColonyModule m, double dx, double dz)
    {
        double c = Math.Cos(m.Yaw), s = Math.Sin(m.Yaw);
        double x = dx * c - dz * s, z = dx * s + dz * c;
        return Math.Atan2(x, -z);
    }

    private void Add(ColonyModule m, double lx, double lz, double fx, double fz, CrewActivity a, double y = 1.55)
    {
        var (x, z) = Local(m, lx, lz);
        Spots.Add(new CrewSpot(x, y, z, FacingOf(m, fx, fz), m.Id, a));
        _byActivity[(int)a].Add(Spots.Count - 1);
    }

    private void AddSpots(ColonyModule m, RoomKind room)
    {
        double l = m.Length;
        switch (m.Kind)
        {
            case ModuleKind.Hub:
                // at the consoles' ring, facing out to them (consoles 30° off the diagonals, 4.1 m out)
                for (int k = 0; k < 4; k++)
                {
                    double a = Math.PI / 4 + k * Math.PI / 2 + (k % 2 == 0 ? -0.26 : 0.26);
                    Add(m, Math.Sin(a) * 3.1, Math.Cos(a) * 3.1, Math.Sin(a), Math.Cos(a), CrewActivity.Work);
                }
                break;
            case ModuleKind.Can:
            {
                var a = room switch
                {
                    RoomKind.Galley => CrewActivity.Eat, RoomKind.Gym => CrewActivity.Exercise, RoomKind.Quarters => CrewActivity.Asleep,
                    RoomKind.Storage => CrewActivity.Visit, _ => CrewActivity.Work,
                };
                // two places on the aisle, a quarter in from each end, turned to the fittings either side
                Add(m, 0, -l / 4, 1, 0, a);
                Add(m, 0, l / 4, -1, 0, a);
                break;
            }
            case ModuleKind.Stack:
                Add(m, 0, 0, 0, 1, CrewActivity.Asleep);
                Add(m, 1.2, 1.2, 1, 1, CrewActivity.Visit);
                break;
            case ModuleKind.Dome:
                Add(m, 0, -m.Width * 0.15, 1, 0, room == RoomKind.Greenhouse ? CrewActivity.Work : CrewActivity.Visit);
                break;
        }
    }

    /// <summary>The EVA places: by the solar field's masts, at the digger's pit, at the free pads' edge.</summary>
    private void AddOutside(ColonyPlan plan)
    {
        foreach (var i in plan.Industry)
        {
            if (i.Kind == IndustryKind.SolarArray && _byActivity[(int)CrewActivity.Eva].Count < 3)
            {
                var (ax, az) = ColonyPlan.Along(i.Yaw + Math.PI / 2);
                Spots.Add(new CrewSpot(i.X + ax * 2.5, 0, i.Z + az * 2.5, i.Yaw - Math.PI / 2, -1, CrewActivity.Eva));
                _byActivity[(int)CrewActivity.Eva].Add(Spots.Count - 1);
            }
            if (i.Kind == IndustryKind.Digger && i.Size > 0)
            {
                Spots.Add(new CrewSpot(i.X + i.Size + 12, 0, i.Z, -Math.PI / 2, -1, CrewActivity.Eva));
                _byActivity[(int)CrewActivity.Eva].Add(Spots.Count - 1);
            }
        }
    }

    /// <summary>A colonist's spot in a window and what they do there (null: asleep, out of sight in their bunk).</summary>
    public (int Spot, CrewActivity Activity)? Plan(in Colonist who, long window, double hourUtc)
    {
        double asleep = (hourUtc - who.SleepFromHour + 48) % 24;
        if (asleep < 8) return null;
        ulong h = Mix((ulong)_seed, (ulong)who.Index, (ulong)window);
        double r = (h & 0xFFFF) / 65536.0;
        int pick = (int)((h >> 16) % 1000);
        if (who.Eva >= 0 && r < 0.4) return (who.Eva, CrewActivity.Eva);
        if (r < 0.62 || Count(CrewActivity.Eat) + Count(CrewActivity.Exercise) + Count(CrewActivity.Visit) == 0)
            return (who.Work, CrewActivity.Work);
        if (r < 0.77 && Count(CrewActivity.Eat) > 0) return (Of(CrewActivity.Eat, pick), CrewActivity.Eat);
        if (r < 0.87 && Count(CrewActivity.Exercise) > 0) return (Of(CrewActivity.Exercise, pick), CrewActivity.Exercise);
        var others = _byActivity[(int)CrewActivity.Work];
        return (others[pick % others.Count], CrewActivity.Visit);
    }

    private int Count(CrewActivity a) => _byActivity[(int)a].Count;

    private int Of(CrewActivity a, int pick) => _byActivity[(int)a][pick % _byActivity[(int)a].Count];

    /// <summary>
    /// Where a colonist is at <paramref name="t"/> (shared-clock seconds) with the colony's hour <paramref name="hourUtc"/>:
    /// asleep (not shown), outside at an EVA spot, walking from the last window's spot to this one's along the path (both
    /// inside), or standing at it.
    /// </summary>
    public CrewMoment At(in Colonist who, double t, double hourUtc)
    {
        long w = (long)Math.Floor(t / Window);
        double into = t - w * Window;
        var now = Plan(who, w, hourUtc);
        if (now is not { } n) return new CrewMoment(CrewActivity.Asleep, who.Bunk, null, 0, false, false);
        var spot = Spots[n.Spot];
        if (spot.Module < 0) return new CrewMoment(n.Activity, n.Spot, null, 1, true, true);
        // from the last window's spot, if they were inside and awake (an hour back: the same hour's plan)
        if (Plan(who, w - 1, hourUtc - Window / 3600) is { } before && Spots[before.Spot].Module >= 0 && before.Spot != n.Spot)
        {
            var path = Path(before.Spot, n.Spot);
            double len = Length(path);
            double walked = into * WalkSpeed;
            if (walked < len) return new CrewMoment(n.Activity, n.Spot, path, walked, true, false);
        }
        return new CrewMoment(n.Activity, n.Spot, null, 1, true, false);
    }

    public static double Length((double X, double Y, double Z)[] path)
    {
        double len = 0;
        for (int i = 0; i + 1 < path.Length; i++) len += Dist(path[i], path[i + 1]);
        return len;
    }

    /// <summary>The point <paramref name="along"/> metres along a path, and the direction it goes there (radians from north).</summary>
    public static ((double X, double Y, double Z) At, double Heading) Walk((double X, double Y, double Z)[] path, double along)
    {
        for (int i = 0; i + 1 < path.Length; i++)
        {
            double d = Dist(path[i], path[i + 1]);
            if (along > d && i + 2 < path.Length) { along -= d; continue; }
            double f = d < 1e-6 ? 1 : Math.Clamp(along / d, 0, 1);
            var a = path[i];
            var b = path[i + 1];
            return ((a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f), Math.Atan2(b.X - a.X, -(b.Z - a.Z)));
        }
        return (path[^1], 0);
    }

    private static double Dist((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>
    /// The way between two inside spots: through the modules the colony's tubes join (breadth first), out of each by its door
    /// and in at the next's, round the hub's map table by its consoles' ring, along a can's aisle.
    /// </summary>
    public (double X, double Y, double Z)[] Path(int from, int to)
    {
        var a = Spots[from];
        var b = Spots[to];
        return Path((a.X, a.Y, a.Z), a.Module, (b.X, b.Y, b.Z), b.Module);
    }

    /// <summary>The way between two inside points, each in its module: <see cref="Path(int, int)"/>'s walk for any two points.</summary>
    public (double X, double Y, double Z)[] Path((double X, double Y, double Z) a, int fromModule, (double X, double Y, double Z) b, int toModule)
    {
        var pts = new List<(double, double, double)> { (a.X, a.Y, a.Z) };
        var route = Route(fromModule, toModule);
        for (int i = 0; i + 1 < route.Count; i++)
        {
            var link = Between(route[i], route[i + 1]);
            var dOut = _plan.Doors[link.DoorA].Module == route[i] ? _plan.Doors[link.DoorA] : _plan.Doors[link.DoorB];
            var dIn = _plan.Doors[link.DoorA].Module == route[i] ? _plan.Doors[link.DoorB] : _plan.Doors[link.DoorA];
            RoundHub(pts, route[i], (pts[^1].Item1, pts[^1].Item3), (dOut.X, dOut.Z));
            // out of a can's side door from its aisle, never across its furniture (#1976)
            if (Aisle(route[i], dOut) is { } aisleOut) pts.Add(aisleOut);
            // a step in from each doorway, so the walk turns inside the room, not in the doorway
            var (ix, iz) = ColonyPlan.Along(dOut.Out);
            pts.Add((dOut.X - ix * 0.8, 1.55, dOut.Z - iz * 0.8));
            pts.Add((dOut.X, 1.55, dOut.Z));
            pts.Add((dIn.X, 1.55, dIn.Z));
            var (jx, jz) = ColonyPlan.Along(dIn.Out);
            pts.Add((dIn.X - jx * 0.8, 1.55, dIn.Z - jz * 0.8));
            if (Aisle(route[i + 1], dIn) is { } aisleIn) pts.Add(aisleIn);
        }
        RoundHub(pts, toModule, (pts[^1].Item1, pts[^1].Item3), (b.X, b.Z));
        pts.Add((b.X, b.Y, b.Z));
        return pts.ToArray();
    }

    // ---- out on an EVA, through an airlock (#1976: the colonists as bots) -------------------------------------------

    /// <summary>The outer stairs' run (<c>ColonyKit</c>: 7 treads of 0.34 m) and a step past it: where outside begins.</summary>
    public const double StairsOut = 7 * 0.34 + 1.2;

    /// <summary>How far before an airlock's inner door a colonist waits in the tube while it cycles, metres.</summary>
    public const double WaitBack = 1.2;

    /// <summary>
    /// An airlock's places a colonist walks through (colony frame): where it waits in the tube before the inner door,
    /// that door, the chamber's middle, the outer door, and the ground past the outer stairs.
    /// </summary>
    public readonly record struct LockWay((double X, double Y, double Z) Wait, (double X, double Y, double Z) Inner,
        (double X, double Y, double Z) Centre, (double X, double Y, double Z) Outer, (double X, double Y, double Z) Outside, int Module);

    /// <summary>The way through airlock <paramref name="index"/> (of the plan's <c>Airlocks</c>, the hangar not among them).</summary>
    public LockWay Lock(int index)
    {
        var a = _plan.Airlocks[index];
        var m = _plan.Modules[a.Module];
        var inner = _plan.Doors[a.Inner];
        var outer = _plan.Doors[a.Outer];
        // the tube behind the inner door: wait part way along it (a short tube: half way)
        double tube = WaitBack;
        foreach (int li in _links[a.Module])
        {
            var l = _plan.Links[li];
            if (l.DoorA != a.Inner && l.DoorB != a.Inner) continue;
            var other = _plan.Doors[l.DoorA == a.Inner ? l.DoorB : l.DoorA];
            tube = Math.Min(WaitBack, Math.Sqrt((other.X - inner.X) * (other.X - inner.X) + (other.Z - inner.Z) * (other.Z - inner.Z)) / 2);
        }
        var (ix, iz) = ColonyPlan.Along(inner.Out);
        var (ox, oz) = ColonyPlan.Along(outer.Out);
        return new LockWay((inner.X + ix * tube, 1.55, inner.Z + iz * tube), (inner.X, 1.55, inner.Z), (m.X, 1.55, m.Z),
            (outer.X, 1.55, outer.Z), (outer.X + ox * StairsOut, 0, outer.Z + oz * StairsOut), a.Module);
    }

    /// <summary>The airlock nearest an outside spot (by the ground past its stairs), or -1 with none.</summary>
    public int LockFor(int outsideSpot)
    {
        var s = Spots[outsideSpot];
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = 0; i < _plan.Airlocks.Count; i++)
        {
            var o = Lock(i).Outside;
            double d = (o.X - s.X) * (o.X - s.X) + (o.Z - s.Z) * (o.Z - s.Z);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>From an inside spot to the place before airlock <paramref name="index"/>'s inner door, in the tube.</summary>
    public (double X, double Y, double Z)[] ToLock(int from, int index)
    {
        var a = Spots[from];
        var way = Lock(index);
        var through = Path((a.X, a.Y, a.Z), a.Module, way.Centre, way.Module);
        // the walk up to the inner door, stopping before it: the door itself and the chamber come after the cycle
        var pts = new List<(double X, double Y, double Z)>();
        foreach (var p in through)
        {
            if (Math.Abs(p.X - way.Inner.X) < 1e-6 && Math.Abs(p.Z - way.Inner.Z) < 1e-6) break;
            pts.Add(p);
        }
        // the tube's far door is the last point before the inner one: the wait is along the tube from it
        pts.Add(way.Wait);
        return pts.ToArray();
    }

    /// <summary>From airlock <paramref name="index"/>'s chamber, through its inner door, to an inside spot.</summary>
    public (double X, double Y, double Z)[] FromLock(int index, int to)
    {
        var b = Spots[to];
        var way = Lock(index);
        return Path(way.Centre, way.Module, (b.X, b.Y, b.Z), b.Module);
    }

    /// <summary>The point on a can's aisle level with its side door (a dome's way in, #1976); null for any other door.</summary>
    private (double X, double Y, double Z)? Aisle(int module, ColonyDoor door)
    {
        var m = _plan.Modules[module];
        if (m.Kind != ModuleKind.Can) return null;
        double c = Math.Cos(m.Yaw), s = Math.Sin(m.Yaw);
        double dx = door.X - m.X, dz = door.Z - m.Z, along = -dx * s + dz * c;
        if (Math.Abs(along) >= m.Length / 2 - 0.5) return null;
        var (x, z) = Local(m, 0, along);
        return (x, 1.55, z);
    }

    /// <summary>In the hub, from one point to another round its map table: by the ring 3.1 m out when they are far apart round it.</summary>
    private void RoundHub(List<(double, double, double)> pts, int module, (double X, double Z) from, (double X, double Z) to)
    {
        var m = _plan.Modules[module];
        if (m.Kind != ModuleKind.Hub) return;
        double a0 = Math.Atan2(from.X - m.X, from.Z - m.Z), a1 = Math.Atan2(to.X - m.X, to.Z - m.Z);
        double d = Math.IEEERemainder(a1 - a0, Math.Tau);
        int steps = (int)Math.Ceiling(Math.Abs(d) / (Math.PI / 4));
        for (int k = 1; k < steps; k++)
        {
            double a = a0 + d * k / steps;
            pts.Add((m.X + Math.Sin(a) * 2.6, 1.55, m.Z + Math.Cos(a) * 2.6));
        }
    }

    private ColonyLink Between(int a, int b)
    {
        foreach (int li in _links[a])
        {
            var l = _plan.Links[li];
            int ma = _plan.Doors[l.DoorA].Module, mb = _plan.Doors[l.DoorB].Module;
            if (ma == a && mb == b || ma == b && mb == a) return l;
        }
        throw new InvalidOperationException($"no link between modules {a} and {b}");
    }

    /// <summary>The modules from one to another through the tubes (breadth first; the airlocks and the hangar are rooms too).</summary>
    public List<int> Route(int from, int to)
    {
        var prev = new int[_plan.Modules.Count];
        Array.Fill(prev, -2);
        prev[from] = -1;
        var q = new Queue<int>();
        q.Enqueue(from);
        while (q.Count > 0)
        {
            int m = q.Dequeue();
            if (m == to) break;
            foreach (int li in _links[m])
            {
                var l = _plan.Links[li];
                int o = _plan.Doors[l.DoorA].Module == m ? _plan.Doors[l.DoorB].Module : _plan.Doors[l.DoorA].Module;
                if (prev[o] != -2) continue;
                prev[o] = m;
                q.Enqueue(o);
            }
        }
        var route = new List<int>();
        for (int m = to; m != -1 && m >= 0; m = prev[m])
        {
            route.Add(m);
            if (prev[m] == -2) return [from];
        }
        route.Reverse();
        return route;
    }

    private static ulong Mix(ulong a, ulong b, ulong c)
    {
        ulong h = a * 0x9E3779B97F4A7C15UL ^ b * 0xC2B2AE3D27D4EB4FUL ^ c * 0x165667B19E3779F9UL;
        h ^= h >> 31;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 29;
        return h;
    }
}
