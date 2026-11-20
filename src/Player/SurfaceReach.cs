using Godot;

namespace UnitSport.Player;

/// <summary>
/// How far the nearest surface of any body is from a world point (#1851, docs/plans/planets.md section 8): what the
/// spectator's speed (<see cref="SpectatorSpeed.SurfaceCap"/>) and the shuttle's cruise drive (v = lever · d / τ) measure.
/// Near the anchor's body it is the altitude the caller already has; past <see cref="NearSurfaceM"/> the frame tree's
/// (<see cref="Core.Frames.NearestSurface"/>: the body, its moons, its parent and its siblings), so the drive slows for the
/// Moon as it does for the Earth.
/// </summary>
public static class SurfaceReach
{
    /// <summary>Under this altitude the anchor's own body is the nearest surface for certain (the Moon is 356,000 km away at the least).</summary>
    public const double NearSurfaceM = 10_000_000;

    /// <summary>The distance to the nearest surface from <paramref name="world"/>, metres, given its <paramref name="altitude"/> over the anchor's body.</summary>
    public static double Of(Core.OriginFrame frame, Vector3 world, double altitude) => Of(frame, world, altitude, out _);

    /// <summary>
    /// <see cref="Of(Core.OriginFrame, Vector3, double)"/>, and whether that surface is a ring (#2030: Saturn's slab counts,
    /// even near Saturn, whose rings reach to 14,000 km over its cloud tops).
    /// </summary>
    public static double Of(Core.OriginFrame frame, Vector3 world, double altitude, out bool ring)
    {
        ring = false;
        if (frame.Axes is not { } axes) return altitude;
        if (altitude < NearSurfaceM && Core.CelestialBody.Of(axes.Body).Rings is null) return altitude;
        var p = axes.ToEcef(world.X, world.Y, world.Z);
        var (_, d, isRing) = Core.Frames.NearestReach(axes.Body, new Core.DVec3(p.X, p.Y, p.Z), Core.SkyClock.Now);
        ring = isRing;
        return d;
    }
}
