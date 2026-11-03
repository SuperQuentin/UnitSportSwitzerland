using Godot;
using UnitSport.Player;

namespace UnitSport.Vehicles;

/// <summary>
/// A vehicle standing in the world, in a UFO's tractor beam (#861, <see cref="TractorBeams"/>):
/// lifted and let down by its authority (the client that parked it, or the server for one it
/// placed or woke from a dormant slot), its own physics held meanwhile, its place published at the
/// moving rate so every peer sees it rise. Nothing bigger than <see cref="BeamMaxSize"/> (a steamer,
/// an airliner), nothing hitched or carried.
/// </summary>
public partial class VehicleBody : IBeamable
{
    /// <summary>The longest side a beam lifts, m: cars, vans, a tractor, a small boat, a saucer.</summary>
    public const float BeamMaxSize = 12f;

    /// <summary>Seconds the beam's hold lasts without a new step: it lets go by itself if the service stops.</summary>
    private float _beamLease;

    public override void _EnterTree() => TractorBeams.Register(this);

    public Vector3 BeamPosition => GlobalPosition;

    public bool BeamMovable
    {
        get
        {
            if (!IsInsideTree() || !IsMultiplayerAuthority() || _inHold) return false;
            if (Ride is ParkedTrailer or Truck { TrailerCode: not 0 }) return false;
            var size = Ride.ParkedBox.Size;
            return Mathf.Max(size.X, Mathf.Max(size.Y, size.Z)) <= BeamMaxSize;
        }
    }

    public bool BeamGrounded
    {
        get
        {
            float terrain = Terrain != null && Terrain.TryGetHeight(GlobalPosition, out float g) ? g : float.NaN;
            float ground = TractorBeams.GroundUnder(this, GlobalPosition, terrain, GetRid());
            return float.IsNaN(ground) || GlobalPosition.Y <= ground + 0.2f;
        }
    }

    public void BeamMove(Vector3 velocity, float dt)
    {
        if (_beamLease <= 0f && _sync != null) _sync.ReplicationInterval = 0.05f;
        _beamLease = 0.25f;
        GlobalPosition += velocity * dt;
        // it turns slowly as it rises
        Rotation = Rotation with { Y = Rotation.Y + 0.35f * dt };
        Velocity = Vector3.Zero;
        _flight.Velocity = Vector3.Zero;
        _motion.Speed = 0f;
        _place.Publish(GlobalPosition);
    }

    public void BeamRelease()
    {
        if (_beamLease <= 0f) return;
        _beamLease = 0f;
        _restTime = 0f;
        _place.Publish(GlobalPosition);
        // the dedicated server has no ground to simulate it on: it rests where it was let down
        if (Net.NetworkManager.DedicatedServer)
        {
            _asleep = true;
            if (_sync != null) _sync.ReplicationInterval = 2f;
            return;
        }
        // awake for a moment, so it settles on its wheels (or legs) and goes back to sleep by itself
        _asleep = false;
        SetAnchored(true);
    }

    /// <summary>In <c>_PhysicsProcess</c>, first: held by a beam, nothing else moves it. False when it is not.</summary>
    private bool BeamHolding(float dt)
    {
        if (_beamLease <= 0f) return false;
        _beamLease -= dt;
        // the service stopped stepping it (the beam's peer left): let go where it is
        if (_beamLease <= 0f) { _beamLease = 0.01f; BeamRelease(); }
        return true;
    }
}
