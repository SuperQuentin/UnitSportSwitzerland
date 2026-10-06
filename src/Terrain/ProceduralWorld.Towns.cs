using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

public sealed partial class ProceduralWorld
{
    // ---- towns (#559) ------------------------------------------------------------------------
    //
    // The biggest village slots (HalfLength is 160 + 260 h, so the top 15% by length is above 381 m)
    // are towns, when the ground allows it. A town is a village with
    //   - a main street built tight round its crossroads (TownCoreM either way), so the urban
    //     density there reaches what RoadGen's lights want,
    //   - a cross road (class Road, a priority road) straight through the valley road at the
    //     centre: uphill with houses both sides, downhill over the river on a bridge deck, and on
    //     past the other bank to a street along it (class Minor, houses both sides).
    // Everything is a function of the slot and of the raw generated height (no blend), so every
    // peer and every tile plans the same town. A slot that cannot host it stays a village.

    private const double TownHalfLength = 381;
    private const double TownCoreM = 150;
    /// <summary>The bridge starts and ends this far outside the carved channel's bank, m.</summary>
    private const double BridgeMargin = 6;
    /// <summary>A deck's height over the higher of its two banks, m.</summary>
    private const double DeckClearance = 0.5;
    /// <summary>The street running up to a bridge climbs to its deck over this length, m.</summary>
    private const double ApproachM = 30;
    /// <summary>From the bridge's far end to the street along the other bank, m.</summary>
    private const double FarArmM = 45;

    /// <summary>
    /// The generated towns (#559), like <see cref="VillageCentres"/>: the slots big enough to be one
    /// that could host the layout (<paramref name="slots"/> counts those that could not too).
    /// </summary>
    public IEnumerable<(double E, double N, int Buildings, string Name)> TownCentres(out int slots)
    {
        double half = RadiusTiles * 1000.0;
        var towns = new List<(double E, double N, int Buildings, string Name)>();
        slots = 0;
        foreach (var v in VillagesNear(CenterE - half, CenterN - half, CenterE + half, CenterN + half))
        {
            var s = v.Slot;
            if (Math.Abs(s.E - CenterE) > half || Math.Abs(s.N - CenterN) > half || !s.IsTown) continue;
            slots++;
            if (v.IsTown) towns.Add((s.E, s.N, v.Buildings.Count, $"Town {s.Id}"));
        }
        return towns;
    }

    /// <summary>Whether a point lies on a river's wet flat bottom (for plan views and checks; no culvert under a road is excluded).</summary>
    public static bool IsWetChannel(double e, double n)
    {
        var f = FineNear(e, n);
        return f.Dr < f.Half && f.Wet * f.Keep > 0.5;
    }

    private sealed record Town(Street Up, Street Down, Street Bridge, Street Far, Street Bank, double BankMouth);

    /// <summary>The nearest channel exactly: distance, bank, half-width, wet, and its bed (level less depth) there.</summary>
    private readonly record struct Channel(double Dr, double Bank, double Half, bool Wet, double Bed = 0);

    /// <summary>The nearest river channel to a point (an exact distance, not the 5 m lattice's), or none.</summary>
    private static Channel ChannelAt(double e, double n)
    {
        var net = Network.Instance;
        double best = double.MaxValue;
        var c = new Channel(NoChannel, 0, 0, false);
        foreach (int s in net.Channels.At(e, n))
        {
            var (d, u) = SegmentDistance(e, n, net.PE[s], net.PN[s], net.PE[s + 1], net.PN[s + 1]);
            if (d >= best) continue;
            best = d;
            c = new Channel(d, net.Bank[s] + (net.Bank[s + 1] - net.Bank[s]) * u,
                net.Half[s] + (net.Half[s + 1] - net.Half[s]) * u, net.Water[s] && net.Water[s + 1],
                net.Bed[s] + (net.Bed[s + 1] - net.Bed[s]) * u - (net.Depth[s] + (net.Depth[s + 1] - net.Depth[s]) * u));
        }
        return c;
    }

