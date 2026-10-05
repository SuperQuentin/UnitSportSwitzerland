using Godot;
using static UnitSport.Avatar.FreighterLayout;

namespace UnitSport.Avatar;

/// <summary>
/// The military cargo plane (#420; the Battle Royale's aircraft since #207), built from code like the
/// A320 (<see cref="A320MeshBuilder"/>): low-poly, flat-shaded, one <c>Body</c> mesh for what stays
/// put plus hinge nodes for what moves, every number from <see cref="FreighterLayout"/>. The fuselage
/// is a hollow ring of thin slabs with real holes (the crew door, the para doors, the ramp and upper
/// door under the tail, the flight deck's glass); inside are the hold with its lining, ribs, roller
/// tracks and troop benches, the stairs and the flight deck.
///
/// <para>
/// Moving parts: <c>FlapIn/FlapOut/Aileron/Elevator L|R</c> and <c>Rudder</c> (as the A320's),
/// <c>GearMainL/R</c> (tandem wheels rising straight up into the sponsons: <see cref="GearLift"/>),
/// <c>GearNose</c> (folding forward), <c>Fan0..3</c> (the propellers, about their local Z),
/// <c>Door0..3</c> plus <c>Door3b</c> (the upper door that goes with the ramp): <see cref="DoorMotions"/>.
/// </para>
/// Authored +Z forward, +X the left side; <see cref="Build"/> returns NODE space (−Z forward, origin
/// on the ground between the main wheels).
/// </summary>
public static class FreighterMeshBuilder
{
    // ---- palette: Swiss Air Force grey-green ----
    private static readonly Color Hull = new(0.40f, 0.44f, 0.37f);
    private static readonly Color Belly = new(0.50f, 0.53f, 0.49f);
    private static readonly Color Radome = new(0.24f, 0.26f, 0.24f);
    private static readonly Color Edge = new(0.33f, 0.36f, 0.31f);
    private static readonly Color Dark = new(0.07f, 0.07f, 0.08f);
    private static readonly Color Metal = new(0.34f, 0.35f, 0.36f);
    private static readonly Color Lining = new(0.55f, 0.58f, 0.50f);
    private static readonly Color Rib = new(0.30f, 0.32f, 0.28f);
    private static readonly Color Floor = new(0.22f, 0.23f, 0.22f);
    private static readonly Color Track = new(0.55f, 0.56f, 0.57f);
    private static readonly Color Webbing = new(0.62f, 0.12f, 0.10f);
    private static readonly Color Tube = new(0.70f, 0.71f, 0.72f);
    private static readonly Color Lamp = new(1.0f, 0.95f, 0.75f);
    private static readonly Color Panel = new(0.17f, 0.18f, 0.18f);
    private static readonly Color Screen = new(0.04f, 0.08f, 0.06f);
    private static readonly Color Seat = new(0.24f, 0.26f, 0.22f);
    private static readonly Color Tyre = new(0.06f, 0.06f, 0.06f);
    private static readonly Color Red = new(0.85f, 0.10f, 0.10f);
    private static readonly Color Pane = new(0.40f, 0.52f, 0.58f, 0.35f);

    private static Vector3 Flip(Vector3 v) => AircraftMeshBuilder.Flip(v);

    // ---- the public contract --------------------------------------------------------------

    /// <summary>How far the main gear nodes rise to stow (straight up into the sponsons), metres.</summary>
    public const float GearLift = MainStowLift;

    /// <summary>The nose leg's stow: a turn about its local X (positive swings the wheels forward).</summary>
    public const float NoseStowAngle = 1.75f;

    /// <summary>
    /// What opening door <paramref name="i"/> moves: hinge nodes, each turned about a local axis by an
    /// angle (fully open). The crew door folds down and out into steps about its sill; the para doors
    /// swing out and forward along the skin; the ramp's lip goes down to the ground about its hinge and
    /// the upper door swings up into the tail.
    /// </summary>
    /// <para>Each part has a second angle for the air (<c>AirAngle</c>): the ramp opened in flight
    /// stops level with the hold floor, its toes stay folded (#420).</para>
    public static (string Node, Vector3 Axis, float Angle, float AirAngle)[] DoorMotions(int i) => i switch
    {
        CrewDoor => new[] { ("Door0", Vector3.Back, 2.19f, 2.19f) },
        ParaDoorL => new[] { ("Door1", Vector3.Up, -2.6f, -2.6f) },
        ParaDoorR => new[] { ("Door2", Vector3.Up, 2.6f, 2.6f) },
        RampDoor => new[]
        {
            ("Door3", Vector3.Right, RampTravel, RampClosedAngle), ("Door3/Toes", Vector3.Right, -Mathf.Pi, 0f),
            ("Door3b", Vector3.Right, UpperDoorOpen, UpperDoorOpen),
        },
        _ => System.Array.Empty<(string, Vector3, float, float)>(),
    };

    /// <summary>How fast door <paramref name="i"/> travels, fraction of its stroke per second (the ramp is slow).</summary>
    public static float DoorRate(int i) => i == RampDoor ? 0.16f : 0.5f;

