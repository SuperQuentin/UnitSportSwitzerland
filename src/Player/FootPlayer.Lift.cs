using Godot;
using UnitSport.World.Ropeways;

namespace UnitSport.Player;

/// <summary>
/// Riding a ski lift (#912, docs/notes/world/ski-lifts.md). At a bottom station, on foot or on
/// skis, interact boards the next carrier (<see cref="RopewayMotion.Next"/>): the body waits at the
/// station until it arrives, then <see cref="Carrier"/> follows it up the line on the shared clock;
/// at the top it is set down past the station, still on skis if it came on skis. A drag lift (T-bar,
/// rope tow) is let go of with interact again. Runs on whoever simulates the body: the owner, or the
/// server for a bot (<see cref="TryLift"/> is the bots' call too). Nothing new is sent: the rider's
/// position replicates as any player's.
/// </summary>
public partial class FootPlayer
{
    private RopewayLine? _lift;
    private int _liftCarrier;
    private double _liftBoardAt;
    private bool _liftArriving;

    /// <summary>The lift this body rides, or waits for at its station.</summary>
    public RopewayLine? Lift => _lift;

    /// <summary>Seconds until the carrier comes, while waiting at the station; 0 once aboard.</summary>
    public double LiftWait => _lift == null ? 0 : System.Math.Max(0, _liftBoardAt - RopewayService.Sim);

    /// <summary>A bottom station within reach, if the body may board: on foot or on skis, standing, not carried.</summary>
    public RopewayLine? LiftInReach()
    {
        if (_lift != null || Carrier != null || RidingWith != 0 || !IsOnFloor()) return null;
        if (_ride != null && _ride is not Skis) return null;
        return RopewayService.Instance?.StationNear(GlobalPosition);
    }

    /// <summary>Interact at a station boards; on a drag lift lets go. True when it did something.</summary>
    public bool TryLift()
    {
        if (_lift != null)
        {
            // on a chair or in a cabin there is no getting off half way
            if (_lift.Kind is RopewayKind.TBar or RopewayKind.RopeTow && LiftWait <= 0) LetGoOfLift();
            return true;
        }
        if (LiftInReach() is not { } line) return false;
        Board(line);
        return true;
    }

    private void Board(RopewayLine line)
    {
        var (carrier, wait) = RopewayMotion.Next(line, RopewayService.Sim);
        _lift = line;
        _liftCarrier = carrier;
        _liftBoardAt = RopewayService.Sim + wait;
        _liftArriving = false;
        _motion.Speed = 0f;
        // inside a cabin there is nothing to see; on a chair or a drag lift the rider is
        ShowWhileCarried = line.Kind != RopewayKind.Gondola;
        CarriedPose = 0;
        Carrier = LiftFrame;
    }

    /// <summary>The carrier's hold on the body this frame.</summary>
    private (Vector3 At, float Yaw, Vector3 Velocity)? LiftFrame()
    {
        if (_lift is not { } line || Terrain?.Origin is not { } origin) return null;
        double sim = RopewayService.Sim;
        var (speed, _, hang, _) = RopewayMotion.Of(line.Kind);
        if (sim < _liftBoardAt)
        {
            // waiting on the station's spot, facing up the line
            var (e0, n0, alt0, de0, dn0) = line.At(0);
            var spot = origin.ToWorld(e0, n0, alt0);
            float ground = Terrain.TryGetHeight(spot, out float g0) ? g0 : alt0;
            return (spot with { Y = ground }, Yaw(de0, dn0), Vector3.Zero);
        }
        float s = (float)((sim - _liftBoardAt) * speed);
        if (s >= line.Length - 2f && !_liftArriving)
        {
            _liftArriving = true;
            Callable.From(ArriveAtTop).CallDeferred();
        }
        var (e, n, alt, de, dn) = line.At(s);
        var at = origin.ToWorld(e, n, alt);
        if (line.Kind is RopewayKind.TBar or RopewayKind.RopeTow)
            at.Y = Terrain.TryGetHeight(at, out float g) ? g : alt - 6f;   // pulled along the snow
        else
            at.Y = alt - hang - (line.Kind == RopewayKind.Chair ? 0.5f : 0f);   // on the seat, on the cabin's floor
        var velocity = new Vector3(de, 0, -dn) * speed;
        return (at, Yaw(de, dn), velocity);
    }

    /// <summary>
    /// How much higher and further back the chase camera sits on a chair or in a cabin: above the
    /// cable, which a camera at a skier's chase height would have right in front of it.
    /// </summary>
    private (float Up, float Back) LiftCamera => _lift == null || LiftWait > 0 ? (0f, 0f) : _lift.Kind switch
    {
        RopewayKind.Chair => (3.2f, 3f),
        RopewayKind.Gondola => (4f, 7f),
        _ => (0f, 0f),
    };

    /// <summary>The yaw that faces a flat direction (east, north).</summary>
    private static float Yaw(float de, float dn) => Mathf.Atan2(-de, dn);

    /// <summary>At the top: off past the station, still on skis if on skis, at a walking pace.</summary>
    private void ArriveAtTop()
    {
        if (_lift is not { } line || Terrain?.Origin is not { } origin) { _lift = null; return; }
        var (e, n, alt, de, dn) = line.At(line.Length);
        var at = origin.ToWorld(e + de * 4, n + dn * 4, alt);
        at.Y = Terrain.TryGetHeight(at, out float g) ? g + 0.1f : alt;
        var on = new Vector3(de, 0, -dn) * 2f;
        _lift = null;
        if (_ride is Skis) ReleaseRiding(at, on, Yaw(de, dn));
        else Release(at, Vector3.Zero);
    }

    /// <summary>Let go of a T-bar or a rope tow where it is, on skis, gliding on at its speed.</summary>
    private void LetGoOfLift()
    {
        if (_lift is not { } line) return;
        var (speed, _, _, _) = RopewayMotion.Of(line.Kind);
        var fwd = -GlobalTransform.Basis.Z with { Y = 0 };
        _lift = null;
        if (_ride is Skis) ReleaseRiding(GlobalPosition, fwd.Normalized() * speed, Rotation.Y);
        else Release(GlobalPosition, Vector3.Zero);
    }

    /// <summary>
    /// Out of a carrier still riding (skis off a lift): the ride kept, its speed and heading taken
    /// afresh. <see cref="Release"/> would keep the speed the ride had when it was picked up.
    /// </summary>
    public void ReleaseRiding(Vector3 at, Vector3 velocity, float yaw)
    {
        Release(at, velocity);
        Rotation = new Vector3(0, yaw, 0);
        _viewYaw = yaw;
        _motion = new RideMotion { Speed = Core.MathX.FlatLength(velocity), Yaw = yaw };
    }
}
