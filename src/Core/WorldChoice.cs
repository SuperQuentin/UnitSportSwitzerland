using Godot;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.TestRegion;

namespace UnitSport.Core;

/// <summary>
/// Which world a session plays (#999, <c>docs/notes/core/test-worlds.md</c>): the UnitSport open
/// world, or one of the test worlds the checks run on: the traffic lights region (#386) and the
/// fixture courses (<c>Terrain/Fixture</c>). Picked on Play solo and in the Host dialog, or with
/// <c>--test-world &lt;id&gt;</c> on a client or server command line.
///
/// <para>
/// While a test world runs (<see cref="Active"/>), the chunk directory, the fixture course and the
/// generated fill answer for it (<see cref="TerrainPaths"/>, <see cref="Systems"/>), and what the
/// world saves goes to its own folder (<see cref="SavePath"/>), so nothing placed on a course built
/// at Riddes' coordinates shows up in Riddes. The open world is <c>null</c>: every reader then
/// behaves as before, and so do the command-line runs (<c>--chunks fixture:x</c>) the checks use.
/// </para>
/// </summary>
public sealed record WorldChoice(string Id, string Title, string Blurb, string? Course = null)
{
    /// <summary>The id of the open world, on the wire and in the settings.</summary>
    public const string OpenId = "open";

    public const string SignalsId = "signals";

    /// <summary>The fun world (#1396): a small synthwave world with the game's activities side by side.</summary>
    public const string FunId = "fun";

    /// <summary>The index in <see cref="All"/> of the first test world: the pickers head the rest "Test worlds".</summary>
    public const int FirstTestWorld = 2;

    public static readonly WorldChoice Open = new(OpenId, "UnitSport open world",
        "The real Switzerland where its terrain is on this machine, generated everywhere else.");

    /// <summary>Every world the pickers offer, the open world and the fun world first, then the test worlds.</summary>
    public static readonly WorldChoice[] All =
    {
        Open,
        new(FunId, "Neon fun world",
            "A small synthwave world at dusk: a race track, bowling, an arcade, a cinema, a café, an arena, a runway with air rings, a stunt park, a ski slope, a lake, a festival, a truck yard and a PvP block.", FunId),
        new(SignalsId, "Traffic lights region",
            "Designed junctions with traffic lights on flat ground, built by the real road pipeline. The place search finds them (J1, J2...)."),
        new("junction", "Junction course", "A 9 m road through a T junction and a crossroads with 6 m side roads.", "junction"),
        new("straight", "Straight", "3 km of straight 6 m road on the flat: top speed, overtaking.", "straight"),
        new("hairpin", "Hairpins", "A fast downhill with six tight hairpins, 7 % down.", "hairpin"),
        new("narrow", "Narrow road", "A winding 4 m road with a tree trunk every 5 m on both edges: no verge.", "narrow"),
        new("verge", "Verges", "Two bends with 6 m of grass verge, then a tree line on each side.", "verge"),
        new("airport", "Airport", "A 2.7 km runway, a taxiway, a terminal apron with six stands and a cargo apron.", "airport"),
        new("lake", "Lake", "A 2.6 x 2 km lake: a beach, a shelf, a drop-off to 25 m, a river and a slipway.", "lake"),
        new("parking", "Car park", "A big-box store's car park off a main road, turned off the grid.", "parking"),
        new("garage", "Garage blocks", "Blocks of flats along a street, each with an underground garage ramp.", "garage"),
        new("site", "Building sites", "Three building sites with pits along a street: foundations, a shell, a turned house plot.", "site"),
        new("crag", "Crag", "A rock wall with a ledge, a scree slope, a glacier wall, a block, a sunny slope and a ridge: climbing and lift.", "crag"),
        new("flat", "Flat ground", "No roads: open grass, three fields, a paved strip and a ridge.", "flat"),
    };

    public bool IsOpen => Id == OpenId;

    /// <summary>Whether the fun world (#1396) is being played.</summary>
    public static bool FunActive => Active?.Id == FunId;

    /// <summary>The world for an id, or null when no world has it (an older or newer peer's).</summary>
    public static WorldChoice? Find(string? id) => id == null ? null : Array.Find(All, w => w.Id == id);

