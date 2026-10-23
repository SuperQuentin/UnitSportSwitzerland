namespace UnitSport.Tools.RoadGen.TestRegion;

using System.Globalization;
using System.Text;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Import;
using UnitSport.Tools.RoadGen.Rewrite;

/// <summary>
/// <c>RoadGen --test-region DIR</c> (#386): a small synthetic chunk directory for traffic lights.
/// Flat ground, a hand-designed road network written as the road extractor's raw output (TLM class
/// names, widths, importance and owner through the extractor's own <see cref="RoadFormat"/> parsers,
/// keys per segment), an OSM overlay and <c>osm_nodes.tsv</c> with a <c>highway=traffic_signals</c>
/// node at each signalised junction (data first: OSM decides where the lights are), and a few box
/// buildings where room has to run short; then the normal network stage (<see cref="TileRewriter"/>),
/// so pockets, paint, bike lanes, boxes, signals, poles, <c>SGNL</c> and <c>LANE</c> all come from
/// the real pipeline. Nothing here special-cases the planner: the lane sets come from the room the
/// design leaves (arm length, buildings, road class). Region and junctions:
/// docs/notes/tools/signal-test-region.md.
/// </summary>
public static partial class SignalTestRegion
{
    /// <summary>Tiles written: a row of junction tiles (N 1322) with a flat tile row either side. Outside Switzerland on purpose: no real tile, cached or not, shares an id.</summary>
    public const int MinTileE = 2910, MaxTileE = 2917, MinTileN = 1321, MaxTileN = 1324;
    /// <summary>
    /// The rail row (#1052), LV95 N <see cref="RailN"/>: a double-track tram in a town street (houses along its north side, none
    /// south), a reserved double tram track, the same as two single tracks mapped apart (Geneva, #1094), and a double-track main line with a 600 m curve, for their overhead lines.
    /// </summary>
    public const double RailN = 1324500;
    /// <summary>The junctions' row, LV95 N: the middle of the tile row N 1322.</summary>
    public const double RowN = 1322500;
    /// <summary>The T without lights in town (#711), LV95 E on <see cref="RowN"/>: a split lead-in from the west.</summary>
    public const double TownTeeE = 2911750;
    /// <summary>
    /// The twins (#711 phase 4): the same T in town, LV95 E on <see cref="TwinRowN"/>, once with lights and once without. Their
    /// paint differs only in what <see cref="UnitSport.Tools.RoadGen.Junctions.JunctionRules"/> lists.
    /// </summary>
    public const double TwinRowN = 1321400, TwinLitE = 2910750, TwinUnlitE = 2912250;
    /// <summary>
    /// A road that is no arm of a core but meets it (#818, the edge case 2670385,1253605), LV95 E on <see cref="TwinRowN"/>: a T
    /// whose lane arm reaches a node 7 m out, where two tracks branch off, inside the core.
    /// </summary>
    public const double ShortArmE = 2914000;
    /// <summary>
    /// The real junction 2619795,1264700 (#818, the user's edge case) moved here as it is, its T's node at this LV95 E on
    /// <see cref="TwinRowN"/>: 6 m west of the T the main road divides into two 2.2 m carriageways, joined at their start by a link.
    /// </summary>
    public const double DividedTeeE = 2915300;
    /// <summary>
    /// Staggered T's (#818 phase 5), LV95 E on <see cref="TwinRowN"/>: a 10 m main road with a side road north here and one south
    /// 24 m east (both cores fit on the link between: two cores), 150 m east a pair 8 m apart (the cores overlap: one core), and
    /// 300 m east a pair 24 m apart with lights at both (J9).
    /// </summary>
    public const double StaggerE = 2916500;

    /// <summary>
    /// Roundabouts (#854), LV95 N <see cref="RingN"/>: a compact one (ring centreline radius 8 m, four two-way arms) at
    /// <see cref="RingE"/> and a mini one (radius 5 m, three arms) at <see cref="MiniRingE"/>; both tight, laid whole.
    /// </summary>
    public const double RingN = 1321850, RingE = 2911100, MiniRingE = 2913000;
    /// <summary>
    /// Merged cores laid member by member (#854), LV95 E on <see cref="RingN"/>: a 10 m main road with two 6 m side roads north
    /// 12 m apart (parallel arms: their turns overlap), and 300 m east one north and one north-east 14 m apart.
    /// </summary>
    public const double ForkE = 2915000;
    /// <summary>
    /// J1's crossroads again (#854), at LV95 <see cref="BetweenE"/>,<see cref="BetweenN"/> a tile north of it: every approach with
    /// its bike lane between the through lane and the right pocket (layout (b)), the right turns going with the through green.
    /// </summary>
    public const double BetweenE = 2910500, BetweenN = 1323500;
    /// <summary>
    /// J2's town crossroads again (#971), at LV95 <see cref="TownPocketsE"/>,<see cref="BetweenN"/>: sidewalks and crossings on every arm,
    /// and OSM's turn:lanes left|through|right on every approach: the data builds its right pockets (J2's come from the rule).
    /// </summary>
    public const double TownPocketsE = 2911500;
    /// <summary>
    /// The canton row (#1038), LV95 N <see cref="BetweenN"/>: J2's town crossroads once per canton zone, at these E (the default
    /// zone, Geneva, Vaud, Basel-Landschaft, Bern), on one main road across the borders.
    /// </summary>
    public const double CantonDefaultE = 2912500, CantonGenevaE = 2913500, CantonVaudE = 2914500, CantonBaselLandE = 2915500, CantonBernE = 2916500;
    /// <summary>
    /// Overhead heads (#1037), LV95 E 2917500: a portal over a 4+4 artery (its left lanes beyond a mast arm's reach) on
    /// <see cref="TwinRowN"/>'s tile row (N <see cref="PortalN"/>), and J6 again as a tram street (a span wire) on the canton row's.
    /// </summary>
    public const double OverheadE = 2917500, PortalN = 1321500;

    /// <summary>
    /// The Geneva tram junction (#1155, J15): J14's artery with its tram and branch inside the Geneva zone's tile (2913/1323), south-west
    /// of the canton row's GE crossroads (whose arms stay as they are), arms 200 m, so its tram heads are Romandy ones.
    /// </summary>
    public const double GenevaTramE = 2913230, GenevaTramN = 1323230;
    /// <summary>J15's arms run this far out (m): inside its tile, clear of the canton row and the GE crossroads.</summary>
    private const double GenevaTramArm = 200;
    /// <summary>
    /// Reserved tram track (#1161): J16, a divided road with its double track on its own bed between the carriageways across a cross
    /// road, in the Geneva tile's north-east quarter (Romandy heads).
    /// </summary>
    public const double ReservedTramE = 2913760, ReservedTramN = 1323760;
    /// <summary>J17 (#1161): the divided road's tram turning off its reservation into the cross road (Vaud tile, north-east quarter).</summary>
    public const double TurningTramE = 2914760, TurningTramN = 1323760;
    /// <summary>J18 (#1161): a T off the divided road, its tram straight through on the reservation (Vaud tile, south-west quarter).</summary>
    public const double TeeTramE = 2914230, TeeTramN = 1323230;
    /// <summary>J19 (#1176): J16 in town, its crossings in three stages, an island either side of the tracks (Geneva tile, south-east quarter).</summary>
    public const double IslandTramE = 2913760, IslandTramN = 1323240;
    /// <summary>J20 (#1176): J19 with the tracks' stage unlit, walkers giving way to the trams (Geneva tile, north-west quarter).</summary>
    public const double UnlitTramE = 2913240, UnlitTramN = 1323760;
    /// <summary>
    /// The region's cantons (#1038), by kilometre tile: only those whose lights or poles differ (pedestrian heads, arrow plates,
    /// pole colours, #1037; <c>tools/traffic-signals</c>); every other tile is the default zone (the cantons not checked yet).
    /// </summary>
    public static readonly IReadOnlyDictionary<(int E, int N), string> CantonTiles =
        new Dictionary<(int E, int N), string> { [(2913, 1323)] = "GE", [(2914, 1323)] = "VD", [(2915, 1323)] = "BL", [(2916, 1323)] = "BE" };
    /// <summary>The border drawn on the ground (#1038): purple, as no Swiss marking is (bike crossings are red).</summary>
    public const uint BorderRgba = 0x8E3FB0FF;
    /// <summary>The ground band's half width (vertices within it take <see cref="CoverClass.Border"/>), the stripe across a carriageway.</summary>
    private const double BorderBandHalf = 1.5;
    private const float BorderStripe = 0.5f;
    /// <summary>The ground, metres (as the fixture courses).</summary>
    public const double Ground = 500;
    /// <summary>Region centre: the manifest's suggested origin.</summary>
    public const double OriginE = 2913000, OriginN = RowN;
    /// <summary>Written into both directories; a directory holding anything without it is never wiped.</summary>
    public const string MarkerFile = "signal_test_region.txt";

    // the road extractor's drape (RoadExtractor.DrapeOffset, ClassLift) and densifying (MaxDrapeSpacing)
    private const double DrapeOffset = 0.35, MaxDrapeSpacing = 4.0;

    /// <summary>A signalised junction of the region: what it is for and the car lanes each approach is designed to get (left to right, "|" between lanes; null = not designed, reported only).</summary>
    public sealed record Junction(string Name, double E, double N, string Case, IReadOnlyDictionary<string, string?> Design,
        (double E, double N)[]? Signals = null);

    /// <summary>One TLM line as the extractor would read it, with its OSM overlay row.</summary>
    private sealed record Line(string Id, string Objektart, string Verkehrsbedeutung, string Eigentuemer, (double E, double N)[] Points,
        string Highway, string Sidewalk = "", string Cycleway = "", OsmRow[]? Rows = null, bool Divided = false, bool Ring = false,
        RoadFlags? Rail = null, bool OnStreet = false)
    {
        public string Uuid => "{386-" + Id + "}";
    }

    /// <summary>A box building, LV95 footprint corners and height.</summary>
    private sealed record Box(double E0, double N0, double E1, double N1, double Height);

    /// <summary>An OSM row over part of a line (#700): metres along it, and the lane columns it carries (turn lanes in the line's drawing direction).</summary>
    private sealed record OsmRow(double From, double To, string Lanes = "", string LanesFwd = "", string LanesBwd = "", string TurnFwd = "", string TurnBwd = "",
        string BikeFwd = "", string BikeBwd = "", bool Tram = false);

    /// <summary>An OSM <c>highway=crossing</c> node (#700) on a line, metres along it, with its <c>crossing=*</c> value.</summary>
    private sealed record CrossingNode(string LineId, double Along, string Value, string? Tags = null);

    private sealed record Design(List<Line> Lines, List<Box> Buildings, List<Junction> Junctions, List<CrossingNode> Crossings);

    // ---- the design ----------------------------------------------------------------------------

    public static IReadOnlyList<Junction> Junctions => Build().Junctions;

