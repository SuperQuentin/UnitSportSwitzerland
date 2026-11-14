using Godot;

namespace UnitSport.Net;

/// <summary>
/// Whether a node's multiplayer API may be asked anything. Once an ENet peer has lost its server
/// (connection dropped, server gone, mid-teardown) every <c>GetUniqueId</c>, <c>IsServer</c>,
/// <c>IsMultiplayerAuthority</c> and RPC logs "The multiplayer instance isn't currently active",
/// and a per-frame caller then writes that line sixty times a second. Per-frame and event paths
/// check here first and skip their network work while it is false.
/// <para>
/// The link's state is read once a frame on the main thread (#1910), from the tree's own API: <c>node.Multiplayer</c>
/// makes the engine build the node's path to look for a subtree's own API (none is ever set here, every node shares the
/// tree's), and the per-frame callers (<c>FootPlayer</c> above all) asked it hundreds of times a frame: ~8 % of an earth
/// client's main thread in the cinema check. A peer swapped within the frame (joining, leaving) is read afresh; off the
/// main thread, and for a node outside the tree, each call reads the link itself as before.
/// </para>
/// </summary>
public static class NetLink
{
    /// <summary>Offline (the game is its own server), or a peer that is connected: safe to ask.</summary>
    public static bool Ready(Node node) =>
        node.IsInsideTree() && State(node) is var s && s.HasPeer && (s.Offline || s.Status == MultiplayerPeer.ConnectionStatus.Connected);

    /// <summary>Connected to (or serving) other machines; false offline and once the link is down.</summary>
    public static bool Online(Node node) =>
        node.IsInsideTree() && State(node) is var s && s.HasPeer && !s.Offline && s.Status == MultiplayerPeer.ConnectionStatus.Connected;

    /// <summary>
    /// The server's link has just gone (not offline, not still connecting): the frame or two
    /// before the spawner frees the networked nodes, whose per-frame code then skips.
    /// </summary>
    public static bool Dropped(Node node) =>
        State(node) is var s && s.HasPeer && !s.Offline && s.Status == MultiplayerPeer.ConnectionStatus.Disconnected;

    /// <summary>This side owns the shared state: the server online, the game itself offline. False while the link is down.</summary>
    public static bool IsServer(Node node) => Ready(node) && State(node).IsServer;

    /// <summary>This peer's id (1 on the server and offline) while <see cref="Ready"/>, 0 otherwise; read once a frame.</summary>
    public static int UniqueId(Node node) => State(node).UniqueId;

    private readonly record struct Link(bool HasPeer, bool Offline, MultiplayerPeer.ConnectionStatus Status, bool IsServer, int UniqueId);

    private static Link _link;
    private static MultiplayerPeer? _peer;
    private static ulong _frame = ulong.MaxValue;
    private static int _mainThread = -1;

    private static Link State(Node node)
    {
        if (!node.IsInsideTree() || !OnMainThread()) return Read(node.Multiplayer, node.Multiplayer.MultiplayerPeer);
        // the tree's own API: the one every node here answers with, without the path
        var api = node.GetTree().GetMultiplayer();
        var peer = api.MultiplayerPeer;
        ulong frame = Engine.GetProcessFrames();
        if (frame == _frame && ReferenceEquals(peer, _peer)) return _link;
        _link = Read(api, peer);
        (_frame, _peer) = (frame, peer);
        return _link;
    }

    private static Link Read(MultiplayerApi api, MultiplayerPeer? peer)
    {
        if (peer == null) return new Link(false, false, MultiplayerPeer.ConnectionStatus.Disconnected, false, 0);
        bool offline = peer is OfflineMultiplayerPeer;
        var status = peer.GetConnectionStatus();
        // the id and the server flag only while the link answers (a dead peer logs on every ask)
        bool answers = offline || status == MultiplayerPeer.ConnectionStatus.Connected;
        return new Link(true, offline, status, answers && api.IsServer(), answers ? api.GetUniqueId() : 0);
    }

    /// <summary>The engine's main thread: its managed id, found on the first call made from it.</summary>
    private static bool OnMainThread()
    {
        int id = System.Environment.CurrentManagedThreadId;
        if (_mainThread == id) return true;
        if (_mainThread != -1) return false;
        if (OS.GetThreadCallerId() != OS.GetMainThreadId()) return false;
        _mainThread = id;
        return true;
    }
}
