namespace UnitSport.Terrain.Format;

/// <summary>What one signal head shows (SSV Art. 68-70).</summary>
public enum SignalAspect : byte
{
    Off = 0,
    Red = 1,
    /// <summary>Red and yellow together before green (SSV Art. 71 al. 5).</summary>
    RedAmber = 2,
    Green = 3,
    Amber = 4,
    /// <summary>The flashing yellow lamp beside a green whose turn crosses a pedestrian or bike green (SSV Art. 68 al. 3).</summary>
    FlashingAmber = 5,
}

public enum SignalGroupKind : byte
{
    /// <summary>A full-lens head: the main lane of an approach (through, and the turns that share it).</summary>
    Car = 0,
    /// <summary>A left-turn pocket's own arrow head: protected only.</summary>
    LeftArrow = 1,
    /// <summary>A right-turn pocket's own arrow head: protected only.</summary>
    RightArrow = 2,
    Pedestrian = 3,
    Bike = 4,
    /// <summary>The flashing yellow lamp qualifying <see cref="SignalGroup.Qualifies"/>.</summary>
    Flasher = 5,
}

/// <summary>Movements a group serves, as the approaching driver turns.</summary>
[Flags]
public enum SignalMoves : byte { None = 0, Left = 1, Through = 2, Right = 4 }

/// <summary>
/// One arm of a signalised junction as the planner sees it. <see cref="Heading"/> points out of
/// the junction along the arm, radians in plan view (LV95: east 0, north π/2), as RoadGen's
/// <c>JunctionArm.OutwardHeading</c>.
/// </summary>
public readonly record struct SignalArm(
    double Heading,
    bool In,
    bool Out,
    bool LeftPocket = false,
    bool RightPocket = false,
    bool Pedestrians = false,
    bool BikeSignal = false,
    float SpeedKmh = 50,
    float CrossingM = 7,
    byte Rank = 1);

public readonly record struct SignalInterval(SignalAspect Aspect, float From, float To);

/// <summary>A signal group (Signalgruppe): heads that always show the same aspect.</summary>
public sealed class SignalGroup
{
    public SignalGroupKind Kind { get; set; }
    /// <summary>The approach it controls; for a pedestrian group, the arm it crosses.</summary>
    public int Arm { get; init; }
    public SignalMoves Moves { get; set; }
    /// <summary>For a <see cref="SignalGroupKind.Flasher"/>, the group whose green it qualifies; else -1.</summary>
    public int Qualifies { get; init; } = -1;
    /// <summary>The cycle, from 0 to <see cref="SignalPlan.Cycle"/>, in order and without gaps.</summary>
    public List<SignalInterval> Intervals { get; init; } = new();
}

/// <summary>
/// A fixed-time signal plan (#349) for one junction: its groups and their aspects over one cycle.
/// Built by <see cref="Build"/> from the lanes the network stage laid out, stored in the tile as
/// computed, and read at runtime with the server clock (<see cref="State"/>), so every peer shows
/// the same aspects and nothing is replicated. Rules and values: docs/notes/tools/traffic-signals.md.
/// </summary>
public sealed class SignalPlan
{
    // ---- Swiss values (sources in the note) -------------------------------------------------

    /// <summary>Yellow by speed limit: 3 s up to 50 km/h, 4 s at 60, 5 s from 70 (SN 640 837).</summary>
    public static float Yellow(float speedKmh) => speedKmh <= 50.5f ? 3f : speedKmh <= 60.5f ? 4f : 5f;

    public const float RedAmberCar = 2f, RedAmberBike = 1f, YellowBike = 2f;
    public const float MinGreen = 4f, AllRed = 2f, AllRedAfterArrow = 1f, BikeLead = 3f;
    /// <summary>A pedestrian clears 2/3 of the crossing at this pace in the clearance time (2-8 s).</summary>
    public const float WalkSpeed = 1.2f;
    /// <summary>The green of a leading or lagging left-turn phase.</summary>
    public const float TurnPhaseGreen = 5f;
    public const float MaxCycle = 120f;

    // ---- the plan ----------------------------------------------------------------------------

    public List<SignalArm> Arms { get; init; } = new();
    public List<SignalGroup> Groups { get; init; } = new();
    public float Cycle { get; set; }
    /// <summary>Seconds added to the clock before the lookup: junctions are not coordinated (#356).</summary>
    public float Offset { get; set; }
    /// <summary>Pedestrian heads have a yellow lens (3-lens canton heads, e.g. Vaud); else red after green (e.g. Geneva).</summary>
    public bool PedestrianAmber { get; set; } = true;
    /// <summary>
    /// Bike heads stand on a white plate like the car heads; not in every canton (#759). Bit 1 of
    /// the byte that holds <see cref="PedestrianAmber"/>: data written before reads false.
    /// </summary>
    public bool BikeBoard { get; set; }

