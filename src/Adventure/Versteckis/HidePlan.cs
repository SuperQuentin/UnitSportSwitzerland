using System;
using System.Collections.Generic;
using System.Linq;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Versteckis;

// Plain C#, no Godot: linked into the unit tests (VersteckisTests; docs/notes/adventure/versteckis.md).

/// <summary>
/// A building as the hide plan reads it (#1268), in LV95 metres: its centre, its long axis (unit, east and north),
/// its width along that axis and depth across, its kind, its main door and the door's outward direction.
/// Made from <c>Interiors.Footprint</c> by the Godot side, by hand in the tests.
/// </summary>
public readonly record struct VsBuilding(double E, double N, double UE, double UN, float Width, float Depth, BuildingKind Kind,
    double DoorE, double DoorN, double OutE, double OutN)
{
    /// <summary>Whether a point lies inside the box, grown by <paramref name="pad"/>.</summary>
    public bool Contains(double e, double n, float pad = 0f)
    {
        double dx = e - E, dn = n - N;
        double u = dx * UE + dn * UN, v = -dx * UN + dn * UE;
        return Math.Abs(u) <= Width / 2 + pad && Math.Abs(v) <= Depth / 2 + pad;
    }
}

/// <summary>One decor prop of a game's area: a copy hiders blend into. <see cref="Yaw"/> as a body's (0 faces north, + turns west).</summary>
public readonly record struct VsDecor(VsProp Kind, double E, double N, float Yaw);

/// <summary>
/// The decor of a game's area (#1268, plan section 2): where the village's own props stand, so a hider has something to
/// blend into. Pure and hashed per spot from the game's seed (as <c>TileDecor</c> and <c>HerdPlan</c> are), so every
/// peer and the server compute the same list from the same tiles and none of it goes on the wire. The rules:
/// <list type="bullet">
/// <item>hay bales on open grass, in rows of 2 to 5, 10 m off roads and buildings;</item>
/// <item>a woodpile against the long wall of one house or farm building in two;</item>
/// <item>milk cans in pairs at the doors of farm buildings;</item>
/// <item>one to three gnomes within 6 m of the door of one house in three;</item>
/// <item>benches on the verges every 40 to 70 m of road, and one facing the church door;</item>
/// <item>the real herds' cows (drawn by the herds themselves, listed so a cow disguise is offered);</item>
/// <item>a flower box under a front window of one house in two.</item>
/// </list>
/// Nothing on a road or in a building; 300 at most.
/// </summary>
public static class HidePlan
{
    public const int MaxDecor = 300;
    public const float RoadClear = 3f, HayRoadClear = 10f, BenchVerge = 4.5f;

