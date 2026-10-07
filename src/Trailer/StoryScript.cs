using System.Collections.Generic;
using System.Linq;
using Godot;
using UnitSport.Avatar;
using UnitSport.Items;
using UnitSport.Player;
using static UnitSport.Trailer.TrailerScript;

namespace UnitSport.Trailer;

/// <summary>
/// "Meet You at the Top" (#706, the remake): four friends race the sun to a fondue on the
/// Gornergrat, the script <c>docs/trailer/script.md</c> as shots. Nothing on screen names a
/// feature: the chat is the dialogue, the cards are real places and hours, the light tells the
/// time. Places as in <see cref="TrailerScript"/> (read off overhead stills), timed from
/// <c>--trailer-log</c>.
/// </summary>
public static class StoryScript
{
    // ---- the cast: the same face and clothes in every shot ------------------------------------

    internal static readonly Character Lea = new("LÉA", new Color("5ad06e"),
        new Appearance(BodyBuild.Slim, 3, 2, 1, HairStyle.Ponytail, 3),
        [ItemId.GreenPolo, ItemId.BlackShorts, ItemId.WhiteSneakers]);
    internal static readonly Character Marco = new("MARCO", new Color("ffb84d"),
        new Appearance(BodyBuild.Broad, 5, 0, 3, HairStyle.Quiff, 0),
        [ItemId.WhiteTee, ItemId.Jeans, ItemId.BlackShades, ItemId.WhiteSneakers]);
    internal static readonly Character Nina = new("NINA", new Color("6ec8ff"),
        new Appearance(BodyBuild.Curvy, 8, 1, 0, HairStyle.Bun, 6),
        [ItemId.NavyPolo, ItemId.CargoPants, ItemId.RoundGlasses, ItemId.WhiteSneakers]);
    internal static readonly Character Jonas = new("JONAS", new Color("ff7a45"),
        new Appearance(BodyBuild.Stocky, 6, 0, 4, HairStyle.Short, 2),
        [ItemId.BlackBeanie, ItemId.StripedLongsleeve, ItemId.CargoPants, ItemId.CombatBoots]);
    internal static readonly Character Boss = new("BOSS", new Color("b4b4b4"), Appearance.Default, []);

    private const int Shrug = 5;

    // ---- places of the story ------------------------------------------------------------------

    /// <summary>Lausanne, Sous-Gare: the avenue down to Ouchy and the lake.</summary>
    private static readonly Spot LeaDoor = new(2537700, 1151760);
    private static readonly Spot AvenueTop = new(2537625, 1151830), AvenueFoot = new(2537660, 1151300);
    /// <summary>Täsch: the valley road and the terminal where it ends (Zermatt is car-free).</summary>
    private static readonly Spot TaschRoad = new(2626190, 1102350), TaschTerminal = new(2626080, 1101950);
    private static readonly Spot PullySite = new(2542905, 1152292);
    /// <summary>The theodul slope, falling west toward the Matterhorn (35 m over 60 m).</summary>
    private static readonly Spot Slope = new(2621770, 1089700);

    /// <summary>The four on the Gornergrat, round the pot: Léa west, Marco north, Nina east, Jonas south.</summary>
    private static Cast[] Friends(int lea, int marco, int nina, int jonas, int leaHolds = 0) =>
    [
        new() { Who = Lea, At = Crew.Toward(270, 1.6f), Heading = 90, Dance = lea, Item = leaHolds },
        new() { Who = Marco, At = Crew.Toward(0, 1.6f), Heading = 180, Dance = marco },
        new() { Who = Nina, At = Crew.Toward(90, 1.6f), Heading = 270, Dance = nina },
        new() { Who = Jonas, At = Crew.Toward(180, 1.6f), Heading = 0, Dance = jonas },
    ];

    /// <summary>The fondue pot set down in the middle of them, and the radio on its rock.</summary>
    private static readonly Prop[] Picnic =
    [
        new(Crew.Up(0f), 0, default, default) { Item = ItemId.FonduePot, Scale = 1.6f },
        new(Crew.Toward(45, 2.4f).Up(0.4f), 220, default, default) { Item = ItemId.Radio, Scale = 1.4f },
    ];

