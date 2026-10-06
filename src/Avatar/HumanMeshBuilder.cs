using Godot;

namespace UnitSport.Avatar;

public enum HumanPose
{
    /// <summary>Upright, arms at the sides. What another player looks like standing still.</summary>
    Standing,

    /// <summary>Mid-stride, opposite arm and leg forward.</summary>
    Running,

    /// <summary>Folded over the bars, hands on the drops, knees up.</summary>
    Cycling,

    /// <summary>Ski stance: knees driven forward, torso at 45°, hands out in front.</summary>
    Tucked,

    /// <summary>
    /// Wingsuit: arms straight out and swept back, legs apart — the suit's wings stretched between
    /// them. Authored upright like every pose; the flyer lays it prone.
    /// </summary>
    Spread,

    /// <summary>Seated in a harness under a canopy: thighs forward, hands up on the brake lines.</summary>
    Hanging,
    /// <summary>On a ladder, facing it (+Z): left hand and left knee up, the right ones low (#359).</summary>
    ClimbLeft,

    /// <summary>The other half of a climbing step: right hand and knee up.</summary>
    ClimbRight,
}

/// <summary>
/// An arm-override layer on top of any body pose: where the hands go while an item is held or in
/// use. Legs and gait are untouched. Derived on every peer from replicated state
/// (<c>HeldItemId</c> + <c>ItemAction</c>), so it is never sent itself. See docs/notes/avatar/item-arm-poses.md.
/// </summary>
public enum ItemArmPose
{
    /// <summary>The gait's own arms.</summary>
    None,
    /// <summary>The item hand forward at the waist, the other arm swinging less.</summary>
    Hold,
    /// <summary>Stock in the shoulder pocket, the other hand forward under the barrel.</summary>
    ShoulderAim,
    /// <summary>Both hands at the face: binoculars, a camera.</summary>
    TwoHandEye,
    /// <summary>The item hand up at the mouth: eating, drinking.</summary>
    Mouth,
    /// <summary>The item hand low and forward: planting something in the ground.</summary>
    Plant,
    /// <summary>Winding up a throw: the item hand cocked back above the shoulder, the other arm pointing ahead (#206).</summary>
    ThrowWindup,
    /// <summary>A throw let go: the item arm whipped through, forward and down, the other arm swung back.</summary>
    ThrowRelease,
}

/// <summary>Colours for one figure. Kept separate so riders can be told apart at distance.</summary>
public sealed record HumanPalette(
    Color Skin,
    Color Jersey,
    Color Shorts,
    Color Shoes,
    Color Helmet)
{
    public static readonly HumanPalette Default = new(
        Skin: new Color(0.86f, 0.70f, 0.57f),
        Jersey: new Color(0.85f, 0.24f, 0.20f),
        Shorts: new Color(0.16f, 0.17f, 0.20f),
        Shoes: new Color(0.92f, 0.92f, 0.90f),
        Helmet: new Color(0.93f, 0.90f, 0.86f));

    /// <summary>
    /// The clothes the figure has on (#251): what is worn replaces the jersey, shorts and shoes it
    /// would otherwise get; empty for everyone who never put anything on, and for every NPC.
    /// </summary>
    public Outfit Outfit { get; init; }

    /// <summary>
    /// The air streaming past the figure, m/s in its author space (+Z forward): riding forward at
    /// v is (0, 0, −v), falling is up. Skirts and robes stream with it and flutter
    /// (<see cref="FigureWind"/> measures it from a node's motion). Zero: they hang still.
    /// </summary>
    public Vector3 Wind { get; init; }

    /// <summary>
    /// Who the figure is (#394): build, face, eyes, hair. <see cref="Skin"/> is its skin tone; set
    /// both with <see cref="With(Avatar.Appearance)"/>.
    /// </summary>
    public Appearance Appearance { get; init; } = Appearance.Default;

    /// <summary>This palette on a figure of <paramref name="appearance"/>, its skin tone included.</summary>
    public HumanPalette With(Appearance appearance) => this with { Appearance = appearance, Skin = appearance.SkinColour };

    /// <summary>
    /// A deterministic jersey colour, so each rider in a race is distinguishable, on the figure the
    /// rider chose (<see cref="Appearance.Register"/>) or else one of its own from the index.
    /// </summary>
    public static HumanPalette ForRider(int index)
    {
        float hue = (index * 0.37f) % 1f;      // golden-ratio stride keeps neighbours apart
        return (Default with
        {
            Jersey = Color.FromHsv(hue, 0.62f, 0.88f),
            Helmet = Color.FromHsv(hue, 0.25f, 0.95f),
        }).With(Appearance.For(index));
    }
}

/// <summary>
/// What a figure wears on its head besides a cycling helmet — the occasions' hats (#18).
/// Replicated as an int (<see cref="Player.FootPlayer.HeadwearId"/>), so append only.
/// </summary>
public enum Headwear
{
    None = 0,
    WitchHat = 1,
    PumpkinHead = 2,
    SantaHat = 3,
    ReindeerAntlers = 4,
}

/// <summary>
/// Beat-driven pose parameters for one frame of a dance (docs/notes/avatar/dance-moves.md).
/// <paramref name="Bar"/> is the absolute 4-beat bar index (8-count moves use <c>Bar % 2</c>);
/// <paramref name="Weight"/> (0..1) eases the dance in and out from the rest pose.
/// <paramref name="PrevMove"/> is the move before (-1 none) and <paramref name="MoveBlend"/> how far
/// the figure has flowed out of it into <paramref name="Move"/> (0..1): a move change is a short
/// crossfade on the beat, not a cut (#261). A move at or past <see cref="HumanMeshBuilder.GroupMoves"/>
/// is one of the crowd moves danced together (<see cref="HumanMeshBuilder.GroupPogo"/>, <see cref="HumanMeshBuilder.GroupJump"/>);
/// one at or past <see cref="HumanMeshBuilder.EmoteMoves"/> is an emote off the wheel (#404), whatever the style.
/// </summary>
public readonly record struct DanceParams(Audio.Cd.MusicStyle Style, int Move, float BeatPhase, float BarPhase, int Bar, float Weight,
    int PrevMove = -1, float MoveBlend = 1f);

/// <summary>
/// A low-poly human, built from tubes and boxes at roughly 1.78 m.
///
/// <para>
/// Deliberately a figure and not a character model: the whole world is flat-shaded, dithered
/// and snapped to a 640×480 grid, so a smooth-skinned mesh would look more out of place here
/// than a blocky one. What matters at this fidelity is silhouette — that you can tell at a
/// glance whether someone is standing, running or on a bike — which is why the pose is a first
/// class parameter and the geometry is not.
/// </para>
///
/// <para>
/// Everything derives from a small set of joint positions, so a new pose is a table of numbers
/// rather than new geometry. Limb radii taper toward the joints, which is what stops a stack of
/// cylinders reading as scaffolding.
/// </para>
/// </summary>
public static partial class HumanMeshBuilder
{
    /// <summary>Joint positions in metres, origin at the feet, +Z forward, +X right.</summary>
    private readonly record struct Rig(
        Vector3 HeadTop, Vector3 HeadBase, Vector3 Neck, Vector3 Chest, Vector3 Waist, Vector3 Hip,
        Vector3 ShoulderL, Vector3 ElbowL, Vector3 WristL,
        Vector3 ShoulderR, Vector3 ElbowR, Vector3 WristR,
        Vector3 HipL, Vector3 KneeL, Vector3 AnkleL, Vector3 ToeL,
        Vector3 HipR, Vector3 KneeR, Vector3 AnkleR, Vector3 ToeR,
        float TorsoLean, Vector3 HandDir = default);

    /// <param name="into">A mesh to rebuild in place (a figure redrawn while it moves keeps one), or null for a new one.</param>
    public static ArrayMesh Build(HumanPalette palette, HumanPose pose = HumanPose.Standing,
        bool includeLegs = true, bool helmet = false, Headwear hat = Headwear.None, ArrayMesh? into = null)
    {
        var scratch = ScratchFor(into);
        Append(scratch, palette, pose, includeLegs, helmet, hat);
        return into == null ? scratch.Build() : scratch.BuildInto(into);
    }

    /// <summary>
    /// Adds the figure to an existing scratch, so a rider and their equipment come out as one
    /// surface and therefore one draw call — which is the whole reason
    /// <see cref="MeshScratch"/> exists.
    /// </summary>
    public static void Append(MeshScratch scratch, HumanPalette palette,
        HumanPose pose = HumanPose.Standing, bool includeLegs = true, bool helmet = false,
        Headwear hat = Headwear.None) =>
        AppendRig(scratch, palette, RigFor(pose), includeLegs, helmet, hat);

    /// <summary>
    /// A figure mid-stride, walking or running depending on <paramref name="speed"/>.
    ///
    /// <para>
    /// One mesh per frame per figure — a few hundred triangles, which is far cheaper than it
    /// sounds and much cheaper than splitting the body into animated nodes. The pose is a table
    /// of joint positions either way; this one is computed rather than looked up.
    /// </para>
    /// </summary>
    /// <param name="phase">Gait cycle position, 0..1. Both feet complete one step each per cycle.</param>
    /// <param name="dance">Beat-driven dance layer over the gait (<see cref="ApplyDance"/>); null = no dance.
    /// The caller passes <c>arm = None</c> while dancing, the dance owns the arms.</param>
    /// <param name="into">A mesh to rebuild in place (an animated figure keeps one), or null for a new one.</param>
    public static ArrayMesh BuildStride(HumanPalette palette, float speed, float phase,
        bool helmet = false, Headwear hat = Headwear.None,
        ItemArmPose arm = ItemArmPose.None, float armBlend = 0f, DanceParams? dance = null, ArrayMesh? into = null,
        VrArms? vr = null)
    {
        var scratch = ScratchFor(into);
        // a skirt with no measured wind still feels the stride's own (#251)
        if (palette.Wind == Vector3.Zero && speed > 0.05f && Flutters(palette.Outfit))
            palette = palette with { Wind = new Vector3(0, 0, -speed) };
        AppendRig(scratch, palette, ApplyVr(ApplyArms(GaitWithDance(speed, phase, dance), arm, armBlend), vr), includeLegs: true, helmet, hat);
        return into == null ? scratch.Build() : scratch.BuildInto(into);
    }

    /// <summary>
    /// A VR player's real hands (#439), each relative to the eyes in the body's frame (x right, y up,
    /// -z ahead, metres): what <see cref="Player.FootPlayer.VrHands"/> carries to every peer.
    /// </summary>
    public readonly record struct VrArms(Vector3 Right, Vector3 Left);

    /// <summary>The eyes in author space, from the head's base: where the real head's hands are measured from.</summary>
    private static readonly Vector3 EyeFromHeadBase = new(0f, 0.1f, 0.08f);

    /// <summary>
    /// Puts the wrists where a VR player's hands really are (#439) and re-solves the elbows; the
    /// item hand (author -X, the figure's right) is the right hand. A hand out of reach is pulled
    /// back along its line to the shoulder.
    /// </summary>
    private static Rig ApplyVr(Rig rig, VrArms? vr)
    {
        if (vr is not { } hands) return rig;
        var eye = rig.HeadBase + EyeFromHeadBase;
        float reach = (UpperArmLength + ForearmLength) * 0.98f;
        var wristL = Reach(rig.ShoulderL, eye + Author(hands.Right), reach);
        var wristR = Reach(rig.ShoulderR, eye + Author(hands.Left), reach);
        var elbowL = Limb.Solve(rig.ShoulderL, wristL, UpperArmLength, ForearmLength, new Vector3(-0.6f, -0.6f, -0.2f));
        var elbowR = Limb.Solve(rig.ShoulderR, wristR, UpperArmLength, ForearmLength, new Vector3(0.6f, -0.6f, -0.2f));
        return rig with
        {
            ElbowL = elbowL, WristL = wristL, ElbowR = elbowR, WristR = wristR,
            HandDir = (wristL - elbowL).Normalized(),
        };
    }

    /// <summary>The body faces -Z, the author space +Z: a half turn about Y.</summary>
    private static Vector3 Author(Vector3 v) => new(-v.X, v.Y, -v.Z);

    private static Vector3 Reach(Vector3 shoulder, Vector3 wrist, float reach) =>
        wrist.DistanceTo(shoulder) > reach ? shoulder + (wrist - shoulder).Normalized() * reach : wrist;

    /// <summary>A fixed pose with the item arm override on top (uncached: the blend changes every frame).</summary>
    public static ArrayMesh BuildPosed(HumanPalette palette, HumanPose pose, ItemArmPose arm, float armBlend,
        Headwear hat = Headwear.None, ArrayMesh? into = null)
    {
        var scratch = ScratchFor(into);
        AppendRig(scratch, palette, ApplyArms(RigFor(pose), arm, armBlend), includeLegs: true, helmet: false, hat);
        return into == null ? scratch.Build() : scratch.BuildInto(into);
    }

    // one scratch per thread for in-place rebuilds, emptied each time: its lists keep their capacity
    [ThreadStatic] private static MeshScratch? _reused;

    private static MeshScratch ScratchFor(ArrayMesh? into)
    {
        if (into == null) return new MeshScratch();
        var scratch = _reused ??= new MeshScratch();
        scratch.Clear();
        return scratch;
    }

    /// <summary>
    /// Replaces the hands' targets for <paramref name="arm"/>, blended by <paramref name="blend"/> (0..1)
    /// from the pose's own arms, and re-solves the elbows. The item hand is the rig's -X one
    /// (the figure's right once the mesh is turned to face -Z). Targets hang off chest, neck and head
    /// so they follow the torso lean; none depends on view pitch (only yaw is replicated).
    /// </summary>
    private static Rig ApplyArms(Rig rig, ItemArmPose arm, float blend)
    {
        blend = Mathf.Clamp(blend, 0f, 1f);
        if (arm == ItemArmPose.None || blend <= 0.001f) return rig;

        const float s = -1f;   // item hand side in author space
        Vector3 item, support, dir;
        Vector3 rest = new(-s * 0.21f, rig.Hip.Y + 0.02f, rig.Hip.Z + 0.02f);   // support arm hangs
        switch (arm)
        {
            case ItemArmPose.ShoulderAim:
                // butt in the shoulder pocket, trigger hand near the cheek, fore-end hand under the barrel
                item = new(s * 0.135f, rig.Neck.Y + 0.05f, rig.Chest.Z + 0.29f);
                support = new(-s * 0.02f, rig.Neck.Y - 0.03f, rig.Chest.Z + 0.50f);
                dir = new Vector3(-s * 0.03f, 0.04f, 1f); break;
            case ItemArmPose.TwoHandEye:
                item = new(s * 0.08f, rig.HeadBase.Y + 0.09f, rig.HeadBase.Z + 0.29f);
                support = new(-s * 0.08f, rig.HeadBase.Y + 0.09f, rig.HeadBase.Z + 0.29f);
                dir = Vector3.Back; break;
            case ItemArmPose.Mouth:
                item = new(s * 0.05f, rig.HeadBase.Y - 0.03f, rig.HeadBase.Z + 0.18f);
                support = Reduce(rig.WristR, rest);
                dir = new Vector3(0f, 0.8f, -0.3f); break;
            case ItemArmPose.Plant:
                // both hands on the pole, which stands upright in front with its foot near the ground (cloth up)
                item = new(s * 0.05f, rig.Hip.Y - 0.22f, rig.Hip.Z + 0.42f);
                support = new(-s * 0.05f, rig.Hip.Y + 0.08f, rig.Hip.Z + 0.42f);
                dir = new Vector3(0f, 1f, 0.12f); break;
            case ItemArmPose.ThrowWindup:
                item = new(s * 0.24f, rig.HeadBase.Y + 0.10f, rig.Chest.Z - 0.24f);
                support = new(-s * 0.10f, rig.Neck.Y - 0.02f, rig.Chest.Z + 0.48f);
                dir = new Vector3(0f, 0.7f, -0.5f); break;
            case ItemArmPose.ThrowRelease:
                item = new(s * 0.02f, rig.Waist.Y + 0.12f, rig.Chest.Z + 0.52f);
                support = new(-s * 0.26f, rig.Hip.Y + 0.02f, rig.Hip.Z - 0.14f);
                dir = new Vector3(0f, -0.4f, 1f); break;
            default:   // Hold
                item = new(s * 0.19f, rig.Waist.Y + 0.05f, rig.Waist.Z + 0.30f);
                support = Reduce(rig.WristR, rest);
                dir = new Vector3(0f, -0.35f, 1f); break;
        }
        Vector3 Reduce(Vector3 gait, Vector3 to) => gait.Lerp(to, 0.5f);

        var wristL = rig.WristL.Lerp(item, blend);
        var wristR = rig.WristR.Lerp(support, blend);
        var elbowL = Limb.Solve(rig.ShoulderL, wristL, UpperArmLength, ForearmLength, new Vector3(-0.6f, -0.6f, -0.2f));
        var elbowR = Limb.Solve(rig.ShoulderR, wristR, UpperArmLength, ForearmLength, new Vector3(0.6f, -0.6f, -0.2f));
        // the item points along the forearm swinging in the gait, along the pose's own direction once blended
        var fore = (rig.WristL - rig.ElbowL).Normalized();
        // head down onto the stock: the crown tips forward a little
        var headTop = arm == ItemArmPose.ShoulderAim ? rig.HeadTop + new Vector3(0, -0.012f, 0.04f) * blend : rig.HeadTop;
        return rig with
        {
            HeadTop = headTop,
            ElbowL = elbowL, WristL = wristL, ElbowR = elbowR, WristR = wristR,
            HandDir = fore.Lerp(dir.Normalized(), blend).Normalized(),
        };
    }

    /// <summary>
    /// Places a camera can be mounted on a moving figure, in the built mesh's own frame.
    ///
    /// <para>
    /// <b>Already flipped to face -Z</b>, like <see cref="MeshScratch.Build"/> output. The rig is
    /// authored facing +Z, so a mount taken straight off it sits on the wrong side of the body and
    /// points backwards - the same half turn that made every avatar ride in reverse. Returning
    /// mounts in mesh space means a caller can use them directly with the node's transform.
    /// </para>
    /// </summary>
    public readonly record struct GaitMounts(
        Vector3 Eye, Vector3 Head, Vector3 Chest, Vector3 Hip,
        Vector3 ShoulderL, Vector3 ShoulderR, Vector3 FootL, Vector3 FootR,
        float Lean, Vector3 HandL, Vector3 HandR, Basis HandBasis = default);

