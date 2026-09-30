using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// One motorbike as a shape: the published geometry (wheelbase, rake, tyre sizes) plus the three
/// points the rider touches. Author space, metres, +Z forward, origin on the ground under the middle
/// of the wheelbase. The contact points are the RIGHT side; the left mirrors them.
/// </summary>
public sealed record MotoLook
{
    /// <summary>Bodywork: full fairing and a tail that points at the sky, a naked roadster, or an adventure bike's tall screen and beak.</summary>
    public MotoStyle Style { get; init; } = MotoStyle.Sport;
    /// <summary>What sits in the frame, as far as the eye is concerned.</summary>
    public MotoEngineShape EngineShape { get; init; } = MotoEngineShape.InlineFour;
    /// <summary>Wire-spoked rims (an adventure bike's 21/18) rather than cast Y-spokes.</summary>
    public bool Spoked { get; init; }
    public float Wheelbase { get; init; } = 1.4f;
    /// <summary>Tyre sizes as on the sidewall: "120/70ZR17", "90/90-21", "150/70R18".</summary>
    public string FrontTyre { get; init; } = "120/70ZR17";
    public string RearTyre { get; init; } = "190/55ZR17";
    /// <summary>Steering head angle from vertical, degrees, and trail, m (data: the mesh has no fork offset).</summary>
    public float RakeDeg { get; init; } = 24f;
    public float Trail { get; init; } = 0.1f;
    /// <summary>Suspension travel front / rear, m (data for now: nothing compresses).</summary>
    public float FrontTravel { get; init; } = 0.12f;
    public float RearTravel { get; init; } = 0.12f;
    /// <summary>Front axle to top triple clamp along the steering axis, m.</summary>
    public float ForkLength { get; init; } = 0.64f;
    /// <summary>Seat surface under the pelvis, right grip, right footpeg.</summary>
    public Vector3 Seat { get; init; }
    public Vector3 Grip { get; init; }
    public Vector3 Peg { get; init; }
    public Color Paint { get; init; } = new(0.1f, 0.25f, 0.75f);
    public Color Trim { get; init; } = new(0.9f, 0.9f, 0.92f);
    public Color Frame { get; init; } = new(0.55f, 0.56f, 0.6f);
    public Color Wheel { get; init; } = new(0.1f, 0.1f, 0.11f);

    public float FrontRadius => Tyre.Radius(FrontTyre);
    public float RearRadius => Tyre.Radius(RearTyre);
    public float FrontWidth => Tyre.Width(FrontTyre);
    public float RearWidth => Tyre.Width(RearTyre);
    public Vector3 FrontAxle => new(0, FrontRadius, Wheelbase * 0.5f);
    public Vector3 RearAxle => new(0, RearRadius, -Wheelbase * 0.5f);
    /// <summary>Up the steering axis, author space: leans back by the rake.</summary>
    public Vector3 SteerAxis => new(0, Mathf.Cos(Mathf.DegToRad(RakeDeg)), -Mathf.Sin(Mathf.DegToRad(RakeDeg)));
    public Vector3 TopClamp => FrontAxle + SteerAxis * ForkLength;
}

public enum MotoStyle { Sport, Naked, Adventure }

public enum MotoEngineShape { InlineFour, VTwin, ParallelTwin }

/// <summary>
/// A motorcycle tyre size read off the sidewall: width mm / aspect % then the construction ("ZR",
/// "R", or "-" for bias ply) and the rim diameter in inches. "90/90-21": 90 mm wide, sidewall 81 mm,
/// on a 21 in rim, radius 21*25.4/2 + 81 = 348 mm.
/// </summary>
public static class Tyre
{
    private static (float W, float Aspect, float Rim) Parse(string size)
    {
        var t = size.Replace(" ", "").ToUpperInvariant();
        int slash = t.IndexOf('/');
        int sep = slash < 0 ? -1 : t.IndexOfAny(new[] { 'Z', 'R', '-', 'B' }, slash + 1);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var num = System.Globalization.NumberStyles.Float;
        if (slash < 1 || sep < 0
            || !float.TryParse(t[..slash], num, inv, out float w)
            || !float.TryParse(t[(slash + 1)..sep], num, inv, out float ar)
            || !float.TryParse(new string(t[sep..].SkipWhile(c => !char.IsDigit(c)).TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()), num, inv, out float rim))
            throw new System.FormatException($"tyre size '{size}'");
        return (w, ar, rim);
    }

