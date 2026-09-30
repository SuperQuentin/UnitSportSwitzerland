using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>A building, named the way every peer can agree on: its tile and its index in that tile's <c>.bldg</c>.</summary>
public readonly record struct BuildingKey(int TileE, int TileN, int Index)
{
    public TileId Tile => new(TileE, TileN);
    public override string ToString() => $"{TileE}_{TileN}_{Index}";

    public static bool TryParse(string s, out BuildingKey key)
    {
        key = default;
        var p = s.Split('_');
        if (p.Length != 3 || !int.TryParse(p[0], out int e) || !int.TryParse(p[1], out int n)
            || !int.TryParse(p[2], out int i)) return false;
        key = new BuildingKey(e, n, i);
        return true;
    }
}

/// <summary>
/// Where a building's front door is, in tile-local metres (X east, Y altitude, Z south — the
/// frame the building triangles and the chunk node share). <see cref="Outward"/> is horizontal.
/// </summary>
public readonly record struct DoorSpot(int Index, Vector3 Position, Vector3 Outward, float Width, float Height)
{
    /// <summary>The building's kind, so door consumers (garage doors) need not keep the tile.</summary>
    public BuildingKind Kind { get; init; }

    /// <summary>A garage's drive-in room behind this door (<see cref="GarageBay"/>), null for every other door.</summary>
    public GarageBay.Bay? Bay { get; init; }
}

/// <summary>
/// A building's plan-view box and its door, derived from nothing but its wall triangles. The
/// <c>.bldg</c> format carries no outline, no orientation and no entrance, so all three are
/// reconstructed here — deterministically, because every client draws the door from this and
/// the server checks entry requests against it.
/// </summary>
public sealed record Footprint(
    BuildingKey Key, BuildingKind Kind,
    Vector2 Center, Vector2 AxisU, float Width, float Depth,
    DoorSpot Door)
{
    /// <summary>Back of the building, i.e. away from the door, horizontal unit (x, z).</summary>
    public Vector2 AxisV => new(-AxisU.Y, AxisU.X);

    /// <summary>
    /// Yaw of the interior frame: local +X maps to <see cref="AxisU"/>, local +Z to
    /// <see cref="AxisV"/>, which is a proper rotation (X × Y = Z) by construction.
    /// </summary>
    public float Yaw => Mathf.Atan2(-AxisU.Y, AxisU.X);

    /// <summary>The door's position along the entry wall, in interior-local X.</summary>
    public float EntryX
    {
        get
        {
            var d = new Vector2(Door.Position.X, Door.Position.Z) - Center;
            float half = Width * 0.5f - Door.Width * 0.5f - 0.35f;
            return half <= 0 ? 0 : Mathf.Clamp(d.Dot(AxisU), -half, half);
        }
    }
}

public static class BuildingFootprint
{
    /// <summary>Same wall/roof split the building renderer uses.</summary>
    private const float RoofNormalY = 0.45f;

    /// <summary>Rooms need somewhere to stand; a 1.5 m shed is still entered, as a 3 m box.</summary>
    public const float MinSide = 3.0f;

    /// <summary>Beyond this the interior is clamped — a 300 m warehouse is one hall either way.</summary>
    public const float MaxSide = 120f;

    public static float DoorWidthFor(BuildingKind kind) => kind switch
    {
        BuildingKind.House or BuildingKind.Other => 1.0f,
        BuildingKind.Apartment or BuildingKind.Commercial or BuildingKind.Civic or BuildingKind.Sacral => 1.8f,
        _ => 2.8f, // barns, works, garages
    };

    public static float DoorHeightFor(BuildingKind kind) =>
        DoorWidthFor(kind) > 2f ? 2.8f : kind == BuildingKind.Sacral ? 2.6f : 2.1f;

    /// <summary>The front door's leaf, linear: the facade's baked leaf and the interior's swinging one.</summary>
    public static Color DoorLeafColorFor(BuildingKind kind) => (kind switch
    {
        BuildingKind.Agricultural or BuildingKind.Annex => new Color(0.42f, 0.30f, 0.20f),
        BuildingKind.Industrial => new Color(0.46f, 0.50f, 0.54f),
        BuildingKind.Apartment or BuildingKind.Commercial or BuildingKind.Civic => new Color(0.22f, 0.26f, 0.30f),
        BuildingKind.Sacral => new Color(0.30f, 0.18f, 0.10f), // old oak
        _ => new Color(0.40f, 0.25f, 0.15f),
    }).SrgbToLinear();

