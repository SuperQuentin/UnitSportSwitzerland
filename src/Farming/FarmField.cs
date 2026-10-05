using System.Collections.Concurrent;
using System.Text.Json;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Farming;

/// <summary>
/// The farmed ground on this peer (#494, <c>docs/notes/farming/</c>): field tiles near the players
/// (<see cref="FieldTile"/>, from <see cref="IChunkSource.LoadFieldsAsync"/>), the worked cells
/// (<see cref="FieldCells"/>), their drawing (<c>FarmField.Draw.cs</c>) and the link to the server
/// (<c>FarmField.Net.cs</c>). At <c>World/Farm</c> on the server and every client.
///
/// <para>
/// <b>Authority</b>: the server owns the cells (<c>user://farm/E_N.json</c>). A client predicts
/// what its own work does at once (<see cref="Sweep"/>), batches it to the server every
/// <see cref="FlushSeconds"/>, and takes the server's answer for every cell it sent. Offline this
/// peer is the server: the same store, saved the same way.
/// </para>
/// </summary>
public partial class FarmField : Node
{
    public const string NodeName = "Farm";

    public static FarmField? Instance { get; private set; }

    /// <summary>Seconds added to the farm clock (the debug fast-forward, <c>--farmcheck</c>). Authority only matters.</summary>
    public static double ClockSkew { get; set; }

    /// <summary>A client loads the field tiles whose edge is within this of the camera.</summary>
    public const double LoadRadius = 700;
    /// <summary>...and frees them past this.</summary>
    public const double FreeRadius = 1100;
    /// <summary>A client's strokes go to the server this often.</summary>
    public const double FlushSeconds = 0.2;
    /// <summary>Server: a peer works cells within this of where it is (loose: a moving machine, lag).</summary>
    public const double WorkReach = 90;

    private readonly IChunkSource? _source;
    private readonly WorldOrigin _origin;
    private readonly ChunkManager? _chunks;
    private readonly bool _dedicated;

    private sealed class Tile
    {
        public TileId Id;
        public FieldTile? Data;
        public FieldCells Cells = new();
        public bool Loading, Loaded;
        public CancellationTokenSource Cts = new();
        /// <summary>Client: cells predicted and not yet answered, with the batch they went in.</summary>
        public readonly Dictionary<int, int> Pending = new();
        /// <summary>Server: peers that hold this tile and want its changes.</summary>
        public readonly HashSet<long> Subs = new();
        /// <summary>Server: work that arrived before the field data.</summary>
        public readonly List<(long Peer, FarmTool Tool, CropKind Seed, int Seq, int[] Cells)> Waiting = new();
        public DrawChunk?[]? Draw;
        public double LastWanted;
    }

    private readonly Dictionary<TileId, Tile> _tiles = new();
    private readonly ConcurrentQueue<(Tile Tile, FieldTile? Data, int Generation)> _loaded = new();
    private int _generation;

    // authority: the stored cells per tile, loaded from disk on first use, saved in the background
    private readonly Dictionary<TileId, FieldCells> _store = new();
    private readonly HashSet<TileId> _dirty = new();
    private double _saveTimer;
    private string _storeDir = "";

    private int _month;
    private bool _monthFromServer;
    private bool? _wasOnline;
    private double _wantTimer, _flushTimer;

    // reused by every Sweep: no allocation per call
    private readonly List<long> _sweepKeys = new(64);

    public FarmField(IChunkSource? source, WorldOrigin origin, ChunkManager? chunks, bool dedicated)
    {
        _source = source;
        _origin = origin;
        _chunks = chunks;
        _dedicated = dedicated;
        Name = NodeName;
    }

    public FarmField() : this(null, WorldOrigin.SwissDefault(), null, false) { }

