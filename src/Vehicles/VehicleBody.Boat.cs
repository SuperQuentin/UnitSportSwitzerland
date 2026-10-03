using Godot;
using UnitSport.Core;
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
    /// <summary>The hull's collision shape, its offset in the drawn boat's frame, and where it was last put (#378).</summary>
    private CollisionShape3D? _hull;
    private Vector3 _hullCentre;
    private Transform3D _hullPosed;
    /// <summary>The smallest turn of the box worth setting, squared (a basis axis's move): its far corner moves a centimetre.</summary>
    private float _hullTurn2;

    /// <summary>Waves smaller than this (summed amplitude where it floats, m) do not keep a boat awake.</summary>
    private const float CalmSwell = 0.06f;

    // ---- the mooring (#378) -------------------------------------------------------------------

    /// <summary>Where it is moored (the body's place, origin-free) and its heading there; null: it floats free.</summary>
    private GlobalPos? _mooredAt;
    private float _mooredYaw;
    /// <summary>Moors itself where it comes to rest (slower than <see cref="MoorBelow"/>) unless told otherwise.</summary>
    private bool _moorWhereItStops = true;
    private const float MoorBelow = 0.6f;

    /// <summary>
    /// Moors it (#378): from now on it is pulled softly back to <paramref name="at"/> (the body's place)
    /// and <paramref name="yaw"/> against the waves' drift (<see cref="BoatDynamics.Moor"/>), still
    /// riding the swell. A boat left in the world moors itself where it comes to rest; a berth
    /// (#377's landings) or an AI steamer stopping at one (#379) moors it where it must lie. The
    /// authority's call; taking the wheel ends it (the parked body goes).
    /// </summary>
    public void Moor(GlobalPos at, float yaw)
    {
        _mooredAt = at;
        _mooredYaw = yaw;
    }

    /// <summary>Lets it float free (it does not moor itself again).</summary>
    public void Unmoor()
    {
        _mooredAt = null;
        _moorWhereItStops = false;
    }

    /// <summary>Where it is moored in this peer's frame (the body's place), null when it floats free; for checks.</summary>
    public Vector3? MooringSpot => _mooredAt is { } at ? Origin.ToWorld(at) : null;

    private void BeginBoat(Boat boat, VehicleState s)
    {
        // from where the body was put in this peer's frame (_Ready: the state's GlobalPos, a hand's breadth up)
        boat.State = BoatState.At(Position + boat.Pivot, s.Yaw, s.Velocity);
        if (s.Angles != default) boat.State.Attitude = Quaternion.FromEuler(s.Angles);
        Tilt = boat.State.Attitude;
        _drawnTilt = Tilt;
        // left at rest: moored where it lies (one left running moors where it comes to a stop)
        if (MathX.FlatLength(s.Velocity) < MoorBelow) Moor(s.Position, s.Yaw);
        _hull = GetNodeOrNull<CollisionShape3D>("Hull");
        // where the shape was put in the level boat's frame: the box's centre, nothing for a shaped hull
        _hullCentre = _hull?.Position ?? Vector3.Zero;
        _hullPosed = _hull?.Transform ?? Transform3D.Identity;
        float reach = (boat.ParkedBox.Size * 0.5f).Length();
        _hullTurn2 = 0.01f / Mathf.Max(reach, 0.5f) * (0.01f / Mathf.Max(reach, 0.5f));
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
        if (_mooredAt == null && _moorWhereItStops && MathX.FlatLength(s.Velocity) < MoorBelow && _life > SettleTime)
            Moor(Origin.ToGlobal(GlobalPosition), Rotation.Y);
        if (_mooredAt is { } moored) BoatDynamics.Moor(ref s, Origin.ToWorld(moored) + boat.Pivot, _mooredYaw, dt);
        var wanted = (s.Position - start) / Mathf.Max(dt, 1e-4f);
        Velocity = wanted;
        MoveAndSlide();
        var real = GetRealVelocity();
        // a pier, the shore, another hull took what it could not keep (not in the first second:
        // that is the driver who just got off, still in the box)
        // (only ever slower: faster is the solver shoving it out of something, not motion, #303)
        if (_life > SettleTime && (wanted - real).LengthSquared() > 4f && real.Length() <= wanted.Length() + 0.5f) s.Velocity = real;
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
            PoseHull();
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
        PoseHull();
        if (_visual is BoatRig rig) rig.Water(speed, 1f, 0f, Heave < Boat.NoHeave * 0.5f);
        else if (_visual is SteamerRig steamer) steamer.Animate(0f, speed, Heave < Boat.NoHeave * 0.5f, false, 0f, DoorsOpen, dt);
    }

    /// <summary>
    /// The hull's collision box where the hull is drawn (#378): heaving, pitching and rolling with it
    /// on every peer, on its own copy of the waves (a headless one poses an empty frame the same
    /// way). A level box let the bow rise through a swimmer's head and left a player standing on
    /// air beside a rolled hull. Only the shape moves inside the body, which stays where it is: the
    /// body is not teleported, so Jolt has nothing to sweep. Set only when its middle or its far corner
    /// moved by a centimetre, so a boat asleep in a calm touches nothing.
    /// </summary>
    private void PoseHull()
    {
        if (_hull == null || _visual == null) return;
        var at = _visual.Transform * new Transform3D(Basis.Identity, _hullCentre);
        if (at.Origin.DistanceSquaredTo(_hullPosed.Origin) < 1e-4f
            && at.Basis.Y.DistanceSquaredTo(_hullPosed.Basis.Y) < _hullTurn2
            && at.Basis.Z.DistanceSquaredTo(_hullPosed.Basis.Z) < _hullTurn2) return;
        _hullPosed = at;
        _hull.Transform = at;
    }
}
