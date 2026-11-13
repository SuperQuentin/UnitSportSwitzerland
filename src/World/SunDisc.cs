using Godot;
using UnitSport.Core;

namespace UnitSport.World;

/// <summary>
/// The real sun in the sky (#1479), drawn as the moon is (<see cref="MoonDisc"/>): a disc at its ephemeris
/// place (<see cref="DayNight.Sky"/>), at its true 0.27° radius (never smaller than the moon's least share of
/// the view, so the two read alike), limb-darkened, with a corona that grows warmer and wider through more air
/// near the horizon (<c>shaders/sun.gdshader</c>). Under the real sun the scene's light is never drawn in the
/// sky (<see cref="DayNight"/>): before, the Cartoon sky drew a 20° soft glow along the light, lifted 3° above
/// the horizon at sunrise, and no disc at all. Above the air the atmosphere draws its own (#1798), so this one
/// fades out with <see cref="Atmosphere.Space"/>.
///
/// <para>
/// A quad turned toward the camera along the sun's direction, just inside the far plane, placed each frame
/// before drawing for whichever camera is current: a ridge or a building hides it. Built once; only its
/// transform and a few shader parameters move.
/// </para>
/// </summary>
public partial class SunDisc : MeshInstance3D
{
    /// <summary>The sun's angular radius seen from the Earth, rad (its distance changes it by 1.7 %).</summary>
    public const float TrueRadius = 0.00465f;

    /// <summary>The quad's angular half-size, rad: the corona fades to nothing inside it.</summary>
    public const float Reach = 0.14f;

    private static readonly StringName CoreR = "core_r", ReachP = "reach", Warm = "warm", Brightness = "brightness";

    private readonly ShaderMaterial _material;
    private (float Core, float Warm, float Bright) _shown = (-1, -1, -1);

    public SunDisc()
    {
        Name = "SunDisc";
        TopLevel = true;
        Visible = false;
        CastShadow = ShadowCastingSetting.Off;
        GIMode = GIModeEnum.Disabled;
        Mesh = new QuadMesh { Size = new Vector2(2f, 2f) };
        _material = NewMaterial();
        MaterialOverride = _material;
    }

    /// <summary>The sun's material (the sky check reads it too).</summary>
    public static ShaderMaterial NewMaterial()
    {
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/sun.gdshader"), RenderPriority = -2 };
        material.SetShaderParameter(ReachP, Reach);
        return material;
    }

    public override void _EnterTree() => RenderingServer.FramePreDraw += Place;

    public override void _ExitTree() => RenderingServer.FramePreDraw -= Place;

    private void Place()
    {
        if (DayNight.Instance is not { Sky: { } sky } || GetViewport()?.GetCamera3D() is not { } cam)
        {
            Visible = false;
            return;
        }
        var toSun = sky.Sun.Normalized();
        // above the air the atmosphere's own sun takes over; well under the horizon nothing shows
        float bright = (1f - Atmosphere.Space) * Mathf.SmoothStep(-0.06f, -0.01f, toSun.Y);
        if (bright <= 0.001f)
        {
            Visible = false;
            return;
        }
        float fov = Mathf.DegToRad(CameraLens.FovOf(cam));
        float core = Mathf.Max(TrueRadius, fov * MoonDisc.MinScreenShare);
        // through more air low down: warmer and wider (0 high, 1 on the horizon)
        float warm = 1f - Mathf.SmoothStep(0.02f, 0.35f, toSun.Y);

        // a quad square to the sun's direction, its up from the camera's (so it never turns edge-on)
        float distance = cam.Far * 0.9f;
        var up = cam.GlobalBasis.Y;
        if (Mathf.Abs(up.Dot(toSun)) > 0.99f) up = cam.GlobalBasis.X;
        var right = up.Cross(toSun).Normalized();
        up = toSun.Cross(right).Normalized();
        float half = distance * Mathf.Tan(Reach);
        GlobalTransform = new Transform3D(new Basis(right * half, up * half, -toSun), cam.GlobalPosition + toSun * distance);
        Visible = true;

        var shown = (core, warm, bright);
        if (shown == _shown) return;
        _shown = shown;
        _material.SetShaderParameter(CoreR, core);
        _material.SetShaderParameter(Warm, warm);
        _material.SetShaderParameter(Brightness, bright);
    }
}
