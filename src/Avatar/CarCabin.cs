using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Full-scale readings of a car's two dials: the speedometer's top, the rev counter's top and
/// where its red band starts (km/h and rpm).
/// </summary>
public readonly record struct CarGauges(float SpeedoKmh, float TachRpm, float RedlineRpm)
{
    public static CarGauges Default => new(260f, 9000f, 7500f);

    /// <summary>Round tops above a car's real numbers: the needle never pins, the red band starts at the redline.</summary>
    public static CarGauges For(float topSpeedKmh, float redlineRpm) => new(
        Mathf.Clamp(Mathf.Ceil(topSpeedKmh * 1.1f / 20f) * 20f, 140f, 360f),
        Mathf.Ceil((redlineRpm + 900f) / 1000f) * 1000f,
        redlineRpm);
}

/// <summary>
/// Where the driver sits and what they touch, in the car's authored frame (+Z forward, ground
/// origin; the driver's right is −X). Derived from the body, never placed by eye
/// (<c>riding-position-derived-from-bike</c>): <see cref="HumanMeshBuilder.AppendDriver"/> poses
/// the figure from it and the rig puts the camera at its eye.
/// </summary>
/// <param name="Hip">The hip joint's midpoint, over the cushion.</param>
/// <param name="Recline">The seat back's lean from upright, radians.</param>
/// <param name="WheelCentre">The steering wheel's hub.</param>
/// <param name="WheelAxis">Along the column, from the wheel toward the driver (unit).</param>
/// <param name="WheelRadius">To the middle of the rim.</param>
/// <param name="Throttle">The ball of the foot on the throttle pad, at rest.</param>
/// <param name="Brake">The same on the brake pad.</param>
/// <param name="Rest">Where the left foot rests (the clutch pad).</param>
public sealed record DriverSeat(Vector3 Hip, float Recline, Vector3 WheelCentre, Vector3 WheelAxis, float WheelRadius,
    Vector3 Throttle, Vector3 Brake, Vector3 Rest)
{
    /// <summary>A pedal's hinge, from its pad: the arm hangs from the bulkhead above and ahead of it.</summary>
    public static readonly Vector3 PedalHinge = new(0, 0.22f, 0.07f);
    /// <summary>How far a pedal swings about its hinge, floored, radians.</summary>
    public const float PedalTravel = 0.3f;

    /// <summary>
    /// How far the hands turn with the wheel before the rim slides through them, rad: a car's
    /// upright wheel far round (<see cref="HumanMeshBuilder.MaxGripTurn"/>), a truck's flat one
    /// less, its far side being out of reach.
    /// </summary>
    public float MaxGrip { get; init; } = HumanMeshBuilder.MaxGripTurn;

    /// <summary>A pad pressed <paramref name="amount"/> (0..1) down: it swings forward about its hinge.</summary>
    public static Vector3 Pressed(Vector3 pad, float amount)
    {
        var hinge = pad + PedalHinge;
        return hinge + new Basis(Vector3.Right, -PedalTravel * Mathf.Clamp(amount, 0f, 1f)) * (pad - hinge);
    }
}

/// <summary>A dial's needle: its mesh built pointing straight up, the pivot and the axis it turns about (node space).</summary>
public sealed record CarNeedle(ArrayMesh Mesh, Vector3 Pivot, Vector3 Axis)
{
    /// <summary>The needle's turn for a reading <paramref name="t"/> (0 empty .. 1 full scale): from 7:30, clockwise, to 4:30.</summary>
    public static float Angle(float t) => Sweep * 0.5f - Sweep * Mathf.Clamp(t, 0f, 1.04f);
    public const float Sweep = 1.5f * Mathf.Pi;
}

/// <summary>A mirror's reflecting face, node space: its middle, the way it faces and its size (m).</summary>
public sealed record CarMirror(string Name, Vector3 Centre, Vector3 Normal, Vector2 Size);

