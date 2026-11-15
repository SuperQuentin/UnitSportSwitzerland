using System;
using System.Collections.Generic;
using UnitSport.Terrain.Format;
using UnitSport.World;

namespace UnitSport.Net;

/// <summary>
/// Line of sight in true Earth positions (#1513, G2 C4, <c>--frame earth</c>): the straight ray between two ECEF points
/// against the ground's ellipsoidal heights. The Earth's curve is in the geometry itself (a chord between two far points
/// dips under the ellipsoid, so the ground rises above it), and normal atmospheric refraction (k = 0.13,
/// <see cref="Core.Curvature.Refraction"/>) lifts the ray by k·s(L−s)/2R: a peak just past the geometric horizon is seen,
/// as a photo shows it. Over a level plain this is the flat world's <see cref="Interest.Clear"/> with its bulge.
/// </summary>
public static class EarthSight
{
    /// <summary>
    /// The ground within this of either end is not asked: each end stands on its own hill, and a coarse lattice there (the
    /// pyramid outside the built region is ETOPO's ~450 m) rounds a summit off or lifts a slope over a body standing on it.
    /// Mont Blanc's top from La Dôle was hidden by its own smoothed massif 100 m before it.
    /// </summary>
    public const double EndM = 250;

    /// <summary>
    /// False when the ground stands above the ray by more than <paramref name="margin"/> anywhere between the two, sampled
    /// every <paramref name="step"/> metres. <paramref name="ground"/> answers the ground's ellipsoidal height under a point,
    /// or null where it knows none (that sample is skipped).
    /// </summary>
    public static bool Clear(EcefPos eye, EcefPos target, Func<EcefPos, double?> ground, double step = 100, double margin = 12,
        double refraction = Core.Curvature.Refraction)
    {
        double dx = target.X - eye.X, dy = target.Y - eye.Y, dz = target.Z - eye.Z;
        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        int n = (int)(length / step);
        double lift = refraction / (2 * Core.Curvature.EarthRadiusM);
        for (int i = 1; i < n; i++)
        {
            double t = i / (double)n, s = t * length;
            if (s < EndM || length - s < EndM) continue;
            var p = new EcefPos(eye.X + dx * t, eye.Y + dy * t, eye.Z + dz * t);
            if (ground(p) is not { } g) continue;
            double h = Ellipsoid.Wgs84.ToGeodetic(p).H + s * (length - s) * lift;
            if (g > h + margin) return false;
        }
        return true;
    }

    /// <summary>For the checks: how far the ground stands highest over the ray less the margin (above 0: blocked), and where, metres from the eye.</summary>
    public static (double Excess, double At) Worst(EcefPos eye, EcefPos target, Func<EcefPos, double?> ground, double step = 100,
        double margin = 12, double refraction = Core.Curvature.Refraction)
    {
        double dx = target.X - eye.X, dy = target.Y - eye.Y, dz = target.Z - eye.Z;
        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        int n = (int)(length / step);
        double lift = refraction / (2 * Core.Curvature.EarthRadiusM), worst = double.NegativeInfinity, at = 0;
        for (int i = 1; i < n; i++)
        {
            double t = i / (double)n, s = t * length;
            if (s < EndM || length - s < EndM) continue;
            var p = new EcefPos(eye.X + dx * t, eye.Y + dy * t, eye.Z + dz * t);
            if (ground(p) is not { } g) continue;
            double excess = g - (Ellipsoid.Wgs84.ToGeodetic(p).H + s * (length - s) * lift) - margin;
            if (excess > worst) (worst, at) = (excess, s);
        }
        return (worst, at);
    }

    /// <summary>
    /// A point <paramref name="eastM"/> metres east of a place along its parallel, at height <paramref name="h"/> (for the
    /// self-check: the parallel's radius at the latitude, so the distance holds to a few metres in 100 km).
    /// </summary>
    public static EcefPos East(double latDeg, double lonDeg, double eastM, double h)
    {
        double lat = latDeg * Math.PI / 180;
        double a = Ellipsoid.Wgs84.A, e2 = Ellipsoid.Wgs84.E2;
        double prime = a / Math.Sqrt(1 - e2 * Math.Sin(lat) * Math.Sin(lat));
        double dLon = eastM / (prime * Math.Cos(lat)) * 180 / Math.PI;
        return Ellipsoid.Wgs84.ToEcef(new Geodetic(latDeg, lonDeg + dLon, h));
    }

