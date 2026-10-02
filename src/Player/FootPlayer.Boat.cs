using Godot;
using UnitSport.Core;
using UnitSport.Vehicles;
using UnitSport.World;

namespace UnitSport.Player;

/// <summary>
/// At the helm of a boat (#302): the boat owns its 3D motion (<see cref="BoatDynamics"/> against the
/// shared wave field); the body carries it through the world as a flight does, upright and
/// yaw-only, and reports what it hit; the drawn boat takes the attitude. The capsule's origin is the
/// keel under the centre of mass, so it meets the lake bed where the hull does.
/// </summary>
public partial class FootPlayer
{
    private readonly BoatWater _boatWater = new();

    /// <summary>The boat being driven's live state, for checks and the feel layer (default when not in one).</summary>
    public BoatState BoatMotion => (_ride as Boat)?.State ?? default;

    /// <summary>A boat throws a hull this fast into something solid apart, m/s: a crash, the boat wrecked.</summary>
    private const float BoatCrashSpeed = 13f;

    /// <summary>Starts a boat's motion where the body stands (from the picker, getting in, a probe).</summary>
    private void BeginBoat(Boat boat, Vector3 velocity, Vector3 attitude = default)
    {
        // picked standing in the water (on the bed, or wading): it starts afloat at the surface
        if (WaterField.TryLevelAt(GlobalPosition, out float level) && level - 0.3f > GlobalPosition.Y)
            GlobalPosition = GlobalPosition with { Y = level - 0.3f };
        boat.State = BoatState.At(GlobalPosition + boat.Pivot, Rotation.Y, velocity);
        if (attitude != default) boat.State.Attitude = Quaternion.FromEuler(attitude);
        boat.Heave = Boat.NoHeave;
    }

    /// <summary>For probes: puts the boat being driven at <paramref name="body"/> heading <paramref name="yaw"/>, at rest or moving.</summary>
    public void PlaceBoat(Vector3 body, float yaw, Vector3 velocity = default)
    {
        if (_ride is not Boat boat) return;
        GlobalPosition = body;
        Rotation = new Vector3(0, yaw, 0);
        _placed = true;
        Velocity = velocity;
        boat.State = BoatState.At(body + boat.Pivot, yaw, velocity);
        _settle = 0.3f;
    }

