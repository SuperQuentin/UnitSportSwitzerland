using Godot;
using UnitSport.Player;
using static UnitSport.Avatar.HeavyMesh;

namespace UnitSport.Avatar;

/// <summary>
/// Low-poly farm machines (#494), in the heavy vehicles' style (<see cref="PickupMeshBuilder"/>,
/// <see cref="TrailerMeshBuilder"/>): a 4WD tractor (narrow bonnet between small front wheels, a
/// glass cab with two doors over big rear wheels, the lift arms, drawbar and PTO), a combine
/// harvester (its header and reel on the lift, the cab over the feeder, the grain tank open to its
/// heap, the unloading auger on its hinge) and the implements: a reversible plough, a seed drill and
/// a disc mower on the lift, a tipping trailer's dolly and body with its heap. Authored facing +Z,
/// origin on the ground under the centre of mass (z = cg − metres behind the front); colours from
/// <see cref="HeavyLook"/> and <see cref="TrailerSpec"/>. Wheels are the rig's.
/// </summary>
public static class FarmMeshBuilder
{
    private static readonly Color Grain = new(0.86f, 0.71f, 0.33f);
    private static readonly Color Soil = new(0.36f, 0.26f, 0.17f);
    private static readonly Color Share = new(0.62f, 0.64f, 0.66f);

    // ---- the tractor's stations, metres behind its front, and heights ----
    public const float TractorCabFrom = 2.35f, TractorCabTo = 4.45f;
    private const float TractorFloor = 1.4f, TractorRoof = 3.1f, TractorPane = 2.45f;
    /// <summary>The bonnet and the rear wheels' tops: the hull's lower box (<c>Truck.FarmHull</c>).</summary>
    public const float TractorBonnetTop = 1.92f;
    /// <summary>The eye from the seat (as <c>Truck.FirstPersonEye</c> takes it) and the door's station.</summary>
    public const float TractorEyeY = 2.62f, TractorEyeAt = 3.45f, TractorDoorAt = 3.0f;

    // ---- the combine's ----
    public const float HeaderFrom = 0f, HeaderTo = 1.9f, HeaderWidth = 6.0f, HeaderTop = 1.3f;
    private const float CombineCabFrom = 2.5f, CombineCabTo = 4.3f, CombineFloor = 2.15f, CombineRoof = 3.8f, CombinePane = 2.6f;
    public const float CombineEyeY = 3.4f, CombineEyeAt = 3.5f, CombineDoorAt = 3.1f;
    /// <summary>How far the header and the implements rise for the road, m.</summary>
    public const float HeaderRaise = 0.6f;
    /// <summary>The auger's hinge (metres behind the front, up, out to the left) and its length, m.</summary>
    public const float AugerAt = 7.0f, AugerY = 3.6f, AugerX = 1.5f, AugerLength = 5.2f;

    /// <summary>Whether this builder draws a trailer: the implements and the tipping trailer.</summary>
    public static bool Draws(TrailerSpec spec) => spec.Body is TrailerBody.Plough or TrailerBody.SeedDrill or TrailerBody.Mower or TrailerBody.Tipper;

    private static int DoorIndex(bool left) => left ? 1 : 0;

    // ---- the tractor ----------------------------------------------------------------------------

