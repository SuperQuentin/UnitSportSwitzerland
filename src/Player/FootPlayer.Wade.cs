using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.World;

namespace UnitSport.Player;

/// <summary>
/// Wading (#380): the walk slows with the water's depth over the feet, from the knees to the waist
/// (<see cref="Wading"/>), and the legs throw spray and leave foam on the moving surface, heard
/// stride by stride. The owner's depth is set by the water's turn in the physics
/// (<see cref="SwimPhysics"/>); the fx run on every peer from its own copy of the waves.
/// </summary>
public partial class FootPlayer
{
    /// <summary>Metres from the moving surface down to the feet while on foot and not swimming (owner); 0 out of the water.</summary>
    public float WadeDepth { get; private set; }

    /// <summary>The walk's pace in the water (<see cref="Wading.SpeedFactor"/>): 1 out of it.</summary>
    private float WadePace(bool running) => Wading.SpeedFactor(WadeDepth, running);

    private GpuParticles3D? _wadeSpray, _wadeFoam;
    private Vector2 _wadeSprayWater, _wadeFoamWater;
    private float _shownWadeSpray = -1f, _shownWadeFoam = -1f;

    /// <summary>The owner's depth this physics step (from <see cref="SwimPhysics"/>), and no sliding deeper than the knees.</summary>
    private void StepWade(bool wet, float sub)
    {
        WadeDepth = wet && sub > 0f ? sub : 0f;
        if (_sliding && WadeDepth > Wading.Knee) EndSlide();
    }

    /// <summary>
    /// Per frame, every peer: spray round the legs of a wader and foam left on the water, and on a
    /// remote copy its strides are heard through <c>Audio.BodySteps</c> (the owner's through <c>PlayerFeel</c>).
    /// </summary>
    private void TickWade(float dt)
    {
        if (_wadeSpray == null && (DisplayServer.GetName() == "headless" || !IsInsideTree())) return;
        bool owner = IsMultiplayerAuthority();
        bool walking = RideKindId == (int)RideKind.OnFoot && !IsSwimming && !Ragdolled && RidingWith == 0 && Visible;
        float depth = 0f, level = 0f;
        if (walking && WaterField.TryLevelAt(GlobalPosition, out level)) depth = Mathf.Max(0f, level - GlobalPosition.Y);
        float speed = owner ? MathX.FlatLength(Velocity) : PoseKind == PoseStride ? Anim.X : 0f;
        float spray = Wading.Spray(depth, speed);
        if (spray <= 0f && _wadeSpray == null) return;
        BuildWadeFx();
        float foam = depth > 0.15f && speed > 0.3f ? Mathf.Clamp(depth * 1.2f, 0.3f, 1f) * Mathf.Clamp(speed / 2f, 0.3f, 1f) : 0f;
        SetEmitting(_wadeSpray!, spray, ref _shownWadeSpray);
        SetEmitting(_wadeFoam!, foam, ref _shownWadeFoam);
        // a little ahead of the feet, where the shins push into the water
        var ahead = speed > 0.1f ? (owner ? Velocity with { Y = 0 } : -GlobalTransform.Basis.Z).Normalized() * 0.18f : Vector3.Zero;
        if (spray > 0f) WakeFoam.OnSurface(_wadeSpray!, GlobalPosition + ahead, 0.02f, ref _wadeSprayWater);
        if (foam > 0f) WakeFoam.OnSurface(_wadeFoam!, GlobalPosition + ahead * 0.5f, 0.01f, ref _wadeFoamWater);
    }

    private static void SetEmitting(GpuParticles3D p, float amount, ref float shown)
    {
        float q = Mathf.Round(amount * 10f) / 10f;
        if (q == shown) return;
        shown = q;
        p.Emitting = q > 0f;
        if (q > 0f) p.AmountRatio = q;
    }

    /// <summary>The two emitters, built the first time this player wades (on peers that draw).</summary>
    private void BuildWadeFx()
    {
        if (_wadeSpray != null) return;
        var foamColour = new Color(0.93f, 0.96f, 1f);
        _wadeSpray = WadeEmitter("WadeSpray", 70, 0.75f, 0.06f, lying: false, new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring,
            EmissionRingAxis = Vector3.Up, EmissionRingRadius = 0.26f, EmissionRingInnerRadius = 0.12f, EmissionRingHeight = 0.04f,
            Direction = Vector3.Up, Spread = 32f,
            InitialVelocityMin = 1.1f, InitialVelocityMax = 2.6f,
            Gravity = new Vector3(0, -9.8f, 0), DampingMin = 0.2f, DampingMax = 0.6f,
            ScaleMin = 0.6f, ScaleMax = 1.4f,
            Color = foamColour, ColorRamp = WadeFade(0.9f),
        });
        _wadeFoam = WadeEmitter("WadeFoam", 50, 1.6f, 0.17f, lying: true, new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring,
            EmissionRingAxis = Vector3.Up, EmissionRingRadius = 0.3f, EmissionRingInnerRadius = 0.15f, EmissionRingHeight = 0.01f,
            Direction = Vector3.Right, Spread = 180f, Flatness = 1f,
            InitialVelocityMin = 0.15f, InitialVelocityMax = 0.5f,
            Gravity = Vector3.Zero, DampingMin = 0.3f, DampingMax = 0.6f,
            ScaleMin = 0.7f, ScaleMax = 1.3f, ScaleCurve = WadeGrow(),
            Color = foamColour, ColorRamp = WadeFade(0.5f),
        });
    }

    private GpuParticles3D WadeEmitter(string name, int amount, float life, float size, bool lying, ParticleProcessMaterial mat)
    {
        var p = new GpuParticles3D
        {
            Name = name, Amount = amount, Lifetime = life, Emitting = false, ProcessMaterial = mat,
            DrawPass1 = new QuadMesh
            {
                Size = new Vector2(size, size),
                Orientation = lying ? PlaneMesh.OrientationEnum.Y : PlaneMesh.OrientationEnum.Z,
                Material = WakeFoam.Material(lying),
            },
            // left where they fell, on the water's own waves (WakeFoam)
            LocalCoords = false, TopLevel = true,
            VisibilityAabb = new Aabb(new Vector3(-4, -2, -4), new Vector3(8, 5, 8)),
        };
        AddChild(p);
        return p;
    }

    private static CurveTexture WadeGrow()
    {
        var c = new Curve();
        c.AddPoint(new Vector2(0, 0.5f));
        c.AddPoint(new Vector2(1, 1.8f));
        return new CurveTexture { Curve = c };
    }

    private static GradientTexture1D WadeFade(float alpha)
    {
        var g = new Gradient();
        g.SetColor(0, new Color(1, 1, 1, alpha));
        g.SetColor(1, new Color(1, 1, 1, 0));
        return new GradientTexture1D { Gradient = g };
    }
}
