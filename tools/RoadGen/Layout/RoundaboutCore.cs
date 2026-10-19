namespace UnitSport.Tools.RoadGen.Layout;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;
using UnitSport.Tools.RoadGen.Network;

/// <summary>
/// A tight roundabout's core (#854, <c>docs/plans/roundabout-core.md</c>): the whole roundabout one junction, its ring the
/// circle <c>RoundaboutShaper</c> fitted (#122), its carriageway at least <see cref="LeastRing"/> wide. An arm that TLM draws as
/// an entry and an exit meeting at a splitter island's tip starts there: from its mouth by the tip the entry and the exit flare
/// apart round a raised splitter island down to the ring. Between two arms the kerb runs from one mouth's corner on a fillet
/// into the ring's outer circle, along it, and out on a fillet to the next mouth. Every arriving lane is joined to every other
/// arm's departing lane: in to the ring lane (its centreline), counter-clockwise round it, out to the exit; each entry gives
/// way at the ring's edge. Produces a <see cref="JunctionCore"/> as <see cref="CorePlanner"/> does, so transitions, the
/// exterior, the writer and the traffic paths take it as any core.
/// </summary>
public static class RoundaboutCore
{
    /// <summary>A ring this small outside (its centreline radius plus half its carriageway, m) is laid whole; a larger one as T's.</summary>
    public const double TightOuter = 20;
    /// <summary>A splitter island's tip (where TLM's entry and exit lines meet) this far out past the ring's outer edge at most (m).</summary>
    public const double TipReach = 30;
    /// <summary>The ring's carriageway at least this wide (m): TLM's ring line is one 3.3 m lane, a Swiss compact roundabout's ring 6-8 m.</summary>
    public const double LeastRing = 6;
    /// <summary>The entry and exit kerbs' fillet into the ring: this share of the ring's outer radius plus a constant, within limits (m).</summary>
    public const double FilletShare = 0.4, FilletPlus = 2, FilletLeast = 2, FilletMost = 12;
    /// <summary>A connector's run along its arm down to this far out from the ring's outer edge before it curves in (m).</summary>
    public const double RunOut = 0.5;
    /// <summary>The mouth stands this far out past where its kerbs leave their fillets, or past a splitter island's tip (m).</summary>
    public const double MouthPast = 0.5;
    /// <summary>A splitter island: half as wide at the ring as this (m), from 1 m in from the mouth to 1 m out from the ring; the lanes
    /// keep <see cref="IslandMargin"/> off it; none shorter than <see cref="IslandLeast"/>.</summary>
    public const double IslandHalf = 1.25, IslandMargin = 0.3, IslandLeast = 4;
    /// <summary>A splitter island laid where TLM draws no tip (an arm of one line): this long out from the ring (m).</summary>
    public const double IslandLength = 8;
    /// <summary>The give-way row this far out from the ring's outer edge (m).</summary>
    public const double TeethOut = 0.3;
    /// <summary>An arm turned further than this from the ring's radius runs past the ring (a bypass, a slip): not laid whole.</summary>
    public static readonly double PastRing = 50 * Math.PI / 180;
    /// <summary>An entry and an exit line closer than this round the ring are one road's, apart round a splitter island (TLM, no tip).</summary>
    public static readonly double SplitPair = 30 * Math.PI / 180;

    /// <summary>The ring's carriageway as laid: TLM's, at least <see cref="LeastRing"/>.</summary>
    public static double RingWidth(double width) => Math.Max(width, LeastRing);

    /// <summary>Whether a ring of centreline radius <paramref name="radius"/> and carriageway <paramref name="width"/> is laid whole.</summary>
    public static bool Tight(double radius, double width) => radius + width * 0.5 <= TightOuter;

    /// <summary>One arm as the builder sees it: its mouth station, its splitter island's span (none: both stations NaN).</summary>
    private sealed class Arm
    {
        public required ArmPlan Plan;
        public double Mouth;
        public double Apex = double.NaN, Base = double.NaN;
        public double Centre;
        /// <summary>How far the arriving side moves right and the departing side left of the arm's own offsets at station s.</summary>
        public double Flare(double s) => double.IsNaN(Apex) || s >= Apex ? 0 : s <= Base ? IslandHalf : IslandHalf * (Apex - s) / (Apex - Base);
    }

