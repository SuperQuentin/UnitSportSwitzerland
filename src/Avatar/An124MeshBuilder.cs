using Godot;
using static UnitSport.Avatar.An124Layout;

namespace UnitSport.Avatar;

/// <summary>
/// The Antonov AN-124 Ruslan (#419), built from code like the military freighter
/// (<see cref="FreighterMeshBuilder"/>): low-poly, flat-shaded, one <c>Body</c> mesh plus hinge nodes,
/// every number from <see cref="An124Layout"/>. The fuselage is a hollow ring of thin slabs with real
/// holes (the crew door, the visor and the nose ramp, the rear ramp, the rear door and the side
/// petals, the windows); inside are the drive-through hold, the ladder, the upper deck and the cockpit.
///
/// <para>
/// Moving parts: <c>FlapIn/FlapOut/Aileron/Elevator L|R</c>, <c>Rudder</c>, <c>GearMainL/R</c> (five
/// twin-wheel legs a side rising into the fairings), <c>GearNose</c>, <c>Fan0..3</c>; the doors'
/// nodes in <see cref="DoorMotions"/>. Authored +Z forward, +X the left side; <see cref="Build"/>
/// returns NODE space (−Z forward, origin on the ground between the main gear, standing).
/// </para>
/// </summary>
public static class An124MeshBuilder
{
    private static readonly Color Hull = new(0.86f, 0.87f, 0.88f);
    private static readonly Color Belly = new(0.58f, 0.60f, 0.63f);
    private static readonly Color Stripe = new(0.12f, 0.25f, 0.62f);
    private static readonly Color Radome = new(0.30f, 0.32f, 0.34f);
    private static readonly Color Edge = new(0.70f, 0.72f, 0.74f);
    private static readonly Color Dark = new(0.07f, 0.07f, 0.08f);
    private static readonly Color Metal = new(0.40f, 0.41f, 0.43f);
    private static readonly Color Lining = new(0.62f, 0.64f, 0.60f);
    private static readonly Color Rib = new(0.38f, 0.40f, 0.38f);
    private static readonly Color Floor = new(0.24f, 0.25f, 0.24f);
    private static readonly Color Track = new(0.58f, 0.59f, 0.60f);
    private static readonly Color Lamp = new(1.0f, 0.95f, 0.75f);
    private static readonly Color Panel = new(0.17f, 0.18f, 0.20f);
    private static readonly Color Screen = new(0.04f, 0.08f, 0.06f);
    private static readonly Color Seat = new(0.20f, 0.28f, 0.45f);
    private static readonly Color Tyre = new(0.06f, 0.06f, 0.06f);
    private static readonly Color Yellow = new(0.95f, 0.78f, 0.10f);
    private static readonly Color Pane = new(0.40f, 0.52f, 0.58f, 0.35f);

    private static Vector3 Flip(Vector3 v) => AircraftMeshBuilder.Flip(v);

    // ---- the public contract --------------------------------------------------------------

    public const float GearLift = MainGearLift;
    public const float NoseStow = NoseStowAngle;

    /// <summary>
    /// What opening door <paramref name="i"/> moves: hinge nodes, each turned about a local axis by an
    /// angle (fully open), and how much kneeling takes back off it (a ramp's toes rest on the ground
    /// 0.85 m nearer, so it opens less). The visor swings up; the nose ramp's three plates unfold from
    /// standing behind it to a slope to the ground; the rear ramp's lip goes down, its toes unfold,
    /// the side petals swing out and the rear door swings up into the tail.
    /// </summary>
    public static (string Node, Vector3 Axis, float Angle, float Kneel)[] DoorMotions(int i) => i switch
    {
        CrewDoor => new[] { ("Door0", Vector3.Up, -2.4f, 0f) },
        NoseDoor => new[]
        {
            ("Door1", Vector3.Right, VisorOpen, 0f),
            ("Door1r", Vector3.Right, -(Mathf.Pi * 0.5f + OpenSlope(false)), RampKneelBack),
            ("Door1r/Toe", Vector3.Right, Mathf.Pi, 0f),
            ("Door1r/Toe/Toe2", Vector3.Right, -Mathf.Pi, 0f),
        },
        RearDoor => new[]
        {
            ("Door2", Vector3.Right, RampTravel, -RampKneelBack),
            ("Door2/Toes", Vector3.Right, -Mathf.Pi, 0f),
            // the petals fold up and in about their top edges, inside the tail's outline
            ("Door2L", PetalAxis(1), 1.7f, 0f),
            ("Door2R", PetalAxis(-1), -1.7f, 0f),
            ("Door2b", Vector3.Right, RearDoorOpen, 0f),
        },
        _ => System.Array.Empty<(string, Vector3, float, float)>(),
    };

    /// <summary>How fast door <paramref name="i"/> travels, fraction of its stroke per second (the visor, ramps and kneeling are slow).</summary>
    public static float DoorRate(int i) => i == CrewDoor ? 0.5f : 0.13f;

    public static Node3D Build()
    {
        var bodyMat = HumanMeshBuilder.FigureMaterial();
        var glassMat = CarRig.GlassMaterial();
        var root = new Node3D { Name = "An124" };

        var b = new MeshScratch();
        Fuselage(b);
        Hold(b);
        Upper(b);
        Wings(b);
        Engines(b);
        Tail(b);
        Fairings(b);
        // the door buttons where the walk has them, inside and out
        foreach (var button in An124Deck.Deck.Buttons)
            b.Box(Flip(button.At) - Flip(button.Normal) * 0.02f, new Vector3(0.12f, 0.12f, 0.12f), Yellow);
        var body = new MeshInstance3D { Name = "Body", Mesh = b.Build() };
        root.AddChild(body);
        MeshScratch.Paint(body, bodyMat, glassMat);

        foreach (int sg in new[] { 1, -1 })
        {
            string s = sg > 0 ? "L" : "R";
            root.AddChild(Flap($"FlapIn{s}", sg, InnerFlapFrom, InnerFlapTo, bodyMat, glassMat));
            root.AddChild(Flap($"FlapOut{s}", sg, OuterFlapFrom, OuterFlapTo, bodyMat, glassMat));
            root.AddChild(Flap($"Aileron{s}", sg, AileronFrom, AileronTo, bodyMat, glassMat));
            root.AddChild(Elevator($"Elevator{s}", sg, bodyMat, glassMat));
            root.AddChild(MainGear($"GearMain{s}", sg, bodyMat, glassMat));
            root.AddChild(Petal(sg, bodyMat, glassMat));
        }
        root.AddChild(Rudder(bodyMat, glassMat));
        root.AddChild(NoseGear(bodyMat, glassMat));
        for (int i = 0; i < EngineX.Length; i++) root.AddChild(Fan(i, bodyMat));
        root.AddChild(CrewDoorLeaf(bodyMat, glassMat));
        root.AddChild(Visor(bodyMat, glassMat));
        root.AddChild(NoseRamp(bodyMat, glassMat));
        root.AddChild(RearRamp(bodyMat, glassMat));
        root.AddChild(RearDoorLeaf(bodyMat, glassMat));
        AddLights(root);
        return root;
    }

