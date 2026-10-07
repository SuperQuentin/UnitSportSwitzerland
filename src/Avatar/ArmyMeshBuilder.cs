using Godot;
using UnitSport.Player;
using static UnitSport.Avatar.HeavyMesh;

namespace UnitSport.Avatar;

/// <summary>
/// The Swiss Army's light vehicles (#714), low-poly like the pickup and at their published
/// dimensions: the Mowag Duro II troop transporter (<see cref="Duro"/>: a short two-door cab, a
/// canvas-covered bed with a bench down each side, the spare wheel hung under the tail) and the
/// Mercedes-Benz G 300 CDI estate (<see cref="GClass"/>: the boxy body with its wings and
/// bonnet, four doors, the spare wheel on the tailgate). Both carry an "M" military plate. The Iveco
/// Trakker lorry is <see cref="TruckMeshBuilder"/>'s cab under the same <see cref="CanvasBody"/>.
/// Authored facing +Z with the origin on the ground under the centre of mass (z = cg − metres
/// behind the front), as the trucks; colours from <see cref="HeavyLook"/>. Wheels are the rig's.
/// </summary>
public static class ArmyMeshBuilder
{
    private static readonly Color Plank = new(0.34f, 0.27f, 0.18f);
    private static readonly Color BenchFrame = new(0.17f, 0.19f, 0.14f);
    private static readonly Color PlateWhite = new(0.95f, 0.95f, 0.92f);
    private static readonly Color PlateInk = new(0.06f, 0.06f, 0.07f);

    // ---- the canvas body: a Duro's bed and a Trakker's cargo body ----

    /// <summary>
    /// A cargo body under a canvas tilt (#714). Stations are metres behind the section's front, heights
    /// metres over the ground. <see cref="Board"/> is the height of the side boards over the floor, the
    /// canvas runs from the eave up to the ridge, and a side that is <see cref="OpenSides"/> has its
    /// curtain rolled up under the eave so the benches show. <see cref="Places"/> seats a bench
    /// down each side facing across.
    /// </summary>
    public readonly record struct Canvas(float From, float To, float Floor, float Board, float Eave, float Ridge,
        float Width, bool OpenSides, int Places);

    /// <summary>Seat height over the bench's floor: the cushion's top, m.</summary>
    private const float BenchHeight = 0.42f;