    /// <summary>
    /// The four in a row for the photo, north to south, facing east where it is taken from, the
    /// Kulmhotel's domes behind them: Marco, Léa (her camera in hand), Nina, Jonas.
    /// </summary>
    private static Cast[] Row(int marco, int lea, int nina, int jonas) =>
    [
        new() { Who = Lea, At = Crew.Toward(0, 0.6f), Heading = 90, Dance = lea, Item = (int)ItemId.Camera },
        new() { Who = Marco, At = Crew.Toward(0, 1.8f), Heading = 90, Dance = marco },
        new() { Who = Nina, At = Crew.Toward(180, 0.6f), Heading = 90, Dance = nina },
        new() { Who = Jonas, At = Crew.Toward(180, 1.8f), Heading = 90, Dance = jonas },
    ];

    /// <summary>The pot in front of the row, the radio beside it.</summary>
    private static readonly Prop[] PicnicFront =
    [
        new(Crew.Toward(90, 1.3f), 0, default, default) { Item = ItemId.FonduePot, Scale = 1.6f },
        new(Crew.Toward(40, 2.6f).Up(0.3f), 250, default, default) { Item = ItemId.Radio, Scale = 1.4f },
    ];

    public static readonly IReadOnlyList<Shot> Shots = new List<Shot>
    {
        // ---- cold open: the invitation (intro) -----------------------------------------------------
        new()
        {
            Number = 1, Name = "The invitation", FromBar = 1, Bars = 3, Hour = 7.8, FadeIn = 1.8,
            Keys =
            [
                new(0, Pt.Alt(2627300, 1092800, 3330), Pt.Alt(Matterhorn.E, Matterhorn.N, 3980), 50),
                new(6.04, Pt.Alt(2626950, 1092720, 3372), Pt.Alt(Matterhorn.E, Matterhorn.N, 4010), 50),
            ],
            Chat = [new(0.15, Lea, "fondue on the gornergrat tonight. sunset. don't be late")],
            Supers = [new(2.6, 3.2, "GORNERGRAT · 3135 M")],
        },
        new()
        {
            Number = 2, Name = "Léa's door", FromBar = 4, Bars = 1, Hour = 7.7,
            Cast = [new() { Who = Lea, At = LeaDoor, FromDoor = true, Item = (int)ItemId.FonduePot }],
            Keys =
            [
                new(0, Pt.On(0, 1.4f, 1.3f, -4.2f), Pt.On(0, 0f, 1.3f), 28),
                new(1.95, Pt.On(0, 1.1f, 1.4f, -3.4f), Pt.On(0, 0f, 1.4f), 28),
            ],
            Smooth = 0.3f, Preroll = 0.4,
            Chat = [new(0.3, Marco, "say less")],
        },
        new()
        {
            Number = 3, Name = "Downhill to the lake", FromBar = 5, Bars = 1, Hour = 7.7,
            Cast = [new() { Who = Lea, Ride = RideKind.RoadBike, At = AvenueTop, Toward = AvenueFoot, Drive = Drive.Road, Route = "avenue", Arc = 100, Item = (int)ItemId.FonduePot }],
            Keys =
            [
                new(0, Pt.Road("avenue", 134, -2.6f, 1.0f), Pt.On(0, 0f, 1.2f), 28),
                new(1.95, Pt.Road("avenue", 134, -2.6f, 1.1f), Pt.On(0, 0f, 1.0f), 28),
            ],
            Smooth = 0.1f, Preroll = 4.0,
            Chat = [new(0.2, Nina, "flying till 4. ill make it")],
            Supers = [new(0.2, 1.7, "LAUSANNE · 07:40")],
        },
        new()
        {
            Number = 4, Name = "Lavaux", FromBar = 6, Bars = 2, Hour = 8.6,
            Cast =
            [
                new() { Who = Lea, Ride = RideKind.RoadBike, At = new Spot(2547020, 1149060), Heading = 290, Drive = Drive.Road, Arc = 0 },
                new()
                {
                    Ride = RideKind.Steamer, At = new Spot(2546900, 1148660), Heading = 298, Board = new Spot(2546900, 1148950),
                    Draught = 1.6f, Launch = 5f, Drive = Drive.Controls, Controls = _ => Pedal(1f), Seed = 70,
                },
            ],
            Keys =
            [
                new(0, Pt.On(0, -2.6f, 2.0f, 6.5f), Pt.On(0, 5f, -1.5f, -25f), 35),
                new(3.9, Pt.On(0, -2.2f, 2.4f, 5.5f), Pt.On(0, 6f, -2f, -25f), 35),
            ],
            Smooth = 0.3f, Preroll = 3,
            Chat = [new(0.3, Jonas, "site deadline today. ill try")],
        },

        // ---- act one: four ways (groove A) -------------------------------------------------------
        new()
        {
            Number = 5, Name = "The pigeon", FromBar = 8, Bars = 1, Hour = 10.5,
            Cast = [new() { Ride = RideKind.Pigeon, At = KramgasseEast.Up(15), Heading = 268, Drive = Drive.Fly, Launch = 14, Flight = t => Stick(0f, -0.5f, up: t % 0.9 < 0.3 ? 1f : 0f) }],
            Keys =
            [
                new(0, Pt.On(0, 0.4f, 0.6f, 2.2f), Pt.On(0, 0f, -0.8f, -8f), 24),
                new(1.95, Pt.On(0, 0.2f, 0.5f, 2.0f), Pt.On(0, 0f, -1.2f, -8f), 24),
            ],
            Smooth = 0.05f, Preroll = 0.8,
        },
        new()
        {
            Number = 6, Name = "Tremola", FromBar = 9, Bars = 2, Hour = 9.0, Traffic = 0,
            Cast = [new() { Who = Marco, Ride = Car(Ae86), At = Tremola, Toward = TremolaFoot, Drive = Drive.Road, Route = "tremola", Arc = 280, Skill = 1.1f }],
            Keys =
            [
                new(0, Pt.Road("tremola", 378, -9f, 2.3f), Pt.On(0, 0f, 0.7f), 50),
                new(3.9, Pt.Road("tremola", 378, -9f, 2.3f), Pt.On(0, 0f, 0.7f), 50),
            ],
            Smooth = 0.1f, Preroll = 5.8,
            Supers = [new(0.3, 3.0, "AIROLO · 08:15")],
        },
        new()
        {
            Number = 7, Name = "The stranger", FromBar = 11, Bars = 2, Hour = 9.0, Traffic = 0,
            Cast =
            [
                new() { Who = Marco, Ride = Car(Ae86), At = Tremola, Toward = TremolaFoot, Drive = Drive.Road, Route = "tremola", Arc = 505, Skill = 1.1f },
                new() { Ride = Car(Fd3s), At = Tremola, Toward = TremolaFoot, Drive = Drive.Road, Route = "tremola", Arc = 490, Skill = 1.15f, Aggression = 0.8f, Seed = 3 },
            ],
            Keys =
            [
                new(0, Pt.On(1, 3.2f, 1.0f, 1.5f), Pt.On(1, -0.5f, 0.8f, -6f), 35),
                new(3.9, Pt.On(1, 3.0f, 1.1f, -1.0f), Pt.On(1, -0.5f, 0.8f, -8f), 35),
            ],
            Smooth = 0.15f, Preroll = 3,
            Chat = [new(1.2, Marco, "brb")],
        },
        new()
        {
            Number = 8, Name = "Nina's morning", FromBar = 13, Bars = 2, Hour = 9.1,
            Cast = [new() { Who = Nina, Ride = RideKind.A320, At = new Spot(2496388, 1120293, 40), Heading = 226, Drive = Drive.Fly, Launch = 80, Climb = 8, Flight = _ => Stick(lever: 1f) }],
            Keys =
            [
                new(0, Pt.At(2496200, 1120110, 1.7f), Pt.On(0, 0f, 0f), 28),
                new(3.9, Pt.At(2496200, 1120110, 1.7f), Pt.On(0, 0f, 0f), 28),
            ],
            Smooth = 0.06f, Preroll = 0.4,
            Supers = [new(0.3, 3.0, "GENÈVE · 09:05")],
        },
        new()
        {
            Number = 9, Name = "Jonas's site", FromBar = 15, Bars = 2, Hour = 10.5,
            Cast =
            [
                new() { Who = Jonas, Ride = RideKind.Excavator, At = PullySite, Heading = 30, Drive = Drive.Controls, Controls = _ => Pedal(0.4f, 0.6f) },
                new() { Ride = RideKind.WheelLoader, At = new Spot(2542930, 1152305), Heading = 300, Drive = Drive.Controls, Controls = _ => Pedal(0.4f, 0.15f), Seed = 4 },
                new() { Ride = RideKind.CompactRoller, At = new Spot(2542880, 1152285), Heading = 80, Drive = Drive.Controls, Controls = _ => Pedal(0.35f), Seed = 5 },
            ],
            Keys =
            [
                new(0, Pt.At(2542935, 1152245, 2.5f), Pt.At(2542895, 1152335, 6), 35),
                new(3.9, Pt.At(2542940, 1152240, 22), Pt.At(2542895, 1152335, 3), 35),
            ],
            Preroll = 2,
            Supers = [new(0.3, 3.0, "PULLY · 10:30")],
        },
        new()
        {
            Number = 10, Name = "The steamer", FromBar = 17, Bars = 2, Hour = 12.5,
            Cast =
            [
                new()
                {
                    Ride = RideKind.Steamer, At = new Spot(2560620, 1140420), Heading = 318, Board = ChillonBoard,
                    Draught = 1.6f, Drive = Drive.Controls, Controls = _ => Pedal(1f), Seed = 71,
                },
                new() { Ride = RideKind.Jetski, At = new Spot(2560700, 1140360), Heading = 250, Board = ChillonBoard, Launch = 15, Drive = Drive.Controls, Controls = _ => Pedal(1f), Seed = 8 },
            ],
            Keys =
            [
                new(0, Pt.Alt(2560560, 1140300, 373.6f), Pt.Alt(2560662, 1140440, 378), 35),
                new(3.9, Pt.Alt(2560575, 1140296, 373.8f), Pt.Alt(2560672, 1140442, 378), 35),
            ],
            Smooth = 0.3f, Preroll = 1.5,
            Chat = [new(0.6, Lea, "on the boat. chillon says hi")],
        },
        new()
        {
            Number = 11, Name = "The inside line", FromBar = 19, Bars = 2, Hour = 11.5, Traffic = 0,
            Cast =
            [
                new() { Ride = Car(Fd3s), At = Furka, Toward = FurkaTop, Drive = Drive.Road, Route = "pack", Arc = 1380, Skill = 1.0f, Aggression = 0.5f, Seed = 3 },
                new() { Who = Marco, Ride = Car(Ae86), At = Furka, Toward = FurkaTop, Drive = Drive.Road, Route = "pack", Arc = 1373, Skill = 1.3f, Aggression = 1f },
            ],
            Keys =
            [
                new(0, Pt.Road("pack", 1500, 0f, 80f), Pt.Road("pack", 1500, 0f, 0f), 35),
                new(3.9, Pt.Road("pack", 1500, 0f, 84f), Pt.Road("pack", 1500, 0f, 0f), 35),
            ],
            Preroll = 6,
        },
        Dream(12, 21, "cartoon", 0),
        Dream(13, 22, "real-", 1),
        new()
        {
            Number = 14, Name = "JONAS.", FromBar = 23, Bars = 2, Hour = 14.0,
            Cast = [new() { Who = Jonas, Ride = RideKind.Excavator, At = PullySite, Heading = 30, Drive = Drive.Stand }],
            Keys =
            [
                new(0, Pt.On(0, -4.6f, 2.7f, -0.6f), Pt.Cockpit(0), 50),
                new(3.9, Pt.On(0, -4.2f, 2.7f, -0.4f), Pt.Cockpit(0), 50),
            ],
            Preroll = 1,
            Chat = [new(0.15, Boss, "JONAS.")],
        },

        // ---- act one b: above it all (section B) ---------------------------------------------------
        new()
        {
            Number = 15, Name = "The Aletsch", FromBar = 25, Bars = 2, Hour = 14.5,
            Cast = [new() { Who = Nina, Ride = RideKind.A320, At = Spot.Alt(2646200, 1151600, 3700), Heading = 145, Drive = Drive.Fly, Launch = 90, Flight = _ => Stick(lever: 0.8f) }],
            Keys =
            [
                new(0, Pt.On(0, -34f, 7f, 30f), Pt.On(0, 0f, -2f, -10f), 35),
                new(3.9, Pt.On(0, -28f, 5f, 24f), Pt.On(0, 0f, -2f, -10f), 35),
            ],
            Smooth = 0.2f, Preroll = 1,
            Chat = [new(0.4, Nina, "u guys should see the aletsch rn")],
        },
        new()
        {
            Number = 16, Name = "Car-free", FromBar = 27, Bars = 2, Hour = 15.0, Traffic = 0,
            Cast = [new() { Who = Marco, Ride = Car(Ae86), At = TaschRoad, Toward = TaschTerminal, Drive = Drive.Follow, Route = "tasch", Speed = 9, StopAt = 85 }],
            Keys =
            [
                new(0, Pt.On(0, 0.6f, 1.1f, 7f), Pt.On(0, 0f, 0.8f, -25f), 35),
                new(3.9, Pt.On(0, 0.8f, 1.2f, 8f), Pt.On(0, 0f, 0.8f, -25f), 35),
            ],
            Smooth = 0.3f, Preroll = 8,
            Chat = [new(0.9, Marco, "zermatt is CAR FREE??"), new(2.5, Lea, "obviously")],
        },
        new()
        {
            Number = 17, Name = "Improvised", FromBar = 29, Bars = 2, Hour = 15.5,
            Cast =
            [
                new()
                {
                    Who = Marco, Ride = RideKind.Skis, At = Slope, Heading = 290, Drive = Drive.Controls,
                    Controls = t => new RideInput(0f, 0f, 0.15f * Mathf.Sin((float)t * 2.4f), false),
                },
            ],
            Keys =
            [
                new(0, Pt.On(0, 2.5f, 1.5f, 5f), Pt.On(0, -0.5f, 0.3f, -10f), 28),
                new(3.9, Pt.On(0, 1.6f, 1.3f, 4.2f), Pt.On(0, -0.5f, 0.3f, -10f), 28),
            ],
            Smooth = 0.08f, Preroll = 1.5,
        },

        // ---- act two: the sun is going down (breakdown) --------------------------------------------
        new()
        {
            Number = 18, Name = "The sun goes down", FromBar = 31, Bars = 2, Hour = 17.6, MinutesPerDay = 1.56f,
            Keys =
            [
                new(0, Pt.At(2542900, 1151150, 90), Pt.Dir(248, -5), 35),
                new(3.9, Pt.At(2542870, 1151140, 88), Pt.Dir(248, -5.5f), 35),
            ],
            Preroll = 0.5,
            Supers = [new(0.3, 3.4, "18:20")],
        },
        new()
        {
            Number = 19, Name = "Still at work", FromBar = 33, Bars = 2, Hour = 18.75,
            Cast = [new() { Who = Jonas, Ride = RideKind.Excavator, At = PullySite, Heading = 30, Drive = Drive.Controls, Controls = _ => Pedal(0.3f, -0.5f) }],
            Keys =
            [
                new(0, Pt.At(2542935, 1152245, 2.8f), Pt.At(2542900, 1152320, 5), 35),
                new(3.9, Pt.At(2542937, 1152243, 3.0f), Pt.At(2542900, 1152320, 5), 35),
            ],
            Preroll = 1,
            Chat = [new(0.6, Jonas, "not gonna make it")],
        },
        new()
        {
            Number = 20, Name = "Alone with the pot", FromBar = 35, Bars = 2, Hour = 18.35,
            Cast = [new() { Who = Lea, At = Crew, Heading = 85, Dance = Shrug, Item = (int)ItemId.FonduePot }],
            Keys =
            [
                new(0, Ground(Crew, 100, 9, 1.0f), Pt.On(0, 0f, 1.3f), 28),
                new(3.9, Ground(Crew, 98, 8, 1.0f), Pt.On(0, 0f, 1.3f), 28),
            ],
            Preroll = 0.5,
            Chat = [new(1.2, Lea, "...")],
        },
        new()
        {
            Number = 21, Name = "Where are you", FromBar = 37, Bars = 1, Hour = 18.45,
            Cast = [new() { Who = Nina, Ride = RideKind.Freighter, At = new Spot(2497250, 1121000), Heading = 226, Drive = Drive.Stand, Doors = 1 << 3 }],
            Keys =
            [
                new(0, Pt.On(0, -14f, 2.0f, -22f), Pt.On(0, 0f, 3.5f, -8f), 35),
                new(1.95, Pt.On(0, -13f, 2.0f, -21f), Pt.On(0, 0f, 3.5f, -8f), 35),
            ],
            Preroll = 0.5,
            Chat = [new(0.05, Nina, "where r u"), new(0.9, Jonas, "pully. why")],
        },
        new()
        {
            Number = 22, Name = "Stand by", FromBar = 38, Bars = 1, Hour = 18.5,
            Cast =
            [
                new() { Who = Nina, Ride = RideKind.Freighter, At = new Spot(2543060, 1152150, 110), Heading = 320, Doors = 1 << 3, Drive = Drive.Fly, Launch = 70, Flight = _ => Stick(lever: 0.6f) },
                new() { Who = Jonas, At = new Spot(2542915, 1152282), Heading = 140 },
            ],
            Keys =
            [
                new(0, Pt.On(1, 0.9f, 1.5f, 2.6f), Pt.On(0, 0f, 0f), 24),
                new(1.95, Pt.On(1, 0.9f, 1.5f, 2.6f), Pt.On(0, 0f, 0f), 24),
            ],
            Smooth = 0.1f, Preroll = 0.3,
            Chat = [new(0.05, Nina, "stand by")],
        },
        new()
        {
            Number = 23, Name = "Golden hour", FromBar = 39, Bars = 1, Hour = 18.35,
            Cast = [new() { Who = Nina, Ride = RideKind.Freighter, At = Spot.Alt(2624000, 1104000, 3300), Heading = 175, Doors = 1 << 3, Drive = Drive.Fly, Launch = 72, Flight = _ => Stick(lever: 0.6f) }],
            Keys =
            [
                new(0, Pt.On(0, -48f, 8f, 18f), Pt.On(0, 0f, 0f, 4f), 35),
                new(1.95, Pt.On(0, -40f, 5f, 26f), Pt.On(0, 0f, -1f, 6f), 35),
            ],
            Smooth = 0.2f, Preroll = 5,
        },
        new()
        {
            Number = 24, Name = "Heading south", FromBar = 40, Bars = 1, Hour = 18.4,
            Cast =
            [
                new() { Who = Nina, Ride = RideKind.Freighter, At = Spot.Alt(2624000, 1104000, 3300), Heading = 175, Doors = 1 << 3, Drive = Drive.Fly, Launch = 72, Flight = _ => Stick(lever: 0.6f) },
            ],
            Keys =
            [
                new(0, Pt.On(0, -7f, 3f, 36f), Pt.On(0, 5f, -3f, -80f), 35),
                new(1.95, Pt.On(0, -5f, 2f, 30f), Pt.On(0, 6f, -4f, -80f), 35),
            ],
            Smooth = 0.05f, Preroll = 1,
        },

        // ---- act three: everything at once (the drop) ----------------------------------------------
        new()
        {
            Number = 25, Name = "He jumps", FromBar = 41, Bars = 1, Hour = 18.4,
            Cast =
            [
                new() { Who = Nina, Ride = RideKind.Freighter, At = Spot.Alt(2624000, 1104000, 3300), Heading = 175, Doors = 1 << 3, Drive = Drive.Fly, Launch = 72, Flight = _ => Stick(lever: 0.6f) },
                new()
                {
                    Who = Jonas, Ride = RideKind.Wingsuit, At = Spot.Alt(2624000, 1104000, 3295).Toward(355, 18), Heading = 175,
                    Drive = Drive.Fly, Launch = 58, Climb = -8, Flight = _ => Stick(0f, -0.3f),
                },
            ],
            Keys =
            [
                new(0, Pt.On(0, 6f, 4f, 26f), Pt.On(1, 0f, 0f), 35),
                new(1.95, Pt.On(0, 6f, 4f, 26f), Pt.On(1, 0f, 0f), 35),
            ],
            Smooth = 0.05f, Preroll = 0.05,
        },
        new()
        {
            Number = 26, Name = "The valley", FromBar = 42, Bars = 1, Hour = 18.4,
            Cast = [new() { Who = Jonas, Ride = RideKind.Wingsuit, At = Spot.Alt(2625190, 1099400, 2015), Heading = 180, Drive = Drive.Fly, Launch = 50, Climb = -18, Flight = _ => Stick(0f, -0.4f) }],
            Keys =
            [
                new(0, Pt.Alt(2625194, 1099326, 2000), Pt.On(0, 0f, 0f), 24),
                new(1.95, Pt.Alt(2625194, 1099326, 2000), Pt.On(0, 0f, 0f), 24),
            ],
            Smooth = 0.05f, Preroll = 0.3, Shake = 0.3f,
        },
        new()
        {
            Number = 27, Name = "Full tuck", FromBar = 43, Bars = 1, Hour = 18.45,
            Cast = [new() { Who = Marco, Ride = RideKind.Skis, At = Slope, Heading = 290, Drive = Drive.Controls, Controls = _ => new RideInput(0f, 0f, 0f, true) }],
            Keys =
            [
                new(0, Pt.On(0, 1.2f, 0.6f, -7f), Pt.On(0, 0f, 0.6f), 24),
                new(1.95, Pt.On(0, 1.4f, 0.6f, -2f), Pt.On(0, 0f, 0.6f), 24),
            ],
            Smooth = 0.05f, Preroll = 3,
        },
        new()
        {
            Number = 28, Name = "Still coming", FromBar = 44, Bars = 1, Hour = 18.45,
            Cast = [new() { Ride = RideKind.Pigeon, At = Spot.Alt(2620200, 1092600, 3500), Heading = 250, Drive = Drive.Fly, Launch = 14, Flight = t => Stick(0f, -0.5f, up: t % 0.9 < 0.3 ? 1f : 0f) }],
            Keys =
            [
                new(0, Pt.On(0, 0.5f, 0.5f, 2.4f), Pt.On(0, 0f, 0f, -30f), 35),
                new(1.95, Pt.On(0, 0.3f, 0.4f, 2.2f), Pt.On(0, 0f, 0f, -30f), 35),
            ],
            Smooth = 0.05f, Preroll = 0.8,
        },
        new()
        {
            Number = 29, Name = "Canopy", FromBar = 45, Bars = 2, Hour = 18.5,
            Cast = [new() { Who = Jonas, Ride = RideKind.Paraglider, At = Spot.Alt(2627000, 1092350, 3290), Heading = 290, Drive = Drive.Fly, Launch = 10, Flight = _ => Stick(0.1f) }],
            Keys = Around(0, 22f, 4f, 140f, 50f, 3.9, 35, lookUp: 3f),
            Smooth = 0.3f, Preroll = 1,
        },
        new()
        {
            Number = 30, Name = "She sees him", FromBar = 47, Bars = 1, Hour = 18.5,
            Cast = [new() { Who = Lea, At = Crew, Heading = 85, Dance = Cheer }],
            Keys =
            [
                new(0, Pt.On(0, 1.0f, 0.4f, -3.2f), Pt.On(0, 0f, 1.7f), 24),
                new(1.95, Pt.On(0, 0.8f, 0.3f, -2.8f), Pt.On(0, 0f, 1.8f), 24),
            ],
            Preroll = 0.5,
        },
        new()
        {
            Number = 31, Name = "Marco skids in", FromBar = 48, Bars = 1, Hour = 18.5,
            Cast =
            [
                new()
                {
                    Who = Marco, Ride = RideKind.Skis, At = new Spot(2626870, 1092430), Heading = 160, Drive = Drive.Controls,
                    Controls = t => t < 2.6 ? new RideInput(0f, 0f, 0f, false) : new RideInput(0f, 1f, 0.8f, false),
                },
            ],
            Keys =
            [
                new(0, Pt.On(0, -3f, 1.2f, -9f), Pt.On(0, 0f, 0.8f), 28),
                new(1.95, Pt.On(0, -3.5f, 1.2f, -4f), Pt.On(0, 0f, 0.8f), 28),
            ],
            Smooth = 0.1f, Preroll = 1.5,
        },
        new()
        {
            Number = 32, Name = "Nina, of course", FromBar = 49, Bars = 2, Hour = 18.52,
            Cast = [new() { Who = Nina, Ride = RideKind.Helicopter, At = Spot.Alt(2626800, 1092385, 3130), Heading = 352, Drive = Drive.Fly, Launch = 10, Flight = _ => Stick(0f, -0.4f, up: 1f) }],
            Keys =
            [
                new(0, Ground(Crew, 175, 20, 1.6f), Pt.On(0, 0f, 0f), 35),
                new(3.9, Ground(Crew, 175, 20, 1.8f), Pt.On(0, 0f, 0f), 35),
            ],
            Smooth = 0.1f, Preroll = 1,
        },
        new()
        {
            Number = 33, Name = "Touchdown", FromBar = 51, Bars = 2, Hour = 18.53,
            Cast = [new() { Who = Jonas, Ride = RideKind.Paraglider, At = Crew.Toward(75, 50).Up(7), Heading = 255, Drive = Drive.Fly, Launch = 9, Flight = t => Stick(0f, t > 3.2 ? 1f : 0f) }],
            Keys =
            [
                new(0, Ground(Crew, 150, 10, 1.4f), Pt.On(0, 0f, 0f), 28),
                new(3.9, Ground(Crew, 150, 10, 1.4f), Pt.On(0, 0f, 0f), 28),
            ],
            Smooth = 0.15f, Preroll = 0.5,
        },
        new()
        {
            Number = 34, Name = "Together", FromBar = 53, Bars = 2, Hour = 18.55,
            Cast = Friends(Wave, Cheer, FistPump, Clap),
            Props = Picnic,
            Keys = Orbit(Crew, 6.5f, 6f, 1.6f, 1.8f, 120f, 70f, 3.9, 28, lookUp: 1.0f),
            Preroll = 0.5,
        },
        new()
        {
            Number = 35, Name = "The party", FromBar = 55, Bars = 2, Hour = 18.57,
            Cast = Friends(Chicken, Griddy, Robot, Ymca),
            Props = Picnic,
            Keys = Orbit(Crew, 5.5f, 6f, 1.3f, 1.5f, 220f, 290f, 3.9, 28, lookUp: 1.0f),
            Preroll = 0.5,
        },
        new()
        {
            Number = 36, Name = "The flag", FromBar = 57, Bars = 1, Hour = 18.6,
            Cast = [new() { Who = Lea, At = Crew.Toward(270, 1.6f), Heading = 85, Item = (int)ItemId.SwissFlag, Dance = Cheer }],
            Props = Picnic,
            Keys =
            [
                new(0, Pt.On(0, 1.0f, 0.4f, -3.2f), Pt.On(0, 0f, 1.7f), 24),
                new(1.95, Pt.On(0, 0.8f, 0.3f, -2.8f), Pt.On(0, 0f, 1.8f), 24),
            ],
            Preroll = 0.5,
        },

        // ---- ending: the photo (outro) -------------------------------------------------------------
        Ending(37, 58, 4, 0),
        Ending(38, 62, 4, 4 * Song.BarLength),
    };