    /// <summary>Outer radius, m.</summary>
    public static float Radius(string size) { var (w, ar, rim) = Parse(size); return (rim * 25.4f * 0.5f + w * ar / 100f) / 1000f; }
    /// <summary>Rim radius, m.</summary>
    public static float RimRadius(string size) => Parse(size).Rim * 0.0127f;
    /// <summary>Tread width, m.</summary>
    public static float Width(string size) => Parse(size).W / 1000f;
}

/// <summary>
/// A motorbike from tapered tubes and boxes, like every other machine here (<see cref="MeshScratch"/>).
/// Split into the parts that move: the body (frame, engine, bodywork, and the rider, who does not
/// move relative to it), the front end that steers about the raked axis, and two wheels that spin.
/// </summary>
public static class MotorbikeMeshBuilder
{
    private static readonly Color Black = new(0.07f, 0.07f, 0.08f);
    private static readonly Color Metal = new(0.62f, 0.63f, 0.66f);
    private static readonly Color Disc = new(0.72f, 0.72f, 0.74f);
    private static readonly Color Lamp = new(0.95f, 0.95f, 0.85f);
    private static readonly Color Glass = new(0.2f, 0.24f, 0.3f);

    /// <summary>Frame, engine, tank, seat, bodywork, swingarm — and the rider when given a palette.</summary>
    public static ArrayMesh BuildBody(MotoLook k, HumanPalette? rider)
    {
        var s = new MeshScratch();
        var rear = k.RearAxle;
        var top = k.TopClamp;
        var headLow = k.FrontAxle + k.SteerAxis * (k.ForkLength - 0.2f);
        var pivot = new Vector3(0, rear.Y + 0.14f, rear.Z + 0.56f);   // swingarm pivot, ~0.56 m ahead of the axle

        // --- swingarm, shock, footpegs ---
        foreach (float x in new[] { -0.11f, 0.11f })
            s.Tube(pivot + new Vector3(x * 1.1f, 0, 0), rear + new Vector3(x, 0, 0), 0.035f, 0.025f, Metal);
        s.Tube(pivot + new Vector3(0, 0.02f, -0.1f), pivot + new Vector3(0, 0.3f, 0.02f), 0.03f, Black);
        foreach (float x in new[] { -1f, 1f })
        {
            var peg = k.Peg with { X = x * Mathf.Abs(k.Peg.X) };
            s.Tube(peg - new Vector3(x * 0.05f, 0, 0), peg + new Vector3(x * 0.04f, 0, 0), 0.012f, Metal, 5);
            s.Tube(peg - new Vector3(x * 0.05f, 0, 0), pivot + new Vector3(x * 0.12f, 0.05f, -0.02f), 0.018f, Metal, 5);   // hanger
        }

        if (k.EngineShape == MotoEngineShape.VTwin) Twin(s, k); else Inline(s, k.EngineShape == MotoEngineShape.InlineFour ? 4 : 2);

        // --- frame: head stock to swingarm pivot ---
        s.Tube(headLow, top, 0.045f, k.Frame);
        if (k.Style == MotoStyle.Naked)
        {
            // Monster: a short frame from the head onto the cylinder heads, the engine carries the rest
            foreach (float x in new[] { -0.09f, 0.09f })
                s.Tube(top + new Vector3(x * 0.6f, -0.03f, -0.02f), new Vector3(x, 0.66f, 0.05f), 0.035f, 0.03f, k.Frame);
        }
        else
        {
            // twin spar wrapping over the engine to the pivot
            foreach (float x in new[] { -0.14f, 0.14f })
            {
                var bend = new Vector3(x, 0.68f, -0.02f);
                s.Tube(headLow + new Vector3(x * 0.4f, 0.05f, -0.03f), bend, 0.05f, 0.055f, k.Frame);
                s.Tube(bend, pivot + new Vector3(x, 0.04f, 0), 0.055f, 0.045f, k.Frame);
            }
        }

        // --- tank, seat, tail ---
        var seat = k.Seat;
        var tankFront = top + new Vector3(0, -0.04f, -0.12f);
        var tankBack = new Vector3(0, seat.Y + 0.02f, seat.Z + 0.2f);
        if (k.Style != MotoStyle.Sport)
        {
            // the Monster's "bull" tank (an adventure bike's is the same idea, bigger): tall and broad at the front, swept down into the seat
            s.Tube(tankFront + new Vector3(0, -0.02f, 0), tankBack + new Vector3(0, 0.03f, 0), 0.17f, 0.12f, k.Paint, 8);
            s.Box(new Vector3(0, seat.Y - 0.02f, seat.Z - 0.05f), new Vector3(0.28f, 0.07f, 0.42f), Black);
            // short tail and a plate hanger on the swingarm is what a naked roadster looks like
            s.Tube(new Vector3(0, seat.Y - 0.03f, seat.Z - 0.2f), new Vector3(0, seat.Y + 0.02f, seat.Z - 0.52f), 0.1f, 0.05f, k.Paint, 6);
            s.Box(new Vector3(0, seat.Y - 0.02f, seat.Z - 0.54f), new Vector3(0.12f, 0.04f, 0.05f), new Color(0.8f, 0.05f, 0.05f));
            // trellis subframe under the seat
            foreach (float x in new[] { -0.1f, 0.1f })
                s.Tube(pivot + new Vector3(x, 0.1f, 0.05f), new Vector3(x, seat.Y - 0.06f, seat.Z - 0.4f), 0.015f, k.Frame, 4);
        }
        else
        {
            s.Tube(tankFront, tankBack, 0.16f, 0.13f, k.Paint, 8);
            s.Box(new Vector3(0, seat.Y - 0.015f, seat.Z - 0.02f), new Vector3(0.24f, 0.05f, 0.36f), Black);
            // the R1's tail: slim, rising to a point well above the seat
            var tailTip = new Vector3(0, seat.Y + 0.14f, seat.Z - 0.62f);
            s.Tube(new Vector3(0, seat.Y - 0.06f, seat.Z - 0.12f), tailTip, 0.13f, 0.03f, k.Paint, 6);
            s.Box(tailTip + new Vector3(0, -0.04f, 0.04f), new Vector3(0.1f, 0.03f, 0.04f), new Color(0.8f, 0.05f, 0.05f));
            // plate hanger under the tail
            s.Tube(new Vector3(0, seat.Y - 0.12f, seat.Z - 0.35f), new Vector3(0, rear.Y + 0.12f, rear.Z - 0.12f), 0.02f, Black, 4);
            Fairing(s, k);
        }
        if (k.Style == MotoStyle.Adventure) Adventure(s, k);

        if (rider != null) HumanMeshBuilder.AppendRider(s, rider, k.Seat, k.Grip, k.Peg);
        return s.Build();
    }

