using System;
using System.Collections.Generic;
using UnitSport.Core;

namespace UnitSport.Terrain.Bodies;

// Plain C# (linked into the unit tests).

/// <summary>
/// A named place on a body other than the Earth (#2039, the place search by body): where it is (planetocentric latitude,
/// east longitude, degrees, as <c>/goto</c> takes them), what it is, and where a traveller lands (metres east and north of
/// it: beside a lander rather than on it, past a colony's hangar rather than in its hub).
/// </summary>
public readonly record struct BodyPlace(BodyId Body, string Name, string Kind, double LatDeg, double LonDeg,
    double LandEast = 0, double LandNorth = 0);

/// <summary>
/// The places the search lists on each body but the Earth (whose towns are <c>PlaceIndex</c>'s): on the Moon its own sites,
/// colonies and outposts (<see cref="MoonSites"/>, <see cref="MoonColonies"/>, <see cref="MoonOutposts"/>); on the others a
/// short committed table of landing sites and great features (the IAU's names, positions to a few km). Pure.
/// </summary>
public static class BodyPlaces
{
    /// <summary>A body's places, in a fixed order (the table's), or none.</summary>
    public static IReadOnlyList<BodyPlace> For(BodyId body) => body == BodyId.Moon ? Moon.Value : Table.TryGetValue(body, out var l) ? l : [];

    /// <summary>The bodies that have places here, in the Sun's order (the Earth's are elsewhere).</summary>
    public static readonly BodyId[] Bodies =
        [BodyId.Moon, BodyId.Mercury, BodyId.Venus, BodyId.Mars, BodyId.Phobos, BodyId.Deimos, BodyId.Io, BodyId.Europa,
         BodyId.Ganymede, BodyId.Callisto, BodyId.Titan];

    /// <summary>Places whose name holds every word of <paramref name="query"/> (any case, any accent), table order; all for a blank one.</summary>
    public static List<BodyPlace> Search(BodyId body, string query)
    {
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var found = new List<BodyPlace>();
        foreach (var p in For(body))
        {
            string name = Fold(p.Name + " " + p.Kind);
            bool all = true;
            foreach (var w in words) all &= name.Contains(w, StringComparison.Ordinal);
            if (all) found.Add(p);
        }
        return found;
    }

    private static string Fold(string s)
    {
        var d = s.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (char c in d)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString();
    }

    private static readonly Lazy<List<BodyPlace>> Moon = new(() =>
    {
        var list = new List<BodyPlace>();
        // the five colonies first: past the hangar's ramp down the road (outside the modules), else south of the plateau
        foreach (var c in MoonColonies.All)
        {
            var plan = ColonyPlan.For(c);
            double east = 0, north = -(plan.Extent + 25);
            if (plan.Road.Count >= 2)
            {
                var (rx, rz) = plan.Road[0];
                var (ex, ez) = plan.Road[^1];
                double len = Math.Sqrt((ex - rx) * (ex - rx) + (ez - rz) * (ez - rz));
                east = rx + (ex - rx) / len * 30;
                north = -(rz + (ez - rz) / len * 30);
            }
            list.Add(new BodyPlace(BodyId.Moon, c.Name, c.Staffed ? "colony" : "abandoned colony", c.Lat, c.Lon, east, north));
        }
        foreach (var s in MoonSites.All)
            list.Add(new BodyPlace(BodyId.Moon, s.Name, s.Kind switch
            {
                MoonSiteKind.Apollo => "Apollo landing site",
                MoonSiteKind.Lander => "lander",
                MoonSiteKind.Rover => "rover",
                MoonSiteKind.Impact => "impact crater",
                MoonSiteKind.Wreck => "wreck",
                MoonSiteKind.Flag => "flag",
                _ => "site",
            }, s.Lat, s.Lon, 0, -25));
        foreach (var o in MoonOutposts.All)
            list.Add(new BodyPlace(BodyId.Moon, o.Name, "outpost", o.Lat, o.Lon, 0, -60));
        return list;
    });