    /// <summary>
    /// Shots 12-13: Jonas's daydream, the Lauterbrunnen drone move of the first cut, in Cartoon then
    /// Realistic; the second starts its keys where the first stopped.
    /// </summary>
    private static Shot Dream(int number, int bar, string style, int part) => new()
    {
        Number = number, Name = style == "cartoon" ? "Daydream" : "Deeper", FromBar = bar, Bars = 1, Hour = 12.0, Style = style,
        Keys =
        [
            new(0, Pt.Alt(2635980, 1158700, 1080), Pt.Alt(2635950, 1160600, 860), 35),
            new(3.9, Pt.Alt(2635960, 1159350, 1030), Pt.Alt(2635950, 1161200, 840), 35),
        ],
        KeysFrom = part * Song.BarLength,
        Preroll = 0.5,
    };

    /// <summary>
    /// Shots 37-38, one move cut in two: the four in a row at sunset, the pigeon by the pot, the
    /// camera drawing back; then the photo from in front of them: the flash, the
    /// polaroid, the title, the credit.
    /// </summary>
    private static Shot Ending(int number, int bar, int bars, double from) => new()
    {
        Number = number, Name = number == 37 ? "The fifth guest" : "The photo", FromBar = bar, Bars = bars, Hour = 18.62,
        Cast =
        [
            .. Row(Wave, number == 37 ? 0 : Cheer, number == 37 ? Clap : FistPump, number == 37 ? Cheer : Salute),
            // by the pot, in the photo too (one gliding in passed over their heads and out of the frame)
            new() { Ride = RideKind.Pigeon, At = Crew.Toward(80, 2.6f), Heading = 110, Seed = 90 },
        ],
        Props = PicnicFront,
        Keys =
        [
            new(0, Ground(Crew, 95, 3.6f, 1.6f), Ground(Crew, 0, 0, 1.2f), 28),
            new(7.8, Ground(Crew, 88, 6.5f, 2.4f), Ground(Crew, 0, 0, 1.1f), 28),
            new(15.6, Ground(Crew, 80, 12f, 7f), Ground(Crew, 0, 0, 1.0f), 28),
        ],
        KeysFrom = from,
        Preroll = 0.5,
        FadeOut = number == 38 ? 2.2 : 0,
        Photo = number == 38 ? 0.3 : null,
        PhotoFrom = number == 38 ? new Key(0, Ground(Crew, 90, 4.6f, 1.5f), Ground(Crew, 0, 0, 1.15f), 28) : null,
        Chat = number == 37 ? [new(2.4, null, "PIGEON joined the game")] : [],
        Captions = number == 38
            ?
            [
                new(1.2, 6.6, "UNITSPORT SWITZERLAND", "Meet you at the top.", Title: true),
                new(3.6, 4.2, "", Song.Credit),
            ]
            : [],
    };
}
