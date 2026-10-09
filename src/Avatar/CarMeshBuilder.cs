using Godot;

namespace UnitSport.Avatar;

/// <summary>The meshes a <see cref="CarRig"/> is assembled from, and where its wheels go.</summary>
public sealed record CarParts(ArrayMesh Body, ArrayMesh Head, ArrayMesh Tail, ArrayMesh Wheel,
    float WheelRadius, float FrontAxleZ, float RearAxleZ, float HalfTrack,
    CarDoor[] Doors, float Drop, Color? Glow, CarCabin Cabin)
{
    /// <summary>A roadster's soft top (cloth, rear window, C-pillars), origin on its hinge behind the seats.</summary>
    public HingedPart? Top { get; init; }
    /// <summary>A roadster's side glass, origin on the belt line it winds down into.</summary>
    public HingedPart? Windows { get; init; }
    /// <summary>
    /// Both pop-up headlight pods, raised, with the lamps in their faces as <see cref="HingedPart.Lamp"/>;
    /// origin on the nose, where they fold down to lie as lids.
    /// </summary>
    public HingedPart? Flaps { get; init; }
    /// <summary>A kart's front tyres, smaller than its rear ones (#715); null = <see cref="Wheel"/> on all four.</summary>
    public ArrayMesh? FrontWheel { get; init; }
    public float? FrontWheelRadius { get; init; }
}

/// <summary>A part that moves: its mesh (and lamp mesh, if it carries lights), origin on <see cref="Pivot"/> in node space.</summary>
public sealed record HingedPart(ArrayMesh Mesh, Vector3 Pivot, ArrayMesh? Lamp = null);

/// <summary>
/// One door: its mesh, built around its hinge, where that hinge is and the rotation that opens
/// it. <paramref name="Hinge"/> and <paramref name="Centre"/> (the closed door's middle, for
/// "which door is this player at") are in node space, like the shell's vertices.
/// </summary>
public sealed record CarDoor(string Name, byte Bit, ArrayMesh Mesh, Vector3 Hinge, Quaternion Open, Vector3 Centre);

/// <summary>
/// Low-poly cars from <see cref="MeshScratch"/> boxes, tubes and panes, at real dimensions.
/// Authored facing +Z with the origin on the ground under the middle of the wheelbase;
/// <see cref="MeshScratch.Build()"/> turns everything to face −Z like any node.
///
/// <para>
/// The body is hollow where people sit: from the rear glass to the windscreen there is a floor,
/// sills, side walls and doors, and inside them the cabin (<see cref="CarCabin"/>, CarCabin.cs).
/// The glass is panes in the mesh's second surface, drawn translucent by the rig, so the driver
/// shows from outside and the road from inside. It used to be a staircase of solid glass boxes
/// under a sloped slab: fine while glass was opaque, a stack of tinted floors once it is not.
/// </para>
///
/// <para>
/// Parts that move are built apart, each around its hinge (<see cref="MeshScratch.Build(Vector3)"/>):
/// a roadster's soft top and side glass, and the pop-up pods, authored raised. The pods fold flat
/// onto the nose and the rig hides their lamps when they are put away (from when boxes rendered
/// inside out, the note <c>meshscratch-boxes-render-inside-out</c>; they need not be any more,
/// but it is no worse).
/// </para>
/// </summary>
public static partial class CarMeshBuilder
{
    private static readonly Color Glass = new(0.34f, 0.44f, 0.54f);
    private static readonly Color Rubber = new(0.07f, 0.07f, 0.08f);
    private static readonly Color Trim = new(0.06f, 0.06f, 0.07f);
    private static readonly Color Head = new(1f, 0.96f, 0.8f);
    private static readonly Color Tail = new(1f, 0.12f, 0.09f);
    private static readonly Color Steel = new(0.55f, 0.56f, 0.6f);
    private static readonly Color Cabin = new(0.14f, 0.13f, 0.13f);
    private static readonly Color Amber = new(1f, 0.6f, 0.12f);

    internal sealed record Dims(
        float Length, float Width, float Roof, float Wheelbase, float WheelR, float TyreW, float Track,
        float Belt, float Hood, float Deck,
        float WsBase, float WsTop, float RgTop, float RgBase)
    {
        /// <summary>
        /// The rear glass's foot: the belt, except on a liftback, whose glass starts at its high tail
        /// (<see cref="Deck"/>) and whose rear side glass ends square under it.
        /// </summary>
        public float RgFoot { get; init; } = Belt;
    }

