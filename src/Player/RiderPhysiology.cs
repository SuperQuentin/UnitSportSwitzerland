using Godot;

namespace UnitSport.Player;

/// <summary>
/// The rider's body on the bike (#1124): critical power and W′, the reserve above it, and a heart
/// rate that follows the effort. Pure, so the unit tests can ride it without Godot.
///
/// <para>
/// CP/W′ is the model sports scientists use for efforts above threshold (Monod &amp; Scherrer 1965;
/// Skiba 2012/2015 for its balance over a ride): below <see cref="CriticalPower"/> a rider can go on
/// for an hour; above it every watt over comes out of a reserve of a fixed size, <see cref="WPrime"/>,
/// which refills below CP the faster the further below. Empty, the rider is held to CP. That is
/// why a sprint up a Swiss ramp costs the next minute: the reserve is the "matches" riders talk of.
/// </para>
/// </summary>
public sealed class RiderPhysiology
{
    /// <summary>Power the rider can hold for about an hour, W: their threshold (FTP ≈ 0.95·CP).</summary>
    public float CriticalPower { get; private set; }

    /// <summary>The reserve above CP, J: 15-25 kJ for trained amateurs.</summary>
    public float WPrime { get; private set; }

    /// <summary>What is left of the reserve, J.</summary>
    public float WBalance { get; private set; }

    /// <summary>The reserve left, 0..1.</summary>
    public float WFraction => WPrime > 0f ? WBalance / WPrime : 0f;

    public float RestingHeartRate { get; }
    public float MaxHeartRate { get; }

    /// <summary>Beats per minute now.</summary>
    public float HeartRate { get; private set; }

    /// <summary>Heart rate kinetics, s: it climbs to a new effort in about half a minute and falls back slower.</summary>
    public const float HeartRateRise = 25f, HeartRateFall = 45f;

    /// <summary>
    /// Under this share of W′ the rider can no longer get far above CP: a spent rider cannot sprint,
    /// they fade to it over the last few percent rather than hitting a wall at zero.
    /// </summary>
    private const float FadeShare = 0.08f;

    public RiderPhysiology(float criticalPower, float wPrime = 20000f, float restingHr = 60f, float maxHr = 185f)
    {
        RestingHeartRate = restingHr;
        MaxHeartRate = maxHr;
        Set(criticalPower, wPrime);
        HeartRate = restingHr + 0.15f * (maxHr - restingHr);
    }

    /// <summary>A new threshold or reserve (the settings changed): the reserve keeps its share.</summary>
    public void Set(float criticalPower, float wPrime)
    {
        float share = WPrime > 0f ? WFraction : 1f;
        CriticalPower = Mathf.Max(criticalPower, 50f);
        WPrime = Mathf.Max(wPrime, 1000f);
        WBalance = share * WPrime;
    }

    /// <summary>
    /// The most the rider can put out now, W, when they ask for <paramref name="wanted"/>: all of it
    /// while the reserve lasts, fading to CP as it runs out.
    /// </summary>
    public float Available(float wanted)
    {
        if (wanted <= CriticalPower) return wanted;
        float fade = Mathf.Clamp(WFraction / FadeShare, 0f, 1f);
        return CriticalPower + (wanted - CriticalPower) * fade;
    }

    /// <summary>
    /// One step at <paramref name="watts"/>: the reserve drains by the watts over CP, or refills as
    /// Skiba's differential model has it, <c>dW′bal/dt = (W′ − W′bal)·(CP − P)/W′</c>; the heart
    /// rate eases toward the effort's.
    /// </summary>
    /// <param name="measuredHeartRate">A chest strap's reading: taken as is.</param>
    public void Step(float watts, float dt, float? measuredHeartRate = null)
    {
        if (watts > CriticalPower) WBalance = Mathf.Max(0f, WBalance - (watts - CriticalPower) * dt);
        else WBalance = Mathf.Min(WPrime, WBalance + (WPrime - WBalance) * (CriticalPower - watts) / WPrime * dt);

        if (measuredHeartRate is { } hr)
        {
            HeartRate = hr;
            return;
        }
        float target = TargetHeartRate(watts);
        float tau = target > HeartRate ? HeartRateRise : HeartRateFall;
        HeartRate += (target - HeartRate) * (1f - Mathf.Exp(-dt / tau));
    }

    /// <summary>
    /// The heart rate an effort settles at: ~15% of the reserve between rest and max freewheeling,
    /// ~90% at CP, and the last of it as W′ runs out (the drift of a hard effort).
    /// </summary>
    public float TargetHeartRate(float watts)
    {
        float share = 0.15f + 0.75f * Mathf.Clamp(watts / CriticalPower, 0f, 1.2f) + 0.1f * (1f - WFraction);
        return RestingHeartRate + (MaxHeartRate - RestingHeartRate) * Mathf.Min(share, 1f);
    }
}
