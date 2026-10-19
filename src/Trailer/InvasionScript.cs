using System.Collections.Generic;
using System.Linq;
using Godot;
using UnitSport.Avatar;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Ui.Bubbles;
using static UnitSport.Trailer.TrailerScript;

namespace UnitSport.Trailer;

/// <summary>
/// "Invasion, Swiss Style" (#866): a UFO fleet invades Switzerland, the Swiss answer with everything
/// they have, and it ends with the aliens dancing at the party. The script
/// <c>docs/trailer/invasion.md</c> as shots, cut on "Space 1990"; every shot in Cartoon, on real
/// terrain, with the film kit (#882): saucers flown as actors or set as <see cref="Craft"/>, a sighting's
/// flyover, the mothership, herds under the beam, the speech bubbles (each with the id it carries in
/// <c>docs/trailer/invasion.subtitles.json</c>) and the game's map screen.
/// </summary>
public static class InvasionScript
{
    /// <summary>
    /// Kevin MacLeod's "Space 1990" (CC BY 4.0): 111.00 BPM (a comb over the whole song's spectral
    /// flux), the first downbeat at 0.025 s (the low band's attack); the music fades out by bar 121,
    /// the film ends with it at 261.65 s.
    /// </summary>
    internal static readonly Song Space1990 = new("Space 1990", 111.0, 0.025, 0.025 + 121 * 240.0 / 111.0,
        "\"Space 1990\" Kevin MacLeod (incompetech.com)\nLicensed under Creative Commons: By Attribution 4.0",
        "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Space%201990.mp3");

    /// <summary>One bar, s.</summary>
    private const double B = 240.0 / 111.0;

    // ---- the cast ------------------------------------------------------------------------------

    internal static readonly Character Heidi = new("HEIDI", new Color("ff8fc8"),
        new Appearance(BodyBuild.Slim, 2, 1, 1, HairStyle.Ponytail, 6),
        [ItemId.PastelHoodie, ItemId.Jeans, ItemId.CombatBoots]);
    internal static readonly Character Rochat = new("ROCHAT", new Color("c8d86a"),
        new Appearance(BodyBuild.Broad, 1, 0, 2, HairStyle.Short, 8),
        [ItemId.TazJacket, ItemId.TazTrousers, ItemId.CombatBoots]);
    internal static readonly Character Luca = new("LUCA", new Color("ffb84d"),
        new Appearance(BodyBuild.Broad, 5, 0, 3, HairStyle.Quiff, 0),
        [ItemId.BandTee, ItemId.Jeans, ItemId.BlackShades, ItemId.WhiteSneakers]);
    internal static readonly Character Yvonne = new("YVONNE", new Color("6ec8ff"),
        new Appearance(BodyBuild.Curvy, 8, 1, 0, HairStyle.Bun, 1),
        [ItemId.NavyPolo, ItemId.CargoPants, ItemId.RoundGlasses, ItemId.WhiteSneakers]);
    /// <summary>The aliens' commander and his crewmate (#880's looks).</summary>
    internal static readonly Character Zorp = new("ZORP", new Color("7dff6a"), Aliens.Zorp.Looks, Aliens.Zorp.Wears);
    internal static readonly Character Zib = new("ZIB", new Color("b8ff6a"), Aliens.Zib.Looks, Aliens.Zib.Wears);
    internal static readonly Character Tourist = new("TOURIST", new Color("e8e2cf"),
        new Appearance(BodyBuild.Stocky, 4, 1, 0, HairStyle.Short, 7), [ItemId.RainbowTee, ItemId.JoggingShorts, ItemId.HeartShades]);
    internal static readonly Character Skier = new("SKIER", new Color("e8e2cf"),
        new Appearance(BodyBuild.Slim, 6, 2, 1, HairStyle.Long, 3), [ItemId.PastelTee, ItemId.CargoPants, ItemId.BlackBeanie]);
    internal static readonly Character Passer = new("PASSER-BY", new Color("e8e2cf"),
        new Appearance(BodyBuild.Curvy, 1, 4, 5, HairStyle.Bob, 2), [ItemId.WhiteMarcel, ItemId.TartanSkirt, ItemId.MaryJanes]);

    /// <summary>The volunteers' looks (shot 31): what the wardrobe can do.</summary>
    private static readonly Character[] Volunteers =
    [
        new("V1", default, new Appearance(BodyBuild.Slim, 3, 7, 0, HairStyle.Long, 0), [ItemId.GothicRobe, ItemId.PlatformBoots, ItemId.CatEarsBlack, ItemId.GothShades]),
        new("V2", default, new Appearance(BodyBuild.Curvy, 5, 5, 6, HairStyle.BluntBangs, 11), [ItemId.DiscoTop, ItemId.HoloSkirt, ItemId.DiscoShades, ItemId.DiscoPlatforms]),
        new("V3", default, new Appearance(BodyBuild.Broad, 2, 1, 4, HairStyle.Mohawk, 4), [ItemId.BlackMarcel, ItemId.CargoPants, ItemId.CombatBoots, ItemId.SpikedChoker]),
        new("V4", default, new Appearance(BodyBuild.Slim, 8, 2, 2, HairStyle.Bob, 6), [ItemId.PostalJacket, ItemId.PostalSkirt, ItemId.KneeSocks, ItemId.WhiteSneakers]),
        new("V5", default, new Appearance(BodyBuild.Stocky, 4, 0, 3, HairStyle.Short, 1), [ItemId.HiVisVest, ItemId.Jeans, ItemId.HardHat, ItemId.CombatBoots]),
        new("V6", default, new Appearance(BodyBuild.Curvy, 6, 3, 7, HairStyle.Shaggy, 10), [ItemId.GalaxyHoodie, ItemId.BeeStockings, ItemId.PinkSneakers, ItemId.BunnyEars]),
        new("V7", default, new Appearance(BodyBuild.Slim, 1, 6, 1, HairStyle.Spiky, 9), [ItemId.GlitchTee, ItemId.BlackShorts, ItemId.StarGlasses, ItemId.PawGloves]),
    ];

    /// <summary>A speech bubble over actor <paramref name="who"/>'s head; <paramref name="id"/> is its subtitles key.</summary>
    private static Bubble Say(string id, double t, int who, string text, BubbleShape shape = BubbleShape.Say) => new(t, who, text, shape) { Id = id };

    /// <summary>A voice off: the bubble's tail to a point of the frame (0..1 across, 0..1 down), in the speaker's colour.</summary>
    private static Bubble Off(string id, double t, Character who, string text, Vector2 at) => new(t, -1, text) { Id = id, At = at, Colour = who.Colour };

    // ---- moves --------------------------------------------------------------------------------

    /// <summary>Headwear (Cast.Headwear): the pumpkin head, the Santa hat.</summary>
    private const int PumpkinHead = 2, SantaHat = 3;
    private const int Shrug = 5, Dab = 7, Moonwalk = 19, DiscoPoint = 27;
    // fist-fight poses (FootPlayer.FightPose): stances, and 16 + a FightMove
    private const int Guard = 2, HitStun = 6, Dazed = 8, Victory = 9, Jab = 17, JumpKick = 21, Uppercut = 22;

    // ---- places -------------------------------------------------------------------------------

    /// <summary>Heidi's meadow in Lauterbrunnen, west of the main street, the Staubbach cliff beyond (scouted from above).</summary>
    private static readonly Spot Meadow = new(2635820, 1160480);
    /// <summary>Lauterbrunnen's main street, by the meadow.</summary>
    private static readonly Spot MainStreet = new(2635924, 1160470);
    /// <summary>IKEA Riddes: its car park, full of parked cars, the blue facade to its south-east (scouted from above).</summary>
    private static readonly Spot IkeaPark = new(2582300, 1112690);
    /// <summary>The Riddes straight (VS), west end and east end, as in the showcase.</summary>
    private static readonly Spot RiddesW = new(2583100, 1113236), RiddesE = new(2583390, 1113236);
    /// <summary>Nyon: the harbour mole, the lake to the east; the park above the shore.</summary>
    private static readonly Spot NyonMole = new(2507915, 1137230), NyonPark = new(2507810, 1137290);
    /// <summary>Lausanne, Sous-Gare: the avenue down to Ouchy (the story's).</summary>
    private static readonly Spot AvenueTop = new(2537625, 1151830), AvenueFoot = new(2537660, 1151300);
    private static readonly Spot PullySite = new(2542905, 1152292);
    private static readonly Spot Slope = new(2621770, 1089700);
    private static readonly Spot GotthardOn = new(2686150, 1154560);

    // ---- the saucers ---------------------------------------------------------------------------

    /// <summary>
    /// A saucer in flight (#861): launched at <paramref name="speed"/> m/s along <paramref name="heading"/>,
    /// then flown flat out ahead (<paramref name="speed"/> 0: hovering, letting go holds its height).
    /// A flyer mounts on the floor at its spot (or on <paramref name="board"/>: over water, a steep face) before it is launched.
    /// </summary>
    private static Cast Ufo(Spot at, float heading, float speed = 0f, Spot? board = null, System.Func<double, UfoSwitches>? switches = null) => new()
    {
        Ride = RideKind.Ufo, At = at, Heading = heading, Drive = Drive.Fly, Launch = speed, Board = board, Ufo = switches,
        Flight = speed > 0f ? _ => new FlightInput(new Vector2(0f, -1f), 0f, 0f, 0f, 0f, false, speed > 30f, 0f) : _ => default,
    };

