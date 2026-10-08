namespace UnitSport.Tools.RoadGen.Junctions;

using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Meshing;

/// <summary>
/// What a junction's layout does differently with traffic lights and without them (#711 phase 4, plan
/// <c>docs/plans/unify-junctions.md</c>). Every junction is laid out by one code path; wherever it differs by kind it asks
/// <see cref="JunctionRules.Has"/>, never the junction's kind. Lights only: <see cref="JunctionRules.Lights"/>; without
/// them (a main road with side roads, and every other kind): <see cref="JunctionRules.NoLights"/>. Signals, poles and
/// their plan are the lights' own (<c>TileRewriter.EmitSignals</c>) and not listed here.
/// </summary>
public enum JunctionRule
{
    /// <summary>Lights: every approach may get a turn pocket, its exit the straightest arm. Without: only the main road's arms, the exit the other main arm.</summary>
    PocketsOnEveryApproach,
    /// <summary>Lights: a white stop line (SSV 6.10, 0.50 m) across every approach, set back past the skewed mouth with room for the crosswalk and the bike crossing; the lane lines go solid up to it, a bike lane solid up to its yellow line.</summary>
    StopLine,
    /// <summary>Without lights: a pocket's own 0.40 m stop bar at the mouth; the main road's through lanes have none.</summary>
    PocketStopBar,
    /// <summary>Without lights: give-way teeth (SSV 6.13) on a yielding arm, moved back behind a path crossing, and its 3.02 sign.</summary>
    GiveWay,
    /// <summary>Without lights: the main road's edge lines carried through the junction as dashed guides, dashed across a joining road's mouth, following a widening (the pocket's and a split lead-in's).</summary>
    EdgeGuides,
    /// <summary>Lights: the centre line solid over the last 20 m before the stop line on a two-way approach without a left pocket (the user's rule).</summary>
    SolidCentreBeforeStop,
    /// <summary>Lights: a left turn guided through the junction (a dashed line, its inner edge) toward an exit island, or where a left-turn bike lane turns with it (a second line between the two, the user's rule).</summary>
    LeftTurnGuides,
    /// <summary>Lights: a bike box where the left turn goes into a street without bike lane or path, else an advanced bike stop line.</summary>
    BikeBoxes,
    /// <summary>Lights: a left-turn bike lane beside a left pocket (stays lights only, the user's decision).</summary>
    LeftTurnBikeLane,
    /// <summary>Lights: a painted bike lane beside a right pocket runs kerbside (a, a bike signal guards it) or between (b), by a hash; without lights always (b).</summary>
    KerbsideBikeLane,
    /// <summary>Lights: an island in the exit hatch even without a crosswalk (the left repeater's), the hatch kept wide enough for it against split lead-ins (stays lights only, the user's decision).</summary>
    ExitIslands,
    /// <summary>Lights: where OSM maps no crossing round the junction, a crosswalk on every arm with a sidewalk. Without lights only where OSM maps one (both draw a mapped one).</summary>
    CrosswalkOnSidewalkArms,
    /// <summary>Without lights: the exit lane continues the through lane (wider than the carriageway's own lane), narrowing to a turn lane where an OSM crosswalk crosses it.</summary>
    ExitLaneContinuesThrough,
    /// <summary>Lights: a bike crossing square across a widened arm, its band straight beside kerb arcs, red only where a car crosses it in the same phase.</summary>
    BikeCrossingByPhase,
    /// <summary>Without lights: a path carried on straight up to the kerb of the road it crosses, red over that road wherever a road joins (SSV 74a).</summary>
    PathsToKerb,
    /// <summary>Lights: the sidewalk, verge and path bands laid round every kerb arc (#682), the arcs clamped to the mouths. Without: the sidewalk corner planner rounds the arcs, which may run past a mouth.</summary>
    BandsRoundArcs,
}

/// <summary>The rules one kind of junction follows (<see cref="JunctionRule"/>), and the markings that only it draws.</summary>
public sealed class JunctionRules
{
    /// <summary>A stop line at the lights: this far back past the mouth (with its skew), and this wide (#348).</summary>
    public const double StopLineSetback = 3.6;
    public const float StopLineWidth = 0.5f;
    /// <summary>A pocket's stop bar without lights: this wide, its junction edge this far out from the mouth (#123).</summary>
    public const float StopBarWidth = 0.4f;
    public const double StopBarSetback = 0.1;

