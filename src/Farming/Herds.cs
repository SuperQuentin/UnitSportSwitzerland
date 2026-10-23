using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Farming;

/// <summary>
/// The cows on the pastures (#863, docs/notes/farming/cows.md), at <c>World/Herds</c> on the server
/// and on every client (RPCs route by path). Each peer puts the herds of the field tiles it holds
/// (<see cref="FarmField.TileReady"/>, <see cref="HerdPlan"/>) and works every cow out for itself
/// from the shared simulation clock (<see cref="CowMotion"/>): nothing crosses the wire for a grazing
/// herd. A tractor beam is what changes that: the cows are the server's (the game's offline), so
/// only the authority registers them with <see cref="TractorBeams"/>, moves a lifted one and sends
/// where it is (<see cref="CowAir"/>, ~10 a second) and where it was let down (<see cref="CowDown"/>);
/// a cow let down walks back to its herd (or, set down far away, stays and grazes there). A
/// joining peer gets those overrides (<see cref="SendTo"/>). Drawn as two MultiMeshes a herd (bodies,
/// heads) by the client, in <c>Herds.Draw.cs</c>.
/// </summary>
public partial class Herds : Node3D, IOriginContainer
{
    public const string NodeName = "Herds";

    /// <summary>A cow set down further than this from its herd stays where it is, m.</summary>
    public const float LostBeyond = 150f;

    public static Herds? Instance { get; private set; }

    private readonly ChunkManager? _chunks;
    private readonly WorldOrigin _origin;
    private readonly bool _dedicated;

    /// <summary>Every herd this peer holds, by tile.</summary>
    private readonly Dictionary<TileId, List<Live>> _byTile = new();
    private readonly List<Live> _all = new();
    /// <summary>Cows a beam moved, by <see cref="Key"/>: kept apart from the herds, so a herd that loads after the news still has it.</summary>
    private readonly Dictionary<long, Override> _over = new();
    private readonly List<long> _settled = new();
    private int _staged;
    private double _sendIn;

    /// <summary>A cow's state away from its pure walk: in the air (held or let down), or set down at a point at a time.</summary>
    private struct Override
    {
        public bool Air;
        public double E, N;
        public float Alt, Yaw;
        public double Since;
        public float Distance;
        public bool Dirty;
    }

    /// <summary>One herd on this peer, its cows and (on a client) its drawing.</summary>
    private sealed class Live
    {
        public required Herd Plan;
        public required Cow[] Cows;
        public MultiMeshInstance3D? Bodies, Heads;
        public float[] Ground = [];
        public Vector2[] GroundAt = [];
        public float[] HeadPitch = [], HeadYaw = [];
        /// <summary>The bodies' and heads' instance transforms, 12 floats each, sent in one call a pose.</summary>
        public float[] BodyBuffer = [], HeadBuffer = [];
        public bool[] WasAir = [];
        /// <summary>Where a cow in the air is drawn: eased toward the server's ~10 updates a second.</summary>
        public Vector3[] AirAt = [];
        public float AnchorGround = float.NaN;
        /// <summary>The time its cows' walk is read from (<see cref="CowMotion.At"/> at now minus this): 0, or a staged herd's go (<see cref="Rewind"/>).</summary>
        public double Epoch;
    }

    public Herds(ChunkManager? chunks, WorldOrigin origin, bool dedicated)
    {
        Name = NodeName;
        _chunks = chunks;
        _origin = origin;
        _dedicated = dedicated;
    }

    public Herds() : this(null, WorldOrigin.SwissDefault(), false) { }

    public static Herds Create(Node world, ChunkManager? chunks, WorldOrigin origin, bool dedicated)
    {
        var herds = new Herds(chunks, origin, dedicated);
        world.AddChild(herds);
        return herds;
    }

    public override void _EnterTree()
    {
        Instance = this;
        FarmField.TileReady += OnTileReady;
        FarmField.TileNoFields += OnTileNoFields;
        FarmField.TileGone += OnTileGone;
    }

    public override void _ExitTree()
    {
        FarmField.TileReady -= OnTileReady;
        FarmField.TileNoFields -= OnTileNoFields;
        FarmField.TileGone -= OnTileGone;
        foreach (var h in _all) foreach (var c in h.Cows) TractorBeams.Unregister(c);
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        // the tiles the farm already held before this node was there
        if (FarmField.Instance is { } farm)
            foreach (var tile in farm.LoadedData) OnTileReady(tile);
        if (!_dedicated) ReadyDraw();
    }

