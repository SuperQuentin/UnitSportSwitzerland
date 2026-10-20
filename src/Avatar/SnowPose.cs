using System;
using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The live skier's and snowboarder's body (#933): joint positions computed from a handful of
/// numbers instead of one baked crouch, so the figure works. Pure maths (Godot's structs only),
/// unit-tested; <see cref="SnowRig"/> draws it.
///
/// <para>
/// Joints are in author space (+Z forward, origin at the feet, the order of
/// <see cref="HumanMeshBuilder.PoseJoints"/>). The torso is a pelvis height and a pitch, the legs
/// and arms two-bone solves from the hip and shoulders to the feet and hands, so a bone never
/// changes length whatever the pose asks of it.
/// </para>
/// </summary>
public static class SnowPose
{
    /// <summary>What the body is doing.</summary>
    /// <param name="Crouch">0 an athletic stance .. 1 the schuss tuck (an egg).</param>
    /// <param name="Lean">The ride's lean, rad (<c>RideMotion.Lean</c>): the whole figure is rolled by it; the upper body takes some back (angulation) and the inside hand plants its pole.</param>
    /// <param name="Plough">0..1, skis only: the tails pushed out into a V.</param>
    /// <param name="Stride">0 none, else ±(1 + phase), phase 0..1 of one push; the sign is the leg that pushes (+ the +X one).</param>
    /// <param name="Absorb">0..1: a landing taken in the knees.</param>
    /// <param name="Air">In the air: arms out for balance.</param>
    public readonly record struct Look(float Crouch, float Lean, float Plough, float Stride, float Absorb, bool Air)
    {
        /// <summary>A key of the pose at the steps it is drawn at, so an unchanged pose is not rebuilt.</summary>
        public (int, int, int, int, int, bool) Key => (
            (int)MathF.Round(Crouch * 16f), (int)MathF.Round(Lean * 20f), (int)MathF.Round(Plough * 10f),
            (int)MathF.Round(Stride * 12f), (int)MathF.Round(Absorb * 8f), Air);
    }

    /// <summary>
    /// A look as the ride pose a peer sends (<c>FootPlayer.Anim</c>): X the crouch plus the landing
    /// in eighths, two apart; Y the plough, plus 2 in the air; Z the stride; W the lean.
    /// </summary>
    public static Vector4 Pack(in Look look) =>
        new(MathF.Round(Math.Clamp(look.Absorb, 0f, 1f) * 8f) * 2f + Math.Clamp(look.Crouch, 0f, 1f),
            Math.Clamp(look.Plough, 0f, 1f) + (look.Air ? 2f : 0f), look.Stride, look.Lean);

    public static Look Unpack(Vector4 pose)
    {
        // the crouch is 0..1 and the landing steps two apart, so the two never overlap
        float steps = MathF.Floor(pose.X / 2f);
        float crouch = pose.X - steps * 2f;
        bool air = pose.Y >= 1.5f;
        return new Look(crouch, pose.W, air ? pose.Y - 2f : pose.Y, pose.Z, steps / 8f, air);
    }

    public const float Thigh = 0.43f, Shin = 0.40f, UpperArm = 0.27f, Forearm = 0.25f;
    /// <summary>Ankle height over the snow, standing in a boot on a ski.</summary>
    public const float AnkleY = 0.10f;

    /// <summary>Where a ski sits under each foot: its boot, and its yaw (+ turns the tip toward +X).</summary>
    public readonly record struct Ski(Vector3 Boot, float Yaw, float Lift);

    /// <summary>A pole, grip to basket.</summary>
    public readonly record struct Pole(Vector3 Grip, Vector3 Basket);

    // indices in the joint table
    public const int HeadTop = 0, HeadBase = 1, Neck = 2, Chest = 3, Waist = 4, Hip = 5,
        ShoulderL = 6, ElbowL = 7, WristL = 8, ShoulderR = 9, ElbowR = 10, WristR = 11,
        HipL = 12, KneeL = 13, AnkleL = 14, ToeL = 15, HipR = 16, KneeR = 17, AnkleR = 18, ToeR = 19;

    /// <summary>
    /// The skier: joints into <paramref name="j"/> (20), the two skis (−X then +X) and two poles.
    /// </summary>
    public static void Skier(in Look look, Span<Vector3> j, Span<Ski> skis, Span<Pole> poles)
    {
        float c = Math.Clamp(look.Crouch, 0f, 1f), plough = Math.Clamp(look.Plough, 0f, 1f);
        float absorb = Math.Clamp(look.Absorb, 0f, 1f);
        float leg = MathF.Sign(look.Stride), phase = leg == 0 ? 0f : Math.Clamp(MathF.Abs(look.Stride) - 1f, 0f, 1f);
        // a push: out over the first two thirds, the ski lifted and brought back in the last
        float push = leg == 0 ? 0f : MathF.Sin(MathF.PI * MathF.Min(phase * 1.5f, 1f));
        float recover = leg == 0 ? 0f : MathF.Max(0f, phase * 3f - 2f);

        // ---- the feet, and the skis under them ------------------------------------------------
        for (int side = 0; side < 2; side++)
        {
            float sx = side == 0 ? -1f : 1f;
            bool pushing = leg == sx;
            float x = sx * (0.11f + 0.11f * plough + (pushing ? 0.26f * push : 0f));
            float z = 0.10f - (pushing ? 0.22f * push : 0f);
            float lift = pushing ? 0.20f * recover * (1f - recover) + 0.03f * push : 0f;
            // the plough turns the tips in; a skating ski turns its tip out
            float yaw = -sx * 0.32f * plough + (pushing ? sx * 0.42f * push : 0f);
            var ankle = new Vector3(x, AnkleY + lift, z);
            j[side == 0 ? AnkleL : AnkleR] = ankle;
            j[side == 0 ? ToeL : ToeR] = ankle + new Basis(Vector3.Up, yaw) * new Vector3(0, -0.01f, 0.14f);
            skis[side] = new Ski(ankle with { Y = ankle.Y - AnkleY }, yaw, lift);
        }

        // ---- the torso: a pelvis height and a pitch ---------------------------------------------
        float hipY = Mathf.Lerp(0.80f, 0.60f, c) - 0.16f * absorb - 0.05f * plough - 0.04f * push;
        float hipZ = Mathf.Lerp(-0.04f, -0.16f, c) - 0.05f * absorb;
        float pitch = Mathf.Lerp(0.45f, 1.15f, c) + 0.2f * absorb + 0.15f * push;
        var hip = new Vector3(0, hipY, hipZ);
        Torso(j, hip, pitch, 0.5f);

        // ---- legs: knees forward, a little out in a plough's A-frame ----------------------------
        for (int side = 0; side < 2; side++)
        {
            float sx = side == 0 ? -1f : 1f;
            var top = j[side == 0 ? HipL : HipR];
            var ankle = j[side == 0 ? AnkleL : AnkleR];
            j[side == 0 ? KneeL : KneeR] = TwoBone(top, ankle, Thigh, Shin, new Vector3(-sx * 0.25f * plough, 0, 1));
        }

        // ---- hands: out in front; a pole planted inside a turn; pushing back while skating ------
        // the ride rolls the drawn body by the lean about its +Z; the body is author space turned
        // half round ((−x, y, −z), MeshScratch.Build), so in author space a + lean takes the head
        // toward +X: the inside of the turn
        float inside = MathF.Sign(look.Lean), plant = Math.Clamp(MathF.Abs(look.Lean) * 1.4f, 0f, 1f);
        float shoulderY = j[ShoulderL].Y;
        Span<Vector3> dirs = stackalloc Vector3[2];
        for (int side = 0; side < 2; side++)
        {
            float sx = side == 0 ? -1f : 1f;
            var wrist = new Vector3(sx * Mathf.Lerp(0.30f, 0.11f, c), Mathf.Lerp(hipY + 0.17f, shoulderY - 0.08f, c), Mathf.Lerp(0.48f, 0.66f, c));
            if (sx == inside) wrist += new Vector3(0, -0.10f, 0.12f) * plant;
            else wrist += new Vector3(sx * 0.06f, 0.06f, -0.04f) * plant;
            // both poles push together while skating: the hands swing back past the hips
            if (leg != 0) wrist += new Vector3(0, -0.12f * push, -0.55f * push + 0.12f * recover);
            if (look.Air) wrist += new Vector3(sx * 0.18f, 0.10f, -0.05f);
            Arm(j, side, wrist, sx);

            // the pole, 1.2 m: trailing, planted ahead inside a turn, pushing back, under the arm in a tuck
            var dir = new Vector3(sx * 0.25f, -1f, -0.50f);
            if (sx == inside) dir = dir.Lerp(new Vector3(sx * 0.12f, -1f, 0.30f), plant);
            if (leg != 0) dir = dir.Lerp(new Vector3(sx * 0.15f, -0.75f, -1f), push);
            dirs[side] = dir.Lerp(new Vector3(sx * 0.05f, 0.12f, -1f), c).Normalized();
        }

        Angulate(j, hip, look.Lean, 0.45f);
        // the poles from where the hands ended up, turned with the upper body
        var turn = new Basis(Vector3.Back, look.Lean * 0.45f);
        for (int side = 0; side < 2; side++)
        {
            var grip = j[side == 0 ? WristL : WristR];
            var basket = grip + turn * dirs[side] * 1.15f;
            // a basket under the snow stops on it
            if (basket.Y < 0.03f) basket = grip + (basket - grip) * ((grip.Y - 0.03f) / Mathf.Max(grip.Y - basket.Y, 1e-3f));
            poles[side] = new Pole(grip, basket);
        }
    }

    /// <summary>
    /// The snowboarder, in its own frame (<see cref="HumanPose.Boarding"/>'s: chest to +Z, the board
    /// along X): joints into <paramref name="j"/>. <paramref name="noseX"/> is the side of the board's
    /// nose (+1 or −1); the back foot pushes. <paramref name="toe"/> is −1 (on the heels) .. 1 (on the toes).
    /// </summary>
    public static void Boarder(in Look look, float toe, float noseX, Span<Vector3> j)
    {
        float c = Math.Clamp(look.Crouch, 0f, 1f), absorb = Math.Clamp(look.Absorb, 0f, 1f);
        toe = Math.Clamp(toe, -1f, 1f);
        float phase = look.Stride == 0 ? 0f : Math.Clamp(MathF.Abs(look.Stride) - 1f, 0f, 1f);
        float push = look.Stride == 0 ? 0f : MathF.Sin(MathF.PI * phase);
        float back = -MathF.Sign(noseX == 0 ? 1f : noseX);

        // the front foot strapped in; the back one too unless it is out pushing beside the board
        j[noseX > 0 ? AnkleR : AnkleL] = new Vector3(-back * 0.27f, AnkleY, 0);
        var rear = new Vector3(back * 0.27f, AnkleY, 0);
        if (look.Stride != 0)
        {
            // out of its binding, on the snow in front of the toe edge, sweeping toward the tail
            float sweep = phase < 0.6f ? phase / 0.6f : 1f - (phase - 0.6f) / 0.4f;
            rear = new Vector3(back * Mathf.Lerp(0.08f, 0.50f, sweep), AnkleY - 0.04f + (phase > 0.6f ? 0.10f * MathF.Sin(MathF.PI * (phase - 0.6f) / 0.4f) : 0f), 0.30f);
        }
        j[noseX > 0 ? AnkleL : AnkleR] = rear;
        j[ToeL] = j[AnkleL] + new Vector3(0, -0.01f, 0.14f);
        j[ToeR] = j[AnkleR] + new Vector3(0, -0.01f, 0.14f);

        // an edge is set with the knees: lower on either; on the heels the rider sits back
        float hipY = Mathf.Lerp(0.74f, 0.60f, c) - 0.14f * absorb - 0.07f * MathF.Abs(toe) - 0.04f * MathF.Max(-toe, 0f) - 0.05f * push;
        float hipZ = 0.04f * MathF.Max(toe, 0f) - 0.10f * MathF.Max(-toe, 0f) + (look.Stride != 0 ? 0.06f : 0f);
        float pitch = 0.22f + 0.55f * c + 0.15f * absorb + 0.2f * push;
        var hip = new Vector3(look.Stride != 0 ? -back * 0.06f : 0f, hipY, hipZ);
        Torso(j, hip, pitch, 0.7f);
        j[KneeL] = TwoBone(j[HipL], j[AnkleL], Thigh, Shin, new Vector3(-0.35f, 0, 1));
        j[KneeR] = TwoBone(j[HipR], j[AnkleR], Thigh, Shin, new Vector3(0.35f, 0, 1));

        // arms out along the board for balance, the front one leading; in toward the knees in a tuck
        for (int side = 0; side < 2; side++)
        {
            float sx = side == 0 ? -1f : 1f;
            bool front = sx == -back;
            var wrist = new Vector3(sx * Mathf.Lerp(front ? 0.62f : 0.55f, 0.22f, c), Mathf.Lerp(hipY + 0.12f, hipY + 0.05f, c),
                Mathf.Lerp(0.18f, 0.42f, c) + 0.10f * toe);
            if (look.Air) wrist += new Vector3(sx * 0.08f, 0.22f, 0);
            Arm(j, side, wrist, sx);
        }

        // angulation, as the skier's: the whole figure rolls with the board onto its edge (toward
        // the chest on the toes), and the body above the hips takes part of it back
        float roll = toe * 1.1f * 0.5f;
        if (MathF.Abs(roll) > 1e-4f)
        {
            var upright = new Basis(Vector3.Right, -roll);
            for (int i = HeadTop; i <= WristR; i++)
                if (i != Hip) j[i] = hip + upright * (j[i] - hip);
        }
    }

    /// <summary>The pelvis, spine, head and shoulders from a hip point and a forward pitch.</summary>
    private static void Torso(Span<Vector3> j, Vector3 hip, float pitch, float headUp)
    {
        var spine = new Basis(Vector3.Right, pitch);
        j[Hip] = hip;
        j[Waist] = hip + spine * new Vector3(0, 0.125f, 0);
        j[Chest] = hip + spine * new Vector3(0, 0.38f, 0);
        j[Neck] = hip + spine * new Vector3(0, 0.56f, 0);
        j[HeadBase] = hip + spine * new Vector3(0, 0.625f, 0);
        // the head lifted back toward level to look down the run, not at the snow
        j[HeadTop] = j[HeadBase] + new Basis(Vector3.Right, pitch * (1f - headUp)) * new Vector3(0, 0.19f, 0);
        j[ShoulderL] = hip + spine * new Vector3(-0.18f, 0.48f, 0);
        j[ShoulderR] = hip + spine * new Vector3(0.18f, 0.48f, 0);
        j[HipL] = hip + new Vector3(-0.09f, 0, 0);
        j[HipR] = hip + new Vector3(0.09f, 0, 0);
    }

    private static void Arm(Span<Vector3> j, int side, Vector3 wrist, float sx)
    {
        var shoulder = j[side == 0 ? ShoulderL : ShoulderR];
        // elbows out and back
        var elbow = TwoBone(shoulder, wrist, UpperArm, Forearm, new Vector3(sx * 0.6f, -0.2f, -0.8f));
        j[side == 0 ? ElbowL : ElbowR] = elbow;
        // a hand out of reach stops at the arm's length
        var reach = wrist - elbow;
        j[side == 0 ? WristL : WristR] = elbow + reach.Normalized() * Forearm;
    }

    /// <summary>
    /// Angulation: the figure is rolled by the lean about its feet; the body above the hips takes
    /// <paramref name="share"/> of it back, so the legs tip the edges while the chest stays over them.
    /// </summary>
    private static void Angulate(Span<Vector3> j, Vector3 hip, float lean, float share)
    {
        if (MathF.Abs(lean) < 1e-4f) return;
        // the ride's roll is about −Z in author space (see the inside of the turn above): undo part of it
        var back = new Basis(Vector3.Back, lean * share);
        for (int i = HeadTop; i <= WristR; i++)
            if (i != Hip) j[i] = hip + back * (j[i] - hip);
    }

    /// <summary>
    /// The middle joint of a two-bone limb from <paramref name="root"/> to <paramref name="end"/>,
    /// bending toward <paramref name="hint"/>. Out of reach, the limb is straight toward the end.
    /// </summary>
    public static Vector3 TwoBone(Vector3 root, Vector3 end, float upper, float lower, Vector3 hint)
    {
        var to = end - root;
        float d = to.Length();
        if (d < 1e-5f) return root + hint.Normalized() * upper;
        var axis = to / d;
        d = MathF.Min(d, upper + lower - 1e-4f);
        // the distance along the axis to the joint's foot, and the joint's height off it
        float a = (upper * upper - lower * lower + d * d) / (2f * d);
        float h = MathF.Sqrt(MathF.Max(0f, upper * upper - a * a));
        var bend = hint - axis * hint.Dot(axis);
        bend = bend.LengthSquared() < 1e-8f ? axis.Cross(Vector3.Right).Normalized() : bend.Normalized();
        return root + axis * a + bend * h;
    }
}
