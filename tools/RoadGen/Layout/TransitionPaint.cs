namespace UnitSport.Tools.RoadGen.Layout;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Meshing;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// A line painted along a transition (#818): along the boundary on the left of strip <see cref="Boundary"/> (the strips' count:
/// right of the last), <see cref="Offset"/> to its right, from station <see cref="From"/> to <see cref="To"/>, in a style.
/// </summary>
public sealed record TransitionLine(int Boundary, double Offset, double From, double To, PaintType Type, uint Rgba, float Width, float Dash, float Gap);

/// <summary>An arrow or a bike symbol in a strip of a transition: its tail's station, and which way it points (toward the near mouth or the far one).</summary>
public sealed record TransitionMark(int Strip, double Station, PaintArrow Arrow, bool Bike, bool TowardFar);

/// <summary>What the street paints where the transition meets it (<see cref="PaintEmitter"/>'s rules for its segment).</summary>
/// <param name="CentreLine">The street has a centre line (two-way, wide enough, no Kernfahrbahn).</param>
/// <param name="SolidCentre">Its centre line is solid (four lanes or more, #700).</param>
/// <param name="EdgeLines">Randlinien (outside built-up areas).</param>
/// <param name="Dash">Its Leitlinie's dash and gap.</param>
public readonly record struct StreetPaint(bool CentreLine, bool SolidCentre, bool EdgeLines, float Dash, float Gap)
{
    /// <summary>A town street of a carriageway that wide: a dashed centre line where it is wide enough, no edge lines.</summary>
    public static StreetPaint Town(double width) => new(width >= PaintEmitter.MinCentreLineWidth(true), false, false, 3f, 6f);

    /// <summary>What <see cref="PaintEmitter.Emit"/> paints on <paramref name="seg"/> (a two-way street's rules; one way: no centre line).</summary>
    public static StreetPaint Of(RoadSegment seg)
    {
        var a = seg.Attributes;
        bool oneWay = a.OneWay != 0 || (seg.Flags & RoadFlags.Divided) != 0 || RoadCrossSection.IsHighSpeed(seg.Class) || seg.Class == RoadClass.Ramp;
        bool urban = a.Has(RoadAttrFlags.Urban);
        bool centre = !oneWay && !BikePlanner.IsKernfahrbahn(seg.Width, a.Left, a.Right) && seg.Width >= PaintEmitter.MinCentreLineWidth(urban);
        var (dash, gap) = PaintEmitter.Leitlinie(seg);
        return new StreetPaint(centre, centre && Math.Max(1, (int)a.LanesBackward) + Math.Max(1, (int)a.LanesForward) >= 4, centre && !urban, dash, gap);
    }
}

/// <summary>Everything a transition paints: lines along strip boundaries, hatch stripes (quads of (station, offset), each with its hatch's strip), arrows and symbols.</summary>
public sealed class TransitionMarkings
{
    public List<TransitionLine> Lines { get; } = new();
    public List<(int Strip, (double S, double O)[] Quad)> Stripes { get; } = new();
    public List<TransitionMark> Marks { get; } = new();
}

/// <summary>
/// The paint of a transition, by rule (#818, the plan's step 4; the user's generalization: every marking is the boundary between
/// two strips, its style from the pair): two lanes the same way a lane line (arriving: dashed 3/3, solid the last
/// <see cref="SolidToMouth"/> m; a lane change into a pocket is one); arriving and departing lanes the centre line, solid beside a
/// pocket or a hatch and the last <see cref="SolidAtLights"/> m at the lights, else the street's; anything beside a hatch the
/// hatch's solid outline (so a lead-in hatch's diagonal boundary with its pocket is the angled closing line), its stripes leaning
/// with the traffic, none under <see cref="HatchMinWidth"/> x <see cref="HatchMinLength"/> m (the centre line alone); a bike lane
/// its yellow dashed line; the carriageway's edges the street's Randlinien, none beside a bike lane. Arrows two per arriving car
/// lane in the storage (tips <see cref="ArrowFirst"/> and <see cref="ArrowApart"/> m on, 8 m in a short one), a bike symbol at
/// each bike lane's mouth end. Pure: stations and offsets as the transition counts them.
/// </summary>
public static class TransitionPaint
{
    public const double SolidToMouth = 10, SolidAtLights = 20;
    public const double HatchMinWidth = 1.5, HatchMinLength = 20, HatchStep = 2.5, HatchStripe = 0.3;
    public const double ArrowFirst = 5, ArrowApart = 15, ArrowApartShort = 8;
    /// <summary>A lane line's 3/3 dashes in a pocket's storage and lead-in (#123).</summary>
    public const float LaneDash = 3f, LaneGap = 3f;

