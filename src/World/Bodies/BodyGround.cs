using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// The ground under your feet on another body (#1850, docs/plans/planets.md sections 4, 5 and 7): a square of
/// <see cref="Cells"/>² cells of <see cref="CellM"/> round the camera, solid and drawn, while the anchor is on the body and
/// the camera within <see cref="NearM"/> of it. Its heights are <see cref="BodyHeights"/>: the pyramid's finest level
/// (the Moon's LOLA, 167 m cells) and the generated craters down to 2.5 m (<see cref="CraterField"/>), the same on every
/// peer.
///
/// <para>
/// <b>Built when it moves, never per frame</b>: on a worker once the camera is <see cref="MoveM"/> from the square's
/// centre, then swapped in whole (the new one first, so the walker never stands on nothing). Placed from doubles at each
/// re-anchor, as the globe's tiles are. The globe's own tiles leave a hole under it (<see cref="Hole"/>): their coarse
/// cells would cut through the finer ground.
/// </para>
/// </summary>
public partial class BodyGround : Node, Terrain.IBodyGround
{
    /// <summary>Cells a side.</summary>
    public const int Cells = 256;

    /// <summary>A cell's side, metres: the smallest crater (2.5 m) is a shape at it.</summary>
    public const float CellM = 2f;

    /// <summary>The square moves when the camera is this far from its centre, metres (a quarter of its side less a margin).</summary>
    public const double MoveM = 96;

    /// <summary>The square is built while the camera is under this, metres over the body's reference surface.</summary>
    public const double NearM = 25_000;

    /// <summary>The last metres before the square's edge, eased into the globe's own surface (no step where they meet).</summary>
    public const double BlendM = 64;

    /// <summary>How far each way from the centre the globe's own tiles step aside, metres: the square's half side.</summary>
    public const float HoleM = Cells * CellM / 2 - 0.25f;

    private static readonly StringName SunDir = "sun_dir", AlbedoTex = "albedo_tex";

    public CelestialBody Body => _globe.Body;

    /// <summary>The ground's heights (the checks read them too).</summary>
    public BodyHeights Heights { get; }

    /// <summary>The square in place, with its frame (x east, y up, z south, its origin on the ground under its centre).</summary>
    public EnuFrame? Frame { get; private set; }

    /// <summary>How many squares were built; a check waits for a new one.</summary>
    public int Built { get; private set; }

    /// <summary>The live one, for the checks.</summary>
    public static BodyGround? Active { get; private set; }

    private readonly BodyGlobe _globe;
    private readonly WorldOrigin _origin;
    private readonly Shader _shader = GD.Load<Shader>("res://shaders/body_globe.gdshader");
    private StaticBody3D? _static;
    private ShaderMaterial? _material;
    private Task<Square>? _task;
    private int _epoch = -1;

    private sealed record Square(EnuFrame Frame, float[] Y, Godot.Collections.Array Arrays, Image? Colour);

    public BodyGround(BodyGlobe globe, WorldOrigin origin)
    {
        _globe = globe;
        _origin = origin;
        Name = $"{globe.Body.Name}Ground";
        var cube = globe.Body.Cube;
        int level = globe.MaxLevel;
        var index = globe.HeightIndex;
        Heights = new BodyHeights(cube, level,
            key => index.TryGetValue(key, out var at) ? CubeTileCodec.Decode(GlobeFiles.ReadBytes(at)) : null,
            globe.Body.CraterDensity > 0 ? new CraterField((ulong)globe.Body.Id, cube.Shape.A, globe.Body.CraterDensity) : null);
    }

    public override void _EnterTree()
    {
        Active = this;
        Terrain.Earth.EarthWorld.OtherBody = this;
    }

    public override void _ExitTree()
    {
        if (Active == this) Active = null;
        if (Terrain.Earth.EarthWorld.OtherBody == this) Terrain.Earth.EarthWorld.OtherBody = null;
    }

    /// <summary>The ground's world Y under a world point the square covers: the ground there, along the anchor's up.</summary>
    public bool TryGetHeight(Vector3 worldPos, out float height)
    {
        height = 0f;
        if (!Covers(worldPos) || _origin.Frame.Axes is not { } axes) return false;
        var n = Heights.NormalUnder(axes.ToEcef(worldPos.X, worldPos.Y, worldPos.Z));
        var (_, y, _) = axes.FromEcef(Heights.Ground(n));
        height = (float)y;
        return true;
    }

    /// <summary>The square is solid wherever it covers.</summary>
    public bool HasCollisionAt(Vector3 worldPos) => Covers(worldPos);

    /// <summary>
    /// Where the globe's own tiles step aside, in world space: the square's centre, its up, its east and its half side
    /// (<see cref="HoleM"/>); null while there is no square.
    /// </summary>
    public (Vector3 Centre, Vector3 Up, Vector3 East, float Half)? Hole =>
        _static is { } s && _origin.Body == Body.Id ? (s.GlobalPosition, s.GlobalBasis.Y.Normalized(), s.GlobalBasis.X.Normalized(), HoleM) : null;

    /// <summary>The square is in place on the anchor's body and a world point stands over it (within <see cref="MoveM"/> of its centre).</summary>
    public bool Covers(Vector3 world)
    {
        if (_static == null || Frame is not { } f || _origin.Body != Body.Id || _origin.Frame.Axes is not { } axes) return false;
        var (x, _, z) = f.ToLocal(axes.ToEcef(world.X, world.Y, world.Z));
        return x * x + z * z < MoveM * MoveM;
    }

