using Godot;

namespace UnitSport.Styles;

/// <summary>
/// Sets the <c>world_cam_pos</c> shader global to the player's camera just before each frame is
/// drawn, after everything has moved, so it is never a frame stale (a floating-origin shift
/// would otherwise flip near trees to billboards for one frame).
///
/// <para>
/// The tree shaders measure their near/far handover from it rather than from
/// <c>CAMERA_POSITION_WORLD</c>, so every view agrees on which trees are 3D: a cockpit
/// mirror, the zoom bubble and, in the lit styles, the sun's shadow pass.
/// </para>
///
/// <para>
/// Inside a building the camera is 3 km down with the interiors, which put every tree seen out
/// through a doorway past the handover: a billboard through the portal, 3D seen directly. So
/// there it is carried up through the building's doorway first, to where the portal camera stands.
/// </para>
///
/// <para>
/// It also sets <c>world_lod_scale</c>, how much the camera zooms over the walking view
/// (<see cref="LodScale"/>, #1433), and carries a zoom step on to the visibility ranges
/// (<see cref="ZoomRanges"/>) and to the sun's shadow distance: zoomed, the 3D trees reach
/// range x zoom, and past the 400 m cascades they lost the shade their billboards draw.
/// </para>
/// </summary>
public partial class CameraGlobal : Node
{
    public CameraGlobal() => Name = "CameraGlobal";

    /// <summary>The world's origin: the camera's altitude is read through it (#1430 H3: in an anchor frame Y is not the altitude).</summary>
    public Core.WorldOrigin? Origin { get; set; }

    /// <summary>The drop per square metre last pushed (<see cref="Core.Curvature"/>), NaN before the first push.</summary>
    private float _curvature = float.NaN;

    /// <summary><c>--curvature 0</c>: the flat world, as before #187 (a picture to compare with main's).</summary>
    public override void _Ready()
    {
        if (Core.CmdArgs.Double("--curvature") is 0) Core.Curvature.Drawn = false;
    }

    public override void _EnterTree() => RenderingServer.FramePreDraw += Push;

    public override void _ExitTree() => RenderingServer.FramePreDraw -= Push;

    private static readonly StringName WorldCamPos = "world_cam_pos", WorldLodScale = "world_lod_scale",
        CurvatureParam = "world_curvature", CurveBlendParam = "world_curve_blend", GroundYParam = "world_ground_y",
        BaseFogMeta = "base_fog_density";
    private float _groundY = float.NaN;
    private float _curveBlend = float.NaN, _fogScale = float.NaN;
    private Godot.Environment? _fogEnv;
    private float _pushedScale = 1f;

    // the sun whose shadow distance follows the zoom, and its own distance (a restyle makes a new sun)
    private DirectionalLight3D? _sun;
    private float _sunBase, _sunScale = 1f;

    private void Push()
    {
        if (GetViewport()?.GetCamera3D() is { } cam)
        {
            var at = cam.GlobalPosition;
            if (Interiors.InteriorManager.Instance is { } interiors) at = interiors.ThroughNearestDoor(at);
            RenderingServer.GlobalShaderParameterSet(WorldCamPos, at);
            LodScale.Update(cam);
            // the step, not the value: NearTrees and the visibility ranges gather by the step, and
            // a tree the shader made 3D past their reach would be neither 3D nor a billboard
            if (LodScale.Step != _pushedScale)
            {
                _pushedScale = LodScale.Step;
                RenderingServer.GlobalShaderParameterSet(WorldLodScale, _pushedScale);
            }
        }
        // the Earth's curvature in every world shader (#187), by the camera's height (#1430 H1): refraction
        // fades out from 5 to 20 km, the drop gives way to the exact sphere from 20 to 50 km. Pushed when it changes.
        var camera = GetViewport()?.GetCamera3D();
        double altitude = camera == null ? 0 : Origin?.AltitudeOf(camera.GlobalPosition) ?? camera.GlobalPosition.Y;
        float curvature = Core.Curvature.Drawn ? (float)Core.Curvature.DropPerM2At(altitude) : 0f;
        if (curvature != _curvature)
        {
            _curvature = curvature;
            RenderingServer.GlobalShaderParameterSet(CurvatureParam, curvature);
        }
        float blend = Core.Curvature.Drawn ? (float)Core.Curvature.SphereBlendAt(altitude) : 0f;
        if (blend != _curveBlend)
        {
            _curveBlend = blend;
            RenderingServer.GlobalShaderParameterSet(CurveBlendParam, blend);
        }
        // where sea level is in world Y: 0, but in the Space regime (the origin rises with the camera, #1430 H3)
        float groundY = (float)-(Origin?.Alt0 ?? 0);
        if (groundY != _groundY)
        {
            _groundY = groundY;
            RenderingServer.GlobalShaderParameterSet(GroundYParam, groundY);
        }
        if (camera != null) Climb(camera, altitude);
        ZoomRanges.Tick();
        ScaleShadows();
    }

    /// <summary>
    /// High up (#1430 H1): the view reaches past the geometric horizon (√(2Rh + h²) + 200 km above 5 km),
    /// and the style's haze thins with the air, kept at a sixth so the edge of the terrain data stays
    /// in the haze until the globe (H4). Below 3 km nothing changes.
    /// </summary>
    private void Climb(Camera3D camera, double altitude)
    {
        float far = Core.GameSettings.Current.CameraFar;
        if (altitude > 5_000) far = Mathf.Max(far, (float)(Core.Curvature.HorizonM(altitude) + 200_000));
        if (Mathf.Abs(camera.Far - far) > far * 0.01f) camera.Far = far;

        float scale = HazeAt(altitude, World.Globe.Present);
        if (GetViewport()?.FindWorld3D()?.Environment is not { } env || (scale == _fogScale && env == _fogEnv)) return;
        _fogScale = scale;
        _fogEnv = env;   // a restyle hands over a new one: its own ground haze is the base
        if (!env.HasMeta(BaseFogMeta)) env.SetMeta(BaseFogMeta, env.FogDensity);
        env.FogDensity = (float)env.GetMeta(BaseFogMeta) * scale;
    }

    /// <summary>
    /// The style's haze at a camera height, as a share of the ground's: 1 to 3 km, then thinning with
    /// the air, never under a sixth while it has to hide the edge of the data. With the globe drawn
    /// past that edge (#1430 H4) nothing needs hiding, and it thins on: depth fog counts distance, not
    /// air, so a sixth of it over the hundreds of kilometres seen from orbit drew a white wall.
    /// </summary>
    public static float HazeAt(double altitude, bool globe = false) =>
        altitude <= 3_000 ? 1f : Mathf.Max(globe ? 0f : 1f / 6f, Mathf.Exp(-(float)(altitude - 3_000) / 10_000f));

    private void ScaleShadows()
    {
        var sun = World.DayNight.Instance?.Sun;
        if (sun != _sun)
        {
            _sun = sun;
            _sunScale = 1f;
            if (sun != null) _sunBase = sun.DirectionalShadowMaxDistance;
        }
        if (_sun == null || !IsInstanceValid(_sun) || _sunScale == LodScale.Step) return;
        _sunScale = LodScale.Step;
        _sun.DirectionalShadowMaxDistance = _sunBase * _sunScale;
    }
}
