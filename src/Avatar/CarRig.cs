using Godot;

namespace UnitSport.Avatar;

/// <summary>The body shape a <see cref="CarRig"/> builds.</summary>
public enum CarStyle { Coupe86, RotaryFd, Rally4wd }

/// <summary>
/// A drivable car's visual: body, four wheels that turn and steer, brake lights. Origin on the
/// ground under the middle of the wheelbase, facing −Z like every node (built +Z, turned by
/// <see cref="MeshScratch.Build"/>).
/// </summary>
public partial class CarRig : Node3D
{
    /// <summary>Front road-wheel angle, radians, + = left.</summary>
    public float SteerAngle { get; set; }
    /// <summary>Accumulated wheel rotation, radians; positive rolls forward.</summary>
    public float WheelSpin { get; set; }
    /// <summary>Body pitch, radians, + = nose up (squat under power, dive under braking is −).</summary>
    public float BodyPitch { get; set; }
    public bool BrakeLights { get; set; }

    public static CarRig Create(CarStyle style, Color paint) => new() { Name = "Car" };
}