    /// <summary>The aspect of a group at server time <paramref name="t"/> (seconds).</summary>
    public SignalAspect State(int group, double t)
    {
        var list = Groups[group].Intervals;
        if (list.Count == 0 || Cycle <= 0) return SignalAspect.Off;
        double c = (t + Offset) % Cycle;
        if (c < 0) c += Cycle;
        int lo = 0, hi = list.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (list[mid].From <= c) lo = mid; else hi = mid - 1;
        }
        return list[lo].Aspect;
    }

    /// <summary>Seconds from <paramref name="t"/> until the group's aspect next changes (a lamp builder sleeps until then).</summary>
    public double UntilChange(int group, double t)
    {
        var list = Groups[group].Intervals;
        if (list.Count <= 1 || Cycle <= 0) return double.PositiveInfinity;
        double c = ((t + Offset) % Cycle + Cycle) % Cycle;
        foreach (var iv in list)
            if (iv.To > c + 1e-4) return iv.To - c;
        return Cycle - c + list[0].To;
    }

    /// <summary>
    /// Whether an approach's bike group (else its through group) is green for at least
    /// <see cref="MinGreen"/> while its right-turn group is red (or red and yellow): a phase that
    /// keeps right-turning cars off a kerbside bike lane (#351). The bike group's lead green
    /// (<see cref="BikeLead"/>) alone is not one; a right turn with no group of its own never is.
    /// </summary>
    public bool ThroughWithRightHeld(int arm)
    {
        int bike = -1, through = -1, right = -1;
        for (int g = 0; g < Groups.Count; g++)
        {
            var group = Groups[g];
            if (group.Arm != arm) continue;
            if (group.Kind == SignalGroupKind.Bike) bike = g;
            else if (group.Kind == SignalGroupKind.RightArrow) right = g;
            else if (group.Kind == SignalGroupKind.Car && (group.Moves & SignalMoves.Through) != 0) through = g;
            else if (group.Kind == SignalGroupKind.Car && group.Moves == SignalMoves.Right) right = g;
        }
        int go = bike >= 0 ? bike : through;
        if (go < 0) return true;
        if (right < 0) return false;   // the right turn shares the through lane's green
        double held = 0;
        foreach (var a in Groups[go].Intervals)
        {
            if (a.Aspect != SignalAspect.Green) continue;
            foreach (var b in Groups[right].Intervals)
                if (b.Aspect is SignalAspect.Red or SignalAspect.RedAmber)
                    held += Math.Max(0, Math.Min(a.To, b.To) - Math.Max(a.From, b.From));
        }
        return held >= MinGreen - 1e-3;
    }

    // ---- movements and conflicts -------------------------------------------------------------

    public readonly record struct Movement(int From, int To, SignalMoves Turn, int Group);

    public enum Conflict : byte
    {
        None = 0,
        /// <summary>May share a green: one side yields (a left turn across oncoming traffic, a turn across a pedestrian green with the flasher).</summary>
        Permissive = 1,
        /// <summary>Never green at the same time.</summary>
        Hard = 2,
    }

    /// <summary>Which arm's traffic turns which way: the straightest out-arm within 50 degrees is through, the others left or right.</summary>
    public static SignalMoves Turn(IReadOnlyList<SignalArm> arms, int from, int to)
    {
        if (from == to) return SignalMoves.None;
        double Delta(int k) => Math.IEEERemainder(arms[k].Heading - (arms[from].Heading + Math.PI), 2 * Math.PI);
        int straight = -1;
        double best = 50 * Math.PI / 180;
        for (int k = 0; k < arms.Count; k++)
            if (k != from && arms[k].Out && Math.Abs(Delta(k)) < best) { best = Math.Abs(Delta(k)); straight = k; }
        if (to == straight) return SignalMoves.Through;
        return Delta(to) > 0 ? SignalMoves.Left : SignalMoves.Right;
    }

    /// <summary>Every vehicle and bike movement through the junction, with the group that serves it.</summary>
    public List<Movement> Movements()
    {
        var list = new List<Movement>();
        for (int g = 0; g < Groups.Count; g++)
        {
            var group = Groups[g];
            if (group.Kind is SignalGroupKind.Pedestrian or SignalGroupKind.Flasher) continue;
            for (int to = 0; to < Arms.Count; to++)
            {
                if (to == group.Arm || !Arms[to].Out) continue;
                var turn = Turn(Arms, group.Arm, to);
                if ((group.Moves & turn) != 0) list.Add(new Movement(group.Arm, to, turn, g));
            }
        }
        return list;
    }

    /// <summary>
    /// The conflict matrix between groups. Paths are chords between the arms' lanes on a circle
    /// round the junction (right-hand traffic: an arm's inbound lane lies counter-clockwise of its
    /// heading, its outbound lane clockwise); two movements conflict where their chords cross or
    /// they end on the same arm. A pedestrian crossing conflicts with every movement entering or
    /// leaving its arm. A turn sharing the main lane (a <see cref="SignalGroupKind.Car"/> group)
    /// yields: across oncoming traffic, and across a pedestrian or bike green.
    /// </summary>
    public Conflict[,] Conflicts()
    {
        int n = Groups.Count;
        var c = new Conflict[n, n];
        void Set(int a, int b, Conflict v)
        {
            if (a == b || v <= c[a, b]) return;
            c[a, b] = c[b, a] = v;
        }

        var moves = Movements();
        const double Lane = 0.08;
        double Entry(int arm) => Arms[arm].Heading + Lane;
        double Exit(int arm) => Arms[arm].Heading - Lane;

        for (int i = 0; i < moves.Count; i++)
        for (int k = i + 1; k < moves.Count; k++)
        {
            var a = moves[i];
            var b = moves[k];
            if (a.Group == b.Group || a.From == b.From) continue;
            bool merge = a.To == b.To;
            bool cross = Crosses(Entry(a.From), Exit(a.To), Entry(b.From), Exit(b.To));
            if (!merge && !cross) continue;
            var ga = Groups[a.Group];
            var gb = Groups[b.Group];
            bool bikes = ga.Kind == SignalGroupKind.Bike || gb.Kind == SignalGroupKind.Bike;
            // a left turn in a shared lane yields to the other's car traffic; two lefts never yield to each other
            bool yields = !bikes && ((a.Turn == SignalMoves.Left && ga.Kind == SignalGroupKind.Car && b.Turn != SignalMoves.Left)
                || (b.Turn == SignalMoves.Left && gb.Kind == SignalGroupKind.Car && a.Turn != SignalMoves.Left));
            // a car turning in a shared lane across a bike's way yields to it (the flasher)
            bool bikeYield = bikes && (ga.Kind == SignalGroupKind.Bike ? Turning(b, gb) : Turning(a, ga));
            Set(a.Group, b.Group, yields || bikeYield ? Conflict.Permissive : Conflict.Hard);
        }

        // a bike lane beside a right-turn pocket: right-turning cars cross it (#351)
        for (int g = 0; g < n; g++)
        {
            if (Groups[g].Kind != SignalGroupKind.Bike) continue;
            for (int h = 0; h < n; h++)
                if (Groups[h].Arm == Groups[g].Arm && Groups[h].Kind == SignalGroupKind.RightArrow) Set(g, h, Conflict.Hard);
                else if (Groups[h].Arm == Groups[g].Arm && Groups[h].Kind == SignalGroupKind.Car && (Groups[h].Moves & SignalMoves.Right) != 0)
                    Set(g, h, Conflict.Permissive);
        }

        for (int p = 0; p < n; p++)
        {
            if (Groups[p].Kind != SignalGroupKind.Pedestrian) continue;
            int arm = Groups[p].Arm;
            foreach (var m in moves)
            {
                if (m.From != arm && m.To != arm) continue;
                var g = Groups[m.Group];
                bool turnsIn = m.To == arm && m.Turn != SignalMoves.Through && g.Kind == SignalGroupKind.Car;
                Set(p, m.Group, turnsIn ? Conflict.Permissive : Conflict.Hard);
            }
        }
        return c;
    }

    private static bool Turning(Movement m, SignalGroup g) => m.Turn != SignalMoves.Through && g.Kind == SignalGroupKind.Car;

    /// <summary>Whether chord (a1, a2) crosses chord (b1, b2) on a circle: their ends interleave.</summary>
    private static bool Crosses(double a1, double a2, double b1, double b2)
    {
        static double Norm(double x) => ((x % (2 * Math.PI)) + 2 * Math.PI) % (2 * Math.PI);
        double lo = Math.Min(Norm(a1), Norm(a2)), hi = Math.Max(Norm(a1), Norm(a2));
        bool In(double x) { x = Norm(x); return x > lo && x < hi; }
        return In(b1) != In(b2);
    }

    // ---- planning ----------------------------------------------------------------------------

    /// <summary>
    /// The groups and the fixed-time plan for a junction (#349). A crossroads of two roads runs
    /// each road in turn: a leading phase for one approach's left-turn pocket, both directions,
    /// then a lagging phase for the other's (each only where that pocket exists); a T runs the
    /// main road, its left turn into the side road, then the side road; any other shape gives
    /// each approach a phase of its own. A right-turn pocket's arrow and the pedestrian lights
    /// are green in every phase nothing conflicts with them; a right pocket with no such phase
    /// goes with its own approach and turns across the pedestrians with the flasher.
    /// </summary>
    /// <param name="seed">A stable number for the junction (e.g. its LV95 key): the cycle offset.</param>
    public static SignalPlan Build(IReadOnlyList<SignalArm> arms, uint seed = 0, bool pedestrianAmber = true)
    {
        var plan = new SignalPlan { PedestrianAmber = pedestrianAmber };
        plan.Arms.AddRange(arms);
        var car = new int[arms.Count];
        var left = new int[arms.Count];
        var right = new int[arms.Count];
        Array.Fill(car, -1); Array.Fill(left, -1); Array.Fill(right, -1);

        // groups per approach: the main lane, then its pockets' arrows, then the bikes beside it
        for (int a = 0; a < arms.Count; a++)
        {
            if (!arms[a].In) continue;
            var moves = SignalMoves.None;
            for (int to = 0; to < arms.Count; to++)
                if (to != a && arms[to].Out) moves |= Turn(arms, a, to);
            // pockets get their own arrows only beside a main lane: an approach whose every
            // movement a pocket would take (a right turn out, say) is one group
            var pockets = (arms[a].LeftPocket ? SignalMoves.Left : 0) | (arms[a].RightPocket ? SignalMoves.Right : 0);
            var main = (moves & ~pockets) == SignalMoves.None ? moves : moves & ~pockets;
            if ((moves & pockets & SignalMoves.Left) != 0 && main != moves) left[a] = plan.Add(SignalGroupKind.LeftArrow, a, SignalMoves.Left);
            if ((moves & pockets & SignalMoves.Right) != 0 && main != moves) right[a] = plan.Add(SignalGroupKind.RightArrow, a, SignalMoves.Right);
            if (main != SignalMoves.None) car[a] = plan.Add(SignalGroupKind.Car, a, main);
            if (arms[a].BikeSignal && (moves & SignalMoves.Through) != 0) plan.Add(SignalGroupKind.Bike, a, SignalMoves.Through);
        }
        for (int a = 0; a < arms.Count; a++)
            if (arms[a].Pedestrians) plan.Add(SignalGroupKind.Pedestrian, a, SignalMoves.None);

        var phases = Phases(plan, arms, car, left, right);
        var conflicts = plan.Conflicts();
        // a scheme that would run two crossing movements together (an odd shape): split phasing
        if (phases.Any(p => p.Base.Any(g => p.Base.Any(h => conflicts[g, h] == Conflict.Hard))))
            phases = Split(plan, arms);
        Expand(plan, phases, conflicts);

        // a right arrow no phase can carry: a full green with its own approach (the flasher marks the pedestrians)
        bool changed = false;
        for (int a = 0; a < arms.Count; a++)
        {
            int r = right[a];
            if (r < 0 || phases.Any(p => p.Green.Contains(r))) continue;
            plan.Groups[r].Kind = SignalGroupKind.Car;
            changed = true;
            foreach (var p in phases)
                if (car[a] >= 0 && p.Green.Contains(car[a])) p.Base.Add(r);
        }
        if (changed)
        {
            conflicts = plan.Conflicts();
            foreach (var p in phases) p.Green.Clear();
            Expand(plan, phases, conflicts);
        }

        // anything still never green (a pedestrian crossing every phase runs over): a phase of its own
        var lonely = Enumerable.Range(0, plan.Groups.Count).Where(g => phases.All(p => !p.Green.Contains(g))).ToList();
        if (lonely.Count > 0)
        {
            var extra = new Phase(0, MinGreen + 2);
            extra.Base.AddRange(lonely.Where(g => lonely.All(h => conflicts[g, h] != Conflict.Hard)));
            phases.Add(extra);
            Expand(plan, phases, conflicts);
        }

        // flashers: beside a group whose turn shares a green with a pedestrian or bike group
        int count = plan.Groups.Count;
        for (int g = 0; g < count; g++)
        {
            if (plan.Groups[g].Kind != SignalGroupKind.Car) continue;
            bool flashes = false;
            for (int h = 0; h < count && !flashes; h++)
                flashes = conflicts[g, h] == Conflict.Permissive
                    && plan.Groups[h].Kind is SignalGroupKind.Pedestrian or SignalGroupKind.Bike
                    && phases.Any(p => p.Green.Contains(g) && p.Green.Contains(h));
            if (flashes) plan.Groups.Add(new SignalGroup { Kind = SignalGroupKind.Flasher, Arm = plan.Groups[g].Arm, Qualifies = g });
        }

        Time(plan, phases, conflicts);
        plan.Offset = plan.Cycle <= 0 ? 0 : (Mix(seed) % (uint)Math.Max(1, plan.Cycle * 10)) / 10f;
        return plan;
    }

    private int Add(SignalGroupKind kind, int arm, SignalMoves moves)
    {
        Groups.Add(new SignalGroup { Kind = kind, Arm = arm, Moves = moves });
        return Groups.Count - 1;
    }

    private sealed class Phase(float weight, float fixedGreen = 0)
    {
        /// <summary>The groups the scheme runs in this phase.</summary>
        public readonly List<int> Base = new();
        /// <summary>The base plus every group added because nothing green conflicts with it.</summary>
        public readonly HashSet<int> Green = new();
        public readonly float Weight = weight;
        /// <summary>A fixed green (a turn phase); 0: a share of what is left of the cycle.</summary>
        public readonly float FixedGreen = fixedGreen;
        public float GreenTime;
    }

    private static List<Phase> Phases(SignalPlan plan, IReadOnlyList<SignalArm> arms, int[] car, int[] left, int[] right)
    {
        var phases = new List<Phase>();
        var used = new bool[arms.Count];
        // roads through the junction: pairs of arms about opposite, the straightest pairs first
        // (a skewed T has an arm nearly opposite each of two others), then by rank
        var pairs = new List<(int A, int B, double Off)>();
        for (int a = 0; a < arms.Count; a++)
        for (int b = a + 1; b < arms.Count; b++)
        {
            double off = Math.Abs(Math.IEEERemainder(arms[b].Heading - arms[a].Heading - Math.PI, 2 * Math.PI));
            if (off < 40 * Math.PI / 180) pairs.Add((a, b, off));
        }
        var axes = new List<(int P, int Q)>();
        foreach (var (a, b, _) in pairs.OrderBy(p => p.Off).ThenByDescending(p => arms[p.A].Rank + arms[p.B].Rank).ThenBy(p => p.A).ThenBy(p => p.B))
        {
            if (used[a] || used[b]) continue;
            used[a] = used[b] = true;
            axes.Add((a, b));
        }
        // the higher ranked road runs first
        axes = axes.OrderByDescending(x => arms[x.P].Rank + arms[x.Q].Rank).ThenBy(x => x.P).ToList();
        var single = Enumerable.Range(0, arms.Count).Where(i => !used[i]).ToList();
        void Add(Phase p, params int[] groups)
        {
            foreach (int g in groups) if (g >= 0 && !p.Base.Contains(g)) p.Base.Add(g);
            if (p.Base.Count > 0) phases.Add(p);
        }
        float Rank(int a) => arms[a].In ? arms[a].Rank : 0;

        if (arms.Count == 4 && axes.Count == 2)
        {
            foreach (var (p, q) in axes)
            {
                float weight = Math.Max(1, Rank(p) + Rank(q));
                if (!arms[p].In || !arms[q].In)
                {
                    // one way in along this road: nothing opposes its left turn
                    int a = arms[p].In ? p : q;
                    if (arms[a].In) Add(new Phase(weight), car[a], left[a]);
                    continue;
                }
                if (left[p] >= 0) Add(new Phase(0, TurnPhaseGreen), car[p], left[p]);
                Add(new Phase(weight), car[p], car[q]);
                if (left[q] >= 0) Add(new Phase(0, TurnPhaseGreen), car[q], left[q]);
            }
        }
        else if (arms.Count == 3 && axes.Count == 1 && single.Count == 1)
        {
            var (p, q) = axes[0];
            int s = single[0];
            // the main approach that turns left into the side road
            int ml = arms[p].In && Turn(arms, p, s) == SignalMoves.Left ? p : arms[q].In && Turn(arms, q, s) == SignalMoves.Left ? q : -1;
            Add(new Phase(Math.Max(1, Rank(p) + Rank(q))), car[p], car[q]);
            if (ml >= 0) Add(new Phase(0, TurnPhaseGreen), car[ml], left[ml]);
            Add(new Phase(Math.Max(1, Rank(s))), car[s], left[s], right[s]);
        }
        else phases.AddRange(Split(plan, arms));
        return phases;
    }

    /// <summary>Split phasing: each approach on its own, every lane of it.</summary>
    private static List<Phase> Split(SignalPlan plan, IReadOnlyList<SignalArm> arms)
    {
        var phases = new List<Phase>();
        for (int a = 0; a < arms.Count; a++)
        {
            if (!arms[a].In) continue;
            var phase = new Phase(Math.Max(1, (float)arms[a].Rank));
            phase.Base.AddRange(Enumerable.Range(0, plan.Groups.Count)
                .Where(g => plan.Groups[g].Arm == a && plan.Groups[g].Kind is SignalGroupKind.Car or SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow));
            if (phase.Base.Count > 0) phases.Add(phase);
        }
        return phases;
    }

    /// <summary>Each phase's green: its base, then pedestrians, bikes and right arrows that nothing green stops.</summary>
    private static void Expand(SignalPlan plan, List<Phase> phases, Conflict[,] conflicts)
    {
        foreach (var phase in phases)
        {
            phase.Green.Clear();
            foreach (int g in phase.Base) phase.Green.Add(g);
            foreach (var kind in (ReadOnlySpan<SignalGroupKind>)[SignalGroupKind.Pedestrian, SignalGroupKind.Bike, SignalGroupKind.RightArrow])
                for (int g = 0; g < plan.Groups.Count; g++)
                {
                    if (plan.Groups[g].Kind != kind || phase.Green.Contains(g)) continue;
                    bool protectedOnly = kind == SignalGroupKind.RightArrow;
                    // a bike goes with its own approach's main lane
                    if (kind == SignalGroupKind.Bike && !phase.Green.Any(h => plan.Groups[h].Kind == SignalGroupKind.Car && plan.Groups[h].Arm == plan.Groups[g].Arm))
                        continue;
                    if (phase.Green.All(h => conflicts[g, h] == Conflict.None || (!protectedOnly && conflicts[g, h] == Conflict.Permissive
                            && plan.Groups[h].Kind == SignalGroupKind.Car)))
                        phase.Green.Add(g);
                }
        }
    }

    // ---- timing ------------------------------------------------------------------------------

    private float ClearanceOf(int g)
    {
        var group = Groups[g];
        return group.Kind switch
        {
            SignalGroupKind.Pedestrian => Math.Clamp(Arms[group.Arm].CrossingM * (2f / 3f) / WalkSpeed, 2f, 8f),
            SignalGroupKind.Bike => YellowBike,
            SignalGroupKind.Flasher => 0,
            _ => Yellow(Arms[group.Arm].SpeedKmh),
        };
    }

    /// <summary>
    /// The all-red after a group ends (the next group's red-yellow, 2 s, starts that much before it
    /// is over, so 2 s means no overlap): a turn arrow (a lagging left turn, say) ends with its last
    /// car a few metres past the stop line and the cars that follow need a moment to reach the
    /// conflict (the entry time), so 1 s: the red-yellow overlaps its last second of yellow.
    /// </summary>
    private float AllRedAfter(int g) => Groups[g].Kind is SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow or SignalGroupKind.Pedestrian ? AllRedAfterArrow : AllRed;

    private float RedAmberOf(int g) => Groups[g].Kind switch
    {
        SignalGroupKind.Pedestrian or SignalGroupKind.Flasher => 0,
        SignalGroupKind.Bike => RedAmberBike,
        _ => RedAmberCar,
    };

    private static void Time(SignalPlan plan, List<Phase> phases, Conflict[,] conflicts)
    {
        int n = phases.Count;
        if (n == 0) return;
        // intergreen after each phase: the longest clearance of a group that stops, then all red
        var inter = new float[n];
        for (int i = 0; i < n; i++)
        {
            var next = phases[(i + 1) % n];
            var ending = phases[i].Green.Where(g => !next.Green.Contains(g)).ToList();
            inter[i] = ending.Count == 0 || n == 1 ? 0 : ending.Max(g => plan.ClearanceOf(g) + plan.AllRedAfter(g));
            // a bike group starting next leads its cars (Vorgrün): they wait that much longer
            if (n > 1 && next.Green.Any(g => plan.Groups[g].Kind == SignalGroupKind.Bike && !phases[i].Green.Contains(g))) inter[i] += BikeLead;
        }

        float target = n <= 2 ? 60 : n <= 4 ? 60 : 70;
        float fixedSum = phases.Sum(p => p.FixedGreen) + inter.Sum();
        float weights = phases.Sum(p => p.FixedGreen > 0 ? 0 : p.Weight);
        float cycle = target;
        for (int pass = 0; pass < 2; pass++)
        {
            float share = Math.Max(0, cycle - fixedSum);
            foreach (var p in phases)
                p.GreenTime = p.FixedGreen > 0 ? p.FixedGreen : weights > 0 ? Math.Max(MinGreen, share * p.Weight / weights) : MinGreen;
            // a pedestrian green at least as long as its crossing's clearance, never under the minimum
            foreach (var p in phases)
            {
                float need = p.Green.Where(g => plan.Groups[g].Kind == SignalGroupKind.Pedestrian).Select(_ => 5f).DefaultIfEmpty(MinGreen).Max();
                p.GreenTime = MathF.Round(Math.Max(p.GreenTime, need), 1);
            }
            float total = phases.Sum(p => p.GreenTime) + inter.Sum();
            if (total <= MaxCycle || pass == 1) { cycle = total; break; }
            cycle = MaxCycle;
        }
        plan.Cycle = MathF.Round(cycle, 1);

        // phase i green from start[i] to start[i] + green; then its intergreen
        var start = new float[n];
        for (int i = 1; i < n; i++) start[i] = start[i - 1] + phases[i - 1].GreenTime + inter[i - 1];

        int groups = plan.Groups.Count;
        for (int g = 0; g < groups; g++)
        {
            if (plan.Groups[g].Kind == SignalGroupKind.Flasher) continue;
            // the green spans: a run of phases green in a row, through their intergreens
            var spans = new List<(float From, float To)>();
            if (phases.All(p => p.Green.Contains(g))) spans.Add((0, plan.Cycle));
            else for (int i = 0; i < n; i++)
            {
                if (!phases[i].Green.Contains(g) || phases[(i - 1 + n) % n].Green.Contains(g) && n > 1) continue;
                float from = start[i];
                int k = i;
                while (n > 1 && phases[(k + 1) % n].Green.Contains(g) && (k + 1) % n != i) k = (k + 1) % n;
                float to = start[k] + phases[k].GreenTime;
                if (k < i) to += plan.Cycle;
                // a bike leads its approach by up to BikeLead s, once whatever it conflicts with has cleared
                if (plan.Groups[g].Kind == SignalGroupKind.Bike)
                {
                    int prev = (i - 1 + n) % n;
                    float prevEnd = start[prev] + phases[prev].GreenTime - (prev > i ? plan.Cycle : 0);
                    float clear = phases[prev].Green.Where(h => conflicts[g, h] == Conflict.Hard && !phases[i].Green.Contains(h))
                        .Select(plan.ClearanceOf).DefaultIfEmpty(-AllRed).Max();
                    from = Math.Max(from - BikeLead, prevEnd + clear + AllRed + RedAmberBike);
                }
                spans.Add((from, to));
            }
            plan.Groups[g].Intervals.Clear();
            plan.Groups[g].Intervals.AddRange(Lay(spans, plan.Cycle, plan.ClearanceOf(g), plan.RedAmberOf(g),
                plan.Groups[g].Kind == SignalGroupKind.Pedestrian && !plan.PedestrianAmber));
        }

        // flashers: on while their group is green and a pedestrian or bike group it yields to is green too
        for (int f = 0; f < groups; f++)
        {
            var fl = plan.Groups[f];
            if (fl.Kind != SignalGroupKind.Flasher) continue;
            var cuts = new SortedSet<float> { 0, plan.Cycle };
            var watch = Enumerable.Range(0, conflicts.GetLength(0)).Where(h => conflicts[fl.Qualifies, h] == Conflict.Permissive
                && plan.Groups[h].Kind is SignalGroupKind.Pedestrian or SignalGroupKind.Bike).Append(fl.Qualifies).ToList();
            foreach (int h in watch) foreach (var iv in plan.Groups[h].Intervals) { cuts.Add(iv.From); cuts.Add(iv.To); }
            var list = new List<SignalInterval>();
            float prevCut = -1;
            foreach (float c in cuts)
            {
                if (prevCut >= 0 && c > prevCut)
                {
                    double mid = (prevCut + c) * 0.5 - plan.Offset;
                    bool on = plan.State(fl.Qualifies, mid) == SignalAspect.Green
                        && watch.Any(h => h != fl.Qualifies && plan.State(h, mid) == SignalAspect.Green);
                    var aspect = on ? SignalAspect.FlashingAmber : SignalAspect.Off;
                    if (list.Count > 0 && list[^1].Aspect == aspect) list[^1] = list[^1] with { To = c };
                    else list.Add(new SignalInterval(aspect, prevCut, c));
                }
                prevCut = c;
            }
            fl.Intervals.Clear();
            fl.Intervals.AddRange(list);
        }
    }

    /// <summary>Green spans (may run past the cycle's end) as a closed list of intervals over [0, cycle).</summary>
    private static List<SignalInterval> Lay(List<(float From, float To)> spans, float cycle, float clearance, float redAmber, bool redAfterGreen)
    {
        var raw = new List<SignalInterval>();
        foreach (var (from, to) in spans)
        {
            if (to - from >= cycle - 1e-3) return [new SignalInterval(SignalAspect.Green, 0, cycle)];
            if (redAmber > 0) raw.Add(new SignalInterval(SignalAspect.RedAmber, from - redAmber, from));
            raw.Add(new SignalInterval(SignalAspect.Green, from, to));
            if (!redAfterGreen) raw.Add(new SignalInterval(SignalAspect.Amber, to, to + clearance));
        }
        // wrap into [0, cycle), split at the seam
        var cut = new List<SignalInterval>();
        foreach (var iv in raw)
        {
            float a = Wrap(iv.From, cycle), len = iv.To - iv.From;
            if (len <= 1e-4) continue;
            if (a + len <= cycle + 1e-4) cut.Add(iv with { From = a, To = Math.Min(cycle, a + len) });
            else { cut.Add(iv with { From = a, To = cycle }); cut.Add(iv with { From = 0, To = a + len - cycle }); }
        }
        cut.Sort((x, y) => x.From.CompareTo(y.From));
        // red fills the gaps
        var list = new List<SignalInterval>();
        float t = 0;
        foreach (var iv in cut)
        {
            if (iv.From > t + 1e-4) list.Add(new SignalInterval(SignalAspect.Red, t, iv.From));
            list.Add(iv with { From = Math.Max(iv.From, t) });
            t = Math.Max(t, iv.To);
        }
        if (t < cycle - 1e-4) list.Add(new SignalInterval(SignalAspect.Red, t, cycle));
        return list.Select(iv => iv with { From = MathF.Round(iv.From, 1), To = MathF.Round(iv.To, 1) }).Where(iv => iv.To > iv.From).ToList();
    }

    private static float Wrap(float t, float cycle) => ((t % cycle) + cycle) % cycle;

    private static uint Mix(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
        return x;
    }

    /// <summary>One line per group: kind, arm, moves, and its intervals (for checks and logs).</summary>
    public string Describe()
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.Append(c, $"cycle {Cycle:F1} s, offset {Offset:F1} s, {Groups.Count} groups\n");
        for (int g = 0; g < Groups.Count; g++)
        {
            var group = Groups[g];
            sb.Append(c, $"  {g,2} {group.Kind,-10} arm {group.Arm} {(group.Kind == SignalGroupKind.Flasher ? $"of {group.Qualifies}" : group.Moves.ToString()),-20}");
            foreach (var iv in group.Intervals)
                sb.Append(c, $" {Short(iv.Aspect)}{iv.From:F1}-{iv.To:F1}");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Short(SignalAspect a) => a switch
    {
        SignalAspect.Red => "R", SignalAspect.RedAmber => "RA", SignalAspect.Green => "G",
        SignalAspect.Amber => "A", SignalAspect.FlashingAmber => "F", _ => "-",
    };

    // ---- self-check --------------------------------------------------------------------------

    /// <summary>
    /// What is wrong with a plan, empty when nothing is: two hard-conflicting groups never green,
    /// yellow or red-yellow at once; every green follows red-yellow (cars, bikes) and is followed
    /// by yellow (and pedestrians on yellow-lens heads); greens last at least the minimum; every
    /// group that can be green is; the intervals close the cycle. Sampled every 0.1 s.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        var conflicts = Conflicts();
        int n = Groups.Count;
        for (int g = 0; g < n; g++)
        {
            var list = Groups[g].Intervals;
            if (list.Count == 0) { errors.Add($"group {g} has no intervals"); continue; }
            if (Math.Abs(list[0].From) > 1e-3 || Math.Abs(list[^1].To - Cycle) > 1e-3) errors.Add($"group {g} does not cover the cycle");
            for (int i = 1; i < list.Count; i++)
                if (Math.Abs(list[i].From - list[i - 1].To) > 1e-3) errors.Add($"group {g} has a gap at {list[i].From}");
            if (Groups[g].Kind == SignalGroupKind.Flasher) continue;
            if (!list.Any(iv => iv.Aspect == SignalAspect.Green)) errors.Add($"group {g} ({Groups[g].Kind} arm {Groups[g].Arm}) is never green");
            // the cycle as a ring: a green split by the seam is one green
            var ring = list.ToList();
            if (ring.Count > 1 && ring[0].Aspect == ring[^1].Aspect)
            {
                ring[^1] = ring[^1] with { To = ring[0].To + Cycle };
                ring.RemoveAt(0);
            }
            for (int i = 0; i < ring.Count; i++)
            {
                if (ring[i].Aspect != SignalAspect.Green || ring.Count == 1) continue;
                var before = ring[(i - 1 + ring.Count) % ring.Count].Aspect;
                var after = ring[(i + 1) % ring.Count].Aspect;
                bool ped = Groups[g].Kind == SignalGroupKind.Pedestrian;
                if (!ped && before != SignalAspect.RedAmber) errors.Add($"group {g} turns green from {before}");
                if ((!ped || PedestrianAmber) && after != SignalAspect.Amber) errors.Add($"group {g} leaves green to {after}");
                if (ring[i].To - ring[i].From < MinGreen - 1e-3) errors.Add($"group {g} green for {ring[i].To - ring[i].From:F1} s");
            }
        }
        static bool Live(SignalAspect a) => a is SignalAspect.Green or SignalAspect.Amber or SignalAspect.RedAmber;
        // a turn arrow's last yellow second may overlap the next group's red-yellow (AllRedAfterArrow)
        bool EntryOverlap(int a, int b, double t) =>
            (State(a, t) == SignalAspect.Amber && Groups[a].Kind is SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow or SignalGroupKind.Pedestrian && State(b, t) == SignalAspect.RedAmber)
            || (State(b, t) == SignalAspect.Amber && Groups[b].Kind is SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow or SignalGroupKind.Pedestrian && State(a, t) == SignalAspect.RedAmber);
        var seen = new HashSet<(int, int)>();
        for (double t = 0.05; t < Cycle; t += 0.1)
            for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
                if (conflicts[a, b] == Conflict.Hard && Live(State(a, t - Offset)) && Live(State(b, t - Offset)) && !EntryOverlap(a, b, t - Offset) && seen.Add((a, b)))
                    errors.Add($"groups {a} ({Groups[a].Kind} arm {Groups[a].Arm}) and {b} ({Groups[b].Kind} arm {Groups[b].Arm}) conflict at {t:F1} s");
        return errors;
    }
}

