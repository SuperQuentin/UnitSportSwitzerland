namespace UnitSport.Tools.RoadGen.Layout;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;
using UnitSport.Tools.RoadGen.Junctions;

/// <summary>
/// A merged junction's core laid member by member (#854, <c>docs/plans/merged-cores.md</c>), in place of
/// <see cref="CorePlanner"/> laying all its arms as if they met at one node:
/// <list type="number">
/// <item>each member laid alone by <see cref="CorePlanner"/>: its own arms of the merged junction, and its inner arms (the
/// streets to the other members, unheld: the two members' mouths on one may overlap);</item>
/// <item>every move of the merged junction a chain of legs, one per member it crosses: onto the inner street's lane, along it,
/// off it; each member's core is laid for its legs, the legs then spliced where they run along the inner street's lane;</item>
/// <item>the outline walked round the tree of members: an inner street's two sides join the two members' kerbs, and where the
/// two cores overlap, their kerbs meet where they cross (the loops cut away).</item>
/// </list>
/// Null where the members do not make a tree of car streets, or one has no plan of its own.
/// </summary>
public static class MergedCore
{
    /// <summary>An inner arm's id in its member's core: past every arm of the merged junction.</summary>
    private const int InnerId = 1000;
    /// <summary>A leg's point is on its inner street's lane within this of the lane's centre (m).</summary>
    private const double OnLane = 0.1;
    /// <summary>A street's side, or a lane along it, is drawn with points this far apart (m).</summary>
    private const double Step = 2;

    private sealed class Member
    {
        public required JunctionPlan Alone { get; init; }
        public List<Connector> Legs { get; } = new();
        public Dictionary<(int, int, int, int, ConnectorKind, long, long), Connector> LegOf { get; } = new();
        public JunctionCore Core { get; set; } = null!;
        /// <summary>Its arms round its core, counter-clockwise, as <see cref="Core"/>'s mouths and corners are.</summary>
        public List<int> Order { get; } = new();
        public Dictionary<int, CoreMouth> MouthOf { get; } = new();
        public Dictionary<int, List<Vec2>> KerbAfter { get; } = new();
        /// <summary>Its inner arms: the street's arm here, the member at its far end, that member's arm on it.</summary>
        public Dictionary<int, (ArmPlan Here, Member Far, ArmPlan There)> Inner { get; } = new();
    }

