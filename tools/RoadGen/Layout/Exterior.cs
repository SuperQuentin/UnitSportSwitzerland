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

/// <summary>
/// A kerbed piece of a lit arm's island (#1000): stations and offsets as a bar's, its ends rounded where they stand free (the
/// junction's tip, the far end on the gore), square where the crosswalk cuts it.
/// </summary>
public sealed record IslandPiece(double From, double To, double Left, double Right, bool RoundFrom, bool RoundTo);

/// <summary>A traffic-light pole beside a bar: its station, which kerb (or the refuge), its flags.</summary>
public sealed record PoleSpot(double Station, bool RightKerb, bool OnRefuge, SignalPoleFlags Flags);

/// <summary>
/// A priority sign beside an arm (#121): its station, type and variant, on the arriving traffic's right (else its left) and facing
/// it (else the traffic leaving: a rural 3.03 stands after the junction).
/// </summary>
/// <param name="OnMainPole">A lit approach's priority sign (for when the lights are off): on its main signal pole, not a post of its
/// own (the old layout's rule, #350/#711; #1000).</param>
public sealed record SignSpot(double Station, PointPropType Type, byte Variant, bool RightKerb, bool FacesArriving, bool OnMainPole = false);

/// <summary>
/// An arm's exterior (#818 phase 4, the user's generalization): one stack of bars laid outward from its mouth (the crosswalk, the
/// riders' stop, the cars' stop line or give-way row), the poles and signs at the kerb beside the bar they belong to.
/// </summary>
public sealed class ArmExterior
{
    public required ArmPlan Arm { get; init; }
    public List<ExteriorBar> Bars { get; } = new();
    public Refuge? Refuge { get; set; }
    /// <summary>A lit arm's island laid as kerbed pieces (split by its crosswalk, #1000); empty: the refuge whole.</summary>
    public List<IslandPiece> Islands { get; } = new();
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
    /// <summary>
    /// A lit arm's poles stand this far out past its crosswalk, on its near side, and its riders' line starts
    /// <see cref="RiderLineBehindPole"/> behind them; with no crosswalk, <see cref="PoleAlong"/> out from the mouth, by the corner
    /// (m; the old layout's rules, #350/#711, restored by #1000).
    /// </summary>
    public const double PoleBehindCrosswalk = 0.4, RiderLineBehindPole = 0.5, PoleAlong = 0.4;
    public const double RidersBehindCrosswalk = 0.5, RidersLineWidth = 0.3, AdvancedLine = 3.0, BikeBoxDepth = 4.0;
    public const double PocketBarFromMouth = 0.1;
    /// <summary>A give-way row stands this far out past the mouth (or what lies before it) and keeps this far off the lane edges.</summary>
    public const double TeethSetback = 0.25, TeethMargin = 0.15;
    /// <summary>A 3.02 sign stands this far behind its row.</summary>
    public const double SignBehindTeeth = 2.0;
    /// <summary>A refuge: an island of at least 1.2 m with 0.25 m of hatch either side, running on 1 m past the crosswalk each way.</summary>
    public const double RefugeMargin = 0.25, RefugeMinWidth = 1.2, RefugeBeyond = 1.0;
    /// <summary>A left repeater's island at the lights (#682, #854): from this far out past the mouth to this far past the cars' line.</summary>
    public const double IslandFromMouth = 0.5, IslandPastStop = 2.0;

    /// <summary>
    /// A lit arm's island carrying the left repeater runs this far into the junction past the mouth, its pole half of it in,
    /// plus a tenth of the island's length on the arm (m; the old layout's islands, #682: the user, #1000, the pole in the
    /// island's middle stood beside the riders waiting in the left pocket, who could not see its head).
    /// </summary>
    public const double IslandInside = 3.0;

    /// <summary>A lit arm's island stops this far short of its crosswalk on either side (m; the old layout's, #682), and a piece
    /// shorter than <see cref="MinIslandPiece"/> is left out.</summary>
    public const double CrosswalkCut = 0.15, MinIslandPiece = 0.8;

