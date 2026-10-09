using System.Collections.Concurrent;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// The far horizon: 10 x 10 km blocks of the region's 100 m lattice, drawn out to a distance
/// the streamed LOD rings could never afford, so the ridges two valleys over are on screen
/// the moment you arrive and the world reads as an open map rather than a fogged disc.
///
/// <para>
/// Everything comes from one file (<c>horizon.bin</c>, ~1.6 MB) read once, so the blocks cost
/// no IO at all — only a worker meshing 101x101 vertices and one main-thread ArrayMesh per
/// block. The real tiles draw on top of it; where they exist, the horizon is <c>discard</c>ed
/// per tile by the coverage texture (<see cref="SetCovered"/>), so it never peeks through a
/// tunnel floor or a carved portal.
/// </para>
/// </summary>
public partial class HorizonLayer : Node3D, IOriginContainer, IOriginShiftAware
{
    private const int BlockTiles = TerrainMeshBuilder.HorizonBlockTiles;
    private const double BlockM = BlockTiles * ChunkFormat.TileSizeM;
    private const double UnloadSlackM = 10_000;
    private static readonly int MaxBuildsInFlight = Core.Platform.IsMobile ? 2   // a phone's cores are the tiles' (#63)
        : Math.Clamp(System.Environment.ProcessorCount / 2, 2, 8);

    /// <summary>Main-thread time a frame may spend committing blocks; one always goes through.</summary>
    private const double CommitBudgetMs = 3;

    /// <summary>Metres beyond which no block is loaded. 0 turns the layer off.</summary>
    public double DistanceM { get; set; }

    private IChunkSource? _source;
    private WorldOrigin? _origin;
    private ShaderMaterial? _material;
    private Func<IEnumerable<Vector3>>? _anchors;
    private HorizonIndex? _index;
    private bool _loading;

    private readonly Dictionary<(int E, int N), MeshInstance3D?> _blocks = new();
    private readonly HashSet<(int E, int N)> _building = new();
    private readonly ConcurrentQueue<((int E, int N) Key, int Epoch, int Generation, TerrainMeshBuilder.MeshData? Mesh)> _ready = new();

    /// <summary>Bumped by <see cref="Clear"/>; a block meshed from the old lattice is dropped.</summary>
    private int _epoch;
    private double _sinceEval = double.MaxValue;

    public int BlockCount => _blocks.Count;
    public ShaderMaterial? Material => _material;
    public bool HasData => _index != null;

    public void Initialize(IChunkSource source, WorldOrigin origin, ShaderMaterial material,
        Func<IEnumerable<Vector3>> anchors)
    {
        _source = source;
        _origin = origin;
        _material = material;
        _anchors = anchors;
        // deferred, so a source configured right after (the generated fill) is read once, not
        // twice: whoever reloads first this frame wins, and this then finds it loading
        Callable.From(() => { if (_index == null && !_loading) Reload(); }).CallDeferred();
    }

    /// <summary>
    /// (Re)reads the lattice — at boot, when a server streams one in, and whenever real tiles
    /// merge into the generated fill. A reload asked for while one is running runs again after
    /// it, or a merge during boot would leave a lattice generated against the old real set.
    /// </summary>
    public void Reload()
    {
        if (_source == null) return;
        if (_loading)
        {
            _reloadQueued = true;
            return;
        }
        _loading = true;
        _reloadQueued = false;
        var source = _source;
        int epoch = _epoch;
        Task.Run(async () =>
        {
            HorizonIndex? index = null;
            try { index = await source.LoadHorizonAsync(); }
            catch (Exception e) { GD.PushWarning($"[horizon] could not load: {e.Message}"); }
            Callable.From(() =>
            {
                // a Clear since this started: the lattice is from the world being replaced
                if (epoch != _epoch) return;
                _loading = false;
                if (index != null)
                {
                    _index = index;
                    _indexGeneration++;
                    GD.Print($"[horizon] {index.Count} tiles at {HorizonFormat.SpacingM} m");
                    // rebuild what is on screen from the new data, keeping each old block drawn
                    // until its replacement commits: a merge would otherwise blank the horizon
                    foreach (var key in _blocks.Keys) _stale.Add(key);
                    _sinceEval = double.MaxValue;
                }
                if (_reloadQueued) Reload();
            }).CallDeferred();
        });
    }

    private bool _reloadQueued;

    /// <summary>Bumped per lattice read; a block meshed from an older one stays stale.</summary>
    private int _indexGeneration;

    /// <summary>Blocks on screen that were meshed from an older lattice.</summary>
    private readonly HashSet<(int E, int N)> _stale = new();

