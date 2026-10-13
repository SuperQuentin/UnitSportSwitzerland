namespace UnitSport.Tools.RoadGen.Layout;

using System.Globalization;
using System.Text;
using UnitSport.Tools.RoadGen.Geometry;

/// <summary>A lane that opens along a transition (a pocket, #818): its strip id, its taper and its storage (m).</summary>
public readonly record struct Opening(int Id, double Taper, double Storage);

/// <summary>One arm of a planned junction (#818): its street's normal section, what the data wants at the mouth, and what fitted.</summary>
public sealed class ArmPlan
{
    public required int Arm { get; init; }
    public required int LinkId { get; init; }
    /// <summary>The junction is at the link's last point (traffic arriving drives along its drawing).</summary>
    public bool AtEnd { get; init; }
    /// <summary>The arm's outward heading (radians, 0 east, counter-clockwise), as <c>SignalArm.Heading</c>.</summary>
    public required double Heading { get; init; }
    public required Section Normal { get; init; }
    /// <summary>What the data wants of the approach; a pocket the planner decides against is taken out of it.</summary>
    public required ArmWants Wants { get; set; }
    public required Section Wanted { get; set; }
    public required Section Mouth { get; set; }
    public ArmRoom Room { get; set; } = ArmRoom.Unbounded;
    /// <summary>The street's length from the mouth to its far end (m).</summary>
    public double Length { get; init; }
    /// <summary>The lanes that open along the transition, with their lengths.</summary>
    public List<Opening> Openings { get; } = new();
    /// <summary>The pockets' sizes the fit may try, longest first (each a set of openings).</summary>
    public List<List<Opening>> Sizes { get; } = new();
    /// <summary>The size the fit settled on (index into <see cref="Sizes"/>).</summary>
    public int SizeTried { get; set; }
    /// <summary>The choices made before the fit, in order: pockets too short, merged, dropped for their exit.</summary>
    public List<string> Steps { get; } = new();
    /// <summary>The shrink steps the last fit took (<see cref="MouthPlanner.Fit"/>).</summary>
    public List<string> FitSteps { get; } = new();
    /// <summary>The wanted section less what the planner decided against (pockets too short or without an exit), unmoved, no hatch.</summary>
    public Section Base { get; set; } = null!;
    /// <summary>How far the mouth section stands to the left of the normal one's axis: a split lead-in's share (#700).</summary>
    public double Split { get; set; }
    /// <summary>The arm each move leaves by (as the signal plans name the turns).</summary>
    public Dictionary<Terrain.Format.SignalMoves, int> Toward { get; } = new();
    /// <summary>The arm the through lane leaves by (-1 none).</summary>
    public int Exit { get; init; } = -1;
    /// <summary>The street is too short for this pocket and the exit at its other end apart: one transition holds both (#325).</summary>
    public ArmPlan? MergedWith { get; set; }
    /// <summary>What the pocket layout (#123, #348) built on this approach, for the comparison while both run.</summary>
    public bool BuiltLeft { get; set; }
    public bool BuiltRight { get; set; }
    /// <summary>The arm's axis from the node outward, LV95 (#818 step 2: the core is laid on it).</summary>
    public ArmAxis? Axis { get; set; }
    /// <summary>Where today's cap cut the street back (<c>JunctionBuilder</c>'s trim, m from the node), for the comparison.</summary>
    public double OldTrim { get; init; }
    /// <summary>Its street joins another junction node closer than 30 m: the two are one core (phase 5); left out of the checks till then.</summary>
    public bool Internal { get; init; }
    /// <summary>How far out its mouth may go: the street's length, half of it where a junction stands at its other end (m).</summary>
    public double Reach { get; init; } = double.MaxValue;
    /// <summary>A Major road (or bigger): turns to and from it are laid for a truck (<see cref="CorePlanner.TruckInner"/>).</summary>
    public bool Major { get; init; }
    public bool HasLeft => Mouth.Strips.Any(s => PocketRules.Left.Owns(s.Id));
    public bool HasRight => Mouth.Strips.Any(s => PocketRules.Right.Owns(s.Id));
}

/// <summary>A junction as the planner lays it out (#818): its arms, step 1 of <c>docs/plans/junction-layout.md</c>.</summary>
public sealed class JunctionPlan
{
    public required int Node { get; init; }
    public required Vec2 Centre { get; init; }
    public required bool Lights { get; init; }
    public List<ArmPlan> Arms { get; } = new();
    /// <summary>The main road's two arms (<see cref="ArmPlan.Arm"/>), as the priority plan chose them; empty without.</summary>
    public int[] MainArms { get; init; } = [];
    /// <summary>The priority plan carries the main road's centre line through, and its edge guides.</summary>
    public bool CentreLineThrough { get; init; }
    public bool EdgeGuides { get; init; }
    /// <summary>The core laid between the mouths (step 2).</summary>
    public JunctionCore? Core { get; set; }
    /// <summary>Today's cap (<c>JunctionBuilder</c>), its area and its tight corners (#700, pairs of arms), for the comparison while both run.</summary>
    public IReadOnlyList<Vec2> OldOutline { get; init; } = [];
    public double OldArea { get; init; }
    public HashSet<(int A, int B)> OldTight { get; init; } = new();