    // ---- the fuselage ---------------------------------------------------------------------

    /// <summary>
    /// The skin's rows, heights belly to crown aft of the hump (over the hump the rows above the widest
    /// line stretch with it): the belly's under the floor (the ramp's), the crew door's, the widest line,
    /// the hold ceiling, a cheat line, the windows (8.2..8.9 m over the upper deck), the crown.
    /// </summary>
    private static readonly float[] Levels = { 2.4f, 2.5f, 2.85f, 3.3f, 4.55f, 5.25f, 5.6f, 6.4f, 7.5f, 7.7f, 7.79f, 8.38f, 9.0f, 9.5f, 9.8f, 9.9f };
    private static readonly float[] H = System.Array.ConvertAll(Levels, y => Mathf.Clamp(BarrelH(y), -1f, 1f));
    private static int Row(float y) => System.Array.IndexOf(Levels, y);
    /// <summary>The floor's row (the rows under it are the belly's: the ramp and the rear door), the crew door's top, the hold ceiling's (the visor's and the petals' tops), the cheat line, the windows.</summary>
    private static readonly int FloorRow = Row(FloorY), DoorTopRow = Row(5.25f), CeilingRow = Row(HoldCeilingY);
    private static readonly int StripeRow = Row(7.5f), WindowRow = Row(7.79f), CockpitTopRow = Row(9.0f);
    private const float PetalRearZ = -22f;

    private static Vector3 Po(float z, float h, int sg)
    {
        var s = Section(z);
        return new Vector3(sg * Across(s.A, h), s.Y(h), z);
    }

    private static Vector3 Pi(float z, float h, int sg)
    {
        var s = Section(z);
        return new Vector3(sg * Across(Mathf.Max(s.A - An124Layout.Skin, 0.03f), h), s.Y(h, An124Layout.Skin), z);
    }

    private static readonly float[] Stations = MakeStations();

    private static float[] MakeStations()
    {
        var l = new List<float> { BarrelRear, BarrelFront, RampClosedEndZ, RearDoorHingeZ, PetalRearZ, -26f, NoseHingeZ, VisorCapZ };
        for (int i = 0; i <= 14; i++) l.Add(Mathf.Lerp(BarrelRear, TailZ, i / 14f));
        foreach (float t in new[] { 0f, .1f, .2f, .3f, .4f, .5f, .55f, .6f, .7f, .8f, .88f, .94f, .98f, 1f }) l.Add(BarrelFront + (NoseZ - BarrelFront) * t);
        for (float z = BarrelRear; z < BarrelFront; z += 2.5f) l.Add(z);
        for (int i = 0; i <= 4; i++) l.Add(Mathf.Lerp(HumpFrom, HumpFull, i / 4f));
        l.Sort();
        var r = new List<float>();
        foreach (float z in l) if (r.Count == 0 || z - r[^1] > 0.01f) r.Add(z);
        return r.ToArray();
    }

    private static List<float> Zs(float a, float b)
    {
        var r = new List<float> { a };
        foreach (float z in Stations) if (z > a + 0.01f && z < b - 0.01f) r.Add(z);
        r.Add(b);
        return r;
    }

    private readonly record struct Hole(float From, float To, bool Pane);

    private static List<Hole> HolesFor(int sg, int k)
    {
        var holes = new List<Hole>();
        if (sg > 0 && k >= FloorRow && k < DoorTopRow) holes.Add(new Hole(CrewDoorZ - DoorWidth * 0.5f, CrewDoorZ + DoorWidth * 0.5f, false));
        // the visor, the ramp and the rear door, the petals: their own nodes
        holes.Add(new Hole(k < CeilingRow ? NoseHingeZ : VisorCapZ, NoseZ + 1f, false));
        if (k < FloorRow) holes.Add(new Hole(RearDoorHingeZ, RampHingeZ, false));
        if (k >= FloorRow && k < CeilingRow) holes.Add(new Hole(PetalRearZ, RampHingeZ, false));
        // the upper deck's windows; the cockpit's side windows and windscreen
        if (k == WindowRow) for (int i = 0; i < UpperWindows; i++) { float z = UpperWindowZ(i); holes.Add(new Hole(z - UpperWindowHalf, z + UpperWindowHalf, true)); }
        if (k >= WindowRow && k <= CockpitTopRow)
            foreach (var (f, t) in new[] { (24.3f, 25.2f), (25.4f, 26.3f), (26.5f, 27.4f), (27.5f, 28.25f) }) holes.Add(new Hole(f, t, true));
        holes.Sort((x, y) => x.From.CompareTo(y.From));
        return holes;
    }

    private static Color RowColour(int k) => k < FloorRow ? Belly : k == StripeRow ? Stripe : Hull;

    /// <summary>The upper deck's windows along each side.</summary>
    private const int UpperWindows = 8;
    private const float UpperWindowHalf = 0.2f;
    private static float UpperWindowZ(int i) => 11.6f + i * 1.4f;

    private static void Fuselage(MeshScratch m)
    {
        foreach (int sg in new[] { 1, -1 })
            for (int k = 0; k < Levels.Length - 1; k++)
            {
                float z = TailZ;
                foreach (var h in HolesFor(sg, k))
                {
                    if (h.From > z) Slab(m, sg, k, z, Mathf.Min(h.From, NoseZ), RowColour(k));
                    z = Mathf.Max(z, h.To);
                    if (h.Pane) PaneRow(m, sg, k, h.From, h.To);
                }
                if (z < NoseZ) Slab(m, sg, k, z, NoseZ, RowColour(k));
            }
        var tail = Section(TailZ);
        m.Box(new Vector3(0, (tail.Bottom + tail.Top) * 0.5f, TailZ + 0.03f), new Vector3(tail.A * 2f, tail.Top - tail.Bottom, 0.06f), Edge);
    }

    /// <summary>Row <paramref name="k"/> of one side's skin between two stations, outer face and inner.</summary>
    private static Vector3[][] Rings(int sg, int k, float z0, float z1)
    {
        var zs = Zs(z0, z1);
        var rings = new Vector3[zs.Count][];
        for (int i = 0; i < zs.Count; i++)
            rings[i] = new[] { Po(zs[i], H[k], sg), Po(zs[i], H[k + 1], sg), Pi(zs[i], H[k + 1], sg), Pi(zs[i], H[k], sg) };
        return rings;
    }

    private static void Slab(MeshScratch m, int sg, int k, float z0, float z1, Color c)
    {
        if (z1 - z0 < 0.02f) return;
        m.Loft(Rings(sg, k, z0, z1), new[] { c, c, Rib, c }, c);
    }

    private static void PaneRow(MeshScratch m, int sg, int k, float z0, float z1)
    {
        var zs = Zs(z0, z1);
        for (int i = 0; i + 1 < zs.Count; i++)
        {
            Vector3 Mid(float z, float h) => (Po(z, h, sg) + Pi(z, h, sg)) * 0.5f;
            m.Pane(new[] { Mid(zs[i], H[k]), Mid(zs[i], H[k + 1]), Mid(zs[i + 1], H[k + 1]), Mid(zs[i + 1], H[k]) }, Pane);
        }
    }