/// <summary>
/// What is inside a car: the cabin (lit), the instruments (unshaded, backlit), and the parts that
/// move — the steering wheel on its column, the two needles, the gear digit, three warning lamps
/// and the pedals. Everything is in node space like the shell (−Z forward, ground origin).
/// </summary>
public sealed record CarCabin(
    ArrayMesh Interior, ArrayMesh Instruments,
    HingedPart SteeringWheel, Vector3 ColumnAxis,
    CarNeedle Tach, CarNeedle Speedo, CarGauges Gauges,
    ArrayMesh[] GearDigits, ArrayMesh[] Lamps, HingedPart[] Pedals,
    CarMirror[] Mirrors, DriverSeat Seat, Vector3 Eye, SeatAnchor[] Seats)
{
    /// <summary>
    /// A digital speedometer's figures (<see cref="CarBody.CentreDisplay"/>, #760): per place, the
    /// meshes for 0..9, in readouts of <see cref="SpeedPlaces"/> places, units first (the display,
    /// then the windscreen's <see cref="CarBody.Hud"/>). Null on a car with dials; its needles then
    /// draw nothing.
    /// </summary>
    public ArrayMesh[][]? SpeedDigits { get; init; }
    /// <summary>Places in one speed readout: up to 999 km/h.</summary>
    public const int SpeedPlaces = 3;
    /// <summary>Index into <see cref="GearDigits"/>: reverse, neutral, then 1..6.</summary>
    public static int DigitFor(int gear) => gear < 0 ? 0 : Mathf.Clamp(gear + 1, 1, 7);
    /// <summary>Indices into <see cref="Lamps"/>.</summary>
    public const int LampHandbrake = 0, LampLights = 1, LampEngine = 2;
    /// <summary>Indices into <see cref="Pedals"/>.</summary>
    public const int PedalThrottle = 0, PedalBrake = 1, PedalClutch = 2;
}

public static partial class CarMeshBuilder
{
    private static readonly Color Carpet = new(0.1f, 0.09f, 0.09f);
    private static readonly Color Seat = new(0.12f, 0.12f, 0.14f);
    private static readonly Color Dash = new(0.09f, 0.09f, 0.1f);
    private static readonly Color Digit = new(1f, 0.7f, 0.2f);

    /// <summary>A car's wheel, column, dials' plane, pedals and mirror housings (<see cref="CockpitKit"/>).</summary>
    /// <remarks>Lazy: a static field here could initialise before <c>Trim</c> (CarMeshBuilder.cs) and take it black.</remarks>
    private static CockpitSpec Kit => _kit ??= new(Trim, Dash,
        ColumnFrom: 0.04f, ColumnTo: 0.3f, ColumnRadius: 0.035f, ColumnFoot: 0.045f,
        RimIn: 0.022f, RimOut: 0.006f, RimDepth: 0.03f, RimSides: 14,
        HubBack: 0.02f, Hub: new Vector3(0.1f, 0.09f, 0.05f), SpokeBack: 0.015f, SpokeShort: 0.015f, SpokeRadius: 0.014f,
        Mark: new Vector3(0.03f, 0.02f, 0.034f),
        DialsAhead: 0.13f,
        PedalArm: 0.01f, ThrottlePad: new Vector3(0.05f, 0.11f, 0.015f), Pad: new Vector3(0.075f, 0.07f, 0.015f),
        MirrorRim: 0.02f);
    private static CockpitSpec? _kit;

    /// <summary>The seat and everything the driver reaches, for this body. Cheap: no meshes.</summary>
    public static DriverSeat SeatFor(CarBody body, float wheelbase) =>
        body.Shape == BodyShape.Kart ? KartMeshBuilder.SeatFor(wheelbase) : SeatFor(For(body, wheelbase), body.LeftHandDrive);

