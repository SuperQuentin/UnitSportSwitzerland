using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.World;

/// <summary>
/// What shows a glider pilot where the air rises (#1225, <c>docs/notes/world/air-lift.md</c>): pale
/// pollen-dust specks rising in each visible thermal within 1.5 km of the camera (at most
/// <see cref="Max"/>), and a cumulus puff at the top of a strong one. Asked every 5 s; emitters are moved
/// on change and never rebuilt (<c>no-mesh-rebuild-per-frame</c>): the motion is the particle shader's.
/// Client only; nothing at night, in a match or with <c>--lift off</c> (<see cref="AirLiftField"/> answers none).
/// </summary>
public partial class ThermalCues : Node3D
{
    public const int Max = 4;
    private const double Every = 5.0;
    private const float Reach = 1500f;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly GpuParticles3D[] _specks = new GpuParticles3D[Max];
    private readonly MeshInstance3D[] _puffs = new MeshInstance3D[Max];
    private readonly Thermal[] _found = new Thermal[Max];
    private double _next;
    private int _shown = -1;

    public ThermalCues(ChunkManager chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
        Name = "ThermalCues";
    }

    public override void _Ready()
    {
        // the style's own speck colour: warm pollen in Cartoon, grey dust in PS1, faint in Realistic
        var tint = Styles.StyleKit.Style switch
        {
            Styles.VisualStyle.Ps1 => new Color(0.85f, 0.85f, 0.8f, 0.8f),
            Styles.VisualStyle.Cartoon => new Color(1f, 0.95f, 0.7f, 0.85f),
            _ => new Color(1f, 1f, 0.95f, 0.45f),
        };
        var speck = new QuadMesh { Size = new Vector2(1.2f, 1.2f) };
        speck.Material = new StandardMaterial3D
        {
            AlbedoColor = tint,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
        };
        var cloud = new SphereMesh { Radius = 40f, Height = 36f, RadialSegments = 10, Rings = 5 };
        var cloudMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 1f, 1f, 0.75f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        for (int i = 0; i < Max; i++)
        {
            _specks[i] = new GpuParticles3D
            {
                Amount = 400, Lifetime = 90, Preprocess = 90, Visible = false, DrawPass1 = speck,
                VisibilityAabb = new Aabb(new Vector3(-90, -60, -90), new Vector3(180, 1300, 180)),
                ProcessMaterial = new ParticleProcessMaterial
                {
                    EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
                    EmissionBoxExtents = new Vector3(30, 60, 30),
                    Direction = Vector3.Up, Spread = 12f,
                    InitialVelocityMin = 3f, InitialVelocityMax = 7f,
                    Gravity = Vector3.Zero,
                    TurbulenceEnabled = true, TurbulenceNoiseStrength = 2f,
                },
            };
            AddChild(_specks[i]);
            _puffs[i] = new MeshInstance3D { Mesh = cloud, MaterialOverride = cloudMaterial, Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_puffs[i]);
        }
    }

    public override void _Process(double delta)
    {
        double now = Core.GameClock.Now;
        if (now < _next) return;
        _next = now + Every;
        var cam = GetViewport().GetCamera3D();
        int n = 0;
        if (cam != null)
        {
            AirLiftField.Use(_chunks, _origin);
            n = AirLiftField.ThermalsNear(cam.GlobalPosition, Reach, _found);
        }
        if (n != _shown)
        {
            _shown = n;
            GD.Print($"[lift] {n} thermal(s) shown near the camera");
        }
        for (int i = 0; i < Max; i++)
        {
            bool on = i < n && AirLiftField.ToWorld(_found[i]) is not null;
            if (_specks[i].Visible != on) { _specks[i].Visible = on; _specks[i].Emitting = on; }
            bool puff = on && _found[i].Strength > 0.5f;
            if (_puffs[i].Visible != puff) _puffs[i].Visible = puff;
            if (!on) continue;
            var at = AirLiftField.ToWorld(_found[i])!.Value;
            var base_ = at + Vector3.Up * 20f;
            if (_specks[i].GlobalPosition.DistanceSquaredTo(base_) > 1f) _specks[i].GlobalPosition = base_;
            var top = at + Vector3.Up * _found[i].Top;
            if (puff && _puffs[i].GlobalPosition.DistanceSquaredTo(top) > 1f) _puffs[i].GlobalPosition = top;
        }
    }
}
