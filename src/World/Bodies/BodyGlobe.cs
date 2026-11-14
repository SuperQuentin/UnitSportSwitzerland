using System.Collections.Concurrent;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// A body's globe (#1848, docs/plans/planets.md section 6): its pyramid's tiles (heights and colour, G1's format, built
/// by <c>--cube-body</c> or <c>--cube-global</c>) drawn on its own cube (<see cref="CubeShape"/>) wherever the body is
/// from the anchor, the Moon from lunar orbit and the Earth from the Moon alike: the same renderer for every body.
///
/// <para>
/// <b>Placed through the frame tree</b> (<see cref="Frames"/>, at <see cref="SkyClock.Now"/>):
/// <list type="bullet">
/// <item>on the anchor's own body each tile is placed in doubles from the anchor (as <see cref="Globe"/> places the
/// Earth's), once a shift: the ground under the camera holds to the millimetre;</item>
/// <item>another body hangs under one root node whose transform is that body's frame in world space, worked out every
/// frame (it turns and moves against the anchor's), its tiles placed from its centre (0.1 m at the Moon's radius:
/// nothing from where it is seen); past the far plane the root also shrinks it about the camera, the same angular
/// size and the same occlusion (scaled space).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Levels by size on screen</b>, as the Earth's globe: a tile splits into its four when one of its drawn cells would
/// cover more than <see cref="SplitPixels"/> pixels, once all four are loaded; a tile turned away past the body's
/// horizon is not drawn. Meshes are built once on workers (<c>no-mesh-rebuild-per-frame</c>), 65² vertices a tile with
/// skirts against the cracks between levels; a move only rewrites transforms.
/// </para>
///
/// <para>
/// <b>Lit by its own sun</b> (<c>body_globe.gdshader</c>): the direction from the body to the Sun, so the Moon's
/// terminator stands where the ephemeris has it, with a floor on the night side (earthshine, starlight).
/// </para>
/// </summary>
public partial class BodyGlobe : Node
{
    /// <summary>Every 8th height of a tile's 513²: 64 cells a side.</summary>
    public const int Stride = 8;

    /// <summary>A tile splits when one of its drawn cells would cover more than this many pixels.</summary>
    public const float SplitPixels = 6;

    /// <summary>Tiles loaded at once.</summary>
    private const int Workers = 2;

    public CelestialBody Body { get; }
    public string Dir { get; }

    /// <summary>Drawn this frame (the sky's own picture of the body steps aside, <see cref="MoonDisc"/>).</summary>
    public bool Drawn { get; private set; }

    /// <summary>Whether to draw it at all this frame: the owner's call (<see cref="BodyViews"/>).</summary>
    public bool Wanted { get; set; }

    /// <summary>The direction to the Sun from the body, world axes, last frame (the shots' log).</summary>
    public Vector3 Sun => _sun;

    /// <summary>Tiles drawn last frame (the shots' log).</summary>
    public int DrawnTiles { get; private set; }

    /// <summary>The pyramid's finest level.</summary>
    public int MaxLevel => _maxLevel;

    /// <summary>The body's pyramid (#1656's chain; any thread may read it).</summary>
    public Terrain.Leaves.PyramidSource Pyramid => _pyramid;

    /// <summary>The near ground on this body (#1850): its own tiles step aside under it.</summary>
    public BodyGround? Ground { get; set; }

    private readonly WorldOrigin _origin;
    // the body's pyramid (#1656's chain): its folder, or for the Earth the game's own (local, the server's, the shipped base)
    private readonly Terrain.Leaves.PyramidSource _pyramid;
    private readonly int _maxLevel;
    private readonly Node3D _root = new() { Name = "Frame" };
    private readonly Dictionary<CubeKey, Tile> _tiles = new();
    private readonly HashSet<CubeKey> _loading = new();
    private readonly ConcurrentQueue<(CubeKey Key, Godot.Collections.Array? Arrays, Image? Colour, float Mean)> _done = new();

    /// <summary>
    /// What ground square to the sun comes out at on average (#1853), after the shader's Cartoon lift (the photo to the
    /// power 0.8): every body's photo is exposed to it, whatever its source's own scale. Six-face means of the level-0
    /// colour: the Moon's CGI Moon Kit 0.567 (exposed 0.94), Mars' Viking mosaic 0.332 (1.45), the Earth's Blue Marble
    /// 0.172 (2.2, mostly ocean, at the cap).
    /// </summary>
    public const float LitMean = 0.6f;

