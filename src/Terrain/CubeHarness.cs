using System.Diagnostics;
using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// G1's streaming and collision harness for cube tiles (#1443, docs/plans/global-tiling.md;
/// docs/notes/terrain/cube-tiles.md). Not the game: no <see cref="ChunkManager"/>, nothing else
/// loaded. <c>--cubeharness &lt;region dir&gt;</c> (a <c>--cube</c> build) puts the region in one flat
/// Y-up frame (the ENU frame at its middle tile, as G2's anchor frame), then:
///
/// <list type="number">
/// <item><b>Collision</b>: on the steepest tile and a middling one, a tangent-plane
/// <see cref="HeightMapShape3D"/> (1024² at the mesh's spacing) and a trimesh
/// (<see cref="ConcavePolygonShape3D"/>) of the full mesh: build time, memory, and vertical rays
/// against each compared with the mesh (target 2 cm).</item>
/// <item><b>Collision patches</b> (<c>--cubepatches</c>, a region of <c>.cubl</c> leaves, #1511): G2's collision,
/// <c>CubeHarness.Patches.cs</c>.</item>
/// <item><b>Streaming</b>: a camera flies the region's long axis at <c>--cubespeed</c> m/s
/// (100) and 300 m up; tiles within <c>--cuberadius</c> m (4,000) load on workers (decode, vertices,
/// normals) with an LOD step by distance and are committed on the main thread. Frame times,
/// hitches, draw calls, loads.</item>
/// </list>
/// Writes <c>test_output/cubeharness/&lt;name&gt;.md</c> and quits with a RESULT line.
/// </summary>
public partial class CubeHarness : Node3D
{
    public static bool Requested() => CmdArgs.Value("--cubeharness") != null;

    private string _dir = "";
    private CubeManifest _manifest = null!;
    private readonly Dictionary<CubeKey, (string Path, long Offset, int Length)> _index = new();
    private EnuFrame _frame;
    private readonly Dictionary<CubeKey, Transform3D> _xf = new();
    private Camera3D _camera = null!;
    private readonly StringBuilder _report = new();

    // streaming
    private readonly Dictionary<CubeKey, (MeshInstance3D? Node, int Step, bool Busy)> _loaded = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(CubeKey Key, int Step, Godot.Collections.Array? Arrays, double BuildMs)> _ready = new();
    private readonly Dictionary<CubeKey, float[]> _heights = new();
    private readonly SemaphoreSlim _workers = new(4);
    private Vector3 _from, _to;
    private double _flightS, _t, _nextPlan;
    private float _speed = 100, _radius = 4000;
    private bool _flying;
    private ulong _lastTicks;
    private readonly List<double> _frameMs = new(), _buildMs = new(), _commitMs = new();
    private readonly List<int> _draws = new(), _prims = new(), _tilesShown = new();
    private int _loads;
    private StandardMaterial3D _material = null!;

    public override void _Ready()
    {
        // a measurement: quiet, never steals the keyboard, and not paced by the display
        AudioServer.SetBusMute(0, true);
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.NoFocus, true);
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        DisplayServer.WindowSetSize(new Vector2I(1280, 720));
        Engine.MaxFps = 0;
        if (CmdArgs.Float("--cubespeed") is { } sp) _speed = sp;
        if (CmdArgs.Float("--cuberadius") is { } r) _radius = r;

        _dir = CmdArgs.Value("--cubeharness")!;
        if (CmdArgs.Has("--cubepatches"))
        {
            CallDeferred(nameof(StartPatches));
            return;
        }
        _manifest = CubeManifest.FromJson(File.ReadAllText(Path.Combine(_dir, CubeManifest.FileName)));
        foreach (var path in Directory.EnumerateFiles(_dir, "*.cubb"))
        {
            using var fs = File.OpenRead(path);
            foreach (var (key, at) in CubeBundle.ReadIndex(fs)) _index[key] = (path, at.Offset, at.Length);
        }

