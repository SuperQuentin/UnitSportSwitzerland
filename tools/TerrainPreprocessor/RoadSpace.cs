using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// A piece of a clipped polyline, on one tile of a <see cref="IRoadSpace{TTile}"/>. <see cref="AtLineStart"/> /
/// <see cref="AtLineEnd"/> mark the <i>original</i> ends, so height blending happens at real abutments and
/// portals rather than wherever a tile boundary happened to fall.
/// </summary>
public sealed record RoadPiece<TTile>(TTile Tile, List<(double E, double N, double Z)> Points)
{
    public bool AtLineStart { get; set; }
    public bool AtLineEnd { get; set; }
}

/// <summary>
/// Where the road stage works (#1507, G2 B3 PR 2): its plane, its units, its ground and its tile loop, so the
/// extraction's maths (<see cref="RoadExtractor"/>, <see cref="TunnelCarver"/>, <see cref="ParkingAreaExtractor{TTile}"/>,
/// <see cref="FranceRoads"/>) runs unchanged on either instance. Positions on the plane are (E, N) metres and
/// heights above sea level (LHN95), as the LV95 build's are.
/// <list type="bullet">
/// <item><see cref="Lv95RoadSpace"/>: LV95 E, N and 1 km tiles, the LV95 build as it was (byte-identical).</item>
/// <item><see cref="FrameRoadSpace"/>: a work frame's plane (<see cref="WorkPlane"/>) and its level-14 leaves.</item>
/// </list>
/// </summary>
public interface IRoadSpace<TTile> where TTile : struct, IEquatable<TTile>
{
    /// <summary>The tiles the stage writes.</summary>
    IReadOnlyCollection<TTile> Tiles { get; }

    bool Writes(TTile tile);

    /// <summary>Whether a piece on this tile still carves the written tiles' holes (a portal just outside them).</summary>
    bool Carves(TTile tile);

    /// <summary>The region an LV95 source (swissTLM3D) is read over: the written tiles and <paramref name="marginM"/> round them.</summary>
    ISourceRegion Lv95Region(double marginM);

    /// <summary>An LV95 polyline with its LHN95 heights on the plane (heights above sea level).</summary>
    GeoPackageReader.Polyline FromLv95(GeoPackageReader.Polyline line);

    /// <summary>An LV95 point at its LHN95 height on the plane.</summary>
    (double X, double Y) FromLv95(double e, double n, double h);

    /// <summary>A latitude and longitude (WGS84 / ETRS89) on the plane.</summary>
    (double X, double Y) FromLatLon(double latDeg, double lonDeg);

    /// <summary>The ground on the plane, above sea level; null where there is none.</summary>
    IGround Ground { get; }

    /// <summary>The tile loop: a plane polyline cut where it leaves a tile, each piece on the tile it lies on.</summary>
    List<RoadPiece<TTile>> Split(GeoPackageReader.Polyline line);

    /// <summary>The tile holding a plane point.</summary>
    TTile TileAt(double x, double y);

    /// <summary>A plane point and its height above sea level in a tile's own frame, as its records hold it.</summary>
    (float X, float Y, float Z) ToTile(TTile tile, double x, double y, double h);

    /// <summary>An empty record for a written tile.</summary>
    RoadTile NewTile(TTile tile);

    /// <summary>
    /// The ground quads of the written tiles whose centre may lie within <paramref name="reach"/> of a plane point
    /// (a superset; the caller tests the centre): the tile, the quad's index in that tile's holes, its centre.
    /// </summary>
    IEnumerable<(TTile Tile, int Quad, double X, double Y)> QuadsNear(double x, double y, double reach);
}

/// <summary>
/// The LV95 build's road space: LV95 E, N, its 1 km tiles, its chunk grids' ground (<see cref="Lv95Ground"/>),
/// its 1 m hole quads (<see cref="HoleFormat"/>). The code it holds is what the stage ran inline before #1507.
/// </summary>
public sealed class Lv95RoadSpace(IReadOnlyCollection<TileId> tiles, IGround ground, GeoInput? geo = null) : IRoadSpace<TileId>
{
    private readonly HashSet<TileId> _tiles = tiles.ToHashSet();