    /// <summary>
    /// The mount points for one instant of the gait.
    ///
    /// <para>
    /// Computed from the same <see cref="GaitRig"/> the mesh is built from, so a head-mounted
    /// camera inherits the real stride bob rather than an approximation of it - which is the whole
    /// difference between a helmet cam and a camera floating near a head.
    /// </para>
    /// </summary>
    public static GaitMounts MountsFor(float speed, float phase,
        ItemArmPose arm = ItemArmPose.None, float armBlend = 0f, DanceParams? dance = null, VrArms? vr = null) =>
        MountsForRig(ApplyVr(ApplyArms(GaitWithDance(speed, phase, dance), arm, armBlend), vr));

    /// <summary>
    /// Mounts for a fixed (non-gait) pose — a cyclist, who does not run, still needs a head to
    /// hang a POV camera off and a foot to frame an ankle shot from. Shares the exact derivation
    /// <see cref="MountsFor"/> uses, so a static pose's camera points are correct by construction
    /// rather than duplicated by hand into whichever caller needed them next.
    /// </summary>
    public static GaitMounts MountsForPose(HumanPose pose, ItemArmPose arm = ItemArmPose.None, float armBlend = 0f) =>
        MountsForRig(ApplyArms(RigFor(pose), arm, armBlend));

    /// <summary>
    /// A seated rider (a motorbike) posed from the machine's three contact points, author space
    /// (+Z forward): <paramref name="seat"/> the seat surface under the pelvis, <paramref name="grip"/>
    /// the right grip (X is mirrored for the left), <paramref name="peg"/> the right footpeg.
    /// </summary>
    /// <remarks><paramref name="fullFace"/> false: no helmet, the bare head (a jetski's rider, #302).</remarks>
    public static void AppendRider(MeshScratch scratch, HumanPalette palette, Vector3 seat, Vector3 grip, Vector3 peg, bool fullFace = true)
    {
        // the full-face helmet is sized to the head it goes round (AppendRig)
        AppendRig(scratch, palette, RiderRig(seat, grip, peg), includeLegs: true, helmet: false, fullFace: fullFace);
    }

    /// <summary>Camera mounts for <see cref="AppendRider"/>'s figure, flipped to face −Z like the mesh.</summary>
    public static GaitMounts MountsForRider(Vector3 seat, Vector3 grip, Vector3 peg) => MountsForRig(RiderRig(seat, grip, peg));

    /// <summary>
    /// The riding position is derived, never placed (see the cycling rig): with the pelvis on the
    /// seat and the hands on the grips, the shoulder is the one point a torso of fixed length and an
    /// arm bent to 88% of its reach can both get to — the upper of the two circle intersections in the
    /// side plane. Low clip-ons far ahead fold the torso down onto the tank; a high wide bar sits it up.
    /// The knees follow from the pegs by the same two-bone solve.
    /// </summary>
    private static Rig RiderRig(Vector3 seat, Vector3 grip, Vector3 peg)
    {
        const float torso = 0.48f, shoulderHalf = 0.175f;
        var hip = seat + new Vector3(0, 0.09f, 0);   // the hip joint rides a pelvis above the seat foam
        float reach = 0.8f * (UpperArmLength + ForearmLength);
        float dx = Mathf.Abs(grip.X) - shoulderHalf;
        float armPlanar = Mathf.Sqrt(Mathf.Max(0.01f, reach * reach - dx * dx));

        // side plane (y, z): circle about the hip (torso) meets circle about the grip (arm)
        var h = new Vector2(hip.Y, hip.Z);
        var g = new Vector2(grip.Y, grip.Z);
        var to = g - h;
        float d = Mathf.Max(to.Length(), 1e-3f);
        Vector2 shoulder;
        if (d >= torso + armPlanar) shoulder = h + to / d * torso;   // out of reach: stretched toward the bars
        else
        {
            float along = (d * d + torso * torso - armPlanar * armPlanar) / (2f * d);
            float across = Mathf.Sqrt(Mathf.Max(0f, torso * torso - along * along));
            var dir = to / d;
            var normal = new Vector2(dir.Y, -dir.X);   // (y, z) turned a quarter: points up when the grip is ahead
            if (normal.X < 0) normal = -normal;
            shoulder = h + dir * along + normal * across;
        }
        var torsoDir = new Vector3(0, shoulder.X - hip.Y, shoulder.Y - hip.Z).Normalized();
        Vector3 Along(float t) => new(0, hip.Y + torsoDir.Y * t, hip.Z + torsoDir.Z * t);

        var neck = Along(torso + 0.08f);
        // the head lifts to look down the road, whatever the back is doing
        var headAxis = (torsoDir * 0.35f + Vector3.Up * 0.65f).Normalized();
        var shoulderL = Along(torso) + new Vector3(-shoulderHalf, 0, 0);
        var shoulderR = Along(torso) + new Vector3(shoulderHalf, 0, 0);
        var wristR = grip with { X = Mathf.Abs(grip.X) };
        var wristL = grip with { X = -Mathf.Abs(grip.X) };
        // elbows out and down, as on any bike
        var elbowL = Limb.Solve(shoulderL, wristL, UpperArmLength, ForearmLength, new Vector3(-0.6f, -0.6f, -0.2f));
        var elbowR = Limb.Solve(shoulderR, wristR, UpperArmLength, ForearmLength, new Vector3(0.6f, -0.6f, -0.2f));

        (Vector3 Hip, Vector3 Knee, Vector3 Ankle, Vector3 Toe) Leg(float side)
        {
            var root = hip + new Vector3(side * 0.09f, 0, 0);
            var ball = peg with { X = side * Mathf.Abs(peg.X) };
            // ball of the foot on the peg: the ankle sits above and behind it
            var ankle = ball + new Vector3(0, 0.06f, -0.08f);
            var knee = Limb.Solve(root, ankle, ThighLength, ShinLength, new Vector3(side * 0.35f, 0.4f, 1f));
            return (root, knee, ankle, ball + new Vector3(0, -0.01f, 0.07f));
        }
        var legL = Leg(-1f);
        var legR = Leg(1f);

        return new Rig(
            HeadTop: neck + headAxis * 0.255f, HeadBase: neck + headAxis * 0.065f, Neck: neck,
            Chest: Along(torso * 0.82f), Waist: Along(torso * 0.33f), Hip: hip,
            ShoulderL: shoulderL, ElbowL: elbowL, WristL: wristL,
            ShoulderR: shoulderR, ElbowR: elbowR, WristR: wristR,
            HipL: legL.Hip, KneeL: legL.Knee, AnkleL: legL.Ankle, ToeL: legL.Toe,
            HipR: legR.Hip, KneeR: legR.Knee, AnkleR: legR.Ankle, ToeR: legR.Toe,
            TorsoLean: 0f);
    }

    // ---- a car's driver (Avatar/CarCabin: the seat, wheel and pedals come from the car) ----

    /// <summary>Hip to shoulders along the back, seated.</summary>
    public const float DriverTorso = 0.48f;
    /// <summary>How far the hands turn with the wheel before they stop following it: past it they hold still and the rim slides through them.</summary>
    public const float MaxGripTurn = 1.75f;

    /// <summary>The back, the neck and the head's axis of a figure seated at <paramref name="hip"/>, reclined <paramref name="recline"/>.</summary>
    private static (Vector3 Back, Vector3 Neck, Vector3 HeadAxis) DriverSpine(Vector3 hip, float recline)
    {
        var back = new Vector3(0, Mathf.Cos(recline), -Mathf.Sin(recline));
        // the head comes up off the backrest to look down the road
        var headAxis = (back * 0.3f + Vector3.Up * 0.7f).Normalized();
        return (back, hip + back * (DriverTorso + 0.08f), headAxis);
    }

    /// <summary>
    /// The eye of a figure seated at <paramref name="hip"/> (author space, +Z forward, not flipped):
    /// the same point <see cref="MountsForDriver"/> gives, without a seat to solve. The seat is
    /// placed from it.
    /// </summary>
    public static Vector3 DriverEye(Vector3 hip, float recline)
    {
        var (_, neck, headAxis) = DriverSpine(hip, recline);
        return neck + headAxis * (0.065f + 0.12f) + new Vector3(0, 0, 0.085f);
    }

    /// <summary>
    /// A driver posed from the car's seat: back on the backrest, hands on the rim at a quarter to
    /// three turned with the wheel (<paramref name="wheelAngle"/>, radians, + = anticlockwise as
    /// the driver sees it), right foot on the throttle or, braking, the brake, left foot resting.
    /// </summary>
    private static Rig DriverRig(DriverSeat seat, float wheelAngle, float throttle, float brake)
    {
        var hip = seat.Hip;
        var (back, neck, headAxis) = DriverSpine(hip, seat.Recline);
        var shoulders = hip + back * DriverTorso;
        const float shoulderHalf = 0.175f;
        var shoulderL = shoulders + new Vector3(-shoulderHalf, 0, 0);
        var shoulderR = shoulders + new Vector3(shoulderHalf, 0, 0);

        var n = seat.WheelAxis;
        var up = (Vector3.Up - n * n.Dot(Vector3.Up)).Normalized();
        var left = n.Cross(up);
        var turn = new Basis(n, Mathf.Clamp(wheelAngle, -seat.MaxGrip, seat.MaxGrip));
        // side -1 is the figure's right (-X), +1 its left; a little above the horizontal diameter
        Vector3 Grip(float side) => seat.WheelCentre + turn * (left * side * 0.96f + up * 0.28f) * seat.WheelRadius + n * 0.025f;
        var wristL = Grip(-1f);
        var wristR = Grip(1f);
        var elbowL = Limb.Solve(shoulderL, wristL, UpperArmLength, ForearmLength, new Vector3(-0.6f, -0.7f, -0.3f));
        var elbowR = Limb.Solve(shoulderR, wristR, UpperArmLength, ForearmLength, new Vector3(0.6f, -0.7f, -0.3f));

        (Vector3 Hip, Vector3 Knee, Vector3 Ankle, Vector3 Toe) Leg(float side, Vector3 ball)
        {
            var root = hip + new Vector3(side * 0.09f, 0, 0);
            // heel down behind the ball of the foot, the knee up and a little out
            var ankle = ball + new Vector3(0, 0.07f, -0.09f);
            var knee = Limb.Solve(root, ankle, ThighLength, ShinLength, new Vector3(side * 0.2f, 1f, 0.3f));
            return (root, knee, ankle, ball + new Vector3(0, 0.01f, 0.05f));
        }
        var pedal = brake > 0.05f ? DriverSeat.Pressed(seat.Brake, brake) : DriverSeat.Pressed(seat.Throttle, throttle);
        var legL = Leg(-1f, pedal);
        var legR = Leg(1f, seat.Rest);

        return new Rig(
            HeadTop: neck + headAxis * 0.255f, HeadBase: neck + headAxis * 0.065f, Neck: neck,
            Chest: hip + back * (DriverTorso * 0.82f), Waist: hip + back * (DriverTorso * 0.33f), Hip: hip,
            ShoulderL: shoulderL, ElbowL: elbowL, WristL: wristL,
            ShoulderR: shoulderR, ElbowR: elbowR, WristR: wristR,
            HipL: legL.Hip, KneeL: legL.Knee, AnkleL: legL.Ankle, ToeL: legL.Toe,
            HipR: legR.Hip, KneeR: legR.Knee, AnkleR: legR.Ankle, ToeR: legR.Toe,
            TorsoLean: 0f);
    }

    /// <summary>
    /// A driver in <paramref name="seat"/> (author space, as the car is built): the body (torso,
    /// arms, legs), the head, or both. A first-person driver sees their own arms but not the
    /// inside of their own head.
    /// </summary>
    public static void AppendDriver(MeshScratch scratch, HumanPalette palette, DriverSeat seat,
        float wheelAngle, float throttle, float brake, bool body = true, bool head = true, Headwear hat = Headwear.None) =>
        AppendRig(scratch, palette, DriverRig(seat, wheelAngle, throttle, brake), includeLegs: true, helmet: false, hat, body, head);

    /// <summary>
    /// A pilot in <paramref name="seat"/> (#421, author space, +Z forward, +X the figure's left): seated as a
    /// driver, each hand and foot reaching for what it holds instead of a wheel and pedals: the hand on
    /// the +X side to <paramref name="handPlusX"/> (the captain's sidestick or yoke), the other to
    /// <paramref name="handMinusX"/> (the thrust levers), the feet's balls on the rudder pedals.
    /// </summary>
    public static void AppendPilot(MeshScratch scratch, HumanPalette palette, DriverSeat seat, Vector3 handPlusX, Vector3 handMinusX,
        Vector3 footPlusX, Vector3 footMinusX, bool body = true, bool head = true, Headwear hat = Headwear.None) =>
        AppendRig(scratch, palette, PilotRig(seat, handPlusX, handMinusX, footPlusX, footMinusX), includeLegs: true, helmet: false, hat, body, head);

    /// <summary>How far a pilot's hands and feet fall short of what they hold, m (0 = they reach): <c>--cockpitcheck</c>.</summary>
    public static (float HandPlusX, float HandMinusX, float FootPlusX, float FootMinusX) PilotReach(DriverSeat seat, Vector3 handPlusX, Vector3 handMinusX, Vector3 footPlusX, Vector3 footMinusX)
    {
        var r = PilotRig(seat, handPlusX, handMinusX, footPlusX, footMinusX);
        static float Short(Vector3 mid, Vector3 end, float lower) => Mathf.Max(0f, (end - mid).Length() - lower);
        return (Short(r.ElbowR, r.WristR, ForearmLength), Short(r.ElbowL, r.WristL, ForearmLength),
            Short(r.KneeR, r.AnkleR, ShinLength), Short(r.KneeL, r.AnkleL, ShinLength));
    }

    private static Rig PilotRig(DriverSeat seat, Vector3 handPlusX, Vector3 handMinusX, Vector3 footPlusX, Vector3 footMinusX)
    {
        var r = DriverRig(seat, 0f, 0f, 0f);
        // the rig's L limbs are on the −X side, its R limbs on +X (as DriverRig's grips)
        var elbowR = Limb.Solve(r.ShoulderR, handPlusX, UpperArmLength, ForearmLength, new Vector3(0.6f, -0.7f, -0.3f));
        var elbowL = Limb.Solve(r.ShoulderL, handMinusX, UpperArmLength, ForearmLength, new Vector3(-0.6f, -0.7f, -0.3f));
        (Vector3 Hip, Vector3 Knee, Vector3 Ankle, Vector3 Toe) Leg(float side, Vector3 ball)
        {
            var root = seat.Hip + new Vector3(side * 0.09f, 0, 0);
            var ankle = ball + new Vector3(0, 0.07f, -0.09f);
            var knee = Limb.Solve(root, ankle, ThighLength, ShinLength, new Vector3(side * 0.2f, 1f, 0.3f));
            return (root, knee, ankle, ball + new Vector3(0, 0.01f, 0.05f));
        }
        var legL = Leg(-1f, footMinusX);
        var legR = Leg(1f, footPlusX);
        return r with
        {
            ElbowR = elbowR, WristR = handPlusX, ElbowL = elbowL, WristL = handMinusX,
            HipL = legL.Hip, KneeL = legL.Knee, AnkleL = legL.Ankle, ToeL = legL.Toe,
            HipR = legR.Hip, KneeR = legR.Knee, AnkleR = legR.Ankle, ToeR = legR.Toe,
        };
    }

    /// <summary>
    /// A seated driver's body (no head) for a wheel angle, throttle and brake quantised to what can
    /// be seen (0.03 rad, eighths) and <see cref="SmoothFigures"/> (#311), from <paramref name="cache"/> or built once into it (#221): a car
    /// or a truck keeps one cache, so a wheel that comes back to straight does not rebuild the figure.
    /// </summary>
    public static ArrayMesh DriverBody(Dictionary<(int Turn, int Throttle, int Brake, bool Smooth), ArrayMesh> cache,
        (int Turn, int Throttle, int Brake, bool Smooth) pose, HumanPalette palette, DriverSeat seat)
    {
        if (cache.TryGetValue(pose, out var mesh)) return mesh;
        // ponytail: a full cache is emptied, not evicted by age; a hard drive of a full lock-to-lock
        // wheel crosses ~500 keys, and an LRU is not worth it for a figure this cheap to rebuild
        if (cache.Count >= 256) cache.Clear();
        var s = new MeshScratch();
        AppendDriver(s, palette, seat, pose.Turn * 0.03f, pose.Throttle / 8f, pose.Brake / 8f, head: false);
        return cache[pose] = s.Build();
    }

    /// <summary>
    /// A seated driver's head alone, for a car or truck rig to show from outside. Built once, and
    /// again when <see cref="SmoothFigures"/> changes (the pose key's <c>Smooth</c>).
    /// </summary>
    public static ArrayMesh DriverHead(HumanPalette palette, DriverSeat seat)
    {
        var s = new MeshScratch();
        AppendDriver(s, palette, seat, 0f, 0f, 0f, body: false);
        return s.Build();
    }

    /// <summary>Camera mounts for <see cref="AppendDriver"/>'s figure at rest, flipped to face -Z like the mesh.</summary>
    public static GaitMounts MountsForDriver(DriverSeat seat) => MountsForRig(DriverRig(seat, 0f, 0f, 0f));

    // ---- joints as plain points, for a ragdoll (#214) ----

    /// <summary>Number of joints in a figure, in <see cref="Joint"/> order.</summary>
    public const int JointCount = 20;

    /// <summary>The figure's joints, in the order <see cref="DriverJoints"/> and <see cref="BuildJoints"/> use.</summary>
    public enum Joint
    {
        HeadTop, HeadBase, Neck, Chest, Waist, Hip,
        ShoulderL, ElbowL, WristL, ShoulderR, ElbowR, WristR,
        HipL, KneeL, AnkleL, ToeL, HipR, KneeR, AnkleR, ToeR,
    }

    private static Vector3[] JointsOf(Rig r) => new[]
    {
        r.HeadTop, r.HeadBase, r.Neck, r.Chest, r.Waist, r.Hip,
        r.ShoulderL, r.ElbowL, r.WristL, r.ShoulderR, r.ElbowR, r.WristR,
        r.HipL, r.KneeL, r.AnkleL, r.ToeL, r.HipR, r.KneeR, r.AnkleR, r.ToeR,
    };

