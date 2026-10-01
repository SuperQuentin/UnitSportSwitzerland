using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

// The road blend: plain C# over TerrainFormat, no Godot types, so tools/BlendCheck compiles it.
public static partial class TerrainMeshBuilder
{
    /// <summary>
    /// The visual mesh's blend target sits this far below each road's own drawn surface —
    /// matching <c>RoadExtractor.DrapeOffset</c> (tools/TerrainPreprocessor, a separate
    /// project so the constant can't be shared directly) so the terrain rises to just under
    /// the ribbon instead of erasing the deliberate lift that keeps the two from z-fighting.
    /// Collision uses 0 instead — see <see cref="ComputeRoadBlend"/>.
    /// </summary>
    public const double VisualBlendClearance = 0.35;

    /// <summary>
    /// Every lattice cell an at-grade road's cross-section or slopes constrain, with the band the
    /// ground is clamped into: <c>clamp(ground, Lo - clearance, Hi - clearance)</c>. Under a road
    /// <c>Lo = Hi</c> = the road; on its embankment <c>Lo</c> is the fill slope and <c>Hi</c> the
    /// cut slope, and ground already between them stays where it is. Sparse: a tile's corridors
    /// cover a few percent of it. Computed once per tile and applied twice — to the collision
    /// floor at clearance 0 and to the visual mesh at <see cref="VisualBlendClearance"/> — which
    /// is why the clearance is not baked in.
    /// </summary>
    public sealed record RoadBlend(int[] Cells, float[] Lo, float[] Hi);

    private static float BlendedHeight(float ground, RoadBlend blend, int k, double clearance)
    {
        float lo = (float)(blend.Lo[k] - clearance), hi = (float)(blend.Hi[k] - clearance);
        return ground < lo ? lo : ground > hi ? hi : ground;
    }

    /// <summary>Applies a blend to a full-resolution height map in place.</summary>
    public static void ApplyRoadBlend(float[] map, RoadBlend blend, double clearance)
    {
        for (int k = 0; k < blend.Cells.Length; k++)
        {
            int cell = blend.Cells[k];
            map[cell] = BlendedHeight(map[cell], blend, k, clearance);
        }
    }

    /// <summary>
    /// At most this far a piece is rasterised past each of its ends, so the slopes of two pieces
    /// meeting at a gentle bend overlap instead of leaving an unconstrained wedge on the outside
    /// of it (only as far as the bend needs: most pieces of a smoothed line barely turn). Sharper
    /// bends and the line's two ends get a disc as well.
    /// </summary>
    private const double PieceOverlapM = 1.0;

