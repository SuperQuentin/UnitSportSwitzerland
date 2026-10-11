using Godot;
using FaceGenome = UnitSport.Avatar.Face.FaceGenome;

namespace UnitSport.Avatar;

/// <summary>A figure's build (#394): one skeleton, different flesh on it. Replicated in <see cref="Appearance"/>: append only.</summary>
public enum BodyBuild
{
    Slim = 0,
    Curvy = 1,
    /// <summary>Masculine, athletic: wide shoulders and chest, narrow hips, square jaw.</summary>
    Broad = 2,
    /// <summary>Masculine and slim: flat chest, straight waist, square jaw.</summary>
    Lean = 3,
    /// <summary>Masculine and heavy: thick waist and neck, big arms.</summary>
    Stocky = 4,
}

/// <summary>Hair drawn over the head's crown (#394). Replicated in <see cref="Appearance"/>: append only.</summary>
public enum HairStyle
{
    None = 0,
    Spiky = 1,
    Bob = 2,
    Ponytail = 3,
    Long = 4,
    /// <summary>A crew cut: the cap only, short at the front.</summary>
    Short = 5,
    /// <summary>A quiff: short sides, the front swept up and forward.</summary>
    Quiff = 6,
    /// <summary>Shaggy: short locks falling all round, over the ears and the brow.</summary>
    Shaggy = 7,
    /// <summary>A mohawk: a crest of spikes front to back, the sides shaved.</summary>
    Mohawk = 8,
    /// <summary>Long and straight with a blunt fringe cut straight across the brow.</summary>
    BluntBangs = 9,
    /// <summary>Chin length, a long fringe swept over one eye.</summary>
    SideSwept = 10,
    /// <summary>Two tails high on the sides, and a fringe.</summary>
    Twintails = 11,
    /// <summary>Hair tied in a bun at the back of the crown, a short fringe.</summary>
    Bun = 12,
}

/// <summary>
/// Who a figure is, as opposed to what it wears (#394): build, face, eye colour, skin tone, hair
/// style and hair colour. A player chooses it (<c>GameSettings.Appearance</c>) and it replicates
/// packed in one int (<c>FootPlayer.AppearanceBits</c>); everyone else gets one from a seed.
/// Indices into <see cref="SkinTones"/>, <see cref="EyeColours"/> and <see cref="HairColours"/>:
/// append to those tables only, a packed value must mean the same figure on every peer.
/// </summary>
public readonly record struct Appearance(BodyBuild Build, int Face, int Eyes, int Skin, HairStyle Hair, int HairColour)
{
    public static readonly Color[] SkinTones =
    {
        new("f5dbc7"), new("edc7a8"), new("e6bd9e"), new("cc9970"), new("bd9466"), new("946647"), new("6b4730"), new("4d3324"),
    };

    public static readonly Color[] EyeColours =
    {
        new("6b3e1f"), new("3a78d8"), new("3f8f4a"), new("8e4fd8"), new("d83a5c"), new("d8a83a"), new("8a9aa8"), new("1a1a1a"),
    };

    public static readonly Color[] HairColours =
    {
        new("15100e"), new("3a2416"), new("6b4428"), new("8a3a1c"), new("c81e14"), new("d8732a"),
        new("d8b860"), new("e8e2cf"), new("8a8a8a"), new("33b8bc"), new("1f6fbf"), new("e86aa8"),
    };

    public static readonly int Builds = Enum.GetValues<BodyBuild>().Length;
    public static readonly int HairStyles = Enum.GetValues<HairStyle>().Length;

    public Color SkinColour => SkinTones[Mathf.PosMod(Skin, SkinTones.Length)];
    public Color EyeColour => EyeColours[Mathf.PosMod(Eyes, EyeColours.Length)];
    public Color HairTint => HairColours[Mathf.PosMod(HairColour, HairColours.Length)];

    // bit 30 marks a packed value as chosen: 0 (an int's default) is "nobody chose, use the seed"
    private const int Set = 1 << 30;

    /// <summary>Build 3 bits, face 4, eyes 3, skin 3, hair style 4, hair colour 4, and the chosen flag.</summary>
    public int Pack() => Set | (int)Build | (Face & 15) << 3 | (Eyes & 7) << 7 | (Skin & 7) << 10
        | ((int)Hair & 15) << 13 | (HairColour & 15) << 17;

    /// <summary>A packed value back, or null when it was never set.</summary>
    public static Appearance? Unpack(int bits) => (bits & Set) == 0 ? null : new(
        (BodyBuild)Mathf.Clamp(bits & 7, 0, Builds - 1), bits >> 3 & 15, bits >> 7 & 7, bits >> 10 & 7,
        (HairStyle)Mathf.Clamp(bits >> 13 & 15, 0, HairStyles - 1), bits >> 17 & 15);

    /// <summary>
    /// A figure for someone who never chose one (an NPC, a ghost runner, a player with default
    /// settings), different for each <paramref name="seed"/> and the same on every peer.
    /// </summary>
    public static Appearance ForSeed(int seed)
    {
        uint h = (uint)seed * 2654435761u;
        int Next(int n) { h = h * 1103515245u + 12345u; return (int)(h >> 16) % n; }
        var build = (BodyBuild)Next(Builds);
        bool masc = build >= BodyBuild.Broad;
        // the preset faces (FaceGenome), the #657 ones too; the draw advances the same whatever the count
        ReadOnlySpan<int> faces = masc ? [5, 6, 7, 1, 2, 9, 11, 15] : [0, 1, 2, 3, 4, 8, 9, 10, 12, 13, 14];
        ReadOnlySpan<HairStyle> hairs = masc
            ? [HairStyle.Short, HairStyle.Quiff, HairStyle.Shaggy, HairStyle.Spiky, HairStyle.Mohawk, HairStyle.Long, HairStyle.Bun]
            : [HairStyle.Bob, HairStyle.Ponytail, HairStyle.Long, HairStyle.BluntBangs, HairStyle.SideSwept, HairStyle.Twintails, HairStyle.Bun, HairStyle.Spiky];
        int face = faces[Next(faces.Length)];
        var hair = hairs[Next(hairs.Length)];
        // natural colours mostly, a dyed one now and then
        int hairColour = Next(10) < 8 ? Next(9) : 9 + Next(3);
        return new Appearance(build, face, Next(EyeColours.Length), Next(SkinTones.Length), hair, hairColour);
    }

    public static readonly Appearance Default = new(BodyBuild.Slim, 1, 0, 2, HairStyle.Bob, 2);

    // what each rider chose, by the rider index palettes are made from (HumanPalette.ForRider): a
    // ride's visual is built from that index alone, so it finds its rider's figure here
    private static readonly Dictionary<int, Appearance> Chosen = new();

    /// <summary>
    /// Records what <paramref name="rider"/> chose (<c>FootPlayer.AppearanceBits</c>), or forgets
    /// it when <paramref name="bits"/> is unset; on every peer, before anything of theirs is drawn.
    /// </summary>
    public static void Register(int rider, int bits)
    {
        if (Unpack(bits) is { } a) Chosen[rider] = a;
        else Chosen.Remove(rider);
    }

    /// <summary>The figure of <paramref name="rider"/>: the one they chose, else the one from their seed.</summary>
    public static Appearance For(int rider) => Chosen.TryGetValue(rider, out var a) ? a : ForSeed(rider);
}

