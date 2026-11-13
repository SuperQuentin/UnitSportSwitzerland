using Godot;
using static UnitSport.Avatar.ShuttleLayout;

namespace UnitSport.Avatar;

/// <summary>
/// The space shuttle orbiter (#1492), built from code like every avatar vehicle: low-poly, flat-shaded,
/// one <c>Body</c> mesh for what stays put plus hinge nodes for what moves. Numbers from
/// <see cref="ShuttleLayout"/>. White on top, black tiles on the belly, the leading edges and round the
/// windows, a grey nose cap; the crew module is a hollow skin with real glass (the flight deck and its
/// seats inside, the mid deck under it), the payload bay and the aft fuselage a solid.
///
/// <para>
/// Moving parts, by the names <see cref="AirlinerRig"/> finds: <c>GearMainL/R</c>, <c>GearNose</c> (all
/// swing forward into the belly) and their doors (<see cref="GearDoors"/>), <c>ElevatorL/R</c> (inboard
/// elevons), <c>AileronL/R</c> (outboard elevons), <c>BodyFlap</c>, <c>Rudder</c> with its two halves
/// <c>Rudder/SpoilerL</c> and <c>Rudder/SpoilerR</c> (they splay apart as the speedbrake), <c>Door0</c> the
/// side hatch, the A320's light names, <c>DriveSwitch/Toggle</c> on the centre console. Effects, varied
/// by node scale, visibility and one shader parameter each, never by a new mesh: <c>Plume0..2</c> (the
/// main engines' flames), <c>DriveGlow</c> (the cruise drive) and <c>Plasma</c> (re-entry's glow).
/// </para>
/// Authored +Z forward, +X the left; <see cref="Build"/> returns node space (−Z forward).
/// </summary>
public static class ShuttleMeshBuilder
{
    // ---- palette -------------------------------------------------------------------------
    private static readonly Color White = new(0.95f, 0.95f, 0.93f);
    private static readonly Color Tile = new(0.10f, 0.10f, 0.11f);
    private static readonly Color TileSeam = new(0.2f, 0.2f, 0.22f);
    private static readonly Color Rcc = new(0.50f, 0.51f, 0.53f);
    private static readonly Color Blanket = new(0.88f, 0.87f, 0.82f);
    private static readonly Color Metal = new(0.42f, 0.43f, 0.46f);
    private static readonly Color NozzleGrey = new(0.30f, 0.29f, 0.29f);
    private static readonly Color Lining = new(0.26f, 0.27f, 0.30f);
    private static readonly Color PanelGrey = new(0.22f, 0.23f, 0.26f);
    private static readonly Color Upholstery = new(0.30f, 0.36f, 0.30f);
    private static readonly Color UpholsteryDark = new(0.18f, 0.2f, 0.18f);
    private static readonly Color Locker = new(0.82f, 0.82f, 0.78f);
    private static readonly Color Tyre = new(0.08f, 0.08f, 0.09f);
    private static readonly Color Pane = new(0.55f, 0.68f, 0.75f, 0.35f);
    private static readonly Color SwissRed = new(0.85f, 0.08f, 0.1f);

    private static Vector3 Flip(Vector3 v) => AircraftMeshBuilder.Flip(v);

    // ---- the public contract --------------------------------------------------------------

    /// <summary>The hatch's leaf turns about its node's Z (its bottom edge, fore and aft) this far to open: out and down.</summary>
    public const float HatchOpenAngle = 1.75f;

    /// <summary>The gear doors: each node turns about its local Z (its hinge, fore and aft) this far open.</summary>
    public static readonly (string Name, float Angle)[] GearDoors =
    {
        ("GearDoorL", -1.45f), ("GearDoorR", 1.45f), ("NoseDoorL", -1.4f), ("NoseDoorR", 1.4f),
    };

    /// <summary>The forward panel's half width: as wide as the nose allows at its top.</summary>
    public static float CockpitPanelHalf => Mathf.Min(0.95f, XAt(PanelZ + 0.06f, PanelY + 0.35f, ShuttleLayout.Skin) - 0.04f);

