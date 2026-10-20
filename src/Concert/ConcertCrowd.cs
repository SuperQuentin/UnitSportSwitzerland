using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Stadiums;

namespace UnitSport.Concert;

/// <summary>
/// The audience of a concert (#885): people on the stadium's seats (<see cref="StadiumBuilder.Seats"/>)
/// and standing on the pitch in front of the stage, as instances of one 36-triangle figure in a few
/// multimeshes, drawn by <c>shaders/concert/crowd.gdshader</c>: a shirt colour each, a hop on the
/// beat as hard as the show goes, hands up in a chorus, and the rig's colour on them. Nobody is a
/// node and nothing per person runs on the CPU: a frame sets four uniforms.
///
/// <para>
/// Seats behind an end or side stage are not sold. The floor is filled from the barrier back, densest
/// at the front, never under the stage, its catwalk, the front-of-house riser, the delay towers or an
/// imported set's pieces on the floor (#974, <see cref="StagePlan.ObjectFootprints"/>).
/// Only during the show (the rig says when); out past <see cref="Range"/> m it is not drawn.
/// </para>
/// </summary>
public partial class ConcertCrowd : Node3D
{
    /// <summary>One person in this many seats is drawn, and the share of the floor's places taken.</summary>
    private const int SeatSpacing = 2;
    private const float Occupancy = 0.8f, FloorSpacing = 1.3f;
    private const int MaxFloor = 24000;
    private const float Range = 700f;
    private const int Batches = 4;

    private static readonly StringName UBeat = "beat", UEnergy = "energy", UTint = "tint", UArms = "arms_up";

    private readonly StagePlan _plan;
    private ShaderMaterial _material = null!;
    public int People { get; private set; }

    public ConcertCrowd(StagePlan plan)
    {
        _plan = plan;
        Name = "Crowd";
    }

    /// <summary>
    /// The ground's height at a point of the venue's frame (#948): an open air's field as it lies, an
    /// arena's raised floor; null for a stadium's pitch, level at 0.
    /// </summary>
    public Func<Vector2, float>? Ground { get; init; }

    public ConcertCrowd() : this(new StagePlan(ConcertPlanner.Plan(StadiumCatalog.Geneva, 1))) { }

