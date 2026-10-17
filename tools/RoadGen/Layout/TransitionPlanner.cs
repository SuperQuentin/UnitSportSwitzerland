namespace UnitSport.Tools.RoadGen.Layout;

using UnitSport.Terrain.Format;
using System.Globalization;

/// <summary>
/// Lays each arm's transition (#818, step 4 of <c>docs/plans/junction-layout.md</c>): from its mouth section to the street's
/// normal one, with one timing rule by role (the user, Oct 13 2026): a pocket lane held over its storage, then closing over its
/// taper (on the centre side it gives way to a lead-in hatch, the closing line the diagonal where the two meet; on the kerb side
/// the kerb moves in, the centre line moving with a split lead-in's share); an exit hatch closing over <see cref="ExitTaper"/>; an
/// added bike lane held <see cref="AddedBikeHold"/> m and going at 1:6; every other width change at 1:6 just beyond its side's last
/// opening. Where the street between two planned mouths is too short for both ends' transitions (or a pocket merged with the exit
/// beyond, #325), one transition holds both: every strip sums both ends' changes, the median between the two directions (an exit
/// hatch, a lead-in hatch) is the wider of what each end wants there. Pure.
/// </summary>
public static class TransitionPlanner
{
    /// <summary>An exit hatch closes over this (#123's exit taper), m.</summary>
    public const double ExitTaper = 30;
    /// <summary>A through bike lane the mouth adds is held this far (the lights' solid centre line, #711), m.</summary>
    public const double AddedBikeHold = 20;
    /// <summary>Any other width change: metres along per metre across (1:6, #120's lead-in), and its least length.</summary>
    public const double EasePerMetre = 6, EaseMin = 6;
    /// <summary>A lead-in hatch's angled closing line runs this far along the road, at most half the taper (#700).</summary>
    public const double ClosingSlant = 10;
    /// <summary>A pocket merged with the exit beyond (#325) opens out of its hatch over this diagonal.</summary>
    public const double MergedEntry = 15;
    /// <summary>The lead-in hatch between the centre line and the through lanes, on the arriving side (a centre-side pocket's taper).</summary>
    public const int LeadInHatchId = 131;
    /// <summary>A far end's strips the planner added get their id plus this in the near end's frame.</summary>
    public const int FarIdOffset = 1000;

    /// <summary>One end of a transition in its own arm's frame: each strip's width and the centre line along it.</summary>
    private sealed class End
    {
        public required ArmPlan Arm;
        public required List<Strip> Order;
        public required Dictionary<int, Profile> Widths;
        public required HashSet<int> Right;
        public required Profile Centre;
        public List<string> Events = new();
        public double Length;
    }

    /// <summary>The width of the strip <paramref name="id"/> in <paramref name="s"/>, 0 where it has none.</summary>
    private static double WidthOf(Section s, int id)
    {
        foreach (var x in s.Strips) if (x.Id == id) return x.Width;
        return 0;
    }

    /// <summary>The strips of <paramref name="other"/> that <paramref name="list"/> lacks, each put after its left neighbour there.</summary>
    private static void MergeInto(List<Strip> list, IEnumerable<Strip> other)
    {
        int after = -1;
        foreach (var x in other)
        {
            int at = list.FindIndex(y => y.Id == x.Id);
            if (at >= 0) { after = at; continue; }
            list.Insert(++after, x);
        }
    }

    /// <summary>
    /// The transitions of every planned arm: each arm's own, or one shared with the arm at its street's far end
    /// where both do not fit apart (<see cref="ArmPlan.FarArm"/>, the street between <see cref="ArmPlan.TransitionRoom"/> long). Sets
    /// <see cref="ArmPlan.Transition"/> (the same object on both arms of a shared one).
    /// </summary>
    public static void PlanAll(IEnumerable<JunctionPlan> plans)
    {
        var arms = plans.SelectMany(p => p.Arms).ToList();
        foreach (var a in arms) a.Transition = null;
        var done = new HashSet<ArmPlan>();
        foreach (var a in arms)
        {
            if (!done.Add(a)) continue;
            var b = a.FarArm;
            var ea = EndOf(a, merged: a.MergedWith is not null && a.MergedWith == b);
            if (b is not null && !done.Contains(b))
            {
                var eb = EndOf(b, merged: b.MergedWith is not null && b.MergedWith == a);
                double d = Math.Max(1, a.TransitionRoom);
                bool together = a.MergedWith == b || b.MergedWith == a || ea.Length + eb.Length > d;
                if (together && Joined(ea, eb, d) is { } t)
                {
                    done.Add(b);
                    a.Transition = b.Transition = t;
                    continue;
                }
                // apart: each takes at most its share of the street
                double share = ea.Length + eb.Length > d ? d * ea.Length / (ea.Length + eb.Length) : double.MaxValue;
                a.Transition = Single(ea, share);
                b.Transition = Single(eb, ea.Length + eb.Length > d ? d - share : double.MaxValue);
                done.Add(b);
                continue;
            }
            a.Transition = Single(ea, b is null ? a.TransitionRoom : double.MaxValue);
        }
    }

