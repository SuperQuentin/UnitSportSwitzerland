using Godot;

namespace UnitSport.Avatar;

/// <summary>The meshes a <see cref="CarRig"/> is assembled from, and where its wheels go.</summary>
public sealed record CarParts(ArrayMesh Body, ArrayMesh Head, ArrayMesh Tail, ArrayMesh Wheel,
    float WheelRadius, float FrontAxleZ, float RearAxleZ, float HalfTrack)
{
    /// <summary>A roadster's soft top (cloth, rear window, C-pillars), origin on its hinge behind the seats.</summary>
    public HingedPart? Top { get; init; }
    /// <summary>A roadster's side glass, origin on the belt line it winds down into.</summary>
    public HingedPart? Windows { get; init; }
    /// <summary>A roadster's cockpit (tub, dash, seats, wheel): only worth drawing with the top down.</summary>
    public ArrayMesh? Cockpit { get; init; }
    /// <summary>
    /// Both pop-up headlight pods, raised, with the lamps in their faces as <see cref="HingedPart.Lamp"/>;
    /// origin on the nose, where they fold down to lie as lids.
    /// </summary>
    public HingedPart? Flaps { get; init; }
}

/// <summary>A part that moves: its mesh (and lamp mesh, if it carries lights), origin on <see cref="Pivot"/> in node space.</summary>
public sealed record HingedPart(ArrayMesh Mesh, Vector3 Pivot, ArrayMesh? Lamp = null);