    /// <summary>
    /// The ground under and beside every at-grade road, path and rail line: the road's embankment
    /// (#125, <see cref="RoadEmbankment"/>). Shared by the collision floor and the visual mesh, so
    /// what a player sees is what they stand on.
    ///
    /// <para>
    /// Cross-section: level at the centreline height out to each side's edge
    /// (<see cref="RoadEmbankment.EdgeOffset"/>); a cell under a road takes the height of the
    /// centreline at its own perpendicular foot, nearest segment wins (stamps a metre apart were
    /// measured leaving 0.1-0.4 m of scatter). Past the edge the ground is clamped between the
    /// fill slope below and the cut slope above, out to <see cref="RoadEmbankment.Reach"/>; ground
    /// already inside that band is left alone, so a road on gentle ground changes nothing beside
    /// it. Each cell keeps the tightest bound any road gives it (an envelope, so the order roads
    /// are drawn in does not matter); a shoulder never reaches under another road's surface.
    /// </para>
    ///
    /// <para>
    /// Retaining walls (<see cref="LinearPropType.RetainingWallFill"/>/<c>Cut</c> props, placed
    /// by the network stage) release the ground past their free line from every slope: beyond a
    /// fill wall it is the valley floor, behind a cut wall the hillside. The wall itself is
    /// <c>RoadWallBuilder</c>'s mesh and collision.
    /// </para>
    ///
    /// <para>
    /// Bridges and tunnels are excluded (<see cref="RoadEmbankment.IsAtGrade"/>): a heightfield
    /// has one height per column, so blending toward a deck would fill the gorge it crosses.
    /// Aerial ropeways, watercourses and walls are not ground surfaces.
    /// </para>
    ///
    /// <para>
    /// Each polyline piece is rasterised as one oriented strip, row by row, so a cell is visited
    /// about once per piece rather than once per 1 m stamp of a disc around it.
    /// </para>
    /// </summary>
    public static RoadBlend ComputeRoadBlend(RoadTile roadTile)
    {
        int n = ChunkFormat.GridSize;
        var floats = System.Buffers.ArrayPool<float>.Shared;
        // dense scratch, pooled (4 MB each); initialised on first touch, so only the mask is cleared
        float[] lo = floats.Rent(n * n);
        float[] hi = floats.Rent(n * n);
        // distance to the centreline of the road whose core holds the cell, or +inf on a slope
        float[] coreDist = floats.Rent(n * n);
        byte[] seen = System.Buffers.ArrayPool<byte>.Shared.Rent(n * n);
        Array.Clear(seen, 0, n * n);
        var touched = new List<int>(16384);
        var scratch = new Scratch(lo, hi, coreDist, seen, touched, n);

        try
        {
            foreach (var seg in roadTile.Segments)
            {
                if (!RoadEmbankment.IsAtGrade(seg) || seg.PointCount < 2) continue;
                var line = new Line(seg);
                for (int i = 0; i < seg.PointCount - 1; i++)
                    line.Piece(scratch, i);
                for (int i = 0; i < seg.PointCount; i++)
                    if (line.NeedsDisc(i)) line.Disc(scratch, i);
            }

            foreach (var wall in roadTile.LinearProps)
                if (wall.Type is LinearPropType.RetainingWallFill or LinearPropType.RetainingWallCut)
                    FreeBehindWall(scratch, wall);

            foreach (var island in roadTile.AreaProps)
                if (island.Type == AreaPropType.Island && island.Height > 0 && island.Vertices.Length >= 9)
                    HoldUnderIsland(scratch, island);

            var cells = new List<int>(touched.Count);
            var los = new List<float>(touched.Count);
            var his = new List<float>(touched.Count);
            foreach (int idx in touched)
            {
                float l = lo[idx], h = hi[idx];
                if (float.IsNegativeInfinity(l) && float.IsPositiveInfinity(h)) continue;   // freed
                // a fill slope from above meets a cut slope from below (two roads stacked on a
                // hillside with no wall between them): split the difference
                if (l > h) l = h = (l + h) * 0.5f;
                cells.Add(idx);
                los.Add(l);
                his.Add(h);
            }
            return new RoadBlend(cells.ToArray(), los.ToArray(), his.ToArray());
        }
        finally
        {
            floats.Return(lo);
            floats.Return(hi);
            floats.Return(coreDist);
            System.Buffers.ArrayPool<byte>.Shared.Return(seen);
        }
    }

    private sealed record Scratch(float[] Lo, float[] Hi, float[] CoreDist, byte[] Seen, List<int> Touched, int N);

    /// <summary>One segment's centreline, in lattice units (1 m), with its two edge offsets.</summary>
    private readonly struct Line
    {
        private readonly float[] _p;
        private readonly int _count;
        private readonly double _left, _right, _reach, _radius;

        public Line(RoadSegment seg)
        {
            _p = seg.Points;
            _count = seg.PointCount;
            _left = RoadEmbankment.EdgeOffset(seg, right: false);
            _right = RoadEmbankment.EdgeOffset(seg, right: true);
            _reach = RoadEmbankment.Reach(seg.Class);
            _radius = Math.Max(_left, _right) + _reach;
        }

        private double X(int i) => _p[i * 3] / ChunkFormat.SpacingM;
        private double Y(int i) => _p[i * 3 + 1];
        private double Z(int i) => _p[i * 3 + 2] / ChunkFormat.SpacingM;

        /// <summary>The strip around piece i (i to i+1): every cell within the radius whose foot is on it.</summary>
        public void Piece(Scratch s, int i)
        {
            double ax = X(i), az = Z(i), bx = X(i + 1), bz = Z(i + 1);
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
            if (len < 1e-6) return;
            // past each end by what the bend there needs: the outside of a turn of θ opens a gap
            // of θ·radius at the slope's reach, which the two pieces close half each
            Strip(s, ax, az, Y(i), Y(i + 1), (bx - ax) / len, (bz - az) / len, len, -Overlap(i), len + Overlap(i + 1));
        }

        /// <summary>Every cell within the radius of vertex i, at that vertex's height: a strip of no length.</summary>
        public void Disc(Scratch s, int i)
        {
            // the side a cell is on, from the direction through the vertex
            int a = Math.Max(0, i - 1), b = Math.Min(_count - 1, i + 1);
            double fx = X(b) - X(a), fz = Z(b) - Z(a), fl = Math.Sqrt(fx * fx + fz * fz);
            if (fl < 1e-6) return;
            double rad = _radius / ChunkFormat.SpacingM;
            Strip(s, X(i), Z(i), Y(i), Y(i), fx / fl, fz / fl, 0, -rad, rad);
        }

        /// <summary>
        /// Every cell within the radius of the piece from (ax, az) along (ux, uz) for len, whose
        /// position along it lies in [a0, a1]: under the road (nearest centreline wins) or on its
        /// slope (tightest bound wins). One loop with the cell logic inline: it runs a few hundred
        /// thousand times a tile, and a Debug build does not inline a call.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        private void Strip(Scratch s, double ax, double az, double ay, double by, double ux, double uz,
            double len, double a0, double a1)
        {
            float[] lo = s.Lo, hi = s.Hi, coreDist = s.CoreDist;
            byte[] seen = s.Seen;
            var touched = s.Touched;
            int n = s.N;
            double left = _left, right = _right, reach = _reach, spacing = ChunkFormat.SpacingM;
            double fill = RoadEmbankment.FillSlope, cut = RoadEmbankment.CutSlope;
            // right of the drawing direction, X east and Z south
            double rx = -uz, rz = ux;
            double rad = _radius / spacing;

            // corners of the strip, for the row range
            double minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var (al, pe) in (ReadOnlySpan<(double, double)>)[(a0, -rad), (a0, rad), (a1, -rad), (a1, rad)])
            {
                double z = az + uz * al + rz * pe;
                minZ = Math.Min(minZ, z);
                maxZ = Math.Max(maxZ, z);
            }
            int r0 = Math.Max(0, (int)Math.Ceiling(minZ)), r1 = Math.Min(n - 1, (int)Math.Floor(maxZ));
            double rad2 = rad * rad;

