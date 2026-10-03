using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>Reads .terr chunks + manifest.json from a local directory.</summary>
public sealed class LocalChunkSource : IChunkSource
{
    private readonly string _dir;

    public LocalChunkSource(string dir) => _dir = dir;

    /// <summary>
    /// Reads manifest.json, or returns an empty manifest when there is none.
    ///
    /// <para>
    /// A fresh clone has no <c>terrain_chunks/</c> at all — the generated data is 5.3 GB and
    /// is not in the repository — so "no manifest" is an ordinary state, not an error. An
    /// empty manifest is the truthful answer: this source has no tiles. The caller decides
    /// what to do about it, and with a server to join the answer is "nothing, it will all
    /// stream".
    /// </para>
    /// </summary>
    public async Task<TerrainManifest> LoadManifestAsync(CancellationToken ct = default)
    {
        string path = Path.Combine(_dir, "manifest.json");

        if (!File.Exists(path))
        {
            GD.PushWarning($"[terrain] no manifest at {path}; this copy has no terrain data");
            return new TerrainManifest();
        }

        try
        {
            string json = await File.ReadAllTextAsync(path, ct);
            return TerrainManifest.FromJson(json);
        }
        catch (Exception e)
        {
            // A truncated or half-written manifest should not take the boot down either.
            GD.PushError($"[terrain] {path} could not be read: {e.Message}");
            return new TerrainManifest();
        }
    }

    public Task<ChunkGrid?> LoadChunkAsync(TileId id, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            string path = Path.Combine(_dir, ChunkFormat.ChunkFileName(id));
            if (!File.Exists(path)) return (ChunkGrid?)null;
            using var fs = File.OpenRead(path);
            return ChunkCodec.Decode(fs);
        }, ct);
    }

    public Task<ChunkGrid?> LoadCoarseChunkAsync(TileId id, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            string path = Path.Combine(_dir, ChunkFormat.CoarseFileName(id));
            if (!File.Exists(path)) return (ChunkGrid?)null;
            using var fs = File.OpenRead(path);
            return ChunkCodec.Decode(fs);
        }, ct);
    }

    public Task<HashSet<int>?> LoadHolesAsync(TileId id, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            string path = Path.Combine(_dir, HoleFormat.FileName(id));
            if (!File.Exists(path)) return (HashSet<int>?)null;
            using var fs = File.OpenRead(path);
            return HoleFormat.Decode(fs);
        }, ct);
    }

    public Task<BuildingTile?> LoadBuildingsAsync(TileId id, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            string path = Path.Combine(_dir, BuildingFormat.FileName(id));
            if (!File.Exists(path)) return (BuildingTile?)null;
            using var fs = File.OpenRead(path);
            return BuildingCodec.Decode(fs);
        }, ct);
    }

    public Task<byte[]?> LoadCoverAsync(TileId id, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            string path = Path.Combine(_dir, CoverFormat.FileName(id));
            if (!File.Exists(path)) return (byte[]?)null;
            using var fs = File.OpenRead(path);
            return CoverFormat.Decode(fs);
        }, ct);
    }

    /// <summary>The preprocessor's <c>.water</c> layer (#298), on the runtime's 2 m lattice; null where the tile has no water file.</summary>
    public Task<WaterTile?> LoadWaterAsync(TileId id, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            string path = Path.Combine(_dir, WaterFormat.FileName(id));
            if (!File.Exists(path)) return (WaterTile?)null;
            using var fs = File.OpenRead(path);
            return WaterFormat.Decode(fs).ToTile();
        }, ct);
    }

    public Task<List<TreeInstance>?> LoadTreesAsync(TileId id, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            string path = Path.Combine(_dir, TreeFormat.FileName(id));
            if (!File.Exists(path)) return (List<TreeInstance>?)null;
            using var fs = File.OpenRead(path);
            return TreeFormat.Decode(fs);
        }, ct);
    }

    public Task<HorizonIndex?> LoadHorizonAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            string path = Path.Combine(_dir, HorizonFormat.FileName);
            if (!File.Exists(path)) return (HorizonIndex?)null;
            using var fs = File.OpenRead(path);
            return HorizonFormat.Decode(fs);
        }, ct);
    }

    public Task<LandingIndex?> LoadLandingsAsync(CancellationToken ct = default) => ReadLandingsAsync(_dir, ct);

    /// <summary><c>landings.json</c> in a directory, or null; a file that does not parse is logged and skipped.</summary>
    public static Task<LandingIndex?> ReadLandingsAsync(string dir, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            string path = Path.Combine(dir, LandingIndex.FileName);
            if (!File.Exists(path)) return (LandingIndex?)null;
            try { return LandingIndex.FromJson(File.ReadAllText(path)); }
            catch (Exception e)
            {
                Godot.GD.PushWarning($"[landings] {path} does not parse: {e.Message}");
                return null;
            }
        }, ct);
    }

    public Task<RoadTile?> LoadRoadsAsync(TileId id, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            string path = Path.Combine(_dir, RoadFormat.FileName(id));
            if (!File.Exists(path)) return (RoadTile?)null;
            using var fs = File.OpenRead(path);
            return RoadCodec.Decode(fs);
        }, ct);
    }
}
