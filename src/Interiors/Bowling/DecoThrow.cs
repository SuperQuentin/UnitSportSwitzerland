using Godot;
using UnitSport.Avatar;
using J = UnitSport.Avatar.HumanMeshBuilder.Joint;

namespace UnitSport.Interiors;

/// <summary>
/// A decoration bowler's turn (#1334, for <see cref="BowlingDeco"/>), as a pure function of the time since it began: beside
/// the ball return it bends and lifts a ball, carries it to its stance on the approach, makes a four-step approach with
/// an underarm pendulum swing (push-away, down, backswing, forward and release low at the foul line, follow-through),
/// the ball in its hand until it leaves, then walks back to the return. Positions in the lane pair's own frame (the pins
/// toward +Z); joints in the figure's author space (+Z ahead, its right hand on rig-L, −X).
/// </summary>
public static class DecoThrow
{
    /// <summary>The moments of a turn, s: picked up, at the stance, the approach's start, the release, standing, back.</summary>
    public const double Grab = 0.9, Lifted = 1.5, AtStance = 2.6, Start = 3.2, Release = 4.75, Followed = 5.5, BackFrom = 5.8, BackAt = 8.5;

    private const float Upper = 0.270f, Fore = 0.245f, Reach = 0.50f;
    private const float WalkSpeed = 1.3f, ApproachSpeed = 2.2f;

    private static readonly Vector3[] Standing = HumanMeshBuilder.PoseJoints(HumanPose.Standing);

    /// <summary>The bowler at one moment: where its feet are, which way it faces (0 the pins), its joints, the ball (null once rolled).</summary>
    public readonly record struct Frame(Vector3 Feet, float Face, Vector3[] Joints, Vector3? Ball);

    /// <summary>Where the bowler waits beside the return, facing it.</summary>
    public static (Vector3 Feet, float Face) Waiting(LaneGeometry g, int lane)
    {
        float side = lane == 0 ? -1 : 1;
        // facing the return, toward the pair's middle (a figure facing angle a looks along (sin a, 0, cos a))
        return (new Vector3(side * 0.6f, 0, g.HoodLift.Z), -side * Mathf.Pi / 2);
    }

    /// <summary>The ball on the return's lift, where it is picked from.</summary>
    public static Vector3 HoodBall(LaneGeometry g) => g.HoodLift;

    /// <summary>The stance: four steps back from the slide's end, in line with the lane, clear of the return.</summary>
    private static Vector3 Stance(LaneGeometry g, int lane) => new(g.LaneX(lane) + 0.12f, 0, Mathf.Max(g.Front + 0.6f, End(g, lane).Z - 3.4f));

    /// <summary>Where the slide ends, just behind the foul line.</summary>
    private static Vector3 End(LaneGeometry g, int lane) => new(g.LaneX(lane) + 0.12f, 0, g.Foul - 0.35f);