    /// <summary>The whole orbiter, node space, ready to add to a scene.</summary>
    public static Node3D Build()
    {
        var bodyMat = HumanMeshBuilder.FigureMaterial();
        var glassMat = CarRig.GlassMaterial();
        var root = new Node3D { Name = "Shuttle" };

        var b = new MeshScratch();
        Fuselage(b);
        CrewModule(b);
        Wings(b);
        Tail(b);
        Engines(b);
        Markings(b);
        var body = new MeshInstance3D { Name = "Body", Mesh = b.Build() };
        root.AddChild(body);
        MeshScratch.Paint(body, bodyMat, glassMat);

        foreach (int sg in new[] { 1, -1 })
        {
            string s = sg > 0 ? "L" : "R";
            root.AddChild(Elevon($"Elevator{s}", sg, ElevonInFrom, ElevonInTo, bodyMat, glassMat));
            root.AddChild(Elevon($"Aileron{s}", sg, ElevonOutFrom, ElevonOutTo, bodyMat, glassMat));
            root.AddChild(MainGear($"GearMain{s}", sg, bodyMat, glassMat));
            root.AddChild(MainGearDoor($"GearDoor{s}", sg, bodyMat, glassMat));
            root.AddChild(NoseGearDoor($"NoseDoor{s}", sg, bodyMat, glassMat));
        }
        root.AddChild(NoseGear(bodyMat, glassMat));
        root.AddChild(BodyFlap(bodyMat, glassMat));
        root.AddChild(Rudder(bodyMat, glassMat));
        root.AddChild(Hatch(bodyMat, glassMat));
        root.AddChild(DriveToggle(bodyMat, glassMat));
        AddLights(root);
        AddEffects(root);
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

    /// <summary>A part on a hinge line from <paramref name="inboard"/> to <paramref name="outboard"/>, its local +X pointing to the aircraft's right.</summary>
    private static AircraftPart XPart(int sg, Vector3 inboard, Vector3 outboard)
    {
        var a = Flip(inboard);
        var b = Flip(outboard);
        var along = sg > 0 ? a - b : b - a;
        return new AircraftPart((inboard + outboard) * 0.5f, HingeBasis(along));
    }

    // ---- the fuselage's section ------------------------------------------------------------

    /// <summary>Superellipse exponents under and over the chine: boxy with rounded chines below, the doors' arch above.</summary>
    private const float NL = 6f, NU = 2.4f;

    /// <summary>The skin's rows round the section, belly (−1) to crown (+1), 0 at the chine; row k lies between H[k] and H[k + 1].</summary>
    private static readonly float[] H = { -1f, -0.88f, -0.7f, -0.5f, -0.3f, -0.12f, 0f, 0.18f, 0.36f, 0.52f, 0.66f, 0.8f, 0.91f, 1f };

    /// <summary>The crew module's roof rises a little over the flight deck, ahead of this.</summary>
    private const float CabinHumpZ = 20.4f;

    /// <summary>The crown line ahead of <see cref="CabinHumpZ"/>: the cabin roof's hump, the windscreen, the nose's back.</summary>
    private static readonly (float Z, float Y)[] Crown =
    {
        (CabinHumpZ, TopY), (21.3f, 8.22f), (22.3f, 8.28f), (23.1f, 7.98f), (23.9f, 7.45f), (24.6f, 6.85f), (25.4f, 6.25f),
        (26.4f, 5.65f), (27.4f, 5.05f), (28.4f, 4.45f), (NoseZ, 3.95f),
    };

    private static float TopAt(float z)
    {
        if (z <= Crown[0].Z) return TopY;
        for (int i = 1; i < Crown.Length; i++)
            if (z <= Crown[i].Z) return Mathf.Lerp(Crown[i - 1].Y, Crown[i].Y, (z - Crown[i - 1].Z) / (Crown[i].Z - Crown[i - 1].Z));
        return Crown[^1].Y;
    }

    /// <summary>The section at station <paramref name="z"/>: half width, belly, chine (the widest) and crown heights.</summary>
    public static (float A, float Yb, float Ym, float Yt) Section(float z)
    {
        if (z <= CabinHumpZ) return (HalfWidth, BellyY, ChineY, TopY);
        float t = Mathf.Clamp((z - NoseFromZ) / (NoseZ - NoseFromZ), 0f, 1f);
        float a = Mathf.Max(0.3f, HalfWidth * Mathf.Sqrt(Mathf.Max(0f, 1f - t * t)));
        float yb = BellyY + (NoseTipY - 0.2f - BellyY) * Mathf.Pow(t, 2.2f);
        float ym = Mathf.Lerp(ChineY, NoseTipY, Mathf.Pow(t, 1.3f));
        return (a, yb, ym, TopAt(z));
    }

    /// <summary>A point of the section at <paramref name="z"/>, <paramref name="h"/> round it, side <paramref name="sg"/>; <paramref name="inset"/> shrinks it (the skin's inside), negative grows it.</summary>
    private static Vector3 Po(float z, float h, int sg, float inset = 0f)
    {
        var (a, yb, ym, yt) = Section(z);
        a -= inset; yb += inset; yt -= inset;
        float phi = h * Mathf.Pi * 0.5f;
        float c = Mathf.Cos(phi), s = Mathf.Sin(phi);
        float n = s < 0f ? NL : NU;
        float x = a * Mathf.Pow(Mathf.Max(c, 0f), 2f / n);
        float y = ym + (s < 0f ? -(ym - yb) : yt - ym) * Mathf.Pow(Mathf.Abs(s), 2f / n);
        return new Vector3(sg * x, y, z);
    }

    /// <summary>The section's half width at height <paramref name="y"/> (shrunk by <paramref name="inset"/>); 0 above or below it.</summary>
    private static float XAt(float z, float y, float inset = 0f)
    {
        var (a, yb, ym, yt) = Section(z);
        a -= inset; yb += inset; yt -= inset;
        float n, s;
        if (y >= ym) { n = NU; s = (y - ym) / Mathf.Max(0.01f, yt - ym); }
        else { n = NL; s = (ym - y) / Mathf.Max(0.01f, ym - yb); }
        if (s >= 1f) return 0f;
        float sin = Mathf.Pow(Mathf.Max(s, 0f), n * 0.5f);
        float cos = Mathf.Sqrt(Mathf.Max(0f, 1f - sin * sin));
        return Mathf.Max(0f, a) * Mathf.Pow(cos, 2f / n);
    }

    /// <summary>The constant section's upper surface at half width <paramref name="x"/>: where the OMS pods sit.</summary>
    private static float RoofY(float x)
    {
        float cos = Mathf.Pow(Mathf.Clamp(Mathf.Abs(x) / HalfWidth, 0f, 1f), NU * 0.5f);
        float sin = Mathf.Sqrt(Mathf.Max(0f, 1f - cos * cos));
        return ChineY + (TopY - ChineY) * Mathf.Pow(sin, 2f / NU);
    }

    // ---- the fuselage -------------------------------------------------------------------------

    /// <summary>Stations of the crew module and the nose: every crown point, the glass's edges, the nose cap.</summary>
    private static readonly float[] Stations =
    {
        CabinRearZ, CabinHumpZ, 20.9f, 21.3f, NoseFromZ, 22.0f, 22.3f, 22.8f, 23.1f, 23.55f, 23.9f, 24.25f, 24.6f, 25.0f, 25.4f, 25.9f,
        26.4f, 26.9f, 27.4f, 27.9f, NoseCapZ, 28.4f, 28.8f, 29.0f, NoseZ,
    };

    /// <summary>The glass along the crew module: the side windows, the forward windscreen, its lower panes (between dark pillars).</summary>
    private static readonly (float From, float To)[] Glass = { (22.45f, 23.25f), (23.38f, 24.3f), (24.43f, 25.3f) };

    /// <summary>The pilots' sight: from under the glareshield's line to over their heads (the eye at 6.46 m).</summary>
    private const float SightLow = 5.95f, SightHigh = 7.75f;

    /// <summary>
    /// Row <paramref name="k"/> is in the pilots' sight at station <paramref name="z"/>: glass, framed black.
    /// The crown row is glass only in the forward windscreen (a thin centre post splits it); over the side
    /// windows it is the roof.
    /// </summary>
    private static bool InSight(int k, float z) => (k < H.Length - 2 || z > WindscreenZ)
        && Po(z, H[k + 1], 1).Y > SightLow + 0.1f && Po(z, H[k], 1).Y < SightHigh;

    /// <summary>The forward windscreen starts here (the side windows are aft of it).</summary>
    private const float WindscreenZ = 23.35f;

    /// <summary>A skin row's paint at station z: the nose cap grey, the windows framed black, black tiles under the chine and up the nose's sides, white above.</summary>
    private static Color RowColour(int k, float z)
    {
        if (z > NoseCapZ) return Rcc;
        if (z > 22.2f && z < 25.6f && InSight(k, z)) return Tile;
        // ahead of the windscreen the black comes up to the nose's chine line
        float black = z < 25.0f ? -0.12f : 0.18f;
        return H[k + 1] <= black + 0.001f ? Tile : White;
    }

    /// <summary>The payload bay's and the aft fuselage's rows: black tiles up to the chine's foot, white above.</summary>
    private static Color AftRow(int k) => k <= 4 ? Tile : White;

    private static void Fuselage(MeshScratch m)
    {
        // the crew module and the nose: a hollow skin, row by row, with the glass cut out
        foreach (int sg in new[] { 1, -1 })
            for (int k = 0; k < H.Length - 1; k++)
                SkinRow(m, sg, k);

        // the nose cap closes the rows' tip
        var tip = new Vector3[12];
        var point = new Vector3[12];
        for (int j = 0; j < 12; j++)
        {
            tip[j] = j <= 6 ? Po(NoseZ, -1f + j / 3f, 1) : Po(NoseZ, 1f - (j - 6) / 3f, -1);
            point[j] = new Vector3(tip[j].X * 0.35f, NoseTipY + (tip[j].Y - NoseTipY) * 0.4f, NoseZ + 0.22f);
        }
        m.Loft(new[] { tip, point }, Rep(Rcc, 12), Rcc);

        // the payload bay and the aft fuselage: one solid, its front the crew module's aft bulkhead
        m.Loft(new[] { SolidRing(BaseZ), SolidRing(CabinRearZ) }, SolidColours(), TileSeam);
    }

    private static Vector3[] SolidRing(float z, float inset = 0f)
    {
        var r = new List<Vector3>();
        for (int i = 0; i < H.Length; i++) r.Add(Po(z, H[i], 1, inset));
        for (int i = H.Length - 2; i >= 1; i--) r.Add(Po(z, H[i], -1, inset));
        return r.ToArray();
    }

    /// <summary>Band i of <see cref="SolidRing"/> runs from point i to i + 1: the left rows up, then the right rows down.</summary>
    private static Color[] SolidColours()
    {
        int n = H.Length;
        var c = new Color[2 * n - 2];
        for (int i = 0; i < n - 1; i++) c[i] = AftRow(i);
        for (int j = 0; j < n - 1; j++) c[n - 1 + j] = AftRow(n - 2 - j);
        return c;
    }

    private readonly record struct Hole(float From, float To);

    private static void SkinRow(MeshScratch m, int sg, int k)
    {
        // the stations between the holes, each stretch drawn in runs of one colour
        var cuts = new List<Hole>();
        foreach (var (f, t) in Glass)
            if (InSight(k, (f + t) * 0.5f)) cuts.Add(new Hole(f, t));
        float z = CabinRearZ;
        foreach (var h in cuts)
        {
            Slab(m, sg, k, z, h.From);
            PaneRow(m, sg, k, h.From, h.To);
            z = h.To;
        }
        Slab(m, sg, k, z, NoseZ);
    }

    /// <summary>The stations to draw between <paramref name="a"/> and <paramref name="b"/>, both included.</summary>
    private static List<float> Zs(float a, float b)
    {
        var r = new List<float> { a };
        foreach (float z in Stations) if (z > a + 0.01f && z < b - 0.01f) r.Add(z);
        r.Add(b);
        return r;
    }

    private static void Slab(MeshScratch m, int sg, int k, float z0, float z1)
    {
        if (z1 - z0 < 0.02f) return;
        var zs = Zs(z0, z1);
        int from = 0;
        while (from < zs.Count - 1)
        {
            var c = RowColour(k, (zs[from] + zs[from + 1]) * 0.5f);
            int to = from + 1;
            while (to < zs.Count - 1 && RowColour(k, (zs[to] + zs[to + 1]) * 0.5f) == c) to++;
            var rings = new Vector3[to - from + 1][];
            for (int i = from; i <= to; i++)
                rings[i - from] = new[] { Po(zs[i], H[k], sg), Po(zs[i], H[k + 1], sg), Po(zs[i], H[k + 1], sg, ShuttleLayout.Skin), Po(zs[i], H[k], sg, ShuttleLayout.Skin) };
            m.Loft(rings, new[] { c, c, Lining, c }, c);
            from = to;
        }
    }

    private static void PaneRow(MeshScratch m, int sg, int k, float z0, float z1)
    {
        var zs = Zs(z0, z1);
        for (int i = 0; i + 1 < zs.Count; i++)
        {
            Vector3 Mid(float z, float h) => (Po(z, h, sg) + Po(z, h, sg, ShuttleLayout.Skin)) * 0.5f;
            m.Pane(new[] { Mid(zs[i], H[k]), Mid(zs[i], H[k + 1]), Mid(zs[i + 1], H[k + 1]), Mid(zs[i + 1], H[k]) }, Pane);
        }
    }

    // ---- the crew module's inside -------------------------------------------------------------------

    /// <summary>A floor slab from <paramref name="z0"/> to <paramref name="z1"/> at <paramref name="y"/>, wall to wall.</summary>
    private static void Floor(MeshScratch m, float z0, float z1, float y, Color top)
    {
        var zs = Zs(z0, z1);
        var rings = new Vector3[zs.Count][];
        for (int i = 0; i < zs.Count; i++)
        {
            float z = zs[i];
            float w = Mathf.Max(0.1f, XAt(z, y, ShuttleLayout.Skin) - 0.01f), w2 = Mathf.Max(0.1f, XAt(z, y - 0.1f, ShuttleLayout.Skin) - 0.01f);
            rings[i] = new[] { new Vector3(-w, y, z), new Vector3(w, y, z), new Vector3(w2, y - 0.1f, z), new Vector3(-w2, y - 0.1f, z) };
        }
        m.Loft(rings, new[] { top, Lining, Lining, Lining }, Lining);
    }

    private static void Seat(MeshScratch m, Vector3 hip, float floor, bool pilot)
    {
        float x = hip.X;
        m.Box(new Vector3(x, (floor + hip.Y - 0.16f) * 0.5f, hip.Z), new Vector3(0.22f, hip.Y - 0.16f - floor, 0.3f), UpholsteryDark);
        m.Box(new Vector3(x, hip.Y - 0.1f, hip.Z + 0.1f), new Vector3(0.5f, 0.12f, 0.5f), Upholstery);
        m.Box(new Vector3(x, hip.Y + 0.3f, hip.Z - 0.2f), new Vector3(0.5f, 0.85f, 0.1f), Upholstery, new Basis(Vector3.Right, -0.12f));
        m.Box(new Vector3(x, hip.Y + 0.82f, hip.Z - 0.24f), new Vector3(0.3f, 0.2f, 0.09f), Upholstery);
        if (pilot)
            foreach (float s in new[] { -1f, 1f })
                m.Box(new Vector3(x + s * 0.27f, hip.Y + 0.1f, hip.Z + 0.1f), new Vector3(0.05f, 0.05f, 0.4f), UpholsteryDark);
    }

    private static void CrewModule(MeshScratch m)
    {
        Floor(m, CabinRearZ + 0.02f, 24.0f, FlightDeckY, PanelGrey);
        Floor(m, CabinRearZ + 0.02f, MidDeckFrontZ, MidDeckY, PanelGrey);

        // the mid deck's forward lockers, a wall of cream boxes
        float lw = XAt(MidDeckFrontZ, 4.0f, ShuttleLayout.Skin) - 0.05f;
        m.Box(new Vector3(0, (MidDeckY + FlightDeckY - 0.1f) * 0.5f, MidDeckFrontZ - 0.25f), new Vector3(lw * 2f, FlightDeckY - 0.1f - MidDeckY, 0.5f), Locker);
        for (int r = 0; r < 4; r++)
            for (int c = -3; c <= 3; c++)
                m.Box(new Vector3(c * 0.42f, MidDeckY + 0.3f + r * 0.48f, MidDeckFrontZ - 0.505f), new Vector3(0.36f, 0.4f, 0.012f), Blanket);

        // the flight deck's aft stations (the payload handling panels) against the aft bulkhead
        m.Box(new Vector3(0, FlightDeckY + 0.85f, AftStationZ - 0.15f), new Vector3(2.0f, 1.7f, 0.3f), PanelGrey);

        // the seats: commander and pilot, the two specialists behind, three on the mid deck
        Seat(m, CommanderHip, FlightDeckY, true);
        Seat(m, PilotHip, FlightDeckY, true);
        foreach (var hip in SpecialistHips) Seat(m, hip, FlightDeckY, false);
        foreach (var hip in MidDeckHips) Seat(m, hip, MidDeckY, false);

        // the forward panel, the glareshield over it, the centre console between the seats, the overhead;
        // the moving controls, screens and lamps are the cockpit's own (#421, AircraftCockpit)
        m.Box(new Vector3(0, PanelY, PanelZ + 0.06f), new Vector3(CockpitPanelHalf * 2f, 0.7f, 0.12f), PanelGrey);
        float gw = Mathf.Min(1.0f, XAt(PanelZ + 0.21f, GlareY, ShuttleLayout.Skin) - 0.04f);
        m.Box(new Vector3(0, GlareY - 0.03f, PanelZ + 0.21f), new Vector3(gw * 2f, 0.06f, 0.4f), Tile);
        m.Box(new Vector3(0, FlightDeckY + 0.3f, (PedestalZ0 + PedestalZ1) * 0.5f), new Vector3(PedestalHalf * 2f, 0.6f, PedestalZ1 - PedestalZ0), PanelGrey);
        m.Box(new Vector3(0, OverheadY + 0.06f, OverheadZ), new Vector3(0.9f, 0.12f, 0.9f), PanelGrey);
        // the panel's kick plate down to the floor under it, between the pedals
        m.Box(new Vector3(0, (FlightDeckY + PanelY - 0.35f) * 0.5f, PanelZ + 0.2f), new Vector3(0.3f, PanelY - 0.35f - FlightDeckY, 0.3f), PanelGrey);
    }

    /// <summary>
    /// The drive's guarded toggle (#1492) on the centre console: a plate and a red guard (the body's), and
    /// <c>DriveSwitch/Toggle</c>, the lever the rig throws forward when the drive is on.
    /// </summary>
    private static Node3D DriveToggle(Material bm, Material gm)
    {
        var at = DriveSwitch;
        var node = new Node3D { Name = "DriveSwitch", Position = Flip(at) };
        var guard = new AircraftPart(at, Basis.Identity);
        guard.Box(at + new Vector3(0, 0.008f, 0), new Vector3(0.08f, 0.016f, 0.1f), PanelGrey);
        var red = new Color(0.8f, 0.1f, 0.08f);
        foreach (float s in new[] { -1f, 1f })
            guard.Box(at + new Vector3(s * 0.035f, 0.04f, 0f), new Vector3(0.01f, 0.05f, 0.08f), red);
        guard.Box(at + new Vector3(0, 0.068f, 0f), new Vector3(0.08f, 0.01f, 0.08f), red);
        var guardNode = guard.ToNode("Guard", bm, gm);
        guardNode.Transform = Transform3D.Identity;
        node.AddChild(guardNode);

        var lever = new AircraftPart(at, Basis.Identity);
        lever.Tube(at + new Vector3(0, 0.016f, 0), at + new Vector3(0, 0.05f, 0), 0.005f, 0.004f, Metal, 5);
        lever.Box(at + new Vector3(0, 0.054f, 0), new Vector3(0.012f, 0.012f, 0.012f), White);
        var leverNode = lever.ToNode("Toggle", bm, gm);
        leverNode.Transform = Transform3D.Identity;
        node.AddChild(leverNode);
        return node;
    }

    // ---- the wing: a double delta, flat underneath ---------------------------------------------------

    private static float Le(float x)
    {
        x = Mathf.Max(x, WingRootX);
        return x <= WingKinkX
            ? Mathf.Lerp(GloveRootZ, KinkZ, (x - WingRootX) / (WingKinkX - WingRootX))
            : Mathf.Lerp(KinkZ, TipLeadingZ, (x - WingKinkX) / (WingTipX - WingKinkX));
    }

    private static float Te(float x) => Mathf.Lerp(RootTrailingZ, TipTrailingZ, Mathf.Clamp((x - WingRootX) / (WingTipX - WingRootX), 0f, 1f));
    private static float Thick(float x) => Mathf.Lerp(WingRootThickness, WingTipThickness, Mathf.Clamp((x - WingRootX) / (WingTipX - WingRootX), 0f, 1f));
    private static float WingBase(float x) => BellyY + 0.04f + Mathf.Max(0f, x - WingRootX) * Mathf.Tan(Dihedral);

    /// <summary>Thickness along the chord, 0..1: a blunt leading edge, full to 30 %, a thick trailing edge (the elevons').</summary>
    private static float Prof(float f)
    {
        f = Mathf.Clamp(f, 0f, 1f);
        if (f < 0.1f) return Mathf.Lerp(0.3f, 0.85f, f / 0.1f);
        if (f < 0.3f) return Mathf.Lerp(0.85f, 1f, (f - 0.1f) / 0.2f);
        if (f < 0.8f) return Mathf.Lerp(1f, 0.72f, (f - 0.3f) / 0.5f);
        return Mathf.Lerp(0.72f, 0.16f, (f - 0.8f) / 0.2f);
    }

    /// <summary>The wing's height at span x, chord fraction f: v = +1 upper, −1 lower (nearly flat), 0 the leading edge's middle.</summary>
    private static float SurfaceY(float x, float f, int v) =>
        WingBase(x) + (v > 0 ? Thick(x) * Prof(f) : v < 0 ? -0.04f * Prof(f) : Thick(x) * 0.1f);

    private static Vector3 WPt(int sg, float x, float f, int v)
    {
        float zl = Le(x), zt = Te(x);
        return new Vector3(sg * x, SurfaceY(x, f, v), zl + (zt - zl) * f);
    }

    /// <summary>The chord fraction of the elevons' hinge line at span x (they keep their chord).</summary>
    private static float Fte(float x) => 1f - ElevonChord / (Le(x) - Te(x));

    private static Vector3[] WingRing(int sg, float x, float fte) => new[]
    {
        WPt(sg, x, 0f, 0), WPt(sg, x, 0.03f, 1), WPt(sg, x, 0.12f, 1), WPt(sg, x, 0.35f, 1), WPt(sg, x, 0.85f * fte, 1), WPt(sg, x, fte, 1),
        WPt(sg, x, fte, -1), WPt(sg, x, 0.85f * fte, -1), WPt(sg, x, 0.35f, -1), WPt(sg, x, 0.12f, -1), WPt(sg, x, 0.03f, -1),
    };

    private static void Wings(MeshScratch m)
    {
        // black on the leading edge, the trailing face and all underneath, white on top
        var edges = new[] { Tile, White, White, White, White, Tile, Tile, Tile, Tile, Tile, Tile };
        var segments = new (float From, float To, bool Elevon)[]
        {
            (2.3f, WingRootX, false), (WingRootX, ElevonInFrom, false), (ElevonInFrom, WingKinkX, true), (WingKinkX, ElevonInTo, true),
            (ElevonInTo, ElevonOutFrom, false), (ElevonOutFrom, ElevonOutTo, true), (ElevonOutTo, WingTipX, false),
        };
        foreach (int sg in new[] { 1, -1 })
            foreach (var s in segments)
                m.Loft(new[] { WingRing(sg, s.From, s.Elevon ? Fte(s.From) : 1f), WingRing(sg, s.To, s.Elevon ? Fte(s.To) : 1f) }, edges, Tile);
    }

    private static Node3D Elevon(string name, int sg, float x0, float x1, Material bm, Material gm)
    {
        Vector3 Hinge(float x) => (WPt(sg, x, Fte(x), 1) + WPt(sg, x, Fte(x), -1)) * 0.5f;
        var part = XPart(sg, Hinge(x0), Hinge(x1));
        Vector3[] Ring(float x) => new[] { WPt(sg, x, Fte(x), 1), WPt(sg, x, 1f, 1), WPt(sg, x, 1f, -1), WPt(sg, x, Fte(x), -1) };
        part.Loft(new[] { Ring(x0 + 0.03f), Ring(x1 - 0.03f) }, new[] { White, Tile, Tile, Tile }, Tile);
        return part.ToNode(name, bm, gm);
    }

    // ---- the tail: the fin and its split rudder, the OMS pods ----------------------------------------

    private static float FinFront(float y) => Mathf.Lerp(FinRootFrontZ, FinTopFrontZ, (y - FinRootY) / (FinTopY - FinRootY));
    private static float FinRear(float y) => Mathf.Lerp(FinRootRearZ, FinTopRearZ, (y - FinRootY) / (FinTopY - FinRootY));
    private static float FinThick(float y) => Mathf.Lerp(0.75f, 0.22f, Mathf.Clamp((y - FinRootY) / (FinTopY - FinRootY), 0f, 1f));

    private static Vector3 FinPt(float y, float f, int v) =>
        new(v * FinThick(y) * 0.5f * Prof(f), y, FinFront(y) + f * (FinRear(y) - FinFront(y)));

    private static void Tail(MeshScratch m)
    {
        Vector3[] FinRing(float y, float fte) => new[]
        {
            FinPt(y, 0f, 0), FinPt(y, 0.06f, 1), FinPt(y, 0.3f, 1), FinPt(y, fte, 1), FinPt(y, fte, -1), FinPt(y, 0.3f, -1), FinPt(y, 0.06f, -1),
        };
        var edges = new[] { Tile, White, White, White, White, White, Tile };
        foreach (var (from, to, fte) in new[] { (FinRootY - 0.3f, RudderFrom, 1f), (RudderFrom, RudderTo, RudderF), (RudderTo, FinTopY, 1f) })
            m.Loft(new[] { FinRing(from, fte), FinRing(to, fte) }, edges, from < FinRootY ? White : Tile);

        // the OMS pods either side of the fin: hugging the roof, black tiles on their noses
        foreach (int sg in new[] { 1, -1 })
        {
            Vector3[] Pod(float z, float k)
            {
                var r = new Vector3[12];
                for (int j = 0; j < 12; j++)
                {
                    float th = Mathf.Tau * j / 12f;
                    float x = OmsX + OmsHalfWidth * k * Mathf.Cos(th), y = OmsY + OmsHalfHeight * k * Mathf.Sin(th);
                    if (Mathf.Sin(th) < -0.2f) y = RoofY(x) - 0.06f;
                    r[j] = new Vector3(sg * x, y, z);
                }
                return r;
            }
            m.Loft(new[] { Pod(OmsFrontZ, 0.12f), Pod(OmsFrontZ - 0.45f, 0.6f), Pod(OmsFrontZ - 0.9f, 0.85f) }, Rep(Tile, 12), Tile);
            m.Loft(new[] { Pod(OmsFrontZ - 0.9f, 0.85f), Pod(OmsFrontZ - 1.8f, 1f), Pod(BaseZ, 1f), Pod(OmsRearZ, 0.9f) }, Rep(White, 12), White);
            // the OMS engine's bell out of the pod's end, and the aft RCS's dark ports beside it
            var c = new Vector3(sg * OmsX, OmsY, OmsRearZ + 0.05f);
            using (m.Smoothing(true))
                m.Skirt(c, c + new Vector3(0, 0, -0.75f), 0.18f, 0.4f, NozzleGrey, 14);
            for (int i = 0; i < 3; i++)
                m.Box(new Vector3(sg * (OmsX + OmsHalfWidth * 0.95f), OmsY - 0.1f + i * 0.17f, OmsRearZ + 0.35f), new Vector3(0.04f, 0.1f, 0.1f), Tile);
        }
    }

    /// <summary>The rudder's hinge line, and the frame both its halves are built in (local Y along it, Z aft).</summary>
    private static (Vector3 Mid, Basis Basis) RudderFrame()
    {
        var p0 = FinPt(RudderFrom, RudderF, 0);
        var p1 = FinPt(RudderTo, RudderF, 0);
        var d = (Flip(p1) - Flip(p0)).Normalized();
        return ((p0 + p1) * 0.5f, new Basis(Vector3.Right, d, Vector3.Right.Cross(d)));
    }

    /// <summary>
    /// The rudder (its node turns about its hinge line, local Y) and its two halves, <c>SpoilerL</c> and
    /// <c>SpoilerR</c>, children at the same hinge: they splay apart about it as the speedbrake.
    /// </summary>
    private static Node3D Rudder(Material bm, Material gm)
    {
        var (mid, basis) = RudderFrame();
        var rudder = new Node3D { Name = "Rudder", Transform = new Transform3D(basis, Flip(mid)) };
        foreach (int v in new[] { 1, -1 })
        {
            var part = new AircraftPart(mid, basis);
            Vector3[] Ring(float y) => new[] { FinPt(y, RudderF, 0), FinPt(y, RudderF, v), FinPt(y, 1f, v), FinPt(y, 1f, 0) };
            part.Loft(new[] { Ring(RudderFrom + 0.03f), Ring(RudderTo - 0.03f) }, new[] { White, White, Tile, White }, White);
            var half = part.ToNode(v > 0 ? "SpoilerL" : "SpoilerR", bm, gm);
            half.Transform = Transform3D.Identity;
            rudder.AddChild(half);
        }
        return rudder;
    }

    /// <summary>The speedbrake's halves turn about the rudder's hinge (their node's Y) this far apart each, fully open, rad.</summary>
    public const float SpeedbrakeAngle = 0.75f;

    // ---- the engines and the body flap -------------------------------------------------------------

    /// <summary>The bell's profile (station, radius) from the throat to the exit.</summary>
    private static readonly (float Z, float R)[] Bell =
    {
        (NozzleThroatZ, 0.42f), (-5.15f, 0.62f), (-5.75f, 0.85f), (-6.55f, 1.03f), (NozzleExitZ, NozzleExitRadius),
    };

    private static void Engines(MeshScratch m)
    {
        foreach (var e in ShuttleLayout.Engines)
        {
            var at = new Vector3(e.X, e.Y, 0f);
            // the heat shield round the engine on the base, and the powerhead into it
            m.Tube(at + new Vector3(0, 0, BaseZ + 0.02f), at + new Vector3(0, 0, BaseZ - 0.08f), 1.0f, 0.95f, TileSeam, 16);
            m.Tube(at + new Vector3(0, 0, BaseZ - 0.02f), at + new Vector3(0, 0, NozzleThroatZ + 0.02f), 0.5f, 0.44f, Metal, 12);
            using (m.Smoothing(true))
                for (int i = 0; i + 1 < Bell.Length; i++)
                    m.Skirt(at + new Vector3(0, 0, Bell[i].Z), at + new Vector3(0, 0, Bell[i + 1].Z), Bell[i].R, Bell[i + 1].R, NozzleGrey, 18);
            // a lighter lip round the exit
            m.Ring(at + new Vector3(0, 0, NozzleExitZ - 0.02f), Vector3.Back, NozzleExitRadius - 0.05f, NozzleExitRadius + 0.04f, 0.05f, Metal, 18);
        }
    }

    private static Node3D BodyFlap(Material bm, Material gm)
    {
        float z0 = BaseZ + 0.05f, z1 = BaseZ - BodyFlapLength;
        var hinge = new Vector3(0, BodyFlapY, z0);
        var part = new AircraftPart(hinge, HingeBasis(Vector3.Right));
        Vector3[] Ring(float x) => new[]
        {
            new Vector3(x, BodyFlapY + 0.2f, z0), new Vector3(x, BodyFlapY + 0.08f, z1), new Vector3(x, BodyFlapY - 0.06f, z1), new Vector3(x, BellyY + 0.01f, z0),
        };
        part.Loft(new[] { Ring(BodyFlapHalf), Ring(-BodyFlapHalf) }, new[] { White, Tile, Tile, Tile }, Tile);
        return part.ToNode("BodyFlap", bm, gm);
    }

    // ---- the gear ---------------------------------------------------------------------------------------

    private static Node3D MainGear(string name, int sg, Material bm, Material gm)
    {
        float gx = sg * MainGearX;
        var hinge = new Vector3(gx, MainHingeY, 0f);
        var part = new AircraftPart(hinge, Basis.Identity);
        float r = MainWheelRadius;
        part.Tube(hinge, new Vector3(gx, 1.15f, 0f), 0.17f, 0.14f, Metal, 8);
        part.Tube(new Vector3(gx, 1.15f, 0f), new Vector3(gx, r, 0f), 0.1f, 0.1f, White, 8);
        part.Tube(new Vector3(gx - MainWheelSpread - 0.1f, r, 0f), new Vector3(gx + MainWheelSpread + 0.1f, r, 0f), 0.07f, 0.07f, Metal, 6);
        // torque links
        part.Tube(new Vector3(gx, 1.3f, 0f), new Vector3(gx, 0.95f, 0.3f), 0.035f, 0.035f, Tile, 4);
        part.Tube(new Vector3(gx, 0.95f, 0.3f), new Vector3(gx, 0.75f, 0f), 0.035f, 0.035f, Tile, 4);
        foreach (float off in new[] { -MainWheelSpread, MainWheelSpread })
        {
            float wx = gx + off;
            part.Tube(new Vector3(wx - MainWheelWidth / 2, r, 0f), new Vector3(wx + MainWheelWidth / 2, r, 0f), r, r, Tyre, 14);
            part.Tube(new Vector3(wx - MainWheelWidth / 2 - 0.01f, r, 0f), new Vector3(wx + MainWheelWidth / 2 + 0.01f, r, 0f), 0.3f, 0.3f, Metal, 8);
        }
        return part.ToNode(name, bm, gm);
    }

    private static Node3D NoseGear(Material bm, Material gm)
    {
        var hinge = new Vector3(0f, NoseHingeY, NoseGearZ);
        var part = new AircraftPart(hinge, Basis.Identity);
        float r = NoseWheelRadius;
        part.Tube(hinge, new Vector3(0, 1.1f, NoseGearZ), 0.13f, 0.11f, Metal, 8);
        part.Tube(new Vector3(0, 1.1f, NoseGearZ), new Vector3(0, r, NoseGearZ), 0.08f, 0.08f, White, 8);
        part.Tube(new Vector3(-NoseWheelSpread - 0.08f, r, NoseGearZ), new Vector3(NoseWheelSpread + 0.08f, r, NoseGearZ), 0.05f, 0.05f, Metal, 6);
        part.Tube(new Vector3(0, 1.25f, NoseGearZ), new Vector3(0, 0.9f, NoseGearZ + 0.28f), 0.03f, 0.03f, Tile, 4);
        foreach (float wx in new[] { -NoseWheelSpread, NoseWheelSpread })
        {
            part.Tube(new Vector3(wx - NoseWheelWidth / 2, r, NoseGearZ), new Vector3(wx + NoseWheelWidth / 2, r, NoseGearZ), r, r, Tyre, 12);
            part.Tube(new Vector3(wx - NoseWheelWidth / 2 - 0.01f, r, NoseGearZ), new Vector3(wx + NoseWheelWidth / 2 + 0.01f, r, NoseGearZ), 0.2f, 0.2f, Metal, 8);
        }
        return part.ToNode("GearNose", bm, gm);
    }

    /// <summary>A main gear's door: a black plate under the wing over its well, hinged along its outboard edge.</summary>
    private static Node3D MainGearDoor(string name, int sg, Material bm, Material gm)
    {
        const float x0 = MainGearX - 0.5f, x1 = MainGearX + 0.5f, z0 = -0.5f, z1 = 2.9f;
        float y = SurfaceY(x0, 0.7f, -1) - 0.035f;
        var hinge = new Vector3(sg * x1, y, (z0 + z1) * 0.5f);
        var part = new AircraftPart(hinge, Basis.Identity);
        part.Box(new Vector3(sg * (x0 + x1) * 0.5f, y, (z0 + z1) * 0.5f), new Vector3(x1 - x0, 0.04f, z1 - z0), Tile, new Basis(Vector3.Back, sg * Dihedral));
        return part.ToNode(name, bm, gm);
    }

    /// <summary>A nose gear door: under the forward belly (two plates following its curve), hinged along its outer edge.</summary>
    private static Node3D NoseGearDoor(string name, int sg, Material bm, Material gm)
    {
        const float half = 0.5f, z0 = NoseGearZ - 0.55f, z1 = NoseGearZ + 3.25f;
        float Belly(float z) => Section(z).Yb - 0.035f;
        var a = new Vector3(sg * half, Belly(z0), z0);
        var b = new Vector3(sg * half, Belly(z1), z1);
        var d = (Flip(b) - Flip(a)).Normalized();
        var basis = new Basis(Vector3.Up.Cross(d).Normalized(), d.Cross(Vector3.Up.Cross(d).Normalized()), d);
        var part = new AircraftPart((a + b) * 0.5f, basis);
        float zm = (z0 + z1) * 0.5f;
        foreach (var (f, t) in new[] { (z0, zm), (zm, z1) })
        {
            var p = new Vector3(sg * half * 0.5f, (Belly(f) + Belly(t)) * 0.5f, (f + t) * 0.5f);
            float pitch = Mathf.Atan2(Belly(t) - Belly(f), t - f);
            part.Box(p, new Vector3(half, 0.04f, t - f + 0.02f), Tile, new Basis(Vector3.Right, -pitch));
        }
        return part.ToNode(name, bm, gm);
    }

    // ---- the side hatch -----------------------------------------------------------------------------------

    private static Node3D Hatch(Material bm, Material gm)
    {
        float x = XAt(HatchZ, HatchY);
        var pivot = new Vector3(x + 0.1f, HatchY - HatchRadius, HatchZ);
        var part = new AircraftPart(pivot, Basis.Identity);
        part.Tube(new Vector3(x + 0.065f, HatchY, HatchZ), new Vector3(x + 0.13f, HatchY, HatchZ), HatchRadius, HatchRadius, White, 18);
        part.Tube(new Vector3(x + 0.12f, HatchY, HatchZ), new Vector3(x + 0.15f, HatchY, HatchZ), 0.17f, 0.17f, Tile, 10);
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            float a = Mathf.Tau * i / 8f;
            corners[i] = part.P(new Vector3(x + 0.165f, HatchY + Mathf.Sin(a) * 0.12f, HatchZ + Mathf.Cos(a) * 0.12f));
        }
        part.S.Pane(corners, Pane);
        return part.ToNode("Door0", bm, gm);
    }

