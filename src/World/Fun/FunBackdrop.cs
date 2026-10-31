using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Fixture;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Fun;

/// <summary>
/// The fun world's synthwave look (#1396, <c>docs/notes/world/fun-world.md</c>), client only: the sky swapped for a
/// violet dome with a striped retro sun on the horizon (<c>shaders/fun_sky.gdshader</c>, the glow on), a neon grid
/// floor under the course to the horizon (<c>fun_grid</c>), a ring of wireframe mountains (<c>fun_wire</c>), palm
/// silhouettes along the boulevard, the beach and the track, neon tubes along every road's edges, the runway, the
/// venues' rooflines and the PvP block, and a sign over the boulevard. Everything is built once, from the course
/// (<see cref="FunCourse"/>); only the floor follows the camera. A container for the floating origin.
/// </summary>
public partial class FunBackdrop : Node3D, IOriginContainer
{
    public static FunBackdrop? Instance { get; private set; }

    private readonly ChunkManager? _chunks;
    private readonly WorldOrigin _origin;
    private readonly Godot.Environment _environment;
    private MeshInstance3D? _floor;
    private Vector3 _holeAt = new(float.NaN, 0, 0);
    private double _tilesMinE, _tilesMinN, _tilesMaxE, _tilesMaxN;
    private static readonly double StartE = FunWorld.Start.E, StartN = FunWorld.Start.N;

    // the palette: hot pink, cyan, violet, sunset yellow
    public static readonly Color Pink = new(1f, 0.18f, 0.72f), Cyan = new(0.15f, 0.92f, 1f),
        Violet = new(0.62f, 0.3f, 1f), Yellow = new(1f, 0.82f, 0.25f), Red = new(1f, 0.15f, 0.2f), Orange = new(1f, 0.5f, 0.12f);

    /// <summary>The floor's height under the flat course, so the terrain hides it wherever the course is.</summary>
    private const float FloorDrop = 0.8f;

    public FunBackdrop(ChunkManager? chunks, WorldOrigin origin, Godot.Environment environment)
    {
        Name = "FunBackdrop";
        _chunks = chunks;
        _origin = origin;
        _environment = environment;
    }

