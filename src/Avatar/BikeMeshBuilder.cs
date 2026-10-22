using Godot;
using UnitSport.Player;

namespace UnitSport.Avatar;

/// <summary>
/// A road bike built from its maker's geometry chart (#1124): <see cref="BikeFrame"/> turns a
/// <see cref="BicycleSpec"/>'s stack, reach, angles, chainstay and fork offset into points, and this
/// draws tubes between them. Nothing is placed by eye, because a bike is a shape everybody knows,
/// and the saddle and bars have to be where the rider's hips and hands are, or the cyclist floats.
///
/// <para>
/// Built facing +Z, origin on the ground with the bottom bracket 2 cm behind it, so it drops
/// straight into the world beside a <see cref="HumanPose.Cycling"/> figure with no offset.
/// </para>
/// </summary>
public static class BikeMeshBuilder
{
    private const float TyreThickness = 0.028f;
    private const float RimDepth = 0.032f;

    public static ArrayMesh Build(BikePalette? palette = null, bool includeCranks = true, BikeFrame? frame = null)
    {
        var f = frame ?? BikeFrame.Default;
        var p = palette ?? f.Spec.Look;
        var s = new MeshScratch();

        bool disc = f.Spec.Brakes == BikeBrakes.Disc;
        Wheel(s, f, f.RearAxle, p, disc ? 0.070f : 0f);
        Wheel(s, f, f.FrontAxle, p, disc ? 0.080f : 0f);

        // main triangle: the top tube meets the head tube just under its top, the down tube just
        // over its bottom, as the carbon lay-up does
        var topTubeFront = f.HeadTop - f.SteererUp * 0.020f;
        var downTubeFront = f.HeadBottom + f.SteererUp * 0.025f;
        s.Tube(f.BottomBracket, f.SeatCluster, 0.020f, 0.017f, p.Frame);              // seat tube
        s.Tube(f.SeatCluster, topTubeFront, 0.017f, 0.019f, p.Frame);                // top tube
        s.Tube(f.HeadBottom - f.SteererUp * 0.010f, f.HeadTop, 0.025f, 0.021f, p.Frame);   // tapered head tube
        s.Tube(f.BottomBracket - new Vector3(0.045f, 0, 0), f.BottomBracket + new Vector3(0.045f, 0, 0), 0.024f, p.Frame);

        // the down tube, its middle the maker's name: one band of lettering at PS1 resolution. Three
        // tubes end to end, not a band laid over one, which would z-fight the tube under it
        var dt = downTubeFront - f.BottomBracket;
        float DownTubeRadius(float t) => Mathf.Lerp(0.028f, 0.022f, t);
        s.Tube(f.BottomBracket, f.BottomBracket + dt * 0.38f, DownTubeRadius(0f), DownTubeRadius(0.38f), p.Frame);
        s.Tube(f.BottomBracket + dt * 0.38f, f.BottomBracket + dt * 0.78f, DownTubeRadius(0.38f), DownTubeRadius(0.78f), p.Decal);
        s.Tube(f.BottomBracket + dt * 0.78f, downTubeFront, DownTubeRadius(0.78f), DownTubeRadius(1f), p.Frame);

        // rear triangle, doubled either side of the wheel; the stays meet the seat tube a little
        // under the top tube
        var stayTop = f.SeatCluster - f.SeatUp * 0.035f;
        foreach (float side in new[] { -0.062f, 0.062f })
        {
            var axle = f.RearAxle + new Vector3(side, 0, 0);
            s.Tube(f.BottomBracket + new Vector3(side * 0.55f, 0, 0), axle, 0.014f, 0.010f, p.Frame);
            s.Tube(stayTop + new Vector3(side * 0.25f, 0, 0), axle, 0.011f, 0.009f, p.Frame);
        }

        // fork: a crown under the head tube, two blades down to the axle
        var crown = f.HeadBottom - f.SteererUp * 0.020f;
        s.Tube(crown + new Vector3(-0.040f, 0, 0), crown + new Vector3(0.040f, 0, 0), 0.018f, p.Frame);
        foreach (float side in new[] { -0.050f, 0.050f })
            s.Tube(crown + new Vector3(side * 0.85f, 0, 0), f.FrontAxle + new Vector3(side, 0, 0), 0.016f, 0.010f, p.Frame);

        Cockpit(s, f, p);
        Saddle(s, f, p);
        if (includeCranks) Cranks(s, p, 0f, f);

        return s.Build();
    }

    /// <summary>
    /// Tyre, a carbon rim, a handful of spokes and, on a disc bike, the rotor on the side away
    /// from the chain. Sixteen segments is the point where a wheel stops reading as a polygon at
    /// riding distance; four crossed spokes, because past a few metres thirty-two are one haze.
    /// </summary>
    private static void Wheel(MeshScratch s, BikeFrame f, Vector3 axle, BikePalette p, float rotor)
    {
        float r = f.WheelRadius;
        s.Ring(axle, Vector3.Right, r - TyreThickness, r, 0.028f, p.Tyre);
        s.Ring(axle, Vector3.Right, r - TyreThickness - RimDepth, r - TyreThickness, 0.020f, p.Rim);

        for (int i = 0; i < 4; i++)
        {
            float angle = Mathf.Pi * i / 4f;
            var radial = new Vector3(0, Mathf.Cos(angle), Mathf.Sin(angle)) * (r - TyreThickness - RimDepth + 0.004f);
            s.Tube(axle - radial, axle + radial, 0.004f, p.Accent, 3);
        }

        s.Tube(axle - new Vector3(0.055f, 0, 0), axle + new Vector3(0.055f, 0, 0), 0.016f, p.Accent, 6);
        if (rotor > 0f)
            s.Ring(axle + new Vector3(0.048f, 0, 0), Vector3.Right, rotor - 0.018f, rotor, 0.003f, new Color(0.62f, 0.63f, 0.65f), 12);
    }

