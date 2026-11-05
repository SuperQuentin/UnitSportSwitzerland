using Godot;
using UnitSport.Core;

namespace UnitSport.Net;

/// <summary>
/// Where a networked body is and how it is turned, on the wire (#185, #1503): its position as ECEF
/// doubles, in place of the body's own <c>position</c>, which is world space and means nothing to a
/// peer whose origin is elsewhere; and its rotation and velocity in the local east-up-south at the
/// body (<see cref="NetTurn"/>, <see cref="NetVel"/>), in place of <c>rotation</c> / <c>velocity</c>,
/// whose axes are the sender's world's. A child of the body (a vehicle, a radio, a dropped item),
/// named <see cref="NodeName"/>, whose <see cref="Properties"/> (then <see cref="Turn"/>, and
/// <see cref="Velocity"/> for a vehicle) go in the body's synchronizer. It must be added before the
/// synchronizer, which applies the spawn state.
///
/// <para>
/// The body's authority calls <see cref="Publish"/> after it moves; on every other peer the body
/// is put where it says when the last of the three doubles arrives, then turned and set moving in
/// that peer's own frame at that place. The values stay exact wherever the body is not simulated,
/// which is what the server hands on when it respawns or gives the body away, however far from its
/// own origin.
/// </para>
/// </summary>
public partial class NetPlace : Node
{
    public const string NodeName = "Place";

    /// <summary>The synchronizer's property paths for the position, relative to the body, in the order they must be set.</summary>
    public static readonly string[] Properties = { NodeName + ":NetX", NodeName + ":NetY", NodeName + ":NetZ" };

    /// <summary>The rotation's path (after <see cref="Properties"/>: it is applied at the body's new place).</summary>
    public const string Turn = NodeName + ":NetTurn";

    /// <summary>The velocity's path, for a <c>CharacterBody3D</c> (a vehicle), after <see cref="Turn"/>.</summary>
    public const string Velocity = NodeName + ":NetVel";

    [Export] public double NetX { get; set; }
    [Export] public double NetY { get; set; }

    [Export]
    public double NetZ
    {
        get => _z;
        set
        {
            _z = value;
            _global = GlobalPos.FromWire(NetX, NetY, value);
            // the authority is the body's: this child was added after the body's was set
            if (GetParent() is Node3D body && !body.IsMultiplayerAuthority()) body.Position = _origin.ToWorld(_global);
        }
    }
    private double _z;
    private GlobalPos _global;

    /// <summary>The body's rotation in the local east-up-south at its place (#1503).</summary>
    [Export]
    public Quaternion NetTurn
    {
        get => _turn;
        set
        {
            _turn = value;
            if (GetParent() is Node3D body && !body.IsMultiplayerAuthority())
                body.Quaternion = _origin.Frame.WorldTurn(body.Position, value);
        }
    }
    private Quaternion _turn = Quaternion.Identity;

    /// <summary>The body's velocity in the local east-up-south at its place (#1503), m/s.</summary>
    [Export]
    public Vector3 NetVel
    {
        get => _vel;
        set
        {
            _vel = value;
            if (GetParent() is CharacterBody3D body && !body.IsMultiplayerAuthority())
                body.Velocity = _origin.Frame.WorldOfEnu(body.Position, value);
        }
    }
    private Vector3 _vel;

    private readonly WorldOrigin _origin;
    private EnuNear _enu;

    /// <summary>The body's position, origin-free.</summary>
    public GlobalPos Global => _global;

    public NetPlace() : this(null!, default) { }

    /// <summary>
    /// At <paramref name="at"/>, turned by <paramref name="yaw"/> about up and moving at <paramref name="velocity"/>,
    /// both in GlobalPos's offset axes there (a record's heading and velocity), until the authority publishes.
    /// </summary>
    public NetPlace(WorldOrigin origin, GlobalPos at, float yaw = 0f, Vector3 velocity = default)
    {
        Name = NodeName;
        _origin = origin;
        SetGlobal(at);
        if (origin == null) return;
        var enu = Lv95Grid.Axes(at);
        _turn = new Quaternion(Vector3.Up, enu.TrueYaw(yaw));
        _vel = enu.ToEnu(velocity);
    }

    /// <summary>The same, turned by a whole rotation in GlobalPos's offset axes at <paramref name="at"/> (a thrown item's tumble).</summary>
    public NetPlace(WorldOrigin origin, GlobalPos at, Quaternion turn) : this(origin, at)
    {
        if (origin != null) _turn = Lv95Grid.Axes(at).ToEnu(turn);
    }

    private void SetGlobal(GlobalPos at)
    {
        _global = at;
        (NetX, NetY, _z) = at.ToWire();
    }

    /// <summary>
    /// The authority: the body is at <paramref name="world"/> (world space) now, turned and moving as its node is;
    /// its rotation and velocity are taken in the local east-up-south there.
    /// </summary>
    public void Publish(Vector3 world)
    {
        SetGlobal(_origin.ToGlobal(world));
        if (GetParent() is not Node3D body) return;
        var enu = _enu.At(_origin.Frame, world);
        _turn = enu.ToEnu(body.Quaternion);
        if (body is CharacterBody3D moving) _vel = enu.ToEnu(moving.Velocity);
    }
}