    /// <summary>Rows <paramref name="k0"/>..<paramref name="k1"/> of the skin between two stations into a moving part, both sides when <paramref name="sg"/> is 0.</summary>
    private static void SkinPart(AircraftPart part, int sg, int k0, int k1, float z0, float z1)
    {
        foreach (int s in sg == 0 ? new[] { 1, -1 } : new[] { sg })
            for (int k = k0; k <= k1; k++)
            {
                var c = z0 >= VisorCapZ + 1.5f ? Radome : RowColour(k);
                part.Loft(Rings(s, k, z0, z1), new[] { c, c, Lining, c }, c);
            }
    }

    // ---- the hold, the ladder ----------------------------------------------------------------

    private const float LiningX = HoldHalfWidth + 0.03f, LiningT = 0.06f;

    private static void Hold(MeshScratch m)
    {
        float front = NoseHingeZ, rear = RearWallZ, mid = (RampHingeZ + front) * 0.5f, len = front - RampHingeZ;
        // the floor, its roller tracks and tie-down points; under it the keel to the belly
        m.Box(new Vector3(0, FloorY - 0.075f, mid), new Vector3(HoldHalfWidth * 2f + 0.1f, 0.15f, len), Floor);
        const float keel = BellyY + 0.35f;
        m.Box(new Vector3(0, (keel + FloorY - 0.15f) * 0.5f, mid), new Vector3(OuterX(mid, keel) * 2f - 0.4f, FloorY - 0.15f - keel, len), Belly);
        foreach (float x in new[] { -2.2f, -1.0f, 1.0f, 2.2f })
            m.Box(new Vector3(x, FloorY + 0.012f, mid), new Vector3(0.14f, 0.025f, len), Track);
        for (float z = RampHingeZ + 0.8f; z < front; z += 1.0f)
            foreach (float x in new[] { -1.6f, 1.6f })
                m.Box(new Vector3(x, FloorY + 0.008f, z), new Vector3(0.1f, 0.016f, 0.1f), Dark);

        // the linings (the left one with the crew door cut out), ribs, the ceiling and its lamps
        float h = HoldCeilingY - FloorY, yc = (FloorY + HoldCeilingY) * 0.5f;
        float d0 = CrewDoorZ - DoorWidth * 0.5f, d1 = CrewDoorZ + DoorWidth * 0.5f;
        m.Box(new Vector3(-(LiningX + LiningT * 0.5f), yc, (rear + front) * 0.5f), new Vector3(LiningT, h, front - rear), Lining);
        float lx = LiningX + LiningT * 0.5f;
        m.Box(new Vector3(lx, yc, (rear + d0) * 0.5f), new Vector3(LiningT, h, d0 - rear), Lining);
        m.Box(new Vector3(lx, yc, (d1 + front) * 0.5f), new Vector3(LiningT, h, front - d1), Lining);
        m.Box(new Vector3(lx, (FloorY + DoorHeight + HoldCeilingY) * 0.5f, CrewDoorZ), new Vector3(LiningT, h - DoorHeight, DoorWidth), Lining);
        float sillOut = OuterX(CrewDoorZ, FloorY) - 0.01f;
        m.Box(new Vector3((HoldHalfWidth + sillOut) * 0.5f, FloorY - 0.03f, CrewDoorZ), new Vector3(sillOut - HoldHalfWidth, 0.06f, DoorWidth), Metal);
        foreach (int sg in new[] { 1, -1 })
            for (float rz = rear + 0.4f; rz < front; rz += 1.6f)
            {
                if (sg > 0 && rz > d0 - 0.1f && rz < d1 + 0.1f) continue;
                m.Box(new Vector3(sg * (LiningX - 0.03f), yc, rz), new Vector3(0.05f, h, 0.1f), Rib);
            }
        HatchedSlab(m, HoldCeilingY, HoldCeilingY + 0.04f, rear, front, LiningX, Lining);
        for (float z = rear + 1.5f; z < front - 0.5f; z += 3.0f)
            foreach (float x in new[] { -1.4f, 1.4f })
                if (x > 0f || z < LadderFootZ - 0.1f || z > UpperRearZ + 0.1f)
                    m.Box(new Vector3(x, HoldCeilingY - 0.01f, z), new Vector3(0.5f, 0.03f, 0.2f), Lamp);
        // the gantry crane rails along the ceiling, the right one stopping either side of the hatch
        m.Box(new Vector3(2.4f, HoldCeilingY - 0.12f, (rear + front) * 0.5f), new Vector3(0.18f, 0.22f, front - rear), Metal);
        foreach (var (z0, z1) in new[] { (rear, LadderFootZ), (UpperRearZ, front) })
            m.Box(new Vector3(-2.4f, HoldCeilingY - 0.12f, (z0 + z1) * 0.5f), new Vector3(0.18f, 0.22f, z1 - z0), Metal);

        // the ladder: two stringers and the treads, a hand rail on its open side
        float run = UpperRearZ - LadderFootZ, rise = UpperFloorY - FloorY;
        foreach (float s in new[] { -1f, 1f })
            m.Tube(new Vector3(LadderX + s * LadderWidth * 0.5f, FloorY, LadderFootZ), new Vector3(LadderX + s * LadderWidth * 0.5f, UpperFloorY, UpperRearZ), 0.04f, 0.04f, Metal, 5);
        for (int i = 1; i < 16; i++)
        {
            float t = i / 16f;
            m.Box(new Vector3(LadderX, FloorY + rise * t, LadderFootZ + run * t), new Vector3(LadderWidth, 0.04f, 0.22f), Track);
        }
        float railX = LadderX + LadderWidth * 0.5f + 0.05f;
        m.Tube(new Vector3(railX, FloorY + 0.9f, LadderFootZ), new Vector3(railX, UpperFloorY + 0.9f, UpperRearZ), 0.03f, 0.03f, Yellow, 5);
        m.Tube(new Vector3(railX, UpperFloorY, LadderFootZ), new Vector3(railX, UpperFloorY + 1.0f, LadderFootZ), 0.03f, 0.03f, Yellow, 5);
        m.Box(new Vector3(railX, UpperFloorY + 1.0f, (LadderFootZ + UpperRearZ) * 0.5f), new Vector3(0.05f, 0.05f, run), Yellow);
    }

    /// <summary>
    /// A slab across the hold between two heights and stations, with the hatch the ladder climbs through
    /// cut out (the walk's hole, <see cref="An124Deck"/>): whole, it capped the ladder (#547).
    /// </summary>
    private static void HatchedSlab(MeshScratch m, float y0, float y1, float z0, float z1, float halfX, Color c)
    {
        float h0 = Mathf.Max(z0, LadderFootZ), h1 = Mathf.Min(z1, UpperRearZ);
        float xIn = LadderX + LadderWidth * 0.5f, xOut = LadderX - LadderWidth * 0.5f, yc = (y0 + y1) * 0.5f, t = y1 - y0;
        void Piece(float xa, float xb, float za, float zb)
        {
            if (xb - xa > 0.01f && zb - za > 0.01f) m.Box(new Vector3((xa + xb) * 0.5f, yc, (za + zb) * 0.5f), new Vector3(xb - xa, t, zb - za), c);
        }
        if (h1 <= h0) { Piece(-halfX, halfX, z0, z1); return; }
        Piece(-halfX, halfX, z0, h0);
        Piece(-halfX, halfX, h1, z1);
        Piece(xIn, halfX, h0, h1);
        Piece(-halfX, xOut, h0, h1);
    }

