namespace UnitSport.Terrain.Format;

/// <summary>
/// What a signalised junction can pass (#1134), per movement, from its plan and its approaches' lanes: the demand the test world's
/// generators feed it. A movement's capacity is its share of the cycle (effective green: green and amber less the start-up loss,
/// <see cref="SignalPlan.StartLoss"/>) times the lanes serving it times a saturation flow (one car per
/// <see cref="SignalPlan.Headway"/> s a lane straight on, less for a turn, less again for the part of its green it gives way);
/// the demand is that times a degree of saturation (<see cref="Degree"/>), so queues form at the reds and clear within the green.
/// Riders the same on their bike groups and lanes, at a small share of a bike lane's saturation; walkers per crossing from their
/// group's green and the crossing's length. Docs: <c>docs/notes/world/traffic-generators.md</c>.
/// </summary>
public static class SignalCapacity
{
    /// <summary>A lane's saturation flow straight on (veh/h): a car every <see cref="SignalPlan.Headway"/> s, 1800.</summary>
    public const float CarSaturation = 3600f / SignalPlan.Headway;
    /// <summary>A turn's saturation against straight on's (the tighter path, HCM's turn factors).</summary>
    public const float LeftFactor = 0.95f, RightFactor = 0.85f;
    /// <summary>The part of a turn's green it gives way: to oncoming cars (a left in a shared lane), to walkers or riders only.</summary>
    public const float YieldToCars = 0.4f, YieldToCrossers = 0.75f;
    /// <summary>Demand over capacity (the degree of saturation): queues form and clear within the green.</summary>
    public const float Degree = 0.8f;
    /// <summary>A bike lane's saturation flow (bikes/h, ~1 a second), and the share of it the test world feeds: a believable few a minute.</summary>
    public const float BikeSaturation = 3600f, BikeShare = 0.08f;
    /// <summary>A bike group's start-up loss (s): riders get going quicker than cars.</summary>
    public const float BikeStartLoss = 1f;
    /// <summary>Walkers a minute per metre of crossing at a full green, both ways together (a believable few a minute per crossing).</summary>
    public const float WalkersPerMetre = 0.6f;

    /// <summary>A movement's demand: from arm <see cref="From"/> to <see cref="To"/>, a minute.</summary>
    public readonly record struct Flow(int From, int To, SignalMoves Turn, float PerMinute);

    /// <summary>Within a shared lane, how its moves split it: straight on twice a turn.</summary>
    private static float SplitWeight(SignalMoves m) => m == SignalMoves.Through ? 2f : 1f;

    /// <summary>
    /// The lanes serving <paramref name="turn"/> among <paramref name="lanes"/> of <paramref name="kind"/>: each lane counts as the
    /// share of it the turn has (a shared lane split by <see cref="SplitWeight"/> among the moves the arm makes).
    /// </summary>
    public static float LanesFor(IEnumerable<ApproachLane> lanes, ApproachLaneKind kind, SignalMoves turn, SignalMoves armMoves)
    {
        float n = 0f;
        foreach (var l in lanes)
        {
            if (l.Kind != kind || (l.Moves & turn) == 0) continue;
            float all = 0f;
            foreach (var m in (ReadOnlySpan<SignalMoves>)[SignalMoves.Left, SignalMoves.Through, SignalMoves.Right])
                if ((l.Moves & m) != 0 && (armMoves & m) != 0) all += SplitWeight(m);
            n += all > 0f ? SplitWeight(turn) / all : 0f;
        }
        return n;
    }