    /// <summary>The seated driver's joints, author space (+Z forward, as the car is built).</summary>
    public static Vector3[] DriverJoints(DriverSeat seat) => JointsOf(DriverRig(seat, 0f, 0f, 0f));

    /// <summary>A fixed pose's joints, author space (+Z forward, origin at the feet).</summary>
    public static Vector3[] PoseJoints(HumanPose pose) => JointsOf(RigFor(pose));

    /// <summary>
    /// A figure drawn from free joint points (a ragdoll): <paramref name="joints"/> in
    /// <see cref="Joint"/> order, author space. Bone lengths are whatever the points say.
    /// </summary>
    public static ArrayMesh BuildJoints(HumanPalette palette, ReadOnlySpan<Vector3> joints, Headwear hat = Headwear.None)
    {
        var j = joints;
        var rig = new Rig(j[0], j[1], j[2], j[3], j[4], j[5], j[6], j[7], j[8], j[9], j[10], j[11],
            j[12], j[13], j[14], j[15], j[16], j[17], j[18], j[19], TorsoLean: 0f);
        var scratch = new MeshScratch();
        AppendRig(scratch, palette, rig, includeLegs: true, helmet: false, hat);
        return scratch.Build();
    }

    /// <summary>
    /// How far short each limb falls of what it holds, m: the right and left hand of the rim, the
    /// right foot of the throttle and the left of its rest. Zero when it reaches (the two-bone
    /// solve keeps the upper bone's length, so a target out of reach stretches the lower one).
    /// <c>--cockpitcheck</c> fails a seat where any does not.
    /// </summary>
    public static (float HandR, float HandL, float FootR, float FootL) DriverReach(DriverSeat seat, float wheelAngle)
    {
        var rig = DriverRig(seat, wheelAngle, 0f, 0f);
        static float Short(Vector3 mid, Vector3 end, float lower) => Mathf.Max(0f, (end - mid).Length() - lower);
        return (Short(rig.ElbowL, rig.WristL, ForearmLength), Short(rig.ElbowR, rig.WristR, ForearmLength),
            Short(rig.KneeL, rig.AnkleL, ShinLength), Short(rig.KneeR, rig.AnkleR, ShinLength));
    }

    private static GaitMounts MountsForRig(Rig rig)
    {
        // the eye sits high in the head and forward of its centre, along the head's own axis
        var headAxis = (rig.HeadTop - rig.HeadBase).Normalized();
        var head = rig.HeadBase.Lerp(rig.HeadTop, 0.5f);
        var eye = rig.HeadBase + headAxis * 0.12f + new Vector3(0, 0, 0.085f);

        return new GaitMounts(
            Eye: Flip(eye),
            Head: Flip(head),
            Chest: Flip(rig.Chest),
            Hip: Flip(rig.Hip),
            ShoulderL: Flip(rig.ShoulderL),
            ShoulderR: Flip(rig.ShoulderR),
            FootL: Flip(rig.AnkleL),
            FootR: Flip(rig.AnkleR),
            Lean: rig.TorsoLean,
            HandL: Flip(rig.WristL),
            HandR: Flip(rig.WristR),
            HandBasis: HandBasisOf(rig));

        // the item hand's frame in mesh space: -Z along the item, from the pose's direction or the forearm
        static Basis HandBasisOf(Rig r)
        {
            var d = r.HandDir.LengthSquared() > 1e-6f ? r.HandDir : r.WristL - r.ElbowL;
            d = Flip(d.Normalized());
            var up = Mathf.Abs(d.Y) > 0.97f ? Vector3.Forward : Vector3.Up;
            return Basis.LookingAt(d, up);
        }

        static Vector3 Flip(Vector3 v) => new(-v.X, v.Y, -v.Z);
    }

    /// <summary>
    /// Whether figures are built for a lit style (<see cref="Styles.MeshDetail.High"/>, #311):
    /// rounder tubes, a rounded head and hands, and normals for the cel light and the rim. Read at
    /// each build, so a figure cached by its pose keys on it too (<c>FootPlayer.FootPoseKey</c>,
    /// <see cref="DriverBody"/>, <c>Cyclist</c>'s legs).
    /// </summary>
    public static bool SmoothFigures => Styles.StyleKit.Detail == Styles.MeshDetail.High;

    /// <summary>
    /// A hat on the head, built in the head's own frame so it follows the neck like the helmet
    /// does. The figure is authored facing +Z, so "forward" is +Z made square to the head.
    /// </summary>
    internal static void AppendHat(MeshScratch s, Headwear hat, Vector3 centre, Vector3 axis)
    {
        var up = axis.LengthSquared() > 1e-8f ? axis.Normalized() : Vector3.Up;
        var fwd = Vector3.Back - up * up.Dot(Vector3.Back);
        fwd = fwd.LengthSquared() > 1e-6f ? fwd.Normalized() : Vector3.Back;
        var seat = centre + up * (axis.Length() * 0.5f + 0.0275f);
        AppendHat(s, hat, seat, seat - up * 0.1225f, up.Cross(fwd), up, fwd, 1f);
    }

    /// <summary>
    /// A hat whose band rests at <paramref name="top"/> (on the round head, a little above the
    /// brow, where the head is as wide as the band), in the head's frame; <paramref name="centre"/>
    /// is the middle of the head, for what goes round all of it; <paramref name="scale"/> scales the
    /// hat's radii to the head (#394).
    /// </summary>
    internal static void AppendHat(MeshScratch s, Headwear hat, Vector3 top, Vector3 centre, Vector3 side, Vector3 up, Vector3 fwd, float scale)
    {
        var frame = new Basis(side, up, fwd);
        float k = scale;

        switch (hat)
        {
            case Headwear.WitchHat:
            {
                var black = new Color(0.10f, 0.08f, 0.12f);
                s.Tube(top - up * 0.005f, top + up * 0.012f, 0.21f * k, 0.20f * k, black, 10);   // brim
                s.Tube(top, top + up * 0.05f, 0.105f * k, 0.095f * k, new Color(0.45f, 0.20f, 0.60f), 8);
                var knee = top + up * 0.20f - fwd * 0.02f;
                s.Tube(top + up * 0.05f, knee, 0.095f * k, 0.05f * k, black, 8);
                s.Tube(knee, knee + up * 0.08f - fwd * 0.10f, 0.05f * k, 0.006f, black, 6);   // the tip, bent back
                break;
            }
            case Headwear.PumpkinHead:
            {
                // round the whole head (the figure draws no face or hair under it)
                var orange = new Color(0.93f, 0.44f, 0.07f);
                var c = centre + up * 0.01f * k;
                s.Tube(c - up * 0.13f * k, c, 0.12f * k, 0.155f * k, orange, 8);
                s.Tube(c, c + up * 0.14f * k, 0.155f * k, 0.10f * k, orange, 8);
                var glow = new Color(1f, 0.85f, 0.25f);
                s.Box(c + (up * 0.04f + fwd * 0.145f + side * 0.055f) * k, new Vector3(0.05f, 0.04f, 0.03f) * k, glow, frame);
                s.Box(c + (up * 0.04f + fwd * 0.145f - side * 0.055f) * k, new Vector3(0.05f, 0.04f, 0.03f) * k, glow, frame);
                s.Box(c + (-up * 0.045f + fwd * 0.145f) * k, new Vector3(0.12f, 0.03f, 0.03f) * k, glow, frame);
                s.Tube(c + up * 0.13f * k, c + up * 0.20f * k, 0.02f, 0.014f, new Color(0.28f, 0.26f, 0.10f), 5);
                break;
            }
            case Headwear.SantaHat:
            {
                var red = new Color(0.80f, 0.10f, 0.12f);
                var white = new Color(0.95f, 0.95f, 0.93f);
                s.Tube(top - up * 0.03f, top + up * 0.02f, 0.11f * k, 0.11f * k, white, 10);   // fur trim
                var bend = top + up * 0.15f - fwd * 0.04f;
                s.Tube(top + up * 0.02f, bend, 0.10f * k, 0.05f, red, 8);
                var tip = bend - fwd * 0.11f - up * 0.05f;                             // flops over the back
                s.Tube(bend, tip, 0.05f, 0.012f, red, 6);
                s.Box(tip, new Vector3(0.055f, 0.055f, 0.055f), white, frame);
                break;
            }
            case Headwear.ReindeerAntlers:
            {
                var antler = new Color(0.45f, 0.30f, 0.16f);
                foreach (float sgn in stackalloc[] { -1f, 1f })
                {
                    var root = top - up * 0.02f + side * sgn * 0.055f * k;
                    var fork = root + up * 0.12f + side * sgn * 0.06f;
                    s.Tube(root, fork, 0.016f, 0.012f, antler, 5);
                    s.Tube(fork, fork + up * 0.08f + side * sgn * 0.04f - fwd * 0.02f, 0.012f, 0.006f, antler, 5);
                    s.Tube(fork, fork + up * 0.05f + fwd * 0.06f, 0.011f, 0.005f, antler, 5);
                    s.Tube(root + (fork - root) * 0.45f, root + (fork - root) * 0.45f + fwd * 0.06f + up * 0.03f, 0.010f, 0.005f, antler, 4);
                }
                // the red nose, on the face's nose (0.07 up the head, at its front)
                s.Box(centre + fwd * 0.092f * k - up * 0.035f * k, new Vector3(0.04f, 0.04f, 0.035f), new Color(0.90f, 0.08f, 0.08f), frame);
                break;
            }
        }
    }

    /// <summary>A basis whose Y runs along <paramref name="up"/>, for orienting a box to a bone.</summary>
    private static Basis UprightBasis(Vector3 up)
    {
        if (up.LengthSquared() < 1e-8f) return Basis.Identity;
        up = up.Normalized();

        var reference = Mathf.Abs(up.Dot(Vector3.Right)) > 0.95f ? Vector3.Forward : Vector3.Right;
        var right = reference.Cross(up).Normalized();
        return new Basis(right, up, right.Cross(up));
    }

    // ---- bone lengths, taken from the standing rig so every pose is the same person ----
    private const float ThighLength = 0.435f;
    private const float ShinLength = 0.415f;
    private const float UpperArmLength = 0.270f;
    private const float ForearmLength = 0.245f;

    /// <summary>Ankle height off the ground with the foot flat.</summary>
    private const float AnkleHeight = 0.085f;

    private const float LegReach = ThighLength + ShinLength;

    /// <summary>
    /// The walking and running gait, solved rather than keyframed.
    ///
    /// <para>
    /// One constraint drives all of it: <b>a foot on the ground must travel backwards at exactly
    /// the body's speed.</b> Any other stance sweep and the figure moonwalks — feet skating over
    /// the ground while the body moves at its own rate — which is the single most recognisable
    /// tell of a canned run cycle. So the sweep is not a number to tune; it is
    /// <c>speed × stance time</c>, and stance time falls out of cadence and duty factor.
    /// </para>
    ///
    /// <para>
    /// Everything else follows from real gait measurements. Cadence rises with speed but only
    /// mildly (people mostly lengthen their stride, not quicken it). Duty factor — the share of
    /// the cycle a foot is down — is above 0.5 for a walk, which is why both feet are sometimes
    /// on the ground, and below it for a run, which is what creates the flight phase. Crossing
    /// that 0.5 line <i>is</i> the difference between the two, so there is one gait here and not
    /// two, and it changes over on its own as the figure speeds up.
    /// </para>
    /// </summary>
    /// <summary>
    /// Steps per second at a given speed. Rises only mildly — people cover ground by lengthening
    /// their stride far more than by quickening it, and a figure whose legs whirl faster and
    /// faster is the other classic tell of a faked run.
    /// </summary>
    public static float Cadence(float speed) =>
        Mathf.Clamp(1.55f + 0.31f * Mathf.Max(0f, speed), 1.6f, 3.1f);

    /// <summary>
    /// Advances the gait cycle. A cycle is two steps — one per foot — so it turns at half the
    /// cadence. Driven by time rather than distance so a paused or scrubbed replay behaves.
    /// </summary>
    public static float AdvancePhase(float phase, float speed, float dt) =>
        speed < StandingSpeed ? phase
            : Mathf.PosMod(phase + Cadence(speed) * 0.5f * dt, 1f);

    /// <summary>
    /// Below this the figure is standing, not moving. The cycle stops rather than crawling,
    /// because <see cref="Cadence"/> has a floor — it has to, or a figure inching forward would
    /// take one step per minute — and that floor keeps the legs going when the body does not.
    /// </summary>
    public const float StandingSpeed = 0.25f;

    private static Rig GaitRig(float speed, float phase)
    {
        float v = Mathf.Max(0f, speed);
        // 0 walking, 1 running. The changeover is deliberately quick and sits at about 2 m/s,
        // which is where people really switch — and for the same reason. Blending it slowly
        // leaves a "fast walk" holding a stance duty of 0.5+ at speed, and that demands a
        // longer planted-foot sweep than an actual run does: measured 1.11 m at 2.5 m/s against
        // 0.93 m at 4.6. Walking past this speed is not awkward by accident.
        float run = Mathf.Clamp((v - 1.7f) / 1.0f, 0f, 1f);

        // How much of the gait applies at all. Standing still is not a slow walk: the cadence
        // floor keeps the legs turning over at 1.6 steps/s however slowly the body moves, so
        // without this a stationary runner — stopped at a junction, or a paused replay — marches
        // on the spot. Everything the gait displaces is scaled by it, and at zero the rig
        // collapses to the standing pose it should be.
        float moving = Mathf.Clamp(v / 0.6f, 0f, 1f);

        float cadence = Cadence(v);
        float duty = Mathf.Lerp(0.62f, 0.34f, run);
        float hipY = Mathf.Lerp(0.935f, Mathf.Lerp(0.885f, 0.860f, run), moving);
        float lift = Mathf.Lerp(0.055f, 0.230f, run) * moving;        // swing foot clearance

        // A cycle is two steps, so it lasts 2/cadence, and one foot is down for `duty` of it.
        // The body travels v × that while the foot is planted — which is the no-slip constraint,
        // and the factor of two here is the whole of it: getting it wrong halves every stride.
        float stance = duty * 2f / cadence;

        // The ANKLE does not travel that far, though, and assuming it does is what makes the
        // legs unreachably long. Contact rolls along the foot from heel to toe while the ankle
        // is nearly stationary, so the ankle covers the body's travel minus the length of that
        // roll. Measured on real gait it is 20-odd centimetres walking and less when running,
        // where the strike is further forward on the foot. Without this term the required sweep
        // comes out at roughly twice what a 0.85 m leg can span.
        float footRoll = Mathf.Lerp(0.22f, 0.12f, run);
        float sweep = Mathf.Max(0.05f, v * stance - footRoll) * moving;

        // A runner does not land with the foot far out in front — it lands close to under the
        // body and leaves a long way behind. Walking is near enough symmetric about the hip.
        float strikeBias = Mathf.Lerp(0.02f, 0.22f, run);

        // Toe-off: up on the ball of the foot. This is not decoration — the ankle rising is what
        // buys the leg the reach to stay planted through the end of a long stride.
        float toeOffRise = Mathf.Lerp(0.06f, 0.20f, run);

        // The hips oscillate twice per cycle, once per step — and the PHASE FLIPS between the
        // two gaits. Walking vaults over a straight stance leg, so the hip is highest at
        // midstance; running compresses onto a bent one and rises through the flight phase, so
        // it is lowest there. Using one sign for both makes whichever gait got it wrong look
        // like a torso being wheeled along.
        float bob = Mathf.Lerp(1f, -1f, run) * Mathf.Lerp(0.032f, 0.042f, run) * moving
            * Mathf.Cos(Mathf.Tau * 2f * (phase - duty * 0.5f));
        float hip = hipY + bob;

        // Forward lean, about the hip. Nine degrees at a run, barely any at a walk, upright
        // when stopped — a figure standing still leaning forward looks about to fall over.
        float lean = Mathf.Lerp(0.03f, 0.16f, run) * moving;

        Vector3 Lean(float x, float aboveHip, float forward, float amount)
        {
            float c = Mathf.Cos(amount), s = Mathf.Sin(amount);
            return new Vector3(x, hip + aboveHip * c - forward * s, aboveHip * s + forward * c);
        }

        var neck = Lean(0, 0.590f, 0, lean);
        var shoulderL = Lean(-0.180f, 0.510f, 0, lean);
        var shoulderR = Lean(0.180f, 0.510f, 0, lean);

        // The head keeps looking where it is going rather than at the tarmac, so it carries
        // only part of the torso's lean — rotated back about the neck, not authored separately.
        float headLean = lean * 0.35f;
        Vector3 Head(float aboveNeck)
        {
            float c = Mathf.Cos(headLean), s = Mathf.Sin(headLean);
            return new Vector3(0, neck.Y + aboveNeck * c, neck.Z + aboveNeck * s);
        }

        var legL = Leg(phase, -1);
        var legR = Leg(phase + 0.5f, 1);
        var armL = Arm(shoulderL, -1, legL.Ankle.Z);
        var armR = Arm(shoulderR, 1, legR.Ankle.Z);

        return new Rig(
            HeadTop: Head(0.255f), HeadBase: Head(0.065f), Neck: neck,
            Chest: Lean(0, 0.410f, 0, lean), Waist: Lean(0, 0.155f, 0, lean),
            Hip: new Vector3(0, hip, 0),
            ShoulderL: shoulderL, ElbowL: armL.Elbow, WristL: armL.Wrist,
            ShoulderR: shoulderR, ElbowR: armR.Elbow, WristR: armR.Wrist,
            HipL: legL.Hip, KneeL: legL.Knee, AnkleL: legL.Ankle, ToeL: legL.Toe,
            HipR: legR.Hip, KneeR: legR.Knee, AnkleR: legR.Ankle, ToeR: legR.Toe,
            TorsoLean: lean);

        // --- one leg -----------------------------------------------------------------
        (Vector3 Hip, Vector3 Knee, Vector3 Ankle, Vector3 Toe) Leg(float p, float side)
        {
            p = Mathf.PosMod(p, 1f);
            float x = side * 0.090f;
            var root = new Vector3(x, hip, 0);

            // where the foot is at strike and at toe-off, biased back for a run
            float front = sweep * (0.5f - strikeBias);
            float back = -sweep * (0.5f + strikeBias);

            float forward, ankleY, swing;
            if (p < duty)
            {
                // stance: planted, travelling backwards at exactly the body's own speed
                float s = p / duty;
                swing = 0f;
                forward = Mathf.Lerp(front, back, s);
                // heel up over the last third, rolling onto the toes
                ankleY = AnkleHeight + toeOffRise * Mathf.Max(0f, (s - 0.66f) / 0.34f);
            }
            else
            {
                swing = (p - duty) / (1f - duty);
                forward = Mathf.Lerp(back, front, swing);
                ankleY = AnkleHeight + lift * Mathf.Sin(Mathf.Pi * swing)
                    + toeOffRise * Mathf.Max(0f, 1f - swing * 4f);   // ease down off the toes
            }

            // Clamp to what the leg can genuinely reach at this height rather than shortening
            // the whole stride. Midstance — where the eye actually looks for slip — stays
            // exactly no-slip, and only the extremes give. A sprint still outruns the geometry.
            float span = Mathf.Sqrt(Mathf.Max(0.0025f,
                LegReach * LegReach - (hip - ankleY) * (hip - ankleY))) * 0.99f;
            forward = Mathf.Clamp(forward, -span, span);

            var ankle = new Vector3(x, ankleY, forward);

            // the knee leads: +Z is forward in author space (see MeshScratch.Build)
            var knee = Limb.Solve(root, ankle, ThighLength, ShinLength, new Vector3(0, 0, 1));

            // the toe lifts through the swing, which is what stops a foot ploughing the ground
            var toe = ankle + new Vector3(0, -0.045f + 0.055f * swing, 0.145f);
            return (root, knee, ankle, toe);
        }

        // --- one arm, swinging against its own leg ------------------------------------
        (Vector3 Elbow, Vector3 Wrist) Arm(Vector3 shoulder, float side, float footForward)
        {
            // Opposite the leg on the same side — that counter-rotation is what cancels the
            // torso's yaw, and a figure whose arms swing *with* its legs looks like a puppet.
            //
            // A third of the leg's excursion, and capped. Matching the foot's swing looks like
            // the obvious thing and is badly wrong: the hand would need to travel ±0.6 m from a
            // shoulder with only 0.52 m of arm, so the elbow straightens out and the figure runs
            // with its arms held out like a sleepwalker. A real runner keeps them bent.
            float reach = Mathf.Clamp(-footForward * Mathf.Lerp(0.30f, 0.42f, run),
                -0.26f, 0.26f);
            var wrist = new Vector3(
                shoulder.X + side * 0.035f,
                hip + Mathf.Lerp(0.02f, 0.16f, run),
                reach + Mathf.Lerp(0.02f, 0.10f, run));

            // elbows point back and slightly out, never into the ribs
            var hint = new Vector3(side * 0.35f, -0.25f, -1f);
            return (Limb.Solve(shoulder, wrist, UpperArmLength, ForearmLength, hint), wrist);
        }
    }

