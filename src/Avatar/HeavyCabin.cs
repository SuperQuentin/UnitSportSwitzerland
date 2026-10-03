using Godot;
using static UnitSport.Avatar.HeavyMesh;

namespace UnitSport.Avatar;

/// <summary>How someone sits in a seat: on a chair (a car, a cab, a bus), or astride (a motorbike's pillion).</summary>
public enum SeatPose { Seated, Straddle }

/// <summary>
/// A seat someone can sit in (#157, the passengers of #158): the hip's midpoint over the cushion
/// (astride: the seat surface under the pelvis), how far the back reclines and the floor the feet
/// rest on (y), in the node space of section <see cref="Section"/>'s rig (−Z forward; a car's cabin
/// frame, before its shell offset). A seat faces forward unless it has a <see cref="Yaw"/>. A vehicle's first seat is the driver's.
/// </summary>
public sealed record SeatAnchor(int Section, Vector3 Hip, float Recline, float Floor)
{
    public SeatPose Pose { get; init; }
    /// <summary>The seat's turn about the vertical, node space (0 forward; a freighter's troop seats face across, #420).</summary>
    public float Yaw { get; init; }
    /// <summary>Astride: what the hands hold and where the feet rest, node space.</summary>
    public Vector3 Grip { get; init; }
    public Vector3 Peg { get; init; }
}

/// <summary>
/// What a truck's cab or a bus's driver's place is to its cockpit, authored like the body (+Z
/// forward, +X the vehicle's left, the origin on the ground under the section's centre of mass).
/// The seat, the wheel, the pedals and the dash are all derived from it (<see cref="HeavyCabin.SeatFor"/>).
/// </summary>
public sealed record CabFrame
{
    /// <summary>The cab's face (the windscreen's plane), authored z.</summary>
    public float Front { get; init; }
    /// <summary>The floor under the driver's feet: a truck's cab floor, a bus's raised platform.</summary>
    public float Floor { get; init; }
    /// <summary>The headlining.</summary>
    public float Ceiling { get; init; }
    /// <summary>The windscreen's bottom and top edges, m up.</summary>
    public float WsBase { get; init; }
    public float WsTop { get; init; }
    /// <summary>The dash's top edge: a truck's meets the windscreen, a bus's windscreen goes on down past it.</summary>
    public float DashTop { get; init; }
    /// <summary>Half the width inside the walls.</summary>
    public float InnerHalf { get; init; }
    /// <summary>The bulkhead the pedals hang in front of, metres behind the face.</summary>
    public float Nose { get; init; }
    /// <summary>The driver's hip across the cab: + is the left (left-hand drive).</summary>
    public float DriverX { get; init; }
    /// <summary>The hip above the floor: an air-suspended truck seat sits higher than a car's.</summary>
    public float HipRise { get; init; } = 0.5f;
    /// <summary>The back's lean from upright, rad: a truck or bus driver sits up.</summary>
    public float Recline { get; init; } = 0.22f;
    /// <summary>The column's angle above the horizontal, rad: a bus wheel lies nearly flat.</summary>
    public float ColumnTilt { get; init; } = 0.9f;
    public float WheelRadius { get; init; } = 0.235f;
    /// <summary>Where the dash ends toward the vehicle's right, authored x (a bus's stops at the driver's console).</summary>
    public float DashToX { get; init; }
    /// <summary>A clutch pedal (the automated manual boxes' manual modes take it).</summary>
    public bool Clutch { get; init; }
    /// <summary>A seat beside the driver's, mirrored across the cab (a truck's).</summary>
    public bool PassengerSeat { get; init; }
}

/// <summary>
/// The cockpit of a truck or a bus (#157), the heavy counterpart of <see cref="CarCabin"/>: its
/// instruments (unshaded, backlit) and the parts that move — the steering wheel on its column, the
/// tach, speedometer and air needles, a three-character gear display, the warning lamps and the
/// pedals — plus the mirrors' faces and the driver's seat. The fixed parts (dash, seats, column)
/// are in the section's own shell mesh. Node space (−Z forward, ground origin).
/// </summary>
public sealed record HeavyCockpit(
    ArrayMesh Instruments, HingedPart SteeringWheel, Vector3 ColumnAxis,
    CarNeedle Tach, CarNeedle Speedo, CarNeedle Air, CarGauges Gauges,
    Dictionary<char, ArrayMesh>[] GearChars, ArrayMesh[] Lamps, HingedPart[] Pedals,
    CarMirror[] Mirrors, DriverSeat Seat, Vector3 Eye, CabFrame Frame)
{
    /// <summary>Indices into <see cref="Lamps"/>.</summary>
    public const int LampSpringBrake = 0, LampLowAir = 1, LampLights = 2, LampRetarder = 3, LampDoors = 4, LampEngine = 5;
    /// <summary>Indices into <see cref="Pedals"/> (the clutch only where there is one).</summary>
    public const int PedalThrottle = 0, PedalBrake = 1, PedalClutch = 2;
    /// <summary>What the gear display can show, each character a mesh per position.</summary>
    public const string GearCharset = "0123456789AHLnr";

    /// <summary>A truck's steering, wheel turns per road-wheel radian: about 2.5 turns each way to full lock.</summary>
    public const float SteerRatio = 20f;
}

