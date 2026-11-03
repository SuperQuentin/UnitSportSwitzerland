namespace UnitSport.Tools.RoadGen.Layout;

using UnitSport.Terrain.Format;

/// <summary>
/// A turn pocket (#818, the user's generalization): lanes added to an approach for one move, opening on that move's side of
/// it. Left and right pockets follow one rule; what differs is this table. What a pocket displaces follows from its side,
/// not from the move, and the planner's general steps take care of it: a pocket on the centre side pushes the through lane
/// out (the exit's hatch lines it up again, a split lead-in shares it with the far side, a short street merges it with the far
/// junction's exit, #325); one on the kerb side meets the kerbside bike lane (layout a or b, #351).
/// </summary>
/// <param name="Move">The move its lanes make.</param>
/// <param name="CentreSide">It opens beside the centre line (a left pocket in right-hand traffic), else at the kerb.</param>
/// <param name="OverKmh">Built from the rules (no OSM lane data) only where the approach is faster than this (0: any speed).</param>
/// <param name="Sizes">(taper per lane, storage) in metres, tried longest first (#123, #348).</param>
/// <param name="FirstId">The strip id of its first lane, counting on per lane.</param>
/// <param name="MaxLanes">The most lanes OSM's <c>turn:lanes</c> may build it with (a double, a triple).</param>
/// <param name="DropStep">Its step in the room order (<see cref="MouthPlanner.Fit"/>).</param>
public sealed record PocketRule(SignalMoves Move, bool CentreSide, int OverKmh, (double Taper, double Storage)[] Sizes, int FirstId,
    int MaxLanes, string DropStep)
{
    public bool Owns(int id) => id >= FirstId && id < FirstId + MaxLanes;
}

public static class PocketRules
{
    /// <summary>The left pocket (#123, #700 double left).</summary>
    public static readonly PocketRule Left = new(SignalMoves.Left, CentreSide: true, OverKmh: 0, [(30, 40), (25, 30), (20, 20)],
        FirstId: 100, MaxLanes: 3, DropStep: "7 left pocket");

    /// <summary>The right pocket (#348; #711: only above 50 km/h; #818: double rights from OSM data too).</summary>
    public static readonly PocketRule Right = new(SignalMoves.Right, CentreSide: false, OverKmh: 50, [(15, 30), (10, 25), (10, 15)],
        FirstId: 120, MaxLanes: 3, DropStep: "4 right pocket");

    public static readonly PocketRule[] All = [Left, Right];

    /// <summary>The pocket a strip id belongs to, null for any other strip.</summary>
    public static PocketRule? Of(int id) => Left.Owns(id) ? Left : Right.Owns(id) ? Right : null;
}