    // Belt = body top through the doors, Hood/Deck = top of the bonnet / boot lid (on a sloped
    // nose, the bonnet's front edge); the four Z values are the base and top of the windscreen and
    // rear glass. Heights in metres. Derived from the body's real length and height by per-shape
    // proportions, measured off the three hand-built originals (AE86 hatch, FD, GC8) and extended
    // to the other shapes; the tall hatch and the liftback off side views of the XP90 Yaris and
    // the XW20 Prius (#760).
    internal static Dims For(CarBody b, float wheelbase)
    {
        float hl = b.Length * 0.5f, h = b.Height;
        // fractions of the half-length: windscreen base, windscreen top, rear glass top, rear glass base
        var (wsB, wsT, rgT, rgB) = b.Shape switch
        {
            BodyShape.Hatchback => (0.38f, 0.10f, -0.36f, -0.62f),
            BodyShape.Fastback => (0.26f, -0.07f, -0.40f, -0.60f),
            BodyShape.Sedan => (0.43f, 0.11f, -0.27f, -0.43f),
            BodyShape.Roadster => (0.28f, 0.14f, -0.18f, -0.23f),
            BodyShape.Midship => (0.22f, -0.10f, -0.36f, -0.46f),
            // the windscreen's foot out over the front wheels, a short roof, the hatch raked a third off upright
            BodyShape.TallHatch => (0.60f, 0.06f, -0.74f, -0.95f),
            // the screen raked as far forward, a short roof, the glass all the way down to the tail
            BodyShape.Liftback => (0.55f, 0.02f, -0.45f, -0.95f),
            _ => (0.42f, 0.12f, -0.20f, -0.39f),   // Coupe
        };
        // fractions of the height: belt, bonnet, boot lid
        var (belt, hood, deck) = b.Shape switch
        {
            BodyShape.Midship => (0.62f, 0.60f, 0.70f),
            BodyShape.Fastback => (0.62f, 0.60f, 0.63f),
            BodyShape.TallHatch => (0.62f, 0.50f, 0.62f),
            BodyShape.Liftback => (0.64f, 0.48f, 0.72f),
            _ => (0.62f, 0.60f, 0.66f),
        };
        return new Dims(b.Length, b.Width, h, wheelbase, b.WheelRadius,
            Mathf.Clamp(0.19f + (b.Width - 1.63f) * 0.33f, 0.18f, 0.27f), b.Width - 0.24f,
            h * belt, h * hood, h * deck, hl * wsB, hl * wsT, hl * rgT, hl * rgB)
        {
            RgFoot = h * (b.Shape == BodyShape.Liftback ? deck : belt),
        };
    }

    /// <summary>The noses that slope from the windscreen's foot down to the bumper, not a flat bonnet (#760).</summary>
    internal static bool SlopedNose(BodyShape shape) => shape is BodyShape.TallHatch or BodyShape.Liftback;

    /// <summary>A front and a rear door each side.</summary>
    internal static bool FourDoor(BodyShape shape) => shape is BodyShape.Sedan or BodyShape.Liftback;

    // the lower body: sills from here to there, the cabin floor pan on top of the bottom
    private const float SillY0 = 0.20f, SillY1 = 0.52f;
    /// <summary>Top of the cabin floor.</summary>
    internal const float FloorY = 0.23f;
    /// <summary>How thick the side walls (and doors) are: the paint skin, then the trim inside it.</summary>
    private const float Skin = 0.06f, Lining = 0.08f;

    /// <summary>
    /// Where the footwell ends: the bulkhead a wheel's radius behind the front axle. The feet go
    /// forward between the front wheel wells (<see cref="Tub"/>), as in any real car.
    /// </summary>
    internal static float Firewall(Dims d) => d.Wheelbase * 0.5f - d.WheelR - 0.02f;

    /// <summary>Half the footwell's width between the front wheel wells: inboard of the tyres, with a wall.</summary>
    internal static float Tub(Dims d) => d.Track * 0.5f - d.TyreW * 0.5f - 0.07f;