    /// <summary>Which strips of a section lie right of its centre line: from the first arriving one (none arriving: past the last carriageway strip).</summary>
    private static int AnchorOf(Section s)
    {
        for (int k = 0; k < s.Strips.Count; k++)
            if (s.Strips[k].Way == StripWay.In && s.Strips[k].InCarriageway) return k;
        return s.LastCarriageway >= 0 ? s.LastCarriageway + 1 : s.Strips.Count;
    }

    private static End EndOf(ArmPlan a, bool merged)
    {
        var mouth = a.Mouth;
        var normal = a.Normal;
        var widths = new Dictionary<int, Profile>();
        // the strips left to right, each side of the centre line apart: the mouth's, a lead-in hatch right of the centre line where
        // a centre-side pocket opens, and the normal's the mouth lacks, each after its left neighbour
        int anchor = AnchorOf(mouth), nAnchor = AnchorOf(normal);
        var left = mouth.Strips.Take(anchor).ToList();
        var rightSide = mouth.Strips.Skip(anchor).ToList();
        if (mouth.Strips.Any(x => PocketRules.Of(x.Id) is { CentreSide: true }))
            rightSide.Insert(0, new Strip(StripKind.Hatch, 0, StripWay.None, Id: LeadInHatchId));
        MergeInto(left, normal.Strips.Take(nAnchor));
        MergeInto(rightSide, normal.Strips.Skip(nAnchor));
        var order = left.Concat(rightSide).ToList();
        var right = rightSide.Select(x => x.Id).ToHashSet();
        static double W(Section s, int id) => WidthOf(s, id);
        foreach (var x in order) widths[x.Id] = new Profile(W(mouth, x.Id));

        var events = new List<string>();
        var inv = CultureInfo.InvariantCulture;
        double eIn = 0, eOut = 0;
        double cM = mouth.CentreLine ?? 0, cN = normal.CentreLine ?? cM;
        var centre = new Profile(cM);
        bool centreDone = false;
        foreach (var rule in PocketRules.All)
        {
            var lanes = a.Openings.Where(o => rule.Owns(o.Id)).ToList();
            if (lanes.Count == 0) continue;
            double storage = lanes.Max(o => o.Storage), taper = lanes.Max(o => o.Taper) * lanes.Count;
            int bike = rule.CentreSide ? MouthPlanner.LeftBikeId : MouthPlanner.RightBikeId;
            var ids = mouth.Strips.Where(x => rule.Owns(x.Id) || x.Id == bike).Select(x => x.Id).ToList();
            double width = ids.Sum(id => W(mouth, id));
            if (rule.CentreSide)
            {
                // the pocket gives way to a lead-in hatch along the angled closing line, the hatch closing over the taper; merged
                // with the exit beyond (#325), the hatch runs on to the far mouth (its median)
                double slant = merged ? MergedEntry : Math.Min(ClosingSlant, taper * 0.5);
                foreach (int id in ids) widths[id].HoldThen(storage, storage + slant, 0);
                var hatch = widths[LeadInHatchId];
                if (merged) hatch.HoldThen(storage, storage + slant, width);
                else hatch.HoldThen(storage, storage + slant, width * (1 - slant / Math.Max(slant, taper))).HoldThen(storage + slant, storage + taper, 0);
                // a split lead-in's share moves back with the pocket's taper (the through lane runs on one line)
                if (Math.Abs(cM - cN) > 1e-3 && !merged) { centre.HoldThen(storage, storage + taper, cN); centreDone = true; }
                double end = merged ? storage + slant : storage + taper;
                eIn = Math.Max(eIn, end);
                events.Add(string.Create(inv, $"{(merged ? "merged " : "")}pocket {string.Join("+", ids)} held {storage:F0} m, closing line {storage:F0}-{storage + slant:F0} m, lead-in hatch to {end:F0} m"));
            }
            else
            {
                // the car pocket opens over the taper; the kerb-side bike lane beyond it opens just past (out from) it, over a short
                // ease, and is at full width over the whole taper: its line follows the kerb (the user, Oct 17)
                double bikeOpen = ids.Contains(bike) ? Math.Max(EaseMin, W(mouth, bike) * EasePerMetre) : 0;
                foreach (int id in ids)
                    widths[id].HoldThen(id == bike ? storage + taper : storage, id == bike ? storage + taper + bikeOpen : storage + taper, 0);
                eIn = Math.Max(eIn, storage + taper + bikeOpen);
                events.Add(string.Create(inv, $"pocket {string.Join("+", ids)} held {storage:F0} m, taper to {storage + taper:F0} m"));
            }
        }
        if (W(mouth, MouthPlanner.HatchId) > 0)
        {
            widths[MouthPlanner.HatchId].HoldThen(0, ExitTaper, 0);
            eOut = ExitTaper;
            events.Add(string.Create(inv, $"exit hatch closes 0-{ExitTaper:F0} m"));
        }
        if (W(mouth, MouthPlanner.LineUpHatchId) is var lineUp and > 0)
        {
            // the facing edge's share of the jog: one slope from its start to the mouth and on through the core (the user)
            widths[MouthPlanner.LineUpHatchId].HoldThen(0, ExitTaper, 0);
            eIn = Math.Max(eIn, ExitTaper);
            events.Add(string.Create(inv, $"line-up hatch {lineUp:F2} closes 0-{ExitTaper:F0} m"));
        }
        if (W(mouth, MouthPlanner.AddedBikeId) is var added and > 0)
        {
            // held as far as the lights' solid centre line, and beside a kerb-side pocket while it is there
            double hold = Math.Max(AddedBikeHold, a.Openings.Where(o => PocketRules.Right.Owns(o.Id)).Select(o => o.Storage + o.Taper).DefaultIfEmpty(0).Max());
            double end = hold + Math.Max(EaseMin, added * EasePerMetre);
            widths[MouthPlanner.AddedBikeId].HoldThen(hold, end, 0);
            eIn = Math.Max(eIn, end);
            events.Add(string.Create(inv, $"added bike lane held {hold:F0} m, gone at {end:F0} m"));
        }
        // every other width change at 1:6, just beyond its side's last opening
        var handled = new HashSet<int>(a.Openings.Select(o => o.Id))
            { MouthPlanner.LeftBikeId, MouthPlanner.RightBikeId, MouthPlanner.HatchId, MouthPlanner.AddedBikeId, LeadInHatchId, MouthPlanner.LineUpHatchId };
        foreach (var x in order)
        {
            if (handled.Contains(x.Id)) continue;
            double wm = W(mouth, x.Id), wn = W(normal, x.Id);
            if (Math.Abs(wm - wn) < 1e-3) continue;
            double from = right.Contains(x.Id) ? eIn : eOut, to = from + Math.Max(EaseMin, Math.Abs(wm - wn) * EasePerMetre);
            widths[x.Id].HoldThen(from, to, wn);
            events.Add(string.Create(inv, $"{Section.Name(x.Kind)} {x.Id} {wm:F2}->{wn:F2} at {from:F0}-{to:F0} m"));
        }
        if (!centreDone && Math.Abs(cM - cN) > 1e-3)
        {
            double from = merged ? eIn : Math.Max(eIn, eOut);
            centre.HoldThen(from, from + Math.Max(EaseMin, Math.Abs(cM - cN) * EasePerMetre), cN);
        }
        double length = Math.Max(centre.End, widths.Values.Select(p => p.End).DefaultIfEmpty(0).Max());
        return new End { Arm = a, Order = order, Widths = widths, Right = right, Centre = centre, Events = events, Length = length };
    }

