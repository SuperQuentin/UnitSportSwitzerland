using Godot;
using UnitSport.Core;
using UnitSport.Styles;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Stadiums;

/// <summary>
/// Builds a stadium (#885) from its <see cref="StadiumSpec"/>: the bowl swept round the seating
/// front (rows on every tier, aisles, gates tunnelling under the stand to the pitch), the back
/// walls, the facade on its pillars, the roof with its translucent ring and solar panels, and a
/// truss on every pillar. One prop-material mesh of vertex colours, and collision faces that join
/// the tile's both-sided road cells like a building site's shell (#608).
///
/// <para>
/// Cheap by design, for a stadium in every canton: a row is a tread and a riser per seat block, no
/// seat is modelled (the garnet tread and the darker riser read as rows of seats from any
/// distance), and the walkable stand is one ramp per tier and block rather than one face per step:
/// the ramp lies on the nosings, so a foot is never more than half a riser off the drawn step.
/// Geneva is about 20 k triangles and 1.5 k collision faces.
/// </para>
///
/// <para>
/// Tile-local coordinates, like every tile builder: x = E − MinE, y = height, z = MaxN − N. The
/// tile holding the centre spot builds the whole stadium (<see cref="StadiumCatalog.Owner"/>), so
/// the bowl may reach into the next tile. Any thread.
/// </para>
/// </summary>
public static class StadiumBuilder
{
    /// <summary>A tile's stadiums: the mesh (null when not asked for), the collision faces, and the seats counted.</summary>
    public sealed record StadiumBuild(PierMeshBuilder.MeshData? Mesh, Vector3[] Faces, int Capacity);

    /// <summary>How far walls run down below the pitch, so uneven ground never shows a gap under them.</summary>
    private const float Skirt = 3f;
    /// <summary>Segments in each rounded corner of the bowl.</summary>
    private const int CornerSegments = 6;
    /// <summary>A gate tunnel keeps this much headroom under the first row it leaves standing.</summary>
    private const float GateClearance = 0.6f;

    private enum Seg : byte { Seat, Aisle, Gate, Corner }

    /// <summary>A point of the seating front, its outward normal, the facade's distance out there, and what the segment starting here is.</summary>
    private readonly record struct Station(Vector2 P, Vector2 N, float Depth, Seg Kind, StadiumSide Side);

    /// <summary>The stadiums whose centre spot lies in this tile, or null when there are none or nothing is asked for.</summary>
    public static StadiumBuild? Build(TileId id, ChunkGrid? grid, bool mesh, bool collision)
    {
        if (!mesh && !collision) return null;
        Geo? g = null;
        int capacity = 0;
        foreach (var s in StadiumCatalog.OwnedBy(id))
        {
            g ??= new Geo(mesh, collision);
            float ground = grid != null ? (float)grid.SampleMeshHeight(s.E, s.N) : 0f;
            var frame = Frame(s, id, ground);
            // the ground round the bowl, over the pitch: where a gate's passage has to start
            Func<Vector2, float>? outside = grid == null ? null : uv =>
            {
                var p = frame(uv, 0);
                return (float)grid.SampleMeshHeight(id.MinE + p.X, id.MaxN - p.Z) - ground;
            };
            capacity += Sweep(s, g, frame, outside);
        }
        return g == null ? null : new StadiumBuild(g.Data(), g.Faces.ToArray(), capacity);
    }

    /// <summary>One stadium in its own frame (u, y, v), the centre spot at the origin: the model viewer's and the check's.</summary>
    public static StadiumBuild BuildLocal(StadiumSpec s, bool mesh = true, bool collision = true)
    {
        var g = new Geo(mesh, collision);
        int capacity = Sweep(s, g, (uv, y) => new Vector3(uv.X, y, uv.Y));
        return new StadiumBuild(g.Data(), g.Faces.ToArray(), capacity);
    }

    /// <summary>The facade's outline in <paramref name="id"/>'s frame (x, z), round the bowl: what the stadium owns (<see cref="StadiumClip"/>).</summary>
    public static Vector2[] Outline(StadiumSpec s, TileId id)
    {
        var frame = Frame(s, id, 0f);
        return Stations(s).Select(st => { var p = frame(st.P + st.N * st.Depth, 0); return new Vector2(p.X, p.Z); }).ToArray();
    }