/// <summary>What a signal pole carries (#350): the heads for its arm's approach, and a pedestrian head.</summary>
[Flags]
public enum SignalPoleFlags : byte
{
    None = 0,
    /// <summary>Near side, on the approach's right: the main head, its pockets' arrows, the flashers.</summary>
    Main = 1,
    /// <summary>On the approach's left (the other kerb, or a median): the main head and the left arrow, never the right arrow.</summary>
    Second = 2,
    /// <summary>A pedestrian head for the crossing of its arm, facing across.</summary>
    Pedestrian = 4,
}

/// <summary>
/// A signal pole (#350), tile-local like the record it belongs to: its foot (on the sidewalk or
/// the verge), the heading its car heads face (toward the approaching drivers) and its pedestrian
/// head faces (across the crossing), as <see cref="RoadPointProp.Heading"/> (radians about +Y, 0
/// facing -Z), and the plan arm it serves.
/// </summary>
public readonly record struct SignalPole(float X, float Y, float Z, float CarHeading, float PedHeading, byte Arm, SignalPoleFlags Flags)
{
    public const int RecordSize = 22;
}

/// <summary>
/// A signalised junction in a <c>.road</c> tile (#349, section <c>SGNL</c>): its centre and,
/// per arm, the middle of the stop line across its approach lanes (tile-local, NaN where nothing
/// approaches), its plan, and (version 2, #350) its poles. The lanes of each approach (#353) are
/// in their own section, <c>LANE</c> (<see cref="RoadApproach"/>).
/// </summary>
public sealed class RoadSignal
{
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    /// <summary>Arm count × 3: the stop line's middle per arm.</summary>
    public required float[] Stops { get; init; }
    public required SignalPlan Plan { get; init; }
    public List<SignalPole> Poles { get; init; } = new();

