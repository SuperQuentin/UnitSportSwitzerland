using System.Collections.Generic;
using Godot;
using UnitSport.Core;

namespace UnitSport.World.Bodies;

/// <summary>
/// How a body looks from space, one way whatever draws it (#1998, H7b's single body view, docs/plans/planets.md section 6):
/// its globe (<see cref="BodyGlobe"/>) and its sphere in the sky (<see cref="MoonDisc"/>, <see cref="SkyBodies"/>) are lit
/// with the same formula (the photo lifted for Cartoon, plain Lambert, the night floor, a giant's soft terminator) at the
/// globe's own exposure, so a body never changes its look when one hands it to the other.
///
/// <para>
/// <b>Which one draws it</b> (<see cref="WantsGlobe"/>): the globe while the camera is on the body's ground game (the
/// anchor on it) or, in space, while the body is at least <see cref="GlobeShare"/> of the view's height across its radius
/// (kept down to 0.8 of that); the sky's sphere (or point) below that, and always from inside the air, whose sky the sky's
/// sphere is drawn over (the telescope by day). Before, the globe drew when the anchor was on the body or within ten of its
/// radii: at the handover between the Moon and the Earth the crisp globe became the sky disc in one frame, which in space
/// read as a glow.
/// </para>
/// </summary>
public static class BodyLook
{
    /// <summary>A body's radius past this share of the view's height (in space) is drawn by its globe.</summary>
    public const float GlobeShare = 0.01f;

    /// <summary>
    /// The draw order of what stands in for a far body in the sky (#2051): above the stars' (0), so it covers them, under
    /// the moon's halo (<see cref="HaloPriority"/>) and the air (<see cref="AirPriority"/>), which are in front of it.
    /// </summary>
    public const int StandinPriority = 1, HaloPriority = 2, AirPriority = 3;

    /// <summary>
    /// Where a stand-in for a body <paramref name="distance"/> metres off (radius <paramref name="radius"/>) is put, as a
    /// share of the far plane (#2051): farther bodies farther, so the stand-ins, drawn in order and not by depth, sort
    /// as the bodies do (Io in front of Jupiter, the Moon across the Earth); 0.45 at 1,000 km to 0.85 at 10 billion km
    /// and past, never so far that the body's near side passes 95 % of the far plane.
    /// </summary>
    public static double StandinShare(double distance, double radius)
    {
        double t = Math.Clamp((Math.Log10(Math.Max(distance, 1)) - 6) / 7, 0, 1);
        double share = 0.45 + 0.4 * t;
        return Math.Min(share, 0.95 / (1 + radius / Math.Max(distance, 1)));
    }

    /// <summary>The globe keeps it down to this share of <see cref="GlobeShare"/> (no flicker at the edge).</summary>
    public const float KeepFactor = 0.8f;

    /// <summary>The exposure before a body's globe has worked its own out (<see cref="BodyGlobe.Exposure"/>'s start).</summary>
    public const float DefaultExposure = 1.6f;

    private static readonly Dictionary<BodyId, float> Exposures = new();

    /// <summary>A body's globe worked its exposure out (its photo's mean): its sky sphere takes it too.</summary>
    public static void SetExposure(BodyId body, float exposure) => Exposures[body] = exposure;

    /// <summary>How far the camera is in space for this purpose, 0..1: above the Earth's air, or on a body without air.</summary>
    public static float InSpace => Mathf.Max(Atmosphere.Space, Atmosphere.Airless ? 1f : 0f);

    /// <summary>The look's parameters for <paramref name="body"/>: the globe's exposure, its night floor, its terminator's wrap.</summary>
    public static (float Exposure, float NightFloor, float Wrap) Of(CelestialBody body)
    {
        bool giant = !body.Landable && body.Air != null;
        return (Exposures.TryGetValue(body.Id, out float e) ? e : DefaultExposure, giant ? 0.015f : 0.06f, giant ? 0.3f : 0f);
    }

