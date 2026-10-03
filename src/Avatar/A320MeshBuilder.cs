using Godot;
using static UnitSport.Avatar.A320Layout;

namespace UnitSport.Avatar;

/// <summary>
/// The Airbus A320 (#414), built from code like every avatar vehicle: low-poly, flat-shaded, one
/// <c>Body</c> mesh for everything that stays put plus small hinge nodes for what moves. All numbers
/// come from <see cref="A320Layout"/>; the fuselage is a hollow ring of thin slabs (solid from inside,
/// real holes for the doors, the windows and the flight-deck glass), the cabin, its 27 rows of seats,
/// the galleys, lavatories and the flight deck are drawn inside it.
///
/// <para>
/// Authored like the rest of the avatar meshes (+Z forward, +X the left side); <see cref="Build"/>
/// returns the tree in NODE space (−Z forward, +X right, origin on the ground between the main wheels).
/// Every hinge node's own frame is in node space: a wing surface's local +X runs along its hinge line
/// towards the aircraft's right, so a positive turn about it drops the trailing edge on both wings.
/// </para>
/// </summary>
public static class A320MeshBuilder
{
    // ---- palette -------------------------------------------------------------------------
    private static readonly Color White = new(0.94f, 0.95f, 0.96f);
    private static readonly Color Belly = new(0.58f, 0.60f, 0.64f);
    private static readonly Color WingTop = new(0.80f, 0.82f, 0.85f);
    private static readonly Color WingUnder = new(0.62f, 0.64f, 0.68f);
    private static readonly Color Surface = new(0.74f, 0.76f, 0.80f);
    private static readonly Color Edge = new(0.50f, 0.52f, 0.56f);
    private static readonly Color Dark = new(0.10f, 0.11f, 0.13f);
    private static readonly Color Grey = new(0.46f, 0.48f, 0.52f);
    private static readonly Color Cream = new(0.88f, 0.87f, 0.83f);
    private static readonly Color Carpet = new(0.30f, 0.32f, 0.38f);
    private static readonly Color Upholstery = new(0.30f, 0.37f, 0.50f);
    private static readonly Color UpholsteryDark = new(0.20f, 0.22f, 0.27f);
    private static readonly Color PanelGrey = new(0.22f, 0.23f, 0.26f);
    private static readonly Color Screen = new(0.03f, 0.05f, 0.08f);
    private static readonly Color Pane = new(0.55f, 0.68f, 0.75f, 0.35f);
    private static readonly Color Hull = new(0.45f, 0.46f, 0.50f);

    private static Vector3 Flip(Vector3 v) => AircraftMeshBuilder.Flip(v);

    // ---- the public contract --------------------------------------------------------------

    /// <summary>
    /// How far to turn a gear hinge node to stow that leg, radians. The mains fold inward about their local Z
    /// (the left leg, on the −X side, turns positive: the wheel end swings to +X and up; the right one the
    /// other way); the nose leg folds forward about its local X (positive swings the wheel end to −Z).
    /// </summary>
    public static float GearStowAngle(string node) => node switch
    {
        "GearMainL" => 1.6f,
        "GearMainR" => -1.6f,
        "GearNose" => 1.9f,
        _ => 0f,
    };

    /// <summary>
    /// Turn about a door hinge node's local Y that opens door <paramref name="i"/> (0 L1, 1 R1, 2 L2, 3 R2). The
    /// hinge is the door's forward edge and the leaf extends aft (+Z), so a positive turn swings the aft edge to
    /// +X: right doors are positive, left doors negative; about 115°, the leaf ending out and forward.
    /// </summary>
    public static float DoorOpenAngle(int i) => (i % 2 == 0 ? -1f : 1f) * 2.0f;

    /// <summary>The whole aircraft, node space, ready to add to a scene.</summary>
    public static Node3D Build(Color tailPaint)
    {
        var bodyMat = HumanMeshBuilder.FigureMaterial();
        var glassMat = CarRig.GlassMaterial();
        var root = new Node3D { Name = "A320" };

        var b = new MeshScratch();
        Fuselage(b, tailPaint);
        Cabin(b);
        Cockpit(b);
        Wings(b);
        Engines(b);
        Tail(b, tailPaint);
        Underside(b);
        var body = new MeshInstance3D { Name = "Body", Mesh = b.Build() };
        root.AddChild(body);
        MeshScratch.Paint(body, bodyMat, glassMat);

        foreach (int sg in new[] { 1, -1 })
        {
            string s = sg > 0 ? "L" : "R";
            root.AddChild(Flap($"FlapIn{s}", sg, InnerFlapFrom, InnerFlapTo, bodyMat, glassMat));
            root.AddChild(Flap($"FlapOut{s}", sg, OuterFlapFrom, OuterFlapTo, bodyMat, glassMat));
            root.AddChild(Flap($"Aileron{s}", sg, AileronFrom, AileronTo, bodyMat, glassMat));
            root.AddChild(Spoilers($"Spoiler{s}", sg, bodyMat, glassMat));
            root.AddChild(Elevator($"Elevator{s}", sg, bodyMat, glassMat));
            root.AddChild(MainGear($"GearMain{s}", sg, bodyMat, glassMat));
            root.AddChild(FanNode($"Fan{(sg > 0 ? 0 : 1)}", sg, bodyMat));
        }
        root.AddChild(Rudder(tailPaint, bodyMat, glassMat));
        root.AddChild(NoseGear(bodyMat, glassMat));
        for (int i = 0; i < DoorCount; i++) root.AddChild(DoorLeaf(i, bodyMat, glassMat));
        AddLights(root);
        return root;
    }

    // ---- small helpers --------------------------------------------------------------------

    private static Color[] Rep(Color c, int n)
    {
        var a = new Color[n];
        Array.Fill(a, c);
        return a;
    }

    /// <summary>Basis whose X runs along <paramref name="along"/>, Y as close to up as it can be (node space).</summary>
    private static Basis HingeBasis(Vector3 along)
    {
        var x = along.Normalized();
        var y = (Vector3.Up - x * x.Dot(Vector3.Up)).Normalized();
        return new Basis(x, y, x.Cross(y));
    }

