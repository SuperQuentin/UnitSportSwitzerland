using System.Text.Json;
using Godot;
using UnitSport.Net;
using UnitSport.Core;

namespace UnitSport.Items;

/// <summary>What a placed object is. Append new kinds at the end: the value is saved and sent.</summary>
public enum PlacedKind
{
    None = 0,
    /// <summary>A planted Swiss flag. Anyone may pull it up.</summary>
    Flag = 1,
    /// <summary>A photo stuck on something. Payload: the photo's id/hash. Only its owner removes it.</summary>
    Photo = 2,
    /// <summary>
    /// A campfire (#272). Payload: the Unix time it was lit, set by the server
    /// (<see cref="Crafting.CampfireClock"/>). Its owner may put it out; once burnt out, anyone may clear it.
    /// </summary>
    Campfire = 3,
    /// <summary>A field workbench (#272): a workbench station anywhere. Only its owner packs it up.</summary>
    FieldWorkbench = 4,

    // gadgets (#275, Build.Gadgets): only their owner removes them
    /// <summary>The low end of a zipline. Payload: the high end, "E;N;altitude" (invariant).</summary>
    Zipline = 5,
    /// <summary>A rope ladder hanging from its top. Payload: its length in metres (invariant).</summary>
    RopeLadder = 6,
    Trampoline = 7,
    LaunchPad = 8,
    CamoNet = 9,
    HayHideout = 10,
    /// <summary>A self-service farm stand (#494): its crates and honesty box are <c>Farming.FarmStands</c>' state. Only its owner packs it up, empty.</summary>
    FarmStand = 11,
}

/// <summary>
/// One object in the world. The position is LV95 plus altitude, not world space, so it survives an
/// origin rebase and a server restart with another origin; <see cref="WorldTransform"/> converts.
/// </summary>
public sealed record PlacedObject(long Id, PlacedKind Kind, string Owner, double E, double N, double Altitude,
    Quaternion Rotation, string Payload)
{
    public Transform3D WorldTransform(WorldOrigin origin) =>
        new(new Basis(Rotation), origin.ToWorld(E, N, Altitude));
}

/// <summary>The server's answer to a request: the object on success, else why not.</summary>
public readonly record struct PlacedResult(PlacedObject? Object, string? Refused)
{
    public bool Ok => Refused == null;
}

/// <summary>
/// Objects players leave in the world — planted flags, photos stuck on walls — at
/// <c>World/Placed</c> on the server and every client (RPCs route by node path).
///
/// <para>
/// <b>The server owns the list.</b> A client asks (<see cref="RequestPlace"/>,
/// <see cref="RequestRemove"/>); the server checks the requester stands within
/// <see cref="Reach"/> of the spot and, for kinds not in <see cref="RemovableByAnyone"/>, that it
/// placed it; then it tells every peer (<c>Add</c>/<c>Remove</c>) and answers the requester. A
/// joining peer gets the whole list. It is saved to <c>user://placed/server.json</c> on every
/// change and reloaded at start. Offline this client plays the server's part through the same
/// methods, saving to <c>user://placed/offline.json</c>.
/// </para>
///
/// <para>
/// <b>Visuals</b> come from a kind → factory registry (<see cref="RegisterFactory"/>); the node a
/// factory returns is tagged with the object's id (<see cref="IdOf"/> finds it from any collider
/// under it). A dedicated server builds none. Docs: <c>docs/notes/items/item-net-events.md</c>.
/// </para>
/// </summary>
public partial class PlacedObjects : Node
{
    public const string NodeName = "Placed";
    public const string Group = "placed_object";
    public const string IdMeta = "placed_id";

    /// <summary>How far from the requester's body an object may be placed or removed (server check).</summary>
    public const float Reach = 10f;
    private const int MaxPayload = 512;
    private const int MaxPerOwner = 200;

    public static PlacedObjects? Instance { get; private set; }

