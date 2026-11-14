using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// The ground under your feet on another body (#1850, docs/plans/planets.md sections 4, 5 and 7), while the anchor is on
/// the body and the camera within <see cref="NearM"/> of it. Its heights are <see cref="BodyHeights"/>: the pyramid's
/// finest level (the Moon's LOLA, 167 m cells) and the generated craters down to 2.5 m (<see cref="CraterField"/>), the
/// same on every peer.
///
/// <para>
/// <b>Two squares</b>: the near one (512 m of 2 m cells, every crater, solid: what the walker stands on) inside a medium
/// one (4 km of 16 m cells, the craters from 40 m, drawn only), which reaches a walker's horizon (2.4 km on the Moon,
/// 3.4 on Mars). Each eases at its edge into what lies round it (the near into the medium's ground, the medium into
/// the globe's own cells), and each leaves a hole in what lies under it (the medium under the near one, the globe's
/// tiles under the medium: <see cref="Hole"/>), so no coarser cell cuts through finer ground.
/// </para>
///
/// <para>
/// <b>Built when it moves, never per frame</b>: on a worker once the camera is a square's move distance from its centre,
/// then swapped in whole (the new one first, so the walker never stands on nothing). Placed from doubles at each
/// re-anchor, as the globe's tiles are.
/// </para>
/// </summary>
public partial class BodyGround : Node, Terrain.IBodyGround
{
    /// <summary>The near square's cells a side.</summary>
    public const int Cells = 256;

    /// <summary>A near cell's side, metres: the smallest crater (2.5 m) is a shape at it.</summary>
    public const float CellM = 2f;

    /// <summary>The near square moves when the camera is this far from its centre, metres.</summary>
    public const double MoveM = 96;

    /// <summary>The squares are built while the camera is under this, metres over the body's reference surface.</summary>
    public const double NearM = 25_000;

    /// <summary>The near square's last metres, eased into the medium ground (no step where they meet).</summary>
    public const double BlendM = 64;

    /// <summary>The medium square's cells: 256 of 16 m, 4 km, a walker's horizon and more.</summary>
    public const float MediumCellM = 16f;

    /// <summary>The medium square moves when the camera is this far from its centre, metres.</summary>
    public const double MediumMoveM = 512;

    /// <summary>The medium square's last metres, eased into the globe's own cells.</summary>
    public const double MediumBlendM = 512;

    /// <summary>The medium square holds the craters from this diameter up (two and a half of its cells across), metres.</summary>
    public const double MediumCraterM = 40;

    /// <summary>How far each way from the centre the near square holds the true ground (inside its eased band), metres.</summary>
    public const double TrueHalfM = Cells * CellM / 2 - BlendM;

    private static readonly StringName AirTau = "air_tau";
    private static readonly StringName SunDir = "sun_dir", AlbedoTex = "albedo_tex", SunStrength = "sun_strength",
        HoleCentre = "hole_centre", HoleUp = "hole_up", HoleEast = "hole_east", HoleHalf = "hole_half",
        LampPos = "lamp_pos", LampDir = "lamp_dir", LampCol = "lamp_col", LampCount = "lamp_count";

    // the lamps on the ground (#1942, GroundLamps): one set of arrays, rewritten on a change, a re-anchor or each second
    private static readonly Vector4[] LampPosArr = new Vector4[GroundLamps.Max], LampDirArr = new Vector4[GroundLamps.Max],
        LampColArr = new Vector4[GroundLamps.Max];

    public CelestialBody Body => _globe.Body;

    /// <summary>The ground's heights (the checks read them too).</summary>
    public BodyHeights Heights { get; }

    /// <summary>The near square in place, with its frame (x east, y up, z south, its origin on the ground under its centre).</summary>
    public EnuFrame? Frame => _near.Frame;

    /// <summary>How many near squares were built; a check waits for a new one.</summary>
    public int Built => _near.Built;

    /// <summary>How many medium squares were built.</summary>
    public int MediumBuilt => _medium.Built;

    /// <summary>The anchor body's own (one a landable body with a pyramid; the anchor's claims it), for the checks and the step-in.</summary>
    public static BodyGround? Active { get; private set; }

    private readonly BodyGlobe _globe;
    private readonly WorldOrigin _origin;
    private readonly Shader _shader = GD.Load<Shader>("res://shaders/body_globe.gdshader");
    private readonly Layer _near, _medium;

