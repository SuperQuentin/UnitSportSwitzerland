using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Birds;

/// <summary>Where on a building a town bird sits.</summary>
public enum PerchKind : byte
{
    /// <summary>The top of a roof: a ridge, or the rim of a flat roof.</summary>
    Ridge,
    /// <summary>The low edge of a sloping roof, the gutter.</summary>
    Eave,
    /// <summary>A window ledge or facade edge, one per storey.</summary>
    Ledge,
    /// <summary>The street or square in front of a facade (the bird stands on the ground there).</summary>
    Street,
}

public readonly record struct Perch(Vector3 At, float Yaw, PerchKind Kind);

/// <summary>
/// The perches of one tile's buildings (<c>.bldg</c>, #143), worked out once from the roof and wall
/// triangles: ridges and flat-roof rims (the top edges of roof faces), eaves (the low horizontal edges
/// of sloping roof faces), window ledges (each 3 m storey of a wall, 0.18 m out) and street spots
/// (3–5 m out from the foot of a wall, outside every building). Bucketed in <see cref="Cell"/> m cells
/// like the tree tops. <see cref="Density"/> counts the real buildings (no barns, sheds or garages)
/// per <see cref="DensityCell"/> m cell, for <see cref="BirdLife"/>'s town test: the cover raster
/// alone reads a village as farmland.
/// </summary>
public sealed class TownPerches
{
    public const float Cell = 16f, DensityCell = 64f;
    private const float Storey = 3f, RoofNormalY = 0.45f;

    public readonly Dictionary<(int, int), List<Perch>> Cells = new();
    public readonly Dictionary<(int, int), int> Density = new();

    private readonly List<Rect2> _boxes = new();
    private readonly Dictionary<(int, int), List<int>> _boxGrid = new();

    /// <summary>Inside some building's flat box (its footprint's bounding box).</summary>
    public bool Inside(Vector3 p)
    {
        if (!_boxGrid.TryGetValue(CellOf(p, DensityCell), out var list)) return false;
        var flat = new Vector2(p.X, p.Z);
        foreach (int i in list) if (_boxes[i].HasPoint(flat)) return true;
        return false;
    }

    /// <summary>A straight walk from <paramref name="a"/> to <paramref name="b"/> crosses a building (sampled every metre).</summary>
    public bool Blocked(Vector3 a, Vector3 b)
    {
        int n = (int)a.DistanceTo(b);
        for (int i = 1; i < n; i++)
            if (Inside(a.Lerp(b, (float)i / n))) return true;
        return false;
    }

    public static (int, int) CellOf(Vector3 p, float size) => ((int)Mathf.Floor(p.X / size), (int)Mathf.Floor(p.Z / size));

