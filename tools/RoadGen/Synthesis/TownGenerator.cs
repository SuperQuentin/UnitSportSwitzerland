namespace UnitSport.Tools.RoadGen.Synthesis;

using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Grows a whole settlement: a tensor field, then streets traced through it at three
/// decreasing separations so the network comes out hierarchical rather than uniform.
///
/// <para>
/// Tracing every street at one separation is the usual failure of procedural road networks —
/// it produces a mesh in which every road is equally important, which no real place has.
/// Growing generations coarse-to-fine, and letting each generation snap onto the last, gives
/// through routes that run the length of the town and lanes that only ever connect two of them.
/// </para>
/// </summary>
public static class TownGenerator
{
    public sealed record TownOptions(
        int Seed = 1,
        double MajorSeparation = 260,
        double MinorSeparation = 95,
        double LaneSeparation = 45,
        double NoiseAmplitude = 0.22,
        bool RadialCentre = true,
        Func<Vec2, double>? Height = null);

    public static (RoadNetwork Network, TensorField Field) Generate(Bounds bounds, TownOptions? options = null)
    {
        var opts = options ?? new TownOptions();
        var random = new Random(opts.Seed);
        var centre = bounds.Centre;
        double span = Math.Max(bounds.Width, bounds.Height);

        var field = new TensorField
        {
            NoiseAmplitude = opts.NoiseAmplitude,
            NoiseScale = span * 0.45,
        };

        // one weak global grid so the field is never empty, plus a couple of local ones at
        // their own angles — this is what gives a town districts that meet at odd angles
        field.Fields.Add(new GridField { Heading = random.NextDouble() * Math.PI, Decay = 0, Weight = 0.35 });

        for (int i = 0; i < 2; i++)
        {
            var at = new Vec2(
                bounds.MinX + random.NextDouble() * bounds.Width,
                bounds.MinY + random.NextDouble() * bounds.Height);
            field.Fields.Add(new GridField
            {
                Heading = random.NextDouble() * Math.PI,
                Centre = at,
                Decay = 1.0 / (span * span * 0.08),
                Weight = 1.0,
            });
        }

        if (opts.RadialCentre)
            field.Fields.Add(new RadialField
            {
                Centre = centre,
                Decay = 1.0 / (span * span * 0.02),
                Weight = 1.4,
            });

        if (opts.Height is not null)
            field.Fields.Add(new TerrainField
            {
                Height = opts.Height,
                Decay = 0,
                Weight = 2.2,          // strong: on a real slope the terrain should win
                FullStrengthGrade = 0.12,
            });

        var network = new RoadNetwork();

        Generation(field, bounds, network, opts.MajorSeparation, RoadProfile.Major, RoadProfile.Road,
            opts.Seed * 7 + 1, maxStreets: 6);
        Generation(field, bounds, network, opts.MinorSeparation, RoadProfile.Minor, RoadProfile.Minor,
            opts.Seed * 7 + 2, maxStreets: 40, seeded: network);
        Generation(field, bounds, network, opts.LaneSeparation, RoadProfile.Lane, RoadProfile.Lane,
            opts.Seed * 7 + 3, maxStreets: 90, seeded: network);

        return (network, field);
    }

    private static void Generation(TensorField field, Bounds bounds, RoadNetwork network,
        double separation, RoadProfile majorProfile, RoadProfile minorProfile,
        int seed, int maxStreets, RoadNetwork? seeded = null)
    {
        var tracer = new StreetTracer(bounds, new TraceOptions(
            StepLength: Math.Max(3.0, separation / 25),
            Separation: separation,
            MaxLength: Math.Max(bounds.Width, bounds.Height) * 2.5));

        // later generations must see the earlier ones, or they trace straight through them
        if (seeded is not null)
            foreach (var link in seeded.Links)
                tracer.Register(link.Centreline);
        int alreadyThere = tracer.Streets.Count;

        tracer.Grow(field, useMajor: true, separation, maxStreets / 2, seed);
        int afterMajor = tracer.Streets.Count;
        tracer.Grow(field, useMajor: false, separation, maxStreets, seed + 1000);

        for (int i = alreadyThere; i < tracer.Streets.Count; i++)
        {
            var street = tracer.Streets[i];
            if (street.Count < 2) continue;
            network.AddLink(street, i < afterMajor ? majorProfile : minorProfile);
        }
    }

    /// <summary>
    /// A stand-in landscape for running the synthesiser with no data: a broad valley with a
    /// couple of spurs. Enough relief for the terrain field to have something to say.
    /// </summary>
    public static Func<Vec2, double> DemoTerrain(Bounds bounds)
    {
        var centre = bounds.Centre;
        double span = Math.Max(bounds.Width, bounds.Height);

        return p =>
        {
            double u = (p.X - centre.X) / span;
            double v = (p.Y - centre.Y) / span;

            double valley = 220 * (u * u) * 4;                       // floor rising to both sides
            double ridge = 90 * Math.Exp(-((u - 0.35) * (u - 0.35) + (v - 0.3) * (v - 0.3)) * 26);
            double knoll = 55 * Math.Exp(-((u + 0.3) * (u + 0.3) + (v + 0.25) * (v + 0.25)) * 40);
            double tilt = 60 * v;

            return 500 + valley + ridge + knoll + tilt;
        };
    }
}
