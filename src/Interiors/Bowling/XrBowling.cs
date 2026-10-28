using Godot;
using UnitSport.Player;

namespace UnitSport.Interiors;

/// <summary>
/// Bowling for real in VR (#1323): on your turn, on the approach, the grip closes on your ball; swing the
/// arm and open the hand, and the ball leaves it as the hand was moving. The hand's last tenth of a
/// second gives the release: where it crossed the lane, its line, its speed, and the twist of the wrist
/// (its turn about the lane's direction) the hook, as a real release's does. A swing too slow to roll
/// keeps the ball. The Wii-style steps (<see cref="BowlingAim"/>) stay on the stick and trigger.
/// </summary>
public static class XrBowling
{
    /// <summary>A hand slower than this along the lane has not thrown: the ball stays in it, m/s.</summary>
    private const float ThrowSpeed = 1.6f;
    /// <summary>VR swings come out softer than a real arm's (no ball's weight to carry through): scaled up.</summary>
    private const float SwingGain = 1.3f;
    private const int Samples = 12;

    private static readonly (double At, Transform3D Hand)[] Track = new (double, Transform3D)[Samples];
    private static int _count, _next;
    private static XRController3D? _holding;
    private static (string Key, int Pair, int Lane)? _at;

    /// <summary>Where the ball held for a throw is: in the gripping controller's palm; null when none is.</summary>
    public static Transform3D? HeldAt =>
        _holding is { } hand && GodotObject.IsInstanceValid(hand) ? hand.GlobalTransform * new Transform3D(Basis.Identity, new Vector3(0, -0.04f, 0.06f)) : null;

    /// <summary>Whether <paramref name="hand"/> holds the ball for a throw.</summary>
    public static bool Holds(XRController3D hand) => _holding == hand;

    /// <summary>
    /// The grip closing: takes the ball for a throw when the local player is up at their lane, holding
    /// it, on the approach. True when it did.
    /// </summary>
    public static bool Grip(FootPlayer player, XRController3D hand)
    {
        if (BowlingLanes.Instance is not { } b || InteriorManager.Instance?.CurrentNode is not { } node) return false;
        if (b.SignedInHere() is not { } me || me.Key != node.Layout.Key || me.Bowler.Ball != BowlingLanes.BallAt.Hand) return false;
        var pair = node.Layout.Furniture[me.Pair];
        var g = LaneGeometry.Of(pair);
        var at = BowlingLanes.PieceFrame(node.Layout, pair).AffineInverse() * node.ToLocal(player.GlobalPosition);
        if (Mathf.Abs(at.X - g.LaneX(me.Lane)) > 1.0f || at.Z < g.Front - 0.5f || at.Z > g.Foul + 0.4f) return false;
        _holding = hand;
        _at = (me.Key, me.Pair, me.Lane);
        _count = _next = 0;
        return true;
    }

    /// <summary>Every frame the grip stays closed on the ball: where the hand is.</summary>
    public static void Follow(XRController3D hand)
    {
        if (_holding != hand) return;
        Track[_next] = (Time.GetTicksMsec() / 1000.0, hand.GlobalTransform);
        _next = (_next + 1) % Samples;
        _count = Math.Min(_count + 1, Samples);
    }

    /// <summary>The grip opening: a swing fast enough along the lane rolls the ball.</summary>
    public static void LetGo(XRController3D hand)
    {
        if (_holding != hand) return;
        _holding = null;
        if (_count < 4 || _at is not { } lane || BowlingLanes.Instance is not { } b
            || InteriorManager.Instance?.CurrentNode is not { } node || node.Layout.Key != lane.Key) return;
        var newest = Track[(_next - 1 + Samples) % Samples];
        // the sample a tenth of a second before (or the oldest kept)
        var older = newest;
        for (int k = 2; k <= _count; k++)
        {
            older = Track[(_next - k + Samples * 2) % Samples];
            if (newest.At - older.At >= 0.1) break;
        }
        double dt = newest.At - older.At;
        if (dt < 0.02) return;
        // into the lane's frame: x across (+x the bowler's left), z toward the pins
        var pair = node.Layout.Furniture[lane.Pair];
        var g = LaneGeometry.Of(pair);
        var toLane = (node.GlobalTransform * BowlingLanes.PieceFrame(node.Layout, pair)).AffineInverse();
        var p1 = toLane * newest.Hand.Origin;
        var v = (p1 - toLane * older.Hand.Origin) / (float)dt;
        if (v.Z < ThrowSpeed) return;
        // the wrist's turn about the lane's direction over the same tenth: the twist
        var turn = (toLane.Basis * newest.Hand.Basis) * (toLane.Basis * older.Hand.Basis).Inverse();
        var axis = turn.GetRotationQuaternion().GetAxis();
        float twist = turn.GetRotationQuaternion().GetAngle() * axis.Z / (float)dt;
        var release = new BowlingBall.Release(
            p1.X - g.LaneX(lane.Lane), Mathf.Atan2(v.X, v.Z), new Vector2(v.X, v.Z).Length() * SwingGain,
            16f + Mathf.Min(Mathf.Abs(twist), 12f) * 2.5f, Mathf.Clamp(-twist / 8f, -1f, 1f));
        b.AskRelease(lane.Key, release);
    }
}