    public IReadOnlyCollection<TileId> Tiles => tiles;

    public bool Writes(TileId tile) => _tiles.Contains(tile);

    public bool Carves(TileId tile) => _tiles.Contains(tile);

    public ISourceRegion Lv95Region(double marginM) => new TileRegion(tiles, marginM);

    public GeoPackageReader.Polyline FromLv95(GeoPackageReader.Polyline line) => line;

    public (double X, double Y) FromLv95(double e, double n, double h) => (e, n);

    /// <summary>On the ground of the built tiles (<see cref="GeoInput"/>), as the LV95 build takes every latitude and longitude.</summary>
    public (double X, double Y) FromLatLon(double latDeg, double lonDeg) => (geo ?? GeoInput.NoGround).ToLv95(latDeg, lonDeg);

    public IGround Ground => ground;

    public List<RoadPiece<TileId>> Split(GeoPackageReader.Polyline line) => PolylineClipper.SplitByTile(line);

    public TileId TileAt(double x, double y) => TileId.FromLv95(x, y);

    public (float X, float Y, float Z) ToTile(TileId tile, double x, double y, double h) =>
        ((float)(x - tile.MinE), (float)h, (float)(tile.MaxN - y));

    public RoadTile NewTile(TileId tile) => new() { Id = tile, Segments = new() };

    public IEnumerable<(TileId Tile, int Quad, double X, double Y)> QuadsNear(double x, double y, double reach)
    {
        double spacing = ChunkFormat.SpacingM;
        for (double qe = Math.Floor((x - reach) / spacing) * spacing; qe <= x + reach; qe += spacing)
            for (double qn = Math.Floor((y - reach) / spacing) * spacing; qn <= y + reach; qn += spacing)
            {
                // the quad's centre
                double ce = qe + spacing * 0.5, cn = qn + spacing * 0.5;
                var tile = TileId.FromLv95(ce, cn);
                int col = (int)Math.Floor((ce - tile.MinE) / spacing);
                int row = (int)Math.Floor((tile.MaxN - cn) / spacing);
                if ((uint)col >= HoleFormat.QuadsPerSide || (uint)row >= HoleFormat.QuadsPerSide) continue;
                yield return (tile, HoleFormat.CellIndex(col, row), ce, cn);
            }
    }
}

/// <summary>
/// A work frame's road space (#1507): the frame's <see cref="WorkPlane"/> (true metres, within 3 ppm), heights
/// above its sea level, the frame's core leaves as the tiles written, their mesh as the ground
/// (<see cref="FrameGround"/>), their lattice quads as the holes' (<see cref="LeafHoles"/>). Sources enter
/// rigorously (<see cref="WorkFrame.FromSource"/>, <see cref="WorkPlane.ToPlane(double, double)"/>); records leave in
/// each leaf's anchor frame (<see cref="LeafTransform.PlaneToLocal"/>).
///
/// <para>
/// <b>The tile loop</b> cuts a line where its leaf changes, the leaf being the cube cell of the geodetic normal
/// under the point (as <see cref="FrameGround"/> finds it): on one face the leaf's fractional index along u and
/// v is solved for each integer it passes (a secant on the exact mapping, to 1e-9 of a leaf), across a face
/// edge by bisection. Both pieces share the cut point, so a line stays continuous across leaves; across
/// frames the two frames cut it on their own planes, the same place to well under a micrometre.
/// </para>
/// </summary>
public sealed class FrameRoadSpace : IRoadSpace<CubeKey>
{
    private readonly FramePlan _plan;
    private readonly HashSet<CubeKey> _core;
    private readonly int _level;

    public FrameRoadSpace(FramePlan plan, IGround ground)
    {
        _plan = plan;
        _core = plan.Core.ToHashSet();
        _level = plan.Core[0].Level;
        Ground = ground;
    }

    public WorkFrame Frame => _plan.Frame;

    public IReadOnlyCollection<CubeKey> Tiles => _plan.Core;

    public bool Writes(CubeKey tile) => _core.Contains(tile);

    /// <summary>Any leaf: a portal in the next frame's leaf still opens this frame's quads within reach of it.</summary>
    public bool Carves(CubeKey tile) => true;

