using System.Collections.Concurrent;
using UnitSport.Terrain.Format;

namespace UnitSport.LayoutService;

/// <summary>A building as the game has it: the solid from our own .bldg tiles, and its footprint frame.</summary>
public sealed record BuildingInfo(Building Building, TileId Tile, BuildingOutline Outline);

/// <summary>
/// Finds buildings in the deployed tiles (the same directory the game server streams), so a layout is
/// drawn on the game's footprint and keyed by the EGID the game will look up.
/// </summary>
public sealed class Buildings(string chunksDir)
{
    private readonly ConcurrentDictionary<TileId, BuildingTile?> _tiles = new();

    private BuildingTile? Tile(TileId id)
    {
        if (_tiles.TryGetValue(id, out var t)) return t;
        if (_tiles.Count > 256) _tiles.Clear(); // a crude cap: a house search touches a handful of tiles
        var path = Path.Combine(chunksDir, BuildingFormat.FileName(id));
        try
        {
            if (File.Exists(path))
                using (var s = File.OpenRead(path)) t = BuildingCodec.Decode(s);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            t = null;
        }
        return _tiles[id] = t;
    }

    /// <summary>
    /// The building at an address: the solid carrying <paramref name="egid"/> in the tile round the point
    /// (preferring one that covers the point), else the solid under the point itself. Null when none.
    /// </summary>
    public BuildingInfo? Find(uint egid, double e, double n)
    {
        var at = TileId.FromLv95(e, n);
        if (egid != 0)
        {
            BuildingInfo? best = null;
            bool bestCovers = false;
            for (int de = -1; de <= 1; de++)
            for (int dn = -1; dn <= 1; dn++)
            {
                var id = new TileId(at.E + de, at.N + dn);
                var tile = Tile(id);
                if (tile == null) continue;
                foreach (var b in tile.Buildings)
                {
                    if (b.Egid != egid) continue;
                    bool covers = b.Covers((float)(e - id.MinE), (float)(id.MaxN - n));
                    // the solid under the address first, then the biggest part carrying the EGID
                    if (best != null && (covers, b.TriangleCount).CompareTo((bestCovers, best.Building.TriangleCount)) <= 0) continue;
                    best = new BuildingInfo(b, id, null!);
                    bestCovers = covers;
                }
            }
            if (best != null) return best with { Outline = BuildingOutline.Compute(best.Building, best.Tile) };
        }
        // the solid under the point, else the nearest within NearM: an address point often sits on the
        // street side of its building, a few metres outside the solid
        float x = (float)(e - at.MinE), z = (float)(at.MaxN - n);
        var here = Tile(at);
        var hit = here?.Buildings.FirstOrDefault(b => b.Covers(x, z));
        if (hit == null && here != null)
        {
            float best = NearM * NearM;
            foreach (var b in here.Buildings)
            {
                var t = b.Triangles;
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    float dx = t[i] - x, dz = t[i + 2] - z, d = dx * dx + dz * dz;
                    if (d < best) { best = d; hit = b; }
                }
            }
        }
        return hit == null ? null : new BuildingInfo(hit, at, BuildingOutline.Compute(hit, at));
    }

    public const float NearM = 15;
}