    public static FarmField Create(Node world, IChunkSource? source, WorldOrigin origin, ChunkManager? chunks, bool dedicated)
    {
        var f = new FarmField(source, origin, chunks, dedicated);
        world.AddChild(f);
        return f;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        foreach (var t in _tiles.Values) t.Cts.Cancel();
        SaveDirty();
    }

    public override void _Ready()
    {
        _storeDir = ProjectSettings.GlobalizePath("user://farm");
        _month = FarmRules.MonthFromArgs(OS.GetCmdlineUserArgs(), DateTime.Now);
        Multiplayer.PeerDisconnected += OnPeerGone;
        if (!_dedicated) ReadyDraw();
        GD.Print($"[farm] {(_dedicated ? "server" : "client")} ready, month {_month}");
    }

    /// <summary>This peer owns the cells: the server, or the game offline.</summary>
    private bool Authority => _dedicated || !NetLink.Online(this);

    /// <summary>The farm clock: server Unix seconds (<see cref="ClockSync.ServerUnixNow"/>) plus the debug skew.</summary>
    public static uint Now => (uint)Math.Max(0, ClockSync.ServerUnixNow + ClockSkew);

    /// <summary>The calendar month the fields show (the server's, once it said).</summary>
    public int Month => _month;

    public override void _Process(double delta)
    {
        // the loads finished on workers
        while (_loaded.TryDequeue(out var done))
        {
            if (done.Generation != _generation || !_tiles.TryGetValue(done.Tile.Id, out var t) || t != done.Tile) continue;
            t.Data = done.Data;
            t.Loading = false;
            t.Loaded = true;
            if (_dedicated) DrainWaiting(t);
        }

        if (_dedicated)
        {
            SaveTick(delta);
            return;
        }

        bool online = NetLink.Online(this);
        if (_wasOnline != online)
        {
            // offline -> online (or back): what this peer held belonged to the other authority
            if (_wasOnline != null) ResetTiles();
            _wasOnline = online;
            _monthFromServer = false;
            if (!online) _month = FarmRules.MonthFromArgs(OS.GetCmdlineUserArgs(), DateTime.Now);
        }

        _wantTimer -= delta;
        if (_wantTimer <= 0)
        {
            _wantTimer = 0.5;
            UpdateWanted(online);
        }
        if (online)
        {
            _flushTimer -= delta;
            if (_flushTimer <= 0) { _flushTimer = FlushSeconds; FlushAll(); }
        }
        else SaveTick(delta);
        ProcessDraw(delta);
    }

    // ---- tiles ----------------------------------------------------------------------------------

    /// <summary>Where the client cares about fields: the camera, else the origin.</summary>
    private (double E, double N) Focus()
    {
        var cam = GetViewport()?.GetCamera3D();
        return _origin.ToLv95(cam?.GlobalPosition ?? Vector3.Zero);
    }

    private void UpdateWanted(bool online)
    {
        if (_source == null) return;
        var (e, n) = Focus();
        double now = Time.GetTicksMsec() / 1000.0;
        var centre = TileId.FromLv95(e, n);
        for (int dn = -1; dn <= 1; dn++)
            for (int de = -1; de <= 1; de++)
            {
                var id = new TileId(centre.E + de, centre.N + dn);
                if (EdgeDistance(id, e, n) > LoadRadius) continue;
                var t = Ensure(id);
                t.LastWanted = now;
            }
        List<TileId>? gone = null;
        foreach (var (id, t) in _tiles)
            if (EdgeDistance(id, e, n) > FreeRadius && now - t.LastWanted > 5) (gone ??= new()).Add(id);
        if (gone != null)
            foreach (var id in gone) Free(id, online);
    }

