using System.Collections.Concurrent;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// The Earth from far out (#1430 H4; docs/plans/ellipsoid-handover.md §6a): the coarse cube levels
/// of a <c>--cube-global</c> region (G1's format), in real geometry, coloured by the month. Until
/// round tiles (#1443 G2) it lives beside the flat LV95 world: placed through the frame's Earth axes
/// (<see cref="OriginFrame.EcefAxes"/>), drawn a little behind whatever stands at the same place
/// (<c>shaders/globe.gdshader</c>), so the flat tiles and horizon blocks win where they are, and
/// shown once the camera is above <see cref="ShowAboveM"/>.
///
/// <para>
/// Levels by size on screen (<see cref="Styles.LodScale"/>, C0): a tile splits into its four children
/// when a drawn cell would cover more than <see cref="SplitPixels"/> pixels, and only once all four are
/// built, so nothing is ever missing; past the horizon a tile is not drawn. Each tile is drawn every
/// <see cref="Stride"/>-th lattice vertex (64 cells), its mesh built once on a worker around its own
/// ENU anchor (<see cref="CubeGeometry.Exact"/>, rigorous) with skirts against cracks between levels;
/// a shift only rewrites transforms (<c>no-mesh-rebuild-per-frame</c>).
/// </para>
/// </summary>
public partial class Globe : Node
{
    public const string DefaultDir = "D:/UnitSportSwitzerland/cube_global";

    /// <summary>
    /// The region to draw: <c>--globe DIR</c> ("off" none), else the full one on D: when it is there, else the
    /// levels the game ships (<see cref="GlobeFiles.Shipped"/>, 0-2: 23.5 MB in Git LFS); null leaves the globe out.
    /// </summary>
    public static string? DataDir
    {
        get
        {
            string? dir = CmdArgs.Value("--globe");
            if (dir is "off" or "0") return null;
            if (dir != null) return GlobeFiles.HasManifest(dir) ? dir : null;
            if (GlobeFiles.HasManifest(DefaultDir)) return DefaultDir;
            return GlobeFiles.HasManifest(GlobeFiles.Shipped) ? GlobeFiles.Shipped : null;
        }
    }

    /// <summary>
    /// Drawn above this camera altitude, metres: where the far plane starts reaching past the flat world's
    /// horizon (<c>CameraGlobal</c>, from 5 km) and the haze thins on the globe's word (<c>HazeAt</c>, from 3 km).
    /// At 15 km a landing from space looked through 3-15 km of thinned haze at nothing past the horizon
    /// blocks' 60 km: a flat haze-coloured plane with the lakes on it (#1528).
    /// </summary>
    public const float ShowAboveM = 5_000;

    /// <summary>Every this many lattice vertices are drawn: 64 cells a tile of 512.</summary>
    public const int Stride = 8;

    public const float SplitPixels = 6;

    private static readonly StringName Cartoon = "cartoon", Ambient = "ambient", SunColor = "sun_color";

    // the environment's light as the globe's tiles are lit by it, kept on change
    private static Vector3 _ambient = new(0.40f, 0.48f, 0.74f), _sunColor = Vector3.One;

    private const double EarthR = 6_371_000;

    public WorldOrigin? Origin { get; set; }

    /// <summary>The tiles with real data (the rest is the generated fill, which gives way to the globe from high up).</summary>
    public Func<IReadOnlySet<TileId>?>? RealTiles { get; set; }

    /// <summary>The flat world's node (the tiles, the horizon blocks, what stands on them): hidden outright once every flat tile has dissolved, so none of it is drawn.</summary>
    public Node3D? FlatWorld { get; set; }

    /// <summary>
    /// The generated fill dissolves from here to <see cref="FillGoneM"/>, metres: high, where a globe cell
    /// (1.2 km past the leaves) is a few pixels; from 18 km the whole view went to the globe's blur.
    /// </summary>
    public const double FillFromM = 50_000, FillGoneM = 80_000;

    /// <summary>Every flat tile dissolves from here to <see cref="FlatGoneM"/>: the globe alone above.</summary>
    public const double FlatFromM = 150_000, FlatGoneM = 250_000;

    /// <summary>
    /// Farther than this from the real tiles' box, metres, the fill is the generator shaping ground past the country
    /// it knows (#1497: re-entry 1,600 km out drew it as a black disc under the shuttle from 68 km): there it is
    /// hidden wherever the globe shows, gone over the 5 km above <see cref="ShowAboveM"/>.
    /// </summary>
    public const double FillFarM = 100_000;

    private static readonly StringName FlatEdge = "world_flat_edge", FlatEdgeOn = "world_flat_edge_on";
    private Vector4 _edge = new(float.NaN, 0, 0, 0);
    private float _edgeOn = -1;

    private static readonly StringName FillHide = "world_fill_hide", FlatHide = "world_flat_hide", RealMask = "world_real_mask",
        RealOrigin = "world_real_origin", RealExtent = "world_real_extent", RealAxes = "world_real_axes";

    private int _realCount = -1, _maskMinE, _maskMaxE, _maskMinN, _maskMaxN;
    private float _fill = -1, _flat = -1;
    private ImageTexture? _mask;

    /// <summary>Whether a globe is drawn past the edge of the data (the altitude haze need not hide it).</summary>
    public static bool Present { get; private set; }

    /// <summary>
    /// The world in Earth positions as the globe is placed: the flat world's embedding under the camera
    /// (<see cref="OriginFrame.EcefAxesAt"/>), redone past <see cref="ReembedM"/>; the atmosphere uses it too.
    /// </summary>
    public static FrameAxes? Axes { get; private set; }

    /// <summary>The camera's nadir may move this far before the globe is placed again: the flat plane leaves the embedding by 8 cm there.</summary>
    public const double ReembedM = 1000;

    private double _axesE = double.NaN, _axesN;

    public string Dir { get; set; } = DefaultDir;

    /// <summary>
    /// The earth world (<c>--frame earth</c>, #1510): no flat world to give way to, so the globe is drawn at every
    /// altitude, placed in the world's own anchor frame, down to the region's leaves (<see cref="Leaves"/>), its tiles
    /// added under <see cref="CommitBudgetMs"/>. The leaves round the camera are drawn whole by <c>EarthWorld</c>, in
    /// front of these (the shader's push).
    /// </summary>
    public bool Earth { get; set; }