    public static readonly JunctionRules Lights = new("lights",
        JunctionRule.PocketsOnEveryApproach, JunctionRule.StopLine, JunctionRule.SolidCentreBeforeStop, JunctionRule.LeftTurnGuides, JunctionRule.BikeBoxes,
        JunctionRule.LeftTurnBikeLane, JunctionRule.KerbsideBikeLane, JunctionRule.ExitIslands, JunctionRule.CrosswalkOnSidewalkArms,
        JunctionRule.BikeCrossingByPhase, JunctionRule.BandsRoundArcs);

    public static readonly JunctionRules NoLights = new("no lights",
        JunctionRule.PocketStopBar, JunctionRule.GiveWay, JunctionRule.EdgeGuides, JunctionRule.ExitLaneContinuesThrough,
        JunctionRule.PathsToKerb);

    /// <summary>The rules of a junction planned as <paramref name="kind"/>: lights or not.</summary>
    public static JunctionRules Of(PriorityPlanner.Kind kind) => kind == PriorityPlanner.Kind.Signal ? Lights : NoLights;

    private readonly HashSet<JunctionRule> _rules;

    private JunctionRules(string name, params JunctionRule[] rules) => (Name, _rules) = (name, [.. rules]);

    public string Name { get; }
    public IReadOnlySet<JunctionRule> Rules => _rules;
    public bool Has(JunctionRule rule) => _rules.Contains(rule);

    /// <summary>Where cars stop on an approach, metres out from the mouth's middle: the stop line's middle, or the pocket bar's.</summary>
    public double StopAt(double mouthSkew) => Has(JunctionRule.StopLine) ? mouthSkew + StopLineSetback + StopLineWidth * 0.5 : StopBarSetback;

    /// <summary>The stop line's junction edge, out from the mouth (a pocket's lines are set back by its own skew).</summary>
    public double StopSetback => Has(JunctionRule.StopLine) ? StopLineSetback : StopBarSetback;

    /// <summary>How wide the stop line is.</summary>
    public float StopWidth => Has(JunctionRule.StopLine) ? StopLineWidth : StopBarWidth;

    /// <summary>
    /// The paint each rule draws that the other kind does not, as (type, colour): what the same arms with and without lights
    /// may differ in (tier 0 <c>JunctionRulesTests</c>). A rule not listed changes where paint goes, not what is drawn.
    /// </summary>
    public static IReadOnlyList<(PaintType Type, uint Rgba)> PaintOf(JunctionRule rule) => rule switch
    {
        JunctionRule.StopLine => [(PaintType.StopLine, White), (PaintType.StopLine, Yellow)],   // a bike lane's or path's yellow stop line
        JunctionRule.SolidCentreBeforeStop => [(PaintType.WhiteSolid, White), (PaintType.WhiteDashed, White)],
        JunctionRule.PocketStopBar => [(PaintType.StopLine, White)],
        JunctionRule.GiveWay => [(PaintType.SharkTooth, White)],
        JunctionRule.EdgeGuides => [(PaintType.WhiteDashed, White)],
        JunctionRule.LeftTurnGuides => [(PaintType.WhiteDashed, White)],
        JunctionRule.BikeBoxes => [(PaintType.BikeSymbol, Yellow), (PaintType.YellowSolid, Yellow), (PaintType.BikeCrossing, Red)],
        JunctionRule.LeftTurnBikeLane => [(PaintType.YellowSolid, Yellow), (PaintType.BikeSymbol, Yellow)],
        JunctionRule.CrosswalkOnSidewalkArms => [(PaintType.YellowSolid, Yellow)],
        JunctionRule.BikeCrossingByPhase or JunctionRule.PathsToKerb => [(PaintType.BikeCrossing, Red), (PaintType.YellowDashed, Yellow)],
        _ => [],
    };

    private const uint White = PaintEmitter.White, Yellow = PaintEmitter.Yellow, Red = PaintEmitter.Red;
}
