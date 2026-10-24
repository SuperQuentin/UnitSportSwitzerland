using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Who a skinned figure is, as opposed to how it stands (#1202): everything its mesh is built
/// from. Two figures with the same look share one mesh (<see cref="HumanMeshBuilder.Skin"/>); a
/// pose only moves the bones. The palette's wind is not part of it (it moves the bones and the
/// shader), and <see cref="Point"/> is the pointing hand's folded fingers (#889).
/// </summary>
public readonly record struct FigureLook(HumanPalette Palette, bool Legs = true, bool Helmet = false,
    Headwear Hat = Headwear.None, bool Body = true, bool Head = true, bool FullFace = false, bool Point = false)
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
    /// <summary>The skirt's waistband centre at rest, author space (only a look with a skirt uses it).</summary>
    internal Vector3 SkirtTop { get; init; }
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
    private const int BoneHead = BoneLegR + 4, BoneSkirt = BoneHead + 1;

    /// <summary>How many bones a skinned figure has; bone 0 is its origin, which never moves.</summary>
    public const int BoneCount = BoneSkirt + 1;

    // ---- the parts a vertex is drawn for (MeshScratch.RestMap.Part)
    private const int PartTrunk = 1, PartArmL = 2, PartArmR = 3, PartLegL = 4, PartLegR = 5, PartFootL = 6, PartFootR = 7,
        PartHead = 8, PartNeck = 9, PartSkirt = 10;

    /// <summary>The trunk station (0..16) a skirt hangs from: its waistband is at spine 2.</summary>
    private const int SkirtStation = 8;

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
        return look.Point ? r with { PointDirR = (r.WristR - r.ElbowR).Normalized() } : r;
    }

    private static FigureSkin BuildSkin(in FigureLook look)
    {
        var rig = BindRig(look);
        var palette = look.Palette;
        var top = palette.Outfit[WearSlot.Top];
        var bottom = top is { CoversBottom: true } ? null : palette.Outfit[WearSlot.Bottom];
        var fit = new Fit(rig, palette.Appearance.Build);
        var skirtTop = SkirtTopOf(fit);
        var weigher = new Weigher(rig, fit, skirtTop, SkirtHem(rig, top, bottom));
        var scratch = new MeshScratch { Weigher = weigher };
        _smoothOverride = look.Smooth;
        try
        {
            AppendRig(scratch, palette, rig, look.Legs, look.Helmet, look.Hat, look.Body, look.Head, look.FullFace);
        }
        finally { _smoothOverride = null; }
        var frames = new Transform3D[BoneCount];
        Frames(rig, top, bottom, skirtTop, Vector3.Zero, frames);
        var inverse = new Transform3D[BoneCount];
        for (int i = 0; i < BoneCount; i++) inverse[i] = frames[i].AffineInverse();
        return new FigureSkin
        {
            Look = look, Mesh = scratch.Build(), BindInverse = inverse, SkirtTop = skirtTop,
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
        var frames = _frames ??= new Transform3D[BoneCount];
        var o = skin.Look.Palette.Outfit;
        var top = o[WearSlot.Top];
        Frames(r, top, top is { CoversBottom: true } ? null : o[WearSlot.Bottom], skin.SkirtTop, wind, frames);
        f.Pose(frames);
        if (skin.Flutters) f.SetFlutter(SkirtFlutter(wind));
    }

    /// <summary><see cref="BuildStride"/>'s figure as bones: the gait, a dance, an item's arms, VR hands, a pointing finger.</summary>
    public static void PoseStride(SkinnedFigure f, float speed, float phase, ItemArmPose arm = ItemArmPose.None, float armBlend = 0f,
        DanceParams? dance = null, VrArms? vr = null, Pointing? point = null, Vector3 wind = default)
    {
        // a skirt with no measured wind still feels the stride's own (#251)
        if (wind == Vector3.Zero && speed > 0.05f && f.Skin is { Flutters: true }) wind = new Vector3(0, 0, -speed);
        PoseRig(f, ApplyPoint(ApplyVr(ApplyArms(GaitWithDance(speed, phase, dance), arm, armBlend), vr), point), wind);
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
    private static void Frames(in Rig r, Garment? top, Garment? bottom, Vector3 bindSkirtTop, Vector3 wind, Span<Transform3D> frames)
    {
        frames[0] = Transform3D.Identity;
        var torso = new Torso(r, default);
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
        frames[BoneHead] = new Transform3D(new Basis(headSide * len, headUp * len, headSide.Cross(headUp) * len), r.HeadBase);

        frames[BoneSkirt] = SkirtBone(r, torso, top, bottom, frames[BoneTrunk + SkirtStation], bindSkirtTop, wind);
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
    /// A skirt's bone: at its waistband, Y down to its hem (scaled to the length), the hem where
    /// <see cref="AppendSkirt"/> puts it, blown downwind.
    /// </summary>
    private static Transform3D SkirtBone(in Rig r, in Torso torso, Garment? top, Garment? bottom, in Transform3D station,
        Vector3 bindTop, Vector3 wind)
    {
        if (SkirtHem(r, top, bottom) is not { } hem) return Transform3D.Identity;
        // the waistband rides on the trunk's ring at spine 2
        var bind = RestStation(SkirtStation);
        var c0 = station * (bind.AffineInverse() * bindTop);
        hem = Blown(hem, r.Waist, wind);
        var axis = hem - c0;
        float len = Mathf.Max(axis.Length(), 0.05f);
        axis /= len;
        var side = torso.Frame(2f).Side;
        side = (side - axis * side.Dot(axis)).Normalized();
        return new Transform3D(new Basis(side, axis * len, axis.Cross(side)), c0);
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

    /// <summary>The hem a skirt, robe or dress hangs to (<see cref="AppendSkirt"/>'s, before the wind), or null without one.</summary>
    private static Vector3? SkirtHem(in Rig r, Garment? top, Garment? bottom)
    {
        var knees = (r.KneeL + r.KneeR) * 0.5f;
        var ankles = (r.AnkleL + r.AnkleR) * 0.5f;
        if (top is { Draped: true })
            return top.Shape == GarmentShape.Robe ? ankles + Vector3.Up * 0.06f : r.Hip.Lerp(knees, 0.85f);
        if (!IsSkirt(bottom)) return null;
        return bottom!.Shape switch
        {
            GarmentShape.PleatedSkirt => r.Hip.Lerp(knees, 0.55f),
            GarmentShape.RuffleMini => r.Hip.Lerp(knees, 0.40f),
            GarmentShape.HighLowSkirt => r.Hip.Lerp(knees, 0.85f) + new Vector3(0, -0.04f, -0.14f),
            GarmentShape.SlitMaxi => ankles + Vector3.Up * 0.07f,
            _ => r.Hip.Lerp(ankles, 0.80f),   // LongPleated
        };
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
        private readonly float _neckLow, _neckHigh;
        private readonly Vector3 _skirtTop, _skirtAxis;
        private readonly float _skirtLength;

        public Weigher(in Rig r, in Fit fit, Vector3 skirtTop, Vector3? skirtHem)
        {
            _r = r;
            var crotch = r.Hip - (r.Waist - r.Hip).Normalized() * 0.085f;
            _spineY = new[] { crotch.Y, r.Hip.Y, r.Waist.Y, r.Chest.Y, r.Neck.Y };
            // the neck from the bottom of its tube to where it goes into the skull (AppendBody)
            _neckLow = r.Neck.Y - 0.03f;
            _neckHigh = fit.Head.NeckTop.Y;
            _skirtTop = skirtTop;
            var axis = (skirtHem ?? skirtTop + Vector3.Down) - skirtTop;
            _skirtLength = Mathf.Max(axis.Length(), 0.05f);
            _skirtAxis = axis / _skirtLength;
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
            // the neck and what is round it: from the top of the trunk into the head
            PartNeck => new(BoneTrunk + TrunkStations - 1, BoneHead, Mathf.Clamp((p.Y - _neckLow) / (_neckHigh - _neckLow), 0f, 1f)),
            // a skirt from its waistband (the trunk) down to its hem (its own bone)
            PartSkirt => new(BoneTrunk + SkirtStation, BoneSkirt, Mathf.Clamp((p - _skirtTop).Dot(_skirtAxis) / _skirtLength, 0f, 1f)),
            _ => new(0),
        };

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