    public static CarParts Build(CarBody body, float wheelbase, CarGauges? gauges = null)
    {
        // a kart is a tube frame with plastic round it, not a shell with a cabin: its own builder (#715)
        if (body.Shape == BodyShape.Kart) return KartMeshBuilder.Build(body, wheelbase);
        var d = For(body, wheelbase);
        var paint = body.Paint;
        bool open = body.Shape == BodyShape.Roadster;
        float hl = d.Length * 0.5f, hw = d.Width * 0.5f;
        float axF = d.Wheelbase * 0.5f, axR = -axF;
        var lower = body.Lower ?? paint;    // two-tone: the panda's black lower half
        var s = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();
        float firewall = Firewall(d);

        // ---- lower body: sills between the axles, bumpers beyond them ----
        float sillCy = (SillY0 + SillY1) * 0.5f, sillH = SillY1 - SillY0;
        float arch = d.WheelR + 0.13f;
        // between the axles: solid behind the cabin (and ahead of the footwell, if it stops short
        // of the wheel arches), and under the cabin a floor pan between two sills, so there is
        // somewhere to put your feet; the footwell runs on between the front wheel wells
        float cabinBack = d.RgBase, sillF = axF - arch, sillR = axR + arch;
        foreach (var (z0, z1) in new[] { (sillR, cabinBack), (firewall, sillF) })
            if (z1 - z0 > 0.01f)
                s.Box(new Vector3(0, sillCy, (z0 + z1) * 0.5f), new Vector3(d.Width, sillH, z1 - z0), lower);
        float cabinFront = Mathf.Min(firewall, sillF);
        float cabinLen = cabinFront - cabinBack;
        float cabinMid = (cabinFront + cabinBack) * 0.5f;
        s.Box(new Vector3(0, (SillY0 + FloorY) * 0.5f, cabinMid), new Vector3(d.Width, FloorY - SillY0, cabinLen), lower);
        foreach (float sx in new[] { -1f, 1f })
            s.Box(new Vector3(sx * (hw - (Skin + Lining) * 0.5f), sillCy, cabinMid), new Vector3(Skin + Lining, sillH, cabinLen), lower);
        if (firewall - sillF > 0.01f)
        {
            float tub = Tub(d), noseLen = firewall - sillF, noseMid = (firewall + sillF) * 0.5f;
            s.Box(new Vector3(0, (SillY0 + FloorY) * 0.5f, noseMid), new Vector3(tub * 2f + 0.12f, FloorY - SillY0, noseLen), lower);
            foreach (float sx in new[] { -1f, 1f })
                s.Box(new Vector3(sx * (tub + 0.03f), sillCy, noseMid), new Vector3(0.06f, sillH, noseLen), lower);
        }
        float fbLen = hl - (axF + arch);
        s.Box(new Vector3(0, sillCy, axF + arch + fbLen * 0.5f), new Vector3(d.Width, sillH, fbLen), lower);
        s.Box(new Vector3(0, sillCy, axR - arch - fbLen * 0.5f), new Vector3(d.Width, sillH, fbLen), lower);
        // dark splitter and diffuser under the bumpers
        s.Box(new Vector3(0, SillY0 + 0.02f, hl - 0.25f), new Vector3(d.Width - 0.1f, 0.05f, 0.5f), Trim);
        s.Box(new Vector3(0, SillY0 + 0.02f, -hl + 0.25f), new Vector3(d.Width - 0.1f, 0.05f, 0.5f), Trim);
        // garage aero: a lip or a splitter with canards up front, fins in the diffuser, side skirts
        if (body.FrontAero == 1)
            s.Box(new Vector3(0, SillY0 - 0.015f, hl - 0.1f), new Vector3(d.Width - 0.12f, 0.03f, 0.3f), Trim);
        else if (body.FrontAero == 2)
        {
            s.Box(new Vector3(0, SillY0 - 0.03f, hl - 0.05f), new Vector3(d.Width + 0.06f, 0.03f, 0.45f), Trim);
            foreach (float sx in new[] { -1f, 1f })
                s.Box(new Vector3(sx * (hw + 0.02f), sillCy, hl - 0.2f), new Vector3(0.05f, 0.03f, 0.26f), Trim);
        }
        if (body.Diffuser)
            for (int i = -2; i <= 2; i++)
                s.Box(new Vector3(i * 0.28f, SillY0 + 0.04f, -hl + 0.12f), new Vector3(0.03f, 0.16f, 0.42f), Trim);
        if (body.Skirts)
            foreach (float sx in new[] { -1f, 1f })
                s.Box(new Vector3(sx * (hw + 0.015f), SillY0 - 0.02f, 0), new Vector3(0.05f, 0.1f, d.Wheelbase - 2 * arch), lower);

        // ---- upper body: boot, side walls, doors, bonnet ----
        float bot = SillY1;
        // From the rear glass to the windscreen the body is two walls, paint outside and trim
        // inside, with the doors in them; the cabin is the space between.
        var doors = DoorSpans(body.Shape, d);
        float open0 = doors.Min(x => x.Z0), open1 = doors.Max(x => x.Z1);
        foreach (var (z0, z1) in new[] { (d.RgBase, open0), (open1, d.WsBase) })
            if (z1 - z0 > 0.01f)
                foreach (float sx in new[] { -1f, 1f })
                    Wall(s, sx, hw, bot, d.Belt, z0, z1, paint);
        float noseZ = hl - (hl - d.WsBase) * 0.3f;   // the nose is a little lower and narrower
        bool sloped = SlopedNose(body.Shape);
        if (sloped)
            Nose(s, d, bot, body.Bonnet ?? paint);
        else
        {
            s.Box(new Vector3(0, (bot + d.Hood) * 0.5f, (d.WsBase + noseZ) * 0.5f), new Vector3(d.Width, d.Hood - bot, noseZ - d.WsBase), body.Bonnet ?? paint);
            s.Box(new Vector3(0, (bot + d.Hood - 0.06f) * 0.5f, (noseZ + hl) * 0.5f), new Vector3(d.Width - 0.1f, d.Hood - 0.06f - bot, hl - noseZ), paint);
        }
        s.Box(new Vector3(0, (bot + d.Deck) * 0.5f, (d.RgBase - hl) * 0.5f), new Vector3(d.Width, d.Deck - bot, d.RgBase + hl), paint);

        // ---- greenhouse ----
        // A roadster's side glass and soft top come off it (their own scratches, their own
        // hinges); the windscreen and A-pillars stay with the body.
        var win = open ? new MeshScratch() : s;
        var top = open ? new MeshScratch() : s;
        float cw = d.Width - 0.2f;
        var glass = Tinted(body.Glass ?? Glass);
        const float roofSkin = 0.05f;
        SlopedPane(s, cw - 0.02f, d.Belt, d.WsBase, d.Roof - roofSkin, d.WsTop, glass);
        SlopedPane(top, cw - 0.02f, d.RgFoot, d.RgBase, d.Roof - roofSkin, d.RgTop, glass);
        // the side glass not in a door: the quarter lights behind and ahead of the doors (a
        // roadster's doors carry none, its whole side glass winds down into the body)
        foreach (float sx in new[] { -1f, 1f })
            if (open)
                SidePane(win, d, sx * cw * 0.5f, d.RgBase, d.WsBase, 0f, glass);
            else
            {
                SidePane(win, d, sx * cw * 0.5f, d.RgBase, open0, 0f, glass);
                SidePane(win, d, sx * cw * 0.5f, open1, d.WsBase, 0f, glass);
            }
        // a roadster's roof is its soft top, up: dark cloth rather than paint
        top.Box(new Vector3(0, d.Roof - roofSkin * 0.5f, (d.WsTop + d.RgTop) * 0.5f), new Vector3(cw + 0.02f, roofSkin, d.WsTop - d.RgTop), open ? Trim : paint);
        // the headlining under it, pale, so looking up from the seat is not into black
        top.Box(new Vector3(0, d.Roof - roofSkin - 0.01f, (d.WsTop + d.RgTop) * 0.5f), new Vector3(cw - 0.04f, 0.02f, d.WsTop - d.RgTop - 0.04f),
            open ? Cabin : Liner);
        // pillars: A, B (four doors only) and C (cloth on a roadster)
        foreach (float sx in new[] { -1f, 1f })
        {
            float px = sx * (cw * 0.5f + 0.005f);
            s.Tube(new Vector3(px, d.Belt, d.WsBase), new Vector3(px, d.Roof, d.WsTop), 0.03f, paint, 4);
            top.Tube(new Vector3(px, d.RgFoot, d.RgBase), new Vector3(px, d.Roof, d.RgTop), 0.035f, open ? Trim : paint, 4);
            // the B-pillar between a four-door's doors (a sedan's where it always stood)
            if (FourDoor(body.Shape))
            {
                float bz = body.Shape == BodyShape.Sedan ? (d.WsTop + d.RgTop) * 0.5f : doors[1].Z1;
                s.Box(new Vector3(px, (d.Belt + d.Roof) * 0.5f, bz), new Vector3(0.03f, d.Roof - d.Belt, 0.09f), paint);
            }
        }

        // ---- the cabin: seat, dash, gauges, wheel, pedals, mirrors (CarCabin.cs) ----
        var cabin = BuildCabin(s, body, d, cw, gauges ?? CarGauges.Default);

        // ---- per-car features ----
        var lampY = d.Hood - 0.02f;
        MeshScratch? pods = null, podLamps = null;
        if (body.PopUps)
        {
            // pop-up headlights, raised: a pod on the bonnet edge with the lamp in its face.
            // Closed, the rig folds them down onto the nose they stand on, as lids.
            pods = new MeshScratch();
            podLamps = new MeshScratch();
            foreach (float sx in new[] { -1f, 1f })
            {
                float x = sx * (hw - 0.3f);
                pods.Box(new Vector3(x, d.Hood + 0.05f, noseZ + 0.1f), new Vector3(0.34f, 0.12f, 0.2f), body.Bonnet ?? paint);
                podLamps.Box(new Vector3(x, d.Hood + 0.05f, noseZ + 0.205f), new Vector3(0.28f, 0.09f, 0.02f), Head);
                s.Box(new Vector3(sx * (hw - 0.22f), lampY - 0.12f, hl + 0.005f), new Vector3(0.2f, 0.06f, 0.02f), Amber);   // indicators
            }
        }
        else if (sloped)
            SweptLamps(s, head, d, body.Shape);
        else
            foreach (float sx in new[] { -1f, 1f })
                head.Box(new Vector3(sx * (hw - 0.36f), d.Hood - 0.06f, hl + 0.005f), new Vector3(0.4f, 0.13f, 0.02f), Head);
        s.Box(new Vector3(0, SillY1 - 0.1f, hl - 0.03f), new Vector3(0.9f, 0.14f, 0.05f), Trim);   // grille intake

        float wingZ = -hl + 0.55f;
        switch (body.Wing)
        {
            case WingSize.Lip:
                s.Box(new Vector3(0, d.Deck + 0.03f, -hl + 0.12f), new Vector3(d.Width - 0.2f, 0.05f, 0.18f), paint);
                break;
            case WingSize.Small:
                foreach (float sx in new[] { -1f, 1f })
                    s.Box(new Vector3(sx * 0.5f, d.Deck + 0.08f, wingZ), new Vector3(0.05f, 0.16f, 0.12f), Trim);
                s.Box(new Vector3(0, d.Deck + 0.18f, wingZ), new Vector3(1.4f, 0.03f, 0.3f), Trim);
                break;
            case WingSize.Big:
            case WingSize.Gt:
                float lift = body.Wing == WingSize.Gt ? 0.45f : 0.33f;
                foreach (float sx in new[] { -1f, 1f })
                {
                    s.Box(new Vector3(sx * 0.72f, d.Deck + lift * 0.5f, wingZ), new Vector3(0.05f, lift - 0.03f, 0.34f), Trim);
                    s.Box(new Vector3(sx * 0.4f, d.Deck + lift * 0.15f, wingZ), new Vector3(0.05f, lift * 0.3f, 0.12f), Trim);
                }
                s.Box(new Vector3(0, d.Deck + lift, wingZ), new Vector3(Mathf.Min(1.6f, d.Width - 0.1f), 0.04f, 0.42f), Trim);
                break;
        }
        if (body.Scoop)
            s.Box(new Vector3(0, d.Hood + 0.05f, noseZ - 0.6f), new Vector3(0.5f, 0.1f, 0.55f), Trim);

        // tail lamps across the back, and exhausts
        float tailY = d.Deck - 0.08f;
        foreach (float sx in new[] { -1f, 1f })
        {
            switch (body.Shape)
            {
                case BodyShape.TallHatch:
                    // the Yaris's tall lamps up the corners beside the hatch, round onto the sides
                    tail.Box(new Vector3(sx * (hw - 0.1f), d.Deck - 0.2f, -hl - 0.005f), new Vector3(0.18f, 0.4f, 0.02f), Tail);
                    tail.Box(new Vector3(sx * (hw + 0.005f), d.Deck - 0.2f, -hl + 0.07f), new Vector3(0.02f, 0.4f, 0.14f), Tail);
                    break;
                case BodyShape.Liftback:
                    // the Prius's lamps under the spoiler, wrapping round the corners
                    tail.Box(new Vector3(sx * (hw - 0.2f), d.Deck - 0.11f, -hl - 0.005f), new Vector3(0.36f, 0.13f, 0.02f), Tail);
                    tail.Box(new Vector3(sx * (hw + 0.005f), d.Deck - 0.11f, -hl + 0.09f), new Vector3(0.02f, 0.13f, 0.18f), Tail);
                    break;
                default:
                    tail.Box(new Vector3(sx * (hw - 0.3f), tailY, -hl - 0.005f), new Vector3(0.4f, 0.12f, 0.02f), Tail);
                    break;
            }
            s.Tube(new Vector3(sx * 0.45f, SillY0 + 0.1f, -hl + 0.25f), new Vector3(sx * 0.45f, SillY0 + 0.1f, -hl - 0.05f), 0.04f, Steel, 6);
        }
        if (body.Shape == BodyShape.Liftback)
        {
            // the Kamm tail: a spoiler across the top of the tailgate, and under it the second,
            // upright strip of rear glass between the lamps
            s.Box(new Vector3(0, d.Deck + 0.02f, -hl + 0.03f), new Vector3(d.Width - 0.1f, 0.05f, 0.14f), paint);
            float gx = hw - 0.4f, gy0 = d.Deck - 0.2f, gy1 = d.Deck - 0.03f, gz = -hl - 0.004f;
            s.Pane(stackalloc Vector3[] { new(-gx, gy0, gz), new(gx, gy0, gz), new(gx, gy1, gz), new(-gx, gy1, gz) }, Tinted(TailGlass));
        }
        s.Box(new Vector3(0, SillY1 + 0.03f, -hl - 0.005f), new Vector3(0.5f, 0.05f, 0.01f), Steel);   // number plate blanks
        s.Box(new Vector3(0, SillY1 + 0.03f, hl + 0.005f), new Vector3(0.5f, 0.05f, 0.01f), Steel);

        // underglow: neon strips along both sills, in the unshaded lamp mesh so they glow
        if (body.Underglow is { } neon)
            foreach (float sx in new[] { -1f, 1f })
                head.Box(new Vector3(sx * (hw - 0.12f), SillY0 - 0.01f, 0), new Vector3(0.05f, 0.02f, d.Wheelbase - 2 * arch), neon);

        var doorParts = doors.Select(span => BuildDoor(span, body, d, bot, cw, glass)).SelectMany(x => x).ToArray();
        var parts = new CarParts(s.Build(), head.Build(), tail.Build(), BuildWheel(d, body.Rim, body.RimSize, body.Slicks),
            d.WheelR, axF, axR, d.Track * 0.5f, doorParts, body.Drop, body.Underglow, cabin);
        if (open)
        {
            var topHinge = new Vector3(0, d.Belt, d.RgBase);
            var belt = new Vector3(0, d.Belt, 0);
            parts = parts with
            {
                Top = new HingedPart(top.Build(topHinge), Turned(topHinge)),
                Windows = new HingedPart(win.Build(belt), Turned(belt)),
            };
        }
        if (pods != null)
        {
            // the nose top the pods stand on (it steps 6 cm down from the bonnet)
            var hinge = new Vector3(0, d.Hood - 0.06f, noseZ);
            parts = parts with { Flaps = new HingedPart(pods.Build(hinge), Turned(hinge), podLamps!.Build(hinge)) };
        }
        return parts;
    }