    /// <summary>
    /// An island piece from <paramref name="from"/> to <paramref name="to"/> within the exit's gore all along, from the
    /// <paramref name="left"/>..<paramref name="right"/> it was planned at: as wide as the gore's narrowest station lets it (a
    /// margin clear of both edges), ending where the gore gets narrower than an island; null where it is too narrow at once.
    /// </summary>
    /// <summary>How far into the junction (a negative station, from <paramref name="from"/> toward <paramref name="to"/>) an island's tip
    /// may reach with every car's way through the core clear of it by half its width and <see cref="IslandCarClear"/>.</summary>
    private static double ClearTip(JunctionPlan jp, ArmPlan a, double from, double to, double left, double right)
    {
        if (jp.Core is not { } core || a.Axis is not { } axis) return to;
        double m0 = core.Mouths.First(m => m.Arm == a.Arm).Station;
        var cars = core.Connectors.Where(c => c.Kind == ConnectorKind.Car).ToList();
        for (double s = from; s < to; s += 0.25)
        {
            // the part from s to the mouth, as points along its edges and middle
            bool clear = true;
            for (double t = s; t <= to + 1e-6 && clear; t += 0.5)
                foreach (double o in (ReadOnlySpan<double>)[left, (left + right) * 0.5, right])
                {
                    var p = axis.At(m0 + t, o);
                    if (cars.Any(c => Near(c.Centre, p) < c.Width * 0.5 + IslandCarClear)) { clear = false; break; }
                }
            if (clear) return s;
        }
        return to;

        static double Near(List<Geometry.Vec2> line, Geometry.Vec2 p)
        {
            double best = double.MaxValue;
            for (int i = 1; i < line.Count; i++)
            {
                var ab = line[i] - line[i - 1];
                double l2 = ab.Dot(ab), t = l2 < 1e-12 ? 0 : Math.Clamp((p - line[i - 1]).Dot(ab) / l2, 0, 1);
                best = Math.Min(best, (line[i - 1] + ab * t).DistanceTo(p));
            }
            return best;
        }
    }

    /// <summary>An island inside the junction keeps this far clear of a car's way beyond half its width (m).</summary>
    public const double IslandCarClear = 0.4;

    private static (double Left, double Right, double To)? FitIsland(ArmPlan a, double from, double to, double left, double right)
    {
        double l = left, r = right, end = from;
        for (double s = Math.Max(0, from); ; s = Math.Min(to, s + 0.5))
        {
            if (ExitHatch(a, s) is not { } h) break;
            double nl = Math.Max(l, h.Left + RefugeMargin), nr = Math.Min(r, h.Right - RefugeMargin);
            if (nr - nl < RefugeMinWidth) break;
            (l, r, end) = (nl, nr, s);
            if (s >= to) break;
        }
        if (from < 0 && end <= 0) end = Math.Min(to, 0);
        return end > from ? (l, r, end) : null;
    }
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