    /// <summary>A crate the beam lifts (there are no hay bales in the game).</summary>
    private static Prop Crate(Spot at) => new(at, 20, new Vector3(1.2f, 1.0f, 1.2f), new Color(0.62f, 0.45f, 0.26f)) { Beamable = true };

    /// <summary>A walker on foot that stays an NPC body (its own looks, no keyboard), walking or running ahead from <paramref name="from"/> s.</summary>
    private static Cast Runner(Spot at, float heading, Character? who = null, int seed = 0, bool run = true, double from = 0) => new()
    {
        Who = who, Seed = seed, At = at, Heading = heading, Drive = Drive.Walk,
        Walk = t => (t < from ? Vector2.Zero : new Vector2(0f, 1f), run), Actions = _ => (false, false),
    };

    /// <summary>The four round the pot on the Gornergrat: Heidi west, Luca north, Yvonne east, Rochat south.</summary>
    private static Cast[] Council(int lucaHolds = 0, System.Func<double, bool>? lucaUses = null) =>
    [
        new() { Who = Heidi, At = Crew.Toward(270, 1.6f), Heading = 90 },
        new() { Who = Luca, At = Crew.Toward(0, 1.6f), Heading = 180, Item = lucaHolds, Use = lucaUses },
        new() { Who = Yvonne, At = Crew.Toward(90, 1.6f), Heading = 270 },
        new() { Who = Rochat, At = Crew.Toward(180, 1.6f), Heading = 0 },
    ];

    private static readonly Prop[] Pot =
    [
        new(Crew.Up(0f), 0, default, default) { Item = ItemId.FonduePot, Scale = 1.6f },
    ];

