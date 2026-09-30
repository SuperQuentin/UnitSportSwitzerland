using System.Collections.Concurrent;
using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// The real inside of a garage (#56): a hole in the facade behind the roll-up door, a box room
/// inset in the walls (floor at the sill, ceiling, four inner walls, reveals lining the opening)
/// and the terrain cells under it carved away, so a car drives in instead of stopping at a
/// painted door. Everything here is a pure function of the building and its door, so the render
/// mesh, the collision faces and the terrain holes agree on every peer.
///
/// <para>
/// The bay frame, in tile-local metres: <c>s</c> along <see cref="Bay.N"/> (out of the door
/// wall), <c>a</c> along <see cref="Bay.T"/> (the door's own "along the wall" axis), <c>y</c> up.
/// </para>
/// </summary>
public static class GarageBay
{
    /// <summary>Inner walls stand this far inside the building's plan box.</summary>
    public const float Inset = 0.15f;
    /// <summary>Smallest inner room a car fits in; a smaller garage keeps its painted door.</summary>
    public const float MinWidth = 2.4f, MinDepth = 4.5f;

    public sealed record Bay(int Index, Vector2 Center, Vector2 N, float OuterS, float OuterA,
        float Back, float Front, float Facade, float HalfA, float Along, float HalfDoor,
        float Sill, float DoorTop, float Ceiling)
    {
        public Vector2 T => new(-N.Y, N.X);

        public Vector3 P(float s, float a, float y) =>
            new(Center.X + N.X * s + T.X * a, y, Center.Y + N.Y * s + T.Y * a);

        /// <summary>Bay coordinates of a tile-local point.</summary>
        public (float S, float A) Local(Vector3 p)
        {
            var d = new Vector2(p.X, p.Z) - Center;
            return (d.Dot(N), d.Dot(T));
        }

        /// <summary>Whether a tile-local point is in the room (up to the facade), with a margin.</summary>
        public bool Contains(Vector3 p, float margin = 0f)
        {
            var (s, a) = Local(p);
            return s >= Back - margin && s <= Facade + margin && Mathf.Abs(a) <= HalfA + margin
                && p.Y >= Sill - 1.5f && p.Y <= Ceiling + 0.5f;
        }
    }

    private static readonly ConcurrentDictionary<(TileId, int), byte> Logged = new();

    /// <summary>Narrowest and lowest a drive-in doorway is made to fit a small garage; a car is ~1.8 x 1.5 m.</summary>
    public const float MinDoorWidth = 2.3f, MinDoorHeight = 2.1f;

