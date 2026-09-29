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
        // Sources: series character/car lists (GTPlanet "Initial D cars" thread, Initial D wiki extracts),
        // real-world figures from factory/period road-test specs of each model; liveries approximate the series.
        // ---- 11: Project D / Akina / Akagi heroes ----
        new CarSpec
        {
            Label = "FC3S",
            Blurb = "Ryosuke Takahashi's white RX-7: the White Comet, a precise turbo rotary tuned for hill climbs",
            Body = new CarBody
            {
                Shape = BodyShape.Fastback, Length = 4.3f, Width = 1.69f, Height = 1.27f, WheelRadius = 0.31f,
                Paint = White, Rim = Silver, PopUps = true, Wing = WingSize.Lip,
            },
            Engine = EngineLayout.RotaryTurbo,
            // source: Savanna RX-7 Infini III factory specs (13B-T, 215 PS, 5MT, 4.10 final)
            Mass = 1345f, FrontAxle = 1.17f, RearAxle = 1.26f, CgHeight = 0.47f, Grip = 1.02f,
            PeakKw = 158f, PeakRpm = 6500f, IdleRpm = 800f, Redline = 7500f,
            Gears = new[] { 3.483f, 2.015f, 1.391f, 1.000f, 0.719f }, FinalDrive = 4.1f,
            MaxSteer = 0.6f, DragArea = 0.58f,
        },
        new CarSpec
        {
            Label = "BNR32",
            Style = DriveStyle.Grip,
            Blurb = "Takeshi Nakazato's Skyline GT-R: the Night Kid's twin-turbo six with AWD grip, heavy in the corners",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.54f, Width = 1.755f, Height = 1.34f, WheelRadius = 0.316f,
                Paint = new Color(0.33f, 0.34f, 0.38f), Rim = Silver, Wing = WingSize.Small,
            },
            Engine = EngineLayout.Inline6Turbo,
            // source: R32 GT-R V-spec II factory specs (RB26DETT, 280 PS, ATTESA E-TS)
            Mass = 1505f, FrontAxle = 1.07f, RearAxle = 1.545f, CgHeight = 0.5f, Grip = 1.05f,
            PeakKw = 206f, PeakRpm = 6800f, IdleRpm = 800f, Redline = 8000f,
            Gears = new[] { 3.214f, 1.925f, 1.302f, 1.000f, 0.752f }, FinalDrive = 4.111f,
            Drive = Drivetrain.All, RearBias = 0.7f,
            MaxSteer = 0.58f, DragArea = 0.72f,
        },
        new CarSpec
        {
            Label = "EG6 hatch",
            Style = DriveStyle.Grip,
            Blurb = "Shingo Shoji's Civic SiR-II: a front-drive VTEC screamer that wins on the climb",
            Body = new CarBody
            {
                Shape = BodyShape.Hatchback, Length = 4.0f, Width = 1.695f, Height = 1.34f, WheelRadius = 0.29f,
                Paint = White, Rim = Silver,
            },
            Engine = EngineLayout.Inline4,
            // source: EG6 Civic SiR-II factory specs (B16A, 160 PS @ 7600, 5MT)
            Mass = 1115f, FrontAxle = 1.0f, RearAxle = 1.62f, CgHeight = 0.48f, Grip = 1.0f,
            PeakKw = 118f, PeakRpm = 7600f, IdleRpm = 800f, Redline = 8200f,
            Gears = new[] { 3.230f, 2.105f, 1.458f, 1.107f, 0.848f }, FinalDrive = 4.4f,
            Drive = Drivetrain.Front,
            MaxSteer = 0.6f, DragArea = 0.6f,
        },
        new CarSpec
        {
            Label = "S13 Silvia",
            Blurb = "Koichiro Iketani's turbo coupe: the Akina Speed Stars' old-school drifter",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.44f, Width = 1.69f, Height = 1.29f, WheelRadius = 0.3f,
                Paint = White, Rim = Silver, PopUps = true,
            },
            Engine = EngineLayout.Inline4Turbo,
            // source: S13 Silvia K's factory specs (CA18DET, 175 PS)
            Mass = 1225f, FrontAxle = 1.09f, RearAxle = 1.385f, CgHeight = 0.48f, Grip = 1.0f,
            PeakKw = 129f, PeakRpm = 6400f, IdleRpm = 850f, Redline = 7000f,
            Gears = new[] { 3.321f, 1.902f, 1.308f, 1.000f, 0.759f }, FinalDrive = 4.083f,
            MaxSteer = 0.62f, DragArea = 0.62f,
        },
        new CarSpec
        {
            Label = "RPS13 180SX",
            Blurb = "Kenji's One-Eighty: a turbo SR20 fastback that drifts on the touge, a rival's dream",
            Body = new CarBody
            {
                Shape = BodyShape.Fastback, Length = 4.5f, Width = 1.69f, Height = 1.29f, WheelRadius = 0.31f,
                Paint = new Color(0.85f, 0.85f, 0.87f), Rim = Silver, PopUps = true,
            },
            Engine = EngineLayout.Inline4Turbo,
            // source: RPS13 180SX Type X factory specs (SR20DET, 205 PS)
            Mass = 1315f, FrontAxle = 1.1f, RearAxle = 1.375f, CgHeight = 0.48f, Grip = 1.0f,
            PeakKw = 151f, PeakRpm = 6000f, IdleRpm = 850f, Redline = 7200f,
            Gears = new[] { 3.321f, 1.902f, 1.308f, 1.000f, 0.759f }, FinalDrive = 4.363f,
            MaxSteer = 0.62f, DragArea = 0.62f,
        },
        new CarSpec
        {
            Label = "PS13 Sileighty",
            Blurb = "Mako Sato's Impact Blue: a 180SX with a Silvia face, a wild rear-drive turbo that leaves no time to think",
            Body = new CarBody
            {
                Shape = BodyShape.Fastback, Length = 4.5f, Width = 1.69f, Height = 1.29f, WheelRadius = 0.31f,
                Paint = new Color(0.14f, 0.3f, 0.72f), Rim = Silver,
            },
            Engine = EngineLayout.Inline4Turbo,
            // source: SR20DET Sileighty conversion (180SX body, S13 front), 205 PS
            Mass = 1315f, FrontAxle = 1.1f, RearAxle = 1.375f, CgHeight = 0.48f, Grip = 1.02f,
            PeakKw = 151f, PeakRpm = 6000f, IdleRpm = 850f, Redline = 7200f,
            Gears = new[] { 3.321f, 1.902f, 1.308f, 1.000f, 0.759f }, FinalDrive = 4.363f,
            MaxSteer = 0.62f, DragArea = 0.62f,
        },
        new CarSpec
        {
            Label = "AE85 Levin",
            Blurb = "Itsuki Takeuchi's little brother of the AE86: a 1.5 litre that is all about learning to drive",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.2f, Width = 1.63f, Height = 1.31f, WheelRadius = 0.28f,
                Paint = new Color(0.6f, 0.7f, 0.82f), Rim = Silver, PopUps = true,
            },
            Engine = EngineLayout.Inline4,
            // source: AE85 Levin 1.5 factory specs (3A-U, 83 PS)
            Mass = 970f, FrontAxle = 1.1f, RearAxle = 1.3f, CgHeight = 0.5f, Grip = 0.96f,
            PeakKw = 61f, PeakRpm = 6000f, IdleRpm = 800f, Redline = 6500f,
            Gears = new[] { 3.587f, 2.022f, 1.384f, 1.000f, 0.861f }, FinalDrive = 4.1f,
            MaxSteer = 0.62f, DragArea = 0.62f,
        },
        new CarSpec
        {
            Label = "AE86 coupe",
            Blurb = "Two-door Trueno of Akina's rivals (Wataru Akiyama, Shinji Inui): same 4A-GE as the tofu hatch",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.2f, Width = 1.63f, Height = 1.31f, WheelRadius = 0.29f,
                Paint = new Color(0.75f, 0.76f, 0.79f), Lower = Black, Rim = Silver, PopUps = true,
            },
            Engine = EngineLayout.Inline4,
            // source: AE86 Sprinter Trueno GT-APEX factory specs (4A-GE, 130 PS)
            Mass = 1000f, FrontAxle = 1.12f, RearAxle = 1.28f, CgHeight = 0.5f, Grip = 1.0f,
            PeakKw = 96f, PeakRpm = 6600f, IdleRpm = 900f, Redline = 7800f,
            Gears = new[] { 3.59f, 2.02f, 1.38f, 1.00f, 0.86f }, FinalDrive = 4.3f,
            MaxSteer = 0.62f, DragArea = 0.62f,
        },
        // ---- Lancer Evolutions (Team Emperor and later rivals) ----
        new CarSpec
        {
            Label = "CE9A Evo III",
            Style = DriveStyle.Grip,
            Blurb = "Kyoichi Sudo's Emperor Evo: turbo four-wheel drive that pulls out of every corner",
            Body = new CarBody
            {
                Shape = BodyShape.Sedan, Length = 4.31f, Width = 1.69f, Height = 1.42f, WheelRadius = 0.31f,
                Paint = White, Rim = new Color(0.85f, 0.7f, 0.2f), Wing = WingSize.Big, Scoop = true,
            },
            Engine = EngineLayout.Inline4Turbo,
            // source: Lancer Evolution III GSR factory specs (4G63, 270 PS)
            Mass = 1325f, FrontAxle = 1.0f, RearAxle = 1.506f, CgHeight = 0.52f, Grip = 1.05f,
            PeakKw = 199f, PeakRpm = 6250f, IdleRpm = 850f, Redline = 7500f,
            Gears = new[] { 2.785f, 1.950f, 1.407f, 1.031f, 0.731f }, FinalDrive = 4.529f,
            Drive = Drivetrain.All, RearBias = 0.5f,
            MaxSteer = 0.58f, DragArea = 0.68f,
        },
        new CarSpec
        {
            Label = "CN9A Evo IV",
            Style = DriveStyle.Grip,
            Blurb = "Seiji Iwaki's Evo IV: active yaw control turns the AWD saloon into a scalpel",
            Body = new CarBody
            {
                Shape = BodyShape.Sedan, Length = 4.33f, Width = 1.7f, Height = 1.42f, WheelRadius = 0.315f,
                Paint = Silver, Rim = new Color(0.85f, 0.7f, 0.2f), Wing = WingSize.Big, Scoop = true,
            },
            Engine = EngineLayout.Inline4Turbo,
            // source: Lancer Evolution IV GSR factory specs (4G63, 280 PS, AYC)
            Mass = 1425f, FrontAxle = 1.0f, RearAxle = 1.5f, CgHeight = 0.52f, Grip = 1.06f,
            PeakKw = 206f, PeakRpm = 6500f, IdleRpm = 850f, Redline = 7500f,
            Gears = new[] { 2.785f, 1.950f, 1.407f, 1.031f, 0.720f }, FinalDrive = 4.529f,
            Drive = Drivetrain.All, RearBias = 0.55f,
            MaxSteer = 0.58f, DragArea = 0.68f,
        },
        new CarSpec
        {
            Label = "CT9A Evo VII",
            Style = DriveStyle.Grip,
            Blurb = "The Evo VII GSR that meets Project D in Final Stage: active centre diff, the last of the iron-block era",
            Body = new CarBody
            {
                Shape = BodyShape.Sedan, Length = 4.35f, Width = 1.7f, Height = 1.45f, WheelRadius = 0.32f,
                Paint = new Color(0.16f, 0.16f, 0.2f), Rim = Silver, Wing = WingSize.Big, Scoop = true,
            },
            Engine = EngineLayout.Inline4Turbo,
            // source: Lancer Evolution VII GSR factory specs (4G63, 280 PS, ACD)
            Mass = 1455f, FrontAxle = 1.0f, RearAxle = 1.5f, CgHeight = 0.52f, Grip = 1.07f,
            PeakKw = 206f, PeakRpm = 6500f, IdleRpm = 850f, Redline = 7500f,
            Gears = new[] { 2.785f, 1.950f, 1.407f, 1.031f, 0.720f }, FinalDrive = 4.529f,
            Drive = Drivetrain.All, RearBias = 0.55f,
            MaxSteer = 0.58f, DragArea = 0.68f,
        },
        // ---- Honda ----
        new CarSpec
        {
            Label = "NA1 NSX",
            Style = DriveStyle.Grip,
            Blurb = "Go Hojo's Sidewinder NSX: mid-engined V6, the overwhelming car of Project D's Kanagawa run",
            Body = new CarBody
            {
                Shape = BodyShape.Midship, Length = 4.43f, Width = 1.81f, Height = 1.17f, WheelRadius = 0.32f,
                Paint = White, Rim = Silver, PopUps = true, Wing = WingSize.Lip,
            },
            Engine = EngineLayout.V6,
            // source: NSX Type R factory specs (C30A, 280 PS, 5MT)
            Mass = 1425f, FrontAxle = 1.47f, RearAxle = 1.06f, CgHeight = 0.46f, Grip = 1.06f,
            PeakKw = 206f, PeakRpm = 7300f, IdleRpm = 850f, Redline = 8000f,
            Gears = new[] { 3.230f, 2.105f, 1.458f, 1.107f, 0.848f }, FinalDrive = 4.062f,
            MaxSteer = 0.58f, DragArea = 0.62f,
        },
        new CarSpec
        {
            Label = "EK9 Type R",
            Style = DriveStyle.Grip,
            Blurb = "A yellow Civic Type R: high-revving front drive that punishes any mistake in a hairpin",
            Body = new CarBody
            {
                Shape = BodyShape.Hatchback, Length = 4.1f, Width = 1.695f, Height = 1.35f, WheelRadius = 0.3f,
                Paint = new Color(0.98f, 0.8f, 0.1f), Rim = Silver,
            },
            Engine = EngineLayout.Inline4,
            // source: EK9 Civic Type R factory specs (B16B, 185 PS @ 8200)
            Mass = 1125f, FrontAxle = 1.05f, RearAxle = 1.57f, CgHeight = 0.48f, Grip = 1.02f,
            PeakKw = 136f, PeakRpm = 8200f, IdleRpm = 800f, Redline = 8600f,
            Gears = new[] { 3.230f, 2.105f, 1.458f, 1.107f, 0.848f }, FinalDrive = 4.4f,
            Drive = Drivetrain.Front,
            MaxSteer = 0.6f, DragArea = 0.6f,
        },
        new CarSpec
        {
            Label = "DC2 Type R",
            Style = DriveStyle.Grip,
            Blurb = "A white Integra Type R (turbocharged in the series): the front-drive coupe with the sharpest nose",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.38f, Width = 1.695f, Height = 1.32f, WheelRadius = 0.3f,
                Paint = White, Rim = Silver, Wing = WingSize.Small,
            },
            Engine = EngineLayout.Inline4,
            // source: DC2 Integra Type R factory specs (B18C, 200 PS @ 8000); the series' turbo is not modelled
            Mass = 1155f, FrontAxle = 1.05f, RearAxle = 1.57f, CgHeight = 0.48f, Grip = 1.02f,
            PeakKw = 147f, PeakRpm = 8000f, IdleRpm = 800f, Redline = 8600f,
            Gears = new[] { 3.230f, 2.105f, 1.458f, 1.107f, 0.848f }, FinalDrive = 4.4f,
            Drive = Drivetrain.Front,
            MaxSteer = 0.6f, DragArea = 0.62f,
        },
        // ---- Toyota ----
        new CarSpec
        {
            Label = "SW20 MR2",
            Style = DriveStyle.Grip,
            Blurb = "Kai Kogashiwa's MR2: mid-engine balance, sharp until it snaps",
            Body = new CarBody
            {
                Shape = BodyShape.Midship, Length = 4.17f, Width = 1.7f, Height = 1.24f, WheelRadius = 0.3f,
                Paint = White, Rim = Silver, PopUps = true,
            },
            Engine = EngineLayout.Inline4,
            // source: MR2 G-Limited factory specs (3S-GE, ~165 PS)
            Mass = 1335f, FrontAxle = 1.34f, RearAxle = 1.06f, CgHeight = 0.46f, Grip = 1.02f,
            PeakKw = 121f, PeakRpm = 6800f, IdleRpm = 800f, Redline = 7500f,
            Gears = new[] { 3.166f, 1.904f, 1.310f, 0.969f, 0.815f }, FinalDrive = 4.312f,
            MaxSteer = 0.58f, DragArea = 0.6f,
        },
        new CarSpec
        {
            Label = "ZZW30 MR-S",
            Style = DriveStyle.Grip,
            Blurb = "Kai Kogashiwa's open two-seater in Kanagawa: featherweight and mid-engined",
            Body = new CarBody
            {
                Shape = BodyShape.Roadster, Length = 3.89f, Width = 1.695f, Height = 1.24f, WheelRadius = 0.29f,
                Paint = new Color(0.8f, 0.15f, 0.12f), Rim = Silver,
            },
            Engine = EngineLayout.Inline4,
            // source: MR-S factory specs (1ZZ-FE, 140 PS, 5MT)
            Mass = 1045f, FrontAxle = 1.37f, RearAxle = 1.08f, CgHeight = 0.44f, Grip = 1.0f,
            PeakKw = 103f, PeakRpm = 6400f, IdleRpm = 750f, Redline = 7000f,
            Gears = new[] { 3.538f, 2.045f, 1.376f, 1.031f, 0.838f }, FinalDrive = 4.058f,
            MaxSteer = 0.6f, DragArea = 0.58f,
        },
        new CarSpec
        {
            Label = "JZA80 Supra",
            Blurb = "Minagawa's Supra RZ in Kanagawa: a twin-turbo 2JZ with more power than the road can hold",
            Body = new CarBody
            {
                Shape = BodyShape.Fastback, Length = 4.51f, Width = 1.81f, Height = 1.27f, WheelRadius = 0.33f,
                Paint = Silver, Rim = Silver, Wing = WingSize.Big,
            },
            Engine = EngineLayout.Inline6Turbo,
            // source: Supra RZ factory specs (2JZ-GTE, 280 PS, 6MT)
            Mass = 1585f, FrontAxle = 1.2f, RearAxle = 1.35f, CgHeight = 0.5f, Grip = 1.08f,
            PeakKw = 206f, PeakRpm = 5600f, IdleRpm = 750f, Redline = 6800f,
            Gears = new[] { 3.827f, 2.360f, 1.685f, 1.312f, 1.000f, 0.793f }, FinalDrive = 3.133f,
            MaxSteer = 0.58f, DragArea = 0.68f,
        },
        // ---- Mazda and Nissan rivals ----
        new CarSpec
        {
            Label = "NA6CE Roadster",
            Blurb = "Tohru's Roadster of Seven Star Leaf: light, pop-up eyes and a tiny 1.6",
            Body = new CarBody
            {
                Shape = BodyShape.Roadster, Length = 3.95f, Width = 1.675f, Height = 1.235f, WheelRadius = 0.29f,
                Paint = new Color(0.75f, 0.12f, 0.12f), Rim = Silver, PopUps = true,
            },
            Engine = EngineLayout.Inline4,
            // source: NA6CE Roadster factory specs (B6-ZE, 120 PS, 5MT)
            Mass = 1015f, FrontAxle = 1.13f, RearAxle = 1.135f, CgHeight = 0.46f, Grip = 1.0f,
            PeakKw = 88f, PeakRpm = 6500f, IdleRpm = 800f, Redline = 7000f,
            Gears = new[] { 3.136f, 1.888f, 1.330f, 1.000f, 0.814f }, FinalDrive = 4.1f,
            MaxSteer = 0.6f, DragArea = 0.58f,
        },
        new CarSpec
        {
            Label = "NB8C Roadster",
            Blurb = "Satoshi Omiya's Roadster of Team 246: the second-generation open car, cleanly balanced",
            Body = new CarBody
            {
                Shape = BodyShape.Roadster, Length = 3.95f, Width = 1.68f, Height = 1.235f, WheelRadius = 0.3f,
                Paint = Silver, Rim = Silver,
            },
            Engine = EngineLayout.Inline4,
            // source: NB8C Roadster RS factory specs (BP-ZE, 145 PS, 5MT)
            Mass = 1075f, FrontAxle = 1.13f, RearAxle = 1.135f, CgHeight = 0.46f, Grip = 1.0f,
            PeakKw = 107f, PeakRpm = 6500f, IdleRpm = 800f, Redline = 7000f,
            Gears = new[] { 3.136f, 1.888f, 1.330f, 1.000f, 0.814f }, FinalDrive = 4.1f,
            MaxSteer = 0.6f, DragArea = 0.58f,
        },
        new CarSpec
        {
            Label = "ER34 Skyline",
            Blurb = "Atsuro Kawai's 25GT Turbo coupe: a rear-drive six that Seven Star Leaf uses as a heavy hammer",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.6f, Width = 1.72f, Height = 1.34f, WheelRadius = 0.32f,
                Paint = new Color(0.2f, 0.24f, 0.34f), Rim = Silver,
            },
            Engine = EngineLayout.Inline6Turbo,
            // source: ER34 Skyline 25GT Turbo factory specs (RB25DET, 280 PS, 5MT)
            Mass = 1425f, FrontAxle = 1.2f, RearAxle = 1.465f, CgHeight = 0.5f, Grip = 1.02f,
            PeakKw = 206f, PeakRpm = 6400f, IdleRpm = 800f, Redline = 7000f,
            Gears = new[] { 3.321f, 1.902f, 1.308f, 1.000f, 0.759f }, FinalDrive = 3.937f,
            MaxSteer = 0.58f, DragArea = 0.68f,
        },
        new CarSpec
        {
            Label = "BNR34",
            Style = DriveStyle.Grip,
            Blurb = "Kozo Hoshino's God Foot R34 GT-R of Purple Shadow: AWD, twin turbo, brutally fast on the flat",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.6f, Width = 1.785f, Height = 1.36f, WheelRadius = 0.32f,
                Paint = new Color(0.33f, 0.17f, 0.5f), Rim = Silver, Wing = WingSize.Small,
            },
            Engine = EngineLayout.Inline6Turbo,
            // source: BNR34 GT-R V-spec II factory specs (RB26DETT, 280 PS, 6MT, ATTESA E-TS Pro)
            Mass = 1635f, FrontAxle = 1.146f, RearAxle = 1.519f, CgHeight = 0.5f, Grip = 1.07f,
            PeakKw = 206f, PeakRpm = 6800f, IdleRpm = 800f, Redline = 8000f,
            Gears = new[] { 3.827f, 2.360f, 1.685f, 1.312f, 1.000f, 0.793f }, FinalDrive = 3.545f,
            Drive = Drivetrain.All, RearBias = 0.7f,
            MaxSteer = 0.58f, DragArea = 0.7f,
        },
        new CarSpec
        {
            Label = "Z33",
            Style = DriveStyle.Grip,
            Blurb = "The Fairlady Z from Final Stage's line-up: a torquey V6 coupe, more grip than finesse",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.31f, Width = 1.815f, Height = 1.31f, WheelRadius = 0.33f,
                Paint = new Color(0.85f, 0.45f, 0.1f), Rim = Silver,
            },
            Engine = EngineLayout.V6,
            // source: Z33 350Z factory specs (VQ35DE, 280 PS, 6MT)
            Mass = 1525f, FrontAxle = 1.245f, RearAxle = 1.405f, CgHeight = 0.5f, Grip = 1.08f,
            PeakKw = 206f, PeakRpm = 6200f, IdleRpm = 700f, Redline = 6600f,
            Gears = new[] { 3.794f, 2.324f, 1.624f, 1.271f, 1.000f, 0.794f }, FinalDrive = 3.538f,
            MaxSteer = 0.58f, DragArea = 0.66f,
        },
        // ---- MF Ghost ----
        new CarSpec
        {
            Label = "ZN6",
            Blurb = "The GT86, the AE86's modern heir and Kanata Rivington's car in MF Ghost: light, low and rear drive",
            Body = new CarBody
            {
                Shape = BodyShape.Coupe, Length = 4.24f, Width = 1.775f, Height = 1.285f, WheelRadius = 0.32f,
                Paint = White, Lower = Black, Rim = Silver,
            },
            // no naturally-aspirated boxer in EngineLayout, so it borrows the boxer voice
            Engine = EngineLayout.Boxer4Turbo,
            // source: Toyota GT86 (ZN6, 2012) factory specs (FA20, 200 PS @ 7000, 6MT)
            Mass = 1315f, FrontAxle = 1.208f, RearAxle = 1.362f, CgHeight = 0.46f, Grip = 1.05f,
            PeakKw = 147f, PeakRpm = 7000f, IdleRpm = 700f, Redline = 7450f,
            Gears = new[] { 3.626f, 2.188f, 1.541f, 1.213f, 1.000f, 0.767f }, FinalDrive = 4.1f,
            MaxSteer = 0.6f, DragArea = 0.6f,
        },
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
