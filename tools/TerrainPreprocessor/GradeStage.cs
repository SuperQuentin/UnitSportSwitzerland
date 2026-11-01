using System.Collections.Concurrent;
using System.Diagnostics;
using UnitSport.Terrain.Format;

namespace UnitSport.Tools.Preprocessor;

/// <summary>
/// The tile grading stage (#1419): building pads (<see cref="BuildingPad"/>) written into the <c>.terr</c>
/// heights, so the coarse rings, the collision and the roads draped afterwards all see the graded ground.
///
/// <para>
/// <b>Order.</b> Runs between the building extraction (the <c>.bldg</c> files it reads) and the road
/// extraction (which drapes on the heights it leaves). A building's base is seated on the raw
/// heights, so <see cref="Restore"/> first puts back the originals of an earlier run.
/// </para>
///
/// <para>
/// <b>Seams.</b> A pad near a tile border reaches into the neighbour. Every tile is graded from the
/// footprints of its own and its eight neighbours' buildings, in one global order (owner tile, then
/// building index), and the neighbours of a requested tile are graded with it, so the shared border
/// vertices get bit-identical heights in both tiles (checked with <see cref="TerrainBuild.VerifySeams"/>).
/// </para>
///
/// <para>
/// <b>Re-runnable.</b> The cells a run changed are kept in <c>&lt;temp&gt;/grade/E_N.grd</c> as
/// (cell, original, graded). A later run puts a cell back to its original only if it still holds
/// the graded value, so heights another pass wrote since (a lake bed) are not undone, and grading
/// again never piles a second pad on the first. The heights keep the tile format and its encoding
/// (only their values change), so the format version stays.
/// </para>
/// </summary>
public static class GradeStage
{
    /// <summary>The sidecar directory of the pads; <see cref="TownGroundStage"/> keeps its own beside it.</summary>
    internal const string PadDir = "grade";

    internal static string SidecarPath(string tempDir, TileId id, string sub = PadDir) => Path.Combine(tempDir, sub, $"{id.E}_{id.N}.grd");

    internal readonly record struct Change(int Cell, ushort Original, ushort Graded);

