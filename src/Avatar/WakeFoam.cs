using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The foam boats leave on the water and throw off it (#380), drawn on the moving waves
/// (<c>shaders/wake_foam.gdshader</c>, the surface's own wave function) in every style. One
/// material per kind, shared; the water under each emitter (still level and wave scale) is a
/// per-instance parameter, written only when it changes.
/// </summary>
public static class WakeFoam
{
    private static ShaderMaterial? _flat, _spray;
    private static Shader? _shader;
    private static readonly StringName StillLevel = "still_level", WaveScale = "wave_scale", FlatParam = "lying";

    /// <summary>The material for patches lying on the water (<paramref name="flat"/>) or specks of spray facing the camera.</summary>
    public static ShaderMaterial Material(bool flat)
    {
        if (flat && _flat != null) return _flat;
        if (!flat && _spray != null) return _spray;
        _shader ??= GD.Load<Shader>("res://shaders/wake_foam.gdshader");
        var m = new ShaderMaterial { Shader = _shader, RenderPriority = 1 };
        m.SetShaderParameter(FlatParam, flat);
        return flat ? _flat = m : _spray = m;
    }

    /// <summary>
    /// Puts an emitter at <paramref name="at"/> (world) on the surface there, <paramref name="lift"/>
    /// over it, and hands its foam the still water there. <paramref name="shown"/> keeps what was
    /// last written (still level, wave scale). False where there is no water.
    /// </summary>
    public static bool OnSurface(GpuParticles3D p, Vector3 at, float lift, ref Vector2 shown)
    {
        if (!World.WaterField.TryLevelAt(at, out float level)) return false;
        p.GlobalPosition = at with { Y = level + lift };
        Water(p, at, ref shown);
        return true;
    }

    /// <summary>Hands an emitter's foam the still water at <paramref name="at"/> (world), without moving it.</summary>
    public static void Water(GeometryInstance3D p, Vector3 at, ref Vector2 shown)
    {
        if (!World.WaterField.TryGetStill(at, out float still, out float scale)) return;
        var now = new Vector2(Mathf.Snapped(still, 0.001f), Mathf.Snapped(scale, 0.01f));
        if (now == shown) return;
        shown = now;
        p.SetInstanceShaderParameter(StillLevel, now.X);
        p.SetInstanceShaderParameter(WaveScale, now.Y);
    }
}
