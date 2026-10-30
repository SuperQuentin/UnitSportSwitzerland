using Godot;
using UnitSport.Adventure;
using UnitSport.Adventure.Errands;
using UnitSport.Adventure.Together;
using UnitSport.Combat;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Occasions.Festivals;
using UnitSport.Player;
using UnitSport.Occasions.Festivals.August1;
using UnitSport.Occasions.Festivals.Chalanda;
using UnitSport.Occasions.Festivals.Christmas;
using UnitSport.Occasions.Festivals.Desalpe;
using UnitSport.Occasions.Festivals.Escalade;
using UnitSport.Occasions.Festivals.Fasnacht;
using UnitSport.Occasions.Festivals.Horn;
using UnitSport.Occasions.Festivals.Sechselaeuten;

namespace UnitSport.Playtest.Scenarios;

/// <summary>
/// The Adventure phase 3 (#1270) scenarios (<c>adv3-*</c>, #1390): what <c>tests/TO-TEST-BY-HUMAN.md</c> asks a person to judge,
/// one scenario per feature, each starting at the spot with its date, clock, items and partner set (<see cref="Adv3"/>).
/// They run on the fixture course with the check-only festival, so the festivals stand at the fixture's start; the scenarios
/// that need the real map or a headset to be judged say so in their text. Only the owner's tick of the TO-TEST list deletes a line.
/// </summary>
public static class Adventure3Scenarios
{
    private const string Cat = Adv3.Category;
    private static readonly string[] Look = ["Cartoon, PS1 and Realistic (Settings > Style): it reads well in each, nothing z-fights, no prop floats or sinks"];

    private static string[] Checks(params string[] own) => [.. own, .. Look];

    // ---- Together -------------------------------------------------------------------------------------------