    /// <summary>The door frame on the facade, linear.</summary>
    public static readonly Color DoorFrameColor = new Color(0.86f, 0.84f, 0.79f).SrgbToLinear();

    /// <summary>The front door's street-side handle, linear: on the facade and on the swinging leaf.</summary>
    public static readonly Color DoorHandleColor = DoorFrameColor * 0.7f;

    /// <summary>Doors for every building of a tile, in building order.</summary>
    public static DoorSpot[] ComputeDoors(BuildingTile tile, RoadTile? roads, ChunkGrid? grid)
    {
        var roadIndex = (RoadPoints.Build(roads), RoadPoints.Build(roads, paths: true));
        var doors = new DoorSpot[tile.Buildings.Count];
        for (int i = 0; i < doors.Length; i++)
        {
            var d = (Compute(tile, i, roadIndex, grid)?.Door ?? default) with { Kind = tile.Buildings[i].Kind };
            // a bay needs the sill on the real ground: only with the full-resolution grid
            if (grid != null && d.Kind == BuildingKind.Garage && GarageBay.Plan(tile, i, d) is (var fitted, { } bay))
                d = fitted with { Bay = bay };
            doors[i] = d;
        }
        return doors;
    }

    public static Footprint? Compute(BuildingTile tile, int index, RoadTile? roads, ChunkGrid? grid) =>
        Compute(tile, index, (RoadPoints.Build(roads), RoadPoints.Build(roads, paths: true)), grid);

    /// <summary>The street point a front door here would face, as <see cref="Compute"/> aims doors.</summary>
    public static Vector2? StreetNear(RoadTile? roads, Vector2 at) =>
        RoadPoints.Build(roads).Nearest(at, 60f) ?? RoadPoints.Build(roads, paths: true).Nearest(at, 40f);

