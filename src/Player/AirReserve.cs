namespace UnitSport.Player;

/// <summary>
/// The breath a swimmer holds (#301). Plain C# with no Godot in it, so the unit tests link it
/// (<c>tests/UnitSportSwitzerland.Tests/SwimTests.cs</c>). It drains while the head is under water
/// (faster on a sprint stroke), refills with the head out, and once it is empty it asks for
/// drowning damage once a second, starting at once.
/// </summary>
public struct AirReserve
{
    /// <summary>Seconds of air in a full breath.</summary>
    public const float Max = 45f;
    /// <summary>A sprint stroke under water uses air this much faster.</summary>
    public const float SprintDrain = 1.7f;
    /// <summary>Seconds of air won back per second with the head out.</summary>
    public const float Refill = 9f;
    /// <summary>Health lost per second with no air left, in one bite a second.</summary>
    public const float DrownDamage = 15f;
    /// <summary>Back up with less than this share of the reserve left: a gasp.</summary>
    public const float GaspBelow = 0.5f;

    /// <summary>Seconds of air left, 0..<see cref="Max"/>.</summary>
    public float Seconds;

    private float _drownTick;
    private bool _wasUnder;

    public static AirReserve Full => new() { Seconds = Max };

    /// <summary>
    /// One step: <paramref name="under"/> is the head under the surface. Returns the health to take
    /// now (0 on most steps), and whether this is the breath taken coming up after a long time under.
    /// </summary>
    public (float Damage, bool Gasp) Step(float dt, bool under, bool sprinting)
    {
        bool gasp = false;
        if (under) Seconds = Math.Max(0f, Seconds - dt * (sprinting ? SprintDrain : 1f));
        else
        {
            gasp = _wasUnder && Seconds < Max * GaspBelow;
            Seconds = Math.Min(Max, Seconds + Refill * dt);
        }
        _wasUnder = under;
        float damage = 0f;
        if (under && Seconds <= 0f)
        {
            _drownTick -= dt;
            if (_drownTick <= 0f)
            {
                _drownTick += 1f;
                if (_drownTick <= 0f) _drownTick = 1f;   // a long step bites once, not in a burst
                damage = DrownDamage;
            }
        }
        else _drownTick = 0f;
        return (damage, gasp);
    }
}
