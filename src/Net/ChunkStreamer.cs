using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Net;

/// <summary>
/// Streams generated terrain files from the server to clients that do not have them.
///
/// <para>
/// One class runs on both sides at <c>World/ChunkStream</c> — the path must match, because
/// Godot routes RPCs by node path — and branches on which half was constructed. The server
/// reads raw files and meters them out; the client asks for what it is missing and hands the
/// bytes to <see cref="Terrain.NetworkChunkSource"/>, which caches them under the ordinary
/// filename so the ordinary decoders read them back.
/// </para>
///
/// <para>
/// Transfers are metered rather than blasted. A single dense tile is ~2.6 MB, and a player
/// walking into unexplored terrain asks for several at once; sending them as fast as the
/// socket allows would bury position updates and make everyone rubber-band. The server sends
/// at most <see cref="BytesPerSecondPerPeer"/> to each client, on a channel of its own.
/// </para>
/// </summary>
/// <summary>
/// Outcome of one asset request.
/// </summary>
/// <param name="Data">The raw file, or null when it did not arrive.</param>
/// <param name="PermanentlyMissing">
/// True only when the server said it does not have the file. A transfer refused for
/// backpressure, a timeout or a failed integrity check leaves this false, so the caller
/// asks again later instead of writing the tile off for the session — conflating the two
/// is how a busy server permanently blanks terrain it actually has.
/// </param>
public readonly record struct AssetResult(byte[]? Data, bool PermanentlyMissing);

public partial class ChunkStreamer : Node
{
    /// <summary>Node name, which must match on server and client for RPC routing.</summary>
    public const string NodeName = "ChunkStream";

    /// <summary>Server-side bandwidth ceiling per client.</summary>
    public int BytesPerSecondPerPeer { get; set; } = 3 * 1024 * 1024;

    /// <summary>
    /// Transfers one client may have queued. This is a memory safety valve, not the pacing
    /// mechanism — bandwidth is what actually meters delivery — so it is set well above what
    /// a client arriving in new terrain asks for in one burst.
    /// </summary>
    public int MaxQueuedPerPeer { get; set; } = 96;

    /// <summary>How long a client waits for a transfer before giving up on it.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // ---- server state ----------------------------------------------------------------

    private string? _serveDirectory;
    private readonly Dictionary<long, PeerQueue> _queues = new();

    // ---- client state ----------------------------------------------------------------

    private readonly Dictionary<uint, PendingRequest> _pending = new();
    private uint _nextRequestId = 1;

    /// <summary>Bytes this client has received since it connected.</summary>
    public long BytesReceived { get; private set; }

    /// <summary>Files this client has received since it connected.</summary>
    public int FilesReceived { get; private set; }

    /// <summary>Raised on the client when a transfer completes, for progress display.</summary>
    public event Action<AssetKind, TileId, int>? AssetReceived;

    /// <summary>Builds the server half, serving raw files out of a directory.</summary>
    public static ChunkStreamer CreateServer(string chunkDirectory) => new()
    {
        Name = NodeName,
        _serveDirectory = chunkDirectory,
    };

    /// <summary>Builds the client half.</summary>
    public static ChunkStreamer CreateClient() => new() { Name = NodeName };

    private bool IsServing => _serveDirectory is not null;

    // ---- client API -------------------------------------------------------------------

    /// <summary>
    /// Asks the server for one file. Returns its raw bytes, or null when the server does not
    /// have it either — which the caller treats exactly like a missing local file.
    ///
    /// <para>
    /// <b>Called from a worker thread.</b> <see cref="ChunkManager"/> loads and meshes tiles
    /// on the thread pool, so both the connectivity check and the RPC are deferred onto the
    /// main thread. Issuing an RPC from a worker does not throw — it simply never arrives,
    /// which is a genuinely difficult failure to spot, because the request just disappears
    /// and the tile stays blank forever.
    /// </para>
    /// </summary>
    public Task<AssetResult> FetchAsync(AssetKind kind, TileId id, CancellationToken ct = default)
    {
        if (IsServing) return Task.FromResult(new AssetResult(null, true));

        uint requestId;
        PendingRequest pending;
        lock (_pending)
        {
            requestId = _nextRequestId++;
            pending = new PendingRequest(kind, id, DateTime.UtcNow);
            _pending[requestId] = pending;
        }

        // Cancellation and timeout both resolve the task rather than leaving a caller hanging
        // on a chunk the server silently dropped.
        ct.Register(() => Fail(requestId, "cancelled", permanent: false));

        Callable.From(() =>
        {
            // Connectivity is only knowable on the main thread, so it is checked here rather
            // than in the caller.
            if (!Multiplayer.HasMultiplayerPeer()
                || Multiplayer.MultiplayerPeer.GetConnectionStatus()
                   != MultiplayerPeer.ConnectionStatus.Connected
                || Multiplayer.GetUniqueId() == 1)
            {
                Fail(requestId, "not connected to a server", permanent: false);
                return;
            }

            RpcId(1, MethodName.RequestAsset, requestId, (int)kind, id.E, id.N);
        }).CallDeferred();

        return pending.Completion.Task;
    }