    /// <summary>The merged junction's core from its members' own plans (the first planning pass, by node); null where it cannot.</summary>
    public static JunctionCore? Plan(JunctionPlan jp, IReadOnlyDictionary<int, JunctionPlan> alone, Func<Vec2, bool>? blocked)
    {
        if (jp.Merged is not { Members: { } nodes, Ring: null }) return null;
        var members = new Dictionary<int, Member>();
        foreach (int node in nodes)
        {
            if (!alone.TryGetValue(node, out var p) || p.Merged is not null) return null;
            members[node] = new Member { Alone = new JunctionPlan { Node = node, Centre = p.Centre, Lights = jp.Lights } };
        }
        var byEnd = new Dictionary<(int Link, bool AtEnd), (Member M, ArmPlan A)>();
        foreach (int node in nodes)
            foreach (var a in alone[node].Arms) byEnd[(a.LinkId, a.AtEnd)] = (members[node], a);

        // its own arms of the merged junction, with the merged plan's sections (its transitions were planned from them)
        var arms = jp.Arms.Where(a => a.Axis is not null).OrderBy(a => Angles.Normalize(a.RingHeading)).ToList();
        var ownerOf = new Dictionary<int, Member>();
        foreach (var a in arms)
        {
            if (!byEnd.TryGetValue((a.LinkId, a.AtEnd), out var at)) return null;
            ownerOf[a.Arm] = at.M;
            at.M.Alone.Arms.Add(Copy(a, a.Arm, a.Mouth, a.Reach));
        }
        // the streets between members, each a tree's edge, on their normal section both ends (one carriageway seen both ways)
        var links = arms.Select(a => a.LinkId).ToHashSet();
        var seen = new HashSet<int>();
        int edges = 0;
        foreach (int node in nodes)
            foreach (var a in alone[node].Arms)
            {
                if (links.Contains(a.LinkId) || !seen.Add(a.LinkId)) continue;
                if (a.Axis is null || !byEnd.TryGetValue((a.LinkId, !a.AtEnd), out var far) || far.M == members[node] || far.A.Axis is null) return null;
                var here = Copy(a, InnerId + 2 * edges, a.Normal, double.MaxValue);
                var there = Copy(far.A, InnerId + 2 * edges + 1, far.A.Normal, double.MaxValue);
                members[node].Alone.Arms.Add(here);
                far.M.Alone.Arms.Add(there);
                members[node].Inner[here.Arm] = (here, far.M, there);
                far.M.Inner[there.Arm] = (there, members[node], here);
                edges++;
            }
        if (edges != members.Count - 1 || members.Values.Any(m => m.Alone.Arms.Count < 2)) return null;

        // every move of the merged junction, as one node's, a chain of legs through the members
        var index = new Dictionary<int, int>();
        for (int k = 0; k < arms.Count; k++) index[arms[k].Arm] = k;
        var chains = new List<(Connector Move, List<Connector> Legs, List<(ArmPlan Here, ArmPlan There, double Offset)> Streets)>();
        foreach (var c in CorePlanner.Connectors(arms, index, (_, _) => false))
        {
            var from = ownerOf[c.FromArm];
            if (Route(from, ownerOf[c.ToArm]) is not { } route) return null;
            var legs = new List<Connector>();
            var streets = new List<(ArmPlan, ArmPlan, double)>();
            var at = from;
            ArmPlan arm = jp.Arms.First(a => a.Arm == c.FromArm);
            (int Id, double Offset, double Width) lane = (c.FromId, c.FromOffset, c.Width);
            bool ok = true;
            foreach (var (here, next, there) in route)
            {
                // onto the inner street: the kerb lane turning right, else the lane by its centre line
                bool right = Angles.Normalize(here.Heading - arm.Heading - Math.PI) < 0;
                if (OutLane(here.Mouth, c.Kind, right, c.Width) is not { } onto) { ok = false; break; }
                legs.Add(Leg(at, c, arm.Arm, lane, here.Arm, onto));
                streets.Add((here, there, onto.Offset));
                // the same lane arriving at the far member (its offset seen from there)
                at = next;
                arm = there;
                lane = (InLaneId(there.Mouth, c.Kind, -onto.Offset), -onto.Offset, onto.Width);
            }
            if (!ok) continue;
            legs.Add(Leg(at, c, arm.Arm, lane, c.ToArm, (c.ToId, c.ToOffset, c.ToWidth)));
            chains.Add((c, legs, streets));
        }

        // each member laid alone for its legs
        foreach (var m in members.Values)
        {
            m.Core = CorePlanner.Plan(m.Alone, blocked, trucks: false, given: () => m.Legs);
            for (int k = 0; k < m.Core.Mouths.Count; k++)
            {
                var mouth = m.Core.Mouths[k];
                m.Order.Add(mouth.Arm);
                m.MouthOf[mouth.Arm] = mouth;
                m.KerbAfter[mouth.Arm] = m.Core.Corners.FirstOrDefault(x => x.ArmA == mouth.Arm)?.Kerb ?? [];
            }
            if (m.Order.Count != m.Alone.Arms.Count) return null;
        }

        var core = new JunctionCore();
        // the moves, their legs spliced along the inner streets
        foreach (var (c, legs, streets) in chains)
        {
            var centre = legs[0].Centre.ToList();
            for (int k = 1; k < legs.Count; k++)
            {
                var (here, _, offset) = streets[k - 1];
                Splice(centre, legs[k].Centre, here.Axis!, offset);
            }
            // a through move across the members: one S from mouth to mouth, as on one node (its legs would narrow onto the inner
            // street's lanes and widen again)
            if (legs.Count > 1 && centre.Count > 2 && Angle(centre[1] - centre[0], centre[^1] - centre[^2]) < CorePlanner.TurnAngle) Smooth(centre);
            // legs that spliced away to nothing (#1279: a Bern block threw in Set): the junction is planned whole instead
            if (centre.Count < 2) return null;
            var move = new Connector
            {
                FromArm = c.FromArm, FromId = c.FromId, ToArm = c.ToArm, ToId = c.ToId, Move = c.Move, Kind = c.Kind, Width = c.Width, ToWidth = c.ToWidth,
                FromOffset = c.FromOffset, ToOffset = c.ToOffset, MinRadius = legs.Min(l => l.MinRadius), WideRadius = legs.Max(l => l.WideRadius),
                Radius = legs.Max(l => l.Radius), UnderRadius = legs.Any(l => l.UnderRadius), Fill = legs.Min(l => l.Fill), Turns = legs.Any(l => l.Turns),
                HoldIn = legs[0].HoldIn, HoldOut = legs[^1].HoldOut,
            };
            CorePlanner.Set(move, centre, 0, 0);
            core.Connectors.Add(move);
        }
        var paved = core.Connectors.SelectMany(c => c.Left.Concat(c.Right)).ToList();

        // the outline round the tree, from the first arm of the merged junction
        var root = ownerOf[arms[0].Arm];
        var outline = new List<Vec2>();
        int k0 = root.Order.IndexOf(arms[0].Arm);
        for (int d = 0; d < root.Order.Count; d++)
        {
            int id = root.Order[(k0 + d) % root.Order.Count];
            Arm(outline, root, id, paved);
            outline.AddRange(root.KerbAfter[id]);
        }
        // (the mouths' ends kept through every clean-up: a member's kerb ending on one, its duplicate goes)
        var mouthEnds = arms.Select(a => ownerOf[a.Arm].MouthOf[a.Arm]).SelectMany(m => new[] { m.Left, m.Right }).ToHashSet();
        Dedupe(outline, mouthEnds);
        Clean(outline, mouthEnds);
        // every kerb keeps every move paved (a move spliced or smoothed across members runs where no member's leg did)
        var before = outline.ToList();
        Envelope(outline, paved, arms.Select(a => ownerOf[a.Arm].MouthOf[a.Arm]).ToList());
        Clean(outline, mouthEnds);
        // (a push that cut a mouth away is no core: the outline as walked, the check failing)
        if (!mouthEnds.All(outline.Contains))
        {
            outline.Clear();
            outline.AddRange(before);
        }
        core.Outline.AddRange(outline);

        // the mouths and corners of its own arms, in the order the outline meets them
        var mouths = arms.Select(a => ownerOf[a.Arm].MouthOf[a.Arm]).ToDictionary(m => m.Arm);
        var found = mouths.Values.Select(m => (m, i: outline.IndexOf(m.Left), j: outline.IndexOf(m.Right))).ToList();
        // (a mouth cut away by another member's kerb: the outline is no core)
        if (found.Any(x => x.i < 0 || x.j < 0)) core.Simple = false;
        var round = found.Where(x => x.i >= 0 && x.j >= 0).OrderBy(x => x.i).ToList();
        foreach (var (m, _, _) in round) core.Mouths.Add(m);
        for (int k = 0; k < round.Count; k++)
        {
            var (ma, _, ja) = round[k];
            var (mb, ib, _) = round[(k + 1) % round.Count];
            var kerb = new List<Vec2>();
            for (int p = (ja + 1) % outline.Count; p != ib; p = (p + 1) % outline.Count) kerb.Add(outline[p]);
            // two arms of one member next to each other: that member's corner, else the kerb round the members between
            var own = ownerOf[ma.Arm] == ownerOf[mb.Arm] ? ownerOf[ma.Arm].Core.Corners.FirstOrDefault(x => x.ArmA == ma.Arm && x.ArmB == mb.Arm) : null;
            var corner = new CoreCorner { ArmA = ma.Arm, ArmB = mb.Arm, Shape = own?.Shape ?? CornerShape.Chord, Angle = own?.Angle ?? 0, Meet = own?.Meet ?? default,
                Radius = own?.Radius ?? 0, Floor = own?.Floor ?? 0, Bound = own?.Bound, RoomClipped = own?.RoomClipped ?? false,
                StationA = own?.StationA ?? double.NaN, StationB = own?.StationB ?? double.NaN };
            corner.Kerb.AddRange(kerb);
            core.Corners.Add(corner);
        }


        // the checks and numbers, on its own arms
        foreach (var m in members.Values)
            foreach (var x in m.Core.Corners.Where(x => x.Shape == CornerShape.Arc))
                foreach (var (arm, station) in (ReadOnlySpan<(int, double)>)[(x.ArmA, x.StationA), (x.ArmB, x.StationB)])
                    if (!m.Inner.ContainsKey(arm) && station > m.MouthOf[arm].Station + 0.02) core.KerbsNotTangent++;
        foreach (var a in arms)
        {
            var m = ownerOf[a.Arm];
            if (m.Core.Needs.TryGetValue(a.Arm, out double need)) core.Needs[a.Arm] = need;
            if (m.Core.ArmsAtReach.Contains(a.Arm)) core.ArmsAtReach.Add(a.Arm);
        }
        core.MouthsAtReach = core.ArmsAtReach.Count;
        core.MouthsAtMost = core.Mouths.Count(m => m.Station >= CorePlanner.MostMouth - 0.02);
        core.Area = Math.Abs(CorePlanner.SignedArea(core.Outline));
        core.Simple &= CorePlanner.Simple(core.Outline);
        core.Triangles.AddRange(EarClip.Triangulate(core.Outline));
        foreach (var c in core.Connectors)
        {
            c.Outside = c.Left.Concat(c.Right).Select(p => CorePlanner.Outside(core.Outline, p)).DefaultIfEmpty(0).Max();
            if (c.Outside > CorePlanner.OutsideTolerance) core.ConnectorsOutside++;
        }
        CorePlanner.Lines(jp, arms, index, core);
        core.ByMembers = true;
        return core;

        // the route from one member to another through the tree: each inner street's arm here, the member past it, its arm there
        List<(ArmPlan Here, Member Next, ArmPlan There)>? Route(Member from, Member to)
        {
            var back = new Dictionary<Member, (Member Prev, ArmPlan Here, ArmPlan There)> { [from] = default };
            var todo = new Queue<Member>([from]);
            while (todo.TryDequeue(out var m) && m != to)
                foreach (var (here, far, there) in m.Inner.Values)
                    if (back.TryAdd(far, (m, here, there))) todo.Enqueue(far);
            if (!back.ContainsKey(to)) return null;
            var route = new List<(ArmPlan, Member, ArmPlan)>();
            for (var m = to; m != from; m = back[m].Prev) route.Add((back[m].Here, m, back[m].There));
            route.Reverse();
            return route;
        }
    }