    internal static DriverSeat SeatFor(Dims d, bool leftHandDrive)
    {
        float cw = d.Width - 0.2f;
        // right-hand drive (the Initial D cars are Japanese) puts the driver on −X authored, the
        // driver's right; left-hand drive (the European Yaris and Prius, #760) on +X
        float x = (leftHandDrive ? 1f : -1f) * Mathf.Min(0.37f, cw * 0.25f + 0.01f);
        // Eye 20 cm under the roof, which leaves the head clear of the headlining. A low car
        // reclines the seat further rather than putting the hip through the floor.
        float eyeY = d.Roof - 0.2f;
        float hipMin = FloorY + 0.1f, hipMax = FloorY + 0.36f;
        float recline = 0.35f;
        float Rise(float r) => HumanMeshBuilder.DriverEye(Vector3.Zero, r).Y;
        while (eyeY - Rise(recline) < hipMin && recline < 0.8f) recline += 0.02f;
        float hipY = Mathf.Clamp(eyeY - Rise(recline), hipMin, hipMax);

        // Feet forward to the pedals at the bulkhead, and the eye at least a third of a metre
        // behind the top of the windscreen: a cab-forward car moves the seat back, not the eye
        // up into the glass.
        float ballY = FloorY + 0.09f;
        const float leg = 0.76f;   // hip to the ball of the foot: knees a little bent
        float reach = Mathf.Sqrt(Mathf.Max(0.09f, leg * leg - (hipY - ballY) * (hipY - ballY)));
        float eyeAhead = HumanMeshBuilder.DriverEye(Vector3.Zero, recline).Z;
        float pedalRoom = Firewall(d) - DriverSeat.PedalHinge.Z - 0.02f;
        float hipZ = Mathf.Min(pedalRoom - reach, d.WsTop - 0.33f - eyeAhead);
        float pedalZ = hipZ + reach;
        var hip = new Vector3(x, hipY, hipZ);

        // the wheel in front of the chest, where the arms reach it three-quarters straight, and
        // high enough that the thighs pass under the rim
        var back = new Vector3(0, Mathf.Cos(recline), -Mathf.Sin(recline));
        var shoulders = hip + back * HumanMeshBuilder.DriverTorso;
        var wheel = shoulders + new Vector3(0, -0.16f, 0.37f);
        wheel.Y = Mathf.Max(wheel.Y, hipY + 0.38f);
        var column = new Vector3(0, Mathf.Sin(0.4f), -Mathf.Cos(0.4f));

        return new DriverSeat(hip, recline, wheel, column, 0.185f,
            Throttle: new Vector3(x - 0.1f, ballY, pedalZ),
            Brake: new Vector3(x + 0.03f, ballY, pedalZ),
            Rest: new Vector3(x + 0.16f, ballY, pedalZ - 0.02f));
    }

