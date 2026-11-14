using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The Moon rovers (#1943, docs/plans/moon-surface.md section 7), built like the kart (<see cref="KartMeshBuilder"/>):
/// their own chassis and seat rather than <see cref="CarMeshBuilder"/>'s shell. The <b>open rover</b> is an Apollo-LRV-like
/// two-seater for the Artemis era: a flat folding chassis, wire-mesh wheels with chevron treads under fenders, two
/// tubular seats side by side, a hand controller between them, the dish antenna and camera on the front, a tool pallet
/// behind. The <b>pressurised rover</b> is a cabin on a chassis: thin walls you see from inside as from out, a big
/// windscreen and side windows (glass), two seats in front and two behind, two suitports with their suits on the back,
/// radiators and lamps on the roof. Authored with the front at +Z (the build turns it to −Z), the driver on the left (+X).
/// </summary>
public static class MoonRoverMeshBuilder
{
    private static readonly Color Foil = new(0.86f, 0.70f, 0.30f), Alu = new(0.78f, 0.79f, 0.80f), DarkAlu = new(0.42f, 0.43f, 0.45f),
        Black = new(0.07f, 0.07f, 0.08f), Mesh = new(0.62f, 0.62f, 0.60f), Chevron = new(0.85f, 0.85f, 0.82f),
        Seat = new(0.30f, 0.33f, 0.38f), Glass = new(0.55f, 0.72f, 0.85f), Lamp = new(1f, 0.96f, 0.85f), Suit = new(0.92f, 0.93f, 0.95f);

    /// <summary>The open rover's half track and wheels' width; the cabin's.</summary>
    private const float OpenHalfTrack = 0.92f, CabinHalfTrack = 1.35f, OpenTyre = 0.23f, CabinTyre = 0.36f;

    private static bool Cabin(CarBody b) => b.Shape == BodyShape.RoverCabin;

    /// <summary>The driver's seat: the open rover's upright on the left of its chassis, the cabin's at the front left.</summary>
    public static DriverSeat SeatFor(CarBody body, float wheelbase)
    {
        if (Cabin(body))
        {
            var hip = new Vector3(0.55f, 1.55f, 1.05f);
            const float recline = 0.22f;
            var back = new Vector3(0, Mathf.Cos(recline), -Mathf.Sin(recline));
            var wheel = hip + back * HumanMeshBuilder.DriverTorso + new Vector3(0, -0.22f, 0.45f);
            var column = new Vector3(0, Mathf.Sin(0.95f), -Mathf.Cos(0.95f));
            float ballY = hip.Y - 0.42f, pedalZ = hip.Z + 0.78f;
            return new DriverSeat(hip, recline, wheel, column, 0.15f,
                Throttle: new Vector3(hip.X - 0.10f, ballY, pedalZ), Brake: new Vector3(hip.X + 0.03f, ballY, pedalZ),
                Rest: new Vector3(hip.X + 0.16f, ballY, pedalZ - 0.02f));
        }
        else
        {
            var hip = new Vector3(0.36f, 0.98f, -0.05f);
            const float recline = 0.18f;
            // the hand controller between the seats: a small T-grip the driver's right hand holds
            var grip = new Vector3(0.0f, hip.Y + 0.22f, hip.Z + 0.38f);
            var column = Vector3.Up;
            float ballY = 0.62f, pedalZ = hip.Z + 0.62f;
            return new DriverSeat(hip, recline, grip, column, 0.08f,
                Throttle: new Vector3(hip.X - 0.10f, ballY, pedalZ), Brake: new Vector3(hip.X + 0.03f, ballY, pedalZ),
                Rest: new Vector3(hip.X + 0.16f, ballY, pedalZ - 0.02f));
        }
    }

    private static CockpitSpec Kit => _kit ??= new(Black, DarkAlu,
        ColumnFrom: 0.03f, ColumnTo: 0.35f, ColumnRadius: 0.018f, ColumnFoot: 0.02f,
        RimIn: 0.014f, RimOut: 0.010f, RimDepth: 0.03f, RimSides: 12,
        HubBack: 0.012f, Hub: new Vector3(0.10f, 0.06f, 0.04f), SpokeBack: 0.012f, SpokeShort: 0.01f, SpokeRadius: 0.01f,
        Mark: new Vector3(0.02f, 0.015f, 0.02f),
        DialsAhead: 0.13f,
        PedalArm: 0.01f, ThrottlePad: new Vector3(0.05f, 0.10f, 0.015f), Pad: new Vector3(0.07f, 0.06f, 0.015f),
        MirrorRim: 0.02f);
    private static CockpitSpec? _kit;

    public static CarParts Build(CarBody body, float wheelbase)
    {
        bool cabin = Cabin(body);
        float axF = wheelbase * 0.5f, axR = -axF, r = body.WheelRadius;
        float halfTrack = cabin ? CabinHalfTrack : OpenHalfTrack;
        var seat = SeatFor(body, wheelbase);
        var s = new MeshScratch();   // the chassis and the body
        var c = new MeshScratch();   // the cabin (lit with the driver)
        var seats = new List<Vector3>();
        if (cabin) CabinBody(s, c, body, axF, axR, r, seats, seat);
        else OpenBody(s, c, body, axF, axR, r, seats, seat);

        var steering = CockpitKit.Wheel(c, seat, Kit);
        var pedals = new[] { seat.Throttle, seat.Brake, seat.Rest }.Select(Pedal).ToArray();
        var eye = HumanMeshBuilder.DriverEye(seat.Hip, seat.Recline);
        var none = new MeshScratch().Build();
        var hub = CarMeshBuilder.Turned(seat.WheelCentre);
        var anchors = seats.Select((h, i) => new SeatAnchor(0, CarMeshBuilder.Turned(h), seat.Recline, h.Y - 0.45f)).ToArray();
        var cab = new CarCabin(c.Build(), none, steering, CarMeshBuilder.Turned(seat.WheelAxis),
            new CarNeedle(none, hub, Vector3.Right), new CarNeedle(none, hub, Vector3.Right), CarGauges.Default,
            Enumerable.Repeat(none, 8).ToArray(), Enumerable.Repeat(none, 3).ToArray(), pedals,
            Array.Empty<CarMirror>(), seat, CarMeshBuilder.Turned(eye), anchors);
        return new CarParts(s.Build(), none, none, Wheel(r, cabin ? CabinTyre : OpenTyre, cabin), r, axF, axR, halfTrack,
            Array.Empty<CarDoor>(), 0f, null, cab);
    }

    /// <summary>
    /// The open rover: the chassis' three panels at the axles' height, fenders over the wheels, battery and electronics
    /// boxes in gold foil at the front, the dish antenna and the camera on their masts, two tubular seats side by side,
    /// foot rests, a tool pallet and a sample box at the back.
    /// </summary>
    private static void OpenBody(MeshScratch s, MeshScratch c, CarBody b, float axF, float axR, float r, List<Vector3> seats, DriverSeat seat)
    {
        float y = r + 0.12f;
        // the chassis: three panels, a hinge line between them
        foreach (var (z0, z1) in new[] { (axR - 0.35f, axR + 0.55f), (axR + 0.6f, axF - 0.6f), (axF - 0.55f, axF + 0.35f) })
            s.Box(new Vector3(0, y, (z0 + z1) / 2), new Vector3(1.25f, 0.08f, z1 - z0), Alu);
        foreach (float sx in new[] { -1f, 1f })
        {
            s.Tube(new Vector3(sx * 0.62f, y, axR - 0.4f), new Vector3(sx * 0.62f, y, axF + 0.4f), 0.035f, DarkAlu, 6);
            foreach (float az in new[] { axF, axR })
            {
                // the suspension arms out to the wheel's hub, the fender over it
                s.Tube(new Vector3(sx * 0.62f, y, az - 0.25f), new Vector3(sx * (OpenHalfTrack - 0.12f), r, az), 0.03f, DarkAlu, 5);
                s.Tube(new Vector3(sx * 0.62f, y, az + 0.25f), new Vector3(sx * (OpenHalfTrack - 0.12f), r, az), 0.03f, DarkAlu, 5);
                for (int k = 0; k < 5; k++)
                {
                    float a0 = Mathf.Pi * (0.15f + 0.7f * k / 5f), a1 = Mathf.Pi * (0.15f + 0.7f * (k + 1) / 5f);
                    var p0 = new Vector3(sx * OpenHalfTrack, r + Mathf.Sin(a0) * (r + 0.1f), az + Mathf.Cos(a0) * (r + 0.1f));
                    var p1 = new Vector3(sx * OpenHalfTrack, r + Mathf.Sin(a1) * (r + 0.1f), az + Mathf.Cos(a1) * (r + 0.1f));
                    var d = p1 - p0;
                    s.Box((p0 + p1) / 2, new Vector3(OpenTyre + 0.08f, 0.02f, d.Length() * 1.05f), Foil,
                        new Basis(Vector3.Right, -Mathf.Atan2(d.Y, d.Z)));
                }
            }
        }
        // the front: batteries and the electronics in foil, the dish antenna and the camera
        s.Box(new Vector3(0, y + 0.22f, axF + 0.05f), new Vector3(1.1f, 0.36f, 0.55f), Foil);
        s.Box(new Vector3(0, y + 0.42f, axF + 0.05f), new Vector3(1.12f, 0.04f, 0.57f), Alu);
        s.Tube(new Vector3(0.35f, y + 0.44f, axF + 0.2f), new Vector3(0.35f, y + 1.5f, axF + 0.2f), 0.025f, DarkAlu, 5);
        s.Tube(new Vector3(0.35f, y + 1.5f, axF + 0.2f), new Vector3(0.35f, y + 1.55f, axF + 0.32f), 0.5f, 0.06f, Alu, 14);
        s.Tube(new Vector3(-0.35f, y + 0.44f, axF + 0.1f), new Vector3(-0.35f, y + 1.15f, axF + 0.1f), 0.025f, DarkAlu, 5);
        s.Box(new Vector3(-0.35f, y + 1.2f, axF + 0.12f), new Vector3(0.18f, 0.14f, 0.26f), Black);
        // the console between the seats with its hand controller's housing
        c.Box(new Vector3(0, y + 0.2f, seat.Hip.Z + 0.38f), new Vector3(0.2f, 0.32f, 0.3f), DarkAlu);
        c.Box(new Vector3(0, y + 0.55f, seat.Hip.Z + 0.55f), new Vector3(0.5f, 0.35f, 0.08f), Black, new Basis(Vector3.Right, -0.4f));
        // two seats: tubular frames, a webbing seat and back, a foot rest each
        foreach (float sx in new[] { 1f, -1f })
        {
            var hip = new Vector3(sx * seat.Hip.X, seat.Hip.Y, seat.Hip.Z);
            seats.Add(hip);
            var back = new Vector3(0, Mathf.Cos(seat.Recline), -Mathf.Sin(seat.Recline));
            c.Box(new Vector3(hip.X, hip.Y - 0.06f, hip.Z + 0.04f), new Vector3(0.48f, 0.04f, 0.46f), Seat);
            c.Box(hip + back * 0.32f + new Vector3(0, 0, -0.22f), new Vector3(0.48f, 0.55f, 0.04f), Seat, new Basis(Vector3.Right, -seat.Recline));
            foreach (float ex in new[] { -0.24f, 0.24f })
            {
                c.Tube(new Vector3(hip.X + ex, y, hip.Z - 0.2f), new Vector3(hip.X + ex, hip.Y - 0.06f, hip.Z - 0.2f), 0.018f, DarkAlu, 5);
                c.Tube(new Vector3(hip.X + ex, y, hip.Z + 0.25f), new Vector3(hip.X + ex, hip.Y - 0.06f, hip.Z + 0.25f), 0.018f, DarkAlu, 5);
            }
            c.Box(new Vector3(hip.X, 0.62f - 0.03f, hip.Z + 0.62f), new Vector3(0.42f, 0.03f, 0.3f), DarkAlu);
        }
        // the back: the tool pallet, a sample box, a flag
        s.Box(new Vector3(0, y + 0.3f, axR - 0.05f), new Vector3(1.15f, 0.5f, 0.55f), DarkAlu);
        s.Box(new Vector3(0.3f, y + 0.62f, axR - 0.05f), new Vector3(0.36f, 0.16f, 0.3f), Alu);
        for (int i = 0; i < 4; i++) s.Tube(new Vector3(-0.45f + i * 0.12f, y + 0.55f, axR - 0.2f), new Vector3(-0.45f + i * 0.12f, y + 1.1f, axR - 0.2f), 0.015f, Alu, 4);
        s.Tube(new Vector3(-0.55f, y + 0.05f, axR - 0.3f), new Vector3(-0.55f, y + 1.7f, axR - 0.3f), 0.012f, Alu, 4);
        s.Box(new Vector3(-0.55f, y + 1.55f, axR - 0.05f), new Vector3(0.02f, 0.3f, 0.5f), b.Paint);
    }

    /// <summary>
    /// The pressurised rover: a chassis, the cabin of thin walls (seen from inside as from out) with a raked windscreen
    /// and side windows on the glass surface, two seats in front at a console and two behind, the suitports on the back
    /// wall with a suit on each, roof radiators, lamps, an antenna, a hatch.
    /// </summary>
    private static void CabinBody(MeshScratch s, MeshScratch c, CarBody b, float axF, float axR, float r, List<Vector3> seats, DriverSeat seat)
    {
        float floor = r + 0.45f, top = floor + 2.15f, hw = 1.25f, front = axF + 0.9f, rear = axR - 0.75f;
        const float t = 0.06f;
        // the chassis and the wheel pods
        s.Box(new Vector3(0, r + 0.2f, (front + rear) / 2), new Vector3(2.2f, 0.4f, front - rear), DarkAlu);
        foreach (float sx in new[] { -1f, 1f })
            foreach (float az in new[] { axF, axR })
            {
                s.Box(new Vector3(sx * (CabinHalfTrack - 0.35f), r + 0.05f, az), new Vector3(0.5f, 0.3f, 0.5f), Black);
                s.Box(new Vector3(sx * CabinHalfTrack, r * 2 + 0.12f, az), new Vector3(CabinTyre + 0.2f, 0.06f, 2 * r + 0.3f), b.Paint);
            }
        // the cabin's walls (thin boxes: both faces), the floor and the roof
        s.Box(new Vector3(0, floor - t / 2, (front + rear) / 2 - 0.4f), new Vector3(2 * hw, t, front - rear - 0.8f), b.Lower ?? Alu);
        s.Box(new Vector3(0, top + t / 2, (front + rear) / 2 - 0.3f), new Vector3(2 * hw, t, front - rear - 0.6f), b.Paint);
        float glassFrom = floor + 0.95f, glassTo = top - 0.3f, winz0 = rear + 0.6f, winz1 = front - 1.4f;
        foreach (float sx in new[] { -1f, 1f })
        {
            float x = sx * hw;
            // under and over the windows, the posts between, the pane
            s.Box(new Vector3(x, (floor + glassFrom) / 2, (front - 0.6f + rear) / 2), new Vector3(t, glassFrom - floor, front - 0.6f - rear), b.Paint);
            s.Box(new Vector3(x, (glassTo + top) / 2, (front - 0.6f + rear) / 2), new Vector3(t, top - glassTo, front - 0.6f - rear), b.Paint);
            s.Box(new Vector3(x, (glassFrom + glassTo) / 2, (rear + winz0) / 2), new Vector3(t, glassTo - glassFrom, winz0 - rear), b.Paint);
            s.Box(new Vector3(x, (glassFrom + glassTo) / 2, (winz1 + front - 0.6f) / 2), new Vector3(t, glassTo - glassFrom, front - 0.6f - winz1), b.Paint);
            s.Pane([new(x, glassFrom, winz0), new(x, glassFrom, winz1), new(x, glassTo, winz1), new(x, glassTo, winz0)], Glass);
            s.Box(new Vector3(x + sx * 0.02f, glassFrom - 0.12f, (front + rear) / 2), new Vector3(0.02f, 0.08f, front - rear), b.Lower ?? Alu);
        }
        // the nose: a sloped lower panel and the raked windscreen up to the roof
        float noseZ = front - 0.6f;
        s.Box(new Vector3(0, (floor + glassFrom) / 2, noseZ + 0.3f), new Vector3(2 * hw, glassFrom - floor, t), b.Paint, new Basis(Vector3.Right, 0.35f));
        s.Pane([new(-hw, glassFrom, noseZ + 0.45f), new(hw, glassFrom, noseZ + 0.45f), new(hw, top, noseZ - 0.3f), new(-hw, top, noseZ - 0.3f)], Glass);
        s.Box(new Vector3(0, top - 0.02f, noseZ - 0.3f), new Vector3(2 * hw, 0.08f, 0.1f), b.Paint);
        foreach (float sx in new[] { -1f, 1f })
            s.Box(new Vector3(sx * hw, (glassFrom + top) / 2, noseZ + 0.07f), new Vector3(0.1f, top - glassFrom + 0.1f, 0.1f), b.Paint,
                new Basis(Vector3.Right, -Mathf.Atan2(0.75f, top - glassFrom)));
        // the back wall with two suitports, a suit docked in each
        s.Box(new Vector3(0, (floor + top) / 2, rear), new Vector3(2 * hw, top - floor, t), b.Paint);
        foreach (float sx in new[] { -0.55f, 0.55f })
        {
            s.Ring(new Vector3(sx, floor + 1.25f, rear - 0.05f), Vector3.Back, 0.33f, 0.42f, 0.1f, DarkAlu, 16);
            s.Box(new Vector3(sx, floor + 1.25f, rear - 0.35f), new Vector3(0.55f, 0.7f, 0.5f), Suit);
            s.Tube(new Vector3(sx, floor + 1.7f, rear - 0.3f), new Vector3(sx, floor + 1.72f, rear - 0.3f), 0.17f, Suit, 10);
            s.Tube(new Vector3(sx - 0.22f, floor + 0.85f, rear - 0.35f), new Vector3(sx - 0.15f, floor + 0.2f, rear - 0.35f), 0.09f, Suit, 6);
            s.Tube(new Vector3(sx + 0.22f, floor + 0.85f, rear - 0.35f), new Vector3(sx + 0.15f, floor + 0.2f, rear - 0.35f), 0.09f, Suit, 6);
        }
        // the roof: radiators, lamps, an antenna, the hatch
        s.Box(new Vector3(0, top + 0.12f, rear + 0.9f), new Vector3(2.0f, 0.06f, 1.4f), Alu);
        for (int i = 0; i < 6; i++) s.Box(new Vector3(-0.85f + i * 0.34f, top + 0.16f, rear + 0.9f), new Vector3(0.04f, 0.04f, 1.38f), DarkAlu);
        s.Ring(new Vector3(0, top + 0.08f, (front + rear) / 2 + 0.3f), Vector3.Up, 0.0f, 0.4f, 0.08f, DarkAlu, 14);
        s.Tube(new Vector3(-0.9f, top, rear + 0.3f), new Vector3(-0.9f, top + 1.1f, rear + 0.3f), 0.02f, Alu, 4);
        foreach (float sx in new[] { -0.8f, 0.8f })
            s.Pane([new(sx - 0.18f, top - 0.18f, noseZ - 0.27f), new(sx + 0.18f, top - 0.18f, noseZ - 0.27f),
                new(sx + 0.18f, top - 0.04f, noseZ - 0.33f), new(sx - 0.18f, top - 0.04f, noseZ - 0.33f)], Lamp);
        // inside: four seats, the console, a lamp strip along the roof
        foreach (var hip in new[] { seat.Hip, seat.Hip with { X = -seat.Hip.X }, seat.Hip with { Z = seat.Hip.Z - 1.25f },
                     new Vector3(-seat.Hip.X, seat.Hip.Y, seat.Hip.Z - 1.25f) })
        {
            seats.Add(hip);
            c.Box(new Vector3(hip.X, (floor + hip.Y - 0.06f) / 2, hip.Z + 0.02f), new Vector3(0.48f, hip.Y - 0.06f - floor, 0.48f), Seat);
            var back = new Vector3(0, Mathf.Cos(seat.Recline), -Mathf.Sin(seat.Recline));
            c.Box(hip + back * 0.34f + new Vector3(0, 0, -0.24f), new Vector3(0.5f, 0.68f, 0.08f), Seat, new Basis(Vector3.Right, -seat.Recline));
        }
        c.Box(new Vector3(0, floor + 0.55f, noseZ - 0.05f), new Vector3(2 * hw - 0.1f, 1.1f, 0.5f), DarkAlu);
        c.Box(new Vector3(0, floor + 1.12f, noseZ - 0.15f), new Vector3(2 * hw - 0.2f, 0.04f, 0.5f), Black);
        for (int i = 0; i < 3; i++)
            c.Pane([new(-0.9f + i * 0.6f, floor + 1.15f, noseZ - 0.3f), new(-0.5f + i * 0.6f, floor + 1.15f, noseZ - 0.3f),
                new(-0.5f + i * 0.6f, floor + 1.4f, noseZ - 0.2f), new(-0.9f + i * 0.6f, floor + 1.4f, noseZ - 0.2f)], new Color(0.35f, 0.75f, 0.95f));
        c.Box(new Vector3(0, top - 0.04f, (front + rear) / 2 - 0.3f), new Vector3(0.2f, 0.03f, front - rear - 1.6f), Lamp);
    }

    /// <summary>A pedal's pad on its hinge (a foot rest on the open rover).</summary>
    private static HingedPart Pedal(Vector3 pad)
    {
        var hinge = pad + DriverSeat.PedalHinge;
        var m = new MeshScratch();
        m.Box(pad + new Vector3(0, 0, 0.012f), new Vector3(0.06f, 0.09f, 0.015f), Black, new Basis(Vector3.Right, -0.35f));
        return new HingedPart(m.Build(hinge), CarMeshBuilder.Turned(hinge));
    }

    /// <summary>
    /// One wheel about X at the origin: the open rover's woven wire mesh with titanium chevrons over a hub and spokes (an
    /// LRV's), the cabin's a big airless tyre of blocks.
    /// </summary>
    private static ArrayMesh Wheel(float radius, float width, bool cabin)
    {
        var s = new MeshScratch();
        if (cabin)
        {
            s.Ring(Vector3.Zero, Vector3.Right, radius * 0.6f, radius, width, Black, 20);
            for (int i = 0; i < 20; i++)
            {
                float a = Mathf.Tau * i / 20;
                s.Box(new Vector3(0, Mathf.Cos(a), Mathf.Sin(a)) * (radius + 0.02f), new Vector3(width * 0.9f, 0.06f, 0.12f), DarkAlu, new Basis(Vector3.Right, a));
            }
            s.Ring(Vector3.Zero, Vector3.Right, 0.06f, radius * 0.62f, width * 0.6f, Alu, 16);
        }
        else
        {
            s.Ring(Vector3.Zero, Vector3.Right, radius * 0.88f, radius, width, Mesh, 20);
            for (int i = 0; i < 20; i++)
            {
                float a = Mathf.Tau * i / 20;
                foreach (float sx in new[] { -1f, 1f })
                    s.Box(new Vector3(sx * width * 0.22f, Mathf.Cos(a), Mathf.Sin(a)) * 1 + new Vector3(0, Mathf.Cos(a), Mathf.Sin(a)) * (radius - 1),
                        new Vector3(width * 0.42f, 0.015f, 0.05f), Chevron, new Basis(Vector3.Right, a) * new Basis(Vector3.Up, sx * 0.5f));
            }
            for (int i = 0; i < 8; i++)
            {
                float a = Mathf.Tau * i / 8;
                s.Box(new Vector3(0, Mathf.Cos(a), Mathf.Sin(a)) * (radius * 0.45f), new Vector3(0.02f, radius * 0.86f, 0.02f), DarkAlu, new Basis(Vector3.Right, a));
            }
            s.Tube(new Vector3(-width * 0.3f, 0, 0), new Vector3(width * 0.3f, 0, 0), 0.08f, Alu, 8);
        }
        return s.Build();
    }

    /// <summary>The rovers in the model viewer.</summary>
    [Core.Showcase("Moon rovers")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> Showcase()
    {
        foreach (var spec in new[] { Player.MoonRovers.Open, Player.MoonRovers.Cabin })
        {
            var sp = spec;
            yield return (sp.Label, () => CarRig.Create(sp.Body, sp.Wheelbase, sp.Gauges, HumanPalette.ForRider(3)));
        }
    }
}
