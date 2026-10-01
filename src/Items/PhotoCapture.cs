using Godot;

namespace UnitSport.Items;

/// <summary>
/// Takes the picture: renders what the camera's viewfinder looks at into a frame of its own, not
/// a grab of the screen. The screen also holds the HUD layers (chat, prompts, the hurt vignette,
/// the developing card of the previous shot...), the held item, and in third person over the
/// shoulder whatever the chase camera sees. Here a one-shot <see cref="SubViewport"/> sharing the
/// world draws a camera put exactly at the eye, at the viewfinder's focal length (not the FOV the
/// screen is still easing toward), without the viewmodel layer. Docs: <c>docs/notes/items/polaroid.md</c>.
/// </summary>
public static class PhotoCapture
{
    /// <summary>
    /// Renders one frame from <paramref name="eye"/>'s pose with vertical FOV <paramref name="fov"/>,
    /// the size of the screen (the print keeps its centre square). Resolves after the frame is drawn.
    /// </summary>
    public static async Task<Image> Render(Node host, Camera3D eye, float fov)
    {
        var screen = host.GetViewport();
        var size = screen.GetTexture().GetSize();
        var port = new SubViewport
        {
            Name = "PhotoCapture",
            Size = new Vector2I(Math.Max(16, (int)size.X), Math.Max(16, (int)size.Y)),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            HandleInputLocally = false,
            Msaa3D = screen.Msaa3D,
            ScreenSpaceAA = screen.ScreenSpaceAA,
            Scaling3DMode = screen.Scaling3DMode,
            Scaling3DScale = screen.Scaling3DScale,
        };
        var camera = new Camera3D
        {
            Name = "Lens",
            CullMask = eye.CullMask & ~HeldItemVisual.ViewmodelLayer,
            Projection = eye.Projection,
            KeepAspect = eye.KeepAspect,
            Fov = fov,
            Near = eye.Near,
            Far = eye.Far,
            Environment = eye.Environment,
            Attributes = eye.Attributes,
            Compositor = eye.Compositor,
        };
        port.AddChild(camera);
        host.AddChild(port);   // no World3D of its own: it shares the one the screen draws
        camera.GlobalTransform = eye.GlobalTransform;   // frozen at the moment of the shutter
        camera.MakeCurrent();
        try
        {
            // two drawn frames: the first one after adding it is the first it is rendered in
            await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            return port.GetTexture().GetImage();
        }
        finally
        {
            port.QueueFree();
        }
    }
}
