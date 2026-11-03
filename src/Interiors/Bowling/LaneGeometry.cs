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

    // ---- the ball return (#1336, the owner's pick): between the two lanes at the head of the approach, a hood over the lift
    // on the pin side, a double-rail track that runs from it toward the bowlers, curls round a bend and runs back, sloping
    // all the way, about a dozen balls queued nose to tail from the stop at its end

    /// <summary>The ball return's near end (its bend), at the head of the approach between the two lanes.</summary>
    public float HoodZ => Front + 0.15f;
    /// <summary>The track's two runs, either side of the return's middle (ball centres), m.</summary>
    public const float ReturnRun = 0.15f;
    /// <summary>The rails' top where a ball leaves the hood, and their fall per metre along the track.</summary>
    public const float ReturnTop = 0.80f, ReturnSlope = 0.035f;
    /// <summary>How high a ball's centre sits over its rails (the rails 0.14 m apart under a 0.109 m ball).</summary>
    public const float ReturnSeat = 0.085f;
    /// <summary>A queued ball's place along the track: a ball's width and a hair.</summary>
    public const float ReturnPitch = 0.222f;
    /// <summary>The balls the track holds.</summary>
    public const int HoodSlots = 12;
    /// <summary>The run out of the hood (+x side) starts here; the bend's centre; the run back (−x side) stops here.</summary>
    public float ReturnStart => HoodZ + 1.5f;
    public float ReturnBend => HoodZ + 0.3f;
    public float ReturnStop => HoodZ + 1.3f;
    /// <summary>The hood's box over the lift, from the track's start toward the pins.</summary>
    public float HoodFront => ReturnStart + 0.04f;
    public float HoodBack => ReturnStart + 0.5f;

    /// <summary>The track's length from the hood's mouth to the stop.</summary>
    public float ReturnLength => (ReturnStart - ReturnBend) + Mathf.Pi * ReturnRun + (ReturnStop - ReturnBend);

    /// <summary>
    /// A ball's centre <paramref name="s"/> m along the track from the hood's mouth: down the +x run toward the bowlers,
    /// round the bend, back up the −x run to the stop; lower all the way.
    /// </summary>
    public Vector3 ReturnPoint(float s)
    {
        s = Mathf.Clamp(s, 0, ReturnLength);
        float y = ReturnTop + ReturnSeat - ReturnSlope * s;
        float run = ReturnStart - ReturnBend, arc = Mathf.Pi * ReturnRun;
        if (s <= run) return new Vector3(ReturnRun, y, ReturnStart - s);
        if (s <= run + arc)
        {
            float a = (s - run) / ReturnRun;
            return new Vector3(ReturnRun * Mathf.Cos(a), y, ReturnBend - ReturnRun * Mathf.Sin(a));
        }
        return new Vector3(-ReturnRun, y, ReturnBend + (s - run - arc));
    }

    /// <summary>Where along the track queued ball <paramref name="slot"/> waits: slot 0 against the stop.</summary>
    public float ReturnSlotAt(int slot) => ReturnLength - BallRadius - 0.01f - slot * ReturnPitch;

    /// <summary>A waiting ball's centre, slot 0..<see cref="HoodSlots"/>−1: slot 0 at the stop, the queue back toward the hood.</summary>
    public Vector3 HoodSlot(int slot) => ReturnPoint(ReturnSlotAt(slot));

    /// <summary>Where a ball comes up out of the subway: the lift's mouth under the hood, at the track's start.</summary>
    public Vector3 HoodLift => ReturnPoint(0);
}
