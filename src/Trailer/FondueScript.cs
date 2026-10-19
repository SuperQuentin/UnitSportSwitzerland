using System.Collections.Generic;
using System.Linq;
using Godot;
using UnitSport.Avatar;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Ui.Bubbles;
using static UnitSport.Trailer.StoryScript;
using static UnitSport.Trailer.TrailerScript;

namespace UnitSport.Trailer;

/// <summary>
/// "The Stolen Fondue" (#864): a UFO beams up the four friends' caquelon on the Gornergrat at
/// sunset, and they chase it across Switzerland with every way the game has to move, until they
/// get it back and share it with its thief. The script <c>docs/trailer/fondue.md</c> as shots, cut
/// on "Hep Cats" (90 BPM). Places as in <see cref="TrailerScript"/> and <see cref="StoryScript"/>
/// where they are the same, read off overhead stills (<c>--trailer-scout</c>) where they are new.
///
/// <para>
/// The saucer is #861's: an actor (<see cref="RideKind.Ufo"/>, <see cref="Drive.Fly"/>) where it
/// flies, a <see cref="Craft"/> where it hovers or stands, its beam lifting <see cref="Prop.Beamable"/>
/// props (the caquelon). The dialogue is speech bubbles (#862), the <see cref="Lines"/> by id, as in
/// <c>docs/trailer/fondue.subtitles.json</c>.
/// </para>
/// </summary>
public static class FondueScript
{
    /// <summary>
    /// "Hep Cats", Kevin MacLeod, CC BY 4.0: swing at 90 BPM (one bar 2.667 s), measured off the file
    /// (spectral flux, the comb's phase: 90.000 BPM, no drift in 4 min), the first downbeat at 1.621 s.
    /// The music stops at the end of bar 92 (246.95 s); the film runs a 93rd bar, to 249.62 s, the end
    /// card over its last ring.
    /// </summary>
    public static readonly Song HepCats = new("Hep Cats", 90.0, 1.621, 249.62,
        "\"Hep Cats\" Kevin MacLeod (incompetech.com)\nLicensed under Creative Commons: By Attribution 4.0",
        "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Hep%20Cats.mp3");

    /// <summary>One bar of the song, s.</summary>
    private const double B = 8.0 / 3.0;

    // ---- the cast: the friends of "Meet You at the Top", and who they meet ---------------------

    private static readonly Character Pigeon = new("PIGEON", new Color("c8c8d2"), Appearance.Default, []);
    private static readonly Character Racer = new("RACER", new Color("ffe14d"),
        new Appearance(BodyBuild.Lean, 9, 6, 2, HairStyle.Mohawk, 5), [ItemId.BlackMarcel, ItemId.Jeans, ItemId.BlackShades]);
    /// <summary>The UFO's pilot, #880's alien.</summary>
    private static readonly Character Zorp = new("ZORP", new Color("8cff5a"), Aliens.Zorp.Looks, Aliens.Zorp.Wears);

    // emotes past TrailerScript's: DanceId = 2 + the table's index
    private const int Shrug = 5, Moonwalk = 19, Charleston = 33;
    // fist-fight poses (FootPlayer.FightPose): a stance, or 16 + a move
    private const int GuardHigh = 2, Down = 7, Victory = 9, Jab = 17, Kick = 18, Uppercut = 22;

    // ---- the dialogue: English here, every language in docs/trailer/fondue.subtitles.json --------

    /// <summary>Every line of the film by id (≤ 8 words), for the bubbles and the subtitle tracks.</summary>
    public static readonly IReadOnlyDictionary<string, string> Lines = new Dictionary<string, string>
    {
        ["f02a"] = "Fondue's ready!",
        ["f02b"] = "Finally. I'm starving.",
        ["f03"] = "Uh... guys?",
        ["f04"] = "HEY! That's OUR fondue!",
        ["f06"] = "AFTER IT!",
        ["f08"] = "Stop, thief! Bring back the cheese!",
        ["f10"] = "Wait for meeee!",
        ["f11"] = "I'll take the chopper.",
        ["f12"] = "Got it! It's over the glacier!",
        ["f14"] = "Lost it. Where did it go?",
        ["f15"] = "Everyone split up. Find clues.",
        ["f18"] = "Pigeon! Wait!",
        ["f19a"] = "Coo. Big plate. Went south. Coo.",
        ["f19b"] = "...Thanks?",
        ["f20a"] = "Which one...?",
        ["f20b"] = "All of them.",
        ["f22"] = "There you are!",
        ["f24"] = "Race you to the saucer!",
        ["f27"] = "Legs... don't... fail me now!",
        ["f28"] = "I'm fine!",
        ["f29"] = "It's taking the cows too?!",
        ["f30"] = "Plan B: a new caquelon.",
        ["f31"] = "Wait! I have a ticket!",
        ["f32"] = "Faster! It's getting away!",
        ["f33"] = "Not my fish too!",
        ["f34"] = "Okay. We build a trap.",
        ["f36"] = "Bait: one fondue, extra garlic.",
        ["f37a"] = "First dip is mine!",
        ["f37b"] = "No way!",
        ["f38"] = "...",
        ["f39"] = "Still no fondue.",
        ["f40"] = "Wait. Radar ping!",
        ["f41"] = "Everyone aboard. It's over the Alps!",
        ["f46"] = "The zone is closing!",
        ["f47"] = "Cheese! ...Wrong cheese.",
        ["f49"] = "It's heading back to the mountain!",
        ["f50"] = "Shh. Here it comes.",
        ["f52a"] = "Fondue... please?",
        ["f52b"] = "...You could have asked.",
        ["f53"] = "Okay. But you bring the bread.",
        ["f55"] = "Same time next year?",
        ["f56"] = "Everyone say... FONDUE!",
    };

    /// <summary>The cards and titles on screen by id (places, dates, the title, the game's system line).</summary>
    public static readonly IReadOnlyDictionary<string, string> Cards = new Dictionary<string, string>
    {
        ["s01"] = "GORNERGRAT · 3135 M · 17:40",
        ["s16"] = "LAUSANNE · 21:00",
        ["s17"] = "BERN · 23:10",
        ["s20"] = "GENÈVE · 07:00",
        ["s21"] = "GOTTHARD · 07:30",
        ["s29"] = "RIDDES · 11:00",
        ["s31"] = "LAVAUX · 13:00",
        ["s34"] = "PULLY · 16:00",
        ["s38"] = "DAY 12",
        ["s39"] = "31 OCTOBER",
        ["s40"] = "24 DECEMBER",
        ["s41"] = "GENÈVE · 26 DECEMBER",
        ["s48"] = "ZÜRICH · 18:20",
        ["s50"] = "GORNERGRAT · 17:30",
        ["t06"] = "THE STOLEN FONDUE",
        ["t06b"] = "a UnitSport Switzerland film",
        ["y53"] = "ZORP joined the game",
    };

    private static string Card(string id) => Cards[id];

    /// <summary>Line <paramref name="id"/> as a bubble at <paramref name="t"/>, over actor <paramref name="who"/> (−1: a voice off, the tail to the frame's right).</summary>
    private static Bubble Say(string id, double t, int who, BubbleShape shape = BubbleShape.Say) =>
        new(t, who, Lines[id], shape) { Id = id, At = who < 0 ? new Vector2(0.94f, 0.55f) : null };

    // ---- the saucer ------------------------------------------------------------------------------

    /// <summary>Letting go: a saucer hovers where it is, on its heading.</summary>
    private static FlightInput Hover(double t) => new(Vector2.Zero, 0f, 0f, 0f, 0f, false, false, 0f);

    /// <summary>Ahead on its heading (boosting), climbing (+) or sinking (−).</summary>
    private static FlightInput Ahead(bool boost = true, float climb = 0f) =>
        new(new Vector2(0f, -1f), Mathf.Max(0f, climb), Mathf.Max(0f, -climb), 0f, 0f, false, boost, 0f);

