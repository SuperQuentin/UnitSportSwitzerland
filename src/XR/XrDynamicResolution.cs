using Godot;
using UnitSport.Core;

namespace UnitSport.XR;

/// <summary>
/// Dynamic resolution with a floor on a standalone headset (#1112): each frame the headset renders
/// into a part of its eye buffers (OpenXR's render region, which the compositor stretches to the
/// display), as big as the GPU time allows and never smaller than <see cref="GameSettings.VrResolutionFloor"/>.
/// No swapchain is recreated and no scaling pass is added, so the Mobile renderer keeps its single
/// pass and the foveation. Meta's own dynamic resolution has no floor: with a frame far over budget
/// it sank until text and edges were mush, which is why it stays off (docs/notes/xr/quest-standalone.md).
/// </summary>
public partial class XrDynamicResolution : Node
{
    /// <summary>Above this share of the frame budget the picture shrinks; below the next, it grows.</summary>
    private const float ShrinkAbove = 0.88f, GrowBelow = 0.72f;
    /// <summary>Scale steps a frame: down fast (a missed frame shows), up slowly (no see-saw).</summary>
    private const float ShrinkStep = 0.02f, GrowStep = 0.004f;

    private readonly SubViewport _view;
    private readonly OpenXRInterface _xr;
    private readonly OpenXRApiExtension _api = new();
    private float _scale = 1f;
    private Rect2I _applied;
    private double _gpuAverage;

    /// <summary>The current share of the eye buffers' width and height being rendered.</summary>
    public float Scale => _scale;

    public XrDynamicResolution(SubViewport view, OpenXRInterface xr)
    {
        _view = view;
        _xr = xr;
    }

    public override void _Ready()
    {
        Name = "DynamicResolution";
        ProcessPriority = 1002;   // after the rig and the monitor
        RenderingServer.ViewportSetMeasureRenderTime(_view.GetViewportRid(), true);
    }

    public override void _Process(double delta)
    {
        float floor = Mathf.Clamp(GameSettings.Current.VrResolutionFloor, GameSettings.MinVrResolutionFloor, 1f);
        // the last measured frame's GPU time, smoothed over a few frames so one hitch does not jolt it
        double gpu = RenderingServer.ViewportGetMeasuredRenderTimeGpu(_view.GetViewportRid());
        if (gpu > 0) _gpuAverage = _gpuAverage <= 0 ? gpu : _gpuAverage + (gpu - _gpuAverage) * 0.25;
        // under SpaceWarp the game renders every other frame: two of the headset's frames each
        double budget = 1000.0 / Mathf.Max(30f, _xr.DisplayRefreshRate) * (XrSession.SpaceWarp ? 2 : 1);
        if (_gpuAverage > budget * ShrinkAbove) _scale -= ShrinkStep;
        else if (_gpuAverage < budget * GrowBelow) _scale += GrowStep;
        _scale = Mathf.Clamp(_scale, floor, 1f);

        var full = _xr.GetRenderTargetSize();
        var size = new Vector2I(Mathf.RoundToInt(full.X * _scale), Mathf.RoundToInt(full.Y * _scale));
        var region = _scale >= 0.999f ? new Rect2I() : new Rect2I(Vector2I.Zero, size);
        // a pixel's change is not worth a new region for the renderer and the foveation
        if (Mathf.Abs(region.Size.X - _applied.Size.X) < 4 && region.HasArea() == _applied.HasArea()) return;
        _api.SetRenderRegion(region);
        _applied = region;
    }
}