    /// <summary>
    /// The earth world's leaves, the quadtree's last level: the region's, and the fill's round them (#1510 PR 3, their
    /// heights alone), so the globe under the near leaves is their ground and never the real relief the fill replaces.
    /// </summary>
    public Terrain.Leaves.ILeafSource? Leaves { get; set; }

    private readonly HashSet<CubeKey> _drawnLeaves = new();
    // every ancestor of a drawn leaf, with how many drawn leaves it holds: such a tile always splits
    private readonly Dictionary<CubeKey, int> _holdsDrawn = new();

    /// <summary>
    /// The earth world drew a leaf, or dropped it (<c>EarthWorld.TileEntered</c>/<c>TileUnloaded</c>): the globe steps
    /// aside exactly there, splitting down to that leaf and leaving its tile out, so its coarser ground (every 8th
    /// vertex, a metre off in the hills) never shows through the leaf's own (#1629). Its neighbours stay the globe's.
    /// </summary>
    public void SetLeafDrawn(CubeKey leaf, bool drawn)
    {
        if (drawn ? !_drawnLeaves.Add(leaf) : !_drawnLeaves.Remove(leaf)) return;
        for (var k = leaf; k.Level > 0;)
        {
            k = k.Parent;
            int n = _holdsDrawn.GetValueOrDefault(k) + (drawn ? 1 : -1);
            if (n > 0) _holdsDrawn[k] = n;
            else _holdsDrawn.Remove(k);
        }
        // planned again at once: a gap or a double layer lasts a frame, not a fifth of a second
        _nextPlan = 0;
    }

    /// <summary>Main-thread time a frame spends adding built tiles in the earth world, ms.</summary>
    public double CommitBudgetMs { get; set; } = 3;

    private CubeManifest _manifest = null!;

    // the pyramid's lake levels (#1669), null for one built before them
    private float[]? _lakeLevels;
    private int _maxLevel;
    private Dictionary<CubeKey, (string Path, long Offset, int Length)> _heights = new(), _albedo = new();
    private readonly Dictionary<CubeKey, MeshInstance3D> _patches = new();
    private readonly Dictionary<CubeKey, EnuFrame> _anchors = new();
    private readonly ConcurrentQueue<(CubeKey Key, Godot.Collections.Array Arrays, Image? Colour, int Month, Terrain.ChunkNode.TreeMeshes? Trees, Terrain.WaterMeshBuilder.MeshData? Water)> _ready = new();
    private readonly HashSet<CubeKey> _loading = new(), _want = new();
    /// <summary>Leaf requests that came back empty (a streamed leaf not come yet, #1655): freed on the main thread, asked again.</summary>
    private readonly ConcurrentQueue<CubeKey> _leafMissed = new();
    /// <summary>Leaf patches to read again (<see cref="RefreshLeaves"/>): each drawn until its new one lands.</summary>
    private readonly HashSet<CubeKey> _leafStale = new();
    private volatile int _leafGeneration;

    /// <summary>
    /// The leaves changed under the globe (#1655: the server's leaf index arrived): every leaf patch is read again, the
    /// old one drawn until the new one lands. Main thread.
    /// </summary>
    public void RefreshLeaves()
    {
        _leafGeneration++;
        foreach (var key in _patches.Keys)
            if (key.Level == Terrain.Leaves.LeafFrames.LeafLevel) _leafStale.Add(key);
        _nextPlan = 0;
    }
    private readonly SemaphoreSlim _workers = new(3);
    private Shader _shader = null!;
    private int _month = 7, _epoch = int.MinValue;
    private double _nextPlan;
    private bool _shown;

    /// <summary>
    /// The earth world draws its tiles below this camera altitude, metres, in the style's own terrain material
    /// (<c>StyleKit.Material(Terrain)</c>, the flat world's horizon blocks' and tiles'): lit, hazed, banded and lifted
    /// by refraction as the leaves and the flat terrain are, coloured per vertex from the pyramid's palette
    /// (<see cref="GlobeGround"/>). Above it, <c>globe.gdshader</c>: the true sun's terminator over the whole Earth,
    /// which one scene light cannot give (#1629). Refraction's lift is gone by 20 km (<c>Curvature</c>), so the two meet.
    /// </summary>
    public const double TerrainBelowM = 20_000;

    // each tile's own globe material (its colour image, the sun's terminator); the terrain one is shared
    private readonly Dictionary<CubeKey, ShaderMaterial> _globeMaterials = new();
    private bool _onTerrain;

    public override void _Ready()
    {
        var files = new GlobeFiles(Dir);
        _manifest = files.Manifest();
        _lakeLevels = _manifest.Lakes?.ToArray();
        _maxLevel = _manifest.LevelsOrLeaf.Max(l => l.Level);
        if (Leaves != null) _maxLevel = Math.Max(_maxLevel, Terrain.Leaves.LeafFrames.LeafLevel);
        (_heights, _albedo) = files.Index(_manifest);
        _shader = GD.Load<Shader>("res://shaders/globe.gdshader");
        if (DayNight.Instance is { Sky: { } sky }) _month = sky.Date.Month;
        GD.Print($"[globe] {_heights.Count} tiles to level {_maxLevel}, {_albedo.Count} coloured, month {_month}, from {Dir}");
        for (int f = 0; f < CubeSphere.Faces; f++) Request(new CubeKey(f, 0, 0, 0));
        Present = true;
    }

    public override void _ExitTree()
    {
        Present = false;
        Axes = null;
        RenderingServer.GlobalShaderParameterSet(FillHide, 0f);
        RenderingServer.GlobalShaderParameterSet(FlatHide, 0f);
        RenderingServer.GlobalShaderParameterSet(FlatEdgeOn, 0f);
        if (FlatWorld != null && IsInstanceValid(FlatWorld)) FlatWorld.Visible = true;
    }

    /// <summary>
    /// The flat world's edge dissolved into the globe while it is drawn (#1528, the user's choice: from above,
    /// the tiles' square stood out against the globe as a patch): the tiles' square round the camera's tile, the
    /// horizon blocks' circle, faded in over the 5 km above <see cref="ShowAboveM"/> (terrain.gdshaderinc's
    /// <c>flat_edge</c>). Written on change only.
    /// </summary>
    private void Edge(double altitude, Vector3 camera)
    {
        float on = FlatWorld is Terrain.ChunkManager { Visible: true } ? (float)Mathf.SmoothStep(ShowAboveM, ShowAboveM + 5_000, altitude) : 0f;
        if (on != _edgeOn) RenderingServer.GlobalShaderParameterSet(FlatEdgeOn, _edgeOn = on);
        if (on <= 0 || FlatWorld is not Terrain.ChunkManager chunks) return;
        var (e, n) = Origin!.ToLv95(camera);
        // the tiles load by Chebyshev distance from the anchor's tile: the camera's tile, give or take one
        var centre = Origin.ToWorld(Math.Floor(e / 1000) * 1000 + 500, Math.Floor(n / 1000) * 1000 + 500, 0);
        var edge = new Vector4(centre.X, centre.Z, (chunks.Lod.MaxDist + 0.5f) * 1000f, (float)(chunks.Horizon?.DistanceM ?? 0));
        if (edge != _edge) RenderingServer.GlobalShaderParameterSet(FlatEdge, _edge = edge);
    }