    /// <summary>Inline four (R1) or parallel twin across the frame, tilted forward, headers into a low muffler.</summary>
    private static void Inline(MeshScratch s, int cylinders)
    {
        float width = cylinders * 0.1f;
        var crank = new Vector3(0, 0.38f, 0.1f);
        s.Box(crank, new Vector3(width + 0.02f, 0.26f, 0.4f), Black);
        var tilt = new Basis(Vector3.Right, 0.5f);   // cylinders lean forward ~30 deg
        s.Box(crank + new Vector3(0, 0.2f, 0.1f), new Vector3(width, 0.26f, 0.2f), new Color(0.2f, 0.2f, 0.22f), tilt);
        // radiator ahead of the cylinders
        s.Box(new Vector3(0, 0.5f, 0.42f), new Vector3(0.44f, 0.3f, 0.05f), Black, new Basis(Vector3.Right, 0.25f));
        for (int i = 0; i < cylinders; i++)
        {
            float x = -width * 0.5f + 0.05f + i * 0.1f;
            var port = new Vector3(x, 0.44f, 0.33f);
            s.Tube(port, new Vector3(x * 0.6f, 0.2f, 0.34f), 0.022f, Metal, 5);
            s.Tube(new Vector3(x * 0.6f, 0.2f, 0.34f), new Vector3(x * 0.3f, 0.14f, 0.05f), 0.022f, Metal, 5);
        }
        // short muffler under the engine, poking out on the right (−X in author space)
        s.Tube(new Vector3(0, 0.14f, 0.05f), new Vector3(-0.14f, 0.2f, -0.28f), 0.06f, 0.07f, new Color(0.25f, 0.25f, 0.27f));
    }