    /// <summary>
    /// The cabin, built into <paramref name="s"/> (the shell) where it is part of the body — the
    /// outside mirrors — and into meshes of its own otherwise.
    /// </summary>
    private static CarCabin BuildCabin(MeshScratch s, CarBody body, Dims d, float cw, CarGauges gauges)
    {
        var seat = SeatFor(d, body.LeftHandDrive);
        var c = new MeshScratch();
        float hw = d.Width * 0.5f, inner = hw - Skin - Lining;
        float firewall = Firewall(d), back = d.RgBase;
        var eye = HumanMeshBuilder.DriverEye(seat.Hip, seat.Recline);

        // ---- floor, bulkheads, tunnel ----
        // the footwell narrows between the front wheel wells, past the sills (CarMeshBuilder)
        float sillF = d.Wheelbase * 0.5f - d.WheelR - 0.13f;
        float sills = Mathf.Min(firewall, sillF), well = firewall > sillF ? Tub(d) : inner;
        float len = firewall - back, mid = (firewall + back) * 0.5f;
        c.Box(new Vector3(0, FloorY + 0.005f, (sills + back) * 0.5f), new Vector3(inner * 2f, 0.01f, sills - back), Carpet);
        if (firewall > sills)
            c.Box(new Vector3(0, FloorY + 0.005f, (sills + firewall) * 0.5f), new Vector3(well * 2f, 0.01f, firewall - sills), Carpet);
        c.Box(new Vector3(0, (FloorY + SillY1) * 0.5f, firewall - 0.015f), new Vector3(well * 2f, SillY1 - FloorY, 0.03f), Dash);
        c.Box(new Vector3(0, (SillY1 + d.Belt) * 0.5f, firewall - 0.015f), new Vector3(inner * 2f, d.Belt - SillY1 - 0.01f, 0.03f), Dash);
        c.Box(new Vector3(0, (FloorY + d.Belt) * 0.5f, back + 0.015f), new Vector3(inner * 2f, d.Belt - FloorY - 0.01f, 0.03f), Cabin);
        c.Box(new Vector3(0, FloorY + 0.08f, mid), new Vector3(0.24f, 0.16f, len), Cabin);

        // ---- seats: the driver's and the passenger's the same, a bench behind if there is room ----
        // (each one a seat a player can take, #158: the driver's first)
        var seats = new List<SeatAnchor>();
        foreach (float side in new[] { 1f, -1f })
        {
            Bucket(c, seat.Hip with { X = seat.Hip.X * side }, seat.Recline);
            seats.Add(new SeatAnchor(0, Turned(seat.Hip with { X = seat.Hip.X * side }), seat.Recline, FloorY));
        }
        if (body.Shape is not (BodyShape.Roadster or BodyShape.Midship))
        {
            float front = seat.Hip.Z - 0.3f, rear = back + 0.1f;
            float benchHip = Mathf.Min(FloorY + 0.37f, seat.Hip.Y);
            if (body.Shape == BodyShape.Liftback)
            {
                // the rear glass comes down over the bench (#760): sat lower, and forward to where
                // the heads, leaning back with the seat, are under it
                benchHip = Mathf.Min(FloorY + 0.3f, seat.Hip.Y);
                float lean = seat.Recline + 0.1f, crown = benchHip + 0.88f * Mathf.Cos(lean) + 0.01f;
                float under = d.RgBase + (crown - d.RgFoot) / (d.Roof - 0.05f - d.RgFoot) * (d.RgTop - d.RgBase);
                rear = Mathf.Max(rear, Mathf.Min(under, d.RgTop) + 0.88f * Mathf.Sin(lean) - 0.28f);
            }
            if (front - rear > 0.3f)
            {
                // two places on the bench, sat back against it no higher than the front seats (the
                // roof is no higher there) and a little more reclined
                foreach (float side in new[] { 1f, -1f })
                    seats.Add(new SeatAnchor(0, Turned(new Vector3(side * Mathf.Min(0.36f, inner - 0.26f),
                        benchHip, rear + 0.28f)), seat.Recline + 0.1f, FloorY));
                float cushionY = FloorY + 0.22f;
                c.Box(new Vector3(0, cushionY, (front + rear) * 0.5f + 0.04f), new Vector3(inner * 2f - 0.06f, 0.12f, front - rear - 0.08f), Seat);
                float h = d.Belt + 0.12f - cushionY;
                c.Box(new Vector3(0, cushionY + h * 0.5f, rear + 0.02f), new Vector3(inner * 2f - 0.06f, h, 0.1f), Seat,
                    new Basis(Vector3.Right, -0.25f));
            }
        }

        // ---- column, wheel ----
        var steering = CockpitKit.Wheel(c, seat, Kit);

        // ---- the binnacle, seen through the top of the wheel, 13 cm ahead of the hub ----
        var panel = new CockpitKit.Panel(seat, eye, Kit);
        var inst = panel.Inst;
        var (face, nd, ud, ld, dialZ) = (panel.Face, panel.N, panel.Up, panel.Left, panel.DialZ);
        const float dialR = 0.052f, spread = 0.068f;
        var tachAt = face + new Vector3(spread, 0, 0);   // on the driver's left, the speedometer on the right
        var speedoAt = face - new Vector3(spread, 0, 0);

        float dashTop = d.Belt + 0.02f;
        float dashBottom = Mathf.Min(Mathf.Max(d.Belt - 0.2f, seat.Hip.Y + 0.3f), dashTop - 0.08f);
        float dashRear = dialZ + 0.035f;
        float dashFront = Mathf.Max(d.WsBase + 0.02f, dashRear + 0.1f);
        c.Box(new Vector3(0, (dashTop + dashBottom) * 0.5f, (dashRear + dashFront) * 0.5f),
            new Vector3(inner * 2f, dashTop - dashBottom, dashFront - dashRear), Dash);
        // the binnacle box behind the dials and its hood over them
        if (!body.CentreDisplay)
        {
            c.Box(face + new Vector3(0, 0, 0.07f), new Vector3(spread * 2f + 0.16f, 0.16f, 0.14f), Dash);
            c.Box(face + new Vector3(0, 0.088f, 0.02f), new Vector3(spread * 2f + 0.18f, 0.02f, 0.2f), Dash);
        }
        // centre stack, gear lever
        c.Box(new Vector3(0, (FloorY + dashBottom) * 0.5f + 0.05f, dashRear - 0.08f), new Vector3(0.22f, dashBottom - FloorY - 0.1f, 0.18f), Dash);
        c.Box(new Vector3(0, dashBottom - 0.02f, dashRear - 0.172f), new Vector3(0.16f, 0.06f, 0.01f), Trim);
        if (body.Automatic)
        {
            // a selector, not a lever (#760): the Prius's little joystick standing out of the dash
            // on the driver's side of the stack, within a hand's reach of the wheel
            var stub = new Vector3(Mathf.Sign(seat.Hip.X) * 0.13f, dashBottom + 0.04f, dashRear - 0.01f);
            c.Box(stub, new Vector3(0.07f, 0.05f, 0.03f), Trim);
            c.Tube(stub, stub + new Vector3(0, 0.02f, -0.06f), 0.007f, Steel, 4);
            c.Box(stub + new Vector3(0, 0.025f, -0.07f), new Vector3(0.03f, 0.03f, 0.03f), Steel);
        }
        else
        {
            var gate = new Vector3(0, FloorY + 0.16f, seat.Hip.Z + 0.3f);
            c.Tube(gate, gate + new Vector3(0, 0.17f, -0.04f), 0.012f, Steel, 4);
            c.Box(gate + new Vector3(0, 0.19f, -0.045f), new Vector3(0.045f, 0.045f, 0.045f), Trim);
        }

        // an automatic's forward gears all read D (#760)
        string gearChars = body.Automatic ? "rndddddd" : "rn123456";
        var lampColours = new[] { CockpitKit.RedBand, new Color(0.25f, 1f, 0.35f), Amber };
        ArrayMesh[] digits, lamps;
        ArrayMesh[][]? speedDigits = null;
        CarNeedle tach, speedo;
        if (body.CentreDisplay)
        {
            (digits, lamps, speedDigits) = CentreDisplay(c, inst, eye, gearChars, lampColours, dashTop, dashBottom, dashRear, d.WsBase);
            if (body.Hud) speedDigits = speedDigits.Concat(Hud(inst, eye, d)).ToArray();
            // no dials: needles that draw nothing, on pivots the rig can still turn
            tach = new CarNeedle(new ArrayMesh(), Turned(face), Turned(nd));
            speedo = tach;
        }
        else
        {
            // ---- dials: ticks of a fixed length (a heavy's scale with the dial) ----
            var dialBasis = panel.Basis;
            panel.DialFace(tachAt, dialR, gauges.TachRpm, 500f, 1000f, 0.013f, 0.007f, gauges.RedlineRpm);
            panel.DialFace(speedoAt, dialR, gauges.SpeedoKmh, 10f, 20f, 0.013f, 0.007f, float.MaxValue);

            // ---- the gear digit between the dials, the warning lamps under them ----
            var digitAt = face + nd * 0.006f - ud * 0.012f;
            digits = gearChars.Select(ch => SevenSegment(digitAt, ud, -ld, nd, ch)).ToArray();
            inst.Box(digitAt - nd * 0.002f, new Vector3(0.026f, 0.04f, 0.002f), CockpitKit.Dial, dialBasis);
            lamps = panel.Lamps(lampColours, 0.07f, 1f, 0.03f, new Vector2(0.018f, 0.01f));
            tach = panel.Needle(tachAt, dialR);
            speedo = panel.Needle(speedoAt, dialR);
        }

        // ---- pedals, each on its own hinge ----
        // an automatic has no clutch: the left foot rests on the floor where it would be
        var pedals = CockpitKit.Pedals(body.Automatic ? new[] { seat.Throttle, seat.Brake } : new[] { seat.Throttle, seat.Brake, seat.Rest }, Kit);

        // ---- mirrors: inside at the top of the windscreen, outside on the doors' front corners ----
        var mirrors = new List<CarMirror>();
        void Mirror(string name, MeshScratch into, Vector3 at, Vector2 size, Color housing, float depth) =>
            mirrors.Add(CockpitKit.Mirror(into, eye, Kit, name, at, size, housing, depth));
        var rearView = new Vector3(0, d.Roof - 0.05f - 0.09f, d.WsTop + 0.01f);
        // the stem comes down from the headlining to the back of the housing, not through the glass
        c.Tube(new Vector3(0, d.Roof - 0.06f, d.WsTop + 0.05f), rearView - CockpitKit.Facing(eye, rearView) * 0.03f, 0.008f, Trim, 4);
        Mirror("MirrorRear", c, rearView, new Vector2(0.18f, 0.05f), Trim, 0.03f);
        foreach (float sx in new[] { -1f, 1f })
        {
            var housing = new Vector3(sx * (hw + 0.08f), d.Belt + 0.1f, d.WsBase - 0.1f);
            s.Tube(new Vector3(sx * (hw - 0.02f), d.Belt + 0.04f, d.WsBase - 0.08f), housing, 0.018f, Trim, 4);
            Mirror(sx < 0 ? "MirrorRight" : "MirrorLeft", s, housing - new Vector3(0, 0, 0.03f), new Vector2(0.13f, 0.08f),
                body.Lower != null ? Trim : body.Paint, 0.07f);
        }

        return new CarCabin(c.Build(), inst.Build(),
            steering, Turned(seat.WheelAxis),
            tach, speedo, gauges, digits, lamps, pedals,
            mirrors.ToArray(), seat, Turned(eye), seats.ToArray()) { SpeedDigits = speedDigits };
    }

