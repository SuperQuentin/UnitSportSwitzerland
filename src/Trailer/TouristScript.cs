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
/// "The Alien Tourist" (#865): a small saucer crash-lands in a field at Riddes at night; its pilot,
/// BLIP, needs a day to mend it before the mothership comes back, and the four friends of the story
/// film give it the tour of Switzerland. The script is <c>docs/trailer/tourist.md</c>; every feature
/// of the game is something BLIP tries, badly at first, then brilliantly. Cartoon throughout (no PS1,
/// no Realistic); real terrain only. Places as in <see cref="TrailerScript"/> where a shot of
/// the other films already proved them, the rest read off overhead stills (<c>--trailer-scout</c>).
///
/// <para>
/// Stage 1 of 2: the UFO (#861) and the speech bubbles (#862) are not in yet. Every UFO beat has a
/// grey stand-in box where the saucer will be, marked <c>// UFO (#861) in stage 2</c>; the dialogue
/// is the group chat for now (<see cref="Say"/>), each line with the id of its subtitles
/// (<c>docs/trailer/tourist.subtitles.json</c>), to become a bubble over its speaker.
/// </para>
/// </summary>
public static class TouristScript
{
    /// <summary>
    /// "Mighty Like Us", Kevin MacLeod (incompetech.com), CC BY 4.0: 90 BPM (incompetech's figure, the
    /// comb fits it to the end), the first downbeat at 0.624 s, 88 bars of music to 235.3 s; the film
    /// runs two bars more, to 240.62 s, the end card over the silence.
    /// </summary>
    internal static readonly Song MightyLikeUs = new("Mighty Like Us", 90.0, 0.624, 0.624 + 90 * 240.0 / 90.0,
        "\"Mighty Like Us\" Kevin MacLeod (incompetech.com)\nLicensed under Creative Commons: By Attribution 4.0",
        "https://incompetech.com/music/royalty-free/mp3-royaltyfree/Mighty%20Like%20Us.mp3");

    private static readonly Song S = MightyLikeUs;

    // ---- the cast: the story film's four friends, and the tourist ------------------------------

    /// <summary>
    /// The alien, until a figure of its own exists (see the script): bald, pale, a galaxy hoodie,
    /// neon gloves and star glasses, a headset for antennae. The skin palette has no green.
    /// </summary>
    internal static readonly Character Blip = new("BLIP", new Color("9cff57"),
        new Appearance(BodyBuild.Lean, 9, 2, 0, HairStyle.None, 0),
        [ItemId.GalaxyHoodie, ItemId.CargoPants, ItemId.NeonGloves, ItemId.StarGlasses, ItemId.NeonHeadset, ItemId.DiscoPlatforms]);

    /// <summary>The disguises it tries in the morning (shots 12-14).</summary>
    private static readonly Character BlipWitch = Blip with { Wears = [ItemId.WitchRobe, ItemId.StarGlasses, ItemId.DiscoPlatforms] };
    private static readonly Character BlipWorker = Blip with { Wears = [ItemId.HardHat, ItemId.HiVisVest, ItemId.CargoPants, ItemId.CombatBoots, ItemId.StarGlasses] };
    private static readonly Character BlipArmy = Blip with { Wears = [ItemId.TazJacket, ItemId.TazTrousers, ItemId.CombatBoots, ItemId.StarGlasses] };

    /// <summary>The Battle Royale opponent BLIP knocks out (a bot in the game).</summary>
    private static readonly Character Bot = new("BOT", new Color("b4b4b4"),
        new Appearance(BodyBuild.Stocky, 4, 7, 5, HairStyle.Short, 0), [ItemId.TazJacket, ItemId.TazTrousers, ItemId.CombatBoots, ItemId.MaskBlack]);

    // ---- the dialogue: one id a line, the English here, every language in the subtitles file ----

    /// <summary>
    /// The lines, by subtitle id (<c>docs/trailer/tourist.subtitles.json</c> holds them with their
    /// fr, de, it and rm). BLIP speaks in beeps and symbols; NINA translates. At most 8 words.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Lines = new Dictionary<string, string>
    {
        ["s03_what"] = "WHAT WAS THAT?!",
        ["s04_police"] = "call the police?",
        ["s04_tv"] = "call the TV!",
        ["s05_blip"] = "◉ ◉ ... BLIP?",
        ["s06_hi"] = "...hi?",
        ["s07_bloop"] = "BLOOP ▲✦◆ ☾☾ ↑",
        ["s07_broken"] = "it's broken. its mothership comes tomorrow night.",
        ["s08_alien"] = "you speak alien??",
        ["s08_milan"] = "i fly to milan a lot",
        ["s09_tour"] = "so... want the tour?",
        ["s09_yes"] = "✦✦✦ !!!",
        ["s11_no"] = "no.",
        ["s12_no"] = "still no.",
        ["s13_perfect"] = "perfect. nobody will notice.",
        ["s14_here"] = "we're here.",
        ["s14_wow"] = "◉ ◉ ✦",
        ["s15_shark"] = "it bought a shark.",
        ["s16_pedals"] = "right pedal go. left pedal STOP.",
        ["s16_beep"] = "BLIP ✓",
        ["s17_again"] = "AGAIN!",
        ["s17_no"] = "no.",
        ["s18_pedal"] = "pedal!! PEDAL!",
        ["s20_pick"] = "74 bikes. pick one.",
        ["s20_all"] = "ALL ✦",
        ["s24_luck"] = "beginner's luck.",
        ["s25_song"] = "♪ ♪ ♪",
        ["s26_natural"] = "it's a natural.",
        ["s28_home"] = "☾ ... ◯ ♥",
        ["s28_homesick"] = "homesick?",
        ["s29_matterhorn"] = "◉ ◉ ✦ !!!",
        ["s30_br"] = "last stop: battle royale.",
        ["s34_shiny"] = "SHINY ✦",
        ["s36_zone"] = "the zone! RUN!",
        ["s38_won"] = "its first game. it WON.",
        ["s40_radio"] = "♪ BLIP ♪ BLOOP ♪",
        ["s41_who"] = "WHO TAUGHT IT THAT?!",
        ["s44_slow"] = "too slow ◇",
        ["s47_dad"] = "dad will love this.",
        ["s50_party"] = "♪ ✦ ♪ ✦ ♪",
        ["s51_more"] = "MORE HOLIDAYS ✦",
        ["s53_filmed"] = "we filmed everything.",
        ["s54_ride"] = "your ride.",
        ["s55_thanks"] = "merci. danke. grazie. grazia. ✦",
        ["s57_car"] = "HEY! MY CAR!",
    };

    /// <summary>A line of dialogue at <paramref name="t"/>: the chat for now, a speech bubble in stage 2 (#862).</summary>
    private static Line Say(double t, Character? who, string id) => new(t, who, Lines[id]);

    // ---- places ---------------------------------------------------------------------------------

    /// <summary>Riddes (VS): the farm on the east edge of the village, its house and the field below it (crop parcels running NE).</summary>
    private static readonly Spot Farm = new(2583392, 1113007);
    private static readonly Spot Yard = new(2583405, 1112990);
    private static readonly Spot Crash = new(2583470, 1112930);
    /// <summary>The IKEA at Riddes (<c>Interiors.Landmarks</c>): its entrance at the west end, the car park north-west.</summary>
    private static readonly Spot IkeaDoor = new(2582250, 1112640), IkeaPark = new(2582300, 1112700);
    /// <summary>The Riddes straight (the showcase's crash), west end and east end.</summary>
    private static readonly Spot RiddesW = new(2583100, 1113236), RiddesE = new(2583390, 1113236);
    /// <summary>Chillon: the shore by the castle where the boats get in.</summary>
    private static readonly Spot ChillonShore = ChillonBoard;
    /// <summary>Lauterbrunnen's main street (the showcase's rider), and the meadow south of it.</summary>
    private static readonly Spot Lauter = new(2635922, 1160430), LauterNorth = new(2635925, 1160900);
    private static readonly Spot Meadow = new(2635900, 1159700);

