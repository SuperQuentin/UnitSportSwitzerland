namespace UnitSport.Tools.RoadGen.Layout;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;

/// <summary>
/// Lays a junction's core (#818, step 2 of <c>docs/plans/junction-layout.md</c>) from its arms' mouth sections:
/// <list type="number">
/// <item>a connector for every allowed move of every arriving lane, into the exit lane it feeds (left into the lane by the
/// centre line, right into the kerb lane, same-turn lanes lane to lane, through lanes in order from the centre line; bike
/// lanes into the exit's bike lane, else the kerb side of its kerb lane);</item>
/// <item>the mouths: each arm's pushed out until every turn's arc at its vehicle's least radius (car 6 m on the inner edge;
/// a truck's 10 m between two Major roads where it fits untouched; a bike lane concentric inside the car's) and both its
/// corners' kerbs start inside it;</item>
/// <item>every connector as wide as fits between the mouths (the user's rule), narrowed back toward its least radius where
/// its corner's kerb would start outside a mouth, and under it where buildings hold the corner;</item>
/// <item>each corner's kerb: the least rounding (legs of at least <see cref="TightLeg"/>) that keeps every connector
/// paved, so a corner no one turns round stays tight with no rule of its own (the user's generalization);</item>
/// <item>the outline (mouths and corners) and the lines through it, each a connector edge, chosen by <see cref="JunctionRules"/>.</item>
/// </list>
/// Pure: the rewriter hands it the arms' axes and a probe for buildings.
/// </summary>
public static class CorePlanner
{
    /// <summary>
    /// A car's and a truck's turning radius on the inner edge (m). A truck's only for a turn between two Major (or bigger) roads,
    /// and only where it fits untouched (the user, Oct 13 2026): <see cref="Plan"/> falls back to a car's otherwise.
    /// </summary>
    public const double CarInner = 6, TruckInner = 10;
    /// <summary>A corner no one turns round: its kerb runs on this far along both edges from where they meet (#700's tight corner).</summary>
    public static readonly double TightLeg = new JunctionOptions().TightKerb;
    /// <summary>A mouth is never pushed further out than this from the node (m); past it the turn is laid tighter than its vehicle wants.</summary>
    public const double MostMouth = 40;
    /// <summary>
    /// A local crossroads' mouths (<see cref="JunctionPlan.Local"/>) go no further (m) where its core then passes its checks: its
    /// turns are laid as tight as fits within, a gore's nose further out is not followed (#854, the user, Oct 19, at a garage
    /// ramp's fork in Geneva). Where it fails them, it is laid as any junction.
    /// </summary>
    public const double LocalMostMouth = 12;
    /// <summary>Arms closer than this merge their carriageways (a gore); arms within this of a straight line meet with a straight kerb.</summary>
    public static readonly double GoreAngle = 20 * Math.PI / 180;
    /// <summary>
    /// A corner wider than this is passed only by connectors turning less than <see cref="TurnAngle"/>: no turn rounds it,
    /// so its kerb is the S between the two mouths (one number with the turn's: 180 degrees less a turn).
    /// </summary>
    public static readonly double StraightAngle = Math.PI - 30 * Math.PI / 180;
    /// <summary>A connector turning less than this is laid between the mouths rather than setting them (a bend through, a slight "turn" at a fork).</summary>
    public static readonly double TurnAngle = 30 * Math.PI / 180;
    /// <summary>Lane lines closer than this to parallel are not joined by an arc.</summary>
    private static readonly double ParallelAngle = 2 * Math.PI / 180;
    /// <summary>Buildings are looked for beside kerb arcs up to this radius (m).</summary>
    private const double RoomRadius = 100;
    /// <summary>The largest kerb radius considered (m).</summary>
    private const double MostRadius = 500;
    /// <summary>A connector's edge may stand this far outside the outline (m): the mitred edges of a lane whose width eases along a tight curve.</summary>
    public const double OutsideTolerance = 0.25;
    /// <summary>A kerb's arc may start this far past its mouth (m) before the check fails: pushing the mouth out widens the turns and the arc with it.</summary>
    public const double KerbTolerance = 0.3;
    /// <summary>An arc is drawn with chords no longer than this (m).</summary>
    private const double Chord = 0.5;

    private readonly record struct Frame(Vec2 At, Vec2 U, double Station)
    {
        public Vec2 R => U.Perp;
        public Vec2 Point(double offset) => At + R * offset;
        /// <summary>A point's station along the arm (the arm taken straight from its mouth in).</summary>
        public double StationOf(Vec2 p) => Station + (p - At).Dot(U);
    }

    /// <summary>
    /// The core, a truck's radius on a turn between two Major (or bigger) roads only where it fits untouched (the user): a truck
    /// turn the buildings beside its corner or a held mouth (half the street to the next junction, <see cref="MostMouth"/>)
    /// would lay under its radius falls back to a car's, nothing in between, and the core is laid again. Without
    /// <paramref name="trucks"/> every turn is a car's.
    /// </summary>
    /// <param name="given">The connectors to lay in place of every allowed move's (a merged core's member, #854: its legs).</param>
    public static JunctionCore Plan(JunctionPlan jp, Func<Vec2, bool>? blocked = null, bool trucks = true, Func<List<Connector>>? given = null)
    {
        // a local crossroads within LocalMostMouth where that passes its checks, else as any junction
        static bool Passes(JunctionCore c) => c.Simple && c.KerbsNotTangent == 0 && c.ConnectorsOutside == 0;
        if (jp.Local && given is null && PlanWithin(jp, blocked, trucks, null, LocalMostMouth) is var tight && Passes(tight)) return tight;
        var core = PlanWithin(jp, blocked, trucks, given, MostMouth);
        if (Passes(core) || given is not null) return core;
        // failing its checks with its mouths out to MostMouth (a fork whose gore's nose lies far out, turns pushed past a building):
        // laid again with them held nearer, the first that passes kept (#854)
        foreach (double most in (ReadOnlySpan<double>)[RetryMostMouth, LocalMostMouth])
            if (PlanWithin(jp, blocked, trucks, null, most) is var held && Passes(held)) return held;
        return core;
    }

    /// <summary>A core failing its checks is laid again with its mouths within this, then <see cref="LocalMostMouth"/> (m).</summary>
    public const double RetryMostMouth = 20;

    private static JunctionCore PlanWithin(JunctionPlan jp, Func<Vec2, bool>? blocked, bool trucks, Func<List<Connector>>? given, double most)
    {
        var asCar = new HashSet<(int From, int To)>();
        for (int round = 0; ; round++)
        {
            var core = Lay(jp, blocked, (a, b) => trucks && a.Major && b.Major && !asCar.Contains((a.Arm, b.Arm)), given, most);
            bool fellBack = false;
            foreach (var c in core.Connectors)
                if (c.Truck && (c.Fill < 0 || c.UnderRadius)) fellBack |= asCar.Add((c.FromArm, c.ToArm));
            if (!fellBack || round >= 4)
            {
                core.TrucksAsCars = asCar.Count;
                return core;
            }
        }
    }

