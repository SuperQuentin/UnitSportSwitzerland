namespace UnitSport.Tools.RoadGen.Layout;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;

/// <summary>Who drives a connector: cars (and trucks), or riders on a bike lane.</summary>
public enum ConnectorKind : byte { Car, Bike }

/// <summary>
/// A lane-to-lane path through a junction core (#818, step 2): from an arriving lane's centre at its arm's mouth, along the
/// arm's heading, to a departing lane's centre at the exit's mouth, along the exit's. A turn is two straight runs joined by
/// a circular arc; a through move a straight line, an arc or a smooth S where the lanes do not line up. Points LV95.
/// </summary>
public sealed class Connector
{
    /// <summary>The arm it leaves (<see cref="ArmPlan.Arm"/>) and its lane's strip id there; the arm and lane it enters.</summary>
    public required int FromArm { get; init; }
    public required int FromId { get; init; }
    public required int ToArm { get; init; }
    public required int ToId { get; init; }
    public required SignalMoves Move { get; init; }
    public required ConnectorKind Kind { get; init; }
    /// <summary>Its width leaving its lane, and arriving in the exit's: the width eases from one to the other along it.</summary>
    public required double Width { get; init; }
    public required double ToWidth { get; init; }
    /// <summary>The lane's centre across its arm at each mouth (offsets right of a driver arriving on that arm).</summary>
    public required double FromOffset { get; init; }
    public required double ToOffset { get; init; }
    /// <summary>The least centre-line radius of its arc: its vehicle's turning radius on the inner edge plus half its width.</summary>
    public double MinRadius { get; set; }
    /// <summary>The widest centre-line radius whose arc fits between the two mouths (the user: every turn as wide as fits).</summary>
    public double WideRadius { get; set; }
    /// <summary>The centre-line radius laid; 0 for a straight line or an S.</summary>
    public double Radius { get; set; }
    /// <summary>Laid for a truck's radius (a turn between two Major roads with room for it).</summary>
    public bool Truck { get; init; }
    /// <summary>Its mouths leave no room for its vehicle's radius: laid as tight as fits.</summary>
    public bool UnderRadius { get; set; }
    /// <summary>1 as wide as fits, 0 at its least radius, below 0 under it (the room clipped the corner it passes).</summary>
    public double Fill { get; set; } = 1;
    /// <summary>How far its edges leave the core's outline at most (m; a check).</summary>
    public double Outside { get; set; }
    /// <summary>An S's straight runs along its lanes before it moves sideways, from each mouth (m).</summary>
    public double HoldIn { get; set; }
    public double HoldOut { get; set; }
    /// <summary>
    /// It turns (by <see cref="CorePlanner.TurnAngle"/> or more, whatever its move is called): its least radius sets the
    /// mouths; a straighter one fits between them.
    /// </summary>
    public bool Turns { get; set; }
    public List<Vec2> Centre { get; set; } = new();
    /// <summary>Its edges, left and right as seen travelling along it.</summary>
    public List<Vec2> Left { get; set; } = new();
    public List<Vec2> Right { get; set; } = new();
}

/// <summary>How a corner of the core's outline is shaped between one arm's right kerb and the next arm's left kerb.</summary>
public enum CornerShape : byte
{
    /// <summary>A kerb arc round the sidewalk's corner, tangent to both kerbs: the envelope of the connectors passing it.</summary>
    Arc,
    /// <summary>The kerbs meet at a point on the outside of a bend (the gap between the arms is over 180 degrees).</summary>
    Vertex,
    /// <summary>A straight kerb between the two mouths (arms in line, or kerbs that never meet near the junction).</summary>
    Chord,
    /// <summary>Two arms closer than <see cref="CorePlanner.GoreAngle"/>: their carriageways merge, no kerb between them.</summary>
    Gore,
}

/// <summary>A corner of the core between arm <see cref="ArmA"/>'s right kerb and arm <see cref="ArmB"/>'s left kerb (B the next arm counter-clockwise).</summary>
public sealed class CoreCorner
{
    public required int ArmA { get; init; }
    public required int ArmB { get; init; }
    public CornerShape Shape { get; set; }
    /// <summary>The gap between the two arms' headings, counter-clockwise from A to B (radians).</summary>
    public double Angle { get; set; }
    /// <summary>Where the two kerb lines meet.</summary>
    public Vec2 Meet { get; set; }
    /// <summary>The kerb arc's radius; at least <see cref="Floor"/>, the rounding of a corner no one turns round.</summary>
    public double Radius { get; set; }
    public double Floor { get; set; }
    /// <summary>No connector needs more than the least rounding: what #700 called a tight corner.</summary>
    public bool Tight => Shape == CornerShape.Arc && Radius <= Floor + 1e-6;
    /// <summary>Buildings beside the corner held the kerb arc smaller than its connectors wanted: they were narrowed.</summary>
    public bool RoomClipped { get; set; }
    /// <summary>The connector that rounds it most (for <c>--debug-junction</c>).</summary>
    public Connector? Bound { get; set; }
    /// <summary>Where the kerb leaves each arm's straight edge (stations along the arms).</summary>
    public double StationA { get; set; }
    public double StationB { get; set; }
    /// <summary>The kerb from arm A's straight edge to arm B's (an arc, a vertex, or nothing for a chord).</summary>
    public List<Vec2> Kerb { get; } = new();
}