    /// <summary>
    /// The rules on synthetic ground, for <c>--interestcheck</c>: a level plain (h = 0) and a 500 m peak far east of an eye
    /// 100 m up. The 12 m margin lets the ground stand that far over the ray: the geometric line to the peak's top clears up
    /// to ~119 km, the refracted one to ~127 km.
    /// </summary>
    public static bool SelfCheck(Action<bool, bool, string> expect)
    {
        const double lat = 46.5, lon = 7.5;
        var eye = East(lat, lon, 0, 100);
        Func<EcefPos, double?> plain = _ => 0;
        bool ok = true;
        void Expect(bool got, bool want, string what) { expect(got, want, what); ok &= got == want; }
        // the flat world's pinned cases (#187): 61 m of bulge halfway at 60 km, 138 m at 90 km
        Expect(Clear(eye, East(lat, lon, 60_000, 100), plain), true, "earth: 60 km over a level plain, 100 m up");
        Expect(Clear(eye, East(lat, lon, 90_000, 100), plain), false, "earth: 90 km over a level plain, behind the Earth's curve");
        // a 500 m peak's top: hidden by the curve without refraction at 122 km (13 m under), seen with it (20 m over);
        // hidden either way at 140 km (51 m under, refracted)
        Expect(Clear(eye, East(lat, lon, 122_000, 501), plain, refraction: 0), false, "earth: a peak 122 km off, behind the geometric horizon");
        Expect(Clear(eye, East(lat, lon, 122_000, 501), plain), true, "earth: the same peak seen past the horizon with refraction");
        Expect(Clear(eye, East(lat, lon, 140_000, 501), plain), false, "earth: a peak 140 km off, hidden by the Earth's curve even refracted");
        // a 300 m ridge halfway between two walkers 1 km apart
        // (the ridge 120 m wide across the line at 500 m: a degree of longitude is ~76.7 km here)
        double ridgeLon = Ellipsoid.Wgs84.ToGeodetic(East(lat, lon, 500, 0)).LonDeg, halfDeg = 60 / 76_700.0;
        Func<EcefPos, double?> ridge = p => Math.Abs(Ellipsoid.Wgs84.ToGeodetic(p).LonDeg - ridgeLon) < halfDeg ? 300 : 0;
        Expect(Clear(East(lat, lon, 0, 2), East(lat, lon, 1000, 2), ridge), false, "earth: a ridge blocks two walkers");
        Expect(Clear(East(lat, lon, 0, 2), East(lat, lon, 1000, 2), plain), true, "earth: two walkers 1 km apart on a plain");
        return ok;
    }

    /// <summary>
    /// <c>--earthsightcheck</c>: the rule on the real <c>cube_global</c> pyramid in <paramref name="dir"/> (the D: one), from
    /// La Dôle's summit (1677 m, Jura): Mont Blanc's top 88 km off (the pyramid's highest point within 1.5 km: ETOPO's
    /// smoothed massif outside the built region) is seen, as from every Jura summit on a clear day; the
    /// floors of the Chamonix valley under it and of the Rhône at Riddes are not. Skipped (true) without a pyramid that
    /// reaches level 10.
    /// </summary>
    public static bool RealCheck(Terrain.Leaves.PyramidSource? source)
    {
        if (source is not { FinestRealLevel: >= PyramidGround.DefaultLevel } || new PyramidGround(source) is not { } ground)
        {
            Godot.GD.Print("[interest] earth on the real pyramid: no pyramid, skipped");
            return true;
        }
        bool ok = true;
        EcefPos At(double latDeg, double lonDeg, double up, double searchM = 0)
        {
            // a summit: the highest point of the pyramid within searchM (a smoothed massif's top is beside the real one)
            if (searchM > 0)
            {
                double best = double.NegativeInfinity, bestLat = latDeg, bestLon = lonDeg;
                for (double dn = -searchM; dn <= searchM; dn += 100)
                    for (double de = -searchM; de <= searchM; de += 100)
                    {
                        double la = latDeg + dn / 111_200, lo = lonDeg + de / (111_200 * Math.Cos(latDeg * Math.PI / 180));
                        if (ground.HeightAt(Ellipsoid.Wgs84.ToEcef(new Geodetic(la, lo, 0))) is { } hh && hh > best) (best, bestLat, bestLon) = (hh, la, lo);
                    }
                (latDeg, lonDeg) = (bestLat, bestLon);
            }
            var onGround = Ellipsoid.Wgs84.ToEcef(new Geodetic(latDeg, lonDeg, 0));
            double h = ground.HeightAt(onGround) ?? double.NaN;
            Godot.GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[interest] ground at {latDeg:F4}N {lonDeg:F4}E: {h:F0} m (ellipsoidal)"));
            return Ellipsoid.Wgs84.ToEcef(new Geodetic(latDeg, lonDeg, h + up));
        }
        Func<EcefPos, double?> heights = ground.HeightAt;
        void Expect(EcefPos eye, EcefPos target, bool want, string what)
        {
            bool got = Clear(eye, target, heights);
            var (excess, at) = Worst(eye, target, heights);
            Godot.GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[interest] {(got == want ? "ok  " : "FAIL")} {what}: the ground at most {excess:F0} m over the ray less the margin, {at / 1000:F1} km out"));
            ok &= got == want;
        }
        var dole = At(46.4253, 6.0997, 2);
        Expect(dole, At(45.8326, 6.8652, 2, searchM: 1500), true, "earth, real pyramid: Mont Blanc's top from La Dôle (88 km)");
        Expect(dole, At(45.9237, 6.8694, 2), false, "earth, real pyramid: Chamonix's valley floor from La Dôle, behind the ranges");
        Expect(dole, At(46.1709, 7.1991, 2), false, "earth, real pyramid: Riddes in the Rhône valley from La Dôle");
        return ok;
    }
}

