using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Traffic-light poles and heads (#350) from a tile's <see cref="RoadSignal"/> records: the
/// silver-grey pole (RAL 7001), black housings (RAL 9017) on a black backboard with a white
/// border (SSV Art. 70), and where each lens is. Runs on the build worker: the poles, housings and
/// boards go into the road mesh (one draw call with the rest), the lenses into
/// <see cref="Lamps"/>, which <see cref="SignalLamps"/> draws as instanced quads coloured from the
/// plan and the server clock. Collision is the pole only. Layout per pole, from the driver's view:
/// the left arrow, the main head, the right arrow (or the right lane's full head), the flasher
/// beside the green it qualifies; a second pole (on the approach's left) never shows the right
/// arrow; a pedestrian head faces across the crossing, below the car heads. Sizes from SSV Art. 70/71
/// and the Kanton Zürich Wegleitung LSA (docs/notes/tools/traffic-signals.md).
/// </summary>
public static class SignalBuilder
{
    private static readonly Color PoleColour = new(0.55f, 0.59f, 0.62f);   // RAL 7001 silver grey
    private static readonly Color Housing = new(0.07f, 0.07f, 0.08f);      // RAL 9017 traffic black
    private static readonly Color WhitePlate = new(0.93f, 0.93f, 0.91f);   // RAL 9016 traffic white
    private static readonly Color SignYellow = new(0.98f, 0.76f, 0.05f);   // signal 5.18's yellow bike and arrow, on black
    private static readonly Color Rim = new(0.20f, 0.20f, 0.22f);           // the lens ring, a shade off the housing

    /// <summary>Lens pitch and the 200 mm lens drawn a little smaller than its housing's cell.</summary>
    public const float Pitch = 0.3f, LensRadius = 0.09f;
    private const float HeadWidth = 0.3f, HeadDepth = 0.22f, BoardMargin = 0.1f, BoardThickness = 0.02f;
    /// <summary>
    /// Romandy's tram head (#1105, the user's photos): a one-lens car head module, its lens dark with the white T lit in it; the
    /// small "tram detected" dot above the T (its radius and height in lens radii), never lit.
    /// </summary>
    private const float TramDetectRadius = 0.1f, TramDetectAt = 0.66f;
    /// <summary>Romandy's tram head to the next one or to its car head, plate against plate (#1105: packed, no gap).</summary>
    private const float TramPack = HeadWidth + 2f * (BorderGap + BorderWidth);
    /// <summary>Romandy's tram heads a column beside the car heads holds, about their height (#1105); more go on top of them.</summary>
    private const int TramColumn = 2;
    private static readonly Color TramLens = new(0.05f, 0.05f, 0.06f), TramDetect = new(0.22f, 0.22f, 0.24f);
    /// <summary>
    /// A car head's plate (#759, from a photo of a Swiss junction and the user's review): a white
    /// band <see cref="BorderWidth"/> wide all round, black behind, flush with the housing's face
    /// and <see cref="BorderGap"/> clear of it. Heads side by side keep 5 cm between their plates.
    /// </summary>
    private const float BorderGap = 0.02f, BorderWidth = 0.08f;
    /// <summary>
    /// The arrow under a head (#759): where the canton's plates carry it (<see cref="SignalPlan.ArrowPlates"/>)
    /// the white band runs on this far below the housing; a bike head elsewhere has a plate of its
    /// own this tall, just below its housing. The arrow is this much of the panel's height.
    /// </summary>
    private const float ArrowPanel = 0.16f, ArrowFill = 0.62f;

    /// <summary>
    /// The housing's rounded edges, each lens's visor over its top (open below, longer at the
    /// crown) and the ring round it (#759: the heads were plain boxes). Scaled with the head.
    /// </summary>
    private const float CornerRadius = 0.04f, VisorLength = 0.13f, VisorGap = 0.02f, RimWidth = 0.018f;
    private const int PoleSides = 12, VisorSegments = 6, RimSegments = 12;
    /// <summary>
    /// The road shader's styles for a prop that keeps its colour (no asphalt over it), and for a
    /// plate that is its colour in front and black behind (#759).
    /// </summary>
    private const float PropStyle = 7f, PlateStyle = 8f;
    /// <summary>Head centre to head centre across a pole.</summary>
    private const float HeadSpacing = 0.55f;
    /// <summary>Lower edge of the car heads at the roadside, 2.35-3.50 m (SSV Art. 71): low alone, high above a pedestrian head.</summary>
    private const float CarLowerEdge = 2.35f, CarLowerEdgeAbovePedestrian = 3.35f, PedestrianLowerEdge = 2.3f;
    private const float PoleRadius = RoadSigns.PoleDiameter * 0.5f, Sink = 0.5f;
    /// <summary>A head's face stands this far in front of the pole's axis: the pole, the board, the housing.</summary>
    private const float InFront = PoleRadius + BoardThickness + HeadDepth + 0.01f;

    /// <summary>A lens's shape; <c>Tram</c>: a tram bar signal's lamp (#1057), its white bar or dot by its role.</summary>
    public enum Shape : byte { Circle = 0, LeftArrow = 1, RightArrow = 2, Square = 3, Bike = 4, Tram = 5, TramT = 6 }

    /// <summary>Shapes there are (one lens mesh and one MultiMesh each).</summary>
    public const int ShapeCount = 7;

    /// <summary>
    /// A bike head (#351): 100 mm lenses (Stadt Zürich Velostandards LSA), half a car head, low on
    /// the main pole below the priority sign, facing the approach; its middle lens at a rider's shoulder (the user's review,
    /// #711: 1.05 m was below it).
    /// </summary>
    private const float BikeScale = 0.5f, BikeLowerEdge = 1.2f;

    public enum Role : byte { Red, Amber, Green, Flash }

    /// <summary>One lens: its instanced shape and transform (tile-local, +Z toward the viewer), which junction and group light it, and as what.</summary>
    /// <param name="Flash">A yellow lens that also flashes with this flasher group (#971: a bike head's, its riders turning right
    /// across a crossing green with them), -1 none.</param>
    /// <param name="Arrow">A round lens's arrow when not its group's (#1037: an overhead head shows its own lanes' moves), null the group's.</param>
    /// <param name="Blink">A permissive turn's flashing yellow (#1109, <see cref="SignalPlan.LeftFlash"/>, <see cref="SignalPlan.TurnFlash"/>):
    /// a ball head's yellow lens flashes with it, and the lamp left of a combined arrow head; an uncombined arrow's yellow flashes
    /// and its green stays dark meanwhile; null none.</param>
    public readonly record struct Lens(Shape Shape, Transform3D Transform, int Junction, int Group, Role Role, int Flash = -1, SignalMoves? Arrow = null,
        SignalGroup? Blink = null);

    /// <summary>A tile's lenses and the plans that light them.</summary>
    public sealed class Lamps
    {
        public readonly List<SignalPlan> Plans = new();
        public readonly List<Lens> Lenses = new();
    }

    /// <summary>
    /// A head on a pole: its centre, the way it faces and its right as the viewer sees it, its group, its lenses; its flasher on its
    /// right and that flasher's plate (#1109), and the flasher on its left of a permissive left shown by an arrow (#1109); where
    /// each flasher stands, in head spacings to the head's right (negative: its left).
    /// </summary>
    private readonly record struct Head(Vector3 Centre, Vector3 Front, Vector3 Right, int Group, Shape Shape, int Lenses, bool Flasher,
        float Scale = 1f, bool SideBySide = false, SignalMoves? Arrow = null, SignalPlate Plate = SignalPlate.None,
        SignalMoves PlateArrow = SignalMoves.None, bool LeftFlasher = false, float FlasherSide = 1f, float LeftSide = -1f)
    {
        /// <summary>Half the housing's height.</summary>
        public float Half => SideBySide ? GenevaHalfHeight : Lenses * Pitch * Scale * 0.5f;
    }

    /// <summary>
    /// Geneva's pedestrian head (#759, from the user's photo): a light grey housing with one dark
    /// window, the red standing figure on the left and the green walking one on the right, side
    /// by side and larger than a 200 mm lens. The two-lens heads (no yellow) are Geneva's.
    /// </summary>
    private const float GenevaHalfWidth = 0.21f, GenevaHalfHeight = 0.17f, GenevaDepth = 0.14f, GenevaFigureX = 0.1f, GenevaFigure = 1.45f;
    private static readonly Color GenevaGrey = new(0.70f, 0.72f, 0.74f);
    private static readonly Color Window = new(0.04f, 0.04f, 0.05f);

    /// <summary>Appends every pole, housing and backboard of the tile, and its overhead structures (#1037), to a road mesh under construction.</summary>
    /// <param name="rounded">The plates' corners are rounded (Cartoon's mesh detail, #759).</param>
    public static void Append(RoadTile tile, List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices, bool rounded = false)
    {
        foreach (var signal in tile.Signals)
        {
            var colour = PoleColourOf(signal.Plan.PoleColour).SrgbToLinear();
            foreach (var pole in signal.Poles)
            {
                var heads = PoleHeads(signal, pole);
                var foot = new Vector3(pole.X, pole.Y, pole.Z);
                float top = 0f;
                foreach (var h in heads) top = Mathf.Max(top, h.Centre.Y - pole.Y + h.Half + BoardMargin);
                // a mast arm's or portal's post stands on the pole and carries its heads: no pole beside it (#1037, the user)
                if (!signal.Overheads.Any(o => o.StandsOn(pole.X, pole.Z)))
                    Column(vertices, colors, uvs, uv2s, indices, foot, top, colour);
                RightOnRedSign(vertices, colors, uvs, uv2s, indices, signal.Plan, pole, heads);   // #711
                foreach (var h in heads)
                {
                    // a bracket from the pole into the back of the head's housing (not through to its face)
                    var reach = h.Centre - h.Front * ((h.SideBySide ? GenevaDepth : HeadDepth * h.Scale) * 0.8f) - foot;
                    reach.Y = 0;
                    if (reach.Length() > PoleRadius * 2)
                        Tube(vertices, colors, uvs, uv2s, indices, Housing.SrgbToLinear(),
                            new Vector3(foot.X, h.Centre.Y, foot.Z), new Vector3(foot.X, h.Centre.Y, foot.Z) + reach, 0.025f);
                    DrawHead(vertices, colors, uvs, uv2s, indices, signal.Plan, h, rounded, signal.Plan.ArrowPlates);
                }
            }
            foreach (var o in signal.Overheads)
                Overhead(vertices, colors, uvs, uv2s, indices, signal.Plan, o, colour, rounded);
        }
    }

    /// <summary>
    /// A head's backboard, housing, lens fittings and flasher (#759). <paramref name="arrowsBelow"/>: the canton's plates run on
    /// below car heads with their arrow (<see cref="SignalPlan.ArrowPlates"/>; an overhead head's arrow is on a plate above it).
    /// </summary>
    private static void DrawHead(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        SignalPlan plan, Head h, bool rounded, bool arrowsBelow)
    {
        float half = h.Half, w = HeadWidth * h.Scale, depth = HeadDepth * h.Scale;
        // the backboard (#759): a car head's plate flush with the housing's face, clear of it by
        // a hair, white in front and black behind (one face, PlateStyle); a bike head's only in
        // some cantons; a pedestrian head has none
        // (Romandy's tram head is a car head module, its plate too: #1105)
        bool board = h.Shape is not (Shape.Square or Shape.Tram) && (h.Shape != Shape.Bike || arrowsBelow);
        float gap = BorderGap * h.Scale, band = BorderWidth * h.Scale,
            corner = rounded ? CornerRadius * h.Scale : 0f;
        // the arrow under the head: on the band run on below it where the canton does that,
        // on a small plate of its own under a bike head elsewhere
        // (a tram head's where its approach's trams go more than one way: one head per direction, #1057, the user; Romandy's always)
        var arrow = h.Shape is Shape.Bike or Shape.Tram or Shape.TramT || arrowsBelow ? HeadMoves(plan, h) : SignalMoves.None;
        float panel = arrow != SignalMoves.None ? ArrowPanel * h.Scale : 0f;
        var face = h.Centre - h.Front * 0.002f;
        if (board)
        {
            BorderFrame(vertices, colors, uvs, uv2s, indices, WhitePlate.SrgbToLinear(), face,
                h.Right, w * 0.5f + gap, half + gap, band, corner, below: panel);
            if (panel > 0f)
                ArrowOn(vertices, colors, uvs, uv2s, indices, arrow, face + h.Front * 0.01f + Vector3.Down * (half + gap + (band + panel) * 0.5f),
                    h.Right, (band + panel) * 0.5f * ArrowFill);
        }
        else if (panel > 0f)
        {
            // a bike head's own arrow plate, white with its back black, just below the housing
            float below = half + gap, height = panel + band;
            var at = face + Vector3.Down * (below + height * 0.5f);
            RoundedPlate(vertices, colors, uvs, uv2s, indices, WhitePlate.SrgbToLinear(), at, h.Right, w * 0.5f + gap, height * 0.5f, corner);
            ArrowOn(vertices, colors, uvs, uv2s, indices, arrow, at + h.Front * 0.01f, h.Right, height * 0.5f * ArrowFill);
        }
        if (h.SideBySide)
        {
            RoundedHousing(vertices, colors, uvs, uv2s, indices, h.Centre - h.Front * (GenevaDepth * 0.5f), h.Right, h.Front,
                GenevaHalfWidth, GenevaDepth * 0.5f, GenevaHalfHeight, CornerRadius, GenevaGrey);
            var window = Outline(GenevaHalfWidth - 0.025f, GenevaHalfHeight - 0.025f, CornerRadius * 0.6f, 2);
            var ring = new Vector3[window.Count];
            for (int i = 0; i < window.Count; i++)
                ring[window.Count - 1 - i] = h.Centre + h.Front * 0.01f + h.Right * window[i].X + Vector3.Up * window[i].Y;
            Polygon(vertices, colors, uvs, uv2s, indices, Window.SrgbToLinear(), ring);
            return;
        }
        RoundedHousing(vertices, colors, uvs, uv2s, indices, h.Centre - h.Front * (depth * 0.5f), h.Right, h.Front,
            w * 0.5f, depth * 0.5f, half, CornerRadius * h.Scale);
        float pitch = Pitch * h.Scale;
        for (int k = 0; k < h.Lenses; k++)
            LensFittings(vertices, colors, uvs, uv2s, indices, h.Centre + Vector3.Up * (half - pitch * (k + 0.5f)),
                h.Right, h.Front, h.Scale, square: h.Shape == Shape.Square);
        if (h.Shape == Shape.TramT)
        {
            // Romandy's tram head (#1105): its lens dark, the T's solid strokes lit in it (SignalLamps), and over the T the small
            // "tram detected" dot, unlit (the lights run on fixed cycles)
            float r = LensRadius * h.Scale;
            Polygon(vertices, colors, uvs, uv2s, indices, TramLens.SrgbToLinear(), Circle(h.Centre + h.Front * 0.003f, h.Right, r, 16));
            Polygon(vertices, colors, uvs, uv2s, indices, TramDetect.SrgbToLinear(),
                Circle(h.Centre + h.Front * 0.008f + Vector3.Up * (TramDetectAt * r), h.Right, TramDetectRadius * r, 8));
        }
        if (h.Flasher)
            FlasherLamp(vertices, colors, uvs, uv2s, indices, h, h.FlasherSide, h.Plate, h.PlateArrow, board, corner);
        // a permissive left shown by an arrow (#1109, the user): its own flasher on its left, a left arrow on its plate
        if (h.LeftFlasher)
            FlasherLamp(vertices, colors, uvs, uv2s, indices, h, h.LeftSide, SignalPlate.Arrow, SignalMoves.Left, board, corner);
    }

    /// <summary>Where a head's flasher lamp is: level with its green, <paramref name="side"/> head spacings to its right (negative: its left).</summary>
    private static Vector3 FlasherAt(Head h, float side) => h.Centre + h.Right * (side * HeadSpacing) + Vector3.Down * (h.Half - Pitch * 0.5f);

    /// <summary>
    /// A flasher lamp beside head <paramref name="h"/> (#759, #771): its own small housing spaced as a head beside it, held by a
    /// bracket from the side of the head's housing, its own white band; that band run on below it as a plate saying what it warns
    /// of (#1109, the user: a pedestrian, a bike, or the turn's arrow where it crosses both), under the lamp so a Romandy tram
    /// head over it still fits (#1105).
    /// </summary>
    private static void FlasherLamp(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        Head h, float side, SignalPlate plate, SignalMoves arrow, bool board, float corner)
    {
        var at = FlasherAt(h, side);
        float w = HeadWidth * h.Scale;
        RoundedHousing(vertices, colors, uvs, uv2s, indices, at - h.Front * (HeadDepth * 0.5f), h.Right, h.Front,
            HeadWidth * 0.5f, HeadDepth * 0.5f, Pitch * 0.5f, CornerRadius);
        LensFittings(vertices, colors, uvs, uv2s, indices, at, h.Right, h.Front, 1f, square: false);
        // behind the plates (#771)
        var back = h.Front * (HeadDepth * 0.6f);
        float sign = Mathf.Sign(side);
        Tube(vertices, colors, uvs, uv2s, indices, Housing.SrgbToLinear(),
            at - h.Right * (side * HeadSpacing - sign * w * 0.5f) - back, at - h.Right * (sign * HeadWidth * 0.5f) - back, 0.025f);
        float panel = plate != SignalPlate.None ? ArrowPanel : 0f;
        var face = at - h.Front * 0.002f;
        if (board)
            BorderFrame(vertices, colors, uvs, uv2s, indices, WhitePlate.SrgbToLinear(), face,
                h.Right, HeadWidth * 0.5f + BorderGap, Pitch * 0.5f + BorderGap, BorderWidth, corner, below: panel);
        else if (panel > 0f)
            RoundedPlate(vertices, colors, uvs, uv2s, indices, WhitePlate.SrgbToLinear(),
                face + Vector3.Down * (Pitch * 0.5f + BorderGap + (BorderWidth + panel) * 0.5f), h.Right, HeadWidth * 0.5f + BorderGap,
                (BorderWidth + panel) * 0.5f, corner);
        if (panel <= 0f) return;
        // the symbol in the plate's lower part, black, 1 cm proud of it (#876)
        float room = (BorderWidth + panel) * 0.5f;
        var centre = face + h.Front * 0.01f + Vector3.Down * (Pitch * 0.5f + BorderGap + room);
        var glyph = plate switch
        {
            SignalPlate.Pedestrian => SignalGlyphs.Walker(),
            SignalPlate.Bike => SignalGlyphs.Bike(),
            _ => SignalGlyphs.Arrow(arrow),
        };
        GlyphOn(vertices, colors, uvs, uv2s, indices, glyph, centre, h.Right, room * (plate == SignalPlate.Arrow ? ArrowFill : PlateFigureFill));
    }

    /// <summary>A figure on a flasher's plate (#1109): the walker or the bike this much of the plate's half height to a lens radius.</summary>
    private const float PlateFigureFill = 1f;

    // ---- overhead heads (#1037) --------------------------------------------------------------

    /// <summary>
    /// The canton's colour for its poles and overhead structures (#1037): RAL 7001 silver grey, Basel-Landschaft's anthracite
    /// (DB703), Bern's RAL 7042 traffic grey A.
    /// </summary>
    private static Color PoleColourOf(SignalPoleColour colour) => colour switch
    {
        SignalPoleColour.Anthracite => new Color(0.28f, 0.29f, 0.30f),
        SignalPoleColour.TrafficGrey => new Color(0.56f, 0.57f, 0.57f),
        _ => PoleColour,
    };

    /// <summary>An overhead head's lenses are 300 mm (Bern, Basel-Landschaft): 1.5 times a kerb head's 200 mm.</summary>
    private const float OverheadScale = 1.5f;
    /// <summary>
    /// Basel-Landschaft's mast arms and portals (drawings TZ-1205/1206/1208): posts and arms of one-piece square hollow sections,
    /// RRW 300 the post and 300 x 200 the arm, hung this far above the heads' lane plates; past <see cref="TieFrom"/> m an arm
    /// has a tie rod ("ab 7 m mit Abspannung") (m).
    /// </summary>
    private const float PostHalf = SignalOverhead.PostHalf, BeamHeight = 0.3f, BeamDepth = 0.2f, HangerGap = 0.25f, TieFrom = 7f;
    /// <summary>A span wire hangs between two facades (#1037, the user: between buildings, never on poles), from a plate on each wall.</summary>
    private const float WireRadius = 0.012f, WireSag = 0.25f, HangerHalf = 0.025f, WallPlate = 0.12f;
    /// <summary>The heads an overhead structure carries, each over its lanes (with its record): a turn's arrow head, else the approach's main head.</summary>
    private static List<(Head Head, OverheadHead Record)> OverheadHeads(SignalPlan plan, SignalOverhead o)
    {
        var heads = new List<(Head, OverheadHead)>();
        var (front, right) = Frame(o.Heading);
        var a = new Vector3(o.X, o.Y, o.Z);
        var dir = new Vector3(o.EndX - o.X, 0f, o.EndZ - o.Z).Normalized();
        float half = 1.5f * Pitch * OverheadScale;
        foreach (var h in o.Heads)
        {
            var (group, shape) = OverheadGroup(plan, o.Arm, h.Moves);
            if (group < 0) continue;
            // on an arm or a portal mounted in front of the beam, its back a bracket's length off the beam's face (the user: mounted
            // onto the arm, not hung); on a span wire under it
            float ahead = o.Kind == OverheadKind.Span ? HeadDepth * OverheadScale * 0.5f : BeamDepth * 0.5f + MountGap + HeadDepth * OverheadScale;
            var at = a + dir * h.Along + front * ahead;
            // drawn as a pole's head (the user): where the canton's plates run on below the heads with their arrow, the plate's
            // lower edge stays at the structure's
            at.Y = o.Bottom + (BorderGap + BorderWidth + (plan.ArrowPlates ? ArrowPanel : 0f)) * OverheadScale + half;
            // its round lenses show its own lanes' moves (the user, #1037: the head over the through lanes is not the kerb lane's
            // through-and-right)
            var arrow = shape == Shape.Circle ? LaneArrow(plan, o.Arm, h.Moves) : (SignalMoves?)null;
            heads.Add((new Head(at, front, right, group, shape, 3, false, OverheadScale, Arrow: arrow), h));
        }
        return heads;
    }

    /// <summary>
    /// The arrow over lanes making <paramref name="moves"/> (#1037): a ball where they make every move the approach has (or none is
    /// known), else their moves, as <see cref="ArrowMoves"/> does for a group.
    /// </summary>
    private static SignalMoves LaneArrow(SignalPlan plan, int arm, SignalMoves moves)
    {
        var all = SignalMoves.None;
        foreach (var g in plan.Groups)
            if (g.Arm == arm && g.Kind is SignalGroupKind.Car or SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow) all |= g.Moves;
        moves &= all;
        return moves == SignalMoves.None || moves == all || moves == (SignalMoves.Left | SignalMoves.Right) ? SignalMoves.None : moves;
    }

    /// <summary>The group and shape of an overhead head over lanes making <paramref name="moves"/>.</summary>
    private static (int Group, Shape Shape) OverheadGroup(SignalPlan plan, int arm, SignalMoves moves)
    {
        if (moves == SignalMoves.Left && Find(plan, SignalGroupKind.LeftArrow, arm, SignalMoves.Left) is var l and >= 0) return (l, Shape.LeftArrow);
        if (moves == SignalMoves.Right)
        {
            if (Find(plan, SignalGroupKind.RightArrow, arm, SignalMoves.Right) is var r and >= 0) return (r, Shape.RightArrow);
            int lane = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Car && g.Arm == arm && g.Moves == SignalMoves.Right);
            if (lane >= 0) return (lane, Shape.Circle);
        }
        return (MainCar(plan, arm), Shape.Circle);
    }

    /// <summary>A head mounted on an arm or beam stands this far off its face (m): the bracket between.</summary>
    private const float MountGap = 0.04f;

    /// <summary>The top of an overhead head's plate above the plates' lower edge.</summary>
    private static float PlateTop(SignalPlan plan) =>
        ((BorderGap + BorderWidth) * 2 + (plan.ArrowPlates ? ArrowPanel : 0f) + 3 * Pitch) * OverheadScale;

    /// <summary>
    /// An overhead structure and its heads (#1037): a mast arm (its post by the main pole or on the refuge, a tie rod past
    /// <see cref="TieFrom"/> m), a portal (two posts, a beam), or a span wire between two facades; each head mounted on the front
    /// of the arm or beam at its mid-height (hung from the wire), drawn as a pole's (the canton's plates and arrows).
    /// </summary>
    private static void Overhead(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        SignalPlan plan, SignalOverhead o, Color colour, bool rounded)
    {
        var a = new Vector3(o.X, o.Y, o.Z);
        var b = new Vector3(o.EndX, o.EndY, o.EndZ);
        var dir = new Vector3(b.X - a.X, 0f, b.Z - a.Z);
        float length = dir.Length();
        if (length < 0.5f) return;
        dir /= length;
        var depth = dir.Cross(Vector3.Up).Normalized();
        var heads = OverheadHeads(plan, o);
        // the beam's lower face: on an arm or portal the beam runs behind the heads' middle, a wire above their plates
        float plateTop = o.Bottom + PlateTop(plan);
        float beam = o.Kind == OverheadKind.Span || heads.Count == 0 ? plateTop + HangerGap : heads[0].Head.Centre.Y - BeamHeight * 0.5f;
        Func<float, float> hangFrom;   // the underside of what carries a head, at a distance along the structure
        if (o.Kind == OverheadKind.Span)
        {
            // the wire's lowest point just above the plates, its ends on a plate on each facade
            float ends = beam + WireSag;
            foreach (var (end, inward) in new[] { (a, dir), (b, -dir) })
            {
                var at = end; at.Y = ends;
                Beam(vertices, colors, uvs, uv2s, indices, at - inward * 0.03f, at + inward * 0.03f, depth, WallPlate, WallPlate, Housing.SrgbToLinear());
            }
            float WireY(float along) { float t = Mathf.Clamp(along / length, 0f, 1f); return ends - 4f * WireSag * t * (1f - t); }
            const int Pieces = 16;
            for (int k = 0; k < Pieces; k++)
            {
                float d0 = length * k / Pieces, d1 = length * (k + 1) / Pieces;
                var p0 = a + dir * d0; p0.Y = WireY(d0);
                var p1 = a + dir * d1; p1.Y = WireY(d1);
                Tube(vertices, colors, uvs, uv2s, indices, Housing.SrgbToLinear(), p0, p1, WireRadius);
            }
            hangFrom = WireY;
        }
        else
        {
            float beamTop = beam + BeamHeight;
            Post(vertices, colors, uvs, uv2s, indices, a, beamTop, dir, depth, PostHalf, colour);
            if (o.Kind == OverheadKind.Portal) Post(vertices, colors, uvs, uv2s, indices, b, beamTop, dir, depth, PostHalf, colour);
            var start = a; start.Y = beam + BeamHeight * 0.5f;
            var tip = a + dir * length; tip.Y = start.Y;
            Beam(vertices, colors, uvs, uv2s, indices, start, tip, depth, BeamDepth * 0.5f, BeamHeight * 0.5f, colour);
            // a long arm's tie rod, from the post above it to two thirds of its way
            if (o.Kind is OverheadKind.Arm or OverheadKind.RefugeArm && length > TieFrom)
            {
                var from = a; from.Y = beamTop + 1.4f;
                Post(vertices, colors, uvs, uv2s, indices, a, from.Y + 0.1f, dir, depth, PostHalf * 0.7f, colour);
                var to = a + dir * (length * 0.66f); to.Y = beamTop;
                Tube(vertices, colors, uvs, uv2s, indices, colour, from, to, 0.035f);
            }
            hangFrom = _ => beam;
        }
        var (front, _) = Frame(o.Heading);
        foreach (var (h, record) in heads)
        {
            DrawHead(vertices, colors, uvs, uv2s, indices, plan, h, rounded, plan.ArrowPlates);
            var on = a + dir * record.Along;
            if (o.Kind == OverheadKind.Span)
            {
                // a hanger from the top of its plate up to the wire
                on.Y = h.Centre.Y + h.Half + (BorderGap + BorderWidth) * OverheadScale;
                Post(vertices, colors, uvs, uv2s, indices, on, hangFrom(record.Along), dir, depth, HangerHalf, Housing.SrgbToLinear(), sink: false);
                continue;
            }
            // a bracket from the beam's face into the back of its housing
            on.Y = h.Centre.Y;
            Beam(vertices, colors, uvs, uv2s, indices, on + front * (BeamDepth * 0.5f - 0.01f), on + front * (BeamDepth * 0.5f + MountGap + 0.01f),
                dir, 0.06f, 0.06f, Housing.SrgbToLinear());
        }
    }

    /// <summary>A square post round <paramref name="foot"/> (sunk below it unless <paramref name="sink"/> is false) up to <paramref name="top"/>, capped.</summary>
    private static void Post(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        Vector3 foot, float top, Vector3 along, Vector3 across, float half, Color colour, bool sink = true)
    {
        var c = new[] { along * half + across * half, -along * half + across * half, -along * half - across * half, along * half - across * half };
        float bottom = foot.Y - (sink ? Sink : 0f);
        for (int k = 0; k < 4; k++)
        {
            var p = c[k];
            var q = c[(k + 1) % 4];
            Polygon(vertices, colors, uvs, uv2s, indices, colour,
                [new Vector3(foot.X + p.X, bottom, foot.Z + p.Z), new Vector3(foot.X + q.X, bottom, foot.Z + q.Z),
                 new Vector3(foot.X + q.X, top, foot.Z + q.Z), new Vector3(foot.X + p.X, top, foot.Z + p.Z)]);
        }
        Polygon(vertices, colors, uvs, uv2s, indices, colour,
            [new Vector3(foot.X + c[3].X, top, foot.Z + c[3].Z), new Vector3(foot.X + c[2].X, top, foot.Z + c[2].Z),
             new Vector3(foot.X + c[1].X, top, foot.Z + c[1].Z), new Vector3(foot.X + c[0].X, top, foot.Z + c[0].Z)]);
    }

    /// <summary>A horizontal box from <paramref name="a"/> to <paramref name="b"/> (its axis), <paramref name="halfDepth"/> across and <paramref name="halfHeight"/> up and down.</summary>
    private static void Beam(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        Vector3 a, Vector3 b, Vector3 across, float halfDepth, float halfHeight, Color colour)
    {
        var c = new[] { across * halfDepth + Vector3.Up * halfHeight, -across * halfDepth + Vector3.Up * halfHeight,
            -across * halfDepth + Vector3.Down * halfHeight, across * halfDepth + Vector3.Down * halfHeight };
        for (int k = 0; k < 4; k++)
            Polygon(vertices, colors, uvs, uv2s, indices, colour, [a + c[k], b + c[k], b + c[(k + 1) % 4], a + c[(k + 1) % 4]]);
        Polygon(vertices, colors, uvs, uv2s, indices, colour, [b + c[0], b + c[3], b + c[2], b + c[1]]);
        Polygon(vertices, colors, uvs, uv2s, indices, colour, [a + c[0], a + c[1], a + c[2], a + c[3]]);
    }

    /// <summary>
    /// Signal 5.18 "right turn for cyclists permitted" (SSV Art. 69a, #711, the user's rule): a black square the size of a
    /// lens beside the red lamp, right against its visor in front of the white band, a yellow bike over a yellow arrow to the right. On the
    /// approach's bike head where it has one, else on the rightmost car head of its main pole (no head stands beside it).
    /// </summary>
    private static void RightOnRedSign(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        SignalPlan plan, SignalPole pole, List<Head> heads)
    {
        if ((pole.Flags & SignalPoleFlags.Main) == 0 || pole.Arm >= plan.Arms.Count || !plan.Arms[pole.Arm].BikeRightOnRed) return;
        var foot = new Vector3(pole.X, pole.Y, pole.Z);
        Head? on = null;
        foreach (var h in heads)
        {
            if (h.Shape == Shape.Bike) { on = h; break; }
            if (h.Shape is Shape.Square or Shape.Tram or Shape.TramT) continue;
            if (on is not { } best || (h.Centre - foot).Dot(h.Right) > (best.Centre - foot).Dot(best.Right)) on = h;
        }
        if (on is not { } head) return;
        // right against the red lamp's visor, in front of the white band (the user's review)
        float s = LensRadius * head.Scale, edge = HeadWidth * head.Scale * 0.5f + 0.005f * head.Scale;
        var red = head.Centre + Vector3.Up * (head.Half - Pitch * head.Scale * 0.5f);
        var centre = red + head.Right * (edge + s) + head.Front * 0.006f;
        RoundedPlate(vertices, colors, uvs, uv2s, indices, Housing.SrgbToLinear(), centre, head.Right, s, s, 0f);
        var yellow = SignYellow.SrgbToLinear();
        // the bike and arrow 1 cm proud of the plate: 3 mm in front of it, they fought it (#876, zfight note)
        void Draw(List<Vector2[]> polygons, Vector2 at, float size)
        {
            foreach (var polygon in polygons)
            {
                var ring = new Vector3[polygon.Length];
                for (int i = 0; i < polygon.Length; i++)
                    ring[i] = centre + head.Front * 0.01f + head.Right * ((at.X + polygon[i].X * size) * s) + Vector3.Up * ((at.Y + polygon[i].Y * size) * s);
                Polygon(vertices, colors, uvs, uv2s, indices, yellow, ring);
            }
        }
        Draw(SignalGlyphs.Bike(), new Vector2(0f, 0.3f), 0.85f);
        Draw(SignalGlyphs.Arrow(SignalMoves.Right), new Vector2(0f, -0.55f), 0.32f);
    }

    /// <summary>Collision triangles for the poles: a closed square column from below the ground to the top head.</summary>
    public static Vector3[] BuildCollisionFaces(RoadTile tile)
    {
        var faces = new List<Vector3>();
        foreach (var signal in tile.Signals)
            foreach (var pole in signal.Poles)
            {
                var foot = new Vector3(pole.X, pole.Y, pole.Z);
                const float H = 4f;   // the heads are above anything that hits the pole
                var c = new[] { new Vector3(PoleRadius, 0, PoleRadius), new Vector3(-PoleRadius, 0, PoleRadius),
                    new Vector3(-PoleRadius, 0, -PoleRadius), new Vector3(PoleRadius, 0, -PoleRadius) };
                for (int k = 0; k < 4; k++)
                {
                    var a0 = foot + c[k] + Vector3.Down * Sink;
                    var b0 = foot + c[(k + 1) % 4] + Vector3.Down * Sink;
                    var a1 = foot + c[k] + Vector3.Up * H;
                    var b1 = foot + c[(k + 1) % 4] + Vector3.Up * H;
                    faces.Add(a0); faces.Add(b0); faces.Add(b1);
                    faces.Add(a0); faces.Add(b1); faces.Add(a1);
                }
            }
        // an overhead structure's posts (#1037): the arm, beam and wire are above any vehicle (SSV Art. 71: 4.50 m and up), a span
        // wire's ends on facades
        foreach (var signal in tile.Signals)
            foreach (var o in signal.Overheads)
            {
                if (o.Kind == OverheadKind.Span) continue;
                Box(new Vector3(o.X, o.Y, o.Z), PostHalf);
                if (o.Kind == OverheadKind.Portal) Box(new Vector3(o.EndX, o.EndY, o.EndZ), PostHalf);
            }
        return faces.ToArray();

        void Box(Vector3 foot, float half)
        {
            const float H = 4f;
            var c = new[] { new Vector3(half, 0, half), new Vector3(-half, 0, half), new Vector3(-half, 0, -half), new Vector3(half, 0, -half) };
            for (int k = 0; k < 4; k++)
            {
                var a0 = foot + c[k] + Vector3.Down * Sink;
                var b0 = foot + c[(k + 1) % 4] + Vector3.Down * Sink;
                var a1 = foot + c[k] + Vector3.Up * H;
                var b1 = foot + c[(k + 1) % 4] + Vector3.Up * H;
                faces.Add(a0); faces.Add(b0); faces.Add(b1);
                faces.Add(a0); faces.Add(b1); faces.Add(a1);
            }
        }
    }

    /// <summary>The tile's lenses, or null when it has no signal heads.</summary>
    public static Lamps? BuildLamps(RoadTile tile)
    {
        if (tile.Signals.Count == 0) return null;
        var lamps = new Lamps();
        foreach (var signal in tile.Signals)
        {
            int j = lamps.Plans.Count;
            lamps.Plans.Add(signal.Plan);
            // the permissive lefts' flashing yellow (#1109), derived from the plan once per group
            var leftFlash = new Dictionary<int, SignalGroup?>();
            SignalPlan.Conflict[,]? conflicts = null;
            SignalGroup? LeftFlashOf(int g)
            {
                if (leftFlash.TryGetValue(g, out var done)) return done;
                conflicts ??= signal.Plan.Conflicts();
                return leftFlash[g] = signal.Plan.LeftFlash(g, conflicts);
            }
            var turnFlash = new Dictionary<(int, SignalMoves), SignalGroup?>();
            SignalGroup? TurnFlashOf(int g, SignalMoves turn)
            {
                if (turnFlash.TryGetValue((g, turn), out var done)) return done;
                conflicts ??= signal.Plan.Conflicts();
                return turnFlash[(g, turn)] = signal.Plan.TurnFlash(g, turn, conflicts);
            }
            foreach (var h in signal.Poles.SelectMany(pole => PoleHeads(signal, pole)).Concat(signal.Overheads.SelectMany(o => OverheadHeads(signal.Plan, o).Select(x => x.Head))))
                {
                    // a 300 mm overhead lens is the 200 mm lens scaled (a bike head's 100 mm has a mesh of its own)
                    float lens = h.Shape == Shape.Bike ? 1f : h.Scale;
                    var basis = new Basis(h.Right * lens, Vector3.Up * lens, h.Front);
                    var face = h.Front * 0.006f;
                    float pitch = Pitch * h.Scale, half = h.Half;
                    if (h.SideBySide)
                    {
                        // red standing on the left, green walking on the right, as the walker sees them
                        var figure = new Basis(h.Right * GenevaFigure, Vector3.Up * GenevaFigure, h.Front);
                        lamps.Lenses.Add(new Lens(h.Shape, new Transform3D(figure, h.Centre - h.Right * GenevaFigureX + face), j, h.Group, Role.Red));
                        lamps.Lenses.Add(new Lens(h.Shape, new Transform3D(figure, h.Centre + h.Right * GenevaFigureX + face), j, h.Group, Role.Green));
                        continue;
                    }
                    if (h.Shape == Shape.TramT)
                    {
                        // Romandy's head (#1105): the T's two solid strokes in its one lens, each its own lamp: the bar across (stop)
                        // and the bar upright under it (go), in lens radii (SignalLamps)
                        lamps.Lenses.Add(new Lens(h.Shape, new Transform3D(basis, h.Centre + face), j, h.Group, Role.Red));
                        lamps.Lenses.Add(new Lens(h.Shape, new Transform3D(basis, h.Centre + face), j, h.Group, Role.Green));
                        continue;
                    }
                    for (int k = 0; k < h.Lenses; k++)
                    {
                        // top down: red, (yellow), green
                        var role = h.Lenses == 2 ? (k == 0 ? Role.Red : Role.Green) : (Role)k;
                        var at = h.Centre + Vector3.Up * (half - pitch * (k + 0.5f)) + face;
                        // a bike head's own yellow flashes during its green where its riders turn right across a crossing green with them (#971, the user)
                        int flash = h.Shape == Shape.Bike && role == Role.Amber ? Flasher(signal.Plan, h.Group) : -1;
                        // a ball head's own yellow flashes during its green while its left gives way to oncoming traffic (#1109, the user)
                        // an uncombined arrow's yellow flashes in place of its green while its turn gives way (#1109, the user)
                        var shown = h.Shape == Shape.Circle && h.Lenses == 3 ? h.Arrow ?? ArrowMoves(signal.Plan, h.Group) : SignalMoves.Left | SignalMoves.Right;
                        var blink = shown == SignalMoves.None && role == Role.Amber ? LeftFlashOf(h.Group)
                            : shown is SignalMoves.Left or SignalMoves.Right && role is Role.Amber or Role.Green ? TurnFlashOf(h.Group, shown)
                            : null;
                        lamps.Lenses.Add(new Lens(h.Shape, new Transform3D(basis, at), j, h.Group, role, flash, h.Arrow, blink));
                    }
                    if (h.Flasher && Flasher(signal.Plan, h.Group) is int f and >= 0)
                        lamps.Lenses.Add(new Lens(Shape.Circle, new Transform3D(basis, FlasherAt(h, h.FlasherSide) + face), j, f, Role.Flash));
                    // an arrow head's permissive left: the lamp on its left (#1109), lit by the derived flasher, not its group
                    if (h.LeftFlasher && LeftFlashOf(h.Group) is { } lf)
                        lamps.Lenses.Add(new Lens(Shape.Circle, new Transform3D(basis, FlasherAt(h, h.LeftSide) + face), j, h.Group, Role.Flash, Blink: lf));
                }
        }
        return lamps.Lenses.Count == 0 ? null : lamps;
    }

    /// <summary>
    /// What a car head's round lenses show (#759): a ball where its group gives every move the
    /// approach has (<see cref="SignalMoves.None"/>); else, as in Switzerland, an arrow of the moves
    /// it does give: straight on, straight on and a turn, or the turn alone (a pocket with its own
    /// arrow head takes the rest).
    /// </summary>
    public static SignalMoves ArrowMoves(SignalPlan plan, int group)
    {
        var g = plan.Groups[group];
        if (g.Kind != SignalGroupKind.Car || g.Moves == SignalMoves.None) return SignalMoves.None;
        var all = SignalMoves.None;
        foreach (var o in plan.Groups)
            if (o.Arm == g.Arm && o.Kind is SignalGroupKind.Car or SignalGroupKind.LeftArrow or SignalGroupKind.RightArrow)
                all |= o.Moves;
        // left and right without straight on has no Swiss arrow: a ball
        return (g.Moves & all) == all || g.Moves == (SignalMoves.Left | SignalMoves.Right) ? SignalMoves.None : g.Moves;
    }

    /// <summary>The moves a head's arrow shows on a plate: a pocket head's turn, a car head's <see cref="ArrowMoves"/>, a bike head's group's.</summary>
    private static SignalMoves HeadMoves(SignalPlan plan, Head h) => h.Shape switch
    {
        Shape.LeftArrow => SignalMoves.Left,
        Shape.RightArrow => SignalMoves.Right,
        Shape.Bike => plan.Groups[h.Group].Moves is var m and not SignalMoves.None ? m : SignalMoves.Through,
        // a tram head shows its direction where its approach's trams have more than one (#1057)
        Shape.Tram or Shape.TramT => plan.Groups.Count(g => g.Kind == SignalGroupKind.Tram && g.Arm == plan.Groups[h.Group].Arm) > 1 ? plan.Groups[h.Group].Moves : SignalMoves.None,
        Shape.Circle => h.Arrow ?? ArrowMoves(plan, h.Group),
        _ => SignalMoves.None,
    };

    private static int Flasher(SignalPlan plan, int group)
    {
        for (int g = 0; g < plan.Groups.Count; g++)
            if (plan.Groups[g].Kind == SignalGroupKind.Flasher && plan.Groups[g].Qualifies == group) return g;
        return -1;
    }

    /// <summary>A pole's heads, moved out onto the face of an overhead structure's post where one stands on the pole (#1037).</summary>
    private static List<Head> PoleHeads(RoadSignal signal, SignalPole pole)
    {
        var heads = Heads(signal.Plan, pole);
        if (!signal.Overheads.Any(o => o.StandsOn(pole.X, pole.Z))) return heads;
        float shift = SignalOverhead.PostHalf - PoleRadius;
        return heads.Select(h => h with { Centre = h.Centre + h.Front * shift }).ToList();
    }

    /// <summary>The heads a pole carries, from the plan's groups of its arm.</summary>
    private static List<Head> Heads(SignalPlan plan, SignalPole pole)
    {
        var heads = new List<Head>();
        var foot = new Vector3(pole.X, pole.Y, pole.Z);
        bool ped = (pole.Flags & SignalPoleFlags.Pedestrian) != 0 && Find(plan, SignalGroupKind.Pedestrian, pole.Arm, SignalMoves.None) >= 0;
        bool main = (pole.Flags & SignalPoleFlags.Main) != 0, second = (pole.Flags & SignalPoleFlags.Second) != 0;
        if (main || second)
        {
            var (front, right) = Frame(pole.CarHeading);
            float lower = ped ? CarLowerEdgeAbovePedestrian : CarLowerEdge;
            // left to right as the driver sees them; the main head on the pole
            var row = new List<(int Group, Shape Shape)>();
            int left = Find(plan, SignalGroupKind.LeftArrow, pole.Arm, SignalMoves.Left);
            int car = MainCar(plan, pole.Arm);
            if (left >= 0) row.Add((left, Shape.LeftArrow));
            if (car >= 0) row.Add((car, Shape.Circle));
            if (main)
            {
                int rightArrow = Find(plan, SignalGroupKind.RightArrow, pole.Arm, SignalMoves.Right);
                int rightLane = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Car && g.Arm == pole.Arm && g.Moves == SignalMoves.Right);
                if (rightArrow >= 0) row.Add((rightArrow, Shape.RightArrow));
                else if (rightLane >= 0 && rightLane != car) row.Add((rightLane, Shape.Circle));
            }
            // the bike head (#351), low, in front of the pole, facing the approach: on the main pole the riders going straight on and
            // right; the riders turning left with a group of their own (#979, the user) on the left pole, its arrow on its plate
            int bike = plan.Groups.FindIndex(g => g.Kind == SignalGroupKind.Bike && g.Arm == pole.Arm && (main ? g.Moves != SignalMoves.Left : g.Moves == SignalMoves.Left));
            if (bike >= 0)
                heads.Add(new Head(foot + front * (PoleRadius + BoardThickness + HeadDepth * BikeScale + 0.01f)
                    + Vector3.Up * (BikeLowerEdge + 1.5f * Pitch * BikeScale), front, right, bike, Shape.Bike, 3, false, BikeScale));
            int centre = Math.Max(0, row.FindIndex(r => r.Group == car));
            SignalPlan.Conflict[,]? conflicts = null;
            int leftLamps = 0;   // flasher lamps left of the leftmost head
            for (int i = 0; i < row.Count; i++)
            {
                var (g, shape) = row[i];
                var at = foot + front * InFront + right * ((i - centre) * HeadSpacing)
                    + Vector3.Up * (lower + 1.5f * Pitch);
                // an uncombined arrow (the turn alone) flashes its own yellow in place of its green (#1109, the user): no lamp beside it
                bool single = shape == Shape.Circle && ArrowMoves(plan, g) is SignalMoves.Left or SignalMoves.Right;
                int f = main && !single ? Flasher(plan, g) : -1;
                // its plate (#1109): what the flasher warns of
                var (plate, plateArrow) = f >= 0 ? plan.PlateOf(f, conflicts ??= plan.Conflicts()) : (SignalPlate.None, SignalMoves.None);
                // a permissive left on an arrow head (#1109, the user): no flashing yellow in the arrow, a flasher on its left (the
                // leftmost head: a left pocket's arrow would have taken the left)
                bool leftOf = main && i == 0 && shape == Shape.Circle && !single && (ArrowMoves(plan, g) & SignalMoves.Left) != 0
                    && plan.LeftGivesWayTo(g, conflicts ??= plan.Conflicts()).Count > 0;
                // beside its green on its right, or on its left where the next head stands there (a right lane's or arrow's head: they
                // overlapped, #1109); a permissive left's flasher beyond it
                float side = i < row.Count - 1 ? -1f : 1f;
                float leftSide = f >= 0 && side < 0 ? -2f : -1f;
                if (i == 0) leftLamps = (leftOf ? 1 : 0) + (f >= 0 && side < 0 ? 1 : 0);
                heads.Add(new Head(at, front, right, g, shape, 3, f >= 0, Plate: plate, PlateArrow: plateArrow, LeftFlasher: leftOf,
                    FlasherSide: side, LeftSide: leftSide));
            }
            // the trams' heads (#1057) beside the car heads on the road's side: left of the main pole's, right of the second's
            // (Romandy's packed against the car heads, beside or above them: #1105)
            if (plan.TramTHead) RomandyTramHeads(plan, pole, heads, foot, front, right, lower, row.Count, centre);
            // (left of a left flasher, #1109)
            else TramHeads(plan, pole, heads, foot, front, right, lower, !main ? row.Count - centre : -(centre + 1 + leftLamps), !main ? 1 : -1);
        }
        else if ((pole.Flags & SignalPoleFlags.Tram) != 0)
        {
            var (front, right) = Frame(pole.CarHeading);
            TramHeads(plan, pole, heads, foot, front, right, ped ? CarLowerEdgeAbovePedestrian : CarLowerEdge, 0, -1);
        }
        if (ped)
        {
            int lenses = plan.PedestrianAmber ? 3 : 2;
            bool geneva = !plan.PedestrianAmber;
            // a two-stage crossing's heads show their half (#1030): the kerbs' their own, the island's two back to back, the
            // arriving half's facing the pole's way
            bool arriving = (pole.Flags & SignalPoleFlags.ArrivingHalf) != 0, departing = (pole.Flags & SignalPoleFlags.DepartingHalf) != 0;
            var faces = new List<(int Group, float Heading)>(2);
            if (arriving) faces.Add((Walkers(plan, pole.Arm, CrossingHalf.Arriving), pole.PedHeading));
            if (departing) faces.Add((Walkers(plan, pole.Arm, CrossingHalf.Departing), pole.PedHeading + (arriving ? Mathf.Pi : 0f)));
            if (faces.Count == 0) faces.Add((Find(plan, SignalGroupKind.Pedestrian, pole.Arm, SignalMoves.None), pole.PedHeading));
            foreach (var (g, heading) in faces)
            {
                var (front, right) = Frame(heading);
                var at = foot + front * InFront + Vector3.Up * (PedestrianLowerEdge + (geneva ? GenevaHalfHeight : lenses * Pitch * 0.5f));
                heads.Add(new Head(at, front, right, g, Shape.Square, lenses, false, SideBySide: geneva));
            }
        }
        return heads;
    }

    /// <summary>
    /// A pole's tram bar heads (#1057, flagged <see cref="SignalPoleFlags.Tram"/>): one per tram group of its arm, a black housing of
    /// three lamps (the bar across, the dot, the bar upright or slanted), no plate, at the car heads' height beside them on the
    /// road's side (<paramref name="slot"/> head spacings from the pole along <paramref name="right"/>, then on by
    /// <paramref name="step"/>), facing the arriving trams.
    /// </summary>
    private static void TramHeads(SignalPlan plan, SignalPole pole, List<Head> heads, Vector3 foot, Vector3 front, Vector3 right, float lower, int slot, int step)
    {
        if ((pole.Flags & SignalPoleFlags.Tram) == 0) return;
        // (one head per direction, left to right as the tram driver sees them: laid outward from the pole either way)
        int count = plan.Groups.Count;
        bool t = plan.TramTHead;
        // (Romandy's one-lens heads on a pole of their own: side by side as car heads, level with a car head's lower half, #1105)
        float x = slot * HeadSpacing;
        for (int k = 0; k < count; k++)
        {
            int g = step > 0 ? k : count - 1 - k;
            if (plan.Groups[g].Kind != SignalGroupKind.Tram || plan.Groups[g].Arm != pole.Arm) continue;
            var at = foot + front * InFront + right * x + Vector3.Up * (lower + (t ? 1f : 1.5f) * Pitch);
            heads.Add(t ? new Head(at, front, right, g, Shape.TramT, 1, false) : new Head(at, front, right, g, Shape.Tram, 3, false));
            x += step * (t ? TramPack : HeadSpacing);
        }
    }

    /// <summary>
    /// Romandy's tram heads on a pole with car heads (#1105, the user's photos), one-lens car head modules packed against the
    /// <paramref name="cars"/> car heads (the main one <paramref name="centre"/>-th from the left): beside them on their right, plate
    /// against plate, where there is one (level with their lower half, photo 1; over the main head's flasher where it has one) or
    /// more than there are car heads, up to <see cref="TramColumn"/> without a flasher (a column down their side, the user);
    /// otherwise in a row right on top of them ending over the main head, each its arrow plate between it and the car head under
    /// it (photo 2).
    /// </summary>
    private static void RomandyTramHeads(SignalPlan plan, SignalPole pole, List<Head> heads, Vector3 foot, Vector3 front, Vector3 right,
        float lower, int cars, int centre)
    {
        if ((pole.Flags & SignalPoleFlags.Tram) == 0) return;
        var trams = new List<int>(3);
        // left to right as the tram driver sees them (the plan adds an arm's tram groups left, through, right)
        for (int g = 0; g < plan.Groups.Count; g++)
            if (plan.Groups[g].Kind == SignalGroupKind.Tram && plan.Groups[g].Arm == pole.Arm) trams.Add(g);
        if (trams.Count == 0) return;
        float frame = BorderGap + BorderWidth, panel = trams.Count > 1 ? ArrowPanel : 0f;
        // (a flasher on the right of the rightmost car head: one on a head's left stands where nothing goes, #1109)
        bool flasher = heads.Any(h => h.Flasher && h.FlasherSide > 0);
        if (flasher && trams.Count == 1)
        {
            // one beside the main head over its flasher, plate on plate, still touching the car head (the user: "if there is a
            // flasher, there is still space for one transit head"; beside the rightmost head where a right lane's stands right of it)
            heads.Add(new Head(foot + front * InFront + right * ((cars - 1 - centre) * HeadSpacing + TramPack) + Vector3.Up * (lower + 1.5f * Pitch + 2f * frame + 0.01f),
                front, right, trams[0], Shape.TramT, 1, false));
            return;
        }
        if (!flasher && (trams.Count == 1 || trams.Count > cars) && trams.Count <= TramColumn)
        {
            // beside: one level with the car heads' lower half (photo 1), several in a column against their side, its top level
            // with theirs, one module (head, its plate's bands and arrow panel) under the next (the user)
            float side = (cars - 1 - centre) * HeadSpacing + TramPack, from = trams.Count == 1 ? lower + Pitch : lower + 2.5f * Pitch;
            for (int k = 0; k < trams.Count; k++)
                heads.Add(new Head(foot + front * InFront + right * side + Vector3.Up * (from - k * (Pitch + 2f * frame + panel)),
                    front, right, trams[k], Shape.TramT, 1, false));
            return;
        }
        // on top: the car heads' plates' top, then the tram plate's foot on it: its band, its arrow panel (several ways), its band
        float y = lower + 3f * Pitch + frame + 0.01f + panel + frame + Pitch * 0.5f;
        // (the last over the main head, the others over the heads on its left while there are some)
        int first = Math.Min(trams.Count - 1, centre);
        for (int k = 0; k < trams.Count; k++)
        {
            var at = foot + front * InFront + right * ((k - first) * HeadSpacing) + Vector3.Up * y;   // over its car head
            heads.Add(new Head(at, front, right, trams[k], Shape.TramT, 1, false));
        }
    }

    /// <summary>A crossing's pedestrian group for <paramref name="half"/> (#1030), else its only one (a crossing in one go).</summary>
    private static int Walkers(SignalPlan plan, int arm, CrossingHalf half)
    {
        int any = -1;
        for (int g = 0; g < plan.Groups.Count; g++)
        {
            if (plan.Groups[g].Kind != SignalGroupKind.Pedestrian || plan.Groups[g].Arm != arm) continue;
            if (plan.Groups[g].Half == half) return g;
            if (any < 0) any = g;
        }
        return any;
    }

    /// <summary>The approach's main-lane group: the one going straight, else its only car group.</summary>
    private static int MainCar(SignalPlan plan, int arm)
    {
        int any = -1;
        for (int g = 0; g < plan.Groups.Count; g++)
        {
            var group = plan.Groups[g];
            if (group.Kind != SignalGroupKind.Car || group.Arm != arm) continue;
            if ((group.Moves & SignalMoves.Through) != 0) return g;
            if (any < 0 || group.Moves != SignalMoves.Right) any = g;
        }
        return any;
    }

    private static int Find(SignalPlan plan, SignalGroupKind kind, int arm, SignalMoves moves)
    {
        for (int g = 0; g < plan.Groups.Count; g++)
            if (plan.Groups[g].Kind == kind && plan.Groups[g].Arm == arm && (moves == SignalMoves.None || (plan.Groups[g].Moves & moves) != 0)) return g;
        return -1;
    }

    /// <summary>The way a heading faces (horizontal) and the right of a viewer looking at it (as <c>RoadSignBuilder</c>).</summary>
    private static (Vector3 Front, Vector3 Right) Frame(float heading)
    {
        var front = new Vector3(-Mathf.Sin(heading), 0f, -Mathf.Cos(heading));
        return (front, new Vector3(front.Z, 0f, -front.X));
    }

    // ---- geometry -------------------------------------------------------------------------

    private static void Column(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        Vector3 foot, float top, Color colour)
    {
        // a round pole with a cap a little wider than it (collision stays the square column)
        var ring = new Vector3[PoleSides];
        for (int k = 0; k < PoleSides; k++)
        {
            float a = Mathf.Tau * k / PoleSides;
            ring[k] = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
        }
        const float CapRise = 0.03f, CapOver = 1.25f;
        var cap = new Vector3[PoleSides];
        for (int k = 0; k < PoleSides; k++)
        {
            var a = ring[k] * PoleRadius;
            var b = ring[(k + 1) % PoleSides] * PoleRadius;
            Polygon(vertices, colors, uvs, uv2s, indices, colour,
                [foot + a + Vector3.Down * Sink, foot + b + Vector3.Down * Sink, foot + b + Vector3.Up * top, foot + a + Vector3.Up * top]);
            Polygon(vertices, colors, uvs, uv2s, indices, colour,
                [foot + a + Vector3.Up * top, foot + b + Vector3.Up * top,
                 foot + b * CapOver + Vector3.Up * (top + CapRise), foot + a * CapOver + Vector3.Up * (top + CapRise)]);
            cap[PoleSides - 1 - k] = foot + a * CapOver + Vector3.Up * (top + CapRise);
        }
        Polygon(vertices, colors, uvs, uv2s, indices, colour, cap);
    }

    /// <summary>
    /// A housing round <paramref name="centre"/> with its edges along <paramref name="front"/>
    /// rounded: a rounded rectangle in the right-up plane, extruded front to back.
    /// </summary>
    private static void RoundedHousing(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        Vector3 centre, Vector3 right, Vector3 front, float halfWidth, float halfDepth, float halfHeight, float radius, Color? tint = null)
    {
        var colour = (tint ?? Housing).SrgbToLinear();
        var outline = Outline(halfWidth, halfHeight, radius, 2);
        Vector3 P(Vector2 p, float z) => centre + right * p.X + Vector3.Up * p.Y + front * z;
        int n = outline.Count;
        var face = new Vector3[n];
        var back = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            face[i] = P(outline[i], halfDepth);
            back[n - 1 - i] = P(outline[i], -halfDepth);
            var a = outline[i];
            var b = outline[(i + 1) % n];
            Polygon(vertices, colors, uvs, uv2s, indices, colour, [P(a, halfDepth), P(a, -halfDepth), P(b, -halfDepth), P(b, halfDepth)]);
        }
        Polygon(vertices, colors, uvs, uv2s, indices, colour, face);
        Polygon(vertices, colors, uvs, uv2s, indices, colour, back);
    }

    /// <summary>
    /// The ring round a lens (a square frame round a pedestrian lens) just proud of the housing
    /// at <paramref name="centre"/>, and the visor over it: an arc over the top and down the
    /// sides, open below, longest at the crown and flaring a little toward its mouth.
    /// </summary>
    /// <summary>A disc facing the way <paramref name="right"/> and up make, clockwise from the front.</summary>
    private static Vector3[] Circle(Vector3 centre, Vector3 right, float r, int n)
    {
        var ring = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float a = -Mathf.Tau * i / n;
            ring[i] = centre + right * (r * Mathf.Cos(a)) + Vector3.Up * (r * Mathf.Sin(a));
        }
        return ring;
    }

    private static void LensFittings(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        Vector3 centre, Vector3 right, Vector3 front, float scale, bool square)
    {
        float r = LensRadius * scale, rim = RimWidth * scale;
        var rimColour = Rim.SrgbToLinear();
        // a centimetre proud of the housing: 3 mm z-fought from across a junction (#1079)
        var ringAt = centre + front * 0.01f;
        Vector3 Q(float x, float y, Vector3 at) => at + right * x + Vector3.Up * y;
        if (square)
        {
            float a = r, b = r + rim;
            Polygon(vertices, colors, uvs, uv2s, indices, rimColour, [Q(-b, a, ringAt), Q(b, a, ringAt), Q(b, b, ringAt), Q(-b, b, ringAt)]);
            Polygon(vertices, colors, uvs, uv2s, indices, rimColour, [Q(-b, -b, ringAt), Q(b, -b, ringAt), Q(b, -a, ringAt), Q(-b, -a, ringAt)]);
            Polygon(vertices, colors, uvs, uv2s, indices, rimColour, [Q(-b, -a, ringAt), Q(-a, -a, ringAt), Q(-a, a, ringAt), Q(-b, a, ringAt)]);
            Polygon(vertices, colors, uvs, uv2s, indices, rimColour, [Q(a, -a, ringAt), Q(b, -a, ringAt), Q(b, a, ringAt), Q(a, a, ringAt)]);
        }
        else
            for (int k = 0; k < RimSegments; k++)
            {
                float a0 = Mathf.Tau * k / RimSegments, a1 = Mathf.Tau * (k + 1) / RimSegments;
                Polygon(vertices, colors, uvs, uv2s, indices, rimColour,
                    [Q(r * Mathf.Cos(a0), r * Mathf.Sin(a0), ringAt), Q((r + rim) * Mathf.Cos(a0), (r + rim) * Mathf.Sin(a0), ringAt),
                     Q((r + rim) * Mathf.Cos(a1), (r + rim) * Mathf.Sin(a1), ringAt), Q(r * Mathf.Cos(a1), r * Mathf.Sin(a1), ringAt)]);
            }
        // the visor: from 20 degrees below the horizontal on one side, over the top, to the other
        var colour = Housing.SrgbToLinear();
        float inner = r + rim + VisorGap * scale, length = VisorLength * scale;
        const float From = -0.35f, To = Mathf.Pi + 0.35f;
        for (int k = 0; k < VisorSegments; k++)
        {
            float a0 = Mathf.Lerp(From, To, k / (float)VisorSegments), a1 = Mathf.Lerp(From, To, (k + 1) / (float)VisorSegments);
            float l0 = length * (0.55f + 0.45f * Mathf.Max(0f, Mathf.Sin(a0))), l1 = length * (0.55f + 0.45f * Mathf.Max(0f, Mathf.Sin(a1)));
            float o0 = inner * (1f + 0.25f * l0 / length), o1 = inner * (1f + 0.25f * l1 / length);
            Polygon(vertices, colors, uvs, uv2s, indices, colour,
                [Q(inner * Mathf.Cos(a0), inner * Mathf.Sin(a0), centre), Q(inner * Mathf.Cos(a1), inner * Mathf.Sin(a1), centre),
                 Q(o1 * Mathf.Cos(a1), o1 * Mathf.Sin(a1), centre + front * l1), Q(o0 * Mathf.Cos(a0), o0 * Mathf.Sin(a0), centre + front * l0)]);
        }
    }

    /// <summary>A six-sided tube from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private static void Tube(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        Color colour, Vector3 a, Vector3 b, float radius)
    {
        const int Sides = 6;
        var axis = (b - a).Normalized();
        var u = axis.Cross(Vector3.Up).Normalized();
        var v = axis.Cross(u);
        for (int k = 0; k < Sides; k++)
        {
            float a0 = Mathf.Tau * k / Sides, a1 = Mathf.Tau * (k + 1) / Sides;
            var d0 = (u * Mathf.Cos(a0) + v * Mathf.Sin(a0)) * radius;
            var d1 = (u * Mathf.Cos(a1) + v * Mathf.Sin(a1)) * radius;
            Polygon(vertices, colors, uvs, uv2s, indices, colour, [a + d0, a + d1, b + d1, b + d0]);
        }
    }

    /// <summary>
    /// A flat band <paramref name="band"/> wide round an opening of half sizes
    /// <paramref name="innerW"/> x <paramref name="innerH"/> in the right-up plane through
    /// <paramref name="centre"/>, facing the viewer in front; its corners rounded by
    /// <paramref name="corner"/> inside and that plus the band outside, or square when it is 0.
    /// </summary>
    /// <param name="below">How much further the band runs on below the opening (an arrow panel), 0 for none.</param>
    private static void BorderFrame(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        Color colour, Vector3 centre, Vector3 right, float innerW, float innerH, float band, float corner, float below = 0f)
    {
        int seg = corner > 0f ? 3 : 0;
        var inner = Outline(innerW, innerH, corner, seg);
        var outer = Outline(innerW + band, innerH + band + below * 0.5f, corner + band, seg);
        for (int i = 0; i < outer.Count; i++) outer[i] -= new Vector2(0f, below * 0.5f);   // down: Vector2.Down is +y
        Vector3 Q(Vector2 p) => centre + right * p.X + Vector3.Up * p.Y;
        // the outlines run counter-clockwise as the viewer in front sees them; each quad is wound
        // clockwise, Godot's front face (PlateStyle draws the back black)
        for (int i = 0; i < inner.Count; i++)
        {
            int j = (i + 1) % inner.Count;
            Polygon(vertices, colors, uvs, uv2s, indices, colour, [Q(inner[i]), Q(inner[j]), Q(outer[j]), Q(outer[i])], PlateStyle);
        }
    }

    /// <summary>A flat rounded rectangle facing the viewer in front, its back black (<see cref="PlateStyle"/>).</summary>
    private static void RoundedPlate(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        Color colour, Vector3 centre, Vector3 right, float halfWidth, float halfHeight, float corner)
    {
        var outline = Outline(halfWidth, halfHeight, corner, corner > 0f ? 3 : 0);
        var ring = new Vector3[outline.Count];
        // clockwise from the front: Godot's front face
        for (int i = 0; i < outline.Count; i++) ring[outline.Count - 1 - i] = centre + right * outline[i].X + Vector3.Up * outline[i].Y;
        Polygon(vertices, colors, uvs, uv2s, indices, colour, ring, PlateStyle);
    }

    /// <summary>The black arrow of <paramref name="moves"/> (<see cref="SignalGlyphs"/>), <paramref name="size"/> metres to a lens radius, round <paramref name="centre"/>.</summary>
    private static void ArrowOn(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        SignalMoves moves, Vector3 centre, Vector3 right, float size) =>
        GlyphOn(vertices, colors, uvs, uv2s, indices, SignalGlyphs.Arrow(moves), centre, right, size);

    /// <summary>A black glyph (<see cref="SignalGlyphs"/>), <paramref name="size"/> metres to a lens radius, round <paramref name="centre"/>.</summary>
    private static void GlyphOn(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        List<Vector2[]> glyph, Vector3 centre, Vector3 right, float size)
    {
        var colour = Housing.SrgbToLinear();
        foreach (var polygon in glyph)
        {
            var ring = new Vector3[polygon.Length];
            for (int i = 0; i < polygon.Length; i++) ring[i] = centre + right * (polygon[i].X * size) + Vector3.Up * (polygon[i].Y * size);
            Polygon(vertices, colors, uvs, uv2s, indices, colour, ring);
        }
    }

    /// <summary>
    /// A rectangle of half sizes <paramref name="halfWidth"/> x <paramref name="halfHeight"/> with
    /// corners rounded by <paramref name="radius"/> in <paramref name="seg"/> steps, counter-clockwise
    /// from the top of its right side; one point per corner when <paramref name="seg"/> is 0.
    /// </summary>
    private static List<Vector2> Outline(float halfWidth, float halfHeight, float radius, int seg)
    {
        radius = Mathf.Min(radius, Mathf.Min(halfWidth, halfHeight) * 0.9f);
        var outline = new List<Vector2>();
        (float X, float Y)[] corners = [(halfWidth - radius, halfHeight - radius), (radius - halfWidth, halfHeight - radius),
            (radius - halfWidth, radius - halfHeight), (halfWidth - radius, radius - halfHeight)];
        for (int c = 0; c < 4; c++)
            for (int k = 0; k <= seg; k++)
            {
                float a = Mathf.Pi * 0.5f * (c + (seg == 0 ? 0.5f : k / (float)seg));
                outline.Add(seg == 0
                    ? new Vector2(Mathf.Sign(Mathf.Cos(a)) * halfWidth, Mathf.Sign(Mathf.Sin(a)) * halfHeight)
                    : new Vector2(corners[c].X + radius * Mathf.Cos(a), corners[c].Y + radius * Mathf.Sin(a)));
            }
        return outline;
    }

    /// <summary>A flat rectangle facing <paramref name="front"/>, both sides drawn (the road material is two-sided).</summary>
    private static void Plate(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        Color colour, Vector3 centre, Vector3 front, Vector3 right, float halfWidth, float halfHeight)
    {
        var r = right * halfWidth;
        var u = Vector3.Up * halfHeight;
        Polygon(vertices, colors, uvs, uv2s, indices, colour, [centre - r - u, centre + r - u, centre + r + u, centre - r + u]);
    }

    private static void Polygon(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s,
        List<int> indices, Color color, Vector3[] ring, float style = PropStyle)
    {
        int start = vertices.Count;
        foreach (var v in ring)
        {
            vertices.Add(v);
            colors.Add(color);
            uvs.Add(Vector2.Zero);
            uv2s.Add(new Vector2(style, 0f));
        }
        for (int i = 1; i + 1 < ring.Length; i++) { indices.Add(start); indices.Add(start + i); indices.Add(start + i + 1); }
    }
}
