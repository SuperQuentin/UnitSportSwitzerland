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
/// </summary>
public partial class CameraGlobal : Node
{
    public CameraGlobal() => Name = "CameraGlobal";

    private static readonly StringName CamPos = "world_cam_pos", CurvatureParam = "world_curvature";

    /// <summary>The drop per square metre last pushed (<see cref="Core.Curvature"/>), NaN before the first push.</summary>
    private float _curvature = float.NaN;

    /// <summary><c>--curvature 0</c>: the flat world, as before #187 (a picture to compare with main's).</summary>
    public override void _Ready()
    {
        if (Core.CmdArgs.Double("--curvature") is 0) Core.Curvature.Drawn = false;
    }

    public override void _EnterTree() => RenderingServer.FramePreDraw += Push;

    public override void _ExitTree() => RenderingServer.FramePreDraw -= Push;

    private void Push()
    {
        if (GetViewport()?.GetCamera3D() is { } cam)
        {
            var at = cam.GlobalPosition;
            if (Interiors.InteriorManager.Instance is { } interiors) at = interiors.ThroughNearestDoor(at);
            RenderingServer.GlobalShaderParameterSet(CamPos, at);
        }
        // the Earth's curvature in every world shader (#187), pushed when it changes
        float curvature = Core.Curvature.Drawn ? (float)Core.Curvature.DropPerM2 : 0f;
        if (curvature != _curvature)
        {
            _curvature = curvature;
            RenderingServer.GlobalShaderParameterSet(CurvatureParam, curvature);
        }
    }
}
