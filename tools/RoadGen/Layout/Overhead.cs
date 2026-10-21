namespace UnitSport.Tools.RoadGen.Layout;

using UnitSport.Terrain.Format;

/// <summary>An overhead head as planned (#1037): its offset across the arm (right of a driver arriving) and the moves of the lanes under it.</summary>
public sealed record OverheadSpot(double Offset, SignalMoves Moves);

/// <summary>
/// Which lit approaches get heads over their lanes (#1037, <c>docs/plans/overhead-traffic-lights.md</c>), from the lanes at the cars'
/// line and where the kerb heads stand. Swiss practice decides per approach, not by the road's lane count: the main head stays on
/// the right kerb (SSV Art. 71) and is repeated over the lanes where a lane's own head would be too far from it (Stadt Bern,
/// Handbuch Lichtsignalanlagen 2025, ch. 12.2, Abb. 12-19). A lane's own head stands on the side it turns to (Bern: "an derjenigen
/// Fahrbahnseite, nach der abgebogen wird"): a left lane's on the left kerb or the refuge, every other lane's on the right kerb.
/// Only on a street with two lanes or more toward the junction (a 2+2 artery, OSM lanes:forward=2 and up), its turn pockets not
/// counted (the user, Oct 21 2026: pockets make a 1+1 road some 20 m wide at the lights, and the rules alone put heads over
/// nearly every lit approach). Pure.
/// </summary>
public static class OverheadPlanner
{
    /// <summary>
    /// A lane whose own kerb head stands further than this sideways from its middle brings the approach's heads overhead (m): a
    /// left pocket on a 2+2 road (its head across both oncoming lanes, ~7.5 m), not one on a 1+1 road (~4.5 m).
    /// </summary>
    public const double OwnHeadReach = 6.0;

    /// <summary>
    /// The approach's overhead heads, left to right; empty where its kerb heads do. Triggers: a lane further than
    /// <see cref="OwnHeadReach"/> from its own head; two lanes or more turning the same way (Bern Abb. 12, 13); a side road (it yields
    /// when the lights are off) of two lanes or more without a refuge (Abb. 18); three lanes or more (Abb. 19). Then a head over
    /// the middle of every lane, showing that lane's moves (the user, Oct 21 2026: one light per lane; a head for two lanes hung
    /// over the line between them; Bern: "Bei mehr als einem Fahrstreifen für einen Verkehrsstrom können ... weitere Signalgeber
    /// notwendig werden"). Bern's Abb. 19 leaves the kerb lane to its kerb head; here it gets one too.
    /// </summary>
    /// <param name="sec">The approach's section at its cars' line.</param>
    /// <param name="streetLanes">The street's own lanes toward the junction (its normal section's), pockets not counted.</param>
    /// <param name="rightHead">Where the right kerb's heads stand across the arm (the main pole).</param>
    /// <param name="leftHead">Where the left heads stand (the left kerb's or the refuge's pole), null without them.</param>
    public static List<OverheadSpot> Heads(Section sec, int streetLanes, double rightHead, double? leftHead, bool refuge, bool sideRoad)
    {
        if (streetLanes < 2) return [];
        var lanes = sec.CarLanes(StripWay.In)
            .Select(l => sec.Span(l.Index) is var (from, to) ? (Mid: (from + to) * 0.5, l.Strip.Moves) : default).ToList();
        if (lanes.Count == 0) return [];
        double Own(SignalMoves m) => m == SignalMoves.Left && leftHead is { } left ? left : rightHead;
        bool far = lanes.Any(l => Math.Abs(l.Mid - Own(l.Moves)) > OwnHeadReach);
        bool doubleTurn = lanes.Count(l => l.Moves == SignalMoves.Left) >= 2 || lanes.Count(l => l.Moves == SignalMoves.Right) >= 2;
        bool side = sideRoad && !refuge && lanes.Count >= 2;
        bool three = lanes.Count >= 3;
        if (!(far || doubleTurn || side || three)) return [];

        return lanes.Select(l => new OverheadSpot(l.Mid, l.Moves)).OrderBy(s => s.Offset).ToList();
    }
}
