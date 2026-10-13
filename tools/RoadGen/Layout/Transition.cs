namespace UnitSport.Tools.RoadGen.Layout;

using System.Globalization;
using System.Text;

/// <summary>A width (or offset) along a transition: knots (station, value), linear between them, held beyond either end.</summary>
public sealed class Profile
{
    private readonly List<(double S, double V)> _knots = new();

    public Profile(double value) => _knots.Add((0, value));

    public IReadOnlyList<(double S, double V)> Knots => _knots;

    /// <summary>The value held from the last knot to <paramref name="s"/>, then linear to <paramref name="v"/> at <paramref name="to"/>.</summary>
    public Profile HoldThen(double s, double to, double v)
    {
        double held = _knots[^1].V;
        if (s > _knots[^1].S + 1e-9) _knots.Add((s, held));
        if (to > _knots[^1].S + 1e-9) _knots.Add((to, v));
        else _knots[^1] = (_knots[^1].S, v);
        return this;
    }

    public double At(double s)
    {
        if (s <= _knots[0].S) return _knots[0].V;
        for (int k = 1; k < _knots.Count; k++)
            if (s <= _knots[k].S)
            {
                var (s0, v0) = _knots[k - 1];
                var (s1, v1) = _knots[k];
                return s1 - s0 < 1e-9 ? v1 : v0 + (v1 - v0) * (s - s0) / (s1 - s0);
            }
        return _knots[^1].V;
    }

    public double End => _knots[^1].S;
}

/// <summary>One strip of a transition: what it is (its width field unused) and its width at each of the transition's stations.</summary>
public sealed record TransitionStrip(Strip Strip, double[] Widths);

/// <summary>
/// A stretch of an arm going from one cross-section to another (#818, step 4 of <c>docs/plans/junction-layout.md</c>): from its
/// arm's mouth section at station 0 (the mouth) out to the street's normal section, or to the far junction's mouth section
/// where the street between is too short for two (<see cref="Far"/>: both ends planned together, #325). Every strip keeps its
/// place left to right, its width linear between the stations (a lane that opens or closes is 0 wide where it is not); the
/// centre line is the anchor: the strips left of it stack out from it to the left, the others to the right. Offsets and
/// sides as a <see cref="Section"/> counts them (seen driving in at station 0, metres right of the arm's axis).
/// </summary>
public sealed class Transition
{
    public required ArmPlan Arm { get; init; }
    /// <summary>The arm whose mouth is at <see cref="Length"/>, null where the transition ends in the normal section.</summary>
    public ArmPlan? Far { get; init; }
    /// <summary>Metres out from the mouth, increasing; the first 0.</summary>
    public required double[] Stations { get; init; }
    public required List<TransitionStrip> Strips { get; init; }
    /// <summary>The centre line's offset from the axis at each station.</summary>
    public required double[] Centre { get; init; }
    /// <summary>Strips before this index lie left of the centre line, the rest right of it.</summary>
    public required int Anchor { get; init; }
    public int KerbLeftCm { get; init; }
    public int KerbRightCm { get; init; }
    /// <summary>What happens along it, in order, for <c>--debug-junction</c> (e.g. "lane 100 closes 40-50 m").</summary>
    public List<string> Events { get; } = new();
    /// <summary>Its events did not fit the street before the far end (m squeezed off; a check).</summary>
    public double Squeezed { get; set; }

    public double Length => Stations[^1];

    private static double Lerp(double[] values, double[] at, double s)
    {
        if (s <= at[0]) return values[0];
        for (int k = 1; k < at.Length; k++)
            if (s <= at[k])
                return at[k] - at[k - 1] < 1e-9 ? values[k] : values[k - 1] + (values[k] - values[k - 1]) * (s - at[k - 1]) / (at[k] - at[k - 1]);
        return values[^1];
    }

    public double WidthAt(int strip, double s) => Lerp(Strips[strip].Widths, Stations, s);

    public double CentreAt(double s) => Lerp(Centre, Stations, s);

    /// <summary>Where strip <paramref name="strip"/> lies across the arm at station <paramref name="s"/>.</summary>
    public (double From, double To) SpanAt(int strip, double s)
    {
        double at = CentreAt(s);
        if (strip >= Anchor)
        {
            for (int k = Anchor; k < strip; k++) at += WidthAt(k, s);
            return (at, at + WidthAt(strip, s));
        }
        for (int k = Anchor - 1; k > strip; k--) at -= WidthAt(k, s);
        return (at - WidthAt(strip, s), at);
    }

    /// <summary>The offset of the boundary on the left of strip <paramref name="strip"/> (<see cref="Strips"/>.Count: right of the last).</summary>
    public double BoundaryAt(int strip, double s) => strip < Strips.Count ? SpanAt(strip, s).From : SpanAt(Strips.Count - 1, s).To;

    /// <summary>The section at station <paramref name="s"/>: the strips wider than a millimetre.</summary>
    public Section At(double s)
    {
        var strips = new List<Strip>();
        double left = double.NaN;
        for (int k = 0; k < Strips.Count; k++)
        {
            double w = WidthAt(k, s);
            if (w < 1e-3) continue;
            var x = Strips[k].Strip.WithWidth(w);
            if (x.InCarriageway && double.IsNaN(left)) left = SpanAt(k, s).From;
            strips.Add(x);
        }
        return new Section(strips, double.IsNaN(left) ? CentreAt(s) : left) { KerbLeftCm = KerbLeftCm, KerbRightCm = KerbRightCm };
    }

    /// <summary>Its stations and every strip's widths there, for <c>--debug-junction</c>.</summary>
    public string Describe()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(inv, $"    transition {Length:F1} m{(Far is { } f ? $" to arm {f.Arm}'s mouth beyond (one transition for both ends)" : "")}{(Squeezed > 0.05 ? $", squeezed {Squeezed:F1} m" : "")}: ")
          .Append(string.Join("; ", Events)).Append('\n');
        sb.Append("      at   ").Append(string.Join(" ", Stations.Select(s => s.ToString("F1", inv).PadLeft(6)))).Append('\n');
        for (int k = 0; k < Strips.Count; k++)
        {
            var x = Strips[k].Strip;
            if (k == Anchor) sb.Append("      ---- centre line\n");
            sb.Append(inv, $"      {Section.Name(x.Kind) + (x.Way == StripWay.Out ? ">" : x.Way == StripWay.In ? "<" : ""),-7}{x.Id,4} ")
              .Append(string.Join(" ", Strips[k].Widths.Select(w => w.ToString("F2", inv).PadLeft(6)))).Append('\n');
        }
        return sb.ToString();
    }
}