    public static JunctionCore Plan(JunctionPlan jp, RoundaboutShaper.Ring shaped)
    {
        var core = new JunctionCore();
        var o = shaped.Centre;
        double radius = shaped.Radius, width = RingWidth(shaped.Width), outer = radius + width * 0.5;
        bool mini = radius - width * 0.5 < RoundaboutShaper.MiniIsland;
        var arms = jp.Arms.Where(a => a.Axis is not null).OrderBy(a => Angles.Normalize((a.Axis!.PointAt(0) - o).Heading))
            .Select(a => new Arm { Plan = a, Centre = a.Mouth.CentreLine ?? 0 }).ToList();
        int n = arms.Count;
        if (n < 2) return core;
        foreach (var arm in arms)
        {
            var start = arm.Plan.Axis!.PointAt(0);
            if (Math.Abs(Angles.Delta((start - o).Heading, arm.Plan.Axis.DirAt(0).Heading)) > PastRing)
            {
                if (Environment.GetEnvironmentVariable("RINGDBG") == "1")
                    Console.WriteLine(System.FormattableString.Invariant($"[ring] {o.X:F0},{o.Y:F0} arm {arm.Plan.Arm} (link {arm.Plan.LinkId}) starts {start.DistanceTo(o):F1} m from the centre heading {arm.Plan.Axis.DirAt(0).Heading * 180 / Math.PI:F0} deg, the radius there {(start - o).Heading * 180 / Math.PI:F0} deg"));
                core.Refused = "an arm runs past the ring";
                return core;
            }
        }

        // a point of an arm at station s (negative: on past its first point toward the ring, straight) and offset (right of a driver
        // arriving)
        static Vec2 At(ArmPlan a, double s, double offset) =>
            s >= 0 ? a.Axis!.At(s, offset) : a.Axis!.At(0, offset) - a.Axis.DirAt(0) * -s;
        // the station where a line of the arm at this offset reaches distance d from the ring's centre, coming in from s = 30
        double StationAt(ArmPlan a, double offset, double d)
        {
            double lo = -2 * outer - TipReach, hi = 30;
            if (At(a, hi, offset).DistanceTo(o) < d) return hi;
            for (int it = 0; it < 50; it++)
            {
                double mid = (lo + hi) * 0.5;
                if (At(a, mid, offset).DistanceTo(o) >= d) hi = mid; else lo = mid;
            }
            return hi;
        }

        // the mouths, and the splitter islands where an arm starts at a tip off the ring with both an arriving and a departing lane
        foreach (var arm in arms)
        {
            var a = arm.Plan;
            double ringAt = StationAt(a, arm.Centre, outer + 1);
            bool atTip = a.Axis!.PointAt(0).DistanceTo(o) > outer + 2;
            arm.Mouth = atTip ? MouthPast : 0;
            bool twoWay = a.Mouth.CarLanes(StripWay.In).Any() && a.Mouth.CarLanes(StripWay.Out).Any();
            if (atTip && twoWay && !mini && arm.Mouth - 1 - ringAt >= IslandLeast) (arm.Apex, arm.Base) = (arm.Mouth - 1, ringAt);
            // (an arm TLM draws as one line still gets its splitter island on a compact ring: its mouth out past it, #854)
            // (shorter, down to IslandLeast, where a road joins the arm or the next junction stands nearer: its mouth within the
            // arm's reach; none shorter)
            else if (!atTip && twoWay && !mini && Math.Min(IslandLength, a.Reach - 1 - ringAt) is var length && length >= IslandLeast)
                (arm.Base, arm.Apex, arm.Mouth) = (ringAt, ringAt + length, ringAt + length + 1);
        }

        // the kerbs: each arm's right kerb and the next arm's left kerb, as straight lines down from the mouth (flared past a splitter
        // island), filleted into the ring's outer circle on the side away from the arm
        (Vec2 T, Vec2 C, List<Vec2> Arc)? Fillet(Arm arm, bool right, double rf)
        {
            var a = arm.Plan;
            double kerb = right ? a.Mouth.CarriagewayRight : a.Mouth.CarriagewayLeft, side = right ? 1 : -1;
            double Kerb(double s) => kerb + side * arm.Flare(s);
            // the kerb follows the arm (curved, flared) from the mouth down to where it leaves for the fillet, straight from there
            double low = Math.Min(arm.Mouth - 1, double.IsNaN(arm.Base) ? StationAt(a, kerb, outer) : arm.Base);
            Vec2 bottom = default, u = default, away = default;
            double t = 0;
            // (twice: where the fillet first touches, then the kerb leaving the arm there, on a curved arm along its direction there)
            for (int pass = 0; pass < 2; pass++)
            {
                bottom = At(a, low, Kerb(low));
                // (its direction just in from there: past a splitter island's base the flare holds, the kerb runs along the arm)
                u = bottom - At(a, low - 1, Kerb(low - 1));
                u /= Math.Max(1e-9, u.Length);
                // away from the arm: an arm's offsets grow to the right of a driver arriving, the outward direction's left (Perp)
                away = u.Perp * side;
                var b = bottom + away * rf - o;
                double bu = b.Dot(u), disc = bu * bu - (b.LengthSquared - (outer + rf) * (outer + rf));
                if (disc < 0) return null;
                // (coming in from the mouth, the first place the fillet touches the ring)
                t = -bu + Math.Sqrt(disc);
                if (t <= 0 || pass == 1) break;
                low = Math.Min(arm.Mouth - 0.5, low + t);
            }
            t = Math.Min(0, t);
            var f = bottom + u * t + away * rf;
            var tp = bottom + u * t;
            var c = o + (f - o) * (outer / (outer + rf));
            var run = new List<Vec2>();
            for (double s = arm.Mouth; s > low; s -= 1) run.Add(At(a, s, Kerb(s)));
            Line(run, bottom, tp);
            double a0 = (tp - f).Heading, da = Angles.Delta(a0, (c - f).Heading);
            int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(da) * rf / 0.75));
            for (int k = 1; k <= steps; k++) run.Add(f + Vec2.FromHeading(a0 + da * k / steps) * rf);
            return (tp, c, run);
        }
        double Heading(Arm arm) => (arm.Plan.Axis!.PointAt(0) - o).Heading;
        bool OneWay(Arm arm, StripWay way) => arm.Plan.Mouth.CarLanes(way).Any() && !arm.Plan.Mouth.CarLanes(way == StripWay.In ? StripWay.Out : StripWay.In).Any();
        bool Split(Arm a, Arm b) => Ccw(Heading(a), Heading(b)) < SplitPair
            && (OneWay(a, StripWay.In) && OneWay(b, StripWay.Out) || OneWay(a, StripWay.Out) && OneWay(b, StripWay.In));
        double rf0 = Math.Clamp(FilletShare * outer + FilletPlus, FilletLeast, FilletMost);
        var rightOf = new (Vec2 T, Vec2 C, List<Vec2> Arc)?[n];
        var leftOf = new (Vec2 T, Vec2 C, List<Vec2> Arc)?[n];
        var apartAt = new bool[n];
        // an arm starting on the ring: its mouth out past where its kerbs leave their fillets (laid first from the ring's edge)
        foreach (var arm in arms.Where(x => x.Mouth == 0 && double.IsNaN(x.Apex)))
        {
            // (measured from a mouth well out, then set where the kerbs leave their fillets)
            arm.Mouth = outer - radius + rf0 + 5;
            double s = outer - radius + MouthPast;
            foreach (bool right in (ReadOnlySpan<bool>)[true, false])
                if (Fillet(arm, right, rf0) is { } fl) s = Math.Max(s, (fl.T - arm.Plan.Axis!.PointAt(0)).Dot(arm.Plan.Axis.DirAt(0)) + MouthPast);
            arm.Mouth = s;
        }
        for (int k = 0; k < n; k++)
        {
            int j = (k + 1) % n;
            for (double rf = rf0; ; rf *= 0.75)
            {
                var r = Fillet(arms[k], true, rf);
                var l = Fillet(arms[j], false, rf);
                bool apart = r is { } rr && l is { } ll && Ccw(Heading(arms[k]), (rr.C - o).Heading) <= Ccw(Heading(arms[k]), (ll.C - o).Heading);
                if (apart || rf < FilletLeast) { (rightOf[k], leftOf[j], apartAt[k]) = (r, l, apart); break; }
            }
        }

        // the outline: each mouth across its carriageway, then the kerb to the next one
        for (int k = 0; k < n; k++)
        {
            int j = (k + 1) % n;
            var a = arms[k].Plan;
            var left = At(a, arms[k].Mouth, a.Mouth.CarriagewayLeft);
            var right = At(a, arms[k].Mouth, a.Mouth.CarriagewayRight);
            core.Mouths.Add(new CoreMouth(a.Arm, arms[k].Mouth, left, right));
            core.Outline.Add(left);
            core.Outline.Add(right);
            var corner = new CoreCorner { ArmA = a.Arm, ArmB = arms[j].Plan.Arm, Shape = CornerShape.Arc, Radius = rf0 };
            if (Split(arms[k], arms[j]))
            {
                // one road's entry and exit drawn apart: the kerb down each to the ring and across, grass between them
                var b = arms[j].Plan;
                double sa = StationAt(a, a.Mouth.CarriagewayRight, outer + 1), sb = StationAt(b, b.Mouth.CarriagewayLeft, outer + 1);
                corner.Kerb.Add(At(a, Math.Min(sa, arms[k].Mouth - 0.5), a.Mouth.CarriagewayRight));
                corner.Kerb.Add(At(b, Math.Min(sb, arms[j].Mouth - 0.5), b.Mouth.CarriagewayLeft));
                corner.Shape = CornerShape.Gore;
            }
            else if (!apartAt[k])
            {
                // two arms so close their fillets overlap: their kerbs meet at a point out from the ring, or run straight across
                var b = arms[j].Plan;
                Vec2 pa = At(a, arms[k].Mouth, a.Mouth.CarriagewayRight), pb = At(b, arms[j].Mouth, b.Mouth.CarriagewayLeft);
                Vec2 da = -a.Axis.DirAt(Math.Max(0, arms[k].Mouth)), db = -b.Axis!.DirAt(Math.Max(0, arms[j].Mouth));
                double cross = da.Cross(db);
                if (Math.Abs(cross) > 1e-6)
                {
                    double t = (pb - pa).Cross(db) / cross;
                    var x = pa + da * t;
                    if (t > 0 && x.DistanceTo(o) >= outer) corner.Kerb.Add(x);
                }
                corner.Shape = corner.Kerb.Count > 0 ? CornerShape.Vertex : CornerShape.Chord;
            }
            else if (rightOf[k] is { } rc && leftOf[j] is { } lc && Crossing(rc.Arc, lc.Arc) is { } cross)
            {
                // two arms so close their kerbs cross before the ring: the corner is where they meet
                corner.Kerb.AddRange(rc.Arc.Take(cross.I + 1));
                corner.Kerb.Add(cross.At);
                for (int q = cross.J; q >= 0; q--) corner.Kerb.Add(lc.Arc[q]);
                corner.Shape = CornerShape.Vertex;
            }
            else if (rightOf[k] is { } r && leftOf[j] is { } l)
            {
                corner.Kerb.AddRange(r.Arc);
                double h0 = (r.C - o).Heading, sweep = Ccw(h0, (l.C - o).Heading);
                int steps = Math.Max(1, (int)Math.Ceiling(sweep * outer / 1.0));
                for (int q = 1; q < steps; q++) corner.Kerb.Add(o + Vec2.FromHeading(h0 + sweep * q / steps) * outer);
                corner.Kerb.AddRange(Enumerable.Reverse(l.Arc));
            }
            else corner.Shape = CornerShape.Chord;
            core.Corners.Add(corner);
            core.Outline.AddRange(corner.Kerb);
        }
        Dedupe(core.Outline);
        core.Area = Math.Abs(CorePlanner.SignedArea(core.Outline));
        core.Simple = CorePlanner.Simple(core.Outline);
        core.Triangles.AddRange(EarClip.Triangulate(core.Outline));

        // the splitter islands, between the flared arriving and departing lanes
        foreach (var arm in arms.Where(x => !double.IsNaN(x.Apex)))
        {
            var island = new List<Vec2>();
            int steps = Math.Max(2, (int)Math.Ceiling((arm.Apex - arm.Base) / 1.0));
            for (int q = 0; q <= steps; q++)
            {
                double s = arm.Apex - (arm.Apex - arm.Base) * q / steps, h = Math.Max(0, arm.Flare(s) - IslandMargin);
                island.Add(At(arm.Plan, s, arm.Centre + h));
            }
            for (int q = steps; q >= 0; q--)
            {
                double s = arm.Apex - (arm.Apex - arm.Base) * q / steps, h = Math.Max(0, arm.Flare(s) - IslandMargin);
                island.Add(At(arm.Plan, s, arm.Centre - h));
            }
            Dedupe(island);
            if (island.Count >= 3) core.Islands.Add(CorePlanner.SignedArea(island) < 0 ? Enumerable.Reverse(island).ToList() : island);
        }

        // the give-way rows across each entry at the ring's edge
        foreach (var arm in arms)
        {
            var ins = arm.Plan.Mouth.CarLanes(StripWay.In).ToList();
            if (ins.Count == 0) continue;
            double from = ins.Min(l => arm.Plan.Mouth.Span(l.Index).From), to = ins.Max(l => arm.Plan.Mouth.Span(l.Index).To);
            double s = StationAt(arm.Plan, (from + to) * 0.5 + IslandHalf, outer + TeethOut);
            double f = arm.Flare(s);
            core.Teeth.Add((At(arm.Plan, s, from + f + 0.15), At(arm.Plan, s, to + f - 0.15)));
        }

        // the connectors: every arriving lane to every other arm's departing lane, counter-clockwise round the ring lane
        for (int k = 0; k < n; k++)
        {
            var a = arms[k];
            foreach (var (index, lane) in a.Plan.Mouth.CarLanes(StripWay.In))
                for (int step = 1; step < n; step++)
                {
                    var b = arms[(k + step) % n];
                    var outs = b.Plan.Mouth.CarLanes(StripWay.Out).ToList();
                    if (outs.Count == 0) continue;
                    var to = outs[0];
                    double sweep = Ccw(Heading(a), Heading(b));
                    var move = sweep < 0.75 * Math.PI ? SignalMoves.Right : sweep < 1.25 * Math.PI ? SignalMoves.Through : SignalMoves.Left;
                    var c = new Connector
                    {
                        FromArm = a.Plan.Arm, FromId = lane.Id, ToArm = b.Plan.Arm, ToId = to.Strip.Id, Move = move, Kind = ConnectorKind.Car,
                        Width = lane.Width, ToWidth = to.Strip.Width, FromOffset = Mid(a.Plan.Mouth.Span(index)), ToOffset = Mid(b.Plan.Mouth.Span(to.Index)),
                        Turns = true, Radius = radius,
                    };
                    c.Centre = Path(a, c.FromOffset, b, c.ToOffset);
                    // its lane's width on the entry, the exit lane's on the exit, easing from one to the other round the ring
                    var along = new double[c.Centre.Count];
                    for (int q = 1; q < along.Length; q++) along[q] = along[q - 1] + c.Centre[q].DistanceTo(c.Centre[q - 1]);
                    int on = 0, off = c.Centre.Count - 1;
                    while (on < off && c.Centre[on].DistanceTo(o) > outer) on++;
                    while (off > on && c.Centre[off].DistanceTo(o) > outer) off--;
                    double Half(int q) => (q <= on ? c.Width : q >= off ? c.ToWidth
                        : c.Width + (c.ToWidth - c.Width) * (along[q] - along[on]) / Math.Max(1e-9, along[off] - along[on])) * 0.5;
                    c.Left = CorePlanner.Offset(c.Centre, q => -Half(q));
                    c.Right = CorePlanner.Offset(c.Centre, q => Half(q));
                    c.Outside = c.Left.Concat(c.Right).Select(p => CorePlanner.Outside(core.Outline, p)).DefaultIfEmpty(0).Max();
                    if (c.Outside > CorePlanner.OutsideTolerance) core.ConnectorsOutside++;
                    // (RINGDBG=1: where a connector leaves the outline, for a look at a ring that fails its checks)
                    if (c.Outside > CorePlanner.OutsideTolerance && Environment.GetEnvironmentVariable("RINGDBG") == "1")
                    {
                        var all = c.Left.Concat(c.Right).ToList();
                        int wi = Enumerable.Range(0, all.Count).MaxBy(i => CorePlanner.Outside(core.Outline, all[i]));
                        Console.WriteLine(System.FormattableString.Invariant($"[ring] {o.X:F0},{o.Y:F0} connector {c.FromArm}->{c.ToArm} leaves the outline by {c.Outside:F2} m, {all[wi].DistanceTo(o):F1} m from the centre (point {wi % c.Left.Count} of {c.Left.Count}, {(wi < c.Left.Count ? "left" : "right")} edge); mouths {a.Mouth:F1}/{b.Mouth:F1}, outline simple {core.Simple}, corners {string.Join(",", core.Corners.Select(x => x.Shape))}; across the exit at {b.Plan.Axis!.StationPast(all[wi]):F1} m: {(all[wi] - b.Plan.Axis.PointAt(b.Plan.Axis.StationPast(all[wi]))).Dot(b.Plan.Axis.RightAt(b.Plan.Axis.StationPast(all[wi]))):F2} (carriageway {b.Plan.Mouth.CarriagewayLeft:F2}..{b.Plan.Mouth.CarriagewayRight:F2}, lane at {c.ToOffset:F2} w {c.ToWidth:F2})"));
                    }
                    core.Connectors.Add(c);
                }
        }
        // the kerbs pushed out where a lane passes beyond them by a few decimetres (as CorePlanner's), the checks again
        if (core.ConnectorsOutside > 0 && CorePlanner.Pave(core, core.Mouths.Count))
        {
            core.Area = Math.Abs(CorePlanner.SignedArea(core.Outline));
            core.Simple = CorePlanner.Simple(core.Outline);
            core.Triangles.Clear();
            core.Triangles.AddRange(EarClip.Triangulate(core.Outline));
            foreach (var c in core.Connectors) c.Outside = c.Left.Concat(c.Right).Select(p => CorePlanner.Outside(core.Outline, p)).DefaultIfEmpty(0).Max();
            core.ConnectorsOutside = core.Connectors.Count(c => c.Outside > CorePlanner.OutsideTolerance);
        }
        return core;

        // a lane's path: in along its arm (flared round the splitter island), onto the ring lane on a circular fillet tangent to
        // both (a right turn), counter-clockwise round it, off on a fillet onto the exit lane and out along it (flared) to its mouth
        List<Vec2> Path(Arm a, double offA, Arm b, double offB)
        {
            // a lane line near the ring: a point on it, its station and its outward direction there
            (Vec2 P, double S, Vec2 U) LaneLine(Arm arm, double off, double sign)
            {
                double s0 = Math.Min(StationAt(arm.Plan, off + sign * IslandHalf, outer + RunOut), arm.Mouth);
                var p0 = At(arm.Plan, s0, off + sign * arm.Flare(s0));
                var u0 = At(arm.Plan, s0 + 1, off + sign * arm.Flare(s0 + 1)) - p0;
                return (p0, s0, u0 / Math.Max(1e-9, u0.Length));
            }
            // the fillet off a lane line onto the ring lane, its centre on the side given (+1: the line's Perp side) and outside the
            // ring lane: the tangent point on the line (and its station), on the ring lane, and the fillet's centre
            (Vec2 T, double S, Vec2 C, Vec2 F)? Join((Vec2 P, double S, Vec2 U) line, double side, double re)
            {
                var away = line.U.Perp * side;
                var q = line.P + away * re - o;
                double qu = q.Dot(line.U), disc = qu * qu - (q.LengthSquared - (radius + re) * (radius + re));
                if (disc < 0) return null;
                double t = -qu + Math.Sqrt(disc);
                var tp = line.P + line.U * t;
                var f = tp + away * re;
                return (tp, line.S + t, o + (f - o) * (radius / (radius + re)), f);
            }
            static void Arc(List<Vec2> into, Vec2 f, Vec2 from, Vec2 to)
            {
                double r = from.DistanceTo(f), h0 = (from - f).Heading, dh = Angles.Delta(h0, (to - f).Heading);
                int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(dh) * r / 0.75));
                for (int k = 0; k <= steps; k++) into.Add(f + Vec2.FromHeading(h0 + dh * k / steps) * r);
            }
            var inLine = LaneLine(a, offA, +1);
            var outLine = LaneLine(b, offB, -1);
            double armSweep = Ccw(Heading(a), Heading(b));
            (Vec2 T, double S, Vec2 C, Vec2 F)? jin = null, jout = null;
            double ringSweep = 0;
            // the fillets smaller until they meet the ring lane in order and start inside their mouths
            for (double re = Math.Clamp(0.8 * radius + 3, 4, 15); re >= 1; re *= 0.75)
            {
                jin = Join(inLine, +1, re);
                jout = Join(outLine, -1, re);
                if (jin is not { } ji || jout is not { } jo) continue;
                ringSweep = Ccw((ji.C - o).Heading, (jo.C - o).Heading);
                if (ringSweep <= armSweep + 0.3 && ji.S < a.Mouth - 0.2 && jo.S < b.Mouth - 0.2) break;
            }
            var pts = new List<Vec2>();
            if (jin is not { } i || jout is not { } x) return pts;
            for (double s = a.Mouth; s > i.S; s -= 1) pts.Add(At(a.Plan, s, offA + a.Flare(s)));
            Arc(pts, i.F, i.T, i.C);
            int steps = Math.Max(1, (int)Math.Ceiling(ringSweep * radius / 1.0));
            double h0 = (i.C - o).Heading;
            for (int q = 1; q < steps; q++) pts.Add(o + Vec2.FromHeading(h0 + ringSweep * q / steps) * radius);
            Arc(pts, x.F, x.C, x.T);
            for (double s = Math.Ceiling(x.S); s < b.Mouth; s += 1) if (s > x.S + 0.05) pts.Add(At(b.Plan, s, offB - b.Flare(s)));
            pts.Add(At(b.Plan, b.Mouth, offB));
            Dedupe(pts);
            return pts;
        }
    }

    private static double Mid((double From, double To) span) => (span.From + span.To) * 0.5;

    /// <summary>Where two polylines first cross: the segments (after point I of a, point J of b) and the point.</summary>
    private static (int I, int J, Vec2 At)? Crossing(List<Vec2> a, List<Vec2> b)
    {
        for (int i = 0; i + 1 < a.Count; i++)
            for (int j = 0; j + 1 < b.Count; j++)
            {
                Vec2 p = a[i], r = a[i + 1] - a[i], q = b[j], s = b[j + 1] - b[j];
                double d = r.Cross(s);
                if (Math.Abs(d) < 1e-12) continue;
                double t = (q - p).Cross(s) / d, u = (q - p).Cross(r) / d;
                if (t >= 0 && t <= 1 && u >= 0 && u <= 1) return (i, j, p + r * t);
            }
        return null;
    }

    /// <summary>The counter-clockwise turn from <paramref name="from"/> to <paramref name="to"/>, in [0, 2π).</summary>
    private static double Ccw(double from, double to)
    {
        double d = Angles.Normalize(to - from);
        return d < 0 ? d + 2 * Math.PI : d;
    }

    private static void Line(List<Vec2> into, Vec2 a, Vec2 b)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(a.DistanceTo(b) / 1.0));
        for (int k = 0; k <= steps; k++) into.Add(a + (b - a) * ((double)k / steps));
    }

    /// <summary>A cubic from <paramref name="a"/> heading <paramref name="da"/> to <paramref name="b"/> heading <paramref name="db"/>.</summary>
    private static void Bezier(List<Vec2> into, Vec2 a, Vec2 da, Vec2 b, Vec2 db)
    {
        double k = a.DistanceTo(b) / 3;
        Vec2 c1 = a + da * k, c2 = b - db * k;
        int steps = Math.Max(4, (int)Math.Ceiling(a.DistanceTo(b) / 0.75));
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps, u = 1 - t;
            into.Add(a * (u * u * u) + c1 * (3 * u * u * t) + c2 * (3 * u * t * t) + b * (t * t * t));
        }
    }

    private static void Dedupe(List<Vec2> pts)
    {
        for (int i = pts.Count - 1; i > 0; i--)
            if (pts[i].DistanceTo(pts[i - 1]) < 0.01) pts.RemoveAt(i);
        if (pts.Count > 2 && pts[0].DistanceTo(pts[^1]) < 0.01) pts.RemoveAt(pts.Count - 1);
    }
}