    // ---- coverage: which km tiles have a real mesh on screen ---------------------------
    //
    // One texel per tile over the whole region (Switzerland is ~350 x 220 km, so a few hundred
    // texels a side), sampled by world position in the shader. Per tile rather than one ring
    // rectangle because the rectangle was wrong twice: it dropped the horizon under tiles that
    // had not arrived yet - the gap seen while loading - and kept it under tiles that had
    // left the rings but not yet unloaded.

    private Image? _coverImage;
    private ImageTexture? _coverTexture;
    private int _coverMinE, _coverMaxN, _coverCols, _coverRows;
    private bool _coverDirty;

    /// <summary>(Re)sizes the coverage texture to hold every tile the world knows about.</summary>
    public void EnsureCoverage(int minE, int maxE, int minN, int maxN)
    {
        int cols = maxE - minE + 1, rows = maxN - minN + 1;
        if (_coverImage != null && minE >= _coverMinE && maxN <= _coverMaxN
            && minE + cols <= _coverMinE + _coverCols && maxN - rows >= _coverMaxN - _coverRows)
            return;

        // a little slack so a merged server manifest rarely forces a rebuild
        minE -= 8; maxE += 8; minN -= 8; maxN += 8;
        cols = maxE - minE + 1; rows = maxN - minN + 1;
        var image = Image.CreateEmpty(cols, rows, false, Image.Format.R8);
        image.Fill(Colors.Black);
        // carry over what is already covered
        if (_coverImage != null)
            for (int r = 0; r < _coverRows; r++)
                for (int c = 0; c < _coverCols; c++)
                    if (_coverImage.GetPixel(c, r).R > 0.5f)
                    {
                        int e = _coverMinE + c, n = _coverMaxN - r;
                        image.SetPixel(e - minE, maxN - n, Colors.White);
                    }
        _coverImage = image;
        _coverMinE = minE; _coverMaxN = maxN; _coverCols = cols; _coverRows = rows;
        _coverTexture = ImageTexture.CreateFromImage(image);

        _material?.SetShaderParameter("detail_cover", _coverTexture);
        PushCoverOrigin();
        _material?.SetShaderParameter("cover_extent",
            new Vector2((float)(cols * ChunkFormat.TileSizeM), (float)(rows * ChunkFormat.TileSizeM)));
        _material?.SetShaderParameter("use_cover", true);
    }

    /// <summary>The coverage texture is sampled by world XZ, so its corner moves with the origin.</summary>
    private void PushCoverOrigin()
    {
        if (_coverImage == null) return;
        var nw = _origin!.ToWorld(_coverMinE * ChunkFormat.TileSizeM, (_coverMaxN + 1) * ChunkFormat.TileSizeM, 0);
        _material?.SetShaderParameter("cover_origin", new Vector2(nw.X, nw.Z));
    }

    /// <summary>The blocks are children and have moved already (#185); the coverage has not.</summary>
    public void OnOriginShifted(OriginShift shift) => PushCoverOrigin();

    /// <summary>Marks a tile as drawn by real terrain (or not); the texture uploads next frame.</summary>
    public void SetCovered(TileId id, bool covered)
    {
        if (_coverImage == null) return;
        int c = id.E - _coverMinE, r = _coverMaxN - id.N;
        if (c < 0 || r < 0 || c >= _coverCols || r >= _coverRows) return;
        _coverImage.SetPixel(c, r, covered ? Colors.White : Colors.Black);
        _coverDirty = true;
    }

    public override void _Process(double delta)
    {
        if (_coverDirty && _coverTexture != null && _coverImage != null)
        {
            _coverTexture.Update(_coverImage);
            _coverDirty = false;
        }

        if (_index == null || _origin == null || _anchors == null) return;

        // a 101x101 ArrayMesh is small, but a teleport wants ~170 of them: commit within a budget
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (_ready.TryDequeue(out var done))
        {
            _building.Remove(done.Key);
            // a slot is free: refill it this frame, not on the next half-second tick, or the
            // whole horizon trickles in at eight blocks a second
            _sinceEval = double.MaxValue;
            if (done.Epoch == _epoch && _blocks.ContainsKey(done.Key))
            {
                Commit(done.Key, done.Mesh);
                // meshed from a lattice that has since been replaced: drawn, and rebuilt again
                if (done.Generation == _indexGeneration) _stale.Remove(done.Key);
            }
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds > CommitBudgetMs) break;
        }

