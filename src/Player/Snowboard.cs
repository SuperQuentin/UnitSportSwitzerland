using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// A snowboard (#922, docs/plans/snowboard.md): both feet strapped across one board, on the ski
/// force model (<see cref="SkiDynamics"/>, <see cref="SkiDynamics.Tuning.Board"/>).
///
/// <para>
/// One edge carves at a time: leaning toward the chest is the toe edge, which holds more than the
/// heel edge behind. There is no plough: brake swings the board across the travel onto its heel
/// edge and skids to a stop (with a turn, onto the side turned to). The board is the same at both
/// ends, so spinning past a quarter turn at speed, or landing a jump backwards, rides on
/// <b>switch</b>: the tail becomes the nose and the rider, still facing the same way, now leads
/// with the other foot. Forward is a push of the free foot per press, shorter than a skate.
/// </para>
///
/// <para>
/// The stance (<see cref="Goofy"/>, <c>GameSettings.Goofy</c>) and <see cref="Switch"/> travel in
/// <c>FootPlayer.TuningBits</c> (<see cref="Bits"/>), so every peer draws the rider facing the
/// way its owner sees it.
/// </para>
/// </summary>
public sealed class Snowboard : SnowRide
{
    public override RideKind Kind => RideKind.Snowboard;
    public override string Label => "Snowboard";
    public override string Blurb =>
        "Gravity: {move_left}/{move_right} carve toe or heel edge, {brake} skid to a stop, tap {move_forward} to push, {jump} ollie, hold {tuck_boost} to straight-line";

    /// <summary>A board tips further than skis: the whole body leans over the edge. ~63 degrees.</summary>
    private const float MaxLean = 1.1f;
    /// <summary>Gentler than it was (#933), as the skis: a board comes round on its sidecut, not on a pivot.</summary>
    private const float MaxYawRate = 1.7f;
    private const float BankResponse = 4.5f;
    private const float SlowSpeed = 1.2f;

    /// <summary>The swing across the travel for a stop, rad/s; it stops at the quarter turn.</summary>
    private const float PivotRate = 3.2f;
    private const float PivotTo = 1.45f;

    private const float ArcadeMaxLean = 1.2f, ArcadeBankResponse = 5.5f, ArcadeMaxYawRate = 1.95f;

    /// <summary>The tuck (Shift) runs straight: steering only nudges the board.</summary>
    private const float TuckSteer = 0.3f;

    protected override float StrideTime => 0.5f;

    /// <summary>Right foot forward. The owner's setting; a remote copy has it from <see cref="Bits"/>.</summary>
    public bool Goofy { get; set; }

    /// <summary>Riding tail first, the other foot leading.</summary>
    public bool Switch { get; private set; }

    /// <summary>Stance and switch, as <c>FootPlayer.TuningBits</c> carries them: bit 0 goofy, bit 1 switch.</summary>
    public long Bits
    {
        get => (Goofy ? 1L : 0L) | (Switch ? 2L : 0L);
        set { Goofy = (value & 1) != 0; Switch = (value & 2) != 0; }
    }

    /// <summary>The rider's chest faces the right of the travel: regular forward, or goofy on switch.</summary>
    public bool FacingRight => Goofy == Switch;

    public override float EyeHeight => 1.42f;
    public override float ChaseDistance => 5.4f;
    public override float ChaseHeight => 1.9f;
    public override float MaxFov => 100f;
    public override float FovSpeed => 20f;
    public override float DismountSpeed => 3.0f;

    /// <summary>The live boarder (#933): toe and heel lean, the back foot pushing, the arms balancing.</summary>
    public override Node3D BuildVisual(int riderIndex, Outfit outfit = default) =>
        SnowRig.Boarder(HumanPalette.ForRider(riderIndex) with { Outfit = outfit }, SkiPalette.ForRider(riderIndex), FacingRight);

    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion)
    {
        bool arcade = Arcade;
        var tuning = arcade ? SkiDynamics.Tuning.BoardGame : SkiDynamics.Tuning.Board;
        float maxLean = arcade ? ArcadeMaxLean : MaxLean;

        // a skid stop: brake swings the board across the travel, to the side steered to, else
        // a heel-side stop (the chest turned to the travel), and holds it there at a quarter turn
        bool stop = ground.OnFloor && input.Brake > 0.3f && motion.Speed > 2f;
        if (stop)
        {
            float side = Mathf.Abs(input.Steer) > 0.3f ? -Mathf.Sign(input.Steer) : FacingRight ? 1f : -1f;
            float yawRate = Mathf.Abs(motion.Slip) < PivotTo ? side * PivotRate : 0f;
            motion.Yaw += yawRate * dt;
            motion.YawRate = yawRate;
            motion.Slip -= yawRate * dt;
            // leaning away from the slide onto the trailing edge (Slip + is travel to the left,
            // Bank − a lean to the right): facing the travel, that is the heel edge
            motion.Bank = Mathf.MoveToward(motion.Bank, -Mathf.Sign(motion.Slip) * maxLean, 4f * dt);
            motion.Lean = motion.Bank * 0.6f;
        }
        else
        {
            float before = motion.Yaw;
            float steer = input.Effort && ground.OnFloor ? input.Steer * TuckSteer : input.Steer;
            SteerByLean(ref motion, steer, motion.Speed, maxLean, arcade ? ArcadeMaxYawRate : MaxYawRate,
                arcade ? ArcadeBankResponse : BankResponse, SlowSpeed, dt);
            motion.Slip -= motion.Yaw - before;
        }

        float edge = stop ? 1f : Mathf.Abs(motion.Bank) / maxLean;
        float grip = SkiDynamics.BoardEdgeGrip(SkiDynamics.ToeSide(motion.Bank, FacingRight));
        Glide(input, ground, tuning, edge, stop ? 0f : input.Brake, grip, dt, ref motion);

        // tail first at speed (a spin, a backwards landing): the tail is the nose now
        if (!stop && SkiDynamics.Switch(ref motion.Yaw, ref motion.Slip, motion.Speed))
        {
            Switch = !Switch;
            State = SkiDynamics.State.From(motion.Speed, motion.Slip, State);
        }
    }
}
