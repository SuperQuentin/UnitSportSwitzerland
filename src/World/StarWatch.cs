using System.Globalization;
using Godot;
using UnitSport.Core;

namespace UnitSport.World;

/// <summary>
/// <c>--starwatch</c> (windowed; #2035): every 10th drawn frame, how many stars show (bright points on a dark sky in the
/// picture, counted on a 4-pixel grid), the camera's distance from the world's origin and whether the star field's cull
/// box holds the camera. Beside any run that moves the camera (<c>--bodyshot ...,fly:V</c>); the RESULT line at the end
/// gives the least count seen once the field is up. "when moving toward a planet, all stars disappear ... all re-appears
/// when stopping": the field's box stayed round the origin while the camera flew out of it between re-anchors.
/// </summary>
public partial class StarWatch : Node
{
    public static bool Requested => CmdArgs.Has("--starwatch");

    private int _frames, _least = int.MaxValue, _samples;
    private bool _up;
    private int _saved;

    public override void _EnterTree() => RenderingServer.FramePostDraw += Sample;

    public override void _ExitTree()
    {
        RenderingServer.FramePostDraw -= Sample;
        if (_samples > 0)
            GD.Print(string.Create(CultureInfo.InvariantCulture,
                $"[starwatch] RESULT: {(_least > 0 ? "ok" : "FAILED")} — the least {_least} star(s) over {_samples} samples"));
    }

    private void Sample()
    {
        if (++_frames % 10 != 0 || GetViewport()?.GetCamera3D() is not { } cam) return;
        if (GetTree().Root.FindChild("StarField", true, false) is not StarField field || !field.Visible) return;
        var image = GetViewport().GetTexture().GetImage();
        int w = image.GetWidth(), h = image.GetHeight(), stars = 0;
        // a star: a bright pixel well over its neighbours four pixels off (not the sun's glare, a planet's disc or the ground)
        for (int y = 8; y < h - 8; y += 4)
            for (int x = 8; x < w - 8; x += 4)
            {
                float v = image.GetPixel(x, y).Luminance;
                if (v < 0.2f) continue;
                float round = Mathf.Max(Mathf.Max(image.GetPixel(x - 4, y).Luminance, image.GetPixel(x + 4, y).Luminance),
                    Mathf.Max(image.GetPixel(x, y - 4).Luminance, image.GetPixel(x, y + 4).Luminance));
                if (v - round > 0.15f) stars++;
            }
        var box = field.GlobalTransform * field.CustomAabb;
        bool holds = box.HasPoint(cam.GlobalPosition);
        _samples++;
        // counted from the first sample with stars up (a run starts on the Earth's daylit ground, a black sky after)
        if (stars > 20) _up = true;
        if (_up) _least = Mathf.Min(_least, stars);
        // a low frame once up: kept to look at (three at most)
        if (_up && stars <= 20 && _saved < 3)
        {
            _saved++;
            image.SavePng(ProjectSettings.GlobalizePath($"res://test_output/starwatch_low_{_frames}.png"));
        }
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[starwatch] frame {_frames}: {stars} star(s), the camera {cam.GlobalPosition.Length():E2} m from the origin, the field's box {(holds ? "holds" : "misses")} it; near {cam.Near:E2} m, far {cam.Far:E2} m"));
    }
}
