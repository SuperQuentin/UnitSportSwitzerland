namespace UnitSport.Terrain.Format;

/// <summary>
/// How a car chooses its lane and moves to it (#1134, the user: "cars should plan their lane changes more in advance, trying to only
/// change one lane at a time. approaching an intersection with multiple lanes in one direction, cars should try to fill them
/// equally, with a bias to the one that will minimise the lane changes after the intersection").
/// </summary>
public static class LaneChoice
{
    /// <summary>A lane's width as a lane change crosses it (m): one change moves the car this far at most.</summary>
    public const float LaneWidth = 3.2f;
    /// <summary>The least distance driven between two lane changes (m), unless the line is too near to wait.</summary>
    public const float MinGap = 35f;
    /// <summary>A lateral difference above this is a lane change (below: easing along a taper, done at once).</summary>
    public const float Change = 1.0f;
    /// <summary>A lane's place in the bias is worth this many queued cars.</summary>
    public const float BiasCars = 1.5f;

    /// <summary>
    /// Of lanes side by side making the same move to the same exit (<paramref name="lanes"/>: each its offset, m right of the road's
    /// line, and the cars already queued for it), the one to take: the fewest queued, plus <see cref="BiasCars"/> per place away from
    /// the side its next turn after the junction lies on (left: the leftmost, right: the rightmost; none or straight on: no bias); the
    /// car's habit breaks a tie. Its index in <paramref name="lanes"/>.
    /// </summary>
    public static int Pick(IReadOnlyList<(float Offset, int Queued)> lanes, SignalMoves nextTurn, uint habit)
    {
        if (lanes.Count == 0) return -1;
        var order = Enumerable.Range(0, lanes.Count).OrderBy(i => lanes[i].Offset).ToList();
        int best = -1;
        float bestScore = float.MaxValue;
        for (int rank = 0; rank < order.Count; rank++)
        {
            int i = order[rank];
            float bias = nextTurn == SignalMoves.Left ? rank : nextTurn == SignalMoves.Right ? order.Count - 1 - rank : 0;
            float score = lanes[i].Queued + BiasCars * bias + ((habit >> (rank % 16)) & 1) * 0.01f;
            if (score < bestScore) (bestScore, best) = (score, i);
        }
        return best;
    }

    /// <summary>
    /// Where a car moving over from <paramref name="lane"/> toward <paramref name="wanted"/> aims now (m right of its line): one lane at
    /// a time (<see cref="LaneWidth"/>), a new change only <see cref="MinGap"/> m after the last unless its line is so near
    /// (<paramref name="toLine"/>) that the changes left need every metre; a small difference (a taper) at once. Sets
    /// <paramref name="starts"/> where a change begins now.
    /// </summary>
    public static float Aim(float lane, float wanted, float stepTarget, float sinceLast, float toLine, out bool starts)
    {
        starts = false;
        float left = wanted - lane;
        if (Math.Abs(left) <= Change) return wanted;
        // a change under way: carry it on
        if (!float.IsNaN(stepTarget) && Math.Abs(stepTarget - lane) > 0.1f && Math.Sign(stepTarget - lane) == Math.Sign(left)) return stepTarget;
        int changes = (int)Math.Ceiling(Math.Abs(left) / LaneWidth - 0.25f);
        bool hurry = toLine < changes * MinGap;
        if (sinceLast < MinGap && !hurry) return lane;
        starts = true;
        return lane + Math.Sign(left) * Math.Min(Math.Abs(left), LaneWidth);
    }
}
