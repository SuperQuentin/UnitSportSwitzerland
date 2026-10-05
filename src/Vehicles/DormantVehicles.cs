using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
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

public partial class DormantVehicles : Node3D, IOriginContainer, IOriginShiftAware
{
    public static DormantVehicles? Instance { get; private set; }

    /// <summary>A slot this close to a point is the one that point means.</summary>
    public const float ReachM = 3.0f;

    /// <summary>Dormant cars are drawn to here, as <c>Traffic</c> draws its own to 600 m.</summary>
    private const float DrawnM = 400f;

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

    public DormantVehicles(ChunkManager chunks, WorldOrigin origin)
    {
        Name = "DormantVehicles";
        _chunks = chunks;
        _origin = origin;
    }

    public override void _Ready()
    {
        Instance = this;
        _chunks.TileEntered += OnTileEntered;
        Watch();
    }

    public override void _ExitTree()
    {
        _chunks.TileEntered -= OnTileEntered;
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
        if (_drawn.ContainsKey(key.Tile)) Draw(key.Tile);
    }

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

    // ---- the fleet of a tile -----------------------------------------------------------------

    private void OnTileEntered(TileId id)
    {
        if (_slots.ContainsKey(id) || _pending.Contains(id)) return;
        _pending.Add(id);
        _ = Fill();
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
                if (_slots.ContainsKey(id)) continue;
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
                    return list;
                });
                if (!IsInsideTree()) return;

                Watch();
                _slots[id] = slots;
                if (slots.Count > 0) Draw(id);
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
    /// Draws a tile's dormant fleet: one <see cref="MultiMesh"/> per look and part, so a lot of 80
    /// cars is a handful of draw calls and no nodes per car. A slot already awake is skipped — its
    /// real vehicle stands there instead.
    /// </summary>
    private void Draw(TileId id)
    {
        Clear(id);
        if (!_slots.TryGetValue(id, out var slots)) return;

        var byLook = new Dictionary<(byte Paint, bool Van), List<VehicleSlot>>();
        foreach (var s in slots)
        {
            if (_awake.Contains(KeyOf(s))) continue;
            if (VehicleManager.Instance?.GetNodeOrNull(s.NodeName) != null)
            {
                // a late joiner: the vehicle is already in the world, so this slot woke before we arrived
                _awake.Add(KeyOf(s));
                continue;
            }
            var look = (s.Paint, s.Van);
            if (!byLook.TryGetValue(look, out var list)) byLook[look] = list = new List<VehicleSlot>();
            list.Add(s);
        }
        if (byLook.Count == 0) return;

        var instances = new List<MultiMeshInstance3D>(byLook.Count * 2);
        foreach (var ((paint, van), list) in byLook)
        {
            var (body, lamps) = TrafficMeshBuilder.Car(TrafficMeshBuilder.Paints[paint % TrafficMeshBuilder.Paints.Length], van);
            instances.Add(Instanced(body, list, HumanMeshBuilder.Material()));
            instances.Add(Instanced(lamps, list, TrafficMeshBuilder.LampMaterial()));
        }
        foreach (var mm in instances) AddChild(mm);
        _drawn[id] = instances;

        // one static box per car. A lot is 80 of them and only a handful of tiles hold a lot, so
        // they are created with the tile rather than pooled by distance the way TreeColliders pools
        // its trunks; if --perflog ever says otherwise, pooling is the next step and the slot list
        // is already the right input for it.
        var bodies = new List<DormantBody>();
        foreach (var (_, list) in byLook)
            foreach (var s in list)
            {
                var box = new BoxShape3D { Size = new Vector3(s.Van ? 1.9f : 1.75f, s.Van ? 2.0f : 1.45f, s.Van ? 5.0f : 4.2f) };
                var body = new DormantBody
                {
                    Slot = s,
                    Position = _origin.ToWorld(s.E, s.N, s.Height) + Vector3.Up * box.Size.Y * 0.5f,
                    Basis = new Basis(Vector3.Up, s.Yaw),
                };
                body.AddChild(new CollisionShape3D { Shape = box });
                AddChild(body);
                bodies.Add(body);
            }
        _solid[id] = bodies;
    }

    private MultiMeshInstance3D Instanced(Mesh mesh, List<VehicleSlot> slots, Material material)
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
            // the meshes face -Z like a traffic car, and a yaw about +Y with 0 = -Z is exactly that
            var basis = new Basis(Vector3.Up, s.Yaw);
            mm.SetInstanceTransform(i, new Transform3D(basis, _origin.ToWorld(s.E, s.N, s.Height)));
        }
        return new MultiMeshInstance3D
        {
            Multimesh = mm,
            MaterialOverride = material,
            VisibilityRangeEnd = DrawnM,
            // a car park's cars are not worth a shadow pass each at distance
            CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
        };
    }

    private void Clear(TileId id)
    {
        if (_drawn.Remove(id, out var list))
            foreach (var mm in list)
            {
                mm.Multimesh?.Dispose();
                mm.QueueFree();
            }
        if (_solid.Remove(id, out var bodies))
            foreach (var b in bodies) b.QueueFree();
    }

    /// <summary>Every drawn instance moves with the origin: their transforms are world positions (#185).</summary>
    public void OnOriginShifted(OriginShift shift)
    {
        foreach (var id in _drawn.Keys.ToList()) Draw(id);
    }

    // ---- what is standing where --------------------------------------------------------------

    /// <summary>
    /// The dormant slot nearest <paramref name="world"/> within <see cref="ReachM"/>, or null. What
    /// <c>VehicleReach</c> asks before it decides there is nothing to get into.
    /// </summary>
    public VehicleSlot? Nearest(Vector3 world)
    {
        VehicleSlot? best = null;
        float bestD = ReachM * ReachM;
        foreach (var (_, slots) in _slots)
            foreach (var s in slots)
            {
                if (_awake.Contains(KeyOf(s))) continue;
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
        if (TileOf(owner) is { } id && _drawn.ContainsKey(id)) Draw(id);
    }

    /// <summary>Puts the real vehicle in the slot. Returns false when nothing could be placed.</summary>
    private bool Promote(VehicleSlot slot)
    {
        if (VehicleManager.Instance is not { } vehicles) return false;
        if (vehicles.GetNodeOrNull(slot.NodeName) != null) { _awake.Add(KeyOf(slot)); return true; }

        // the state first, then its own ride: CreateRide is what knows a lone trailer from a truck
        // with one coupled to it from a plain car, so the wake path must not assume a mountable kind
        var state = new VehicleState(KindFor(slot), new GlobalPos(slot.E, slot.N, slot.Height), slot.Yaw,
            Vector3.Zero, 0f, EngineOn: false, Wrecked: false, Throttle: 0f, SpawnedAt: 0,
            Train: slot.Train, Load: slot.Load);
        if (state.CreateRide() is not { } ride) return false;
        state = state with { Health = ride.MaxHealth };
        if (vehicles.Place(state, slot.NodeName) == null) return false;

        _awake.Add(KeyOf(slot));
        if (TileOf(slot.Owner) is { } id && _drawn.ContainsKey(id)) Draw(id);
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
