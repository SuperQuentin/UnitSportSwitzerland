namespace UnitSport.Core;

/// <summary>
/// The ground game's gravity where the anchor is (#1850, docs/plans/planets.md section 7): the Earth's tuned 9.81 m/s²
/// at home, the anchor body's <see cref="CelestialBody.GroundGravity"/> elsewhere (the Moon's 1.63: a jump six times as
/// high). The walker falls by it; the client's physics space is set to it with the body. On a server each body's step
/// runs under its own body's (<see cref="For"/>).
/// </summary>
public static class BodyGravity
{
    /// <summary>The ground's gravity on the anchor's body, m/s².</summary>
    public static float Ground { get; private set; } = (float)CelestialBody.Earth.GroundGravity;

    /// <summary>The anchor is on <paramref name="body"/> now.</summary>
    public static void On(BodyId body) => Ground = (float)CelestialBody.Of(body).GroundGravity;

    /// <summary>
    /// For one body's own step (#2000), the ground's gravity of the body it stands on, put back after: a server runs bodies
    /// on several bodies at once (its Moon region's bots by its Earth's), one at a time on the main thread. A Moon colonist's
    /// rover at the Earth's 9.81 stalled on the plateau. Nothing changes where the body is on the anchor's own body.
    /// </summary>
    public static Scope For(BodyId? body) => new(body is { } b ? (float)CelestialBody.Of(b).GroundGravity : Ground);

    /// <summary>The gravity of one step; the one before comes back with <see cref="Dispose"/>.</summary>
    public readonly struct Scope : System.IDisposable
    {
        private readonly float _before;

        public Scope(float g)
        {
            _before = Ground;
            Ground = g;
        }

        public void Dispose() => Ground = _before;
    }
}