    /// <summary>
    /// The floor, side boards, tailgate, headboard, benches, bows and canvas of a body, built into
    /// <paramref name="m"/>; returns the bench seats (the passengers' anchors, facing across).
    /// </summary>
    public static SeatAnchor[] CanvasBody(MeshScratch m, float cg, Canvas c, HeavyLook look)
    {
        float half = c.Width * 0.5f;
        float boardTop = c.Floor + c.Board;
        // the deck on its frame, the boards round it
        Along(m, cg, c.From, c.To, c.Floor - 0.12f, c.Floor, c.Width, Plank);
        foreach (float sx in new[] { -1f, 1f })
        {
            Along(m, cg, c.From, c.To, c.Floor, boardTop, 0.05f, look.Paint, sx * (half - 0.025f));
            Along(m, cg, c.From, c.To, boardTop - 0.06f, boardTop, 0.09f, look.Accent, sx * (half - 0.045f));
        }
        Along(m, cg, c.From, c.From + 0.06f, c.Floor, boardTop, c.Width, look.Paint);
        Along(m, cg, c.To - 0.06f, c.To, c.Floor, boardTop, c.Width, look.Paint);
        Along(m, cg, c.To - 0.06f, c.To - 0.03f, boardTop - 0.08f, boardTop, 0.5f, look.Accent);

        // ---- the canvas: a half profile from the board up the wall, over the shoulder to the ridge ----
        float wallX = half - 0.02f;
        var p0 = new Vector2(wallX, boardTop);
        var p1 = new Vector2(wallX, c.Eave);
        var p2 = new Vector2(half - 0.16f, c.Eave + (c.Ridge - c.Eave) * 0.55f);
        var p3 = new Vector2(half - 0.46f, c.Ridge);
        Color canvas = look.Cargo, rib = canvas.Darkened(0.35f);
        float z0 = c.From, z1 = c.To;

        void Panel(Vector2 a, Vector2 b, float from, float to, Color colour, float thick, float lift)
        {
            var d = b - a;
            float len = d.Length();
            var mid = (a + b) * 0.5f;
            var n = new Vector2(d.Y, -d.X) / len;
            float ang = Mathf.Atan2(d.Y, d.X);
            foreach (float sx in new[] { -1f, 1f })
            {
                float turn = sx > 0 ? ang : Mathf.Pi - ang;
                m.Box(new Vector3(sx * (mid.X + n.X * lift), mid.Y + n.Y * lift, cg - (from + to) * 0.5f),
                    new Vector3(len, thick, to - from), colour, new Basis(Vector3.Back, turn));
            }
        }

        if (!c.OpenSides) Panel(p0, p1, z0, z1, canvas, 0.03f, 0f);
        Panel(p1, p2, z0, z1, canvas, 0.03f, 0f);
        Panel(p2, p3, z0, z1, canvas, 0.03f, 0f);
        Along(m, cg, z0, z1, c.Ridge - 0.03f, c.Ridge, p3.X * 2f, canvas);
        if (c.OpenSides)
        {
            // the curtain rolled up along each eave, tied back
            foreach (float sx in new[] { -1f, 1f })
                m.Tube(new Vector3(sx * (wallX - 0.02f), c.Eave - 0.05f, cg - z0 - 0.05f),
                    new Vector3(sx * (wallX - 0.02f), c.Eave - 0.05f, cg - z1 + 0.05f), 0.06f, canvas, 6);
        }

        // the bows over the tilt, a little proud of it; on an open body also the posts they stand on
        int bows = Mathf.Max(2, Mathf.RoundToInt((z1 - z0) / 0.9f));
        for (int i = 0; i < bows; i++)
        {
            float at = z0 + (i + 0.5f) * (z1 - z0) / bows;
            if (!c.OpenSides) Panel(p0, p1, at - 0.03f, at + 0.03f, rib, 0.05f, 0.012f);
            Panel(p1, p2, at - 0.03f, at + 0.03f, rib, 0.05f, 0.012f);
            Panel(p2, p3, at - 0.03f, at + 0.03f, rib, 0.05f, 0.012f);
            Along(m, cg, at - 0.03f, at + 0.03f, c.Ridge, c.Ridge + 0.025f, p3.X * 2f, rib);
            if (c.OpenSides)
                foreach (float sx in new[] { -1f, 1f })
                    Along(m, cg, at - 0.03f, at + 0.03f, boardTop, c.Eave, 0.05f, rib, sx * (half - 0.05f));
        }

        // the ends: the headboard's canvas against the cab, the tail's flap; each a stack of slabs inside the profile
        void End(float from, float to, float bottom)
        {
            Along(m, cg, from, to, bottom, c.Eave, wallX * 2f, canvas);
            Along(m, cg, from, to, c.Eave, p2.Y, p2.X * 2f, canvas);
            Along(m, cg, from, to, p2.Y, c.Ridge, p3.X * 2f, canvas);
        }
        End(z0, z0 + 0.04f, boardTop);
        if (c.OpenSides)
            // a valance across the top of the open tail, the rest left open for the benches
            End(z1 - 0.04f, z1, c.Eave - 0.25f);
        else
        {
            End(z1 - 0.04f, z1, boardTop);
            Along(m, cg, z1 - 0.045f, z1 - 0.035f, boardTop, c.Eave, 0.03f, rib);
        }

        // ---- the benches: a plank seat down each side on a frame, a back rail on the boards ----
        var seats = new List<SeatAnchor>();
        if (c.Places <= 0) return seats.ToArray();
        float b0 = c.From + 0.22f, b1 = c.To - 0.22f, step = (b1 - b0) / c.Places;
        foreach (float sx in new[] { -1f, 1f })
        {
            float bx = sx * (half - 0.05f - 0.2f);
            Along(m, cg, b0 - 0.05f, b1 + 0.05f, c.Floor, c.Floor + BenchHeight - 0.04f, 0.4f, BenchFrame, bx);
            Along(m, cg, b0 - 0.05f, b1 + 0.05f, c.Floor + BenchHeight - 0.04f, c.Floor + BenchHeight, 0.42f, Plank, bx);
            Along(m, cg, b0 - 0.05f, b1 + 0.05f, c.Floor + BenchHeight + 0.1f, c.Floor + BenchHeight + 0.5f, 0.04f, Plank, sx * (half - 0.08f));
            for (int i = 0; i < c.Places; i++)
            {
                float at = b0 + (i + 0.5f) * step;
                // occupants face across: the vehicle's left bench (authored +X, node −X) faces the right (node +X)
                seats.Add(new SeatAnchor(0, CarMeshBuilder.Turned(new Vector3(sx * (half - 0.05f - 0.2f), c.Floor + BenchHeight + 0.09f, cg - at)), 0.08f, c.Floor)
                {
                    Yaw = sx > 0 ? -Mathf.Pi * 0.5f : Mathf.Pi * 0.5f,
                });
            }
        }
        return seats.ToArray();
    }

