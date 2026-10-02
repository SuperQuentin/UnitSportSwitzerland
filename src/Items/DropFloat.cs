using Godot;

namespace UnitSport.Items;

/// <summary>
/// Minecraft-style look for items lying on the ground: once a <see cref="DroppedItem"/> has come to
/// rest, its drawn mesh is blown up to a readable size and hovers upright above the spot, spinning
/// and bobbing. Only the visual moves: the body and its collider stay where the item landed.
///
/// <para>
/// One loop for all of them (driven by <see cref="DroppedItems"/>), no per-item <c>_Process</c>:
/// only items within <see cref="AnimateRange"/> of the camera are posed each frame, the rest keep
/// their last pose, and beyond <see cref="DrawRange"/> they are not drawn at all.
/// </para>
/// </summary>
public static class DropFloat
{
    /// <summary>Drawn size the largest side of a small item is blown up to.</summary>
    public const float TargetSize = 0.4f;
    public const float MaxScale = 3.5f;
    /// <summary>Gap between the ground and the bottom of the floating item, and the bob around it.</summary>
    public const float Hover = 0.14f, BobHeight = 0.06f;
    /// <summary>Spin in rad/s (a turn in about 4 s) and the bob's speed.</summary>
    public const float SpinSpeed = 1.6f, BobSpeed = 2.4f;
    public const float AnimateRange = 45f, DrawRange = 90f;

    private static readonly List<DroppedItem> Items = new();

    internal static void Add(DroppedItem item) => Items.Add(item);
    internal static void Remove(DroppedItem item) => Items.Remove(item);

    /// <summary>The blow-up for a mesh whose box is <paramref name="size"/>: small things grow, big ones stay.</summary>
    public static float ScaleFor(Vector3 size) =>
        Mathf.Clamp(TargetSize / Mathf.Max(size.X, Mathf.Max(size.Y, Mathf.Max(size.Z, 0.01f))), 1f, MaxScale);

    /// <summary>Poses every settled item near <paramref name="eye"/> (null: pose each once and leave it).</summary>
    public static void Step(Vector3? eye, float time)
    {
        const float range2 = AnimateRange * AnimateRange;
        foreach (var item in Items)
        {
            if (!item.Settled || item.Visual is not { } visual || !visual.Visible) continue;
            if (item.Floating && (eye is not { } e || item.GlobalPosition.DistanceSquaredTo(e) > range2)) continue;
            item.PoseFloat(time);
        }
    }
}