    /// <summary>The owner of what a Battle Royale match sets out (#276): nobody can take it, it is never saved, it goes with the match.</summary>
    public const string MatchOwner = "(match)";

    /// <summary>Kinds anyone may remove; every other kind only its owner. Must agree on the server.</summary>
    public static readonly HashSet<PlacedKind> RemovableByAnyone = new() { PlacedKind.Flag };

    private static readonly Dictionary<PlacedKind, Func<PlacedObject, Node3D>> Factories = new()
    {
        [PlacedKind.Flag] = _ => FlagVisual(),
        [PlacedKind.Photo] = PhotoVisuals.Placed,
        [PlacedKind.Campfire] = Crafting.StationVisuals.Campfire,
        [PlacedKind.FieldWorkbench] = Crafting.StationVisuals.Workbench,
        [PlacedKind.Zipline] = Build.GadgetMeshes.Visual,
        [PlacedKind.RopeLadder] = Build.GadgetMeshes.Visual,
        [PlacedKind.Trampoline] = Build.GadgetMeshes.Visual,
        [PlacedKind.LaunchPad] = Build.GadgetMeshes.Visual,
        [PlacedKind.CamoNet] = Build.GadgetMeshes.Visual,
        [PlacedKind.HayHideout] = Build.GadgetMeshes.Visual,
        [PlacedKind.FarmStand] = Farming.FarmStandVisual.Visual,
    };

    /// <summary>
    /// Whether anyone (not only its owner) may remove <paramref name="o"/> now: a kind in
    /// <see cref="RemovableByAnyone"/>, or a campfire that has burnt out. Must agree on the server.
    /// </summary>
    public static bool AnyoneMayRemove(PlacedObject o) =>
        RemovableByAnyone.Contains(o.Kind)
        || o.Kind == PlacedKind.Campfire && !Crafting.CampfireClock.Burning(o.Payload, World.WorldClock.EnvNow);

    /// <summary>
    /// Sets (or replaces) how a kind is drawn: the factory returns a node whose origin is the
    /// object's transform (it is positioned for you). Replacing it redraws what is already placed.
    /// </summary>
    public static void RegisterFactory(PlacedKind kind, Func<PlacedObject, Node3D> factory)
    {
        Factories[kind] = factory;
        Instance?.Redraw();
    }

    /// <summary>The placed object id a node (or any node under the object's visual) belongs to.</summary>
    public static long? IdOf(Node? node)
    {
        for (; node != null; node = node.GetParent())
            if (node.HasMeta(IdMeta)) return node.GetMeta(IdMeta).AsInt64();
        return null;
    }

    /// <summary>Server: the display name of a peer, the owner recorded on what it places (set from the chat).</summary>
    public Func<long, string>? NameOf { get; set; }

    public IReadOnlyDictionary<long, PlacedObject> All => _objects;
    /// <summary>The origin <see cref="PlacedObject.WorldTransform"/> is taken against here.</summary>
    public WorldOrigin Origin => _origin;
    public event Action<PlacedObject>? Added;
    public event Action<long>? Removed;

    private bool _server;

    /// <summary>This client will join a server (title screen's Multiplayer, or --connect), so the offline objects stay on disk.</summary>

    private bool _networked;
    private WorldOrigin _origin = null!;
    private string _storePath = "";
    private long _nextId = 1;
    private readonly Dictionary<long, PlacedObject> _objects = new();
    private readonly Dictionary<long, Node3D> _visuals = new();
    private readonly Dictionary<int, Action<PlacedResult>> _pending = new();
    private int _nextRequest = 1;

    public static PlacedObjects Create(Node world, WorldOrigin origin, bool server, bool networked = false)
    {
        var p = new PlacedObjects { Name = NodeName, _server = server, _origin = origin, _networked = networked };
        world.AddChild(p);
        if (!server) Instance = p;
        return p;
    }

