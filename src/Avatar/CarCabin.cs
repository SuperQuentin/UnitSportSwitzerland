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
    CarMirror[] Mirrors, DriverSeat Seat, Vector3 Eye)
{
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
    private static readonly Color Dial = new(0.02f, 0.02f, 0.025f);
    private static readonly Color Tick = new(0.92f, 0.92f, 0.88f);
    private static readonly Color RedBand = new(0.95f, 0.12f, 0.08f);
    private static readonly Color Needle = new(1f, 0.38f, 0.1f);
    private static readonly Color Bezel = new(0.45f, 0.46f, 0.5f);
    private static readonly Color MirrorFace = new(0.16f, 0.18f, 0.22f);
    private static readonly Color Digit = new(1f, 0.7f, 0.2f);

    /// <summary>The seat and everything the driver reaches, for this body. Cheap: no meshes.</summary>
    public static DriverSeat SeatFor(CarBody body, float wheelbase) => SeatFor(For(body, wheelbase));

    internal static DriverSeat SeatFor(Dims d)
    {
        float cw = d.Width - 0.2f;
        // right-hand drive (the Initial D cars are Japanese): the driver's side is −X authored
        float x = -Mathf.Min(0.37f, cw * 0.25f + 0.01f);
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
        var seat = SeatFor(d);
        var c = new MeshScratch();
        var inst = new MeshScratch();
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
        foreach (float side in new[] { 1f, -1f })
            Bucket(c, seat.Hip with { X = seat.Hip.X * side }, seat.Recline);
        if (body.Shape is not (BodyShape.Roadster or BodyShape.Midship))
        {
            float front = seat.Hip.Z - 0.3f, rear = back + 0.1f;
            if (front - rear > 0.3f)
            {
                float cushionY = FloorY + 0.22f;
                c.Box(new Vector3(0, cushionY, (front + rear) * 0.5f + 0.04f), new Vector3(inner * 2f - 0.06f, 0.12f, front - rear - 0.08f), Seat);
                float h = d.Belt + 0.12f - cushionY;
                c.Box(new Vector3(0, cushionY + h * 0.5f, rear + 0.02f), new Vector3(inner * 2f - 0.06f, h, 0.1f), Seat,
                    new Basis(Vector3.Right, -0.25f));
            }
        }

        // ---- column, wheel ----
        var n = seat.WheelAxis;
        var up = (Vector3.Up - n * n.Dot(Vector3.Up)).Normalized();
        var left = n.Cross(up);
        var wc = seat.WheelCentre;
        c.Tube(wc - n * 0.04f, wc - n * 0.3f, 0.035f, 0.045f, Dash, 6);
        var wheel = new MeshScratch();
        float r = seat.WheelRadius;
        wheel.Ring(wc, n, r - 0.022f, r + 0.006f, 0.03f, Trim, 14);
        var hubBasis = new Basis(-left, up, n);
        wheel.Box(wc - n * 0.02f, new Vector3(0.1f, 0.09f, 0.05f), Trim, hubBasis);
        // three spokes, 3, 9 and 6 o'clock, and a mark at the top so the turn is readable
        foreach (var dir in new[] { left, -left, -up })
            wheel.Tube(wc - n * 0.015f, wc + dir * (r - 0.015f), 0.014f, Steel, 4);
        wheel.Box(wc + up * r + n * 0.004f, new Vector3(0.03f, 0.02f, 0.034f), Amber, hubBasis);

        // ---- the binnacle, seen through the top of the wheel ----
        // Its face is where the line from the eye through the top of the wheel's opening meets
        // a plane 13 cm ahead of the hub, square to the eye: the dials just under the rim.
        var through = wc + up * r * 0.62f;
        var ray = through - eye;
        float dialZ = wc.Z + 0.13f;
        var face = eye + ray * ((dialZ - eye.Z) / ray.Z);
        var nd = (eye - face).Normalized();
        var ud = (Vector3.Up - nd * nd.Dot(Vector3.Up)).Normalized();
        var ld = nd.Cross(ud);
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
        c.Box(face + new Vector3(0, 0, 0.07f), new Vector3(spread * 2f + 0.16f, 0.16f, 0.14f), Dash);
        c.Box(face + new Vector3(0, 0.088f, 0.02f), new Vector3(spread * 2f + 0.18f, 0.02f, 0.2f), Dash);
        // centre stack, gear lever
        c.Box(new Vector3(0, (FloorY + dashBottom) * 0.5f + 0.05f, dashRear - 0.08f), new Vector3(0.22f, dashBottom - FloorY - 0.1f, 0.18f), Dash);
        c.Box(new Vector3(0, dashBottom - 0.02f, dashRear - 0.172f), new Vector3(0.16f, 0.06f, 0.01f), Trim);
        var gate = new Vector3(0, FloorY + 0.16f, seat.Hip.Z + 0.3f);
        c.Tube(gate, gate + new Vector3(0, 0.17f, -0.04f), 0.012f, Steel, 4);
        c.Box(gate + new Vector3(0, 0.19f, -0.045f), new Vector3(0.045f, 0.045f, 0.045f), Trim);

        // ---- dials ----
        var dialBasis = new Basis(-ld, ud, nd);
        Vector3 Dir(float angle) => ud * Mathf.Cos(angle) + ld * Mathf.Sin(angle);
        void DialFace(Vector3 at, float full, float step, float major, float red)
        {
            inst.Ring(at + nd * 0.002f, nd, 0.003f, dialR, 0.004f, Dial, 16);
            inst.Ring(at + nd * 0.004f, nd, dialR, dialR + 0.006f, 0.008f, Bezel, 16);
            for (float v = 0; v <= full + 0.01f; v += step)
            {
                bool big = Mathf.Abs(v / major - Mathf.Round(v / major)) < 0.01f;
                var dir = Dir(CarNeedle.Angle(v / full));
                float length = big ? 0.013f : 0.007f;
                inst.Box(at + nd * 0.005f + dir * (dialR * 0.86f - length * 0.5f), new Vector3(0.0035f, length, 0.002f),
                    v >= red ? RedBand : Tick, new Basis(dir.Cross(nd), dir, nd));
            }
        }
        DialFace(tachAt, gauges.TachRpm, 500f, 1000f, gauges.RedlineRpm);
        DialFace(speedoAt, gauges.SpeedoKmh, 10f, 20f, float.MaxValue);
        CarNeedle BuildNeedle(Vector3 at)
        {
            var m = new MeshScratch();
            float length = dialR * 0.84f;
            m.Box(at + nd * 0.008f + ud * (length * 0.5f - 0.008f), new Vector3(0.004f, length, 0.002f), Needle, dialBasis);
            m.Box(at + nd * 0.009f, new Vector3(0.012f, 0.012f, 0.004f), Trim, dialBasis);
            return new CarNeedle(m.Build(at), Turned(at), Turned(nd));
        }

        // ---- the gear digit between the dials, the warning lamps under them ----
        var digitAt = face + nd * 0.006f - ud * 0.012f;
        var digits = "rn123456".Select(ch => SevenSegment(digitAt, ud, -ld, nd, ch)).ToArray();
        inst.Box(digitAt - nd * 0.002f, new Vector3(0.026f, 0.04f, 0.002f), Dial, dialBasis);
        var lampColours = new[] { RedBand, new Color(0.25f, 1f, 0.35f), Amber };
        var lamps = new ArrayMesh[lampColours.Length];
        for (int i = 0; i < lamps.Length; i++)
        {
            var at = face - ud * 0.07f + ld * ((i - 1) * 0.03f) + nd * 0.004f;
            inst.Box(at, new Vector3(0.018f, 0.01f, 0.002f), lampColours[i].Darkened(0.85f), dialBasis);
            var lit = new MeshScratch();
            lit.Box(at + nd * 0.0015f, new Vector3(0.018f, 0.01f, 0.002f), lampColours[i], dialBasis);
            lamps[i] = lit.Build();
        }

        // ---- pedals, each on its own hinge ----
        var pedals = new[] { seat.Throttle, seat.Brake, seat.Rest }.Select((pad, i) =>
        {
            var hinge = pad + DriverSeat.PedalHinge;
            var m = new MeshScratch();
            m.Tube(hinge, pad + new Vector3(0, 0.03f, 0), 0.01f, Steel, 4);
            // the pad square to its arm, sloping back toward the sole
            m.Box(pad + new Vector3(0, 0, 0.012f), new Vector3(i == 0 ? 0.05f : 0.075f, i == 0 ? 0.11f : 0.07f, 0.015f), Trim,
                new Basis(Vector3.Right, -0.35f));
            return new HingedPart(m.Build(hinge), Turned(hinge));
        }).ToArray();

        // ---- mirrors: inside at the top of the windscreen, outside on the doors' front corners ----
        // halfway between the eye and straight back: what the driver sees in it is behind the car
        Vector3 Facing(Vector3 at) => ((eye - at).Normalized() + new Vector3(0, 0, -1f)).Normalized();
        var mirrors = new List<CarMirror>();
        void Mirror(string name, MeshScratch into, Vector3 at, Vector2 size, Color housing, float depth)
        {
            var normal = Facing(at);
            var side = Vector3.Up.Cross(normal).Normalized();
            var basis = new Basis(side, normal.Cross(side), normal);
            into.Box(at - normal * depth * 0.5f, new Vector3(size.X + 0.02f, size.Y + 0.02f, depth), housing, basis);
            into.Box(at + normal * 0.001f, new Vector3(size.X, size.Y, 0.002f), MirrorFace, basis);
            mirrors.Add(new CarMirror(name, Turned(at + normal * 0.004f), Turned(normal), size));
        }
        var rearView = new Vector3(0, d.Roof - 0.05f - 0.09f, d.WsTop + 0.01f);
        // the stem comes down from the headlining to the back of the housing, not through the glass
        c.Tube(new Vector3(0, d.Roof - 0.06f, d.WsTop + 0.05f), rearView - Facing(rearView) * 0.03f, 0.008f, Trim, 4);
        Mirror("MirrorRear", c, rearView, new Vector2(0.18f, 0.05f), Trim, 0.03f);
        foreach (float sx in new[] { -1f, 1f })
        {
            var housing = new Vector3(sx * (hw + 0.08f), d.Belt + 0.1f, d.WsBase - 0.1f);
            s.Tube(new Vector3(sx * (hw - 0.02f), d.Belt + 0.04f, d.WsBase - 0.08f), housing, 0.018f, Trim, 4);
            Mirror(sx < 0 ? "MirrorRight" : "MirrorLeft", s, housing - new Vector3(0, 0, 0.03f), new Vector2(0.13f, 0.08f),
                body.Lower != null ? Trim : body.Paint, 0.07f);
        }

        return new CarCabin(c.Build(), inst.Build(),
            new HingedPart(wheel.Build(wc), Turned(wc)), Turned(n),
            BuildNeedle(tachAt), BuildNeedle(speedoAt), gauges, digits, lamps, pedals,
            mirrors.ToArray(), seat, Turned(eye));
    }

    /// <summary>A bucket seat for a hip at <paramref name="hip"/>: cushion, a back reclined with the driver, headrest.</summary>
    private static void Bucket(MeshScratch s, Vector3 hip, float recline)
    {
        var back = new Vector3(0, Mathf.Cos(recline), -Mathf.Sin(recline));
        var ahead = new Vector3(0, Mathf.Sin(recline), Mathf.Cos(recline));
        var tilt = new Basis(Vector3.Right, -recline);
        float cushion = hip.Y - 0.09f;
        s.Box(new Vector3(hip.X, (cushion + FloorY) * 0.5f, hip.Z + 0.12f), new Vector3(0.46f, cushion - FloorY, 0.46f), Seat,
            new Basis(Vector3.Right, -0.08f));
        s.Box(hip + back * 0.3f - ahead * 0.17f, new Vector3(0.48f, 0.62f, 0.1f), Seat, tilt);
        s.Box(hip + back * 0.7f - ahead * 0.17f, new Vector3(0.26f, 0.17f, 0.1f), Seat, tilt);
        // side bolsters
        foreach (float sx in new[] { -1f, 1f })
            s.Box(hip + new Vector3(sx * 0.22f, 0, 0) + back * 0.28f - ahead * 0.1f, new Vector3(0.05f, 0.5f, 0.1f), Seat, tilt);
    }

    /// <summary>
    /// A seven-segment character (<c>r n 1..8</c>) in the plane of <paramref name="normal"/>,
    /// 3 cm tall: the gear display. <paramref name="right"/> is the reader's right.
    /// </summary>
    private static ArrayMesh SevenSegment(Vector3 centre, Vector3 up, Vector3 right, Vector3 normal, char ch)
    {
        const float h = 0.015f, w = 0.009f, t = 0.0035f;
        string on = ch switch
        {
            'r' => "eg", 'n' => "ceg", '1' => "bc", '2' => "abged", '3' => "abgcd", '4' => "fgbc",
            '5' => "afgcd", '6' => "afgedc", '7' => "abc", _ => "abcdefg",
        };
        var basis = new Basis(right, up, normal);
        var m = new MeshScratch();
        void Seg(char id, Vector3 at, bool across)
        {
            if (on.Contains(id))
                m.Box(centre + at, across ? new Vector3(w * 2f, t, 0.002f) : new Vector3(t, h, 0.002f), Digit, basis);
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
