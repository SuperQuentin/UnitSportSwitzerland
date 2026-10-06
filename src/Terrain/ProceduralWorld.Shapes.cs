using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Shaped buildings in the generated world (#598): blocks whose outline is not their rectangle, so
/// the interiors that follow a building's real shape (#577) are met in every generated village and
/// not only in <c>--flatcheck</c>. Right-angled ones (an L, a U, a T, a ring round a courtyard) and
/// ones with walls at other angles (a trapezoid, a block with its corners cut, a bar bent in the
/// middle, a skewed block). A few per village, behind the main street's row, each from its own
/// seed: no other building moves, and they come last in a tile's list (<see cref="PlansNear"/>), so
/// no other building's index in its tile changes either.
/// </summary>
public sealed partial class ProceduralWorld
{
    /// <summary>
    /// A building's outline in its own frame (a along its footprint's u, b to the left of it,
    /// metres from its centre): the outer ring counter-clockwise, a courtyard's ring clockwise, so
    /// the outside of every edge is on its right; and the flat roof as convex parts, each
    /// counter-clockwise, covering exactly what the rings enclose.
    /// </summary>
    private sealed record Shape(string Name, (double A, double B)[][] Rings, (double A, double B)[][] Parts);

    /// <summary>The shapes, in the order villages take them round (<see cref="AddShaped"/>).</summary>
    public static readonly string[] ShapeNames =
        ["L", "U", "T", "courtyard", "trapezoid", "chamfered", "bent", "skewed"];

    private const int ShapedPerVillage = 2;

    /// <summary>
    /// The shaped buildings planned round a point (for checks and probes): their centre in LV95,
    /// the name of their shape, their kind, and their outline's rings in LV95 (the outer one
    /// counter-clockwise, a courtyard's clockwise). Planned, not built: one on ground its solid
    /// turns down (too steep at its corners once blended, say) has no building.
    /// </summary>
    public IEnumerable<(double E, double N, string Shape, BuildingKind Kind, (double E, double N)[][] Rings)> ShapedNear(
        double e, double n, double radius)
    {
        foreach (var v in VillagesNear(e - radius, n - radius, e + radius, n + radius))
            foreach (var p in v.Buildings)
                if (p.Outline is { } s && Math.Abs(p.Rect.E - e) <= radius && Math.Abs(p.Rect.N - n) <= radius)
                    yield return (p.Rect.E, p.Rect.N, s.Name, p.Kind, Rings(p));
    }

    /// <summary>
    /// One of the shapes, sized by the roll, centred on its bounding box: the shape and its half
    /// extents along a and b.
    /// </summary>
    private static (Shape Shape, double HalfA, double HalfB) MakeShape(int which, Random rng)
    {
        double R(double lo, double hi) => lo + (hi - lo) * rng.NextDouble();
        (double, double)[] Rect(double a0, double b0, double a1, double b1) => [(a0, b0), (a1, b0), (a1, b1), (a0, b1)];
        double d = R(11, 13);   // a wing's depth: a stairwell and a flat either side of its corridor
        string name = ShapeNames[which];
        (double A, double B)[][] rings, parts;
        switch (name)
        {
            case "L":
            {
                double w = R(14, 17), h = R(12, 14);
                rings = [[(-w, -h), (w, -h), (w, h), (w - d, h), (w - d, -h + d), (-w, -h + d)]];
                parts = [Rect(-w, -h, w, -h + d), Rect(w - d, -h + d, w, h)];
                break;
            }
            case "U":
            {
                double w = R(17, 19), h = R(13, 15);
                rings = [[(-w, -h), (w, -h), (w, h), (w - d, h), (w - d, -h + d), (-w + d, -h + d), (-w + d, h), (-w, h)]];
                parts = [Rect(-w, -h, w, -h + d), Rect(-w, -h + d, -w + d, h), Rect(w - d, -h + d, w, h)];
                break;
            }
            case "T":
            {
                double w = R(15, 17), h = R(12, 14), s = d / 2;
                rings = [[(-s, -h), (s, -h), (s, h - d), (w, h - d), (w, h), (-w, h), (-w, h - d), (-s, h - d)]];
                parts = [Rect(-w, h - d, w, h), Rect(-s, -h, s, h - d)];
                break;
            }
            case "courtyard":
            {
                double w = R(18, 21), h = R(18, 21);
                d = R(11, 12);
                rings =
                [
                    [(-w, -h), (w, -h), (w, h), (-w, h)],
                    // the courtyard, clockwise: its walls face into it
                    [(-w + d, -h + d), (-w + d, h - d), (w - d, h - d), (w - d, -h + d)],
                ];
                parts = [Rect(-w, -h, w, -h + d), Rect(-w, h - d, w, h), Rect(-w, -h + d, -w + d, h - d), Rect(w - d, -h + d, w, h - d)];
                break;
            }
            case "trapezoid":
            {
                // its back narrower than its front by a few metres each side
                double w = R(14, 17), h = R(7, 9), s = R(3, 6);
                rings = [[(-w, -h), (w, -h), (w - s, h), (-w + s, h)]];
                parts = rings;
                break;
            }
            case "chamfered":
            {
                double w = R(12, 16), h = R(8, 10), c = R(3, 4.5);
                rings = [[(-w + c, -h), (w - c, -h), (w, -h + c), (w, h - c), (w - c, h), (-w + c, h), (-w, h - c), (-w, -h + c)]];
                parts = rings;
                break;
            }
            case "bent":
            {
                // two arms rising from a mitre in the middle, like a wide V
                double len = R(14, 18), half = d / 2, al = R(12, 20) * Math.PI / 180;
                double ca = Math.Cos(al), sa = Math.Sin(al);
                (double, double) Arm(double side, double along, double off) =>
                    // side +1 the right arm, -1 the left; off + toward the arm's lower edge
                    (side * ca * along + side * sa * off, sa * along - ca * off);
                var mBot = (0.0, -half / ca);
                var mTop = (0.0, half / ca);
                var rBot = Arm(1, len, half); var rTop = Arm(1, len, -half);
                var lBot = Arm(-1, len, half); var lTop = Arm(-1, len, -half);
                rings = [[mBot, rBot, rTop, mTop, lTop, lBot]];
                parts = [[mBot, rBot, rTop, mTop], [mBot, mTop, lTop, lBot]];
                break;
            }
            default: // skewed
            {
                double w = R(14, 17), h = R(7, 8), k = R(4, 7);
                rings = [[(-w, -h), (w - k, -h), (w, h), (-w + k, h)]];
                parts = rings;
                break;
            }
        }
        // centred on its bounding box, which is the footprint every other test reads
        var all = rings[0];
        double a0 = all.Min(p => p.A), a1 = all.Max(p => p.A), b0 = all.Min(p => p.B), b1 = all.Max(p => p.B);
        double ca0 = (a0 + a1) / 2, cb0 = (b0 + b1) / 2;
        (double A, double B)[][] Shift((double A, double B)[][] rs) =>
            rs.Select(r => r.Select(p => (p.A - ca0, p.B - cb0)).ToArray()).ToArray();
        return (new Shape(name, Shift(rings), Shift(parts)), (a1 - a0) / 2, (b1 - b0) / 2);
    }

    /// <summary>
    /// A village's shaped buildings: <see cref="ShapedPerVillage"/> of them, the shapes taken round
    /// by the village's id so neighbouring villages show different ones, each set back behind the
    /// main street's row where it is clear of every street, the valley road, the river, a lake and
    /// the buildings already planned, on ground level enough for it.
    /// </summary>
    private static void AddShaped(VillageSlot slot, Line line, List<Street> streets, List<Plan> plans)
    {
        var rng = new Random(unchecked(slot.Id * 7919 + 15485863));
        for (int j = 0; j < ShapedPerVillage; j++)
        {
            int which = (int)((uint)(slot.Id * ShapedPerVillage + j) % (uint)ShapeNames.Length);
            var (shape, ha, hb) = MakeShape(which, rng);
            bool commercial = which >= 4 && rng.NextDouble() < 0.3;
            int floors = 3 + rng.Next(3);
            var year = (ushort)(1960 + rng.Next(60));
            bool turned = rng.NextDouble() < 0.5;   // its opening toward the street or away from it

            // the places to try: along the main street out from the middle, either side, two depths
            var tries = new List<(double Along, int Side, double Back)>();
            for (int k = 0; k < 9; k++)
            {
                double along = (k % 2 == 0 ? 1 : -1) * ((k + 1) / 2) * 35 + (rng.NextDouble() - 0.5) * 10;
                if (Math.Abs(along) > slot.HalfLength * 0.85) continue;
                foreach (int side in rng.NextDouble() < 0.5 ? new[] { 1, -1 } : new[] { -1, 1 })
                    foreach (double back in new[] { 30.0, 44.0 })
                        tries.Add((along, side, back));
            }
            foreach (var (along, side, back) in tries)
            {
                var (p, t, nrm) = RoadFrame(line, slot.S + along);
                // b is to the left of u, so its front (b < 0) faces the road when its left is
                // away from it (the normal is the line's side's); turned, its back does
                var u = (side * line.Side > 0) == turned ? (E: -t.E, N: -t.N) : t;
                double setback = back + hb;
                var c = (E: p.E + nrm.E * side * setback, N: p.N + nrm.N * side * setback);
                var plan = new Plan(new Footprint(c.E, c.N, u.E, u.N, ha, hb),
                    commercial ? BuildingKind.Commercial : BuildingKind.Apartment,
                    floors * 3.0, 0, (byte)floors, year, Outline: shape);
                if (!Clear(plan, streets, plans)) continue;
                plans.Add(plan);
                break;
            }
        }
    }

    /// <summary>
    /// Whether a shaped building's outline, walked every few metres, keeps clear of the streets,
    /// the valley road and railway, the river and lakes, stands on ground within 4 m from corner to
    /// corner (its solid allows 4.5 once blended), and overlaps no planned building.
    /// </summary>
    private static bool Clear(Plan plan, List<Street> streets, List<Plan> plans)
    {
        var f = plan.Rect;
        foreach (var other in plans)
            if (Overlap(f, other.Rect, 4)) return false;
        double low = double.MaxValue, high = double.MinValue;
        foreach (var (e, n) in OutlineProbes(plan, 4))
        {
            foreach (var st in streets)
                if ((st.Flags & RoadFlags.Bridge) == 0
                    && DistanceTo(st.Points, e, n) < (st.Class == RoadClass.Road ? 10 : 8)) return false;
            if (NearestLine(e, n) is (var d, { } cls) && d < RoadFormat.DefaultWidth(cls) / 2 + 6.5) return false;
            var ch = ChannelAt(e, n);
            if (ch.Dr < ch.Bank + 8) return false;
            if (Relief.Instance.LakeWeight(e, n, out _) > 0.02) return false;
            double h = Height(null, e, n);
            low = Math.Min(low, h);
            high = Math.Max(high, h);
        }
        return high - low <= 4.0;
    }

    /// <summary>Points round every ring of a plan's outline (or its rectangle), at most <paramref name="step"/> apart, and its centre.</summary>
    private static IEnumerable<(double E, double N)> OutlineProbes(Plan plan, double step)
    {
        yield return (plan.Rect.E, plan.Rect.N);
        foreach (var ring in Rings(plan))
            for (int i = 0; i < ring.Length; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Length];
                double len = Math.Sqrt(Sq(b.E - a.E) + Sq(b.N - a.N));
                int k = Math.Max(1, (int)Math.Ceiling(len / step));
                for (int s = 0; s < k; s++)
                    yield return (a.E + (b.E - a.E) * s / k, a.N + (b.N - a.N) * s / k);
            }
    }

    /// <summary>A plan's outline rings in LV95: its shape's, or its rectangle counter-clockwise.</summary>
    private static (double E, double N)[][] Rings(Plan plan)
    {
        var f = plan.Rect;
        (double E, double N) At(double a, double b) => (f.E + f.UE * a - f.UN * b, f.N + f.UN * a + f.UE * b);
        if (plan.Outline is { } s)
            return s.Rings.Select(r => r.Select(p => At(p.A, p.B)).ToArray()).ToArray();
        return [[At(-f.HalfLength, -f.HalfWidth), At(f.HalfLength, -f.HalfWidth), At(f.HalfLength, f.HalfWidth), At(-f.HalfLength, f.HalfWidth)]];
    }

    /// <summary>A plan's flat roof parts in LV95 (its shape's), or null for its rectangle.</summary>
    private static (double E, double N)[][]? RoofParts(Plan plan)
    {
        if (plan.Outline is not { } s) return null;
        var f = plan.Rect;
        return s.Parts.Select(r => r.Select(p => (f.E + f.UE * p.A - f.UN * p.B, f.N + f.UN * p.A + f.UE * p.B)).ToArray()).ToArray();
    }
}
