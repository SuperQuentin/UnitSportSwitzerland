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
    private static readonly Color Border = new(0.90f, 0.90f, 0.88f);

    /// <summary>Lens pitch and the 200 mm lens drawn a little smaller than its housing's cell.</summary>
    public const float Pitch = 0.3f, LensRadius = 0.09f;
    private const float HeadWidth = 0.3f, HeadDepth = 0.22f, BoardMargin = 0.1f, BoardBorder = 0.04f, BoardThickness = 0.02f;
    /// <summary>Head centre to head centre across a pole.</summary>
    private const float HeadSpacing = 0.55f;
    /// <summary>Lower edge of the car heads at the roadside, 2.35-3.50 m (SSV Art. 71): low alone, high above a pedestrian head.</summary>
    private const float CarLowerEdge = 2.35f, CarLowerEdgeAbovePedestrian = 3.35f, PedestrianLowerEdge = 2.3f;
    private const float PoleRadius = RoadSigns.PoleDiameter * 0.5f, Sink = 0.5f;
    /// <summary>A head's face stands this far in front of the pole's axis: the pole, the board, the housing.</summary>
    private const float InFront = PoleRadius + BoardThickness + HeadDepth + 0.01f;

    public enum Shape : byte { Circle = 0, LeftArrow = 1, RightArrow = 2, Square = 3 }

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
    private readonly record struct Head(Vector3 Centre, Vector3 Front, Vector3 Right, int Group, Shape Shape, int Lenses, bool Flasher);

    /// <summary>Appends every pole, housing and backboard of the tile to a road mesh under construction.</summary>
    public static void Append(RoadTile tile, List<Vector3> vertices, List<Color> colors, List<Vector2> uvs,
        List<Vector2> uv2s, List<int> indices)
    {
        foreach (var signal in tile.Signals)
            foreach (var pole in signal.Poles)
            {
                var heads = Heads(signal.Plan, pole);
                var foot = new Vector3(pole.X, pole.Y, pole.Z);
                float top = 0f;
                foreach (var h in heads) top = Mathf.Max(top, h.Centre.Y - pole.Y + h.Lenses * Pitch * 0.5f + BoardMargin);
                Column(vertices, colors, uvs, uv2s, indices, foot, top);
                foreach (var h in heads)
                {
                    float half = h.Lenses * Pitch * 0.5f;
                    // a bracket from the pole to a head beside it
                    var reach = h.Centre - foot;
                    reach.Y = 0;
                    if (reach.Length() > PoleRadius * 2)
                        Box(vertices, colors, uvs, uv2s, indices, Housing.SrgbToLinear(),
                            new Vector3(foot.X, h.Centre.Y, foot.Z) + reach * 0.5f, reach.Normalized(), 0.04f, reach.Length() * 0.5f, 0.04f);
                    // the backboard: white border, black field, then the housing in front
                    var board = h.Centre - h.Front * (HeadDepth + BoardThickness * 0.5f);
                    Plate(vertices, colors, uvs, uv2s, indices, Border.SrgbToLinear(), board, h.Front, h.Right,
                        HeadWidth * 0.5f + BoardMargin, half + BoardMargin);
                    Plate(vertices, colors, uvs, uv2s, indices, Housing.SrgbToLinear(), board + h.Front * 0.004f, h.Front, h.Right,
                        HeadWidth * 0.5f + BoardMargin - BoardBorder, half + BoardMargin - BoardBorder);
                    Box(vertices, colors, uvs, uv2s, indices, Housing.SrgbToLinear(), h.Centre - h.Front * (HeadDepth * 0.5f), h.Right,
                        HeadWidth * 0.5f, HeadDepth * 0.5f, half, h.Front);
                    if (h.Flasher)
                    {
                        // the flasher's own small housing beside the green
                        var at = h.Centre + h.Right * (HeadWidth + 0.02f) + Vector3.Down * (half - Pitch * 0.5f);
                        Box(vertices, colors, uvs, uv2s, indices, Housing.SrgbToLinear(), at - h.Front * (HeadDepth * 0.5f), h.Right,
                            HeadWidth * 0.5f, HeadDepth * 0.5f, Pitch * 0.5f, h.Front);
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
                    float half = h.Lenses * Pitch * 0.5f;
                    for (int k = 0; k < h.Lenses; k++)
                    {
                        // top down: red, (yellow), green
                        var role = h.Lenses == 2 ? (k == 0 ? Role.Red : Role.Green) : (Role)k;
                        var at = h.Centre + Vector3.Up * (half - Pitch * (k + 0.5f)) + face;
                        lamps.Lenses.Add(new Lens(h.Shape, new Transform3D(basis, at), j, h.Group, role));
                    }
                    if (h.Flasher && Flasher(signal.Plan, h.Group) is int f and >= 0)
                    {
                        var at = h.Centre + h.Right * (HeadWidth + 0.02f) + Vector3.Down * (half - Pitch * 0.5f) + face;
                        lamps.Lenses.Add(new Lens(Shape.Circle, new Transform3D(basis, at), j, f, Role.Flash));
                    }
                }
        }
        return lamps.Lenses.Count == 0 ? null : lamps;
    }

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
            var at = foot + front * InFront + Vector3.Up * (PedestrianLowerEdge + lenses * Pitch * 0.5f);
            heads.Add(new Head(at, front, right, g, Shape.Square, lenses, false));
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
        var colour = PoleColour.SrgbToLinear();
        var c = new[] { new Vector3(PoleRadius, 0, PoleRadius), new Vector3(-PoleRadius, 0, PoleRadius),
            new Vector3(-PoleRadius, 0, -PoleRadius), new Vector3(PoleRadius, 0, -PoleRadius) };
        for (int k = 0; k < 4; k++)
            Polygon(vertices, colors, uvs, uv2s, indices, colour,
                [foot + c[k] + Vector3.Down * Sink, foot + c[(k + 1) % 4] + Vector3.Down * Sink,
                 foot + c[(k + 1) % 4] + Vector3.Up * top, foot + c[k] + Vector3.Up * top]);
        Polygon(vertices, colors, uvs, uv2s, indices, colour,
            [foot + c[3] + Vector3.Up * top, foot + c[2] + Vector3.Up * top, foot + c[1] + Vector3.Up * top, foot + c[0] + Vector3.Up * top]);
    }

    /// <summary>A flat rectangle facing <paramref name="front"/>, both sides drawn (the road material is two-sided).</summary>
    private static void Plate(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        Color colour, Vector3 centre, Vector3 front, Vector3 right, float halfWidth, float halfHeight)
    {
        var r = right * halfWidth;
        var u = Vector3.Up * halfHeight;
        Polygon(vertices, colors, uvs, uv2s, indices, colour, [centre - r - u, centre + r - u, centre + r + u, centre - r + u]);
    }

    /// <summary>A box round <paramref name="centre"/>: half sizes along <paramref name="right"/>, the horizontal at right angles to it, and up.</summary>
    private static void Box(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices,
        Color colour, Vector3 centre, Vector3 right, float halfRight, float halfDepth, float halfUp, Vector3? front = null)
    {
        var f = front ?? new Vector3(-right.Z, 0, right.X);
        var x = right * halfRight;
        var z = f * halfDepth;
        var y = Vector3.Up * halfUp;
        Vector3 P(int sx, int sy, int sz) => centre + x * sx + y * sy + z * sz;
        Polygon(vertices, colors, uvs, uv2s, indices, colour, [P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1)]);     // front
        Polygon(vertices, colors, uvs, uv2s, indices, colour, [P(1, -1, -1), P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1)]); // back
        Polygon(vertices, colors, uvs, uv2s, indices, colour, [P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1)]); // left
        Polygon(vertices, colors, uvs, uv2s, indices, colour, [P(1, -1, 1), P(1, -1, -1), P(1, 1, -1), P(1, 1, 1)]);     // right
        Polygon(vertices, colors, uvs, uv2s, indices, colour, [P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), P(-1, 1, -1)]);     // top
        Polygon(vertices, colors, uvs, uv2s, indices, colour, [P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1)]); // bottom
    }

    private static void Polygon(List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s,
        List<int> indices, Color color, Vector3[] ring)
    {
        int start = vertices.Count;
        foreach (var v in ring)
        {
            vertices.Add(v);
            colors.Add(color);
            uvs.Add(Vector2.Zero);
            uv2s.Add(Vector2.Zero);
        }
        for (int i = 1; i + 1 < ring.Length; i++) { indices.Add(start); indices.Add(start + i); indices.Add(start + i + 1); }
    }
}