    /// <summary>One square: its size, how it moves and eases, and what is in place.</summary>
    private sealed class Layer(string name, int cells, float cellM, double moveM, double blendM, float skirtM, bool solid, double smallestCrater)
    {
        public readonly string Name = name;
        public readonly int Cells = cells;
        public readonly float CellM = cellM, SkirtM = skirtM;
        public readonly double MoveM = moveM, BlendM = blendM, SmallestCrater = smallestCrater;
        public readonly bool Solid = solid;
        public StaticBody3D? Node;
        public ShaderMaterial? Material;
        public float ExposureWritten;
        public int LampsVersion = -1, LampsEpoch = -1;
        public double LampsAge;
        public EnuFrame? Frame;
        public Task<Square>? Task;
        public int Epoch = -1, Built;
        public (Vector3 Centre, Vector3 Up, Vector3 East, float Half) HoleWritten;

        public float Half => Cells * CellM / 2;
    }

    private sealed record Square(EnuFrame Frame, float[] Y, Godot.Collections.Array Arrays, Image? Colour);

    public BodyGround(BodyGlobe globe, WorldOrigin origin)
    {
        _globe = globe;
        _origin = origin;
        Name = $"{globe.Body.Name}Ground";
        Heights = HeightsOf(globe.Body, globe.Pyramid, globe.MaxLevel);
        _near = new Layer("Square", Cells, CellM, MoveM, BlendM, 30f, solid: true, smallestCrater: 0);
        _medium = new Layer("Medium", Cells, MediumCellM, MediumMoveM, MediumBlendM, 200f, solid: false, smallestCrater: MediumCraterM);
    }

    /// <summary>
    /// A body's heights, the same on every peer and on the server (#1852, <see cref="ServerBodyGround"/>): its pyramid's
    /// finest level (null where it has none: the generated relief alone, on its datum), its craters, the Moon's sites and
    /// colonies, its relief family.
    /// </summary>
    public static BodyHeights HeightsOf(CelestialBody body, Terrain.Leaves.PyramidSource? pyramid, int level)
    {
        var cube = body.Cube;
        return new BodyHeights(cube, level,
            key => pyramid?.Heights(key),
            body.CraterDensity > 0 ? new CraterField((ulong)body.Id, cube.Shape.A, body.CraterDensity) : null,
            // the Moon's sites (#1939): craters calmed under the landers, the S-IVBs' fresh craters; the colonies' plateaus (#1940)
            body.Id == BodyId.Moon ? new SurfaceStamps(MoonSites.Stamps().Concat(MoonColonies.Stamps()), cube.Shape.A) : null,
            // the body's relief family under its data (#1853, #1854): dunes, ridges, grooves, lava flows
            SurfaceProfile.For(body.Id, cube.Shape.A));
    }

    /// <summary>The near square's heights round <paramref name="near"/> (on a worker): what the walker stands on, eased into the medium ground.</summary>
    public static (EnuFrame Frame, float[] Y) NearGrid(BodyHeights heights, EcefPos near) =>
        heights.Grid(near, Cells, CellM, BlendM, n => heights.At(n, MediumCraterM), 0);

    /// <summary>This body's ground is the one everything asks while the anchor is on it.</summary>
    private void Claim()
    {
        if (Active == this) return;
        Active = this;
        Terrain.Earth.EarthWorld.OtherBody = this;
    }

    public override void _ExitTree()
    {
        if (Active == this) Active = null;
        if (Terrain.Earth.EarthWorld.OtherBody == this) Terrain.Earth.EarthWorld.OtherBody = null;
    }

    /// <summary>The ground's world Y under a world point the near square covers: the ground there, along the anchor's up.</summary>
    public bool TryGetHeight(Vector3 worldPos, out float height)
    {
        height = 0f;
        if (!Covers(worldPos) || _origin.Frame.Axes is not { } axes) return false;
        var n = Heights.NormalUnder(axes.ToEcef(worldPos.X, worldPos.Y, worldPos.Z));
        var (_, y, _) = axes.FromEcef(Heights.Ground(n));
        height = (float)y;
        return true;
    }

    /// <summary>The near square is solid wherever it covers.</summary>
    public bool HasCollisionAt(Vector3 worldPos) => Covers(worldPos);

    /// <summary>
    /// Where the globe's own tiles step aside, in world space: the outer square in place (the medium, or the near one
    /// before it), its centre, up, east and half side; null while there is none.
    /// </summary>
    public (Vector3 Centre, Vector3 Up, Vector3 East, float Half)? Hole =>
        _origin.Body != Body.Id ? null : HoleOf(_medium) ?? HoleOf(_near);

    private static (Vector3 Centre, Vector3 Up, Vector3 East, float Half)? HoleOf(Layer l) =>
        l.Node is { } s ? (s.GlobalPosition, s.GlobalBasis.Y.Normalized(), s.GlobalBasis.X.Normalized(), l.Half - 0.25f) : null;