    /// <summary>A point of a valley road as it is drawn (the Catmull-Rom through its nodes), at an arc length.</summary>
    private static (double E, double N) DrawnPoint(Line line, double s)
    {
        int i = Array.BinarySearch(line.S, s);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, line.Count - 2);
        double t = (s - line.S[i]) / Math.Max(1e-9, line.S[i + 1] - line.S[i]);
        int i0 = Math.Max(0, i - 1), i3 = Math.Min(line.Count - 1, i + 2);
        return (CatmullRom(line.E[i0], line.E[i], line.E[i + 1], line.E[i3], t),
            CatmullRom(line.N[i0], line.N[i], line.N[i + 1], line.N[i3], t));
    }

    /// <summary>A straight street from a point, every <see cref="RoadStep"/>, ending exactly at its length.</summary>
    private static List<(double E, double N)> Straight((double E, double N) from, (double E, double N) dir, double length)
    {
        var pts = new List<(double E, double N)>();
        for (double s = 0; s < length - 1e-6; s += RoadStep) pts.Add((from.E + dir.E * s, from.N + dir.N * s));
        pts.Add((from.E + dir.E * length, from.N + dir.N * length));
        return pts;
    }

    /// <summary>
    /// How far a street can run from a point along a direction, up to <paramref name="max"/>, in steps
    /// of <see cref="RoadStep"/>: it stops before a lake, before a channel's bank (plus
    /// <paramref name="channelMargin"/>), where the ground is 8 m off the start's, or steeper than
    /// 2.2 m in 9 m.
    /// </summary>
    private static double Reach((double E, double N) from, (double E, double N) dir, double max, double h0,
        double channelMargin)
    {
        var hist = new List<double> { h0 };
        double good = 0;
        for (double s = RoadStep; s <= max + 1e-6; s += RoadStep)
        {
            double e = from.E + dir.E * s, n = from.N + dir.N * s;
            if (Relief.Instance.LakeWeight(e, n, out _) > 0.1) break;
            var ch = ChannelAt(e, n);
            if (ch.Dr < ch.Bank + channelMargin) break;
            double h = Height(null, e, n);
            if (Math.Abs(h - h0) > 8) break;
            if (hist.Count >= 3 && Math.Abs(h - hist[^3]) > 2.2) break;
            hist.Add(h);
            good = s;
        }
        return good;
    }

    /// <summary>
    /// The cross road, bridge and bank street of a town, or null where the slot cannot host them
    /// (no river within 300 m downhill, a river too wide, no steady ground either side of it).
    /// </summary>
    private static Town? PlanTown(VillageSlot slot, Line line)
    {
        var rng = new Random(unchecked(slot.Id * 104729 + 15485863));
        var p = DrawnPoint(line, slot.S);
        var pa = DrawnPoint(line, slot.S - 1.5);
        var pb = DrawnPoint(line, slot.S + 1.5);
        double tl = Math.Max(1e-9, Math.Sqrt(Sq(pb.E - pa.E) + Sq(pb.N - pa.N)));
        var along = ((pb.E - pa.E) / tl, (pb.N - pa.N) / tl);
        // away from the river, as the side streets go
        var up = (E: -along.Item2 * line.Side, N: along.Item1 * line.Side);
        var down = (E: -up.E, N: -up.N);
        double h0 = Height(null, p.E, p.N);

        // downhill, the first channel and the way across it
        double tIn = -1, tOut = -1;
        bool wet = false;
        for (double t = 12; t <= 300; t += 2)
        {
            double e = p.E + down.E * t, n = p.N + down.N * t;
            if (Relief.Instance.LakeWeight(e, n, out _) > 0.1) return null;
            var ch = ChannelAt(e, n);
            bool inside = ch.Dr < ch.Bank + BridgeMargin;
            // a bridge over water: a dry channel (a headwater) is no reason for one
            if (inside && ch.Wet && ch.Dr < ch.Half) wet = true;
            if (tIn < 0) { if (inside) tIn = t; }
            else if (!inside) { tOut = t; break; }
        }
        if (tIn < 0 || tOut < 0 || !wet) return null;
        double start = tIn - 2, end = tOut, span = end - start;
        if (span < 8 || span > 90 || start < 30) return null;

        var bs = (E: p.E + down.E * start, N: p.N + down.N * start);
        var be = (E: p.E + down.E * end, N: p.N + down.N * end);
        double hs = Height(null, bs.E, bs.N), he = Height(null, be.E, be.N);
        if (Math.Abs(hs - he) > 6 || Math.Abs(hs - h0) > 8) return null;
        if (Reach(p, down, start, h0, 4) < start - RoadStep) return null;
        if (Reach(be, down, FarArmM, he, 4) < FarArmM - RoadStep) return null;

        // the street along the other bank, across the far arm's end
        var q = (E: be.E + down.E * FarArmM, N: be.N + down.N * FarArmM);
        double hq = Height(null, q.E, q.N);
        double bankMax = 110 + rng.NextDouble() * 70;
        double lenA = Reach(q, along, bankMax, hq, 14), lenB = Reach(q, (-along.Item1, -along.Item2), bankMax, hq, 14);
        if (lenA < 20) lenA = 0;
        if (lenB < 20) lenB = 0;
        if (lenA + lenB < 60) return null;

        // uphill, a few hundred metres, while the ground stays steady
        double upLen = Reach(p, up, 200 + rng.NextDouble() * 160, h0, 8);
        if (upLen < 110) return null;

        var bridge = new Street(Straight(bs, down, span), RoadClass.Road, RoadFlags.Bridge);
        var bank = new List<(double E, double N)>();
        if (lenB > 0)
        {
            var origin = (E: q.E - along.Item1 * lenB, N: q.N - along.Item2 * lenB);
            bank.AddRange(Straight(origin, along, lenB));
            bank.RemoveAt(bank.Count - 1);
        }
        bank.AddRange(Straight(q, along, lenA));
        return new Town(
            new Street(Straight(p, up, upLen), RoadClass.Road),
            new Street(Straight(p, down, start), RoadClass.Road, 0, bridge, true),
            bridge,
            new Street(Straight(be, down, FarArmM), RoadClass.Road, 0, bridge, false),
            new Street(bank, RoadClass.Minor),
            lenB);
    }

    /// <summary>Whether two footprints, each grown by a margin, overlap (separating axes).</summary>
    private static bool Overlap(in Footprint a, in Footprint b, double margin)
    {
        double de = b.E - a.E, dn = b.N - a.N;
        for (int k = 0; k < 4; k++)
        {
            var f = k < 2 ? a : b;
            double ax = k % 2 == 0 ? f.UE : -f.UN, an = k % 2 == 0 ? f.UN : f.UE;
            double Radius(in Footprint r) => Math.Abs(r.UE * ax + r.UN * an) * r.HalfLength
                + Math.Abs(-r.UN * ax + r.UE * an) * r.HalfWidth;
            if (Math.Abs(de * ax + dn * an) >= Radius(a) + Radius(b) + margin) return false;
        }
        return true;
    }

    private static double DistanceTo(List<(double E, double N)> pts, double e, double n)
    {
        double best = double.MaxValue;
        for (int i = 0; i + 1 < pts.Count; i++)
            best = Math.Min(best, SegmentDistance(e, n, pts[i].E, pts[i].N, pts[i + 1].E, pts[i + 1].N).D);
        return best;
    }

    /// <summary>
    /// Adds a town's streets and the houses along them: the cross road up and down, and the street
    /// along the other bank. Each house is kept clear of every street (carriageway, sidewalk and
    /// bike path), of the valley road and of the buildings already planned.
    /// </summary>
    private static void AddTown(VillageSlot slot, Town town, List<Street> streets, List<Plan> plans)
    {
        streets.Add(town.Up);
        streets.Add(town.Down);
        streets.Add(town.Bridge);
        streets.Add(town.Far);
        streets.Add(town.Bank);
        var rng = new Random(unchecked(slot.Id * 7919 + 1299709));

        bool Accept(Plan plan)
        {
            var f = plan.Rect;
            var probes = new List<(double E, double N)>(9) { (f.E, f.N) };
            foreach (double ka in new[] { -1.0, 0, 1 })
                foreach (double kb in new[] { -1.0, 0, 1 })
                    if (ka != 0 || kb != 0)
                    {
                        double pa = ka * f.HalfLength, pb = kb * f.HalfWidth;
                        probes.Add((f.E + f.UE * pa - f.UN * pb, f.N + f.UN * pa + f.UE * pb));
                    }
            foreach (var (e, n) in probes)
            {
                foreach (var st in streets)
                    if ((st.Flags & RoadFlags.Bridge) == 0
                        && DistanceTo(st.Points, e, n) < (st.Class == RoadClass.Road ? 10 : 8)) return false;
                if (NearestLine(e, n) is (var d, { } cls) && d < RoadFormat.DefaultWidth(cls) / 2 + 6.5) return false;
            }
            foreach (var other in plans)
                if (Overlap(f, other.Rect, 2)) return false;
            return true;
        }

        void Row(List<(double E, double N)> pts, double from, double to, double facade, Func<double, double> blocks)
        {
            (double E, double N) Dir(int i, out (double E, double N) a)
            {
                a = pts[i];
                double dl = Math.Sqrt(Sq(pts[i + 1].E - a.E) + Sq(pts[i + 1].N - a.N));
                return ((pts[i + 1].E - a.E) / dl, (pts[i + 1].N - a.N) / dl);
            }

            for (int side = -1; side <= 1; side += 2)
                for (double s = from; s < to;)
                {
                    double width = 9 + rng.NextDouble() * 4, depth = 10 + rng.NextDouble() * 3;
                    int i = Math.Min((int)(s / RoadStep), pts.Count - 2);
                    var dir = Dir(i, out _);
                    var left = (E: -dir.N, N: dir.E);
                    var plan = House(rng, (0, 0), dir, width, depth, blocks(s));
                    var f = plan.Rect;
                    double Ext((double E, double N) axis) =>
                        Math.Abs(f.UE * axis.E + f.UN * axis.N) * f.HalfLength
                        + Math.Abs(-f.UN * axis.E + f.UE * axis.N) * f.HalfWidth;
                    double alongExt = Ext(dir), sideExt = Ext(left);
                    double sc = s + alongExt;
                    if (sc + alongExt > to) break;

                    int j = Math.Min((int)(sc / RoadStep), pts.Count - 2);
                    dir = Dir(j, out var a);
                    left = (-dir.N, dir.E);
                    double front = facade + rng.NextDouble() * 2;
                    double off = sc - j * RoadStep;
                    var c = (E: a.E + dir.E * off + left.E * side * (front + sideExt),
                        N: a.N + dir.N * off + left.N * side * (front + sideExt));
                    plan = plan with { Rect = f with { E = c.E, N = c.N } };
                    bool house = Accept(plan);
                    if (house) plans.Add(plan);
                    s = sc + alongExt + 2 + rng.NextDouble() * 6;

                    // a garage beside some of the houses, as the side streets have
                    double gs = sc + alongExt + 2 + GarageHalfWidth;
                    if (house && plan.Kind == BuildingKind.House && gs + GarageHalfWidth < to
                        && Noise.Hash01((int)Math.Floor(c.E), (int)Math.Floor(c.N), 211) < 0.5)
                    {
                        var garage = Garage(pts, gs, side, front + GarageHalfDepth);
                        if (Accept(garage)) plans.Add(garage);
                        s = gs + GarageHalfWidth + 2 + rng.NextDouble() * 4;
                    }
                }
        }

        double Blocks(double s) => s < 120 ? 0.35 : 0.1;
        // clear of the main street's frontage and of the crossroads' corners
        Row(town.Up.Points, 28, (town.Up.Points.Count - 1) * RoadStep - 6, 10, Blocks);
        Row(town.Down.Points, 28, (town.Down.Points.Count - 1) * RoadStep - 24, 10, s => 0.2);
        // the other bank's street, either side of the far arm's mouth
        double mouth = town.BankMouth;
        double bankEnd = (town.Bank.Points.Count - 1) * RoadStep;
        if (mouth > 30) Row(town.Bank.Points, 6, mouth - 16, 8, s => 0.1);
        if (bankEnd - mouth > 30) Row(town.Bank.Points, mouth + 16, bankEnd - 6, 8, s => 0.1);
    }
}
