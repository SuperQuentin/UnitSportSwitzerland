using System;
using System.Collections.Generic;
using System.Linq;

namespace UnitSport.Trailer;

/// <summary>
/// Every scene on real terrain only, never the generated fill (#882): the 1 km tiles a shot's
/// cameras and actors need. Every tile within <see cref="Radius"/> of each camera key's eye and of
/// each actor's spot (<see cref="HighRadius"/> when the eye is more than <see cref="HighEye"/> m above
/// the sea: it sees further) must be a built real tile, one the chunk manifest lists. A tile abroad
/// (no swissALTI3D tile there: Italy, France, much of Lake Geneva's French side) has no survey to build
/// from, so it is allowed and only counted. Pure (no Godot), so tier 0 tests it.
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

    /// <summary>"E-N", one tile a line, as <c>MapSetup --tiles-file</c> reads them.</summary>
    public static string TilesFile(IEnumerable<(int E, int N)> tiles) =>
        string.Concat(tiles.Select(t => $"{t.E}-{t.N}\n"));
}
