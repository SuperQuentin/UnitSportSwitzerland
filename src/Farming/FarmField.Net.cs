using Godot;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Farming;

// The link to the server (#494, docs/notes/farming/field-state-net.md):
//  client -> server  Subscribe(e, n) / Unsubscribe(e, n): the tiles it holds;
//                    Work(e, n, tool, seed, seq, cells[]): its strokes, batched every FlushSeconds;
//  server -> client  Cells(e, n, month, packed[], snapshot, ack): a subscribe's snapshot, the
//                    answer to a Work (every cell sent, as the server has it, ack = its seq), and
//                    other peers' changes (ack 0). Packed records: FieldCells.Pack.
public partial class FarmField
{
    private sealed class Batch
    {
        public FarmTool Tool;
        public CropKind Seed;
        public int Seq;
        public readonly List<int> Cells = new();
    }

    private readonly Dictionary<TileId, Batch> _outbox = new();
    private int _seq;

    /// <summary>Client: a predicted cell goes in its tile's batch (a new batch when the tool or seed changes).</summary>
    private void Queue(Tile t, FarmTool tool, CropKind seed, int cell)
    {
        if (!_outbox.TryGetValue(t.Id, out var batch)) _outbox[t.Id] = batch = new Batch();
        if (batch.Cells.Count > 0 && (batch.Tool != tool || batch.Seed != seed)) Flush(t.Id, batch);
        if (batch.Cells.Count == 0) { batch.Tool = tool; batch.Seed = seed; batch.Seq = ++_seq; }
        batch.Cells.Add(cell);
        t.Pending[cell] = batch.Seq;
    }

    private void FlushAll()
    {
        foreach (var (id, batch) in _outbox)
            if (batch.Cells.Count > 0) Flush(id, batch);
    }

    private void Flush(TileId id, Batch batch)
    {
        if (NetLink.Online(this))
            RpcId(1, MethodName.Work, id.E, id.N, (int)batch.Tool, (int)batch.Seed, batch.Seq, batch.Cells.ToArray());
        batch.Cells.Clear();
    }

