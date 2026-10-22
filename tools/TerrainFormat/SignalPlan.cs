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
    /// <summary>
    /// The trams of an approach (#1057): a tram bar signal (white bars: across stop, upright or slanted go, a dot stop expected), its
    /// moves the trams' through the junction. Green with its approach's phases where nothing it crosses is, else a phase of its own.
    /// </summary>
    Tram = 6,
}

/// <summary>
/// The white plate under a flasher lamp (#1109, the user): a pedestrian where its turns cross a walkers' crossing green with
/// them, a bike where they cross a bike lane or way, the turns' arrow where they cross both.
/// </summary>
public enum SignalPlate : byte { None = 0, Pedestrian = 1, Bike = 2, Arrow = 3 }

/// <summary>Where a left turn's flasher stands (#1109, <see cref="RoadSignal.LeftFlasherPole"/>).</summary>
public enum FlasherSite : byte { MainPole = 0, SecondPole = 1, CrossingPole = 2 }

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
    byte Rank = 1,
    /// <summary>Turns this approach may not make (#700: its lane data or an OSM restriction): its groups leave them out. Not stored: the groups carry it.</summary>
    SignalMoves Banned = SignalMoves.None,
    /// <summary>Riders may turn right on red (SSV Art. 69a, signal 5.18, #711): the sign stands beside the red lamp.</summary>
    bool BikeRightOnRed = false,
    /// <summary>
    /// The riders turning left (a left-turn bike lane) have a bike group of their own, with the left arrow's phase, so their slow
    /// clearing can end before the cars' (#979). Not stored: the groups carry it.
    /// </summary>
    bool RiderLeft = false,
    /// <summary>
    /// The bike lane runs between the through lane and the right pocket (layout b, #351): the cars turning right cross it before
    /// the junction, not at the mouth, so the riders' green and the right arrow's are free to meet (#979). Bit 7 of the arm's byte.
    /// </summary>
    bool BikeBetween = false,
    /// <summary>
    /// How far behind the stop line the main lane can hold a queue (m): its run back to the junction behind (#982); 0 unknown.
    /// Not stored: the greens carry it.
    /// </summary>
    float StorageM = 0,
    /// <summary>The left-turn lane's queue room behind the stop line (m, #982): its pocket's storage; 0 unknown. Not stored.</summary>
    float LeftStorageM = 0,
    /// <summary>The right pocket's queue room behind the stop line (m, #982); 0 unknown. Not stored.</summary>
    float RightStorageM = 0,
    /// <summary>
    /// A two-stage crossing (#1030): the walkers cross the arriving lanes, kerb to island, and the departing ones, island to kerb,
    /// each half its own group; these are the halves' lengths (m), 0 for a crossing in one go. Not stored: the groups carry it.
    /// </summary>
    float HalfInM = 0,
    float HalfOutM = 0,
    /// <summary>
    /// The riders turning left wait in a bike box (SSV 6.26), in front of the left pocket's cars: their group never ends before
    /// those cars' (they would be hit from behind, the user); it may start before them. Not stored: only the timing reads it.
    /// </summary>
    bool RiderBox = false,
    /// <summary>
    /// The moves the trams make from this arm (#1057: its tram tracks into the junction, as RoadGen's tram ways through the core): a
    /// tram group of their own. Not stored: the groups carry it.
    /// </summary>
    SignalMoves TramMoves = SignalMoves.None,
    /// <summary>
    /// Cars may drive into this arm's exit without room on it for them (#1134, the user: "allow block intersection"): the box rule
    /// (<see cref="BoxRule"/>) is off for cars heading out here. Bit 7 of the rank byte (ranks stay under 128); nothing sets it yet.
    /// </summary>
    bool AllowBlock = false)
{
    /// <summary>Whether its walkers cross in two stages, waiting on the island between (#1030).</summary>
    public bool TwoStage => Pedestrians && HalfInM > 0 && HalfOutM > 0;
}

/// <summary>Which part of its arm's crossing a pedestrian group serves (#1030).</summary>
public enum CrossingHalf : byte
{
    /// <summary>Kerb to kerb, in one go.</summary>
    Whole = 0,
    /// <summary>Over the arriving lanes, the approach's right kerb to the island: stopped only by the approach's own movements.</summary>
    Arriving = 1,
    /// <summary>Over the departing lanes, the island to the left kerb: stopped by the movements into the arm.</summary>
    Departing = 2,
}

public readonly record struct SignalInterval(SignalAspect Aspect, float From, float To);

/// <summary>A signal group (Signalgruppe): heads that always show the same aspect.</summary>
public sealed class SignalGroup
{
    public SignalGroupKind Kind { get; set; }
    /// <summary>The approach it controls; for a pedestrian group, the arm it crosses.</summary>
    public int Arm { get; init; }
    public SignalMoves Moves { get; set; }
    /// <summary>For a pedestrian group, the part of the crossing it serves (#1030; stored in the moves byte, which it leaves None).</summary>
    public CrossingHalf Half { get; init; }
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

    /// <summary>
    /// A tram group's amber (#1105, the user: "5s at 50, 6s at 60 and 7s above seems fine"; no source): the cars' speed bands, a
    /// tram's 1.2 m/s² service brake needing longer to stop or clear than a car's. Its passing time and clearance too.
    /// </summary>
    public static float TramYellow(float speedKmh) => speedKmh <= 50.5f ? 5f : speedKmh <= 60.5f ? 6f : 7f;

    public const float RedAmberCar = 2f, RedAmberBike = 1f, YellowBike = 2f;
    public const float MinGreen = 4f, AllRed = 2f, AllRedAfterArrow = 1f, BikeLead = 3f;
    /// <summary>
    /// A tram's lead (#1057, the user: "a short leading signal (1 sec)"): its bar goes upright this long before its approach's
    /// cars get green, where nothing it crosses is still clearing; else less, or none. Where a car starting with it turns across its
    /// way, the riders' lead instead (<see cref="BikeLead"/>, the user: "like for bikes").
    /// </summary>
    public const float TramLead = 1f;

    /// <summary>Whether the trams lead (#1057; RoadGen turns it off where the leads would take the cycle past <see cref="MaxCycle"/>). Not stored.</summary>
    public bool TramLeads { get; set; } = true;
    /// <summary>A pedestrian clears 2/3 of the crossing at this pace in the clearance time (2-8 s).</summary>
    public const float WalkSpeed = 1.2f;
    /// <summary>The green of a leading or lagging left-turn phase whose pocket's storage is unknown; the time the cycle budgets for one.</summary>
    public const float TurnPhaseGreen = 5f;
    public const float MinCycle = 30f, MaxCycle = 120f;
    /// <summary>
    /// The first walker of a two-stage crossing's half waits on the island at most this share of the cycle for the other half
    /// (#1030, the user); where the plan would keep them longer, the phase that lets them on gets longer (<see cref="Stretch"/>).
    /// </summary>
    public const float IslandWaitShare = 1f / 3f;
    /// <summary>
    /// Keeping an island wait may lengthen the cycle by this much at most (s, the user: where the other phases have no green to
    /// give); past that the arm crosses in one go (RoadGen), as without the island.
    /// </summary>
    public const float IslandGrowMost = 3f;
    private const int StretchPasses = 8;
    /// <summary>A queue (#982): a car per <see cref="CarSpacing"/> m of lane, leaving every <see cref="Headway"/> s (saturation) after the start-up loss.</summary>
    public const float CarSpacing = 6f, Headway = 2f, StartLoss = 2f;
    /// <summary>
    /// The most queue a turn phase clears (m, #982): the longest left pocket RoadGen lays (<c>PocketRules.Left</c>). A turn lane
    /// running the whole street back (OSM's lanes) is not all left-turners.
    /// </summary>
    public const float MaxTurnStorage = 40f;

    /// <summary>
    /// The green that clears a full queue of <paramref name="storageM"/> m (#982): the start-up loss, then a car every
    /// <see cref="Headway"/> s; never under <see cref="MinGreen"/>, infinite where the storage is unknown (0).
    /// </summary>
    public static float ClearingGreen(float storageM) =>
        storageM <= 0 ? float.PositiveInfinity : Math.Max(MinGreen, StartLoss + storageM / CarSpacing * Headway);

    // ---- the plan ----------------------------------------------------------------------------

    public List<SignalArm> Arms { get; init; } = new();
    public List<SignalGroup> Groups { get; init; } = new();
    public float Cycle { get; set; }
    /// <summary>Seconds added to the clock before the lookup: junctions are not coordinated (#356).</summary>
    public float Offset { get; set; }
    /// <summary>Pedestrian heads have a yellow lens (3-lens canton heads, e.g. Vaud); else red after green (e.g. Geneva).</summary>
    public bool PedestrianAmber { get; set; } = true;
    /// <summary>
    /// The canton's white plates run on below each car and bike head and carry its arrow (the lenses
    /// keep theirs), and its bike heads stand on one too; elsewhere a bike head has a small arrow
    /// plate of its own (#759). Bit 1 of the byte that holds <see cref="PedestrianAmber"/>:
    /// data written before reads false.
    /// </summary>
    public bool ArrowPlates { get; set; }

    /// <summary>
    /// Romandy's tram head (#1057, #1105 after the user's Geneva photo): a square head, a white dotted T in its round window, the
    /// stroke across lit for stop, the stroke upright for go; the upright one flashing before red (the amber), the one across before green (the red-amber, longer than the cars':
    /// <see cref="TramRedAmberT"/>). Elsewhere the three-lamp bar head. Bit 6 of the byte that holds <see cref="PedestrianAmber"/>.
    /// </summary>
    public bool TramTHead { get; set; }

    /// <summary>The flashing bar across before a tram's green on Romandy's T head (s, #1057): "way longer than for cars" (the user); no source found.</summary>
    public const float TramRedAmberT = 5f;

    /// <summary>
    /// Timed from the conflict times between its groups (#979, <see cref="Retime"/>): a group's green may begin while one it
    /// conflicts with is still amber, as far as the geometry allows. Bit 2 of the byte that holds <see cref="PedestrianAmber"/>.
    /// </summary>
    public bool ConflictTimed { get; set; }

    /// <summary>
    /// The canton's colour for its signal poles and overhead structures (#1037, <see cref="SignalPoleColour"/>). Bits 3-5 of the
    /// byte that holds <see cref="PedestrianAmber"/>: data written before reads the default, RAL 7001.
    /// </summary>
    public SignalPoleColour PoleColour { get; set; }