    public ISourceRegion Lv95Region(double marginM) => _plan.Region(Lv95Source.Instance, marginM);

    public GeoPackageReader.Polyline FromLv95(GeoPackageReader.Polyline line)
    {
        var x = new double[line.Count];
        var y = new double[line.Count];
        var h = new double[line.Count];
        for (int i = 0; i < line.Count; i++)
            (x[i], y[i], h[i]) = Frame.FromSource(Lv95Source.Instance, line.E[i], line.N[i], line.Z[i]);
        return new GeoPackageReader.Polyline(x, y, h);
    }

    public (double X, double Y) FromLv95(double e, double n, double h)
    {
        var (x, y, _) = Frame.FromSource(Lv95Source.Instance, e, n, h);
        return (x, y);
    }

    /// <summary>Exact whatever the height: the plane is the ellipsoid seen from above.</summary>
    public (double X, double Y) FromLatLon(double latDeg, double lonDeg) => Frame.Plane.ToPlane(latDeg, lonDeg);

    public IGround Ground { get; }

    public CubeKey TileAt(double x, double y)
    {
        var (f, u, v) = CubeSphere.FromNormal(Frame.Plane.NormalAt(x, y));
        return CubeKey.FromFace(f, u, v, _level);
    }

    public (float X, float Y, float Z) ToTile(CubeKey tile, double x, double y, double h)
    {
        var (lx, ly, lz) = _plan.Transform(tile).PlaneToLocal(x, y, h);
        return ((float)lx, (float)ly, (float)lz);
    }

    public RoadTile NewTile(CubeKey tile) => new() { Segments = new() };