    public FunBackdrop() : this(null, null!, null!) { }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        Restyle(_environment);
        var course = FunCourse.Create();
        // the course's tiles, as its source covers them
        var (minX, minY, maxX, maxY) = course.Bounds(0);
        var lo = TileId.FromLv95(StartE + minX, StartN + minY);
        var hi = TileId.FromLv95(StartE + maxX, StartN + maxY);
        (_tilesMinE, _tilesMinN, _tilesMaxE, _tilesMaxN) = (lo.MinE, lo.MinN, hi.MinE + ChunkFormat.TileSizeM, hi.MaxN);
        _floor = new MeshInstance3D
        {
            Name = "GridFloor",
            Mesh = new PlaneMesh { Size = new Vector2(30000, 30000) },
            MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fun_grid.gdshader") },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_floor);
        AddChild(new MeshInstance3D
        {
            Name = "Mountains", Mesh = Mountains(5200, 7400, 0),
            Position = At(150, 0, -FloorDrop),
            MaterialOverride = WireMaterial(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        AddChild(Palms(PalmSpots()));
        AddChild(Lamps());
        AddChild(new MeshInstance3D
        {
            Name = "NeonLines", Mesh = Neon(course),
            MaterialOverride = TubeMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        AddChild(Sign());
        GD.Print("[fun] the synthwave backdrop is up");
    }

    public override void _Process(double delta)
    {
        // the floor under the camera, snapped to its own grid so the lines never slide
        if (_floor == null || GetViewport()?.GetCamera3D() is not { } cam) return;
        var p = cam.GlobalPosition;
        float flat = (float)FunCourse.Flat - FloorDrop;
        _floor.GlobalPosition = new Vector3(Mathf.Snapped(p.X, 100f), flat, Mathf.Snapped(p.Z, 100f));
        // the hole where the course's tiles are, in this frame: set again only when the origin moved
        var corner = _origin.ToWorld(_tilesMinE, _tilesMaxN, 0);
        if (corner == _holeAt) return;
        _holeAt = corner;
        var far = _origin.ToWorld(_tilesMaxE, _tilesMinN, 0);
        ((ShaderMaterial)_floor.MaterialOverride).SetShaderParameter("hole", new Vector4(corner.X, corner.Z, far.X, far.Z));
    }

    /// <summary>
    /// The synthwave sky on <paramref name="env"/>, and the glow that haloes the neon: on this world's environment and
    /// on each one a restyle makes. The day-night clock then drives only its ambient light (a sky that is not the
    /// style's procedural one keeps its own colours).
    /// </summary>
    public void Restyle(Godot.Environment env)
    {
        var sky = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fun_sky.gdshader") };
        var sun = SunPath.Direction(FunWorld.DuskHour);
        // the disc sits on the horizon whatever the hour says: its centre a little up
        sun = new Vector3(sun.X, 0f, sun.Z).Normalized() * 0.985f + Vector3.Up * 0.17f;
        sky.SetShaderParameter("sun_dir", sun.Normalized());
        env.BackgroundMode = Godot.Environment.BGMode.Sky;
        env.Sky ??= new Sky();
        env.Sky.SkyMaterial = sky;
        env.GlowEnabled = true;
        env.GlowIntensity = 0.9f;
        env.GlowBloom = 0.05f;
        env.GlowHdrThreshold = 1.05f;
        env.GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive;
        env.FogLightColor = new Color(0.42f, 0.14f, 0.5f);
    }

    /// <summary>A course point (metres from the start) in this world's space, <paramref name="lift"/> over the course's ground.</summary>
    private Vector3 At(double x, double y, float lift) =>
        _origin.ToWorld(StartE + x, StartN + y, FunCourse.Ground(x, y) + lift);

    private static ShaderMaterial? _tube;
    private static ShaderMaterial TubeMaterial => _tube ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/neon_tube.gdshader") };

    private static ShaderMaterial WireMaterial() => new() { Shader = GD.Load<Shader>("res://shaders/fun_wire.gdshader") };

    // ---- neon tubes -----------------------------------------------------------------------------

    /// <summary>
    /// Every neon line of the course in one mesh: both edges of every road (the track pink and cyan, the kart loop
    /// yellow, the streets violet), the runway's edges and centre dashes, the venues' rooflines and corners, the PvP
    /// block in red and the stunt park in orange, on the ground.
    /// </summary>
    private ArrayMesh Neon(FixtureCourse course)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        for (int r = 0; r < course.Roads.Count; r++)
        {
            var (cls, pts) = course.Roads[r];
            if (RoadFormat.IsAerial(cls)) continue;
            float half = course.RoadWidths.TryGetValue(r, out float w) ? w / 2 : RoadFormat.DefaultWidth(cls) / 2;
            var (inner, outer) = cls == RoadClass.Major ? (Pink, Cyan) : cls == RoadClass.Minor ? (Yellow, Yellow) : (Violet, Violet);
            Edge(st, pts, half - 0.25, inner, 0.35f);
            Edge(st, pts, -(half - 0.25), outer, 0.35f);
        }
        // the runway: its edges, and dashes down the middle
        foreach (double side in new[] { -1.0, 1.0 })
            Flat(st, [(FunCourse.RunwayX0, FunCourse.RunwayY + side * (FunCourse.RunwayWidth / 2 - 1)), (FunCourse.RunwayX1, FunCourse.RunwayY + side * (FunCourse.RunwayWidth / 2 - 1))], Cyan, 0.6f);
        for (double x = FunCourse.RunwayX0 + 30; x < FunCourse.RunwayX1 - 30; x += 60)
            Flat(st, [(x, FunCourse.RunwayY), (x + 30, FunCourse.RunwayY)], Pink, 0.9f);
        // the venues and the yard's warehouse: a band under the roofline and the two front corners
        Color[] roof = [Pink, Cyan, Yellow, Violet];
        int k = 0;
        foreach (var b in course.Blocks)
        {
            if (b.Height < 5) continue;
            var c = roof[k++ % roof.Length];
            double hw = b.Width / 2.0, hd = b.Depth / 2.0;
            var corners = new (double X, double Y)[] { (-hw, -hd), (hw, -hd), (hw, hd), (-hw, hd), (-hw, -hd) };
            var ring = corners.Select(p => Turned(b, p.X, p.Y)).ToList();
            Upright(st, ring, b.Height - 0.5f, 0.5f, c);
            foreach (int i in new[] { 0, 1 })
                Post(st, ring[i], 0.2f, b.Height - 0.3f, c);
        }
        var (px0, py0, px1, py1) = FunCourse.PvpBox;
        Flat(st, [(px0, py0), (px1, py0), (px1, py1), (px0, py1), (px0, py0)], Red, 0.5f);
        Flat(st, [(FunCourse.StuntX0, FunCourse.StuntY0), (FunCourse.StuntX1, FunCourse.StuntY0), (FunCourse.StuntX1, FunCourse.StuntY1), (FunCourse.StuntX0, FunCourse.StuntY1), (FunCourse.StuntX0, FunCourse.StuntY0)], Orange, 0.5f);
        Flat(st, [(FunCourse.YardX0, FunCourse.YardY0), (FunCourse.YardX1, FunCourse.YardY0), (FunCourse.YardX1, FunCourse.YardY1), (FunCourse.YardX0, FunCourse.YardY1), (FunCourse.YardX0, FunCourse.YardY0)], Yellow, 0.4f);
        var (fx, fy, fr) = FunCourse.Festival;
        var circle = Enumerable.Range(0, 73).Select(i => (fx + fr * Math.Cos(i * Math.PI / 36), fy + fr * Math.Sin(i * Math.PI / 36))).ToList();
        Flat(st, circle, Violet, 0.5f);
        return st.Commit();
    }

    private static (double X, double Y) Turned(FixtureCourse.Block b, double u, double v)
    {
        // as FixtureBlocks turns a block: v runs south (the tile's +Z), the turn about the block's middle
        double t = b.Turn * Math.PI / 180, z = v;
        double x = u * Math.Cos(t) - z * Math.Sin(t), s = u * Math.Sin(t) + z * Math.Cos(t);
        return (b.X + x, b.Y - s);
    }

    /// <summary>A tube along a road's edge: <paramref name="offset"/> metres left of its centreline (negative: right).</summary>
    private void Edge(SurfaceTool st, List<(double X, double Y, double Z)> pts, double offset, Color c, float width)
    {
        var line = new List<(double X, double Y)>(pts.Count);
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[Math.Max(0, i - 1)];
            var b = pts[Math.Min(pts.Count - 1, i + 1)];
            double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) continue;
            line.Add((pts[i].X - dy / len * offset, pts[i].Y + dx / len * offset));
        }
        Flat(st, line, c, width);
    }

    /// <summary>A tube lying on the ground along a course polyline, a few centimetres up.</summary>
    private void Flat(SurfaceTool st, IReadOnlyList<(double X, double Y)> line, Color c, float width)
    {
        for (int i = 1; i < line.Count; i++)
        {
            var a = At(line[i - 1].X, line[i - 1].Y, 0.07f);
            var b = At(line[i].X, line[i].Y, 0.07f);
            var along = b - a;
            if (along.LengthSquared() < 1e-6f) continue;
            var across = along.Cross(Vector3.Up).Normalized() * (width / 2);
            Band(st, a, b, across, c);
        }
    }

    /// <summary>An upright band round a block's ring, from <paramref name="from"/> to <paramref name="from"/> + <paramref name="height"/> over its ground.</summary>
    private void Upright(SurfaceTool st, List<(double X, double Y)> ring, float from, float height, Color c)
    {
        for (int i = 1; i < ring.Count; i++)
        {
            // the block stands on the flat; the band just off the wall, so it is not inside the block's own face
            var a = At(ring[i - 1].X, ring[i - 1].Y, 0);
            var b = At(ring[i].X, ring[i].Y, 0);
            a.Y = b.Y = (float)FunCourse.Flat + from + height / 2;
            var outward = (b - a).Cross(Vector3.Up).Normalized() * 0.06f;
            Band(st, a - outward, b - outward, Vector3.Up * (height / 2), c);
        }
    }

    /// <summary>An upright tube at a corner, from the ground to <paramref name="height"/>.</summary>
    private void Post(SurfaceTool st, (double X, double Y) at, float width, float height, Color c)
    {
        var bottom = At(at.X, at.Y, 0);
        bottom.Y = (float)FunCourse.Flat;
        var top = bottom + Vector3.Up * height;
        Band(st, bottom, top, Vector3.Right * (width / 2), c);
        Band(st, bottom, top, Vector3.Back * (width / 2), c);
    }

    /// <summary>A band from a to b, <paramref name="across"/> to each side: the core (UV.y 1) on the line, the edges 0.</summary>
    private static void Band(SurfaceTool st, Vector3 a, Vector3 b, Vector3 across, Color c)
    {
        void V(Vector3 p, float core)
        {
            st.SetColor(c);
            st.SetUV(new Vector2(0, core));
            st.AddVertex(p);
        }
        foreach (var (o0, o1) in new[] { (-1f, 0f), (0f, 1f) })
        {
            V(a + across * o0, o0 == 0 ? 1 : 0); V(b + across * o0, o0 == 0 ? 1 : 0); V(b + across * o1, o1 == 0 ? 1 : 0);
            V(a + across * o0, o0 == 0 ? 1 : 0); V(b + across * o1, o1 == 0 ? 1 : 0); V(a + across * o1, o1 == 0 ? 1 : 0);
        }
    }

    // ---- the sign over the boulevard ----------------------------------------------------------------

    private Node3D Sign()
    {
        // over the avenue behind the venues, read from the plaza
        var sign = new Node3D { Name = "Sign", Position = At(0, FunCourse.FrontY + 40, 0) };
        sign.AddChild(new Label3D
        {
            Text = "NEON FUN WORLD", FontSize = 220, OutlineSize = 0, PixelSize = 0.02f,
            Modulate = Pink * 1.6f, Shaded = false, DoubleSided = true, Position = new Vector3(0, 19, 0),
        });
        sign.AddChild(new Label3D
        {
            Text = "race · bowl · play · fly · ski · fight", FontSize = 90, OutlineSize = 0, PixelSize = 0.02f,
            Modulate = Cyan * 1.5f, Shaded = false, DoubleSided = true, Position = new Vector3(0, 15.6f, 0),
        });
        return sign;
    }

    // ---- lamp posts -----------------------------------------------------------------------------------

    /// <summary>Lamp posts every 32 m along both sides of the boulevard and the avenues, their heads pink and cyan in turn.</summary>
    private Node3D Lamps()
    {
        var pink = new List<Transform3D>();
        var cyan = new List<Transform3D>();
        int k = 0;
        void Along(double x0, double y0, double x1, double y1)
        {
            double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            double ux = (x1 - x0) / len, uy = (y1 - y0) / len;
            for (double s = 16; s < len - 8; s += 32)
                foreach (int side in new[] { 1, -1 })
                {
                    double x = x0 + ux * s - uy * 6.5 * side, y = y0 + uy * s + ux * 6.5 * side;
                    // the arm reaches over the road: the post turned to face it
                    float yaw = Mathf.Atan2((float)(-ux * side), (float)(uy * side));
                    (k++ % 2 == 0 ? pink : cyan).Add(new Transform3D(new Basis(Vector3.Up, yaw), At(x, y, 0)));
                }
        }
        Along(-FunCourse.BoulevardHalf, FunCourse.BoulevardY, FunCourse.EastRoadEnd, FunCourse.BoulevardY);
        Along(0, FunCourse.BoulevardY, 0, FunCourse.AvenueNorthEnd);
        Along(0, FunCourse.BoulevardY, 0, FunCourse.AvenueSouthEnd);
        var lamps = new Node3D { Name = "Lamps" };
        foreach (var (spots, colour) in new[] { (pink, Pink), (cyan, Cyan) })
        {
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = LampMesh(colour), InstanceCount = spots.Count };
            for (int i = 0; i < spots.Count; i++) mm.SetInstanceTransform(i, spots[i]);
            lamps.AddChild(new MultiMeshInstance3D { Multimesh = mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
        return lamps;
    }

    /// <summary>
    /// A lamp post (#1396): a dark 7 m post with an arm over the road (+X) and a glowing bar under its end, in
    /// <paramref name="glow"/>. Two surfaces: the post in the palms' material, the bar a neon tube.
    /// </summary>
    public static ArrayMesh LampMesh(Color glow)
    {
        var mesh = new ArrayMesh();
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetColor(new Color(0.12f, 0.06f, 0.16f));
        Box(st, new Vector3(0, 3.5f, 0), new Vector3(0.18f, 7f, 0.18f));
        Box(st, new Vector3(1.2f, 6.95f, 0), new Vector3(2.5f, 0.12f, 0.14f));
        st.GenerateNormals();
        st.Commit(mesh);
        mesh.SurfaceSetMaterial(0, PalmMaterial);
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetColor(glow);
        st.SetUV(new Vector2(0, 1));
        Box(st, new Vector3(1.5f, 6.82f, 0), new Vector3(1.8f, 0.12f, 0.2f));
        st.Commit(mesh);
        mesh.SurfaceSetMaterial(1, TubeMaterial);
        return mesh;

        static void Box(SurfaceTool st, Vector3 c, Vector3 size)
        {
            var h = size / 2;
            Vector3 P(int x, int y, int z) => c + new Vector3(x * h.X, y * h.Y, z * h.Z);
            int[][] faces = [[-1, -1, 1, 1, -1, 1, 1, 1, 1, -1, 1, 1], [1, -1, -1, -1, -1, -1, -1, 1, -1, 1, 1, -1],
                [1, -1, 1, 1, -1, -1, 1, 1, -1, 1, 1, 1], [-1, -1, -1, -1, -1, 1, -1, 1, 1, -1, 1, -1],
                [-1, 1, 1, 1, 1, 1, 1, 1, -1, -1, 1, -1], [-1, -1, -1, 1, -1, -1, 1, -1, 1, -1, -1, 1]];
            foreach (var f in faces)
            {
                var (a, b, cc, d) = (P(f[0], f[1], f[2]), P(f[3], f[4], f[5]), P(f[6], f[7], f[8]), P(f[9], f[10], f[11]));
                foreach (var v in new[] { a, cc, b, a, d, cc }) st.AddVertex(v);
            }
        }
    }

    [Showcase("World", "Fun world lamp posts")]
    public static IEnumerable<(string, Func<Node3D>)> LampShowcase() =>
        [("Pink", () => new MeshInstance3D { Mesh = LampMesh(Pink) }), ("Cyan", () => new MeshInstance3D { Mesh = LampMesh(Cyan) })];

    // ---- palms -------------------------------------------------------------------------------------

    /// <summary>Where the palms stand: both sides of the boulevard, round the plaza, along the beach and the track's straights.</summary>
    private static List<(double X, double Y)> PalmSpots()
    {
        var spots = new List<(double X, double Y)>();
        for (double x = -310; x <= 310; x += 28)
            if (Math.Abs(x) > 12) spots.Add((x, FunCourse.BoulevardY - 8.5));
        for (double x = -300; x <= 300; x += 56) spots.Add((x + 14, FunCourse.BoulevardY - 28));
        for (int i = 0; i < 18; i++)
        {
            double a = Math.PI * (-0.35 + i * 0.045);
            spots.Add((FunCourse.LakeX + (FunCourse.LakeR + 30) * Math.Cos(a), FunCourse.LakeY + (FunCourse.LakeR + 30) * Math.Sin(a)));
        }
        for (double x = FunCourse.TrackX0; x <= FunCourse.TrackX1; x += 40)
        {
            spots.Add((x, FunCourse.TrackY0 - 18));
            spots.Add((x + 20, FunCourse.TrackY1 + 40));
        }
        // the avenues, behind their lamp posts; the runway's south side
        for (double y = 40; y < FunCourse.AvenueNorthEnd - 30; y += 32) { spots.Add((-11, y)); spots.Add((11, y + 16)); }
        for (double y = -60; y > FunCourse.AvenueSouthEnd + 30; y -= 32) { spots.Add((-11, y)); spots.Add((11, y - 16)); }
        for (double x = FunCourse.RunwayX0; x < FunCourse.RunwayX1; x += 70) spots.Add((x, FunCourse.RunwayY - 45));
        // round the festival field, the arena and the PvP block
        var (fx, fy, fr) = FunCourse.Festival;
        for (int i = 0; i < 24; i++) spots.Add((fx + (fr + 14) * Math.Cos(i * Math.PI / 12), fy + (fr + 14) * Math.Sin(i * Math.PI / 12)));
        for (double x = FunCourse.ArenaX - 80; x <= FunCourse.ArenaX + 80; x += 32) { spots.Add((x, FunCourse.ArenaY - 95)); spots.Add((x, FunCourse.ArenaY + 95)); }
        var (px0, py0, px1, py1) = FunCourse.PvpBox;
        for (double x = px0; x <= px1; x += 40) { spots.Add((x, py0 - 10)); spots.Add((x, py1 + 10)); }
        // groves in the open ground between the areas, hashed
        foreach (var (gx, gy) in new[] { (-420.0, 180.0), (-300.0, -230.0), (230.0, 420.0), (-250.0, 400.0), (480.0, -220.0), (-450.0, -480.0), (160.0, -200.0), (-80.0, 260.0), (650.0, 330.0), (850.0, 420.0) })
            for (int i = 0; i < 9; i++)
                spots.Add((gx + (Hash(i * 17 + (int)gx) - 0.5) * 60, gy + (Hash(i * 29 + (int)gy) - 0.5) * 60));
        return spots;
    }

    private Node3D Palms(List<(double X, double Y)> spots)
    {
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = PalmMesh(), InstanceCount = spots.Count };
        for (int i = 0; i < spots.Count; i++)
        {
            float turn = i * 2.39996f, scale = 0.85f + (i * 37 % 10) * 0.04f;
            mm.SetInstanceTransform(i, new Transform3D(new Basis(Vector3.Up, turn).Scaled(Vector3.One * scale), At(spots[i].X, spots[i].Y, -0.1f)));
        }
        return new MultiMeshInstance3D { Name = "Palms", Multimesh = mm, MaterialOverride = PalmMaterial };
    }

    private static StandardMaterial3D? _palmMaterial;
    private static StandardMaterial3D PalmMaterial => _palmMaterial ??= new StandardMaterial3D
    {
        VertexColorUseAsAlbedo = true, Roughness = 0.9f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    /// <summary>
    /// A palm silhouette (#1396): a leaning, tapering trunk of eight hexagonal rings, a crown of nine drooping fronds,
    /// in dark violet and teal so it reads black against the sunset. About 9 m tall.
    /// </summary>
    [Showcase("World", "Fun world palm")]
    public static ArrayMesh PalmMesh()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var trunk = new Color(0.16f, 0.07f, 0.2f);
        var frond = new Color(0.05f, 0.16f, 0.17f);
        const int rings = 8, sides = 6;
        Vector3 Spine(float t) => new(1.4f * t * t, 9f * t, 0);
        for (int r = 0; r < rings; r++)
        {
            float t0 = r / (float)rings, t1 = (r + 1) / (float)rings;
            float r0 = Mathf.Lerp(0.28f, 0.16f, t0), r1 = Mathf.Lerp(0.28f, 0.16f, t1);
            for (int s = 0; s < sides; s++)
            {
                float a0 = s * Mathf.Tau / sides, a1 = (s + 1) * Mathf.Tau / sides;
                Vector3 P(float t, float rad, float a) => Spine(t) + new Vector3(Mathf.Cos(a) * rad, 0, Mathf.Sin(a) * rad);
                var (p00, p01, p10, p11) = (P(t0, r0, a0), P(t0, r0, a1), P(t1, r1, a0), P(t1, r1, a1));
                foreach (var p in new[] { p00, p10, p11, p00, p11, p01 }) { st.SetColor(trunk); st.AddVertex(p); }
            }
        }
        var top = Spine(1f);
        const int fronds = 9;
        for (int f = 0; f < fronds; f++)
        {
            float a = f * Mathf.Tau / fronds + 0.3f;
            var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            var side = dir.Cross(Vector3.Up).Normalized();
            // a leaf in four segments, out and up, then drooping
            Vector3 prev = top;
            float prevW = 0.1f;
            for (int s = 1; s <= 4; s++)
            {
                float u = s / 4f;
                var p = top + dir * (4.2f * u) + Vector3.Up * (1.2f * u - 3.4f * u * u);
                float w = 0.9f * Mathf.Sin(u * Mathf.Pi) + 0.05f;
                foreach (var v in new[] { prev - side * prevW, p - side * w, p + side * w, prev - side * prevW, p + side * w, prev + side * prevW })
                {
                    st.SetColor(frond);
                    st.AddVertex(v);
                }
                prev = p;
                prevW = w;
            }
        }
        st.GenerateNormals();
        return st.Commit();
    }

    // ---- the wireframe mountains ----------------------------------------------------------------------

    /// <summary>
    /// A ring of wireframe mountains round the course (#1396): <paramref name="inner"/> to <paramref name="outer"/> metres
    /// out, every triangle unindexed with (1,0,0), (0,1,0), (0,0,1) in its corners' colours for <c>fun_wire</c>. Peaks
    /// up to 1.4 km, lower toward the sun in the west so the retro sun sits clear of them. Hashed, so the same each run.
    /// </summary>
    public static ArrayMesh Mountains(float inner, float outer, int seed)
    {
        const int steps = 120, rows = 7;
        float sunAz = Mathf.Atan2(SunPath.Direction(FunWorld.DuskHour).X, -SunPath.Direction(FunWorld.DuskHour).Z);
        float Height(int s, int r)
        {
            if (r == 0 || r == rows - 1) return 0;
            float a = s * Mathf.Tau / steps;
            float bump = Mathf.Sin(r / (float)(rows - 1) * Mathf.Pi);
            float n = Hash(s * 31 + r * 7 + seed);
            float wave = 0.55f + 0.45f * Mathf.Sin(a * 5f + seed) * Mathf.Cos(a * 3f + 1.3f);
            // low toward the sun: the disc clear above them
            float off = Mathf.Abs(Mathf.Wrap(a - sunAz, -Mathf.Pi, Mathf.Pi));
            float sunCut = Mathf.Lerp(0.12f, 1f, Mathf.SmoothStep(0.15f, 0.6f, off));
            return 1400f * bump * wave * (0.55f + 0.45f * n) * sunCut;
        }
        Vector3 P(int s, int r)
        {
            float a = (s % steps) * Mathf.Tau / steps;
            float rad = Mathf.Lerp(inner, outer, r / (float)(rows - 1)) + (Hash(s * 13 + r) - 0.5f) * 250f;
            // azimuth clockwise from north: +X east, -Z north
            return new Vector3(Mathf.Sin(a) * rad, Height(s % steps, r), -Mathf.Cos(a) * rad);
        }
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        void Tri(Vector3 a, Vector3 b, Vector3 c)
        {
            st.SetColor(new Color(1, 0, 0)); st.AddVertex(a);
            st.SetColor(new Color(0, 1, 0)); st.AddVertex(b);
            st.SetColor(new Color(0, 0, 1)); st.AddVertex(c);
        }
        for (int s = 0; s < steps; s++)
            for (int r = 0; r < rows - 1; r++)
            {
                Tri(P(s, r), P(s + 1, r), P(s + 1, r + 1));
                Tri(P(s, r), P(s + 1, r + 1), P(s, r + 1));
            }
        return st.Commit();
    }

    private static float Hash(int i)
    {
        uint x = (uint)i * 2654435761u;
        x ^= x >> 15;
        x *= 2246822519u;
        x ^= x >> 13;
        return (x & 0xFFFF) / 65535f;
    }

    /// <summary>A slice of the mountain ring at a small scale, for the model viewer.</summary>
    [Showcase("World", "Fun world mountains")]
    public static Node3D MountainsShowcase() => new MeshInstance3D
    {
        Name = "Mountains", Mesh = Mountains(40, 60, 0), Scale = new Vector3(1, 0.04f, 1), MaterialOverride = WireMaterial(),
    };
}
