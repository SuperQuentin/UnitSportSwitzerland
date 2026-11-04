using System;
using System.Diagnostics;
using UnitSport.Terrain.Format;
using Xunit;
using Xunit.Abstractions;

namespace UnitSport.Tests;

/// <summary>
/// The build's work frames and source projections (#1443 G2, B2 #1506, tools/TerrainFormat/WorkFrame.cs,
/// SourceProjection.cs, SeaLevel.cs): every source enters a ~20 km cube cell's flat frame rigorously and
/// leaves it into ECEF, with heights above sea level.
/// </summary>
public class WorkFrameTests
{
    private readonly ITestOutputHelper _out;

    public WorkFrameTests(ITestOutputHelper output) => _out = output;

    /// <summary>Cells to compare on: Switzerland, Brittany, the pole, a face edge's middle, a cube corner.</summary>
    private static readonly (string Name, CubeKey Cell)[] Cells =
    {
        ("Bern", CubeKey.FromLatLon(46.95108, 7.43863, WorkFrame.Level)),
        ("Brest", CubeKey.FromLatLon(48.39, -4.49, WorkFrame.Level)),
        ("North Pole", CubeKey.FromLatLon(89.99, 0, WorkFrame.Level)),
        ("face edge", CubeKey.FromFace(0, 0.999, 0.0, WorkFrame.Level)),
        ("cube corner", CubeKey.FromFace(0, 0.999, 0.999, WorkFrame.Level)),
    };

    private static IWorkPlane[] Planes(CubeKey cell)
    {
        var c = cell.Centre;
        return new IWorkPlane[] { new OrthographicPlane(cell.Anchor), new StereographicPlane(c.LatDeg, c.LonDeg), new LatticePlane(cell) };
    }

    /// <summary>Plane points over the cell and a 2 km margin round it, on a 21 × 21 grid.</summary>
    private static (double X, double Y)[] Grid(CubeKey cell, IWorkPlane plane)
    {
        // the cell's extent on the plane from its corners, then the margin
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (u, v) in new[] { (cell.U0, cell.V0), (cell.U0 + cell.Size, cell.V0), (cell.U0, cell.V0 + cell.Size), (cell.U0 + cell.Size, cell.V0 + cell.Size) })
        {
            var g = CubeSphere.ToGeodetic(cell.Face, u, v);
            var (x, y) = plane.ToPlane(g.LatDeg, g.LonDeg);
            (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
        }
        const double margin = 2_000;
        var points = new (double, double)[21 * 21];
        for (int j = 0, k = 0; j < 21; j++)
            for (int i = 0; i < 21; i++, k++)
                points[k] = (minX - margin + (maxX - minX + 2 * margin) * i / 20, minY - margin + (maxY - minY + 2 * margin) * j / 20);
        return points;
    }

    private static EcefPos Ground(IWorkPlane plane, double x, double y)
    {
        var (lat, lon) = plane.FromPlane(x, y);
        return Ellipsoid.Wgs84.ToEcef(lat * Math.PI / 180, lon * Math.PI / 180, 0);
    }

    [Fact]
    public void Every_plane_round_trips_to_a_tenth_of_a_millimetre_over_its_cell_and_margin()
    {
        foreach (var (name, cell) in Cells)
            foreach (var plane in Planes(cell))
                foreach (var (x, y) in Grid(cell, plane))
                {
                    var (lat, lon) = plane.FromPlane(x, y);
                    var (bx, by) = plane.ToPlane(lat, lon);
                    double off = Math.Sqrt((bx - x) * (bx - x) + (by - y) * (by - y));
                    Assert.True(off < 1e-4, $"{plane.Name} at {name} ({x:F0}, {y:F0}) comes back {off * 1000:F3} mm off");
                }
    }