    // ---- who decides -----------------------------------------------------------------------------

    /// <summary>This peer moves the cows: the server, or the game offline.</summary>
    public bool Authority => _dedicated || !NetLink.Online(this);

    /// <summary>The shared simulation clock, seconds: what every peer walks the cows by.</summary>
    public static double Now => SimClock.SimAt(ClockSync.ServerNow);

    /// <summary>A cow's name on every peer: its tile, its herd's number there, its own.</summary>
    public static long Key(TileId tile, int herd, int cow) =>
        ((long)(tile.E & 0xFFFF) << 48) | ((long)(tile.N & 0xFFFF) << 32) | ((long)(herd & 0xFFFFFF) << 8) | (long)(cow & 0xFF);

    /// <summary>A staged herd's cow: negative, so it never names a cow on the wire.</summary>
    private static long StagedKey(int handle, int cow) => long.MinValue + ((long)handle << 8) + cow;

    // ---- herds in and out ------------------------------------------------------------------------

    private void OnTileReady(FieldTile tile)
    {
        if (_byTile.ContainsKey(tile.Id)) return;
        var list = new List<Live>();
        foreach (var plan in HerdPlan.For(tile)) list.Add(Add(plan));
        _byTile[tile.Id] = list;
    }

    /// <summary>A tile with no farm layer: its herds from the land cover (<see cref="HerdPlan.FromLand"/>), worked out on a worker.</summary>
    private void OnTileNoFields(TileId id)
    {
        if (_byTile.ContainsKey(id) || !_landPending.Add(id) || _chunks?.Source is not { } source) return;
        Task.Run(async () =>
        {
            try
            {
                var cover = await source.LoadCoverAsync(id).ConfigureAwait(false);
                if (cover == null) { _landDone.Enqueue((id, new List<Herd>())); return; }
                var buildings = await source.LoadBuildingsAsync(id).ConfigureAwait(false);
                var roads = await source.LoadRoadsAsync(id).ConfigureAwait(false);
                var grid = await source.LoadChunkAsync(id).ConfigureAwait(false);
                _landDone.Enqueue((id, HerdPlan.FromLand(id, cover, buildings, roads, grid is { Stride: 1 } ? grid : null)));
            }
            catch (Exception e)
            {
                GD.PushWarning($"[herds] {id}: {e.Message}");
                _landDone.Enqueue((id, new List<Herd>()));
            }
        });
    }

    private readonly HashSet<TileId> _landPending = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(TileId Id, List<Herd> Herds)> _landDone = new();

    /// <summary>Main thread: the open-land herds worked out, put in if their tile is still held.</summary>
    private void TakeLand()
    {
        while (_landDone.TryDequeue(out var done))
        {
            if (!_landPending.Remove(done.Id) || _byTile.ContainsKey(done.Id)) continue;
            var list = new List<Live>();
            foreach (var plan in done.Herds) list.Add(Add(plan));
            _byTile[done.Id] = list;
            if (done.Herds.Count > 0) GD.Print($"[herds] {done.Id}: {done.Herds.Count} herd(s) on open land (no farm layer)");
        }
    }

    private void OnTileGone(TileId id)
    {
        _landPending.Remove(id);
        if (!_byTile.Remove(id, out var list)) return;
        foreach (var h in list) Remove(h);
    }

    private Live Add(Herd plan)
    {
        var live = new Live { Plan = plan, Cows = new Cow[plan.Count] };
        for (int i = 0; i < plan.Count; i++)
        {
            live.Cows[i] = new Cow(this, live, i, plan.Staged ? StagedKey(-plan.Index - 1, i) : Key(plan.Tile, plan.Index, i));
            if (Authority) TractorBeams.Register(live.Cows[i]);
        }
        _all.Add(live);
        return live;
    }

    private void Remove(Live h)
    {
        foreach (var c in h.Cows)
        {
            TractorBeams.Unregister(c);
            // one held when its herd goes: it is set down where it is, not left hanging
            if (Authority && _over.TryGetValue(c.Key, out var o) && o.Air) Settle(c, o);
        }
        _all.Remove(h);
        h.Bodies?.QueueFree();
        h.Heads?.QueueFree();
    }