    /// <summary>
    /// Whether <paramref name="body"/>'s globe draws it now: its radius <paramref name="share"/> of the view's height (its
    /// angular radius over the vertical field), the anchor on it, whether it drew last frame.
    /// </summary>
    public static bool WantsGlobe(bool anchorOn, float share, bool drawn) =>
        ForceGlobe is { } forced ? forced || anchorOn : anchorOn || InSpace > 0.99f && share >= GlobeShare * (drawn ? KeepFactor : 1f);

    /// <summary>A check's: the globes drawn (true) or not (false) whatever their size (<c>--bodyviewcheck</c>); null: by size.</summary>
    public static bool? ForceGlobe { get; set; }

    /// <summary>
    /// Writes the look into a sky sphere's material (moon.gdshader, planet.gdshader): its weight, then the parameters; only
    /// when they changed from <paramref name="shown"/> (shader parameters are never set per frame for nothing).
    /// </summary>
    public static void Write(ShaderMaterial material, CelestialBody body, float weight, ref (float, float, float, float) shown)
    {
        var (exposure, floor, wrap) = Of(body);
        if (shown == (weight, exposure, floor, wrap)) return;
        shown = (weight, exposure, floor, wrap);
        material.SetShaderParameter(GlobeLookName, weight);
        material.SetShaderParameter(SunStrengthName, exposure);
        material.SetShaderParameter(NightFloorName, floor);
        material.SetShaderParameter(WrapName, wrap);
    }

    /// <summary>
    /// Where a body is seen from the camera in space, through the frame tree as its globe places it (#1998): the sky's own
    /// reading (<c>SunPath</c>) counts its observer from the Earth, right on and near the Earth but ~90° off for the Moon
    /// from 140,000 km out sunward, so the sky's sphere drew the Moon off-screen while its globe stood in the middle.
    /// In space (<see cref="InSpace"/> over a half) with an anchor frame; false otherwise (the sky's reading then).
    /// </summary>
    /// <param name="camera">The camera's world position.</param>
    /// <param name="direction">World axes, unit, toward the body's centre.</param>
    /// <param name="distance">To its centre, metres.</param>
    /// <param name="face">Its own axes in world axes: columns 0° N 0° E, 90° E, its north pole.</param>
    /// <param name="sun">World axes, unit, from the body's centre toward the Sun (what lights it).</param>
    public static bool Seen(BodyId body, Vector3 camera, out Vector3 direction, out double distance, out Basis face, out Vector3 sun)
    {
        direction = sun = Vector3.Zero;
        distance = 0;
        face = Basis.Identity;
        if (InSpace <= 0.5f || OriginShifter.Instance?.Origin.Frame.Axes is not { } axes) return false;
        var m = SkyClock.Now;
        var e = axes.ToEcef(camera.X, camera.Y, camera.Z);
        var eye = new DVec3(e.X, e.Y, e.Z);
        var centre = Frames.Convert(body, new DVec3(0, 0, 0), axes.Body, m);
        var to = centre - eye;
        distance = to.Length;
        if (!(distance > 0)) return false;
        Vector3 World(DVec3 v)
        {
            var p = new Terrain.Format.EcefPos(v.X, v.Y, v.Z);
            return new Vector3((float)FrameAxes.Dot(p, axes.X), (float)FrameAxes.Dot(p, axes.Y), (float)FrameAxes.Dot(p, axes.Z));
        }
        direction = World(to.Normalized()).Normalized();
        sun = body == BodyId.Sun ? -direction : World((Frames.Convert(BodyId.Sun, new DVec3(0, 0, 0), axes.Body, m) - centre).Normalized()).Normalized();
        face = new Basis(
            World(Frames.ConvertDirection(body, new DVec3(1, 0, 0), axes.Body, m)),
            World(Frames.ConvertDirection(body, new DVec3(0, 1, 0), axes.Body, m)),
            World(Frames.ConvertDirection(body, new DVec3(0, 0, 1), axes.Body, m)));
        return true;
    }

    private static readonly StringName GlobeLookName = "globe_look", SunStrengthName = "sun_strength", NightFloorName = "night_floor",
        WrapName = "terminator_wrap";
}
