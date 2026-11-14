using System;
using System.Collections.Generic;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Bodies;

// Plain C# (linked into the unit tests).

/// <summary>A colony's look and programme (docs/plans/moon-surface.md section 4).</summary>
public enum ColonyTheme : byte
{
    /// <summary>NASA's Artemis base: white panels, blue trims, the US flag.</summary>
    Artemis,
    /// <summary>ESA's Moon Village: pale ribbed shells, ESA blue, the member flags.</summary>
    MoonVillage,
    /// <summary>The China/Russia ILRS: red and gold trims, the two flags.</summary>
    Ilrs,
    /// <summary>The abandoned colony in a lava tube: dark, half-buried, broken lights.</summary>
    Abandoned,
    /// <summary>The 1970s Cold War base: olive, rivets, stencils, an overgrown biodome.</summary>
    ColdWar,
}

/// <summary>
/// A colony on the Moon (#1940): its centre at selenographic latitude and longitude (LOLA's frame), its theme, the seed
/// its plan grows from, the plateau it stands on (a <see cref="StampMode.Level"/> disc) and its heading (the main spine's
/// direction, degrees from north).
/// </summary>
public sealed record MoonColony(string Id, string Name, ColonyTheme Theme, double Lat, double Lon, int Seed,
    double PlateauM, double Heading, bool Staffed)
{
    public EcefPos Normal => CubeSphere.NormalAt(Lat, Lon);
}

/// <summary>
/// The five colonies (#1940, docs/plans/moon-surface.md section 4): few on the whole Moon, by the user's choice. Their
/// centres were picked with <c>--moonsurvey</c> (the flattest disc near each candidate on LOLA's heights): the numbers
/// are committed, the ground does not change.
/// </summary>
public static class MoonColonies
{
    /// <summary>The table. Artemis Base is the main one (the spaceport); the colonies of later phases follow it.</summary>
    public static readonly MoonColony[] All =
    [
        // high ground 19 km from the pole between Shackleton and de Gerlache (1,498 m, long sunlit): --moonsurvey
        // -89.45,-137,6,450 found it the flattest 450 m disc there (6.3 m RMS round a plane tilted 0.4°)
        new("artemis", "Artemis Base", ColonyTheme.Artemis, ArtemisLat, ArtemisLon, 1001, 420, ArtemisHeading, Staffed: true),
        // the mare 2.1 km north-east of Tranquility Base, outside its heritage zone: --moonsurvey 0.70,23.56,3,420
        // (1.0 m RMS round a plane tilted 0.01°, -1,952 m); the pads lead on away from Apollo 11
        new("moonvillage", "Moon Village", ColonyTheme.MoonVillage, 0.7198, 23.5270, 2002, 420, 45, Staffed: true),
        // Von Karman's mare floor on the far side, 18 km north-west of Chang'e 4: --moonsurvey -45.0,176.6,8,420
        // (0.22 m RMS, 0.02°, -5,914 m); radio-quiet, the telescope 4.7 km south-west
        new("ilrs", "ILRS", ColonyTheme.Ilrs, -44.8549, 176.7306, 3003, 420, 300, Staffed: true),
    ];

    /// <summary>Artemis Base's centre and main spine (from <c>--moonsurvey</c>).</summary>
    public const double ArtemisLat = -89.3840, ArtemisLon = -150.0549, ArtemisHeading = 40;

    /// <summary>The ground's stamps: each colony's level plateau, eased back to the natural ground over 150 m.</summary>
    public static IEnumerable<SurfaceStamp> Stamps()
    {
        foreach (var c in All) yield return new SurfaceStamp(c.Normal, null, c.PlateauM, 150, StampMode.Level);
        foreach (var o in MoonOutposts.All) yield return o.Stamp;
    }

    public static MoonColony? Find(string id)
    {
        foreach (var c in All)
            if (c.Id == id) return c;
        return null;
    }

    /// <summary>
    /// The flattest discs of radius <paramref name="radiusM"/> within <paramref name="searchM"/> of a place, best first:
    /// for each candidate centre on a <paramref name="stepM"/> grid, the RMS of the heights round a plane fitted through
    /// them (how much a plateau would cut and fill) and the plane's tilt. <paramref name="height"/> is the ground at a unit
    /// normal (the data's).
    /// </summary>
    public static List<(double Lat, double Lon, double Rms, double TiltDeg, double Height)> Flattest(Func<EcefPos, double> height,
        double lat, double lon, double searchM, double radiusM, double stepM = 200, int keep = 5)
    {
        var found = new List<(double, double, double, double, double)>();
        var probe = new MoonSite("survey", "survey", MoonSiteKind.Flag, lat, lon, []);
        int n = (int)(searchM / stepM);
        for (int j = -n; j <= n; j++)
            for (int i = -n; i <= n; i++)
            {
                double ce = i * stepM, cn = j * stepM;
                if (ce * ce + cn * cn > searchM * searchM) continue;
                // the disc's heights on a 9 x 9 grid inside the circle; a plane h = a + b e + c n by least squares
                var pts = new List<(double E, double N, double H)>();
                for (int b = -4; b <= 4; b++)
                    for (int a = -4; a <= 4; a++)
                    {
                        double e = a * radiusM / 4, nn = b * radiusM / 4;
                        if (e * e + nn * nn > radiusM * radiusM) continue;
                        pts.Add((e, nn, height(MoonSites.Offset(probe, ce + e, cn + nn))));
                    }
                var (pa, pb, pc) = Plane(pts);
                double ss = 0;
                foreach (var p in pts)
                {
                    double d = p.H - (pa + pb * p.E + pc * p.N);
                    ss += d * d;
                }
                double rms = Math.Sqrt(ss / pts.Count);
                double tilt = Math.Atan(Math.Sqrt(pb * pb + pc * pc)) * 180 / Math.PI;
                var at = MoonSites.Offset(probe, ce, cn);
                var g = Ellipsoid.Moon.ToGeodetic(new EcefPos(at.X * Ellipsoid.Moon.A, at.Y * Ellipsoid.Moon.A, at.Z * Ellipsoid.Moon.A));
                found.Add((g.LatDeg, g.LonDeg, rms, tilt, pa));
            }
        // a level plateau cuts and fills by the RMS and by the tilt across its width: both count
        found.Sort((x, y) => (x.Item3 + Math.Tan(x.Item4 * Math.PI / 180) * radiusM).CompareTo(y.Item3 + Math.Tan(y.Item4 * Math.PI / 180) * radiusM));
        return found.GetRange(0, Math.Min(keep, found.Count));
    }

    private static (double A, double B, double C) Plane(List<(double E, double N, double H)> pts)
    {
        // centred sums (the grid is symmetric about 0, so the cross terms vanish but are kept for the clipped disc)
        double se = 0, sn = 0, sh = 0, see = 0, snn = 0, sen = 0, seh = 0, snh = 0;
        foreach (var p in pts)
        {
            se += p.E; sn += p.N; sh += p.H;
            see += p.E * p.E; snn += p.N * p.N; sen += p.E * p.N; seh += p.E * p.H; snh += p.N * p.H;
        }
        double k = pts.Count;
        double me = se / k, mn = sn / k, mh = sh / k;
        double cee = see / k - me * me, cnn = snn / k - mn * mn, cen = sen / k - me * mn;
        double ceh = seh / k - me * mh, cnh = snh / k - mn * mh;
        double det = cee * cnn - cen * cen;
        double b = det == 0 ? 0 : (ceh * cnn - cnh * cen) / det;
        double c = det == 0 ? 0 : (cnh * cee - ceh * cen) / det;
        return (mh - b * me - c * mn, b, c);
    }
}