    [Fact]
    public void Every_plane_is_east_and_north_and_true_to_scale_at_the_centre()
    {
        foreach (var (name, cell) in Cells)
            foreach (var plane in Planes(cell))
            {
                var c = cell.Centre;
                var o = plane.ToPlane(c.LatDeg, c.LonDeg);
                var anchor = cell.Anchor;
                // a metre east and north of the centre on the plane lands a metre east and north on the ground
                var east = anchor.ToLocal(Ground(plane, o.X + 1, o.Y));
                var north = anchor.ToLocal(Ground(plane, o.X, o.Y + 1));
                var at = anchor.ToLocal(Ground(plane, o.X, o.Y));
                // the lattice plane is the Jacobian at the cell's (u, v) centre: a hair off the centre's geodetic
                // point and its scale, so it gets a looser bound
                double tol = plane is LatticePlane ? 1e-3 : 1e-6;
                Assert.True(Math.Abs(east.X - at.X - 1) < tol && Math.Abs(east.Z - at.Z) < tol, $"{plane.Name} at {name}: east is ({east.X - at.X}, {at.Z - east.Z})");
                Assert.True(Math.Abs(at.Z - north.Z - 1) < tol && Math.Abs(north.X - at.X) < tol, $"{plane.Name} at {name}: north is ({north.X - at.X}, {at.Z - north.Z})");
            }
    }

    [Fact]
    public void A_work_frame_takes_an_ECEF_point_and_its_height_above_sea_level_there_and_back()
    {
        var cell = Cells[0].Cell;
        var frame = new WorkFrame(cell, new OrthographicPlane(cell.Anchor), SwissSeaLevel.Instance);
        foreach (var (e, n, h) in new[] { (2_600_000.0, 1_200_000.0, 540.0), (2_612_345.6, 1_189_876.5, 1_234.5), (2_590_000.0, 1_210_000.0, 4_000.0) })
        {
            var p = SwissGeodesy.ToEcef(e, n, h);
            var (x, y, hSea) = frame.FromEcef(p);
            Assert.Equal(h, hSea, 3);
            Assert.True(frame.ToEcef(x, y, hSea).DistanceTo(p) < 1e-4, $"LV95 {e}/{n} {h} m");
            var (sx, sy, sh) = frame.FromSource(Lv95Source.Instance, e, n, h);
            Assert.True(Math.Abs(sx - x) < 1e-6 && Math.Abs(sy - y) < 1e-6 && Math.Abs(sh - hSea) < 1e-6);
        }
    }

    [Fact]
    public void Switzerland_s_sea_level_is_about_fifty_metres_above_the_ellipsoid_and_matches_LHN95()
    {
        var bern = SwissGeodesy.ToWgs84(2_600_000, 1_200_000, 540);
        double n = SwissSeaLevel.Instance.Undulation(bern.LatDeg, bern.LonDeg);
        Assert.InRange(n, 47, 53);
        // a height above sea level plus the undulation is the ellipsoidal height swisstopo's chain gives, to a millimetre
        Assert.Equal(bern.H, 540 + n, 3);
    }

    [Fact]
    public void Lambert93_has_its_origin_where_IGN_defines_it_and_round_trips()
    {
        var l93 = new Lambert93Source(SwissSeaLevel.Instance);
        var (x, y) = l93.FromLatLon(46.5, 3);
        Assert.Equal(700_000, x, 6);
        Assert.Equal(6_600_000, y, 6);
        foreach (var (lat, lon) in new[] { (48.8566, 2.3522), (48.39, -4.49), (41.92, 8.74), (46.2044, 6.1432), (51.08, 2.53) })
        {
            var (a, b) = l93.FromLatLon(lat, lon);
            var (blat, blon) = l93.ToLatLon(a, b);
            Assert.Equal(lat, blat, 10);
            Assert.Equal(lon, blon, 10);
        }
    }

    [Fact]
    public void LV95_enters_as_swisstopo_s_chain_has_it()
    {
        var (lat, lon) = Lv95Source.Instance.ToLatLon(2_600_000, 1_200_000);
        var (e, n) = Lv95Source.Instance.FromLatLon(lat, lon);
        Assert.Equal(2_600_000, e, 4);
        Assert.Equal(1_200_000, n, 4);
        var g = Lv95Source.ToGeodetic(2_600_000, 1_200_000, 540);
        Assert.Equal(SwissGeodesy.ToWgs84(2_600_000, 1_200_000, 540), g);
    }