    /// <summary>One member's leg of a move, shared by every move making it (one lane to one lane).</summary>
    private static Connector Leg(Member m, Connector move, int fromArm, (int Id, double Offset, double Width) from, int toArm, (int Id, double Offset, double Width) to)
    {
        var key = (fromArm, from.Id, toArm, to.Id, move.Kind, (long)Math.Round(from.Offset * 100), (long)Math.Round(to.Offset * 100));
        if (m.LegOf.TryGetValue(key, out var leg)) return leg;
        leg = new Connector
        {
            FromArm = fromArm, FromId = from.Id, ToArm = toArm, ToId = to.Id, Move = move.Move, Kind = move.Kind, Width = from.Width, ToWidth = to.Width,
            FromOffset = from.Offset, ToOffset = to.Offset,
            MinRadius = move.Kind == ConnectorKind.Car ? CorePlanner.CarInner + (from.Width + to.Width) * 0.25 : Math.Max(1, CorePlanner.CarInner - from.Width) + from.Width * 0.5,
        };
        m.LegOf[key] = leg;
        m.Legs.Add(leg);
        return leg;
    }

    /// <summary>The lane a move takes leaving along an inner street: a car's kerb lane turning right, else the one by the centre line; a rider's bike lane, else the kerb side of the kerb lane.</summary>
    private static (int Id, double Offset, double Width)? OutLane(Section s, ConnectorKind kind, bool right, double width)
    {
        var outs = s.CarLanes(StripWay.Out).Reverse().ToList();
        if (outs.Count == 0) return null;
        if (kind == ConnectorKind.Car)
        {
            var o = right ? outs[^1] : outs[0];
            return (o.Strip.Id, Mid(s.Span(o.Index)), o.Strip.Width);
        }
        int bike = Enumerable.Range(0, s.Strips.Count).FirstOrDefault(j => s.Strips[j].Kind == StripKind.BikeLane && s.Strips[j].Way == StripWay.Out, -1);
        return bike >= 0 ? (s.Strips[bike].Id, Mid(s.Span(bike)), s.Strips[bike].Width) : (outs[^1].Strip.Id, s.Span(outs[^1].Index).From + width * 0.5, width);
    }

