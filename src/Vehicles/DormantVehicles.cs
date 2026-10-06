using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Construction;
using UnitSport.Terrain.Format;
using UnitSport.World;

namespace UnitSport.Vehicles;

/// <summary>
/// The vehicles already standing in the world until somebody touches one: the cars in the car parks
/// (#499), an industrial yard's fleet (#516) and a harbour's moored boats (#554).
///
/// <para>
/// <b>Why they are dormant.</b> A big retail lot is 80 bays and a city harbour a hundred boats.
/// Spawning a replicated <see cref="VehicleBody"/> for each is not affordable, and scenery you
/// cannot take away is not what a full car park should be. So a slot is drawn as instanced geometry
/// with a static box, and is <b>promoted</b> to a real vehicle only when someone touches it.
/// </para>
///
/// <para>
/// <b>Nothing is replicated.</b> <see cref="DormantSlots"/> is a pure function of the tile's bytes
/// (and of the landings, for a harbour), so the server and every client work out the same fleet
/// independently, the <see cref="BuildingTypes"/> trick. What goes on the wire is "slot N of owner
/// X is awake", once, when it wakes, and for a slot that respawns, "asleep again".
/// </para>
///
/// <para>
/// <b>Waking.</b> Anything that would move one asks <see cref="Wake"/>. The server promotes the slot
/// exactly once through <see cref="VehicleManager.Place"/> under <see cref="VehicleSlot.NodeName"/>
/// (a server-initiated <c>Place</c> is not subject to <c>MayPark</c>, which is what
/// <c>AfricaTwinEgg</c> relies on) and tells every peer to stop drawing the dormant copy. A woken
/// slot stays a vehicle while that vehicle exists; a slot that respawns sleeps again a while after
/// its vehicle is gone (<see cref="Restock"/>).
/// </para>
///
/// <para>
/// <b>The look is the woken vehicle's own model</b> (<see cref="DormantLooks"/>), so waking changes
/// what is alive about it, not what it is (#552).
/// </para>
/// </summary>
/// <summary>
/// The solid box of one dormant vehicle. Carries its slot, so a ray that hits it (the player
/// aiming at the car) knows which slot to wake — <c>VehicleReach</c> asks for exactly that.
/// </summary>
public partial class DormantBody : StaticBody3D
{
    public VehicleSlot Slot { get; init; }
}

public partial class DormantVehicles : Node3D, IOriginContainer
{
    public static DormantVehicles? Instance { get; private set; }

    /// <summary>A slot this close to a point is the one that point means.</summary>
    public const float ReachM = 3.0f;

    /// <summary>Dormant cars are drawn to here, as <c>Traffic</c> draws its own to 600 m.</summary>
    private const float DrawnM = 400f;

    /// <summary>
    /// A tile holds its fleet while it is within this many tiles of an anchor's tile (#552): at
    /// least 500 m round the anchor in every direction, past <see cref="DrawnM"/>. It used to be
    /// every tile <c>ChunkManager.TileEntered</c> announced — the whole streamed square, 961 tiles
    /// at render distance 15 — so every car park and yard in a 30 km square got its boxes and
    /// lorries, Geneva's thousands of them, and none was ever freed.
    /// </summary>
    private const int NearRings = 1;

    /// <summary>A fleet is dropped once its tile is further than this from every anchor's tile (hysteresis).</summary>
    private const int KeepRings = 2;

    /// <summary>How often the anchors' tiles are looked at, in seconds.</summary>
    private const double CheckEvery = 0.5;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;

    /// <summary>Slots by tile, in <c>PARK</c> order; a tile with no car park has no entry.</summary>
    private readonly Dictionary<TileId, List<VehicleSlot>> _slots = new();

    /// <summary>The drawn instances of each tile, so a tile's fleet can be rebuilt or dropped whole.</summary>
    private readonly Dictionary<TileId, List<MultiMeshInstance3D>> _drawn = new();

    /// <summary>The solid box of each dormant car, by tile. A ghost car you walk through is worse than no car.</summary>
    private readonly Dictionary<TileId, List<DormantBody>> _solid = new();

    /// <summary>Slots promoted to real vehicles: <c>owner|ordinal</c>. Never drawn dormant again.</summary>
    private readonly HashSet<string> _awake = new();

    private readonly List<TileId> _pending = new();
    private bool _busy;

    /// <summary>The anchors' tiles at the last look, and now: the fleets only change when these do.</summary>
    private readonly List<TileId> _anchorTiles = new(), _anchorTilesNow = new();
    private readonly List<TileId> _drop = new();
    private double _sinceCheck = CheckEvery;

    public DormantVehicles(ChunkManager chunks, WorldOrigin origin)
    {
        Name = "DormantVehicles";
        _chunks = chunks;
        _origin = origin;
    }

    public override void _Ready()
    {
        Instance = this;
        Landings.Changed += OnLandingsChanged;
        Watch();
        // woken slots stay awake across a restart (#689)
        if (World.ObjectContainers.Instance is { } containers) RestoreAwake(containers.LoadAwake(), containers.FiledNames);
    }