    /// <summary>
    /// The trailer director's herd (#866): <paramref name="count"/> cows of <paramref name="breed"/>
    /// grazing round an LV95 point, on this peer only, until <see cref="Unstage"/>. Returns its handle.
    /// <paramref name="seed"/> places and moves its cows; by default the run's staging count's, so a
    /// film passes one of its own (the shot's, #924): the same herd whatever was staged before.
    /// </summary>
    public int Stage(double e, double n, int count, CowBreed breed = CowBreed.Brown, float radius = 18f, uint? seed = null)
    {
        int index = _staged++;
        var plan = HerdPlan.Staged(index, e, n, Math.Clamp(count, 1, 40), breed, radius, seed ?? (uint)(index * 7 + 3));
        var id = new TileId(int.MinValue + index, 0);
        _byTile[id] = new List<Live> { Add(plan) };
        return index;
    }

    /// <summary>
    /// A staged herd's walk from its start again, from now (#924): the cows stand where they stood at
    /// time 0 whatever the clock read when it was staged, so a film's shot shows the same grazing
    /// however long its ground took to stream in. The director calls it as its actors are let go.
    /// </summary>
    public void Rewind(int handle)
    {
        if (_byTile.TryGetValue(new TileId(int.MinValue + handle, 0), out var list))
            foreach (var h in list) h.Epoch = Now;
    }

    /// <summary>A staged herd's cows where they are now, LV95 and compass yaw (rad), for the director's track dump.</summary>
    public IEnumerable<(double E, double N, float Alt, float Yaw)> StagedCows(int handle)
    {
        if (!_byTile.TryGetValue(new TileId(int.MinValue + handle, 0), out var list)) yield break;
        double now = Now;
        foreach (var h in list)
            for (int i = 0; i < h.Cows.Length; i++)
            {
                var (e, n, alt, yaw, _, _) = Where(h, i, now);
                yield return (e, n, alt, yaw);
            }
    }

    /// <summary>Takes a staged herd away again.</summary>
    public void Unstage(int handle) => OnTileGone(new TileId(int.MinValue + handle, 0));

    /// <summary>Every herd's anchor and count, for probes and bots (LV95).</summary>
    public IEnumerable<Herd> All => _all.Select(h => h.Plan);

    // ---- where a cow is --------------------------------------------------------------------------

    /// <summary>
    /// One cow now: LV95 E/N, its height above the sea if it is off the ground (NaN on it), the Godot
    /// yaw it faces, its head (0 up .. 1 grazing) and gait. Every peer the same, from the clock and
    /// the overrides it was told.
    /// </summary>
    private (double E, double N, float Alt, float Yaw, float Head, float Gait) Where(Live h, int i, double now)
    {
        var plan = h.Plan;
        var pure = CowMotion.At(plan, i, now - h.Epoch);
        double pe = plan.E + pure.Offset.X, pn = plan.N + pure.Offset.Y;
        long key = h.Cows[i].Key;
        if (!_over.TryGetValue(key, out var o)) return (pe, pn, float.NaN, pure.Yaw, pure.Head, pure.Gait);
        if (o.Air) return (o.E, o.N, o.Alt, o.Yaw, 0f, 0f);
        // set down: walks back to where its herd would have it, or (far away) grazes where it is
        if (o.Distance > LostBeyond) return (o.E, o.N, float.NaN, o.Yaw, 1f, 0f);
        float w = Mathf.Clamp((float)((now - o.Since) * CowMotion.WalkSpeed / Math.Max(o.Distance, 0.5f)), 0f, 1f);
        double e = o.E + (pe - o.E) * w, n = o.N + (pn - o.N) * w;
        float yaw = w < 1f ? CowMotion.YawOf(new Vector2((float)(pe - o.E), (float)(pn - o.N))) : pure.Yaw;
        return (e, n, float.NaN, yaw, w < 1f ? 0.15f : pure.Head, w < 1f ? 1f : pure.Gait);
    }

    /// <summary>The terrain's height at an LV95 point on this peer, NaN where it is not loaded.</summary>
    private float GroundAt(double e, double n)
    {
        if (_chunks == null) return float.NaN;
        var at = _origin.ToWorld(e, n, 0);
        if (!_dedicated && _chunks.TryGetSurface(at, out float s)) return s;
        return _chunks.TryGetHeight(at, out float h) ? h : float.NaN;
    }

    // ---- the authority: beams, and what it sends -------------------------------------------------

    /// <summary>One cow as the tractor beam sees it (registered on the authority only).</summary>
    private sealed class Cow : IBeamable
    {
        private readonly Herds _owner;
        private readonly Live _herd;
        private readonly int _index;
        public readonly long Key;
        private float _ground = float.NaN;
        private double _groundE = double.NaN, _groundN;

