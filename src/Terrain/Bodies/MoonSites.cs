using System;
using System.Collections.Generic;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Bodies;

// Plain C# (linked into the unit tests).

/// <summary>What a site is, for the log and the check.</summary>
public enum MoonSiteKind : byte { Apollo, Lander, Rover, Impact, Wreck, Flag, Colony, Industry }

/// <summary>A model a site places (each one in the model viewer, <c>World/Moon/MoonSiteMeshes</c>).</summary>
public enum MoonPart : byte
{
    /// <summary>An Apollo lunar module's descent stage: four legs, gold foil, the ladder.</summary>
    DescentStage,
    /// <summary>A US flag on its pole with the horizontal rod.</summary>
    FlagUs,
    /// <summary>Apollo 11's flag, knocked flat by the ascent stage's exhaust.</summary>
    FlagFallen,
    /// <summary>The scientific package (ALSEP / EASEP): the central station, the radio antenna, the RTG.</summary>
    Alsep,
    /// <summary>The Lunar Roving Vehicle, parked.</summary>
    Lrv,
    /// <summary>Surveyor 3, which Apollo 12's crew walked to.</summary>
    Surveyor,
    /// <summary>Luna 9's little capsule, its petals open.</summary>
    Luna9,
    /// <summary>The Luna sample-return descent stage (Luna 16, 20, 24): the round tanks and four legs.</summary>
    LunaStage,
    /// <summary>The Luna 17/21 landing stage with its two ramps.</summary>
    LunaRampStage,
    /// <summary>Lunokhod: the tub on eight wheels with its lid open.</summary>
    Lunokhod,
    /// <summary>A Chang'e lander (3 to 6): the octagonal body, four legs, its panels.</summary>
    ChangeLander,
    /// <summary>A Yutu rover: six wheels, two panels.</summary>
    Yutu,
    /// <summary>A Saturn V third stage's twisted pieces on its crater's rim.</summary>
    SivbDebris,
    /// <summary>An invented crashed rocket: the broken booster in its furrow.</summary>
    RocketWreck,
    /// <summary>Scattered tanks and panels round a wreck.</summary>
    WreckDebris,
    /// <summary>An invented flag with its plaque: <see cref="SitePart.Flag"/> names whose.</summary>
    Flag,
}

/// <summary>Whose flag an invented <see cref="MoonPart.Flag"/> is.</summary>
public enum MoonFlag : byte { Us, Switzerland, Esa, Japan, India, China, Ussr }

/// <summary>A model at a site: its offset from the site's point (metres east and north) and its heading (degrees from north).</summary>
public readonly record struct SitePart(MoonPart Part, double East, double North, double Heading, MoonFlag Flag = MoonFlag.Us);

/// <summary>
/// A site on the Moon (#1939, docs/plans/moon-surface.md section 1): selenographic latitude and longitude in the Mean
/// Earth / polar axis frame (LOLA's, so the pyramid's own craters line up), what stands there, and how the ground round
/// it is calmed.
/// </summary>
public sealed record MoonSite(string Id, string Name, MoonSiteKind Kind, double Lat, double Lon, SitePart[] Parts,
    double CalmM = 40, int Seed = 0, double CraterM = 0)
{
    /// <summary>The unit normal (body-fixed) of the site's point.</summary>
    public EcefPos Normal => CubeSphere.NormalAt(Lat, Lon);

    /// <summary>How far the farthest part is from the site's point, metres.</summary>
    public double Extent
    {
        get
        {
            double m = CraterM;
            foreach (var p in Parts) m = Math.Max(m, Math.Sqrt(p.East * p.East + p.North * p.North));
            return m;
        }
    }
}

/// <summary>
/// Every site on the Moon (#1939): the real ones at the positions the LRO Camera team published, a few invented ones.
/// Very sparse by the user's choice: a few dozen on the whole body. The offsets of the parts round a lander are
/// approximate (from the LROC traverse maps), a few metres off at worst.
///
/// <para>
/// Footpaths and rover tracks are generated from each site's seed (<see cref="Paths"/>): the same on every peer.
/// </para>
/// </summary>
public static class MoonSites
{
    private static SitePart P(MoonPart part, double e, double n, double heading = 0, MoonFlag flag = MoonFlag.Us) => new(part, e, n, heading, flag);

