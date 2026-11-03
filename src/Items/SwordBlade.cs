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
    /// <summary>The centre of the closed palm, in the hand mount's frame: the sword node is placed by it (<see cref="SwordPoses.GripSocket"/>).</summary>
    public static readonly Vector3 SocketOffset = new(SwordPoses.GripSocket.X, SwordPoses.GripSocket.Y, SwordPoses.GripSocket.Z);
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

    private static float _sign;

    /// <summary>+1 when the item mesh's blade runs along +Z of its node, -1 when along -Z (the mesh is built mirrored).</summary>
    public static float BladeSign
    {
        get
        {
            if (_sign == 0f)
            {
                var aabb = ItemDefs.HandMesh(ItemId.Sword)!.GetAabb();
                _sign = aabb.End.Z > -aabb.Position.Z ? 1f : -1f;
            }
            return _sign;
        }
    }

    /// <summary>
    /// The sword node's transform from a hand's grip (<c>GaitMounts.GripOrigin</c> / <c>GripBasis</c>, <see cref="HandGrip"/>): the handle's axis along
    /// the grip's Z, the point of the handle the four fingers close on (<see cref="HandGrip.HandleAtFingers"/> from the mesh origin) at the ring centre.
    /// The blade, the trail and the hit capsule follow the hand through this and nothing else.
    /// </summary>
    public static Transform3D NodeFor(Vector3 gripOrigin, Basis grip)
    {
        float sign = BladeSign;
        var basis = sign > 0f ? grip : grip * new Basis(Vector3.Up, Mathf.Pi);
        return new Transform3D(basis, gripOrigin - basis.Z * (sign * Avatar.HandGrip.HandleAtFingers));
    }

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
        float sign = BladeSign;
        var hilt = new Vector3[Count];
        var tip = new Vector3[Count];
        float reach = 0f, forward = 0f;
        for (int i = 0; i < Count; i++)
        {
            float p = Mathf.Lerp(r0, r1, i / (float)(Count - 1));
            var dance = new DanceParams(Audio.Cd.MusicStyle.Pop, HumanMeshBuilder.SwordMoves, 0f, Mathf.Min(p, 0.999f),
                HumanMeshBuilder.SwordCode(move, variant, seed), 1f);
            var mounts = HumanMeshBuilder.MountsFor(0f, 0f, ItemArmPose.None, 0f, dance, null, null);
            // the item node as the game places it: from the right hand's own grip frame (the one its skinned bone and its fist are built in)
            var full = NodeFor(mounts.GripOrigin, mounts.GripBasis);
            hilt[i] = full * new Vector3(0, 0, sign * HiltAt);
            tip[i] = full * new Vector3(0, 0, sign * TipAt);
            reach = Mathf.Max(reach, new Vector2(tip[i].X, tip[i].Z).Length());
            if (i >= Count / 3)
            {
                // where the cut crosses the line straight ahead of the body: the blade's point on it, else the tip's own forward reach
                float hx = hilt[i].X, tx = tip[i].X;
                if (hx * tx < 0f) forward = Mathf.Max(forward, -(hilt[i].Z + (tip[i].Z - hilt[i].Z) * (hx / (hx - tx))));
                else forward = Mathf.Max(forward, Mathf.Abs(tx) < 0.3f ? -tip[i].Z : 0f);
            }
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
