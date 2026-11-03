using Godot;
using UnitSport.Player;

namespace UnitSport.Adventure.Alcohol;

/// <summary>
/// What the local body does while drunk (#1271 part 3), set on <see cref="FootPlayer.Drunk"/> by the <see cref="AlcoholService"/>:
/// one filter on the intents for everyone (keyboard, pad, wheel and VR all end in a <see cref="RideInput"/> or a walk wish, so
/// they are lagged and nudged the same way), the camera's sway, the stumble and passing-out clocks. Plain objects, built once;
/// nothing here allocates per frame and no mesh is touched.
/// </summary>
public sealed class AlcoholBody
{
    private readonly DelayLine<RideInput> _ride = new();
    private readonly DelayLine<Vector3> _walk = new();
    private double _rideT, _walkT, _swayT;

    /// <summary>The effects now: set by the service at most four times a second.</summary>
    public DrunkEffects Fx { get; set; }
    /// <summary>The game's stumble and passing-out clocks.</summary>
    public DrunkState State { get; } = new();
    /// <summary>Comfort: the view sway and blur are off, the lag stays (<c>Settings > Gameplay > Alcohol view effects</c>).</summary>
    public bool ViewEffects { get; set; } = true;
    /// <summary>VR: no head sway, a vignette instead.</summary>
    public bool Vr { get; set; }

    /// <summary>Raised when free climbing is refused for the drink (the service throttles the toast).</summary>
    public Action? ClimbRefused { get; set; }

    /// <summary>The vehicle input, lagged and with a little steering noise. Passed out: no pedal, the brake on.</summary>
    public RideInput Ride(RideInput input, float dt)
    {
        _rideT += dt;
        if (State.Out) return new RideInput(0f, 1f, 0f, false);
        _ride.Push(_rideT, input);
        var fx = Fx;
        if (fx.Lag <= 0f && fx.SteerNoise <= 0f) return input;
        var lagged = _ride.At(_rideT, fx.Lag, input);
        if (fx.SteerNoise > 0f)
        {
            float noise = (Mathf.Sin((float)_rideT * 1.7f) + Mathf.Sin((float)_rideT * 4.3f + 1f)) * 0.5f * fx.SteerNoise;
            lagged = lagged with { Steer = Mathf.Clamp(lagged.Steer + noise, -1f, 1f) };
        }
        return lagged;
    }

    /// <summary>The walk wish (direction x amount), lagged and drifting slowly to one side and the other.</summary>
    public Vector3 Walk(Vector3 wish, float dt)
    {
        _walkT += dt;
        _walk.Push(_walkT, wish);
        var fx = Fx;
        if (fx.Lag <= 0f && fx.DriftDegrees <= 0f) return wish;
        var w = _walk.At(_walkT, fx.Lag, wish);
        if (fx.DriftDegrees > 0f && w.LengthSquared() > 1e-4f)
            w = w.Rotated(Vector3.Up, Mathf.DegToRad(fx.DriftDegrees) * Mathf.Sin((float)_walkT * Mathf.Tau / 9f));
        return w;
    }

    /// <summary>The camera sway offset (a frustum shift, the shake's own unit) for this frame: 0.2 Hz, none in VR or with the view effects off.</summary>
    public Vector2 Sway(float dt)
    {
        if (!ViewEffects || Vr || Fx.SwayDegrees <= 0f) return Vector2.Zero;
        _swayT += dt;
        float a = Fx.SwayDegrees / 0.3f * 0.012f;
        float ph = (float)_swayT * Mathf.Tau * 0.2f;
        return new Vector2(Mathf.Sin(ph) * a, Mathf.Sin(ph * 0.5f + 1f) * a * 0.6f);
    }

    /// <summary>The edge blur / vignette strength 0..1 to show now (0 with the view effects off on a flat screen; VR always keeps its vignette).</summary>
    public float Blur => ViewEffects || Vr ? Fx.Blur : 0f;
}