    /// <summary>Prints each phase change's wait and the pair that sets it while timing (a debugging aid).</summary>
    public static bool DebugTiming;

    /// <summary>The conflict times it was timed from (<see cref="Retime"/>), by ending and starting group; not written.</summary>
    public float[,]? ConflictTimes { get; private set; }

    // what Build planned, kept to time it again (not written: a plan read from a tile has none)
    private List<Phase>? _phases;
    private Conflict[,]? _conflicts;
    private uint _seed;

    /// <summary>
    /// Times the plan again from <paramref name="times"/> (#979): seconds from the end of an ending group's green to the start
    /// of a starting one's it conflicts with, NaN where there is none (<c>RoadGen</c>'s <c>ConflictTimes</c>). Each group then
    /// starts as soon as its own conflicts with the groups ending allow. False (and the plan as it was) for a plan not built
    /// here, or one the times leave invalid.
    /// </summary>
    public bool Retime(float[,] times)
    {
        if (_phases is null || _conflicts is null || times.GetLength(0) != Groups.Count || times.GetLength(1) != Groups.Count) return false;
        ConflictTimes = times;
        ConflictTimed = true;
        foreach (var p in _phases) p.Extra = 0;
        float unstretched = float.NaN;
        (int Phase, float Add)? last = null;
        for (int stretch = 0; ; stretch++)
        {
            // a slow group's early end is held back by its minimum green: limit it to what that green leaves and time again (the
            // phase change then waits for it); failing that, every group ends with its phase
            var limits = new Dictionary<(int, int), float>();
            for (int pass = 0; pass < 4; pass++)
            {
                var slowEnds = new List<(int Phase, int Group, float From, float End)>();
                Time(this, _phases, _conflicts, times, true, limits, slowEnds);
                if (KeepsTimes(times)) break;
                bool more = false;
                foreach (var (k, g, from, end) in slowEnds)
                {
                    float most = Math.Max(0f, end - from - MinGreen);
                    if (most < limits.GetValueOrDefault((k, g), float.PositiveInfinity) - 0.05f) { limits[(k, g)] = most; more = true; }
                }
                if (!more) break;
            }
            if (!KeepsTimes(times)) Time(this, _phases, _conflicts, times, early: false);
            // the island waits kept (#1030): stretched, timed again, taken back where the cycle grew
            if (float.IsNaN(unstretched)) unstretched = Cycle;
            else if (last is { } done && !Kept(this, _phases, done, unstretched)) { last = null; stretch = StretchPasses; continue; }
            if (stretch >= StretchPasses || (last = Stretch(this, _phases)) is null) break;
        }
        Offset = Cycle <= 0 ? 0 : (Mix(_seed) % (uint)Math.Max(1, Cycle * 10)) / 10f;
        // (never past the legal maximum, the user)
        if (Validate().Count == 0 && KeepsTimes(times) && Cycle <= MaxCycle + 0.05f) return true;
        if (DebugTiming) Console.WriteLine($"[tr] not timed from conflict times: {string.Join("; ", Validate().Take(3).DefaultIfEmpty("valid"))}, times kept {KeepsTimes(times)}, cycle {Cycle:F1} s\n{Describe()}");
        ConflictTimes = null;
        ConflictTimed = false;
        foreach (var p in _phases) p.Extra = 0;
        TimeStretched(this, _phases, _conflicts);
        Offset = Cycle <= 0 ? 0 : (Mix(_seed) % (uint)Math.Max(1, Cycle * 10)) / 10f;
        return false;
    }

    /// <summary>
    /// Whether every green keeps its conflict time after the end of the last green of each group it conflicts with hard (#979):
    /// <c>start − end ≥ time</c>, round the cycle.
    /// </summary>
    public bool KeepsTimes(float[,] times)
    {
        var conflicts = Conflicts();
        int n = Groups.Count;
        List<(float From, float To)> Greens(int g) => Groups[g].Intervals.Where(v => v.Aspect == SignalAspect.Green).Select(v => (v.From, v.To)).ToList();
        for (int g = 0; g < n; g++)
            for (int h = 0; h < n; h++)
            {
                if (g == h || conflicts[g, h] != Conflict.Hard || float.IsNaN(times[g, h])) continue;
                var ends = Greens(g).Select(v => v.To).ToList();
                if (ends.Count == 0) continue;
                foreach (var (from, _) in Greens(h))
                {
                    if (from < 1e-3f && Groups[h].Intervals[^1].Aspect == SignalAspect.Green) continue;   // a green over the seam starts earlier
                    // the end of g's green that comes last before this start, round the cycle
                    float since = ends.Select(e => ((from - e) % Cycle + Cycle) % Cycle).Min();
                    if (since < Math.Max(0f, times[g, h]) - 0.15f) return false;
                }
            }
        return true;
    }

    /// <summary>The aspect of a group at server time <paramref name="t"/> (seconds).</summary>
    public SignalAspect State(int group, double t) => State(Groups[group], t);

    /// <summary>The aspect of <paramref name="group"/> (one of this plan's, or a detached one like <see cref="LeftFlash"/>'s) at server time <paramref name="t"/>.</summary>
    public SignalAspect State(SignalGroup group, double t)
    {
        var list = group.Intervals;
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
    public double UntilChange(int group, double t) => UntilChange(Groups[group], t);

    /// <summary>Seconds from <paramref name="t"/> until <paramref name="group"/>'s aspect next changes (a detached group too).</summary>
    public double UntilChange(SignalGroup group, double t)
    {
        var list = group.Intervals;
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
            if (group.Kind == SignalGroupKind.Bike && (group.Moves & SignalMoves.Through) != 0) bike = g;
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
            // riders turning right keep to their own way round the corner: no car's (#971; the walkers across the exit below)
            if (RidesRight(a, Groups[a.Group]) || RidesRight(b, Groups[b.Group])) continue;
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
            if (Arms[Groups[g].Arm].BikeBetween) continue;
            for (int h = 0; h < n; h++)
                if (Groups[h].Arm == Groups[g].Arm && Groups[h].Kind == SignalGroupKind.RightArrow) Set(g, h, Conflict.Hard);
                else if (Groups[h].Arm == Groups[g].Arm && Groups[h].Kind == SignalGroupKind.Car && (Groups[h].Moves & SignalMoves.Right) != 0)
                    Set(g, h, Conflict.Permissive);
        }

        for (int p = 0; p < n; p++)
        {
            if (Groups[p].Kind != SignalGroupKind.Pedestrian) continue;
            int arm = Groups[p].Arm;
            var half = Groups[p].Half;
            foreach (var m in moves)
            {
                // a half crosses only its own lanes (#1030): the arriving half the approach's, the departing half those into the arm
                if (m.From != arm && m.To != arm || half == CrossingHalf.Arriving && m.From != arm || half == CrossingHalf.Departing && m.To != arm) continue;
                var g = Groups[m.Group];
                // a car turning in, or riders turning right onto the path across this crossing (#971): they give way, by the flasher
                bool turnsIn = m.To == arm && m.Turn != SignalMoves.Through && g.Kind == SignalGroupKind.Car || m.To == arm && RidesRight(m, g);
                Set(p, m.Group, turnsIn ? Conflict.Permissive : Conflict.Hard);
            }
        }
        return c;
    }

    private static bool Turning(Movement m, SignalGroup g) => m.Turn != SignalMoves.Through && g.Kind == SignalGroupKind.Car;

    /// <summary>
    /// Whether the movement from arm <paramref name="from"/> to <paramref name="to"/> (<paramref name="turn"/>, on group
    /// <paramref name="group"/>) has no absolute priority at <paramref name="t"/> (#971, the debug drawing): a flasher qualifies its
    /// group, or a group it yields to is green with it, by <see cref="Conflicts"/>' rules: a left turn from a shared lane to
    /// oncoming cars, a turn across a bike's way, a turn into an arm whose crossing is green.
    /// </summary>
    public bool YieldsAt(int from, int to, SignalMoves turn, int group, double t)
    {
        var g = Groups[group];
        bool Going(int h) => State(h, t) is SignalAspect.Green;
        for (int h = 0; h < Groups.Count; h++)
        {
            var other = Groups[h];
            // (a flasher warns the turns of its group: straight on from a shared lane keeps its priority, the user)
            if (other.Kind == SignalGroupKind.Flasher && other.Qualifies == group && turn != SignalMoves.Through
                && State(h, t) == SignalAspect.FlashingAmber) return true;
            if (!Going(h)) continue;
            if (other.Kind == SignalGroupKind.Pedestrian && other.Arm == to && other.Half != CrossingHalf.Arriving && turn != SignalMoves.Through
                && (g.Kind == SignalGroupKind.Car || g.Kind == SignalGroupKind.Bike && turn == SignalMoves.Right)) return true;
            if (other.Kind == SignalGroupKind.Bike && other.Arm == from && g.Kind == SignalGroupKind.Car && turn == SignalMoves.Right) return true;
        }
        // riders turning right keep to their own way round the corner: only the exit's crossing above stops them
        if (g.Kind == SignalGroupKind.Bike && turn == SignalMoves.Right) return false;
        const double Lane = 0.08;
        double Entry(int arm) => Arms[arm].Heading + Lane;
        double Exit(int arm) => Arms[arm].Heading - Lane;
        foreach (var m in Movements())
        {
            if (m.Group == group || m.From == from || !Going(m.Group) || RidesRight(m, Groups[m.Group])) continue;
            if (m.To != to && !Crosses(Entry(from), Exit(to), Entry(m.From), Exit(m.To))) continue;
            var gm = Groups[m.Group];
            if (gm.Kind == SignalGroupKind.Bike ? turn != SignalMoves.Through && g.Kind == SignalGroupKind.Car
                : turn == SignalMoves.Left && g.Kind == SignalGroupKind.Car && m.Turn != SignalMoves.Left) return true;
        }
        return false;
    }

    /// <summary>Riders turning right (#971): on their own way round the corner, across only the exit's crossing.</summary>
    private static bool RidesRight(Movement m, SignalGroup g) => g.Kind == SignalGroupKind.Bike && m.Turn == SignalMoves.Right;

    // ---- what a flasher warns of, and the lefts that give way (#1109) --------------------------