    /// <summary>The bowler <paramref name="t"/> s into its turn.</summary>
    public static Frame At(LaneGeometry g, int lane, double t)
    {
        var (wait, waitFace) = Waiting(g, lane);
        var stance = Stance(g, lane);
        var end = End(g, lane);
        var j = new Vector3[Standing.Length];
        Standing.CopyTo(j, 0);

        if (t < Lifted)
        {
            // bent over the return, the ball in both hands from Grab, lifted to the chest
            float bend = Smooth(0.2, 0.7, t) * (1 - Smooth(Grab, Lifted, t));
            Lean(j, 0.75f * bend);
            var ball = t < Grab ? Local(HoodBall(g), wait, waitFace) : Vector3.Zero;
            var chest = new Vector3(0, 1.12f, 0.34f);
            var held = t < Grab ? ball : Local(HoodBall(g), wait, waitFace).Lerp(chest, Smooth(Grab, Lifted, t));
            float reach = Smooth(0.2, 0.8, t);
            TwoHands(j, Lerp(Rest(j, true), held, reach), Lerp(Rest(j, false), held, reach));
            return new Frame(wait, waitFace, j, World(held, wait, waitFace));
        }
        if (t < AtStance)
        {
            // carried to the stance
            float u = (float)((t - Lifted) / (AtStance - Lifted));
            var feet = wait.Lerp(stance, Ease(u));
            float face = Mathf.LerpAngle(waitFace, Mathf.Atan2(stance.X - wait.X, stance.Z - wait.Z), Ease(Mathf.Min(1, u * 3)));
            face = Mathf.LerpAngle(face, 0, Smooth(0.7, 1.0, u));
            Walk(j, WalkSpeed, (float)(t - Lifted) * HumanMeshBuilder.Cadence(WalkSpeed) / 2);
            var chest = new Vector3(0, 1.12f, 0.34f);
            TwoHands(j, chest, chest);
            return new Frame(feet, face, j, World(chest, feet, face));
        }
        if (t < Start)
        {
            var chest = new Vector3(0, 1.12f, 0.34f);
            TwoHands(j, chest, chest);
            return new Frame(stance, 0, j, World(chest, stance, 0));
        }
        if (t < Followed)
        {
            // the approach: four steps to the slide's end, the swing over them
            double tau = t - Start, len = Release - Start;
            float u = (float)Math.Min(1, tau / len);
            var feet = stance.Lerp(end, u * (2 - u) * 0.5f + u * 0.5f);
            if (t < Release) Walk(j, ApproachSpeed, (float)(tau * HumanMeshBuilder.Cadence(ApproachSpeed) / 2));
            // the body drops and leans into the release, and straightens after
            float lean = 0.15f + 0.45f * Smooth(0.8, 1.55, tau) - 0.45f * Smooth(1.7, 2.4, tau);
            Lean(j, lean);
            float swing = SwingAngle(tau);
            var shoulder = j[(int)J.ShoulderL];
            var pendulum = shoulder + new Vector3(0.02f, -Mathf.Cos(swing), Mathf.Sin(swing)) * Reach;
            var chest = new Vector3(0, 1.12f, 0.34f);
            // both hands push the ball away, then the right arm swings alone, the left out for balance
            float push = Smooth(0, 0.4, tau);
            var right = tau < 0.4 ? chest.Lerp(pendulum, push) : pendulum;
            var left = tau < 0.4 ? chest.Lerp(Balance(j), push) : Balance(j);
            Arm(j, true, right);
            Arm(j, false, left);
            Vector3? ball = null;
            if (t < Release)
            {
                var hand = j[(int)J.WristL];
                var along = (hand - j[(int)J.ShoulderL]).Normalized();
                ball = World(hand + along * 0.1f, feet, 0);
            }
            return new Frame(feet, 0, j, ball);
        }
        if (t < BackFrom) return new Frame(end, 0, j, null);
        {
            // walking back to the return
            float u = (float)Math.Min(1, (t - BackFrom) / (BackAt - BackFrom));
            var feet = end.Lerp(wait, Ease(u));
            float face = u < 1 ? Mathf.Atan2(wait.X - end.X, wait.Z - end.Z) : waitFace;
            face = Mathf.LerpAngle(face, waitFace, Smooth(0.85, 1.0, u));
            if (u < 1) Walk(j, WalkSpeed, (float)((t - BackFrom) * HumanMeshBuilder.Cadence(WalkSpeed) / 2));
            return new Frame(feet, face, j, null);
        }
    }

    /// <summary>
    /// Just the swing, in place (a bot partner beside the approach, #1323): <paramref name="sinceRelease"/> s from its
    /// release (negative before it); holding the ball at the chest before the swing starts.
    /// </summary>
    public static Vector3[] Swing(double sinceRelease)
    {
        var j = new Vector3[Standing.Length];
        Standing.CopyTo(j, 0);
        double tau = Release - Start + sinceRelease;
        var chest = new Vector3(0, 1.12f, 0.34f);
        if (tau <= 0) { TwoHands(j, chest, chest); return j; }
        Lean(j, 0.15f + 0.45f * Smooth(0.8, 1.55, tau) - 0.45f * Smooth(1.7, 2.4, tau));
        float swing = SwingAngle(tau);
        var pendulum = j[(int)J.ShoulderL] + new Vector3(0.02f, -Mathf.Cos(swing), Mathf.Sin(swing)) * Reach;
        float push = Smooth(0, 0.4, tau);
        Arm(j, true, tau < 0.4 ? chest.Lerp(pendulum, push) : pendulum);
        Arm(j, false, tau < 0.4 ? chest.Lerp(Balance(j), push) : Balance(j));
        return j;
    }

