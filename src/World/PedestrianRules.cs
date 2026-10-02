using Godot;

namespace UnitSport.World;

/// <summary>
/// The pure rules of the server's pedestrians (#217, docs/notes/world/pedestrians.md), linked into the
/// tier-0 tests: what a player sees, where new people may appear (only where the player is about to
/// look, never in plain sight), who gets a body, and how a record walks its path.
/// </summary>
public static class PedestrianRules
{
    /// <summary>Drawn up to this far (a walker is ~10 px tall at 150 m on a 1152 px render).</summary>
    public const float ViewRange = 150f;
    /// <summary>Half the horizontal field of view of the default 75° vertical FOV at 16:9, plus a margin.</summary>
    public const float HalfFov = 1.0f;
    /// <summary>How far ahead in time the spawner looks: the player's motion and turn over this.</summary>
    public const float Lead = 1.5f;
    /// <summary>Every pedestrian within this of a player has a body, seen or not; wider ahead of a fast one.</summary>
    public const float BodyRadius = 30f, BodyLead = 1.5f;
    /// <summary>Budgets, measured at 16 players (the note): per client drawn, per player wanted, server records.</summary>
    public const int VisibleCap = 40, WantedPerPlayer = 28, RecordCap = 1200;
    /// <summary>Forgotten past this from every player, or after this long unseen.</summary>
    public const float ForgetDistance = 3000f, ForgetSeconds = 1200f;
    public const float MinGap = 5f;

    /// <param name="Pos">The player's eye, world.</param>
    /// <param name="Yaw">Where the camera looks, radians about +Y; forward is (−sin, 0, −cos).</param>
    /// <param name="YawRate">How fast the view is turning, rad/s (+ left).</param>
    /// <param name="Vel">The player's velocity.</param>
    public readonly record struct View(Vector3 Pos, float Yaw, float YawRate, Vector3 Vel);

    public static Vector2 Forward(float yaw) => new(-Mathf.Sin(yaw), -Mathf.Cos(yaw));

    /// <summary>Inside the view cone (flat) and range, with <paramref name="margin"/> radians either side.</summary>
    public static bool InView(in View v, Vector3 p, float margin = 0f, float range = ViewRange)
    {
        var d = new Vector2(p.X - v.Pos.X, p.Z - v.Pos.Z);
        float dist = d.Length();
        if (dist > range) return false;
        if (dist < 2f) return true;
        return Forward(v.Yaw).Dot(d / dist) >= Mathf.Cos(Mathf.Min(HalfFov + margin, Mathf.Pi));
    }

    /// <summary>The view <see cref="Lead"/> seconds on: moved by the velocity, turned by the turn rate.</summary>
    public static View Ahead(in View v, float lead = Lead) =>
        v with { Pos = v.Pos + v.Vel * lead, Yaw = v.Yaw + Mathf.Clamp(v.YawRate * lead, -1.5f, 1.5f) };

    /// <summary>
    /// Where a new pedestrian may appear for this player: where it is about to look but does not yet
    /// (the anticipated cone and the cone turned further either way, out of the current one), or in
    /// view beyond 75 % of the range, where a person is a few pixels. Never in plain sight.
    /// </summary>
    public static bool SpawnOk(in View v, Vector3 p)
    {
        if (InView(v, p, 0.15f, ViewRange * 0.75f)) return false;
        if (InView(v, p, 0.15f)) return true;   // the far band
        var a = Ahead(v);
        // a turn can go either way: a still player gets the band just outside both edges
        return InView(a, p, 0.5f) || InView(v, p, 0.9f);
    }

    /// <summary>A pedestrian this close (wider ahead of a fast player or vehicle) gets a capsule on that client.</summary>
    public static bool NeedsBody(in View v, Vector3 p)
    {
        var ahead = v.Pos + v.Vel * BodyLead;
        return MathFlat(p - v.Pos) < BodyRadius || MathFlat(p - ahead) < BodyRadius;
    }

    public static bool Forget(float nearestPlayer, float unseenSeconds) =>
        nearestPlayer > ForgetDistance || unseenSeconds > ForgetSeconds;

    /// <summary>Walks <paramref name="pos"/> toward <paramref name="goal"/>; true on arrival.</summary>
    public static bool Walk(ref Vector3 pos, Vector3 goal, float speed, float dt)
    {
        var to = goal - pos;
        float d = to.Length();
        float step = speed * dt;
        if (d <= step) { pos = goal; return true; }
        pos += to / d * step;
        return false;
    }

    /// <summary>A walking speed 1.1..1.6 m/s and a look, both from the record's seed.</summary>
    public static float Speed(int seed) => 1.1f + 0.5f * Hash01(seed, 1);

    /// <summary>
    /// The next leg's goal: among <paramref name="candidates"/> (street spots near the walker), one
    /// 6..30 m away that keeps roughly the way it was going (a pavement, not a zigzag), picked by the
    /// seed and the leg number so it is the same every time it is replayed. Null: turn back.
    /// </summary>
    public static int PickNext(Vector3 at, float yaw, IReadOnlyList<Vector3> candidates, int seed, int leg,
        System.Func<Vector3, Vector3, bool>? blocked = null)
    {
        var fwd = Forward(yaw);
        int best = -1;
        float bestScore = float.MinValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            var d = new Vector2(candidates[i].X - at.X, candidates[i].Z - at.Z);
            float dist = d.Length();
            if (dist is < 6f or > 30f) continue;
            float score = fwd.Dot(d / dist) + 0.6f * Hash01(seed * 31 + leg, i);
            if (score > bestScore && !(blocked?.Invoke(at, candidates[i]) ?? false)) { bestScore = score; best = i; }
        }
        return best;
    }

    public static float Hash01(int a, int b)
    {
        unchecked
        {
            uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }

    private static float MathFlat(Vector3 v) => new Vector2(v.X, v.Z).Length();
}
