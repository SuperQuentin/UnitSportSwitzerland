using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Playing the pigeon (#217, docs/notes/player/pigeon.md): perching on the town birds' perches,
/// the drop button, and the eye camera in first person and VR. The flight is <see cref="Pigeon"/>.
/// </summary>
public partial class FootPlayer
{
    private bool _dropHeld;
    private float _dropCooldown;

    /// <summary>Droppings this player let go as a pigeon (probes read it).</summary>
    public int PigeonDrops { get; private set; }

    /// <summary>Before the flight step: catch a perch, and let go on Fire (LMB / RB, the right trigger in VR).</summary>
    private void PigeonStep(in FlightInput input, float dt)
    {
        var c = new PigeonFlight.Controls(input.Stick, input.Up > 0.5f, input.Down > 0.5f, input.Effort);
        var s = Pigeon.ToState(_flight);
        // only a slow bird that is not flapping looks for a perch: no query in ordinary flight
        if (s.Mode == PigeonFlight.Mode.Air && !c.Flap && s.Velocity.Length() < PigeonFlight.PerchSpeed
            && Birds.BirdLife.Instance?.NearestPerch(GlobalPosition, PigeonFlight.PerchReach) is { } perch
            && PigeonFlight.Catches(s, c, GlobalPosition, perch.At))
        {
            GlobalPosition = perch.At;
            Pigeon.FromState(PigeonFlight.Perch(s), ref _flight);
            GD.Print($"[pigeon] perched on a {perch.Kind}");
        }

        _dropCooldown -= dt;
        bool fire = !UiFocus.TextEntryActive && PlayerInput.Held(PlayerInput.Fire);
        if (fire && !_dropHeld && _dropCooldown <= 0f)
        {
            _dropCooldown = (float)Birds.BirdNet.DropInterval;
            PigeonDrops++;
            Birds.BirdLife.Instance?.PlayerDrop(GlobalPosition + Vector3.Down * 0.05f, _flight.Velocity * 0.6f);
        }
        _dropHeld = fire;
    }

    /// <summary>
    /// First person (and always in VR): the camera at the bird's eye, level, turned with the heading —
    /// no bob, roll or pitch forced on a headset. False: the usual chase camera.
    /// </summary>
    private bool PigeonEye(float dt, Pigeon pigeon)
    {
        bool vr = XR.XrSession.Active;
        if (_camera == null || (_thirdPerson && !vr)) return false;
        _lookYaw = Mathf.MoveToward(_lookYaw, 0f, 1.2f * dt);
        var yaw = new Basis(Vector3.Up, _flight.Yaw);
        var basis = vr ? yaw : new Basis(Vector3.Up, _flight.Yaw + _lookYaw) * new Basis(Vector3.Right, _pitch);
        _camera.GlobalTransform = new Transform3D(basis, GlobalPosition + yaw * pigeon.FirstPersonEye);
        _camera.Fov = Mathf.Lerp(_camera.Fov, pigeon.BaseFov, MathX.Damp(3f, dt));
        return true;
    }

    /// <summary>VR snap turn as a pigeon (#217): turns a perched or walking bird; in the air the stick steers.</summary>
    public bool PigeonSnap(float angle)
    {
        if (_ride is not Pigeon || Pigeon.ModeOf(_flight) == PigeonFlight.Mode.Air) return false;
        _flight.Yaw += angle;
        return true;
    }
}
