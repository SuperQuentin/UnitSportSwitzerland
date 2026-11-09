using System.Collections.Concurrent;
using System.Diagnostics;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

using UnitSport.Terrain.Leaves;

namespace UnitSport.Terrain.Earth;

/// <summary>
/// The earth world's ground (#1510, G2 C1, behind <c>--frame earth</c>): the region's built cube leaves round the
/// camera and the streaming anchors, read from a <see cref="LeafStore"/>, drawn at full resolution and asked through
/// <see cref="Ground"/>, the same <see cref="IWorldGround"/> the flat world's <see cref="ChunkManager"/> serves.
///
/// <para>
/// The world is an anchor frame (<see cref="WorldOrigin(FrameAxes)"/>); a leaf stands in it by one rigid transform
/// from its own ENU anchor (<see cref="LeafToWorld"/>). Every query composes in doubles: the world point to ECEF,
/// to the leaf it lies in, to that leaf's anchor frame, down to its mesh, and back. The mesh is the collision's
/// (<see cref="LeafCollision.Prepare"/>, the road blend at clearance 0), so what is drawn is what is stood on.
/// Collision is C2's <see cref="CollisionPatches"/> round the collision anchors: trimesh patches placed by their
/// leaf's transform. The drawn ground is the bare lattice blended at the visual clearance under the roads; roads,
/// buildings, doors, trees and water are furnished on each leaf's stand-in tile (<c>EarthWorld.Furnish.cs</c>). The far
/// ground is the <c>Globe</c>'s.
/// </para>
/// </summary>
public sealed partial class EarthWorld : Node3D, IOriginContainer, IOriginShiftAware
{
    /// <summary>Leaves within this of a focus (the camera, an anchor) are loaded, drawn and answer queries, metres.</summary>
    public const double NearM = 900;

    /// <summary>A loaded leaf is dropped once every focus is this far past <see cref="NearM"/>.</summary>
    public const double ReleaseM = 300;

    /// <summary>A leaf's half diagonal at level 14, a little generous: a leaf counts as near when its centre is within <see cref="NearM"/> plus this.</summary>
    public const double LeafRadiusM = 450;

    /// <summary>Leaves read and meshed on workers at once.</summary>
    public const int Workers = 3;

    /// <summary>Main-thread time a frame spends adding what the workers built, ms (the flat world's <c>CommitBudgetMs</c>).</summary>
    public double CommitBudgetMs { get; set; } = 4;

    /// <summary>
    /// Whether leaves are meshed and drawn: off on the dedicated server (#1513), which asks the leaves' heights and stands
    /// its bots on the collision patches, but draws nothing.
    /// </summary>
    public bool BuildMeshes { get; init; } = true;

    public LeafStore Store { get; }

    public WorldOrigin Origin { get; }

    /// <summary>The world's ground: what gameplay asks.</summary>
    public IWorldGround Ground => _ground ??= new LeafGround(this);

    /// <summary>The camera's position, or null: a focus of the streaming besides the anchors.</summary>
    public Func<Vector3?>? Camera { get; set; }

    private IWorldGround? _ground;
    private readonly LeafCollisionSource _collisionSource;
    private readonly CollisionPatches _patches;
    private readonly Material _material;

    private readonly Dictionary<CubeKey, Near> _near = new();
    private readonly HashSet<CubeKey> _loading = new();
    private readonly ConcurrentQueue<Near> _ready = new();
    private readonly SemaphoreSlim _workers = new(Workers);
    private readonly Dictionary<CubeKey, StaticBody3D> _bodies = new();
    private readonly List<Node3D> _anchors = new();
    private readonly HashSet<Node3D> _collisionAnchors = new();
    private readonly List<CollisionPatches.Body> _bodyList = new();
    private readonly List<EcefPos> _foci = new();
    private readonly HashSet<CubeKey> _want = new();
    private readonly Queue<CubeKey> _walk = new();
    private readonly List<CubeKey> _drop = new();
    private double _nextPlan;

    public event Action<GroundTile>? TileEntered;
    public event Action<GroundTile>? TileUnloaded;

    /// <summary>A loaded leaf: its content, its mesh (blended) in its anchor frame, its node once drawn.</summary>
    private sealed class Near
    {
        public required CubeKey Key;
        public required CubeLeafContent Content;
        public required EnuFrame Anchor;
        public required float[] Vertices;
        public required int Side;
        public required Godot.Collections.Array? Arrays;
        public List<(float[] Points, int Count, float Half, float Height)>? Bores;
        public LeafTransform? Lattice;
        public (int I, int J) Hint;
        public MeshInstance3D? Mesh;
        public Furniture? Furniture;
        public ChunkNode? Node;
    }

