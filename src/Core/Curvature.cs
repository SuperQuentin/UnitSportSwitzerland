using System;

namespace UnitSport.Core;

/// <summary>
/// The Earth's curvature, drawn round the camera (#187, <c>docs/notes/world/earth-curvature.md</c>).
///
/// <para>
/// The world stays flat LV95 everywhere the CPU looks: positions, physics, the wire, the tiles. Only
/// what is drawn curves: every world shader lowers its vertices by <see cref="Drop"/> of their flat
/// distance from the camera (<c>shaders/common/curvature.gdshaderinc</c>, the <c>world_curvature</c>
/// global), so the ground falls away as it does from a real viewpoint, 246 m at 60 km. The rules that
/// judge a sight line over the ground (<c>Net/Interest.Clear</c>, <c>PhotoJudge</c>) add the Earth's
/// <see cref="Bulge"/> between the two ends, so the server sees what the picture shows.
/// </para>
///
/// <para>
/// Apparent curvature: the radius is LV95's projection sphere's, enlarged for normal atmospheric
/// refraction (k = 0.13, the surveying standard), which is what a photo of a far range shows.
/// Pure maths, no Godot: unit-tested in <c>tests/</c>.
/// </para>
/// </summary>
public static class Curvature
{
    /// <summary>The radius of LV95's projection sphere (swisstopo), metres: the Earth's at Bern.</summary>
    public const double EarthRadiusM = 6_378_815.904;

    /// <summary>The coefficient of refraction: a sight line bends with the Earth by this share.</summary>
    public const double Refraction = 0.13;

    /// <summary>The radius that, with straight sight lines, shows what refraction makes us see.</summary>
    public const double ApparentRadiusM = EarthRadiusM / (1 - Refraction);

    /// <summary>The drop per square metre of flat distance, 1 / (2·R): the <c>world_curvature</c> global.</summary>
    public const double DropPerM2 = 1 / (2 * ApparentRadiusM);

    /// <summary>
    /// Whether the client draws the curvature: on unless <c>--curvature 0</c> or the debug menu
    /// turned it off. The picture only: the server's rules always count the Earth.
    /// </summary>
    public static bool Drawn { get; set; } = true;

    /// <summary>How far a point <paramref name="distanceM"/> away (flat) has dropped below the viewer's horizon, m.</summary>
    public static double Drop(double distanceM) => distanceM * distanceM * DropPerM2;

    /// <summary>The same from a squared flat distance (no square root on a hot path).</summary>
    public static double DropSquared(double distanceSquaredM2) => distanceSquaredM2 * DropPerM2;

    // ---- at altitude (#1430, phase H1 of docs/plans/ellipsoid-handover.md) -----------------------

    /// <summary>Refraction holds up to here, metres: the dense low air bends near-horizontal rays.</summary>
    public const double RefractionTopM = 5_000;

    /// <summary>And is gone from here: from the stratosphere the planet looks geometric.</summary>
    public const double GeometricFromM = 20_000;

    /// <summary>The drop gives way to the exact sphere between these two altitudes, metres.</summary>
    public const double SphereFromM = 20_000, SphereAtM = 50_000;

    /// <summary>
    /// The spectator's own ceiling, metres: far enough to see the whole Earth (#1430 H4: the globe, the
    /// air and the stars draw there; the flat tiles are gone above 250 km). A camera, no physics, so its
    /// float position (4 m steps out there) jitters nothing. The UFO has no ceiling: above 60 km it flies an orbit (#1480).
    /// </summary>
    public const double SpectatorCeilingM = 50_000_000;

    /// <summary>The Space regime (#1430 H3) starts above this camera altitude and ends below <see cref="SpaceExitM"/> (hysteresis), metres.</summary>
    public const double SpaceEnterM = 60_000, SpaceExitM = 50_000;

    /// <summary>The regime for a camera at <paramref name="altitude"/>, coming from <paramref name="inSpace"/>: <c>OriginShifter</c> follows it.</summary>
    public static bool SpaceRegime(bool inSpace, double altitude) => inSpace ? altitude >= SpaceExitM : altitude > SpaceEnterM;

    /// <summary>
    /// The Space frames turn above this camera altitude and stop below <see cref="TurnExitM"/> (#1480), metres:
    /// where every flat tile has dissolved (<c>World.Globe.FlatGoneM</c>, #1430 H4). Lower, the flat world is
    /// still drawn and the curvature shader wraps it round the camera as one plane, which a turned frame breaks.
    /// </summary>
    public const double TurnEnterM = 250_000, TurnExitM = 240_000;

    /// <summary>Whether the Space frame turns for a camera at <paramref name="altitude"/>, coming from <paramref name="turned"/>.</summary>
    public static bool TurnRegime(bool turned, double altitude) => turned ? altitude >= TurnExitM : altitude > TurnEnterM;

    /// <summary>A smooth 0 → 1 from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private static double SmoothStep(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>The refraction coefficient seen from a camera at <paramref name="altitudeM"/>: 0.13 low, 0 from 20 km.</summary>
    public static double RefractionAt(double altitudeM) => Refraction * (1 - SmoothStep(RefractionTopM, GeometricFromM, altitudeM));

    /// <summary>The drop per square metre for a camera at <paramref name="altitudeM"/>: the <c>world_curvature</c> global.</summary>
    public static double DropPerM2At(double altitudeM) => (1 - RefractionAt(altitudeM)) / (2 * EarthRadiusM);

