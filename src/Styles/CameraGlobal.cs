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

    public override void _EnterTree() => RenderingServer.FramePreDraw += Push;

    public override void _ExitTree() => RenderingServer.FramePreDraw -= Push;

    private void Push()
    {
        if (GetViewport()?.GetCamera3D() is { } cam)
        {
            var at = cam.GlobalPosition;
            if (Interiors.InteriorManager.Instance is { } interiors) at = interiors.ThroughNearestDoor(at);
            RenderingServer.GlobalShaderParameterSet("world_cam_pos", at);
        }
    }
}
