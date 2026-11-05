namespace UnitSport.Tools.RoadGen.Rewrite;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// The network stage's tile loop (#1507, G2 B3 4a): the blocks it runs one after another, the tiles it
/// finishes, what it reads (<see cref="TileRewriter.Inputs"/>, with the units and the ground), and where a
/// finished tile goes. <see cref="Lv95TileLoop"/> is the LV95 build's (files in a chunk directory); a work
/// frame's (4b) runs a frame's tiles and writes leaves.
/// </summary>
public interface ITileLoop
{
    /// <summary>The blocks, each run as one (its halo read for context).</summary>
    IReadOnlyList<HashSet<TileId>> Blocks { get; }

    /// <summary>The tiles finished; any other tile a block holds is context.</summary>
    HashSet<TileId> Wanted { get; }

    TileRewriter.Inputs Inputs { get; }

    /// <summary>
    /// Whether no block reads anything another writes (<see cref="Inputs"/>' functions and <see cref="Write"/> then run on several
    /// threads at once, each block's tiles on its own): false runs them one after another (#1539).
    /// </summary>
    bool BlocksIndependent { get; }

    /// <summary>A finished tile and its encoded bytes, on the thread that ran its block.</summary>
    void Write(RoadTile tile, byte[] bytes);

    /// <summary>After the last block: what the run writes besides its tiles.</summary>
    void Finish();
}

/// <summary>
/// The LV95 build's loop: the raw extractor output (or a fresh tile) of each tile asked for, in
/// <see cref="TileRewriter.Options.BlockSize"/> km blocks, with the region's OSM, canton, signal and gantry
/// inputs, and the finished <c>.road</c> files written into the chunk directory.
/// </summary>
public sealed class Lv95TileLoop : ITileLoop
{
    private readonly string _chunkDir;
    private readonly TileRewriter.Options _options;
    private readonly GantryContext? _gantries;

    public IReadOnlyList<HashSet<TileId>> Blocks { get; }
    public HashSet<TileId> Wanted { get; }
    public TileRewriter.Inputs Inputs { get; }

    /// <summary>
    /// A tile with no raw input is read from the chunk directory that the stage writes (and stashed there first, or, with
    /// <see cref="TileRewriter.Options.Force"/>, read as a block before it left it): those runs stay one block after another.
    /// </summary>
    public bool BlocksIndependent { get; }

