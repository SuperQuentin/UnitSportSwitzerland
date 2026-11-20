using System;
using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Who drives the long drive's car (#1744, <see cref="LongDriveCheck"/>): the racing <see cref="AutoPilot"/> on a fixture
/// course, a <see cref="LaneFollower"/> on a planned route to a destination.
/// </summary>
public interface ILongDriveDriver
{
    /// <summary>How far along <see cref="Route"/> the car is (m).</summary>
    float Arc { get; }
    RaceRoute Route { get; }
    /// <summary>The speed it keeps under (m/s).</summary>
    float MaxSpeed { set; }
    /// <summary>False: it stops where it is.</summary>
    bool Go { set; }
    RideInput Drive(float dt);
}

/// <summary>The racing pilot (a fixture course: the line, flat out).</summary>
public sealed class RacingDriver(AutoPilot pilot) : ILongDriveDriver
{
    public float Arc => pilot.Arc;
    public RaceRoute Route => pilot.Route;
    public float MaxSpeed { set => pilot.MaxSpeed = value; }
    public bool Go { set => pilot.Go = value; }
    public RideInput Drive(float dt) => pilot.Drive(dt, true, Array.Empty<AutoPilot.Other>());
}

/// <summary>
/// A driver on its lane (#1744): pure pursuit of a point on the route's line a little ahead (3 m and 0.5 s of travel),
/// throttle and brake toward the speed it is given. The racing pilot reads every building and missing-data edge of a 3 m
/// street as a hazard of the racing line's verge, holds and resets itself onto the line: in Geneva's streets it got
/// nowhere. The route keeps the lane (<see cref="RaceRoute.ToAsync"/>); this keeps to the route.
/// </summary>
public sealed class LaneFollower : ILongDriveDriver
{
    private readonly FootPlayer _car;
    private readonly WorldOrigin _origin;
    private int _near;
    private float _maxSpeed = 10f;
    private bool _go = true;
    // backing off what it ran into (a signal pole on an island's nose, a kerb): held still at full throttle for
    // HeldS, it reverses for ReverseS with the wheel the other way, then takes up the line again
    private float _heldS, _reverseS, _reverseSteer, _lastSteer;
    private Vector3 _lastPos;   // where it was a step ago: held is not moving, whatever the wheels say
    private const float HeldS = 3f, ReverseS = 2.5f;

    /// <summary>How many times it has backed off something it ran into.</summary>
    public int BackOffs { get; private set; }

    public LaneFollower(RaceRoute route, FootPlayer car, WorldOrigin origin)
    {
        Route = route;
        _car = car;
        _origin = origin;
        route.Follow(origin.Frame);
        _near = route.NearestCentre(car.GlobalPosition);
    }

    public RaceRoute Route { get; }
    public float Arc => Route.Arc[_near];
    public float MaxSpeed { set => _maxSpeed = value; }
    public bool Go { set => _go = value; }

    /// <summary>How far back and on the nearest point is looked for from the last (2 m points): the route never doubles back that close.</summary>
    private const int Back = 10, On = 60;

    public RideInput Drive(float dt)
    {
        Route.Follow(_origin.Frame);   // a re-anchor since the last step moves the line with the world
        var pos = _car.GlobalPosition;
        var c = Route.Centre;
        int best = _near;
        float bestD = float.MaxValue;
        for (int i = Math.Max(0, _near - Back); i < Math.Min(c.Count, _near + On); i++)
        {
            float d = RaceRoute.Flat(c[i] - pos).LengthSquared();
            if (d < bestD) { bestD = d; best = i; }
        }
        _near = best;

        var m = _car.Motion;
        float v = m.Speed;
        var target = c[Route.NearestCentreIndexAt(Arc + Mathf.Clamp(3f + v * 0.5f, 5f, 25f))];
        var heading = new Basis(Vector3.Up, m.Yaw) * Vector3.Forward;
        float angle = RaceRoute.SignedAngle(RaceRoute.Flat(heading), RaceRoute.Flat(target - pos));
        float steer = Mathf.Clamp(-angle * 2.2f, -1f, 1f);   // as AutoPilot: + angle is to the left, steer right is +
        float want = _go ? _maxSpeed : 0f;
        if (Mathf.Abs(angle) > 0.6f) want = Mathf.Min(want, 3f);   // far off the line's way: crawl round to it
        float throttle = Mathf.Clamp((want - v) * 0.5f, 0f, 1f);
        float brake = v > want + 0.5f ? Mathf.Clamp((v - want) * 0.4f, 0f, 1f) : 0f;
        if (_reverseS > 0f)
        {
            // the brake at a standstill selects reverse and drives it (Car); the wheel the other way swings the nose clear
            _reverseS -= dt;
            _lastPos = pos;
            Said = System.FormattableString.Invariant($"backing off ({BackOffs}), {_reverseS:F1} s left");
            return new RideInput(0f, 1f, _reverseSteer, false);
        }
        float moved = dt > 0f ? RaceRoute.Flat(pos - _lastPos).Length() / dt : 0f;
        _lastPos = pos;
        // still rolling backwards after backing off (#1916): the box is in reverse, where the brake pedal DRIVES. Braking it
        // toward the crawl ran the car backwards to 62 km/h into a tree and threw the driver. The pedal against the motion
        // in reverse is the gas: it stops the car, and at a standstill selects first
        if (Backwards(m) && _go)
        {
            _lastSteer = steer;
            Said = System.FormattableString.Invariant($"stopping its backward roll at {v * 3.6f:F0} km/h, then on");
            return new RideInput(1f, 0f, 0f, false);
        }
        _heldS = _go && throttle > 0.3f && moved < 0.5f ? _heldS + dt : 0f;
        if (_heldS > HeldS)
        {
            _heldS = 0f;
            _reverseS = ReverseS;
            _reverseSteer = -Mathf.Sign(_lastSteer == 0f ? 1f : _lastSteer) * 0.8f;
            BackOffs++;
        }
        _lastSteer = steer;
        Said = System.FormattableString.Invariant(
            $"off the line {Mathf.Sqrt(bestD):F1} m, its way {Mathf.RadToDeg(angle):F0}°, want {want * 3.6f:F0} km/h, throttle {throttle:F2} brake {brake:F2} steer {steer:F2}");
        return new RideInput(throttle, brake, steer, false);
    }

    /// <summary>Moving backwards (its motion's way more than 90° off its heading), faster than a creep.</summary>
    internal static bool Backwards(RideMotion m) => m.Speed > 0.5f && Mathf.Cos(m.Slip) < 0f;

    /// <summary>The last step's numbers, for the log.</summary>
    public string Said { get; private set; } = "";
}
