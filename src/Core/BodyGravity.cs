namespace UnitSport.Core;

/// <summary>
/// The ground game's gravity where the anchor is (#1850, docs/plans/planets.md section 7): the Earth's tuned 9.81 m/s²
/// at home, the anchor body's <see cref="CelestialBody.GroundGravity"/> elsewhere (the Moon's 1.63: a jump six times as
/// high). The walker falls by it; the client's physics space is set to it with the body.
/// </summary>
public static class BodyGravity
{
    /// <summary>The ground's gravity on the anchor's body, m/s².</summary>
    public static float Ground { get; private set; } = (float)CelestialBody.Earth.GroundGravity;

    /// <summary>The anchor is on <paramref name="body"/> now.</summary>
    public static void On(BodyId body) => Ground = (float)CelestialBody.Of(body).GroundGravity;
}