    /// <summary>The table. Apollo, then the Soviet and Chinese landers and rovers, the S-IVB impacts, the invented wrecks and flags.</summary>
    public static readonly MoonSite[] All =
    [
        // ---- Apollo: the descent stage, the flag, the package, the rover (15-17); the footpaths from the seed --------
        new("apollo11", "Apollo 11 (Tranquility Base)", MoonSiteKind.Apollo, 0.67416, 23.47314,
            [P(MoonPart.DescentStage, 0, 0, 270), P(MoonPart.FlagFallen, -6, 6, 30), P(MoonPart.Alsep, -12, -14, 0)], Seed: 11),
        new("apollo12", "Apollo 12", MoonSiteKind.Apollo, -3.01239, -23.42157,
            [P(MoonPart.DescentStage, 0, 0, 250), P(MoonPart.FlagUs, -8, 6), P(MoonPart.Alsep, -95, 90, 0),
             P(MoonPart.Surveyor, 115, -105, 40)], Seed: 12, CalmM: 50),
        new("apollo14", "Apollo 14", MoonSiteKind.Apollo, -3.64530, -17.47136,
            [P(MoonPart.DescentStage, 0, 0, 270), P(MoonPart.FlagUs, -10, -4), P(MoonPart.Alsep, -175, 20, 0)], Seed: 14),
        new("apollo15", "Apollo 15", MoonSiteKind.Apollo, 26.13222, 3.63386,
            [P(MoonPart.DescentStage, 0, 0, 280), P(MoonPart.FlagUs, -12, 8), P(MoonPart.Alsep, -100, 55, 0),
             P(MoonPart.Lrv, 95, 10, 250)], Seed: 15),
        new("apollo16", "Apollo 16", MoonSiteKind.Apollo, -8.97301, 15.50019,
            [P(MoonPart.DescentStage, 0, 0, 260), P(MoonPart.FlagUs, -14, 5), P(MoonPart.Alsep, -95, -5, 0),
             P(MoonPart.Lrv, 88, -12, 270)], Seed: 16),
        new("apollo17", "Apollo 17 (Taurus-Littrow)", MoonSiteKind.Apollo, 20.19080, 30.77168,
            [P(MoonPart.DescentStage, 0, 0, 265), P(MoonPart.FlagUs, -9, 10), P(MoonPart.Alsep, -185, 5, 0),
             P(MoonPart.Lrv, 150, -20, 245)], Seed: 17),

        // ---- the Soviet landers and rovers ---------------------------------------------------------------------------
        new("luna9", "Luna 9", MoonSiteKind.Lander, 7.08, -64.37, [P(MoonPart.Luna9, 0, 0, 0)], CalmM: 12),
        new("luna16", "Luna 16", MoonSiteKind.Lander, -0.5137, 56.3638, [P(MoonPart.LunaStage, 0, 0, 30)], CalmM: 15),
        new("luna17", "Luna 17", MoonSiteKind.Lander, 38.2378, -35.0017, [P(MoonPart.LunaRampStage, 0, 0, 180)], CalmM: 15, Seed: 170),
        new("lunokhod1", "Lunokhod 1", MoonSiteKind.Rover, 38.3150, -35.0081, [P(MoonPart.Lunokhod, 0, 0, 160)], CalmM: 10),
        new("luna20", "Luna 20", MoonSiteKind.Lander, 3.7866, 56.6242, [P(MoonPart.LunaStage, 0, 0, 120)], CalmM: 15),
        new("luna21", "Luna 21", MoonSiteKind.Lander, 25.8508, 30.4080, [P(MoonPart.LunaRampStage, 0, 0, 90)], CalmM: 15, Seed: 210),
        new("lunokhod2", "Lunokhod 2", MoonSiteKind.Rover, 25.8323, 30.9221, [P(MoonPart.Lunokhod, 0, 0, 75)], CalmM: 10),
        new("luna24", "Luna 24", MoonSiteKind.Lander, 12.7145, 62.2129, [P(MoonPart.LunaStage, 0, 0, 300)], CalmM: 15),

        // ---- the Chinese landers and rovers ----------------------------------------------------------------------------
        new("change3", "Chang'e 3 and Yutu", MoonSiteKind.Lander, 44.1214, -19.5116,
            [P(MoonPart.ChangeLander, 0, 0, 0), P(MoonPart.Yutu, -12, -38, 200)], Seed: 30),
        new("change4", "Chang'e 4 (Von Karman)", MoonSiteKind.Lander, -45.4446, 177.5991, [P(MoonPart.ChangeLander, 0, 0, 0)], CalmM: 20, Seed: 40),
        new("yutu2", "Yutu-2", MoonSiteKind.Rover, -45.4380, 177.5890, [P(MoonPart.Yutu, 0, 0, 300)], CalmM: 10),
        new("change5", "Chang'e 5", MoonSiteKind.Lander, 43.0576, -51.9161, [P(MoonPart.ChangeLander, 0, 0, 20)], CalmM: 20),
        new("change6", "Chang'e 6", MoonSiteKind.Lander, -41.6385, -153.9852, [P(MoonPart.ChangeLander, 0, 0, 350)], CalmM: 20),

        // ---- where the Saturn V third stages hit (a fresh crater with its rays, the pieces on its rim) --------------
        new("sivb13", "Apollo 13 S-IVB", MoonSiteKind.Impact, -2.55, -27.88, [P(MoonPart.SivbDebris, 21, 9, 70)], CalmM: 0, Seed: 113, CraterM: 17),
        new("sivb14", "Apollo 14 S-IVB", MoonSiteKind.Impact, -8.18, -26.02, [P(MoonPart.SivbDebris, -18, 14, 150)], CalmM: 0, Seed: 114, CraterM: 18),
        new("sivb15", "Apollo 15 S-IVB", MoonSiteKind.Impact, -1.30, -11.81, [P(MoonPart.SivbDebris, 13, -17, 220)], CalmM: 0, Seed: 115, CraterM: 16),
        new("sivb16", "Apollo 16 S-IVB", MoonSiteKind.Impact, 1.92, -24.55, [P(MoonPart.SivbDebris, -21, -7, 300)], CalmM: 0, Seed: 116, CraterM: 17),
        new("sivb17", "Apollo 17 S-IVB", MoonSiteKind.Impact, -4.21, -12.31, [P(MoonPart.SivbDebris, 18, 15, 20)], CalmM: 0, Seed: 117, CraterM: 18),

        // ---- invented: crashed rockets in their furrows, flags of later missions --------------------------------------
        new("wreck-imbrium", "A crashed booster (Mare Imbrium)", MoonSiteKind.Wreck, 33.40, -15.70,
            [P(MoonPart.RocketWreck, 0, 0, 62), P(MoonPart.WreckDebris, 30, 14, 0), P(MoonPart.WreckDebris, -26, -10, 120)], CalmM: 50, Seed: 901),
        new("wreck-serenitatis", "A crashed booster (Mare Serenitatis)", MoonSiteKind.Wreck, 24.90, 19.10,
            [P(MoonPart.RocketWreck, 0, 0, 215), P(MoonPart.WreckDebris, -22, 18, 40)], CalmM: 50, Seed: 902),
        new("wreck-procellarum", "A crashed booster (Oceanus Procellarum)", MoonSiteKind.Wreck, 9.40, -51.20,
            [P(MoonPart.RocketWreck, 0, 0, 340), P(MoonPart.WreckDebris, 18, -25, 200), P(MoonPart.WreckDebris, -15, 30, 80)], CalmM: 50, Seed: 903),
        new("wreck-crisium", "A crashed booster (Mare Crisium)", MoonSiteKind.Wreck, 15.60, 58.40,
            [P(MoonPart.RocketWreck, 0, 0, 105), P(MoonPart.WreckDebris, 24, 4, 300)], CalmM: 50, Seed: 904),
        new("wreck-moscoviense", "A crashed booster (Mare Moscoviense)", MoonSiteKind.Wreck, 27.30, 147.90,
            [P(MoonPart.RocketWreck, 0, 0, 30), P(MoonPart.WreckDebris, -20, -22, 10)], CalmM: 50, Seed: 905),
        new("flag-ch", "A Swiss flag (Mare Nubium)", MoonSiteKind.Flag, -21.30, -16.60, [P(MoonPart.Flag, 0, 0, 0, MoonFlag.Switzerland)], CalmM: 8),
        new("flag-esa", "An ESA flag (Mare Fecunditatis)", MoonSiteKind.Flag, -4.80, 51.30, [P(MoonPart.Flag, 0, 0, 0, MoonFlag.Esa)], CalmM: 8),
        new("flag-jp", "A Japanese flag (Mare Vaporum)", MoonSiteKind.Flag, 13.20, 3.60, [P(MoonPart.Flag, 0, 0, 0, MoonFlag.Japan)], CalmM: 8),
        new("flag-in", "An Indian flag (Mare Nectaris)", MoonSiteKind.Flag, -15.20, 34.60, [P(MoonPart.Flag, 0, 0, 0, MoonFlag.India)], CalmM: 8),
    ];