    /// <summary>Pully's building site, where the excavator works (the story film's).</summary>
    private static readonly Spot PullySiteSpot = new(2542905, 1152292);
    /// <summary>The Theodul slope of the story film, falling west toward the Matterhorn.</summary>
    private static readonly Spot Slope = new(2621770, 1089700);

    private const float Moonlight = 1.2f;
    private const int Shrug = 5;
    private const int Torch = (int)ItemId.Torch;
    /// <summary>Fist fight poses (<see cref="FootPlayer.FightPose"/>): stances, and 16 + a move.</summary>
    private const int Guard = 2, HitStun = 6, Down = 7, Victory = 9, Jab = 17, Kick = 18, Uppercut = 22;

    /// <summary>The saucer's stand-in until the UFO exists: a grey disc-sized box on the ground.</summary>
    // UFO (#861) in stage 2: the saucer itself, landed (legs down, ramp open when it says so)
    private static Prop Saucer(Spot at) => new(at, 30, new Vector3(7f, 1.4f, 7f), new Color(0.55f, 0.58f, 0.62f));

    /// <summary>A walker that keeps its own looks: with foot actions it is an NPC body, a rider index of its own.</summary>
    private static Cast Walker(Character who, Spot at, float heading, Vector2 wish, bool run = false, int item = 0) => new()
    {
        Who = who, At = at, Heading = heading, Drive = Drive.Walk, Item = item,
        Walk = _ => (wish, run), Actions = _ => (false, false),
    };

