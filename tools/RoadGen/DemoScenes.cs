namespace UnitSport.Tools.RoadGen;

using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// Hand-built networks that each isolate one thing the generator has to get right. They run
/// with no terrain, no swissTLM3D and no Godot, which is the point: a junction bug should be
/// reproducible in a second, not after a fourteen-minute region rebuild.
/// </summary>
public static class DemoScenes
{
    public static RoadNetwork Build(string name) => name switch
    {
        "crossroads" => Crossroads(),
        "exit" => MotorwayExit(),
        "hairpin" => Hairpin(),
        "village" => Village(),
        _ => throw new ArgumentException(
            $"unknown scene '{name}'; try crossroads, exit, hairpin or village"),
    };

    public static readonly string[] All = { "crossroads", "exit", "hairpin", "village" };

    /// <summary>
    /// Four arms of three different widths at uneven angles. The plain case, and the one where
    /// overlapping ribbons are most obvious: without trimming, the middle is painted four times.
    /// </summary>
    private static RoadNetwork Crossroads()
    {
        var net = new RoadNetwork();
        var centre = new Vec2(0, 0);

        Add(net, centre, 0, 140, RoadProfile.Major);
        Add(net, centre, Math.PI, 140, RoadProfile.Major);
        Add(net, centre, Math.PI / 2 * 0.92, 120, RoadProfile.Road);
        Add(net, centre, -Math.PI / 2 * 1.08, 120, RoadProfile.Minor);

        return net;
    }

    /// <summary>
    /// A motorway with an exit peeling off at a shallow angle — the case the user reported as
    /// "junction issues when there is a highway exit". The edge intersection between two arms
    /// 6° apart is over a hundred metres out, so this is the scene that exercises the trim
    /// ceiling. Arms that hit it are drawn in red by the SVG writer.
    /// </summary>
    private static RoadNetwork MotorwayExit()
    {
        var net = new RoadNetwork();
        var divergence = new Vec2(0, 0);

        net.AddLink(new List<Vec2> { new(-420, 0), divergence }, RoadProfile.Motorway);
        net.AddLink(new List<Vec2> { divergence, new(420, 0) }, RoadProfile.Motorway);

        // the ramp leaves at ~6° and then curves away, the way a real diverge does
        var ramp = new List<Vec2> { divergence };
        for (int i = 1; i <= 22; i++)
        {
            double t = i / 22.0;
            double x = t * 380;
            ramp.Add(new Vec2(x, -(x * Math.Tan(6 * Math.PI / 180) + 90 * t * t * t)));
        }
        net.AddLink(ramp, RoadProfile.Ramp);

        return net;
    }

    /// <summary>
    /// Alpine switchbacks: corners far tighter than any design radius, on short legs. Tests
    /// that the radius solver backs off instead of inventing a curve that does not fit — a
    /// fixed-radius fillet here would cut the corner clean off the mountain.
    /// </summary>
    private static RoadNetwork Hairpin()
    {
        var net = new RoadNetwork();
        var points = new List<Vec2>();

        double y = 0;
        for (int i = 0; i < 7; i++)
        {
            double x = i % 2 == 0 ? -85 : 85;
            points.Add(new Vec2(x, y));
            y += 55;
            points.Add(new Vec2(x * 1.12, y));
            y += 22;
        }

        net.AddLink(points, RoadProfile.Minor);
        return net;
    }

    /// <summary>
    /// A through road with side streets that end against its flank, plus a curved lane. Tests
    /// T-junction splitting: nothing here shares an endpoint until the builder makes it so.
    /// </summary>
    private static RoadNetwork Village()
    {
        var net = new RoadNetwork();

        var through = new List<Vec2>();
        for (int i = 0; i <= 26; i++)
        {
            double x = -320 + i * 26;
            through.Add(new Vec2(x, 34 * Math.Sin(x / 150.0)));
        }
        net.AddLink(through, RoadProfile.Road);

        // side streets stop *on* the through road, not at a shared vertex
        foreach (double x in new double[] { -190, -60, 95, 215 })
        {
            double onRoad = 34 * Math.Sin(x / 150.0);
            net.AddLink(new List<Vec2> { new(x, onRoad), new(x + 18, onRoad + 150) }, RoadProfile.Minor);
        }

        var lane = new List<Vec2>();
        for (int i = 0; i <= 16; i++)
        {
            double t = i / 16.0;
            lane.Add(new Vec2(-172 + t * 290, 150 + 70 * Math.Sin(t * Math.PI)));
        }
        net.AddLink(lane, RoadProfile.Lane);

        return net;
    }

    private static void Add(RoadNetwork net, Vec2 from, double heading, double length, RoadProfile profile)
    {
        var direction = Vec2.FromHeading(heading);
        // a slight bend so the arm is not a perfect ray — straight test cases hide bugs that
        // only appear once an alignment has more than one piece
        var mid = from + direction * (length * 0.5) + direction.Perp * (length * 0.06);
        net.AddLink(new List<Vec2> { from, mid, from + direction * length }, profile);
    }
}
