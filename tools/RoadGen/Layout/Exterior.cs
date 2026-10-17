namespace UnitSport.Tools.RoadGen.Layout;

using System.Globalization;
using System.Text;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Junctions;

/// <summary>What a bar across an arm is (#818 phase 4).</summary>
public enum BarKind : byte
{
    /// <summary>A zebra (SSV 6.17, yellow bars) across the carriageway, a refuge's span left out.</summary>
    Crosswalk,
    /// <summary>The riders' stop line at the lights (yellow, 0.30 m) across a bike lane.</summary>
    RidersLine,
    /// <summary>A bike box (SSV 6.26): the area in front of the cars' line across the arriving car lanes.</summary>
    BikeBox,
    /// <summary>The cars' stop line at the lights (SSV 6.10, 0.50 m) across a run of arriving car lanes.</summary>
    StopLine,
    /// <summary>A pocket's stop bar without lights (0.40 m).</summary>
    PocketBar,
    /// <summary>A give-way row (Wartelinie, SSV 6.13) across the arriving lanes of a yielding arm.</summary>
    GiveWay,
}

/// <summary>
/// One bar across an arm (#818 phase 4): from station <see cref="From"/> to <see cref="To"/> (metres out from the mouth: its
/// junction edge and its far edge), across <see cref="Left"/> to <see cref="Right"/> (right of a driver arriving), a span left out
/// (<see cref="Gap"/>: a refuge island the crosswalk passes).
/// </summary>
public sealed record ExteriorBar(BarKind Kind, double From, double To, double Left, double Right, (double Left, double Right)? Gap = null)
{
    public double Middle => (From + To) * 0.5;
}

/// <summary>A refuge island in the exit hatch at a crosswalk: stations and offsets as a bar's.</summary>
public sealed record Refuge(double From, double To, double Left, double Right);

/// <summary>A traffic-light pole beside a bar: its station, which kerb (or the refuge), its flags.</summary>
public sealed record PoleSpot(double Station, bool RightKerb, bool OnRefuge, SignalPoleFlags Flags);

/// <summary>
/// A priority sign beside an arm (#121): its station, type and variant, on the arriving traffic's right (else its left) and facing
/// it (else the traffic leaving: a rural 3.03 stands after the junction).
/// </summary>
public sealed record SignSpot(double Station, PointPropType Type, byte Variant, bool RightKerb, bool FacesArriving);

/// <summary>
/// An arm's exterior (#818 phase 4, the user's generalization): one stack of bars laid outward from its mouth (the crosswalk, the
/// riders' stop, the cars' stop line or give-way row), the poles and signs at the kerb beside the bar they belong to.
/// </summary>
public sealed class ArmExterior
{
    public required ArmPlan Arm { get; init; }
    public List<ExteriorBar> Bars { get; } = new();
    public Refuge? Refuge { get; set; }
    public List<PoleSpot> Poles { get; } = new();
    public List<SignSpot> Signs { get; } = new();
    /// <summary>Where the arriving cars stop: the middle of their stop line (or bar, or row), m out from the mouth; null where none arrive.</summary>
    public double? CarStop { get; set; }
    /// <summary>Where the arriving riders stop at the lights; null without a riders' line.</summary>
    public double? RiderStop { get; set; }
    /// <summary>The crosswalk's stations, null without one.</summary>
    public (double From, double To)? Crosswalk => Bars.FirstOrDefault(b => b.Kind == BarKind.Crosswalk) is { } c ? (c.From, c.To) : null;

    public string Describe()
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("    exterior ");
        sb.Append(string.Join("; ", Bars.Select(b => string.Create(inv, $"{b.Kind} {b.From:F1}-{b.To:F1} m across {b.Left:F2}..{b.Right:F2}{(b.Gap is { } g ? $" (gap {g.Left:F2}..{g.Right:F2})" : "")}"))));
        if (Refuge is { } r) sb.Append(inv, $"; refuge {r.From:F1}-{r.To:F1} m across {r.Left:F2}..{r.Right:F2}");
        foreach (var p in Poles) sb.Append(inv, $"; pole {(p.OnRefuge ? "refuge" : p.RightKerb ? "right" : "left")} at {p.Station:F1} m ({p.Flags})");
        foreach (var s in Signs) sb.Append(inv, $"; sign {s.Type} at {s.Station:F1} m");
        return sb.Append('\n').ToString();
    }
}