    /// <summary>The arriving lane nearest an offset (its strip id; 0 where the street brings none).</summary>
    private static int InLaneId(Section s, ConnectorKind kind, double offset)
    {
        var lanes = Enumerable.Range(0, s.Strips.Count)
            .Where(j => s.Strips[j].Way == StripWay.In && (s.Strips[j].Kind == StripKind.CarLane || kind == ConnectorKind.Bike && s.Strips[j].Kind == StripKind.BikeLane)).ToList();
        return lanes.Count == 0 ? 0 : s.Strips[lanes.MinBy(j => Math.Abs(Mid(s.Span(j)) - offset))].Id;
    }

    private static double Mid((double From, double To) span) => (span.From + span.To) * 0.5;

    /// <summary>An arm of a member for its own core: the plan's sections and axis, no moves of its own (its legs are given).</summary>
    private static ArmPlan Copy(ArmPlan a, int id, Section mouth, double reach) => new()
    {
        Arm = id, LinkId = a.LinkId, AtEnd = a.AtEnd, Heading = a.Heading, Normal = a.Normal, Wants = a.Wants, Wanted = mouth, Mouth = mouth, Base = mouth,
        Axis = a.Axis, Reach = reach, Major = a.Major, Room = a.Room, Length = a.Length, StreetLength = a.StreetLength,
    };