    private void Fail(uint requestId, string why, bool permanent)
    {
        PendingRequest? pending;
        lock (_pending)
        {
            if (!_pending.Remove(requestId, out pending)) return;
        }

        GD.Print($"[stream] request {requestId} {pending.Kind} {pending.Tile} {why}");
        pending.Completion.TrySetResult(new AssetResult(null, permanent));
    }

    // ---- client -> server --------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable,
        TransferChannel = AssetStream.Channel)]
    private void RequestAsset(uint requestId, int kind, int tileE, int tileN)
    {
        if (!IsServing) return;

        long peer = Multiplayer.GetRemoteSenderId();
        var assetKind = (AssetKind)kind;
        var tile = new TileId(tileE, tileN);

        if (!Enum.IsDefined(assetKind))
        {
            RpcId(peer, MethodName.AssetMissing, requestId, true);
            return;
        }

        var queue = GetQueue(peer);
        if (queue.Transfers.Count >= MaxQueuedPerPeer)
        {
            // Refusing is better than growing an unbounded queue — but it must be refused as
            // "busy", not "absent", or the client writes the tile off for the whole session.
            RpcId(peer, MethodName.AssetBusy, requestId);
            return;
        }

        string path = System.IO.Path.Combine(
            _serveDirectory!, AssetStream.FileNameFor(assetKind, tile));

        byte[] payload;
        try
        {
            if (!System.IO.File.Exists(path))
            {
                RpcId(peer, MethodName.AssetMissing, requestId, true);
                return;
            }
            payload = System.IO.File.ReadAllBytes(path);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[stream] cannot read {path}: {e.Message}");
            RpcId(peer, MethodName.AssetBusy, requestId);
            return;
        }

        uint crc = AssetStream.Crc32(payload);
        int rawLength = payload.Length;

        byte[] wire = payload;
        bool compressed = false;
        if (!AssetStream.IsAlreadyCompressed(assetKind)
            && AssetStream.TryCompress(payload) is { } smaller)
        {
            wire = smaller;
            compressed = true;
        }

        RpcId(peer, MethodName.BeginAsset, requestId, rawLength, wire.Length, crc, compressed);
        queue.Transfers.Enqueue(new Transfer(requestId, wire));
    }

    // ---- server -> client --------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable,
        TransferChannel = AssetStream.Channel)]
    private void BeginAsset(uint requestId, int rawLength, int wireLength, uint crc, bool compressed)
    {
        lock (_pending)
        {
            if (!_pending.TryGetValue(requestId, out var pending)) return;
            pending.Begin(rawLength, wireLength, crc, compressed);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable,
        TransferChannel = AssetStream.Channel)]
    private void AssetFragment(uint requestId, byte[] data)
    {
        PendingRequest? pending;
        lock (_pending)
        {
            if (!_pending.TryGetValue(requestId, out pending)) return;
            if (!pending.Append(data)) return;      // not the last fragment yet
            _pending.Remove(requestId);
        }

        byte[]? result = pending.Finish();
        if (result is null)
        {
            GD.PushWarning(
                $"[stream] {pending.Kind} {pending.Tile} failed its integrity check; discarded");
            // A corrupt payload is not the server saying "absent", so allow a retry.
            pending.Completion.TrySetResult(new AssetResult(null, false));
            return;
        }

        BytesReceived += result.Length;
        FilesReceived++;
        AssetReceived?.Invoke(pending.Kind, pending.Tile, result.Length);
        pending.Completion.TrySetResult(new AssetResult(result, false));
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable,
        TransferChannel = AssetStream.Channel)]
    private void AssetMissing(uint requestId, bool permanent) =>
        Fail(requestId, "not available on the server", permanent);

    /// <summary>The server has this file but is already saturated; ask again later.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
        TransferMode = MultiplayerPeer.TransferModeEnum.Reliable,
        TransferChannel = AssetStream.Channel)]
    private void AssetBusy(uint requestId) =>
        Fail(requestId, "server busy, will retry", permanent: false);

    // ---- server pump --------------------------------------------------------------------

    private PeerQueue GetQueue(long peer)
    {
        if (!_queues.TryGetValue(peer, out var queue))
            _queues[peer] = queue = new PeerQueue();
        return queue;
    }

    /// <summary>Drops a disconnected peer's queued transfers.</summary>
    public void ForgetPeer(long peer) => _queues.Remove(peer);

    public override void _Process(double delta)
    {
        if (IsServing)
        {
            PumpServer(delta);
            return;
        }

        ExpireClientRequests();
    }

    private void PumpServer(double delta)
    {
        // Each peer gets its own budget, so one client pulling a city does not starve the
        // others. Budget carries over between frames but is capped, or a long pause would
        // bank enough credit to burst megabytes in a single frame.
        int perFrame = (int)(BytesPerSecondPerPeer * delta);

        foreach (var (peer, queue) in _queues)
        {
            queue.Budget = Math.Min(queue.Budget + perFrame, BytesPerSecondPerPeer / 2);

            while (queue.Budget > 0 && queue.Transfers.Count > 0)
            {
                var transfer = queue.Transfers.Peek();
                int remaining = transfer.Payload.Length - transfer.Offset;
                int size = Math.Min(AssetStream.FragmentBytes, remaining);

                var slice = new byte[size];
                Array.Copy(transfer.Payload, transfer.Offset, slice, 0, size);
                RpcId(peer, MethodName.AssetFragment, transfer.RequestId, slice);

                transfer.Offset += size;
                queue.Budget -= size;

                if (transfer.Offset >= transfer.Payload.Length) queue.Transfers.Dequeue();
            }
        }
    }

    private void ExpireClientRequests()
    {
        if (_pending.Count == 0) return;

        uint[] expired;
        lock (_pending)
        {
            var now = DateTime.UtcNow;
            expired = _pending
                .Where(kv => now - kv.Value.StartedAt > RequestTimeout)
                .Select(kv => kv.Key)
                .ToArray();
        }

        foreach (uint id in expired) Fail(id, "timed out", permanent: false);
    }

    // ---- state holders --------------------------------------------------------------------

    private sealed class PeerQueue
    {
        public Queue<Transfer> Transfers { get; } = new();
        public int Budget { get; set; }
    }

    private sealed class Transfer(uint requestId, byte[] payload)
    {
        public uint RequestId { get; } = requestId;
        public byte[] Payload { get; } = payload;
        public int Offset { get; set; }
    }

    private sealed class PendingRequest(AssetKind kind, TileId tile, DateTime startedAt)
    {
        private byte[]? _buffer;
        private int _filled;
        private int _rawLength;
        private uint _crc;
        private bool _compressed;

        public AssetKind Kind { get; } = kind;
        public TileId Tile { get; } = tile;
        public DateTime StartedAt { get; } = startedAt;

        public TaskCompletionSource<AssetResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Begin(int rawLength, int wireLength, uint crc, bool compressed)
        {
            _buffer = new byte[wireLength];
            _filled = 0;
            _rawLength = rawLength;
            _crc = crc;
            _compressed = compressed;
        }

        /// <summary>Appends a fragment. True once the last one has arrived.</summary>
        public bool Append(byte[] data)
        {
            // A fragment before its header means the two RPCs were reordered, which the
            // reliable ordered channel makes impossible — but treat it as fatal rather than
            // writing past the end of a null buffer.
            if (_buffer is null) return false;
            if (_filled + data.Length > _buffer.Length) return false;

            Array.Copy(data, 0, _buffer, _filled, data.Length);
            _filled += data.Length;
            return _filled >= _buffer.Length;
        }

        /// <summary>Decompresses and verifies. Null when the payload did not survive.</summary>
        public byte[]? Finish()
        {
            if (_buffer is null) return null;

            byte[] raw;
            try
            {
                raw = _compressed ? AssetStream.Decompress(_buffer, _rawLength) : _buffer;
            }
            catch (Exception e)
            {
                GD.PushWarning($"[stream] {Kind} {Tile} failed to decompress: {e.Message}");
                return null;
            }

            if (raw.Length != _rawLength) return null;
            return AssetStream.Crc32(raw) == _crc ? raw : null;
        }
    }
}