    /// <summary>The stadium frame (u across, v along the pitch, y up from it) into tile-local metres.</summary>
    private static Func<Vector2, float, Vector3> Frame(StadiumSpec s, TileId id, float ground)
    {
        float a = Mathf.DegToRad(s.AzimuthDeg);
        var u = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
        var v = new Vector3(-MathF.Sin(a), 0, MathF.Cos(a));
        var origin = new Vector3((float)(s.E - id.MinE), ground, (float)(id.MaxN - s.N));
        return (uv, y) => origin + u * uv.X + v * uv.Y + new Vector3(0, y, 0);
    }

    [Showcase("Terrain", "Stadium")]
    private static IEnumerable<(string, Func<Node3D>)> Showcase() =>
        StadiumCatalog.All.Select(s => (s.Name, (Func<Node3D>)(() => new MeshInstance3D
        {
            Name = "Stadium",
            Mesh = ChunkNode.ToPropMesh(BuildLocal(s, collision: false).Mesh!, StyleKit.Material(MaterialRole.Prop)),
        })));

    // ---- the seating front, walked once round -------------------------------------------------

    /// <summary>
    /// The stations round the seating front, clockwise from above starting at the north-west end
    /// of the north side: each side cut into seat blocks with an aisle between two and a gate in
    /// its middle when it has one, each corner into <see cref="CornerSegments"/> arcs.
    /// </summary>
    private static List<Station> Stations(StadiumSpec s)
    {
        var list = new List<Station>();
        float hu = s.FrontHalfU, hv = s.FrontHalfV, rc = s.FrontCorner;

        float DepthAt(Vector2 n)
        {
            float Sq(float x) => x > 0 ? x * x : 0f;
            return s.DepthNorth * Sq(-n.Y) + s.DepthEast * Sq(n.X) + s.DepthSouth * Sq(n.Y) + s.DepthWest * Sq(-n.X);
        }
        void Add(Vector2 p, Vector2 n, Seg kind, StadiumSide side) => list.Add(new Station(p, n, DepthAt(n), kind, side));
        void Blocks(StadiumSide side, Vector2 start, Vector2 dir, Vector2 n, float from, float len)
        {
            int k = Math.Max(1, (int)MathF.Round((len + s.AisleWidth) / (s.BlockLength + s.AisleWidth)));
            float block = (len - (k - 1) * s.AisleWidth) / k, at = from;
            for (int b = 0; b < k; b++)
            {
                Add(start + dir * at, n, Seg.Seat, side);
                at += block;
                if (b < k - 1)
                {
                    Add(start + dir * at, n, Seg.Aisle, side);
                    at += s.AisleWidth;
                }
            }
        }
        void Side(StadiumSide side, Vector2 start, Vector2 dir, Vector2 n, float len)
        {
            if (Array.IndexOf(s.Gates, side) >= 0 && len > s.GateWidth + s.BlockLength)
            {
                float half = (len - s.GateWidth) / 2;
                Blocks(side, start, dir, n, 0, half);
                Add(start + dir * half, n, Seg.Gate, side);
                Blocks(side, start, dir, n, half + s.GateWidth, half);
            }
            else Blocks(side, start, dir, n, 0, len);
        }
        void Corner(StadiumSide side, Vector2 c, float a0)
        {
            for (int i = 0; i < CornerSegments; i++)
            {
                float a = Mathf.DegToRad(a0 + 90f * i / CornerSegments);
                var n = new Vector2(MathF.Cos(a), MathF.Sin(a));
                Add(c + n * rc, n, Seg.Corner, side);
            }
        }

        // u east, v south: the normal (cos a, sin a) turns clockwise seen from above as a grows
        Side(StadiumSide.North, new(-hu + rc, -hv), new(1, 0), new(0, -1), 2 * (hu - rc));
        Corner(StadiumSide.North, new(hu - rc, -hv + rc), -90f);
        Side(StadiumSide.East, new(hu, -hv + rc), new(0, 1), new(1, 0), 2 * (hv - rc));
        Corner(StadiumSide.East, new(hu - rc, hv - rc), 0f);
        Side(StadiumSide.South, new(hu - rc, hv), new(-1, 0), new(0, 1), 2 * (hu - rc));
        Corner(StadiumSide.South, new(-hu + rc, hv - rc), 90f);
        Side(StadiumSide.West, new(-hu, hv - rc), new(0, -1), new(-1, 0), 2 * (hv - rc));
        Corner(StadiumSide.West, new(-hu + rc, -hv + rc), 180f);
        return list;
    }