    /// <summary>One end's transition, squeezed into <paramref name="room"/> metres where it is longer; null where nothing changes.</summary>
    private static Transition? Single(End e, double room)
    {
        if (e.Length < 1e-6) return null;
        double scale = e.Length > room && room > 1 ? room / e.Length : 1;
        var stations = e.Widths.Values.SelectMany(p => p.Knots.Select(k => k.S)).Concat(e.Centre.Knots.Select(k => k.S)).Append(0).Append(e.Length)
            .Select(s => s * scale).Distinct().OrderBy(s => s).ToList();
        Dedupe(stations);
        var at = stations.ToArray();
        var strips = e.Order.Select(x => new TransitionStrip(x, at.Select(s => Math.Max(0, e.Widths[x.Id].At(s / scale))).ToArray())).ToList();
        var t = new Transition
        {
            Arm = e.Arm, Stations = at, Strips = strips, Centre = at.Select(s => e.Centre.At(s / scale)).ToArray(),
            Anchor = e.Order.Count(x => !e.Right.Contains(x.Id)), KerbLeftCm = e.Arm.Mouth.KerbLeftCm, KerbRightCm = e.Arm.Mouth.KerbRightCm,
            Squeezed = e.Length * (1 - scale),
        };
        t.Events.AddRange(e.Events);
        return t;
    }

    /// <summary>Stations closer than a centimetre are one.</summary>
    private static void Dedupe(List<double> stations)
    {
        for (int k = stations.Count - 1; k > 0; k--)
            if (stations[k] - stations[k - 1] < 0.01) stations.RemoveAt(k == stations.Count - 1 ? k - 1 : k);
    }