    /// <summary>Monster: 90° L-twin, front cylinder nearly flat, rear one upright, exhaust low on the right.</summary>
    private static void Twin(MeshScratch s, MotoLook k)
    {
        var crank = new Vector3(0, 0.36f, 0.02f);
        s.Box(crank, new Vector3(0.3f, 0.26f, 0.36f), Black);
        // front cylinder 21° above horizontal, rear 90° from it
        var front = crank + new Vector3(0, Mathf.Sin(0.37f), Mathf.Cos(0.37f)) * 0.36f;
        var back = crank + new Vector3(0, Mathf.Cos(0.37f), -Mathf.Sin(0.37f)) * 0.36f;
        s.Tube(crank, front, 0.1f, 0.09f, new Color(0.2f, 0.2f, 0.22f), 8);
        s.Tube(crank, back, 0.1f, 0.09f, new Color(0.2f, 0.2f, 0.22f), 8);
        s.Box(front, new Vector3(0.2f, 0.16f, 0.1f), k.Trim, new Basis(Vector3.Right, -0.37f));   // heads, cam belt covers
        s.Box(back, new Vector3(0.2f, 0.1f, 0.16f), k.Trim, new Basis(Vector3.Right, -0.37f));
        // radiator hung off the left of the front cylinder: a Monster tell
        s.Box(new Vector3(0.12f, 0.52f, 0.38f), new Vector3(0.24f, 0.26f, 0.05f), Black, new Basis(Vector3.Up, 0.5f));
        // headers down under the engine, stacked silencer on the right (−X)
        s.Tube(front + new Vector3(0, -0.05f, 0.06f), new Vector3(0, 0.14f, 0.2f), 0.025f, Metal, 5);
        s.Tube(new Vector3(0, 0.14f, 0.2f), new Vector3(-0.06f, 0.16f, -0.15f), 0.03f, Metal, 5);
        s.Tube(new Vector3(-0.06f, 0.16f, -0.15f), new Vector3(-0.15f, 0.26f, -0.42f), 0.07f, 0.06f, Black, 7);
    }

    private static void Fairing(MeshScratch s, MotoLook k)
    {
        var top = k.TopClamp;
        // the nose: a cone from the headlights forward of the clamps back to the tank
        var nose = new Vector3(0, top.Y - 0.12f, k.FrontAxle.Z - 0.02f);
        var body = new Vector3(0, top.Y - 0.14f, top.Z - 0.06f);
        s.Tube(nose, body, 0.06f, 0.21f, k.Paint, 8);
        // twin slit headlights either side of the ram air intake
        foreach (float x in new[] { -0.07f, 0.07f })
            s.Box(nose + new Vector3(x, 0.01f, 0.01f), new Vector3(0.08f, 0.025f, 0.03f), Lamp);
        s.Box(nose + new Vector3(0, -0.035f, 0.02f), new Vector3(0.06f, 0.05f, 0.03f), Black);
        // screen, raked back over the clamps
        s.Box(new Vector3(0, top.Y + 0.02f, top.Z + 0.06f), new Vector3(0.26f, 0.01f, 0.24f), Glass, new Basis(Vector3.Right, 0.6f));
        // side panels down past the engine, and the belly pan
        foreach (float x in new[] { -0.2f, 0.2f })
            s.Box(new Vector3(x, 0.52f, 0.36f), new Vector3(0.03f, 0.26f, 0.5f), k.Paint, new Basis(Vector3.Right, 0.35f));
        s.Box(new Vector3(0, 0.22f, 0.15f), new Vector3(0.34f, 0.1f, 0.5f), k.Trim);
        // mirrors on stalks
        foreach (float x in new[] { -0.26f, 0.26f })
            s.Box(new Vector3(x, top.Y - 0.02f, top.Z + 0.08f), new Vector3(0.11f, 0.05f, 0.03f), Black);
    }