    /// <summary>
    /// The flat world giving way: the generated fill from <see cref="FillFromM"/>, every tile from
    /// <see cref="FlatFromM"/> (terrain.gdshaderinc's <c>flat_hidden</c>); the real tiles' mask rebuilt
    /// when the real set grows. Written on change only.
    /// </summary>
    private void Hide(double altitude, Vector3 camera)
    {
        if (RealTiles?.Invoke() is { Count: > 0 } real && real.Count != _realCount)
        {
            _realCount = real.Count;
            int minE = int.MaxValue, maxE = int.MinValue, minN = int.MaxValue, maxN = int.MinValue;
            foreach (var t in real)
            {
                minE = Math.Min(minE, t.E); maxE = Math.Max(maxE, t.E);
                minN = Math.Min(minN, t.N); maxN = Math.Max(maxN, t.N);
            }
            var image = Image.CreateEmpty(maxE - minE + 1, maxN - minN + 1, false, Image.Format.R8);
            image.Fill(Colors.Black);
            foreach (var t in real) image.SetPixel(t.E - minE, maxN - t.N, Colors.White);
            (_maskMinE, _maskMaxE, _maskMinN, _maskMaxN) = (minE, maxE, minN, maxN);
            _mask = ImageTexture.CreateFromImage(image);
            RenderingServer.GlobalShaderParameterSet(RealMask, _mask.GetRid());
            RenderingServer.GlobalShaderParameterSet(RealExtent, new Vector2(image.GetWidth() * 1000f, image.GetHeight() * 1000f));
            PushMaskCorner();
        }
        float fill = _mask == null ? 0 : (float)Mathf.SmoothStep(FillFromM, FillGoneM, altitude);
        if (_mask != null && fill < 1f && altitude > ShowAboveM && FarFromReal(camera))
            fill = Math.Max(fill, (float)Mathf.SmoothStep(ShowAboveM, ShowAboveM + 5_000, altitude));
        float flat = (float)Mathf.SmoothStep(FlatFromM, FlatGoneM, altitude);
        if (fill != _fill) RenderingServer.GlobalShaderParameterSet(FillHide, _fill = fill);
        if (flat != _flat)
        {
            RenderingServer.GlobalShaderParameterSet(FlatHide, _flat = flat);
            // all dissolved: not drawn at all (34 M triangles discarded a pixel at a time at 3,000 km)
            if (FlatWorld != null && IsInstanceValid(FlatWorld)) FlatWorld.Visible = flat < 1f;
        }
    }

    /// <summary>Whether the camera is more than <see cref="FillFarM"/> outside the real tiles' box.</summary>
    private bool FarFromReal(Vector3 camera)
    {
        var g = Origin!.ToGlobal(camera);
        // the box's nearest point to the camera, in the real tiles' LV95 notation (#1443: no distance by hand)
        var (e, n) = (g.Lv95.E, g.Lv95.N);
        var nearest = GlobalPos.FromLv95(Math.Clamp(e, _maskMinE * 1000.0, (_maskMaxE + 1) * 1000.0),
            Math.Clamp(n, _maskMinN * 1000.0, (_maskMaxN + 1) * 1000.0), g.Alt);
        return g.HorizontalDistanceSquaredTo(nearest) > FillFarM * FillFarM;
    }

    /// <summary>The mask is sampled by world XZ: its NW corner and grid axes, again after a shift.</summary>
    private void PushMaskCorner()
    {
        if (_mask == null || Origin == null) return;
        double e = _maskMinE * 1000.0, n = (_maskMaxN + 1) * 1000.0;
        var nw = Origin.ToWorld(e, n, 0);
        var east = (Origin.ToWorld(e + 1000, n, 0) - nw).Normalized();
        var south = (Origin.ToWorld(e, n - 1000, 0) - nw).Normalized();
        RenderingServer.GlobalShaderParameterSet(RealOrigin, new Vector2(nw.X, nw.Z));
        RenderingServer.GlobalShaderParameterSet(RealAxes, new Vector4(east.X, east.Z, south.X, south.Z));
    }

    public override void _Process(double delta)
    {
        if (Origin == null || GetViewport()?.GetCamera3D() is not { } cam) return;

        // what the workers built, a few a frame (the earth world: under a time budget)
        var clock = Earth ? System.Diagnostics.Stopwatch.StartNew() : null;
        for (int k = 0; (clock != null ? clock.Elapsed.TotalMilliseconds < CommitBudgetMs : k < 4) && _ready.TryDequeue(out var r); k++)
            if (r.Month == _month && (!_patches.ContainsKey(r.Key) || _leafStale.Remove(r.Key))) Commit(r.Key, r.Arrays, r.Colour, r.Trees, r.Water);
        while (_leafMissed.TryDequeue(out var missed)) _loading.Remove(missed);
        for (int k = 0; (clock != null ? clock.Elapsed.TotalMilliseconds < CommitBudgetMs : k < 4) && _buildingsReady.TryDequeue(out var b); k++)
            CommitBuildings(b.Key, b.Mesh);

        double altitude = Origin.AltitudeOf(cam.GlobalPosition);
        if (!Earth)
        {
            Hide(altitude, cam.GlobalPosition);
            Edge(altitude, cam.GlobalPosition);
        }
        bool show = Earth || altitude > ShowAboveM;
        if (show != _shown)
        {
            _shown = show;
            foreach (var p in _patches.Values) p.Visible = false;
            _nextPlan = 0;
        }
        if (!show) return;
        Relight();
        if (Earth)
        {
            bool terrain = altitude < TerrainBelowM;
            if (terrain != _onTerrain)
            {
                _onTerrain = terrain;
                foreach (var (key, node) in _patches) node.MaterialOverride = MaterialFor(key);
                foreach (var lake in _lakes.Values) lake.Visible = terrain;
            }
            // the world is the anchor frame itself: placed again at each re-anchor
            if (Origin.Frame.Epoch != _epoch)
            {
                // no generated fill to hide (terrain.gdshaderinc's flat_hide would hide every terrain fragment) and
                // none to stand behind (globe.gdshader's fill push is off by its own flag in this world)
                if (_fill != 0) RenderingServer.GlobalShaderParameterSet(FillHide, _fill = 0);
                _epoch = Origin.Frame.Epoch;
                Axes = Origin.Frame.Axes ?? Origin.EcefAxes;
                foreach (var (key, node) in _patches) node.Transform = Place(key);
            }
        }
        else
        {
            var (ce, cn) = Origin.ToLv95(cam.GlobalPosition);
            if (Origin.Frame.Epoch != _epoch || double.IsNaN(_axesE) || Math.Abs(ce - _axesE) > ReembedM || Math.Abs(cn - _axesN) > ReembedM)
            {
                _epoch = Origin.Frame.Epoch;
                (_axesE, _axesN) = (ce, cn);
                Axes = Origin.Frame.EcefAxesAt(ce, cn);
                foreach (var (key, node) in _patches) node.Transform = Place(key);
                PushMaskCorner();
            }
        }

        // five plans a second
        _nextPlan -= delta;
        if (_nextPlan > 0) return;
        _nextPlan = 0.2;
        // a new month (the game's date moved on, or /time set another): the season's colour, built again
        if (DayNight.Instance is { Sky: { } sky } && sky.Date.Month != _month) Rebuild(sky.Date.Month);
        Plan(cam);
    }