    /// <summary>2 adds the poles (#350); 1 is still read.</summary>
    public const byte SectionVersion = 2;

    [Flags]
    private enum ArmBits : byte { In = 1, Out = 2, LeftPocket = 4, RightPocket = 8, Pedestrians = 16, Bike = 32 }

    public static void Write(BinaryWriter w, IReadOnlyList<RoadSignal> signals)
    {
        w.Write((uint)signals.Count);
        w.Write(SectionVersion);
        foreach (var s in signals)
        {
            var p = s.Plan;
            w.Write(s.X); w.Write(s.Y); w.Write(s.Z);
            w.Write(p.Cycle); w.Write(p.Offset);
            w.Write((byte)((p.PedestrianAmber ? 1 : 0) | (p.BikeBoard ? 2 : 0)));
            w.Write(checked((byte)p.Arms.Count));
            w.Write(checked((byte)p.Groups.Count));
            for (int i = 0; i < p.Arms.Count; i++)
            {
                var a = p.Arms[i];
                w.Write((float)a.Heading);
                var bits = (a.In ? ArmBits.In : 0) | (a.Out ? ArmBits.Out : 0) | (a.LeftPocket ? ArmBits.LeftPocket : 0)
                    | (a.RightPocket ? ArmBits.RightPocket : 0) | (a.Pedestrians ? ArmBits.Pedestrians : 0) | (a.BikeSignal ? ArmBits.Bike : 0);
                w.Write((byte)bits);
                w.Write((byte)Math.Clamp(MathF.Round(a.SpeedKmh), 0, 255));
                w.Write(a.Rank);
                w.Write((ushort)Math.Clamp(MathF.Round(a.CrossingM * 10), 0, ushort.MaxValue));
                w.Write(s.Stops[i * 3]); w.Write(s.Stops[i * 3 + 1]); w.Write(s.Stops[i * 3 + 2]);
            }
            foreach (var g in p.Groups)
            {
                w.Write((byte)g.Kind);
                w.Write(checked((byte)g.Arm));
                w.Write((byte)g.Moves);
                w.Write(checked((sbyte)g.Qualifies));
                w.Write(checked((ushort)g.Intervals.Count));
                foreach (var iv in g.Intervals)
                {
                    w.Write((byte)iv.Aspect);
                    w.Write((ushort)MathF.Round(iv.From * 10));
                    w.Write((ushort)MathF.Round(iv.To * 10));
                }
            }
            w.Write(checked((ushort)s.Poles.Count));
            foreach (var pole in s.Poles)
            {
                w.Write(pole.X); w.Write(pole.Y); w.Write(pole.Z);
                w.Write(pole.CarHeading); w.Write(pole.PedHeading);
                w.Write(pole.Arm); w.Write((byte)pole.Flags);
            }
        }
    }