    /// <summary>
    /// The three fixed poses, as joint tables. Metres from the ground for a 1.78 m figure.
    /// </summary>
    private static Rig RigFor(HumanPose pose) => pose switch
    {
        HumanPose.Running => new Rig(
            HeadTop: new(0, 1.760f, 0.030f), HeadBase: new(0, 1.570f, 0.020f),
            Neck: new(0, 1.505f, 0.015f), Chest: new(0, 1.330f, 0.010f),
            Waist: new(0, 1.080f, 0), Hip: new(0, 0.960f, 0),
            ShoulderL: new(-0.175f, 1.430f, 0), ElbowL: new(-0.205f, 1.180f, 0.140f),
            WristL: new(-0.170f, 1.055f, -0.075f),
            ShoulderR: new(0.175f, 1.430f, 0), ElbowR: new(0.205f, 1.180f, -0.140f),
            WristR: new(0.170f, 1.055f, 0.140f),
            HipL: new(-0.088f, 0.930f, 0), KneeL: new(-0.095f, 0.520f, 0.230f),
            AnkleL: new(-0.100f, 0.130f, 0.115f), ToeL: new(-0.100f, 0.055f, 0.290f),
            HipR: new(0.088f, 0.930f, 0), KneeR: new(0.095f, 0.500f, -0.180f),
            AnkleR: new(0.100f, 0.190f, -0.330f), ToeR: new(0.100f, 0.230f, -0.480f),
            TorsoLean: -0.12f),

        // Derived from the bike, not eyeballed. The three contact points are fixed — hips on
        // the saddle (0.92 m), hands on the drops (0.885 m, 0.79 m forward) — and the shoulder
        // is then the one place a 0.52 m torso and a 0.58 m arm can both reach, which puts it
        // 0.335 m above the saddle and 0.40 m forward. Hand-placing these instead produced a
        // rider lying horizontally in front of the bars: with the ends pinned, the middle is
        // not a free choice.
        HumanPose.Cycling => new Rig(
            HeadTop: new(0, 1.345f, 0.470f), HeadBase: new(0, 1.245f, 0.375f),
            Neck: new(0, 1.215f, 0.335f), Chest: new(0, 1.130f, 0.245f),
            Waist: new(0, 1.020f, 0.095f), Hip: new(0, 0.920f, -0.055f),
            ShoulderL: new(-0.170f, 1.235f, 0.400f), ElbowL: new(-0.185f, 1.020f, 0.575f),
            WristL: new(-0.190f, 0.885f, 0.780f),
            ShoulderR: new(0.170f, 1.235f, 0.400f), ElbowR: new(0.185f, 1.020f, 0.575f),
            WristR: new(0.190f, 0.885f, 0.780f),
            HipL: new(-0.090f, 0.905f, -0.050f), KneeL: new(-0.100f, 0.690f, 0.240f),
            AnkleL: new(-0.100f, 0.375f, 0.140f), ToeL: new(-0.100f, 0.345f, 0.280f),
            HipR: new(0.090f, 0.905f, -0.050f), KneeR: new(0.100f, 0.560f, 0.155f),
            AnkleR: new(0.100f, 0.240f, 0.020f), ToeR: new(0.100f, 0.220f, 0.160f),
            TorsoLean: 0f),

        // Same discipline as the cycling rig: the torso is laid along a 45° line from the hip
        // and the joints fall where its own length puts them, rather than being placed by eye.
        // Hip 0.72 with the knees driven to 0.30 m ahead of the ankle is the ski stance —
        // shins parallel to the pole line, which is what a boot's forward lean forces.
        HumanPose.Tucked => new Rig(
            HeadTop: new(0, 1.381f, 0.426f), HeadBase: new(0, 1.191f, 0.411f),
            Neck: new(0, 1.116f, 0.376f), Chest: new(0, 0.985f, 0.245f),
            Waist: new(0, 0.845f, 0.105f), Hip: new(0, 0.720f, -0.020f),
            ShoulderL: new(-0.175f, 1.059f, 0.319f), ElbowL: new(-0.200f, 0.800f, 0.400f),
            WristL: new(-0.210f, 0.800f, 0.645f),
            ShoulderR: new(0.175f, 1.059f, 0.319f), ElbowR: new(0.200f, 0.800f, 0.400f),
            WristR: new(0.210f, 0.800f, 0.645f),
            HipL: new(-0.090f, 0.720f, -0.020f), KneeL: new(-0.100f, 0.446f, 0.299f),
            AnkleL: new(-0.110f, 0.100f, 0.100f), ToeL: new(-0.110f, 0.090f, 0.240f),
            HipR: new(0.090f, 0.720f, -0.020f), KneeR: new(0.100f, 0.446f, 0.299f),
            AnkleR: new(0.110f, 0.100f, 0.100f), ToeR: new(0.110f, 0.090f, 0.240f),
            TorsoLean: 0f),

        // bone lengths as the standing rig's, only the joints re-aimed
        HumanPose.Spread => new Rig(
            HeadTop: new(0, 1.780f, 0.02f), HeadBase: new(0, 1.590f, 0.01f),
            Neck: new(0, 1.525f, 0), Chest: new(0, 1.345f, 0),
            Waist: new(0, 1.090f, 0), Hip: new(0, 0.965f, 0),
            ShoulderL: new(-0.180f, 1.445f, 0), ElbowL: new(-0.445f, 1.400f, -0.030f),
            WristL: new(-0.685f, 1.350f, -0.070f),
            ShoulderR: new(0.180f, 1.445f, 0), ElbowR: new(0.445f, 1.400f, -0.030f),
            WristR: new(0.685f, 1.350f, -0.070f),
            HipL: new(-0.090f, 0.935f, 0), KneeL: new(-0.200f, 0.515f, -0.020f),
            AnkleL: new(-0.300f, 0.115f, -0.040f), ToeL: new(-0.310f, -0.020f, -0.060f),
            HipR: new(0.090f, 0.935f, 0), KneeR: new(0.200f, 0.515f, -0.020f),
            AnkleR: new(0.300f, 0.115f, -0.040f), ToeR: new(0.310f, -0.020f, -0.060f),
            TorsoLean: 0f),

        HumanPose.Hanging => new Rig(
            HeadTop: new(0, 1.780f, 0), HeadBase: new(0, 1.590f, 0),
            Neck: new(0, 1.525f, 0), Chest: new(0, 1.345f, 0),
            Waist: new(0, 1.090f, 0), Hip: new(0, 0.965f, 0),
            ShoulderL: new(-0.180f, 1.445f, 0), ElbowL: new(-0.250f, 1.700f, 0.030f),
            WristL: new(-0.240f, 1.945f, 0.020f),
            ShoulderR: new(0.180f, 1.445f, 0), ElbowR: new(0.250f, 1.700f, 0.030f),
            WristR: new(0.240f, 1.945f, 0.020f),
            HipL: new(-0.090f, 0.935f, 0), KneeL: new(-0.100f, 0.900f, 0.430f),
            AnkleL: new(-0.100f, 0.500f, 0.520f), ToeL: new(-0.100f, 0.470f, 0.660f),
            HipR: new(0.090f, 0.935f, 0), KneeR: new(0.100f, 0.900f, 0.430f),
            AnkleR: new(0.100f, 0.500f, 0.520f), ToeR: new(0.100f, 0.470f, 0.660f),
            TorsoLean: 0f),

        // a ladder rung each 0.45 m: one hand up at the next rung, the other at chest height, the
        // knee on the same side raised to the next foothold (#359); bone lengths as the standing rig's
        HumanPose.ClimbLeft => Climb(1f),
        HumanPose.ClimbRight => Climb(-1f),

        _ => new Rig(
            HeadTop: new(0, 1.780f, 0), HeadBase: new(0, 1.590f, 0),
            Neck: new(0, 1.525f, 0), Chest: new(0, 1.345f, 0),
            Waist: new(0, 1.090f, 0), Hip: new(0, 0.965f, 0),
            ShoulderL: new(-0.180f, 1.445f, 0), ElbowL: new(-0.205f, 1.175f, 0.015f),
            WristL: new(-0.215f, 0.930f, 0.030f),
            ShoulderR: new(0.180f, 1.445f, 0), ElbowR: new(0.205f, 1.175f, 0.015f),
            WristR: new(0.215f, 0.930f, 0.030f),
            HipL: new(-0.090f, 0.935f, 0), KneeL: new(-0.095f, 0.500f, 0.010f),
            AnkleL: new(-0.098f, 0.085f, 0), ToeL: new(-0.098f, 0.040f, 0.145f),
            HipR: new(0.090f, 0.935f, 0), KneeR: new(0.095f, 0.500f, 0.010f),
            AnkleR: new(0.098f, 0.085f, 0), ToeR: new(0.098f, 0.040f, 0.145f),
            TorsoLean: 0f),
    };

    /// <summary>A climbing step: <paramref name="side"/> 1 = the left hand and knee up, −1 = the right ones.</summary>
    private static Rig Climb(float side)
    {
        // up = the side whose hand reaches for the next rung; x of a joint on the left is negative
        Vector3 Up(float x, float y, float z, bool left) => new(left ? -x : x, y, z);
        bool l = side > 0;
        return new Rig(
            HeadTop: new(0, 1.775f, 0.060f), HeadBase: new(0, 1.585f, 0.045f),
            Neck: new(0, 1.520f, 0.035f), Chest: new(0, 1.340f, 0.030f),
            Waist: new(0, 1.090f, 0.010f), Hip: new(0, 0.965f, 0),
            ShoulderL: new(-0.180f, 1.445f, 0.030f), ShoulderR: new(0.180f, 1.445f, 0.030f),
            // the reaching arm: elbow out and up, the hand on the rung above the head
            ElbowL: l ? new(-0.200f, 1.700f, 0.150f) : new(-0.240f, 1.250f, 0.180f),
            WristL: l ? new(-0.190f, 1.930f, 0.290f) : new(-0.200f, 1.420f, 0.330f),
            ElbowR: !l ? new(0.200f, 1.700f, 0.150f) : new(0.240f, 1.250f, 0.180f),
            WristR: !l ? new(0.190f, 1.930f, 0.290f) : new(0.200f, 1.420f, 0.330f),
            HipL: new(-0.090f, 0.935f, 0), HipR: new(0.090f, 0.935f, 0),
            // the raised leg on the next foothold, the other straight on the rung below
            KneeL: l ? Up(0.100f, 0.740f, 0.380f, true) : Up(0.095f, 0.500f, 0.060f, true),
            AnkleL: l ? Up(0.100f, 0.340f, 0.300f, true) : Up(0.098f, 0.090f, 0.100f, true),
            ToeL: l ? Up(0.100f, 0.320f, 0.450f, true) : Up(0.098f, 0.050f, 0.250f, true),
            KneeR: !l ? Up(0.100f, 0.740f, 0.380f, false) : Up(0.095f, 0.500f, 0.060f, false),
            AnkleR: !l ? Up(0.100f, 0.340f, 0.300f, false) : Up(0.098f, 0.090f, 0.100f, false),
            ToeR: !l ? Up(0.100f, 0.320f, 0.450f, false) : Up(0.098f, 0.050f, 0.250f, false),
            TorsoLean: 0f);
    }

    /// <summary>
    /// Vertex-coloured, backface-culled, no specular. Matches how the rest of the world is shaded:
    /// the terrain gets its form from flat facets and dither, not from specular highlights. The
    /// visual style shades it its own way (<see cref="Styles.StyleKit.Figure"/>: toon in Cartoon).
    ///
    /// <para>
    /// One shared instance (#221): every caller used to get an identical new one, one per figure,
    /// vehicle and preview. Never modify it; a variant (the soot of a burnt vehicle,
    /// <c>VehicleBody.Char</c>) is a material of its own.
    /// </para>
    /// </summary>
    public static StandardMaterial3D Material() => _material ??= Styles.StyleKit.Figure(new()
    {
        VertexColorUseAsAlbedo = true,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
        SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
        Roughness = 1f,
    });

    private static StandardMaterial3D? _material;
    private static ShaderMaterial? _figureMaterial;

    /// <summary>
    /// <see cref="Material"/> as a shader that also draws the clothes' finishes (rainbow, disco
    /// ball, galaxy…, <c>shaders/body/avatar.gdshaderinc</c>), read from the vertex alpha, and the
    /// procedural faces (#657). One shared instance: what is per figure is in the mesh (the face's
    /// genome) or in instance uniforms on its node (the face's state,
    /// <see cref="Face.FaceAnimator"/>). The visual style swaps its shader
    /// (<see cref="Styles.MaterialRole.Figure"/>).
    /// </summary>
    public static ShaderMaterial FigureMaterial() =>
        _figureMaterial ??= Styles.StyleKit.Material(Styles.MaterialRole.Figure);

    // =====================================================================================
    // Dance layer. The spec (conventions, every move's joint formulas, moving variants) is
    // docs/notes/avatar/dance-moves.md; this is its implementation, written so a frame costs
    // a few hundred float operations and no allocations (it runs per visible figure per frame).
    //
    // Shape: a move fills a DanceCh (the channels: pelvis shift, lean/roll/twist, head, shrugs,
    // arm aims, standing foot targets) for either the standing or the moving variant; Compose
    // turns channels into a full Rig blended against the rest rig by the dance weight; when the
    // figure is between standing and walking both variants are composed and the rigs mixed.
    // =====================================================================================

    /// <summary>How many bars a move lasts before the caller should pick the next one.</summary>
    public const int BarsPerMove = 2;

    private enum DanceMove : byte
    {
        SideStepClap, HipSway, ClapBackbeat, Carlton, Macarena, DiscoPoint, Floss, OrangeJustice,
        GangnamStyle, Headbang, AirGuitar, FistPump, Bounce, ArmWave, RunningMan, TStep, Robot,
        Sprinkler, ShoulderLean, Twerk, Dab, Griddy, Moonwalk, Sway, FolkClap, HandsOnHipsSkip,
        Pogo, JumpTogether,
        // #370: the rat dance, only to the chess type beat
        RatSwing, RatArmPump, RatHeadBob, RatHop,
        // #404: new dances (also in the style tables) and gestures (emote wheel only)
        Ymca, ChickenDance, CabbagePatch, SwimDance, Wave, Cheer, Salute, Shrug,
        // #495: fist fights (HumanMeshBuilder.Fight.cs); keep them last, Channels skips the groove from FightStand on
        FightStand, FightGuardHigh, FightCrouch, FightGuardLow, FightAir, FightHit, FightDazed, FightVictory,
        FightBlockStun, FightJab, FightKick, FightLowJab, FightSweep, FightJumpKick, FightUppercut,
        FightStringKick, FightFinisher,
    }

    /// <summary>
    /// <see cref="DanceParams.Move"/> values from here on are the crowd moves (#261), outside any
    /// style's table: every dancer near the same music switches to one on the same bar.
    /// </summary>
    public const int GroupMoves = 1000;
    /// <summary>Pogo: straight up on every beat, everyone in the air at once.</summary>
    public const int GroupPogo = GroupMoves;
    /// <summary>Three bounces winding up, then one big jump with the arms thrown up, together on beat four.</summary>
    public const int GroupJump = GroupMoves + 1;

