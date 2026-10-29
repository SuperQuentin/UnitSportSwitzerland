using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Adventure.Together;

/// <summary>
/// A hidden crevasse whose snow bridge broke (#1266 part 4, #1372): an ice slot 10 m deep and 14 m long, made on the
/// owner only, where the bridge broke, with its own collision (two walls, two ends, a floor, all on
/// <see cref="FootPlayer.CrevasseLayer"/>: only a body in the slot has that layer in its mask, the ground it let go of).
/// The walls are plain static bodies, so the climbing rules take them (ice, not terrain cover). The node's origin is
/// the break on the surface, its local Z along the strip. Built once when the bridge breaks, freed after a while;
/// nothing moves or rebuilds per frame.
/// </summary>
public sealed partial class CrevasseNode : Node3D
{
    public const float Depth = 10f, Length = 14f, Thick = 0.6f;
    /// <summary>How long the open slot stays drawn, seconds.</summary>
    public const double Lifetime = 120.0;
    private static ArrayMesh? _mesh;

    /// <summary>Local Y of the walls' tops (a little over the surface, so a slope's lip is above the ground on both ends).</summary>
    public float Top { get; set; } = 0.3f;

    /// <summary>The slot: two ice walls, the dark floor far down, the broken snow lips, the two ends. Built once.</summary>
    [Showcase("Adventure", "Crevasse")]
    public static ArrayMesh Slot()
    {
        if (_mesh != null) return _mesh;
        var s = new MeshScratch();
        float w = (float)Crevasses.Width / 2;
        var ice = new Color("7ab8d8");
        var deep = new Color("1a3a58");
        var snow = new Color("f2f6fa");
        s.Box(new Vector3(-w - 0.1f, -Depth / 2, 0), new Vector3(0.2f, Depth, Length), ice);
        s.Box(new Vector3(w + 0.1f, -Depth / 2, 0), new Vector3(0.2f, Depth, Length), ice);
        s.Box(new Vector3(0, -Depth + 0.1f, 0), new Vector3(w * 2, 0.2f, Length), deep);
        s.Box(new Vector3(0, -Depth * 0.5f, 0), new Vector3(w * 1.6f, 0.05f, Length * 0.9f), new Color(deep, 0.6f));
        // the ends, so the slot is a closed pit in the ice
        s.Box(new Vector3(0, -Depth / 2, -Length / 2 - 0.1f), new Vector3(w * 2 + 0.4f, Depth, 0.2f), ice);
        s.Box(new Vector3(0, -Depth / 2, Length / 2 + 0.1f), new Vector3(w * 2 + 0.4f, Depth, 0.2f), ice);
        // the broken bridge's lips, ragged slabs of snow at the edges
        for (int i = 0; i < 7; i++)
        {
            float z = -Length / 2 + (i + 0.5f) * Length / 7;
            s.Box(new Vector3(-w - 0.25f, 0.02f, z), new Vector3(0.5f, 0.12f, 1.8f), snow);
            s.Box(new Vector3(w + 0.25f, 0.02f, z + 0.5f), new Vector3(0.5f, 0.12f, 1.6f), snow);
        }
        return _mesh = s.Build();
    }

    public override void _Ready()
    {
        AddChild(new MeshInstance3D { Mesh = Slot(), MaterialOverride = HumanMeshBuilder.Material() });
        float w = (float)Crevasses.Width / 2;
        float height = Depth + Top, mid = (Top - Depth) / 2;
        var body = new StaticBody3D { Name = "Walls", CollisionLayer = FootPlayer.CrevasseLayer, CollisionMask = 0 };
        var side = new BoxShape3D { Size = new Vector3(Thick, height, Length + 2 * Thick) };
        var end = new BoxShape3D { Size = new Vector3(w * 2 + 2 * Thick, height, Thick) };
        var floor = new BoxShape3D { Size = new Vector3(w * 2 + 2 * Thick, Thick, Length + 2 * Thick) };
        void Add(Shape3D shape, Vector3 at) => body.AddChild(new CollisionShape3D { Shape = shape, Position = at });
        Add(side, new Vector3(-w - Thick / 2, mid, 0));
        Add(side, new Vector3(w + Thick / 2, mid, 0));
        Add(end, new Vector3(0, mid, -Length / 2 - Thick / 2));
        Add(end, new Vector3(0, mid, Length / 2 + Thick / 2));
        Add(floor, new Vector3(0, -Depth - Thick / 2, 0));
        AddChild(body);
        GetTree().CreateTimer(Lifetime).Timeout += () => { if (IsInstanceValid(this)) QueueFree(); };
    }
}
