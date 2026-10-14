namespace UnitSport.Tools.RoadGen.Layout;

using UnitSport.Terrain.Format;

/// <summary>
/// What the data wants of an approach (#818), as the junction planner reads it: the turns it has and those banned, the
/// pockets' lanes (<see cref="PocketRules"/>) and the carriageway's own lanes with their moves, the bike layout and the
/// junction's kind.
/// </summary>
/// <param name="LeftPocket">The left pocket's lanes, left to right; empty for none.</param>
/// <param name="RightPocket">The right pocket's lanes, left to right; empty for none.</param>
/// <param name="OwnMoves">The carriageway's own lanes, left to right, null to share the remaining turns out (one lane: all of them).</param>
/// <param name="FromData">The lanes are OSM's <c>turn:lanes</c> (the user: the truth; the room order never drops their pockets).</param>
/// <param name="BikeBetween">Beside a right pocket the painted bike lane runs between it and the through lane, layout (b) (#351).</param>
/// <param name="LeftTurnBikeLane">A left-turn bike lane between the left pocket and the through lane.</param>
/// <param name="ThroughBike">Riders arriving with neither a bike lane nor a path get a through bike lane.</param>
/// <param name="RightTurnBike">Beside a right pocket with the bike lane between (b), a bike right-turn lane at the kerb.</param>
/// <remarks>
/// The three bike moves follow one rule (the user, Oct 13 2026): a bike lane beside the car lane making that move (the left
/// and right ones beside the car pocket), where the street the move goes into carries riders on (a departing bike lane or
/// path), or at the lights, where it ends at its advanced line or bike box anyway. The planner decides the three flags.
/// </remarks>
public sealed record ArmWants(bool Lights, SignalMoves Can, SignalMoves Banned, SignalMoves[] LeftPocket, SignalMoves[]? OwnMoves,
    SignalMoves[] RightPocket, bool BikeBetween, bool LeftTurnBikeLane, bool ThroughBike = false, bool RightTurnBike = false,
    bool FromData = false)
{
    /// <summary>The lanes wanted for a pocket of <paramref name="rule"/>.</summary>
    public SignalMoves[] Pocket(PocketRule rule) => rule == PocketRules.Left ? LeftPocket : RightPocket;

    /// <summary>These wants without that pocket.</summary>
    public ArmWants Without(PocketRule rule) => rule == PocketRules.Left ? this with { LeftPocket = [] } : this with { RightPocket = [] };
}

/// <summary>
/// OSM's <c>turn:lanes</c> fitted onto a carriageway of <c>own</c> lanes toward the junction (#818, the user: the data is the
/// truth): its lanes beyond the carriageway's own are the pockets, taken from each end, the left-turning ones at the centre
/// and the right-only ones at the kerb (at most <see cref="PocketRule.MaxLanes"/> each); the rest lie on the own lanes, any
/// still left over folded into their neighbour. Moves are among <c>allowed</c> (a lane showing none keeps them all).
/// </summary>
public readonly record struct DataLanes(SignalMoves[] Left, SignalMoves[] Own, SignalMoves[] Right, int Folded)
{
    public static DataLanes Of(SignalMoves[] wished, int own, SignalMoves allowed)
    {
        var wish = wished.Select(m => m & allowed).Select(m => m == SignalMoves.None ? allowed : m).ToArray();
        int m = wish.Length, extra = Math.Max(0, m - Math.Max(1, own));
        int left = 0, right = 0;
        while (left < extra && left < PocketRules.Left.MaxLanes && (wish[left] & SignalMoves.Left) != 0) left++;
        while (right < extra - left && right < PocketRules.Right.MaxLanes && wish[m - 1 - right] == SignalMoves.Right) right++;
        var middle = wish[left..(m - right)];
        int n = Math.Max(1, own);
        var lanes = new SignalMoves[n];
        int folded = Math.Max(0, middle.Length - n);
        if (middle.Length >= n)
        {
            for (int k = 0; k <= folded; k++) lanes[0] |= middle[k];
            for (int k = 1; k < n; k++) lanes[k] = middle[folded + k];
        }
        else
            for (int k = 0; k < n; k++) lanes[k] = middle[Math.Max(0, k - (n - middle.Length))];
        return new DataLanes(wish[..left], lanes, wish[(m - right)..], folded);
    }
}