    /// <summary>
    /// Today's paved area out to the planner's mouths: the cap, plus each street's ribbon (its normal carriageway) from where the
    /// cap cut it to the new mouth (less where the new mouth stands inside the old cut). What the core's area compares with.
    /// </summary>
    public double OldAreaToMouths => Core is null ? OldArea
        : OldArea + Arms.Sum(a => Core.Mouths.FirstOrDefault(m => m.Arm == a.Arm) is { } m ? (m.Station - a.OldTrim) * a.Normal.CarriagewayWidth : 0);

    /// <summary>The compass direction an arm's traffic comes from: the west arm's comes from the west (as the test region names it).</summary>
    public static string Compass(double heading)
    {
        double deg = (heading * 180 / Math.PI % 360 + 360) % 360;
        return deg < 45 || deg >= 315 ? "E" : deg < 135 ? "N" : deg < 225 ? "W" : "S";
    }

    /// <summary>The region's plans in one line (#818): junctions, arms, pockets, split lead-ins, exit hatches, the shrink steps taken.</summary>
    public static string Summary(IReadOnlyList<JunctionPlan> plans)
    {
        var inv = CultureInfo.InvariantCulture;
        var arms = plans.SelectMany(p => p.Arms).ToList();
        int lefts = arms.Count(a => a.HasLeft);
        int doubles = arms.Count(a => a.Mouth.Strips.Count(s => PocketRules.Left.Owns(s.Id)) >= 2);
        int rights = arms.Count(a => a.HasRight);
        int splits = arms.Count(a => a.Split > 0);
        int hatches = arms.Count(a => a.Mouth.Strips.Any(s => s.Kind == StripKind.Hatch));
        var steps = arms.SelectMany(a => a.Steps.Concat(a.FitSteps).Select(s => s.Split(':', ',')[0])).GroupBy(s => s).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} x{g.Count()}");
        string Where(Func<ArmPlan, bool> f)
        {
            var at = plans.SelectMany(p => p.Arms.Where(f).Select(a => (p, a))).Take(6)
                .Select(x => string.Create(inv, $"{x.p.Centre.X:F0},{x.p.Centre.Y:F0} {Compass(x.a.Heading)}"));
            return string.Join("; ", at);
        }
        int both = arms.Count(a => a.HasLeft && a.BuiltLeft), newOnly = arms.Count(a => a.HasLeft && !a.BuiltLeft), oldOnly = arms.Count(a => !a.HasLeft && a.BuiltLeft);
        int rBoth = arms.Count(a => a.HasRight && a.BuiltRight), rNew = arms.Count(a => a.HasRight && !a.BuiltRight), rOld = arms.Count(a => !a.HasRight && a.BuiltRight);
        return string.Create(inv, $"    junction planner (#818): {plans.Count:N0} junctions ({plans.Count(p => p.Lights):N0} with lights), {arms.Count:N0} car arms, ")
            + string.Create(inv, $"{lefts:N0} left pockets ({doubles:N0} of two lanes or more), {rights:N0} right pockets, {splits:N0} split lead-ins, {hatches:N0} exit hatches; ")
            + "steps taken: " + string.Join(", ", steps) + "\n"
            + string.Create(inv, $"      against the pocket layout: left pockets in both {both:N0}, planner only {newOnly:N0} (at {Where(a => a.HasLeft && !a.BuiltLeft)}), ")
            + string.Create(inv, $"pocket layout only {oldOnly:N0} (at {Where(a => !a.HasLeft && a.BuiltLeft)}); right pockets in both {rBoth:N0}, planner only {rNew:N0}, pocket layout only {rOld:N0}")
            + (arms.Any(a => a.FitSteps.Contains("does not fit")) ? "\n      does not fit at " + Where(a => a.FitSteps.Contains("does not fit")) : "");
    }

    /// <summary>
    /// The region's cores in one line (#818 step 2): connectors, mouths, corners by shape (tight ones: no connector needs
    /// more than the least rounding), the checks (kerbs starting outside their mouth, connectors leaving the outline), the
    /// tight corners against #700's and the paved area against today's caps.
    /// </summary>
    public static string CoreSummary(IReadOnlyList<JunctionPlan> plans)
    {
        var inv = CultureInfo.InvariantCulture;
        // a junction with an internal arm is half of a multi-node core (phase 5): counted apart
        int multi = plans.Count(p => p.Core is not null && p.Arms.Any(a => a.Internal));
        var cores = plans.Where(p => p.Core is not null && !p.Arms.Any(a => a.Internal)).ToList();
        var conns = cores.SelectMany(p => p.Core!.Connectors).ToList();
        var corners = cores.SelectMany(p => p.Core!.Corners).ToList();
        var mouths = cores.SelectMany(p => p.Core!.Mouths.Select(m => m.Station)).OrderBy(x => x).ToList();
        string At(IEnumerable<JunctionPlan> ps) => string.Join("; ", ps.Take(6).Select(p => string.Create(inv, $"{p.Centre.X:F0},{p.Centre.Y:F0}")));
        // #700's tight corners against the planner's, on corners between two car arms both name
        int both = 0, newOnly = 0, oldOnly = 0;
        var newOnlyAt = new List<JunctionPlan>();
        var oldOnlyAt = new List<JunctionPlan>();
        foreach (var p in cores)
            foreach (var c in p.Core!.Corners.Where(c => c.Shape == CornerShape.Arc))
            {
                bool old = p.OldTight.Contains((c.ArmA, c.ArmB));
                if (old && c.Tight) both++;
                else if (c.Tight) { newOnly++; newOnlyAt.Add(p); }
                else if (old) { oldOnly++; oldOnlyAt.Add(p); }
            }
        double oldArea = cores.Sum(p => p.OldAreaToMouths), newArea = cores.Sum(p => p.Core!.Area);
        var ratios = cores.Where(p => p.OldAreaToMouths > 1).Select(p => p.Core!.Area / p.OldAreaToMouths).OrderBy(x => x).ToList();
        var biggest = cores.OrderByDescending(p => Math.Abs(p.Core!.Area - p.OldAreaToMouths)).Take(4)
            .Select(p => string.Create(inv, $"{p.Centre.X:F0},{p.Centre.Y:F0} {p.OldAreaToMouths:F0}->{p.Core!.Area:F0}"));
        double Median(List<double> xs) => xs.Count == 0 ? 0 : xs[xs.Count / 2];
        var outside = cores.Where(p => p.Core!.ConnectorsOutside > 0).ToList();
        var notTangent = cores.Where(p => p.Core!.KerbsNotTangent > 0).ToList();
        return string.Create(inv, $"    junction cores (#818 step 2): {cores.Count:N0} cores ({multi:N0} more joined to another node by a street under 30 m: multi-node, phase 5, left out), {conns.Count:N0} connectors ({conns.Count(c => c.Kind == ConnectorKind.Bike):N0} for bikes; ")
            + string.Create(inv, $"{conns.Count(c => c.Radius > 0):N0} arcs, {conns.Count(c => c.Fill < 1 - 1e-6 && c.Radius > 0 && c.WideRadius > c.MinRadius + 0.01):N0} narrowed for a mouth or the room, {conns.Count(c => c.Fill < 0 || c.UnderRadius):N0} under their vehicle's radius), ")
            + string.Create(inv, $"mouths median {Median(mouths):F1} m (p90 {(mouths.Count == 0 ? 0 : mouths[mouths.Count * 9 / 10]):F1}, {cores.Sum(p => p.Core!.MouthsAtMost):N0} held at the most, {cores.Sum(p => p.Core!.MouthsAtReach):N0} at half a street to the next junction); ")
            + string.Create(inv, $"corners: arcs {corners.Count(c => c.Shape == CornerShape.Arc):N0} ({corners.Count(c => c.Tight):N0} tight, {corners.Count(c => c.RoomClipped):N0} held by buildings), ")
            + string.Create(inv, $"vertices {corners.Count(c => c.Shape == CornerShape.Vertex):N0}, chords {corners.Count(c => c.Shape == CornerShape.Chord):N0}, gores {corners.Count(c => c.Shape == CornerShape.Gore):N0}\n")
            + string.Create(inv, $"      checks: kerbs starting outside their mouth {cores.Sum(p => p.Core!.KerbsNotTangent):N0}{(notTangent.Count > 0 ? " (at " + At(notTangent) + ")" : "")}, ")
            + string.Create(inv, $"connectors leaving the outline {cores.Sum(p => p.Core!.ConnectorsOutside):N0}{(outside.Count > 0 ? " (at " + At(outside) + ")" : "")}, ")
            + string.Create(inv, $"outlines crossing themselves {cores.Count(p => !p.Core!.Simple):N0}{(cores.Any(p => !p.Core!.Simple) ? " (at " + At(cores.Where(p => !p.Core!.Simple)) + ")" : "")}\n")
            + string.Create(inv, $"      against today: tight corners in both {both:N0}, planner only {newOnly:N0}{(newOnly > 0 ? " (at " + At(newOnlyAt.Distinct()) + ")" : "")}, ")
            + string.Create(inv, $"#700 only {oldOnly:N0}{(oldOnly > 0 ? " (at " + At(oldOnlyAt.Distinct()) + ")" : "")}; paved area out to the new mouths today (cap and ribbons) {oldArea:N0} m2, of the cores {newArea:N0} m2 ")
            + string.Create(inv, $"(per junction median x{Median(ratios):F2}, p10 x{(ratios.Count == 0 ? 0 : ratios[ratios.Count / 10]):F2}, p90 x{(ratios.Count == 0 ? 0 : ratios[ratios.Count * 9 / 10]):F2}; most changed {string.Join("; ", biggest)})");
    }

    /// <summary>Every arm's sections, room and steps, for <c>--debug-junction</c>.</summary>
    public string Describe()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(inv, $"[junction] node {Node} at LV95 {Centre.X:F1},{Centre.Y:F1}{(Lights ? ", lights" : "")}\n");
        foreach (var a in Arms)
        {
            sb.Append(inv, $"  arm {a.Arm} {Compass(a.Heading)} (link {a.LinkId}, {a.Length:F0} m, exit arm {a.Exit}), room L {Fmt(a.Room.Left)} R {Fmt(a.Room.Right)}\n");
            sb.Append("    normal  ").Append(a.Normal).Append('\n');
            sb.Append("    wanted  ").Append(a.Wanted).Append('\n');
            sb.Append("    mouth   ").Append(a.Mouth).Append('\n');
            if (a.Openings.Count > 0)
                sb.Append("    opens   ").Append(string.Join(", ", a.Openings.Select(o => string.Create(inv, $"lane {o.Id}: taper {o.Taper:F0} + storage {o.Storage:F0} m")))).Append('\n');
            if (a.Split > 0) sb.Append(inv, $"    split lead-in: {a.Split:F2} m to the left\n");
            if (a.Steps.Count + a.FitSteps.Count > 0) sb.Append("    steps   ").Append(string.Join("; ", a.Steps.Concat(a.FitSteps))).Append('\n');
        }
        if (Core is { } core)
        {
            sb.Append(inv, $"  core: area {core.Area:F0} m2 (today: cap {OldArea:F0} m2, with its ribbons to the new mouths {OldAreaToMouths:F0} m2), kerbs not tangent {core.KerbsNotTangent}, connectors outside {core.ConnectorsOutside}\n");
            foreach (var m in core.Mouths) sb.Append(inv, $"    mouth arm {m.Arm}: {m.Station:F1} m out{(Arms.First(a => a.Arm == m.Arm) is { } am ? (am.Internal ? " (internal: multi-node)" : am.Reach < 1e9 ? $" (reach {am.Reach:F1} m)" : "") : "")}\n");
            foreach (var c in core.Corners)
                sb.Append(inv, $"    corner {c.ArmA}-{c.ArmB}: {c.Shape}{(c.Shape == CornerShape.Arc ? $" r {c.Radius:F1} m (floor {c.Floor:F1}){(c.Tight ? " tight" : "")}{(c.RoomClipped ? " held by buildings" : "")}, kerb from {c.StationA:F1} / {c.StationB:F1} m{(c.Bound is { } k ? $", round {k.FromArm}:{k.FromId}->{k.ToArm}:{k.ToId} {Section.MovesName(k.Move)}" : "")}" : "")}, gap {c.Angle * 180 / Math.PI:F0} deg{(OldTight.Contains((c.ArmA, c.ArmB)) ? ", #700 tight" : "")}\n");
            foreach (var c in core.Connectors)
                sb.Append(inv, $"    {(c.Kind == ConnectorKind.Bike ? "bike" : "car ")} {c.FromArm}:{c.FromId} -> {c.ToArm}:{c.ToId} {Section.MovesName(c.Move)} w {c.Width:F2} ")
                  .Append(c.Radius > 0 ? string.Create(inv, $"r {c.Radius:F1} (least {c.MinRadius:F1}, widest {c.WideRadius:F1}, fill {c.Fill:F2})") : string.Create(inv, $"straight/S (holds {c.HoldIn:F1} / {c.HoldOut:F1} m)"))
                  .Append(c.Outside > CorePlanner.OutsideTolerance ? string.Create(inv, $"  OUTSIDE by {c.Outside:F2} m") : "").Append('\n');
            foreach (var l in core.Lines) sb.Append(inv, $"    line {l.Kind}{(l.Dashed ? " dashed" : "")}, {l.Points.Count} points\n");
        }
        return sb.ToString();

        string Fmt(double v) => v >= 1e9 ? "-" : v.ToString("F2", inv);
    }
}