        _sinceEval += delta;
        if (_sinceEval < 0.5) return;
        _sinceEval = 0;
        Evaluate();
    }

    /// <summary>
    /// How many of the blocks within <see cref="DistanceM"/> are drawn from the current lattice,
    /// of how many are wanted: what the loading screen waits on. Null while the lattice is still
    /// being read or generated; (0, 0) when there is no horizon to draw.
    /// </summary>
    public (int Done, int Total)? Progress()
    {
        if (_loading) return null;
        if (_index == null || _origin == null || _anchors == null) return (0, 0);
        var wanted = WantedBlocks(_anchors().ToList());
        int done = 0;
        foreach (var key in wanted.Keys)
            if (_blocks.ContainsKey(key) && !_building.Contains(key) && !_stale.Contains(key)) done++;
        return (done, wanted.Count);
    }

    private void Evaluate()
    {
        var anchors = _anchors!().ToList();
        var wanted = WantedBlocks(anchors);

        foreach (var (key, _) in wanted.OrderBy(kv => kv.Value))
        {
            if (_building.Contains(key) || (_blocks.ContainsKey(key) && !_stale.Contains(key))) continue;
            if (_building.Count >= MaxBuildsInFlight) break;
            // reserved; the mesh arrives on the queue (a stale block stays drawn meanwhile)
            _blocks.TryAdd(key, null);
            _building.Add(key);
            var index = _index!;
            int epoch = _epoch, generation = _indexGeneration;
            Task.Run(() =>
            {
                TerrainMeshBuilder.MeshData? mesh = null;
                try { mesh = TerrainMeshBuilder.BuildHorizonBlock(index, key.E, key.N); }
                catch (Exception e) { GD.PushError($"[horizon] block {key} failed: {e}"); }
                _ready.Enqueue((key, epoch, generation, mesh));
            });
        }

        // unload with hysteresis
        var drop = new List<(int E, int N)>();
        foreach (var key in _blocks.Keys)
        {
            if (wanted.ContainsKey(key) || _building.Contains(key)) continue;
            double nearest = double.MaxValue;
            foreach (var a in anchors)
            {
                var (ae, an) = _origin!.ToLv95(a);
                nearest = Math.Min(nearest, BlockDistance(key.E / BlockTiles, key.N / BlockTiles, ae, an));
            }
            if (DistanceM <= 0 || nearest > DistanceM + UnloadSlackM) drop.Add(key);
        }
        foreach (var key in drop)
        {
            _stale.Remove(key);
            Free(_blocks[key]);
            _blocks.Remove(key);
        }
    }

    /// <summary>The blocks within <see cref="DistanceM"/> of any anchor, with their distance in metres.</summary>
    private Dictionary<(int E, int N), double> WantedBlocks(List<Vector3> anchors)
    {
        var wanted = new Dictionary<(int E, int N), double>();
        if (DistanceM > 0)
            foreach (var a in anchors)
            {
                var (ae, an) = _origin!.ToLv95(a);
                int span = (int)Math.Ceiling(DistanceM / BlockM) + 1;
                int cE = (int)Math.Floor(ae / BlockM), cN = (int)Math.Floor(an / BlockM);
                for (int bn = cN - span; bn <= cN + span; bn++)
                    for (int be = cE - span; be <= cE + span; be++)
                    {
                        double d = BlockDistance(be, bn, ae, an);
                        if (d > DistanceM) continue;
                        var key = (be * BlockTiles, bn * BlockTiles);
                        if (!wanted.TryGetValue(key, out double cur) || d < cur) wanted[key] = d;
                    }
            }
        return wanted;
    }

    /// <summary>Metres from a point to the nearest edge of a block (0 inside it).</summary>
    private static double BlockDistance(int be, int bn, double e, double n)
    {
        double minE = be * BlockM, minN = bn * BlockM;
        double de = Math.Max(0, Math.Max(minE - e, e - (minE + BlockM)));
        double dn = Math.Max(0, Math.Max(minN - n, n - (minN + BlockM)));
        return Math.Max(de, dn);
    }

    private void Commit((int E, int N) key, TerrainMeshBuilder.MeshData? data)
    {
        // what this replaces, if it is a rebuild from a newer lattice
        Free(_blocks[key]);
        _blocks[key] = null;
        if (data == null) return;   // nothing built there; keep the key so it is not retried

        var mesh = ChunkNode.ToArrayMesh(data, _material!);

        var instance = new MeshInstance3D
        {
            Name = $"Horizon_{key.E}_{key.N}",
            Mesh = mesh,
            // ground: never a sun-shadow caster (lit styles)
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // NW corner of the block, like a tile
            Position = _origin!.ToWorld(key.E * ChunkFormat.TileSizeM, (key.N + BlockTiles) * ChunkFormat.TileSizeM, 0),
        };
        AddChild(instance);
        _blocks[key] = instance;
    }

    /// <summary>
    /// Frees a block and its mesh now: the mesh's memory is the RenderingServer's, so left to the
    /// finalizer every rebuilt or dropped block kept its mesh alive (see ChunkNode.ReleaseResources).
    /// </summary>
    private static void Free(MeshInstance3D? block)
    {
        if (block == null) return;
        var mesh = block.Mesh;
        block.Mesh = null;
        mesh?.Dispose();
        block.QueueFree();
    }
}