    private static readonly Color Liner = new(0.5f, 0.48f, 0.45f);
    /// <summary>The liftback's upright strip of rear glass, dark against the tailgate.</summary>
    private static readonly Color TailGlass = new(0.1f, 0.12f, 0.15f);

    /// <summary>
    /// A sloped nose (#760): one solid from the windscreen's foot, at the belt, down the bonnet to
    /// its front edge (<see cref="Dims.Hood"/>) and round onto the bumper, narrowing as it goes,
    /// its top corners chamfered.
    /// </summary>
    private static void Nose(MeshScratch s, Dims d, float bot, Color paint)
    {
        var stations = NoseStations(d);
        var rings = new Vector3[stations.Length][];
        for (int i = 0; i < stations.Length; i++)
        {
            var (z, top, w) = stations[i];
            float hx = w * 0.5f, ch = 0.08f;
            rings[i] = new[]
            {
                new Vector3(-hx, bot, z), new Vector3(hx, bot, z), new Vector3(hx, top - ch, z),
                new Vector3(hx - ch, top, z), new Vector3(-hx + ch, top, z), new Vector3(-hx, top - ch, z),
            };
        }
        s.Loft(rings, new[] { paint, paint, paint, paint, paint, paint }, paint);
    }

    /// <summary>The sloped nose's stations, back to front, as (z, top, width): the screen's foot, the bonnet's front edge, the bumper's face.</summary>
    private static (float Z, float Top, float Width)[] NoseStations(Dims d)
    {
        float hl = d.Length * 0.5f;
        return new[]
        {
            (d.WsBase, d.Belt - 0.01f, d.Width),
            (hl - 0.18f, d.Hood + 0.04f, d.Width - 0.04f),
            (hl, d.Hood - 0.06f, d.Width - 0.16f),
        };
    }