    /// <summary>
    /// The bay behind a garage's door, and the door fitted to it (narrowed or slid along the wall
    /// to clear the inner side walls, lowered under a low eave). No bay, and the door unchanged,
    /// when the door is not on a plan-box side, the box is too small for a car, or it is not a
    /// plain rectangle (logged once each).
    /// </summary>
    public static (DoorSpot Door, Bay? Bay) Plan(BuildingTile tile, int index, DoorSpot door)
    {
        var b = tile.Buildings[index];
        if (b.Kind != BuildingKind.Garage || door.Width <= 0) return (door, null);
        if (BuildingTypes.For(tile).Boxes[index] is not { } box) return (door, null);

        var o = new Vector2(door.Outward.X, door.Outward.Z);
        var axes = new[] { box.AxisU, -box.AxisU, box.AxisV, -box.AxisV };
        var n = axes.MaxBy(x => x.Dot(o));
        bool alongU = Mathf.Abs(n.Dot(box.AxisU)) > 0.5f;
        float hn = (alongU ? box.Width : box.Depth) / 2, ht = (alongU ? box.Depth : box.Width) / 2;
        var bay = new Bay(index, box.Center, n, hn, ht, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var (sd, ad) = bay.Local(door.Position);
        float facade = sd - 0.03f, halfA = ht - Inset, sill = door.Position.Y;
        // clear of the inner side walls, and under the eave with room for a ceiling
        float hw = Math.Min(door.Width / 2, halfA - 0.05f);
        float along = Mathf.Clamp(ad, -(halfA - 0.05f - hw), halfA - 0.05f - hw);
        float height = Math.Min(door.Height, box.Eave - 0.2f - sill);

        string? why = null;
        if (n.Dot(o) < 0.966f) why = "door not square to the plan box";
        else if (facade > hn + 0.1f || facade < hn - 0.5f) why = $"door wall {hn - facade:F2} m off the box side";
        else if (2 * halfA < MinWidth || facade - Inset + hn - Inset < MinDepth)
            why = $"too small ({2 * halfA:F1} x {facade - Inset + hn - Inset:F1} m)";
        else if (2 * hw < MinDoorWidth) why = $"door only {2 * hw:F1} m wide";
        else if (height < MinDoorHeight) why = $"eave {box.Eave - sill:F1} m over the sill";
        if (why == null)
        {
            float top = sill + height;
            bay = bay with
            {
                Back = -hn + Inset, Front = facade - Inset, Facade = facade, HalfA = halfA,
                Along = along, HalfDoor = hw, Sill = sill, DoorTop = top,
                Ceiling = Math.Max(top + 0.1f, Math.Min(top + 0.4f, box.Eave - 0.1f)),
            };
            if (!Covered(b, bay)) why = "footprint not a rectangle";
        }
        if (why == null)
            return (door with { Position = bay.P(facade + 0.03f, along, sill), Width = 2 * hw, Height = height }, bay);
        if (Logged.TryAdd((tile.Id, index), 0))
            GD.Print($"[garage] {tile.Id} #{index}: no drive-in bay, {why}");
        return (door, null);
    }

    /// <summary>Whether the building's own roof covers the whole inner room, sampled every half metre.</summary>
    private static bool Covered(Building b, Bay bay)
    {
        var roofs = new List<(Vector2, Vector2, Vector2)>();
        for (int t = 0; t < b.TriangleCount; t++)
        {
            var (p, q, r) = Tri(b.Triangles, t);
            var nrm = (q - p).Cross(r - p);
            float len = nrm.Length();
            if (len > 1e-6f && Mathf.Abs(nrm.Y / len) >= 0.45f)
                roofs.Add((new(p.X, p.Z), new(q.X, q.Z), new(r.X, r.Z)));
        }
        int ns = Mathf.CeilToInt((bay.Front - bay.Back) / 0.5f), na = Mathf.CeilToInt(2 * bay.HalfA / 0.5f);
        for (int i = 0; i <= ns; i++)
            for (int j = 0; j <= na; j++)
            {
                var p = bay.P(Mathf.Lerp(bay.Back, bay.Front, i / (float)ns), Mathf.Lerp(-bay.HalfA, bay.HalfA, j / (float)na), 0);
                var xz = new Vector2(p.X, p.Z);
                if (!roofs.Any(r => InTri(xz, r.Item1, r.Item2, r.Item3))) return false;
            }
        return true;
    }

    private static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        static float Cross(Vector2 o, Vector2 u, Vector2 v) => (u.X - o.X) * (v.Y - o.Y) - (u.Y - o.Y) * (v.X - o.X);
        float d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
        const float e = 1e-4f;
        return (d1 >= -e && d2 >= -e && d3 >= -e) || (d1 <= e && d2 <= e && d3 <= e);
    }

    public static (Vector3, Vector3, Vector3) Tri(float[] t, int i)
    {
        int o = i * 9;
        return (new(t[o], t[o + 1], t[o + 2]), new(t[o + 3], t[o + 4], t[o + 5]), new(t[o + 6], t[o + 7], t[o + 8]));
    }

    // ---- the facade opening --------------------------------------------------------------------