    /// <summary>
    /// Both ends' transitions as one over the street of <paramref name="d"/> metres between their mouths, in the near end's frame:
    /// every strip its normal width (easing from one end's normal to the other's) plus both ends' changes; the median (exit and
    /// lead-in hatches) the wider of the two ends'. Null where the two ends' normal sections do not match strip for strip.
    /// </summary>
    private static Transition? Joined(End a, End b, double d)
    {
        var na = a.Arm.Normal.Strips;
        var nb = b.Arm.Normal.Strips;
        if (na.Count != nb.Count) return null;
        for (int k = 0; k < na.Count; k++)
            if (na[k].Kind != nb[na.Count - 1 - k].Kind) return null;
        // the far end's ids in the near end's frame: its normal strips mirrored, its median hatches swapped (its exit hatch is the
        // near end's lead-in median and the other way round), whatever else it added offset
        int Map(int id)
        {
            int k = nb.ToList().FindIndex(x => x.Id == id);
            if (k >= 0) return na[na.Count - 1 - k].Id;
            return id == MouthPlanner.HatchId ? LeadInHatchId : id == LeadInHatchId ? MouthPlanner.HatchId : id + FarIdOffset;
        }
        static StripWay Flip(StripWay w) => w == StripWay.In ? StripWay.Out : w == StripWay.Out ? StripWay.In : StripWay.None;

        // the order, each side of the centre line apart: the near end's, the far end's (reversed, mapped) merged in; the far end's
        // strips right of its centre line are left of the near end's
        var mirrored = Enumerable.Reverse(b.Order).Select(x => (Right: !b.Right.Contains(x.Id), Strip: x with { Id = Map(x.Id), Way = Flip(x.Way) })).ToList();
        var left = a.Order.Where(x => !a.Right.Contains(x.Id)).ToList();
        var rightSide = a.Order.Where(x => a.Right.Contains(x.Id)).ToList();
        MergeInto(left, mirrored.Where(x => !x.Right).Select(x => x.Strip));
        MergeInto(rightSide, mirrored.Where(x => x.Right).Select(x => x.Strip));
        var order = left.Concat(rightSide).ToList();
        var right = rightSide.Select(x => x.Id).ToHashSet();
        var bWidths = b.Widths.ToDictionary(kv => Map(kv.Key), kv => kv.Value);
        double NormalA(int id) => WidthOf(a.Arm.Normal, id);
        double NormalB(int id)
        {
            foreach (var x in nb) if (Map(x.Id) == id) return x.Width;
            return 0;
        }
        bool Median(int id) => id is MouthPlanner.HatchId or LeadInHatchId;

        var stations = a.Widths.Values.SelectMany(p => p.Knots.Select(k => k.S)).Concat(a.Centre.Knots.Select(k => k.S))
            .Concat(b.Widths.Values.SelectMany(p => p.Knots.Select(k => d - k.S))).Concat(b.Centre.Knots.Select(k => d - k.S))
            .Append(0).Append(d).Where(s => s >= 0 && s <= d).Distinct().OrderBy(s => s).ToList();
        Dedupe(stations);
        var at0 = stations.ToArray();
        var strips = new List<TransitionStrip>();
        foreach (var x in order)
        {
            var w = new double[at0.Length];
            for (int k = 0; k < at0.Length; k++)
            {
                double s = at0[k], f = s / d;
                double wa = a.Widths.TryGetValue(x.Id, out var pa) ? pa.At(s) : NormalA(x.Id);
                double wb = bWidths.TryGetValue(x.Id, out var pb) ? pb.At(d - s) : NormalB(x.Id);
                double nA = NormalA(x.Id), nB = NormalB(x.Id);
                w[k] = Math.Max(0, Median(x.Id) ? Math.Max(wa, wb) : nA + (nB - nA) * f + (wa - nA) + (wb - nB));
            }
            strips.Add(new TransitionStrip(x, w));
        }
        double cA = a.Arm.Normal.CentreLine ?? 0, cB = -(b.Arm.Normal.CentreLine ?? 0);
        var centre = at0.Select(s => cA + (cB - cA) * (s / d) + (a.Centre.At(s) - cA) + (-b.Centre.At(d - s) - cB)).ToArray();
        var t = new Transition
        {
            Arm = a.Arm, Far = b.Arm, Stations = at0, Strips = strips, Centre = centre, Anchor = order.Count(x => !right.Contains(x.Id)),
            KerbLeftCm = a.Arm.Mouth.KerbLeftCm, KerbRightCm = a.Arm.Mouth.KerbRightCm,
        };
        t.Events.AddRange(a.Events);
        t.Events.AddRange(b.Events.Select(e => "far end: " + e));
        return t;
    }
}