    private static BodyPlace P(BodyId b, string name, string kind, double lat, double lon) => new(b, name, kind, lat, lon);

    private static readonly Dictionary<BodyId, List<BodyPlace>> Table = new()
    {
        [BodyId.Mars] =
        [
            P(BodyId.Mars, "Olympus Mons", "volcano", 18.65, 226.2),
            P(BodyId.Mars, "Valles Marineris", "canyon", -13.9, 300.8),
            P(BodyId.Mars, "Hellas Planitia", "basin", -42.4, 70.5),
            P(BodyId.Mars, "Gale crater (Curiosity)", "rover landing site", -4.59, 137.44),
            P(BodyId.Mars, "Jezero crater (Perseverance)", "rover landing site", 18.44, 77.45),
            P(BodyId.Mars, "Gusev crater (Spirit)", "rover landing site", -14.57, 175.47),
            P(BodyId.Mars, "Meridiani Planum (Opportunity)", "rover landing site", -1.95, 354.47),
            P(BodyId.Mars, "Viking 1", "lander", 22.27, 312.05),
            P(BodyId.Mars, "Viking 2", "lander", 47.67, 134.28),
            P(BodyId.Mars, "Mars Pathfinder", "lander", 19.13, 326.78),
            P(BodyId.Mars, "Phoenix", "lander", 68.22, 234.25),
            P(BodyId.Mars, "InSight", "lander", 4.50, 135.62),
            P(BodyId.Mars, "Utopia Planitia (Zhurong)", "rover landing site", 25.07, 109.93),
            P(BodyId.Mars, "Arsia Mons", "volcano", -8.26, 239.91),
            P(BodyId.Mars, "Pavonis Mons", "volcano", 1.48, 247.04),
            P(BodyId.Mars, "Ascraeus Mons", "volcano", 11.92, 255.92),
        ],
        [BodyId.Mercury] =
        [
            P(BodyId.Mercury, "Caloris Planitia", "basin", 30.5, 170.2),
            P(BodyId.Mercury, "Rachmaninoff", "crater", -27.6, 57.6),
            P(BodyId.Mercury, "MESSENGER's impact", "impact site", 54.44, 210.12),
        ],
        [BodyId.Venus] =
        [
            P(BodyId.Venus, "Maxwell Montes", "mountains", 65.2, 3.3),
            P(BodyId.Venus, "Aphrodite Terra", "highland", -5.8, 104.8),
            P(BodyId.Venus, "Venera 13", "lander", -7.55, 303.69),
            P(BodyId.Venus, "Venera 7", "lander", -5.0, 351.0),
        ],
        [BodyId.Phobos] = [P(BodyId.Phobos, "Stickney", "crater", 1.0, 49.0)],
        [BodyId.Deimos] = [P(BodyId.Deimos, "Voltaire", "crater", 22.0, 3.5)],
        [BodyId.Io] =
        [
            P(BodyId.Io, "Loki Patera", "volcano", 13.0, 51.0),
            P(BodyId.Io, "Pele", "volcano", -18.7, 104.7),
        ],
        [BodyId.Europa] =
        [
            P(BodyId.Europa, "Conamara Chaos", "chaos terrain", 9.7, 86.4),
            P(BodyId.Europa, "Pwyll", "crater", -25.2, 88.6),
        ],
        [BodyId.Ganymede] = [P(BodyId.Ganymede, "Galileo Regio", "dark region", 35.0, 145.0)],
        [BodyId.Callisto] = [P(BodyId.Callisto, "Valhalla", "impact basin", 14.7, 304.0)],
        [BodyId.Titan] =
        [
            P(BodyId.Titan, "Huygens landing site", "lander", -10.57, 167.67),
            P(BodyId.Titan, "Kraken Mare", "methane sea", 68.0, 50.0),
            P(BodyId.Titan, "Ligeia Mare", "methane sea", 79.0, 112.0),
        ],
    };
}
