using Godot;

namespace UnitSport.Core;

/// <summary>
/// Drives the distance-fog uniforms every world shader carries from the one Fog setting.
///
/// <para>
/// The fog code stays in the shaders and is simply pushed past the far plane when the setting
/// is off — so the two looks can be compared with a toggle, and turning fog back on costs
/// nothing but two floats. Off is the default: the whole point of the horizon layer is that
/// the ridges 60 km away are on screen, and fog would paint over them.
/// </para>
/// </summary>
public static class FogUniforms
{
    public const float OnStart = 3500f, OnEnd = 8000f;

    public static void Apply(Material? material)
    {
        if (material is not ShaderMaterial shader) return;
        bool on = GameSettings.Current.Fog;
        shader.SetShaderParameter("fog_start", on ? OnStart : 1e9f);
        shader.SetShaderParameter("fog_end", on ? OnEnd : 2e9f);
    }
}
