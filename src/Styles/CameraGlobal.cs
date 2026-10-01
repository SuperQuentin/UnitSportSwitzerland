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
/// </summary>
public partial class CameraGlobal : Node
{
    public CameraGlobal() => Name = "CameraGlobal";

    public override void _EnterTree() => RenderingServer.FramePreDraw += Push;

    public override void _ExitTree() => RenderingServer.FramePreDraw -= Push;

    private void Push()
    {
        if (GetViewport()?.GetCamera3D() is { } cam)
            RenderingServer.GlobalShaderParameterSet("world_cam_pos", cam.GlobalPosition);
    }
}