    /// <summary>
    /// Arm <paramref name="id"/> of member <paramref name="m"/> into the outline: its mouth; an inner arm's two sides walked to
    /// the far member and round it, its kerbs and arms, back.
    /// </summary>
    private static void Arm(List<Vec2> outline, Member m, int id, List<Vec2> edges)
    {
        var mouth = m.MouthOf[id];
        outline.Add(mouth.Left);
        if (m.Inner.TryGetValue(id, out var inner))
        {
            var (here, far, there) = inner;
            var end = far.MouthOf[there.Arm];
            Side(outline, here.Axis!, mouth.Station, here.Mouth.CarriagewayLeft, here.Axis!.Length - end.Station, -there.Mouth.CarriagewayRight, back: false, edges);
            // round the far member, from its mouth's right to its left
            outline.Add(end.Right);
            outline.AddRange(far.KerbAfter[there.Arm]);
            int k0 = far.Order.IndexOf(there.Arm);
            for (int d = 1; d < far.Order.Count; d++)
            {
                int next = far.Order[(k0 + d) % far.Order.Count];
                Arm(outline, far, next, edges);
                outline.AddRange(far.KerbAfter[next]);
            }
            outline.Add(end.Left);
            Side(outline, here.Axis!, mouth.Station, here.Mouth.CarriagewayRight, here.Axis!.Length - end.Station, -there.Mouth.CarriagewayLeft, back: true, edges);
        }
        outline.Add(mouth.Right);
    }

    /// <summary>
    /// An inner street's side between its two mouths (stations and offsets on the near member's axis), ends left out: out from
    /// the near mouth, or <paramref name="back"/> to it, each point pushed out as far as a connector's edge passes beyond it there
    /// (the envelope rule, as <see cref="CorePlanner"/>'s kerb between arms in line). Nothing where the two mouths overlap (the
    /// kerbs meet where they cross).
    /// </summary>
    private static void Side(List<Vec2> outline, ArmAxis axis, double near, double nearOffset, double far, double farOffset, bool back, List<Vec2> edges)
    {
        if (far - near < 0.5) return;
        int steps = Math.Max(2, (int)Math.Ceiling((far - near) / Step));
        // outward: away from the street's axis on this side
        double sign = Math.Sign(nearOffset + farOffset);
        for (int k = 1; k < steps; k++)
        {
            double t = (double)(back ? steps - k : k) / steps, s = near + (far - near) * t;
            var p = axis.At(s, nearOffset + (farOffset - nearOffset) * t);
            var outward = axis.RightAt(s) * sign;
            double half = (far - near) / steps * 0.5, push = 0;
            foreach (var q in edges)
            {
                var d = q - p;
                if (Math.Abs(d.Dot(axis.DirAt(s))) <= half) push = Math.Max(push, d.Dot(outward));
            }
            outline.Add(p + outward * (push > CorePlanner.OutsideTolerance ? push + 0.02 : 0));
        }
    }