    // ---- server --------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Subscribe(int e, int n)
    {
        if (!_dedicated) return;
        long peer = Multiplayer.GetRemoteSenderId();
        var t = Ensure(new TileId(e, n));
        t.Subs.Add(peer);
        RpcId(peer, MethodName.Cells, e, n, _month, t.Cells.PackAll(), true, 0);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Unsubscribe(int e, int n)
    {
        if (!_dedicated) return;
        var id = new TileId(e, n);
        if (!_tiles.TryGetValue(id, out var t)) return;
        t.Subs.Remove(Multiplayer.GetRemoteSenderId());
        ForgetIfIdle(t);
    }

    private void OnPeerGone(long peer)
    {
        if (!_dedicated) return;
        List<Tile>? idle = null;
        foreach (var t in _tiles.Values)
            if (t.Subs.Remove(peer) && t.Subs.Count == 0) (idle ??= new()).Add(t);
        if (idle != null) foreach (var t in idle) ForgetIfIdle(t);
    }

    /// <summary>Server: the field data of a tile nobody holds is dropped (its cells stay in the store).</summary>
    private void ForgetIfIdle(Tile t)
    {
        if (t.Subs.Count > 0 || t.Waiting.Count > 0) return;
        t.Cts.Cancel();
        _tiles.Remove(t.Id);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Work(int e, int n, int tool, int seed, int seq, int[] cells)
    {
        if (!_dedicated || cells.Length > FieldFormat.CellCount) return;
        long peer = Multiplayer.GetRemoteSenderId();
        var t = Ensure(new TileId(e, n));
        if (!t.Loaded) { t.Waiting.Add((peer, (FarmTool)tool, (CropKind)seed, seq, cells)); return; }
        Serve(t, peer, (FarmTool)tool, (CropKind)seed, seq, cells);
    }

    private void DrainWaiting(Tile t)
    {
        foreach (var w in t.Waiting) Serve(t, w.Peer, w.Tool, w.Seed, w.Seq, w.Cells);
        t.Waiting.Clear();
        ForgetIfIdle(t);
    }

    /// <summary>
    /// Server: works what a peer sent, in arrival order (that is the conflict rule), with loose
    /// checks: the peer stands near each cell and the tool can work it now. The sender gets every
    /// cell back as stored (its prediction is corrected), the other holders what changed.
    /// </summary>
    private void Serve(Tile t, long peer, FarmTool tool, CropKind seed, int seq, int[] cells)
    {
        var body = GetNodeOrNull<FootPlayer>("../Players/" + peer);
        var at = body?.Global;
        uint now = Now;
        var answer = new long[cells.Length];
        List<long>? changed = null;
        for (int i = 0; i < cells.Length; i++)
        {
            int cell = cells[i];
            if ((uint)cell >= FieldFormat.CellCount) { answer[i] = 0; continue; }
            var fieldCrop = t.Data?.CropAt(cell) ?? CropKind.None;
            bool near = false;
            if (at is { } g)
            {
                var (ce, cn) = FieldFormat.CellCentre(cell);
                double de = t.Id.MinE + ce - g.E, dn = t.Id.MinN + cn - g.N;
                near = de * de + dn * dn <= WorkReach * WorkReach;
            }
            if (near && fieldCrop != CropKind.None
                && FarmRules.Work(tool, fieldCrop, t.Cells.Get(cell), seed, _month, now) is { } after
                && t.Cells.Set(cell, after))
                (changed ??= new()).Add(FieldCells.Pack(cell, after));
            answer[i] = t.Cells.PackCell(cell);
        }
        if (changed != null)
        {
            _dirty.Add(t.Id);
            GD.Print($"[farm] peer {peer} {tool} {changed.Count}/{cells.Length} cells on {t.Id}");
        }
        RpcId(peer, MethodName.Cells, t.Id.E, t.Id.N, _month, answer, false, seq);
        if (changed == null) return;
        var packed = changed.ToArray();
        foreach (long other in t.Subs)
            if (other != peer) RpcId(other, MethodName.Cells, t.Id.E, t.Id.N, _month, packed, false, 0);
    }

    // ---- client --------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Cells(int e, int n, int month, long[] packed, bool snapshot, int ack)
    {
        if (_dedicated) return;
        if (month is >= 1 and <= 12 && (!_monthFromServer || month != _month))
        {
            _month = month;
            _monthFromServer = true;
        }
        if (!_tiles.TryGetValue(new TileId(e, n), out var t)) return;
        if (snapshot)
        {
            // what the server holds replaces what this peer had, except cells it still waits on
            var listed = new HashSet<int>();
            foreach (long v in packed)
            {
                var (cell, state) = FieldCells.Unpack(v);
                listed.Add(cell);
                if (!t.Pending.ContainsKey(cell)) t.Cells.Set(cell, state);
            }
            var stale = new List<int>();
            foreach (int cell in t.Cells.All.Keys)
                if (!listed.Contains(cell) && !t.Pending.ContainsKey(cell)) stale.Add(cell);
            foreach (int cell in stale) t.Cells.Set(cell, null);
            return;
        }
        foreach (long v in packed)
        {
            var (cell, state) = FieldCells.Unpack(v);
            if (t.Pending.TryGetValue(cell, out int waiting))
            {
                // a newer prediction of this cell is still on its way: its own answer settles it
                if (ack == 0 || waiting > ack) continue;
                t.Pending.Remove(cell);
            }
            t.Cells.Set(cell, state);
        }
    }
}

public partial class FarmField
{
    /// <summary>Client: predicted cells the server has not answered yet (probes).</summary>
    public int PendingCount
    {
        get
        {
            int n = 0;
            foreach (var t in _tiles.Values) n += t.Pending.Count;
            return n;
        }
    }
}
