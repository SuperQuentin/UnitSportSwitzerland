// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-framework.md).

namespace UnitSport.Occasions.Festivals;

/// <summary>A town of the region, as the relocation reads it (<c>OccasionTowns.Town</c> without Godot).</summary>
public readonly record struct TownRef(string Name, double E, double N, int Buildings);

/// <summary>Where a festival is held this year, and the line the Journal says it with.</summary>
public sealed record ResolvedSite(string Name, double E, double N, double RadiusM, bool Relocated, string? PoiId, string Text)
{
    public double DistanceTo(double e, double n) => Math.Sqrt((e - E) * (e - E) + (n - N) * (n - N));
}

/// <summary>
/// A festival is held in the real place when the region holds it, else it moves to the region's largest town
/// (#1269 section 1.2): the western dev region still gets its Fasnacht, with "held this year in Bulle". Pure.
/// </summary>
public static class FestivalSites
{
    /// <summary>A town of at least this many buildings can hold a festival: <c>OccasionTowns.MinBuildings</c> (20) x 10.</summary>
    public const int MinBuildings = 200;

    /// <summary>
    /// The first of <paramref name="def"/>'s sites that <paramref name="inRegion"/> holds, else the region's largest town with
    /// <see cref="MinBuildings"/> (ties: the name, so the server and a check agree), else null: the festival is not held here.
    /// </summary>
    public static ResolvedSite? Resolve(FestivalDef def, Func<double, double, bool> inRegion, IEnumerable<TownRef> towns)
    {
        foreach (var s in def.Sites)
            if (inRegion(s.E, s.N))
                return new ResolvedSite(s.Name, s.E, s.N, s.RadiusM, false, s.PoiId, $"{def.Title} at {s.Name}");

        TownRef? best = null;
        foreach (var t in towns)
        {
            if (t.Buildings < MinBuildings || !inRegion(t.E, t.N)) continue;
            if (best is not { } b || t.Buildings > b.Buildings
                || (t.Buildings == b.Buildings && string.CompareOrdinal(t.Name, b.Name) < 0))
                best = t;
        }
        if (best is not { } town) return null;
        double radius = def.Sites.Length > 0 ? def.Sites[0].RadiusM : 150;
        return new ResolvedSite(town.Name, town.E, town.N, radius, true, null, HeldIn(def.Title, town.Name));
    }

    /// <summary>"Älplerfest, held this year in Bulle".</summary>
    public static string HeldIn(string title, string town) => $"{title}, held this year in {town}";
}