    private static Footprint? Compute(BuildingTile tile, int index, (RoadPoints Streets, RoadPoints Paths) roads, ChunkGrid? grid)
    {
        var b = tile.Buildings[index];
        var key = new BuildingKey(tile.Id.E, tile.Id.N, index);

        // ---- plan box, shared with type detection ------------------------------------------
        var map = BuildingTypes.For(tile);
        if (map.Boxes[index] is not { } box) return null;
        Vector2 u = box.AxisU, center = box.Center;
        float w = box.Width, dpt = box.Depth;
        // a church's tower gets a church door, whatever kind the cadastre match made it
        var group = map.GroupOf(index);
        var kind = group?.Type == BuildingType.Church ? BuildingKind.Sacral : b.Kind;
        // the other solids of the same building: a door on a wall one of them stands against
        // would open into it
        var fellows = group?.Members.Where(m => m != index && map.Boxes[m] != null).Select(m => map.Boxes[m]!.Value).ToList();
        bool Covered(Vector2 xz) => fellows != null && fellows.Any(f => f.DistanceTo(xz) < 0.6f);

        // ---- wall triangles -------------------------------------------------------------
        var walls = new List<(Vector3 A, Vector3 B, Vector3 C, Vector2 N)>();
        for (int t = 0; t < b.TriangleCount; t++)
        {
            int o = t * 9;
            var a = new Vector3(b.Triangles[o], b.Triangles[o + 1], b.Triangles[o + 2]);
            var c = new Vector3(b.Triangles[o + 3], b.Triangles[o + 4], b.Triangles[o + 5]);
            var d = new Vector3(b.Triangles[o + 6], b.Triangles[o + 7], b.Triangles[o + 8]);
            var n = (c - a).Cross(d - a);
            float len = n.Length();
            if (len < 1e-6f || Mathf.Abs(n.Y / len) >= RoofNormalY) continue;
            var flat = new Vector2(n.X, n.Z);
            if (flat.LengthSquared() < 1e-10f) continue;
            walls.Add((a, c, d, flat.Normalized()));
        }

        // ---- facade facets: coplanar wall triangles, merged along their wall line --------
        var facets = new Dictionary<(int, int), Facet>();
        var cuts = new List<(Vector2 Mid, Vector2 Normal, float Ground)>();
        foreach (var (a, c, d, n0) in walls)
        {
            var mid = new Vector2((a.X + c.X + d.X) / 3f, (a.Z + c.Z + d.Z) / 3f);
            // TIN winding is not consistent; outward is away from the box centre
            var n = n0.Dot(mid - center) < 0 ? -n0 : n0;
            int angle = Mathf.RoundToInt(Mathf.RadToDeg(Mathf.Atan2(n.Y, n.X)) / 6f);
            // tight: coplanar TIN triangles agree to the millimetre, while a recess or a bay only
            // 0.3 m back is a different wall - a coarse bin merged the two and put the door in
            // the air between them
            int offset = Mathf.RoundToInt(n.Dot(mid) / 0.08f);
            var k = (angle, offset);
            if (!facets.TryGetValue(k, out var f)) facets[k] = f = new Facet(n, n.Dot(mid));
            // The run is the wall's cross-section a metre above the ground in front of it, not the
            // triangle's full width: a gable, or an upper storey over a porch roof, is wall where
            // no door can go, and taking its whole extent put doors in mid-air under it.
            float groundHere = grid != null
                ? (float)grid.SampleMeshHeight(tile.Id.MinE + mid.X, tile.Id.MaxN - mid.Y)
                : b.MinY + 0.8f;
            if (!CutAt(a, c, d, Math.Max(groundHere, b.MinY) + 1.0f, out var p0, out var p1)) continue;
            cuts.Add(((p0 + p1) * 0.5f, n, Math.Max(groundHere, b.MinY)));
            var t = new Vector2(-n.Y, n.X);
            float u0 = t.Dot(p0), u1 = t.Dot(p1);
            f.Spans.Add((Math.Min(u0, u1), Math.Max(u0, u1)));
        }

        float doorW = DoorWidthFor(kind), doorH = DoorHeightFor(kind);
        var roadTarget = roads.Streets.Nearest(center, 60f) ?? roads.Paths.Nearest(center, 40f);

        var ranked = new List<(float Score, DoorSpot Door)>();
        foreach (var f in facets.Values)
        {
            var (s0, s1) = f.LongestRun();
            float length = s1 - s0;
            if (length < 0.9f) continue;
            float width = Math.Min(doorW, length - 0.3f);
            var t = new Vector2(-f.Normal.Y, f.Normal.X);
            var xz = f.Normal * f.Offset + t * ((s0 + s1) * 0.5f);
            if (Covered(xz)) continue;

            float ground = grid != null
                ? (float)grid.SampleMeshHeight(tile.Id.MinE + xz.X, tile.Id.MaxN - xz.Y)
                : b.MinY + 0.8f;
            float baseY = Math.Max(ground, b.MinY);

            float score = Math.Min(length, 12f) * 0.08f;
            if (roadTarget is { } r)
            {
                var to = r - xz;
                float dist = to.Length();
                if (dist > 1e-3f) score += f.Normal.Dot(to / dist) * 1.5f;
                score -= dist * 0.01f;
            }
            else score += f.Normal.Dot(new Vector2(0.3f, 0.95f)) * 0.3f; // south-ish, like Swiss entrances
            if (ground < b.MinY - 0.6f) score -= 2f;          // on stilts over a slope
            if (ground > b.MaxY - doorH - 0.3f) score -= 3f; // buried side of a hillside house
            if (width < doorW * 0.8f) score -= 1f;

            var pos = new Vector3(xz.X + f.Normal.X * 0.03f, baseY, xz.Y + f.Normal.Y * 0.03f);
            ranked.Add((score, new DoorSpot(index, pos, new Vector3(f.Normal.X, 0, f.Normal.Y), Math.Max(0.7f, width), doorH)));
        }

        // best-scoring door that is actually on a wall; the score alone can pick a run whose
        // middle falls in a gap the cross-section never saw (a pillar between two bays)
        DoorSpot door = default;
        bool found = false;
        foreach (var (_, candidate) in ranked.OrderByDescending(r => r.Score))
            if (DoorOnWall(b, candidate)) { door = candidate; found = true; break; }
        if (!found && cuts.Count > 0)
        {
            // a round tank or a many-sided silo: no flat run is door-wide, so stand the door on
            // whichever real piece of wall faces the street best
            var aim = roadTarget ?? center + new Vector2(0.3f, 0.95f) * 50f;
            var open = cuts.Where(k => !Covered(k.Mid)).ToList();
            var cut = (open.Count > 0 ? open : cuts).MaxBy(k => k.Normal.Dot((aim - k.Mid).Normalized()) - k.Mid.DistanceTo(aim) * 0.01f);
            var pos = new Vector3(cut.Mid.X + cut.Normal.X * 0.03f, cut.Ground, cut.Mid.Y + cut.Normal.Y * 0.03f);
            door = new DoorSpot(index, pos, new Vector3(cut.Normal.X, 0, cut.Normal.Y), Math.Min(doorW, 1.0f), doorH);
            found = true;
        }
        if (!found && ranked.Count > 0) { door = ranked.MaxBy(r => r.Score).Door; found = true; }
        // No wall anywhere a metre above the ground: a roof on posts, a canopy, a reservoir sunk
        // into the slope. Nothing to walk into, so no door (Width 0) rather than one in mid-air.
        if (!found)
        {
            var v = new Vector2(-u.Y, u.X);
            var xz = center - v * (dpt * 0.5f);
            door = new DoorSpot(index, new Vector3(xz.X, b.MinY, xz.Y), new Vector3(-v.X, 0, -v.Y), 0f, doorH);
        }

        // ---- interior frame: the box edge the door is on becomes local -Z ----------------
        var outward = new Vector2(door.Outward.X, door.Outward.Z);
        var candidates = new[] { u, -u, new Vector2(-u.Y, u.X), new Vector2(u.Y, -u.X) };
        var edge = candidates.OrderByDescending(c => c.Dot(outward)).First();
        var back = -edge;
        var axisU = new Vector2(back.Y, -back.X); // so that AxisV == back
        bool alongU = Mathf.Abs(axisU.Dot(u)) > 0.5f;
        float width2 = alongU ? w : dpt, depth2 = alongU ? dpt : w;

        return new Footprint(key, kind, center, axisU,
            Mathf.Clamp(width2, MinSide, MaxSide), Mathf.Clamp(depth2, MinSide, MaxSide), door);
    }