    public override void _Ready()
    {
        if (_server)
        {
            Load("user://placed/server.json");
            return;
        }
        // a client that is going to connect shows the server's list, not the offline one
        if (!_networked) Load("user://placed/offline.json");
        Multiplayer.ServerDisconnected += FailPending;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => NetLink.Online(this);

    // ---- client API -----------------------------------------------------------------------------

    /// <summary>
    /// Asks to place an object at a world transform. <paramref name="done"/> runs once with the
    /// placed object, or with the reason it was refused (also if the connection drops meanwhile) —
    /// give back whatever was spent on it then.
    /// </summary>
    public void RequestPlace(PlacedKind kind, Transform3D at, string payload, Action<PlacedResult>? done = null)
    {
        var (e, n) = _origin.ToLv95(at.Origin);
        var rot = at.Basis.Orthonormalized().GetRotationQuaternion();
        int req = Track(done);
        if (Online) RpcId(1, MethodName.AskPlace, req, (int)kind, e, n, (double)at.Origin.Y, rot, payload);
        else ServePlace(1, req, (int)kind, e, n, at.Origin.Y, rot, payload);
    }

    /// <summary>Asks to remove an object. <paramref name="done"/> gets the removed object, or the reason it stays.</summary>
    public void RequestRemove(long id, Action<PlacedResult>? done = null)
    {
        int req = Track(done);
        if (Online) RpcId(1, MethodName.AskRemove, req, id);
        else ServeRemove(1, req, id);
    }

    private int Track(Action<PlacedResult>? done)
    {
        int req = _nextRequest++;
        if (done != null) _pending[req] = done;
        return req;
    }

    private void FailPending()
    {
        var pending = _pending.Values.ToList();
        _pending.Clear();
        foreach (var done in pending) done(new PlacedResult(null, "Disconnected from the server."));
    }

    // ---- client: from the server ----------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Add(long id, int kind, string owner, double e, double n, double alt, Quaternion rot, string payload) =>
        Spawned(Put(new PlacedObject(id, (PlacedKind)kind, owner, e, n, alt, rot, payload)));

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Remove(long id) => Drop(id);

    /// <summary>The whole list, on joining: replaces whatever this client showed before.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Snapshot(long[] ids, int[] kinds, string[] owners, double[] pos, float[] rot, string[] payloads)
    {
        foreach (long id in _objects.Keys.ToList()) Drop(id);
        for (int i = 0; i < ids.Length; i++)
            Put(new PlacedObject(ids[i], (PlacedKind)kinds[i], owners[i], pos[3 * i], pos[3 * i + 1], pos[3 * i + 2],
                new Quaternion(rot[4 * i], rot[4 * i + 1], rot[4 * i + 2], rot[4 * i + 3]), payloads[i]));
        GD.Print($"[placed] snapshot: {ids.Length} object(s)");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Answer(int req, long id, string refused)
    {
        if (!_pending.Remove(req, out var done)) return;
        if (refused.Length > 0) done(new PlacedResult(null, refused));
        else done(new PlacedResult(_objects.GetValueOrDefault(id) ?? _lastRemoved.GetValueOrDefault(id), null));
    }

    /// <summary>Objects just removed, so a removal's answer (which follows the <c>Remove</c>) can still hand them back.</summary>
    private readonly Dictionary<long, PlacedObject> _lastRemoved = new();

    private PlacedObject Put(PlacedObject o)
    {
        if (_objects.ContainsKey(o.Id)) Drop(o.Id);
        _objects[o.Id] = o;
        if (!_server) Draw(o);
        Added?.Invoke(o);
        return o;
    }

    /// <summary>A live placement (not the join snapshot, not a redraw): the local planting effect.</summary>
    private void Spawned(PlacedObject o)
    {
        if (o.Kind == PlacedKind.Flag && _visuals.TryGetValue(o.Id, out var node) && IsInstanceValid(node)) FlagFx.Spawned(node);
    }

    private void Drop(long id)
    {
        if (!_objects.Remove(id, out var o)) return;
        if (_lastRemoved.Count > 32) _lastRemoved.Clear();
        _lastRemoved[id] = o;
        if (_visuals.Remove(id, out var node) && IsInstanceValid(node)) node.QueueFree();
        Removed?.Invoke(id);
    }

    private void Draw(PlacedObject o)
    {
        if (!Factories.TryGetValue(o.Kind, out var factory)) return;
        Node3D node;
        try { node = factory(o); }
        catch (Exception ex)
        {
            GD.PushError($"[placed] {o.Kind} factory: {ex.Message}");
            return;
        }
        node.Name = $"P{o.Id}";
        node.SetMeta(IdMeta, o.Id);
        node.AddToGroup(Group);
        node.Transform = o.WorldTransform(_origin);
        AddChild(node);
        _visuals[o.Id] = node;
        // near music (#734) its meshes squash and hop on the beat; the body and its colliders stay
        foreach (var child in node.GetChildren())
            if (child is MeshInstance3D mesh) BeatField.Add(mesh, 0.7f, 0.15f);
    }

    private void Redraw()
    {
        if (_server) return;
        foreach (var node in _visuals.Values)
            if (IsInstanceValid(node)) node.QueueFree();
        _visuals.Clear();
        foreach (var o in _objects.Values) Draw(o);
    }

    // ---- server ---------------------------------------------------------------------------------

    /// <summary>Server: hands a joining peer the whole list.</summary>
    public void SendTo(long peer)
    {
        var list = _objects.Values.OrderBy(o => o.Id).ToList();
        RpcId(peer, MethodName.Snapshot,
            list.Select(o => o.Id).ToArray(),
            list.Select(o => (int)o.Kind).ToArray(),
            list.Select(o => o.Owner).ToArray(),
            list.SelectMany(o => new[] { o.E, o.N, o.Altitude }).ToArray(),
            list.SelectMany(o => new[] { o.Rotation.X, o.Rotation.Y, o.Rotation.Z, o.Rotation.W }).ToArray(),
            list.Select(o => o.Payload).ToArray());
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskPlace(int req, int kind, double e, double n, double alt, Quaternion rot, string payload) =>
        ServePlace(Multiplayer.GetRemoteSenderId(), req, kind, e, n, alt, rot, payload);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskRemove(int req, long id) => ServeRemove(Multiplayer.GetRemoteSenderId(), req, id);

    private string OwnerName(long peer) => Online ? NameOf?.Invoke(peer) ?? $"Rider{peer}" : "local";

    private void ServePlace(long peer, int req, int kind, double e, double n, double alt, Quaternion rot, string payload)
    {
        string owner = OwnerName(peer);
        string? refused =
            !Enum.IsDefined((PlacedKind)kind) || kind == 0 ? "Unknown object."
            : payload.Length > MaxPayload ? "Too much data."
            : !double.IsFinite(e) || !double.IsFinite(n) || !double.IsFinite(alt) || !rot.IsFinite() ? "Bad position."
            : !InReach(peer, e, n, alt) ? "Too far away."
            : _objects.Values.Count(o => o.Owner == owner) >= MaxPerOwner ? $"You already placed {MaxPerOwner} things."
            // a kind's own rules (a zipline's length and slope): Build.Gadgets
            : Build.Gadgets.Check(new PlacedObject(0, (PlacedKind)kind, owner, e, n, alt, rot, payload), _origin);
        if (refused != null)
        {
            Reply(peer, req, 0, refused);
            return;
        }

        // a campfire is lit now, by the server's clock: what the client sent does not count
        if ((PlacedKind)kind == PlacedKind.Campfire) payload = Crafting.CampfireClock.Lit(World.WorldClock.EnvNow);
        var o = new PlacedObject(_nextId++, (PlacedKind)kind, owner, e, n, alt, rot.Normalized(), payload);
        Spawned(Put(o));   // offline the client plays the server's part; a dedicated server has no visual, so it is a no-op there
        Save();
        GD.Print(FormattableString.Invariant($"[placed] {o.Kind} #{o.Id} by {owner} at LV95 {e:F1}/{n:F1}"));
        if (Online)
            foreach (int p in Multiplayer.GetPeers())
                RpcId(p, MethodName.Add, o.Id, (int)o.Kind, o.Owner, o.E, o.N, o.Altitude, o.Rotation, o.Payload);
        Reply(peer, req, o.Id, "");
    }

    /// <summary>Server: sets something down on its own authority (a match's gadgets): no checks, told to everyone.</summary>
    public PlacedObject ServerPlace(PlacedKind kind, Transform3D at, string payload, string owner)
    {
        var (e, n) = _origin.ToLv95(at.Origin);
        var o = Put(new PlacedObject(_nextId++, kind, owner, e, n, at.Origin.Y, at.Basis.Orthonormalized().GetRotationQuaternion(), payload));
        if (owner != MatchOwner) Save();
        if (Online)
            foreach (int p in Multiplayer.GetPeers())
                RpcId(p, MethodName.Add, o.Id, (int)o.Kind, o.Owner, o.E, o.N, o.Altitude, o.Rotation, o.Payload);
        return o;
    }

    /// <summary>Server: takes away everything one owner set down (the match's gadgets when it is over).</summary>
    public void ClearOwner(string owner)
    {
        foreach (var o in _objects.Values.Where(o => o.Owner == owner).ToList())
        {
            Drop(o.Id);
            if (Online)
                foreach (int p in Multiplayer.GetPeers())
                    RpcId(p, MethodName.Remove, o.Id);
        }
    }

    private void ServeRemove(long peer, int req, long id)
    {
        string? refused = !_objects.TryGetValue(id, out var o) ? "It is not there any more."
            : !AnyoneMayRemove(o) && o.Owner != OwnerName(peer) ? "That is not yours."
            : !InReach(peer, o.E, o.N, o.Altitude) ? "Too far away."
            : Farming.FarmStands.RemoveProblem(o);
        if (refused != null)
        {
            Reply(peer, req, 0, refused);
            return;
        }

        Drop(id);
        Save();
        GD.Print($"[placed] {o!.Kind} #{id} removed by {OwnerName(peer)}");
        if (Online)
            foreach (int p in Multiplayer.GetPeers())
                RpcId(p, MethodName.Remove, id);
        Reply(peer, req, id, "");
    }

    private void Reply(long peer, int req, long id, string refused)
    {
        // the server's own "peer" is only ever the offline client
        if (Online && peer != 1) RpcId(peer, MethodName.Answer, req, id, refused);
        else Answer(req, id, refused);
    }

    /// <summary>Within reach of the requester's body; offline there is nobody to doubt.</summary>
    private bool InReach(long peer, double e, double n, double alt, float reach = Reach)
    {
        if (!Online) return true;
        if (GetNodeOrNull<Player.FootPlayer>("../Players/" + peer) is not { } body) return false;
        // in LV95, from what the player published: the server's origin may be far away (#185)
        return body.Global.DistanceTo(new GlobalPos(e, n, alt)) <= reach;
    }

    // ---- a flare sets a hay hideout alight (#359) --------------------------------------------------

    /// <summary>How far from the shooter a flare can set a hay hideout alight (server check).</summary>
    public const float BurnReach = 30f;

    /// <summary>Asks to burn a hay hideout the requester's flare hit; anyone's, not only one's own.</summary>
    public void RequestBurn(long id)
    {
        if (Online) RpcId(1, MethodName.AskBurn, id);
        else ServeBurn(1, id);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskBurn(long id) => ServeBurn(Multiplayer.GetRemoteSenderId(), id);

    private void ServeBurn(long peer, long id)
    {
        if (!_objects.TryGetValue(id, out var o) || o.Kind != PlacedKind.HayHideout || !InReach(peer, o.E, o.N, o.Altitude, BurnReach)) return;
        GD.Print($"[placed] HayHideout #{id} set alight by {OwnerName(peer)}");
        if (Online)
            foreach (int p in Multiplayer.GetPeers())
                RpcId(p, MethodName.Burnt, id);
        Burnt(id);   // here: offline the client's own fire, on a server just the removal
        if (o.Owner != MatchOwner) Save();
    }

    /// <summary>Burns down where it stands (fire, smoke, light for a few seconds), then is gone.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Burnt(long id)
    {
        if (!_server && _objects.TryGetValue(id, out var o)) Build.GadgetMeshes.Burn(this, o.WorldTransform(_origin));
        Drop(id);
    }

    // ---- persistence ----------------------------------------------------------------------------

    private sealed class Store
    {
        public long Next { get; set; } = 1;
        public List<Entry> Objects { get; set; } = new();
    }

    private sealed class Entry
    {
        public long Id { get; set; }
        public string Kind { get; set; } = "";
        public string Owner { get; set; } = "";
        public double E { get; set; }
        public double N { get; set; }
        public double Altitude { get; set; }
        public float[] Rotation { get; set; } = { 0, 0, 0, 1 };
        public string Payload { get; set; } = "";
    }

    private void Load(string path)
    {
        _storePath = ProjectSettings.GlobalizePath(path);
        try
        {
            if (!File.Exists(_storePath)) return;
            // System.Text.Json reads and writes numbers culture-invariantly
            var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(_storePath)) ?? new Store();
            foreach (var en in store.Objects)
            {
                // by name, so a reordered enum does not turn flags into photos
                if (!Enum.TryParse<PlacedKind>(en.Kind, out var kind) || en.Rotation.Length != 4) continue;
                Put(new PlacedObject(en.Id, kind, en.Owner, en.E, en.N, en.Altitude,
                    new Quaternion(en.Rotation[0], en.Rotation[1], en.Rotation[2], en.Rotation[3]), en.Payload));
            }
            _nextId = Math.Max(store.Next, _objects.Count > 0 ? _objects.Keys.Max() + 1 : 1);
            GD.Print($"[placed] loaded {_objects.Count} object(s) from {_storePath}");
        }
        catch (Exception ex) { GD.PushWarning($"[placed] {_storePath}: {ex.Message}"); }
    }

    private void Save()
    {
        if (_storePath.Length == 0) _storePath = ProjectSettings.GlobalizePath("user://placed/offline.json");
        try
        {
            var store = new Store
            {
                Next = _nextId,
                Objects = _objects.Values.Where(o => o.Owner != MatchOwner).OrderBy(o => o.Id).Select(o => new Entry
                {
                    Id = o.Id, Kind = o.Kind.ToString(), Owner = o.Owner, E = o.E, N = o.N, Altitude = o.Altitude,
                    Rotation = new[] { o.Rotation.X, o.Rotation.Y, o.Rotation.Z, o.Rotation.W }, Payload = o.Payload,
                }).ToList(),
            };
            Core.JsonStore.SaveAsync(_storePath, store, Core.JsonStore.Indented, e => GD.PushError($"[placed] saving: {e.Message}"));
        }
        catch (Exception ex) { GD.PushError($"[placed] saving: {ex.Message}"); }
    }

    // ---- built-in visuals -----------------------------------------------------------------------

    /// <summary>The planted Swiss flag: the item's planted mesh on a pole-thin collider.</summary>
    public static Node3D FlagVisual()
    {
        var body = new StaticBody3D();
        body.AddChild(new MeshInstance3D { Mesh = ItemDefs.PlantedFlagMesh(), MaterialOverride = ItemDefs.Material });
        // pole-thin, so it is something to aim at for picking up rather than a wall to walk into
        body.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(0.12f, 1.9f, 0.12f) },
            Position = new Vector3(0, 0.95f, 0),
        });
        return body;
    }
}
