using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The sizes a cockpit's shared parts (<see cref="CockpitKit"/>) differ by between a car
/// (<see cref="CarCabin"/>) and a truck or bus (<see cref="HeavyCockpit"/>), m. Each vehicle keeps its
/// own instance next to its builder: change a number there, never in the kit.
/// </summary>
/// <param name="Trim">The wheel's rim and hub, the needles' caps, the pedal pads.</param>
/// <param name="Column">The steering column's colour (the dash's).</param>
/// <param name="ColumnFrom">The column starts this far behind the hub, along the wheel's axis…</param>
/// <param name="ColumnTo">…and ends this far, into the dash.</param>
/// <param name="ColumnRadius">At the hub end.</param>
/// <param name="ColumnFoot">At the dash end.</param>
/// <param name="RimIn">The rim from <see cref="DriverSeat.WheelRadius"/> minus this…</param>
/// <param name="RimOut">…to plus this.</param>
/// <param name="RimDepth">The rim's thickness along the axis.</param>
/// <param name="RimSides">The rim's segments.</param>
/// <param name="HubBack">The hub box's centre behind the wheel's plane.</param>
/// <param name="Hub">The hub box.</param>
/// <param name="SpokeBack">Where the three spokes leave the hub, behind the plane.</param>
/// <param name="SpokeShort">How far short of the rim's middle the spokes stop.</param>
/// <param name="SpokeRadius">The spokes' radius.</param>
/// <param name="Mark">The amber mark at the top of the rim.</param>
/// <param name="DialsAhead">The dials' plane ahead of the hub (authored z).</param>
/// <param name="PedalArm">The pedal arms' radius.</param>
/// <param name="ThrottlePad">The throttle's pad (width, height, thickness).</param>
/// <param name="Pad">The brake's and the clutch's.</param>
/// <param name="MirrorRim">How much wider and taller a mirror's housing is than its glass.</param>
public sealed record CockpitSpec(
    Color Trim, Color Column,
    float ColumnFrom, float ColumnTo, float ColumnRadius, float ColumnFoot,
    float RimIn, float RimOut, float RimDepth, int RimSides,
    float HubBack, Vector3 Hub, float SpokeBack, float SpokeShort, float SpokeRadius, Vector3 Mark,
    float DialsAhead,
    float PedalArm, Vector3 ThrottlePad, Vector3 Pad,
    float MirrorRim);

/// <summary>
/// What a car's cabin and a heavy's cockpit build the same way (#221): the steering wheel on its column,
/// the dials' plane seen through the top of the wheel (<see cref="Panel"/>: dial faces, needles, warning
/// lamps), the hinged pedals and the mirrors aimed halfway between the eye and straight back.
/// Authored space (+Z forward), like the builders that call it.
/// </summary>
public static class CockpitKit
{
    public static readonly Color Dial = new(0.02f, 0.02f, 0.025f);
    public static readonly Color Tick = new(0.92f, 0.92f, 0.88f);
    public static readonly Color RedBand = new(0.95f, 0.12f, 0.08f);
    public static readonly Color Needle = new(1f, 0.38f, 0.1f);
    public static readonly Color Bezel = new(0.45f, 0.46f, 0.5f);
    public static readonly Color MirrorFace = new(0.16f, 0.18f, 0.22f);

    /// <summary>The column into <paramref name="shell"/>; the wheel (rim, hub, three spokes at 3, 9 and 6 o'clock, a mark at the top) on its own hinge.</summary>
    public static HingedPart Wheel(MeshScratch shell, DriverSeat seat, CockpitSpec k)
    {
        var n = seat.WheelAxis;
        var up = (Vector3.Up - n * n.Dot(Vector3.Up)).Normalized();
        var left = n.Cross(up);
        var wc = seat.WheelCentre;
        shell.Tube(wc - n * k.ColumnFrom, wc - n * k.ColumnTo, k.ColumnRadius, k.ColumnFoot, k.Column, 6);
        var wheel = new MeshScratch();
        float r = seat.WheelRadius;
        wheel.Ring(wc, n, r - k.RimIn, r + k.RimOut, k.RimDepth, k.Trim, k.RimSides);
        var hubBasis = new Basis(-left, up, n);
        wheel.Box(wc - n * k.HubBack, k.Hub, k.Trim, hubBasis);
        foreach (var dir in new[] { left, -left, -up })
            wheel.Tube(wc - n * k.SpokeBack, wc + dir * (r - k.SpokeShort), k.SpokeRadius, HeavyMesh.Steel, 4);
        wheel.Box(wc + up * r + n * 0.004f, k.Mark, HeavyMesh.Amber, hubBasis);
        return new HingedPart(wheel.Build(wc), CarMeshBuilder.Turned(wc));
    }

    /// <summary>Halfway between the eye and straight back: what the driver sees in a mirror there is behind the vehicle.</summary>
    public static Vector3 Facing(Vector3 eye, Vector3 at) => ((eye - at).Normalized() + new Vector3(0, 0, -1f)).Normalized();

    /// <summary>A mirror's housing (<paramref name="depth"/> deep) and glass into <paramref name="into"/>, its face aimed by <see cref="Facing"/>.</summary>
    public static CarMirror Mirror(MeshScratch into, Vector3 eye, CockpitSpec k, string name, Vector3 at, Vector2 size, Color housing, float depth)
    {
        var normal = Facing(eye, at);
        var side = Vector3.Up.Cross(normal).Normalized();
        var basis = new Basis(side, normal.Cross(side), normal);
        into.Box(at - normal * depth * 0.5f, new Vector3(size.X + k.MirrorRim, size.Y + k.MirrorRim, depth), housing, basis);
        into.Box(at + normal * 0.001f, new Vector3(size.X, size.Y, 0.002f), MirrorFace, basis);
        return new CarMirror(name, CarMeshBuilder.Turned(at + normal * 0.004f), CarMeshBuilder.Turned(normal), size);
    }

