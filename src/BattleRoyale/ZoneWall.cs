using Godot;

namespace UnitSport.BattleRoyale;

/// <summary>
/// The zone's edge as a wall you can see from kilometres away: an open cylinder on the circle,
/// 3 km tall around the camera's height, drawn by <c>shaders/br_zone_wall.gdshader</c>.
/// </summary>
public partial class ZoneWall : Node3D
{
    private const float Height = 3000f;
    private MeshInstance3D _mesh = null!;
    private ShaderMaterial _material = null!;

    public override void _Ready()
    {
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/br_zone_wall.gdshader") };
        _mesh = new MeshInstance3D
        {
            Mesh = new CylinderMesh
            {
                TopRadius = 1f, BottomRadius = 1f, Height = 1f, RadialSegments = 160, Rings = 1, CapTop = false, CapBottom = false,
            },
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // the circle is kilometres across: never culled for being "out of view"
            ExtraCullMargin = 16384f,
        };
        AddChild(_mesh);
    }

    /// <summary>Stands the wall on the circle of <paramref name="radius"/> around <paramref name="centre"/> (centre's height = the middle of the wall).</summary>
    public void Place(Vector3 centre, float radius)
    {
        _mesh.Visible = radius > 1f;
        if (radius <= 1f) return;
        GlobalPosition = centre;
        _mesh.Scale = new Vector3(radius, Height, radius);
        _material.SetShaderParameter("radius", radius);
    }
}