    // ---- small parts ----

    /// <summary>
    /// A Swiss military number plate (#714): white with black figures, which start with an "M". Stands
    /// proud of the body face at <paramref name="z"/> by a centimetre, readable from the side
    /// <paramref name="dir"/> points to (+1 the front, −1 the back).
    /// </summary>
    private static void Plate(MeshScratch m, float x, float y, float z, float dir)
    {
        m.Box(new Vector3(x, y, z + dir * 0.01f), new Vector3(0.30f, 0.12f, 0.02f), PlateWhite);
        float zz = z + dir * 0.022f;
        void Ink(float dx, float dy, float w, float h, float turn = 0f) =>
            m.Box(new Vector3(x + dx, y + dy, zz), new Vector3(w, h, 0.012f), PlateInk, new Basis(Vector3.Back, turn));
        // the M: two uprights and a V between them
        Ink(-0.105f, 0f, 0.012f, 0.07f);
        Ink(-0.045f, 0f, 0.012f, 0.07f);
        Ink(-0.0975f, 0.005f, 0.04f, 0.011f, -0.95f);
        Ink(-0.0525f, 0.005f, 0.04f, 0.011f, 0.95f);
        // the figures
        for (int i = 0; i < 4; i++) Ink(-0.005f + i * 0.04f, 0f, 0.022f, 0.07f);
    }

    /// <summary>A spare wheel standing on its edge with its axis along z: the tyre, a cover over the rim.</summary>
    private static void SpareUpright(MeshScratch m, float x, float y, float zFrom, float zTo, float radius, Color cover)
    {
        m.Tube(new Vector3(x, y, zFrom), new Vector3(x, y, zTo), radius, Rubber, 12);
        m.Tube(new Vector3(x, y, zTo), new Vector3(x, y, zTo + 0.025f * Mathf.Sign(zTo - zFrom)), radius * 0.72f, cover, 10);
    }

    /// <summary>The door bit for a leaf: front right 0, front left 1, rear right 2, rear left 3 (a car's, <see cref="CarRig"/>).</summary>
    private static int DoorIndex(bool left, bool rear) => (rear ? 2 : 0) + (left ? 1 : 0);

    /// <summary>
    /// A car door (#463): a leaf hinged at its front edge and swinging out, the panel to the window line, a
    /// dark handle, the glass to the rail. The front one's glass runs up under the pillar's rake.
    /// </summary>
    private static void Door(List<HeavyDoorLeaf> leaves, float cg, float sx, bool rear, float at0, float at1, float bodyW, float wall,
        float floor, float belt, float glassTop, float wsTopAt, HeavyLook look)
    {
        float Z(float at) => cg - at;
        float xw = sx * (bodyW * 0.5f - wall * 0.5f), x = sx * bodyW * 0.5f;
        var leaf = new MeshScratch();
        Along(leaf, cg, at0 + 0.01f, at1 - 0.01f, floor - 0.12f, belt + 0.04f, wall, look.Paint, xw);
        Along(leaf, cg, at1 - 0.32f, at1 - 0.12f, belt - 0.12f, belt - 0.08f, 0.03f, look.Accent, x + sx * 0.015f);
        if (rear) SidePane(leaf, x, Z(at0 + 0.04f), Z(at1 - 0.04f), belt + 0.04f, glassTop);
        else
            leaf.Pane(new[]
            {
                new Vector3(x, belt + 0.04f, Z(at0 + 0.08f)), new Vector3(x, belt + 0.04f, Z(at1 - 0.04f)),
                new Vector3(x, glassTop, Z(at1 - 0.04f)), new Vector3(x, glassTop, Z(wsTopAt + 0.05f)),
            }, PaneTint);
        var pivot = new Vector3(x, 0f, Z(at0));
        // node space: the left side is −X; front-hinged, a left door swings out with a negative turn
        leaves.Add(new HeavyDoorLeaf(DoorIndex(sx > 0, rear), leaf.Build(pivot), new Vector3(-pivot.X, 0f, -pivot.Z), sx > 0 ? -1.15f : 1.15f)
        {
            Centre = new Vector3(-x, (floor + glassTop) * 0.5f, -Z((at0 + at1) * 0.5f)),
        });
    }