    public static List<VsDecor> For(double ce, double cn, float radius, int seed, IReadOnlyList<VsBuilding> buildings,
        IReadOnlyList<(double E, double N)[]> roads, Func<double, double, bool> grass, IReadOnlyList<(double E, double N)> cows,
        (double E, double N)? churchDoor = null)
    {
        var list = new List<VsDecor>();
        uint s = (uint)seed;
        bool Free(double e, double n, float roadClear, float pad)
        {
            if (!VsRules.Inside(ce, cn, radius - 1.5f, e, n)) return false;
            foreach (var b in buildings)
                if (b.Contains(e, n, pad)) return false;
            return RoadDistance(roads, e, n) >= roadClear;
        }
        // pad: how far off every wall its centre must stand (a woodpile and a flower box stand against one)
        void Add(VsProp kind, double e, double n, float yaw, float roadClear = RoadClear, float pad = 0.6f)
        {
            if (Free(e, n, roadClear, pad)) list.Add(new VsDecor(kind, e, n, yaw));
        }

        // buildings: woodpiles, milk cans, gnomes, flower boxes, each hashed on the building's own position
        foreach (var b in buildings)
        {
            if (!VsRules.Inside(ce, cn, radius, b.E, b.N)) continue;
            uint key = Hash((uint)Math.Round(b.E * 2), (uint)Math.Round(b.N * 2), s);
            // the door's frame: out of the front wall, and along it
            double oe = b.OutE, on = b.OutN, ae = -on, an = oe;
            float faceOut = YawOf(oe, on);
            if (b.Kind is BuildingKind.House or BuildingKind.Agricultural && Unit(key, 1) < 0.5f)
            {
                // the long wall: the axis is along the width; a pile against the side +v (or -v)
                bool alongU = b.Width >= b.Depth;
                double le = alongU ? b.UE : -b.UN, ln = alongU ? b.UN : b.UE;          // along the long wall
                double ne = alongU ? -b.UN : b.UE, nn = alongU ? b.UE : b.UN;          // out of it
                float half = (alongU ? b.Depth : b.Width) / 2 + 0.65f;
                float side = Unit(key, 2) < 0.5f ? 1f : -1f;
                float slide = (Unit(key, 3) - 0.5f) * Math.Max(0f, (alongU ? b.Width : b.Depth) - 2.4f);
                Add(VsProp.Woodpile, b.E + ne * half * side + le * slide, b.N + nn * half * side + ln * slide, YawOf(le, ln), 1.5f, 0.3f);
            }
            if (b.Kind == BuildingKind.Agricultural)
                for (int k = -1; k <= 1; k += 2)
                    Add(VsProp.MilkCan, b.DoorE + oe * 0.9 + ae * (1.2 + 0.25 * k), b.DoorN + on * 0.9 + an * (1.2 + 0.25 * k), faceOut + 0.3f * k, 1.2f);
            if (b.Kind == BuildingKind.House && Unit(key, 4) < 1f / 3f)
            {
                int gnomes = 1 + (int)(Unit(key, 5) * 3f);
                for (int g = 0; g < gnomes; g++)
                {
                    double out_ = 2 + Unit(key, 10u + (uint)g) * 4, lat = (Unit(key, 20u + (uint)g) - 0.5) * 6;
                    Add(VsProp.Gnome, b.DoorE + oe * out_ + ae * lat, b.DoorN + on * out_ + an * lat, faceOut + (Unit(key, 30u + (uint)g) - 0.5f), 1.5f);
                }
            }
            if (b.Kind == BuildingKind.House && Unit(key, 6) < 0.5f)
            {
                float lat = Unit(key, 7) < 0.5f ? 2.2f : -2.2f;
                Add(VsProp.FlowerBox, b.DoorE + oe * 0.35 + ae * lat, b.DoorN + on * 0.35 + an * lat, faceOut, 0.8f, 0.15f);
            }
        }

        // benches on the verges, every 40-70 m of road inside the circle, and one facing the church door
        int road = 0;
        foreach (var line in roads)
        {
            uint rk = Hash((uint)road++, 0x5EA7, s);
            double next = 20 + Unit(rk, 1) * 30, walked = 0;
            for (int i = 1; i < line.Length; i++)
            {
                double de = line[i].E - line[i - 1].E, dn = line[i].N - line[i - 1].N, len = Math.Sqrt(de * de + dn * dn);
                if (len < 0.01) continue;
                while (walked + len >= next)
                {
                    double t = (next - walked) / len;
                    double e = line[i - 1].E + de * t, n = line[i - 1].N + dn * t;
                    uint bk = Hash((uint)Math.Round(e), (uint)Math.Round(n), s ^ 0xBE4C);
                    double side = Unit(bk, 1) < 0.5f ? 1 : -1;
                    double pe = -dn / len * side, pn = de / len * side;   // out to the verge
                    Add(VsProp.Bench, e + pe * BenchVerge, n + pn * BenchVerge, YawOf(-pe, -pn), BenchVerge - 1.2f);
                    next += 40 + Unit(bk, 2) * 30;
                }
                walked += len;
            }
        }
        if (churchDoor is { } door)
        {
            double de = door.E - ce, dn = door.N - cn;
            // in front of the door, facing it: out of the church along the door's own outward when known
            var church = buildings.Where(b => b.Kind == BuildingKind.Sacral)
                .OrderBy(b => (b.DoorE - door.E) * (b.DoorE - door.E) + (b.DoorN - door.N) * (b.DoorN - door.N)).Select(b => (VsBuilding?)b).FirstOrDefault();
            if (church is { } c) { de = c.OutE; dn = c.OutN; }
            double l = Math.Sqrt(de * de + dn * dn);
            if (l > 0.01) Add(VsProp.Bench, door.E + de / l * 6, door.N + dn / l * 6, YawOf(-de, -dn), 2f);
        }

        // hay bales: a 25 m lattice over the circle, one cell in four on open grass gets a row
        int cells = (int)Math.Ceiling(radius / 25f);
        for (int gx = -cells; gx <= cells; gx++)
            for (int gy = -cells; gy <= cells; gy++)
            {
                uint hk = Hash((uint)(gx + 1000), (uint)(gy + 1000), s ^ 0x4A7B);
                if (Unit(hk, 1) >= 0.25f) continue;
                double e0 = ce + gx * 25 + (Unit(hk, 2) - 0.5) * 12, n0 = cn + gy * 25 + (Unit(hk, 3) - 0.5) * 12;
                if (!grass(e0, n0)) continue;
                float yaw = Unit(hk, 4) * MathF.PI;
                double re = -Math.Sin(yaw), rn = Math.Cos(yaw);   // the row runs along the yaw
                int count = 2 + (int)(Unit(hk, 5) * 4f);
                for (int k = 0; k < count; k++)
                {
                    double e = e0 + re * k * 1.8, n = n0 + rn * k * 1.8;
                    if (grass(e, n)) Add(VsProp.HayBale, e, n, yaw + Unit(hk, 6u + (uint)k), HayRoadClear);
                }
            }

        // the cows of the herds inside the circle (the herds draw them; listed for the radial and the bots)
        foreach (var (e, n) in cows)
            if (VsRules.Inside(ce, cn, radius, e, n)) list.Add(new VsDecor(VsProp.Cow, e, n, 0f));

        if (list.Count > MaxDecor)
        {
            // thinned by each spot's own hash, never by order, so the same spots stay whatever was added before
            list = list.OrderBy(d => Hash((uint)Math.Round(d.E * 4), (uint)Math.Round(d.N * 4), s ^ 0x7417)).Take(MaxDecor).ToList();
        }
        return list;
    }