    /// <summary>A pedal per pad, each on its hinge (<see cref="DriverSeat.PedalHinge"/>); the first is the throttle.</summary>
    public static HingedPart[] Pedals(IEnumerable<Vector3> pads, CockpitSpec k) => pads.Select((pad, i) =>
    {
        var hinge = pad + DriverSeat.PedalHinge;
        var m = new MeshScratch();
        m.Tube(hinge, pad + new Vector3(0, 0.03f, 0), k.PedalArm, HeavyMesh.Steel, 4);
        // the pad square to its arm, sloping back toward the sole
        m.Box(pad + new Vector3(0, 0, 0.012f), i == 0 ? k.ThrottlePad : k.Pad, k.Trim, new Basis(Vector3.Right, -0.35f));
        return new HingedPart(m.Build(hinge), CarMeshBuilder.Turned(hinge));
    }).ToArray();

    /// <summary>
    /// The instruments' plane: where the line from the eye through the top of the wheel's opening
    /// meets a plane <see cref="CockpitSpec.DialsAhead"/> ahead of the hub, square to the eye (the
    /// dials just under the rim). The instruments (unshaded) collect in <see cref="Inst"/>.
    /// </summary>
    public sealed class Panel
    {
        public readonly MeshScratch Inst = new();
        /// <summary>The middle of the plane, the dials' plane z.</summary>
        public readonly Vector3 Face;
        public readonly float DialZ;
        /// <summary>Toward the eye, up the plane, and the driver's left along it.</summary>
        public readonly Vector3 N, Up, Left;
        /// <summary>Square to the plane.</summary>
        public readonly Basis Basis;
        private readonly Color _trim;

        public Panel(DriverSeat seat, Vector3 eye, CockpitSpec k)
        {
            var n = seat.WheelAxis;
            var up = (Vector3.Up - n * n.Dot(Vector3.Up)).Normalized();
            var wc = seat.WheelCentre;
            var through = wc + up * seat.WheelRadius * 0.62f;
            var ray = through - eye;
            DialZ = wc.Z + k.DialsAhead;
            Face = eye + ray * ((DialZ - eye.Z) / ray.Z);
            N = (eye - Face).Normalized();
            Up = (Vector3.Up - N * N.Dot(Vector3.Up)).Normalized();
            Left = N.Cross(Up);
            Basis = new Basis(-Left, Up, N);
            _trim = k.Trim;
        }

        private Vector3 Dir(float angle) => Up * Mathf.Cos(angle) + Left * Mathf.Sin(angle);

        /// <summary>
        /// A dial's face and bezel, a tick every <paramref name="step"/> up to <paramref name="full"/>
        /// (<paramref name="bigTick"/> long on each <paramref name="major"/>, <paramref name="smallTick"/>
        /// otherwise), red from <paramref name="redFrom"/> up and below <paramref name="redBelow"/>.
        /// </summary>
        public void DialFace(Vector3 at, float radius, float full, float step, float major, float bigTick, float smallTick,
            float redFrom, float redBelow = float.MinValue)
        {
            Inst.Ring(at + N * 0.002f, N, 0.003f, radius, 0.004f, Dial, 16);
            Inst.Ring(at + N * 0.004f, N, radius, radius + 0.006f, 0.008f, Bezel, 16);
            for (float v = 0; v <= full + 0.01f; v += step)
            {
                bool big = Mathf.Abs(v / major - Mathf.Round(v / major)) < 0.01f;
                var dir = Dir(CarNeedle.Angle(v / full));
                float length = big ? bigTick : smallTick;
                Inst.Box(at + N * 0.005f + dir * (radius * 0.86f - length * 0.5f), new Vector3(0.0035f, length, 0.002f),
                    v >= redFrom || v < redBelow ? RedBand : Tick, new Basis(dir.Cross(N), dir, N));
            }
        }

        /// <summary>A dial's needle, built pointing straight up, on its own pivot.</summary>
        public CarNeedle Needle(Vector3 at, float radius)
        {
            var m = new MeshScratch();
            float length = radius * 0.84f;
            m.Box(at + N * 0.008f + Up * (length * 0.5f - 0.008f), new Vector3(0.004f, length, 0.002f), CockpitKit.Needle, Basis);
            m.Box(at + N * 0.009f, new Vector3(0.012f, 0.012f, 0.004f), _trim, Basis);
            return new CarNeedle(m.Build(at), CarMeshBuilder.Turned(at), CarMeshBuilder.Turned(N));
        }

        /// <summary>
        /// A row of warning lamps <paramref name="below"/> the plane's middle, <paramref name="pitch"/> apart,
        /// lamp <paramref name="middle"/> in the middle: each dark in <see cref="Inst"/>, lit as a mesh of its own.
        /// </summary>
        public ArrayMesh[] Lamps(Color[] colours, float below, float middle, float pitch, Vector2 size)
        {
            var lamps = new ArrayMesh[colours.Length];
            var box = new Vector3(size.X, size.Y, 0.002f);
            for (int i = 0; i < lamps.Length; i++)
            {
                var at = Face - Up * below + Left * ((i - middle) * pitch) + N * 0.004f;
                Inst.Box(at, box, colours[i].Darkened(0.85f), Basis);
                var lit = new MeshScratch();
                lit.Box(at + N * 0.0015f, box, colours[i], Basis);
                lamps[i] = lit.Build();
            }
            return lamps;
        }
    }
}