    /// <summary>
    /// A moving part: geometry authored in the aircraft's frame, stored in the hinge node's own frame. The
    /// node sits at <paramref name="pivotAuth"/> (flipped) turned by <paramref name="basis"/>.
    /// </summary>
    private sealed class Part
    {
        public readonly MeshScratch S = new();
        private readonly Vector3 _pivot;
        private readonly Basis _basis, _inverse;

        public Part(Vector3 pivotAuth, Basis basis)
        {
            _pivot = Flip(pivotAuth);
            _basis = basis;
            _inverse = basis.Inverse();
        }

        /// <summary>Authored point of the aircraft to the scratch's coordinates (flipped again by Build).</summary>
        public Vector3 P(Vector3 auth) => Flip(_inverse * (Flip(auth) - _pivot));

        public Vector3[] P(Vector3[] ring)
        {
            var r = new Vector3[ring.Length];
            for (int i = 0; i < r.Length; i++) r[i] = P(ring[i]);
            return r;
        }

        public void Loft(Vector3[][] rings, Color[] edges, Color cap)
        {
            var local = new Vector3[rings.Length][];
            for (int i = 0; i < rings.Length; i++) local[i] = P(rings[i]);
            S.Loft(local, edges, cap);
        }

        public void Tube(Vector3 a, Vector3 b, float ra, float rb, Color c, int sides = 8) => S.Tube(P(a), P(b), ra, rb, c, sides);

        public void Box(Vector3 centre, Vector3 size, Color c) => S.Box(P(centre), size, c);

        public Node3D ToNode(string name, Material body, Material glass)
        {
            var node = new Node3D { Name = name, Transform = new Transform3D(_basis, _pivot) };
            var mi = new MeshInstance3D { Name = name + "Mesh", Mesh = S.Build() };
            node.AddChild(mi);
            MeshScratch.Paint(mi, body, glass);
            return node;
        }
    }

    // ---- the fuselage ---------------------------------------------------------------------

    /// <summary>Heights (authored y) of the skin's rows, belly to crown; row k lies between Levels[k] and Levels[k + 1].</summary>
    private static readonly float[] Levels = { 2.0f, 2.35f, 2.8f, 3.3f, 3.72f, 3.9f, 4.12f, 4.47f, 5.15f, 5.65f, 6.0f, 6.14f };
    private static readonly float[] H = MakeH();
    private const int StripeRow = 4, WindowRow = 6;

    private static float[] MakeH()
    {
        var h = new float[Levels.Length];
        for (int i = 0; i < h.Length; i++) h[i] = Mathf.Clamp((Levels[i] - CentreY) / HalfHeight, -1f, 1f);
        h[0] = -1f;
        h[^1] = 1f;
        return h;
    }

    /// <summary>The oval at station <paramref name="z"/>: half width, centre height, half height.</summary>
    private static (float A, float Cy, float B) Section(float z)
    {
        if (z > BarrelFront)
        {
            float t = Mathf.Clamp((z - BarrelFront) / (NoseZ - BarrelFront), 0f, 1f);
            float top = TopY - 1.6f * t * t * t * t;
            float bottom = BellyY + 1.2f * t * t;
            return (Mathf.Max(0.1f, HalfWidth * Mathf.Sqrt(1f - t * t)), (top + bottom) / 2f, (top - bottom) / 2f);
        }
        if (z < BarrelRear)
        {
            float u = Mathf.Clamp((BarrelRear - z) / (BarrelRear - TailZ), 0f, 1f);
            float s = 1f - 0.88f * Mathf.Pow(u, 1.5f);
            return (HalfWidth * s, CentreY + 1.9f * u * u, HalfHeight * s);
        }
        return (HalfWidth, CentreY, HalfHeight);
    }

    private static Vector3 Po(float z, float h, int sg)
    {
        var (a, cy, b) = Section(z);
        return new Vector3(sg * a * Mathf.Sqrt(Mathf.Max(0f, 1f - h * h)), cy + b * h, z);
    }

    private static Vector3 Pi(float z, float h, int sg)
    {
        var (a, cy, b) = Section(z);
        float ai = Mathf.Max(a - A320Layout.Skin, 0.02f), bi = Mathf.Max(b - A320Layout.Skin, 0.02f);
        return new Vector3(sg * ai * Mathf.Sqrt(Mathf.Max(0f, 1f - h * h)), cy + bi * h, z);
    }

    /// <summary>Half width of the cabin's inner surface at (z, y); 0 outside it.</summary>
    private static float InnerX(float z, float y)
    {
        var (a, cy, b) = Section(z);
        float ai = Mathf.Max(a - A320Layout.Skin, 0.02f), bi = Mathf.Max(b - A320Layout.Skin, 0.02f);
        float h = (y - cy) / bi;
        return Mathf.Abs(h) >= 1f ? 0f : ai * Mathf.Sqrt(1f - h * h);
    }

    private static readonly float[] Stations = MakeStations();

    private static float[] MakeStations()
    {
        var l = new List<float> { BarrelRear, BarrelFront };
        foreach (float u in new[] { 0f, .1f, .2f, .3f, .4f, .5f, .6f, .7f, .8f, .9f, .96f, 1f }) l.Add(BarrelRear - (BarrelRear - TailZ) * u);
        foreach (float t in new[] { 0f, .1f, .2f, .3f, .4f, .5f, .6f, .7f, .8f, .88f, .94f, .985f, 1f }) l.Add(BarrelFront + (NoseZ - BarrelFront) * t);
        l.Sort();
        var r = new List<float>();
        foreach (float z in l) if (r.Count == 0 || z - r[^1] > 0.01f) r.Add(z);
        return r.ToArray();
    }

    /// <summary>The stations to draw between <paramref name="a"/> and <paramref name="b"/>, both included.</summary>
    private static List<float> Zs(float a, float b)
    {
        var r = new List<float> { a };
        foreach (float z in Stations) if (z > a + 0.01f && z < b - 0.01f) r.Add(z);
        r.Add(b);
        return r;
    }

    private static Color RowColour(int k, Color tail) => k < 2 ? Belly : k == StripeRow ? tail : White;
    private static Color InnerColour(int k) => k < 3 ? Hull : Cream;