    // ---- the upper deck and the cockpit ------------------------------------------------------

    private static void Upper(MeshScratch m)
    {
        float rear = LadderFootZ - 0.1f, front = CabinFrontZ;
        // its floor is the hold's ceiling (drawn there); on it a carpet, round the ladder's hole
        m.Box(new Vector3(0, UpperFloorY - 0.01f, (UpperRearZ + CockpitFloorFrontZ) * 0.5f), new Vector3(UpperWallX * 2f, 0.04f, CockpitFloorFrontZ - UpperRearZ), Seat.Darkened(0.4f));
        HatchedSlab(m, HoldCeilingY + 0.02f, UpperFloorY - 0.02f, rear, CockpitFloorFrontZ, UpperWallX, Lining);
        float wy = (UpperFloorY + UpperCeilingY) * 0.5f, wh = UpperCeilingY - UpperFloorY;
        // the side linings, up to where the ceiling's coves lean in, with the windows cut through and lined
        // out to the skin's panes (behind a plain lining they were hidden, #491)
        float wallTop = UpperCeilingY - 0.5f, coveX = UpperWallX - 0.6f;
        float win0 = Po(UpperWindowZ(0), H[WindowRow], 1).Y, win1 = Po(UpperWindowZ(0), H[WindowRow + 1], 1).Y;
        foreach (int sg in new[] { 1, -1 })
        {
            float lx = sg * (UpperWallX + 0.03f), len = front - rear, zc = (rear + front) * 0.5f;
            m.Box(new Vector3(lx, (UpperFloorY + win0) * 0.5f, zc), new Vector3(0.06f, win0 - UpperFloorY, len), Lining);
            m.Box(new Vector3(lx, (win1 + wallTop) * 0.5f, zc), new Vector3(0.06f, wallTop - win1, len), Lining);
            float z = rear;
            for (int i = 0; i <= UpperWindows; i++)
            {
                float next = i < UpperWindows ? UpperWindowZ(i) - UpperWindowHalf : front;
                m.Box(new Vector3(lx, (win0 + win1) * 0.5f, (z + next) * 0.5f), new Vector3(0.06f, win1 - win0, next - z), Lining);
                if (i == UpperWindows) break;
                z = UpperWindowZ(i) + UpperWindowHalf;
                // the window's reveal: sill, head and jambs from the lining to the skin
                float wz = UpperWindowZ(i), inner = UpperWallX + 0.06f, skin = OuterX(wz, (win0 + win1) * 0.5f) - An124Layout.Skin;
                float rx = sg * (inner + skin) * 0.5f, depth = skin - inner;
                m.Box(new Vector3(rx, win0, wz), new Vector3(depth, 0.04f, UpperWindowHalf * 2f), Lining.Lightened(0.2f));
                m.Box(new Vector3(rx, win1, wz), new Vector3(depth, 0.04f, UpperWindowHalf * 2f), Lining.Lightened(0.2f));
                foreach (float e in new[] { -1f, 1f })
                    m.Box(new Vector3(rx, (win0 + win1) * 0.5f, wz + e * UpperWindowHalf), new Vector3(depth, win1 - win0, 0.03f), Lining.Lightened(0.2f));
            }
            // the cove from the wall's top in to the ceiling
            float run = UpperWallX + 0.03f - coveX, rise = UpperCeilingY - wallTop;
            m.Box(new Vector3(sg * (coveX + run * 0.5f), wallTop + rise * 0.5f, zc), new Vector3(Mathf.Sqrt(run * run + rise * rise), 0.06f, len), Lining,
                new Basis(Vector3.Back, -sg * Mathf.Atan2(rise, run)));
        }
        m.Box(new Vector3(0, wy, rear - 0.05f), new Vector3(UpperWallX * 2f, wh, 0.1f), Lining);
        m.Box(new Vector3(0, UpperCeilingY + 0.03f, (rear + CockpitFloorFrontZ) * 0.5f), new Vector3(coveX * 2f, 0.06f, CockpitFloorFrontZ - rear), Lining);
        for (float z = rear + 1.0f; z < front; z += 2.0f) m.Box(new Vector3(0, UpperCeilingY - 0.01f, z), new Vector3(0.6f, 0.03f, 0.2f), Lamp);
        // the cabin's seats, two pairs a row
        for (int row = 0; row < CabinRows; row++)
            foreach (float x in CabinSeatX)
            {
                float z = RowZ(row);
                var recline = new Basis(Vector3.Right, -0.15f);
                m.Box(new Vector3(x, UpperFloorY + 0.42f, z + 0.02f), new Vector3(0.5f, 0.14f, 0.48f), Seat.Lightened(0.12f));
                m.Box(new Vector3(x, UpperFloorY + 0.82f, z - 0.24f), new Vector3(0.52f, 0.72f, 0.12f), Seat, recline);
                m.Box(new Vector3(x, UpperFloorY + 1.12f, z - 0.27f), new Vector3(0.36f, 0.16f, 0.13f), Lamp, recline);
                foreach (float e in new[] { -1f, 1f })
                    m.Box(new Vector3(x + e * 0.28f, UpperFloorY + 0.6f, z - 0.02f), new Vector3(0.05f, 0.05f, 0.42f), Dark);
                m.Box(new Vector3(x, UpperFloorY + 0.17f, z), new Vector3(0.4f, 0.34f, 0.3f), Dark);
            }
        // the cockpit's wall with its doorway
        foreach (int sg in new[] { 1, -1 })
            m.Box(new Vector3(sg * (UpperWallX + 0.45f) * 0.5f, wy, front + 0.05f), new Vector3(UpperWallX - 0.45f, wh, 0.1f), Lining);
        m.Box(new Vector3(0, UpperFloorY + 2.05f + (UpperCeilingY - UpperFloorY - 2.05f) * 0.5f, front + 0.05f), new Vector3(0.9f, Mathf.Max(0.02f, UpperCeilingY - UpperFloorY - 2.05f), 0.1f), Lining);

        // the cockpit: three seats, yokes, the pedestal, the panel and glareshield, the engineer's panel
        foreach (var hip in new[] { PilotHip, CopilotHip, EngineerHip })
        {
            bool eng = hip == EngineerHip;
            var turn = eng ? new Basis(Vector3.Up, -Mathf.Pi * 0.5f) : Basis.Identity;
            m.Box(new Vector3(hip.X, UpperFloorY + 0.14f, hip.Z), new Vector3(0.24f, 0.28f, 0.3f), Dark);
            m.Box(new Vector3(hip.X, hip.Y - 0.1f, hip.Z), new Vector3(0.52f, 0.12f, 0.5f), Seat, turn);
            var back = eng ? new Vector3(0.25f, 0, 0) : new Vector3(0, 0, -0.25f);
            m.Box(new Vector3(hip.X, hip.Y + 0.35f, hip.Z) + back, eng ? new Vector3(0.12f, 0.85f, 0.52f) : new Vector3(0.52f, 0.85f, 0.12f), Seat);
            if (eng) continue;
            m.Tube(new Vector3(hip.X, UpperFloorY, hip.Z + 0.75f), new Vector3(hip.X, hip.Y + 0.35f, hip.Z + 0.55f), 0.035f, 0.03f, Dark, 5);
            m.Box(new Vector3(hip.X, hip.Y + 0.38f, hip.Z + 0.53f), new Vector3(0.36f, 0.05f, 0.05f), Dark);
        }
        m.Box(new Vector3(0, UpperFloorY + 0.3f, PilotHip.Z + 0.5f), new Vector3(0.4f, 0.6f, 0.9f), Panel);
        for (int i = 0; i < 4; i++)
            m.Tube(new Vector3(-0.12f + i * 0.08f, UpperFloorY + 0.6f, PilotHip.Z + 0.35f), new Vector3(-0.12f + i * 0.08f, UpperFloorY + 0.8f, PilotHip.Z + 0.5f), 0.015f, 0.012f, Lamp, 4);
        float hw = Mathf.Max(0.8f, OuterX(PanelZ, UpperFloorY + 1.0f) - 0.35f);
        m.Box(new Vector3(0, UpperFloorY + 0.75f, PanelZ + 0.3f), new Vector3(hw * 2f, 0.7f, 0.6f), Panel);
        for (int i = 0; i < 10; i++)
            m.Box(new Vector3((i % 5 - 2) * hw * 0.36f, UpperFloorY + (i < 5 ? 0.95f : 0.68f), PanelZ - 0.005f), new Vector3(0.22f, 0.2f, 0.02f), i % 3 == 0 ? Screen : Dark);
        m.Box(new Vector3(0, UpperFloorY + 1.15f, PanelZ + 0.1f), new Vector3(hw * 2f, 0.06f, 0.45f), Dark);
        m.Box(new Vector3(0, UpperCeilingY - 0.1f, PilotHip.Z + 0.3f), new Vector3(1.0f, 0.14f, 1.2f), Panel);
        m.Box(new Vector3(-2.15f, UpperFloorY + 0.7f, EngineerHip.Z), new Vector3(0.5f, 1.4f, 1.4f), Panel);
        for (int i = 0; i < 6; i++)
            m.Box(new Vector3(-1.89f, UpperFloorY + 0.9f + (i / 3) * 0.3f, EngineerHip.Z - 0.4f + (i % 3) * 0.4f), new Vector3(0.02f, 0.2f, 0.25f), i % 2 == 0 ? Screen : Dark);
        // the cockpit's floor over the visor, and the bulkhead the visor shuts against
        float nose = OuterX(CockpitFloorFrontZ, UpperFloorY + 0.8f) - 0.3f;
        m.Box(new Vector3(0, (HoldCeilingY + UpperFloorY) * 0.5f, (NoseHingeZ + CockpitFloorFrontZ) * 0.5f), new Vector3(nose * 2f + 0.6f, UpperFloorY - HoldCeilingY, CockpitFloorFrontZ - NoseHingeZ), Lining);
        m.Box(new Vector3(0, (UpperFloorY + VisorHingeY) * 0.5f, CockpitFloorFrontZ + 0.06f), new Vector3(nose * 2f, VisorHingeY - UpperFloorY, 0.12f), Lining);
    }