/// <summary>
/// Low-poly cars from <see cref="MeshScratch"/> boxes and tubes, at real dimensions. Authored
/// facing +Z with the origin on the ground under the middle of the wheelbase;
/// <see cref="MeshScratch.Build"/> turns everything to face −Z like any node.
///
/// <para>
/// The greenhouse is a short staircase of glass boxes under a sloped glass slab, because there is
/// no wedge primitive: the slab reads as a raked windscreen and the steps fill the gap beneath it
/// (their corners poke through the slab, in the same colour, so nothing shows).
/// </para>
///
/// <para>
/// Parts that move are built apart, each around its hinge (<see cref="MeshScratch.Build(Vector3)"/>):
/// a roadster's soft top and side glass, which fold away over a cockpit, and the pop-up pods,
/// authored raised. Nothing may be put away INSIDE the body: a box does not hide what is in it
/// (see the note <c>meshscratch-boxes-render-inside-out</c>), so the pods fold flat onto the nose
/// and the rig hides the lamps and the cockpit when they are put away.
/// </para>
/// </summary>
public static class CarMeshBuilder
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
        float WsBase, float WsTop, float RgTop, float RgBase);

    // Belt = body top through the doors, Hood/Deck = top of the bonnet / boot lid; the four Z
    // values are the base and top of the windscreen and rear glass. Heights in metres. Derived
    // from the body's real length and height by per-shape proportions, measured off the three
    // hand-built originals (AE86 hatch, FD, GC8) and extended to the other shapes.
    internal static Dims For(CarBody b, float wheelbase)
    {
        float hl = b.Length * 0.5f, h = b.Height;
        // fractions of the half-length: windscreen base, windscreen top, rear glass top, rear glass base
        var (wsB, wsT, rgT, rgB) = b.Shape switch
        {
            BodyShape.Hatchback => (0.38f, 0.10f, -0.36f, -0.62f),
            BodyShape.Fastback => (0.26f, -0.07f, -0.40f, -0.60f),
            BodyShape.Sedan => (0.43f, 0.11f, -0.27f, -0.43f),
            BodyShape.Roadster => (0.28f, 0.14f, -0.16f, -0.22f),
            BodyShape.Midship => (0.22f, -0.10f, -0.36f, -0.46f),
            _ => (0.42f, 0.12f, -0.20f, -0.39f),   // Coupe
        };
        float deck = b.Shape == BodyShape.Midship ? 0.70f : b.Shape == BodyShape.Fastback ? 0.63f : 0.66f;
        return new Dims(b.Length, b.Width, h, wheelbase, b.WheelRadius,
            Mathf.Clamp(0.19f + (b.Width - 1.63f) * 0.33f, 0.18f, 0.27f), b.Width - 0.24f,
            h * 0.62f, h * 0.60f, h * deck, hl * wsB, hl * wsT, hl * rgT, hl * rgB);
    }

    public static CarParts Build(CarBody body, float wheelbase)
    {
        var d = For(body, wheelbase);
        var paint = body.Paint;
        bool open = body.Shape == BodyShape.Roadster;
        float hl = d.Length * 0.5f, hw = d.Width * 0.5f;
        float axF = d.Wheelbase * 0.5f, axR = -axF;
        var lower = body.Lower ?? paint;    // two-tone: the panda's black lower half
        var s = new MeshScratch();
        var head = new MeshScratch();
        var tail = new MeshScratch();

        // ---- lower body: sills between the axles, bumpers beyond them ----
        const float sillY0 = 0.20f, sillY1 = 0.52f;
        float sillCy = (sillY0 + sillY1) * 0.5f, sillH = sillY1 - sillY0;
        float arch = d.WheelR + 0.13f;
        s.Box(new Vector3(0, sillCy, 0), new Vector3(d.Width, sillH, d.Wheelbase - 2 * arch), lower);
        float fbLen = hl - (axF + arch);
        s.Box(new Vector3(0, sillCy, axF + arch + fbLen * 0.5f), new Vector3(d.Width, sillH, fbLen), lower);
        s.Box(new Vector3(0, sillCy, axR - arch - fbLen * 0.5f), new Vector3(d.Width, sillH, fbLen), lower);
        // dark splitter and diffuser under the bumpers
        s.Box(new Vector3(0, sillY0 + 0.02f, hl - 0.25f), new Vector3(d.Width - 0.1f, 0.05f, 0.5f), Trim);
        s.Box(new Vector3(0, sillY0 + 0.02f, -hl + 0.25f), new Vector3(d.Width - 0.1f, 0.05f, 0.5f), Trim);

        // ---- upper body: boot, doors, bonnet ----
        float bot = sillY1;
        s.Box(new Vector3(0, (bot + d.Belt) * 0.5f, (d.RgBase + d.WsBase) * 0.5f), new Vector3(d.Width, d.Belt - bot, d.WsBase - d.RgBase), paint);
        float noseZ = hl - (hl - d.WsBase) * 0.3f;   // the nose is a little lower and narrower
        s.Box(new Vector3(0, (bot + d.Hood) * 0.5f, (d.WsBase + noseZ) * 0.5f), new Vector3(d.Width, d.Hood - bot, noseZ - d.WsBase), body.Bonnet ?? paint);
        s.Box(new Vector3(0, (bot + d.Hood - 0.06f) * 0.5f, (noseZ + hl) * 0.5f), new Vector3(d.Width - 0.1f, d.Hood - 0.06f - bot, hl - noseZ), paint);
        s.Box(new Vector3(0, (bot + d.Deck) * 0.5f, (d.RgBase - hl) * 0.5f), new Vector3(d.Width, d.Deck - bot, d.RgBase + hl), paint);

        // ---- greenhouse ----
        // A roadster's side glass and soft top come off it (their own scratches, their own
        // hinges); the windscreen and A-pillars stay with the body.
        var win = open ? new MeshScratch() : s;
        var top = open ? new MeshScratch() : s;
        float cw = d.Width - 0.2f;
        const int steps = 4;
        float h = (d.Roof - d.Belt) / steps;
        for (int k = 0; k < steps; k++)
        {
            float t = (k + 0.5f) / steps;   // fraction up the slope, at the middle of the layer
            float zf = Mathf.Lerp(d.WsBase, d.WsTop, t), zr = Mathf.Lerp(d.RgBase, d.RgTop, t);
            win.Box(new Vector3(0, d.Belt + h * (k + 0.5f), (zf + zr) * 0.5f), new Vector3(cw, h, zf - zr), Glass);
        }
        SlopedGlass(s, cw - 0.04f, d.Belt, d.WsBase, d.Roof, d.WsTop);
        SlopedGlass(top, cw - 0.04f, d.Belt, d.RgBase, d.Roof, d.RgTop);
        // a roadster's roof is its soft top, up: dark cloth rather than paint
        top.Box(new Vector3(0, d.Roof - 0.025f, (d.WsTop + d.RgTop) * 0.5f), new Vector3(cw + 0.02f, 0.05f, d.WsTop - d.RgTop), open ? Trim : paint);
        // pillars: A, B (four doors only) and C (cloth on a roadster)
        foreach (float sx in new[] { -1f, 1f })
        {
            float px = sx * (cw * 0.5f + 0.005f);
            s.Tube(new Vector3(px, d.Belt, d.WsBase), new Vector3(px, d.Roof, d.WsTop), 0.03f, paint, 4);
            top.Tube(new Vector3(px, d.Belt, d.RgBase), new Vector3(px, d.Roof, d.RgTop), 0.035f, open ? Trim : paint, 4);
            if (body.Shape == BodyShape.Sedan)
                s.Box(new Vector3(px, (d.Belt + d.Roof) * 0.5f, (d.WsTop + d.RgTop) * 0.5f), new Vector3(0.03f, d.Roof - d.Belt, 0.09f), paint);
            // door shut lines and mirrors
            float dz = (d.WsBase + d.RgBase) * 0.5f;
            s.Box(new Vector3(sx * (hw + 0.002f), (bot + d.Belt) * 0.5f, dz + 0.55f), new Vector3(0.01f, d.Belt - bot - 0.05f, 0.02f), Trim);
            s.Box(new Vector3(sx * (hw + 0.002f), (bot + d.Belt) * 0.5f, dz - 0.5f), new Vector3(0.01f, d.Belt - bot - 0.05f, 0.02f), Trim);
            s.Box(new Vector3(sx * (hw + 0.08f), d.Belt + 0.1f, d.WsBase - 0.1f), new Vector3(0.16f, 0.1f, 0.09f), body.Lower != null ? Trim : paint);
        }
        var cockpit = open ? new MeshScratch() : null;
        if (cockpit != null) Cockpit(cockpit, d, cw);

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
        else
            foreach (float sx in new[] { -1f, 1f })
                head.Box(new Vector3(sx * (hw - 0.36f), d.Hood - 0.06f, hl + 0.005f), new Vector3(0.4f, 0.13f, 0.02f), Head);
        s.Box(new Vector3(0, sillY1 - 0.1f, hl - 0.03f), new Vector3(0.9f, 0.14f, 0.05f), Trim);   // grille intake

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
            tail.Box(new Vector3(sx * (hw - 0.3f), tailY, -hl - 0.005f), new Vector3(0.4f, 0.12f, 0.02f), Tail);
            s.Tube(new Vector3(sx * 0.45f, sillY0 + 0.1f, -hl + 0.25f), new Vector3(sx * 0.45f, sillY0 + 0.1f, -hl - 0.05f), 0.04f, Steel, 6);
        }
        s.Box(new Vector3(0, sillY1 + 0.03f, -hl - 0.005f), new Vector3(0.5f, 0.05f, 0.01f), Steel);   // number plate blanks
        s.Box(new Vector3(0, sillY1 + 0.03f, hl + 0.005f), new Vector3(0.5f, 0.05f, 0.01f), Steel);

        var parts = new CarParts(s.Build(), head.Build(), tail.Build(), BuildWheel(d, body.Rim),
            d.WheelR, axF, axR, d.Track * 0.5f);
        if (open)
        {
            var topHinge = new Vector3(0, d.Belt, d.RgBase);
            var belt = new Vector3(0, d.Belt, 0);
            parts = parts with
            {
                Top = new HingedPart(top.Build(topHinge), Turned(topHinge)),
                Windows = new HingedPart(win.Build(belt), Turned(belt)),
                Cockpit = cockpit!.Build(),
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

    /// <summary>An authored (+Z facing) point in node space, as <see cref="MeshScratch.Build()"/> turns it.</summary>
    private static Vector3 Turned(Vector3 p) => new(-p.X, p.Y, -p.Z);

    /// <summary>
    /// What an open car shows with its top down: a dark tub over the body at the belt, a dash,
    /// two seats and the wheel on the right (the Initial D cars are Japanese). All of it fits
    /// inside the closed greenhouse, where the rig hides it.
    /// </summary>
    private static void Cockpit(MeshScratch s, Dims d, float cw)
    {
        float y = d.Belt;
        s.Box(new Vector3(0, y + 0.005f, (d.WsBase + d.RgBase) * 0.5f), new Vector3(cw - 0.04f, 0.01f, d.WsBase - d.RgBase - 0.04f), Cabin);
        s.Box(new Vector3(0, y + 0.04f, d.WsBase - 0.16f), new Vector3(cw - 0.06f, 0.07f, 0.24f), Cabin);
        foreach (float sx in new[] { -1f, 1f })
        {
            float x = sx * 0.33f;
            // seat back, reclined a little, and its headrest
            s.Box(new Vector3(x, y + 0.1f, d.RgBase + 0.26f), new Vector3(0.44f, 0.36f, 0.1f), Trim, new Basis(Vector3.Right, -0.2f));
            s.Box(new Vector3(x, y + 0.33f, d.RgBase + 0.22f), new Vector3(0.24f, 0.12f, 0.08f), Trim);
        }
        // −X authored is the right-hand side once turned to face −Z
        s.Ring(new Vector3(-0.33f, y + 0.12f, d.WsBase - 0.3f), new Vector3(0, 0.35f, -1f).Normalized(), 0.15f, 0.18f, 0.03f, Trim, 12);
    }

    /// <summary>A thin glass slab from (y0, z0) to (y1, z1): the raked windscreen or hatch.</summary>
    private static void SlopedGlass(MeshScratch s, float width, float y0, float z0, float y1, float z1)
    {
        float dy = y1 - y0, dz = z1 - z0;
        float len = Mathf.Sqrt(dy * dy + dz * dz);
        // a box's flat face lies in the XZ plane; tilting about X by atan(dy/dz) lays it along the slope
        float tilt = Mathf.Atan2(-dy, Mathf.Abs(dz)) * Mathf.Sign(dz);
        s.Box(new Vector3(0, (y0 + y1) * 0.5f + 0.01f, (z0 + z1) * 0.5f), new Vector3(width, 0.03f, len), Glass,
            new Basis(Vector3.Right, tilt));
    }

    /// <summary>One wheel about the X axis, at the origin: tyre, dark disc, spokes in the rim colour.</summary>
    private static ArrayMesh BuildWheel(Dims d, Color rim)
    {
        var s = new MeshScratch();
        float r = d.WheelR, w = d.TyreW;
        s.Ring(Vector3.Zero, Vector3.Right, r - 0.12f, r, w, Rubber, 14);
        s.Ring(Vector3.Zero, Vector3.Right, 0.03f, r - 0.115f, w * 0.7f, new Color(0.2f, 0.2f, 0.22f), 14);
        foreach (float sx in new[] { -1f, 1f })
            for (int i = 0; i < 5; i++)
            {
                float a = Mathf.Tau * i / 5f;
                var radial = new Vector3(0, Mathf.Cos(a), Mathf.Sin(a)) * (r - 0.12f);
                s.Tube(new Vector3(sx * w * 0.36f, 0, 0), new Vector3(sx * w * 0.36f, 0, 0) + radial, 0.035f, 0.03f, rim, 4);
            }
        return s.Build();
    }
}