    /// <summary>
    /// The outline pushed out wherever a connector's edge passes beyond it (<see cref="CorePlanner"/>'s envelope rule, as its kerb
    /// between arms in line): drawn with points at most <see cref="EnvelopeStep"/> apart (the mouths kept straight), each but the
    /// mouths' ends moved out, away from the core, as far as an edge point outside the outline lies beyond it there.
    /// </summary>
    internal static void Envelope(List<Vec2> ring, List<Vec2> paved, IReadOnlyList<CoreMouth> mouths, double most = MostPush, double nearMouth = NearMouth)
    {
        var mouthEnds = mouths.SelectMany(m => new[] { m.Left, m.Right }).ToHashSet();
        bool MouthLine(Vec2 a, Vec2 b) => mouths.Any(m => m.Left == a && m.Right == b || m.Left == b && m.Right == a);
        var outside = paved.Where(q => CorePlanner.Outside(ring, q) > CorePlanner.OutsideTolerance).ToList();
        if (outside.Count == 0) return;
        var dense = new List<Vec2>();
        for (int k = 0; k < ring.Count; k++)
        {
            Vec2 a = ring[k], b = ring[(k + 1) % ring.Count];
            dense.Add(a);
            if (MouthLine(a, b)) continue;
            int steps = (int)Math.Ceiling(a.DistanceTo(b) / EnvelopeStep);
            for (int j = 1; j < steps; j++) dense.Add(Vec2.Lerp(a, b, (double)j / steps));
        }
        var moved = new List<Vec2>(dense.Count);
        for (int k = 0; k < dense.Count; k++)
        {
            var p = dense[k];
            // (a mouth's ends and its kerbs' first metres stay: the kerb leaves the mouth along its arm)
            if (mouthEnds.Any(m => m.DistanceTo(p) < nearMouth)) { moved.Add(p); continue; }
            var along = (dense[(k + 1) % dense.Count] - dense[(k - 1 + dense.Count) % dense.Count]).Normalized();
            var outward = -along.Perp;
            double push = 0;
            foreach (var q in outside)
            {
                var d = q - p;
                double o = d.Dot(outward);
                if (Math.Abs(d.Dot(along)) <= EnvelopeStep * 0.75 && o > 0 && o <= most) push = Math.Max(push, o);
            }
            moved.Add(push > 0 ? p + outward * (push + 0.05) : p);
        }
        ring.Clear();
        ring.AddRange(moved);
        Dedupe(ring, mouthEnds);
    }

    /// <summary>The outline's points apart where it is pushed out (m), and the farthest it is pushed (m).</summary>
    private const double EnvelopeStep = 0.75, MostPush = 5, NearMouth = 0.5;

    /// <summary>
    /// The outline made simple: points where it turns back on itself dropped, and every loop where it crosses itself cut away (the
    /// part of the larger counter-clockwise area kept: where two members' kerbs overlap, they meet where they cross).
    /// </summary>
    public static void Clean(List<Vec2> ring, IReadOnlySet<Vec2>? keep = null)
    {
        for (int round = 0; round < 200; round++)
        {
            Spikes(ring, keep);
            if (FirstCrossing(ring) is not { } x) return;
            var (i, j, at) = x;
            var loop = ring.Skip(i + 1).Take(j - i).Append(at).ToList();
            var rest = ring.Take(i + 1).Append(at).Concat(ring.Skip(j + 1)).ToList();
            // (the part holding more of the points kept, the mouths' ends; else the larger counter-clockwise one)
            int inLoop = keep is null ? 0 : loop.Count(keep.Contains), inRest = keep is null ? 0 : rest.Count(keep.Contains);
            bool takeLoop = inLoop != inRest ? inLoop > inRest : CorePlanner.SignedArea(loop) > CorePlanner.SignedArea(rest);
            ring.Clear();
            ring.AddRange(takeLoop ? loop : rest);
            Dedupe(ring, keep);
        }
    }

