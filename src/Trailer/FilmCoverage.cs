using System;
using System.Collections.Generic;
using System.Linq;
using UnitSport.Terrain.Format;

namespace UnitSport.Trailer;

/// <summary>
/// Every scene on real terrain only, never the generated fill (#882): the 1 km tiles a shot's
/// cameras and actors need. Every tile within <see cref="Radius"/> of each camera key's eye and of
/// each actor's spot (<see cref="HighRadius"/> when the eye is more than <see cref="HighEye"/> m above
/// the sea: it sees further) must be a built real tile, one the chunk manifest lists. A tile abroad
/// (no swissALTI3D tile there: Italy, France, much of Lake Geneva's French side) has no survey to build
/// from, so it is allowed and only counted. Under <c>--frame earth</c> the ground is cube leaves: <see cref="MissingLeaves"/>
/// (#1514). Pure (no Godot), so tier 0 tests it.
/// </summary>
public static class FilmCoverage
{
    public const double Radius = 5000, HighRadius = 8000, HighEye = 2200;

    /// <summary>A point a shot needs the ground round: LV95, and its height above the sea when known (NaN: low).</summary>
    public readonly record struct Need(double E, double N, double Alt, bool Eye);

    /// <summary>The tiles (E, N in km, as the manifest names them) whose square comes within <paramref name="radius"/> m of a point.</summary>
    public static IEnumerable<(int E, int N)> TilesNear(double e, double n, double radius)
    {
        int e0 = (int)Math.Floor((e - radius) / 1000), e1 = (int)Math.Floor((e + radius) / 1000);
        int n0 = (int)Math.Floor((n - radius) / 1000), n1 = (int)Math.Floor((n + radius) / 1000);
        for (int te = e0; te <= e1; te++)
            for (int tn = n0; tn <= n1; tn++)
            {
                // the nearest point of the square to the point
                double dx = Math.Max(0, Math.Max(te * 1000.0 - e, e - (te + 1) * 1000.0));
                double dy = Math.Max(0, Math.Max(tn * 1000.0 - n, n - (tn + 1) * 1000.0));
                if (dx * dx + dy * dy <= radius * radius) yield return (te, tn);
            }
    }

    /// <summary>
    /// Every tile round <paramref name="points"/> (each within its <see cref="RadiusFor"/>), sorted,
    /// each once: the ground a shot shows, whose content hashes make its terrain fingerprint (#924).
    /// </summary>
    public static SortedSet<(int E, int N)> Tiles(IEnumerable<Need> points)
    {
        var all = new SortedSet<(int, int)>();
        foreach (var p in points)
            foreach (var t in TilesNear(p.E, p.N, RadiusFor(p))) all.Add(t);
        return all;
    }

    /// <summary>The radius a point needs: an eye high up sees further.</summary>
    public static double RadiusFor(Need p) => p.Eye && p.Alt > HighEye ? HighRadius : Radius;

    /// <summary>The tiles round <paramref name="points"/> that are not real (<paramref name="real"/> false), sorted, each once.</summary>
    public static List<(int E, int N)> Missing(IEnumerable<Need> points, Func<int, int, bool> real) =>
        Missing(points, real, swiss: null, out _);

    /// <summary>
    /// As above, but a tile <paramref name="swiss"/> says has no Swiss survey (abroad) is not missing:
    /// it goes to <paramref name="abroad"/> instead. A null <paramref name="swiss"/> takes every tile as Swiss.
    /// </summary>
    public static List<(int E, int N)> Missing(IEnumerable<Need> points, Func<int, int, bool> real,
        Func<int, int, bool>? swiss, out List<(int E, int N)> abroad)
    {
        var missing = new SortedSet<(int, int)>();
        var outside = new SortedSet<(int, int)>();
        foreach (var p in points)
            foreach (var t in TilesNear(p.E, p.N, RadiusFor(p)))
            {
                if (real(t.E, t.N)) continue;
                if (swiss != null && !swiss(t.E, t.N)) outside.Add(t);
                else missing.Add(t);
            }
        abroad = outside.ToList();
        return missing.ToList();
    }

    // ---- on cube leaves (#1514, G2 C5) -------------------------------------------------------------

    /// <summary>
    /// The leaves (at <paramref name="level"/>, 14 for the real world's) within <paramref name="radius"/> m of a need,
    /// on the level plane at the place: what <c>--frame earth</c> must hold real round a shot, as
    /// <see cref="TilesNear"/> is in the flat world. A lattice of points every <paramref name="step"/> m over the
    /// disc and its rim, each's leaf; a leaf is ~600 m, so 150 m misses none that reaches into the disc by more.
    /// </summary>
    public static IEnumerable<CubeKey> LeavesNear(Need p, double radius, int level = 14, double step = 150)
    {
        var at = SwissGeodesy.ToEcef(p.E, p.N, double.IsNaN(p.Alt) ? 500 : p.Alt);
        var enu = new EnuFrame(Ellipsoid.Wgs84.ToGeodetic(at));
        var seen = new HashSet<CubeKey>();
        int n = (int)Math.Ceiling(radius / step);
        for (int i = -n; i <= n; i++)
            for (int j = -n; j <= n; j++)
            {
                double x = i * step, z = j * step;
                if (x * x + z * z > radius * radius) continue;
                var key = CubeKey.FromEcef(enu.ToEcef(x, 0, z), level);
                if (seen.Add(key)) yield return key;
            }
        for (int k = 0; k < 64; k++)
        {
            double a = k * Math.PI / 32;
            var rim = CubeKey.FromEcef(enu.ToEcef(Math.Sin(a) * radius, 0, Math.Cos(a) * radius), level);
            if (seen.Add(rim)) yield return rim;
        }
    }

    /// <summary>The leaves round <paramref name="points"/> (each within its <see cref="RadiusFor"/>) that are not real, sorted, each once.</summary>
    public static List<CubeKey> MissingLeaves(IEnumerable<Need> points, Func<CubeKey, bool> real, int level = 14)
    {
        var missing = new SortedSet<CubeKey>(Comparer<CubeKey>.Create((a, b) =>
            (a.Face, a.Level, a.Y, a.X).CompareTo((b.Face, b.Level, b.Y, b.X))));
        foreach (var p in points)
            foreach (var leaf in LeavesNear(p, RadiusFor(p), level))
                if (!real(leaf)) missing.Add(leaf);
        return missing.ToList();
    }

    /// <summary>"E-N", one tile a line, as <c>MapSetup --tiles-file</c> reads them.</summary>
    public static string TilesFile(IEnumerable<(int E, int N)> tiles) =>
        string.Concat(tiles.Select(t => $"{t.E}-{t.N}\n"));
}