    /// <summary>
    /// The near square is in place on the anchor's body and a world point stands over its true ground (within
    /// <see cref="TrueHalfM"/> each way of its centre, twice <see cref="MoveM"/>: the next square is there before the
    /// walker is past it).
    /// </summary>
    public bool Covers(Vector3 world)
    {
        if (_near.Node == null || _near.Frame is not { } f || _origin.Body != Body.Id || _origin.Frame.Axes is not { } axes) return false;
        var (x, _, z) = f.ToLocal(axes.ToEcef(world.X, world.Y, world.Z));
        return Math.Abs(x) < TrueHalfM && Math.Abs(z) < TrueHalfM;
    }

    public override void _Process(double delta)
    {
        Finish(_near);
        Finish(_medium);
        if (_origin.Body != Body.Id || _origin.Frame.Axes is not { } axes)
        {
            Drop(_near);
            Drop(_medium);
            return;
        }
        Claim();
        Keep(_near, axes);
        Keep(_medium, axes);
        // the medium square steps aside under the near one: its hole follows the near square (moved, or shifted)
        if (_medium.Material is { } medium)
        {
            var hole = HoleOf(_near) ?? default;
            if (hole != _medium.HoleWritten)
            {
                _medium.HoleWritten = hole;
                medium.SetShaderParameter(HoleCentre, hole.Centre);
                medium.SetShaderParameter(HoleUp, hole.Up);
                medium.SetShaderParameter(HoleEast, hole.East);
                medium.SetShaderParameter(HoleHalf, hole.Half);
            }
        }

        var cam = GetViewport()?.GetCamera3D();
        if (cam == null) return;
        var at = cam.GlobalPosition;
        if (_origin.AltitudeOf(at) > NearM) return;
        var p = axes.ToEcef(at.X, at.Y, at.Z);
        var heights = Heights;
        var globe = _globe;
        // the near square first (what the walker stands on), its edge on the medium's ground; the medium once the near
        // one is in, its edge on the globe's own cells
        if (Wants(_near, p))
        {
            var near = _near;
            near.Task = Task.Run(() => Build(heights, globe, p, near, null));
        }
        else if (_near.Node != null && Wants(_medium, p))
        {
            var mediumLayer = _medium;
            mediumLayer.Task = Task.Run(() => Build(heights, globe, p, mediumLayer, n => heights.Coarse(n, BodyGlobe.Stride)));
        }
    }

    /// <summary>A square in place: placed again at a re-anchor, lit by the body's sun.</summary>
    private void Keep(Layer l, FrameAxes axes)
    {
        if (l.Node != null && _origin.Epoch != l.Epoch) Place(l, axes);
        if (l.Material != null && _globe.Sun != Vector3.Zero) l.Material.SetShaderParameter(SunDir, _globe.Sun);
        // the globe's exposure comes once its six faces' colour is in: a square made before kept the shader's default
        // (Phobos' near square at 1.6 in a globe at 0.9, a bright patch, #1853)
        if (l.Material != null && l.ExposureWritten != _globe.Exposure)
            l.Material.SetShaderParameter(SunStrength, l.ExposureWritten = _globe.Exposure);
        if (l.Material != null) WriteLamps(l);
    }

    /// <summary>The nearest lamps into a square's shader (#1942): on a change of the set, a re-anchor, or once a second.</summary>
    private void WriteLamps(Layer l)
    {
        l.LampsAge += GetProcessDeltaTime();
        if (l.LampsVersion == GroundLamps.Version && l.LampsEpoch == _origin.Epoch && (l.LampsAge < 1 || GroundLamps.All.Count == 0)) return;
        l.LampsVersion = GroundLamps.Version;
        l.LampsEpoch = _origin.Epoch;
        l.LampsAge = 0;
        var eye = GetViewport()?.GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
        int n = GroundLamps.Fill(eye, LampPosArr, LampDirArr, LampColArr);
        l.Material!.SetShaderParameter(LampCount, n);
        if (n == 0) return;
        l.Material.SetShaderParameter(LampPos, LampPosArr);
        l.Material.SetShaderParameter(LampDir, LampDirArr);
        l.Material.SetShaderParameter(LampCol, LampColArr);
    }

    /// <summary>A layer needs a new square round <paramref name="p"/>: none yet, or the camera past its move distance.</summary>
    private static bool Wants(Layer l, EcefPos p)
    {
        if (l.Task != null) return false;
        if (l.Frame is not { } f) return true;
        var (x, _, z) = f.ToLocal(p);
        return x * x + z * z >= l.MoveM * l.MoveM;
    }