    public override void _ExitTree()
    {
        Landings.Changed -= OnLandingsChanged;
        if (_watched is { } vm && IsInstanceValid(vm))
        {
            vm.ChildEnteredTree -= OnVehicleAdded;
            vm.ChildExitingTree -= OnVehicleRemoved;
        }
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// The harbour jetties changed (#554): a streaming client gets the server's landings after its
    /// first tiles, and a marina's slots come from them. Every fleet is worked out again on the next
    /// look; the awake slots stay awake.
    /// </summary>
    private void OnLandingsChanged()
    {
        _drop.Clear();
        _drop.AddRange(_slots.Keys);
        foreach (var id in _drop) Drop(id);
        _pending.Clear();
        _anchorTiles.Clear();
        _sinceCheck = CheckEvery;
    }

    private VehicleManager? _watched;

    /// <summary>
    /// Watches for a vehicle arriving under a slot's name.
    ///
    /// <para>
    /// This is the only thing that undraws a dormant copy. A "woken" broadcast used to, as well: it
    /// only reached the peers connected when the slot woke (a late joiner gets the vehicle in its
    /// join snapshot and no broadcast, and drew the copy and the real car one inside the other: the
    /// tier-2 check caught that), and it could arrive before the spawn and leave the bay empty for a
    /// frame (#560). Keying on the node existing is the fact rather than the message: it covers the
    /// spawn, the join snapshot and any later one alike, on the very frame the live vehicle is drawn.
    /// </para>
    /// </summary>
    private void Watch()
    {
        if (_watched != null || VehicleManager.Instance is not { } vehicles) return;
        _watched = vehicles;
        vehicles.ChildEnteredTree += OnVehicleAdded;
        vehicles.ChildExitingTree += OnVehicleRemoved;
        // anything already standing there when this system started (a join snapshot that arrived first)
        foreach (var child in vehicles.GetChildren()) OnVehicleAdded(child);
    }

    private void OnVehicleAdded(Node node)
    {
        if (SlotOf(node.Name) is not { } key) return;
        _gone.Remove(key.Key);
        _asked.Remove(key.Key);
        bool fresh = !_awake.Contains(key.Key);
        MarkAwake(key.Key);
        if (fresh && Decides && World.ObjectContainers.Instance is { } containers)
        {
            _awakeSince[key.Key] = VehicleState.Now;
            containers.SaveAwake(_awakeSince);
        }
        // every peer is told, near or not (#689): with entity interest a peer far away never gets
        // the node, and its bay must still be empty when it comes; the fact is global, the car local
        if (fresh && Online && Multiplayer.IsServer()) Rpc(MethodName.Woke, key.Key);
    }

    /// <summary>A slot is awake here: its dormant copy goes, in a car park or a hall (#630).</summary>
    private void MarkAwake(string key)
    {
        _awaiting.Remove(key);
        // a forklift asleep in a hall (#630) is no tile's slot: its own node undraws itself
        ParkedForklift.Woke(key);
        if (!_awake.Add(key)) return;
        int bar = key.LastIndexOf('|');
        if (bar > 0 && TileOf(key[..bar]) is { } tile) Forget(tile, key);
    }

    // ---- the awake set on every peer (#689) ----------------------------------------------------

    /// <summary>
    /// Awake slots the server told of whose vehicle has not arrived here yet, and until when the
    /// dormant copy waits for it. Within a client's interest the node is on its way, and the copy
    /// stays until it lands rather than leave the bay empty for a frame (the spawn and the message
    /// travel apart, #560); past the wait the vehicle is elsewhere and the bay is empty.
    /// </summary>
    private readonly Dictionary<string, double> _awaiting = new();

    /// <summary>Seconds a copy waits for its woken vehicle; longer on joining, when the spawns come in budgets.</summary>
    private const double AwaitNode = 1.0, AwaitNodeOnJoin = 3.0;

    /// <summary>Whether a slot (<c>owner|ordinal</c>) is awake: its vehicle is somewhere in the world, or asleep in a container.</summary>
    public bool IsAwake(string key) => _awake.Contains(key);

    /// <summary>Server: when each slot woke (unix s), kept across restarts by the containers (#689).</summary>
    private Dictionary<string, double> _awakeSince = new();

    /// <summary>
    /// Server, starting: the slots the last run had woken stay awake (<see cref="World.ContainerRules.KeepAwake"/>):
    /// those woken this past week, and any whose vehicle is asleep in a container under the slot's name.
    /// </summary>
    public void RestoreAwake(IReadOnlyDictionary<string, double> saved, IEnumerable<string> filedNames)
    {
        var filedSlots = new HashSet<string>();
        foreach (var name in filedNames)
            if (SlotOf(name) is { } key) filedSlots.Add(key.Key);
        var keep = World.ContainerRules.KeepAwake(saved, VehicleState.Now, filedSlots.Contains);
        foreach (var key in filedSlots) keep.TryAdd(key, VehicleState.Now);
        foreach (var (key, since) in keep)
        {
            _awakeSince[key] = since;
            MarkAwake(key);
        }
        if (keep.Count > 0) GD.Print($"[dormant] {keep.Count} slots stay awake from the last run");
    }

    /// <summary>Server: a joining peer gets every awake slot, wherever its vehicle is.</summary>
    public void SendTo(long peer)
    {
        if (_awake.Count > 0) RpcId(peer, MethodName.AwakeSet, _awake.ToArray());
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeerExtension.TransferModeEnum.Reliable)]
    private void Woke(string key)
    {
        if (!_awake.Contains(key)) _awaiting.TryAdd(key, GameClock.Now + AwaitNode);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeerExtension.TransferModeEnum.Reliable)]
    private void AwakeSet(string[] keys)
    {
        foreach (var key in keys)
            if (!_awake.Contains(key)) _awaiting.TryAdd(key, GameClock.Now + AwaitNodeOnJoin);
    }

    /// <summary>The waits that ran out: those slots' vehicles are elsewhere, their bays empty.</summary>
    private void AwaitedLong()
    {
        if (_awaiting.Count == 0) return;
        double now = GameClock.Now;
        _expiredWaits.Clear();
        foreach (var (key, until) in _awaiting)
            if (now >= until) _expiredWaits.Add(key);
        foreach (var key in _expiredWaits) MarkAwake(key);
    }

    private readonly List<string> _expiredWaits = new();

    /// <summary>
    /// A woken slot's vehicle left the world (taken by a player, wrecked and cleared): where the slot
    /// respawns, the clock starts. Only the peer that decides acts on it (<see cref="Restock"/>).
    /// </summary>
    private void OnVehicleRemoved(Node node)
    {
        // gone to sleep in a container (#689), not gone: the slot is still that vehicle's
        if (World.ObjectContainers.IsFiling(node)) return;
        if (SlotOf(node.Name) is not { } key || !_awake.Contains(key.Key)) return;
        _gone[key.Key] = GameClock.Now;
    }

    // ---- respawning (#554) ---------------------------------------------------------------------

    /// <summary>Seconds a respawning slot stays empty after its vehicle is gone (#383's marina boats).</summary>
    public static double RespawnSeconds { get; set; } = 180;

    /// <summary>Nobody within this of a slot when it is restocked: it must not appear in front of someone.</summary>
    public const float ClearOfPlayers = 40f;

    /// <summary>No vehicle within this of a slot when it is restocked, flat metres.</summary>
    public const float ClearOfVehicles = 4f;

    /// <summary>Awake slots whose vehicle is gone, and since when (<see cref="GameClock.Now"/>).</summary>
    private readonly Dictionary<string, double> _gone = new();
    private readonly List<string> _restocked = new();

    /// <summary>True on the peer that decides what stands where: the server, or a game played offline.</summary>
    private bool Decides => !Online || Multiplayer.IsServer();

    /// <summary>
    /// Puts a respawning slot back to sleep once its vehicle has been gone <see cref="RespawnSeconds"/>,
    /// its place is clear and nobody is near: the dormant copy is drawn again on every peer
    /// (<see cref="Slept"/>), so a harbour is restocked as #383's boats were. A slot whose vehicle
    /// still exists, however far it went, stays awake: that vehicle is the slot's.
    /// </summary>
    private void Restock()
    {
        if (_gone.Count == 0 || !Decides || VehicleManager.Instance is not { } vehicles) return;
        double now = GameClock.Now;
        _restocked.Clear();
        foreach (var (key, since) in _gone)
        {
            if (now - since < RespawnSeconds) continue;
            int bar = key.LastIndexOf('|');
            if (Find(key[..bar], int.Parse(key[(bar + 1)..])) is not { } slot) continue;   // its tile is not here
            if (!slot.Respawns) { _restocked.Add(key); continue; }
            if (!Clear(vehicles, _origin.ToWorld(slot.E, slot.N, slot.Height))) continue;
            _restocked.Add(key);
            if (Online) Rpc(MethodName.Slept, slot.Owner, slot.Ordinal);
            else Slept(slot.Owner, slot.Ordinal);
        }
        foreach (var key in _restocked) _gone.Remove(key);
    }

    /// <summary>No vehicle within <see cref="ClearOfVehicles"/> of the place and nobody within <see cref="ClearOfPlayers"/>.</summary>
    private static bool Clear(VehicleManager vehicles, Vector3 at)
    {
        foreach (var node in vehicles.GetChildren())
            if (node is VehicleBody v && MathX.FlatDistance(v.GlobalPosition, at) < ClearOfVehicles) return false;
        if (vehicles.PlayerPositions?.Invoke() is { } players)
            foreach (var p in players)
                if (MathX.FlatDistance(p, at) < ClearOfPlayers) return false;
        return true;
    }

    /// <summary>A slot sleeps again: dormant on every peer, drawn where its tile is.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeerExtension.TransferModeEnum.Reliable)]
    private void Slept(string owner, int ordinal)
    {
        string key = $"{owner}|{ordinal}";
        _gone.Remove(key);
        _awaiting.Remove(key);
        if (_awakeSince.Remove(key) && Decides) World.ObjectContainers.Instance?.SaveAwake(_awakeSince);
        if (!_awake.Remove(key)) return;
        if (TileOf(owner) is { } id && _slots.ContainsKey(id)) Draw(id);
    }

    /// <summary>
    /// The state a slot wakes with. <see cref="Promote"/> places exactly this, and the dormant look
    /// is built from its ride, so the car or the artic you touch and the one that was standing
    /// there cannot drift apart.
    /// </summary>
    private static VehicleState StateOf(VehicleSlot s) =>
        new(KindFor(s), new GlobalPos(s.E, s.N, s.Height), s.Yaw,
            Vector3.Zero, 0f, EngineOn: false, Wrecked: false, Throttle: 0f, SpawnedAt: 0,
            Train: s.Train, Load: s.Load);

    /// <summary>Whether anything is drawn at all: a dedicated server or a headless check needs the boxes only.</summary>
    private static readonly bool Drawn = DisplayServer.GetName() != "headless";

    /// <summary>
    /// A slot's look (<see cref="DormantLooks"/>): its parked model, merged once per kind, train and
    /// load and shared by every slot that has it, and its boxes — the parked hull plus each further
    /// section's, as <see cref="VehicleBody"/> takes them for a real parked train.
    /// </summary>
    private static DormantLook? LookOf(VehicleSlot s) =>
        DormantLooks.For(DormantLooks.KeyOf(s), () => StateOf(s).CreateRide(), Drawn);

    /// <summary>
    /// The slot a vehicle node name belongs to: <c>veh_slot_&lt;owner&gt;_&lt;ordinal&gt;</c>, where the
    /// owner is itself underscore-separated and its first two parts are always its tile.
    ///
    /// <para>
    /// The owner is <b>not</b> a fixed number of parts. A car park's is its tile (<c>E_N</c>, so five
    /// parts in all); an industrial yard's is a building (<c>E_N_Index</c>, so six). The first
    /// version demanded exactly five and returned null for anything else — which would have made
    /// every yard vehicle draw its dormant copy and its real self one inside the other, for every
    /// peer, every time, with nothing in the parking check able to see it (#497 spotted it).
    /// So: the ordinal is the last part, the owner is everything between the prefix and it.
    /// </para>
    /// </summary>
    private static (string Key, TileId Tile)? SlotOf(string name)
    {
        var p = name.Split('_');
        if (p.Length < 5 || p[0] != "veh" || p[1] != "slot") return null;
        if (!int.TryParse(p[^1], out int ordinal)) return null;
        if (!int.TryParse(p[2], out int e) || !int.TryParse(p[3], out int n)) return null;
        // the whole owner, not just its tile: two yards in one tile must not share a key
        return ($"{string.Join('_', p[2..^1])}|{ordinal}", new TileId(e, n));
    }

    /// <summary>
    /// The cars a lot is filled with: ordinary road shapes only. A car park of MR2s and Roadsters
    /// would be a car show, so the mid-engined and open ones are left out.
    /// </summary>
    private static readonly int[] ParkedKinds = CarCatalog.All
        .Where(c => c.Body.Shape is BodyShape.Hatchback or BodyShape.Sedan
            or BodyShape.Coupe or BodyShape.Fastback)
        .Select(c => (int)c.Kind).ToArray();

    /// <summary>
    /// What stands in an industrial yard beyond cars (#496 phase 3): the goods vehicles only —
    /// the tractor and the rigid, never a bus or a coach, which belong to an operator's depot and
    /// not to a haulier's. Buses would read as a mistake outside a warehouse.
    /// </summary>
    private static readonly int[] YardHeavies = HeavyCatalog.All
        .Where(h => h.Takes != Coupling.None || h.Sections.Length == 1 && h.Label.Contains("rigid"))
        .Select(h => (int)h.Kind).ToArray();

    /// <summary>
    /// The trailers a yard holds, as <c>TrailerCatalog</c> codes with a load already in them. Each
    /// trailer appears empty, part and fully loaded, so a row of them is not all the same.
    /// </summary>
    private static readonly int[] YardTrailers = Enumerable.Range(0, TrailerCatalog.All.Count)
        .SelectMany(i => new[] { TrailerCatalog.Code(i, 0f), TrailerCatalog.Code(i, 0.55f), TrailerCatalog.Code(i, 1f) })
        .ToArray();

    // ---- the fleet of a tile -----------------------------------------------------------------

    /// <summary>
    /// Twice a second: when an anchor has changed tile, the tiles near one are queued and the
    /// fleets of those now out of reach are freed. Nothing else happens per frame.
    /// </summary>
    public override void _Process(double delta)
    {
        _sinceCheck += delta;
        if (_sinceCheck < CheckEvery) return;
        _sinceCheck = 0;
        Restock();
        AwaitedLong();
        if (_unposed.Count > 0)
        {
            _repose.Clear();
            _repose.AddRange(_unposed);
            foreach (var id in _repose)
            {
                if (!_slots.TryGetValue(id, out var slots)) { _unposed.Remove(id); continue; }
                // only once the collision has come: posing again before would find the same terrain
                foreach (var s in slots)
                    if (LookOf(s) is { Bodies: not null } && _chunks.HasCollisionAt(_origin.ToWorld(s.E, s.N, s.Height)))
                    {
                        DrawLooks(id);
                        break;
                    }
            }
        }

        _anchorTilesNow.Clear();
        foreach (var anchor in _chunks.Anchors)
            if (IsInstanceValid(anchor) && anchor.IsInsideTree()) _anchorTilesNow.Add(_origin.TileAt(anchor.GlobalPosition));
        if (SameTiles(_anchorTilesNow, _anchorTiles))
        {
            if (_pending.Count > 0) _ = Fill();
            return;
        }
        _anchorTiles.Clear();
        _anchorTiles.AddRange(_anchorTilesNow);

        _drop.Clear();
        foreach (var id in _slots.Keys)
            if (!Within(id, KeepRings)) _drop.Add(id);
        foreach (var id in _drop) Drop(id);
        _pending.RemoveAll(id => !Within(id, KeepRings));

        foreach (var at in _anchorTiles)
            for (int de = -NearRings; de <= NearRings; de++)
                for (int dn = -NearRings; dn <= NearRings; dn++)
                {
                    var id = new TileId(at.E + de, at.N + dn);
                    if (!_slots.ContainsKey(id) && !_pending.Contains(id)) _pending.Add(id);
                }
        if (_drop.Count > 0) Report();
        if (_pending.Count > 0) _ = Fill();
    }

    private static bool SameTiles(List<TileId> a, List<TileId> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>True when the tile is within <paramref name="rings"/> tiles of some anchor's tile.</summary>
    private bool Within(TileId id, int rings)
    {
        foreach (var at in _anchorTiles)
            if (LodPolicy.Distance(id, at) <= rings) return true;
        return false;
    }

    /// <summary>Frees a tile's fleet: drawn, solid and worked out. Its awake slots stay awake.</summary>
    private void Drop(TileId id)
    {
        Clear(id);
        _slots.Remove(id);
        PalletService.Instance?.HideYard(id);
    }

    /// <summary>One line on what the layer holds, whenever that changes: what a perf log needs to see it.</summary>
    private void Report()
    {
        int slots = 0, bodies = 0;
        foreach (var (_, list) in _slots) slots += list.Count;
        foreach (var (_, list) in _solid) bodies += list.Count;
        GD.Print($"[dormant] {_slots.Count} tiles, {slots} slots, {bodies} bodies, {GetChildCount()} nodes");
    }

    /// <summary>
    /// Works out one waiting tile's fleet off the main thread (a <c>.road</c> is tens of
    /// milliseconds to decode) and draws it on. One tile at a time: a car park is not urgent.
    /// </summary>
    private async Task Fill()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            while (_pending.Count > 0 && IsInsideTree())
            {
                var id = _pending[0];
                _pending.RemoveAt(0);
                if (_slots.ContainsKey(id) || !Within(id, KeepRings)) continue;
                if (_chunks.Source is not { } source) return;

                // The provider seam. Nothing below this line knows what a bay is: a provider's only
                // job is to append `VehicleSlot`s, and the draw, the collision, the wake path and
                // the awake bookkeeping work off the record alone. An industrial yard (#496 phase 3)
                // has no bay list at all — it derives its standing positions from the building's
                // plan box and the cover around it — and joins by adding one call here.
                var (slots, pallets) = await Task.Run(() =>
                {
                    var roads = source.LoadRoadsAsync(id).GetAwaiter().GetResult();
                    var list = new List<VehicleSlot>();
                    var stacks = new List<YardPallet>();
                    if (roads is { Parking.Count: > 0 })
                        DormantSlots.ForParking(id, roads.Parking, ParkedKinds, list);
                    var buildings = source.LoadBuildingsAsync(id).GetAwaiter().GetResult();
                    Yards(source, id, buildings, roads, list, stacks);
                    Sites(source, id, buildings, roads, list, stacks);
                    Marina(source, id, list);
                    return (list, stacks);
                });
                if (!IsInsideTree()) return;
                // the anchors moved on while the worker read it
                if (!Within(id, KeepRings)) continue;

                Watch();
                _slots[id] = slots;
                // the yards' pallets live and die with the tile's fleet, but are the pallets' to draw
                if (pallets.Count > 0) PalletService.Instance?.ShowYard(id, pallets);
                if (slots.Count > 0)
                {
                    Draw(id);
                    Report();
                }
            }
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[dormant] {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// The second provider (#496 phase 3): an industrial site's yard. Costs a tile's <c>.bldg</c>
    /// and its height grid, so it is skipped entirely on a tile with no industrial building —
    /// which is nearly all of them. Worker thread, like the rest of <see cref="Fill"/>.
    ///
    /// <para>
    /// A slot the grid put inside a building or on a road is dropped here rather than in the
    /// provider, because deciding that needs the tile's geometry and the provider is tier-0 and has
    /// none. Dropping keeps the ordinals, which name the vehicle: a gap in the yard is fine, a
    /// renumbered fleet is not.
    /// </para>
    /// </summary>
    /// <param name="pallets">The pallets out on the sites' aprons (#583 phase 3), filled from the same
    /// fronts: drawn by <c>PalletService</c>, not by this layer.</param>
    private static void Yards(IChunkSource source, TileId id, BuildingTile? tile, RoadTile? roads, List<VehicleSlot> into, List<YardPallet> pallets)
    {
        // most tiles have buildings and no site: their height grid is 2 MB a streaming client
        // would download for nothing (#63)
        if (tile is not { Buildings.Count: > 0 } || !SiteYards.HasSite(tile)) return;
        var grid = source.LoadChunkAsync(id).GetAwaiter().GetResult();
        var fronts = SiteYards.Fronts(tile, roads, grid);
        if (fronts.Count == 0) return;
        var yards = fronts.Select(f => f.Yard).ToList();

        var map = BuildingTypes.For(tile);
        int before = into.Count;
        DormantSlots.ForSite(id, yards, ParkedKinds, YardHeavies, YardTrailers, (int)RideKind.Trailer, into);
        // a warehouse's and a works' forklift, on the apron beside the facade (#583 phase 3)
        foreach (var front in fronts)
            if (DormantSlots.ForkliftOf(id, front, (int)RideKind.Forklift) is { } lift) into.Add(lift);
        for (int i = into.Count - 1; i >= before; i--)
        {
            var s = into[i];
            var at = new Vector2((float)(s.E - id.MinE), (float)(id.MaxN - s.N));
            // a lorry needs more room round it than a hatchback before it reads as parked in a wall
            bool goods = s.Train != 0 || s.KindId == (int)RideKind.Trailer || s.KindId is >= HeavyCatalog.First and <= HeavyCatalog.Last;
            // (a forklift, RideKind 193, is past HeavyCatalog's range and parks as a car does)
            float radius = goods ? 3.2f : 1.6f;
            if (SiteYards.Blocked(tile, roads, map, at, radius)) { into.RemoveAt(i); continue; }
            // On its own ground, not the yard's (#560): a yard is one height, the ground at its
            // middle, and on a slope its far rows floated or sank by metres. Online, the server
            // keeps a woken vehicle exactly at its slot, so the slot's height is where it stands.
            if (grid != null) into[i] = s with { Height = GroundUnder(grid, s, goods) };
        }
        pallets.AddRange(SiteYards.Pallets(tile, roads, grid, fronts));
    }

    /// <summary>
    /// The fourth provider (#616): the machines parked on a building site, where its plan
    /// (<see cref="SitePlans"/>, the very plan the tile's site is built from) put them. Skipped on a
    /// tile with no site, which is nearly all of them. Each stands on its own ground; the planner
    /// already kept the yard's places off the building, the roads and the neighbours.
    /// </summary>
    /// <param name="pallets">The sites' pallets of bricks and cement (#615), handed to <c>PalletService</c> with the yards' stacks.</param>
    private static void Sites(IChunkSource source, TileId id, BuildingTile? tile, RoadTile? roads, List<VehicleSlot> into, List<YardPallet> pallets)
    {
        if (tile is not { Buildings.Count: > 0 } || !SitePlans.HasSite(tile)) return;
        var sites = SitePlans.For(tile, roads);
        if (sites.Count == 0) return;
        int before = into.Count;
        DormantSlots.ForConstruction(id, sites, SiteKind, ParkedKinds, into);
        var grid = source.LoadChunkAsync(id).GetAwaiter().GetResult();
        // the materials' pallets stand on the ground the dressing is drawn on, as the server works them out
        foreach (var site in sites) pallets.AddRange(SitePlans.PalletsOf(tile, site, grid));
        if (grid == null) return;
        for (int i = before; i < into.Count; i++) into[i] = into[i] with { Height = GroundUnder(grid, into[i], false) };
    }

    /// <summary>
    /// What a site's machine parks as: the excavator (#611), the wheel loader (#612), a quarter of
    /// them with forks (#615, from the slot's own roll), and the small kit (#614). The tipper, the
    /// mixer and the mini dumper (#613) join here once they can be driven.
    /// </summary>
    private static int? SiteKind(MachineRole role, ulong roll) => role switch
    {
        MachineRole.Excavator => (int)RideKind.Excavator,
        MachineRole.MiniExcavator => (int)RideKind.MiniExcavator,
        MachineRole.Roller => (int)RideKind.CompactRoller,
        MachineRole.Telehandler => (int)RideKind.Telehandler,
        MachineRole.WheelLoader => (roll >> 52 & 3) == 0 ? (int)RideKind.WheelLoaderForks : (int)RideKind.WheelLoader,
        _ => null,
    };

    /// <summary>
    /// The ground a slot stands at: for a car, the highest point under its box's corners and middle,
    /// where a box set down on a slope comes to rest; for a goods vehicle, the ground at its origin
    /// (its sections are posed on the ground axle by axle, <see cref="Poses"/>).
    /// </summary>
    private static double GroundUnder(ChunkGrid grid, VehicleSlot s, bool heavy)
    {
        double ground = grid.SampleMeshHeight(s.E, s.N);
        if (heavy) return ground;
        // yaw 0 = -Z = north: forward is (-sin, cos) in LV95 (east, north), right is (cos, sin)
        double fe = -Math.Sin(s.Yaw), fn = Math.Cos(s.Yaw), re = Math.Cos(s.Yaw), rn = Math.Sin(s.Yaw);
        const double halfLength = 2.2, halfWidth = 0.9;
        for (int a = -1; a <= 1; a += 2)
            for (int b = -1; b <= 1; b += 2)
                ground = Math.Max(ground, grid.SampleMeshHeight(
                    s.E + fe * halfLength * a + re * halfWidth * b, s.N + fn * halfLength * a + rn * halfWidth * b));
        return ground;
    }

    /// <summary>
    /// The third provider (#554): a harbour's moored boats. Costs the tile's full grid, cover and
    /// water, so it is skipped on a tile whose jetties are elsewhere, which is nearly all of them. The
    /// water comes from the source, as the tile's own build reads it, not from the loaded chunk: the
    /// tile may not be built yet, and the server and every client must agree on which berths float.
    /// Worker thread, like the rest of <see cref="Fill"/>.
    /// </summary>
    private static void Marina(IChunkSource source, TileId id, List<VehicleSlot> into)
    {
        var jetties = Landings.Current.Jetties;
        bool here = false;
        foreach (var j in jetties)
        {
            var (e, n) = j.Ribbon.Middle;
            if (TileId.FromLv95(e, n) == id) { here = true; break; }
        }
        if (!here) return;
        if (source.LoadChunkAsync(id).GetAwaiter().GetResult() is not { } grid) return;
        var cover = source.LoadCoverAsync(id).GetAwaiter().GetResult();
        if (ChunkManager.LoadWaterLayerAsync(source, id, grid, cover, default).GetAwaiter().GetResult() is not { } water) return;
        DormantSlots.ForMarina(id, jetties, (e, n) =>
        {
            // a berth a few metres across the tile's edge reads the edge's water
            double x = Math.Clamp(e - id.MinE, 0, ChunkFormat.TileSizeM), z = Math.Clamp(id.MaxN - n, 0, ChunkFormat.TileSizeM);
            if (!water.TrySample(x, z, out float level, out _)) return null;
            return (level, (float)grid.SampleHeight(id.MinE + x, id.MaxN - z));
        }, (int)RideKind.Speedboat, (int)RideKind.Jetski, into);
    }

    /// <summary>
    /// Builds a tile's dormant fleet: one static body per slot, its boxes sharing their shapes with
    /// every slot of the same look, and the drawing (<see cref="DrawLooks"/>). A slot already awake
    /// is skipped — its real vehicle stands there instead.
    /// </summary>
    private void Draw(TileId id)
    {
        Clear(id);
        if (!_slots.TryGetValue(id, out var slots)) return;

        // Every body of the tile, cars and lorries, in ONE list: the lorries' used to be stored and
        // then overwritten by the cars', so Clear never freed them and every redraw stacked another
        // copy of every lorry in the yard on top of the last (#552).
        //
        // One static body per slot. Only the tiles round an anchor hold a fleet, so they are created
        // with the tile rather than pooled by distance the way TreeColliders pools its trunks; if
        // --perflog ever says otherwise, pooling is the next step and the slot list is already the
        // right input for it.
        var bodies = new List<DormantBody>();
        foreach (var s in slots)
        {
            if (_awake.Contains(KeyOf(s))) continue;
            if (VehicleManager.Instance?.GetNodeOrNull(s.NodeName) != null)
            {
                // a late joiner: the vehicle is already in the world, so this slot woke before we arrived
                _awake.Add(KeyOf(s));
                continue;
            }
            if (LookOf(s) is not { } look) continue;
            var body = new DormantBody
            {
                Slot = s,
                Position = _origin.ToWorld(s.E, s.N, s.Height),
                Basis = new Basis(Vector3.Up, s.Yaw),
            };
            foreach (var (pose, shape) in look.Boxes)
                body.AddChild(new CollisionShape3D { Shape = shape, Transform = pose });
            AddChild(body);
            bodies.Add(body);
        }
        _solid[id] = bodies;
        DrawLooks(id);
    }

    /// <summary>
    /// (Re)draws a tile's fleet: one <see cref="MultiMesh"/> per look, so a lot of 80 cars of a
    /// dozen kinds is a dozen instancers and no node per car, and a lorry is drawn as the lorry it
    /// wakes as. Only these are redone when a slot wakes; the bodies are left alone. Every tile with
    /// a fleet has an entry, even an empty one: a yard of lorries alone used to have none, so a
    /// lorry woken there was never undrawn.
    /// </summary>
    private void DrawLooks(TileId id)
    {
        if (_drawn.Remove(id, out var old)) Free(old);
        if (!_slots.TryGetValue(id, out var slots)) return;

        var byLook = new Dictionary<DormantLooks.Key, List<VehicleSlot>>();
        foreach (var s in slots)
        {
            if (_awake.Contains(KeyOf(s))) continue;
            var key = DormantLooks.KeyOf(s);
            if (!byLook.TryGetValue(key, out var list)) byLook[key] = list = new List<VehicleSlot>();
            list.Add(s);
        }
        var instances = new List<MultiMeshInstance3D>(byLook.Count);
        bool flat = false;
        foreach (var (_, list) in byLook)
        {
            if (LookOf(list[0]) is not { } look) continue;
            if (look.Sections is { } sections)
            {
                // each section where the live train would stand it, all of a look's slots at once
                var perSection = new List<Transform3D>[sections.Length];
                for (int k = 0; k < sections.Length; k++) perSection[k] = new List<Transform3D>(list.Count);
                foreach (var s in list)
                {
                    var poses = Poses(s, look, out bool grounded);
                    flat |= !grounded;
                    for (int k = 0; k < sections.Length && k < poses.Length; k++) perSection[k].Add(poses[k]);
                }
                for (int k = 0; k < sections.Length; k++)
                    if (sections[k].GetSurfaceCount() > 0) instances.Add(Instanced(sections[k], perSection[k]));
            }
            else if (look.Mesh is { } mesh)
            {
                var at = new List<Transform3D>(list.Count);
                foreach (var s in list) at.Add(SlotTransform(s));
                instances.Add(Instanced(mesh, at));
            }
        }
        foreach (var mm in instances) AddChild(mm);
        _drawn[id] = instances;
        // a train drawn before the ground under it had loaded stands level: posed again on a later look
        if (flat) _unposed.Add(id);
        else _unposed.Remove(id);
    }

    /// <summary>
    /// Tiles whose trains were posed before the collision under them was built (on the terrain's
    /// height, which a live train does not stand on): posed again once it has been.
    /// </summary>
    private readonly HashSet<TileId> _unposed = new();
    private readonly List<TileId> _repose = new();

    /// <summary>Where a slot stands, a vehicle in one piece: its place and its heading.</summary>
    private Transform3D SlotTransform(VehicleSlot s) =>
        // a vehicle's model faces -Z, and a yaw about +Y with 0 = -Z is exactly that
        new(new Basis(Vector3.Up, s.Yaw), _origin.ToWorld(s.E, s.N, s.Height));

    /// <summary>
    /// A goods vehicle's sections where the live one stands them (#560): <see cref="HeavyGround.Stand"/>
    /// from the slot, on the ground's height under each axle, which is what <see cref="VehicleBody"/>
    /// poses a parked train with. Level at the slot where the ground has not loaded
    /// (<paramref name="grounded"/> false).
    /// </summary>
    private Transform3D[] Poses(VehicleSlot s, DormantLook look, out bool grounded)
    {
        bool ok = true;
        var world = GetWorld3D();
        float Ground(Vector3 p)
        {
            // the very query a parked train stands on (VehicleBody.Ground): what a vehicle collides with
            float y = World.GroundQuery.Under(world, GroundMask, _groundRay, _noExclude, p, _chunks, true, out bool solid);
            ok &= solid;
            return y;
        }
        var poses = HeavyGround.Stand(SlotTransform(s), look.Bodies!, look.NodeLocal!, Ground);
        grounded = ok;
        return poses;
    }

    /// <summary>What a parked vehicle's ground rays hit: its own collision mask (the default layer), trees aside.</summary>
    private const uint GroundMask = 1u;

    private readonly RayQuery _groundRay = new();
    private readonly Godot.Collections.Array<Rid> _noExclude = new();

    /// <summary>
    /// Where the dormant copy of a slot is drawn, section by section (one entry for a vehicle in one
    /// piece): what a check compares the woken vehicle with (#560).
    /// </summary>
    public Transform3D[] DrawnPoses(VehicleSlot s)
    {
        if (LookOf(s) is { Bodies: not null } look) return Poses(s, look, out _);
        return new[] { SlotTransform(s) };
    }

    /// <summary>
    /// The real vehicle stands in this slot now: its dormant copy goes, and nothing else is rebuilt —
    /// its body is freed and the tile's instancers are redone from the shared looks.
    /// </summary>
    private void Forget(TileId id, string key)
    {
        if (!_solid.TryGetValue(id, out var bodies)) return;
        for (int i = bodies.Count - 1; i >= 0; i--)
        {
            if (KeyOf(bodies[i].Slot) != key) continue;
            bodies[i].QueueFree();
            bodies.RemoveAt(i);
            DrawLooks(id);
            return;
        }
    }

    private static MultiMeshInstance3D Instanced(Mesh mesh, List<Transform3D> at)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = at.Count,
        };
        for (int i = 0; i < at.Count; i++) mm.SetInstanceTransform(i, at[i]);
        return new MultiMeshInstance3D
        {
            Multimesh = mm,
            VisibilityRangeEnd = DrawnM,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        };
    }


    private void Clear(TileId id)
    {
        if (_drawn.Remove(id, out var list)) Free(list);
        if (_solid.Remove(id, out var bodies))
            foreach (var b in bodies) b.QueueFree();
    }

    private static void Free(List<MultiMeshInstance3D> list)
    {
        foreach (var mm in list)
        {
            mm.Multimesh?.Dispose();
            mm.QueueFree();
        }
    }

    // No IOriginShiftAware: this is an IOriginContainer, so the shifter has already moved every body
    // and instancer by the time it would be told. Redrawing every fleet on top of that rebuilt each
    // lorry's model at every shift: 98 s for one teleport into Geneva (#552).

    // ---- what is standing where --------------------------------------------------------------

    /// <summary>
    /// The dormant slot nearest <paramref name="world"/> within <see cref="ReachM"/>, or null. What
    /// <c>VehicleReach</c> asks before it decides there is nothing to get into. With
    /// <paramref name="awakeToo"/>, a slot already woken counts as well: the bay is taken either way.
    /// </summary>
    public VehicleSlot? Nearest(Vector3 world, bool awakeToo = false)
    {
        VehicleSlot? best = null;
        float bestD = ReachM * ReachM;
        foreach (var (_, slots) in _slots)
            foreach (var s in slots)
            {
                if (!awakeToo && _awake.Contains(KeyOf(s))) continue;
                float d = _origin.ToWorld(s.E, s.N, s.Height).DistanceSquaredTo(world);
                if (d >= bestD) continue;
                bestD = d;
                best = s;
            }
        return best;
    }

    /// <summary>
    /// The slot standing at a place, woken or not, within <paramref name="within"/> flat metres:
    /// what a check asks to find a berth's or a bay's slot by where it is (LV95).
    /// </summary>
    public VehicleSlot? SlotAt(double e, double n, double within = 1.5)
    {
        foreach (var (_, slots) in _slots)
            foreach (var s in slots)
                if ((s.E - e) * (s.E - e) + (s.N - n) * (s.N - n) < within * within) return s;
        return null;
    }

    /// <summary>Every slot worked out round the anchors, woken or not: what a check picks one from.</summary>
    public IEnumerable<VehicleSlot> Slots()
    {
        foreach (var (_, slots) in _slots)
            foreach (var s in slots) yield return s;
    }

    /// <summary>True where this slot has already become a real vehicle.</summary>
    public bool IsAwake(VehicleSlot s) => _awake.Contains(KeyOf(s));

    private static string KeyOf(VehicleSlot s) => $"{s.Owner}|{s.Ordinal}";

    // ---- waking ------------------------------------------------------------------------------

    private bool Online => Multiplayer.MultiplayerPeer is not (null or OfflineMultiplayerPeer);

    /// <summary>
    /// Asks for a dormant slot to become a real vehicle. Offline it happens here; online the server
    /// decides, so two players touching the same car get one car. Safe to call twice.
    /// </summary>
    public void Wake(VehicleSlot slot)
    {
        string key = KeyOf(slot);
        if (_awake.Contains(key)) return;
        if (!Online) { Promote(slot); return; }
        if (Multiplayer.IsServer()) { ServerWake(slot.Owner, slot.Ordinal); return; }
        // the aim ray hits the dormant box every frame until the vehicle arrives: ask once, again
        // only if no answer came in AskAgainSeconds
        double now = GameClock.Now;
        if (_asked.TryGetValue(key, out double at) && now - at < AskAgainSeconds) return;
        _asked[key] = now;
        RpcId(1, MethodName.RequestWake, slot.Owner, slot.Ordinal);
    }

    /// <summary>Wakes asked of the server and not answered yet, by slot key, and when.</summary>
    private readonly Dictionary<string, double> _asked = new();

    private const double AskAgainSeconds = 2.0;

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeerExtension.TransferModeEnum.Reliable)]
    private void RequestWake(string owner, int ordinal)
    {
        if (!Multiplayer.IsServer()) return;
        ServerWake(owner, ordinal, Multiplayer.GetRemoteSenderId());
    }

    /// <summary>
    /// The server's half: the slot has to be one this peer worked out for itself from the tile, so a
    /// client cannot conjure a vehicle at a position of its choosing by asking for a slot that is
    /// not there.
    /// </summary>
    private void ServerWake(string owner, int ordinal, long peer = 0)
    {
        if (HallForklifts.BuildingOf(owner) is { } hall) { ServerWakeHall(hall, ordinal, peer); return; }
        if (Find(owner, ordinal) is not { } slot) return;
        if (_awake.Contains(KeyOf(slot))) return;
        // No "woken" broadcast (#560). Every peer drops its dormant copy when the vehicle node enters
        // its tree (OnVehicleAdded): the very frame the live one is drawn. A broadcast sent at once
        // could arrive before the spawn, which the server sends at its next poll, and leave the bay
        // empty in between.
        Promote(slot);
    }

    /// <summary>
    /// A hall's forklift (#630): the slot is the plan's, worked out here from the layout the server
    /// keeps (<see cref="HallForklifts.SlotOf"/>), so a client names a furniture index and nothing
    /// more. The asker has to be in that very building, as for a container (<c>LootService</c>);
    /// <paramref name="peer"/> 0 is the server's own, which has nobody to doubt.
    /// </summary>
    private async void ServerWakeHall(string building, int furniture, long peer)
    {
        if (InteriorManager.Instance is not { } interiors) return;
        if (peer != 0 && interiors.SpaceOf(peer) != building) return;
        InteriorLayout? layout;
        try { layout = await interiors.GetOrCreate(building); }
        catch (Exception e) { GD.PushError($"[dormant] hall {building}: {e.Message}"); return; }
        if (layout == null || HallForklifts.SlotOf(layout, furniture, _origin) is not { } slot) return;
        if (_awake.Contains(KeyOf(slot))) return;
        Promote(slot);
    }

    /// <summary>Puts the real vehicle in the slot. Returns false when nothing could be placed.</summary>
    private bool Promote(VehicleSlot slot)
    {
        if (VehicleManager.Instance is not { } vehicles) return false;
        if (vehicles.GetNodeOrNull(slot.NodeName) != null) { _awake.Add(KeyOf(slot)); return true; }

        // the state first, then its own ride: CreateRide is what knows a lone trailer from a truck
        // with one coupled to it from a plain car, so the wake path must not assume a mountable kind
        var state = StateOf(slot);
        if (state.CreateRide() is not { } ride) return false;
        state = state with { Health = ride.MaxHealth };
        // exactly where the dormant copy stood, asleep: not dropped a hand's breadth onto the ground (#560)
        if (vehicles.Place(state, slot.NodeName, settled: true) == null) return false;

        _awake.Add(KeyOf(slot));
        if (TileOf(slot.Owner) is { } id) Forget(id, KeyOf(slot));
        return true;
    }

    /// <summary>
    /// What a slot becomes. A car park hashes ordinary road cars; an industrial yard's provider
    /// (#496 phase 3) may name a truck, or <see cref="RideKind.Trailer"/> for one standing on its
    /// own legs — which is not mountable, and does not need to be.
    /// </summary>
    private static RideKind KindFor(VehicleSlot slot) => (RideKind)slot.KindId;

    private VehicleSlot? Find(string owner, int ordinal)
    {
        if (TileOf(owner) is not { } id || !_slots.TryGetValue(id, out var slots)) return null;
        // on owner AND ordinal: one tile can hold several owners (a car park and two yards), and
        // their ordinals start at 0 each
        foreach (var s in slots)
            if (s.Ordinal == ordinal && s.Owner == owner) return s;
        return null;
    }

    /// <summary>
    /// The tile an owner lives on: its first two parts. A car park's owner is exactly its tile
    /// (<c>E_N</c>); an industrial site's is a building (<c>E_N_Index</c>), so this must not insist
    /// on a part count any more than <see cref="SlotOf"/> does.
    /// </summary>
    private static TileId? TileOf(string owner)
    {
        var parts = owner.Split('_');
        return parts.Length >= 2 && int.TryParse(parts[0], out int e) && int.TryParse(parts[1], out int n)
            ? new TileId(e, n) : null;
    }
}