    /// <summary>Worker-thread safe: plain maths over the tile's triangles.</summary>
    public static TownPerches Build(BuildingTile tile, WorldOrigin origin)
    {
        var town = new TownPerches();
        var id = tile.Id;
        Vector3 W(float x, float y, float z) => origin.ToWorld(id.MinE + x, id.MaxN - z, y);

        // every building's flat box, so a street spot is never inside a house (kept: pedestrians, #217)
        var boxes = town._boxes;
        var boxGrid = town._boxGrid;
        foreach (var b in tile.Buildings)
        {
            var box = FlatBox(b, W);
            for (int x = (int)Mathf.Floor(box.Position.X / DensityCell); x <= (int)Mathf.Floor(box.End.X / DensityCell); x++)
                for (int z = (int)Mathf.Floor(box.Position.Y / DensityCell); z <= (int)Mathf.Floor(box.End.Y / DensityCell); z++)
                {
                    if (!boxGrid.TryGetValue((x, z), out var list)) boxGrid[(x, z)] = list = new List<int>();
                    list.Add(boxes.Count);
                }
            boxes.Add(box);
        }
        bool Inside(Vector3 p) => town.Inside(p);

        var seen = new HashSet<(int, int, int)>();
        var ridge = new List<Perch>(); var eave = new List<Perch>(); var ledge = new List<Perch>(); var street = new List<Perch>();
        for (int bi = 0; bi < tile.Buildings.Count; bi++)
        {
            var b = tile.Buildings[bi];
            if (b.MaxY - b.MinY < 2.5f) continue;
            if (b.Kind is not (BuildingKind.Agricultural or BuildingKind.Annex or BuildingKind.Garage or BuildingKind.UnderConstruction))
            {
                var middle = boxes[bi].GetCenter();
                var key = CellOf(new Vector3(middle.X, 0, middle.Y), DensityCell);
                town.Density[key] = town.Density.GetValueOrDefault(key) + 1;
            }
            var t = b.Triangles;
            var centre = boxes[bi].GetCenter();
            seen.Clear(); ridge.Clear(); eave.Clear(); ledge.Clear(); street.Clear();
            for (int i = 0; i + 8 < t.Length; i += 9)
            {
                var a = W(t[i], t[i + 1], t[i + 2]);
                var bb = W(t[i + 3], t[i + 4], t[i + 5]);
                var cc = W(t[i + 6], t[i + 7], t[i + 8]);
                var n = (bb - a).Cross(cc - a);
                if (n.LengthSquared() < 1e-6f) continue;
                n = n.Normalized();
                var mid = (a + bb + cc) / 3f;
                // the face's outward side: away from the middle of the building
                var outward = new Vector2(mid.X - centre.X, mid.Z - centre.Y);
                if (Mathf.Abs(n.Y) > RoofNormalY)
                {
                    if (mid.Y < b.MinY + 2f) continue;
                    float lo = Mathf.Min(a.Y, Mathf.Min(bb.Y, cc.Y));
                    Edge(a, bb); Edge(bb, cc); Edge(cc, a);

                    void Edge(Vector3 p, Vector3 q)
                    {
                        if (Mathf.Abs(p.Y - q.Y) > 0.3f || p.DistanceTo(q) < 0.8f) return;
                        bool top = p.Y > b.MaxY - 0.4f;
                        bool low = !top && Mathf.Abs(p.Y - lo) < 0.3f && lo < b.MaxY - 1f;
                        if (!top && !low) return;
                        var along = (q - p) with { Y = 0 };
                        float yaw = Mathf.Atan2(along.Z, -along.X);   // across the edge
                        if (low)
                        {
                            // an eave bird looks out over the street
                            var o = (p + q) * 0.5f - mid;
                            yaw = Mathf.Atan2(o.X, o.Z);
                        }
                        int steps = Math.Max(1, (int)(p.DistanceTo(q) / 2.5f));
                        for (int s = 0; s < steps; s++)
                        {
                            var at = p.Lerp(q, (s + 0.5f) / steps);
                            if (seen.Add(((int)Mathf.Round(at.X * 2), (int)Mathf.Round(at.Y * 2), (int)Mathf.Round(at.Z * 2))))
                                (top ? ridge : eave).Add(new Perch(at, yaw, top ? PerchKind.Ridge : PerchKind.Eave));
                        }
                    }
                }
                else if (Mathf.Abs(n.Y) < 0.2f)
                {
                    var flat = new Vector2(n.X, n.Z).Normalized();
                    if (flat.Dot(outward) < 0) flat = -flat;
                    float yaw = Mathf.Atan2(flat.X, flat.Y);
                    var out3 = new Vector3(flat.X, 0, flat.Y);
                    // a ledge per storey above the ground floor, where this face spans it
                    for (float y = b.MinY + Storey + 0.9f; y < b.MaxY - 0.5f; y += Storey)
                        if (Across(a, bb, cc, y) is { } span && span.Length > 1.2f)
                            ledge.Add(new Perch(span.Mid + out3 * 0.18f, yaw, PerchKind.Ledge));
                    if (Across(a, bb, cc, b.MinY + 0.3f) is { } foot && foot.Length > 2f)
                    {
                        var at = foot.Mid + out3 * (3f + 2f * Frac(foot.Mid.X + foot.Mid.Z));
                        street.Add(new Perch(at with { Y = b.MinY }, yaw, PerchKind.Street));
                    }
                }
            }
            // a big block has hundreds of faces: a few of each kind is plenty
            street.RemoveAll(p => Inside(p.At));
            Keep(town, ridge, 10); Keep(town, eave, 10); Keep(town, ledge, 8); Keep(town, street, 6);
        }
        return town;
    }

    private static void Keep(TownPerches town, List<Perch> list, int most)
    {
        int stride = Math.Max(1, (list.Count + most - 1) / most);
        for (int i = 0; i < list.Count; i += stride)
        {
            var key = CellOf(list[i].At, Cell);
            if (!town.Cells.TryGetValue(key, out var cell)) town.Cells[key] = cell = new List<Perch>();
            cell.Add(list[i]);
        }
    }

    private static Rect2 FlatBox(Building b, Func<float, float, float, Vector3> w)
    {
        var t = b.Triangles;
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            var p = w(t[i], t[i + 1], t[i + 2]);
            minX = Mathf.Min(minX, p.X); maxX = Mathf.Max(maxX, p.X);
            minZ = Mathf.Min(minZ, p.Z); maxZ = Mathf.Max(maxZ, p.Z);
        }
        return t.Length < 3 ? default : new Rect2(minX, minZ, maxX - minX, maxZ - minZ);
    }

    private readonly record struct Span(Vector3 Mid, float Length);

    /// <summary>Where a wall triangle crosses the height <paramref name="y"/>: the middle and length of that cut.</summary>
    private static Span? Across(Vector3 a, Vector3 b, Vector3 c, float y)
    {
        Vector3 h0 = default, h1 = default;
        int n = 0;
        Cut(a, b, y, ref n, ref h0, ref h1); Cut(b, c, y, ref n, ref h0, ref h1); Cut(c, a, y, ref n, ref h0, ref h1);
        return n < 2 ? null : new Span((h0 + h1) * 0.5f, h0.DistanceTo(h1));
    }

    private static void Cut(Vector3 p, Vector3 q, float y, ref int n, ref Vector3 h0, ref Vector3 h1)
    {
        if (n >= 2 || (p.Y - y) * (q.Y - y) > 0 || Mathf.IsEqualApprox(p.Y, q.Y)) return;
        var h = p.Lerp(q, (y - p.Y) / (q.Y - p.Y));
        if (n++ == 0) h0 = h; else h1 = h;
    }

    private static float Frac(float x) => x - Mathf.Floor(x);

    /// <summary>Real buildings in the 3×3 density cells around a point (a 192 m square).</summary>
    public int BuildingsAround(Vector3 p)
    {
        var (cx, cz) = CellOf(p, DensityCell);
        int sum = 0;
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                sum += Density.GetValueOrDefault((cx + dx, cz + dz));
        return sum;
    }
}
