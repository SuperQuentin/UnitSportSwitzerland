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

    /// <summary>
    /// How high the Earth stands above the straight line between two points <paramref name="totalM"/>
    /// apart (flat), <paramref name="alongM"/> from the first: s·(D − s) / 2R, highest halfway. A sight
    /// line clears the ground there when the ground plus this stays under it.
    /// </summary>
    public static double Bulge(double alongM, double totalM) => alongM * (totalM - alongM) * DropPerM2;
}