    /// <summary>
    /// The building's triangles with the doorway cut out of the facade: every wall triangle on the
    /// door's plane loses what lies inside the opening (the door width, sill to lintel).
    /// </summary>
    public static float[] CutFacade(Building b, Bay bay)
    {
        var o = new Vector3(bay.N.X, 0, bay.N.Y);
        var t = new Vector3(bay.T.X, 0, bay.T.Y);
        var origin = bay.P(bay.Facade, bay.Along, bay.Sill);
        var kept = new List<Vector3>(b.TriangleCount * 3 + 32);
        for (int i = 0; i < b.TriangleCount; i++)
        {
            var (p, q, r) = Tri(b.Triangles, i);
            var nrm = (q - p).Cross(r - p);
            float len = nrm.Length();
            bool onFacade = len > 1e-6f && Mathf.Abs(nrm.Dot(o)) / len > 0.95f
                && Mathf.Abs(((p + q + r) / 3 - origin).Dot(o)) < 0.35f;
            if (onFacade) SubtractRect(p, q, r, origin, t, Vector3.Up, -bay.HalfDoor, bay.HalfDoor, 0, bay.DoorTop - bay.Sill, kept);
            else { kept.Add(p); kept.Add(q); kept.Add(r); }
        }
        var flat = new float[kept.Count * 3];
        for (int i = 0; i < kept.Count; i++)
        {
            flat[i * 3] = kept[i].X; flat[i * 3 + 1] = kept[i].Y; flat[i * 3 + 2] = kept[i].Z;
        }
        return flat;
    }

    /// <summary>
    /// Appends the parts of triangle (p, q, r) outside the rectangle a0..a1 x b0..b1, measured from
    /// <paramref name="origin"/> along <paramref name="ax"/> and <paramref name="ay"/>, as
    /// triangles with the input's winding: the outside is cut into four disjoint convex bands
    /// (left, right, and below and above between them) and the triangle clipped to each.
    /// </summary>
    public static void SubtractRect(Vector3 p, Vector3 q, Vector3 r, Vector3 origin, Vector3 ax, Vector3 ay,
        float a0, float a1, float b0, float b1, List<Vector3> kept)
    {
        float A(Vector3 v) => (v - origin).Dot(ax);
        float B(Vector3 v) => (v - origin).Dot(ay);
        float minA = Math.Min(A(p), Math.Min(A(q), A(r))), maxA = Math.Max(A(p), Math.Max(A(q), A(r)));
        float minB = Math.Min(B(p), Math.Min(B(q), B(r))), maxB = Math.Max(B(p), Math.Max(B(q), B(r)));
        if (maxA <= a0 || minA >= a1 || maxB <= b0 || minB >= b1) { kept.Add(p); kept.Add(q); kept.Add(r); return; }

        Func<Vector3, float> left = v => a0 - A(v), right = v => A(v) - a1;
        Func<Vector3, float> inA0 = v => A(v) - a0, inA1 = v => a1 - A(v);
        Func<Vector3, float> below = v => b0 - B(v), above = v => B(v) - b1;
        Emit(Clip([p, q, r], left), kept);
        Emit(Clip([p, q, r], right), kept);
        Emit(Clip([p, q, r], inA0, inA1, below), kept);
        Emit(Clip([p, q, r], inA0, inA1, above), kept);
    }

    /// <summary>The part of triangle (p, q, r) inside the rectangle, for the self-check.</summary>
    private static List<Vector3> Inside(Vector3 p, Vector3 q, Vector3 r, Vector3 origin, Vector3 ax, Vector3 ay,
        float a0, float a1, float b0, float b1)
    {
        var o = new List<Vector3>();
        Emit(Clip([p, q, r], v => (v - origin).Dot(ax) - a0, v => a1 - (v - origin).Dot(ax),
            v => (v - origin).Dot(ay) - b0, v => b1 - (v - origin).Dot(ay)), o);
        return o;
    }

    /// <summary>Sutherland-Hodgman: the polygon's part where every <paramref name="planes"/> is &gt;= 0.</summary>
    private static List<Vector3> Clip(List<Vector3> poly, params Func<Vector3, float>[] planes)
    {
        foreach (var f in planes)
        {
            if (poly.Count < 3) break;
            var next = new List<Vector3>(poly.Count + 2);
            for (int i = 0; i < poly.Count; i++)
            {
                var u = poly[i];
                var v = poly[(i + 1) % poly.Count];
                float fu = f(u), fv = f(v);
                if (fu >= 0) next.Add(u);
                if ((fu >= 0) != (fv >= 0)) next.Add(u + (v - u) * (fu / (fu - fv)));
            }
            poly = next;
        }
        return poly;
    }

    private static void Emit(List<Vector3> poly, List<Vector3> kept)
    {
        for (int i = 1; i + 1 < poly.Count; i++)
        {
            if ((poly[i] - poly[0]).Cross(poly[i + 1] - poly[0]).LengthSquared() < 1e-10f) continue;
            kept.Add(poly[0]); kept.Add(poly[i]); kept.Add(poly[i + 1]);
        }
    }