    private static JunctionCore Lay(JunctionPlan jp, Func<Vec2, bool>? blocked, Func<ArmPlan, ArmPlan, bool> truck, Func<List<Connector>>? given, double most)
    {
        var core = new JunctionCore();
        var arms = jp.Arms.Where(a => a.Axis is not null).OrderBy(a => Angles.Normalize(a.RingHeading)).ToList();
        int n = arms.Count;
        if (n < 2) return core;
        var index = new Dictionary<int, int>();
        for (int k = 0; k < n; k++) index[arms[k].Arm] = k;
        core.Connectors.AddRange(given?.Invoke() ?? Connectors(arms, index, truck));

        // 2. the mouths: pushed out until every turn's arc at its least radius and both kerbs start inside them
        var m = new double[n];
        Array.Fill(m, 1.0);
        Frame[] frames = Frames(arms, m);
        foreach (var c in core.Connectors)
            c.Turns = Math.Acos(Math.Clamp(-frames[index[c.FromArm]].U.Dot(frames[index[c.ToArm]].U), -1, 1)) >= TurnAngle;
        // a mouth stays on its street: at most MostMouth out, half the street where a junction stands at its other end (a
        // shorter street joins two nodes of one junction: phase 5's multi-node cores)
        double cap = most;
        double Most(int k) => Math.Min(cap, arms[k].Reach);
        var held = new bool[n];
        var needs = new double[n];
        for (int round = 0; round < 8; round++)
        {
            frames = Frames(arms, m);
            var need = new double[n];
            Array.Fill(need, 0.5);
            foreach (var c in core.Connectors.Where(c => c.Turns))
            {
                int a = index[c.FromArm], b = index[c.ToArm];
                // an arc only where the lanes' lines meet near the junction (as the kerbs'); else a smooth curve between the mouths
                if (Meet(frames, a, b, c) is not { } x || Math.Abs(frames[a].StationOf(x.Point)) > MostMouth || Math.Abs(frames[b].StationOf(x.Point)) > MostMouth)
                {
                    LaySmooth(frames, a, b, c);
                    continue;
                }
                double t = x.Half > 0 ? c.MinRadius * Math.Tan(x.Half) : 0;
                need[a] = Math.Max(need[a], frames[a].StationOf(x.Point) + t);
                need[b] = Math.Max(need[b], frames[b].StationOf(x.Point) + t);
                LayArc(frames, a, b, c, c.MinRadius, full: false);
            }
            foreach (var corner in Corners(arms, frames, core.Connectors, index, blocked, mouthCap: false))
                if (corner.Shape is CornerShape.Arc or CornerShape.Gore && !double.IsNaN(corner.StationA))
                {
                    need[index[corner.ArmA]] = Math.Max(need[index[corner.ArmA]], corner.StationA);
                    need[index[corner.ArmB]] = Math.Max(need[index[corner.ArmB]], corner.StationB);
                }
            bool settled = true;
            for (int k = 0; k < n; k++)
            {
                needs[k] = need[k];
                held[k] = need[k] > Most(k) + 1e-6;
                // on a curved arm the kerb's direction moves with the mouth: past the first rounds a mouth only moves out (settles)
                double at = Math.Min(round < 2 ? need[k] : Math.Max(m[k], need[k]), Math.Max(0.5, Most(k)));
                if (Math.Abs(at - m[k]) > 0.02) settled = false;
                m[k] = at;
            }
            if (settled) break;
        }
        for (int k = 0; k < n; k++) core.Needs[arms[k].Arm] = needs[k];
        core.MouthsAtMost = Enumerable.Range(0, n).Count(k => held[k] && Most(k) >= cap);
        core.MouthsAtReach = Enumerable.Range(0, n).Count(k => held[k] && Most(k) < cap);
        for (int k = 0; k < n; k++)
            if (held[k] && Most(k) < cap) core.ArmsAtReach.Add(arms[k].Arm);

        // two arms leaving within a gore of each other at their mouths (a garage ramp beside its street, #854, the user, Oct 19):
        // a move between them turns back round the gore's nose, a U-turn no one makes
        frames = Frames(arms, m);
        core.Connectors.RemoveAll(c => frames[index[c.FromArm]].U.Dot(frames[index[c.ToArm]].U) > Math.Cos(GoreAngle));

        // 3 and 4, until every kerb starts inside its mouth: a corner the final connectors round further (a bend through the
        // mouths did not reckon with) pushes its mouths out, and all again
        var least = core.Connectors.ToDictionary(c => c, c => c.MinRadius);
        List<CoreCorner> corners;
        for (int round = 0; ; round++)
        {
        frames = Frames(arms, m);
        // 3. every connector as wide as fits between the mouths
        foreach (var c in core.Connectors)
        {
            int a = index[c.FromArm], b = index[c.ToArm];
            (c.MinRadius, c.UnderRadius) = (least[c], false);
            var x = Meet(frames, a, b, c);
            if (x is not { } meet || meet.In <= 0 || meet.Out <= 0)
            {
                c.MinRadius = c.WideRadius = c.Radius = 0;
                Hold(arms, frames, a, b, c);
                LaySmooth(frames, a, b, c);
                continue;
            }
            double wide = Math.Max(0, Math.Min(meet.In, meet.Out)) / Math.Tan(meet.Half);
            // a bend through whose lanes' lines meet next to a mouth (arms nearly in line, offset): a smooth S between the
            // mouths, not an arc tighter than a car's (#854: a 1.5 m kink left the outline)
            if (!c.Turns && wide < CarInner + c.Width * 0.5)
            {
                c.MinRadius = c.WideRadius = c.Radius = 0;
                Hold(arms, frames, a, b, c);
                LaySmooth(frames, a, b, c);
                continue;
            }
            // a turn the held mouths leave no room for at its vehicle's radius: as tight as it fits
            if (c.Turns && wide < c.MinRadius - 0.01) (c.UnderRadius, c.MinRadius) = (true, Math.Max(wide, c.Width * 0.5 + 0.5));
            // a bend through: as wide as fits, narrowed (where its kerb would start outside a mouth) at most to a car's radius
            if (!c.Turns) c.MinRadius = Math.Min(wide, CarInner + c.Width * 0.5);
            c.WideRadius = Math.Max(wide, c.MinRadius);
            c.Fill = 1;
            Lay(frames, index, c);
        }

        // 4. the corners: each the envelope of its connectors, the turns narrowed where it would start outside a mouth
        // or reach a building
        var narrowed = Corners(arms, frames, core.Connectors, index, blocked, mouthCap: true, held: k => held[k]);
        // every kerb drawn again from the final connectors: narrowing a turn at one corner tightens its arc, which may cut
        // deeper into the corner at its other side
        corners = Corners(arms, frames, core.Connectors, index, blocked, mouthCap: true, narrow: false, held: k => held[k]);
        for (int k = 0; k < corners.Count; k++) corners[k].RoomClipped = narrowed[k].RoomClipped;
        // (pushed at most four times; the connectors and corners are then laid once more on the final mouths, a kerb still
        // starting outside its mouth failing the check: they ended at the mouths before the last push)
        bool pushed = false;
        if (round < 8)
            foreach (var corner in corners.Where(c => c.Shape == CornerShape.Arc))
                foreach (var (k, at) in (ReadOnlySpan<(int, double)>)[(index[corner.ArmA], corner.StationA), (index[corner.ArmB], corner.StationB)])
                    if (at > m[k] + 0.02 && m[k] < Most(k) - 0.02)
                    {
                        m[k] = Math.Min(at, Most(k));
                        pushed = true;
                    }
        if (!pushed) break;
        }
        core.Corners.AddRange(corners);
        foreach (var corner in corners)
        {
            int a = index[corner.ArmA], b = index[corner.ArmB];
            if (corner.Shape == CornerShape.Arc && (corner.StationA > m[a] + KerbTolerance || corner.StationB > m[b] + KerbTolerance)) core.KerbsNotTangent++;
        }

        // the outline: each mouth across its carriageway, then the corner after it
        for (int k = 0; k < n; k++)
        {
            var sec = arms[k].Mouth;
            var left = frames[k].Point(sec.CarriagewayLeft);
            var right = frames[k].Point(sec.CarriagewayRight);
            core.Mouths.Add(new CoreMouth(arms[k].Arm, m[k], left, right));
            core.Outline.Add(left);
            core.Outline.Add(right);
            core.Outline.AddRange(corners[k].Kerb);
        }
        Dedupe(core.Outline);
        Pave(core, n);
        core.Area = Math.Abs(SignedArea(core.Outline));
        // the surface: the outline triangulated (as today's cap); an outline crossing itself is reported
        core.Simple = Simple(core.Outline);
        core.Triangles.AddRange(EarClip.Triangulate(core.Outline));
        foreach (var c in core.Connectors)
        {
            c.Outside = c.Left.Concat(c.Right).Select(p => Outside(core.Outline, p)).Max();
            if (c.Outside > OutsideTolerance) core.ConnectorsOutside++;
        }

        Lines(jp, arms, index, core);
        return core;
    }

