using Godot;

namespace UnitSport.Gpx;

public enum CameraMode
{
    Chase = 0,
    FirstPerson = 1,
    Cinematic = 2,
    Free = 3,
}

/// <summary>
/// Camera for track playback. Chase and first-person ride the avatar, cinematic stands
/// off and pans as the runner passes, and Free hands control back to the spectator cam.
/// </summary>
public partial class PlaybackCamera : Camera3D
{
    [Export] public CameraMode Mode { get; set; } = CameraMode.Chase;

    private RacePlayback _race = null!;
    private Vector3 _cinematicAnchor;
    private double _sinceAnchorPick = double.MaxValue;
    private float _yaw, _pitch;

    /// <summary>Distance at which the cinematic camera picks a fresh vantage point.</summary>
    private const float CinematicRange = 140f;

    public static PlaybackCamera Create(RacePlayback race) => new()
    {
        Name = "PlaybackCamera",
        _race = race,
        Near = 0.08f,
        Far = 20000f,
        Fov = 70f,
    };

    public void CycleMode()
    {
        Mode = (CameraMode)(((int)Mode + 1) % 4);
        if (Mode == CameraMode.Cinematic) _sinceAnchorPick = double.MaxValue;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Mode != CameraMode.Free) return;
        if (@event is InputEventMouseMotion m && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _yaw -= m.Relative.X * 0.0022f;
            _pitch = Mathf.Clamp(_pitch - m.Relative.Y * 0.0022f, -1.55f, 1.55f);
        }
    }

    public override void _Process(double delta)
    {
        var focused = _race.Focused;
        if (focused?.Avatar == null) return;

        var target = focused.Avatar.GlobalPosition;
        var heading = focused.Heading;
        float dt = (float)delta;

        switch (Mode)
        {
            case CameraMode.Chase:
            {
                // trail behind and above, easing so the view does not snap on corners
                var desired = target - heading * 7.5f + Vector3.Up * 3.0f;
                GlobalPosition = GlobalPosition.Lerp(desired, 1f - Mathf.Exp(-6f * dt));
                Aim(target + Vector3.Up * 1.2f);
                break;
            }

            case CameraMode.FirstPerson:
            {
                GlobalPosition = target + Vector3.Up * Runner.EyeHeight;
                Aim(GlobalPosition + heading);
                break;
            }

            case CameraMode.Cinematic:
            {
                _sinceAnchorPick += delta;
                // re-place once the runner has gone past, so the camera keeps "catching" them
                if (GlobalPosition.DistanceTo(target) > CinematicRange || _sinceAnchorPick > 14.0)
                {
                    _sinceAnchorPick = 0;
                    var side = new Vector3(-heading.Z, 0, heading.X);
                    float lateral = (GD.Randf() > 0.5f ? 1f : -1f) * (18f + GD.Randf() * 22f);
                    _cinematicAnchor = target + heading * 55f + side * lateral + Vector3.Up * (8f + GD.Randf() * 14f);
                }
                GlobalPosition = _cinematicAnchor;
                Aim(target + Vector3.Up * 1.0f);
                break;
            }

            case CameraMode.Free:
            {
                var basis = new Basis(Vector3.Up, _yaw) * new Basis(Vector3.Right, _pitch);
                Basis = basis;

                var move = Vector3.Zero;
                if (Input.IsPhysicalKeyPressed(Key.W)) move -= basis.Z;
                if (Input.IsPhysicalKeyPressed(Key.S)) move += basis.Z;
                if (Input.IsPhysicalKeyPressed(Key.A)) move -= basis.X;
                if (Input.IsPhysicalKeyPressed(Key.D)) move += basis.X;
                if (Input.IsPhysicalKeyPressed(Key.E)) move += Vector3.Up;
                if (Input.IsPhysicalKeyPressed(Key.Q)) move -= Vector3.Up;
                if (move != Vector3.Zero)
                {
                    float speed = Input.IsPhysicalKeyPressed(Key.Shift) ? 120f : 25f;
                    GlobalPosition += move.Normalized() * speed * dt;
                }
                break;
            }
        }
    }

    /// <summary>
    /// Points the camera at a world position. Avoids Camera3D.LookAt, which raises a Godot
    /// error when the target coincides with the camera — and an error raised inside a C#
    /// callback can crash the runtime via Godot's stack-capture path.
    /// </summary>
    private void Aim(Vector3 target)
    {
        var dir = target - GlobalPosition;
        if (dir.LengthSquared() < 1e-6f) return;
        dir = dir.Normalized();
        // guard the near-vertical case, where "up" stops defining a roll
        var up = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.999f ? Vector3.Forward : Vector3.Up;
        var right = up.Cross(dir).Normalized();
        Basis = new Basis(right, dir.Cross(right).Normalized(), -dir);
    }

    /// <summary>Called when switching into Free so it starts where the last view was.</summary>
    public void AdoptCurrentOrientation()
    {
        _yaw = Rotation.Y;
        _pitch = Rotation.X;
    }
}