    /// <summary>A site by id, or null.</summary>
    public static MoonSite? Find(string id)
    {
        foreach (var s in All)
            if (s.Id == id) return s;
        return null;
    }

    /// <summary>
    /// The ground's stamps for every site: the generated craters calmed round each lander, rover and flag (their real
    /// ground kept); an S-IVB's fresh crater dug where it hit.
    /// </summary>
    public static IEnumerable<SurfaceStamp> Stamps()
    {
        foreach (var s in All)
        {
            if (s.CraterM > 0) yield return new SurfaceStamp(s.Normal, null, s.CraterM, 0, StampMode.Crater);
            if (s.CalmM <= 0) continue;
            // the main point, and every part farther than the calm disc (Surveyor 3, the far packages) its own small one
            yield return new SurfaceStamp(s.Normal, null, s.CalmM, s.CalmM * 0.75, StampMode.Calm);
            foreach (var p in s.Parts)
            {
                double d = Math.Sqrt(p.East * p.East + p.North * p.North);
                if (d + 6 <= s.CalmM) continue;
                yield return new SurfaceStamp(Offset(s, p.East, p.North), null, 8, 6, StampMode.Calm);
            }
        }
    }

    /// <summary>The unit normal <paramref name="east"/> and <paramref name="north"/> metres from a site's point (the Moon's sphere).</summary>
    public static EcefPos Offset(MoonSite s, double east, double north)
    {
        double r = Ellipsoid.Moon.A;
        double lat = s.Lat + north / r * 180 / Math.PI;
        double lon = s.Lon + east / (r * Math.Cos(s.Lat * Math.PI / 180)) * 180 / Math.PI;
        return CubeSphere.NormalAt(lat, lon);
    }