    /// <summary>
    /// A plain core where the planned one fails its checks (#854, the user, Oct 20: so the old layout can go): its mouths and
    /// connectors, and between each mouth and the next a straight kerb, eased where the arms do not line up and pushed out wherever
    /// a connector passes beyond it (<see cref="InLineKerb"/>); where the kerbs cross, the loops cut away, the mouths kept. No check
    /// holds it back; <see cref="JunctionCore.IsFallback"/> counts it.
    /// </summary>
    /// <param name="limit">How far out each arm's mouth may stand (short of the next node where anything joins its street).</param>
    public static JunctionCore Fallback(JunctionPlan jp, JunctionCore failed, Func<ArmPlan, double>? limit = null)
    {
        var core = new JunctionCore { IsFallback = true };
        foreach (var m in failed.Mouths)
        {
            // (held in short of a road joining its street: that road's own cap past it)
            var arm = jp.Arms.FirstOrDefault(a => a.Arm == m.Arm);
            double most = arm?.Axis is not null && limit is not null ? limit(arm) : double.MaxValue;
            core.Mouths.Add(m.Station <= most ? m : new CoreMouth(m.Arm, Math.Max(0.5, most),
                arm!.Axis!.At(Math.Max(0.5, most), arm.Mouth.CarriagewayLeft), arm.Axis.At(Math.Max(0.5, most), arm.Mouth.CarriagewayRight)));
        }
        core.Connectors.AddRange(failed.Connectors);
        // each move laid again as one smooth curve from its lane at its mouth to its lane at the exit's (the failed core's may stray)
        var stationOf = core.Mouths.ToDictionary(m => m.Arm, m => m.Station);
        foreach (var c in core.Connectors)
        {
            var from = jp.Arms.FirstOrDefault(a => a.Arm == c.FromArm);
            var to = jp.Arms.FirstOrDefault(a => a.Arm == c.ToArm);
            if (from?.Axis is null || to?.Axis is null || !stationOf.TryGetValue(c.FromArm, out double sf) || !stationOf.TryGetValue(c.ToArm, out double st)) continue;
            Vec2 p = from.Axis.At(sf, c.FromOffset), q = to.Axis.At(st, c.ToOffset), din = -from.Axis.DirAt(sf), dout = to.Axis.DirAt(st);
            double turn = Math.Min(Math.Acos(Math.Clamp(din.Dot(dout), -1, 1)), 170 * Math.PI / 180);
            double handle = p.DistanceTo(q) * (turn < 1e-6 ? 1.0 / 3 : 2.0 / 3 * Math.Tan(turn / 4) / Math.Sin(turn / 2));
            Vec2 c1 = p + din * handle, c2 = q - dout * handle;
            int steps = Math.Max(8, (int)Math.Ceiling(p.DistanceTo(q) / Chord));
            var pts = new List<Vec2>(steps + 1);
            for (int k = 0; k <= steps; k++)
            {
                double t = (double)k / steps, mt = 1 - t;
                pts.Add(p * (mt * mt * mt) + c1 * (3 * mt * mt * t) + c2 * (3 * mt * t * t) + q * (t * t * t));
            }
            c.Radius = 0;
            Set(c, pts, 0, 0);
        }
        core.Lines.AddRange(failed.Lines);
        int n = core.Mouths.Count;
        foreach (var (k, needs) in failed.Needs) core.Needs[k] = needs;
        core.ArmsAtReach.UnionWith(failed.ArmsAtReach);
        Vec2 U(CoreMouth m) => jp.Arms.First(a => a.Arm == m.Arm).Axis!.DirAt(m.Station);
        var ends = core.Mouths.SelectMany(m => new[] { m.Left, m.Right }).ToHashSet();
        var paved = core.Connectors.SelectMany(c => c.Left.Concat(c.Right)).ToList();
        List<Vec2> Build(bool star)
        {
            // (star: the mouths in their order round their centre, straight between: simple where the kerbs cross)
            if (star)
            {
                var centre = new Vec2(core.Mouths.Average(m => (m.Left.X + m.Right.X) * 0.5), core.Mouths.Average(m => (m.Left.Y + m.Right.Y) * 0.5));
                var order = core.Mouths.OrderBy(m => ((m.Left + m.Right) * 0.5 - centre).Heading).ToList();
                core.Mouths.Clear();
                core.Mouths.AddRange(order);
            }
            var outline = new List<Vec2>();
            for (int k = 0; k < n; k++)
            {
                CoreMouth a = core.Mouths[k], b = core.Mouths[(k + 1) % n];
                outline.Add(a.Left);
                outline.Add(a.Right);
                if (!star) outline.AddRange(InLineKerb(a.Right, -U(a), b.Left, U(b), core.Connectors));
            }
            MergedCore.Clean(outline, ends);
            if (SignedArea(outline) < 0) outline.Reverse();
            // (every connector paved: the outline pushed out where one still passes beyond it, the loops cut again)
            // (in rounds while it helps: a push reaches only the points beside each kerb point, a far one takes a few)
            int out0 = Out(outline);
            for (int round = 0; round < 4 && out0 > 0; round++)
            {
                var next = outline.ToList();
                MergedCore.Envelope(next, paved, core.Mouths, FallbackPush, nearMouth: 0.05);
                MergedCore.Clean(next, ends);
                if (SignedArea(next) < 0) next.Reverse();
                int out1 = Out(next);
                if (out1 >= out0) break;
                (outline, out0) = (next, out1);
            }
            return outline;
        }
        int Out(List<Vec2> ring) => paved.Count(p => Outside(ring, p) > OutsideTolerance);
        var outline = Build(star: false);
        bool starred = false;
        // (the mouths joined round their centre where the kerbs cross)
        if (!Simple(outline) || !ends.All(outline.Contains))
        {
            var mouths = core.Mouths.ToList();
            var round = Build(star: true);
            if (Simple(round) || !Simple(outline)) { outline = round; starred = true; }
            else { core.Mouths.Clear(); core.Mouths.AddRange(mouths); }
        }
        core.Outline.AddRange(outline);
        if (Environment.GetEnvironmentVariable("FALLBACKDBG") is not null)
            Console.WriteLine(FormattableString.Invariant($"[fallback] {jp.Centre.X:F0},{jp.Centre.Y:F0} star {starred} simple {Simple(outline)} mouths {ends.Count(outline.Contains)}/{ends.Count} centre points out {core.Connectors.Sum(c => c.Centre.Count(p => Outside(outline, p) > 0.05))} (most {core.Connectors.SelectMany(c => c.Centre).Select(p => Outside(outline, p)).DefaultIfEmpty(0).Max():F2})"));
        var at = core.Mouths.Select(m => (L: outline.IndexOf(m.Left), R: outline.IndexOf(m.Right))).ToList();
        for (int k = 0; k < n; k++)
        {
            var corner = new CoreCorner { ArmA = core.Mouths[k].Arm, ArmB = core.Mouths[(k + 1) % n].Arm, Shape = CornerShape.Chord,
                StationA = double.NaN, StationB = double.NaN };
            if (at[k].R >= 0 && at[(k + 1) % n].L >= 0)
                for (int p = (at[k].R + 1) % outline.Count; p != at[(k + 1) % n].L; p = (p + 1) % outline.Count) corner.Kerb.Add(outline[p]);
            core.Corners.Add(corner);
        }
        // (a mouth cut away by the clean-up: its arm ends inside the paved core, a fallback's residue)
        core.Simple = Simple(core.Outline);
        core.Area = Math.Abs(SignedArea(core.Outline));
        core.Triangles.AddRange(EarClip.Triangulate(core.Outline));
        foreach (var c in core.Connectors)
        {
            c.Outside = c.Left.Concat(c.Right).Select(p => Outside(core.Outline, p)).DefaultIfEmpty(0).Max();
            if (c.Outside > OutsideTolerance) core.ConnectorsOutside++;
        }
        return core;
    }