/// <summary>
/// Lays each arm's exterior (#818 phase 4 of <c>docs/plans/junction-layout.md</c>, the user's generalization): one stack of bars
/// outward from the mouth, each across the strips of its kind in the arm's section at its station (<see cref="SectionAt"/>). The
/// crosswalk right outside the mouth (or where OSM maps it further out), 3 m deep, a refuge where the exit hatch is wide enough;
/// at the lights one riders' line across the bike lanes, the cars' line 3 m behind it for an advanced line or 4 m for a bike box
/// (the box across the car lanes between), else 2 m behind the crosswalk; without lights a pocket's bar and, on a yielding arm, the
/// give-way row. The stop distance falls out of the stack; <see cref="JunctionRules"/> only says which bars exist. Pure.
/// </summary>
public static class ExteriorPlanner
{
    public const double CrosswalkFromMouth = 0.5, CrosswalkDepth = 3.0, StopBehindCrosswalk = 2.0, StopFromMouth = 0.5;
    public const double RidersBehindCrosswalk = 0.5, RidersLineWidth = 0.3, AdvancedLine = 3.0, BikeBoxDepth = 4.0;
    public const double PocketBarFromMouth = 0.1;
    /// <summary>A give-way row stands this far out past the mouth (or what lies before it) and keeps this far off the lane edges.</summary>
    public const double TeethSetback = 0.25, TeethMargin = 0.15;
    /// <summary>A 3.02 sign stands this far behind its row.</summary>
    public const double SignBehindTeeth = 2.0;
    /// <summary>A refuge: an island of at least 1.2 m with 0.25 m of hatch either side, running on 1 m past the crosswalk each way.</summary>
    public const double RefugeMargin = 0.25, RefugeMinWidth = 1.2, RefugeBeyond = 1.0;
    /// <summary>A 3.03 main-road sign stands this far out from the mouth.</summary>
    public const double MainSignOut = 3.0;

    /// <summary>
    /// The arm's section at station <paramref name="s"/> out from its mouth, seen driving in: its transition's there (the far end
    /// of a shared one mirrored), its mouth section without one, its normal section past its transition.
    /// </summary>
    public static Section SectionAt(ArmPlan a, double s)
    {
        if (a.Transition is not { } t) return a.Mouth;
        if (t.Arm == a) return s >= t.Length ? a.Normal : t.At(Math.Max(0, s));
        return Mirrored(t.At(Math.Clamp(t.Length - s, 0, t.Length)));
    }

    /// <summary>A section seen from the other way: its strips reversed, their ways swapped, its offsets mirrored.</summary>
    public static Section Mirrored(Section s)
    {
        var strips = s.Strips.Reverse().Select(x => x with { Way = x.Way == StripWay.In ? StripWay.Out : x.Way == StripWay.Out ? StripWay.In : StripWay.None }).ToList();
        return new Section(strips, -s.CarriagewayRight) { KerbLeftCm = s.KerbRightCm, KerbRightCm = s.KerbLeftCm };
    }

    /// <summary>The runs of side-by-side strips <paramref name="pick"/> takes, each as its left and right offsets.</summary>
    public static List<(double Left, double Right)> Runs(Section s, Func<Strip, bool> pick)
    {
        var runs = new List<(double, double)>();
        (double L, double R)? cur = null;
        for (int k = 0; k < s.Strips.Count; k++)
        {
            if (!s.Strips[k].InCarriageway) continue;
            var (from, to) = s.Span(k);
            if (pick(s.Strips[k])) cur = cur is { } c ? (c.L, to) : (from, to);
            else if (cur is { } c) { runs.Add(c); cur = null; }
        }
        if (cur is { } last) runs.Add(last);
        return runs;
    }

    private static bool CarIn(Strip x) => x.IsCarLane && x.Way == StripWay.In;