    /// <summary>Every tile dropped and the six faces asked for again, coloured for <paramref name="month"/>; tiles still on a worker are dropped as they land.</summary>
    private void Rebuild(int month)
    {
        _month = month;
        foreach (var node in _patches.Values) node.QueueFree();
        _patches.Clear();
        _globeMaterials.Clear();
        _palette.Clear();
        _buildings.Clear();
        _lakes.Clear();
        _loading.Clear();
        _leafStale.Clear();
        for (int f = 0; f < CubeSphere.Faces; f++) Request(new CubeKey(f, 0, 0, 0));
        GD.Print($"[globe] month {month}: built again");
    }

    /// <summary>
    /// The tiles lit as the style lights the world: its ambient (colour by energy) and its sun's colour by
    /// energy, written to every tile when they change (the night's moonlight never: the sun stays the real one).
    /// </summary>
    private void Relight()
    {
        if (GetViewport()?.FindWorld3D()?.Environment is not { } env) return;
        var a = env.AmbientLightColor * env.AmbientLightEnergy;
        var ambient = new Vector3(a.R, a.G, a.B);
        var sun = DayNight.Instance?.Sun is { } light && IsInstanceValid(light) && (DayNight.Instance?.Night ?? 0) < 0.5f
            ? new Vector3(light.LightColor.R, light.LightColor.G, light.LightColor.B) * light.LightEnergy : _sunColor;
        if (ambient.IsEqualApprox(_ambient) && sun.IsEqualApprox(_sunColor)) return;
        (_ambient, _sunColor) = (ambient, sun);
        foreach (var m in _globeMaterials.Values)
        {
            m.SetShaderParameter(Ambient, ambient);
            m.SetShaderParameter(SunColor, sun);
        }
    }

    /// <summary>Which tiles to draw: from the six faces down, by size on screen and the horizon.</summary>
    private void Plan(Camera3D cam)
    {
        var axes = Axes ?? Origin!.EcefAxes;
        var c = cam.GlobalPosition;
        var eye = axes.ToEcef(c.X, c.Y, c.Z);
        double eyeR = Math.Sqrt(FrameAxes.Dot(eye, eye));
        var eyeDir = new EcefPos(eye.X / eyeR, eye.Y / eyeR, eye.Z / eyeR);
        double horizon = GlobeGround.HorizonAngle(eye);
        float viewH = GetViewport().GetVisibleRect().Size.Y;
        double focal = viewH / (2 * Math.Tan(Mathf.DegToRad(cam.Fov) / 2)) * Styles.LodScale.Value;

        _want.Clear();
        _distance.Clear();
        for (int f = 0; f < CubeSphere.Faces; f++) Select(new CubeKey(f, 0, 0, 0), eye, eyeDir, horizon, focal);
        foreach (var (key, node) in _patches) node.Visible = _want.Contains(key);
        // leaf patches read again after the server's leaves came (#1655), those in view first: the old one stays drawn meanwhile
        foreach (var key in _leafStale) if (_want.Contains(key)) Request(key);
        PlanBuildings();
    }

    private void Select(CubeKey key, EcefPos eye, EcefPos eyeDir, double horizon, double focal)
    {
        // drawn whole by the earth world
        if (_drawnLeaves.Contains(key)) return;
        var anchor = AnchorOf(key);
        var centre = anchor.Origin;
        double centreR = Math.Sqrt(FrameAxes.Dot(centre, centre));
        // the tile's angular radius seen from the Earth's centre, a little generous: a face spans 90°
        double radius = Math.PI / 2 / key.Side * 0.8;
        double angle = Math.Acos(Math.Clamp(FrameAxes.Dot(eyeDir, centre) / centreR, -1, 1));
        if (angle > horizon + radius) return;

        // to the tile's nearest edge, from its anchor on the ellipsoid (the sphere's centre stood ~20 km off it
        // at Swiss latitudes, so the tiles under the camera never split down to the leaves)
        var d = eye - centre;
        double distance = Math.Max(Math.Sqrt(FrameAxes.Dot(d, d)) - centreR * radius, GlobeGround.NearestM);
        double cell = EarthR * Math.PI / 2 / key.Side / ((1 << CubeKey.LatticeBits(key.Level)) / Stride);
        bool holds = _holdsDrawn.ContainsKey(key);
        // the far leaves' band: down to the real leaves, as the flat world's rings draw its real tiles
        bool far = Earth && FarLeafM > 0 && distance < FarLeafM && key.Level < Terrain.Leaves.LeafFrames.LeafLevel && HoldsReal(key);
        bool split = key.Level < _maxLevel && (holds || far || cell / distance * focal > SplitPixels);
        if (split)
        {
            bool all = true;
            foreach (var child in key.Children())
                if (!_patches.ContainsKey(child) && !_drawnLeaves.Contains(child)) { all = false; Request(child); }
            if (all)
            {
                foreach (var child in key.Children()) Select(child, eye, eyeDir, horizon, focal);
                return;
            }
        }
        if (_patches.ContainsKey(key))
        {
            _want.Add(key);
            _distance[key] = distance;
        }
        else Request(key);
    }

