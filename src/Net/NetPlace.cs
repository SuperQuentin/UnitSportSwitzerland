using Godot;
using UnitSport.Core;

namespace UnitSport.Net;

/// <summary>
/// Where a networked body is, on the wire: LV95 and altitude as doubles (#185), in place of the
/// body's own <c>position</c>, which is world space and means nothing to a peer whose origin is
/// elsewhere. A child of the body (a vehicle, a radio, a dropped item), named
/// <see cref="NodeName"/>, whose <see cref="Properties"/> go in the body's synchronizer instead of
/// <c>.:position</c>. It must be added before the synchronizer, which applies the spawn state.
///
/// <para>
/// The body's authority calls <see cref="Publish"/> after it moves; on every other peer the body
/// is put where it says when the last of the three arrives. The value stays exact wherever the
/// body is not simulated, which is what the server hands on when it respawns or gives the body
/// away, however far from its own origin.
/// </para>
/// </summary>
public partial class NetPlace : Node
{
    public const string NodeName = "Place";

    /// <summary>The synchronizer's property paths, relative to the body, in the order they must be set.</summary>
    public static readonly string[] Properties = { NodeName + ":NetE", NodeName + ":NetN", NodeName + ":NetAlt" };

    [Export] public double NetE { get; set; }
    [Export] public double NetN { get; set; }

    [Export]
    public double NetAlt
    {
        get => _alt;
        set
        {
            _alt = value;
            // the authority is the body's: this child was added after the body's was set
            if (GetParent() is Node3D body && !body.IsMultiplayerAuthority()) body.Position = _origin.ToWorld(Global);
        }
    }
    private double _alt;

    private readonly WorldOrigin _origin;

    /// <summary>The body's position, origin-free.</summary>
    public GlobalPos Global => new(NetE, NetN, _alt);

    public NetPlace() : this(null!, default) { }

    public NetPlace(WorldOrigin origin, GlobalPos at)
    {
        Name = NodeName;
        _origin = origin;
        NetE = at.E;
        NetN = at.N;
        _alt = at.Alt;
    }

    /// <summary>The authority: the body is at <paramref name="world"/> (world space) now.</summary>
    public void Publish(Vector3 world)
    {
        var at = _origin.ToGlobal(world);
        NetE = at.E;
        NetN = at.N;
        _alt = at.Alt;
    }
}