    /// <summary>The driver's eye in a cab frame, authored space.</summary>
    private static Vector3 Driver(CabFrame f)
    {
        var seat = HeavyCabin.SeatFor(f);
        return HumanMeshBuilder.DriverEye(seat.Hip, seat.Recline);
    }

    // ================= Mowag Duro II =================

    // stations (metres behind the bumper) and heights (over the ground)
    private const float DuroWs = 1.28f, DuroBack = 2.80f, DuroBedFrom = 2.86f;
    private const float DuroFloor = 0.86f, DuroBelt = 1.42f, DuroRoof = 2.25f, DuroGlass = 2.10f, DuroBedFloor = 1.14f;
    /// <summary>The doors' extent: front edge, back edge.</summary>
    private const float DuroDoor0 = DuroWs + 0.12f, DuroDoor1 = DuroBack - 0.12f;

    /// <summary>The Duro's cab frame, the origin on the ground under the centre of mass <paramref name="cg"/> metres behind the front.</summary>
    private static CabFrame DuroCab(SectionSpec s, float cg) => new()
    {
        Front = cg - DuroWs, Floor = DuroFloor, Ceiling = DuroRoof - 0.1f, WsBase = DuroBelt + 0.03f, WsTop = DuroGlass,
        DashTop = DuroBelt, InnerHalf = (s.Width - 0.1f) * 0.5f - 0.07f,
        Nose = 0.3f, DriverX = s.Width * 0.5f - 0.6f, HipRise = 0.42f, Recline = 0.25f, ColumnTilt = 0.65f, WheelRadius = 0.22f,
        DashToX = -((s.Width - 0.1f) * 0.5f - 0.07f), Clutch = false, PassengerSeat = true,
    };

