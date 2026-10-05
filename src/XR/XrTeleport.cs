using Godot;
using UnitSport.Player;

namespace UnitSport.XR;

/// <summary>
/// Teleport on foot (#439), the comfort alternative to walking with the stick, when the setting
/// asks for it: push the moving hand's stick forward and an arc falls from that hand; let go and,
/// where it lands on walkable ground, a blink puts you there.
///
/// <para>
/// It is never faster than walking: after a jump the next waits as long as walking that far would
/// take (<see cref="Pace"/>), so a match (#425) gives a teleporting player no edge.
/// </para>
/// </summary>
internal sealed partial class XrTeleport : Node3D
{
    private const int Dots = 28;
    private const float Speed = 7.5f, Gravity = 9.8f, TimeStep = 0.06f;
    /// <summary>The longest jump, m, and the steepest ground it lands on (the normal's y).</summary>
    private const float Range = 12f, Flat = 0.7f;
    /// <summary>Walking pace: a jump of d metres waits d / Pace seconds before the next.</summary>
    private const float Pace = 5f;

    private readonly MeshInstance3D[] _dots = new MeshInstance3D[Dots];
    private readonly StandardMaterial3D _good, _bad;
    private readonly Core.RayQuery _ray = new();
    private bool _aiming;
    private Vector3? _target;
    private float _wait;

    public XrTeleport()
    {
        Name = "Teleport";
        TopLevel = true;
        _good = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.95f, 0.6f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        _bad = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.45f, 0.4f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        var mesh = new SphereMesh { Radius = 0.03f, Height = 0.06f, RadialSegments = 6, Rings = 3 };
        for (int i = 0; i < Dots; i++)
        {
            _dots[i] = new MeshInstance3D { Mesh = mesh, Visible = false, Layers = XrSession.HeadsetOnlyLayer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_dots[i]);
        }
    }

    /// <param name="hand">The moving hand (the left one, or the right when left-handed).</param>
    /// <param name="push">That hand's stick forward, 0..1.</param>
    /// <returns>True in teleport mode on foot: the stick aims, it does not walk.</returns>
    public bool Update(FootPlayer? player, XRController3D hand, float push, Action blink, float dt)
    {
        _wait = Mathf.Max(0f, _wait - dt);
        bool can = player is { Ride: RideKind.OnFoot, RidingWith: 0 } && hand.GetHasTrackingData()
                   && Input.MouseMode == Input.MouseModeEnum.Captured && Core.GameSettings.Current.VrTeleport;
        if (!can)
        {
            Hide();
            return false;
        }
        if (push > 0.6f) _aiming = true;
        else if (_aiming && push < 0.3f)
        {
            // let go: there, if there is somewhere and the last jump has been walked off
            if (_target is { } at && _wait <= 0f)
            {
                float d = player!.GlobalPosition.DistanceTo(at);
                blink();
                player.PlaceAt(at + Vector3.Up * 0.05f, player.LookYaw);
                _wait = d / Pace;
            }
            Hide();
            return true;
        }
        if (!_aiming) return true;

        Arc(player!, hand);
        return true;
    }

    private void Arc(FootPlayer player, XRController3D hand)
    {
        var space = player.GetWorld3D().DirectSpaceState;
        var from = hand.GlobalPosition;
        var velocity = -hand.GlobalBasis.Z * Speed;
        _target = null;
        int shown = 0;
        bool good = false;
        for (int i = 0; i < Dots; i++)
        {
            var next = from + velocity * TimeStep;
            velocity += Vector3.Down * Gravity * TimeStep;
            var hit = _ray.Cast(space, from, next, uint.MaxValue, player.SelfExclude);
            _dots[i].Visible = true;
            _dots[i].GlobalPosition = from;
            shown = i + 1;
            if (hit.Count > 0)
            {
                var at = hit["position"].AsVector3();
                var normal = hit["normal"].AsVector3();
                good = normal.Y > Flat && at.DistanceTo(player.GlobalPosition) < Range && _wait <= 0f;
                if (good) _target = at;
                break;
            }
            from = next;
        }
        var mat = good ? _good : _bad;
        for (int i = 0; i < Dots; i++)
        {
            if (i >= shown) _dots[i].Visible = false;
            else _dots[i].MaterialOverride = mat;
        }
    }

    private void Hide()
    {
        _aiming = false;
        _target = null;
        foreach (var d in _dots) d.Visible = false;
    }
}
