using System;
using System.Collections.Generic;
using UnitSport.Adventure.Places;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Rewards;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/rewards.md).

/// <summary>
/// A famous view (#1227, rewards plan 7): where to stand (a disc, LV95) and what to frame (a point and its
/// height). <see cref="Review"/>: drafted by Claude from memory of the places, for the team to check on the map.
/// </summary>
public sealed record PhotoSpot(string Id, string Name, double StandE, double StandN, float Radius,
    double TargetE, double TargetN, float TargetAlt, string Target, string? PoiId = null, bool Review = false);

/// <summary>Why a photo did or did not count.</summary>
public enum PhotoVerdict : byte { Taken, NotThere, OffTarget, Blocked, Unknown }

/// <summary>
/// The photo spots (#1227): ~30 curated views and one per viewpoint POI, the same list on every peer.
/// </summary>
public static class PhotoSpots
{
    public const float StandRadius = 150;
    /// <summary>The target must lie inside this share of the frame's half-angle (the plan's 80 %).</summary>
    public const float FrameShare = 0.8f;
    /// <summary>Line of sight is sampled every 50 m along the coarse terrain.</summary>
    public const float SightStep = 50;

    private static PhotoSpot W(string id, string name, double standLat, double standLon, double targetLat, double targetLon, float targetAlt, string target)
    {
        var (se, sn) = SwissProjection.ToLv95(standLat, standLon);
        var (te, tn) = SwissProjection.ToLv95(targetLat, targetLon);
        return new PhotoSpot("ph:" + id, name, Math.Round(se), Math.Round(sn), StandRadius, Math.Round(te), Math.Round(tn), targetAlt, target, Review: true);
    }

