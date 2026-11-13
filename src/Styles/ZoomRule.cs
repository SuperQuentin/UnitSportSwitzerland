using System;

namespace UnitSport.Styles;

/// <summary>
/// The arithmetic of zoom-aware detail (#1433), with nothing of the engine in it (tier-0 tested):
/// how much a field of view zooms over the walking view, the step the managers use, and the ring
/// a tile's detail is picked by. <see cref="LodScale"/> applies it to the live camera.
/// </summary>
public static class ZoomRule
{
    /// <summary>
    /// The walking view's vertical field of view (<c>FootPlayer.BaseFov</c>), the one every
    /// distance LOD was tuned on: zoom 1.
    /// </summary>
    public const float ReferenceFov = 68f;

    /// <summary>The step between <see cref="Step"/>s.</summary>
    public const float StepRatio = 1.25f;

    /// <summary>How far under a step the zoom must fall before the step drops: a zoom resting on a boundary does not flap.</summary>
    public const float Hysteresis = 1.05f;

    /// <summary>
    /// The most the detail follows a zoom: a ~0.07° view, the telescope at 20,000 mm (#1813). Past it
    /// only the sky is looked at (the telescope reaches a 10″ view for the planets), and a mountain
    /// at x10,000 would ask for every tile's finest detail to the horizon.
    /// </summary>
    public const float MaxZoom = 1100f;

    /// <summary>The zoom of a vertical field of view of <paramref name="fovDeg"/>: tan(34°) / tan(fov / 2), from 1 to <see cref="MaxZoom"/>.</summary>
    public static float Of(float fovDeg)
    {
        if (!(fovDeg > 0f) || fovDeg >= 179f) return 1f;
        return Math.Clamp(MathF.Tan(ReferenceFov * MathF.PI / 360f) / MathF.Tan(fovDeg * MathF.PI / 360f), 1f, MaxZoom);
    }

    /// <summary>The step index for a zoom of <paramref name="value"/>, coming from step <paramref name="current"/>.</summary>
    public static int StepFor(float value, int current)
    {
        int down = (int)MathF.Floor(MathF.Log(MathF.Max(1f, value)) / MathF.Log(StepRatio) + 1e-4f);
        if (current == down + 1 && value >= Step(current) / Hysteresis) return current;
        return down;
    }

    /// <summary>The zoom of step <paramref name="index"/>: <see cref="StepRatio"/> to that power.</summary>
    public static float Step(int index) => MathF.Pow(StepRatio, index);

    /// <summary>
    /// The ring a tile at ring <paramref name="dist"/> inside the view cone is detailed as at a zoom
    /// of <paramref name="step"/>: as big on screen as one at dist / step walking, rounded to the
    /// nearest ring, never under 1 (ring 0, the tile underfoot, keeps its own finest stride: a ring-1
    /// tile at ring 0's stride is four times the vertices for a difference no lens shows).
    /// </summary>
    public static int Ring(int dist, float step)
    {
        if (dist <= 0 || step <= 1f) return dist;
        return Math.Max(1, (int)MathF.Floor(dist / step + 0.5f));
    }
}