    private void Finish(Layer l)
    {
        if (l.Task is not { IsCompleted: true } done) return;
        l.Task = null;
        if (done.IsCompletedSuccessfully) Commit(l, done.Result);
        else GD.PrintErr($"[bodies] {Body.Name}'s {l.Name.ToLowerInvariant()} ground: {done.Exception?.GetBaseException().Message}");
    }

    /// <summary>A square round <paramref name="near"/>: the heights, the mesh and the colour under it; on a worker.</summary>
    private static Square Build(BodyHeights heights, BodyGlobe globe, EcefPos near, Layer l, Func<EcefPos, double>? edge)
    {
        int cells = l.Cells;
        float cellM = l.CellM;
        // the near square is the server's too (NearGrid): one call, so both stand on the same ground
        var (frame, y) = edge == null ? NearGrid(heights, near) : heights.Grid(near, cells, cellM, l.BlendM, edge, l.SmallestCrater);
        int n = cells + 1;
        var pos = new Vector3[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                pos[j * n + i] = new Vector3((i - cells / 2) * cellM, y[j * n + i], (j - cells / 2) * cellM);

        // the colour: the finest coloured tile over the centre that holds the whole square (on a moon of a few km, #1853,
        // the finest tile is smaller than the 4 km square, whose edges read past it), its UVs from each vertex's place on
        // that tile; where none holds it (across a face's edge), the finest over the centre
        var cube = heights.Cube;
        Image? colour = null;
        CubeKey? colourKey = null;
        var n0 = heights.NormalUnder(frame.Origin);
        double half = cells / 2 * cellM;
        var corners = new EcefPos[4];
        for (int c = 0; c < 4; c++) corners[c] = heights.NormalUnder(frame.ToEcef((c & 1) == 0 ? -half : half, 0, (c & 2) == 0 ? -half : half));
        bool Holds(CubeKey k)
        {
            double side = 1 << k.Level;
            foreach (var pc in corners)
            {
                var (fu, fv) = cube.OnFace(k.Face, pc);
                double tu = (fu + 1) * 0.5 * side - k.X, tv = (fv + 1) * 0.5 * side - k.Y;
                if (!(tu >= 0 && tu <= 1 && tv >= 0 && tv <= 1)) return false;
            }
            return true;
        }
        for (int pass = 0; pass < 2 && colourKey == null; pass++)
        for (int level = globe.MaxLevel; level >= 0 && colourKey == null; level--)
        {
            var (face, u, v) = cube.FromNormal(n0);
            var k = CubeKey.FromFace(face, u, v, level);
            if (pass == 0 && !Holds(k)) continue;
            if (!globe.Pyramid.HasColour(k) || globe.Pyramid.Colour(k, 1) is not { } webp) continue;
            colour = new Image();
            if (colour.LoadWebpFromBuffer(webp) != Error.Ok) { colour = null; continue; }
            colour.GenerateMipmaps();
            colourKey = k;
        }
        var uvs = new Vector2[n * n];
        if (colourKey is { } ck)
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    var pn = heights.NormalUnder(frame.ToEcef((i - cells / 2) * cellM, 0, (j - cells / 2) * cellM));
                    var (u, v) = cube.OnFace(ck.Face, pn);
                    double side = 1 << ck.Level;
                    uvs[j * n + i] = new Vector2((float)((u + 1) * 0.5 * side - ck.X), (float)((v + 1) * 0.5 * side - ck.Y));
                }
        return new Square(frame, y, Mesh(pos, uvs, cells, l.SkirtM), colour);
    }

