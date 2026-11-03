namespace UnitSport.Interiors;

/// <summary>
/// A ten-pin lane's fixed sizes (#1323) [S: <c>docs/notes/terrain/venue-bowling.md</c>], apart from
/// the plan so the pure parts of the game (<see cref="BowlingBall"/>, the score) stand alone and are
/// unit tested without the generator. <see cref="LaneGeometry"/> places them on a lane pair.
/// </summary>
public static class BowlingSizes
{
    /// <summary>Half the lane's playing surface (1.06 m) and a gutter's width.</summary>
    public const float HalfLane = 0.53f, Gutter = 0.2325f;
    /// <summary>Pins 0.305 m apart in an equilateral triangle, rows 0.264 m deep.</summary>
    public const float PinSpacing = 0.305f, PinRow = 0.264f;
    /// <summary>A pin's height, its widest radius, a ball's radius.</summary>
    public const float PinHeight = 0.381f, PinRadius = 0.0605f, BallRadius = 0.109f;
    /// <summary>The lane's top above the piece's floor, where a ball rolls and a pin stands.</summary>
    public const float LaneTop = 0.025f, DeckTop = 0.04f;

    /// <summary>
    /// A lane's length as the ball's run needs it, from the foul line: to the head pin and to the
    /// pit's edge, and its length relative to a full lane (mini lanes shrink it, and their oil).
    /// </summary>
    public readonly record struct Run(float HeadPin, float LaneEnd, float Scale)
    {
        /// <summary>A regulation lane: 18.29 m to the head pin, 19.2 m to the pit.</summary>
        public static Run Full => new(19.2f - 0.868f, 19.2f, 1f);
    }
}