/// <summary>Builds the heavy cockpit (#157). Seats are derived, never placed: see <c>docs/notes/avatar/heavy-cabin.md</c>.</summary>
public static class HeavyCabin
{
    public static readonly Color Dash = new(0.16f, 0.16f, 0.17f);
    public static readonly Color DriverSeatColour = new(0.13f, 0.13f, 0.15f);
    public static readonly Color Lining = new(0.62f, 0.62f, 0.6f);
    public static readonly Color FloorColour = new(0.2f, 0.2f, 0.21f);

    /// <summary>A heavy's wheel, column, dials' plane, pedals and mirror housings (<see cref="CockpitKit"/>).</summary>
    private static readonly CockpitSpec Kit = new(Trim, Dash,
        ColumnFrom: 0.05f, ColumnTo: 0.42f, ColumnRadius: 0.04f, ColumnFoot: 0.055f,
        RimIn: 0.024f, RimOut: 0.008f, RimDepth: 0.034f, RimSides: 16,
        HubBack: 0.025f, Hub: new Vector3(0.13f, 0.11f, 0.06f), SpokeBack: 0.02f, SpokeShort: 0.016f, SpokeRadius: 0.016f,
        Mark: new Vector3(0.035f, 0.022f, 0.038f),
        DialsAhead: 0.16f,
        // a heavy's pedals are wide pads; the throttle a long one
        PedalArm: 0.012f, ThrottlePad: new Vector3(0.07f, 0.14f, 0.016f), Pad: new Vector3(0.1f, 0.08f, 0.016f),
        MirrorRim: 0.03f);

    /// <summary>Hip to the ball of the foot, m: a little longer than a car's, the knees less bent sitting up.</summary>
    private const float Leg = 0.8f;

    /// <summary>
    /// The driver's seat in a cab: the hip <see cref="CabFrame.HipRise"/> over the floor, as far
    /// back from the pedals (hung in front of the bulkhead) as the legs reach; the wheel in front of
    /// the chest, high enough that its near rim clears the thighs, on a column tilted
    /// <see cref="CabFrame.ColumnTilt"/>.
    /// </summary>
    public static DriverSeat SeatFor(CabFrame f)
    {
        float ballY = f.Floor + 0.09f;
        float hipY = f.Floor + f.HipRise;
        float reach = Mathf.Sqrt(Mathf.Max(0.09f, Leg * Leg - (hipY - ballY) * (hipY - ballY)));
        float pedalZ = f.Front - f.Nose - DriverSeat.PedalHinge.Z - 0.02f;
        var hip = new Vector3(f.DriverX, hipY, pedalZ - reach);

        var back = new Vector3(0, Mathf.Cos(f.Recline), -Mathf.Sin(f.Recline));
        var shoulders = hip + back * HumanMeshBuilder.DriverTorso;
        var column = new Vector3(0, Mathf.Sin(f.ColumnTilt), -Mathf.Cos(f.ColumnTilt));
        // the rim's near edge (down the wheel's own plane) clear of the lap
        float nearDrop = f.WheelRadius * Mathf.Cos(f.ColumnTilt);
        float wheelY = Mathf.Max(shoulders.Y - 0.2f, hipY + 0.2f + nearDrop);

        // As far ahead of the shoulders as both hands still reach the rim turned to the grip's
        // limit: on a flat wheel turned, the far hand goes forward, not up as on a car's.
        DriverSeat At(float ahead) => new(hip, f.Recline, new Vector3(hip.X, wheelY, shoulders.Z + ahead), column, f.WheelRadius,
            Throttle: new Vector3(f.DriverX - 0.11f, ballY, pedalZ),
            Brake: new Vector3(f.DriverX + 0.04f, ballY, pedalZ),
            Rest: new Vector3(f.DriverX + 0.19f, ballY, pedalZ - 0.02f))
        { MaxGrip = HeavyGrip };
        bool Reaches(DriverSeat s)
        {
            foreach (float turn in new[] { 0f, HeavyGrip, -HeavyGrip })
            {
                var (hr, hl, _, _) = HumanMeshBuilder.DriverReach(s, turn);
                if (Mathf.Max(hr, hl) > 0.002f) return false;
            }
            return true;
        }
        float reachAhead = 0.45f;
        while (reachAhead > 0.2f && !Reaches(At(reachAhead))) reachAhead -= 0.01f;
        return At(reachAhead);
    }

