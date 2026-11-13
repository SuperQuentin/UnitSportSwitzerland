using Godot;

namespace UnitSport.Player;

/// <summary>
/// The body and its camera kept finite (#1811). A user came down from a long spectator flight round the Earth, went
/// back into the body, and saw nothing ever again in it: the light culler failed every frame
/// (<c>create_frustum_points</c>), the body stood still at 1,076 m with the wingsuit prompt up (falling), and it stayed
/// so after a creative flight to space and a teleport home. The spectator camera drew fine all along. One non-finite
/// value in a state that eases toward itself (the landing spring, the arm, the zoom) never leaves it, and the third
/// person camera adds them all to its position: a NaN eye with a sane rotation, the sky drawn and nothing else.
///
/// <para>
/// <see cref="StepInFromSpectator"/> puts the body where the fly camera leaves it at rest and upright (parked far away,
/// thousands of turning re-anchors had turned its basis by the Earth's curve between there and here, ~60° from
/// Switzerland to Siberia), with every eased state of its view started afresh. <see cref="KeepFinite"/> runs each
/// physics step and each frame on the local body: a non-finite one is reset and named in the log, once a session per
/// name, instead of drawing nothing forever.
/// </para>
/// </summary>
public partial class FootPlayer
{
    /// <summary>What <see cref="KeepFinite"/> has reported already: each name once a session.</summary>
    private static readonly System.Collections.Generic.HashSet<string> _saidNonFinite = new();

    /// <summary>
    /// Out of the fly camera into this body (<c>ClientWorld</c>'s T): standing at <paramref name="at"/>, upright,
    /// at rest, facing <paramref name="yaw"/>, its fall, landing and the camera's easing started afresh.
    /// </summary>
    public void StepInFromSpectator(Vector3 at, float yaw)
    {
        if (!float.IsFinite(yaw)) yaw = 0f;
        GlobalTransform = new Transform3D(new Basis(Vector3.Up, yaw), at);
        _viewYaw = yaw;
        if (!float.IsFinite(_pitch)) _pitch = 0f;
        Velocity = Vector3.Zero;
        ResetViewState();
        // a kinematic body moved by hand: its collision goes with it, not swept from 6,000 km away
        if (IsInsideTree() && PhysicsServer3D.BodyGetMode(GetRid()) == PhysicsServer3D.BodyMode.Kinematic)
            Core.OriginShifter.Teleport(this);
    }

    /// <summary>The fall, the landing spring and the camera's eased state, as on a fresh body.</summary>
    private void ResetViewState()
    {
        _fallSpeed = 0f;
        _landingDip = _landingVelocity = 0f;
        _wasOnFloor = true;
        _punch = _jolt = 0f;
        _pivotY = float.NaN;
        _armBlend = _chaseBlend = 1f;
        if (!float.IsFinite(_zoomWant)) _zoomWant = ArmLength;
        _zoom = _zoomWant;
    }

    /// <summary>
    /// Every value the body's motion and its camera ease from, finite; a non-finite one is put back (named in the log
    /// once). Returns false when one was. The camera's own transform last: rebuilt at the eyes if it is still not
    /// finite (placed this frame from a value fixed only now).
    /// </summary>
    private bool KeepFinite()
    {
        bool ok = true;
        if (!Velocity.IsFinite()) { Velocity = Vector3.Zero; ok = Said("velocity"); }
        if (!float.IsFinite(_fallSpeed)) { _fallSpeed = 0f; ok = Said("fall speed"); }
        if (!float.IsFinite(_landingDip) || !float.IsFinite(_landingVelocity)) { _landingDip = _landingVelocity = 0f; ok = Said("landing spring"); }
        if (!float.IsFinite(_armBlend)) { _armBlend = 1f; ok = Said("camera arm"); }
        if (!float.IsFinite(_chaseBlend)) { _chaseBlend = 1f; ok = Said("chase arm"); }
        if (!float.IsFinite(_zoom) || !float.IsFinite(_zoomWant)) { _zoom = _zoomWant = ArmLength; ok = Said("zoom"); }
        if (!float.IsFinite(_viewYaw)) { _viewYaw = Rotation.Y is var y && float.IsFinite(y) ? y : 0f; ok = Said("view yaw"); }
        if (!float.IsFinite(_pitch)) { _pitch = 0f; ok = Said("view pitch"); }
        if (!float.IsFinite(_punch) || !float.IsFinite(_jolt)) { _punch = _jolt = 0f; ok = Said("punch"); }
        if (_pivotY is var p && float.IsInfinity(p)) { _pivotY = float.NaN; ok = Said("camera pivot"); }
        if (_camera != null)
        {
            if (!float.IsFinite(_camera.Fov)) { _camera.Fov = BaseFov; ok = Said("field of view"); }
            if (!_camera.GlobalTransform.IsFinite() && GlobalTransform.IsFinite())
            {
                _camera.GlobalTransform = new Transform3D(new Basis(Vector3.Up, _viewYaw) * new Basis(Vector3.Right, _pitch), EyePosition);
                ok = Said("camera transform");
            }
        }
        return ok;
    }

    /// <summary>Logs a non-finite value put back, the first time this session; always false.</summary>
    private bool Said(string what)
    {
        if (_saidNonFinite.Add(what))
            GD.PushWarning($"[player] {Name}: a non-finite {what} was put back (#1811); at {GlobalPosition}, velocity {Velocity}, on floor {IsOnFloor()}");
        return false;
    }

    /// <summary>Test hook (<c>--spacelandcheck</c>): the values a non-finite velocity spreads into, poisoned.</summary>
    internal void DebugPoisonView()
    {
        Velocity = new Vector3(0, float.NaN, 0);
        _fallSpeed = _landingVelocity = _landingDip = float.NaN;
        _armBlend = _zoom = float.NaN;
    }
}