    public static readonly IReadOnlyList<Shot> Shots = new List<Shot>
    {
        // ---- act one: the crash (night, Riddes) -----------------------------------------------------
        new()
        {
            // UFO (#861) in stage 2: a light streaks across the stars and dives toward the field
            Number = 1, Name = "A light in the sky", FromBar = 1, Bars = 3, Song = S, Hour = 23.6, Moon = Moonlight, FadeIn = 2.0,
            Feature = "night, the real Rhône valley",
            Keys =
            [
                new(0, Pt.At(2583300, 1112700, 40), Pt.Dir(60, 22), 28),
                new(8.6, Pt.At(2583330, 1112730, 30), Pt.At(Crash.E, Crash.N, 2), 28),
            ],
            Preroll = 0.5,
            Supers = [new(2.2, 4.0, "RIDDES (VS) · 23:40")],
        },
        new()
        {
            // UFO (#861) in stage 2: the saucer wobbles in low over the field, smoking, and skids to a stop
            Number = 2, Name = "The crash", FromBar = 4, Bars = 2, Song = S, Hour = 23.6, Moon = Moonlight,
            Feature = "the UFO",
            Props = [Saucer(Crash)],
            Keys =
            [
                new(0, Pt.At(2583420, 1112870, 1.4f), Pt.At(Crash.E, Crash.N, 6), 35),
                new(5.3, Pt.At(2583425, 1112872, 1.2f), Pt.At(Crash.E, Crash.N, 1.5f), 35),
            ],
            Preroll = 0.5, Shake = 0.6f,
        },
        new()
        {
            Number = 3, Name = "Jonas's door", FromBar = 6, Bars = 2, Song = S, Hour = 23.7, Moon = Moonlight,
            Feature = "real buildings and their doors, a torch",
            // out of the farmhouse into the yard (the nearest door index found none within 40 m of the house)
            Cast = [new() { Who = Jonas, At = Yard, Heading = 135, Item = Torch }],
            Keys =
            [
                new(0, Pt.On(0, 1.4f, 1.3f, -4.2f), Pt.On(0, 0f, 1.3f), 28),
                new(5.3, Pt.On(0, 1.1f, 1.4f, -3.4f), Pt.On(0, 0f, 1.4f), 28),
            ],
            Smooth = 0.3f, Preroll = 0.4,
            Chat = [Say(0.6, Jonas, "s03_what")],
        },
        new()
        {
            Number = 4, Name = "Toward the glow", FromBar = 8, Bars = 2, Song = S, Hour = 23.7, Moon = Moonlight,
            Feature = "on foot, friends together",
            Cast =
            [
                Walker(Jonas, Farm.Toward(135, 18), 135, new Vector2(0f, 0.6f), item: Torch),
                Walker(Lea, Farm.Toward(135, 16).Toward(45, 1.6f), 135, new Vector2(0f, 0.6f)),
                Walker(Marco, Farm.Toward(135, 15).Toward(225, 1.6f), 135, new Vector2(0f, 0.6f), item: Torch),
                Walker(Nina, Farm.Toward(135, 13.5f), 135, new Vector2(0f, 0.6f)),
            ],
            Props = [Saucer(Crash)],
            Keys =
            [
                new(0, Pt.On(3, 1.2f, 1.7f, 5f), Pt.At(Crash.E, Crash.N, 2), 28),
                new(5.3, Pt.On(3, 1.0f, 1.7f, 4.5f), Pt.At(Crash.E, Crash.N, 2), 28),
            ],
            Smooth = 0.3f, Preroll = 1,
            Chat = [Say(0.8, Lea, "s04_police"), Say(2.8, Marco, "s04_tv")],
        },
        new()
        {
            // UFO (#861) in stage 2: the hatch opens, the ramp comes down, BLIP walks down it
            Number = 5, Name = "BLIP", FromBar = 10, Bars = 2, Song = S, Hour = 23.75, Moon = Moonlight,
            Feature = "the UFO, the alien",
            Cast = [Walker(Blip, Crash.Toward(315, 5), 315, new Vector2(0f, 0.25f))],
            Props = [Saucer(Crash)],
            Keys =
            [
                new(0, Pt.At(Crash.Toward(315, 13).E, Crash.Toward(315, 13).N, 1.3f), Pt.On(0, 0f, 1.5f), 35),
                new(5.3, Pt.At(Crash.Toward(315, 12).E, Crash.Toward(315, 12).N, 1.2f), Pt.On(0, 0f, 1.6f), 50),
            ],
            Smooth = 0.3f, Preroll = 0.5,
            Chat = [Say(2.4, Blip, "s05_blip")],
        },
        new()
        {
            Number = 6, Name = "Hi?", FromBar = 12, Bars = 1, Song = S, Hour = 23.75, Moon = Moonlight,
            Feature = "emotes",
            Cast = Row(Crash.Toward(315, 9), 135, (Lea, 0), (Marco, Wave), (Nina, 0), (Jonas, 0)),
            Keys =
            [
                new(0, Ground(Crash.Toward(315, 9), 135, 3.6f, 1.5f), Ground(Crash.Toward(315, 9), 0, 0, 1.5f), 35),
                new(2.67, Ground(Crash.Toward(315, 9), 135, 3.2f, 1.5f), Ground(Crash.Toward(315, 9), 0, 0, 1.5f), 35),
            ],
            Preroll = 0.5,
            Chat = [Say(0.6, Marco, "s06_hi")],
        },
        new()
        {
            Number = 7, Name = "It explains", FromBar = 13, Bars = 2, Song = S, Hour = 23.8, Moon = Moonlight,
            Feature = "the alien, emotes",
            Cast =
            [
                new() { Who = Blip, At = Crash.Toward(315, 5.5f), Heading = 315, Dance = ArmWave },
                new() { Who = Nina, At = Crash.Toward(315, 8.5f), Heading = 135 },
            ],
            Props = [Saucer(Crash)],
            Keys =
            [
                new(0, Pt.On(1, 0.8f, 1.6f, 1.6f), Pt.On(0, 0f, 1.3f), 35),
                new(5.3, Pt.On(1, 0.7f, 1.6f, 1.3f), Pt.On(0, 0f, 1.3f), 35),
            ],
            Preroll = 0.5,
            Chat = [Say(0.3, Blip, "s07_bloop"), Say(2.6, Nina, "s07_broken")],
        },
        new()
        {
            Number = 8, Name = "You speak alien?", FromBar = 15, Bars = 1, Song = S, Hour = 23.8, Moon = Moonlight,
            Feature = "friends together",
            Cast =
            [
                new() { Who = Marco, At = Crash.Toward(315, 9).Toward(45, 1.2f), Heading = 225 },
                new() { Who = Nina, At = Crash.Toward(315, 9).Toward(225, 1.2f), Heading = 45, Dance = Shrug },
            ],
            Keys =
            [
                new(0, Ground(Crash.Toward(315, 9), 135, 3.4f, 1.6f), Ground(Crash.Toward(315, 9), 0, 0, 1.5f), 35),
                new(2.67, Ground(Crash.Toward(315, 9), 140, 3.2f, 1.6f), Ground(Crash.Toward(315, 9), 0, 0, 1.5f), 35),
            ],
            Preroll = 0.5,
            Chat = [Say(0.1, Marco, "s08_alien"), Say(1.3, Nina, "s08_milan")],
        },
        new()
        {
            Number = 9, Name = "The tour", FromBar = 16, Bars = 1, Song = S, Hour = 23.8, Moon = Moonlight,
            Feature = "emotes",
            Cast =
            [
                new() { Who = Jonas, At = Crash.Toward(315, 8.5f), Heading = 135, Item = Torch },
                new() { Who = Blip, At = Crash.Toward(315, 5.5f), Heading = 315, Dance = Cheer },
            ],
            Props = [Saucer(Crash)],
            Keys =
            [
                new(0, Pt.On(0, -0.9f, 1.7f, 1.8f), Pt.On(1, 0f, 1.2f), 35),
                new(2.67, Pt.On(0, -0.8f, 1.7f, 1.6f), Pt.On(1, 0f, 1.3f), 35),
            ],
            Preroll = 0.4,
            Chat = [Say(0.1, Jonas, "s09_tour"), Say(1.4, Blip, "s09_yes")],
        },
        new()
        {
            // UFO (#861) in stage 2: the saucer under the camouflage net at dawn
            Number = 10, Name = "Title", FromBar = 17, Bars = 2, Song = S, Hour = 6.2, MinutesPerDay = 6f,
            Feature = "dawn, a camouflage net (gadgets)",
            Props = [Saucer(Crash), new(Crash, 30, default, default) { Placed = PlacedKind.CamoNet }],
            Keys =
            [
                new(0, Ground(Crash, 290, 30, 6f), Pt.At(Crash.E, Crash.N, 1.5f), 35),
                new(5.3, Ground(Crash, 300, 26, 5f), Pt.At(Crash.E, Crash.N, 1.5f), 35),
            ],
            Preroll = 0.5,
            Captions = [new(0.4, 4.6, "THE ALIEN TOURIST", "a UnitSport Switzerland film", Title: true)],
            Supers = [new(0.3, 4.0, "RIDDES · 06:10")],
        },

        // ---- act two: the morning, first tries (section B) ----------------------------------------
        Disguise(11, 19, BlipWitch, Lea, "s11_no"),
        Disguise(12, 20, BlipWorker, Lea, "s12_no"),
        Disguise(13, 21, BlipArmy, Marco, "s13_perfect"),
        new()
        {
            // the polaroid is a screenshot of the game's map (--map --uishot), see the script
            Number = 14, Name = "We're here", FromBar = 22, Bars = 2, Song = S, Hour = 8.0,
            Feature = "the terrain download map",
            Cast =
            [
                new() { Who = Lea, At = Yard, Heading = 200, Item = (int)ItemId.Camera },
                new() { Who = BlipArmy, At = Yard.Toward(110, 1.3f), Heading = 250 },
            ],
            Keys =
            [
                new(0, Pt.On(0, -1.2f, 1.6f, -3.4f), Pt.On(0, 0.6f, 1.3f), 28),
                new(5.3, Pt.On(0, -1.0f, 1.6f, -3.0f), Pt.On(0, 0.6f, 1.3f), 28),
            ],
            Preroll = 0.5,
            Photo = 0.5, PhotoFile = "test_output/trailer/tourist/screens/map.png",
            Chat = [Say(0.9, Lea, "s14_here"), Say(3.0, Blip, "s14_wow")],
        },
        new()
        {
            Number = 15, Name = "IKEA", FromBar = 24, Bars = 2, Song = S, Hour = 9.5,
            Feature = "shops, IKEA, items",
            Cast =
            [
                Walker(BlipArmy, IkeaDoor.Toward(320, 4), 320, new Vector2(0f, 0.5f), item: (int)ItemId.Blahaj),
                Walker(Marco, IkeaDoor.Toward(320, 4).Toward(50, 1.5f), 320, new Vector2(0f, 0.5f)),
            ],
            Keys =
            [
                new(0, Ground(IkeaDoor, 320, 14, 1.4f), Pt.On(0, 0f, 1.2f), 28),
                new(5.3, Ground(IkeaDoor, 318, 15, 1.5f), Pt.On(0, 0f, 1.3f), 28),
            ],
            Smooth = 0.3f, Preroll = 0.5,
            Chat = [Say(2.0, Marco, "s15_shark")],
        },
        new()
        {
            Number = 16, Name = "The driving lesson", FromBar = 26, Bars = 2, Song = S, Hour = 10.0, Traffic = 0,
            Feature = "cars, the cockpit view",
            Cast = [new() { Who = BlipArmy, Ride = Car(Ae86), At = RiddesW, Toward = RiddesE, Drive = Drive.Follow, Route = "riddes", Arc = 20, Speed = 9, FirstPerson = true }],
            Keys =
            [
                new(0, Pt.Cockpit(0, -0.7f, 0.12f), Pt.Cockpit(0, 30f, -1.5f), 28),
                new(5.3, Pt.Cockpit(0, -0.7f, 0.12f), Pt.Cockpit(0, 30f, -1.5f), 28),
            ],
            Preroll = 3,
            Chat = [Say(0.3, Marco, "s16_pedals"), Say(3.0, Blip, "s16_beep")],
        },
        new()
        {
            Number = 17, Name = "Through the windscreen", FromBar = 28, Bars = 2, Song = S, Hour = 10.2, Traffic = 0,
            Feature = "the crash, through the windscreen",
            Cast = [new() { Who = BlipArmy, Ride = Car(Ae86), At = RiddesW, Toward = RiddesE, Drive = Drive.Follow, Route = "riddes", Arc = 60, Speed = 24 }],
            Props = [new(default, 0, new Vector3(12f, 3f, 0.8f), new Color(0.62f, 0.6f, 0.56f)) { Route = "riddes", Arc = 150 }],
            Keys =
            [
                new(0, Pt.Road("riddes", 125, -3.5f, 1.3f), Pt.Road("riddes", 151, 0f, 1.0f), 35),
                new(5.3, Pt.Road("riddes", 127, -3.5f, 1.3f), Pt.Road("riddes", 156, 0f, 0.6f), 35),
            ],
            Preroll = 4.7,
            Chat = [Say(2.8, Blip, "s17_again"), Say(4.0, Marco, "s17_no")],
        },
        new()
        {
            Number = 18, Name = "Pedal!", FromBar = 30, Bars = 1, Song = S, Hour = 11.0,
            Feature = "the road bike, villages",
            Cast =
            [
                new() { Who = BlipArmy, Ride = RideKind.RoadBike, At = Lauter, Toward = LauterNorth, Drive = Drive.Road, Route = "lauter", Skill = 0.6f },
                new() { Who = Lea, Ride = RideKind.RoadBike, At = Lauter, Toward = LauterNorth, Drive = Drive.Road, Route = "lauter", Arc = -6, Skill = 1f },
            ],
            Keys =
            [
                new(0, Pt.At(2635929, 1160478, 1.0f), Pt.On(0, 0f, 1.2f), 28),
                new(2.67, Pt.At(2635929, 1160479, 1.0f), Pt.On(0, 0f, 1.2f), 28),
            ],
            Smooth = 0.15f, Preroll = 4.6,
            Chat = [Say(0.4, Lea, "s18_pedal")],
        },
        new()
        {
            Number = 19, Name = "Wheee", FromBar = 31, Bars = 1, Song = S, Hour = 11.5,
            Feature = "skis",
            Cast =
            [
                new()
                {
                    Who = BlipArmy, Ride = RideKind.Skis, At = Slope, Heading = 290, Drive = Drive.Controls,
                    Controls = t => new RideInput(0f, 0f, 0.45f * Mathf.Sin((float)t * 3.1f), false),
                },
            ],
            Keys =
            [
                new(0, Pt.On(0, 2.5f, 1.5f, 5f), Pt.On(0, -0.5f, 0.3f, -10f), 28),
                new(2.67, Pt.On(0, 2.0f, 1.4f, 4.5f), Pt.On(0, -0.5f, 0.3f, -10f), 28),
            ],
            Smooth = 0.08f, Preroll = 2,
        },
        new()
        {
            Number = 20, Name = "74 bikes", FromBar = 32, Bars = 2, Song = S, Hour = 12.0, Traffic = 0,
            Feature = "74 motorbikes",
            Cast =
            [
                .. new[] { 0, 5, 11, 20, 30, 41, 52, 66 }.Select((m, i) => new Cast
                {
                    Ride = Moto(m), At = RiddesW.Toward(90, 40 + i * 3.2f), Heading = 0, Seed = 300 + i,
                    Who = i == 4 ? BlipArmy : null,
                }),
                new() { Who = Marco, At = RiddesW.Toward(90, 36).Toward(0, 2.5f), Heading = 100 },
            ],
            Keys =
            [
                new(0, Ground(RiddesW.Toward(90, 38), 0, 7, 1.4f), Ground(RiddesW.Toward(90, 48), 0, 0, 0.9f), 28),
                new(5.3, Ground(RiddesW.Toward(90, 58), 0, 7, 1.4f), Ground(RiddesW.Toward(90, 64), 0, 0, 0.9f), 28),
            ],
            Preroll = 0.5,
            Chat = [Say(0.4, Marco, "s20_pick"), Say(2.8, Blip, "s20_all")],
        },
        new()
        {
            Number = 21, Name = "Lean", FromBar = 34, Bars = 1, Song = S, Hour = 13.0, Traffic = 0,
            Feature = "motorbikes, the Furka",
            Cast =
            [
                new() { Who = Marco, Ride = Car(Bnr32), At = Furka, Toward = FurkaTop, Drive = Drive.Follow, Route = "lean", Arc = 460, Speed = 20 },
                new() { Who = BlipArmy, Ride = Moto(Monster), At = Furka, Toward = FurkaTop, Drive = Drive.Follow, Route = "lean", Arc = 430, Speed = 23 },
            ],
            Keys =
            [
                new(0, Pt.On(1, -3.6f, 0.9f, 3.5f), Pt.On(0, 0f, 0.8f), 35),
                new(2.67, Pt.On(1, -3.4f, 0.9f, 1.6f), Pt.On(0, 0f, 0.8f), 35),
            ],
            Smooth = 0.12f, Preroll = 6,
        },

        // ---- the afternoon: water and air (section C, A') -------------------------------------------
        new()
        {
            Number = 22, Name = "Chillon", FromBar = 35, Bars = 2, Song = S, Hour = 14.0, Sea = 0.25f,
            Feature = "jetski, speedboat, the castle",
            Cast =
            [
                new() { Who = Blip, Ride = RideKind.Jetski, At = new Spot(2560693, 1140362), Heading = 315, Board = ChillonBoard, Launch = 15, Drive = Drive.Controls, Controls = t => Pedal(1f, 0.1f * Mathf.Sin((float)t * 1.6f)) },
                new() { Who = Lea, Ride = RideKind.Speedboat, At = new Spot(2560689, 1140349), Heading = 315, Board = ChillonBoard, Launch = 14, Drive = Drive.Controls, Controls = _ => Pedal(1f) },
            ],
            Keys =
            [
                new(0, Pt.Alt(2560628, 1140350, 373.4f), Pt.At(2560720, 1140445, 9), 35),
                new(5.3, Pt.Alt(2560626, 1140348, 373.4f), Pt.At(2560714, 1140445, 9), 35),
            ],
            Preroll = 1.0,
        },
        new()
        {
            Number = 23, Name = "The steamer", FromBar = 37, Bars = 1, Song = S, Hour = 14.5,
            Feature = "the CGN paddle steamer, Lavaux",
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
                new(2.67, Pt.At(2547040, 1149052, 26), Pt.On(0, 0f, 3f, 25f), 35),
            ],
            Smooth = 0.4f, Preroll = 4,
        },
        new()
        {
            Number = 24, Name = "Beginner's luck", FromBar = 38, Bars = 2, Song = S, Hour = 15.0,
            Feature = "fishing, swimming",
            Cast =
            [
                new() { Who = Nina, At = ChillonShore, Heading = 230, Item = (int)ItemId.FishingRod },
                // swims in toward the shore with a pike (a walker swims on WalkControls, #837)
                new() { Who = Blip, At = ChillonShore.Toward(230, 9), Heading = 50, Drive = Drive.Walk, Walk = _ => (new Vector2(0f, 0.4f), false), Item = (int)ItemId.Pike },
            ],
            Keys =
            [
                new(0, Ground(ChillonShore, 175, 6, 1.6f), Ground(ChillonShore, 230, 4, 0.6f), 28),
                new(5.3, Ground(ChillonShore, 170, 5.5f, 1.6f), Ground(ChillonShore, 230, 3, 0.8f), 28),
            ],
            Preroll = 0.5,
            Chat = [Say(3.0, Nina, "s24_luck")],
        },
        new()
        {
            Number = 25, Name = "Paraglider", FromBar = 40, Bars = 2, Song = S, Hour = 15.5,
            Feature = "the paraglider, Interlaken",
            Cast = [new() { Who = Blip, Ride = RideKind.Paraglider, At = new Spot(2632479, 1170652, 380), Heading = 250, Drive = Drive.Fly, Launch = 10, Flight = _ => Stick(0.12f) }],
            Keys = Around(0, 26f, 4f, 120f, 30f, 5.3, 35, lookUp: 3f),
            Smooth = 0.3f, Preroll = 2,
            Chat = [Say(1.2, Blip, "s25_song")],
        },