    private static readonly Color Screen = new(0.01f, 0.012f, 0.02f);
    private static readonly Color Readout = new(0.55f, 0.9f, 1f);
    private static readonly Color Ready = new(0.3f, 1f, 0.45f);
    private static readonly Color Touch = new(0.04f, 0.08f, 0.18f);
    private static readonly Color HudGreen = new(0.45f, 1f, 0.75f);

    /// <summary>
    /// The XW30 Prius's head-up display (#760): the speed reflected into the windscreen where the
    /// driver's line of sight, a few degrees under level, meets the glass, a centimetre in from it and
    /// turned to the eye, with an eco bar under it. Its figures come back as one more readout of
    /// <see cref="CarCabin.SpeedPlaces"/> places.
    /// </summary>
    private static IEnumerable<ArrayMesh[]> Hud(MeshScratch inst, Vector3 eye, Dims d)
    {
        // the windscreen as a line in (z, y): from its foot on the belt to its top under the roof
        var foot = new Vector2(d.WsBase, d.Belt);
        var top = new Vector2(d.WsTop, d.Roof - 0.05f);
        var look = new Vector2(Mathf.Cos(0.11f), -Mathf.Sin(0.11f));   // 6° under level
        var from = new Vector2(eye.Z, eye.Y);
        // from + look·t on the glass: solve against foot + (top − foot)·s
        var edge = top - foot;
        float t = ((foot.X - from.X) * edge.Y - (foot.Y - from.Y) * edge.X) / (look.X * edge.Y - look.Y * edge.X);
        var hit = from + look * t;
        var at = new Vector3(eye.X, hit.Y, hit.X);
        var n = (eye - at).Normalized();
        at += n * 0.01f;
        var up = (Vector3.Up - n * n.Dot(Vector3.Up)).Normalized();
        var right = up.Cross(n);
        for (int place = 0; place < CarCabin.SpeedPlaces; place++)
        {
            var digitAt = at + right * (0.012f - place * 0.025f);
            yield return Enumerable.Range(0, 10).Select(k => SevenSegment(digitAt, up, right, n, (char)('0' + k), HudGreen)).ToArray();
        }
        for (int i = 0; i < 5; i++)
            inst.Box(at + right * (-0.04f + i * 0.02f) - up * 0.03f, new Vector3(0.014f, 0.004f, 0.002f), HudGreen, new Basis(right, up, n));
    }

