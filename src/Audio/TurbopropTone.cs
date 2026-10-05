namespace UnitSport.Audio;

/// <summary>
/// The numbers behind a turboprop's sound (#420, the military freighter), pure for the unit tests:
/// a constant-speed propeller whose governor holds the rpm once the engine is above ground idle,
/// its blade-pass tone, the slow beating of several engines never quite in step, and how loud the
/// blades are for the thrust they make. <see cref="EngineSynth"/> plays them.
/// </summary>
public static class TurbopropTone
{
    /// <summary>Where the governor takes over, as the spool (0 idle .. 1 full): above it the prop turns at full rpm.</summary>
    public const float GovernedFrom = 0.55f;

    /// <summary>
    /// The propeller's rpm at <paramref name="spool"/>: from <paramref name="idleRpm"/> at ground idle
    /// up to <paramref name="maxRpm"/> at <see cref="GovernedFrom"/>, then held there (thrust comes
    /// from the blades' pitch, not the rpm). Spool below 0 or above 1 is clamped.
    /// </summary>
    public static float PropRpm(float spool, float idleRpm, float maxRpm)
    {
        float s = System.Math.Clamp(spool / GovernedFrom, 0f, 1f);
        return idleRpm + (maxRpm - idleRpm) * s;
    }

    /// <summary>Blade-pass frequency, Hz: each blade's pressure pulse, once per blade per turn.</summary>
    public static float BladePassHz(float propRpm, int blades) => propRpm / 60f * blades;

    /// <summary>
    /// Engine <paramref name="engine"/>'s rpm factor among <paramref name="engines"/>: spread over
    /// ±<paramref name="spread"/> so two props a few tenths of a percent apart beat a slow wow-wow
    /// (unsynchronised props), the sound that says "four turboprops" from the ground.
    /// </summary>
    public static float Detune(int engine, int engines, float spread = 0.006f) =>
        engines <= 1 ? 1f : 1f - spread + 2f * spread * engine / (engines - 1);

    /// <summary>The beat between engines 0 and the last at <paramref name="bladePassHz"/>, Hz.</summary>
    public static float BeatHz(float bladePassHz, int engines, float spread = 0.006f) =>
        bladePassHz * (Detune(engines - 1, engines, spread) - Detune(0, engines, spread));

    /// <summary>
    /// How loud the blades are, 0..1, from the blade loading <paramref name="load"/> (the thrust
    /// lever's share): a flat-pitch prop at idle hums, a loaded one at take-off rasps.
    /// </summary>
    public static float BladeLoudness(float load)
    {
        float l = System.Math.Clamp(load, 0f, 1f);
        return 0.3f + 0.7f * l * l;
    }
}