/// <summary>
/// The flesh of a build: half-widths and radii in metres at the body's landmarks (#394). The bone
/// lengths are the skeleton's and shared by every build, so the gait, the IK, the dances and every
/// seat are the same for all of them; only what is drawn round the bones changes.
/// </summary>
public readonly record struct Physique(
    float Crotch, float Hip, float HipBack, float Waist, float UnderBust,
    float Chest, float ChestFront, float ChestBack, float Shoulder, float Neck,
    float ThighTop, float ThighMid, float Knee, float Calf, float Ankle,
    float Deltoid, float UpperArm, float Elbow, float Forearm, float Wrist,
    float Hand, float Head, float Jaw = 0f)
{
    public static Physique Of(BodyBuild build) => build switch
    {
        BodyBuild.Lean => new(
            Crotch: 0.112f, Hip: 0.130f, HipBack: 0.084f, Waist: 0.118f, UnderBust: 0.132f,
            Chest: 0.146f, ChestFront: 0.084f, ChestBack: 0.080f, Shoulder: 0.172f, Neck: 0.052f,
            ThighTop: 0.082f, ThighMid: 0.074f, Knee: 0.053f, Calf: 0.056f, Ankle: 0.037f,
            Deltoid: 0.062f, UpperArm: 0.050f, Elbow: 0.039f, Forearm: 0.044f, Wrist: 0.031f,
            Hand: 1.04f, Head: 1.05f, Jaw: 1f),
        BodyBuild.Stocky => new(
            Crotch: 0.128f, Hip: 0.152f, HipBack: 0.100f, Waist: 0.158f, UnderBust: 0.166f,
            Chest: 0.168f, ChestFront: 0.108f, ChestBack: 0.096f, Shoulder: 0.180f, Neck: 0.066f,
            ThighTop: 0.100f, ThighMid: 0.090f, Knee: 0.062f, Calf: 0.066f, Ankle: 0.043f,
            Deltoid: 0.070f, UpperArm: 0.063f, Elbow: 0.048f, Forearm: 0.054f, Wrist: 0.036f,
            Hand: 1.12f, Head: 1.02f, Jaw: 1f),
        BodyBuild.Curvy => new(
            Crotch: 0.125f, Hip: 0.168f, HipBack: 0.112f, Waist: 0.100f, UnderBust: 0.112f,
            Chest: 0.132f, ChestFront: 0.112f, ChestBack: 0.074f, Shoulder: 0.140f, Neck: 0.044f,
            ThighTop: 0.096f, ThighMid: 0.084f, Knee: 0.052f, Calf: 0.057f, Ankle: 0.033f,
            Deltoid: 0.050f, UpperArm: 0.044f, Elbow: 0.033f, Forearm: 0.038f, Wrist: 0.026f,
            Hand: 0.92f, Head: 1.08f),
        BodyBuild.Broad => new(
            Crotch: 0.118f, Hip: 0.140f, HipBack: 0.090f, Waist: 0.132f, UnderBust: 0.150f,
            Chest: 0.170f, ChestFront: 0.096f, ChestBack: 0.090f, Shoulder: 0.182f, Neck: 0.058f,
            ThighTop: 0.090f, ThighMid: 0.081f, Knee: 0.058f, Calf: 0.063f, Ankle: 0.040f,
            Deltoid: 0.072f, UpperArm: 0.058f, Elbow: 0.044f, Forearm: 0.050f, Wrist: 0.034f,
            Hand: 1.08f, Head: 1.05f, Jaw: 1f),
        _ => new(
            Crotch: 0.110f, Hip: 0.138f, HipBack: 0.092f, Waist: 0.106f, UnderBust: 0.120f,
            Chest: 0.136f, ChestFront: 0.086f, ChestBack: 0.076f, Shoulder: 0.150f, Neck: 0.046f,
            ThighTop: 0.082f, ThighMid: 0.073f, Knee: 0.050f, Calf: 0.053f, Ankle: 0.034f,
            Deltoid: 0.054f, UpperArm: 0.047f, Elbow: 0.036f, Forearm: 0.040f, Wrist: 0.028f,
            Hand: 0.97f, Head: 1.08f),
    };
}

/// <summary>
/// What a figure looks like, resolved (#394): build, skin, face, hair, and where its clothes start
/// and end along the body. The clothes (<c>HumanMeshBuilder.Clothing.cs</c>) turn an outfit into
/// one of these, then add what does not follow the skin (collars, skirts, buckles…). Lengths run
/// along the body: <c>Spine</c> from the crotch (0) through hip (1), waist (2) and chest (3) to the
/// neck (4); <c>Arm</c> from the shoulder (0) through the elbow (1) to the wrist (2); <c>Leg</c>
/// from the hip (0) through the knee (1) to the ankle (2).
/// </summary>
public readonly record struct BodyLook(BodyBuild Build, Color Skin)
{
    public Color Top { get; init; } = new(0.85f, 0.24f, 0.20f);
    public Color Bottom { get; init; } = new(0.16f, 0.17f, 0.20f);
    public Color Shoes { get; init; } = new(0.92f, 0.92f, 0.90f);
    public Color Sole { get; init; } = new(0.16f, 0.16f, 0.17f);
    /// <summary>Tights or stockings from <see cref="LegwearFrom"/> to the shoes; null: bare legs.</summary>
    public Color? Legwear { get; init; }
    /// <summary>Leg: where the legwear starts (0: at the hip, 0.45: a thigh-high, 1.1: a knee sock).</summary>
    public float LegwearFrom { get; init; }
    /// <summary>The legwear is drawn over the bare leg rather than as it (a pattern with holes: fishnet, lace).</summary>
    public bool LegwearOver { get; init; }
    public Color? Gloves { get; init; }
    public bool Fingerless { get; init; } = true;
    public Color? Belt { get; init; }
    public Color Hair { get; init; } = new(0.20f, 0.13f, 0.08f);
    public HairStyle HairStyle { get; init; }
    public int Face { get; init; }
    /// <summary>A face of its own instead of preset <see cref="Face"/> (#657): a seeded one (<see cref="FaceGenome.ForSeed"/>).</summary>
    public FaceGenome? Genome { get; init; }
    /// <summary>The iris, whatever the face (the atlas keys it, the shader paints it).</summary>
    public Color Eyes { get; init; } = new(0.35f, 0.22f, 0.12f);
    /// <summary>Cloth patterns (<see cref="Finish"/>: Checker, Stripes, Studs, Tartan, Fishnet…) on the top, the bottom and the legwear.</summary>
    public Finish TopPattern { get; init; }
    public Finish BottomPattern { get; init; }
    public Finish LegPattern { get; init; }
    /// <summary>Spine: where the top's hem is (above the waist, 2.5: a crop top).</summary>
    public float TopFrom { get; init; } = 1.75f;
    /// <summary>Spine: where the top stops at the top (4: the neck, 3.1: strapless).</summary>
    public float TopTo { get; init; } = 4f;
    /// <summary>Spine: the bottom's waistband.</summary>
    public float Waistband { get; init; } = 2.0f;
    /// <summary>Arm: the sleeve's end (0: sleeveless, 2: to the wrist).</summary>
    public float SleeveTo { get; init; } = 0.45f;
    /// <summary>Arm: where gloves start (past 2: none).</summary>
    public float GloveFrom { get; init; } = 2.5f;
    /// <summary>Leg: the bottom's hem (0.3 shorts, 2 trousers to the ankle).</summary>
    public float LegTo { get; init; } = 0.35f;
    /// <summary>Leg: the top of the boots (2: shoes only).</summary>
    public float BootFrom { get; init; } = 1.9f;
    /// <summary>Sole thickness, 1 a trainer's.</summary>
    public float Platform { get; init; } = 1f;

    /// <summary>A plain figure of <paramref name="a"/>: no clothes picked, the default jersey, shorts and shoes.</summary>
    public static BodyLook Of(Appearance a) => new(a.Build, a.SkinColour)
    {
        Face = a.Face, Eyes = a.EyeColour, HairStyle = a.Hair, Hair = a.HairTint,
    };
}

/// <summary>What sits on the head, so the hair goes under it (#394).</summary>
public enum HairCover
{
    /// <summary>Nothing, or something that sits on the hair (cat ears, a bow).</summary>
    None,
    /// <summary>A hat or helmet: no spikes, crest, quiff, bun or tails through it; fringe and long hair still show.</summary>
    Hat,
    /// <summary>Something over the whole head (a pumpkin): no hair, no face.</summary>
    Head,
}

public static partial class HumanMeshBuilder
{
    /// <summary>A figure drawn straight from a look, for the preview (#394).</summary>
    public static ArrayMesh BuildBody(BodyLook look, HumanPose pose = HumanPose.Standing, ArrayMesh? into = null)
    {
        var scratch = ScratchFor(into);
        using (scratch.Smoothing(SmoothFigures)) AppendBody(scratch, look, RigFor(pose));
        return into == null ? scratch.Build() : scratch.BuildInto(into);
    }

    /// <summary>A look mid-stride (<see cref="BuildStride"/>'s gait), for the preview.</summary>
    public static ArrayMesh BuildBodyStride(BodyLook look, float speed, float phase, ArrayMesh? into = null)
    {
        var scratch = ScratchFor(into);
        using (scratch.Smoothing(SmoothFigures)) AppendBody(scratch, look, GaitRig(speed, phase));
        return into == null ? scratch.Build() : scratch.BuildInto(into);
    }

    // a colour carrying the pixel face's finish id in its alpha (the clothes' Garments.Fx convention)
    private static Color FaceMark(Color eyes) => new(eyes.R, eyes.G, eyes.B, 1f - (int)Finish.Face / 255f);

    // where a face's blink and glances start (#657): from the look, so two figures side by side in
    // one mesh (a bus's passengers) blink apart, and a figure rebuilt every frame keeps its rhythm
    private static int FaceSeed(BodyLook look) =>
        (int)((uint)look.Eyes.ToRgba32() * 2654435761u >> 24 ^ (uint)look.Skin.ToRgba32() * 40503u >> 24
            ^ (uint)look.Hair.ToRgba32() * 2246822519u >> 24 ^ (uint)look.Top.ToRgba32() * 3266489917u >> 24) & 0xFF;