    /// <summary>Points within a tenth of a millimetre of the one before dropped, never a kept one (the other of the two goes).</summary>
    private static void Dedupe(List<Vec2> ring, IReadOnlySet<Vec2>? keep)
    {
        for (int k = ring.Count - 1; k > 0; k--)
            if (ring[k].DistanceTo(ring[k - 1]) < 1e-4) ring.RemoveAt(keep?.Contains(ring[k]) == true ? k - 1 : k);
        if (ring.Count > 1 && ring[0].DistanceTo(ring[^1]) < 1e-4) ring.RemoveAt(keep?.Contains(ring[^1]) == true ? 0 : ring.Count - 1);
    }

    /// <summary>Drops the points where the ring turns back along itself (an inner street's side running back over a kerb).</summary>
    private static void Spikes(List<Vec2> ring, IReadOnlySet<Vec2>? keep = null)
    {
        for (bool again = true; again && ring.Count > 3;)
        {
            again = false;
            for (int k = 0; k < ring.Count && ring.Count > 3; k++)
            {
                Vec2 a = ring[(k - 1 + ring.Count) % ring.Count], b = ring[k], c = ring[(k + 1) % ring.Count];
                Vec2 u = b - a, v = c - b;
                if (keep?.Contains(b) == true) continue;
                if (u.Length < 1e-4 || v.Length < 1e-4 || Math.Abs(u.Cross(v)) < 1e-3 * u.Length * v.Length && u.Dot(v) < 0)
                {
                    ring.RemoveAt(k);
                    again = true;
                    k--;
                }
            }
        }
    }

