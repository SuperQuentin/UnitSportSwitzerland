using Godot;
using UnitSport.Core;
using UnitSport.Net.Regions;

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
        Terrain = target.Chunks;
        ApplyRegionSpace();
        if (from != null) OriginShifter.Move(this, target.Origin.Frame.Since(from.Frame));
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
