namespace UnitSport.Tools.RoadGen.Network;

using UnitSport.Tools.RoadGen.Geometry;

/// <summary>Which end of a link meets a node.</summary>
public enum LinkEnd { Start, End }

/// <summary>One link arriving at a node, with the heading it arrives on.</summary>
/// <param name="OutwardHeading">
/// Heading pointing <i>away</i> from the node along the link. Everything in the junction
/// solver is expressed outward so both ends of a link can be treated identically.
/// </param>
public readonly record struct Approach(int LinkId, LinkEnd End, double OutwardHeading);

/// <summary>
/// A stretch of road between two nodes. The raw centreline is what came in; the alignment is
/// the smoothed version; the trims are how much the junction solver ate off each end.
/// </summary>
public sealed class RoadLink
{
    public required int Id { get; init; }
    public required List<Vec2> Centreline { get; set; }
    public required RoadProfile Profile { get; init; }

    /// <summary>
    /// Grade separation. 0 is ground level, +1 a bridge, -1 a tunnel. Links on different
    /// layers never share a node and are never split against each other.
    /// </summary>
    public int Layer { get; init; }

    public int StartNode { get; set; } = -1;
    public int EndNode { get; set; } = -1;

    /// <summary>
    /// Caller payload, carried through splits. The rewrite pass uses it to find the source
    /// segment a link came from — for its heights, and for which tile to write it back to.
    /// </summary>
    public object? Tag { get; init; }

    /// <summary>
    /// False for bridges and tunnels. Their plan-view line is load-bearing elsewhere: the
    /// tunnel carve mask and the bridge piers were both derived from it, so moving it by even
    /// a corner radius would leave a bore beside its own hole. They still take junction trims —
    /// only the shape is frozen.
    /// </summary>
    public bool AllowSmoothing { get; init; } = true;

    public Alignment? Alignment { get; set; }
    public double TrimStart { get; set; }
    public double TrimEnd { get; set; }

    public Vec2 First => Centreline[0];
    public Vec2 Last => Centreline[^1];

    /// <summary>Remaining length once both junctions have taken their share.</summary>
    public double UsableLength => Alignment is null ? 0 : Math.Max(0, Alignment.Length - TrimStart - TrimEnd);
}

public sealed class RoadNode
{
    public required int Id { get; init; }
    public Vec2 Position { get; set; }
    public int Layer { get; init; }
    public List<Approach> Approaches { get; } = new();

    public int Degree => Approaches.Count;

    /// <summary>A node that only continues one road into another is not a junction.</summary>
    public bool IsJunction => Degree >= 3;
}

public sealed class RoadNetwork
{
    public List<RoadLink> Links { get; } = new();
    public List<RoadNode> Nodes { get; } = new();

    public RoadLink AddLink(List<Vec2> centreline, RoadProfile profile, int layer = 0,
        object? tag = null, bool allowSmoothing = true)
    {
        var link = new RoadLink
        {
            Id = Links.Count,
            Centreline = centreline,
            Profile = profile,
            Layer = layer,
            Tag = tag,
            AllowSmoothing = allowSmoothing,
        };
        Links.Add(link);
        return link;
    }

    /// <summary>Splits inherit everything but the geometry.</summary>
    public RoadLink AddSplit(RoadLink parent, List<Vec2> centreline) =>
        AddLink(centreline, parent.Profile, parent.Layer, parent.Tag, parent.AllowSmoothing);

    public IEnumerable<RoadNode> Junctions => Nodes.Where(n => n.IsJunction);
}
