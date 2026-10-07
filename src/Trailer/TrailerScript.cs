using System.Collections.Generic;
using System.Linq;
using Godot;
using UnitSport.Player;

namespace UnitSport.Trailer;

/// <summary>
/// The trailer's shots (#706), in film order: the storyboard (<c>docs/trailer/storyboard.md</c>)
/// as data. Places are LV95; a spot's height is above the ground unless made with <see cref="Spot.Alt"/>.
/// Bearings are compass degrees (0 north, 90 east). Actor-relative camera points are (right, up,
/// back) in the actor's travel frame; road points are (arc along it, right of it, up).
/// Places were read off overhead stills (<c>--trailer-scout</c>), timings off <c>--trailer-log</c>.
/// </summary>
public static class TrailerScript
{
    internal static RideKind Car(int i) => (RideKind)(CarCatalog.First + i);
    internal static RideKind Moto(int i) => MotorbikeCatalog.All[i].Kind;
    internal static RideKind Heavy(int i) => (RideKind)(HeavyCatalog.First + i);

    // cars by CarCatalog index
    internal const int Ae86 = 0, Fd3s = 1, Gc8 = 2, Bnr32 = 4, S13 = 6, Evo3 = 11, Nsx = 14, Supra = 19, Yaris = 26;
    // motorbikes by MotorbikeCatalog index
    internal const int R1 = 0, Monster = 1, AfricaTwin = 11;
    // heavies by HeavyCatalog index; trailers by TrailerCatalog index
    internal const int Scania = 0, Setra = 4, Curtainsider = 0;
    // emotes: DanceId = 2 + EmoteTable index (HumanMeshBuilder.EmoteName)
    internal const int Wave = 2, Cheer = 3, Salute = 4, Clap = 6, FistPump = 8, ArmWave = 11, Floss = 12, Macarena = 13,
        Ymca = 14, Chicken = 15, Cabbage = 16, Robot = 18, Griddy = 20, Carlton = 21, Gangnam = 22, Orange = 23, RunningMan = 24;

    /// <summary>Scripted flight input: the stick (x right; y forward = dive, back = climb), climb power, thrust lever.</summary>
    internal static FlightInput Stick(float x = 0f, float y = 0f, float up = 0f, float lever = 0f, bool effort = false) =>
        new(new Vector2(x, y), up, 0f, Mathf.Max(0f, lever), Mathf.Max(0f, -lever), false, effort, 0f);

    internal static RideInput Pedal(float throttle, float steer = 0f, float brake = 0f) => new(throttle, brake, steer, false);

    /// <summary>
    /// Keys orbiting a world point on the ground: the eye from bearing <paramref name="from"/> to
    /// <paramref name="to"/> (where it stands, seen from the point), <paramref name="r0"/> to
    /// <paramref name="r1"/> m out, <paramref name="h0"/> to <paramref name="h1"/> m up.
    /// </summary>
    internal static Key[] Orbit(Spot c, float r0, float r1, float h0, float h1, float from, float to, double seconds,
        float lens, float lookUp = 1.5f, int steps = 4) =>
        Enumerable.Range(0, steps + 1).Select(i =>
        {
            float f = i / (float)steps;
            var eye = c.Toward(Mathf.Lerp(from, to, f), Mathf.Lerp(r0, r1, f));
            return new Key(seconds * f, Pt.At(eye.E, eye.N, Mathf.Lerp(h0, h1, f)), Pt.At(c.E, c.N, lookUp), lens);
        }).ToArray();

    /// <summary>Keys orbiting an actor in its travel frame: angle 0 behind it, 90 to its right.</summary>
    internal static Key[] Around(int actor, float r, float h, float from, float to, double seconds, float lens,
        float lookUp = 0.5f, int steps = 4) =>
        Enumerable.Range(0, steps + 1).Select(i =>
        {
            float a = Mathf.DegToRad(Mathf.Lerp(from, to, i / (float)steps));
            return new Key(seconds * i / steps, Pt.On(actor, r * Mathf.Sin(a), h, r * Mathf.Cos(a)), Pt.On(actor, 0f, lookUp), lens);
        }).ToArray();

    internal static Pt Ground(Spot s, float bearing, float metres, float up)
    {
        var at = s.Toward(bearing, metres);
        return Pt.At(at.E, at.N, up);
    }

    // --- places ----------------------------------------------------------------------------------