/// <summary>An arm's mouth (#818): the line across its carriageway at a station; out from it the arm's, in from it the core's.</summary>
public sealed record CoreMouth(int Arm, double Station, Vec2 Left, Vec2 Right);

/// <summary>A line painted through the core, along a connector's edge (<see cref="CorePlanner"/>'s rules choose which).</summary>
public sealed record CoreLine(string Kind, List<Vec2> Points, bool Dashed);

/// <summary>
/// A junction's core (#818, step 2 of <c>docs/plans/junction-layout.md</c>): the mouths, the connectors for every allowed
/// move, the kerb corners they define, the outline and the lines painted through it.
/// </summary>
public sealed class JunctionCore
{
    public List<CoreMouth> Mouths { get; } = new();
    public List<Connector> Connectors { get; } = new();
    public List<CoreCorner> Corners { get; } = new();
    /// <summary>The paved outline, counter-clockwise: each mouth, then the corner after it.</summary>
    public List<Vec2> Outline { get; } = new();
    public List<CoreLine> Lines { get; } = new();
    /// <summary>Raised islands inside the core (a roundabout's splitter islands, #854), each a ring of points counter-clockwise.</summary>
    public List<List<Vec2>> Islands { get; } = new();
    /// <summary>Give-way rows inside the core (a roundabout's entries at the ring's edge, #854): from the arriving lanes' left to right.</summary>
    public List<(Vec2 Left, Vec2 Right)> Teeth { get; } = new();
    public double Area { get; set; }
    /// <summary>The surface: triangles over <see cref="Outline"/>'s points (as today's cap).</summary>
    public List<int> Triangles { get; } = new();
    /// <summary>The surface's height at each <see cref="Outline"/> point (m, LV95 heights as the arms').</summary>
    public List<double> Heights { get; } = new();
    /// <summary>The steepest grade along a connector, from its mouth's height to its exit's (0.05 is 5 %).</summary>
    public double SteepestGrade { get; set; }
    /// <summary>The outline does not cross itself (a check).</summary>
    public bool Simple { get; set; } = true;
    /// <summary>Why its builder could not lay it (a roundabout's arm running past its ring, #854); null when it could.</summary>
    public string? Refused { get; set; }
    /// <summary>Checks: kerbs whose curve starts outside their mouth; connectors whose edges leave the outline; mouths held at the most.</summary>
    public int KerbsNotTangent { get; set; }
    public int ConnectorsOutside { get; set; }
    public int MouthsAtMost { get; set; }
    /// <summary>Turns between two Major roads laid for a car after all: a truck's radius did not fit untouched (pairs of arms).</summary>
    public int TrucksAsCars { get; set; }
    /// <summary>Mouths held by their street's length (half of it where another junction stands at its far end): a multi-node core (phase 5).</summary>
    public int MouthsAtReach { get; set; }
    /// <summary>The arms whose mouth was held at half their street (another junction at its far end): there the two cores would overlap.</summary>
    public HashSet<int> ArmsAtReach { get; } = new();
    /// <summary>
    /// The plain core written where the planned one failed its checks (#854, the user, Oct 20): the same mouths and connectors,
    /// straight kerbs between the mouths pushed out to pave the connectors (<see cref="CorePlanner.Fallback"/>).
    /// </summary>
    public bool IsFallback { get; set; }
    /// <summary>Today's cap written by the planner (#854): its boundary, its mouths at today's trims; the sidewalk corners as today's.</summary>
    public bool IsCap { get; set; }
    /// <summary>Written as laid though it fails a check (a roundabout's core, #854): its checks no longer hold it back.</summary>
    public bool AsLaid { get; set; }
    /// <summary>A merged junction's core laid member by member (<see cref="MergedCore"/>, #854), not as one node's.</summary>
    public bool ByMembers { get; set; }
    /// <summary>How far out each arm's mouth would go unheld (its turns and kerbs, before its reach held it; #854).</summary>
    public Dictionary<int, double> Needs { get; } = new();
}
