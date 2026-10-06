using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// The piers and jetties of a tile (#377), from the region's <see cref="LandingIndex"/>: a timber
/// deck on a slab, piles down to the lake bed, rails along a pier's neck, bollards at a berth. Drawn
/// with the <see cref="Styles.MaterialRole.Prop"/> material (vertex colours, every style), as one
/// more surface of the tile's roads mesh; the collision faces join the tile's road cells
/// (<c>docs/notes/terrain/perf-collision-commits.md</c>), so they are walked on like a bridge deck
/// and stop a boat. Tile-local coordinates: x = E − MinE, y = height, z = MaxN − N. Each ribbon is
/// built by the one tile its middle lies in (<see cref="LandingIndex.RibbonsOf"/>), a bollard by the
/// tile it stands in. Any thread.
/// </summary>
public static class PierMeshBuilder
{
    public sealed record MeshData(Vector3[] Vertices, Color[] Colors, int[] Indices);

    /// <summary>Slab under a deck, m: a pier's concrete, a jetty's timber frame.</summary>
    public const float PierThickness = 0.45f, JettyThickness = 0.25f;
    /// <summary>A rail's height over the deck, the walls the walk meets there.</summary>
    public const float RailHeight = 1.05f;

    // vertex colours, raw linear (the prop shader takes them as they are); alpha 0 = no glow
    private static readonly Color PierDeck = new(0.40f, 0.28f, 0.17f, 0f);
    private static readonly Color PierSide = new(0.56f, 0.56f, 0.54f, 0f);
    private static readonly Color JettyDeck = new(0.44f, 0.36f, 0.27f, 0f);
    private static readonly Color JettySide = new(0.33f, 0.27f, 0.21f, 0f);
    private static readonly Color Pile = new(0.27f, 0.25f, 0.23f, 0f);
    private static readonly Color Rail = new(0.82f, 0.83f, 0.81f, 0f);
    private static readonly Color Iron = new(0.1f, 0.1f, 0.11f, 0f);
    private static readonly Color Edge = new(0.78f, 0.66f, 0.18f, 0f);

    /// <summary>
    /// The tile's piers: their mesh (null when not asked for, or none) and their collision faces
    /// (triangles, tile-local; empty when not asked for, or none). Null when the tile has none.
    /// </summary>
    /// <param name="grid">The tile's heights, for the piles' feet on the bed (any stride); null: piles 4 m long.</param>
    public static (MeshData? Mesh, Vector3[] Faces)? Build(LandingIndex index, TileId id, ChunkGrid? grid, bool mesh, bool collision)
    {
        Core.ShowcaseTrace.Mark();
        if (!mesh && !collision) return null;
        var g = new Geo(id, grid, mesh, collision);
        foreach (var r in index.RibbonsOf(id)) g.Ribbon(r);
        foreach (var b in index.BollardsOf(id)) g.Bollard(b);
        if (g.Empty) return null;
        return (mesh && g.Vertices.Count > 0 ? new MeshData(g.Vertices.ToArray(), g.Colors.ToArray(), g.Indices.ToArray()) : null,
            g.Faces.ToArray());
    }

    /// <summary>The geometry being built: mesh lists and collision triangles.</summary>
    private sealed class Geo
    {
        public readonly List<Vector3> Vertices = new();
        public readonly List<Color> Colors = new();
        public readonly List<int> Indices = new();
        public readonly List<Vector3> Faces = new();
        private readonly TileId _id;
        private readonly ChunkGrid? _grid;
        private readonly bool _mesh, _collide;

        public Geo(TileId id, ChunkGrid? grid, bool mesh, bool collide) => (_id, _grid, _mesh, _collide) = (id, grid, mesh, collide);

        public bool Empty => Vertices.Count == 0 && Faces.Count == 0;

        private Vector3 Local(double e, double n, double h) => new((float)(e - _id.MinE), (float)h, (float)(_id.MaxN - n));

        /// <summary>The bed (or ground) under a tile-local point; NaN off the tile or with no grid.</summary>
        private float Bed(Vector3 p)
        {
            if (_grid == null || p.X < 0 || p.Z < 0 || p.X > ChunkFormat.TileSizeM || p.Z > ChunkFormat.TileSizeM) return float.NaN;
            return (float)_grid.SampleHeight(_id.MinE + p.X, _id.MaxN - p.Z);
        }

        /// <summary>A quad a-b-c-d (around its edge): drawn, and solid when <paramref name="solid"/>.</summary>
        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color, bool solid = true, bool draw = true)
        {
            if (_mesh && draw)
            {
                int i = Vertices.Count;
                Vertices.Add(a); Vertices.Add(b); Vertices.Add(c); Vertices.Add(d);
                for (int k = 0; k < 4; k++) Colors.Add(color);
                Indices.Add(i); Indices.Add(i + 1); Indices.Add(i + 2);
                Indices.Add(i); Indices.Add(i + 2); Indices.Add(i + 3);
            }
            if (_collide && solid)
            {
                Faces.Add(a); Faces.Add(b); Faces.Add(c);
                Faces.Add(a); Faces.Add(c); Faces.Add(d);
            }
        }