        // ---- the breakdown: the quiet afternoon -----------------------------------------------------
        new()
        {
            Number = 26, Name = "A natural", FromBar = 42, Bars = 1, Song = S, Hour = 16.0,
            Feature = "the light plane, the Aletsch glacier",
            Cast = [new() { Who = Blip, Ride = RideKind.Plane, At = Spot.Alt(2646200, 1151600, 3150), Heading = 145, Drive = Drive.Fly, Launch = 55, Flight = _ => Stick(lever: 1f) }],
            Keys =
            [
                new(0, Pt.On(0, -14f, 3f, 16f), Pt.On(0, 0f, -2f, -25f), 35),
                new(2.67, Pt.On(0, -11f, 2f, 11f), Pt.On(0, 0f, -2f, -25f), 35),
            ],
            Smooth = 0.15f, Preroll = 1,
            Chat = [Say(0.6, Nina, "s26_natural")],
        },
        new()
        {
            Number = 27, Name = "Pigeons", FromBar = 43, Bars = 1, Song = S, Hour = 16.2,
            Feature = "play as a pigeon, birds, Bern",
            Cast = [new() { Ride = RideKind.Pigeon, At = KramgasseEast.Up(15), Heading = 268, Drive = Drive.Fly, Launch = 14, Flight = t => Stick(0f, -0.5f, up: t % 0.9 < 0.3 ? 1f : 0f) }],
            Keys =
            [
                new(0, Pt.On(0, 0.4f, 0.6f, 2.2f), Pt.On(0, 0f, -0.8f, -8f), 24),
                new(2.67, Pt.On(0, 0.2f, 0.5f, 2.0f), Pt.On(0, 0f, -1.2f, -8f), 24),
            ],
            Smooth = 0.05f, Preroll = 0.8,
        },
        new()
        {
            Number = 28, Name = "Homesick", FromBar = 44, Bars = 2, Song = S, Hour = 16.6,
            Feature = "Lavaux, the lake",
            Props =
            [
                new(new Spot(2542880, 1151142), 248, default, default) { Seated = Blip },
                new(new Spot(2542880, 1151142).Toward(338, 1.0f), 248, default, default) { Seated = Lea },
            ],
            Keys =
            [
                new(0, Ground(new Spot(2542880, 1151142), 68, 4.5f, 1.6f), Pt.Dir(248, -4), 35),
                new(5.3, Ground(new Spot(2542880, 1151142), 68, 4.0f, 1.5f), Pt.Dir(248, -4), 35),
            ],
            Preroll = 0.5,
            Chat = [Say(0.6, Blip, "s28_home"), Say(3.0, Lea, "s28_homesick")],
        },
        new()
        {
            Number = 29, Name = "The Matterhorn", FromBar = 46, Bars = 2, Song = S, Hour = 16.9,
            Feature = "the Gornergrat and the Matterhorn, the camera and its photo",
            Cast =
            [
                new() { Who = Blip, At = Crew.Toward(270, 1.6f), Heading = 250, Item = (int)ItemId.Camera, Dance = 0 },
                new() { Who = Nina, At = Crew.Toward(270, 1.6f).Toward(0, 1.3f), Heading = 250, Dance = Wave },
            ],
            Keys =
            [
                new(0, Pt.On(0, 1.2f, 1.7f, 4.5f), Pt.Alt(Matterhorn.E, Matterhorn.N, 3900), 35),
                new(5.3, Pt.On(0, 0.8f, 1.8f, 3.8f), Pt.Alt(Matterhorn.E, Matterhorn.N, 3950), 35),
            ],
            Preroll = 0.5,
            Photo = 2.6,
            PhotoFrom = new Key(0, Pt.On(0, 0f, 1.6f, -0.3f), Pt.Alt(Matterhorn.E, Matterhorn.N, 4100), 35),
            Supers = [new(0.3, 2.2, "GORNERGRAT · 3135 M")],
            Chat = [Say(0.6, Blip, "s29_matterhorn")],
        },
        new()
        {
            Number = 30, Name = "Last stop", FromBar = 48, Bars = 1, Song = S, Hour = 17.2,
            Feature = "the Battle Royale plane",
            Cast =
            [
                new() { Who = Nina, Ride = RideKind.Freighter, At = new Spot(2497250, 1121000), Heading = 226, Drive = Drive.Stand, Doors = 1 << 3 },
                new() { Who = Blip, At = new Spot(2497250, 1121000).Toward(46, 26).Toward(316, 3), Heading = 226 },
            ],
            Keys =
            [
                new(0, Pt.On(0, -14f, 2.0f, -22f), Pt.On(0, 0f, 3.5f, -8f), 35),
                new(2.67, Pt.On(0, -13f, 2.0f, -21f), Pt.On(0, 0f, 3.5f, -8f), 35),
            ],
            Preroll = 0.5,
            Chat = [Say(0.2, Nina, "s30_br")],
        },