    // ---- the room ------------------------------------------------------------------------------

    public static readonly Color FloorColor = new Color(0.52f, 0.52f, 0.50f).SrgbToLinear();
    public static readonly Color WallColor = new Color(0.74f, 0.74f, 0.72f).SrgbToLinear();
    public static readonly Color CeilingColor = new Color(0.62f, 0.62f, 0.62f).SrgbToLinear();

    /// <summary>
    /// The room's quads (a, b, c, d in order around the edge): floor, ceiling, back and side walls,
    /// the front wall around the opening, and the reveals between the facade and the front wall.
    /// Callers emit both windings: it is seen and collided from inside and out.
    /// </summary>
    public static void Room(Bay b, Action<Vector3, Vector3, Vector3, Vector3, Color> quad)
    {
        float s0 = b.Back, s1 = b.Front, sf = b.Facade, a = b.HalfA, y0 = b.Sill, yd = b.DoorTop, yc = b.Ceiling;
        float d0 = b.Along - b.HalfDoor, d1 = b.Along + b.HalfDoor;
        void Flat(float sa, float sb, float aa, float ab, float y, Color c) =>
            quad(b.P(sa, aa, y), b.P(sb, aa, y), b.P(sb, ab, y), b.P(sa, ab, y), c);
        void AlongA(float s, float aa, float ab, float ya, float yb, Color c) =>   // a wall facing +-N
            quad(b.P(s, aa, ya), b.P(s, ab, ya), b.P(s, ab, yb), b.P(s, aa, yb), c);
        void AlongS(float aa, float sa, float sb, float ya, float yb, Color c) =>  // a wall facing +-T
            quad(b.P(sa, aa, ya), b.P(sb, aa, ya), b.P(sb, aa, yb), b.P(sa, aa, yb), c);

        Flat(s0, s1, -a, a, y0, FloorColor);
        Flat(s1, sf, d0, d1, y0, FloorColor);
        Flat(s0, s1, -a, a, yc, CeilingColor);
        Flat(s1, sf, d0, d1, yd, WallColor);
        AlongA(s0, -a, a, y0, yc, WallColor);
        AlongS(-a, s0, s1, y0, yc, WallColor);
        AlongS(a, s0, s1, y0, yc, WallColor);
        AlongA(s1, -a, d0, y0, yc, WallColor);
        AlongA(s1, d1, a, y0, yc, WallColor);
        AlongA(s1, d0, d1, yd, yc, WallColor);
        AlongS(d0, s1, sf, y0, yd, WallColor);
        AlongS(d1, s1, sf, y0, yd, WallColor);
    }

    // ---- the ground under it -------------------------------------------------------------------

    /// <summary>
    /// Full-resolution terrain cells (<see cref="HoleFormat.CellIndex"/>) touching a bay's inner
    /// room: carved from the terrain mesh and collision so the floor slab is the ground in there.
    /// </summary>
    public static HashSet<int> HoleCells(IEnumerable<Bay> bays)
    {
        var cells = new HashSet<int>();
        foreach (var b in bays)
        {
            var c = new[] { b.P(b.Back, -b.HalfA, 0), b.P(b.Back, b.HalfA, 0), b.P(b.Facade, b.HalfA, 0), b.P(b.Facade, -b.HalfA, 0) };
            int c0 = Math.Max(0, (int)Mathf.Floor(c.Min(p => p.X))), c1 = Math.Min(HoleFormat.QuadsPerSide - 1, (int)Mathf.Floor(c.Max(p => p.X)));
            int r0 = Math.Max(0, (int)Mathf.Floor(c.Min(p => p.Z))), r1 = Math.Min(HoleFormat.QuadsPerSide - 1, (int)Mathf.Floor(c.Max(p => p.Z)));
            for (int r = r0; r <= r1; r++)
                for (int col = c0; col <= c1; col++)
                    if (CellTouches(b, col, r)) cells.Add(HoleFormat.CellIndex(col, r));
        }
        return cells;
    }