    /// <summary>A saucer actor at <paramref name="at"/>, flown by <paramref name="flight"/> (hovering if none), its switches over time.</summary>
    private static Cast Saucer(Spot at, float heading, System.Func<double, FlightInput>? flight = null, float launch = 0f,
        System.Func<double, UfoSwitches>? ufo = null, Spot? board = null) => new()
    {
        Ride = RideKind.Ufo, Drive = Drive.Fly, At = at, Heading = heading, Flight = flight ?? Hover, Launch = launch,
        Ufo = ufo, Board = board, Who = Zorp,
    };

    // ---- places ---------------------------------------------------------------------------------

    /// <summary>The Gornergrat ridge's south edge, where it drops to the Gorner glacier (to frame).</summary>
    /// <summary>Where the saucer comes down on the Gornergrat, on the flat east of the pot.</summary>
    private static readonly Spot LandSpot = Crew.Toward(100, 13);
    /// <summary>The saucer's heading on the ground there: its ramp toward the pot.</summary>
    private const float LandHeading = 280;
    private static readonly Spot GornerEdge = Crew.Toward(167, 51);
    /// <summary>The Riddes plain (VS): IKEA, the fields along the Rhône.</summary>
    private static readonly Spot RiddesIkea = new(2582265, 1112638);
    private static readonly Spot RiddesCow = new Spot(2584199, 1113836).Toward(59, 60).Toward(329, 22);
    private static readonly Spot RiddesField = new(2584199, 1113836), RiddesFieldEnd = new(2584426, 1113972);
    /// <summary>Geneva airport's apron by the freighter's stand: flat, wide, a car park for the garage.</summary>
    private static readonly Spot Apron = new(2497250, 1121000);
    /// <summary>The Nyon pier and the park the party danced in.</summary>
    private static readonly Spot NyonPier = new(2508077, 1137494), NyonPark = new(2507810, 1137290);
    /// <summary>Lauterbrunnen's main street, the Staubbach behind.</summary>
    private static readonly Spot Lauterbrunnen = new(2635922, 1160430);
    private static readonly Spot PullySite = new(2542905, 1152292);
    private static readonly Spot ZurichCam = new(2683480, 1246900);
    /// <summary>The theodul slope, falling west toward the Matterhorn (as in the story).</summary>
    private static readonly Spot Slope = new(2621770, 1089700);
    /// <summary>The Riddes straight, west end and east end (the showcase's crash).</summary>
    private static readonly Spot RiddesWest = new(2583100, 1113236), RiddesEast = new(2583390, 1113236);

    // ---- pieces of scenes ------------------------------------------------------------------------

    private static FlightInput Chute(double t, double at) => new(Vector2.Zero, 0f, 0f, 0f, 0f, t >= at && t < at + 0.25, false, 0f);

    /// <summary>The four round the pot: Léa west, Marco north, Nina east, Jonas south.</summary>
    private static Cast[] Ring(Spot c, int lea, int marco, int nina, int jonas, int jonasHolds = (int)ItemId.Bread, float r = 1.6f) =>
    [
        new() { Who = Lea, At = c.Toward(270, r), Heading = 90, Dance = lea },
        new() { Who = Marco, At = c.Toward(0, r), Heading = 180, Dance = marco },
        new() { Who = Nina, At = c.Toward(90, r), Heading = 270, Dance = nina },
        new() { Who = Jonas, At = c.Toward(180, r), Heading = 0, Dance = jonas, Item = jonasHolds },
    ];

    private static Prop[] Pot(Spot c) =>
    [
        new(c.Up(0f), 0, default, default) { Item = ItemId.FonduePot, Scale = 1.6f },
        new(c.Toward(45, 2.4f).Up(0.4f), 220, default, default) { Item = ItemId.Radio, Scale = 1.4f },
    ];