    /// <summary>
    /// The XW20 Prius's instruments (#760), with no dials behind the wheel: a long black display in a
    /// hood at the top middle of the dash, set well forward toward the screen and turned to the
    /// driver's eye (the speed in figures, the gear, READY, a fuel bar and the warning lamps), and the
    /// touchscreen under it in the face of the dash, its energy monitor showing. Static parts go into
    /// the cabin and the unshaded instruments; what changes comes back as meshes of its own.
    /// </summary>
    private static (ArrayMesh[] Gear, ArrayMesh[] Lamps, ArrayMesh[][] Speed) CentreDisplay(MeshScratch c, MeshScratch inst, Vector3 eye,
        string gearChars, Color[] lampColours, float dashTop, float dashBottom, float dashRear, float wsBase)
    {
        var at = new Vector3(0, dashTop + 0.05f, Mathf.Min(dashRear + 0.4f, wsBase - 0.3f));
        var n = (eye - at).Normalized();
        var up = (Vector3.Up - n * n.Dot(Vector3.Up)).Normalized();
        var right = up.Cross(n);   // the reader's right
        var basis = new Basis(right, up, n);
        Vector3 P(float u, float v, float lift) => at + right * u + up * v + n * lift;

        // the hood, sunk into the dash, its visor over the face, and the face black glass
        c.Box(P(0, -0.01f, -0.045f), new Vector3(0.4f, 0.12f, 0.09f), Dash, basis);
        c.Box(P(0, 0.055f, -0.005f), new Vector3(0.42f, 0.015f, 0.1f), Dash, basis);
        inst.Box(P(0, 0, 0.001f), new Vector3(0.36f, 0.085f, 0.002f), Screen, basis);

        // the speed, three figures to the left of the middle, units first; the gear right of them
        var speed = new ArrayMesh[3][];
        for (int place = 0; place < 3; place++)
        {
            var digitAt = P(-0.06f - place * 0.025f, 0.006f, 0.004f);
            speed[place] = Enumerable.Range(0, 10).Select(k => SevenSegment(digitAt, up, right, n, (char)('0' + k), Readout)).ToArray();
        }
        var gearAt = P(0.03f, 0.006f, 0.004f);
        var gear = gearChars.Select(ch => SevenSegment(gearAt, up, right, n, ch, Readout)).ToArray();
        // READY over a fuel bar on the right
        inst.Box(P(0.11f, 0.02f, 0.003f), new Vector3(0.05f, 0.012f, 0.002f), Ready, basis);
        for (int i = 0; i < 6; i++)
            inst.Box(P(0.085f + i * 0.01f, -0.012f, 0.003f), new Vector3(0.006f, 0.012f, 0.002f), Readout, basis);

        // the warning lamps along the bottom left: dark in the face, lit as meshes of their own
        var lamps = new ArrayMesh[lampColours.Length];
        var lampSize = new Vector3(0.016f, 0.009f, 0.002f);
        for (int i = 0; i < lamps.Length; i++)
        {
            var lampAt = P(-0.13f + i * 0.024f, -0.03f, 0.003f);
            inst.Box(lampAt, lampSize, lampColours[i].Darkened(0.85f), basis);
            var lit = new MeshScratch();
            lit.Box(lampAt + n * 0.0015f, lampSize, lampColours[i], basis);
            lamps[i] = lit.Build();
        }

        // the touchscreen in the dash's face: the energy monitor, the car's outline with the engine,
        // the motor between them and the battery's bars
        float sy = Mathf.Min((dashTop + dashBottom) * 0.5f + 0.01f, dashTop - 0.07f), sz = dashRear - 0.003f;
        inst.Box(new Vector3(0, sy, sz), new Vector3(0.17f, 0.1f, 0.002f), Touch);
        foreach (var (x, y, w, h) in new[] { (0f, 0.022f, 0.08f, 0.004f), (0f, -0.012f, 0.08f, 0.004f), (0.04f, 0.005f, 0.004f, 0.038f), (-0.04f, 0.005f, 0.004f, 0.038f) })
            inst.Box(new Vector3(x, sy + y, sz - 0.002f), new Vector3(w, h, 0.002f), CockpitKit.Tick);
        inst.Box(new Vector3(0.022f, sy + 0.005f, sz - 0.002f), new Vector3(0.022f, 0.016f, 0.002f), Amber);
        inst.Box(new Vector3(0f, sy + 0.005f, sz - 0.002f), new Vector3(0.01f, 0.004f, 0.002f), Readout);
        for (int i = 0; i < 5; i++)
            inst.Box(new Vector3(-0.012f - i * 0.006f, sy - 0.03f, sz - 0.002f), new Vector3(0.004f, 0.014f, 0.002f), Ready);
        return (gear, lamps, speed);
    }