    /// <summary>The kinds a plan holds, in the props' order: the Disguise radial's choices (no cow without a herd).</summary>
    public static List<VsProp> Kinds(IEnumerable<VsDecor> decor)
    {
        var set = new HashSet<VsProp>(decor.Select(d => d.Kind));
        return VsProps.All.Select(p => p.Kind).Where(set.Contains).ToList();
    }

    /// <summary>Whether a kind belongs where it stands by the plan's rules (the seeker bot's "out of place").</summary>
    public static bool InPlace(IReadOnlyList<VsDecor> decor, VsProp kind, double e, double n, float within = 8f)
    {
        foreach (var d in decor)
            if (d.Kind == kind && (d.E - e) * (d.E - e) + (d.N - n) * (d.N - n) <= within * within) return true;
        return false;
    }

    /// <summary>The nearest decor prop to a point within <paramref name="reach"/>, or null.</summary>
    public static VsDecor? Nearest(IReadOnlyList<VsDecor> decor, double e, double n, float reach)
    {
        VsDecor? best = null;
        double bd = reach * reach;
        foreach (var d in decor)
        {
            double dd = (d.E - e) * (d.E - e) + (d.N - n) * (d.N - n);
            if (dd <= bd) { bd = dd; best = d; }
        }
        return best;
    }

    /// <summary>Metres from a point to the nearest road centreline.</summary>
    public static double RoadDistance(IReadOnlyList<(double E, double N)[]> roads, double e, double n)
    {
        double best = double.MaxValue;
        foreach (var line in roads)
            for (int i = 1; i < line.Length; i++)
            {
                double ax = line[i - 1].E, ay = line[i - 1].N, bx = line[i].E, by = line[i].N;
                double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
                double t = l2 < 1e-9 ? 0 : Math.Clamp(((e - ax) * dx + (n - ay) * dy) / l2, 0, 1);
                double px = ax + dx * t - e, py = ay + dy * t - n;
                best = Math.Min(best, px * px + py * py);
            }
        return best == double.MaxValue ? double.MaxValue : Math.Sqrt(best);
    }

    /// <summary>A body's yaw facing the direction (east, north): 0 north, + to the west.</summary>
    public static float YawOf(double e, double n) => (float)Math.Atan2(-e, n);

