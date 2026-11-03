using System.Runtime.CompilerServices;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>Where a rider rides on one direction of a ride edge (#1056).</summary>
public enum RideSide : byte
{
    /// <summary>In the car lane, near its right edge: no bike provision on that side (cars follow it).</summary>
    Road = 0,
    /// <summary>A painted bike lane on the carriageway (#120, yellow dashes).</summary>
    Lane = 1,
    /// <summary>A separated bike path beside the carriageway (#120).</summary>
    Track = 2,
    /// <summary>A way of its own with no cars: a track, a cycle path.</summary>
    Way = 3,
    /// <summary>Not ridden that way (a one-way street with nothing for riders against it).</summary>
    None = 4,
}

/// <summary>
/// What a rider needs of an edge of the ride graph (#1056) beyond the <see cref="LaneEdge"/>: per direction where it rides
/// (<see cref="RideSide"/>), the street piece it came from (its sides' widths and shifts), and for a junction path whether it is a
/// riders' own (<c>JPTH</c> bike or path, #971) or a car path riders in the car lane take.
/// </summary>
public sealed class RideWay
{
    public RideSide Fwd, Bwd;
    public RoadSegment? Segment;
    /// <summary>A riders' junction path (<see cref="JunctionPathKind.Bike"/> from a bike lane, <see cref="JunctionPathKind.Path"/> from a separated path); null on a road or a car path.</summary>
    public JunctionPathKind? Bike;

    public RideSide Side(bool forward) => forward ? Fwd : Bwd;
}

public sealed partial class LaneGraph
{
    /// <summary>
    /// The ride graphs' own facts per edge (#1056), kept as long as the edge lives: a rider still on an edge of the graph before a
    /// rebuild reads them too. Thread-safe (the graph is built on a worker).
    /// </summary>
    private static readonly ConditionalWeakTable<LaneEdge, RideWay> Ways = new();

    /// <summary>A ride edge's facts; null for an edge of no ride graph (a link <see cref="JoinTrimmedEnds"/> added).</summary>
    public static RideWay? RideOf(LaneEdge e) => Ways.TryGetValue(e, out var r) ? r : null;

    /// <summary>A street riders may use: not a motorway, an expressway or a ramp, not stairs or a ford, not a tunnel (#120's rule).</summary>
    private static bool RideableStreet(RoadSegment s) =>
        s.Class is RoadClass.Major or RoadClass.Road or RoadClass.Minor or RoadClass.Lane
        && (s.Flags & (RoadFlags.Stairs | RoadFlags.Ford | RoadFlags.Tunnel)) == 0;

    /// <summary>A way with no cars a rider takes: a track, or a path or square in the Veloland or Mountainbikeland networks.</summary>
    private static bool RideableWay(RoadSegment s) =>
        (s.Flags & (RoadFlags.Stairs | RoadFlags.Ford)) == 0
        && (s.Class == RoadClass.Track || s.Class is RoadClass.Path or RoadClass.Square && (s.Flags & (RoadFlags.Cycle | RoadFlags.MountainBike)) != 0);

    /// <summary>Where riders ride on a side of a street piece, in the direction that side is on their right.</summary>
    private static RideSide SideOf(RoadSide side) => side.HasLane ? RideSide.Lane : side.HasTrack ? RideSide.Track : RideSide.Road;

    /// <summary>
    /// The ride graph (#1056, on the traffic's worker thread): every street riders may use (its painted lanes and separated paths
    /// where it has them, else its car lane), the tracks and cycle paths, the car graph's junction paths and links across trimmed
    /// ends (riders in the car lane take those), and the riders' own junction paths (<c>JPTH</c> bike and path ways, #971) with
    /// their lights and groups. Street pieces take the approaches (#353) of the car graph's same pieces, so riders read the same
    /// stop lines. Every edge is a copy (its own points): the car graph's edges move with the origin once, and so do these.
    /// One-way streets carry riders against their cars only where the side on their right has a lane or a path (a contraflow lane
    /// as OSM maps it, #769).
    /// </summary>
    public static LaneGraph BuildRides(IReadOnlyList<RoadTile> near, OriginFrame origin, LaneGraph roads)
    {
        var g = new LaneGraph(origin);
        // the car graph's street pieces by their ends, for their approaches
        var carPiece = new Dictionary<(long, long), LaneEdge>();
        foreach (var e in roads.Edges)
            if (!e.Connector && e.Path is null) carPiece.TryAdd((e.KeyStart, e.KeyEnd), e);
        int site0 = 0;
        foreach (var tile in near)
        {
            Dictionary<RoadSegment, RoadTransition>? transitionOf = null;
            foreach (var t in tile.Transitions) (transitionOf ??= new(ReferenceEqualityComparer.Instance))[t.Segment] = t;
            foreach (var seg in tile.Segments)
            {
                bool street = RideableStreet(seg), way = !street && RideableWay(seg);
                if (seg.PointCount < 2 || !street && !way) continue;
                var pts = new Vector3[seg.PointCount];
                var cum = new float[seg.PointCount];
                long keyStart = 0, keyEnd = 0;
                for (int i = 0; i < pts.Length; i++)
                {
                    var (e, n) = seg.Lv95(tile.Id, i);
                    pts[i] = origin.ToWorld(e, n, seg.Points[i * 3 + 1]);
                    if (i > 0) cum[i] = cum[i - 1] + pts[i].DistanceTo(pts[i - 1]);
                    if (i == 0) keyStart = Key(e, n);
                    if (i == pts.Length - 1) keyEnd = Key(e, n);
                }
                if (cum[^1] < 1f) continue;
                var at = seg.Attributes;
                var ride = new RideWay { Segment = seg };
                if (way) ride.Fwd = ride.Bwd = RideSide.Way;
                else
                {
                    ride.Fwd = SideOf(at.Right);
                    ride.Bwd = SideOf(at.Left);
                    // a one-way street: riders with its cars, against them only on a lane or a path of their own (contraflow, #769)
                    if (at.OneWay > 0 && ride.Bwd == RideSide.Road) ride.Bwd = RideSide.None;
                    if (at.OneWay < 0 && ride.Fwd == RideSide.Road) ride.Fwd = RideSide.None;
                }
                var edge = new LaneEdge
                {
                    Points = pts, Cumulative = cum, Class = seg.Class, Flags = seg.Flags, Kind = MoverKind.Rider, Width = seg.Width,
                    KeyStart = keyStart, KeyEnd = keyEnd,
                    OneWay = ride.Fwd == RideSide.None ? -1 : ride.Bwd == RideSide.None ? 1 : 0,
                    Transition = transitionOf?.GetValueOrDefault(seg),
                };
                if (street && carPiece.TryGetValue((keyStart, keyEnd), out var car))
                {
                    edge.ApproachAtEnd = car.ApproachAtEnd;
                    edge.ApproachAtStart = car.ApproachAtStart;
                }
                g.Add(edge);
                Ways.AddOrUpdate(edge, ride);
            }
            // the riders' own ways through the junctions (#971): a bike lane's on to the exit's lane, a path's round the core
            foreach (var jpath in tile.JunctionPaths)
            {
                if (jpath.Kind == JunctionPathKind.Car || jpath.PointCount < 2) continue;
                var site = jpath.Signal >= 0 && site0 + jpath.Signal < roads.Signals.Count && jpath.Signal < tile.Signals.Count
                    ? roads.Signals[site0 + jpath.Signal] : null;
                var edge = PathEdge(tile, jpath, origin, new JunctionPathInfo(jpath.Move, jpath.From.Offset, jpath.To.Offset, site, site is null ? -1 : jpath.Group));
                if (edge is null) continue;
                g.Add(edge);
                Ways.AddOrUpdate(edge, new RideWay { Fwd = RideSide.Way, Bwd = RideSide.None, Bike = jpath.Kind });
            }
            site0 += tile.Signals.Count;
        }
        // the cars' junction paths and links (riders in the car lane, and where a junction has no riders' way of its own)
        foreach (var e in roads.Edges)
        {
            if (!e.Connector) continue;
            if (e.Class is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp) continue;
            var copy = new LaneEdge
            {
                Points = (Vector3[])e.Points.Clone(), Cumulative = e.Cumulative, Class = e.Class, Flags = e.Flags, Kind = MoverKind.Rider,
                Width = e.Width, KeyStart = e.KeyStart, KeyEnd = e.KeyEnd, OneWay = e.OneWay, Connector = true, Path = e.Path,
            };
            g.Add(copy);
            Ways.AddOrUpdate(copy, new RideWay { Fwd = RideSide.Road, Bwd = RideSide.Road });
        }
        // the tracks' and cycle paths' ends trimmed back from a junction, joined across it as the roads' are
        g.JoinTrimmedEnds();
        return g;
    }

    /// <summary>A junction path of a tile as a one-way edge in <paramref name="origin"/>'s frame, from its arm's road end to the exit's; null if too short.</summary>
    private static LaneEdge? PathEdge(RoadTile tile, RoadJunctionPath jpath, OriginFrame origin, JunctionPathInfo info)
    {
        int n = jpath.PointCount;
        var pts = new Vector3[n];
        var cum = new float[n];
        for (int i = 0; i < n; i++)
        {
            pts[i] = origin.ToWorld(tile.Id.MinE + jpath.Points[i * 3], tile.Id.MaxN - jpath.Points[i * 3 + 2], jpath.Points[i * 3 + 1]);
            if (i > 0) cum[i] = cum[i - 1] + pts[i].DistanceTo(pts[i - 1]);
        }
        if (cum[^1] < 0.5f) return null;
        return new LaneEdge
        {
            Points = pts, Cumulative = cum, Class = jpath.Junction.Class, Width = Math.Max(jpath.Width, jpath.ToWidth), OneWay = 1, Connector = true,
            Kind = MoverKind.Rider,
            KeyStart = Key(tile.Id.MinE + jpath.From.X, tile.Id.MaxN - jpath.From.Z), KeyEnd = Key(tile.Id.MinE + jpath.To.X, tile.Id.MaxN - jpath.To.Z),
            Path = info,
        };
    }

    /// <summary>
    /// Where a rider rides across a street piece going <paramref name="forward"/>, <paramref name="arc"/> m along its way: m right
    /// of the edge's line (its centreline), in its direction. A transition's piece (#818) from its strips: the bike lane of its
    /// direction (the left-turn bike lane when <paramref name="turn"/> is a left and it has one), else its separated path, else
    /// the kerbside car lane's right part; a plain piece from its sides (a lane's centre inside the carriageway, a path's
    /// beyond the verge, both moved out by a turn lane's widening), else 0.9 m in from the carriageway's edge.
    /// </summary>
    public static float RideLateral(LaneEdge e, RideWay? w, bool forward, float arc, SignalMoves turn)
    {
        if (w is null) return 0f;
        var side = w.Side(forward);
        if (side == RideSide.Way || w.Segment is not { } seg) return e.Path is null && e.Width >= 2.5f ? 0.5f : 0f;
        float len = Mathf.Max(e.Length, 1e-3f), along = Mathf.Clamp(forward ? arc : len - arc, 0f, len);
        if (e.Transition is { } tr && StripLateral(tr, forward, tr.Stations[^1] * along / len, side, turn) is { } onStrip)
            return forward ? onStrip : -onStrip;
        var rs = forward ? seg.Attributes.Right : seg.Attributes.Left;
        float half = seg.Width * 0.5f, t = along / len;
        return side switch
        {
            RideSide.Lane => half + rs.ShiftAt(t) - rs.BikeDm / 20f,
            RideSide.Track => half + rs.ShiftAt(t) + RoadStreetSection.TrackCentre(rs),
            _ => Mathf.Max(0.3f, half - 0.9f),
        };
    }

    /// <summary>
    /// How far out from an edge's line its way reaches at <paramref name="arc"/> (m): a transition's outermost strip, a street piece's
    /// carriageway and widest side (sidewalk, path, verge), a way's half width; for <c>--trafficcheck</c>, whether riders keep to it.
    /// </summary>
    public static float RideReach(LaneEdge e, RideWay w, bool forward, float arc)
    {
        float len = Mathf.Max(e.Length, 1e-3f), reach = Mathf.Max(e.Width * 0.5f, 1f);
        if (e.Transition is { } tr && tr.Strips.Count > 0)
        {
            double s = tr.Stations[^1] * Mathf.Clamp(forward ? arc : len - arc, 0f, len) / len;
            reach = Mathf.Max(reach, Mathf.Max(Mathf.Abs(tr.SpanAt(0, s).From), Mathf.Abs(tr.SpanAt(tr.Strips.Count - 1, s).To)));
        }
        // (a transition without a side's bands: the piece's own sides, which RideLateral rides then)
        if (w.Segment is { } seg) reach = Mathf.Max(reach, seg.Width * 0.5f + Mathf.Max(seg.Attributes.Left.Reach, seg.Attributes.Right.Reach));
        return reach;
    }

    /// <summary>A transition's strip a rider rides (its centre, m right of the drawing) at station <paramref name="s"/>; null for none.</summary>
    private static float? StripLateral(RoadTransition tr, bool forward, double s, RideSide side, SignalMoves turn)
    {
        var (first, last) = tr.Carriageway;
        int travel = forward ? 1 : -1;
        int lane = -1, leftLane = -1, path = -1, car = -1;
        float Centre(int k) { var (from, to) = tr.SpanAt(k, s); return (from + to) * 0.5f; }
        // (the rider's right is the drawing's right going forward, its left going back: "outermost" flips with it)
        bool Outer(int k, int than) => than < 0 || (forward ? Centre(k) > Centre(than) : Centre(k) < Centre(than));
        for (int k = 0; k < tr.Strips.Count; k++)
        {
            var x = tr.Strips[k];
            if (tr.WidthAt(k, s) < 0.5f) continue;
            switch (x.Kind)
            {
                case StripKind.BikeLane when x.Travel == travel || x.Travel == 0:
                    if (x.Moves == SignalMoves.Left) { leftLane = k; break; }
                    if (Outer(k, lane)) lane = k;
                    break;
                case StripKind.Path when first >= 0 && (forward ? k > last : k < first):
                    if (Outer(k, path)) path = k;
                    break;
                case StripKind.CarLane when x.Travel == travel:
                    if (Outer(k, car)) car = k;
                    break;
            }
        }
        if (turn == SignalMoves.Left && leftLane >= 0 && side != RideSide.Track) return Centre(leftLane);
        if (side == RideSide.Track && path >= 0) return Centre(path);
        if (lane >= 0) return Centre(lane);
        if (path >= 0) return Centre(path);
        if (car >= 0)
        {
            var (from, to) = tr.SpanAt(car, s);
            return forward ? Mathf.Max((from + to) * 0.5f, to - 0.9f) : Mathf.Min((from + to) * 0.5f, from + 0.9f);
        }
        return null;
    }
}