    /// <summary>
    /// The white plate under a flasher lamp (#1109, the user): what its turns cross while green with it. A car group's flasher only;
    /// a bike group's is its head's own yellow lens, no lamp and no plate (#971).
    /// </summary>
    public (SignalPlate Plate, SignalMoves Arrow) PlateOf(int flasher, Conflict[,]? conflicts = null,
        SignalMoves turns = SignalMoves.Left | SignalMoves.Right)
    {
        var watch = Watched(flasher, conflicts, turns);
        bool walkers = false, riders = false;
        SignalMoves acrossWalkers = SignalMoves.None, acrossRiders = SignalMoves.None;
        foreach (var (h, across) in watch)
            if (Groups[h].Kind == SignalGroupKind.Pedestrian) { walkers = true; acrossWalkers |= across; }
            else { riders = true; acrossRiders |= across; }
        if (!walkers && !riders) return (SignalPlate.None, SignalMoves.None);
        if (!(walkers && riders)) return (walkers ? SignalPlate.Pedestrian : SignalPlate.Bike, acrossWalkers | acrossRiders);
        // both: the arrow of the turn that crosses both, else of every turn that crosses one
        return (SignalPlate.Arrow, (acrossWalkers & acrossRiders) is var both and not SignalMoves.None ? both : acrossWalkers | acrossRiders);
    }

    /// <summary>
    /// The walkers' and riders' groups a car group's flasher warns its <paramref name="turns"/> of (#1109), with the turns crossing
    /// each: those green with it, or still on the crossing after their green (#979; a plan timed from conflict times may start the
    /// cars' green as the walkers' ends); any it gives way to where none is. A crossing the geometry gives no turn for counts for
    /// every turn of the group.
    /// </summary>
    private List<(int Group, SignalMoves Across)> Watched(int flasher, Conflict[,]? conflicts, SignalMoves turns)
    {
        var watch = new List<(int, SignalMoves)>();
        var fl = Groups[flasher];
        if (fl.Kind != SignalGroupKind.Flasher || fl.Qualifies < 0 || fl.Qualifies >= Groups.Count) return watch;
        int g = fl.Qualifies;
        if (Groups[g].Kind != SignalGroupKind.Car) return watch;
        conflicts ??= Conflicts();
        var moves = Movements();
        bool any = Enumerable.Range(0, Groups.Count).Any(h => Warned(g, h, conflicts, together: true));
        for (int h = 0; h < Groups.Count; h++)
        {
            if (!Warned(g, h, conflicts, together: any)) continue;
            var across = TurnsAcross(g, h, moves);
            if (across == SignalMoves.None) across = Groups[g].Moves & ~SignalMoves.Through;
            if ((across & turns) != SignalMoves.None) watch.Add((h, across & turns));
        }
        return watch;
    }

    /// <summary>
    /// The part of a car group's flasher that warns its <paramref name="turns"/> (#1109, the user: "right flasher here, left flasher
    /// on a pole next to the left crosswalk"): the flasher itself where it warns of nothing else, else a detached flasher on while
    /// the group is green and a crossing of those turns has walkers or riders on it; null where those turns cross none.
    /// </summary>
    public SignalGroup? SideFlash(int flasher, SignalMoves turns, Conflict[,]? conflicts = null)
    {
        conflicts ??= Conflicts();
        var watch = Watched(flasher, conflicts, turns);
        if (watch.Count == 0) return null;
        if (Watched(flasher, conflicts, SignalMoves.Left | SignalMoves.Right).Count == watch.Count) return Groups[flasher];
        int g = Groups[flasher].Qualifies;
        float Walk(int h) => Groups[h].Kind == SignalGroupKind.Pedestrian ? CrossingOf(h) / WalkSpeed : 0f;
        bool Active(int h, double t)
        {
            if (State(h, t) == SignalAspect.Green) return true;
            float walk = Walk(h);
            if (walk <= 0) return false;
            double u = ((t + Offset) % Cycle + Cycle) % Cycle;
            foreach (var iv in Groups[h].Intervals)
                if (iv.Aspect == SignalAspect.Green && ((u - iv.To) % Cycle + Cycle) % Cycle < walk) return true;
            return false;
        }
        var cuts = new SortedSet<float> { 0, Cycle };
        foreach (var iv in Groups[g].Intervals) { cuts.Add(iv.From); cuts.Add(iv.To); }
        foreach (var (h, _) in watch)
            foreach (var iv in Groups[h].Intervals)
            {
                cuts.Add(iv.From);
                cuts.Add(iv.To);
                if (iv.Aspect == SignalAspect.Green && Walk(h) > 0) cuts.Add((iv.To + Walk(h)) % Cycle);
            }
        var flash = new SignalGroup { Kind = SignalGroupKind.Flasher, Arm = Groups[g].Arm, Qualifies = g };
        var list = flash.Intervals;
        float prev = -1;
        foreach (float c in cuts)
        {
            if (prev >= 0 && c > prev)
            {
                double mid = (prev + c) * 0.5 - Offset;
                bool on = State(g, mid) == SignalAspect.Green && watch.Any(w => Active(w.Group, mid));
                var aspect = on ? SignalAspect.FlashingAmber : SignalAspect.Off;
                if (list.Count > 0 && list[^1].Aspect == aspect) list[^1] = list[^1] with { To = c };
                else list.Add(new SignalInterval(aspect, prev, c));
            }
            prev = c;
        }
        return flash;
    }

    private bool Warned(int g, int h, Conflict[,] conflicts, bool together) =>
        conflicts[g, h] == Conflict.Permissive && Groups[h].Kind is SignalGroupKind.Pedestrian or SignalGroupKind.Bike
        && (!together || GreenTogether(g, h, Groups[h].Kind == SignalGroupKind.Pedestrian ? CrossingOf(h) / WalkSpeed : 0f));

    /// <summary>The turns of car group <paramref name="g"/> that cross group <paramref name="h"/> (walkers or riders), by <see cref="Conflicts"/>' rules.</summary>
    private SignalMoves TurnsAcross(int g, int h, List<Movement> moves)
    {
        var other = Groups[h];
        var turns = SignalMoves.None;
        foreach (var a in moves)
        {
            if (a.Group != g || a.Turn == SignalMoves.Through) continue;
            if (other.Kind == SignalGroupKind.Pedestrian)
            {
                // a car turning into the crossed arm
                if (a.To == other.Arm && other.Half != CrossingHalf.Arriving) turns |= a.Turn;
                continue;
            }
            foreach (var b in moves)
                if (b.Group == h && b.From != a.From && !RidesRight(b, other) && Meet(a, b)) turns |= a.Turn;
            // a kerbside bike lane beside the cars of its own approach: the right turn crosses it (#351)
            if (other.Arm == Groups[g].Arm && !Arms[other.Arm].BikeBetween && a.Turn == SignalMoves.Right) turns |= SignalMoves.Right;
        }
        return turns;
    }

    /// <summary>Whether two movements cross or end on the same arm (<see cref="Conflicts"/>' chords).</summary>
    private bool Meet(Movement a, Movement b)
    {
        const double Lane = 0.08;
        return a.To == b.To || Crosses(Arms[a.From].Heading + Lane, Arms[a.To].Heading - Lane, Arms[b.From].Heading + Lane, Arms[b.To].Heading - Lane);
    }

    /// <summary>
    /// The groups car group <paramref name="g"/>'s left turn gives way to while green with them (#1109): the oncoming cars (and
    /// trams) it crosses or merges with, as a left from a shared lane does (<see cref="Conflicts"/>: permissive). Empty for a
    /// protected left.
    /// </summary>
    public List<int> LeftGivesWayTo(int g, Conflict[,]? conflicts = null)
    {
        var to = new List<int>();
        var group = Groups[g];
        if (group.Kind != SignalGroupKind.Car || (group.Moves & SignalMoves.Left) == 0) return to;
        conflicts ??= Conflicts();
        var moves = Movements();
        foreach (var a in moves)
        {
            if (a.Group != g || a.Turn != SignalMoves.Left) continue;
            foreach (var b in moves)
            {
                if (b.Group == g || b.From == a.From || b.Turn == SignalMoves.Left || Groups[b.Group].Kind == SignalGroupKind.Bike
                    || conflicts[g, b.Group] != Conflict.Permissive || to.Contains(b.Group) || !Meet(a, b)) continue;
                if (GreenTogether(g, b.Group)) to.Add(b.Group);
            }
        }
        return to;
    }

    /// <summary>
    /// The flashing yellow of a permissive left (#1109, the user): a detached flasher (not one of <see cref="Groups"/>), flashing
    /// while car group <paramref name="g"/> is green and a group its left gives way to (<see cref="LeftGivesWayTo"/>) is green
    /// too; null where its left never gives way. Read with <see cref="State(SignalGroup, double)"/>. Derived from the plan, not
    /// stored: every peer derives the same.
    /// </summary>
    public SignalGroup? LeftFlash(int g, Conflict[,]? conflicts = null)
    {
        var watch = LeftGivesWayTo(g, conflicts);
        if (watch.Count == 0 || Cycle <= 0) return null;
        var cuts = new SortedSet<float> { 0, Cycle };
        foreach (int h in watch.Append(g))
            foreach (var iv in Groups[h].Intervals) { cuts.Add(iv.From); cuts.Add(iv.To); }
        var flash = new SignalGroup { Kind = SignalGroupKind.Flasher, Arm = Groups[g].Arm, Qualifies = g };
        var list = flash.Intervals;
        float prev = -1;
        foreach (float c in cuts)
        {
            if (prev >= 0 && c > prev)
            {
                double mid = (prev + c) * 0.5 - Offset;
                bool on = State(g, mid) == SignalAspect.Green && watch.Any(h => State(h, mid) == SignalAspect.Green);
                var aspect = on ? SignalAspect.FlashingAmber : SignalAspect.Off;
                if (list.Count > 0 && list[^1].Aspect == aspect) list[^1] = list[^1] with { To = c };
                else list.Add(new SignalInterval(aspect, prev, c));
            }
            prev = c;
        }
        return list.Any(iv => iv.Aspect == SignalAspect.FlashingAmber) ? flash : null;
    }