    /// <summary>A square's mesh: the grid, its normals, and a skirt round it (no gap against lower ground round it).</summary>
    private static Godot.Collections.Array Mesh(Vector3[] pos, Vector2[] uv, int m, float skirtM)
    {
        int n = m + 1;
        var normals = new Vector3[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                var dx = pos[j * n + System.Math.Min(i + 1, m)] - pos[j * n + System.Math.Max(i - 1, 0)];
                var dz = pos[System.Math.Min(j + 1, m) * n + i] - pos[System.Math.Max(j - 1, 0) * n + i];
                var nn = dz.Cross(dx);
                normals[j * n + i] = nn.LengthSquared() > 0 ? nn.Normalized() : Vector3.Up;
                if (normals[j * n + i].Y < 0) normals[j * n + i] = -normals[j * n + i];
            }
        var edge = new System.Collections.Generic.List<int>(4 * m);
        for (int i = 0; i < m; i++) edge.Add(i);
        for (int j = 0; j < m; j++) edge.Add(j * n + m);
        for (int i = m; i > 0; i--) edge.Add(m * n + i);
        for (int j = m; j > 0; j--) edge.Add(j * n);
        int total = n * n + edge.Count;
        var verts = new Vector3[total];
        var norms = new Vector3[total];
        var uvs = new Vector2[total];
        System.Array.Copy(pos, verts, n * n);
        System.Array.Copy(normals, norms, n * n);
        System.Array.Copy(uv, uvs, n * n);
        for (int e = 0; e < edge.Count; e++)
        {
            int v = edge[e];
            verts[n * n + e] = pos[v] - Vector3.Up * skirtM;
            norms[n * n + e] = normals[v];
            uvs[n * n + e] = uv[v];
        }
        var idx = new int[m * m * 6 + edge.Count * 6];
        int w = 0;
        for (int j = 0; j < m; j++)
            for (int i = 0; i < m; i++)
            {
                int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                idx[w++] = a; idx[w++] = c; idx[w++] = b;
                idx[w++] = b; idx[w++] = c; idx[w++] = d;
            }
        for (int e = 0; e < edge.Count; e++)
        {
            int top0 = edge[e], top1 = edge[(e + 1) % edge.Count];
            int bot0 = n * n + e, bot1 = n * n + (e + 1) % edge.Count;
            idx[w++] = top0; idx[w++] = bot0; idx[w++] = top1;
            idx[w++] = top1; idx[w++] = bot0; idx[w++] = bot1;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Godot.Mesh.ArrayType.Normal] = norms;
        arrays[(int)Godot.Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Godot.Mesh.ArrayType.Index] = idx;
        return arrays;
    }

    /// <summary>Main thread: the new square in, then the old one out.</summary>
    private void Commit(Layer l, Square sq)
    {
        if (_origin.Body != Body.Id || _origin.Frame.Axes is not { } axes) return;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, sq.Arrays);
        var material = new ShaderMaterial { Shader = _shader };
        if (sq.Colour != null) material.SetShaderParameter(AlbedoTex, ImageTexture.CreateFromImage(sq.Colour));
        material.SetShaderParameter(SunDir, _globe.Sun == Vector3.Zero ? Vector3.Up : _globe.Sun);
        material.SetShaderParameter(SunStrength, _globe.Exposure);
        material.SetShaderParameter(AirTau, BodyGlobe.AirTauOf(_globe.Body));
        var body = new StaticBody3D { Name = l.Name };
        if (l.Solid)
        {
            int side = l.Cells + 1;
            body.AddChild(new CollisionShape3D
            {
                Shape = new HeightMapShape3D { MapWidth = side, MapDepth = side, MapData = sq.Y },
                // a height map's cells are one unit, centred: scaled to the grid, centred as the grid is
                Scale = new Vector3(l.CellM, 1f, l.CellM),
            });
        }
        else
        {
            // drawn only: nothing collides with it (the near square is the solid ground)
            body.CollisionLayer = 0;
            body.CollisionMask = 0;
        }
        body.AddChild(new MeshInstance3D
        {
            Name = "Mesh", Mesh = mesh, MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // its box is round its centre: ground all round a camera inside it is never culled
            ExtraCullMargin = l.Half,
        });
        var old = l.Node;
        l.Node = body;
        l.Material = material;
        l.Frame = sq.Frame;
        l.HoleWritten = default;
        AddChild(body);
        Place(l, axes);
        old?.QueueFree();
        l.Built++;
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[bodies] {Body.Name}'s {l.Name.ToLowerInvariant()} ground: {l.Built} at {Body.Shape.ToGeodetic(sq.Frame.Origin).H:F0} m over the reference, {l.Cells * l.CellM:F0} m a side"));
    }

    /// <summary>A square from its frame into world space, in doubles from the anchor (once a re-anchor).</summary>
    private void Place(Layer l, FrameAxes axes)
    {
        if (l.Node == null || l.Frame is not { } f) return;
        l.Epoch = _origin.Epoch;
        Vector3 Turn(EcefPos v) => new((float)FrameAxes.Dot(v, axes.X), (float)FrameAxes.Dot(v, axes.Y), (float)FrameAxes.Dot(v, axes.Z));
        var (ox, oy, oz) = axes.FromEcef(f.Origin);
        var south = new EcefPos(-f.North.X, -f.North.Y, -f.North.Z);
        l.Node.GlobalTransform = new Transform3D(new Basis(Turn(f.East), Turn(f.Up), Turn(south)), new Vector3((float)ox, (float)oy, (float)oz));
    }

    private static void Drop(Layer l)
    {
        l.Node?.QueueFree();
        l.Node = null;
        l.Material = null;
        l.Frame = null;
    }
}