    /// <summary>
    /// The ball arm's angle from hanging (rad, + ahead) <paramref name="tau"/> s into the approach: pushed out over the
    /// first step, down by the second, up behind by the third, forward and low at the release, up in the follow-through.
    /// </summary>
    private static float SwingAngle(double tau)
    {
        // the forward swing quickest: a pendulum falling from the top of the backswing, the ball leaving at speed
        (double T, float A)[] keys = { (0, 0.6f), (0.4, 0.9f), (0.8, 0f), (1.25, -1.35f), (1.55, 0.2f), (2.0, 2.3f), (2.6, 0f) };
        if (tau <= keys[0].T) return keys[0].A;
        for (int i = 1; i < keys.Length; i++)
            if (tau <= keys[i].T)
            {
                float u = (float)((tau - keys[i - 1].T) / (keys[i].T - keys[i - 1].T));
                return Mathf.Lerp(keys[i - 1].A, keys[i].A, i == 4 ? u : Ease(u));
            }
        return keys[^1].A;
    }

    // —— pieces ——

    /// <summary>The left arm out to the side and a little back, for balance.</summary>
    private static Vector3 Balance(Vector3[] j) => j[(int)J.ShoulderR] + new Vector3(0.42f, -0.18f, -0.02f);

    private static Vector3 Rest(Vector3[] j, bool right) => j[right ? (int)J.WristL : (int)J.WristR];

    private static void TwoHands(Vector3[] j, Vector3 right, Vector3 left)
    {
        Arm(j, true, right + new Vector3(-0.1f, 0.02f, -0.04f));
        Arm(j, false, left + new Vector3(0.1f, 0.02f, -0.04f));
    }

    /// <summary>The walking legs (and their arms, set over after) at a phase of the gait.</summary>
    private static void Walk(Vector3[] j, float speed, float phase)
    {
        var w = HumanMeshBuilder.StrideJoints(speed, phase - MathF.Floor(phase));
        w.CopyTo(j, 0);
    }

    /// <summary>A point of the pair's frame in the author space of a figure standing at <paramref name="feet"/> facing <paramref name="face"/>.</summary>
    private static Vector3 Local(Vector3 p, Vector3 feet, float face) => new Basis(Vector3.Up, face).Inverse() * (p - feet);

    private static Vector3 World(Vector3 a, Vector3 feet, float face) => feet + new Basis(Vector3.Up, face) * a;

    private static Vector3 Lerp(Vector3 a, Vector3 b, float u) => a.Lerp(b, u);

    private static float Ease(float x)
    {
        x = Mathf.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    private static float Smooth(double a, double b, double x) => Ease((float)((x - a) / (b - a)));

    /// <summary>The upper body bent forward over the hips by <paramref name="angle"/> rad.</summary>
    private static void Lean(Vector3[] j, float angle)
    {
        if (angle == 0) return;
        var pivot = j[(int)J.Hip];
        var turn = new Basis(Vector3.Right, angle);
        foreach (var k in UpperBody) j[(int)k] = pivot + turn * (j[(int)k] - pivot);
    }

    private static readonly J[] UpperBody =
    {
        J.HeadTop, J.HeadBase, J.Neck, J.Chest, J.Waist, J.ShoulderL, J.ElbowL, J.WristL, J.ShoulderR, J.ElbowR, J.WristR,
    };

    /// <summary>An arm reaching for <paramref name="wrist"/> (clamped to its reach), the elbow solved from its two bones.</summary>
    private static void Arm(Vector3[] j, bool right, Vector3 wrist)
    {
        var sh = j[(int)(right ? J.ShoulderL : J.ShoulderR)];
        var to = wrist - sh;
        float dist = to.Length();
        if (dist > Reach) { to *= Reach / dist; dist = Reach; }
        if (dist < 0.05f) { to = new Vector3(0, -0.05f, 0); dist = 0.05f; }
        var dir = to / dist;
        float along = (Upper * Upper - Fore * Fore + dist * dist) / (2 * dist);
        float up = MathF.Sqrt(MathF.Max(0, Upper * Upper - along * along));
        var pole = new Vector3(right ? -0.35f : 0.35f, -1f, -0.2f);
        var bend = (pole - dir * pole.Dot(dir)).Normalized();
        j[(int)(right ? J.ElbowL : J.ElbowR)] = sh + dir * along + bend * up;
        j[(int)(right ? J.WristL : J.WristR)] = sh + to;
    }
}
