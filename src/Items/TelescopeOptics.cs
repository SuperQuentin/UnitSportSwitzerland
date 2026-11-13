using System;

namespace UnitSport.Items;

// Plain C# (linked into the unit tests).

/// <summary>
/// The telescope's lens (#1440): a focal length in 35 mm-equivalent millimetres, as the camera's
/// (<c>items/camera-zoom</c>), from a wide finder view to the moon filling the view and past it.
/// The moon is 0.52° wide: it fills the view at ~2,300 mm and goes soft past ~20,000 mm (its map is
/// 2.7 km a texel). The longest end, 500,000 mm, is a 9.9″ view (#1813): Mars (3.5-25″) is half the
/// view or more, Jupiter (~45″) and Saturn's rings overflow it. The terrain's detail stops following
/// the zoom past ~20,000 mm (<c>Styles.ZoomRule.MaxZoom</c>): only the sky is looked at that close.
/// Each notch zooms by <see cref="Step"/>.
/// </summary>
public static class TelescopeOptics
{
    public const float FocalMin = 100f, FocalMax = 500_000f, FocalStart = 400f, Step = 1.25f;

    /// <summary>The vertical field of view, degrees, of a focal length: 2·atan(12 / f), the camera's rule.</summary>
    public static float Fov(float focalMm) => MathF.Atan(12f / focalMm) * 2f * 180f / MathF.PI;

    /// <summary>One notch in (<paramref name="notches"/> &gt; 0) or out, held inside the range.</summary>
    public static float Zoom(float focalMm, int notches) =>
        Math.Clamp(focalMm * MathF.Pow(Step, notches), FocalMin, FocalMax);

    /// <summary>
    /// How much the mouse and the stick turn the tube: the camera's fov / 76, with no floor, so one
    /// mouse count moves the view by the same share of it at any zoom.
    /// </summary>
    public static float LookScale(float focalMm) => Fov(focalMm) / 76f;

    /// <summary>"×N": how many times larger than the naked eye's 50 mm.</summary>
    public static float Magnification(float focalMm) => focalMm / 50f;
}