    /// <summary>
    /// The curated views, drafted by Claude for the team to review (decided Oct 2026): positions from WGS84 as
    /// remembered, converted with <see cref="SwissProjection"/>; each is marked <see cref="PhotoSpot.Review"/> until a
    /// person has stood there on the map. <c>docs/notes/adventure/photo-spots.md</c> lists them with what to check.
    /// </summary>
    public static readonly PhotoSpot[] Curated =
    {
        // ---- the west (the dev region's tiles) ----
        W("chillon", "Château de Chillon from the lakeside path", 46.4168, 6.9301, 46.4142, 6.9275, 395, "Château de Chillon"),
        W("lavaux", "Lavaux terraces over the Léman", 46.4874, 6.7806, 46.4380, 6.7300, 372, "Lac Léman"),
        W("gruyeres", "Château de Gruyères from the village street", 46.5837, 7.0820, 46.5848, 7.0823, 845, "Château de Gruyères"),
        W("moleson", "Gruyères castle from the Moléson", 46.5480, 7.0172, 46.5848, 7.0823, 845, "Château de Gruyères"),
        W("bern-rosengarten", "Bern's old town from the Rosengarten", 46.9524, 7.4604, 46.9472, 7.4511, 600, "Berner Münster"),
        W("zytglogge", "The Zytglogge from the Kramgasse", 46.9478, 7.4497, 46.9480, 7.4475, 575, "Zytglogge"),
        W("jet-deau", "The Jet d'Eau from the Pont du Mont-Blanc", 46.2078, 6.1478, 46.2074, 6.1557, 440, "Jet d'Eau"),
        W("fribourg", "Fribourg's cathedral from the Zähringen bridge", 46.8072, 7.1672, 46.8062, 7.1631, 660, "Cathédrale Saint-Nicolas"),
        W("rochers-de-naye", "The Léman from the Rochers de Naye", 46.4318, 6.9763, 46.4300, 6.8800, 372, "Lac Léman"),
        W("stockhorn", "The Niesen from the Stockhorn", 46.6941, 7.5380, 46.6450, 7.6517, 2362, "Niesen"),
        // ---- the Alps ----
        W("matterhorn-riffelsee", "The Matterhorn in the Riffelsee", 45.9838, 7.7535, 45.9764, 7.6586, 4478, "Matterhorn"),
        W("matterhorn-zermatt", "The Matterhorn from Zermatt's Kirchbrücke", 46.0194, 7.7480, 45.9764, 7.6586, 4478, "Matterhorn"),
        W("gornergrat", "Monte Rosa from the Gornergrat", 45.9836, 7.7842, 45.9369, 7.8669, 4634, "Dufourspitze"),
        W("eggishorn", "The Aletsch glacier from the Eggishorn", 46.4300, 8.0940, 46.5000, 8.0250, 2750, "Konkordiaplatz"),
        W("kleine-scheidegg", "The Eiger's north face from the Kleine Scheidegg", 46.5853, 7.9614, 46.5776, 8.0053, 3967, "Eiger"),
        W("interlaken", "The Jungfrau from the Höhematte", 46.6862, 7.8556, 46.5368, 7.9626, 4158, "Jungfrau"),
        W("staubbach", "The Staubbach falls over Lauterbrunnen", 46.5925, 7.9080, 46.5966, 7.9058, 900, "Staubbachfall"),
        W("bachalpsee", "The Schreckhorn over the Bachalpsee", 46.6693, 8.0228, 46.5899, 8.1185, 4078, "Schreckhorn"),
        W("oeschinensee", "The Oeschinensee from the Heuberg path", 46.5003, 7.7385, 46.4985, 7.7275, 1578, "Oeschinensee"),
        W("diavolezza", "Piz Bernina from the Diavolezza", 46.4116, 9.9700, 46.3823, 9.9081, 4049, "Piz Bernina"),
        W("seealpsee", "The Säntis over the Seealpsee", 47.2684, 9.3966, 47.2494, 9.3433, 2502, "Säntis"),
        W("rigi-kulm", "The Pilatus from the Rigi Kulm", 47.0563, 8.4853, 46.9775, 8.2483, 2128, "Pilatus"),
        W("valere", "Valère and Tourbillon over Sion", 46.2318, 7.3606, 46.2338, 7.3653, 620, "Basilique de Valère"),
        W("landwasser", "The Landwasser viaduct", 46.6798, 9.6745, 46.6806, 9.6764, 1000, "Landwasserviadukt"),
        // ---- towns and lakes ----
        W("kapellbruecke", "The Kapellbrücke and its water tower", 47.0522, 8.3100, 47.0517, 8.3075, 440, "Kapellbrücke"),
        W("pilatus-luzern", "The Pilatus over Lucerne's lake front", 47.0525, 8.3110, 46.9775, 8.2483, 2128, "Pilatus"),
        W("rheinfall", "The Rheinfall from Schloss Laufen", 47.6763, 8.6147, 47.6778, 8.6153, 385, "Rheinfall"),
        W("grossmuenster", "The Grossmünster from the Münsterbrücke", 47.3698, 8.5413, 47.3700, 8.5441, 450, "Grossmünster"),
        W("castelgrande", "Castelgrande over Bellinzona", 46.1931, 9.0216, 46.1938, 9.0204, 270, "Castelgrande"),
        W("monte-bre", "Lugano's San Salvatore from the Monte Brè", 46.0075, 8.9853, 45.9805, 8.9450, 912, "Monte San Salvatore"),
    };

    /// <summary>
    /// The spot a viewpoint POI makes: stand at it, frame the highest landmark peak 3-25 km away that rises over the
    /// viewpoint (ties by id). Null when none.
    /// </summary>
    public static PhotoSpot? OfViewpoint(Poi vp, PoiCatalogue cat, List<Poi> scratch)
    {
        if (!vp.Is(PoiFlags.Viewpoint)) return null;
        cat.Near(vp.E, vp.N, 25000, scratch, PoiKind.Peak);
        Poi? best = null;
        foreach (var p in scratch)
        {
            double d = Math.Sqrt((p.E - vp.E) * (p.E - vp.E) + (p.N - vp.N) * (p.N - vp.N));
            if (d < 3000 || p.Tier > 2 || p.Elevation <= vp.Elevation) continue;
            if (best == null || p.Elevation > best.Elevation || p.Elevation == best.Elevation && string.CompareOrdinal(p.Id, best.Id) < 0) best = p;
        }
        return best == null ? null
            : new PhotoSpot("ph:" + vp.Id, $"{best.Name} from {vp.Name}", vp.E, vp.N, StandRadius, best.E, best.N, best.Elevation, best.Name, vp.Id);
    }

    /// <summary>Hand-placed spots of a check's world (<c>--rewardspots fixture</c>: the fixture peak from the start); empty for players.</summary>
    public static List<PhotoSpot> Extra { get; } = new();