    public static HeavyParts Tractor(HeavySpec spec, int section, float load)
    {
        var s = spec.Sections[section];
        var look = spec.Look;
        float cg = Cg(s, load);
        float Z(float at) => cg - at;
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var rev = new MeshScratch();
        float rf = Tyre.Radius(s.Axles[0].Tyre) * 0.97f, rr = Tyre.Radius(s.Axles[1].Tyre) * 0.97f;
        float front = s.Axles[0].At, rear = s.Axles[1].At;
        float wheelX = s.Width * 0.5f - 0.05f;   // the tyres' outer face

        // ---- the nose: front weight, grille, the narrow bonnet between the front wheels ----
        Along(m, cg, 0f, 0.3f, 0.45f, 1.0f, 0.85f, look.Lower);
        Along(m, cg, 0.25f, 0.4f, 0.8f, 1.7f, 0.95f, look.Accent);
        Along(m, cg, 0.35f, TractorCabFrom + 0.05f, 0.85f, 1.75f, 1.0f, look.Paint);
        Along(m, cg, 0.5f, TractorCabFrom, 1.75f, 1.8f, 0.7f, look.Paint);
        // engine and frame under it, the front axle
        Along(m, cg, 0.4f, rear, 0.45f, 0.85f, 0.8f, look.Lower);
        m.Tube(new Vector3(-wheelX + 0.3f, rf, Z(front)), new Vector3(wheelX - 0.3f, rf, Z(front)), 0.09f, look.Lower, 6);
        // front mudguards, swinging with nothing: over the tyre's top
        foreach (float sx in new[] { -1f, 1f })
            Along(m, cg, front - 0.55f, front + 0.55f, 2f * rf + 0.06f, 2f * rf + 0.1f, 0.62f, look.Lower, sx * (wheelX - 0.29f));
        // the exhaust up the right front pillar
        m.Tube(new Vector3(-0.42f, 1.75f, Z(TractorCabFrom - 0.12f)), new Vector3(-0.42f, 3.0f, Z(TractorCabFrom - 0.12f)), 0.055f, Trim, 6);
        // head lamps in the grille and the work lamps on the roof
        foreach (float sx in new[] { -1f, 1f })
        {
            Lamp(head, sx * 0.3f, 1.45f, Z(0.24f), 0.18f, 0.1f, 0.04f, HeadLamp);
            Lamp(head, sx * 0.7f, TractorRoof - 0.12f, Z(TractorCabFrom - 0.02f), 0.16f, 0.08f, 0.04f, HeadLamp);
        }

        // ---- the cab: a glass box on its floor, roof over it, two doors ----
        float cabW = 1.9f, inner = cabW * 0.5f - 0.05f;
        Along(m, cg, TractorCabFrom, TractorCabTo, TractorFloor - 0.12f, TractorFloor, cabW, look.Lower);
        Along(m, cg, TractorCabFrom + 0.05f, TractorCabTo - 0.05f, TractorFloor, TractorFloor + 0.01f, inner * 2f, HeavyCabin.FloorColour);
        Along(m, cg, TractorCabFrom - 0.05f, TractorCabTo + 0.05f, TractorRoof - 0.14f, TractorRoof, cabW + 0.1f, look.Paint);
        Along(m, cg, TractorCabFrom, TractorCabTo, TractorRoof - 0.15f, TractorRoof - 0.14f, inner * 2f, HeavyCabin.Lining);
        // four corner pillars
        foreach (float sx in new[] { -1f, 1f })
            foreach (float at in new[] { TractorPane, TractorCabTo - 0.05f })
                Along(m, cg, at - 0.05f, at + 0.05f, TractorFloor, TractorRoof - 0.14f, 0.07f, look.Lower, sx * (cabW * 0.5f - 0.035f));
        // the windscreen down to the floor's front, the rear window
        FrontPane(m, Z(TractorPane), inner, TractorFloor + 0.1f, TractorRoof - 0.16f);
        FrontPane(m, Z(TractorCabTo - 0.05f), inner, TractorFloor + 0.45f, TractorRoof - 0.16f);
        // the cowl under the windscreen, the rear wall's foot
        Along(m, cg, TractorPane - 0.12f, TractorPane, TractorFloor - 0.1f, TractorFloor + 0.1f, cabW, look.Paint);
        Along(m, cg, TractorCabTo - 0.08f, TractorCabTo, TractorFloor, TractorFloor + 0.45f, cabW, look.Paint);
        // a door each side: all glass in a dark frame, hinged at its front edge
        var leaves = new List<HeavyDoorLeaf>();
        foreach (float sx in new[] { -1f, 1f })
        {
            float at0 = TractorPane + 0.06f, at1 = TractorCabTo - 0.65f;
            float x = sx * cabW * 0.5f;
            var leaf = new MeshScratch();
            Along(leaf, cg, at0, at1, TractorFloor, TractorFloor + 0.06f, 0.05f, look.Lower, x);
            Along(leaf, cg, at0, at1, TractorRoof - 0.2f, TractorRoof - 0.15f, 0.05f, look.Lower, x);
            Along(leaf, cg, at1 - 0.05f, at1, TractorFloor, TractorRoof - 0.15f, 0.05f, look.Lower, x);
            Along(leaf, cg, at1 - 0.25f, at1 - 0.12f, 2.0f, 2.04f, 0.04f, Trim, x + sx * 0.025f);
            SidePane(leaf, x, Z(at0), Z(at1 - 0.05f), TractorFloor + 0.06f, TractorRoof - 0.2f);
            var pivot = new Vector3(x, 0f, Z(at0));
            leaves.Add(new HeavyDoorLeaf(DoorIndex(sx > 0), leaf.Build(pivot), new Vector3(-pivot.X, 0f, -pivot.Z), sx > 0 ? -1.2f : 1.2f)
            {
                Centre = new Vector3(-x, (TractorFloor + TractorRoof) * 0.5f, -Z((at0 + at1) * 0.5f)),
            });
            // the fixed glass behind the door, over the fender
            SidePane(m, x, Z(at1 + 0.05f), Z(TractorCabTo - 0.1f), TractorFloor + 0.5f, TractorRoof - 0.2f);
            // the step up to the door
            Along(m, cg, at0 + 0.1f, at0 + 0.55f, 0.55f, 0.6f, 0.06f, Trim, sx * (cabW * 0.5f + 0.15f));
            Along(m, cg, at0 + 0.1f, at0 + 0.55f, 1.0f, 1.05f, 0.06f, Trim, sx * (cabW * 0.5f + 0.1f));
        }
        // mirrors on arms off the front pillars
        var mirrors = new List<(string, Vector3, Vector2)>();
        foreach (float sx in new[] { -1f, 1f })
        {
            m.Tube(new Vector3(sx * cabW * 0.5f, 2.6f, Z(TractorPane)), new Vector3(sx * (cabW * 0.5f + 0.45f), 2.65f, Z(TractorPane + 0.05f)), 0.02f, Trim, 4);
            mirrors.Add((sx > 0 ? "MirrorLeft" : "MirrorRight", new Vector3(sx * (cabW * 0.5f + 0.5f), 2.5f, Z(TractorPane + 0.08f)), new Vector2(0.18f, 0.28f)));
        }
        var cab = new CabFrame
        {
            Front = Z(TractorPane), Floor = TractorFloor, Ceiling = TractorRoof - 0.15f, WsBase = TractorFloor + 0.1f, WsTop = TractorRoof - 0.18f,
            DashTop = TractorFloor + 0.5f, InnerHalf = inner, Nose = 0.3f, DriverX = 0f, HipRise = 0.48f, Recline = 0.2f, ColumnTilt = 0.95f,
            WheelRadius = 0.19f, DashToX = -0.25f, Clutch = false, PassengerSeat = false,
        };
        var cockpit = HeavyCabin.Build(m, cab, HeavyCabin.GaugesFor(spec.LimiterKmh, spec.Redline), HeavyDriveline.AirLow, HeavyDriveline.AirMax, mirrors);
        var hip = cockpit.Seat.Hip;
        // the instructor's fold-down seat on the right, beside the driver
        var buddy = new Vector3(-inner + 0.3f, TractorFloor + 0.45f, hip.Z + 0.2f);
        CarMeshBuilder.Bucket(m, buddy, 0.15f, TractorFloor, HeavyCabin.DriverSeatColour);
        // the right-hand console with its joystick
        Along(m, cg, cg - hip.Z - 0.4f, cg - hip.Z + 0.25f, TractorFloor, hip.Y + 0.1f, 0.22f, HeavyCabin.Dash, -0.38f);
        var seats = new[]
        {
            new SeatAnchor(0, CarMeshBuilder.Turned(hip), cockpit.Seat.Recline, TractorFloor),
            new SeatAnchor(0, CarMeshBuilder.Turned(buddy), 0.15f, TractorFloor),
        };

        // ---- the rear: fenders over the big wheels, the axle housing, the linkage and drawbar ----
        foreach (float sx in new[] { -1f, 1f })
        {
            float fx = sx * (wheelX - 0.33f);
            Along(m, cg, rear - rr - 0.05f, rear + rr * 0.85f, 2f * rr + 0.04f, 2f * rr + 0.1f, 0.72f, look.Paint, fx);
            Along(m, cg, rear + rr * 0.7f, rear + rr * 0.85f, rr * 1.2f, 2f * rr + 0.04f, 0.72f, look.Paint, fx);
            // the tail lamps on the fenders' backs
            Lamp(tail, fx, rr * 1.5f, Z(rear + rr * 0.86f), 0.14f, 0.12f, 0.04f, TailLamp);
            Lamp(rev, fx - sx * 0.2f, rr * 1.5f, Z(rear + rr * 0.86f), 0.1f, 0.1f, 0.04f, White);
        }
        Along(m, cg, rear - 0.5f, rear + 0.6f, 0.5f, 1.35f, 0.95f, look.Lower);
        m.Tube(new Vector3(-wheelX + 0.3f, rr, Z(rear)), new Vector3(wheelX - 0.3f, rr, Z(rear)), 0.14f, look.Lower, 8);
        // the lift arms up top, the lower links and the top link, the PTO stub
        foreach (float sx in new[] { -1f, 1f })
        {
            m.Tube(new Vector3(sx * 0.32f, 1.3f, Z(rear + 0.4f)), new Vector3(sx * 0.36f, 1.15f, Z(s.HitchAt - 0.1f)), 0.05f, look.Lower, 5);
            m.Tube(new Vector3(sx * 0.3f, 0.6f, Z(rear + 0.2f)), new Vector3(sx * 0.42f, s.HitchHeight, Z(s.HitchAt)), 0.045f, Steel, 5);
        }
        m.Tube(new Vector3(0, 1.25f, Z(rear + 0.55f)), new Vector3(0, 1.1f, Z(s.HitchAt)), 0.045f, Steel, 5);
        m.Tube(new Vector3(0, 0.75f, Z(rear + 0.6f)), new Vector3(0, 0.75f, Z(rear + 0.75f)), 0.04f, Steel, 6);
        // the drawbar's jaw under it
        Along(m, cg, rear + 0.4f, s.HitchAt, s.HitchHeight - 0.06f, s.HitchHeight + 0.06f, 0.16f, Trim);

        return new HeavyParts(m.Build(), head.Build(), tail.Build(), rev.Build(), Wheels(s, cg), leaves.ToArray())
        {
            Cockpit = cockpit,
            Seats = seats,
            CarDoors = true,
        };
    }