    /// <summary>A trace on the ground: a footpath or a rover's two tracks, as points in the site's metres (east, north).</summary>
    public readonly record struct Trace(bool Rover, (double East, double North)[] Points);

    /// <summary>
    /// The traces round a site from its seed: Apollo's footpaths between the lander, the flag and the package, a few
    /// loops out to look at things, and the rover's tracks out of sight and back to where it is parked; Lunokhod's and
    /// Yutu's tracks behind them. Pure: the same on every peer.
    /// </summary>
    public static List<Trace> Paths(MoonSite s)
    {
        var traces = new List<Trace>();
        var rng = new Random(s.Seed * 7919 + 17);
        SitePart? stage = null, lrv = null;
        foreach (var p in s.Parts)
        {
            if (p.Part == MoonPart.DescentStage) stage = p;
            if (p.Part == MoonPart.Lrv) lrv = p;
        }
        if (stage is { } lm)
        {
            // a walk from the ladder to every other part, wobbling (they hopped)
            foreach (var p in s.Parts)
            {
                if (p.Part is MoonPart.DescentStage or MoonPart.Lrv) continue;
                traces.Add(new Trace(false, Wander(rng, (lm.East - 2.5, lm.North), (p.East + 1.2, p.North + 1.2), 0.08)));
            }
            // a few loops out and back (a look into a crater, a sample stop)
            int loops = 2 + rng.Next(3);
            for (int i = 0; i < loops; i++)
            {
                double a = rng.NextDouble() * Math.Tau, r = 25 + rng.NextDouble() * 45;
                var far = (lm.East + Math.Sin(a) * r, lm.North + Math.Cos(a) * r);
                var side = (lm.East + Math.Sin(a + 0.5) * r * 0.6, lm.North + Math.Cos(a + 0.5) * r * 0.6);
                var path = new List<(double, double)>(Wander(rng, (lm.East - 2.5, lm.North), far, 0.1));
                path.AddRange(Wander(rng, far, side, 0.1)[1..]);
                path.AddRange(Wander(rng, side, (lm.East - 2.5, lm.North), 0.1)[1..]);
                traces.Add(new Trace(false, path.ToArray()));
            }
        }
        if (lrv is { } rv && stage is { } lm2)
        {
            // the rover's tracks: out on a long loop past the horizon, back to its parking spot
            double a0 = rng.NextDouble() * Math.Tau;
            var pts = new List<(double, double)> { (rv.East, rv.North) };
            var at = (rv.East, rv.North);
            for (int k = 0; k < 6; k++)
            {
                double a = a0 + k * 0.9;
                var next = (lm2.East + Math.Sin(a) * (120 + 60 * k), lm2.North + Math.Cos(a) * (120 + 60 * k));
                pts.AddRange(Wander(rng, at, next, 0.04)[1..]);
                at = next;
            }
            traces.Add(new Trace(true, pts.ToArray()));
            var back = Wander(rng, (rv.East + Math.Sin(a0 + 2.6) * 400, rv.North + Math.Cos(a0 + 2.6) * 400), (rv.East, rv.North), 0.04);
            traces.Add(new Trace(true, back));
        }
        foreach (var p in s.Parts)
            if (p.Part is MoonPart.Lunokhod or MoonPart.Yutu)
            {
                // the tracks it left coming here: from behind it, curving
                double back = (p.Heading + 180) * Math.PI / 180;
                var from = (p.East + Math.Sin(back) * 150, p.North + Math.Cos(back) * 150);
                traces.Add(new Trace(true, Wander(rng, from, (p.East, p.North), 0.05)));
            }
        return traces;
    }

    /// <summary>Points every ~1.5 m from one place to another, the line wandering sideways by <paramref name="wobble"/> of its length.</summary>
    private static (double East, double North)[] Wander(Random rng, (double E, double N) a, (double E, double N) b, double wobble)
    {
        double dx = b.E - a.E, dy = b.N - a.N, len = Math.Sqrt(dx * dx + dy * dy);
        int n = Math.Max(2, (int)(len / 1.5) + 1);
        double sx = -dy / Math.Max(len, 1e-6), sy = dx / Math.Max(len, 1e-6);
        // two sine waves of random phase: smooth, ends pinned
        double p1 = rng.NextDouble() * Math.Tau, p2 = rng.NextDouble() * Math.Tau, a1 = (rng.NextDouble() - 0.5) * 2, a2 = (rng.NextDouble() - 0.5);
        var pts = new (double, double)[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)(n - 1);
            double off = Math.Sin(t * Math.PI) * len * wobble * (a1 * Math.Sin(t * 3 + p1) + a2 * Math.Sin(t * 7 + p2));
            pts[i] = (a.E + dx * t + sx * off, a.N + dy * t + sy * off);
        }
        return pts;
    }
}
