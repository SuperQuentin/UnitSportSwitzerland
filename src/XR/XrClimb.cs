using Godot;
using UnitSport.Player;

namespace UnitSport.XR;

/// <summary>
/// Climbing by hand (#439): on foot, a grip closed with the hand against a steep face (rock, a
/// wall) holds that point, and the body hangs from it: moving the hand moves the body the other
/// way, hand over hand up the Alps. Let go with the last hand and you drop with the push you gave.
///
/// <para>
/// It drives the body as a carrier does (<see cref="FootPlayer.Carrier"/>, the climbing pose
/// everyone sees, #359), so nothing in the physics or the network learns a new state. Arms tire:
/// after <see cref="Stamina"/> seconds holding on, the hands slip; standing rests them.
/// </para>
/// </summary>
internal sealed class XrClimb
{
    /// <summary>A face steeper than this (its normal's y) can be climbed; the hand must touch it within <see cref="Touch"/> m.</summary>
    private const float Steep = 0.6f, Touch = 0.12f;
    private const float Stamina = 15f;

    private sealed class Grip
    {
        public required XRController3D Ctl;
        public bool Closed;
        public Vector3? Held;
        public Vector3 Prev;
    }

    private readonly Grip _l, _r;
    private readonly Core.RayQuery _ray = new();
    private Grip? _hanging;
    private Vector3 _body, _push;
    private float _tired;
    private FootPlayer? _player;

    public XrClimb(XRController3D left, XRController3D right)
    {
        _l = new Grip { Ctl = left };
        _r = new Grip { Ctl = right };
    }

    public bool LeftHeld => _l.Held != null;
    public bool RightHeld => _r.Held != null;

    public void Update(FootPlayer? player, float dt)
    {
        bool can = player != null && Input.MouseMode == Input.MouseModeEnum.Captured
                   && (player.Ride == RideKind.OnFoot && player.RidingWith == 0 || _hanging != null && player == _player);
        if (!can || player != _player)
        {
            LetGo(drop: false);
            _player = player;
        }
        if (player == null || !can) return;

        foreach (var g in new[] { _l, _r })
        {
            float grip = g.Ctl.GetHasTrackingData() ? g.Ctl.GetFloat("grip") : 0f;
            if (g.Closed && grip < 0.35f)
            {
                g.Closed = false;
                if (g.Held != null)
                {
                    g.Held = null;
                    if (_hanging == g) _hanging = _l.Held != null ? _l : _r.Held != null ? _r : null;
                    if (_hanging == null) LetGo(drop: true);
                }
            }
            else if (!g.Closed && grip > 0.7f)
            {
                g.Closed = true;
                if (Face(player, g.Ctl) is { } point)
                {
                    g.Held = point;
                    g.Prev = g.Ctl.GlobalPosition;
                    if (_hanging == null) Hang(player);
                    _hanging = g;   // the newest hand carries
                    g.Ctl.TriggerHapticPulse("haptic", 0.0, 0.5, 0.05, 0.0);
                }
            }
        }

        if (_hanging is not { Held: { } held } hand)
        {
            _tired = Mathf.Max(0f, _tired - dt * 2f);   // rested while standing
            return;
        }
        // the hand stays on its hold: the body goes where that puts it
        var move = held - hand.Ctl.GlobalPosition;
        _body += move;
        _push = -(hand.Ctl.GlobalPosition - hand.Prev) / Mathf.Max(dt, 1e-3f);
        hand.Prev = hand.Ctl.GlobalPosition + move;
        if (move.Y > 0.25f) player.ClimbStep++;
        _tired += dt;
        if (_tired > Stamina)
        {
            hand.Ctl.TriggerHapticPulse("haptic", 0.0, 0.8, 0.2, 0.0);
            LetGo(drop: true);
        }
    }

    /// <summary>The point on a steep face the hand touches, if any: a short ray from the hand along its pointing.</summary>
    private Vector3? Face(FootPlayer player, XRController3D ctl)
    {
        var from = ctl.GlobalPosition - (-ctl.GlobalBasis.Z) * 0.04f;
        var to = ctl.GlobalPosition + -ctl.GlobalBasis.Z * Touch;
        var hit = _ray.Cast(player.GetWorld3D().DirectSpaceState, from, to, uint.MaxValue, player.SelfExclude);
        if (hit.Count == 0 || hit["normal"].AsVector3().Y > Steep) return null;
        return hit["position"].AsVector3();
    }

    private void Hang(FootPlayer player)
    {
        _body = player.GlobalPosition;
        float yaw = player.Rotation.Y;
        player.ShowWhileCarried = true;
        player.CarriedPose = 2;
        player.Carrier = () => (_body, yaw, Vector3.Zero);
    }

    private void LetGo(bool drop)
    {
        _l.Held = _r.Held = null;
        _hanging = null;
        if (_player is { } p && GodotObject.IsInstanceValid(p) && p.Carrier != null && p.CarriedPose == 2)
            p.Release(_body, drop ? _push.LimitLength(6f) : Vector3.Zero);
    }
}