    // ---- the sweep ----------------------------------------------------------------------------

    /// <summary>Sweeps one stadium into <paramref name="g"/>; returns the seats it counted.</summary>
    /// <param name="outside">The ground's height over the pitch at a point of the frame, or null for flat ground.</param>
    private static int Sweep(StadiumSpec s, Geo g, Func<Vector2, float, Vector3> at, Func<Vector2, float>? outside = null)
    {
        var st = Stations(s);
        var c = new Palette(s);
        float capacity = 0;
        for (int i = 0; i < st.Count; i++)
        {
            var a = st[i];
            var b = st[(i + 1) % st.Count];
            var q = new SegmentQuads(g, at, a, b);
            // a gate's headroom counts from the ground at its mouth: the street round a stadium
            // often stands above the pitch, and the tunnel runs down under the stand to it
            float gateTop = s.GateHeight;
            if (a.Kind == Seg.Gate && outside != null)
                gateTop += Math.Max(0f, Math.Max(outside(a.P + a.N * a.Depth), outside(b.P + b.N * b.Depth)));
            capacity += Stand(s, q, a.Kind, c, gateTop);
            Roof(s, q, a.Kind, c);
            Facade(s, q, a.Kind, c, gateTop);
        }
        Pillars(s, g, at, st, c);
        return (int)capacity;
    }

    /// <summary>The tiers of one segment, the back wall over them; returns the seats it holds.</summary>
    private static float Stand(StadiumSpec s, SegmentQuads q, Seg kind, Palette c, float gateTop)
    {
        bool gate = kind == Seg.Gate, aisle = kind == Seg.Aisle;
        var tread = aisle ? c.Concrete : c.Seat;
        var riser = aisle ? c.ConcreteDark : c.SeatAlt;
        float seats = 0, d = 0, h = 0;
        bool tunnelClosed = !gate;
        for (int t = 0; t < s.Tiers.Length; t++)
        {
            var tier = s.Tiers[t];
            if (tier.Gap > 0)
            {
                if (!gate || h >= gateTop) q.Flat(d, d + tier.Gap, h, c.Concrete, solid: true);
                d += tier.Gap;
            }
            float top = h + tier.Step;
            float wallFrom = t == 0 ? -Skirt : h;
            if (gate) wallFrom = Math.Max(wallFrom, gateTop);
            if (wallFrom < top) q.Wall(d, wallFrom, top, t == 0 ? c.Concrete : c.Glazing, solid: true);
            h = top;

            float firstD = float.NaN, firstH = 0;
            for (int r = 0; r < tier.Rows; r++)
            {
                float hr = h + r * tier.Riser, d0 = d + r * tier.RowDepth, d1 = d0 + tier.RowDepth;
                if (gate && hr < gateTop + GateClearance) continue;
                if (float.IsNaN(firstD))
                {
                    firstD = d0;
                    firstH = hr;
                    // the tunnel: its ceiling from the pitch to the first row left standing, a
                    // lintel over its mouth, and that row's front down to the ceiling
                    if (!tunnelClosed)
                    {
                        q.Flat(0, d0, gateTop, c.ConcreteDark, solid: true);
                        q.Wall(0, gateTop, gateTop + GateClearance, c.Concrete, solid: true);
                        q.Wall(d0, gateTop, hr, c.Concrete, solid: true);
                        tunnelClosed = true;
                    }
                }
                q.Flat(d0, d1, hr, (r & 1) == 0 ? tread : tread.Darkened(0.04f), solid: false);
                if (r < tier.Rows - 1) q.Wall(d1, hr, hr + tier.Riser, riser, solid: false);
                if (!aisle) seats += q.LengthAt(d0 + tier.RowDepth / 2) / s.SeatWidth;
            }
            float endD = d + tier.Rows * tier.RowDepth, endH = h + (tier.Rows - 1) * tier.Riser;
            if (!float.IsNaN(firstD))
            {
                q.Ramp(firstD, firstH, endD, endH);
                // the tier's concrete underside, seen from the concourse: a riser under the ramp,
                // so under every step
                float soffit = tier.Riser + 0.2f;
                q.Quad(q.Pt(true, firstD, firstH - soffit), q.Pt(false, firstD, firstH - soffit),
                    q.Pt(false, endD, endH - soffit), q.Pt(true, endD, endH - soffit), c.ConcreteDark, solid: false);
            }
            d = endD;
            h = endH;
        }
        // the back of the top row, up to the roof's underside
        float under = RoofTop(s, d, q.A.Depth) - s.RoofThickness, underB = RoofTop(s, d, q.B.Depth) - s.RoofThickness;
        q.Quad(q.Pt(true, d, h), q.Pt(false, d, h), q.Pt(false, d, underB), q.Pt(true, d, under), c.Concrete, solid: true);
        return seats;
    }

