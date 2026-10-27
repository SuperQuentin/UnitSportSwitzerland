using System;
using System.Collections.Generic;
using System.Linq;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Versteckis;

// Plain C#, no Godot: linked into the unit tests (VersteckisTests; docs/notes/adventure/versteckis.md).

/// <summary>A town of <c>places.json</c> (name, point, buildings), as <see cref="VsArea"/> reads it.</summary>
public readonly record struct VsTown(string Name, double E, double N, int Buildings);

/// <summary>A church or chapel a game can be held round (or, from <c>/vs open here</c>, a plain point).</summary>
public sealed record VsCandidate(string Id, string Name, string Canton, double E, double N, bool Church);

/// <summary>What the server learned of a candidate's ground once its tiles are in (<see cref="VsArea.Reject"/>).</summary>
public readonly record struct VsAreaFacts(int Buildings, int Decor, float WaterShare, float SteepShare);

/// <summary>
/// Where a game is played (#1268, plan section 2), pure, as <c>BrRegion.Pick</c> is: every church or chapel POI within
/// 600 m of a village or hamlet (a <c>places.json</c> town of 15 to 600 buildings), the radius by head count, the
/// nearest or a random pick (never one of the last five centres), and the rules that reject a candidate once its
/// tiles are read.
/// </summary>
public static class VsArea
{
    public const double TownReach = 600, NearestReach = 5000, HereSnap = 1000;
    public const int MinTownBuildings = 15, MaxTownBuildings = 600;
    public const int MinBuildings = 8, MinDecor = 60;
    public const float MaxWater = 0.2f, MaxSteep = 0.3f;
    /// <summary>A rejected nearest pick tries the next, five at most, then refuses.</summary>
    public const int Tries = 5;
    public const int History = 5;

    /// <summary>The candidates: each church or chapel within <see cref="TownReach"/> of a village or a hamlet, named after it.</summary>
    public static List<VsCandidate> Candidates(IEnumerable<(string Id, string Name, PoiKind Kind, string Canton, double E, double N)> pois,
        IReadOnlyList<VsTown> towns)
    {
        var list = new List<VsCandidate>();
        foreach (var p in pois)
        {
            if (p.Kind is not (PoiKind.Church or PoiKind.Chapel)) continue;
            VsTown? best = null;
            double bestD = TownReach * TownReach;
            foreach (var t in towns)
            {
                if (t.Buildings is < MinTownBuildings or > MaxTownBuildings) continue;
                double d = (t.E - p.E) * (t.E - p.E) + (t.N - p.N) * (t.N - p.N);
                if (d <= bestD) { bestD = d; best = t; }
            }
            if (best is { } town) list.Add(new VsCandidate(p.Id, town.Name, p.Canton, p.E, p.N, p.Kind == PoiKind.Church));
        }
        return list;
    }

    /// <summary>The candidates by distance from a point, nearest first, within <paramref name="reach"/>.</summary>
    public static List<VsCandidate> Nearest(IReadOnlyList<VsCandidate> candidates, double e, double n, double reach = NearestReach) =>
        candidates.Select(c => (c, d: (c.E - e) * (c.E - e) + (c.N - n) * (c.N - n)))
            .Where(x => x.d <= reach * reach).OrderBy(x => x.d).Select(x => x.c).ToList();

    /// <summary>
    /// <c>/vs open here</c>: the nearest candidate within 1 km, else the point itself (a farm, a chalet hamlet with no
    /// church), named after the nearest town.
    /// </summary>
    public static VsCandidate Here(IReadOnlyList<VsCandidate> candidates, IReadOnlyList<VsTown> towns, double e, double n, string canton = "")
    {
        if (Nearest(candidates, e, n, HereSnap) is { Count: > 0 } near) return near[0];
        var town = towns.OrderBy(t => (t.E - e) * (t.E - e) + (t.N - n) * (t.N - n)).Select(t => (VsTown?)t).FirstOrDefault();
        return new VsCandidate($"here:{Math.Round(e)}_{Math.Round(n)}", town?.Name ?? "Here", canton, e, n, false);
    }

    /// <summary><c>/vs open random</c>: any candidate, never one within 100 m of the last five centres.</summary>
    public static VsCandidate? Random(IReadOnlyList<VsCandidate> candidates, Random rng, IReadOnlyList<(double E, double N)> history)
    {
        var fresh = candidates.Where(c => !history.Any(h => (h.E - c.E) * (h.E - c.E) + (h.N - c.N) * (h.N - c.N) < 100 * 100)).ToList();
        if (fresh.Count == 0) fresh = candidates.ToList();
        return fresh.Count == 0 ? null : fresh[rng.Next(fresh.Count)];
    }

    /// <summary>A name typed after <c>/vs open</c>: the candidate whose village starts with it (any case).</summary>
    public static VsCandidate? Named(IReadOnlyList<VsCandidate> candidates, string typed, double e, double n)
    {
        typed = typed.Trim();
        if (typed.Length == 0) return null;
        return candidates.Where(c => c.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => (c.E - e) * (c.E - e) + (c.N - n) * (c.N - n)).FirstOrDefault();
    }

    /// <summary>Whether a centre lies at least <see cref="VsRules.AreaSpacing"/> from every running game's.</summary>
    public static bool FarFromOthers(double e, double n, IEnumerable<(double E, double N)> others) =>
        others.All(o => (o.E - e) * (o.E - e) + (o.N - n) * (o.N - n) >= VsRules.AreaSpacing * VsRules.AreaSpacing);

    /// <summary>Why a candidate's ground will not do, or null: too few buildings or decor spots, too wet, too steep.</summary>
    public static string? Reject(VsAreaFacts f)
    {
        if (f.Buildings < MinBuildings) return $"only {f.Buildings} buildings";
        if (f.Decor < MinDecor) return $"only {f.Decor} places to hide";
        if (f.WaterShare > MaxWater) return "too much water";
        if (f.SteepShare > MaxSteep) return "too steep";
        return null;
    }

    /// <summary>The history kept on the server: the last five centres, oldest first.</summary>
    public static List<(double E, double N)> Remember(IReadOnlyList<(double E, double N)> history, double e, double n)
    {
        var list = history.ToList();
        list.Add((e, n));
        if (list.Count > History) list.RemoveRange(0, list.Count - History);
        return list;
    }
}