    public static HeavyParts Duro(HeavySpec spec, int section, float load)
    {
        var s = spec.Sections[section];
        var look = spec.Look;
        float cg = Cg(s, load);
        float hw = s.Width * 0.5f;
        float bodyW = s.Width - 0.1f;
        const float wall = 0.07f;
        float inner = bodyW * 0.5f - wall;
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var rev = new MeshScratch();
        float front = cg;
        float rear = cg - s.Length;
        float r = Tyre.Radius(s.Axles[0].Tyre);
        float Z(float at) => cg - at;

        // ---- the front: bumper, grille, the bonnet and wings over open wheel arches ----
        Along(m, cg, 0f, 0.2f, 0.52f, 0.84f, bodyW, look.Lower);
        Along(m, cg, 0.04f, 0.3f, 0.84f, DuroBelt - 0.02f, bodyW, look.Paint);
        Along(m, cg, 0f, 0.1f, 0.9f, 1.3f, bodyW - 0.8f, look.Accent);
        Skirt(m, s, cg, 0.2f, DuroWs, 0.56f, 1.1f, bodyW, look.Paint);
        Along(m, cg, 0.3f, DuroWs, 1.1f, DuroBelt - 0.02f, bodyW, look.Paint);
        // the engine and axle between the wheels, so the arches are not holes
        Along(m, cg, 0.3f, DuroWs - 0.1f, 0.55f, 1.1f, 1.2f, Trim);
        foreach (var a in s.Axles)
            Along(m, cg, a.At - r - 0.12f, a.At + r + 0.12f, 2f * r * 0.97f + 0.04f, 2f * r * 0.97f + 0.1f, s.Width - 0.02f, look.Lower);

        // ---- the cab: hollow above its floor, two doors, glass you see through ----
        Skirt(m, s, cg, DuroWs, DuroBack, 0.56f, DuroFloor, bodyW, look.Lower);
        Along(m, cg, DuroWs + wall, DuroBack - wall, DuroFloor, DuroFloor + 0.01f, inner * 2f, HeavyCabin.FloorColour);
        Along(m, cg, DuroWs + 0.1f, DuroBack - 0.1f, 0.5f, 0.56f, s.Width - 0.1f, Trim);   // the step
        // the cowl, the upright windscreen, its pillars, the roof and the headlining
        const float wsTopAt = DuroWs + 0.24f;
        Along(m, cg, DuroWs - 0.04f, DuroWs + 0.14f, DuroFloor, DuroBelt + 0.03f, bodyW, look.Paint);
        float half = bodyW * 0.5f - 0.1f;
        m.Pane(new[]
        {
            new Vector3(-half, DuroBelt + 0.03f, Z(DuroWs + 0.1f)), new Vector3(half, DuroBelt + 0.03f, Z(DuroWs + 0.1f)),
            new Vector3(half - 0.04f, DuroGlass, Z(wsTopAt)), new Vector3(-half + 0.04f, DuroGlass, Z(wsTopAt)),
        }, PaneTint);
        foreach (float sx in new[] { -1f, 1f })
            m.Tube(new Vector3(sx * (half + 0.04f), DuroBelt + 0.03f, Z(DuroWs + 0.1f)), new Vector3(sx * (half + 0.01f), DuroRoof - 0.06f, Z(wsTopAt)), 0.05f, look.Paint, 4);
        Along(m, cg, wsTopAt - 0.05f, DuroBack, DuroRoof - 0.09f, DuroRoof, bodyW - 0.06f, look.Paint);
        Along(m, cg, wsTopAt, DuroBack - wall, DuroRoof - 0.1f, DuroRoof - 0.09f, inner * 2f, HeavyCabin.Lining);
        foreach (float sx in new[] { -1f, 1f })
        {
            float xw = sx * (bodyW * 0.5f - wall * 0.5f);
            Along(m, cg, DuroBack - 0.12f, DuroBack, DuroFloor, DuroRoof - 0.09f, wall, look.Paint, xw);
            Along(m, cg, wsTopAt, DuroBack, DuroGlass, DuroRoof - 0.09f, wall, look.Paint, xw);
        }
        var leaves = new List<HeavyDoorLeaf>();
        foreach (float sx in new[] { -1f, 1f })
            Door(leaves, cg, sx, false, DuroDoor0, DuroDoor1, bodyW, wall, DuroFloor, DuroBelt, DuroGlass, wsTopAt, look);
        // the back wall, and the engine tunnel down the middle
        Along(m, cg, DuroBack - wall, DuroBack, DuroFloor, DuroRoof - 0.09f, bodyW, look.Paint);
        Along(m, cg, DuroWs + 0.4f, DuroBack - 0.3f, DuroFloor, DuroFloor + 0.16f, 0.4f, HeavyCabin.Dash);

        // mirrors on arms: a main and a convex one each side
        var mirrors = new List<(string, Vector3, Vector2)>();
        foreach (float sx in new[] { -1f, 1f })
        {
            m.Tube(new Vector3(sx * (bodyW * 0.5f), DuroBelt + 0.5f, Z(DuroWs + 0.3f)), new Vector3(sx * (hw + 0.1f), DuroBelt + 0.56f, Z(DuroWs + 0.2f)), 0.025f, Trim, 4);
            mirrors.Add((sx > 0 ? "MirrorLeft" : "MirrorRight", new Vector3(sx * (hw + 0.12f), DuroBelt + 0.6f, Z(DuroWs + 0.24f)), new Vector2(0.2f, 0.34f)));
        }
        var cockpit = HeavyCabin.Build(m, DuroCab(s, cg), HeavyCabin.GaugesFor(spec.LimiterKmh, spec.Redline),
            HeavyDriveline.AirLow, HeavyDriveline.AirMax, mirrors);
        var seats = new List<SeatAnchor>
        {
            new(0, CarMeshBuilder.Turned(cockpit.Seat.Hip), cockpit.Seat.Recline, DuroFloor),
            new(0, CarMeshBuilder.Turned(cockpit.Seat.Hip with { X = -cockpit.Seat.Hip.X }), cockpit.Seat.Recline, DuroFloor),
        };

        // head lamps and indicators either side of the grille
        foreach (float sx in new[] { -1f, 1f })
        {
            Lamp(head, sx * (bodyW * 0.5f - 0.2f), 1.12f, front - 0.02f, 0.26f, 0.2f, 0.06f, HeadLamp);
            Lamp(m, sx * (bodyW * 0.5f - 0.2f), 0.94f, front - 0.02f, 0.26f, 0.08f, 0.06f, Amber);
        }

        // ---- the bed: boards on the frame, the benches, the canvas over bows ----
        Skirt(m, s, cg, DuroBedFrom, s.Length, 0.56f, DuroBedFloor, bodyW, look.Paint);
        var bed = new Canvas(DuroBedFrom, s.Length - 0.12f, DuroBedFloor, 0.5f, 2.2f, 2.65f, bodyW, true, 5);
        seats.AddRange(CanvasBody(m, cg, bed, look));
        // the chassis between the wheels, the spare wheel hung flat under the tail, the rear bumper bar
        Along(m, cg, DuroBedFrom, s.Length - 0.1f, 0.62f, 0.82f, 0.9f, Trim);
        m.Tube(new Vector3(0, 0.6f, Z(5.12f)), new Vector3(0, 0.9f, Z(5.12f)), 0.5f, Rubber, 12);
        m.Tube(new Vector3(0, 0.9f, Z(5.12f)), new Vector3(0, 0.925f, Z(5.12f)), 0.34f, look.Paint, 10);
        Along(m, cg, s.Length - 0.12f, s.Length, 0.5f, 0.72f, bodyW - 0.2f, look.Lower);
        foreach (float sx in new[] { -1f, 1f })
        {
            Lamp(tail, sx * (bodyW * 0.5f - 0.15f), 0.9f, rear - 0.02f, 0.3f, 0.16f, 0.04f, TailLamp);
            Lamp(rev, sx * (bodyW * 0.5f - 0.45f), 0.9f, rear - 0.02f, 0.14f, 0.12f, 0.04f, White);
        }
        Plate(m, 0f, 0.6f, front, 1f);
        Plate(m, 0f, 0.62f, rear, -1f);

        return new HeavyParts(m.Build(), head.Build(), tail.Build(), rev.Build(), Wheels(s, cg), leaves.ToArray())
        {
            Cockpit = cockpit,
            Seats = seats.ToArray(),
            CarDoors = true,
        };
    }

