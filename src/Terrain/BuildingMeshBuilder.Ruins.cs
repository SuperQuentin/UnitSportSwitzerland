using Godot;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Ruined castles (#859): a ruin's surveyed solid is a closed box with a flat lid, which drew a
/// ruin as a roofed tower. It is drawn and collided instead as its walls broken down: each wall run
/// of the solid becomes a slab as thick as a castle wall, cut into 0.8 m columns whose tops follow a
/// broken line (the corners standing taller than the middles, as they do), capped with mossy stone,
/// open to the sky inside, so the ground shows within the walls, and what fell off them lies at their
/// foot as tumbled blocks. A pure function of the tile like the rest of the castle dress: server and
/// clients build the same broken walls, the same stones and the same collision.
/// </summary>
public static partial class BuildingMeshBuilder
{
    /// <summary>The width of one broken column of a ruin's wall, metres.</summary>
    public const float RuinColumn = 0.8f;
    /// <summary>A wall run shorter or lower than this is a step in the TIN, not a wall to rebuild.</summary>
    private const float RuinMinLength = 1.0f, RuinMinHeight = 1.5f;

    /// <summary>
    /// Mirror of <c>BuildingExtractor.FoundationDepth</c> (tools/TerrainPreprocessor): a solid's base is
    /// set this far under the median ground of its footprint, so its foot plus this is the ground.
    /// </summary>
    private const float FoundationDepth = 0.8f;
    /// <summary>One fallen block per this many metres a column lost, at most <see cref="StonesPerColumn"/>.</summary>
    private const float StoneEvery = 2.5f;
    private const int StonesPerColumn = 4;

    private enum RuinFace : byte { Outer, Inner, Cap, Step, Rubble }

    /// <summary>
    /// One quad of a rebuilt ruin: its corners wound outward, which face it is, its wall run, and for a
    /// fallen block its tone (its stone's own shade, moss under 0).
    /// </summary>
    private readonly record struct RuinQuad(Vector3 A, Vector3 B, Vector3 C, Vector3 D, RuinFace Face, WallRun Run, float Tone = 1f);

    /// <summary>The facade vertices of rebuilt ruins, which the shader dresses as castle walls.</summary>
    private sealed class FacadeSink
    {
        public readonly List<Vector3> V = new();
        public readonly List<Color> C = new();
        public readonly List<Vector2> Uv = new();
        public readonly List<Vector2> Uv2 = new();
        public readonly List<float> Frames = new();
    }

    /// <summary>Whether solid <paramref name="index"/> is a ruin drawn as broken walls (and not as its solid).</summary>
    public static bool RebuiltRuin(BuildingTile tile, BuildingTypeMap types, int index) =>
        types.PartOf(index) == BuildingPart.None && types.CastleOf(index) is >= 0 and var castle
        && Castles.List[castle].Kind == CastleKind.Ruin && Drawn(tile.Buildings[index])
        && RuinWalls(tile.Buildings[index], index, types.Boxes[index]).Count > 0;

    /// <summary>
    /// A ruin's broken walls: per wall run (vertical, at least <see cref="RuinMinLength"/> long and
    /// <see cref="RuinMinHeight"/> tall), columns <see cref="RuinColumn"/> wide from the run's foot up
    /// to a broken height (a smooth noise along the wall plus a stone-to-stone jitter, dropping up to
    /// 85% of the wall, half as much at its ends), each with its outer face, its inner face a wall's
    /// thickness in, its cap, and the steps between columns; the run's two ends closed (2 cm in, so a corner hides them).
    /// Under a wall standing on the ground, what each column lost lies at its foot (<see cref="FallenStones"/>).
    /// </summary>
    private static List<RuinQuad> RuinWalls(Building b, int index, PlanBox? box)
    {
        var quads = new List<RuinQuad>();
        var runs = new List<WallRun>();
        WallRuns(b, float.MaxValue, runs);
        var walls = new Dictionary<(Vector3, float), (WallRun Run, Vector3 On)>();
        for (int t = 0; t < runs.Count; t++)
        {
            var run = runs[t];
            if (run.Length < RuinMinLength || run.Top - run.Bottom < RuinMinHeight) continue;
            walls.TryAdd((run.Tangent, run.Start), (run, b.Tri(t).A));
        }
        // a castle wall's thickness, thinner for a small tower
        float thick = Mathf.Clamp((box?.Short ?? 5f) * 0.22f, 0.6f, 1.4f);

        foreach (var (run, on) in walls.Values.OrderBy(w => w.Run.Start).ThenBy(w => w.Run.Tangent.X).ThenBy(w => w.Run.Tangent.Z))
        {
            var along = run.Tangent;
            var o = new Vector3(along.Z, 0f, -along.X); // outward, as WallRuns turns the tangent
            float plane = on.X * o.X + on.Z * o.Z, height = run.Top - run.Bottom;
            string key = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{index}|{run.Start:F1}|{along.X:F2}|ruin");
            float Noise(float x)
            {
                float i = MathF.Floor(x), fr = x - i;
                float h0 = (float)Core.Fnv.Unit(key + (int)i), h1 = (float)Core.Fnv.Unit(key + ((int)i + 1));
                return Mathf.Lerp(h0, h1, fr * fr * (3f - 2f * fr));
            }
            int columns = Mathf.Max(1, Mathf.CeilToInt(run.Length / RuinColumn));
            float w = run.Length / columns;
            var tops = new float[columns];
            for (int k = 0; k < columns; k++)
            {
                float u = (k + 0.5f) * w, edge = Mathf.Min(u, run.Length - u) / Mathf.Max(run.Length, 1f);
                float drop = 0.12f + 0.62f * Noise(u / 5f) + 0.16f * ((float)Core.Fnv.Unit(key + "|c" + k) - 0.5f);
                drop *= Mathf.Lerp(0.5f, 1f, Mathf.SmoothStep(0f, 0.25f, edge));
                tops[k] = run.Bottom + height * (1f - Mathf.Clamp(drop, 0.04f, 0.85f));
            }
            Vector3 P(float u, float y, float out_) => along * (run.Start + u) + o * (plane + out_) + Vector3.Up * y;
            for (int k = 0; k < columns; k++)
            {
                float u0 = k * w, u1 = u0 + w, y = tops[k];
                quads.Add(new RuinQuad(P(u0, run.Bottom, 0f), P(u1, run.Bottom, 0f), P(u1, y, 0f), P(u0, y, 0f), RuinFace.Outer, run));
                quads.Add(new RuinQuad(P(u1, run.Bottom, -thick), P(u0, run.Bottom, -thick), P(u0, y, -thick), P(u1, y, -thick), RuinFace.Inner, run));
                quads.Add(new RuinQuad(P(u0, y, 0f), P(u1, y, 0f), P(u1, y, -thick), P(u0, y, -thick), RuinFace.Cap, run));
                // the step up to the next column; at the run's ends the whole end, set 2 cm in, so at a
                // corner it hides inside the next wall's slab instead of lying in its outer face
                float next = k + 1 < columns ? tops[k + 1] : run.Bottom, ue = k + 1 < columns ? u1 : u1 - 0.02f;
                if (y > next + 0.01f)
                    quads.Add(new RuinQuad(P(ue, next, 0f), P(ue, next, -thick), P(ue, y, -thick), P(ue, y, 0f), RuinFace.Step, run));
                float prev = k > 0 ? tops[k - 1] : run.Bottom, us = k > 0 ? u0 : u0 + 0.02f;
                if (y > prev + 0.01f)
                    quads.Add(new RuinQuad(P(us, prev, -thick), P(us, prev, 0f), P(us, y, 0f), P(us, y, -thick), RuinFace.Step, run));
                // what this column lost, fallen at its foot; a wall high on the solid drops onto a roof
                // that is gone, so only a wall on the ground keeps its stones
                if (run.Bottom < b.MinY + CastleFootReach)
                    FallenStones(quads, run, key + "|s" + k, b.MinY + FoundationDepth, run.Top - y, u0, w, thick, P);
            }
        }
        return quads;
    }

    /// <summary>
    /// The blocks a broken column dropped: one per <see cref="StoneEvery"/> metres it lost (a stone-to-
    /// stone chance for the rest), at most <see cref="StonesPerColumn"/>; seven in ten fell outward,
    /// the rest into the courtyard; the higher the fall the further they rolled (up to 2.5 m); each
    /// 0.25-0.8 m, squarish, turned and tipped at random and half sunk in the ground, which the
    /// solid's foot only knows to its footprint's median.
    /// </summary>
    private static void FallenStones(List<RuinQuad> quads, WallRun run, string key, float ground, float lost,
        float u0, float w, float thick, Func<float, float, float, Vector3> at)
    {
        float H(int stone, int i) => (float)Core.Fnv.Unit(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{key}|{stone}|{i}"));
        int count = Mathf.Min(StonesPerColumn, Mathf.FloorToInt(lost / StoneEvery + H(-1, 0)));
        var along = run.Tangent;
        for (int s = 0; s < count; s++)
        {
            bool inside = H(s, 1) < 0.3f;
            float size = 0.25f + 0.55f * H(s, 2) * H(s, 2);
            var half = new Vector3(size * (0.8f + 0.5f * H(s, 3)), size * (0.45f + 0.35f * H(s, 4)), size * (0.7f + 0.5f * H(s, 5))) * 0.5f;
            float dist = 0.1f + H(s, 6) * Mathf.Min(2.5f, 0.4f + lost * 0.25f) + half.Z;
            float out_ = inside ? -thick - dist : dist;
            // sunk: its bottom 30% of its height under the ground, and as much again on a slope
            var centre = at(u0 + w * H(s, 7), ground + half.Y * 0.4f - 0.15f, out_);
            var basis = new Basis(Vector3.Up, H(s, 8) * Mathf.Tau) * new Basis(Vector3.Right, (H(s, 9) - 0.5f) * 0.7f)
                * new Basis(Vector3.Forward, (H(s, 10) - 0.5f) * 0.5f);
            // a stone's shade, a mossy one now and then (a negative tone)
            float tone = (0.72f + 0.38f * H(s, 11)) * (H(s, 12) < 0.35f ? -1f : 1f);
            Vector3 C(float x, float y, float z) => centre + basis * new Vector3(x * half.X, y * half.Y, z * half.Z);
            // five faces: the bottom is in the ground
            quads.Add(new RuinQuad(C(-1, 1, -1), C(1, 1, -1), C(1, 1, 1), C(-1, 1, 1), RuinFace.Rubble, run, tone));
            quads.Add(new RuinQuad(C(-1, -1, 1), C(1, -1, 1), C(1, 1, 1), C(-1, 1, 1), RuinFace.Rubble, run, tone * 0.9f));
            quads.Add(new RuinQuad(C(1, -1, -1), C(-1, -1, -1), C(-1, 1, -1), C(1, 1, -1), RuinFace.Rubble, run, tone * 0.9f));
            quads.Add(new RuinQuad(C(1, -1, 1), C(1, -1, -1), C(1, 1, -1), C(1, 1, 1), RuinFace.Rubble, run, tone * 0.82f));
            quads.Add(new RuinQuad(C(-1, -1, -1), C(-1, -1, 1), C(-1, 1, 1), C(-1, 1, -1), RuinFace.Rubble, run, tone * 0.82f));
        }
    }

    /// <summary>
    /// A ruin's broken walls into the meshes: the outer and inner faces as castle walls (the ruin
    /// variant: v metres above the run's foot, the run's full height, so its openings break off
    /// with it), the caps and steps as plain stone gone green with moss.
    /// </summary>
    private static void AppendRuin(Building b, int index, PlanBox? box, CastleDress dress, FacadeSink sink,
        List<Vector3> v, List<Color> c, List<float> f)
    {
        var wall = dress.Wall.SrgbToLinear();
        var cap = (dress.Wall * new Color(0.78f, 0.86f, 0.70f)).SrgbToLinear();
        var broken = (dress.Wall * 0.8f).SrgbToLinear();
        var moss = new Color(0.62f, 0.74f, 0.52f);
        foreach (var q in RuinWalls(b, index, box))
        {
            // a fallen block: the wall's stone in its own shade, some of them grown over
            if (q.Face == RuinFace.Rubble)
            {
                var stone = q.Tone < 0f ? dress.Wall * moss * -q.Tone : dress.Wall * q.Tone;
                stone.A = 1f;
                Quad(v, c, f, q.A, q.B, q.C, q.D, stone.SrgbToLinear());
                continue;
            }
            // the tops gone green with moss; the broken faces between them bare, darker stone
            if (q.Face is RuinFace.Cap or RuinFace.Step)
            {
                Quad(v, c, f, q.A, q.B, q.C, q.D, q.Face == RuinFace.Cap ? cap : broken);
                continue;
            }
            var run = q.Run;
            float flag = CastleWallFlag + 4f * CastleRuin + (run.Bottom < b.MinY + CastleFootReach ? 2f : 0f);
            void Vert(Vector3 p)
            {
                sink.V.Add(p);
                sink.C.Add(wall);
                sink.Uv.Add(new Vector2(p.X * run.Tangent.X + p.Z * run.Tangent.Z - run.Start, p.Y - run.Bottom));
                sink.Uv2.Add(new Vector2(run.Top - run.Bottom, flag));
                sink.Frames.Add(run.Tangent.X);
                sink.Frames.Add(run.Tangent.Z);
                sink.Frames.Add(run.Length);
                sink.Frames.Add(0f);
            }
            // both windings, like every castle part: the TIN's own winding is not to be trusted
            Vert(q.A); Vert(q.B); Vert(q.C); Vert(q.A); Vert(q.C); Vert(q.D);
            Vert(q.A); Vert(q.C); Vert(q.B); Vert(q.A); Vert(q.D); Vert(q.C);
        }
    }

    /// <summary>A rebuilt ruin's collision: every quad of its broken walls, both windings (the shape is one-sided).</summary>
    private static void RuinCollision(Building b, int index, PlanBox? box, List<Vector3> faces)
    {
        foreach (var q in RuinWalls(b, index, box))
        {
            faces.Add(q.A); faces.Add(q.B); faces.Add(q.C); faces.Add(q.A); faces.Add(q.C); faces.Add(q.D);
            faces.Add(q.A); faces.Add(q.C); faces.Add(q.B); faces.Add(q.A); faces.Add(q.D); faces.Add(q.C);
        }
    }
}