    internal static readonly Spot Matterhorn = Spot.Alt(2617049, 1091673, 4478);
    internal static readonly Spot Tremola = new(2686790, 1156030);
    internal static readonly Spot TremolaFoot = new(2686800, 1155445);
    internal static readonly Spot Furka = new(2672150, 1157560);
    internal static readonly Spot FurkaTop = new(2673420, 1158140);
    internal static readonly Spot Gotthard = new(2686600, 1154120);
    /// <summary>Bern's Kramgasse, east end and west end (the Zytglogge).</summary>
    internal static readonly Spot KramgasseEast = new(2601080, 1199661), KramgasseWest = new(2600720, 1199655);
    /// <summary>The flat by the Gornergrat Kulmhotel, the Matterhorn to the west-south-west.</summary>
    internal static readonly Spot Crew = new(2626790, 1092478);
    /// <summary>The Riddes straight (VS), west end and east end.</summary>
    private static readonly Spot RiddesWest = new(2583100, 1113236), RiddesEast = new(2583390, 1113236);
    internal static readonly Spot ChillonBoard = new(2560770, 1140430);

    public static readonly IReadOnlyList<Shot> Shots = new List<Shot>
    {
        // ---- Act I: the country (intro) ----------------------------------------------------------
        new()
        {
            Number = 1, Name = "Alpenglow", FromBar = 1, Bars = 3, Feature = "swissALTI3D relief, sunrise",
            Hour = 7.8, FadeIn = 1.6,
            Keys =
            [
                new(0, Pt.Alt(2627300, 1092800, 3330), Pt.Alt(Matterhorn.E, Matterhorn.N, 3980), 50),
                new(6.04, Pt.Alt(2626950, 1092720, 3372), Pt.Alt(Matterhorn.E, Matterhorn.N, 4010), 50),
            ],
            Captions = [new(1.3, 4.5, "The whole of Switzerland.")],
        },
        new()
        {
            Number = 2, Name = "The lake", FromBar = 4, Bars = 2, Feature = "real buildings, vineyards, the lake, the steamer",
            Hour = 9.0,
            Cast =
            [
                new()
                {
                    Ride = RideKind.Steamer, At = new Spot(2546900, 1148660), Heading = 298, Board = new Spot(2546900, 1148950),
                    Draught = 1.6f, Launch = 5f, Drive = Drive.Controls, Controls = _ => Pedal(1f),
                },
            ],
            Keys =
            [
                new(0, Pt.At(2547020, 1149060, 26), Pt.On(0, 0f, 3f, 25f), 35),
                new(3.9, Pt.At(2547060, 1149045, 26), Pt.On(0, 0f, 3f, 25f), 35),
            ],
            Smooth = 0.4f, Preroll = 4,
            Captions = [new(0.25, 3.5, "Built from real survey data.", "swissALTI3D · swissTLM3D · swissBUILDINGS3D")],
        },
        new()
        {
            Number = 3, Name = "Village morning", FromBar = 6, Bars = 2, Feature = "villages, streets, the road bike",
            Hour = 9.5,
            Cast =
            [
                new() { Ride = RideKind.RoadBike, At = new Spot(2635922, 1160430), Toward = new Spot(2635925, 1160900), Drive = Drive.Road, Seed = 7 },
            ],
            Keys =
            [
                new(0, Pt.At(2635929, 1160478, 1.0f), Pt.On(0, 0f, 1.2f), 28),
                new(3.9, Pt.At(2635929, 1160479, 1.0f), Pt.On(0, 0f, 1.2f), 28),
            ],
            Smooth = 0.15f, Preroll = 4.6,
            Captions = [new(0.2, 3.5, "Every road. Every house.")],
        },

        // ---- Act II: every way to move (groove A) --------------------------------------------------
        new()
        {
            Number = 4, Name = "Pigeon", FromBar = 8, Bars = 1, Feature = "play as a pigeon",
            Hour = 10.5,
            Cast =
            [
                new() { Ride = RideKind.Pigeon, At = KramgasseEast.Up(15), Heading = 268, Drive = Drive.Fly, Launch = 14, Flight = t => Stick(0f, -0.5f, up: t % 0.9 < 0.3 ? 1f : 0f) },
            ],
            Keys =
            [
                new(0, Pt.On(0, 0.4f, 0.6f, 2.2f), Pt.On(0, 0f, -0.8f, -8f), 24),
                new(1.95, Pt.On(0, 0.2f, 0.5f, 2.0f), Pt.On(0, 0f, -1.2f, -8f), 24),
            ],
            Smooth = 0.05f, Preroll = 0.8,
            Captions = [new(0.1, 1.85, "Go anywhere.")],
        },
        new()
        {
            Number = 5, Name = "Tremola", FromBar = 9, Bars = 2, Feature = "cars, drifting, the autopilot",
            Hour = 11.0, Traffic = 0,
            Cast =
            [
                new() { Ride = Car(Ae86), At = Tremola, Toward = TremolaFoot, Drive = Drive.Road, Route = "tremola", Arc = 280, Skill = 1.1f },
            ],
            Keys =
            [
                new(0, Pt.Road("tremola", 378, -9f, 2.3f), Pt.On(0, 0f, 0.7f), 50),
                new(3.9, Pt.Road("tremola", 378, -9f, 2.3f), Pt.On(0, 0f, 0.7f), 50),
            ],
            Smooth = 0.1f, Preroll = 5.8,
        },
        new()
        {
            Number = 6, Name = "Car to car", FromBar = 11, Bars = 2, Feature = "racing, slipstream",
            Hour = 11.0, Traffic = 0,
            Cast =
            [
                new() { Ride = Car(Ae86), At = Tremola, Toward = TremolaFoot, Drive = Drive.Road, Route = "tremola", Arc = 505, Skill = 1.1f },
                new() { Ride = Car(Fd3s), At = Tremola, Toward = TremolaFoot, Drive = Drive.Road, Route = "tremola", Arc = 490, Skill = 1.15f, Aggression = 0.8f, Seed = 3 },
            ],
            Keys =
            [
                new(0, Pt.On(1, 3.2f, 1.0f, 1.5f), Pt.On(1, -0.5f, 0.8f, -6f), 35),
                new(3.9, Pt.On(1, 3.0f, 1.1f, -1.0f), Pt.On(1, -0.5f, 0.8f, -8f), 35),
            ],
            Smooth = 0.15f, Preroll = 3,
        },
        new()
        {
            Number = 7, Name = "Furka", FromBar = 13, Bars = 1, Feature = "motorbikes",
            Hour = 11.5, Traffic = 0,
            Cast =
            [
                new() { Ride = Moto(AfricaTwin), At = Furka, Toward = FurkaTop, Drive = Drive.Follow, Route = "furka", Arc = 1380, Speed = 22, Seed = 11 },
            ],
            Keys =
            [
                // the bend is on a bridge over the stream: high enough to see over its parapets
                new(0, Pt.Road("furka", 1497, -9f, 6f), Pt.On(0, 0f, 0.9f), 35),
                new(1.95, Pt.Road("furka", 1497, -9f, 6f), Pt.On(0, 0f, 0.9f), 35),
            ],
            Smooth = 0.1f, Preroll = 6.5,
        },
        new()
        {
            Number = 8, Name = "Glacier run", FromBar = 14, Bars = 1, Feature = "skis",
            Hour = 12.0,
            Cast =
            [
                // the fall line runs west here (35 m down over 60 m), toward the Matterhorn
                new()
                {
                    Ride = RideKind.Skis, At = new Spot(2621770, 1089700), Heading = 290, Drive = Drive.Controls, Seed = 21,
                    Controls = t => new RideInput(0f, 0f, 0.15f * Mathf.Sin((float)t * 2.4f), true),
                },
            ],
            Keys =
            [
                new(0, Pt.On(0, 2.5f, 1.5f, 5f), Pt.On(0, -0.5f, 0.3f, -10f), 28),
                new(1.95, Pt.On(0, 2.0f, 1.4f, 4.5f), Pt.On(0, -0.5f, 0.3f, -10f), 28),
            ],
            Smooth = 0.08f, Preroll = 2,
        },
        new()
        {
            Number = 9, Name = "Heavy", FromBar = 15, Bars = 2, Feature = "trucks, buses, traffic",
            Hour = 12.5, Traffic = 6,
            Cast =
            [
                new() { Ride = Heavy(Scania), Trailer = Curtainsider, At = Gotthard, Toward = new Spot(2686150, 1154560), Drive = Drive.Follow, Route = "gotthard", Arc = 560, Speed = 14 },
            ],
            Keys =
            [
                new(0, Pt.Road("gotthard", 745, -45f, 22f), Pt.On(0, 0f, 2f, 6f), 50),
                new(3.9, Pt.Road("gotthard", 745, -45f, 22f), Pt.On(0, 0f, 2f, 6f), 50),
            ],
            Smooth = 0.3f, Preroll = 12,
        },
        new()
        {
            Number = 10, Name = "Work site", FromBar = 17, Bars = 2, Feature = "building sites, works machinery",
            Hour = 10.5,
            Cast =
            [
                new() { Ride = RideKind.Excavator, At = new Spot(2542905, 1152292), Heading = 30, Drive = Drive.Controls, Controls = _ => Pedal(0.4f, 0.6f) },
                new() { Ride = RideKind.WheelLoader, At = new Spot(2542930, 1152305), Heading = 300, Drive = Drive.Controls, Controls = _ => Pedal(0.4f, 0.15f), Seed = 4 },
                new() { Ride = RideKind.CompactRoller, At = new Spot(2542880, 1152285), Heading = 80, Drive = Drive.Controls, Controls = _ => Pedal(0.35f), Seed = 5 },
            ],
            Keys =
            [
                new(0, Pt.At(2542935, 1152245, 2.5f), Pt.At(2542895, 1152335, 6), 35),
                new(3.9, Pt.At(2542940, 1152240, 22), Pt.At(2542895, 1152335, 3), 35),
            ],
            Preroll = 2,
        },
        new()
        {
            Number = 11, Name = "Chillon", FromBar = 19, Bars = 2, Feature = "boats, waves, real lake depths",
            Hour = 14.0, Sea = 0.25f,
            Cast =
            [
                new() { Ride = RideKind.Jetski, At = new Spot(2560693, 1140362), Heading = 315, Board = ChillonBoard, Launch = 15, Drive = Drive.Controls, Controls = t => Pedal(1f, 0.1f * Mathf.Sin((float)t * 1.6f)), Seed = 8 },
                new() { Ride = RideKind.Speedboat, At = new Spot(2560689, 1140349), Heading = 315, Board = ChillonBoard, Launch = 14, Drive = Drive.Controls, Controls = _ => Pedal(1f), Seed = 9 },
            ],
            Keys =
            [
                new(0, Pt.Alt(2560628, 1140350, 373.4f), Pt.At(2560720, 1140445, 9), 35),
                new(3.9, Pt.Alt(2560626, 1140348, 373.4f), Pt.At(2560714, 1140445, 9), 35),
            ],
            Preroll = 1.0,
        },
        new()
        {
            Number = 12, Name = "Take-off", FromBar = 21, Bars = 2, Feature = "airliners",
            Hour = 15.0,
            Cast =
            [
                new() { Ride = RideKind.A320, At = new Spot(2496388, 1120293, 40), Heading = 226, Drive = Drive.Fly, Launch = 80, Climb = 8, Flight = _ => Stick(lever: 1f) },
            ],
            Keys =
            [
                new(0, Pt.At(2496200, 1120110, 1.7f), Pt.On(0, 0f, 0f), 28),
                new(3.9, Pt.At(2496200, 1120110, 1.7f), Pt.On(0, 0f, 0f), 28),
            ],
            Smooth = 0.06f, Preroll = 0.4,
        },
        new()
        {
            Number = 13, Name = "Valley", FromBar = 23, Bars = 2, Feature = "helicopter",
            Hour = 13.0,
            Cast =
            [
                new() { Ride = RideKind.Helicopter, At = new Spot(2635900, 1159300, 120), Heading = 355, Drive = Drive.Fly, Launch = 30, Flight = _ => Stick(0f, -1f, up: 0.2f, effort: true) },
            ],
            Keys =
            [
                new(0, Pt.On(0, 14f, 5f, 22f), Pt.On(0, 0f, 0f, -12f), 35),
                new(3.9, Pt.On(0, 18f, 4f, 16f), Pt.On(0, 0f, 0f, -12f), 35),
            ],
            Smooth = 0.4f, Preroll = 2,
        },

        // ---- Act II b: the air (section B) ---------------------------------------------------------
        new()
        {
            Number = 14, Name = "Interlaken", FromBar = 25, Bars = 2, Feature = "paraglider",
            Hour = 14.5,
            Cast =
            [
                new() { Ride = RideKind.Paraglider, At = new Spot(2632479, 1170652, 380), Heading = 250, Drive = Drive.Fly, Launch = 10, Flight = _ => Stick(0.12f), Seed = 12 },
            ],
            Keys = Around(0, 26f, 4f, 120f, 30f, 3.9, 35, lookUp: 3f),
            Smooth = 0.3f, Preroll = 2,
            Captions = [new(0.2, 3.5, "Fly.")],
        },
        new()
        {
            Number = 15, Name = "Wingsuit", FromBar = 27, Bars = 2, Feature = "wingsuit",
            Hour = 14.5,
            Cast =
            [
                new() { Ride = RideKind.Wingsuit, At = Spot.Alt(2635350, 1161600, 1500), Heading = 185, Drive = Drive.Fly, Launch = 42, Climb = -15, Flight = _ => Stick(0f, -0.15f), Seed = 13 },
            ],
            Keys =
            [
                new(0, Pt.On(0, 0.9f, 1.3f, 4.5f), Pt.On(0, 0f, -1f, -20f), 24),
                new(3.9, Pt.On(0, -0.9f, 1.5f, 4.0f), Pt.On(0, 0f, -1f, -20f), 24),
            ],
            Smooth = 0.03f, Shake = 0.4f, Preroll = 1.5,
        },
        new()
        {
            Number = 16, Name = "Aletsch", FromBar = 29, Bars = 2, Feature = "plane",
            Hour = 13.5,
            Cast =
            [
                new() { Ride = RideKind.Plane, At = Spot.Alt(2646200, 1151600, 3150), Heading = 145, Drive = Drive.Fly, Launch = 55, Flight = _ => Stick(lever: 1f), Seed = 14 },
            ],
            Keys =
            [
                new(0, Pt.On(0, -14f, 3f, 16f), Pt.On(0, 0f, -2f, -25f), 35),
                new(3.9, Pt.On(0, -10f, 2f, 10f), Pt.On(0, 0f, -2f, -25f), 35),
            ],
            Smooth = 0.15f, Preroll = 1,
        },

        // ---- Act III: day and night (breakdown) ----------------------------------------------------
        new()
        {
            Number = 17, Name = "Sunset", FromBar = 31, Bars = 2, Feature = "day/night, lit windows",
            Hour = 18.3, MinutesPerDay = 0.75f,
            Keys =
            [
                new(0, Pt.At(2542900, 1151150, 90), Pt.Dir(248, -5), 35),
                new(3.9, Pt.At(2542870, 1151140, 88), Pt.Dir(248, -5.5f), 35),
            ],
            Captions = [new(0.25, 3.5, "Day and night.")],
            Preroll = 0.5,
        },
        new()
        {
            Number = 18, Name = "Night town", FromBar = 33, Bars = 2, Feature = "night lighting, headlights",
            Hour = 22.5, Traffic = 0,
            Cast =
            [
                new() { Ride = Car(Yaris), At = KramgasseEast, Toward = KramgasseWest, Drive = Drive.Follow, Route = "kram", Arc = -223, Speed = 9, Lights = true },
            ],
            Keys =
            [
                new(0, Pt.At(2600900, 1199647, 2.6f), Pt.On(0, 0f, 0.8f), 28),
                new(3.9, Pt.At(2600900, 1199647, 2.6f), Pt.On(0, 0f, 0.8f), 28),
            ],
            Smooth = 0.2f, Preroll = 4,
        },
        new()
        {
            Number = 19, Name = "Party", FromBar = 35, Bars = 2, Feature = "emotes, dances, together",
            Hour = 19.15,
            Cast = Circle(new Spot(2507810, 1137290), 2.6f, [Ymca, Cabbage, Chicken, Macarena, Griddy, Gangnam], 30),
            Keys = Orbit(new Spot(2507810, 1137290), 7f, 6.2f, 1.4f, 1.6f, 200f, 260f, 3.9, 28, lookUp: 1.1f),
            Captions = [new(0.25, 3.5, "Together.")],
            Preroll = 1,
        },
        StyleShot(20, 37, "ps1", "PS1", 0),
        StyleShot(21, 38, "cartoon", "Cartoon", 1),
        StyleShot(22, 39, "real-", "Realistic", 2),
        new()
        {
            Number = 23, Name = "The drop plane", FromBar = 40, Bars = 1, Feature = "Battle Royale plane",
            Hour = 17.4,
            Cast =
            [
                new() { Ride = RideKind.Freighter, At = Spot.Alt(2638000, 1163000, 3100), Heading = 200, Doors = 1 << 3, Drive = Drive.Fly, Launch = 72, Flight = _ => Stick(lever: 0.6f) },
            ],
            Keys =
            [
                new(0, Pt.On(0, -48f, 8f, 18f), Pt.On(0, 0f, 0f, 4f), 35),
                new(1.95, Pt.On(0, -40f, 5f, 26f), Pt.On(0, 0f, -1f, 6f), 35),
            ],
            Smooth = 0.2f, Preroll = 1,
            Captions = [new(0.1, 1.8, "Drop in.")],
        },

        // ---- Act IV: everything at once (drop) -------------------------------------------------------
        new()
        {
            Number = 24, Name = "Jump", FromBar = 41, Bars = 1, Feature = "Battle Royale drop",
            Hour = 17.4,
            Cast =
            [
                new() { Ride = RideKind.Freighter, At = Spot.Alt(2638000, 1163000, 3100), Heading = 200, Doors = 1 << 3, Drive = Drive.Fly, Launch = 72, Flight = _ => Stick(lever: 0.6f) },
                .. Enumerable.Range(0, 4).Select(i => new Cast
                {
                    Ride = RideKind.Wingsuit, At = Spot.Alt(2638000, 1163000, 3096 - i * 2).Toward(20, 16 + i * 6).Toward(110, (i % 2 - 0.5f) * 3f),
                    Heading = 200, Drive = Drive.Fly, Launch = 58 - i * 3, Climb = -8, Flight = _ => Stick(0f, -0.3f), Seed = 40 + i,
                }),
            ],
            Keys =
            [
                // above and behind the tail, clear of the ramp, looking down on them falling away
                new(0, Pt.On(0, 6f, 4f, 26f), Pt.On(2, 0f, 0f), 35),
                new(1.95, Pt.On(0, 6f, 4f, 26f), Pt.On(3, 0f, 0f), 35),
            ],
            Smooth = 0.05f, Preroll = 0.05,
        },
        new()
        {
            Number = 25, Name = "Dive", FromBar = 42, Bars = 1, Feature = "wingsuit",
            Hour = 17.4,
            Cast = Enumerable.Range(0, 3).Select(i => new Cast
            {
                Ride = RideKind.Wingsuit, At = Spot.Alt(2635950 + (i - 1) * 9, 1158575 + i * 6, 1515 + i * 3), Heading = 180,
                Drive = Drive.Fly, Launch = 50, Climb = -18, Flight = _ => Stick(0f, -0.4f), Seed = 50 + i,
            }).ToArray(),
            Keys =
            [
                new(0, Pt.Alt(2635954, 1158500, 1500), Pt.On(1, 0f, 0f), 24),
                new(1.95, Pt.Alt(2635954, 1158500, 1500), Pt.On(1, 0f, 0f), 24),
            ],
            Smooth = 0.05f, Preroll = 0.3, Shake = 0.3f,
        },
        new()
        {
            Number = 26, Name = "Crash", FromBar = 43, Bars = 1, Feature = "crash ragdoll",
            Hour = 16.0, Traffic = 0,
            Cast =
            [
                new() { Ride = Car(Gc8), At = RiddesWest, Toward = RiddesEast, Drive = Drive.Follow, Route = "riddes", Arc = 60, Speed = 24 },
            ],
            Props = [new(default, 0, new Vector3(12f, 3f, 0.8f), new Color(0.62f, 0.6f, 0.56f)) { Route = "riddes", Arc = 150 }],
            Keys =
            [
                new(0, Pt.Road("riddes", 125, -3.5f, 1.3f), Pt.Road("riddes", 151, 0f, 1.0f), 35),
            ],
            Preroll = 4.7,
        },
        new()
        {
            Number = 27, Name = "Grid", FromBar = 44, Bars = 1, Feature = "races, NPC drivers",
            Hour = 15.5, Traffic = 0,
            Cast = Grid([Supra, Bnr32, Fd3s, Evo3, Nsx, S13], Furka, FurkaTop, "grid", 1350, goAt: 1.25),
            Keys =
            [
                new(0, Pt.Road("grid", 1362, -3.0f, 0.9f), Pt.On(1, 0f, 0.8f), 50),
                new(1.95, Pt.Road("grid", 1363, -3.2f, 1.0f), Pt.On(1, 0f, 0.8f), 50),
            ],
            Preroll = 1,
        },
        new()
        {
            Number = 28, Name = "Pack", FromBar = 45, Bars = 2, Feature = "racecraft",
            Hour = 15.5, Traffic = 0,
            Cast = Grid([Supra, Bnr32, Fd3s, Evo3, Nsx, S13], Furka, FurkaTop, "pack", 1380, goAt: 0),
            Keys =
            [
                new(0, Pt.Road("pack", 1500, 0f, 80f), Pt.Road("pack", 1500, 0f, 0f), 35),
                new(3.9, Pt.Road("pack", 1500, 0f, 84f), Pt.Road("pack", 1500, 0f, 0f), 35),
            ],
            Preroll = 6,
        },
        new()
        {
            Number = 29, Name = "Lean", FromBar = 47, Bars = 1, Feature = "motorbikes",
            Hour = 15.5, Traffic = 0,
            Cast =
            [
                new() { Ride = Car(Bnr32), At = Furka, Toward = FurkaTop, Drive = Drive.Follow, Route = "lean", Arc = 460, Speed = 20 },
                new() { Ride = Moto(Monster), At = Furka, Toward = FurkaTop, Drive = Drive.Follow, Route = "lean", Arc = 430, Speed = 23, Seed = 15 },
            ],
            Keys =
            [
                new(0, Pt.On(1, -3.6f, 0.9f, 3.5f), Pt.On(0, 0f, 0.8f), 35),
                new(1.95, Pt.On(1, -3.4f, 0.9f, 2.0f), Pt.On(0, 0f, 0.8f), 35),
            ],
            Smooth = 0.12f, Preroll = 6,
        },
        new()
        {
            Number = 30, Name = "Air", FromBar = 48, Bars = 1, Feature = "boats, sea state",
            Hour = 16.0, Sea = 0.9f,
            Cast =
            [
                new() { Ride = RideKind.Jetski, At = new Spot(2560650, 1140280), Heading = 330, Board = ChillonBoard, Launch = 16, Drive = Drive.Controls, Controls = _ => Pedal(1f), Seed = 16 },
            ],
            Keys =
            [
                new(0, Pt.On(0, -4f, 1.2f, -14f), Pt.On(0, 0f, 0.6f), 24),
                new(1.95, Pt.On(0, -5f, 1.4f, -4f), Pt.On(0, 0f, 0.6f), 24),
            ],
            Smooth = 0.2f, Preroll = 2,
        },
        new()
        {
            Number = 31, Name = "Heavy metal", FromBar = 49, Bars = 2, Feature = "AN-124",
            Hour = 18.6,
            Cast =
            [
                new() { Ride = RideKind.An124, At = Spot.Alt(2508144, 1137170, 440), Heading = 222, Drive = Drive.Fly, Launch = 80, Flight = _ => Stick(lever: 0.7f) },
            ],
            Keys =
            [
                new(0, Pt.Alt(2507835, 1137200, 374.8f), Pt.On(0, 0f, 0f), 35),
                new(3.9, Pt.Alt(2507835, 1137200, 374.8f), Pt.On(0, 0f, 0f), 35),
            ],
            Smooth = 0.06f, Preroll = 0.3,
        },
        new()
        {
            Number = 32, Name = "Cockpit", FromBar = 51, Bars = 2, Feature = "first-person driving, car cabins",
            Hour = 16.5, Traffic = 0,
            Cast =
            [
                new() { Ride = Car(Supra), At = Furka, Toward = FurkaTop, Drive = Drive.Road, Route = "cockpit", Arc = 900, Skill = 1.05f, FirstPerson = true },
            ],
            Keys =
            [
                // over the driver's shoulder: the wheel, the dash and the road through the windscreen
                new(0, Pt.Cockpit(0, -0.7f, 0.12f), Pt.Cockpit(0, 30f, -1.5f), 28),
                new(3.9, Pt.Cockpit(0, -0.7f, 0.12f), Pt.Cockpit(0, 30f, -1.5f), 28),
            ],
            Preroll = 5,
        },
        new()
        {
            Number = 33, Name = "Downhill", FromBar = 53, Bars = 2, Feature = "road bike",
            Hour = 16.0, Traffic = 0,
            Cast =
            [
                new() { Ride = RideKind.RoadBike, At = Tremola, Toward = TremolaFoot, Drive = Drive.Road, Route = "tremola", Arc = 120, Seed = 18 },
            ],
            Keys =
            [
                new(0, Pt.On(0, 0.6f, 1.7f, 4.5f), Pt.On(0, 0f, 0.9f, -10f), 35),
                new(3.9, Pt.On(0, -0.6f, 1.6f, 4.2f), Pt.On(0, 0f, 0.9f, -10f), 35),
            ],
            Smooth = 0.15f, Preroll = 4,
        },
        new()
        {
            Number = 34, Name = "Gridlock", FromBar = 55, Bars = 2, Feature = "city traffic",
            Hour = 19.2, Traffic = 80,
            Keys =
            [
                new(0, Pt.At(2683480, 1246900, 32), Pt.At(2683570, 1246790, 1), 50),
                new(3.9, Pt.At(2683470, 1246895, 32), Pt.At(2683590, 1246800, 1), 50),
            ],
            Preroll = 4,
        },
        new()
        {
            Number = 35, Name = "Flag", FromBar = 57, Bars = 1, Feature = "the Swiss flag",
            Hour = 18.3,
            Cast =
            [
                new() { At = Crew.Toward(60, 12), Heading = 85, Item = (int)Items.ItemId.SwissFlag, Dance = Cheer, Seed = 19 },
            ],
            Keys =
            [
                new(0, Pt.On(0, 1.0f, 0.4f, -3.2f), Pt.On(0, 0f, 1.7f), 24),
                new(1.95, Pt.On(0, 0.8f, 0.3f, -2.8f), Pt.On(0, 0f, 1.8f), 24),
            ],
            Preroll = 0.5,
        },

        // ---- Act V: together (outro and end card) ----------------------------------------------------
        new()
        {
            Number = 36, Name = "Summit", FromBar = 58, Bars = 8, Feature = "multiplayer",
            Hour = 18.55,
            Cast = Circle(Crew, 3.2f, [Wave, Cheer, FistPump, Salute, ArmWave, Clap, Floss, Orange], 60),
            Keys =
            [
                new(0, Ground(Crew, 80, 9, 2.2f), Ground(Crew, 0, 0, 1.3f), 28),
                new(7.8, Ground(Crew, 85, 70, 30f), Pt.Alt(2620000, 1092000, 3400), 28),
                new(15.6, Ground(Crew, 95, 160, 90f), Pt.Alt(Matterhorn.E, Matterhorn.N, 3700), 28),
            ],
            Preroll = 1, FadeOut = 2.2,
            Captions =
            [
                new(1.95, 13.6, "UNITSPORT SWITZERLAND", "On foot · on wheels · on water · in the air", Title: true),
                new(9.8, 5.6, "", Song.Credit),
            ],
        },
    };

