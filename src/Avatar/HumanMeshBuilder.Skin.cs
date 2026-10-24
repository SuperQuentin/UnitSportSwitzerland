using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Who a skinned figure is, as opposed to how it stands (#1202): everything its mesh is built
/// from. Two figures with the same look share one mesh (<see cref="HumanMeshBuilder.Skin"/>); a
/// pose only moves the bones. The palette's wind is not part of it (it moves the bones and the
/// shader), and <see cref="Point"/> is the pointing hand's folded fingers (#889).
/// </summary>
/// <remarks>
/// <see cref="Pedalling"/>: a cyclist's legs (<see cref="Cyclist"/>), as they were drawn apart from
/// the rider: dressed ones as the figure's, bare ones as plain tubes and a shoe on the pedal.
/// </remarks>
public readonly record struct FigureLook(HumanPalette Palette, bool Legs = true, bool Helmet = false,
    Headwear Hat = Headwear.None, bool Body = true, bool Head = true, bool FullFace = false, bool Point = false,
    bool Pedalling = false)
{
    /// <summary>Whether it is built for a lit style (<see cref="HumanMeshBuilder.SmoothFigures"/>): read when the look is made.</summary>
    public bool Smooth { get; init; } = HumanMeshBuilder.SmoothFigures;
}

/// <summary>A look's skinned mesh, built once at rest (<see cref="HumanMeshBuilder.Skin"/>), and what its bones are measured from.</summary>
public sealed class FigureSkin
{
    public required FigureLook Look { get; init; }
    public required ArrayMesh Mesh { get; init; }
    /// <summary>Each bone's frame at rest, inverted (author space): a pose's frame times this moves a vertex.</summary>
    internal Transform3D[] BindInverse { get; init; } = Array.Empty<Transform3D>();
    /// <summary>What its bones are measured with besides the rig: its clothes, its build, its skirt at rest.</summary>
    internal HumanMeshBuilder.SkinBody Body { get; init; } = null!;
    /// <summary>How many bones it is posed by: the body's, and a skirt's when it has one.</summary>
    public int BoneCount => BindInverse.Length;
    /// <summary>Whether its hem flutters in the wind (a skirt, robe or dress): the shader's <c>skirt_flutter</c>.</summary>
    public bool Flutters { get; init; }
}

/// <summary>
/// Skinned figures (#1202, docs/notes/avatar/skinned-figures.md): a figure is built once per look,
/// standing at rest, every vertex on one or two bones; a pose sets the bones and the GPU moves it.
/// <para>
/// The bones are the frames the builder already draws each part in, taken from the rig: the
/// trunk's rings every quarter of the spine (<see cref="Torso.Frame"/>), each limb's upper bone, its
/// mitred joint ring (stretched across the bend, as <see cref="LimbLoft"/> draws it) and its lower
/// bone, the hands (<see cref="HandFrameOf"/>), the feet (<see cref="Boot"/>'s), the head
/// (<see cref="Head"/>'s) and a skirt's. A part rigid in its frame is posed exactly as a rebuilt
/// mesh would draw it; a vertex between two trunk rings, the neck and a skirt blend two bones.
/// </para>
/// </summary>
public static partial class HumanMeshBuilder
{
    // ---- the bones, in this order
    private const int TrunkStations = 17;   // spine 0, 0.25 … 4
    private const int BoneTrunk = 1;
    private const int BoneArmL = BoneTrunk + TrunkStations, BoneArmR = BoneArmL + 4;   // upper, elbow, lower, hand
    private const int BoneLegL = BoneArmR + 4, BoneLegR = BoneLegL + 4;                // upper, knee, lower, foot
    private const int BoneHead = BoneLegR + 4, BoneNeck = BoneHead + 1, BoneSkirt = BoneNeck + 1;   // a skirt's rings: upper, middle, hem

    /// <summary>
    /// How many bones a skinned figure has (<see cref="FigureSkin.BoneCount"/>): the body's, and a
    /// skirt's after them; bone 0 is its origin, which never moves.
    /// </summary>
    public const int BodyBones = BoneSkirt, MaxBones = BoneSkirt + 2 * SkirtBoneCount;

    /// <summary>A skirt's bones, and as many again for a tier under it (<see cref="SkirtBones"/>).</summary>
    private const int SkirtBoneCount = SkirtLevels * SkirtSectors;

    // ---- the parts a vertex is drawn for (MeshScratch.RestMap.Part)
    private const int PartTrunk = 1, PartArmL = 2, PartArmR = 3, PartLegL = 4, PartLegR = 5, PartFootL = 6, PartFootR = 7,
        PartHead = 8, PartNeck = 9, PartSkirt = 10, PartTier = 11;

    /// <summary>The trunk station (0..16) a skirt hangs from: its waistband is at spine 2.</summary>
    private const int SkirtStation = 8;

    /// <summary>
    /// A skirt's bones (<see cref="SkirtBones"/>): one for each point of the drape's rings below the
    /// waistband (<see cref="DrapeAt"/>, <see cref="DrapeSides"/> round each), so it hangs as drawn.
    /// </summary>
    private const int SkirtLevels = 10, SkirtSectors = DrapeSides;

    /// <summary>
    /// What a look's bones are measured with besides the rig (#1202): its clothes and build, and a
    /// skirt's waistband and ring extents at rest.
    /// </summary>
    internal sealed class SkinBody
    {
        public Garment? Top, Bottom;
        public Physique Shape;
        /// <summary>The skirt, robe or dress it wears (<see cref="SkirtCutOf"/>), or null.</summary>
        public SkirtCut? Cut;
        /// <summary>The waistband's middle at rest, author space.</summary>
        public Vector3 SkirtTop;
        /// <summary>At rest, how far out each skirt bone hangs from the skirt's axis, and each tier bone from the tier's (<see cref="SkirtExtents"/>).</summary>
        public readonly float[] SkirtRest = new float[SkirtBoneCount], TierRest = new float[SkirtBoneCount];
        /// <summary>The skirt's axis at rest (down to its hem), its length, and its side and front square to it; the tier's axis and length.</summary>
        public Vector3 SkirtAxis, SkirtSide, SkirtFwd, TierAxis;
        public float SkirtLength, TierLength;

        /// <summary>The bones it is posed by: the body's, a skirt's, a tier's.</summary>
        public int Bones => Cut is not { } c ? BodyBones : c.Tiered ? BoneSkirt + 2 * SkirtBoneCount : BoneSkirt + SkirtBoneCount;

        public static SkinBody Of(HumanPalette palette)
        {
            var top = palette.Outfit[WearSlot.Top];
            return new SkinBody
            {
                Top = top,
                Bottom = top is { CoversBottom: true } ? null : palette.Outfit[WearSlot.Bottom],
                Shape = Physique.Of(palette.Appearance.Build),
            };
        }
    }

    private static readonly Dictionary<FigureLook, FigureSkin> Skins = new();

    /// <summary>
    /// The skinned mesh of <paramref name="look"/>, built at rest the first time it is asked for and
    /// shared after: many figures of one look (a crowd, a race's riders) draw one mesh.
    /// </summary>
    public static FigureSkin Skin(in FigureLook look)
    {
        var key = look with { Palette = look.Palette.Wind == Vector3.Zero ? look.Palette : look.Palette with { Wind = Vector3.Zero } };
        if (Skins.TryGetValue(key, out var skin)) return skin;
        // a full cache is emptied, not evicted by age: a figure keeps the skin it was given
        if (Skins.Count >= 256) Skins.Clear();
        return Skins[key] = BuildSkin(key);
    }

    /// <summary>The rig a look is built at: standing at rest, the pointing hand (#889) already pointing along the forearm.</summary>
    private static Rig BindRig(in FigureLook look)
    {
        var r = RestRig;
        if (look.Point) r = r with { PointDirR = (r.WristR - r.ElbowR).Normalized() };
        // a pedalling foot points straight ahead along its pedal (PoseCyclist)
        if (look.Pedalling) r = r with { ToeL = r.AnkleL + PedalToe, ToeR = r.AnkleR + PedalToe };
        return r;
    }

    /// <summary>A pedalling foot's toe from its ankle, and a dressed one's ankle from the pedal (as the cyclist's legs always were).</summary>
    private static readonly Vector3 PedalToe = new(0, 0, 0.09f), PedalAnkle = new(0, 0.03f, -0.03f);

    private static FigureSkin BuildSkin(in FigureLook look)
    {
        var rig = BindRig(look);
        var palette = look.Palette;
        var body = SkinBody.Of(palette);
        var fit = new Fit(rig, palette.Appearance.Build);
        body.SkirtTop = SkirtTopOf(fit);
        if ((body.Cut = SkirtCutOf(rig, fit.Torso, body.Top, body.Bottom)) is { } cut)
        {
            var (axis, len, side, fwd) = SkirtAxes(fit.Torso, body.SkirtTop, cut.Hem);
            (body.SkirtAxis, body.SkirtLength, body.SkirtSide, body.SkirtFwd) = (axis, len, side, fwd);
            SkirtExtents(rig, fit.Torso, body.Shape, body.SkirtTop, axis, len, side, fwd, cut.Radius, body.SkirtRest);
            if (cut.Tiered)
            {
                var (tAxis, tLen, tSide, tFwd) = SkirtAxes(fit.Torso, body.SkirtTop, cut.TierHem);
                (body.TierAxis, body.TierLength) = (tAxis, tLen);
                SkirtExtents(rig, fit.Torso, body.Shape, body.SkirtTop, tAxis, tLen, tSide, tFwd, cut.TierRadius, body.TierRest);
            }
        }
        var weigher = new Weigher(rig, body);
        var scratch = new MeshScratch { Weigher = weigher };
        _smoothOverride = look.Smooth;
        try
        {
            AppendRig(scratch, palette, rig, look.Legs && !look.Pedalling, look.Helmet, look.Hat, look.Body, look.Head, look.FullFace);
            if (look.Pedalling)
            {
                PedallingLeg(scratch, palette, rig.HipL, rig.KneeL, rig.AnkleL, rig.ToeL);
                PedallingLeg(scratch, palette, rig.HipR, rig.KneeR, rig.AnkleR, rig.ToeR);
            }
        }
        finally { _smoothOverride = null; }
        var frames = new Transform3D[body.Bones];
        Frames(rig, body, Vector3.Zero, frames);
        var inverse = new Transform3D[frames.Length];
        for (int i = 0; i < frames.Length; i++) inverse[i] = frames[i].AffineInverse();
        return new FigureSkin
        {
            Look = look, Mesh = scratch.Build(), BindInverse = inverse, Body = body,
            Flutters = Flutters(palette.Outfit),
        };
    }

    // a skin built for a style other than the one in use (a cached look made under the other one)
    [ThreadStatic] private static bool? _smoothOverride;

    // ---- posing a skinned figure: the rig as the builders compute it, then its bones

    [ThreadStatic] private static Transform3D[]? _frames;

    /// <summary>Sets <paramref name="f"/>'s bones to <paramref name="r"/>, its skirt blown by <paramref name="wind"/> (author space, m/s).</summary>
    private static void PoseRig(SkinnedFigure f, in Rig r, Vector3 wind)
    {
        if (f.Skin is not { } skin) return;
        var frames = (_frames ??= new Transform3D[MaxBones]).AsSpan(0, skin.BoneCount);
        Frames(r, skin.Body, wind, frames);
        f.Pose(frames);
        if (!skin.Flutters) return;
        f.SetFlutter(SkirtFlutter(wind));
        f.SetFloor(SkirtFloor(r));
    }

    /// <summary><see cref="BuildStride"/>'s figure as bones: the gait, a dance, an item's arms, VR hands, a pointing finger.</summary>
    public static void PoseStride(SkinnedFigure f, float speed, float phase, ItemArmPose arm = ItemArmPose.None, float armBlend = 0f,
        DanceParams? dance = null, VrArms? vr = null, Pointing? point = null, Vector3 wind = default)
    {
        // a skirt with no measured wind still feels the stride's own (#251)
        if (wind == Vector3.Zero && speed > 0.05f && f.Skin is { Flutters: true }) wind = new Vector3(0, 0, -speed);
        PoseRig(f, ApplyPoint(ApplyVr(ApplyArms(GaitWithDance(speed, phase, dance), arm, armBlend), vr), point), wind);
    }

    /// <summary>
    /// A cyclist (<see cref="Cyclist"/>) on <paramref name="frame"/>: part way (<paramref name="t"/>,
    /// eased) from riding position <paramref name="from"/> to <paramref name="to"/>, the legs solved
    /// from the pedals at <paramref name="crankAngle"/>, the upper body turned <paramref name="sway"/>
    /// (rad, about the node's forward axis) against a standing rider's rocking bike.
    /// </summary>
    public static void PoseCyclist(SkinnedFigure f, Player.BikeFrame frame, Player.RidingPosition from, Player.RidingPosition to,
        float t, float crankAngle, float sway, Vector3 wind)
    {
        var rig = t >= 1f ? CyclingRigFor(to, frame) : Lerp(CyclingRigFor(from, frame), CyclingRigFor(to, frame), t);
        if (sway != 0f)
        {
            // the node's turn about its forward axis (−Z) is the opposite turn about author +Z
            var turn = new Basis(Vector3.Back, -sway);
            rig = rig with
            {
                HeadTop = turn * rig.HeadTop, HeadBase = turn * rig.HeadBase, Neck = turn * rig.Neck, Chest = turn * rig.Chest,
                Waist = turn * rig.Waist, Hip = turn * rig.Hip,
                ShoulderL = turn * rig.ShoulderL, ElbowL = turn * rig.ElbowL, WristL = turn * rig.WristL,
                ShoulderR = turn * rig.ShoulderR, ElbowR = turn * rig.ElbowR, WristR = turn * rig.WristR,
            };
        }
        bool dressed = f.Skin is { } skin && !skin.Look.Palette.Outfit.IsEmpty;
        (Vector3 Hip, Vector3 Knee, Vector3 Ankle, Vector3 Toe) Leg(int side)
        {
            var hip = t >= 1f ? frame.LegHip(side, to) : frame.LegHip(side, from).Lerp(frame.LegHip(side, to), t);
            // BikeFrame.Pedal carries the sign: the cranks are drawn from the same call, so the two cannot disagree
            var pedal = frame.Pedal(crankAngle, side);
            // the knee leads the hip on a bicycle; +Z is forward in author space
            var knee = Limb.Solve(hip, pedal, Player.BikeFrame.ThighLength, Player.BikeFrame.ShinLength, new Vector3(0, 0, 1));
            return (hip, knee, dressed ? pedal + PedalAnkle : pedal, pedal + PedalToe);
        }
        var plus = Leg(1);
        var minus = Leg(-1);
        rig = rig with
        {
            HipR = plus.Hip, KneeR = plus.Knee, AnkleR = plus.Ankle, ToeR = plus.Toe,
            HipL = minus.Hip, KneeL = minus.Knee, AnkleL = minus.Ankle, ToeL = minus.Toe,
        };
        PoseRig(f, rig, wind);
    }

    /// <summary>
    /// A cyclist's leg at rest, as it was drawn apart from the rider: in its clothes (#251), or bare,
    /// the cycling shorts and a shoe on the pedal (the ankle).
    /// </summary>
    private static void PedallingLeg(MeshScratch s, HumanPalette p, Vector3 hip, Vector3 knee, Vector3 ankle, Vector3 toe)
    {
        if (!p.Outfit.IsEmpty)
        {
            AppendLeg(s, p, hip, knee, ankle, toe);
            return;
        }
        using var smoothing = s.Smoothing(SmoothFigures);
        bool left = hip.X < 0;
        using (s.Resting(LegRest(hip, knee, ankle, Vector3.Right, left)))
        {
            s.Tube(hip, knee, 0.088f, 0.062f, p.Shorts, 6);
            s.Tube(knee, ankle, 0.062f, 0.042f, p.Skin, 6);
        }
        using (s.Resting(FootRest(ankle, toe, Vector3.Right, left)))
            s.Box(ankle, new Vector3(0.058f, 0.045f, 0.115f), p.Shoes);
    }

    /// <summary>A fixed pose with the item arm override on top (<see cref="BuildPosed"/>'s).</summary>
    public static void PosePosed(SkinnedFigure f, HumanPose pose, ItemArmPose arm = ItemArmPose.None, float armBlend = 0f, Vector3 wind = default) =>
        PoseRig(f, ApplyArms(RigFor(pose), arm, armBlend), wind);

    /// <summary>A fall's windmill at <paramref name="t"/> seconds (<see cref="BuildFalling"/>'s).</summary>
    public static void PoseFalling(SkinnedFigure f, float t, ItemArmPose arm = ItemArmPose.None, float armBlend = 0f, Vector3 wind = default) =>
        PoseRig(f, ApplyArms(FallRig(t), arm, armBlend), wind);

    /// <summary>A swimmer at <paramref name="phase"/> of its stroke (<see cref="BuildSwim"/>'s).</summary>
    public static void PoseSwim(SkinnedFigure f, SwimStyle style, float phase) => PoseRig(f, SwimRig(style, phase), Vector3.Zero);

    /// <summary>A figure from free joint points in <see cref="Joint"/> order (a ragdoll, a skier: <see cref="BuildJoints"/>'s).</summary>
    public static void PoseJoints(SkinnedFigure f, ReadOnlySpan<Vector3> j, Vector3 wind = default) =>
        PoseRig(f, new Rig(j[0], j[1], j[2], j[3], j[4], j[5], j[6], j[7], j[8], j[9], j[10], j[11],
            j[12], j[13], j[14], j[15], j[16], j[17], j[18], j[19], TorsoLean: 0f), wind);

    /// <summary>A driver in its seat holding the wheel at <paramref name="wheelAngle"/>, on the pedals (<see cref="AppendDriver"/>'s).</summary>
    public static void PoseDriver(SkinnedFigure f, DriverSeat seat, float wheelAngle, float throttle, float brake) =>
        PoseRig(f, DriverRig(seat, wheelAngle, throttle, brake), Vector3.Zero);

    /// <summary>A pilot reaching for its controls and pedals (<see cref="AppendPilot"/>'s).</summary>
    public static void PosePilot(SkinnedFigure f, DriverSeat seat, Vector3 handPlusX, Vector3 handMinusX, Vector3 footPlusX, Vector3 footMinusX) =>
        PoseRig(f, PilotRig(seat, handPlusX, handMinusX, footPlusX, footMinusX), Vector3.Zero);

    /// <summary>A rider astride from its seat, grips and pegs (<see cref="AppendRider"/>'s).</summary>
    public static void PoseRider(SkinnedFigure f, Vector3 seat, Vector3 grip, Vector3 peg, Vector3 wind = default) =>
        PoseRig(f, RiderRig(seat, grip, peg), wind);

    /// <summary>
    /// Every bone's frame in <paramref name="r"/>, author space, into <paramref name="frames"/>:
    /// joint math only. <paramref name="wind"/> blows a skirt's hem downwind (<see cref="AppendSkirt"/>).
    /// </summary>
    private static void Frames(in Rig r, SkinBody body, Vector3 wind, Span<Transform3D> frames)
    {
        frames[0] = Transform3D.Identity;
        var torso = new Torso(r, body.Shape);
        for (int k = 0; k < TrunkStations; k++)
        {
            float s = k * 0.25f;
            var (side, up, fwd) = torso.Frame(s);
            frames[BoneTrunk + k] = new Transform3D(new Basis(side, up, fwd), torso.At(s));
        }
        var shoulderLine = ShoulderLine(r);
        LimbFrames(r.ShoulderL, r.ElbowL, r.WristL, shoulderLine, frames[BoneArmL..]);
        LimbFrames(r.ShoulderR, r.ElbowR, r.WristR, shoulderLine, frames[BoneArmR..]);
        frames[BoneArmL + 3] = HandBone(r.ElbowL, r.WristL, -1f);
        // pointing (#889), the hand is drawn along its own way (AppendBody)
        frames[BoneArmR + 3] = HandBone(r.PointDirR != Vector3.Zero ? r.WristR - r.PointDirR : r.ElbowR, r.WristR, 1f);
        LimbFrames(r.HipL, r.KneeL, r.AnkleL, shoulderLine, frames[BoneLegL..]);
        LimbFrames(r.HipR, r.KneeR, r.AnkleR, shoulderLine, frames[BoneLegR..]);
        frames[BoneLegL + 3] = FootBone(r.AnkleL, r.ToeL, shoulderLine);
        frames[BoneLegR + 3] = FootBone(r.AnkleR, r.ToeR, shoulderLine);

        // the head as Head draws it: up its axis, square to the shoulder line, scaled with its length
        var axis = r.HeadTop - r.HeadBase;
        float len = axis.Length();
        var headUp = len > 1e-4f ? axis / len : Vector3.Up;
        var headSide = (shoulderLine - headUp * shoulderLine.Dot(headUp)).Normalized();
        var headFwd = headSide.Cross(headUp);
        frames[BoneHead] = new Transform3D(new Basis(headSide * len, headUp * len, headFwd * len), r.HeadBase);

        // the neck as AppendBody draws it: a tube from the top of the trunk into the skull behind the jaw
        float scale = body.Shape.Head * len / 0.19f;
        var neckTop = r.HeadBase + (headUp * 0.045f - headFwd * 0.012f) * scale;
        var neckBottom = r.Neck - torso.Up(4f) * 0.03f;
        var neck = neckTop - neckBottom;
        var neckUp = neck.LengthSquared() > 1e-8f ? neck.Normalized() : Vector3.Up;
        var neckSide = (shoulderLine - neckUp * shoulderLine.Dot(neckUp)).Normalized();
        frames[BoneNeck] = new Transform3D(new Basis(neckSide, neck, neckSide.Cross(neckUp)), neckBottom);

        if (frames.Length > BoneSkirt) SkirtBones(r, torso, body, frames[BoneTrunk + SkirtStation], wind, frames[BoneSkirt..]);
    }

    /// <summary>
    /// A limb's three bones as <see cref="LimbLoft"/> draws it: the upper bone (Y along it, scaled
    /// to its length), the joint's ring on the mitre stretched across the bend, the lower bone; each
    /// side carried on from the one before, from <paramref name="hint"/>.
    /// </summary>
    private static void LimbFrames(Vector3 root, Vector3 joint, Vector3 end, Vector3 hint, Span<Transform3D> bones)
    {
        var d1 = joint - root;
        float len1 = Mathf.Max(d1.Length(), 1e-5f);
        d1 /= len1;
        var d2 = end - joint;
        float len2 = Mathf.Max(d2.Length(), 1e-5f);
        d2 /= len2;
        var mitre = (d1 + d2).LengthSquared() > 1e-6f ? (d1 + d2).Normalized() : d1;
        var bend = d1.Cross(d2);
        var across = bend.LengthSquared() > 1e-6f ? mitre.Cross(bend).Normalized() : Vector3.Zero;
        float stretch = 1f / Mathf.Max(mitre.Dot(d1), 0.7f) - 1f;

        var side1 = Square(hint, d1);
        var sideJ = Square(side1, mitre);
        var side2 = Square(sideJ, d2);
        bones[0] = new Transform3D(new Basis(side1, d1 * len1, side1.Cross(d1)), root);
        var x = sideJ;
        var z = sideJ.Cross(mitre);
        bones[1] = new Transform3D(new Basis(x + across * (across.Dot(x) * stretch), mitre + across * (across.Dot(mitre) * stretch),
            z + across * (across.Dot(z) * stretch)), joint);
        bones[2] = new Transform3D(new Basis(side2, d2 * len2, side2.Cross(d2)), joint);

        // LimbLoft's side carried along: made square to the direction, or any square one when it lies along it
        static Vector3 Square(Vector3 u, Vector3 dir)
        {
            u -= dir * u.Dot(dir);
            if (u.LengthSquared() < 1e-8f) u = dir.Cross(Mathf.Abs(dir.Y) < 0.9f ? Vector3.Up : Vector3.Right);
            return u.Normalized();
        }
    }

    /// <summary>A hand's frame as <see cref="Hand"/> draws it: at the wrist, Y along the hand, X its thumb side.</summary>
    private static Transform3D HandBone(Vector3 from, Vector3 wrist, float side)
    {
        var h = HandFrameOf(from, wrist, side, 1f);
        return new Transform3D(new Basis(h.Thumb, h.Along, h.Thumb.Cross(h.Along)), wrist);
    }

    /// <summary>A foot's frame as <see cref="Boot"/> draws it: at the ankle, Y to the toe (its length), X across it.</summary>
    private static Transform3D FootBone(Vector3 ankle, Vector3 toe, Vector3 sideAxis)
    {
        var f = toe - ankle;
        float len = f.Length();
        if (len < 1e-4f) return new Transform3D(Basis.Identity, ankle);
        var along = f / len;
        var side = (sideAxis - along * sideAxis.Dot(along)).Normalized();
        return new Transform3D(new Basis(side, f, along.Cross(side)), ankle);
    }

    /// <summary>The middle of a skirt's waistband (spine 2, a few mm proud), as <see cref="HangSkirt"/> hangs it.</summary>
    private static Vector3 SkirtTopOf(in Fit fit)
    {
        var c = Vector3.Zero;
        for (int k = 0; k < DrapeSides; k++) c += fit.Torso.Surface(2f, Mathf.Tau * k / DrapeSides, 0.006f);
        return c / DrapeSides;
    }

    /// <summary>
    /// A skirt's bones: one for each point of its rings below the waistband (where
    /// <see cref="AppendSkirt"/> hangs it, its hem blown downwind), each moved out as far as
    /// <see cref="HangSkirt"/> pushes its point out round the hips and legs. A tier under it (a
    /// petticoat, a second ruffle) is hung the same way to its own hem, on bones of its own.
    /// </summary>
    private static void SkirtBones(in Rig r, in Torso torso, SkinBody body, in Transform3D station, Vector3 wind, Span<Transform3D> bones)
    {
        if (body.Cut is not { } rest || SkirtCutOf(r, torso, body.Top, body.Bottom) is not { } cut)
        {
            bones.Fill(Transform3D.Identity);
            return;
        }
        // the waistband rides on the trunk's ring at spine 2
        var c0 = station * (RestStation(SkirtStation).AffineInverse() * body.SkirtTop);
        DrapeBones(r, torso, body.Shape, c0, Blown(cut.Hem, r.Waist, wind), rest.Radius, body.SkirtRest, bones[..SkirtBoneCount]);
        if (rest.Tiered && bones.Length >= 2 * SkirtBoneCount)
            DrapeBones(r, torso, body.Shape, c0, Blown(cut.TierHem, r.Waist, wind), rest.TierRadius, body.TierRest, bones[SkirtBoneCount..]);
    }

    /// <summary>One drape's bones, from the waistband's middle <paramref name="c0"/> to <paramref name="hem"/>, each point pushed out from where it was at rest.</summary>
    private static void DrapeBones(in Rig r, in Torso torso, Physique shape, Vector3 c0, Vector3 hem, float radius, float[] atRest,
        Span<Transform3D> into)
    {
        var (axis, len, side, fwd) = SkirtAxes(torso, c0, hem);
        Span<float> reach = stackalloc float[SkirtBoneCount];
        SkirtExtents(r, torso, shape, c0, axis, len, side, fwd, radius, reach);
        var basis = new Basis(side, axis * len, fwd);
        for (int i = 0; i < SkirtLevels; i++)
        {
            var at = c0 + axis * (len * DrapeAt[i + 1]);
            for (int q = 0; q < SkirtSectors; q++)
            {
                int b = i * SkirtSectors + q;
                into[b] = new Transform3D(basis, at + SectorDir(q, side, fwd) * (reach[b] - atRest[b]));
            }
        }
    }

    /// <summary>Point <paramref name="q"/>'s way out round a skirt's axis: 0 its side, then round by the front (<see cref="HangSkirt"/>'s).</summary>
    private static Vector3 SectorDir(int q, Vector3 side, Vector3 fwd)
    {
        float a = Mathf.Tau * q / SkirtSectors;
        return side * Mathf.Cos(a) + fwd * Mathf.Sin(a);
    }

    /// <summary>A skirt's axis from its waistband to its hem, its length, and the side and front square to it (<see cref="HangSkirt"/>'s).</summary>
    private static (Vector3 Axis, float Length, Vector3 Side, Vector3 Fwd) SkirtAxes(in Torso torso, Vector3 c0, Vector3 hem)
    {
        var axis = hem - c0;
        float len = Mathf.Max(axis.Length(), 0.05f);
        axis /= len;
        var side = torso.Frame(2f).Side;
        side = (side - axis * side.Dot(axis)).Normalized();
        return (axis, len, side, axis.Cross(side));
    }

    /// <summary>
    /// How far <see cref="HangSkirt"/> hangs each point of each ring below the waistband out from
    /// the axis: the waistband's own shape flared toward a hem of <paramref name="hemRadius"/>, pushed
    /// out round the trunk below it and the legs, never in again on the way down.
    /// </summary>
    private static void SkirtExtents(in Rig r, in Torso torso, Physique shape, Vector3 c0, Vector3 axis, float len,
        Vector3 side, Vector3 fwd, float hemRadius, Span<float> levels)
    {
        const float band = 2f;
        Span<float> start = stackalloc float[SkirtSectors];
        float topRadius = 0f;
        for (int k = 0; k < SkirtSectors; k++)
        {
            var off = torso.Surface(band, Mathf.Tau * k / SkirtSectors, 0.006f) - c0;
            start[k] = (off - axis * off.Dot(axis)).Length();
            topRadius += start[k] / SkirtSectors;
        }
        // what the skirt must clear, as HangSkirt has it: the trunk below the band, then each leg
        Span<Vector4> points = stackalloc Vector4[128];
        int n = 0;
        for (float sp = 0f; sp < band - 0.05f; sp += 0.25f)
            for (int k = 0; k < 10; k++)
            {
                var q = torso.Surface(sp, Mathf.Tau * k / 10f, 0.010f);
                points[n++] = new Vector4(q.X, q.Y, q.Z, 0f);
            }
        for (int leg = 0; leg < 2; leg++)
        {
            var (hip, knee, ankle) = leg == 0 ? (r.HipL, r.KneeL, r.AnkleL) : (r.HipR, r.KneeR, r.AnkleR);
            for (int j = 0; j <= 14 && n < points.Length; j++)
            {
                float lt = j / 7f;
                var q = Along(hip, knee, ankle, lt);
                points[n++] = new Vector4(q.X, q.Y, q.Z, LegRadius(shape, lt) + 0.014f);
            }
        }
        points = points[..n];
        Span<float> need = stackalloc float[SkirtSectors];
        start.CopyTo(need);
        for (int i = 1; i <= SkirtLevels; i++)
        {
            float f = DrapeAt[i];
            var c = c0 + axis * len * f;
            float flare = Mathf.Max(hemRadius - topRadius, 0f) * Mathf.Pow(f, 1.5f);
            float reach = (DrapeAt[i] - DrapeAt[i - 1]) * len * 0.6f + 0.02f;
            for (int q = 0; q < SkirtSectors; q++)
            {
                var d = SectorDir(q, side, fwd);
                float want = Mathf.Max(need[q], start[q] * (1f + flare / topRadius));
                foreach (var b in points)
                {
                    var v = new Vector3(b.X, b.Y, b.Z) - c;
                    if (Mathf.Abs(v.Dot(axis)) > reach) continue;
                    want = Mathf.Max(want, v.Dot(d) + b.W);
                }
                need[q] = want;
            }
            need.CopyTo(levels.Slice(SkirtSectors * (i - 1), SkirtSectors));
        }
    }

    private static Transform3D? _restStation8;

    /// <summary>The rest rig's trunk ring at station <paramref name="k"/> (the skirt's only, so far).</summary>
    private static Transform3D RestStation(int k)
    {
        if (k == SkirtStation && _restStation8 is { } t) return t;
        var torso = new Torso(RestRig, default);
        var (side, up, fwd) = torso.Frame(k * 0.25f);
        var frame = new Transform3D(new Basis(side, up, fwd), torso.At(k * 0.25f));
        if (k == SkirtStation) _restStation8 = frame;
        return frame;
    }

    /// <summary>
    /// How a skirt, robe or dress hangs (<see cref="AppendSkirt"/>'s, before the wind): its hem and
    /// the hem's radius, and a tier under it (a dress's petticoat, a ruffle's second): its own hem
    /// and radius, and the fraction of the skirt it starts at (<see cref="Tier"/>).
    /// </summary>
    internal readonly record struct SkirtCut(Vector3 Hem, float Radius, Vector3 TierHem = default, float TierRadius = 0f, float TierFrom = 0f)
    {
        public bool Tiered => TierRadius > 0f;
    }

    private static SkirtCut? SkirtCutOf(in Rig r, in Torso torso, Garment? top, Garment? bottom)
    {
        var knees = (r.KneeL + r.KneeR) * 0.5f;
        var ankles = (r.AnkleL + r.AnkleR) * 0.5f;
        // hips wider than the old figure's (a curvy build) widen every hem with them
        float extra = Mathf.Max(0f, torso.Width(1f) - 0.135f);
        if (top is { Draped: true })
        {
            if (top.Shape == GarmentShape.Robe) return new SkirtCut(ankles + Vector3.Up * 0.06f, 0.30f + extra);
            var dress = r.Hip.Lerp(knees, 0.85f);
            return new SkirtCut(dress, 0.29f + extra, dress - Vector3.Up * 0.035f, 0.32f + extra, 0.78f);
        }
        if (!IsSkirt(bottom)) return null;
        var mini = r.Hip.Lerp(knees, 0.40f);
        return bottom!.Shape switch
        {
            GarmentShape.PleatedSkirt => new SkirtCut(r.Hip.Lerp(knees, 0.55f), 0.25f + extra),
            GarmentShape.RuffleMini => new SkirtCut(mini, 0.22f + extra, mini - Vector3.Up * 0.045f, 0.255f + extra, 0.55f),
            GarmentShape.HighLowSkirt => new SkirtCut(r.Hip.Lerp(knees, 0.85f) + new Vector3(0, -0.04f, -0.14f), 0.27f + extra),
            GarmentShape.SlitMaxi => new SkirtCut(ankles + Vector3.Up * 0.07f, 0.29f + extra),
            _ => new SkirtCut(r.Hip.Lerp(ankles, 0.80f), 0.29f + extra),   // LongPleated
        };
    }

    /// <summary>The first of a tier's rings, from the fraction of the skirt it starts at (<see cref="Tier"/>'s).</summary>
    private static int TierRing(float from)
    {
        int i0 = 1;
        while (i0 < DrapeAt.Length - 2 && DrapeAt[i0] < from) i0++;
        return i0;
    }

    /// <summary>A hem carried downwind, never further than the skirt is long, nor up past the waist (<see cref="AppendSkirt"/>).</summary>
    private static Vector3 Blown(Vector3 hem, Vector3 waist, Vector3 wind)
    {
        float speed = wind.Length();
        float gust = Mathf.Clamp(speed / 14f, 0f, 1f);
        if (gust <= 0f || speed <= 0.05f) return hem;
        float length = (hem - waist).Length();
        var shifted = hem + wind / speed * length * 0.65f * gust;
        return waist + (shifted - waist).Normalized() * length;
    }

    /// <summary>The hem never comes within 9 cm of the ground under the lower foot (<see cref="AppendSkirt"/>'s).</summary>
    private static float SkirtFloor(in Rig r) => Mathf.Min(r.AnkleL.Y, r.AnkleR.Y) - AnkleHeight + 0.09f;

    /// <summary>The shader's <c>skirt_flutter</c> for <paramref name="wind"/>: the hem's ripple and its phase now (<see cref="AppendSkirt"/>'s).</summary>
    public static Vector2 SkirtFlutter(Vector3 wind)
    {
        float speed = wind.Length();
        if (speed <= 0.05f) return Vector2.Zero;
        float gust = Mathf.Clamp(speed / 14f, 0f, 1f);
        float phase = Time.GetTicksMsec() / 1000f * (5f + 1.1f * Mathf.Min(speed, 30f));
        return new Vector2(0.03f + 0.13f * gust, Mathf.PosMod(phase, Mathf.Tau * 100f));
    }

    /// <summary>
    /// Which bones a vertex of the figure built at rest rides on, from the part it is drawn for and
    /// where it is on the rest rig (standing straight: the spine and the limbs are straight lines).
    /// </summary>
    private sealed class Weigher : MeshScratch.IBoneWeigher
    {
        private readonly Rig _r;
        private readonly float[] _spineY;   // crotch, hip, waist, chest, neck
        private readonly SkinBody _body;

        public Weigher(in Rig r, SkinBody body)
        {
            _r = r;
            _body = body;
            var crotch = r.Hip - (r.Waist - r.Hip).Normalized() * 0.085f;
            _spineY = new[] { crotch.Y, r.Hip.Y, r.Waist.Y, r.Chest.Y, r.Neck.Y };
        }

        public MeshScratch.BoneWeights Weigh(int part, Vector3 p) => part switch
        {
            PartTrunk => Trunk(p.Y),
            PartArmL => Limb(p, _r.ShoulderL, _r.ElbowL, _r.WristL, BoneArmL),
            PartArmR => Limb(p, _r.ShoulderR, _r.ElbowR, _r.WristR, BoneArmR),
            PartLegL => Limb(p, _r.HipL, _r.KneeL, _r.AnkleL, BoneLegL),
            PartLegR => Limb(p, _r.HipR, _r.KneeR, _r.AnkleR, BoneLegR),
            PartFootL => new(BoneLegL + 3),
            PartFootR => new(BoneLegR + 3),
            PartHead => new(BoneHead),
            // the neck and what is round it, on the neck's own bone
            PartNeck => new(BoneNeck),
            PartSkirt => Skirt(p),
            PartTier => Tier(p),
            _ => new(0),
        };

        /// <summary>The ring point a point of a skirt is drawn at: its ring (1..10 below the waistband) and its place round it, and how far toward the next.</summary>
        private (float Down, int Q, int Q1, float Next) Round(Vector3 p, Vector3 axis, float length)
        {
            var b = _body;
            var off = p - b.SkirtTop;
            float a = Mathf.PosMod(Mathf.Atan2(off.Dot(b.SkirtFwd), off.Dot(b.SkirtSide)), Mathf.Tau) / Mathf.Tau * SkirtSectors;
            int q = (int)a % SkirtSectors;
            return (off.Dot(axis) / length, q, (q + 1) % SkirtSectors, a - (int)a);
        }

        /// <summary>
        /// A point of a skirt: on the bones of its ring point, or (a trim, a strap) between the ring
        /// points round it and the rings above and below.
        /// </summary>
        private MeshScratch.BoneWeights Skirt(Vector3 p)
        {
            var (f, q, q1, next) = Round(p, _body.SkirtAxis, _body.SkirtLength);
            int level = 1;
            while (level < SkirtLevels && f > DrapeAt[level]) level++;
            float above = DrapeAt[level - 1], at = DrapeAt[level];
            float w = Mathf.Clamp((f - above) / (at - above), 0f, 1f);
            int lower = BoneSkirt + (level - 1) * SkirtSectors;
            if (level == 1)
                // the waistband's ring on the trunk, then the first ring's points
                return new(BoneTrunk + SkirtStation, lower + q, w * (1f - next), lower + q1, w * next);
            int upper = lower - SkirtSectors;
            return new(upper + q, upper + q1, (1f - w) * next, lower + q, w * (1f - next), lower + q1, w * next);
        }

        /// <summary>
        /// A point of a tier: its ring is a blend of the skirt's over it and the tier's own
        /// (<see cref="Tier"/>), so it rides on both ring points' bones by that blend.
        /// </summary>
        private MeshScratch.BoneWeights Tier(Vector3 p)
        {
            var b = _body;
            int i0 = TierRing(b.Cut!.Value.TierFrom);
            var (f, q, _, next) = Round(p, b.TierAxis, b.TierLength);
            // the ring whose blended height (along the tier's axis) is nearest
            int ring = i0;
            float best = float.MaxValue;
            for (int i = i0; i <= SkirtLevels; i++)
            {
                float w0 = (DrapeAt[i] - DrapeAt[i0]) / (1f - DrapeAt[i0]);
                float h = ((b.SkirtAxis * b.SkirtLength * (1f - w0) + b.TierAxis * b.TierLength * w0) * DrapeAt[i]).Dot(b.TierAxis) / b.TierLength;
                if (Mathf.Abs(h - f) < best) { best = Mathf.Abs(h - f); ring = i; }
            }
            float w = (DrapeAt[ring] - DrapeAt[i0]) / (1f - DrapeAt[i0]);
            int point = (ring - 1) * SkirtSectors + (next < 0.5f ? q : (q + 1) % SkirtSectors);
            return new(BoneSkirt + point, BoneSkirt + SkirtBoneCount + point, w);
        }

        /// <summary>Between the two trunk rings either side of its height on the straight rest spine.</summary>
        private MeshScratch.BoneWeights Trunk(float y)
        {
            float s;
            if (y <= _spineY[0]) s = 0f;
            else if (y >= _spineY[4]) s = 4f;
            else
            {
                int i = 0;
                while (y > _spineY[i + 1]) i++;
                s = i + (y - _spineY[i]) / (_spineY[i + 1] - _spineY[i]);
            }
            float q = s * 4f;
            int k = Mathf.Min((int)q, TrunkStations - 2);
            float w = q - k;
            return w < 1e-4f ? new(BoneTrunk + k) : new(BoneTrunk + k, BoneTrunk + k + 1, w);
        }

        /// <summary>The upper bone before the joint's mitre, the joint's ring on it, then the lower bone, the hand or foot past the end.</summary>
        private static MeshScratch.BoneWeights Limb(Vector3 p, Vector3 root, Vector3 joint, Vector3 end, int first)
        {
            const float eps = 1e-3f;
            var d1 = (joint - root).Normalized();
            var d2 = (end - joint).Normalized();
            var mitre = (d1 + d2).Normalized();
            float a = (p - joint).Dot(mitre);
            if (a < -eps) return new(first);
            if (a <= eps) return new(first + 1);
            return (p - end).Dot(d2) > eps ? new(first + 3) : new(first + 2);
        }
    }
}