    /// <summary>How far the hands follow a heavy's flat wheel before it slides through them, rad (~45°).</summary>
    private const float HeavyGrip = 0.8f;

    /// <summary>The dials' full scales: the speedometer a round step above the limiter, the tach above the governor.</summary>
    public static CarGauges GaugesFor(float limiterKmh, float redlineRpm) => new(
        Mathf.Ceil(limiterKmh * 1.25f / 10f) * 10f,
        Mathf.Ceil((redlineRpm + 500f) / 500f) * 500f,
        redlineRpm);

    /// <summary>
    /// The cockpit, its fixed parts (dash, binnacle, bulkhead, seats, column, the mirrors' housings)
    /// built into <paramref name="m"/> (the section's shell), the rest into meshes of its own.
    /// </summary>
    /// <param name="mirrors">Each mirror's glass: a name, its middle (authored) and its size; the face is aimed from the eye.</param>
    public static HeavyCockpit Build(MeshScratch m, CabFrame f, CarGauges gauges, float airLow, float airMax,
        IEnumerable<(string Name, Vector3 At, Vector2 Size)> mirrors)
    {
        var seat = SeatFor(f);
        var eye = HumanMeshBuilder.DriverEye(seat.Hip, seat.Recline);

        // ---- seats ----
        CarMeshBuilder.Bucket(m, seat.Hip, seat.Recline, f.Floor, DriverSeatColour);
        if (f.PassengerSeat) CarMeshBuilder.Bucket(m, seat.Hip with { X = -seat.Hip.X }, seat.Recline, f.Floor, DriverSeatColour);

        // ---- column, wheel ----
        var steering = CockpitKit.Wheel(m, seat, Kit);

        // ---- the binnacle, seen through the top of the wheel ----
        var panel = new CockpitKit.Panel(seat, eye, Kit);
        var inst = panel.Inst;
        var (face, nd, ud, ld, dialZ) = (panel.Face, panel.N, panel.Up, panel.Left, panel.DialZ);
        const float dialR = 0.058f, spread = 0.11f, airR = 0.03f;
        var tachAt = face + new Vector3(spread, 0, 0);   // on the driver's left, the speedometer on the right
        var speedoAt = face - new Vector3(spread, 0, 0);
        var airAt = face - ud * 0.042f;

        float dashTop = f.DashTop + 0.03f;
        float dashBottom = Mathf.Min(Mathf.Max(f.DashTop - 0.42f, seat.Hip.Y + 0.3f), dashTop - 0.1f);
        float dashRear = dialZ + 0.08f;
        float dashFront = f.Front - 0.06f;
        float x0 = f.InnerHalf, x1 = Mathf.Max(-f.InnerHalf, f.DashToX);
        if (dashFront > dashRear)
            m.Box(new Vector3((x0 + x1) * 0.5f, (dashTop + dashBottom) * 0.5f, (dashRear + dashFront) * 0.5f),
                new Vector3(x0 - x1, dashTop - dashBottom, dashFront - dashRear), Dash);
        // The binnacle behind the dials, square to them, and its hood over them along the line of
        // sight. A flat wheel puts the dials well below the eye, their faces tilted up at it: a box
        // square to the road would stand in front of their upper half.
        var faceBasis = panel.Basis;
        m.Box(face - nd * 0.08f, new Vector3(spread * 2f + 0.18f, 0.21f, 0.16f), Dash, faceBasis);
        m.Box(face + ud * 0.115f, new Vector3(spread * 2f + 0.2f, 0.02f, 0.12f), Dash, faceBasis);
        // the bulkhead the pedals hang in front of, under the dash
        float bulk = f.Front - f.Nose + 0.02f;
        m.Box(new Vector3((x0 + x1) * 0.5f, (f.Floor + dashBottom) * 0.5f, bulk), new Vector3(x0 - x1, dashBottom - f.Floor, 0.04f), Dash);

        // ---- dials: ticks scale with the dial (22 %, 12 % of its radius) ----
        var dialBasis = panel.Basis;
        void DialFace(Vector3 at, float radius, float full, float step, float major, float redFrom, float redBelow) =>
            panel.DialFace(at, radius, full, step, major, 0.22f * radius, 0.12f * radius, redFrom, redBelow);
        DialFace(tachAt, dialR, gauges.TachRpm, 100f, 500f, gauges.RedlineRpm, float.MinValue);
        DialFace(speedoAt, dialR, gauges.SpeedoKmh, 5f, 10f, float.MaxValue, float.MinValue);
        DialFace(airAt, airR, airMax, 1f, 2f, float.MaxValue, airLow);

        // ---- the gear display above the air gauge, the warning lamps along the bottom ----
        var displayAt = face + ud * 0.035f + nd * 0.006f;
        inst.Box(displayAt - nd * 0.002f, new Vector3(0.078f, 0.042f, 0.002f), CockpitKit.Dial, dialBasis);
        var chars = new Dictionary<char, ArrayMesh>[3];
        for (int i = 0; i < chars.Length; i++)
        {
            // position 0 is the leftmost as the driver reads it (−ld is the reader's right)
            var at = displayAt - ld * ((i - 1) * 0.024f);
            chars[i] = HeavyCockpit.GearCharset.ToDictionary(ch => ch, ch => CarMeshBuilder.SevenSegment(at, ud, -ld, nd, ch));
        }
        var lamps = panel.Lamps(new[] { CockpitKit.RedBand, CockpitKit.RedBand, new Color(0.25f, 1f, 0.35f), new Color(0.3f, 0.6f, 1f), Amber, Amber },
            0.09f, 2.5f, 0.034f, new Vector2(0.022f, 0.012f));

        // ---- pedals, each on its own hinge ----
        var pedals = CockpitKit.Pedals(f.Clutch ? new[] { seat.Throttle, seat.Brake, seat.Rest } : new[] { seat.Throttle, seat.Brake }, Kit);

        // ---- mirrors: each face turned halfway between the eye and straight back ----
        var mounts = mirrors.Select(x => CockpitKit.Mirror(m, eye, Kit, x.Name, x.At, x.Size, Trim, 0.08f)).ToArray();

        return new HeavyCockpit(inst.Build(),
            steering, CarMeshBuilder.Turned(seat.WheelAxis),
            panel.Needle(tachAt, dialR), panel.Needle(speedoAt, dialR), panel.Needle(airAt, airR), gauges,
            chars, lamps, pedals, mounts, seat, CarMeshBuilder.Turned(eye), f);
    }

