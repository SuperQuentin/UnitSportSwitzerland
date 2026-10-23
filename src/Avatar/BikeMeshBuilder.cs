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

    /// <summary>Which of the bike to build: all of it, or the frame and the steered front apart, to turn the bars (#1124).</summary>
    public enum Parts { All, Rear, Front }

    /// <param name="parts">
    /// <see cref="Parts.Front"/> is the fork, front wheel, stem and bars, still in bike space: turn it
    /// about the steering axis (<see cref="BikeFrame.HeadTop"/>, <see cref="BikeFrame.SteererUp"/>).
    /// </param>
    public static ArrayMesh Build(BikePalette? palette = null, bool includeCranks = true, BikeFrame? frame = null, Parts parts = Parts.All)
    {
        var f = frame ?? BikeFrame.Default;
        var p = palette ?? f.Spec.Look;
        var s = new MeshScratch();

        bool disc = f.Spec.Brakes == BikeBrakes.Disc;
        if (parts != Parts.Rear) Front(s, f, p, disc);
        if (parts == Parts.Front) return s.Build();

        Wheel(s, f, f.RearAxle, p, disc ? 0.070f : 0f);

        if (f.Spec.Assist != null || f.Spec.Kit != null)
        {
            TrekkingFrame(s, f, p);
            if (includeCranks) Cranks(s, p, 0f, f);
            return s.Build();
        }

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

        Saddle(s, f, p);
        if (includeCranks) Cranks(s, p, 0f, f);

        return s.Build();
    }

    /// <summary>The steered part: fork, front wheel, stem and bars.</summary>
    private static void Front(MeshScratch s, BikeFrame f, BikePalette p, bool disc)
    {
        Wheel(s, f, f.FrontAxle, p, disc ? 0.080f : 0f);
        if (f.Spec.Kit is { } kit)
        {
            TrekkingFront(s, f, p, kit);
            return;
        }
        // fork: a crown under the head tube, two blades down to the axle
        var crown = f.HeadBottom - f.SteererUp * 0.020f;
        s.Tube(crown + new Vector3(-0.040f, 0, 0), crown + new Vector3(0.040f, 0, 0), 0.018f, p.Frame);
        foreach (float side in new[] { -0.050f, 0.050f })
            s.Tube(crown + new Vector3(side * 0.85f, 0, 0), f.FrontAxle + new Vector3(side, 0, 0), 0.016f, 0.010f, p.Frame);
        Cockpit(s, f, p);
    }

    /// <summary>
    /// Tyre, a carbon rim, a handful of spokes and, on a disc bike, the rotor on the side away
    /// from the chain. Sixteen segments is the point where a wheel stops reading as a polygon at
    /// riding distance; four crossed spokes, because past a few metres thirty-two are one haze.
    /// </summary>
    private static void Wheel(MeshScratch s, BikeFrame f, Vector3 axle, BikePalette p, float rotor)
    {
        float r = f.WheelRadius;
        // a road tyre is drawn 28 mm whatever it is; a balloon tyre (#1159) as wide and tall as its sidewall says,
        // on a shallow aluminium rim
        bool balloon = f.Spec.TyreWidth > 0.040f;
        float tyre = balloon ? f.Spec.TyreWidth * 0.9f : TyreThickness;
        float rimDepth = balloon ? 0.020f : RimDepth;
        s.Ring(axle, Vector3.Right, r - tyre, r, balloon ? f.Spec.TyreWidth : 0.028f, p.Tyre);
        s.Ring(axle, Vector3.Right, r - tyre - rimDepth, r - tyre, balloon ? 0.026f : 0.020f, p.Rim);

        for (int i = 0; i < 4; i++)
        {
            float angle = Mathf.Pi * i / 4f;
            var radial = new Vector3(0, Mathf.Cos(angle), Mathf.Sin(angle)) * (r - tyre - rimDepth + 0.004f);
            s.Tube(axle - radial, axle + radial, 0.004f, p.Accent, 3);
        }

        s.Tube(axle - new Vector3(0.055f, 0, 0), axle + new Vector3(0.055f, 0, 0), 0.016f, p.Accent, 6);
        if (rotor > 0f)
            s.Ring(axle + new Vector3(0.048f, 0, 0), Vector3.Right, rotor - 0.018f, rotor, 0.003f, new Color(0.62f, 0.63f, 0.65f), 12);
    }

    // ---- a trekking pedelec (#1159): the Moustache Samedi's aluminium frame, motor, battery and kit ----

    /// <summary>Black anodised aluminium: rack, fenders, stem, the motor's cover.</summary>
    private static readonly Color Alloy = new(0.10f, 0.10f, 0.11f);
    private static readonly Color MotorGrey = new(0.20f, 0.21f, 0.22f);
    private static readonly Color Stanchion = new(0.70f, 0.71f, 0.72f);
    private static readonly Color HeadLens = new(1.00f, 0.97f, 0.85f);
    private static readonly Color TailLens = new(0.85f, 0.04f, 0.04f);

    /// <summary>
    /// The frame of a trekking e-bike: hydroformed tubes half as fat again as a road bike's, the down
    /// tube fatter still round the PowerTube battery inside it with the maker's name along it, a mid
    /// motor under the bottom bracket, then the rack, the rear fender and light, the comfort saddle on
    /// a suspension post, and the kickstand folded along the left chainstay.
    /// </summary>
    private static void TrekkingFrame(MeshScratch s, BikeFrame f, BikePalette p)
    {
        var kit = f.Spec.Kit;
        var topTubeFront = f.HeadTop - f.SteererUp * 0.030f;
        var downTubeFront = f.HeadBottom + f.SteererUp * 0.030f;
        s.Tube(f.BottomBracket + f.SeatUp * 0.06f, f.SeatCluster, 0.020f, 0.018f, p.Frame);   // seat tube, from over the motor
        if (f.Spec.FrameStyle == BikeFrameStyle.LowFrame)
        {
            // the Open frame: no top tube; a second tube leaves the head tube under the top tube's place,
            // runs down alongside the battery and sweeps into the seat tube just over the motor, the
            // seat tube's lower half thickened where it carries the saddle's load alone
            var from = f.HeadTop - f.SteererUp * 0.075f;
            var to = f.BottomBracket + f.SeatUp * 0.17f;
            var bend = from + (to - from) * 0.55f + new Vector3(0, -0.085f, 0);
            s.Tube(from, bend, 0.024f, 0.023f, p.Frame);
            s.Tube(bend, to, 0.023f, 0.025f, p.Frame);
            s.Tube(f.BottomBracket + f.SeatUp * 0.06f, f.BottomBracket + f.SeatUp * 0.24f, 0.024f, 0.022f, p.Frame);
        }
        else s.Tube(f.SeatCluster, topTubeFront, 0.022f, 0.024f, p.Frame);                    // top tube
        s.Tube(f.HeadBottom - f.SteererUp * 0.015f, f.HeadTop + f.SteererUp * 0.005f, 0.030f, 0.026f, p.Frame);   // tapered head tube

        // the down tube holds the battery: oval and fat, the name on its middle as three tubes end to end
        var bb = f.BottomBracket + new Vector3(0, 0.02f, 0.06f);
        var dt = downTubeFront - bb;
        float Radius(float t) => Mathf.Lerp(0.040f, 0.032f, t);
        s.Tube(bb, bb + dt * 0.30f, Radius(0f), Radius(0.30f), p.Frame, 8);
        s.Tube(bb + dt * 0.30f, bb + dt * 0.72f, Radius(0.30f), Radius(0.72f), p.Decal, 8);
        s.Tube(bb + dt * 0.72f, downTubeFront, Radius(0.72f), Radius(1f), p.Frame, 8);

        // the Bosch motor: a cast housing under and ahead of the cranks, its cover along the down tube
        var down = dt.Normalized();
        var side = Vector3.Right;
        var up = down.Cross(side).Normalized();
        if (up.Y < 0) up = -up;
        var basis = new Basis(up.Cross(down), up, down);   // right-handed, or the faces turn inside out
        s.RoundedBox(f.BottomBracket + down * 0.035f - up * 0.015f, new Vector3(0.095f, 0.13f, 0.17f), MotorGrey, basis);

        // rear triangle: chainstays off the motor's mounts, seatstays to a little under the top tube
        var stayTop = f.SeatCluster - f.SeatUp * 0.045f;
        foreach (float x in new[] { -0.066f, 0.066f })
        {
            var axle = f.RearAxle + new Vector3(x, 0, 0);
            s.Tube(f.BottomBracket + new Vector3(x * 0.6f, 0.01f, -0.04f), axle, 0.017f, 0.012f, p.Frame);
            s.Tube(stayTop + new Vector3(x * 0.3f, 0, 0), axle, 0.013f, 0.011f, p.Frame);
        }

        if (kit is { Kickstand: true })
            // folded along the left (+X, away from the chain) chainstay, its foot at the back
            s.Tube(f.BottomBracket + new Vector3(0.050f, -0.030f, -0.070f), f.RearAxle + new Vector3(0.085f, -0.050f, 0.130f), 0.010f, 0.008f, Alloy, 4);

        if (kit is { Rack: true }) Rack(s, f, stayTop, kit.Lights);
        if (kit is { Fenders: true }) Fender(s, f.RearAxle, f.WheelRadius, 30f, -110f, f.Spec.TyreWidth);
        ComfortSaddle(s, f, p, kit is { SuspensionSeatpost: true });
    }

    /// <summary>
    /// The steered part of a trekking bike: a suspension fork (crown, silver stanchions into black
    /// lowers), the front fender, the headlight on the crown, and the riser bar.
    /// </summary>
    private static void TrekkingFront(MeshScratch s, BikeFrame f, BikePalette p, BikeKit kit)
    {
        var crown = f.HeadBottom - f.SteererUp * 0.030f;
        s.Tube(crown + new Vector3(-0.060f, 0, 0), crown + new Vector3(0.060f, 0, 0), 0.024f, Alloy, 6);
        // the fork's line from the crown to the axle; on a suspension fork the lowers take its bottom half
        foreach (float x in new[] { -0.058f, 0.058f })
        {
            var top = crown + new Vector3(x, 0, 0);
            var axle = f.FrontAxle + new Vector3(x, 0, 0);
            if (kit.SuspensionFork > 0f)
            {
                var knee = top + (axle - top) * 0.42f;
                s.Tube(top, knee, 0.017f, Stanchion, 6);
                s.Tube(knee - (axle - top).Normalized() * 0.02f, axle, 0.024f, 0.016f, Alloy, 6);
            }
            else s.Tube(top, axle, 0.018f, 0.012f, p.Frame);
        }
        // the fork's brace over the tyre, between the lowers' tops
        if (kit.SuspensionFork > 0f)
        {
            var brace = crown + (f.FrontAxle - crown) * 0.45f + new Vector3(0, 0.02f, 0.025f);
            s.Tube(brace + new Vector3(-0.058f, 0, 0), brace + new Vector3(0.058f, 0, 0), 0.012f, Alloy, 4);
        }
        if (kit.Fenders) Fender(s, f.FrontAxle, f.WheelRadius, 75f, -50f, f.Spec.TyreWidth);
        if (kit.Lights)
        {
            // the headlight on a bracket ahead of the crown, its lens forward
            var lamp = crown + new Vector3(0, -0.010f, 0.075f);
            // the lens a 1 cm slab ahead of the housing, never a sheet laid on its face (#1159: 4 mm fought)
            s.Box(lamp - new Vector3(0, 0, 0.005f), new Vector3(0.055f, 0.040f, 0.035f), Alloy);
            s.Box(lamp + new Vector3(0, 0, 0.0175f), new Vector3(0.042f, 0.028f, 0.010f), HeadLens);
        }
        RiserCockpit(s, f, p);
    }

    /// <summary>
    /// A swept riser bar on a short rising stem: the bar rises from the clamp and bends back to the
    /// grips at the fit's hand points, black grips, brake levers ahead of them and the Bosch remote and
    /// display by the left grip.
    /// </summary>
    private static void RiserCockpit(MeshScratch s, BikeFrame f, BikePalette p)
    {
        s.Tube(f.HeadTop, f.StemBase + f.SteererUp * 0.02f, 0.019f, Alloy);
        s.Tube(f.StemBase + f.SteererUp * 0.01f, f.BarClamp, 0.020f, 0.018f, Alloy);
        s.Box(f.BarClamp + new Vector3(0, 0.035f, -0.010f), new Vector3(0.055f, 0.012f, 0.040f), Alloy);   // the display, on the clamp

        float half = f.Hood.X;
        var rise = f.Hood.Y - f.BarClamp.Y;
        var sweep = f.Hood.Z - f.BarClamp.Z;
        s.Tube(f.BarClamp + new Vector3(-0.045f, 0, 0), f.BarClamp + new Vector3(0.045f, 0, 0), 0.016f, Alloy);
        foreach (float x in new[] { -1f, 1f })
        {
            var root = f.BarClamp + new Vector3(x * 0.045f, 0, 0);
            var bend = f.BarClamp + new Vector3(x * 0.15f, rise * 0.75f, sweep * 0.15f);
            var gripIn = new Vector3(x * (half - 0.065f), f.Hood.Y, f.Hood.Z + 0.020f);
            var gripOut = new Vector3(x * (half + 0.060f), f.Hood.Y - 0.004f, f.Hood.Z - 0.016f);
            s.Tube(root, bend, 0.0135f, Alloy);
            s.Tube(bend, gripIn, 0.0125f, Alloy);
            s.Tube(gripIn, gripOut, 0.0175f, p.Saddle);   // the grip
            // the brake lever: from its clamp at the grip's inside forward and out over the grip
            var lever = gripIn + new Vector3(x * 0.012f, 0.008f, 0.012f);
            s.Tube(lever, lever + new Vector3(x * 0.085f, -0.012f, 0.055f), 0.006f, 0.005f, Alloy, 4);
        }
        // the Bosch remote at the left grip (the rider's left is +X)
        s.Box(RemoteAt(f), new Vector3(0.030f, 0.020f, 0.028f), Alloy);
    }

    /// <summary>
    /// The Bosch remote on the bar inboard of the left grip, bike space (+X the rider's left, +Z
    /// forward): drawn there, and the VR hand pokes it there to step the assist level (#1167).
    /// </summary>
    public static Vector3 RemoteAt(BikeFrame f) => new(f.Hood.X - 0.085f, f.Hood.Y + 0.012f, f.Hood.Z + 0.014f);

    /// <summary>
    /// A fender over a wheel, from <paramref name="fromDeg"/> to <paramref name="toDeg"/> about the axle
    /// (0 straight up, positive forward), drawn as thin plates a little wider than the tyre, with one
    /// stay to the axle at its back end.
    /// </summary>
    private static void Fender(MeshScratch s, Vector3 axle, float wheelRadius, float fromDeg, float toDeg, float tyreWidth)
    {
        const int Segments = 9;
        float radius = wheelRadius + 0.022f;
        float width = tyreWidth + 0.016f;
        Vector3 At(float deg)
        {
            float a = Mathf.DegToRad(deg);
            return axle + new Vector3(0, Mathf.Cos(a), Mathf.Sin(a)) * radius;
        }
        for (int i = 0; i < Segments; i++)
        {
            var a = At(Mathf.Lerp(fromDeg, toDeg, i / (float)Segments));
            var b = At(Mathf.Lerp(fromDeg, toDeg, (i + 1) / (float)Segments));
            var along = (b - a).Normalized();
            var outward = ((a + b) * 0.5f - axle).Normalized();
            s.Box((a + b) * 0.5f, new Vector3(width, 0.004f, (b - a).Length() + 0.002f), Alloy,
                new Basis(outward.Cross(along), outward, along));   // right-handed, or the plates face inward
        }
        var end = At(toDeg + 12f);
        foreach (float x in new[] { -1f, 1f })
            s.Tube(axle + new Vector3(x * 0.060f, 0, 0), end + new Vector3(x * width / 2f, 0, 0), 0.0035f, Alloy, 3);
    }

    /// <summary>
    /// The rack over the back wheel: a platform of two rails and cross bars at the top of the fender,
    /// held from the seatstays' top and on struts down to the axle, the tail light on its back end.
    /// </summary>
    private static void Rack(MeshScratch s, BikeFrame f, Vector3 stayTop, bool light)
    {
        float y = f.RearAxle.Y + f.WheelRadius + 0.075f;
        float front = stayTop.Z - 0.030f;
        float back = f.RearAxle.Z - 0.160f;
        const float Half = 0.060f;
        foreach (float x in new[] { -Half, Half })
        {
            s.Tube(new Vector3(x, y, front), new Vector3(x, y, back), 0.0065f, Alloy, 4);           // rail
            s.Tube(new Vector3(x, y, back + 0.020f), f.RearAxle + new Vector3(x * 1.2f, 0.010f, -0.010f), 0.0065f, Alloy, 4);   // strut
            s.Tube(new Vector3(x, y, front), stayTop + new Vector3(x * 0.6f, -0.010f, -0.010f), 0.005f, Alloy, 4);   // to the seatstays
        }
        for (int i = 0; i <= 3; i++)
        {
            float z = Mathf.Lerp(front, back, i / 3f);
            s.Tube(new Vector3(-Half, y, z), new Vector3(Half, y, z), 0.005f, Alloy, 4);
        }
        if (light)
        {
            var lamp = new Vector3(0, y - 0.020f, back - 0.012f);
            // the lens an 8 mm slab behind the housing (#1159: a 2 mm sheet 1.5 mm off its face fought)
            s.Box(lamp + new Vector3(0, 0, 0.004f), new Vector3(0.070f, 0.030f, 0.012f), Alloy);
            s.Box(lamp - new Vector3(0, 0, 0.006f), new Vector3(0.060f, 0.022f, 0.008f), TailLens);
        }
    }

    /// <summary>A sat-up rider's wide, padded saddle on a fat post; on a suspension post, its black sliding top.</summary>
    private static void ComfortSaddle(MeshScratch s, BikeFrame f, BikePalette p, bool suspension)
    {
        float railY = f.SaddleTop.Y - 0.045f;
        var postTop = f.SeatCluster + f.SeatUp * ((railY - f.SeatCluster.Y) / f.SeatUp.Y);
        if (suspension)
        {
            var mid = f.SeatCluster + (postTop - f.SeatCluster) * 0.55f;
            s.Tube(f.SeatCluster, mid, 0.0158f, Stanchion, 6);
            s.Tube(mid, postTop, 0.019f, 0.017f, Alloy, 6);
        }
        else s.Tube(f.SeatCluster, postTop, 0.0158f, Stanchion, 6);
        s.RoundedBox(new Vector3(0, f.SaddleTop.Y - 0.022f, f.SaddleTop.Z - 0.035f), new Vector3(0.190f, 0.045f, 0.170f), p.Saddle);
        s.Tube(new Vector3(0, f.SaddleTop.Y - 0.024f, f.SaddleTop.Z + 0.040f),
            new Vector3(0, f.SaddleTop.Y - 0.016f, f.SaddleTop.Z + 0.120f), 0.055f, 0.020f, p.Saddle, 6);
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
    public static ArrayMesh BuildCranks(BikePalette? palette = null, float crankAngle = 0f, BikeFrame? frame = null)
    {
        var s = new MeshScratch();
        var f = frame ?? BikeFrame.Default;
        Cranks(s, palette ?? f.Spec.Look, crankAngle, f);
        return s.Build();
    }
}