    private enum Style { None, Solid, Dashed, LaneChange, Bike, BikePocket, BikeSolid, BikeCross }

    /// <param name="nearStop">Where the arriving traffic stops at the mouth end (m out from the mouth, its stop line or bar): the solid
    /// stretches and the arrows count from there (the exterior, phase 4); <paramref name="farStop"/> the same at a shared transition's far end.</param>
    public static TransitionMarkings Of(Transition t, StreetPaint street, bool lights, bool farLights = false, double nearStop = 0, double farStop = 0)
    {
        var m = new TransitionMarkings();
        var st = t.Stations;
        int n = t.Strips.Count;
        // a hatch striped only where it is big enough; a smaller one leaves the centre line alone
        var striped = new bool[n];
        for (int k = 0; k < n; k++)
        {
            if (t.Strips[k].Strip.Kind != StripKind.Hatch) continue;
            var w = t.Strips[k].Widths;
            double from = double.MaxValue, to = double.MinValue;
            for (int i = 0; i < st.Length; i++)
                if (w[i] > 0.05 || (i > 0 && w[i - 1] > 0.05) || (i + 1 < st.Length && w[i + 1] > 0.05)) { from = Math.Min(from, st[i]); to = Math.Max(to, st[i]); }
            striped[k] = w.Max() >= HatchMinWidth && to - from >= HatchMinLength;
        }
        bool Present(int k, int i) => Math.Max(t.Strips[k].Widths[i], t.Strips[k].Widths[i + 1]) > 1e-3;
        // a bike lane opening from the kerb-side lane: at the transition's outer end no car lane lies on its right
        bool FromKerb(int k)
        {
            int at = t.Strips[k].Widths.Length - 1;
            for (int r = k + 1; r < n; r++)
                if (t.Strips[r].Strip.IsCarLane && t.Strips[r].Widths[at] > 1e-3) return false;
            return true;
        }
        var near = PocketRules.Left;
        bool CentreSidePocket(int id) => near.Owns(id) || near.Owns(id - TransitionPlanner.FarIdOffset) || id == MouthPlanner.LeftBikeId;

        // per interval between stations, each boundary between present carriageway strips, its style; runs of one style merged
        var open = new Dictionary<(int Boundary, double Offset), (Style Style, double From)>();
        void Close((int, double) key, double at)
        {
            if (!open.Remove(key, out var run) || at - run.From < 0.05 || run.Style == Style.None) return;
            m.Lines.Add(LineOf(key.Item1, key.Item2, run.From, at, run.Style, street));
        }
        var openBefore = new HashSet<int>();
        var openNow = new HashSet<int>();
        for (int i = 0; i + 1 < st.Length; i++)
        {
            double s0 = st[i], s1 = st[i + 1];
            var here = Enumerable.Range(0, n).Where(k => t.Strips[k].Strip.InCarriageway && Present(k, i)).ToList();
            bool pocketHere = here.Any(k => CentreSidePocket(t.Strips[k].Strip.Id)) || here.Any(k => t.Strips[k].Strip.Kind == StripKind.Hatch);
            var lines = new Dictionary<(int, double), Style>();
            var boxed = new HashSet<(int, double)>();
            var bare = new HashSet<(int, double)>();
            void Put(int boundary, double offset, Style s) { if (s != Style.None) lines[(boundary, offset)] = s; }
            for (int j = 0; j < here.Count; j++)
            {
                var a = t.Strips[here[j]].Strip;
                // the carriageway's left edge
                if (j == 0 && street.EdgeLines && a.Kind != StripKind.BikeLane) Put(here[j], PaintEmitter.EdgeLineInset, Style.Solid);
                if (j == here.Count - 1 && street.EdgeLines && a.Kind != StripKind.BikeLane) Put(here[j] + 1, -PaintEmitter.EdgeLineInset, Style.Solid);
                if (j == here.Count - 1) continue;
                var b = t.Strips[here[j + 1]].Strip;
                int boundary = here[j + 1];
                var pair = PairStyle(a, b, striped[here[j]], striped[here[j + 1]], boundary == t.Anchor || (here[j] < t.Anchor && here[j + 1] >= t.Anchor), pocketHere, street);
                // an arriving bike lane's line against a car lane goes solid before its stop, as a car pocket's (the user, Oct 17:
                // the kerb-side lane too, not only one between two car lanes)
                if (pair == Style.Bike && (ArrivingBike(a) && b.IsCarLane || ArrivingBike(b) && a.IsCarLane)) pair = Style.BikePocket;
                Put(boundary, 0, pair);
                // a street without a centre line still gets it solid before the stop line at the lights (the user's rule, #711)
                if (pair == Style.None && a.IsCarLane && b.IsCarLane && a.Way != b.Way) bare.Add((boundary, 0));
                // a bike lane stopping at a box: its lines end at the box's back, the cars' line (the box draws its own sides)
                if (t.Strips[here[j]].Strip.Stop == BikeStop.Box || t.Strips[here[j + 1]].Strip.Stop == BikeStop.Box) boxed.Add((boundary, 0));
            }
            static bool ArrivingBike(Strip x) => x is { Kind: StripKind.BikeLane, Way: StripWay.In };
            // an in-between bike lane (arriving, a car lane either side: the through riders' lane beside a right pocket, the user,
            // Oct 17): where the pocket on its right opens the cars cross it, crossing dashes and red; nearer the mouth its lines
            // solid, its right one dashed where it does not run on from the kerb-side lane (riders cross over to it from the right)
            var between = new Dictionary<(int, double), Style>();
            openBefore = openNow;
            openNow = new HashSet<int>();
            for (int p = 1; p < here.Count; p++)
            {
                var x = t.Strips[here[p]].Strip;
                // (on its right the pocket, or before it opens the kerb-side bike lane opening first; or nothing yet at the
                // transition's start, carried on from the opening)
                Strip? right = p + 1 < here.Count ? t.Strips[here[p + 1]].Strip : null;
                if (!ArrivingBike(x) || !t.Strips[here[p - 1]].Strip.IsCarLane) continue;
                if (right is { } r0 ? !(r0.IsCarLane || ArrivingBike(r0)) : !openBefore.Contains(x.Id)) continue;
                // opening, red from the transition's start (the user, Oct 17), while any lane on its right still widens: the kerb-side
                // bike lane, then the car pocket
                // (the lane next to it only: a pocket opening further right is no one crossing this lane)
                bool opening = right is not null && Math.Abs(t.Strips[here[p + 1]].Widths[i] - t.Strips[here[p + 1]].Widths[i + 1]) > 0.05
                    // (and on out over the rest of the transition's start while anything still changes there, the lane beside it easing)
                    || (openBefore.Contains(x.Id) && here.Any(r => Math.Abs(t.Strips[r].Widths[i] - t.Strips[r].Widths[i + 1]) > 0.05));
                if (opening) openNow.Add(x.Id);
                if (!opening && right is not { IsCarLane: true }) continue;
                // crossing dashes only over the conflict, where a car lane on its right opens (the cars cross it there); from the
                // transition's start to the conflict its lines continuous (the user, Oct 17)
                bool conflict = right is { IsCarLane: true } && Math.Abs(t.Strips[here[p + 1]].Widths[i] - t.Strips[here[p + 1]].Widths[i + 1]) > 0.05;
                var lineStyle = conflict ? Style.BikeCross : Style.BikeSolid;
                between[(here[p], 0)] = lineStyle;
                // no line on its right while the right-turn bike lane opens beside it: it leads straight into it (the user, Oct 17)
                if (right is { } r1) between[(here[p + 1], 0)] = ArrivingBike(r1) ? Style.None : opening ? lineStyle : FromKerb(here[p]) ? Style.BikeSolid : Style.BikePocket;
                if (opening)
                {
                    // red over it in pieces about a metre long, each as wide as the lane there (it may open from nothing)
                    var wl = t.Strips[here[p]].Widths;
                    int pieces = Math.Max(1, (int)Math.Ceiling(s1 - s0));
                    for (int q = 0; q < pieces; q++)
                    {
                        double f0 = (double)q / pieces, f1 = (double)(q + 1) / pieces;
                        float w = (float)Math.Min(wl[i] + (wl[i + 1] - wl[i]) * f0, wl[i] + (wl[i + 1] - wl[i]) * f1);
                        float band = w - 2 * BikePlanner.LineWidth - 0.1f;
                        if (band > 0.3f) m.Lines.Add(new TransitionLine(here[p], w * 0.5, s0 + (s1 - s0) * f0, s0 + (s1 - s0) * f1, PaintType.BikeCrossing, PaintEmitter.Red, band, 0, 0));
                    }
                }
            }
            // runs, split where a solid stretch by a mouth starts or ends inside the interval: lanes the last 10 m, the centre
            // line 20 m at the lights
            double[] cuts = [s0, .. new[] { nearStop, nearStop + SolidToMouth, nearStop + SolidAtLights, t.Length - farStop - SolidToMouth, t.Length - farStop - SolidAtLights }.Where(x => x > s0 + 1e-6 && x < s1 - 1e-6).Order(), s1];
            for (int c = 0; c + 1 < cuts.Length; c++)
            {
                double a0 = cuts[c], a1 = cuts[c + 1], mid = (a0 + a1) * 0.5;
                var now = new Dictionary<(int, double), Style>();
                foreach (var key in bare)
                    if (key.Item1 == CentreBoundary(t, i) && ((lights && mid < nearStop + SolidAtLights) || (farLights && t.Far is not null && mid > t.Length - farStop - SolidAtLights)))
                        now[key] = Style.Solid;
                foreach (var (key, given) in lines)
                {
                    var style = between.TryGetValue(key, out var b0) && b0 == Style.BikePocket ? Style.BikePocket : given;
                    var s = style;
                    if (style == Style.LaneChange && (mid < nearStop + SolidToMouth || (t.Far is not null && mid > t.Length - farStop - SolidToMouth))) s = Style.Solid;
                    if (style == Style.BikePocket) s = mid < nearStop + SolidToMouth || (t.Far is not null && mid > t.Length - farStop - SolidToMouth) ? Style.BikeSolid : Style.Bike;
                    if (between.TryGetValue(key, out var b) && b != Style.BikePocket) s = b;
                    if (boxed.Contains(key) && mid < nearStop) s = Style.None;
                    if (key.Item1 == CentreBoundary(t, i) && ((lights && mid < nearStop + SolidAtLights) || (farLights && t.Far is not null && mid > t.Length - farStop - SolidAtLights)) && s == Style.Dashed) s = Style.Solid;
                    now[key] = s;
                }
                foreach (var key in open.Keys.ToList())
                    if (!now.TryGetValue(key, out var s) || s != open[key].Style) Close(key, a0);
                foreach (var (key, s) in now)
                    if (!open.ContainsKey(key)) open[key] = (s, a0);
            }
        }
        foreach (var key in open.Keys.ToList()) Close(key, t.Length);

        for (int k = 0; k < n; k++) if (striped[k]) Stripes(t, k, m.Stripes);
        Marks(t, lights, m.Marks, nearStop);
        return m;
    }