        public Cow(Herds owner, Live herd, int index, long key)
        {
            _owner = owner;
            _herd = herd;
            _index = index;
            Key = key;
        }

        public int Index => _index;
        public Live Herd => _herd;

        private float Ground(double e, double n)
        {
            if (Math.Abs(e - _groundE) > 0.5 || Math.Abs(n - _groundN) > 0.5 || float.IsNaN(_ground))
            {
                _ground = _owner.GroundAt(e, n);
                _groundE = e;
                _groundN = n;
            }
            return _ground;
        }

        public Vector3 BeamPosition
        {
            get
            {
                var (e, n, alt, _, _, _) = _owner.Where(_herd, _index, Now);
                if (float.IsNaN(alt)) alt = Ground(e, n);
                if (float.IsNaN(alt)) return new Vector3(0, -1e6f, 0);   // no ground here yet: out of every beam
                return _owner._origin.ToWorld(e, n, alt + 1.0f);
            }
        }

        public bool BeamMovable => _owner.Authority && _owner.IsInsideTree();

        public bool BeamGrounded
        {
            get
            {
                if (!_owner._over.TryGetValue(Key, out var o) || !o.Air) return true;
                float g = Ground(o.E, o.N);
                return float.IsNaN(g) || o.Alt <= g + 0.02f;
            }
        }

        public void BeamMove(Vector3 velocity, float dt)
        {
            if (!_owner._over.TryGetValue(Key, out var o) || !o.Air)
            {
                var (e, n, alt, yaw, _, _) = _owner.Where(_herd, _index, Now);
                if (float.IsNaN(alt)) alt = Ground(e, n);
                o = new Override { Air = true, E = e, N = n, Alt = alt, Yaw = yaw, Since = Now };
                GD.Print($"[herds] cow {_herd.Plan.Tile}/{_herd.Plan.Index}/{_index} lifted at LV95 {e:F1}/{n:F1}");
            }
            o.E += velocity.X * dt;
            o.N -= velocity.Z * dt;
            o.Alt += velocity.Y * dt;
            float g = Ground(o.E, o.N);
            if (!float.IsNaN(g) && o.Alt < g) o.Alt = g;
            // it turns slowly as it rises, the way things do in a beam
            o.Yaw += 0.8f * dt;
            o.Dirty = true;
            _owner._over[Key] = o;
        }

        public void BeamRelease()
        {
            if (_owner._over.TryGetValue(Key, out var o) && o.Air) _owner.Settle(this, o);
        }
    }

    /// <summary>Authority: a cow let down at its override's point; every peer told.</summary>
    private void Settle(Cow cow, Override o)
    {
        var plan = cow.Herd.Plan;
        double now = Now;
        var pure = CowMotion.At(plan, cow.Index, now - cow.Herd.Epoch);
        double de = plan.E + pure.Offset.X - o.E, dn = plan.N + pure.Offset.Y - o.N;
        var down = new Override
        {
            Air = false, E = o.E, N = o.N, Alt = o.Alt, Yaw = o.Yaw, Since = now,
            Distance = (float)Math.Sqrt(de * de + dn * dn),
        };
        _over[cow.Key] = down;
        GD.Print($"[herds] cow {plan.Tile}/{plan.Index}/{cow.Index} set down at LV95 {o.E:F1}/{o.N:F1}, {down.Distance:F0} m from its herd");
        if (!plan.Staged && NetLink.Online(this) && Multiplayer.IsServer())
            Rpc(MethodName.CowDown, cow.Key, o.E, o.N, o.Alt, o.Yaw, now, down.Distance);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_landDone.IsEmpty) TakeLand();
        // a cow back with its herd needs no override any more (both sides drop it by the same rule)
        if (_over.Count == 0) return;
        double now = Now;
        _settled.Clear();
        foreach (var (key, o) in _over)
            if (!o.Air && o.Distance <= LostBeyond && (now - o.Since) * CowMotion.WalkSpeed > o.Distance + 1f) _settled.Add(key);
        foreach (var key in _settled) _over.Remove(key);

