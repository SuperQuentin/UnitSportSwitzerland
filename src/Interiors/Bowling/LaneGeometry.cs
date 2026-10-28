using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// Where things are on a lane pair (#1323), in the piece's own frame (centred, back to −Z, the pins
/// toward +Z): the one set of numbers the pair's mesh, its moving parts (<see cref="LaneRig"/>) and the
/// game's ball and pins all read, so they cannot drift apart. Mini lanes scale the lane's length, never
/// the pins or the ball. Numbers: <c>docs/notes/terrain/venue-bowling.md</c>.
/// </summary>
public readonly record struct LaneGeometry(float W, float D, float H, bool Mini)
{
    public const float HalfLane = BowlingSizes.HalfLane, Gutter = BowlingSizes.Gutter, PinSpacing = BowlingSizes.PinSpacing,
        PinRow = BowlingSizes.PinRow, PinHeight = BowlingSizes.PinHeight, PinRadius = BowlingSizes.PinRadius,
        BallRadius = BowlingSizes.BallRadius, LaneTop = BowlingSizes.LaneTop, DeckTop = BowlingSizes.DeckTop;

    public static LaneGeometry Of(FurniturePlan p) => new(p.W, p.D, p.H, p.D < 20f);

    public InteriorGenerator.LaneSpec Spec => Mini ? InteriorGenerator.MiniLanes : InteriorGenerator.FullLanes;
    /// <summary>The lane's length relative to a full lane: what mini lanes shrink.</summary>
    public float Scale => Mini ? InteriorGenerator.MiniLanes.Lane / InteriorGenerator.FullLanes.Lane : 1f;

    public float Front => -D / 2;
    public float Foul => Front + Spec.Approach;
    /// <summary>The far end of the lane: the pit's edge.</summary>
    public float LaneEnd => Foul + Spec.Lane;
    /// <summary>Where the pin deck starts: the last 0.9 m of the lane.</summary>
    public float DeckStart => LaneEnd - 0.9f * Scale;
    /// <summary>The head pin's spot, 0.868 m before the pit [S].</summary>
    public float HeadPin => LaneEnd - 0.868f * Scale;
    public float PitEnd => LaneEnd + 0.6f * Scale;

    /// <summary>The lane as the ball's run sees it, measured from the foul line (<see cref="BowlingBall"/>).</summary>
    public BowlingSizes.Run Run => new(HeadPin - Foul, LaneEnd - Foul, Scale);

    /// <summary>The masking unit's front face, and the opening under it that shows the pins.</summary>
    public float Masking => LaneEnd - 1.15f * Scale;
    public float MaskingBottom => Mini ? 0.6f : 0.85f;
    public float MaskingTop => Mathf.Min(H, Mini ? 2.4f : 3.3f);

    /// <summary>The two lanes' centres across the pair: ±0.8875 m at the regulation pitch.</summary>
    public float LaneX(int lane) => lane == 0 ? -W / 4 + 0.0375f : W / 4 - 0.0375f;

    /// <summary>
    /// Pin <paramref name="pin"/>'s spot (0 the head pin, 1-2 the second row left to right as the bowler
    /// sees it, 3-5, 6-9) on lane <paramref name="lane"/>, at the deck's top. Pin numbers are these plus
    /// one: the 7 pin far left, the 10 pin far right. The bowler faces +Z, so their left is +X.
    /// </summary>
    public Vector3 PinSpot(int lane, int pin)
    {
        int row = pin < 1 ? 0 : pin < 3 ? 1 : pin < 6 ? 2 : 3;
        int first = row * (row + 1) / 2, i = pin - first;
        return new Vector3(LaneX(lane) + (row / 2f - i) * PinSpacing * Scale, DeckTop, HeadPin + row * PinRow * Scale);
    }

    /// <summary>The score screens' bottom edge and their face, over the approach, facing back at the settees (−Z).</summary>
    public float ScreenY => Mathf.Min(2.5f, H - 0.7f);
    public float ScreenZ => Front + Spec.Approach * 0.4f;

    /// <summary>The ball return's hood at the head of the approach, between the two lanes.</summary>
    public float HoodZ => Front + 0.15f;
    /// <summary>The hood's rack holds this many balls: two rows of three behind the hood.</summary>
    public const int HoodSlots = 6;
    /// <summary>A waiting ball's centre on the hood's rack, slot 0..5: three across, the second row further on.</summary>
    public Vector3 HoodSlot(int slot) => new(-0.22f + slot % 3 * 0.22f, 0.62f + BallRadius - 0.02f, HoodZ + 0.86f + slot / 3 * 0.22f);
    /// <summary>Where a ball comes up out of the subway: the lift's mouth at the hood's back.</summary>
    public Vector3 HoodLift => new(0, 0.62f + BallRadius - 0.02f, HoodZ + 0.72f);
}