    // ================= Mercedes-Benz G 300 CDI =================

    private const float GWs = 1.28f, GBPillar = 2.34f, GRearDoorEnd = 3.14f, GBack = 4.18f;
    private const float GFloor = 0.55f, GSill = 0.42f, GBelt = 1.12f, GRoof = 1.97f, GGlass = 1.84f, GWingTop = 1.05f;

    private static CabFrame GCab(SectionSpec s, float cg) => new()
    {
        Front = cg - GWs, Floor = GFloor, Ceiling = GRoof - 0.1f, WsBase = GBelt, WsTop = GGlass, DashTop = GBelt,
        InnerHalf = (s.Width - 0.06f) * 0.5f - 0.07f,
        Nose = 0.3f, DriverX = s.Width * 0.5f - 0.5f, HipRise = 0.36f, Recline = 0.3f, ColumnTilt = 0.5f, WheelRadius = 0.19f,
        DashToX = -((s.Width - 0.06f) * 0.5f - 0.07f), Clutch = false, PassengerSeat = true,
    };

    public static HeavyParts GClass(HeavySpec spec, int section, float load)
    {
        var s = spec.Sections[section];
        var look = spec.Look;
        float cg = Cg(s, load);
        float hw = s.Width * 0.5f;
        float bodyW = s.Width - 0.06f;           // the fender flares stand out past the body
        const float wall = 0.07f;
        float inner = bodyW * 0.5f - wall;
        var m = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        var rev = new MeshScratch();
        float front = cg;
        float rear = cg - s.Length;
        float r = Tyre.Radius(s.Axles[0].Tyre);
        float Z(float at) => cg - at;
        float wheelTop = 2f * r * 0.97f;

        // ---- the front: steel bumper, the upright grille, wings with their round lamps, the flat bonnet ----
        Along(m, cg, 0f, 0.2f, GSill - 0.04f, 0.62f, bodyW, look.Lower);
        Along(m, cg, 0.14f, 0.24f, 0.62f, 1.02f, bodyW - 0.6f, look.Accent);
        Skirt(m, s, cg, 0.14f, GWs, GSill, wheelTop + 0.05f, bodyW, look.Paint);
        Along(m, cg, 0.14f, GWs, wheelTop + 0.05f, GWingTop, bodyW, look.Paint);
        Along(m, cg, 0.24f, GWs, GWingTop, GBelt + 0.03f, bodyW - 0.62f, look.Paint);
        Along(m, cg, 0.3f, GWs - 0.1f, 0.45f, wheelTop + 0.05f, 1.0f, Trim);   // the engine between the wings
        foreach (var a in s.Axles)
            Along(m, cg, a.At - r - 0.1f, a.At + r + 0.1f, wheelTop + 0.02f, wheelTop + 0.12f, s.Width - 0.01f, look.Lower);
        foreach (float sx in new[] { -1f, 1f })
        {
            Lamp(head, sx * (bodyW * 0.5f - 0.2f), 0.84f, front - 0.14f + 0.025f, 0.2f, 0.2f, 0.05f, HeadLamp);
            Lamp(m, sx * (bodyW * 0.5f - 0.2f), 0.68f, front - 0.14f + 0.025f, 0.2f, 0.07f, 0.05f, Amber);
            Lamp(m, sx * (bodyW * 0.5f - 0.14f), GWingTop + 0.03f, front - 0.45f, 0.1f, 0.06f, 0.12f, Amber);
        }

        // ---- the cabin: hollow above its floor, four doors, glass you see through ----
        Skirt(m, s, cg, GWs, GBack, GSill, GFloor, bodyW, look.Lower);
        Along(m, cg, GWs + wall, GBack - wall, GFloor, GFloor + 0.01f, inner * 2f, HeavyCabin.FloorColour);
        const float wsTopAt = GWs + 0.36f;
        Along(m, cg, GWs - 0.04f, GWs + 0.14f, GFloor, GBelt + 0.03f, bodyW, look.Paint);
        float half = bodyW * 0.5f - 0.1f;
        m.Pane(new[]
        {
            new Vector3(-half, GBelt + 0.03f, Z(GWs + 0.1f)), new Vector3(half, GBelt + 0.03f, Z(GWs + 0.1f)),
            new Vector3(half - 0.04f, GGlass, Z(wsTopAt)), new Vector3(-half + 0.04f, GGlass, Z(wsTopAt)),
        }, PaneTint);
        foreach (float sx in new[] { -1f, 1f })
            m.Tube(new Vector3(sx * (half + 0.04f), GBelt + 0.03f, Z(GWs + 0.1f)), new Vector3(sx * (half + 0.01f), GRoof - 0.06f, Z(wsTopAt)), 0.05f, look.Paint, 4);
        Along(m, cg, wsTopAt - 0.05f, GBack, GRoof - 0.09f, GRoof, bodyW - 0.06f, look.Paint);
        Along(m, cg, wsTopAt, GBack - wall, GRoof - 0.1f, GRoof - 0.09f, inner * 2f, HeavyCabin.Lining);
        // the fixed side: the B pillar, the rear quarter (over its wheel, with a window), the rail over the doors
        foreach (float sx in new[] { -1f, 1f })
        {
            float xw = sx * (bodyW * 0.5f - wall * 0.5f), x = sx * bodyW * 0.5f;
            Along(m, cg, GBPillar - 0.05f, GBPillar + 0.05f, GFloor, GRoof - 0.09f, wall, look.Paint, xw);
            Along(m, cg, wsTopAt, GBack, GGlass, GRoof - 0.09f, wall, look.Paint, xw);
            Along(m, cg, GRearDoorEnd, GBack, wheelTop + 0.05f, GBelt + 0.04f, wall, look.Paint, xw);
            Along(m, cg, GRearDoorEnd, GRearDoorEnd + 0.1f, GBelt + 0.04f, GGlass, wall, look.Paint, xw);
            Along(m, cg, GBack - 0.12f, GBack, GBelt + 0.04f, GGlass, wall, look.Paint, xw);
            SidePane(m, x, Z(GRearDoorEnd + 0.1f), Z(GBack - 0.12f), GBelt + 0.04f, GGlass);
        }
        var leaves = new List<HeavyDoorLeaf>();
        foreach (float sx in new[] { -1f, 1f })
        {
            Door(leaves, cg, sx, false, GWs + 0.12f, GBPillar - 0.05f, bodyW, wall, GFloor, GBelt, GGlass, wsTopAt, look);
            Door(leaves, cg, sx, true, GBPillar + 0.05f, GRearDoorEnd, bodyW, wall, GFloor, GBelt, GGlass, wsTopAt, look);
        }
        // the tailgate (a rear window in it) with the spare wheel and its cover on the outside
        Along(m, cg, GBack - wall, GBack, GFloor, 1.0f, bodyW, look.Paint);
        Along(m, cg, GBack - wall, GBack, 1.5f, GRoof - 0.09f, bodyW, look.Paint);
        foreach (float sx in new[] { -1f, 1f })
            Along(m, cg, GBack - wall, GBack, 1.0f, 1.5f, 0.2f, look.Paint, sx * (bodyW * 0.5f - 0.1f));
        FrontPane(m, Z(GBack) - 0.01f, 0.58f, 1.0f, 1.5f);
        SpareUpright(m, 0f, 1.0f, Z(GBack), Z(s.Length), wheelTop * 0.5f - 0.02f, look.Paint);
        // the step under the tailgate, with its plate, and the engine tunnel down the middle
        Along(m, cg, GBack - 0.02f, GBack + 0.14f, GSill - 0.04f, 0.56f, bodyW - 0.3f, look.Lower);
        Along(m, cg, GWs + 0.4f, GBack - 0.3f, GFloor, GFloor + 0.16f, 0.3f, HeavyCabin.Dash);

        // mirrors on arms off the doors
        var mirrors = new List<(string, Vector3, Vector2)>();
        foreach (float sx in new[] { -1f, 1f })
        {
            m.Tube(new Vector3(sx * (bodyW * 0.5f), GBelt + 0.2f, Z(GWs + 0.3f)), new Vector3(sx * (hw + 0.1f), GBelt + 0.3f, Z(GWs + 0.36f)), 0.025f, Trim, 4);
            mirrors.Add((sx > 0 ? "MirrorLeft" : "MirrorRight", new Vector3(sx * (hw + 0.12f), GBelt + 0.36f, Z(GWs + 0.4f)), new Vector2(0.2f, 0.26f)));
        }
        var cockpit = HeavyCabin.Build(m, GCab(s, cg), HeavyCabin.GaugesFor(spec.LimiterKmh, spec.Redline),
            HeavyDriveline.AirLow, HeavyDriveline.AirMax, mirrors);
        var hip = cockpit.Seat.Hip;
        // the rear seats: two buckets behind the front ones
        var back = new[] { hip with { Z = hip.Z - 0.86f }, hip with { X = -hip.X, Z = hip.Z - 0.86f } };
        foreach (var b in back) CarMeshBuilder.Bucket(m, b, cockpit.Seat.Recline, GFloor, HeavyCabin.DriverSeatColour);
        var seats = new List<SeatAnchor>
        {
            new(0, CarMeshBuilder.Turned(hip), cockpit.Seat.Recline, GFloor),
            new(0, CarMeshBuilder.Turned(hip with { X = -hip.X }), cockpit.Seat.Recline, GFloor),
        };
        seats.AddRange(back.Select(b => new SeatAnchor(0, CarMeshBuilder.Turned(b), cockpit.Seat.Recline, GFloor)));

        // tail lamps up the corners either side of the spare, reversing lamps under them
        foreach (float sx in new[] { -1f, 1f })
        {
            Lamp(tail, sx * (bodyW * 0.5f - 0.06f), 0.95f, rear + (s.Length - GBack) - 0.02f, 0.12f, 0.3f, 0.04f, TailLamp);
            Lamp(rev, sx * (bodyW * 0.5f - 0.06f), 0.7f, rear + (s.Length - GBack) - 0.02f, 0.12f, 0.1f, 0.04f, White);
        }
        Plate(m, 0f, 0.5f, Z(GBack + 0.14f), -1f);
        Plate(m, 0f, 0.5f, front, 1f);

        return new HeavyParts(m.Build(), head.Build(), tail.Build(), rev.Build(), Wheels(s, cg), leaves.ToArray())
        {
            Cockpit = cockpit,
            Seats = seats.ToArray(),
            CarDoors = true,
        };
    }

    // ---- what Truck needs to know of them ----

    /// <summary>
    /// The driver's eye of a Duro or a G-Class in the section's frame, <c>At</c> metres behind the front
    /// (the rig's origin is the centre of mass: z = −(cgAt − At)); X is the left-hand-drive seat's, node space.
    /// </summary>
    public static (float X, float Y, float At) Eye(HeavySpec spec)
    {
        var s = spec.Sections[0];
        var frame = spec.Class == HeavyClass.Transporter ? DuroCab(s, 0f) : GCab(s, 0f);
        var eye = Driver(frame);
        return (-eye.X, eye.Y, -eye.Z);
    }

    /// <summary>The door a driver gets in by: its middle, metres behind the front.</summary>
    public static float DoorAt(HeavyClass cls) => cls == HeavyClass.Transporter
        ? (DuroDoor0 + DuroDoor1) * 0.5f : (GWs + 0.12f + GBPillar - 0.05f) * 0.5f;
}