    /// <summary>
    /// The bike boxes in front of an approach's cars (the user, Oct 17): one per bike lane stopping at a box, over that lane and
    /// the car lanes up to the nearest one making its riders' move (not the whole approach), two merged only where they touch.
    /// </summary>
    private static List<(double Left, double Right)> BikeBoxes(Section sec)
    {
        var cars = sec.Strips.Select((x, k) => (x, k)).Where(p => CarIn(p.x)).Select(p => (Span: sec.Span(p.k), p.x.Moves)).ToList();
        var boxes = new List<(double Left, double Right)>();
        for (int k = 0; k < sec.Strips.Count; k++)
        {
            var x = sec.Strips[k];
            if (!BikeIn(x) || x.Stop != BikeStop.Box) continue;
            var (bl, br) = sec.Span(k);
            var move = (x.Moves & SignalMoves.Through) != 0 ? SignalMoves.Through : (x.Moves & SignalMoves.Left) != 0 ? SignalMoves.Left : SignalMoves.Right;
            double mid = (bl + br) * 0.5;
            var lane = cars.Where(c => c.Moves == SignalMoves.None || (c.Moves & move) != 0)
                .OrderBy(c => Math.Abs((c.Span.From + c.Span.To) * 0.5 - mid)).Select(c => ((double From, double To)?)c.Span).FirstOrDefault()
                ?? cars.OrderBy(c => Math.Abs((c.Span.From + c.Span.To) * 0.5 - mid)).Select(c => ((double From, double To)?)c.Span).FirstOrDefault();
            boxes.Add(lane is { } c0 ? (Math.Min(bl, c0.From), Math.Max(br, c0.To)) : (bl, br));
        }
        boxes.Sort((p, q) => p.Left.CompareTo(q.Left));
        var merged = new List<(double Left, double Right)>();
        foreach (var b in boxes)
            if (merged.Count > 0 && b.Left <= merged[^1].Right + 0.1) merged[^1] = (merged[^1].Left, Math.Max(merged[^1].Right, b.Right));
            else merged.Add(b);
        return merged;
    }
    private static bool BikeIn(Strip x) => x.Kind == StripKind.BikeLane && x.Way == StripWay.In;

