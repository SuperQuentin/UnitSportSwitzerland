namespace UnitSport.Terrain.Format;

/// <summary>
/// Where the lanes lie inside a carriageway (#117). The network stage sizes every carriageway
/// with these rules and stores the result (<see cref="RoadSegment.Width"/> = the paved width,
/// <see cref="RoadAttributes.LanesForward"/>/<see cref="RoadAttributes.LanesBackward"/>); the
/// paint layer and traffic read the same rules back, so lanes, markings and cars agree.
///
/// <para>
/// A one-way carriageway of a motorway, expressway or ramp, in its direction of travel:
/// inner margin (left) + lanes + hard shoulder (right). Anything else: the lanes centred in the
/// paved width, which is TLM's nominal class width (or OSM's) and so not built from lanes.
/// Notes: docs/notes/tools/road-widths-lanes-oneway.md.
/// </para>
/// </summary>
public static class RoadCrossSection
{
    public static float LaneWidth(RoadClass c) => c switch
    {
        RoadClass.Motorway or RoadClass.Ramp => 3.75f,
        RoadClass.Expressway => 3.5f,
        _ => 3.0f,
    };

    /// <summary>Paved strip left of the fast lane (one-way carriageways of the high-speed classes).</summary>
    public static float InnerMargin(RoadClass c) => IsHighSpeed(c) ? 0.5f : 0f;

    /// <summary>Hard shoulder right of the slow lane.</summary>
    public static float Shoulder(RoadClass c) => c switch
    {
        RoadClass.Motorway => 2.5f,
        RoadClass.Expressway or RoadClass.Ramp => 1.5f,
        _ => 0f,
    };

    public static bool IsHighSpeed(RoadClass c) => c is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp;

    /// <summary>Paved width of one direction's carriageway of a high-speed road with <paramref name="lanes"/> lanes.</summary>
    public static float OneWayWidth(RoadClass c, int lanes) => InnerMargin(c) + lanes * LaneWidth(c) + Shoulder(c);

    /// <summary>Paved width of an undivided high-speed road: lanes each way plus a margin on both edges.</summary>
    public static float TwoWayWidth(RoadClass c, int lanesEach) => 2 * lanesEach * LaneWidth(c) + 2 * InnerMargin(c);

    /// <summary>Lanes in one direction when nothing says otherwise.</summary>
    public static int DefaultLanes(RoadClass c, bool oneWayCarriageway) => c switch
    {
        // a motorway drawn as one line is a two-way bore or a short undivided stretch: 1 + 1
        RoadClass.Motorway or RoadClass.Expressway => oneWayCarriageway ? 2 : 1,
        RoadClass.Ramp or RoadClass.Major or RoadClass.Road or RoadClass.Minor or RoadClass.Lane
            or RoadClass.Square => 1,
        _ => 0,   // tracks, paths, links: no lanes
    };

    /// <summary>
    /// Lateral offset, metres right of the centreline in the direction of travel, of the centre of
    /// the rightmost lane of a one-way carriageway. 0 when the lanes do not fit the width, which is
    /// what a v1/v2 tile's narrow divided carriageway gives: traffic then keeps to the centreline.
    /// </summary>
    public static float RightLaneOffset(RoadClass c, float width, int lanes)
    {
        if (lanes <= 0) lanes = Math.Max(1, DefaultLanes(c, true));
        float lane = LaneWidth(c), block = lanes * lane;
        if (block > width + 0.01f) return 0f;
        float margin = IsHighSpeed(c) ? Math.Min(InnerMargin(c), width - block) : (width - block) * 0.5f;
        return -width * 0.5f + margin + block - lane * 0.5f;
    }
}
