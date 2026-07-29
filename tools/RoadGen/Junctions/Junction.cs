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
