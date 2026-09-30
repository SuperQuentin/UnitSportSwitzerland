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

    /// <summary>A deterministic jersey colour, so each rider in a race is distinguishable.</summary>
    public static HumanPalette ForRider(int index)
    {
        float hue = (index * 0.37f) % 1f;      // golden-ratio stride keeps neighbours apart
        return Default with
        {
            Jersey = Color.FromHsv(hue, 0.62f, 0.88f),
            Helmet = Color.FromHsv(hue, 0.25f, 0.95f),
        };
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
public static class HumanMeshBuilder
{
    /// <summary>Joint positions in metres, origin at the feet, +Z forward, +X right.</summary>
    private readonly record struct Rig(
        Vector3 HeadTop, Vector3 HeadBase, Vector3 Neck, Vector3 Chest, Vector3 Waist, Vector3 Hip,
        Vector3 ShoulderL, Vector3 ElbowL, Vector3 WristL,
        Vector3 ShoulderR, Vector3 ElbowR, Vector3 WristR,
        Vector3 HipL, Vector3 KneeL, Vector3 AnkleL, Vector3 ToeL,
        Vector3 HipR, Vector3 KneeR, Vector3 AnkleR, Vector3 ToeR,
        float TorsoLean);

    public static ArrayMesh Build(HumanPalette palette, HumanPose pose = HumanPose.Standing,
        bool includeLegs = true, bool helmet = false, Headwear hat = Headwear.None)
    {
        var scratch = new MeshScratch();
        Append(scratch, palette, pose, includeLegs, helmet, hat);
        return scratch.Build();
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
    public static ArrayMesh BuildStride(HumanPalette palette, float speed, float phase,
        bool helmet = false, Headwear hat = Headwear.None)
    {
        var scratch = new MeshScratch();
        AppendRig(scratch, palette, GaitRig(speed, phase), includeLegs: true, helmet, hat);
        return scratch.Build();
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
        float Lean, Vector3 HandL, Vector3 HandR);

    /// <summary>
    /// The mount points for one instant of the gait.
    ///
    /// <para>
    /// Computed from the same <see cref="GaitRig"/> the mesh is built from, so a head-mounted
    /// camera inherits the real stride bob rather than an approximation of it - which is the whole
    /// difference between a helmet cam and a camera floating near a head.
    /// </para>
    /// </summary>
    public static GaitMounts MountsFor(float speed, float phase) => MountsForRig(GaitRig(speed, phase));

    /// <summary>
    /// Mounts for a fixed (non-gait) pose — a cyclist, who does not run, still needs a head to
    /// hang a POV camera off and a foot to frame an ankle shot from. Shares the exact derivation
    /// <see cref="MountsFor"/> uses, so a static pose's camera points are correct by construction
    /// rather than duplicated by hand into whichever caller needed them next.
    /// </summary>
    public static GaitMounts MountsForPose(HumanPose pose) => MountsForRig(RigFor(pose));

    /// <summary>
    /// A seated rider (a motorbike) posed from the machine's three contact points, author space
    /// (+Z forward): <paramref name="seat"/> the seat surface under the pelvis, <paramref name="grip"/>
    /// the right grip (X is mirrored for the left), <paramref name="peg"/> the right footpeg.
    /// </summary>
    public static void AppendRider(MeshScratch scratch, HumanPalette palette, Vector3 seat, Vector3 grip, Vector3 peg)
    {
        var rig = RiderRig(seat, grip, peg);
        AppendRig(scratch, palette, rig, includeLegs: true, helmet: false);
        // a full-face helmet round the whole head, dark visor at the front (+Z); framed like
        // AppendHat (side, up, forward), a right-handed basis, or the box renders inside out
        var up = (rig.HeadTop - rig.HeadBase).Normalized();
        var forward = (Vector3.Back - up * up.Dot(Vector3.Back)).Normalized();
        var basis = new Basis(up.Cross(forward), up, forward);
        var centre = (rig.HeadBase + rig.HeadTop) * 0.5f + up * 0.01f;
        scratch.Box(centre, new Vector3(0.22f, 0.25f, 0.25f), palette.Helmet, basis);
        scratch.Box(centre + forward * 0.13f + up * 0.02f, new Vector3(0.17f, 0.07f, 0.03f),
            new Color(0.08f, 0.09f, 0.12f), basis);
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
            HandR: Flip(rig.WristR));

        static Vector3 Flip(Vector3 v) => new(-v.X, v.Y, -v.Z);
    }

    private static void AppendRig(MeshScratch scratch, HumanPalette palette, Rig rig,
        bool includeLegs, bool helmet, Headwear hat = Headwear.None)
    {
        // torso as a lozenge rather than a cylinder: shoulders wider than waist is most of
        // what makes a figure read as a person from behind at fifty metres
        scratch.Tube(rig.Hip, rig.Waist, 0.130f, 0.140f, palette.Shorts, 8);
        scratch.Tube(rig.Waist, rig.Chest, 0.140f, 0.158f, palette.Jersey, 8);
        scratch.Tube(rig.Chest, rig.Neck, 0.158f, 0.098f, palette.Jersey, 8);

        // shoulder caps, so the arms do not appear to sprout from the ribs
        scratch.Tube(rig.ShoulderL, rig.ShoulderR, 0.082f, palette.Jersey, 6);

        // The head follows the neck rather than a separately authored lean angle. Carrying its
        // own angle meant the head tilted independently of the body the moment a pose changed,
        // which is what left the cyclist's head hanging off the shoulders at an angle nothing
        // else shared.
        var headAxis = rig.HeadTop - rig.HeadBase;
        var headBasis = UprightBasis(headAxis);
        var headCentre = (rig.HeadBase + rig.HeadTop) * 0.5f;

        scratch.Tube(rig.Neck, rig.HeadBase, 0.052f, palette.Skin, 6);
        scratch.Box(headCentre,
            new Vector3(0.150f, headAxis.Length() + 0.055f, 0.180f), palette.Skin, headBasis);

        if (helmet)
            scratch.Box(headCentre + headBasis.Y * 0.062f,
                new Vector3(0.168f, 0.085f, 0.205f), palette.Helmet, headBasis);
        else if (hat != Headwear.None)
            AppendHat(scratch, hat, headCentre, headAxis);

        Arm(scratch, palette, rig.ShoulderL, rig.ElbowL, rig.WristL);
        Arm(scratch, palette, rig.ShoulderR, rig.ElbowR, rig.WristR);

        if (includeLegs)
        {
            Leg(scratch, palette, rig.HipL, rig.KneeL, rig.AnkleL, rig.ToeL);
            Leg(scratch, palette, rig.HipR, rig.KneeR, rig.AnkleR, rig.ToeR);
        }
    }

    /// <summary>
    /// A hat on the head, built in the head's own frame so it follows the neck like the helmet
    /// does. The figure is authored facing +Z, so "forward" is +Z made square to the head.
    /// </summary>
    private static void AppendHat(MeshScratch s, Headwear hat, Vector3 centre, Vector3 axis)
    {
        var up = axis.LengthSquared() > 1e-8f ? axis.Normalized() : Vector3.Up;
        var fwd = Vector3.Back - up * up.Dot(Vector3.Back);
        fwd = fwd.LengthSquared() > 1e-6f ? fwd.Normalized() : Vector3.Back;
        var side = up.Cross(fwd);
        var frame = new Basis(side, up, fwd);
        var top = centre + up * (axis.Length() * 0.5f + 0.0275f);

        switch (hat)
        {
            case Headwear.WitchHat:
            {
                var black = new Color(0.10f, 0.08f, 0.12f);
                s.Tube(top - up * 0.005f, top + up * 0.012f, 0.21f, 0.20f, black, 10);   // brim
                s.Tube(top, top + up * 0.05f, 0.105f, 0.095f, new Color(0.45f, 0.20f, 0.60f), 8);
                var knee = top + up * 0.20f - fwd * 0.02f;
                s.Tube(top + up * 0.05f, knee, 0.095f, 0.05f, black, 8);
                s.Tube(knee, knee + up * 0.08f - fwd * 0.10f, 0.05f, 0.006f, black, 6);   // the tip, bent back
                break;
            }
            case Headwear.PumpkinHead:
            {
                var orange = new Color(0.93f, 0.44f, 0.07f);
                var c = centre + up * 0.01f;
                s.Tube(c - up * 0.12f, c, 0.12f, 0.155f, orange, 8);
                s.Tube(c, c + up * 0.13f, 0.155f, 0.10f, orange, 8);
                var glow = new Color(1f, 0.85f, 0.25f);
                s.Box(c + up * 0.04f + fwd * 0.145f + side * 0.055f, new Vector3(0.05f, 0.04f, 0.03f), glow, frame);
                s.Box(c + up * 0.04f + fwd * 0.145f - side * 0.055f, new Vector3(0.05f, 0.04f, 0.03f), glow, frame);
                s.Box(c - up * 0.045f + fwd * 0.145f, new Vector3(0.12f, 0.03f, 0.03f), glow, frame);
                s.Tube(c + up * 0.12f, c + up * 0.19f, 0.02f, 0.014f, new Color(0.28f, 0.26f, 0.10f), 5);
                break;
            }
            case Headwear.SantaHat:
            {
                var red = new Color(0.80f, 0.10f, 0.12f);
                var white = new Color(0.95f, 0.95f, 0.93f);
                s.Tube(top - up * 0.03f, top + up * 0.02f, 0.11f, 0.11f, white, 10);   // fur trim
                var bend = top + up * 0.15f - fwd * 0.04f;
                s.Tube(top + up * 0.02f, bend, 0.10f, 0.05f, red, 8);
                var tip = bend - fwd * 0.11f - up * 0.05f;                             // flops over the back
                s.Tube(bend, tip, 0.05f, 0.012f, red, 6);
                s.Box(tip, new Vector3(0.055f, 0.055f, 0.055f), white, frame);
                break;
            }
            case Headwear.ReindeerAntlers:
            {
                var antler = new Color(0.45f, 0.30f, 0.16f);
                foreach (float sgn in new[] { -1f, 1f })
                {
                    var root = top - up * 0.02f + side * sgn * 0.055f;
                    var fork = root + up * 0.12f + side * sgn * 0.06f;
                    s.Tube(root, fork, 0.016f, 0.012f, antler, 5);
                    s.Tube(fork, fork + up * 0.08f + side * sgn * 0.04f - fwd * 0.02f, 0.012f, 0.006f, antler, 5);
                    s.Tube(fork, fork + up * 0.05f + fwd * 0.06f, 0.011f, 0.005f, antler, 5);
                    s.Tube(root + (fork - root) * 0.45f, root + (fork - root) * 0.45f + fwd * 0.06f + up * 0.03f, 0.010f, 0.005f, antler, 4);
                }
                s.Box(centre + fwd * 0.085f - up * 0.01f, new Vector3(0.04f, 0.04f, 0.035f), new Color(0.90f, 0.08f, 0.08f), frame);
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

    private static void Arm(MeshScratch s, HumanPalette p, Vector3 shoulder, Vector3 elbow, Vector3 wrist)
    {
        s.Tube(shoulder, elbow, 0.058f, 0.045f, p.Jersey, 6);   // sleeve
        s.Tube(elbow, wrist, 0.045f, 0.033f, p.Skin, 6);
        s.Box(wrist, new Vector3(0.055f, 0.075f, 0.085f), p.Skin);
    }

    private static void Leg(MeshScratch s, HumanPalette p, Vector3 hip, Vector3 knee,
        Vector3 ankle, Vector3 toe)
    {
        s.Tube(hip, knee, 0.088f, 0.062f, p.Shorts, 6);
        s.Tube(knee, ankle, 0.062f, 0.040f, p.Skin, 6);

        // the foot points along ankle→toe, so it follows the pose without extra bookkeeping
        var forward = (toe - ankle);
        if (forward.LengthSquared() > 1e-6f)
            s.Tube(ankle, toe, 0.048f, 0.038f, p.Shoes, 5);
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

    /// <summary>
    /// Unlit, vertex-coloured, backface-culled. Matches how the rest of the world is shaded:
    /// the terrain gets its form from flat facets and dither, not from specular highlights.
    /// </summary>
    public static StandardMaterial3D Material() => new()
    {
        VertexColorUseAsAlbedo = true,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
        SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
        Roughness = 1f,
    };
}