    /// <summary>
    /// The sun's strength on this body's photo (<c>sun_strength</c>), from its level-0 colour's mean brightness over all
    /// six faces (<see cref="LitMean"/> over that mean lifted as the shader lifts it, 0.7 to 2.2); the shader's 1.6 until
    /// the six have come.
    /// </summary>
    public float Exposure { get; private set; } = 1.6f;

    private bool _exposed;
    private readonly Dictionary<int, float> _faceMeans = new();
    private readonly HashSet<CubeKey> _show = new();
    private readonly Shader _shader = GD.Load<Shader>("res://shaders/body_globe.gdshader");
    private readonly int _month;
    private int _epoch = int.MinValue, _running;
    private BodyId _placedIn = (BodyId)255;
    private Vector3 _sun = Vector3.Zero;

    private sealed class Tile(MeshInstance3D node, ShaderMaterial material)
    {
        public readonly MeshInstance3D Node = node;
        public readonly ShaderMaterial Material = material;
    }

    private static readonly StringName SunDir = "sun_dir", AlbedoTex = "albedo_tex",
        SunStrength = "sun_strength", HoleCentre = "hole_centre", HoleUp = "hole_up", HoleEast = "hole_east", HoleHalf = "hole_half";

    /// <summary>The hole every tile has now (half side 0: none), set on a tile only when it changes.</summary>
    private (Vector3 Centre, Vector3 Up, Vector3 East, float Half) _hole;

    public BodyGlobe(CelestialBody body, string dir, WorldOrigin origin)
        : this(body, new Terrain.Leaves.PyramidSource([new GlobeFiles(dir).Layer(dir, local: true)]), dir, origin)
    {
    }

    /// <summary>A body's globe on a pyramid chain (#1656: the Earth's is <see cref="GlobeData.Earth"/>).</summary>
    public BodyGlobe(CelestialBody body, Terrain.Leaves.PyramidSource pyramid, string describe, WorldOrigin origin)
    {
        Body = body;
        Dir = describe;
        _origin = origin;
        Name = $"{body.Name}Globe";
        _pyramid = pyramid;
        _maxLevel = Math.Max(pyramid.MaxLevel, 0);
        _month = Calendar.Month;
        AddChild(_root);
        GD.Print($"[bodies] {body.Name}'s globe: {pyramid.Count} tiles to level {_maxLevel}, from {describe}");
    }

    public override void _Process(double delta)
    {
        Commit();
        var cam = GetViewport()?.GetCamera3D();
        if (!Wanted || cam == null || _origin.Frame.Axes is not { } axes)
        {
            if (Drawn) Hide();
            return;
        }
        var sky = SkyClock.Now;
        bool own = axes.Body == Body.Id;
        if (axes.Body != _placedIn || (own && _origin.Epoch != _epoch)) Replace(axes, own);

        // the body's frame in world space (doubles): a point p of the body is W (R p + c - O) in world
        var w = Rows(axes);
        var r = own ? DMat3.Identity : Columns(Body.Id, axes.Body, sky);
        var c = own ? DVec3.Zero : Frames.Convert(Body.Id, DVec3.Zero, axes.Body, sky);
        var o = new DVec3(axes.Origin.X, axes.Origin.Y, axes.Origin.Z);
        var camWorld = cam.GlobalPosition;
        var centreWorld = w.ApplyInverse(c - o);
        if (!own)
        {
            // basis W R, origin W (c - O); shrunk about the camera past the far plane
            var basis = BasisOf(w, r);
            var origin = ToVector(centreWorld);
            double reach = (ToD(origin) - ToD(camWorld)).Length + Body.Shape.A;
            float s = reach > cam.Far * 0.9 ? (float)(cam.Far * 0.85 / reach) : 1f;
            _root.Transform = new Transform3D(basis.Scaled(Vector3.One * s), camWorld + (origin - camWorld) * s);
        }

        // the sun from the body, into world axes
        var sunIcrf = Frames.Offset(Body.Id, BodyId.Sun, sky.Utc);
        var sunAnchor = Frames.Orientation(axes.Body, sky).ApplyInverse(sunIcrf);
        var sun = ToVector(w.ApplyInverse(sunAnchor).Normalized());
        bool sunMoved = sun.DistanceSquaredTo(_sun) > 1e-8f;
        _sun = sun;

        // the eye in the body's own frame, for the levels
        var eyeAnchor = new DVec3(0, 0, 0);
        {
            var (ex, ey, ez) = (camWorld.X, camWorld.Y, camWorld.Z);
            var e = axes.ToEcef(ex, ey, ez);
            eyeAnchor = new DVec3(e.X, e.Y, e.Z);
        }
        var eye = own ? eyeAnchor : Frames.Convert(axes.Body, eyeAnchor, Body.Id, sky);
        float fov = Mathf.DegToRad(CameraLens.FovOf(cam));
        double focal = GetViewport().GetVisibleRect().Size.Y / (2 * Math.Tan(fov / 2));
        _show.Clear();
        for (int f = 0; f < CubeSphere.Faces; f++) Select(new CubeKey(f, 0, 0, 0), eye, focal);

        // the near ground's hole (own body only): moved with the square and at each re-anchor, so rarely
        var hole = own && Ground?.Hole is { } h ? h : default;
        bool holeMoved = hole != _hole;
        _hole = hole;

        int shown = 0;
        foreach (var (key, tile) in _tiles)
        {
            bool on = _show.Contains(key);
            if (tile.Node.Visible != on) tile.Node.Visible = on;
            if (on)
            {
                shown++;
                if (sunMoved) tile.Material.SetShaderParameter(SunDir, sun);
            }
            if (holeMoved) SetHole(tile.Material);
        }
        DrawnTiles = shown;
        Drawn = shown > 0;
    }