    /// <summary>
    /// The test world this process plays, or null for the open world. Set by the world as it
    /// boots (<see cref="ClientWorld"/> from its launch, <see cref="ServerWorld"/> from
    /// <c>--test-world</c>), so it always matches the world in the tree.
    /// </summary>
    public static WorldChoice? Active { get; set; }

    /// <summary>The id the handshake and the status reply carry: the active world's, else the open world's.</summary>
    public static string ActiveId => Active?.Id ?? OpenId;

    /// <summary><c>--test-world &lt;id&gt;</c>, or null (none, the open world, or an unknown id: logged).</summary>
    public static WorldChoice? FromArgs()
    {
        if (CmdArgs.Value("--test-world") is not { } id) return null;
        if (Find(id) is not { } w)
        {
            GD.PushWarning($"[world] no test world '{id}' (known: {string.Join(", ", All.Select(x => x.Id))}): the open world");
            return null;
        }
        return w.IsOpen ? null : w;
    }

    /// <summary>The value <c>--chunks</c> would have for this world: <c>fixture:x</c>, the region's folder, or null (open).</summary>
    public string? ChunksArg => Course != null ? Systems.FixturePrefix + Course : Id == SignalsId ? SignalsDir : null;

    /// <summary>
    /// Where a session in this world starts, LV95 metres: a fixture course is built around the
    /// default spawn, the traffic lights region starts at J1's south-west corner.
    /// </summary>
    public (double E, double N) Start => Id == SignalsId
        ? (SignalTestRegion.OriginE - 2525, SignalTestRegion.RowN - 25)
        : (SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N);

    // ---- saves ----------------------------------------------------------------------------------

    /// <summary>
    /// A world-located save path (<c>user://placed/offline.json</c>...) for the world being played:
    /// the same path in the open world, <c>user://worlds/&lt;id&gt;/...</c> in a test world.
    /// Absolute paths are mapped the same way, under the globalized <c>user://</c>.
    /// </summary>
    public static string SavePath(string path)
    {
        if (Active is not { } w) return path;
        const string user = "user://";
        if (path.StartsWith(user, StringComparison.Ordinal)) return $"{user}worlds/{w.Id}/{path[user.Length..]}";
        string root = ProjectSettings.GlobalizePath(user).TrimEnd('/', '\\');
        if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return System.IO.Path.Combine(root, "worlds", w.Id, path[root.Length..].TrimStart('/', '\\'));
        return path;
    }

    /// <summary>
    /// A store's save path for the frame being played (#1726, G2 D2): <see cref="EarthStores.Earth"/> under
    /// <c>--frame earth</c>, the flat path under <c>--frame flat</c>, then mapped as <see cref="SavePath"/> maps it.
    /// </summary>
    public static string StorePath(string path) => SavePath(EarthStores.For(path, GameSettings.Current.Frame == WorldFrame.Earth));

    // ---- the traffic lights region ---------------------------------------------------------------

    /// <summary>The region's tiles, built by the game itself (<see cref="Prepare"/>).</summary>
    public static string SignalsDir => ProjectSettings.GlobalizePath($"user://worlds/{SignalsId}/chunks");

    /// <summary>
    /// The region's cube leaves (#1647), built beside its tiles under <c>--frame earth</c>: the chunk folder's <c>_cube</c>
    /// sibling, where <see cref="TerrainPaths.FindLeafDir"/> looks.
    /// </summary>
    public static string SignalsLeafDir => UnitSport.Tools.Preprocessor.CubeTestRegion.RegionOf(SignalsDir);

    private static string SignalsStamp => System.IO.Path.Combine(SignalsDir, "built_by.txt");

    private static string SignalsLeafStamp => System.IO.Path.Combine(SignalsLeafDir, "built_by.txt");

    private static string RoadGenStamp => typeof(SignalTestRegion).Assembly.ManifestModule.ModuleVersionId.ToString();

    /// <summary>The leaves' stamp: RoadGen's and the cube stages' module ids (the preprocessor writes them).</summary>
    private static string LeafStamp => RoadGenStamp + " " + typeof(UnitSport.Tools.Preprocessor.CubeTestRegion).Assembly.ManifestModule.ModuleVersionId;