    // ---- wings ---------------------------------------------------------------------------------

    private static float Thick(float x) => Mathf.Lerp(WingRootThickness, WingTipThickness, WingSpanT(x));

    private static float Prof(float f)
    {
        f = Mathf.Clamp(f, 0f, 1f);
        if (f < 0.12f) return Mathf.Lerp(0.3f, 0.8f, f / 0.12f);
        if (f < 0.3f) return Mathf.Lerp(0.8f, 1f, (f - 0.12f) / 0.18f);
        if (f < 0.72f) return Mathf.Lerp(1f, 0.55f, (f - 0.3f) / 0.42f);
        return Mathf.Lerp(0.55f, 0.08f, (f - 0.72f) / 0.28f);
    }

    private static Vector3 WPt(int sg, float x, float f, int v)
    {
        float zl = WingLeading(x), zt = WingTrailing(x), t = Thick(x), p = Prof(f);
        return new Vector3(sg * x, WingY(x) + (v > 0 ? t * 0.6f * p : v < 0 ? -t * 0.4f * p : 0f), zl + (zt - zl) * f);
    }

    private static Vector3[] WingRing(int sg, float x, float fte) => new[]
    {
        WPt(sg, x, 0f, 0), WPt(sg, x, 0.12f, 1), WPt(sg, x, 0.35f, 1), WPt(sg, x, 0.85f * fte, 1), WPt(sg, x, fte, 1),
        WPt(sg, x, fte, -1), WPt(sg, x, 0.85f * fte, -1), WPt(sg, x, 0.35f, -1), WPt(sg, x, 0.12f, -1),
    };

    private static void Wings(MeshScratch m)
    {
        var edges = new[] { Hull, Hull, Hull, Hull, Edge, Belly, Belly, Belly, Belly };
        m.Loft(new[] { WingRing(-1, WingRootX, 1f), WingRing(1, WingRootX, 1f) }, edges, Edge);
        var segments = new (float From, float To, float Fte)[]
        {
            (WingRootX, InnerFlapFrom, 1f), (InnerFlapFrom, InnerFlapTo, FlapChord), (InnerFlapTo, OuterFlapFrom, 1f),
            (OuterFlapFrom, OuterFlapTo, FlapChord), (OuterFlapTo, AileronFrom, 1f), (AileronFrom, AileronTo, FlapChord),
            (AileronTo, WingTipX, 1f),
        };
        foreach (int sg in new[] { 1, -1 })
        {
            foreach (var s in segments) m.Loft(new[] { WingRing(sg, s.From, s.Fte), WingRing(sg, s.To, s.Fte) }, edges, Edge);
            foreach (float x in new[] { 6f, 13f, 17f, 24f, 30f })
            {
                var a = WPt(sg, x, 0.55f, -1) + new Vector3(0, -0.05f, 0);
                m.Tube(a, new Vector3(sg * x, a.Y - 0.15f, WingTrailing(x) - 0.6f), 0.16f, 0.04f, Belly, 5);
            }
        }
        // the wing-to-body fairing over the roof
        m.Box(new Vector3(0, TopY + 0.15f, (WingRootLeadingZ + WingRootTrailingZ) * 0.5f), new Vector3(4.6f, 0.5f, WingRootLeadingZ - WingRootTrailingZ + 1.5f), Hull);
    }

    // ---- engines: D-18T turbofans ----------------------------------------------------------------