    /// <summary>
    /// Today's cap as a core (#854, the user, Oct 20: what no plain core can hold, a road joining right at the node, a path's stub
    /// meeting it): its boundary (every arm, the paths' too, already trimmed to it), its car arms' mouths at today's trims, each move
    /// a smooth curve between them (as <see cref="Fallback"/>'s). Null where an arm has no end on the cap.
    /// </summary>
    public static JunctionCore? Cap(JunctionPlan jp, Junction cap, IReadOnlyList<Connector> moves)
    {
        if (cap.Boundary.Count < 3) return null;
        // each mouth at today's trim, across the planner's carriageway there (its transition starts at the written mouth with the
        // mouth's section: the moves keep their lanes), today's ribbon corners in the boundary moved out to it
        var stub = new JunctionCore();
        var outline = cap.Boundary.ToList();
        foreach (var a in jp.Arms)
        {
            int i = cap.Arms.FindIndex(x => x.LinkId == a.LinkId);
            if (i < 0 || a.Axis is null) return null;
            double s = cap.Arms[i].Trim;
            var mouth = new CoreMouth(a.Arm, s, a.Axis.At(s, a.Mouth.CarriagewayLeft), a.Axis.At(s, a.Mouth.CarriagewayRight));
            stub.Mouths.Add(mouth);
            // (a driver arriving faces the node: the arm's right looking outward is the mouth's left)
            int l = outline.FindIndex(p => p.DistanceTo(cap.Arms[i].Right) < 1e-3), r = outline.FindIndex(p => p.DistanceTo(cap.Arms[i].Left) < 1e-3);
            if (l < 0 || r < 0) return null;
            outline[l] = mouth.Left;
            outline[r] = mouth.Right;
        }
        stub.Connectors.AddRange(moves);
        var core = Fallback(jp, stub);
        core.IsCap = true;
        if (SignedArea(outline) < 0) outline.Reverse();
        var ends = core.Mouths.SelectMany(m => new[] { m.Left, m.Right }).ToHashSet();
        var paved = core.Connectors.SelectMany(c => c.Left.Concat(c.Right)).ToList();
        MergedCore.Clean(outline, ends);
        for (int round = 0; round < 4 && paved.Any(p => Outside(outline, p) > OutsideTolerance); round++)
        {
            MergedCore.Envelope(outline, paved, core.Mouths, FallbackPush, nearMouth: 0.05);
            MergedCore.Clean(outline, ends);
            if (SignedArea(outline) < 0) outline.Reverse();
        }
        core.Outline.Clear();
        core.Outline.AddRange(outline);
        core.Corners.Clear();
        core.Triangles.Clear();
        core.Triangles.AddRange(EarClip.Triangulate(core.Outline));
        core.Simple = Simple(core.Outline);
        core.Area = Math.Abs(SignedArea(core.Outline));
        core.ConnectorsOutside = 0;
        foreach (var c in core.Connectors)
            if ((c.Outside = c.Left.Concat(c.Right).Select(p => Outside(core.Outline, p)).DefaultIfEmpty(0).Max()) > OutsideTolerance) core.ConnectorsOutside++;
        return core;
    }

    /// <summary>The most a fallback core's outline is pushed out to pave a connector (m).</summary>
    private const double FallbackPush = 20;

    /// <summary>The least kerb rounding beside a held mouth (m, <see cref="CoreCorner.Floor"/>).</summary>
    private const double MinFloor = 0.3;

    /// <summary>The most a kerb is pushed out to keep a connector paved (m): further, the core fails its check.</summary>
    private const double MostPave = 0.6;

    /// <summary>
    /// The kerbs pushed out wherever a connector's edge passes beyond them by up to <see cref="MostPave"/> (#854: cores failing
    /// their check by a few decimetres, a turn's mitred edge past its arc), the mouths kept; each corner's kerb taken again from
    /// the outline (the sidewalk corners follow it). Whether it moved them.
    /// </summary>
    internal static bool Pave(JunctionCore core, int n)
    {
        var paved = core.Connectors.SelectMany(c => c.Left.Concat(c.Right)).ToList();
        if (!paved.Any(p => Outside(core.Outline, p) is > OutsideTolerance and <= MostPave)) return false;
        var ends = core.Mouths.SelectMany(m => new[] { m.Left, m.Right }).ToHashSet();
        var ring = core.Outline.ToList();
        MergedCore.Envelope(ring, paved, core.Mouths, MostPave);
        // (each mouth's two ends kept in order: the kerbs between them taken again)
        var at = core.Mouths.Select(m => (L: ring.IndexOf(m.Left), R: ring.IndexOf(m.Right))).ToList();
        if (at.Any(x => x.L < 0 || x.R < 0) || !Simple(ring)) return false;
        for (int k = 0; k < n; k++)
        {
            var kerb = core.Corners[k].Kerb;
            if (core.Corners[k].Shape == CornerShape.Gore) continue;
            kerb.Clear();
            for (int p = (at[k].R + 1) % ring.Count; p != at[(k + 1) % n].L; p = (p + 1) % ring.Count) kerb.Add(ring[p]);
        }
        core.Outline.Clear();
        core.Outline.AddRange(ring);
        return true;
    }

    // ------------------------------------------------------------------ connectors: which lanes join

    internal static List<Connector> Connectors(List<ArmPlan> arms, Dictionary<int, int> index, Func<ArmPlan, ArmPlan, bool> truck)
    {
        var list = new List<Connector>();
        foreach (var a in arms)
        {
            var sec = a.Mouth;
            var inCars = sec.CarLanes(StripWay.In).ToList();
            foreach (var move in (ReadOnlySpan<SignalMoves>)[SignalMoves.Left, SignalMoves.Through, SignalMoves.Right])
            {
                if (!a.Toward.TryGetValue(move, out int to) || !index.TryGetValue(to, out int bi)) continue;
                var b = arms[bi];
                // the exit's departing lanes from its centre line out
                var outs = b.Mouth.CarLanes(StripWay.Out).Reverse().ToList();
                if (outs.Count == 0) continue;
                bool forTruck = truck(a, b);
                double inner = forTruck ? TruckInner : CarInner;
                // left into the lane by the centre line, same-turn lanes lane to lane; right counted from the kerb
                var lanes = inCars.Where(l => (l.Strip.Moves & move) != 0).ToList();
                for (int k = 0; k < lanes.Count; k++)
                {
                    int target = Math.Clamp(move == SignalMoves.Right ? outs.Count - lanes.Count + k : k, 0, outs.Count - 1);
                    var o = outs[target];
                    list.Add(new Connector
                    {
                        FromArm = a.Arm, FromId = lanes[k].Strip.Id, ToArm = b.Arm, ToId = o.Strip.Id, Move = move, Kind = ConnectorKind.Car,
                        Width = lanes[k].Strip.Width, ToWidth = o.Strip.Width, FromOffset = Mid(sec.Span(lanes[k].Index)), ToOffset = Mid(b.Mouth.Span(o.Index)),
                        MinRadius = inner + (lanes[k].Strip.Width + o.Strip.Width) * 0.25, Truck = forTruck,
                    });
                }
                // the riders: each arriving bike lane making the move, into the exit's bike lane, else the kerb side of its kerb lane;
                // concentric inside the car's turn (the user: the bike lane turns within the car's 6 m)
                for (int k = 0; k < sec.Strips.Count; k++)
                {
                    var s = sec.Strips[k];
                    if (s.Kind != StripKind.BikeLane || s.Way != StripWay.In || (s.Moves & move) == 0) continue;
                    int bike = Enumerable.Range(0, b.Mouth.Strips.Count)
                        .FirstOrDefault(j => b.Mouth.Strips[j].Kind == StripKind.BikeLane && b.Mouth.Strips[j].Way == StripWay.Out, -1);
                    double toWidth = bike >= 0 ? b.Mouth.Strips[bike].Width : s.Width;
                    double toOffset = bike >= 0 ? Mid(b.Mouth.Span(bike)) : b.Mouth.Span(outs[^1].Index).From + toWidth * 0.5;
                    list.Add(new Connector
                    {
                        FromArm = a.Arm, FromId = s.Id, ToArm = b.Arm, ToId = bike >= 0 ? b.Mouth.Strips[bike].Id : outs[^1].Strip.Id, Move = move,
                        Kind = ConnectorKind.Bike, Width = s.Width, ToWidth = toWidth, FromOffset = Mid(sec.Span(k)), ToOffset = toOffset,
                        MinRadius = Math.Max(1, inner - s.Width) + s.Width * 0.5,
                    });
                }
            }
        }
        return list;

        static double Mid((double From, double To) span) => (span.From + span.To) * 0.5;
    }

    // ------------------------------------------------------------------ geometry of one connector

    private static Frame[] Frames(List<ArmPlan> arms, double[] m)
    {
        var frames = new Frame[arms.Count];
        for (int k = 0; k < arms.Count; k++) frames[k] = new Frame(arms[k].Axis!.PointAt(m[k]), arms[k].Axis!.DirAt(m[k]), m[k]);
        return frames;
    }

    /// <summary>Where a connector's two lane lines meet, how far that is from each mouth along them, and half the turn.</summary>
    private readonly record struct Meeting(Vec2 Point, double In, double Out, double Half);

    private static Meeting? Meet(Frame[] frames, int a, int b, Connector c)
    {
        var (p, din, q, dout) = Ends(frames, a, b, c);
        double cross = din.Cross(dout);
        double turn = Math.Acos(Math.Clamp(din.Dot(dout), -1, 1));
        if (Math.Abs(cross) < 1e-9 || turn < ParallelAngle) return null;
        // p + din t = q + dout s
        double t = (q - p).Cross(dout) / cross;
        var x = p + din * t;
        return new Meeting(x, t, (q - x).Dot(dout), turn * 0.5);
    }

