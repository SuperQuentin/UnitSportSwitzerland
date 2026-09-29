using System.Collections.Generic;
using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// Every drivable car: the Initial D roster. Figures are the real model's (factory or period
/// road-test numbers, tuned where the series says the car is tuned); the livery is the one it wears
/// in the series.
///
/// <para>
/// <b>Append-only.</b> A car's <see cref="RideKind"/> is <c>First + its index here</c>, replicated
/// as an int, so inserting or reordering renumbers every car after it on every peer. Cars own
/// <see cref="RideKind"/> 8..63; the next non-car mount is 64.
/// </para>
///
/// <para>
/// Names are the chassis code plus what it is ("AE86 hatch"), not brand or model names, and the
/// blurb says who drives it in the series.
/// </para>
/// </summary>
public static class CarCatalog
{
    public const int First = 8, Last = 63;

    private static readonly Color Black = new(0.06f, 0.06f, 0.07f);
    private static readonly Color Silver = new(0.78f, 0.79f, 0.82f);
    private static readonly Color White = new(0.94f, 0.94f, 0.92f);

    public static readonly IReadOnlyList<CarSpec> All = Number(new[]
    {
        // ---- 8: the three from #1, ids fixed ----
        new CarSpec
        {
            Label = "AE86 hatch",
            Blurb = "Tofu delivery panda: light, rear drive, a 4A-GE that revs to 7,800. W / RT gas, S / LT brake, Space / A handbrake",
            Body = new CarBody
            {
                Shape = BodyShape.Hatchback, Length = 4.2f, Width = 1.63f, Height = 1.34f, WheelRadius = 0.29f,
                Paint = White, Lower = Black, Rim = new Color(0.85f, 0.85f, 0.85f), PopUps = true,
            },
            Engine = EngineLayout.Inline4,
            Mass = 1000f, FrontAxle = 1.12f, RearAxle = 1.28f, CgHeight = 0.5f, Grip = 1.0f,
            PeakKw = 96f, PeakRpm = 6600f, IdleRpm = 900f, Redline = 7800f,
            Gears = new[] { 3.59f, 2.02f, 1.38f, 1.00f, 0.86f }, FinalDrive = 4.3f,
            MaxSteer = 0.62f, DragArea = 0.62f,
        },
        new CarSpec
        {
            Label = "FD3S",
            Blurb = "Yellow twin-turbo rotary, rear drive, 280 hp. Snappy: throttle alone breaks the rear loose",
            Body = new CarBody
            {
                Shape = BodyShape.Fastback, Length = 4.3f, Width = 1.76f, Height = 1.23f, WheelRadius = 0.31f,
                Paint = new Color(1f, 0.86f, 0.16f), Rim = Silver, PopUps = true, Wing = WingSize.Small,
            },
            Engine = EngineLayout.RotaryTurbo,
            Mass = 1320f, FrontAxle = 1.2f, RearAxle = 1.23f, CgHeight = 0.46f, Grip = 1.05f,
            PeakKw = 206f, PeakRpm = 6500f, IdleRpm = 850f, Redline = 8000f,
            Gears = new[] { 3.48f, 2.02f, 1.39f, 1.00f, 0.72f }, FinalDrive = 4.1f,
            MaxSteer = 0.6f, DragArea = 0.58f,
        },
        new CarSpec
        {
            Label = "GC8 rally saloon",
            Blurb = "Turbo boxer, four-wheel drive. Grips hard; drifts all four wheels on gravel",
            Body = new CarBody
            {
                Shape = BodyShape.Sedan, Length = 4.4f, Width = 1.74f, Height = 1.43f, WheelRadius = 0.32f,
                Paint = new Color(0.2f, 0.4f, 0.85f), Rim = new Color(0.86f, 0.66f, 0.15f),
                Wing = WingSize.Big, Scoop = true,
            },
            Engine = EngineLayout.Boxer4Turbo,
            Mass = 1360f, FrontAxle = 1.28f, RearAxle = 1.24f, CgHeight = 0.52f, Grip = 1.05f,
            PeakKw = 206f, PeakRpm = 6000f, IdleRpm = 850f, Redline = 7000f,
            Gears = new[] { 3.17f, 1.88f, 1.30f, 0.97f, 0.74f }, FinalDrive = 4.44f,
            Drive = Drivetrain.All, RearBias = 0.6f,
            MaxSteer = 0.58f, DragArea = 0.7f,
        },
        // ---- 11 onward: the rest of the roster (append only) ----
    });

    /// <summary>The spec for a car kind, or null when the kind is not a car.</summary>
    public static CarSpec? For(RideKind kind)
    {
        int i = (int)kind - First;
        return i >= 0 && i < All.Count ? All[i] : null;
    }

    public static bool IsCar(RideKind kind) => (int)kind >= First && (int)kind <= Last;

    private static IReadOnlyList<CarSpec> Number(CarSpec[] cars)
    {
        if (cars.Length > Last - First + 1)
            throw new System.InvalidOperationException($"{cars.Length} cars overflow RideKind {First}..{Last}");
        for (int i = 0; i < cars.Length; i++) cars[i] = cars[i] with { Kind = (RideKind)(First + i) };
        return cars;
    }
}