    private static void Fuselage(MeshScratch m, Color tail)
    {
        foreach (int sg in new[] { 1, -1 })
            for (int k = 0; k < Levels.Length - 1; k++)
                SkinRow(m, sg, k, tail);

        // the rear pressure bulkhead: a thin slab filling the inner oval
        var (a, cy, bb) = Section(RearBulkheadZ);
        var ring = new Vector3[12];
        for (int j = 0; j < 12; j++)
        {
            float ang = Mathf.Tau * j / 12f;
            ring[j] = new Vector3((a - A320Layout.Skin) * 0.99f * Mathf.Cos(ang), cy + (bb - A320Layout.Skin) * 0.99f * Mathf.Sin(ang), RearBulkheadZ);
        }
        var ring2 = new Vector3[12];
        for (int j = 0; j < 12; j++) ring2[j] = ring[j] + new Vector3(0, 0, -0.1f);
        m.Loft(new[] { ring, ring2 }, Rep(Cream, 12), Cream);

        // the APU exhaust at the very tip of the tail cone
        float tipY = CentreY + 1.9f;
        m.Tube(new Vector3(0, tipY, TailZ + 0.2f), new Vector3(0, tipY, TailZ - 0.18f), 0.17f, 0.14f, Dark, 8);
    }

    private readonly record struct Hole(float From, float To, bool Pane);

    private static List<Hole> HolesFor(int sg, int k)
    {
        var holes = new List<Hole>();
        if (k >= 3 && k <= 7)
            for (int i = 0; i < DoorCount; i++)
            {
                var (zc, side) = Door(i);
                if (side == sg) holes.Add(new Hole(zc - DoorWidth / 2, zc + DoorWidth / 2, false));
            }
        if (k == WindowRow)
        {
            for (int j = 0; ; j++)
            {
                float z = FirstWindowZ - j * WindowPitch;
                if (z < LastWindowZ - 0.001f) break;
                bool skip = z > OverwingExitZ[1] - 0.45f - 0.15f && z < OverwingExitZ[0] + 0.45f;
                for (int i = 0; i < DoorCount; i++)
                    if (Mathf.Abs(z - Door(i).Z) < DoorWidth / 2 + WindowWidth / 2 + 0.1f) skip = true;
                if (!skip) holes.Add(new Hole(z - WindowWidth / 2, z + WindowWidth / 2, true));
            }
        }
        // the flight deck: side windows, and the windscreen over the nose
        if (k == 7) holes.Add(new Hole(15.2f, 17.3f, true));
        if (k == 8) holes.Add(new Hole(16.0f, 17.3f, true));
        holes.Sort((x, y) => x.From.CompareTo(y.From));
        return holes;
    }

    private static void SkinRow(MeshScratch m, int sg, int k, Color tail)
    {
        var c = RowColour(k, tail);
        float z = TailZ;
        foreach (var h in HolesFor(sg, k))
        {
            Slab(m, sg, k, z, h.From, c);
            z = h.To;
            if (h.Pane) PaneRow(m, sg, k, h.From, h.To);
        }
        Slab(m, sg, k, z, NoseZ, c);
    }