    // ---- markings and small parts on the body ----------------------------------------------------------

    private static void Markings(MeshScratch m)
    {
        // the windscreen's centre post down the crown
        for (int i = 0; i + 1 < Stations.Length; i++)
        {
            float z0 = Stations[i], z1 = Stations[i + 1];
            if (z0 < WindscreenZ - 0.05f || z1 > Glass[^1].To + 0.15f) continue;
            m.Tube(Po(z0, 1f, 1, ShuttleLayout.Skin * 0.5f), Po(z1, 1f, 1, ShuttleLayout.Skin * 0.5f), 0.05f, 0.05f, Tile, 5);
        }

        // the hatch's dark opening (the leaf closes over it)
        float hx = XAt(HatchZ, HatchY);
        m.Tube(new Vector3(hx - 0.03f, HatchY, HatchZ), new Vector3(hx + 0.06f, HatchY, HatchZ), HatchRadius + 0.06f, HatchRadius + 0.06f, Tile, 18);

        // the payload bay doors' seams: the centre line, the hinge lines, the panels' joints
        m.Box(new Vector3(0, TopY + 0.01f, (BayRearZ + CabinRearZ) * 0.5f), new Vector3(0.06f, 0.04f, CabinRearZ - BayRearZ - 0.4f), TileSeam);
        foreach (int sg in new[] { 1, -1 })
        {
            var a = Po(BayRearZ + 0.2f, 0.27f, sg, -0.015f);
            var b = Po(CabinRearZ - 0.2f, 0.27f, sg, -0.015f);
            m.Tube(a, b, 0.03f, 0.03f, TileSeam, 4);
            foreach (float z in new[] { 6.2f, 10.8f, 15.3f })
                for (int i = 6; i < H.Length - 1; i++)
                    m.Tube(Po(z, Mathf.Max(H[i], 0.27f), sg, -0.015f), Po(z, H[i + 1], sg, -0.015f), 0.02f, 0.02f, TileSeam, 4);
        }

        // a Swiss flag on each side of the payload bay, forward of the wing's top
        foreach (int sg in new[] { 1, -1 })
        {
            const float z = 15.8f, y = 5.0f;
            float fx = XAt(z, y);
            m.Box(new Vector3(sg * (fx + 0.01f), y, z), new Vector3(0.02f, 0.95f, 0.95f), SwissRed);
            m.Box(new Vector3(sg * (fx + 0.02f), y, z), new Vector3(0.02f, 0.2f, 0.62f), White);
            m.Box(new Vector3(sg * (fx + 0.02f), y, z), new Vector3(0.02f, 0.62f, 0.2f), White);
        }

        // the forward RCS thrusters' ports on the nose's sides
        foreach (int sg in new[] { 1, -1 })
            for (int i = 0; i < 3; i++)
            {
                float z = 27.0f + i * 0.3f, y = 4.75f - i * 0.12f;
                float px = XAt(z, y);
                m.Box(new Vector3(sg * (px - 0.02f), y, z), new Vector3(0.08f, 0.14f, 0.16f), Tile);
            }
    }

