using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Breakdance (#728): the moves that leave the standing pose. Toprock is an ordinary move (channels
/// over the gait, <see cref="Toprock"/>); everything on the floor (the six-step, the windmill, the
/// headspin, the freeze) cannot be a pelvis shift and a lean of the standing figure, so each builds
/// the whole <see cref="Rig"/> itself: the torso turned any way (<see cref="Posed"/>), the hands and
/// feet put where they touch the floor, the limbs solved to them, and the whole body spun about the
/// vertical when it turns (<see cref="Spun"/>). A set is two move slots long
/// (<see cref="BreakDown"/> then <see cref="BreakPower"/>), so the caller picks it rarely and keeps
/// the second slot for it; like every move, a pure function of the beat clock.
/// </summary>
public static partial class HumanMeshBuilder
{
    /// <summary>
    /// <see cref="DanceParams.Move"/> values of a break set (#728), outside any style's table:
    /// <see cref="BreakDown"/> (toprock, drop, six-step) is always followed by one of the power
    /// slots (a windmill or a headspin, then the freeze and back up).
    /// </summary>
    public const int BreakMoves = 4000;
    public const int BreakDown = BreakMoves;
    public const int BreakPowerWindmill = BreakMoves + 1;
    public const int BreakPowerHeadspin = BreakMoves + 2;

    /// <summary>A move number that is the second half of a break set.</summary>
    public static bool IsBreakPower(int move) => move is BreakPowerWindmill or BreakPowerHeadspin;

    private static bool IsFloor(DanceMove m) => m is DanceMove.BreakDown or DanceMove.BreakWindmill or DanceMove.BreakHeadspin;

    private static DanceMove BreakResolve(int index) => index switch
    {
        BreakPowerWindmill => DanceMove.BreakWindmill,
        BreakPowerHeadspin => DanceMove.BreakHeadspin,
        _ => DanceMove.BreakDown,
    };

    /// <summary>
    /// A floor move's whole rig at this instant, from the standing <paramref name="stand"/> (what
    /// the figure gets up into) and the slot clock <paramref name="beats"/> (0..8, two bars).
    /// </summary>
    private static Rig FloorRig(DanceMove move, in Rig stand, float beats, in Beat t)
    {
        switch (move)
        {
            case DanceMove.BreakDown:
            {
                // bar 1: toprock; beats 4-5: down to the floor; 5.5-8: the six-step
                if (beats < 4f) return Compose(stand, ToprockCh(t), 1f, false);
                var six = SixStep((beats - 5f) / 3f);
                if (beats >= 5f) return six;
                var top = Compose(stand, ToprockCh(t), 1f, false);
                return MixRigs(top, Crouch(), DSm(beats - 4f));
            }
            default:
            {
                // beats 0-3.7: the power move; 3.7-4: into the freeze, hit on the bar's one and held;
                // 7-8: back up to standing
                bool head = move == DanceMove.BreakHeadspin;
                if (beats < 3.7f)
                {
                    // the first half beat comes out of the six-step's crouch
                    var power = head ? Headspin(beats) : Windmill(beats);
                    return beats < 0.5f ? MixRigs(SixStep(1f), power, DSm(beats * 2f)) : power;
                }
                var freeze = Freeze(beats - 4f);
                if (beats < 4f) return MixRigs(head ? Headspin(3.7f) : Windmill(3.7f), freeze, DSm((beats - 3.7f) / 0.3f));
                if (beats < 7f) return freeze;
                return MixRigs(freeze, Compose(stand, ToprockCh(t), 1f, false), DSm(beats - 7f));
            }
        }
    }

    // ---- toprock -------------------------------------------------------------------------------

    /// <summary>
    /// Toprock, the upright opening of a set: on each beat one foot crosses in front of the other and
    /// steps back out (right on 1, left on 3), the body leaning into it, the arms in a loose open
    /// guard that swings across with the step. Also danced on its own (HipHop, Electronic).
    /// </summary>
    private static void Toprock(ref DanceCh ch, in Beat t, bool mv)
    {
        float hit = DDip(t.B);
        // which foot crosses: rig-L (the figure's right) on beats 1 and 2, rig-R on 3 and 4
        float side = t.K < 2 ? -1f : 1f;
        // in on the beat's first half (1, 3), back out on the second (2, 4)
        float cross = (t.K & 1) == 0 ? DSm(Mathf.Min(1f, t.B * 2.2f)) : 1f - DSm(Mathf.Min(1f, t.B * 2.2f));
        ch.Py = -0.06f - 0.03f * hit;
        ch.Theta = 0.14f + 0.04f * hit;
        ch.Px = 0.04f * side * cross;
        ch.Psi = -0.30f * side * cross;           // the chest turns away from the crossing leg
        ch.Phi = 0.05f * side * cross;
        ch.ThN = 0.10f * hit;
        for (int i = 0; i < 2; i++)
        {
            float s = i * 2f - 1f;
            bool crossing = s == side;
            float x = crossing ? s * Mathf.Lerp(0.15f, -0.10f, cross) : s * 0.16f;
            float z = crossing ? Mathf.Lerp(0f, 0.20f, cross) : -0.03f * cross;
            Foot(ref ch, s, x, AnkleHeight + (crossing ? 0.05f * DSin(Mathf.Min(1f, t.B * 2.2f) * 0.5f) : 0f), z,
                crossing ? -0.45f * cross : 0.25f, 0f);
            // open guard: forearms up, fists at the chest's height, swinging across with the twist
            float swing = -s * side * cross;
            Aim(ref ch, s, 0.10f + 0.08f * swing, -0.12f + 0.06f * hit, 0.28f - 0.06f * swing, new Vector3(s, -0.6f, -0.4f));
        }
        if (mv) MovingScale(ref ch, 0.5f, 0.8f);
    }

    private static DanceCh ToprockCh(in Beat t)
    {
        var ch = default(DanceCh);
        ch.ArmBlend = 1f;
        Planted(ref ch, 0.098f, 0f);
        Toprock(ref ch, t, false);
        return ch;
    }

    // ---- the floor -----------------------------------------------------------------------------

    /// <summary>
    /// A whole rig from a torso turned by <paramref name="torso"/> (its +Y up the spine, +Z the chest's
    /// front) with the pelvis at <paramref name="hip"/>, and the hands and ankles put at targets
    /// (clamped to what the limbs reach), the elbows and knees bending toward their hints.
    /// <paramref name="nod"/> tips the head forward along the spine.
    /// </summary>
    private static Rig Posed(Vector3 hip, Basis torso, float nod,
        Vector3 wristL, Vector3 elbowHintL, Vector3 wristR, Vector3 elbowHintR,
        Vector3 ankleL, Vector3 kneeHintL, Vector3 ankleR, Vector3 kneeHintR,
        Vector3 toeDirL, Vector3 toeDirR)
    {
        var waist = hip + torso * new Vector3(0f, 0.155f, 0f);
        var chest = hip + torso * new Vector3(0f, 0.410f, 0f);
        var neck = hip + torso * new Vector3(0f, 0.590f, 0f);
        var shL = hip + torso * new Vector3(-0.18f, 0.510f, 0f);
        var shR = hip + torso * new Vector3(0.18f, 0.510f, 0f);
        var hipL = hip + torso * new Vector3(-0.09f, 0f, 0f);
        var hipR = hip + torso * new Vector3(0.09f, 0f, 0f);
        var u = (torso * new Vector3(0f, Mathf.Cos(nod), Mathf.Sin(nod))).Normalized();
        wristL = Reach(shL, wristL, 0.50f);
        wristR = Reach(shR, wristR, 0.50f);
        ankleL = Reach(hipL, ankleL, 0.845f);
        ankleR = Reach(hipR, ankleR, 0.845f);
        var elbowL = Limb.Solve(shL, wristL, UpperArmLength, ForearmLength, elbowHintL);
        var elbowR = Limb.Solve(shR, wristR, UpperArmLength, ForearmLength, elbowHintR);
        var kneeL = Limb.Solve(hipL, ankleL, ThighLength, ShinLength, kneeHintL);
        var kneeR = Limb.Solve(hipR, ankleR, ThighLength, ShinLength, kneeHintR);
        return new Rig(
            HeadTop: neck + u * 0.255f, HeadBase: neck + u * 0.065f, Neck: neck,
            Chest: chest, Waist: waist, Hip: hip,
            ShoulderL: shL, ElbowL: elbowL, WristL: wristL,
            ShoulderR: shR, ElbowR: elbowR, WristR: wristR,
            HipL: hipL, KneeL: kneeL, AnkleL: ankleL, ToeL: ankleL + toeDirL.Normalized() * 0.145f,
            HipR: hipR, KneeR: kneeR, AnkleR: ankleR, ToeR: ankleR + toeDirR.Normalized() * 0.145f,
            TorsoLean: 0f);
    }

    /// <summary>The whole rig turned by <paramref name="yaw"/> about the vertical through <paramref name="pivot"/>.</summary>
    private static Rig Spun(in Rig r, float yaw, Vector3 pivot)
    {
        var b = new Basis(Vector3.Up, yaw);
        Vector3 S(Vector3 v) => pivot + b * (v - pivot);
        return r with
        {
            HeadTop = S(r.HeadTop), HeadBase = S(r.HeadBase), Neck = S(r.Neck), Chest = S(r.Chest),
            Waist = S(r.Waist), Hip = S(r.Hip),
            ShoulderL = S(r.ShoulderL), ElbowL = S(r.ElbowL), WristL = S(r.WristL),
            ShoulderR = S(r.ShoulderR), ElbowR = S(r.ElbowR), WristR = S(r.WristR),
            HipL = S(r.HipL), KneeL = S(r.KneeL), AnkleL = S(r.AnkleL), ToeL = S(r.ToeL),
            HipR = S(r.HipR), KneeR = S(r.KneeR), AnkleR = S(r.AnkleR), ToeR = S(r.ToeR),
        };
    }

    /// <summary>A torso basis: pitched forward by <paramref name="pitch"/> (rad, + = chest toward the floor), then rolled, then turned.</summary>
    private static Basis BodyTurn(float pitch, float roll = 0f, float yaw = 0f) =>
        new Basis(Vector3.Up, yaw) * new Basis(Vector3.Back, roll) * new Basis(Vector3.Right, pitch);

    /// <summary>Down on the floor, ready for the footwork: squatting low on the balls of the feet, a hand down on each side.</summary>
    private static Rig Crouch() =>
        Posed(new Vector3(0f, 0.42f, -0.05f), BodyTurn(0.75f), 0.25f,
            new Vector3(-0.30f, 0.05f, 0.30f), new Vector3(-1f, 0f, -0.3f),
            new Vector3(0.30f, 0.05f, 0.30f), new Vector3(1f, 0f, -0.3f),
            new Vector3(-0.20f, AnkleHeight + 0.03f, 0.05f), new Vector3(-0.4f, 0.3f, 1f),
            new Vector3(0.20f, AnkleHeight + 0.03f, 0.05f), new Vector3(0.4f, 0.3f, 1f),
            new Vector3(-0.2f, -0.4f, 1f), new Vector3(0.2f, -0.4f, 1f));

    /// <summary>
    /// The six-step, one round per two beats (<paramref name="u"/> counts rounds): low on the hands,
    /// the legs walking a circle round them. Steps 1-3 carry the legs from the front round the
    /// figure's right to behind, 4-6 back round the left; the hand on the side the legs pass lifts
    /// to let them by, the body turning a little toward them.
    /// </summary>
    private static Rig SixStep(float u)
    {
        u = Mathf.Max(0f, u);
        float a = Mathf.Tau * DFrac(u);                       // where the legs are on their circle, 0 = in front
        var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
        var across = new Vector3(dir.Z, 0f, -dir.X);
        // the pivot is between the hands; the legs reach 0.5-0.7 m out from it, one a little ahead
        var c = new Vector3(0f, 0f, 0.05f);
        float cross = DSin(u * 2f);                           // the legs cross over twice a round
        var footL = c + dir * (0.78f + 0.08f * cross) - across * 0.16f * (1f - Mathf.Abs(cross));
        var footR = c + dir * (0.86f - 0.08f * cross) + across * 0.16f * (1f - Mathf.Abs(cross));
        footL.Y = footR.Y = AnkleHeight + 0.01f;
        // the pelvis sits up off the floor, leaning back from the legs; the torso leans over the hands
        var hip = c + dir * 0.30f + new Vector3(0f, 0.30f, 0f);
        float yaw = 0.35f * Mathf.Sin(a);
        var torso = BodyTurn(0.35f, -0.30f * Mathf.Sin(a), yaw);
        // the hand on the side the legs pass is lifted off the floor and in
        float liftL = Mathf.Max(0f, Mathf.Sin(a)), liftR = Mathf.Max(0f, -Mathf.Sin(a));
        var handL = new Vector3(-0.28f, 0.04f + 0.30f * liftL, 0.0f - 0.05f * liftL);
        var handR = new Vector3(0.28f, 0.04f + 0.30f * liftR, 0.0f - 0.05f * liftR);
        return Posed(hip, torso, 0.30f,
            handL, new Vector3(-1f, 0.2f, -0.5f), handR, new Vector3(1f, 0.2f, -0.5f),
            footL, new Vector3(-0.3f, 0.6f, 0.4f) + dir, footR, new Vector3(0.3f, 0.6f, 0.4f) + dir,
            dir + Vector3.Down * 0.3f, dir + Vector3.Down * 0.3f);
    }

    /// <summary>
    /// The windmill: on the upper back and shoulders, the body rolling round from back to chest
    /// and over again while the legs, wide in a V, sweep round in the air. One turn per two beats.
    /// </summary>
    private static Rig Windmill(float beats)
    {
        float spin = Mathf.Tau * beats * 0.5f;
        float roll = 0.55f * Mathf.Sin(spin * 2f);           // back to shoulder to chest, twice a turn
        // lying on the back: the spine nearly flat (pitched back past horizontal), shoulders at the pivot
        var torso = BodyTurn(-1.45f, roll);
        var shoulders = new Vector3(0f, 0.16f, 0f);
        var hip = shoulders - torso * new Vector3(0f, 0.51f, 0f);
        hip.Y = Mathf.Max(hip.Y, 0.30f);
        var legUp = (torso * Vector3.Down).Normalized() + Vector3.Up * 0.9f;
        var side = torso * Vector3.Right;
        var ankleL = hip + (legUp.Normalized() - side * 0.75f).Normalized() * 0.84f;
        var ankleR = hip + (legUp.Normalized() + side * 0.75f).Normalized() * 0.84f;
        // one arm tucked across the belly, the other out on the floor to push the turn
        var handL = hip + torso * new Vector3(0.05f, 0.30f, 0.12f);
        var handR = new Vector3(0.45f, 0.05f, 0.10f);
        var rig = Posed(hip, torso, -0.2f,
            handL, torso * new Vector3(-1f, 0f, 0.3f), handR, new Vector3(0.6f, 0.6f, 0f),
            ankleL, legUp, ankleR, legUp, legUp, legUp);
        return Spun(rig, spin, shoulders);
    }

    /// <summary>The headspin: upside down on the crown, the legs straight up in a V, spinning a turn a beat; the hands tripod for the first beat.</summary>
    private static Rig Headspin(float beats)
    {
        float spin = Mathf.Tau * beats;
        var torso = BodyTurn(Mathf.Pi);                          // the spine straight down, chest to the back
        var neck = new Vector3(0f, 0.255f, 0f);
        var hip = neck - torso * new Vector3(0f, 0.59f, 0f);
        float tripod = 1f - DSm(beats - 0.5f);                // hands on the floor, then out to the sides
        var handL = new Vector3(-0.24f, 0.04f, 0.12f).Lerp(new Vector3(-0.50f, 0.55f, 0f), 1f - tripod);
        var handR = new Vector3(0.24f, 0.04f, 0.12f).Lerp(new Vector3(0.50f, 0.55f, 0f), 1f - tripod);
        var ankleL = hip + new Vector3(-0.30f, 0.79f, 0f);
        var ankleR = hip + new Vector3(0.30f, 0.79f, 0f);
        var rig = Posed(hip, torso, 0f,
            handL, new Vector3(-1f, 0.4f, -0.3f), handR, new Vector3(1f, 0.4f, -0.3f),
            ankleL, Vector3.Forward, ankleR, Vector3.Forward, Vector3.Up, Vector3.Up);
        return Spun(rig, spin, Vector3.Zero);
    }

    /// <summary>
    /// The chair freeze, hit on the bar's one and held: balanced on the figure's right hand (rig-L)
    /// with the elbow tucked into the hip, the body tipped on its side, one leg bent with its foot
    /// on the other knee (the "4"), the free hand thrown up. A little settle as it lands.
    /// </summary>
    private static Rig Freeze(float since)
    {
        float settle = 0.03f * Mathf.Exp(-Mathf.Max(0f, since) * 6f);
        // the spine runs down toward the figure's right at 30 degrees, chest to the front: the head
        // ends near the floor beside the hand, the hips high over the tucked elbow
        var torso = BodyTurn(0.10f, 2.09f);
        var hip = new Vector3(0.05f, 0.50f + settle, 0f);
        var hand = new Vector3(-0.12f, 0.04f, 0.06f);
        var up = Vector3.Up;
        var right = new Vector3(1f, 0f, 0f);
        // the straight leg points up and away; the bent one folds its foot onto the other knee (the "4")
        var ankleR = hip + (up * 0.72f + right * 0.40f + Vector3.Back * 0.05f);
        var ankleL = hip + (up * 0.40f + right * 0.05f + Vector3.Back * 0.25f);
        var freeHand = hip + new Vector3(-0.05f, 0.55f, 0.20f);
        return Posed(hip, torso, 0.35f,
            hand, new Vector3(0.6f, 0.4f, -0.4f), freeHand, new Vector3(0.5f, 0.2f, -0.5f),
            ankleL, new Vector3(0.2f, 0.4f, 1f), ankleR, new Vector3(0f, 0.2f, 1f),
            new Vector3(0.6f, 0.3f, 0.3f), new Vector3(0.2f, 1f, 0.2f));
    }
}
