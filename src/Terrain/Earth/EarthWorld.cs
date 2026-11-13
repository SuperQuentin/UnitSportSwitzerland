using System.Collections.Concurrent;
using System.Diagnostics;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

using UnitSport.Terrain.Leaves;

namespace UnitSport.Terrain.Earth;

/// <summary>
/// The earth world's ground (#1510, G2 C1, behind <c>--frame earth</c>): the cube leaves round the camera and the
/// streaming anchors, drawn at full resolution and asked through <see cref="Ground"/>, the same
/// <see cref="IWorldGround"/> the flat world's <see cref="ChunkManager"/> serves. The leaves are
/// <see cref="EarthLeaves"/>': the region's built ones, and round them the generated world and the fill blended into
/// them (PR 3), or a fixture course's.
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

    /// <summary>
    /// A camera or anchor higher than this streams no leaves under it, metres (#1631): from Space a leaf is under a pixel, the globe
    /// draws the ground, and a fast orbit or climb over the fill would queue hundreds of leaves (~2 s each with roads)
    /// that the ground under the landing waits behind. <see cref="Curvature.SpaceEnterM"/>, the Space regime.
    /// </summary>
    public const double CameraCeilingM = Curvature.SpaceEnterM;

    /// <summary>Main-thread time a frame spends adding what the workers built, ms (the flat world's <c>CommitBudgetMs</c>).</summary>
    public double CommitBudgetMs { get; set; } = 4;

    /// <summary>
    /// Whether leaves are meshed and drawn: off on the dedicated server (#1513), which asks the leaves' heights and stands
    /// its bots on the collision patches, but draws nothing.
    /// </summary>
    public bool BuildMeshes { get; init; } = true;

    /// <summary>Every leaf this world stands on: the region's, the fill's, a fixture course's.</summary>
    public EarthLeaves Leaves { get; }

    /// <summary>The region's built leaves (the globe draws them from afar).</summary>
    public LeafStore Store => Leaves.Real;

    public WorldOrigin Origin { get; }

    /// <summary>The world's ground: what gameplay asks.</summary>
    public IWorldGround Ground => _ground ??= new LeafGround(this);

    /// <summary>
    /// What its tiles hold by name (#1684, <see cref="IWorldGround.Content"/>): the leaves' stand-ins over the region's
    /// files, <see cref="Source"/> of the world's chunk source; set by whoever builds the world.
    /// </summary>
    public IChunkSource? Content { get; set; }

    /// <summary>The camera's position, or null: a focus of the streaming besides the anchors.</summary>
    public Func<Vector3?>? Camera { get; set; }

    private IWorldGround? _ground;
    private readonly LeafCollisionSource _collisionSource;
    private readonly CollisionPatches _patches;
    private ShaderMaterial? _material;

    private readonly Dictionary<CubeKey, Near> _near = new();
    private readonly HashSet<CubeKey> _loading = new();
    private readonly ConcurrentQueue<Near> _ready = new();
    /// <summary>Requests no focus wants any more: a worker skips them (main thread marks, workers take).</summary>
    private readonly ConcurrentDictionary<CubeKey, byte> _cancelled = new();
    /// <summary>Requests that came back with nothing (skipped, or no ground): main thread frees them from <see cref="_loading"/>.</summary>
    private readonly ConcurrentQueue<CubeKey> _abandoned = new();
    private readonly SemaphoreSlim _workers = new(Workers);
    private readonly Dictionary<CubeKey, StaticBody3D> _bodies = new();
    /// <summary>
    /// The leaf preparation each standing body was cut from (#1626): a loaded leaf answers its heights from its own, and
    /// a body cut from another is built again. Main thread; <see cref="_cutFrom"/> carries it from the patch's worker.
    /// </summary>
    private readonly Dictionary<CubeKey, LeafCollision> _bodyFrom = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Vector3[], Cut> _cutFrom = new();
    private readonly List<Node3D> _anchors = new();
    private readonly HashSet<Node3D> _collisionAnchors = new();
    private readonly List<CollisionPatches.Body> _bodyList = new();
    private readonly List<EcefPos> _foci = new();
    private readonly HashSet<CubeKey> _want = new();
    /// <summary>The leaves a plan asks for, nearest a focus first (reused).</summary>
    private readonly List<(double Distance, CubeKey Key)> _asks = new();
    private readonly Queue<CubeKey> _walk = new();
    private readonly List<CubeKey> _drop = new();
    /// <summary>Loaded leaves to read again (<see cref="Refresh"/>): each stays until its new read replaces it.</summary>
    private readonly HashSet<CubeKey> _stale = new();
    /// <summary>Bumped by each <see cref="Refresh"/>: a read begun before it is dropped as it lands.</summary>
    private volatile int _generation;
    private double _nextPlan;

    /// <summary>
    /// Main-thread time spent since <see cref="TakeSpent"/>, by part, ms (#1773): what a frame paid this world, for a probe
    /// naming its worst frames. Timestamps only, nothing allocated.
    /// </summary>
    public struct Spent
    {
        public double LeafMesh, LeafEntered, LeafFurnish, LeafDrop, Bodies, Plan, PatchPlan;
        /// <summary>Leaves added; collision bodies whole; collision pieces raised.</summary>
        public int Leaves, BodyCount, Pieces;
        public readonly double Total => LeafMesh + LeafEntered + LeafFurnish + LeafDrop + Bodies + Plan + PatchPlan;
    }

    private Spent _spent;

    /// <summary>The time spent since the last call, and the counters started again.</summary>
    public Spent TakeSpent()
    {
        var s = _spent;
        _spent = default;
        return s;
    }

    private static double Ms(long from) => Stopwatch.GetElapsedTime(from).TotalMilliseconds;

    /// <summary>
    /// The leaves changed under this world (#1655: the server's leaf index arrived, <see cref="LeafStore.UseRemote"/>, with
    /// <paramref name="changed"/> the leaves it added, dropped or rebuilt). Everything made from the old view is read again:
    /// the fill and its blend (<see cref="EarthLeaves.Refresh"/>), every loaded leaf (in place: the old one stays until the
    /// new one lands, so nobody falls through) and every collision patch (the new faces replace the old body). Main thread.
    /// </summary>
    public void Refresh(IReadOnlyCollection<CubeKey> changed)
    {
        if (changed.Count == 0) return;
        _generation++;
        Leaves.Refresh(changed);
        _collisionSource.Forget();
        foreach (var key in _near.Keys) _stale.Add(key);
        _patches.Rebuild(_ => true);
        _nextPlan = 0;
        GD.Print($"[earth] {changed.Count} leaves changed ({Store.RemoteCount} from the server): {_near.Count} loaded leaves read again");
    }

    /// <summary>
    /// What stands on some leaves changed, not their ground (#1736: the server's landings arrived, their piers): those
    /// leaves are prepared, furnished and collided again, in place (the old ones stay until the new ones land). The fill
    /// and its blend are kept, unlike <see cref="Refresh"/>. Main thread.
    /// </summary>
    public void RefreshFurniture(IReadOnlyCollection<CubeKey> leaves)
    {
        if (leaves.Count == 0) return;
        var set = leaves.ToHashSet();
        _collisionSource.Forget(set);
        foreach (var key in _near.Keys)
            if (set.Contains(key)) _stale.Add(key);
        _patches.Rebuild(p => set.Contains(LeafCollision.Split(p).Leaf));
        _nextPlan = 0;
    }

    /// <summary>
    /// The group of the leaves' collision patches (their ground with its roads and buildings): what a climb reads the cover
    /// of, as a flat chunk's own body (#1649).
    /// </summary>
    public const string GroundGroup = "leaf_ground";

    public event Action<GroundTile>? TileEntered;
    public event Action<GroundTile>? TileUnloaded;

    /// <summary>A loaded leaf: its content, its mesh (blended) in its anchor frame, its node once drawn.</summary>
    private sealed class Near
    {
        public required CubeKey Key;
        /// <summary>The <see cref="Refresh"/> it was read under.</summary>
        public int Generation;
        public required CubeLeafContent Content;
        public required EnuFrame Anchor;
        public required float[] Vertices;
        /// <summary>What its vertices and its patches come from, pinned in the collision source while it is loaded (#1626).</summary>
        public required LeafCollision Prepared;
        public required int Side;
        public required Godot.Collections.Array? Arrays;
        public List<(float[] Points, int Count, float Half, float Height)>? Bores;
        /// <summary>The building sites' pits in its anchor frame (#1653), null without any.</summary>
        public List<Construction.PitCut>? Pits;
        public LeafTransform? Lattice;
        /// <summary>The world it is loaded in: a thread's recent leaves (<see cref="t_recent"/>) are told apart by it.</summary>
        public EarthWorld? World;
        /// <summary>Unloaded (<see cref="Remove"/>): a thread that still holds it in its recent leaves drops it.</summary>
        public volatile bool Gone;
        /// <summary>Its last view from the origin (<see cref="ViewOf"/>), swapped whole by whichever thread re-anchors it first.</summary>
        public volatile LeafView? View;
        public MeshInstance3D? Mesh;
        public Furniture? Furniture;
        public ChunkNode? Node;
    }

    public EarthWorld(EarthLeaves leaves, WorldOrigin origin)
    {
        Leaves = leaves;
        Origin = origin;
        Name = "Earth";
        // the stadiums (#1723) on the ground the leaves draw, built leaf by leaf in the collision and the furniture alike
        _collisionSource = new LeafCollisionSource(leaves.Get)
        {
            Stadiums = Stadiums.StadiumLeaves.OnLeaves(leaves.Get),
            // the piers and jetty decks (#1736) from the region's landings, as the flat world's tiles build them
            Piers = new LeafPiers(() => World.Landings.Current),
        };
        _patches = new CollisionPatches(CutPatch)
        {
            Failed = (key, e) => GD.PushError($"[earth] collision patch {key}: {e.Message}"),
        };
    }

    /// <summary>
    /// The leaves' ground material: the style kit's terrain one, as the flat world's tiles (#1634), restyled with them.
    /// Its own, not the flat streamer's: that one dissolves at the flat tiles' square (<c>flat_edge</c>). Main thread.
    /// </summary>
    public ShaderMaterial GroundMaterial => _material ??= NewGroundMaterial();

    private static readonly StringName GivesWay = "gives_way";

    private static ShaderMaterial NewGroundMaterial()
    {
        var m = Styles.StyleKit.Material(Styles.MaterialRole.Terrain);
        // the leaves are the globe's last level: they never dissolve into it as the flat tiles do
        m.SetShaderParameter(GivesWay, false);
        return m;
    }

    public override void _Ready()
    {
        GD.Print($"[earth] {Store.Count} leaves from {Store.Dir}; near {NearM:F0} m, collision {CollisionReach.RadiusM:F0} m"
            + (Leaves.Domain is { } fill ? $"; the fill round them ({fill.Discs.Count} disc(s) of {LeafFillDomain.RadiusM / 1000:F0} km and the region's)" : "")
            + (Leaves.IsFixture ? "; a fixture course" : ""));
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
        if ((collision ?? anchor is PhysicsBody3D) && _collisionAnchors.Add(anchor)) _nextPatchPlan = 0;
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
    public bool Idle => _loading.Count == 0 && _ready.IsEmpty && !_patches.Busy && !_patches.HasReady && _raising.Count == 0;

    /// <summary>What the world is busy with, for a log line when something waited on <see cref="Idle"/> too long.</summary>
    public string Status => $"{_loading.Count} leaves loading ({_cancelled.Count} skipped), {_ready.Count} to add, {_near.Count} loaded, "
        + $"{_anchors.Count} anchors, collision {(_patches.Busy ? "busy" : "idle")}, {_raising.Count} patches standing up";

    public override void _Process(double delta)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalMilliseconds < CommitBudgetMs && _ready.TryDequeue(out var leaf)) Add(leaf);
        while (_abandoned.TryDequeue(out var gone))
        {
            _loading.Remove(gone);
            _cancelled.TryRemove(gone, out _);
            // a leaf to load again that the region no longer holds (the server's index dropped it): its old one goes
            if (_stale.Contains(gone) && !Leaves.Has(gone)) Remove(gone);
        }

        long raise = Stopwatch.GetTimestamp();
        RaiseBodies();
        _spent.Bodies += Ms(raise);

        _nextPlan -= delta;
        if (_nextPlan > 0) return;
        _nextPlan = 0.2;
        long t = Stopwatch.GetTimestamp();
        Plan();
        _spent.Plan += Ms(t);
    }

    /// <summary>
    /// Seconds to the next collision plan (<see cref="CollisionPatches.Update"/>): it only notices which patches the bodies
    /// want (300 m round, 400 m to release, 10 s ahead), so 10 Hz is enough (#1773: on every physics tick it cost 2 to
    /// 4 ms a frame). A new anchor plans at once.
    /// </summary>
    private double _nextPatchPlan;
    private const double PatchPlanSeconds = 0.1;

    public override void _PhysicsProcess(double delta)
    {
        _nextPatchPlan -= delta;
        if (_nextPatchPlan <= 0)
        {
            _nextPatchPlan = PatchPlanSeconds;
            long t0 = Stopwatch.GetTimestamp();
            PlanPatches();
            _spent.PatchPlan += Ms(t0);
        }

        // the finished patches start standing up (RaiseBodies, under a budget); the released ones go
        long t1 = Stopwatch.GetTimestamp();
        while (_patches.TryTakeReady(out var key, out var faces)) AddBody(key, faces);
        while (_patches.TryTakeReleased(out var gone))
        {
            if (_raising.Remove(gone, out var raising)) raising.Body.QueueFree();
            if (_bodies.Remove(gone, out var body)) body.QueueFree();
            _bodyFrom.Remove(gone);
        }
        _spent.Bodies += Ms(t1);
    }

    /// <summary>The collision anchors and where they are headed, and the patches they want.</summary>
    private void PlanPatches()
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
    }

    /// <summary>Which leaves are wanted: every leaf within <see cref="NearM"/> of a focus, by a walk over cube neighbours.</summary>
    private void Plan()
    {
        // the debug menu's freeze: what is loaded stays, nothing more is asked for
        if (Frozen) return;
        _foci.Clear();
        bool lifted = false;
        if (Camera?.Invoke() is { } cam && cam.IsFinite()) AddFocus(cam, ref lifted);
        // an anchor in Space (the spectator camera is one, so is a craft in orbit) streams nothing either
        foreach (var a in _anchors)
            if (IsInstanceValid(a) && a.IsInsideTree()) AddFocus(a.GlobalPosition, ref lifted);
        // no focus at all (between a probe's bodies): keep what is loaded; only Space lets it all go
        if (_foci.Count == 0 && !lifted) return;

        _want.Clear();
        foreach (var focus in _foci)
        {
            _walk.Clear();
            var start = CubeKey.FromEcef(focus, LeafFrames.LeafLevel);
            // the fill reaches wherever a focus goes (a teleport, a long flight), as the flat world's domain grows
            Leaves.Reach(start);
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
        // nearest a focus first (#1682): a fill leaf with roads takes ~2 s and they are made one at a time, so a set in
        // hash order kept the leaf under a spawn waiting behind a dozen others (40 s and more on a generated world)
        _asks.Clear();
        foreach (var key in _want)
            if ((!_near.ContainsKey(key) || _stale.Contains(key)) && Leaves.Has(key))
            {
                double nearest = double.MaxValue;
                foreach (var focus in _foci) nearest = Math.Min(nearest, Distance(key, focus));
                _asks.Add((nearest, key));
            }
        _asks.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        foreach (var (_, key) in _asks) Request(key);
        // what is still queued but no longer wanted (the camera flew on, or up into Space) is skipped by the workers
        foreach (var key in _loading)
            if (!_want.Contains(key)) _cancelled.TryAdd(key, 0);

        _drop.Clear();
        foreach (var key in _near.Keys)
        {
            double nearest = double.MaxValue;
            foreach (var focus in _foci) nearest = Math.Min(nearest, Distance(key, focus));
            if (nearest > NearM + ReleaseM) _drop.Add(key);
        }
        foreach (var key in _drop) Remove(key);
    }

    /// <summary>A focus of the streaming, unless it is in Space (<see cref="CameraCeilingM"/>): then <paramref name="lifted"/>.</summary>
    private void AddFocus(Vector3 at, ref bool lifted)
    {
        if (Origin.AltitudeOf(at) >= CameraCeilingM) lifted = true;
        else _foci.Add(Origin.ToEcef(at));
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
        // wanted again while still queued: no longer skipped
        _cancelled.TryRemove(key, out _);
        if (!_loading.Add(key)) return;
        int generation = _generation;
        _ = Task.Run(async () =>
        {
            await _workers.WaitAsync().ConfigureAwait(false);
            bool built = false;
            try
            {
                if (_cancelled.TryRemove(key, out _)) return;
                if (Load(key) is { } leaf)
                {
                    leaf.Generation = generation;
                    _ready.Enqueue(leaf);
                    built = true;
                }
                else GD.PushWarning($"[earth] {key}: no ground");
            }
            catch (Exception e)
            {
                GD.PushError($"[earth] {key}: {e.Message}");
            }
            finally
            {
                // a leaf that never comes is not loading any more: Idle may hold again, and a later plan asks again
                if (!built) _abandoned.Enqueue(key);
                _workers.Release();
            }
        });
    }

    /// <summary>A leaf read, its blended mesh made and meshed for drawing: on a worker.</summary>
    private Near? Load(CubeKey key)
    {
        if (Leaves.Get(key) is not { Heights: not null } content) return null;
        if (_collisionSource.Prepared(key) is not { } prepared) return null;
        var vertices = prepared.Vertices.ToArray();
        int side = prepared.Side;
        // drawn: the bare lattice blended under the roads at the visual clearance, as a tile's mesh (stood on: the collision's)
        var drawn = (float[])prepared.Bare.Clone();
        if (prepared.Bands is { } bands) LeafRoadBlend.Apply(drawn, bands, TerrainMeshBuilder.VisualBlendClearance);
        // the building sites' graded ground, as the collision's (#1731)
        SitePads.Apply(drawn, prepared.Stand.Pads);
        // the building sites' pits, cut into the drawn ground as into the collision (#1653)
        var pits = prepared.Stand.Pits.Count > 0 ? prepared.Stand.PitsInAnchor() : null;
        return new Near
        {
            Key = key,
            Content = content,
            Anchor = key.Anchor,
            Vertices = vertices,
            Prepared = prepared,
            Side = side,
            Arrays = BuildMeshes ? Mesh(drawn, side, content, pits) : null,
            Furniture = BuildMeshes ? Furnish(prepared.Stand, content, side) : null,
            Bores = content.Roads is { } roads ? RoadTunnels.Bores(roads) : null,
            Lattice = LeafFrames.Swiss.TransformOf(key),
            World = this,
            Pits = pits,
        };
    }

    private void Add(Near leaf)
    {
        _loading.Remove(leaf.Key);
        // cancelled while it was being built: it is here now, and the next plan drops it if nothing wants it
        _cancelled.TryRemove(leaf.Key, out _);
        // read before a Refresh: from the old view, asked again by the next plan
        if (leaf.Generation != _generation) return;
        if (_near.ContainsKey(leaf.Key))
        {
            // a leaf loaded again in place (#1655): the old one goes as the new one comes, so the ground never gaps
            if (!_stale.Contains(leaf.Key)) return;
            Remove(leaf.Key);
        }
        // patches built while this leaf had not come stood on nothing: built again now it has
        if (!Store.IsLocal(leaf.Key)) _patches.Rebuild(p => LeafCollision.Split(p).Leaf == leaf.Key, emptyOnly: true);
        _near[leaf.Key] = leaf;
        // its heights are answered from this preparation from now on: the patches are cut from it alone (#1626), and those
        // standing already from another (the source's cache dropped and the leaf prepared again from a later view of the
        // fill, its neighbours' roads) are built again
        _collisionSource.Pin(leaf.Prepared);
        int other = 0;
        foreach (var (p, from) in _bodyFrom)
            if (from.Leaf == leaf.Key && from != leaf.Prepared) other++;
        if (other > 0)
        {
            GD.Print($"[earth] {leaf.Key}: {other} patches cut from another preparation of it, built again (#1626)");
            _patches.Rebuild(p => _bodyFrom.TryGetValue(p, out var from) && from.Leaf == leaf.Key && from != leaf.Prepared);
        }
        _spent.Leaves++;
        long t = Stopwatch.GetTimestamp();
        if (leaf.Arrays is { } arrays)
        {
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays, flags: LeafPattern.Flags);
            leaf.Mesh = new MeshInstance3D { Name = leaf.Key.FileStem, Mesh = mesh, MaterialOverride = GroundMaterial };
            leaf.Arrays = null;
            AddChild(leaf.Mesh);
            leaf.Mesh.Transform = LeafToWorld(leaf.Anchor);
        }
        _spent.LeafMesh += Ms(t);
        t = Stopwatch.GetTimestamp();
        TileEntered?.Invoke(GroundTile.FromLeaf(leaf.Key));
        _spent.LeafEntered += Ms(t);
        t = Stopwatch.GetTimestamp();
        if (leaf.Furniture is { } f)
        {
            leaf.Node = Commit(leaf.Key, f);
            leaf.Furniture = null;
        }
        if (_debugHidden != TileLayers.None || _hideReal || _hideFill) ApplyDebugHidden(leaf);
        _spent.LeafFurnish += Ms(t);
    }

    private void Remove(CubeKey key)
    {
        _stale.Remove(key);
        if (!_near.Remove(key, out var leaf)) return;
        leaf.Gone = true;
        long t = Stopwatch.GetTimestamp();
        if (_collisionSource.PinnedOf(key) == leaf.Prepared) _collisionSource.Unpin(key);
        leaf.Mesh?.QueueFree();
        if (leaf.Node != null) Unfurnish(key, leaf.Node);
        TileUnloaded?.Invoke(GroundTile.FromLeaf(key));
        _spent.LeafDrop += Ms(t);
    }

    /// <summary>
    /// Every standing patch built again, the produced leaves (<see cref="EarthLeaves.DropMade"/>) and the preparations of
    /// the leaves not loaded (<see cref="LeafCollisionSource.Trim"/>) dropped first, as their trims do on a long journey:
    /// <c>--earthcheck</c>'s stress of #1626. The count of standing patches. Main thread.
    /// </summary>
    internal int RecutPatches()
    {
        Leaves.DropMade();
        _collisionSource.Trim();
        _patches.Rebuild(_ => true);
        return _bodies.Count;
    }

    /// <summary>No patch waiting, building, built and not yet standing, or standing up piece by piece.</summary>
    internal bool CollisionSettled => !_patches.Busy && !_patches.HasReady && _raising.Count == 0;

    /// <summary>
    /// Triangles in one piece of a patch's collision (#1773): Jolt builds a trimesh as its body enters the tree, ~1.7 µs a
    /// triangle on the main thread, and a whole patch (33k to 86k triangles) cost 50 to 170 ms in one frame. A piece is
    /// 2 to 3 ms, raised under <see cref="CollisionBudgetMs"/>.
    /// </summary>
    internal const int PieceTriangles = 1536;

    /// <summary>Main-thread time a frame spends raising collision pieces, ms (at least one piece a frame while any wait).</summary>
    public double CollisionBudgetMs { get; set; } = 3;

    /// <summary>
    /// A patch's collision cut on its worker (#1773): the ground in strips of lattice rows (it comes first, row by row), the
    /// roads and pits and the buildings each in squares of their triangles' middles, a piece about
    /// <see cref="PieceTriangles"/>.
    /// </summary>
    private sealed record Cut(LeafCollision? From, Vector3[][] Ground, Vector3[][] Features, Vector3[][] Shells);

    /// <summary>A patch body standing up piece by piece; it replaces the patch's old body only once it is whole.</summary>
    private sealed class Raising(StaticBody3D body, Cut cut)
    {
        public readonly StaticBody3D Body = body;
        public readonly Cut Cut = cut;
        public StaticBody3D? Shells;
        /// <summary>Pieces raised so far, ground then features then shells.</summary>
        public int Next;
        public int Count => Cut.Ground.Length + Cut.Features.Length + Cut.Shells.Length;
    }

    private readonly Dictionary<CubeKey, Raising> _raising = new();

    /// <summary>A patch's faces, on a patch worker, cut into pieces for <see cref="AddBody"/> with the preparation they came from.</summary>
    private Vector3[]? CutPatch(CubeKey key)
    {
        var faces = _collisionSource.Build(key, out var from);
        if (faces is { Length: >= 3 }) _cutFrom.AddOrUpdate(faces, CutUp(key, faces, from));
        return faces;
    }

    private static Cut CutUp(CubeKey key, Vector3[] faces, LeafCollision? from)
    {
        int features = from?.FeatureVertices(key) ?? 0;
        int ground = faces.Length - features;
        var shells = Squares(from?.Buildings(key));
        // one role only: the ground's when the patch has no features, else theirs (its ground all holes)
        if (features <= 0) return new(from, Strips(faces, 0, faces.Length), [], shells);
        if (ground < 3) return new(from, [], Squares(faces), shells);
        return new(from, Strips(faces, 0, ground), Squares(faces[ground..]), shells);
    }

    /// <summary>Consecutive runs of <see cref="PieceTriangles"/>: the ground's triangles come row by row, so a run is a strip.</summary>
    private static Vector3[][] Strips(Vector3[] faces, int from, int to)
    {
        int per = PieceTriangles * 3, count = (to - from + per - 1) / per;
        var strips = new Vector3[count][];
        for (int s = 0; s < count; s++)
        {
            int a = from + s * per;
            strips[s] = faces[a..Math.Min(to, a + per)];
        }
        return strips;
    }

    /// <summary>Triangles sorted into a k × k grid of their middles over their own extent (x and z of the anchor frame).</summary>
    private static Vector3[][] Squares(Vector3[]? faces)
    {
        if (faces is not { Length: >= 3 }) return [];
        int tris = faces.Length / 3;
        if (tris <= PieceTriangles) return [faces];
        float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
        for (int t = 0; t < tris; t++)
        {
            var m = faces[3 * t] + faces[3 * t + 1] + faces[3 * t + 2];
            x0 = Math.Min(x0, m.X); x1 = Math.Max(x1, m.X); z0 = Math.Min(z0, m.Z); z1 = Math.Max(z1, m.Z);
        }
        int k = (int)Math.Ceiling(Math.Sqrt((double)tris / PieceTriangles));
        float sx = k / Math.Max(1e-3f, x1 - x0), sz = k / Math.Max(1e-3f, z1 - z0);
        var cell = new int[tris];
        var counts = new int[k * k];
        for (int t = 0; t < tris; t++)
        {
            var m = faces[3 * t] + faces[3 * t + 1] + faces[3 * t + 2];
            int i = Math.Clamp((int)((m.X - x0) * sx), 0, k - 1), j = Math.Clamp((int)((m.Z - z0) * sz), 0, k - 1);
            counts[cell[t] = i + j * k]++;
        }
        var pieces = new Vector3[k * k][];
        var fill = new int[k * k];
        for (int c = 0; c < pieces.Length; c++) pieces[c] = new Vector3[counts[c] * 3];
        for (int t = 0; t < tris; t++)
        {
            int c = cell[t], at = fill[c]++ * 3;
            pieces[c][at] = faces[3 * t];
            pieces[c][at + 1] = faces[3 * t + 1];
            pieces[c][at + 2] = faces[3 * t + 2];
        }
        // a crowded square (a town block's roofs) stays one piece: still far smaller than the patch
        return Array.FindAll(pieces, p => p.Length > 0);
    }

    private static readonly StringName GroundShapes = "ground_shapes";

    /// <summary>
    /// Whether a ray's hit (its <c>collider</c> and <c>shape</c>) is a leaf's ground, not a road or building on it (#1626: the
    /// ground off the height query is a fault, a roof over it is not): a patch body holds its ground's pieces first.
    /// </summary>
    public static bool IsGroundHit(GodotObject? collider, int shape) =>
        collider is StaticBody3D body && body.IsInGroup(GroundGroup) && body.HasMeta(GroundShapes)
        && shape < body.GetMeta(GroundShapes).AsInt32();

    /// <summary>A patch's new faces: its body starts standing up, piece by piece (<see cref="RaiseBodies"/>).</summary>
    private void AddBody(CubeKey key, Vector3[]? faces)
    {
        // a body still standing up for this patch: these faces are newer
        if (_raising.Remove(key, out var stale)) stale.Body.QueueFree();
        var (leaf, _, _) = LeafCollision.Split(key);
        if (faces is not { Length: >= 3 } || !_cutFrom.TryGetValue(faces, out var cut))
        {
            // no ground (yet): the old body goes
            if (_bodies.Remove(key, out var old)) old.QueueFree();
            _bodyFrom.Remove(key);
            return;
        }
        _cutFrom.Remove(faces);
        if (cut.From is { } from)
        {
            _bodyFrom[key] = from;
            // cut from another preparation than the loaded leaf answers from (begun before it was pinned): built again
            if (_near.TryGetValue(leaf, out var near) && near.Prepared != from)
            {
                GD.Print($"[earth] {key}: cut from another preparation of {leaf} than it answers from, built again (#1626)");
                _patches.Rebuild(p => p == key);
            }
        }
        else _bodyFrom.Remove(key);
        var body = new StaticBody3D { Name = key.FileStem };
        body.AddToGroup(GroundGroup);
        body.SetMeta(GroundShapes, cut.Ground.Length);
        AddChild(body);
        body.Transform = LeafToWorld(leaf.Anchor);
        _raising[key] = new Raising(body, cut);
    }

    /// <summary>
    /// Collision pieces raised under <see cref="CollisionBudgetMs"/>, the patch nearest a body first; a whole body replaces
    /// the patch's old one in the same frame, so a rebuilt patch never gaps under a walker (#1773). Main thread.
    /// </summary>
    private void RaiseBodies()
    {
        if (_raising.Count == 0) return;
        long start = Stopwatch.GetTimestamp();
        do
        {
            CubeKey key = default;
            Raising? next = null;
            double nearest = double.MaxValue;
            foreach (var (k, r) in _raising)
            {
                double d = double.MaxValue;
                var c = k.Anchor.Origin;
                foreach (var b in _bodyList) d = Math.Min(d, Squared(c, b.At));
                if (next == null || d < nearest) { key = k; next = r; nearest = d; }
            }
            RaisePiece(next!);
            _spent.Pieces++;
            if (next!.Next < next.Count) continue;
            _raising.Remove(key);
            if (_bodies.Remove(key, out var old)) old.QueueFree();
            _bodies[key] = next.Body;
            _spent.BodyCount++;
        }
        while (_raising.Count > 0 && Ms(start) < CollisionBudgetMs);

        static double Squared(EcefPos a, EcefPos b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }
    }

    /// <summary>One piece of a body: a trimesh shape added to it in the tree, built by Jolt there and then.</summary>
    private static void RaisePiece(Raising r)
    {
        var cut = r.Cut;
        int i = r.Next++;
        if (i < cut.Ground.Length + cut.Features.Length)
        {
            var faces = i < cut.Ground.Length ? cut.Ground[i] : cut.Features[i - cut.Ground.Length];
            r.Body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = faces } });
            return;
        }
        // its buildings in a body of their own, placed with it (#1704): an open doorway lets a walker through them alone
        if (r.Shells == null)
        {
            r.Shells = new StaticBody3D { Name = BuildingBodyName };
            r.Body.AddChild(r.Shells);
        }
        r.Shells.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = cut.Shells[i - cut.Ground.Length - cut.Features.Length] } });
    }

    private const string BuildingBodyName = "Buildings";

    /// <summary>
    /// The buildings' collision bodies of the patches within <paramref name="radius"/> m of a world point (#1704): what an
    /// open doorway lets a walker through (<c>InteriorManager.BuildingBodies</c>), as a flat tile's <c>BuildingBody</c>.
    /// </summary>
    public void BuildingBodiesNear(Vector3 world, List<StaticBody3D> into, double radius = 30)
    {
        foreach (var key in CollisionPatches.Near(Origin.ToEcef(world), radius).Keys)
            if (_bodies.TryGetValue(key, out var body) && body.GetNodeOrNull<StaticBody3D>(BuildingBodyName) is { } shells) into.Add(shells);
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
        foreach (var (key, raising) in _raising)
            raising.Body.Transform = LeafToWorld(LeafCollision.Split(key).Leaf.Anchor);
        // what the flat streamer keeps in world space: the door index, the building shader's door boxes
        Flat?.OnOriginShifted(shift);
    }

    /// <summary>
    /// A leaf's mesh for drawing, every lattice vertex (the collision's), hole quads left out, for the style kit's
    /// terrain material (#1634) as a flat tile's: coloured by its cover through <see cref="CoverPalette"/> at the
    /// vertex's altitude, blended with differing neighbours (<see cref="TerrainMeshBuilder"/>'s boundary blend), in
    /// linear colour, the pattern code in alpha; the leaf's pattern points in CUSTOM1/CUSTOM2 (<see cref="LeafPattern"/>).
    /// Triangles split as the collision's: along (i, j)–(i+1, j+1), front faces up. The building sites' pits are cut
    /// out and lined (<see cref="Construction.PitTerrain.CutMesh"/>, #1653), as the collision's patches are.
    /// </summary>
    private static Godot.Collections.Array? Mesh(float[] v, int n, CubeLeafContent content, List<Construction.PitCut>? pits = null)
    {
        var pos = new Vector3[n * n];
        for (int k = 0; k < pos.Length; k++) pos[k] = new Vector3(v[3 * k], v[3 * k + 1], v[3 * k + 2]);
        var normals = new Vector3[n * n];
        var colours = new Color[n * n];
        var cover = content.Cover is { } cells && cells.Length >= n * n ? cells : null;
        // the anchor is on the ellipsoid: a height in its frame less the geoid is an altitude (the palette bands by it)
        float geoid = (float)-SwissGeodesy.FromEcef(content.Key.Anchor.Origin).H;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int k = j * n + i;
                var normal = BoxNormal(pos, n, i, j);
                // a no-data (NaN) neighbour at the leaf set's edge: up, not a NaN normal (85k warnings a drive, #1512)
                normal = normal.IsFinite() && normal.LengthSquared() > 0 ? normal.Normalized() : Vector3.Up;
                normals[k] = normal.Y < 0 ? -normal : normal;
                float alt = float.IsNaN(pos[k].Y) ? 0f : pos[k].Y - geoid;
                colours[k] = ColourAt(cover, n, i, j, alt).SrgbToLinear();
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
        int[] index = indices.ToArray();
        if (pits is { Count: > 0 })
            (pos, colours, normals, index) = Construction.PitTerrain.CutMesh(pos, colours, normals, index, pits);

        // patterns (cover patterns, Cartoon's paint, Realistic's textures) from the leaf's place on the Earth (#1512):
        // the pattern is baked in the stand-in tile's frame, the anchor's shifted
        var standIn = new Vector3[pos.Length];
        var shift = new Vector3((float)LeafStandIn.Offset, 0, (float)LeafStandIn.Offset);
        for (int k = 0; k < pos.Length; k++) standIn[k] = pos[k] + shift;
        var (custom1, custom2) = LeafPattern.For(content.Key).Bake(standIn);

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = pos;
        arrays[(int)Godot.Mesh.ArrayType.Normal] = normals;
        arrays[(int)Godot.Mesh.ArrayType.Color] = colours;
        arrays[(int)Godot.Mesh.ArrayType.Custom1] = custom1;
        arrays[(int)Godot.Mesh.ArrayType.Custom2] = custom2;
        arrays[(int)Godot.Mesh.ArrayType.Index] = index;
        return arrays;
    }

    /// <summary>How far either side of a vertex its normal's box of slopes reaches, lattice steps (~1.2 m each): the flat
    /// world's ±3 m (<c>TerrainMeshBuilder.SmoothNormal</c>, #883), or a cliff's facets light up as vertical stripes.</summary>
    private const int NormalReach = 3;

    /// <summary>
    /// The normal at lattice vertex (i, j) from the slopes across ±<see cref="NormalReach"/> on three rows and three
    /// columns through it, as <c>TerrainMeshBuilder.SmoothNormal</c> does on a tile, in 3-D (the lattice is not square
    /// in the anchor frame). Clamped at the leaf's edges; a NaN (no data) slope is left out.
    /// </summary>
    private static Vector3 BoxNormal(Vector3[] pos, int n, int i, int j)
    {
        int i0 = Math.Max(i - NormalReach, 0), i1 = Math.Min(i + NormalReach, n - 1);
        int j0 = Math.Max(j - NormalReach, 0), j1 = Math.Min(j + NormalReach, n - 1);
        Vector3 dx = Vector3.Zero, dz = Vector3.Zero;
        ReadOnlySpan<int> rows = [j0, j, j1], cols = [i0, i, i1];
        foreach (int jj in rows)
            if (pos[jj * n + i1] - pos[jj * n + i0] is var d && d.IsFinite()) dx += d;
        foreach (int ii in cols)
            if (pos[j1 * n + ii] - pos[j0 * n + ii] is var d && d.IsFinite()) dz += d;
        return dz.Cross(dx);
    }

    /// <summary>
    /// A lattice vertex's ground colour (sRGB), as <see cref="TerrainMeshBuilder"/>'s boundary blend at stride 1: its
    /// cover's, averaged with each 4-neighbour's of another class, so a class edge is a soft step, not a hard line.
    /// </summary>
    internal static Color ColourAt(byte[]? cover, int n, int i, int j, float alt)
    {
        if (cover == null) return CoverPalette.ColorFor(CoverClass.Open, alt, CoverPalette.Hash(i, j));
        byte centre = cover[j * n + i];
        var sum = CoverPalette.ColorFor((CoverClass)centre, alt, CoverPalette.Hash(i, j));
        int count = 1;
        for (int q = 0; q < 4; q++)
        {
            int ni = i + (q == 0 ? 1 : q == 1 ? -1 : 0), nj = j + (q == 2 ? 1 : q == 3 ? -1 : 0);
            if ((uint)ni >= n || (uint)nj >= n) continue;
            byte other = cover[nj * n + ni];
            if (other == centre) continue;
            sum += CoverPalette.ColorFor((CoverClass)other, alt, CoverPalette.Hash(ni, nj));
            count++;
        }
        return count == 1 ? sum : sum / count;
    }

    // --- the queries -------------------------------------------------------------------------------------------

    /// <summary>The leaves a thread asked last (#1751), most recent first: successive queries are nearly always on one of them.</summary>
    [ThreadStatic] private static Near?[]? t_recent;

    private const int RecentLeaves = 4;

    /// <summary>
    /// How far inside a recent leaf's lattice (cells, ~1.2 m each) a point must be for that leaf to answer without asking
    /// the cube key (#1751). It covers the affine lattice guess (a few cm) and the key's other projection: the key follows
    /// the point's geodetic normal down, the mesh lookup the anchor's up, which part by height × the normals' tilt
    /// (≤ 0.07 mrad at a corner: 0.7 m at <see cref="RecentMaxHeight"/>).
    /// </summary>
    private const double RecentMargin = 4;

    /// <summary>Above this height over the anchor the recent leaves are not trusted (space): the cube key decides.</summary>
    private const double RecentMaxHeight = 10_000;

    /// <summary>
    /// A loaded leaf seen from one origin frame (#1751): world → anchor frame, world → lattice guess and anchor frame →
    /// world height composed into plain affine rows, so a query is a few dot products instead of the world → ECEF →
    /// anchor round trip and back. Immutable; made again when the origin re-anchors (once a leaf every 3 km).
    /// </summary>
    private sealed class LeafView
    {
        public readonly FrameAxes Axes;
        // world (x, y, z) -> the anchor frame's x, y, z
        public readonly double X0, Xx, Xy, Xz, Y0, Yx, Yy, Yz, Z0, Zx, Zy, Zz;
        // world -> the lattice guess (LeafTransform.LocalToLatticeGuess)
        public readonly double I0, Ix, Iy, Iz, J0, Jx, Jy, Jz;
        // the anchor frame's (x, y, z) -> world y
        public readonly double W0, Wx, Wy, Wz;
        /// <summary>The highest lattice coordinate well inside (<see cref="RecentMargin"/>).</summary>
        public readonly double Hi;

        public LeafView(EnuFrame anchor, LeafTransform lattice, FrameAxes axes)
        {
            Axes = axes;
            EcefPos e = anchor.East, u = anchor.Up, n = anchor.North;
            var o = new EcefPos(axes.Origin.X - anchor.Origin.X, axes.Origin.Y - anchor.Origin.Y, axes.Origin.Z - anchor.Origin.Z);
            (X0, Xx, Xy, Xz) = (Dot(e, o), Dot(e, axes.X), Dot(e, axes.Y), Dot(e, axes.Z));
            (Y0, Yx, Yy, Yz) = (Dot(u, o), Dot(u, axes.X), Dot(u, axes.Y), Dot(u, axes.Z));
            (Z0, Zx, Zy, Zz) = (-Dot(n, o), -Dot(n, axes.X), -Dot(n, axes.Y), -Dot(n, axes.Z));
            var g0 = lattice.LocalToLatticeGuess(0, 0);
            var gx = lattice.LocalToLatticeGuess(1, 0);
            var gz = lattice.LocalToLatticeGuess(0, 1);
            double ix = gx.I - g0.I, iz = gz.I - g0.I, jx = gx.J - g0.J, jz = gz.J - g0.J;
            (I0, Ix, Iy, Iz) = (g0.I + ix * X0 + iz * Z0, ix * Xx + iz * Zx, ix * Xy + iz * Zy, ix * Xz + iz * Zz);
            (J0, Jx, Jy, Jz) = (g0.J + jx * X0 + jz * Z0, jx * Xx + jz * Zx, jx * Xy + jz * Zy, jx * Xz + jz * Zz);
            // world y = Y·(A + E x + U y − N z − O)
            (W0, Wx, Wy, Wz) = (-Dot(axes.Y, o), Dot(axes.Y, e), Dot(axes.Y, u), -Dot(axes.Y, n));
            Hi = lattice.Cells - RecentMargin;
        }

        private static double Dot(EcefPos a, EcefPos b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }

    /// <summary>A leaf's view from these axes: its last one, or a new one after a re-anchor. Any thread.</summary>
    private static LeafView? ViewOf(Near leaf, FrameAxes axes)
    {
        if (leaf.View is { } v && ReferenceEquals(v.Axes, axes)) return v;
        if (leaf.Lattice is not { } t) return null;
        return leaf.View = new LeafView(leaf.Anchor, t, axes);
    }

    /// <summary>The loaded leaf under a world point, and the point in its anchor frame; false off the loaded leaves.</summary>
    private bool LeafAt(Vector3 worldPos, out Near leaf, out double x, out double y, out double z) =>
        Locate(worldPos, out leaf, out _, out x, out y, out z, out _, out _);

    /// <summary>
    /// <see cref="LeafAt"/>, and the leaf's view from the current frame (null in an LV95 frame) and the lattice guess
    /// (NaN when not known). A thread's recent leaf answers when the point is well inside it (<see cref="RecentMargin"/>,
    /// <see cref="RecentMaxHeight"/>): the leaf the cube key names, without the key's geodetic round trip (#1751).
    /// </summary>
    private bool Locate(Vector3 worldPos, out Near leaf, out LeafView? view, out double x, out double y, out double z, out double i, out double j)
    {
        var axes = Origin.Frame.Axes;
        var recent = t_recent ??= new Near?[RecentLeaves];
        if (axes != null)
        {
            double wx = worldPos.X, wy = worldPos.Y, wz = worldPos.Z;
            for (int k = 0; k < RecentLeaves; k++)
            {
                if (recent[k] is not { } r) break;
                if (r.World != this || r.Gone || ViewOf(r, axes) is not { } v) continue;
                i = v.I0 + v.Ix * wx + v.Iy * wy + v.Iz * wz;
                j = v.J0 + v.Jx * wx + v.Jy * wy + v.Jz * wz;
                if (!(i >= RecentMargin && i <= v.Hi && j >= RecentMargin && j <= v.Hi)) continue;
                y = v.Y0 + v.Yx * wx + v.Yy * wy + v.Yz * wz;
                if (Math.Abs(y) > RecentMaxHeight) continue;
                x = v.X0 + v.Xx * wx + v.Xy * wy + v.Xz * wz;
                z = v.Z0 + v.Zx * wx + v.Zy * wy + v.Zz * wz;
                for (int m = k; m > 0; m--) recent[m] = recent[m - 1];
                recent[0] = r;
                leaf = r;
                view = v;
                return true;
            }
        }

        i = j = double.NaN;
        var ecef = Origin.ToEcef(worldPos);
        if (!_near.TryGetValue(CubeKey.FromEcef(ecef, LeafFrames.LeafLevel), out leaf!))
        {
            x = y = z = 0;
            view = null;
            return false;
        }
        (x, y, z) = leaf.Anchor.ToLocal(ecef);
        view = axes != null ? ViewOf(leaf, axes) : null;
        // newest first; a leaf already held moves to the front, the oldest (or a gone one) falls off
        int at = RecentLeaves - 1;
        for (int k = 0; k < RecentLeaves; k++)
            if (recent[k] == leaf || recent[k] == null || recent[k]!.Gone) { at = k; break; }
        for (int m = at; m > 0; m--) recent[m] = recent[m - 1];
        recent[0] = leaf;
        return true;
    }

    /// <summary>
    /// The leaf mesh's height under a local (x, z), NaN off it: the walk starts at the quad of the lattice guess (i, j)
    /// (#1751; worked out here when NaN), so it ends in a step or two whoever asked before, and no state is shared between
    /// threads.
    /// </summary>
    private static double MeshHeight(Near leaf, double x, double z, double i = double.NaN, double j = double.NaN)
    {
        if (double.IsNaN(i) && leaf.Lattice is { } t) (i, j) = t.LocalToLatticeGuess(x, z);
        int top = leaf.Side - 2;
        // NaN-safe: a NaN fails both tests and starts at 0
        var hint = (i > 0 ? (int)Math.Min(i, top) : 0, j > 0 ? (int)Math.Min(j, top) : 0);
        return CubeGeometry.MeshHeightAt(leaf.Vertices, leaf.Side, x, z, ref hint);
    }

    /// <summary>
    /// <see cref="HeightAt"/> the way it was before #1751, for <c>--worldgroundcheck</c>'s parity: always through the cube
    /// key and the ECEF round trip, the mesh walked from its corner.
    /// </summary>
    internal bool HeightAtByKey(Vector3 worldPos, out float height)
    {
        height = 0f;
        var ecef = Origin.ToEcef(worldPos);
        if (!_near.TryGetValue(CubeKey.FromEcef(ecef, LeafFrames.LeafLevel), out var leaf)) return false;
        var (x, _, z) = leaf.Anchor.ToLocal(ecef);
        var hint = (0, 0);
        double h = CubeGeometry.MeshHeightAt(leaf.Vertices, leaf.Side, x, z, ref hint);
        if (double.IsNaN(h)) return false;
        height = ToWorld(leaf, x, h, z).Y;
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
        if (!Locate(worldPos, out var leaf, out var view, out double x, out _, out double z, out double i, out double j)) return false;
        double h = MeshHeight(leaf, x, z, i, j);
        if (double.IsNaN(h)) return false;
        height = view != null ? (float)(view.W0 + view.Wx * x + view.Wy * h + view.Wz * z) : ToWorld(leaf, x, h, z).Y;
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
        double h = MeshHeight(leaf, x, z);
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

    /// <summary>
    /// Down in a building site's pit (#619) on a loaded leaf (#1653): within half a metre of its rectangle, no lower than
    /// 1.5 m under its floor, as the flat world's <c>ChunkManager.InPitAt</c>; the height queries answer the ground at its top.
    /// </summary>
    private bool InPitAt(Vector3 worldPos)
    {
        if (!LeafAt(worldPos, out var leaf, out double x, out double y, out double z) || leaf.Pits is not { } pits) return false;
        var at = new Vector2((float)x, (float)z);
        foreach (var pit in pits)
            if (pit.Rect.Contains(at, 0.5f) && y > pit.Floor - 1.5f) return true;
        return false;
    }

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

    /// <summary>A loaded leaf's furnished roads mesh (in its stand-in's frame), for per-tile shader parameters (the ski slopes, #1735).</summary>
    public MeshInstance3D? RoadsAt(CubeKey leaf) => _near.TryGetValue(leaf, out var near) ? near.Node?.RoadMesh : null;

    /// <summary>The earth world's <see cref="IWorldGround"/>: every query on the loaded leaves.</summary>
    private sealed class LeafGround(EarthWorld w) : IWorldGround
    {
        public WorldOrigin? Origin => w.Origin;
        public IChunkSource? Content => w.Content;
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
        public bool PatternsOnFaces => true;
        public bool InTunnel(Vector3 worldPos) => w.InTunnelAt(worldPos);
        public bool InPit(Vector3 worldPos) => w.InPitAt(worldPos);
        public bool FloorBelow(Node3D body, Vector3 worldPos, Rid self, float depth = 4f) => ChunkManager.RayFloorBelow(body, worldPos, self, depth);
        public bool HasCollisionAt(Vector3 worldPos) => w.HasCollisionAt(worldPos);
        // no buildings on leaves until PR 2: nothing to wait for
        public bool BuildingCollisionDoneAt(Vector3 worldPos) => w.HasCollisionAt(worldPos);
        public GroundTile TileAt(Vector3 worldPos) => w.TileAt(worldPos);
        public MeshInstance3D? GroundAt(GroundTile tile) => w.GroundAt(tile);
        public void AddAnchor(Node3D anchor, bool? collision = null) => w.AddAnchor(anchor, collision);
        public void RemoveAnchor(Node3D anchor) => w.RemoveAnchor(anchor);
        public bool HasAnchor(Node3D anchor) => w.HasAnchor(anchor);
        public IReadOnlyList<Node3D> Anchors => w._anchors;

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
