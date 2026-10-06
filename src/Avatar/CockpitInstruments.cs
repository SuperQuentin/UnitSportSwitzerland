using System;

namespace UnitSport.Avatar;

/// <summary>Cockpit warnings (#421): what the master lights, the gear lights and the aural alerts show.</summary>
[Flags]
public enum CockpitWarning : byte
{
    None = 0,
    Stall = 1,
    Overspeed = 2,
    /// <summary>Low, slow and descending with the gear not down and locked.</summary>
    Gear = 4,
    /// <summary>Less than a tenth of the fuel left.</summary>
    FuelLow = 8,
    /// <summary>The parking brake set with the levers forward.</summary>
    ParkBrake = 16,
    /// <summary>An engine not running in the air.</summary>
    EngineOut = 32,
}

/// <summary>
/// What an airliner's instruments show (#421), rounded to what a PS1-resolution screen or needle can
/// tell apart: two readouts that compare equal draw the same picture, so a screen is redrawn only when
/// this changes. Built the same way on every peer from the drawn aircraft and its replicated levers.
/// </summary>
public readonly record struct Readout(
    bool Power, int Ias, int Alt, int Vs, int Hdg, int Track, int Pitch, int Roll, int Gs,
    int N1a, int N1b, int N1c, int N1d, int Egt, int Ff, int Fuel,
    int FlapLever, int Flaps, int Gear, int Speedbrake, bool Park, bool Autopilot, CockpitWarning Warn, bool Flash)
{
    public int N1(int i) => i switch { 0 => N1a, 1 => N1b, 2 => N1c, _ => N1d };
}

/// <summary>
/// The arithmetic of the cockpit's instruments (#421), plain C# with no Godot so it is unit tested:
/// units, density, tape positions, needle angles and the warnings' rules. Angles are radians clockwise
/// from 12 o'clock, as a dial is read.
/// </summary>
public static class CockpitInstruments
{
    public const float Knot = 0.514444f, Foot = 0.3048f;

    /// <summary>ISA density over sea level's at <paramref name="altitude"/> metres (troposphere).</summary>
    public static float Sigma(float altitude) =>
        MathF.Pow(Math.Clamp(1f - 2.25577e-5f * altitude, 0.2f, 1.2f), 4.2559f);

    /// <summary>Indicated airspeed from the true one at an altitude, no wind, m/s.</summary>
    public static float Ias(float tas, float altitude) => tas * MathF.Sqrt(Sigma(altitude));

    /// <summary>
    /// A tape's marks: the first one at or below the bottom of the window and how many fit.
    /// <paramref name="value"/> is at the window's middle, <paramref name="half"/> units each way.
    /// </summary>
    public static (int First, int Count) TapeMarks(float value, float half, int step)
    {
        int first = (int)MathF.Floor((value - half) / step) * step;
        int last = (int)MathF.Ceiling((value + half) / step) * step;
        return (first, (last - first) / step + 1);
    }

    /// <summary>Pixels a tape mark sits above the window's middle (up = more).</summary>
    public static float TapeOffset(float mark, float value, float pxPerUnit) => (mark - value) * pxPerUnit;

    /// <summary>The steam airspeed indicator: 40 kt at 12 o'clock, 400 kt after 330° (all four types read in knots).</summary>
    public static float AsiAngle(float kt) => Math.Clamp((kt - 40f) / 360f, 0f, 1f) * 330f * Deg;

    /// <summary>The altimeter's long hand (a turn per 1000 ft) and its short hand (a turn per 10 000 ft).</summary>
    public static (float Hundreds, float Thousands) AltimeterAngles(float ft)
    {
        float f = MathF.Max(ft, 0f);
        return (f % 1000f / 1000f * Tau, f % 10000f / 10000f * Tau);
    }

    /// <summary>
    /// The vertical speed indicator: 9 o'clock is level, climbs clockwise; 0-1000 fpm takes 60°, 1000-6000
    /// another 105° (the scale is squeezed, as on the real dial), past 6000 it pins.
    /// </summary>
    public static float VsiAngle(float fpm)
    {
        float a = MathF.Abs(fpm);
        float deg = a <= 1000f ? a * 0.06f : 60f + MathF.Min(a - 1000f, 5000f) * 0.021f;
        return -MathF.PI / 2f + MathF.Sign(fpm) * deg * Deg;
    }

