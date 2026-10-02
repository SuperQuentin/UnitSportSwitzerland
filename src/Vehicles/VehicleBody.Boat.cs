using Godot;
using UnitSport.Avatar;
using UnitSport.Player;
using UnitSport.World;

namespace UnitSport.Vehicles;

/// <summary>
/// A boat left in the world (#302): it floats and drifts on the waves under its own model with
/// nobody at the helm, runs aground, and sleeps once it lies still on water too flat to move it.
/// Copies draw it on their own copy of the waves at the authority's height over them.
/// </summary>
public partial class VehicleBody
{
    private readonly BoatWater _boatWater = new();
    private Quaternion _drawnTilt = Quaternion.Identity;
    private bool _boatCalm;

    /// <summary>Waves smaller than this (summed amplitude where it floats, m) do not keep a boat awake.</summary>
    private const float CalmSwell = 0.06f;

    private void BeginBoat(Boat boat, VehicleState s)
    {
        // the body arrives a hand's breadth up (see _Ready): the boat starts from there, as it was
        boat.State = BoatState.At(s.Position + Vector3.Up * 0.15f + boat.Pivot, s.Yaw, s.Velocity);
        if (s.Angles != default) boat.State.Attitude = Quaternion.FromEuler(s.Angles);
        Tilt = boat.State.Attitude;
        _drawnTilt = Tilt;
    }

    private void StepBoat(float dt, Boat boat)
    {
        ref var s = ref boat.State;
        var start = GlobalPosition + boat.Pivot;
        s.Position = start;
        _boatWater.Terrain = Terrain;
        _boatWater.Begin(GlobalPosition, WaterField.Now);
        // nobody at the helm: no throttle, the helm where it was left
        BoatDynamics.Step(boat.Spec, ref s, default, _boatWater, dt);
        var wanted = (s.Position - start) / Mathf.Max(dt, 1e-4f);
        Velocity = wanted;
        MoveAndSlide();
        var real = GetRealVelocity();
        // a pier, the shore, another hull took what it could not keep (not in the first second:
        // that is the driver who just got off, still in the box)
        if (_life > SettleTime && (wanted - real).LengthSquared() > 4f) s.Velocity = real;
        s.Position = GlobalPosition + boat.Pivot;
        if (IsOnFloor()) BoatDynamics.Beached(ref s, dt);
        float yaw = s.Yaw(Rotation.Y);
        Rotation = new Vector3(0, yaw, 0);
        Tilt = s.Attitude;
        boat.MeasureHeave(GlobalPosition, yaw);
        Heave = boat.Heave;
        _boatCalm = s.Velocity.LengthSquared() < 0.03f && s.Spin.LengthSquared() < 0.003f && Swell(GlobalPosition) < CalmSwell;
    }

    /// <summary>The summed amplitude of the waves where a hull floats, m (0 with no water).</summary>
    private static float Swell(Vector3 at)
    {
        if (!WaterField.TryGetStill(at, out _, out float scale)) return 0f;
        float sum = 0f;
        foreach (float a in WaterField.Amplitudes) sum += a;
        return sum * scale;
    }

    /// <summary>
    /// Every frame where it is drawn: the authority from its live state; a copy from the replicated
    /// attitude (eased between the 20 Hz states) and its own waves plus the authority's height over
    /// them, so a parked boat bobs on the very waves this peer draws.
    /// </summary>
    private void DrawBoat(Boat boat, float dt)
    {
        if (_visual == null) return;
        float speed = Velocity.Length();
        if (IsMultiplayerAuthority())
        {
            boat.Pose(_visual, Rotation.Y, boat.State.Attitude);
            // the frame its deck is walked in stands where the model is now (#303): from the first pose
            Posed = true;
            if (_visual is BoatRig own) own.Water(speed, boat.State.Wet, 0f, boat.State.Airborne <= 0.1f);
            else if (_visual is SteamerRig ownSteamer)
                ownSteamer.Animate(boat.State.Shaft, speed, boat.State.Airborne <= 0.1f && boat.State.Wet > 0.05f, false, 0f, DoorsOpen, dt);
            return;
        }
        _drawnTilt = _drawnTilt.Slerp(Tilt.Normalized(), 1f - Mathf.Exp(-10f * dt));
        boat.Pose(_visual, Rotation.Y, _drawnTilt);
        float y = boat.RemoteY(GlobalPosition, Rotation.Y, Heave);
        _visual.Position += Vector3.Up * (y - GlobalPosition.Y);
        Posed = true;
        if (_visual is BoatRig rig) rig.Water(speed, 1f, 0f, Heave < Boat.NoHeave * 0.5f);
        else if (_visual is SteamerRig steamer) steamer.Animate(0f, speed, Heave < Boat.NoHeave * 0.5f, false, 0f, DoorsOpen, dt);
    }
}