/// <summary>
/// The ground's ellipsoidal height anywhere from the <c>cube_global</c> pyramid (#1513): the tile at
/// <see cref="Level"/> under a point, or its nearest stored ancestor, read bilinearly on its lattice. Decoded tiles are
/// kept (the last <see cref="Kept"/>); the server's interest asks it on its main thread only.
/// </summary>
public sealed class PyramidGround
{
    /// <summary>The level asked first: ~9.8 km tiles, a vertex every ~19 m, as fine as a 100 m sight step needs.</summary>
    public const int DefaultLevel = 10;

    public const int Kept = 48;

    public int Level { get; }
    public int Count => _source.Count;

    /// <summary>Where the pyramid comes from, for the log line.</summary>
    public string Dir => _source.Describe();

    private readonly Terrain.Leaves.PyramidSource _source;
    private readonly Dictionary<CubeKey, CubeTile?> _tiles = new();
    private readonly Queue<CubeKey> _order = new();

    /// <param name="source">The pyramid's chain (#1656); a tile the server streams is passed over for its ancestor: the
    /// sight lines and the probes ask on the main thread, which never waits on the network.</param>
    public PyramidGround(Terrain.Leaves.PyramidSource source, int level = DefaultLevel)
    {
        _source = source;
        Level = level;
    }

    /// <summary>The ground's ellipsoidal height under <paramref name="p"/> (its height ignored), or null with no tile there.</summary>
    public double? HeightAt(EcefPos p)
    {
        var (face, u, v, _) = CubeSphere.FromEcef(p);
        for (var key = CubeKey.FromFace(face, u, v, Level); ; key = key.Parent)
        {
            if (Tile(key) is { } tile) return Sample(tile, u, v);
            if (key.Level == 0) return null;
        }
    }

    private CubeTile? Tile(CubeKey key)
    {
        if (_tiles.TryGetValue(key, out var tile)) return tile;
        if (!_source.Has(key) || _source.IsStreamed(key)) return null;
        tile = _source.Heights(key);
        _tiles[key] = tile;
        _order.Enqueue(key);
        if (_order.Count > Kept) _tiles.Remove(_order.Dequeue());
        return tile;
    }

    private static double? Sample(CubeTile tile, double u, double v)
    {
        var key = tile.Key;
        int last = tile.Side - 1;
        double cell = 2.0 / (1L << (key.Level + tile.Bits));
        double fi = Math.Clamp((u - key.U0) / cell, 0, last), fj = Math.Clamp((v - key.V0) / cell, 0, last);
        int i0 = Math.Min((int)fi, last - 1), j0 = Math.Min((int)fj, last - 1);
        double tx = fi - i0, ty = fj - j0;
        double h00 = tile.HeightAt(i0, j0), h10 = tile.HeightAt(i0 + 1, j0), h01 = tile.HeightAt(i0, j0 + 1), h11 = tile.HeightAt(i0 + 1, j0 + 1);
        double h = (h00 * (1 - tx) + h10 * tx) * (1 - ty) + (h01 * (1 - tx) + h11 * tx) * ty;
        return double.IsNaN(h) ? null : h;
    }
}