    /// <summary>The exit hatch's span at station <paramref name="s"/> (a hatch left of the centre line; a lead-in's lies right of it).</summary>
    private static (double Left, double Right)? ExitHatch(ArmPlan a, double s)
    {
        var sec = SectionAt(a, s);
        for (int k = 0; k < sec.Strips.Count; k++)
            if (sec.Strips[k].Kind == StripKind.Hatch && sec.Strips[k].Way == StripWay.None && sec.Strips[k].Width > 0.05)
            {
                var (l, r) = sec.Span(k);
                if (sec.CentreLine is { } c && r > c + 1e-3) continue;
                return (l, r);
            }
        return null;
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
                // (beside a crosswalk the riders wait a little behind the pole, the old layout's #711)
                double riders = s + (crosswalkAt is not null ? PoleBehindCrosswalk + RiderLineBehindPole : StopFromMouth);
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
            // without a crosswalk's refuge, an island over the exit hatch's wide end where it is wide enough all along, from the
            // mouth to past the cars' line: the left repeater's (ExitIslands, #682; the old layout's, #854)
            if (ex.Refuge is null && JunctionRules.Lights.Has(JunctionRule.ExitIslands) && ExitHatch(a, IslandFromMouth) is { } near
                && ExitHatch(a, carFront + width + IslandPastStop) is { } far)
            {
                double l = Math.Max(near.Left, far.Left) + RefugeMargin, r = Math.Min(near.Right, far.Right) - RefugeMargin;
                if (r - l >= RefugeMinWidth) ex.Refuge = new Refuge(IslandFromMouth, carFront + width + IslandPastStop, l, r);
            }
            // the poles: the main one at the right kerb, the second at the left one or on the refuge; beside a crosswalk on its near
            // side, past every line, else by the corner (the old layout's rules, #350/#711; #1000: at the cars' line they stood behind
            // the riders' line and box)
            double poleAt = crosswalkAt is not null ? s + PoleBehindCrosswalk : PoleAlong;
            // the approach's priority signs for when the lights are off on its main pole (the old layout's, #350/#711): its main-road
            // sign, and a give-way sign where it would yield
            for (int i = 0; i < ex.Signs.Count; i++)
                if (ex.Signs[i].FacesArriving) ex.Signs[i] = ex.Signs[i] with { Station = poleAt, RightKerb = true, OnMainPole = true };
            foreach (var sign in signs ?? [])
                if (sign.FacesArriving && sign.Type == PointPropType.YieldSign)
                    ex.Signs.Add(new SignSpot(poleAt, sign.Type, sign.Variant, RightKerb: true, FacesArriving: true, OnMainPole: true));
            bool second = a.HasLeft || a.HasRight || here.CarLanes(StripWay.In).Count() > 1;
            // pedestrian heads only where the arm has a crosswalk (#711; where OSM maps its crossings on other arms, none here)
            var walk = crosswalkAt is not null ? SignalPoleFlags.Pedestrian : 0;
            ex.Poles.Add(new PoleSpot(poleAt, RightKerb: true, OnRefuge: false, SignalPoleFlags.Main | walk));
            // the left repeater's island (the old layout's, #682; #1000): carried 3 m into the junction, split by the crosswalk (which
            // runs level through it), behind it on to the gore past the cars' line, each piece as wide as the gore lets it be all
            // along and rounded where it stands free; its pole ahead of every line: behind the crosswalk where there is one (seen
            // from the lines, the user), else on the part inside the junction
            double islandPole = 0;
            var zebra = ex.Bars.FirstOrDefault(b => b.Kind == BarKind.Crosswalk);
            if (ex.Refuge is { } island)
            {
                double gore = Math.Max(island.To, carFront + width + IslandPastStop);
                var pieces = zebra is not null
                    ? new[] { (From: -IslandInside, To: zebra.From - CrosswalkCut, RoundFrom: true, RoundTo: false), (zebra.To + CrosswalkCut, gore, false, true) }
                    : [(-IslandInside, gore, true, true)];
                foreach (var (from, to, roundFrom, roundTo) in pieces)
                    if (FitIsland(a, from, to, island.Left, island.Right) is var (l, r, end))
                    {
                        // inside the junction no further than every car's way keeps clear of it (#1000: a left turn clipped the tip)
                        double start = from < 0 ? ClearTip(jp, a, from, Math.Min(end, 0), l, r) : from;
                        if (end - start >= MinIslandPiece) ex.Islands.Add(new IslandPiece(start, end, l, r, roundFrom, roundTo || end < to - 1e-6));
                    }
                if (zebra is not null) ex.Bars[ex.Bars.IndexOf(zebra)] = zebra with { Gap = null };
                ex.Refuge = ex.Islands.Count == 0 ? null : island with { From = ex.Islands[0].From, To = ex.Islands[^1].To };
                islandPole = zebra is not null ? zebra.To + PoleBehindCrosswalk : -IslandInside * 0.5 + gore * 0.1;
                // (where the gore left no room for the piece it stands on: on the nearest piece there is)
                if (ex.Islands.Count > 0 && !ex.Islands.Any(x => x.From + 0.3 <= islandPole && islandPole <= x.To - 0.3))
                {
                    var on = ex.Islands.MinBy(x => Math.Min(Math.Abs(x.From - islandPole), Math.Abs(x.To - islandPole)))!;
                    islandPole = Math.Clamp(islandPole, on.From + 0.3, Math.Max(on.From + 0.3, on.To - 0.3));
                }
            }
            // the left kerb's pole for the walkers' head; the repeater on the island where there is one (#1000: the island's pole had
            // taken the left kerb's, its walkers' head with it)
            if (ex.Refuge is not null)
            {
                if (walk != 0) ex.Poles.Add(new PoleSpot(poleAt, RightKerb: false, OnRefuge: false, walk));
                if (second) ex.Poles.Add(new PoleSpot(islandPole, RightKerb: false, OnRefuge: true, SignalPoleFlags.Second));
            }
            else ex.Poles.Add(new PoleSpot(poleAt, RightKerb: false, OnRefuge: false, (second ? SignalPoleFlags.Second : 0) | walk));
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