    /// <summary>The roof's top at <paramref name="d"/> out from the seating front, where the facade is <paramref name="depth"/> out.</summary>
    private static float RoofTop(StadiumSpec s, float d, float depth) =>
        s.RoofInner + (s.RoofOuter - s.RoofInner) * Math.Clamp((d - s.RoofInset) / Math.Max(1f, depth - s.RoofInset), 0f, 1f);

    /// <summary>The roof over one segment: the translucent ring, the sheet, its underside, the inner fascia, panels.</summary>
    private static void Roof(StadiumSpec s, SegmentQuads q, Seg kind, Palette c)
    {
        float din = s.RoofInset, ring = din + s.RoofRing, th = s.RoofThickness;
        var (A, B) = (q.A, q.B);
        Vector3 Top(bool a, float d) => q.Pt(a, d, RoofTop(s, d, a ? A.Depth : B.Depth));
        Vector3 Under(bool a, float d) => q.Pt(a, d, RoofTop(s, d, a ? A.Depth : B.Depth) - th);
        Vector3 Edge(bool a) => Top(a, a ? A.Depth : B.Depth);
        Vector3 EdgeUnder(bool a) => Under(a, a ? A.Depth : B.Depth);

        if (s.RoofRing > 0)
        {
            q.Quad(Top(true, din), Top(false, din), Top(false, ring), Top(true, ring), c.RoofRing, solid: true);
            q.Quad(Under(true, din), Under(false, din), Under(false, ring), Under(true, ring), c.RoofRingUnder, solid: false);
        }
        q.Quad(Top(true, ring), Top(false, ring), Edge(false), Edge(true), c.Roof, solid: true);
        q.Quad(Under(true, ring), Under(false, ring), EdgeUnder(false), EdgeUnder(true), c.RoofUnder, solid: false);
        q.Quad(Under(true, din), Under(false, din), Top(false, din), Top(true, din), c.Steel, solid: true);

        if (s.Solar && kind != Seg.Corner)
        {
            const float Lift = 0.08f;
            float p0 = ring + 2f;
            Vector3 Panel(bool a, float d) => Top(a, d) + Vector3.Up * Lift;
            float endA = A.Depth - 3f, endB = B.Depth - 3f;
            if (endA > p0 && endB > p0)
                q.Quad(Panel(true, p0), Panel(false, p0), q.Pt(false, endB, RoofTop(s, endB, B.Depth) + Lift),
                    q.Pt(true, endA, RoofTop(s, endA, A.Depth) + Lift), c.Solar, solid: false);
        }
    }

    /// <summary>The facade over one segment, from below ground to the roof's edge; a gate's passage left open.</summary>
    private static void Facade(StadiumSpec s, SegmentQuads q, Seg kind, Palette c, float gateTop)
    {
        float da = q.A.Depth, db = q.B.Depth, plinth = kind == Seg.Gate ? gateTop : s.GateHeight;
        if (kind != Seg.Gate)
            q.Quad(q.Pt(true, da, -Skirt), q.Pt(false, db, -Skirt), q.Pt(false, db, plinth), q.Pt(true, da, plinth), c.Plinth, solid: true);
        else
        {
            // the lintel over the passage
            q.Quad(q.Pt(true, da, plinth - 0.6f), q.Pt(false, db, plinth - 0.6f), q.Pt(false, db, plinth), q.Pt(true, da, plinth), c.Steel, solid: true);
        }
        q.Quad(q.Pt(true, da, plinth), q.Pt(false, db, plinth), q.Pt(false, db, s.RoofOuter), q.Pt(true, da, s.RoofOuter), c.Facade, solid: true);
    }

