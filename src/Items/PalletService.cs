using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.Items;

/// <summary>
/// Pallets a forklift has moved (#583 phase 2), at <c>World/Pallets</c>: server-owned and
/// <b>session-only</b>, the dormant vehicles' answer (#499) — what has been moved stays moved until
/// the server restarts, and nothing is written to disk. Built by <c>ServerWorld</c> and
/// <c>ClientWorld</c>; offline the client plays the server's part through the same methods.
///
/// <para>
/// What it remembers is mostly <b>absence</b>: <see cref="Taken"/>, the ids of the plan's own
/// pallets that have left their birthplace, and <see cref="Loose"/>, the pallets that have been set
/// down somewhere, in LV95 and altitude so they survive an origin rebase and mean the same spot in
/// a hall 3 km down as out in the yard. A pallet <b>on the forks</b> is in neither: it is part of
/// the forklift (<c>Forklift.Carrying</c>, in its pose and its parked flags), so it has no second
/// owner and costs nothing on the wire per frame.
/// </para>
///
/// <para>
/// No button: <see cref="Tend"/> runs for the local driver of a forklift and asks to take whatever
/// pallet the raised forks are under, and to set down what they carry once lowered
/// (<see cref="Pallets"/>' fork rule). <c>PlacedObjects</c> is deliberately not reused; the plan
/// (<c>docs/plans/forklift-and-pallets.md</c>) says why. Note: <c>docs/notes/vehicles/pallets.md</c>.
/// </para>
/// </summary>
public partial class PalletService : Node
{
    public const string NodeName = "Pallets";

    /// <summary>How far the asker's body may be from the pallet, m: a relayed copy's slack on a 1.8 m reach.</summary>
    public const float Reach = 4f;

    /// <summary>An answer not back in this long is given up on, s.</summary>
    private const double AnswerTimeout = 3;
    /// <summary>After any answer, the forks are not tested again for this long, s: a refusal does not repeat sixty times a second.</summary>
    private const double Quiet = 0.5;

    public static PalletService? Instance { get; private set; }

    /// <summary>A pallet somebody set down: LV95, altitude, its yaw (its runners' heading) and its load.</summary>
    public sealed record LoosePallet(long Id, double E, double N, double Altitude, float Yaw, byte Load);

    private bool _server;
    private WorldOrigin _origin = null!;
    private readonly HashSet<string> _taken = new();
    /// <summary><see cref="_taken"/>'s hall pallets as (building, furniture): what the loot search asks, without a string.</summary>
    private readonly HashSet<(string, int)> _takenHall = new();
    private readonly Dictionary<long, LoosePallet> _loose = new();
    private readonly Dictionary<long, PalletNode> _drawn = new();
    private long _nextId = 1;

    // client: one request in flight at a time
    private Action<byte?>? _takeDone;
    private Action<bool>? _dropDone;
    private double _askedAt = -1, _quietUntil;

    /// <summary>The plan's own pallets that have left their birthplace, by id.</summary>
    public IReadOnlyCollection<string> Taken => _taken;
    /// <summary>The pallets set down somewhere, by the server's number.</summary>
    public IReadOnlyDictionary<long, LoosePallet> Loose => _loose;
    /// <summary>Something was taken or set down, or the join snapshot arrived.</summary>
    public event Action? Changed;

    /// <summary>
    /// Where a hall's plan comes from: the interiors (<c>InteriorManager.GetOrCreate</c>) unless
    /// set — a check with a hand-made hall and no interior manager sets it.
    /// </summary>
    public Func<string, Task<InteriorLayout?>>? Layouts { get; set; }

    /// <summary>The tiles' files, for a yard pallet's place (<c>SiteYards.PalletAt</c>): the world's chunk source.</summary>
    public Func<IChunkSource?>? Source { get; set; }

    // ---- the yards' stacks (#583 phase 3) ---------------------------------------------------------

    /// <summary>The drawn apron pallets of each tile, as <c>DormantVehicles</c> works its yards out and drops them.</summary>
    private readonly Dictionary<TileId, List<PalletNode>> _yards = new();

    /// <summary>
    /// Client: draws a tile's apron pallets (<see cref="SitePallets"/>), handed over by the dormant
    /// layer, which already reads the tile's yards on its worker and frees them with its fleet. A
    /// pallet taken this session hides itself as it enters the tree, as a hall's does.
    /// </summary>
    public void ShowYard(TileId id, IReadOnlyList<YardPallet> pallets)
    {
        if (_server) return;
        HideYard(id);
        var nodes = new List<PalletNode>(pallets.Count);
        var material = HumanMeshBuilder.FigureMaterial();
        foreach (var p in pallets)
        {
            var node = PalletNode.Create(p.Id, p.Load, material);
            node.Transform = new Transform3D(new Basis(Vector3.Up, p.Yaw), _origin.ToWorld(p.E, p.N, p.Height));
            AddChild(node);
            nodes.Add(node);
        }
        _yards[id] = nodes;
    }