    /// <summary>Where a triangle crosses the horizontal plane at <paramref name="y"/>, in plan (x, z).</summary>
    private static bool CutAt(Vector3 a, Vector3 b, Vector3 c, float y, out Vector2 p0, out Vector2 p1)
    {
        var hits = new List<Vector2>(3);
        void Edge(Vector3 u, Vector3 v)
        {
            if ((u.Y - y) * (v.Y - y) > 0 || Mathf.Abs(u.Y - v.Y) < 1e-6f) return;
            float s = (y - u.Y) / (v.Y - u.Y);
            hits.Add(new Vector2(u.X + (v.X - u.X) * s, u.Z + (v.Z - u.Z) * s));
        }
        Edge(a, b); Edge(b, c); Edge(c, a);
        p0 = p1 = default;
        if (hits.Count < 2) return false;
        p0 = hits[0];
        p1 = hits[1].DistanceSquaredTo(p0) > (hits.Count > 2 ? hits[2].DistanceSquaredTo(p0) : -1) ? hits[1] : hits[2];
        return p0.DistanceSquaredTo(p1) > 1e-6f;
    }

    /// <summary>
    /// Whether the door's centre, a metre up, lies on one of the building's own triangles, within
    /// 12 cm of it. A door that only scores well can still hang in the air beside the wall.
    /// </summary>
    public static bool DoorOnWall(Building b, DoorSpot door)
    {
        var p = door.Position + Vector3.Up * Math.Min(1.0f, door.Height * 0.5f) - door.Outward * 0.03f;
        for (int t = 0; t < b.TriangleCount; t++)
        {
            int o = t * 9;
            var a = new Vector3(b.Triangles[o], b.Triangles[o + 1], b.Triangles[o + 2]);
            var c = new Vector3(b.Triangles[o + 3], b.Triangles[o + 4], b.Triangles[o + 5]);
            var d = new Vector3(b.Triangles[o + 6], b.Triangles[o + 7], b.Triangles[o + 8]);
            var n = (c - a).Cross(d - a);
            float len = n.Length();
            if (len < 1e-6f) continue;
            n /= len;
            float dist = (p - a).Dot(n);
            if (Mathf.Abs(dist) > 0.12f) continue;
            var q = p - n * dist;
            // barycentric, with a little slack so a point on a shared edge counts
            var v0 = c - a; var v1 = d - a; var v2 = q - a;
            float d00 = v0.Dot(v0), d01 = v0.Dot(v1), d11 = v1.Dot(v1), d20 = v2.Dot(v0), d21 = v2.Dot(v1);
            float den = d00 * d11 - d01 * d01;
            if (Mathf.Abs(den) < 1e-9f) continue;
            float v = (d11 * d20 - d01 * d21) / den, w = (d00 * d21 - d01 * d20) / den;
            if (v >= -0.02f && w >= -0.02f && v + w <= 1.02f) return true;
        }
        return false;
    }