    /// <summary>
    /// Everything a dressed figure's clothes need to know about the body under them: the trunk's
    /// surface, the head's, the build. Built once per figure, no allocation.
    /// </summary>
    private readonly struct Fit
    {
        public readonly Rig Rig;
        public readonly Physique Shape;
        public readonly Torso Torso;
        public readonly Head Head;
        public readonly Vector3 Side;

        public Fit(Rig rig, BodyBuild build)
        {
            Rig = rig;
            Shape = Physique.Of(build);
            Torso = new Torso(rig, Shape);
            Side = (rig.ShoulderR - rig.ShoulderL).Normalized();
            Head = new Head(rig, Side, Shape.Head, Shape.Jaw);
        }
    }

    // ---- rest maps (#724): where each bit of a posed figure is when it stands at rest, for the
    // clothes' patterns (MeshScratch.Rest), so they ride on the cloth instead of the body moving
    // through them

    // the standing figure every pose is taken back to; built on first use, not from another
    // partial's statics (#221)
    private static Rig? _restRig;
    private static Rig RestRig => _restRig ??= RigFor(HumanPose.Standing);

    /// <summary>
    /// A frame on the bone <paramref name="a"/>→<paramref name="b"/>: origin at <paramref name="a"/>,
    /// Y along the bone, X <paramref name="hint"/> made square to it, turning smoothly toward
    /// <paramref name="fallback"/> as the bone comes within 30° of the hint (an arm raised sideways).
    /// </summary>
    private static Transform3D BoneFrame(Vector3 a, Vector3 b, Vector3 hint, Vector3 fallback)
    {
        var y = b - a;
        y = y.LengthSquared() > 1e-10f ? y.Normalized() : Vector3.Up;
        var x = hint - y * hint.Dot(y);
        var f = fallback - y * fallback.Dot(y);
        x += f * (1f - Mathf.Min(x.Length() * 2f, 1f));
        if (x.LengthSquared() < 1e-8f) x = y.Cross(Mathf.Abs(y.Z) < 0.9f ? Vector3.Back : Vector3.Right);
        x = x.Normalized();
        return new Transform3D(new Basis(x, y, x.Cross(y)), a);
    }

    /// <summary>The rigid motion taking a posed bone back to the same bone at rest.</summary>
    private static Transform3D BoneRest(Vector3 a, Vector3 b, Vector3 hint, Vector3 fallback,
        Vector3 restA, Vector3 restB, Vector3 restHint, Vector3 restFallback) =>
        BoneFrame(restA, restB, restHint, restFallback) * BoneFrame(a, b, hint, fallback).AffineInverse();

    /// <summary>Two bones meeting at a joint (a limb), split at the joint's mitre and blended over <paramref name="blend"/> metres.</summary>
    private static MeshScratch.RestMap TwoBoneRest(Vector3 root, Vector3 joint, Vector3 end, Vector3 hint, Vector3 fallback,
        Vector3 restRoot, Vector3 restJoint, Vector3 restEnd, Vector3 restHint, Vector3 restFallback, float blend)
    {
        var mitre = (joint - root).Normalized() + (end - joint).Normalized();
        if (mitre.LengthSquared() < 1e-8f) mitre = end - joint;
        return new MeshScratch.RestMap(
            BoneRest(root, joint, hint, fallback, restRoot, restJoint, restHint, restFallback),
            BoneRest(joint, end, hint, fallback, restJoint, restEnd, restHint, restFallback),
            joint, mitre.Normalized(), blend);
    }

    private static Vector3 ShoulderLine(in Rig r) => (r.ShoulderR - r.ShoulderL).Normalized();
    private static Vector3 TrunkUp(in Rig r) => (r.Neck - r.Hip).Normalized();
    private static Vector3 TrunkFwd(in Rig r) => ShoulderLine(r).Cross(TrunkUp(r));

    /// <summary>The trunk: hips to waist, waist to neck.</summary>
    private static MeshScratch.RestMap TrunkRest(in Rig r)
    {
        var q = RestRig;
        // forward as the fallback: the trunk never lies along its own shoulder line
        return TwoBoneRest(r.Hip, r.Waist, r.Neck, ShoulderLine(r), TrunkFwd(r), q.Hip, q.Waist, q.Neck, ShoulderLine(q), TrunkFwd(q), 0.08f);
    }

    /// <summary>An arm, shoulder to wrist (and the hand, on the forearm).</summary>
    private static MeshScratch.RestMap ArmRest(in Rig r, bool left)
    {
        var q = RestRig;
        return left
            ? TwoBoneRest(r.ShoulderL, r.ElbowL, r.WristL, ShoulderLine(r), TrunkUp(r), q.ShoulderL, q.ElbowL, q.WristL, ShoulderLine(q), TrunkUp(q), 0.04f)
            : TwoBoneRest(r.ShoulderR, r.ElbowR, r.WristR, ShoulderLine(r), TrunkUp(r), q.ShoulderR, q.ElbowR, q.WristR, ShoulderLine(q), TrunkUp(q), 0.04f);
    }

    /// <summary>A leg from free joints (a cyclist's too), hip to ankle, <paramref name="side"/> across the figure.</summary>
    private static MeshScratch.RestMap LegRest(Vector3 hip, Vector3 knee, Vector3 ankle, Vector3 side, bool left)
    {
        var q = RestRig;
        var (rh, rk, ra) = left ? (q.HipL, q.KneeL, q.AnkleL) : (q.HipR, q.KneeR, q.AnkleR);
        return TwoBoneRest(hip, knee, ankle, side, Vector3.Back, rh, rk, ra, ShoulderLine(q), Vector3.Back, 0.05f);
    }

    /// <summary>A foot, ankle to toe.</summary>
    private static MeshScratch.RestMap FootRest(Vector3 ankle, Vector3 toe, Vector3 side, bool left)
    {
        var q = RestRig;
        var (ra, rt) = left ? (q.AnkleL, q.ToeL) : (q.AnkleR, q.ToeR);
        return MeshScratch.RestMap.Rigid(BoneRest(ankle, toe, side, Vector3.Up, ra, rt, ShoulderLine(q), Vector3.Up));
    }

    /// <summary>The head and the neck under it.</summary>
    private static MeshScratch.RestMap HeadRest(in Rig r)
    {
        var q = RestRig;
        return MeshScratch.RestMap.Rigid(BoneRest(r.HeadBase, r.HeadTop, ShoulderLine(r), TrunkFwd(r),
            q.HeadBase, q.HeadTop, ShoulderLine(q), TrunkFwd(q)));
    }

    /// <summary>
    /// The figure itself: trunk, limbs, hands, boots, neck, head, face and hair, coloured by where
    /// <paramref name="look"/>'s clothes start and end. <paramref name="body"/> and
    /// <paramref name="head"/> leave out either half (a first-person driver sees their arms but not
    /// the inside of their head); <paramref name="includeLegs"/> leaves out the legs (a cyclist's are
    /// their own mesh, driven by the cranks).
    /// </summary>
    private static Fit AppendBody(MeshScratch s, BodyLook look, Rig r, bool includeLegs = true, bool body = true,
        bool head = true, HairCover cover = HairCover.None)
    {
        // the patterns ride in the colours' alpha, as the clothes' finishes do
        look = Patterned(look);
        var fit = new Fit(r, look.Build);
        // the patterns stay on the cloth however the figure moves (#724): the trunk unless a part says otherwise
        using var trunkRest = s.Resting(TrunkRest(r));
        var shape = fit.Shape;
        var torso = fit.Torso;

        if (body)
        {
            // ---- trunk: the bottom up to its waistband, bare skin to the top's hem, the top to its neckline
            float band = look.Waistband, hem = Mathf.Min(look.TopFrom, 3.8f), neckline = Mathf.Max(look.TopTo, hem);
            torso.Band(s, 0f, Mathf.Min(band, hem), look.Bottom);
            if (hem > band) torso.Band(s, band, hem, look.Skin);
            torso.Band(s, hem, neckline, look.Top);
            if (neckline < 4f) torso.Band(s, neckline, 4f, look.Skin);
            if (look.Belt is { } belt) torso.Band(s, band - 0.13f, band, belt, inflate: 0.007f);

            var armZones = new Zones(look.SleeveTo, 9f, look.GloveFrom, look.Top, look.Skin, look.Skin, look.Gloves ?? look.Skin, 0.003f);
            foreach (float side in stackalloc[] { -1f, 1f })
            {
                bool left = side < 0;
                if (includeLegs)
                {
                    var (hip, knee, ankle, toe) = left ? (r.HipL, r.KneeL, r.AnkleL, r.ToeL) : (r.HipR, r.KneeR, r.AnkleR, r.ToeR);
                    DrawLeg(s, look, shape, hip, knee, ankle, toe, fit.Side, left);
                }

                var (shoulder, elbow, wrist) = left ? (r.ShoulderL, r.ElbowL, r.WristL) : (r.ShoulderR, r.ElbowR, r.WristR);
                // one skin from the shoulder's round (the deltoid, a dome over the joint) down to the
                // wrist, mitred at the elbow, so no seam opens where the parts meet
                float upper = Mathf.Max(shoulder.DistanceTo(elbow), 0.05f);
                Span<Vector2> arm =
                [
                    new(-0.032f / upper, shape.Deltoid * 0.45f), new(-0.016f / upper, shape.Deltoid * 0.85f),
                    new(0.022f / upper, shape.Deltoid), new(0.12f / upper, shape.UpperArm), new(0.5f, shape.UpperArm * 0.93f),
                    new(1f, shape.Elbow), new(1.3f, shape.Forearm), new(2f, shape.Wrist),
                ];
                using var armRest = s.Resting(ArmRest(r, left));
                LimbLoft(s, shoulder, elbow, wrist, arm, armZones, fit.Side);
                var palm = look.Gloves ?? look.Skin;
                var fingers = look.Gloves is { } gloves && !look.Fingerless ? gloves : look.Skin;
                Hand(s, elbow, wrist, side, shape.Hand, palm, fingers);
            }
        }
        if (!head) return fit;

        using var headRest = s.Resting(HeadRest(r));
        // a straight neck up into the skull behind the jaw: the jaw's underside overhangs it, so the
        // head reads as a head on a neck rather than one cone running down into the collar
        s.Tube(r.Neck - torso.Up(4f) * 0.03f, fit.Head.NeckTop, shape.Neck, look.Skin, 8);
        fit.Head.Draw(s, look.Skin);
        if (cover != HairCover.Head)
        {
            fit.Head.Face(s, look.Genome ?? FaceGenome.Preset(look.Face).WithSeed(FaceSeed(look)), look.Eyes);
            fit.Head.Ears(s, look.Skin);
            fit.Head.Hair(s, look.HairStyle, look.Hair, look.Skin, cover);
        }
        return fit;
    }

