using Godot;

namespace UnitSport.Core;

/// <summary>
/// Takes <see cref="Flicker"/>'s frames (#788): the viewport rendered <see cref="Frames"/> times while
/// the camera creeps sideways and up through its <see cref="Camera3D.HOffset"/> /
/// <see cref="Camera3D.VOffset"/> (nothing that steers it is touched), then the last frame with every
/// flickering pixel painted magenta. Used by the model viewer's <c>--flicker</c> and the debug
/// menu's "Flicker shot" (docs/notes/general/zfight.md).
/// </summary>
public static class FlickerShot
{
    public const int Frames = 16;

    /// <summary>The creep per frame, as a share of the distance to what is looked at: 2 mm at 10 m.</summary>
    public const float StepPerMetre = 0.0002f;

    public sealed record Result(Image Heat, int Flickering, int Pixels)
    {
        public float Percent => Pixels == 0 ? 0 : 100f * Flickering / Pixels;
    }

    /// <summary>
    /// <paramref name="distance"/>: how far the camera is from what it looks at (sets the creep).
    /// Hold the camera still meanwhile: anything else that moves flickers too.
    /// </summary>
    public static async Task<Result> Take(Node host, Camera3D camera, float distance)
    {
        var tree = host.GetTree();
        var viewport = host.GetViewport();
        float h = camera.HOffset, v = camera.VOffset;
        float step = Mathf.Max(distance, 0.5f) * StepPerMetre;
        var frames = new List<byte[]>(Frames);
        Image last = null!;
        try
        {
            for (int k = 0; k < Frames; k++)
            {
                camera.HOffset = h + step * k;
                camera.VOffset = v + step * 0.37f * k;
                await host.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                last = viewport.GetTexture().GetImage();
                if (last.GetFormat() != Image.Format.Rgba8) last.Convert(Image.Format.Rgba8);
                frames.Add(last.GetData());
            }
        }
        finally
        {
            camera.HOffset = h;
            camera.VOffset = v;
        }

        int width = last.GetWidth(), height = last.GetHeight();
        var turns = Flicker.Reversals(frames, width, height);
        var heat = Image.CreateFromData(width, height, false, Image.Format.Rgba8, frames[0]);
        var magenta = new Color(1f, 0.1f, 0.85f);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (turns[y * width + x] >= Flicker.MinReversals)
                    heat.SetPixel(x, y, heat.GetPixel(x, y).Lerp(magenta, 0.8f));
        return new Result(heat, Flicker.Count(turns), width * height);
    }
}
