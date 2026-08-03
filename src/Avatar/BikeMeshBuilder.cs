using Godot;

namespace UnitSport.Avatar;

public sealed record BikePalette(Color Frame, Color Accent, Color Tyre, Color Rim, Color Saddle)
{
    public static readonly BikePalette Default = new(
        Frame: new Color(0.12f, 0.42f, 0.68f),
        Accent: new Color(0.85f, 0.86f, 0.88f),
        Tyre: new Color(0.10f, 0.10f, 0.11f),
        Rim: new Color(0.62f, 0.63f, 0.65f),
        Saddle: new Color(0.09f, 0.09f, 0.10f));

    public static BikePalette ForRider(int index) => Default with
    {
        Frame = Color.FromHsv((index * 0.37f + 0.5f) % 1f, 0.55f, 0.72f),
    };
}

/// <summary>
/// A road bike at real proportions: 700c wheels, a 56 cm-ish diamond frame, drop bars.
///
/// <para>
/// The numbers are actual bicycle geometry rather than eyeballed ones, because a bike is a
/// shape everybody knows. Wheelbase 0.99 m, bottom bracket 0.27 m off the ground, 73° head and
/// seat angles, saddle at 0.90 m. Getting those wrong is instantly visible even in a few
/// hundred triangles — and the saddle height in particular has to agree with the rider's hip in
/// <see cref="HumanMeshBuilder"/>, or the cyclist floats above the machine.
/// </para>
///
/// <para>
/// Built facing +Z, origin on the ground between the wheels, so it drops straight into the
/// world beside a <see cref="HumanPose.Cycling"/> figure with no offset.
/// </para>
/// </summary>
public static class BikeMeshBuilder
{
    // ---- geometry, metres ----
    public const float WheelRadius = 0.337f;     // 700x25c
    private const float TyreThickness = 0.026f;
    private const float RearAxleZ = -0.415f;
    private const float FrontAxleZ = 0.575f;
    private const float BottomBracketY = 0.270f;
    private const float BottomBracketZ = -0.020f;

    /// <summary>Saddle top. The rider's hip sits here in the cycling pose.</summary>
    public const float SaddleY = 0.900f;
    private const float SaddleZ = -0.055f;

    /// <summary>Where the hands go. Matches the cycling rig's wrists.</summary>
    public const float DropsY = 0.885f;
    public const float DropsZ = 0.790f;

    private static readonly Vector3 BottomBracket = new(0, BottomBracketY, BottomBracketZ);
    private static readonly Vector3 RearAxle = new(0, WheelRadius, RearAxleZ);
    private static readonly Vector3 FrontAxle = new(0, WheelRadius, FrontAxleZ);
    private static readonly Vector3 SeatTop = new(0, SaddleY - 0.035f, SaddleZ);
    private static readonly Vector3 HeadTop = new(0, 0.845f, 0.545f);
    private static readonly Vector3 HeadBottom = new(0, 0.640f, 0.610f);

    public static ArrayMesh Build(BikePalette? palette = null, bool includeCranks = true)
    {
        var p = palette ?? BikePalette.Default;
        var s = new MeshScratch();

        Wheel(s, RearAxle, p);
        Wheel(s, FrontAxle, p);

        // main triangle
        s.Tube(BottomBracket, SeatTop, 0.026f, 0.020f, p.Frame);            // seat tube
        s.Tube(BottomBracket, HeadBottom, 0.030f, 0.024f, p.Frame);         // down tube
        s.Tube(SeatTop, HeadTop, 0.023f, 0.021f, p.Frame);                  // top tube
        s.Tube(HeadBottom, HeadTop, 0.026f, p.Accent);                      // head tube

        // rear triangle, doubled either side of the wheel
        foreach (float side in new[] { -0.055f, 0.055f })
        {
            var axle = RearAxle + new Vector3(side, 0, 0);
            s.Tube(BottomBracket + new Vector3(side * 0.6f, 0, 0), axle, 0.017f, 0.011f, p.Frame);
            s.Tube(SeatTop + new Vector3(side * 0.35f, 0, 0), axle, 0.014f, 0.010f, p.Frame);
        }

        // fork: two blades from the crown down to the axle
        foreach (float side in new[] { -0.048f, 0.048f })
            s.Tube(HeadBottom + new Vector3(side * 0.5f, -0.02f, 0),
                FrontAxle + new Vector3(side, 0, 0), 0.017f, 0.011f, p.Accent);

        Handlebars(s, p);
        Saddle(s, p);
        if (includeCranks) Cranks(s, p, 0f);

        return s.Build();
    }

