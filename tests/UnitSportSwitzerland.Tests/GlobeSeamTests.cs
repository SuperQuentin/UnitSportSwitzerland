using UnitSport.Core;
using UnitSport.Terrain.Format;
using Xunit;
using Xunit.Abstractions;

namespace UnitSport.Tests;

/// <summary>
/// The seam between the flat world and the globe (#1430 H4, docs/plans/ellipsoid-handover.md §6a):
/// above 20 km H1 bends the flat LV95 tiles onto a sphere through the camera's nadir
/// (<see cref="Curvature.Offset"/>, the shader's <c>curve_offset</c>), while the globe draws the same
/// ground on the real ellipsoid, placed through the frame's Earth axes. Where they meet the two must
/// agree to well under a pixel, or the flat tiles' edge shows against the globe.
/// </summary>
public class GlobeSeamTests(ITestOutputHelper output)
{
    // the game's default lens, 75° vertical, on a 1080-line screen
    private static readonly double PixelsPerRad = 540 / Math.Tan(75.0 / 2 * Math.PI / 180);

    /// <summary>The angle, in pixels, between where H1 draws a flat ground point and where the globe has it.</summary>
    private static double GapPixels(OriginFrame frame, double eyeX, double altitude, double dx, double dz, double h)
    {
        // placed as the globe is: through the flat world's embedding under the camera
        var axes = frame.EcefAxesAt(frame.E + eyeX, frame.N);
        // H1: the flat point, moved by the curvature as the shader moves it
        double s = Math.Sqrt(dx * dx + dz * dz);
        var (along, up) = Curvature.Offset(s, h, Curvature.DropPerM2At(altitude), Curvature.SphereBlendAt(altitude));
        double fx = dx + dx / s * along, fy = h + up, fz = dz + dz / s * along;
        // the globe: the same Earth point, through the frame's Earth axes
        var (tx, ty, tz) = axes.FromEcef(SwissGeodesy.ToEcef(frame.E + eyeX + dx, frame.N - dz, h));
        // both seen from the camera (eyeX, altitude, 0), the point dx, dz from it
        double ax = fx, ay = fy - altitude, az = fz, bx = tx - eyeX, by = ty - altitude, bz = tz;
        double cos = (ax * bx + ay * by + az * bz) / Math.Sqrt((ax * ax + ay * ay + az * az) * (bx * bx + by * by + bz * bz));
        return Math.Acos(Math.Min(1, cos)) * PixelsPerRad;
    }

    [Theory]
    [InlineData(20_000, 0)]
    [InlineData(60_000, 0)]
    [InlineData(120_000, 0)]
    // the Space regime re-anchors past 20 km: the camera that far from the frame's origin
    [InlineData(60_000, 20_000)]
    [InlineData(120_000, 20_000)]
    public void Where_the_flat_tiles_meet_the_globe_they_agree_to_a_pixel(double altitude, double eyeX)
    {
        // Lausanne, the shots' place: the data reaches ~150 km round (the horizon blocks)
        var frame = new OriginFrame(2538300, 1151200, 0);
        double worst = 0;
        foreach (double distance in new[] { 10_000.0, 50_000, 100_000, 150_000 })
        {
            double worstHere = 0;
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4;
                worstHere = Math.Max(worstHere, GapPixels(frame, eyeX, altitude, distance * Math.Cos(a), distance * Math.Sin(a), 500));
            }
            output.WriteLine($"{altitude / 1000:F0} km up, {eyeX / 1000:F0} km from the origin, {distance / 1000:F0} km out: {worstHere:F3} px");
            worst = Math.Max(worst, worstHere);
        }
        Assert.InRange(worst, 0, 1.0);
    }
}