    private static (Vec2 P, Vec2 Din, Vec2 Q, Vec2 Dout) Ends(Frame[] frames, int a, int b, Connector c) =>
        (frames[a].Point(c.FromOffset), -frames[a].U, frames[b].Point(c.ToOffset), frames[b].U);

    /// <summary>Lays a connector at its fill: from its least radius (0) to as wide as fits (1), under its least below 0.</summary>
    private static void Lay(Frame[] frames, Dictionary<int, int> index, Connector c)
    {
        int a = index[c.FromArm], b = index[c.ToArm];
        if (c.WideRadius <= 0) { LaySmooth(frames, a, b, c); return; }
        double r = c.Fill >= 0 ? c.MinRadius + c.Fill * (c.WideRadius - c.MinRadius) : c.MinRadius * (1 + 0.5 * c.Fill);
        LayArc(frames, a, b, c, Math.Max(r, c.Width * 0.5 + 0.5), full: true);
    }

    /// <summary>Two straight runs joined by an arc of centre radius <paramref name="radius"/>; without <paramref name="full"/> the arc alone.</summary>
    private static void LayArc(Frame[] frames, int a, int b, Connector c, double radius, bool full)
    {
        var (p, din, q, dout) = Ends(frames, a, b, c);
        if (Meet(frames, a, b, c) is not { } x) { LaySmooth(frames, a, b, c); return; }
        double t = radius * Math.Tan(x.Half);
        var t1 = x.Point - din * t;
        var t2 = x.Point + dout * t;
        bool left = din.Cross(dout) > 0;
        var centre = t1 + (left ? din.Perp : -din.Perp) * radius;
        double a0 = (t1 - centre).Heading, sweep = (left ? 1 : -1) * x.Half * 2;
        int steps = Math.Max(2, (int)Math.Ceiling(radius * Math.Abs(sweep) / Chord));
        var pts = new List<Vec2>();
        if (full) pts.Add(p);
        for (int k = 0; k <= steps; k++) pts.Add(centre + Vec2.FromHeading(a0 + sweep * k / steps) * radius);
        if (full) pts.Add(q);
        Dedupe(pts);
        c.Radius = radius;
        Set(c, pts, full ? p.DistanceTo(t1) : 0, full ? q.DistanceTo(t2) : 0);
    }

    /// <summary>
    /// A straight line, or a smooth S (a cubic leaving and arriving along the lanes) where the lanes do not line up, after
    /// the straight runs <see cref="Connector.HoldIn"/> and <see cref="Connector.HoldOut"/>.
    /// </summary>
    private static void LaySmooth(Frame[] frames, int a, int b, Connector c)
    {
        var (p, din, q, dout) = Ends(frames, a, b, c);
        c.Radius = 0;
        if (Math.Abs((q - p).Cross(din)) < 0.05 && din.Dot(dout) > 0.9995) { Set(c, [p, q], 0, 0); return; }
        Vec2 from = p + din * c.HoldIn, to = q - dout * c.HoldOut;
        double l = from.DistanceTo(to) / 3;
        Vec2 c1 = from + din * l, c2 = to - dout * l;
        var pts = new List<Vec2> { p };
        const int Steps = 16;
        for (int k = 0; k <= Steps; k++)
        {
            double t = (double)k / Steps, mt = 1 - t;
            pts.Add(from * (mt * mt * mt) + c1 * (3 * mt * mt * t) + c2 * (3 * mt * t * t) + to * (t * t * t));
        }
        pts.Add(q);
        Dedupe(pts);
        Set(c, pts, c.HoldIn, c.HoldOut);
    }

    /// <summary>The longest an S takes per metre it moves sideways (1:5); it holds its lane no longer than leaves it that.</summary>
    private const double SidewaysPerMetre = 5;

    /// <summary>
    /// How long an S holds its lane before it moves sideways: on the side it moves toward, a kerb runs beside the arriving
    /// lane up to the corner on the driver's right (a lane moving right holds it till that corner), and beside the departing
    /// lane from the corner on its right (a lane moving left comes onto it from the kerb side only past that corner). Moving
    /// toward the centre line it meets no kerb. At most what leaves the S <see cref="SidewaysPerMetre"/> metres per metre sideways.
    /// </summary>
    private static void Hold(List<ArmPlan> arms, Frame[] frames, int a, int b, Connector c)
    {
        c.HoldIn = c.HoldOut = 0;
        var (p, din, q, _) = Ends(frames, a, b, c);
        double sideways = (q - p).Dot(-din.Perp);   // > 0: the exit lane lies to the driver's right
        if (Math.Abs(sideways) < 0.05) return;
        int n = arms.Count;
        if (sideways > 0 && KerbMeet(arms, frames, a, (a + 1) % n) is { } ca)
            c.HoldIn = Math.Clamp(frames[a].Station - Math.Max(0, frames[a].StationOf(ca)), 0, frames[a].Station);
        if (sideways < 0 && KerbMeet(arms, frames, (b - 1 + n) % n, b) is { } cb)
            c.HoldOut = Math.Clamp(frames[b].Station - Math.Max(0, frames[b].StationOf(cb)), 0, frames[b].Station);
        double most = Math.Max(0, (q - p).Dot(din) - SidewaysPerMetre * Math.Abs(sideways));
        c.HoldIn = Math.Min(c.HoldIn, most);
        c.HoldOut = Math.Min(c.HoldOut, most);
    }

    /// <summary>Where arm <paramref name="i"/>'s right kerb meets arm <paramref name="j"/>'s left kerb, where they meet as a corner (not in line).</summary>
    private static Vec2? KerbMeet(List<ArmPlan> arms, Frame[] frames, int i, int j)
    {
        Vec2 ui = frames[i].U, uj = frames[j].U;
        double alpha = Math.Atan2(ui.Cross(uj), ui.Dot(uj));
        if (alpha <= 0) alpha += 2 * Math.PI;
        double cross = ui.Cross(uj);
        if (alpha < GoreAngle || alpha > 2 * Math.PI - GoreAngle || alpha >= StraightAngle || Math.Abs(cross) < 1e-9) return null;
        var ki = frames[i].Point(arms[i].Mouth.CarriagewayRight);
        var kj = frames[j].Point(arms[j].Mouth.CarriagewayLeft);
        var meet = ki + ui * ((kj - ki).Cross(uj) / cross);
        double si = frames[i].StationOf(meet), sj = frames[j].StationOf(meet);
        return si >= -1 && sj >= -1 && si <= MostMouth && sj <= MostMouth ? meet : null;
    }

    /// <summary>
    /// A connector's centre line and its edges: its lane's width along the straight run in (<paramref name="runIn"/> m), the
    /// exit lane's along the run out, easing from one to the other along the curve between.
    /// </summary>
    internal static void Set(Connector c, List<Vec2> centre, double runIn, double runOut)
    {
        c.Centre = centre;
        // its width eases from its lane's to the exit lane's along its length
        var at = new double[centre.Count];
        for (int k = 1; k < centre.Count; k++) at[k] = at[k - 1] + centre[k].DistanceTo(centre[k - 1]);
        double from = Math.Min(runIn, at[^1]), to = Math.Max(from, at[^1] - runOut);
        double Half(int k) => 0.5 * (c.Width + (c.ToWidth - c.Width) * (to - from > 1e-9 ? Math.Clamp((at[k] - from) / (to - from), 0, 1) : at[k] > from ? 1 : 0));
        c.Left = Offset(centre, Half);
        c.Right = Offset(centre, k => -Half(k));
    }

    /// <summary>A polyline moved sideways, to its left by <paramref name="by"/> (right when negative), mitred at its vertices.</summary>
    public static List<Vec2> Offset(IReadOnlyList<Vec2> line, double by) => Offset(line, _ => by);

    /// <summary>A polyline moved sideways by a distance per vertex, to its left (right when negative), mitred at its vertices.</summary>
    public static List<Vec2> Offset(IReadOnlyList<Vec2> line, Func<int, double> by)
    {
        var result = new List<Vec2>(line.Count);
        for (int k = 0; k < line.Count; k++)
        {
            var before = k > 0 ? (line[k] - line[k - 1]).Normalized() : (line[k + 1] - line[k]).Normalized();
            var after = k + 1 < line.Count ? (line[k + 1] - line[k]).Normalized() : before;
            var normal = (before.Perp + after.Perp).Normalized();
            double cos = Math.Max(0.5, normal.Dot(after.Perp));
            result.Add(line[k] + normal * (by(k) / cos));
        }
        return result;
    }