    /// <summary>
    /// A passenger seat for a hip at <paramref name="hip"/> (authored), on a floor at
    /// <paramref name="floor"/>: a moulded shell on a pedestal, the back a little reclined, a grab
    /// handle on its top. Returns its anchor's recline.
    /// </summary>
    public static float PassengerSeat(MeshScratch m, Vector3 hip, float floor, Color colour, bool coach)
    {
        float recline = coach ? 0.32f : 0.2f;
        var back = new Vector3(0, Mathf.Cos(recline), -Mathf.Sin(recline));
        var ahead = new Vector3(0, Mathf.Sin(recline), Mathf.Cos(recline));
        var tilt = new Basis(Vector3.Right, -recline);
        float cushion = hip.Y - 0.09f;
        float width = coach ? 0.46f : 0.43f;
        m.Box(new Vector3(hip.X, cushion - 0.04f, hip.Z + 0.13f), new Vector3(width, 0.08f, 0.44f), colour);
        m.Box(new Vector3(hip.X, (cushion - 0.08f + floor) * 0.5f, hip.Z + 0.13f), new Vector3(0.08f, cushion - 0.08f - floor, 0.3f), Trim);
        float height = coach ? 0.8f : 0.6f;
        m.Box(hip + back * (height * 0.5f) - ahead * 0.15f, new Vector3(width, height, 0.07f), colour, tilt);
        if (!coach)
            m.Box(hip + back * (height + 0.02f) - ahead * 0.15f, new Vector3(0.16f, 0.04f, 0.05f), Steel, tilt);
        else
            // a coach's headrest
            m.Box(hip + back * (height + 0.06f) - ahead * 0.15f, new Vector3(0.3f, 0.12f, 0.09f), colour.Darkened(0.2f), tilt);
        return recline;
    }
}
