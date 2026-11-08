using System;
using System.Collections.Generic;
using UnitSport.Core;
using UnitSport.Gpx;
using UnitSport.Terrain.Format;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// A GPX track on its own map plane (#1514, G2 C5; src/Gpx/GpxTrack.cs, linked in): its lengths are the Earth's,
/// a plane point goes to the Earth and back, and the LV95 tiles under it are found.
/// </summary>
public class GpxTrackPlaneTests
{
    /// <summary>
    /// A winding 70 km ride from Lausanne towards Bern, a fix every ~300 m, on the ellipsoid (height 0, so the
    /// Earth's distance is the plane's to compare against with no height stretch in it).
    /// </summary>
    private static (List<double> Lats, List<double> Lons) Ride()
    {
        var lats = new List<double>();
        var lons = new List<double>();
        for (int i = 0; i <= 240; i++)
        {
            double t = i / 240.0;
            lats.Add(46.52 + 0.43 * t + 0.02 * Math.Sin(t * 17));
            lons.Add(6.63 + 0.80 * t + 0.03 * Math.Cos(t * 11));
        }
        return (lats, lons);
    }

    private static GlobalPos OnEllipsoid(double lat, double lon) =>
        GlobalPos.FromEcef(Ellipsoid.Wgs84.ToEcef(new Geodetic(lat, lon, 0)));

    [Fact]
    public void Lengths_on_the_plane_are_the_Earth_s()
    {
        var (lats, lons) = Ride();
        var plane = TrackPlane.Around(lats, lons);
        double onPlane = 0, raw = 0, earth = 0, worstStep = 0;
        var (px, py) = plane.Forward(lats[0], lons[0]);
        for (int i = 1; i < lats.Count; i++)
        {
            var (x, y) = plane.Forward(lats[i], lons[i]);
            double step = TrackPlane.Length(plane, px, py, x, y);
            double truth = OnEllipsoid(lats[i - 1], lons[i - 1]).HorizontalDistanceTo(OnEllipsoid(lats[i], lons[i]));
            worstStep = Math.Max(worstStep, Math.Abs(step - truth) / truth);
            onPlane += step;
            raw += Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
            earth += truth;
            (px, py) = (x, y);
        }
        Assert.InRange(earth, 60_000, 90_000);
        // the scale taken off: to a part in a million per step and over the whole ride
        Assert.True(worstStep < 1e-6, $"worst step off by {worstStep * 1e6:F3} ppm");
        Assert.True(Math.Abs(onPlane - earth) < 1e-6 * earth, $"the ride is {onPlane - earth:F4} m off over {earth:F0} m");
        // the plane's own metres are longer away from the centre (up to ~11 ppm at its ends here)
        Assert.True(raw > earth && raw - earth > 50 * Math.Abs(onPlane - earth), $"raw {raw - earth:F4} m, scaled {onPlane - earth:F4} m");
    }

    [Fact]
    public void A_plane_point_goes_to_the_Earth_and_back()
    {
        var (lats, lons) = Ride();
        var plane = TrackPlane.Around(lats, lons);
        foreach (var (x, y, ele) in new[] { (0.0, 0.0, 400.0), (31_000.0, 22_000.0, 1_850.0), (-29_000.0, -24_000.0, 372.0) })
        {
            var at = plane.At(x, y, ele);
            Assert.Equal(ele, at.Alt, 6);
            var (bx, by) = plane.Forward(at.Ecef);
            Assert.Equal(x, bx, 6);
            Assert.Equal(y, by, 6);
            // the same place the LV95 track gave: its LV95 back on the plane, at its own height
            var lv = at.Lv95;
            var (lx, ly) = plane.FromLv95(lv.E, lv.N, lv.H);
            Assert.True(Math.Abs(lx - x) < 1e-3 && Math.Abs(ly - y) < 1e-3, $"{lx - x:F4}/{ly - y:F4}");
        }
    }

    [Fact]
    public void The_LV95_box_holds_the_track()
    {
        var (lats, lons) = Ride();
        var plane = TrackPlane.Around(lats, lons);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var points = new List<(double X, double Y)>();
        for (int i = 0; i < lats.Count; i++)
        {
            var (x, y) = plane.Forward(lats[i], lons[i]);
            points.Add((x, y));
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        var (e0, n0, e1, n1) = TrackPlane.Lv95Box(plane, minX - 200, minY - 200, maxX + 200, maxY + 200);
        foreach (var (x, y) in points)
        {
            var (e, n) = plane.Lv95Of(x, y);
            Assert.InRange(e, e0 + 150, e1 - 150);
            Assert.InRange(n, n0 + 150, n1 - 150);
        }
        // Lausanne to Bern in LV95
        Assert.InRange(e0, 2_530_000, 2_550_000);
        Assert.InRange(n1, 1_190_000, 1_210_000);
    }
}