    /// <param name="crosswalkAt">The crosswalk's junction edge, m out from the mouth (null for none): right outside it, or where OSM maps it.</param>
    /// <param name="yields">The arm gives way (no lights): its give-way row.</param>
    /// <param name="signs">The priority plan's signs for this arm (#121): a 3.02 goes behind the row, a 3.03 <see cref="MainSignOut"/> m out.</param>
    public static ArmExterior Plan(JunctionPlan jp, ArmPlan a, double? crosswalkAt, bool yields,
        IReadOnlyList<(PointPropType Type, byte Variant, bool FacesArriving)>? signs = null)
    {
        var ex = new ArmExterior { Arm = a };
        double s = 0;
        if (crosswalkAt is { } at)
        {
            double from = Math.Max(CrosswalkFromMouth, at), to = from + CrosswalkDepth;
            var sec = SectionAt(a, (from + to) * 0.5);
            (double, double)? gap = null;
            // a refuge where the exit hatch leaves room for an island with hatch either side (#711)
            for (int k = 0; k < sec.Strips.Count; k++)
                if (sec.Strips[k].Kind == StripKind.Hatch && sec.Strips[k].Way == StripWay.None && sec.Strips[k].Width >= RefugeMinWidth + 2 * RefugeMargin)
                {
                    var (l, r) = sec.Span(k);
                    // the exit hatch, left of the centre line (a lead-in's lies right of it, with nothing to cross to)
                    if (sec.CentreLine is { } c && r > c + 1e-3) continue;
                    ex.Refuge = new Refuge(from - RefugeBeyond, to + RefugeBeyond, l + RefugeMargin, r - RefugeMargin);
                    gap = (l + RefugeMargin, r - RefugeMargin);
                    break;
                }
            ex.Bars.Add(new ExteriorBar(BarKind.Crosswalk, from, to, sec.CarriagewayLeft, sec.CarriagewayRight, gap));
            s = to;
        }
        // a main-road sign past whatever the stack holds by the mouth: urban before the junction for the arriving traffic, rural
        // after it for the leaving traffic (on its right: the arriving traffic's left)
        foreach (var sign in signs ?? [])
            if (sign.Type == PointPropType.MainRoadSign)
                ex.Signs.Add(new SignSpot(Math.Max(MainSignOut, s + 1), sign.Type, sign.Variant, RightKerb: sign.FacesArriving, sign.FacesArriving));
        var here = SectionAt(a, s + 1);
        bool arrives = here.CarLanes(StripWay.In).Any();
        // a street to another lit node closer than 30 m is inside the lights (#348): no line, the traffic on it is in the junction
        if (!arrives || (jp.Lights && a.InsideLights)) return ex;
        if (jp.Lights)
        {
            var bikes = Runs(here, BikeIn);
            double carFront;
            if (bikes.Count > 0)
            {
                double riders = s + (crosswalkAt is not null ? RidersBehindCrosswalk : StopFromMouth);
                foreach (var (l, r) in bikes) ex.Bars.Add(new ExteriorBar(BarKind.RidersLine, riders, riders + RidersLineWidth, l, r));
                ex.RiderStop = riders + RidersLineWidth * 0.5;
                var stops = SectionAt(a, riders).Strips.Where(BikeIn).Select(x => x.Stop).ToList();
                double behind = stops.Contains(BikeStop.Box) ? BikeBoxDepth : stops.Contains(BikeStop.Advanced) ? AdvancedLine : 0;
                carFront = riders + Math.Max(RidersLineWidth, behind);
                if (stops.Contains(BikeStop.Box))
                    foreach (var (l, r) in BikeBoxes(SectionAt(a, riders + 2)))
                    {
                        // the riders' line across the box's whole front (the user, Oct 17: a box without its stop line)
                        ex.Bars.Add(new ExteriorBar(BarKind.RidersLine, riders, riders + RidersLineWidth, l, r));
                        ex.Bars.Add(new ExteriorBar(BarKind.BikeBox, riders + RidersLineWidth, carFront, l, r));
                    }
            }
            else carFront = s + (crosswalkAt is not null ? StopBehindCrosswalk : StopFromMouth);
            double width = JunctionRules.StopLineWidth;
            foreach (var (l, r) in Runs(SectionAt(a, carFront + width * 0.5), CarIn))
                ex.Bars.Add(new ExteriorBar(BarKind.StopLine, carFront, carFront + width, l, r));
            ex.CarStop = carFront + width * 0.5;
            // the poles beside the cars' line: the main one at the right kerb, the second at the left one or on the refuge
            bool second = a.HasLeft || a.HasRight || here.CarLanes(StripWay.In).Count() > 1;
            ex.Poles.Add(new PoleSpot(carFront + width * 0.5, RightKerb: true, OnRefuge: false, SignalPoleFlags.Main | SignalPoleFlags.Pedestrian));
            ex.Poles.Add(new PoleSpot(ex.Refuge is { } rf ? (rf.From + rf.To) * 0.5 : carFront + width * 0.5, RightKerb: false, OnRefuge: ex.Refuge is not null,
                (second ? SignalPoleFlags.Second : 0) | SignalPoleFlags.Pedestrian));
            return ex;
        }
        // without lights: a pocket's own bar, and on a yielding arm the give-way row across the arriving lanes
        double barAt = s + (crosswalkAt is not null ? RidersBehindCrosswalk : PocketBarFromMouth);
        var pockets = Runs(SectionAt(a, barAt), x => CarIn(x) && PocketRules.Of(x.Id) is not null);
        foreach (var (l, r) in pockets)
            ex.Bars.Add(new ExteriorBar(BarKind.PocketBar, barAt, barAt + JunctionRules.StopBarWidth, l, r));
        if (pockets.Count > 0) ex.CarStop = barAt + JunctionRules.StopBarWidth * 0.5;
        if (yields)
        {
            double rowAt = s + TeethSetback;
            var row = SectionAt(a, rowAt);
            var lanes = Runs(row, x => x.Way == StripWay.In && (x.IsCarLane || x.Kind == StripKind.BikeLane));
            if (lanes.Count > 0)
            {
                double l = lanes.Min(x => x.Left) + TeethMargin, r = lanes.Max(x => x.Right) - TeethMargin;
                if (r - l >= RoadSigns.ToothBase)
                {
                    ex.Bars.Add(new ExteriorBar(BarKind.GiveWay, rowAt, rowAt + RoadSigns.ToothHeight, l, r));
                    ex.CarStop ??= rowAt + RoadSigns.ToothHeight * 0.5;
                    foreach (var sign in signs ?? [])
                        if (sign.Type == PointPropType.YieldSign)
                            ex.Signs.Add(new SignSpot(rowAt + RoadSigns.ToothHeight + SignBehindTeeth, sign.Type, sign.Variant, RightKerb: true, FacesArriving: true));
                }
            }
        }
        return ex;
    }
}
