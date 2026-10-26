using System;
using System.Collections.Generic;
using System.Linq;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Places;

// Plain C#, no Godot: linked into the unit tests (AtlasProgressTests; docs/notes/adventure/atlas.md).

/// <summary>
/// The Swiss Atlas's six chapters (#1224, places plan 7.2): a presentation grouping of the 26 cantons, none
/// in two. A chapter at 100 % of its landmarks raises <see cref="ChapterCompleted"/> once.
/// </summary>
public static class AtlasChapters
{
    public static readonly (string Name, string[] Cantons)[] All =
    {
        ("Romandie", new[] { "GE", "VD", "NE", "JU", "FR" }),
        ("Mittelland", new[] { "BE", "SO", "AG", "BL", "BS", "ZH" }),
        ("Central Switzerland", new[] { "LU", "ZG", "SZ", "UR", "OW", "NW" }),
        ("Eastern Switzerland", new[] { "SH", "TG", "SG", "AR", "AI", "GL" }),
        ("Ticino", new[] { "TI" }),
        ("Alps", new[] { "VS", "GR" }),
    };

    /// <summary>The chapter a canton is in, or null (outside Switzerland).</summary>
    public static string? Of(string canton)
    {
        foreach (var (name, cantons) in All)
            if (Array.IndexOf(cantons, canton) >= 0) return name;
        return null;
    }

    public static string[] CantonsOf(string chapter)
    {
        foreach (var (name, cantons) in All)
            if (name == chapter) return cantons;
        return Array.Empty<string>();
    }

    /// <summary>The cantons' full names, for the Atlas headers.</summary>
    public static string NameOf(string code) => code switch
    {
        "GE" => "Geneva", "VD" => "Vaud", "NE" => "Neuchâtel", "JU" => "Jura", "FR" => "Fribourg",
        "BE" => "Bern", "SO" => "Solothurn", "AG" => "Aargau", "BL" => "Basel-Landschaft", "BS" => "Basel-Stadt",
        "ZH" => "Zurich", "LU" => "Lucerne", "ZG" => "Zug", "SZ" => "Schwyz", "UR" => "Uri", "OW" => "Obwalden",
        "NW" => "Nidwalden", "SH" => "Schaffhausen", "TG" => "Thurgau", "SG" => "St. Gallen",
        "AR" => "Appenzell Ausserrhoden", "AI" => "Appenzell Innerrhoden", "GL" => "Glarus", "TI" => "Ticino",
        "VS" => "Valais", "GR" => "Graubünden",
        _ => code,
    };
}

/// <summary>
/// Completion of the Atlas (#1224, places plan 7.3), counted against this server's catalogue (a region
/// server has part of Switzerland: "of this map"): per canton the landmarks (tier 1 and 2) found, the fog
/// cells revealed; the milestones at 25, 50, 75 and 100 %, each raised once.
/// </summary>
public static class AtlasProgress
{
    public static readonly int[] Steps = { 25, 50, 75, 100 };

    /// <summary>Landmarks of a canton (tier 1 and 2) found, and how many this map has.</summary>
    public static (int Found, int Total) Landmarks(PoiCatalogue cat, PlacesProgress p, string canton)
    {
        int found = 0, total = 0;
        foreach (var poi in cat.InCanton(canton))
        {
            if (poi.Tier is < 1 or > 2) continue;
            total++;
            if (p.Found.ContainsKey(poi.Id)) found++;
        }
        return (found, total);
    }

    public static int Percent(int found, int total) => total <= 0 ? 0 : (int)Math.Floor(100.0 * found / total);

    /// <summary>
    /// After a stamp in <paramref name="canton"/>: the milestone steps it has now crossed for the first time
    /// (written to <see cref="PlacesProgress.Milestones"/>), and the chapter completed by it, if any (written as
    /// <c>chapter:&lt;name&gt;</c>). The caller touches the section and raises the events.
    /// </summary>
    public static (List<int> Steps, string? Chapter) Check(PoiCatalogue cat, PlacesProgress p, string canton)
    {
        var crossed = new List<int>();
        if (string.IsNullOrEmpty(canton)) return (crossed, null);
        var (found, total) = Landmarks(cat, p, canton);
        if (total == 0) return (crossed, null);
        int percent = Percent(found, total), had = p.Milestones.GetValueOrDefault(canton);
        foreach (int step in Steps)
            if (step > had && percent >= step) crossed.Add(step);
        if (crossed.Count > 0) p.Milestones[canton] = crossed[^1];
        string? chapter = null;
        if (percent >= 100 && AtlasChapters.Of(canton) is { } name && !p.Milestones.ContainsKey("chapter:" + name))
        {
            // every canton of the chapter this map has, complete
            bool all = true;
            foreach (string c in AtlasChapters.CantonsOf(name))
            {
                var (f, t) = Landmarks(cat, p, c);
                if (t > 0 && f < t) { all = false; break; }
            }
            if (all)
            {
                p.Milestones["chapter:" + name] = 100;
                chapter = name;
            }
        }
        return (crossed, chapter);
    }

    /// <summary>
    /// The revealed land cells per canton. A cell's canton is the canton of the nearest POI within 2 km of its
    /// centre (the file holds no canton lattice; a good approximation where places are dense); a cell inside a
    /// lake outline is water and not counted.
    /// </summary>
    public static Dictionary<string, int> RevealedLand(FogMask fog, PoiCatalogue cat)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var near = new List<Poi>();
        fog.CountWhere((col, row) =>
        {
            var (e, n) = FogMask.CentreOf(col, row);
            cat.Near(e, n, 2000, near);
            string? canton = null;
            double best = double.MaxValue;
            foreach (var p in near)
            {
                if (p.Outline != null)
                {
                    if (PoiCatalogue.InLake(p, e, n, 0)) return false;
                    continue;
                }
                if (p.Canton.Length == 0) continue;
                double d = (p.E - e) * (p.E - e) + (p.N - n) * (p.N - n);
                if (d < best) { best = d; canton = p.Canton; }
            }
            if (canton != null) counts[canton] = counts.GetValueOrDefault(canton) + 1;
            return false;
        });
        return counts;
    }
}