    private static void Engines(MeshScratch m)
    {
        for (int i = 0; i < EngineX.Length; i++)
        {
            float x = EngineX[i], y = EngineY(i), z0 = EngineFrontZ(i);
            Vector3[] Ring(float z, float r)
            {
                var ring = new Vector3[10];
                for (int j = 0; j < 10; j++)
                {
                    float a = Mathf.Tau * j / 10f;
                    ring[j] = new Vector3(x + Mathf.Cos(a) * r, y + Mathf.Sin(a) * r, z);
                }
                return ring;
            }
            m.Loft(new[] { Ring(z0, EngineRadius * 0.93f), Ring(z0 - 0.6f, EngineRadius), Ring(z0 - 4.2f, EngineRadius), Ring(z0 - 6.2f, EngineRadius * 0.7f), Ring(z0 - EngineLength, EngineRadius * 0.42f) },
                new[] { Hull, Hull, Hull, Hull, Hull, Hull, Hull, Hull, Hull, Hull }, Dark);
            // the intake's dark throat and the exhaust cone
            m.Tube(new Vector3(x, y, z0 + 0.01f), new Vector3(x, y, z0 - 0.4f), EngineRadius * 0.86f, EngineRadius * 0.86f, Dark, 10);
            m.Tube(new Vector3(x, y, z0 - EngineLength + 0.05f), new Vector3(x, y, z0 - EngineLength - 0.8f), EngineRadius * 0.35f, 0.08f, Metal, 8);
            // the pylon up to the wing
            float wy = WingY(x) - Thick(x) * 0.35f;
            m.Box(new Vector3(x, (y + EngineRadius + wy) * 0.5f, z0 - 3.4f), new Vector3(0.45f, wy - y - EngineRadius + 0.3f, 4.8f), Hull);
        }
    }