    /// <summary>One physics step at the helm. See the class summary.</summary>
    private void BoatPhysics(float dt, Boat boat)
    {
        var stick = PlayerInput.Move;
        var input = RideControls?.Invoke() ?? new RideInput(
            Throttle: Mathf.Max(PlayerInput.Strength(PlayerInput.Throttle), Mathf.Max(0f, -stick.Y)),
            Brake: Mathf.Max(PlayerInput.Strength(PlayerInput.Brake), Mathf.Max(0f, stick.Y)),
            Steer: SteerInput(),
            Effort: false);
        // nobody at the helm (the driver jumped out, #158), or the engine off: it drifts
        if (SeatIndex != 0 || !EngineOn) input = new RideInput(0f, 0f, SeatIndex != 0 ? 0f : input.Steer, false);
        LastRideInput = input;
        var controls = new BoatControls(input.Throttle, input.Brake, input.Steer);
        boat.Controls = controls;

        // the model starts from where the body is (a teleport, the floating origin, the solver's push)
        ref var s = ref boat.State;
        var start = GlobalPosition + boat.Pivot;
        s.Position = start;
        bool wasAirborne = s.Airborne > 0.25f;
        _boatWater.Terrain = Terrain;
        _boatWater.Begin(GlobalPosition, WaterField.Now);
        var ev = BoatDynamics.Step(boat.Spec, ref s, controls, _boatWater, dt);

        // the body follows the model's path through the world, and the world has its say
        var wanted = (s.Position - start) / Mathf.Max(dt, 1e-4f);
        Velocity = wanted;
        MoveAndSlide();
        var real = GetRealVelocity();
        var lost = wanted - real;
        s.Position = GlobalPosition + boat.Pivot;
        if (_settle > 0f)
        {
            _settle -= dt;
            // the solver easing a fresh hull out of whatever it was put down on is not a crash
            if (lost.LengthSquared() > 1f) s.Velocity = real;
        }
        else if (lost.LengthSquared() > 4f)
        {
            // a pier, the shore's rocks, another hull: what it took off is the impact
            float impact = lost.Length();
            s.Velocity = real;
            if (impact > BoatCrashSpeed) { Impacted?.Invoke(impact); WreckVehicle(); return; }
            if (impact > 4f)
            {
                VehicleHealth -= (impact - 4f) * 10f;
                Impacted?.Invoke(impact);
                PlayerInput.Rumble(0.4f, Mathf.Clamp(impact / 12f, 0.2f, 1f), 0.15f);
                if (VehicleHealth <= 0f) { WreckVehicle(); return; }
            }
        }

        // the keel on a beach or a slipway: the capsule holds the body up there, so the hull drags on it
        if (IsOnFloor()) BoatDynamics.Beached(ref s, dt);
        float yaw = s.Yaw(Rotation.Y);
        Rotation = new Vector3(0, yaw, 0);
        boat.MeasureHeave(GlobalPosition, yaw);
        // the motion everything else reads: speed, heading, turn, bank (the chase camera, the HUD, the feel layer)
        _motion.Speed = new Vector2(s.Velocity.X, s.Velocity.Z).Length();
        _motion.YawRate = s.Spin.Y;
        _motion.Yaw = yaw;
        _motion.Lean = -s.Roll * 0.5f;
        _motion.Bank = 0f;
        _motion.Slip = 0f;

        // a landing the feel layer hears
        if (wasAirborne && s.Airborne <= 0f && s.LastLanding > 1.5f)
        {
            Landed?.Invoke(s.LastLanding);
            PlayerInput.Rumble(0.3f, Mathf.Clamp(s.LastLanding / 8f, 0.1f, 1f), 0.15f);
        }
        if (ev == BoatEvent.Thrown)
        {
            ThrownFromBoat(boat);
            return;
        }
        if (_visual != null) boat.Pose(_visual, yaw, s.Attitude);
        UpdateRideCamera(dt);
        // first person: from the helm, pitching and rolling with the hull (the horizon only half)
        if (!_thirdPerson && _camera != null && _visual != null && SeatIndex == 0)
        {
            _camera.Position = _visual.Transform * boat.FirstPersonEye;
            _camera.Rotation = new Vector3(_pitch + s.Pitch * 0.5f, _lookYaw, -s.Roll * 0.5f);
        }
    }

    /// <summary>
    /// A hard landing or a flip throws the jetski's rider (and anyone behind them) into the water;
    /// the machine floats on riderless, its engine cut by the lanyard, and is claimed back like any
    /// parked vehicle.
    /// </summary>
    private void ThrownFromBoat(Boat boat)
    {
        var state = CaptureVehicle(wrecked: false) with { EngineOn = false };
        Announced?.Invoke("THROWN OFF!", false);
        PlayerInput.Rumble(0.8f, 0.8f, 0.4f);
        Impacted?.Invoke(Mathf.Max(boat.State.LastLanding, 6f));
        if (OnlineSeats && (SeatIndex > 0 || Riders.Any())) PassengerService.Instance!.Wrecked(state.Velocity * 0.5f);
        SeatIndex = 0;
        Vehicles?.Park(state);
        var right = GlobalTransform.Basis.X with { Y = 0 };
        right = right.LengthSquared() > 1e-6f ? right.Normalized() : Vector3.Right;
        var at = state.Position + right * (boat.ParkedBox.Size.X * 0.5f + BodyRadius + 0.6f);
        ApplyRide(RideKind.OnFoot, Vector3.Zero);
        IntoWater(at, state.Velocity with { Y = 0 } * 0.4f);
        _stunTimer = 1f;
        GD.Print($"[boat] {Name} thrown off the {boat.Label} (landing {boat.State.LastLanding:F1} m/s, capsized {boat.State.Capsized:F1} s)");
    }

    /// <summary>
    /// Puts a player who left a boat in the water at <paramref name="at"/>: swimming at the surface
    /// (#301) with what is left of the boat's way; on foot there where the water has no layer.
    /// </summary>
    private void IntoWater(Vector3 at, Vector3 velocity)
    {
        if (StartSwimmingAtSurface(at)) { Velocity = velocity; return; }
        if (WaterField.TryLevelAt(at, out float level)) at.Y = Mathf.Max(at.Y, level - 0.2f);
        GlobalPosition = at;
    }
}