    /// <summary>Null when the section is of a newer version this reader does not know (the caller skips it).</summary>
    public static List<RoadSignal>? Read(BinaryReader r)
    {
        uint n = r.ReadUInt32();
        byte version = r.ReadByte();
        if (version is < 1 or > SectionVersion) return null;
        var list = new List<RoadSignal>((int)n);
        for (uint k = 0; k < n; k++)
        {
            float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle();
            float cycle = r.ReadSingle(), offset = r.ReadSingle();
            byte kinds = r.ReadByte();
            bool pedAmber = (kinds & 1) != 0, bikeBoard = (kinds & 2) != 0;
            int arms = r.ReadByte(), groups = r.ReadByte();
            var plan = new SignalPlan { Cycle = cycle, Offset = offset, PedestrianAmber = pedAmber, BikeBoard = bikeBoard };
            var stops = new float[arms * 3];
            for (int i = 0; i < arms; i++)
            {
                double heading = r.ReadSingle();
                var bits = (ArmBits)r.ReadByte();
                float speed = r.ReadByte();
                byte rank = r.ReadByte();
                float crossing = r.ReadUInt16() / 10f;
                plan.Arms.Add(new SignalArm(heading, bits.HasFlag(ArmBits.In), bits.HasFlag(ArmBits.Out), bits.HasFlag(ArmBits.LeftPocket),
                    bits.HasFlag(ArmBits.RightPocket), bits.HasFlag(ArmBits.Pedestrians), bits.HasFlag(ArmBits.Bike), speed, crossing, rank));
                stops[i * 3] = r.ReadSingle(); stops[i * 3 + 1] = r.ReadSingle(); stops[i * 3 + 2] = r.ReadSingle();
            }
            for (int g = 0; g < groups; g++)
            {
                var group = new SignalGroup
                {
                    Kind = (SignalGroupKind)r.ReadByte(), Arm = r.ReadByte(), Moves = (SignalMoves)r.ReadByte(), Qualifies = r.ReadSByte(),
                };
                int count = r.ReadUInt16();
                for (int i = 0; i < count; i++)
                    group.Intervals.Add(new SignalInterval((SignalAspect)r.ReadByte(), r.ReadUInt16() / 10f, r.ReadUInt16() / 10f));
                plan.Groups.Add(group);
            }
            var poles = new List<SignalPole>();
            if (version >= 2)
                for (int i = r.ReadUInt16(); i > 0; i--)
                    poles.Add(new SignalPole(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
                        r.ReadByte(), (SignalPoleFlags)r.ReadByte()));
            list.Add(new RoadSignal { X = x, Y = y, Z = z, Stops = stops, Plan = plan, Poles = poles });
        }
        return list;
    }
}