    private static double EdgeDistance(TileId id, double e, double n)
    {
        double dx = Math.Max(0, Math.Max(id.MinE - e, e - (id.MinE + ChunkFormat.TileSizeM)));
        double dy = Math.Max(0, Math.Max(id.MinN - n, n - id.MaxN));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>The tile's record, its field data loading on a worker the first time.</summary>
    private Tile Ensure(TileId id)
    {
        if (_tiles.TryGetValue(id, out var t)) return t;
        t = new Tile { Id = id, LastWanted = Time.GetTicksMsec() / 1000.0 };
        if (Authority) t.Cells = Store(id);
        _tiles[id] = t;
        StartLoad(t);
        if (!_dedicated && NetLink.Online(this)) RpcId(1, MethodName.Subscribe, id.E, id.N);
        return t;
    }

    private void StartLoad(Tile t)
    {
        if (_source == null || t.Loading) return;
        t.Loading = true;
        var source = _source;
        var token = t.Cts.Token;
        int generation = _generation;
        Task.Run(async () =>
        {
            FieldTile? data = null;
            try
            {
                var fields = await source.LoadFieldsAsync(t.Id, token).ConfigureAwait(false);
                if (fields is { Count: > 0 } && !token.IsCancellationRequested) data = FieldTile.Build(t.Id, fields);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { GD.PushWarning($"[farm] fields {t.Id}: {e.Message}"); }
            _loaded.Enqueue((t, data, generation));
        }, token);
    }

    private void Free(TileId id, bool online)
    {
        if (!_tiles.Remove(id, out var t)) return;
        t.Cts.Cancel();
        FreeDraw(t);
        if (online && !_dedicated) RpcId(1, MethodName.Unsubscribe, id.E, id.N);
    }

    /// <summary>Drops every tile (the authority changed): they load again, from the new one.</summary>
    private void ResetTiles()
    {
        _generation++;
        foreach (var t in _tiles.Values) { t.Cts.Cancel(); FreeDraw(t); }
        _tiles.Clear();
        _outbox.Clear();
        _store.Clear();
        _dirty.Clear();
    }

    // ---- the store (authority) --------------------------------------------------------------------

    private string PathFor(TileId id) => Path.Combine(_storeDir, $"{id.E}_{id.N}.json");

    /// <summary>The stored cells of a tile, read from disk the first time (a few KB at most).</summary>
    private FieldCells Store(TileId id)
    {
        if (_store.TryGetValue(id, out var cells)) return cells;
        cells = new FieldCells();
        try
        {
            string path = PathFor(id);
            if (File.Exists(path) && JsonSerializer.Deserialize<FieldCells.File>(File.ReadAllText(path)) is { } file
                && file.Version == FieldCells.File.CurrentVersion)
                cells.Apply(file.Cells);
        }
        catch (Exception e) { GD.PushWarning($"[farm] reading {id}: {e.Message}"); }
        _store[id] = cells;
        return cells;
    }

    private void SaveTick(double delta)
    {
        _saveTimer -= delta;
        if (_saveTimer > 0) return;
        _saveTimer = 2.0;
        SaveDirty();
    }

    private void SaveDirty()
    {
        if (_dirty.Count == 0) return;
        foreach (var id in _dirty)
        {
            if (!_store.TryGetValue(id, out var cells)) continue;
            string path = PathFor(id);
            try
            {
                JsonStore.SaveAsync(path, new FieldCells.File { Cells = cells.PackAll() },
                    onError: e => GD.PushWarning($"[farm] could not write {path}: {e.Message}"));
            }
            catch (Exception e) { GD.PushWarning($"[farm] saving {id}: {e.Message}"); }
        }
        _dirty.Clear();
    }

    /// <summary>For probes: the file the authority writes for a tile, after the queued saves landed.</summary>
    public string FlushedPath(TileId id)
    {
        SaveDirty();
        SaveQueue.Flush();
        return PathFor(id);
    }

    // ---- reading and working ----------------------------------------------------------------------

    private bool TryCell(double e, double n, out Tile tile, out int cell)
    {
        var (id, c) = FieldFormat.CellAt(e, n);
        cell = c;
        if (_tiles.TryGetValue(id, out tile!) && tile.Data != null) return true;
        tile = null!;
        return false;
    }

    /// <summary>The cell under a world point now, or null off every field (or before its tile is loaded).</summary>
    public FieldCellView? CellAt(Vector3 world)
    {
        var (e, n) = _origin.ToLv95(world);
        return CellAtLv95(e, n);
    }

    public FieldCellView? CellAtLv95(double e, double n)
    {
        if (!TryCell(e, n, out var t, out int cell)) return null;
        var fieldCrop = t.Data!.CropAt(cell);
        if (fieldCrop == CropKind.None) return null;
        var stage = FarmRules.StageOf(fieldCrop, t.Cells.Get(cell), _month, Now, out float growth, out var crop);
        return new FieldCellView(t.Data.FieldAt(cell), fieldCrop, crop, stage, growth);
    }

    /// <summary>
    /// Works the strip from <paramref name="a"/> to <paramref name="b"/> (<see cref="FarmWork.Sweep"/>):
    /// applied here at once, sent to the server when online. No allocation per call.
    /// </summary>
    public FarmStroke Sweep(FarmTool tool, Vector3 a, Vector3 b, float width, CropKind seed)
    {
        if (tool == FarmTool.None) return default;
        var (ae, an) = _origin.ToLv95(a);
        var (be, bn) = _origin.ToLv95(b);
        return SweepLv95(tool, ae, an, be, bn, width, seed);
    }

    public FarmStroke SweepLv95(FarmTool tool, double ae, double an, double be, double bn, float width, CropKind seed)
    {
        _sweepKeys.Clear();
        FarmRules.CellsInStrip(ae, an, be, bn, width, _sweepKeys);
        bool online = !_dedicated && NetLink.Online(this);
        uint now = Now;
        int cells = 0;
        float units = 0;
        CropKind first = CropKind.None;
        for (int i = 0; i < _sweepKeys.Count; i++)
        {
            var (id, cell) = FarmRules.Unkey(_sweepKeys[i]);
            if (!_tiles.TryGetValue(id, out var t) || t.Data == null) continue;
            var fieldCrop = t.Data.CropAt(cell);
            if (fieldCrop == CropKind.None) continue;
            var stored = t.Cells.Get(cell);
            FarmRules.StageOf(fieldCrop, stored, _month, now, out _, out var before);
            if (FarmRules.Work(tool, fieldCrop, stored, seed, _month, now) is not { } after) continue;
            t.Cells.Set(cell, after);
            cells++;
            units += FarmRules.UnitsPerCell(tool, before);
            if (first == CropKind.None) first = tool == FarmTool.Sow ? seed : before;
            if (online) Queue(t, tool, seed, cell);
            else if (Authority) _dirty.Add(id);
        }
        return new FarmStroke(cells, first, units);
    }

    /// <summary>The shown stage of every cell of a field tile, for probes: (stage, count).</summary>
    public Dictionary<FieldStage, int> Census(TileId id, uint fieldId)
    {
        var counts = new Dictionary<FieldStage, int>();
        if (!_tiles.TryGetValue(id, out var t) || t.Data == null) return counts;
        uint now = Now;
        for (int c = 0; c < FieldFormat.CellCount; c++)
        {
            if (t.Data.FieldAt(c) != fieldId) continue;
            var s = FarmRules.StageOf(t.Data.CropAt(c), t.Cells.Get(c), _month, now, out _, out _);
            counts[s] = counts.GetValueOrDefault(s) + 1;
        }
        return counts;
    }

    /// <summary>Whether a tile's field data is here (probes wait for it).</summary>
    public bool HasFields(TileId id) => _tiles.TryGetValue(id, out var t) && t.Data != null;

    /// <summary>How many cells of a tile are stored on this peer.</summary>
    public int StoredCount(TileId id) => _tiles.TryGetValue(id, out var t) ? t.Cells.Count : 0;
}
