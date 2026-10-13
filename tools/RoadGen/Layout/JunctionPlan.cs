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
    /// <summary>The shrink steps taken (<see cref="MouthPlanner.Fit"/>) and other choices, in order.</summary>
    public List<string> Steps { get; } = new();
    /// <summary>How far the mouth section stands to the left of the normal one's axis: a split lead-in's share (#700).</summary>
    public double Split { get; set; }
    /// <summary>The arm the through lane leaves by (-1 none).</summary>
    public int Exit { get; init; } = -1;
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
            if (a.Steps.Count > 0) sb.Append("    steps   ").Append(string.Join("; ", a.Steps)).Append('\n');
        }
        return sb.ToString();

        string Fmt(double v) => v >= 1e9 ? "-" : v.ToString("F2", inv);
    }
}