    /// <summary>
    /// Everything that turns with the bars, centred on the top triple clamp (the node's pivot on
    /// the steering axis): fork legs, clamps, bars, mudguard and a naked bike's round headlight.
    /// </summary>
    public static ArrayMesh BuildFront(MotoLook k)
    {
        var s = new MeshScratch();
        var p = k.TopClamp;
        var axle = k.FrontAxle - p;
        var up = k.SteerAxis;
        var top = Vector3.Zero;
        var low = up * -0.2f;

        foreach (float x in new[] { -0.1f, 0.1f })
        {
            var off = new Vector3(x, 0, 0);
            s.Tube(axle + off, axle + off + up * 0.3f, 0.034f, 0.034f, Black);             // lower leg
            s.Tube(axle + off + up * 0.3f, top + off + up * 0.03f, 0.024f, Metal);         // stanchion
        }
        s.Box(top, new Vector3(0.26f, 0.03f, 0.08f), k.Trim);
        s.Box(low, new Vector3(0.26f, 0.04f, 0.08f), k.Trim);
        // front mudguard over the tyre
        s.Box(axle + up * (k.FrontRadius + 0.03f), new Vector3(0.14f, 0.02f, 0.28f), k.Paint);

        var grip = k.Grip - p;
        foreach (float side in new[] { -1f, 1f })
        {
            var g = grip with { X = side * Mathf.Abs(grip.X) };
            if (k.Style == MotoStyle.Sport)
            {
                // clip-ons: under the top clamp, angled down and out
                var clamp = new Vector3(side * 0.1f, g.Y + 0.02f, g.Z + 0.03f);
                s.Tube(clamp, g, 0.016f, Metal, 5);
                s.Tube(g, g + new Vector3(side * 0.11f, -0.01f, -0.02f), 0.022f, Black, 6);
            }
            else
            {
                // a tubular bar on risers: up from the clamp and wide
                var riser = new Vector3(side * 0.04f, 0.05f, -0.01f);
                s.Tube(new Vector3(side * 0.04f, 0, 0), riser, 0.018f, Black, 5);
                s.Tube(riser, g, 0.013f, Black, 6);
                s.Tube(g, g + new Vector3(side * 0.11f, 0, -0.02f), 0.022f, Black, 6);
            }
        }
        if (k.Style == MotoStyle.Naked)
        {
            // the Monster's round headlight ahead of the clamps, and a clock above it
            var lamp = low + new Vector3(0, 0.02f, 0.14f);
            s.Tube(lamp - new Vector3(0, 0, 0.06f), lamp + new Vector3(0, 0, 0.04f), 0.09f, 0.1f, Black, 10);
            s.Tube(lamp + new Vector3(0, 0, 0.04f), lamp + new Vector3(0, 0, 0.05f), 0.085f, Lamp, 10);
            s.Box(top + new Vector3(0, 0.05f, 0.06f), new Vector3(0.14f, 0.08f, 0.02f), Black, new Basis(Vector3.Right, -0.4f));
        }
        return s.Build();
    }

    /// <summary>Adventure: tall screen over a twin-lamp nose, the high "beak" mudguard, a sump guard.</summary>
    private static void Adventure(MeshScratch s, MotoLook k)
    {
        var top = k.TopClamp;
        var nose = new Vector3(0, top.Y - 0.05f, top.Z + 0.14f);
        s.Box(nose, new Vector3(0.3f, 0.2f, 0.16f), k.Paint, new Basis(Vector3.Right, 0.35f));
        foreach (float x in new[] { -0.06f, 0.06f })
            s.Box(nose + new Vector3(x, 0, 0.085f), new Vector3(0.09f, 0.06f, 0.02f), Lamp, new Basis(Vector3.Right, 0.35f));
        s.Box(nose + new Vector3(0, 0.25f, -0.06f), new Vector3(0.34f, 0.01f, 0.4f), Glass, new Basis(Vector3.Right, 1.1f));   // the tall screen
        s.Box(nose + new Vector3(0, -0.14f, 0.12f), new Vector3(0.14f, 0.03f, 0.3f), k.Trim, new Basis(Vector3.Right, 0.3f));  // beak
        s.Box(new Vector3(0, 0.17f, 0.15f), new Vector3(0.3f, 0.04f, 0.5f), Metal);   // sump guard
    }

