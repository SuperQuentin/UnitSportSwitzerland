using Godot;

namespace UnitSport.Player;

/// <summary>
/// What skis and a snowboard share (#922): the snow under them, gravity down the fall line, the
/// edges that carve or skid (<see cref="SkiDynamics"/>), the slopes' snow state, the lifts, the
/// slope races. Every check that used to ask "on skis?" asks <see cref="Is(RideKind)"/>.
/// </summary>
public abstract class SnowRide : Rideable
{
    /// <summary>Skis or a snowboard: a ride the slopes, lifts and slope races are for.</summary>
    public static bool Is(RideKind kind) => kind is RideKind.Skis or RideKind.Snowboard;

    public static bool Is(int kindId) => Is((RideKind)kindId);

    protected SkiDynamics.State State;

    /// <summary>Skidding in the last step (sound, spray).</summary>
    public bool Skidding => State.Skidding;

    /// <summary>
    /// One step of the snow model after the heading was turned: the velocity from the motion, the
    /// air or the snow, and the velocity back. <paramref name="edge"/> is 0..1, how far the edges
    /// bite; <paramref name="gripScale"/> scales the snow's grip (a board's heel side holds less).
    /// </summary>
    protected void Glide(in RideInput input, in RideGround ground, in SkiDynamics.Tuning tuning, float edge, float brake,
        float gripScale, float dt, ref RideMotion motion)
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
            SkiDynamics.Step(ref State, tuning, g, new SkiDynamics.Input(input.Throttle, brake, input.Effort, edge), dt);
        }
        motion.Speed = State.Speed;
        motion.Slip = State.Slip;
    }
}
