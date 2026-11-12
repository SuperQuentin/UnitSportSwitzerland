using System.Collections.Concurrent;
using System.Diagnostics;
using Godot;
using UnitSport.Styles;
using UnitSport.Terrain.Format;

namespace UnitSport.Farming;

// Drawing the fields near the camera (#494, docs/notes/farming/fields-runtime.md): one mesh per
// 100 m chunk (FieldTile.ChunkCells² cells), built on a worker (FieldMeshBuilder), rebuilt only when
// one of its cells changed, its detail ring changed, its ground grid was replaced or a crop grew
// past the next tenth. Never per frame: the schedule runs four times a second, commits are
// capped per frame.
public partial class FarmField
{
    /// <summary>Chunks whose centre is within this of the camera are drawn...</summary>
    public const double DrawRadius = 360;
    /// <summary>...with full detail (furrows, rows, heads) within this.</summary>
    public const double DetailRadius = 160;
    private const int MaxInflight = 2;
    private const int MaxCommitsPerFrame = 1;

    private sealed class DrawChunk
    {
        public MeshInstance3D? Node;
        public int Version = -1, Lod = -1, Month = -1, Verts, Tris;
        public uint NextChange = uint.MaxValue;
        /// <summary>The ground it was draped on: a flat tile's <see cref="ChunkGrid"/>, or the earth leaves' generation.</summary>
        public object? Grid;
        public bool Building;
    }

    private readonly record struct Built(Tile Tile, int Chunk, Vector3[] Verts, Color[] Colors, int[] Indices, int Version, int Lod, int Month,
        uint NextChange, object? Grid, int Generation, double WorkerMs);

    private readonly ConcurrentQueue<Built> _built = new();
    private int _inflight;
    private double _drawTimer;
    private ShaderMaterial? _material;
    private bool _draws;

    /// <summary>Drawing numbers, for the probes and <c>--farmstats</c>: chunks drawn, vertices, main-thread ms.</summary>
    public int DrawnChunks { get; private set; }
    public long DrawnVertices { get; private set; }
    public long DrawnTriangles { get; private set; }
    public int Rebuilds { get; private set; }
    public double MainMsTotal { get; private set; }
    public double WorkerMsTotal { get; private set; }
    public double MainMsMax { get; private set; }
    private double _statsTimer;
    private bool _stats;

    private void ReadyDraw()
    {
        _draws = DisplayServer.GetName() != "headless" || Array.IndexOf(OS.GetCmdlineUserArgs(), "--farmdraw") >= 0
            || Array.IndexOf(OS.GetCmdlineUserArgs(), "--farmcheck") >= 0;
        _stats = Array.IndexOf(OS.GetCmdlineUserArgs(), "--farmstats") >= 0;
        if (_draws) _material = StyleKit.Material(MaterialRole.Prop);
    }

