using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Net.Regions;

/// <summary>
/// One of the dedicated server's local frames (#1456, G2a of #1443): a frame, its own physics space
/// and the collision built round the bots in it. The main region is the server's world as it always
/// was (the manifest origin, the root world, <c>World/Terrain</c>); every other region is a
/// <see cref="SubViewport"/> with its own <see cref="World3D"/> holding its terrain: a <see cref="ChunkManager"/>
/// and trunks on the flat frame, an <see cref="UnitSport.Terrain.Earth.EarthWorld"/> on leaves under <c>--frame earth</c> (#1513). Bodies stay at their node paths (RPCs find them there) and only their physics moves
/// into the region's space (<see cref="Player.FootPlayer.EnterRegion"/>).
/// </summary>
public sealed class ServerRegion
{
    public ServerRegion(int id, WorldOrigin origin, Node terrain, IWorldGround ground, World3D world, SubViewport? root)
    {
        Id = id;
        Origin = origin;
        Terrain = terrain;
        Ground = ground;
        World = world;
        Root = root;
    }

    /// <summary>A region on the flat tiles: its <see cref="ChunkManager"/> is its terrain and answers its ground.</summary>
    public ServerRegion(int id, WorldOrigin origin, ChunkManager chunks, World3D world, SubViewport? root)
        : this(id, origin, chunks, chunks.Ground, world, root) { }

    public int Id { get; }
    /// <summary>The region's frame: a body in it has its position in these coordinates.</summary>
    public WorldOrigin Origin { get; }
    /// <summary>Its terrain's node: collision round its bots, heights round its players' proxies.</summary>
    public Node Terrain { get; }
    /// <summary>What its bodies stand on and ask: the flat tiles' or the leaves' (<c>--frame earth</c>, #1513).</summary>
    public IWorldGround Ground { get; }
    /// <summary>Its flat tiles; null on leaves (<c>--frame earth</c>).</summary>
    public ChunkManager? Chunks => Terrain as ChunkManager;
    public World3D World { get; }
    /// <summary>The viewport holding its world; null for the main region (the root's world).</summary>
    public SubViewport? Root { get; }
    public bool IsMain => Root == null;
    public Rid Space => World.Space;
    /// <summary>Its physics for queries (rays, shapes): only valid in a physics step, as any space state.</summary>
    public PhysicsDirectSpaceState3D DirectSpace => World.DirectSpaceState;
    /// <summary>
    /// The anchor of its frame as an Earth position: an ENU frame's origin (on the ellipsoid, #1513), or a flat frame's
    /// whole-km LV95 corner at altitude 0.
    /// </summary>
    public GlobalPos Anchor => Origin.Frame.Axes is { } axes ? GlobalPos.On(axes.Body, axes.Origin) : GlobalPos.FromLv95(Origin.E, Origin.N, 0);

    /// <summary>The body its frame is on (#1852): the Earth, or the Moon's (another body's) ground under a region of its own.</summary>
    public BodyId Body => Origin.Body;

    /// <summary>Whether its frame is an ENU anchor frame (true north, true metres, its own up) rather than the flat LV95 one.</summary>
    public bool IsEnu => Origin.Frame.IsAnchor;

    public override string ToString() => Body != BodyId.Earth
        ? $"region {Id} ({Body} ENU at {Anchor.Geodetic.LatDeg:F4}N {Anchor.Geodetic.LonDeg:F4}E)"
        : IsEnu
        ? $"region {Id} (ENU at {Anchor.Geodetic.LatDeg:F4}N {Anchor.Geodetic.LonDeg:F4}E, LV95 {Origin.E:F0}/{Origin.N:F0})"
        : $"region {Id} (LV95 {Origin.E:F0}/{Origin.N:F0})";
}