    // ---- the combine ----------------------------------------------------------------------------

    public static HeavyParts Combine(HeavySpec spec, int section, float load)
    {
        var s = spec.Sections[section];
        var look = spec.Look;
        float cg = Cg(s, load);
        float Z(float at) => cg - at;
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var rev = new MeshScratch();
        float rf = Tyre.Radius(s.Axles[0].Tyre) * 0.97f, rr = Tyre.Radius(s.Axles[1].Tyre) * 0.97f;
        float front = s.Axles[0].At, rear = s.Axles[1].At;
        float hw = s.Width * 0.5f;

        // ---- the header on the lift, drawn down on the stubble: the reel apart ----
        var h = new MeshScratch();
        float half = HeaderWidth * 0.5f;
        Along(h, cg, 0.05f, HeaderTo - 0.3f, 0.06f, 0.18f, HeaderWidth - 0.1f, Steel);                       // the floor pan
        Along(h, cg, HeaderTo - 0.45f, HeaderTo - 0.3f, 0.18f, 1.15f, HeaderWidth - 0.1f, look.Paint);       // the back wall
        Along(h, cg, 0.25f, 0.4f, 0.04f, 0.1f, HeaderWidth, Share);                                           // the cutter bar
        h.Tube(new Vector3(-half + 0.1f, 0.45f, Z(1.05f)), new Vector3(half - 0.1f, 0.45f, Z(1.05f)), 0.28f, look.Paint, 8);   // the cross auger
        foreach (float sx in new[] { -1f, 1f })
        {
            Along(h, cg, 0.0f, HeaderTo - 0.3f, 0.06f, 0.95f, 0.08f, look.Paint, sx * (half - 0.04f));          // the end plates
            h.Tube(new Vector3(sx * (half - 0.04f), 0.1f, Z(-0.25f)), new Vector3(sx * (half - 0.04f), 0.7f, Z(0.6f)), 0.05f, look.Accent, 4);   // the crop dividers
            // the reel's arms back to the wall
            h.Tube(new Vector3(sx * (half - 0.25f), 1.35f, Z(0.75f)), new Vector3(sx * (half - 0.25f), 1.15f, Z(HeaderTo - 0.4f)), 0.05f, look.Lower, 5);
        }
        // the feeder up into the body, with the header's back
        Along(h, cg, HeaderTo - 0.3f, 3.0f, 0.4f, 1.6f, 1.5f, look.Paint);
        var reel = new MeshScratch();
        reel.Tube(new Vector3(-half + 0.3f, 0, 0), new Vector3(half - 0.3f, 0, 0), 0.07f, look.Lower, 6);
        for (int i = 0; i < 6; i++)
        {
            float a = i * Mathf.Tau / 6f;
            var o = new Vector3(0, Mathf.Sin(a), Mathf.Cos(a)) * 0.55f;
            reel.Tube(new Vector3(-half + 0.35f, o.Y, o.Z), new Vector3(half - 0.35f, o.Y, o.Z), 0.03f, look.Accent, 4);
            foreach (float x in new[] { -half + 0.4f, 0f, half - 0.4f })
                reel.Tube(new Vector3(x, 0, 0), new Vector3(x, o.Y, o.Z), 0.02f, look.Lower, 3);
        }

        // ---- the body over the wheels, the tank open to its heap, the engine and the chopper ----
        Along(m, cg, 2.9f, 9.9f, 0.85f, 2.05f, 1.6f, look.Paint);
        Along(m, cg, 3.0f, 9.7f, 2.05f, 3.0f, s.Width, look.Paint);
        Sides(m, cg, 3.0f, 9.7f, 2.0f, 2.1f, s.Width, look.Lower);
        Along(m, cg, front - 0.3f, front + 0.3f, rf - 0.1f, rf + 0.15f, 2.2f, look.Lower);                   // the drive axle
        Along(m, cg, rear - 0.2f, rear + 0.2f, rr - 0.1f, rr + 0.1f, 2.4f, look.Lower);                      // the steering axle
        // mudguards over the drive wheels
        foreach (float sx in new[] { -1f, 1f })
            Along(m, cg, front - rf - 0.05f, front + rf + 0.05f, 2f * rf + 0.05f, 2f * rf + 0.12f, 0.85f, look.Lower, sx * (hw - 0.45f));
        const float tank0 = 4.4f, tank1 = 7.2f, tankFloor = 3.0f, tankTop = 3.95f;
        foreach (float sx in new[] { -1f, 1f })
            Along(m, cg, tank0, tank1, tankFloor, tankTop, 0.08f, look.Paint, sx * (hw - 0.2f));
        Along(m, cg, tank0, tank0 + 0.08f, tankFloor, tankTop, s.Width - 0.4f, look.Paint);
        Along(m, cg, tank1 - 0.08f, tank1, tankFloor, tankTop, s.Width - 0.4f, look.Paint);
        Along(m, cg, tank0, tank1, tankFloor, tankFloor + 0.02f, s.Width - 0.5f, look.Lower);
        // the engine bay behind the tank, its grille, the exhaust
        Along(m, cg, tank1, 9.6f, 3.0f, 3.55f, s.Width - 0.6f, look.Paint);
        Sides(m, cg, tank1 + 0.3f, 9.3f, 3.05f, 3.45f, s.Width - 0.6f, look.Lower);
        m.Tube(new Vector3(-0.6f, 3.5f, Z(8.8f)), new Vector3(-0.6f, 4.0f, Z(8.8f)), 0.08f, Trim, 6);
        // the straw chopper at the back
        Along(m, cg, 9.6f, s.Length, 0.8f, 2.4f, 1.8f, look.Lower);
        Along(m, cg, 9.9f, s.Length, 2.4f, 2.6f, 2.2f, look.Paint);
        foreach (float sx in new[] { -1f, 1f })
        {
            Lamp(tail, sx * 0.85f, 2.2f, Z(s.Length + 0.01f), 0.2f, 0.15f, 0.04f, TailLamp);
            Lamp(rev, sx * 0.5f, 2.2f, Z(s.Length + 0.01f), 0.12f, 0.12f, 0.04f, White);
        }

        // ---- the cab over the feeder: glass all round, the ladder on the left ----
        float cabW = 2.0f, inner = cabW * 0.5f - 0.05f;
        Along(m, cg, CombineCabFrom, CombineCabTo, CombineFloor - 0.2f, CombineFloor, cabW + 0.3f, look.Lower);
        Along(m, cg, CombineCabFrom + 0.05f, CombineCabTo - 0.05f, CombineFloor, CombineFloor + 0.01f, inner * 2f, HeavyCabin.FloorColour);
        Along(m, cg, CombineCabFrom - 0.1f, CombineCabTo + 0.05f, CombineRoof - 0.15f, CombineRoof, cabW + 0.2f, look.Accent);
        Along(m, cg, CombineCabFrom, CombineCabTo, CombineRoof - 0.16f, CombineRoof - 0.15f, inner * 2f, HeavyCabin.Lining);
        foreach (float sx in new[] { -1f, 1f })
            foreach (float at in new[] { CombinePane, CombineCabTo - 0.05f })
                Along(m, cg, at - 0.05f, at + 0.05f, CombineFloor, CombineRoof - 0.15f, 0.07f, look.Lower, sx * (cabW * 0.5f - 0.035f));
        FrontPane(m, Z(CombinePane), inner, CombineFloor + 0.05f, CombineRoof - 0.17f);
        Along(m, cg, CombineCabTo - 0.08f, CombineCabTo, CombineFloor, CombineRoof - 0.15f, cabW, look.Paint);
        // the right side is fixed glass; the left is the door
        SidePane(m, -cabW * 0.5f, Z(CombinePane + 0.06f), Z(CombineCabTo - 0.1f), CombineFloor + 0.05f, CombineRoof - 0.18f);
        var leaves = new List<HeavyDoorLeaf>();
        {
            float at0 = CombinePane + 0.06f, at1 = CombineCabTo - 0.1f, x = cabW * 0.5f;
            var leaf = new MeshScratch();
            Along(leaf, cg, at0, at1, CombineFloor, CombineFloor + 0.06f, 0.05f, look.Lower, x);
            Along(leaf, cg, at0, at1, CombineRoof - 0.22f, CombineRoof - 0.16f, 0.05f, look.Lower, x);
            Along(leaf, cg, at1 - 0.05f, at1, CombineFloor, CombineRoof - 0.16f, 0.05f, look.Lower, x);
            SidePane(leaf, x, Z(at0), Z(at1 - 0.05f), CombineFloor + 0.06f, CombineRoof - 0.22f);
            var pivot = new Vector3(x, 0f, Z(at1));
            // hinged at its back edge, opening forward onto the platform
            leaves.Add(new HeavyDoorLeaf(DoorIndex(true), leaf.Build(pivot), new Vector3(-pivot.X, 0f, -pivot.Z), 1.2f)
            {
                Centre = new Vector3(-x, (CombineFloor + CombineRoof) * 0.5f, -Z((at0 + at1) * 0.5f)),
            });
        }
        // the platform and the ladder down the left, ahead of the drive wheel
        Along(m, cg, CombineCabFrom, CombineCabTo, CombineFloor - 0.08f, CombineFloor, 0.5f, Trim, cabW * 0.5f + 0.25f);
        for (float y = 0.45f; y < CombineFloor - 0.1f; y += 0.3f)
            Along(m, cg, front - rf - 0.45f, front - rf - 0.1f, y, y + 0.04f, 0.4f, Trim, hw - 0.1f);
        foreach (float sx in new[] { -1f, 1f })
        {
            Lamp(head, sx * 0.75f, CombineRoof - 0.07f, Z(CombineCabFrom - 0.12f), 0.2f, 0.08f, 0.04f, HeadLamp);
            Lamp(head, sx * 0.4f, 1.5f, Z(HeaderTo + 0.2f), 0.15f, 0.1f, 0.04f, HeadLamp);
        }
        var mirrors = new List<(string, Vector3, Vector2)>();
        foreach (float sx in new[] { -1f, 1f })
        {
            m.Tube(new Vector3(sx * cabW * 0.5f, 3.3f, Z(CombinePane)), new Vector3(sx * (cabW * 0.5f + 0.5f), 3.35f, Z(CombinePane + 0.05f)), 0.02f, Trim, 4);
            mirrors.Add((sx > 0 ? "MirrorLeft" : "MirrorRight", new Vector3(sx * (cabW * 0.5f + 0.55f), 3.2f, Z(CombinePane + 0.08f)), new Vector2(0.18f, 0.28f)));
        }
        var frame = new CabFrame
        {
            Front = Z(CombinePane), Floor = CombineFloor, Ceiling = CombineRoof - 0.16f, WsBase = CombineFloor + 0.05f, WsTop = CombineRoof - 0.2f,
            DashTop = CombineFloor + 0.5f, InnerHalf = inner, Nose = 0.3f, DriverX = 0f, HipRise = 0.48f, Recline = 0.2f, ColumnTilt = 0.95f,
            WheelRadius = 0.19f, DashToX = -0.25f, Clutch = false, PassengerSeat = false,
        };
        var cockpit = HeavyCabin.Build(m, frame, HeavyCabin.GaugesFor(spec.LimiterKmh, spec.Redline), HeavyDriveline.AirLow, HeavyDriveline.AirMax, mirrors);
        var hip = cockpit.Seat.Hip;
        var buddy = new Vector3(-inner + 0.3f, CombineFloor + 0.45f, hip.Z + 0.2f);
        CarMeshBuilder.Bucket(m, buddy, 0.15f, CombineFloor, HeavyCabin.DriverSeatColour);
        Along(m, cg, cg - hip.Z - 0.4f, cg - hip.Z + 0.25f, CombineFloor, hip.Y + 0.1f, 0.22f, HeavyCabin.Dash, -0.38f);

        // ---- the heap in the tank, and the auger folded back along the left ----
        var heap = Heap(s.Width - 0.6f, tank1 - tank0 - 0.3f, tankTop - tankFloor + 0.35f);
        var auger = new MeshScratch();
        auger.Tube(new Vector3(0, 0, 0), new Vector3(0, 0.1f, -AugerLength), 0.17f, look.Paint, 8);
        auger.Tube(new Vector3(0, 0.1f, -AugerLength), new Vector3(0, -0.45f, -AugerLength - 0.15f), 0.14f, look.Lower, 6);
        auger.Box(new Vector3(0, -0.35f, -0.1f), new Vector3(0.4f, 0.5f, 0.4f), look.Lower);
        // authored at the hinge pointing back; the node turns it out to the left
        var hinge = new Vector3(AugerX, AugerY, Z(AugerAt));

        return new HeavyParts(m.Build(), head.Build(), tail.Build(), rev.Build(), Wheels(s, cg), leaves.ToArray())
        {
            Cockpit = cockpit,
            Seats = new[]
            {
                new SeatAnchor(0, CarMeshBuilder.Turned(hip), cockpit.Seat.Recline, CombineFloor),
                new SeatAnchor(0, CarMeshBuilder.Turned(buddy), 0.15f, CombineFloor),
            },
            CarDoors = true,
            Lift = h.Build(), LiftRaise = HeaderRaise,
            Reel = (reel.Build(), new Vector3(0, 1.35f, -Z(0.75f))),
            Heap = (heap, new Vector3(0, tankFloor + 0.02f, -Z((tank0 + tank1) * 0.5f))),
            Fill = spec.TankItems > 0 ? load : 0f,
            Auger = (auger.Build(), new Vector3(-hinge.X, hinge.Y, -hinge.Z), -Mathf.Pi * 0.5f),
        };
    }