    /// <summary>
    /// Tyre, rim and a handful of spokes. Sixteen segments is the point where a wheel stops
    /// reading as a polygon at riding distance; the spokes are four crossed tubes rather than
    /// thirty-two, because past a few metres they are one grey haze either way.
    /// </summary>
    private static void Wheel(MeshScratch s, Vector3 axle, BikePalette p)
    {
        s.Ring(axle, Vector3.Right, WheelRadius - TyreThickness, WheelRadius, 0.025f, p.Tyre);
        s.Ring(axle, Vector3.Right, WheelRadius - 0.055f, WheelRadius - TyreThickness, 0.019f, p.Rim);

        for (int i = 0; i < 4; i++)
        {
            float angle = Mathf.Pi * i / 4f;
            var radial = new Vector3(0, Mathf.Cos(angle), Mathf.Sin(angle)) * (WheelRadius - 0.05f);
            s.Tube(axle - radial, axle + radial, 0.005f, p.Rim, 3);
        }

        s.Tube(axle - new Vector3(0.045f, 0, 0), axle + new Vector3(0.045f, 0, 0), 0.018f, p.Accent, 6);
    }

    private static void Handlebars(MeshScratch s, BikePalette p)
    {
        var stemFront = new Vector3(0, HeadTop.Y + 0.020f, HeadTop.Z + 0.095f);
        s.Tube(HeadTop, stemFront, 0.019f, 0.016f, p.Accent);

        // the tops, then a drop on each side — the curve is three straight tubes, which at this
        // resolution is indistinguishable from a bend and a great deal cheaper
        var barL = stemFront + new Vector3(-0.195f, 0, 0);
        var barR = stemFront + new Vector3(0.195f, 0, 0);
        s.Tube(barL, barR, 0.014f, p.Accent);

        foreach (var bar in new[] { barL, barR })
        {
            float side = Mathf.Sign(bar.X);
            var hood = bar + new Vector3(0, 0.010f, 0.105f);
            var bend = hood + new Vector3(0, -0.075f, 0.035f);
            var drop = new Vector3(bar.X + side * 0.005f, DropsY - 0.012f, DropsZ);

            s.Tube(bar, hood, 0.014f, 0.013f, p.Accent);
            s.Tube(hood, bend, 0.013f, p.Frame);       // lever body
            s.Tube(bend, drop, 0.013f, 0.012f, p.Accent);
        }
    }

    private static void Saddle(MeshScratch s, BikePalette p)
    {
        s.Box(new Vector3(0, SaddleY - 0.012f, SaddleZ - 0.020f),
            new Vector3(0.115f, 0.028f, 0.150f), p.Saddle);
        s.Tube(new Vector3(0, SaddleY - 0.020f, SaddleZ + 0.055f),
            new Vector3(0, SaddleY - 0.010f, SaddleZ + 0.130f), 0.035f, 0.016f, p.Saddle, 5);
    }

    /// <summary>
    /// Chainring, cranks and pedals at <paramref name="crankAngle"/> radians. Zero puts the
    /// right crank forward and level, which is where the cycling rig's right foot is, and
    /// <b>increasing the angle pedals forwards</b>.
    ///
    /// <para>
    /// That sign is the whole reason this is spelled out. The bike faces +Z, so driving it
    /// forward turns the chainring with its top moving toward +Z — which means a crank starting
    /// at the front goes <i>down</i> next, not up. Taking the obvious <c>(sin, cos)</c> circle
    /// runs it the other way, and a rider back-pedalling down a mountain is instantly obvious to
    /// anyone who has ridden a bike.
    /// </para>
    /// </summary>
    public static void Cranks(MeshScratch s, BikePalette p, float crankAngle)
    {
        // Chainring on the rider's right — the drive side, on every road bike ever made.
        // Facing +Z in author space the rider's right is −X, not +X, which is the sort of thing
        // that is invisible until someone who rides looks at it from the correct side.
        s.Ring(BottomBracket - new Vector3(0.055f, 0, 0), Vector3.Right, 0.075f, 0.098f, 0.004f,
            p.Accent, 12);
        s.Tube(BottomBracket - new Vector3(0.060f, 0, 0), BottomBracket + new Vector3(0.060f, 0, 0),
            0.020f, p.Accent, 6);

        for (int i = 0; i < 2; i++)
        {
            float angle = crankAngle + i * Mathf.Pi;
            float side = i == 0 ? 0.070f : -0.070f;
            var hub = BottomBracket + new Vector3(side, 0, 0);
            var pedal = hub + new Vector3(0, -Mathf.Sin(angle) * 0.170f, Mathf.Cos(angle) * 0.170f);

            s.Tube(hub, pedal, 0.012f, 0.010f, p.Frame, 4);
            s.Box(pedal + new Vector3(Mathf.Sign(side) * 0.020f, 0, 0),
                new Vector3(0.055f, 0.014f, 0.075f), p.Saddle);
        }
    }

    /// <summary>Just the cranks and pedals, so they can be spun as their own node.</summary>
    public static ArrayMesh BuildCranks(BikePalette? palette = null, float crankAngle = 0f)
    {
        var s = new MeshScratch();
        Cranks(s, palette ?? BikePalette.Default, crankAngle);
        return s.Build();
    }
}
