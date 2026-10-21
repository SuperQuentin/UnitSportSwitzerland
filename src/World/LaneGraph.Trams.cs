using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// The tram graph (#1057): the tram tracks of the tiles (<see cref="RoadFlags.Tramway"/>, left out of the rails), each track the way
/// its trams run (<see cref="TramTracks.Directions"/>: TLM maps a line's tracks one by one, the trams keep right), on its own
/// line; a line mapped as one with two tracks (<see cref="RoadFlags.DoubleTrack"/>) both ways, a tram on its right track as a train.
/// </summary>
public sealed partial class LaneGraph
{
    /// <summary>The tram graph's tracks laid in a carriageway (#124 <c>Embedded</c>): its trams share the cars' lanes there.</summary>
    private readonly HashSet<LaneEdge> _inStreet = new();

    /// <summary>Whether a tram on <paramref name="edge"/> runs in a car lane (street running): the cars behind it follow it.</summary>
    public bool InStreet(LaneEdge edge) => _inStreet.Contains(edge);

    /// <summary>Per tram path through a lit junction (#1057): how far before its start its tram stop line lies (m).</summary>
    private readonly Dictionary<LaneEdge, float> _tramStopBack = new();

    /// <summary>How far before the start of tram path <paramref name="path"/> its trams stop at red (m); 0 without a record.</summary>
    public float TramStopBack(LaneEdge path) => _tramStopBack.TryGetValue(path, out float back) ? back : 0f;

    /// <summary>The tram paths (#1057) laid through junction cores in place of their tracks.</summary>
    public int TramPaths { get; private set; }

    /// <summary>Whether a segment is a tram track the trams run on (not out of service).</summary>
    public static bool IsTramTrack(RoadSegment s) =>
        s.Class == RoadClass.Railway && (s.Flags & RoadFlags.Tramway) != 0 && (s.Flags & (RoadFlags.Disused | RoadFlags.Funicular)) == 0;

    /// <summary>
    /// Builds the tram graph from <paramref name="tiles"/> in <paramref name="origin"/>'s frame (on a worker thread, as
    /// <see cref="Build"/>): every tram track, oriented the way its trams run.
    /// </summary>
    public static LaneGraph BuildTrams(IReadOnlyList<RoadTile> tiles, OriginFrame origin)
    {
        var g = new LaneGraph(origin);
        var lines = new List<(double E, double N)[]>();
        var doubles = new List<bool>();
        foreach (var tile in tiles)
            foreach (var seg in tile.Segments)
            {
                if (seg.PointCount < 2 || !IsTramTrack(seg)) continue;
                var pts = new Vector3[seg.PointCount];
                var cum = new float[seg.PointCount];
                var plan = new (double E, double N)[seg.PointCount];
                // a track embedded in a road (#124) lies at the road's height, its rail head the groove paint
                float sink = seg.Attributes.Has(RoadAttrFlags.Embedded) ? EmbeddedRailSink : 0f;
                for (int i = 0; i < pts.Length; i++)
                {
                    plan[i] = seg.Lv95(tile.Id, i);
                    pts[i] = origin.ToWorld(plan[i].E, plan[i].N, seg.Points[i * 3 + 1] - sink);
                    if (i > 0) cum[i] = cum[i - 1] + pts[i].DistanceTo(pts[i - 1]);
                }
                if (cum[^1] < 0.5f) continue;
                var edge = new LaneEdge
                {
                    Points = pts, Cumulative = cum, Class = seg.Class, Flags = seg.Flags, Kind = MoverKind.Tram, Width = seg.Width,
                    KeyStart = Key(plan[0].E, plan[0].N), KeyEnd = Key(plan[^1].E, plan[^1].N),
                };
                g.Edges.Add(edge);
                if (seg.Attributes.Has(RoadAttrFlags.Embedded)) g._inStreet.Add(edge);
                lines.Add(plan);
                doubles.Add((seg.Flags & RoadFlags.DoubleTrack) != 0);
            }
        var ways = TramTracks.Directions(lines, doubles);
        for (int i = 0; i < g.Edges.Count; i++)
        {
            g.Edges[i].OneWay = ways[i];
            g.Link(g.Edges[i].KeyStart, g.Edges[i], true);
            g.Link(g.Edges[i].KeyEnd, g.Edges[i], false);
        }
        return g;
    }
}