/// <summary>
/// The free room beside an arm (#818): at stations out from the mouth (m), how far each way from its axis the section may
/// reach before a facade, another line, the ground off the road or the tile's edge (m, left and right of a driver arriving).
/// </summary>
public sealed class ArmRoom(double[] stations, double[] left, double[] right)
{
    public static readonly ArmRoom Unbounded = new([0], [double.MaxValue], [double.MaxValue]);

    public double[] Stations { get; } = stations;
    public double[] LeftAt { get; } = left;
    public double[] RightAt { get; } = right;

    /// <summary>The least room each side over every station.</summary>
    public double Left => LeftAt.Min();
    public double Right => RightAt.Min();

    public static ArmRoom Flat(double left, double right) => new([0], [left], [right]);
}

/// <summary>
/// How a section's reach changes along its transition (#818): the mouth section's held over <see cref="Hold"/> metres (the
/// storage), then easing to the normal section's over <see cref="Ease"/> (the taper and lead-in).
/// </summary>
public readonly record struct Stretch(double Hold, double Ease)
{
    /// <summary>How much of the mouth's extra reach is still there at station <paramref name="s"/>: 1 held, 0 back to normal.</summary>
    public double Share(double s) => s <= Hold ? 1 : Ease <= 0 ? 0 : Math.Clamp(1 - (s - Hold) / Ease, 0, 1);
}

/// <summary>
/// Plans an arm's mouth section (#818, step 1 of <c>docs/plans/junction-layout.md</c>): the lanes the data wants built on
/// the street's normal section, then shrunk in the user's order until it fits its room. Pure: the rewriter measures the room
/// and gathers the wants.
/// </summary>
public static class MouthPlanner
{
    /// <summary>A car lane at the mouth, and the least it shrinks to (step 3).</summary>
    public const double Lane = 3.0, LaneMin = 2.75;
    /// <summary>Where an approach gets pockets its lanes are no wider than this (a wide street's lanes are not copied to the mouth).</summary>
    public const double LaneMax = 3.5;
    /// <summary>A painted bike lane, its least (step 3), and the left-turn bike lane at the lights (#351).</summary>
    public const double BikeMin = 1.25, LeftBikeLane = 1.5;
    /// <summary>A sidewalk is never narrower than this (step 5).</summary>
    public const double SidewalkMin = 1.5;

    /// <summary>The step that gives up an exit's hatch: the pocket that feeds it goes (the planner drops it on that arm).</summary>
    public const string FeederStep = "7 the pocket feeding this exit";

    /// <summary>Ids of strips the planner adds: above any a normal section numbers.</summary>
    public const int LeftBikeId = 110, AddedBikeId = 115, RightBikeId = 125, HatchId = 130, LineUpHatchId = 132;
    /// <summary>The first lane of each pocket (<see cref="PocketRule.FirstId"/>).</summary>
    public static readonly int LeftPocketId = PocketRules.Left.FirstId, RightPocketId = PocketRules.Right.FirstId;