    private void ProcessDraw(double delta)
    {
        if (!_draws) return;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < MaxCommitsPerFrame && _built.TryDequeue(out var b); i++) Commit(b);
        _drawTimer -= delta;
        if (_drawTimer <= 0)
        {
            _drawTimer = 0.25;
            Schedule();
        }
        double ms = watch.Elapsed.TotalMilliseconds;
        MainMsTotal += ms;
        if (ms > MainMsMax) MainMsMax = ms;
        if (_stats && (_statsTimer -= delta) <= 0)
        {
            _statsTimer = 10;
            GD.Print($"[farm] draw: {DrawnChunks} chunks, {DrawnVertices} vertices ({DrawnTriangles} triangles), {Rebuilds} builds, main {MainMsTotal:F1} ms in all (max frame {MainMsMax:F2} ms), worker {WorkerMsTotal:F0} ms");
        }
    }

    private void Schedule()
    {
        if (_chunks == null && _earth == null) return;
        var (fe, fn) = Focus();
        uint now = Now;
        while (_inflight < MaxInflight)
        {
            Tile? bestTile = null;
            int bestChunk = -1, bestLod = 0;
            double best = double.MaxValue;
            ChunkGrid? bestGrid = null;
            object? bestGround = null;
            foreach (var t in _tiles.Values)
            {
                if (t.Data == null) continue;
                // the flat world drapes on the tile's grid; the earth world on its leaves, sampled when built (#1729)
                var grid = _earth == null ? _chunks!.GridAt(t.Id) : null;
                object? ground = _earth != null ? _earthGround : grid;
                t.Draw ??= new DrawChunk?[FieldTile.ChunkCount];
                for (int k = 0; k < FieldTile.ChunkCount; k++)
                {
                    if (t.Data.ChunkCellCounts[k] == 0) continue;
                    // field cells count east and north from the tile's south-west corner: z = size - north
                    var (ce, cn) = t.Id.Global((k % FieldTile.ChunksPerSide + 0.5) * FieldTile.ChunkCells * FieldFormat.CellSize,
                        ChunkFormat.TileSizeM - (k / FieldTile.ChunksPerSide + 0.5) * FieldTile.ChunkCells * FieldFormat.CellSize);
                    double d = Math.Sqrt((ce - fe) * (ce - fe) + (cn - fn) * (cn - fn));
                    var dc = t.Draw[k];
                    if (d > DrawRadius + 60)
                    {
                        if (dc?.Node != null) { FreeNode(dc); }
                        continue;
                    }
                    if (d > DrawRadius || ground == null || d >= best) continue;
                    dc ??= t.Draw[k] = new DrawChunk();
                    if (dc.Building) continue;
                    // detail with a little hysteresis, so a walk along the edge does not flip it
                    int lod = dc.Lod == 0 ? (d < DetailRadius + 20 ? 0 : 1) : (d < DetailRadius - 20 ? 0 : 1);
                    bool stale = dc.Node == null && dc.Version == -1 || dc.Version != t.Cells.ChunkVersions[k] || dc.Lod != lod
                        || dc.Grid != ground || dc.Month != _month || now >= dc.NextChange;
                    if (!stale) continue;
                    best = d;
                    bestTile = t;
                    bestChunk = k;
                    bestLod = lod;
                    bestGrid = grid;
                    bestGround = ground;
                }
            }
            if (bestTile == null) return;
            // the leaves under it not all there yet: asked again at the next schedule
            if (!StartBuild(bestTile, bestChunk, bestLod, bestGrid, bestGround!, now)) return;
        }
    }

    private bool StartBuild(Tile t, int k, int lod, ChunkGrid? grid, object ground, uint now)
    {
        var data = t.Data!;
        int c0 = k % FieldTile.ChunksPerSide * FieldTile.ChunkCells, r0 = k / FieldTile.ChunksPerSide * FieldTile.ChunkCells;
        var (e0, n0) = t.Id.Global(c0 * FieldFormat.CellSize, ChunkFormat.TileSizeM - r0 * FieldFormat.CellSize);
        Func<double, double, double> height;
        if (grid != null) height = grid.SampleMeshHeight;
        else if (EarthCorners(t.Id, e0, n0) is { } corners) height = corners;
        else return false;
        var dc = t.Draw![k]!;
        dc.Building = true;
        _inflight++;
        var looks = new CellLook[FieldMeshBuilder.LookSide * FieldMeshBuilder.LookSide];
        uint next = uint.MaxValue;
        for (int r = -FieldMeshBuilder.Ring; r < FieldTile.ChunkCells + FieldMeshBuilder.Ring; r++)
            for (int c = -FieldMeshBuilder.Ring; c < FieldTile.ChunkCells + FieldMeshBuilder.Ring; c++)
            {
                int col = c0 + c, row = r0 + r;
                if (col < 0 || row < 0 || col >= FieldFormat.CellsPerSide || row >= FieldFormat.CellsPerSide) continue;
                int cell = FieldFormat.CellIndex(col, row);
                var fieldCrop = data.CropAt(cell);
                if (fieldCrop == CropKind.None) continue;
                var stored = t.Cells.Get(cell);
                var stage = FarmRules.StageOf(fieldCrop, stored, _month, now, out float growth, out var crop);
                float q = MathF.Floor(growth * 10f) / 10f;
                if (stored is { Stage: FieldStage.Sown or FieldStage.Mown } s && growth < 1f)
                {
                    double total = s.Stage == FieldStage.Mown ? FarmTables.GrowSeconds(CropKind.Meadow)
                        : FarmTables.GrowSeconds(s.Crop) * (s.Fertilised ? FarmTables.FertilisedGrowth : 1f);
                    uint at = (uint)Math.Ceiling(s.Since + total * (q + 0.1f) + 0.5);
                    if (at < next) next = at;
                }
                looks[FieldMeshBuilder.LookIndex(c, r)] = new CellLook { Stage = stage, Crop = crop, Growth = q, Owner = data.Owner[cell] };
            }
        int version = t.Cells.ChunkVersions[k];
        int month = _month;
        int generation = _generation;
        Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            Vector3[] verts = Array.Empty<Vector3>();
            Color[] colors = Array.Empty<Color>();
            int[] indices = Array.Empty<int>();
            try
            {
                var b = new FieldMeshBuilder(looks, data, c0, r0, height, e0, n0, lod);
                b.Build();
                verts = b.Verts.ToArray();
                colors = b.Colors.ToArray();
                indices = b.Indices.ToArray();
            }
            catch (Exception e) { GD.PushWarning($"[farm] building {t.Id} #{k}: {e.Message}"); }
            _built.Enqueue(new Built(t, k, verts, colors, indices, version, lod, month, next, ground, generation, watch.Elapsed.TotalMilliseconds));
        });
        return true;
    }

    /// <summary>
    /// The earth world's ground under a chunk's cell corners, in the tile's frame (#1729): sampled here on the main thread
    /// (the leaves' height query is main-thread only), at exactly the corners <see cref="FieldMeshBuilder.Build"/> asks
    /// for, so the worker reads them back. Null while a corner is off the loaded leaves.
    /// </summary>
    private Func<double, double, double>? EarthCorners(TileId id, double e0, double n0)
    {
        const int side = FieldMeshBuilder.Side + 1;
        const float cs = FieldFormat.CellSize;
        var frame = _origin.TileTransform(id);
        var back = frame.AffineInverse();
        var corners = new float[side * side];
        for (int r = 0; r < side; r++)
            for (int c = 0; c < side; c++)
            {
                var (x, z) = id.Local(e0 + c * cs, n0 + r * cs);
                var p = frame * new Vector3((float)x, 0f, (float)z);
                if (!_earth!.TryGetHeight(p, out float h)) return null;
                corners[r * side + c] = (back * new Vector3(p.X, h, p.Z)).Y;
            }
        return (e, n) =>
        {
            int c = Math.Clamp((int)Math.Round((e - e0) / cs), 0, side - 1), r = Math.Clamp((int)Math.Round((n - n0) / cs), 0, side - 1);
            return corners[r * side + c];
        };
    }

    private void Commit(Built b)
    {
        _inflight--;
        WorkerMsTotal += b.WorkerMs;
        if (b.Generation != _generation || !_tiles.TryGetValue(b.Tile.Id, out var t) || t != b.Tile || t.Draw?[b.Chunk] is not { } dc) return;
        dc.Building = false;
        dc.Version = b.Version;
        dc.Lod = b.Lod;
        dc.Month = b.Month;
        dc.NextChange = b.NextChange;
        dc.Grid = b.Grid;
        Rebuilds++;
        if (b.Verts.Length == 0)
        {
            // nothing to draw (untouched grass): no node, but this version is done
            FreeNode(dc);
            dc.Version = b.Version;
            dc.Lod = b.Lod;
            return;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = b.Verts;
        arrays[(int)Mesh.ArrayType.Color] = b.Colors;
        arrays[(int)Mesh.ArrayType.Index] = b.Indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, _material);
        if (dc.Node == null)
        {
            dc.Node = new MeshInstance3D { Name = $"F{t.Id.E}_{t.Id.N}_{b.Chunk}", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(dc.Node);
            DrawnChunks++;
        }
        else { DrawnVertices -= dc.Verts; DrawnTriangles -= dc.Tris; }
        int c0 = b.Chunk % FieldTile.ChunksPerSide * FieldTile.ChunkCells, r0 = b.Chunk / FieldTile.ChunksPerSide * FieldTile.ChunkCells;
        var corner = new Vector3(c0 * FieldFormat.CellSize, 0, (float)ChunkFormat.TileSizeM - r0 * FieldFormat.CellSize);
        // on the earth world the tile's frame turns too (true north, the curve): the chunk takes the whole of it
        if (_earth != null) dc.Node.Transform = _origin.TileTransform(t.Id) * new Transform3D(Basis.Identity, corner);
        else dc.Node.Position = _origin.TileToWorld(t.Id, corner);
        dc.Node.Mesh = mesh;
        dc.Verts = b.Verts.Length;
        dc.Tris = b.Indices.Length / 3;
        DrawnTriangles += dc.Tris;
        DrawnVertices += dc.Verts;
    }

    private void FreeNode(DrawChunk dc)
    {
        if (dc.Node == null) return;
        DrawnVertices -= dc.Verts;
        dc.Verts = 0;
        DrawnTriangles -= dc.Tris;
        dc.Tris = 0;
        DrawnChunks--;
        dc.Node.QueueFree();
        dc.Node = null;
        dc.Version = -1;
        dc.Lod = -1;
    }

    private void FreeDraw(Tile t)
    {
        if (t.Draw == null) return;
        foreach (var dc in t.Draw)
            if (dc != null) FreeNode(dc);
        t.Draw = null;
    }
}