    /// <summary>The boundary the centre line runs on in interval <paramref name="i"/>: left of the first present strip right of the anchor.</summary>
    private static int CentreBoundary(Transition t, int i)
    {
        for (int k = t.Anchor; k < t.Strips.Count; k++)
            if (Math.Max(t.Strips[k].Widths[i], t.Strips[k].Widths[i + 1]) > 1e-3) return k;
        return t.Strips.Count;
    }

    /// <summary>A pocket's lane, this end's or the far end's.</summary>
    private static bool Pocket(int id) => PocketRules.Of(id) is not null || PocketRules.Of(id - TransitionPlanner.FarIdOffset) is not null;

    private static Style PairStyle(Strip a, Strip b, bool aStriped, bool bStriped, bool acrossCentre, bool pocketHere, StreetPaint street)
    {
        bool aHatch = a.Kind == StripKind.Hatch, bHatch = b.Kind == StripKind.Hatch;
        // a hatch's outline; one too small to stripe is no hatch: its centre-line side alone, solid
        if (aHatch || bHatch)
        {
            if ((aHatch && aStriped) || (bHatch && bStriped)) return Style.Solid;
            return acrossCentre ? Style.Solid : Style.None;
        }
        if (a.Kind == StripKind.BikeLane || b.Kind == StripKind.BikeLane) return Style.Bike;
        if (!a.IsCarLane || !b.IsCarLane) return Style.Solid;
        if (a.Way == b.Way) return a.Way == StripWay.In || Pocket(a.Id) || Pocket(b.Id) ? Style.LaneChange : Style.Dashed;
        // the centre line
        if (pocketHere || street.SolidCentre) return Style.Solid;
        return street.CentreLine ? Style.Dashed : Style.None;
    }

