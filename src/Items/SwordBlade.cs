using Godot;
using UnitSport.Avatar;
using UnitSport.Combat;

namespace UnitSport.Items;

/// <summary>
/// Where the blade is through a swing (#1371): for each move, set and seed, the hilt and the tip sampled through the move's keyframe table (the
/// spline pose the figure is drawn in, the hand mount the sword is held by, the blade's whip at the wrist), in the character's own space, at
/// <see cref="Count"/> moments across the strike's window. Built once per swing and cached. The hits are decided on it
/// (<see cref="SwordSweep"/>, by the owner's client during the active frames, by the server for a claimed hit) and the trail is drawn from it
/// (<see cref="SwordTrails"/>), so the two always agree. No mesh, no allocation after the first build.
/// </summary>
public static class SwordBlade
{
    public const int Count = 16;
    /// <summary>The hilt and the tip along the blade, in the item mesh's units.</summary>
    public const float HiltAt = 0.06f, TipAt = 0.89f;

    public sealed class Track
    {
        public required Vector3[] Hilt, Tip;
        /// <summary>The progress of the move at the first and the last sample.</summary>
        public float From, To;
        /// <summary>The farthest the tip gets from the body's axis along the ground over the window, plus the blade's thickness: the swing's real reach.</summary>
        public float Reach;
        /// <summary>The farthest the tip gets straight ahead of the body (along its facing), within 0.45 m of its centre line, over the window: what a lunge has to bring a target within.</summary>
        public float Forward;
    }

    private static readonly Dictionary<int, Track> Cache = new();

    public static Track Get(SwordMove move, int variant, int seed)
    {
        int key = (int)move | (variant << 4) | (seed << 6);
        if (Cache.TryGetValue(key, out var t)) return t;
        return Cache[key] = Build(move, variant, seed);
    }

    private static Track Build(SwordMove move, int variant, int seed)
    {
        var (r0, r1) = SwordPoses.TrailWindow(move, seed);
        float total = Math.Max(0.01f, SwordRules.Def(move).Total);
        var aabb = ItemDefs.HandMesh(ItemId.Sword)!.GetAabb();
        float sign = aabb.End.Z > -aabb.Position.Z ? 1f : -1f;
        var hilt = new Vector3[Count];
        var tip = new Vector3[Count];
        float reach = 0f, forward = 0f;
        for (int i = 0; i < Count; i++)
        {
            float p = Mathf.Lerp(r0, r1, i / (float)(Count - 1));
            var dance = new DanceParams(Audio.Cd.MusicStyle.Pop, HumanMeshBuilder.SwordMoves, 0f, Mathf.Min(p, 0.999f),
                HumanMeshBuilder.SwordCode(move, variant, seed), 1f);
            var mounts = HumanMeshBuilder.MountsFor(0f, 0f, ItemArmPose.None, 0f, dance, null, null);
            var hand = mounts.HandL.X > mounts.HandR.X ? mounts.HandL : mounts.HandR;
            var basis = mounts.HandBasis;
            // the item node as the game places it (HeldItemVisual), then the blade's whip at the wrist (FootPlayer.SwordArcNow)
            var node = new Transform3D(basis, hand + basis * new Vector3(0, -0.05f, -0.03f));
            float tBlade = Mathf.Max(0f, p - SwordPoses.BladeLag / total);
            var (yaw, pitch, roll, fwd) = SwordRules.Arc(move, tBlade);
            float k = move is SwordMove.Plunge ? 1f : 0.5f;
            var full = node * new Transform3D(Basis.FromEuler(new Vector3(pitch * k, yaw * k, roll * k)), new Vector3(0, 0, fwd * k));
            hilt[i] = full * new Vector3(0, 0, sign * HiltAt);
            tip[i] = full * new Vector3(0, 0, sign * TipAt);
            reach = Mathf.Max(reach, new Vector2(tip[i].X, tip[i].Z).Length());
            if (Mathf.Abs(tip[i].X) < 0.45f) forward = Mathf.Max(forward, -tip[i].Z);   // where the cut crosses in front of the body
        }
        return new Track { Hilt = hilt, Tip = tip, From = r0, To = r1, Reach = reach + SwordSweep.BladeRadius, Forward = forward + SwordSweep.BladeRadius };
    }

    /// <summary>The blade at <paramref name="progress"/> of the move (clamped to the window), by interpolating the samples.</summary>
    public static void At(Track t, float progress, out Vector3 hilt, out Vector3 tip)
    {
        float x = Mathf.Clamp((progress - t.From) / Mathf.Max(0.0001f, t.To - t.From), 0f, 1f) * (Count - 1);
        int i = Mathf.Min((int)x, Count - 2);
        float f = x - i;
        hilt = t.Hilt[i].Lerp(t.Hilt[i + 1], f);
        tip = t.Tip[i].Lerp(t.Tip[i + 1], f);
    }
}
