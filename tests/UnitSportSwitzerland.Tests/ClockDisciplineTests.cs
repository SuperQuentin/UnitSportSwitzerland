using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The three clocks stay three (#579). A raw wall-clock read in gameplay code is how environment
/// time, simulation speed and real time rot back into one: it looks harmless, it is invisible at 1x
/// speed, and it only shows up as "the sun did not slow down" long after the commit that did it.
///
/// <para>
/// So every <c>Time.GetTicksMsec</c>, <c>Time.GetTicksUsec</c>, <c>Time.GetUnixTimeFromSystem</c>,
/// <c>ClockSync.ServerNow</c> / <c>ServerUnixNow</c> (the server's real clock, #867),
/// <c>DateTime(Offset).Now/UtcNow</c> and <c>Environment.TickCount</c> under <c>src/</c> must be listed
/// below with a reason, and new
/// code has to justify itself here or use <c>Core.RealClock</c>, <c>Core.GameClock</c>,
/// <c>Net.ClockSync.SimNow</c> or <c>World.WorldClock</c>. Pure file-system logic, no Godot — see
/// docs/notes/core/three-clocks.md.
/// </para>
/// </summary>
public class ClockDisciplineTests
{
    private static readonly string[] WallClock =
    {
        "Time.GetTicksMsec", "Time.GetTicksUsec", "Time.GetUnixTimeFromSystem",
        // the server's real clock (#867): battle royale, the traffic lights and the lifts all ran on it
        // and ignored /speed. Simulated time is ClockSync.SimNow; SimClock.SimAt(ServerNow) is fine.
        "ClockSync.ServerNow", "ClockSync.ServerUnixNow",
        "DateTime.Now", "DateTime.UtcNow", "DateTimeOffset.Now", "DateTimeOffset.UtcNow", "Environment.TickCount",
    };

    /// <summary>Turning the server's clock into simulated time is what it is for, not a wall-clock read.</summary>
    private static readonly System.Text.RegularExpressions.Regex ToSimulated =
        new(@"SimAt\((Net\.)?ClockSync\.ServerNow\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex LineComment =
        new(@"//.*$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>
    /// A file's code: line comments out (a doc comment naming <c>ServerNow</c> is not a read), and the
    /// conversions to simulated time out.
    /// </summary>
    private static string Source(string path) => ToSimulated.Replace(LineComment.Replace(File.ReadAllText(path), ""), "SimAt()");

    /// <summary>
    /// Why each file is allowed to read the wall clock directly. Four kinds of reason appear:
    /// <b>clock</b> (it is the clock, or the fallback under it), <b>plumbing</b> (network liveness,
    /// rate limits, hardware, worker threads, file names — none of which bend with the world),
    /// <b>presentation</b> (an animation phase, a UI timeout, or music, which plays in real time: the
    /// player is not slowed even when their character is) and <b>calendar</b> (the real date on
    /// purpose: the season, the occasions, a restock a real day long).
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        // ---- the clocks themselves ----
        ["src/Core/RealClock.cs"] = "clock: it is the wall clock",
        ["src/Core/GameClock.cs"] = "clock: Pace holds the frames to real time while the world loads",
        ["src/Net/ClockSync.cs"] = "clock: LocalNow's fallback off --fixed-fps, and the server's Unix offset",
        ["src/Core/DebugMenu.cs"] = "clock: a speed change is scheduled at a server instant (SimClock.Schedule)",
        ["src/Phone/PhoneController.cs"] = "calendar: the phone's lock screen shows today's date, as a phone does",
        ["src/Ui/Skins/MenuSkin.cs"] = "calendar: Auto picks the day or the night menu skin by the player's own clock on the title screen (#1135)",
        ["src/World/WorldClock.cs"] = "clock: Hour reads the sky at a server instant, through SimClock",
        ["src/Net/ChatManager.cs"] = "clock: /speed is scheduled at a server instant; plumbing: the rate limit on asking the server for player names",

        // ---- plumbing: real whatever the world is doing ----
        ["src/Net/Handshake.cs"] = "plumbing: the protocol handshake times out in real time",
        ["src/Net/Accounts/AccountGate.cs"] = "plumbing: the account step times out in real time, as the handshake before it (#1123)",
        ["src/Net/AdminApi.cs"] = "plumbing: the dashboard's admin token is trusted for a minute of real time (#1211); no Godot, so no RealClock",
        ["src/Net/Accounts/Account.cs"] = "plumbing: a Nakama session token runs out at a Unix time (#1123)",
        ["src/Net/ServerStats.cs"] = "plumbing: the stats sampling window",
        ["src/Net/Swarm.cs"] = "plumbing: a load tool that measures frame times, so it wants the engine's own delta",
        ["src/Net/StatusFile.cs"] = "plumbing: Unix stamps a web page compares with its own clock",
        ["src/Net/LiveMapFile.cs"] = "plumbing: Unix stamps the live map page compares with its own clock",
        ["src/Core/SteeringWheel.Force.cs"] = "plumbing: force feedback goes to hardware on the wall clock",
        ["src/Items/RadioSpeaker.cs"] = "plumbing: the backoff before retrying a failed network stream, and where the CD should be playing now",
        ["src/Occasions/OccasionHunt.cs"] = "plumbing: a probe's wall-clock timeout on a tile load (#522)",
        ["src/Interiors/InteriorManager.cs"] = "plumbing: the re-ask interval on a door request that got no answer",
        ["src/Interiors/FloorPicker.cs"] = "plumbing: the guard against the click that opened the picker also choosing in it",
        ["src/Build/BuildTool.cs"] = "plumbing: the repeat rate of a held place key, which is input, not simulation",
        ["src/BattleRoyale/BrManager.cs"] = "plumbing: the flare and ping rate limits on what a client may ask for (#855)",
        ["src/Net/Invites/InviteService.cs"] = "plumbing: an invite expires in real time, whatever the world's speed",
        ["src/Net/ServerQuery.cs"] = "plumbing: server-list ping times",
        ["src/Net/QueryResponder.cs"] = "plumbing: the per-second rate limit on query answers",
        ["src/Net/LanDiscovery.cs"] = "plumbing: the LAN broadcast cadence and expiry",
        ["src/Net/ChunkStreamer.cs"] = "plumbing: chunk request timeouts",
        ["src/Net/ServerBook.cs"] = "plumbing: when a saved server was last played",
        ["src/Adventure/AdventureService.cs"] = "calendar: a quest's start and done dates, and a player's first and last visit, are real dates saved with its progress (#1223)",
        ["src/Adventure/Rewards/RewardService.cs"] = "presentation: seals' windows, a guide's legs and a trial's time are judged on the shared clock the player's client shows them on (#1227); calendar: a Luxurious chest's edition is the real year",
        ["src/Adventure/Rewards/GuideNode.cs"] = "presentation: a guide's leg is played from its start on the shared clock the server stamped it with (#1227)",
        ["src/Adventure/Rewards/TrialRunner.cs"] = "presentation: a trial's ghost and its 4 Hz samples run on the shared clock the server times the trial on (#1227)",
        ["src/Adventure/Rewards/RewardHud.cs"] = "presentation: a trial's clock on the HUD counts from the server's start stamp (#1227); calendar: the detector skips the Luxurious chests of this real year's edition already opened",
        ["src/Adventure/Rewards/RewardWorld.cs"] = "calendar: which Luxurious edition this player sees is the real year's (#1227)",
        ["src/Adventure/Places/PlacesProgress.cs"] = "calendar: a stamp's day is the real date the place was found, saved with the Atlas (#1224)",
        ["src/Vehicles/VehicleState.cs"] = "plumbing: SpawnedAt is a Unix stamp that outlives the server on disk (#452)",
        ["src/Bots/BotDirector.cs"] = "plumbing: a random seed",
        ["src/Playtest/PlaytestDirector.cs"] = "plumbing: the live listener's poll liveness, and a ledger entry's date",
        ["src/Playtest/Recording/PlaytestRecorder.cs"] = "plumbing: a session folder's date stamp",
        ["src/Core/PerfRecorder.cs"] = "plumbing: a perf log's file name and header date",
        ["src/Gpx/GpxSession.cs"] = "plumbing: a recorded track's file name",
        ["src/Items/PhotoStore.cs"] = "plumbing: when a photo was taken, as metadata",
        ["src/Movie/MovieStudio.cs"] = "plumbing: an export's file name",
        ["src/Movie/ReplayRecorder.cs"] = "plumbing: a recording's name",
        ["src/Movie/SessionSpool.cs"] = "plumbing: a sound file's name",
        ["src/Movie/SoundImport.cs"] = "plumbing: an imported sound file's name",

        // ---- presentation: the player is not slowed even when their character is ----
        // music plays in real time, so a CD's position, its beat and everything dancing to it do too
        ["src/Items/RadioManager.cs"] = "presentation: a radio's CD position is server real time, as its audio plays",
        // an aura battle (#907) is a rhythm game two peers play on one beat: a player's press keeps the player's time
        ["src/Combat/AuraManager.cs"] = "presentation: an aura battle's beat, its downbeats and every press graded on them, shared by both battlers",
        ["src/Combat/AuraHud.cs"] = "presentation: the aura battle's beat ring and countdown, on the battle's shared beat",
        ["src/Player/FootPlayer.Aura.cs"] = "presentation: which move of the combo the shared beat is on, and the Griddy's beat",
        ["src/Combat/AuraManager.Bots.cs"] = "presentation: a bot battler's laugh on the aura battle's shared beat (#914)",
        ["src/Combat/AuraManager.Music.cs"] = "presentation: a radio's CD position, which an aura battle takes its beat from (#914)",
        ["src/Combat/AuraWatchHud.cs"] = "presentation: the move a watched aura battle is on, on its shared beat (#914)",
        ["src/Items/RadioBody.cs"] = "presentation: a placed radio's CD position and groove",
        ["src/Concert/ConcertService.Songs.cs"] = "presentation: /concert song reads where the setlist is on the shared clock the music plays to",
        ["src/Concert/ConcertService.cs"] = "presentation: an operator's cue is stamped with the shared clock, so every peer's light show keeps the same beat",
        ["src/Concert/PogoQte.cs"] = "presentation: the pogo QTE judges a press against the beat of the music as heard, which plays on the shared clock in real time",
        ["src/Concert/ConcertBeatDebug.cs"] = "presentation: the beat debug window scrolls its own history in real time",
        ["src/Concert/Mvr/Mdns.cs"] = "plumbing: mDNS answers at most once a second, real time on its receive thread",
        ["src/Concert/Mvr/MvrXchangeStation.cs"] = "plumbing: an MVR-xchange station retries a silent peer after 30 s of real time, on a socket task",
        ["src/Concert/ConcertRig.cs"] ="presentation: a concert's light show runs in real time with the music it lights, the same frame on every peer",
        ["src/Items/RadioTap.cs"] = "presentation: the tap on a playing CD's samples",
        ["src/Items/BeatField.cs"] = "presentation: the beat of the music nearby",
        ["src/Items/HeldItemVisual.cs"] = "presentation: a held radio's sparkles on the beat",
        ["src/Interiors/ChurchRadios.cs"] = "presentation: a church's CD position, as its audio plays",
        ["src/Interiors/ChurchStage.cs"] = "presentation: the stage lights on the church's music",
        ["src/Player/FootPlayer.Face.cs"] = "presentation: the face on the beat",
        ["src/Player/FootPlayer.Back.cs"] = "presentation: the backpack radio's sparkles on the beat",
        ["src/Audio/Live/WebRadioSpeaker.cs"] = "presentation: where a live stream should be playing now",
        ["src/Audio/Live/StationTap.cs"] = "presentation: the tap on a live stream's samples",
        ["src/Audio/Tracker/TrackerSpeaker.cs"] = "presentation: where a venue's tracker playlist should be playing now, on the shared clock (#1244)",
        ["src/Audio/Ambience.cs"] = "presentation: church bells strike on the machine's local hour (docs/notes/audio/audio.md)",
        ["src/Audio/Cd/CdLibrary.cs"] = "presentation: the library's burn progress readout",
        ["src/Audio/Surfaces.cs"] = "presentation: footstep surface sampling",
        ["src/Avatar/HumanMeshBuilder.Clothing.cs"] = "presentation: cloth sway phase",
        ["src/BattleRoyale/BrCrates.cs"] = "presentation: the crate light's pulse",
        ["src/BattleRoyale/BrHud.cs"] = "presentation: the ping bob and the zone edge's pulse",
        ["src/BattleRoyale/BrManager.Client.cs"] = "presentation: how long a kill-feed line stays up",
        ["src/BattleRoyale/BrManager.Recall.cs"] = "presentation: a recall's feed line",
        ["src/BattleRoyale/BrManager.Sounds.cs"] = "presentation: the cadence of the zone warning sounds",
        ["src/BattleRoyale/BrPlane.cs"] = "presentation: the plane's idle bank wobble",
        ["src/Items/CatalogueUi.cs"] = "presentation: panel timeouts and the arm-to-clear guard",
        ["src/Items/DroppedItems.cs"] = "presentation: the float animation on a dropped item",
        ["src/Items/Fishing/FishingRod.cs"] = "presentation: the hooked fish's wander, drawn not simulated",
        ["src/Items/Fishing/FishingVisuals.cs"] = "presentation: float and line animation",
        ["src/Items/InventoryUi.cs"] = "presentation: panel timeouts and the drag halo's pulse",
        ["src/Items/ItemController.cs"] = "presentation: the held item's idle breath sway",
        ["src/Trailer/Actor.cs"] = "presentation: a film actor's held radio starts its CD on the server's real clock, as a player's does (RadioUi)",
        ["src/Items/RadioUi.cs"] = "presentation: how long a status line stays on the radio's display, and a CD's start on the server's real clock",
        ["src/Items/SmartBinocularsHud.cs"] = "presentation: the HUD's sweep",
        ["src/Items/ThrowAim.cs"] = "presentation: the charge hum's pitch and the full-power flash",
        ["src/Loot/VendingUi.cs"] = "presentation: how long a message stays on the machine's LCD",
        ["src/Player/FootPlayer.cs"] = "presentation and plumbing: net state timestamps and interpolation, the emote blend, the dance crowd poll and the dance on the music's beat, camera shake",
        ["src/Player/FootPlayer.Fight.cs"] = "presentation: fight animation phase",
        ["src/Player/PlayerFeel.cs"] = "presentation: the chime bank stepping up when chimes come close together",
        ["src/World/Traffic.cs"] = "presentation: a car's age in the debug readout",

        // ---- calendar: the real date, on purpose ----
        ["src/Core/Calendar.cs"] = "calendar: the one reader of today's date for the occasions, the farm, the birds, fishing, shops and tonight's concert; --date or a film's date pins it (#924)",
        ["src/Occasions/OccasionAmbience.cs"] = "calendar: an occasion's ambience follows the real date",
        ["src/Loot/LootService.cs"] = "calendar: a building restocks once a real day (LootTables.RestockSeconds)",
        ["src/Loot/ShopService.cs"] = "calendar: a shop restocks once a real day",
    };

    /// <summary>The repo root, found by walking up to the directory holding project.godot.</summary>
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "project.godot"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// A probe or check is a test harness, not gameplay, and the quick tier's rule is the opposite
    /// one: wait for threaded work on the wall clock, because threads do not speed up under
    /// <c>--fixed-fps</c> (docs/notes/general/fast-checks.md). They are exempt by name.
    /// </summary>
    private static bool IsHarness(string relative) =>
        relative.EndsWith("Probe.cs", StringComparison.Ordinal) || relative.EndsWith("Check.cs", StringComparison.Ordinal);

    [Fact]
    public void Every_wall_clock_read_in_gameplay_code_has_a_stated_reason()
    {
        string root = Root();
        string src = Path.Combine(root, "src");
        Assert.True(Directory.Exists(src), $"no src/ under {root}");

        var offenders = new List<string>();
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsHarness(relative) || Allowed.ContainsKey(relative)) continue;
            string text = Source(file);
            foreach (string call in WallClock)
                if (text.Contains(call, StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}: {call}");
                    break;
                }
        }

        Assert.True(offenders.Count == 0,
            "These files read the wall clock without saying why.\n"
            + "Pick the clock the thing belongs to — World.WorldClock (environment), Core.SimClock or\n"
            + "Core.GameClock (simulation), Core.RealClock (real) — or add the file to\n"
            + "ClockDisciplineTests.Allowed with a reason. See docs/notes/core/three-clocks.md.\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The allow-list is a list of exceptions, so a stale entry is as bad as a missing one: it
    /// would quietly permit a future wall-clock read in a file that had stopped needing one.
    /// </summary>
    [Fact]
    public void The_allow_list_has_no_stale_entries()
    {
        string root = Root();
        var stale = new List<string>();

        foreach (var (relative, reason) in Allowed)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                stale.Add($"{relative}: no such file (renamed or deleted?)");
                continue;
            }
            string text = Source(path);
            if (!WallClock.Any(c => text.Contains(c, StringComparison.Ordinal)))
                stale.Add($"{relative}: no longer reads the wall clock — drop the entry (\"{reason}\")");
        }

        Assert.True(stale.Count == 0, "Stale ClockDisciplineTests.Allowed entries:\n  " + string.Join("\n  ", stale));
    }

    /// <summary>Every reason says which of the three kinds it is, so the list stays readable.</summary>
    [Fact]
    public void Every_reason_names_its_kind()
    {
        var bad = Allowed.Where(e => !e.Value.StartsWith("clock:", StringComparison.Ordinal)
                                  && !e.Value.StartsWith("plumbing:", StringComparison.Ordinal)
                                  && !e.Value.StartsWith("presentation", StringComparison.Ordinal)
                                  && !e.Value.StartsWith("calendar:", StringComparison.Ordinal))
                         .Select(e => $"{e.Key}: \"{e.Value}\"").ToList();
        Assert.True(bad.Count == 0,
            "A reason must start with \"clock:\", \"plumbing:\", \"presentation\" or \"calendar:\":\n  " + string.Join("\n  ", bad));
    }
}