    private sealed class Facet
    {
        public readonly Vector2 Normal;
        public readonly float Offset;
        public readonly List<(float, float)> Spans = new();
        public Facet(Vector2 normal, float offset) { Normal = normal; Offset = offset; }

        /// <summary>Longest continuous stretch of wall on this line (two wings of a U are two runs).</summary>
        public (float, float) LongestRun()
        {
            if (Spans.Count == 0) return (0, 0); // wall entirely above door height
            Spans.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            (float, float) best = (0, 0), cur = Spans[0];
            foreach (var s in Spans)
            {
                if (s.Item1 <= cur.Item2 + 0.05f) cur.Item2 = Math.Max(cur.Item2, s.Item2);
                else
                {
                    if (cur.Item2 - cur.Item1 > best.Item2 - best.Item1) best = cur;
                    cur = s;
                }
            }
            return cur.Item2 - cur.Item1 > best.Item2 - best.Item1 ? cur : best;
        }
    }

    /// <summary>Road vertices hashed into 20 m cells, so a tile's thousand buildings each find their street quickly.</summary>
    private sealed class RoadPoints
    {
        private const float Cell = 20f;
        private readonly Dictionary<(int, int), List<Vector2>> _cells = new();

        /// <summary>
        /// Streets only by default: a front door faces the street, and a footpath or farm track
        /// running past the back garden is often nearer than the road in front. With
        /// <paramref name="paths"/> the index holds paths and tracks instead, the fallback for a
        /// building no street reaches.
        /// </summary>
        public static RoadPoints Build(RoadTile? roads, bool paths = false)
        {
            var r = new RoadPoints();
            if (roads == null) return r;
            foreach (var s in roads.Segments)
            {
                // somewhere a front door faces: not motorways, rail or rivers
                if (s.Class is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp
                    || s.Class >= RoadClass.Railway) continue;
                bool isPath = s.Class is RoadClass.Track or RoadClass.Path or RoadClass.Link;
                if (isPath != paths) continue;
                for (int i = 0; i < s.PointCount; i++)
                {
                    var p = new Vector2(s.Points[i * 3], s.Points[i * 3 + 2]);
                    var k = ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell));
                    if (!r._cells.TryGetValue(k, out var list)) r._cells[k] = list = new();
                    list.Add(p);
                }
            }
            return r;
        }

        public Vector2? Nearest(Vector2 at, float reach)
        {
            if (_cells.Count == 0) return null;
            int span = (int)MathF.Ceiling(reach / Cell);
            int cx = (int)MathF.Floor(at.X / Cell), cz = (int)MathF.Floor(at.Y / Cell);
            Vector2? best = null;
            float bestD = reach * reach;
            for (int x = cx - span; x <= cx + span; x++)
                for (int z = cz - span; z <= cz + span; z++)
                    if (_cells.TryGetValue((x, z), out var list))
                        foreach (var p in list)
                        {
                            float d = p.DistanceSquaredTo(at);
                            if (d < bestD) { bestD = d; best = p; }
                        }
            return best;
        }
    }
}