    /// <summary>
    /// The section the data wants at the mouth: the normal section's sides and departing lanes kept, the arriving side laid out
    /// from the centre line: the left pocket's lanes, the left-turn bike lane, the own lanes, then the right pocket and the
    /// painted bike lane in layout (a) or (b). Every arriving car lane is at least <see cref="Lane"/> and as wide as the
    /// street's own; a departing car lane beside a bike lane is at least <see cref="Lane"/> (#120: a car passes a cyclist
    /// without crossing the solid centre line). The carriageway keeps its left edge (no split lead-in yet: the alignment's).
    /// </summary>
    public static Section Wanted(Section normal, ArmWants w)
    {
        var strips = normal.Strips;
        var outSide = new List<Strip>();
        var inCars = new List<Strip>();
        Strip? bikeIn = null;
        var rightBands = new List<Strip>();
        var leftBands = new List<Strip>();
        // an arriving lane as wide as the street's own, between a full lane and LaneMax where lanes are added
        double laneIn = Lane;
        foreach (var (_, s) in normal.CarLanes(StripWay.In)) laneIn = Math.Max(laneIn, s.Width);
        var allowedHere = w.Can & ~w.Banned;
        // each pocket's lanes, only for a move that may be made
        SignalMoves[] Lanes(PocketRule rule) => (allowedHere & rule.Move) != 0 ? w.Pocket(rule) : [];
        bool adds = PocketRules.All.Any(r => Lanes(r).Length > 0);
        if (adds) laneIn = Math.Min(laneIn, LaneMax);
        for (int k = 0; k < strips.Count; k++)
        {
            var s = strips[k];
            if (k < normal.FirstCarriageway || normal.FirstCarriageway < 0) leftBands.Add(s);
            else if (k > normal.LastCarriageway) rightBands.Add(s);
            else if (s.Way == StripWay.Out) outSide.Add(s);
            else if (s.Kind == StripKind.BikeLane) bikeIn = s;
            else if (s.IsCarLane) inCars.Add(s);
        }
        // at the lights a departing car lane beside its bike lane is a full lane (#711: the solid centre line before the stop line)
        for (int k = 0; w.Lights && k < outSide.Count; k++)
            if (outSide[k].IsCarLane && (k > 0 && outSide[k - 1].Kind == StripKind.BikeLane) && outSide[k].Width < Lane)
                outSide[k] = outSide[k].WithWidth(Lane);
        // a one-way street leaving the junction: nothing arrives
        if (inCars.Count == 0)
            return new Section([.. leftBands, .. outSide, .. rightBands], normal.CarriagewayLeft) { KerbLeftCm = normal.KerbLeftCm, KerbRightCm = normal.KerbRightCm };

        var allowed = allowedHere;
        var arriving = new List<Strip>();
        // the centre side's pocket (left), then the own lanes, then the kerb side's (right): one rule, the side from the table
        var pocket = Lanes(PocketRules.Left);
        for (int k = 0; k < pocket.Length; k++)
            arriving.Add(new Strip(StripKind.CarLane, laneIn, StripWay.In, pocket[k] & allowed, LeftPocketId + k));
        if (pocket.Length > 0 && w.LeftTurnBikeLane)
            arriving.Add(new Strip(StripKind.BikeLane, LeftBikeLane, StripWay.In, SignalMoves.Left, LeftBikeId));
        // the through riders first (the user, Oct 13 2026): where they have neither a painted lane nor a path on this side and
        // the straight-on exit carries one on, or at the lights (where it ends at its advanced line or bike box), the mouth gets
        // them a bike lane
        if (w.ThroughBike && bikeIn is null && !rightBands.Any(x => x.Kind == StripKind.Path))
            bikeIn = new Strip(StripKind.BikeLane, LeftBikeLane, StripWay.In, Id: AddedBikeId);
        // the own lanes: as the data assigned them, else what the pockets leave of the turns
        var rights = Lanes(PocketRules.Right);
        bool right = rights.Length > 0;
        var rest = allowed & ~(pocket.Length > 0 ? SignalMoves.Left : 0) & ~(right ? SignalMoves.Right : 0);
        if (rest == SignalMoves.None) rest = allowed;
        for (int k = 0; k < inCars.Count; k++)
        {
            var moves = w.OwnMoves is { } o && k < o.Length ? o[k] & allowed : rest;
            if (moves == SignalMoves.None) moves = rest;
            // where nothing is added the street's own lanes run on as they are
            arriving.Add(new Strip(StripKind.CarLane, adds ? laneIn : inCars[k].Width, StripWay.In, moves, inCars[k].Id));
        }
        var lastOwn = arriving[^1];
        var rightLanes = rights.Select((m, k) => new Strip(StripKind.CarLane, laneIn, StripWay.In, m & allowed, RightPocketId + k)).ToList();
        if (right && bikeIn is { } b && w.BikeBetween)
        {
            // the bike lane between goes straight on; riders turning right get their own lane at the kerb, right of the pocket,
            // where there is plenty of room (it goes first), else share the pocket (the user, Oct 13 2026)
            arriving.Add(b with { Moves = lastOwn.Moves & SignalMoves.Through });
            if (w.RightTurnBike)
            {
                arriving.AddRange(rightLanes);
                arriving.Add(new Strip(StripKind.BikeLane, LeftBikeLane, StripWay.In, SignalMoves.Right, RightBikeId));
            }
            else arriving.AddRange(rightLanes.Select((x, k) => k == rightLanes.Count - 1 ? x with { SharedWithBikes = true } : x));
        }
        else
        {
            arriving.AddRange(rightLanes);
            if (bikeIn is { } b2) arriving.Add(b2 with { Moves = (lastOwn.Moves | (right ? SignalMoves.Right : 0)) & (SignalMoves.Through | SignalMoves.Right) & allowed });
        }

        var all = new List<Strip>(leftBands.Count + outSide.Count + arriving.Count + rightBands.Count);
        all.AddRange(leftBands);
        all.AddRange(outSide);
        all.AddRange(arriving);
        all.AddRange(rightBands);
        return new Section(all, normal.CarriagewayLeft) { KerbLeftCm = normal.KerbLeftCm, KerbRightCm = normal.KerbRightCm };
    }

