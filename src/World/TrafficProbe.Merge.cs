using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// <c>--trafficcheck --at E,N --merge</c> (#1172): the merge scenario on its own, no other traffic (a world's endpoints are switched off).
/// At the signalised approach nearest <c>--at</c> with two car lanes side by side for <see cref="MergeRun"/> m where one lane shows a
/// move the one beside it does not, two cars start side by side <see cref="MergeRun"/> m before the line, both taking that move: one
/// keeps its lane, the other must merge into it. Passes when both are past the line within a light cycle plus <see cref="MergeSlack"/> s
/// and their bodies never stood or drove on each other (middles under <see cref="MergeLength"/> m along and
/// <see cref="MergeWidth"/> m across).
/// </summary>
public partial class TrafficProbe
{
    private readonly bool _merge = CmdArgs.Has("--at") && CmdArgs.Has("--merge");
    private bool _mergeSpawned;
    private double _mergeStart, _mergeLimit, _mergeLogged;
    private int _mergeOverlapTicks;
    private float _mergeClosest = float.MaxValue;
    private string? _mergeFailed;

    /// <summary>How far before the line the pair starts (m), at most: the lanes must run side by side that far.</summary>
    private const float MergeRun = 60f;
    /// <summary>Beyond a light cycle, the time both have to be past the line (s).</summary>
    private const double MergeSlack = 30;
    /// <summary>Two bodies overlap closer than this middle to middle (m): a car's 4.4 m length, its 1.8 m width.</summary>
    private const float MergeLength = 4.4f, MergeWidth = 1.8f;

    /// <summary>Every frame with <c>--merge</c>: the pair started once the roads are there, then watched to the end.</summary>
    private void MergeTick()
    {
        if (!_placed || _traffic.Roads is not { } roads) return;
        if (_traffic.WorldEndpoints.Count > 0) _traffic.WorldEndpoints = [];
        if (!_mergeSpawned)
        {
            if (_t < 2) return;
            _mergeSpawned = true;
            var pick = roads.Approaches.Where(a => a.Site is not null && a.MultiLane)
                .OrderBy(a => MathX.Flat(a.Stop - _spot).Length())
                .Select(a => (a, Pair: MergeLanes(a))).FirstOrDefault(x => x.Pair is not null);
            if (pick.Pair is not var (keep, merge, move))
            {
                Finish("no approach near --at with two lanes side by side");
                return;
            }
            var a = pick.a;
            float before = Math.Min(MergeRun, Math.Min(a.Lanes[keep].FullFrom, a.Lanes[merge].FullFrom) - 5f);
            if (!_traffic.SpawnMergePair(a, before, keep, merge, move, 8f)) { Finish("the road does not reach back to the start"); return; }
            _mergeStart = _t;
            _mergeLimit = a.Site!.Plan.Cycle + MergeSlack;
            string where = Origin is { } o && o.ToLv95(a.Stop) is var (e, n) ? $"{e:F0},{n:F0}" : $"{a.Stop}";
            GD.Print($"[trafficcheck] merge: two cars side by side {before:F0} m before the line at {where}, lanes {keep} (keeps) and {merge} (merges), "
                + $"both {move}, within {_mergeLimit:F0} s");
            return;
        }
        if (_traffic.MergePair() is not var (keeperPast, mergerPast, along, across)) return;
        float gap = Math.Max(Math.Abs(along) / MergeLength, Math.Abs(across) / MergeWidth);
        _mergeClosest = Math.Min(_mergeClosest, gap);
        if (!keeperPast && !mergerPast && gap < 1f) _mergeOverlapTicks++;
        if (_t - _mergeLogged >= 2)
        {
            _mergeLogged = _t;
            GD.Print($"[trafficcheck] merge t+{_t - _mergeStart:F0}s: {_traffic.MergePairLanes()}, merger {along:+0.0;-0.0} m along, {across:+0.0;-0.0} m across");
        }
        if (keeperPast && mergerPast) Finish(null);
        else if (_t - _mergeStart > _mergeLimit) Finish($"not both past the line within {_mergeLimit:F0} s (keeper {(keeperPast ? "past" : "not")}, merger {(mergerPast ? "past" : "not")})");
    }

    /// <summary>
    /// Of an approach's car lanes side by side, one showing a move the next does not (the keeper's) and that next one (the merger's):
    /// both full for long enough to start side by side.
    /// </summary>
    private static (int Keep, int Merge, SignalMoves Move)? MergeLanes(LaneApproach a)
    {
        var cars = Enumerable.Range(0, a.Lanes.Length).Where(i => a.Lanes[i].Kind == ApproachLaneKind.Car).ToList();
        for (int k = 0; k + 1 < cars.Count; k++)
            foreach (var (keep, merge) in new[] { (cars[k], cars[k + 1]), (cars[k + 1], cars[k]) })
            {
                var only = a.Lanes[keep].Moves & ~a.Lanes[merge].Moves;
                if (only == 0 || Math.Min(a.Lanes[keep].FullFrom, a.Lanes[merge].FullFrom) < 25f) continue;
                var move = (only & SignalMoves.Left) != 0 ? SignalMoves.Left : (only & SignalMoves.Right) != 0 ? SignalMoves.Right : SignalMoves.Through;
                return (keep, merge, move);
            }
        return null;
    }

    private void Finish(string? failure)
    {
        failure ??= _mergeOverlapTicks > 0 ? $"their bodies overlapped for {_mergeOverlapTicks} ticks" : null;
        _mergeFailed = failure;
        GD.Print(failure is null
            ? $"[trafficcheck] RESULT: ok (merge: both past the line in {_t - _mergeStart:F0} s, never on each other, closest {_mergeClosest:F2} of a body)"
            : $"[trafficcheck] RESULT: FAILED — merge: {failure}");
        GetTree().Quit(failure is null ? 0 : 1);
        SetProcess(false);
    }
}