    [PlaytestScenario("adv3-together-hands", "Together: holding hands", Cat)]
    private static PlaytestScenario Hands() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "A stand-in party member (\"Partner\", a body that stands still) is already holding your hand. Walk, turn, stop, run a little: the follower with no input is led along your trail. " +
            "Try `/rope off` to let go and hold E (pad: hold Y) on the Partner for the invite; the stand-in cannot accept it, so the second player's side of the invite is in the TO-TEST list (needs two clients).",
        Checklist = Checks(
            "The two hands meet at hip height and look natural from the third-person camera and from the first person view",
            "Walking away leads the Partner along your trail at a believable distance, no jitter",
            "The Together chip shows up while the Partner is near",
            "Pad: the same invite is a hold of Y; VR: the same through the pad route (gripping a hand is not built)"),
        Covers = Adv3.Dir("src/Adventure/Together/LinkBook.cs", "src/Adventure/Together/Tether.cs", "src/Adventure/Together/TogetherService.Links.cs",
            "src/Adventure/Together/TogetherService.Bodies.cs", "src/Adventure/Together/TogetherChip.cs"),
        Setup = async c =>
        {
            await Adv3.Begin(c, "11:00");
            var me = await c.PutPlayer(c.At(0), c.Heading(0));
            Adv3.Partner(c, me, new Vector3(1.6f, 0.3f, 0.6f));
            await c.Wait(1);
            TogetherService.Instance!.ServeLink(AdventureService.LocalPeer, Adv3.Pal, LinkMode.Hand);
        },
    };

    [PlaytestScenario("adv3-fondue", "Together: the shared fondue pot", Cat)]
    private static PlaytestScenario Fondue() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "A campfire, a fondue pot, cheese, bread and white wine are in your pack, a stand-in Partner stands beside you. Light the campfire on the ground in front of you (select it, aim, Use), then use the fondue pot at the fire: the panel opens and the Partner sits down by itself. " +
            "Add the cheese and the wine, then trace an eight with the mouse (hold LMB) or the right stick. Stop stirring on a high heat to see the cheese split. Keep dipping until you lose your bread: the forfeit popup answers 1-3, D-pad + A or the laser.",
        Checklist = Checks(
            "The panel's size and place are right on the monitor, with a pad, and in a headset",
            "Stirring an eight feels natural (mouse and right stick) and the cheese stays smooth; stopping on a high heat splits it with a hiss",
            "The forfeit popup (line, buy a round, yodel) is readable and answers to the keys, the pad and the laser",
            "The caquelon, the forks and the cheese read well by the fire"),
        Covers = Adv3.Dir("src/Adventure/Together/FondueRules.cs", "src/Adventure/Together/FonduePanel.cs", "src/Adventure/Together/FondueTable.cs",
            "src/Adventure/Together/PotBook.cs", "src/Adventure/Together/StirRules.cs", "src/Adventure/Together/TogetherService.Pots.cs", "src/Adventure/Together/TogetherSounds.cs"),
        Setup = async c =>
        {
            await Adv3.Begin(c, "18:30");
            var me = await c.PutPlayer(c.At(0), c.Heading(0));
            Adv3.Give(c, ItemId.Campfire, 1);
            Adv3.Give(c, ItemId.FonduePot, 1);
            Adv3.Give(c, ItemId.Cheese, 2);
            Adv3.Give(c, ItemId.Bread, 6);
            Adv3.Give(c, ItemId.WhiteWine, 1);
            Adv3.Partner(c, me, new Vector3(1.6f, 0.3f, 0.6f));
        },
        // the stand-in sits down at the pot, as a friend accepting would
        Tick = (_, _) =>
        {
            var svc = TogetherService.Instance;
            if (svc?.MyPot is { } pot && !pot.Seats.Contains(Adv3.Pal)) svc.ServeSit(Adv3.Pal, pot.Id);
        },
    };

    // ---- Crevasses -------------------------------------------------------------------------------------------

    /// <summary>A pretend glacier round the player, and the hidden strip nearest to it: the player is put 10 m before it, facing across.</summary>
    private static async Task<(FootPlayer Me, double E, double N, double Angle)> BeforeAStrip(PlaytestContext c)
    {
        await Adv3.Begin(c, "11:00");
        var me = await c.PutPlayer(c.At(0), c.Heading(0));
        GameSettings.Current.Crevasses = true;
        var box = (E: me.Global.E, N: me.Global.N, R: 150.0);
        TogetherService.GlacierBox = box;
        Adv3.OnClear(c, () => TogetherService.GlacierBox = null);
        (double E, double N, double Angle)? best = null;
        double bestD = double.MaxValue;
        for (double e = box.E - 80; e < box.E + 80; e += Crevasses.Cell)
            for (double n = box.N - 80; n < box.N + 80; n += Crevasses.Cell)
                if (Crevasses.InCell(e, n) is { } cell)
                {
                    double d = (cell.E - box.E) * (cell.E - box.E) + (cell.N - box.N) * (cell.N - box.N);
                    if (d < bestD) { bestD = d; best = cell; }
                }
        if (best is not { } s) throw new InvalidOperationException("no crevasse cell within 80 m of the start");
        double se = -Math.Sin(s.Angle), sn = Math.Cos(s.Angle);
        var at = (s.E - se * 10, s.N - sn * 10);
        await Adv3.Stand(c, at, (s.E, s.N));
        return (me, s.E, s.N, s.Angle);
    }

    [PlaytestScenario("adv3-crevasse", "Hidden crevasse: the fall and the climb out", Cat)]
    private static PlaytestScenario Crevasse() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "A pretend glacier covers the start (the fixture has no ice and no snow: the look of a real glacier is only judged on the real map, Aletsch, Rhône or Morteratsch). A hidden strip lies 10 m ahead across your way: walk straight on. " +
            "The snow bridge breaks, you fall 10 m into the ice slot, unhurt. Look up and sideways, then push into an ice wall (W, the left stick) to climb out. Run it again with Reset, and once wait a full minute at the bottom without climbing.",
        Checklist = Checks(
            "The break (crack sound), the fall and the thud read well; the toast says to climb",
            "The blue ice slot, its dark depth and the sky above look right; looking from the slot, the terrain is not seen from underneath",
            "Pushing into a wall grabs it; the climb speed and the stamina ring feel fair; the top-out onto solid snow is smooth",
            "Waiting 60 s in the slot sets you on the lip: is a minute the right time?",
            "Pad: the left stick into the wall climbs; VR: the same through the pad route"),
        Covers = Adv3.Dir("src/Adventure/Together/Crevasses.cs", "src/Adventure/Together/CrevasseNode.cs", "src/Adventure/Together/TogetherService.Crevasses.cs"),
        Setup = async c => await BeforeAStrip(c),
    };

    [PlaytestScenario("adv3-crevasse-rope", "Hidden crevasse: roped, the partner hauls", Cat)]
    private static PlaytestScenario CrevasseRope() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "You are roped to a stand-in Partner standing behind you, on a pretend glacier. Walk straight ahead over the hidden strip: the bridge breaks, the rope holds you 3 m under the lip and you hang. After five seconds the Partner hauls you out by itself (a friend would hold E for two seconds, pad: hold Y). " +
            "Watch it from the third-person camera: the swing, the rope, the haul. A real friend's view of you in the slot is in the TO-TEST list (needs two clients).",
        Checklist = Checks(
            "The rope holds with a zip sound; you hang about 3 m under the lip, not in the wall",
            "The swing and the haul speed (0.6 m/s) feel right; the rope sags and tightens believably",
            "You end on the snow, on your feet, with the rope still tied"),
        Covers = Adv3.Dir("src/Adventure/Together/Crevasses.cs", "src/Adventure/Together/CrevasseNode.cs", "src/Adventure/Together/TogetherService.Crevasses.cs",
            "src/Adventure/Together/RopeNode.cs", "src/Adventure/Together/TogetherService.Links.cs"),
        Setup = async c =>
        {
            var (me, e, n, angle) = await BeforeAStrip(c);
            double se = -Math.Sin(angle), sn = Math.Cos(angle);
            var origin = me.Origin!;
            var behind = origin.ToWorld(e - se * 14, n - sn * 14, me.Global.Alt);
            var partner = Adv3.Partner(c, me, Vector3.Zero);
            partner.DebugLaunch(behind with { Y = c.Ground(behind) + 0.5f }, Vector3.Zero);
            await c.Wait(0.8);
            TogetherService.Instance!.ServeLink(AdventureService.LocalPeer, Adv3.Pal, LinkMode.Rope);
        },
        // the stand-in hauls once you have hung for five seconds, and lets go when you are up
        Tick = HaulAfterFive(),
    };

    private static Action<PlaytestContext, double> HaulAfterFive()
    {
        double hung = 0;
        bool hauling = false;
        return (_, dt) =>
        {
            var svc = TogetherService.Instance;
            if (svc == null) return;
            bool hanging = svc.Hanging(AdventureService.LocalPeer);
            hung = hanging ? hung + dt : 0;
            if (hung > 5 && !hauling) { svc.ServeHaul(Adv3.Pal, true); hauling = true; }
            else if (!hanging && hauling) { svc.ServeHaul(Adv3.Pal, false); hauling = false; }
        };
    }

    // ---- Errands, world events, alcohol ---------------------------------------------------------------------

    [PlaytestScenario("adv3-errands", "Errands: the givers and their dialogue", Cat)]
    private static PlaytestScenario Errands() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "You stand 8 m from the fixture Post Office giver (eight givers stand within 350 m of the start: post office, farm, hut, station, landing, alp dairy, dairy, Beiz). " +
            "Walk up to the figure with the amber card glyph, E (pad Y) to talk, take the mail run, then open the Journal (J) Errands tab. Walk to the other givers to compare. Real huts, stations and landings are on the real map only.",
        Checklist = Checks(
            "A giver's figure reads from 20 m: height, the amber glyph, the outline at 3 m and the prompt \"Talk to ...\"",
            "The dialogue (bottom centre, three choices) is readable; pad: D-pad + A pick, B leaves, LB / RB switch cards; VR: laser + trigger, panel size and distance",
            "Taking a card: the sacks in the pack, the stamp toast and sound, the Journal's Quests tab and the tracker; handing a sack over pays CHF and reputation",
            "The Errands tab on a pad (LB / RB) and with `--mobile`: cards, Track, Share, the reputation"),
        Covers = Adv3.Dir("src/Adventure/Errands/ErrandDialog.cs", "src/Adventure/Errands/ErrandGivers.cs", "src/Adventure/Errands/ErrandsPage.cs",
            "src/Adventure/Errands/ErrandService.cs", "src/Adventure/Errands/Giver.cs", "src/Adventure/Errands/GiverHost.cs", "src/Adventure/Errands/ErrandSounds.cs"),
        Setup = async c =>
        {
            await Adv3.Begin(c, "11:00");
            await Adv3.Stand(c, FixturePoint(c, -62, -30), FixturePoint(c, -70, -30));
        },
    };

    /// <summary>An LV95 point by its metres east and north of the fixture course's start.</summary>
    private static (double E, double N) FixturePoint(PlaytestContext c, double x, double y)
    {
        var start = c.CourseStart ?? throw new InvalidOperationException("not on a fixture course");
        return (start.E + x, start.N + y);
    }

    [PlaytestScenario("adv3-world-events", "World events: cow, hiker, avalanche, fire", Cat)]
    private static PlaytestScenario WorldEventsScenario() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "Four events are staged round you on the flat fixture: a stray cow 45 m north, a lost hiker 60 m east, an avalanche cone 60 m west, a forest fire 60 m south (the forest is a checkerboard). " +
            "Visit each: stand on the cow's far side within 7 m so it turns home; talk to the hiker (E, pad Y) and lead it; hold G (pad X) at the avalanche signal to dig; hold G next to a burning cell for 2 s to beat it out. " +
            "A slope and real forest are only on the real map: the cone's fit into a slope is not judged here.",
        Checklist = Checks(
            "The cow reads as a stray (model, bell clank carries); it turns home at a walking pace",
            "The hiker's compass diamond and map letter; the figure follows you at a believable gait; the call carries",
            "The avalanche: the beep quickens as you near (\"Signal 25 m\"), the bar fills in about 8 s, the probe pole marks the place",
            "The fire: flames flicker, smoke dims the view up close but never hurts, it does not spread too fast (one cell in 15 s)",
            "The toast and pay (CHF 60 for the avalanche, CHF 40 for the fire) and the Journal's Errands tab"),
        Covers = Adv3.Dir("src/Adventure/Errands/WorldEvents.cs", "src/Adventure/Errands/WorldEvents.Draw.cs", "src/Adventure/Errands/WorldEventMeshes.cs",
            "src/Adventure/Errands/WorldEventSims.cs", "src/Adventure/Errands/WorldEventSounds.cs", "src/Adventure/Errands/WorldEventRules.cs"),
        Setup = async c =>
        {
            await Adv3.Begin(c, "11:00");
            var me = await c.PutPlayer(c.At(0), c.Heading(0));
            var events = WorldEvents.Instance ?? throw new InvalidOperationException("no world events service");
            events.ForestOverride = (e, n) => ((long)Math.Floor(e / 16) + (long)Math.Floor(n / 16)) % 2 == 0;
            Adv3.OnClear(c, () => events.ForestOverride = null);
            double e0 = me.Global.E, n0 = me.Global.N;
            events.Stage(WorldEventKind.RunawayCow, e0, n0 + 45, seed: 3, lifeS: 900);
            events.Stage(WorldEventKind.LostHiker, e0 + 60, n0, seed: 4, goalE: e0 + 60, goalN: n0 + 120, lifeS: 900);
            events.Stage(WorldEventKind.Avalanche, e0 - 60, n0, seed: 2, lifeS: 900);
            events.Stage(WorldEventKind.ForestFire, e0, n0 - 60, seed: 5, lifeS: 900);
        },
    };

    [PlaytestScenario("adv3-alcohol", "Alcohol: the effects and the two breathalyzers", Cat)]
    private static PlaytestScenario Alcohol() => new()
    {
        Flags = Adv3.Flags,
        Params = new() { ["beers"] = 6, ["minutesAgo"] = 30 },
        Instructions = "You have just had `beers` beers `minutesAgo` minutes ago (knobs; the default is about 1 per mille), and the electronic Breathalyzer and a single-use Breath test are in your pack. Walk about, steer a car if you like, look at the screen edges. " +
            "Hold Use with the Breathalyzer (pad RT) for 5 s; use the single-use test. Try other levels with `/alcohol test drink 4 40`, `... 10 30` (stumbles over 1.5), and Settings > Gameplay > Alcohol (View effects off, Mild, Off).",
        Checklist = Checks(
            "Around 0.8 and over: the screen edges blur, walking drifts to the side, the view feels drunk, not nauseating",
            "Over 1.5: the stumble comes now and then (not too often), a wall will not be grabbed (\"Not in this state\")",
            "The Breathalyzer: rising tone for 5 s, two beeps, its screen low in the view reads `0.62 per mille / 0.31 mg/L` and the verdict for 8 s; within 15 min of a drink it says WAIT",
            "The Breath test tube turns green (under 0.5) or amber, then it is used up; the colours read clearly",
            "The glass icon left of the stamina ring appears above 0.3 per mille; judge its place against the Together chip",
            "VR (headset): the head does not sway, a soft vignette comes instead; judge the comfort. Driving at 0.5 and over: the steering answers a beat late"),
        Covers = Adv3.Dir("src/Adventure/Alcohol", "src/Player/FootPlayer.Drunk.cs", "shaders/alcohol_edge.gdshader"),
        Setup = async c =>
        {
            await Adv3.Begin(c, "20:00");
            await c.PutPlayer(c.At(0), c.Heading(0));
            Adv3.Give(c, ItemId.Breathalyzer, 1);
            Adv3.Give(c, ItemId.BreathTestSingle, 2);
            c.Command($"/alcohol test drink {(int)c.Param("beers")} {(int)c.Param("minutesAgo")}");
        },
    };

    // ---- The Älplerfest and the test festival ---------------------------------------------------------------

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [PlaytestScenario("adv3-schwingen", "Schwingen at the Älplerfest", Cat)]
    private static PlaytestScenario Schwingen() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "The Älplerfest (18 August 2027) is held at the fixture's start; the sawdust ring is 20 m north of the middle and you stand 6 m south of it. Walk up: the prompt says \"Wrestle: Schwingen\"; E (pad Y) joins the queue and a bot joins after about 8 s (`/schwing bot` brings one at once). " +
            "In the bout lean with the stick, throw with LMB (pad X), hook a leg with RMB, answer the named throw with Shift (pad B) and the lean.",
        Checklist = Checks(
            "The leans, the braced stance, each of the six throws and the thrown body read from the side-on camera; the lying body sits in the sawdust",
            "The banner names the throw and how to answer; the 0.35 s window is fair on a pad",
            "On the ground, mashing the punch key pins or frees; the winner's brush and the loser's dusting; the ring bell and thump (sound player Schwing*)",
            "A won bout pays Batzen and party points (chat line, Journal's Festival tab); VR: the stick leans, B braces, the triggers throw"),
        Covers = Adv3.Dir("src/Combat/SchwingManager.cs", "src/Combat/SchwingRing.cs", "src/Combat/SchwingRules.cs", "src/Combat/SchwingHud.cs", "src/Combat/SchwingSounds.cs", "src/Combat/SchwingBot.cs"),
        Setup = async c =>
        {
            var site = await Adv3.Festival(c, FestivalCatalog.AelplerfestId, D(2027, 8, 18), "11:00");
            var ring = SchwingRing.Of(site);
            await Adv3.Stand(c, (ring.E, ring.N - 6), ring);
        },
    };

    [PlaytestScenario("adv3-fahnenschwingen", "Älplerfest flag swinging", Cat)]
    private static PlaytestScenario Fahnen() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "The Älplerfest (16 August 2027) is held at the fixture's start; the flag stage is 12 m east of its middle and you stand 5 m from it. Press the interact key at the stage (\"Getting in line\"), then draw the figures with the mouse (up, circle, eight, sideways sweep, down) or the right stick and throw with the use key. " +
            "Hold, then let go on the 1st, 2nd or 3rd beat for a high throw, and catch it on the beat.",
        Checklist = Checks(
            "A figure is recognised without big sweeps; the camera does not turn; the flag in your hand follows the figure",
            "The right stick's feel (30 Hz sampling, dead zone) and the use button for the throw on a pad; VR: Y at the stage, the right stick, the trigger",
            "The high throw: it flies as many beats as it was held plus one; catching on the beat is fair at 96 BPM",
            "The flag's cloth (its own shader) and the stage look right in each style; the HUD does not hide the figure",
            "The result panel and the Journal's Festival tab agree on points and Batzen (daily cap 100 Batzen, not points)"),
        Covers = Adv3.Dir("src/Occasions/Festivals/Fahnen", "src/Occasions/Festivals/FestivalService.Fahnen.cs"),
        Setup = async c =>
        {
            var site = await Adv3.Festival(c, FestivalCatalog.AelplerfestId, D(2027, 8, 16), "11:00");
            var ground = FestivalCatalog.FahneGround(site);
            await Adv3.Stand(c, (ground.E - 5, ground.N), ground);
        },
    };

    [PlaytestScenario("adv3-alphorn", "Älplerfest alphorn and yodel", Cat)]
    private static PlaytestScenario Alphorn() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "On the check-only festival's day 2 the three alphorns on trestles stand 4 m ahead of you (16 m east and 7 m north of the middle). The prompt says \"Play the alphorn and yodel\": E starts a four-beat count-in. " +
            "Play the melody with the mouse (wheel picks the note, hold the left button along a note), or R stick + RT/RB and A for the yodel. The echo off a mountain face and the multi-player harmony need the real map and friends.",
        Checklist = Checks(
            "The row of horns is easy to find; the note sounds like an alphorn",
            "PERFECT / GOOD / MISS on each press; the breath bar drains while blowing and refills in rests",
            "The bottom panel is readable at 1080p and with `--mobile` and clears the hotbar; pad and headset prompts name the right buttons",
            "Esc or walking away ends the song with a message"),
        Covers = Adv3.Dir("src/Occasions/Festivals/Horn", "src/Occasions/Festivals/FestivalService.Horn.cs"),
        Setup = async c =>
        {
            var site = await Adv3.Festival(c, FestivalCatalog.TestId, D(2027, 3, 11), "16:00");
            var spot = HornGround.SpotOf(site);
            await Adv3.Stand(c, (spot.E - 1, spot.N), (spot.E + HornGround.HornsAheadM, spot.N));
        },
    };

    [PlaytestScenario("adv3-steinstossen", "Steinstossen, the stone put", Cat)]
    private static PlaytestScenario Stein() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "On the check-only festival's last day (14 March 2027) the grey stone lies on its sawdust lane 3 m ahead (12 m south of the middle). The prompt says \"Put the stone (3 of 3 today)\": E (pad Y) takes it. " +
            "Hold LMB / RB, watch the meter rise and fall and let go on the peak, press on each of the four drum beats, look up about 40 degrees at the fourth (the push). Space while lifting changes the stone.",
        Checklist = Checks(
            "The stone reads as a stone and the lane as a lane; the meter and the peak band are easy to read; PERFECT / GOOD / MISS on a drum you can follow at 90 BPM (mouse and pad)",
            "The arc, the landing, the stake with your name and distance; a perfect put near 4 m for the Unspunnen stone, further for lighter stones",
            "Letting go at the bottom: \"The stone slipped\"; walking, jumping and the hotbar do nothing during an attempt",
            "Three puts, then the stone rests until tomorrow; the Journal's Festival tab shows Batzen and the best score. VR (headset): are the meter and lamps readable beside the body"),
        Covers = Adv3.Dir("src/Occasions/Festivals/SteinGame.cs", "src/Occasions/Festivals/SteinProps.cs", "src/Occasions/Festivals/SteinRules.cs", "src/Occasions/Festivals/SteinService.cs", "src/Occasions/Festivals/SteinSounds.cs"),
        Setup = async c =>
        {
            var site = await Adv3.Festival(c, FestivalCatalog.TestId, D(2027, 3, 14), "11:00");
            await Adv3.Stand(c, (site.E, site.N - 9), (site.E, site.N - 12));
        },
    };

    [PlaytestScenario("adv3-fireworks", "Festival fireworks and the closing show", Cat)]
    private static PlaytestScenario Fireworks() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "The last day of the check-only festival (14 March 2027) at 22:00: a show starts about 15 s after the scenario does, from the mortar rack at the middle; you stand 40 m south. Walk to the field, then use `/tp` or walk away to 150 m, 1 km and 3 km. " +
            "The finale (the last 20 s of a 6-12 minute show) is the performance test: F3 or `--perf`. Settings > Occasions > the test festival > Off hides it. Opening J > Festival checks the framework's page, readable at 1080p and with `--mobile`.",
        Checklist = Checks(
            "The shell climbs with a spark trail and bursts with a flash; the bang comes a moment later: believable delay, low and round, not a click",
            "At 3 km the bursts stay readable sparks and the bang is 9 s late; the ground light fades with distance",
            "Peony, chrysanthemum, willow (droop), palm, ring, crossette and the Swiss cross are told apart",
            "The finale of forty shells has no stutter (plan: under 0.3 ms)",
            "VR (headset): the depth reads right and no particle sheet or flash light clips close to the eyes; the Journal's Festival tab lists the day, the bell, the gong and the show"),
        Covers = Adv3.Dir("src/Occasions/Festivals/Fireworks.cs", "src/Occasions/Festivals/FireworkShow.cs", "src/Occasions/Festivals/FireworkSounds.cs", "src/Occasions/Festivals/FestivalPage.cs"),
        Setup = async c =>
        {
            var site = await Adv3.Festival(c, FestivalCatalog.TestId, D(2027, 3, 14), "22:00");
            await Adv3.Stand(c, (site.E, site.N - 40), (site.E, site.N));
        },
    };

    // ---- The calendar festivals -----------------------------------------------------------------------------

    [PlaytestScenario("adv3-fasnacht", "Fasnacht: Luzern's Larven, Räppli and Guggenmusik", Cat)]
    private static PlaytestScenario Fasnacht() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "Luzern's Fasnacht (4 February 2027), held at the fixture's start: the Larven stall is 9 m west of the middle (you stand 4 m from it), the Guggenmusik stage 14 m east and 6 m north. " +
            "E at the stall takes a mask and a bag of Räppli once a day; wear the mask from the pack; Use with the bag throws a handful. At the stage E starts the song: the count-in, then tap on every note (LMB, pad RT, VR trigger); Esc leaves.",
        Checklist = Checks(
            "The six Larven (Waggis, Alti Dante, Harlekin, Teufel, Fritschi, Ueli; also in the model viewer, Figures / Hat) read as faces at game distance; the Waggis's nose is funny, not scary",
            "The Räppli look and fall like confetti, the toss sound is good; ten handfuls pay 3 Batzen",
            "Playing 120-140 BPM on the drum (40 notes in 15 s) is possible on a pad and with the VR trigger; the snare, bass drum, trumpet and sousaphone sound like a Gugge (loud, a bit sour)",
            "The stall, the stage and the props by day and at night (`/time set 22:00`)"),
        Covers = Adv3.Dir("src/Occasions/Festivals/Fasnacht", "src/Occasions/Festivals/FestivalService.Fasnacht.cs"),
        Setup = async c =>
        {
            var site = await Adv3.Festival(c, FasnachtRules.FestivalId, D(2027, 2, 4), "11:00");
            var stall = FasnachtRules.StallAt(site);
            await Adv3.Stand(c, (stall.E + 4, stall.N), stall);
        },
    };

    [PlaytestScenario("adv3-fasnacht-basel", "Fasnacht: Basel's Morgestraich", Cat)]
    private static PlaytestScenario Basel() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "Basel's Fasnacht (15 February 2027), the clock at 03:55, held at the fixture's start. Stand by the big lantern 11 m north of the middle and wait for 04:00 (`/time set 03:59` skips ahead). " +
            "At 05:30 the lamps come back.",
        Checklist = Checks(
            "At 04:00 the street lamps and lit windows (houses, lamps, props) really go out while the lanterns shine: is it dark enough to be an event?",
            "The drum roll with the saying lands",
            "At 05:30 the lamps come back; the Fritschi / Ueli mask is worn from the stall when nothing is on the head"),
        Covers = Adv3.Dir("src/Occasions/Festivals/Fasnacht", "src/Occasions/Festivals/FestivalService.Fasnacht.cs"),
        Setup = async c =>
        {
            var site = await Adv3.Festival(c, FasnachtRules.BaselId, D(2027, 2, 15), "03:55");
            var lantern = FasnachtRules.LanternAt(site);
            await Adv3.Stand(c, (lantern.E, lantern.N - 5), lantern);
        },
    };

    [PlaytestScenario("adv3-sechselaeuten", "Sechseläuten: the Böögg burns", Cat)]
    private static PlaytestScenario Sechselaeuten() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "Zürich's Sechseläuten (19 April 2027) at 17:55, held at the fixture's start: the pyre with the Böögg stands at the middle, the Zunft Weibel 9 m east. E (pad Y) at the Weibel takes the purse once; at the pyre Use steps the minute and E places the bet. " +
            "At 18:00 the pyre is lit (`/time set 17:59` brings it on at once); wait for the bang. After the bang E at the pyre grills a Cervelat; eat it (LMB, pad X).",
        Checklist = Checks(
            "The ten metre pyre of crossed logs, the cotton-wool snowman, the Weibel and six guild banners read; the pyre does not float or sink",
            "The picker is readable, the coin sound lands, it is clear the panel changes after you bet",
            "The light: whoosh and bell, flames rising over about fifteen seconds with smoke, the glow on the square; flames look like fire in each style",
            "The bang: flash, sparks and boom, the head vanishes, the pyre falls to embers: satisfying, loud but not harsh; a median burn is 78 s, is the wait worth a right bet",
            "The Cervelat warms for 2 minutes. VR (headset): a grip at the pyre or the Weibel works through the pad route"),
        Covers = Adv3.Dir("src/Occasions/Festivals/Sechselaeuten", "src/Occasions/Festivals/FestivalService.Sechselaeuten.cs"),
        Setup = async c =>
        {
            var site = await Adv3.Festival(c, SechselaeutenRules.FestivalId, D(2027, 4, 19), "17:55");
            var pyre = SechselaeutenRules.PyreAt(site);
            await Adv3.Stand(c, (pyre.E + 4, pyre.N - 4), pyre);
        },
    };

    [PlaytestScenario("adv3-chalandamarz", "Chalandamarz: the Treichel loop", Cat)]
    private static PlaytestScenario Chalandamarz() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "The Chalandamarz (1 March 2027), held at the fixture's start: the Treichel stand is 45 m west of the middle and you stand 3 m from it; the loop of seven pennant stations rings it. E (pad Y) at the stand begins; then ring with E on the beat while walking round the loop (drum at 120 BPM, the closing ring on the HUD). " +
            "A second person in the party (online) rings in step for the bonus: that part is in the TO-TEST list.",
        Checklist = Checks(
            "The Treichel stand (wooden frame, five brass Treicheln, a blue board, a straw doll) and seven pennant banners on the ring of 45 m read well",
            "The drum and the closing ring on the HUD feel like the same beat as the Treichel you hear when you press; Perfect 70 ms, Good 140 ms is fair",
            "Walking at a child's pace (1.4 m/s) while pressing every step is comfortable; \"Keep walking!\" and \"Back onto the loop!\" are clear",
            "VR (headset): a grip at the stand begins and a grip on each beat rings"),
        Covers = Adv3.Dir("src/Occasions/Festivals/Chalanda", "src/Occasions/Festivals/FestivalService.Chalanda.cs"),
        Setup = async c =>
        {
            var site = await Adv3.Festival(c, ChalandaRules.FestivalId, D(2027, 3, 1), "10:00");
            var stand = ChalandaRules.StandAt(site);
            await Adv3.Stand(c, (stand.E + 3, stand.N), stand);
        },
    };

    [PlaytestScenario("adv3-august1", "1 August: lampions, Höhenfeuer and own fireworks", Cat)]
    private static PlaytestScenario August1() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "The eve of 1 August (31 July 2027) at 22:30, held at the fixture's start, with 40 Batzen in your purse: the lampion stand is 10 m west and 6 m north of the village fire (you stand at the fire), the fireworks stand 10 m east and 6 m north. " +
            "Take a lampion at its stand and hold it; buy a box (20 Batzen) and light it with the use key (LMB, pad X): the fuse hiss and a 40 s volley. The post with its lanterns is south of you: E (pad Y) starts the walk with friends. " +
            "The Höhenfeuer on the peaks and the lake fireworks over Genève, Lausanne, Montreux, Neuchâtel, Zürich, Luzern and Lugano need the real map: not judged here.",
        Checklist = Checks(
            "The lampion stand, the fireworks stand, the village fire, the post and the stop lanterns read, and the lanterns and flames glow at night",
            "The held lampion's red light is pleasant in each style and in VR",
            "Own fireworks: the fuse hiss and the volley are loud and big enough, others in sight would play it; the frame time is safe for a Quest (F3)",
            "The walk with friends: the HUD shows the stop, its distance and compass point (party needed: online)"),
        Covers = Adv3.Dir("src/Occasions/Festivals/August1", "src/Occasions/Festivals/FestivalService.August1.cs"),
        Setup = async c =>
        {
            var site = await Adv3.Festival(c, August1Rules.FestivalId, D(2027, 7, 31), "22:30");
            Adv3.Batzen(August1Rules.FestivalId, 40);
            var fire = August1Rules.VillageFireAt(site);
            await Adv3.Stand(c, (fire.E, fire.N - 3), August1Rules.LampionStandAt(site));
        },
    };

    [PlaytestScenario("adv3-zibelemaerit", "Zibelemärit: stalls, Zwiebelzopf and confetti eggs", Cat)]
    private static PlaytestScenario Zibele() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "The Zibelemärit (22 November 2027), held at the fixture's start, with 30 Batzen in your purse: four stalls in a row 4 m north of you. E (pad Y) at a stall tastes (free, pays 6 Batzen), E again buys. Buy six confetti eggs at the Konfetti stall and throw them (LMB, pad X); every fifth throw pays. " +
            "Eat the Zibelechueche and the Mehlsuppe (LMB, pad X): the soup warms for 3 minutes. `/time set 19:00` for the lanterns.",
        Checklist = Checks(
            "Four booths with red-and-black awnings, onion braids, tarts, an iron kettle with a glowing lid, crates of eggs under bunting; the lanterns glow warm at night",
            "The prompt is readable and the outline round the stall right; all four stalls in a day pay the Marktgang",
            "The burst of paper is like the Räppli; every fifth egg pays with the crack sound; 16 Batzen a day is fair",
            "VR (headset): a grip at a stall works through the pad route"),
        Covers = Adv3.Dir("src/Occasions/Festivals/Zibelemaerit", "src/Occasions/Festivals/FestivalService.Zibelemaerit.cs"),
        Setup = async c =>
        {
            var site = await Adv3.Festival(c, "zibelemaerit", D(2027, 11, 22), "15:00");
            Adv3.Batzen("zibelemaerit", 30);
            await Adv3.Stand(c, (site.E, site.N - 1), (site.E, site.N + 4));
        },
    };

    [PlaytestScenario("adv3-escalade", "Escalade: Marmite, soup pot and the Course", Cat)]
    private static PlaytestScenario Escalade() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "The Escalade (11 December 2027) at 17:30, held at the fixture's start: the Marmite stands at the middle (you are 4 m from it), the soup pot 9 m east, the start arch 12 m west, six torch posts on a ring of 55 m. " +
            "E (pad Y) at the Marmite; at the soup pot one E feeds the party, eat the soupe (LMB, pad X); E at the start arch begins the Course with the horn: run the gates, the next one is shown with its distance and compass point. " +
            "Smashing the Marmite takes two players in a party (online): that part is in the TO-TEST list.",
        Checklist = Checks(
            "The cast-iron Marmite with its chocolate lid, the copper soup pot over a fire, the start arch and the torch posts read; fire, torches and lanterns glow at dusk",
            "The saying on the Marmite is readable; the soupe warms for 3 minutes",
            "The Course: the panel counts time and shows the next gate; the torch posts are easy to find; a bell at each gate and the line; par (a good jog) is fair, the 6 m gate rings forgiving",
            "VR (headset): a grip at the Marmite, the pot or the start line works through the pad route"),
        Covers = Adv3.Dir("src/Occasions/Festivals/Escalade", "src/Occasions/Festivals/FestivalService.Escalade.cs"),
        Setup = async c =>
        {
            var site = await Adv3.Festival(c, EscaladeRules.FestivalId, D(2027, 12, 11), "17:30");
            var marmite = EscaladeRules.MarmiteAt(site);
            await Adv3.Stand(c, (marmite.E, marmite.N - 4), marmite);
        },
    };

    [PlaytestScenario("adv3-christmas-market", "Christmas market and the Samichlaus", Cat)]
    private static PlaytestScenario Christmas() => new()
    {
        Flags = Adv3.Flags,
        Params = new() { ["samichlausDay"] = 0 },
        Instructions = "The Christmas market (30 November 2027, or 6 December with the knob `samichlausDay` = 1, then Reset) at dusk, held at the fixture's start: two rows of four stalls, the tree at one end, the Samichlaus at the other. " +
            "E (pad Y) at a stall tastes (free, pays 8 Batzen), E again buys; drink the Glühwein mug (LMB, pad X). Taste at all eight stalls in a day for the Marktgang (80 Batzen). Settings > Gameplay > Alcohol off: no glass icon.",
        Checklist = Checks(
            "Lanterns, garlands and baubles glow warm at night; the stalls, the tree and the Samichlaus read well; every counter faces the lane on both rows",
            "The prompt is readable, the outline round the stall right; the ting on tasting and the clink on buying",
            "Drinking the mug: the mouth pose and the gulp; with alcohol real, a few mugs climb the glass icon (15.8 g each)",
            "6 December: the Samichlaus's prompt reads \"Take the Samichlaus's bag\" and the sleigh bells play; the day before it says he comes on 6 December",
            "The Journal's Festival tab shows the Batzen and the party's points. VR (headset): holding the mug to the mouth drinks it"),
        Covers = Adv3.Dir("src/Occasions/Festivals/Christmas", "src/Occasions/Festivals/FestivalService.Christmas.cs"),
        Setup = async c =>
        {
            var day = c.Param("samichlausDay") > 0 ? D(2027, 12, 6) : D(2027, 11, 30);
            var site = await Adv3.Festival(c, ChristmasRules.FestivalId, day, "16:30");
            await Adv3.Stand(c, (site.E, site.N - 6), (site.E, site.N));
        },
    };

    [PlaytestScenario("adv3-desalpe", "Désalpe: dressing the cows and the drive", Cat)]
    private static PlaytestScenario Desalpe() => new()
    {
        Flags = Adv3.Flags,
        Instructions = "The Désalpe (24 September 2027, day 4: the fair's vote is open too), held at the fixture's start with the 45 m test route. You stand at the herd on the alp, facing down the route. " +
            "Walk up to a cow: E (pad Y) turns the headdress (wreath, straw hat, fan crown, bare), LMB (pad X) the colour. At the alp sign E starts the Alpabzug; walk behind the herd. At the fair's ring E gives the vote to the herd shown, LMB picks the next. " +
            "The real 700 m route, Charmey's fair and the herd on level ground need the real map: not judged here.",
        Checklist = Checks(
            "The three headdresses read from 5 m and the six colours differ; the Leitkuh's big Treichel looks right on every cow's neck, dressed or bare",
            "From the side, front and at 60 m: the headdress on the head when it grazes and walks; the Treichel swings with the stride; no flicker on the headdresses or the collar",
            "The herd stays together, goes on when you are behind it and backs off when you come from the front; the 5 m flight zone on foot (9 m mounted) is right",
            "The bells ring from the walking cows, deeper on the Leitkuh; the herder's call at the start; the levels against the field herds' moos",
            "The result panel, the chat line and the Journal's Festival tab agree; VR (headset): Y at a cow, the sign and the ring, the trigger for colour and pick"),
        Covers = Adv3.Dir("src/Occasions/Festivals/Desalpe", "src/Occasions/Festivals/FestivalService.Desalpe.cs"),
        Setup = async c =>
        {
            var site = await Adv3.Festival(c, DesalpeRules.FestivalId, D(2027, 9, 24), "09:00");
            var route = DesalpeGround.RouteOf(site, DesalpeRules.TestRouteMeters);
            var (tx, ty) = route.Tangent(0);
            var start = DesalpeGround.Lv95(site, (route.Start.X, route.Start.Y));
            await Adv3.Stand(c, (start.E - tx * 4, start.N - ty * 4), (start.E, start.N));
        },
    };
}
