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
    private static readonly Color Rim = new(0.20f, 0.20f, 0.22f);           // the lens ring, a shade off the housing

    /// <summary>Lens pitch and the 200 mm lens drawn a little smaller than its housing's cell.</summary>
    public const float Pitch = 0.3f, LensRadius = 0.09f;
    private const float HeadWidth = 0.3f, HeadDepth = 0.22f, BoardMargin = 0.1f, BoardThickness = 0.02f;
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

    public enum Shape : byte { Circle = 0, LeftArrow = 1, RightArrow = 2, Square = 3, Bike = 4 }

    /// <summary>Shapes there are (one lens mesh and one MultiMesh each).</summary>
    public const int ShapeCount = 5;

    /// <summary>
    /// A bike head (#351): 100 mm lenses (Stadt Zürich Velostandards LSA), half a car head, low on
    /// the main pole below the priority sign, facing the approach.
    /// </summary>
    private const float BikeScale = 0.5f, BikeLowerEdge = 1.05f;

    public enum Role : byte { Red, Amber, Green, Flash }

    /// <summary>One lens: its instanced shape and transform (tile-local, +Z toward the viewer), which junction and group light it, and as what.</summary>
    public readonly record struct Lens(Shape Shape, Transform3D Transform, int Junction, int Group, Role Role);

    /// <summary>A tile's lenses and the plans that light them.</summary>
    public sealed class Lamps
    {
        public readonly List<SignalPlan> Plans = new();
        public readonly List<Lens> Lenses = new();
    }

    /// <summary>A head on a pole: its centre, the way it faces and its right as the viewer sees it, its group, its lenses.</summary>
    private readonly record struct Head(Vector3 Centre, Vector3 Front, Vector3 Right, int Group, Shape Shape, int Lenses, bool Flasher,
        float Scale = 1f, bool SideBySide = false)
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

    /// <summary>Appends every pole, housing and backboard of the tile to a road mesh under construction.</summary>
    /// <param name="rounded">The plates' corners are rounded (Cartoon's mesh detail, #759).</param>
    public static void Append(RoadTile tile, List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices, bool rounded = false)
    {
        foreach (var signal in tile.Signals)
            foreach (var pole in signal.Poles)
            {
                var heads = Heads(signal.Plan, pole);
                var foot = new Vector3(pole.X, pole.Y, pole.Z);
                float top = 0f;
                foreach (var h in heads) top = Mathf.Max(top, h.Centre.Y - pole.Y + h.Half + BoardMargin);
                Column(vertices, colors, uvs, uv2s, indices, foot, top);
                foreach (var h in heads)
                {
                    float half = h.Half, w = HeadWidth * h.Scale, depth = HeadDepth * h.Scale;
                    // a bracket from the pole into the back of the head's housing (not through to its face)
                    var reach = h.Centre - h.Front * ((h.SideBySide ? GenevaDepth : depth) * 0.8f) - foot;
                    reach.Y = 0;
                    if (reach.Length() > PoleRadius * 2)
                        Tube(vertices, colors, uvs, uv2s, indices, Housing.SrgbToLinear(),
                            new Vector3(foot.X, h.Centre.Y, foot.Z), new Vector3(foot.X, h.Centre.Y, foot.Z) + reach, 0.025f);
                    // the backboard (#759): a car head's plate flush with the housing's face, clear of it by
                    // a hair, white in front and black behind (one face, PlateStyle); a bike head's only in
                    // some cantons; a pedestrian head has none
                    bool plates = signal.Plan.ArrowPlates;
                    bool board = h.Shape != Shape.Square && (h.Shape != Shape.Bike || plates);
                    float gap = BorderGap * h.Scale, band = BorderWidth * h.Scale, corner = rounded ? CornerRadius * h.Scale : 0f;
                    // the arrow under the head: on the band run on below it where the canton does that,
                    // on a small plate of its own under a bike head elsewhere
                    var arrow = h.Shape == Shape.Bike || plates ? HeadMoves(signal.Plan, h) : SignalMoves.None;
                    float panel = arrow != SignalMoves.None ? ArrowPanel * h.Scale : 0f;
                    var face = h.Centre - h.Front * 0.002f;
                    if (board)
                    {
                        BorderFrame(vertices, colors, uvs, uv2s, indices, WhitePlate.SrgbToLinear(), face,
                            h.Right, w * 0.5f + gap, half + gap, band, corner, below: panel);
                        if (panel > 0f)
                            ArrowOn(vertices, colors, uvs, uv2s, indices, arrow, face + h.Front * 0.003f + Vector3.Down * (half + gap + (band + panel) * 0.5f),
                                h.Right, (band + panel) * 0.5f * ArrowFill);
                    }
                    else if (panel > 0f)
                    {
                        // a bike head's own arrow plate, white with its back black, just below the housing
                        float below = half + gap, height = panel + band;
                        var at = face + Vector3.Down * (below + height * 0.5f);
                        RoundedPlate(vertices, colors, uvs, uv2s, indices, WhitePlate.SrgbToLinear(), at, h.Right, w * 0.5f + gap, height * 0.5f, corner);
                        ArrowOn(vertices, colors, uvs, uv2s, indices, arrow, at + h.Front * 0.003f, h.Right, height * 0.5f * ArrowFill);
                    }
                    if (h.SideBySide)
                    {
                        RoundedHousing(vertices, colors, uvs, uv2s, indices, h.Centre - h.Front * (GenevaDepth * 0.5f), h.Right, h.Front,
                            GenevaHalfWidth, GenevaDepth * 0.5f, GenevaHalfHeight, CornerRadius, GenevaGrey);
                        var window = Outline(GenevaHalfWidth - 0.025f, GenevaHalfHeight - 0.025f, CornerRadius * 0.6f, 2);
                        var ring = new Vector3[window.Count];
                        for (int i = 0; i < window.Count; i++)
                            ring[window.Count - 1 - i] = h.Centre + h.Front * 0.003f + h.Right * window[i].X + Vector3.Up * window[i].Y;
                        Polygon(vertices, colors, uvs, uv2s, indices, Window.SrgbToLinear(), ring);
                        continue;
                    }
                    RoundedHousing(vertices, colors, uvs, uv2s, indices, h.Centre - h.Front * (depth * 0.5f), h.Right, h.Front,
                        w * 0.5f, depth * 0.5f, half, CornerRadius * h.Scale);
                    float pitch = Pitch * h.Scale;
                    for (int k = 0; k < h.Lenses; k++)
                        LensFittings(vertices, colors, uvs, uv2s, indices, h.Centre + Vector3.Up * (half - pitch * (k + 0.5f)),
                            h.Right, h.Front, h.Scale, square: h.Shape == Shape.Square);
                    if (h.Flasher)
                    {
                        // the flasher's own small housing beside the green, spaced as a head beside it (#759)
                        var at = h.Centre + h.Right * HeadSpacing + Vector3.Down * (half - Pitch * 0.5f);
                        RoundedHousing(vertices, colors, uvs, uv2s, indices, at - h.Front * (HeadDepth * 0.5f), h.Right, h.Front,
                            HeadWidth * 0.5f, HeadDepth * 0.5f, Pitch * 0.5f, CornerRadius);
                        LensFittings(vertices, colors, uvs, uv2s, indices, at, h.Right, h.Front, 1f, square: false);
                        // its own white band, clear of the head's like the next head's
                        if (board)
                            BorderFrame(vertices, colors, uvs, uv2s, indices, WhitePlate.SrgbToLinear(), at - h.Front * 0.002f,
                                h.Right, HeadWidth * 0.5f + gap, Pitch * 0.5f + gap, band, corner);
                    }
                }
            }
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
        return faces.ToArray();
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
            foreach (var pole in signal.Poles)
                foreach (var h in Heads(signal.Plan, pole))
                {
                    var basis = new Basis(h.Right, Vector3.Up, h.Front);
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
                    for (int k = 0; k < h.Lenses; k++)
                    {
                        // top down: red, (yellow), green
                        var role = h.Lenses == 2 ? (k == 0 ? Role.Red : Role.Green) : (Role)k;
                        var at = h.Centre + Vector3.Up * (half - pitch * (k + 0.5f)) + face;
                        lamps.Lenses.Add(new Lens(h.Shape, new Transform3D(basis, at), j, h.Group, role));
                    }
                    if (h.Flasher && Flasher(signal.Plan, h.Group) is int f and >= 0)
                    {
                        var at = h.Centre + h.Right * HeadSpacing + Vector3.Down * (half - Pitch * 0.5f) + face;
                        lamps.Lenses.Add(new Lens(Shape.Circle, new Transform3D(basis, at), j, f, Role.Flash));
                    }
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
        Shape.Circle => ArrowMoves(plan, h.Group),
        _ => SignalMoves.None,
    };

    private static int Flasher(SignalPlan plan, int group)
    {
        for (int g = 0; g < plan.Groups.Count; g++)
            if (plan.Groups[g].Kind == SignalGroupKind.Flasher && plan.Groups[g].Qualifies == group) return g;
        return -1;
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
            // the bike head (#351), low, in front of the pole, facing the approach
            int bike = main ? Find(plan, SignalGroupKind.Bike, pole.Arm, SignalMoves.None) : -1;
            if (bike >= 0)
                heads.Add(new Head(foot + front * (PoleRadius + BoardThickness + HeadDepth * BikeScale + 0.01f)
                    + Vector3.Up * (BikeLowerEdge + 1.5f * Pitch * BikeScale), front, right, bike, Shape.Bike, 3, false, BikeScale));
            int centre = Math.Max(0, row.FindIndex(r => r.Group == car));
            for (int i = 0; i < row.Count; i++)
            {
                var (g, shape) = row[i];
                var at = foot + front * InFront + right * ((i - centre) * HeadSpacing)
                    + Vector3.Up * (lower + 1.5f * Pitch);
                bool flasher = main && Flasher(plan, g) >= 0;
                heads.Add(new Head(at, front, right, g, shape, 3, flasher));
            }
        }
        if (ped)
        {
            var (front, right) = Frame(pole.PedHeading);
            int g = Find(plan, SignalGroupKind.Pedestrian, pole.Arm, SignalMoves.None);
            int lenses = plan.PedestrianAmber ? 3 : 2;
            bool geneva = !plan.PedestrianAmber;
            var at = foot + front * InFront + Vector3.Up * (PedestrianLowerEdge + (geneva ? GenevaHalfHeight : lenses * Pitch * 0.5f));
            heads.Add(new Head(at, front, right, g, Shape.Square, lenses, false, SideBySide: geneva));
        }
        return heads;
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
        Vector3 foot, float top)
    {
        // a round pole with a cap a little wider than it (collision stays the square column)
        var colour = PoleColour.SrgbToLinear();
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
    private static void LensFittings(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        Vector3 centre, Vector3 right, Vector3 front, float scale, bool square)
    {
        float r = LensRadius * scale, rim = RimWidth * scale;
        var rimColour = Rim.SrgbToLinear();
        var ringAt = centre + front * 0.003f;
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
        SignalMoves moves, Vector3 centre, Vector3 right, float size)
    {
        var colour = Housing.SrgbToLinear();
        foreach (var polygon in SignalGlyphs.Arrow(moves))
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