        // ---- the drop: Battle Royale (section D) ----------------------------------------------------
        new()
        {
            Number = 31, Name = "Jump", FromBar = 49, Bars = 1, Song = S, Hour = 17.4,
            Feature = "the Battle Royale drop, wingsuits",
            Cast =
            [
                new() { Who = Nina, Ride = RideKind.Freighter, At = Spot.Alt(2638000, 1163000, 3100), Heading = 200, Doors = 1 << 3, Drive = Drive.Fly, Launch = 72, Flight = _ => Stick(lever: 0.6f) },
                .. new[] { Blip, Lea, Marco, Jonas }.Select((who, i) => new Cast
                {
                    Who = who, Ride = RideKind.Wingsuit, At = Spot.Alt(2638000, 1163000, 3096 - i * 2).Toward(20, 16 + i * 6).Toward(110, (i % 2 - 0.5f) * 3f),
                    Heading = 200, Drive = Drive.Fly, Launch = 58 - i * 3, Climb = -8, Flight = _ => Stick(0f, -0.3f),
                }),
            ],
            Keys =
            [
                new(0, Pt.On(0, 6f, 4f, 26f), Pt.On(1, 0f, 0f), 35),
                new(2.67, Pt.On(0, 6f, 4f, 26f), Pt.On(2, 0f, 0f), 35),
            ],
            Smooth = 0.05f, Preroll = 0.05,
        },
        new()
        {
            Number = 32, Name = "Dive", FromBar = 50, Bars = 1, Song = S, Hour = 17.4,
            Feature = "wingsuit",
            Cast = new[] { Lea, Blip, Marco }.Select((who, i) => new Cast
            {
                Who = who, Ride = RideKind.Wingsuit, At = Spot.Alt(2635950 + (i - 1) * 9, 1158575 + i * 6, 1515 + i * 3), Heading = 180,
                Drive = Drive.Fly, Launch = 50, Climb = -18, Flight = _ => Stick(0f, -0.4f),
            }).ToArray(),
            Keys =
            [
                new(0, Pt.Alt(2635954, 1158500, 1500), Pt.On(1, 0f, 0f), 24),
                new(2.67, Pt.Alt(2635954, 1158500, 1500), Pt.On(1, 0f, 0f), 24),
            ],
            Smooth = 0.05f, Preroll = 0.3, Shake = 0.3f,
        },
        new()
        {
            Number = 33, Name = "Canopy", FromBar = 51, Bars = 1, Song = S, Hour = 17.45,
            Feature = "wingsuit to parachute",
            Cast =
            [
                new()
                {
                    Who = Blip, Ride = RideKind.Wingsuit, At = Meadow.Up(160), Heading = 180, Drive = Drive.Fly, Launch = 40, Climb = -10,
                    Flight = t => Stick(0f, -0.2f) with { Action = t is > 1.0 and < 1.1 },
                },
            ],
            Keys =
            [
                new(0, Pt.On(0, 5f, 1f, 9f), Pt.On(0, 0f, 1.5f), 28),
                new(2.67, Pt.On(0, 6f, 0f, 8f), Pt.On(0, 0f, 3f), 28),
            ],
            Smooth = 0.2f, Preroll = 0.6,
        },
        new()
        {
            Number = 34, Name = "Shiny", FromBar = 52, Bars = 1, Song = S, Hour = 17.5,
            Feature = "loot, items",
            Cast = [new() { Who = Blip, At = Meadow, Heading = 20, Item = (int)ItemId.Pistol, Use = _ => true, Dance = 0 }],
            Props =
            [
                new(Meadow.Toward(20, 1.6f), 40, default, default) { Item = ItemId.ArmorVest, Scale = 1.2f },
                new(Meadow.Toward(60, 1.8f), 10, default, default) { Item = ItemId.FirstAidKit, Scale = 1.3f },
                new(Meadow.Toward(340, 2.0f), 80, default, default) { Item = ItemId.Shotgun, Scale = 1.1f },
                new(Meadow.Toward(100, 1.4f), 0, default, default) { Item = ItemId.Ammo9mm, Scale = 1.4f },
            ],
            Keys =
            [
                new(0, Pt.On(0, 1.3f, 1.2f, -3.2f), Pt.On(0, 0f, 1.2f), 28),
                new(2.67, Pt.On(0, 0.9f, 1.0f, -2.8f), Pt.On(0, 0f, 1.3f), 28),
            ],
            Preroll = 0.4,
            Chat = [Say(0.4, Blip, "s34_shiny")],
        },
        new()
        {
            Number = 35, Name = "Fist fight", FromBar = 53, Bars = 2, Song = S, Hour = 17.55,
            Feature = "fist fights, a bot",
            Cast =
            [
                new() { Who = Blip, At = Lauter, Heading = 0, Fight = t => t < 1.2 ? Guard : t < 1.8 ? Jab : t < 2.6 ? Guard : t < 3.4 ? Kick : t < 4.0 ? Uppercut : Victory },
                new() { Who = Bot, At = Lauter.Toward(0, 1.7f), Heading = 180, Fight = t => t < 1.6 ? Guard : t < 2.2 ? HitStun : t < 3.2 ? Jab : t < 4.1 ? HitStun : Down },
            ],
            Keys = Orbit(Lauter.Toward(0, 0.85f), 4.5f, 4.2f, 1.3f, 1.4f, 80f, 120f, 5.3, 28, lookUp: 1.1f),
            Preroll = 1.4,
        },
        new()
        {
            Number = 36, Name = "The zone", FromBar = 55, Bars = 2, Song = S, Hour = 17.6,
            Feature = "the Battle Royale zone, running",
            Zone = (LauterNorth, 520f),
            Cast =
            [
                Walker(Blip, Lauter.Toward(180, 30), 0, new Vector2(0f, 1f), run: true),
                Walker(Lea, Lauter.Toward(180, 33).Toward(90, 1.8f), 0, new Vector2(0f, 1f), run: true),
                Walker(Jonas, Lauter.Toward(180, 36).Toward(270, 1.5f), 0, new Vector2(0f, 1f), run: true),
            ],
            Keys =
            [
                new(0, Pt.On(0, 1.5f, 1.4f, -6f), Pt.On(1, 0f, 1.2f, 4f), 24),
                new(5.3, Pt.On(0, 1.5f, 1.5f, -6.5f), Pt.On(1, 0f, 1.2f, 4f), 24),
            ],
            Smooth = 0.2f, Preroll = 1,
            Chat = [Say(0.5, Lea, "s36_zone")],
        },
        new()
        {
            Number = 37, Name = "Slide", FromBar = 57, Bars = 1, Song = S, Hour = 17.6,
            Feature = "running and sliding",
            Zone = (LauterNorth, 520f),
            Cast = [new() { Who = Blip, At = Lauter.Toward(180, 20), Heading = 0, Drive = Drive.Walk, Walk = _ => (new Vector2(0f, 1f), true), Actions = t => (false, t > 1.6) }],
            Keys =
            [
                new(0, Pt.On(0, 2.2f, 0.5f, -5f), Pt.On(0, 0f, 0.6f), 24),
                new(2.67, Pt.On(0, 2.0f, 0.4f, -3.5f), Pt.On(0, 0f, 0.5f), 24),
            ],
            Smooth = 0.1f, Preroll = 1.2,
        },
        new()
        {
            Number = 38, Name = "Winner", FromBar = 58, Bars = 1, Song = S, Hour = 17.65,
            Feature = "a Battle Royale win, emotes",
            Cast =
            [
                new() { Who = Blip, At = Lauter, Heading = 200, Fight = _ => Victory },
                new() { Who = Marco, At = Lauter.Toward(200, 4).Toward(110, 1.4f), Heading = 20, Dance = Clap },
                new() { Who = Lea, At = Lauter.Toward(200, 4).Toward(290, 1.4f), Heading = 20, Dance = Cheer },
            ],
            Keys =
            [
                new(0, Pt.On(0, 0.6f, 0.4f, -3.0f), Pt.On(0, 0f, 1.6f), 24),
                new(2.67, Pt.On(0, 0.5f, 0.3f, -2.6f), Pt.On(0, 0f, 1.7f), 24),
            ],
            Preroll = 0.5,
            Chat = [Say(0.5, Marco, "s38_won")],
        },