    public static readonly IReadOnlyList<Shot> Shots = new List<Shot>
    {
        // ==== ACT ONE: A QUIET DAY (bars 1-20, the theme) =========================================
        new()
        {
            Number = 1, Name = "A quiet Tuesday", FromBar = 1, Bars = 3, Feature = "the real Switzerland: swissALTI3D relief, sunrise",
            Hour = 7.8, FadeIn = 1.8,
            Keys =
            [
                new(0, Pt.Alt(2627300, 1092800, 3330), Pt.Alt(Matterhorn.E, Matterhorn.N, 3980), 50),
                new(3 * B, Pt.Alt(2626950, 1092720, 3372), Pt.Alt(Matterhorn.E, Matterhorn.N, 4010), 50),
            ],
            Supers = [new(1.0, 4.6, "SWITZERLAND · A QUIET TUESDAY")],
        },
        new()
        {
            Number = 2, Name = "Lavaux", FromBar = 4, Bars = 2, Feature = "real terrain, vineyards, the lake, the CGN steamer",
            Hour = 8.6,
            Cast =
            [
                new()
                {
                    Ride = RideKind.Steamer, At = new Spot(2546900, 1148660), Heading = 298, Board = new Spot(2546900, 1148950),
                    Draught = 1.6f, Launch = 5f, Drive = Drive.Controls, Controls = _ => Pedal(1f), Seed = 70,
                },
            ],
            Keys =
            [
                new(0, Pt.At(2547020, 1149060, 26), Pt.On(0, 0f, 3f, 25f), 35),
                new(2 * B, Pt.At(2547060, 1149045, 26), Pt.On(0, 0f, 3f, 25f), 35),
            ],
            Smooth = 0.4f, Preroll = 4,
            Supers = [new(0.3, 3.4, "LAVAUX · 08:35")],
        },
        new()
        {
            Number = 3, Name = "Heidi's meadow", FromBar = 6, Bars = 2, Feature = "farming: the tractor and its mower; cows (#863)",
            Hour = 9.2,
            Cast =
            [
                new() { Who = Heidi, Ride = Heavy(6), Trailer = 8, Lowered = true, At = Meadow, Heading = 200, Drive = Drive.Controls, Controls = _ => Pedal(0.35f) },
            ],
            Herds = [new Cows(Meadow.Toward(250, 35), Count: 7, Radius: 15f)],
            Keys =
            [
                new(0, Pt.On(0, 9f, 2.2f, -8f), Pt.On(0, 0f, 1.6f), 35),
                new(2 * B, Pt.On(0, 11f, 2.6f, -4f), Pt.On(0, 0f, 1.6f), 35),
            ],
            Smooth = 0.3f, Preroll = 1.5,
            Supers = [new(0.2, 3.0, "LAUTERBRUNNEN · 09:10")],
            Bubbles = [Say("heidi_nothing", 1.4, 0, "Nothing ever happens here.", BubbleShape.Think)],
        },
        new()
        {
            Number = 4, Name = "Harvest", FromBar = 8, Bars = 1, Feature = "farming: the combine harvester",
            Hour = 9.6,
            // on the meadow north of Heidi's, heading south, seen from its left: the cliff beyond
            Cast = [new() { Ride = Heavy(7), At = Meadow.Toward(330, 40), Heading = 190, Drive = Drive.Controls, Controls = _ => Pedal(0.4f), Seed = 31 }],
            Keys =
            [
                new(0, Pt.On(0, -9f, 1.6f, -16f), Pt.On(0, 0f, 2.2f), 35),
                new(B, Pt.On(0, -10f, 1.7f, -12f), Pt.On(0, 0f, 2.2f), 35),
            ],
            Smooth = 0.3f, Preroll = 1.5,
        },
        new()
        {
            Number = 5, Name = "Bern", FromBar = 9, Bars = 1, Feature = "play as a pigeon; a real city",
            Hour = 10.0,
            Cast = [new() { Ride = RideKind.Pigeon, At = KramgasseEast.Up(15), Heading = 268, Drive = Drive.Fly, Launch = 14, Flight = t => Stick(0f, -0.5f, up: t % 0.9 < 0.3 ? 1f : 0f) }],
            Keys =
            [
                new(0, Pt.On(0, 0.4f, 0.6f, 2.2f), Pt.On(0, 0f, -0.8f, -8f), 24),
                new(B, Pt.On(0, 0.2f, 0.5f, 2.0f), Pt.On(0, 0f, -1.2f, -8f), 24),
            ],
            Smooth = 0.05f, Preroll = 0.8,
            Supers = [new(0.2, 1.9, "BERN · 10:00")],
        },
        new()
        {
            Number = 6, Name = "The site", FromBar = 10, Bars = 2, Feature = "real building sites, tower cranes, works machines",
            Hour = 10.5,
            Cast =
            [
                new() { Ride = RideKind.Excavator, At = PullySite, Heading = 30, Drive = Drive.Controls, Controls = _ => Pedal(0.4f, 0.6f), Seed = 32 },
                new() { Ride = RideKind.WheelLoader, At = new Spot(2542930, 1152305), Heading = 300, Drive = Drive.Controls, Controls = _ => Pedal(0.4f, 0.15f), Seed = 4 },
                new() { Ride = RideKind.CompactRoller, At = new Spot(2542880, 1152285), Heading = 80, Drive = Drive.Controls, Controls = _ => Pedal(0.35f), Seed = 5 },
            ],
            Keys =
            [
                new(0, Pt.At(2542935, 1152245, 2.5f), Pt.At(2542895, 1152335, 6), 35),
                new(2 * B, Pt.At(2542940, 1152240, 22), Pt.At(2542895, 1152335, 3), 35),
            ],
            Preroll = 2,
            Supers = [new(0.3, 3.2, "PULLY · 10:30")],
        },
        new()
        {
            Number = 7, Name = "Tremola", FromBar = 12, Bars = 2, Feature = "cars, a real pass road",
            Hour = 10.75, Traffic = 0,
            Cast = [new() { Who = Luca, Ride = Car(Ae86), At = Tremola, Toward = TremolaFoot, Drive = Drive.Road, Route = "tremola", Arc = 280, Skill = 1.1f }],
            Keys =
            [
                new(0, Pt.Road("tremola", 378, -9f, 2.3f), Pt.On(0, 0f, 0.7f), 50),
                new(2 * B, Pt.Road("tremola", 378, -9f, 2.3f), Pt.On(0, 0f, 0.7f), 50),
            ],
            Smooth = 0.1f, Preroll = 5.8,
            Supers = [new(0.3, 3.2, "TREMOLA · 10:45")],
        },
        new()
        {
            Number = 8, Name = "Radio on", FromBar = 14, Bars = 1, Feature = "the cockpit view, the car radio",
            Hour = 10.8, Traffic = 0,
            Cast = [new() { Who = Luca, Ride = Car(Ae86), At = Furka, Toward = FurkaTop, Drive = Drive.Road, Route = "cockpit", Arc = 900, Skill = 1.05f, FirstPerson = true }],
            Keys =
            [
                new(0, Pt.Cockpit(0, -0.7f, 0.12f), Pt.Cockpit(0, 30f, -1.5f), 28),
                new(B, Pt.Cockpit(0, -0.7f, 0.12f), Pt.Cockpit(0, 30f, -1.5f), 28),
            ],
            Preroll = 5,
            Bubbles = [Say("luca_radio", 0.2, 0, "Radio on. Perfect day.")],
        },
        new()
        {
            Number = 9, Name = "Routine", FromBar = 15, Bars = 2, Feature = "the army's vehicles, trucks",
            Hour = 11.0, Traffic = 0,
            Cast =
            [
                new() { Who = Rochat, Ride = Heavy(10), At = Gotthard, Toward = GotthardOn, Drive = Drive.Follow, Route = "gotthard", Arc = 560, Speed = 13 },
                new() { Ride = Heavy(11), At = Gotthard, Toward = GotthardOn, Drive = Drive.Follow, Route = "gotthard", Arc = 542, Speed = 13, Seed = 33 },
                new() { Ride = Heavy(12), At = Gotthard, Toward = GotthardOn, Drive = Drive.Follow, Route = "gotthard", Arc = 522, Speed = 13, Seed = 34 },
            ],
            Keys =
            [
                new(0, Pt.Road("gotthard", 745, -45f, 22f), Pt.On(0, 0f, 2f, 4f), 50),
                new(2 * B, Pt.Road("gotthard", 745, -45f, 22f), Pt.On(0, 0f, 2f, 4f), 50),
            ],
            Smooth = 0.3f, Preroll = 12,
            Supers = [new(0.3, 3.2, "GOTTHARD · 11:00")],
            Bubbles = [Say("rochat_routine", 1.2, 0, "Routine exercise. Nothing to report.")],
        },
        new()
        {
            Number = 10, Name = "Geneva", FromBar = 17, Bars = 2, Feature = "airliners: the A320",
            Hour = 11.1,
            Cast = [new() { Who = Yvonne, Ride = RideKind.A320, At = new Spot(2496388, 1120293, 40), Heading = 226, Drive = Drive.Fly, Launch = 80, Climb = 8, Flight = _ => Stick(lever: 1f) }],
            Keys =
            [
                new(0, Pt.At(2496200, 1120110, 1.7f), Pt.On(0, 0f, 0f), 28),
                new(2 * B, Pt.At(2496200, 1120110, 1.7f), Pt.On(0, 0f, 0f), 28),
            ],
            Smooth = 0.06f, Preroll = 0.4,
            Supers = [new(0.3, 3.2, "GENÈVE · 11:05")],
        },
        new()
        {
            Number = 11, Name = "Something up there", FromBar = 19, Bars = 2, Feature = "flight over the Aletsch; the first UFO",
            Hour = 12.5,
            Cast =
            [
                new() { Who = Yvonne, Ride = RideKind.A320, At = Spot.Alt(2646200, 1151600, 3700), Heading = 145, Drive = Drive.Fly, Launch = 90, Flight = _ => Stick(lever: 0.8f) },
                // a saucer flashes past ahead of her, left to right
                Ufo(Spot.Alt(2646400, 1151150, 3740), 60, 60f),
            ],
            Keys =
            [
                new(0, Pt.On(0, -34f, 7f, 30f), Pt.On(0, 0f, -2f, -10f), 35),
                new(2 * B, Pt.On(0, -28f, 5f, 24f), Pt.On(0, 0f, -2f, -10f), 35),
            ],
            Smooth = 0.2f, Preroll = 1,
            Bubbles = [Say("yvonne_saucer", 1.6, 0, "Tower... is that a saucer?", BubbleShape.Whisper)],
        },

        // ==== ACT TWO: THEY ARRIVE (bars 21-52, the groove) =======================================
        new()
        {
            Number = 12, Name = "The fleet", FromBar = 21, Bars = 3, Feature = "UFOs: the fleet and the mothership",
            Hour = 12.8,
            // four saucers in formation out of the Matterhorn's sky (a sighting's flyover, half-way across
            // as the camera rolls), the mothership hanging over the peak behind them
            Ufos = [new Saucers(Occasions.SightingKind.Flyover, new Spot(2623500, 1092100, 350), 75, Count: 4, From: 27)],
            Craft = [new Craft(Spot.Alt(2618200, 1091800, 4900), 0) { Mothership = true }],
            Keys =
            [
                new(0, Pt.Alt(2626950, 1092720, 3372), Pt.Alt(Matterhorn.E, Matterhorn.N, 4250), 35),
                new(3 * B, Pt.Alt(2626900, 1092700, 3376), Pt.Alt(2624000, 1092150, 3300), 28),
            ],
            Smooth = 0.4f, Preroll = 0.5,
            Supers = [new(0.4, 3.0, "12:48")],
        },
        new()
        {
            Number = 13, Name = "Over Zürich", FromBar = 24, Bars = 2, Feature = "city traffic; the mothership's shadow",
            Hour = 13.0, Traffic = 80,
            // over the gridlock, the camera tilts up to the mothership hanging over the city
            Craft = [new Craft(new Spot(2683700, 1246650, 260), 0) { Mothership = true }],
            Keys =
            [
                new(0, Pt.At(2683480, 1246900, 32), Pt.At(2683570, 1246790, 1), 35),
                new(2 * B, Pt.At(2683470, 1246895, 30), Pt.At(2683700, 1246650, 240), 28),
            ],
            Preroll = 4,
            Supers = [new(0.3, 3.2, "ZÜRICH · 13:00")],
        },
        new()
        {
            Number = 14, Name = "My cows", FromBar = 26, Bars = 3, Feature = "the tractor beam lifts a cow and a crate; the herd",
            Hour = 13.3,
            Cast =
            [
                Runner(Meadow.Toward(90, 30), 270, Heidi),
            ],
            // a saucer over the meadow, its beam lifting a cow and a crate; the herd grazes round it
            Craft = [new Craft(Meadow.Toward(250, 22).Up(16f), 70) { Switches = UfoSwitches.BeamBetween(0.6, 99) }],
            Herds = [new Cows(Meadow.Toward(250, 22), Count: 1, Radius: 1.2f), new Cows(Meadow.Toward(235, 45), Count: 6, Radius: 14f)],
            Props = [Crate(Meadow.Toward(250, 22).Toward(330, 2.2f))],
            Keys =
            [
                new(0, Ground(Meadow, 80, 40, 1.6f), Ground(Meadow, 250, 20, 8f), 28),
                new(3 * B, Ground(Meadow, 82, 37, 1.5f), Ground(Meadow, 250, 25, 18f), 28),
            ],
            Smooth = 0.2f, Preroll = 0.5,
            Bubbles = [Say("heidi_cows", 2.0, 0, "HEY! Those are MY cows!", BubbleShape.Shout)],
        },
        new()
        {
            Number = 15, Name = "Click and collect", FromBar = 29, Bars = 2, Feature = "parked cars at IKEA Riddes; the beam takes one",
            Hour = 14.5,
            // a saucer over the car park: a parked car rises out of its bay into the beam
            Craft = [new Craft(IkeaPark.Up(17f), 40) { Switches = UfoSwitches.BeamBetween(0.5, 99) }],
            Props = [new(IkeaPark, 30, default, default) { Vehicle = Car(Yaris) }],
            Keys =
            [
                // from the far side of the car park, clear of the IKEA pylon, the blue facade beyond
                new(0, Pt.At(2582262, 1112735, 2.5f), Pt.At(IkeaPark.E, IkeaPark.N, 5f), 28),
                new(2 * B, Pt.At(2582264, 1112733, 2.5f), Pt.At(IkeaPark.E, IkeaPark.N, 14f), 28),
            ],
            Smooth = 0.3f, Preroll = 0.5,
            Supers = [new(0.3, 3.0, "RIDDES · 14:30")],
        },
        new()
        {
            Number = 16, Name = "Chillon", FromBar = 31, Bars = 2, Feature = "the steamer, the jetski, the castle; a UFO",
            Hour = 14.8,
            Cast =
            [
                new()
                {
                    Ride = RideKind.Steamer, At = new Spot(2560620, 1140420), Heading = 318, Board = ChillonBoard,
                    Draught = 1.6f, Drive = Drive.Controls, Controls = _ => Pedal(1f), Seed = 71,
                },
                new() { Who = Tourist, Ride = RideKind.Jetski, At = new Spot(2560700, 1140360), Heading = 250, Board = ChillonBoard, Launch = 15, Drive = Drive.Controls, Controls = _ => Pedal(1f) },
            ],
            // a saucer hangs over the steamer
            Craft = [new Craft(Spot.Alt(2560650, 1140450, 405), 300)],
            Keys =
            [
                new(0, Pt.Alt(2560560, 1140300, 373.6f), Pt.Alt(2560662, 1140440, 382), 35),
                new(2 * B, Pt.Alt(2560575, 1140296, 373.8f), Pt.Alt(2560672, 1140442, 386), 35),
            ],
            Smooth = 0.3f, Preroll = 1.5,
            Bubbles = [Say("tourist_cruise", 1.0, 1, "Is this part of the cruise?")],
        },
        new()
        {
            Number = 17, Name = "Not the AE86", FromBar = 33, Bars = 2, Feature = "the beam takes a car",
            Hour = 15.0, Traffic = 0,
            Cast =
            [
                new() { Who = Luca, At = Tremola, Toward = TremolaFoot, Route = "tremola", Arc = 7, Drive = Drive.Stand, Fight = _ => Dazed },
            ],
            // the beam lifts the parked AE86 off the cobbles
            Props = [new(default, 0, default, default) { Vehicle = Car(Ae86), Route = "tremola", Arc = 0 }],
            Craft = [new Craft(Tremola.Up(16f), 0) { Switches = UfoSwitches.BeamBetween(0.3, 99) }],
            Keys =
            [
                new(0, Pt.On(0, 2.5f, 1.2f, -5f), Tremola.Up(1.5f), 28),
                new(2 * B, Pt.On(0, 3.0f, 0.9f, -6f), Tremola.Up(7f), 28),
            ],
            Smooth = 0.3f, Preroll = 1,
            Bubbles = [Say("luca_ae86", 0.8, 0, "NOT THE AE86!", BubbleShape.Shout)],
        },
        new()
        {
            Number = 18, Name = "Pigeon chase", FromBar = 35, Bars = 2, Feature = "the pigeon; a UFO in the street",
            Hour = 15.3,
            Cast =
            [
                new() { Ride = RideKind.Pigeon, At = KramgasseEast.Up(12), Heading = 268, Drive = Drive.Fly, Launch = 15, Flight = t => Stick(0f, -0.4f, up: t % 0.8 < 0.3 ? 1f : 0f) },
                // a saucer right behind it down the Kramgasse
                Ufo(KramgasseEast.Toward(90, 40).Up(16), 268, 16f),
            ],
            Keys =
            [
                new(0, Pt.On(0, 0.5f, 0.2f, -2.6f), Pt.On(0, 0f, 0.6f, 10f), 24),
                new(2 * B, Pt.On(0, 0.4f, 0.3f, -2.2f), Pt.On(0, 0f, 3f, 20f), 24),
            ],
            Smooth = 0.05f, Preroll = 0.8,
        },
        new()
        {
            Number = 19, Name = "Not routine", FromBar = 37, Bars = 2, Feature = "the army; emotes",
            Hour = 15.5, Traffic = 0,
            Cast =
            [
                new() { Ride = Heavy(10), At = Gotthard, Toward = GotthardOn, Route = "gotthard", Arc = 600, Drive = Drive.Stand, Seed = 36 },
                new() { Who = Rochat, At = Gotthard, Toward = GotthardOn, Route = "gotthard", Arc = 608, Drive = Drive.Stand, Dance = Salute },
            ],
            Keys =
            [
                new(0, Pt.On(1, 0.8f, 1.5f, -3.6f), Pt.On(1, 0f, 1.6f), 35),
                new(2 * B, Pt.On(1, 0.5f, 1.3f, -2.6f), Pt.On(1, 0f, 1.75f), 35),
            ],
            Smooth = 0.3f, Preroll = 0.5,
            Bubbles = [Say("rochat_not_routine", 0.6, 1, "This is NOT routine.", BubbleShape.Shout)],
        },
        new()
        {
            Number = 20, Name = "The pointy one", FromBar = 39, Bars = 2, Feature = "the mothership over the Matterhorn",
            Hour = 15.8,
            // the mothership parked over the summit, its beam on the peak
            Craft = [new Craft(Spot.Alt(Matterhorn.E, Matterhorn.N, 4800), 0) { Mothership = true, Switches = UfoSwitches.BeamBetween(1.0, 99) }],
            Keys =
            [
                new(0, Pt.Alt(2627300, 1092800, 3330), Pt.Alt(Matterhorn.E, Matterhorn.N, 4600), 85),
                new(2 * B, Pt.Alt(2627300, 1092800, 3330), Pt.Alt(Matterhorn.E, Matterhorn.N, 4700), 60),
            ],
            Smooth = 0.4f, Preroll = 0.5,
            Bubbles = [Off("zorp_pointy", 0.8, Zorp, "We take the pointy mountain.", new Vector2(0.62f, 0.12f))],
        },
        new()
        {
            Number = 21, Name = "Keep calm", FromBar = 41, Bars = 2, Feature = "on foot: running; food items",
            Hour = 16.0, Traffic = 0,
            Cast =
            [
                Runner(AvenueTop, 0, seed: 41) with { Route = "avenue", Toward = AvenueFoot, Arc = 60 },
                Runner(AvenueTop, 0, seed: 42) with { Route = "avenue", Toward = AvenueFoot, Arc = 55 },
                Runner(AvenueTop, 0, seed: 43) with { Route = "avenue", Toward = AvenueFoot, Arc = 51 },
                new() { Who = Passer, At = AvenueTop, Route = "avenue", Toward = AvenueFoot, Arc = 72, Item = (int)ItemId.Chocolate, Use = t => t % 1.6 > 1.0 },
            ],
            Keys =
            [
                new(0, Pt.Road("avenue", 80, 1.5f, 1.5f), Pt.On(0, 0f, 1.4f), 35),
                new(2 * B, Pt.Road("avenue", 80, 1.8f, 1.5f), Pt.On(3, 0f, 1.4f), 35),
            ],
            Smooth = 0.3f, Preroll = 0.5,
            Supers = [new(0.3, 3.0, "LAUSANNE · 16:00")],
            Bubbles = [Say("passer_chocolate", 2.2, 3, "I'm finishing my chocolate first.")],
        },
        new()
        {
            Number = 22, Name = "Dodge", FromBar = 43, Bars = 2, Feature = "the A320 dodges a UFO",
            Hour = 16.2,
            Cast =
            [
                new() { Who = Yvonne, Ride = RideKind.A320, At = Spot.Alt(2646200, 1151600, 3700), Heading = 145, Drive = Drive.Fly, Launch = 90, Flight = t => Stick(t > 1.5 && t < 3.5 ? 0.8f : 0f, lever: 0.8f) },
                // a saucer cuts across her nose
                Ufo(Spot.Alt(2646150, 1151250, 3720), 80, 60f),
            ],
            Keys =
            [
                new(0, Pt.On(0, 26f, 4f, 30f), Pt.On(0, 0f, 0f, -20f), 35),
                new(2 * B, Pt.On(0, 22f, 3f, 26f), Pt.On(0, 0f, 0f, -20f), 35),
            ],
            Smooth = 0.25f, Preroll = 1,
            Bubbles = [Say("yvonne_dodge", 1.0, 0, "Ladies and gentlemen, hold on.")],
        },
        new()
        {
            Number = 23, Name = "The map", FromBar = 45, Bars = 2, Feature = "the game's map and download screen",
            Hour = 16.4,
            // the game's own map screen (what is downloaded) over the valley seen from straight above
            Screens = [new(0.0, 2 * B, GameScreen.Map)],
            Keys =
            [
                new(0, Pt.At(2635900, 1160000, 2200), Pt.At(2635900, 1160000, 0), 24),
                new(2 * B, Pt.At(2635900, 1160000, 1500), Pt.At(2635900, 1160001, 0), 24),
            ],
            Preroll = 0.5,
            Bubbles = [Off("rochat_everywhere", 0.6, Rochat, "They're everywhere. All 26 cantons.", new Vector2(0.9f, 0.85f))],
        },
        new()
        {
            Number = 24, Name = "Time for the horn", FromBar = 47, Bars = 2, Feature = "items: the alphorn",
            Hour = 16.6,
            Cast = [new() { Who = Heidi, At = Meadow, Heading = 80, Item = (int)ItemId.Alphorn }],
            Keys =
            [
                new(0, Pt.On(0, 0.8f, 1.5f, -3.4f), Pt.On(0, 0f, 1.5f), 35),
                new(2 * B, Pt.On(0, 0.5f, 1.5f, -2.6f), Pt.On(0, 0f, 1.55f), 35),
            ],
            Smooth = 0.3f, Preroll = 0.5,
            Bubbles = [Say("heidi_horn", 0.5, 0, "Right. Time for the horn.")],
        },
        new()
        {
            Number = 25, Name = "The call", FromBar = 49, Bars = 2, Feature = "the alphorn's call, heard 1.5 km off; sound",
            Hour = 16.8,
            Cast = [new() { Who = Heidi, At = Meadow, Heading = 250, Item = (int)ItemId.Alphorn, Use = _ => true }],
            ItemCues = [new(0.3, 0, ItemEventKind.Horn)],
            Keys =
            [
                new(0, Pt.On(0, 1.4f, 1.2f, 3.5f), Pt.On(0, 0f, 6f, -60f), 28),
                new(2 * B, Pt.On(0, 3f, 6f, 16f), Pt.On(0, 0f, 30f, -200f), 28),
            ],
            Smooth = 0.4f, Preroll = 0.5,
        },
        new()
        {
            Number = 26, Name = "Was that a horn", FromBar = 51, Bars = 2, Feature = "skis",
            Hour = 17.0,
            Cast =
            [
                new()
                {
                    Who = Skier, Ride = RideKind.Skis, At = Slope, Heading = 290, Drive = Drive.Controls,
                    Controls = t => t < 2.5 ? new RideInput(0f, 0f, 0.15f * Mathf.Sin((float)t * 2.4f), false) : new RideInput(0f, 1f, 0.7f, false),
                },
            ],
            Keys =
            [
                new(0, Pt.On(0, 2.5f, 1.5f, 5f), Pt.On(0, -0.5f, 0.3f, -10f), 28),
                new(2 * B, Pt.On(0, 3.0f, 1.4f, 2f), Pt.On(0, 0f, 1.0f), 28),
            ],
            Smooth = 0.1f, Preroll = 1.5,
            Bubbles = [Say("skier_horn", 2.6, 0, "Was that... an alphorn?", BubbleShape.Think)],
        },

        // ==== THE COUNCIL (bars 53-61, the breakdown) =============================================
        new()
        {
            Number = 27, Name = "The council", FromBar = 53, Bars = 3, Feature = "multiplayer: friends together; the Gornergrat",
            Hour = 18.2,
            Cast = Council(),
            Props = Pot,
            Keys = Orbit(Crew, 6.5f, 6f, 1.6f, 1.8f, 120f, 75f, 3 * B, 28, lookUp: 1.0f),
            Preroll = 0.5,
            Supers = [new(0.2, 2.6, "GORNERGRAT · 18:10")],
            Bubbles =
            [
                Say("rochat_options", 0.5, 3, "Options?"),
                Say("luca_cars", 1.9, 1, "Fast cars."),
                Say("yvonne_planes", 3.3, 2, "Big planes."),
                Say("heidi_fondue", 4.7, 0, "Fondue first."),
            ],
        },
        new()
        {
            Number = 28, Name = "Fondue heals", FromBar = 56, Bars = 2, Feature = "cooking: the fondue heals everyone near",
            Hour = 18.3,
            Cast = Council((int)ItemId.Fondue, t => t > 1.0 && t % 1.4 < 0.8),
            Props = Pot,
            ItemCues = [new(0.2, 0, ItemEventKind.Fondue)],
            Keys =
            [
                new(0, Ground(Crew, 315, 4.6f, 1.9f), Ground(Crew, 0, 0, 0.5f), 35),
                new(2 * B, Ground(Crew, 320, 4.2f, 2.0f), Pt.On(1, 0f, 1.4f), 35),
            ],
            Smooth = 0.3f, Preroll = 0.5,
            Bubbles = [Say("luca_better", 2.4, 1, "Ah. Much better.")],
        },
        new()
        {
            Number = 29, Name = "Swiss-made barricade", FromBar = 58, Bars = 2, Feature = "building with the hammer: sandbag and stone walls",
            Hour = 17.8,
            Cast = [new() { Who = Rochat, At = Crew.Toward(200, 14), Heading = 20, Item = (int)ItemId.Hammer, Use = t => t % 0.7 < 0.35 }],
            Props =
            [
                new(Crew.Toward(205, 10), 205, default, default) { Piece = Build.PieceKind.Wall, Material = Build.BuildMaterial.Sandbag },
                new(Crew.Toward(185, 10.5f), 185, default, default) { Piece = Build.PieceKind.HalfWall, Material = Build.BuildMaterial.Sandbag },
                new(Crew.Toward(225, 10.5f), 225, default, default) { Piece = Build.PieceKind.Wall, Material = Build.BuildMaterial.Stone },
            ],
            Keys =
            [
                new(0, Pt.On(0, -4f, 1.6f, -1f), Pt.On(0, 0f, 1.3f, -2f), 28),
                new(2 * B, Pt.On(0, -4.6f, 1.9f, 0.5f), Pt.On(0, 0f, 1.3f, -2.5f), 28),
            ],
            Smooth = 0.3f, Preroll = 0.5,
            Bubbles = [Say("rochat_barricade", 0.6, 0, "Swiss-made barricade. Very neutral.")],
        },
        new()
        {
            Number = 30, Name = "IKEA", FromBar = 60, Bars = 2, Feature = "IKEA and the shops; items: the Blåhaj",
            Hour = 17.2,
            // from the blue facade toward the car park
            Cast = [new() { Who = Luca, At = new Spot(2582250, 1112640), FromDoor = true, Drive = Drive.Walk, Walk = t => (new Vector2(0f, t > 0.6 ? 0.6f : 0f), false), Item = (int)ItemId.Blahaj }],
            Keys =
            [
                new(0, Pt.On(0, 2f, 1.7f, -7f), Pt.On(0, 0f, 1.3f), 28),
                new(2 * B, Pt.On(0, 1.5f, 1.7f, -5.5f), Pt.On(0, 0f, 1.3f), 28),
            ],
            Smooth = 0.3f, Preroll = 0.4,
            Supers = [new(0.2, 3.0, "IKEA RIDDES · 17:10")],
            Bubbles = [Say("luca_ikea", 1.0, 0, "IKEA has everything.")],
        },

        // ==== MOBILISATION (bars 62-73, the build) ================================================
        new()
        {
            Number = 31, Name = "Volunteers", FromBar = 62, Bars = 2, Feature = "character looks: faces, hair, clothes; emotes",
            Hour = 7.2,
            Cast = Volunteers.Select((v, i) => new Cast
            {
                Who = v, At = NyonPark.Toward(20, (i - 3) * 1.3f), Heading = 290,
                Dance = new[] { FistPump, Salute, Dab, Cheer, Salute, Wave, FistPump }[i],
            }).ToArray(),
            Keys =
            [
                new(0, Ground(NyonPark, 330, 5.5f, 1.5f), Ground(NyonPark, 200, 2.5f, 1.3f), 28),
                new(2 * B, Ground(NyonPark, 260, 5.5f, 1.5f), Ground(NyonPark, 20, 2.5f, 1.3f), 28),
            ],
            Smooth = 0.3f, Preroll = 0.5,
            Supers = [new(0.2, 3.0, "NYON · 07:10")],
        },
        new()
        {
            Number = 32, Name = "The army rolls", FromBar = 64, Bars = 2, Feature = "army vehicles: Duro, G-Class, Trakker",
            Hour = 7.0, Traffic = 0,
            Cast =
            [
                new() { Who = Rochat, Ride = Heavy(10), At = RiddesW, Toward = RiddesE, Drive = Drive.Follow, Route = "riddes", Arc = 80, Speed = 15 },
                new() { Ride = Heavy(11), At = RiddesW, Toward = RiddesE, Drive = Drive.Follow, Route = "riddes", Arc = 62, Speed = 15, Seed = 37 },
                new() { Ride = Heavy(12), At = RiddesW, Toward = RiddesE, Drive = Drive.Follow, Route = "riddes", Arc = 42, Speed = 15, Seed = 38 },
                new() { Ride = Heavy(10), At = RiddesW, Toward = RiddesE, Drive = Drive.Follow, Route = "riddes", Arc = 24, Speed = 15, Seed = 39 },
            ],
            Keys =
            [
                new(0, Pt.Road("riddes", 150, -6f, 1.2f), Pt.On(0, 0f, 1.5f), 35),
                new(2 * B, Pt.Road("riddes", 150, -6f, 1.2f), Pt.On(2, 0f, 1.5f), 35),
            ],
            Smooth = 0.2f, Preroll = 2,
            Supers = [new(0.2, 3.0, "RIDDES · 07:00")],
        },
        new()
        {
            Number = 33, Name = "Farmers' brigade", FromBar = 66, Bars = 2, Feature = "tractors and trailers, the combine on the road",
            Hour = 7.3, Traffic = 0,
            Cast =
            [
                new() { Who = Heidi, Ride = Heavy(6), Trailer = 9, At = RiddesW, Toward = RiddesE, Drive = Drive.Follow, Route = "riddes", Arc = 90, Speed = 9 },
                new() { Ride = Heavy(6), Trailer = 6, At = RiddesW, Toward = RiddesE, Drive = Drive.Follow, Route = "riddes", Arc = 66, Speed = 9, Seed = 44 },
                new() { Ride = Heavy(7), At = RiddesW, Toward = RiddesE, Drive = Drive.Follow, Route = "riddes", Arc = 42, Speed = 9, Seed = 45 },
            ],
            Keys =
            [
                new(0, Pt.On(0, -2.5f, 2.6f, -9f), Pt.On(0, 0f, 2.2f), 28),
                new(2 * B, Pt.On(0, -3.0f, 2.4f, -8f), Pt.On(0, 0f, 2.2f), 28),
            ],
            Smooth = 0.3f, Preroll = 3,
            Bubbles = [Say("heidi_farmers", 0.6, 0, "Farmers! With me!", BubbleShape.Shout)],
        },
        new()
        {
            Number = 34, Name = "Extrafahrt", FromBar = 68, Bars = 2, Feature = "buses: the PostAuto coach",
            Hour = 8.0, Traffic = 0,
            Cast = [new() { Ride = Heavy(Setra), At = Gotthard, Toward = GotthardOn, Drive = Drive.Follow, Route = "gotthard", Arc = 560, Speed = 14, Seed = 46 }],
            Keys =
            [
                new(0, Pt.Road("gotthard", 745, -45f, 22f), Pt.On(0, 0f, 2f, 6f), 50),
                new(2 * B, Pt.Road("gotthard", 745, -45f, 22f), Pt.On(0, 0f, 2f, 6f), 50),
            ],
            Smooth = 0.3f, Preroll = 12,
        },
        new()
        {
            Number = 35, Name = "Pick a car", FromBar = 70, Bars = 2, Feature = "the 30 cars of the Initial D roster",
            Hour = 8.4, Traffic = 0,
            Cast =
            [
                new() { Who = Luca, At = RiddesW, Toward = RiddesE, Route = "garage", Arc = 136, Drive = Drive.Stand, Dance = DiscoPoint },
                .. new[] { Fd3s, Gc8, 3, Bnr32, 5, S13, 7, 8, Evo3, Nsx, 16, Supra, 22, 23, 24 }.Select((car, i) => new Cast
                {
                    Ride = Car(car), At = RiddesW, Toward = RiddesE, Route = "garage", Arc = 128 - i * 7f, Drive = Drive.Stand, Seed = 80 + i,
                }),
            ],
            Keys =
            [
                new(0, Pt.Road("garage", 20, -5f, 1.4f), Pt.Road("garage", 40, 0f, 0.8f), 28),
                new(2 * B, Pt.Road("garage", 120, -5f, 1.6f), Pt.Road("garage", 140, 0f, 1.2f), 28),
            ],
            Smooth = 0.3f, Preroll = 0.5,
            Bubbles = [Say("luca_pick", 2.4, 0, "Pick a car. Any car.")],
        },
        new()
        {
            Number = 36, Name = "Two wheels", FromBar = 72, Bars = 2, Feature = "the 74 motorbikes",
            Hour = 9.0, Traffic = 0,
            Cast = new[] { R1, Monster, AfricaTwin, 20, 33, 38 }.Select((bike, i) => new Cast
            {
                Ride = Moto(bike), At = Furka, Toward = FurkaTop, Drive = Drive.Follow, Route = "furka", Arc = 1390 - i * 10f, Speed = 21, Seed = 90 + i,
            }).ToArray(),
            Keys =
            [
                new(0, Pt.Road("furka", 1497, -9f, 6f), Pt.On(0, 0f, 0.9f), 35),
                new(2 * B, Pt.Road("furka", 1497, -9f, 6f), Pt.On(3, 0f, 0.9f), 35),
            ],
            Smooth = 0.2f, Preroll = 6,
        },

        // ==== THE DROP (bars 74-81, the second breakdown) =========================================
        new()
        {
            Number = 37, Name = "Everybody in", FromBar = 74, Bars = 2, Feature = "the military freighter: the Battle Royale's plane",
            Hour = 13.5,
            Cast =
            [
                new() { Who = Yvonne, Ride = RideKind.Freighter, At = new Spot(2497250, 1121000), Heading = 226, Drive = Drive.Stand, Doors = 1 << 3 },
                Runner(new Spot(2497250, 1121000).Toward(46, 40), 226, Rochat, run: false),
                Runner(new Spot(2497250, 1121000).Toward(50, 44), 226, Luca, run: false),
            ],
            Keys =
            [
                new(0, Pt.On(0, -14f, 2.0f, -22f), Pt.On(0, 0f, 3.5f, -8f), 35),
                new(2 * B, Pt.On(0, -12f, 2.0f, -20f), Pt.On(0, 0f, 3.5f, 4f), 35),
            ],
            Preroll = 0.5,
            Supers = [new(0.2, 3.0, "GENÈVE · 13:30")],
            Bubbles = [Say("yvonne_board", 0.8, 0, "Everybody in. Next stop: the saucer.")],
        },
        new()
        {
            Number = 38, Name = "Above the Alps", FromBar = 76, Bars = 2, Feature = "the freighter in flight, its ramp open",
            Hour = 13.8,
            Cast = [new() { Who = Yvonne, Ride = RideKind.Freighter, At = Spot.Alt(2624000, 1104000, 3300), Heading = 175, Doors = 1 << 3, Drive = Drive.Fly, Launch = 72, Flight = _ => Stick(lever: 0.6f) }],
            Keys =
            [
                new(0, Pt.On(0, -48f, 8f, 18f), Pt.On(0, 0f, 0f, 4f), 35),
                new(2 * B, Pt.On(0, -38f, 4f, 28f), Pt.On(0, 0f, -1f, 6f), 35),
            ],
            Smooth = 0.2f, Preroll = 5,
        },
        new()
        {
            Number = 39, Name = "Go go go", FromBar = 78, Bars = 2, Feature = "the Battle Royale drop: the wingsuit",
            Hour = 13.85,
            Cast =
            [
                new() { Who = Yvonne, Ride = RideKind.Freighter, At = Spot.Alt(2624000, 1104000, 3300), Heading = 175, Doors = 1 << 3, Drive = Drive.Fly, Launch = 72, Flight = _ => Stick(lever: 0.6f) },
                .. new[] { Rochat, Luca, Heidi, Volunteers[2] }.Select((who, i) => new Cast
                {
                    Who = who, Ride = RideKind.Wingsuit, At = Spot.Alt(2624000, 1104000, 3296 - i * 2).Toward(355, 16 + i * 6).Toward(85, (i % 2 - 0.5f) * 3f),
                    Heading = 175, Drive = Drive.Fly, Launch = 58 - i * 3, Climb = -8, Flight = _ => Stick(0f, -0.3f),
                }),
            ],
            Keys =
            [
                new(0, Pt.On(0, 6f, 4f, 26f), Pt.On(1, 0f, 0f), 35),
                new(2 * B, Pt.On(0, 6f, 4f, 26f), Pt.On(3, 0f, 0f), 35),
            ],
            Smooth = 0.05f, Preroll = 0.05,
            Bubbles = [Say("rochat_go", 0.3, 1, "GO GO GO!", BubbleShape.Shout)],
        },
        new()
        {
            Number = 40, Name = "Canopies", FromBar = 80, Bars = 2, Feature = "the parachute",
            Hour = 13.9,
            Cast = Enumerable.Range(0, 3).Select(i => new Cast
            {
                Who = new[] { Rochat, Luca, Heidi }[i], Ride = RideKind.Parachute,
                At = Spot.Alt(2625190 + (i - 1) * 14, 1099440 + i * 10, 2070 + i * 6), Heading = 180,
                Drive = Drive.Fly, Launch = 8, Flight = _ => Stick(0.1f),
            }).ToArray(),
            Keys =
            [
                new(0, Pt.On(1, 6f, 1f, 10f), Pt.On(1, 0f, 3f), 35),
                new(2 * B, Pt.On(1, 9f, 0f, 6f), Pt.On(1, 0f, 3f), 35),
            ],
            Smooth = 0.2f, Preroll = 0.5,
        },

        // ==== THE COUNTER-ATTACK (bars 82-113, the second groove) =================================
        new()
        {
            Number = 41, Name = "Loot", FromBar = 82, Bars = 2, Feature = "Battle Royale loot on the ground; the zone",
            Hour = 15.0,
            Cast = [new() { Who = Luca, At = Meadow, Heading = 200, Item = (int)ItemId.Blahaj, Dance = Shrug }],
            Props =
            [
                new(Meadow.Toward(170, 2.2f), 30, default, default) { Item = ItemId.Shotgun, Scale = 1.4f },
                new(Meadow.Toward(215, 2.0f), 80, default, default) { Item = ItemId.SmokeCanister, Scale = 1.6f },
                new(Meadow.Toward(240, 2.6f), 140, default, default) { Item = ItemId.FlareGun, Scale = 1.6f },
                new(Meadow.Toward(150, 3.0f), 10, default, default) { Item = ItemId.FirstAidKit, Scale = 1.6f },
                new(Meadow.Toward(195, 3.4f), 60, default, default) { Item = ItemId.FonduePot, Scale = 1.4f },
            ],
            Zone = (Meadow.Toward(250, 200), 230f),
            Keys =
            [
                new(0, Pt.On(0, 0.8f, 1.6f, -4.0f), Pt.On(0, 0f, 0.8f, 1.5f), 28),
                new(2 * B, Pt.On(0, 1.4f, 1.9f, -4.6f), Pt.On(0, 0f, 1.2f), 28),
            ],
            Smooth = 0.3f, Preroll = 0.5,
            Bubbles = [Say("luca_blahaj", 0.8, 0, "Who packed the Blåhaj?")],
        },
        new()
        {
            Number = 42, Name = "The zone", FromBar = 84, Bars = 2, Feature = "the Battle Royale zone, players and bots running in",
            Hour = 15.05,
            Cast =
            [
                .. Enumerable.Range(0, 6).Select(i => Runner(Meadow.Toward(220 + i * 9, 40 + i * 6), 250, seed: 50 + i)),
            ],
            // the saucer landed in the middle of the zone, its ramp down
            Craft = [new Craft(Meadow.Toward(250, 200), 60) { Switches = _ => new UfoSwitches(Ramp: true) }],
            Zone = (Meadow.Toward(250, 200), 230f),
            Keys =
            [
                new(0, Ground(Meadow, 70, 60, 40f), Ground(Meadow, 250, 200, 0f), 28),
                new(2 * B, Ground(Meadow, 70, 40, 55f), Ground(Meadow, 250, 240, 0f), 28),
            ],
            Smooth = 0.4f, Preroll = 1,
        },
        new()
        {
            Number = 43, Name = "Pursuit", FromBar = 86, Bars = 2, Feature = "cars chase a UFO; racing",
            Hour = 15.2, Traffic = 0,
            Cast =
            [
                new() { Who = Luca, Ride = Car(Fd3s), At = Tremola, Toward = TremolaFoot, Drive = Drive.Road, Route = "tremola", Arc = 505, Skill = 1.1f },
                new() { Ride = Car(Bnr32), At = Tremola, Toward = TremolaFoot, Drive = Drive.Road, Route = "tremola", Arc = 490, Skill = 1.15f, Aggression = 0.8f, Seed = 47 },
                // the saucer flees down the hairpins ahead of them
                Ufo(Tremola.Up(30), 180, 20f),
            ],
            Keys =
            [
                new(0, Pt.On(1, 3.2f, 1.0f, 1.5f), Pt.On(1, -0.5f, 0.8f, -6f), 35),
                new(2 * B, Pt.On(1, 3.0f, 1.1f, -1.0f), Pt.On(1, -0.5f, 0.8f, -8f), 35),
            ],
            Smooth = 0.15f, Preroll = 3,
        },
        new()
        {
            Number = 44, Name = "The pack", FromBar = 88, Bars = 2, Feature = "races with AI drivers",
            Hour = 15.2, Traffic = 0,
            Cast = Grid([Supra, Bnr32, Fd3s, Evo3, Nsx, S13], Furka, FurkaTop, "pack", 1380, goAt: 0),
            Keys =
            [
                new(0, Pt.Road("pack", 1500, 0f, 80f), Pt.Road("pack", 1500, 0f, 0f), 35),
                new(2 * B, Pt.Road("pack", 1500, 0f, 84f), Pt.Road("pack", 1500, 0f, 0f), 35),
            ],
            Preroll = 6,
        },
        new()
        {
            Number = 45, Name = "Lean", FromBar = 90, Bars = 1, Feature = "motorbikes lean through a bend",
            Hour = 15.3, Traffic = 0,
            Cast =
            [
                new() { Ride = Car(Bnr32), At = Furka, Toward = FurkaTop, Drive = Drive.Follow, Route = "lean", Arc = 460, Speed = 20, Seed = 48 },
                new() { Ride = Moto(Monster), At = Furka, Toward = FurkaTop, Drive = Drive.Follow, Route = "lean", Arc = 430, Speed = 23, Seed = 15 },
            ],
            Keys =
            [
                new(0, Pt.On(1, -3.6f, 0.9f, 3.5f), Pt.On(0, 0f, 0.8f), 35),
                new(B, Pt.On(1, -3.4f, 0.9f, 2.0f), Pt.On(0, 0f, 0.8f), 35),
            ],
            Smooth = 0.12f, Preroll = 6,
        },
        new()
        {
            Number = 46, Name = "Downhill", FromBar = 91, Bars = 1, Feature = "the road bike",
            Hour = 15.3, Traffic = 0,
            Cast = [new() { Who = Volunteers[3], Ride = RideKind.RoadBike, At = Tremola, Toward = TremolaFoot, Drive = Drive.Road, Route = "tremola", Arc = 120 }],
            Keys =
            [
                new(0, Pt.On(0, 0.6f, 1.7f, 4.5f), Pt.On(0, 0f, 0.9f, -10f), 35),
                new(B, Pt.On(0, -0.6f, 1.6f, 4.2f), Pt.On(0, 0f, 0.9f, -10f), 35),
            ],
            Smooth = 0.15f, Preroll = 4,
        },
        new()
        {
            Number = 47, Name = "Up the valley", FromBar = 92, Bars = 2, Feature = "the helicopter chases a UFO",
            Hour = 15.4,
            Cast =
            [
                new() { Who = Yvonne, Ride = RideKind.Helicopter, At = new Spot(2635900, 1159300, 120), Heading = 355, Drive = Drive.Fly, Launch = 30, Flight = _ => Stick(0f, -1f, up: 0.2f, effort: true) },
                // the saucer just ahead of the helicopter
                Ufo(new Spot(2635900, 1159420, 135), 355, 32f),
            ],
            Keys =
            [
                new(0, Pt.On(0, 14f, 5f, 22f), Pt.On(0, 0f, 0f, -40f), 35),
                new(2 * B, Pt.On(0, 18f, 4f, 16f), Pt.On(0, 0f, 0f, -40f), 35),
            ],
            Smooth = 0.4f, Preroll = 2,
        },
        new()
        {
            Number = 48, Name = "Air force", FromBar = 94, Bars = 2, Feature = "the paraglider and the light plane",
            Hour = 15.5,
            Cast =
            [
                new() { Who = Volunteers[5], Ride = RideKind.Paraglider, At = new Spot(2632479, 1170652, 380), Heading = 250, Drive = Drive.Fly, Launch = 10, Flight = _ => Stick(0.12f) },
                new() { Who = Volunteers[0], Ride = RideKind.Plane, At = new Spot(2632600, 1170900, 430), Heading = 200, Drive = Drive.Fly, Launch = 50, Flight = _ => Stick(lever: 1f) },
            ],
            // the saucer they circle
            Craft = [new Craft(new Spot(2632440, 1170600, 390), 90)],
            Keys = Around(0, 26f, 4f, 120f, 30f, 2 * B, 35, lookUp: 3f),
            Smooth = 0.3f, Preroll = 2,
            Supers = [new(0.2, 3.0, "INTERLAKEN · 15:30")],
        },
        new()
        {
            Number = 49, Name = "Pigeon squadron", FromBar = 96, Bars = 2, Feature = "pigeons and the birds",
            Hour = 15.6,
            Cast =
            [
                .. Enumerable.Range(0, 5).Select(i => new Cast
                {
                    Ride = RideKind.Pigeon, At = KramgasseEast.Toward(0, (i - 2) * 2.5f).Toward(90, i % 2 * 3f).Up(18 + i % 3), Heading = 268, Seed = 95 + i,
                    Drive = Drive.Fly, Launch = 14, Flight = t => Stick(0f, -0.3f, up: (t + i * 0.17) % 0.9 < 0.3 ? 1f : 0f),
                }),
            ],
            // the saucer over the Kramgasse they dive on
            Craft = [new Craft(KramgasseEast.Toward(270, 90).Up(28f), 90)],
            Keys =
            [
                new(0, Pt.On(2, 0.6f, 1.0f, 5f), Pt.On(2, 0f, 0f, -12f), 24),
                new(2 * B, Pt.On(2, 0.4f, 0.8f, 4f), Pt.On(2, 0f, 0f, -12f), 24),
            ],
            Smooth = 0.08f, Preroll = 0.8,
        },
        new()
        {
            Number = 50, Name = "The navy", FromBar = 98, Bars = 2, Feature = "the jetski, the speedboat, the steamer",
            Hour = 15.7, Sea = 0.3f,
            Cast =
            [
                new() { Ride = RideKind.Jetski, At = new Spot(2560693, 1140362), Heading = 315, Board = ChillonBoard, Launch = 15, Drive = Drive.Controls, Controls = t => Pedal(1f, 0.25f * Mathf.Sin((float)t * 1.4f)), Seed = 8 },
                new() { Who = Tourist, Ride = RideKind.Speedboat, At = new Spot(2560689, 1140349), Heading = 315, Board = ChillonBoard, Launch = 14, Drive = Drive.Controls, Controls = _ => Pedal(1f, 0.15f) },
            ],
            // the saucer skimming the water, the boats circling it
            Craft = [new Craft(Spot.Alt(2560740, 1140420, 382), 0)],
            Keys =
            [
                new(0, Pt.Alt(2560628, 1140350, 373.4f), Pt.At(2560720, 1140445, 9), 35),
                new(2 * B, Pt.Alt(2560626, 1140348, 373.4f), Pt.At(2560714, 1140445, 9), 35),
            ],
            Preroll = 1.0,
        },
        new()
        {
            Number = 51, Name = "Swiss fist", FromBar = 100, Bars = 2, Feature = "fist fights",
            Hour = 15.8,
            Cast =
            [
                new()
                {
                    Who = Rochat, At = Meadow.Toward(270, 0.9f), Heading = 90,
                    Fight = t => t < 0.8 ? Guard : t < 1.4 ? Jab : t < 1.7 ? Guard : t < 2.4 ? Jab : t < 2.7 ? Guard : t < 3.6 ? Uppercut : Victory,
                },
                new()
                {
                    Who = Zorp, At = Meadow.Toward(90, 0.9f), Heading = 270,
                    Fight = t => t < 1.0 ? Guard : t < 1.6 ? JumpKick : t < 3.7 ? HitStun : Dazed,
                },
            ],
            Keys =
            [
                new(0, Pt.On(0, 3.6f, 1.3f, -0.6f), Pt.On(0, 0f, 1.2f, -0.9f), 28),
                new(2 * B, Pt.On(0, 3.0f, 1.1f, -1.4f), Pt.On(0, 0f, 1.2f, -0.9f), 28),
            ],
            Smooth = 0.25f, Preroll = 0.5,
            Bubbles = [Say("rochat_fist", 3.0, 0, "Swiss army. Swiss fist.")],
        },
        new()
        {
            Number = 52, Name = "Catch of the day", FromBar = 102, Bars = 2, Feature = "fishing; swimming",
            Hour = 16.0,
            Cast =
            [
                new() { Who = Heidi, At = NyonMole, Heading = 95, Item = (int)ItemId.FishingRod, Use = t => t > 0.3 },
                new() { Who = Zib, At = NyonMole.Toward(95, 24), Board = NyonMole.Toward(0, 6), Swim = true, Heading = 275, Drive = Drive.Walk, Walk = _ => (new Vector2(0f, 0.5f), false) },
            ],
            ItemCues = [new(0.4, 0, ItemEventKind.FishCast)],
            Keys =
            [
                new(0, Pt.On(0, -2.2f, 1.6f, 3.4f), Pt.On(0, 0f, 0.2f, -8f), 28),
                new(2 * B, Pt.On(0, -2.6f, 1.7f, 3.8f), Pt.On(1, 0f, 0.3f), 28),
            ],
            Smooth = 0.3f, Preroll = 2.0,
            Supers = [new(0.2, 3.0, "NYON · 16:00")],
            Bubbles = [Say("heidi_catch", 2.2, 0, "Biggest catch of the year.")],
        },
        new()
        {
            Number = 53, Name = "I'm OK", FromBar = 104, Bars = 2, Feature = "the crash: thrown through the windscreen",
            Hour = 16.1, Traffic = 0,
            Cast = [new() { Who = Luca, Ride = Car(Gc8), At = RiddesW, Toward = RiddesE, Drive = Drive.Follow, Route = "riddes", Arc = 60, Speed = 24 }],
            // the saucer landed across the road (a wall under its hull stops the car)
            Craft = [new Craft(new Spot(2583250, 1113236), 0)],
            Props = [new(default, 0, new Vector3(6f, 1.6f, 0.8f), new Color(0.75f, 0.76f, 0.8f)) { Route = "riddes", Arc = 150 }],
            Keys =
            [
                new(0, Pt.Road("riddes", 125, -3.5f, 1.3f), Pt.Road("riddes", 151, 0f, 1.0f), 35),
                new(2 * B, Pt.Road("riddes", 128, -4.5f, 1.5f), Pt.Road("riddes", 160, 0f, 0.6f), 35),
            ],
            Preroll = 4.7,
            Bubbles = [Say("luca_ok", 2.6, 0, "I'm OK!", BubbleShape.Shout)],
        },
        new()
        {
            Number = 54, Name = "Base jump", FromBar = 106, Bars = 2, Feature = "the base jump into the wingsuit",
            Hour = 16.2,
            Cast = [new() { Who = Volunteers[2], Ride = RideKind.Wingsuit, At = Spot.Alt(2635350, 1161600, 1500), Heading = 185, Drive = Drive.Fly, Launch = 42, Climb = -15, Flight = _ => Stick(0f, -0.15f) }],
            Keys =
            [
                new(0, Pt.On(0, 0.9f, 1.3f, 4.5f), Pt.On(0, 0f, -1f, -20f), 24),
                new(2 * B, Pt.On(0, -0.9f, 1.5f, 4.0f), Pt.On(0, 0f, -1f, -20f), 24),
            ],
            Smooth = 0.03f, Shake = 0.4f, Preroll = 1.5,
        },
        new()
        {
            Number = 55, Name = "Parkour", FromBar = 108, Bars = 2, Feature = "on foot: run, slide, jump",
            Hour = 16.3, Traffic = 0,
            Cast =
            [
                new()
                {
                    Who = Luca, At = AvenueTop, Route = "avenue", Toward = AvenueFoot, Arc = 40, Drive = Drive.Walk,
                    Walk = _ => (new Vector2(0f, 1f), true),
                    Actions = t => (t > 3.2 && t < 3.4, t > 1.6 && t < 1.8),
                },
            ],
            Keys =
            [
                new(0, Pt.On(0, 1.6f, 1.0f, -5f), Pt.On(0, 0f, 1.0f), 24),
                new(2 * B, Pt.On(0, 2.2f, 1.2f, -2f), Pt.On(0, 0f, 1.0f), 24),
            ],
            Smooth = 0.12f, Preroll = 1,
        },
        new()
        {
            Number = 56, Name = "Trick or treat", FromBar = 110, Bars = 2, Feature = "night; the Halloween occasion",
            Hour = 21.5, Moon = 1.0f, Occasion = "halloween",
            Cast =
            [
                new() { Who = Heidi, At = MainStreet, Heading = 0, Headwear = PumpkinHead, Dance = Cheer },
                Runner(MainStreet.Toward(0, 6), 0, Zorp, from: 0.8),
            ],
            Keys =
            [
                new(0, Pt.On(0, 1.5f, 1.4f, 4f), Pt.On(1, 0f, 1.3f), 28),
                new(2 * B, Pt.On(0, 1.2f, 1.5f, 3f), Pt.On(1, 0f, 1.3f), 28),
            ],
            Smooth = 0.3f, Preroll = 0.5,
            Supers = [new(0.2, 3.0, "LAUTERBRUNNEN · 21:30")],
            Bubbles = [Say("zorp_pumpkin", 0.9, 1, "AAAH! A giant orange head!", BubbleShape.Shout)],
        },
        new()
        {
            Number = 57, Name = "That smell", FromBar = 112, Bars = 2, Feature = "the fondue makes peace",
            Hour = 18.4,
            Cast =
            [
                new() { Who = Heidi, At = Crew.Toward(270, 1.4f), Heading = 90, Item = (int)ItemId.Fondue },
                new() { Who = Zorp, At = Crew.Toward(90, 1.4f), Heading = 270 },
            ],
            Props = Pot,
            ItemCues = [new(0.1, 0, ItemEventKind.Fondue)],
            Keys =
            [
                new(0, Ground(Crew, 180, 3.4f, 1.5f), Ground(Crew, 0, 0, 1.0f), 35),
                new(2 * B, Ground(Crew, 175, 2.8f, 1.4f), Ground(Crew, 0, 0, 1.1f), 35),
            ],
            Smooth = 0.3f, Preroll = 0.5,
            Bubbles =
            [
                Say("zorp_smell", 0.4, 1, "What... is that smell?"),
                Say("heidi_want", 2.4, 0, "Fondue. Want some?"),
            ],
        },

        // ==== PEACE (bars 114-121, the outro) =====================================================
        new()
        {
            Number = 58, Name = "Christmas on the Gornergrat", FromBar = 114, Bars = 4, Feature = "the Christmas occasion; dances; the radio; together",
            Hour = 17.2, Occasion = "christmas",
            Cast = Circle(Crew, 3.2f, [Chicken, Griddy, Robot, Ymca, Moonwalk, Floss, Macarena], 0)
                .Zip(new[] { Heidi, Luca, Yvonne, Rochat, Zorp, Zib, Volunteers[1] }, (c, who) => c with { Who = who, Headwear = SantaHat })
                .ToArray(),
            Props = [.. Pot, new(Crew.Toward(45, 1.2f).Up(0.3f), 220, default, default) { Item = ItemId.Radio, Scale = 1.4f }],
            Keys = Orbit(Crew, 7f, 6.2f, 1.6f, 1.9f, 200f, 270f, 4 * B, 28, lookUp: 1.1f),
            Preroll = 0.5,
            Craft = [new Craft(Spot.Alt(2620500, 1092000, 4300), 0) { Mothership = true }],
            Supers = [new(0.3, 3.4, "GORNERGRAT · DECEMBER")],
            Bubbles =
            [
                Say("heidi_monday", 1.4, 0, "Cows back by Monday."),
                Say("zorp_deal", 3.4, 4, "Deal. Same time next year?"),
            ],
        },
        new()
        {
            Number = 59, Name = "The photo", FromBar = 118, Bars = 4, Feature = "the camera item: the polaroid; the end card",
            Hour = 17.25, Occasion = "christmas",
            Cast =
            [
                new() { Who = Heidi, At = Crew.Toward(0, 0.6f), Heading = 90, Item = (int)ItemId.Camera, Headwear = SantaHat },
                new() { Who = Zorp, At = Crew.Toward(0, 1.8f), Heading = 90, Dance = Cheer },
                new() { Who = Luca, At = Crew.Toward(0, 3.0f), Heading = 90, Dance = Wave, Headwear = SantaHat },
                new() { Who = Yvonne, At = Crew.Toward(180, 0.6f), Heading = 90, Dance = FistPump, Headwear = SantaHat },
                new() { Who = Rochat, At = Crew.Toward(180, 1.8f), Heading = 90, Dance = Salute, Headwear = SantaHat },
                new() { Who = Zib, At = Crew.Toward(180, 3.0f), Heading = 90, Dance = Clap },
                new() { Ride = RideKind.Pigeon, At = Crew.Toward(80, 2.6f), Heading = 110, Seed = 90 },
            ],
            Props = [new(Crew.Toward(90, 1.3f), 0, default, default) { Item = ItemId.FonduePot, Scale = 1.6f }],
            Keys =
            [
                new(0, Ground(Crew, 92, 5.5f, 1.6f), Ground(Crew, 0, 0, 1.2f), 28),
                new(4 * B, Ground(Crew, 85, 14f, 6f), Ground(Crew, 0, 0, 1.0f), 28),
            ],
            Preroll = 0.5, FadeOut = 2.4,
            Photo = 0.3,
            PhotoFrom = new Key(0, Ground(Crew, 90, 5.2f, 1.5f), Ground(Crew, 0, 0, 1.15f), 28),
            Captions =
            [
                new(1.2, 7.0, "UNITSPORT SWITZERLAND", "Invasion, Swiss Style.", Title: true) { SubId = "title_tagline" },
                new(4.0, 4.6, "", Space1990.Credit),
            ],
        },
    // every shot in Cartoon, on the film's song, no occasion but the ones asked for (null is the calendar's)
    }.Select(s => s with { Style = "cartoon", Song = Space1990, Occasion = s.Occasion ?? "none" }).ToList();
}