    /// <summary>A colour image's mean brightness (Rec. 709 luma of a 32 × 32 sample), 0 to 1.</summary>
    private static float MeanBrightness(Image image)
    {
        int w = image.GetWidth(), h = image.GetHeight();
        double sum = 0;
        for (int j = 0; j < 32; j++)
            for (int i = 0; i < 32; i++)
            {
                var c = image.GetPixel((int)((i + 0.5) * w / 32), (int)((j + 0.5) * h / 32));
                sum += 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
            }
        return (float)(sum / 1024);
    }

    private void SetHole(ShaderMaterial m)
    {
        m.SetShaderParameter(HoleCentre, _hole.Centre);
        m.SetShaderParameter(HoleUp, _hole.Up);
        m.SetShaderParameter(HoleEast, _hole.East);
        m.SetShaderParameter(HoleHalf, _hole.Half);
    }

    private void Hide()
    {
        foreach (var t in _tiles.Values) t.Node.Visible = false;
        Drawn = false;
        DrawnTiles = 0;
    }

    /// <summary>The tiles to draw: a tile splits when its drawn cells would be big on screen and its four are loaded.</summary>
    private void Select(CubeKey key, DVec3 eye, double focal)
    {
        if (!_pyramid.Has(key)) return;
        var cube = Body.Cube;
        var centre = (DVec3)cube.ToEcef(key.Face, CubeSphere.Lattice(key.Level + 1, 2L * key.X + 1), CubeSphere.Lattice(key.Level + 1, 2L * key.Y + 1), 0);
        double edge = Body.Shape.A * Math.PI / 2 / key.Side;
        var toEye = eye - centre;
        double distance = Math.Max(toEye.Length - edge, 1);
        // turned away past the body's horizon: a tile whose up looks away from the eye by more than its own size
        if (key.Level > 0 && centre.Normalized().Dot(toEye.Normalized()) < -0.15 - edge / Body.Shape.A) return;
        if (!_tiles.ContainsKey(key)) { Request(key); if (key.Level > 0) return; }
        double cell = edge / (512.0 / Stride);
        bool split = key.Level < _maxLevel && cell / distance * focal > SplitPixels;
        if (split)
        {
            bool all = true;
            foreach (var child in key.Children())
                if (!_tiles.ContainsKey(child)) { all = false; if (_pyramid.Has(child)) Request(child); }
            if (all)
            {
                foreach (var child in key.Children()) Select(child, eye, focal);
                return;
            }
        }
        if (_tiles.ContainsKey(key)) _show.Add(key);
    }