    /// <summary>The section with a hatch of <paramref name="width"/> between the centre line and the departing lanes (an exit, #123).</summary>
    public static Section WithExitHatch(Section s, double width)
    {
        var strips = s.Strips.Where(x => x.Kind != StripKind.Hatch).ToList();
        if (width > 0.05)
        {
            int at = strips.FindIndex(x => x.Way == StripWay.In && x.InCarriageway);
            if (at < 0) at = s.LastCarriageway + 1;
            strips.Insert(at, new Strip(StripKind.Hatch, width, StripWay.None, Id: HatchId));
        }
        // the hatch widens the carriageway on the left (the departing side, seen driving in); a line-up hatch right of the centre
        // line goes with the right edge
        double grown = (width > 0.05 ? width : 0) - s.Strips.Where(x => x.Kind == StripKind.Hatch && x.Way != StripWay.In).Sum(x => x.Width);
        return new Section(strips, s.CarriagewayLeft - grown) { KerbLeftCm = s.KerbLeftCm, KerbRightCm = s.KerbRightCm };
    }

    /// <summary>
    /// The section with a hatch of <paramref name="width"/> right of the centre line, before the arriving lanes (the user, Oct 14
    /// 2026: the facing arm's edge lined up across the whole junction): the arriving side moves out by it, the departing side stays.
    /// It is an arriving strip, so the centre line stays on its left.
    /// </summary>
    public static Section WithLineUpHatch(Section s, double width)
    {
        var strips = s.Strips.Where(x => x.Id != LineUpHatchId).ToList();
        int at = strips.FindIndex(x => x.Way == StripWay.In && x.InCarriageway);
        if (width > 0.05 && at >= 0) strips.Insert(at, new Strip(StripKind.Hatch, width, StripWay.In, Id: LineUpHatchId));
        return new Section(strips, s.CarriagewayLeft) { KerbLeftCm = s.KerbLeftCm, KerbRightCm = s.KerbRightCm };
    }

    /// <summary>The section moved <paramref name="by"/> metres to the right across the arm (a split lead-in moves it left: negative).</summary>
    public static Section Moved(Section s, double by) =>
        new(s.Strips, s.CarriagewayLeft + by) { KerbLeftCm = s.KerbLeftCm, KerbRightCm = s.KerbRightCm };

    /// <summary>How far the section reaches from the axis on each side, side bands included.</summary>
    public static (double Left, double Right) Reach(Section s)
    {
        double left = -s.CarriagewayLeft, right = s.CarriagewayRight;
        for (int k = 0; k < s.FirstCarriageway; k++) left += s.Strips[k].Width;
        for (int k = s.LastCarriageway + 1; k < s.Strips.Count; k++) right += s.Strips[k].Width;
        return (left, right);
    }