    private static Node3D Fan(int i, Material bodyMat)
    {
        var f = new MeshScratch();
        f.Tube(new Vector3(0, 0, -0.2f), new Vector3(0, 0, 0.35f), 0.42f, 0.05f, Metal, 10);
        for (int b = 0; b < 14; b++)
        {
            float ang = Mathf.Tau * b / 14f;
            var dir = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0);
            f.Box(dir * (0.42f + (EngineRadius * 0.82f - 0.42f) * 0.5f), new Vector3(EngineRadius * 0.82f - 0.42f, 0.22f, 0.04f), Track,
                new Basis(Vector3.Back, ang) * new Basis(Vector3.Right, 0.6f));
        }
        var node = new Node3D { Name = $"Fan{i}", Position = Flip(new Vector3(EngineX[i], EngineY(i), EngineFrontZ(i) - 0.45f)) };
        node.AddChild(new MeshInstance3D { Name = $"Fan{i}Mesh", Mesh = f.Build(), MaterialOverride = bodyMat });
        return node;
    }

    // ---- the tail ------------------------------------------------------------------------------

    private static float FinFront(float y) => Mathf.Lerp(FinRootFrontZ, FinTopFrontZ, (y - FinRootY) / (FinTopY - FinRootY));
    private static float FinRear(float y) => Mathf.Lerp(FinRootRearZ, FinTopRearZ, (y - FinRootY) / (FinTopY - FinRootY));
    private static float FinThick(float y) => Mathf.Lerp(0.9f, 0.3f, Mathf.Clamp((y - FinRootY) / (FinTopY - FinRootY), 0f, 1f));
    private static Vector3 FinPt(float y, float f, int v) => new(v * FinThick(y) * 0.5f * Prof(f), y, FinFront(y) + f * (FinRear(y) - FinFront(y)));

    private const float RudderFrom = 10.6f, RudderTo = 20.6f, RudderF = 0.6f;
    private const float ElevatorFrom = 1.2f, ElevatorTo = 11.6f, ElevatorF = 0.66f;

    private static float StabLe(float x) => Mathf.Lerp(StabRootFrontZ, StabTipFrontZ, (x - StabRootX) / (StabTipX - StabRootX));
    private static float StabTe(float x) => Mathf.Lerp(StabRootRearZ, StabTipRearZ, (x - StabRootX) / (StabTipX - StabRootX));
    private static float StabThick(float x) => Mathf.Lerp(0.55f, 0.15f, Mathf.Clamp(x / StabTipX, 0f, 1f));

    private static Vector3 StabPt(int sg, float x, float f, int v)
    {
        float zl = StabLe(x), zt = StabTe(x), t = StabThick(x), p = Prof(f);
        return new Vector3(sg * x, StabY + (v > 0 ? t * 0.55f * p : v < 0 ? -t * 0.45f * p : 0f), zl + (zt - zl) * f);
    }

    private static void Tail(MeshScratch m)
    {
        Vector3[] FinRing(float y, float fte) => new[] { FinPt(y, 0f, 0), FinPt(y, 0.3f, 1), FinPt(y, fte, 1), FinPt(y, fte, -1), FinPt(y, 0.3f, -1) };
        foreach (var (from, to, fte) in new[] { (FinRootY - 0.4f, RudderFrom, 1f), (RudderFrom, RudderTo, RudderF), (RudderTo, FinTopY, 1f) })
            m.Loft(new[] { FinRing(from, fte), FinRing(to, fte) }, new[] { Hull, Hull, Stripe, Hull, Hull }, Edge);
        foreach (int sg in new[] { 1, -1 })
        {
            Vector3[] Ring(float x, float fte) => new[] { StabPt(sg, x, 0f, 0), StabPt(sg, x, 0.3f, 1), StabPt(sg, x, fte, 1), StabPt(sg, x, fte, -1), StabPt(sg, x, 0.3f, -1) };
            var edges = new[] { Hull, Hull, Edge, Belly, Belly };
            m.Loft(new[] { Ring(0f, 1f), Ring(ElevatorFrom, 1f) }, edges, Edge);
            m.Loft(new[] { Ring(ElevatorFrom, ElevatorF), Ring(ElevatorTo, ElevatorF) }, edges, Edge);
            m.Loft(new[] { Ring(ElevatorTo, 1f), Ring(StabTipX, 1f) }, edges, Edge);
        }
        // a blue band round the fin
        float by = 16.5f;
        m.Box(new Vector3(0, by, (FinFront(by) + FinRear(by)) * 0.5f), new Vector3(FinThick(by) * 0.62f, 1.2f, (FinRear(by) - FinFront(by)) * -0.62f), Stripe);
    }

    private static Node3D Rudder(Material bm, Material gm)
    {
        var p0 = FinPt(RudderFrom, RudderF, 0);
        var p1 = FinPt(RudderTo, RudderF, 0);
        var yAxis = (Flip(p1) - Flip(p0)).Normalized();
        var part = new AircraftPart((p0 + p1) * 0.5f, new Basis(Vector3.Right, yAxis, Vector3.Right.Cross(yAxis)));
        Vector3[] Ring(float y) => new[] { FinPt(y, RudderF, 1), FinPt(y, 1f, 1), FinPt(y, 1f, -1), FinPt(y, RudderF, -1) };
        part.Loft(new[] { Ring(RudderFrom), Ring(RudderTo) }, new[] { Hull, Edge, Hull, Edge }, Edge);
        return part.ToNode("Rudder", bm, gm);
    }

    private static AircraftPart XPart(int sg, Vector3 inboard, Vector3 outboard)
    {
        var a = Flip(inboard);
        var b = Flip(outboard);
        var along = (sg > 0 ? a - b : b - a).Normalized();
        var y = (Vector3.Up - along * along.Dot(Vector3.Up)).Normalized();
        return new AircraftPart((inboard + outboard) * 0.5f, new Basis(along, y, along.Cross(y)));
    }

    private static Node3D Elevator(string name, int sg, Material bm, Material gm)
    {
        var part = XPart(sg, StabPt(sg, ElevatorFrom, ElevatorF, 0), StabPt(sg, ElevatorTo, ElevatorF, 0));
        Vector3[] Ring(float x) => new[] { StabPt(sg, x, ElevatorF, 1), StabPt(sg, x, 1f, 1), StabPt(sg, x, 1f, -1), StabPt(sg, x, ElevatorF, -1) };
        part.Loft(new[] { Ring(ElevatorFrom), Ring(ElevatorTo) }, new[] { Hull, Edge, Belly, Edge }, Edge);
        return part.ToNode(name, bm, gm);
    }

    private static Node3D Flap(string name, int sg, float x0, float x1, Material bm, Material gm)
    {
        var part = XPart(sg, WPt(sg, x0, FlapChord, 0), WPt(sg, x1, FlapChord, 0));
        Vector3[] Ring(float x) => new[] { WPt(sg, x, FlapChord, 1), WPt(sg, x, 1f, 1), WPt(sg, x, 1f, -1), WPt(sg, x, FlapChord, -1) };
        part.Loft(new[] { Ring(x0), Ring(x1) }, new[] { Hull, Edge, Belly, Edge }, Edge);
        return part.ToNode(name, bm, gm);
    }

    // ---- the gear fairings and the gear ------------------------------------------------------------

    private static void Fairings(MeshScratch m)
    {
        foreach (int sg in new[] { 1, -1 })
        {
            // full, the blister's section; faired, each point drawn in onto the skin (no lower than 2.7 m)
            Vector3[] Ring(float z, float full)
            {
                (float X, float Y)[] pts =
                {
                    (OuterX(z, FairingTopY) - 0.05f, FairingTopY), (FairingOutX - 0.3f, FairingTopY - 0.3f), (FairingOutX, 2.6f), (FairingOutX - 0.1f, 1.8f),
                    (FairingOutX - 0.5f, FairingBottomY), (1.9f, FairingBottomY), (1.9f, 2.65f),
                };
                var r = new Vector3[pts.Length];
                for (int i = 0; i < r.Length; i++)
                {
                    float y0 = Mathf.Max(pts[i].Y, 2.7f), x0 = OuterX(z, y0) - 0.05f;
                    r[i] = new Vector3(sg * Mathf.Lerp(x0, pts[i].X, full), Mathf.Lerp(y0, pts[i].Y, full), z);
                }
                return r;
            }
            var rings = new System.Collections.Generic.List<Vector3[]>();
            foreach (float f in new[] { 1f, 0.75f, 0.45f, 0.2f, 0f }) rings.Add(Ring(Mathf.Lerp(FairingTailZ, FairingRearZ, f), f * (2f - f)));
            rings.Reverse();
            foreach (float f in new[] { 0.2f, 0.45f, 0.75f, 1f }) rings.Add(Ring(Mathf.Lerp(FairingFrontZ, FairingNoseZ, f), 1f - f * f));
            m.Loft(rings.ToArray(), new[] { Hull, Hull, Belly, Belly, Belly, Belly, Belly }, Belly);
            m.Box(new Vector3(sg * MainGearX, FairingBottomY - 0.01f, 0f), new Vector3(1.2f, 0.02f, FairingFrontZ - FairingRearZ - 0.6f), Dark);
        }
        // the nose gear's well doors
        m.Box(new Vector3(0, Section(NoseGearZ).Bottom - 0.01f, NoseGearZ), new Vector3(1.2f, 0.03f, 2.2f), Dark);
    }

    private static Node3D MainGear(string name, int sg, Material bm, Material gm)
    {
        var part = new AircraftPart(new Vector3(sg * MainGearX, 0f, 0f), Basis.Identity);
        float gx = sg * MainGearX;
        foreach (float z in MainLegZ)
        {
            part.Tube(new Vector3(gx, FairingTopY - 1.0f, z), new Vector3(gx, MainWheelRadius, z), 0.13f, 0.1f, Metal, 8);
            part.Tube(new Vector3(gx - 0.35f, MainWheelRadius, z), new Vector3(gx + 0.35f, MainWheelRadius, z), 0.07f, 0.07f, Metal, 6);
            foreach (float wx in new[] { -0.33f, 0.33f })
            {
                part.Tube(new Vector3(gx + wx - MainWheelWidth / 2, MainWheelRadius, z), new Vector3(gx + wx + MainWheelWidth / 2, MainWheelRadius, z), MainWheelRadius, MainWheelRadius, Tyre, 12);
                part.Tube(new Vector3(gx + wx - MainWheelWidth / 2 - 0.01f, MainWheelRadius, z), new Vector3(gx + wx + MainWheelWidth / 2 + 0.01f, MainWheelRadius, z), 0.3f, 0.3f, Metal, 8);
            }
        }
        return part.ToNode(name, bm, gm);
    }

    private static Node3D NoseGear(Material bm, Material gm)
    {
        var part = new AircraftPart(NoseHinge, Basis.Identity);
        foreach (float lx in new[] { -NoseGearX, NoseGearX })
        {
            part.Tube(new Vector3(lx, NoseHinge.Y, NoseHinge.Z), new Vector3(lx, NoseWheelRadius, NoseGearZ), 0.12f, 0.09f, Metal, 8);
            part.Tube(new Vector3(lx - 0.3f, NoseWheelRadius, NoseGearZ), new Vector3(lx + 0.3f, NoseWheelRadius, NoseGearZ), 0.06f, 0.06f, Metal, 6);
            foreach (float wx in new[] { -0.3f, 0.3f })
                part.Tube(new Vector3(lx + wx - 0.17f, NoseWheelRadius, NoseGearZ), new Vector3(lx + wx + 0.17f, NoseWheelRadius, NoseGearZ), NoseWheelRadius, NoseWheelRadius, Tyre, 12);
        }
        return part.ToNode("GearNose", bm, gm);
    }

    // ---- doors ----------------------------------------------------------------------------------------

    private static Node3D CrewDoorLeaf(Material bm, Material gm)
    {
        float z0 = CrewDoorZ - DoorWidth * 0.5f, z1 = CrewDoorZ + DoorWidth * 0.5f;
        var part = new AircraftPart(new Vector3(OuterX(z1, FloorY + 1f), FloorY, z1), Basis.Identity);
        SkinPart(part, 1, FloorRow, DoorTopRow - 1, z0, z1);
        return part.ToNode("Door0", bm, gm);
    }

    /// <summary>The visor: the skin under the cockpit ahead of the hold, and the whole nose ahead of the windscreen; it swings up about its hinge.</summary>
    private static Node3D Visor(Material bm, Material gm)
    {
        var part = new AircraftPart(new Vector3(0, VisorHingeY, VisorCapZ), Basis.Identity);
        SkinPart(part, 0, 0, CeilingRow - 1, NoseHingeZ, VisorCapZ);
        SkinPart(part, 0, 0, Levels.Length - 2, VisorCapZ, NoseZ);
        // its inside face across the hold's front: the bulkhead the ramp folds against
        part.Box(new Vector3(0, BellyY + 0.2f, NoseZ - 0.3f), new Vector3(0.3f, 0.3f, 0.5f), Radome);
        return part.ToNode("Door1", bm, gm);
    }

    /// <summary>The nose ramp: three plates standing folded behind the shut visor, unfolding forward and down.</summary>
    private static Node3D NoseRamp(Material bm, Material gm)
    {
        float p = NosePlate, w = HoldHalfWidth * 2f - 0.1f, z = NoseHingeZ;
        Node3D Plate(string name, Vector3 hinge, float zOff, bool down)
        {
            var part = new AircraftPart(hinge, Basis.Identity);
            float yMid = hinge.Y + (down ? -p * 0.5f : p * 0.5f);
            part.Box(new Vector3(0, yMid, hinge.Z + zOff), new Vector3(w, p, 0.09f), Metal);
            // its treads on the face that is walked once it lies open
            float face = hinge.Z + zOff + (down ? -0.05f : 0.05f) * (zOff == 0f ? -1f : 1f);
            for (int i = 1; i < 6; i++)
                part.Box(new Vector3(0, hinge.Y + (down ? -1f : 1f) * p * i / 6f, face), new Vector3(w - 0.3f, 0.06f, 0.025f), Track);
            return part.ToNode(name, bm, gm);
        }
        var leaf = Plate("Door1r", new Vector3(0, FloorY, z), -0.06f, false);
        var toe = Plate("Toe", new Vector3(0, FloorY + p, z - 0.12f), -0.06f, true);
        var toe2 = Plate("Toe2", new Vector3(0, FloorY, z - 0.24f), -0.06f, false);
        // nested hinges: their positions relative to their parents (all unturned while shut)
        toe2.Position -= toe.Position;
        toe.AddChild(toe2);
        toe.Position -= leaf.Position;
        leaf.AddChild(toe);
        return leaf;
    }

    private static Node3D RearRamp(Material bm, Material gm)
    {
        var part = new AircraftPart(new Vector3(0, FloorY, RampHingeZ), Basis.Identity);
        SkinPart(part, 0, 0, FloorRow - 1, RampClosedEndZ, RampHingeZ);
        var tilt = new Basis(Vector3.Right, -RampClosedAngle);
        float w = HoldHalfWidth * 2f - 0.1f;
        var back = new Vector3(0, Mathf.Sin(RampClosedAngle), -Mathf.Cos(RampClosedAngle));
        var hinge = new Vector3(0, FloorY, RampHingeZ);
        part.Box(hinge + back * (RampLength * 0.5f) + new Vector3(0, -0.05f, 0), new Vector3(w, 0.1f, RampLength), Floor, tilt);
        for (int i = 1; i < 10; i++)
            part.Box(hinge + back * (RampLength * i / 10f) + new Vector3(0, 0.01f, 0), new Vector3(w - 0.3f, 0.03f, 0.08f), Track, tilt);
        var node = part.ToNode("Door2", bm, gm);
        // the toes: hinged at the lip, folded back up the ramp's face while shut
        var lip = hinge + back * RampLength;
        var toes = new AircraftPart(lip, Basis.Identity);
        toes.Box(lip - back * (ToeLength * 0.5f) + new Vector3(0, 0.06f, 0), new Vector3(w - 0.2f, 0.06f, ToeLength), Metal, tilt);
        for (int i = 1; i < 6; i++)
            toes.Box(lip - back * (ToeLength * i / 6f) + new Vector3(0, 0.1f, 0), new Vector3(w - 0.4f, 0.025f, 0.06f), Track, tilt);
        var toeNode = toes.ToNode("Toes", bm, gm);
        toeNode.Position -= node.Position;
        node.AddChild(toeNode);
        return node;
    }

    private static Node3D RearDoorLeaf(Material bm, Material gm)
    {
        var part = new AircraftPart(new Vector3(0, RampTop(RearDoorHingeZ), RearDoorHingeZ), Basis.Identity);
        SkinPart(part, 0, 0, FloorRow - 1, RearDoorHingeZ, RampClosedEndZ);
        return part.ToNode("Door2b", bm, gm);
    }

    /// <summary>A side petal's hinge along its top edge, node space, from its front end aft (the tail rises).</summary>
    private static Vector3 PetalAxis(int sg) => (Flip(Po(PetalRearZ, H[CeilingRow], sg)) - Flip(Po(RampHingeZ, H[CeilingRow], sg))).Normalized();

    /// <summary>A side petal of the rear opening, hinged along its top edge, folding up and in.</summary>
    private static Node3D Petal(int sg, Material bm, Material gm)
    {
        var part = new AircraftPart(Po(RampHingeZ, H[CeilingRow], sg), Basis.Identity);
        SkinPart(part, sg, FloorRow, CeilingRow - 1, PetalRearZ, RampHingeZ);
        return part.ToNode(sg > 0 ? "Door2L" : "Door2R", bm, gm);
    }

    private static void AddLights(Node3D root)
    {
        void Add(string name, Vector3 auth, Vector3 size, Color c)
        {
            var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = c };
            root.AddChild(new MeshInstance3D { Name = name, Mesh = new BoxMesh { Size = size }, MaterialOverride = mat, Position = Flip(auth) });
        }
        var red = new Color(1f, 0.1f, 0.08f);
        var green = new Color(0.1f, 1f, 0.2f);
        var white = new Color(1f, 1f, 0.95f);
        float tipY = WingY(WingTipX), tipZ = (WingLeading(WingTipX) + WingTrailing(WingTipX)) * 0.5f;
        Add("NavL", new Vector3(WingTipX + 0.1f, tipY, tipZ + 1.0f), new Vector3(0.16f, 0.14f, 0.24f), red);
        Add("NavR", new Vector3(-(WingTipX + 0.1f), tipY, tipZ + 1.0f), new Vector3(0.16f, 0.14f, 0.24f), green);
        Add("NavTail", new Vector3(0, 9.0f, TailZ - 0.08f), new Vector3(0.16f, 0.18f, 0.12f), white);
        Add("BeaconTop", new Vector3(0, Crown(14f) + 0.08f, 14f), new Vector3(0.26f, 0.16f, 0.26f), red);
        Add("BeaconBottom", new Vector3(0, BellyY - 0.06f, 10f), new Vector3(0.26f, 0.12f, 0.26f), red);
        Add("StrobeL", new Vector3(WingTipX + 0.1f, tipY, tipZ - 0.8f), new Vector3(0.14f, 0.12f, 0.14f), white);
        Add("StrobeR", new Vector3(-(WingTipX + 0.1f), tipY, tipZ - 0.8f), new Vector3(0.14f, 0.12f, 0.14f), white);
        const float lx = 8.0f;
        Add("LandingL", new Vector3(lx, WingY(lx) - 0.4f, WingLeading(lx) - 0.2f), new Vector3(0.35f, 0.14f, 0.1f), white);
        Add("LandingR", new Vector3(-lx, WingY(lx) - 0.4f, WingLeading(lx) - 0.2f), new Vector3(0.35f, 0.14f, 0.1f), white);
    }
}