    /// <summary>
    /// What a head showing <paramref name="turn"/> alone (an uncombined arrow) of car group <paramref name="g"/> flashes (#1109, the
    /// user: "for flashing arrows it takes the place of the green arrow"): a detached flasher on while that turn gives way, to the
    /// oncoming cars (<see cref="LeftFlash"/>) or to walkers or riders across it (the group's <see cref="SignalGroupKind.Flasher"/>,
    /// where its plate's turns hold it); null where it never does.
    /// </summary>
    public SignalGroup? TurnFlash(int g, SignalMoves turn, Conflict[,]? conflicts = null)
    {
        conflicts ??= Conflicts();
        var parts = new List<SignalGroup>(2);
        if (turn == SignalMoves.Left && LeftFlash(g, conflicts) is { } left) parts.Add(left);
        int f = Groups.FindIndex(x => x.Kind == SignalGroupKind.Flasher && x.Qualifies == g);
        if (f >= 0 && SideFlash(f, turn, conflicts) is { } side) parts.Add(side);
        if (parts.Count <= 1) return parts.Count == 0 ? null : parts[0];
        // either: on wherever one is
        var cuts = new SortedSet<float> { 0, Cycle };
        foreach (var part in parts)
            foreach (var iv in part.Intervals) { cuts.Add(iv.From); cuts.Add(iv.To); }
        var flash = new SignalGroup { Kind = SignalGroupKind.Flasher, Arm = Groups[g].Arm, Qualifies = g };
        float prev = -1;
        foreach (float c in cuts)
        {
            if (prev >= 0 && c > prev)
            {
                double mid = (prev + c) * 0.5 - Offset;
                var aspect = parts.Any(x => State(x, mid) == SignalAspect.FlashingAmber) ? SignalAspect.FlashingAmber : SignalAspect.Off;
                var list = flash.Intervals;
                if (list.Count > 0 && list[^1].Aspect == aspect) list[^1] = list[^1] with { To = c };
                else list.Add(new SignalInterval(aspect, prev, c));
            }
            prev = c;
        }
        return flash;
    }

    /// <summary>Whether two groups are ever green at the same time, <paramref name="h"/>'s green running on <paramref name="after"/> s (round the cycle).</summary>
    private bool GreenTogether(int g, int h, float after = 0f)
    {
        foreach (var a in Groups[g].Intervals)
        {
            if (a.Aspect != SignalAspect.Green) continue;
            foreach (var b in Groups[h].Intervals)
            {
                if (b.Aspect != SignalAspect.Green) continue;
                for (int k = -1; k <= 1; k++)
                {
                    float shift = k * Cycle;
                    if (Math.Min(a.To, b.To + after + shift) - Math.Max(a.From, b.From + shift) > 1e-3) return true;
                }
            }
        }
        return false;
    }

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
    /// <param name="tramTHead">Romandy's tram heads (<see cref="TramTHead"/>), set before the timing: their red-amber is
    /// <see cref="TramRedAmberT"/> (#1105: set after, it never applied).</param>
    public static SignalPlan Build(IReadOnlyList<SignalArm> arms, uint seed = 0, bool pedestrianAmber = true, bool tramTHead = false)
    {
        var plan = new SignalPlan { PedestrianAmber = pedestrianAmber, TramTHead = tramTHead };
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
            // what the approach may not do (#700): none of its groups shows it, unless that leaves it nothing
            if ((moves & ~arms[a].Banned) != SignalMoves.None) moves &= ~arms[a].Banned;
            // a pocket gets its own arrow (#1119, the user: "whenever there is a left turn only lane at a light, it should get its own
            // light ... same for right turns"), a T's stem with a left and a right lane too, no main lane then; an approach whose one
            // movement a pocket takes (a right turn out, say) is one group
            var pockets = (arms[a].LeftPocket ? SignalMoves.Left : 0) | (arms[a].RightPocket ? SignalMoves.Right : 0);
            var main = (moves & ~pockets) != SignalMoves.None ? moves & ~pockets
                : moves is SignalMoves.Left or SignalMoves.Right or SignalMoves.Through ? moves : SignalMoves.None;
            if ((moves & pockets & SignalMoves.Left) != 0 && main != moves) left[a] = plan.Add(SignalGroupKind.LeftArrow, a, SignalMoves.Left);
            if ((moves & pockets & SignalMoves.Right) != 0 && main != moves) right[a] = plan.Add(SignalGroupKind.RightArrow, a, SignalMoves.Right);
            if (main != SignalMoves.None) car[a] = plan.Add(SignalGroupKind.Car, a, main);
            // the riders go straight on, and turn right beside the cars on their own way (#971: from a path onto the next arm's)
            if (arms[a].BikeSignal && (moves & SignalMoves.Through) != 0) plan.Add(SignalGroupKind.Bike, a, SignalMoves.Through | (moves & SignalMoves.Right));
            if (arms[a].RiderLeft && (moves & SignalMoves.Left) != 0) plan.Add(SignalGroupKind.Bike, a, SignalMoves.Left);
        }
        // the walkers: kerb to kerb, or each half on its own where they wait on the island between (#1030)
        for (int a = 0; a < arms.Count; a++)
            if (arms[a].TwoStage)
            {
                plan.Add(SignalGroupKind.Pedestrian, a, SignalMoves.None, CrossingHalf.Arriving);
                plan.Add(SignalGroupKind.Pedestrian, a, SignalMoves.None, CrossingHalf.Departing);
            }
            else if (arms[a].Pedestrians) plan.Add(SignalGroupKind.Pedestrian, a, SignalMoves.None);
        // the trams (#1057): a group of their own per approach and direction (the user: one head per direction, its arrow on its
        // plate), after the rest (a plan without trams keeps its groups as they were)
        var trams = new List<int>();
        for (int a = 0; a < arms.Count; a++)
            foreach (var move in (ReadOnlySpan<SignalMoves>)[SignalMoves.Left, SignalMoves.Through, SignalMoves.Right])
                if ((arms[a].TramMoves & move) != 0) trams.Add(plan.Add(SignalGroupKind.Tram, a, move));

        var phases = Phases(plan, arms, car, left, right);
        var conflicts = plan.Conflicts();
        // a scheme that would run two crossing movements together (an odd shape): split phasing
        if (phases.Any(p => p.Base.Any(g => p.Base.Any(h => conflicts[g, h] == Conflict.Hard))))
            phases = Split(plan, arms);
        // a tram group goes with its approach's phases where nothing there crosses it, else with any phase nothing crosses it in;
        // where none, it gets one of its own below (a turning tram across the oncoming traffic)
        foreach (int t in trams)
        {
            bool Free(Phase p) => p.Base.All(g => conflicts[t, g] != Conflict.Hard);
            // where its walkers would be green otherwise (nothing in the phase crossing them), a tram crossing them stays out of it:
            // they would lose their green (a turning tram across the crosswalk it turns over)
            bool Spares(Phase p) => !Enumerable.Range(0, plan.Groups.Count).Any(w => plan.Groups[w].Kind == SignalGroupKind.Pedestrian
                && conflicts[t, w] == Conflict.Hard && p.Base.All(g => conflicts[w, g] != Conflict.Hard));
            var own = phases.Where(p => Free(p) && p.Base.Any(g => plan.Groups[g].Arm == plan.Groups[t].Arm)).ToList();
            var with = own.Where(Spares).ToList();
            // (none spares them: every one but the last of its approach's, so its walkers keep that one)
            if (with.Count == 0) with = own.Count > 1 ? own.Take(own.Count - 1).ToList() : own;
            if (with.Count == 0) with = phases.Where(Free).Take(1).ToList();
            foreach (var p in with) p.Base.Add(t);
        }
        Expand(plan, phases, conflicts);

        // a right arrow no phase carries: protected, green with its own approach, where the walkers and riders it crosses keep a
        // green in another phase (#1119, the user: "even right turns with their own lanes are usually protected"; no phase added,
        // the schemes hold); else a full green with its approach, giving way to them (its yellow arrow flashes, #1109)
        bool Lonely(int g) => phases.All(p => !p.Green.Contains(g));
        // the arms whose two halves are green in one phase
        IEnumerable<int> Halves() => Enumerable.Range(0, arms.Count).Where(a => phases.Any(p =>
            p.Green.Any(g => plan.Groups[g] is { Kind: SignalGroupKind.Pedestrian, Half: CrossingHalf.Arriving } && plan.Groups[g].Arm == a)
            && p.Green.Any(g => plan.Groups[g] is { Kind: SignalGroupKind.Pedestrian, Half: CrossingHalf.Departing } && plan.Groups[g].Arm == a)));
        bool Fits(int r, List<Phase> with)
        {
            if (with.Any(p => p.Base.Any(g => conflicts[r, g] == Conflict.Hard))) return false;
            // (across a two-stage crossing's half: held red there, its walkers wait on the island past a third of the cycle, #1030)
            if (Enumerable.Range(0, plan.Groups.Count).Any(g => plan.Groups[g].Half != CrossingHalf.Whole && conflicts[r, g] == Conflict.Hard)) return false;
            var before = Enumerable.Range(0, plan.Groups.Count).Where(Lonely).ToHashSet();
            var halves = Halves().ToList();
            foreach (var p in with) p.Base.Add(r);
            Expand(plan, phases, conflicts);
            // (and a two-stage crossing's halves still green together where they were: a walker crosses in one go, #1030)
            if (!Lonely(r) && Enumerable.Range(0, plan.Groups.Count).All(g => g == r || !Lonely(g) || before.Contains(g))
                && halves.All(Halves().Contains)) return true;
            foreach (var p in with) p.Base.Remove(r);
            Expand(plan, phases, conflicts);
            return false;
        }
        bool changed = false;
        for (int a = 0; a < arms.Count; a++)
        {
            int r = right[a];
            if (r < 0 || phases.Any(p => p.Green.Contains(r))) continue;
            // (its approach's main lane, or its left arrow on a stem with no main lane, #1119)
            int lane = car[a] >= 0 ? car[a] : left[a];
            var own = lane < 0 ? [] : phases.Where(p => p.Green.Contains(lane)).ToList();
            if (own.Count > 0 && (Fits(r, own) || own.Any(p => Fits(r, [p])))) continue;
            plan.Groups[r].Kind = SignalGroupKind.Car;
            changed = true;
            foreach (var p in own) p.Base.Add(r);
            // (no lane of its own approach anywhere: a phase of its own below, as a lonely group)
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
        // a tram group still never green (it crossed the other lonely groups too, #1057): a phase of its own each
        foreach (int t in trams.Where(t => phases.All(p => !p.Green.Contains(t))).ToList())
        {
            var own = new Phase(0, MinGreen + 2);
            own.Base.Add(t);
            phases.Add(own);
            Expand(plan, phases, conflicts);
        }

        // flashers: beside a group whose turn shares a green with a pedestrian or bike group; the riders' too, turning right
        // across a crossing that is green with them (#971, the user)
        int count = plan.Groups.Count;
        for (int g = 0; g < count; g++)
        {
            if (plan.Groups[g].Kind is not (SignalGroupKind.Car or SignalGroupKind.Bike)) continue;
            bool flashes = false;
            for (int h = 0; h < count && !flashes; h++)
                flashes = conflicts[g, h] == Conflict.Permissive
                    && plan.Groups[h].Kind is SignalGroupKind.Pedestrian or SignalGroupKind.Bike
                    && phases.Any(p => p.Green.Contains(g) && p.Green.Contains(h));
            if (flashes) plan.Groups.Add(new SignalGroup { Kind = SignalGroupKind.Flasher, Arm = plan.Groups[g].Arm, Qualifies = g });
        }

        TimeStretched(plan, phases, conflicts);
        plan.Offset = plan.Cycle <= 0 ? 0 : (Mix(seed) % (uint)Math.Max(1, plan.Cycle * 10)) / 10f;
        (plan._phases, plan._conflicts, plan._seed) = (phases, conflicts, seed);
        return plan;
    }