    private static BodyLook Patterned(BodyLook look) => look.TopPattern == Finish.None && look.BottomPattern == Finish.None && look.LegPattern == Finish.None
        ? look
        : look with
        {
            Top = Garments.Fx(look.Top, look.TopPattern),
            Bottom = Garments.Fx(look.Bottom, look.BottomPattern),
            Legwear = look.Legwear is { } legs ? Garments.Fx(legs, look.LegPattern) : null,
            TopPattern = Finish.None, BottomPattern = Finish.None, LegPattern = Finish.None,
        };

    /// <summary>One leg: thigh and shin in the bottom's, the skin's and the legwear's colours, then the boot.</summary>
    private static void DrawLeg(MeshScratch s, BodyLook look, Physique shape, Vector3 hip, Vector3 knee, Vector3 ankle, Vector3 toe, Vector3 sideAxis, bool left)
    {
        using var legRest = s.Resting(LegRest(hip, knee, ankle, sideAxis, left));
        var legwear = look.Legwear is { } w && !look.LegwearOver ? w : look.Skin;
        var zones = new Zones(look.LegTo, look.LegwearFrom, look.BootFrom, look.Bottom, look.Skin, legwear, look.Shoes, 0.012f);
        // one skin from a little inside the pelvis (so no seam opens when it swings) to the ankle, mitred at the knee
        float thigh = Mathf.Max(hip.DistanceTo(knee), 0.05f);
        Span<Vector2> leg =
        [
            new(-0.035f / thigh, shape.ThighTop), new(0.4f, shape.ThighMid), new(1f, shape.Knee),
            new(1.3f, shape.Calf), new(2f, shape.Ankle),
        ];
        LimbLoft(s, hip, knee, ankle, leg, zones, sideAxis);
        // a net over the bare leg, a few mm proud, from where it starts to the boot
        if (look.LegwearOver && look.Legwear is { } over)
            LimbBand(s, hip, knee, ankle, Mathf.Clamp(look.LegwearFrom, 0f, 2f), Mathf.Clamp(look.BootFrom, 0f, 2f), shape, arm: false, 0.003f, over);
        using var footRest = s.Resting(FootRest(ankle, toe, sideAxis, left));
        Boot(s, ankle, toe, sideAxis, look);
    }

    /// <summary>The arm's radius at arm parameter <paramref name="t"/> (shoulder 0, elbow 1, wrist 2), as the body draws it.</summary>
    private static float ArmRadius(Physique p, float t) => t switch
    {
        <= 0.5f => Mathf.Lerp(p.UpperArm, p.UpperArm * 0.93f, t / 0.5f),
        <= 1f => Mathf.Lerp(p.UpperArm * 0.93f, p.Elbow, (t - 0.5f) / 0.5f),
        <= 1.3f => Mathf.Lerp(p.Elbow, p.Forearm, (t - 1f) / 0.3f),
        _ => Mathf.Lerp(p.Forearm, p.Wrist, (t - 1.3f) / 0.7f),
    };

    /// <summary>The leg's radius at leg parameter <paramref name="t"/> (hip 0, knee 1, ankle 2).</summary>
    private static float LegRadius(Physique p, float t) => t switch
    {
        <= 0.4f => Mathf.Lerp(p.ThighTop, p.ThighMid, t / 0.4f),
        <= 1f => Mathf.Lerp(p.ThighMid, p.Knee, (t - 0.4f) / 0.6f),
        <= 1.3f => Mathf.Lerp(p.Knee, p.Calf, (t - 1f) / 0.3f),
        _ => Mathf.Lerp(p.Calf, p.Ankle, (t - 1.3f) / 0.7f),
    };

    /// <summary>A point on a limb at parameter <paramref name="t"/> (root 0, middle joint 1, end 2).</summary>
    private static Vector3 Along(Vector3 root, Vector3 joint, Vector3 end, float t) =>
        t <= 1f ? root.Lerp(joint, Mathf.Max(t, 0f)) : joint.Lerp(end, Mathf.Min(t - 1f, 1f));

    /// <summary>
    /// A band of colour round a limb (an arm, or a leg) from <paramref name="t0"/> to
    /// <paramref name="t1"/>, <paramref name="proud"/> off the skin: a cuff, a sock's top, a boot's
    /// buckle, a sleeve over the arm. Cut at the limb's own stations so it follows the body's taper
    /// and the bend of the joint.
    /// </summary>
    private static void LimbBand(MeshScratch s, Vector3 root, Vector3 joint, Vector3 end, float t0, float t1,
        Physique shape, bool arm, float proud, Color colour, int sides = 7)
    {
        ReadOnlySpan<float> stations = arm ? [0f, 0.5f, 1f, 1.3f, 2f] : [0f, 0.4f, 1f, 1.3f, 2f];
        for (int i = 0; i + 1 < stations.Length; i++)
        {
            float a = Mathf.Max(t0, stations[i]), b = Mathf.Min(t1, stations[i + 1]);
            if (b - a < 1e-3f) continue;
            float ra = arm ? ArmRadius(shape, a) : LegRadius(shape, a), rb = arm ? ArmRadius(shape, b) : LegRadius(shape, b);
            s.Tube(Along(root, joint, end, a), Along(root, joint, end, b), ra + proud, rb + proud, colour, sides);
        }
    }

    /// <summary>
    /// Colours along a limb by its length parameter, in order of precedence: <see cref="C3"/> from
    /// <see cref="C"/> on (a boot, a glove; it stands <see cref="Inflate3"/> proud), <see cref="C0"/>
    /// below <see cref="A"/> (the bottom's leg, the sleeve), <see cref="C2"/> from <see cref="B"/>
    /// (legwear), else <see cref="C1"/> (skin).
    /// </summary>
    private readonly record struct Zones(float A, float B, float C, Color C0, Color C1, Color C2, Color C3, float Inflate3)
    {
        public (Color Colour, float Inflate) At(float t) =>
            t >= C ? (C3, Inflate3) : t < A ? (C0, 0f) : t >= B ? (C2, 0f) : (C1, 0f);
    }