    private EnuFrame AnchorOf(CubeKey key)
    {
        if (!_anchors.TryGetValue(key, out var a)) _anchors[key] = a = key.Anchor;
        return a;
    }

    private void Request(CubeKey key)
    {
        if ((_patches.ContainsKey(key) && !_leafStale.Contains(key)) || _loading.Contains(key)) return;
        // the earth world's last level: a leaf's own heights, no colour of its own yet
        if (key.Level == Terrain.Leaves.LeafFrames.LeafLevel && Leaves is { } leaves)
        {
            if (!leaves.Has(key)) return;   // past the fill's domain: the parent's tile stays
            _loading.Add(key);
            int leafMonth = _month, generation = _leafGeneration;
            if (IsFarLeaf(key) && RealLeaves is { } real)
            {
                _ = Task.Run(async () =>
                {
                    await _workers.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (BuildFarLeaf(key, real) is { } far) _ready.Enqueue((key, far.Arrays, null, leafMonth, far.Trees, far.Water));
                        else _leafMissed.Enqueue(key);
                    }
                    catch (Exception e)
                    {
                        GD.PushError($"[globe] far leaf {key}: {e.Message}");
                    }
                    finally
                    {
                        _workers.Release();
                    }
                });
                return;
            }
            _ = Task.Run(async () =>
            {
                await _workers.WaitAsync().ConfigureAwait(false);
                try
                {
                    // nothing yet (a streamed leaf not come, #1655), or read before a RefreshLeaves: asked again
                    if (leaves.HeightsOf(key) is { } tile && generation == _leafGeneration)
                    {
                        var (palette, rect) = Earth ? AncestorPalette(key) : (null, default);
                        // the heights hold the lake's bed (#298): the surface over it, as the leaf draws its water
                        bool isReal = leaves is Terrain.Leaves.EarthLeaves { Real: var real } && real.Has(key);
                        // a fill leaf's too (#1678): the pyramid's lakes it stands on, over the fill's bed
                        var water = leaves is Terrain.Leaves.EarthLeaves earthLeaves ? earthLeaves.WaterOf(key) : null;
                        var lakes = Earth ? new WaterOut() : null;
                        if (Build(tile, palette, rect, water, ancestor: AncestorTile, lakesOut: lakes, beds: isReal || leaves is not Terrain.Leaves.EarthLeaves,
                            lakes: _lakeLevels) is { } arrays)
                            _ready.Enqueue((key, arrays, null, leafMonth, null, lakes?.Mesh));
                        else _leafMissed.Enqueue(key);
                    }
                    else _leafMissed.Enqueue(key);
                }
                catch (Exception e)
                {
                    GD.PushError($"[globe] leaf {key}: {e.Message}");
                }
                finally
                {
                    _workers.Release();
                }
            });
            return;
        }
        if (!_heights.TryGetValue(key, out var at)) return;
        _loading.Add(key);
        _albedo.TryGetValue(key, out var colourAt);
        int month = _month;
        _ = Task.Run(async () =>
        {
            await _workers.WaitAsync().ConfigureAwait(false);
            try
            {
                var tile = CubeTileCodec.Decode(ReadBytes(at));
                Image? colour = null;
                if (colourAt.Path != null)
                {
                    var a = CubeAlbedoCodec.Decode(ReadBytes(colourAt));
                    colour = new Image();
                    if (colour.LoadWebpFromBuffer(a.ForMonth(month)) != Error.Ok) colour = null;
                }
                byte[]? palette = null;
                var rect = new Vector4(0, 0, 1, 1);
                if (Earth)
                {
                    // the ground's cover per vertex, for the style's terrain material near the ground
                    if (colour != null) _palette[key] = palette = GlobeGround.Weigh(colour.GetWidth(), colour.GetHeight(), colour.GetPixel);
                    else (palette, rect) = AncestorPalette(key);
                }
                colour?.GenerateMipmaps();
                // no height at all: the parent's tile stays (the key stays loading, never asked again)
                var lakes = Earth ? new WaterOut() : null;
                if (Build(tile, palette, rect, ancestor: AncestorTile, lakesOut: lakes, lakes: _lakeLevels) is { } arrays) _ready.Enqueue((key, arrays, colour, month, null, lakes?.Mesh));
            }
            catch (Exception e)
            {
                GD.PushError($"[globe] {key}: {e.Message}");
            }
            finally
            {
                _workers.Release();
            }
        });
    }

    private static byte[] ReadBytes((string Path, long Offset, int Length) at) => GlobeFiles.ReadBytes(at);

    /// <summary>A pyramid tile's heights, decoded on the asking worker (a coarser level filling a finer one's holes); null where none.</summary>
    private CubeTile? AncestorTile(CubeKey key) => _heights.TryGetValue(key, out var at) ? CubeTileCodec.Decode(ReadBytes(at)) : null;

    /// <summary>
    /// A tile's mesh around its anchor: every <see cref="Stride"/>-th lattice vertex, rigorous, with
    /// normals from its neighbours, the colour's UVs (row 0 the south edge, as the image), and a skirt
    /// along each edge hanging a third of a drawn cell, so a neighbour drawn a level coarser leaves no crack.
    /// </summary>
    public static Godot.Collections.Array? Build(CubeTile tile, byte[]? palette = null, Vector4 rect = default, CubeWater? water = null,
        int stride = Stride, byte[]? cover = null, Func<CubeKey, CubeTile?>? ancestor = null, WaterOut? lakesOut = null, bool beds = true,
        float[]? lakes = null)
    {
        var key = tile.Key;
        int bits = tile.Bits, m = (1 << bits) / stride, n = m + 1;
        var anchor = key.Anchor;
        var pos = new Vector3[n * n];
        var up = new Vector3[n * n];
        var hs = new float[n * n];
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                hs[j * n + i] = tile.HeightAt(i * stride, j * stride);
        // the coarser levels under this tile, sampled at its vertices: where its own heights are missing (past the data a
        // level was built from), and under a leaf, whose heights hold a lake's bed (#298) even where it has no water
        float[]? under = null;
        bool leaf = key.Level == Terrain.Leaves.LeafFrames.LeafLevel;
        if (ancestor != null && (leaf || Array.Exists(hs, float.IsNaN)))
        {
            under = new float[n * n];
            Array.Fill(under, float.NaN);
            GlobeGround.FillFromAncestors(under, n, key, bits, stride, ancestor);
            for (int k = 0; k < hs.Length; k++)
                if (float.IsNaN(hs[k])) hs[k] = under[k];
        }
        // what is still missing, from the neighbours: never taken for still water (a hole filled from its edge is level)
        bool[]? guessed = null;
        if (Array.Exists(hs, float.IsNaN))
        {
            guessed = new bool[n * n];
            for (int k = 0; k < hs.Length; k++) guessed[k] = float.IsNaN(hs[k]);
        }
        if (!GlobeGround.FillHoles(hs, n)) return null;
        // still water's surface over its bed: a leaf's own water (heights and water on the same lattice), and the
        // coarser level's surface where the leaf has none (level within 5 cm, or the leaf's bed well under it: Lavaux's
        // is 310 m down, and its shore vertices drew a wall that deep)
        bool[]? wet = null;
        if (water != null && water.Bits == bits)
        {
            wet = new bool[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    // as the pyramid is built from it (#1654): a bed the water stage left dry by the leaf's edge is water too
                    float ground = hs[j * n + i], surface = CubeSurface.SurfaceAt(water, i * stride, j * stride, ground);
                    if (surface == ground && float.IsNaN(CubeSurface.LevelAt(water.Level, water.Side, i * stride, j * stride))) continue;
                    wet[j * n + i] = true;
                    hs[j * n + i] = surface;
                }
        }
        // a real leaf's heights only: a generated fill leaf holds no bed, and under a mountain the generator lacks (the
        // Salève, past its relief) it stood far under the coarser level and drew a lake on the slope (#1669)
        if (leaf && beds && under != null && !Array.Exists(under, float.IsNaN))
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int k = j * n + i;
                    // the coarser level holds the surface (a 2.4 m lattice at level 13): a leaf vertex well under it is a bed
                    bool bed = under[k] - hs[k] > GlobeGround.BedM;
                    if (!bed && (!GlobeGround.Level(under, n, i, j) || hs[k] > under[k] + GlobeGround.LevelM)) continue;
                    (wet ??= new bool[n * n])[k] = true;
                    hs[k] = under[k];
                }
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float h = hs[j * n + i];
                var (x, y, z) = CubeGeometry.Exact(key, bits, i * stride, j * stride, h);
                pos[j * n + i] = new Vector3((float)x, (float)y, (float)z);
                var normal = CubeSphere.Normal(key.Face, CubeSphere.Lattice(key.Level + bits, ((long)key.X << bits) + i * stride), CubeSphere.Lattice(key.Level + bits, ((long)key.Y << bits) + j * stride));
                var (ux, uy, uz) = anchor.DirectionToLocal(normal);
                up[j * n + i] = new Vector3((float)ux, (float)uy, (float)uz);
            }

        double cellM = EarthR * Math.PI / 2 / key.Side / m;
        float skirt = (float)(cellM / 3);
        int edge = 4 * m, total = n * n + edge;
        var verts = new Vector3[total];
        var norms = new Vector3[total];
        var uvs = new Vector2[total];
        var colours = palette != null || cover != null ? new Color[total] : null;
        // the vertices painted as water, for the lakes' own surface (#1669)
        var lakeAt = lakesOut != null && colours != null ? new bool[n * n] : null;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int k = j * n + i;
                var du = pos[j * n + Math.Min(i + 1, m)] - pos[j * n + Math.Max(i - 1, 0)];
                var dv = pos[Math.Min(j + 1, m) * n + i] - pos[Math.Max(j - 1, 0) * n + i];
                var nrm = du.Cross(dv).Normalized();
                if (nrm.Dot(up[k]) < 0) nrm = -nrm;
                verts[k] = pos[k];
                norms[k] = nrm;
                uvs[k] = new Vector2(i / (float)m, j / (float)m);
                if (colours != null)
                {
                    var weights = palette != null ? GlobeGround.Sample(palette, rect.X + uvs[k].X * rect.Z, rect.Y + uvs[k].Y * rect.W) : Vector4.Zero;
                    // a leaf's own water decides wet and dry; the colour image only what the dry ground is
                    bool isWater = false;
                    if (wet != null) weights = (isWater = wet[k]) ? new Vector4(1, 0, 0, 0) : GlobeGround.Dry(weights);
                    // a coarser level has no water of its own: a lake's surface is level to the centimetre, where the
                    // colour image hints at water too (a flat plateau in the data on the Salève drew a lake)
                    else if (guessed?[k] != true && GlobeGround.Level(hs, n, i, j)
                        && (GlobeGround.OnLake(lakes, hs[k], tile.Quantum) is var onLake && onLake || weights.X > GlobeGround.WaterHint))
                    {
                        // a lake's surface only where the image says water plainly: a level plateau with a hint of it
                        // (the Salève's) is painted so, but a water surface there caught the sky (#1669); or at one of the
                        // pyramid's lake levels, where Blue Marble's dark shore read as forest and left 2 km of Léman ground
                        isWater = onLake || weights.X > GlobeGround.LakeWeight;
                        weights = new Vector4(1, 0, 0, 0);
                    }
                    float hash = Terrain.CoverPalette.Hash(key.X * 64 + i, key.Y * 64 + j);
                    if (cover != null)
                    {
                        // a far leaf: its own cover, as the flat world's tiles are painted (no surface pattern this far)
                        var cls = wet?[k] == true ? CoverClass.Water : (CoverClass)cover[j * stride * ((1 << bits) + 1) + i * stride];
                        isWater = cls == CoverClass.Water;
                        var c = Terrain.CoverPalette.ColorFor(cls, hs[k], hash).SrgbToLinear();
                        colours[k] = new Color(c.R, c.G, c.B, 0);
                    }
                    else colours[k] = GlobeGround.Colour(weights, hs[k], hash);
                    if (lakeAt != null) lakeAt[k] = isWater;
                }
            }

        // the edge ring, counter-clockwise in (i, j), then its lowered copy
        var ring = new int[edge];
        int r = 0;
        for (int i = 0; i < m; i++) ring[r++] = i;
        for (int j = 0; j < m; j++) ring[r++] = j * n + m;
        for (int i = m; i > 0; i--) ring[r++] = m * n + i;
        for (int j = m; j > 0; j--) ring[r++] = j * n;
        for (int e = 0; e < edge; e++)
        {
            int k = ring[e];
            verts[n * n + e] = pos[k] - up[k] * skirt;
            norms[n * n + e] = norms[k];
            uvs[n * n + e] = uvs[k];
            if (colours != null) colours[n * n + e] = colours[k];
        }

        // Godot's front faces wind clockwise as seen; whether (i, j) runs clockwise from above depends on the face
        var a0 = pos[0];
        bool flip = (pos[1] - a0).Cross(pos[n] - a0).Dot(up[0]) > 0;
        if (lakeAt != null && Array.IndexOf(lakeAt, true) >= 0) lakesOut!.Mesh = Lakes(pos, up, verts, lakeAt, n, flip, (float)cellM);
        var idx = new List<int>(m * m * 6 + edge * 12);
        void Tri(int a, int b, int c)
        {
            if (flip) { idx.Add(a); idx.Add(c); idx.Add(b); }
            else { idx.Add(a); idx.Add(b); idx.Add(c); }
        }
        for (int j = 0; j < m; j++)
            for (int i = 0; i < m; i++)
            {
                int k00 = j * n + i, k10 = k00 + 1, k01 = k00 + n, k11 = k01 + 1;
                Tri(k00, k10, k11);
                Tri(k00, k11, k01);
            }
        for (int e = 0; e < edge; e++)
        {
            int a = ring[e], b = ring[(e + 1) % edge], la = n * n + e, lb = n * n + (e + 1) % edge;
            // both windings: a skirt is seen from either side
            idx.Add(a); idx.Add(b); idx.Add(lb);
            idx.Add(a); idx.Add(lb); idx.Add(la);
            idx.Add(a); idx.Add(lb); idx.Add(b);
            idx.Add(a); idx.Add(la); idx.Add(lb);
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = norms;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        if (colours != null) arrays[(int)Mesh.ArrayType.Color] = colours;
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        return arrays;
    }

    /// <summary>A tile's lakes, built on the worker with its ground (<see cref="Build"/>).</summary>
    public sealed class WaterOut
    {
        public Terrain.WaterMeshBuilder.MeshData? Mesh;
    }

    /// <summary>How far the ground under a lake's surface sinks by its shore, metres: the water drawn over it, not into it.</summary>
    public const float LakeSinkM = 3f;

    /// <summary>
    /// How far it sinks inside the lake (every neighbour water): the water shaders shade by the depth under the surface,
    /// and flat's lakes stand over their beds (Lake Geneva's 300 m), so a shallow far lake drew lighter than the near one.
    /// </summary>
    public const float LakeDeepM = 100f;

    /// <summary>
    /// A tile's lakes as the world's water (#1669): a quad wherever the four corners of a drawn cell are water, the
    /// triangle over three (a diagonal shore, as <see cref="Terrain.WaterMeshBuilder"/>), at the surface the ground was drawn
    /// at; that ground sinks <see cref="LakeSinkM"/> under it. No waves this far (wave scale 0, as flat's far tiles).
    /// </summary>
    private static Terrain.WaterMeshBuilder.MeshData? Lakes(Vector3[] pos, Vector3[] up, Vector3[] verts, bool[] lakeAt, int n, bool flip, float cellM)
    {
        var map = new int[n * n];
        Array.Fill(map, -1);
        var vs = new List<Vector3>();
        var uvs = new List<Vector2>();
        var idx = new List<int>();
        int V(int k)
        {
            if (map[k] < 0)
            {
                map[k] = vs.Count;
                vs.Add(pos[k]);
                uvs.Add(new Vector2(0, cellM));
            }
            return map[k];
        }
        void Tri(int a, int b, int c)
        {
            if (flip) { idx.Add(V(a)); idx.Add(V(c)); idx.Add(V(b)); }
            else { idx.Add(V(a)); idx.Add(V(b)); idx.Add(V(c)); }
        }
        int m = n - 1;
        for (int j = 0; j < m; j++)
            for (int i = 0; i < m; i++)
            {
                int k00 = j * n + i, k10 = k00 + 1, k01 = k00 + n, k11 = k01 + 1;
                bool w00 = lakeAt[k00], w10 = lakeAt[k10], w01 = lakeAt[k01], w11 = lakeAt[k11];
                int wet = (w00 ? 1 : 0) + (w10 ? 1 : 0) + (w01 ? 1 : 0) + (w11 ? 1 : 0);
                if (wet == 4)
                {
                    Tri(k00, k10, k11);
                    Tri(k00, k11, k01);
                }
                else if (wet == 3)
                {
                    if (!w00) Tri(k10, k11, k01);
                    else if (!w10) Tri(k00, k11, k01);
                    else if (!w11) Tri(k00, k10, k01);
                    else Tri(k00, k10, k11);
                }
            }
        if (idx.Count == 0) return null;
        // the ground under the water sinks, so the two never fight for a pixel; inside the lake (every neighbour water,
        // so every cell round it is covered) as deep as a lake looks
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int k = j * n + i;
                if (map[k] < 0) continue;
                // a vertex on the tile's edge by its neighbours on this side (#1678): counted as a shore it sank 3 m,
                // and every leaf edge across a lake drew a pale shallow line
                bool inside = true;
                for (int b = -1; b <= 1 && inside; b++)
                    for (int a = -1; a <= 1; a++)
                        if (!lakeAt[Math.Clamp(j + b, 0, m) * n + Math.Clamp(i + a, 0, m)]) { inside = false; break; }
                verts[k] = pos[k] - up[k] * (inside ? LakeDeepM : LakeSinkM);
            }
        return new Terrain.WaterMeshBuilder.MeshData(vs.ToArray(), uvs.ToArray(), idx.ToArray());
    }

    // each tile's ground cover weights (GlobeGround.Weigh), for its children's vertices too
    private readonly ConcurrentDictionary<CubeKey, byte[]> _palette = new();

    /// <summary>The nearest ancestor's palette and the part of it over <paramref name="key"/>; none, (null, _).</summary>
    private (byte[]? Palette, Vector4 Rect) AncestorPalette(CubeKey key)
    {
        for (var k = key; k.Level > 0;)
        {
            k = k.Parent;
            if (!_palette.TryGetValue(k, out var p)) continue;
            int dl = key.Level - k.Level;
            float scale = 1f / (1 << dl);
            return (p, new Vector4((key.X - (k.X << dl)) * scale, (key.Y - (k.Y << dl)) * scale, scale, scale));
        }
        return (null, default);
    }

    private void Commit(CubeKey key, Godot.Collections.Array arrays, Image? colour, Terrain.ChunkNode.TreeMeshes? trees = null,
        Terrain.WaterMeshBuilder.MeshData? water = null)
    {
        _loading.Remove(key);
        var node = Patch(key, arrays, colour, _shader);
        var globe = (ShaderMaterial)node.MaterialOverride;
        if (colour == null) Inherit(key, globe);
        _globeMaterials[key] = globe;
        if (Earth)
        {
            // drawn into a frame that turns with the Earth: no fill to stand behind
            globe.SetShaderParameter(FillPush, false);
            node.MaterialOverride = MaterialFor(key);
        }
        if (trees != null) AddTrees(node, trees);
        // a leaf patch read again (#1655) takes the old one's place and its visibility
        bool shown = false;
        if (_patches.Remove(key, out var old)) { shown = old.Visible; old.QueueFree(); _buildings.Remove(key); _lakes.Remove(key); }
        if (water != null && WaterMaterial is { } waterMaterial)
        {
            var lake = new MeshInstance3D { Name = "Lakes", Mesh = Terrain.ChunkNode.ToArrayMesh(water, waterMaterial),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = _onTerrain };
            node.AddChild(lake);
            _lakes[key] = lake;
        }
        node.Visible = shown;
        AddChild(node);
        node.Transform = Place(key);
        _patches[key] = node;
    }

    private static readonly StringName AlbedoTex = "albedo_tex", UvRect = "uv_rect", FillPush = "fill_push";

    /// <summary>A tile's material in the earth world: the style's terrain near the ground, its globe material above.</summary>
    private Material MaterialFor(CubeKey key) =>
        _onTerrain ? TerrainMaterial ??= Styles.StyleKit.Material(Styles.MaterialRole.Terrain) : _globeMaterials[key];

    /// <summary>
    /// A tile with no colour of its own (the leaves' level: heights alone) takes its nearest coloured ancestor's
    /// image, the part over it (the pyramid's Swiss levels are the world's own palette, <see cref="OurColours"/>):
    /// it was white (#1629).
    /// </summary>
    private void Inherit(CubeKey key, ShaderMaterial material)
    {
        for (var k = key; k.Level > 0;)
        {
            k = k.Parent;
            if (!_globeMaterials.TryGetValue(k, out var m)
                || m.GetShaderParameter(AlbedoTex).Obj is not Texture2D tex) continue;
            int dl = key.Level - k.Level;
            float scale = 1f / (1 << dl);
            var rect = m.GetShaderParameter(UvRect).Obj is Vector4 r ? r : new Vector4(0, 0, 1, 1);
            // within the ancestor's own rectangle of its image
            var sub = new Vector4((key.X - (k.X << dl)) * scale, (key.Y - (k.Y << dl)) * scale, scale, scale);
            material.SetShaderParameter(AlbedoTex, tex);
            material.SetShaderParameter(UvRect, new Vector4(rect.X + sub.X * rect.Z, rect.Y + sub.Y * rect.W, sub.Z * rect.Z, sub.W * rect.W));
            return;
        }
    }

    /// <summary>A tile's drawn patch: its mesh and its colour, in its anchor frame.</summary>
    private static MeshInstance3D Patch(CubeKey key, Godot.Collections.Array arrays, Image? colour, Shader shader)
    {
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter(Cartoon, Styles.StyleKit.Style == Styles.VisualStyle.Cartoon);
        material.SetShaderParameter(Ambient, _ambient);
        material.SetShaderParameter(SunColor, _sunColor);
        if (colour != null) material.SetShaderParameter("albedo_tex", ImageTexture.CreateFromImage(colour));
        return new MeshInstance3D
        {
            Name = key.FileStem,
            Mesh = mesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    /// <summary>A tile's anchor (x east, y up, z south) in this world, composed in doubles.</summary>
    private Transform3D Place(CubeKey key) => PlaceIn(Axes ?? Origin!.EcefAxes, AnchorOf(key));

    private static Transform3D PlaceIn(FrameAxes axes, EnuFrame a)
    {
        Vector3 Turn(EcefPos v) => new((float)FrameAxes.Dot(v, axes.X), (float)FrameAxes.Dot(v, axes.Y), (float)FrameAxes.Dot(v, axes.Z));
        var (ox, oy, oz) = axes.FromEcef(a.Origin);
        var south = new EcefPos(-a.North.X, -a.North.Y, -a.North.Z);
        return new Transform3D(new Basis(Turn(a.East), Turn(a.Up), Turn(south)), new Vector3((float)ox, (float)oy, (float)oz));
    }

    /// <summary>
    /// The globe in the model viewer: the whole Earth at level 1 (24 tiles, Earth-fixed axes, north up)
    /// and one level-4 tile over Switzerland, built as the game builds them, coloured for July and
    /// lit from +Y. Needs a <c>--cube-global</c> region (<see cref="DataDir"/>); none, nothing shown.
    /// </summary>
    [Showcase("Space", "Globe")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> GlobeShowcase()
    {
        if (DataDir is not { } dir) yield break;
        yield return ("whole Earth, level 1", () => Showcase(dir, k => k.Level == 1, earthFixed: true));
        var bern = CubeKey.FromLatLon(46.95, 7.44, 4);
        yield return ("a level-4 tile over Switzerland", () => Showcase(dir, k => k == bern, earthFixed: false));
    }

    private static Node3D Showcase(string dir, Func<CubeKey, bool> which, bool earthFixed)
    {
        var root = new Node3D { Name = "Globe" };
        var files = new GlobeFiles(dir);
        var (heights, albedo) = files.Index(files.Manifest());
        var shader = GD.Load<Shader>("res://shaders/globe.gdshader");
        // Earth-fixed with the north pole up (+Y), or one tile in its own frame
        var earth = new FrameAxes(new EcefPos(0, 0, 0), new EcefPos(0, 1, 0), new EcefPos(0, 0, 1), new EcefPos(1, 0, 0));
        foreach (var (key, at) in heights)
        {
            if (!which(key)) continue;
            var tile = CubeTileCodec.Decode(ReadBytes(at));
            Image? colour = null;
            if (albedo.TryGetValue(key, out var c))
            {
                colour = new Image();
                if (colour.LoadWebpFromBuffer(CubeAlbedoCodec.Decode(ReadBytes(c)).ForMonth(7)) != Error.Ok) colour = null;
            }
            if (Build(tile) is not { } arrays) continue;
            var node = Patch(key, arrays, colour, shader);
            root.AddChild(node);
            if (earthFixed) node.Transform = PlaceIn(earth, key.Anchor);
        }
        return root;
    }
}