    private int Add(SignalGroupKind kind, int arm, SignalMoves moves, CrossingHalf half = CrossingHalf.Whole)
    {
        Groups.Add(new SignalGroup { Kind = kind, Arm = arm, Moves = moves, Half = half });
        return Groups.Count - 1;
    }

    private sealed class Phase(float weight, float fixedGreen = 0, float budget = -1)
    {
        /// <summary>The groups the scheme runs in this phase.</summary>
        public readonly List<int> Base = new();
        /// <summary>The base plus every group added because nothing green conflicts with it.</summary>
        public readonly HashSet<int> Green = new();
        public readonly float Weight = weight;
        /// <summary>A fixed green (a turn phase); 0: a share of what is left of the cycle.</summary>
        public readonly float FixedGreen = fixedGreen;
        /// <summary>
        /// What the target cycle sets aside for the fixed green (#982: a turn phase's <see cref="TurnPhaseGreen"/>): a pocket that
        /// needs more lengthens the cycle rather than shortening the through phases.
        /// </summary>
        public readonly float Budget = budget < 0 ? fixedGreen : budget;
        /// <summary>The most green its lanes' queues need (#982); infinite where a lane's storage is unknown.</summary>
        public float Cap = float.PositiveInfinity;
        public float GreenTime;
        /// <summary>Green added to keep a two-stage crossing's island wait (#1030, <see cref="Stretch"/>).</summary>
        public float Extra;
        /// <summary>When its green starts in the cycle, as last timed.</summary>
        public float Start;
    }

