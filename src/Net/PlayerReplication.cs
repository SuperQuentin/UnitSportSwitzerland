using Godot;
using UnitSport.Player;

namespace UnitSport.Net;

/// <summary>
/// Shared spawner wiring for server and client. The spawner must sit at the same scene
/// path on both sides ("World/PlayerSpawner", spawning into "World/Players"); the spawn
/// function runs on every peer and builds the same node with the owning peer's authority.
/// </summary>
public static class PlayerReplication
{
    public static MultiplayerSpawner CreateSpawner() => new()
    {
        Name = "PlayerSpawner",
        SpawnPath = new NodePath("../Players"),
        SpawnFunction = Callable.From((Variant data) => (Node)CreatePlayer(data.AsInt64())),
    };

    public static FootPlayer CreatePlayer(long peerId)
    {
        var player = new FootPlayer { Name = peerId.ToString() };
        player.SetMultiplayerAuthority((int)peerId);
        return player;
    }
}
