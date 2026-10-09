using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The three clocks stay three (#579). A raw wall-clock read in gameplay code is how environment
/// time, simulation speed and real time rot back into one: it looks harmless, it is invisible at 1x
/// speed, and it only shows up as "the sun did not slow down" long after the commit that did it.
///
/// <para>
/// So every <c>Time.GetTicksMsec</c>, <c>Time.GetTicksUsec</c> and
/// <c>Time.GetUnixTimeFromSystem</c> under <c>src/</c> must be listed below with a reason, and new
/// code has to justify itself here or use <c>Core.RealClock</c>, <c>Core.GameClock</c>,
/// <c>Core.SimClock</c> or <c>World.WorldClock</c>. Pure file-system logic, no Godot — see
/// docs/notes/core/three-clocks.md.
/// </para>
/// </summary>
public class ClockDisciplineTests
{
    private static readonly string[] WallClock =
        { "Time.GetTicksMsec", "Time.GetTicksUsec", "Time.GetUnixTimeFromSystem" };

    /// <summary>
    /// Why each file is allowed to read the wall clock directly. Three kinds of reason appear:
    /// <b>clock</b> (it is the clock, or the fallback under it), <b>plumbing</b> (network liveness,
    /// rate limits, hardware, worker threads — none of which bend with the world) and
    /// <b>presentation</b> (an animation phase or a UI timeout, where the player is not slowed even
    /// when their character is).
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        // ---- the clocks themselves ----
        ["src/Core/RealClock.cs"] = "clock: it is the wall clock",
        ["src/Core/GameClock.cs"] = "clock: Pace holds the frames to real time while the world loads",
        ["src/Net/ClockSync.cs"] = "clock: LocalNow's fallback off --fixed-fps, and the server's Unix offset",

        // ---- plumbing: real whatever the world is doing ----
        ["src/Net/Handshake.cs"] = "plumbing: the protocol handshake times out in real time",
        ["src/Net/ServerStats.cs"] = "plumbing: the stats sampling window",
        ["src/Net/Swarm.cs"] = "plumbing: a load tool that measures frame times, so it wants the engine's own delta",
        ["src/Net/ChatManager.cs"] = "plumbing: the rate limit on asking the server for player names",
        ["src/Net/StatusFile.cs"] = "plumbing: Unix stamps a web page compares with its own clock",
        ["src/Core/SteeringWheel.Force.cs"] = "plumbing: force feedback goes to hardware on the wall clock",
        ["src/Items/RadioSpeaker.cs"] = "plumbing: the backoff before retrying a failed network stream",
        ["src/Occasions/OccasionHunt.cs"] = "plumbing: a probe's wall-clock timeout on a tile load (#522)",
        ["src/Interiors/InteriorManager.cs"] = "plumbing: the re-ask interval on a door request that got no answer",
        ["src/Interiors/FloorPicker.cs"] = "plumbing: the guard against the click that opened the picker also choosing in it",
        ["src/Build/BuildTool.cs"] = "plumbing: the repeat rate of a held place key, which is input, not simulation",

        // ---- presentation: the player is not slowed even when their character is ----
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
        ["src/Items/RadioUi.cs"] = "presentation: how long a status line stays on the radio's display",
        ["src/Items/SmartBinocularsHud.cs"] = "presentation: the HUD's sweep",
        ["src/Items/ThrowAim.cs"] = "presentation: the charge hum's pitch and the full-power flash",
        ["src/Loot/VendingUi.cs"] = "presentation: how long a message stays on the machine's LCD",
        ["src/Player/FootPlayer.cs"] = "presentation and plumbing: net state timestamps and interpolation, the emote blend, the dance crowd poll, camera shake",
        ["src/Player/FootPlayer.Fight.cs"] = "presentation: fight animation phase",
        ["src/Player/PlayerFeel.cs"] = "presentation: the chime bank stepping up when chimes come close together",
        ["src/World/Traffic.cs"] = "presentation: a car's age in the debug readout",
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
            string text = File.ReadAllText(file);
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
            string text = File.ReadAllText(path);
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
                                  && !e.Value.StartsWith("presentation", StringComparison.Ordinal))
                         .Select(e => $"{e.Key}: \"{e.Value}\"").ToList();
        Assert.True(bad.Count == 0,
            "A reason must start with \"clock:\", \"plumbing:\" or \"presentation\":\n  " + string.Join("\n  ", bad));
    }
}
