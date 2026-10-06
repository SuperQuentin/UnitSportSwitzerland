using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Vehicles;

/// <summary>
/// The cars already standing in the car parks (#499) — and, when #496 phase 3 lands, the fleets in
/// the industrial yards, through the same layer.
///
/// <para>
/// <b>Why they are dormant.</b> A big retail lot is 80 bays. Spawning 80 replicated
/// <see cref="VehicleBody"/> nodes per lot is not affordable, and scenery you cannot drive away is
/// not what a car park full of cars should be. So a slot is drawn as instanced geometry and nothing
/// else until someone actually touches it, and only then is it <b>promoted</b> to a real vehicle.
/// </para>
///
/// <para>
/// <b>Nothing is replicated.</b> <see cref="DormantSlots"/> is a pure function of the tile's bytes,
/// so the server and every client work out the same fleet independently — the
/// <see cref="BuildingTypes"/> trick. The only thing that ever goes on the wire is "slot N of owner
/// X is awake", once, when it wakes.
/// </para>
///
/// <para>
/// <b>Waking.</b> Anything that would move one asks <see cref="RequestWake"/>. The server promotes
/// the slot exactly once through <see cref="VehicleManager.Place"/> under
/// <see cref="VehicleSlot.NodeName"/> (a server-initiated <c>Place</c> is not subject to
/// <c>MayPark</c>, which is what <c>AfricaTwinEgg</c> relies on) and tells every peer to stop
/// drawing the dormant copy. Once woken a slot stays a vehicle for the session: re-sleeping is
/// deliberately left out until it is measured, as <c>docs/plans/industrial-sites.md</c> says.
/// </para>
///
/// <para>
/// <b>The look changes on waking</b>, and that is a known wart: a dormant car is drawn from the
/// shared low-poly meshes <see cref="Traffic"/> already uses for its traffic, and a woken one is a
/// real catalogue car. <see cref="KindFor"/> keeps the body shape and the paint as close as it can,
/// so what pops is the detail, not the car.
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
        Watch();
    }

    public override void _ExitTree()
    {
        if (_watched is { } vm && IsInstanceValid(vm))
            vm.ChildEnteredTree -= OnVehicleAdded;
        if (Instance == this) Instance = null;
    }

    private VehicleManager? _watched;

    /// <summary>
    /// Watches for a vehicle arriving under a slot's name.
    ///
    /// <para>
    /// The <see cref="Woken"/> broadcast only reaches the peers that were connected when the slot
    /// woke. A client that <b>joins afterwards</b> gets the vehicle in its join snapshot and no
    /// broadcast at all, and <see cref="Draw"/>'s one-off check only helps if the tile happens to be
    /// drawn after the spawn arrives — so a late joiner drew the dormant copy <i>and</i> the real
    /// car, one inside the other. The tier-2 check caught exactly that. Keying on the node actually
    /// existing is the fact rather than the message, so it covers the broadcast, the join snapshot
    /// and any later spawn alike.
    /// </para>
    /// </summary>
    private void Watch()
    {
        if (_watched != null || VehicleManager.Instance is not { } vehicles) return;
        _watched = vehicles;
        vehicles.ChildEnteredTree += OnVehicleAdded;
        // anything already standing there when this system started (a join snapshot that arrived first)
        foreach (var child in vehicles.GetChildren()) OnVehicleAdded(child);
    }

    private void OnVehicleAdded(Node node)
    {
        if (SlotOf(node.Name) is not { } key) return;
        if (!_awake.Add(key.Key)) return;
        Forget(key.Tile, key.Key);
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
                var slots = await Task.Run(() =>
                {
                    var roads = source.LoadRoadsAsync(id).GetAwaiter().GetResult();
                    var list = new List<VehicleSlot>();
                    if (roads is { Parking.Count: > 0 })
                        DormantSlots.ForParking(id, roads.Parking, ParkedKinds, list);
                    Yards(source, id, roads, list);
                    return list;
                });
                if (!IsInsideTree()) return;
                // the anchors moved on while the worker read it
                if (!Within(id, KeepRings)) continue;

                Watch();
                _slots[id] = slots;
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
    private static void Yards(IChunkSource source, TileId id, RoadTile? roads, List<VehicleSlot> into)
    {
        var tile = source.LoadBuildingsAsync(id).GetAwaiter().GetResult();
        // most tiles have buildings and no site: their height grid is 2 MB a streaming client
        // would download for nothing (#63)
        if (tile is not { Buildings.Count: > 0 } || !SiteYards.HasSite(tile)) return;
        var grid = source.LoadChunkAsync(id).GetAwaiter().GetResult();
        var yards = SiteYards.For(tile, roads, grid);
        if (yards.Count == 0) return;

        var map = BuildingTypes.For(tile);
        int before = into.Count;
        DormantSlots.ForSite(id, yards, ParkedKinds, YardHeavies, YardTrailers, (int)RideKind.Trailer, into);
        for (int i = into.Count - 1; i >= before; i--)
        {
            var s = into[i];
            var at = new Vector2((float)(s.E - id.MinE), (float)(id.MaxN - s.N));
            // a lorry needs more room round it than a hatchback before it reads as parked in a wall
            float radius = s.Train != 0 || s.KindId != (int)RideKind.Trailer && s.KindId >= HeavyCatalog.First ? 3.2f : 1.6f;
            if (SiteYards.Blocked(tile, roads, map, at, radius)) into.RemoveAt(i);
        }
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
        foreach (var (_, list) in byLook)
            if (LookOf(list[0]) is { Mesh: { } mesh })
                instances.Add(Instanced(mesh, list));
        foreach (var mm in instances) AddChild(mm);
        _drawn[id] = instances;
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

    private MultiMeshInstance3D Instanced(Mesh mesh, List<VehicleSlot> slots)
    {
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = slots.Count,
        };
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            // a vehicle's model faces -Z, and a yaw about +Y with 0 = -Z is exactly that
            var basis = new Basis(Vector3.Up, s.Yaw);
            mm.SetInstanceTransform(i, new Transform3D(basis, _origin.ToWorld(s.E, s.N, s.Height)));
        }
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
        if (_awake.Contains(KeyOf(slot))) return;
        if (!Online) { Promote(slot); return; }
        if (Multiplayer.IsServer()) { ServerWake(slot.Owner, slot.Ordinal); return; }
        RpcId(1, MethodName.RequestWake, slot.Owner, slot.Ordinal);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeerExtension.TransferModeEnum.Reliable)]
    private void RequestWake(string owner, int ordinal)
    {
        if (!Multiplayer.IsServer()) return;
        ServerWake(owner, ordinal);
    }

    /// <summary>
    /// The server's half: the slot has to be one this peer worked out for itself from the tile, so a
    /// client cannot conjure a vehicle at a position of its choosing by asking for a slot that is
    /// not there.
    /// </summary>
    private void ServerWake(string owner, int ordinal)
    {
        if (Find(owner, ordinal) is not { } slot) return;
        if (_awake.Contains(KeyOf(slot))) return;
        if (!Promote(slot)) return;
        Rpc(MethodName.Woken, owner, ordinal);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeerExtension.TransferModeEnum.Reliable)]
    private void Woken(string owner, int ordinal)
    {
        if (Find(owner, ordinal) is not { } slot) return;
        if (!_awake.Add(KeyOf(slot))) return;
        // the real vehicle stands there now: stop drawing the dormant copy of this one
        if (TileOf(owner) is { } id) Forget(id, KeyOf(slot));
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
        if (vehicles.Place(state, slot.NodeName) == null) return false;

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