    /// <summary>Separating axes: the unit cell (col, row) against the inner room up to the facade.</summary>
    private static bool CellTouches(Bay b, int col, int row)
    {
        var (sc, ac) = b.Local(new Vector3(col + 0.5f, 0, row + 0.5f));
        // the cell's half-extent projected on N and T
        float en = 0.5f * (Mathf.Abs(b.N.X) + Mathf.Abs(b.N.Y)), et = 0.5f * (Mathf.Abs(b.T.X) + Mathf.Abs(b.T.Y));
        float sMid = (b.Back + b.Facade) / 2, sHalf = (b.Facade - b.Back) / 2;
        if (Mathf.Abs(sc - sMid) >= sHalf + en || Mathf.Abs(ac) >= b.HalfA + et) return false;
        // the box's half-extent projected on the grid axes
        var corners = new[] { b.P(b.Back, -b.HalfA, 0), b.P(b.Back, b.HalfA, 0), b.P(b.Facade, b.HalfA, 0), b.P(b.Facade, -b.HalfA, 0) };
        return !(corners.Max(p => p.X) <= col || corners.Min(p => p.X) >= col + 1
            || corners.Max(p => p.Z) <= row || corners.Min(p => p.Z) >= row + 1);
    }

    /// <summary>
    /// The carved cells' ground outside the buildings' plan boxes, as triangles on the terrain's own
    /// vertices and diagonal: a carved cell straddles the wall, and without this its outer part
    /// would be a slot beside the garage. For collision the cells Jolt drops too (one NaN vertex
    /// removes the four quads around it, so the hole reaches one cell further west and north).
    /// </summary>
    public static void Apron(IReadOnlyList<Bay> bays, HashSet<int> cells, IReadOnlySet<int>? otherHoles,
        Func<int, int, float> ground, bool collision, List<Vector3> tris)
    {
        var todo = new HashSet<int>(cells);
        if (collision)
            foreach (int cell in cells)
            {
                int c = cell % HoleFormat.QuadsPerSide, r = cell / HoleFormat.QuadsPerSide;
                if (c > 0) todo.Add(cell - 1);
                if (r > 0) todo.Add(cell - HoleFormat.QuadsPerSide);
                if (c > 0 && r > 0) todo.Add(cell - HoleFormat.QuadsPerSide - 1);
            }
        var pieces = new List<Vector3>();
        var next = new List<Vector3>();
        foreach (int cell in todo)
        {
            if (otherHoles != null && otherHoles.Contains(cell) && !cells.Contains(cell)) continue;
            int c = cell % HoleFormat.QuadsPerSide, r = cell / HoleFormat.QuadsPerSide;
            Vector3 V(int cc, int rr) => new(cc, ground(cc, rr), rr);
            pieces.Clear();
            // the terrain mesh's diagonal: (v00, v10, v01) and (v10, v11, v01)
            pieces.AddRange([V(c, r), V(c + 1, r), V(c, r + 1), V(c + 1, r), V(c + 1, r + 1), V(c, r + 1)]);
            foreach (var b in bays)
            {
                var origin = new Vector3(b.Center.X, 0, b.Center.Y);
                next.Clear();
                for (int i = 0; i < pieces.Count; i += 3)
                    SubtractRect(pieces[i], pieces[i + 1], pieces[i + 2], origin, new Vector3(b.T.X, 0, b.T.Y),
                        new Vector3(b.N.X, 0, b.N.Y), -b.OuterA, b.OuterA, -b.OuterS, b.OuterS, next);
                (pieces, next) = (next, pieces);
            }
            tris.AddRange(pieces);
        }
    }

    // ---- self-check ----------------------------------------------------------------------------

    private static float Area(List<Vector3> tris)
    {
        float sum = 0;
        for (int i = 0; i < tris.Count; i += 3) sum += (tris[i + 1] - tris[i]).Cross(tris[i + 2] - tris[i]).Length() / 2;
        return sum;
    }