    /// <summary>A bucket seat for a hip at <paramref name="hip"/>: cushion, a back reclined with the driver, headrest.</summary>
    private static void Bucket(MeshScratch s, Vector3 hip, float recline) => Bucket(s, hip, recline, FloorY, Seat);

    /// <summary>As above, on a floor at <paramref name="floor"/> (a truck's cab, a bus's platform), in <paramref name="colour"/>.</summary>
    internal static void Bucket(MeshScratch s, Vector3 hip, float recline, float floor, Color colour)
    {
        var back = new Vector3(0, Mathf.Cos(recline), -Mathf.Sin(recline));
        var ahead = new Vector3(0, Mathf.Sin(recline), Mathf.Cos(recline));
        var tilt = new Basis(Vector3.Right, -recline);
        float cushion = hip.Y - 0.09f;
        s.Box(new Vector3(hip.X, (cushion + floor) * 0.5f, hip.Z + 0.12f), new Vector3(0.46f, cushion - floor, 0.46f), colour,
            new Basis(Vector3.Right, -0.08f));
        s.Box(hip + back * 0.3f - ahead * 0.17f, new Vector3(0.48f, 0.62f, 0.1f), colour, tilt);
        s.Box(hip + back * 0.7f - ahead * 0.17f, new Vector3(0.26f, 0.17f, 0.1f), colour, tilt);
        // side bolsters
        foreach (float sx in new[] { -1f, 1f })
            s.Box(hip + new Vector3(sx * 0.22f, 0, 0) + back * 0.28f - ahead * 0.1f, new Vector3(0.05f, 0.5f, 0.1f), colour, tilt);
    }

