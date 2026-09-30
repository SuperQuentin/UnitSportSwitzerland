using System.Collections.Generic;
using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// One motorbike as numbers: the published figures of the real machine, each entry's source or
/// assumption next to it in <see cref="MotorbikeCatalog"/>. Adding a bike is adding an entry there;
/// <see cref="Motorbike"/> and <see cref="Motorcyclist"/> read everything from this.
/// </summary>
public sealed record MotorbikeSpec
{
    /// <summary>Assigned by <see cref="MotorbikeCatalog"/> from the entry's position; never set by hand.</summary>
    public RideKind Kind { get; init; }
    public required string Label { get; init; }
    public required string Blurb { get; init; }
    /// <summary>Shape for the mesh: bodywork style, engine shape, tyre sizes, geometry, contact points, colours.</summary>
    public required MotoLook Look { get; init; }
    /// <summary>What it sounds like (<see cref="EngineProfile.For"/>, at this bike's idle and redline).</summary>
    public EngineLayout Engine { get; init; } = EngineLayout.Crossplane4;

    /// <summary>Wet (ready to ride) mass of the bike, kg; <see cref="Motorbike.RiderMass"/> is added on top.</summary>
    public float WetMass { get; init; }
    /// <summary>Centre of mass height with the rider aboard, m, and the rear axle's share of the weight.</summary>
    public float CgHeight { get; init; }
    public float RearShare { get; init; } = 0.5f;
    /// <summary>Peak tyre friction on dry tarmac (street sport tyres ~1.1-1.2, dual-sport ~0.95).</summary>
    public float Grip { get; init; } = 1.15f;
    /// <summary>Maximum lean the tyres and the ground clearance allow, radians.</summary>
    public float MaxLean { get; init; }
    /// <summary>Drag area Cd·A with the rider in position, m².</summary>
    public float DragArea { get; init; }
    /// <summary>Best braking, m/s² (both brakes, ABS), before the stoppie limit.</summary>
    public float BrakeDecel { get; init; }

    public float IdleRpm { get; init; }
    public float PeakRpm { get; init; }
    public float Redline { get; init; }
    /// <summary>Clutch-slip launch rpm: what the engine is held at while the clutch takes up.</summary>
    public float LaunchRpm { get; init; }
    /// <summary>Crank torque curve (rpm, N·m), ascending.</summary>
    public required (float Rpm, float Nm)[] Torque { get; init; }
    public required float[] Gears { get; init; }
    public float Primary { get; init; }
    public float FinalDrive { get; init; }
    /// <summary>Dual-clutch box: shifts itself with no drive cut.</summary>
    public bool Dct { get; init; }
    /// <summary>Clutchless up-shifts (a quickshifter): a short cut rather than a rider's clutch-and-lift.</summary>
    public bool QuickShifter { get; init; }

    /// <summary>Published figures the model is checked against (<c>--motocheck</c>).</summary>
    public float RefZeroTo100 { get; init; }
    public float RefTopKmh { get; init; }

    /// <summary>Drive cut per up-shift, s. Every box shifts itself here (there is no clutch input); how long it takes is the difference.</summary>
    public float ShiftCut => Dct ? 0.02f : QuickShifter ? 0.07f : 0.25f;

    public float TorqueAt(float rpm)
    {
        if (rpm <= Torque[0].Rpm) return Torque[0].Nm;
        for (int i = 1; i < Torque.Length; i++)
            if (rpm <= Torque[i].Rpm)
                return Mathf.Lerp(Torque[i - 1].Nm, Torque[i].Nm, (rpm - Torque[i - 1].Rpm) / Mathf.Max(1f, Torque[i].Rpm - Torque[i - 1].Rpm));
        return Torque[^1].Nm;
    }
}

/// <summary>
/// Every motorbike. <b>Append-only</b>: a bike's <see cref="RideKind"/> is <c>First + its index
/// here</c>, replicated as an int, so inserting or reordering renumbers every bike after it on every
/// peer. Motorbikes own <see cref="RideKind"/> 64..95; the next other mount is 96.
/// </summary>
public static class MotorbikeCatalog
{
    public const int First = 64, Last = 95;