        private void Tri(Vector3 a, Vector3 b, Vector3 c, Color color)
        {
            if (_mesh)
            {
                int i = Vertices.Count;
                Vertices.Add(a); Vertices.Add(b); Vertices.Add(c);
                for (int k = 0; k < 3; k++) Colors.Add(color);
                Indices.Add(i); Indices.Add(i + 1); Indices.Add(i + 2);
            }
            if (_collide) { Faces.Add(a); Faces.Add(b); Faces.Add(c); }
        }

        /// <summary>An upright box: centre of its foot, its size, its long axis along <paramref name="along"/> (level).</summary>
        private void Box(Vector3 foot, Vector3 along, float length, float width, float height, Color color, bool solid)
        {
            var a = along * (length * 0.5f);
            var s = new Vector3(along.Z, 0, -along.X) * (width * 0.5f);
            var up = Vector3.Up * height;
            Vector3 p0 = foot - a - s, p1 = foot + a - s, p2 = foot + a + s, p3 = foot - a + s;
            Quad(p0, p1, p1 + up, p0 + up, color, solid);
            Quad(p1, p2, p2 + up, p1 + up, color, solid);
            Quad(p2, p3, p3 + up, p2 + up, color, solid);
            Quad(p3, p0, p0 + up, p3 + up, color, solid);
            Quad(p0 + up, p1 + up, p2 + up, p3 + up, color, solid);
        }

        public void Ribbon(PierRibbon r)
        {
            int n = r.Points.Count;
            if (n < 2) return;
            bool pier = r.Kind == PierKind.Pier;
            float half = (float)r.Width * 0.5f;
            float thick = pier ? PierThickness : JettyThickness;
            Color deck = pier ? PierDeck : JettyDeck, side = pier ? PierSide : JettySide;

            var p = new Vector3[n];
            for (int i = 0; i < n; i++) p[i] = Local(r.Points[i][0], r.Points[i][1], r.Points[i][2]);
            // left of each stretch, level; mitred at the bends
            var dir = new Vector3[n - 1];
            for (int i = 0; i + 1 < n; i++)
            {
                var d = (p[i + 1] - p[i]) with { Y = 0 };
                dir[i] = d.LengthSquared() > 1e-8f ? d.Normalized() : (i > 0 ? dir[i - 1] : Vector3.Forward);
            }
            var off = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var l0 = Left(dir[Math.Max(0, i - 1)]);
                var l1 = Left(dir[Math.Min(n - 2, i)]);
                var m = (l0 + l1).Normalized();
                off[i] = m * (half / Mathf.Max(0.35f, m.Dot(l1)));
            }

            var down = Vector3.Down * thick;
            for (int i = 0; i + 1 < n; i++)
            {
                Vector3 la = p[i] + off[i], ra = p[i] - off[i], lb = p[i + 1] + off[i + 1], rb = p[i + 1] - off[i + 1];
                // the deck: planks across in ~0.9 m strips, every other one a shade darker (drawn); one quad (solid)
                float len = (p[i + 1] - p[i]).Length();
                int strips = Math.Max(1, Mathf.RoundToInt(len / 0.9f));
                for (int k = 0; k < strips; k++)
                {
                    float t0 = (float)k / strips, t1 = (float)(k + 1) / strips;
                    var shade = (k & 1) == 0 ? deck : deck.Darkened(0.12f);
                    Quad(la.Lerp(lb, t0), la.Lerp(lb, t1), ra.Lerp(rb, t1), ra.Lerp(rb, t0), shade with { A = 0 }, solid: false);
                }
                Quad(la, lb, rb, ra, deck, draw: false);
                // the slab's sides and underside
                Quad(la, la + down, lb + down, lb, side);
                Quad(rb, rb + down, ra + down, ra, side);
                Quad(la + down, ra + down, rb + down, lb + down, side);
                if (pier && r.Rails)
                {
                    Rails(la, lb);
                    Rails(rb, ra);
                }
            }
            // the ends
            Quad(p[0] + off[0], p[0] - off[0], p[0] - off[0] + down, p[0] + off[0] + down, side);
            Quad(p[n - 1] - off[n - 1], p[n - 1] + off[n - 1], p[n - 1] + off[n - 1] + down, p[n - 1] - off[n - 1] + down, side);
            // a berth's head: a yellow edge along its long sides (where boats lie), drawn only
            if (pier && !r.Rails)
                for (int i = 0; i + 1 < n; i++)
                    foreach (int s in new[] { 1, -1 })
                    {
                        var e0 = p[i] + off[i] * s + Vector3.Up * 0.005f;
                        var e1 = p[i + 1] + off[i + 1] * s + Vector3.Up * 0.005f;
                        var inward = -off[i].Normalized() * s * 0.15f;
                        Quad(e0, e1, e1 + inward, e0 + inward, Edge, solid: false);
                    }