    public Lv95TileLoop(string chunkDir, IReadOnlyList<TileId> tiles, TileRewriter.Options options, Action<string> log)
    {
        _chunkDir = chunkDir;
        _options = options;
        string rawDir = options.RawDir ?? RawRoads.DirFor(RawRoads.DefaultTempDir(chunkDir));
        // the car park rings are written to their own directory beside the raw roads, not into it
        // (#499, ParkingAreaExtractor -> RawParking.DirFor), so they are found from the temp dir
        string parkDir = RawParking.DirFor(Directory.GetParent(rawDir)?.FullName ?? RawRoads.DefaultTempDir(chunkDir));

        // Input per tile: the raw extractor output if kept, else a fresh (never rewritten) tile
        // in the chunk dir. A rewritten tile with no raw input would be trimmed a second time.
        var targets = new List<TileId>();
        var rewritten = new List<TileId>();
        foreach (var id in tiles)
        {
            if (File.Exists(RawRoads.RoadPath(rawDir, id))) targets.Add(id);
            else if (!File.Exists(Path.Combine(chunkDir, RoadFormat.FileName(id)))) continue;
            else if (!options.Force && RawRoads.IsRewritten(Path.Combine(chunkDir, RoadFormat.FileName(id)))) rewritten.Add(id);
            else targets.Add(id);
        }
        if (rewritten.Count > 0 && !options.SkipRewritten)
            throw new TileRewriter.AlreadyRewrittenException(
                $"{rewritten.Count} of {tiles.Count} tiles were already rewritten (v2, or v3 from the network\n"
                + $"stage) and have no raw input in {rawDir}.\n"
                + "Rewriting is not idempotent on its own output — a second pass trims roads that are\n"
                + "already trimmed and leaves a hole at every junction. Re-run the roads preprocessing\n"
                + "(it keeps the raw input), --skip-rewritten to leave them, or --force if you know better.");
        if (rewritten.Count > 0) log($"  skipping {rewritten.Count} rewritten tiles with no raw input");

        var overlay = options.OsmOverlay is { } overlayPath ? OsmOverlayReader.TryLoad(overlayPath) : null;
        var cantons = options.Cantons ?? TileRewriter.Cantons.Find();   // pedestrian heads per canton (#348); a test world's zones (#1038)
        // OSM traffic signals (#347), beside the overlay
        var osmNodes = options.OsmOverlay is { } nodesBeside
            ? OsmNodesReader.TryLoad(Path.Combine(Path.GetDirectoryName(nodesBeside) ?? ".", OsmNodesReader.FileName)) : null;
        var signalSites = TileRewriter.SignalSites.From(osmNodes);
        var restrictions = TileRewriter.Restrictions.From(osmNodes);   // turns forbidden at an approach (#353)
        var crossingNodes = TileRewriter.CrossingNodes.From(osmNodes);   // pedestrian crossings (#700)
        // walkers' crossings over a tram track left without lights (#1176: OSM railway=tram_crossing, crossing=uncontrolled)
        TileRewriter.UncontrolledTramCrossings = osmNodes is null ? [] : osmNodes.All.Where(e => e.Kind == OsmNodesReader.NodeKind.Crossing
            && e.Value == "uncontrolled" && e.Tags.GetValueOrDefault("railway") == "tram_crossing").Select(e => new Vec2(e.E, e.N)).ToList();
        if (signalSites is not null) log($"  OSM traffic signals: {signalSites.Count:N0} junction signals");
        if (overlay is not null) log($"  OSM overlay: {overlay.RowCount:N0} rows from {options.OsmOverlay}");

        BlocksIndependent = targets.All(id => File.Exists(RawRoads.RoadPath(rawDir, id)));
        Wanted = new HashSet<TileId>(targets);
        Blocks = GroupIntoBlocks(targets, options.BlockSize);
        // gantries of lane signs (#1110): SITG's in canton Geneva, the rule elsewhere
        _gantries = options.Gantries ? GantryContext.Load(options.SitgSigns, options.Places) : null;
        if (_gantries is not null) _gantries.Overlay = overlay;
        if (_gantries?.Sitg is { } sitg) log($"  SITG lane signs (#1110): {sitg.Gantries.Count:N0} gantries from {options.SitgSigns}");

        var wanted = Wanted;
        Inputs = new TileRewriter.Inputs
        {
            Roads = id => TileRewriter.LoadInput(chunkDir, rawDir, id, stash: wanted.Contains(id) && !options.DryRun),
            Grid = id => TileRewriter.LoadGrid(chunkDir, id),
            Buildings = id => Facades.Read(chunkDir, id),
            Parking = id => RawParking.Read(parkDir, id),
            Overlay = overlay, Cantons = cantons, SignalSites = signalSites, Restrictions = restrictions, Crossings = crossingNodes,
            Gantries = _gantries,
        };
    }

    public void Write(RoadTile tile, byte[] bytes)
    {
        string path = Path.Combine(_chunkDir, RoadFormat.FileName(tile.Id));
        string temp = path + ".part";

        // write via a temp file: a half-written .road looks exactly like a valid short
        // one, and the region being rewritten is the region being played
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    public void Finish()
    {
        if (_gantries is not null && _options.GantryFacts is { } factsPath && !_options.DryRun) _gantries.WriteFacts(factsPath);
    }

    /// <summary>The tiles in <paramref name="size"/> km blocks of the LV95 grid.</summary>
    private static List<HashSet<TileId>> GroupIntoBlocks(IReadOnlyList<TileId> tiles, int size)
    {
        var blocks = new Dictionary<(int, int), HashSet<TileId>>();
        foreach (var id in tiles)
        {
            var key = ((int)Math.Floor(id.E / (double)size), (int)Math.Floor(id.N / (double)size));
            if (!blocks.TryGetValue(key, out var set)) blocks[key] = set = new HashSet<TileId>();
            set.Add(id);
        }
        return blocks.Values.ToList();
    }
}