    /// <summary>
    /// <c>--garagehole</c>: the opening cut conserves area — for random triangles and rectangles,
    /// area(triangle) = area(kept pieces) + area(triangle ∩ rectangle), the pieces keep the winding
    /// and none reaches into the rectangle. Returns the process exit code.
    /// </summary>
    public static int Check()
    {
        var rng = new Random(56);
        float R() => (float)rng.NextDouble() * 8f - 4f;
        int bad = 0;
        for (int k = 0; k < 20000; k++)
        {
            var p = new Vector3(R(), R(), R() * 0.1f);
            var q = new Vector3(R(), R(), R() * 0.1f);
            var r = new Vector3(R(), R(), R() * 0.1f);
            float a0 = R(), a1 = a0 + Math.Abs(R()) + 0.1f, b0 = R(), b1 = b0 + Math.Abs(R()) + 0.1f;
            var origin = new Vector3(R() * 0.2f, R() * 0.2f, 0);
            var ax = new Vector3(1, 0, 0).Rotated(Vector3.Forward, R() * 0.2f);
            var ay = ax.Cross(Vector3.Forward).Normalized() * -1;
            var kept = new List<Vector3>();
            SubtractRect(p, q, r, origin, ax, ay, a0, a1, b0, b1, kept);
            var inside = Inside(p, q, r, origin, ax, ay, a0, a1, b0, b1);
            float whole = Area([p, q, r]), sum = Area(kept) + Area(inside);
            bool ok = Mathf.Abs(whole - sum) <= 1e-3f * Math.Max(1f, whole);
            var nrm = (q - p).Cross(r - p);
            for (int i = 0; i < kept.Count && ok; i += 3)
            {
                ok &= (kept[i + 1] - kept[i]).Cross(kept[i + 2] - kept[i]).Dot(nrm) >= 0;
                var mid = (kept[i] + kept[i + 1] + kept[i + 2]) / 3;
                float ma = (mid - origin).Dot(ax), mb = (mid - origin).Dot(ay);
                ok &= !(ma > a0 + 1e-3f && ma < a1 - 1e-3f && mb > b0 + 1e-3f && mb < b1 - 1e-3f);
            }
            if (!ok && bad++ < 5) GD.Print($"[garagehole] FAIL #{k}: whole {whole:F4} kept+inside {sum:F4}");
        }
        GD.Print($"[garagehole] area conservation over 20000 random cuts: {(bad == 0 ? "ok" : $"{bad} FAIL")}");
        Survey();
        return bad == 0 ? 0 : 1;
    }

    /// <summary>Every garage in the local terrain data: its door, and its bay or why it has none.</summary>
    private static void Survey()
    {
        string dir = Core.TerrainPaths.FindChunkDir();
        var source = new Terrain.LocalChunkSource(dir);
        int garages = 0, bays = 0;
        foreach (var file in Directory.GetFiles(dir, "buildings_*.bldg").Order())
        {
            var parts = Path.GetFileNameWithoutExtension(file).Split('_');
            var id = new TileId(int.Parse(parts[1]), int.Parse(parts[2]));
            var tile = source.LoadBuildingsAsync(id).GetAwaiter().GetResult();
            if (tile == null || !tile.Buildings.Any(b => b.Kind == BuildingKind.Garage)) continue;
            var grid = source.LoadChunkAsync(id).GetAwaiter().GetResult();
            var roads = source.LoadRoadsAsync(id).GetAwaiter().GetResult();
            foreach (var d in BuildingFootprint.ComputeDoors(tile, roads, grid))
            {
                if (d.Kind != BuildingKind.Garage) continue;
                garages++;
                string at = $"{id.MinE + d.Position.X:F1},{id.MaxN - d.Position.Z:F1} out ({d.Outward.X:F2},{d.Outward.Z:F2})";
                if (d.Bay is not { } b) { GD.Print($"[garagehole] #{d.Index} door {at}: no bay"); continue; }
                bays++;
                float rise = float.MinValue;
                foreach (int cell in HoleCells([b]))
                    rise = Math.Max(rise, (float)grid!.HeightMetersAt(cell % HoleFormat.QuadsPerSide, cell / HoleFormat.QuadsPerSide) - b.Sill);
                GD.Print($"[garagehole] #{d.Index} door {at}: bay {2 * b.HalfA:F1} x {b.Front - b.Back:F1} m, "
                    + $"ceiling {b.Ceiling - b.Sill:F1} m, ground up to {rise:F2} m over the sill");
            }
        }
        GD.Print($"[garagehole] {garages} garages, {bays} with a drive-in bay");
    }
}