    /// <summary>
    /// A seven-segment character (<c>r n d 0..9 A H L</c>) in the plane of <paramref name="normal"/>,
    /// 3 cm tall: the gear display, in its amber unless <paramref name="colour"/> says otherwise.
    /// <paramref name="right"/> is the reader's right.
    /// </summary>
    internal static ArrayMesh SevenSegment(Vector3 centre, Vector3 up, Vector3 right, Vector3 normal, char ch, Color? colour = null)
    {
        var lit = colour ?? Digit;
        const float h = 0.015f, w = 0.009f, t = 0.0035f;
        string on = ch switch
        {
            'r' => "eg", 'n' => "ceg", 'd' => "bcdeg", '1' => "bc", '2' => "abged", '3' => "abgcd", '4' => "fgbc",
            '5' => "afgcd", '6' => "afgedc", '7' => "abc", '9' => "abcdfg", '0' => "abcdef",
            'A' => "abcefg", 'H' => "bcefg", 'L' => "def", _ => "abcdefg",
        };
        var basis = new Basis(right, up, normal);
        var m = new MeshScratch();
        void Seg(char id, Vector3 at, bool across)
        {
            if (on.Contains(id))
                m.Box(centre + at, across ? new Vector3(w * 2f, t, 0.002f) : new Vector3(t, h, 0.002f), lit, basis);
        }
        Seg('a', up * h, true);
        Seg('g', Vector3.Zero, true);
        Seg('d', -up * h, true);
        Seg('f', -right * w + up * h * 0.5f, false);
        Seg('b', right * w + up * h * 0.5f, false);
        Seg('e', -right * w - up * h * 0.5f, false);
        Seg('c', right * w - up * h * 0.5f, false);
        return m.Build();
    }
}