    private static TransitionLine LineOf(int boundary, double offset, double from, double to, Style s, StreetPaint street) => s switch
    {
        Style.Solid => new(boundary, offset, from, to, PaintType.WhiteSolid, PaintEmitter.White, PaintEmitter.LineWidth, 0, 0),
        Style.Dashed => new(boundary, offset, from, to, PaintType.WhiteDashed, PaintEmitter.White, PaintEmitter.LineWidth, street.Dash, street.Gap),
        Style.LaneChange => new(boundary, offset, from, to, PaintType.WhiteDashed, PaintEmitter.White, PaintEmitter.LineWidth, LaneDash, LaneGap),
        Style.BikeSolid => new(boundary, offset, from, to, PaintType.YellowSolid, PaintEmitter.Yellow, BikePlanner.LineWidth, 0, 0),
        Style.BikeCross => new(boundary, offset, from, to, PaintType.YellowDashed, PaintEmitter.Yellow, BikePlanner.LineWidth, BikePlanner.JunctionDash, BikePlanner.JunctionDash),
        _ => new(boundary, offset, from, to, PaintType.YellowDashed, PaintEmitter.Yellow, BikePlanner.LineWidth, BikePlanner.Dash, BikePlanner.Gap),
    };

    /// <summary>
    /// A hatch's stripes at 45 degrees every <see cref="HatchStep"/> m, leaning the way the hatch pushes a car (#406): going with
    /// the traffic beside its moving edge, a stripe runs out toward that traffic's lane. A lead-in's (its edge along the arriving
    /// lanes moving) lean toward the mouth at that edge, an exit hatch's (its edge along the departing lane moving) away from it,
    /// though the two are one strip; each a quad clipped to the hatch.
    /// </summary>
    private static void Stripes(Transition t, int k, List<(int Strip, (double S, double O)[] Quad)> into)
    {
        double from = t.Stations[0], to = t.Length;
        double h = HatchStripe * Math.Sqrt(0.5);
        double fallback = k < t.Anchor ? 1 : -1;
        // the traffic beside an edge: the nearest car lane on that side, arriving (toward the mouth, s falling) or not
        bool? Arriving(int step, double s)
        {
            for (int r = k + step; r >= 0 && r < t.Strips.Count; r += step)
                if (t.Strips[r].Strip.IsCarLane && t.WidthAt(r, s) > 0.05) return t.Strips[r].Strip.Way == StripWay.In;
            return null;
        }
        // s = c + sigma * u from the left edge: the left end at c, the right one sigma * width further from the mouth
        double SigmaAt(double c)
        {
            double a = Math.Max(from, c - 1), b = Math.Min(to, c + 1);
            if (b - a < 1e-3) return fallback;
            var (l0, r0) = t.SpanAt(k, a);
            var (l1, r1) = t.SpanAt(k, b);
            double left = Math.Abs(l1 - l0), right = Math.Abs(r1 - r0);
            if (Math.Max(left, right) < 1e-3) return fallback;
            // the end at the moving edge further along that traffic's way: an arriving lane's toward the mouth
            bool leftMoves = left > right;
            if (Arriving(leftMoves ? -1 : 1, c) is not { } arriving) return fallback;
            return leftMoves == arriving ? 1 : -1;
        }
        double sigma = fallback;
        // a stripe's line: s = c + sigma * (o - left(s)) is solved by stepping across: o runs from the hatch's left edge outward
        (double S, double O)? Point(double c, double across, bool far)
        {
            // where the line s = c + sigma * u (u metres from the hatch's left edge) leaves the hatch: u = width(s)
            double lo = 0, hi = 30;
            double W(double u) { double s = c + sigma * u; return s < from || s > to ? -1 : t.WidthAt(k, s) - u; }
            if (W(0) <= 0.02) return null;
            if (!far) return (c, t.SpanAt(k, c).From);
            for (int it = 0; it < 40; it++) { double mid = (lo + hi) * 0.5; if (W(mid) > 0) lo = mid; else hi = mid; }
            double sEnd = c + sigma * lo;
            return (sEnd, t.SpanAt(k, sEnd).From + lo);
        }
        for (double c = from + 0.6; c < to; c += HatchStep)
        {
            sigma = SigmaAt(c);
            if (Point(c - h, 0, false) is not { } a0 || Point(c - h, 0, true) is not { } a1) continue;
            if (Point(c + h, 0, false) is not { } b0 || Point(c + h, 0, true) is not { } b1) continue;
            if (Math.Abs(a1.O - a0.O) < 0.3 && Math.Abs(b1.O - b0.O) < 0.3) continue;
            into.Add((k, [a0, a1, b1, b0]));
        }
    }

