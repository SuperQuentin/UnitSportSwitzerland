using Godot;
using UnitSport.Items;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// The bottled gust's updraft (#1227, rewards plan 8): where the bottle lands, on every peer (<c>ItemEventKind.Gust</c>,
/// relayed like the smoke), a column of rising rings for <see cref="Seconds"/> and the wind's roar. The lift itself
/// is a volume of the stamina plan's air (<c>World/AirLift.Volumes</c>, #1225) once that is on main.
/// </summary>
public static class GustFx
{
    public const float Seconds = 20, Radius = 6, Top = 40, Speed = 6;

    /// <summary>Gusts let out this run (checks).</summary>
    public static int Count { get; private set; }

    public static void OnGust(ItemEvents n, ItemEvent e)
    {
        Count++;
        n.Sound3D(e.Position, RewardSounds.Gust, 1f, -2f, unitSize: 10f, maxDistance: 200f);
        var column = new Node3D { Name = "Gust" };
        n.AddChild(column);
        column.GlobalPosition = e.Position;
        var material = RewardMeshes.GlowMaterial();
        // rings that rise through the column and start again from the ground: nodes moved, nothing rebuilt
        for (int i = 0; i < 6; i++)
        {
            var ring = new MeshInstance3D { Mesh = RewardMeshes.Ring(), MaterialOverride = material, Rotation = new Vector3(Mathf.Pi / 2, 0, 0),
                Scale = Vector3.One * Radius * (0.6f + 0.08f * i), Position = new Vector3(0, i * Top / 6, 0), Transparency = 0.55f };
            column.AddChild(ring);
            var t = ring.CreateTween().SetLoops((int)(Seconds / (Top / Speed)) + 1);
            t.TweenProperty(ring, "position:y", Top, (Top - i * Top / 6) / Speed);
            t.TweenProperty(ring, "position:y", 0f, 0);
            t.TweenProperty(ring, "position:y", i * Top / 6, i * Top / 6 / Speed);
        }
        var end = column.CreateTween();
        end.TweenInterval(Seconds);
        end.TweenCallback(Callable.From(column.QueueFree));
    }
}
