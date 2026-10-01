using Godot;

namespace UnitSport.Items;

/// <summary>
/// Moves Polaroid prints between machines, through the server, at <c>World/PhotoTransfer</c> on the
/// server and every client (RPCs route by node path).
///
/// <para>
/// A placed photo only carries its id (<see cref="PlacedObject.Payload"/>); the image is sent
/// separately. The owner <see cref="Upload"/>s the JPEG before asking to place it, in
/// <see cref="ChunkSize"/> chunks, at most <see cref="MaxBytes"/>; the server checks the bytes hash
/// to the id and keeps them in <c>user://placed/photos</c>. A peer that draws a photo it does not
/// have <see cref="Ensure"/>s it: the server sends the chunks back, or remembers the request until
/// the upload arrives (it may still be on its way). Received prints go to
/// <c>user://photo_cache</c> and raise <see cref="Arrived"/>. Late joiners go through the same
/// path from the join snapshot. Offline there is nothing to move: every print is local.
/// </para>
/// </summary>
public partial class PhotoTransfer : Node
{
    public const string NodeName = "PhotoTransfer";
    public const int ChunkSize = 16 * 1024;
    public const int MaxBytes = 64 * 1024;
    private const int MaxChunks = MaxBytes / ChunkSize;

    public static PhotoTransfer? Instance { get; private set; }

    /// <summary>Client: a print is now on this machine (its hash checked). The id.</summary>
    public static event Action<string>? Arrived;
    /// <summary>Drops the subscribers a world left behind when it was freed (<see cref="Core.WorldStatics"/>).</summary>
    internal static void ResetEvents() => Arrived = null;

    private bool _server;
    private readonly HashSet<string> _asked = new();
    private readonly Dictionary<(long Peer, string Id), byte[]?[]> _incoming = new();
    private readonly Dictionary<string, HashSet<long>> _waiting = new();

    public static PhotoTransfer Create(Node world, bool server)
    {
        var t = new PhotoTransfer { Name = NodeName, _server = server };
        world.AddChild(t);
        if (!server) Instance = t;
        return t;
    }

    public override void _Ready()
    {
        if (_server) Multiplayer.PeerDisconnected += Forget;
        else Multiplayer.ServerDisconnected += () => _asked.Clear();
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => Multiplayer.MultiplayerPeer is { } peer and not OfflineMultiplayerPeer
        && peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    // ---- client API -----------------------------------------------------------------------------

    /// <summary>Sends a print of ours to the server (before placing it). False if there is nothing to send.</summary>
    public bool Upload(string id)
    {
        if (!Online) return true;
        if (PhotoStore.Bytes(id) is not { } bytes || bytes.Length > MaxBytes || bytes.Length == 0)
        {
            GD.PushWarning($"[photo] cannot upload {id}: {(PhotoStore.Has(id) ? "too big" : "not here")}");
            return false;
        }
        int total = (bytes.Length + ChunkSize - 1) / ChunkSize;
        for (int i = 0; i < total; i++)
        {
            int from = i * ChunkSize;
            RpcId(1, MethodName.UploadChunk, id, i, total, bytes[from..Math.Min(bytes.Length, from + ChunkSize)]);
        }
        GD.Print($"[photo] uploading {id}: {bytes.Length} bytes in {total} chunk(s)");
        return true;
    }

    /// <summary>Asks the server for a print this machine lacks, once. <see cref="Arrived"/> says when it is here.</summary>
    public void Ensure(string id)
    {
        if (!PhotoStore.IsValidId(id) || PhotoStore.Has(id) || !Online || !_asked.Add(id)) return;
        RpcId(1, MethodName.RequestPhoto, id);
    }

    // ---- wire -----------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void UploadChunk(string id, int index, int total, byte[] data)
    {
        if (!_server) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (Receive(peer, id, index, total, data) is not { } jpeg) return;
        string path = Path.Combine(PhotoStore.ServerDir, id + ".jpg");
        try
        {
            Directory.CreateDirectory(PhotoStore.ServerDir);
            if (!File.Exists(path)) File.WriteAllBytes(path, jpeg);
        }
        catch (Exception ex)
        {
            GD.PushError($"[photo] storing {id}: {ex.Message}");
            return;
        }
        GD.Print($"[photo] stored {id} ({jpeg.Length} bytes) from peer {peer}");
        if (_waiting.Remove(id, out var peers))
            foreach (long p in peers)
                if (Multiplayer.GetPeers().Contains((int)p)) Serve(p, id, jpeg);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPhoto(string id)
    {
        if (!_server || !PhotoStore.IsValidId(id)) return;
        long peer = Multiplayer.GetRemoteSenderId();
        string path = Path.Combine(PhotoStore.ServerDir, id + ".jpg");
        if (File.Exists(path))
        {
            Serve(peer, id, File.ReadAllBytes(path));
            return;
        }
        // not uploaded (yet): answered when it is
        if (!_waiting.TryGetValue(id, out var set)) _waiting[id] = set = new HashSet<long>();
        if (set.Count < 64) set.Add(peer);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DeliverChunk(string id, int index, int total, byte[] data)
    {
        if (_server) return;
        if (Receive(1, id, index, total, data) is not { } jpeg) return;
        if (!PhotoStore.StoreFetched(id, jpeg))
        {
            GD.PushWarning($"[photo] {id}: bytes do not match the id");
            return;
        }
        GD.Print($"[photo] received {id} ({jpeg.Length} bytes)");
        Arrived?.Invoke(id);
    }

    private void Serve(long peer, string id, byte[] jpeg)
    {
        int total = (jpeg.Length + ChunkSize - 1) / ChunkSize;
        for (int i = 0; i < total; i++)
        {
            int from = i * ChunkSize;
            RpcId(peer, MethodName.DeliverChunk, id, i, total, jpeg[from..Math.Min(jpeg.Length, from + ChunkSize)]);
        }
    }

    /// <summary>One chunk in; the whole file once every chunk is here and it hashes to its id, else null.</summary>
    private byte[]? Receive(long peer, string id, int index, int total, byte[] data)
    {
        if (!PhotoStore.IsValidId(id) || total < 1 || total > MaxChunks || index < 0 || index >= total
            || data.Length == 0 || data.Length > ChunkSize)
        {
            GD.PushWarning($"[photo] bad chunk {index}/{total} of '{id}' from {peer}");
            return null;
        }
        var key = (peer, id);
        if (!_incoming.TryGetValue(key, out var parts) || parts.Length != total)
            _incoming[key] = parts = new byte[]?[total];
        parts[index] = data;
        if (parts.Any(p => p == null)) return null;
        _incoming.Remove(key);

        var all = parts.SelectMany(p => p!).ToArray();
        if (all.Length > MaxBytes || PhotoStore.IdOf(all) != id)
        {
            GD.PushWarning($"[photo] {id} from {peer}: {all.Length} bytes that do not hash to the id");
            return null;
        }
        return all;
    }

    private void Forget(long peer)
    {
        foreach (var key in _incoming.Keys.Where(k => k.Peer == peer).ToList()) _incoming.Remove(key);
        foreach (var set in _waiting.Values) set.Remove(peer);
    }
}