    public EarthWorld(LeafStore store, WorldOrigin origin)
    {
        Store = store;
        Origin = origin;
        Name = "Earth";
        _collisionSource = new LeafCollisionSource(store.Get);
        _patches = new CollisionPatches(_collisionSource.Build)
        {
            Failed = (key, e) => GD.PushError($"[earth] collision patch {key}: {e.Message}"),
        };
        _material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1f };
    }

    public override void _Ready()
    {
        GD.Print($"[earth] {Store.Count} leaves from {Store.Dir}; near {NearM:F0} m, collision {CollisionReach.RadiusM:F0} m");
        // where the region is: its leaves' box in LV95 (a start outside it stands on nothing)
        if (Store.Count == 0) return;
        double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
        foreach (var key in Store.Keys)
        {
            var (e, n, _) = SwissGeodesy.FromEcef(key.Anchor.Origin);
            (minE, maxE, minN, maxN) = (Math.Min(minE, e), Math.Max(maxE, e), Math.Min(minN, n), Math.Max(maxN, n));
        }
        var densest = Store.Keys.MaxBy(Store.Bytes);
        var (de, dn, _) = SwissGeodesy.FromEcef(densest.Anchor.Origin);
        GD.Print($"[earth] leaves from LV95 {minE:F0}/{minN:F0} to {maxE:F0}/{maxN:F0}; the densest {densest} at {de:F0}/{dn:F0}");
    }

    public void AddAnchor(Node3D anchor, bool? collision = null)
    {
        if (!_anchors.Contains(anchor)) _anchors.Add(anchor);
        if (collision ?? anchor is PhysicsBody3D) _collisionAnchors.Add(anchor);
    }

    public void RemoveAnchor(Node3D anchor)
    {
        _anchors.Remove(anchor);
        _collisionAnchors.Remove(anchor);
    }

    public bool HasAnchor(Node3D anchor) => _anchors.Contains(anchor);

    /// <summary>Loaded leaves (they answer queries).</summary>
    public int NearCount => _near.Count;

    /// <summary>Collision patches standing as bodies.</summary>
    public int BodyCount => _bodies.Count;

    /// <summary>Nothing loading, waiting to be added, or building collision.</summary>
    public bool Idle => _loading.Count == 0 && _ready.IsEmpty && !_patches.Busy;

    public override void _Process(double delta)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalMilliseconds < CommitBudgetMs && _ready.TryDequeue(out var leaf)) Add(leaf);

        _nextPlan -= delta;
        if (_nextPlan > 0) return;
        _nextPlan = 0.2;
        Plan();
    }

    public override void _PhysicsProcess(double delta)
    {
        _bodyList.Clear();
        _anchors.RemoveAll(a => !IsInstanceValid(a));
        _collisionAnchors.RemoveWhere(a => !IsInstanceValid(a));
        foreach (var a in _collisionAnchors)
        {
            var at = a.GlobalPosition;
            var velocity = a switch
            {
                RigidBody3D r => r.LinearVelocity,
                CharacterBody3D c => c.Velocity,
                _ => Vector3.Zero,
            };
            var ahead = velocity * (float)CollisionReach.LookAheadSeconds;
            if (ahead.Length() > CollisionReach.LookAheadMaxM) ahead = ahead.Normalized() * (float)CollisionReach.LookAheadMaxM;
            // high in the air (a plane, a jump from a cliff) a body wants no ground patches round it
            bool airborne = HeightAt(at, out float ground) && at.Y - ground > CollisionReach.AirGapM;
            _bodyList.Add(new(Origin.ToEcef(at), Origin.ToEcef(at + ahead), airborne));
        }
        _patches.Update(_bodyList);

        // a few patches a frame: each is one trimesh shape
        for (int k = 0; k < 2 && _patches.TryTakeReady(out var key, out var faces); k++) AddBody(key, faces);
        while (_patches.TryTakeReleased(out var gone))
            if (_bodies.Remove(gone, out var body)) body.QueueFree();
    }

    /// <summary>Which leaves are wanted: every leaf within <see cref="NearM"/> of a focus, by a walk over cube neighbours.</summary>
    private void Plan()
    {
        _foci.Clear();
        if (Camera?.Invoke() is { } cam && cam.IsFinite()) _foci.Add(Origin.ToEcef(cam));
        foreach (var a in _anchors)
            if (IsInstanceValid(a) && a.IsInsideTree()) _foci.Add(Origin.ToEcef(a.GlobalPosition));
        if (_foci.Count == 0) return;

        _want.Clear();
        foreach (var focus in _foci)
        {
            _walk.Clear();
            var start = CubeKey.FromEcef(focus, LeafFrames.LeafLevel);
            _walk.Enqueue(start);
            var seen = new HashSet<CubeKey> { start };
            while (_walk.TryDequeue(out var key))
            {
                if (Distance(key, focus) > NearM) continue;
                _want.Add(key);
                foreach (var next in key.Neighbours())
                    if (seen.Add(next)) _walk.Enqueue(next);
            }
        }
        foreach (var key in _want)
            if (!_near.ContainsKey(key) && Store.Has(key)) Request(key);

        _drop.Clear();
        foreach (var key in _near.Keys)
        {
            double nearest = double.MaxValue;
            foreach (var focus in _foci) nearest = Math.Min(nearest, Distance(key, focus));
            if (nearest > NearM + ReleaseM) _drop.Add(key);
        }
        foreach (var key in _drop) Remove(key);
    }

    /// <summary>
    /// From a point to a leaf's nearest edge along the ground, about: the point brought down (or up) to the leaf
    /// centre's radius, then the chord to the centre less the half diagonal. A camera 2 km up still loads the leaves
    /// under it.
    /// </summary>
    private static double Distance(CubeKey key, EcefPos p)
    {
        var c = key.Anchor.Origin;
        double rc = Math.Sqrt(c.X * c.X + c.Y * c.Y + c.Z * c.Z), rp = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
        double k = rp > 0 ? rc / rp : 1;
        double dx = c.X - p.X * k, dy = c.Y - p.Y * k, dz = c.Z - p.Z * k;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) - LeafRadiusM;
    }

    private void Request(CubeKey key)
    {
        if (!_loading.Add(key)) return;
        _ = Task.Run(async () =>
        {
            await _workers.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Load(key) is { } leaf) _ready.Enqueue(leaf);
                else GD.PushWarning($"[earth] {key}: no ground");
            }
            catch (Exception e)
            {
                GD.PushError($"[earth] {key}: {e.Message}");
            }
            finally
            {
                _workers.Release();
            }
        });
    }

    /// <summary>A leaf read, its blended mesh made and meshed for drawing: on a worker.</summary>
    private Near? Load(CubeKey key)
    {
        if (Store.Get(key) is not { Heights: not null } content) return null;
        if (_collisionSource.Prepared(key) is not { } prepared) return null;
        var vertices = prepared.Vertices.ToArray();
        int side = prepared.Side;
        // drawn: the bare lattice blended under the roads at the visual clearance, as a tile's mesh (stood on: the collision's)
        var drawn = (float[])prepared.Bare.Clone();
        if (prepared.Bands is { } bands) LeafRoadBlend.Apply(drawn, bands, TerrainMeshBuilder.VisualBlendClearance);
        return new Near
        {
            Key = key,
            Content = content,
            Anchor = key.Anchor,
            Vertices = vertices,
            Side = side,
            Arrays = BuildMeshes ? Mesh(drawn, side, content) : null,
            Furniture = BuildMeshes ? Furnish(prepared.Stand, content, side) : null,
            Bores = content.Roads is { } roads ? RoadTunnels.Bores(roads) : null,
            Lattice = LeafFrames.Swiss.TransformOf(key),
        };
    }

    private void Add(Near leaf)
    {
        _loading.Remove(leaf.Key);
        if (_near.ContainsKey(leaf.Key)) return;
        _near[leaf.Key] = leaf;
        if (leaf.Arrays is { } arrays)
        {
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
            leaf.Mesh = new MeshInstance3D { Name = leaf.Key.FileStem, Mesh = mesh, MaterialOverride = _material };
            leaf.Arrays = null;
            AddChild(leaf.Mesh);
            leaf.Mesh.Transform = LeafToWorld(leaf.Anchor);
        }
        TileEntered?.Invoke(GroundTile.FromLeaf(leaf.Key));
        if (leaf.Furniture is { } f)
        {
            leaf.Node = Commit(leaf.Key, f);
            leaf.Furniture = null;
        }
    }

    private void Remove(CubeKey key)
    {
        if (!_near.Remove(key, out var leaf)) return;
        leaf.Mesh?.QueueFree();
        if (leaf.Node != null) Unfurnish(key, leaf.Node);
        TileUnloaded?.Invoke(GroundTile.FromLeaf(key));
    }

    private void AddBody(CubeKey key, Vector3[]? faces)
    {
        if (faces is not { Length: >= 3 } || _bodies.ContainsKey(key)) return;
        var (leaf, _, _) = LeafCollision.Split(key);
        var body = new StaticBody3D { Name = key.FileStem };
        body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = faces } });
        AddChild(body);
        body.Transform = LeafToWorld(leaf.Anchor);
        _bodies[key] = body;
    }

    /// <summary>A leaf's anchor frame in the world: one rigid transform, composed in doubles.</summary>
    public Transform3D LeafToWorld(EnuFrame a)
    {
        var axes = Origin.Frame.Axes ?? Origin.EcefAxes;
        Vector3 Turn(EcefPos v) => new((float)FrameAxes.Dot(v, axes.X), (float)FrameAxes.Dot(v, axes.Y), (float)FrameAxes.Dot(v, axes.Z));
        var (ox, oy, oz) = axes.FromEcef(a.Origin);
        var south = new EcefPos(-a.North.X, -a.North.Y, -a.North.Z);
        return new Transform3D(new Basis(Turn(a.East), Turn(a.Up), Turn(south)), new Vector3((float)ox, (float)oy, (float)oz));
    }

    // the children are placed again from the frame after a shift, not moved: their doubles stay exact
    public void OnOriginShifted(OriginShift shift)
    {
        foreach (var leaf in _near.Values)
        {
            if (leaf.Mesh != null) leaf.Mesh.Transform = LeafToWorld(leaf.Anchor);
            if (leaf.Node != null) leaf.Node.Transform = StandInToWorld(leaf.Key);
        }
        foreach (var (key, body) in _bodies)
            body.Transform = LeafToWorld(LeafCollision.Split(key).Leaf.Anchor);
        // what the flat streamer keeps in world space: the door index, the building shader's door boxes
        Flat?.OnOriginShifted(shift);
    }

    /// <summary>
    /// A leaf's mesh for drawing, every lattice vertex (the collision's), coloured by its cover, hole quads left out.
    /// Triangles split as the collision's: along (i, j)–(i+1, j+1), front faces up.
    /// </summary>
    private static Godot.Collections.Array? Mesh(float[] v, int n, CubeLeafContent content)
    {
        var pos = new Vector3[n * n];
        for (int k = 0; k < pos.Length; k++) pos[k] = new Vector3(v[3 * k], v[3 * k + 1], v[3 * k + 2]);
        var normals = new Vector3[n * n];
        var colours = new Color[n * n];
        var cover = content.Cover;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int k = j * n + i;
                var dx = pos[j * n + Math.Min(i + 1, n - 1)] - pos[j * n + Math.Max(i - 1, 0)];
                var dz = pos[Math.Min(j + 1, n - 1) * n + i] - pos[Math.Max(j - 1, 0) * n + i];
                var normal = dz.Cross(dx);
                // a no-data (NaN) neighbour at the leaf set's edge: up, not a NaN normal (85k warnings a drive, #1512)
                normal = normal.IsFinite() && normal.LengthSquared() > 0 ? normal.Normalized() : Vector3.Up;
                normals[k] = normal.Y < 0 ? -normal : normal;
                var c = cover != null && k < cover.Length ? (CoverClass)cover[k] : CoverClass.Open;
                colours[k] = CoverPalette.ColorFor(c, pos[k].Y, ((i * 73856093) ^ (j * 19349663)) * (1f / int.MaxValue));
            }

        var holes = content.Holes is { Length: > 0 } h ? h.ToHashSet() : null;
        int bits = content.Bits;
        var indices = new List<int>((n - 1) * (n - 1) * 6);
        for (int j = 0; j + 1 < n; j++)
            for (int i = 0; i + 1 < n; i++)
            {
                if (holes != null && holes.Contains(LeafHoles.Quad(i, j, bits))) continue;
                int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                if (float.IsNaN(pos[a].Y) || float.IsNaN(pos[b].Y) || float.IsNaN(pos[c].Y) || float.IsNaN(pos[d].Y)) continue;
                // j grows north (z falls): a b d and a d c wind front faces up
                indices.Add(a); indices.Add(d); indices.Add(b);
                indices.Add(a); indices.Add(c); indices.Add(d);
            }
        if (indices.Count == 0) return null;
        for (int k = 0; k < pos.Length; k++)
            if (float.IsNaN(pos[k].Y)) pos[k] = Vector3.Zero;

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = pos;
        arrays[(int)Godot.Mesh.ArrayType.Normal] = normals;
        arrays[(int)Godot.Mesh.ArrayType.Color] = colours;
        arrays[(int)Godot.Mesh.ArrayType.Index] = indices.ToArray();
        return arrays;
    }

    // --- the queries -------------------------------------------------------------------------------------------

    /// <summary>The loaded leaf under a world point, and the point in its anchor frame; false off the loaded leaves.</summary>
    private bool LeafAt(Vector3 worldPos, out Near leaf, out double x, out double y, out double z)
    {
        var ecef = Origin.ToEcef(worldPos);
        var key = CubeKey.FromEcef(ecef, LeafFrames.LeafLevel);
        if (!_near.TryGetValue(key, out leaf!))
        {
            x = y = z = 0;
            return false;
        }
        (x, y, z) = leaf.Anchor.ToLocal(ecef);
        return true;
    }

    /// <summary>A point of a leaf's anchor frame in the world.</summary>
    private Vector3 ToWorld(Near leaf, double x, double y, double z)
    {
        var axes = Origin.Frame.Axes ?? Origin.EcefAxes;
        var (wx, wy, wz) = axes.FromEcef(leaf.Anchor.ToEcef(x, y, z));
        return new((float)wx, (float)wy, (float)wz);
    }

    internal bool HeightAt(Vector3 worldPos, out float height)
    {
        height = 0f;
        if (!LeafAt(worldPos, out var leaf, out double x, out _, out double z)) return false;
        double h = CubeGeometry.MeshHeightAt(leaf.Vertices, leaf.Side, x, z, ref leaf.Hint);
        if (double.IsNaN(h)) return false;
        height = ToWorld(leaf, x, h, z).Y;
        return true;
    }

    /// <summary>
    /// The ground's ellipsoidal height under an Earth position on the loaded leaves (its own height ignored), false off
    /// them: in doubles from the leaf's anchor frame, never through the world's floats (#1513: the server's line of sight).
    /// Main thread.
    /// </summary>
    public bool EllipsoidalHeightAt(EcefPos p, out double height)
    {
        height = 0;
        if (!_near.TryGetValue(CubeKey.FromEcef(p, LeafFrames.LeafLevel), out var leaf)) return false;
        var (x, _, z) = leaf.Anchor.ToLocal(p);
        double h = CubeGeometry.MeshHeightAt(leaf.Vertices, leaf.Side, x, z, ref leaf.Hint);
        if (double.IsNaN(h)) return false;
        height = Ellipsoid.Wgs84.ToGeodetic(leaf.Anchor.ToEcef(x, h, z)).H;
        return true;
    }

    private bool WaterAt(Vector3 worldPos, out float stillLevel, out float waveScale)
    {
        stillLevel = 0f;
        waveScale = 0f;
        if (!LeafAt(worldPos, out var leaf, out double x, out double y, out double z)) return false;
        if (leaf.Content.Water is not { } water || leaf.Lattice is not { } t) return false;
        var (i, j, _) = t.LocalToLattice(x, y, z);
        int side = water.Side, si = (int)Math.Round(i / CubeWater.Stride), sj = (int)Math.Round(j / CubeWater.Stride);
        if ((uint)si >= side || (uint)sj >= side) return false;
        int s = sj * side + si;
        float level = water.Level[s];
        if (float.IsNaN(level)) return false;
        var (lx, ly, lz) = t.LatticeToLocal(i, j, level);
        stillLevel = ToWorld(leaf, lx, ly, lz).Y;
        float bed = HeightAt(worldPos, out float g) ? g : stillLevel;
        waveScale = WaterLayer.FetchFactor((float)(water.Fetch[s] * WaterFormat.FetchUnitM)) * WaterLayer.DepthFactor(stillLevel - bed);
        return true;
    }

    private bool InTunnelAt(Vector3 worldPos) =>
        LeafAt(worldPos, out var leaf, out double x, out double y, out double z)
        && leaf.Bores is { Count: > 0 } bores && RoadTunnels.Inside(bores, x, y, z)
        && HasCollisionAt(worldPos);

    private bool CoverAt(Vector3 worldPos, out CoverClass cover)
    {
        cover = default;
        if (!LeafAt(worldPos, out var leaf, out double x, out double y, out double z)) return false;
        if (leaf.Content.Cover is not { } cells || leaf.Lattice is not { } t) return false;
        var (i, j, _) = t.LocalToLattice(x, y, z);
        int n = leaf.Side, ci = (int)Math.Round(i), cj = (int)Math.Round(j);
        if ((uint)ci >= n || (uint)cj >= n) return false;
        cover = (CoverClass)cells[cj * n + ci];
        return true;
    }

    private bool HasCollisionAt(Vector3 worldPos) =>
        _bodies.ContainsKey(CubeKey.FromEcef(Origin.ToEcef(worldPos), CollisionPatches.PatchLevel));

    private GroundTile TileAt(Vector3 worldPos) =>
        GroundTile.FromLeaf(CubeKey.FromEcef(Origin.ToEcef(worldPos), LeafFrames.LeafLevel));

    private MeshInstance3D? GroundAt(GroundTile tile) =>
        tile.IsLeaf && _near.TryGetValue(tile.Leaf, out var leaf) ? leaf.Mesh : null;

    /// <summary>The earth world's <see cref="IWorldGround"/>: every query on the loaded leaves.</summary>
    private sealed class LeafGround(EarthWorld w) : IWorldGround
    {
        public WorldOrigin? Origin => w.Origin;
        public bool TryGetHeight(Vector3 worldPos, out float height) => w.HeightAt(worldPos, out height);

        public bool TryGetSurface(Vector3 worldPos, out float height)
        {
            if (!w.HeightAt(worldPos, out height)) return false;
            if (w.WaterAt(worldPos, out float still, out _) && still > height) height = still;
            return true;
        }

        public bool TryGetCover(Vector3 worldPos, out CoverClass cover) => w.CoverAt(worldPos, out cover);
        public bool TryGetWaterLevel(Vector3 worldPos, out float stillLevel) => w.WaterAt(worldPos, out stillLevel, out _);
        public bool TryGetWater(Vector3 worldPos, out float stillLevel, out float waveScale) => w.WaterAt(worldPos, out stillLevel, out waveScale);
        public bool InTunnel(Vector3 worldPos) => w.InTunnelAt(worldPos);
        // building sites stand on leaves with PR 2
        public bool InPit(Vector3 worldPos) => false;
        public bool FloorBelow(Node3D body, Vector3 worldPos, Rid self, float depth = 4f) => ChunkManager.RayFloorBelow(body, worldPos, self, depth);
        public bool HasCollisionAt(Vector3 worldPos) => w.HasCollisionAt(worldPos);
        // no buildings on leaves until PR 2: nothing to wait for
        public bool BuildingCollisionDoneAt(Vector3 worldPos) => w.HasCollisionAt(worldPos);
        public GroundTile TileAt(Vector3 worldPos) => w.TileAt(worldPos);
        public MeshInstance3D? GroundAt(GroundTile tile) => w.GroundAt(tile);
        public void AddAnchor(Node3D anchor, bool? collision = null) => w.AddAnchor(anchor, collision);
        public void RemoveAnchor(Node3D anchor) => w.RemoveAnchor(anchor);
        public bool HasAnchor(Node3D anchor) => w.HasAnchor(anchor);

        public event Action<GroundTile>? TileEntered
        {
            add => w.TileEntered += value;
            remove => w.TileEntered -= value;
        }

        public event Action<GroundTile>? TileUnloaded
        {
            add => w.TileUnloaded += value;
            remove => w.TileUnloaded -= value;
        }

        public event Action<GroundTile, ChunkNode, Interiors.DoorSpot[]>? TileFurnished
        {
            add => w.TileFurnished += value;
            remove => w.TileFurnished -= value;
        }

        public event Action<GroundTile, ChunkNode>? TileUnfurnished
        {
            add => w.TileUnfurnished += value;
            remove => w.TileUnfurnished -= value;
        }

        // replacement comes with PR 3 (generated leaves): never raised yet
        public event Action<Func<GroundTile, bool>?>? TerrainReplaced { add { } remove { } }
    }
}