        // ---- races ----------------------------------------------------------------------------------
        new()
        {
            Number = 39, Name = "Grid", FromBar = 59, Bars = 1, Song = S, Hour = 17.7, Traffic = 0,
            Feature = "races, AI drivers, the Initial D cars",
            Cast = Racers("grid", 1350, goAt: 1.25),
            Keys =
            [
                new(0, Pt.Road("grid", 1362, -3.0f, 0.9f), Pt.On(1, 0f, 0.8f), 50),
                new(2.67, Pt.Road("grid", 1363, -3.2f, 1.0f), Pt.On(1, 0f, 0.8f), 50),
            ],
            Preroll = 1,
        },
        new()
        {
            Number = 40, Name = "Radio on", FromBar = 60, Bars = 2, Song = S, Hour = 17.75, Traffic = 0,
            Feature = "the cockpit, the car radio",
            Cast = [new() { Who = Blip, Ride = Car(Fd3s), At = Furka, Toward = FurkaTop, Drive = Drive.Road, Route = "cockpit", Arc = 900, Skill = 1.1f, FirstPerson = true }],
            Keys =
            [
                new(0, Pt.Cockpit(0, -0.7f, 0.12f), Pt.Cockpit(0, 30f, -1.5f), 28),
                new(5.3, Pt.Cockpit(0, -0.7f, 0.12f), Pt.Cockpit(0, 30f, -1.5f), 28),
            ],
            Preroll = 5,
            Chat = [Say(0.8, Blip, "s40_radio")],
        },
        new()
        {
            Number = 41, Name = "The inside line", FromBar = 62, Bars = 2, Song = S, Hour = 17.8, Traffic = 0,
            Feature = "racecraft, the Furka",
            Cast = Racers("pack", 1380, goAt: 0),
            Keys =
            [
                new(0, Pt.Road("pack", 1500, 0f, 80f), Pt.Road("pack", 1500, 0f, 0f), 35),
                new(5.3, Pt.Road("pack", 1500, 0f, 84f), Pt.Road("pack", 1500, 0f, 0f), 35),
            ],
            Preroll = 6,
            Chat = [Say(3.0, Marco, "s41_who")],
        },