    private void Request(CubeKey key)
    {
        if (_running >= Workers || !_loading.Add(key)) return;
        _running++;
        bool coloured = _pyramid.HasColour(key);
        var cube = Body.Cube;
        int month = _month;
        Task.Run(() =>
        {
            Godot.Collections.Array? arrays = null;
            Image? colour = null;
            float mean = float.NaN;
            try
            {
                // a streamed tile not come yet: none this time, asked again
                if (_pyramid.Heights(key) is { } tile) arrays = Build(tile, cube);
                if (arrays != null && coloured && _pyramid.Colour(key, month) is { } webp)
                {
                    colour = new Image();
                    if (colour.LoadWebpFromBuffer(webp) != Error.Ok) colour = null;
                    else
                    {
                        if (key.Level == 0) mean = MeanBrightness(colour);
                        colour.GenerateMipmaps();
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[bodies] {key}: {ex.Message}");
            }
            _done.Enqueue((key, arrays, colour, mean));
        });
    }

    /// <summary>Main thread: the tiles the workers made, a few a frame.</summary>
    private void Commit()
    {
        for (int n = 0; n < 8 && _done.TryDequeue(out var r); n++)
        {
            _running--;
            _loading.Remove(r.Key);
            if (r.Arrays == null) continue;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, r.Arrays);
            var material = new ShaderMaterial { Shader = _shader };
            if (r.Colour != null) material.SetShaderParameter(AlbedoTex, ImageTexture.CreateFromImage(r.Colour));
            material.SetShaderParameter(SunDir, _sun == Vector3.Zero ? Vector3.Up : _sun);
            SetHole(material);
            if (float.IsFinite(r.Mean)) _faceMeans[r.Key.Face] = r.Mean;
            if (!_exposed && _faceMeans.Count == CubeSphere.Faces)
            {
                _exposed = true;
                float mean = _faceMeans.Values.Sum() / CubeSphere.Faces;
                Exposure = Mathf.Clamp(LitMean / Mathf.Pow(Mathf.Max(mean, 0.01f), 0.8f), 0.7f, 2.2f);
                foreach (var t in _tiles.Values) t.Material.SetShaderParameter(SunStrength, Exposure);
                GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"[bodies] {Body.Name}'s photo: mean {mean:F3} over its six faces, exposed at {Exposure:F2}"));
            }
            material.SetShaderParameter(SunStrength, Exposure);
            var node = new MeshInstance3D
            {
                Name = r.Key.FileStem, Mesh = mesh, MaterialOverride = material, Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                // a tile's own box is near its centre; the scaled root and the frame tree's turn carry it far: never culled by Godot's guess
                ExtraCullMargin = 16384,
            };
            _tiles[r.Key] = new Tile(node, material);
            Place(r.Key, node);
        }
    }

    /// <summary>Every tile moved to the anchor's way of placing them (its own body, or another's under the root).</summary>
    private void Replace(FrameAxes axes, bool own)
    {
        _placedIn = axes.Body;
        _epoch = _origin.Epoch;
        if (own) _root.Transform = Transform3D.Identity;
        foreach (var (key, tile) in _tiles) Place(key, tile.Node);
    }

    /// <summary>A tile's node: from the anchor in doubles on its own body, from the body's centre under the root elsewhere.</summary>
    private void Place(CubeKey key, MeshInstance3D node)
    {
        var a = key.AnchorOn(Body.Cube);
        if (node.GetParent() == null) _root.AddChild(node);
        if (_origin.Frame.Axes is { } axes && axes.Body == Body.Id)
        {
            Vector3 Turn(EcefPos v) => new((float)FrameAxes.Dot(v, axes.X), (float)FrameAxes.Dot(v, axes.Y), (float)FrameAxes.Dot(v, axes.Z));
            var (ox, oy, oz) = axes.FromEcef(a.Origin);
            var south = new EcefPos(-a.North.X, -a.North.Y, -a.North.Z);
            node.Transform = new Transform3D(new Basis(Turn(a.East), Turn(a.Up), Turn(south)), new Vector3((float)ox, (float)oy, (float)oz));
        }
        else
        {
            Vector3 V(EcefPos v) => new((float)v.X, (float)v.Y, (float)v.Z);
            var south = new EcefPos(-a.North.X, -a.North.Y, -a.North.Z);
            node.Transform = new Transform3D(new Basis(V(a.East), V(a.Up), V(south)), V(a.Origin));
        }
    }

    /// <summary>
    /// A tile's mesh in its anchor's east-up-south (floats round its centre): every <see cref="Stride"/>th height, the
    /// normals from the grid, the colour's UVs (row 0 the south edge, as the colour is baked) and skirts a cell deep.
    /// </summary>
    public static Godot.Collections.Array Build(CubeTile tile, CubeShape cube)
    {
        var key = tile.Key;
        // 64 cells whatever the lattice (#1656: the Earth's shipped base holds 65² tiles)
        int bits = tile.Bits, stride = Globe.StrideOf(bits), m = (1 << bits) / stride, n = m + 1;
        var anchor = key.AnchorOn(cube);
        var pos = new Vector3[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float h = tile.HeightAt(i * stride, j * stride);
                if (!float.IsFinite(h)) h = 0;
                var p = key.VertexOn(cube, bits, i * stride, j * stride, h);
                var (x, y, z) = anchor.ToLocal(p);
                pos[j * n + i] = new Vector3((float)x, (float)y, (float)z);
            }
        var normals = new Vector3[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                var dx = pos[j * n + Math.Min(i + 1, m)] - pos[j * n + Math.Max(i - 1, 0)];
                var dy = pos[Math.Min(j + 1, m) * n + i] - pos[Math.Max(j - 1, 0) * n + i];
                var nn = dy.Cross(dx);
                normals[j * n + i] = nn.LengthSquared() > 0 ? nn.Normalized() : Vector3.Up;
                if (normals[j * n + i].Dot(Vector3.Up) < 0) normals[j * n + i] = -normals[j * n + i];
            }

        // the grid, then a skirt round it: each edge vertex again, a cell lower along the local up
        double cellM = cube.Shape.A * Math.PI / 2 / key.Side / m;
        float skirt = (float)Math.Max(cellM, 50);
        var edge = new List<int>(4 * m);
        for (int i = 0; i < m; i++) edge.Add(i);                       // south, west to east
        for (int j = 0; j < m; j++) edge.Add(j * n + m);               // east, south to north
        for (int i = m; i > 0; i--) edge.Add(m * n + i);               // north, east to west
        for (int j = m; j > 0; j--) edge.Add(j * n);                   // west, north to south
        int total = n * n + edge.Count;
        var verts = new Vector3[total];
        var norms = new Vector3[total];
        var uvs = new Vector2[total];
        for (int k = 0; k < n * n; k++)
        {
            verts[k] = pos[k];
            norms[k] = normals[k];
            uvs[k] = new Vector2((float)(k % n) / m, (float)(k / n) / m);
        }
        for (int e = 0; e < edge.Count; e++)
        {
            int v = edge[e];
            verts[n * n + e] = pos[v] - Vector3.Up * skirt;
            norms[n * n + e] = normals[v];
            uvs[n * n + e] = uvs[v];
        }
        var idx = new List<int>(m * m * 6 + edge.Count * 6);
        for (int j = 0; j < m; j++)
            for (int i = 0; i < m; i++)
            {
                int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                // counter-clockwise seen from above (+Y up, +Z south): front faces outward
                idx.Add(a); idx.Add(c); idx.Add(b);
                idx.Add(b); idx.Add(c); idx.Add(d);
            }
        for (int e = 0; e < edge.Count; e++)
        {
            int top0 = edge[e], top1 = edge[(e + 1) % edge.Count];
            int bot0 = n * n + e, bot1 = n * n + (e + 1) % edge.Count;
            // both sides: a skirt is seen from wherever the crack is
            idx.Add(top0); idx.Add(bot0); idx.Add(top1);
            idx.Add(top1); idx.Add(bot0); idx.Add(bot1);
            idx.Add(top0); idx.Add(top1); idx.Add(bot0);
            idx.Add(top1); idx.Add(bot1); idx.Add(bot0);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = norms;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        return arrays;
    }

    /// <summary>The anchor's world axes as the rows of a rotation: <c>ApplyInverse(v)</c> is a body-fixed vector in world axes.</summary>
    private static DMat3 Rows(FrameAxes axes) => new(Of(axes.X), Of(axes.Y), Of(axes.Z));

    private static DVec3 Of(EcefPos p) => new(p.X, p.Y, p.Z);

    /// <summary>Another body's fixed axes in the anchor body's (columns), at a sky moment.</summary>
    private static DMat3 Columns(BodyId body, BodyId anchor, SkyMoment sky) => new(
        Frames.ConvertDirection(body, new DVec3(1, 0, 0), anchor, sky),
        Frames.ConvertDirection(body, new DVec3(0, 1, 0), anchor, sky),
        Frames.ConvertDirection(body, new DVec3(0, 0, 1), anchor, sky));

    /// <summary>W R as a Godot basis: the body's axes in world axes.</summary>
    private static Basis BasisOf(DMat3 w, DMat3 r) =>
        new(ToVector(w.ApplyInverse(r.C0)), ToVector(w.ApplyInverse(r.C1)), ToVector(w.ApplyInverse(r.C2)));

    private static Vector3 ToVector(DVec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static DVec3 ToD(Vector3 v) => new(v.X, v.Y, v.Z);
}
