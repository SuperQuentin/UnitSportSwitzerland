using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Net;

/// <summary>
/// Shared spawner wiring for server and client. The spawner must sit at the same scene
/// path on both sides ("World/PlayerSpawner", spawning into "World/Players"); the spawn
/// function runs on every peer and builds the same node with the owning peer's authority.
///
/// <para>
/// Two kinds of spawn data: a player is its peer id (a long); a race NPC is an array
/// <c>[ownerPeer, n, rideKind, E, N, altitude, yaw, setup]</c> (<see cref="NpcData"/>), its
/// position in LV95 like every position on the wire (#185). An NPC's authority is
/// its current simulator on every peer — first the client that asked for it, later whoever the
/// server hands it to (<c>World/RaceNpcs</c>, #50; a later spawn learns it from the spawn state,
/// <c>FootPlayer.SimPeer</c>): that client simulates it, everyone else sees and hits it.
/// </para>
/// </summary>
public static class PlayerReplication
{
    /// <param name="origin">This peer's origin: every player built here puts its position on the wire through it.</param>
    public static MultiplayerSpawner CreateSpawner(WorldOrigin origin) => new()
    {
        Name = "PlayerSpawner",
        SpawnPath = new NodePath("../Players"),
        SpawnFunction = Callable.From((Variant data) => data.VariantType == Variant.Type.Array
            ? (Node)CreateNpc(data.AsGodotArray(), origin)
            : CreatePlayer(data.AsInt64(), origin)),
    };

    public static FootPlayer CreatePlayer(long peerId, WorldOrigin origin)
    {
        var player = new FootPlayer { Name = peerId.ToString(), Origin = origin };
        player.SetMultiplayerAuthority((int)peerId);
        return player;
    }

    // ---- race NPCs ----

    /// <summary>An NPC's entrant id: negative, so it never collides with a peer id.</summary>
    public static long NpcId(long owner, int n) => -(owner * 1000 + n);

    public static long NpcOwner(long id) => -id / 1000;

    /// <summary>The node name under <c>World/Players</c> of a player or an NPC entrant id.</summary>
    public static string NodeName(long id) => id < 0 ? $"npc_{NpcOwner(id)}_{-id % 1000}" : id.ToString();

    public static Godot.Collections.Array NpcData(long owner, int n, int rideKind, GlobalPos at, float yaw, int setup = 0) =>
        new() { owner, n, rideKind, at.E, at.N, at.Alt, yaw, setup };

    // ---- bots (#837) ----

    /// <summary>The owner of every bot: the server, which runs them itself (authority 1).</summary>
    public const long BotOwner = 1;

    /// <summary>A bot's id: an NPC id of the server, -(1000 + n).</summary>
    public static long BotId(int n) => NpcId(BotOwner, n);

    /// <summary>Whether an entrant id is a bot's (an NPC of the server; race NPCs belong to clients).</summary>
    public static bool IsBotId(long id) => id < 0 && NpcOwner(id) == BotOwner;

    /// <summary>Driver kinds at index 8 of the NPC spawn data: a race NPC (the default) or a bot.</summary>
    private const int DriverRace = 0, DriverBot = 1;

    /// <summary>A bot's spawn data: the NPC layout, then the driver kind and its display name.</summary>
    public static Godot.Collections.Array BotData(int n, GlobalPos at, float yaw, string name) =>
        new() { BotOwner, n, (int)RideKind.OnFoot, at.E, at.N, at.Alt, yaw, 0, DriverBot, name };

    public static FootPlayer CreateNpc(Godot.Collections.Array d, WorldOrigin origin)
    {
        long owner = d[0].AsInt64();
        int n = d[1].AsInt32();
        var npc = new FootPlayer
        {
            Name = NodeName(NpcId(owner, n)),
            Npc = true,
            Origin = origin,
            Position = origin.ToWorld(d[3].AsDouble(), d[4].AsDouble(), d[5].AsDouble()),
            Rotation = new Vector3(0, d[6].AsSingle(), 0),
        };
        npc.SetMultiplayerAuthority((int)owner);
        if (d.Count > 8 && d[8].AsInt32() == DriverBot)
        {
            // a bot (#837): run by the server, driven there by Bots.BotDirector through the body's seams
            npc.Bot = true;
            npc.BotName = d.Count > 9 ? d[9].AsString() : "";
            return npc;
        }
        // the driver: on every peer, active only on its current simulator (which can change, #50)
        npc.AddChild(new World.RaceNpc
        {
            Name = World.RaceNpc.NodeName, Id = NpcId(owner, n), Kind = (RideKind)d[2].AsInt32(),
            Setup = CarSetups.Clamp(d[7].AsInt32()),
        });
        return npc;
    }
}
