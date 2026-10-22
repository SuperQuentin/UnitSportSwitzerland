using Godot;

namespace UnitSport.Player;

/// <summary>Where the rider's hands are, and so how much air they push (#1124).</summary>
public enum RidingPosition { Hoods, Drops, Tuck }

/// <summary>
/// The road bike's longitudinal physics, pure (#1124): the power equation's every term as a cycling
/// scientist would write it, so the unit tests can hold it to published numbers without Godot.
///
/// <para>
/// <c>(m + I/r²)·a = η·P/v − ½ρ·CdA·(1 − draft)·v² − Crr·m·g·cosθ − m·g·sinθ</c>. Over the old model:
/// the wheels' spin is mass to accelerate, the chain loses 2.5%, the air thins with altitude, the
/// tyre rolls harder on gravel than on tarmac, the drag area is the rider's own size and position,
/// and a wheel ahead shelters you.
/// </para>
/// </summary>
public static class BikePhysics
{
    public const float Gravity = 9.81f;

    /// <summary>
    /// Crank to wheel through a clean 11-speed chain: 97-98% measured (Spicer 2001, Friction Facts).
    /// A trainer that measures at the hub already counts it; a crank power meter and the keys do not.
    /// </summary>
    public const float DrivetrainEfficiency = 0.975f;

    /// <summary>
    /// Drag area on the hoods of the reference rider (72.5 kg, 1.78 m): a fit amateur, not a pro.
    /// Calibrated against the owner's own ride when there is one (plan, Validation).
    /// </summary>
    public const float ReferenceDragArea = 0.32f;
    public const float ReferenceRiderMass = 72.5f;

    /// <summary>
    /// A body's frontal area grows as mass^0.425 at a given height: the Du Bois body surface area's
    /// mass exponent, the one the cycling literature uses to scale CdA between riders (Heil 2001).
    /// </summary>
    public const float FrontalAreaMassExponent = 0.425f;

    /// <summary>Hoods 1, drops 0.88, a descender's tuck 0.75 of the hoods' drag area (wind-tunnel ratios, Blocken 2018).</summary>
    public static float PositionFactor(RidingPosition position) => position switch
    {
        RidingPosition.Drops => 0.88f,
        RidingPosition.Tuck => 0.75f,
        _ => 1f,
    };

    /// <summary>The drag area of a rider of <paramref name="riderMass"/> kg in <paramref name="position"/>, m².</summary>
    public static float DragArea(float riderMass, RidingPosition position, float referenceDragArea = ReferenceDragArea) =>
        referenceDragArea * Mathf.Pow(Mathf.Max(riderMass, 30f) / ReferenceRiderMass, FrontalAreaMassExponent) * PositionFactor(position);

    /// <summary>
    /// Air density at <paramref name="altitude"/> m, kg/m³: the standard atmosphere's pressure over
    /// R·T, with <paramref name="seaLevelCelsius"/> falling 6.5 °C per km. 1.225 at sea level, 1.17 at
    /// 500 m, 0.98 at the Grimsel (2164 m): the same watts go ~6% faster on the pass.
    /// </summary>
    public static float AirDensity(float altitude, float seaLevelCelsius = 15f)
    {
        float h = Mathf.Clamp(altitude, -500f, 9000f);
        float kelvin = seaLevelCelsius + 273.15f - 0.0065f * h;
        float pressure = 101325f * Mathf.Pow(1f - 2.25577e-5f * h, 5.25588f);
        return pressure / (287.05f * kelvin);
    }

    /// <summary>
    /// The share of drag a rider saves <paramref name="gap"/> m behind a wheel, <paramref name="lateral"/>
    /// m off its line: ~40% half a wheel back, a third at 1 m, a fifth at 3 m, gone by 15 m
    /// (Blocken 2013, Barry 2015 wind-tunnel and CFD), and nothing once out of the wake (~0.6 m aside).
    /// </summary>
    public static float WheelDraft(float gap, float lateral)
    {
        if (gap < 0.2f || gap > 15f) return 0f;
        float behind = 0.40f * Mathf.Exp(-Mathf.Max(gap - 0.5f, 0f) / 4f);
        float aside = 1f - Mathf.Clamp(Mathf.Abs(lateral) / 0.6f, 0f, 1f);
        return behind * aside;
    }

    /// <summary>Air, tyres and gravity against the bike along the road, N (negative: a descent pulls it on).</summary>
    public static float ResistingForce(float speed, float mass, float dragArea, float draft, float rollingResistance,
        float airDensity, float grade)
    {
        float cos = 1f / Mathf.Sqrt(1f + grade * grade);
        float drag = 0.5f * airDensity * dragArea * (1f - draft) * speed * speed;
        float rolling = rollingResistance * mass * Gravity * cos;
        float climb = mass * Gravity * grade * cos;
        return drag + rolling + climb;
    }

    /// <summary>
    /// The speed <paramref name="watts"/> at the cranks hold for ever, m/s: where η·P = F·v. The
    /// reference numbers in the tests and the plan come from here.
    /// </summary>
    public static float SteadySpeed(float watts, float mass, float dragArea, float rollingResistance, float airDensity,
        float grade = 0f, float draft = 0f)
    {
        float lo = 0f, hi = 40f;
        for (int i = 0; i < 60; i++)
        {
            float v = 0.5f * (lo + hi);
            float need = ResistingForce(v, mass, dragArea, draft, rollingResistance, airDensity, grade) * v;
            if (need < watts * DrivetrainEfficiency) lo = v; else hi = v;
        }
        return lo;
    }
}