    /// <summary>The move table of the note, indexed by <see cref="Audio.Cd.MusicStyle"/> (its numeric order).</summary>
    private static readonly DanceMove[][] DanceTable =
    {
        // Pop
        new[] { DanceMove.SideStepClap, DanceMove.HipSway, DanceMove.ClapBackbeat, DanceMove.Carlton,
            DanceMove.Macarena, DanceMove.DiscoPoint, DanceMove.Floss, DanceMove.OrangeJustice,
            DanceMove.GangnamStyle, DanceMove.Ymca },
        // Rock
        new[] { DanceMove.Headbang, DanceMove.AirGuitar, DanceMove.FistPump, DanceMove.Bounce,
            DanceMove.ClapBackbeat, DanceMove.ArmWave, DanceMove.Pogo },
        // Electronic
        new[] { DanceMove.Bounce, DanceMove.FistPump, DanceMove.RunningMan, DanceMove.TStep,
            DanceMove.Robot, DanceMove.Sprinkler, DanceMove.ArmWave, DanceMove.Pogo },
        // HipHop
        new[] { DanceMove.Bounce, DanceMove.ShoulderLean, DanceMove.Twerk, DanceMove.Dab,
            DanceMove.Griddy, DanceMove.Moonwalk, DanceMove.RunningMan, DanceMove.Floss, DanceMove.CabbagePatch },
        // Chill
        new[] { DanceMove.Sway, DanceMove.HipSway, DanceMove.ArmWave, DanceMove.ClapBackbeat,
            DanceMove.Moonwalk, DanceMove.Bounce, DanceMove.SwimDance },
        // Folk
        new[] { DanceMove.FolkClap, DanceMove.HandsOnHipsSkip, DanceMove.SideStepClap,
            DanceMove.HipSway, DanceMove.ClapBackbeat, DanceMove.Macarena, DanceMove.GangnamStyle,
            DanceMove.ChickenDance },
        // RatDance (#370): the chess type beat, whatever it was analysed as; RatSwing first, the crowd's move
        new[] { DanceMove.RatSwing, DanceMove.RatArmPump, DanceMove.RatHeadBob, DanceMove.RatHop },
    };

    /// <summary>Number of moves a style has; <see cref="DanceParams.Move"/> is taken modulo this.</summary>
    public static int MoveCount(Audio.Cd.MusicStyle style) => DanceTable[DanceStyleIndex(style)].Length;

    private static int DanceStyleIndex(Audio.Cd.MusicStyle style)
    {
        int i = (int)style;
        return (uint)i < (uint)DanceTable.Length ? i : 0;
    }

    // ---- the pipeline ------------------------------------------------------------------

    /// <summary>The rig after the gait, before the item arms: the gait rig plus the beat-driven dance.</summary>
    private static Rig GaitWithDance(float speed, float phase, in DanceParams? dance)
    {
        var rig = GaitRig(speed, phase);
        if (dance is { } d && d.Weight > 0.001f)
            rig = ApplyDance(rig, d, Mathf.Clamp(Mathf.Max(0f, speed) / 0.6f, 0f, 1f));
        return rig;
    }

    /// <summary>
    /// Lays the dance over the gait rig. <paramref name="moving"/> (0 standing, 1 walking) picks the
    /// standing variant (legs dance, pelvis free) or the moving variant (legs keep the gait, upper
    /// body only); in between both are composed and mixed, so a figure that starts walking mid-move
    /// glides from one to the other.
    /// </summary>
    private static Rig ApplyDance(Rig rig, in DanceParams d, float moving)
    {
        if (d.Weight <= 0.001f) return rig;
        var move = Resolve(d.Style, d.Move);
        // the move it is flowing out of, for the crossfade on a change (#261)
        float flow = d.PrevMove >= 0 && d.MoveBlend < 0.999f ? DSm(d.MoveBlend) : 1f;
        var prev = flow < 1f ? Resolve(d.Style, d.PrevMove) : move;
        if (prev == move) flow = 1f;
        var beat = new Beat(d.BarPhase, d.Bar);
        float we = DSm(d.Weight);
        float m = DSm(moving);

        if (m <= 0.001f) return DanceVariant(rig, prev, move, flow, beat, we, false);
        if (m >= 0.999f) return DanceVariant(rig, prev, move, flow, beat, we, true);
        return MixRigs(DanceVariant(rig, prev, move, flow, beat, we, false), DanceVariant(rig, prev, move, flow, beat, we, true), m);
    }

    /// <summary>A move number as the caller has it (a style's table index, or a crowd move) to the move itself.</summary>
    private static DanceMove Resolve(Audio.Cd.MusicStyle style, int index)
    {
        if (index == GroupPogo) return DanceMove.Pogo;
        if (index == GroupJump) return DanceMove.JumpTogether;
        if (index >= FightMoves) return FightResolve(index - FightMoves);
        if (index >= EmoteMoves) return EmoteMove(index - EmoteMoves);
        var table = DanceTable[DanceStyleIndex(style)];
        return table[((index % table.Length) + table.Length) % table.Length];
    }

    private static Rig DanceVariant(in Rig rig, DanceMove prev, DanceMove move, float flow, in Beat t, float we, bool mv)
    {
        var ch = Channels(move, t, mv);
        if (flow < 1f) ch = Mix(Channels(prev, t, mv), ch, flow);
        return Compose(rig, ch, we, mv);
    }

    private static DanceCh Channels(DanceMove move, in Beat t, bool mv)
    {
        var ch = default(DanceCh);
        ch.ArmBlend = 1f;
        Planted(ref ch, 0.098f, 0f);
        EvalMove(move, t, mv, ref ch);
        if (move is not (DanceMove.Pogo or DanceMove.JumpTogether or DanceMove.Salute) && move < DanceMove.FightStand) Groove(ref ch, t, mv);
        return ch;
    }

    /// <summary>
    /// The groove under every move (#261): the knees give a little on each beat, the head nods
    /// into it and the shoulders bounce a beat-fraction later, so even a move that only works the
    /// arms has the whole body in time. Small on purpose: added to what the move already does.
    /// </summary>
    private static void Groove(ref DanceCh ch, in Beat t, bool mv)
    {
        float hit = DDip(t.B);
        float late = DDip(DFrac(t.B - 0.12f));
        float k = mv ? 0.5f : 1f;
        ch.Py -= 0.018f * hit * k;
        ch.ThN += 0.045f * hit * k;
        ch.ShL += 0.010f * late * k;
        ch.ShR += 0.010f * late * k;
        // a sway of the hips over two beats, alternating, under whatever the move does
        ch.Px += 0.012f * t.D * k;
        ch.Phi += 0.02f * t.D * k;
    }

    // ---- beat clock and small helpers ---------------------------------------------------

    /// <summary>The note's count clock: c = 4r (0..4), k the beat index, b the phase inside it, D/E the two-beat alternators.</summary>
    private readonly struct Beat
    {
        public readonly float C, B, R, D, E;
        public readonly int K, Par;

        public Beat(float barPhase, int bar)
        {
            R = barPhase - Mathf.Floor(barPhase);
            C = R * 4f;
            K = Mathf.Clamp((int)C, 0, 3);
            B = C - K;
            D = DSin(C * 0.5f);
            E = DCos(C * 0.5f);
            Par = bar & 1;
        }
    }

    private static float DSin(float turns) => Mathf.Sin(Mathf.Tau * turns);
    private static float DCos(float turns) => Mathf.Cos(Mathf.Tau * turns);
    private static float DSm(float x) { x = Mathf.Clamp(x, 0f, 1f); return x * x * (3f - 2f * x); }
    private static float DRamp(float x) => Mathf.Clamp(x, 0f, 1f);
    private static float DDip(float b) => (1f + DCos(b)) * 0.5f;
    private static float DHop(float b) => DSin(b * 0.5f);
    private static float DSq(float x) => Mathf.Clamp(3f * x, -1f, 1f);
    private static float DFrac(float x) => x - Mathf.Floor(x);
    private static float DCl(float c) { float x = (1f - DCos(c * 0.5f)) * 0.5f; return x * x; }

    private static float DSawNod(float b) => b < 0.75f
        ? Mathf.Lerp(0.60f, -0.25f, DSm(b / 0.75f))
        : Mathf.Lerp(-0.25f, 0.60f, DSm((b - 0.75f) / 0.25f));

    // ---- channels ------------------------------------------------------------------------

    private enum AimKind : byte { Gait, Shoulder, Chain, Knee }

    /// <summary>Where one hand goes: an offset in the torso frame from the shoulder (or from the knee), or an ArmChain.</summary>
    private struct ArmAim
    {
        public AimKind Kind;
        public Vector3 Off, Hint;
        public float A1, G1, A2, G2;
    }

    /// <summary>
    /// Everything one move sets for one instant. Pelvis and torso channels are in the note's terms;
    /// foot fields are absolute ankle targets and toe offsets from the ankle (standing variant only).
    /// </summary>
    private struct DanceCh
    {
        public float Px, Py, Pz, Tilt;
        public float Theta, Phi, Psi, ThN, PhH, ShL, ShR;
        public float ArmBlend;
        public ArmAim ArmL, ArmR;
        public Vector3 AnkleL, AnkleR, ToeL, ToeR;
    }

    private static DanceCh Mix(in DanceCh a, in DanceCh b, float t)
    {
        DanceCh r;
        r.Px = Mathf.Lerp(a.Px, b.Px, t); r.Py = Mathf.Lerp(a.Py, b.Py, t);
        r.Pz = Mathf.Lerp(a.Pz, b.Pz, t); r.Tilt = Mathf.Lerp(a.Tilt, b.Tilt, t);
        r.Theta = Mathf.Lerp(a.Theta, b.Theta, t); r.Phi = Mathf.Lerp(a.Phi, b.Phi, t);
        r.Psi = Mathf.Lerp(a.Psi, b.Psi, t); r.ThN = Mathf.Lerp(a.ThN, b.ThN, t);
        r.PhH = Mathf.Lerp(a.PhH, b.PhH, t); r.ShL = Mathf.Lerp(a.ShL, b.ShL, t);
        r.ShR = Mathf.Lerp(a.ShR, b.ShR, t); r.ArmBlend = Mathf.Lerp(a.ArmBlend, b.ArmBlend, t);
        r.ArmL = MixArm(a.ArmL, b.ArmL, t); r.ArmR = MixArm(a.ArmR, b.ArmR, t);
        r.AnkleL = a.AnkleL.Lerp(b.AnkleL, t); r.AnkleR = a.AnkleR.Lerp(b.AnkleR, t);
        r.ToeL = a.ToeL.Lerp(b.ToeL, t); r.ToeR = a.ToeR.Lerp(b.ToeR, t);
        return r;
    }

    private static ArmAim MixArm(in ArmAim a, in ArmAim b, float t)
    {
        if (a.Kind != b.Kind) return t < 0.5f ? a : b;
        ArmAim r;
        r.Kind = a.Kind;
        r.Off = a.Off.Lerp(b.Off, t); r.Hint = a.Hint.Lerp(b.Hint, t);
        r.A1 = Mathf.Lerp(a.A1, b.A1, t); r.G1 = Mathf.Lerp(a.G1, b.G1, t);
        r.A2 = Mathf.Lerp(a.A2, b.A2, t); r.G2 = Mathf.Lerp(a.G2, b.G2, t);
        return r;
    }

    /// <summary>The moving variant's common rule: torso and pelvis amplitudes scale by k, the arms blend over the gait arms.</summary>
    private static void MovingScale(ref DanceCh ch, float k, float armBlend)
    {
        ch.Px *= k; ch.Py *= k; ch.Pz *= k; ch.Tilt *= k;
        ch.Theta *= k; ch.Phi *= k; ch.Psi *= k; ch.ThN *= k; ch.PhH *= k; ch.ShL *= k; ch.ShR *= k;
        ch.ArmBlend = armBlend;
    }

    // ---- arm aims ------------------------------------------------------------------------

    private static Vector3 HLow(float s) => new(s * 0.6f, -0.6f, -0.2f);
    private static Vector3 HOut(float s) => new(s, 0f, -0.3f);
    private static Vector3 HUp(float s) => new(s * 0.5f, 0.9f, -0.2f);

    /// <summary>The note's o(out, up, fwd): out mirrors per side.</summary>
    private static void Aim(ref DanceCh ch, float s, float outward, float up, float fwd, Vector3 hint) =>
        SetArm(ref ch, s, new Vector3(s * outward, up, fwd), hint);

    /// <summary>The note's O(x, y, z): unmirrored, so both hands shift the same way.</summary>
    private static void AimO(ref DanceCh ch, float s, float x, float y, float z, Vector3 hint) =>
        SetArm(ref ch, s, new Vector3(x, y, z), hint);

    /// <summary>The note's H(x, y, z), anchored at the hip: the same target as a shoulder offset.</summary>
    private static void AimH(ref DanceCh ch, float s, float x, float y, float z, Vector3 hint) =>
        SetArm(ref ch, s, new Vector3(x - s * 0.18f, y - 0.51f, z), hint);

    private static void SetArm(ref DanceCh ch, float s, Vector3 off, Vector3 hint)
    {
        ref ArmAim a = ref (s < 0f ? ref ch.ArmL : ref ch.ArmR);
        a.Kind = AimKind.Shoulder; a.Off = off; a.Hint = hint;
    }

    private static void AimKnee(ref DanceCh ch, float s, Vector3 off, Vector3 hint)
    {
        ref ArmAim a = ref (s < 0f ? ref ch.ArmL : ref ch.ArmR);
        a.Kind = AimKind.Knee; a.Off = off; a.Hint = hint;
    }

    private static void AimChain(ref DanceCh ch, float s, float a1, float g1, float a2, float g2)
    {
        ref ArmAim a = ref (s < 0f ? ref ch.ArmL : ref ch.ArmR);
        a.Kind = AimKind.Chain; a.A1 = a1; a.G1 = g1; a.A2 = a2; a.G2 = g2;
    }

    /// <summary>Hands on the hips, elbows flared.</summary>
    private static void Akimbo(ref DanceCh ch, float s, float upExtra = 0f) =>
        Aim(ref ch, s, 0.05f, -0.40f + upExtra, 0.06f, HOut(s));

    private static readonly Vector3 AkimboOff = new(0.05f, -0.40f, 0.06f);   // x mirrored by the caller

    private static void ClapArms(ref DanceCh ch, float cl)
    {
        Aim(ref ch, -1f, 0.20f - 0.35f * cl, -0.05f - 0.05f * cl, 0.30f, HLow(-1f));
        Aim(ref ch, 1f, 0.20f - 0.35f * cl, -0.05f - 0.05f * cl, 0.30f, HLow(1f));
    }

    // ---- feet ----------------------------------------------------------------------------

    private static void Foot(ref DanceCh ch, float s, float x, float y, float z, float turn, float lift)
    {
        var ankle = new Vector3(x, y, z);
        var toe = new Vector3(s * 0.145f * Mathf.Sin(turn), -0.045f + 0.045f * lift, 0.145f * Mathf.Cos(turn));
        if (s < 0f) { ch.AnkleL = ankle; ch.ToeL = toe; }
        else { ch.AnkleR = ankle; ch.ToeR = toe; }
    }

    /// <summary>Both feet flat at half-stance <paramref name="w"/>, toes turned out by <paramref name="turn"/>.</summary>
    private static void Planted(ref DanceCh ch, float w, float turn = 0.15f)
    {
        Foot(ref ch, -1f, -w, AnkleHeight, 0f, turn, 0f);
        Foot(ref ch, 1f, w, AnkleHeight, 0f, turn, 0f);
    }

    /// <summary>Heel off the floor, ball of the foot stays down (the toe goes to the ground).</summary>
    private static void HeelUp(ref DanceCh ch, float s, float h)
    {
        if (h <= 0f) return;
        ref Vector3 a = ref (s < 0f ? ref ch.AnkleL : ref ch.AnkleR);
        ref Vector3 toe = ref (s < 0f ? ref ch.ToeL : ref ch.ToeR);
        a.Y += h;
        toe = new Vector3(0f, 0.040f - a.Y, 0.13f);
    }

    /// <summary>The whole foot off the ground.</summary>
    private static void Lift(ref DanceCh ch, float s, float dy)
    {
        ref Vector3 a = ref (s < 0f ? ref ch.AnkleL : ref ch.AnkleR);
        a.Y += dy;
    }

    // ---- composition ---------------------------------------------------------------------

    /// <summary>Torso rotation about the hip: pitch, then roll, then yaw scaled per joint (0.25 waist, 0.6 chest, 1 shoulders/neck).</summary>
    private readonly struct Spine
    {
        private readonly float _ct, _st, _cp, _sp, _c0, _s0, _c1, _s1, _c2, _s2;

        public Spine(float theta, float phi, float psi)
        {
            _ct = Mathf.Cos(theta); _st = Mathf.Sin(theta);
            _cp = Mathf.Cos(phi); _sp = Mathf.Sin(phi);
            _c0 = Mathf.Cos(psi * 0.25f); _s0 = Mathf.Sin(psi * 0.25f);
            _c1 = Mathf.Cos(psi * 0.6f); _s1 = Mathf.Sin(psi * 0.6f);
            _c2 = Mathf.Cos(psi); _s2 = Mathf.Sin(psi);
        }

        /// <summary>level 0 waist, 1 chest, 2 shoulders/neck/torso frame.</summary>
        public Vector3 Pos(Vector3 v, int level)
        {
            float y1 = v.Y * _ct - v.Z * _st, z1 = v.Y * _st + v.Z * _ct;
            float x2 = v.X * _cp + y1 * _sp, y2 = -v.X * _sp + y1 * _cp;
            float cy = level == 0 ? _c0 : level == 1 ? _c1 : _c2;
            float sy = level == 0 ? _s0 : level == 1 ? _s1 : _s2;
            return new Vector3(x2 * cy + z1 * sy, y2, -x2 * sy + z1 * cy);
        }

        /// <summary>A direction or offset in the torso frame, to the world axes.</summary>
        public Vector3 Rot(Vector3 v) => Pos(v, 2);
    }

    private static float FitY(Vector3 ankle, Vector3 hip, float s, float tilt)
    {
        float dx = ankle.X - (hip.X + s * 0.09f), dz = ankle.Z - hip.Z;
        float r2 = 0.845f * 0.845f - dx * dx - dz * dz;
        return ankle.Y - s * tilt + Mathf.Sqrt(Mathf.Max(r2, 0.0144f));
    }