        // the frame: ENU at the built tile nearest the middle of the region, or nearest --cubeat lat,lon
        var keys = _index.Keys.ToList();
        EcefPos target;
        if (CmdArgs.Value("--cubeat") is { } atArg)
        {
            var ll = atArg.Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            target = Ellipsoid.Wgs84.ToEcef(new Geodetic(ll[0], ll[1], 0));
        }
        else
        {
            double mx = 0, my = 0, mz = 0;
            foreach (var k in keys) { var o = k.Anchor.Origin; mx += o.X; my += o.Y; mz += o.Z; }
            target = new EcefPos(mx / keys.Count, my / keys.Count, mz / keys.Count);
        }
        var mid = keys.MinBy(k => k.Anchor.Origin.DistanceTo(target));
        _frame = mid.Anchor;
        foreach (var k in keys) _xf[k] = TileTransform(_frame, k.Anchor);

        AddChild(new DirectionalLight3D { Rotation = new Vector3(Mathf.DegToRad(-40), Mathf.DegToRad(-30), 0), LightEnergy = 1.2f });
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.55f, 0.68f, 0.82f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.5f, 0.5f, 0.55f),
            },
        });
        _material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 1 };
        _camera = new Camera3D { Far = 20000, Near = 0.5f, Current = true };
        AddChild(_camera);

        _report.AppendLine($"# Cube harness: {Path.GetFileName(_dir.TrimEnd('/', '\\'))}");
        _report.AppendLine();
        _report.AppendLine($"Level {_manifest.LeafLevel}, {1 << _manifest.Bits} cells a tile, {keys.Count} tiles; anchor frame at {mid.Describe()}.");
        _report.AppendLine();
        CallDeferred(nameof(Start));
    }

    /// <summary>A tile's anchor frame in the game frame: one rigid transform, composed in doubles.</summary>
    public static Transform3D TileTransform(EnuFrame game, EnuFrame tile)
    {
        var (ex, ey, ez) = game.DirectionToLocal(tile.East);
        var (ux, uy, uz) = game.DirectionToLocal(tile.Up);
        var (nx, ny, nz) = game.DirectionToLocal(tile.North);
        var (ox, oy, oz) = game.ToLocal(tile.Origin);
        var basis = new Basis(new Vector3((float)ex, (float)ey, (float)ez), new Vector3((float)ux, (float)uy, (float)uz), new Vector3((float)-nx, (float)-ny, (float)-nz));
        return new Transform3D(basis, new Vector3((float)ox, (float)oy, (float)oz));
    }

    private async void Start()
    {
        if (CmdArgs.Value("--cubeshot") is { } shot)
        {
            await Shot(shot);
            return;
        }
        await Collision();
        StartFlight();
    }

    /// <summary>
    /// <c>--cubeshot &lt;png&gt;</c>: no measurement, one still: the camera 1.6 km above the anchor, 3 km
    /// to its south, looking north 30° down, once every tile in reach is in.
    /// </summary>
    private async Task Shot(string png)
    {
        _from = _to = new Vector3(0, 1600, 3000);
        _flightS = 1;
        _camera.Position = _from;
        _camera.LookAt(new Vector3(0, 0, -800), Vector3.Up);
        _radius = 6000;
        Plan();
        int pending;
        do
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            while (_ready.TryDequeue(out var job)) Commit(job.Key, job.Step, job.Arrays, job.BuildMs);
            pending = _loaded.Count(kv => kv.Value.Busy);
        } while (pending > 0);
        int meshes = _loaded.Count(kv => kv.Value.Node != null);
        var aabb = _loaded.Values.Where(e => e.Node != null).Select(e => e.Node!.Transform * e.Node.GetAabb()).ToList();
        GD.Print($"[cubeharness] still: {meshes} tiles drawn of {_loaded.Count} planned; y {aabb.Min(a => a.Position.Y):F0}..{aabb.Max(a => a.End.Y):F0}, "
            + $"x {aabb.Min(a => a.Position.X):F0}..{aabb.Max(a => a.End.X):F0}, z {aabb.Min(a => a.Position.Z):F0}..{aabb.Max(a => a.End.Z):F0}");
        if (CmdArgs.Has("--cubenocull")) _material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        for (int k = 0; k < 10; k++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng(png);
        GD.Print($"[cubeharness] RESULT: ok (shot {png})");
        GetTree().Quit();
    }

    // ---- collision ---------------------------------------------------------------------------

    private async Task Collision()
    {
        // the steepest tile with data everywhere, and a middling one
        var byRange = _manifest.Tiles.Where(t => t.Max > t.Min).OrderByDescending(t => t.Max - t.Min).Select(t => CubeKey.Parse(t.Key)).ToList();
        bool Full(CubeKey k) => !Load(k).Heights.Any(float.IsNaN);
        var picks = new[] { byRange.First(Full), byRange.Skip(byRange.Count / 2).First(Full) }.Distinct().ToList();
        _report.AppendLine("## Collision against the mesh");
        _report.AppendLine();
        _report.AppendLine("| Tile | Shape | Build (ms) | First step (ms) | Memory (MB) | Rays | Median (mm) | p99 (mm) | Max (mm) | Over 2 cm |");
        _report.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var key in picks)
        {
            var tile = Load(key);
            int n = tile.Side;
            var verts = CubeGeometry.Vertices(key, tile.Bits, tile.Heights);
            double spacing = key.Vertex(tile.Bits, n / 2, n / 2, 0).DistanceTo(key.Vertex(tile.Bits, n / 2 + 1, n / 2, 0));
            var hint = (n / 2, n / 2);
            double MeshAt(double x, double z) => CubeGeometry.MeshHeightAt(verts, n, x, z, ref hint);
            var xf = _xf[key];

            // the height field: 1024² at the mesh's spacing, heights divided by it so one uniform scale places it
            const int Side = 1024;
            var sw = Stopwatch.StartNew();
            var field = CubeGeometry.CollisionField(MeshAt, Side, spacing);
            double sampleMs = sw.Elapsed.TotalMilliseconds;
            for (int k = 0; k < field.Length; k++) field[k] /= (float)spacing;
            long mem0 = WorkingSet();
            sw.Restart();
            var hf = new HeightMapShape3D { MapWidth = Side, MapDepth = Side, MapData = field };
            var hfBody = Body(new CollisionShape3D { Shape = hf, Scale = Vector3.One * (float)spacing }, xf, 1u << 20);
            double hfBuild = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            double hfStep = sw.Elapsed.TotalMilliseconds;
            long hfMem = WorkingSet() - mem0;

            // the trimesh: every mesh triangle
            var faces = new List<Vector3>(n * n * 6);
            for (int j = 0; j < n - 1; j++)
                for (int i = 0; i < n - 1; i++)
                {
                    int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                    if (float.IsNaN(verts[a * 3 + 1]) || float.IsNaN(verts[b * 3 + 1]) || float.IsNaN(verts[c * 3 + 1]) || float.IsNaN(verts[d * 3 + 1])) continue;
                    // clockwise seen from above, as the render mesh: Godot's front faces, and a trimesh collides with those only
                    faces.Add(V(verts, a)); faces.Add(V(verts, d)); faces.Add(V(verts, b));
                    faces.Add(V(verts, a)); faces.Add(V(verts, c)); faces.Add(V(verts, d));
                }
            mem0 = WorkingSet();
            sw.Restart();
            var tm = new ConcavePolygonShape3D { Data = faces.ToArray() };
            var tmBody = Body(new CollisionShape3D { Shape = tm }, xf, 1u << 21);
            double tmBuild = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            double tmStep = sw.Elapsed.TotalMilliseconds;
            long tmMem = WorkingSet() - mem0;

            // a patch of it: what collision near a body would build (128 x 128 cells, ~150 m)
            var patch = new List<Vector3>();
            for (int j = n / 2 - 64; j < n / 2 + 64; j++)
                for (int i = n / 2 - 64; i < n / 2 + 64; i++)
                {
                    int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                    patch.Add(V(verts, a)); patch.Add(V(verts, d)); patch.Add(V(verts, b));
                    patch.Add(V(verts, a)); patch.Add(V(verts, c)); patch.Add(V(verts, d));
                }
            mem0 = WorkingSet();
            sw.Restart();
            var pBody = Body(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = patch.ToArray() } }, xf, 1u << 22);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            double patchMs = sw.Elapsed.TotalMilliseconds;
            long patchMem = WorkingSet() - mem0;
            pBody.QueueFree();

            // rays straight down the tile's up, inside its middle 80 %
            var space = GetWorld3D().DirectSpaceState;
            var rng = new Random(key.GetHashCode());
            var errHf = new List<double>();
            var errTm = new List<double>();
            double reach = (Side - 1) * spacing / 2 * 0.8;
            for (int k = 0; k < 4000; k++)
            {
                double x = (rng.NextDouble() * 2 - 1) * reach, z = (rng.NextDouble() * 2 - 1) * reach;
                double y = MeshAt(x, z);
                if (double.IsNaN(y)) continue;
                var from = xf * new Vector3((float)x, (float)y + 60, (float)z);
                var to = xf * new Vector3((float)x, (float)y - 60, (float)z);
                foreach (var (mask, into) in new[] { (1u << 20, errHf), (1u << 21, errTm) })
                {
                    var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, mask));
                    if (hit.Count == 0) continue;
                    var local = xf.AffineInverse() * hit["position"].AsVector3();
                    into.Add(Math.Abs(local.Y - y));
                }
            }
            // alignment: rays exactly at the field's own samples must read the field's values
            var align = new List<double>();
            for (int k = 0; k < 300; k++)
            {
                int col = rng.Next(100, Side - 100), row = rng.Next(100, Side - 100);
                float fy = field[row * Side + col] * (float)spacing;
                if (float.IsNaN(fy)) continue;
                double x = -(Side - 1) * spacing / 2 + col * spacing, z = -(Side - 1) * spacing / 2 + row * spacing;
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(xf * new Vector3((float)x, fy + 60, (float)z), xf * new Vector3((float)x, fy - 60, (float)z), 1u << 20));
                if (hit.Count > 0) align.Add(Math.Abs((xf.AffineInverse() * hit["position"].AsVector3()).Y - fy));
            }
            align.Sort();
            GD.Print($"[cubeharness] height field alignment: {align.Count} rays at its samples, median {(align.Count > 0 ? align[align.Count / 2] * 1000 : -1):F1} mm, max {(align.Count > 0 ? align[^1] * 1000 : -1):F1} mm");
            hfBody.QueueFree();
            tmBody.QueueFree();
            var mt = _manifest.Tiles.First(t => t.Key == key.ToString());
            string name = $"{key} ({mt.Max - mt.Min:F0} m relief)";
            Row(name, $"height field 1024² at {spacing:F2} m (sampled in {sampleMs:F0} ms)", hfBuild, hfStep, hfMem, errHf);
            Row(name, $"trimesh, {faces.Count / 3:N0} triangles", tmBuild, tmStep, tmMem, errTm);
            _report.AppendLine($"| {name} | trimesh patch, 128² cells ({patch.Count / 3:N0} triangles) | {patchMs:F0} (with a physics frame) | | {patchMem / 1e6:F1} | | | | | |");
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        _report.AppendLine();
    }

    private void Row(string tile, string shape, double build, double step, long mem, List<double> err)
    {
        err.Sort();
        string s = err.Count == 0 ? "| 0 | | | | |" :
            $"| {err.Count} | {err[err.Count / 2] * 1000:F1} | {err[(int)(err.Count * 0.99)] * 1000:F1} | {err[^1] * 1000:F1} | {err.Count(e => e > 0.02) * 100.0 / err.Count:F1} % |";
        _report.AppendLine($"| {tile} | {shape} | {build:F0} | {step:F0} | {mem / 1e6:F0} {s}");
        GD.Print($"[cubeharness] {tile} {shape}: build {build:F0} ms, step {step:F0} ms, {mem / 1e6:F0} MB, {s}");
    }

    private StaticBody3D Body(CollisionShape3D shape, Transform3D xf, uint layer)
    {
        var body = new StaticBody3D { Transform = xf, CollisionLayer = layer, CollisionMask = 0 };
        body.AddChild(shape);
        AddChild(body);
        return body;
    }

    private static Vector3 V(float[] v, int i) => new(v[i * 3], v[i * 3 + 1], v[i * 3 + 2]);

    private static long WorkingSet()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        return p.WorkingSet64;
    }

    private CubeTile Load(CubeKey key)
    {
        var (path, offset, length) = _index[key];
        using var fs = File.OpenRead(path);
        fs.Position = offset;
        var bytes = new byte[length];
        fs.ReadExactly(bytes);
        return CubeTileCodec.Decode(bytes);
    }

    // ---- streaming ---------------------------------------------------------------------------

    private void StartFlight()
    {
        // the region's long axis through its tiles' anchors, in the game frame
        var pts = _xf.Values.Select(x => x.Origin).ToList();
        float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minZ = pts.Min(p => p.Z), maxZ = pts.Max(p => p.Z);
        float cy = pts.Average(p => p.Y) + 300;
        bool alongX = maxX - minX >= maxZ - minZ;
        _from = alongX ? new Vector3(minX, cy, (minZ + maxZ) / 2) : new Vector3((minX + maxX) / 2, cy, maxZ);
        _to = alongX ? new Vector3(maxX, cy, (minZ + maxZ) / 2) : new Vector3((minX + maxX) / 2, cy, minZ);
        _flightS = _from.DistanceTo(_to) / _speed;
        _report.AppendLine($"## Streaming: {_from.DistanceTo(_to) / 1000:F1} km at {_speed:F0} m/s, load radius {_radius:F0} m");
        _report.AppendLine();
        PlaceCamera(0);
        _flying = true;
        _lastTicks = Time.GetTicksUsec();
        GD.Print($"[cubeharness] flight {_flightS:F0} s");
    }

    private void PlaceCamera(double t)
    {
        var p = _from.Lerp(_to, (float)(t / _flightS));
        var dir = (_to - _from).Normalized();
        _camera.Position = p;
        _camera.LookAt(p + dir * 1000 + Vector3.Down * 350, Vector3.Up);
    }

    public override void _Process(double delta)
    {
        if (!_flying) return;
        // (the still keeps its camera)
        ulong now = Time.GetTicksUsec();
        double ms = (now - _lastTicks) / 1000.0;
        _lastTicks = now;
        if (_t > 2) // the first frames compile shaders
        {
            _frameMs.Add(ms);
            _draws.Add((int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame));
            _prims.Add((int)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame));
            _tilesShown.Add(_loaded.Count(kv => kv.Value.Node != null));
        }
        _t += delta;
        PlaceCamera(Math.Min(_t, _flightS));

        // at most one commit a frame, as a streamer would
        if (_ready.TryDequeue(out var job)) Commit(job.Key, job.Step, job.Arrays, job.BuildMs);

        if (_t >= _nextPlan)
        {
            _nextPlan = _t + 0.25;
            Plan();
        }
        if (_t > _flightS + 1) Finish();
    }

    /// <summary>A tile's mesh into the scene: once when it loads or its LOD step changes.</summary>
    private void Commit(CubeKey key, int step, Godot.Collections.Array? arrays, double buildMs)
    {
        var sw = Stopwatch.StartNew();
        if (!_loaded.TryGetValue(key, out var entry)) return;
        entry.Node?.QueueFree();
        MeshInstance3D? node = null;
        if (arrays != null)
        {
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            node = new MeshInstance3D { Mesh = mesh, MaterialOverride = _material, Transform = _xf[key] };
            AddChild(node);
        }
        _loaded[key] = (node, step, false);
        _commitMs.Add(sw.Elapsed.TotalMilliseconds);
        _buildMs.Add(buildMs);
        _loads++;
    }

    /// <summary>Loads what is in reach at its LOD step, drops what has left it (500 m of slack).</summary>
    private void Plan()
    {
        var cam = _camera.Position;
        foreach (var (key, xf) in _xf)
        {
            float d = new Vector2(xf.Origin.X - cam.X, xf.Origin.Z - cam.Z).Length();
            bool has = _loaded.TryGetValue(key, out var e);
            if (d > _radius + 500)
            {
                if (has && !e.Busy) { e.Node?.QueueFree(); _loaded.Remove(key); _heights.Remove(key); }
                continue;
            }
            if (d > _radius) continue;
            int step = d < 800 ? 1 : d < 1600 ? 2 : d < 3200 ? 4 : 8;
            if (has && (e.Busy || e.Step == step)) continue;
            _loaded[key] = (has ? e.Node : null, step, true);
            float[]? cached = _heights.GetValueOrDefault(key);
            var k = key;
            Task.Run(async () =>
            {
                await _workers.WaitAsync();
                try
                {
                    var sw = Stopwatch.StartNew();
                    var tile = cached != null ? null : Load(k);
                    var h = cached ?? tile!.Heights;
                    var arrays = Build(k, _manifest.Bits, h, step);
                    _ready.Enqueue((k, step, arrays, sw.Elapsed.TotalMilliseconds));
                    if (cached == null) CallDeferred(nameof(KeepHeights), k.ToString(), h);
                }
                finally { _workers.Release(); }
            });
        }
    }

    private void KeepHeights(string key, float[] h)
    {
        var k = CubeKey.Parse(key);
        if (_loaded.ContainsKey(k)) _heights[k] = h;
    }

    /// <summary>A tile's mesh at an LOD step: the lattice every <paramref name="step"/> vertices, the same points at any step.</summary>
    private static Godot.Collections.Array? Build(CubeKey key, int bits, float[] heights, int step)
    {
        int n = (1 << bits) + 1, sbits = bits - (int)Math.Log2(step), m = (1 << sbits) + 1;
        var sub = new float[m * m];
        for (int j = 0; j < m; j++)
            for (int i = 0; i < m; i++)
                sub[j * m + i] = heights[j * step * n + i * step];
        var v = CubeGeometry.Vertices(key, sbits, sub);
        var pos = new Vector3[m * m];
        var nor = new Vector3[m * m];
        // a vertex with no data is in no triangle; kept finite so the mesh's bounds stay finite
        for (int k = 0; k < pos.Length; k++) pos[k] = float.IsNaN(v[k * 3 + 1]) ? Vector3.Zero : new Vector3(v[k * 3], v[k * 3 + 1], v[k * 3 + 2]);
        for (int j = 0; j < m; j++)
            for (int i = 0; i < m; i++)
            {
                var dx = pos[j * m + Math.Min(i + 1, m - 1)] - pos[j * m + Math.Max(i - 1, 0)];
                var dz = pos[Math.Min(j + 1, m - 1) * m + i] - pos[Math.Max(j - 1, 0) * m + i];
                var nn = dz.Cross(dx);
                // next to a hole (no data) there is no normal: up, and no warning per vertex
                nor[j * m + i] = !nn.IsFinite() || nn.LengthSquared() == 0 ? Vector3.Up : nn.Y < 0 ? -nn.Normalized() : nn.Normalized();
            }
        // tinted by height: lowland green, then brown, rock grey, snow above 2,600 m
        var col = new Color[m * m];
        for (int k = 0; k < col.Length; k++)
        {
            float hh = float.IsNaN(sub[k]) ? 0 : sub[k] - 50;
            col[k] = hh < 700 ? new Color(0.42f, 0.56f, 0.30f).Lerp(new Color(0.55f, 0.58f, 0.36f), hh / 700)
                : hh < 1800 ? new Color(0.55f, 0.58f, 0.36f).Lerp(new Color(0.52f, 0.45f, 0.36f), (hh - 700) / 1100)
                : hh < 2600 ? new Color(0.52f, 0.45f, 0.36f).Lerp(new Color(0.6f, 0.6f, 0.6f), (hh - 1800) / 800)
                : new Color(0.93f, 0.94f, 0.97f);
        }
        var idx = new List<int>((m - 1) * (m - 1) * 6);
        for (int j = 0; j < m - 1; j++)
            for (int i = 0; i < m - 1; i++)
            {
                int a = j * m + i, b = a + 1, c = a + m, d = c + 1;
                if (float.IsNaN(sub[a]) || float.IsNaN(sub[b]) || float.IsNaN(sub[c]) || float.IsNaN(sub[d])) continue;
                // (i, j)-(i+1, j+1) diagonal, wound to face up: j runs north along v, so the order depends on the face's turn
                idx.Add(a); idx.Add(d); idx.Add(b);
                idx.Add(a); idx.Add(c); idx.Add(d);
            }
        if (idx.Count == 0) return null;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = pos;
        arrays[(int)Mesh.ArrayType.Normal] = nor;
        arrays[(int)Mesh.ArrayType.Color] = col;
        arrays[(int)Mesh.ArrayType.Index] = idx.ToArray();
        return arrays;
    }

    private void Finish()
    {
        _flying = false;
        static string Pct(List<double> l, double p) { var s = l.OrderBy(x => x).ToList(); return s.Count == 0 ? "-" : s[Math.Min(s.Count - 1, (int)(s.Count * p))].ToString("F1"); }
        double median = double.Parse(Pct(_frameMs, 0.5), System.Globalization.CultureInfo.InvariantCulture);
        int hitches = _frameMs.Count(m => m > 33.3), spikes = _frameMs.Count(m => m > 2 * median);
        _report.AppendLine("| Frames | Frame ms median / p95 / p99 / max | Hitches > 33 ms | Frames > 2x median | Draw calls mean / max | Primitives mean (M) | Tiles shown mean / max | Loads | Worker build ms median / p95 | Commit ms median / max | Working set (MB) |");
        _report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        _report.AppendLine($"| {_frameMs.Count} | {Pct(_frameMs, 0.5)} / {Pct(_frameMs, 0.95)} / {Pct(_frameMs, 0.99)} / {Pct(_frameMs, 1)} | {hitches} | {spikes} | "
            + $"{(_draws.Count > 0 ? _draws.Average() : 0):F0} / {(_draws.Count > 0 ? _draws.Max() : 0)} | {(_prims.Count > 0 ? _prims.Average() / 1e6 : 0):F2} | "
            + $"{(_tilesShown.Count > 0 ? _tilesShown.Average() : 0):F0} / {(_tilesShown.Count > 0 ? _tilesShown.Max() : 0)} | {_loads} | {Pct(_buildMs, 0.5)} / {Pct(_buildMs, 0.95)} | "
            + $"{Pct(_commitMs, 0.5)} / {Pct(_commitMs, 1)} | {WorkingSet() / 1e6:F0} |");
        string outDir = ProjectSettings.GlobalizePath("res://test_output/cubeharness");
        Directory.CreateDirectory(outDir);
        string file = Path.Combine(outDir, Path.GetFileName(_dir.TrimEnd('/', '\\')) + ".md");
        File.WriteAllText(file, _report.ToString());
        GD.Print(_report.ToString());
        GD.Print($"[cubeharness] RESULT: ok ({file})");
        GetTree().Quit();
    }
}
