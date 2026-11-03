using Godot;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// A dropped item in a UFO's tractor beam (#861, <see cref="TractorBeams"/>): lifted and let down
/// by the peer that simulates it (the dropper's client, or the server for one it owns), kinematic
/// meanwhile, its place published at the falling rate so every other peer sees it rise.
/// </summary>
public partial class DroppedItem : IBeamable
{
    private bool _beamed;

    public override void _EnterTree() => TractorBeams.Register(this);

    public Vector3 BeamPosition => GlobalPosition;

    public bool BeamMovable => !Proxy && IsInsideTree() && IsMultiplayerAuthority() && (Settled || _beamed);

    public bool BeamGrounded
    {
        get
        {
            float terrain = DroppedItems.GroundHeight?.Invoke(GlobalPosition) ?? float.NaN;
            float ground = TractorBeams.GroundUnder(this, GlobalPosition, terrain, GetRid());
            return float.IsNaN(ground) || GlobalPosition.Y <= ground + 0.25f;
        }
    }

    public void BeamMove(Vector3 velocity, float dt)
    {
        if (!_beamed)
        {
            _beamed = true;
            FreezeMode = FreezeModeEnum.Kinematic;
            Freeze = true;
            if (_sync != null) _sync.ReplicationInterval = 0.05f;
            if (_relay != null) _relay.ReplicationInterval = 0.05f;
        }
        GlobalPosition += velocity * dt;
        // a slow turn as it rises, the way things do in a beam
        RotateY(0.8f * dt);
        _place.Publish(Position);
    }

    public void BeamRelease()
    {
        if (!_beamed) return;
        _beamed = false;
        FreezeMode = FreezeModeEnum.Static;
        Settled = true;
        if (_sync != null) _sync.ReplicationInterval = 2f;
        if (_relay != null) _relay.ReplicationInterval = 2f;
        _place.Publish(Position);
    }
}
