using Godot;

namespace UnitSport.Player;

/// <summary>
/// Carried by something that is not a player's vehicle: the Battle Royale cargo plane (#207). While
/// <see cref="Carrier"/> returns a frame the body sits there with its collision off and no physics of
/// its own; its velocity is the carrier's, so the position others receive extrapolates along with it.
/// <see cref="Leap"/> lets go into a ride (the wingsuit) at a point and speed. Only on the authority.
/// </summary>
public partial class FootPlayer
{
    /// <summary>Where the carrier holds the body this frame (position, heading, velocity), or null once it lets go.</summary>
    public Func<(Vector3 At, float Yaw, Vector3 Velocity)?>? Carrier { get; set; }

    /// <summary>
    /// The body stays on show while carried: hanging from a zipline or on a ladder (#275), where the
    /// player's own camera still looks at it. The cargo plane leaves it false (inside the fuselage).
    /// </summary>
    public bool ShowWhileCarried { get; set; }

    /// <summary>The next landing does not hurt, however far the fall: thrown up by a trampoline (#275).</summary>
    public bool SoftLanding { get; set; }

    private bool _carried;

    /// <summary>
    /// Another peer's body that is stowed out of sight (in the cargo plane's hold): hidden and not solid
    /// here, whatever else would show it. Set by the Battle Royale on each client.
    /// </summary>
    public bool Stowed { get; set; }

    /// <summary>Called first in <c>_PhysicsProcess</c>: true while carried (and nothing else moves the body).</summary>
    private bool Carried()
    {
        if (Carrier?.Invoke() is not { } c)
        {
            if (_carried) Uncarry();
            return false;
        }
        if (!_carried)
        {
            if (Indoors) Interiors.InteriorManager.Instance?.Leave(this);
            if (_ride is { IsVehicle: true }) ExitVehicle();
            _carried = true;
            Visible = ShowWhileCarried;   // inside the fuselage: nothing to see, and the camera is the plane's
        }
        _body.Disabled = true;
        _placed = true;
        GlobalPosition = c.At;
        Rotation = new Vector3(0, c.Yaw, 0);
        Velocity = c.Velocity;
        return true;
    }

    private void Uncarry()
    {
        _carried = false;
        _body.Disabled = false;
        Visible = true;
    }

    /// <summary>
    /// Let go on foot at <paramref name="at"/> with <paramref name="velocity"/> (the end of a zipline, the
    /// top of a ladder, a jump off either): no ride change, and the fall counts from here.
    /// </summary>
    public void Release(Vector3 at, Vector3 velocity)
    {
        Carrier = null;
        ShowWhileCarried = false;
        if (_carried) Uncarry();
        GlobalPosition = at;
        Velocity = Vector3.Zero;
        MoveAndSlide();
        _fallSpeed = 0f;
        _placed = true;
        Velocity = velocity;
    }

    /// <summary>
    /// Out of the carrier: at <paramref name="at"/> with <paramref name="velocity"/>, in a <paramref name="ride"/>
    /// (the wingsuit; its Jump opens the parachute as on any base jump).
    /// </summary>
    public void Leap(Vector3 at, Vector3 velocity, RideKind ride)
    {
        Carrier = null;
        ShowWhileCarried = false;
        if (_carried) Uncarry();
        GlobalPosition = at;
        // no physics ran while carried, so the body still believes it stands where it boarded:
        // one still step in the air clears that, or the wingsuit would "land" at once (SPLAT at 3 km)
        Velocity = Vector3.Zero;
        MoveAndSlide();
        _fallSpeed = 0f;
        if (velocity.LengthSquared() > 1f) Rotation = new Vector3(0, Mathf.Atan2(-velocity.X, -velocity.Z), 0);
        ApplyRide(ride, velocity);
        DebugLaunch(at, velocity);
        Velocity = velocity;
        _viewYaw = Rotation.Y;
        Announced?.Invoke(ride == RideKind.Wingsuit ? "WINGSUIT" : "JUMP", true);
    }
}
