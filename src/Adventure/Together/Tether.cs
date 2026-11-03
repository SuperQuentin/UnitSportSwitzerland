using Godot;

namespace UnitSport.Adventure.Together;

/// <summary>What the owner knows of its link this physics step (#1266 part 3.2), all in its own world space.</summary>
public struct TetherState
{
    /// <summary>The own body.</summary>
    public Vector3 Self;
    /// <summary>The partner's body (its replicated position, about 100 ms old).</summary>
    public Vector3 Partner;
    /// <summary>The link's length (<see cref="LinkRules.Length"/>).</summary>
    public float Length;
    /// <summary>A hand-holding follower: with no input, the leader's trail walks it.</summary>
    public bool Led;
    /// <summary>Where the trail says to be (1.1 m behind the leader), when there is one.</summary>
    public Vector3? TrailTarget;
    /// <summary>The partner's speed, m/s: a follower runs when its leader does.</summary>
    public float PartnerSpeed;
    /// <summary>Toward the partner the ground drops away (the walk code would refuse it): no pull then.</summary>
    public bool PullBlocked;
}

/// <summary>
/// The soft follow constraint (#1266 part 3.2), on the walk intent, so a bot's <c>WalkControls</c> goes through it
/// too: inside the link's length the wish is untouched; beyond it, the part of the wish pointing away from the
/// partner is taken out and a pull toward them added (0.8 per metre over, capped at 1); a hand-holding follower
/// with no input is walked along the leader's trail. Pure (Godot's managed vectors only), tier 0 (<c>TetherTests</c>).
/// </summary>
public static class Tether
{
    /// <summary>Pull per metre past the length.</summary>
    public const float PullPerMetre = 0.8f;
    /// <summary>A stick under this is "no input".</summary>
    public const float NoInput = 0.05f;
    /// <summary>Close enough to the trail's point: stand.</summary>
    public const float Arrived = 0.25f;
    /// <summary>The leader runs above this, m/s, so its follower does.</summary>
    public const float RunAbove = 4f;
    /// <summary>A led follower this far behind its trail point runs to catch up, m.</summary>
    public const float CatchUp = 0.45f;

    public static (Vector3 Wish, bool Run) Apply((Vector3 Wish, bool Run) own, in TetherState s)
    {
        var wish = new Vector3(own.Wish.X, 0, own.Wish.Z);
        bool run = own.Run;
        if (s.Led && wish.Length() < NoInput && s.TrailTarget is { } target)
        {
            var to = new Vector3(target.X - s.Self.X, 0, target.Z - s.Self.Z);
            float d = to.Length();
            wish = d > Arrived ? to / d * Mathf.Min(1f, d / 0.4f) : Vector3.Zero;
            // the leader runs, or the follower fell behind its point: it runs to keep the hand
            run = d > Arrived && (s.PartnerSpeed > RunAbove || d > CatchUp);
        }
        var toPartner = new Vector3(s.Partner.X - s.Self.X, 0, s.Partner.Z - s.Self.Z);
        float dist = toPartner.Length();
        float over = dist - s.Length;
        if (over <= 0f || dist < 1e-3f) return (wish, run);
        var dir = toPartner / dist;
        float along = wish.Dot(dir);
        if (along < 0f) wish -= dir * along;
        if (!s.PullBlocked) wish += dir * Mathf.Min(1f, PullPerMetre * over);
        if (wish.LengthSquared() > 1f) wish = wish.Normalized();
        return (wish, run);
    }
}

/// <summary>
/// The leader's trail as its follower sees it (#1266 part 3.2): a fixed ring of <see cref="Size"/> points, one every
/// <see cref="Spacing"/> m of the leader's replicated position; <see cref="Target"/> is the point
/// <see cref="Behind"/> m behind the leader along it. No allocation after construction.
/// </summary>
public sealed class TetherTrail
{
    public const int Size = 32;
    public const float Spacing = 0.4f;
    public const float Behind = 1.1f;
    /// <summary>A jump this long (an origin shift, a teleport) starts the trail again.</summary>
    public const float Jump = 6f;

    private readonly Vector3[] _points = new Vector3[Size];
    private int _count, _newest = -1;

    public int Count => _count;

    public void Clear()
    {
        _count = 0;
        _newest = -1;
    }

    /// <summary>The leader is here now: a new point when it moved <see cref="Spacing"/> from the last.</summary>
    public void Push(Vector3 leader)
    {
        if (_count > 0)
        {
            float d = leader.DistanceTo(_points[_newest]);
            if (d > Jump) Clear();
            else if (d < Spacing) return;
        }
        _newest = (_newest + 1) % Size;
        _points[_newest] = leader;
        if (_count < Size) _count++;
    }

    /// <summary>The point <see cref="Behind"/> m back from <paramref name="leader"/> along the trail, or null with none yet.</summary>
    public Vector3? Target(Vector3 leader)
    {
        if (_count == 0) return null;
        float left = Behind;
        var from = leader;
        for (int k = 0; k < _count; k++)
        {
            var p = _points[(_newest - k + Size) % Size];
            float seg = from.DistanceTo(p);
            if (seg >= left) return from + (p - from) * (left / seg);
            left -= seg;
            from = p;
        }
        return from;
    }
}