    /// <summary>
    /// Headlamps swept back from the nose's corners up the bonnet (#760): the Yaris's long
    /// teardrops, the Prius's shorter wedges. Each lies on the nose in two plates, the face and the
    /// bonnet, a centimetre proud of it; an amber indicator at the outer end, a slot grille between.
    /// </summary>
    private static void SweptLamps(MeshScratch s, MeshScratch head, Dims d, BodyShape shape)
    {
        var st = NoseStations(d);
        float hl = d.Length * 0.5f;
        bool yaris = shape == BodyShape.TallHatch;
        float back = yaris ? 0.62f : 0.5f, width = yaris ? 0.34f : 0.36f;
        // where the lamp's tail lies on the bonnet, between the front edge and the screen's foot
        float tz = hl - back;
        var tip = (Z: tz, Top: Mathf.Lerp(st[1].Top, st[0].Top, (st[1].Z - tz) / (st[1].Z - st[0].Z)),
            Width: Mathf.Lerp(st[1].Width, st[0].Width, (st[1].Z - tz) / (st[1].Z - st[0].Z)));
        foreach (float sx in new[] { -1f, 1f })
        {
            Plate(head, sx, st[2], st[1], width - 0.04f, Head);
            Plate(head, sx, st[1], tip, width, Head);
            s.Box(new Vector3(sx * (st[2].Width * 0.5f - 0.05f), st[2].Top - 0.07f, hl + 0.006f), new Vector3(0.07f, 0.05f, 0.02f), Amber);
        }
        // the slot grille between the lamps
        s.Box(new Vector3(0, st[2].Top - 0.06f, hl + 0.006f), new Vector3(yaris ? 0.42f : 0.5f, 0.05f, 0.02f), Trim);

        // a thin plate lying on the nose between two stations, its outer edge in from the chamfer
        static void Plate(MeshScratch m, float sx, (float Z, float Top, float Width) a, (float Z, float Top, float Width) b, float w, Color c)
        {
            var from = new Vector3(0, a.Top, a.Z);
            var to = new Vector3(0, b.Top, b.Z);
            float len = (to - from).Length();
            var dir = (to - from) / len;
            var normal = dir.Cross(Vector3.Right).Normalized();
            if (normal.Y < 0) normal = -normal;
            float x = sx * ((a.Width + b.Width) * 0.25f - 0.09f - w * 0.5f);
            var basis = new Basis(Vector3.Right, normal, Vector3.Right.Cross(normal));
            m.Box(new Vector3(x, 0, 0) + (from + to) * 0.5f + normal * 0.012f, new Vector3(w, 0.02f, len), c, basis);
        }
    }

