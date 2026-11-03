namespace UnitSport.Tools.RoadGen.Junctions;

using UnitSport.Tools.RoadGen.Geometry;

/// <summary>One arm of a junction, after trimming.</summary>
/// <param name="Trim">How far back along the link the ribbon now stops.</param>
/// <param name="Left">Left corner of the trimmed ribbon end, looking outward.</param>
/// <param name="Right">Right corner of the trimmed ribbon end, looking outward.</param>
public readonly record struct JunctionArm(
    int LinkId,
    double OutwardHeading,
    double HalfWidth,
    double Trim,
    Vec2 Left,
    Vec2 Right,
    bool TrimWasClamped);

/// <summary>
/// The paved area where several roads meet, as an explicit piece of geometry.
///
/// <para>
/// Making the junction a real object is the whole fix. ASAM OpenDRIVE reaches the same
/// conclusion from the other direction: connecting roads inside a junction are singled out
/// as the only roads in the entire standard whose surfaces are allowed to overlap. Everywhere
/// else, roads meet at a junction boundary and stop. If a renderer instead draws every
/// centreline to full length, the overlap is not a special case — it is at every single
/// intersection, and no depth bias will make four carriageways painted on top of each other
/// into a junction.
/// </para>
/// </summary>
public sealed class Junction
{
    public required int NodeId { get; init; }
    public required Vec2 Centre { get; init; }
    public required int Layer { get; init; }
    public List<JunctionArm> Arms { get; } = new();

    /// <summary>
    /// A junction merged from several nodes (#818 phase 5c, made by the planner only): every member's node id, <see cref="NodeId"/>
    /// one of them; its arms are the members' arms but the short streets between them. Null for a junction of one node.
    /// </summary>
    public IReadOnlySet<int>? Members { get; init; }

    /// <summary>
    /// A tight roundabout laid whole (#854): its ring as <c>RoundaboutShaper</c> fitted it, the ring's nodes and its splitter
    /// islands' tips its <see cref="Members"/>. Null for any other junction.
    /// </summary>
    public Network.RoundaboutShaper.Ring? Ring { get; init; }

    /// <summary>Whether <paramref name="node"/> is this junction's node or one of its members.</summary>
    public bool Holds(int node) => node == NodeId || Members?.Contains(node) == true;

    /// <summary>Corners left tight (#700): corner i lies between arm i's left edge and the next arm's right edge; no right turn rounds it.</summary>
    public HashSet<int> TightCorners { get; } = new();

    /// <summary>
    /// Where an arm's kerb ends beside a tight corner (#700): metres inside its mouth, along the arm, on its left edge
    /// (<c>true</c>) or right edge (<c>false</c>) looking outward. Absent: at the mouth.
    /// </summary>
    public Dictionary<(int Arm, bool Left), double> KerbInset { get; } = new();

    /// <summary>Boundary ring, counter-clockwise, closed implicitly.</summary>
    public List<Vec2> Boundary { get; } = new();

    /// <summary>Triangle fan indices into <see cref="Vertices"/>.</summary>
    public List<int> Triangles { get; } = new();

    /// <summary>Vertex 0 is the centre; the rest is <see cref="Boundary"/>.</summary>
    public List<Vec2> Vertices { get; } = new();

    public double Area
    {
        get
        {
            double sum = 0;
            for (int i = 0; i < Boundary.Count; i++)
            {
                var a = Boundary[i];
                var b = Boundary[(i + 1) % Boundary.Count];
                sum += a.Cross(b);
            }
            return Math.Abs(sum) * 0.5;
        }
    }
}