    /// <summary>
    /// A whole limb as one loft through <paramref name="stations"/> (limb parameter, radius; root 0,
    /// joint 1, end 2, below 0 back past the root), cut where its colour changes. Every ring is
    /// square to the limb, the joint's on the bisector and stretched across the bend, like a mitre,
    /// so the skin runs through the elbow or knee with no seam, gap or overlap. The rings are
    /// carried along without twisting from <paramref name="sideHint"/>.
    /// </summary>
    private static void LimbLoft(MeshScratch s, Vector3 root, Vector3 joint, Vector3 end, ReadOnlySpan<Vector2> stations, Zones z, Vector3 sideHint)
    {
        // the stations, with a cut at every colour change
        Span<Vector2> at = stackalloc Vector2[stations.Length + 3];
        int n = 0;
        foreach (var st in stations) at[n++] = st;
        float first = stations[0].X, last = stations[^1].X;
        foreach (float c in stackalloc[] { z.A, z.B, z.C })
        {
            if (c <= first + 1e-3f || c >= last - 1e-3f) continue;
            int k = 1;
            while (stations[k].X < c) k++;
            var (a, b) = (stations[k - 1], stations[k]);
            at[n++] = new Vector2(c, Mathf.Lerp(a.Y, b.Y, (c - a.X) / (b.X - a.X)));
        }
        at = at[..n];
        at.Sort((p, q) => p.X.CompareTo(q.X));

        var d1 = (joint - root).Normalized();
        var d2 = (end - joint).Normalized();
        var mitre = (d1 + d2).LengthSquared() > 1e-6f ? (d1 + d2).Normalized() : d1;
        var bend = d1.Cross(d2);
        var across = bend.LengthSquared() > 1e-6f ? mitre.Cross(bend).Normalized() : Vector3.Zero;
        float stretch = 1f / Mathf.Max(mitre.Dot(d1), 0.7f) - 1f;

        // each station's centre, direction and side, the side carried from one to the next
        Span<Vector3> centres = stackalloc Vector3[n], dirs = stackalloc Vector3[n], sides = stackalloc Vector3[n];
        var u = sideHint;
        for (int i = 0; i < n; i++)
        {
            float t = at[i].X;
            bool atJoint = Mathf.Abs(t - 1f) < 1e-4f;
            centres[i] = t <= 1f ? root.Lerp(joint, t) : joint.Lerp(end, t - 1f);
            dirs[i] = atJoint ? mitre : t < 1f ? d1 : d2;
            u -= dirs[i] * u.Dot(dirs[i]);
            if (u.LengthSquared() < 1e-8f) u = dirs[i].Cross(Mathf.Abs(dirs[i].Y) < 0.9f ? Vector3.Up : Vector3.Right);
            sides[i] = u = u.Normalized();
        }

        int m = s.Smooth ? MeshScratch.SmoothSides : 7;
        var pool = Rings;
        // one loft per run of bands in the same colour; neighbouring runs share their ring
        int start = 0;
        while (start + 1 < n)
        {
            var (colour, inflate) = z.At(Mathf.Max((at[start].X + at[start + 1].X) * 0.5f, 0f));
            int stop = start + 1;
            while (stop + 1 < n && z.At(Mathf.Max((at[stop].X + at[stop + 1].X) * 0.5f, 0f)) == (colour, inflate)) stop++;
            pool.Begin();
            for (int i = start; i <= stop; i++)
            {
                var ring = pool.Ring(m);
                var v = dirs[i].Cross(sides[i]);
                bool atJoint = Mathf.Abs(at[i].X - 1f) < 1e-4f;
                float r = at[i].Y + inflate;
                for (int k = 0; k < m; k++)
                {
                    float a = Mathf.Tau * k / m;
                    var off = (sides[i] * Mathf.Cos(a) + v * Mathf.Sin(a)) * r;
                    if (atJoint) off += across * off.Dot(across) * stretch;
                    ring[k] = centres[i] + off;
                }
                pool.Sections.Add(ring);
            }
            s.Loft(pool.Sections, pool.Fill(colour), colour);
            start = stop;
        }
    }

    /// <summary>Where a hand is and which way it faces, for what goes on it (a paw's pads).</summary>
    private readonly record struct HandFrame(Vector3 Palm, Vector3 Along, Vector3 Thumb, Vector3 Inward, float Scale);

    private static HandFrame HandFrameOf(Vector3 elbow, Vector3 wrist, float side, float scale)
    {
        var dir = (wrist - elbow).Normalized();
        var inward = new Vector3(-side, 0, 0);
        var n = inward - dir * inward.Dot(dir);
        n = n.LengthSquared() > 1e-4f ? n.Normalized() : (Mathf.Abs(dir.Y) < 0.9f ? Vector3.Up : Vector3.Back).Cross(dir).Normalized();
        var thumbSide = dir.Cross(n) * -side;   // forward for both hands while the arms hang
        return new HandFrame(wrist + dir * 0.042f * scale, dir, thumbSide, n, scale);
    }

    /// <summary>
    /// A hand: palm, four fingers curled a little toward the palm, and a thumb on the side facing
    /// forward when the arm hangs. The palm faces the body.
    /// </summary>
    private static void Hand(MeshScratch s, Vector3 elbow, Vector3 wrist, float side, float scale, Color palm, Color fingers)
    {
        var h = HandFrameOf(elbow, wrist, side, scale);
        var (dir, thumbSide, n, k) = (h.Along, h.Thumb, h.Inward, scale);
        var basis = new Basis(thumbSide, dir, thumbSide.Cross(dir));
        s.Box(h.Palm, new Vector3(0.068f, 0.072f, 0.028f) * k, palm, basis);

        // fingers from the knuckle line, the index on the thumb side
        var curl = (dir * Mathf.Cos(0.35f) + n * Mathf.Sin(0.35f)).Normalized();
        var fingerBasis = new Basis(thumbSide, curl, thumbSide.Cross(curl));
        var knuckles = h.Palm + dir * 0.036f * k;
        ReadOnlySpan<float> lengths = [0.066f, 0.072f, 0.068f, 0.054f];
        for (int i = 0; i < 4; i++)
        {
            float across = (1.5f - i) * 0.0168f * k;
            float len = lengths[i] * k;
            s.Box(knuckles + thumbSide * across + curl * len * 0.5f, new Vector3(0.0145f, len, 0.019f) * k, fingers, fingerBasis);
        }

        var thumbRoot = wrist + dir * 0.022f * k + thumbSide * 0.028f * k;
        var thumbDir = (dir * 0.65f + thumbSide * 0.5f + n * 0.45f).Normalized();
        s.Tube(thumbRoot, thumbRoot + thumbDir * 0.055f * k, 0.013f * k, 0.009f * k, fingers, 5);
    }

    /// <summary>
    /// Rings for the lofts, reused: a figure is rebuilt whenever its pose changes, and a loft only
    /// reads its rings while it is drawn, so one pool per thread serves every loft in turn.
    /// </summary>
    private sealed class RingPool
    {
        private readonly List<Vector3[]>[] _bySize = new List<Vector3[]>[17];
        private readonly int[] _used = new int[17];
        public readonly List<Vector3[]> Sections = new(16);
        public readonly Color[] Colours = new Color[16];

        /// <summary>Starts a loft: every ring handed out before is free again.</summary>
        public void Begin()
        {
            Array.Clear(_used);
            Sections.Clear();
        }

        public Vector3[] Ring(int size)
        {
            var list = _bySize[size] ??= new List<Vector3[]>();
            if (_used[size] == list.Count) list.Add(new Vector3[size]);
            return list[_used[size]++];
        }

        public Color[] Fill(Color colour)
        {
            Array.Fill(Colours, colour);
            return Colours;
        }
    }

    [ThreadStatic] private static RingPool? _rings;
    private static RingPool Rings => _rings ??= new RingPool();

    /// <summary>
    /// A boot or shoe along ankle→toe: a heel, the instep and a toe box lofted through five
    /// sections, its lowest band the sole's colour so a platform reads from the side.
    /// </summary>
    private static void Boot(MeshScratch s, Vector3 ankle, Vector3 toe, Vector3 sideAxis, BodyLook look)
    {
        var f = toe - ankle;
        float len = f.Length();
        if (len < 1e-4f) return;
        f /= len;
        var side = (sideAxis - f * sideAxis.Dot(f)).Normalized();
        var up = f.Cross(side);
        float k = len / 0.152f;
        // the sole's share of the shoe's height: a platform's grows up into the boot, the ground stays put
        float sole = Mathf.Clamp(0.22f * look.Platform, 0.1f, 0.6f);

        // (along, centre offset up, half width, half height): the sole stays on the standing rig's ground line
        ReadOnlySpan<Vector4> stations =
        [
            new(-0.048f, -0.050f, 0.043f, 0.050f),
            new(0.000f, -0.026f, 0.049f, 0.060f),
            new(0.070f, -0.021f, 0.051f, 0.045f),
            new(0.130f, -0.020f, 0.050f, 0.028f),
            new(0.172f, -0.018f, 0.036f, 0.017f),
        ];
        var pool = Rings;
        pool.Begin();
        for (int i = 0; i < stations.Length; i++)
        {
            var st = stations[i];
            float w = st.Z, h = st.W, welt = -h + sole * 2f * h;
            var c = ankle + f * st.X * k + up * st.Y;
            ReadOnlySpan<Vector2> ring =
            [
                new(w, 0.45f * h), new(0.55f * w, h), new(-0.55f * w, h), new(-w, 0.45f * h),
                new(-w, welt), new(-0.95f * w, -h), new(0.95f * w, -h), new(w, welt),
            ];
            var section = pool.Ring(ring.Length);
            for (int j = 0; j < ring.Length; j++) section[j] = c + side * ring[j].X + up * ring[j].Y;
            pool.Sections.Add(section);
        }
        var colours = pool.Fill(look.Shoes);
        colours[4] = colours[5] = colours[6] = look.Sole;
        s.Loft(pool.Sections, colours, look.Shoes);
    }

