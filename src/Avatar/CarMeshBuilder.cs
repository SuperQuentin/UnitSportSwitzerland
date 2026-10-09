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

        /// <summary>
        /// A roof in one arc (the liftback, #760): from the windscreen's foot up to a level peak at
        /// <see cref="PeakZ"/>, then down to the rear glass's foot, the glass and the roof panel
        /// following it (<see cref="RoofAt"/>). Off, a flat roof between two straight panes.
        /// </summary>
        public bool Arched { get; init; }
        public float PeakZ { get; init; }

        /// <summary>The roof's outer line at z: the arc on an arched roof, the flat <see cref="Roof"/> otherwise.</summary>
        public float RoofAt(float z)
        {
            if (!Arched) return Roof;
            if (z >= PeakZ)
            {
                float span = WsBase - PeakZ;
                return Hermite(Roof, 0f, Belt, -ScreenSlope, span, Mathf.Clamp((z - PeakZ) / span, 0f, 1f));
            }
            float back = PeakZ - RgBase;
            return Hermite(RgFoot, RearSlope, Roof, 0f, back, Mathf.Clamp((z - RgBase) / back, 0f, 1f));
        }

        /// <summary>Where the windscreen is at height y (on the roof's outer line).</summary>
        public float FrontAt(float y) => Arched
            ? Solve(PeakZ, WsBase, y, falling: true)
            : Mathf.Lerp(WsBase, WsTop, (y - Belt) / (Roof - Belt));

        /// <summary>Where the rear glass is at height y (on the roof's outer line).</summary>
        public float RearAt(float y) => Arched
            ? Solve(RgBase, PeakZ, y, falling: false)
            : Mathf.Lerp(RgBase, RgTop, (y - RgFoot) / (Roof - RgFoot));

        // the arc's z at height y between z0 and z1, where it only rises or only falls
        private float Solve(float z0, float z1, float y, bool falling)
        {
            for (int i = 0; i < 30; i++)
            {
                float mid = (z0 + z1) * 0.5f;
                if (RoofAt(mid) > y == falling) z0 = mid; else z1 = mid;
            }
            return (z0 + z1) * 0.5f;
        }

        private static float Hermite(float y0, float m0, float y1, float m1, float span, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * y0 + (t3 - 2 * t2 + t) * span * m0 + (-2 * t3 + 3 * t2) * y1 + (t3 - t2) * span * m1;
        }
    }

    /// <summary>An arched roof's slope (rise over run) at the windscreen's foot and at the rear glass's.</summary>
    private const float ScreenSlope = 0.5f, RearSlope = 0.45f;

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
            // one arc (Dims.Arched): the screen from far forward up to just ahead of the peak, the
            // rear glass from half a metre behind it all the way down to the tail
            BodyShape.Liftback => (0.55f, 0.01f, -0.37f, -0.95f),
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
            // the Prius's roof peaks over the B-pillar, a little behind the middle (its rear headroom)
            Arched = b.Shape == BodyShape.Liftback,
            PeakZ = -0.15f * hl,
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

    /// <summary>
    /// The car's parts. <paramref name="smooth"/>: the rounded body and cabin of the lit styles
    /// (CarMeshBuilder.Rounded.cs, #760) where the shape has one; null = as the style says. The hull
    /// is measured with it off, so collision never depends on a client's style.
    /// </summary>
    public static CarParts Build(CarBody body, float wheelbase, CarGauges? gauges = null, bool? smooth = null)
    {
        // a kart is a tube frame with plastic round it, not a shell with a cabin: its own builder (#715)
        if (body.Shape == BodyShape.Kart) return KartMeshBuilder.Build(body, wheelbase);
        bool round = (smooth ?? HumanMeshBuilder.SmoothFigures) && RoundedShape(body.Shape);
        var d = For(body, wheelbase);
        var paint = body.Paint;
        bool open = body.Shape == BodyShape.Roadster;
        float hl = d.Length * 0.5f, hw = d.Width * 0.5f;
        float axF = d.Wheelbase * 0.5f, axR = -axF;
        var lower = body.Lower ?? paint;    // two-tone: the panda's black lower half
        var s = new MeshScratch { Smooth = round };
        var head = new MeshScratch();
        var tail = new MeshScratch();
        float firewall = Firewall(d);

        // ---- lower body: sills between the axles, bumpers beyond them ----
        float sillCy = (SillY0 + SillY1) * 0.5f, sillH = SillY1 - SillY0;
        float arch = d.WheelR + 0.13f;
        // between the axles: solid behind the cabin (and ahead of the footwell, if it stops short
        // of the wheel arches), and under the cabin a floor pan between two sills, so there is
        // somewhere to put your feet; the footwell runs on between the front wheel wells
        // (a rounded body has its own: RoundedBody)
        float cabinBack = d.RgBase, sillF = axF - arch, sillR = axR + arch;
        if (!round)
        {
            foreach (var (z0, z1) in new[] { (sillR, cabinBack), (firewall, sillF) })
                if (z1 - z0 > 0.01f)
                    s.Box(new Vector3(0, sillCy, (z0 + z1) * 0.5f), new Vector3(d.Width, sillH, z1 - z0), lower);
            float cabinFront = Mathf.Min(firewall, sillF);
            float cabinLen = cabinFront - cabinBack;
            float cabinMid = (cabinFront + cabinBack) * 0.5f;
            s.Box(new Vector3(0, (SillY0 + FloorY) * 0.5f, cabinMid), new Vector3(d.Width, FloorY - SillY0, cabinLen), lower);
            foreach (float sx in new[] { -1f, 1f })
                s.Box(new Vector3(sx * (hw - (Skin + Lining) * 0.5f), sillCy, cabinMid), new Vector3(Skin + Lining, sillH, cabinLen), lower);
        }
        if (firewall - sillF > 0.01f)
        {
            float tub = Tub(d), noseLen = firewall - sillF, noseMid = (firewall + sillF) * 0.5f;
            s.Box(new Vector3(0, (SillY0 + FloorY) * 0.5f, noseMid), new Vector3(tub * 2f + 0.12f, FloorY - SillY0, noseLen), lower);
            foreach (float sx in new[] { -1f, 1f })
                s.Box(new Vector3(sx * (tub + 0.03f), sillCy, noseMid), new Vector3(0.06f, sillH, noseLen), lower);
        }
        if (!round)
        {
            float fbLen = hl - (axF + arch);
            s.Box(new Vector3(0, sillCy, axF + arch + fbLen * 0.5f), new Vector3(d.Width, sillH, fbLen), lower);
            s.Box(new Vector3(0, sillCy, axR - arch - fbLen * 0.5f), new Vector3(d.Width, sillH, fbLen), lower);
            // dark splitter and diffuser under the bumpers
            s.Box(new Vector3(0, SillY0 + 0.02f, hl - 0.25f), new Vector3(d.Width - 0.1f, 0.05f, 0.5f), Trim);
            s.Box(new Vector3(0, SillY0 + 0.02f, -hl + 0.25f), new Vector3(d.Width - 0.1f, 0.05f, 0.5f), Trim);
        }
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
        float noseZ = hl - (hl - d.WsBase) * 0.3f;   // the nose is a little lower and narrower
        bool sloped = SlopedNose(body.Shape);
        if (round)
            RoundedBody(s, body, d, doors);
        else
        {
            foreach (var (z0, z1) in new[] { (d.RgBase, open0), (open1, d.WsBase) })
                if (z1 - z0 > 0.01f)
                    foreach (float sx in new[] { -1f, 1f })
                        Wall(s, sx, hw, bot, d.Belt, z0, z1, paint);
            if (sloped)
                Nose(s, d, bot, body.Bonnet ?? paint);
            else
            {
                s.Box(new Vector3(0, (bot + d.Hood) * 0.5f, (d.WsBase + noseZ) * 0.5f), new Vector3(d.Width, d.Hood - bot, noseZ - d.WsBase), body.Bonnet ?? paint);
                s.Box(new Vector3(0, (bot + d.Hood - 0.06f) * 0.5f, (noseZ + hl) * 0.5f), new Vector3(d.Width - 0.1f, d.Hood - 0.06f - bot, hl - noseZ), paint);
            }
            s.Box(new Vector3(0, (bot + d.Deck) * 0.5f, (d.RgBase - hl) * 0.5f), new Vector3(d.Width, d.Deck - bot, d.RgBase + hl), paint);
        }

        // ---- greenhouse ----
        // A roadster's side glass and soft top come off it (their own scratches, their own
        // hinges); the windscreen and A-pillars stay with the body.
        var win = open ? new MeshScratch() : s;
        var top = open ? new MeshScratch() : s;
        float cw = d.Width - 0.2f;
        var glass = Tinted(body.Glass ?? Glass);
        const float roofSkin = 0.05f;
        if (d.Arched)
            ArchedGreenhouse(s, d, cw, paint, glass, round);
        else
        {
            SlopedPane(s, cw - 0.02f, d.Belt, d.WsBase, d.Roof - roofSkin, d.WsTop, glass);
            SlopedPane(top, cw - 0.02f, d.RgFoot, d.RgBase, d.Roof - roofSkin, d.RgTop, glass);
        }
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
        // a roadster's roof is its soft top, up: dark cloth rather than paint (an arched roof has its own)
        if (d.Arched) { }
        else if (round)
            RoundedRoof(top, d, cw, paint);
        else
            top.Box(new Vector3(0, d.Roof - roofSkin * 0.5f, (d.WsTop + d.RgTop) * 0.5f), new Vector3(cw + 0.02f, roofSkin, d.WsTop - d.RgTop), open ? Trim : paint);
        // the headlining under it, pale, so looking up from the seat is not into black
        if (!d.Arched)
            top.Box(new Vector3(0, d.Roof - roofSkin - 0.01f, (d.WsTop + d.RgTop) * 0.5f), new Vector3(cw - 0.04f, 0.02f, d.WsTop - d.RgTop - 0.04f),
                open ? Cabin : Liner);
        // pillars: A, B (four doors only) and C (cloth on a roadster)
        foreach (float sx in new[] { -1f, 1f })
        {
            float px = sx * (cw * 0.5f + 0.005f);
            // a rounded car's pillars as thick as a real one's
            if (!d.Arched)
            {
                s.Tube(new Vector3(px, d.Belt, d.WsBase), new Vector3(px, d.Roof, d.WsTop), round ? 0.045f : 0.03f, paint, 4);
                top.Tube(new Vector3(px, d.RgFoot, d.RgBase), new Vector3(px, d.Roof, d.RgTop), round ? 0.06f : 0.035f, open ? Trim : paint, 4);
            }
            // the B-pillar between a four-door's doors (a sedan's where it always stood)
            if (FourDoor(body.Shape))
            {
                float bz = body.Shape == BodyShape.Sedan ? (d.WsTop + d.RgTop) * 0.5f : doors[1].Z1;
                // up to the roof where it stands; black on the liftback, as on the Prius
                float bTop = d.RoofAt(bz) - (d.Arched ? 0.03f : 0f);
                s.RoundedBox(new Vector3(px, (d.Belt + bTop) * 0.5f, bz), new Vector3(round ? 0.05f : 0.03f, bTop - d.Belt, round ? 0.11f : 0.09f),
                    body.Shape == BodyShape.Liftback ? Trim : paint);
            }
        }

        // ---- the cabin: seat, dash, gauges, wheel, pedals, mirrors (CarCabin.cs) ----
        var cabin = BuildCabin(s, body, d, cw, gauges ?? CarGauges.Default, round);

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
            SweptLamps(s, head, d, body.Shape, round);
        else
            foreach (float sx in new[] { -1f, 1f })
                head.Box(new Vector3(sx * (hw - 0.36f), d.Hood - 0.06f, hl + 0.005f), new Vector3(0.4f, 0.13f, 0.02f), Head);
        if (body.Shape == BodyShape.Liftback)
        {
            // the Prius's big lower intake across the bumper, a fog lamp in each corner pod
            s.RoundedBox(new Vector3(0, SillY0 + 0.16f, hl - 0.01f), new Vector3(0.95f, 0.16f, 0.05f), Trim);
            foreach (float sx in new[] { -1f, 1f })
                s.RoundedBox(new Vector3(sx * (hw - 0.27f), SillY0 + 0.15f, hl - 0.005f), new Vector3(0.12f, 0.07f, 0.04f), new Color(0.85f, 0.86f, 0.82f));
        }
        else
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

        // tail lamps across the back, and exhausts; a rounded tail is narrower at its face
        float tailY = d.Deck - 0.08f;
        float tailHw = round ? PlanHalf(d, -hl) : hw, wrapHw = round ? PlanHalf(d, -hl + 0.08f) : hw;
        foreach (float sx in new[] { -1f, 1f })
        {
            switch (body.Shape)
            {
                case BodyShape.TallHatch when round:
                    RoundedTailLamp(tail, d, sx, d.Deck - 0.42f, d.Deck - 0.08f, 0.16f, 0.14f, true);
                    break;
                case BodyShape.Liftback when round:
                    RoundedTailLamp(tail, d, sx, d.Deck - 0.175f, d.Deck - 0.045f, 0.3f, 0.2f, false);
                    break;
                case BodyShape.TallHatch:
                    // the Yaris's tall lamps up the corners beside the hatch, round onto the sides
                    tail.Box(new Vector3(sx * (tailHw - 0.1f), d.Deck - 0.2f, -hl - 0.005f), new Vector3(0.18f, 0.4f, 0.02f), Tail);
                    tail.Box(new Vector3(sx * (wrapHw + 0.005f), d.Deck - 0.2f, -hl + 0.07f), new Vector3(0.02f, 0.4f, 0.14f), Tail);
                    break;
                case BodyShape.Liftback:
                    // the Prius's lamps under the spoiler, wrapping round the corners
                    tail.Box(new Vector3(sx * (tailHw - 0.2f), d.Deck - 0.11f, -hl - 0.005f), new Vector3(0.36f, 0.13f, 0.02f), Tail);
                    tail.Box(new Vector3(sx * (wrapHw + 0.005f), d.Deck - 0.11f, -hl + 0.09f), new Vector3(0.02f, 0.13f, 0.18f), Tail);
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
            s.RoundedBox(new Vector3(0, d.Deck + 0.02f, -hl + 0.03f), new Vector3(tailHw * 2f - 0.1f, 0.05f, 0.14f), paint);
            float gx = hw - 0.4f, gy0 = d.Deck - 0.2f, gy1 = d.Deck - 0.03f, gz = -hl - 0.004f;
            s.Pane(stackalloc Vector3[] { new(-gx, gy0, gz), new(gx, gy0, gz), new(gx, gy1, gz), new(-gx, gy1, gz) }, Tinted(TailGlass));
        }
        s.Box(new Vector3(0, SillY1 + 0.03f, -hl - 0.005f), new Vector3(0.5f, 0.05f, 0.01f), Steel);   // number plate blanks
        s.Box(new Vector3(0, SillY1 + 0.03f, hl + 0.005f), new Vector3(0.5f, 0.05f, 0.01f), Steel);

        // underglow: neon strips along both sills, in the unshaded lamp mesh so they glow
        if (body.Underglow is { } neon)
            foreach (float sx in new[] { -1f, 1f })
                head.Box(new Vector3(sx * (hw - 0.12f), SillY0 - 0.01f, 0), new Vector3(0.05f, 0.02f, d.Wheelbase - 2 * arch), neon);

        var doorParts = doors.Select(span => BuildDoor(span, body, d, bot, cw, glass, round)).SelectMany(x => x).ToArray();
        var parts = new CarParts(s.Build(), head.Build(), tail.Build(), BuildWheel(d, body.Rim, body.RimSize, body.Slicks, round),
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
    private static void SweptLamps(MeshScratch s, MeshScratch head, Dims d, BodyShape shape, bool round = false)
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
            if (round)
                RoundedLamp(head, d, sx, back, width);
            else
            {
                Plate(head, sx, st[2], st[1], width - 0.04f, Head);
                Plate(head, sx, st[1], tip, width, Head);
            }
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
        if (d.Arched) return ArchedOutline(d, z0, z1, inset);
        float yTop = d.Roof - 0.05f - inset, yBot = d.Belt + inset;
        // the rakes, as z at a height
        float Front(float y) => Mathf.Lerp(d.WsBase, d.WsTop, (y - d.Belt) / (d.Roof - d.Belt)) - inset;
        float Rear(float y) => Mathf.Lerp(d.RgBase, d.RgTop, (y - d.RgFoot) / (d.Roof - d.RgFoot)) + inset;
        var poly = new List<Vector2> { new(Rear(yBot), yBot), new(Front(yBot), yBot), new(Front(yTop), yTop), new(Rear(yTop), yTop) };
        poly = Clip(poly, z0 + inset, 1f);
        return Clip(poly, z1 - inset, -1f);
    }

    /// <summary>
    /// An arched roof's side window between z0 and z1 (#760): the belt along the bottom, the roof's
    /// arc along the top, square at the rear glass's foot, clipped to the span and inset.
    /// </summary>
    private static List<Vector2> ArchedOutline(Dims d, float z0, float z1, float inset)
    {
        float yBot = d.Belt + inset;
        float Top(float z) => d.RoofAt(z) - 0.05f - inset;
        // the front end: where the arc comes down to the belt, ahead of the peak
        float front = d.FrontAt(yBot + 0.05f + inset) - inset, back = d.RgBase + inset;
        var poly = new List<Vector2> { new(back, yBot), new(front, yBot) };
        const int n = 24;
        for (int i = 1; i < n; i++)
        {
            float z = Mathf.Lerp(front, back, i / (float)n);
            poly.Add(new Vector2(z, Mathf.Max(Top(z), yBot)));
        }
        poly.Add(new Vector2(back, Mathf.Max(Top(back), yBot)));
        poly = Clip(poly, z0 + inset, 1f);
        return Clip(poly, z1 - inset, -1f);
    }

    /// <summary>
    /// An arched roof's greenhouse (#760, the liftback): the windscreen and the rear glass as strips
    /// following the arc, the roof panel between them lofted along it (crowned and rolled at the
    /// edges on a rounded body) with the headlining under it, and each side one rail along the
    /// arc, A-pillar, roof rail and C-pillar in one, thicker at the back.
    /// </summary>
    private static void ArchedGreenhouse(MeshScratch s, Dims d, float cw, Color paint, Color glass, bool round)
    {
        const float skin = 0.05f;
        float Glass(float z) => d.RoofAt(z) - skin;
        float hx = (cw - 0.02f) * 0.5f;
        void Strips(float from, float to, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float za = Mathf.Lerp(from, to, i / (float)n), zb = Mathf.Lerp(from, to, (i + 1) / (float)n);
                s.Pane(stackalloc Vector3[] { new(-hx, Glass(za), za), new(hx, Glass(za), za), new(hx, Glass(zb), zb), new(-hx, Glass(zb), zb) }, glass);
            }
        }
        Strips(d.WsTop, d.WsBase, 10);
        Strips(d.RgBase, d.RgTop, 10);

        // the roof panel, a little past the glass at each end, and the headlining under it
        float rx = cw * 0.5f + 0.01f;
        var roof = new List<Vector3[]>();
        var liner = new List<Vector3[]>();
        const int stations = 9;
        for (int i = 0; i < stations; i++)
        {
            float z = Mathf.Lerp(d.RgTop - 0.03f, d.WsTop + 0.03f, i / (stations - 1f)), y1 = d.RoofAt(z), y0 = y1 - skin;
            roof.Add(round
                ? new[]
                {
                    new Vector3(-rx, y0, z), new Vector3(rx, y0, z), new Vector3(rx, y1 - 0.025f, z), new Vector3(rx - 0.06f, y1 - 0.004f, z),
                    new Vector3(0, y1, z), new Vector3(-rx + 0.06f, y1 - 0.004f, z), new Vector3(-rx, y1 - 0.025f, z),
                }
                : new[] { new Vector3(-rx, y0, z), new Vector3(rx, y0, z), new Vector3(rx, y1, z), new Vector3(-rx, y1, z) });
            float lx = cw * 0.5f - 0.02f, ly = y0 - 0.01f;
            liner.Add(new[] { new Vector3(-lx, ly - 0.02f, z), new Vector3(lx, ly - 0.02f, z), new Vector3(lx, ly, z), new Vector3(-lx, ly, z) });
        }
        s.Loft(roof, Enumerable.Repeat(paint, roof[0].Length).ToArray(), paint, averaged: true);
        s.Loft(liner, Enumerable.Repeat(Liner, 4).ToArray(), Liner, averaged: true);

        // the rails along the arc, from the screen's foot over the roof to the rear glass's
        const int railSteps = 18;
        foreach (float sx in new[] { -1f, 1f })
        {
            float x = sx * (cw * 0.5f + 0.005f);
            for (int i = 0; i < railSteps; i++)
            {
                float za = Mathf.Lerp(d.RgBase, d.WsBase, i / (float)railSteps), zb = Mathf.Lerp(d.RgBase, d.WsBase, (i + 1) / (float)railSteps);
                // its top flush with the roof's outer line, not standing proud of it
                float r = (za + zb) * 0.5f < d.RgTop ? (round ? 0.045f : 0.035f) : (round ? 0.035f : 0.03f);
                s.Tube(new Vector3(x, d.RoofAt(za) - r, za), new Vector3(x, d.RoofAt(zb) - r, zb), r, paint, 4);
            }
        }
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
    private static IEnumerable<CarDoor> BuildDoor((float Z0, float Z1, bool Rear) span, CarBody body, Dims d, float bot, float cw, Color glass, bool round)
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
            var m = new MeshScratch { Smooth = round };
            if (round)
            {
                // the rounded wall itself between the pillars, its inside the card; the handle on the
                // skin, the armrest on the card
                RoundedDoorPanel(m, body, d, span, sx, hinge);
                m.RoundedBox(new Vector3(sx * (hw - 0.012f), midY + 0.1f, span.Z1 - 0.25f) - hinge, new Vector3(0.025f, 0.03f, 0.14f), Trim);
                m.RoundedBox(new Vector3(sx * (hw - RoundSkin - 0.07f), midY + 0.02f, midZ - 0.05f) - hinge, new Vector3(0.08f, 0.05f, len * 0.45f), Trim);
            }
            else
            {
                m.Box(new Vector3(sx * (hw - Skin * 0.5f), midY, midZ) - hinge, new Vector3(Skin, d.Belt - bot, len), body.Paint);
                m.Box(new Vector3(sx * (hw + 0.005f), midY + 0.1f, span.Z1 - 0.25f) - hinge, new Vector3(0.02f, 0.03f, 0.14f), Trim);   // handle
                // the door card, and an armrest on it
                m.Box(new Vector3(sx * (hw - Skin - Lining * 0.5f), midY - 0.005f, midZ) - hinge, new Vector3(Lining, d.Belt - bot - 0.01f, len - 0.04f), Cabin);
                m.Box(new Vector3(sx * (hw - Skin - Lining - 0.035f), midY + 0.02f, midZ - 0.05f) - hinge, new Vector3(0.07f, 0.05f, len * 0.45f), Trim);
            }
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
    private static ArrayMesh BuildWheel(Dims d, Color rim, int rimSize, bool slicks, bool round = false)
    {
        // a rounded car's (#760): a round tyre, its shoulders rolled off
        var s = new MeshScratch { Smooth = round };
        float r = d.WheelR, w = d.TyreW * (slicks ? 1.35f : 1f);
        float wall = 0.12f - 0.03f * rimSize;
        int sides = round ? 28 : 14;
        if (round)
        {
            s.Ring(Vector3.Zero, Vector3.Right, r - wall, r - 0.02f, w, Rubber, sides);
            s.Ring(Vector3.Zero, Vector3.Right, r - 0.03f, r, w * 0.82f, Rubber, sides);
        }
        else
            s.Ring(Vector3.Zero, Vector3.Right, r - wall, r, w, Rubber, sides);
        s.Ring(Vector3.Zero, Vector3.Right, 0.03f, r - wall + 0.005f, w * 0.7f, new Color(0.2f, 0.2f, 0.22f), sides);
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