    private static Design Build()
    {
        var lines = new List<Line>();
        var boxes = new List<Box>();
        var junctions = new List<Junction>();
        const double n = RowN;
        static Dictionary<string, string?> Arms(string? w, string? e, string? s, string? nn) =>
            new() { ["W"] = w, ["E"] = e, ["S"] = s, ["N"] = nn };

        // the main road A, west to east through every junction: a 10 m cantonal through road at the two
        // ideal crossroads, 8 m from the mismatched one on; split where TLM would (junctions, attribute changes)
        double j1 = 2910500, j2 = 2911500, j3 = 2912500, j3b = 2913500, j4 = 2914500, j5a = 2915450, j5b = 2915550, j6 = 2916500, j7 = 2917500;
        const double jt = TownTeeE;   // a T without lights in town (#711), east of J2
        void A(string id, double from, double to, string objektart, string sidewalk = "") =>
            lines.Add(new Line(id, objektart, "Durchgangsstrasse", "Kanton", [(from, n), (to, n)], "secondary", sidewalk));
        A("A0", 2910050, j1, "10m Strasse");
        A("A1", j1, 2910900, "10m Strasse");
        A("A2", 2910900, j2, "10m Strasse", "yes");
        A("A3", j2, jt, "10m Strasse", "yes");
        A("A3b", jt, 2911900, "10m Strasse", "yes");
        A("A4", 2911900, j3, "8m Strasse");
        A("A5", j3, j3b, "8m Strasse");
        A("A6", j3b, j4, "8m Strasse");
        A("A7", j4, j5a, "8m Strasse");
        A("A8", j5a, j5b, "8m Strasse");
        A("A9", j5b, 2915950, "8m Strasse");

        // cross roads, drawn south to north
        void Cross(string id, double e, double from, double to, string objektart, string importance, string highway,
            string sidewalk = "", string cycleway = "") =>
            lines.Add(new Line(id, objektart, importance, "Gemeinde", [(e, from), (e, to)], highway, sidewalk, cycleway));

        // 1. ideal crossroads, painted bike lanes (rural: the #120 rule paints them on both 10 m roads).
        // The north road's cyclists share the lane (OSM cycleway=shared_lane), so a left turn into it from
        // the west waits in a bike box; the other left turns get an advanced bike line.
        Cross("J1S", j1, n - 400, n, "10m Strasse", "Verbindungsstrasse", "secondary");
        Cross("J1N", j1, n, n + 400, "10m Strasse", "Verbindungsstrasse", "secondary", cycleway: "shared_lane");
        junctions.Add(new Junction("J1-ideal-lanes", j1, n, "ideal crossroads, painted bike lanes, bike box (W) and advanced lines",
            Arms("L|T|R", "L|T|R", "L|T|R", "L|T|R")));

        // 1b. J1 again a tile north (#854), every approach with the bike lane between the through lane and the right pocket
        // (layout (b)): no rider goes straight beside a right-turning car at the mouth, so the right turns can run with the
        // through green. OSM says where the bike lane lies (bicycle:lanes, the bike lane third of left | through | bike | right);
        // the planner's own rule would put it at the kerb, every arm turning left
        {
            const double e = BetweenE, nb = BetweenN;
            const string Between = "no|no|designated|no";
            lines.Add(new Line("J10W", "10m Strasse", "Durchgangsstrasse", "Kanton", [(e - 450, nb), (e, nb)], "secondary",
                Rows: [new OsmRow(0, 450, BikeFwd: Between)]));
            lines.Add(new Line("J10E", "10m Strasse", "Durchgangsstrasse", "Kanton", [(e, nb), (e + 450, nb)], "secondary",
                Rows: [new OsmRow(0, 450, BikeBwd: Between)]));
            lines.Add(new Line("J10S", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(e, nb - 400), (e, nb)], "secondary",
                Rows: [new OsmRow(0, 400, BikeFwd: Between)]));
            lines.Add(new Line("J10N", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(e, nb), (e, nb + 400)], "secondary",
                Rows: [new OsmRow(0, 400, BikeBwd: Between)]));
            junctions.Add(new Junction("J10-between-lanes", e, nb, "J1 with every bike lane between the through lane and the right pocket",
                Arms("L|T|R", "L|T|R", "L|T|R", "L|T|R")));
        }

        // 2b. J2 again a tile north (#971): the town crossroads with crossings on every arm, its right pockets from OSM's turn:lanes
        // (the data is the truth at any speed; at the lights the rule builds them too since #971, J2's)
        {
            const double e = TownPocketsE, nb = BetweenN;
            const string Turns = "left|through|right";
            lines.Add(new Line("J11W", "10m Strasse", "Durchgangsstrasse", "Kanton", [(e - 450, nb), (e, nb)], "secondary", "yes",
                Rows: [new OsmRow(0, 450, TurnFwd: Turns)]));
            lines.Add(new Line("J11E", "10m Strasse", "Durchgangsstrasse", "Kanton", [(e, nb), (e + 450, nb)], "secondary", "yes",
                Rows: [new OsmRow(0, 450, TurnBwd: Turns)]));
            lines.Add(new Line("J11S", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(e, nb - 400), (e, nb)], "secondary", "yes",
                Rows: [new OsmRow(0, 400, TurnFwd: Turns)]));
            lines.Add(new Line("J11N", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(e, nb), (e, nb + 400)], "secondary", "yes",
                Rows: [new OsmRow(0, 400, TurnBwd: Turns)]));
            junctions.Add(new Junction("J11-town-pockets", e, nb, "J2 in town with OSM turn:lanes left|through|right: right pockets and crossings",
                Arms("L|T|R", "L|T|R", "L|T|R", "L|T|R")));
        }

        // 2. the same crossroads in town (OSM maps sidewalks): separated bike paths. At the lights a right-turn pocket at any
        // speed (the 50 km/h rule of #711 is for junctions without lights, the user, #971)
        Cross("J2S", j2, n - 400, n, "10m Strasse", "Verbindungsstrasse", "secondary", sidewalk: "yes");
        Cross("J2N", j2, n, n + 400, "10m Strasse", "Verbindungsstrasse", "secondary", sidewalk: "yes");
        junctions.Add(new Junction("J2-ideal-paths", j2, n, "ideal crossroads in town, separated bike paths: bike signals, square crossings, right pockets at the lights",
            Arms("L|T|R", "L|T|R", "L|T|R", "L|T|R")));

        // 2t. a T without lights in town (#711): a 6 m road gives way to the main road from the north. The main road's left
        // pocket from the west splits its lead-in between both edges (#700), so its corners round a widened kerb on both
        // sides of the west arm: the sidewalk corners follow those kerb arcs. No designed junction: no lights
        Cross("JTN", jt, n, n + 300, "6m Strasse", "Verbindungsstrasse", "tertiary", sidewalk: "yes");

        // 3. mismatched room. W: a long arm, L|T|R. E: houses 1.5 m from the kerb from 68 m out (the corner radius of #682 takes ~16 m of the arm): only the
        // shortest left pocket (20 + 20 m) fits, and a right pocket reaches at most 8 m past the left's storage (#682):
        // L|T|R. N: houses 1 m from the kerb from the corner on: no room for a widening, one lane. S: a 6 m
        // road 52 m long to a T where it gives way: too short for a left pocket, long enough for a right (LT|R; the planner gives it
        // up to the exit the west arm's right pocket needs, PlannerDesign: LTR). W: the houses beside the north road give its last metres sidewalks, a street in
        // town, still a right pocket at the lights (#971), L|T|R.
        Cross("J3N", j3, n, n + 400, "8m Strasse", "Verbindungsstrasse", "tertiary");
        Cross("J3S", j3, n - 52, n, "6m Strasse", "k_W", "tertiary");
        lines.Add(new Line("J3D0", "6m Strasse", "k_W", "Gemeinde", [(j3 - 300, n - 52), (j3, n - 52)], "tertiary"));
        lines.Add(new Line("J3D1", "6m Strasse", "k_W", "Gemeinde", [(j3, n - 52), (j3 + 300, n - 52)], "tertiary"));
        boxes.Add(new Box(j3 + 68, n + 5.5, j3 + 84, n + 15.5, 9));
        boxes.Add(new Box(j3 + 87, n + 5.5, j3 + 104, n + 15.5, 12));
        boxes.Add(new Box(j3 + 107, n + 5.5, j3 + 124, n + 15.5, 9));
        boxes.Add(new Box(j3 - 17, n + 12, j3 - 5, n + 30, 12));
        boxes.Add(new Box(j3 - 17, n + 33, j3 - 5, n + 52, 9));
        boxes.Add(new Box(j3 - 17, n + 55, j3 - 5, n + 75, 12));
        boxes.Add(new Box(j3 - 17, n + 78, j3 - 5, n + 100, 9));
        junctions.Add(new Junction("J3-mismatch", j3, n, "mismatched room: long arm, houses at the kerb, a short arm",
            Arms("L|T|R", "L|T|R", "LT|R", "LTR")));

        // 3b. a narrower road class: a 4 m road (no pockets on that class) meets an 8 m and a 6 m road. The 6 m
        // road's left pocket has no through lane to carry on into the 4 m road (#123: no main road out), so
        // that approach keeps its own lane for left and through, and gets a right pocket
        Cross("J3bS", j3b, n - 400, n, "6m Strasse", "Verbindungsstrasse", "tertiary");
        Cross("J3bN", j3b, n, n + 400, "4m Strasse", "k_W", "unclassified");
        junctions.Add(new Junction("J3b-narrow-arm", j3b, n, "a 4 m road (single lane) across the 8 m main road and a 6 m road",
            Arms("L|T|R", "L|T|R", "LT|R", "LTR")));

        // 4. a T: the main road's left pocket (from the east) gets its own phase
        Cross("J4S", j4, n - 400, n, "6m Strasse", "Verbindungsstrasse", "tertiary");
        junctions.Add(new Junction("J4-tee", j4, n, "T junction: the main road's left-pocket phase",
            Arms("T|R", "L|T", "L|R", null)));

        // 5. two crossroads 100 m apart: queues reach back across the one before
        foreach (var (id, e) in new[] { ("J5a", j5a), ("J5b", j5b) })
        {
            Cross(id + "S", e, n - 400, n, "6m Strasse", "Verbindungsstrasse", "tertiary");
            Cross(id + "N", e, n, n + 400, "6m Strasse", "Verbindungsstrasse", "tertiary");
        }
        junctions.Add(new Junction("J5a-pair-west", j5a, n, "two lights 100 m apart, the west one", Arms("L|T|R", null, "L|T|R", "L|T|R")));
        junctions.Add(new Junction("J5b-pair-east", j5b, n, "two lights 100 m apart, the east one", Arms(null, "L|T|R", "L|T|R", "L|T|R")));

        // 6. a 2+2 artery (OSM lanes=4: 12 m) across a 10 m road, every approach with turn:lanes left|through;right (#700): the
        // artery's two lanes are assigned in place, L | TR, nothing is widened; the 10 m road holds one lane toward the lights, so its
        // data builds the left pocket, L | TR too. The artery goes on east to J7 (the stretch between is 2+2 for paint and traffic)
        const string Arrows = "left|through;right";
        lines.Add(new Line("A10", "10m Strasse", "Durchgangsstrasse", "Kanton", [(2915950, n), (j6, n)], "secondary",
            Rows: [new OsmRow(0, 550, Lanes: "4", TurnFwd: Arrows, TurnBwd: Arrows)]));
        lines.Add(new Line("A11", "10m Strasse", "Durchgangsstrasse", "Kanton", [(j6, n), (2917050, n)], "secondary",
            Rows: [new OsmRow(0, 550, Lanes: "4", TurnFwd: Arrows, TurnBwd: Arrows)]));
        lines.Add(new Line("J6S", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(j6, n - 400), (j6, n)], "secondary",
            Rows: [new OsmRow(0, 400, TurnFwd: Arrows)]));
        lines.Add(new Line("J6N", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(j6, n), (j6, n + 400)], "secondary",
            Rows: [new OsmRow(0, 400, TurnBwd: Arrows)]));
        junctions.Add(new Junction("J6-artery-in-place", j6, n, "2+2 artery, turn:lanes assigned to its lanes in place; the 10 m cross road gets its left pocket from data",
            Arms("L|TR", "L|TR", "L|TR", "L|TR")));

        // 7. a 1+1 road whose last 150 m before the lights OSM maps as its own way with lanes:forward=3 and turn:lanes
        // left|left|through;right (the way is too short to change the TLM line's own 1+1): a double left pocket (#700). East of
        // the lights the way says through|through only: no left pocket although a road leaves to the left, and no right turn, so the
        // corner on that approach's right (into the north arm) stays tight and the crossings beside it run diagonal (#700)
        lines.Add(new Line("A12", "8m Strasse", "Durchgangsstrasse", "Kanton", [(2917050, n), (j7, n)], "secondary",
            Rows: [new OsmRow(0, 300), new OsmRow(300, 450, LanesFwd: "3", LanesBwd: "1", TurnFwd: "left|left|through;right")]));
        lines.Add(new Line("A13", "8m Strasse", "Durchgangsstrasse", "Kanton", [(j7, n), (2917950, n)], "secondary",
            Rows: [new OsmRow(0, 150, TurnBwd: "through|through"), new OsmRow(150, 450)]));
        // the cross road is 2+2 (lanes=4, no turn:lanes): the double left turns into two lanes, and the cross road's own two
        // lanes go on straight across into two; both pairs are kept apart by a dashed line through the junction
        lines.Add(new Line("J7S", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(j7, n - 400), (j7, n)], "secondary",
            Rows: [new OsmRow(0, 400, Lanes: "4")]));
        lines.Add(new Line("J7N", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(j7, n), (j7, n + 400)], "secondary",
            Rows: [new OsmRow(0, 400, Lanes: "4")]));
        // 8. the twins (#711 phase 4): the same T twice in town, a 10 m main road with sidewalks and a 4 m road joining from the
        // north (a class with no pockets, so planning is alike: the main road's left pocket from the west at both), once with
        // lights. Each main road is its own line from a dead end 650 m west to one 650 m east, nothing else near
        foreach (var (id, e) in new[] { ("TA", TwinLitE), ("TB", TwinUnlitE) })   // ids whose hash gives both mains the same path layout (5)
        {
            lines.Add(new Line(id + "W", "10m Strasse", "Durchgangsstrasse", "Kanton", [(e - 650, TwinRowN), (e, TwinRowN)], "secondary", "yes"));
            lines.Add(new Line(id + "E", "10m Strasse", "Durchgangsstrasse", "Kanton", [(e, TwinRowN), (e + 650, TwinRowN)], "secondary", "yes"));
            Cross(id + "N", e, TwinRowN, TwinRowN + 300, "4m Strasse", "k_W", "unclassified", sidewalk: "yes");
        }
        // 9. a road that is no arm of the core but meets it (#818, the user's edge case 2670385,1253605): a town T without lights on
        // a 10 m main road with sidewalks, a 3 m lane from the south whose first link ends 7 m out, where two tracks branch off; the
        // lane runs on south with its sidewalks (the core's mouth lies past that node: the lane is trimmed to it there)
        {
            const double nb = TwinRowN, e1 = ShortArmE;
            lines.Add(new Line("SAW", "10m Strasse", "Durchgangsstrasse", "Kanton", [(e1 - 600, nb), (e1, nb)], "secondary", "yes"));
            lines.Add(new Line("SAE", "10m Strasse", "Durchgangsstrasse", "Kanton", [(e1, nb), (e1 + 600, nb)], "secondary", "yes"));
            lines.Add(new Line("SAS1", "3m Strasse", "k_W", "Gemeinde", [(e1, nb - 7), (e1, nb)], "service"));
            lines.Add(new Line("SAS0", "3m Strasse", "k_W", "Gemeinde", [(e1, nb - 250), (e1, nb - 7)], "service", "yes"));
            lines.Add(new Line("SATW", "2m Weg", "k_W", "Gemeinde", [(e1 - 90, nb - 40), (e1, nb - 7)], "track"));
            lines.Add(new Line("SATE", "2m Weg", "k_W", "Gemeinde", [(e1, nb - 7), (e1 + 90, nb - 40)], "track"));
        }
        // 10. the real junction 2619795,1264700 (#818, the user's edge case), its points moved by (295505.6, 56695.2): a T of an
        // 8 m main road and an 8 m side road from the south-south-east; 6 m west of the T (node B) the main road divides into two
        // 2.2 m carriageways ~3 m apart (richtungsgetrennt), the southern one starting 1.4 m south of B at a 1.5 m link (C)
        {
            const double dx = 295505.6, dy = 56695.2;
            (double, double) R(double e, double n) => (e + dx, n + dy);
            var a = R(2619794.4, 1264704.8);
            var b = R(2619788.8, 1264702.8);
            var c = R(2619789.2, 1264701.4);
            lines.Add(new Line("DTE", "8m Strasse", "Durchgangsstrasse", "Kanton", [a, R(2619803.4, 1264708.3), R(2620073.0, 1264813.0)], "secondary", "yes"));
            lines.Add(new Line("DTS", "8m Strasse", "Durchgangsstrasse", "Kanton", [R(2619842.3, 1264548.3), a], "secondary", "yes"));
            lines.Add(new Line("DTW", "8m Strasse", "Durchgangsstrasse", "Kanton", [b, a], "secondary", "yes"));
            lines.Add(new Line("DTN", "4m Strasse", "Durchgangsstrasse", "Kanton", [b, R(2619739.9, 1264688.7), R(2619500.0, 1264620.0)], "secondary", "yes", Divided: true));
            lines.Add(new Line("DTD", "4m Strasse", "Durchgangsstrasse", "Kanton", [R(2619501.3, 1264616.9), R(2619741.2, 1264685.6), c], "secondary", "yes", Divided: true));
            lines.Add(new Line("DTL", "Verbindung", "Durchgangsstrasse", "Kanton", [c, b], "secondary"));
        }
        // 11. staggered T's (#818 phase 5): a side road north at StaggerE and one south 24 m east, two cores; 150 m east a pair
        // 8 m apart, whose cores overlap; 300 m east a pair 24 m apart with lights at both (the street between is inside them, #348)
        {
            const double nb = TwinRowN, e = StaggerE;
            double[] at = [e - 450, e, e + 24, e + 150, e + 158, e + 300, e + 324, e + 450];   // inside the tile: a street run on into the next block is not written
            string[] ids = ["SGW", "SGA", "SGM", "SGB", "SGC", "SGL", "SGE"];
            for (int i = 0; i < ids.Length; i++)
                lines.Add(new Line(ids[i], "10m Strasse", "Durchgangsstrasse", "Kanton", [(at[i], nb), (at[i + 1], nb)], "secondary", "yes"));
            foreach (var (id, x, north) in new[] { ("SGN1", e, true), ("SGS1", e + 24, false), ("SGN2", e + 150, true), ("SGS2", e + 158, false),
                ("SGN3", e + 300, true), ("SGS3", e + 324, false) })
                lines.Add(new Line(id, "4m Strasse", "k_W", "Gemeinde", [(x, nb), (x, north ? nb + 250 : nb - 250)], "unclassified", "yes"));
        }

        // 12. roundabouts (#854): a ring of flagged lines (TLM's kreisel) between its arms' nodes, counter-clockwise, and its arms
        void Ring(string id, double e, double n, double radius, double[] armDegrees, string objektart)
        {
            var nodes = armDegrees.Select(d => (E: e + radius * Math.Cos(d * Math.PI / 180), N: n + radius * Math.Sin(d * Math.PI / 180))).ToArray();
            for (int k = 0; k < armDegrees.Length; k++)
            {
                double a0 = armDegrees[k], a1 = armDegrees[(k + 1) % armDegrees.Length];
                if (a1 <= a0) a1 += 360;
                var arc = new List<(double E, double N)>();
                for (double d = a0; d < a1 - 1e-6; d += 10) arc.Add((e + radius * Math.Cos(d * Math.PI / 180), n + radius * Math.Sin(d * Math.PI / 180)));
                arc.Add(nodes[(k + 1) % armDegrees.Length]);
                lines.Add(new Line($"{id}R{k}", objektart, "Verbindungsstrasse", "Gemeinde", arc.ToArray(), "secondary", Ring: true));
                var dir = (Math.Cos(a0 * Math.PI / 180), Math.Sin(a0 * Math.PI / 180));
                lines.Add(new Line($"{id}A{k}", "6m Strasse", "Verbindungsstrasse", "Gemeinde",
                    [nodes[k], (nodes[k].E + dir.Item1 * 140, nodes[k].N + dir.Item2 * 140)], "secondary", "yes"));
            }
        }
        Ring("RB", RingE, RingN, 8, [0, 90, 180, 270], "6m Strasse");
        Ring("RM", MiniRingE, RingN, 5, [30, 150, 270], "4m Strasse");

        // 13. merged cores laid member by member (#854): two side roads north 12 m apart off a main road, their turns overlapping;
        // 300 m east one north and one north-east 14 m apart
        {
            const double nb = RingN, e = ForkE;
            double[] at = [e - 200, e, e + 12, e + 300, e + 314, e + 450];
            string[] ids = ["FKW", "FKA", "FKM", "FKB", "FKE"];
            for (int i = 0; i < ids.Length; i++)
                lines.Add(new Line(ids[i], "10m Strasse", "Durchgangsstrasse", "Kanton", [(at[i], nb), (at[i + 1], nb)], "secondary", "yes"));
            lines.Add(new Line("FKN1", "6m Strasse", "Verbindungsstrasse", "Gemeinde", [(e, nb), (e, nb + 200)], "secondary", "yes"));
            lines.Add(new Line("FKN2", "6m Strasse", "Verbindungsstrasse", "Gemeinde", [(e + 12, nb), (e + 12, nb + 200)], "secondary", "yes"));
            lines.Add(new Line("FKN3", "6m Strasse", "Verbindungsstrasse", "Gemeinde", [(e + 300, nb), (e + 300, nb + 200)], "secondary", "yes"));
            lines.Add(new Line("FKN4", "6m Strasse", "Verbindungsstrasse", "Gemeinde", [(e + 314, nb), (e + 314 + 140, nb + 140)], "secondary", "yes"));
        }

        // 14. the canton row (#1038): J2's town crossroads in the default zone, in Geneva and in Vaud (CantonTiles), on one 10 m main
        // road with sidewalks that crosses both borders halfway between them; only the canton differs (ids whose hash gives every
        // street J2's path layout, 2: BikePlanner.StrokeLayouts)
        {
            const double nb = BetweenN;
            double[] at = [CantonDefaultE - 450, CantonDefaultE, CantonGenevaE, CantonVaudE, CantonBaselLandE, CantonBernE, CantonBernE + 450];
            for (int i = 0; i + 1 < at.Length; i++)
                lines.Add(new Line($"CN{i}", "10m Strasse", "Durchgangsstrasse", "Kanton", [(at[i], nb), (at[i + 1], nb)], "secondary", "yes"));
            foreach (var (name, id, e) in new[] { ("default", "CA", CantonDefaultE), ("GE", "CB", CantonGenevaE), ("VD", "CI", CantonVaudE),
                         ("BL", "CS", CantonBaselLandE), ("BE", "KA", CantonBernE) })
            {
                Cross(id + "S", e, nb - 400, nb, "10m Strasse", "Verbindungsstrasse", "secondary", sidewalk: "yes");
                Cross(id + "N", e, nb, nb + 400, "10m Strasse", "Verbindungsstrasse", "secondary", sidewalk: "yes");
                junctions.Add(new Junction($"J12-canton-{name}", e, nb, $"J2's crossroads in {(name == "default" ? "the default zone" : name)}: the canton's heads and plates",
                    Arms("L|T|R", "L|T|R", "L|T|R", "L|T|R")));
            }
        }

        // 15. overhead heads (#1037): a 4+4 artery (OSM lanes=8, 24 m) whose turn:lanes left|through|through|through;right are
        // assigned in place, across a 10 m road: its left lane lies beyond a mast arm's reach from the right kerb, so a portal; and
        // the same artery with OSM's tram on it, between houses: a span wire from facade to facade where the portal would stand (the
        // cross roads as J6's)
        {
            const string Wide = "left|through|through|through;right";
            lines.Add(new Line("POW", "10m Strasse", "Durchgangsstrasse", "Kanton", [(OverheadE - 450, PortalN), (OverheadE, PortalN)], "secondary",
                Rows: [new OsmRow(0, 450, Lanes: "8", TurnFwd: Wide, TurnBwd: Wide)]));
            lines.Add(new Line("POE", "10m Strasse", "Durchgangsstrasse", "Kanton", [(OverheadE, PortalN), (OverheadE + 450, PortalN)], "secondary",
                Rows: [new OsmRow(0, 450, Lanes: "8", TurnFwd: Wide, TurnBwd: Wide)]));
            Cross("POS", OverheadE, PortalN - 400, PortalN, "10m Strasse", "Verbindungsstrasse", "secondary");
            Cross("PON", OverheadE, PortalN, PortalN + 400, "10m Strasse", "Verbindungsstrasse", "secondary");
            junctions.Add(new Junction("J13-portal", OverheadE, PortalN, "a 4+4 artery, its lanes assigned in place: heads overhead on a portal",
                Arms("L|T|T|TR", "L|T|T|TR", "L|T|R", "L|T|R")));

            const double nb = BetweenN;
            lines.Add(new Line("TRW", "10m Strasse", "Durchgangsstrasse", "Kanton", [(OverheadE - 450, nb), (OverheadE, nb)], "secondary",
                Rows: [new OsmRow(0, 450, Lanes: "8", TurnFwd: Wide, TurnBwd: Wide, Tram: true)]));
            lines.Add(new Line("TRE", "10m Strasse", "Durchgangsstrasse", "Kanton", [(OverheadE, nb), (OverheadE + 450, nb)], "secondary",
                Rows: [new OsmRow(0, 450, Lanes: "8", TurnFwd: Wide, TurnBwd: Wide, Tram: true)]));
            lines.Add(new Line("TRS", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(OverheadE, nb - 400), (OverheadE, nb)], "secondary",
                Rows: [new OsmRow(0, 400, TurnFwd: Arrows)]));
            lines.Add(new Line("TRN", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(OverheadE, nb), (OverheadE, nb + 400)], "secondary",
                Rows: [new OsmRow(0, 400, TurnBwd: Arrows)]));
            // houses 3 m back from both kerbs of the artery (12 m out), on both arms: the span wire's facades
            foreach (double side in (ReadOnlySpan<double>)[-1, 1])
                foreach (double way in (ReadOnlySpan<double>)[-1, 1])
                    boxes.Add(new Box(Math.Min(OverheadE + way * 9, OverheadE + way * 60), Math.Min(nb + side * 15, nb + side * 27),
                        Math.Max(OverheadE + way * 9, OverheadE + way * 60), Math.Max(nb + side * 15, nb + side * 27), 15));
            // its tram (#1057): two tracks in the middle of the artery as TLM maps a tram line (each its own line, auf_strasse, 3.2 m
            // apart), both drawn west to east: the trams keep right, the eastbound on the south track
            // a branch 12 m east of the junction (the user: a head per direction): the westbound trams may turn left down the south
            // arm, the northbound ones from it turn right onto the eastbound track, each curve round (E + 12, N - 12)
            const double branch = OverheadE + 12;
            foreach (var (id, side) in (ReadOnlySpan<(string, double)>)[("TTS", -1.6), ("TTN", 1.6)])
            {
                lines.Add(new Line(id + "W", "", "", "", [(OverheadE - 450, nb + side), (branch, nb + side)], "", Rail: RoadFlags.Tramway, OnStreet: true));
                lines.Add(new Line(id + "E", "", "", "", [(branch, nb + side), (OverheadE + 450, nb + side)], "", Rail: RoadFlags.Tramway, OnStreet: true));
            }
            List<(double E, double N)> Arc(double radius)
            {
                var arc = new List<(double E, double N)>();
                for (int k = 0; k <= 12; k++)
                {
                    double a = Math.PI / 2 * k / 12;   // from the east-west track round to the north-south one
                    arc.Add((branch - radius * Math.Sin(a), nb - 12 + radius * Math.Cos(a)));
                }
                return arc;
            }
            var left = Arc(13.6);
            left.Add((OverheadE - 1.6, nb - 400));
            lines.Add(new Line("TTL", "", "", "", [.. left], "", Rail: RoadFlags.Tramway, OnStreet: true));
            var right = Arc(10.4);
            right.Reverse();
            right.Insert(0, (OverheadE + 1.6, nb - 400));
            lines.Add(new Line("TTR", "", "", "", [.. right], "", Rail: RoadFlags.Tramway, OnStreet: true));
            junctions.Add(new Junction("J14-tram", OverheadE, nb,"J13's 4+4 artery with trams between houses: heads overhead on a span wire between facades",
                Arms("L|T|T|TR", "L|T|T|TR", "L|TR", "L|TR")));

            // 15b. J15, the Geneva tram junction (#1155, the user: "test world is missing a tram intersection in geneva"): J14 again in
            // the Geneva zone, arms 200 m, no houses: the westbound trams go straight on or left down the south arm from one track
            // (two Romandy heads with arrow plates, each direction its own group), the northbound ones from the south arm turn right
            {
                const double ge = GenevaTramE, gn = GenevaTramN, arm = GenevaTramArm;
                lines.Add(new Line("GTW", "10m Strasse", "Durchgangsstrasse", "Kanton", [(ge - arm, gn), (ge, gn)], "secondary", "yes",
                    Rows: [new OsmRow(0, arm, Lanes: "8", TurnFwd: Wide, TurnBwd: Wide, Tram: true)]));
                lines.Add(new Line("GTE", "10m Strasse", "Durchgangsstrasse", "Kanton", [(ge, gn), (ge + arm, gn)], "secondary", "yes",
                    Rows: [new OsmRow(0, arm, Lanes: "8", TurnFwd: Wide, TurnBwd: Wide, Tram: true)]));
                lines.Add(new Line("GTS", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(ge, gn - arm), (ge, gn)], "secondary", "yes",
                    Rows: [new OsmRow(0, arm, TurnFwd: Arrows)]));
                lines.Add(new Line("GTN", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(ge, gn), (ge, gn + arm)], "secondary", "yes",
                    Rows: [new OsmRow(0, arm, TurnBwd: Arrows)]));
                double gb = ge + 12;
                foreach (var (id, side) in (ReadOnlySpan<(string, double)>)[("GTTS", -1.6), ("GTTN", 1.6)])
                {
                    lines.Add(new Line(id + "W", "", "", "", [(ge - arm, gn + side), (gb, gn + side)], "", Rail: RoadFlags.Tramway, OnStreet: true));
                    lines.Add(new Line(id + "E", "", "", "", [(gb, gn + side), (ge + arm, gn + side)], "", Rail: RoadFlags.Tramway, OnStreet: true));
                }
                List<(double E, double N)> GArc(double radius)
                {
                    var arc = new List<(double E, double N)>();
                    for (int k = 0; k <= 12; k++)
                    {
                        double a = Math.PI / 2 * k / 12;
                        arc.Add((gb - radius * Math.Sin(a), gn - 12 + radius * Math.Cos(a)));
                    }
                    return arc;
                }
                var gl = GArc(13.6);
                gl.Add((ge - 1.6, gn - arm));
                lines.Add(new Line("GTTL", "", "", "", [.. gl], "", Rail: RoadFlags.Tramway, OnStreet: true));
                var gr = GArc(10.4);
                gr.Reverse();
                gr.Insert(0, (ge + 1.6, gn - arm));
                lines.Add(new Line("GTTR", "", "", "", [.. gr], "", Rail: RoadFlags.Tramway, OnStreet: true));
                junctions.Add(new Junction("J15-geneva-tram", ge, gn, "J14's artery and tram in the Geneva zone: Romandy tram heads, arrow plates",
                    Arms(null, null, null, null)));
            }
        }

        junctions.Add(new Junction("J9-stagger-west", StaggerE + 300, TwinRowN, "staggered T's 24 m apart, both lit: the west one", Arms(null, null, null, null)));
        junctions.Add(new Junction("J9-stagger-east", StaggerE + 324, TwinRowN, "staggered T's 24 m apart, both lit: the east one", Arms(null, null, null, null)));
        junctions.Add(new Junction("J8-twin-lights", TwinLitE, TwinRowN, "the twins' T with lights (the other, 1.5 km east, has none)",
            Arms(null, null, null, null)));
        junctions.Add(new Junction("J7-double-left", j7, n, "a double left pocket from lanes:forward=3 and turn:lanes; no left pocket where OSM marks none",
            Arms("L|L|TR", "T", "LT|TR", "LT|TR")));
        // pedestrian crossings from OSM (#700): J7 is lit and has no sidewalks, so only the data draws its zebras, the ones beside
        // the tight north-east corner diagonal; the T south of J3 has no lights: zebras on two arms, the unmarked one on its east arm none.
        // J3 maps one crossing, on its west arm: its crosswalks follow the data, none on the north arm's sidewalks (#711)
        var crossings = new List<CrossingNode>
        {
            new("A13", 8, "zebra"), new("J7N", 8, "zebra"), new("A4", 592, "traffic_signals"),
            new("J3S", 6, "zebra"), new("J3D0", 294, "marked"), new("J3D1", 6, "unmarked"),
        };
        // 16. the rail row (#1052): overhead lines. A 10 m town street with a double-track tram on it (TLM auf_strasse), houses 5 m
        // back from its north kerb for its western half (the spans' facades), none south (masts); a reserved double tram track (centre
        // masts: nothing built near) on its own, and 60 m north of it the same as two single tracks 3.6 m apart, drawn in opposite
        // directions (Geneva maps each tram track so, #1094); a double-track main line bending through a 600 m curve
        {
            const double rn = RailN;
            lines.Add(new Line("RS", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(2910100, rn), (2911900, rn)], "secondary", "yes"));
            lines.Add(new Line("RT", "", "", "", [(2910100, rn), (2911900, rn)], "", Rail: RoadFlags.Tramway | RoadFlags.DoubleTrack, OnStreet: true));
            boxes.Add(new Box(2910200, rn + 10, 2911000, rn + 24, 15));
            lines.Add(new Line("RR", "", "", "", [(2912100, rn), (2913900, rn)], "", Rail: RoadFlags.Tramway | RoadFlags.DoubleTrack));
            lines.Add(new Line("RP1", "", "", "", [(2912100, rn + 58.2), (2913900, rn + 58.2)], "", Rail: RoadFlags.Tramway));
            lines.Add(new Line("RP2", "", "", "", [(2913900, rn + 61.8), (2912100, rn + 61.8)], "", Rail: RoadFlags.Tramway));
            var curve = new List<(double E, double N)> { (2914100, rn - 200), (2914500, rn - 200) };
            // a 600 m arc turning left by 30 degrees, then on straight
            for (int k = 1; k <= 30; k++)
            {
                double a = k * Math.PI / 180;   // the centre 600 m north of the curve's start
                curve.Add((2914500 + 600 * Math.Sin(a), rn - 200 + 600 - 600 * Math.Cos(a)));
            }
            curve.Add((curve[^1].E + 800 * Math.Cos(Math.PI / 6), curve[^1].N + 800 * Math.Sin(Math.PI / 6)));
            lines.Add(new Line("RM", "", "", "", [.. curve.Where(c => c.N < (MaxTileN + 1) * 1000 - 50 && c.E < (MaxTileE + 1) * 1000 - 50)], "",
                Rail: RoadFlags.DoubleTrack));
        }

        // 17. reserved tram track (#1161, the user: "isn't it just like a bike lane?"): J16, a main road of two one-way carriageways
        // 14 m apart (TLM richtungsgetrennt) with a double tram track on its own bed between them, straight through a 10 m cross
        // road; in the Geneva zone's north-east quarter (Romandy heads)
        {
            const double ce = ReservedTramE, cn = ReservedTramN, arm = 180, half = 7;
            lines.Add(new Line("RTWb", "6m Strasse", "Durchgangsstrasse", "Kanton", [(ce + arm, cn + half), (ce, cn + half)], "secondary", Divided: true));
            lines.Add(new Line("RTWa", "6m Strasse", "Durchgangsstrasse", "Kanton", [(ce, cn + half), (ce - arm, cn + half)], "secondary", Divided: true));
            lines.Add(new Line("RTEa", "6m Strasse", "Durchgangsstrasse", "Kanton", [(ce - arm, cn - half), (ce, cn - half)], "secondary", Divided: true));
            lines.Add(new Line("RTEb", "6m Strasse", "Durchgangsstrasse", "Kanton", [(ce, cn - half), (ce + arm, cn - half)], "secondary", Divided: true));
            lines.Add(new Line("RTS", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(ce, cn - arm), (ce, cn - half)], "secondary"));
            lines.Add(new Line("RTM", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(ce, cn - half), (ce, cn + half)], "secondary"));
            lines.Add(new Line("RTN", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(ce, cn + half), (ce, cn + arm)], "secondary"));
            lines.Add(new Line("RTT", "", "", "", [(ce - arm, cn), (ce + arm, cn)], "", Rail: RoadFlags.Tramway | RoadFlags.DoubleTrack));
            junctions.Add(new Junction("J16-reserved-tram", ce, cn, "a divided road with its tram on its own bed between the carriageways, straight through",
                Arms(null, null, null, null), Signals: [(ce, cn - half), (ce, cn + half)]));
        }

        // J17 (#1161): the same divided road, its tram on the reservation turning left at the junction onto the cross road, where it
        // runs in the street's middle (embedded); the trams from the north turn right onto the reservation. In the Vaud tile's
        // north-east quarter (Romandy heads)
        {
            const double ce = TurningTramE, cn = TurningTramN, arm = 180, half = 7, radius = 15;
            DividedRoad("KT", ce, cn, arm, half);
            lines.Add(new Line("KTS", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(ce, cn - arm), (ce, cn - half)], "secondary"));
            lines.Add(new Line("KTM", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(ce, cn - half), (ce, cn + half)], "secondary"));
            lines.Add(new Line("KTN", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(ce, cn + half), (ce, cn + arm)], "secondary",
                Rows: [new OsmRow(0, arm - half, Tram: true)]));
            // two single tracks as TLM maps them (Geneva, #1094), TrackOffset either side of the line: from the reservation round
            // (E - radius, N + radius) to the cross road's axis and on up its middle (embedded there)
            var line = new List<(double E, double N)> { (ce - arm, cn) };
            for (int k = 0; k <= 12; k++)
            {
                double a = Math.PI / 2 * k / 12;
                line.Add((ce - radius + radius * Math.Sin(a), cn + radius - radius * Math.Cos(a)));
            }
            line.Add((ce, cn + 2 * radius));
            double off = RoadFormat.TrackOffset(RoadFlags.Tramway | RoadFlags.DoubleTrack);
            foreach (var (id, side) in (ReadOnlySpan<(string, double)>)[("KTTR", 1.0), ("KTTL", -1.0)])
            {
                // (right of the line's way: the eastbound, then northbound track; left: the trams the other way)
                var track = new List<(double E, double N)>();
                for (int i = 0; i < line.Count; i++)
                {
                    var (p0, p1) = (line[Math.Max(0, i - 1)], line[Math.Min(line.Count - 1, i + 1)]);
                    double dx = p1.E - p0.E, dn = p1.N - p0.N, len = Math.Sqrt(dx * dx + dn * dn);
                    track.Add((line[i].E + dn / len * off * side, line[i].N - dx / len * off * side));
                }
                lines.Add(new Line(id, "", "", "", [.. track], "", Rail: RoadFlags.Tramway));
                lines.Add(new Line(id + "N", "", "", "", [track[^1], (ce + off * side, cn + arm)], "", Rail: RoadFlags.Tramway, OnStreet: true));
            }
            junctions.Add(new Junction("J17-tram-turning", ce, cn, "the divided road's tram turning off its reservation into the cross road's middle",
                Arms(null, null, null, null), Signals: [(ce, cn - half), (ce, cn + half)]));
        }

        // J18 (#1161): a T, the divided road's tram straight through on its reservation, the side road from the south across the
        // reservation to the far carriageway; in the Vaud tile's south-west quarter
        {
            const double ce = TeeTramE, cn = TeeTramN, arm = 180, half = 7;
            DividedRoad("LT", ce, cn, arm, half);
            lines.Add(new Line("LTS", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(ce, cn - arm), (ce, cn - half)], "secondary"));
            lines.Add(new Line("LTM", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(ce, cn - half), (ce, cn + half)], "secondary"));
            lines.Add(new Line("LTT", "", "", "", [(ce - arm, cn), (ce + arm, cn)], "", Rail: RoadFlags.Tramway | RoadFlags.DoubleTrack));
            junctions.Add(new Junction("J18-tram-tee", ce, cn, "a T off the divided road, its tram straight through on the reservation",
                Arms(null, null, null, null), Signals: [(ce, cn - half), (ce, cn + half)]));
        }

        // J19 and J20 (#1176, the user: "a multi step crossing with islands on both sides of the center running tram lanes"): J16 in
        // town (sidewalks on every road), a crossing over the divided road in three stages, carriageway, the tracks between two
        // islands, carriageway; J20 the same with the tracks' stage unlit, its walkers giving way to the trams. Geneva tile, its
        // south-east and north-west quarters
        foreach (var (id, ce, cn, unlit) in new[] { ("IT", IslandTramE, IslandTramN, false), ("UT", UnlitTramE, UnlitTramN, true) })
        {
            const double arm = 180, half = 8;
            DividedRoad(id, ce, cn, arm, half, "yes");
            lines.Add(new Line(id + "S", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(ce, cn - arm), (ce, cn - half)], "secondary", "yes"));
            lines.Add(new Line(id + "M", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(ce, cn - half), (ce, cn + half)], "secondary", "yes"));
            lines.Add(new Line(id + "N", "10m Strasse", "Verbindungsstrasse", "Gemeinde", [(ce, cn + half), (ce, cn + arm)], "secondary", "yes"));
            lines.Add(new Line(id + "T", "", "", "", [(ce - arm, cn), (ce + arm, cn)], "", Rail: RoadFlags.Tramway | RoadFlags.DoubleTrack));
            // (J20: OSM's walkers' crossing of the tracks either side of the junction, uncontrolled: its middle stages unlit)
            if (unlit)
                foreach (double along in (ReadOnlySpan<double>)[arm - 12, arm + 12])
                    crossings.Add(new CrossingNode(id + "T", along, "uncontrolled", "railway=tram_crossing;crossing=uncontrolled"));
            junctions.Add(new Junction(unlit ? "J20-tram-unlit-middle" : "J19-tram-islands", ce, cn,
                unlit ? "J19 with the crossing's middle stage over the tracks unlit: its walkers give way to the trams"
                    : "a divided road in town, its tram on the reservation: the crossings in three stages, an island either side of the tracks",
                Arms(null, null, null, null), Signals: [(ce, cn - half), (ce, cn + half)]));
        }

        return new Design(lines, boxes, junctions, crossings);

        // a main road of two one-way carriageways (TLM richtungsgetrennt) 2 x half apart, east and west of (e, n), node at each
        // carriageway's crossing of the cross road (#1161)
        void DividedRoad(string id, double e, double n, double arm, double half, string sidewalk = "")
        {
            lines.Add(new Line(id + "Wb", "6m Strasse", "Durchgangsstrasse", "Kanton", [(e + arm, n + half), (e, n + half)], "secondary", sidewalk, Divided: true));
            lines.Add(new Line(id + "Wa", "6m Strasse", "Durchgangsstrasse", "Kanton", [(e, n + half), (e - arm, n + half)], "secondary", sidewalk, Divided: true));
            lines.Add(new Line(id + "Ea", "6m Strasse", "Durchgangsstrasse", "Kanton", [(e - arm, n - half), (e, n - half)], "secondary", sidewalk, Divided: true));
            lines.Add(new Line(id + "Eb", "6m Strasse", "Durchgangsstrasse", "Kanton", [(e, n - half), (e + arm, n - half)], "secondary", sidewalk, Divided: true));
        }
    }

    // ---- build ---------------------------------------------------------------------------------

    public static int Run(string chunkDir, string? tempDir, Action<string> log, bool newJunctions = true, bool writeJunctions = true)
    {
        tempDir ??= RawRoads.DefaultTempDir(chunkDir);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (string dir in new[] { chunkDir, tempDir })
            if (!Clear(dir, log)) return 1;

        var design = Build();
        var tiles = new List<TileId>();
        for (int e = MinTileE; e <= MaxTileE; e++)
            for (int nn = MinTileN; nn <= MaxTileN; nn++)
                tiles.Add(new TileId(e, nn));

        WriteTerrain(chunkDir, tiles);
        WriteBuildings(chunkDir, tiles, design.Buildings);
        WritePlaces(chunkDir, design.Junctions);
        var roadTiles = WriteRawRoads(RawRoads.DirFor(tempDir), design.Lines);
        WriteOverlay(tempDir, design.Lines);
        // J14's tram stops (#1057): two stops on the artery, 150 m either side of the junction, as swissTLM3D gives a stop: one point
        File.WriteAllText(Path.Combine(tempDir, "tram_stops.tsv"), string.Create(CultureInfo.InvariantCulture,
            $"name\te\tn\nJ14 west\t{OverheadE - 150:F2}\t{BetweenN:F2}\nJ14 east\t{OverheadE + 150:F2}\t{BetweenN:F2}\n"
            + $"J15 west\t{GenevaTramE - 100:F2}\t{GenevaTramN:F2}\nJ15 east\t{GenevaTramE + 100:F2}\t{GenevaTramN:F2}\n"));
        WriteSignals(tempDir, design);
        string about = About(design);
        foreach (string dir in new[] { chunkDir, tempDir })
        {
            File.WriteAllText(Path.Combine(dir, MarkerFile), about);
            File.WriteAllText(Path.Combine(dir, ".gdignore"), "");
        }
        log($"test region (#386): {tiles.Count} flat tiles, {design.Lines.Count} lines on {roadTiles.Count} road tiles, "
            + $"{design.Buildings.Count} buildings, {design.Junctions.Count} signal nodes; written in {clock.Elapsed.TotalSeconds:F1} s");

        var stats = TileRewriter.Run(chunkDir, roadTiles, new TileRewriter.Options(
            RawDir: RawRoads.DirFor(tempDir), OsmOverlay: Path.Combine(tempDir, "osm_overlay.tsv"), TramStops: Path.Combine(tempDir, "tram_stops.tsv"),
            NewJunctions: newJunctions, WriteJunctions: writeJunctions,
            Cantons: TileRewriter.Cantons.Of(CantonTiles), Decorate: MarkBorders), log);
        log(stats.Network.Format(stats.TilesWritten));
        log("");
        int bad = Report(chunkDir, design.Junctions, log) + BorderReport(chunkDir, log);
        // its traffic (#1134): generators and destinations on every lit junction, rates from what the lights pass
        log("");
        if (WriteTraffic(chunkDir, design.Junctions, design.Lines, log).Count == 0) bad++;
        int planned = 0;
        if (newJunctions)
        {
            log("");
            planned = ReportPlans(stats.Network.Layouts, design.Junctions, log);
            log(planned == 0 ? "[testregion] planner (#818): every designed approach as designed" : $"[testregion] planner (#818): {planned} approaches not as designed");
        }
        // the planner writing its junctions (#818 phase 3): their lights and lane records come with phases 4 and 5, so the table
        // above is the old layout's only where it still builds; the planner's own report decides
        if (writeJunctions)
        {
            log($"[testregion] the planner wrote its junctions: the lights' table above is left out of the result ({bad} approaches there)");
            bad = planned;
        }
        log(string.Create(CultureInfo.InvariantCulture, $"{clock.Elapsed.TotalSeconds:F1} s; chunks {chunkDir}, raw input {tempDir}"));
        log(bad == 0 ? "[testregion] RESULT: ok" : $"[testregion] RESULT: FAILED {bad} approaches not as designed");
        return bad == 0 ? 0 : 2;
    }

    /// <summary>Empties a directory this command wrote before (it holds the marker); refuses one that holds anything else.</summary>
    private static bool Clear(string dir, Action<string> log)
    {
        if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); return true; }
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) return true;
        if (!File.Exists(Path.Combine(dir, MarkerFile)))
        {
            log($"{dir} is not empty and was not written by --test-region (no {MarkerFile}): refusing to clear it");
            return false;
        }
        foreach (string f in Directory.EnumerateFiles(dir)) File.Delete(f);
        foreach (string d in Directory.EnumerateDirectories(dir)) Directory.Delete(d, recursive: true);
        return true;
    }

    private static void WriteTerrain(string dir, List<TileId> tiles)
    {
        ushort q = ChunkFormat.Quantize(Ground);
        float h = (float)ChunkFormat.Dequantize(q);
        var manifest = new TerrainManifest
        {
            SuggestedOriginLv95 = new Lv95Point { E = OriginE, N = OriginN },
            BoundsLv95 = new Lv95Bounds
            {
                MinE = MinTileE * 1000.0, MinN = MinTileN * 1000.0, MaxE = (MaxTileE + 1) * 1000.0, MaxN = (MaxTileN + 1) * 1000.0,
            },
        };
        var horizon = new Dictionary<TileId, ushort[]>();
        int size = ChunkFormat.GridSize, coarse = (size - 1) / ChunkFormat.CoarseStride + 1;
        foreach (var id in tiles)
        {
            var grid = new ChunkGrid(id, Enumerable.Repeat(q, size * size).ToArray(), h, h);
            using (var fs = File.Create(Path.Combine(dir, ChunkFormat.ChunkFileName(id)))) ChunkCodec.Encode(grid, fs);
            using (var fs = File.Create(Path.Combine(dir, ChunkFormat.CoarseFileName(id))))
                ChunkCodec.Encode(new ChunkGrid(id, Enumerable.Repeat(q, coarse * coarse).ToArray(), h, h, ChunkFormat.CoarseStride), fs);
            using (var fs = File.Create(Path.Combine(dir, CoverFormat.FileName(id))))
                CoverFormat.Encode(id, BorderBand(id), fs);   // open grass, the canton borders' band (#1038)
            using (var fs = File.Create(Path.Combine(dir, TreeFormat.FileName(id)))) TreeFormat.Encode(id, [], fs);
            horizon[id] = HorizonFormat.Extract(grid);
            manifest.Tiles.Add(new ManifestTile { E = id.E, N = id.N, Min = h, Max = h });
        }
        using (var fs = File.Create(Path.Combine(dir, HorizonFormat.FileName))) HorizonFormat.Encode(horizon, fs);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest.ToJson());
    }

    // ---- canton borders (#1038) ------------------------------------------------------------------

    /// <summary>The canton of the zone a point lies in (<see cref="CantonTiles"/>), null in the default zone.</summary>
    public static string? CantonAt(double e, double n) => CantonTiles.GetValueOrDefault(((int)Math.Floor(e / 1000), (int)Math.Floor(n / 1000)));

    /// <summary>
    /// A tile edge between two zones: along N (E = <see cref="At"/>, N from <see cref="From"/> to <see cref="To"/>) or along E,
    /// the cantons on its low side (west, south) and its high side (null: the default zone).
    /// </summary>
    private readonly record struct Border(bool AlongN, double At, double From, double To, string? Low, string? High);

    /// <summary>Every edge between two of the region's tiles in different zones.</summary>
    private static List<Border> Borders()
    {
        var borders = new List<Border>();
        for (int e = MinTileE; e <= MaxTileE; e++)
            for (int n = MinTileN; n <= MaxTileN; n++)
            {
                string? here = CantonTiles.GetValueOrDefault((e, n));
                string? east = CantonTiles.GetValueOrDefault((e + 1, n)), north = CantonTiles.GetValueOrDefault((e, n + 1));
                if (e < MaxTileE && east != here) borders.Add(new Border(true, (e + 1) * 1000.0, n * 1000.0, (n + 1) * 1000.0, here, east));
                if (n < MaxTileN && north != here) borders.Add(new Border(false, (n + 1) * 1000.0, e * 1000.0, (e + 1) * 1000.0, here, north));
            }
        return borders;
    }

    /// <summary>A tile's cover: open grass, and <see cref="CoverClass.Border"/> on the vertices within <see cref="BorderBandHalf"/> of a border.</summary>
    private static byte[] BorderBand(TileId id)
    {
        var cells = new byte[CoverFormat.Size * CoverFormat.Size];
        foreach (var b in Borders())
            for (int row = 0; row < CoverFormat.Size; row++)
                for (int col = 0; col < CoverFormat.Size; col++)
                {
                    double e = id.MinE + ChunkFormat.SpacingM * col, n = id.MaxN - ChunkFormat.SpacingM * row;   // row 0 north
                    var (across, along) = b.AlongN ? (e, n) : (n, e);
                    if (Math.Abs(across - b.At) <= BorderBandHalf && along >= b.From - BorderBandHalf && along <= b.To + BorderBandHalf)
                        cells[row * CoverFormat.Size + col] = (byte)CoverClass.Border;
                }
        return cells;
    }

    // a canton sign stands this far past the border along the road, this far out from everything beside the carriageway
    private const double SignPast = 0.5, SignClear = 0.6;

    /// <summary>
    /// A tile's border marks as it is written (#1038, <see cref="TileRewriter.Options.Decorate"/>): where a carriageway crosses a
    /// border, a stripe across it (in the tile whose piece reaches the border from the low side), and a canton sign on each side,
    /// on the right of the traffic entering that canton and facing it, each in the tile its side lies in. The ground band is in
    /// the cover (<see cref="BorderBand"/>): the terrain blends up to the roads, a line painted at the ground's height would sink.
    /// </summary>
    private static void MarkBorders(RoadTile tile)
    {
        const double eps = 1e-3;
        var borders = Borders();
        foreach (var seg in tile.Segments)
        {
            if (seg.Class is not (RoadClass.Major or RoadClass.Road or RoadClass.Minor or RoadClass.Lane)) continue;
            for (int i = 1; i < seg.PointCount; i++)
            {
                var (p0, p1) = (seg.Lv95(tile.Id, i - 1), seg.Lv95(tile.Id, i));
                foreach (var b in borders)
                {
                    double c0 = (b.AlongN ? p0.E : p0.N) - b.At, c1 = (b.AlongN ? p1.E : p1.N) - b.At;
                    // a piece reaching the border from the low side (on to it or across), or one leaving it to the high side
                    bool fromLow = c0 < -eps && c1 > -eps || c1 < -eps && c0 > -eps;
                    bool toHigh = !fromLow && (Math.Abs(c0) <= eps && c1 > eps || Math.Abs(c1) <= eps && c0 > eps);
                    if (!fromLow && !toHigh) continue;
                    double t = -c0 / (c1 - c0);
                    double e = p0.E + (p1.E - p0.E) * t, n = p0.N + (p1.N - p0.N) * t;
                    double along = b.AlongN ? n : e;
                    if (along < b.From || along > b.To) continue;

                    // tile-local: x east, z south; the way to the high side and its right
                    bool forward = c1 > c0;
                    double dx = (p1.E - p0.E) * (forward ? 1 : -1), dz = -(p1.N - p0.N) * (forward ? 1 : -1), length = Math.Sqrt(dx * dx + dz * dz);
                    dx /= length; dz /= length;
                    double rx = -dz, rz = dx;
                    float x = (float)(e - tile.Id.MinE), z = (float)(tile.Id.MaxN - n);
                    float y = seg.Points[(i - 1) * 3 + 1] + (seg.Points[i * 3 + 1] - seg.Points[(i - 1) * 3 + 1]) * (float)t;
                    float half = seg.Width * 0.5f;

                    if (fromLow)
                        tile.Paint.Add(new RoadPaint
                        {
                            Shape = PaintShape.Polyline, Type = PaintType.WhiteSolid, Rgba = BorderRgba, Width = BorderStripe,
                            Vertices = [x - (float)rx * half, y, z - (float)rz * half, x + (float)rx * half, y, z + (float)rz * half],
                        });
                    // the high side's sign where this tile holds the road past the border; the low side's where it holds the road before it
                    if (toHigh || Math.Max(c0, c1) > eps)
                        tile.PointProps.Add(CantonSign(b.High, forward ? seg.Attributes.Right : seg.Attributes.Left, x, y, z, half, dx, dz, rx, rz));
                    if (fromLow)
                        tile.PointProps.Add(CantonSign(b.Low, forward ? seg.Attributes.Left : seg.Attributes.Right, x, y, z, half, -dx, -dz, -rx, -rz));
                }
            }
        }
    }

    /// <summary>The sign for traffic going (dx, dz) from the border point into <paramref name="canton"/>: on its right past the street's side, facing it.</summary>
    private static RoadPointProp CantonSign(string? canton, RoadSide side, float x, float y, float z, float half, double dx, double dz, double rx, double rz)
    {
        double reach = half + side.Reach + SignClear;
        float foot = y + RoadStreetSection.HeightAt(side, side.OuterDm / 10f);
        return new RoadPointProp(PointPropType.CantonSign, RoadSigns.CantonVariant(canton), PropFlags.Solid,
            x + (float)(dx * SignPast + rx * reach), foot, z + (float)(dz * SignPast + rz * reach),
            (float)Math.Atan2(dx, dz), RoadSigns.LowerEdge + RoadSigns.CantonSignHeight);
    }

    /// <summary>Box buildings (walls and a flat roof) as the building stage writes them: tile-local triangles, X east, Y up, Z south.</summary>
    private static void WriteBuildings(string dir, List<TileId> tiles, List<Box> boxes)
    {
        foreach (var id in tiles)
        {
            var list = new List<Building>();
            foreach (var b in boxes.Where(b => TileId.FromLv95((b.E0 + b.E1) * 0.5, (b.N0 + b.N1) * 0.5) == id))
            {
                float x0 = (float)(b.E0 - id.MinE), x1 = (float)(b.E1 - id.MinE);
                float z0 = (float)(id.MaxN - b.N1), z1 = (float)(id.MaxN - b.N0);   // z0 north
                float y0 = (float)Ground, y1 = (float)(Ground + b.Height);
                var t = new List<float>();
                void Quad(float ax, float az, float bx, float bz)   // a wall from a to b, outward on its left seen from above
                {
                    t.AddRange([ax, y0, az, bx, y0, bz, bx, y1, bz]);
                    t.AddRange([ax, y0, az, bx, y1, bz, ax, y1, az]);
                }
                Quad(x0, z0, x1, z0);   // north
                Quad(x1, z0, x1, z1);   // east
                Quad(x1, z1, x0, z1);   // south
                Quad(x0, z1, x0, z0);   // west
                t.AddRange([x0, y1, z0, x0, y1, z1, x1, y1, z1]);   // roof
                t.AddRange([x0, y1, z0, x1, y1, z1, x1, y1, z0]);
                list.Add(new Building { Kind = BuildingKind.Apartment, Floors = (byte)Math.Round(b.Height / 3), MinY = y0, MaxY = y1, Triangles = [.. t] });
            }
            using var fs = File.Create(Path.Combine(dir, BuildingFormat.FileName(id)));
            BuildingCodec.Encode(new BuildingTile { Id = id, Buildings = list }, fs);
        }
    }

    /// <summary>The junctions as places: Tab (or <c>--goto J3</c>) finds them. No buildings counted, so no occasion or town sound picks them.</summary>
    private static void WritePlaces(string dir, List<Junction> junctions)
    {
        var index = new PlaceIndex();
        foreach (var j in junctions)
            index.Places.Add(new Place { Name = j.Name, Canton = CantonAt(j.E, j.N) ?? "", E = j.E, N = j.N, Kind = PlaceKind.Town });
        File.WriteAllText(Path.Combine(dir, PlaceIndex.FileName), index.ToJson());
    }

    /// <summary>
    /// The lines as the road extractor leaves them in <c>roads_raw/</c>: clipped to tiles, densified,
    /// draped on the flat ground (offset and class lift as the extractor's), attributes from the TLM
    /// values through the extractor's parsers, a key per segment (uuid, part 0, along-line metre).
    /// </summary>
    private static List<TileId> WriteRawRoads(string rawDir, List<Line> lines)
    {
        var segments = new SortedDictionary<(int E, int N), (List<RoadSegment> Segments, List<RawRoads.Key?> Keys)>();
        foreach (var line in lines)
        {
            var cls = line.Rail is null ? RoadFormat.ParseClass(line.Objektart) : RoadClass.Railway;
            var surface = line.Rail is null ? RoadFormat.ParseSurface("Hart") : RoadSurface.Unknown;
            var flags = line.Rail ?? RoadFormat.ParseFlags(null, null, null, line.Divided ? "Wahr" : "Falsch");
            // a rail line as the extractor writes it (#1052): its formation's width, auf_strasse
            var attr = line.Rail is not null
                ? new RoadAttributes(Flags: line.OnStreet ? RoadAttrFlags.OnStreet : RoadAttrFlags.None, Layer: RoadFormat.LayerFor(null, flags))
                : new RoadAttributes(
                    Flags: RoadFormat.ParseAttrFlags(line.Ring ? "Wahr" : "Falsch", line.Eigentuemer),
                    Layer: RoadFormat.LayerFor("0", flags),
                    Priority: RoadFormat.PriorityFor(cls, line.Verkehrsbedeutung),
                    WidthCm: RoadFormat.NominalWidthCm(line.Objektart));
            float width = line.Rail is not null
                ? (flags & RoadFlags.DoubleTrack) != 0 ? 9.0f : (flags & RoadFlags.NarrowGauge) != 0 ? 3.6f : 4.6f
                : RoadFormat.WidthFor(cls, flags);
            float y = (float)(Ground + DrapeOffset + (12 - (int)cls) * 0.012);
            double along = 0;
            foreach (var (tile, piece) in SplitAtTiles(line.Points))
            {
                var dense = Densify(piece);
                var pts = new float[dense.Count * 3];
                for (int i = 0; i < dense.Count; i++)
                {
                    pts[i * 3] = (float)(dense[i].E - tile.MinE);
                    pts[i * 3 + 1] = y;
                    pts[i * 3 + 2] = (float)(tile.MaxN - dense[i].N);
                }
                if (!segments.TryGetValue((tile.E, tile.N), out var into)) segments[(tile.E, tile.N)] = into = (new(), new());
                into.Segments.Add(new RoadSegment
                {
                    Class = cls, Surface = surface, Flags = flags, Width = width, Points = pts, Attributes = attr,
                });
                into.Keys.Add(new RawRoads.Key(line.Uuid, 0, along));
                for (int i = 1; i < piece.Count; i++) along += Dist(piece[i - 1], piece[i]);
            }
        }
        var ids = new List<TileId>();
        foreach (var ((e, nn), (list, keys)) in segments)
        {
            var id = new TileId(e, nn);
            RawRoads.Write(rawDir, new RoadTile { Id = id, Segments = list }, keys);
            ids.Add(id);
        }
        return ids;
    }

    private static double Dist((double E, double N) a, (double E, double N) b) =>
        Math.Sqrt((b.E - a.E) * (b.E - a.E) + (b.N - a.N) * (b.N - a.N));

    /// <summary>A polyline cut where it crosses a tile edge, the crossing point ending one piece and starting the next.</summary>
    private static List<(TileId Tile, List<(double E, double N)> Piece)> SplitAtTiles((double E, double N)[] line)
    {
        var pieces = new List<(TileId Tile, List<(double E, double N)> Piece)>();
        for (int i = 1; i < line.Length; i++)
        {
            var (a, b) = (line[i - 1], line[i]);
            var cuts = new List<double> { 0, 1 };
            Crossings(a.E, b.E, cuts);
            Crossings(a.N, b.N, cuts);
            cuts.Sort();
            for (int k = 1; k < cuts.Count; k++)
            {
                var p = At(cuts[k - 1]);
                var q = At(cuts[k]);
                var tile = TileId.FromLv95((p.E + q.E) / 2, (p.N + q.N) / 2);
                if (pieces.Count == 0 || pieces[^1].Tile != tile) pieces.Add((tile, new() { p }));
                pieces[^1].Piece.Add(q);
            }

            (double E, double N) At(double t) => t == 0 ? a : t == 1 ? b : (a.E + (b.E - a.E) * t, a.N + (b.N - a.N) * t);
        }
        return pieces;

        static void Crossings(double from, double to, List<double> into)
        {
            for (double k = Math.Floor(Math.Min(from, to) / 1000) + 1; k * 1000 < Math.Max(from, to); k++)
                into.Add((k * 1000 - from) / (to - from));
        }
    }

    /// <summary>At most <see cref="MaxDrapeSpacing"/> between points, as the extractor drapes.</summary>
    private static List<(double E, double N)> Densify(List<(double E, double N)> piece)
    {
        var dense = new List<(double E, double N)> { piece[0] };
        for (int i = 1; i < piece.Count; i++)
        {
            var (a, b) = (piece[i - 1], piece[i]);
            int steps = Math.Max(1, (int)Math.Ceiling(Dist(a, b) / MaxDrapeSpacing));
            for (int s = 1; s <= steps; s++)
                dense.Add((a.E + (b.E - a.E) * s / steps, a.N + (b.N - a.N) * s / steps));
        }
        return dense;
    }


    /// <summary>The point <paramref name="along"/> metres along a line.</summary>
    private static (double E, double N) PointAlong(Line line, double along)
    {
        for (int i = 1; i < line.Points.Length; i++)
        {
            double d = Dist(line.Points[i - 1], line.Points[i]);
            if (along <= d || i == line.Points.Length - 1)
            {
                double t = d < 1e-9 ? 0 : Math.Clamp(along / d, 0, 1);
                return (line.Points[i - 1].E + (line.Points[i].E - line.Points[i - 1].E) * t, line.Points[i - 1].N + (line.Points[i].N - line.Points[i - 1].N) * t);
            }
            along -= d;
        }
        return line.Points[^1];
    }
    private static double Length(Line line)
    {
        double total = 0;
        for (int i = 1; i < line.Points.Length; i++) total += Dist(line.Points[i - 1], line.Points[i]);
        return total;
    }

    private const string FileTag = "pbf=signal-test-region tlm=signal-test-region";

    /// <summary><c>osm_overlay.tsv</c> (format: docs/notes/tools/osm-overlay.md): one row over each whole line, sorted by uuid.</summary>
    private static void WriteOverlay(string tempDir, List<Line> lines)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(inv, $"# osm_overlay v3 {FileTag} bbox={MinTileE * 1000},{MinTileN * 1000},{(MaxTileE + 1) * 1000},{(MaxTileN + 1) * 1000} synthetic (#386)\n");
        sb.Append("uuid\tpart\tfrom_m\tto_m\tosm_way\tdir\thighway\toneway\tlanes\tlanes_fwd\tlanes_bwd\twidth"
            + "\tsidewalk_left\tsidewalk_right\tcycleway_left\tcycleway_right\tturn_lanes_fwd\tturn_lanes_bwd\troundabout\ttram\tmaxspeed_fwd\tmaxspeed_bwd\tbicycle_lanes_fwd\tbicycle_lanes_bwd\n");
        int way = 0;
        foreach (var line in lines.Where(l => l.Rail is null).OrderBy(l => l.Uuid, StringComparer.Ordinal))
            foreach (var row in line.Rows ?? [new OsmRow(0, Length(line))])
                sb.Append(inv, $"{line.Uuid}\t0\t{row.From:F1}\t{row.To:F1}\t{386000 + ++way}\t+\t{line.Highway}\t\t{row.Lanes}\t{row.LanesFwd}\t{row.LanesBwd}\t")
                    .Append(inv, $"\t{line.Sidewalk}\t{line.Sidewalk}\t{line.Cycleway}\t{line.Cycleway}\t{row.TurnFwd}\t{row.TurnBwd}\t0\t{(row.Tram ? 1 : 0)}\t\t\t{row.BikeFwd}\t{row.BikeBwd}\n");
        File.WriteAllText(Path.Combine(tempDir, "osm_overlay.tsv"), sb.ToString());
    }

    /// <summary>
    /// <c>osm_nodes.tsv</c>: a <c>highway=traffic_signals</c> node on each designed junction's node
    /// (junction <c>node</c>, at the end of one of its lines), sorted by kind and uuid as the writer sorts.
    /// </summary>
    private static void WriteSignals(string tempDir, Design design)
    {
        var inv = CultureInfo.InvariantCulture;
        var rows = new List<(string Uuid, string Text)>();
        long id = 386100;
        // (a junction of several nodes, #1161: a node on each)
        foreach (var (je, jn) in design.Junctions.SelectMany(j => j.Signals ?? [(j.E, j.N)]))
        {
            // the line ending (else starting) at the junction
            var (line, atEnd) = design.Lines.Select(l => (l, true)).FirstOrDefault(x => Dist(x.l.Points[^1], (je, jn)) < 0.01);
            if (line is null) (line, atEnd) = (design.Lines.First(l => Dist(l.Points[0], (je, jn)) < 0.01), false);
            double along = atEnd ? Length(line) : 0;
            rows.Add((line.Uuid, string.Create(inv,
                $"signal\t{++id}\t{je:F1}\t{jn:F1}\t{line.Uuid}\t0\t{along:F1}\tnode\t{(atEnd ? "end" : "start")}\t{je:F1}\t{jn:F1}\t\t\t\t\t\thighway=traffic_signals\n")));
        }
        var sb = new StringBuilder();
        sb.Append(inv, $"# osm_nodes v1 {FileTag} bbox={MinTileE * 1000},{MinTileN * 1000},{(MaxTileE + 1) * 1000},{(MaxTileN + 1) * 1000} synthetic (#386)\n");
        sb.Append("kind\tosm_id\te\tn\tuuid\tpart\talong_m\tjunction\tline_end\tend_e\tend_n\tdir\tvalue\tto_uuid\tto_part\tto_end\ttags\n");
        foreach (var (_, text) in rows.OrderBy(r => r.Uuid, StringComparer.Ordinal)) sb.Append(text);
        // the crossing nodes (#700): on their line, an approach to the line end within 30 m
        long crossingId = 386500;
        foreach (var c in design.Crossings.OrderBy(c => design.Lines.First(l => l.Id == c.LineId).Uuid, StringComparer.Ordinal))
        {
            var line = design.Lines.First(l => l.Id == c.LineId);
            double length = Length(line);
            var (e, nn) = PointAlong(line, c.Along);
            bool atEnd = length - c.Along < c.Along;
            var end = atEnd ? line.Points[^1] : line.Points[0];
            sb.Append(inv, $"crossing\t{++crossingId}\t{e:F1}\t{nn:F1}\t{line.Uuid}\t0\t{c.Along:F1}\tapproach\t{(atEnd ? "end" : "start")}\t{end.E:F1}\t{end.N:F1}\t\t{c.Value}\t\t\t\t{c.Tags ?? "highway=crossing;crossing=" + c.Value}\n");
        }
        File.WriteAllText(Path.Combine(tempDir, OsmNodesReader.FileName), sb.ToString());
    }

    private static string About(Design design)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("Traffic lights test region (#386), written by RoadGen --test-region; rebuilt from scratch on every run.\n");
        sb.Append(inv, $"Tiles E {MinTileE}-{MaxTileE} N {MinTileN}-{MaxTileN}, flat at {Ground:F0} m. docs/notes/tools/signal-test-region.md\n\n");
        foreach (var j in design.Junctions)
            sb.Append(inv, $"{j.Name,-16} {j.E:F0},{j.N:F0}  {j.Case}\n");
        return sb.ToString();
    }

    // ---- report --------------------------------------------------------------------------------

    /// <summary>
    /// The junction table: per designed junction its plan's cycle and phases, and per approach the
    /// lanes as built (the <c>LANE</c> record, left to right) against the design. Returns how many
    /// designed approaches came out otherwise (or are missing).
    /// </summary>
    public static int Report(string chunkDir, IReadOnlyList<Junction> junctions, Action<string> log)
    {
        var inv = CultureInfo.InvariantCulture;
        var tiles = new Dictionary<TileId, RoadTile>();
        RoadTile? Tile(TileId id)
        {
            if (tiles.TryGetValue(id, out var t)) return t;
            string path = Path.Combine(chunkDir, RoadFormat.FileName(id));
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return tiles[id] = RoadCodec.Decode(fs);
        }

        int bad = 0;
        log("junction         LV95 centre        approach: lanes as built, left to right (designed)        cycle  phases");
        foreach (var j in junctions)
        {
            var id = TileId.FromLv95(j.E, j.N);
            var tile = Tile(id);
            int k = -1;
            double best = 15;
            for (int i = 0; tile is not null && i < tile.Signals.Count; i++)
            {
                double d = Math.Sqrt(Math.Pow(id.MinE + tile.Signals[i].X - j.E, 2) + Math.Pow(id.MaxN - tile.Signals[i].Z - j.N, 2));
                if (d < best) { best = d; k = i; }
            }
            if (tile is null || k < 0)
            {
                log(string.Create(inv, $"{j.Name,-16} {j.E:F0},{j.N:F0}    NO TRAFFIC LIGHTS"));
                bad += j.Design.Count(a => a.Value is not null);
                continue;
            }
            var signal = tile.Signals[k];
            var plan = signal.Plan;
            var arms = new List<string>();
            var seen = new HashSet<string>();
            foreach (var a in tile.Approaches.Where(a => a.Signal == k).OrderBy(a => Compass(a.Heading)))
            {
                string dir = Compass(a.Heading);
                seen.Add(dir);
                string car = string.Join("|", a.Lanes.Where(l => l.Kind == ApproachLaneKind.Car).Select(l => Moves(l.Moves)));
                string all = string.Join(" | ", a.Lanes.Select(l => l.Kind switch { ApproachLaneKind.Bike => "b", ApproachLaneKind.Tram => "tram:", _ => "" } + Moves(l.Moves)
                    + (l.StopBehind > 3.5f ? "[box]" : l.StopBehind > 0.01f ? "[adv]" : "")));
                var arm = plan.Arms[a.SignalArm];
                string designed = j.Design.GetValueOrDefault(dir) ?? "-";
                bool ok = designed == "-" || designed == car;
                if (!ok) bad++;
                arms.Add($"{dir}: {all}{(arm.BikeSignal ? " +bike signal" : "")} ({designed}{(ok ? "" : " MISMATCH")})");
            }
            foreach (var (dir, want) in j.Design)
                if (want is not null && !seen.Contains(dir)) { bad++; arms.Add($"{dir}: MISSING ({want})"); }
            // the canton's look (#1038): two-lens pedestrian heads (Geneva), arrow plates (Vaud)
            string look = (plan.PedestrianAmber ? "" : "  2-lens peds") + (plan.ArrowPlates ? "  arrow plates" : "");
            log(string.Create(inv, $"{j.Name,-16} {j.E:F0},{j.N:F0}   {string.Join("; ", arms)}   {plan.Cycle:F0} s{look}   {Phases(plan)}"));
        }
        return bad;
    }

    /// <summary>
    /// The border marks as written (#1038): stripes across carriageways and canton signs, by canton. Every crossing has a stripe and
    /// a sign each way; returns 1 when the counts disagree or nothing crosses a border.
    /// </summary>
    private static int BorderReport(string chunkDir, Action<string> log)
    {
        int stripes = 0;
        var signs = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(chunkDir, "*" + Path.GetExtension(RoadFormat.FileName(new TileId(MinTileE, MinTileN)))))
        {
            RoadTile tile;
            using (var fs = File.OpenRead(path)) tile = RoadCodec.Decode(fs);
            stripes += tile.Paint.Count(p => p.Rgba == BorderRgba);
            foreach (var p in tile.PointProps.Where(p => p.Type == PointPropType.CantonSign))
            {
                string name = RoadSigns.CantonCode(p.Variant) ?? "default";
                signs[name] = signs.GetValueOrDefault(name) + 1;
            }
        }
        int total = signs.Values.Sum();
        log($"canton borders (#1038): {stripes} stripes across carriageways, {total} signs ({string.Join(", ", signs.Select(s => $"{s.Key} {s.Value}"))})");
        return stripes > 0 && total == 2 * stripes ? 0 : 1;
    }

    /// <summary>
    /// The junction planner's mouth sections (#818) against the design: per designed junction its arms' arriving car lanes
    /// as planned, left to right. Returns how many designed approaches came out otherwise (or are missing).
    /// </summary>
    /// <summary>
    /// Where the planner's lanes are meant to differ from the pocket layout's design (#818, the user's decisions), by junction
    /// and arm (J3 N: room for a pocket or a through bike lane, not both: the bike lane stays, as designed, LTR). J3 S (#971): its
    /// 52 m hold the west arm's right pocket's exit (a right pocket at the lights in town since then), so its own right pocket goes.
    /// Merged into <see cref="Junction.Design"/> at the switch.
    /// </summary>
    public static readonly IReadOnlyDictionary<(string Junction, string Arm), string> PlannerDesign =
        new Dictionary<(string, string), string> { [("J3-mismatch", "S")] = "LTR" };

    public static int ReportPlans(IReadOnlyList<Layout.JunctionPlan> plans, IReadOnlyList<Junction> junctions, Action<string> log)
    {
        int bad = 0;
        log("junction         planned arriving car lanes, left to right (designed)");
        foreach (var j in junctions)
        {
            var plan = plans.Where(p => Math.Abs(p.Centre.X - j.E) < 15 && Math.Abs(p.Centre.Y - j.N) < 15)
                .OrderBy(p => Math.Abs(p.Centre.X - j.E) + Math.Abs(p.Centre.Y - j.N)).FirstOrDefault();
            if (plan is null)
            {
                log(string.Create(CultureInfo.InvariantCulture, $"{j.Name,-16} NO PLAN"));
                bad += j.Design.Count(a => a.Value is not null);
                continue;
            }
            var arms = new List<string>();
            var seen = new HashSet<string>();
            foreach (var a in plan.Arms.OrderBy(a => Layout.JunctionPlan.Compass(a.Heading)))
            {
                string dir = Layout.JunctionPlan.Compass(a.Heading);
                seen.Add(dir);
                string car = a.Mouth.CarLanesIn();
                if (car.Length == 0) continue;
                string designed = PlannerDesign.GetValueOrDefault((j.Name, dir)) ?? j.Design.GetValueOrDefault(dir) ?? "-";
                bool ok = designed == "-" || designed == car;
                if (!ok) bad++;
                arms.Add($"{dir}: {car} ({designed}{(ok ? "" : " MISMATCH")}){(a.Steps.Count + a.FitSteps.Count > 0 ? " [" + string.Join("; ", a.Steps.Concat(a.FitSteps)) + "]" : "")}");
            }
            foreach (var (dir, want) in j.Design)
                if (want is not null && !seen.Contains(dir)) { bad++; arms.Add($"{dir}: MISSING ({want})"); }
            log($"{j.Name,-16} {string.Join("; ", arms)}");
        }
        return bad;
    }

    /// <summary>The approach direction an arm's outward heading names: the west arm's traffic comes from the west.</summary>
    private static string Compass(double heading)
    {
        double deg = (heading * 180 / Math.PI % 360 + 360) % 360;
        return deg < 45 || deg >= 315 ? "E" : deg < 135 ? "N" : deg < 225 ? "W" : "S";
    }

    private static string Moves(SignalMoves m) =>
        (m.HasFlag(SignalMoves.Left) ? "L" : "") + (m.HasFlag(SignalMoves.Through) ? "T" : "") + (m.HasFlag(SignalMoves.Right) ? "R" : "");

    /// <summary>The plan's phases: the car groups green together, in cycle order (arrows marked ^), all-red gaps left out.</summary>
    public static string Phases(SignalPlan plan)
    {
        var phases = new List<string>();
        string last = "";
        for (double t = 0; t < plan.Cycle; t += 0.25)
        {
            var green = new List<string>();
            for (int g = 0; g < plan.Groups.Count; g++)
            {
                var group = plan.Groups[g];
                if (group.Kind is not (SignalGroupKind.Car or SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow or SignalGroupKind.Tram)) continue;
                var iv = group.Intervals.FirstOrDefault(x => x.From <= t && t < x.To);
                if (iv.Aspect != SignalAspect.Green) continue;
                green.Add(Compass(plan.Arms[group.Arm].Heading) + ":" + (group.Kind == SignalGroupKind.Tram ? "tram" : "") + Moves(group.Moves)
                    + (group.Kind is SignalGroupKind.Car or SignalGroupKind.Tram ? "" : "^"));
            }
            string set = string.Join(" ", green.Order(StringComparer.Ordinal));
            if (set.Length == 0 || set == last) continue;
            if (phases.Count == 0 || phases[^1] != set) phases.Add(set);
            last = set;
        }
        if (phases.Count > 1 && phases[0] == phases[^1]) phases.RemoveAt(phases.Count - 1);   // the cycle wraps
        return string.Join(" / ", phases.Select(p => "[" + p + "]"));
    }
}
