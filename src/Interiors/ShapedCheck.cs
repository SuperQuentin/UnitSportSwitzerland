using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--shapedcheck</c> (#598): the generated world's shaped buildings (an L, a U, a T, a courtyard
/// ring, a trapezoid, cut corners, a bent bar, a skewed block) end to end, without a world: the
/// generator plans the villages round the default spawn, builds the tiles they stand in, and each
/// shaped building goes through <see cref="BuildingFootprint.ComputeDoors"/> and
/// <see cref="InteriorGenerator"/>. Asks: is its roof its outline (the courtyard open), does every
/// door stand on its outline facing out, does its plan validate, and does a block of flats keep
/// every room inside the outline (#577) with most of it used? Every shape must be built at least
/// once. Writes each plan as an SVG to <c>test_output/shaped/</c>. Prints a RESULT line, exits 0 or 1.
/// </summary>
public static class ShapedCheck
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--shapedcheck") >= 0;

    /// <summary>How far round the default spawn villages are looked at, m.</summary>
    private const double Radius = 12000;
    /// <summary>At most this many of each shape are checked, nearest the spawn first.</summary>
    private const int PerShape = 3;

    public static int Run()
    {
        int failures = 0;
        void Expect(bool ok, string what)
        {
            if (!ok) failures++;
            GD.Print($"[shapedcheck] {(ok ? "ok  " : "FAIL")} {what}");
        }

        string dir = ProjectSettings.GlobalizePath("res://test_output/shaped");
        System.IO.Directory.CreateDirectory(dir);
        double e0 = SpawnPoint.DefaultLv95E, n0 = SpawnPoint.DefaultLv95N;
        var world = new ProceduralWorld(e0, n0);
        var planned = world.ShapedNear(e0, n0, Radius).ToList();
        GD.Print($"[shapedcheck] {planned.Count} shaped buildings planned within {Radius / 1000:F0} km: "
            + string.Join(", ", planned.GroupBy(p => p.Shape).Select(g => $"{g.Count()} {g.Key}")));

        var picks = planned.GroupBy(p => p.Shape)
            .SelectMany(g => g.OrderBy(p => Sq(p.E - e0) + Sq(p.N - n0)).Take(PerShape)).ToList();
        var tiles = new Dictionary<TileId, (BuildingTile? Tile, DoorSpot[] Doors)>();
        var built = new HashSet<string>();
        int dropped = 0;
        foreach (var p in picks)
        {
            var id = TileId.FromLv95(p.E, p.N);
            if (!tiles.TryGetValue(id, out var t))
            {
                var bt = world.BuildBuildings(id);
                t = (bt, bt == null ? [] : BuildingFootprint.ComputeDoors(bt, world.BuildRoads(id), null));
                tiles[id] = t;
            }
            string what = $"a {p.Shape} {p.Kind} at {p.E:F0},{p.N:F0}";
            // the planned outline in the tile's frame (X east, Z south)
            var rings = p.Rings.Select(r => r.Select(q => new Vector2((float)(q.E - id.MinE), (float)(id.MaxN - q.N))).ToArray()).ToArray();
            int index = t.Tile == null ? -1 : Find(t.Tile, rings[0]);
            if (index < 0)
            {
                // its solid turned it down (too steep once blended, on a road): not a failure by
                // itself, but every shape must stand somewhere
                dropped++;
                GD.Print($"[shapedcheck] {what}: not built");
                continue;
            }
            built.Add(p.Shape);
            var b = t.Tile!.Buildings[index];
            float outline = Area(rings);

            // the roof is the outline: a courtyard stays open
            float roof = RoofArea(b);
            Expect(Math.Abs(roof - outline) <= 0.01f * outline, $"{what}: roof {roof:F0} m² over an outline of {outline:F0} m², base at {b.MinY:F0} m");

            // nothing else built inside it: no house, garage or barn through its walls
            int through = 0;
            for (int o = 0; o < t.Tile.Buildings.Count; o++)
            {
                if (o == index) continue;
                var tr = t.Tile.Buildings[o].Triangles;
                for (int k = 0; k + 2 < tr.Length; k += 3)
                {
                    var q = new Vector2(tr[k], tr[k + 2]);
                    if (Inside(rings, q) && Edge(rings, q) > 0.5f) { through++; break; }
                }
            }
            Expect(through == 0, $"{what}: {through} other building(s) inside its outline");

            // every door on the outline, facing out of it
            var doors = t.Doors.Where(d => d.Index == index && d.Width > 0).ToList();
            Expect(doors.Count > 0, $"{what}: {doors.Count} door(s)");
            foreach (var d in doors)
            {
                var at = new Vector2(d.Position.X, d.Position.Z);
                var o = new Vector2(d.Outward.X, d.Outward.Z).Normalized();
                bool onWall = Edge(rings, at) < 0.6f, faces = !Inside(rings, at + o * 1.0f) && Inside(rings, at - o * 1.0f);
                Expect(onWall && faces, $"{what}: door {d.Slot} on its outline ({Edge(rings, at):F2} m off) facing out ({faces})");
            }

            // the plan
            InteriorGenerator.WingFailure = null;
            var l = InteriorGenerator.Generate(t.Tile, index, null, null);
            string? why = InteriorGenerator.WingFailure;
            if (l == null) { Expect(false, $"{what}: no plan"); continue; }
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"{p.Shape}_{p.E:F0}_{p.N:F0}.svg"), InteriorValidator.ToSvg(l));
            var problems = InteriorValidator.Validate(l);
            Expect(problems.Count == 0, $"{what}: the plan ({l.Type}) validates{(problems.Count > 0 ? " — " + string.Join("; ", problems.Take(4)) : "")}");

            // a block of flats follows the outline (#577): it may differ a little from it, never
            // from its shape, so the ground floor is measured on a 0.5 m grid: how much of it
            // stands outside the outline, how much of the outline it fills
            float yaw = l.Yaw;
            Vector2 World(float x, float z) => new(
                l.CenterX + Mathf.Cos(yaw) * x + Mathf.Sin(yaw) * z,
                l.CenterZ - Mathf.Sin(yaw) * x + Mathf.Cos(yaw) * z);
            float outside = 0, used = 0;
            foreach (var r in l.GroundFloor.Rooms)
                for (float x = r.X0 + 0.25f; x < r.X1; x += 0.5f)
                    for (float z = r.Z0 + 0.25f; z < r.Z1; z += 0.5f)
                    {
                        var c = World(x, z);
                        if (Inside(rings, c)) used += 0.25f;
                        else outside += 0.25f;
                    }
            bool flats = l.Type is BuildingType.Apartments or BuildingType.MixedUse;
            string inside = $"{what}: the ground floor fills {used:F0} of its {outline:F0} m² outline, {outside:F0} m² outside it";
            if (flats)
            {
                // a box its roof covers PlanOutline.Boxy of is planned as the box, so up to the
                // rest may stand outside (cut corners); an outline at other angles is planned as
                // the largest rectangles inside it, so a bent bar fills a little over half of it
                bool follows = outside <= (1 - PlanOutline.Boxy) * outline / PlanOutline.Boxy && used >= 0.55f * outline;
                Expect(follows, inside);
                if (!follows && BuildingFootprint.Compute(t.Tile, index, null, null) is { } fp)
                {
                    // what the outline was read as, to see why
                    var wings = PlanOutline.Wings(b, fp.Center, fp.AxisU, fp.Width, fp.Depth);
                    GD.Print($"[shapedcheck]      box {fp.Width:F1} x {fp.Depth:F1}, wings: "
                        + (wings == null ? "none (the box)" : string.Join(" ", wings.Select(w => $"[{w.X0:F1},{w.Z0:F1}..{w.X1:F1},{w.Z1:F1}]")))
                        + (why != null ? $"; planned as the box: {why}" : ""));
                }
            }
            else GD.Print($"[shapedcheck] {inside} (a {l.Type} plan: not checked)");
        }

        foreach (var name in ProceduralWorld.ShapeNames)
            Expect(built.Contains(name), $"a {name} building stands within {Radius / 1000:F0} km of the spawn");
        GD.Print($"[shapedcheck] {picks.Count - dropped} of {picks.Count} picked buildings built");
        GD.Print($"[shapedcheck] RESULT: {(failures == 0 ? "ok" : $"FAILED ({failures})")}");
        return failures == 0 ? 0 : 1;
    }

    private static double Sq(double v) => v * v;

    /// <summary>The building whose walls' extent is the outline's, or -1.</summary>
    private static int Find(BuildingTile tile, Vector2[] outer)
    {
        float x0 = outer.Min(p => p.X), x1 = outer.Max(p => p.X), z0 = outer.Min(p => p.Y), z1 = outer.Max(p => p.Y);
        for (int i = tile.Buildings.Count - 1; i >= 0; i--)
        {
            var t = tile.Buildings[i].Triangles;
            float bx0 = float.MaxValue, bx1 = float.MinValue, bz0 = float.MaxValue, bz1 = float.MinValue;
            for (int k = 0; k < t.Length; k += 3)
            {
                bx0 = Math.Min(bx0, t[k]); bx1 = Math.Max(bx1, t[k]);
                bz0 = Math.Min(bz0, t[k + 2]); bz1 = Math.Max(bz1, t[k + 2]);
            }
            if (Math.Abs(bx0 - x0) < 0.3f && Math.Abs(bx1 - x1) < 0.3f && Math.Abs(bz0 - z0) < 0.3f && Math.Abs(bz1 - z1) < 0.3f)
                return i;
        }
        return -1;
    }

    /// <summary>The area of the faces looking up, m² (a flat roof's, seen from above).</summary>
    private static float RoofArea(Building b)
    {
        float area = 0;
        var t = b.Triangles;
        for (int k = 0; k + 8 < t.Length; k += 9)
        {
            var a = new Vector3(t[k], t[k + 1], t[k + 2]);
            var n = (new Vector3(t[k + 3], t[k + 4], t[k + 5]) - a).Cross(new Vector3(t[k + 6], t[k + 7], t[k + 8]) - a);
            if (Mathf.Abs(n.Y) > 0.9f * n.Length()) area += Mathf.Abs(n.Y) / 2;
        }
        return area;
    }

    /// <summary>The outline's area: the outer ring less a courtyard.</summary>
    private static float Area(Vector2[][] rings)
    {
        float area = 0;
        foreach (var r in rings)
        {
            float s = 0;
            for (int i = 0; i < r.Length; i++) s += r[i].X * r[(i + 1) % r.Length].Y - r[(i + 1) % r.Length].X * r[i].Y;
            area += Mathf.Abs(s) / 2 * (r == rings[0] ? 1 : -1);
        }
        return area;
    }

    /// <summary>Whether a point is inside the outline (even-odd over every ring: a courtyard is out).</summary>
    private static bool Inside(Vector2[][] rings, Vector2 p)
    {
        bool inside = false;
        foreach (var r in rings)
            for (int i = 0, j = r.Length - 1; i < r.Length; j = i++)
                if ((r[i].Y > p.Y) != (r[j].Y > p.Y)
                    && p.X < (r[j].X - r[i].X) * (p.Y - r[i].Y) / (r[j].Y - r[i].Y) + r[i].X)
                    inside = !inside;
        return inside;
    }

    /// <summary>The distance from a point to the nearest wall of the outline, m.</summary>
    private static float Edge(Vector2[][] rings, Vector2 p)
    {
        float best = float.MaxValue;
        foreach (var r in rings)
            for (int i = 0; i < r.Length; i++)
            {
                Vector2 a = r[i], b = r[(i + 1) % r.Length], ab = b - a;
                float t = Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0, 1);
                best = Math.Min(best, p.DistanceTo(a + ab * t));
            }
        return best;
    }
}
