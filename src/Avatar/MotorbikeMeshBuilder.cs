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
    /// <summary>Third livery colour (a tricolour's red: stripes, subframe, tail light surround).</summary>
    public Color Accent { get; init; } = new(0.8f, 0.06f, 0.07f);
    public Color SeatColor { get; init; } = new(0.07f, 0.07f, 0.08f);

    // ---- adventure bodywork (MotoStyle.Adventure) ----
    /// <summary>Angle between a V-twin's cylinders, degrees (Monster 90, Africa Twin XRV 52).</summary>
    public float VAngleDeg { get; init; } = 90f;
    /// <summary>Front discs and their diameter, mm.</summary>
    public int FrontDiscs { get; init; } = 2;
    public float FrontDiscMm { get; init; } = 320f;
    /// <summary>Fuel tank, litres: sizes an adventure bike's tank (0 = the style's default).</summary>
    public float TankLitres { get; init; }
    /// <summary>Ground clearance, m: where the sump guard sits.</summary>
    public float GroundClearance { get; init; } = 0.13f;
    /// <summary>Screen height above the headlight cowl, m.</summary>
    public float ScreenHeight { get; init; } = 0.25f;
    /// <summary>A frame-mounted half fairing with twin round headlights (XRV) rather than the CRF's narrow LED beak.</summary>
    public bool HalfFairing { get; init; }
    /// <summary>Tubular crash bars around the fairing (Adventure Sports).</summary>
    public bool CrashBars { get; init; }

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
    private static readonly Color Screen = new(0.52f, 0.6f, 0.68f);   // a tall clear screen reads lighter than a sport bike's smoked bubble

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

        bool adv = k.Style == MotoStyle.Adventure;
        if (k.EngineShape == MotoEngineShape.VTwin) Twin(s, k); else Inline(s, k.EngineShape == MotoEngineShape.InlineFour ? 4 : 2, muffler: !adv);

        // --- frame: head stock to swingarm pivot ---
        s.Tube(headLow, top, 0.045f, k.Frame);
        if (k.Style == MotoStyle.Naked)
        {
            // Monster: a short frame from the head onto the cylinder heads, the engine carries the rest
            foreach (float x in new[] { -0.09f, 0.09f })
                s.Tube(top + new Vector3(x * 0.6f, -0.03f, -0.02f), new Vector3(x, 0.66f, 0.05f), 0.035f, 0.03f, k.Frame);
        }
        else if (adv)
        {
            // steel semi-double cradle: spars over the engine to the pivot, down tubes to the sump guard
            foreach (float x in new[] { -0.12f, 0.12f })
            {
                var bend = new Vector3(x, 0.8f, 0.02f);
                s.Tube(headLow + new Vector3(x * 0.4f, 0.04f, -0.03f), bend, 0.03f, k.Frame, 5);
                s.Tube(bend, pivot + new Vector3(x, 0.04f, 0), 0.03f, k.Frame, 5);
                s.Tube(headLow + new Vector3(x * 0.3f, -0.02f, 0), new Vector3(x * 0.8f, k.GroundClearance + 0.08f, 0.32f), 0.026f, k.Frame, 5);
            }
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
        if (adv) Adventure(s, k, tankFront, pivot);
        else if (k.Style != MotoStyle.Sport)
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

        if (rider != null) HumanMeshBuilder.AppendRider(s, rider, k.Seat, k.Grip, k.Peg);
        return s.Build();
    }

    /// <summary>Inline four (R1) or parallel twin across the frame, tilted forward, headers into a low muffler.</summary>
    private static void Inline(MeshScratch s, int cylinders, bool muffler = true)
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
            // headers down the front of the engine; an adventure bike's stop above its sump guard and route on from there
            float low = muffler ? 0.2f : 0.34f;
            s.Tube(port, new Vector3(x * 0.6f, low, 0.34f), 0.022f, Metal, 5);
            if (muffler) s.Tube(new Vector3(x * 0.6f, 0.2f, 0.34f), new Vector3(x * 0.3f, 0.14f, 0.05f), 0.022f, Metal, 5);
        }
        // short muffler under the engine, poking out on the right (−X in author space)
        if (muffler) s.Tube(new Vector3(0, 0.14f, 0.05f), new Vector3(-0.14f, 0.2f, -0.28f), 0.06f, 0.07f, new Color(0.25f, 0.25f, 0.27f));
    }

    /// <summary>
    /// A V-twin across the frame: the Monster's 90° L (front cylinder nearly flat, rear one upright,
    /// exhaust low on the right) or the XRV's upright 52° V, split either side of vertical.
    /// </summary>
    private static void Twin(MeshScratch s, MotoLook k)
    {
        var crank = new Vector3(0, k.Style == MotoStyle.Adventure ? k.GroundClearance + 0.2f : 0.36f, 0.02f);
        s.Box(crank, new Vector3(0.3f, 0.26f, 0.36f), Black);
        // front cylinder's elevation above horizontal: the Monster's 21°, else the V split about 6° forward of vertical
        float v = Mathf.DegToRad(k.VAngleDeg);
        float e = k.VAngleDeg >= 89f ? 0.37f : Mathf.Pi / 2f - v / 2f - 0.1f;
        var front = crank + new Vector3(0, Mathf.Sin(e), Mathf.Cos(e)) * 0.36f;
        var back = crank + new Vector3(0, Mathf.Sin(e + v), Mathf.Cos(e + v)) * 0.36f;
        var head = k.Style == MotoStyle.Adventure ? Metal : k.Trim;
        s.Tube(crank, front, 0.1f, 0.09f, new Color(0.2f, 0.2f, 0.22f), 8);
        s.Tube(crank, back, 0.1f, 0.09f, new Color(0.2f, 0.2f, 0.22f), 8);
        s.Box(front, new Vector3(0.2f, 0.16f, 0.1f), head, new Basis(Vector3.Right, Mathf.Pi / 2f - e));   // heads
        s.Box(back, new Vector3(0.2f, 0.16f, 0.1f), head, new Basis(Vector3.Right, Mathf.Pi / 2f - e - v));
        // radiator hung off the left of the front cylinder: a Monster tell
        if (k.Style == MotoStyle.Naked)
            s.Box(new Vector3(0.12f, 0.52f, 0.38f), new Vector3(0.24f, 0.26f, 0.05f), Black, new Basis(Vector3.Up, 0.5f));
        if (k.Style == MotoStyle.Adventure) return;   // the adventure body routes its own exhaust
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
        if (k.Style == MotoStyle.Adventure)
            foreach (float side in new[] { -1f, 1f })
            {
                // knuckle guards over the grip ends
                var g = grip with { X = side * Mathf.Abs(grip.X) };
                s.Box(g + new Vector3(side * 0.06f, 0.02f, 0.07f), new Vector3(0.14f, 0.07f, 0.02f), k.Paint, new Basis(Vector3.Up, side * -0.3f));
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

    /// <summary>
    /// An Africa Twin: the big tank between radiator shrouds, a long flat seat onto a luggage rack,
    /// a high silencer on the right, a sump guard at the published ground clearance, and at the
    /// front either the CRF's narrow cowl (twin LED lamps, the "beak" under them, a screen over) or
    /// the XRV's frame-mounted half fairing with two round headlights. Crash bars on the Adventure Sports.
    /// </summary>
    private static void Adventure(MeshScratch s, MotoLook k, Vector3 tankFront, Vector3 pivot)
    {
        var top = k.TopClamp;
        var seat = k.Seat;
        float gc = k.GroundClearance;

        // --- tank: its volume sets its girth (18.8 L CRF, 24-25 L XRV / Adventure Sports) ---
        float girth = Mathf.Sqrt(Mathf.Max(k.TankLitres, 10f) / 18.8f);
        var tankBack = new Vector3(0, seat.Y + 0.02f, seat.Z + 0.24f);
        s.Tube(tankFront + new Vector3(0, -0.04f, 0.02f), tankBack, 0.16f * girth, 0.11f, k.Paint, 8);
        s.Box(tankFront + new Vector3(0, 0.06f * girth, -0.08f), new Vector3(0.05f, 0.012f, 0.26f), k.Accent);   // centre stripe

        // --- radiator shrouds flanking the tank, the livery's second colour low on them ---
        foreach (float x in new[] { -1f, 1f })
        {
            var shroud = new Vector3(x * (0.13f + 0.04f * girth), top.Y - 0.3f, top.Z - 0.2f);
            s.Box(shroud, new Vector3(0.03f, 0.3f, 0.36f), k.Paint, new Basis(Vector3.Right, 0.35f));
            s.Box(shroud + new Vector3(x * 0.018f, -0.07f, -0.02f), new Vector3(0.006f, 0.12f, 0.3f), k.Trim, new Basis(Vector3.Right, 0.35f));
            s.Box(shroud + new Vector3(x * 0.02f, 0.04f, 0.02f), new Vector3(0.006f, 0.035f, 0.28f), k.Accent, new Basis(Vector3.Right, 0.35f));
        }

        // --- side covers under the seat, seat, tail, luggage rack ---
        s.Box(new Vector3(0, seat.Y - 0.17f, seat.Z - 0.12f), new Vector3(0.24f, 0.2f, 0.36f), k.Paint, new Basis(Vector3.Right, -0.15f));
        s.Box(new Vector3(0, seat.Y - 0.2f, seat.Z - 0.1f), new Vector3(0.25f, 0.05f, 0.3f), k.Trim, new Basis(Vector3.Right, -0.15f));
        s.Box(new Vector3(0, seat.Y - 0.025f, seat.Z - 0.12f), new Vector3(0.28f, 0.07f, 0.66f), k.SeatColor);
        var tailEnd = new Vector3(0, seat.Y + 0.0f, seat.Z - 0.78f);
        s.Tube(new Vector3(0, seat.Y - 0.06f, seat.Z - 0.3f), tailEnd, 0.11f, 0.05f, k.Paint, 6);
        s.Box(tailEnd + new Vector3(0, -0.02f, -0.01f), new Vector3(0.11f, 0.04f, 0.03f), new Color(0.8f, 0.05f, 0.05f));
        var rack = new Vector3(0, seat.Y + 0.04f, seat.Z - 0.66f);
        s.Box(rack, new Vector3(0.3f, 0.02f, 0.32f), Black);
        foreach (float x in new[] { -0.15f, 0.15f })
        {
            s.Tube(rack + new Vector3(x, 0, 0.18f), rack + new Vector3(x, 0, -0.16f), 0.012f, Metal, 4);
            // subframe rails under the seat, pivot to rack
            s.Tube(pivot + new Vector3(x * 0.7f, 0.12f, 0.02f), rack + new Vector3(x * 0.7f, -0.08f, 0.1f), 0.018f, k.Accent, 4);
        }
        // rear mudguard and plate hanger
        s.Tube(tailEnd + new Vector3(0, -0.06f, 0), k.RearAxle + new Vector3(0, k.RearRadius + 0.05f, -0.28f), 0.05f, 0.03f, Black, 5);

        // --- exhaust: header under the engine, high silencer on the right (−X) beside the rack ---
        s.Tube(new Vector3(0, gc + 0.1f, 0.34f), new Vector3(-0.08f, gc + 0.12f, -0.1f), 0.028f, Metal, 5);
        s.Tube(new Vector3(-0.08f, gc + 0.12f, -0.1f), new Vector3(-0.15f, seat.Y - 0.3f, seat.Z - 0.3f), 0.03f, Metal, 5);
        s.Tube(new Vector3(-0.15f, seat.Y - 0.3f, seat.Z - 0.3f), new Vector3(-0.17f, seat.Y - 0.2f, seat.Z - 0.64f), 0.075f, 0.065f, new Color(0.22f, 0.22f, 0.24f), 8);

        // --- sump guard at the ground clearance, its nose swept up in front of the engine ---
        s.Box(new Vector3(0, gc + 0.015f, 0.12f), new Vector3(0.3f, 0.03f, 0.44f), Metal);
        s.Box(new Vector3(0, gc + 0.1f, 0.4f), new Vector3(0.3f, 0.03f, 0.22f), Metal, new Basis(Vector3.Right, -0.9f));

        // --- the front: cowl or fairing, lamps, screen ---
        Vector3 face;
        if (k.HalfFairing)
        {
            // XRV: a broad fairing on the frame, round twin headlights side by side
            face = new Vector3(0, top.Y - 0.12f, top.Z + 0.26f);
            s.Tube(face, new Vector3(0, top.Y - 0.14f, top.Z - 0.12f), 0.17f, 0.27f, k.Paint, 8);
            s.Box(face + new Vector3(0, 0, -0.05f), new Vector3(0.34f, 0.26f, 0.04f), k.Paint);   // the tube is open-ended: close its nose
            foreach (float x in new[] { -0.08f, 0.08f })
                s.Tube(face + new Vector3(x, 0, -0.02f), face + new Vector3(x, 0, 0.012f), 0.065f, Lamp, 10);
            s.Box(face + new Vector3(0, -0.09f, -0.02f), new Vector3(0.34f, 0.025f, 0.05f), k.Accent);
            foreach (float x in new[] { -1f, 1f })   // flanks sweeping back into the tank
                s.Box(new Vector3(x * 0.2f, top.Y - 0.2f, top.Z - 0.18f), new Vector3(0.03f, 0.3f, 0.34f), k.Paint, new Basis(Vector3.Right, 0.3f));
        }
        else
        {
            // CRF: a narrow cowl on the frame, two LED lamps side by side under a DRL line, the beak beneath
            face = new Vector3(0, top.Y - 0.1f, top.Z + 0.22f);
            s.Box(face + new Vector3(0, 0, -0.1f), new Vector3(0.26f, 0.24f, 0.24f), k.Paint, new Basis(Vector3.Right, 0.3f));
            foreach (float x in new[] { -0.055f, 0.055f })
                s.Box(face + new Vector3(x, -0.02f, 0.02f), new Vector3(0.08f, 0.07f, 0.02f), Lamp, new Basis(Vector3.Right, 0.3f));
            s.Box(face + new Vector3(0, 0.06f, 0.0f), new Vector3(0.2f, 0.015f, 0.02f), Lamp, new Basis(Vector3.Right, 0.3f));
            // the beak: a raked upper fender pointing forward and down from under the lamps
            s.Tube(face + new Vector3(0, -0.12f, -0.02f), face + new Vector3(0, -0.24f, 0.2f), 0.09f, 0.03f, k.Paint, 6);
            s.Box(face + new Vector3(0, -0.14f, 0.06f), new Vector3(0.1f, 0.012f, 0.18f), k.Accent, new Basis(Vector3.Right, 0.5f));
            foreach (float x in new[] { -1f, 1f })   // cowl side panels
                s.Box(new Vector3(x * 0.15f, top.Y - 0.12f, top.Z + 0.02f), new Vector3(0.025f, 0.26f, 0.3f), k.Trim, new Basis(Vector3.Right, 0.3f));
        }
        // screen, raked back, its height from the data
        float h = k.ScreenHeight;
        var screenBase = face + new Vector3(0, 0.12f, -0.06f);
        s.Box(screenBase + new Vector3(0, Mathf.Cos(0.45f), -Mathf.Sin(0.45f)) * (h * 0.5f), new Vector3(0.32f, h, 0.035f), Screen, new Basis(Vector3.Right, -0.45f));
        // mirrors on stalks
        foreach (float x in new[] { -0.3f, 0.3f })
        {
            s.Tube(new Vector3(x * 0.6f, top.Y - 0.04f, top.Z + 0.1f), new Vector3(x, top.Y + 0.16f, top.Z + 0.06f), 0.008f, Black, 4);
            s.Box(new Vector3(x, top.Y + 0.18f, top.Z + 0.06f), new Vector3(0.1f, 0.06f, 0.02f), Black);
        }

        if (!k.CrashBars) return;
        // Adventure Sports: tubular bars around the cowl, down past the radiator
        foreach (float x in new[] { -1f, 1f })
        {
            var a = new Vector3(x * 0.16f, top.Y - 0.08f, top.Z - 0.08f);
            var b = new Vector3(x * 0.27f, top.Y - 0.3f, top.Z - 0.02f);
            var c = new Vector3(x * 0.25f, gc + 0.25f, 0.3f);
            var d = new Vector3(x * 0.14f, gc + 0.12f, 0.2f);
            s.Tube(a, b, 0.015f, Black, 5);
            s.Tube(b, c, 0.015f, Black, 5);
            s.Tube(c, d, 0.015f, Black, 5);
        }
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
            for (int i = 0; i < 18; i++)
            {
                // wire spokes crossing from alternate hub flanges
                float a = Mathf.Tau * i / 18f;
                var dir = new Vector3(0, Mathf.Cos(a), Mathf.Sin(a));
                float flange = (i % 2 == 0 ? 1f : -1f) * 0.04f;
                s.Tube(new Vector3(flange, 0, 0) + dir * 0.05f, dir * (rim - 0.015f), 0.004f, Metal, 3);
            }
        s.Tube(new Vector3(-w * 0.45f, 0, 0), new Vector3(w * 0.45f, 0, 0), 0.05f, Metal, 8);
        float disc = k.FrontDiscMm * 0.0005f;
        if (front)
            foreach (float x in k.FrontDiscs == 1 ? new[] { 0.075f } : new[] { -0.075f, 0.075f })
                s.Ring(new Vector3(x, 0, 0), Vector3.Right, disc * 0.56f, disc, 0.006f, Disc, 16);
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

    /// <summary>
    /// The rider's place and the pillion's behind it (#158), node space: the pillion a third of a
    /// metre back and a little higher, hands on the rider's waist, feet on pegs behind the rider's.
    /// </summary>
    public static SeatAnchor[] SeatsFor(MotoLook k) => new[]
    {
        new SeatAnchor(0, Flip(k.Seat), 0f, 0f) { Pose = SeatPose.Straddle, Grip = Flip(k.Grip), Peg = Flip(k.Peg) },
        new SeatAnchor(0, Flip(k.Seat + new Vector3(0, 0.07f, -0.34f)), 0f, 0f)
        {
            Pose = SeatPose.Straddle,
            Grip = Flip(k.Seat + new Vector3(-0.16f, 0.24f, -0.14f)),
            Peg = Flip(k.Peg with { Y = k.Peg.Y + 0.09f, Z = k.Peg.Z - 0.3f }),
        },
    };

    public SeatAnchor[] Seats => SeatsFor(_look);
    private Node3D _steer = null!, _frontSpin = null!, _rearSpin = null!;
    private Vector3 _steerAxis;

    public static Motorcyclist Create(MotoLook look, int riderIndex, bool rider = true, Outfit outfit = default)
    {
        var node = new Motorcyclist { Name = "Motorbike", _look = look };
        node.Assemble(rider ? HumanPalette.ForRider(riderIndex) with { Outfit = outfit } : null);
        return node;
    }

    // author (+Z forward) to node space (−Z forward): what MeshScratch.Build does to every vertex
    private static Vector3 Flip(Vector3 v) => new(-v.X, v.Y, -v.Z);

    private void Assemble(HumanPalette? rider)
    {
        var k = _look;
        // a dressed rider (#251) is a mesh of their own, in the figure shader, rebuilt while a skirt
        // blows; the plain one stays baked into the bike's single body mesh
        bool dressed = rider is { Outfit.IsEmpty: false };
        Material material = dressed ? HumanMeshBuilder.FigureMaterial() : HumanMeshBuilder.Material();
        AddChild(new MeshInstance3D { Name = "Body", Mesh = MotorbikeMeshBuilder.BuildBody(k, dressed ? null : rider), MaterialOverride = material });
        if (dressed)
        {
            _rider = rider;
            _riderMesh = new MeshInstance3D { Name = "Rider", Mesh = BuildRider(rider!), MaterialOverride = material };
            AddChild(_riderMesh);
        }

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

    private HumanPalette? _rider;
    private MeshInstance3D? _riderMesh;
    private readonly FigureWind _wind = new();

    private ArrayMesh BuildRider(HumanPalette rider)
    {
        var s = new MeshScratch();
        HumanMeshBuilder.AppendRider(s, rider, _look.Seat, _look.Grip, _look.Peg);
        return s.Build();
    }

    public override void _Process(double delta)
    {
        if (_steer == null) return;
        // the ride's wind in a skirt: measured from the bike's own motion, so remote copies blow too
        if (_rider is { } r && _riderMesh != null && HumanMeshBuilder.Flutters(r.Outfit))
            _riderMesh.Mesh = BuildRider(r with { Wind = _wind.Update(_riderMesh, (float)delta) });
        _steer.Basis = new Basis(_steerAxis, SteerAngle);
        // top edge toward −Z (forward), as CarRig
        _frontSpin.Rotation = new Vector3(-WheelSpin * _look.RearRadius / _look.FrontRadius, 0, 0);
        _rearSpin.Rotation = new Vector3(-WheelSpin, 0, 0);
    }
}
