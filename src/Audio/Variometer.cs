using Godot;

namespace UnitSport.Audio;

/// <summary>
/// The variometer (#1225, <c>docs/plans/adventure-stamina.md</c> section 7): while gliding (paraglider,
/// parachute, wingsuit, pigeon) a beep whose pitch and rate rise with the climb, and a low tone in
/// strong sink. Fed the vertical speed once a frame by <see cref="Player.PlayerFeel"/>; owner only,
/// non-spatial. No allocation per frame: the sounds are <see cref="SfxSynth.VarioBank"/>'s baked variants.
/// </summary>
public sealed class Variometer
{
    /// <summary>Climb above this (m/s, smoothed) beeps.</summary>
    public const float ClimbBeepAbove = 0.3f;
    /// <summary>Sink below this (m/s, smoothed, negative) plays the low tone.</summary>
    public const float SinkToneBelow = -2f;
    /// <summary>Smoothing time of the vertical speed (s).</summary>
    public const float Smoothing = 0.5f;

    private float _vy, _wait;

    /// <summary>Which rides carry the instrument.</summary>
    public static bool Carries(Player.RideKind ride) =>
        ride is Player.RideKind.Paraglider or Player.RideKind.Parachute or Player.RideKind.Pigeon or Player.RideKind.Wingsuit;

    /// <summary>Beep interval (s) for a smoothed climb: 0.9 s at the threshold down to 0.18 s at +4 m/s.</summary>
    public static float Interval(float climb) => Mathf.Lerp(0.9f, 0.18f, Mathf.Clamp((climb - ClimbBeepAbove) / 3.7f, 0f, 1f));

    /// <summary>Beep variant (0..3, low to high) for a smoothed climb.</summary>
    public static int Variant(float climb) => Mathf.Clamp((int)((climb - ClimbBeepAbove) / 1.0f), 0, 3);

    /// <summary>Back to silence (left the glider, setting off).</summary>
    public void Reset() { _vy = 0f; _wait = 0f; }

    /// <summary>One frame. Returns the variant to play now (a rising beep 0..3, <see cref="SfxSynth.VarioSink"/>), or -1.</summary>
    public int Tick(float dt, float verticalSpeed)
    {
        _vy += (verticalSpeed - _vy) * (1f - Mathf.Exp(-dt / Smoothing));
        _wait -= dt;
        if (_wait > 0f) return -1;
        if (_vy > ClimbBeepAbove)
        {
            _wait = Interval(_vy);
            return Variant(_vy);
        }
        if (_vy < SinkToneBelow)
        {
            _wait = 0.55f;
            return SfxSynth.VarioSink;
        }
        _wait = 0f;
        return -1;
    }
}
