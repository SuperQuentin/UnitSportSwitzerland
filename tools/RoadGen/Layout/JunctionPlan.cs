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
    public bool HasLeft => Mouth.Strips.Any(s => s.Id is >= MouthPlanner.LeftPocketId and < MouthPlanner.LeftPocketId + 10);
    public bool HasRight => Mouth.Strips.Any(s => s.Id == MouthPlanner.RightPocketId);
}

/// <summary>A junction as the planner lays it out (#818): its arms, step 1 of <c>docs/plans/junction-layout.md</c>.</summary>
public sealed class JunctionPlan
{
    public required int Node { get; init; }
    public required Vec2 Centre { get; init; }
    public required bool Lights { get; init; }
    public List<ArmPlan> Arms { get; } = new();

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
        int doubles = arms.Count(a => a.Mouth.Strips.Count(s => s.Id is >= MouthPlanner.LeftPocketId and < MouthPlanner.LeftPocketId + 10) >= 2);
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
        return sb.ToString();

        string Fmt(double v) => v >= 1e9 ? "-" : v.ToString("F2", inv);
    }
}