    public IEnumerable<(CubeKey Tile, int Quad, double X, double Y)> QuadsNear(double x, double y, double reach)
    {
        var home = TileAt(x, y);
        var near = new List<CubeKey> { home };
        foreach (var n in home.Neighbours()) near.Add(n);
        foreach (var leaf in near)
        {
            if (!_core.Contains(leaf)) continue;
            var t = _plan.Transform(leaf);
            var (i, j) = t.PlaneToLattice(x, y);
            if (double.IsNaN(i) || double.IsNaN(j)) continue;
            // the lattice step there, so the window holds every quad within reach
            int ci = Math.Clamp((int)Math.Floor(i), 0, t.Cells - 1), cj = Math.Clamp((int)Math.Floor(j), 0, t.Cells - 1);
            var (x0, y0) = t.LatticeToPlane(ci, cj);
            var (x1, y1) = t.LatticeToPlane(ci + 1, cj);
            var (x2, y2) = t.LatticeToPlane(ci, cj + 1);
            double step = Math.Min(Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)), Math.Sqrt((x2 - x0) * (x2 - x0) + (y2 - y0) * (y2 - y0)));
            int r = (int)Math.Ceiling(reach / step) + 2;
            int i0 = Math.Max(0, (int)Math.Floor(i) - r), i1 = Math.Min(t.Cells - 1, (int)Math.Floor(i) + r);
            int j0 = Math.Max(0, (int)Math.Floor(j) - r), j1 = Math.Min(t.Cells - 1, (int)Math.Floor(j) + r);
            for (int qj = j0; qj <= j1; qj++)
                for (int qi = i0; qi <= i1; qi++)
                {
                    var (cx, cy) = t.LatticeToPlane(qi + 0.5, qj + 0.5);
                    yield return (leaf, LeafHoles.Quad(qi, qj, t.Bits), cx, cy);
                }
        }
    }

    public List<RoadPiece<CubeKey>> Split(GeoPackageReader.Polyline line)
    {
        var pieces = new List<RoadPiece<CubeKey>>();
        if (line.Count < 2) return pieces;
        var first = (line.E[0], line.N[0], line.Z[0]);
        var current = new RoadPiece<CubeKey>(TileAt(first.Item1, first.Item2), new() { first });
        pieces.Add(current);
        for (int i = 1; i < line.Count; i++)
        {
            var a = (E: line.E[i - 1], N: line.N[i - 1], Z: line.Z[i - 1]);
            var b = (E: line.E[i], N: line.N[i], Z: line.Z[i]);
            var end = TileAt(b.E, b.N);
            double t0 = 0;
            for (int guard = 0; current.Tile != end && guard < 64; guard++)
            {
                var (t, next) = Leaves(current.Tile, a.E, a.N, b.E, b.N, t0);
                var c = (a.E + (b.E - a.E) * t, a.N + (b.N - a.N) * t, a.Z + (b.Z - a.Z) * t);
                current.Points.Add(c);
                current = new RoadPiece<CubeKey>(next, new() { c });
                pieces.Add(current);
                t0 = t;
            }
            current.Points.Add(b);
        }
        pieces.RemoveAll(p => p.Points.Count < 2);
        if (pieces.Count > 0)
        {
            pieces[0].AtLineStart = true;
            pieces[^1].AtLineEnd = true;
        }
        return pieces;
    }

    /// <summary>The fractional leaf index along u and v of a plane point, and its face.</summary>
    private (int Face, double I, double J) Index(double x, double y)
    {
        var (f, u, v) = CubeSphere.FromNormal(Frame.Plane.NormalAt(x, y));
        double n = 1 << _level;
        return (f, (u + 1) * 0.5 * n, (v + 1) * 0.5 * n);
    }

    /// <summary>
    /// Where the segment a–b, past <paramref name="t0"/>, first leaves <paramref name="leaf"/>, and the leaf it goes on
    /// in: the smallest t of the leaf's four edges it crosses (secant on the face index; by bisection where the far end
    /// is on another face), then bracketed on the leaf itself so the cut is the first point past the edge, on the next leaf.
    /// </summary>
    private (double T, CubeKey Next) Leaves(CubeKey leaf, double ax, double ay, double bx, double by, double t0)
    {
        double guess = Guess(leaf, ax, ay, bx, by, t0);
        bool In(double t) => TileAt(ax + (bx - ax) * t, ay + (by - ay) * t) == leaf;
        // a bracket round the guess with the leaf at its start and another at its end, then bisection to the last bit
        double lo = Math.Max(t0, guess - 1e-6), hi = Math.Min(1, guess + 1e-6);
        if (!In(lo)) lo = t0;
        if (In(hi)) hi = 1;
        for (int it = 0; it < 64 && hi - lo > 1e-15; it++)
        {
            double m = 0.5 * (lo + hi);
            if (In(m)) lo = m; else hi = m;
        }
        return (hi, TileAt(ax + (bx - ax) * hi, ay + (by - ay) * hi));
    }

    private double Guess(CubeKey leaf, double ax, double ay, double bx, double by, double t0)
    {
        var ib = Index(bx, by);
        if (ib.Face == leaf.Face)
        {
            double best = double.PositiveInfinity;
            foreach (var (axis, k) in new[] { (0, leaf.X), (0, leaf.X + 1), (1, leaf.Y), (1, leaf.Y + 1) })
            {
                double G(double t)
                {
                    var p = Index(ax + (bx - ax) * t, ay + (by - ay) * t);
                    return (axis == 0 ? p.I : p.J) - k;
                }
                double g0 = G(t0), g1 = G(1);
                if (g0 * g1 > 0 || g0 == g1) continue;
                double lo = t0, hi = 1, glo = g0, ghi = g1;
                // regula falsi with the Illinois step: the index is nearly linear along a straight plane line
                for (int it = 0; it < 60 && hi - lo > 1e-12; it++)
                {
                    double t = (lo * ghi - hi * glo) / (ghi - glo);
                    double g = G(t);
                    if (g == 0) { lo = hi = t; break; }
                    if ((g < 0) == (glo < 0)) { lo = t; glo = g; ghi *= 0.5; }
                    else { hi = t; ghi = g; glo *= 0.5; }
                }
                double cut = 0.5 * (lo + hi);
                if (cut > t0 + 1e-12) best = Math.Min(best, cut);
            }
            if (!double.IsPositiveInfinity(best)) return best;
        }
        // across a face edge (or a grazing cut the signs missed): the first t whose leaf differs
        double l = t0, h = 1;
        for (int it = 0; it < 60; it++)
        {
            double m = 0.5 * (l + h);
            if (TileAt(ax + (bx - ax) * m, ay + (by - ay) * m) == leaf) l = m; else h = m;
        }
        return 0.5 * (l + h);
    }
}