    private static Rig Compose(in Rig rig, in DanceCh ch, float we, bool mv)
    {
        Vector3 hip;
        if (mv)
            hip = rig.Hip + new Vector3(
                Mathf.Clamp(ch.Px, -0.04f, 0.04f), Mathf.Clamp(ch.Py, -0.04f, 0.04f), Mathf.Clamp(ch.Pz, -0.04f, 0.04f)) * we;
        else
            hip = rig.Hip.Lerp(new Vector3(ch.Px, 0.935f + ch.Py, ch.Pz), we);
        float tilt = mv ? 0f : ch.Tilt * we;

        Vector3 hipL, kneeL, ankleL, toeL, hipR, kneeR, ankleR, toeR;
        if (mv)
        {
            // legs stay on the gait; the pelvis shift is hidden by the round pelvis tube
            hipL = rig.HipL; kneeL = rig.KneeL; ankleL = rig.AnkleL; toeL = rig.ToeL;
            hipR = rig.HipR; kneeR = rig.KneeR; ankleR = rig.AnkleR; toeR = rig.ToeR;
        }
        else
        {
            ankleL = rig.AnkleL.Lerp(ch.AnkleL, we); toeL = rig.ToeL.Lerp(ch.AnkleL + ch.ToeL, we);
            ankleR = rig.AnkleR.Lerp(ch.AnkleR, we); toeR = rig.ToeR.Lerp(ch.AnkleR + ch.ToeR, we);
            // LegFit: never ask the legs for an ankle they cannot reach
            hip.Y = Mathf.Min(hip.Y, Mathf.Min(FitY(ankleL, hip, -1f, tilt), FitY(ankleR, hip, 1f, tilt)));
            hipL = new Vector3(hip.X - 0.09f, hip.Y - tilt, hip.Z);
            hipR = new Vector3(hip.X + 0.09f, hip.Y + tilt, hip.Z);
            kneeL = Limb.Solve(hipL, ankleL, ThighLength, ShinLength,
                new Vector3(Mathf.Abs(ankleL.X) >= 0.2f ? -0.6f : -0.35f, 0f, 1f));
            kneeR = Limb.Solve(hipR, ankleR, ThighLength, ShinLength,
                new Vector3(Mathf.Abs(ankleR.X) >= 0.2f ? 0.6f : 0.35f, 0f, 1f));
        }

        float theta = mv ? rig.TorsoLean + we * ch.Theta : Mathf.Lerp(rig.TorsoLean, ch.Theta, we);
        var sp = new Spine(theta, we * ch.Phi, we * ch.Psi);
        var waist = hip + sp.Pos(new Vector3(0f, 0.155f, 0f), 0);
        var chest = hip + sp.Pos(new Vector3(0f, 0.410f, 0f), 1);
        var neck = hip + sp.Pos(new Vector3(0f, 0.590f, 0f), 2);
        var shoulderL = hip + sp.Pos(new Vector3(-0.18f, 0.510f, 0f), 2) + new Vector3(0f, we * ch.ShL, 0f);
        var shoulderR = hip + sp.Pos(new Vector3(0.18f, 0.510f, 0f), 2) + new Vector3(0f, we * ch.ShR, 0f);

        float thH = 0.35f * theta + we * ch.ThN, phH = we * ch.PhH;
        var u = new Vector3(Mathf.Sin(phH), Mathf.Cos(phH) * Mathf.Cos(thH), Mathf.Cos(phH) * Mathf.Sin(thH));

        float bl = we * ch.ArmBlend;
        SolveArm(ch.ArmL, -1f, sp, shoulderL, kneeL, rig.ShoulderL, rig.ElbowL, rig.WristL, bl,
            out var elbowL, out var wristL);
        SolveArm(ch.ArmR, 1f, sp, shoulderR, kneeR, rig.ShoulderR, rig.ElbowR, rig.WristR, bl,
            out var elbowR, out var wristR);

        return new Rig(
            HeadTop: neck + u * 0.255f, HeadBase: neck + u * 0.065f, Neck: neck,
            Chest: chest, Waist: waist, Hip: hip,
            ShoulderL: shoulderL, ElbowL: elbowL, WristL: wristL,
            ShoulderR: shoulderR, ElbowR: elbowR, WristR: wristR,
            HipL: hipL, KneeL: kneeL, AnkleL: ankleL, ToeL: toeL,
            HipR: hipR, KneeR: kneeR, AnkleR: ankleR, ToeR: toeR,
            TorsoLean: theta, HandDir: rig.HandDir);
    }

    /// <summary>
    /// Places one arm. The dance target is blended against the gait arm carried along by the moved
    /// shoulder (so the two always have the same length), clamped to what the arm can reach, and the
    /// elbow is re-solved toward the preferred bend, like <see cref="ApplyArms"/>.
    /// </summary>
    private static void SolveArm(in ArmAim aim, float s, in Spine sp, Vector3 shoulder, Vector3 knee,
        Vector3 restShoulder, Vector3 restElbow, Vector3 restWrist, float bl,
        out Vector3 elbow, out Vector3 wrist)
    {
        var restW = shoulder + (restWrist - restShoulder);
        var restE = shoulder + (restElbow - restShoulder);
        if (aim.Kind == AimKind.Gait || bl <= 0.001f) { elbow = restE; wrist = restW; return; }

        Vector3 wd, ed;
        if (aim.Kind == AimKind.Chain)
        {
            float c1 = Mathf.Cos(aim.A1), c2 = Mathf.Cos(aim.A2);
            ed = shoulder + sp.Rot(new Vector3(s * c1 * Mathf.Cos(aim.G1), Mathf.Sin(aim.A1), c1 * Mathf.Sin(aim.G1))) * UpperArmLength;
            wd = ed + sp.Rot(new Vector3(s * c2 * Mathf.Cos(aim.G2), Mathf.Sin(aim.A2), c2 * Mathf.Sin(aim.G2))) * ForearmLength;
        }
        else
        {
            wd = aim.Kind == AimKind.Knee ? knee + aim.Off : shoulder + sp.Rot(aim.Off);
            var to = wd - shoulder;
            float len = to.Length();
            if (len > 0.50f) wd = shoulder + to * (0.50f / len);   // 0.515 m of arm, never ask for all of it
            ed = Limb.Solve(shoulder, wd, UpperArmLength, ForearmLength, sp.Rot(aim.Hint));
        }

        wrist = restW.Lerp(wd, bl);
        elbow = Limb.Solve(shoulder, wrist, UpperArmLength, ForearmLength, restE.Lerp(ed, bl) - shoulder);
    }

    private static Vector3 Mx(Vector3 a, Vector3 b, float t) => a.Lerp(b, t);

    /// <summary>Mixes two composed rigs joint by joint, then re-solves the elbows and knees so the bones keep their length.</summary>
    private static Rig MixRigs(in Rig a, in Rig b, float t)
    {
        var shL = Mx(a.ShoulderL, b.ShoulderL, t); var shR = Mx(a.ShoulderR, b.ShoulderR, t);
        var wrL = Mx(a.WristL, b.WristL, t); var wrR = Mx(a.WristR, b.WristR, t);
        var hipL = Mx(a.HipL, b.HipL, t); var hipR = Mx(a.HipR, b.HipR, t);
        var anL = Mx(a.AnkleL, b.AnkleL, t); var anR = Mx(a.AnkleR, b.AnkleR, t);
        return new Rig(
            HeadTop: Mx(a.HeadTop, b.HeadTop, t), HeadBase: Mx(a.HeadBase, b.HeadBase, t),
            Neck: Mx(a.Neck, b.Neck, t), Chest: Mx(a.Chest, b.Chest, t),
            Waist: Mx(a.Waist, b.Waist, t), Hip: Mx(a.Hip, b.Hip, t),
            ShoulderL: shL, ElbowL: Limb.Solve(shL, wrL, UpperArmLength, ForearmLength, Mx(a.ElbowL, b.ElbowL, t) - shL), WristL: wrL,
            ShoulderR: shR, ElbowR: Limb.Solve(shR, wrR, UpperArmLength, ForearmLength, Mx(a.ElbowR, b.ElbowR, t) - shR), WristR: wrR,
            HipL: hipL, KneeL: Limb.Solve(hipL, anL, ThighLength, ShinLength, Mx(a.KneeL, b.KneeL, t) - hipL),
            AnkleL: anL, ToeL: Mx(a.ToeL, b.ToeL, t),
            HipR: hipR, KneeR: Limb.Solve(hipR, anR, ThighLength, ShinLength, Mx(a.KneeR, b.KneeR, t) - hipR),
            AnkleR: anR, ToeR: Mx(a.ToeR, b.ToeR, t),
            TorsoLean: Mathf.Lerp(a.TorsoLean, b.TorsoLean, t), HandDir: a.HandDir);
    }

    // ---- the moves -----------------------------------------------------------------------

    private static void EvalMove(DanceMove move, in Beat t, bool mv, ref DanceCh ch)
    {
        switch (move)
        {
            case DanceMove.SideStepClap: SideStepClap(ref ch, t, mv); break;
            case DanceMove.HipSway: HipSway(ref ch, t, mv); break;
            case DanceMove.ClapBackbeat: ClapBackbeat(ref ch, t, mv); break;
            case DanceMove.Carlton: Carlton(ref ch, t, mv); break;
            case DanceMove.Macarena: Macarena(ref ch, t, mv); break;
            case DanceMove.DiscoPoint: DiscoPoint(ref ch, t, mv); break;
            case DanceMove.Floss: Floss(ref ch, t, mv); break;
            case DanceMove.OrangeJustice: OrangeJustice(ref ch, t, mv); break;
            case DanceMove.GangnamStyle: GangnamStyle(ref ch, t, mv); break;
            case DanceMove.Headbang: Headbang(ref ch, t, mv); break;
            case DanceMove.AirGuitar: AirGuitar(ref ch, t, mv); break;
            case DanceMove.FistPump: FistPump(ref ch, t, mv); break;
            case DanceMove.Bounce: Bounce(ref ch, t, mv); break;
            case DanceMove.ArmWave: ArmWave(ref ch, t, mv); break;
            case DanceMove.RunningMan: RunningMan(ref ch, t, mv); break;
            case DanceMove.TStep: TStep(ref ch, t, mv); break;
            case DanceMove.Robot: Robot(ref ch, t, mv); break;
            case DanceMove.Sprinkler: Sprinkler(ref ch, t, mv); break;
            case DanceMove.ShoulderLean: ShoulderLean(ref ch, t, mv); break;
            case DanceMove.Twerk: Twerk(ref ch, t, mv); break;
            case DanceMove.Dab: Dab(ref ch, t, mv); break;
            case DanceMove.Griddy: Griddy(ref ch, t, mv); break;
            case DanceMove.Moonwalk: Moonwalk(ref ch, t, mv); break;
            case DanceMove.Sway: Sway(ref ch, t, mv); break;
            case DanceMove.FolkClap: FolkClap(ref ch, t, mv); break;
            case DanceMove.HandsOnHipsSkip: HandsOnHipsSkip(ref ch, t, mv); break;
            case DanceMove.Pogo: Pogo(ref ch, t, mv); break;
            case DanceMove.JumpTogether: JumpTogether(ref ch, t, mv); break;
            case DanceMove.RatSwing: RatSwing(ref ch, t, mv); break;
            case DanceMove.RatArmPump: RatArmPump(ref ch, t, mv); break;
            case DanceMove.RatHeadBob: RatHeadBob(ref ch, t, mv); break;
            case DanceMove.RatHop: RatHop(ref ch, t, mv); break;
            case DanceMove.Ymca: Ymca(ref ch, t, mv); break;
            case DanceMove.ChickenDance: ChickenDance(ref ch, t, mv); break;
            case DanceMove.CabbagePatch: CabbagePatch(ref ch, t, mv); break;
            case DanceMove.SwimDance: SwimDance(ref ch, t, mv); break;
            case DanceMove.Wave: Wave(ref ch, t, mv); break;
            case DanceMove.Cheer: Cheer(ref ch, t, mv); break;
            case DanceMove.Salute: Salute(ref ch, t, mv); break;
            case DanceMove.Shrug: Shrug(ref ch, t, mv); break;
            case >= DanceMove.FightStand: EvalFight(move, t, mv, ref ch); break;
        }
    }

    // ---- the rat dance (#370) -----------------------------------------------------------------
    // The pastor rat's moves to the chess type beat: the whole body swinging a beat to each side,
    // the head tilting with it, knees giving on every beat.