    /// <summary>How far the drop has given way to the exact sphere, 0..1: the <c>world_curve_blend</c> global.</summary>
    public static double SphereBlendAt(double altitudeM) => SmoothStep(SphereFromM, SphereAtM, altitudeM);

    // ---- round geometry (#1512, G2 C3: the earth world) -------------------------------------------

    /// <summary>
    /// The world's geometry is round already (<c>--frame earth</c>: cube leaves in true Earth positions, in a frame
    /// that turns under the camera, #1512): it falls away by itself, so the shaders only lift it by what refraction
    /// bends a sight line back up, and the exact sphere's wrap is off. Set by the client world when it boots.
    /// </summary>
    public static bool RoundGeometry { get; set; }

    /// <summary>
    /// The <c>world_curvature</c> global for a camera at <paramref name="altitudeM"/>: the drop per m² on flat
    /// geometry (<see cref="DropPerM2At"/>); on round geometry −k/2R, a lift, k fading out with height as the drop's
    /// does (0.13 low: 1 cm at 1 km, 1 m at 10 km, 37 m at 60 km; 0 from 20 km).
    /// </summary>
    public static double CurvaturePerM2At(double altitudeM, bool round) =>
        round ? -RefractionAt(altitudeM) / (2 * EarthRadiusM) : DropPerM2At(altitudeM);

    /// <summary>The <c>world_curve_blend</c> global: the sphere's wrap on flat geometry, none on round geometry (it is the sphere).</summary>
    public static double CurveBlendAt(double altitudeM, bool round) => round ? 0 : SphereBlendAt(altitudeM);

    /// <summary>
    /// How far below its world position a point <paramref name="distanceM"/> from a camera near the ground is drawn:
    /// <see cref="Drop"/> on flat geometry, the refraction's lift (negative) on round geometry (<see cref="RoundGeometry"/>).
    /// What a screen projection of a far place (a marker) subtracts.
    /// </summary>
    public static double DrawnDrop(double distanceM) =>
        RoundGeometry ? -distanceM * distanceM * Refraction / (2 * EarthRadiusM) : Drop(distanceM);

    /// <summary>The geometric horizon's distance from <paramref name="altitudeM"/> up, metres: √(2Rh + h²).</summary>
    public static double HorizonM(double altitudeM)
    {
        double h = Math.Max(0, altitudeM);
        return Math.Sqrt(2 * EarthRadiusM * h + h * h);
    }

    /// <summary>
    /// Where a flat point is drawn, as an offset from it (<c>shaders/common/curvature.gdshaderinc</c>'s
    /// <c>curve_offset</c>, in doubles): <paramref name="flatM"/> from the camera on the ground plane,
    /// <paramref name="altitudeM"/> high. The drop lowers it by s² · <paramref name="dropPerM2"/>; the exact
    /// wrap reads the flat distance as arc length on the sphere through the camera's nadir: the point
    /// moves in by s − (R + y)·sin θ and down by (R + y)·(1 − cos θ). Blended by <paramref name="blend"/>.
    /// Returns the shift along the ground towards the point (negative: drawn nearer) and the height change.
    /// </summary>
    public static (double Along, double Up) Offset(double flatM, double altitudeM, double dropPerM2, double blend)
    {
        double drop = flatM * flatM * dropPerM2;
        double theta = flatM / EarthRadiusM;
        double along = (EarthRadiusM + altitudeM) * Math.Sin(theta) - flatM;
        double down = (EarthRadiusM + altitudeM) * (1 - Math.Cos(theta));
        return (blend * along, -((1 - blend) * drop + blend * down));
    }

    /// <summary>
    /// The same in single precision, the way the shader works it (camera-relative, no R + y, 1 − cos θ as
    /// 2·sin²(θ/2), small-angle series near zero): what the GPU draws, checked against <see cref="Offset"/>.
    /// </summary>
    public static (float Along, float Up) OffsetAsShader(float flatM, float altitudeM, float dropPerM2, float blend)
    {
        const float r = (float)EarthRadiusM;
        float s = flatM, s2 = s * s;
        float drop = s2 * dropPerM2;
        if (blend <= 0f || s < 1e-3f) return (0f, -drop);
        float th = s / r, half = th * 0.5f;
        float sincHalf = half < 1e-3f ? 1f - half * half / 6f : MathF.Sin(half) / half;
        float sincM1 = th < 1e-2f ? -th * th / 6f + th * th * th * th / 120f : MathF.Sin(th) / th - 1f;
        float along = s * sincM1 + altitudeM * MathF.Sin(th);
        float sinHalf = MathF.Sin(half);
        float down = s2 / (2f * r) * sincHalf * sincHalf + altitudeM * 2f * sinHalf * sinHalf;
        return (blend * along, -((1f - blend) * drop + blend * down));
    }

    /// <summary>
    /// How high the Earth stands above the straight line between two points <paramref name="totalM"/>
    /// apart (flat), <paramref name="alongM"/> from the first: s·(D − s) / 2R, highest halfway. A sight
    /// line clears the ground there when the ground plus this stays under it.
    /// </summary>
    public static double Bulge(double alongM, double totalM) => alongM * (totalM - alongM) * DropPerM2;
}
