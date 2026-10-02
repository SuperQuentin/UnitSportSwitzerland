using Godot;

namespace UnitSport.Player;

public partial class FootPlayer
{
    /// <summary>
    /// Where this body's ears are, facing where the body faces (#375): what the audio listener
    /// (<see cref="Audio.Ears"/>) follows, so a third-person or chase camera never moves the sound.
    /// In first person (and in VR) the camera is the head, so its own frame is used, head turns
    /// included; otherwise the eye height above the body, turned with the drawn figure, level.
    /// </summary>
    public Transform3D EarFrame
    {
        get
        {
            if (_camera != null && !_thirdPerson && _camera.IsInsideTree()) return _camera.GlobalTransform;
            // a passenger hears from the seat it is drawn in, on its host's vehicle
            var body = RidingWith != 0 && Host is { } host && IsInstanceValid(host) ? host : this;
            var facing = body._visual?.GlobalTransform.Basis ?? body.GlobalTransform.Basis;
            var fwd = -facing.Z;
            fwd.Y = 0;
            if (fwd.LengthSquared() < 1e-4f) fwd = -body.GlobalTransform.Basis.Z with { Y = 0 };
            if (fwd.LengthSquared() < 1e-4f) fwd = Vector3.Forward;
            float eye = _ride?.EyeHeight ?? (IsSliding ? SlideEyeHeight : EyeHeight);
            var head = body.GlobalPosition + Vector3.Up * eye;
            return new Transform3D(Basis.LookingAt(fwd.Normalized(), Vector3.Up), head);
        }
    }

    /// <summary>
    /// The body whose closed cabin these ears sit in (#375): oneself at the wheel of a car, truck,
    /// bus, helicopter or plane, its driver when riding along in one; null on foot or on an open
    /// machine. A speaker hung on that body (its car stereo) plays in the cabin; the rest of the
    /// world is heard through the shell.
    /// </summary>
    public FootPlayer? CabinOwner
    {
        get
        {
            var owner = RidingWith != 0 ? Host : (Ride != RideKind.OnFoot ? this : null);
            return owner != null && IsInstanceValid(owner) && ClosedCabin((RideKind)owner.RideKindId) ? owner : null;
        }
    }

    /// <summary>A machine you sit inside, behind glass: cars, trucks, buses and the two aircraft.</summary>
    public static bool ClosedCabin(RideKind kind) =>
        HasCarRadio(kind) || kind is RideKind.Helicopter or RideKind.Plane;
}