    /// <summary>
    /// Spacers, stem and a drop bar, from the fit's points: tops across at the clamp, forward to
    /// the hoods, the lever down, and the drop curving back. Each curve is two straight tubes,
    /// which at this resolution is indistinguishable from a bend.
    /// </summary>
    private static void Cockpit(MeshScratch s, BikeFrame f, BikePalette p)
    {
        s.Tube(f.HeadTop, f.StemBase + f.SteererUp * 0.02f, 0.017f, p.Accent);
        s.Tube(f.StemBase + f.SteererUp * 0.01f, f.BarClamp, 0.018f, 0.016f, p.Accent);

        float half = f.Hood.X;
        s.Tube(f.BarClamp + new Vector3(-half + 0.02f, 0, 0), f.BarClamp + new Vector3(half - 0.02f, 0, 0), 0.0135f, p.Accent);

        foreach (float side in new[] { -1f, 1f })
        {
            var corner = f.BarClamp + new Vector3(side * half, 0, 0.025f);
            var hood = f.Hood with { X = side * half };
            var leverFoot = hood + new Vector3(0, -0.080f, 0.020f);
            var drop = f.Drop with { X = side * half };
            var dropBottom = new Vector3(side * half, drop.Y + 0.030f, leverFoot.Z + 0.005f);

            s.Tube(f.BarClamp + new Vector3(side * (half - 0.02f), 0, 0), corner, 0.0135f, p.Accent);
            s.Tube(corner, hood - new Vector3(0, 0.020f, 0.020f), 0.0135f, p.Accent);
            s.Tube(hood + new Vector3(0, 0.005f, -0.030f), leverFoot, 0.016f, 0.010f, p.Accent);  // hood and lever
            s.Tube(hood - new Vector3(0, 0.020f, 0.020f), dropBottom, 0.0135f, p.Accent);
            s.Tube(dropBottom, drop, 0.0135f, p.Accent);
        }
    }

    private static void Saddle(MeshScratch s, BikeFrame f, BikePalette p)
    {
        float railY = f.SaddleTop.Y - 0.030f;
        var postTop = f.SeatCluster + f.SeatUp * ((railY - f.SeatCluster.Y) / f.SeatUp.Y);
        s.Tube(f.SeatCluster, postTop, 0.014f, p.Frame);
        s.Box(new Vector3(0, f.SaddleTop.Y - 0.012f, f.SaddleTop.Z - 0.015f), new Vector3(0.130f, 0.026f, 0.150f), p.Saddle);
        s.Tube(new Vector3(0, f.SaddleTop.Y - 0.018f, f.SaddleTop.Z + 0.055f),
            new Vector3(0, f.SaddleTop.Y - 0.010f, f.SaddleTop.Z + 0.135f), 0.035f, 0.014f, p.Saddle, 5);
    }

    /// <summary>
    /// Chainrings, cranks and pedals at <paramref name="crankAngle"/> radians. Zero puts the +X
    /// crank forward and level, which is where the cycling rig's "R" foot is, and <b>increasing the
    /// angle pedals forwards</b> (<see cref="BikeFrame.Pedal"/>).
    ///
    /// <para>
    /// That sign is the whole reason this is spelled out. The bike faces +Z, so driving it
    /// forward turns the chainring with its top moving toward +Z — which means a crank starting
    /// at the front goes <i>down</i> next, not up. Taking the obvious <c>(sin, cos)</c> circle
    /// runs it the other way, and a rider back-pedalling down a mountain is instantly obvious to
    /// anyone who has ridden a bike.
    /// </para>
    /// </summary>
    public static void Cranks(MeshScratch s, BikePalette p, float crankAngle, BikeFrame? frame = null)
    {
        var f = frame ?? BikeFrame.Default;
        var bb = f.BottomBracket;

        // Chainrings on the rider's right — the drive side, on every road bike ever made. Facing +Z
        // in author space the rider's right is −X, not +X. A ring's pitch radius is teeth × ½" / 2π.
        float ringX = -0.058f;
        foreach (int teeth in f.Spec.Chainrings)
        {
            float radius = teeth * 0.0127f / Mathf.Tau;
            s.Ring(bb + new Vector3(ringX, 0, 0), Vector3.Right, radius - 0.016f, radius + 0.003f, 0.003f, p.Accent, 12);
            ringX += 0.006f;
        }

        for (int side = 1; side >= -1; side -= 2)
        {
            var pedal = f.Pedal(crankAngle, side);
            var hub = bb + new Vector3(pedal.X, 0, 0);
            s.Tube(hub, pedal, 0.012f, 0.010f, p.Accent, 4);
            s.Box(pedal + new Vector3(side * 0.020f, 0, 0), new Vector3(0.055f, 0.014f, 0.075f), p.Saddle);
        }
    }

    /// <summary>Just the cranks and pedals, so they can be spun as their own node.</summary>
    public static ArrayMesh BuildCranks(BikePalette? palette = null, float crankAngle = 0f)
    {
        var s = new MeshScratch();
        Cranks(s, palette ?? BikeFrame.Default.Spec.Look, crankAngle);
        return s.Build();
    }
}
