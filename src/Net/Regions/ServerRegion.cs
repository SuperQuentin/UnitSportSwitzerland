using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Net.Regions;

/// <summary>
/// One of the dedicated server's local frames (#1456, G2a of #1443): a frame, its own physics space
/// and the collision built round the bots in it. The main region is the server's world as it always
/// was (the manifest origin, the root world, <c>World/Terrain</c>); every other region is a
/// <see cref="SubViewport"/> with its own <see cref="World3D"/> holding its <see cref="ChunkManager"/>
/// and trunks. Bodies stay at their node paths (RPCs find them there) and only their physics moves
/// into the region's space (<see cref="Player.FootPlayer.EnterRegion"/>).
/// </summary>
public sealed class ServerRegion
{
    public ServerRegion(int id, WorldOrigin origin, ChunkManager chunks, World3D world, SubViewport? root)
    {
        Id = id;
        Origin = origin;
        Chunks = chunks;
        World = world;
        Root = root;
    }

    public int Id { get; }
    /// <summary>The region's frame: a body in it has its position in these coordinates.</summary>
    public WorldOrigin Origin { get; }
    /// <summary>Its terrain: collision round its bots, coarse grids round its players' proxies.</summary>
    public ChunkManager Chunks { get; }
    public World3D World { get; }
    /// <summary>The viewport holding its world; null for the main region (the root's world).</summary>
    public SubViewport? Root { get; }
    public bool IsMain => Root == null;
    public Rid Space => World.Space;
    /// <summary>Its physics for queries (rays, shapes): only valid in a physics step, as any space state.</summary>
    public PhysicsDirectSpaceState3D DirectSpace => World.DirectSpaceState;
    /// <summary>The anchor of its frame as an Earth position.</summary>
    public GlobalPos Anchor => GlobalPos.FromLv95(Origin.E, Origin.N, 0);

    public override string ToString() => $"region {Id} (LV95 {Origin.E:F0}/{Origin.N:F0})";
}