    /// <summary>
    /// The trunk as rings along the spine, crotch (0) to neck (4): wide at the shoulders and
    /// hips, in at the waist, front and back depths of their own so a chest and a seat read.
    /// </summary>
    private readonly struct Torso
    {
        private readonly Vector3 _crotch, _hip, _waist, _chest, _neck, _hipSide, _shoulderSide;
        private readonly Physique _b;
        private const int Sides = 10;

        // (spine position, half width, front depth, back depth, forward offset)
        private Vector4 Key(int i, out float s) => i switch
        {
            0 => Key(0f, _b.Crotch, 0.062f, _b.HipBack * 0.78f, 0f, out s),
            1 => Key(1f, _b.Hip, 0.080f, _b.HipBack, -0.006f, out s),
            2 => Key(2f, _b.Waist, 0.072f, 0.070f, 0f, out s),
            3 => Key(2.5f, _b.UnderBust, 0.077f, 0.074f, 0f, out s),
            4 => Key(3f, _b.Chest, _b.ChestFront, _b.ChestBack, 0f, out s),
            5 => Key(3.55f, _b.Shoulder, 0.064f, 0.072f, -0.004f, out s),
            _ => Key(4f, _b.Neck + 0.026f, _b.Neck + 0.006f, _b.Neck + 0.012f, 0f, out s),
        };

        private const int Keys = 7;

        private static Vector4 Key(float at, float w, float df, float db, float dz, out float s)
        {
            s = at;
            return new Vector4(w, df, db, dz);
        }

        public Torso(Rig r, Physique b)
        {
            _hip = r.Hip; _waist = r.Waist; _chest = r.Chest; _neck = r.Neck;
            _crotch = r.Hip - (r.Waist - r.Hip).Normalized() * 0.085f;
            _hipSide = (r.HipR - r.HipL).Normalized();
            _shoulderSide = (r.ShoulderR - r.ShoulderL).Normalized();
            _b = b;
        }

        /// <summary>The spine at <paramref name="s"/>.</summary>
        public Vector3 At(float s)
        {
            s = Mathf.Clamp(s, 0f, 4f);
            int i = Mathf.Min((int)s, 3);
            float f = s - i;
            Vector3 a = i switch { 0 => _crotch, 1 => _hip, 2 => _waist, _ => _chest };
            Vector3 b = i switch { 0 => _hip, 1 => _waist, 2 => _chest, _ => _neck };
            return a.Lerp(b, f);
        }

        public Vector3 Up(float s) => (At(s + 0.25f) - At(s - 0.25f)).Normalized();

        /// <summary>The trunk's frame at <paramref name="s"/>: side, up and forward.</summary>
        public (Vector3 Side, Vector3 Up, Vector3 Fwd) Frame(float s)
        {
            var up = Up(s);
            var side = _hipSide.Lerp(_shoulderSide, Mathf.Clamp(s / 4f, 0f, 1f));
            side = (side - up * side.Dot(up)).Normalized();
            return (side, up, side.Cross(up));
        }

        /// <summary>(half width, front depth, back depth, forward offset) at <paramref name="s"/>.</summary>
        public Vector4 Profile(float s)
        {
            Key(0, out float prev);
            var last = Key(0, out _);
            for (int i = 1; i < Keys; i++)
            {
                var next = Key(i, out float at);
                if (s <= at) return last.Lerp(next, Mathf.Clamp((s - prev) / (at - prev), 0f, 1f));
                last = next;
                prev = at;
            }
            return last;
        }

        /// <summary>
        /// A point on the trunk's surface at spine <paramref name="s"/> and angle <paramref name="angle"/>
        /// round it (radians; π/2 the front, 0 the figure's left side), lifted <paramref name="proud"/>.
        /// On the flat facets, as drawn, so what is laid on it sits flush.
        /// </summary>
        public Vector3 Surface(float s, float angle, float proud = 0f)
        {
            float t = Mathf.PosMod(angle, Mathf.Tau) / Mathf.Tau * Sides;
            int k = (int)t;
            var a = Vertex(s, k, 0f);
            var b = Vertex(s, k + 1, 0f);
            var p = a.Lerp(b, t - k);
            var (side, up, fwd) = Frame(s);
            // out of the facet: square to its edge and to the spine
            var edge = b - a;
            var normal = up.Cross(edge).Normalized();
            if (normal.Dot(p - Centre(s)) < 0) normal = -normal;
            return p + normal * proud;
        }

        /// <summary>The front of the trunk at <paramref name="s"/>, <paramref name="proud"/> off it.</summary>
        public Vector3 Front(float s, float proud = 0f) => Surface(s, Mathf.Pi / 2f, proud);

        /// <summary>The trunk's half width at <paramref name="s"/>.</summary>
        public float Width(float s) => Profile(s).X;

        private Vector3 Centre(float s)
        {
            var p = Profile(s);
            return At(s) + Frame(s).Fwd * p.W;
        }

        private Vector3 Vertex(float s, int k, float inflate)
        {
            var p = Profile(s);
            var (side, _, fwd) = Frame(s);
            var centre = At(s) + fwd * p.W;
            float a = Mathf.Tau * k / Sides, c = Mathf.Cos(a), sn = Mathf.Sin(a);
            float z = sn > 0 ? (p.Y + inflate) * sn : (p.Z + inflate) * sn;
            return centre + side * (p.X + inflate) * c + fwd * z;
        }

        private Vector3[] Ring(RingPool pool, float s, float inflate)
        {
            var ring = pool.Ring(Sides);
            for (int k = 0; k < Sides; k++) ring[k] = Vertex(s, k, inflate);
            return ring;
        }

        /// <summary>The trunk from spine position <paramref name="s0"/> to <paramref name="s1"/> in one colour.</summary>
        public void Band(MeshScratch s, float s0, float s1, Color colour, float inflate = 0f)
        {
            s0 = Mathf.Clamp(s0, 0f, 4f);
            s1 = Mathf.Clamp(s1, 0f, 4f);
            if (s1 - s0 < 0.01f) return;
            var pool = Rings;
            pool.Begin();
            pool.Sections.Add(Ring(pool, s0, inflate));
            for (int i = 0; i < Keys; i++)
            {
                Key(i, out float at);
                if (at > s0 + 0.01f && at < s1 - 0.01f) pool.Sections.Add(Ring(pool, at, inflate));
            }
            pool.Sections.Add(Ring(pool, s1, inflate));
            s.Loft(pool.Sections, pool.Fill(colour), colour);
        }

        /// <summary>Alternating bands of <paramref name="colour"/> over the trunk from <paramref name="s0"/> to <paramref name="s1"/>, every other one of <paramref name="count"/>, a few mm proud.</summary>
        public void Stripes(MeshScratch s, float s0, float s1, int count, Color colour)
        {
            for (int i = 1; i < count; i += 2)
                Band(s, Mathf.Lerp(s0, s1, (float)i / count), Mathf.Lerp(s0, s1, (float)(i + 1) / count), colour, 0.004f);
        }
    }

