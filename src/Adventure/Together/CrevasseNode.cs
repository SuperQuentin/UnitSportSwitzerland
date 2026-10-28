using Godot;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Adventure.Together;

/// <summary>
/// A hidden crevasse whose snow bridge broke (#1266 part 4): the opening and its blue ice walls going down 10 m, made on
/// the owner only where the bridge broke, and freed after a while. The fallback the plan names: the body stumbles at the
/// lip, winded, rather than falling into a slot with its own collision (see <c>docs/notes/adventure/links.md</c>).
/// </summary>
public sealed partial class CrevasseNode : Node3D
{
    public const float Depth = 10f, Length = 12f;
    private static ArrayMesh? _mesh;

    /// <summary>The slot: two ice walls, the dark floor far down, the broken snow lips. Built once.</summary>
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
        s.Box(new Vector3(0, -Depth, 0), new Vector3(w * 2, 0.2f, Length), deep);
        s.Box(new Vector3(0, -Depth * 0.5f, 0), new Vector3(w * 1.6f, 0.05f, Length * 0.9f), new Color(deep, 0.6f));
        // the broken bridge's lips, ragged slabs of snow at the edges
        for (int i = 0; i < 6; i++)
        {
            float z = -Length / 2 + (i + 0.5f) * Length / 6;
            s.Box(new Vector3(-w - 0.25f, 0.02f, z), new Vector3(0.5f, 0.12f, 1.8f), snow);
            s.Box(new Vector3(w + 0.25f, 0.02f, z + 0.5f), new Vector3(0.5f, 0.12f, 1.6f), snow);
        }
        return _mesh = s.Build();
    }

    public override void _Ready()
    {
        AddChild(new MeshInstance3D { Mesh = Slot(), MaterialOverride = HumanMeshBuilder.Material() });
        GetTree().CreateTimer(30.0).Timeout += () => { if (IsInstanceValid(this)) QueueFree(); };
    }
}