    /// <summary>Whether this session stands on leaves (<c>--frame earth</c>): the region is then built as leaves too.</summary>
    private static bool Earth => GameSettings.Current.Frame == WorldFrame.Earth;

    private static bool Stamped(string stamp, string expected, string needs)
    {
        try
        {
            return System.IO.File.Exists(stamp) && System.IO.File.ReadAllText(stamp).Trim() == expected && System.IO.File.Exists(needs);
        }
        catch (System.IO.IOException) { return false; }
    }

    /// <summary>
    /// Nothing to build before this world loads: a fixture course, or the region already built by
    /// this RoadGen (and, under <c>--frame earth</c>, its leaves by these cube stages). When false, <see cref="Prepare"/>
    /// takes a while (~15 s in a release build, close to a minute in the editor's debug build; the leaves ~40 s more):
    /// the loading screen says so.
    /// </summary>
    public bool Prepared => Id != SignalsId
        || Stamped(SignalsStamp, RoadGenStamp, System.IO.Path.Combine(SignalsDir, "manifest.json"))
            && (!Earth || Stamped(SignalsLeafStamp, LeafStamp, System.IO.Path.Combine(SignalsLeafDir, CubeManifest.FileName)));

    /// <summary>What the loading screen says while <see cref="Prepare"/> builds.</summary>
    public static string PreparingDetail => Earth
        ? "Building the traffic lights region and its leaves: up to two minutes, the first time"
        : "Building the traffic lights region: up to a minute, the first time";

    /// <summary>
    /// Makes sure this world's tiles exist; run on a worker. A fixture course is built in code as
    /// it streams, so only the traffic lights region has anything to do: the game runs RoadGen's
    /// <c>--test-region</c> into <see cref="SignalsDir"/> (~2 s), again whenever RoadGen changed
    /// (its module id is the stamp), and under <c>--frame earth</c> the cube stages' copy into
    /// <see cref="SignalsLeafDir"/> (#1647, again whenever either changed; from the region's design, not the LV95 copy, #1711).
    /// Keeps the LV95 copy until G2's D.
    /// Returns an error for the player, or null.
    /// </summary>
    public string? Prepare()
    {
        if (Prepared) return null;
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (!Stamped(SignalsStamp, RoadGenStamp, System.IO.Path.Combine(SignalsDir, "manifest.json")))
            {
                System.IO.Directory.CreateDirectory(SignalsDir);
                // its verdict on the junctions is a check's; the tiles are written either way
                SignalTestRegion.Run(SignalsDir, SignalsDir + "_temp", _ => { });
                if (!System.IO.File.Exists(System.IO.Path.Combine(SignalsDir, "manifest.json")))
                    return "The traffic lights region could not be built. See the log.";
                System.IO.File.WriteAllText(SignalsStamp, RoadGenStamp);
                GD.Print($"[world] built the traffic lights region in {clock.Elapsed.TotalSeconds:F1} s: {SignalsDir}");
            }
            if (Earth && !Stamped(SignalsLeafStamp, LeafStamp, System.IO.Path.Combine(SignalsLeafDir, CubeManifest.FileName)))
            {
                var leaves = System.Diagnostics.Stopwatch.StartNew();
                int code = UnitSport.Tools.Preprocessor.CubeTestRegion.Build(SignalsLeafDir, line => GD.Print("[world] " + line));
                if (code != 0 || !System.IO.File.Exists(System.IO.Path.Combine(SignalsLeafDir, CubeManifest.FileName)))
                    return "The traffic lights region's leaves could not be built. See the log.";
                System.IO.File.WriteAllText(SignalsLeafStamp, LeafStamp);
                GD.Print($"[world] built the traffic lights region's leaves in {leaves.Elapsed.TotalSeconds:F1} s: {SignalsLeafDir}");
            }
            return null;
        }
        catch (Exception e)
        {
            GD.PushError($"[world] building the traffic lights region failed: {e}");
            return $"The traffic lights region could not be built: {e.Message}";
        }
    }
}