    // ------------------------------------------------------------------ corners

    /// <summary>
    /// Every corner between one arm's right kerb and the next arm's left kerb (counter-clockwise), shaped round the connectors
    /// laid. With <paramref name="mouthCap"/> (the mouths settled) the turns passing a corner are narrowed until its kerb starts
    /// inside both mouths and keeps off the buildings, and each corner's kerb is drawn.
    /// </summary>
    private static List<CoreCorner> Corners(List<ArmPlan> arms, Frame[] frames, List<Connector> connectors, Dictionary<int, int> index,
        Func<Vec2, bool>? blocked, bool mouthCap, bool narrow = true, Func<int, bool>? held = null)
    {
        int n = arms.Count;
        var corners = new List<CoreCorner>(n);
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            var corner = new CoreCorner { ArmA = arms[i].Arm, ArmB = arms[j].Arm };
            corners.Add(corner);
            var fi = frames[i];
            var fj = frames[j];
            Vec2 ui = fi.U, uj = fj.U;
            var ki = fi.Point(arms[i].Mouth.CarriagewayRight);
            var kj = fj.Point(arms[j].Mouth.CarriagewayLeft);
            double alpha = Math.Atan2(ui.Cross(uj), ui.Dot(uj));
            if (alpha <= 0) alpha += 2 * Math.PI;
            corner.Angle = alpha;
            double cross = ui.Cross(uj);
            Vec2? meet = Math.Abs(cross) < 1e-9 ? null : ki + ui * ((kj - ki).Cross(uj) / cross);
            double si = meet is { } q ? fi.StationOf(q) : double.NaN, sj = meet is { } r0 ? fj.StationOf(r0) : double.NaN;
            corner.StationA = corner.StationB = double.NaN;
            // arms within GoreAngle either way (their order at the mouths may differ from the node's)
            if (alpha < GoreAngle || alpha > 2 * Math.PI - GoreAngle)
            {
                // two arms leaving almost together: their carriageways part where the kerbs between them meet (the gore's
                // nose); the mouths are pushed out to it, as far as they go
                corner.Shape = CornerShape.Gore;
                if (meet is { } g && si >= -1 && sj >= -1)
                {
                    corner.Meet = g;
                    (corner.StationA, corner.StationB) = (Math.Min(si, MostMouth), Math.Min(sj, MostMouth));
                    if (mouthCap && si <= fi.Station + 0.02 && sj <= fj.Station + 0.02) corner.Kerb.Add(g);
                }
                continue;
            }
            // a kerb arc only where the kerbs meet near the junction (arms nearly in line with kerbs offset meet far off)
            bool near = meet is not null && Math.Abs(si) <= MostMouth && Math.Abs(sj) <= MostMouth;
            if (alpha >= StraightAngle || !near)
            {
                // the outside of a bend: the kerbs meet behind both mouths; else a straight kerb between them
                if (meet is { } v && alpha > Math.PI && fi.StationOf(v) <= fi.Station && fj.StationOf(v) <= fj.Station
                    && fi.StationOf(v) > -MostMouth && fj.StationOf(v) > -MostMouth)
                {
                    corner.Shape = CornerShape.Vertex;
                    corner.Meet = v;
                    corner.Kerb.Add(v);
                }
                else
                {
                    // arms in line: the kerb eases from one to the other as an S, pushed out where a connector passes beyond it
                    // (the envelope rule, as at a corner: every kerb keeps every connector paved)
                    corner.Shape = CornerShape.Chord;
                    if (mouthCap)
                        corner.Kerb.AddRange(InLineKerb(fi.Point(arms[i].Mouth.CarriagewayRight), -ui, fj.Point(arms[j].Mouth.CarriagewayLeft), uj, connectors));
                }
                continue;
            }
            var c = meet.Value;
            corner.Shape = CornerShape.Arc;
            corner.Meet = c;
            double half = alpha * 0.5;
            var bisector = (ui + uj).Normalized();
            corner.Floor = TightLeg * Math.Tan(half);
            // beside a mouth held short (half the street to the next junction, the most out): the least rounding is what the mouths
            // leave, and the turns past it may go under their vehicle's radius (#854: two cores held apart, the user, Oct 17)
            bool heldHere = mouthCap && held is not null && (held(i) || held(j));
            if (heldHere)
                corner.Floor = Math.Max(MinFloor, Math.Min(corner.Floor, Math.Min(fi.Station - fi.StationOf(c), fj.Station - fj.StationOf(c)) * Math.Tan(half)));
            double r = Envelope(c, ui, uj, half, bisector, connectors, corner.Floor);
            if (mouthCap)
            {
                // the most the mouths allow, and the buildings beside the corner
                double legs = Math.Min(fi.Station - fi.StationOf(c), fj.Station - fj.StationOf(c));
                double cap = Math.Max(corner.Floor, legs * Math.Tan(half));
                double band = Math.Max(Bands(arms[i].Mouth, right: true), Bands(arms[j].Mouth, right: false));
                double room = blocked is null ? MostRadius : Room(c, half, bisector, band, corner.Floor, blocked);
                if (narrow && r > Math.Min(cap, room) + 1e-3)
                {
                    corner.RoomClipped = room < cap && r > room + 1e-3;
                    Narrow(frames, index, connectors, c, ui, uj, half, bisector, corner.Floor, Math.Min(cap, room), lowest: corner.RoomClipped || heldHere ? -1 : 0);
                    r = Envelope(c, ui, uj, half, bisector, connectors, corner.Floor);
                }
            }
            corner.Radius = r;
            if (mouthCap && r > corner.Floor + 1e-6)
                corner.Bound = connectors.MaxBy(k => Envelope(c, ui, uj, half, bisector, [k], corner.Floor));
            double leg = r / Math.Tan(half);
            corner.StationA = fi.StationOf(c) + leg;
            corner.StationB = fj.StationOf(c) + leg;
            if (!mouthCap) continue;
            // an arc whose kerb would start past both mouths (a corner near a straight line, a through lane bending past it): a
            // straight kerb between the mouths instead, pushed out where a connector passes beyond it (#854)
            if (corner.StationA > fi.Station + 1 && corner.StationB > fj.Station + 1)
            {
                corner.Shape = CornerShape.Chord;
                corner.StationA = corner.StationB = double.NaN;
                corner.Kerb.AddRange(InLineKerb(fi.Point(arms[i].Mouth.CarriagewayRight), -ui, fj.Point(arms[j].Mouth.CarriagewayLeft), uj, connectors));
                continue;
            }
            // the arc from arm A's kerb to arm B's, round the sidewalk's corner
            var ta = c + ui * leg;
            var tb = c + uj * leg;
            var o = c + bisector * (r / Math.Sin(half));
            double a0 = (ta - o).Heading, sweep = Angles.Normalize((tb - o).Heading - a0);
            int steps = Math.Max(2, (int)Math.Ceiling(r * Math.Abs(sweep) / Chord));
            for (int k = 0; k <= steps; k++) corner.Kerb.Add(o + Vec2.FromHeading(a0 + sweep * k / steps) * r);
        }
        return corners;
    }

    /// <summary>
    /// The kerb between two arms in line, from arm A's right kerb at its mouth (leaving along <paramref name="din"/>) to arm B's
    /// left kerb at its (arriving along <paramref name="dout"/>), its ends left out: an S between them, each point pushed out
    /// (to the right of the way from A to B: away from the core) as far as a connector's edge passes beyond it there.
    /// </summary>
    private static List<Vec2> InLineKerb(Vec2 from, Vec2 din, Vec2 to, Vec2 dout, List<Connector> connectors)
    {
        double l = from.DistanceTo(to) / 3;
        Vec2 c1 = from + din * l, c2 = to - dout * l;
        int steps = Math.Max(4, (int)Math.Ceiling(from.DistanceTo(to) / Chord));
        var s = new List<Vec2>(steps + 1);
        for (int k = 0; k <= steps; k++)
        {
            double t = (double)k / steps, mt = 1 - t;
            s.Add(from * (mt * mt * mt) + c1 * (3 * mt * mt * t) + c2 * (3 * mt * t * t) + to * (t * t * t));
        }
        var edges = connectors.SelectMany(c => c.Left.Concat(c.Right)).ToList();
        var kerb = new List<Vec2>(steps - 1);
        for (int k = 1; k < steps; k++)
        {
            var along = (s[k + 1] - s[k - 1]).Normalized();
            var outward = -along.Perp;
            double half = s[k + 1].DistanceTo(s[k - 1]) * 0.5, push = 0;
            foreach (var p in edges)
            {
                var d = p - s[k];
                if (Math.Abs(d.Dot(along)) <= half) push = Math.Max(push, d.Dot(outward));
            }
            // (a few centimetres, a lane edge's mitred points along the kerb, within the outline's tolerance: no push, else the
            // kerb and the bands carried along it wave, J9)
            kerb.Add(s[k] + outward * (push > OutsideTolerance ? push + 0.02 : 0));
        }
        return kerb;
    }

    /// <summary>The side bands' width beyond a section's kerb: its right side's (seen driving in) or its left side's.</summary>
    private static double Bands(Section s, bool right) =>
        right ? s.Strips.Skip(s.LastCarriageway + 1).Sum(x => x.Width) : s.Strips.Take(Math.Max(0, s.FirstCarriageway)).Sum(x => x.Width);

    /// <summary>Whether a point lies in the sidewalk's wedge of a corner: beyond both kerbs.</summary>
    private static bool InWedge(Vec2 p, Vec2 c, Vec2 ui, Vec2 uj) => ui.Cross(p - c) > 1e-6 && (p - c).Cross(uj) > 1e-6;

    /// <summary>
    /// The least kerb radius at a corner (at least <paramref name="floor"/>) whose rounding keeps every connector edge point
    /// paved: a point in the sidewalk's wedge is paved when no disc of that radius inside the wedge covers it, which only
    /// gets truer as the radius grows (the roundings are nested), so the least one is found by halving.
    /// </summary>
    private static double Envelope(Vec2 c, Vec2 ui, Vec2 uj, double half, Vec2 bisector, List<Connector> connectors, double floor)
    {
        var points = new List<Vec2>();
        foreach (var k in connectors)
        {
            foreach (var p in k.Left) if (InWedge(p, c, ui, uj)) points.Add(p);
            foreach (var p in k.Right) if (InWedge(p, c, ui, uj)) points.Add(p);
        }
        if (points.Count == 0) return floor;
        bool Paved(double r)
        {
            var apex = c + bisector * (r / Math.Sin(half));
            foreach (var p in points)
                if (DistanceToWedge(p - apex, ui, uj) <= r) return false;
            return true;
        }
        if (Paved(floor)) return floor;
        if (!Paved(MostRadius)) return MostRadius;
        double lo = floor, hi = MostRadius;
        for (int k = 0; k < 40 && hi - lo > 1e-3; k++)
        {
            double mid = (lo + hi) * 0.5;
            if (Paved(mid)) hi = mid; else lo = mid;
        }
        return hi;
    }

    /// <summary>The distance from <paramref name="v"/> (relative to a wedge's apex) to the wedge between the rays <paramref name="ui"/> and <paramref name="uj"/>.</summary>
    private static double DistanceToWedge(Vec2 v, Vec2 ui, Vec2 uj)
    {
        if (ui.Cross(v) >= 0 && v.Cross(uj) >= 0) return 0;
        double di = (v - ui * Math.Max(0, v.Dot(ui))).Length;
        double dj = (v - uj * Math.Max(0, v.Dot(uj))).Length;
        return Math.Min(di, dj);
    }

    /// <summary>The largest kerb radius whose arc, with the side bands behind it, keeps off the buildings (halving; unbounded where even the least one does not).</summary>
    private static double Room(Vec2 c, double half, Vec2 bisector, double band, double floor, Func<Vec2, bool> blocked)
    {
        bool Clear(double r)
        {
            var mid = c + bisector * (r / Math.Sin(half) - r);
            for (double t = 0; t <= band + 0.5 + 1e-6; t += 0.25)
                if (blocked(mid + bisector * t)) return false;
            return true;
        }
        if (!Clear(floor)) return MostRadius;
        // outward from the least rounding to the first radius whose arc reaches a building (a far larger one would pass it)
        double lo = floor, hi = double.NaN;
        for (double r = floor + 0.5; r <= RoomRadius; r += 0.5)
        {
            if (!Clear(r)) { hi = r; break; }
            lo = r;
        }
        if (double.IsNaN(hi)) return MostRadius;
        for (int k = 0; k < 12 && hi - lo > 0.01; k++)
        {
            double mid = (lo + hi) * 0.5;
            if (Clear(mid)) lo = mid; else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// Narrows the turns passing a corner (their fills lowered together, down to <paramref name="lowest"/>) until its envelope is
    /// at most <paramref name="cap"/>: the most found by halving.
    /// </summary>
    private static void Narrow(Frame[] frames, Dictionary<int, int> index, List<Connector> connectors, Vec2 c, Vec2 ui, Vec2 uj, double half,
        Vec2 bisector, double floor, double cap, double lowest)
    {
        var passing = connectors.Where(k => k.WideRadius > 0 && k.Left.Concat(k.Right).Any(p => InWedge(p, c, ui, uj))).ToList();
        if (passing.Count == 0) return;
        var fills = passing.ToDictionary(k => k, k => k.Fill);
        void At(List<Connector> set, double f)
        {
            foreach (var k in set)
            {
                k.Fill = Math.Min(fills[k], f);
                Lay(frames, index, k);
            }
        }
        bool Fits() => Envelope(c, ui, uj, half, bisector, connectors, floor) <= cap + 1e-3;
        // the most fill in [lo, hi] the set may keep with the corner fitting (left at lo where even that does not)
        void Search(List<Connector> set, double lo, double hi)
        {
            At(set, lo);
            if (!Fits()) return;
            for (int k = 0; k < 20 && hi - lo > 1e-3; k++)
            {
                double mid = (lo + hi) * 0.5;
                At(set, mid);
                if (Fits()) lo = mid; else hi = mid;
            }
            At(set, lo);
        }
        // the turns passing the corner narrowed together, down to their vehicles' radius
        Search(passing, 0, fills.Values.Max());
        if (Fits() || lowest >= 0) return;
        // under it (the room): only those that still reach past the cap on their own
        var still = passing.Where(k => Envelope(c, ui, uj, half, bisector, [k], floor) > cap + 1e-3).ToList();
        if (still.Count > 0) Search(still, lowest, 0);
    }

    // ------------------------------------------------------------------ lines through the core

    /// <summary>
    /// The lines painted through the core, each a connector's edge (the plan: never computed apart), chosen by the rules:
    /// the main road's centre line (between its two straight connectors) where the priority plan carries it through; its
    /// edge guides without lights (the outer connectors' right edges, dashed across a joining road); at the lights the
    /// left-turn guide (the left turn's inner edge) toward an exit island or beside a left-turn bike lane, the line between
    /// the two; same-turn pairs kept apart; the through guide past an exit island no left-turn guide leads into.
    /// </summary>
    internal static void Lines(JunctionPlan jp, List<ArmPlan> arms, Dictionary<int, int> index, JunctionCore core)
    {
        var rules = jp.Lights ? JunctionRules.Lights : JunctionRules.NoLights;
        var cars = core.Connectors.Where(c => c.Kind == ConnectorKind.Car).ToList();
        Connector? Outer(int from, int to, bool leftmost)
        {
            var between = cars.Where(c => c.FromArm == from && c.ToArm == to).OrderBy(c => c.FromOffset).ToList();
            return between.Count == 0 ? null : leftmost ? between[0] : between[^1];
        }
        if (jp.MainArms.Length == 2 && jp.CentreLineThrough)
        {
            int a = jp.MainArms[0], b = jp.MainArms[1];
            // no centre line through a core (the user, Oct 17: halfway between two offset through lanes it matched no lane); the
            // edge guides below still run on
            if (rules.Has(JunctionRule.EdgeGuides) && jp.EdgeGuides)
                foreach (var (from, to) in (ReadOnlySpan<(int, int)>)[(a, b), (b, a)])
                    if (Outer(from, to, leftmost: false) is { } c)
                    {
                        // a road joins on the right of this way when an arm lies counter-clockwise between the two
                        int i = index[from], j = index[to];
                        bool joins = (j - i + arms.Count) % arms.Count > 1;
                        core.Lines.Add(new CoreLine("edge", c.Right, Dashed: joins));
                    }
        }
        var leftInto = new HashSet<int>();
        foreach (var arm in arms)
        {
            var mine = cars.Where(c => c.FromArm == arm.Arm).ToList();
            if (rules.Has(JunctionRule.LeftTurnGuides))
            {
                var lefts = mine.Where(c => c.Move == SignalMoves.Left).OrderBy(c => c.FromOffset).ToList();
                var bike = core.Connectors.FirstOrDefault(c => c.Kind == ConnectorKind.Bike && c.FromArm == arm.Arm && c.Move == SignalMoves.Left);
                // the inner guide only where a raised island stands on the exit; the outer one only beside a bike lane turning left too,
                // as the guide between two left lanes (the user, Oct 17)
                if (lefts.Count > 0 && HasRaisedIsland(arms[index[lefts[0].ToArm]]))
                {
                    core.Lines.Add(new CoreLine("left", lefts[0].Left, Dashed: true));
                    leftInto.Add(lefts[0].ToArm);
                }
                // (only into an exit carrying the riders' way on, as a bike lane marked through the core: the user, Oct 17)
                if (lefts.Count > 0 && bike is not null && arms[index[bike.ToArm]].Normal.CarriesBikesAway)
                    core.Lines.Add(new CoreLine("left bike", Middle(lefts[^1].Right, bike.Left), Dashed: true));
            }
            foreach (var move in (ReadOnlySpan<SignalMoves>)[SignalMoves.Left, SignalMoves.Through, SignalMoves.Right])
            {
                var same = mine.Where(c => c.Move == move).OrderBy(c => c.FromOffset).ToList();
                for (int k = 0; k + 1 < same.Count; k++)
                    if (same[k].ToId != same[k + 1].ToId && same[k].ToArm == same[k + 1].ToArm)
                        core.Lines.Add(new CoreLine("pair", Middle(same[k].Right, same[k + 1].Left), Dashed: true));
            }
        }
        if (rules.Has(JunctionRule.ExitIslands))
            foreach (var arm in arms)
                if (cars.Where(c => c.FromArm == arm.Arm && c.Move == SignalMoves.Through).OrderBy(c => c.FromOffset).FirstOrDefault() is { } t
                    && HasIsland(arms[index[t.ToArm]]) && !leftInto.Contains(t.ToArm))
                    core.Lines.Add(new CoreLine("through", t.Left, Dashed: true));

        static bool HasIsland(ArmPlan exit) => exit.Mouth.Strips.Any(s => s.Kind is StripKind.Island || s.Kind == StripKind.Hatch && s.Way == StripWay.None);
        // a kerbed island, not a painted hatch (the user, Oct 17)
        static bool HasRaisedIsland(ArmPlan exit) => exit.Mouth.Strips.Any(s => s.Kind is StripKind.Island);
    }

    /// <summary>The line halfway between two lines running the same way, matched by their share of length.</summary>
    private static List<Vec2> Middle(IReadOnlyList<Vec2> a, IReadOnlyList<Vec2> b)
    {
        const int Count = 24;
        var ra = Resample(a, Count);
        var rb = Resample(b, Count);
        return ra.Select((p, k) => (p + rb[k]) * 0.5).ToList();
    }

    private static List<Vec2> Resample(IReadOnlyList<Vec2> line, int count)
    {
        var at = new double[line.Count];
        for (int k = 1; k < line.Count; k++) at[k] = at[k - 1] + line[k].DistanceTo(line[k - 1]);
        var result = new List<Vec2>(count + 1);
        int seg = 0;
        for (int k = 0; k <= count; k++)
        {
            double s = at[^1] * k / count;
            while (seg + 2 < line.Count && at[seg + 1] < s) seg++;
            double len = at[seg + 1] - at[seg];
            result.Add(len < 1e-9 ? line[seg] : Vec2.Lerp(line[seg], line[seg + 1], (s - at[seg]) / len));
        }
        return result;
    }

    private static List<Vec2> Reversed(IReadOnlyList<Vec2> line) => line.Reverse().ToList();

    // ------------------------------------------------------------------ polygons

    internal static void Dedupe(List<Vec2> ring)
    {
        for (int k = ring.Count - 1; k > 0; k--)
            if (ring[k].DistanceTo(ring[k - 1]) < 1e-4) ring.RemoveAt(k);
        if (ring.Count > 1 && ring[0].DistanceTo(ring[^1]) < 1e-4) ring.RemoveAt(ring.Count - 1);
    }

    /// <summary>
    /// The surface's heights (the plan's step 2, as today's cap): every mouth line is on its arm's height there, every other
    /// outline point blends the mouths by inverse square distance to the mouth line (a point on a mouth takes it exactly, so
    /// the core meets each arm with no step). Sets <see cref="JunctionCore.Heights"/> and the steepest triangle's grade.
    /// </summary>
    public static void Lift(JunctionCore core, Func<CoreMouth, double> mouthHeight)
    {
        var anchors = core.Mouths.Select(m => (m.Left, m.Right, Height: mouthHeight(m))).ToList();
        core.Heights.Clear();
        foreach (var p in core.Outline) core.Heights.Add(HeightAt(anchors, p));
        // the steepest a driver meets: along a connector, from its mouth's height to its exit's
        var at = core.Mouths.ToDictionary(m => m.Arm, m => mouthHeight(m));
        core.SteepestGrade = 0;
        foreach (var c in core.Connectors)
        {
            double length = 0;
            for (int k = 1; k < c.Centre.Count; k++) length += c.Centre[k].DistanceTo(c.Centre[k - 1]);
            if (length > 1 && at.TryGetValue(c.FromArm, out double h0) && at.TryGetValue(c.ToArm, out double h1))
                core.SteepestGrade = Math.Max(core.SteepestGrade, Math.Abs(h1 - h0) / length);
        }
    }

    /// <summary>A point's height blended from mouth lines (inverse square distance to each line, exact on one).</summary>
    public static double HeightAt(IReadOnlyList<(Vec2 Left, Vec2 Right, double Height)> anchors, Vec2 p)
    {
        double weights = 0, sum = 0;
        foreach (var (a, b, h) in anchors)
        {
            var ab = b - a;
            double t = ab.LengthSquared < 1e-12 ? 0 : Math.Clamp((p - a).Dot(ab) / ab.LengthSquared, 0, 1);
            double d2 = (a + ab * t).DistanceSquaredTo(p);
            if (d2 < 1e-6) return h;
            weights += 1 / d2;
            sum += h / d2;
        }
        return weights > 0 ? sum / weights : 0;
    }

    /// <summary>No two edges of the ring cross (neighbours share their end).</summary>
    public static bool Simple(IReadOnlyList<Vec2> ring)
    {
        int n = ring.Count;
        for (int i = 0; i < n; i++)
        {
            Vec2 a = ring[i], b = ring[(i + 1) % n];
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1) continue;
                Vec2 c = ring[j], d = ring[(j + 1) % n];
                double d1 = (b - a).Cross(c - a), d2 = (b - a).Cross(d - a), d3 = (d - c).Cross(a - c), d4 = (d - c).Cross(b - c);
                if ((d1 > 1e-9 && d2 < -1e-9 || d1 < -1e-9 && d2 > 1e-9) && (d3 > 1e-9 && d4 < -1e-9 || d3 < -1e-9 && d4 > 1e-9)) return false;
            }
        }
        return true;
    }

    public static double SignedArea(IReadOnlyList<Vec2> ring)
    {
        double sum = 0;
        for (int k = 0; k < ring.Count; k++) sum += ring[k].Cross(ring[(k + 1) % ring.Count]);
        return sum * 0.5;
    }

    /// <summary>How far a point lies outside the ring (0 inside).</summary>
    public static double Outside(IReadOnlyList<Vec2> ring, Vec2 p)
    {
        bool inside = false;
        double near = double.MaxValue;
        for (int k = 0, l = ring.Count - 1; k < ring.Count; l = k++)
        {
            Vec2 a = ring[k], b = ring[l];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            var ab = b - a;
            double t = ab.LengthSquared < 1e-12 ? 0 : Math.Clamp((p - a).Dot(ab) / ab.LengthSquared, 0, 1);
            near = Math.Min(near, (a + ab * t).DistanceTo(p));
        }
        return inside ? 0 : near;
    }
}
