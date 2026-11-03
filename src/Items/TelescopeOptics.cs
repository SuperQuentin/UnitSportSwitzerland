using System;

namespace UnitSport.Items;

// Plain C# (linked into the unit tests).

/// <summary>
/// The telescope's lens (#1440): a focal length in 35 mm-equivalent millimetres, as the camera's
/// (<c>items/camera-zoom</c>), from a wide finder view to the moon filling the view and past it.
/// The moon is 0.52° wide: it fills the view at ~2,300 mm; Jupiter (~45″) is a fifth of the view at
/// the longest end, 20,000 mm, where the moon goes soft (its map is 2.7 km a texel, a pixel 0.45 km).
/// Each notch zooms by <see cref="Step"/>.
/// </summary>
public static class TelescopeOptics
{
    public const float FocalMin = 100f, FocalMax = 20000f, FocalStart = 400f, Step = 1.25f;

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

    /// <summary>
    /// Whether a body <paramref name="offDeg"/> degrees off the view's centre is close enough to lock on:
    /// inside the view, or within a degree of it at the longest focal lengths.
    /// </summary>
    public static bool CanLock(float offDeg, float focalMm) => offDeg <= MathF.Max(Fov(focalMm) * 0.6f, 1f);
}