    public override void _Ready()
    {
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/concert/crowd.gdshader") };
        var places = Places();
        People = places.Count;
        var mesh = Figure();
        int per = (places.Count + Batches - 1) / Batches;
        for (int b = 0; b < Batches; b++)
        {
            int from = b * per, n = Math.Min(per, places.Count - from);
            if (n <= 0) break;
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = mesh, InstanceCount = n,
            };
            for (int i = 0; i < n; i++)
            {
                var (t, custom) = places[from + i];
                mm.SetInstanceTransform(i, t);
                mm.SetInstanceCustomData(i, custom);
            }
            AddChild(new MultiMeshInstance3D
            {
                Name = $"Crowd{b}", Multimesh = mm, MaterialOverride = _material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, VisibilityRangeEnd = Range,
            });
        }
    }

    /// <summary>The show's state, every frame: the beat, how hard they dance, the light on them, hands up.</summary>
    public void Drive(float beat, float energy, Color tint, float armsUp)
    {
        _material.SetShaderParameter(UBeat, beat);
        _material.SetShaderParameter(UEnergy, energy);
        _material.SetShaderParameter(UTint, tint);
        _material.SetShaderParameter(UArms, armsUp);
    }

    // ---- where they are -------------------------------------------------------------------------------

    private static readonly Color[] Shirts =
    {
        new(0.08f, 0.08f, 0.09f), new(0.9f, 0.9f, 0.88f), new(0.55f, 0.08f, 0.12f), new(0.15f, 0.25f, 0.55f),
        new(0.2f, 0.2f, 0.22f), new(0.85f, 0.75f, 0.2f), new(0.25f, 0.45f, 0.25f), new(0.6f, 0.35f, 0.55f),
    };

    private List<(Transform3D, Color)> Places()
    {
        var list = new List<(Transform3D, Color)>();
        var rng = new Random(_plan.Event.Seed ^ 0x5eed);
        void Person(Vector3 at, Vector2 facing)
        {
            // facing -Z of the figure along the facing
            float yaw = Mathf.Atan2(-facing.X, -facing.Y);
            float scale = 0.92f + (float)rng.NextDouble() * 0.16f;
            var t = new Transform3D(new Basis(Vector3.Up, yaw).Scaled(Vector3.One * scale), at);
            var shirt = Shirts[rng.Next(Shirts.Length)];
            // some wear the artist's colour
            if (rng.NextDouble() < 0.18) shirt = _plan.Event.Palette[0] * 0.8f;
            list.Add((t, new Color((float)rng.NextDouble(), shirt.R, shirt.G, shirt.B)));
        }

        // the seats: not behind the stage, a share of them taken
        foreach (var (pos, facing) in StadiumBuilder.Seats(_plan.Event.Stadium, SeatSpacing))
        {
            if (!_plan.Round && (new Vector2(pos.X, pos.Z) - _plan.Origin).Dot(_plan.Forward) < -_plan.Event.Depth) continue;
            if (rng.NextDouble() > Occupancy) continue;
            // standing at their seat, on the row: the hip point is the tread
            Person(pos - new Vector3(0, 0.05f, 0), facing);
        }

        // the floor: everyone looks at the stage; densest in front of it. A festival field is wide:
        // spread out to at most MaxFloor people, as the eye counts a crowd from inside it
        var s = _plan.Event.Stadium;
        float spacing = Math.Max(FloorSpacing, MathF.Sqrt(4f * s.FrontHalfU * s.FrontHalfV * 0.55f / MaxFloor));
        for (float u = -s.FrontHalfU + 2; u < s.FrontHalfU - 2; u += spacing)
            for (float v = -s.FrontHalfV + 2; v < s.FrontHalfV - 2; v += spacing)
            {
                var p = new Vector2(u, v) + new Vector2((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * spacing * 0.7f;
                if (_plan.Blocked(p, 1.2f)) continue;
                Vector2 toStage;
                float density;
                if (_plan.Round)
                {
                    toStage = -p.Normalized();
                    density = Mathf.Clamp(1.2f - p.Length() / 55f, 0.2f, 1f);
                }
                else
                {
                    float ahead = (p - _plan.Origin).Dot(_plan.Forward);
                    if (ahead < 3f) continue;   // behind the barrier, or beside and behind the stage
                    toStage = (_plan.Origin - p).Normalized();
                    density = Mathf.Clamp(1.15f - ahead / 70f, 0.15f, 1f);
                }
                if (rng.NextDouble() > density) continue;
                Person(new Vector3(p.X, Ground?.Invoke(p) ?? 0f, p.Y), toStage);
            }
        return list;
    }

    // ---- the figure ---------------------------------------------------------------------------------

    /// <summary>Legs, a shirt, a head: three boxes, 36 triangles, the feet at the origin, facing −Z. Alpha 1 marks the shirt.</summary>
    [Showcase("Concert", "Crowd figure")]
    private static ArrayMesh Figure()
    {
        var v = new List<Vector3>();
        var c = new List<Color>();
        void Box(Vector3 min, Vector3 max, Color col)
        {
            Vector3 P(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
            void Q(Vector3 a, Vector3 b, Vector3 cc, Vector3 d, float shade)
            {
                var col2 = new Color(col.R * shade, col.G * shade, col.B * shade, col.A);
                foreach (var p in new[] { a, b, cc, a, cc, d }) { v.Add(p); c.Add(col2); }
            }
            Q(P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1), 1f);
            Q(P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1), 0.85f);
            Q(P(1, 0, 0), P(0, 0, 0), P(0, 1, 0), P(1, 1, 0), 0.75f);
            Q(P(1, 0, 1), P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), 0.8f);
            Q(P(0, 0, 0), P(0, 0, 1), P(0, 1, 1), P(0, 1, 0), 0.8f);
            Q(P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1), 0.6f);
        }
        Box(new Vector3(-0.17f, 0f, -0.1f), new Vector3(0.17f, 0.8f, 0.1f), new Color(0.12f, 0.13f, 0.18f, 0f));     // jeans
        Box(new Vector3(-0.22f, 0.8f, -0.13f), new Vector3(0.22f, 1.45f, 0.13f), new Color(1f, 1f, 1f, 1f));       // shirt
        Box(new Vector3(-0.1f, 1.47f, -0.11f), new Vector3(0.1f, 1.72f, 0.11f), new Color(0.78f, 0.6f, 0.48f, 0f)); // head
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = c.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