            Piles(p, off, dir, thick, pier);
        }

        private static Vector3 Left(Vector3 d) => new(d.Z, 0, -d.X);

        /// <summary>A rail along a deck edge from a to b: posts, a handrail and a mid-rail drawn, a wall to the walk.</summary>
        private void Rails(Vector3 a, Vector3 b)
        {
            var d = (b - a) with { Y = 0 };
            float len = d.Length();
            if (len < 0.05f) return;
            var along = d / len;
            // the wall the walker meets (not drawn)
            Quad(a, b, b + Vector3.Up * RailHeight, a + Vector3.Up * RailHeight, Rail, draw: false);
            if (!_mesh) return;
            int posts = Math.Max(1, Mathf.RoundToInt(len / 2f));
            for (int k = 0; k <= posts; k++)
            {
                var foot = a.Lerp(b, (float)k / posts);
                Box(foot, along, 0.07f, 0.07f, RailHeight, Rail, solid: false);
            }
            foreach (float y in new[] { RailHeight - 0.05f, RailHeight * 0.5f })
            {
                var lift = Vector3.Up * y;
                var s = new Vector3(along.Z, 0, -along.X) * 0.03f;
                Quad(a + lift - s, b + lift - s, b + lift + s, a + lift + s, Rail, solid: false);
                Quad(a + lift - s, b + lift - s, b + lift - s + Vector3.Up * 0.05f, a + lift - s + Vector3.Up * 0.05f, Rail, solid: false);
                Quad(b + lift + s, a + lift + s, a + lift + s + Vector3.Up * 0.05f, b + lift + s + Vector3.Up * 0.05f, Rail, solid: false);
            }
        }

        /// <summary>Piles under both edges to the bed, every few metres, where the deck stands over water or low ground.</summary>
        private void Piles(Vector3[] p, Vector3[] off, Vector3[] dir, float thick, bool pier)
        {
            float spacing = pier ? 4.5f : 4f, size = pier ? 0.4f : 0.22f;
            var cum = new float[p.Length];
            for (int i = 1; i < p.Length; i++) cum[i] = cum[i - 1] + ((p[i] - p[i - 1]) with { Y = 0 }).Length();
            float total = cum[^1];
            if (total < 0.5f) return;
            int count = Math.Max(1, Mathf.RoundToInt((total - 1f) / spacing));
            int seg = 0;
            for (int k = 0; k <= count; k++)
            {
                float s = 0.5f + (total - 1f) * k / count;
                while (seg < p.Length - 2 && cum[seg + 1] < s) seg++;
                float len = cum[seg + 1] - cum[seg];
                float t = len > 1e-4f ? (s - cum[seg]) / len : 0f;
                var c = p[seg].Lerp(p[seg + 1], t);
                var o = off[seg].Lerp(off[seg + 1], t);
                float inset = 1f - 0.35f / Mathf.Max(o.Length(), 0.5f);
                foreach (float side in new[] { 1f, -1f })
                {
                    var top = c + o * (side * inset) + Vector3.Down * thick;
                    float bed = Bed(top);
                    float foot = float.IsNaN(bed) ? top.Y - 4f : bed - 0.3f;
                    if (top.Y - foot < 0.4f) continue;   // ashore: the slab lies on the ground
                    Box(top with { Y = foot }, dir[seg], size, size, top.Y - foot, Pile, solid: true);
                }
            }
        }

        /// <summary>A bollard: an iron post with a cap, solid.</summary>
        public void Bollard(double[] b)
        {
            var foot = Local(b[0], b[1], b[2]);
            const int sides = 8;
            const float r = 0.16f, h = 0.45f, cap = 0.21f;
            for (int k = 0; k < sides; k++)
            {
                float a0 = Mathf.Tau * k / sides, a1 = Mathf.Tau * (k + 1) / sides;
                var d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0));
                var d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1));
                Quad(foot + d0 * r, foot + d1 * r, foot + d1 * r + Vector3.Up * h, foot + d0 * r + Vector3.Up * h, Iron);
                var top = foot + Vector3.Up * h;
                Quad(top + d0 * cap, top + d1 * cap, top + d1 * cap + Vector3.Up * 0.07f, top + d0 * cap + Vector3.Up * 0.07f, Iron);
                Tri(top + Vector3.Up * 0.07f, top + d1 * cap + Vector3.Up * 0.07f, top + d0 * cap + Vector3.Up * 0.07f, Iron);
            }
        }
    }
}
