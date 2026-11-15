using Godot;
using UnitSport.Core;
using UnitSport.Net.Regions;
using UnitSport.Terrain;

namespace UnitSport.Player;

/// <summary>
/// The dedicated server's regions (#1456): a body the server holds is in one region, whose frame its
/// position is in and whose physics space its collision is in. Its node stays at its path (RPCs find it
/// there); <see cref="Origin"/>, <see cref="Terrain"/> and <see cref="Space"/> follow the region.
/// Null on clients, where there is one world.
/// </summary>
public partial class FootPlayer
{
    /// <summary>The server region this body is in (null on clients).</summary>
    public ServerRegion? Region { get; internal set; }

    /// <summary>
    /// The ground game's gravity for this body (#1852): its server region's body's on the server, where bodies on the Earth
    /// and the Moon step side by side; the anchor's (<see cref="BodyGravity"/>) on a client, where there is one world.
    /// </summary>
    private float Gravity => Region is { } r && r.Body != BodyId.Earth ? (float)CelestialBody.Of(r.Body).GroundGravity : BodyGravity.Ground;

    /// <summary>
    /// The physics this body acts in, for its rays and shape queries: its region's on the server, the
    /// world's anywhere else. Never <c>GetWorld3D().DirectSpaceState</c> in body code: on the server
    /// that is the main region's whatever region the body is in.
    /// </summary>
    public PhysicsDirectSpaceState3D Space => Region is { IsMain: false } r ? r.DirectSpace : GetWorld3D().DirectSpaceState;

    /// <summary>
    /// Where <paramref name="other"/> is in this body's frame: its own position when both are in one
    /// region (or on a client), else through its Earth position. A brain comparing itself with a
    /// teammate never compares floats of two frames.
    /// </summary>
    public Vector3 WhereIs(FootPlayer other) =>
        other.Region == Region || Origin is not { } frame ? other.GlobalPosition : frame.ToWorld(other.Global);

    /// <summary>
    /// A point of <paramref name="frame"/> (a main-frame object's, on the server: a parked car, a saucer)
    /// in this body's frame; unchanged when they are the same frame, or on a client.
    /// </summary>
    public Vector3 FromFrame(WorldOrigin? frame, Vector3 p) =>
        frame == null || Origin is not { } mine || frame == mine ? p : mine.ToWorld(frame.ToGlobal(p));

    /// <summary>A point of this body's frame in <paramref name="frame"/>'s: the way back of <see cref="FromFrame"/>.</summary>
    public Vector3 ToFrame(WorldOrigin? frame, Vector3 p) =>
        frame == null || Origin is not { } mine || frame == mine ? p : frame.ToWorld(mine.ToGlobal(p));

    /// <summary>Where a vehicle is in this body's frame: from its exact Earth position when the frames differ.</summary>
    public Vector3 WhereIs(Vehicles.VehicleBody v) =>
        Origin is { } mine && v.Origin is { } theirs && mine != theirs ? mine.ToWorld(v.Global) : v.GlobalPosition;

    /// <summary>The ground of the region this body is in when that is not the main one (the statics bound to the main terrain step aside for it), else null.</summary>
    public IWorldGround? RegionTerrain => Region is { IsMain: false } r ? r.Ground : null;

    /// <summary>Whether this body is in the server's main frame (always on a client): where parked vehicles, crates and stops are placed.</summary>
    public bool InMainFrame => Region is null or { IsMain: true };

    /// <summary>
    /// Moves this body into <paramref name="target"/>'s frame and space, standing at
    /// <paramref name="at"/>: its terrain anchor, its frame (what it keeps in world space moves as at
    /// an origin shift), its position exact from the Earth position, then its collision.
    /// </summary>
    public void EnterRegion(ServerRegion target, GlobalPos at)
    {
        var from = Origin;
        Terrain?.RemoveAnchor(this);
        Region = target;
        Origin = target.Origin;
        Terrain = target.Ground;
        ApplyRegionSpace();
        if (from != null && from.Body != target.Origin.Body)
        {
            // onto another body (#1852): nothing it kept in the old world means anything there; it arrives at rest, upright
            Velocity = Vector3.Zero;
            Rotation = new Vector3(0, 0, 0);
        }
        else if (from != null)
        {
            // what the body keeps in world space moves as at an origin shift; between frames that turn against each other
            // (ENU regions, #1513) it turns too, keeping its bearings against true north (velocity, headings, as at a
            // Space re-anchor), and the node is stood up on the new frame's up
            var shift = target.Origin.Frame.Carry(from.Frame, Position, at);
            OriginShifter.Move(this, shift);
            if (shift.Turns) Rotation = new Vector3(0, Rotation.Y, 0);
        }
        // exact from the Earth position, not the float sum of a far position and a far offset
        Position = target.Origin.ToWorld(at);
        if (PhysicsServer3D.BodyGetMode(GetRid()) == PhysicsServer3D.BodyMode.Kinematic) OriginShifter.Teleport(this);
        if (IsMultiplayerAuthority()) NetGlobal = at;
        if (IsInsideTree()) Terrain.AddAnchor(this, collision: !Net.NetworkManager.DedicatedServer || IsMultiplayerAuthority());
    }

    /// <summary>Puts this body's collision, and its parts' (a truck's sections), in its region's space.</summary>
    public void ApplyRegionSpace()
    {
        if (Region == null || !IsInsideTree()) return;
        SetSpace(this, Region.Space);
    }

    /// <summary>Every collision object at and under <paramref name="node"/> into <paramref name="space"/>.</summary>
    public static void SetSpace(Node node, Rid space)
    {
        // a body Godot took out of the physics (disabled, removed) is put back by Godot itself: _Notification
        if (node is CollisionObject3D co && co.IsInsideTree() && (co.CanProcess() || co.DisableMode != CollisionObject3D.DisableModeEnum.Remove))
        {
            if (co is Area3D) PhysicsServer3D.AreaSetSpace(co.GetRid(), space);
            else PhysicsServer3D.BodySetSpace(co.GetRid(), space);
        }
        for (int i = 0; i < node.GetChildCount(); i++) SetSpace(node.GetChild(i), space);
    }

    public override void _Notification(int what)
    {
        // Godot puts a re-enabled body back into its viewport's space: the main region's, not ours
        if (what == NotificationEnabled && Region is { IsMain: false }) ApplyRegionSpace();
    }
}