    /// <summary>The whole aircraft, node space, ready to add to a scene.</summary>
    public static Node3D Build()
    {
        var bodyMat = HumanMeshBuilder.FigureMaterial();
        var glassMat = CarRig.GlassMaterial();
        var root = new Node3D { Name = "Freighter" };

        var b = new MeshScratch();
        Fuselage(b);
        Hold(b);
        FlightDeck(b);
        Wings(b);
        Nacelles(b);
        Tail(b);
        Sponsons(b);
        Marks(b);
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
        }
        root.AddChild(Rudder(bodyMat, glassMat));
        root.AddChild(NoseGear(bodyMat, glassMat));
        for (int i = 0; i < EngineX.Length; i++) root.AddChild(Propeller(i, bodyMat));
        root.AddChild(CrewDoorLeaf(bodyMat, glassMat));
        root.AddChild(ParaDoorLeaf(ParaDoorL, bodyMat, glassMat));
        root.AddChild(ParaDoorLeaf(ParaDoorR, bodyMat, glassMat));
        root.AddChild(RampLeaf(bodyMat, glassMat));
        root.AddChild(UpperDoorLeaf(bodyMat, glassMat));
        AddLights(root);
        return root;
    }

    private static Color[] Rep(Color c, int n)
    {
        var a = new Color[n];
        System.Array.Fill(a, c);
        return a;
    }

    // ---- the fuselage ---------------------------------------------------------------------

    /// <summary>The skin's rows, barrel heights belly to crown (row k between Levels[k] and Levels[k + 1]); as fractions of each station's section.</summary>
    private static readonly float[] Levels = { 0.75f, 0.9f, 1.05f, 1.6f, 2.2f, 2.85f, 3.25f, 3.8f, 4.3f, 4.6f, 4.75f };
    private static readonly float[] H = MakeH();

    private static float[] MakeH()
    {
        var h = new float[Levels.Length];
        for (int i = 0; i < h.Length; i++) h[i] = Mathf.Clamp((Levels[i] - CentreY) / HalfHeight, -1f, 1f);
        return h;
    }

    /// <summary>A point of the skin's outer face at station z, fraction h (−1 belly .. 1 crown), side sg.</summary>
    private static Vector3 Po(float z, float h, int sg)
    {
        var (a, b0, b1) = Section(z);
        return new Vector3(sg * Across(a, h), (b0 + b1) * 0.5f + (b1 - b0) * 0.5f * h, z);
    }

    /// <summary>The same on the skin's inner face.</summary>
    private static Vector3 Pi(float z, float h, int sg)
    {
        var (a, b0, b1) = Section(z);
        float ai = Mathf.Max(a - FreighterLayout.Skin, 0.03f), bi = Mathf.Max((b1 - b0) * 0.5f - FreighterLayout.Skin, 0.03f);
        return new Vector3(sg * Across(ai, h), (b0 + b1) * 0.5f + bi * h, z);
    }

    private static readonly float[] Stations = MakeStations();

    private static float[] MakeStations()
    {
        var l = new List<float> { BarrelRear, BarrelFront, RampClosedEndZ, UpperDoorHingeZ, -10f };
        for (int i = 0; i <= 12; i++) l.Add(Mathf.Lerp(BarrelRear, TailZ, i / 12f));
        foreach (float t in new[] { 0f, .1f, .2f, .3f, .4f, .5f, .6f, .7f, .8f, .88f, .94f, .98f, 1f }) l.Add(BarrelFront + (NoseZ - BarrelFront) * t);
        for (float z = BarrelRear; z < BarrelFront; z += 1.4f) l.Add(z);
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
        float hw = DoorWidth * 0.5f;
        if (k is >= 2 and <= 4)
        {
            if (sg > 0) holes.Add(new Hole(CrewDoorZ - hw, CrewDoorZ + hw, false));
            holes.Add(new Hole(ParaDoorZ - hw, ParaDoorZ + hw, false));
        }
        // the ramp and the upper door: the belly's two bottom rows under the tail
        if (k <= 1) holes.Add(new Hole(UpperDoorHingeZ, RampHingeZ, false));
        if (k == 5) foreach (float z in HoldWindowZ) holes.Add(new Hole(z - 0.18f, z + 0.18f, true));
        // the flight deck: side windows, the windscreen, the eyebrows and the chin
        if (k is 6 or 7)
            foreach (var (f, t) in new[] { (8.75f, 9.2f), (9.3f, 9.75f), (9.85f, 10.4f), (10.5f, 10.95f) }) holes.Add(new Hole(f, t, true));
        if (k == 8) { holes.Add(new Hole(9.3f, 9.75f, true)); holes.Add(new Hole(9.85f, 10.4f, true)); }
        if (k == 3) holes.Add(new Hole(10.45f, 11.0f, true));
        holes.Sort((x, y) => x.From.CompareTo(y.From));
        return holes;
    }

    private static Color RowColour(int k, float z) => z > 11.5f ? Radome : k < 2 ? Belly : Hull;

    private static void Fuselage(MeshScratch m)
    {
        foreach (int sg in new[] { 1, -1 })
            for (int k = 0; k < Levels.Length - 1; k++)
            {
                float z = TailZ;
                foreach (var h in HolesFor(sg, k))
                {
                    Slab(m, sg, k, z, h.From);
                    z = h.To;
                    if (h.Pane) PaneRow(m, sg, k, h.From, h.To);
                }
                Slab(m, sg, k, z, NoseZ);
            }
        // the tail's end: a plate over the cone's last ring
        var (a, b0, b1) = Section(TailZ);
        m.Box(new Vector3(0, (b0 + b1) * 0.5f, TailZ + 0.02f), new Vector3(a * 2f, b1 - b0, 0.06f), Edge);
    }

    private static void Slab(MeshScratch m, int sg, int k, float z0, float z1)
    {
        if (z1 - z0 < 0.02f) return;
        // a slab never spans the radome's edge: its own colour
        const float radome = 11.5f;
        if (z0 < radome - 0.001f && z1 > radome + 0.001f) { Slab(m, sg, k, z0, radome); Slab(m, sg, k, radome, z1); return; }
        var c = RowColour(k, (z0 + z1) * 0.5f);
        var zs = Zs(z0, z1);
        var rings = new Vector3[zs.Count][];
        for (int i = 0; i < zs.Count; i++)
            rings[i] = new[] { Po(zs[i], H[k], sg), Po(zs[i], H[k + 1], sg), Pi(zs[i], H[k + 1], sg), Pi(zs[i], H[k], sg) };
        m.Loft(rings, new[] { c, c, Rib, c }, c);
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

    // ---- the hold ---------------------------------------------------------------------------

    private const float LiningX = HoldHalfWidth + 0.03f, LiningT = 0.06f;

    private static void Hold(MeshScratch m)
    {
        float front = HoldFrontZ, rear = RearWallZ;
        // the floor, its roller tracks and tie-down rails, from the ramp's hinge to the flight deck's step
        {
            var zs = Zs(RampHingeZ, front);
            var rings = new Vector3[zs.Count][];
            for (int i = 0; i < zs.Count; i++)
            {
                float z = zs[i], w = InnerAt(z, FloorY), w2 = InnerAt(z, FloorY - 0.12f);
                rings[i] = new[] { new Vector3(-w, FloorY, z), new Vector3(w, FloorY, z), new Vector3(w2, FloorY - 0.12f, z), new Vector3(-w2, FloorY - 0.12f, z) };
            }
            m.Loft(rings, new[] { Floor, Hull, Hull, Hull }, Hull);
        }
        foreach (float x in new[] { -1.05f, -0.45f, 0.45f, 1.05f })
            m.Box(new Vector3(x, FloorY + 0.012f, (RampHingeZ + StairFootZ) * 0.5f), new Vector3(0.12f, 0.025f, StairFootZ - RampHingeZ), Track);
        for (float z = RampHingeZ + 0.5f; z < StairFootZ; z += 0.5f)
            foreach (float x in new[] { -0.75f, 0.75f })
                m.Box(new Vector3(x, FloorY + 0.008f, z), new Vector3(0.08f, 0.016f, 0.08f), Dark);

        // the linings: flat walls with the doors cut out, ribs every frame, the ceiling under the wing box
        foreach (int sg in new[] { 1, -1 })
        {
            float x = sg * (LiningX + LiningT * 0.5f);
            var cuts = new List<(float From, float To)> { (ParaDoorZ - DoorWidth * 0.5f, ParaDoorZ + DoorWidth * 0.5f) };
            if (sg > 0) cuts.Add((CrewDoorZ - DoorWidth * 0.5f, CrewDoorZ + DoorWidth * 0.5f));
            cuts.Sort((p, q) => p.From.CompareTo(q.From));
            float z = rear;
            foreach (var (f, t) in cuts)
            {
                m.Box(new Vector3(x, (FloorY + HoldCeilingY) * 0.5f, (z + f) * 0.5f), new Vector3(LiningT, HoldCeilingY - FloorY, f - z), Lining);
                m.Box(new Vector3(x, (FloorY + DoorHeight + HoldCeilingY) * 0.5f, (f + t) * 0.5f), new Vector3(LiningT, HoldCeilingY - FloorY - DoorHeight, t - f), Lining);
                Jambs(m, sg, f, t);
                z = t;
            }
            m.Box(new Vector3(x, (FloorY + HoldCeilingY) * 0.5f, (z + front) * 0.5f), new Vector3(LiningT, HoldCeilingY - FloorY, front - z), Lining);
            for (float rz = rear + 0.3f; rz < front; rz += 0.56f)
            {
                bool door = false;
                foreach (var (f, t) in cuts) if (rz > f - 0.05f && rz < t + 0.05f) door = true;
                float y0 = door ? FloorY + DoorHeight : FloorY;
                m.Box(new Vector3(sg * (LiningX - 0.02f), (y0 + HoldCeilingY) * 0.5f, rz), new Vector3(0.04f, HoldCeilingY - y0, 0.07f), Rib);
            }
            // the troop benches: red webbing seat and back, a tube frame, legs every other seat
            float z0 = TroopZ(0) + TroopPitch * 0.5f, z1 = TroopZ(TroopRows - 1) - TroopPitch * 0.5f;
            float bx = sg * (HoldHalfWidth - BenchDepth * 0.5f);
            m.Box(new Vector3(bx, FloorY + BenchHeight - 0.03f, (z0 + z1) * 0.5f), new Vector3(BenchDepth, 0.05f, z0 - z1), Webbing);
            m.Box(new Vector3(sg * (HoldHalfWidth - BenchDepth + 0.02f), FloorY + BenchHeight - 0.05f, (z0 + z1) * 0.5f), new Vector3(0.04f, 0.04f, z0 - z1), Tube);
            m.Box(new Vector3(sg * (HoldHalfWidth - 0.03f), FloorY + 1.0f, (z0 + z1) * 0.5f), new Vector3(0.04f, 0.6f, z0 - z1), Webbing);
            m.Box(new Vector3(sg * (HoldHalfWidth - 0.04f), FloorY + 1.33f, (z0 + z1) * 0.5f), new Vector3(0.04f, 0.04f, z0 - z1), Tube);
            for (int r = 0; r <= TroopRows; r++)
            {
                float sz = TroopZ(0) + TroopPitch * 0.5f - r * TroopPitch;
                m.Box(new Vector3(bx, FloorY + BenchHeight - 0.005f, sz), new Vector3(BenchDepth, 0.012f, 0.02f), Dark);
                if (r % 2 == 0)
                    m.Tube(new Vector3(sg * (HoldHalfWidth - BenchDepth + 0.04f), FloorY + BenchHeight - 0.05f, sz),
                        new Vector3(sg * (HoldHalfWidth - BenchDepth + 0.1f), FloorY, sz), 0.02f, 0.02f, Tube, 4);
            }
        }
        // the ceiling and its lamps
        m.Box(new Vector3(0, HoldCeilingY + 0.03f, (rear + front) * 0.5f), new Vector3(LiningX * 2f + LiningT * 2f, 0.06f, front - rear), Lining);
        for (float z = rear + 1.2f; z < front - 0.5f; z += 2.4f)
            m.Box(new Vector3(0, HoldCeilingY - 0.01f, z), new Vector3(0.5f, 0.03f, 0.18f), Lamp);
        for (float z = rear + 0.3f; z < front; z += 1.12f)
            m.Box(new Vector3(0, HoldCeilingY - 0.04f, z), new Vector3(LiningX * 2f, 0.06f, 0.07f), Rib);
        // the rear wall over the ramp's end
        float wallBottom = RampTop(RampClosedEndZ) + 0.1f;
        m.Box(new Vector3(0, (wallBottom + HoldCeilingY) * 0.5f, rear - 0.04f), new Vector3(LiningX * 2f, HoldCeilingY - wallBottom, 0.08f), Lining);

        // the front: the flight deck's floor edge, the stairs up to it, the bulkhead with its doorway
        float side = (LiningX * 2f - StairWidth) * 0.5f;
        foreach (int sg in new[] { 1, -1 })
        {
            m.Box(new Vector3(sg * (StairWidth * 0.5f + side * 0.5f), (FloorY + FlightDeckY) * 0.5f, front - 0.04f), new Vector3(side, FlightDeckY - FloorY, 0.08f), Lining);
            m.Box(new Vector3(sg * (StairWidth * 0.5f + side * 0.5f), (FlightDeckY + HoldCeilingY) * 0.5f, front), new Vector3(side, HoldCeilingY - FlightDeckY, 0.08f), Lining);
            // a hand rail up each side of the stairs
            m.Tube(new Vector3(sg * (StairWidth * 0.5f + 0.03f), FloorY + 0.9f, StairFootZ), new Vector3(sg * (StairWidth * 0.5f + 0.03f), FlightDeckY + 0.9f, front),
                0.025f, 0.025f, Tube, 5);
        }
        m.Box(new Vector3(0, (HoldCeilingY + FlightDeckCeilingY) * 0.5f, front), new Vector3(LiningX * 2f, FlightDeckCeilingY - HoldCeilingY, 0.08f), Lining);
        const int steps = 4;
        float run = (front - StairFootZ) / steps, rise = (FlightDeckY - FloorY) / steps;
        for (int i = 0; i < steps; i++)
        {
            float top = FloorY + rise * (i + 1);
            float zFront = StairFootZ + run * (i + 1);
            m.Box(new Vector3(0, (FloorY + top) * 0.5f, zFront - run * 0.5f), new Vector3(StairWidth, top - FloorY, run), i % 2 == 0 ? Metal : Floor);
            m.Box(new Vector3(0, top + 0.005f, zFront - run + 0.05f), new Vector3(StairWidth, 0.012f, 0.06f), Track);
        }
    }

    /// <summary>A door's frame through the skin: both jambs, the head and the sill, from the lining out to the skin.</summary>
    private static void Jambs(MeshScratch m, int sg, float z0, float z1)
    {
        float top = FloorY + DoorHeight;
        foreach (float z in new[] { z0, z1 })
        {
            var ring = new List<Vector3> { new(sg * LiningX, FloorY, z) };
            foreach (float y in new[] { FloorY, 1.6f, 2.2f, top }) ring.Add(new Vector3(sg * (OuterX(z, y) - 0.01f), y, z));
            ring.Add(new Vector3(sg * LiningX, top, z));
            float dz = z == z0 ? 0.05f : -0.05f;
            var far = ring.ConvertAll(p => p + new Vector3(0, 0, dz)).ToArray();
            m.Loft(new[] { ring.ToArray(), far }, Rep(Rib, ring.Count), Rib);
        }
        float zm = (z0 + z1) * 0.5f;
        float headOut = OuterX(zm, top) - 0.01f, sillOut = OuterX(zm, FloorY) - 0.01f;
        m.Box(new Vector3(sg * (LiningX + headOut) * 0.5f, top + 0.03f, zm), new Vector3(headOut - LiningX, 0.06f, z1 - z0), Rib);
        m.Box(new Vector3(sg * (HoldHalfWidth + sillOut) * 0.5f, FloorY - 0.03f, zm), new Vector3(sillOut - HoldHalfWidth, 0.06f, z1 - z0), Metal);
    }

    // ---- the flight deck --------------------------------------------------------------------

    private static float InnerAt(float z, float y) => Mathf.Max(0.1f, OuterX(z, y) - FreighterLayout.Skin - 0.02f);

    private static void FlightDeck(MeshScratch m)
    {
        // its floor, narrowing with the nose
        var zs = Zs(HoldFrontZ, FlightDeckFrontZ);
        var rings = new Vector3[zs.Count][];
        for (int i = 0; i < zs.Count; i++)
        {
            float z = zs[i], w = InnerAt(z, FlightDeckY), w2 = InnerAt(z, FlightDeckY - 0.1f);
            rings[i] = new[] { new Vector3(-w, FlightDeckY, z), new Vector3(w, FlightDeckY, z), new Vector3(w2, FlightDeckY - 0.1f, z), new Vector3(-w2, FlightDeckY - 0.1f, z) };
        }
        m.Loft(rings, new[] { Floor, Hull, Hull, Hull }, Hull);
        // the ceiling
        zs = Zs(HoldFrontZ, PanelZ);
        rings = new Vector3[zs.Count][];
        for (int i = 0; i < zs.Count; i++)
        {
            float z = zs[i];
            var (_, _, top) = Section(z);
            float y = Mathf.Min(FlightDeckCeilingY, top - FreighterLayout.Skin - 0.05f), w = InnerAt(z, y);
            rings[i] = new[] { new Vector3(-w, y, z), new Vector3(w, y, z), new Vector3(w, y + 0.04f, z), new Vector3(-w, y + 0.04f, z) };
        }
        m.Loft(rings, Rep(Lining, 4), Lining);

        // the pilots' seats
        foreach (var hip in new[] { CaptainHip, FirstOfficerHip })
        {
            float x = hip.X;
            m.Box(new Vector3(x, FlightDeckY + 0.14f, hip.Z), new Vector3(0.24f, 0.28f, 0.3f), Dark);
            m.Box(new Vector3(x, hip.Y - 0.1f, hip.Z + 0.08f), new Vector3(0.52f, 0.12f, 0.5f), Seat);
            m.Box(new Vector3(x, hip.Y + 0.32f, hip.Z - 0.2f), new Vector3(0.52f, 0.85f, 0.12f), Seat, new Basis(Vector3.Right, -0.12f));
            m.Box(new Vector3(x, hip.Y + 0.84f, hip.Z - 0.25f), new Vector3(0.3f, 0.2f, 0.1f), Seat);
            foreach (float s in new[] { -1f, 1f })
                m.Box(new Vector3(x + s * 0.28f, hip.Y + 0.12f, hip.Z + 0.06f), new Vector3(0.05f, 0.05f, 0.4f), Dark);
            // the control column and its yoke
            m.Tube(new Vector3(x, FlightDeckY, hip.Z + 0.75f), new Vector3(x, hip.Y + 0.35f, hip.Z + 0.55f), 0.035f, 0.03f, Dark, 5);
            m.Box(new Vector3(x, hip.Y + 0.38f, hip.Z + 0.53f), new Vector3(0.34f, 0.05f, 0.05f), Dark);
        }
        // the pedestal with four power levers, the panel with its gauges, the glareshield, the overhead
        m.Box(new Vector3(0, FlightDeckY + 0.3f, 9.75f), new Vector3(0.38f, 0.6f, 0.9f), Panel);
        for (int i = 0; i < 4; i++)
            m.Tube(new Vector3(-0.12f + i * 0.08f, FlightDeckY + 0.6f, 9.6f), new Vector3(-0.12f + i * 0.08f, FlightDeckY + 0.8f, 9.75f), 0.015f, 0.012f, Lamp, 4);
        float hw = Mathf.Min(1.3f, InnerAt(PanelZ, 2.7f));
        m.Box(new Vector3(0, 2.62f, PanelZ), new Vector3(hw * 2f, 0.7f, 0.1f), Panel);
        for (int i = 0; i < 10; i++)
        {
            float x = (i % 5 - 2) * hw * 0.38f;
            m.Box(new Vector3(x, i < 5 ? 2.78f : 2.52f, PanelZ - 0.055f), new Vector3(0.2f, 0.18f, 0.02f), i % 3 == 0 ? Screen : Dark);
        }
        float gw = Mathf.Min(1.3f, InnerAt(PanelZ - 0.15f, 3.0f));
        m.Box(new Vector3(0, 3.0f, PanelZ - 0.12f), new Vector3(gw * 2f, 0.06f, 0.4f), Dark);
        m.Box(new Vector3(0, FlightDeckCeilingY - 0.08f, 9.3f), new Vector3(0.9f, 0.12f, 1.0f), Panel);
        // behind the pilots: the engineer's station (right), a crew bunk (left)
        m.Box(new Vector3(-1.45f, FlightDeckY + 0.5f, 7.6f), new Vector3(0.6f, 1.0f, 1.6f), Panel);
        m.Box(new Vector3(-1.45f, FlightDeckY + 1.3f, 7.6f), new Vector3(0.5f, 0.6f, 1.4f), Panel, new Basis(Vector3.Back, -0.2f));
        m.Box(new Vector3(1.4f, FlightDeckY + 0.5f, 7.2f), new Vector3(0.7f, 0.12f, 1.9f), Seat);
        m.Box(new Vector3(1.4f, FlightDeckY + 0.22f, 7.2f), new Vector3(0.7f, 0.44f, 1.9f), Panel);
    }

    // ---- wings -------------------------------------------------------------------------------

    private static float Thick(float x) => Mathf.Lerp(WingRootThickness, WingTipThickness, Mathf.Clamp((Mathf.Abs(x) - WingRootX) / (WingTipX - WingRootX), 0f, 1f));

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
        float zl = WingLeading(x), zt = WingTrailing(x);
        float t = Thick(x), p = Prof(f);
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
        // over the roof, the centre box from one root to the other
        m.Loft(new[] { WingRing(-1, WingRootX, 1f), WingRing(1, WingRootX, 1f) }, edges, Edge);
        var segments = new (float From, float To, float Fte)[]
        {
            (WingRootX, InnerFlapFrom, 1f), (InnerFlapFrom, InnerFlapTo, FlapChord), (InnerFlapTo, OuterFlapFrom, 1f),
            (OuterFlapFrom, OuterFlapTo, FlapChord), (OuterFlapTo, AileronFrom, 1f), (AileronFrom, AileronTo, FlapChord),
            (AileronTo, WingTipX, 1f),
        };
        foreach (int sg in new[] { 1, -1 })
        {
            foreach (var s in segments)
                m.Loft(new[] { WingRing(sg, s.From, s.Fte), WingRing(sg, s.To, s.Fte) }, edges, Edge);
            // the rounded tip
            m.Loft(new[] { WingRing(sg, WingTipX, 1f), Shrunk(WingRing(sg, WingTipX + 0.25f, 1f), 0.6f) }, edges, Edge);
            // the flap tracks under the trailing edge, the refuelling pod's pylon spot (a fairing)
            foreach (float x in new[] { 3.6f, 6.4f, 9.8f, 12.6f })
            {
                var a = WPt(sg, x, 0.55f, -1) + new Vector3(0, -0.04f, 0);
                m.Tube(a, new Vector3(sg * x, a.Y - 0.08f, WingTrailing(x) - 0.4f), 0.11f, 0.03f, Belly, 5);
            }
        }
        // the fairing that blends the wing into the roof
        m.Box(new Vector3(0, TopY + 0.05f, (WingRootLeadingZ + WingRootTrailingZ) * 0.5f), new Vector3(2.6f, 0.3f, WingRootLeadingZ - WingRootTrailingZ + 0.6f), Hull);
    }

    private static Vector3[] Shrunk(Vector3[] ring, float k)
    {
        var c = Vector3.Zero;
        foreach (var p in ring) c += p;
        c /= ring.Length;
        var r = new Vector3[ring.Length];
        for (int i = 0; i < r.Length; i++) r[i] = c + (ring[i] - c) * k;
        return r;
    }

    // ---- engines -----------------------------------------------------------------------------

    private static void Nacelles(MeshScratch m)
    {
        for (int i = 0; i < EngineX.Length; i++)
        {
            float x = EngineX[i], y = EngineY(i);
            // a tall rounded section: up into the wing, down over the oil cooler
            Vector3[] Ring(float z, float s, float drop)
            {
                var r = new Vector3[8];
                var shape = new (float X, float Y)[] { (0.0f, 0.62f), (0.45f, 0.5f), (0.55f, 0.0f), (0.45f, -0.55f), (0.0f, -0.62f - drop), (-0.45f, -0.55f), (-0.55f, 0.0f), (-0.45f, 0.5f) };
                for (int j = 0; j < 8; j++) r[j] = new Vector3(x + shape[j].X * s, y + shape[j].Y * s, z);
                return r;
            }
            m.Loft(new[] { Ring(PropZ - 0.25f, 0.62f, 0f), Ring(PropZ - 0.9f, 1f, 0.15f), Ring(2.2f, 1f, 0.1f), Ring(0.2f, 0.85f, 0f), Ring(NacelleRearZ, 0.35f, 0f) },
                Rep(Hull, 8), Edge);
            // the intake under the spinner and the exhaust on the inboard flank
            m.Box(new Vector3(x, y - 0.45f, PropZ - 0.45f), new Vector3(0.42f, 0.26f, 0.12f), Dark);
            float sx = x > 0 ? -1f : 1f;
            m.Tube(new Vector3(x + sx * 0.5f, y + 0.05f, 1.6f), new Vector3(x + sx * 0.62f, y + 0.08f, 0.6f), 0.16f, 0.14f, Dark, 6);
        }
    }

    private static Node3D Propeller(int i, Material bodyMat)
    {
        var f = new MeshScratch();
        // the spinner, the hub, four broad blades
        f.Tube(new Vector3(0, 0, -0.3f), new Vector3(0, 0, 0.05f), 0.36f, 0.36f, Edge, 10);
        f.Tube(new Vector3(0, 0, 0.05f), new Vector3(0, 0, 0.75f), 0.36f, 0.04f, Hull, 10);
        for (int b = 0; b < 4; b++)
        {
            float ang = Mathf.Tau * b / 4f + 0.4f;
            var dir = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0);
            f.Box(dir * (0.3f + (PropRadius - 0.3f) * 0.5f), new Vector3(PropRadius - 0.3f, 0.3f, 0.05f), Dark,
                new Basis(Vector3.Back, ang) * new Basis(Vector3.Right, 0.35f));
            f.Box(dir * (PropRadius - 0.12f) + new Vector3(0, 0, 0.01f), new Vector3(0.22f, 0.3f, 0.06f), new Color(0.9f, 0.75f, 0.1f),
                new Basis(Vector3.Back, ang) * new Basis(Vector3.Right, 0.35f));
        }
        var node = new Node3D { Name = $"Fan{i}", Position = Flip(new Vector3(EngineX[i], EngineY(i), PropZ)) };
        node.AddChild(new MeshInstance3D { Name = $"Fan{i}Mesh", Mesh = f.Build(), MaterialOverride = bodyMat });
        return node;
    }

    // ---- the tail -------------------------------------------------------------------------------

    private static float FinFront(float y) => Mathf.Lerp(FinRootFrontZ, FinTopFrontZ, (y - FinRootY) / (FinTopY - FinRootY));
    private static float FinRear(float y) => Mathf.Lerp(FinRootRearZ, FinTopRearZ, (y - FinRootY) / (FinTopY - FinRootY));
    private static float FinThick(float y) => Mathf.Lerp(0.5f, 0.18f, Mathf.Clamp((y - FinRootY) / (FinTopY - FinRootY), 0f, 1f));

    private static Vector3 FinPt(float y, float f, int v) =>
        new(v * FinThick(y) * 0.5f * Prof(f), y, FinFront(y) + f * (FinRear(y) - FinFront(y)));

    private const float RudderFrom = 5.0f, RudderTo = 11.3f, RudderF = 0.66f;
    private const float ElevatorFrom = 0.6f, ElevatorTo = 7.6f, ElevatorF = 0.64f;

    private static float StabLe(float x) => Mathf.Lerp(StabRootFrontZ, StabTipFrontZ, (x - StabRootX) / (StabTipX - StabRootX));
    private static float StabTe(float x) => Mathf.Lerp(StabRootRearZ, StabTipRearZ, (x - StabRootX) / (StabTipX - StabRootX));
    private static float StabThick(float x) => Mathf.Lerp(0.34f, 0.12f, Mathf.Clamp(x / StabTipX, 0f, 1f));

    private static Vector3 StabPt(int sg, float x, float f, int v)
    {
        float zl = StabLe(x), zt = StabTe(x), t = StabThick(x), p = Prof(f);
        return new Vector3(sg * x, StabY + (v > 0 ? t * 0.55f * p : v < 0 ? -t * 0.45f * p : 0f), zl + (zt - zl) * f);
    }

    private static void Tail(MeshScratch m)
    {
        Vector3[] FinRing(float y, float fte) => new[] { FinPt(y, 0f, 0), FinPt(y, 0.3f, 1), FinPt(y, fte, 1), FinPt(y, fte, -1), FinPt(y, 0.3f, -1) };
        foreach (var (from, to, fte) in new[] { (FinRootY - 0.3f, RudderFrom, 1f), (RudderFrom, RudderTo, RudderF), (RudderTo, FinTopY, 1f) })
            m.Loft(new[] { FinRing(from, fte), FinRing(to, fte) }, Rep(Hull, 5), Edge);
        // the dorsal fillet ahead of the fin
        m.Loft(new[]
        {
            new[] { new Vector3(0, TopY - 0.05f, -6.5f), new Vector3(0.25f, TopY - 0.15f, -9.6f), new Vector3(-0.25f, TopY - 0.15f, -9.6f) },
            new[] { new Vector3(0, FinRootY + 0.6f, -9.9f), new Vector3(0.25f, FinRootY - 0.1f, -9.9f), new Vector3(-0.25f, FinRootY - 0.1f, -9.9f) },
        }, Rep(Hull, 3), Hull);
        foreach (int sg in new[] { 1, -1 })
        {
            Vector3[] Ring(float x, float fte) => new[] { StabPt(sg, x, 0f, 0), StabPt(sg, x, 0.3f, 1), StabPt(sg, x, fte, 1), StabPt(sg, x, fte, -1), StabPt(sg, x, 0.3f, -1) };
            var edges = new[] { Hull, Hull, Edge, Belly, Belly };
            m.Loft(new[] { Ring(0f, 1f), Ring(ElevatorFrom, 1f) }, edges, Edge);
            m.Loft(new[] { Ring(ElevatorFrom, ElevatorF), Ring(ElevatorTo, ElevatorF) }, edges, Edge);
            m.Loft(new[] { Ring(ElevatorTo, 1f), Ring(StabTipX, 1f) }, edges, Edge);
        }
    }

    private static Node3D Rudder(Material bm, Material gm)
    {
        var p0 = FinPt(RudderFrom, RudderF, 0);
        var p1 = FinPt(RudderTo, RudderF, 0);
        var yAxis = (Flip(p1) - Flip(p0)).Normalized();
        var basis = new Basis(Vector3.Right, yAxis, Vector3.Right.Cross(yAxis));
        var part = new AircraftPart((p0 + p1) * 0.5f, basis);
        Vector3[] Ring(float y) => new[] { FinPt(y, RudderF, 1), FinPt(y, 1f, 1), FinPt(y, 1f, -1), FinPt(y, RudderF, -1) };
        part.Loft(new[] { Ring(RudderFrom), Ring(RudderTo) }, Rep(Hull, 4), Edge);
        return part.ToNode("Rudder", bm, gm);
    }

    /// <summary>A part on a hinge line from <paramref name="inboard"/> to <paramref name="outboard"/>, its local +X to the aircraft's right.</summary>
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

    // ---- the sponsons and the gear ---------------------------------------------------------------

    private static void Sponsons(MeshScratch m)
    {
        foreach (int sg in new[] { 1, -1 })
        {
            Vector3[] Ring(float z, float k)
            {
                float inTop = OuterX(z, SponsonTopY) - 0.03f, inBottom = OuterX(z, SponsonBottomY) - 0.03f;
                (float X, float Y)[] pts =
                {
                    (inTop, SponsonTopY), (SponsonOutX - 0.25f, SponsonTopY - 0.08f), (SponsonOutX, 1.6f),
                    (SponsonOutX - 0.05f, 1.15f), (SponsonOutX - 0.35f, SponsonBottomY), (inBottom, SponsonBottomY),
                };
                var r = new Vector3[pts.Length];
                for (int i = 0; i < r.Length; i++)
                {
                    float x = pts[i].X, y = pts[i].Y;
                    float xin = i == 0 ? inTop : i == pts.Length - 1 ? inBottom : x;
                    // shrunk toward the skin at the ends: the fairing's taper
                    float xs = Mathf.Lerp(OuterX(z, y) - 0.03f, xin, k);
                    float ys = Mathf.Lerp(1.55f, y, Mathf.Max(k, 0.5f));
                    r[i] = new Vector3(sg * xs, ys, z);
                }
                return r;
            }
            m.Loft(new[] { Ring(SponsonFrontZ + 1.2f, 0.15f), Ring(SponsonFrontZ, 1f), Ring(SponsonRearZ, 1f), Ring(SponsonRearZ - 1.6f, 0.1f) },
                Rep(Hull, 6), Hull);
            // the wells' dark slots in the sponson's floor
            m.Box(new Vector3(sg * MainGearX, SponsonBottomY - 0.005f, 0f), new Vector3(0.6f, 0.02f, MainTandem * 2f + MainWheelRadius * 2f), Dark);
        }
        // the nose gear's bay doors
        m.Box(new Vector3(0, BellyLine(NoseGearZ) + 0.33f, NoseGearZ + 0.2f), new Vector3(0.75f, 0.04f, 1.4f), Dark);
    }

    private static Node3D MainGear(string name, int sg, Material bm, Material gm)
    {
        var part = new AircraftPart(new Vector3(sg * MainGearX, 0f, 0f), Basis.Identity);
        float gx = sg * MainGearX;
        part.Tube(new Vector3(gx, SponsonTopY - 0.3f, 0f), new Vector3(gx, MainWheelRadius, 0f), 0.12f, 0.1f, Metal, 8);
        part.Tube(new Vector3(gx, MainWheelRadius, MainTandem), new Vector3(gx, MainWheelRadius, -MainTandem), 0.07f, 0.07f, Metal, 6);
        foreach (float z in new[] { MainTandem, -MainTandem })
        {
            part.Tube(new Vector3(gx - MainWheelWidth / 2, MainWheelRadius, z), new Vector3(gx + MainWheelWidth / 2, MainWheelRadius, z), MainWheelRadius, MainWheelRadius, Tyre, 14);
            part.Tube(new Vector3(gx - MainWheelWidth / 2 - 0.01f, MainWheelRadius, z), new Vector3(gx + MainWheelWidth / 2 + 0.01f, MainWheelRadius, z), 0.36f, 0.36f, Metal, 8);
        }
        return part.ToNode(name, bm, gm);
    }

    private static Node3D NoseGear(Material bm, Material gm)
    {
        var part = new AircraftPart(NoseHinge, Basis.Identity);
        var axle = new Vector3(0, NoseWheelRadius, NoseGearZ);
        part.Tube(NoseHinge, new Vector3(0, 0.95f, NoseGearZ + 0.05f), 0.11f, 0.09f, Metal, 8);
        part.Tube(new Vector3(0, 0.95f, NoseGearZ + 0.05f), axle, 0.07f, 0.07f, Belly, 8);
        part.Tube(new Vector3(-0.25f, NoseWheelRadius, NoseGearZ), new Vector3(0.25f, NoseWheelRadius, NoseGearZ), 0.05f, 0.05f, Metal, 6);
        foreach (float wx in new[] { -0.25f, 0.25f })
        {
            part.Tube(new Vector3(wx - NoseWheelWidth / 2, NoseWheelRadius, NoseGearZ), new Vector3(wx + NoseWheelWidth / 2, NoseWheelRadius, NoseGearZ), NoseWheelRadius, NoseWheelRadius, Tyre, 12);
            part.Tube(new Vector3(wx - NoseWheelWidth / 2 - 0.01f, NoseWheelRadius, NoseGearZ), new Vector3(wx + NoseWheelWidth / 2 + 0.01f, NoseWheelRadius, NoseGearZ), 0.22f, 0.22f, Metal, 8);
        }
        return part.ToNode("GearNose", bm, gm);
    }

    // ---- doors ---------------------------------------------------------------------------------------------

    /// <summary>The skin rows 2..4 (sill to head) between two stations on one side: a side door's leaf.</summary>
    private static void SideLeaf(AircraftPart part, int sg, float z0, float z1, Color inner)
    {
        Vector3[] Ring(float z)
        {
            var r = new Vector3[8];
            for (int j = 0; j < 4; j++)
            {
                r[j] = Po(z, H[2 + j], sg);
                r[7 - j] = Pi(z, H[2 + j], sg);
            }
            return r;
        }
        part.Loft(new[] { Ring(z0), Ring(z1) }, new[] { Hull, Hull, Hull, Edge, inner, inner, inner, Edge }, Edge);
    }

    private static Node3D CrewDoorLeaf(Material bm, Material gm)
    {
        float z0 = CrewDoorZ - DoorWidth * 0.5f, z1 = CrewDoorZ + DoorWidth * 0.5f;
        var part = new AircraftPart(new Vector3(OuterX(CrewDoorZ, FloorY), FloorY, CrewDoorZ), Basis.Identity);
        SideLeaf(part, 1, z0, z1, Lining);
        // the steps on its inside face: they are the stairs when it lies open
        for (int i = 0; i < 4; i++)
        {
            float y = FloorY + 0.25f + i * 0.4f;
            float x = OuterX(CrewDoorZ, y) - FreighterLayout.Skin - 0.08f;
            part.Box(new Vector3(x, y, CrewDoorZ), new Vector3(0.16f, 0.05f, DoorWidth - 0.12f), Metal);
        }
        return part.ToNode("Door0", bm, gm);
    }

    private static Node3D ParaDoorLeaf(int door, Material bm, Material gm)
    {
        var (zc, sg) = Door(door);
        float z0 = zc - DoorWidth * 0.5f, z1 = zc + DoorWidth * 0.5f;
        var part = new AircraftPart(new Vector3(sg * OuterX(z1, 1.9f), FloorY, z1), Basis.Identity);
        SideLeaf(part, sg, z0, z1, Lining);
        // its window
        var p = (Po(zc, 0.05f, sg) + Pi(zc, 0.05f, sg)) * 0.5f;
        part.S.Pane(new[] { part.P(p + new Vector3(0, -0.2f, -0.15f)), part.P(p + new Vector3(0, 0.15f, -0.15f)),
            part.P(p + new Vector3(0, 0.15f, 0.15f)), part.P(p + new Vector3(0, -0.2f, 0.15f)) }, Pane);
        return part.ToNode($"Door{door}", bm, gm);
    }

    /// <summary>The bottom rows between two stations: the ramp's or the upper door's leaf (its top face is the ramp's floor).</summary>
    private static void BellyLeaf(AircraftPart part, float z0, float z1, Color top)
    {
        Vector3[] Ring(float z) => new[] { Po(z, RampRowTop, 1), Po(z, RampRowMid, 1), Po(z, -1f, 1), Po(z, RampRowMid, -1), Po(z, RampRowTop, -1) };
        var zs = Zs(z0, z1);
        var rings = new Vector3[zs.Count][];
        for (int i = 0; i < zs.Count; i++) rings[i] = Ring(zs[i]);
        part.Loft(rings, new[] { Belly, Belly, Belly, Belly, top }, Edge);
    }

    private static Node3D RampLeaf(Material bm, Material gm)
    {
        var part = new AircraftPart(new Vector3(0, FloorY, RampHingeZ), Basis.Identity);
        BellyLeaf(part, RampClosedEndZ, RampHingeZ, Floor);
        // treads across its top face, and its toe plates
        var tilt = new Basis(Vector3.Right, -RampClosedAngle);
        for (int i = 1; i < 9; i++)
        {
            float s = RampLength * i / 9f;
            var at = new Vector3(0, FloorY + s * Mathf.Sin(RampClosedAngle) + 0.01f, RampHingeZ - s * Mathf.Cos(RampClosedAngle));
            part.Box(at, new Vector3(2.6f, 0.025f, 0.06f), Track, tilt);
        }
        var node = part.ToNode("Door3", bm, gm);
        // the toes: two plates hinged at the lip, folded back on the ramp's top face while it is shut
        var lip = new Vector3(0, RampTop(RampClosedEndZ), RampClosedEndZ);
        var toes = new AircraftPart(lip, Basis.Identity);
        var back = new Vector3(0, Mathf.Sin(RampClosedAngle), Mathf.Cos(RampClosedAngle));   // up the ramp, authored
        foreach (float x in new[] { -0.75f, 0.75f })
        {
            var mid = lip + back * (ToeLength * 0.5f) + new Vector3(x, 0.04f, 0);
            toes.Box(mid, new Vector3(1.2f, 0.05f, ToeLength), Metal, tilt);
            for (int i = 1; i < 5; i++)
                toes.Box(lip + back * (ToeLength * i / 5f) + new Vector3(x, 0.075f, 0), new Vector3(1.1f, 0.02f, 0.05f), Track, tilt);
        }
        var toeNode = toes.ToNode("Toes", bm, gm);
        toeNode.Position -= node.Position;
        node.AddChild(toeNode);
        return node;
    }

    private static Node3D UpperDoorLeaf(Material bm, Material gm)
    {
        var part = new AircraftPart(new Vector3(0, RampTop(UpperDoorHingeZ), UpperDoorHingeZ), Basis.Identity);
        BellyLeaf(part, UpperDoorHingeZ, RampClosedEndZ, Lining);
        return part.ToNode("Door3b", bm, gm);
    }

    // ---- marks and lights -------------------------------------------------------------------------------------

    /// <summary>Swiss Air Force marks: a red square with a white cross, on the fin, the flanks and the wings.</summary>
    private static void Marks(MeshScratch m)
    {
        float finY = 8.3f, finZ = FinFront(finY) + (FinRear(finY) - FinFront(finY)) * 0.4f;
        float finX = FinThick(finY) * 0.5f * Prof(0.4f) + 0.01f;
        Square(m, new Vector3(finX, finY, finZ), Vector3.Right, 1.6f);
        Square(m, new Vector3(-finX, finY, finZ), Vector3.Left, 1.6f);
        Square(m, new Vector3(OuterX(-3.6f, 2.8f) + 0.005f, 2.8f, -3.6f), Vector3.Right, 1.1f);
        Square(m, new Vector3(-OuterX(-3.6f, 2.8f) - 0.005f, 2.8f, -3.6f), Vector3.Left, 1.1f);
        foreach (int sg in new[] { 1, -1 })
        {
            float x = 16.5f, z = (WingLeading(x) + WingTrailing(x)) * 0.5f + 0.2f;
            float top = WPt(sg, x, 0.3f, 1).Y + 0.01f, bottom = WPt(sg, x, 0.3f, -1).Y - 0.01f;
            m.Box(new Vector3(sg * x, top, z), new Vector3(1.4f, 0.02f, 1.4f), Red);
            m.Box(new Vector3(sg * x, top + 0.012f, z), new Vector3(0.28f, 0.02f, 0.84f), Colors.White);
            m.Box(new Vector3(sg * x, top + 0.012f, z), new Vector3(0.84f, 0.02f, 0.28f), Colors.White);
            m.Box(new Vector3(sg * x, bottom, z), new Vector3(1.4f, 0.02f, 1.4f), Red);
            m.Box(new Vector3(sg * x, bottom - 0.012f, z), new Vector3(0.28f, 0.02f, 0.84f), Colors.White);
            m.Box(new Vector3(sg * x, bottom - 0.012f, z), new Vector3(0.84f, 0.02f, 0.28f), Colors.White);
        }
    }

    private static void Square(MeshScratch m, Vector3 at, Vector3 normal, float size)
    {
        m.Box(at, new Vector3(0.03f, size, size), Red);
        var front = at + normal * 0.02f;
        m.Box(front, new Vector3(0.03f, size * 0.6f, size * 0.2f), Colors.White);
        m.Box(front, new Vector3(0.03f, size * 0.2f, size * 0.6f), Colors.White);
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
        float tipY = WingY(WingTipX);
        float tipZ = (WingLeading(WingTipX) + WingTrailing(WingTipX)) * 0.5f;
        Add("NavL", new Vector3(WingTipX + 0.27f, tipY, tipZ + 0.6f), new Vector3(0.14f, 0.12f, 0.2f), red);
        Add("NavR", new Vector3(-(WingTipX + 0.27f), tipY, tipZ + 0.6f), new Vector3(0.14f, 0.12f, 0.2f), green);
        Add("NavTail", new Vector3(0, 4.35f, TailZ - 0.06f), new Vector3(0.14f, 0.16f, 0.1f), white);
        Add("BeaconTop", new Vector3(0, FinTopY + 0.08f, FinTopFrontZ - 1.2f), new Vector3(0.22f, 0.14f, 0.22f), red);
        Add("BeaconBottom", new Vector3(0, BellyY - 0.05f, 1.0f), new Vector3(0.22f, 0.1f, 0.22f), red);
        Add("StrobeL", new Vector3(WingTipX + 0.2f, tipY, tipZ - 0.4f), new Vector3(0.12f, 0.1f, 0.12f), white);
        Add("StrobeR", new Vector3(-(WingTipX + 0.2f), tipY, tipZ - 0.4f), new Vector3(0.12f, 0.1f, 0.12f), white);
        const float lx = 7.5f;
        Add("LandingL", new Vector3(lx, WingY(lx) - 0.3f, WingLeading(lx) - 0.2f), new Vector3(0.3f, 0.12f, 0.1f), white);
        Add("LandingR", new Vector3(-lx, WingY(lx) - 0.3f, WingLeading(lx) - 0.2f), new Vector3(0.3f, 0.12f, 0.1f), white);
    }
}
