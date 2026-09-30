using Godot;
using UnitSport.Player;

namespace UnitSport.Net;

/// <summary>
/// Shared spawner wiring for server and client. The spawner must sit at the same scene
/// path on both sides ("World/PlayerSpawner", spawning into "World/Players"); the spawn
/// function runs on every peer and builds the same node with the owning peer's authority.
///
/// <para>
/// Two kinds of spawn data: a player is its peer id (a long); a race NPC is an array
/// <c>[ownerPeer, n, rideKind, position, yaw]</c> (<see cref="NpcData"/>). An NPC's authority is
/// its owner on every peer: the owner's client simulates it, everyone else sees and hits it.
/// </para>
/// </summary>
public static class PlayerReplication
{
    public static MultiplayerSpawner CreateSpawner() => new()
    {
        Name = "PlayerSpawner",
        SpawnPath = new NodePath("../Players"),
        SpawnFunction = Callable.From((Variant data) => data.VariantType == Variant.Type.Array
            ? (Node)CreateNpc(data.AsGodotArray())
            : CreatePlayer(data.AsInt64())),
    };

    public static FootPlayer CreatePlayer(long peerId)
    {
        var player = new FootPlayer { Name = peerId.ToString() };
        player.SetMultiplayerAuthority((int)peerId);
        return player;
    }

    // ---- race NPCs ----

    /// <summary>An NPC's entrant id: negative, so it never collides with a peer id.</summary>
    public static long NpcId(long owner, int n) => -(owner * 1000 + n);

    public static long NpcOwner(long id) => -id / 1000;

    /// <summary>The node name under <c>World/Players</c> of a player or an NPC entrant id.</summary>
    public static string NodeName(long id) => id < 0 ? $"npc_{NpcOwner(id)}_{-id % 1000}" : id.ToString();

    public static Godot.Collections.Array NpcData(long owner, int n, int rideKind, Vector3 at, float yaw) =>
        new() { owner, n, rideKind, at, yaw };

    public static FootPlayer CreateNpc(Godot.Collections.Array d)
    {
        long owner = d[0].AsInt64();
        int n = d[1].AsInt32();
        var npc = new FootPlayer
        {
            Name = NodeName(NpcId(owner, n)),
            Npc = true,
            Position = d[3].AsVector3(),
            Rotation = new Vector3(0, d[4].AsSingle(), 0),
        };
        npc.SetMultiplayerAuthority((int)owner);
        // the driver; frees itself on every peer but the owner
        npc.AddChild(new World.RaceNpc { Name = World.RaceNpc.NodeName, Id = NpcId(owner, n), Kind = (RideKind)d[2].AsInt32() });
        return npc;
    }
}