    /// <summary>
    /// The pillars, evenly spaced round the facade, each rising through the roof's edge and carrying
    /// a truss under the roof out to the inner edge, a strut under it, and a tie down onto the roof.
    /// </summary>
    private static void Pillars(StadiumSpec s, Geo g, Func<Vector2, float, Vector3> at, List<Station> st, Palette c)
    {
        if (s.Columns <= 0 || !g.Mesh && !g.Collision) return;
        int n = st.Count;
        var cum = new float[n + 1];
        Vector2 F(int i) => st[i % n].P + st[i % n].N * st[i % n].Depth;
        for (int i = 0; i < n; i++) cum[i + 1] = cum[i] + F(i).DistanceTo(F(i + 1));
        float total = cum[n];
        int seg = 0;
        for (int k = 0; k < s.Columns; k++)
        {
            float want = (k + 0.5f) * total / s.Columns;
            while (seg < n - 1 && cum[seg + 1] < want) seg++;
            float t = (want - cum[seg]) / Math.Max(1e-4f, cum[seg + 1] - cum[seg]);
            var A = st[seg];
            var B = st[(seg + 1) % n];
            var p = A.P.Lerp(B.P, t);
            var nrm = A.N.Lerp(B.N, t).Normalized();
            float depth = Mathf.Lerp(A.Depth, B.Depth, t);
            var tangent = new Vector2(-nrm.Y, nrm.X);

            Vector3 P(float d, float y) => at(p + nrm * d, y);
            var side = (at(p + tangent, 0) - at(p, 0)).Normalized();

            // the pillar, just outside the facade, from below ground to the top
            float off = depth + s.ColumnSize.Y / 2 + 0.1f;
            g.Beam(P(off, -1f), P(off, s.ColumnTop), side, s.ColumnSize.X, s.ColumnSize.Y, c.Steel, solid: true);
            if (!g.Mesh) continue;
            // the truss under the roof, the strut that carries it, the tie onto the roof
            float th = s.RoofThickness, inner = s.RoofInset + 0.5f, mid = (s.RoofInset + depth) / 2;
            float underOut = s.RoofOuter - th - 0.7f, underIn = RoofTop(s, inner, depth) - th - 0.7f;
            g.Beam(P(depth, underOut), P(inner, underIn), side, 0.5f, 1.2f, c.Steel, solid: false);
            g.Beam(P(depth, s.RoofOuter - 12f), P(mid, RoofTop(s, mid, depth) - th - 0.7f), side, 0.35f, 0.35f, c.Steel, solid: false);
            float tieAt = s.RoofInset + (depth - s.RoofInset) * 0.45f;
            g.Beam(P(off, s.ColumnTop - 0.5f), P(tieAt, RoofTop(s, tieAt, depth)), side, 0.25f, 0.25f, c.Steel, solid: false);
        }
    }

    // ---- geometry ---------------------------------------------------------------------------------

    /// <summary>The colours of one stadium, raw linear as the prop shader takes them, no glow.</summary>
    private sealed class Palette(StadiumSpec s)
    {
        private static Color L(Color x, float shade = 1f) => new Color(x.R * shade, x.G * shade, x.B * shade).SrgbToLinear() with { A = 0f };
        public readonly Color Seat = L(s.Seat), SeatAlt = L(s.SeatAlt), Concrete = L(s.Concrete), ConcreteDark = L(s.Concrete, 0.8f),
            Glazing = L(s.Glazing), Roof = L(s.Roof), RoofUnder = L(s.Roof, 0.78f), RoofRing = L(s.RoofRingColor),
            RoofRingUnder = L(s.RoofRingColor, 1.08f), Steel = L(s.Steel), Facade = L(s.Facade), Plinth = L(s.Plinth), Solar = L(s.SolarColor);
    }