    /// <summary>
    /// The shots in order, each with its length in bars; numbers and first bars are counted here, so
    /// a shot can grow or shrink without renumbering the rest.
    /// </summary>
    private static readonly List<Shot> Draft = new()
    {
        // ==== cold open: the fondue (bars 1-11) ===================================================
        new()
        {
            Number = 0, FromBar = 0, Bars = 3, Name = "Alpenglow", Feature = "real terrain, sunset",
            Hour = 17.65, FadeIn = 1.8,
            Keys =
            [
                new(0, Pt.Alt(2627300, 1092800, 3330), Pt.Alt(Matterhorn.E, Matterhorn.N, 3980), 50),
                new(3 * B, Pt.Alt(2626950, 1092720, 3372), Pt.Alt(Matterhorn.E, Matterhorn.N, 4010), 50),
            ],
            Supers = [new(1.2, 5.5, Card("s01"))],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Fondue time", Feature = "characters, items (pot, radio, bread)",
            Hour = 17.7,
            Cast = Ring(Crew, Wave, Clap, Cheer, 0),
            Props = Pot(Crew),
            Keys = Orbit(Crew, 6.0f, 5.2f, 1.5f, 1.7f, 150f, 120f, 2 * B, 28, lookUp: 0.9f),
            Preroll = 0.5,
            Bubbles = [Say("f02a", 0.3, 0), Say("f02b", 2.6, 1)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "A light in the sky", Feature = "the UFO",
            Hour = 17.75,
            Cast =
            [
                .. Ring(Crew, 0, 0, 0, 0),
                // the saucer coming in from the north-east, lamps lit, high over them
                Saucer(Spot.Alt(Crew.E + 420, Crew.N + 360, 3300), 230, _ => Ahead(false, -0.3f), launch: 18),
            ],
            Props = Pot(Crew),
            Keys =
            [
                new(0, Ground(Crew, 115, 4.5f, 0.7f), Pt.On(4, 0f, 0f), 28),
                new(B, Ground(Crew, 112, 4.3f, 0.7f), Pt.On(4, 0f, 0f), 28),
            ],
            Smooth = 0.2f, Preroll = 0.3,
            Bubbles = [Say("f03", 0.4, 3)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "The beam", Feature = "the UFO's tractor beam lifts the caquelon",
            Hour = 17.75,
            // the four stepped back out of the cone (6.5 m wide down here): a person standing still in it rises too
            Cast = Ring(Crew.Up(0f), ArmWave, Shrug, Shrug, Shrug, 0, 8f),
            Props = [Pot(Crew)[0] with { Beamable = true }, Pot(Crew)[1]],
            Craft = [new Craft(Crew.Up(14f), 250) { Switches = UfoSwitches.BeamBetween(0.8, 99) }],
            Keys =
            [
                new(0, Ground(Crew, 112, 13, 1.4f), Ground(Crew, 0, 0, 2f), 24),
                new(2 * B, Ground(Crew, 116, 12, 1.2f), Ground(Crew, 0, 0, 8f), 24),
            ],
            Preroll = 0.5,
            Bubbles = [Say("f04", 0.6, 0, BubbleShape.Shout)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Gone", Feature = "the UFO",
            Hour = 17.75,
            Cast =
            [
                // the saucer away toward the Matterhorn, boosting, the pot aboard
                Saucer(Crew.Up(18f), 255, _ => Ahead(true, 0.3f), launch: 12),
            ],
            Keys =
            [
                new(0, Ground(Crew, 80, 6, 1.7f), Pt.On(0, 0f, 0f), 35),
                new(B, Ground(Crew, 80, 6, 1.7f), Pt.On(0, 0f, 0f), 35),
            ],
            Smooth = 0.1f, Preroll = 0.2,
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "After it!", Feature = "emotes, the title",
            Hour = 17.75,
            Cast =
            [
                new() { Who = Marco, At = Crew.Toward(0, 1.8f), Heading = 95, Dance = FistPump },
                new() { Who = Lea, At = Crew.Toward(0, 0.6f), Heading = 90, Dance = Cheer },
                new() { Who = Nina, At = Crew.Toward(180, 0.6f), Heading = 85, Dance = Salute },
                new() { Who = Jonas, At = Crew.Toward(180, 1.8f), Heading = 80, Dance = FistPump },
            ],
            Props = [Pot(Crew)[1]],
            Keys = Orbit(Crew, 4.6f, 4.2f, 0.6f, 0.7f, 78f, 100f, 2 * B, 24, lookUp: 1.3f),
            Preroll = 0.5,
            Bubbles = [Say("f06", 0.2, 0, BubbleShape.Shout)],
            Captions = [new(1.8, 3.4, Card("t06"), Card("t06b"), Title: true) { Id = "t06", SubId = "t06b" }],
        },

        // ==== act one: down the mountain, into the sky (bars 12-26) ================================
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Over the edge", Feature = "on foot: run, base jump into the wingsuit",
            Hour = 17.8,
            Cast =
            [
                new()
                {
                    Who = Jonas, At = GornerEdge, Heading = 165, Drive = Drive.Walk,
                    Walk = _ => (new Vector2(0f, 1f), true),
                    // jump, jump: a hop at the edge, then the wingsuit once he falls
                    Actions = t => (t > 1.0 && (int)(t * 6) % 2 == 0, false),
                    Flight = _ => Stick(0f, -0.3f),
                },
            ],
            Keys =
            [
                new(0, Pt.On(0, 0.4f, 1.8f, 3.6f), Pt.On(0, 0f, 0.2f, -8f), 24),
                new(B, Pt.On(0, 0.2f, 2.6f, 4.6f), Pt.On(0, 0f, -2f, -10f), 24),
            ],
            Smooth = 0.08f, Preroll = 0.8,
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Wingsuit", Feature = "wingsuit; the UFO ahead",
            Hour = 17.8,
            Cast =
            [
                new() { Who = Jonas, Ride = RideKind.Wingsuit, At = Spot.Alt(2626400, 1091900, 3000), Heading = 245, Drive = Drive.Fly, Launch = 45, Climb = -15, Flight = _ => Stick(0f, -0.15f) },
                // the saucer ahead, low over the glacier
                Saucer(Spot.Alt(2626150, 1091780, 2960), 245, _ => Ahead(false, -0.2f), launch: 40),
            ],
            Keys =
            [
                new(0, Pt.On(0, 0.9f, 1.3f, 4.5f), Pt.On(0, 0f, -1f, -20f), 24),
                new(2 * B, Pt.On(0, -0.9f, 1.5f, 4.0f), Pt.On(0, 0f, -1f, -20f), 24),
            ],
            Smooth = 0.03f, Shake = 0.4f, Preroll = 1.0,
            Bubbles = [Say("f08", 0.5, 0, BubbleShape.Shout)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Canopy", Feature = "parachute",
            Hour = 17.82,
            Cast = [new() { Who = Jonas, Ride = RideKind.Wingsuit, At = Spot.Alt(2624600, 1094800, 2700), Heading = 320, Drive = Drive.Fly, Launch = 40, Climb = -15, Flight = t => Chute(t, 0.9) }],
            Keys = Around(0, 14f, 3f, 150f, 110f, B, 35, lookUp: 2f),
            Smooth = 0.2f, Preroll = 0.5,
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Skis", Feature = "skis",
            Hour = 17.8,
            Cast = [new() { Who = Marco, Ride = RideKind.Skis, At = Slope, Heading = 290, Drive = Drive.Controls, Controls = _ => new RideInput(0f, 0f, 0f, true) }],
            Keys =
            [
                new(0, Pt.On(0, 1.2f, 0.6f, -7f), Pt.On(0, 0f, 0.6f), 24),
                new(B, Pt.On(0, 1.4f, 0.6f, -2f), Pt.On(0, 0f, 0.6f), 24),
            ],
            Smooth = 0.05f, Preroll = 3,
            Bubbles = [Say("f10", 0.3, 0, BubbleShape.Shout)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Chopper", Feature = "helicopter",
            Hour = 17.82,
            Cast = [new() { Who = Nina, Ride = RideKind.Helicopter, At = Spot.Alt(2626800, 1092385, 3130), Heading = 352, Drive = Drive.Fly, Launch = 10, Flight = _ => Stick(0f, -0.4f, up: 1f) }],
            Keys =
            [
                new(0, Ground(Crew, 175, 20, 1.6f), Pt.On(0, 0f, 0f), 35),
                new(2 * B, Ground(Crew, 175, 20, 1.8f), Pt.On(0, 0f, 0f), 35),
            ],
            Smooth = 0.1f, Preroll = 1,
            Bubbles = [Say("f11", 0.4, 0)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "The Aletsch", Feature = "light plane; the UFO ahead",
            Hour = 17.5,
            Cast =
            [
                new() { Who = Nina, Ride = RideKind.Plane, At = Spot.Alt(2646200, 1151600, 3150), Heading = 145, Drive = Drive.Fly, Launch = 55, Flight = _ => Stick(lever: 1f) },
                // the saucer ahead over the ice
                Saucer(Spot.Alt(2646200, 1151600, 3150).Toward(145, 120), 145, _ => Ahead(true), launch: 55),
            ],
            Keys =
            [
                new(0, Pt.On(0, -14f, 3f, 16f), Pt.On(0, 0f, -2f, -25f), 35),
                new(2 * B, Pt.On(0, -10f, 2f, 10f), Pt.On(0, 0f, -2f, -25f), 35),
            ],
            Smooth = 0.15f, Preroll = 1,
            Bubbles = [Say("f12", 0.6, 0, BubbleShape.Shout)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Paraglider", Feature = "paraglider",
            Hour = 17.4,
            Cast = [new() { Who = Lea, Ride = RideKind.Paraglider, At = new Spot(2632479, 1170652, 380), Heading = 250, Drive = Drive.Fly, Launch = 10, Flight = _ => Stick(0.12f) }],
            Keys = Around(0, 22f, 4f, 120f, 75f, B, 35, lookUp: 3f),
            Smooth = 0.3f, Preroll = 2,
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Lost it", Feature = "real valleys and villages, helicopter",
            Hour = 17.5,
            Cast = [new() { Who = Nina, Ride = RideKind.Helicopter, At = new Spot(2635900, 1159300, 120), Heading = 355, Drive = Drive.Fly, Launch = 30, Flight = _ => Stick(0f, -1f, up: 0.2f, effort: true) }],
            Keys =
            [
                new(0, Pt.On(0, 14f, 5f, 22f), Pt.On(0, 0f, 0f, -12f), 35),
                new(2 * B, Pt.On(0, 18f, 4f, 16f), Pt.On(0, 0f, 0f, -12f), 35),
            ],
            Smooth = 0.4f, Preroll = 2,
            Bubbles = [Say("f14", 1.0, 0)],
        },
        new()
        {
            // the map screen (#map) is the game's own UI, which the director hides: until it can film a
            // screen, the map is the country seen straight down from high above, where the map's relief comes from
            Number = 0, FromBar = 0, Bars = 1, Name = "The map", Feature = "the in-game map (the terrain download screen)",
            Screens = [new ScreenCue(0.0, B, GameScreen.Map)],
            Hour = 13,
            Keys =
            [
                new(0, Pt.At(2636000, 1162000, 9000), Pt.At(2636000, 1162000.5, 0), 24),
                new(B, Pt.At(2636000, 1162000, 7500), Pt.At(2636000, 1162000.5, 0), 24),
            ],
            Preroll = 0.2,
            Bubbles = [Say("f15", 0.3, -1)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Night falls", Feature = "day and night, lit windows",
            Hour = 17.5, MinutesPerDay = 0.75f,
            Keys =
            [
                new(0, Pt.At(2542900, 1151150, 90), Pt.Dir(248, -5), 35),
                new(2 * B, Pt.At(2542870, 1151140, 88), Pt.Dir(248, -5.5f), 35),
            ],
            Preroll = 0.5,
            Supers = [new(0.3, 4.4, Card("s16"))],
        },

        // ==== act two: the clues (bars 27-64) ========================================================
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "The witness", Feature = "play as a pigeon, a real town at night",
            Hour = 22.6, Moon = 0.5f, Traffic = 0,
            Cast = [new() { Ride = RideKind.Pigeon, At = KramgasseEast.Up(15), Heading = 268, Drive = Drive.Fly, Launch = 14, Flight = t => Stick(0f, -0.5f, up: t % 0.9 < 0.3 ? 1f : 0f), Seed = 90 }],
            Keys =
            [
                new(0, Pt.On(0, 0.4f, 0.6f, 2.2f), Pt.On(0, 0f, -0.8f, -8f), 24),
                new(2 * B, Pt.On(0, 0.2f, 0.5f, 2.0f), Pt.On(0, 0f, -1.2f, -8f), 24),
            ],
            Smooth = 0.05f, Preroll = 0.8,
            Supers = [new(0.3, 3.5, Card("s17"))],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Slide", Feature = "on foot: sprint and slide",
            Hour = 22.6, Moon = 0.5f, Traffic = 0,
            Cast =
            [
                new()
                {
                    Who = Lea, At = new Spot(2600980, 1199658), Heading = 268, Drive = Drive.Walk,
                    Walk = _ => (new Vector2(0f, 1f), true),
                    Actions = t => (false, t > 1.4 && t < 2.6),
                },
            ],
            Keys =
            [
                new(0, Pt.On(0, 0.9f, 1.0f, 3.4f), Pt.On(0, 0f, 0.7f, -3f), 28),
                new(B, Pt.On(0, 0.9f, 0.6f, 3.0f), Pt.On(0, 0f, 0.4f, -3f), 28),
            ],
            Smooth = 0.1f, Preroll = 1.0,
            Bubbles = [Say("f18", 0.2, 0, BubbleShape.Shout)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Pigeon talk", Feature = "birds, characters",
            Hour = 22.7, Moon = 0.5f, Traffic = 0,
            Cast =
            [
                new() { Who = Lea, At = new Spot(2600850, 1199656), Heading = 90 },
                new() { Ride = RideKind.Pigeon, At = new Spot(2600851.6, 1199656), Heading = 270, Seed = 90 },
            ],
            Keys =
            [
                new(0, Pt.At(2600851, 1199651.6, 0.9f), Pt.At(2600850.8, 1199656, 0.8f), 28),
                new(2 * B, Pt.At(2600851.2, 1199651.9, 0.9f), Pt.At(2600850.8, 1199656, 0.8f), 28),
            ],
            Preroll = 0.5,
            Bubbles = [Say("f19a", 0.3, 1), Say("f19b", 3.3, 0)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "The garage", Feature = "the 30 cars, the 74 motorbikes, on foot",
            Hour = 7, Traffic = 0,
            Cast =
            [
                new()
                {
                    Who = Marco, At = Apron.Toward(316, 4), Heading = 136, Drive = Drive.Walk,
                    Walk = _ => (new Vector2(0f, 0.7f), false),
                },
                // cars in a row on his right, bikes on his left, all facing him
                .. Enumerable.Range(0, 12).Select(i => new Cast { Ride = Car(i * 2 + 1), At = Apron.Toward(136, i * 3.4f).Toward(46, 4.5f), Heading = 226, Seed = 120 + i }),
                .. Enumerable.Range(0, 10).Select(i => new Cast { Ride = Moto(i * 7), At = Apron.Toward(136, i * 3.4f + 1.5f).Toward(226, 3.5f), Heading = 46, Seed = 140 + i }),
            ],
            Keys =
            [
                new(0, Pt.On(0, 0.6f, 2.4f, 4.5f), Pt.On(0, 0f, 0.8f, -12f), 24),
                new(2 * B, Pt.On(0, 0.4f, 2.8f, 5.0f), Pt.On(0, 0f, 0.6f, -12f), 24),
            ],
            Smooth = 0.2f, Preroll = 0.3,
            Supers = [new(0.2, 2.6, Card("s20"))],
            Bubbles = [Say("f20a", 0.8, 0, BubbleShape.Think), Say("f20b", 3.4, 0)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Convoy", Feature = "trucks, buses, the army",
            Hour = 7.5, Traffic = 4,
            Cast =
            [
                new() { Ride = Heavy(10), At = Gotthard, Toward = new Spot(2686150, 1154560), Drive = Drive.Follow, Route = "gotthard", Arc = 560, Speed = 13, Seed = 30 },
                new() { Ride = Heavy(12), At = Gotthard, Toward = new Spot(2686150, 1154560), Drive = Drive.Follow, Route = "gotthard", Arc = 530, Speed = 13, Seed = 31 },
                new() { Ride = Heavy(Setra), At = Gotthard, Toward = new Spot(2686150, 1154560), Drive = Drive.Follow, Route = "gotthard", Arc = 495, Speed = 13, Seed = 32 },
            ],
            Keys =
            [
                new(0, Pt.Road("gotthard", 745, -45f, 22f), Pt.On(1, 0f, 2f, 6f), 50),
                new(2 * B, Pt.Road("gotthard", 745, -45f, 22f), Pt.On(1, 0f, 2f, 6f), 50),
            ],
            Smooth = 0.3f, Preroll = 12,
            Supers = [new(0.3, 3.5, Card("s21"))],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Tremola", Feature = "cars on a real pass; the UFO over it",
            Hour = 8.5, Traffic = 0,
            Cast =
            [
                new() { Who = Marco, Ride = Car(Ae86), At = Tremola, Toward = TremolaFoot, Drive = Drive.Road, Route = "tremola", Arc = 280, Skill = 1.1f },
            ],
            // the saucer hovering over the hairpins below
            Craft = [new Craft(new Spot(2686760, 1155800, 35), 180)],
            Keys =
            [
                new(0, Pt.Road("tremola", 378, -9f, 2.3f), Pt.On(0, 0f, 0.7f), 50),
                new(2 * B, Pt.Road("tremola", 378, -9f, 2.3f), Pt.On(0, 0f, 0.7f), 50),
            ],
            Smooth = 0.1f, Preroll = 5.8,
            Bubbles = [Say("f22", 1.0, 0, BubbleShape.Shout)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Cockpit", Feature = "cockpit view; the UFO through the windscreen",
            Hour = 9, Traffic = 0,
            Cast =
            [
                new() { Who = Marco, Ride = Car(Supra), At = Furka, Toward = FurkaTop, Drive = Drive.Road, Route = "cockpit", Arc = 900, Skill = 1.05f, FirstPerson = true },
                // the saucer ahead up the pass
                Saucer(Spot.Alt(2672600, 1157800, 2500), 70, _ => Ahead(false), launch: 30),
            ],
            Keys =
            [
                new(0, Pt.Cockpit(0, -0.7f, 0.12f), Pt.Cockpit(0, 30f, -1.5f), 28),
                new(B, Pt.Cockpit(0, -0.7f, 0.12f), Pt.Cockpit(0, 30f, -1.5f), 28),
            ],
            Preroll = 5,
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "The grid", Feature = "races with AI drivers",
            Hour = 9, Traffic = 0,
            Cast = [.. Grid([Supra, Bnr32, Fd3s, Evo3, Nsx, S13], Furka, FurkaTop, "grid", 1350, goAt: 1.25)],
            Keys =
            [
                new(0, Pt.Road("grid", 1362, -3.0f, 0.9f), Pt.On(1, 0f, 0.8f), 50),
                new(B, Pt.Road("grid", 1363, -3.2f, 1.0f), Pt.On(1, 0f, 0.8f), 50),
            ],
            Preroll = 1,
            Bubbles = [Say("f24", 0.2, 0, BubbleShape.Shout)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "The pack", Feature = "racecraft",
            Hour = 9, Traffic = 0,
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
            Number = 0, FromBar = 0, Bars = 1, Name = "Lean", Feature = "motorbikes",
            Hour = 9.2, Traffic = 0,
            Cast =
            [
                new() { Ride = Car(Bnr32), At = Furka, Toward = FurkaTop, Drive = Drive.Follow, Route = "lean", Arc = 460, Speed = 20 },
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
            Number = 0, FromBar = 0, Bars = 1, Name = "Road bike", Feature = "road bike",
            Hour = 9.5, Traffic = 0,
            Cast = [new() { Who = Lea, Ride = RideKind.RoadBike, At = Tremola, Toward = TremolaFoot, Drive = Drive.Road, Route = "tremola", Arc = 120 }],
            Keys =
            [
                new(0, Pt.On(0, 0.6f, 1.7f, 4.5f), Pt.On(0, 0f, 0.9f, -10f), 35),
                new(B, Pt.On(0, -0.6f, 1.6f, 4.2f), Pt.On(0, 0f, 0.9f, -10f), 35),
            ],
            Smooth = 0.15f, Preroll = 4,
            Bubbles = [Say("f27", 0.2, 0)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Crash", Feature = "the crash through the windscreen",
            Hour = 10.5, Traffic = 0,
            Cast = [new() { Who = Marco, Ride = Car(Gc8), At = RiddesWest, Toward = RiddesEast, Drive = Drive.Follow, Route = "riddes", Arc = 60, Speed = 24 }],
            Props = [new(default, 0, new Vector3(12f, 3f, 0.8f), new Color(0.62f, 0.6f, 0.56f)) { Route = "riddes", Arc = 150 }],
            Keys = [new(0, Pt.Road("riddes", 125, -3.5f, 1.3f), Pt.Road("riddes", 151, 0f, 1.0f), 35)],
            Preroll = 4.7,
            Bubbles = [Say("f28", 2.0, 0)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Harvest", Feature = "farming: tractor and plough, combine, fields; the UFO beams up a cow",
            Hour = 11, Traffic = 0,
            Cast =
            [
                new() { Who = Jonas, Ride = Heavy(6), Trailer = 6, Lowered = true, At = RiddesField, Toward = RiddesFieldEnd, Drive = Drive.Follow, Route = "field", Arc = 0, Speed = 7 },
                new() { Ride = Heavy(7), At = RiddesField, Toward = RiddesFieldEnd, Drive = Drive.Follow, Route = "field", Arc = -30, Speed = 6, Seed = 33 },
            ],
            // the saucer over the field ahead, beaming up a cow
            Craft = [new Craft(RiddesCow.Up(16f), 200) { Switches = UfoSwitches.BeamBetween(2.5, 99) }],
            Herds = [new Cows(RiddesCow, Count: 1, Radius: 1.2f), new Cows(RiddesCow.Toward(300, 30), Count: 5, Radius: 12f)],
            Keys =
            [
                // behind the tractor on its left, the saucer over the field ahead
                new(0, Pt.On(0, -7f, 3.5f, 12f), Pt.On(0, -10f, 7f, -50f), 28),
                new(2 * B, Pt.On(0, -6f, 3.5f, 10f), Pt.On(0, -12f, 9f, -50f), 28),
            ],
            Smooth = 0.3f, Preroll = 3,
            Supers = [new(0.3, 3.0, Card("s29"))],
            Bubbles = [Say("f29", 2.0, 0, BubbleShape.Shout)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Plan B", Feature = "IKEA, shops, items (a new pot, a shark)",
            Hour = 11.5, Traffic = 0,
            Cast =
            [
                new() { Who = Lea, At = RiddesIkea, FromDoor = true, Item = (int)ItemId.FonduePot },
                // beside her on the step (out of the same door, the two stood in one another)
                new() { Who = Nina, At = new Spot(2582253, 1112644).Toward(125, 1.6f), Heading = 215, Item = (int)ItemId.Blahaj },
            ],
            Keys =
            [
                new(0, Pt.On(0, 2.6f, 1.6f, -7.5f), Pt.On(0, -0.8f, 1.6f), 28),
                new(2 * B, Pt.On(0, 2.2f, 1.6f, -6.0f), Pt.On(0, -0.8f, 1.5f), 28),
            ],
            Smooth = 0.3f, Preroll = 0.4,
            Bubbles = [Say("f30", 0.5, 0)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "The steamer", Feature = "the CGN paddle steamer; the UFO",
            Hour = 13,
            Cast =
            [
                new()
                {
                    Ride = RideKind.Steamer, At = new Spot(2546900, 1148660), Heading = 298, Board = new Spot(2546900, 1148950),
                    Draught = 1.6f, Launch = 5f, Drive = Drive.Controls, Controls = _ => Pedal(1f), Seed = 70,
                },
            ],
            // the saucer low over the lake ahead of the steamer
            Craft = [new Craft(Spot.Alt(2546820, 1148700, 395), 290)],
            Keys =
            [
                new(0, Pt.At(2547020, 1149060, 26), Pt.On(0, 0f, 3f, 25f), 35),
                new(B, Pt.At(2547040, 1149052, 26), Pt.On(0, 0f, 3f, 25f), 35),
            ],
            Smooth = 0.4f, Preroll = 4,
            Supers = [new(0.3, 2.3, Card("s31"))],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Swim", Feature = "on foot: swimming",
            Hour = 13,
            // after the steamer, crawl: in from the dry shore, then at the surface, deep enough to swim
            Cast =
            [
                new() { Who = Marco, At = new Spot(2546905, 1148760), Board = new Spot(2546900, 1148950), Swim = true, Heading = 205,
                    Drive = Drive.Walk, Walk = _ => (new Vector2(0f, 0.9f), true) },
            ],
            Keys =
            [
                new(0, Pt.On(0, 3.5f, 2.2f, 4.5f), Pt.On(0, 0f, 0.2f, -2f), 28),
                new(B, Pt.On(0, 3.0f, 2.2f, 3.5f), Pt.On(0, 0f, 0.2f, -2f), 28),
            ],
            Smooth = 0.4f, Preroll = 2.0,
            Bubbles = [Say("f31", 0.3, 0, BubbleShape.Shout)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Chillon", Feature = "jetski, speedboat",
            Hour = 14, Sea = 0.25f,
            Cast =
            [
                new() { Who = Marco, Ride = RideKind.Jetski, At = new Spot(2560693, 1140362), Heading = 315, Board = ChillonBoard, Launch = 15, Drive = Drive.Controls, Controls = t => Pedal(1f, 0.1f * Mathf.Sin((float)t * 1.6f)) },
                new() { Who = Nina, Ride = RideKind.Speedboat, At = new Spot(2560689, 1140349), Heading = 315, Board = ChillonBoard, Launch = 14, Drive = Drive.Controls, Controls = _ => Pedal(1f) },
            ],
            Keys =
            [
                new(0, Pt.On(0, -4f, 1.4f, -14f), Pt.On(0, 0f, 0.6f, 4f), 28),
                new(2 * B, Pt.On(0, -5f, 1.6f, -6f), Pt.On(0, 0f, 0.6f, 4f), 28),
            ],
            Smooth = 0.2f, Preroll = 2.0,
            Bubbles = [Say("f32", 1.0, 0, BubbleShape.Shout)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Fishing", Feature = "fishing; the UFO beams up the catch",
            Hour = 15,
            Cast = [new() { Who = Jonas, At = NyonPier, Heading = 125, Item = (int)ItemId.FishingRod }],
            // his line out on the lake; his catch on the quay beside him, beamed up from under his nose
            ItemCues = [new ItemCue(0.0, 0, Items.ItemEventKind.FishCast) { At = NyonPier.Toward(125, 11) }],
            Props = [new(NyonPier.Toward(215, 6f), 30, default, default) { Item = ItemId.BrownTrout, Scale = 2.2f, Beamable = true }],
            Craft = [new Craft(NyonPier.Toward(215, 6f).Up(8f), 125) { Switches = UfoSwitches.BeamBetween(0.5, 99) }],
            Keys =
            [
                new(0, Pt.On(0, -3.4f, 1.3f, 0.6f), Pt.On(0, 0.4f, 1.2f, -1.6f), 28),
                new(B, Pt.On(0, -3.2f, 1.3f, 0.4f), Pt.On(0, 0.4f, 1.3f, -1.6f), 28),
            ],
            Preroll = 0.5,
            Bubbles = [Say("f33", 1.0, 0, BubbleShape.Shout)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "The trap", Feature = "building sites, works machines, tower cranes",
            Hour = 16,
            Cast =
            [
                new() { Who = Jonas, Ride = RideKind.Excavator, At = PullySite, Heading = 30, Drive = Drive.Controls, Controls = _ => Pedal(0.4f, 0.6f) },
                new() { Ride = RideKind.WheelLoader, At = new Spot(2542930, 1152305), Heading = 300, Drive = Drive.Controls, Controls = _ => Pedal(0.4f, 0.15f), Seed = 4 },
                new() { Ride = RideKind.CompactRoller, At = new Spot(2542880, 1152285), Heading = 80, Drive = Drive.Controls, Controls = _ => Pedal(0.35f), Seed = 5 },
            ],
            Keys =
            [
                new(0, Pt.At(2542935, 1152245, 2.5f), Pt.At(2542895, 1152335, 6), 35),
                new(2 * B, Pt.At(2542940, 1152240, 22), Pt.At(2542895, 1152335, 3), 35),
            ],
            Preroll = 2,
            Supers = [new(0.3, 3.0, Card("s34"))],
            Bubbles = [Say("f34", 1.2, 0)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Hammer", Feature = "building with a hammer",
            Hour = 16.3,
            Cast = [new() { Who = Jonas, At = new Spot(2542915, 1152282), Heading = 40, Item = (int)ItemId.Hammer }],
            Props =
            [
                new(new Spot(2542915, 1152282).Toward(40, 1.3f), 40, new Vector3(2.4f, 1.0f, 1.2f), new Color(0.55f, 0.38f, 0.2f)),
            ],
            Keys =
            [
                new(0, Pt.On(0, -2.2f, 1.3f, -2.4f), Pt.On(0, 0.3f, 1.0f, -0.8f), 28),
                new(B, Pt.On(0, -2.0f, 1.2f, -2.1f), Pt.On(0, 0.3f, 1.0f, -0.8f), 28),
            ],
            Preroll = 0.5,
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "The bait", Feature = "cooking at a campfire, the radio",
            Hour = 17,
            Cast = [new() { Who = Lea, At = new Spot(2542900, 1152270).Toward(250, 1.4f), Heading = 70, Item = (int)ItemId.Cheese }],
            Props =
            [
                new(new Spot(2542900, 1152270), 0, default, default) { Placed = PlacedKind.Campfire },
                new(new Spot(2542900, 1152270).Up(0.35f), 0, default, default) { Item = ItemId.FonduePot, Scale = 1.4f },
                new(new Spot(2542900, 1152270).Toward(120, 1.6f), 300, default, default) { Item = ItemId.Radio, Scale = 1.4f },
            ],
            Keys =
            [
                new(0, Ground(new Spot(2542900, 1152270), 170, 2.6f, 0.8f), Ground(new Spot(2542900, 1152270), 0, 0, 0.5f), 28),
                new(B, Ground(new Spot(2542900, 1152270), 160, 2.3f, 0.7f), Ground(new Spot(2542900, 1152270), 0, 0, 0.5f), 28),
            ],
            Preroll = 0.5,
            Bubbles = [Say("f36", 0.2, 0)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "First dip", Feature = "fist fights",
            Hour = 17.2,
            Cast =
            [
                new() { Who = Marco, At = new Spot(2542906, 1152264), Heading = 90, Fight = t => t < 1.6 ? GuardHigh : t < 2.0 ? Jab : t < 3.4 ? GuardHigh : t < 3.9 ? Kick : t < 5.0 ? GuardHigh : Victory },
                new() { Who = Jonas, At = new Spot(2542908, 1152264), Heading = 270, Fight = t => t < 2.4 ? GuardHigh : t < 2.9 ? Uppercut : t < 4.0 ? GuardHigh : Down },
            ],
            Props = [new(new Spot(2542900, 1152270), 0, default, default) { Placed = PlacedKind.Campfire }],
            Keys =
            [
                new(0, Ground(new Spot(2542907, 1152264), 180, 5.0f, 1.2f), Ground(new Spot(2542907, 1152264), 0, 0, 1.1f), 28),
                new(2 * B, Ground(new Spot(2542907, 1152264), 200, 4.5f, 1.1f), Ground(new Spot(2542907, 1152264), 0, 0, 1.0f), 28),
            ],
            Preroll = 1.0,
            Bubbles = [Say("f37a", 0.2, 0, BubbleShape.Shout), Say("f37b", 1.4, 1, BubbleShape.Shout)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Waiting", Feature = "night, emotes",
            Hour = 21.5, Moon = 0.6f,
            Cast = [new() { Who = Lea, At = new Spot(2542900, 1152270).Toward(250, 1.4f), Heading = 70, Dance = Shrug }],
            Props = [new(new Spot(2542900, 1152270), 0, default, default) { Item = ItemId.FonduePot, Scale = 1.4f }],
            Keys =
            [
                new(0, Ground(new Spot(2542900, 1152270), 100, 8, 1.0f), Pt.On(0, 0f, 1.2f), 28),
                new(B, Ground(new Spot(2542900, 1152270), 98, 7.4f, 1.0f), Pt.On(0, 0f, 1.2f), 28),
            ],
            Preroll = 0.5,
            Supers = [new(0.2, 2.2, Card("s38"))],
            Bubbles = [Say("f38", 0.9, 0, BubbleShape.Think)],
        },
        Season(2, "Halloween", "halloween", 17.6, Card("s39"), (int)Headwear.WitchHat, (int)Headwear.PumpkinHead, "f39", 1),
        Season(2, "Christmas", "christmas", 16.4, Card("s40"), (int)Headwear.SantaHat, (int)Headwear.ReindeerAntlers, "f40", 2),

        // ==== act three: the air and the drop (bars 65-77) ==========================================
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Take-off", Feature = "the A320",
            Hour = 9,
            Cast = [new() { Who = Nina, Ride = RideKind.A320, At = new Spot(2496388, 1120293, 40), Heading = 226, Drive = Drive.Fly, Launch = 80, Climb = 8, Flight = _ => Stick(lever: 1f) }],
            Keys =
            [
                new(0, Pt.At(2496200, 1120110, 1.7f), Pt.On(0, 0f, 0f), 28),
                new(2 * B, Pt.At(2496200, 1120110, 1.7f), Pt.On(0, 0f, 0f), 28),
            ],
            Smooth = 0.06f, Preroll = 0.4,
            Supers = [new(0.3, 3.0, Card("s41"))],
            Bubbles = [Say("f41", 0.5, 0)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Heavy metal", Feature = "the AN-124",
            Hour = 10,
            Cast = [new() { Ride = RideKind.An124, At = Spot.Alt(2508144, 1137170, 440), Heading = 222, Drive = Drive.Fly, Board = NyonPark, Launch = 80, Flight = _ => Stick(lever: 0.7f) }],
            Keys =
            [
                new(0, Pt.Alt(2507780, 1137080, 375.5f), Pt.On(0, 0f, 0f), 35),
                new(2 * B, Pt.Alt(2507780, 1137080, 375.5f), Pt.On(0, 0f, 0f), 35),
            ],
            Smooth = 0.06f, Preroll = 0.3,
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "The drop plane", Feature = "Battle Royale: the cargo plane",
            Hour = 11,
            Cast = [new() { Who = Nina, Ride = RideKind.Freighter, At = Spot.Alt(2638000, 1163000, 3100), Heading = 200, Doors = 1 << 3, Drive = Drive.Fly, Launch = 72, Flight = _ => Stick(lever: 0.6f) }],
            Keys =
            [
                new(0, Pt.On(0, -48f, 8f, 18f), Pt.On(0, 0f, 0f, 4f), 35),
                new(B, Pt.On(0, -40f, 5f, 26f), Pt.On(0, 0f, -1f, 6f), 35),
            ],
            Smooth = 0.2f, Preroll = 1,
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Jump", Feature = "Battle Royale: the drop",
            Hour = 11,
            Cast =
            [
                new() { Who = Nina, Ride = RideKind.Freighter, At = Spot.Alt(2638000, 1163000, 3100), Heading = 200, Doors = 1 << 3, Drive = Drive.Fly, Launch = 72, Flight = _ => Stick(lever: 0.6f) },
                .. new[] { Lea, Marco, Jonas }.Select((who, i) => new Cast
                {
                    Who = who, Ride = RideKind.Wingsuit, At = Spot.Alt(2638000, 1163000, 3096 - i * 2).Toward(20, 16 + i * 6).Toward(110, (i % 2 - 0.5f) * 3f),
                    Heading = 200, Drive = Drive.Fly, Launch = 58 - i * 3, Climb = -8, Flight = _ => Stick(0f, -0.3f),
                }),
            ],
            Keys =
            [
                new(0, Pt.On(0, 6f, 4f, 26f), Pt.On(1, 0f, 0f), 35),
                new(B, Pt.On(0, 6f, 4f, 26f), Pt.On(2, 0f, 0f), 35),
            ],
            Smooth = 0.05f, Preroll = 0.05,
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Dive", Feature = "wingsuits",
            Hour = 11,
            Cast = new[] { Lea, Marco, Jonas }.Select((who, i) => new Cast
            {
                Who = who, Ride = RideKind.Wingsuit, At = Spot.Alt(2635950 + (i - 1) * 9, 1158575 + i * 6, 1515 + i * 3), Heading = 180,
                Drive = Drive.Fly, Launch = 50, Climb = -18, Flight = _ => Stick(0f, -0.4f),
            }).ToArray(),
            Keys =
            [
                new(0, Pt.Alt(2635954, 1158500, 1500), Pt.On(1, 0f, 0f), 24),
                new(B, Pt.Alt(2635954, 1158500, 1500), Pt.On(1, 0f, 0f), 24),
            ],
            Smooth = 0.05f, Preroll = 0.3, Shake = 0.3f,
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "The zone", Feature = "Battle Royale: the zone, bots",
            Hour = 11.2,
            Zone = (new Spot(2635900, 1159600), 200f),
            Cast =
            [
                new() { Who = Jonas, At = new Spot(2635930, 1159420), Heading = 0, Drive = Drive.Walk, Walk = _ => (new Vector2(0f, 1f), true) },
                new() { Who = Marco, At = new Spot(2635924, 1159414), Heading = 0, Drive = Drive.Walk, Walk = _ => (new Vector2(0.1f, 1f), true) },
            ],
            Keys =
            [
                // in front of them, the wall glowing behind them
                new(0, Pt.On(0, -1.5f, 1.5f, -6.5f), Pt.On(0, -0.5f, 1.4f, 4f), 24),
                new(B, Pt.On(0, -1.5f, 1.5f, -5.5f), Pt.On(0, -0.5f, 1.4f, 4f), 24),
            ],
            Smooth = 0.15f, Preroll = 0.8,
            Bubbles = [Say("f46", 0.3, 0, BubbleShape.Shout)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Loot", Feature = "Battle Royale: loot",
            Hour = 11.3,
            Zone = (new Spot(2635900, 1159600), 190f),
            Cast = [new() { Who = Marco, At = new Spot(2635932, 1159436), Heading = 180, Item = (int)ItemId.Cheese, Dance = Cheer }],
            Keys =
            [
                new(0, Pt.On(0, 0.8f, 1.0f, -2.8f), Pt.On(0, 0f, 1.5f), 24),
                new(B, Pt.On(0, 0.6f, 0.9f, -2.4f), Pt.On(0, 0f, 1.6f), 24),
            ],
            Preroll = 0.5,
            Bubbles = [Say("f47", 0.3, 0)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "The city", Feature = "a real city, traffic; the UFO takes a car",
            Hour = 18.3, Traffic = 80,
            // the saucer over the crossroads, beaming up a parked car
            Craft = [new Craft(new Spot(2683580, 1246800, 18), 300) { Switches = UfoSwitches.BeamBetween(1.0, 99) }],
            Props = [new(new Spot(2683580, 1246800), 30, default, default) { Vehicle = Car(Yaris) }],
            Keys =
            [
                new(0, Pt.At(ZurichCam.E, ZurichCam.N, 32), Pt.At(2683570, 1246790, 1), 50),
                new(2 * B, Pt.At(ZurichCam.E - 10, ZurichCam.N - 5, 32), Pt.At(2683590, 1246800, 1), 50),
            ],
            Preroll = 4,
            Supers = [new(0.3, 3.0, Card("s48"))],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Night drive", Feature = "night, headlights, a real old town",
            Hour = 22.5, Traffic = 0, Moon = 0.4f,
            Cast = [new() { Who = Marco, Ride = Car(Yaris), At = KramgasseEast, Toward = KramgasseWest, Drive = Drive.Follow, Route = "kram", Arc = -223, Speed = 9, Lights = true }],
            Keys =
            [
                new(0, Pt.At(2600900, 1199647, 2.6f), Pt.On(0, 0f, 0.8f), 28),
                new(2 * B, Pt.At(2600900, 1199647, 2.6f), Pt.On(0, 0f, 0.8f), 28),
            ],
            Smooth = 0.2f, Preroll = 4,
            Bubbles = [Say("f49", 0.6, 0, BubbleShape.Shout)],
        },

        // ==== the end: the thief comes back (bars 78-93) ============================================
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "The lure", Feature = "items, the radio, a real summit",
            Hour = 17.6,
            Cast =
            [
                new() { Who = Lea, At = Crew.Toward(300, 9), Heading = 120, Dance = Shrug },
                new() { Who = Marco, At = Crew.Toward(310, 10), Heading = 130, Item = (int)ItemId.Radio, Radio = 1 },
                new() { Who = Nina, At = Crew.Toward(290, 10), Heading = 110 },
                new() { Who = Jonas, At = Crew.Toward(300, 11), Heading = 120, Item = (int)ItemId.Blahaj },
            ],
            Props = Pot(Crew),
            Keys =
            [
                new(0, Ground(Crew, 120, 4, 0.5f), Ground(Crew, 300, 10, 1.3f), 35),
                new(2 * B, Ground(Crew, 125, 3.5f, 0.5f), Ground(Crew, 300, 10, 1.2f), 35),
            ],
            Preroll = 0.5,
            Supers = [new(0.3, 3.0, Card("s50"))],
            Bubbles = [Say("f50", 1.0, 0, BubbleShape.Whisper)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Landing", Feature = "the UFO: landing legs, the ramp",
            Hour = 17.65,
            Cast =
            [
                // the saucer settling by the pot on its legs, the ramp opening by itself once down
                Saucer(LandSpot.Up(14f), LandHeading, t => t < 0.3 ? Hover(t) : Ahead(false, -0.8f) with { Stick = Vector2.Zero }),
            ],
            Props = Pot(Crew),
            Keys =
            [
                new(0, Ground(Crew, 175, 20, 1.6f), Pt.On(0, 0f, 0f), 35),
                new(2 * B, Ground(Crew, 175, 19, 1.6f), Pt.On(0, 0f, 0f), 35),
            ],
            Smooth = 0.1f, Preroll = 1,
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Face to face", Feature = "the UFO's pilot down its ramp",
            Hour = 17.7,
            Craft = [new Craft(LandSpot, LandHeading) { Switches = _ => new UfoSwitches(Ramp: true) }],
            Cast =
            [
                new() { Who = Lea, At = Crew.Toward(100, 4f), Heading = 100 },
                // down the ramp, the old caquelon in its hands
                new()
                {
                    Who = Zorp, At = LandSpot, Drive = Drive.Walk, DownRamp = RampOf.Craft(0), Item = (int)ItemId.FonduePot,
                    Walk = t => (t > 0.3 && t < 2.6 ? new Vector2(0, 0.45f) : Vector2.Zero, false),
                },
            ],
            Props = Pot(Crew),
            Keys =
            [
                new(0, Pt.On(0, 0.9f, 1.6f, 1.6f), Pt.On(0, 0f, 1.5f, -3f), 35),
                new(B, Pt.On(0, 0.8f, 1.6f, 1.4f), Pt.On(0, 0f, 1.5f, -3f), 35),
            ],
            Preroll = 0.5,
            Bubbles = [Say("f52a", 0.2, 1), Say("f52b", 1.4, 0)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Sharing", Feature = "multiplayer: together",
            Hour = 17.7,
            Cast = [.. Ring(Crew, Wave, Clap, Cheer, 0), new() { Who = Zorp, At = Crew.Toward(45, 1.8f), Heading = 225, Item = (int)ItemId.Bread }],
            Craft = [new Craft(LandSpot, LandHeading) { Switches = _ => new UfoSwitches(Ramp: true) }],
            Props = Pot(Crew),
            Keys = Orbit(Crew, 6.5f, 6f, 1.6f, 1.8f, 120f, 70f, 2 * B, 28, lookUp: 1.0f),
            Preroll = 0.5,
            Chat = [new(0.2, null, Card("y53")) { Id = "y53" }],
            Bubbles = [Say("f53", 1.6, 0)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "The party", Feature = "emotes and dances",
            Hour = 17.75,
            Cast = [.. Ring(Crew, Charleston, Griddy, Robot, Moonwalk), new() { Who = Zorp, At = Crew.Toward(45, 1.8f), Heading = 225, Dance = Floss }],
            Craft = [new Craft(LandSpot, LandHeading) { Switches = _ => new UfoSwitches(Ramp: true) }],
            Props = Pot(Crew),
            Keys = Orbit(Crew, 5.5f, 6f, 1.3f, 1.5f, 220f, 290f, 2 * B, 28, lookUp: 1.0f),
            Preroll = 0.5,
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Lift-off", Feature = "the UFO: lift-off",
            Hour = 17.8,
            Cast =
            [
                .. Ring(Crew, Wave, Wave, ArmWave, Wave),
                // the saucer up off its legs and away into the sunset, a doggy bag aboard
                Saucer(LandSpot, LandHeading, t => t < 0.8 ? Hover(t) : t < 2.6 ? Ahead(false, 1f) with { Stick = Vector2.Zero } : Ahead(true, 0.6f) with { ViewYaw = -Mathf.DegToRad(250) },
                    ufo: t => new UfoSwitches(Ramp: false)),
            ],
            Props = Pot(Crew),
            Keys =
            [
                new(0, Ground(Crew, 70, 7, 1.0f), Pt.On(4, 0f, 0f), 28),
                new(2 * B, Ground(Crew, 70, 7, 1.0f), Pt.On(4, 0f, 0f), 28),
            ],
            Smooth = 0.2f, Preroll = 0.5,
            Bubbles = [Say("f55", 0.6, 4)],
        },
        Ending(2, 0),
        Ending(3, 2 * B),
    };

    /// <summary>
    /// Shots 39-40: Lauterbrunnen's main street on two days months apart, the same frame: the four
    /// still looking, dressed for the occasion, which the street is dressed for too.
    /// </summary>
    private static Shot Season(int bars, string name, string occasion, double hour, string card, int hatA, int hatB, string line, int who) => new()
    {
        Number = 0, FromBar = 0, Bars = bars, Name = name, Feature = $"occasions: {name}",
        Hour = hour, Occasion = occasion, Traffic = 0,
        Cast =
        [
            new() { Who = Lea, At = Lauterbrunnen.Toward(0, 6), Heading = 180, Headwear = hatA, Dance = Shrug },
            new() { Who = Marco, At = Lauterbrunnen.Toward(20, 7), Heading = 190, Headwear = hatB },
            new() { Who = Nina, At = Lauterbrunnen.Toward(340, 7), Heading = 170, Headwear = hatB },
            new() { Who = Jonas, At = Lauterbrunnen.Toward(0, 8.5f), Heading = 180, Headwear = hatA },
        ],
        Keys =
        [
            new(0, Pt.At(2635927, 1160423, 1.5f), Pt.At(Lauterbrunnen.E, Lauterbrunnen.N + 7, 1.4f), 35),
            new(bars * B, Pt.At(2635927, 1160424.5, 1.5f), Pt.At(Lauterbrunnen.E, Lauterbrunnen.N + 7, 1.4f), 35),
        ],
        Preroll = 0.5,
        Supers = [new(0.2, 3.0, card)],
        Bubbles = [Say(line, 1.2, who)],
    };

    /// <summary>
    /// The last two shots, one move cut in two: the four in a row at sunset, the pot in front of them
    /// (the pilot, #861, with them in stage 2), the camera drawing back; then the photo, the title
    /// and the credit.
    /// </summary>
    private static Shot Ending(int bars, double from) => new()
    {
        Number = 0, FromBar = 0, Bars = bars, Name = from == 0 ? "Say cheese" : "The photo", Feature = "the photo, the end card",
        Hour = 17.82,
        Cast =
        [
            new() { Who = Lea, At = Crew.Toward(0, 0.6f), Heading = 90, Item = (int)ItemId.Camera, Dance = from == 0 ? 0 : Cheer },
            new() { Who = Marco, At = Crew.Toward(0, 1.8f), Heading = 90, Dance = Wave },
            new() { Who = Nina, At = Crew.Toward(180, 0.6f), Heading = 90, Dance = from == 0 ? Clap : FistPump },
            new() { Who = Jonas, At = Crew.Toward(180, 1.8f), Heading = 90, Dance = from == 0 ? Cheer : Salute, Item = (int)ItemId.Blahaj },
            new() { Ride = RideKind.Pigeon, At = Crew.Toward(80, 2.6f), Heading = 110, Seed = 90 },
        ],
        Props =
        [
            new(Crew.Toward(90, 1.3f), 0, default, default) { Item = ItemId.FonduePot, Scale = 1.6f },
            new(Crew.Toward(40, 2.6f).Up(0.3f), 250, default, default) { Item = ItemId.Radio, Scale = 1.4f },
        ],
        Keys =
        [
            new(0, Ground(Crew, 95, 3.6f, 1.6f), Ground(Crew, 0, 0, 1.2f), 28),
            new(5 * B / 2, Ground(Crew, 88, 6.5f, 2.4f), Ground(Crew, 0, 0, 1.1f), 28),
            new(5 * B, Ground(Crew, 80, 12f, 7f), Ground(Crew, 0, 0, 1.0f), 28),
        ],
        KeysFrom = from,
        Preroll = 0.5,
        FadeOut = from == 0 ? 0 : 2.2,
        Photo = from == 0 ? null : 0.3,
        PhotoFrom = from == 0 ? null : new Key(0, Ground(Crew, 90, 4.6f, 1.5f), Ground(Crew, 0, 0, 1.15f), 28),
        Bubbles = from == 0 ? [Say("f56", 0.6, 0, BubbleShape.Shout)] : [],
        Captions = from == 0
            ? []
            :
            [
                new(1.0, 5.6, Card("t06"), "UnitSport Switzerland", Title: true) { Id = "t06", SubId = "t56b" },
                new(3.2, 4.6, "", HepCats.Credit) { SubId = "credit" },
            ],
    };

    /// <summary>
    /// The film: the draft numbered and laid on the bars in order, on the song, every shot in Cartoon
    /// (never PS1, never the Realistic styles), and with no occasion but where one is the point.
    /// </summary>
    public static readonly IReadOnlyList<Shot> Shots = Number(Draft);

    private static List<Shot> Number(List<Shot> draft)
    {
        var shots = new List<Shot>();
        int bar = 1;
        for (int i = 0; i < draft.Count; i++)
        {
            var s = draft[i];
            shots.Add(s with
            {
                Number = i + 1, FromBar = bar, Song = HepCats,
                Style = "cartoon",
                Occasion = s.Occasion ?? "none",
            });
            bar += s.Bars;
        }
        return shots;
    }
}