    /// <summary>
    /// Arrows in every arriving car lane with moves in the storage, at the lights or where a pocket opens (two, the second
    /// <see cref="ArrowApartShort"/> m on in a storage under 20 m); a bike symbol at each bike lane's mouth end (none in a
    /// pocket riders share). A far end's arriving lanes (departing here) likewise, pointing at its mouth.
    /// </summary>
    private static void Marks(Transition t, bool lights, List<TransitionMark> into, double stop)
    {
        double storage = t.Arm.Openings.Count > 0 ? t.Arm.Openings.Max(o => o.Storage) : 0;
        bool arrows = lights || t.Arm.Openings.Count > 0;
        double apart = storage > 0 && storage < 20 + 1e-6 ? ArrowApartShort : ArrowApart;
        for (int k = 0; k < t.Strips.Count; k++)
        {
            var x = t.Strips[k].Strip;
            if (t.WidthAt(k, 0) < 1e-3 && (t.Far is null || t.WidthAt(k, t.Length) < 1e-3)) continue;
            if (x.Kind == StripKind.BikeLane && t.WidthAt(k, BikePlanner.SymbolFromEnd) > 0.9 && t.Length > BikePlanner.SymbolFromEnd + BikePlanner.SymbolSize)
                into.Add(new TransitionMark(k, BikePlanner.SymbolFromEnd + BikePlanner.SymbolSize, PaintArrow.None, true, x.Way == StripWay.Out));
            if (!arrows || !x.IsCarLane || x.Way != StripWay.In || x.Moves == SignalMoves.None) continue;
            var arrow = ArrowOf(x.Moves);
            if (!PaintEmitter.HasArrow(arrow)) continue;
            foreach (double tip in (ReadOnlySpan<double>)[stop + ArrowFirst, stop + ArrowFirst + apart])
            {
                double tail = tip + PaintEmitter.ArrowLength;
                if (tail <= t.Length && t.WidthAt(k, tail) > 2.4 && t.WidthAt(k, tip) > 2.4) into.Add(new TransitionMark(k, tail, arrow, false, false));
            }
        }
    }

    private static PaintArrow ArrowOf(SignalMoves m) =>
        (m.HasFlag(SignalMoves.Left) ? PaintArrow.Left : 0) | (m.HasFlag(SignalMoves.Through) ? PaintArrow.Straight : 0) | (m.HasFlag(SignalMoves.Right) ? PaintArrow.Right : 0);
}