            for (int r = r0; r <= r1; r++)
            {
                double dz = r - az;
                // along(x) = (x - ax)·ux + dz·uz in [a0, a1]; perp(x) = (x - ax)·rx + dz·rz in [-rad, rad]
                double xMin = double.MinValue, xMax = double.MaxValue;
                if (!ClipRange(ux, dz * uz, a0, a1, ref xMin, ref xMax)) continue;
                if (!ClipRange(rx, dz * rz, -rad, rad, ref xMin, ref xMax)) continue;
                int c0 = Math.Max(0, (int)Math.Ceiling(ax + xMin)), c1 = Math.Min(n - 1, (int)Math.Floor(ax + xMax));
                int row = r * n;
                for (int c = c0; c <= c1; c++)
                {
                    double dx = c - ax;
                    double along = dx * ux + dz * uz, perp = dx * rx + dz * rz;
                    double over = along < 0 ? -along : along > len ? along - len : 0;
                    double d2 = perp * perp + over * over;
                    if (d2 > rad2) continue;   // the round ends
                    double dist = (over > 0 ? Math.Sqrt(d2) : Math.Abs(perp)) * spacing;
                    double fromEdge = dist - (perp >= 0 ? right : left);
                    if (fromEdge > reach) continue;
                    double y = len <= 0 || along <= 0 ? ay : along >= len ? by : ay + (by - ay) * (along / len);
                    int idx = row + c;

                    if (fromEdge <= 0)
                    {
                        // under the road: the centreline's height at this cell's own foot,
                        // nearest segment wins outright
                        if (seen[idx] == 0) { seen[idx] = 1; touched.Add(idx); }
                        else if (dist >= coreDist[idx]) continue;
                        coreDist[idx] = (float)dist;
                        lo[idx] = hi[idx] = (float)y;
                        continue;
                    }
                    float l = (float)(y - fill * fromEdge), h = (float)(y + cut * fromEdge);
                    if (seen[idx] == 0)
                    {
                        seen[idx] = 1;
                        touched.Add(idx);
                        coreDist[idx] = float.PositiveInfinity;
                        lo[idx] = l;
                        hi[idx] = h;
                        continue;
                    }
                    if (coreDist[idx] < float.PositiveInfinity) continue;   // a slope never reaches under a road
                    if (l > lo[idx]) lo[idx] = l;
                    if (h < hi[idx]) hi[idx] = h;
                }
            }
        }

        /// <summary>
        /// Whether vertex i needs a round cap: the line's two ends, and bends sharp enough that the
        /// pieces' overlap leaves a wedge on the outside at the slope's full reach.
        /// </summary>
        public bool NeedsDisc(int i) => i == 0 || i == _count - 1 || Turn(i) * _radius * 0.5 > PieceOverlapM;

        /// <summary>How far the pieces meeting at vertex i run on past it: 0 at the line's ends.</summary>
        private double Overlap(int i) =>
            i == 0 || i == _count - 1 ? 0 : Math.Min(PieceOverlapM, Turn(i) * _radius * 0.5 + 0.05) / ChunkFormat.SpacingM;

        /// <summary>Angle the line turns through at interior vertex i, radians.</summary>
        private double Turn(int i)
        {
            double ux = X(i) - X(i - 1), uz = Z(i) - Z(i - 1), vx = X(i + 1) - X(i), vz = Z(i + 1) - Z(i);
            double lu = Math.Sqrt(ux * ux + uz * uz), lv = Math.Sqrt(vx * vx + vz * vz);
            if (lu < 1e-6 || lv < 1e-6) return 0;
            return Math.Acos(Math.Clamp((ux * vx + uz * vz) / (lu * lv), -1, 1));
        }
    }

    /// <summary>
    /// Shapes the ground at a retaining wall so the heightfield's one-cell transition lies on the
    /// wall's solid side, under its cover, and the face side is clean (<see cref="RoadEmbankment"/>).
    /// Depth is measured from the face into the solid (left of the points).
    /// Fill wall: every cell shallower than <see cref="RoadEmbankment.FreeDepth"/>, the valley floor
    /// past the face included, loses its lower bound (the fill slope, or the road itself in the
    /// strip under the road's edge): the ground falls to the valley floor and the cover holds the
    /// road over it. The last <see cref="RoadEmbankment.FreeDepth"/> of a run keeps its road cells,
    /// so no dip opens under the road past the cover's end.
    /// Cut wall: every slope cell from just in front of the face to that depth is levelled with the
    /// road (the paved gutter, and the shelf under the crown); deeper, the hillside is released.
    /// A road's surface is never raised or lowered by a cut wall.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static void FreeBehindWall(Scratch s, RoadLinearProp wall)
    {
        int count = wall.PointCount;
        if (count < 2) return;
        bool fill = wall.Type == LinearPropType.RetainingWallFill;
        double sp = ChunkFormat.SpacingM;
        double free = RoadEmbankment.FreeDepth / sp;
        double q0 = fill ? -(RoadEmbankment.RoadReachM + 1.0) / sp : -(RoadEmbankment.CutFaceOffset + 0.5) / sp;
        double q1 = fill ? free : (RoadEmbankment.CoverDepth + RoadEmbankment.RoadReachM) / sp;
        int n = s.N;

        double total = 0;
        for (int i = 0; i < count - 1; i++)
            total += Math.Sqrt(Sq(wall.Points[(i + 1) * 4] - wall.Points[i * 4]) + Sq(wall.Points[(i + 1) * 4 + 2] - wall.Points[i * 4 + 2])) / sp;

        double start = 0;
        for (int i = 0; i < count - 1; i++)
        {
            double ax = wall.Points[i * 4] / sp, az = wall.Points[i * 4 + 2] / sp;
            double bx = wall.Points[(i + 1) * 4] / sp, bz = wall.Points[(i + 1) * 4 + 2] / sp;
            float fa = wall.Points[i * 4 + 1], fb = wall.Points[(i + 1) * 4 + 1];
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
            if (len < 1e-6) continue;
            double ux = (bx - ax) / len, uz = (bz - az) / len;
            double lx = uz, lz = -ux;   // left, X east and Z south
            double a0 = i == 0 ? 0 : -PieceOverlapM / sp, a1 = i + 1 == count - 1 ? len : len + PieceOverlapM / sp;

            double minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var (al, q) in (ReadOnlySpan<(double, double)>)[(a0, q0), (a0, q1), (a1, q0), (a1, q1)])
            {
                double z = az + uz * al + lz * q;
                minZ = Math.Min(minZ, z);
                maxZ = Math.Max(maxZ, z);
            }
            int r0 = Math.Max(0, (int)Math.Ceiling(minZ)), r1 = Math.Min(n - 1, (int)Math.Floor(maxZ));
            for (int r = r0; r <= r1; r++)
            {
                double dz = r - az;
                double xMin = double.MinValue, xMax = double.MaxValue;
                if (!ClipRange(ux, dz * uz, a0, a1, ref xMin, ref xMax)) continue;
                if (!ClipRange(lx, dz * lz, q0, q1, ref xMin, ref xMax)) continue;
                int c0 = Math.Max(0, (int)Math.Ceiling(ax + xMin)), c1 = Math.Min(n - 1, (int)Math.Floor(ax + xMax));
                int row = r * n;
                for (int c = c0; c <= c1; c++)
                {
                    int idx = row + c;
                    if (s.Seen[idx] == 0) continue;
                    bool core = s.CoreDist[idx] < float.PositiveInfinity;
                    double dx = c - ax, along = dx * ux + dz * uz, q = dx * lx + dz * lz;
                    if (fill)
                    {
                        if (q >= free) continue;
                        if (core)
                        {
                            // only this road's own cells inside the face: the planner keeps every
                            // other line out from under the cover
                            if (q < 0) continue;
                            double at = start + along;
                            if (at < free || at > total - free) continue;
                        }
                        s.Lo[idx] = float.NegativeInfinity;
                    }
                    else if (!core)
                    {
                        if (q < free)
                        {
                            float foot = (float)(fa + (fb - fa) * Math.Clamp(along / len, 0, 1));
                            s.Lo[idx] = s.Hi[idx] = foot;
                        }
                        else
                        {
                            s.Lo[idx] = float.NegativeInfinity;
                            s.Hi[idx] = float.PositiveInfinity;
                        }
                    }
                }
            }
            start += len;
        }
    }

    private static double Sq(double v) => v * v;

    /// <summary>How far under a raised island's top the ground is held.</summary>
    private const float IslandClearance = 0.05f;

    /// <summary>
    /// Holds the ground under a raised roundabout island (#122) below its top, so a mound in the
    /// terrain does not poke through the island's surface: each cell inside one of its triangles
    /// may lie no higher than the top there less <see cref="IslandClearance"/>. A road's own cell
    /// is never touched; the island's kerb and top are <c>IslandBuilder</c>'s mesh and collision.
    /// </summary>
    private static void HoldUnderIsland(Scratch s, RoadAreaProp island)
    {
        var v = island.Vertices;
        var ix = island.Indices;
        double sp = ChunkFormat.SpacingM;
        int n = s.N;
        for (int t = 0; t + 2 < ix.Length; t += 3)
        {
            int a = ix[t] * 3, b = ix[t + 1] * 3, c = ix[t + 2] * 3;
            double ax = v[a] / sp, az = v[a + 2] / sp, bx = v[b] / sp, bz = v[b + 2] / sp, cx = v[c] / sp, cz = v[c + 2] / sp;
            double det = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            if (Math.Abs(det) < 1e-12) continue;
            int c0 = Math.Max(0, (int)Math.Ceiling(Math.Min(ax, Math.Min(bx, cx)))), c1 = Math.Min(n - 1, (int)Math.Floor(Math.Max(ax, Math.Max(bx, cx))));
            int r0 = Math.Max(0, (int)Math.Ceiling(Math.Min(az, Math.Min(bz, cz)))), r1 = Math.Min(n - 1, (int)Math.Floor(Math.Max(az, Math.Max(bz, cz))));
            for (int r = r0; r <= r1; r++)
                for (int col = c0; col <= c1; col++)
                {
                    double wa = ((bz - cz) * (col - cx) + (cx - bx) * (r - cz)) / det;
                    double wb = ((cz - az) * (col - cx) + (ax - cx) * (r - cz)) / det;
                    double wc = 1 - wa - wb;
                    if (wa < -1e-9 || wb < -1e-9 || wc < -1e-9) continue;
                    float top = (float)(wa * v[a + 1] + wb * v[b + 1] + wc * v[c + 1]) + island.Height - IslandClearance;
                    int idx = r * n + col;
                    if (s.Seen[idx] == 0)
                    {
                        s.Seen[idx] = 1;
                        s.Touched.Add(idx);
                        s.CoreDist[idx] = float.PositiveInfinity;
                        s.Lo[idx] = float.NegativeInfinity;
                        s.Hi[idx] = top;
                        continue;
                    }
                    if (s.CoreDist[idx] < float.PositiveInfinity) continue;
                    if (top < s.Hi[idx]) s.Hi[idx] = top;
                    if (s.Lo[idx] > s.Hi[idx]) s.Lo[idx] = s.Hi[idx];
                }
        }
    }

    /// <summary>Narrows [xMin, xMax] to where <c>k·x + c</c> lies in [lo, hi]; false if empty.</summary>
    private static bool ClipRange(double k, double c, double lo, double hi, ref double xMin, ref double xMax)
    {
        if (Math.Abs(k) < 1e-12) return c >= lo && c <= hi;
        double x0 = (lo - c) / k, x1 = (hi - c) / k;
        if (x0 > x1) (x0, x1) = (x1, x0);
        xMin = Math.Max(xMin, x0);
        xMax = Math.Min(xMax, x1);
        return xMin <= xMax;
    }
}