    /// <summary>Quads between two stations, by distance out from the seating front and height.</summary>
    private readonly struct SegmentQuads(Geo g, Func<Vector2, float, Vector3> at, Station a, Station b)
    {
        public Station A => a;
        public Station B => b;

        public Vector3 Pt(bool atA, float d, float y) => atA ? at(a.P + a.N * d, y) : at(b.P + b.N * d, y);

        public void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Color col, bool solid) => g.Quad(p0, p1, p2, p3, col, solid);

        /// <summary>A level strip from <paramref name="d0"/> out to <paramref name="d1"/>.</summary>
        public void Flat(float d0, float d1, float y, Color col, bool solid) =>
            g.Quad(Pt(true, d0, y), Pt(false, d0, y), Pt(false, d1, y), Pt(true, d1, y), col, solid);

        /// <summary>An upright strip at <paramref name="d"/> out.</summary>
        public void Wall(float d, float y0, float y1, Color col, bool solid) =>
            g.Quad(Pt(true, d, y0), Pt(false, d, y0), Pt(false, d, y1), Pt(true, d, y1), col, solid);

        /// <summary>The collision of a tier: one slope on its nosings.</summary>
        public void Ramp(float d0, float y0, float d1, float y1) =>
            g.Solid(Pt(true, d0, y0), Pt(false, d0, y0), Pt(false, d1, y1), Pt(true, d1, y1));

        /// <summary>The segment's length <paramref name="d"/> out from the seating front: longer round a corner.</summary>
        public float LengthAt(float d) => (a.P + a.N * d).DistanceTo(b.P + b.N * d);
    }

    /// <summary>The mesh lists and collision triangles being built.</summary>
    private sealed class Geo(bool mesh, bool collision)
    {
        public readonly List<Vector3> Vertices = new();
        public readonly List<Color> Colors = new();
        public readonly List<int> Indices = new();
        public readonly List<Vector3> Faces = new();

        public bool Mesh => mesh;
        public bool Collision => collision;

        public PierMeshBuilder.MeshData? Data() =>
            mesh && Vertices.Count > 0 ? new PierMeshBuilder.MeshData(Vertices.ToArray(), Colors.ToArray(), Indices.ToArray()) : null;

        /// <summary>A quad, shaded by the way it faces (the prop shader draws both sides), solid if asked.</summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col, bool solid)
        {
            if (mesh)
            {
                var n = (c - a).Cross(b - d);
                n = n.LengthSquared() > 1e-10f ? n.Normalized() : Vector3.Up;
                float shade = 0.72f + 0.28f * Math.Abs(n.Y) + 0.06f * n.X;
                var shaded = new Color(col.R * shade, col.G * shade, col.B * shade, col.A);
                int i = Vertices.Count;
                Vertices.Add(a); Vertices.Add(b); Vertices.Add(c); Vertices.Add(d);
                for (int k = 0; k < 4; k++) Colors.Add(shaded);
                Indices.Add(i); Indices.Add(i + 1); Indices.Add(i + 2);
                Indices.Add(i); Indices.Add(i + 2); Indices.Add(i + 3);
            }
            if (solid) Solid(a, b, c, d);
        }

        /// <summary>A quad that is collision only.</summary>
        public void Solid(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            if (!collision) return;
            Faces.Add(a); Faces.Add(b); Faces.Add(c);
            Faces.Add(a); Faces.Add(c); Faces.Add(d);
        }

        /// <summary>A square-section member from <paramref name="a"/> to <paramref name="b"/>, <paramref name="w"/> wide along <paramref name="side"/>.</summary>
        public void Beam(Vector3 a, Vector3 b, Vector3 side, float w, float h, Color col, bool solid)
        {
            var axis = (b - a).Normalized();
            var s = (side - axis * side.Dot(axis)).Normalized() * (w / 2);
            var up = axis.Cross(s).Normalized() * (h / 2);
            Vector3 A(int i) => a + (i is 0 or 3 ? -s : s) + (i < 2 ? -up : up);
            Vector3 B(int i) => b + (i is 0 or 3 ? -s : s) + (i < 2 ? -up : up);
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 1) % 4;
                Quad(A(i), A(j), B(j), B(i), col, solid);
            }
            Quad(B(0), B(1), B(2), B(3), col, solid);
        }
    }
}