    /// <summary>
    /// Shrinks <paramref name="wanted"/> until it fits <paramref name="room"/>, in the user's order (Oct 13 2026): (1) verge
    /// and buffer, (2) island (the exit hatch stays: it lines the through lane up), the left-turn bike lane (without lights
    /// right after the bike right-turn lane, first of all; a through bike
    /// lane the mouth added survives when possible: it goes with the painted ones, last), (3) lane widths to their least, (4) right pocket, (5) sidewalk to 1.5 m,
    /// (6) [longitudinal: storage and lead-in, decided with the lengths], (7) the pocket feeding this exit (its hatch), then
    /// its own left pocket, (8) painted bike lane. Car
    /// through lanes and the data's lane count never drop; with <paramref name="keepPockets"/> (OSM's lanes) nor its pockets, and
    /// with <paramref name="keepHatch"/> (an exit fed by such a pocket) nor the exit's hatch. Where the street already reaches past its room (its sidewalks
    /// were laid to the facades), the section need only reach no further than the street does. Each step taken is named in
    /// <paramref name="steps"/>.
    /// </summary>
    public static Section Fit(Section wanted, Section normal, ArmRoom room, Stretch left, Stretch right, List<string> steps, bool lights = true,
        bool keepPockets = false, bool keepHatch = false)
    {
        var (roomL, roomR) = MouthRoom(normal, room, left, right);
        bool Fits(Section s)
        {
            var (l, r) = Reach(s);
            return l <= roomL + 1e-6 && r <= roomR + 1e-6;
        }
        var s = wanted;
        if (Fits(s)) return s;
        List<(string Name, Func<Section, Section?> Step)> ladder =
        [
            // a bike right-turn lane only where there is plenty of room: riders turning right share the pocket instead
            ("1 bike right-turn lane", WithoutRightBike),
            ("1 verge and buffer", x => Without(x, k => k is StripKind.Verge or StripKind.Buffer)),
            // the exit hatch itself lines the through lane up (a jog without it): it is not dropped here; an exit that cannot hold it
            // takes the pocket that feeds it (the planner's)
            ("2 island", x => Without(x, k => k is StripKind.Island)),
            // a left-turn bike lane only beside the through riders' own (the user): it goes early; a through lane the mouth added
            // survives when possible, as the street's own (step 8)
            ("2 left-turn bike lane", x => WithoutId(x, LeftBikeId)),
            // each side narrows only where it lacks room: the arriving lanes reach right, the departing ones left
            ("3 lane widths to their least", x => LeastWidths(x, StripWay.In)),
            ("3 lane widths to their least", x => LeastWidths(x, StripWay.Out)),
            (PocketRules.Right.DropStep, x => keepPockets ? null : WithoutPocket(x, PocketRules.Right)),
            ("5 sidewalk to 1.5 m", x => Narrowed(x, StripKind.Sidewalk, SidewalkMin, left: false)),
            ("5 sidewalk to 1.5 m", x => Narrowed(x, StripKind.Sidewalk, SidewalkMin, left: true)),
            (FeederStep, x => keepHatch ? null : Without(x, k => k is StripKind.Hatch)),
            (PocketRules.Left.DropStep, x => keepPockets ? null : WithoutPocket(x, PocketRules.Left)),
            ("8 painted bike lane", x => Without(x, k => k is StripKind.BikeLane)),
        ];
        // without lights the turning riders' lanes go first of all: the right one, then the left one (the user)
        if (!lights)
        {
            int at = ladder.FindIndex(x => x.Name == "2 left-turn bike lane");
            var leftBike = ladder[at];
            ladder.RemoveAt(at);
            ladder.Insert(1, ("1 left-turn bike lane", leftBike.Step));
        }
        foreach (var (name, step) in ladder)
        {
            if (step(s) is not { } next || Same(next, s)) continue;
            // a step on the side that already fits would cost the arm for nothing
            var (l0, r0) = Reach(s);
            var (l1, r1) = Reach(next);
            bool helps = (l0 > roomL + 1e-6 && l1 < l0 - 1e-6) || (r0 > roomR + 1e-6 && r1 < r0 - 1e-6);
            if (!helps) continue;
            if (!steps.Contains(name)) steps.Add(name);
            s = next;
            if (Fits(s)) return s;
        }
        steps.Add("does not fit");
        return s;
    }

    /// <summary>
    /// The most a mouth section may reach from the axis on each side: at every station its extra over the street's own reach
    /// must fit the room there, as much of it as the transition still holds there (<paramref name="left"/>, <paramref name="right"/>).
    /// Where the street already reaches past its room (its sidewalks were laid to the facades), as far as the street does.
    /// </summary>
    public static (double Left, double Right) MouthRoom(Section normal, ArmRoom room, Stretch left, Stretch right)
    {
        var (nl, nr) = Reach(normal);
        double roomL = double.MaxValue, roomR = double.MaxValue;
        for (int k = 0; k < room.Stations.Length; k++)
        {
            double sl = left.Share(room.Stations[k]), sr = right.Share(room.Stations[k]);
            if (sl > 1e-9) roomL = Math.Min(roomL, nl + Math.Max(0, room.LeftAt[k] - nl) / sl);
            if (sr > 1e-9) roomR = Math.Min(roomR, nr + Math.Max(0, room.RightAt[k] - nr) / sr);
        }
        return (roomL, roomR);
    }

    private static bool Same(Section a, Section b) =>
        a.Strips.Count == b.Strips.Count && Math.Abs(a.CarriagewayLeft - b.CarriagewayLeft) < 1e-9 && a.Strips.SequenceEqual(b.Strips);

