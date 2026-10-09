namespace UnitSport.Player;

/// <summary>
/// A speed regulator for a car, a truck, a farm machine or a motorbike: set, it works the throttle
/// to hold a forward speed (a PI loop), and the brake, lightly, when the vehicle runs past it (down a
/// hill, or a stepless tractor that creeps on at idle). The loop's one output is throttle above zero
/// and brake below, so the integral settles on the set speed either way.
/// The driver's own throttle still wins when it asks for more; the brake or the handbrake switches it
/// off, as on a real one. Pure arithmetic, so the tests drive it without a vehicle.
/// </summary>
public sealed class CruiseControl
{
    /// <summary>The lowest speed it holds, m/s (3 km/h): a combine creeping into a field.</summary>
    public const float MinSpeed = 3f / 3.6f;

    /// <summary>Pressed again this far off the set speed, m/s (2 km/h), it sets the new speed instead of switching off.</summary>
    public const float ResetGap = 2f / 3.6f;

    /// <summary>Throttle per m/s short of the set speed, and the integral's rate.</summary>
    private const float Kp = 0.35f, Ki = 0.15f;

    /// <summary>The most brake it applies, and the least it bothers with.</summary>
    private const float BrakeMax = 0.5f, BrakeLeast = 0.02f;

    public bool On { get; private set; }

    /// <summary>The speed it holds, m/s, rounded to a whole km/h.</summary>
    public float SetSpeed { get; private set; }

    /// <summary>The integral: the throttle (above zero) or brake (below) that holds the speed on this road.</summary>
    private float _held;

    /// <summary>
    /// On at the forward speed <paramref name="u"/>. Switched on, the loop starts from no throttle, not
    /// the driver's: a driver pressing it while accelerating had far more on than the speed needs, and a
    /// stepless tractor's lag carried that well past it. Set again while on, it keeps what it had learnt.
    /// </summary>
    public void Engage(float u)
    {
        SetSpeed = MathF.Max(MathF.Round(u * 3.6f) / 3.6f, MinSpeed);
        if (!On) _held = 0f;
        On = true;
    }

    public void Off()
    {
        On = false;
        _held = 0f;
    }

    /// <summary>
    /// The switch pressed at forward speed <paramref name="u"/>: off switches it on there; on, near
    /// the set speed, switches it off, and well off it (the driver sped up or slowed down) sets the
    /// new speed. True when it is on afterwards.
    /// </summary>
    public bool Press(float u)
    {
        if (On && MathF.Abs(u - SetSpeed) < ResetGap) Off();
        else Engage(u);
        return On;
    }

    /// <summary>
    /// The pedals with the regulator's throttle and brake on top, at forward speed <paramref name="u"/>,
    /// m/s, from the driver's <paramref name="throttle"/> and <paramref name="brake"/>. The brake pedal,
    /// the handbrake or rolling backwards switches it off and hands the pedals back as they were.
    /// </summary>
    public (float Throttle, float Brake) Apply(float throttle, float brake, bool handbrake, float u, float dt)
    {
        if (!On) return (throttle, brake);
        if (brake > 0.1f || handbrake || u < -0.5f)
        {
            Off();
            return (throttle, brake);
        }
        float error = SetSpeed - u;
        float output = Math.Clamp(Kp * error + _held, -BrakeMax, 1f);
        // the driver's foot asking for more: the integral waits rather than winding up behind it
        if (throttle <= MathF.Max(output, 0f)) _held = Math.Clamp(_held + Ki * error * dt, -BrakeMax, 1f);
        if (throttle > 0.1f || output > -BrakeLeast) return (MathF.Max(throttle, MathF.Max(output, 0f)), 0f);
        return (throttle, -output);
    }
}
