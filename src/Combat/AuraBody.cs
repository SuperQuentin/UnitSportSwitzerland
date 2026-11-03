using System;
using System.Collections.Generic;

namespace UnitSport.Combat;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// What a VR player's arms are doing, as the aura battle reads it (#914): each move has a pose to
/// strike, and striking the next move's pose cuts to it (<see cref="AuraBody.Strikes"/>).
/// </summary>
[Flags]
public enum AuraGesture : byte
{
    None = 0,
    /// <summary>Both hands down at the sides or the hips.</summary>
    HandsLow = 1,
    /// <summary>Forearms folded across the chest.</summary>
    Crossed = 2,
    /// <summary>One hand above the head, the other not.</summary>
    OneUp = 4,
    /// <summary>Both hands above the head.</summary>
    BothUp = 8,
    /// <summary>A hand at the face (the shades, the jaw, the lips).</summary>
    Face = 16,
    /// <summary>Both hands out in front at chest height.</summary>
    BothForward = 32,
    /// <summary>A hand out in front at chest height.</summary>
    AnyForward = 64,
    /// <summary>Both arms flung out wide.</summary>
    Out = 128,
}

/// <summary>
/// The aura battler's body (#907, #914), pure so the player, the server's bots and the tests agree:
/// the pose a battle's state puts a battler in, and the VR pose each move asks for.
/// </summary>
public static class AuraBody
{
    /// <summary>How long a laugh shows on the battler who cracked up.</summary>
    public const double LaughSeconds = 1.6;

    /// <summary>
    /// The replicated pose of a battler now: planning, waiting, the move the schedule is in (a
    /// stumble over the first beat after a missed cut), laughing, the verdict's victory or defeat.
    /// <paramref name="won"/> is +1 won, -1 lost, 0 draw or not over. <paramref name="stepIn"/> is how
    /// far (0..1) the slow walk has stepped towards the rival.
    /// </summary>
    public static int PoseFor(AuraPhase phase, bool performing, int won, IReadOnlyList<AuraMove> plan,
        double t0, double beat, IReadOnlyList<AuraGrade> grades, double now, bool laughing, out float stepIn)
    {
        stepIn = 0f;
        if (laughing) return (int)AuraStance.Laugh;
        switch (phase)
        {
            case AuraPhase.Done:
                return (int)(won == 0 ? AuraStance.Waiting : won > 0 ? AuraStance.Victory : AuraStance.Defeat);
            case AuraPhase.Plan when performing:
                return (int)AuraStance.Planning;
            case AuraPhase.Perform when performing && plan.Count > 0:
            {
                int i = AuraRules.MoveAt(plan, t0, now, beat);
                if (i < 0) return (int)AuraStance.Waiting;
                var move = plan[i];
                double start = AuraRules.StartAt(plan, i, t0, beat);
                if (i > 0 && i - 1 < grades.Count && grades[i - 1] == AuraGrade.Miss && now - start < beat)
                    return (int)AuraStance.Stumble;
                if (move is AuraMove.SlowWalk or AuraMove.Moonwalk)
                {
                    double len = AuraRules.Def(move).Beats * beat;
                    float p = (float)Math.Clamp((now - start) / len, 0, 1);
                    // the moonwalk slides back the way the slow walk steps in
                    stepIn = (move == AuraMove.Moonwalk ? -0.6f : 1f) * MathF.Sin(p * MathF.PI);
                }
                return AuraRules.PoseOf(move);
            }
            default:
                return (int)AuraStance.Waiting;
        }
    }

    /// <summary>The pose to strike for <paramref name="move"/>; <see cref="AuraMove.None"/> = dropping the last pose (hands down).</summary>
    public static AuraGesture GestureOf(AuraMove move) => move switch
    {
        AuraMove.ArmsCrossed => AuraGesture.Crossed,
        AuraMove.PointToSky or AuraMove.Lasso or AuraMove.Dab => AuraGesture.OneUp,
        AuraMove.Yawn => AuraGesture.BothUp,
        AuraMove.Sunglasses or AuraMove.Mewing or AuraMove.Shush => AuraGesture.Face,
        AuraMove.SixSeven or AuraMove.Robot => AuraGesture.BothForward,
        AuraMove.Griddy or AuraMove.CutTheRope or AuraMove.LeadRival => AuraGesture.AnyForward,
        AuraMove.Siuuu or AuraMove.Bow => AuraGesture.Out,
        _ => AuraGesture.HandsLow,
    };

    /// <summary>
    /// The gestures two hands make, each relative to the eyes in the body's yaw frame (x right, y up,
    /// -z ahead, metres: <c>FootPlayer.VrHands</c>).
    /// </summary>
    public static AuraGesture Classify(float rx, float ry, float rz, float lx, float ly, float lz)
    {
        var g = AuraGesture.None;
        bool rUp = ry > 0.12f, lUp = ly > 0.12f;
        if (rUp && lUp) g |= AuraGesture.BothUp;
        else if (rUp || lUp) g |= AuraGesture.OneUp;
        if (AtFace(rx, ry, rz) || AtFace(lx, ly, lz)) g |= AuraGesture.Face;
        bool rFwd = Forward(ry, rz), lFwd = Forward(ly, lz);
        if (rFwd && lFwd) g |= AuraGesture.BothForward;
        if (rFwd || lFwd) g |= AuraGesture.AnyForward;
        if (ry < -0.45f && ly < -0.45f && rz > -0.3f && lz > -0.3f) g |= AuraGesture.HandsLow;
        // the right hand left of centre and the left hand right of it, in front of the chest
        if (rx < -0.02f && lx > 0.02f && ry is > -0.65f and < -0.15f && ly is > -0.65f and < -0.15f) g |= AuraGesture.Crossed;
        if (rx > 0.45f && lx < -0.45f && ry < 0.05f && ly < 0.05f) g |= AuraGesture.Out;
        return g;
    }

    private static bool AtFace(float x, float y, float z) => MathF.Abs(x) < 0.22f && y is > -0.28f and < 0.06f && z is > -0.30f and < 0.08f;

    private static bool Forward(float y, float z) => z < -0.35f && y is > -0.45f and < 0.05f;

    /// <summary>Whether the hands strike <paramref name="move"/>'s pose.</summary>
    public static bool Strikes(AuraMove move, AuraGesture now) => (now & GestureOf(move)) != 0;
}