    /// <summary>An authored (+Z facing) point in node space, as <see cref="MeshScratch.Build()"/> turns it.</summary>
    internal static Vector3 Turned(Vector3 p) => new(-p.X, p.Y, -p.Z);

    /// <summary>
    /// Glass as a pane colour: clear glass lets most of the cabin through, the garage's darker
    /// tints less and less (limo black is nearly opaque).
    /// </summary>
    private static Color Tinted(Color glass) =>
        glass with { A = Mathf.Clamp(0.95f - glass.Luminance * 1.4f, 0.3f, 0.92f) };

    /// <summary>A side wall between <paramref name="z0"/> and <paramref name="z1"/>: paint outside, trim inside.</summary>
    private static void Wall(MeshScratch s, float sx, float hw, float y0, float y1, float z0, float z1, Color paint)
    {
        float cz = (z0 + z1) * 0.5f, len = z1 - z0;
        s.Box(new Vector3(sx * (hw - Skin * 0.5f), (y0 + y1) * 0.5f, cz), new Vector3(Skin, y1 - y0, len), paint);
        s.Box(new Vector3(sx * (hw - Skin - Lining * 0.5f), (y0 + y1) * 0.5f - 0.005f, cz), new Vector3(Lining, y1 - y0 - 0.01f, len), Cabin);
    }