    /// <summary>
    /// The auger's spout when it is out, in the combine's node space: where the grain falls (#494),
    /// beside the combine's left by the length of the tube.
    /// </summary>
    public static Vector3 AugerSpout(float cg) => new(-(AugerX + AugerLength), AugerY - 0.5f, -(cg - AugerAt));

    /// <summary>A heap of grain, its base at the origin: a full box and a ridge above it, grown by the rig's scale.</summary>
    private static ArrayMesh Heap(float width, float length, float height)
    {
        var m = new MeshScratch();
        m.Box(new Vector3(0, height * 0.35f, 0), new Vector3(width, height * 0.7f, length), Grain);
        m.Box(new Vector3(0, height * 0.82f, 0), new Vector3(width * 0.6f, height * 0.36f, length * 0.75f), Grain);
        return m.Build();
    }

    // ---- the implements and the tipping trailer -------------------------------------------------

    public static HeavyParts Implement(TrailerSpec spec, int section, float load)
    {
        var s = spec.Sections[section];
        // the tipping trailer's dolly is any drawbar trailer's
        if (s.Pivot == Coupling.Drawbar) return TrailerMeshBuilder.Build(spec, section, load);
        float cg = Cg(s, load);
        float Z(float at) => cg - at;
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var rev = new MeshScratch();
        var lift = new MeshScratch();
        float hw = s.Width * 0.5f;

        if (spec.Mounted)
        {
            // the headstock: the lower-link pins and the top link's bracket, at the linkage
            lift.Box(new Vector3(0, 0.75f, Z(0.12f)), new Vector3(0.9f, 0.12f, 0.2f), spec.Frame);
            foreach (float sx in new[] { -1f, 1f })
                lift.Tube(new Vector3(sx * 0.42f, 0.55f, Z(0f)), new Vector3(sx * 0.3f, 1.25f, Z(0.2f)), 0.05f, spec.Frame, 5);
            lift.Box(new Vector3(0, 1.2f, Z(0.18f)), new Vector3(0.18f, 0.25f, 0.18f), spec.Frame);
        }

        switch (spec.Body)
        {
            case TrailerBody.Plough:
            {
                // the beam back and across, four bodies under it and four over it (reversible)
                float beam0 = 0.4f, beam1 = s.Length - 0.1f;
                lift.Tube(new Vector3(0, 0.9f, Z(0.25f)), new Vector3(0, 0.9f, Z(beam0)), 0.07f, spec.Frame, 5);
                lift.Tube(new Vector3(0.25f, 0.9f, Z(beam0)), new Vector3(-hw + 0.2f, 0.9f, Z(beam1)), 0.08f, spec.Paint, 6);
                for (int i = 0; i < 4; i++)
                {
                    float t = (i + 0.5f) / 4f;
                    var at = new Vector3(Mathf.Lerp(0.25f, -hw + 0.2f, t), 0.9f, Z(Mathf.Lerp(beam0, beam1, t)));
                    foreach (float up in new[] { -1f, 1f })
                    {
                        // the leg and its mouldboard, turned to throw the soil to one side
                        lift.Tube(at, at + new Vector3(0, up * 0.5f, 0), 0.04f, spec.Paint, 4);
                        lift.Box(at + new Vector3(-0.1f, up * 0.68f, -0.05f), new Vector3(0.38f, 0.3f, 0.55f), Share,
                            new Basis(Vector3.Up, 0.45f) * new Basis(Vector3.Forward, -up * 0.35f));
                    }
                }
                // the depth wheel at the back
                lift.Tube(new Vector3(-hw + 0.05f, 0.3f, Z(beam1 - 0.2f)), new Vector3(-hw + 0.25f, 0.3f, Z(beam1 - 0.2f)), 0.3f, Rubber, 10);
                break;
            }
            case TrailerBody.SeedDrill:
            {
                // the hopper on its frame, the coulters along the bar, the packer roller behind
                lift.Box(new Vector3(0, 1.25f, Z(0.8f)), new Vector3(s.Width - 0.1f, 0.6f, 0.9f), spec.Paint);
                lift.Box(new Vector3(0, 1.57f, Z(0.8f)), new Vector3(s.Width - 0.05f, 0.05f, 0.95f), spec.Accent);
                lift.Box(new Vector3(0, 0.75f, Z(0.8f)), new Vector3(s.Width - 0.2f, 0.12f, 0.12f), spec.Frame);
                for (int i = 0; i < 12; i++)
                {
                    float x = -hw + 0.15f + i * (s.Width - 0.3f) / 11f;
                    lift.Tube(new Vector3(x, 0.75f, Z(1.0f)), new Vector3(x, 0.08f, Z(spec.WorkAt)), 0.025f, Steel, 4);
                }
                lift.Tube(new Vector3(-hw + 0.05f, 0.22f, Z(s.Length - 0.25f)), new Vector3(hw - 0.05f, 0.22f, Z(s.Length - 0.25f)), 0.22f, Steel, 10);
                foreach (float sx in new[] { -1f, 1f })
                    lift.Tube(new Vector3(sx * (hw - 0.1f), 0.75f, Z(0.9f)), new Vector3(sx * (hw - 0.1f), 0.22f, Z(s.Length - 0.25f)), 0.04f, spec.Frame, 4);
                break;
            }
            case TrailerBody.Mower:
            {
                // the cutter bed across, its discs, the guard over them, out to the right on the arm
                // from the headstock (spec.WorkOffset)
                float ox = spec.WorkOffset;
                lift.Box(new Vector3(ox, 0.08f, Z(spec.WorkAt)), new Vector3(s.Width, 0.12f, 0.5f), Steel);
                for (int i = 0; i < 7; i++)
                    lift.Tube(new Vector3(ox - hw + 0.25f + i * (s.Width - 0.5f) / 6f, 0.14f, Z(spec.WorkAt)),
                        new Vector3(ox - hw + 0.25f + i * (s.Width - 0.5f) / 6f, 0.2f, Z(spec.WorkAt)), 0.18f, Trim, 8);
                lift.Box(new Vector3(ox, 0.45f, Z(spec.WorkAt)), new Vector3(s.Width - 0.05f, 0.5f, 0.75f), spec.Paint);
                lift.Box(new Vector3(ox, 0.72f, Z(spec.WorkAt)), new Vector3(s.Width - 0.2f, 0.05f, 0.6f), spec.Accent);
                lift.Tube(new Vector3(0, 0.75f, Z(0.15f)), new Vector3(ox - hw + 0.4f, 0.7f, Z(spec.WorkAt - 0.3f)), 0.06f, spec.Frame, 5);
                break;
            }
            case TrailerBody.Tipper:
            {
                // the frame on its axle, the bin, the heap in it, the tipping ram at the front
                const float floor = 1.1f;
                foreach (float sx in new[] { -1f, 1f })
                    Along(m, cg, 0.0f, s.Length - 0.2f, floor - 0.25f, floor, 0.14f, spec.Frame, sx * 0.45f);
                Along(m, cg, 0.5f, 1.1f, floor - 0.12f, floor, s.Width - 0.3f, spec.Frame);
                Along(m, cg, 0f, s.Length, floor, floor + 0.08f, s.Width, spec.Paint);
                foreach (float sx in new[] { -1f, 1f })
                    Along(m, cg, 0f, s.Length, floor + 0.08f, s.Height, 0.06f, spec.Paint, sx * (hw - 0.03f));
                Along(m, cg, 0f, 0.06f, floor + 0.08f, s.Height + 0.25f, s.Width, spec.Paint);
                Along(m, cg, s.Length - 0.06f, s.Length, floor + 0.08f, s.Height, s.Width, spec.Paint);
                // the top rails and the steps of the ladder at the front corner
                Sides(m, cg, 0f, s.Length, s.Height - 0.05f, s.Height, s.Width, spec.Accent);
                for (float y = 0.5f; y < s.Height; y += 0.35f)
                    Along(m, cg, -0.05f, 0.0f, y, y + 0.04f, 0.35f, Steel, hw - 0.3f);
                var a = s.Axles[0];
                float r = Tyre.Radius(a.Tyre);
                Along(m, cg, a.At - r - 0.1f, a.At + r + 0.1f, 2f * r + 0.03f, 2f * r + 0.09f, s.Width, Trim);
                m.Tube(new Vector3(-0.9f, r, Z(a.At)), new Vector3(0.9f, r, Z(a.At)), 0.07f, Trim, 6);
                foreach (float sx in new[] { -1f, 1f })
                {
                    Lamp(tail, sx * (hw - 0.25f), 0.85f, Z(s.Length + 0.02f), 0.3f, 0.14f, 0.04f, TailLamp);
                    Lamp(rev, sx * (hw - 0.55f), 0.85f, Z(s.Length + 0.02f), 0.12f, 0.12f, 0.04f, White);
                }
                Along(m, cg, s.Length - 0.1f, s.Length, 0.7f, 0.95f, s.Width - 0.3f, Steel);
                var heap = Heap(s.Width - 0.15f, s.Length - 0.15f, s.Height - floor - 0.08f + 0.3f);
                return new HeavyParts(m.Build(), head.Build(), tail.Build(), rev.Build(), Wheels(s, cg), System.Array.Empty<HeavyDoorLeaf>())
                {
                    Heap = (heap, new Vector3(0, floor + 0.08f, -Z(s.Length * 0.5f))),
                    Fill = load,
                };
            }
        }
        // the road lamps on a board across the back of a mounted implement
        float back = s.Length;
        lift.Box(new Vector3(0, 1.0f, Z(back - 0.05f)), new Vector3(Mathf.Min(s.Width, 1.8f), 0.04f, 0.04f), Steel);
        foreach (float sx in new[] { -1f, 1f })
            Lamp(tail, sx * (Mathf.Min(hw, 0.9f) - 0.1f), 1.0f + spec.LiftHeight, Z(back), 0.15f, 0.1f, 0.04f, TailLamp);
        return new HeavyParts(m.Build(), head.Build(), tail.Build(), rev.Build(), Wheels(s, cg), System.Array.Empty<HeavyDoorLeaf>())
        {
            Lift = lift.Build(), LiftRaise = spec.LiftHeight,
        };
    }
}