        // ---- the evening: everything else that moves (section E) ------------------------------------
        new()
        {
            Number = 42, Name = "Convoy", FromBar = 64, Bars = 1, Song = S, Hour = 18.0, Traffic = 4,
            Feature = "trucks, buses, army vehicles",
            Cast =
            [
                new() { Who = Blip, Ride = Heavy(Scania), Trailer = Curtainsider, At = Gotthard, Toward = new Spot(2686150, 1154560), Drive = Drive.Follow, Route = "gotthard", Arc = 560, Speed = 14 },
                new() { Ride = Heavy(Setra), At = Gotthard, Toward = new Spot(2686150, 1154560), Drive = Drive.Follow, Route = "gotthard", Arc = 530, Speed = 14, Seed = 310 },
                new() { Ride = Heavy(10), At = Gotthard, Toward = new Spot(2686150, 1154560), Drive = Drive.Follow, Route = "gotthard", Arc = 508, Speed = 14, Seed = 311 },
            ],
            Keys =
            [
                new(0, Pt.Road("gotthard", 745, -45f, 22f), Pt.On(0, 0f, 2f, 10f), 50),
                new(2.67, Pt.Road("gotthard", 745, -45f, 22f), Pt.On(0, 0f, 2f, 10f), 50),
            ],
            Smooth = 0.3f, Preroll = 12,
        },
        new()
        {
            Number = 43, Name = "Helicopter", FromBar = 65, Bars = 1, Song = S, Hour = 18.1,
            Feature = "the helicopter, Lauterbrunnen",
            Cast = [new() { Who = Blip, Ride = RideKind.Helicopter, At = new Spot(2635900, 1159300, 120), Heading = 355, Drive = Drive.Fly, Launch = 30, Flight = _ => Stick(0f, -1f, up: 0.2f, effort: true) }],
            Keys =
            [
                new(0, Pt.On(0, 14f, 5f, 22f), Pt.On(0, 0f, 0f, -12f), 35),
                new(2.67, Pt.On(0, 16f, 4.5f, 19f), Pt.On(0, 0f, 0f, -12f), 35),
            ],
            Smooth = 0.4f, Preroll = 2,
        },
        new()
        {
            Number = 44, Name = "Too slow", FromBar = 66, Bars = 1, Song = S, Hour = 18.2,
            Feature = "airliners, Geneva airport",
            Cast = [new() { Who = Blip, Ride = RideKind.A320, At = new Spot(2496388, 1120293, 40), Heading = 226, Drive = Drive.Fly, Launch = 80, Climb = 8, Flight = _ => Stick(lever: 1f) }],
            Keys =
            [
                new(0, Pt.At(2496200, 1120110, 1.7f), Pt.On(0, 0f, 0f), 28),
                new(2.67, Pt.At(2496200, 1120110, 1.7f), Pt.On(0, 0f, 0f), 28),
            ],
            Smooth = 0.06f, Preroll = 0.4,
            Chat = [Say(0.4, Blip, "s44_slow")],
        },
        new()
        {
            Number = 45, Name = "Heavy metal", FromBar = 67, Bars = 1, Song = S, Hour = 18.6,
            Feature = "the AN-124",
            Cast = [new() { Who = Nina, Ride = RideKind.An124, At = Spot.Alt(2508144, 1137170, 440), Heading = 222, Drive = Drive.Fly, Board = new Spot(2507810, 1137290), Launch = 80, Flight = _ => Stick(lever: 0.7f) }],
            Keys =
            [
                new(0, Pt.Alt(2507780, 1137080, 375.5f), Pt.On(0, 0f, 0f), 35),
                new(2.67, Pt.Alt(2507780, 1137080, 375.5f), Pt.On(0, 0f, 0f), 35),
            ],
            Smooth = 0.06f, Preroll = 1.4,
        },
        new()
        {
            Number = 46, Name = "Overtime", FromBar = 68, Bars = 1, Song = S, Hour = 18.75,
            Feature = "building sites, works machines, tower cranes",
            Cast =
            [
                new() { Who = Blip, Ride = RideKind.Excavator, At = PullySiteSpot, Heading = 30, Drive = Drive.Controls, Controls = _ => Pedal(0.4f, 0.6f) },
                new() { Who = Jonas, Ride = RideKind.WheelLoader, At = new Spot(2542930, 1152305), Heading = 300, Drive = Drive.Controls, Controls = _ => Pedal(0.4f, 0.15f) },
                new() { Ride = RideKind.CompactRoller, At = new Spot(2542880, 1152285), Heading = 80, Drive = Drive.Controls, Controls = _ => Pedal(0.35f), Seed = 5 },
            ],
            Keys =
            [
                new(0, Pt.At(2542935, 1152245, 2.5f), Pt.At(2542895, 1152335, 6), 35),
                new(2.67, Pt.At(2542938, 1152242, 14), Pt.At(2542895, 1152335, 4), 35),
            ],
            Preroll = 2,
        },
        new()
        {
            Number = 47, Name = "Harvest", FromBar = 69, Bars = 2, Song = S, Hour = 19.0,
            Feature = "farming: the tractor and plough, the combine",
            Cast =
            [
                new() { Who = Blip, Ride = Heavy(7), At = Crash.Toward(220, 40), Heading = 40, Lowered = true, Drive = Drive.Controls, Controls = _ => Pedal(0.35f) },
                new() { Who = Jonas, Ride = Heavy(6), Trailer = 6, At = Crash.Toward(220, 40).Toward(130, 14), Heading = 40, Lowered = true, Drive = Drive.Controls, Controls = _ => Pedal(0.4f) },
            ],
            Keys =
            [
                new(0, Pt.On(0, -9f, 3f, -12f), Pt.On(0, 0f, 1.5f), 35),
                new(5.3, Pt.On(0, -10f, 3.5f, -6f), Pt.On(0, 0f, 1.5f), 35),
            ],
            Smooth = 0.3f, Preroll = 2,
            Chat = [Say(1.0, Jonas, "s47_dad")],
        },
        new()
        {
            // UFO (#861) in stage 2: the saucer, its panel open; sparks where the hammer hits
            Number = 48, Name = "Repairs", FromBar = 71, Bars = 1, Song = S, Hour = 19.2,
            Feature = "building with the hammer, the field workbench",
            Cast = [new() { Who = Blip, At = Crash.Toward(300, 4.6f), Heading = 120, Item = (int)ItemId.Hammer, Use = t => t % 0.6 < 0.25 }],
            Props = [Saucer(Crash), new(Crash.Toward(330, 6.5f), 150, default, default) { Placed = PlacedKind.FieldWorkbench }],
            Keys =
            [
                new(0, Pt.On(0, 2.0f, 1.4f, -2.6f), Pt.On(0, -0.5f, 1.3f, -1.5f), 28),
                new(2.67, Pt.On(0, 1.7f, 1.3f, -2.3f), Pt.On(0, -0.5f, 1.3f, -1.5f), 28),
            ],
            Preroll = 0.5,
        },
        new()
        {
            Number = 49, Name = "The sun goes down", FromBar = 72, Bars = 1, Song = S, Hour = 19.4, MinutesPerDay = 1.0f,
            Feature = "day and night, lit windows",
            Keys =
            [
                new(0, Pt.At(2542900, 1151150, 90), Pt.Dir(248, -5), 35),
                new(2.67, Pt.At(2542880, 1151143, 89), Pt.Dir(248, -5.3f), 35),
            ],
            Preroll = 0.5,
        },

