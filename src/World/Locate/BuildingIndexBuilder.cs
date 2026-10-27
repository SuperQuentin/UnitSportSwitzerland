using System.Security.Cryptography;
using System.Text;
using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Locate;

/// <summary>
/// Builds the <see cref="BuildingIndex"/> (#1277) from a world's own <c>.bldg</c> tiles: one pass, on
/// workers, at boot. Measured before writing it: decoding the 626 tiles of the Lake Geneva set (86 520
/// buildings) takes 0.7 s, so the 6 699-tile dev region is a few seconds; the index is then written to
/// <c>user://cache/locate/</c> under a key made of the manifest's tile hashes, and the next boot reads it
/// back at once. The flags are the game's own rules (<see cref="BuildingTypes"/> for churches and their
/// towers, <see cref="GarageRule"/> for the garage roll, <see cref="BuildingFootprint.IsBank"/>), so what
/// <c>/locate</c> finds is what stands there.
/// </summary>
public static class BuildingIndexBuilder
{
    public const string CacheDir = "user://cache/locate";

    /// <summary>
    /// The cache file's name for this set of tiles, from the manifest's content hashes; null when a tile has
    /// none (an old manifest, a fixture): then the index is built every boot and not written.
    /// </summary>
    public static string? CacheKey(TerrainManifest manifest)
    {
        if (manifest.Tiles.Count == 0 || manifest.Tiles.Any(t => t.Hash == null)) return null;
        var sb = new StringBuilder($"v{BuildingIndex.Version}|r{BuildingRules}|");
        foreach (var t in manifest.Tiles.OrderBy(t => t.E).ThenBy(t => t.N)) sb.Append(t.E).Append('_').Append(t.N).Append(':').Append(t.Hash).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24].ToLowerInvariant();
    }

    /// <summary>Bump when a rule the flags read changes (the church grouping, the garage roll): the cached indexes go stale.</summary>
    public const int BuildingRules = 2;

    /// <summary>The cached index for this key, or null.</summary>
    public static BuildingIndex? ReadCache(string key)
    {
        string path = ProjectSettings.GlobalizePath($"{CacheDir}/buildings-{key}.bin");
        if (!File.Exists(path)) return null;
        try
        {
            using var f = File.OpenRead(path);
            return BuildingIndex.Read(new BufferedStream(f, 1 << 16));
        }
        catch (IOException e)
        {
            GD.PushWarning($"[locate] cache {path}: {e.Message}");
            return null;
        }
    }

    public static void WriteCache(string key, BuildingIndex index)
    {
        string dir = ProjectSettings.GlobalizePath(CacheDir);
        try
        {
            Directory.CreateDirectory(dir);
            // one index per region: older keys are other builds of it, or other regions long gone
            foreach (string old in Directory.GetFiles(dir, "buildings-*.bin")) File.Delete(old);
            string path = Path.Combine(dir, $"buildings-{key}.bin");
            using (var f = File.Create(path + ".part"))
            using (var b = new BufferedStream(f, 1 << 16))
                index.Write(b);
            File.Move(path + ".part", path, overwrite: true);
        }
        catch (IOException e)
        {
            GD.PushWarning($"[locate] could not write the building index cache: {e.Message}");
        }
    }

    /// <summary>
    /// Reads every tile of <paramref name="tiles"/> from <paramref name="source"/> and indexes its buildings,
    /// <paramref name="workers"/> tiles at a time. <paramref name="progress"/> gets the tiles done (from a worker).
    /// </summary>
    public static async Task<BuildingIndex> BuildAsync(IChunkSource source, IReadOnlyList<TileId> tiles, int workers,
        Action<int>? progress = null, CancellationToken ct = default)
    {
        var parts = new List<IndexedBuilding>[tiles.Count];
        int done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, tiles.Count), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers), CancellationToken = ct },
            async (i, token) =>
            {
                var tile = await source.LoadBuildingsAsync(tiles[i], token);
                parts[i] = tile == null ? [] : Entries(tile);
                progress?.Invoke(Interlocked.Increment(ref done));
            });
        return new BuildingIndex(parts.SelectMany(p => p).ToList());
    }

    /// <summary>A spire inside the hall's own solid: this much higher than the eave at least, m...</summary>
    public const float SpireMinRise = 12f;
    /// <summary>...and than this share of the hall's narrow side (a roof's ridge at 60° is 0.87 of it)...</summary>
    public const float SpireRiseToWidth = 0.9f;
    /// <summary>...with its top this narrow, m: a ridge's top is a line the length of the nave, a spire's a point.</summary>
    public const float SpireTopWidth = 8f, SpireTopDrop = 3f;

    /// <summary>
    /// One tile's buildings as the index keeps them. A plan box is worked out only for the kinds that need
    /// one (a church's hall or tower, a block that may roll a garage, a bank): the houses, farms and works that
    /// are most of a region never do, and the box is most of the cost.
    /// </summary>
    public static List<IndexedBuilding> Entries(BuildingTile tile)
    {
        int count = tile.Buildings.Count;
        var boxes = new PlanBox?[count];
        bool sacral = false;
        for (int i = 0; i < count; i++)
        {
            var kind = tile.Buildings[i].Kind;
            sacral |= kind == BuildingKind.Sacral;
            if (BuildingTypes.ChurchPart(kind) || kind is BuildingKind.Apartment or BuildingKind.Commercial)
                boxes[i] = PlanBox.Of(tile.Buildings[i]);
        }
        // every church has a Sacral solid (its hall, or its tower when the GWR point landed there)
        var churches = sacral ? BuildingTypes.Churches(tile, boxes) : [];

        var list = new List<IndexedBuilding>(count);
        for (int i = 0; i < count; i++)
        {
            var b = tile.Buildings[i];
            var box = boxes[i];
            Vector2 c = box?.Center ?? Centre(b);
            var flags = IndexedFlags.None;
            if (churches.Find(g => g.Primary == i) is { } church)
            {
                flags |= IndexedFlags.Church;
                if (church.Parts.Contains(BuildingPart.Tower) || box is { } hall && Steeple(b, hall)) flags |= IndexedFlags.Spire;
            }
            if (box is { } pb && b.Kind is BuildingKind.Apartment or BuildingKind.Commercial or BuildingKind.Other)
            {
                string key = new BuildingKey(tile.Id.E, tile.Id.N, i).ToString();
                if (BuildingFootprint.IsBank(key, b.Kind, pb.Width, pb.Depth)) flags |= IndexedFlags.Bank;
                else
                {
                    var (storeyH, above) = InteriorGenerator.Storeys(b);
                    var type = GarageRule.BlockType(key, pb.Area, b.Kind, above, false);
                    // which wall is the front is the footprint's to say: either way round makes it a candidate
                    if (GarageRule.RampFor(key, type, above, pb.Width, pb.Depth, storeyH) != GarageRule.RampKind.None
                        || GarageRule.RampFor(key, type, above, pb.Depth, pb.Width, storeyH) != GarageRule.RampKind.None)
                        flags |= IndexedFlags.GarageRoll;
                }
            }
            list.Add(new IndexedBuilding(tile.Id.MinE + c.X, tile.Id.MaxN - c.Y, tile.Id.E, tile.Id.N, i, b.Kind,
                b.Floors, b.YearBuilt, b.MaxY - b.MinY, flags));
        }
        return list;
    }

    /// <summary>
    /// Whether a hall carries its steeple in its own solid (the survey models Lausanne's cathedral, 75 m to the
    /// cross over a 36 m eave, as one): far higher than its eave, and narrow at the top.
    /// </summary>
    public static bool Steeple(Building b, PlanBox hall)
    {
        float rise = b.MaxY - hall.Eave;
        if (rise < Math.Max(SpireMinRise, SpireRiseToWidth * hall.Short)) return false;
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        var t = b.Triangles;
        for (int v = 0; v + 2 < t.Length; v += 3)
        {
            if (t[v + 1] < b.MaxY - SpireTopDrop) continue;
            minX = Math.Min(minX, t[v]); maxX = Math.Max(maxX, t[v]);
            minZ = Math.Min(minZ, t[v + 2]); maxZ = Math.Max(maxZ, t[v + 2]);
        }
        return maxX - minX <= SpireTopWidth && maxZ - minZ <= SpireTopWidth;
    }

    /// <summary>The middle of a solid's vertices in plan (tile-local X east, Z south), for one with no plan box.</summary>
    private static Vector2 Centre(Building b)
    {
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        for (int v = 0; v + 2 < b.Triangles.Length; v += 3)
        {
            minX = Math.Min(minX, b.Triangles[v]); maxX = Math.Max(maxX, b.Triangles[v]);
            minZ = Math.Min(minZ, b.Triangles[v + 2]); maxZ = Math.Max(maxZ, b.Triangles[v + 2]);
        }
        return b.Triangles.Length < 3 ? Vector2.Zero : new Vector2((minX + maxX) / 2, (minZ + maxZ) / 2);
    }
}
