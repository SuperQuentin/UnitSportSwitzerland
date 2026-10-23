using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// The merge scenario (#1172, <c>--trafficcheck --merge</c>): two cars side by side at the same spot before a junction, each in its
/// own lane, both bound for the same turn that only one of the lanes shows; the other merges into it. They must both pass the line,
/// one behind the other, without standing on each other or holding each other for good.
/// </summary>
public partial class Traffic
{
    private Vehicle? _pairMerging, _pairKeeping;
    private LaneApproach? _pairApproach;

    /// <summary>
    /// Two cars <paramref name="before"/> m before <paramref name="approach"/>'s line, side by side: one on lane
    /// <paramref name="keepLane"/>, one on the lane <paramref name="mergeLane"/> beside it, both taking <paramref name="move"/> (a move of
    /// the keeper's lane only), both at <paramref name="speed"/>. False if the road there does not reach back that far.
    /// </summary>
    public bool SpawnMergePair(LaneApproach approach, float before, int keepLane, int mergeLane, SignalMoves move, float speed)
    {
        if (_roads is null) return false;
        foreach (var e in _roads.Edges)
        {
            bool atEnd = e.ApproachAtEnd?.Approach == approach;
            if (!atEnd && e.ApproachAtStart?.Approach != approach) continue;
            // the legs back from the approach's edge (the longest way in at each junction, so the run is the same every time)
            var legs = new List<(LaneEdge Edge, bool Forward)> { (e, atEnd) };
            float behind = e.Length - (atEnd ? e.ApproachAtEnd : e.ApproachAtStart)!.Value.StopBack;
            while (behind < before && legs.Count < 8)
            {
                var (first, forward) = legs[0];
                var options = _roads.Entering(forward ? first.KeyStart : first.KeyEnd).Where(o => o.Edge != first).ToList();
                if (options.Count == 0) break;
                var pick = options.MaxBy(o => o.Edge.Length);
                legs.Insert(0, pick);
                behind += pick.Edge.Length;
            }
            if (behind < before) return false;
            Vehicle Make(int lane)
            {
                var route = new Route(legs[0].Edge, legs[0].Forward, behind - before);
                for (int i = 1; i < legs.Count; i++) route.Legs.Add(legs[i]);
                AddCar(route);
                var v = _cars[^1];
                v.Speed = speed;
                v.Wants = (approach, move);
                // on the lane it starts in, from its usual line, as Approach places one bound for it
                float centre = approach.LaneCentre - KeepRight(route.Edge, route.Forward, v.Habit);
                v.Lane = centre + approach.Lateral(lane, before);
                v.Placed = true;
                return v;
            }
            (_pairKeeping, _pairMerging, _pairApproach) = (Make(keepLane), Make(mergeLane), approach);
            return true;
        }
        return false;
    }

    /// <summary>
    /// The pair now (<see cref="SpawnMergePair"/>): whether each is past its line (or gone), and how far apart their middles are, along
    /// the keeper's way and across it.
    /// </summary>
    public (bool KeeperPast, bool MergerPast, float Along, float Across)? MergePair()
    {
        if (_pairKeeping is not { } k || _pairMerging is not { } m) return null;
        bool Past(Vehicle v) => !_cars.Contains(v) || v.Approach != _pairApproach || v.ToLine < 0f;
        var dir = k.Route.At(0).Dir;
        var d = MathX.Flat(m.Head - k.Head);
        var right = new Vector3(-dir.Z, 0f, dir.X).Normalized();
        return (Past(k), Past(m), d.Dot(dir), d.Dot(right));
    }

    /// <summary>The pair's lanes now, m right of each one's usual line (the probe's log).</summary>
    public string MergePairLanes() => _pairKeeping is { } k && _pairMerging is { } m
        ? $"keeper lane {k.Lane:F2} v {k.Speed:F1} to line {k.ToLine:F0}, merger lane {m.Lane:F2}/{m.LaneWanted:F2} v {m.Speed:F1} to line {m.ToLine:F0}" : "-";
}