    public static readonly IReadOnlyList<MotorbikeSpec> All = Number(new[]
    {
        // ---- 64 ----
        // Yamaha YZF-R1 (2020+). Yamaha EU spec sheet: 998 cc crossplane inline four, 200 PS
        // (147.1 kW) at 13,500 rpm, 112.4 N·m at 11,500 rpm; primary 65/43 = 1.512, gears
        // 2.600/2.176/1.842/1.579/1.381/1.250, final 41/16 = 2.563; wet 201 kg; wheelbase 1,405 mm;
        // rake 24°, trail 102 mm; seat 855 mm; tyres 120/70ZR17 and 190/55ZR17; quickshifter.
        // Top speed 299 km/h (the Japanese makers' limiter), magazine 0-100 km/h ~3.1 s.
        // Assumed (not published): the torque curve between its peaks, redline 14,000 (limiter),
        // CG 0.62 m with a tucked rider, 50/50 weight split with the rider, CdA 0.34 m² tucked (this
        // lands the drag-limited top speed ~2% over the limiter, as the unrestricted bike is), 55°
        // lean on road tyres, braking 10.5 m/s² (~37 m from 100 km/h in tests), launch at 7,000 rpm.
        new MotorbikeSpec
        {
            Label = "Sport bike (R1)",
            Blurb = "998 cc crossplane four, 200 PS, 201 kg: W / RT gas, S / LT brake, A/D lean. Wheelies if you let it",
            Engine = EngineLayout.Crossplane4,
            Look = new MotoLook
            {
                Style = MotoStyle.Sport, EngineShape = MotoEngineShape.InlineFour,
                Wheelbase = 1.405f, RakeDeg = 24f, Trail = 0.102f, ForkLength = 0.62f,
                FrontTyre = "120/70ZR17", RearTyre = "190/55ZR17", FrontTravel = 0.12f, RearTravel = 0.12f,
                // clip-ons low and far forward, rearsets high: the rider folds onto the tank
                Seat = new Vector3(0, 0.855f, -0.26f), Grip = new Vector3(0.33f, 0.83f, 0.38f), Peg = new Vector3(0.17f, 0.39f, -0.2f),
                Paint = new Color(0.05f, 0.2f, 0.72f), Trim = new Color(0.9f, 0.91f, 0.93f),
                Frame = new Color(0.12f, 0.12f, 0.14f), Wheel = new Color(0.1f, 0.25f, 0.8f),
            },
            WetMass = 201f, CgHeight = 0.62f, RearShare = 0.5f, Grip = 1.2f, MaxLean = Mathf.DegToRad(55f),
            DragArea = 0.34f, BrakeDecel = 10.5f,
            IdleRpm = 1300f, PeakRpm = 13500f, Redline = 14000f, LaunchRpm = 7000f,
            Torque = new (float, float)[] { (1300f, 45f), (3000f, 62f), (5000f, 74f), (7000f, 86f), (9000f, 99f), (10500f, 108f), (11500f, 112.4f), (12500f, 110f), (13500f, 104.1f), (14000f, 97f) },
            Gears = new[] { 2.6f, 2.176f, 1.842f, 1.579f, 1.381f, 1.25f }, Primary = 65f / 43f, FinalDrive = 41f / 16f,
            QuickShifter = true,
            RefZeroTo100 = 3.1f, RefTopKmh = 299f,
        },
        // ---- 65 ----
        // Ducati Monster (2021+, 937 Testastretta 11°). Ducati spec sheet: 111 hp (82 kW) at
        // 9,250 rpm, 93 N·m at 6,500 rpm; 188 kg kerb (90% fuel, the published "wet"); wheelbase
        // 1,474 mm; rake 24°, trail 93 mm; seat 820 mm; tyres 120/70ZR17 and 180/55ZR17; Ducati
        // Quick Shift standard. Assumed (uncertain): gear ratios carried over from the 821/939 box
        // (37/15, 30/17, 28/20, 26/22, 24/23, 23/24), primary 1.85 and final 43/15; redline ~10,500;
        // the torque curve between the peaks; CG 0.66 m with an upright rider, 49% rear; CdA
        // 0.44 m² naked and upright; top speed ~230 km/h and 0-100 km/h ~3.4 s from magazine tests;
        // 50° lean, braking 10 m/s², launch at 5,000 rpm.
        new MotorbikeSpec
        {
            Label = "Naked bike (Monster)",
            Blurb = "937 cc 90° V-twin, 111 hp, 188 kg, upright bars: W / RT gas, S / LT brake, A/D lean",
            Engine = EngineLayout.VTwin90,
            Look = new MotoLook
            {
                Style = MotoStyle.Naked, EngineShape = MotoEngineShape.VTwin,
                Wheelbase = 1.474f, RakeDeg = 24f, Trail = 0.093f, ForkLength = 0.64f,
                FrontTyre = "120/70ZR17", RearTyre = "180/55ZR17", FrontTravel = 0.13f, RearTravel = 0.14f,
                // wide bar high and close, pegs under the hips: sits up
                Seat = new Vector3(0, 0.82f, -0.2f), Grip = new Vector3(0.38f, 1.0f, 0.3f), Peg = new Vector3(0.16f, 0.35f, -0.06f),
                Paint = new Color(0.78f, 0.05f, 0.05f), Trim = new Color(0.12f, 0.12f, 0.13f),
                Frame = new Color(0.75f, 0.06f, 0.06f), Wheel = new Color(0.08f, 0.08f, 0.09f),
            },
            WetMass = 188f, CgHeight = 0.66f, RearShare = 0.51f, Grip = 1.15f, MaxLean = Mathf.DegToRad(50f),
            DragArea = 0.44f, BrakeDecel = 10f,
            IdleRpm = 1350f, PeakRpm = 9250f, Redline = 10500f, LaunchRpm = 5000f,
            Torque = new (float, float)[] { (1350f, 50f), (2500f, 62f), (4000f, 77f), (5500f, 89f), (6500f, 93f), (7500f, 91f), (8500f, 88f), (9250f, 84.7f), (10000f, 77f), (10500f, 68f) },
            Gears = new[] { 37f / 15f, 30f / 17f, 28f / 20f, 26f / 22f, 24f / 23f, 23f / 24f }, Primary = 1.85f, FinalDrive = 43f / 15f,
            QuickShifter = true,
            RefZeroTo100 = 3.4f, RefTopKmh = 230f,
        },
    });

    /// <summary>The spec for a motorbike kind, or null when the kind is not a motorbike.</summary>
    public static MotorbikeSpec? For(RideKind kind)
    {
        int i = (int)kind - First;
        return i >= 0 && i < All.Count ? All[i] : null;
    }

    public static bool IsMotorbike(RideKind kind) => (int)kind >= First && (int)kind <= Last;

    private static IReadOnlyList<MotorbikeSpec> Number(MotorbikeSpec[] bikes)
    {
        if (bikes.Length > Last - First + 1)
            throw new System.InvalidOperationException($"{bikes.Length} motorbikes overflow RideKind {First}..{Last}");
        for (int i = 0; i < bikes.Length; i++) bikes[i] = bikes[i] with { Kind = (RideKind)(First + i) };
        return bikes;
    }
}