    /// <summary>The swing: hips and head a beat to each side, the arm on that side thrown up.</summary>
    private static void RatSwing(ref DanceCh ch, in Beat t, bool mv)
    {
        float sw = t.D, dip = DDip(t.B);
        ch.Px = 0.09f * sw; ch.Py = -0.03f - 0.04f * dip;
        ch.Phi = -0.14f * sw; ch.Psi = 0.18f * sw; ch.PhH = 0.30f * sw; ch.ThN = 0.10f * dip;
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f, up = Mathf.Max(0f, s * sw);
            SetArm(ref ch, s, new Vector3(s * 0.25f, -0.35f + 0.85f * up, 0.10f), up > 0.3f ? HUp(s) : HOut(s));
        }
        Planted(ref ch, 0.13f, 0.15f);
        HeelUp(ref ch, -1f, 0.03f * Mathf.Max(0f, sw)); HeelUp(ref ch, 1f, 0.03f * Mathf.Max(0f, -sw));
        if (mv) MovingScale(ref ch, 0.6f, 1f);
    }

    /// <summary>Both fists pumped up on every beat, the head bobbing side to side.</summary>
    private static void RatArmPump(ref DanceCh ch, in Beat t, bool mv)
    {
        float dip = DDip(t.B), up = 1f - dip;
        ch.Py = -0.05f * dip; ch.Px = 0.05f * t.D;
        ch.Phi = -0.06f * t.D; ch.PhH = 0.20f * t.D; ch.ThN = 0.12f * dip;
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            Aim(ref ch, s, 0.22f, -0.10f + 0.55f * up, 0.18f, HUp(s));
        }
        Planted(ref ch, 0.12f, 0.15f);
        if (mv) MovingScale(ref ch, 0.6f, 1f);
    }

    /// <summary>Hands on the hips, the head tilted hard to one side then the other, hips following.</summary>
    private static void RatHeadBob(ref DanceCh ch, in Beat t, bool mv)
    {
        float dip = DDip(t.B);
        ch.Px = 0.06f * t.D; ch.Py = -0.03f * dip;
        ch.Phi = -0.08f * t.D; ch.PhH = 0.40f * t.D; ch.ThN = 0.15f * dip;
        Akimbo(ref ch, -1f); Akimbo(ref ch, 1f);
        Planted(ref ch, 0.12f, 0.2f);
        if (mv) MovingScale(ref ch, 0.6f, 0.4f);
    }

    /// <summary>A little hop on every beat, swinging to the side, arms out flapping.</summary>
    private static void RatHop(ref DanceCh ch, in Beat t, bool mv)
    {
        float air = Air(t.B, 0.15f, 0.85f), h = 0.12f * air;
        ch.Py = h - 0.05f * Give(t.B, 0.02f, 0.1f); ch.Px = 0.08f * t.D;
        ch.Phi = -0.10f * t.D; ch.PhH = 0.25f * t.D; ch.ThN = 0.08f * (1f - air);
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            Aim(ref ch, s, 0.45f, -0.05f + 0.25f * air, 0.05f, HOut(s));
        }
        Foot(ref ch, -1f, -0.11f, AnkleHeight + h, 0f, 0.1f, air);
        Foot(ref ch, 1f, 0.11f, AnkleHeight + h, 0f, 0.1f, air);
        if (mv) { MovingScale(ref ch, 0.5f, 1f); ch.Py = Mathf.Clamp(h, 0f, 0.04f); }
    }

    /// <summary>0 on the ground, rising to 1 at the top of a jump that leaves at <paramref name="off"/> and lands at <paramref name="on"/> (beat phase).</summary>
    private static float Air(float b, float off, float on)
    {
        float x = (b - off) / (on - off);
        return x <= 0f || x >= 1f ? 0f : 4f * x * (1f - x);
    }

    /// <summary>A soft knee-bend peaking at beat phase <paramref name="at"/>, wrapping round the beat.</summary>
    private static float Give(float b, float at, float width)
    {
        float d = Mathf.Abs(b - at);
        d = Mathf.Min(d, 1f - d) / width;
        return Mathf.Exp(-d * d);
    }

    /// <summary>
    /// Pogo (#261), punk's jump: straight up on every beat with the legs together, landing soft
    /// on the next one and going straight back up. Arms tucked at the chest, a fist punched up
    /// every other beat, the head snapping down on the landing.
    /// </summary>
    private static void Pogo(ref DanceCh ch, in Beat t, bool mv)
    {
        float air = Air(t.B, 0.16f, 0.94f);
        float give = Give(t.B, 0.03f, 0.09f);
        float h = 0.22f * air;
        ch.Py = h - 0.085f * give;
        ch.Theta = 0.05f + 0.10f * give;
        ch.ThN = 0.20f * give - 0.10f * air;
        ch.Phi = 0.04f * t.D;
        ch.ShL = ch.ShR = 0.025f * air;
        float punch = (t.K & 1) == 0 ? air : 0f;
        // fists by the chest, elbows in; rig-L (the figure's right) punches up on the even beats
        Aim(ref ch, 1f, 0.08f, -0.22f, 0.20f, HLow(1f));
        SetArm(ref ch, -1f, new Vector3(-0.08f, -0.22f, 0.20f).Lerp(new Vector3(-0.12f, 0.46f, 0.08f), punch), HLow(-1f).Lerp(HUp(-1f), punch));
        Foot(ref ch, -1f, -0.11f, AnkleHeight + h, 0f, 0.1f, air);
        Foot(ref ch, 1f, 0.11f, AnkleHeight + h, 0f, 0.1f, air);
        if (mv) { MovingScale(ref ch, 0.5f, 1f); ch.Py = Mathf.Clamp(h, 0f, 0.04f); }
    }

    /// <summary>
    /// Jump together (#261): three bounces that sink deeper while the arms swing back further,
    /// then on four everybody near the music leaves the ground at once, arms thrown up, and lands
    /// on the next bar's one.
    /// </summary>
    private static void JumpTogether(ref DanceCh ch, in Beat t, bool mv)
    {
        if (t.K < 3)
        {
            // wind-up: a bounce per beat, each a little deeper
            float depth = 0.035f + 0.025f * t.K;
            float dip = DDip(t.B);
            ch.Py = -depth * dip;
            ch.Theta = 0.06f + 0.06f * dip + 0.03f * t.K;
            ch.ThN = 0.08f * dip;
            float back = (0.25f + 0.2f * t.K) * dip;
            AimO(ref ch, -1f, -0.18f, -0.42f, -0.10f - back * 0.35f, HLow(-1f));
            AimO(ref ch, 1f, 0.18f, -0.42f, -0.10f - back * 0.35f, HLow(1f));
            Planted(ref ch, 0.14f, 0.15f);
            Lift(ref ch, -1f, 0.02f * (1f - dip)); Lift(ref ch, 1f, 0.02f * (1f - dip));
        }
        else
        {
            // four: down hard, then up as high as legs go, arms flung overhead; land on the next one
            float crouch = Give(t.B, 0.02f, 0.10f);
            float air = Air(t.B, 0.12f, 0.98f);
            float h = 0.34f * air;
            ch.Py = h - 0.13f * crouch;
            ch.Theta = 0.14f * crouch - 0.06f * air;
            ch.ThN = -0.18f * air + 0.10f * crouch;
            ch.ShL = ch.ShR = 0.04f * air;
            float up = DSm(Mathf.Clamp((t.B - 0.05f) / 0.3f, 0f, 1f));
            SetArm(ref ch, -1f, new Vector3(-0.18f, -0.42f, -0.25f).Lerp(new Vector3(-0.30f, 0.44f, 0.05f), up), HLow(-1f).Lerp(HUp(-1f), up));
            SetArm(ref ch, 1f, new Vector3(0.18f, -0.42f, -0.25f).Lerp(new Vector3(0.30f, 0.44f, 0.05f), up), HLow(1f).Lerp(HUp(1f), up));
            // knees come up a little at the top, as a real jump tucks
            Foot(ref ch, -1f, -0.14f, AnkleHeight + h + 0.06f * air, -0.04f * air, 0.15f, air);
            Foot(ref ch, 1f, 0.14f, AnkleHeight + h + 0.06f * air, -0.04f * air, 0.15f, air);
        }
        if (mv) { MovingScale(ref ch, 0.5f, 1f); ch.Py = Mathf.Clamp(ch.Py, -0.04f, 0.04f); }
    }

    private static void Twerk(ref DanceCh ch, in Beat t, bool mv)
    {
        float b = t.B;
        float q = (1f + DCos(2f * b)) * 0.5f;          // 1 on the beat and the "&"
        float a = 0.75f + 0.25f * DCos(b);             // the on-beat pump is the big one
        float qa = q * a;
        if (!mv)
        {
            ch.Py = -0.235f - 0.035f * qa;             // hips back and down, deeper on each pump
            ch.Pz = -0.12f - 0.08f * qa;
            ch.Tilt = 0.02f * t.E * a;
            ch.Theta = 0.87f - 0.04f * qa;
            ch.ThN = -0.45f;                           // looks forward, not at the floor
            Planted(ref ch, 0.30f, 0.35f);
            // hands on the knees of the same side, riding the pump
            AimKnee(ref ch, -1f, new Vector3(0f, 0.09f, -0.02f), HOut(-1f));
            AimKnee(ref ch, 1f, new Vector3(0f, 0.09f, -0.02f), HOut(1f));
        }
        else
        {
            ch.Py = -0.02f * qa; ch.Pz = -0.03f - 0.03f * qa;
            ch.Theta = 0.35f; ch.ThN = -0.15f;
            Akimbo(ref ch, -1f); Akimbo(ref ch, 1f);
        }
    }

    private static void Dab(ref DanceCh ch, in Beat t, bool mv)
    {
        float ramp = DSm(t.B / 0.25f);
        // +1 toward +X on beats 1-2, -1 on beats 3-4, snapping over the first quarter beat
        float sg = t.K == 0 ? Mathf.Lerp(-1f, 1f, ramp) : t.K == 1 ? 1f : t.K == 2 ? Mathf.Lerp(1f, -1f, ramp) : -1f;
        ch.Px = -sg * 0.03f;
        ch.Py = -0.07f - ((t.K & 1) == 1 ? 0.015f * DDip(t.B) : 0f);
        ch.Theta = 0.20f; ch.Phi = sg * 0.10f; ch.Psi = sg * 0.25f;
        ch.ThN = 0.50f; ch.PhH = sg * 0.25f;
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            float u = (1f + s * sg) * 0.5f;            // 1 when this arm is the straight one
            var straight = new Vector3(s * 0.35f, 0.33f, 0.05f);
            var bent = new Vector3(s * -0.28f, 0.16f, 0.26f + 0.10f * 4f * u * (1f - u));   // crosses in front first
            SetArm(ref ch, s, bent.Lerp(straight, u), new Vector3(s * 0.3f, 1.0f, 0.2f).Lerp(HOut(s), u));
        }
        Planted(ref ch, 0.17f, 0.15f);
        if (mv) { MovingScale(ref ch, 0.8f, 1f); ch.Px = 0f; }
    }

    private static void Floss(ref DanceCh ch, in Beat t, bool mv)
    {
        float ar = mv ? 0.7f : 1f;
        ch.Px = mv ? -0.03f * t.D : -0.07f * t.D;
        ch.Py = -0.06f - 0.02f * DDip(t.B);
        ch.Theta = 0.05f; ch.Phi = 0.08f * t.D; ch.PhH = 0.05f * t.D;
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            // an ellipse about the body axis, radius at least 0.29 m: the fists never enter the torso
            AimH(ref ch, s, 0.20f * ar * t.D, 0.22f, s * 0.22f * ar * t.E, HLow(s));
        }
        Planted(ref ch, 0.15f, 0.15f);
        for (int i = 0; i < 2; i++) { float s = i * 2f - 1f; HeelUp(ref ch, s, 0.02f * Mathf.Max(0f, -s * t.D)); }
        if (mv) { float px = ch.Px; MovingScale(ref ch, 0.7f, 1f); ch.Px = px; }
    }

    private static void Carlton(ref DanceCh ch, in Beat t, bool mv)
    {
        float c = t.C;
        // A (beats 1-2) and B (beats 3-4) crossfade over a quarter beat at c = 2 and c = 4
        float xw = c < 2f ? 1f - DSm(c / 0.25f) : DSm((c - 2f) / 0.25f);
        DanceCh a = ch;
        CarltonA(ref a, t, mv);
        if (xw <= 0.0001f) { ch = a; return; }
        DanceCh bb = ch;
        CarltonB(ref bb, t, mv);
        ch = xw >= 0.9999f ? bb : Mix(a, bb, xw);
    }

    private static void CarltonA(ref DanceCh a, in Beat t, bool mv)
    {
        float c = t.C, ar = mv ? 0.7f : 1f;
        a.Px = 0.07f * t.D; a.Py = -0.04f - 0.02f * DDip(t.B);
        a.Phi = 0.10f * DSin(c * 0.5f - 0.06f); a.Psi = 0.12f * t.D; a.Theta = 0.04f;
        a.ThN = 0.05f * DDip(t.B); a.PhH = -0.10f * t.D;
        float ang = 1.2f * DSin(c * 0.5f + 0.04f) * ar;
        AimO(ref a, -1f, 0.42f * Mathf.Sin(ang), -0.42f * Mathf.Cos(ang), 0.24f, HLow(-1f));
        AimO(ref a, 1f, 0.42f * Mathf.Sin(ang), -0.42f * Mathf.Cos(ang), 0.24f, HLow(1f));
        // step sideways with the hips: the weight foot steps wide, the other closes in
        float S = DSq(t.D);
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            Foot(ref a, s, s * 0.098f + s * 0.06f * (1f + s * S), AnkleHeight, 0f, 0.15f, 0f);
            Lift(ref a, s, 0.03f * Mathf.Max(0f, -s * S) * DHop(t.B));
        }
        if (mv) { float px = a.Px; MovingScale(ref a, 0.7f, 1f); a.Px = px; }
    }

    private static void CarltonB(ref DanceCh bb, in Beat t, bool mv)
    {
        float cB = t.C < 1f ? t.C + 4f : t.C;          // the tail of beat 4 when crossfading into the next bar
        bb.Py = -0.04f - 0.015f * DDip(t.B); bb.Theta = 0.04f; bb.ThN = 0.05f * DDip(t.B);
        Planted(ref bb, 0.14f, 0.15f);
        if (cB < 3f)
        {
            Aim(ref bb, -1f, -0.05f, 0.47f, 0.03f, HUp(-1f));
            Aim(ref bb, 1f, -0.05f, 0.47f, 0.03f, HUp(1f));
        }
        else
        {
            var up = new Vector3(-0.05f, 0.47f, 0.03f);
            float u = DSm(cB - 3f);
            // rig-R flutters down from above the head to the middle of the chest
            var chest = new Vector3(-0.14f, -0.12f + 0.012f * DSin(6f * cB), 0.26f);
            SetArm(ref bb, 1f, new Vector3(up.X, up.Y, up.Z).Lerp(chest, u),
                HUp(1f).Lerp(new Vector3(0.5f, -0.6f, -0.2f), u));
            // rig-L drops its hand onto the hip
            float v = DSm((cB - 3f) / 0.25f);
            SetArm(ref bb, -1f, new Vector3(-up.X, up.Y, up.Z).Lerp(new Vector3(-AkimboOff.X, AkimboOff.Y, AkimboOff.Z), v),
                HUp(-1f).Lerp(HOut(-1f), v));
        }
        if (mv) { MovingScale(ref bb, 0.7f, 1f); bb.ThN = 0.05f * 0.7f * DDip(t.B); }
    }

    private static void GangnamStyle(ref DanceCh ch, in Beat t, bool mv)
    {
        float hop = DHop(t.B), c = t.C;
        ch.Py = mv ? 0.02f * hop : -0.06f + 0.05f * hop;   // lands on the beat, airborne mid-beat
        ch.Theta = 0.10f; ch.Phi = 0.04f * t.D; ch.Psi = 0.10f * t.D; ch.ThN = 0.05f;
        // rig-L keeps the reins all bar; rig-R swaps them for the lasso on beats 3-4
        float w = c < 2f ? 1f - DSm(c / 0.25f) : DSm((c - 2f) / 0.25f);
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            var reins = new Vector3(s * -0.12f, -0.22f + 0.04f * hop + 0.03f * s * t.D, 0.32f);
            if (s > 0f)
            {
                float al = Mathf.Tau * c;
                var lasso = new Vector3(0.15f * Mathf.Cos(al), 0.42f, 0.15f * Mathf.Sin(al));
                SetArm(ref ch, s, reins.Lerp(lasso, w), HLow(s).Lerp(HUp(s), w));
            }
            else SetArm(ref ch, s, reins, HLow(s));
            Foot(ref ch, s, s * 0.16f, AnkleHeight + 0.04f * hop, 0.12f * s * t.E, 0.1f, hop);   // the gallop, both feet hopping
        }
        if (mv) { float py = ch.Py; MovingScale(ref ch, 0.6f, 1f); ch.Py = py; }
    }

    // id: 0 OUT, 1 UP, 2 CROSS, 3 EAR, 4 HIP
    private static readonly byte[] MacarenaL = { 0, 0, 1, 1, 2, 2, 3, 3 };
    private static readonly byte[] MacarenaR = { 4, 0, 0, 1, 1, 2, 2, 3 };

    private static void MacarenaTarget(int id, float s, out Vector3 off, out Vector3 hint)
    {
        switch (id)
        {
            case 0: off = new Vector3(s * -0.03f, 0f, 0.47f); hint = HLow(s); break;
            case 1: off = new Vector3(s * -0.03f, 0.04f, 0.47f); hint = HLow(s); break;
            case 2: off = new Vector3(s * -0.32f, -0.02f, s > 0f ? 0.27f : 0.22f); hint = new Vector3(0f, -1f, 0.3f); break;
            case 3: off = new Vector3(s * -0.05f, 0.16f, -0.04f); hint = new Vector3(s, 0.3f, -0.3f); break;
            default: off = new Vector3(s * 0.05f, -0.40f, 0.06f); hint = HOut(s); break;
        }
    }

    private static void Macarena(ref DanceCh ch, in Beat t, bool mv)
    {
        // authentic speed: one gesture per beat, eight over two bars (bar parity picks the half)
        int j = 4 * t.Par + t.K;
        int prev = (j + 7) & 7;
        float e = DSm(Mathf.Min(t.B / 0.45f, 1f));
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            var tab = s < 0f ? MacarenaL : MacarenaR;
            MacarenaTarget(tab[prev], s, out var o0, out var h0);
            MacarenaTarget(tab[j], s, out var o1, out var h1);
            SetArm(ref ch, s, o0.Lerp(o1, e), h0.Lerp(h1, e));
        }
        Planted(ref ch, 0.13f, 0.15f);
        for (int i = 0; i < 2; i++) { float s = i * 2f - 1f; HeelUp(ref ch, s, 0.02f * Mathf.Max(0f, -s * DSin(t.C * 0.5f))); }
        ch.Px = 0.04f * DSin(t.C * 0.5f); ch.Py = -0.02f * DDip(t.B);
        ch.Phi = -0.04f * DSin(t.C * 0.5f); ch.Psi = 0.08f * t.D;
        ch.ThN = 0.04f * DDip(t.B); ch.PhH = 0.05f * t.D;
        if (mv) { float px = ch.Px * 0.75f; MovingScale(ref ch, 0.6f, 1f); ch.Px = px; }
    }

    private static void OrangeJustice(ref DanceCh ch, in Beat t, bool mv)
    {
        float c = t.C, ar = mv ? 0.7f : 1f;
        float w1 = c >= 3f ? DSm((c - 3f) / 0.2f) : 0f;                       // pump -> shrug
        float w2 = c >= 3.5f ? DSm((c - 3.5f) / 0.2f) : 0f;                   // shrug -> clap
        float wc = c < 0.2f ? 1f - DSm(c / 0.2f) : 0f;                        // clap -> pump at the bar line
        float clap = Mathf.Max(w2, wc);
        ch.Py = -0.06f - 0.05f * DDip(t.B);
        ch.Theta = 0.05f; ch.Phi = 0.04f * t.D; ch.Psi = 0.10f * t.D; ch.ThN = 0.05f * DDip(t.B);
        ch.ShL = ch.ShR = 0.05f * w1 * (1f - clap);
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            var pump = new Vector3(-s * 0.18f * ar * t.D - s * 0.18f, 0.38f + 0.04f * DDip(t.B) - 0.51f,
                (mv ? 0.27f : 0.30f) + 0.08f * s * t.E);
            var shrug = new Vector3(s * 0.05f, -0.05f, 0.22f);
            var clapO = new Vector3(s * -0.16f, 0.44f, 0.03f);
            SetArm(ref ch, s, pump.Lerp(shrug, w1).Lerp(clapO, clap),
                HLow(s).Lerp(HOut(s), w1).Lerp(HUp(s), clap));
        }
        Planted(ref ch, 0.15f, 0.15f);
        HeelUp(ref ch, -1f, 0.03f * clap); HeelUp(ref ch, 1f, 0.03f * clap);
        if (mv) MovingScale(ref ch, 0.7f, 1f);
    }

    private static void SideStepClap(ref DanceCh ch, in Beat t, bool mv)
    {
        float sr = DSin(t.R), S = DSq(sr);
        ch.Px = 0.06f * sr; ch.Py = -0.03f - 0.02f * DDip(t.B);
        ch.Phi = -0.04f * sr; ch.Theta = 0.03f; ch.ThN = 0.03f * DDip(t.B);
        ClapArms(ref ch, DCl(t.C));
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            // step-touch: the weight foot steps out, the free foot closes in and lifts between claps
            Foot(ref ch, s, s * 0.098f + s * 0.06f * (1f + s * S), AnkleHeight, 0f, 0.15f, 0f);
            if (s * S < 0f) Lift(ref ch, s, 0.05f * Mathf.Max(0f, t.D));
        }
        if (mv) { MovingScale(ref ch, 0.6f, 1f); ch.Px = 0.03f * sr; }
    }

    private static void HipSway(ref DanceCh ch, in Beat t, bool mv)
    {
        ch.Px = 0.06f * t.E; ch.Py = -0.02f; ch.Tilt = 0.015f * t.E;
        ch.Phi = -0.07f * t.E; ch.Psi = 0.10f * t.D; ch.PhH = 0.05f * t.E;
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            Aim(ref ch, s, 0.04f, -0.49f, 0.02f + 0.05f * s * t.D, HLow(s));   // HANG, hands counter-swinging
        }
        Planted(ref ch, 0.11f, 0.15f);
        HeelUp(ref ch, -1f, 0.02f * Mathf.Max(0f, t.E)); HeelUp(ref ch, 1f, 0.02f * Mathf.Max(0f, -t.E));
        if (mv) { MovingScale(ref ch, 0.5f, 0f); }
    }

    private static void ClapBackbeat(ref DanceCh ch, in Beat t, bool mv)
    {
        ch.Py = -0.02f * DDip(t.B); ch.Px = 0.03f * t.E;
        ch.Phi = -0.03f * t.E; ch.Theta = 0.04f; ch.ThN = 0.04f * DDip(t.B);
        ClapArms(ref ch, DCl(t.C));
        Planted(ref ch, 0.11f, 0.15f);
        if (mv) MovingScale(ref ch, 0.8f, 1f);
    }

    private static void DiscoPoint(ref DanceCh ch, in Beat t, bool mv)
    {
        float ramp = DSm(t.B / 0.3f);
        // pointing arm: rig-L (sigma -1) on beats 1-2, rig-R (+1) on beats 3-4
        float sg = t.K == 0 ? Mathf.Lerp(1f, -1f, ramp) : t.K == 1 ? -1f : t.K == 2 ? Mathf.Lerp(-1f, 1f, ramp) : 1f;
        float aw = (t.K & 1) == 0 ? ramp : 1f - ramp;        // weight of the up-diagonal count inside the pair
        ch.Px = -sg * 0.05f; ch.Py = -0.04f; ch.Tilt = sg * 0.015f;
        ch.Phi = sg * 0.10f; ch.Psi = sg * 0.10f; ch.PhH = -sg * 0.10f;
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            float pw = (1f + s * sg) * 0.5f;                 // 1 when this arm points
            var point = new Vector3(s * 0.30f, 0.38f, 0.05f).Lerp(new Vector3(s * -0.20f, -0.40f, 0.22f), 1f - aw);
            SetArm(ref ch, s, new Vector3(s * 0.05f, -0.40f, 0.06f).Lerp(point, pw), HOut(s));
        }
        Planted(ref ch, 0.14f, 0.15f);
        HeelUp(ref ch, -1f, 0.03f * (1f - (1f + sg) * 0.5f)); HeelUp(ref ch, 1f, 0.03f * ((1f + sg) * 0.5f));
        if (mv) MovingScale(ref ch, 0.7f, 1f);
    }

    private static void Headbang(ref DanceCh ch, in Beat t, bool mv)
    {
        float dip = DDip(t.B);
        ch.Py = -0.08f - 0.03f * dip;
        ch.Theta = 0.20f + 0.15f * dip; ch.Phi = 0.04f * t.D;
        ch.ThN = DSawNod(t.B); ch.PhH = 0.10f * t.D;          // head lowest exactly on the beat
        Aim(ref ch, -1f, 0f, -0.30f - 0.06f * dip, 0.28f, HLow(-1f));
        Aim(ref ch, 1f, 0f, -0.30f - 0.06f * dip, 0.28f, HLow(1f));
        Planted(ref ch, 0.22f, 0.2f);
        Lift(ref ch, -1f, 0.015f * (1f - dip)); Lift(ref ch, 1f, 0.015f * (1f - dip));
        if (mv)
        {
            float nod = ch.ThN, ph = ch.PhH;
            MovingScale(ref ch, 0.7f, 0.5f);
            ch.ThN = nod;                                      // the nod stays full size, the clearest read at a run
            ch.PhH = ph * 0.7f;
            ch.Theta = 0.12f + 0.08f * dip;
        }
    }

    private static void AirGuitar(ref DanceCh ch, in Beat t, bool mv)
    {
        float c = t.C, dip = DDip(t.B);
        ch.Py = -0.09f - 0.03f * dip;
        ch.Theta = 0.12f + 0.05f * dip; ch.Psi = 0.15f; ch.Phi = -0.05f;
        ch.ThN = 0.25f * dip; ch.PhH = 0.08f * t.D;
        Aim(ref ch, 1f, 0.12f, -0.08f, 0.42f + 0.05f * t.D, HLow(1f));          // the fretting hand slides
        // the strumming hand pumps low on the beat and the "&", and windmills once on beat 4
        var strum = new Vector3(0.18f, -0.36f - 0.06f * DCos(2f * t.B), 0.26f);   // s = -1 mirrors o(-0.18, ...)
        float wm = 0f;
        if (!mv && t.K == 3) wm = DSm(t.B / 0.15f) * (1f - DSm((t.B - 0.85f) / 0.15f));
        float al = Mathf.Tau * (c - 3f);
        var windmill = new Vector3(-0.05f, -0.46f * Mathf.Cos(al), 0.46f * Mathf.Sin(al));
        SetArm(ref ch, -1f, strum.Lerp(windmill, wm), HLow(-1f));
        Planted(ref ch, 0.25f, 0.15f);
        Foot(ref ch, 1f, 0.25f, AnkleHeight, 0.15f, 0.15f, 0f);
        Foot(ref ch, -1f, -0.25f, AnkleHeight, -0.10f, 0.15f, 0f);
        if (mv) MovingScale(ref ch, 0.7f, 1f);
    }

    private static void FistPump(ref DanceCh ch, in Beat t, bool mv)
    {
        ch.Py = -0.04f * DDip(t.B);
        ch.Theta = 0.05f; ch.Phi = -0.06f * t.E; ch.Psi = -0.12f * t.E; ch.ThN = 0.06f * DDip(t.B);
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            float c0 = s < 0f ? 0f : 1f;
            float p = (1f + DCos((t.C - c0) * 0.5f)) * 0.5f;    // 1 while this fist is up
            SetArm(ref ch, s, new Vector3(s * 0.10f, -0.05f, 0.25f).Lerp(new Vector3(s * 0.14f, 0.42f, 0.12f), p),
                HLow(s).Lerp(HUp(s), p));
        }
        Planted(ref ch, 0.15f, 0.15f);
        float heel = 0.015f * (1f - DDip(t.B));
        Lift(ref ch, -1f, heel); Lift(ref ch, 1f, heel);
        if (mv) MovingScale(ref ch, 0.6f, 1f);
    }

    private static void Bounce(ref DanceCh ch, in Beat t, bool mv)
    {
        float dip = DDip(t.B);
        ch.Py = -0.07f * dip;
        ch.Theta = 0.05f + 0.05f * dip; ch.Phi = 0.03f * t.D; ch.ThN = 0.10f * dip; ch.PhH = 0.04f * t.D;
        Aim(ref ch, -1f, 0.10f, -0.30f - 0.05f * dip, 0.22f, HLow(-1f));
        Aim(ref ch, 1f, 0.10f, -0.30f - 0.05f * dip, 0.22f, HLow(1f));
        Planted(ref ch, 0.13f, 0.15f);
        Lift(ref ch, -1f, 0.03f * (1f - dip)); Lift(ref ch, 1f, 0.03f * (1f - dip));   // heels rise as the body rises
        if (mv) { MovingScale(ref ch, 0.5f, 0.6f); ch.Py = -0.03f * dip; }
    }

    private static float Bump(float r, float node)
    {
        float d = Mathf.Abs(r - node);
        d = Mathf.Min(d, 1f - d);
        float x = d / 0.08f;
        return Mathf.Exp(-x * x);
    }

    private static void ArmWave(ref DanceCh ch, in Beat t, bool mv)
    {
        float r = t.R;
        ch.Py = -0.01f * DDip(t.B); ch.Phi = 0.04f * DSin(r); ch.PhH = 0.05f * DSin(r);
        // the crest leaves the rig-L hand, climbs elbow and shoulder, crosses the back, reaches the rig-R hand
        float bhL = Bump(r, 0.05f), beL = Bump(r, 0.20f), bsL = Bump(r, 0.35f);
        float bsR = Bump(r, 0.55f), beR = Bump(r, 0.70f), bhR = Bump(r, 0.85f);
        AimChain(ref ch, -1f, 0.12f + 0.45f * beL, 0f, 0.12f + 0.60f * bhL - 0.35f * beL, 0f);
        AimChain(ref ch, 1f, 0.12f + 0.45f * beR, 0f, 0.12f + 0.60f * bhR - 0.35f * beR, 0f);
        ch.ShL = 0.035f * bsL; ch.ShR = 0.035f * bsR;
        Planted(ref ch, 0.13f, 0.15f);
        if (mv)
        {
            float sh0 = ch.ShL, sh1 = ch.ShR;
            MovingScale(ref ch, 0.5f, 1f);
            ch.ShL = sh0; ch.ShR = sh1;                        // arms only: the wave keeps its full size
        }
    }

    // ArmChain poses (a1, g1, a2, g2) for [A goalposts, B, D tray, C] x [rig-L, rig-R]
    private static readonly float[] RobotPoses =
    {
        // A: both goalposts
        0f, 0f, 1.5708f, 0f,            0f, 0f, 1.5708f, 0f,
        // B: rig-L upper arm forward + forearm up, rig-R upper arm down + forearm forward
        0f, 1.5708f, 1.5708f, 0f,       -1.35f, 0f, 0f, 1.5708f,
        // D: both trays
        -1.35f, 0f, 0f, 1.5708f,        -1.35f, 0f, 0f, 1.5708f,
        // C: mirror of B
        -1.35f, 0f, 0f, 1.5708f,        0f, 1.5708f, 1.5708f, 0f,
    };
    private static readonly float[] RobotPsi = { 0f, 0.30f, 0f, -0.30f };
    private static readonly float[] RobotHead = { 0f, 0.15f, 0f, -0.15f };

    private static void Robot(ref DanceCh ch, in Beat t, bool mv)
    {
        int k = t.K, p = (k + 3) & 3;
        float u = DRamp(t.B / 0.12f);                           // abrupt: a linear ramp in the first 0.12 beat, then hold
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            int o0 = p * 8 + i * 4, o1 = k * 8 + i * 4;
            AimChain(ref ch, s,
                Mathf.Lerp(RobotPoses[o0], RobotPoses[o1], u), Mathf.Lerp(RobotPoses[o0 + 1], RobotPoses[o1 + 1], u),
                Mathf.Lerp(RobotPoses[o0 + 2], RobotPoses[o1 + 2], u), Mathf.Lerp(RobotPoses[o0 + 3], RobotPoses[o1 + 3], u));
        }
        ch.Py = 0f;
        ch.Psi = Mathf.Lerp(RobotPsi[p], RobotPsi[k], u);
        ch.PhH = Mathf.Lerp(RobotHead[p], RobotHead[k], u);
        Planted(ref ch, 0.12f, 0.15f);
        ch.Py = -0.02f;
        if (mv)
        {
            float ph = ch.PhH;
            MovingScale(ref ch, 0.8f, 1f);
            ch.PhH = ph;
        }
    }

    private static void RunningMan(ref DanceCh ch, in Beat t, bool mv)
    {
        ch.Py = -0.04f - 0.03f * DDip(t.B);
        ch.Theta = 0.12f; ch.Psi = 0.10f * t.D;
        float amp = mv ? 0.26f * 0.6f : 0.26f;
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            float a = DSin(t.C * 0.5f + (s < 0f ? 0f : 0.5f));   // fists pump opposite to the legs
            SetArm(ref ch, s, new Vector3(s * 0.03f, -0.25f, 0.05f + amp * a), new Vector3(s * 0.35f, -0.25f, -1f));
            float ph = DFrac(t.C * 0.5f + (s > 0f ? 0f : 0.5f));
            if (ph < 0.5f)
            {
                // the foot lifts and swings forward
                float sw = DSin(ph);
                Foot(ref ch, s, s * 0.10f, AnkleHeight + 0.22f * sw, Mathf.Lerp(-0.05f, 0.20f, DSm(2f * ph)), 0.05f, sw);
            }
            else
            {
                // planted, sliding back
                Foot(ref ch, s, s * 0.10f, AnkleHeight, Mathf.Lerp(0.20f, -0.05f, 2f * (ph - 0.5f)), 0.05f, 0f);
            }
        }
        if (mv) { float th = ch.Theta; MovingScale(ref ch, 0.6f, 1f); ch.Theta = th; }
    }

    private static void TStep(ref DanceCh ch, in Beat t, bool mv)
    {
        float sr = DSin(t.R);
        ch.Py = -0.05f - 0.015f * DDip(t.B); ch.Px = 0.04f * sr;
        ch.Theta = 0.05f; ch.Psi = 0.10f * sr; ch.ThN = 0.04f * DDip(t.B);
        AimO(ref ch, -1f, 0.12f * sr, -0.28f, 0.20f, HLow(-1f));
        AimO(ref ch, 1f, 0.12f * sr, -0.28f, 0.20f, HLow(1f));
        float e = DSin((t.C >= 2f ? t.C - 2f : t.C) / 4f);
        float activeS = t.C < 2f ? 1f : -1f;
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            if (s == activeS) Foot(ref ch, s, s * (0.098f + 0.17f * e), AnkleHeight, -0.04f * e, 1.2f * e, 0f);   // slides out, foot turns into a T
            else Foot(ref ch, s, s * 0.098f, AnkleHeight, 0f, 0f, 0f);
        }
        if (mv) MovingScale(ref ch, 0.6f, 1f);
    }

    private static void Sprinkler(ref DanceCh ch, in Beat t, bool mv)
    {
        ch.Py = -0.12f - 0.01f * DDip(t.B);
        ch.Theta = 0.25f;
        // the arc, as chest twist: three jerks across, one smooth sweep back
        ch.Psi = t.C < 3f ? -0.9f + 0.6f * (t.K + DSm(Mathf.Min(t.B / 0.35f, 1f))) : Mathf.Lerp(0.9f, -0.9f, DSm(t.B));
        Aim(ref ch, 1f, 0.50f, 0f, 0.02f, HOut(1f));
        Aim(ref ch, -1f, -0.05f, 0.16f, -0.06f, new Vector3(-1f, 0.2f, -0.3f));
        Planted(ref ch, 0.14f, 0.15f);
        if (mv) MovingScale(ref ch, 0.6f, 1f);
    }

    private static void ShoulderLean(ref DanceCh ch, in Beat t, bool mv)
    {
        float dip = DDip(t.B);
        ch.Px = -0.04f * t.E; ch.Py = -0.03f - 0.04f * dip;
        ch.Phi = 0.18f * t.E; ch.Theta = 0.06f; ch.Psi = 0.05f * t.D;
        ch.ShL = 0.02f * t.E; ch.ShR = -0.02f * t.E;           // the leaning side drops a little extra
        ch.ThN = 0.08f * dip; ch.PhH = -0.10f * t.E;
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            Aim(ref ch, s, 0.12f, -0.32f, 0.18f + 0.05f * s * t.D, HLow(s));
        }
        Planted(ref ch, 0.15f, 0.15f);
        HeelUp(ref ch, -1f, 0.02f * Mathf.Max(0f, t.E)); HeelUp(ref ch, 1f, 0.02f * Mathf.Max(0f, -t.E));
        if (mv) MovingScale(ref ch, 0.6f, 0.5f);
    }

    private static void Griddy(ref DanceCh ch, in Beat t, bool mv)
    {
        float D = t.D;
        ch.Px = mv ? -0.03f * D : -0.05f * D;
        ch.Py = -0.12f - 0.02f * DDip(t.B);
        ch.Theta = 0.12f; ch.Phi = 0.05f * D; ch.Psi = 0.10f * D; ch.ThN = 0.05f;
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            float up = DSm((1f + s * D) * 0.5f);                // rig-R up while D > 0, rig-L while D < 0
            SetArm(ref ch, s, new Vector3(s * 0.06f, -0.47f, -0.08f).Lerp(new Vector3(s * 0.10f, 0.38f, 0.05f), up),
                new Vector3(s * 0.3f, -1f, -0.5f).Lerp(new Vector3(s, 0f, -0.5f), up));
            float x = Mathf.Max(0f, s * D);                     // crossing foot: rig-R while D > 0
            Foot(ref ch, s, s * (0.28f - 0.26f * x), AnkleHeight, -0.12f * x, 0.3f, 0f);
        }
        if (mv) { float px = ch.Px; MovingScale(ref ch, 0.7f, 1f); ch.Px = px; }
    }

    private static void Moonwalk(ref DanceCh ch, in Beat t, bool mv)
    {
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            SetArm(ref ch, s, new Vector3(s * 0.10f, -0.30f + 0.03f * s * t.D, 0.12f + 0.08f * s * t.D), HLow(s));
        }
        if (mv)
        {
            // walking: no sliding feet; the Bounce moving variant with the lean back
            Bounce(ref ch, t, true);
            ch.Theta = -0.05f;
            for (int i = 0; i < 2; i++)
            {
                float s = i * 2f - 1f;
                SetArm(ref ch, s, new Vector3(s * 0.10f, -0.30f + 0.03f * s * t.D, 0.12f + 0.08f * s * t.D), HLow(s));
            }
            ch.ArmBlend = 0.6f;
            return;
        }
        ch.Py = -0.03f; ch.Pz = -0.02f * t.D;
        ch.Theta = -0.05f; ch.Phi = 0.03f * t.D; ch.Psi = 0.06f * t.D; ch.ThN = 0.04f * DDip(t.B);
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            float ph = DFrac(t.C * 0.5f + (s > 0f ? 0f : 0.5f));
            if (ph < 0.5f)
                Foot(ref ch, s, s * 0.10f, AnkleHeight, Mathf.Lerp(0.10f, -0.12f, 2f * ph), 0f, 0f);   // flat slide back
            else
            {
                Foot(ref ch, s, s * 0.10f, AnkleHeight, Mathf.Lerp(-0.12f, 0.10f, DSm(2f * (ph - 0.5f))), 0f, 0f);
                HeelUp(ref ch, s, 0.06f);                                                                 // drawn forward on the ball
            }
        }
    }

    private static void Sway(ref DanceCh ch, in Beat t, bool mv)
    {
        float sw = DSin(t.R);
        ch.Px = 0.07f * sw; ch.Py = -0.02f;
        ch.Phi = -0.06f * sw; ch.Psi = 0.05f * sw; ch.PhH = 0.10f * sw; ch.ThN = -0.05f;
        float ar = mv ? 0.6f : 1f;
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            SetArm(ref ch, s, new Vector3(s * 0.06f + 0.10f * ar * DSin(t.R - 0.05f), 0.45f + 0.02f * DSin(2f * t.C), 0.04f), HUp(s));
        }
        Planted(ref ch, 0.14f, 0.15f);
        HeelUp(ref ch, -1f, 0.02f * Mathf.Max(0f, sw)); HeelUp(ref ch, 1f, 0.02f * Mathf.Max(0f, -sw));
        if (mv) { MovingScale(ref ch, 0.6f, 1f); }
    }

    private static void FolkClap(ref DanceCh ch, in Beat t, bool mv)
    {
        float b = t.B;
        float cl = DDip(b); cl = cl * cl * cl;
        float hit = (1f - b) * (1f - b) * (1f - b);
        ch.Py = -0.03f - 0.02f * hit; ch.Px = 0.03f * t.E;
        ch.Theta = 0.05f; ch.Phi = -0.03f * t.E; ch.ThN = 0.04f * DDip(b);
        ClapArms(ref ch, cl);
        Planted(ref ch, 0.13f, 0.15f);
        // the stamping foot lifts in the half beat before it lands: rig-R before beats 1 and 3, rig-L before 2 and 4
        float stamp = 0.07f * Mathf.Max(0f, -DSin(b));
        Lift(ref ch, (t.K & 1) == 1 ? 1f : -1f, stamp);
        if (mv) MovingScale(ref ch, 0.8f, 1f);
    }

    private static void HandsOnHipsSkip(ref DanceCh ch, in Beat t, bool mv)
    {
        float hop = DHop(t.B);
        bool liftedR = (t.K & 1) == 0;                          // rig-R knee up on beats 1 and 3
        ch.Py = 0.06f * hop - 0.04f; ch.Px = 0.02f * t.D;
        ch.Theta = 0.04f; ch.Phi = 0.05f * t.D; ch.Psi = 0.10f * t.D;
        Akimbo(ref ch, -1f, 0.02f * hop); Akimbo(ref ch, 1f, 0.02f * hop);
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            bool lifted = (s > 0f) == liftedR;
            if (!lifted) Foot(ref ch, s, s * 0.11f, AnkleHeight + 0.04f * hop, 0f, 0.15f, 0f);
            else Foot(ref ch, s, s * 0.11f, AnkleHeight + 0.14f * hop, 0.10f * hop, 0.15f, hop);
        }
        if (mv) MovingScale(ref ch, 0.5f, 1f);
    }
}