    /// <summary>A curated spot or a viewpoint's, by id; null for an id nobody has (a forged report).</summary>
    public static PhotoSpot? Resolve(string id, PoiCatalogue cat)
    {
        foreach (var s in Curated)
            if (s.Id == id) return s;
        foreach (var s in Extra)
            if (s.Id == id) return s;
        if (!id.StartsWith("ph:", StringComparison.Ordinal) || cat.Resolve(id[3..]) is not { } vp) return null;
        return OfViewpoint(vp, cat, new List<Poi>());
    }

    /// <summary>The spots whose standing disc holds a point (curated first, then the viewpoints within 1 km).</summary>
    public static List<PhotoSpot> At(double e, double n, PoiCatalogue cat)
    {
        var list = new List<PhotoSpot>();
        foreach (var s in Curated)
            if (Inside(s, e, n)) list.Add(s);
        foreach (var s in Extra)
            if (Inside(s, e, n)) list.Add(s);
        var near = new List<Poi>();
        cat.Near(e, n, 1000, near);
        var scratch = new List<Poi>();
        foreach (var vp in near)
            if (OfViewpoint(vp, cat, scratch) is { } s && Inside(s, e, n)) list.Add(s);
        return list;
    }

    public static bool Inside(PhotoSpot s, double e, double n) =>
        (s.StandE - e) * (s.StandE - e) + (s.StandN - n) * (s.StandN - n) <= s.Radius * s.Radius;
}

/// <summary>
/// The server's judge of a photo (#1227): the body (its server position, never the client's word) stands in the
/// spot's disc; the target lies inside <see cref="PhotoSpots.FrameShare"/> of the square print's half-angles from
/// where the camera looked; and a line from the eye to it clears the coarse terrain, sampled every 50 m. Plain C#.
/// </summary>
public static class PhotoJudge
{
    /// <param name="yawDeg">Compass bearing the camera looked along (0 north, 90 east).</param>
    /// <param name="pitchDeg">Up from the horizontal.</param>
    /// <param name="fovDeg">The frame's full angle (the print is a square crop: the same both ways).</param>
    /// <param name="ground">The terrain height, null where unknown (an unloaded tile: taken as clear).</param>
    public static PhotoVerdict Judge(PhotoSpot spot, double e, double n, float eyeAlt, float yawDeg, float pitchDeg, float fovDeg,
        Func<double, double, float?> ground)
    {
        if (!PhotoSpots.Inside(spot, e, n)) return PhotoVerdict.NotThere;
        double de = spot.TargetE - e, dn = spot.TargetN - n, dist = Math.Sqrt(de * de + dn * dn);
        if (dist < 1) return PhotoVerdict.OffTarget;
        double bearing = Math.Atan2(de, dn) * 180 / Math.PI;
        double elev = Math.Atan2(spot.TargetAlt - eyeAlt, dist) * 180 / Math.PI;
        double half = fovDeg / 2 * PhotoSpots.FrameShare;
        if (Math.Abs(AngleDiff(bearing, yawDeg)) > half || Math.Abs(elev - pitchDeg) > half) return PhotoVerdict.OffTarget;
        // the sight line, past the first 30 m and short of the target's own slopes (the last 150 m)
        for (double s = 30; s < dist - 150; s += PhotoSpots.SightStep)
        {
            double t = s / dist;
            float line = (float)(eyeAlt + (spot.TargetAlt - eyeAlt) * t);
            if (ground(e + de * t, n + dn * t) is { } g && g > line + 2) return PhotoVerdict.Blocked;
        }
        return PhotoVerdict.Taken;
    }

    /// <summary>a − b folded into (−180, 180].</summary>
    public static double AngleDiff(double a, double b)
    {
        double d = (a - b) % 360;
        if (d > 180) d -= 360;
        if (d <= -180) d += 360;
        return d;
    }

    public static string Why(PhotoVerdict v) => v switch
    {
        PhotoVerdict.NotThere => "Not at a photo spot.",
        PhotoVerdict.OffTarget => "The view is not in the frame.",
        PhotoVerdict.Blocked => "Something is in the way of the view.",
        PhotoVerdict.Unknown => "No such view.",
        _ => "",
    };
}

/// <summary>The <c>photos</c> section (#1227): the photo targets this player has taken (spot ids).</summary>
public sealed class PhotosProgress
{
    public static readonly SectionKey<PhotosProgress> Section = new("photos", Version: 1);

    public HashSet<string> Taken { get; set; } = new(StringComparer.Ordinal);
}