    /// <summary>A left-turn phase (#982): what clearing its pocket's full queue takes (up to <see cref="MaxTurnStorage"/>), <see cref="TurnPhaseGreen"/> where unknown.</summary>
    private static Phase TurnPhase(IReadOnlyList<SignalArm> arms, int arm, bool pocket) =>
        new(0, pocket && arms[arm].LeftStorageM > 0 ? MathF.Round(ClearingGreen(Math.Min(MaxTurnStorage, arms[arm].LeftStorageM)), 1) : TurnPhaseGreen,
            TurnPhaseGreen);

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
                if (left[p] >= 0) Add(TurnPhase(arms, p, true), car[p], left[p]);
                Add(new Phase(weight), car[p], car[q]);
                if (left[q] >= 0) Add(TurnPhase(arms, q, true), car[q], left[q]);
            }
        }
        else if (arms.Count == 3 && axes.Count == 1 && single.Count == 1)
        {
            var (p, q) = axes[0];
            int s = single[0];
            // the main approach that turns left into the side road
            int ml = arms[p].In && Turn(arms, p, s) == SignalMoves.Left ? p : arms[q].In && Turn(arms, q, s) == SignalMoves.Left ? q : -1;
            Add(new Phase(Math.Max(1, Rank(p) + Rank(q))), car[p], car[q]);
            if (ml >= 0) Add(TurnPhase(arms, ml, left[ml] >= 0), car[ml], left[ml]);
            Add(new Phase(Math.Max(1, Rank(s))), car[s], left[s], right[s]);
        }
        else phases.AddRange(Split(plan, arms));
        return phases;
    }

    /// <summary>Split phasing: each approach on its own, every lane of it.</summary>
    private static List<Phase> Split(SignalPlan plan, IReadOnlyList<SignalArm> arms)
    {
        // each approach with every lane of it; an approach none of whose movements conflict hard with a phase's goes with it
        // (#1030, the user: 120 s is the legal maximum; Geneva's 10-arm node ran 9 phases of 5 s greens to 157 s)
        var conflicts = plan.Conflicts();
        var phases = new List<(List<int> Groups, float Weight)>();
        for (int a = 0; a < arms.Count; a++)
        {
            if (!arms[a].In) continue;
            var mine = Enumerable.Range(0, plan.Groups.Count)
                .Where(g => plan.Groups[g].Arm == a && plan.Groups[g].Kind is SignalGroupKind.Car or SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow).ToList();
            if (mine.Count == 0) continue;
            float weight = Math.Max(1, (float)arms[a].Rank);
            int with = phases.FindIndex(p => p.Groups.All(g => mine.All(h => conflicts[g, h] != Conflict.Hard)));
            if (with >= 0) phases[with] = (phases[with].Groups.Concat(mine).ToList(), Math.Max(phases[with].Weight, weight));
            else phases.Add((mine, weight));
        }
        return phases.Select(p =>
        {
            var phase = new Phase(p.Weight);
            phase.Base.AddRange(p.Groups);
            return phase;
        }).ToList();
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
                    // a bike goes with its own approach's main lane; its riders turning left with its left arrow (#979)
                    bool riderLeft = plan.Groups[g].Moves == SignalMoves.Left;
                    if (kind == SignalGroupKind.Bike && !phase.Green.Any(h => plan.Groups[h].Arm == plan.Groups[g].Arm && (riderLeft
                            ? plan.Groups[h].Kind is SignalGroupKind.LeftArrow or SignalGroupKind.Car && (plan.Groups[h].Moves & SignalMoves.Left) != 0
                            : plan.Groups[h].Kind == SignalGroupKind.Car)))
                        continue;
                    // (riders turning right share a green with the crossing they cross, giving way by the flasher, #971)
                    if (phase.Green.All(h => conflicts[g, h] == Conflict.None || (!protectedOnly && conflicts[g, h] == Conflict.Permissive
                            && (plan.Groups[h].Kind == SignalGroupKind.Car || kind == SignalGroupKind.Bike && plan.Groups[h].Kind == SignalGroupKind.Pedestrian))))
                        phase.Green.Add(g);
                }
        }
    }

    // ---- timing ------------------------------------------------------------------------------

    /// <summary>
    /// How long a group's last movers may still cross its line after its green (#979's passing time): its amber for cars, the
    /// riders' yellow, none for walkers (none may start on amber) or a flasher.
    /// </summary>
    public float PassingTime(int g) => Groups[g].Kind switch
    {
        SignalGroupKind.Pedestrian or SignalGroupKind.Flasher => 0,
        SignalGroupKind.Bike => YellowBike,
        SignalGroupKind.Tram => TramYellow(Arms[Groups[g].Arm].SpeedKmh),
        _ => Yellow(Arms[Groups[g].Arm].SpeedKmh),
    };

    /// <summary>
    /// How far behind its stop line a group's lane can queue (m, #982): an arrow's pocket, the main lane's run back; a right arrow
    /// no phase could carry, made a full green with its approach, keeps its pocket's. 0 unknown, or not a car group.
    /// </summary>
    public float StorageOf(int g)
    {
        var group = Groups[g];
        var arm = Arms[group.Arm];
        return group.Kind switch
        {
            SignalGroupKind.LeftArrow => arm.LeftStorageM,
            SignalGroupKind.RightArrow => arm.RightStorageM,
            SignalGroupKind.Car when group.Moves == SignalMoves.Right && arm.RightPocket
                && Groups.Any(h => h != group && h.Arm == group.Arm && h.Kind == SignalGroupKind.Car) => arm.RightStorageM,
            SignalGroupKind.Car => arm.StorageM,
            _ => 0,
        };
    }

    private float ClearanceOf(int g)
    {
        var group = Groups[g];
        return group.Kind switch
        {
            SignalGroupKind.Pedestrian => Math.Clamp(CrossingOf(g) * (2f / 3f) / WalkSpeed, 2f, 8f),
            SignalGroupKind.Bike => YellowBike,
            SignalGroupKind.Flasher => 0,
            SignalGroupKind.Tram => TramYellow(Arms[group.Arm].SpeedKmh),
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
        SignalGroupKind.Tram when TramTHead => TramRedAmberT,
        _ => RedAmberCar,
    };

    /// <summary>How far a pedestrian group's walkers cross (m): their half of a two-stage crossing (#1030), else kerb to kerb.</summary>
    public float CrossingOf(int g)
    {
        var arm = Arms[Groups[g].Arm];
        return Groups[g].Half switch
        {
            CrossingHalf.Arriving when arm.HalfInM > 0 => arm.HalfInM,
            CrossingHalf.Departing when arm.HalfOutM > 0 => arm.HalfOutM,
            _ => arm.CrossingM,
        };
    }

    /// <summary>
    /// A two-stage crossing's half's green (#1030) and its first walker on the island: they step off at <see cref="Green"/>, reach
    /// the island at <see cref="Arrive"/> (plan time, s) and wait <see cref="Wait"/> s there for <see cref="Other"/>'s next green.
    /// </summary>
    public readonly record struct IslandWait(int Group, int Other, float Green, float Arrive, float Wait);

    /// <summary>
    /// The island waits of every half's every green (#1030, the user: its first walker's, either way): stepping off as it turns
    /// green, on the island <paramref name="walk"/> s later (its half at <see cref="WalkSpeed"/> by default; a plan read from a tile
    /// has no half lengths: its walkers' conflict lines give them), waiting there for the other half's next green, 0 where it is green.
    /// </summary>
    public List<IslandWait> IslandWaits(Func<int, float>? walk = null)
    {
        walk ??= g => CrossingOf(g) / WalkSpeed;
        var list = new List<IslandWait>();
        if (Cycle <= 0) return list;
        for (int g = 0; g < Groups.Count; g++)
        {
            var x = Groups[g];
            if (x.Kind != SignalGroupKind.Pedestrian || x.Half == CrossingHalf.Whole) continue;
            int y = Groups.FindIndex(o => o.Kind == SignalGroupKind.Pedestrian && o.Arm == x.Arm && o.Half != CrossingHalf.Whole && o.Half != x.Half);
            if (y < 0) continue;
            var v = x.Intervals;
            for (int i = 0; i < v.Count; i++)
            {
                // (a green over the cycle's seam from its piece before the seam)
                if (v[i].Aspect != SignalAspect.Green || (v.Count > 1 && v[i].From < 1e-3f && v[^1].Aspect == SignalAspect.Green)) continue;
                float arrive = v[i].From + walk(g);
                list.Add(new IslandWait(g, y, v[i].From, arrive, UntilGreen(y, arrive)));
            }
        }
        return list;
    }

    /// <summary>Seconds from plan time <paramref name="t"/> (the offset not added) until group <paramref name="g"/> is next green; 0 while it is.</summary>
    private float UntilGreen(int g, float t)
    {
        float u = Wrap(t, Cycle), best = float.PositiveInfinity;
        foreach (var iv in Groups[g].Intervals)
        {
            if (iv.Aspect != SignalAspect.Green) continue;
            if (u >= iv.From && u < iv.To) return 0;
            best = Math.Min(best, Wrap(iv.From - u, Cycle));
        }
        return best;
    }

    /// <summary>
    /// Keeps the island waits (#1030, the user: stretch the greens, the cycle at most 3 s longer for it): where a half's
    /// first walker waits on the island longer than <see cref="IslandWaitShare"/> of the cycle because the other half's green
    /// ended while they walked (Geneva: by tenths of a second, then a whole cycle on the island), the phase it ended in holds on
    /// until they reach the island; the seconds come from the phases that share the cycle (<see cref="Time"/>), the cycle
    /// growing only where they are at their minimum. The phase and the seconds it took, null where none does: time the plan
    /// again, and take it back where the cycle would grow past <see cref="IslandGrowMost"/> (<see cref="Kept"/>); a wait left
    /// over sends RoadGen back to one crossing.
    /// </summary>
    private static (int Phase, float Add)? Stretch(SignalPlan plan, List<Phase> phases)
    {
        float cap = plan.Cycle * IslandWaitShare;
        foreach (var w in plan.IslandWaits().Where(w => w.Wait > cap + 0.05f).OrderByDescending(w => w.Wait))
        {
            // the other half green as they set off, its green ending before they reach the island: that green longer
            float ended = float.NaN;
            foreach (var iv in plan.Groups[w.Other].Intervals)
            {
                if (iv.Aspect != SignalAspect.Green) continue;
                float end = w.Green + Wrap(iv.To - w.Green, plan.Cycle);
                if (end < w.Arrive && !(end <= ended)) ended = end;
            }
            if (float.IsNaN(ended)) continue;
            int k = EndingPhase(plan, phases, w.Other, ended);
            if (k < 0) continue;
            float add = MathF.Round(w.Arrive - ended + 0.5f, 1);
            phases[k].Extra += add;
            return (k, add);
        }
        return null;
    }

    /// <summary>
    /// The phase in which group <paramref name="g"/>'s green ends at plan time <paramref name="at"/> (a slow group may end a little
    /// before its phase): the last of a run of phases green for it whose green ends soonest after <paramref name="at"/>; -1 where none.
    /// </summary>
    private static int EndingPhase(SignalPlan plan, List<Phase> phases, int g, float at)
    {
        int k = -1;
        float soonest = float.PositiveInfinity;
        for (int i = 0; i < phases.Count; i++)
        {
            if (!phases[i].Green.Contains(g) || phases[(i + 1) % phases.Count].Green.Contains(g) && phases.Count > 1) continue;
            float d = Wrap(phases[i].Start + phases[i].GreenTime - at - 1e-3f, plan.Cycle);
            if (d < soonest) (soonest, k) = (d, i);
        }
        return k;
    }

    /// <summary>
    /// After a <see cref="Stretch"/> and the timing that followed: whether the cycle stayed within <see cref="IslandGrowMost"/>
    /// of <paramref name="cycle"/>, its length before any stretch (the user); where not, the stretch is taken back.
    /// </summary>
    private static bool Kept(SignalPlan plan, List<Phase> phases, (int Phase, float Add) stretch, float cycle)
    {
        if (plan.Cycle <= cycle + IslandGrowMost + 0.05f) return true;
        phases[stretch.Phase].Extra -= stretch.Add;
        return false;
    }

    /// <summary>The fixed-time plan (<see cref="Time"/> without conflict times) with its island waits kept (<see cref="Stretch"/>).</summary>
    private static void TimeStretched(SignalPlan plan, List<Phase> phases, Conflict[,] conflicts)
    {
        Time(plan, phases, conflicts, null);
        float cycle = plan.Cycle;
        for (int pass = 0; pass < StretchPasses && Stretch(plan, phases) is { } stretch; pass++)
        {
            Time(plan, phases, conflicts, null);
            if (Kept(plan, phases, stretch, cycle)) continue;
            Time(plan, phases, conflicts, null);
            break;
        }
    }

    /// <param name="limits">How far a slow group may end early at a phase change at most (s, by phase and group): its minimum green.</param>
    /// <param name="slowEnds">Filled with each slow group's early-ended green: the phase change, the group, its green's start and the phase's end.</param>
    private static void Time(SignalPlan plan, List<Phase> phases, Conflict[,] conflicts, float[,]? times, bool early = true,
        Dictionary<(int Phase, int Group), float>? limits = null, List<(int Phase, int Group, float From, float End)>? slowEnds = null)
    {
        int n = phases.Count;
        if (n == 0) return;
        // a bike group starting with a car group that turns across its riders leads it (Vorgrün); with every car that would
        // cross them red, the riders are protected anyway: no lead (the user, #979)
        bool Leads(int bike, IEnumerable<int> starting) =>
            plan.Groups[bike].Kind == SignalGroupKind.Bike
            && starting.Any(h => plan.Groups[h].Kind == SignalGroupKind.Car && conflicts[bike, h] == Conflict.Permissive);
        // a tram leads its approach's cars starting with it by TramLead s; by the riders' lead where a car starting with it turns
        // across its way (permissive: the car gives way, the tram is in the conflict first; #1057, the user); 0 with no car starting
        float TramLeadOf(int tram, IEnumerable<int> starting) =>
            plan.Groups[tram].Kind != SignalGroupKind.Tram || !plan.TramLeads ? 0f
            : starting.Any(h => plan.Groups[h].Kind == SignalGroupKind.Car && conflicts[tram, h] == Conflict.Permissive) ? BikeLead
            : starting.Any(h => plan.Groups[h].Kind == SignalGroupKind.Car && plan.Groups[h].Arm == plan.Groups[tram].Arm) ? TramLead : 0f;
        // the gap after each phase, and with conflict times each starting group's own wait after the phase's green ends
        var inter = new float[n];
        var wait = new Dictionary<int, float>[n];
        // walkers and riders clear slowly: with conflict times their green ends early enough that their clearing fits in the gap
        // the cars' set (the user, #979, as a walkers' green ends before the parallel cars'); by how much before the phase's end
        var shift = new Dictionary<int, float>[n];
        // (riders in a bike box end with the cars behind them, never before: not slow here, the user)
        // (a tram is long and clears slowly too, #1057)
        bool Slow(int g) => plan.Groups[g].Kind is SignalGroupKind.Pedestrian or SignalGroupKind.Tram
            || plan.Groups[g].Kind == SignalGroupKind.Bike && !(plan.Groups[g].Moves == SignalMoves.Left && plan.Arms[plan.Groups[g].Arm].RiderBox);
        bool Timed(int g, int h) => times is not null && conflicts[g, h] == Conflict.Hard && !float.IsNaN(times[g, h]);
        for (int i = 0; i < n; i++)
        {
            var next = phases[(i + 1) % n];
            var ending = phases[i].Green.Where(g => !next.Green.Contains(g)).ToList();
            var starting = next.Green.Where(g => !phases[i].Green.Contains(g)).ToList();
            wait[i] = new Dictionary<int, float>();
            shift[i] = new Dictionary<int, float>();
            if (n == 1) continue;
            if (times is null)
            {
                // the longest clearance of a group that stops, then all red; a leading bike or tram group adds its lead
                inter[i] = ending.Count == 0 ? 0 : ending.Max(g => plan.ClearanceOf(g) + plan.AllRedAfter(g));
                inter[i] += starting.Select(g => Leads(g, starting) ? BikeLead : TramLeadOf(g, starting)).DefaultIfEmpty(0f).Max();
                continue;
            }
            // each starting group as soon as every ending one it conflicts with (hard: they never share a green) has given way
            // by its time (#979: from the end of that one's green, its amber included, never before it); then a car group
            // turning across a leading bike group's riders after their lead
            // the gap the cars set (where only slow groups end, theirs)
            var setters = ending.Where(g => !Slow(g) && starting.Any(h => Timed(g, h))).ToList();
            if (setters.Count == 0) setters = ending;
            float gap = Math.Max(0f, setters.SelectMany(g => starting.Where(h => Timed(g, h)).Select(h => times[g, h])).DefaultIfEmpty(0f).Max());
            if (early)
                foreach (int g in ending.Where(g => Slow(g) && !setters.Contains(g)))
                    shift[i][g] = Math.Max(-(limits?.GetValueOrDefault((i, g), float.PositiveInfinity) ?? float.PositiveInfinity),
                        Math.Min(0f, starting.Where(h => Timed(g, h)).Select(h => gap - times[g, h]).DefaultIfEmpty(0f).Min()));
            foreach (int h in starting)
                wait[i][h] = ending.Where(g => Timed(g, h)).Select(g => Math.Max(0f, shift[i].GetValueOrDefault(g) + times[g, h])).DefaultIfEmpty(0f).Max();
            foreach (int b in starting.Where(b => Leads(b, starting)))
                foreach (int h in starting.Where(h => plan.Groups[h].Kind == SignalGroupKind.Car && conflicts[b, h] == Conflict.Permissive))
                    wait[i][h] = Math.Max(wait[i][h], wait[i][b] + BikeLead);
            // a tram leads (#1057, the user): the cars turning across its way starting with it wait the riders' lead after it, its
            // approach's other cars TramLead (only where the tram may go no later than they: else no lead)
            foreach (int t in starting.Where(t => plan.TramLeads && plan.Groups[t].Kind == SignalGroupKind.Tram))
                foreach (int h in starting.Where(h => plan.Groups[h].Kind == SignalGroupKind.Car))
                    if (conflicts[t, h] == Conflict.Permissive) wait[i][h] = Math.Max(wait[i][h], wait[i][t] + BikeLead);
                    else if (plan.Groups[h].Arm == plan.Groups[t].Arm && wait[i][t] <= wait[i][h] + 0.05f)
                        wait[i][h] = Math.Max(wait[i][h], wait[i][t] + TramLead);
            inter[i] = MathF.Round(wait[i].Values.DefaultIfEmpty(0f).Max(), 1);
            if (DebugTiming && wait[i].Count > 0)
            {
                int hb = wait[i].MaxBy(kv => kv.Value).Key;
                int gb = ending.Where(g => Timed(g, hb)).DefaultIfEmpty(-1).MaxBy(g => g < 0 ? 0 : shift[i].GetValueOrDefault(g) + times[g, hb]);
                Console.WriteLine($"[tr] {i}->{(i + 1) % n}: {inter[i]:F1} s by {(gb >= 0 ? $"{plan.Groups[gb].Kind}/{plan.Groups[gb].Moves}/a{plan.Groups[gb].Arm} (shift {shift[i].GetValueOrDefault(gb):F1}, t {times[gb, hb]:F1})" : "-")} -> {plan.Groups[hb].Kind}/{plan.Groups[hb].Moves}/a{plan.Groups[hb].Arm}");
            }
        }

        // a phase needs no more green than clearing what its lanes can store (#982): the longest of its base groups' full queues,
        // less the green that group has in the fixed phases beside it (a leading turn phase's through group); unknown, no cap
        for (int i = 0; i < n; i++)
        {
            var p = phases[i];
            p.Cap = float.PositiveInfinity;
            if (p.FixedGreen > 0 || n == 1 || p.Base.Count == 0) continue;
            float cap = 0;
            foreach (int g in p.Base)
            {
                float need = ClearingGreen(plan.StorageOf(g));
                if (float.IsInfinity(need)) { cap = need; break; }
                for (int k = (i - 1 + n) % n; k != i && phases[k].FixedGreen > 0 && phases[k].Green.Contains(g); k = (k - 1 + n) % n)
                    need -= phases[k].FixedGreen + inter[k];
                for (int k = (i + 1) % n; k != i && phases[k].FixedGreen > 0 && phases[k].Green.Contains(g); k = (k + 1) % n)
                    need -= phases[k].FixedGreen + inter[(k - 1 + n) % n];
                cap = Math.Max(cap, need);
            }
            p.Cap = Math.Max(MinGreen, cap);
        }

        // the phases share the target cycle by weight, none past its cap: the time a capped one saves shortens the cycle (the
        // user, #982), and a pocket needing more than its turn phase's budget lengthens it; over the maximum, the shares shrink
        float target = n <= 2 ? 60 : n <= 4 ? 60 : 70;
        float weights = phases.Sum(p => p.FixedGreen > 0 ? 0 : p.Weight);
        float cycle = target;
        for (int pass = 0; pass < 2; pass++)
        {
            // (a stretch for an island wait comes out of the shared green: the cycle keeps its length, #1030)
            float share = Math.Max(0, cycle - phases.Sum(p => pass == 0 ? p.Budget : p.FixedGreen) - inter.Sum() - phases.Sum(p => p.Extra));
            foreach (var p in phases)
                p.GreenTime = p.FixedGreen > 0 ? p.FixedGreen : weights > 0 ? Math.Max(MinGreen, Math.Min(p.Cap, share * p.Weight / weights)) : MinGreen;
            // a pedestrian green at least as long as its crossing's clearance, never under the minimum
            foreach (var p in phases)
            {
                float need = p.Green.Where(g => plan.Groups[g].Kind == SignalGroupKind.Pedestrian).Select(_ => 5f).DefaultIfEmpty(MinGreen).Max();
                p.GreenTime = MathF.Round(Math.Max(p.GreenTime, need), 1) + p.Extra;
            }
            float total = phases.Sum(p => p.GreenTime) + inter.Sum();
            if (total <= MaxCycle || pass == 1) { cycle = total; break; }
            cycle = MaxCycle;
        }
        // every phase short (#982): never under the minimum cycle, the shares stretched by weight (the fixed phases where none is shared)
        if (cycle < MinCycle && n > 1)
        {
            var stretch = phases.Where(p => p.FixedGreen <= 0).ToList();
            if (stretch.Count == 0) stretch = phases;
            float w = stretch.Sum(p => p.FixedGreen > 0 ? 1 : p.Weight);
            foreach (var p in stretch) p.GreenTime = MathF.Round(p.GreenTime + (MinCycle - cycle) * (p.FixedGreen > 0 ? 1 : p.Weight) / w, 1);
            cycle = phases.Sum(p => p.GreenTime) + inter.Sum();
        }
        plan.Cycle = MathF.Round(cycle, 1);

        // phase i green from start[i] to start[i] + green; then its intergreen
        var start = new float[n];
        for (int i = 1; i < n; i++) start[i] = start[i - 1] + phases[i - 1].GreenTime + inter[i - 1];
        for (int i = 0; i < n; i++) phases[i].Start = start[i];

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
                // timed from conflict times: as soon as its own wait after the phase before ends (#979)
                int before = (i - 1 + n) % n;
                if (times is not null && n > 1 && wait[before].TryGetValue(g, out float w)) from = start[i] - inter[before] + MathF.Round(w, 1);
                int k = i;
                while (n > 1 && phases[(k + 1) % n].Green.Contains(g) && (k + 1) % n != i) k = (k + 1) % n;
                float to = start[k] + phases[k].GreenTime;
                // (a green over the cycle's seam ends in the next one: before the early end below, which measures from its start, #1030:
                // it took the end of a two-stage half's green for before its start and kept it green the whole cycle)
                if (k < i) to += plan.Cycle;
                // a slow group ending early (#979), its green never under the minimum
                if (times is not null && n > 1 && shift[k].TryGetValue(g, out float sooner))
                {
                    slowEnds?.Add((k, g, from, to));
                    to = Math.Max(from + MinGreen, to + MathF.Round(sooner, 1));
                }
                // a bike leads its approach by up to BikeLead s, once whatever it conflicts with has cleared (without conflict
                // times, and only where it leads, #979)
                if (times is null && plan.Groups[g].Kind == SignalGroupKind.Bike && n > 1
                    && Leads(g, phases[i].Green.Where(h => !phases[(i - 1 + n) % n].Green.Contains(h))))
                {
                    int prev = (i - 1 + n) % n;
                    float prevEnd = start[prev] + phases[prev].GreenTime - (prev > i ? plan.Cycle : 0);
                    float clear = phases[prev].Green.Where(h => conflicts[g, h] == Conflict.Hard && !phases[i].Green.Contains(h))
                        .Select(plan.ClearanceOf).DefaultIfEmpty(-AllRed).Max();
                    from = Math.Max(from - BikeLead, prevEnd + clear + AllRed + RedAmberBike);
                }
                // a tram leads the cars starting with it by up to its lead (TramLeadOf), once whatever it conflicts with has cleared
                // (without conflict times, #1057)
                if (times is null && n > 1 && TramLeadOf(g, phases[i].Green.Where(h => !phases[(i - 1 + n) % n].Green.Contains(h))) is var lead and > 0f)
                {
                    int prev = (i - 1 + n) % n;
                    float prevEnd = start[prev] + phases[prev].GreenTime - (prev > i ? plan.Cycle : 0);
                    // (each thing it crosses cleared and its own all-red after it, as the phase change times them)
                    float clear = phases[prev].Green.Where(h => conflicts[g, h] == Conflict.Hard && !phases[i].Green.Contains(h))
                        .Select(h => plan.ClearanceOf(h) + plan.AllRedAfter(h)).DefaultIfEmpty(0f).Max();
                    from = Math.Min(from, Math.Max(from - lead, prevEnd + clear));
                }

                spans.Add((from, to));
            }
            plan.Groups[g].Intervals.Clear();
            plan.Groups[g].Intervals.AddRange(Lay(spans, plan.Cycle, plan.ClearanceOf(g), plan.RedAmberOf(g),
                plan.Groups[g].Kind == SignalGroupKind.Pedestrian && !plan.PedestrianAmber));
        }

        // flashers: on while their group is green and a pedestrian or bike group it yields to is green too, or a crossing's walkers are
        // still on it: the last to step off as its green ended crosses it at the walking speed (the user, #979: it went off as the
        // walkers' green ended, with them still on the zebra)
        float Walk(int h) => plan.Groups[h].Kind == SignalGroupKind.Pedestrian ? plan.CrossingOf(h) / WalkSpeed : 0f;
        bool Active(int h, double t)
        {
            if (plan.State(h, t) == SignalAspect.Green) return true;
            float walk = Walk(h);
            if (walk <= 0) return false;
            double u = ((t + plan.Offset) % plan.Cycle + plan.Cycle) % plan.Cycle;
            foreach (var iv in plan.Groups[h].Intervals)
                if (iv.Aspect == SignalAspect.Green && ((u - iv.To) % plan.Cycle + plan.Cycle) % plan.Cycle < walk) return true;
            return false;
        }
        for (int f = 0; f < groups; f++)
        {
            var fl = plan.Groups[f];
            if (fl.Kind != SignalGroupKind.Flasher) continue;
            var cuts = new SortedSet<float> { 0, plan.Cycle };
            var watch = Enumerable.Range(0, conflicts.GetLength(0)).Where(h => conflicts[fl.Qualifies, h] == Conflict.Permissive
                && plan.Groups[h].Kind is SignalGroupKind.Pedestrian or SignalGroupKind.Bike).Append(fl.Qualifies).ToList();
            foreach (int h in watch)
                foreach (var iv in plan.Groups[h].Intervals)
                {
                    cuts.Add(iv.From);
                    cuts.Add(iv.To);
                    if (iv.Aspect == SignalAspect.Green && Walk(h) > 0) cuts.Add((iv.To + Walk(h)) % plan.Cycle);
                }
            var list = new List<SignalInterval>();
            float prevCut = -1;
            foreach (float c in cuts)
            {
                if (prevCut >= 0 && c > prevCut)
                {
                    double mid = (prevCut + c) * 0.5 - plan.Offset;
                    bool on = plan.State(fl.Qualifies, mid) == SignalAspect.Green
                        && watch.Any(h => h != fl.Qualifies && Active(h, mid));
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
            sb.Append(c, $"  {g,2} {group.Kind,-10} arm {group.Arm} {(group.Kind == SignalGroupKind.Flasher ? $"of {group.Qualifies}" : group.Half != CrossingHalf.Whole ? group.Half.ToString() : group.Moves.ToString()),-20}");
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
        // timed from conflict times (#979), a green may begin during a conflicting group's amber as far as the geometry allows:
        // two conflicting groups then never green together; else never live together
        bool timed = ConflictTimed;
        // (a tram's red-amber still holds it at its line, #1057: Romandy's is long)
        bool Live(int g, SignalAspect a) => timed ? a == SignalAspect.Green
            : a is SignalAspect.Green or SignalAspect.Amber || a == SignalAspect.RedAmber && Groups[g].Kind != SignalGroupKind.Tram;
        // a turn arrow's last yellow second may overlap the next group's red-yellow (AllRedAfterArrow)
        bool EntryOverlap(int a, int b, double t) =>
            (State(a, t) == SignalAspect.Amber && Groups[a].Kind is SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow or SignalGroupKind.Pedestrian && State(b, t) == SignalAspect.RedAmber)
            || (State(b, t) == SignalAspect.Amber && Groups[b].Kind is SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow or SignalGroupKind.Pedestrian && State(a, t) == SignalAspect.RedAmber);
        var seen = new HashSet<(int, int)>();
        for (double t = 0.05; t < Cycle; t += 0.1)
            for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
                if (conflicts[a, b] == Conflict.Hard && Live(a, State(a, t - Offset)) && Live(b, State(b, t - Offset)) && !EntryOverlap(a, b, t - Offset) && seen.Add((a, b)))
                    errors.Add($"groups {a} ({Groups[a].Kind} arm {Groups[a].Arm}) and {b} ({Groups[b].Kind} arm {Groups[b].Arm}) conflict at {t:F1} s");
        return errors;
    }
}

/// <summary>
/// A canton's colour for its signal poles and overhead structures (#1037): RAL 7001 silver grey where not checked; Basel-Landschaft's
/// anthracite (DB703, ATS LSA Aussenanlagen 2024), the city of Bern's RAL 7042 traffic grey A (Handbuch LSA 2025).
/// </summary>
public enum SignalPoleColour : byte { SilverGrey = 0, Anthracite = 1, TrafficGrey = 2 }

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
    /// <summary>
    /// With <see cref="Pedestrian"/>, its head shows the arriving half of a two-stage crossing (#1030). With
    /// <see cref="DepartingHalf"/> too, on the island: two heads back to back, the arriving half's facing <see cref="SignalPole.PedHeading"/>.
    /// </summary>
    ArrivingHalf = 8,
    /// <summary>With <see cref="Pedestrian"/>, its head shows the departing half of a two-stage crossing (#1030).</summary>
    DepartingHalf = 16,
    /// <summary>Its arm's tram bar heads (#1057), on top of the pole facing the arriving trams; older readers ignore it.</summary>
    Tram = 32,
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
    /// <summary>Its approaches' overhead structures (#1037, section <c>OVHD</c>).</summary>
    public List<SignalOverhead> Overheads { get; } = new();
    /// <summary>Its approaches' gantries with lane signs (#1110, section <c>GNTY</c>).</summary>
    public List<SignGantry> Gantries { get; } = new();
    /// <summary>Its movements as its timing took them, each once (#979, section <c>CPAR</c>, for the debug view's trails).</summary>
    public List<RoadConflictLine> ConflictLines { get; } = new();
    /// <summary>The pairs of those lines that set its groups' green starts and ends (#979, <c>CPAR</c>, for the debug view's movers).</summary>
    public List<RoadConflictPair> ConflictPairs { get; } = new();

    /// <summary>
    /// The pole a car group's left-turn flasher stands on (#1109, the user: "left flasher on a pole next to the left crosswalk"):
    /// the approach's left pole (<see cref="SignalPoleFlags.Second"/>, at the corner the left turn sweeps past), else the main pole
    /// of the arm the left turn goes into (it stands on that arm's arriving kerb, the same corner, by its crosswalk), else the
    /// approach's own main pole (with the right turns' flasher); -1 with no pole.
    /// </summary>
    public (int Pole, FlasherSite Site) LeftFlasherPole(int group)
    {
        int arm = Plan.Groups[group].Arm;
        int second = Poles.FindIndex(p => p.Arm == arm && (p.Flags & SignalPoleFlags.Second) != 0);
        if (second >= 0) return (second, FlasherSite.SecondPole);
        foreach (var m in Plan.Movements())
        {
            if (m.Group != group || m.Turn != SignalMoves.Left) continue;
            int at = Poles.FindIndex(p => p.Arm == m.To && (p.Flags & SignalPoleFlags.Main) != 0);
            if (at >= 0) return (at, FlasherSite.CrossingPole);
        }
        return (Poles.FindIndex(p => p.Arm == arm && (p.Flags & SignalPoleFlags.Main) != 0), FlasherSite.MainPole);
    }

    /// <summary>2 adds the poles (#350); 1 is still read.</summary>
    public const byte SectionVersion = 2;

    [Flags]
    private enum ArmBits : byte { In = 1, Out = 2, LeftPocket = 4, RightPocket = 8, Pedestrians = 16, Bike = 32, BikeRightOnRed = 64, BikeBetween = 128 }

    public static void Write(BinaryWriter w, IReadOnlyList<RoadSignal> signals)
    {
        w.Write((uint)signals.Count);
        w.Write(SectionVersion);
        foreach (var s in signals)
        {
            var p = s.Plan;
            w.Write(s.X); w.Write(s.Y); w.Write(s.Z);
            w.Write(p.Cycle); w.Write(p.Offset);
            w.Write((byte)((p.PedestrianAmber ? 1 : 0) | (p.ArrowPlates ? 2 : 0) | (p.ConflictTimed ? 4 : 0) | ((int)p.PoleColour & 7) << 3 | (p.TramTHead ? 64 : 0)));
            w.Write(checked((byte)p.Arms.Count));
            w.Write(checked((byte)p.Groups.Count));
            for (int i = 0; i < p.Arms.Count; i++)
            {
                var a = p.Arms[i];
                w.Write((float)a.Heading);
                var bits = (a.In ? ArmBits.In : 0) | (a.Out ? ArmBits.Out : 0) | (a.LeftPocket ? ArmBits.LeftPocket : 0)
                    | (a.RightPocket ? ArmBits.RightPocket : 0) | (a.Pedestrians ? ArmBits.Pedestrians : 0) | (a.BikeSignal ? ArmBits.Bike : 0)
                    | (a.BikeRightOnRed ? ArmBits.BikeRightOnRed : 0) | (a.BikeBetween ? ArmBits.BikeBetween : 0);
                w.Write((byte)bits);
                w.Write((byte)Math.Clamp(MathF.Round(a.SpeedKmh), 0, 255));
                w.Write((byte)(a.Rank & 0x7F | (a.AllowBlock ? 0x80 : 0)));
                w.Write((ushort)Math.Clamp(MathF.Round(a.CrossingM * 10), 0, ushort.MaxValue));
                w.Write(s.Stops[i * 3]); w.Write(s.Stops[i * 3 + 1]); w.Write(s.Stops[i * 3 + 2]);
            }
            foreach (var g in p.Groups)
            {
                w.Write((byte)g.Kind);
                w.Write(checked((byte)g.Arm));
                // (a pedestrian group's moves are None: the byte carries its half, #1030; an older reader finds a half as the arm's group)
                w.Write(g.Kind == SignalGroupKind.Pedestrian ? (byte)g.Half : (byte)g.Moves);
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
            bool pedAmber = (kinds & 1) != 0, arrowPlates = (kinds & 2) != 0, conflictTimed = (kinds & 4) != 0;
            int arms = r.ReadByte(), groups = r.ReadByte();
            var plan = new SignalPlan
            {
                Cycle = cycle, Offset = offset, PedestrianAmber = pedAmber, ArrowPlates = arrowPlates, ConflictTimed = conflictTimed,
                PoleColour = (SignalPoleColour)((kinds >> 3) & 7), TramTHead = (kinds & 64) != 0,
            };
            var stops = new float[arms * 3];
            for (int i = 0; i < arms; i++)
            {
                double heading = r.ReadSingle();
                var bits = (ArmBits)r.ReadByte();
                float speed = r.ReadByte();
                byte rank = r.ReadByte();
                float crossing = r.ReadUInt16() / 10f;
                plan.Arms.Add(new SignalArm(heading, bits.HasFlag(ArmBits.In), bits.HasFlag(ArmBits.Out), bits.HasFlag(ArmBits.LeftPocket),
                    bits.HasFlag(ArmBits.RightPocket), bits.HasFlag(ArmBits.Pedestrians), bits.HasFlag(ArmBits.Bike), speed, crossing, rank,
                    BikeRightOnRed: bits.HasFlag(ArmBits.BikeRightOnRed), BikeBetween: bits.HasFlag(ArmBits.BikeBetween))
                    { Rank = (byte)(rank & 0x7F), AllowBlock = (rank & 0x80) != 0 });
                stops[i * 3] = r.ReadSingle(); stops[i * 3 + 1] = r.ReadSingle(); stops[i * 3 + 2] = r.ReadSingle();
            }
            for (int g = 0; g < groups; g++)
            {
                var kind = (SignalGroupKind)r.ReadByte();
                int arm = r.ReadByte();
                byte moves = r.ReadByte();
                bool walkers = kind == SignalGroupKind.Pedestrian;
                var group = new SignalGroup
                {
                    Kind = kind, Arm = arm, Moves = walkers ? SignalMoves.None : (SignalMoves)moves, Half = walkers ? (CrossingHalf)moves : CrossingHalf.Whole,
                    Qualifies = r.ReadSByte(),
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