        // ---- the night: the party and the goodbye (A'') ---------------------------------------------
        new()
        {
            Number = 50, Name = "Fondue party", FromBar = 73, Bars = 2, Song = S, Hour = 21.3, Moon = Moonlight,
            Feature = "the campfire and cooking, dances, the radio, friends together",
            Cast =
            [
                new() { Who = Blip, At = Yard.Toward(0, 3.0f), Heading = 180, Dance = Robot },
                new() { Who = Lea, At = Yard.Toward(90, 3.0f), Heading = 270, Dance = Chicken },
                new() { Who = Marco, At = Yard.Toward(180, 3.0f), Heading = 0, Dance = Griddy },
                new() { Who = Nina, At = Yard.Toward(270, 3.0f), Heading = 90, Dance = Macarena },
                new() { Who = Jonas, At = Yard.Toward(225, 4.2f), Heading = 45, Item = (int)ItemId.Radio, Radio = true, Dance = 1 },
            ],
            Props =
            [
                new(Yard, 0, default, default) { Placed = PlacedKind.Campfire },
                new(Yard.Toward(60, 1.6f), 0, default, default) { Item = ItemId.FonduePot, Scale = 1.6f },
            ],
            Keys = Orbit(Yard, 7f, 6.5f, 1.6f, 1.9f, 200f, 260f, 5.3, 28, lookUp: 1.0f),
            Preroll = 0.5,
            Chat = [Say(2.0, Blip, "s50_party")],
        },
        new()
        {
            Number = 51, Name = "Halloween", FromBar = 75, Bars = 1, Song = S, Hour = 19.6, Occasion = "halloween",
            Feature = "Halloween",
            Cast =
            [
                new() { Who = Blip, At = KramgasseWest.Toward(90, 60), Heading = 90, Hat = 2, Dance = Cheer },
                new() { Who = Lea, At = KramgasseWest.Toward(90, 60).Toward(180, 1.6f), Heading = 90, Hat = 1, Dance = Wave },
            ],
            Keys =
            [
                new(0, Pt.On(0, -0.8f, 1.5f, -4.5f), Pt.On(0, -0.8f, 1.3f), 28),
                new(2.67, Pt.On(0, -0.8f, 1.5f, -4.0f), Pt.On(0, -0.8f, 1.3f), 28),
            ],
            Preroll = 0.5,
            Chat = [Say(0.4, Blip, "s51_more")],
        },
        new()
        {
            Number = 52, Name = "Christmas", FromBar = 76, Bars = 1, Song = S, Hour = 16.8, Occasion = "christmas",
            Feature = "Christmas",
            Cast =
            [
                new() { Who = Blip, At = Lauter, Heading = 180, Hat = 3, Dance = ArmWave },
                new() { Who = Marco, At = Lauter.Toward(90, 1.5f), Heading = 180, Hat = 4, Dance = Wave },
            ],
            Keys =
            [
                new(0, Pt.On(0, 0.8f, 1.6f, -5f), Pt.On(0, 0.8f, 1.6f), 28),
                new(2.67, Pt.On(0, 0.8f, 1.8f, -6f), Pt.On(0, 0.8f, 1.8f), 28),
            ],
            Preroll = 0.5,
        },
        new()
        {
            // the polaroid is a screenshot of the movie studio (--moviestudio --uishot), see the script
            Number = 53, Name = "We filmed everything", FromBar = 77, Bars = 2, Song = S, Hour = 21.5, Moon = Moonlight,
            Feature = "the movie studio",
            Cast =
            [
                new() { Who = Lea, At = Yard.Toward(300, 5), Heading = 120, Item = (int)ItemId.Camera },
                new() { Who = Blip, At = Yard.Toward(300, 5).Toward(30, 1.3f), Heading = 200 },
            ],
            Props = [new(Yard, 0, default, default) { Placed = PlacedKind.Campfire }],
            Keys =
            [
                new(0, Pt.On(0, 1.2f, 1.6f, -3.4f), Pt.On(0, -0.6f, 1.3f), 28),
                new(5.3, Pt.On(0, 1.0f, 1.6f, -3.0f), Pt.On(0, -0.6f, 1.3f), 28),
            ],
            Preroll = 0.5,
            Photo = 0.6, PhotoFile = "test_output/trailer/tourist/screens/studio.png",
            Chat = [Say(0.2, Lea, "s53_filmed")],
        },
        new()
        {
            // UFO (#861) in stage 2: the mothership slides over the mountains, lights chasing round its rim
            Number = 54, Name = "Your ride", FromBar = 79, Bars = 2, Song = S, Hour = 22.5, Moon = Moonlight,
            Feature = "the mothership",
            Cast = Row(Crash.Toward(315, 9), 135, (Lea, 0), (Blip, 0), (Nina, 0), (Jonas, 0)),
            Keys =
            [
                new(0, Ground(Crash.Toward(315, 9), 315, 5f, 1.0f), Pt.Dir(135, 18), 24),
                new(5.3, Ground(Crash.Toward(315, 9), 315, 6f, 0.8f), Pt.Dir(135, 24), 24),
            ],
            Preroll = 0.5,
            Chat = [Say(2.4, Nina, "s54_ride")],
        },
        new()
        {
            Number = 55, Name = "Souvenirs", FromBar = 81, Bars = 2, Song = S, Hour = 22.55, Moon = Moonlight,
            Feature = "items: the Swiss flag, the fondue pot, the alphorn",
            Cast =
            [
                new() { Who = Blip, At = Crash.Toward(315, 6), Heading = 315, Dance = Cheer, Item = (int)ItemId.Blahaj },
                new() { Who = Lea, At = Crash.Toward(315, 8.5f).Toward(45, 1.4f), Heading = 135, Item = (int)ItemId.SwissFlag },
                new() { Who = Marco, At = Crash.Toward(315, 9f), Heading = 135, Item = (int)ItemId.FonduePot },
                new() { Who = Jonas, At = Crash.Toward(315, 8.5f).Toward(225, 1.4f), Heading = 135, Item = (int)ItemId.Alphorn },
            ],
            Props = [Saucer(Crash)],
            Keys = Orbit(Crash.Toward(315, 7.5f), 5.5f, 5f, 1.5f, 1.7f, 30f, 80f, 5.3, 28, lookUp: 1.2f),
            Preroll = 0.5,
            Chat = [Say(1.0, Blip, "s55_thanks")],
        },
        new()
        {
            // UFO (#861) in stage 2: BLIP at the top of the ramp, the hatch closing behind it
            Number = 56, Name = "Alphorn", FromBar = 83, Bars = 1, Song = S, Hour = 22.6, Moon = Moonlight,
            Feature = "the alphorn",
            Cast = [new() { Who = Blip, At = Crash.Toward(315, 4.2f), Heading = 315, Item = (int)ItemId.Alphorn, Use = _ => true }],
            Props = [Saucer(Crash)],
            Keys =
            [
                new(0, Pt.On(0, 1.6f, 1.0f, -4.5f), Pt.On(0, 0f, 1.5f), 28),
                new(2.67, Pt.On(0, 1.4f, 0.9f, -4.0f), Pt.On(0, 0f, 1.6f), 28),
            ],
            Preroll = 0.5,
        },
        new()
        {
            // UFO (#861) in stage 2: the saucer lifts off; its tractor beam takes Marco's AE86 up with it
            Number = 57, Name = "My car!", FromBar = 84, Bars = 2, Song = S, Hour = 22.65, Moon = Moonlight,
            Feature = "the UFO's tractor beam",
            Cast =
            [
                new() { Ride = Car(Ae86), At = Yard.Toward(150, 8), Heading = 60, Seed = 320 },
                new() { Who = Marco, At = Yard.Toward(150, 8).Toward(240, 3.2f), Heading = 60, Dance = Shrug },
            ],
            Props = [Saucer(Crash)],
            Keys =
            [
                new(0, Ground(Yard.Toward(150, 8), 240, 11, 1.4f), Ground(Yard.Toward(150, 8), 0, 0, 3f), 28),
                new(5.3, Ground(Yard.Toward(150, 8), 240, 12, 1.2f), Ground(Yard.Toward(150, 8), 0, 0, 12f), 28),
            ],
            Preroll = 0.5,
            Chat = [Say(2.0, Marco, "s57_car")],
        },
        new()
        {
            // UFO (#861) in stage 2: the saucer rises into the mothership, which goes, a light among the stars
            Number = 58, Name = "Home", FromBar = 86, Bars = 1, Song = S, Hour = 22.7, Moon = Moonlight,
            Feature = "the mothership",
            Keys =
            [
                new(0, Pt.At(Crash.E, Crash.N, 2), Pt.Dir(135, 35), 35),
                new(2.67, Pt.At(Crash.E, Crash.N, 2), Pt.Dir(135, 50), 35),
            ],
            Preroll = 0.5,
        },
        new()
        {
            Number = 59, Name = "The end", FromBar = 87, Bars = 4, Song = S, Hour = 22.75, Moon = Moonlight, FadeOut = 2.4,
            Feature = "friends together",
            Cast = Row(Crash.Toward(315, 9), 135, (Lea, Wave), (Marco, 0), (Nina, Wave), (Jonas, Wave)),
            Keys =
            [
                new(0, Ground(Crash.Toward(315, 9), 315, 4.5f, 1.6f), Ground(Crash.Toward(315, 9), 0, 0, 1.4f), 28),
                new(10.6, Ground(Crash.Toward(315, 9), 315, 14f, 9f), Pt.Dir(135, 20), 28),
            ],
            Preroll = 0.5,
            Captions =
            [
                new(1.5, 7.4, "UNITSPORT SWITZERLAND", "The Alien Tourist", Title: true),
                new(5.8, 4.4, "", MightyLikeUs.Credit),
            ],
        },
    // Cartoon throughout (no PS1, no Realistic). The occasion is the calendar's but where a shot names
    // one: "none" (/occasion stop) left broken giant shapes over Riddes in the first stills
    }.Select(s => s with { Style = "cartoon" }).ToList();

    /// <summary>Shots 11-13: BLIP's disguises, the same frame each time, a friend's verdict.</summary>
    private static Shot Disguise(int number, int bar, Character look, Character judge, string line) => new()
    {
        Number = number, Name = "Disguise", FromBar = bar, Bars = 1, Song = S, Hour = 7.6,
        Feature = "character looks and clothes",
        Cast =
        [
            new() { Who = look, At = Yard, Heading = 200, Dance = number == 13 ? Salute : 0 },
            new() { Who = judge, At = Yard.Toward(200, 3.2f).Toward(110, 1.4f), Heading = 20 },
        ],
        Keys =
        [
            new(0, Pt.On(0, 0.9f, 1.4f, -3.6f), Pt.On(0, 0f, 1.1f), 35),
            new(2.67, Pt.On(0, 0.9f, 1.4f, -3.4f), Pt.On(0, 0f, 1.1f), 35),
        ],
        Preroll = 0.5,
        Chat = [Say(0.5, judge, line)],
    };

    /// <summary>Four in a row facing <paramref name="heading"/>, 1.2 m apart across it, each with a dance or emote.</summary>
    private static Cast[] Row(Spot c, float heading, params (Character Who, int Dance)[] who) =>
        who.Select((w, i) => new Cast
        {
            Who = w.Who, At = c.Toward(heading + 90, (i - (who.Length - 1) / 2f) * 1.2f), Heading = heading, Dance = w.Dance,
        }).ToArray();

    /// <summary>The Furka race: MARCO in the AE86, BLIP third in the FD3S, four AI drivers.</summary>
    private static Cast[] Racers(string route, float arc, double goAt) =>
        Grid([Supra, Ae86, Fd3s, Evo3, Nsx, S13], Furka, FurkaTop, route, arc, goAt)
            .Select((c, i) => i switch
            {
                1 => c with { Who = Marco },
                2 => c with { Who = Blip, Skill = 1.35f, Aggression = 1f },
                _ => c,
            }).ToArray();
}
