using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Vehicles;

/// <summary>
/// Shoved by a vehicle driven into it (#756, found in the #751 playtest: a bus stopped dead
/// against three parked cars and none of them moved). A <see cref="CharacterBody3D"/> is never
/// pushed by another, so the driver judges the blow from its speed before the move (after it,
/// both bodies read as stopped), gives up its own share of the momentum by mass and hands this
/// one the rest (<c>FootPlayer.ShoveInto</c>, <see cref="TakeShove"/>).
/// </summary>
public partial class VehicleBody
{
    /// <summary>How much of the closing speed comes back as a bounce: 0 the two go on together, 1 a billiard ball.</summary>
    [Tunable("bounce of a vehicle shoved by another: 0 they go on together, 1 elastic; 0-0.6")]
    public static float ShoveRestitution = 0.2f;
    /// <summary>A shoved vehicle's sideways slide dies at this rate, m/s²: tyres scrubbing across the road.</summary>
    [Tunable("m/s² a shoved vehicle's sideways slide dies at (tyres scrubbing); 3-12")]
    public static float ShoveGrip = 7f;

    /// <summary>The sideways part of a shove, sliding across its own heading until the tyres stop it.</summary>
    private Vector3 _shove;
    /// <summary>Yaw rate from an off-centre blow, rad/s, until the tyres stop it.</summary>
    private float _spin;
    /// <summary>A shoved vehicle's spin dies at this rate, rad/s².</summary>
    [Tunable("rad/s² a shoved vehicle's spin dies at; 1-8")]
    public static float SpinGrip = 3f;

    /// <summary>Mass in kg of a ride, for who shoves whom; a guess where the ride carries none.</summary>
    public static float MassOf(Rideable? ride) => ride switch
    {
        Car car => car.Spec.Mass,
        Truck truck => truck.Spec.Sections.Sum(s => s.Mass),
        Motorbike bike => bike.Mass,
        _ => 1200f,
    };

    /// <summary>
    /// A driver's blow (<c>FootPlayer.ShoveInto</c>): this vehicle's velocity changes by
    /// <paramref name="kick"/>, on its authority (asked over the network when that is not here).
    /// </summary>
    public void TakeShove(Vector3 kick, Vector3 at)
    {
        if (Wrecked || _inHold || Ride is Flyer or Boat or Airstairs) return;
        // where it was struck, from its middle: origin-free, so it means the same on every peer
        var lever = (at - GlobalPosition) with { Y = 0 };
        if (IsMultiplayerAuthority()) Shove(kick, lever);
        else RpcId(GetMultiplayerAuthority(), MethodName.ShovedBy, kick, lever);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ShovedBy(Vector3 kick, Vector3 lever)
    {
        // a blow is a few m/s, from somewhere on the body: anything past that is not one
        if (IsMultiplayerAuthority() && kick.Length() < 40f && lever.Length() < 30f) Shove(kick, lever);
    }

    /// <summary>A change of velocity from a blow: along its heading it rolls, across it slides; asleep, it wakes.</summary>
    private void Shove(Vector3 kick, Vector3 lever)
    {
        // turned by a blow off its middle: the kick's moment over the body's own (a box's, from the hull)
        var size = GetNodeOrNull<CollisionShape3D>("Hull")?.Shape is BoxShape3D box ? box.Size : new Vector3(1.8f, 1.4f, 4.4f);
        _spin += lever.Cross(kick).Y / Mathf.Max((size.X * size.X + size.Z * size.Z) / 12f, 0.5f);
        var heading = -GlobalTransform.Basis.Z with { Y = 0 };
        heading = heading.LengthSquared() > 1e-6f ? heading.Normalized() : Vector3.Forward;
        float along = kick.Dot(heading);
        _motion.Speed += along;
        _shove += kick - heading * along;
        if (_asleep)
        {
            _asleep = false;
            _restTime = 0f;
            SetAnchored(true);
            if (_sync != null) _sync.ReplicationInterval = 0.05f;
        }
    }

    /// <summary>The spin of a shove on top of a step's yaw, and its decay: radians to turn this step.</summary>
    private float Spin(float dt, bool onFloor)
    {
        float turn = _spin * dt;
        _spin = Mathf.MoveToward(_spin, 0f, (onFloor ? SpinGrip : 0.3f) * dt);
        return turn;
    }

    /// <summary>The slide of a shove on top of a step's velocity, and its decay.</summary>
    private Vector3 Slide(float dt, bool onFloor)
    {
        var slide = _shove;
        _shove = _shove.MoveToward(Vector3.Zero, (onFloor ? ShoveGrip : 0.5f) * dt);
        return slide;
    }
}