    /// <summary>One wheel about its own axle: tyre, rim, Y-spokes or wire spokes, hub, disc(s).</summary>
    public static ArrayMesh BuildWheel(MotoLook k, bool front)
    {
        var s = new MeshScratch();
        float r = front ? k.FrontRadius : k.RearRadius;
        float w = front ? k.FrontWidth : k.RearWidth;
        float rim = Tyre.RimRadius(front ? k.FrontTyre : k.RearTyre);
        s.Ring(Vector3.Zero, Vector3.Right, rim + 0.01f, r, w, Black, 18);
        s.Ring(Vector3.Zero, Vector3.Right, rim - 0.02f, rim + 0.012f, w * 0.75f, k.Wheel, 18);
        for (int i = 0; i < (k.Spoked ? 0 : 5); i++)
        {
            float a = Mathf.Tau * i / 5f;
            var dir = new Vector3(0, Mathf.Cos(a), Mathf.Sin(a));
            var side = new Vector3(0, -Mathf.Sin(a), Mathf.Cos(a));
            // a Y: one arm splitting in two at the rim
            var mid = dir * (rim * 0.6f);
            s.Tube(dir * 0.05f, mid, 0.018f, 0.014f, k.Wheel, 4);
            s.Tube(mid, dir * (rim - 0.02f) + side * 0.03f, 0.012f, k.Wheel, 4);
            s.Tube(mid, dir * (rim - 0.02f) - side * 0.03f, 0.012f, k.Wheel, 4);
        }
        if (k.Spoked)
            for (int i = 0; i < 12; i++)
            {
                // wire spokes crossing from alternate hub flanges
                float a = Mathf.Tau * i / 12f;
                var dir = new Vector3(0, Mathf.Cos(a), Mathf.Sin(a));
                float flange = (i % 2 == 0 ? 1f : -1f) * 0.04f;
                s.Tube(new Vector3(flange, 0, 0) + dir * 0.05f, dir * (rim - 0.015f), 0.004f, Metal, 3);
            }
        s.Tube(new Vector3(-w * 0.45f, 0, 0), new Vector3(w * 0.45f, 0, 0), 0.05f, Metal, 8);
        if (front)
            foreach (float x in new[] { -0.075f, 0.075f })
                s.Ring(new Vector3(x, 0, 0), Vector3.Right, 0.09f, 0.16f, 0.006f, Disc, 16);   // twin 320 mm discs
        else
            s.Ring(new Vector3(0.07f, 0, 0), Vector3.Right, 0.06f, 0.11f, 0.006f, Disc, 14);
        return s.Build();
    }
}

/// <summary>
/// A motorbike and (optionally) its rider as a node: the body is one mesh, the front end steers
/// about the raked axis, and both wheels spin. The owner sets <see cref="SteerAngle"/> and
/// <see cref="WheelSpin"/>; a remote copy is fed the same from <c>WritePose</c>. Faces −Z.
/// </summary>
public partial class Motorcyclist : Node3D
{
    /// <summary>Bars angle about the steering axis, radians, + = left.</summary>
    public float SteerAngle { get; set; }
    /// <summary>Accumulated wheel rotation, radians; positive rolls forward.</summary>
    public float WheelSpin { get; set; }

    private MotoLook _look = new();
    private Node3D _steer = null!, _frontSpin = null!, _rearSpin = null!;
    private Vector3 _steerAxis;

    public static Motorcyclist Create(MotoLook look, int riderIndex, bool rider = true)
    {
        var node = new Motorcyclist { Name = "Motorbike", _look = look };
        node.Assemble(rider ? HumanPalette.ForRider(riderIndex) : null);
        return node;
    }

    // author (+Z forward) to node space (−Z forward): what MeshScratch.Build does to every vertex
    private static Vector3 Flip(Vector3 v) => new(-v.X, v.Y, -v.Z);

    private void Assemble(HumanPalette? rider)
    {
        var material = HumanMeshBuilder.Material();
        var k = _look;
        AddChild(new MeshInstance3D { Name = "Body", Mesh = MotorbikeMeshBuilder.BuildBody(k, rider), MaterialOverride = material });

        _steerAxis = Flip(k.SteerAxis).Normalized();
        _steer = new Node3D { Name = "Front", Position = Flip(k.TopClamp) };
        AddChild(_steer);
        _steer.AddChild(new MeshInstance3D { Mesh = MotorbikeMeshBuilder.BuildFront(k), MaterialOverride = material });
        _frontSpin = new Node3D { Name = "FrontWheel", Position = Flip(k.FrontAxle) - Flip(k.TopClamp) };
        _frontSpin.AddChild(new MeshInstance3D { Mesh = MotorbikeMeshBuilder.BuildWheel(k, front: true), MaterialOverride = material });
        _steer.AddChild(_frontSpin);
        _rearSpin = new Node3D { Name = "RearWheel", Position = Flip(k.RearAxle) };
        _rearSpin.AddChild(new MeshInstance3D { Mesh = MotorbikeMeshBuilder.BuildWheel(k, front: false), MaterialOverride = material });
        AddChild(_rearSpin);
    }

    public override void _Process(double delta)
    {
        if (_steer == null) return;
        _steer.Basis = new Basis(_steerAxis, SteerAngle);
        // top edge toward −Z (forward), as CarRig
        _frontSpin.Rotation = new Vector3(-WheelSpin * _look.RearRadius / _look.FrontRadius, 0, 0);
        _rearSpin.Rotation = new Vector3(-WheelSpin, 0, 0);
    }
}