    public static uint Hash(uint a, uint b, uint salt)
    {
        ulong x = ((ulong)a << 32 | b) ^ ((ulong)salt * 0x9E3779B97F4A7C15UL);
        x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27; x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return (uint)x ^ (uint)(x >> 32);
    }

    public static float Unit(uint key, uint k) => (Hash(key, k, 0x51D) & 0xFFFFFF) / 16777216f;
}

/// <summary>The objectives' sites of an area (#1268, plan 4.3), pure, from the same inputs as the decor.</summary>
public static class VsSites
{
    /// <summary>
    /// The bell at the church door (the area's door), the Höhenfeuer on the highest open grass of the circle, the
    /// milk can from the farm door nearest the bench to the bench on the road nearest the centre (120 m at most).
    /// </summary>
    public static List<VsObjective> Plan(double ce, double cn, float radius, double doorE, double doorN,
        IReadOnlyList<VsBuilding> buildings, IReadOnlyList<(double E, double N)[]> roads, Func<double, double, bool> grass,
        Func<double, double, float> ground)
    {
        var sites = new List<VsObjective>
        {
            new() { Site = VsSite.Bell, E = doorE, N = doorN, Alt = ground(doorE, doorN) },
        };
        // the fire: the highest open cell (10 m lattice) clear of buildings and roads, inside the circle
        double fe = ce, fn = cn;
        float best = float.MinValue;
        for (double x = -radius; x <= radius; x += 10)
            for (double y = -radius; y <= radius; y += 10)
            {
                double e = ce + x, n = cn + y;
                if (!VsRules.Inside(ce, cn, radius - 15, e, n) || !grass(e, n)) continue;
                if (buildings.Any(b => b.Contains(e, n, 4f)) || HidePlan.RoadDistance(roads, e, n) < 6) continue;
                float h = ground(e, n);
                if (h > best + 0.01f) { best = h; fe = e; fn = n; }
            }
        sites.Add(new VsObjective { Site = VsSite.Fire, E = fe, N = fn, Alt = ground(fe, fn) });
        // the bench: the road point nearest the centre, out to its verge
        double be = ce + 8, bn = cn;
        double bd = double.MaxValue;
        foreach (var line in roads)
            for (int i = 1; i < line.Length; i++)
            {
                double ax = line[i - 1].E, ay = line[i - 1].N, dx = line[i].E - ax, dy = line[i].N - ay, l2 = dx * dx + dy * dy;
                if (l2 < 1e-6) continue;
                double t = Math.Clamp(((ce - ax) * dx + (cn - ay) * dy) / l2, 0, 1);
                double px = ax + dx * t, py = ay + dy * t, d = (px - ce) * (px - ce) + (py - cn) * (py - cn);
                if (d >= bd || !VsRules.Inside(ce, cn, radius - 10, px, py)) continue;
                double l = Math.Sqrt(l2), oe = -dy / l, on = dx / l;
                // the verge on the centre's side
                if ((ce - px) * oe + (cn - py) * on < 0) { oe = -oe; on = -on; }
                bd = d;
                be = px + oe * HidePlan.BenchVerge;
                bn = py + on * HidePlan.BenchVerge;
            }
        // the can: from the farm door nearest the bench (any door if there is no farm), within the carry
        var doors = buildings.Where(b => b.Kind == BuildingKind.Agricultural).ToList();
        if (doors.Count == 0) doors = buildings.Where(b => b.Kind != BuildingKind.Sacral).ToList();
        double pe = be + 30, pn = bn;
        double pd = double.MaxValue;
        foreach (var b in doors)
        {
            double d = (b.DoorE - be) * (b.DoorE - be) + (b.DoorN - bn) * (b.DoorN - bn);
            if (d < pd && d <= VsRules.CanCarryMax * VsRules.CanCarryMax) { pd = d; pe = b.DoorE + b.OutE * 1.2; pn = b.DoorN + b.OutN * 1.2; }
        }
        sites.Add(new VsObjective { Site = VsSite.Can, E = be, N = bn, Alt = ground(be, bn), FromE = pe, FromN = pn });
        return sites;
    }
}