    public override void _Process(double delta)
    {
        if (_task is { IsCompleted: true } done)
        {
            _task = null;
            if (done.IsCompletedSuccessfully) Commit(done.Result);
            else GD.PrintErr($"[bodies] {Body.Name}'s ground: {done.Exception?.GetBaseException().Message}");
        }
        if (_origin.Body != Body.Id || _origin.Frame.Axes is not { } axes)
        {
            Drop();
            return;
        }
        if (_static != null && _origin.Epoch != _epoch) Place(axes);
        if (_static != null && _material != null && _globe.Sun != Vector3.Zero) _material.SetShaderParameter(SunDir, _globe.Sun);

        var cam = GetViewport()?.GetCamera3D();
        if (cam == null || _task != null) return;
        var at = cam.GlobalPosition;
        if (_origin.AltitudeOf(at) > NearM) return;
        var p = axes.ToEcef(at.X, at.Y, at.Z);
        if (Frame is { } f)
        {
            var (x, _, z) = f.ToLocal(p);
            if (x * x + z * z < MoveM * MoveM) return;
        }
        var heights = Heights;
        var globe = _globe;
        _task = Task.Run(() => Build(heights, globe, p));
    }

    /// <summary>A square round <paramref name="near"/>: the heights, the mesh and the colour under it; on a worker.</summary>
    private static Square Build(BodyHeights heights, BodyGlobe globe, EcefPos near)
    {
        var (frame, y) = heights.Grid(near, Cells, CellM, BlendM, BodyGlobe.Stride);
        int n = Cells + 1;
        var pos = new Vector3[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                pos[j * n + i] = new Vector3((i - Cells / 2) * CellM, y[j * n + i], (j - Cells / 2) * CellM);

        // the colour: the finest coloured tile over the centre, its UVs from each vertex's place on that tile
        var cube = heights.Cube;
        Image? colour = null;
        CubeKey? colourKey = null;
        var n0 = heights.NormalUnder(frame.Origin);
        for (int level = globe.MaxLevel; level >= 0 && colourKey == null; level--)
        {
            var (face, u, v) = cube.FromNormal(n0);
            var k = CubeKey.FromFace(face, u, v, level);
            if (!globe.AlbedoIndex.TryGetValue(k, out var at)) continue;
            colour = new Image();
            if (colour.LoadWebpFromBuffer(CubeAlbedoCodec.Decode(GlobeFiles.ReadBytes(at)).ForMonth(1)) != Error.Ok) { colour = null; continue; }
            colour.GenerateMipmaps();
            colourKey = k;
        }
        var uvs = new Vector2[n * n];
        if (colourKey is { } ck)
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    var pn = heights.NormalUnder(frame.ToEcef((i - Cells / 2) * CellM, 0, (j - Cells / 2) * CellM));
                    var (u, v) = cube.OnFace(ck.Face, pn);
                    double side = 1 << ck.Level;
                    uvs[j * n + i] = new Vector2((float)((u + 1) * 0.5 * side - ck.X), (float)((v + 1) * 0.5 * side - ck.Y));
                }
        return new Square(frame, y, Mesh(pos, uvs), colour);
    }

    /// <summary>The square's mesh: the grid, its normals, and a skirt 30 m down round it (no gap against the globe's lower cells).</summary>
    private static Godot.Collections.Array Mesh(Vector3[] pos, Vector2[] uv)
    {
        int m = Cells, n = m + 1;
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
            verts[n * n + e] = pos[v] - Vector3.Up * 30f;
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
    private void Commit(Square sq)
    {
        if (_origin.Body != Body.Id || _origin.Frame.Axes is not { } axes) return;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, sq.Arrays);
        var material = new ShaderMaterial { Shader = _shader };
        if (sq.Colour != null) material.SetShaderParameter(AlbedoTex, ImageTexture.CreateFromImage(sq.Colour));
        material.SetShaderParameter(SunDir, _globe.Sun == Vector3.Zero ? Vector3.Up : _globe.Sun);
        int side = Cells + 1;
        var body = new StaticBody3D { Name = "Square" };
        body.AddChild(new CollisionShape3D
        {
            Shape = new HeightMapShape3D { MapWidth = side, MapDepth = side, MapData = sq.Y },
            // a height map's cells are one unit, centred: scaled to the grid, centred as the grid is
            Scale = new Vector3(CellM, 1f, CellM),
        });
        body.AddChild(new MeshInstance3D
        {
            Name = "Mesh", Mesh = mesh, MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        var old = _static;
        _static = body;
        _material = material;
        Frame = sq.Frame;
        AddChild(body);
        Place(axes);
        old?.QueueFree();
        Built++;
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[bodies] {Body.Name}'s ground: square {Built} at {sq.Frame.Origin.Length - Body.Shape.A:F0} m over the reference, {Cells * CellM:F0} m a side"));
    }

    /// <summary>The square from its frame into world space, in doubles from the anchor (once a re-anchor).</summary>
    private void Place(FrameAxes axes)
    {
        if (_static == null || Frame is not { } f) return;
        _epoch = _origin.Epoch;
        Vector3 Turn(EcefPos v) => new((float)FrameAxes.Dot(v, axes.X), (float)FrameAxes.Dot(v, axes.Y), (float)FrameAxes.Dot(v, axes.Z));
        var (ox, oy, oz) = axes.FromEcef(f.Origin);
        var south = new EcefPos(-f.North.X, -f.North.Y, -f.North.Z);
        _static.GlobalTransform = new Transform3D(new Basis(Turn(f.East), Turn(f.Up), Turn(south)), new Vector3((float)ox, (float)oy, (float)oz));
    }

    private void Drop()
    {
        _static?.QueueFree();
        _static = null;
        _material = null;
        Frame = null;
    }
}
