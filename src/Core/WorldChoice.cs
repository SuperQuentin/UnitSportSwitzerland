using Godot;
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

    public static readonly WorldChoice Open = new(OpenId, "UnitSport open world",
        "The real Switzerland where its terrain is on this machine, generated everywhere else.");

    /// <summary>Every world the pickers offer, the open world first.</summary>
    public static readonly WorldChoice[] All =
    {
        Open,
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

    // ---- the traffic lights region ---------------------------------------------------------------

    /// <summary>The region's tiles, built by the game itself (<see cref="Prepare"/>).</summary>
    public static string SignalsDir => ProjectSettings.GlobalizePath($"user://worlds/{SignalsId}/chunks");

    private static string SignalsStamp => System.IO.Path.Combine(SignalsDir, "built_by.txt");

    private static string RoadGenStamp => typeof(SignalTestRegion).Assembly.ManifestModule.ModuleVersionId.ToString();

    /// <summary>
    /// Nothing to build before this world loads: a fixture course, or the region already built by
    /// this RoadGen. When false, <see cref="Prepare"/> takes a while (~15 s in a release build, close to
    /// a minute in the editor's debug build): the loading screen says so.
    /// </summary>
    public bool Prepared
    {
        get
        {
            if (Id != SignalsId) return true;
            try
            {
                return System.IO.File.Exists(SignalsStamp) && System.IO.File.ReadAllText(SignalsStamp).Trim() == RoadGenStamp
                    && System.IO.File.Exists(System.IO.Path.Combine(SignalsDir, "manifest.json"));
            }
            catch (System.IO.IOException) { return false; }
        }
    }

    /// <summary>What the loading screen says while <see cref="Prepare"/> builds.</summary>
    public const string PreparingDetail = "Building the traffic lights region: up to a minute, the first time";

    /// <summary>
    /// Makes sure this world's tiles exist; run on a worker. A fixture course is built in code as
    /// it streams, so only the traffic lights region has anything to do: the game runs RoadGen's
    /// <c>--test-region</c> into <see cref="SignalsDir"/> (~2 s), again whenever RoadGen changed
    /// (its module id is the stamp). Returns an error for the player, or null.
    /// </summary>
    public string? Prepare()
    {
        if (Prepared) return null;
        string stamp = RoadGenStamp;
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            System.IO.Directory.CreateDirectory(SignalsDir);
            // its verdict on the junctions is a check's; the tiles are written either way
            SignalTestRegion.Run(SignalsDir, SignalsDir + "_temp", _ => { });
            if (!System.IO.File.Exists(System.IO.Path.Combine(SignalsDir, "manifest.json")))
                return "The traffic lights region could not be built. See the log.";
            System.IO.File.WriteAllText(SignalsStamp, stamp);
            GD.Print($"[world] built the traffic lights region in {clock.Elapsed.TotalSeconds:F1} s: {SignalsDir}");
            return null;
        }
        catch (Exception e)
        {
            GD.PushError($"[world] building the traffic lights region failed: {e}");
            return $"The traffic lights region could not be built: {e.Message}";
        }
    }
}