    /// <summary>The strips of kinds <paramref name="drop"/> left out, the carriageway's left edge moving by what went on its left.</summary>
    private static Section Without(Section s, Func<StripKind, bool> drop)
    {
        var kept = new List<Strip>();
        double leftGone = 0;
        bool seenIn = false;
        foreach (var x in s.Strips)
        {
            if (x.Way == StripWay.In) seenIn = true;
            if (drop(x.Kind))
            {
                // a carriageway strip left of the arriving lanes (an exit hatch) takes the left edge in with it
                if (x.InCarriageway && !seenIn) leftGone += x.Width;
                continue;
            }
            kept.Add(x);
        }
        return new Section(kept, s.CarriagewayLeft + leftGone) { KerbLeftCm = s.KerbLeftCm, KerbRightCm = s.KerbRightCm };
    }

    private static Section? WithoutRightBike(Section s)
    {
        if (!s.Strips.Any(x => x.Id == RightBikeId)) return null;
        int kerbmost = s.Strips.Where(x => PocketRules.Right.Owns(x.Id)).Select(x => x.Id).DefaultIfEmpty(-1).Max();
        var strips = s.Strips.Where(x => x.Id != RightBikeId).Select(x => x.Id == kerbmost ? x with { SharedWithBikes = true } : x).ToList();
        return new Section(strips, s.CarriagewayLeft) { KerbLeftCm = s.KerbLeftCm, KerbRightCm = s.KerbRightCm };
    }

    private static Section? WithoutId(Section s, int id)
    {
        if (!s.Strips.Any(x => x.Id == id)) return null;
        return new Section(s.Strips.Where(x => x.Id != id).ToList(), s.CarriagewayLeft) { KerbLeftCm = s.KerbLeftCm, KerbRightCm = s.KerbRightCm };
    }

    /// <summary>The car and bike lanes going <paramref name="way"/> at their least width; the carriageway's left edge moves in with the departing ones.</summary>
    private static Section LeastWidths(Section s, StripWay way)
    {
        var strips = s.Strips.Select(x => x.Way != way ? x : x.Kind switch
        {
            StripKind.CarLane when x.Width > LaneMin => x.WithWidth(LaneMin),
            StripKind.BikeLane when x.Width > BikeMin => x.WithWidth(BikeMin),
            _ => x,
        }).ToList();
        double leftGone = 0;
        int firstIn = s.Strips.ToList().FindIndex(x => x.Way == StripWay.In);
        for (int k = s.FirstCarriageway; k >= 0 && k < (firstIn < 0 ? s.Strips.Count : firstIn) && k <= s.LastCarriageway; k++)
            leftGone += s.Strips[k].Width - strips[k].Width;
        return new Section(strips, s.CarriagewayLeft + leftGone) { KerbLeftCm = s.KerbLeftCm, KerbRightCm = s.KerbRightCm };
    }

    /// <summary>The bands of <paramref name="kind"/> on one side (left of the carriageway or right of it) at most <paramref name="least"/> wide.</summary>
    private static Section Narrowed(Section s, StripKind kind, double least, bool left) =>
        new(s.Strips.Select((x, k) => x.Kind == kind && x.Width > least && (left ? k < s.FirstCarriageway : k > s.LastCarriageway) ? x.WithWidth(least) : x).ToList(),
            s.CarriagewayLeft)
        { KerbLeftCm = s.KerbLeftCm, KerbRightCm = s.KerbRightCm };

    /// <summary>
    /// The pocket of <paramref name="rule"/> left out (and its bike lane), its move going to the own lane on its side: the
    /// first own lane for the centre side's, the last for the kerb side's.
    /// </summary>
    public static Section? WithoutPocket(Section s, PocketRule rule)
    {
        int bike = rule.CentreSide ? LeftBikeId : RightBikeId;
        var strips = s.Strips.ToList();
        int removed = strips.RemoveAll(x => rule.Owns(x.Id) || x.Id == bike);
        if (removed == 0) return null;
        var own = strips.Select((x, k) => (x, k)).Where(p => p.x.IsCarLane && p.x.Way == StripWay.In && PocketRules.Of(p.x.Id) is null).Select(p => p.k).ToList();
        if (own.Count > 0)
        {
            int at = rule.CentreSide ? own[0] : own[^1];
            strips[at] = strips[at] with { Moves = strips[at].Moves | rule.Move };
        }
        return new Section(strips, s.CarriagewayLeft) { KerbLeftCm = s.KerbLeftCm, KerbRightCm = s.KerbRightCm };
    }
}