    /// <summary>
    /// The effective green of a movement served by <paramref name="groups"/> (any of them green), s a cycle, and the part of its
    /// green time it has priority (<paramref name="yields"/> picks the groups it gives way to while they are green, null none):
    /// (protected, given way). Start-up loss <paramref name="startLoss"/> once per green run, amber counted.
    /// </summary>
    public static (float Protected, float Yielding) Green(SignalPlan plan, IReadOnlyList<int> groups, Func<int, bool>? yields, float startLoss)
    {
        if (plan.Cycle <= 0 || groups.Count == 0) return (0f, 0f);
        const float Step = 0.25f;
        int samples = (int)Math.Round(plan.Cycle / Step);
        float green = 0f, giving = 0f, amber = 0f;
        int runs = 0;
        bool wasGreen = IsGreen(plan, groups, (samples - 1) * Step);
        for (int i = 0; i < samples; i++)
        {
            double t = i * Step + Step * 0.5;
            bool g = IsGreen(plan, groups, t);
            if (g)
            {
                green += Step;
                if (!wasGreen) runs++;
                if (yields is not null)
                    for (int h = 0; h < plan.Groups.Count; h++)
                        if (yields(h) && plan.Groups[h].Intervals.Count > 0 && StateAt(plan, h, t) == SignalAspect.Green) { giving += Step; break; }
            }
            else
                foreach (int k in groups)
                    if (StateAt(plan, k, t) == SignalAspect.Amber) { amber += Step; break; }
            wasGreen = g;
        }
        if (runs == 0 && green > 0f) runs = 1;
        float effective = Math.Max(0f, green + amber - startLoss * runs);
        float share = green > 0f ? giving / green : 0f;
        return (effective * (1f - share), effective * share);
    }

    private static SignalAspect StateAt(SignalPlan plan, int g, double cycleTime) => plan.State(g, cycleTime - plan.Offset);

    private static bool IsGreen(SignalPlan plan, IReadOnlyList<int> groups, double t)
    {
        foreach (int g in groups)
            if (StateAt(plan, g, t) == SignalAspect.Green) return true;
        return false;
    }

    /// <summary>
    /// The cars' demand per movement: for each arm's approach (<paramref name="lanesOf"/> its lanes, null for an arm with none),
    /// each move its car groups serve (<see cref="SignalPlan.Movements"/>).
    /// </summary>
    public static List<Flow> Cars(SignalPlan plan, Func<int, IReadOnlyList<ApproachLane>?> lanesOf)
    {
        var conflicts = plan.Conflicts();
        var list = new List<Flow>();
        foreach (var (from, to, turn, groups) in Moves(plan, k => k is SignalGroupKind.Car or SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow))
        {
            if (lanesOf(from) is not { } lanes) continue;
            float n = LanesFor(lanes, ApproachLaneKind.Car, turn, ArmMoves(plan, from, ApproachLaneKind.Car));
            if (n <= 0f) continue;
            // a turn from a shared lane's full head gives way while what it crosses is green: oncoming cars (a left), walkers, riders
            bool toCars = false;
            bool Yields(int h)
            {
                if (turn == SignalMoves.Through) return false;
                foreach (int g in groups)
                {
                    if (plan.Groups[g].Kind != SignalGroupKind.Car || conflicts[g, h] != SignalPlan.Conflict.Permissive) continue;
                    var other = plan.Groups[h];
                    if (other.Kind is SignalGroupKind.Car or SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow or SignalGroupKind.Tram)
                    {
                        if (turn != SignalMoves.Left) continue;
                        toCars = true;
                    }
                    return true;
                }
                return false;
            }
            var (prot, giving) = Green(plan, groups, Yields, SignalPlan.StartLoss);
            float factor = turn switch { SignalMoves.Left => LeftFactor, SignalMoves.Right => RightFactor, _ => 1f };
            float green = prot + giving * (toCars ? YieldToCars : YieldToCrossers);
            float perHour = CarSaturation * factor * n * green / plan.Cycle;
            list.Add(new Flow(from, to, turn, Degree * perHour / 60f));
        }
        return list;
    }

