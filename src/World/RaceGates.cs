using Godot;

namespace UnitSport.World;

/// <summary>
/// The rings of an air race, drawn for the players racing it: a low-poly torus per gate facing
/// down the course, the next one to fly through in yellow, the finish in red, the ones already
/// passed hidden.
/// </summary>
public partial class RaceGates : Node3D, Core.IOriginContainer
{
    private readonly System.Collections.Generic.List<MeshInstance3D> _rings = new();
    private StandardMaterial3D _idle = null!, _next = null!, _finish = null!;

    public static RaceGates Build(Vector3[] gates, float radius)
    {
        var g = new RaceGates { Name = "RaceGates", TopLevel = true };
        g._idle = Paint(new Color(0.95f, 0.95f, 0.95f));
        g._next = Paint(new Color(1f, 0.85f, 0.1f));
        g._finish = Paint(new Color(0.9f, 0.15f, 0.1f));
        var mesh = new TorusMesh { InnerRadius = radius - 1.5f, OuterRadius = radius + 1.5f, Rings = 16, RingSegments = 4 };
        for (int i = 0; i < gates.Length; i++)
        {
            // the torus's axis is its Y: point it down the course. x = y × z keeps the basis
            // right-handed, and the course is flat, so it is never degenerate
            var y = Player.RaceRoute.Flat(gates[Mathf.Min(i + 1, gates.Length - 1)] - gates[Mathf.Max(i - 1, 0)]).Normalized();
            var ring = new MeshInstance3D
            {
                Name = $"Gate{i}",
                Mesh = mesh,
                Transform = new Transform3D(new Basis(y.Cross(Vector3.Up), y, Vector3.Up), gates[i]),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            g._rings.Add(ring);
            g.AddChild(ring);
        }
        g.Highlight(0);
        return g;
    }

    public void Highlight(int next)
    {
        for (int i = 0; i < _rings.Count; i++)
        {
            _rings[i].Visible = i >= next;
            _rings[i].MaterialOverride = i == next ? _next : i == _rings.Count - 1 ? _finish : _idle;
        }
    }

    private static StandardMaterial3D Paint(Color c) => new()
    {
        AlbedoColor = c,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    };
}
