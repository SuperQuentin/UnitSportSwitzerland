using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// What skis and a snowboard share (#922): the snow under them, gravity down the fall line, the
/// edges that carve or skid (<see cref="SkiDynamics"/>), the slopes' snow state, the lifts, the
/// slope races. Every check that used to ask "on skis?" asks <see cref="Is(RideKind)"/>.
///
/// <para>
/// And the body (#933): a live <see cref="SnowRig"/> whose pose follows what the rider does: the
/// tuck held, the plough, a push per stride, a landing taken in the knees, the arms out in the air.
/// The owner sends it as the ride pose (<see cref="WritePose"/>), so every peer draws the same.
/// </para>
/// </summary>
public abstract class SnowRide : Rideable
{
    /// <summary>Skis or a snowboard: a ride the slopes, lifts and slope races are for.</summary>
    public static bool Is(RideKind kind) => kind is RideKind.Skis or RideKind.Snowboard;

    public static bool Is(int kindId) => Is((RideKind)kindId);

    protected SkiDynamics.State State;

    /// <summary>Skidding in the last step (sound, spray).</summary>
    public bool Skidding => State.Skidding;

    /// <summary>How long one push is drawn, s.</summary>
    protected virtual float StrideTime => 0.42f;

    // ---- what the last step did, for the body ----
    private bool _tuck, _air, _wasAir;
    // strides taken, and drawn: physics steps and frames do not run one for one
    private int _strides, _drawnStrides;
    private float _ploughWanted;
    // ---- the body's pose, eased toward it ----
    private float _crouch, _plough, _absorb, _strideT = -1f;
    private int _strideLeg = 1;
    private Vector3 _eye = new(0, 1.4f, 0.3f);

    /// <summary>The figure's own eye, moved with its head (a tuck takes it down).</summary>
    public override Vector3 FirstPersonEye => _eye;

    /// <summary>The chase camera eases back with speed (#933): 3 m more by 25 m/s.</summary>
    public override float ChaseStretch(float speed) => Mathf.Clamp(speed * 0.12f, 0f, 3f);

    /// <summary>
    /// One step of the snow model after the heading was turned: the velocity from the motion, the
    /// air or the snow, and the velocity back. <paramref name="edge"/> is 0..1, how far the edges
    /// bite; <paramref name="gripScale"/> scales the snow's grip (a board's heel side holds less);
    /// <paramref name="plough"/> is how far the skis are drawn open in a V.
    /// </summary>
    protected void Glide(in RideInput input, in RideGround ground, in SkiDynamics.Tuning tuning, float edge, float brake,
        float gripScale, float dt, ref RideMotion motion, float plough = 0f)
    {
        State = SkiDynamics.State.From(motion.Speed, motion.Slip, State);
        if (!ground.OnFloor)
        {
            // in the air the board or skis are turned freely; the velocity is the jump's
            SkiDynamics.Air(ref State, tuning, input.Effort, dt);
        }
        else
        {
            bool onSnow = ground.Surface is Audio.Surface.Snow or Audio.Surface.Ice;
            var g = new SkiDynamics.Ground(onSnow, ground.SlopeAlong, ground.SlopeAcross, ground.SnowFriction, ground.SnowGrip * gripScale);
            float before = State.StrideCooldown;
            SkiDynamics.Step(ref State, tuning, g, new SkiDynamics.Input(input.Throttle, brake, input.Effort, edge), dt);
            // a stride sets the cooldown afresh
            if (State.StrideCooldown > before) _strides++;
        }
        motion.Speed = State.Speed;
        motion.Slip = State.Slip;
        _tuck = input.Effort;
        _air = !ground.OnFloor;
        _ploughWanted = plough;
    }

    public override void Animate(Node3D visual, in RideMotion motion, float dt)
    {
        if (visual is not SnowRig rig) return;
        _crouch = Mathf.MoveToward(_crouch, _tuck && !_air ? 1f : _air ? 0.3f : 0f, 4f * dt);
        _plough = Mathf.MoveToward(_plough, _ploughWanted, 5f * dt);
        if (_wasAir && !_air) _absorb = 1f;
        _wasAir = _air;
        _absorb = Mathf.MoveToward(_absorb, 0f, 2.5f * dt);
        if (_strides != _drawnStrides)
        {
            _drawnStrides = _strides;
            _strideT = 0f;
            _strideLeg = -_strideLeg;   // the legs take turns
        }
        else if (_strideT >= 0f)
        {
            _strideT += dt / StrideTime;
            if (_strideT >= 1f) _strideT = -1f;
        }
        float stride = _strideT < 0f ? 0f : _strideLeg * (1f + _strideT);
        rig.Show(new SnowPose.Look(_crouch, motion.Lean, _plough, stride, _absorb, _air));
        _eye = rig.Eye;
    }

    /// <summary>The pose for the other peers (<see cref="SnowPose.Pack"/>).</summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight)
    {
        return visual is SnowRig rig ? SnowPose.Pack(rig.Look) : default;
    }

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        if (visual is SnowRig rig) rig.Show(SnowPose.Unpack(pose));
    }
}