    /// <summary>
    /// The comparison the user asked for (B2): over each cell and a 2 km margin, the worst scale error, the
    /// worst angle error (how far a right angle on the plane is from one on the ground), how far the leaves'
    /// lattice is from a regular grid on the plane, and the cost of a conversion. Prints a table; asserts only
    /// the bounds every candidate must meet.
    /// </summary>
    [Fact]
    public void The_three_planes_compared()
    {
        _out.WriteLine("| cell | plane | scale error, ppm | angle error, arcsec | lattice off a regular grid, m | to plane / from plane, ns |");
        _out.WriteLine("|---|---|---|---|---|---|");
        foreach (var (name, cell) in Cells)
            foreach (var plane in Planes(cell))
            {
                double worstScale = 0, worstAngle = 0;
                foreach (var (x, y) in Grid(cell, plane))
                {
                    var p = Ground(plane, x, y);
                    var e = Ground(plane, x + 1, y) - p;
                    var n = Ground(plane, x, y + 1) - p;
                    double ke = e.Length, kn = n.Length;
                    worstScale = Math.Max(worstScale, Math.Max(Math.Abs(ke - 1), Math.Abs(kn - 1)));
                    double cos = (e.X * n.X + e.Y * n.Y + e.Z * n.Z) / (ke * kn);
                    worstAngle = Math.Max(worstAngle, Math.Abs(Math.Asin(Math.Clamp(cos, -1, 1))));
                }
                // the two map projections stay within 10 ppm; the lattice's own drift is what the table shows
                if (plane is not LatticePlane) Assert.True(worstScale < 10e-6, $"{plane.Name} at {name}: scale {worstScale * 1e6:F1} ppm");
                _out.WriteLine($"| {name} | {plane.Name} | {worstScale * 1e6:F2} | {worstAngle * 180 / Math.PI * 3600:F3} | {LatticeOff(cell, plane):F4} | {Cost(cell, plane)} |");
            }
    }

    /// <summary>The leaves' corners over the cell (33 × 33) against the bilinear grid of the cell's own corners on the plane.</summary>
    private static double LatticeOff(CubeKey cell, IWorkPlane plane)
    {
        (double X, double Y) At(int i, int j)
        {
            var g = CubeSphere.ToGeodetic(cell.Face, cell.U0 + cell.Size * i / 32, cell.V0 + cell.Size * j / 32);
            return plane.ToPlane(g.LatDeg, g.LonDeg);
        }
        var (a, b, c, d) = (At(0, 0), At(32, 0), At(0, 32), At(32, 32));
        double worst = 0;
        for (int j = 0; j <= 32; j++)
            for (int i = 0; i <= 32; i++)
            {
                double s = i / 32.0, t = j / 32.0;
                double bx = (1 - s) * (1 - t) * a.X + s * (1 - t) * b.X + (1 - s) * t * c.X + s * t * d.X;
                double by = (1 - s) * (1 - t) * a.Y + s * (1 - t) * b.Y + (1 - s) * t * c.Y + s * t * d.Y;
                var p = At(i, j);
                worst = Math.Max(worst, Math.Sqrt((p.X - bx) * (p.X - bx) + (p.Y - by) * (p.Y - by)));
            }
        return worst;
    }

    private static string Cost(CubeKey cell, IWorkPlane plane)
    {
        var c = cell.Centre;
        const int n = 200_000;
        double sink = 0;
        var w = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) sink += plane.ToPlane(c.LatDeg + i * 1e-9, c.LonDeg).X;
        double to = w.Elapsed.TotalMilliseconds * 1e6 / n;
        w.Restart();
        for (int i = 0; i < n; i++) sink += plane.FromPlane(i * 1e-4, 0).LatDeg;
        double from = w.Elapsed.TotalMilliseconds * 1e6 / n;
        return double.IsNaN(sink) ? "?" : $"{to:F0} / {from:F0}";
    }
}
