using Godot;

namespace UnitSport.Net;

/// <summary>
/// Whether a node's multiplayer API may be asked anything. Once an ENet peer has lost its server
/// (connection dropped, server gone, mid-teardown) every <c>GetUniqueId</c>, <c>IsServer</c>,
/// <c>IsMultiplayerAuthority</c> and RPC logs "The multiplayer instance isn't currently active",
/// and a per-frame caller then writes that line sixty times a second. Per-frame and event paths
/// check here first and skip their network work while it is false.
/// </summary>
public static class NetLink
{
    /// <summary>Offline (the game is its own server), or a peer that is connected: safe to ask.</summary>
    public static bool Ready(Node node) =>
        node.IsInsideTree() && node.Multiplayer.MultiplayerPeer is { } peer
        && (peer is OfflineMultiplayerPeer || peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected);

    /// <summary>Connected to (or serving) other machines; false offline and once the link is down.</summary>
    public static bool Online(Node node) =>
        node.IsInsideTree() && node.Multiplayer.MultiplayerPeer is { } peer and not OfflineMultiplayerPeer
        && peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    /// <summary>This side owns the shared state: the server online, the game itself offline. False while the link is down.</summary>
    public static bool IsServer(Node node) => Ready(node) && node.Multiplayer.IsServer();
}
