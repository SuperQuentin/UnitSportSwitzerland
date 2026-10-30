using Godot;
using UnitSport.Core;

namespace UnitSport.Combat;

/// <summary>
/// The impact frame (#1371): a one-to-two frame high-contrast flash with radial speed lines that fade over about 0.15 s, over the whole screen, on a
/// finisher's hit or a charged blow's. A fullscreen shader on a canvas layer: its one parameter is set only when it changes. Off with the
/// settings' "Impact frames" (photosensitivity) and never in VR (a canvas layer is not drawn there; the world carries the hit-stop and the spark).
/// </summary>
public partial class SwordImpact : CanvasLayer
{
    public static SwordImpact? Instance { get; private set; }

    /// <summary>Seconds the speed lines take to fade.</summary>
    public const float Seconds = 0.15f;

    private ColorRect _rect = null!;
    private ShaderMaterial _mat = null!;
    private float _left, _shown = -1f;

    private const string Code = @"
shader_type canvas_item;
uniform float intensity = 0.0;
void fragment() {
    vec2 p = UV - vec2(0.5);
    float r = length(p) * 1.6;
    float a = atan(p.y, p.x);
    // speed lines: thin spokes, stronger towards the edges, none in the middle
    float spokes = smoothstep(0.62, 0.95, abs(sin(a * 31.0 + sin(a * 7.0))));
    float lines = spokes * smoothstep(0.30, 0.85, r);
    // the first frames: a hard posterized flash, white then falling off
    float flash = step(0.82, intensity) * 0.55;
    COLOR = vec4(vec3(1.0), clamp((lines * 0.9 + flash) * intensity, 0.0, 1.0));
}";

    public override void _Ready()
    {
        Layer = 9;
        Name = "SwordImpact";
        _mat = new ShaderMaterial { Shader = new Shader { Code = Code } };
        _rect = new ColorRect { Material = _mat, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_rect);
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>A hit that earns an impact frame: starts the flash and the lines (when the setting is on).</summary>
    public void Flash()
    {
        if (!GameSettings.Current.ImpactFrames || XR.XrSession.Active) return;
        _left = Seconds;
    }

    public override void _Process(double delta)
    {
        if (_left <= 0f) { if (_rect.Visible) { _rect.Visible = false; _shown = -1f; } return; }
        _left = Mathf.Max(0f, _left - (float)delta);
        float k = _left / Seconds;
        if (!_rect.Visible) _rect.Visible = true;
        if (Mathf.Abs(k - _shown) > 0.01f) { _shown = k; _mat.SetShaderParameter(Intensity, k); }
    }

    private static readonly StringName Intensity = "intensity";
}