    /// <summary>
    /// The side window's outline between <paramref name="z0"/> and <paramref name="z1"/>, as (z, y):
    /// the belt, the windscreen's rake, the roof and the rear glass's rake, clipped to the span
    /// and brought <paramref name="inset"/> in from every edge (a door's frame).
    /// </summary>
    internal static List<Vector2> SideOutline(Dims d, float z0, float z1, float inset)
    {
        float yTop = d.Roof - 0.05f - inset, yBot = d.Belt + inset;
        // the rakes, as z at a height
        float Front(float y) => Mathf.Lerp(d.WsBase, d.WsTop, (y - d.Belt) / (d.Roof - d.Belt)) - inset;
        float Rear(float y) => Mathf.Lerp(d.RgBase, d.RgTop, (y - d.RgFoot) / (d.Roof - d.RgFoot)) + inset;
        var poly = new List<Vector2> { new(Rear(yBot), yBot), new(Front(yBot), yBot), new(Front(yTop), yTop), new(Rear(yTop), yTop) };
        poly = Clip(poly, z0 + inset, 1f);
        return Clip(poly, z1 - inset, -1f);
    }

    /// <summary>Sutherland–Hodgman against one edge: keeps what is on the <paramref name="sign"/> side of z = <paramref name="at"/>.</summary>
    private static List<Vector2> Clip(List<Vector2> poly, float at, float sign)
    {
        var result = new List<Vector2>();
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            bool ina = (a.X - at) * sign >= 0, inb = (b.X - at) * sign >= 0;
            if (ina) result.Add(a);
            if (ina != inb)
            {
                float t = (at - a.X) / (b.X - a.X);
                result.Add(a.Lerp(b, t));
            }
        }
        return result;
    }

    /// <summary>A side window at <paramref name="x"/> between two stations (nothing if they are the same).</summary>
    private static void SidePane(MeshScratch s, Dims d, float x, float z0, float z1, float inset, Color glass, Vector3 offset = default)
    {
        if (z1 - z0 < 0.03f) return;
        var outline = SideOutline(d, z0, z1, inset);
        if (outline.Count < 3) return;
        Span<Vector3> corners = stackalloc Vector3[outline.Count];
        for (int i = 0; i < outline.Count; i++) corners[i] = new Vector3(x, outline[i].Y, outline[i].X) - offset;
        s.Pane(corners, glass);
    }

    /// <summary>
    /// The doors along one side, front to back, as (Z0, Z1, Rear) spans between the pillars: one
    /// long door on a two-door, a front and a rear door on a four-door.
    /// </summary>
    private static (float Z0, float Z1, bool Rear)[] DoorSpans(BodyShape shape, Dims d)
    {
        float dz = (d.WsBase + d.RgBase) * 0.5f;
        switch (shape)
        {
            case BodyShape.Sedan:
                return new[] { (dz, Mathf.Min(dz + 0.95f, d.WsBase), false), (Mathf.Max(dz - 0.85f, d.RgBase), dz, true) };
            case BodyShape.Liftback:
            {
                // the B-pillar a quarter metre behind the screen's top; a small fixed glass ahead
                // of the front door and behind the rear one
                float split = d.WsTop - 0.25f;
                return new[] { (split, Mathf.Min(split + 1.1f, d.WsBase - 0.12f), false), (Mathf.Max(split - 0.9f, d.RgBase + 0.1f), split, true) };
            }
            case BodyShape.TallHatch:
            {
                // one long door from just behind the screen's foot, a quarter light ahead of it
                float z1 = d.WsBase - 0.2f;
                return new[] { (Mathf.Max(z1 - 1.1f, d.RgBase), z1, false) };
            }
            default:
                return new[] { (Mathf.Max(dz - 0.5f, d.RgBase), Mathf.Min(dz + 0.55f, d.WsBase), false) };
        }
    }

    /// <summary>
    /// A span's left and right doors: the panel from the sill to the belt, its card inside and
    /// its window, built around the hinge the style puts it on, plus the rotation that opens it
    /// (in node space, where left is −X and the front −Z).
    /// </summary>
    private static IEnumerable<CarDoor> BuildDoor((float Z0, float Z1, bool Rear) span, CarBody body, Dims d, float bot, float cw, Color glass)
    {
        // rear doors only ever swing out: there is no room for them to go up past the front ones
        var style = span.Rear && body.Doors is not (DoorStyle.Conventional or DoorStyle.Suicide) ? DoorStyle.Conventional : body.Doors;
        float hw = d.Width * 0.5f, midY = (bot + d.Belt) * 0.5f, midZ = (span.Z0 + span.Z1) * 0.5f;
        float len = span.Z1 - span.Z0 - 0.02f;
        foreach (float sx in new[] { 1f, -1f })   // authored +X is the node's −X: left first
        {
            bool left = sx > 0;
            // authored frame (+Z forward); MeshScratch.Build turns it to the node's
            var hinge = style switch
            {
                DoorStyle.Suicide => new Vector3(sx * hw, midY, span.Z0),
                DoorStyle.GullWing => new Vector3(sx * cw * 0.5f, d.Roof, midZ),
                _ => new Vector3(sx * hw, midY, span.Z1),
            };
            var m = new MeshScratch();
            m.Box(new Vector3(sx * (hw - Skin * 0.5f), midY, midZ) - hinge, new Vector3(Skin, d.Belt - bot, len), body.Paint);
            m.Box(new Vector3(sx * (hw + 0.005f), midY + 0.1f, span.Z1 - 0.25f) - hinge, new Vector3(0.02f, 0.03f, 0.14f), Trim);   // handle
            // the door card, and an armrest on it
            m.Box(new Vector3(sx * (hw - Skin - Lining * 0.5f), midY - 0.005f, midZ) - hinge, new Vector3(Lining, d.Belt - bot - 0.01f, len - 0.04f), Cabin);
            m.Box(new Vector3(sx * (hw - Skin - Lining - 0.035f), midY + 0.02f, midZ - 0.05f) - hinge, new Vector3(0.07f, 0.05f, len * 0.45f), Trim);
            if (body.Shape != BodyShape.Roadster)
                SidePane(m, d, sx * (cw * 0.5f + 0.012f), span.Z0, span.Z1, 0.03f, glass, hinge);

            float side = left ? -1f : 1f;   // the node-space X of the door's outside
            var open = style switch
            {
                // about the vertical hinge, the free edge outward
                DoorStyle.Conventional => new Basis(Vector3.Up, side * 1.15f),
                DoorStyle.Suicide => new Basis(Vector3.Up, -side * 1.15f),
                // about the lateral axis at the front hinge, tail up
                DoorStyle.Scissor => new Basis(Vector3.Right, -1.25f),
                DoorStyle.Butterfly => new Basis(Vector3.Up, side * 0.6f) * new Basis(Vector3.Right, -1.05f),
                // about the roof line, bottom edge up and out
                _ => new Basis(Vector3.Back, side * 1.9f),
            };
            string name = (span.Rear ? "DoorR" : "Door") + (left ? "L" : "R");
            // the front doors' bits follow the driver: theirs is CarRig.DriverDoor on either side
            bool driverSide = left == body.LeftHandDrive;
            byte bit = span.Rear ? (left ? CarRig.DoorRearLeft : CarRig.DoorRearRight) : driverSide ? CarRig.DriverDoor : CarRig.PassengerDoor;
            yield return new CarDoor(name, bit, m.Build(), Turned(hinge), open.GetRotationQuaternion(),
                Turned(new Vector3(sx * hw, midY, midZ)));
        }
    }

    /// <summary>A pane from (y0, z0) to (y1, z1) across <paramref name="width"/>: the raked windscreen or rear glass.</summary>
    private static void SlopedPane(MeshScratch s, float width, float y0, float z0, float y1, float z1, Color glass)
    {
        float hx = width * 0.5f;
        s.Pane(stackalloc Vector3[]
        {
            new(-hx, y0, z0), new(hx, y0, z0), new(hx, y1, z1), new(-hx, y1, z1),
        }, glass);
    }

    /// <summary>
    /// One wheel about the X axis, at the origin: tyre, dark disc, spokes in the rim colour. A
    /// bigger rim fills more of the same wheel (a thinner sidewall); slicks are wider.
    /// </summary>
    private static ArrayMesh BuildWheel(Dims d, Color rim, int rimSize, bool slicks)
    {
        var s = new MeshScratch();
        float r = d.WheelR, w = d.TyreW * (slicks ? 1.35f : 1f);
        float wall = 0.12f - 0.03f * rimSize;
        s.Ring(Vector3.Zero, Vector3.Right, r - wall, r, w, Rubber, 14);
        s.Ring(Vector3.Zero, Vector3.Right, 0.03f, r - wall + 0.005f, w * 0.7f, new Color(0.2f, 0.2f, 0.22f), 14);
        foreach (float sx in new[] { -1f, 1f })
            for (int i = 0; i < 5; i++)
            {
                float a = Mathf.Tau * i / 5f;
                var radial = new Vector3(0, Mathf.Cos(a), Mathf.Sin(a)) * (r - wall);
                s.Tube(new Vector3(sx * w * 0.36f, 0, 0), new Vector3(sx * w * 0.36f, 0, 0) + radial, 0.035f, 0.03f, rim, 4);
            }
        return s.Build();
    }
}