    internal static List<Change> ReadSidecar(string tempDir, TileId id, string sub = PadDir)
    {
        var list = new List<Change>();
        string path = SidecarPath(tempDir, id, sub);
        if (!File.Exists(path)) return list;
        using var r = new BinaryReader(File.OpenRead(path));
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++) list.Add(new Change(r.ReadInt32(), r.ReadUInt16(), r.ReadUInt16()));
        return list;
    }

    internal static void WriteSidecar(string tempDir, TileId id, List<Change> changes, string sub = PadDir)
    {
        string path = SidecarPath(tempDir, id, sub);
        if (changes.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Write(path, s =>
        {
            using var w = new BinaryWriter(s, System.Text.Encoding.UTF8, leaveOpen: true);
            w.Write(changes.Count);
            foreach (var c in changes) { w.Write(c.Cell); w.Write(c.Original); w.Write(c.Graded); }
        });
    }

    /// <summary>Puts the original of every cell an earlier run graded and nothing has changed since back.</summary>
    internal static int Undo(ushort[] heights, List<Change> changes)
    {
        int n = 0;
        foreach (var c in changes)
            if (heights[c.Cell] == c.Graded && c.Original != c.Graded) { heights[c.Cell] = c.Original; n++; }
        return n;
    }

    internal static ChunkGrid GridOf(TileId id, ushort[] heights)
    {
        ushort qMin = ushort.MaxValue, qMax = 0;
        foreach (ushort q in heights) { if (q < qMin) qMin = q; if (q > qMax) qMax = q; }
        return new ChunkGrid(id, heights, (float)ChunkFormat.Dequantize(qMin), (float)ChunkFormat.Dequantize(qMax));
    }

    internal static void WriteTile(string outDir, ChunkGrid grid)
    {
        AtomicFile.Write(Path.Combine(outDir, ChunkFormat.ChunkFileName(grid.Id)), s => ChunkCodec.Encode(grid, s));
        AtomicFile.Write(Path.Combine(outDir, ChunkFormat.CoarseFileName(grid.Id)),
            s => ChunkCodec.Encode(grid.Decimate(ChunkFormat.CoarseStride), s));
    }

    internal static ChunkGrid ReadTile(string outDir, TileId id)
    {
        using var fs = File.OpenRead(Path.Combine(outDir, ChunkFormat.ChunkFileName(id)));
        return ChunkCodec.Decode(fs);
    }

    /// <summary>The tiles and their neighbours that exist: a pad reaches into the next tile, so both are graded together.</summary>
    internal static HashSet<TileId> ScopeOf(string outDir, IReadOnlyCollection<TileId> tiles)
    {
        var manifest = TerrainManifest.FromJson(File.ReadAllText(Path.Combine(outDir, "manifest.json")));
        var region = manifest.Tiles.Select(t => t.Id).ToHashSet();
        var scope = new HashSet<TileId>();
        foreach (var id in tiles)
            for (int dn = -1; dn <= 1; dn++)
                for (int de = -1; de <= 1; de++)
                {
                    var nb = new TileId(id.E + de, id.N + dn);
                    if (region.Contains(nb)) scope.Add(nb);
                }
        return scope;
    }

    /// <summary>
    /// Undoes the grading of the tiles that have a record, so the next extraction seats buildings on
    /// the raw heights. The coarse companions, the manifest and the horizon follow in <see cref="Run"/>.
    /// </summary>
    public static int Restore(string outDir, string tempDir, IReadOnlyCollection<TileId> tiles, int jobs, CancellationToken ct = default,
        string sub = PadDir)
    {
        string dir = Path.Combine(tempDir, sub);
        if (!Directory.Exists(dir)) return 0;
        var scope = ScopeOf(outDir, tiles);
        int restored = 0, cells = 0;
        Parallel.ForEach(scope, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct }, id =>
        {
            if (!File.Exists(SidecarPath(tempDir, id, sub)) || !File.Exists(Path.Combine(outDir, ChunkFormat.ChunkFileName(id)))) return;
            var grid = ReadTile(outDir, id);
            var heights = (ushort[])grid.Heights.Clone();
            int n = Undo(heights, ReadSidecar(tempDir, id, sub));
            if (n == 0) return;
            AtomicFile.Write(Path.Combine(outDir, ChunkFormat.ChunkFileName(id)), s => ChunkCodec.Encode(GridOf(id, heights), s));
            Interlocked.Increment(ref restored);
            Interlocked.Add(ref cells, n);
        });
        if (restored > 0) Console.WriteLine($"Grade ({sub}): restored the heights of {restored} tiles ({cells:N0} cells) before seating the buildings");
        return 0;
    }

    /// <summary>
    /// Grades the tiles in <paramref name="tiles"/> and their neighbours under the pads of the buildings
    /// in their <c>.bldg</c> files, then refreshes the coarse companions, the manifest's min/max and the
    /// horizon, and checks the seams.
    /// </summary>
    public static int Run(string outDir, string tempDir, IReadOnlyCollection<TileId> tiles, int jobs, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        string manifestPath = Path.Combine(outDir, "manifest.json");
        var manifest = TerrainManifest.FromJson(File.ReadAllText(manifestPath));
        var scope = ScopeOf(outDir, tiles);

        var footprints = new ConcurrentDictionary<TileId, Lazy<List<BuildingPad.Footprint>>>();
        List<BuildingPad.Footprint> FootprintsOf(TileId id) => footprints.GetOrAdd(id, k => new Lazy<List<BuildingPad.Footprint>>(() =>
        {
            var list = new List<BuildingPad.Footprint>();
            string path = Path.Combine(outDir, BuildingFormat.FileName(k));
            if (!File.Exists(path)) return list;
            BuildingTile bt;
            using (var fs = File.OpenRead(path)) bt = BuildingCodec.Decode(fs);
            foreach (var b in bt.Buildings)
                if (b.Kind != BuildingKind.UnderConstruction && BuildingPad.FootprintOf(b, k) is { } f) list.Add(f);
            return list;
        })).Value;

        var changedTiles = new ConcurrentBag<ManifestTile>();
        long cellsChanged = 0, padCount = 0;
        int written = 0;
        // column by column: a tile reads the columns either side of it, so three are held at a time
        foreach (var column in scope.GroupBy(t => t.E).OrderBy(g => g.Key))
        {
            foreach (var key in footprints.Keys)
                if (key.E < column.Key - 1) footprints.TryRemove(key, out _);
            Parallel.ForEach(column, new ParallelOptions { MaxDegreeOfParallelism = jobs, CancellationToken = ct }, id =>
            {
                var current = ReadTile(outDir, id);
                var sidecar = ReadSidecar(tempDir, id);
                var heights = (ushort[])current.Heights.Clone();
                Undo(heights, sidecar);
                var original = (ushort[])heights.Clone();

                var near = new List<BuildingPad.Footprint>();
                for (int de = -1; de <= 1; de++)
                    for (int dn = -1; dn <= 1; dn++)
                        near.AddRange(FootprintsOf(new TileId(id.E + de, id.N + dn)));
                // owner tile order (E, then N): the order the loops above produce is E-major, N-minor
                int n = BuildingPad.Grade(id, heights, near);

                var changes = new List<Change>(n);
                for (int k = 0; k < heights.Length; k++)
                    if (heights[k] != original[k]) changes.Add(new Change(k, original[k], heights[k]));
                WriteSidecar(tempDir, id, changes);
                Interlocked.Add(ref cellsChanged, n);
                if (n > 0) Interlocked.Increment(ref padCount);

                // the file is rewritten whenever it differs from what is on disk (the original restored, the pads in)
                if (!heights.AsSpan().SequenceEqual(current.Heights))
                {
                    var grid = GridOf(id, heights);
                    WriteTile(outDir, grid);
                    changedTiles.Add(new ManifestTile { E = id.E, N = id.N, Min = grid.MinHeight, Max = grid.MaxHeight });
                    Interlocked.Increment(ref written);
                }
            });
        }

        if (!changedTiles.IsEmpty)
        {
            var byId = changedTiles.ToDictionary(t => t.Id);
            foreach (var t in manifest.Tiles)
                if (byId.TryGetValue(t.Id, out var c)) { t.Min = c.Min; t.Max = c.Max; }
            AtomicFile.Write(manifestPath, s => { using var w = new StreamWriter(s); w.Write(manifest.ToJson()); });
            if (HorizonStage.Run(outDir, jobs, ct) is var hrc && hrc != 0) return hrc;
        }
        int seamErrors = TerrainBuild.VerifySeams(outDir, scope, jobs, ct);
        Console.WriteLine($"Grade pass: {scope.Count} tiles, {padCount} with pads, {cellsChanged:N0} cells graded, {written} files written in {clock.Elapsed.TotalSeconds:F1}s"
            + (seamErrors > 0 ? $", {seamErrors} SEAM ERRORS" : ""));
        return seamErrors > 0 ? 1 : 0;
    }
}