    // ---- lights ---------------------------------------------------------------------------------------------

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
        float tipY = SurfaceY(WingTipX, 0.3f, 0) + 0.05f;
        float tipZ = (TipLeadingZ + TipTrailingZ) * 0.5f;
        Add("NavL", new Vector3(WingTipX + 0.06f, tipY, tipZ + 0.5f), new Vector3(0.12f, 0.14f, 0.2f), red);
        Add("NavR", new Vector3(-WingTipX - 0.06f, tipY, tipZ + 0.5f), new Vector3(0.12f, 0.14f, 0.2f), green);
        Add("StrobeL", new Vector3(WingTipX + 0.06f, tipY, tipZ - 0.6f), new Vector3(0.12f, 0.1f, 0.14f), white);
        Add("StrobeR", new Vector3(-WingTipX - 0.06f, tipY, tipZ - 0.6f), new Vector3(0.12f, 0.1f, 0.14f), white);
        Add("NavTail", new Vector3(0, FinTopY - 0.35f, FinRear(FinTopY - 0.35f) - 0.08f), new Vector3(0.12f, 0.2f, 0.12f), white);
        Add("BeaconTop", new Vector3(0, TopY + 0.08f, 11.5f), new Vector3(0.22f, 0.12f, 0.22f), red);
        Add("BeaconBottom", new Vector3(0, BellyY - 0.05f, 9.0f), new Vector3(0.22f, 0.1f, 0.22f), red);
        const float lz = 23.6f;
        float ly = Section(lz).Yb - 0.04f;
        Add("LandingL", new Vector3(0.9f, ly, lz), new Vector3(0.24f, 0.08f, 0.24f), white);
        Add("LandingR", new Vector3(-0.9f, ly, lz), new Vector3(0.24f, 0.08f, 0.24f), white);
    }

    // ---- the effects: the main engines' plumes, the drive's glow, re-entry's plasma -----------------------

    /// <summary>A plume's length, m, at the spool (0 idle .. 1 full), and its width over the nozzle's exit.</summary>
    public static float PlumeLength(float spool) => 5f + 22f * spool;
    public static float PlumeWidth(float spool) => NozzleExitRadius * (0.92f + 0.25f * spool);
    /// <summary>The drive's glow: its length behind the nozzles, m.</summary>
    public const float DriveLength = 4.5f;

    private static void AddEffects(Node3D root)
    {
        var plume = new ShaderMaterial { Shader = PlumeShader };
        var plumeMesh = PlumeMesh();
        var glow = new Node3D { Name = "DriveGlow", Visible = false };
        var glowMesh = DriveMesh();
        var glowMat = new ShaderMaterial { Shader = DriveShader };
        for (int i = 0; i < ShuttleLayout.Engines.Length; i++)
        {
            var e = ShuttleLayout.Engines[i];
            var at = Flip(new Vector3(e.X, e.Y, NozzleExitZ + 0.35f));
            root.AddChild(new MeshInstance3D
            {
                Name = $"Plume{i}", Mesh = plumeMesh, MaterialOverride = plume, Position = at, Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Scale = new Vector3(PlumeWidth(1f), PlumeWidth(1f), PlumeLength(1f)),
            });
            glow.AddChild(new MeshInstance3D
            {
                // just behind the exit: its disc clear of the bell's lip
                Name = $"Glow{i}", Mesh = glowMesh, MaterialOverride = glowMat, Position = Flip(new Vector3(e.X, e.Y, NozzleExitZ - 0.12f)),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Scale = new Vector3(NozzleExitRadius, NozzleExitRadius, DriveLength),
            });
        }
        root.AddChild(glow);
        root.AddChild(new MeshInstance3D
        {
            Name = "Plasma", Mesh = PlasmaMesh(), MaterialOverride = new ShaderMaterial { Shader = PlasmaShader }, Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }

    /// <summary>
    /// One plume, 1 m long and 1 m across at the nozzle, along the node's +Z (aft): a long soft outer
    /// cone and a short white core inside it (its vertex colour white, the outer's grey, for the shader).
    /// Scaled by its node to the spool.
    /// </summary>
    [Core.Showcase("Parts", "Shuttle plume")]
    public static ArrayMesh PlumeMesh()
    {
        var s = new MeshScratch();
        using (s.Smoothing(true))
        {
            s.Skirt(Vector3.Zero, new Vector3(0, 0, -1f), 0.96f, 1.5f, new Color(0.5f, 0.5f, 0.5f), 16);
            s.Skirt(new Vector3(0, 0, 0.02f), new Vector3(0, 0, -0.42f), 0.8f, 0.12f, Colors.White, 14);
        }
        return s.Build();
    }

    /// <summary>The drive's glow at one nozzle: a bright disc across the exit, a ring round it and a short cone, along +Z (aft), unit sized.</summary>
    [Core.Showcase("Parts", "Shuttle drive glow")]
    public static ArrayMesh DriveMesh()
    {
        var s = new MeshScratch();
        using (s.Smoothing(true))
        {
            s.Tube(new Vector3(0, 0, 0.01f), new Vector3(0, 0, -0.01f), 0.97f, 0.97f, Colors.White, 18);
            // the ring stays inside half the engines' spacing: neighbours' rings never overlap
            s.Ring(new Vector3(0, 0, -0.03f), Vector3.Back, 1.02f, 1.1f, 0.02f, Colors.White, 24);
            s.Skirt(Vector3.Zero, new Vector3(0, 0, -1f), 0.95f, 0.3f, new Color(0.5f, 0.5f, 0.5f), 18);
        }
        return s.Build();
    }

    /// <summary>
    /// Re-entry's plasma: open sheets (no end caps) a little off the belly, the chines and round the
    /// nose's lower half (none over the windscreen: the pilots look out through it), and under each wing
    /// round its leading edge. Two sheaths: a bright one close in (white vertices), a softer one further out (grey).
    /// </summary>
    [Core.Showcase("Parts", "Shuttle plasma shell")]
    public static ArrayMesh PlasmaMesh()
    {
        var s = new MeshScratch();
        using (s.Smoothing(true))
        {
            Sheath(s, 0.3f, 0f, Colors.White);
            Sheath(s, 0.95f, 0.35f, new Color(0.5f, 0.5f, 0.5f));
        }
        return s.Build();
    }

    /// <summary>The section's points round the hull, both sides (for the plasma's rings).</summary>
    private static readonly float[] SheathH = { -1f, -0.8f, -0.6f, -0.4f, -0.2f, 0f, 0.3f, 0.6f, 1f };

    /// <summary>One sheath, <paramref name="d"/> off the skin, its ends <paramref name="longer"/> further out (the two never meet).</summary>
    private static void Sheath(MeshScratch s, float d, float longer, Color colour)
    {
        // closed rings round the grown section; the drape leaves out their upper half
        Vector3[] Ring(float z, float scale = 1f)
        {
            float zz = Mathf.Clamp(z, BaseZ, NoseZ);
            var r = new List<Vector3>();
            foreach (float h in SheathH) r.Add(Po(zz, h, 1, -d));
            for (int i = SheathH.Length - 2; i >= 1; i--) r.Add(Po(zz, SheathH[i], -1, -d));
            for (int i = 0; i < r.Count; i++)
                r[i] = new Vector3(r[i].X * scale, NoseTipY + (r[i].Y - NoseTipY) * scale, z);
            return r.ToArray();
        }
        var rings = new List<Vector3[]>();
        rings.Add(Ring(BaseZ - 0.1f - longer));
        foreach (float z in new[] { 4f, 12f, 19f, 23f, 25.5f, 27.2f, 28.4f, NoseZ }) rings.Add(Ring(z));
        rings.Add(Ring(NoseZ + 0.3f + d * 0.4f, 0.75f));
        rings.Add(Ring(NoseZ + 0.55f + d * 0.8f, 0.3f));
        var bands = Rep(colour, rings.Count);
        s.Drape(rings.ToArray(), 0, rings.Count, bands, Vector3.Up, 1.5f);

        // under each wing and round its leading edge
        foreach (int sg in new[] { 1, -1 })
        {
            Vector3[] W(float x)
            {
                float zl = Le(x), zt = Te(x);
                Vector3 P(float f, int v, float dy, float dz) => WPt(sg, x, f, v) + new Vector3(0, dy, dz);
                return new[]
                {
                    P(0f, 0, 0f, d), P(0.1f, 1, d, 0f), P(0.5f, 1, d, 0f), P(1f, 1, d * 0.5f, -d * 0.6f),
                    P(1f, -1, -d, -d * 0.6f), P(0.5f, -1, -d, 0f), P(0.1f, -1, -d, 0f), P(0.02f, -1, -d * 0.7f, d * 0.7f),
                };
            }
            var wing = new[] { W(2.7f + longer), W(WingKinkX), W(8f), W(WingTipX - 0.05f + longer) };
            s.Drape(wing, 0, wing.Length, Rep(colour, wing.Length), Vector3.Up, 1.5f);
        }
    }

    // ---- the effects' shaders: additive, unshaded, depth tested (the body hides them) -------------------

    private static Shader? _plumeShader, _driveShader, _plasmaShader;

    /// <summary>The plume: a white core, a pale flame turning orange down its length, shock diamonds; <c>power</c> 0..1 its brightness.</summary>
    private static Shader PlumeShader => _plumeShader ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
            uniform float power = 1.0;
            uniform float span = 27.0;
            varying float t;
            void vertex() { t = clamp(VERTEX.z, 0.0, 1.0); }
            void fragment() {
                float core = step(0.6, COLOR.r);
                float facing = abs(dot(normalize(NORMAL), normalize(VIEW)));
                float diamonds = 0.7 + 0.3 * cos(t * span * 1.9);
                vec3 flame = mix(vec3(0.8, 0.9, 1.0), vec3(1.0, 0.55, 0.18), smoothstep(0.04, 0.5, t));
                vec3 outer = flame * pow(1.0 - t, 1.4) * (0.15 + 0.85 * facing * facing) * diamonds * 0.85;
                vec3 inner = vec3(1.0, 0.96, 0.88) * clamp(1.0 - t * 2.4, 0.0, 1.0) * (0.45 + 0.55 * facing) * 1.25;
                ALBEDO = mix(outer, inner, core) * power;
            }
            """,
    };

    /// <summary>The drive's glow: blue-white, pulsing down its length.</summary>
    private static Shader DriveShader => _driveShader ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
            varying float t;
            void vertex() { t = clamp(VERTEX.z, 0.0, 1.0); }
            void fragment() {
                float facing = abs(dot(normalize(NORMAL), normalize(VIEW)));
                float pulse = 0.8 + 0.2 * sin(TIME * 5.0 - t * 9.0);
                vec3 c = mix(vec3(0.85, 0.95, 1.0), vec3(0.3, 0.55, 1.0), smoothstep(0.0, 0.7, t));
                float bright = COLOR.r > 0.6 ? 1.3 : 0.75;
                ALBEDO = c * pow(1.0 - t, 1.8) * pulse * (0.3 + 0.7 * facing) * bright;
            }
            """,
    };

    /// <summary>
    /// Re-entry's plasma: brightest where the shell faces the oncoming air (forward and under, the node's
    /// −Z and −Y), at its rim, flickering; orange at a little <c>heat</c>, pink, then white at 1.
    /// </summary>
    private static Shader PlasmaShader => _plasmaShader ??= new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
            uniform float heat = 0.0;
            varying float wind;
            varying vec3 local;
            void vertex() {
                wind = clamp(dot(NORMAL, normalize(vec3(0.0, -0.75, -0.66))), 0.0, 1.0);
                local = VERTEX;
            }
            void fragment() {
                float rim = 1.0 - abs(dot(normalize(NORMAL), normalize(VIEW)));
                float flicker = 0.75 + 0.25 * sin(local.z * 0.9 + TIME * 23.0) * sin(local.x * 2.3 - TIME * 17.0);
                vec3 hot = mix(vec3(1.0, 0.32, 0.08), vec3(1.0, 0.42, 0.6), smoothstep(0.35, 0.8, heat));
                hot = mix(hot, vec3(1.0, 0.9, 0.85), smoothstep(0.85, 1.0, heat));
                float k = heat * (0.15 + 0.85 * wind) * (0.3 + 0.7 * rim) * flicker;
                ALBEDO = hot * k * mix(0.55, 1.1, COLOR.r);
            }
            """,
    };

    // ---- the model viewer --------------------------------------------------------------------------------
    // the parked orbiter shows through Rideable's "Rides" set (RideKind.Shuttle); these show it at work

    [Core.Showcase("Aircraft", "Space shuttle, main engines firing")]
    private static Node3D ShowcaseFiring() => Posed(new AirlinerLook { Gear = 0f, Spool = 1f, Lever = 1f, Airborne = true });

    [Core.Showcase("Aircraft", "Space shuttle, re-entry")]
    private static Node3D ShowcaseReentry() => Posed(new AirlinerLook { Gear = 0f, Spool = 0.05f, Heat = 0.85f, Airborne = true, Stick = new Vector2(0.3f, 0.4f) });

    [Core.Showcase("Aircraft", "Space shuttle, cruise drive")]
    private static Node3D ShowcaseDrive() => Posed(new AirlinerLook { Gear = 0f, Spool = 0.6f, Drive = true, Airborne = true });

    [Core.Showcase("Aircraft", "Space shuttle, landing (speedbrake open)")]
    private static Node3D ShowcaseLanding() => Posed(new AirlinerLook { Gear = 1f, Spool = 0.05f, Spoilers = 1f, Stick = new Vector2(0f, -0.5f), Doors = 1 });

    private static Node3D Posed(AirlinerLook look)
    {
        var rig = AirlinerRig.CreateShuttle();
        rig.Snap(look);
        return rig;
    }
}