    /// <summary>Dancers in a ring round <paramref name="c"/>, facing in, each with its own move.</summary>
    internal static Cast[] Circle(Spot c, float r, int[] dances, int seed) =>
        dances.Select((d, i) =>
        {
            float b = 360f * i / dances.Length;
            return new Cast { At = c.Toward(b, r), Heading = b + 180f, Dance = d, Seed = seed + i };
        }).ToArray();

    /// <summary>A race grid on one road: single file, 11 m apart, the first <paramref name="arc"/> m along it.</summary>
    internal static Cast[] Grid(int[] cars, Spot from, Spot toward, string route, float arc, double goAt) =>
        cars.Select((c, i) => new Cast
        {
            Ride = Car(c), At = from, Toward = toward, Drive = Drive.Road, Route = route, Arc = arc - i * 11f,
            Skill = 1f + 0.03f * (cars.Length - i), Aggression = 0.6f, GoAt = goAt, Seed = 60 + i,
        }).ToArray();

    /// <summary>
    /// Shots 20-22: one drone move up the Lauterbrunnen valley, cut into three bars, each in another
    /// visual style; each starts its keys where the previous one stopped.
    /// </summary>
    private static Shot StyleShot(int number, int bar, string style, string label, int part) => new()
    {
        Number = number, Name = label, FromBar = bar, Bars = 1, Feature = "visual styles",
        Hour = 11.5, Style = style,
        Keys =
        [
            new(0, Pt.Alt(2635980, 1158700, 1080), Pt.Alt(2635950, 1160600, 860), 35),
            new(5.85, Pt.Alt(2635960, 1159350, 1030), Pt.Alt(2635950, 1161200, 840), 35),
        ],
        KeysFrom = part * Song.BarLength,
        Captions = [new(0.15, 1.7, label)],
        Preroll = 0.5,
    };
}