        if (!Authority || !NetLink.Online(this) || !Multiplayer.IsServer()) return;
        _sendIn -= delta;
        if (_sendIn > 0) return;
        _sendIn = 0.1;
        _settled.Clear();
        foreach (var (key, o) in _over)
            if (o.Air && o.Dirty && key >= 0) _settled.Add(key);
        foreach (var key in _settled)
        {
            var o = _over[key];
            o.Dirty = false;
            _over[key] = o;
            Rpc(MethodName.CowAir, key, o.E, o.N, o.Alt, o.Yaw);
        }
    }

    // ---- the wire --------------------------------------------------------------------------------

    /// <summary>Server: a joining peer gets every cow a beam has moved and that is not home yet.</summary>
    public void SendTo(long peer)
    {
        if (!NetLink.Online(this) || !Multiplayer.IsServer()) return;
        foreach (var (key, o) in _over)
            if (key < 0) continue;
            else if (o.Air) RpcId(peer, MethodName.CowAirSync, key, o.E, o.N, o.Alt, o.Yaw);
            else RpcId(peer, MethodName.CowDown, key, o.E, o.N, o.Alt, o.Yaw, o.Since, o.Distance);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void CowAir(long key, double e, double n, float alt, float yaw) => TakeAir(key, e, n, alt, yaw);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CowAirSync(long key, double e, double n, float alt, float yaw) => TakeAir(key, e, n, alt, yaw);

    private void TakeAir(long key, double e, double n, float alt, float yaw)
    {
        if (Authority) return;
        _over.TryGetValue(key, out var o);
        if (!o.Air) GD.Print($"[herds] server: cow {key:X} in a beam");
        _over[key] = new Override { Air = true, E = e, N = n, Alt = alt, Yaw = yaw, Since = o.Air ? o.Since : Now };
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CowDown(long key, double e, double n, float alt, float yaw, double since, float distance)
    {
        if (Authority) return;
        GD.Print($"[herds] server: cow {key:X} set down at LV95 {e:F1}/{n:F1}");
        _over[key] = new Override { Air = false, E = e, N = n, Alt = alt, Yaw = yaw, Since = since, Distance = distance };
    }

    // ---- probes ------------------------------------------------------------------------------------

    /// <summary>Cows off the ground now on this peer (held or being let down).</summary>
    public int InAir => _over.Values.Count(o => o.Air);

    /// <summary>The cow highest above its ground on this peer now: its key and that height (probes), or null.</summary>
    public (long Key, float Lift)? Lifted()
    {
        (long, float)? best = null;
        foreach (var (key, o) in _over)
            if (o.Air && GroundAt(o.E, o.N) is var g && !float.IsNaN(g) && (best == null || o.Alt - g > best.Value.Item2))
                best = (key, o.Alt - g);
        return best;
    }

    /// <summary>Probes: the cow nearest a world point as the beam sees it, and whether this peer moves it.</summary>
    public string Describe(Vector3 near)
    {
        Cow? best = null;
        float d = float.MaxValue;
        foreach (var h in _all)
            foreach (var c in h.Cows)
                if (c.BeamPosition.DistanceTo(near) is var dd && dd < d) { d = dd; best = c; }
        return best == null ? "no cow" : FormattableString.Invariant(
            $"cow at {best.BeamPosition} ({d:F1} m off), movable {best.BeamMovable}, authority {Authority}, herds {_all.Count}");
    }

    /// <summary>The highest a cow is above its ground on this peer, m (0 if none is up).</summary>
    public float HighestLift()
    {
        float best = 0f;
        foreach (var o in _over.Values)
            if (o.Air && GroundAt(o.E, o.N) is var g && !float.IsNaN(g)) best = Mathf.Max(best, o.Alt - g);
        return best;
    }

    /// <summary>
    /// The cow of a non-staged herd nearest an LV95 point within <paramref name="within"/> m, at the
    /// moment <paramref name="at"/> (the sightings aim an abduction at it): its LV95 point then.
    /// </summary>
    public (double E, double N, float Ground)? CowNear(double e, double n, float within, double at)
    {
        (double E, double N, float Ground)? best = null;
        double bestD = within * within;
        foreach (var h in _all)
        {
            if (h.Plan.Staged) continue;
            double he = h.Plan.E - e, hn = h.Plan.N - n;
            if (he * he + hn * hn > (within + 60) * (within + 60)) continue;
            for (int i = 0; i < h.Cows.Length; i++)
            {
                if (_over.ContainsKey(h.Cows[i].Key)) continue;
                var (ce, cn, _, _, _, _) = Where(h, i, at);
                double d = (ce - e) * (ce - e) + (cn - n) * (cn - n);
                if (d >= bestD) continue;
                float g = GroundAt(ce, cn);
                if (float.IsNaN(g)) continue;
                bestD = d;
                best = (ce, cn, g);
            }
        }
        return best;
    }
}
