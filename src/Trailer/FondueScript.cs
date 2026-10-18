using System.Collections.Generic;
using System.Linq;
using Godot;
using UnitSport.Avatar;
using UnitSport.Items;
using UnitSport.Player;
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
/// Stage 1: the UFO is not built yet (#861): its shots fly a stand-in (the light plane on the move,
/// the helicopter where it hovers), marked <c>// UFO (#861) in stage 2</c>. The dialogue (the
/// <see cref="Lines"/>, ids shared with <c>docs/trailer/fondue.subtitles.json</c>) shows in the chat
/// until the speech bubbles (#862) replace it.
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
    /// <summary>The UFO's pilot: on screen in stage 2, when #861 gives the saucer a pilot.</summary>
    private static readonly Character Zorp = new("ZORP", new Color("8cff5a"), Appearance.Default, []);

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
        ["f29"] = "It's taking the hay too?!",
        ["f30"] = "Plan B: a new caquelon.",
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

    /// <summary>Line <paramref name="id"/>, said at <paramref name="t"/> by <paramref name="who"/>.</summary>
    private static Line Say(string id, double t, Character? who) => new(t, who, Lines[id]);

    // ---- places ---------------------------------------------------------------------------------

    /// <summary>The Gornergrat ridge's south edge, where it drops to the Gorner glacier (to frame).</summary>
    private static readonly Spot GornerEdge = Crew.Toward(167, 46);
    /// <summary>The Riddes plain (VS): IKEA, the fields along the Rhône.</summary>
    private static readonly Spot RiddesIkea = new(2582265, 1112638);
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
    private static Cast[] Ring(Spot c, int lea, int marco, int nina, int jonas, int jonasHolds = (int)ItemId.Bread) =>
    [
        new() { Who = Lea, At = c.Toward(270, 1.6f), Heading = 90, Dance = lea },
        new() { Who = Marco, At = c.Toward(0, 1.6f), Heading = 180, Dance = marco },
        new() { Who = Nina, At = c.Toward(90, 1.6f), Heading = 270, Dance = nina },
        new() { Who = Jonas, At = c.Toward(180, 1.6f), Heading = 0, Dance = jonas, Item = jonasHolds },
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
            Chat = [Say("f02a", 0.3, Lea), Say("f02b", 2.6, Marco)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "A light in the sky", Feature = "UFO (#861)",
            Hour = 17.75,
            Cast =
            [
                .. Ring(Crew, 0, 0, 0, 0),
                // UFO (#861) in stage 2: a light coming in from the north-east, high over them
                new() { Ride = RideKind.Plane, At = Spot.Alt(Crew.E + 600, Crew.N + 500, 3450), Heading = 230, Drive = Drive.Fly, Launch = 45, Flight = _ => Stick(lever: 0.4f), Seed = 99 },
            ],
            Props = Pot(Crew),
            Keys =
            [
                new(0, Ground(Crew, 115, 4.5f, 0.7f), Pt.On(4, 0f, 0f), 28),
                new(B, Ground(Crew, 112, 4.3f, 0.7f), Pt.On(4, 0f, 0f), 28),
            ],
            Smooth = 0.2f, Preroll = 0.3,
            Chat = [Say("f03", 0.4, Jonas)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "The beam", Feature = "UFO (#861): the tractor beam",
            Hour = 17.75,
            Cast =
            [
                .. Ring(Crew, ArmWave, Shrug, Shrug, Shrug),
                // UFO (#861) in stage 2: hovering 20 m over the pot, the beam pulling it up
                new() { Ride = RideKind.Helicopter, At = Spot.Alt(Crew.E, Crew.N, 3160), Heading = 250, Drive = Drive.Fly, Launch = 0, Flight = _ => Stick(up: 0.55f), Seed = 99 },
            ],
            Props = Pot(Crew),
            Keys =
            [
                new(0, Ground(Crew, 100, 7, 1.4f), Ground(Crew, 0, 0, 0.8f), 24),
                new(2 * B, Ground(Crew, 104, 6.5f, 1.2f), Ground(Crew, 0, 0, 4f), 24),
            ],
            Preroll = 0.5,
            Chat = [Say("f04", 0.6, Lea)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Gone", Feature = "UFO (#861)",
            Hour = 17.75,
            Cast =
            [
                // UFO (#861) in stage 2: away toward the Matterhorn, then north
                new() { Ride = RideKind.Plane, At = Spot.Alt(Crew.E - 40, Crew.N, 3170), Heading = 255, Drive = Drive.Fly, Launch = 70, Flight = _ => Stick(0f, 0.3f, lever: 1f), Seed = 99 },
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
            Chat = [Say("f06", 0.2, Marco)],
            Captions = [new(1.8, 3.4, Card("t06"), Card("t06b"), Title: true)],
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
            Number = 0, FromBar = 0, Bars = 2, Name = "Wingsuit", Feature = "wingsuit; UFO (#861) ahead",
            Hour = 17.8,
            Cast =
            [
                new() { Who = Jonas, Ride = RideKind.Wingsuit, At = Spot.Alt(2626400, 1091900, 3000), Heading = 245, Drive = Drive.Fly, Launch = 45, Climb = -15, Flight = _ => Stick(0f, -0.15f) },
                // UFO (#861) in stage 2: far ahead, low over the glacier
                new() { Ride = RideKind.Plane, At = Spot.Alt(2625900, 1091650, 2900), Heading = 245, Drive = Drive.Fly, Launch = 50, Flight = _ => Stick(lever: 0.5f), Seed = 99 },
            ],
            Keys =
            [
                new(0, Pt.On(0, 0.9f, 1.3f, 4.5f), Pt.On(0, 0f, -1f, -20f), 24),
                new(2 * B, Pt.On(0, -0.9f, 1.5f, 4.0f), Pt.On(0, 0f, -1f, -20f), 24),
            ],
            Smooth = 0.03f, Shake = 0.4f, Preroll = 1.0,
            Chat = [Say("f08", 0.5, Jonas)],
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
            Chat = [Say("f10", 0.3, Marco)],
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
            Chat = [Say("f11", 0.4, Nina)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "The Aletsch", Feature = "light plane; UFO (#861) ahead",
            Hour = 17.5,
            Cast =
            [
                new() { Who = Nina, Ride = RideKind.Plane, At = Spot.Alt(2646200, 1151600, 3150), Heading = 145, Drive = Drive.Fly, Launch = 55, Flight = _ => Stick(lever: 1f) },
                // UFO (#861) in stage 2: ahead over the ice
                new() { Ride = RideKind.Plane, At = Spot.Alt(2646200, 1151600, 3150).Toward(145, 160), Heading = 145, Drive = Drive.Fly, Launch = 58, Flight = _ => Stick(lever: 1f), Seed = 99 },
            ],
            Keys =
            [
                new(0, Pt.On(0, -14f, 3f, 16f), Pt.On(0, 0f, -2f, -25f), 35),
                new(2 * B, Pt.On(0, -10f, 2f, 10f), Pt.On(0, 0f, -2f, -25f), 35),
            ],
            Smooth = 0.15f, Preroll = 1,
            Chat = [Say("f12", 0.6, Nina)],
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
            Chat = [Say("f14", 1.0, Nina)],
        },
        new()
        {
            // the map screen (#map) is the game's own UI, which the director hides: until it can film a
            // screen, the map is the country seen straight down from high above, where the map's relief comes from
            Number = 0, FromBar = 0, Bars = 1, Name = "The map", Feature = "the map (director addition: a UI screen)",
            Hour = 13,
            Keys =
            [
                new(0, Pt.At(2636000, 1162000, 9000), Pt.At(2636000, 1162000.5, 0), 24),
                new(B, Pt.At(2636000, 1162000, 7500), Pt.At(2636000, 1162000.5, 0), 24),
            ],
            Preroll = 0.2,
            Chat = [Say("f15", 0.3, Lea)],
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
            Chat = [Say("f18", 0.2, Lea)],
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
            Chat = [Say("f19a", 0.3, Pigeon), Say("f19b", 3.3, Lea)],
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
            Chat = [Say("f20a", 0.8, Marco), Say("f20b", 3.4, Marco)],
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
            Number = 0, FromBar = 0, Bars = 2, Name = "Tremola", Feature = "cars on a real pass; UFO (#861) over it",
            Hour = 8.5, Traffic = 0,
            Cast =
            [
                new() { Who = Marco, Ride = Car(Ae86), At = Tremola, Toward = TremolaFoot, Drive = Drive.Road, Route = "tremola", Arc = 280, Skill = 1.1f },
                // UFO (#861) in stage 2: hovering over the hairpins below
                new() { Ride = RideKind.Helicopter, At = new Spot(2686760, 1155800, 60), Heading = 180, Drive = Drive.Fly, Launch = 0, Flight = _ => Stick(up: 0.55f), Seed = 99 },
            ],
            Keys =
            [
                new(0, Pt.Road("tremola", 378, -9f, 2.3f), Pt.On(0, 0f, 0.7f), 50),
                new(2 * B, Pt.Road("tremola", 378, -9f, 2.3f), Pt.On(0, 0f, 0.7f), 50),
            ],
            Smooth = 0.1f, Preroll = 5.8,
            Chat = [Say("f22", 1.0, Marco)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Cockpit", Feature = "cockpit view; UFO (#861) through the windscreen",
            Hour = 9, Traffic = 0,
            Cast =
            [
                new() { Who = Marco, Ride = Car(Supra), At = Furka, Toward = FurkaTop, Drive = Drive.Road, Route = "cockpit", Arc = 900, Skill = 1.05f, FirstPerson = true },
                // UFO (#861) in stage 2: ahead up the pass
                new() { Ride = RideKind.Plane, At = Spot.Alt(2672600, 1157800, 2500), Heading = 70, Drive = Drive.Fly, Launch = 40, Flight = _ => Stick(lever: 0.4f), Seed = 99 },
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
            Chat = [Say("f24", 0.2, Racer)],
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
            Chat = [Say("f27", 0.2, Lea)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Crash", Feature = "the crash through the windscreen",
            Hour = 10.5, Traffic = 0,
            Cast = [new() { Who = Marco, Ride = Car(Gc8), At = RiddesWest, Toward = RiddesEast, Drive = Drive.Follow, Route = "riddes", Arc = 60, Speed = 24 }],
            Props = [new(default, 0, new Vector3(12f, 3f, 0.8f), new Color(0.62f, 0.6f, 0.56f)) { Route = "riddes", Arc = 150 }],
            Keys = [new(0, Pt.Road("riddes", 125, -3.5f, 1.3f), Pt.Road("riddes", 151, 0f, 1.0f), 35)],
            Preroll = 4.7,
            Chat = [Say("f28", 2.0, Marco)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Harvest", Feature = "farming: tractor, combine, fields; UFO (#861)",
            Hour = 11, Traffic = 0,
            Cast =
            [
                new() { Who = Jonas, Ride = Heavy(6), Trailer = 6, At = RiddesField, Toward = RiddesFieldEnd, Drive = Drive.Follow, Route = "field", Arc = 0, Speed = 7 },
                new() { Ride = Heavy(7), At = RiddesField, Toward = RiddesFieldEnd, Drive = Drive.Follow, Route = "field", Arc = -30, Speed = 6, Seed = 33 },
                // UFO (#861) in stage 2: over the field, beaming up a bale
                new() { Ride = RideKind.Helicopter, At = RiddesField.Toward(30, 80).Up(30), Heading = 200, Drive = Drive.Fly, Launch = 0, Flight = _ => Stick(up: 0.55f), Seed = 99 },
            ],
            Keys =
            [
                new(0, Pt.On(0, 8f, 5f, -12f), Pt.On(0, 0f, 2f, 12f), 28),
                new(2 * B, Pt.On(0, 9f, 5.5f, -8f), Pt.On(0, 0f, 2f, 12f), 28),
            ],
            Smooth = 0.3f, Preroll = 3,
            Supers = [new(0.3, 3.0, Card("s29"))],
            Chat = [Say("f29", 2.0, Jonas)],
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
            Chat = [Say("f30", 0.5, Lea)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "The steamer", Feature = "the CGN paddle steamer; UFO (#861)",
            Hour = 13,
            Cast =
            [
                new()
                {
                    Ride = RideKind.Steamer, At = new Spot(2546900, 1148660), Heading = 298, Board = new Spot(2546900, 1148950),
                    Draught = 1.6f, Launch = 5f, Drive = Drive.Controls, Controls = _ => Pedal(1f), Seed = 70,
                },
                // UFO (#861) in stage 2: low over the lake ahead of the steamer
                new() { Ride = RideKind.Plane, At = Spot.Alt(2546700, 1148760, 420), Board = new Spot(2546900, 1148950), Heading = 290, Drive = Drive.Fly, Launch = 40, Flight = _ => Stick(lever: 0.4f), Seed = 99 },
            ],
            Keys =
            [
                new(0, Pt.At(2547020, 1149060, 26), Pt.On(0, 0f, 3f, 25f), 35),
                new(2 * B, Pt.At(2547060, 1149045, 26), Pt.On(0, 0f, 3f, 25f), 35),
            ],
            Smooth = 0.4f, Preroll = 4,
            Supers = [new(0.3, 3.0, Card("s31"))],
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
            Chat = [Say("f32", 1.0, Marco)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 1, Name = "Fishing", Feature = "fishing; UFO (#861) takes the fish",
            Hour = 15,
            Cast = [new() { Who = Jonas, At = NyonPier, Heading = 125, Item = (int)ItemId.FishingRod }],
            Keys =
            [
                new(0, Pt.On(0, -3.4f, 1.3f, 0.6f), Pt.On(0, 0.4f, 1.2f, -1.6f), 28),
                new(B, Pt.On(0, -3.2f, 1.3f, 0.4f), Pt.On(0, 0.4f, 1.3f, -1.6f), 28),
            ],
            Preroll = 0.5,
            Chat = [Say("f33", 1.0, Jonas)],
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
            Chat = [Say("f34", 1.2, Jonas)],
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
                new(new Spot(2542900, 1152270), 0, default, default) { Item = ItemId.Campfire, Scale = 4f },
                new(new Spot(2542900, 1152270).Up(0.35f), 0, default, default) { Item = ItemId.FonduePot, Scale = 1.4f },
                new(new Spot(2542900, 1152270).Toward(120, 1.6f), 300, default, default) { Item = ItemId.Radio, Scale = 1.4f },
            ],
            Keys =
            [
                new(0, Ground(new Spot(2542900, 1152270), 170, 2.6f, 0.8f), Ground(new Spot(2542900, 1152270), 0, 0, 0.5f), 28),
                new(B, Ground(new Spot(2542900, 1152270), 160, 2.3f, 0.7f), Ground(new Spot(2542900, 1152270), 0, 0, 0.5f), 28),
            ],
            Preroll = 0.5,
            Chat = [Say("f36", 0.2, Lea)],
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
            Props = [new(new Spot(2542900, 1152270), 0, default, default) { Item = ItemId.Campfire, Scale = 4f }],
            Keys =
            [
                new(0, Ground(new Spot(2542907, 1152264), 180, 5.0f, 1.2f), Ground(new Spot(2542907, 1152264), 0, 0, 1.1f), 28),
                new(2 * B, Ground(new Spot(2542907, 1152264), 200, 4.5f, 1.1f), Ground(new Spot(2542907, 1152264), 0, 0, 1.0f), 28),
            ],
            Preroll = 1.0,
            Chat = [Say("f37a", 0.2, Marco), Say("f37b", 1.4, Jonas)],
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
            Chat = [Say("f38", 0.9, Lea)],
        },
        Season(2, "Halloween", "halloween", 17.6, Card("s39"), (int)Headwear.WitchHat, (int)Headwear.PumpkinHead, "f39", Marco),
        Season(2, "Christmas", "christmas", 16.4, Card("s40"), (int)Headwear.SantaHat, (int)Headwear.ReindeerAntlers, "f40", Nina),

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
            Chat = [Say("f41", 0.5, Nina)],
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
            Chat = [Say("f46", 0.3, Jonas)],
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
            Chat = [Say("f47", 0.3, Marco)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "The city", Feature = "a real city, traffic; UFO (#861) over it",
            Hour = 18.3, Traffic = 80,
            Cast =
            [
                // UFO (#861) in stage 2: over the crossroads
                new() { Ride = RideKind.Helicopter, At = new Spot(2683580, 1246800, 45), Heading = 300, Drive = Drive.Fly, Launch = 0, Flight = _ => Stick(up: 0.55f), Seed = 99 },
            ],
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
            Chat = [Say("f49", 0.6, Marco)],
        },

        // ==== the end: the thief comes back (bars 78-93) ============================================
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "The lure", Feature = "items, the radio, a real summit",
            Hour = 17.6,
            Cast =
            [
                new() { Who = Lea, At = Crew.Toward(300, 9), Heading = 120, Dance = Shrug },
                new() { Who = Marco, At = Crew.Toward(310, 10), Heading = 130 },
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
            Chat = [Say("f50", 1.0, Lea)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Landing", Feature = "UFO (#861): landing legs, the ramp",
            Hour = 17.65,
            Cast =
            [
                // UFO (#861) in stage 2: settling by the pot on its legs, the ramp opening
                new() { Ride = RideKind.Helicopter, At = Spot.Alt(2626800, 1092385, 3150), Heading = 352, Drive = Drive.Fly, Launch = 0, Flight = _ => Stick(up: 0.3f), Seed = 99 },
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
            Number = 0, FromBar = 0, Bars = 1, Name = "Face to face", Feature = "UFO (#861): its pilot",
            Hour = 17.7,
            // the pilot (#861) in stage 2, on the ramp facing her
            Cast = [new() { Who = Lea, At = Crew.Toward(270, 2.2f), Heading = 90 }],
            Props = Pot(Crew),
            Keys =
            [
                new(0, Pt.On(0, 0.9f, 1.6f, 1.6f), Pt.On(0, 0f, 1.5f, -3f), 35),
                new(B, Pt.On(0, 0.8f, 1.6f, 1.4f), Pt.On(0, 0f, 1.5f, -3f), 35),
            ],
            Preroll = 0.5,
            Chat = [Say("f52a", 0.2, Zorp), Say("f52b", 1.4, Lea)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Sharing", Feature = "multiplayer: together",
            Hour = 17.7,
            // the pilot (#861) in stage 2, the fifth at the pot
            Cast = Ring(Crew, Wave, Clap, Cheer, 0),
            Props = Pot(Crew),
            Keys = Orbit(Crew, 6.5f, 6f, 1.6f, 1.8f, 120f, 70f, 2 * B, 28, lookUp: 1.0f),
            Preroll = 0.5,
            Chat = [new(0.2, null, Card("y53")), Say("f53", 1.6, Lea)],
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "The party", Feature = "emotes and dances",
            Hour = 17.75,
            // the pilot (#861) in stage 2, dancing too
            Cast = Ring(Crew, Charleston, Griddy, Robot, Moonwalk),
            Props = Pot(Crew),
            Keys = Orbit(Crew, 5.5f, 6f, 1.3f, 1.5f, 220f, 290f, 2 * B, 28, lookUp: 1.0f),
            Preroll = 0.5,
        },
        new()
        {
            Number = 0, FromBar = 0, Bars = 2, Name = "Lift-off", Feature = "UFO (#861): leaving",
            Hour = 17.8,
            Cast =
            [
                .. Ring(Crew, Wave, Wave, ArmWave, Wave),
                // UFO (#861) in stage 2: away into the sunset with a doggy bag
                new() { Ride = RideKind.Helicopter, At = Spot.Alt(2626770, 1092470, 3160), Heading = 250, Drive = Drive.Fly, Launch = 0, Flight = _ => Stick(0f, -0.3f, up: 1f), Seed = 99 },
            ],
            Props = Pot(Crew),
            Keys =
            [
                new(0, Ground(Crew, 70, 7, 1.0f), Pt.On(4, 0f, 0f), 28),
                new(2 * B, Ground(Crew, 70, 7, 1.0f), Pt.On(4, 0f, 0f), 28),
            ],
            Smooth = 0.2f, Preroll = 0.5,
            Chat = [Say("f55", 0.6, Zorp)],
        },
        Ending(2, 0),
        Ending(3, 2 * B),
    };

    /// <summary>
    /// Shots 39-40: Lauterbrunnen's main street on two days months apart, the same frame: the four
    /// still looking, dressed for the occasion, which the street is dressed for too.
    /// </summary>
    private static Shot Season(int bars, string name, string occasion, double hour, string card, int hatA, int hatB, string line, Character who) => new()
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
        Chat = [Say(line, 1.2, who)],
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
        Chat = from == 0 ? [Say("f56", 0.6, Lea)] : [],
        Captions = from == 0
            ? []
            :
            [
                new(1.0, 5.6, Card("t06"), "UnitSport Switzerland", Title: true),
                new(3.2, 4.6, "", HepCats.Credit),
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