    /// <summary>An engine dial (N1 %, EGT): 0 at 7 o'clock, full scale after 270°.</summary>
    public static float DialAngle(float value, float full) => (-135f + Math.Clamp(value / full, 0f, 1.1f) * 270f) * Deg;

    /// <summary>A heading, degrees, wrapped to 0..359 (360 shows as 360 on a compass, kept 0 here).</summary>
    public static int Wrap360(float deg) => ((int)MathF.Round(deg) % 360 + 360) % 360;

    /// <summary>
    /// The warnings now. <paramref name="stall"/> and <paramref name="overspeed"/> come from the owner's
    /// flight model (alpha and the limit speeds); the rest is what any peer sees: the gear warning low
    /// (under 230 m), slow (180 kt) and sinking with the gear not down; fuel under a tenth; the parking
    /// brake with the levers past half; an engine out (spool under 10 %) in the air with the power on.
    /// </summary>
    public static CockpitWarning Warnings(bool stall, bool overspeed, bool airborne, float clearance, float kt, float vsFpm,
        float gear, float fuel, bool park, float lever, bool power, float minSpool)
    {
        var w = CockpitWarning.None;
        if (stall && airborne) w |= CockpitWarning.Stall;
        if (overspeed) w |= CockpitWarning.Overspeed;
        if (airborne && gear < 1f && clearance < 230f && kt < 180f && vsFpm < -100f) w |= CockpitWarning.Gear;
        if (power && fuel < 0.1f) w |= CockpitWarning.FuelLow;
        if (park && lever > 0.5f) w |= CockpitWarning.ParkBrake;
        if (power && airborne && minSpool < 0.1f) w |= CockpitWarning.EngineOut;
        return w;
    }

    /// <summary>Red master warning: what kills (stall, overspeed, gear); the rest is the amber master caution.</summary>
    public static bool MasterWarning(CockpitWarning w) => (w & (CockpitWarning.Stall | CockpitWarning.Overspeed | CockpitWarning.Gear)) != 0;
    public static bool MasterCaution(CockpitWarning w) => (w & (CockpitWarning.FuelLow | CockpitWarning.ParkBrake | CockpitWarning.EngineOut)) != 0;

    /// <summary>
    /// The thrust levers' travel, radians off upright (forward +): idle, the climb detent, flex/MCT and
    /// TOGA on the A320's quadrant (lever 0, ~0.8, ~0.9, 1), reverse pulled behind idle.
    /// </summary>
    public static float LeverAngle(float lever, bool reverse) => reverse ? -18f * Deg : (-8f + Math.Clamp(lever, 0f, 1f) * 40f) * Deg;

    /// <summary>The detent a lever is in, for the A320 E/WD's thrust mode: IDLE, CL, FLX, TOGA, REV.</summary>
    public static string LeverDetent(float lever, bool reverse) =>
        reverse ? "REV" : lever < 0.05f ? "IDLE" : lever < 0.85f ? "CL" : lever < 0.97f ? "FLX" : "TOGA";

    /// <summary>The rounded fuel flow per engine, kg/h, from its N1 share (idle flow at idle, <paramref name="toga"/> at full).</summary>
    public static float FuelFlow(float spool, float idleSpool, float toga) =>
        spool < 0.05f ? 0f : toga * (0.12f + 0.88f * MathF.Pow(Math.Clamp((spool - idleSpool) / (1f - idleSpool), 0f, 1f), 1.6f));

    /// <summary>EGT, °C: ambient when stopped, ~420 at idle, ~900 at take-off thrust.</summary>
    public static float Egt(float spool) => spool < 0.05f ? 15f + spool * 2000f : 420f + 480f * MathF.Pow(Math.Clamp((spool - 0.2f) / 0.8f, 0f, 1f), 1.4f);

    private const float Deg = MathF.PI / 180f, Tau = MathF.PI * 2f;
}
