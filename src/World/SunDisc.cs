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
///
/// <para>
/// Its child <c>SunShafts</c> lays light shafts over the screen (<c>shaders/sun_shafts.gdshader</c>): ridges,
/// trees and buildings cut beams out of the glow, strongest with the sun low. The "Sun rays" setting, off by
/// default on phones and standalone headsets (<see cref="GameSettings.SunRays"/>).
/// </para>
/// </summary>
public partial class SunDisc : MeshInstance3D
{
    /// <summary>The sun's angular radius seen from the Earth, rad (its distance changes it by 1.7 %).</summary>
    public const float TrueRadius = 0.00465f;

    /// <summary>The quad's angular half-size, rad: the corona fades to nothing inside it.</summary>
    public const float Reach = 0.14f;

    /// <summary>The quad's half-size in vacuum (#1989), rad: room for the glare's wide bloom (20°).</summary>
    public const float VacuumReach = 0.35f;

    private static readonly StringName CoreR = "core_r", ReachP = "reach", Warm = "warm", Brightness = "brightness",
        SunDir = "sun_dir", ShaftColour = "shaft_colour", Spikes = "spikes", Glare = "glare";

    /// <summary>The starburst's strength on the ground (in space the atmosphere's sun has it full).</summary>
    private const float GroundSpikes = 0.15f;

    /// <summary>The starburst in vacuum (#1989): full, as the atmosphere's sun in space (a camera's glare against black).</summary>
    private const float VacuumSpikes = 1f;

    /// <summary>The shafts' strength high in the sky and on the horizon.</summary>
    private const float ShaftsHigh = 0.035f, ShaftsLow = 0.3f;

    private readonly ShaderMaterial _material, _shaftMaterial;
    private readonly MeshInstance3D _shafts;
    private (float Core, float Warm, float Bright, float Spikes, float Glare, float Reach) _shown = (-1, -1, -1, -1, -1, -1);
    private (Vector3 Colour, float Warm) _shaftsShown = (-Vector3.One, -1);

    public SunDisc()
    {
        Name = "SunDisc";
        TopLevel = true;
        Visible = false;
        CastShadow = ShadowCastingSetting.Off;
        IgnoreOcclusionCulling = true;   // the sky: depth hides it, never the occlusion buffer (MoonDisc)
        GIMode = GIModeEnum.Disabled;
        Mesh = new QuadMesh { Size = new Vector2(2f, 2f) };
        _material = NewMaterial();
        MaterialOverride = _material;
        _shaftMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/sun_shafts.gdshader") };
        _shafts = new MeshInstance3D
        {
            Name = "SunShafts",
            // a quad the shader lays over the whole screen, never culled (as the atmosphere's)
            Mesh = new QuadMesh { Size = new Vector2(2f, 2f) },
            CustomAabb = new Aabb(-Vector3.One * 1e7f, Vector3.One * 2e7f),
            MaterialOverride = _shaftMaterial,
            CastShadow = ShadowCastingSetting.Off,
            GIMode = GIModeEnum.Disabled,
            IgnoreOcclusionCulling = true,
            Visible = false,
        };
        AddChild(_shafts);
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
        // above the air the atmosphere's own sun takes over; well under the horizon nothing shows. With no air at all
        // (#1989: the Moon) there is no shell to draw it: this one, the vacuum's sun, white, its full starburst
        bool vacuum = Atmosphere.Airless;
        float bright = (vacuum ? 1f : 1f - Atmosphere.Space) * Horizon(toSun);
        if (bright <= 0.001f)
        {
            Visible = false;
            return;
        }
        float fov = Mathf.DegToRad(CameraLens.FovOf(cam));
        float core = Mathf.Max(TrueRadius, fov * MoonDisc.ScreenShare);
        // through more air low down: warmer and wider (0 high, 1 on the horizon); in vacuum white down to the horizon
        float warm = vacuum ? 0f : 1f - Mathf.SmoothStep(0.02f, 0.35f, toSun.Y);

        // a quad square to the sun's direction, its up from the camera's (so it never turns edge-on)
        float distance = cam.Far * 0.9f;
        var up = cam.GlobalBasis.Y;
        if (Mathf.Abs(up.Dot(toSun)) > 0.99f) up = cam.GlobalBasis.X;
        var right = up.Cross(toSun).Normalized();
        up = toSun.Cross(right).Normalized();
        float reach = vacuum ? VacuumReach : Reach;
        float half = distance * Mathf.Tan(reach);
        GlobalTransform = new Transform3D(new Basis(right * half, up * half, -toSun), cam.GlobalPosition + toSun * distance);
        Visible = true;

        // light shafts are the air's: none in vacuum
        PlaceShafts(toSun, warm, vacuum ? 0f : bright);

        // a lens's starburst round a small bright point: gone once the disc is big on screen (a telescope's view)
        float spikes = (vacuum ? VacuumSpikes : GroundSpikes) * (1f - Mathf.SmoothStep(0.02f, 0.06f, core / fov));
        // the vacuum's wide glare, gone with the starburst once the disc is big on screen
        float glare = vacuum ? 1f - Mathf.SmoothStep(0.02f, 0.06f, core / fov) : 0f;
        var shown = (core, warm, bright, spikes, glare, reach);
        if (shown == _shown) return;
        _shown = shown;
        _material.SetShaderParameter(CoreR, core);
        _material.SetShaderParameter(Warm, warm);
        _material.SetShaderParameter(Brightness, bright);
        _material.SetShaderParameter(Spikes, spikes);
        _material.SetShaderParameter(Glare, glare);
        _material.SetShaderParameter(ReachP, reach);
    }

    /// <summary>
    /// How much of the sun is over the horizon, 0..1: on the Earth by world Y (the ground frame's up); off it (#2035) outside the
    /// anchor body's disc (<see cref="Atmosphere.BodyDisc"/>), and always on the Sun itself. 5 AU out, anchored on the Sun,
    /// the Sun was "under the horizon" by world Y and faded to a grey glow.
    /// </summary>
    private static float Horizon(Vector3 toSun)
    {
        if (Atmosphere.OnSun) return 1f;
        var (down, radius) = Atmosphere.BodyDisc;
        if (radius < 0f) return Mathf.SmoothStep(-0.06f, -0.01f, toSun.Y);
        float angle = Mathf.Acos(Mathf.Clamp(toSun.Dot(down), -1f, 1f));
        return Mathf.SmoothStep(radius - 0.01f, radius + 0.04f, angle);
    }

    private void PlaceShafts(Vector3 toSun, float warm, float bright)
    {
        float strength = GameSettings.Current.SunRays ? Mathf.Lerp(ShaftsHigh, ShaftsLow, warm) * bright : 0f;
        _shafts.Visible = strength > 0.002f;
        if (!_shafts.Visible) return;
        _shaftMaterial.SetShaderParameter(SunDir, toSun);
        var tint = new Color(1f, 0.86f, 0.66f).Lerp(new Color(1f, 0.6f, 0.3f), warm);
        var colour = new Vector3(tint.R, tint.G, tint.B) * strength;
        if ((colour, warm) == _shaftsShown) return;
        _shaftsShown = (colour, warm);
        _shaftMaterial.SetShaderParameter(ShaftColour, colour);
        _shaftMaterial.SetShaderParameter(Warm, warm);
    }
}
