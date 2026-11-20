using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// Moves the world's anchor onto a place on any body (#1967's <c>/goto</c>, #2039's travel between bodies): the anchor
/// relabelled to the body first (<see cref="WorldOrigin.Rebase"/>, nothing moves), then shifted to the place's own
/// east-up-south frame at the body's reference surface, so world (0, 0, 0) is the place and +Y its up. What comes next
/// (a camera over it, a body set down on its ground) is the caller's. Only under <c>--frame earth</c>, whose anchor has
/// axes; the flat frame has no other body.
/// </summary>
public static class BodyHandover
{
    /// <summary>A body's reference shape (the Earth's WGS84).</summary>
    public static Ellipsoid ShapeOf(BodyId body) => body == BodyId.Earth ? Ellipsoid.Wgs84 : CelestialBody.Of(body).Shape;

    /// <summary>A place on a body at its reference surface, ECEF in that body's fixed frame.</summary>
    public static EcefPos At(BodyId body, double latDeg, double lonDeg) => ShapeOf(body).ToEcef(new Geodetic(latDeg, lonDeg, 0));

    /// <summary>The anchor onto <paramref name="body"/> at <paramref name="at"/> (its ECEF). False with no anchor frame.</summary>
    public static bool To(WorldOrigin origin, OriginShifter shifter, BodyId body, EcefPos at)
    {
        if (origin.Frame.Axes == null) return false;
        if (origin.Body != body)
            Godot.GD.Print($"[handover] {origin.Body} -> {body}");
        origin.Rebase(body, SkyClock.Now);
        shifter.ShiftTo(0, 0, exact: true, axes: FrameAxes.Enu(at, body));
        return true;
    }
}
