using Godot;

namespace UnitSport.Player;

/// <summary>
/// The saucer's tractor beam (#861), the geometry and the pull: pure maths so tier 0 tests it
/// (tests/…/TractorBeamTests). The beam is a cone of light hanging straight down from the hatch
/// (world down, not the craft's: a wobbling saucer's beam would sweep the field); a thing inside it
/// is drawn up the axis toward the hatch and held a little under it, travelling with the craft.
/// Switched off (or out of the cone), what it held is let down at <see cref="LowerSpeed"/>.
/// </summary>
public static class TractorBeamMath
{
    /// <summary>How far down the beam reaches from the hatch, m.</summary>
    public const float Reach = 45f;
    /// <summary>The cone's radius at the hatch, m, and its half angle, rad (~20°: 17 m across at full reach).</summary>
    public const float ApexRadius = 1.4f, HalfAngle = 0.35f;
    /// <summary>Lift speed up the beam, m/s, and how fast a thing is drawn to the axis, 1/s (capped).</summary>
    public const float LiftSpeed = 3.2f, CentreRate = 1.2f, MaxCentring = 4f;
    /// <summary>A held thing stops this far under the hatch, m, and bobs about it.</summary>
    public const float HoldDepth = 2.2f;
    /// <summary>Let down when the beam lets go, m/s: gentle enough that nothing breaks.</summary>
    public const float LowerSpeed = 2.5f;
    /// <summary>The fastest correction toward the hold point, m/s, on top of the craft's own velocity.</summary>
    public const float MaxPull = 9f;

    /// <summary>The cone's radius <paramref name="depth"/> m under the hatch.</summary>
    public static float RadiusAt(float depth) => ApexRadius + Mathf.Tan(HalfAngle) * Mathf.Max(depth, 0f);

    /// <summary>Whether <paramref name="point"/> is inside the beam hanging from <paramref name="apex"/>.</summary>
    public static bool InCone(Vector3 apex, Vector3 point)
    {
        float depth = apex.Y - point.Y;
        if (depth < 0f || depth > Reach) return false;
        float dx = point.X - apex.X, dz = point.Z - apex.Z;
        float r = RadiusAt(depth);
        return dx * dx + dz * dz <= r * r;
    }

    /// <summary>
    /// The velocity a held thing at <paramref name="point"/> should have this step: the craft's own
    /// velocity (so it travels with it), plus a pull up the beam and in to its axis, slowing to a stop
    /// at <see cref="HoldDepth"/> under the hatch. <paramref name="time"/> makes it bob there.
    /// </summary>
    public static Vector3 Pull(Vector3 apex, Vector3 point, Vector3 craftVelocity, float time = 0f)
    {
        var hold = apex + Vector3.Down * (HoldDepth + 0.25f * Mathf.Sin(time * 1.7f));
        var to = hold - point;
        // up the beam at the lift speed, easing in over the last two metres
        float up = Mathf.Clamp(to.Y, -2f, 2f) / 2f * LiftSpeed;
        var across = new Vector3(to.X, 0f, to.Z) * CentreRate;
        if (across.Length() > MaxCentring) across = across.Normalized() * MaxCentring;
        var correction = across + Vector3.Up * up;
        if (correction.Length() > MaxPull) correction = correction.Normalized() * MaxPull;
        return craftVelocity + correction;
    }

    /// <summary>The velocity of a thing the beam is letting down: straight down, gently, no sideways drift.</summary>
    public static Vector3 Lower => Vector3.Down * LowerSpeed;
}