    /// <summary>
    /// The riders' demand per movement: an arm's bike groups (#351) where it has them, else its cars' groups for its bike lanes;
    /// the bike lanes serving the move (a separated path, no lane in the record: one).
    /// </summary>
    public static List<Flow> Bikes(SignalPlan plan, Func<int, IReadOnlyList<ApproachLane>?> lanesOf)
    {
        var list = new List<Flow>();
        var bikeArms = new HashSet<int>(plan.Groups.Where(g => g.Kind == SignalGroupKind.Bike).Select(g => g.Arm));
        foreach (var (from, to, turn, groups) in Moves(plan, _ => true))
        {
            bool own = bikeArms.Contains(from);
            if (own != (plan.Groups[groups[0]].Kind == SignalGroupKind.Bike)) continue;
            if (!own && plan.Groups[groups[0]].Kind is SignalGroupKind.Pedestrian or SignalGroupKind.Tram or SignalGroupKind.Flasher) continue;
            var lanes = lanesOf(from) ?? [];
            float n = LanesFor(lanes, ApproachLaneKind.Bike, turn, ArmMoves(plan, from, ApproachLaneKind.Bike));
            if (n <= 0f && own) n = 1f;   // a separated path round the corner
            if (n <= 0f) continue;
            var (prot, giving) = Green(plan, groups, null, BikeStartLoss);
            float perHour = BikeSaturation * n * (prot + giving) / plan.Cycle;
            list.Add(new Flow(from, to, turn, BikeShare * perHour / 60f));
        }
        return list;
    }

    /// <summary>
    /// The walkers a minute over each arm's crossing, both ways together: its group's green (a two-stage crossing's least half) times
    /// the crossing's length times <see cref="WalkersPerMetre"/>. Arms without a crossing are left out.
    /// </summary>
    public static Dictionary<int, float> Walkers(SignalPlan plan)
    {
        var byArm = new Dictionary<int, float>();
        if (plan.Cycle <= 0) return byArm;
        foreach (var arm in plan.Groups.Where(g => g.Kind == SignalGroupKind.Pedestrian).GroupBy(g => g.Arm))
        {
            float least = float.MaxValue;
            foreach (var g in arm)
            {
                float green = g.Intervals.Where(iv => iv.Aspect == SignalAspect.Green).Sum(iv => iv.To - iv.From);
                least = Math.Min(least, green);
            }
            float length = plan.Arms[arm.Key].CrossingM;
            byArm[arm.Key] = WalkersPerMetre * length * least / plan.Cycle;
        }
        return byArm;
    }

    /// <summary>The moves the plan lets an arm's cars (or riders: <paramref name="kind"/> Bike, their own groups where it has them) make.</summary>
    private static SignalMoves ArmMoves(SignalPlan plan, int arm, ApproachLaneKind kind)
    {
        bool bikeGroups = kind == ApproachLaneKind.Bike && plan.Groups.Any(g => g.Arm == arm && g.Kind == SignalGroupKind.Bike);
        var moves = SignalMoves.None;
        foreach (var m in plan.Movements())
        {
            var k = plan.Groups[m.Group].Kind;
            if (m.From == arm && (bikeGroups ? k == SignalGroupKind.Bike : k is SignalGroupKind.Car or SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow))
                moves |= m.Turn;
        }
        return moves;
    }

    /// <summary>The plan's movements whose group's kind passes <paramref name="kinds"/>, each with every such group serving it.</summary>
    private static List<(int From, int To, SignalMoves Turn, List<int> Groups)> Moves(SignalPlan plan, Func<SignalGroupKind, bool> kinds)
    {
        var moves = new List<(int, int, SignalMoves, List<int>)>();
        foreach (var m in plan.Movements())
        {
            var kind = plan.Groups[m.Group].Kind;
            if (!kinds(kind)) continue;
            // a bike group and a car group serving the same move are two movements (riders and cars): keyed by the bike-ness
            bool bike = kind == SignalGroupKind.Bike;
            int at = moves.FindIndex(x => x.Item1 == m.From && x.Item2 == m.To && (plan.Groups[x.Item4[0]].Kind == SignalGroupKind.Bike) == bike);
            if (at < 0) moves.Add((m.From, m.To, m.Turn, new List<int> { m.Group }));
            else moves[at].Item4.Add(m.Group);
        }
        return moves;
    }
}