    /// <summary>The first two edges of the ring that cross (not neighbours), and where.</summary>
    private static (int I, int J, Vec2 At)? FirstCrossing(List<Vec2> ring)
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
                if ((d1 > 1e-9 && d2 < -1e-9 || d1 < -1e-9 && d2 > 1e-9) && (d3 > 1e-9 && d4 < -1e-9 || d3 < -1e-9 && d4 > 1e-9))
                    return (i, j, c + (d - c) * (d1 / (d1 - d2)));
            }
        }
        return null;
    }

    /// <summary>A line turning by more than this at one of its points is kinked (a splice of two legs across a short street).</summary>
    private static readonly double KinkAngle = 15 * Math.PI / 180;

    private static double Angle(Vec2 u, Vec2 v) => Math.Acos(Math.Clamp(u.Dot(v) / (u.Length * v.Length), -1, 1));

    private static bool Kinked(List<Vec2> line)
    {
        for (int k = 1; k + 1 < line.Count; k++)
        {
            Vec2 u = line[k] - line[k - 1], v = line[k + 1] - line[k];
            if (u.Length > 1e-6 && v.Length > 1e-6 && Math.Acos(Math.Clamp(u.Dot(v) / (u.Length * v.Length), -1, 1)) > KinkAngle) return true;
        }
        return false;
    }

    /// <summary>
    /// A line laid again as one cubic from its first point to its last, leaving and arriving along its end segments (a
    /// circle's handles for the turn between them): a move across a street too short for its legs' curves, as one node's.
    /// </summary>
    private static void Smooth(List<Vec2> line)
    {
        Vec2 p = line[0], q = line[^1];
        var din = (line[1] - line[0]).Normalized();
        var dout = (line[^1] - line[^2]).Normalized();
        double turn = Math.Min(Math.Acos(Math.Clamp(din.Dot(dout), -1, 1)), 170 * Math.PI / 180);
        double handle = p.DistanceTo(q) * (turn < 1e-6 ? 1.0 / 3 : 2.0 / 3 * Math.Tan(turn / 4) / Math.Sin(turn / 2));
        Vec2 c1 = p + din * handle, c2 = q - dout * handle;
        int steps = Math.Max(8, (int)Math.Ceiling(p.DistanceTo(q) / 0.5));
        line.Clear();
        for (int k = 0; k <= steps; k++)
        {
            double t = (double)k / steps, mt = 1 - t;
            line.Add(p * (mt * mt * mt) + c1 * (3 * mt * mt * t) + c2 * (3 * mt * t * t) + q * (t * t * t));
        }
    }

    /// <summary>A line and the next joined where they cross (the first crossing along the line), else where they come closest.</summary>
    private static void Join(List<Vec2> line, List<Vec2> next)
    {
        for (int i = 0; i + 1 < line.Count; i++)
            for (int j = 0; j + 1 < next.Count; j++)
            {
                Vec2 a = line[i], b = line[i + 1], c = next[j], d = next[j + 1];
                double d1 = (b - a).Cross(c - a), d2 = (b - a).Cross(d - a), d3 = (d - c).Cross(a - c), d4 = (d - c).Cross(b - c);
                if ((d1 > 1e-9) == (d2 > 1e-9) || (d3 > 1e-9) == (d4 > 1e-9) || Math.Abs(d1 - d2) < 1e-12) continue;
                var x = c + (d - c) * (d1 / (d1 - d2));
                line.RemoveRange(i + 1, line.Count - i - 1);
                line.Add(x);
                line.AddRange(next.Skip(j + 1));
                CorePlanner.Dedupe(line);
                return;
            }
        double best = double.MaxValue;
        int bi = line.Count - 1, bj = 0;
        // (the move's last point kept whatever comes closest)
        for (int i = 0; i < line.Count; i++)
            for (int j = 0; j + 1 < next.Count; j++)
                if (line[i].DistanceSquaredTo(next[j]) < best) (best, bi, bj) = (line[i].DistanceSquaredTo(next[j]), i, j);
        line.RemoveRange(bi + 1, line.Count - bi - 1);
        line.AddRange(next.Skip(bj + 1));
        CorePlanner.Dedupe(line);
    }

    /// <summary>
    /// A move's next leg spliced on where the two run along the inner street's lane (offset <paramref name="offset"/> on
    /// <paramref name="axis"/>, the near member's): with the lane drawn between where the two members' mouths leave a gap, cut
    /// halfway along where both are on it where they overlap.
    /// </summary>
    private static void Splice(List<Vec2> centre, List<Vec2> next, ArmAxis axis, double offset)
    {
        // (run on past the street's far end: a leg going on straight into the far member's arm)
        double Station(Vec2 p) => axis.StationPast(p);
        bool On(Vec2 p) { double s = Station(p); return Math.Abs((p - axis.PointAt(s)).Dot(axis.RightAt(s)) - offset) < OnLane; }
        int ia = centre.Count - 1;
        while (ia > 0 && On(centre[ia - 1])) ia--;
        int ib = 0;
        while (ib + 1 < next.Count && On(next[ib + 1])) ib++;
        double endA = Station(centre[^1]), startB = Station(next[0]);
        if (endA <= startB)
        {
            int steps = (int)Math.Floor((startB - endA) / Step);
            for (int k = 1; k < steps; k++) centre.Add(axis.At(endA + (startB - endA) * k / steps, offset));
            centre.AddRange(next);
            CorePlanner.Dedupe(centre);
            return;
        }
        double lo = Math.Max(Station(centre[ia]), startB), hi = Math.Min(endA, Station(next[ib]));
        if (lo > hi)
        {
            // the two legs' curves overlap along the street (a street shorter than both): joined where they cross, else where
            // they come closest
            Join(centre, next);
            if (Kinked(centre)) Smooth(centre);
            return;
        }
        double t = (lo + hi) * 0.5;
        int keep = centre.Count;
        for (int k = Math.Max(ia, 1); k < centre.Count; k++)
            if (Station(centre[k]) >= t) { keep = k; break; }
        centre.RemoveRange(keep, centre.Count - keep);
        centre.Add(axis.At(t, offset));
        int from = 0;
        for (int k = Math.Min(ib, next.Count - 2); k >= 0; k--)
            if (Station(next[k]) <= t) { from = k + 1; break; }
        centre.AddRange(next.Skip(from));
        CorePlanner.Dedupe(centre);
    }
}
