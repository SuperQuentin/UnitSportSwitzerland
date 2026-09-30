using Godot;
using UnitSport.Audio;
using UnitSport.Core;

namespace UnitSport.Items;

/// <summary>A flag driven into the ground, on every peer that sees it happen: the flag springs up from the stab, a thud, a puff of dirt. Local only.</summary>
public static class FlagFx
{
    private static StandardMaterial3D? _dust;

    /// <summary>How many live planting effects ran on this peer (probes).</summary>
    public static int Spawns { get; private set; }

    public static void Spawned(Node3D flag)
    {
        if (!flag.IsInsideTree()) return;
        Spawns++;
        var foot = flag.GlobalPosition;
        flag.Scale = new Vector3(1f, 0.6f, 1f);
        var tween = flag.CreateTween();
        tween.TweenProperty(flag, "scale:y", 1f, 0.2f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);

        var thud = new AudioStreamPlayer3D
        {
            Stream = SfxSynth.Impact, PitchScale = 0.7f, VolumeDb = -4f, UnitSize = 8f, MaxDistance = 120f,
            Bus = SfxBus.Name, TopLevel = true,
        };
        flag.AddChild(thud);
        thud.GlobalPosition = foot;
        thud.Finished += thud.QueueFree;
        thud.Play();

        _dust ??= new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            AlbedoColor = new Color(0.45f, 0.36f, 0.27f, 0.7f),
            VertexColorUseAsAlbedo = true,
        };
        var puff = new CpuParticles3D
        {
            TopLevel = true, OneShot = true, Emitting = true, Amount = 10, Lifetime = 0.55f, Explosiveness = 1f,
            Mesh = new QuadMesh { Size = new Vector2(0.14f, 0.14f), Material = _dust },
            EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.08f,
            Direction = Vector3.Up, Spread = 70f, InitialVelocityMin = 0.5f, InitialVelocityMax = 1.1f,
            Gravity = new Vector3(0, -2.5f, 0), ScaleAmountMin = 0.6f, ScaleAmountMax = 1.4f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        var fade = new Gradient();
        fade.SetColor(0, new Color(1, 1, 1, 0.8f));
        fade.SetColor(1, new Color(1, 1, 1, 0f));
        puff.ColorRamp = fade;
        flag.AddChild(puff);
        puff.GlobalPosition = foot + Vector3.Up * 0.05f;
        flag.GetTree().CreateTimer(1.2).Timeout += () => { if (GodotObject.IsInstanceValid(puff)) puff.QueueFree(); };
    }
}
