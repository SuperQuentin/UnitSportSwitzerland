using Godot;

namespace UnitSport.Player;

/// <summary>Where the rider's hands are, and so how much air they push (#1124).</summary>
public enum RidingPosition { Hoods, Drops, Tuck, Standing }

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

    /// <summary>
    /// Hoods 1, drops 0.88, a descender's tuck 0.75, out of the saddle 1.15 of the hoods' drag area
    /// (wind-tunnel ratios, Blocken 2018; standing is taller and wider).
    /// </summary>
    public static float PositionFactor(RidingPosition position) => position switch
    {
        RidingPosition.Drops => 0.88f,
        RidingPosition.Tuck => 0.75f,
        RidingPosition.Standing => 1.15f,
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

    /// <summary>Peak friction of a road tyre on dry tarmac: 0.8-1.0 measured; the rest is <c>Motorbike.SurfaceGrip</c>'s table.</summary>
    public const float TyreGrip = 0.9f;

    /// <summary>A locked tyre slides at this share of its peak grip.</summary>
    public const float SlidingShare = 0.8f;

    /// <summary>How much of the lever pull goes to the front: a rider who knows the front brake stops a bike.</summary>
    public const float FrontBias = 0.7f;

    /// <summary>
    /// How close to going over the bars the rider lets the front brake take them: they feel the rear
    /// go light and ease off, as the motorbike's rider does at the stoppie point.
    /// </summary>
    public const float PitchMargin = 0.95f;

    /// <summary>
    /// Both levers fully pulled, as a deceleration in g: hydraulic discs lock a wheel with two
    /// fingers; rim calipers need a hand and still give less.
    /// </summary>
    public static float LeverStrength(BikeBrakes brakes) => brakes == BikeBrakes.Disc ? 1.1f : 0.9f;

    /// <summary>One braking step's outcome.</summary>
    /// <param name="Decel">m/s².</param>
    /// <param name="RearLocked">The rear tyre is sliding: the rear went light under the front's braking.</param>
    /// <param name="PitchLimited">The front was held back at the over-the-bars point.</param>
    public readonly record struct BrakeResult(float Decel, bool RearLocked, bool PitchLimited);

    /// <summary>
    /// Front and rear brakes on a bicycle (#1124). The lever pull goes <see cref="FrontBias"/> to the
    /// front; braking moves the load forward by <c>a·h/L</c>, so the rear tyre's grip shrinks as the
    /// stop gets harder until it locks and slides; the front is capped by its own grip and by
    /// the pitch-over point <c>a = g·x_f/h</c>, about 0.6 g for a seated rider (Wilson, Bicycling
    /// Science). No bike stops harder than that on two wheels, whatever its brakes.
    /// </summary>
    /// <param name="lever">0..1, both levers together.</param>
    /// <param name="grip">Peak friction of the tyres on this ground.</param>
    /// <param name="strength"><see cref="LeverStrength"/>.</param>
    /// <param name="toFront">Centre of mass behind the front contact, m.</param>
    /// <param name="toRear">Centre of mass ahead of the rear contact, m.</param>
    /// <param name="height">Centre of mass height, m.</param>
    public static BrakeResult Brake(float lever, float grip, float strength, float toFront, float toRear, float height)
    {
        float wheelbase = toFront + toRear;
        float pitch = PitchMargin * Gravity * toFront / height;
        float wantFront = lever * FrontBias * strength * Gravity;
        float wantRear = lever * (1f - FrontBias) * strength * Gravity;
        float a = 0f;
        bool locked = false, pitched = false;
        // the load split depends on the deceleration it produces: damped rounds settle it to 0.01%
        for (int i = 0; i < 14; i++)
        {
            float frontLoad = Mathf.Clamp((toRear + a / Gravity * height) / wheelbase, 0f, 1f);
            float rearGrip = grip * Gravity * (1f - frontLoad);
            float front = Mathf.Min(wantFront, grip * Gravity * frontLoad);
            locked = lever > 0.02f && wantRear > rearGrip;
            float rear = locked ? SlidingShare * rearGrip : wantRear;
            float next = front + rear;
            pitched = next > pitch;
            a = 0.5f * (a + Mathf.Min(next, pitch));
        }
        return new BrakeResult(a, locked, pitched);
    }

    /// <summary>
    /// The lean the tyres hold after braking or accelerating takes its share of the friction circle,
    /// radians: <c>tan φ ≤ √(μ² − (a/g)²)</c>. Braking hard stands the bike up.
    /// </summary>
    public static float GripLean(float grip, float accel)
    {
        float used = Mathf.Min(Mathf.Abs(accel) / Gravity, grip * 0.95f);
        return Mathf.Atan(Mathf.Sqrt(grip * grip - used * used));
    }
}