    /// <summary>Client: frees a tile's apron pallets, its fleet being dropped.</summary>
    public void HideYard(TileId id)
    {
        if (!_yards.Remove(id, out var nodes)) return;
        foreach (var node in nodes)
            if (IsInstanceValid(node)) node.QueueFree();
    }

    public static PalletService Create(Node world, WorldOrigin origin, bool server)
    {
        var s = new PalletService { Name = NodeName, _server = server, _origin = origin };
        world.AddChild(s);
        Instance = s;
        return s;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => NetLink.Online(this);

    public bool IsTaken(string id) => _taken.Contains(id);

    /// <summary>A hall's own pallet has been forked away: it is no longer a loot container either.</summary>
    public bool IsTakenHall(string building, int furniture) => _takenHall.Contains((building, furniture));

    // ---- client: the forks ----------------------------------------------------------------------

    /// <summary>
    /// The local driver's machine with tines (a forklift, a telehandler, a loader's forks: #615),
    /// once per physics frame: with empty tines raised through <see cref="Pallets.Seat"/> under a
    /// pallet, asks to take it; with a pallet lowered under the set-down height, asks to put it down
    /// where it is. Allocates only when it asks.
    /// </summary>
    public void Tend(FootPlayer p, IForks fork)
    {
        double now = GameClock.Now;
        if (_askedAt >= 0)
        {
            if (now - _askedAt < AnswerTimeout) return;
            // never answered (a dropped link): forget it, and keep what the forks hold
            _askedAt = -1;
            _takeDone = null;
            _dropDone = null;
        }
        if (now < _quietUntil) return;

        if (!fork.HasTines) return;
        var frame = p.GlobalTransform;
        var tines = frame * fork.TinesFrame;
        if (Pallets.LoadCarried(fork.Carrying) is { } load)
        {
            var at = tines * new Vector3(0f, 0f, -Pallets.LoadAhead);
            int carrying = fork.Carrying;
            float yaw = tines.Basis.GetEuler().Y + PalletNode.CarriedYaw(Pallets.CarriedAcross(carrying));
            // lowered onto a tipper's body or a mini dumper's skip (#615): into it, never onto the ground under it
            float tinesTop = p.GlobalPosition.Y + fork.ForkHeight;
            if (BedUnder(p, at, tinesTop, out var bed) is { } over)
            {
                if (tinesTop - over < ForkliftLayout.SetDown && bed is { } host)
                    RequestBed(host, load, yaw, ok => { if (ok && fork.Carrying == carrying) fork.Carrying = 0; });
                return;
            }
            if (!Pallets.SetsDown(fork.ForkHeight)) return;
            // where it rode, on the ground the machine stands on, turned as it rode
            at.Y = p.GlobalPosition.Y;
            RequestDrop(load, _origin.ToGlobal(at), yaw, ok =>
            {
                if (ok && fork.Carrying == carrying) fork.Carrying = 0;
            });
            return;
        }
        if (fork.Carrying != 0 || !Pallets.Lifts(fork.ForkHeight) || fork.ForkHeight >= ForkliftLayout.ForkEntry) return;

        var inverse = frame.AffineInverse();
        var onTines = tines.AffineInverse();
        var ahead = new Vector2(-tines.Basis.Z.X, -tines.Basis.Z.Z).Normalized();
        float speed = p.GroundSpeed;
        foreach (var node in PalletNode.All.Values)
        {
            if (node.Taken || !node.IsInsideTree()) continue;
            // another floor of the hall, or a pallet up on something
            if (Mathf.Abs((inverse * node.GlobalPosition).Y) > 0.5f) continue;
            var local = onTines * node.GlobalPosition;
            var runners = node.GlobalTransform.Basis.X;
            float along = Mathf.Abs(ahead.Dot(new Vector2(runners.X, runners.Z).Normalized()));
            // the tines' frame: −Z ahead of their heel, X across them
            if (!Pallets.OnTines(local.X, -local.Z, along, fork.ForkHeight, speed, fork.TineLength, fork.TineHalfSpan)) continue;
            bool across = Pallets.Across(along) == true;
            RequestTake(node.Id, taken =>
            {
                if (taken is { } l && ReferenceEquals(p.Vehicle, fork) && fork.Carrying == 0) fork.Carrying = Pallets.Carried(l, across);
            });
            return;
        }
    }

    /// <summary>
    /// The local driver's bucket (a wheel loader's, #615), once per physics frame: with an empty
    /// bucket down on the ground and a pallet in it, curled back past <see cref="Pallets.CurlCarry"/>,
    /// asks to take it; with a pallet in it, dumped past <see cref="Pallets.DumpDrop"/>, asks to tip it
    /// out under the lip, on the ground the machine stands on. The same requests and checks as the forks'.
    /// </summary>
    public void TendBucket(FootPlayer p, IBucket bucket)
    {
        if (!bucket.HasBucket || Waiting()) return;
        var frame = p.GlobalTransform;
        var b = frame * bucket.BucketFrame;
        if (Pallets.LoadCarried(bucket.Carrying) is { } load)
        {
            if (!Pallets.Dumped(bucket.BucketPitch)) return;
            var floor = bucket.BucketFloor;
            // out over the lip: the floor's middle, a pallet's half length past the lip
            var at = b * new Vector3(0f, floor.Y, -(bucket.BucketReach + Pallets.Length * 0.5f));
            int carrying = bucket.Carrying;
            float yaw = (frame * bucket.BucketFrame).Basis.GetEuler().Y + PalletNode.CarriedYaw(Pallets.CarriedAcross(carrying));
            // tipped out over a tipper's body or a mini dumper's skip (#615): into it, from above its floor
            float bucketFloor = (b * floor).Y;
            if (BedUnder(p, at, bucketFloor, out var bed) is { } over)
            {
                if (bed is { } host)
                    RequestBed(host, load, yaw, ok => { if (ok && bucket.Carrying == carrying) bucket.Carrying = 0; });
                return;
            }
            at.Y = p.GlobalPosition.Y;
            RequestDrop(load, _origin.ToGlobal(at), yaw, ok =>
            {
                if (ok && bucket.Carrying == carrying) bucket.Carrying = 0;
            });
            return;
        }
        if (bucket.Carrying != 0 || !Pallets.Curled(bucket.BucketPitch)) return;
        var floorAt = b * bucket.BucketFloor;
        if (floorAt.Y - p.GlobalPosition.Y > Pallets.ScoopHeight) return;

        // measured level at the pin, not in the curling bucket's own frame (IBucket.BucketPinLevel)
        var level = frame * bucket.BucketPinLevel;
        var onBucket = level.AffineInverse();
        var ahead = new Vector2(-level.Basis.Z.X, -level.Basis.Z.Z).Normalized();
        foreach (var node in PalletNode.All.Values)
        {
            if (node.Taken || !node.IsInsideTree()) continue;
            var local = onBucket * node.GlobalPosition;
            if (!Pallets.InBucket(local.X, -local.Z, local.Y - bucket.BucketFloor.Y, bucket.BucketHalfWidth, bucket.BucketReach)) continue;
            var runners = node.GlobalTransform.Basis.X;
            bool across = Pallets.Across(Mathf.Abs(ahead.Dot(new Vector2(runners.X, runners.Z).Normalized()))) == true;
            RequestTake(node.Id, taken =>
            {
                if (taken is { } l && ReferenceEquals(p.Vehicle, bucket) && bucket.Carrying == 0) bucket.Carrying = Pallets.Carried(l, across);
            });
            return;
        }
    }

    /// <summary>An answer is awaited, or the quiet after one is not over: test nothing this frame.</summary>
    private bool Waiting()
    {
        double now = GameClock.Now;
        if (_askedAt >= 0)
        {
            if (now - _askedAt < AnswerTimeout) return true;
            // never answered (a dropped link): forget it, and keep what is carried
            _askedAt = -1;
            _takeDone = null;
            _dropDone = null;
        }
        return now < _quietUntil;
    }

    /// <summary>
    /// Asks to lift a pallet onto the local forklift. <paramref name="done"/> gets its load byte,
    /// or null if it was refused (taken by someone else first, out of reach).
    /// </summary>
    public void RequestTake(string id, Action<byte?> done)
    {
        _takeDone = done;
        _askedAt = GameClock.Now;
        if (Online) RpcId(1, MethodName.AskTake, id);
        else ServeTake(1, id);
    }

    /// <summary>Asks to set the forks' pallet down at <paramref name="at"/>, its runners on <paramref name="yaw"/>.</summary>
    public void RequestDrop(byte load, GlobalPos at, float yaw, Action<bool> done)
    {
        _dropDone = done;
        _askedAt = GameClock.Now;
        if (Online) RpcId(1, MethodName.AskDrop, (int)load, at.E, at.N, at.Alt, yaw);
        else ServeDrop(1, load, at.E, at.N, at.Alt, yaw);
    }

    private long MyPeer => NetLink.Ready(this) ? Multiplayer.GetUniqueId() : 1;

    // ---- client: from the server ----------------------------------------------------------------

    /// <summary>A pallet left where it was: a hall's is hidden, a loose one is gone. <paramref name="by"/> carries it now.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Took(string id, int load, long by)
    {
        Remove(id);
        GD.Print($"[pallets] {id} taken by peer {by} (load {load})");
        if (by == MyPeer && _takeDone is { } done)
        {
            Answered();
            _takeDone = null;
            done((byte)load);
        }
        Changed?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TakeRefused(string id, string why)
    {
        GD.Print($"[pallets] taking {id} refused: {why}");
        Answered();
        var done = _takeDone;
        _takeDone = null;
        done?.Invoke(null);
    }

    /// <summary>A pallet was set down; <paramref name="by"/>'s forks are empty now.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Dropped(long id, double e, double n, double alt, float yaw, int load, long by)
    {
        Add(new LoosePallet(id, e, n, alt, yaw, (byte)load));
        GD.Print(FormattableString.Invariant($"[pallets] {Pallets.LooseId(id)} set down by peer {by} at LV95 {e:F1}/{n:F1} alt {alt:F1} (load {load})"));
        if (by == MyPeer && _dropDone is { } done)
        {
            Answered();
            _dropDone = null;
            done(true);
        }
        Changed?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DropRefused(string why)
    {
        GD.Print($"[pallets] setting down refused: {why}");
        Answered();
        var done = _dropDone;
        _dropDone = null;
        done?.Invoke(false);
    }

    /// <summary>The whole state, on joining: replaces whatever this client knew.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Snapshot(string[] taken, long[] ids, double[] pos, float[] yaws, int[] loads)
    {
        _taken.Clear();
        _takenHall.Clear();
        foreach (long id in _loose.Keys.ToList()) Remove(Pallets.LooseId(id));
        foreach (var id in taken) MarkTaken(id);
        for (int i = 0; i < ids.Length; i++)
            Add(new LoosePallet(ids[i], pos[3 * i], pos[3 * i + 1], pos[3 * i + 2], yaws[i], (byte)loads[i]));
        foreach (var node in PalletNode.All.Values) node.SetTaken(_taken.Contains(node.Id));
        GD.Print($"[pallets] snapshot: {taken.Length} taken, {ids.Length} loose");
        Changed?.Invoke();
    }

    private void Answered()
    {
        _askedAt = -1;
        _quietUntil = GameClock.Now + Quiet;
    }

    private void MarkTaken(string id)
    {
        _taken.Add(id);
        if (Pallets.TryParse(id, out var r) && r.Source == PalletSource.Hall) _takenHall.Add((r.Building, r.Index));
    }

    /// <summary>A pallet leaves its place: a plan's is marked taken (and its node hidden), a loose one forgotten.</summary>
    private void Remove(string id)
    {
        if (!Pallets.TryParse(id, out var r)) return;
        if (r.Source != PalletSource.Loose)
        {
            MarkTaken(id);
            if (PalletNode.All.TryGetValue(id, out var node)) node.SetTaken(true);
            return;
        }
        _loose.Remove(r.Loose);
        if (_drawn.Remove(r.Loose, out var drawn) && IsInstanceValid(drawn)) drawn.QueueFree();
    }

    private void Add(LoosePallet p)
    {
        _loose[p.Id] = p;
        _nextId = Math.Max(_nextId, p.Id + 1);
        if (_server) return;
        if (_drawn.Remove(p.Id, out var old) && IsInstanceValid(old)) old.QueueFree();
        var node = PalletNode.Create(Pallets.LooseId(p.Id), p.Load, HumanMeshBuilder.FigureMaterial());
        node.Transform = new Transform3D(new Basis(Vector3.Up, p.Yaw), _origin.ToWorld(p.E, p.N, p.Altitude));
        AddChild(node);
        _drawn[p.Id] = node;
    }

    // ---- a tipping body's pallet (#615) ----------------------------------------------------------

    /// <summary>A body a pallet can go into: a parked vehicle's, or the one another player drives.</summary>
    public readonly record struct BedHost(VehicleBody? Parked, long Driver, IBed Bed, Transform3D Frame);

    /// <summary>How much farther than <see cref="Reach"/> a body's pallet may be from its driver or loader, m: a tipper's length.</summary>
    public const float BedReach = 10f;

    /// <summary>A body loaded is not loaded again for this long, s: the driver's pose has to catch up first.</summary>
    private const double BedPending = 2.0;
    private readonly Dictionary<string, double> _bedPending = new();

    /// <summary>
    /// The floor height (world) of a body under <paramref name="at"/> (world) that a pallet held
    /// from <paramref name="from"/> m up could go into — a parked tipper's or mini dumper's, or one
    /// another player drives — with that body as <paramref name="host"/> when it is empty and down;
    /// null where there is none. A dormant one there is woken, and this frame says only that a body
    /// is there (no host yet): the pallet waits over it, not on the ground.
    /// </summary>
    private float? BedUnder(FootPlayer me, Vector3 at, float from, out BedHost? host)
    {
        host = null;
        ListBeds(me);
        foreach (var node in _bedNodes)
        {
            if (!IsInstanceValid(node)) continue;
            if (node is not VehicleBody { Wrecked: false } v || v.Ride is not IBed { HasBed: true } bed) continue;
            var frame = v.GlobalTransform;
            if (!bed.Bed.Over(frame.AffineInverse() * at)) continue;
            float floor = (frame * bed.Bed.Floor).Y;
            if (from < floor - 0.5f) continue;
            if (v.BedLoad == 0 && !bed.BedUp) host = new BedHost(v, 0, bed, frame);
            return floor;
        }
        foreach (var node in _bedNodes)
        {
            if (!IsInstanceValid(node)) continue;
            if (node is not FootPlayer { SeatIndex: 0 } other || other == me || other.RideModel is not IBed { HasBed: true } bed
                || !long.TryParse(other.Name, out long driver)) continue;
            var frame = other.GlobalTransform;
            if (!bed.Bed.Over(frame.AffineInverse() * at)) continue;
            float floor = (frame * bed.Bed.Floor).Y;
            if (from < floor - 0.5f) continue;
            // what is in it and whether it is up from the driver's published pose, as the server
            // reads it: a copy's own ride is only dressed where it is drawn
            if (BedOf(other.Ride, other.Anim) == 0 && !BedUpInPose(other.Ride, other.Anim)) host = new BedHost(null, driver, bed, frame);
            return floor;
        }
        // a dormant site tipper or dumper: woken, as aiming at it to get in does
        if (DormantVehicles.Instance is { } dormant)
            foreach (var slot in _bedSlots)
            {
                if (dormant.IsAwake(slot) || BedOfKind(slot.KindId) is not { } shape) continue;
                var poses = dormant.DrawnPoses(slot);
                if (poses.Length == 0 || !shape.Over(poses[0].AffineInverse() * at)) continue;
                dormant.Wake(slot);
                return float.PositiveInfinity;
            }
        return null;
    }

    /// <summary>What may have a body to set a pallet in, listed twice a second while one is carried, not every frame.</summary>
    private readonly List<Node3D> _bedNodes = new();
    private readonly List<VehicleSlot> _bedSlots = new();
    private double _bedsListedAt = double.NegativeInfinity;
    private const double BedsListEvery = 0.5;

    private void ListBeds(FootPlayer me)
    {
        double now = GameClock.Now;
        if (now - _bedsListedAt < BedsListEvery) return;
        _bedsListedAt = now;
        _bedNodes.Clear();
        _bedSlots.Clear();
        foreach (var node in GetTree().GetNodesInGroup(VehicleBody.Group))
            if (node is VehicleBody { Ride: IBed { HasBed: true } } v) _bedNodes.Add(v);
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer { SeatIndex: 0, RideModel: IBed { HasBed: true } } other && other != me) _bedNodes.Add(other);
        if (DormantVehicles.Instance is { } dormant)
            foreach (var slot in dormant.Slots())
                if (BedOfKind(slot.KindId) != null) _bedSlots.Add(slot);
    }

    /// <summary>The body a parked kind carries a pallet in, for a dormant one's footprint: a tipper's or a mini dumper's.</summary>
    private static BedShape? BedOfKind(int kind)
    {
        if (_bedOfKind.TryGetValue(kind, out var known)) return known;
        BedShape? shape = kind == (int)RideKind.MiniDumper ? MiniDumperLayout.Bed
            : HeavyCatalog.For((RideKind)kind) is { Body: TruckBody.Tipper } spec ? TruckMeshBuilder.TipperBed(spec, 0.5f) : null;
        return _bedOfKind[kind] = shape;
    }
    private static readonly Dictionary<int, BedShape?> _bedOfKind = new();

    /// <summary>Asks to set the carried pallet into <paramref name="host"/>'s body, its runners on <paramref name="yaw"/> (world).</summary>
    private void RequestBed(BedHost host, byte load, float yaw, Action<bool> done)
    {
        // runners along the machine or across it, whichever is nearer
        float rel = yaw - host.Frame.Basis.GetEuler().Y;
        bool across = Mathf.Abs(Mathf.Cos(rel)) > Mathf.Sqrt2 * 0.5f;
        _dropDone = done;
        _askedAt = GameClock.Now;
        string name = host.Parked?.Name ?? "";
        if (Online) RpcId(1, MethodName.AskBed, (int)load, across, name, host.Driver);
        else ServeBed(1, load, across, name, host.Driver);
    }

    /// <summary>
    /// The local driver's tipper or mini dumper, once per physics frame: with a pallet in the body
    /// and the body up far enough that it has slid to the edge (<see cref="BedShape.Slid"/>), asks
    /// to set it down where it falls, on the ground past the open end.
    /// </summary>
    public void TendBed(FootPlayer p, IBed bed)
    {
        if (!bed.HasBed || Pallets.LoadCarried(bed.BedLoad) is not { } load || Waiting()) return;
        float shown = p.Visual is HeavyRig rig ? rig.TippedShown : bed.BedUp ? 1f : 0f;
        if (BedShape.Slid(shown) < 1f) return;
        var frame = p.GlobalTransform;
        var at = frame * bed.Bed.Spill;
        int carrying = bed.BedLoad;
        float yaw = frame.Basis.GetEuler().Y + PalletNode.CarriedYaw(Pallets.CarriedAcross(carrying));
        RequestDrop(load, _origin.ToGlobal(at), yaw, ok => { if (ok && bed.BedLoad == carrying) bed.BedLoad = 0; });
    }

    /// <summary>The server set this asker's pallet into a body: its forks or bucket are empty now.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Bedded(int bedLoad, string into)
    {
        GD.Print($"[pallets] set into {into} (load {bedLoad})");
        Answered();
        var done = _dropDone;
        _dropDone = null;
        done?.Invoke(true);
        Changed?.Invoke();
    }

    /// <summary>On the driver of a tipper or mini dumper: somebody set a pallet in its body (#615).</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BedLoaded(int bedLoad)
    {
        var me = GetNodeOrNull<FootPlayer>("../Players/" + MyPeer);
        if (me?.Vehicle is IBed { HasBed: true } bed && me.SeatIndex == 0)
        {
            bed.BedLoad = bedLoad;
            GD.Print($"[pallets] a pallet was set in my body (load {bedLoad})");
        }
        else GD.PushWarning("[pallets] a pallet was set in a body I no longer drive");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskBed(int load, bool across, string parked, long driver) =>
        ServeBed(Multiplayer.GetRemoteSenderId(), (byte)Math.Clamp(load, 0, 255), across, parked, driver);

    /// <summary>
    /// Server: sets the asker's carried pallet into a parked vehicle's body (on its authority,
    /// replicated, <see cref="VehicleManager.LoadBed"/>) or into the body another player drives (that
    /// driver is told, and publishes it in its pose). Checked: the asker holds that load, the body is
    /// a tipper's or a mini dumper's, empty and down by what the server has of it, not loaded in the
    /// last <see cref="BedPending"/> s, and within <see cref="BedReach"/> of the asker.
    /// </summary>
    private void ServeBed(long peer, byte load, bool across, string parked, long driver)
    {
        string key = parked != "" ? parked : "#" + driver;
        double now = GameClock.Now;
        string? why = Forks(peer, load);
        GlobalPos where = default;
        VehicleBody? vehicle = null;
        if (why == null && parked != "")
        {
            vehicle = VehicleManager.Instance?.GetNodeOrNull<VehicleBody>(parked);
            if (vehicle is not { Wrecked: false } || vehicle.Ride is not IBed { HasBed: true } bed) why = "No such body.";
            else if (vehicle.BedLoad != 0 || bed.BedUp) why = "There is something in it already.";
            else where = vehicle.Global;
        }
        else if (why == null)
        {
            if (driver == peer || GetNodeOrNull<FootPlayer>("../Players/" + driver) is not { SeatIndex: 0 } body) why = "Nobody drives it.";
            else if (BedOf(body.Ride, body.Anim) is not { } inBody) why = "No such body.";
            else if (inBody != 0 || BedUpInPose(body.Ride, body.Anim)) why = "There is something in it already.";
            else where = body.Global;
        }
        if (why == null && _bedPending.TryGetValue(key, out double at) && now - at < BedPending) why = "There is something in it already.";
        if (why == null && !InReach(peer, where, BedReach)) why = "Too far away.";
        if (why != null)
        {
            if (Online && peer != 1) RpcId(peer, MethodName.DropRefused, why);
            else DropRefused(why);
            return;
        }
        _bedPending[key] = now;
        int carrying = Pallets.Carried(load, across);
        if (vehicle != null) VehicleManager.Instance!.LoadBed(vehicle, carrying);
        else if (Online) RpcId(driver, MethodName.BedLoaded, carrying);
        GD.Print($"[pallets] peer {peer} set load {load} into {key}");
        if (Online && peer != 1) RpcId(peer, MethodName.Bedded, carrying, key);
        else Bedded(carrying, key);
    }

    /// <summary>
    /// What a tipper's or mini dumper's published pose says is in its body (#615), or null for a
    /// machine with none.
    /// </summary>
    public static int? BedOf(RideKind kind, Vector4 anim) =>
        kind == RideKind.MiniDumper ? MiniDumper.BedInPose(anim)
        : HeavyCatalog.For(kind) is { Body: TruckBody.Tipper } ? Truck.BedInPose(anim) : null;

    private static bool BedUpInPose(RideKind kind, Vector4 anim) =>
        kind == RideKind.MiniDumper ? anim.X > 0.5f : Truck.TippedInPose(anim);

    /// <summary>Server: how much farther the asker may set a pallet down: a body's length when it drives one.</summary>
    private float BedReachOf(long peer) =>
        Online && GetNodeOrNull<FootPlayer>("../Players/" + peer) is { } body && BedOf(body.Ride, body.Anim) != null ? BedReach : 0f;

    /// <summary>
    /// Server: why the asker may not set <paramref name="load"/> down — it is neither on its tines
    /// or in its bucket nor in its body — or null.
    /// </summary>
    private string? Holds(long peer, byte load)
    {
        if (!Online) return null;
        if (GetNodeOrNull<FootPlayer>("../Players/" + peer) is not { } body) return "Nobody there.";
        if (BedOf(body.Ride, body.Anim) is { } inBody) return Pallets.LoadCarried(inBody) == load ? null : "That is not in the body.";
        return Forks(peer, load);
    }

    // ---- server ---------------------------------------------------------------------------------

    /// <summary>Server: hands a joining peer what has been moved this session.</summary>
    public void SendTo(long peer)
    {
        var loose = _loose.Values.OrderBy(p => p.Id).ToList();
        RpcId(peer, MethodName.Snapshot, _taken.ToArray(),
            loose.Select(p => p.Id).ToArray(),
            loose.SelectMany(p => new[] { p.E, p.N, p.Altitude }).ToArray(),
            loose.Select(p => p.Yaw).ToArray(),
            loose.Select(p => (int)p.Load).ToArray());
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskTake(string id) => ServeTake(Multiplayer.GetRemoteSenderId(), id);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskDrop(int load, double e, double n, double alt, float yaw) =>
        ServeDrop(Multiplayer.GetRemoteSenderId(), (byte)Math.Clamp(load, 0, 255), e, n, alt, yaw);

    private async void ServeTake(long peer, string id)
    {
        if (!Pallets.TryParse(id, out var r)) { Refuse(peer, id, "No such pallet."); return; }
        byte load;
        GlobalPos at;
        if (r.Source == PalletSource.Loose)
        {
            if (!_loose.TryGetValue(r.Loose, out var loose)) { Refuse(peer, id, "It is not there any more."); return; }
            load = loose.Load;
            at = new GlobalPos(loose.E, loose.N, loose.Altitude);
        }
        else if (r.Source == PalletSource.Hall)
        {
            if (_taken.Contains(id)) { Refuse(peer, id, "It is not there any more."); return; }
            // the hall's plan says where its pallet stands and what is on it: nothing the asker sends
            InteriorLayout? layout = null;
            try { layout = Layouts != null ? await Layouts(r.Building) : InteriorManager.Instance is { } interiors ? await interiors.GetOrCreate(r.Building) : null; }
            catch (Exception e) { GD.PushError($"[pallets] layout {r.Building}: {e.Message}"); }
            if (layout == null || r.Index < 0 || r.Index >= layout.Furniture.Count
                || !InteriorMeshBuilder.IsLoosePallet(layout.Furniture[r.Index]))
            {
                Refuse(peer, id, "No such pallet.");
                return;
            }
            // taken by somebody else while the plan loaded
            if (_taken.Contains(id)) { Refuse(peer, id, "It is not there any more."); return; }
            var f = layout.Furniture[r.Index];
            load = InteriorMeshBuilder.PalletLoad(f);
            at = _origin.ToGlobal(InteriorManager.PlacementFor(layout, _origin) * new Vector3(f.X, layout.FloorY(f.Floor), f.Z));
        }
        else
        {
            // a site's apron stack (#583 phase 3) or a building site's pallet (#615): worked out
            // from the tile's own files, as every peer draws it, so nothing the asker sends decides
            // where it is or what is on it
            if (_taken.Contains(id)) { Refuse(peer, id, "It is not there any more."); return; }
            YardPallet? stack = null;
            if (Source?.Invoke() is { } source)
                try
                {
                    stack = await Task.Run(() => r.Source == PalletSource.Site
                        ? Terrain.Construction.SitePlans.PalletAt(source, r.Building, r.Index)
                        : SiteYards.PalletAt(source, r.Building, r.Index));
                }
                catch (Exception e) { GD.PushError($"[pallets] yard {r.Building}: {e.Message}"); }
            if (stack is not { } yard) { Refuse(peer, id, "No such pallet."); return; }
            if (_taken.Contains(id)) { Refuse(peer, id, "It is not there any more."); return; }
            load = yard.Load;
            at = new GlobalPos(yard.E, yard.N, yard.Height);
        }

        if (Forks(peer, null) is { } why) { Refuse(peer, id, why); return; }
        if (!InReach(peer, at)) { Refuse(peer, id, "Too far away."); return; }

        if (Online)
            foreach (int other in Multiplayer.GetPeers())
                RpcId(other, MethodName.Took, id, (int)load, peer);
        // the server's own copy, and offline the client's: the same call
        Took(id, load, peer);
    }

    private void ServeDrop(long peer, byte load, double e, double n, double alt, float yaw)
    {
        var at = new GlobalPos(e, n, alt);
        string? why = !double.IsFinite(e) || !double.IsFinite(n) || !double.IsFinite(alt) || !float.IsFinite(yaw) ? "Bad position."
            : Holds(peer, load)
              ?? (!InReach(peer, at, BedReachOf(peer)) ? "Too far away." : null);
        if (why != null)
        {
            if (Online && peer != 1) RpcId(peer, MethodName.DropRefused, why);
            else DropRefused(why);
            return;
        }
        long id = _nextId++;
        yaw = Mathf.Wrap(yaw, -Mathf.Pi, Mathf.Pi);
        if (Online)
            foreach (int other in Multiplayer.GetPeers())
                RpcId(other, MethodName.Dropped, id, e, n, alt, yaw, (int)load, peer);
        Dropped(id, e, n, alt, yaw, load, peer);
    }

    private void Refuse(long peer, string id, string why)
    {
        if (Online && peer != 1) RpcId(peer, MethodName.TakeRefused, id, why);
        else TakeRefused(id, why);
    }

    /// <summary>
    /// Server: why the asker may not do it — not driving a machine with tines, or its tines not
    /// holding <paramref name="load"/> (null: empty forks) — or null. From the pose the driver
    /// publishes (<see cref="CarryingOf"/>); offline there is nobody to doubt.
    /// </summary>
    private string? Forks(long peer, byte? load)
    {
        if (!Online) return null;
        if (GetNodeOrNull<FootPlayer>("../Players/" + peer) is not { } body) return "Nobody there.";
        if (CarryingOf(body.Ride, body.Anim) is not { } carrying) return "Not on a machine with forks.";
        var on = Pallets.LoadCarried(carrying);
        return on == load ? null : load == null ? "The forks are full." : "That is not on the forks.";
    }

    /// <summary>
    /// What a machine's published pose says is on its tines, or null for a machine with none: the
    /// forklift's <c>Anim.Z</c>, the telehandler's and the fork loader's packed with their lift in
    /// <c>Anim.Y</c> (#615).
    /// </summary>
    public static int? CarryingOf(RideKind kind, Vector4 anim) => kind switch
    {
        RideKind.Forklift => Mathf.RoundToInt(anim.Z),
        RideKind.Telehandler => TelehandlerLayout.FromPoseLift(anim.Y).Carrying,
        RideKind.WheelLoaderForks or RideKind.WheelLoader => WheelLoaderLayout.FromPoseLift(anim.Y).Carrying,
        _ => null,
    };

    /// <summary>Server: the asker's body within <see cref="Reach"/> (and <paramref name="more"/>), in LV95 (the server's origin may be far away, #185).</summary>
    private bool InReach(long peer, GlobalPos at, float more = 0f)
    {
        if (!Online) return true;
        if (GetNodeOrNull<FootPlayer>("../Players/" + peer) is not { } body) return false;
        return body.Global.DistanceTo(at) <= Reach + more;
    }
}