    private static void Slab(MeshScratch m, int sg, int k, float z0, float z1, Color c)
    {
        if (z1 - z0 < 0.02f) return;
        var zs = Zs(z0, z1);
        var rings = new Vector3[zs.Count][];
        for (int i = 0; i < zs.Count; i++)
            rings[i] = new[] { Po(zs[i], H[k], sg), Po(zs[i], H[k + 1], sg), Pi(zs[i], H[k + 1], sg), Pi(zs[i], H[k], sg) };
        m.Loft(rings, new[] { c, c, InnerColour(k), c }, c);
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

    // ---- the cabin ------------------------------------------------------------------------

    /// <summary>A prism along z hugging the inner wall: from <paramref name="xIn"/> out to the wall (clamped to <paramref name="xOut"/>).</summary>
    private static void Block(MeshScratch m, int sg, float z0, float z1, float xIn, float y0, float y1, Color c, float xOut = 9f)
    {
        var zs = Zs(Mathf.Min(z0, z1), Mathf.Max(z0, z1));
        var rings = new Vector3[zs.Count][];
        float ym = (y0 + y1) * 0.5f;
        for (int i = 0; i < zs.Count; i++)
        {
            float z = zs[i];
            float Xo(float y) => Mathf.Max(xIn + 0.05f, Mathf.Min(xOut, InnerX(z, y) - 0.015f));
            rings[i] = new[]
            {
                new Vector3(sg * xIn, y0, z), new Vector3(sg * Xo(y0), y0, z), new Vector3(sg * Xo(ym), ym, z),
                new Vector3(sg * Xo(y1), y1, z), new Vector3(sg * xIn, y1, z),
            };
        }
        m.Loft(rings, Rep(c, 5), c);
    }

    private static void Cabin(MeshScratch m)
    {
        // floor from the rear bulkhead to the flight deck's front, narrowing with the nose
        {
            var zs = Zs(RearBulkheadZ, CockpitFrontZ);
            var rings = new Vector3[zs.Count][];
            for (int i = 0; i < zs.Count; i++)
            {
                float z = zs[i];
                float w = Mathf.Max(0.1f, InnerX(z, FloorY) - 0.01f), w2 = Mathf.Max(0.1f, InnerX(z, FloorY - 0.12f) - 0.01f);
                rings[i] = new[] { new Vector3(-w, FloorY, z), new Vector3(w, FloorY, z), new Vector3(w2, FloorY - 0.12f, z), new Vector3(-w2, FloorY - 0.12f, z) };
            }
            m.Loft(rings, new[] { Carpet, Hull, Hull, Hull }, Hull);
        }
        // ceiling
        {
            var zs = Zs(RearBulkheadZ, CockpitWallZ);
            var rings = new Vector3[zs.Count][];
            for (int i = 0; i < zs.Count; i++)
            {
                float z = zs[i];
                float w = Mathf.Max(0.1f, InnerX(z, CeilingY + 0.05f) - 0.01f);
                rings[i] = new[] { new Vector3(-w, CeilingY, z), new Vector3(w, CeilingY, z), new Vector3(w, CeilingY + 0.05f, z), new Vector3(-w, CeilingY + 0.05f, z) };
            }
            m.Loft(rings, Rep(Cream, 4), Cream);
        }
        // overhead bins, both sides
        foreach (int sg in new[] { 1, -1 })
            Block(m, sg, -9.3f, 11.2f, 0.62f, 4.95f, 5.35f, Cream);

        // seats: 27 rows of 3-3
        for (int r = 0; r < Rows; r++)
        {
            float zf = RowZ(r);
            for (int i = 0; i < 6; i++)
            {
                float x = SeatX[i];
                m.Box(new Vector3(x, FloorY + SeatHeight - 0.06f, zf - 0.225f), new Vector3(SeatWidth - 0.02f, 0.12f, 0.45f), Upholstery);
                m.Box(new Vector3(x, FloorY + (SeatHeight - 0.05f + SeatBackHeight) * 0.5f, zf - 0.5f), new Vector3(SeatWidth - 0.02f, SeatBackHeight - SeatHeight + 0.05f, 0.09f),
                    Upholstery, new Basis(Vector3.Right, -0.12f));
            }
            foreach (int half in new[] { 0, 3 })
            {
                float xc = (SeatX[half] + SeatX[half + 2]) * 0.5f;
                float w = SeatX[half] - SeatX[half + 2] + SeatWidth;
                m.Box(new Vector3(xc, FloorY + 0.17f, zf - 0.225f), new Vector3(w * 0.8f, 0.3f, 0.08f), UpholsteryDark);
                foreach (float edge in new[] { SeatX[half] + SeatWidth * 0.5f - 0.02f, SeatX[half + 2] - SeatWidth * 0.5f + 0.02f })
                    m.Box(new Vector3(edge, FloorY + SeatHeight + 0.1f, zf - 0.23f), new Vector3(0.04f, 0.05f, 0.4f), UpholsteryDark);
            }
        }

        // forward lavatory (left, +X), forward galley (right)
        Block(m, 1, ForwardLavFrom, ForwardLavTo, 0.55f, FloorY, FloorY + 1.95f, Cream);
        LavDoor(m, 1, 0.55f, (ForwardLavFrom + ForwardLavTo) * 0.5f);
        Block(m, -1, ForwardGalleyFrom, ForwardGalleyTo, 0.5f, FloorY, FloorY + 1.9f, Cream);
        for (int i = 0; i < 3; i++)
            m.Box(new Vector3(-0.5f + 0.006f, FloorY + 0.45f + i * 0.5f, (ForwardGalleyFrom + ForwardGalleyTo) * 0.5f), new Vector3(0.012f, 0.4f, 0.55f), UpholsteryDark);
        // aft lavatories and galleys, both sides
        foreach (int sg in new[] { 1, -1 })
        {
            Block(m, sg, AftLavFrom, AftLavTo, 0.55f, FloorY, FloorY + 1.95f, Cream);
            LavDoor(m, sg, 0.55f, (AftLavFrom + AftLavTo) * 0.5f);
            Block(m, sg, AftGalleyFrom, AftGalleyTo, 0.4f, FloorY, FloorY + 1.9f, Cream);
            for (int i = 0; i < 3; i++)
                m.Box(new Vector3(sg * (0.4f - 0.006f), FloorY + 0.45f + i * 0.5f, (AftGalleyFrom + AftGalleyTo) * 0.5f), new Vector3(0.012f, 0.4f, 0.9f), UpholsteryDark);
        }
    }

    private static void LavDoor(MeshScratch m, int sg, float x, float z) =>
        m.Box(new Vector3(sg * (x - 0.006f), FloorY + 0.95f, z), new Vector3(0.012f, 1.75f, 0.6f), Grey);

    // ---- the flight deck ---------------------------------------------------------------------

    private static void Cockpit(MeshScratch m)
    {
        // the wall behind it, with the door opening
        foreach (int sg in new[] { 1, -1 })
            Block(m, sg, CockpitWallZ - 0.05f, CockpitWallZ + 0.05f, CockpitDoorWidth * 0.5f, FloorY, CeilingY, Cream);
        m.Box(new Vector3(0, (FloorY + 1.9f + CeilingY) * 0.5f, CockpitWallZ), new Vector3(CockpitDoorWidth, CeilingY - FloorY - 1.9f, 0.1f), Cream);

        // seats
        foreach (var hip in new[] { CaptainHip, FirstOfficerHip })
        {
            float x = hip.X;
            m.Box(new Vector3(x, FloorY + 0.14f, hip.Z), new Vector3(0.22f, 0.28f, 0.3f), UpholsteryDark);
            m.Box(new Vector3(x, hip.Y - 0.1f, hip.Z + 0.1f), new Vector3(0.5f, 0.12f, 0.5f), Upholstery);
            m.Box(new Vector3(x, hip.Y + 0.3f, hip.Z - 0.2f), new Vector3(0.5f, 0.85f, 0.1f), Upholstery, new Basis(Vector3.Right, -0.12f));
            m.Box(new Vector3(x, hip.Y + 0.82f, hip.Z - 0.24f), new Vector3(0.3f, 0.2f, 0.09f), Upholstery);
            foreach (float s in new[] { -1f, 1f })
                m.Box(new Vector3(x + s * 0.27f, hip.Y + 0.1f, hip.Z + 0.1f), new Vector3(0.05f, 0.05f, 0.4f), UpholsteryDark);
        }

        // side consoles with the sidesticks
        foreach (int sg in new[] { 1, -1 })
        {
            m.Box(new Vector3(sg * 1.0f, FloorY + 0.4f, 15.95f), new Vector3(0.2f, 0.4f, 0.7f), PanelGrey);
            m.Tube(new Vector3(sg * 1.0f, FloorY + 0.62f, 15.85f), new Vector3(sg * 0.95f, FloorY + 0.9f, 15.95f), 0.025f, 0.02f, Dark, 5);
        }
        // centre pedestal, two thrust levers
        m.Box(new Vector3(0, FloorY + 0.22f, 15.95f), new Vector3(0.3f, 0.44f, 0.7f), PanelGrey);
        foreach (float s in new[] { -1f, 1f })
            m.Tube(new Vector3(s * 0.06f, FloorY + 0.44f, 15.75f), new Vector3(s * 0.06f, FloorY + 0.66f, 15.92f), 0.02f, 0.015f, Dark, 5);

        // instrument panel with dark screens, glareshield
        const float panelZ = 16.55f;
        float hw = Mathf.Min(0.95f, InnerX(panelZ, 4.55f) - 0.04f);
        m.Box(new Vector3(0, 4.2f, panelZ), new Vector3(hw * 2, 0.75f, 0.12f), PanelGrey);
        for (int i = 0; i < 6; i++)
        {
            float x = (i % 3 - 1) * hw * 0.62f;
            m.Box(new Vector3(x, i < 3 ? 4.38f : 4.06f, panelZ - 0.065f), new Vector3(hw * 0.5f, 0.24f, 0.02f), Screen);
        }
        float gw = Mathf.Min(0.95f, InnerX(16.7f, 4.68f) - 0.04f);
        m.Box(new Vector3(0, 4.64f, 16.7f), new Vector3(gw * 2, 0.06f, 0.4f), Dark);
    }

    // ---- wings -------------------------------------------------------------------------------

    private const float LeSlope = (WingRootLeadingZ - WingTipLeadingZ) / (WingTipX - WingRootX);

    private static float Le(float x) => WingRootLeadingZ - (x - WingRootX) * LeSlope;

    private static float Te(float x) =>
        x <= WingRootX ? WingRootTrailingZ
        : x <= WingKinkX ? Mathf.Lerp(WingRootTrailingZ, WingKinkTrailingZ, (x - WingRootX) / (WingKinkX - WingRootX))
        : Mathf.Lerp(WingKinkTrailingZ, WingTipTrailingZ, (x - WingKinkX) / (WingTipX - WingKinkX));

    private static float Thick(float x) => Mathf.Lerp(WingRootThickness, WingTipThickness, Mathf.Clamp((x - WingRootX) / (WingTipX - WingRootX), 0f, 1f));
    private static float Ymid(float x) => x <= WingRootX ? WingRootY : WingY(x);

    /// <summary>Thickness profile along the chord, 0..1 of the section's thickness.</summary>
    private static float Prof(float f)
    {
        f = Mathf.Clamp(f, 0f, 1f);
        if (f < 0.12f) return Mathf.Lerp(0.25f, 0.78f, f / 0.12f);
        if (f < 0.3f) return Mathf.Lerp(0.78f, 1f, (f - 0.12f) / 0.18f);
        if (f < 0.72f) return Mathf.Lerp(1f, 0.55f, (f - 0.3f) / 0.42f);
        return Mathf.Lerp(0.55f, 0.06f, (f - 0.72f) / 0.28f);
    }

    /// <summary>A wing point: span <paramref name="x"/> (|x|, side <paramref name="sg"/>), chord fraction, v = +1 upper surface / −1 lower / 0 mid.</summary>
    private static Vector3 WPt(int sg, float x, float f, int v)
    {
        float zl = Le(x), zt = Te(x);
        float z = zl + (zt - zl) * f;
        return new Vector3(sg * x, SurfaceY(x, f, v), z);
    }

    private static float SurfaceY(float x, float f, int v)
    {
        float t = Thick(x), p = Prof(f);
        return Ymid(x) + (v > 0 ? t * 0.55f * p : v < 0 ? -t * 0.45f * p : 0f);
    }

    /// <summary>The upper surface's height at span x and station z.</summary>
    private static float UpperAtZ(float x, float z)
    {
        float zl = Le(x), zt = Te(x);
        return SurfaceY(x, (z - zl) / (zt - zl), 1);
    }

    private static Vector3[] WingRing(int sg, float x, float fte) => new[]
    {
        WPt(sg, x, 0f, 0), WPt(sg, x, 0.12f, 1), WPt(sg, x, 0.35f, 1), WPt(sg, x, 0.85f * fte, 1), WPt(sg, x, fte, 1),
        WPt(sg, x, fte, -1), WPt(sg, x, 0.85f * fte, -1), WPt(sg, x, 0.35f, -1), WPt(sg, x, 0.12f, -1),
    };

    private static void Wings(MeshScratch m)
    {
        var edges = new[] { WingTop, WingTop, WingTop, WingTop, Edge, WingUnder, WingUnder, WingUnder, WingUnder };
        // the fixed wing stops at the flaps' hinge line except where nothing moves
        var segments = new (float From, float To, float Fte)[]
        {
            (0.6f, InnerFlapFrom, 1f), (InnerFlapFrom, InnerFlapTo, 0.72f), (InnerFlapTo, OuterFlapFrom, 1f),
            (OuterFlapFrom, OuterFlapTo, 0.72f), (OuterFlapTo, AileronFrom, 1f), (AileronFrom, AileronTo, 0.72f),
            (AileronTo, WingTipX, 1f),
        };
        foreach (int sg in new[] { 1, -1 })
        {
            foreach (var s in segments)
                m.Loft(new[] { WingRing(sg, s.From, s.Fte), WingRing(sg, s.To, s.Fte) }, edges, Edge);

            // sharklet: a canted, swept vertical fin on the tip
            var sh = new Vector3[3][];
            for (int j = 0; j < 3; j++)
            {
                float h = SharkletHeight * j / 2f;
                float xc = WingTipX + 0.12f * h, y = Ymid(WingTipX) + h;
                float zle = WingTipLeadingZ - 0.55f * h, zte = WingTipTrailingZ - 0.15f * h;
                float t = 0.2f - 0.1f * (h / SharkletHeight);
                float zq = zle + 0.35f * (zte - zle);
                sh[j] = new[]
                {
                    new Vector3(sg * xc, y, zle), new Vector3(sg * (xc + t / 2), y, zq), new Vector3(sg * (xc + t / 2), y, zte),
                    new Vector3(sg * (xc - t / 2), y, zte), new Vector3(sg * (xc - t / 2), y, zq),
                };
            }
            m.Loft(sh, Rep(WingTop, 5), WingTop);

            // flap-track fairings under the trailing edge
            foreach (float x in new[] { 3.5f, 5.2f, 7.6f, 10.0f, 12.2f, 14.6f })
            {
                var a = WPt(sg, x, 0.6f, -1) + new Vector3(0, -0.06f, 0);
                var b = new Vector3(sg * x, a.Y - 0.1f, Te(x) - 0.45f);
                m.Tube(a, b, 0.12f, 0.035f, Belly, 6);
            }

            // main gear bay doors: dark patches on the underside
            var bayA = WPt(sg, MainGearX, 0.3f, -1);
            m.Box(new Vector3(sg * MainGearX, bayA.Y, bayA.Z - 0.3f), new Vector3(0.9f, 0.05f, 1.6f), Dark);
        }
    }

    // ---- engines -----------------------------------------------------------------------------

    private static void Engines(MeshScratch m)
    {
        foreach (int sg in new[] { 1, -1 })
        {
            float x = sg * EngineX, y = EngineY;
            Vector3 V(float z) => new(x, y, z);
            // the cowl: lip, barrel, the taper into the core
            m.Skirt(V(EngineInletZ), V(6.8f), 1.0f, NacelleRadius, White, 14);
            m.Skirt(V(6.8f), V(4.6f), NacelleRadius, 0.95f, White, 14);
            m.Skirt(V(4.6f), V(4.1f), 0.95f, 0.62f, White, 14);
            m.Tube(V(4.1f), V(3.4f), 0.62f, 0.48f, Hull, 12);
            m.Tube(V(3.6f), V(EngineExhaustZ - 0.15f), 0.3f, 0.03f, Dark, 8);
            // dark intake lining
            m.Skirt(V(EngineInletZ - 0.01f), V(7.1f), 0.97f, 0.96f, Dark, 14);
            m.Ring(V(7.1f), Vector3.Forward, 0.84f, 0.97f, 0.03f, Dark, 14);
            // pylon up into the wing's leading edge
            float py = y + NacelleRadius;
            m.Loft(new[]
            {
                new[] { new Vector3(x - 0.1f, py - 0.4f, 6.4f), new Vector3(x + 0.1f, py - 0.4f, 6.4f), new Vector3(x + 0.1f, py, 6.4f), new Vector3(x - 0.1f, py, 6.4f) },
                new[] { new Vector3(x - 0.1f, py - 0.4f, 3.3f), new Vector3(x + 0.1f, py - 0.4f, 3.3f), new Vector3(x + 0.1f, py + 0.16f, 3.3f), new Vector3(x - 0.1f, py + 0.16f, 3.3f) },
            }, Rep(Hull, 4), Hull);
        }
    }

    private static Node3D FanNode(string name, int sg, Material bodyMat)
    {
        var f = new MeshScratch();
        f.Tube(new Vector3(0, 0, -0.04f), new Vector3(0, 0, 0.02f), FanRadius, FanRadius, new Color(0.14f, 0.15f, 0.17f), 14);
        f.Tube(new Vector3(0, 0, 0f), new Vector3(0, 0, 0.5f), 0.17f, 0.02f, White, 10);
        for (int i = 0; i < 12; i++)
        {
            float ang = Mathf.Tau * i / 12f;
            f.Box(new Vector3(Mathf.Cos(ang) * 0.5f, Mathf.Sin(ang) * 0.5f, 0.03f), new Vector3(0.64f, 0.17f, 0.03f), new Color(0.62f, 0.64f, 0.68f),
                new Basis(Vector3.Back, ang) * new Basis(Vector3.Right, 0.35f));
        }
        var node = new Node3D { Name = name, Position = Flip(new Vector3(sg * EngineX, EngineY, FanZ)) };
        var mi = new MeshInstance3D { Name = name + "Mesh", Mesh = f.Build(), MaterialOverride = bodyMat };
        node.AddChild(mi);
        return node;
    }

    // ---- the tail -------------------------------------------------------------------------------

    private static float FinFront(float y) => Mathf.Lerp(FinRootFrontZ, FinTopFrontZ, (y - FinRootY) / (FinTopY - FinRootY));
    private static float FinRear(float y) => Mathf.Lerp(FinRootRearZ, FinTopRearZ, (y - FinRootY) / (FinTopY - FinRootY));
    private static float FinThick(float y) => Mathf.Lerp(0.34f, 0.14f, Mathf.Clamp((y - FinRootY) / (FinTopY - FinRootY), 0f, 1f));

    private static Vector3 FinPt(float y, float f, int v) =>
        new(v * FinThick(y) * 0.5f * Prof(f), y, FinFront(y) + f * (FinRear(y) - FinFront(y)));

    private const float RudderFrom = 6.6f, RudderTo = 11.5f, RudderF = 0.73f;

    private static float StabLe(float x) => Mathf.Lerp(StabRootFrontZ, StabTipFrontZ, (x - StabRootX) / (StabTipX - StabRootX));
    private static float StabTe(float x) => Mathf.Lerp(StabRootRearZ, StabTipRearZ, (x - StabRootX) / (StabTipX - StabRootX));
    private static float StabThick(float x) => Mathf.Lerp(0.22f, 0.1f, Mathf.Clamp(x / StabTipX, 0f, 1f));

    private static Vector3 StabPt(int sg, float x, float f, int v)
    {
        float zl = StabLe(x), zt = StabTe(x), t = StabThick(x), p = Prof(f);
        float y = StabY + x * 0.035f + (v > 0 ? t * 0.55f * p : v < 0 ? -t * 0.45f * p : 0f);
        return new Vector3(sg * x, y, zl + (zt - zl) * f);
    }

    private const float ElevatorFrom = 0.9f, ElevatorTo = 6.0f, ElevatorF = 0.68f;

    private static void Tail(MeshScratch m, Color tail)
    {
        // fin: a root block, the fixed part, the tip
        Vector3[] FinRing(float y, float fte) => new[] { FinPt(y, 0f, 0), FinPt(y, 0.3f, 1), FinPt(y, fte, 1), FinPt(y, fte, -1), FinPt(y, 0.3f, -1) };
        var segs = new (float From, float To, float Fte)[] { (5.4f, RudderFrom, 1f), (RudderFrom, RudderTo, RudderF), (RudderTo, FinTopY, 1f) };
        foreach (var s in segs)
            m.Loft(new[] { FinRing(s.From, s.Fte), FinRing(s.To, s.Fte) }, Rep(tail, 5), tail);

        // fixed stabiliser, both sides
        foreach (int sg in new[] { 1, -1 })
        {
            Vector3[] Ring(float x, float fte) => new[] { StabPt(sg, x, 0f, 0), StabPt(sg, x, 0.3f, 1), StabPt(sg, x, fte, 1), StabPt(sg, x, fte, -1), StabPt(sg, x, 0.3f, -1) };
            var edges = new[] { WingTop, WingTop, Edge, WingUnder, WingUnder };
            m.Loft(new[] { Ring(0.0f, 1f), Ring(ElevatorFrom, 1f) }, edges, Edge);
            m.Loft(new[] { Ring(ElevatorFrom, ElevatorF), Ring(ElevatorTo, ElevatorF) }, edges, Edge);
            m.Loft(new[] { Ring(ElevatorTo, 1f), Ring(StabTipX, 1f) }, edges, Edge);
        }
    }

    private static Node3D Rudder(Color tail, Material bm, Material gm)
    {
        var p0 = FinPt(RudderFrom, RudderF, 0);
        var p1 = FinPt(RudderTo, RudderF, 0);
        var d = (Flip(p1) - Flip(p0)).Normalized();
        var yAxis = d;
        var xAxis = Vector3.Right;
        var basis = new Basis(xAxis, yAxis, xAxis.Cross(yAxis));
        var part = new Part((p0 + p1) * 0.5f, basis);
        Vector3[] Ring(float y) => new[] { FinPt(y, RudderF, 1), FinPt(y, 1f, 1), FinPt(y, 1f, -1), FinPt(y, RudderF, -1) };
        part.Loft(new[] { Ring(RudderFrom), Ring(RudderTo) }, new[] { tail, tail, tail, tail }, tail);
        return part.ToNode("Rudder", bm, gm);
    }

    private static Node3D Elevator(string name, int sg, Material bm, Material gm)
    {
        var p0 = StabPt(sg, ElevatorFrom, ElevatorF, 0);
        var p1 = StabPt(sg, ElevatorTo, ElevatorF, 0);
        var part = XPart(sg, p0, p1);
        Vector3[] Ring(float x) => new[] { StabPt(sg, x, ElevatorF, 1), StabPt(sg, x, 1f, 1), StabPt(sg, x, 1f, -1), StabPt(sg, x, ElevatorF, -1) };
        part.Loft(new[] { Ring(ElevatorFrom), Ring(ElevatorTo) }, new[] { Surface, Edge, WingUnder, Edge }, Edge);
        return part.ToNode(name, bm, gm);
    }

    // ---- moving wing surfaces ----------------------------------------------------------------------

    /// <summary>A part on a hinge line from <paramref name="inboard"/> to <paramref name="outboard"/>, its local +X pointing to the aircraft's right.</summary>
    private static Part XPart(int sg, Vector3 inboard, Vector3 outboard)
    {
        var a = Flip(inboard);
        var b = Flip(outboard);
        // left wing (sg = +1, node −X): inboard is the more-right end; right wing: outboard is
        var along = sg > 0 ? a - b : b - a;
        return new Part((inboard + outboard) * 0.5f, HingeBasis(along));
    }

    private static Node3D Flap(string name, int sg, float x0, float x1, Material bm, Material gm)
    {
        var part = XPart(sg, WPt(sg, x0, 0.72f, 0), WPt(sg, x1, 0.72f, 0));
        Vector3[] Ring(float x) => new[] { WPt(sg, x, 0.72f, 1), WPt(sg, x, 1f, 1), WPt(sg, x, 1f, -1), WPt(sg, x, 0.72f, -1) };
        part.Loft(new[] { Ring(x0), Ring(x1) }, new[] { Surface, Edge, WingUnder, Edge }, Edge);
        return part.ToNode(name, bm, gm);
    }

    private static Node3D Spoilers(string name, int sg, Material bm, Material gm)
    {
        const float chordFrac = 0.46f;
        float xa = SpoilerFrom, xb = SpoilerTo;
        float za = Le(xa) + chordFrac * (Te(xa) - Le(xa)), zb = Le(xb) + chordFrac * (Te(xb) - Le(xb));
        Vector3 Hinge(float x)
        {
            float z = Mathf.Lerp(za, zb, (x - xa) / (xb - xa));
            return new Vector3(sg * x, UpperAtZ(x, z) + 0.035f, z);
        }
        var part = XPart(sg, Hinge(xa), Hinge(xb));
        Vector3[] Ring(float x)
        {
            var h = Hinge(x);
            float zr = h.Z - Mathf.Lerp(1.25f, 0.65f, (x - xa) / (xb - xa));
            float yr = UpperAtZ(x, zr);
            return new[] { h, new Vector3(sg * x, yr + 0.035f, zr), new Vector3(sg * x, yr, zr), new Vector3(sg * x, UpperAtZ(x, h.Z), h.Z) };
        }
        foreach (var (from, to) in new[] { (3.0f, 4.7f), (4.8f, 6.2f), (6.4f, 8.2f), (8.3f, 10.2f), (10.3f, 12.4f) })
            part.Loft(new[] { Ring(from), Ring(to) }, new[] { Surface, Edge, Edge, Edge }, Edge);
        return part.ToNode(name, bm, gm);
    }

    // ---- gear ----------------------------------------------------------------------------------------

    private static Node3D MainGear(string name, int sg, Material bm, Material gm)
    {
        var hinge = new Vector3(sg * MainHinge.X, MainHinge.Y, MainHinge.Z);
        var part = new Part(hinge, Basis.Identity);
        float z = MainHinge.Z, gx = sg * MainGearX;
        var axle = new Vector3(gx, MainWheelRadius, z);
        part.Tube(hinge, new Vector3(gx, 1.45f, z), 0.15f, 0.12f, Grey, 8);
        part.Tube(new Vector3(gx, 1.45f, z), axle, 0.085f, 0.085f, Belly, 8);
        part.Tube(new Vector3(gx - WheelSpread, MainWheelRadius, z), new Vector3(gx + WheelSpread, MainWheelRadius, z), 0.07f, 0.07f, Grey, 6);
        // torque links
        part.Tube(new Vector3(gx, 1.5f, z), new Vector3(gx, 1.1f, z + 0.35f), 0.035f, 0.035f, Dark, 4);
        part.Tube(new Vector3(gx, 1.5f, z), new Vector3(gx, 1.1f, z - 0.35f), 0.035f, 0.035f, Dark, 4);
        foreach (float off in new[] { -WheelSpread, WheelSpread })
        {
            float wx = gx + off;
            part.Tube(new Vector3(wx - MainWheelWidth / 2, MainWheelRadius, z), new Vector3(wx + MainWheelWidth / 2, MainWheelRadius, z), MainWheelRadius, MainWheelRadius, Dark, 14);
            part.Tube(new Vector3(wx - MainWheelWidth / 2 - 0.01f, MainWheelRadius, z), new Vector3(wx + MainWheelWidth / 2 + 0.01f, MainWheelRadius, z), 0.3f, 0.3f, Grey, 8);
        }
        return part.ToNode(name, bm, gm);
    }

    private static Node3D NoseGear(Material bm, Material gm)
    {
        var part = new Part(NoseHinge, Basis.Identity);
        var axle = new Vector3(0, NoseWheelRadius, NoseGearZ);
        part.Tube(NoseHinge, new Vector3(0, 1.1f, NoseGearZ + 0.1f), 0.12f, 0.1f, Grey, 8);
        part.Tube(new Vector3(0, 1.1f, NoseGearZ + 0.1f), axle, 0.07f, 0.07f, Belly, 8);
        part.Tube(new Vector3(-0.2f, NoseWheelRadius, NoseGearZ), new Vector3(0.2f, NoseWheelRadius, NoseGearZ), 0.05f, 0.05f, Grey, 6);
        part.Tube(new Vector3(0, 1.3f, NoseGearZ + 0.07f), new Vector3(0, 0.9f, NoseGearZ + 0.3f), 0.03f, 0.03f, Dark, 4);
        foreach (float wx in new[] { -0.2f, 0.2f })
        {
            part.Tube(new Vector3(wx - NoseWheelWidth / 2, NoseWheelRadius, NoseGearZ), new Vector3(wx + NoseWheelWidth / 2, NoseWheelRadius, NoseGearZ), NoseWheelRadius, NoseWheelRadius, Dark, 12);
            part.Tube(new Vector3(wx - NoseWheelWidth / 2 - 0.01f, NoseWheelRadius, NoseGearZ), new Vector3(wx + NoseWheelWidth / 2 + 0.01f, NoseWheelRadius, NoseGearZ), 0.2f, 0.2f, Grey, 8);
        }
        return part.ToNode("GearNose", bm, gm);
    }

    /// <summary>Dark gear-bay doors under the belly and the belly fairing.</summary>
    private static void Underside(MeshScratch m)
    {
        // wing-body fairing: a grey bulge along the belly
        var ring = new (float X, float Y)[] { (-1.75f, 2.7f), (-1.45f, 2.25f), (-0.8f, 2.0f), (0.8f, 2.0f), (1.45f, 2.25f), (1.75f, 2.7f), (1.5f, 3.0f), (-1.5f, 3.0f) };
        Vector3[] Section(float z, float scale)
        {
            var r = new Vector3[ring.Length];
            for (int i = 0; i < r.Length; i++) r[i] = new Vector3(ring[i].X * scale, 2.65f + (ring[i].Y - 2.65f) * scale, z);
            return r;
        }
        m.Loft(new[] { Section(-3.8f, 0.35f), Section(-1.5f, 1f), Section(5.6f, 1f), Section(7.3f, 0.35f) }, Rep(Belly, 8), Belly);

        // nose gear bay
        m.Box(new Vector3(0, 2.02f, 13f), new Vector3(0.8f, 0.06f, 1.3f), Dark);
    }

    // ---- doors ---------------------------------------------------------------------------------------------

    private static Node3D DoorLeaf(int i, Material bm, Material gm)
    {
        var (zc, sg) = Door(i);
        float z0 = zc - DoorWidth / 2, z1 = zc + DoorWidth / 2;
        float yPivot = 4.2f;
        var pivot = new Vector3(Po(z1, Mathf.Clamp((yPivot - CentreY) / HalfHeight, -1f, 1f), sg).X, FloorY, z1);
        var part = new Part(pivot, Basis.Identity);

        Vector3[] Ring(float z)
        {
            var r = new Vector3[12];
            for (int j = 0; j < 6; j++)
            {
                r[j] = Po(z, H[3 + j], sg);
                r[11 - j] = Pi(z, H[3 + j], sg);
            }
            return r;
        }
        var edges = new[]
        {
            White, White, White, White, White, White,
            Cream, Cream, Cream, Cream, Cream, White,
        };
        part.Loft(new[] { Ring(z0), Ring(z1) }, edges, White);

        // the little window
        float zm = (z0 + z1) * 0.5f;
        Vector3 At(float z, float y)
        {
            float t = (y - Levels[7]) / (Levels[8] - Levels[7]);
            var p = Po(z, H[7], sg).Lerp(Po(z, H[8], sg), t);
            p.X += sg * 0.012f;
            return part.P(p);
        }
        part.S.Pane(new[] { At(zm - 0.13f, 4.55f), At(zm - 0.13f, 4.95f), At(zm + 0.13f, 4.95f), At(zm + 0.13f, 4.55f) }, Pane);
        return part.ToNode($"Door{i}", bm, gm);
    }

    // ---- lights ----------------------------------------------------------------------------------------------

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
        float tipY = Ymid(WingTipX);
        float shX = WingTipX + 0.12f * SharkletHeight + 0.12f;
        Add("NavL", new Vector3(shX, tipY + SharkletHeight - 0.15f, WingTipTrailingZ - 0.15f * SharkletHeight - 0.05f), new Vector3(0.16f, 0.14f, 0.16f), red);
        Add("NavR", new Vector3(-shX, tipY + SharkletHeight - 0.15f, WingTipTrailingZ - 0.15f * SharkletHeight - 0.05f), new Vector3(0.16f, 0.14f, 0.16f), green);
        Add("NavTail", new Vector3(0, FinTopY - 0.35f, FinTopRearZ - 0.08f), new Vector3(0.14f, 0.2f, 0.12f), white);
        Add("BeaconTop", new Vector3(0, TopY + 0.05f, 3.5f), new Vector3(0.22f, 0.12f, 0.22f), red);
        Add("BeaconBottom", new Vector3(0, BellyY - 0.04f, -1.5f), new Vector3(0.22f, 0.1f, 0.22f), red);
        Add("StrobeL", new Vector3(WingTipX - 0.05f, tipY, WingTipTrailingZ - 0.1f), new Vector3(0.12f, 0.1f, 0.12f), white);
        Add("StrobeR", new Vector3(-(WingTipX - 0.05f), tipY, WingTipTrailingZ - 0.1f), new Vector3(0.12f, 0.1f, 0.12f), white);
        const float lx = 2.7f;
        Add("LandingL", new Vector3(lx, Ymid(lx), Le(lx) + 0.03f), new Vector3(0.24f, 0.12f, 0.08f), white);
        Add("LandingR", new Vector3(-lx, Ymid(lx), Le(lx) + 0.03f), new Vector3(0.24f, 0.12f, 0.08f), white);
    }
}
