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
/// blurb describes the real car: model, era, engine and drivetrain, what it is known for.
/// </para>
/// </summary>
public static class CarCatalog
{
    public const int First = 8, Last = 63;

    private static readonly Color Black = new(0.06f, 0.06f, 0.07f);
    private static readonly Color Silver = new(0.78f, 0.79f, 0.82f);
    private static readonly Color White = new(0.94f, 0.94f, 0.92f);
    /// <summary>A rental kart's hard slick: it grips tarmac, and little else (#715).</summary>
    private static readonly TyreType KartTyre = new("Kart slick", 1f, 0.7f, 0.5f, 0.3f, 0.1f);

    public static readonly IReadOnlyList<CarSpec> All = Number(new[]
    {
        // ---- 8: the three from #1, ids fixed ----
        new CarSpec
        {
            Label = "AE86 hatch",
            Blurb = "1983 Toyota Sprinter Trueno: light, rear drive, a 4A-GE twin-cam that revs to 7,800, a drift and hill-climb icon. {throttle} gas, {brake} brake, {jump} handbrake",
            Body = new CarBody
            {
                Shape = BodyShape.Hatchback, Length = 4.2f, Width = 1.63f, Height = 1.34f, WheelRadius = 0.29f,
                Paint = White, Lower = Black, Rim = new Color(0.85f, 0.85f, 0.85f), PopUps = true,
            },
            Engine = EngineLayout.Inline4,
            // source: Toyota AE86 Sprinter Trueno GT-APEX 3-door (4A-GE 130 PS/6600, 149 Nm/5200, T50 5MT, 4.300 final); kerb 965 kg + driver; period road tests 0-100 ~8.5 s, 100-0 ~42 m; the series shows Takumi's car with a mechanical LSD; curve fitted to published peaks
            Mass = 1040f, FrontAxle = 1.128f, RearAxle = 1.272f, CgHeight = 0.5f, Grip = 0.97f,
            PeakKw = 96f, PeakRpm = 6600f, IdleRpm = 900f, Redline = 7800f,
            Gears = new[] { 3.587f, 2.022f, 1.384f, 1f, 0.861f }, FinalDrive = 4.3f, Reverse = 3.51f,
            Torque = new (float, float)[] { (1000f, 105f), (2000f, 118f), (3000f, 130f), (4000f, 140f), (5200f, 149f), (6000f, 146f), (6600f, 138.3f), (7200f, 122f), (7800f, 105f) },
            Tyre = "185/60R14", BrakeDecel = 9.2f, Diff = Differential.Mechanical,
            RefZeroTo100 = 8.6f, RefTopKmh = 195f,
            MaxSteer = 0.62f, DragArea = 0.62f,
            LockTurns = 3.5f, // lock to lock, est.: unassisted rack
            PowerSteering = false,
        },
        new CarSpec
        {
            Label = "FD3S",
            Blurb = "1990s Mazda RX-7: sequential twin-turbo 13B rotary, rear drive, 280 PS. Snappy: throttle alone breaks the rear loose",
            Body = new CarBody
            {
                Shape = BodyShape.Fastback, Length = 4.3f, Width = 1.76f, Height = 1.23f, WheelRadius = 0.31f,
                Paint = new Color(1f, 0.86f, 0.16f), Rim = Silver, PopUps = true, Wing = WingSize.Small,
            },
            Engine = EngineLayout.RotaryTurbo,
            // source: Mazda FD3S RX-7 Type RS (13B-REW 280 PS/6500, 314 Nm/5000, 5MT 4.100, 225/50R16); the series' car is tuned, stock figures used; road tests 0-100 ~5.3 s, 250-260 km/h unlimited (255 used); curve fitted to published peaks
            Mass = 1355f, FrontAxle = 1.164f, RearAxle = 1.261f, CgHeight = 0.46f, Grip = 1.06f,
            PeakKw = 206f, PeakRpm = 6500f, IdleRpm = 800f, Redline = 7000f,
            Gears = new[] { 3.483f, 2.015f, 1.391f, 1f, 0.719f }, FinalDrive = 4.1f, Reverse = 3.493f,
            Torque = new (float, float)[] { (1000f, 150f), (2000f, 175f), (3000f, 225f), (4000f, 272f), (5000f, 314f), (6000f, 312f), (6500f, 302.6f), (7000f, 270f) },
            Tyre = "225/50R16", BrakeDecel = 10.1f, Diff = Differential.Torsen,
            RefZeroTo100 = 5.3f, RefTopKmh = 255f,
            MaxSteer = 0.6f, DragArea = 0.6f,
            LockTurns = 2.9f, // lock to lock, est.
        },
        new CarSpec
        {
            Label = "GC8 rally saloon",
            Blurb = "1990s Subaru Impreza WRX STi: turbo boxer, four-wheel drive, born of the World Rally Championship. Grips hard; drifts all four wheels on gravel",
            Body = new CarBody
            {
                Shape = BodyShape.Sedan, Length = 4.4f, Width = 1.74f, Height = 1.43f, WheelRadius = 0.32f,
                Paint = new Color(0.2f, 0.4f, 0.85f), Rim = new Color(0.86f, 0.66f, 0.15f),
                Wing = WingSize.Big, Scoop = true,
            },
            Engine = EngineLayout.Boxer4Turbo,
            // source: Subaru Impreza WRX STi (GC8, Version V-era: EJ207 280 PS/6500, 353 Nm/4000, 5MT, 4.444 final, 205/50R16, kerb ~1270 kg); the series' Bunta car is not quoted as tuned; curve fitted to published peaks
            Mass = 1345f, FrontAxle = 1.0332f, RearAxle = 1.4868f, CgHeight = 0.52f, Grip = 1.05f,
            PeakKw = 206f, PeakRpm = 6500f, IdleRpm = 850f, Redline = 7500f,
            Gears = new[] { 3.454f, 1.947f, 1.366f, 0.972f, 0.738f }, FinalDrive = 4.444f, Reverse = 3.416f,
            Torque = new (float, float)[] { (1000f, 170f), (2000f, 250f), (3000f, 315f), (4000f, 353f), (5000f, 345f), (6000f, 320f), (6500f, 302.6f), (7000f, 275f), (7500f, 245f) },
            Tyre = "205/50R16", BrakeDecel = 9.9f, Diff = Differential.Viscous,
            RefZeroTo100 = 5.4f, RefTopKmh = 240f,
            Drive = Drivetrain.All, RearBias = 0.55f,
            MaxSteer = 0.58f, DragArea = 0.66f,
            LockTurns = 2.6f, // lock to lock, est.: STi quick rack
        },
        // Sources: series character/car lists (GTPlanet "Initial D cars" thread, Initial D wiki extracts),
        // real-world figures from factory/period road-test specs of each model; liveries approximate the series.
        // ---- 11: Project D / Akina / Akagi heroes ----
        new CarSpec
        {
            Label = "FC3S",
            Blurb = "Second-generation Mazda RX-7 (1985-92): a light, precise turbo rotary with rear drive and near-even balance",
            Body = new CarBody
            {
                Shape = BodyShape.Fastback, Length = 4.3f, Width = 1.69f, Height = 1.27f, WheelRadius = 0.31f,
                Paint = White, Rim = Silver, PopUps = true, Wing = WingSize.Lip,
            },
            Engine = EngineLayout.RotaryTurbo,
            // source: Mazda Savanna RX-7 Infini III (FC3S, 13B-T 215 PS/6500, 275 Nm/3500, 5MT 4.100); kerb ~1270 kg; brochure figures, 0-100 ~7.2 s; curve fitted to published peaks
            Mass = 1345f, FrontAxle = 1.2028f, RearAxle = 1.2272f, CgHeight = 0.47f, Grip = 1.02f,
            PeakKw = 158f, PeakRpm = 6500f, IdleRpm = 800f, Redline = 7500f,
            Gears = new[] { 3.483f, 2.015f, 1.391f, 1f, 0.719f }, FinalDrive = 4.1f, Reverse = 3.493f,
            Torque = new (float, float)[] { (1000f, 130f), (2000f, 200f), (3000f, 260f), (3500f, 275f), (4500f, 270f), (5500f, 255f), (6500f, 232f), (7000f, 205f), (7500f, 180f) },
            Tyre = "205/55R16", BrakeDecel = 9.6f, Diff = Differential.Torsen,
            RefZeroTo100 = 7.2f, RefTopKmh = 235f,
            MaxSteer = 0.6f, DragArea = 0.58f,
            LockTurns = 2.7f, // lock to lock, est.: with power steering
        },
        new CarSpec
        {
            Label = "BNR32",
            Style = DriveStyle.Grip,
            Blurb = "1989 Nissan Skyline GT-R: twin-turbo RB26 six and ATTESA four-wheel drive that dominated Group A touring cars. Heavy in the corners",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.54f, Width = 1.755f, Height = 1.34f, WheelRadius = 0.316f,
                Paint = new Color(0.33f, 0.34f, 0.38f), Rim = Silver, Wing = WingSize.Small,
            },
            Engine = EngineLayout.Inline6Turbo,
            // source: Nissan Skyline GT-R V-spec II (BNR32, RB26DETT 280 PS/6800, 353 Nm/4400, 5MT 4.111, 225/50R16, kerb 1480 kg); period tests 0-100 ~5.6 s; curve fitted to published peaks
            Mass = 1555f, FrontAxle = 1.0721f, RearAxle = 1.5429f, CgHeight = 0.5f, Grip = 1.05f,
            PeakKw = 206f, PeakRpm = 6800f, IdleRpm = 800f, Redline = 8000f,
            Gears = new[] { 3.214f, 1.925f, 1.302f, 1f, 0.752f }, FinalDrive = 4.111f, Reverse = 3.437f,
            Torque = new (float, float)[] { (1000f, 150f), (2000f, 230f), (3000f, 305f), (4000f, 350f), (4400f, 353f), (5000f, 345f), (6000f, 315f), (6800f, 289.2f), (7500f, 255f), (8000f, 230f) },
            Tyre = "225/50R16", BrakeDecel = 9.9f, Diff = Differential.Torsen,
            RefZeroTo100 = 5.6f, RefTopKmh = 250f,
            Drive = Drivetrain.All, RearBias = 0.7f,
            MaxSteer = 0.58f, DragArea = 0.72f,
            LockTurns = 2.7f, // lock to lock, published
        },
        new CarSpec
        {
            Label = "EG6 hatch",
            Style = DriveStyle.Grip,
            Blurb = "1990s Honda Civic SiR-II: a light front-drive hatch with a 1.6 VTEC that revs past 8,000",
            Body = new CarBody
            {
                Shape = BodyShape.Hatchback, Length = 4.0f, Width = 1.695f, Height = 1.34f, WheelRadius = 0.29f,
                Paint = White, Rim = Silver,
            },
            Engine = EngineLayout.Inline4,
            // source: Honda Civic SiR-II (EG6, 1995: B16A 170 PS/7600, 155 Nm/7000, 5MT 4.400, 195/55R14, kerb 1050 kg); JDM brochure + period tests 0-100 7.4-7.9 s (7.7 used); open diff; curve fitted to published peaks
            Mass = 1125f, FrontAxle = 1.0218f, RearAxle = 1.5982f, CgHeight = 0.48f, Grip = 1.05f,
            PeakKw = 125f, PeakRpm = 7600f, IdleRpm = 800f, Redline = 8200f,
            Gears = new[] { 3.23f, 2.105f, 1.458f, 1.107f, 0.848f }, FinalDrive = 4.4f, Reverse = 3f,
            Torque = new (float, float)[] { (1000f, 90f), (2000f, 118f), (3000f, 125f), (4000f, 130f), (5000f, 133f), (5600f, 140f), (6500f, 148f), (7000f, 154f), (7600f, 157f), (8000f, 148f), (8200f, 138f) },
            Tyre = "195/55R14", BrakeDecel = 9.9f, Diff = Differential.Open,
            RefZeroTo100 = 7.7f, RefTopKmh = 215f,
            Drive = Drivetrain.Front,
            MaxSteer = 0.6f, DragArea = 0.6f,
            LockTurns = 3.0f, // lock to lock, est.
        },
        new CarSpec
        {
            Label = "S13 Silvia",
            Blurb = "Late-1980s Nissan Silvia: turbo CA18 four, rear drive, the budget coupe that grew up with drifting",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.44f, Width = 1.69f, Height = 1.29f, WheelRadius = 0.3f,
                Paint = White, Rim = Silver, PopUps = true,
            },
            Engine = EngineLayout.Inline4Turbo,
            // source: Nissan Silvia K's (S13, CA18DET 175 PS/6400, 226 Nm/4000, 5MT 4.083, 195/60R15, kerb ~1160 kg); brochure; period tests 0-100 ~7.9 s; viscous LSD; curve fitted to published peaks
            Mass = 1235f, FrontAxle = 1.089f, RearAxle = 1.386f, CgHeight = 0.48f, Grip = 1f,
            PeakKw = 129f, PeakRpm = 6400f, IdleRpm = 800f, Redline = 7000f,
            Gears = new[] { 3.321f, 1.902f, 1.308f, 1f, 0.759f }, FinalDrive = 4.083f, Reverse = 3.382f,
            Torque = new (float, float)[] { (1000f, 130f), (2000f, 175f), (3000f, 210f), (4000f, 226f), (5000f, 215f), (6000f, 200f), (6400f, 192.5f), (7000f, 170f) },
            Tyre = "195/60R15", BrakeDecel = 9.2f, Diff = Differential.Viscous,
            RefZeroTo100 = 7.9f, RefTopKmh = 215f,
            MaxSteer = 0.62f, DragArea = 0.62f,
            LockTurns = 2.8f, // lock to lock, est.
        },
        new CarSpec
        {
            Label = "RPS13 180SX",
            Blurb = "Nissan 180SX: pop-up fastback with the turbo SR20, rear drive and long a favourite of drift grids",
            Body = new CarBody
            {
                Shape = BodyShape.Fastback, Length = 4.5f, Width = 1.69f, Height = 1.29f, WheelRadius = 0.31f,
                Paint = new Color(0.85f, 0.85f, 0.87f), Rim = Silver, PopUps = true,
            },
            Engine = EngineLayout.Inline4Turbo,
            // source: Nissan 180SX Type X (RPS13 late: SR20DET 205 PS/6000, 275 Nm/4000, 5MT 4.363, 205/60R15, kerb 1240 kg); brochure; period tests 0-100 ~7.0 s; viscous LSD; curve fitted to published peaks
            Mass = 1315f, FrontAxle = 1.0642f, RearAxle = 1.4108f, CgHeight = 0.48f, Grip = 1.02f,
            PeakKw = 151f, PeakRpm = 6000f, IdleRpm = 800f, Redline = 7200f,
            Gears = new[] { 3.321f, 1.902f, 1.308f, 1f, 0.759f }, FinalDrive = 4.363f, Reverse = 3.382f,
            Torque = new (float, float)[] { (1000f, 150f), (2000f, 205f), (3000f, 255f), (4000f, 275f), (5000f, 262f), (6000f, 240.4f), (6500f, 215f), (7000f, 190f), (7200f, 180f) },
            Tyre = "205/60R15", BrakeDecel = 9.2f, Diff = Differential.Viscous,
            RefZeroTo100 = 7f, RefTopKmh = 225f,
            MaxSteer = 0.62f, DragArea = 0.62f,
            LockTurns = 2.8f, // lock to lock, est.
        },
        new CarSpec
        {
            Label = "PS13 Sileighty",
            Blurb = "A 180SX wearing a Silvia front: a drift-scene favourite, later built as a limited run by a tuner. Turbo SR20, rear drive",
            Body = new CarBody
            {
                Shape = BodyShape.Fastback, Length = 4.5f, Width = 1.69f, Height = 1.29f, WheelRadius = 0.31f,
                Paint = new Color(0.14f, 0.3f, 0.72f), Rim = Silver,
            },
            Engine = EngineLayout.Inline4Turbo,
            // source: Nissan Sileighty (180SX body, S13 front) with the SR20DET: 205 PS/6000, 275 Nm/4000, 5MT 4.363; mass as 180SX Type X; same figures as RPS13; curve fitted to published peaks; 0-100 estimated
            Mass = 1315f, FrontAxle = 1.0642f, RearAxle = 1.4108f, CgHeight = 0.48f, Grip = 1.02f,
            PeakKw = 151f, PeakRpm = 6000f, IdleRpm = 800f, Redline = 7200f,
            Gears = new[] { 3.321f, 1.902f, 1.308f, 1f, 0.759f }, FinalDrive = 4.363f, Reverse = 3.382f,
            Torque = new (float, float)[] { (1000f, 150f), (2000f, 205f), (3000f, 255f), (4000f, 275f), (5000f, 262f), (6000f, 240.4f), (6500f, 215f), (7000f, 190f), (7200f, 180f) },
            Tyre = "205/60R15", BrakeDecel = 9.2f, Diff = Differential.Viscous,
            RefZeroTo100 = 7f, RefTopKmh = 225f,
            MaxSteer = 0.62f, DragArea = 0.62f,
            LockTurns = 2.8f, // lock to lock, est.
        },
        new CarSpec
        {
            Label = "AE85 Levin",
            Blurb = "Toyota Corolla Levin 1.5: the AE86's plain sister with a single-cam 3A engine. Rear drive and forgiving, a car to learn on",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.2f, Width = 1.63f, Height = 1.31f, WheelRadius = 0.28f,
                Paint = new Color(0.6f, 0.7f, 0.82f), Rim = Silver, PopUps = true,
            },
            Engine = EngineLayout.Inline4,
            // source: Toyota Corolla Levin AE85 1.5 (3A-U 83 PS/5600, 118 Nm/3600, 5MT 4.100, 175/70R13, kerb ~900 kg); brochure figures; 0-100, top and curve estimated from the published peaks
            Mass = 975f, FrontAxle = 1.104f, RearAxle = 1.296f, CgHeight = 0.5f, Grip = 0.92f,
            PeakKw = 61f, PeakRpm = 5600f, IdleRpm = 800f, Redline = 6500f,
            Gears = new[] { 3.587f, 2.022f, 1.384f, 1f, 0.861f }, FinalDrive = 4.1f, Reverse = 3.51f,
            Torque = new (float, float)[] { (1000f, 80f), (2000f, 100f), (3600f, 118f), (4500f, 114f), (5600f, 104f), (6000f, 95f), (6500f, 85f) },
            Tyre = "175/70R13", BrakeDecel = 8.6f, Diff = Differential.Open,
            RefZeroTo100 = 13.5f, RefTopKmh = 165f,
            MaxSteer = 0.62f, DragArea = 0.62f,
            LockTurns = 3.5f, // lock to lock, est.: unassisted rack
            PowerSteering = false,
        },
        new CarSpec
        {
            Label = "AE86 coupe",
            Blurb = "Two-door AE86 Sprinter Trueno: the same 4A-GE and rear drive as the hatch, a little lighter",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.2f, Width = 1.63f, Height = 1.31f, WheelRadius = 0.29f,
                Paint = new Color(0.75f, 0.76f, 0.79f), Lower = Black, Rim = Silver, PopUps = true,
            },
            Engine = EngineLayout.Inline4,
            // source: Toyota AE86 Sprinter Trueno GT-APEX 2-door (4A-GE 130 PS/6600, 149 Nm/5200, 5MT 4.300, 185/60R14; kerb ~940 kg); same drivetrain as the hatch; mechanical LSD as in the series; curve fitted to published peaks
            Mass = 1015f, FrontAxle = 1.128f, RearAxle = 1.272f, CgHeight = 0.5f, Grip = 0.97f,
            PeakKw = 96f, PeakRpm = 6600f, IdleRpm = 900f, Redline = 7800f,
            Gears = new[] { 3.587f, 2.022f, 1.384f, 1f, 0.861f }, FinalDrive = 4.3f, Reverse = 3.51f,
            Torque = new (float, float)[] { (1000f, 105f), (2000f, 118f), (3000f, 130f), (4000f, 140f), (5200f, 149f), (6000f, 146f), (6600f, 138.3f), (7200f, 122f), (7800f, 105f) },
            Tyre = "185/60R14", BrakeDecel = 9.2f, Diff = Differential.Mechanical,
            RefZeroTo100 = 8.5f, RefTopKmh = 195f,
            MaxSteer = 0.62f, DragArea = 0.62f,
            LockTurns = 3.5f, // lock to lock, est.: unassisted rack
            PowerSteering = false,
        },
        // ---- Lancer Evolutions (Team Emperor and later rivals) ----
        new CarSpec
        {
            Label = "CE9A Evo III",
            Style = DriveStyle.Grip,
            Blurb = "1995 Mitsubishi Lancer Evolution III: turbo 4G63, four-wheel drive, a Group A rally weapon for the road",
            Body = new CarBody
            {
                Shape = BodyShape.Sedan, Length = 4.31f, Width = 1.69f, Height = 1.42f, WheelRadius = 0.31f,
                Paint = White, Rim = new Color(0.85f, 0.7f, 0.2f), Wing = WingSize.Big, Scoop = true,
            },
            Engine = EngineLayout.Inline4Turbo,
            // source: Mitsubishi Lancer Evolution III GSR (CE9A, 4G63 270 PS/6250, 309 Nm/3000, 5MT 4.529, 205/60R15, kerb ~1260 kg); period tests 0-100 ~5.7 s; viscous centre; curve fitted to published peaks (very flat)
            Mass = 1335f, FrontAxle = 1.0291f, RearAxle = 1.4809f, CgHeight = 0.52f, Grip = 1.03f,
            PeakKw = 199f, PeakRpm = 6250f, IdleRpm = 850f, Redline = 7500f,
            Gears = new[] { 2.785f, 1.95f, 1.407f, 1.031f, 0.731f }, FinalDrive = 4.529f, Reverse = 3.416f,
            Torque = new (float, float)[] { (1000f, 150f), (2000f, 240f), (3000f, 309f), (4500f, 309f), (5500f, 308f), (6250f, 304f), (6750f, 280f), (7500f, 236f) },
            Tyre = "205/60R15", BrakeDecel = 9.6f, Diff = Differential.Viscous,
            RefZeroTo100 = 5.7f, RefTopKmh = 235f,
            Drive = Drivetrain.All, RearBias = 0.5f,
            MaxSteer = 0.58f, DragArea = 0.72f,
            LockTurns = 2.6f, // lock to lock, est.
        },
        new CarSpec
        {
            Label = "CN9A Evo IV",
            Style = DriveStyle.Grip,
            Blurb = "1996 Mitsubishi Lancer Evolution IV: turbo four-wheel drive with Active Yaw Control that pulls the nose into the corner",
            Body = new CarBody
            {
                Shape = BodyShape.Sedan, Length = 4.33f, Width = 1.7f, Height = 1.42f, WheelRadius = 0.315f,
                Paint = Silver, Rim = new Color(0.85f, 0.7f, 0.2f), Wing = WingSize.Big, Scoop = true,
            },
            Engine = EngineLayout.Inline4Turbo,
            // source: Mitsubishi Lancer Evolution IV GSR (CN9A, 4G63 280 PS/6500, 353 Nm/3000, 5MT 4.529, 205/50R16, kerb 1350 kg, AYC); brochure; period tests 0-100 ~5.3 s; curve fitted to published peaks
            Mass = 1425f, FrontAxle = 1.0291f, RearAxle = 1.4809f, CgHeight = 0.52f, Grip = 1.06f,
            PeakKw = 206f, PeakRpm = 6500f, IdleRpm = 850f, Redline = 7500f,
            Gears = new[] { 2.785f, 1.95f, 1.407f, 1.031f, 0.72f }, FinalDrive = 4.529f, Reverse = 3.416f,
            Torque = new (float, float)[] { (1000f, 150f), (2000f, 270f), (3000f, 353f), (4000f, 350f), (5000f, 335f), (6000f, 316f), (6500f, 302.6f), (7000f, 275f), (7500f, 240f) },
            Tyre = "205/50R16", BrakeDecel = 9.8f, Diff = Differential.Torsen,
            RefZeroTo100 = 5.3f, RefTopKmh = 240f,
            Drive = Drivetrain.All, RearBias = 0.55f,
            MaxSteer = 0.58f, DragArea = 0.68f,
            LockTurns = 2.4f, // lock to lock, est.
        },
        new CarSpec
        {
            Label = "CT9A Evo VII",
            Style = DriveStyle.Grip,
            Blurb = "2001 Mitsubishi Lancer Evolution VII: active centre diff and yaw control, the rally-bred turbo 4G63 still up front",
            Body = new CarBody
            {
                Shape = BodyShape.Sedan, Length = 4.35f, Width = 1.7f, Height = 1.45f, WheelRadius = 0.32f,
                Paint = new Color(0.16f, 0.16f, 0.2f), Rim = Silver, Wing = WingSize.Big, Scoop = true,
            },
            Engine = EngineLayout.Inline4Turbo,
            // source: Mitsubishi Lancer Evolution VII GSR (CT9A, 4G63 280 PS/6500, 373 Nm/3500, 5MT 4.529, 235/45R17, kerb ~1360 kg, ACD+AYC); brochure; period tests 0-100 ~5.2 s; curve fitted to published peaks
            Mass = 1435f, FrontAxle = 1.05f, RearAxle = 1.575f, CgHeight = 0.52f, Grip = 1.08f,
            PeakKw = 206f, PeakRpm = 6500f, IdleRpm = 850f, Redline = 7500f,
            Gears = new[] { 2.785f, 1.95f, 1.407f, 1.031f, 0.72f }, FinalDrive = 4.529f, Reverse = 3.416f,
            Torque = new (float, float)[] { (1000f, 150f), (2000f, 280f), (3000f, 365f), (3500f, 373f), (4500f, 365f), (5500f, 340f), (6000f, 322f), (6500f, 302.6f), (7000f, 270f), (7500f, 235f) },
            Tyre = "235/45R17", BrakeDecel = 9.8f, Diff = Differential.Torsen,
            RefZeroTo100 = 5.2f, RefTopKmh = 245f,
            Drive = Drivetrain.All, RearBias = 0.55f,
            MaxSteer = 0.58f, DragArea = 0.68f,
            LockTurns = 2.2f, // lock to lock, published
        },
        // ---- Honda ----
        new CarSpec
        {
            Label = "NA1 NSX",
            Style = DriveStyle.Grip,
            Blurb = "1990 Honda NSX: aluminium mid-engined V6 supercar developed with Ayrton Senna's input. Balanced and precise",
            Body = new CarBody
            {
                Shape = BodyShape.Midship, Length = 4.43f, Width = 1.81f, Height = 1.17f, WheelRadius = 0.32f,
                Paint = White, Rim = Silver, PopUps = true, Wing = WingSize.Lip,
            },
            Engine = EngineLayout.V6,
            // source: Honda NSX NA1 5MT (C30A 280 PS/7300, 294 Nm/5400, 4.062 final, 225/50R16 rear, kerb 1350 kg); brochure + period tests 0-100 ~5.6 s, ~265 km/h; mechanical LSD estimated; curve fitted to published peaks
            Mass = 1425f, FrontAxle = 1.4674f, RearAxle = 1.0626f, CgHeight = 0.46f, Grip = 1.06f,
            PeakKw = 206f, PeakRpm = 7300f, IdleRpm = 800f, Redline = 8000f,
            Gears = new[] { 3.23f, 2.105f, 1.458f, 1.107f, 0.848f }, FinalDrive = 4.062f, Reverse = 3f,
            Torque = new (float, float)[] { (1000f, 130f), (2000f, 190f), (3000f, 230f), (4000f, 260f), (5400f, 294f), (6500f, 285f), (7300f, 269.5f), (7800f, 245f), (8000f, 232f) },
            Tyre = "225/50R16", BrakeDecel = 10.4f, Diff = Differential.Mechanical,
            RefZeroTo100 = 5.6f, RefTopKmh = 265f,
            MaxSteer = 0.58f, DragArea = 0.62f,
            LockTurns = 3.0f, // lock to lock, est.: unassisted with the manual box
            PowerSteering = false,
        },
        new CarSpec
        {
            Label = "EK9 Type R",
            Style = DriveStyle.Grip,
            Blurb = "1997 Honda Civic Type R: a stripped, high-revving front-drive hatch (B16B to 8,400) that punishes any mistake in a hairpin",
            Body = new CarBody
            {
                Shape = BodyShape.Hatchback, Length = 4.1f, Width = 1.695f, Height = 1.35f, WheelRadius = 0.3f,
                Paint = new Color(0.98f, 0.8f, 0.1f), Rim = Silver,
            },
            Engine = EngineLayout.Inline4,
            // source: Honda Civic Type R (EK9, B16B 185 PS/8200, 160 Nm/7500, 5MT 4.400, 195/55R15, kerb 1050 kg); JDM brochure; period tests 0-60 mph 6.7 s / 0-100 ~7.0 s; helical LSD (Torsen); curve fitted to published peaks
            Mass = 1125f, FrontAxle = 1.0218f, RearAxle = 1.5982f, CgHeight = 0.48f, Grip = 1.06f,
            PeakKw = 136f, PeakRpm = 8200f, IdleRpm = 800f, Redline = 8400f,
            Gears = new[] { 3.23f, 2.105f, 1.458f, 1.107f, 0.848f }, FinalDrive = 4.4f, Reverse = 3f,
            Torque = new (float, float)[] { (1000f, 90f), (2000f, 115f), (3000f, 125f), (4000f, 132f), (5000f, 135f), (5600f, 140f), (6500f, 152f), (7500f, 160f), (8200f, 158.4f), (8400f, 148f) },
            Tyre = "195/55R15", BrakeDecel = 10.1f, Diff = Differential.Torsen,
            RefZeroTo100 = 7f, RefTopKmh = 220f,
            Drive = Drivetrain.Front,
            MaxSteer = 0.6f, DragArea = 0.6f,
            LockTurns = 3.0f, // lock to lock, est.
        },
        new CarSpec
        {
            Label = "DC2 Type R",
            Style = DriveStyle.Grip,
            Blurb = "Honda Integra Type R: seam-welded, lightened front-drive coupe with an 8,000 rpm VTEC and the sharpest nose of its era",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.38f, Width = 1.695f, Height = 1.32f, WheelRadius = 0.3f,
                Paint = White, Rim = Silver, Wing = WingSize.Small,
            },
            Engine = EngineLayout.Inline4,
            // source: Honda Integra Type R (DC2, 96-spec, B18C 200 PS/8000, 181 Nm/7500, 5MT 4.400, 195/55R15, kerb 1080 kg); JDM brochure; period tests 0-100 ~6.6 s; helical LSD (Torsen); series' turbo not modelled; curve fitted to published peaks
            Mass = 1155f, FrontAxle = 0.9956f, RearAxle = 1.6244f, CgHeight = 0.48f, Grip = 1.06f,
            PeakKw = 147f, PeakRpm = 8000f, IdleRpm = 800f, Redline = 8400f,
            Gears = new[] { 3.23f, 2.105f, 1.458f, 1.107f, 0.848f }, FinalDrive = 4.4f, Reverse = 3f,
            Torque = new (float, float)[] { (1000f, 95f), (2000f, 125f), (3000f, 140f), (4000f, 150f), (5000f, 155f), (5800f, 160f), (6500f, 170f), (7500f, 181f), (8000f, 175.5f), (8400f, 150f) },
            Tyre = "195/55R15", BrakeDecel = 10f, Diff = Differential.Torsen,
            RefZeroTo100 = 6.6f, RefTopKmh = 230f,
            Drive = Drivetrain.Front,
            MaxSteer = 0.6f, DragArea = 0.62f,
            LockTurns = 2.9f, // lock to lock, est.
        },
        // ---- Toyota ----
        new CarSpec
        {
            Label = "SW20 MR2",
            Style = DriveStyle.Grip,
            Blurb = "Toyota MR2 (1989-99): affordable mid-engined coupe with near-perfect balance, sharp until it snaps",
            Body = new CarBody
            {
                Shape = BodyShape.Midship, Length = 4.17f, Width = 1.7f, Height = 1.24f, WheelRadius = 0.3f,
                Paint = White, Rim = Silver, PopUps = true,
            },
            Engine = EngineLayout.Inline4,
            // source: Toyota MR2 G-Limited (SW20 1st series, 3S-GE 165 PS/6600, 196 Nm/4800, 5MT 4.312, kerb ~1240 kg); brochure figures; tyre size, 0-100 and top estimated; curve fitted to published peaks
            Mass = 1315f, FrontAxle = 1.368f, RearAxle = 1.032f, CgHeight = 0.46f, Grip = 1.02f,
            PeakKw = 121f, PeakRpm = 6600f, IdleRpm = 800f, Redline = 7500f,
            Gears = new[] { 3.166f, 1.904f, 1.31f, 0.969f, 0.815f }, FinalDrive = 4.312f, Reverse = 3.583f,
            Torque = new (float, float)[] { (1000f, 100f), (2000f, 145f), (3000f, 175f), (4000f, 190f), (4800f, 196f), (5500f, 190f), (6600f, 175f), (7000f, 165f), (7500f, 145f) },
            Tyre = "205/55R15", BrakeDecel = 9.4f, Diff = Differential.Open,
            RefZeroTo100 = 8.4f, RefTopKmh = 215f,
            MaxSteer = 0.58f, DragArea = 0.6f,
            LockTurns = 3.0f, // lock to lock, est.
        },
        new CarSpec
        {
            Label = "ZZW30 MR-S",
            Style = DriveStyle.Grip,
            Blurb = "Toyota MR2 Spyder (MR-S): featherweight open two-seater with the engine behind the seats",
            Body = new CarBody
            {
                Shape = BodyShape.Roadster, Length = 3.89f, Width = 1.695f, Height = 1.24f, WheelRadius = 0.29f,
                Paint = new Color(0.8f, 0.15f, 0.12f), Rim = Silver,
            },
            Engine = EngineLayout.Inline4,
            // source: Toyota MR-S (ZZW30, 1ZZ-FE 140 PS/6400, 171 Nm/4400, 5MT 4.058, 205/50R15 rear, kerb ~970 kg); brochure figures; period tests 0-100 ~8.6 s; curve fitted to published peaks
            Mass = 1045f, FrontAxle = 1.372f, RearAxle = 1.078f, CgHeight = 0.44f, Grip = 1.02f,
            PeakKw = 103f, PeakRpm = 6400f, IdleRpm = 750f, Redline = 7200f,
            Gears = new[] { 3.538f, 2.045f, 1.376f, 1.031f, 0.838f }, FinalDrive = 4.058f, Reverse = 3.583f,
            Torque = new (float, float)[] { (1000f, 100f), (2000f, 140f), (3000f, 160f), (4400f, 171f), (5500f, 165f), (6400f, 153.7f), (7000f, 135f), (7200f, 125f) },
            Tyre = "205/50R15", BrakeDecel = 9.2f, Diff = Differential.Open,
            RefZeroTo100 = 8.6f, RefTopKmh = 200f,
            MaxSteer = 0.6f, DragArea = 0.62f,
            LockTurns = 2.7f, // lock to lock, est.
        },
        new CarSpec
        {
            Label = "JZA80 Supra",
            Blurb = "1990s Toyota Supra RZ: twin-turbo 2JZ straight six famed for taking huge tuning power. More grunt than the road can hold",
            Body = new CarBody
            {
                Shape = BodyShape.Fastback, Length = 4.51f, Width = 1.81f, Height = 1.27f, WheelRadius = 0.33f,
                Paint = Silver, Rim = Silver, Wing = WingSize.Big,
            },
            Engine = EngineLayout.Inline6Turbo,
            // source: Toyota Supra RZ (JZA80, 2JZ-GTE 280 PS/5600, 431 Nm/3600, V160 6MT 3.133, 255/40ZR17 rear, kerb 1510 kg); brochure; period tests 0-100 ~5.1 s; Torsen rear; curve fitted to published peaks
            Mass = 1585f, FrontAxle = 1.1985f, RearAxle = 1.3515f, CgHeight = 0.5f, Grip = 1.08f,
            PeakKw = 206f, PeakRpm = 5600f, IdleRpm = 750f, Redline = 6800f,
            Gears = new[] { 3.827f, 2.36f, 1.685f, 1.312f, 1f, 0.793f }, FinalDrive = 3.133f, Reverse = 3.375f,
            Torque = new (float, float)[] { (1000f, 180f), (2000f, 330f), (3000f, 410f), (3600f, 431f), (4500f, 420f), (5000f, 390f), (5600f, 351.5f), (6000f, 320f), (6800f, 270f) },
            Tyre = "255/40R17", BrakeDecel = 10.1f, Diff = Differential.Torsen,
            RefZeroTo100 = 5.1f, RefTopKmh = 250f,
            MaxSteer = 0.58f, DragArea = 0.68f,
            LockTurns = 2.9f, // lock to lock, est.
        },
        // ---- Mazda and Nissan rivals ----
        new CarSpec
        {
            Label = "NA6CE Roadster",
            Blurb = "1989 Mazda MX-5 (Eunos Roadster), the first one: light, a revvy 1.6, rear drive, pop-up eyes ({lights_toggle}) and a top that folds ({roof_toggle})",
            Body = new CarBody
            {
                Shape = BodyShape.Roadster, Length = 3.95f, Width = 1.675f, Height = 1.235f, WheelRadius = 0.29f,
                Paint = new Color(0.75f, 0.12f, 0.12f), Rim = Silver, PopUps = true,
            },
            Engine = EngineLayout.Inline4,
            // source: Eunos/Mazda Roadster NA6CE (B6-ZE 120 PS/6500, 137 Nm/5500, 5MT 4.100, 185/60R14, kerb 940 kg); brochure; road tests 0-100 ~9.4 s; open diff; curve fitted to published peaks
            Mass = 1015f, FrontAxle = 1.1325f, RearAxle = 1.1325f, CgHeight = 0.46f, Grip = 1f,
            PeakKw = 88f, PeakRpm = 6500f, IdleRpm = 800f, Redline = 7000f,
            Gears = new[] { 3.136f, 1.888f, 1.33f, 1f, 0.814f }, FinalDrive = 4.1f, Reverse = 3.758f,
            Torque = new (float, float)[] { (1000f, 90f), (2000f, 110f), (3000f, 122f), (4000f, 130f), (5500f, 137f), (6500f, 129.7f), (7000f, 118f), (7200f, 112f) },
            Tyre = "185/60R14", BrakeDecel = 9.2f, Diff = Differential.Open,
            RefZeroTo100 = 9.4f, RefTopKmh = 195f,
            MaxSteer = 0.6f, DragArea = 0.58f,
            LockTurns = 2.9f, // lock to lock, published
        },
        new CarSpec
        {
            Label = "NB8C Roadster",
            Blurb = "Second-generation Mazda MX-5 (1998-2005): 1.8, rear drive, cleanly balanced open car",
            Body = new CarBody
            {
                Shape = BodyShape.Roadster, Length = 3.95f, Width = 1.68f, Height = 1.235f, WheelRadius = 0.3f,
                Paint = Silver, Rim = Silver,
            },
            Engine = EngineLayout.Inline4,
            // source: Eunos/Mazda Roadster NB8C (BP-ZE 145 PS/6500, 163 Nm/5000, 5MT 4.100, 185/55R15, kerb ~1010 kg); brochure; tests 0-100 ~8.7 s; Torsen LSD assumed for RS; curve fitted to published peaks
            Mass = 1085f, FrontAxle = 1.1325f, RearAxle = 1.1325f, CgHeight = 0.46f, Grip = 1.03f,
            PeakKw = 107f, PeakRpm = 6500f, IdleRpm = 800f, Redline = 7000f,
            Gears = new[] { 3.136f, 1.888f, 1.33f, 1f, 0.814f }, FinalDrive = 4.1f, Reverse = 3.758f,
            Torque = new (float, float)[] { (1000f, 100f), (2000f, 130f), (3000f, 148f), (4000f, 158f), (5000f, 163f), (6000f, 158f), (6500f, 157.2f), (7000f, 145f), (7200f, 138f) },
            Tyre = "185/55R15", BrakeDecel = 9.4f, Diff = Differential.Torsen,
            RefZeroTo100 = 8.7f, RefTopKmh = 205f,
            MaxSteer = 0.6f, DragArea = 0.6f,
            LockTurns = 2.6f, // lock to lock, est.
        },
        new CarSpec
        {
            Label = "ER34 Skyline",
            Blurb = "Nissan Skyline 25GT Turbo (1998-2002): turbo RB25 straight six and rear drive, a heavy but willing coupe",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.6f, Width = 1.72f, Height = 1.34f, WheelRadius = 0.32f,
                Paint = new Color(0.2f, 0.24f, 0.34f), Rim = Silver,
            },
            Engine = EngineLayout.Inline6Turbo,
            // source: Nissan Skyline 25GT-t coupe (ER34, RB25DET 280 PS/6400, 333 Nm/3200, 5MT 3.937, 205/55R16, kerb ~1310 kg); brochure; tests 0-100 ~6.4 s; viscous LSD; curve fitted to published peaks
            Mass = 1390f, FrontAxle = 1.146f, RearAxle = 1.5191f, CgHeight = 0.5f, Grip = 1.03f,
            PeakKw = 206f, PeakRpm = 6400f, IdleRpm = 800f, Redline = 7000f,
            Gears = new[] { 3.321f, 1.902f, 1.308f, 1f, 0.759f }, FinalDrive = 3.937f, Reverse = 3.382f,
            Torque = new (float, float)[] { (1000f, 150f), (2000f, 260f), (3000f, 325f), (3200f, 333f), (4000f, 330f), (5000f, 320f), (6000f, 310f), (6400f, 307.4f), (6800f, 280f), (7000f, 265f) },
            Tyre = "205/55R16", BrakeDecel = 9.9f, Diff = Differential.Viscous,
            RefZeroTo100 = 6.4f, RefTopKmh = 245f,
            MaxSteer = 0.58f, DragArea = 0.68f,
            LockTurns = 2.9f, // lock to lock, est.
        },
        new CarSpec
        {
            Label = "BNR34",
            Style = DriveStyle.Grip,
            Blurb = "1999 Nissan Skyline GT-R V-spec II: twin-turbo RB26, ATTESA E-TS Pro four-wheel drive, brutally fast on the flat",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.6f, Width = 1.785f, Height = 1.36f, WheelRadius = 0.32f,
                Paint = new Color(0.33f, 0.17f, 0.5f), Rim = Silver, Wing = WingSize.Small,
            },
            Engine = EngineLayout.Inline6Turbo,
            // source: Nissan Skyline GT-R V-spec II (BNR34, RB26DETT 280 PS/6800, 392 Nm/4400, Getrag 6MT 3.545, 235/45R17, kerb 1560 kg, ATTESA E-TS Pro); brochure + Best Motoring; 0-100 ~5.0 s; curve fitted to published peaks
            Mass = 1635f, FrontAxle = 1.1193f, RearAxle = 1.5457f, CgHeight = 0.5f, Grip = 1.08f,
            PeakKw = 206f, PeakRpm = 6800f, IdleRpm = 800f, Redline = 8000f,
            Gears = new[] { 3.827f, 2.36f, 1.685f, 1.312f, 1f, 0.793f }, FinalDrive = 3.545f, Reverse = 3.415f,
            Torque = new (float, float)[] { (1000f, 150f), (2000f, 250f), (3000f, 340f), (4000f, 385f), (4400f, 392f), (5000f, 385f), (6000f, 320f), (6800f, 289.2f), (7500f, 255f), (8000f, 230f) },
            Tyre = "235/45R17", BrakeDecel = 10.5f, Diff = Differential.Torsen,
            RefZeroTo100 = 5f, RefTopKmh = 250f,
            Drive = Drivetrain.All, RearBias = 0.7f,
            MaxSteer = 0.58f, DragArea = 0.7f,
            LockTurns = 2.6f, // lock to lock, est.
        },
        new CarSpec
        {
            Label = "Z33",
            Style = DriveStyle.Grip,
            Blurb = "2002 Nissan 350Z (Fairlady Z): a torquey 3.5 V6 rear-drive coupe, more grip than finesse",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.31f, Width = 1.815f, Height = 1.31f, WheelRadius = 0.33f,
                Paint = new Color(0.85f, 0.45f, 0.1f), Rim = Silver,
            },
            Engine = EngineLayout.V6,
            // source: Nissan Fairlady Z (Z33, VQ35DE 280 PS/6200, 358 Nm/4800, 6MT 3.538, 225/50R17, kerb ~1450 kg); brochure + magazine tests 0-100 ~5.9 s; viscous LSD; curve fitted to published peaks
            Mass = 1525f, FrontAxle = 1.2323f, RearAxle = 1.4177f, CgHeight = 0.5f, Grip = 1.08f,
            PeakKw = 206f, PeakRpm = 6200f, IdleRpm = 700f, Redline = 6600f,
            Gears = new[] { 3.794f, 2.324f, 1.624f, 1.271f, 1f, 0.794f }, FinalDrive = 3.538f, Reverse = 3.382f,
            Torque = new (float, float)[] { (1000f, 215f), (2000f, 270f), (3000f, 320f), (4000f, 350f), (4800f, 358f), (5500f, 340f), (6200f, 317.2f), (6600f, 290f), (7000f, 260f) },
            Tyre = "225/50R17", BrakeDecel = 9.8f, Diff = Differential.Viscous,
            RefZeroTo100 = 5.9f, RefTopKmh = 250f,
            MaxSteer = 0.58f, DragArea = 0.66f,
            LockTurns = 2.6f, // lock to lock, est.
        },
        // ---- MF Ghost ----
        new CarSpec
        {
            Label = "ZN6",
            Blurb = "2012 Toyota GT86: the AE86's modern heir with a revvy flat-four, light, low and rear drive",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.24f, Width = 1.775f, Height = 1.285f, WheelRadius = 0.32f,
                Paint = White, Lower = Black, Rim = Silver,
            },
            // no naturally-aspirated boxer in EngineLayout, so it borrows the boxer voice
            Engine = EngineLayout.Boxer4Turbo,
            // source: Toyota GT86 (ZN6, 2012: FA20 200 PS/7000, 205 Nm/6600, 6MT 4.100, 205/55R16, kerb ~1240 kg, Torsen LSD); Toyota brochure (0-100 7.6 s claimed, 226 km/h)
            Mass = 1315f, FrontAxle = 1.2079f, RearAxle = 1.3621f, CgHeight = 0.46f, Grip = 1.03f,
            PeakKw = 147f, PeakRpm = 7000f, IdleRpm = 700f, Redline = 7450f,
            Gears = new[] { 3.626f, 2.188f, 1.541f, 1.213f, 1f, 0.767f }, FinalDrive = 4.1f, Reverse = 3.437f,
            Torque = new (float, float)[] { (1000f, 120f), (2000f, 130f), (3000f, 150f), (3600f, 145f), (4000f, 150f), (5000f, 180f), (6000f, 203f), (6600f, 205f), (7000f, 200.6f), (7450f, 175f) },
            Tyre = "205/55R16", BrakeDecel = 10.1f, Diff = Differential.Torsen,
            RefZeroTo100 = 7.6f, RefTopKmh = 226f,
            MaxSteer = 0.6f, DragArea = 0.6f,
            LockTurns = 2.5f, // lock to lock, est.
        },
        // ---- 34: everyday Toyotas (#464) ----
        new CarSpec
        {
            Label = "XP90 Yaris",
            Style = DriveStyle.Grip,
            Blurb = "2005 Toyota Yaris 1.3: a tall, light front-drive city hatch, and this one has lowrider hydraulics. {roof_toggle} makes it bounce",
            Body = new CarBody
            {
                Shape = BodyShape.Hatchback, Length = 3.75f, Width = 1.695f, Height = 1.53f, WheelRadius = 0.29f,
                Paint = new Color(0.62f, 0.78f, 0.9f), Rim = Silver, Hydraulics = true,
            },
            Engine = EngineLayout.Inline4,
            // source: Toyota Yaris 1.3 VVT-i 3-door (XP90, 2SZ-FE 87 PS/6000, 121 Nm/4400, 5MT, 175/65R14, kerb ~1030 kg); Toyota claims 0-100 11.5 s, 170 km/h; ratios and final drive est.; curve fitted to published peaks
            // Grip 1 rather than an eco tyre's ~0.9: at 0.9 neither car held the Game drift --driftcheck asks of every car
            Mass = 1105f, FrontAxle = 0.935f, RearAxle = 1.525f, CgHeight = 0.56f, Grip = 1f,
            // redline at the fuel cut: at 6,500 the 1-2 upshift fell under the downshift point and hunted
            PeakKw = 64f, PeakRpm = 6000f, IdleRpm = 750f, Redline = 6800f,
            Gears = new[] { 3.545f, 1.904f, 1.31f, 1.027f, 0.815f }, FinalDrive = 4.312f, Reverse = 3.25f,
            Torque = new (float, float)[] { (1000f, 95f), (2000f, 108f), (3000f, 114f), (4000f, 119f), (4400f, 121f), (5000f, 118f), (6000f, 101.9f), (6500f, 92f), (6800f, 85f) },
            Tyre = "175/65R14", BrakeDecel = 9f, Diff = Differential.Open,
            RefZeroTo100 = 11.5f, RefTopKmh = 170f,
            Drive = Drivetrain.Front,
            MaxSteer = 0.62f, DragArea = 0.66f,
            LockTurns = 3.1f, // lock to lock, est.: electric power steering
        },
        new CarSpec
        {
            Label = "XW20 Prius",
            Style = DriveStyle.Grip,
            Blurb = "2004 Toyota Prius: the hybrid that made hybrids normal, a 1.5 Atkinson four and an electric motor through a planetary e-CVT. Quiet, smooth, front drive",
            Body = new CarBody
            {
                // the kamm-tailed liftback is nearest the raked fastback
                Shape = BodyShape.Fastback, Length = 4.45f, Width = 1.725f, Height = 1.49f, WheelRadius = 0.31f,
                Paint = new Color(0.55f, 0.6f, 0.58f), Rim = Silver,
            },
            Engine = EngineLayout.Inline4,
            // source: Toyota Prius (XW20, 1NZ-FXE 78 PS/5000 + 50 kW motor 400 Nm, system 110 PS, e-CVT, 185/65R15, kerb ~1300 kg); Toyota claims 0-100 10.9 s, 170 km/h. The e-CVT is approximated by five close ratios (no CVT in the car model), the torque curve is the system's at the crank: the motor's pull low down, the engine's on top
            Mass = 1375f, FrontAxle = 1.08f, RearAxle = 1.62f, CgHeight = 0.55f, Grip = 1f,
            PeakKw = 82f, PeakRpm = 5000f, IdleRpm = 1000f, Redline = 5600f,
            Gears = new[] { 3.1f, 1.95f, 1.4f, 1.08f, 0.86f }, FinalDrive = 4.113f, Reverse = 3.1f,
            Torque = new (float, float)[] { (1000f, 200f), (2000f, 190f), (3000f, 175f), (4000f, 165f), (5000f, 156.6f), (5600f, 140f) },
            Tyre = "185/65R15", BrakeDecel = 9.1f, Diff = Differential.Open,
            RefZeroTo100 = 10.9f, RefTopKmh = 170f,
            Drive = Drivetrain.Front,
            MaxSteer = 0.6f, DragArea = 0.57f,
            LockTurns = 3.3f, // lock to lock, est.: electric power steering
        },
        // ---- 36: the rental go-kart (#715) ----
        new CarSpec
        {
            Label = "Rental kart",
            Style = DriveStyle.Grip,
            Blurb = "A 270 cc rental kart: 9 hp on a bare tube frame, a solid rear axle and no suspension, 65 km/h with your backside on the road. "
                + "It grips hard and is twitchy at the limit; slide it sideways and it will tip. {throttle} gas, {brake} brake",
            Body = new CarBody
            {
                // the colour and number are the rider's (KartMeshBuilder.Dress); this is the hall's first kart
                Shape = BodyShape.Kart, Length = 1.8f, Width = 1.38f, Height = 0.95f, WheelRadius = 0.136f,
                Paint = new Color(0.88f, 0.07f, 0.08f), Rim = Silver, Number = 7,
            },
            Engine = EngineLayout.Single,
            // source: Honda GX270 on a rental kart (OTK / Sodikart class): 270 cc, 6.6 kW (9 hp) gross at 3,600, 19.1 N·m at 2,500, a governor that cuts it at ~3,900 rpm and a
            // centrifugal clutch that holds the engine at 2,000 when it slips (the idle here); frame 75 kg + driver 90 kg, wheelbase 1.05 m, track 1.2 m, rear 11x7.10-5 (140/55R5 is the nearest to
            // its 0.136 m rolling radius); one fixed ratio, fitted so the governed engine gives 65 km/h; the CG is the driver's torso over a 0.12 m frame; rear-heavy (58%)
            Mass = 165f, FrontAxle = 0.609f, RearAxle = 0.441f, CgHeight = 0.26f, Grip = 1.15f,
            PeakKw = 6.6f, PeakRpm = 3600f, IdleRpm = 2000f, Redline = 3900f,
            Gears = new[] { 1f }, FinalDrive = 3f, Reverse = 3f,
            Torque = new (float, float)[] { (1400f, 13f), (2000f, 17f), (2500f, 19.1f), (3000f, 18.7f), (3600f, 17.5f), (3800f, 10f), (3900f, 2f) },
            Tyre = "140/55R5", BrakeDecel = 8.5f,
            // a solid axle: both rear wheels turn together, which is a locked diff
            Diff = Differential.Mechanical,
            RefZeroTo100 = 0f, RefTopKmh = 65f,
            // the lock is a kart's, 26°, on a wheel that goes about half a turn lock to lock; a bare steering box
            MaxSteer = 0.46f, DragArea = 0.5f,
            LockTurns = 0.55f,
            PowerSteering = false,
            // no suspension: the tyres' sidewalls are all the travel it has, and every bump goes straight into the grip
            TyreType = KartTyre, Travel = 0.06f, Stiffness = 3f,
        },
    });

    /// <summary>The spec for a car kind, or null when the kind is not a car.</summary>
    public static CarSpec? For(RideKind kind)
    {
        int i = (int)kind - First;
        return i >= 0 && i < All.Count ? All[i] : null;
    }

    /// <summary>The rental go-kart (#715), <see cref="RideKind"/> 36.</summary>
    public static CarSpec Kart => _kart ??= All.First(c => c.Body.Shape == BodyShape.Kart);
    private static CarSpec? _kart;

    public static bool IsCar(RideKind kind) => (int)kind >= First && (int)kind <= Last;

    private static IReadOnlyList<CarSpec> Number(CarSpec[] cars)
    {
        if (cars.Length > Last - First + 1)
            throw new System.InvalidOperationException($"{cars.Length} cars overflow RideKind {First}..{Last}");
        for (int i = 0; i < cars.Length; i++) cars[i] = cars[i] with { Kind = (RideKind)(First + i) };
        return cars;
    }
}