    /// <summary>
    /// A head in the anime proportions of the references: a wide cranium over a narrower jaw,
    /// built as rings up the head's axis, with a flat underside to the jaw that overhangs the neck;
    /// a pixel face over its front. <c>jaw</c> 0 is a soft, pointed chin, 1 a square one.
    /// </summary>
    private readonly struct Head
    {
        private readonly Vector3 _base;
        public readonly Vector3 Side, UpAxis, Fwd;
        /// <summary>Metres of the head per metre of the profile tables below.</summary>
        public readonly float K;
        private readonly float _jaw;
        private const int Sides = 12;

        // (half width, front depth, back depth, forward offset) at each height, metres at scale 1
        // #671: round rather than an egg, a full jaw and a short, blunt chin, the crown a dome
        private static readonly float[] Heights = { -0.022f, -0.008f, 0.012f, 0.050f, 0.105f, 0.160f, 0.212f, 0.238f, 0.250f };
        private static readonly Vector4[] Soft =
        {
            new(0.034f, 0.024f, 0.020f, 0.044f), new(0.060f, 0.052f, 0.036f, 0.026f),
            new(0.072f, 0.068f, 0.060f, 0.014f), new(0.081f, 0.078f, 0.079f, 0.006f),
            new(0.085f, 0.085f, 0.091f, 0f), new(0.086f, 0.082f, 0.097f, -0.006f),
            new(0.076f, 0.068f, 0.090f, -0.010f), new(0.058f, 0.050f, 0.072f, -0.010f),
            new(0.030f, 0.026f, 0.040f, -0.010f),
        };
        private static readonly Vector4[] Square =
        {
            new(0.046f, 0.024f, 0.022f, 0.042f), new(0.068f, 0.052f, 0.040f, 0.024f),
            new(0.078f, 0.068f, 0.064f, 0.013f), new(0.083f, 0.078f, 0.081f, 0.006f),
            new(0.085f, 0.085f, 0.091f, 0f), new(0.086f, 0.082f, 0.097f, -0.006f),
            new(0.076f, 0.068f, 0.090f, -0.010f), new(0.058f, 0.050f, 0.072f, -0.010f),
            new(0.030f, 0.026f, 0.040f, -0.010f),
        };

        /// <summary>The height of the top of the scalp, and of the hair cap over it (profile metres).</summary>
        public const float Crown = 0.250f, HairCrown = 0.276f;

        public Head(Rig r, Vector3 shoulderSide, float scale, float jaw)
        {
            var axis = r.HeadTop - r.HeadBase;
            UpAxis = axis.LengthSquared() > 1e-8f ? axis.Normalized() : Vector3.Up;
            Side = (shoulderSide - UpAxis * shoulderSide.Dot(UpAxis)).Normalized();
            Fwd = Side.Cross(UpAxis);
            _base = r.HeadBase;
            K = scale * axis.Length() / 0.19f;
            _jaw = jaw;
        }

        /// <summary>Where the neck goes into the skull: up behind the jaw.</summary>
        public Vector3 NeckTop => _base + (UpAxis * 0.045f - Fwd * 0.012f) * K;

        public Basis Basis => new(Side, UpAxis, Fwd);

        /// <summary>(half width, front depth, back depth, forward offset) at height <paramref name="y"/>.</summary>
        public Vector4 Profile(float y)
        {
            if (y <= Heights[0]) return Soft[0].Lerp(Square[0], _jaw);
            for (int i = 1; i < Heights.Length; i++)
                if (y <= Heights[i])
                {
                    float f = (y - Heights[i - 1]) / (Heights[i] - Heights[i - 1]);
                    return Soft[i - 1].Lerp(Soft[i], f).Lerp(Square[i - 1].Lerp(Square[i], f), _jaw);
                }
            return Soft[^1];
        }

        private Vector3 Vertex(float y, int k, float inflate)
        {
            var p = Profile(y);
            float a = Mathf.Tau * k / Sides, c = Mathf.Cos(a), sn = Mathf.Sin(a);
            float z = sn > 0 ? (p.Y + inflate) * sn : (p.Z + inflate) * sn;
            return _base + (UpAxis * y + Side * (p.X + inflate) * c + Fwd * (z + p.W)) * K;
        }

        /// <summary>A point on the head's surface (its flat facets) at height <paramref name="y"/> and angle <paramref name="angle"/> (radians, π/2 the front), lifted <paramref name="lift"/>.</summary>
        public Vector3 Point(float y, float angle, float lift = 0f)
        {
            float t = Mathf.PosMod(angle, Mathf.Tau) / Mathf.Tau * Sides;
            int k = (int)t;
            var p = Vertex(y, k, 0f).Lerp(Vertex(y, k + 1, 0f), t - k);
            var radial = p - Centre(y);
            radial -= UpAxis * radial.Dot(UpAxis);
            return p + radial.Normalized() * lift * K;
        }

        /// <summary>The head's axis at height <paramref name="y"/>.</summary>
        public Vector3 Centre(float y) => _base + (UpAxis * y + Fwd * Profile(y).W) * K;

        /// <summary>The top of the head, or of the hair on it.</summary>
        public Vector3 Top(bool hair) => _base + UpAxis * (hair ? HairCrown : Crown) * K;

        /// <summary>
        /// Where a hat's band rests: the head is round, so a hat sits down over the crown to just
        /// above the brow (0.178), where the head is as wide as the band, a little higher over hair;
        /// back a little, as the skull is deeper behind than in front.
        /// </summary>
        public Vector3 Seat(bool hair) => Centre(hair ? 0.192f : 0.184f) - Fwd * 0.008f * K;

        /// <summary>The hats' radii (authored for a band of about 0.11 m) on this head.</summary>
        public float HatScale(bool hair) => K * (hair ? 1.06f : 1f);

        /// <summary>Half the head's width at its widest, with hair on or not.</summary>
        public float HalfWidth(bool hair) => (0.086f + (hair ? 0.013f : 0f)) * K;

        private Vector3[] Ring(RingPool pool, float y, float inflate)
        {
            var ring = pool.Ring(Sides);
            for (int k = 0; k < Sides; k++) ring[k] = Vertex(y, k, inflate);
            return ring;
        }

        public void Draw(MeshScratch s, Color skin)
        {
            var pool = Rings;
            pool.Begin();
            for (int i = 0; i < Heights.Length; i++) pool.Sections.Add(Ring(pool, Heights[i], 0f));
            s.Loft(pool.Sections, pool.Fill(skin), skin);
        }

        /// <summary>The pixel face: a band over the front of the head, from brow to chin, drawn from <paramref name="face"/> by the shader (#657); its vertex colour is the eye colour.</summary>
        public void Face(MeshScratch s, FaceGenome face, Color eyes)
        {
            ReadOnlySpan<float> rows = [0.178f, 0.135f, 0.090f, 0.045f, 0.010f];
            const int columns = 9;
            var pool = Rings;
            pool.Begin();
            for (int r = 0; r < rows.Length; r++)
            {
                var row = pool.Ring(columns);
                for (int i = 0; i < columns; i++)
                    row[i] = Point(rows[r], Mathf.DegToRad(Mathf.Lerp(145f, 35f, i / (columns - 1f))), 0.004f);
                pool.Sections.Add(row);
            }
            var (low, high) = face.Code;
            s.FaceBand(pool.Sections, new Rect2(0f, 0f, 1f, 1f), FaceMark(eyes), Centre(0.10f), new Vector2(low, high));
        }

        /// <summary>Where an ear is (<paramref name="side"/> −1 the figure's right, +1 its left).</summary>
        public Vector3 Ear(float side) => Point(0.095f, side > 0 ? 0f : Mathf.Pi, 0.006f) - Fwd * 0.004f * K;

        public void Ears(MeshScratch s, Color skin)
        {
            foreach (float sgn in stackalloc[] { -1f, 1f })
                s.Box(Ear(sgn), new Vector3(0.016f, 0.048f, 0.030f) * K, skin, Basis);
        }

        private const float Deg = Mathf.Pi / 180f;

        /// <summary>A lock of hair: a four-sided tube tapering to a point, from the scalp out to <paramref name="lift"/> off it.</summary>
        private void Lock(MeshScratch s, Color colour, float y0, float a0, float y1, float a1, float lift, float r) =>
            s.Tube(Point(y0, a0 * Deg, 0.010f), Point(y1, a1 * Deg, lift), r * K, 0.003f * K, colour, 4);

        /// <summary>A spike off the scalp along the head's outward normal there, tilted up by <paramref name="rise"/>.</summary>
        private void Spike(MeshScratch s, Color colour, float y, float a, float length, float rise, float r)
        {
            var root = Point(y, a * Deg, 0.006f);
            var outward = (root - Centre(y)).Normalized();
            s.Tube(root, root + (outward * (1f - rise) + UpAxis * rise).Normalized() * length * K, r * K, 0.003f * K, colour, 4);
        }

        /// <summary>The hair as a cap over the crown <paramref name="inflate"/> off the scalp, from <paramref name="low"/> up.</summary>
        private void Cap(MeshScratch s, Color colour, float inflate = 0.013f, float low = 0.160f)
        {
            var pool = Rings;
            pool.Begin();
            // the hairline slants: high on the brow, down over the ears, low at the nape, so short
            // hair does not sit on the head like a beanie
            var hairline = pool.Ring(Sides);
            for (int k = 0; k < Sides; k++)
            {
                float front = (Mathf.Sin(Mathf.Tau * k / Sides) + 1f) * 0.5f;
                hairline[k] = Vertex(Mathf.Lerp(0.060f, low + 0.012f, front), k, inflate);
            }
            pool.Sections.Add(hairline);
            pool.Sections.Add(Ring(pool, 0.200f, inflate));
            pool.Sections.Add(Ring(pool, 0.238f, inflate));   // over the crown's dome (#671)
            pool.Sections.Add(Ring(pool, Crown, inflate));
            pool.Sections.Add(Ring(pool, Crown + inflate * 2f, inflate - 0.013f));   // the top profile, raised over the scalp
            s.Loft(pool.Sections, pool.Fill(colour), colour);
        }

        /// <summary>Hair hanging round the back and sides down to <paramref name="end"/>, open over the face.</summary>
        private void Fall(MeshScratch s, Color colour, float end, float flare, float gapAngle = 1.05f)
        {
            var top = Centre(0.19f) - Fwd * 0.01f * K;
            var bottom = _base + (UpAxis * end - Fwd * Mathf.Lerp(0.012f, 0.03f, Mathf.Clamp(-end * 5f, 0f, 1f))) * K;
            s.Skirt(top, bottom, 0.100f * K, flare * K, colour, 12, gap: Fwd, gapAngle: gapAngle);
        }

        private enum Fringe { None, Short, Locks, Spiky, Blunt, Swept }

        private void Bangs(MeshScratch s, Color colour, Fringe fringe)
        {
            ReadOnlySpan<float> at = [58f, 74f, 90f, 106f, 122f];
            switch (fringe)
            {
                case Fringe.Short:
                    for (int i = 0; i < at.Length; i++) Lock(s, colour, 0.225f, at[i], 0.185f, at[i] + (i - 2) * 3f, 0.018f, 0.024f);
                    break;
                case Fringe.Locks:
                case Fringe.Spiky:
                    for (int i = 0; i < at.Length; i++)
                        Lock(s, colour, 0.215f, at[i], fringe == Fringe.Spiky ? 0.142f : 0.150f, at[i] + (i - 2) * 4f, 0.020f, 0.028f);
                    break;
                case Fringe.Blunt:
                {
                    // cut straight across just above the brows: a band round the front of the head
                    var top = Centre(0.212f);
                    var bottom = Centre(0.150f) + Fwd * 0.004f * K;
                    s.Skirt(top, bottom, 0.097f * K, 0.100f * K, colour, 16, gap: -Fwd, gapAngle: 2.0f);
                    break;
                }
                case Fringe.Swept:
                    // long locks all swept to one side, the longest down over one eye
                    for (int i = 0; i < at.Length; i++)
                        Lock(s, colour, 0.222f, at[i] - 8f, Mathf.Lerp(0.165f, 0.085f, i / 4f), at[i] + 22f, 0.022f, 0.030f);
                    break;
            }
        }

        /// <summary>
        /// The hair. Under a hat (<paramref name="cover"/>) only what would show under a brim is
        /// drawn: the fringe and hair falling past it, no spikes, crest, quiff, bun or tails.
        /// </summary>
        public void Hair(MeshScratch s, HairStyle style, Color colour, Color skin, HairCover cover = HairCover.None)
        {
            bool hat = cover == HairCover.Hat;
            switch (style)
            {
                case HairStyle.None:
                    return;

                case HairStyle.Spiky:
                    Cap(s, colour);
                    Bangs(s, colour, Fringe.Spiky);
                    if (hat) break;
                    // spikes off the crown, out and up, longer at the back
                    for (int i = 0; i < 9; i++)
                    {
                        float a = i * 40f + 20f;
                        Spike(s, colour, 0.230f, a, Mathf.Sin(a * Deg) < 0 ? 0.14f : 0.10f, 0.45f, 0.032f);
                    }
                    Lock(s, colour, 0.20f, 15f, 0.08f, 12f, 0.02f, 0.026f);
                    Lock(s, colour, 0.20f, 165f, 0.08f, 168f, 0.02f, 0.026f);
                    break;

                case HairStyle.Bob:
                case HairStyle.Long:
                {
                    bool longHair = style == HairStyle.Long;
                    Cap(s, colour);
                    Bangs(s, colour, Fringe.Locks);
                    Fall(s, colour, longHair ? -0.20f : 0.005f, longHair ? 0.125f : 0.108f);
                    Lock(s, colour, 0.19f, 25f, longHair ? -0.10f : 0.02f, 22f, 0.03f, 0.032f);
                    Lock(s, colour, 0.19f, 155f, longHair ? -0.10f : 0.02f, 158f, 0.03f, 0.032f);
                    break;
                }

                case HairStyle.Ponytail:
                {
                    Cap(s, colour);
                    Bangs(s, colour, Fringe.Locks);
                    Tail(s, colour, Point(0.170f, -90f * Deg, 0.012f), -Fwd, (-UpAxis * 0.9f - Fwd * 0.35f).Normalized(), 0.26f);
                    // long strands in front of the ears
                    Lock(s, colour, 0.19f, 12f, -0.07f, 10f, 0.025f, 0.022f);
                    Lock(s, colour, 0.19f, 168f, -0.07f, 170f, 0.025f, 0.022f);
                    break;
                }

                case HairStyle.Short:
                    Cap(s, colour, 0.009f, 0.150f);
                    Bangs(s, colour, Fringe.Short);
                    break;

                case HairStyle.Quiff:
                    Cap(s, colour, 0.008f, 0.150f);
                    if (hat) break;
                    // the front swept up and forward over the brow, tallest in the middle
                    foreach (float a in stackalloc[] { 58f, 74f, 90f, 106f, 122f })
                    {
                        var root = Point(0.200f, a * Deg, 0.008f);
                        float tall = 1f - Mathf.Abs(a - 90f) / 110f;
                        var tip = Point(0.255f, a * Deg, 0.065f * tall) + UpAxis * 0.05f * tall * K;
                        s.Tube(root, tip, 0.042f * K, 0.014f * K, colour, 5);
                    }
                    break;

                case HairStyle.Shaggy:
                    Cap(s, colour);
                    Bangs(s, colour, Fringe.Locks);
                    // short locks falling all round the sides and the back, over the ears
                    for (int i = 0; i < 9; i++)
                    {
                        float a = 150f + i * 30f;
                        Lock(s, colour, 0.20f, a, 0.075f, a + 6f, 0.026f, 0.030f);
                    }
                    Lock(s, colour, 0.20f, 20f, 0.075f, 18f, 0.026f, 0.030f);
                    break;

                case HairStyle.Mohawk:
                {
                    // the sides shaved: a scalp just darker than the skin, then a crest front to back
                    Cap(s, skin.Lerp(colour, 0.35f), 0.007f, 0.150f);
                    if (hat) break;
                    ReadOnlySpan<Vector2> crest = [new(0.215f, 90f), new(0.245f, 90f), new(0.262f, 90f), new(0.262f, 270f), new(0.245f, 270f), new(0.215f, 270f), new(0.180f, 270f)];
                    foreach (var c in crest) Spike(s, colour, c.X, c.Y, 0.11f, 0.88f, 0.026f);
                    break;
                }

                case HairStyle.BluntBangs:
                    Cap(s, colour);
                    Bangs(s, colour, Fringe.Blunt);
                    Fall(s, colour, -0.22f, 0.118f);
                    // the straight side locks of a hime cut, cut at the jaw
                    foreach (float a in stackalloc[] { 22f, 158f })
                        Lock(s, colour, 0.19f, a, 0.0f, a, 0.028f, 0.034f);
                    break;

                case HairStyle.SideSwept:
                    Cap(s, colour);
                    Bangs(s, colour, Fringe.Swept);
                    Fall(s, colour, 0.0f, 0.110f);
                    break;

                case HairStyle.Twintails:
                    Cap(s, colour);
                    Bangs(s, colour, Fringe.Locks);
                    if (hat) break;
                    foreach (float a in stackalloc[] { 15f, 165f })
                    {
                        var outward = (Point(0.20f, a * Deg) - Centre(0.20f)).Normalized();
                        Tail(s, colour, Point(0.20f, a * Deg, 0.012f), outward, (-UpAxis * 0.95f + outward * 0.10f - Fwd * 0.12f).Normalized(), 0.30f);
                    }
                    break;

                case HairStyle.Bun:
                {
                    Cap(s, colour);
                    Bangs(s, colour, Fringe.Short);
                    if (hat) break;
                    var root = Point(0.225f, -90f * Deg, 0.010f);
                    var dir = (-Fwd * 0.6f + UpAxis * 0.8f).Normalized();
                    s.Tube(root, root + dir * 0.04f * K, 0.050f * K, 0.060f * K, colour, 8);
                    s.Tube(root + dir * 0.04f * K, root + dir * 0.09f * K, 0.060f * K, 0.025f * K, colour, 8);
                    break;
                }
            }
        }

        /// <summary>A tail of hair: a tie at <paramref name="root"/>, out along <paramref name="outward"/>, then hanging along <paramref name="hang"/>.</summary>
        private void Tail(MeshScratch s, Color colour, Vector3 root, Vector3 outward, Vector3 hang, float length)
        {
            var tie = root + outward * 0.025f * K;
            s.Tube(root, tie, 0.022f * K, colour.Darkened(0.4f), 6);
            var bend = tie + (hang * 0.4f + outward * 0.6f).Normalized() * 0.04f * K;
            s.Tube(tie, bend, 0.030f * K, 0.038f * K, colour, 6);
            s.Tube(bend, bend + hang * length * K, 0.038f * K, 0.008f * K, colour, 6);
        }
    }
}
