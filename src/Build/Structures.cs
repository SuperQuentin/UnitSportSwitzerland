using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;

namespace UnitSport.Build;

/// <summary>
/// One structure: a grid with an LV95 origin (the corner of cell 0,0,0) and a yaw, and the pieces
/// on it. LV95 + altitude, like <see cref="PlacedObject"/>, so it survives a rebase and a restart.
/// <see cref="Match"/>: built during a Battle Royale match, kept in memory only and cleared after it.
/// </summary>
public sealed class Structure
{
    public long Id;
    public double E, N, Altitude;
    public float Yaw;
    public string Owner = "";
    public bool Match;
    public readonly Dictionary<Slot, Placed> Pieces = new();

    public Transform3D WorldTransform(WorldOrigin origin) =>
        new(new Basis(Vector3.Up, Yaw), origin.ToWorld(E, N, Altitude));

    public IReadOnlyCollection<Piece> Grid => Pieces.Values.Select(p => p.Piece).ToList();
}

/// <summary>A piece as placed: who built it, the damage it has taken, and when (server clock) it went up.</summary>
public sealed class Placed
{
    public Piece Piece;
    public string Owner = "";
    public float Damage;
    public double BuiltAt;

    public float Hp(double now) => BuildGrid.GrownHp(Piece.Kind, Piece.Material, now - BuiltAt) - Damage;
}

/// <summary>
/// Player-built structures (#274) at <c>World/Structures</c> on the server and every client (RPCs
/// route by path), on the <see cref="PlacedObjects"/> model: the server owns the list and checks
/// every request (reach, slot free, it stands up: <see cref="BuildGrid"/>), then tells every peer.
/// A joining peer gets everything. Free-roam structures are saved to <c>user://structures/server.json</c>
/// (offline: <c>offline.json</c>); match ones are kept in memory and cleared when the match is over.
///
/// <para>
/// Damage: a shooter's own trace finds a piece first (<see cref="BuildTool.TryHit"/>) and sends the
/// damage; the server caps it, checks who may damage what (the owner in free roam, anyone with
/// <c>/pvp on</c> or in a match), and on a broken piece removes it and everything it held up.
/// Docs: <c>docs/notes/build/building.md</c>.
/// </para>
/// </summary>
public partial class Structures : Node
{
    public const string NodeName = "Structures";
    public const string StructureMeta = "structure_id", SlotMeta = "structure_slot";

    /// <summary>How far from the builder's body a piece's centre may be (server check; the client aims within less).</summary>
    public const float Reach = 9f;
    /// <summary>Grid cells a structure may reach from its origin, either way, and storeys up and down.</summary>
    private const int MaxCells = 48, MinStorey = -4, MaxStorey = 24;

    public static Structures? Instance { get; private set; }

    /// <summary>Server: the display name of a peer (set from the chat), the owner of what it builds.</summary>
    public Func<long, string>? NameOf { get; set; }
    /// <summary>Server: whether a peer is a living entrant of a running match (BrManager.Playing).</summary>
    public Func<long, bool>? InMatch { get; set; }
    /// <summary>Server: whether a match is running, so its structures stay (BrState.Running).</summary>
    public Func<bool>? MatchRunning { get; set; }
    /// <summary>Server: the terrain height under a world point, when this server holds that terrain.</summary>
    public Func<Vector3, float?>? GroundAt { get; set; }

    public IReadOnlyDictionary<long, Structure> All => _structures;
    public WorldOrigin Origin => _origin;

    /// <summary>A piece went up, came down or changed (client): the build tool redraws its ghost from this.</summary>
    public event Action? Changed;

    private bool _server, _networked;
    private WorldOrigin _origin = null!;
    private string _storePath = "";
    private long _nextId = 1;
    private readonly Dictionary<long, Structure> _structures = new();
    private readonly Dictionary<int, Action<string?>> _pending = new();
    private int _nextRequest = 1;
    private StructureVisuals? _visuals;
    private double _sinceSweep;

    public static Structures Create(Node world, WorldOrigin origin, bool server, bool networked = false)
    {
        var s = new Structures { Name = NodeName, _server = server, _origin = origin, _networked = networked };
        world.AddChild(s);
        if (!server) Instance = s;
        return s;
    }

    public override void _Ready()
    {
        if (!_server)
        {
            _visuals = new StructureVisuals(this) { Name = "Visuals" };
            AddChild(_visuals);
            Multiplayer.ServerDisconnected += FailPending;
        }
        if (_server) Load("user://structures/server.json");
        else if (!_networked) Load("user://structures/offline.json");
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => Multiplayer.MultiplayerPeer is { } peer and not OfflineMultiplayerPeer
        && peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    private static double Now => ClockSync.ServerNow;

    // ---- geometry -------------------------------------------------------------------------------

    /// <summary>A piece's transform inside its structure: where its mesh's origin goes, turned its way.</summary>
    public static Transform3D LocalTransform(Piece p)
    {
        const float S = BuildGrid.Cell, H = BuildGrid.Storey;
        var (x, y, z) = (p.Slot.X, p.Slot.Y, p.Slot.Z);
        return p.Slot.Class switch
        {
            SlotClass.Floor => new(Basis.Identity, new Vector3((x + 0.5f) * S, y * H, (z + 0.5f) * S)),
            SlotClass.Edge when p.Slot.Side == 3 => new(new Basis(Vector3.Up, Mathf.Pi / 2), new Vector3(x * S, y * H, (z + 0.5f) * S)),
            SlotClass.Edge => new(Basis.Identity, new Vector3((x + 0.5f) * S, y * H, z * S)),
            _ => new(new Basis(Vector3.Up, DirYaw(p.Kind == PieceKind.Pillar ? 2 : p.Dir)), new Vector3((x + 0.5f) * S, y * H, (z + 0.5f) * S)),
        };
    }

    /// <summary>The yaw that turns a piece authored facing +Z toward <paramref name="dir"/> (0 −Z, 1 +X, 2 +Z, 3 −X).</summary>
    public static float DirYaw(int dir) => (dir & 3) switch { 0 => Mathf.Pi, 1 => Mathf.Pi / 2, 2 => 0f, _ => -Mathf.Pi / 2 };

    /// <summary>The middle of a piece in world space: what reach is measured to.</summary>
    public Vector3 Centre(Structure s, Piece p) =>
        s.WorldTransform(_origin) * (LocalTransform(p) * new Vector3(0, BuildGrid.Storey * 0.5f, 0));

    // ---- client API -----------------------------------------------------------------------------

    /// <summary>
    /// Asks to build <paramref name="piece"/> on structure <paramref name="structure"/>, or on a new
    /// structure at <paramref name="newAt"/> (origin corner, yaw in its basis) when it is 0.
    /// <paramref name="done"/> gets null when built, else why not (also when the connection drops):
    /// pay back the materials then.
    /// </summary>
    public void RequestBuild(long structure, Transform3D newAt, Piece piece, Action<string?> done)
    {
        var (e, n) = _origin.ToLv95(newAt.Origin);
        float yaw = newAt.Basis.GetEuler().Y;
        int req = Track(done);
        int[] packed = Pack(piece);
        if (Online) RpcId(1, MethodName.AskBuild, req, structure, e, n, (double)newAt.Origin.Y, yaw, packed);
        else ServeBuild(1, req, structure, e, n, newAt.Origin.Y, yaw, packed);
    }

    /// <summary>Asks to take back a piece you built. <paramref name="done"/> gets null when it is yours again.</summary>
    public void RequestRemove(long structure, Slot slot, Action<string?> done)
    {
        int req = Track(done);
        if (Online) RpcId(1, MethodName.AskRemove, req, structure, PackSlot(slot));
        else ServeRemove(1, req, structure, PackSlot(slot));
    }

    /// <summary>Reports damage this peer's shot did to a piece (its own trace found it first).</summary>
    public void SendHit(long structure, Slot slot, float damage, ItemId weapon)
    {
        if (Online) RpcId(1, MethodName.AskHit, structure, PackSlot(slot), damage, (int)weapon);
        else ServeHit(1, structure, PackSlot(slot), damage, (int)weapon);
    }

    /// <summary>The structure and slot a collider (or any node under a piece) belongs to.</summary>
    public static (long Structure, Slot Slot)? PieceOf(Node? node)
    {
        for (; node != null; node = node.GetParent())
            if (node.HasMeta(SlotMeta) && node.HasMeta(StructureMeta))
                return (node.GetMeta(StructureMeta).AsInt64(), UnpackSlot(node.GetMeta(SlotMeta).AsInt32Array()));
        return null;
    }

    /// <summary>After an origin rebase: every structure back where its LV95 position now is.</summary>
    public void Reposition() => _visuals?.RedrawAll();

    private int Track(Action<string?> done)
    {
        int req = _nextRequest++;
        _pending[req] = done;
        return req;
    }

    private void FailPending()
    {
        var pending = _pending.Values.ToList();
        _pending.Clear();
        foreach (var done in pending) done("Disconnected from the server.");
    }

    // ---- packing --------------------------------------------------------------------------------

    private static int[] PackSlot(Slot s) => new[] { s.X, s.Y, s.Z, (int)s.Class, s.Side };

    private static Slot UnpackSlot(int[] a) =>
        a.Length >= 5 ? new Slot(a[0], a[1], a[2], (SlotClass)a[3], a[4]) : default;

    private static int[] Pack(Piece p) =>
        new[] { p.Slot.X, p.Slot.Y, p.Slot.Z, (int)p.Slot.Class, p.Slot.Side, (int)p.Kind, p.Dir, (int)p.Material, p.Grounded ? 1 : 0 };

    private static Piece? Unpack(int[] a)
    {
        if (a.Length != 9 || !Enum.IsDefined((SlotClass)a[3]) || !Enum.IsDefined((PieceKind)a[5])
            || !Enum.IsDefined((BuildMaterial)a[7]) || a[4] is not (0 or 3) || a[6] is < 0 or > 3)
            return null;
        return new Piece(new Slot(a[0], a[1], a[2], (SlotClass)a[3], a[4]), (PieceKind)a[5], a[6], (BuildMaterial)a[7], a[8] == 1);
    }

    // ---- client: from the server ----------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AddStructure(long id, double e, double n, double alt, float yaw, string owner, bool match) =>
        PutStructure(new Structure { Id = id, E = e, N = n, Altitude = alt, Yaw = yaw, Owner = owner, Match = match });

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AddPiece(long structure, int[] packed, string owner, float damage, double builtAt, bool live)
    {
        if (!_structures.TryGetValue(structure, out var s) || Unpack(packed) is not { } piece) return;
        PutPiece(s, new Placed { Piece = piece, Owner = owner, Damage = damage, BuiltAt = builtAt }, live);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetDamage(long structure, int[] slot, float damage)
    {
        if (!_structures.TryGetValue(structure, out var s) || !s.Pieces.TryGetValue(UnpackSlot(slot), out var p)) return;
        p.Damage = damage;
        _visuals?.Damaged(s, p);
    }

    /// <summary>Pieces gone: <paramref name="broken"/> = destroyed or fallen (debris), else picked up.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RemovePieces(long structure, int[] slots, bool broken)
    {
        if (!_structures.TryGetValue(structure, out var s)) return;
        for (int i = 0; i + 4 < slots.Length; i += 5) DropPiece(s, UnpackSlot(slots[i..(i + 5)]), broken);
        if (s.Pieces.Count == 0) DropStructure(s.Id);
        Changed?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RemoveStructure(long id)
    {
        DropStructure(id);
        Changed?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Clear()
    {
        foreach (long id in _structures.Keys.ToList()) DropStructure(id);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Answer(int req, string refused)
    {
        if (_pending.Remove(req, out var done)) done(refused.Length > 0 ? refused : null);
    }

    private void PutStructure(Structure s)
    {
        if (_structures.ContainsKey(s.Id)) DropStructure(s.Id);
        _structures[s.Id] = s;
        _visuals?.AddStructure(s);
    }

    private void PutPiece(Structure s, Placed p, bool live)
    {
        s.Pieces[p.Piece.Slot] = p;
        _visuals?.AddPiece(s, p, live);
        Changed?.Invoke();
    }

    private void DropPiece(Structure s, Slot slot, bool broken)
    {
        if (!s.Pieces.Remove(slot, out var p)) return;
        _visuals?.RemovePiece(s, p, broken);
    }

    private void DropStructure(long id)
    {
        if (!_structures.Remove(id, out var s)) return;
        _visuals?.RemoveStructure(s);
    }

    // ---- server ---------------------------------------------------------------------------------

    /// <summary>Server: hands a joining peer every structure and piece.</summary>
    public void SendTo(long peer)
    {
        RpcId(peer, MethodName.Clear);
        foreach (var s in _structures.Values)
        {
            RpcId(peer, MethodName.AddStructure, s.Id, s.E, s.N, s.Altitude, s.Yaw, s.Owner, s.Match);
            foreach (var p in s.Pieces.Values)
                RpcId(peer, MethodName.AddPiece, s.Id, Pack(p.Piece), p.Owner, p.Damage, p.BuiltAt, false);
        }
    }

    public override void _Process(double delta)
    {
        if (!_server) return;
        _sinceSweep += delta;
        if (_sinceSweep < 2) return;
        _sinceSweep = 0;
        // a match is over: its structures go with it
        if (MatchRunning?.Invoke() == true) return;
        foreach (var s in _structures.Values.Where(s => s.Match).ToList())
        {
            DropStructure(s.Id);
            Broadcast(MethodName.RemoveStructure, s.Id);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskBuild(int req, long structure, double e, double n, double alt, float yaw, int[] packed) =>
        ServeBuild(Multiplayer.GetRemoteSenderId(), req, structure, e, n, alt, yaw, packed);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskRemove(int req, long structure, int[] slot) =>
        ServeRemove(Multiplayer.GetRemoteSenderId(), req, structure, slot);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskHit(long structure, int[] slot, float damage, int weapon) =>
        ServeHit(Multiplayer.GetRemoteSenderId(), structure, slot, damage, weapon);

    private string OwnerName(long peer) => Online ? NameOf?.Invoke(peer) ?? $"Rider{peer}" : "local";
    private bool PeerInMatch(long peer) => Online && InMatch?.Invoke(peer) == true;

    private void ServeBuild(long peer, int req, long structure, double e, double n, double alt, float yaw, int[] packed)
    {
        string owner = OwnerName(peer);
        bool match = PeerInMatch(peer);
        var piece = Unpack(packed);
        Structure? s = null;
        string? refused =
            piece is not { } p ? "Unknown piece."
            : Math.Abs(p.Slot.X) > MaxCells || Math.Abs(p.Slot.Z) > MaxCells || p.Slot.Y < MinStorey || p.Slot.Y > MaxStorey ? "Too far from the rest of it."
            : structure != 0 && !_structures.TryGetValue(structure, out s) ? "That structure is gone."
            : structure == 0 && (!double.IsFinite(e) || !double.IsFinite(n) || !double.IsFinite(alt) || !float.IsFinite(yaw)) ? "Bad position."
            : s != null && s.Match != match ? (match ? "Not on something built outside the match." : "Not on something built in a match.")
            : null;
        if (refused == null)
        {
            var probe = s ?? new Structure { E = e, N = n, Altitude = alt, Yaw = yaw };
            int mine = _structures.Values.Sum(x => x.Pieces.Values.Count(q => q.Owner == owner && x.Match == match));
            var centre = Centre(probe, piece!.Value);
            refused = !InReach(peer, centre) ? "Too far away."
                : mine >= (match ? BuildGrid.MaxPiecesMatch : BuildGrid.MaxPiecesFree) ? "You have built as much as you may."
                : piece.Value.Grounded && !GroundOk(probe, piece.Value) ? "Not on the ground there."
                : BuildGrid.CannotPlace(probe.Grid, piece.Value);
        }
        if (refused != null)
        {
            Reply(peer, req, refused);
            return;
        }

        if (s == null)
        {
            s = new Structure { Id = _nextId++, E = e, N = n, Altitude = alt, Yaw = yaw, Owner = owner, Match = match };
            PutStructure(s);
            Broadcast(MethodName.AddStructure, s.Id, s.E, s.N, s.Altitude, s.Yaw, s.Owner, s.Match);
        }
        var placed = new Placed { Piece = piece!.Value, Owner = owner, BuiltAt = Now };
        PutPiece(s, placed, live: true);
        Broadcast(MethodName.AddPiece, s.Id, Pack(placed.Piece), placed.Owner, 0f, placed.BuiltAt, true);
        if (!s.Match) Save();
        Reply(peer, req, "");
    }

    private void ServeRemove(long peer, int req, long structure, int[] slotArr)
    {
        var slot = UnpackSlot(slotArr);
        Placed? p = null;
        string? refused = !_structures.TryGetValue(structure, out var s) || !s.Pieces.TryGetValue(slot, out p) ? "It is not there any more."
            : p.Owner != OwnerName(peer) ? "That is not yours."
            : !InReach(peer, Centre(s, p.Piece)) ? "Too far away."
            : null;
        if (refused != null)
        {
            Reply(peer, req, refused);
            return;
        }
        Break(s!, slot, pickedUp: true);
        Reply(peer, req, "");
    }

    private void ServeHit(long peer, long structure, int[] slotArr, float damage, int weapon)
    {
        var slot = UnpackSlot(slotArr);
        if (!_structures.TryGetValue(structure, out var s) || !s.Pieces.TryGetValue(slot, out var p)) return;
        var def = Weapons.Get((ItemId)weapon);
        if (def == null || !float.IsFinite(damage) || damage <= 0 || damage > def.MaxHit) return;
        bool may = s.Match ? PeerInMatch(peer) : p.Owner == OwnerName(peer) || Combat.PvpRules.Enabled;
        if (!may || !InReach(peer, Centre(s, p.Piece), def.Range + 8f)) return;

        p.Damage += damage;
        if (p.Hp(Now) > 0)
        {
            _visuals?.Damaged(s, p);
            Broadcast(MethodName.SetDamage, s.Id, PackSlot(slot), p.Damage);
            return;
        }
        Break(s, slot, pickedUp: false);
    }

    /// <summary>Server: takes a piece away, then everything it alone held up, and tells everyone.</summary>
    private void Break(Structure s, Slot slot, bool pickedUp)
    {
        DropPiece(s, slot, broken: !pickedUp);
        var gone = new List<int>(PackSlot(slot));
        var fallen = BuildGrid.Fallen(s.Grid);
        foreach (var f in fallen)
        {
            DropPiece(s, f.Slot, broken: true);
            gone.AddRange(PackSlot(f.Slot));
        }
        // a pick-up names the one piece first (no debris for it); the rest fell
        if (pickedUp) Broadcast(MethodName.RemovePieces, s.Id, PackSlot(slot), false);
        var debris = pickedUp ? gone.Skip(5).ToArray() : gone.ToArray();
        if (debris.Length > 0) Broadcast(MethodName.RemovePieces, s.Id, debris, true);
        if (s.Pieces.Count == 0)
        {
            DropStructure(s.Id);
            Broadcast(MethodName.RemoveStructure, s.Id);
        }
        Changed?.Invoke();
        if (!s.Match) Save();
        GD.Print($"[build] {(pickedUp ? "picked up" : "broke")} {slot} on #{s.Id}, {fallen.Count} fell");
    }

    /// <summary>A grounded piece's foot is near the terrain, where this server has it; elsewhere the builder is trusted.</summary>
    private bool GroundOk(Structure s, Piece p)
    {
        var foot = s.WorldTransform(_origin) * LocalTransform(p).Origin;
        if (GroundAt?.Invoke(foot) is not { } ground) return true;
        float gap = foot.Y - ground;
        return gap > -BuildGrid.Storey && gap < BuildGrid.StiltMax + 1f;
    }

    private void Broadcast(StringName method, params Variant[] args)
    {
        if (!Online) return;
        foreach (int peer in Multiplayer.GetPeers()) RpcId(peer, method, args);
    }

    private void Reply(long peer, int req, string refused)
    {
        if (Online && peer != 1) RpcId(peer, MethodName.Answer, req, refused);
        else Answer(req, refused);
    }

    private bool InReach(long peer, Vector3 at, float reach = Reach)
    {
        if (!Online) return true;
        if (GetNodeOrNull<Node3D>("../Players/" + peer) is not { } body) return false;
        return body.GlobalPosition.DistanceTo(at) <= reach;
    }

    // ---- persistence (free roam only) -----------------------------------------------------------

    private sealed class Store
    {
        public long Next { get; set; } = 1;
        public List<SavedStructure> Structures { get; set; } = new();
    }

    private sealed class SavedStructure
    {
        public long Id { get; set; }
        public double E { get; set; }
        public double N { get; set; }
        public double Altitude { get; set; }
        public float Yaw { get; set; }
        public string Owner { get; set; } = "";
        public List<SavedPiece> Pieces { get; set; } = new();
    }

    private sealed class SavedPiece
    {
        public int[] Slot { get; set; } = Array.Empty<int>();
        public string Kind { get; set; } = "";
        public int Dir { get; set; }
        public string Material { get; set; } = "";
        public bool Grounded { get; set; }
        public string Owner { get; set; } = "";
        public float Damage { get; set; }
    }

    private void Load(string path)
    {
        _storePath = ProjectSettings.GlobalizePath(path);
        try
        {
            if (!File.Exists(_storePath)) return;
            var store = System.Text.Json.JsonSerializer.Deserialize<Store>(File.ReadAllText(_storePath)) ?? new Store();
            foreach (var ss in store.Structures)
            {
                var s = new Structure { Id = ss.Id, E = ss.E, N = ss.N, Altitude = ss.Altitude, Yaw = ss.Yaw, Owner = ss.Owner };
                PutStructure(s);
                foreach (var sp in ss.Pieces)
                {
                    // kinds and materials by name, so a reordered enum cannot turn wood into metal
                    if (sp.Slot.Length != 5 || !Enum.TryParse<PieceKind>(sp.Kind, out var kind)
                        || !Enum.TryParse<BuildMaterial>(sp.Material, out var mat)) continue;
                    var piece = new Piece(UnpackSlot(sp.Slot), kind, sp.Dir, mat, sp.Grounded);
                    // built long ago: grown
                    PutPiece(s, new Placed { Piece = piece, Owner = sp.Owner, Damage = sp.Damage, BuiltAt = double.MinValue / 4 }, false);
                }
                if (s.Pieces.Count == 0) DropStructure(s.Id);
            }
            _nextId = Math.Max(store.Next, _structures.Count > 0 ? _structures.Keys.Max() + 1 : 1);
            GD.Print($"[build] loaded {_structures.Count} structure(s) from {_storePath}");
        }
        catch (Exception ex) { GD.PushWarning($"[build] {_storePath}: {ex.Message}"); }
    }

    private void Save()
    {
        if (_storePath.Length == 0) _storePath = ProjectSettings.GlobalizePath("user://structures/offline.json");
        try
        {
            var store = new Store
            {
                Next = _nextId,
                Structures = _structures.Values.Where(s => !s.Match).OrderBy(s => s.Id).Select(s => new SavedStructure
                {
                    Id = s.Id, E = s.E, N = s.N, Altitude = s.Altitude, Yaw = s.Yaw, Owner = s.Owner,
                    Pieces = s.Pieces.Values.Select(p => new SavedPiece
                    {
                        Slot = PackSlot(p.Piece.Slot), Kind = p.Piece.Kind.ToString(), Dir = p.Piece.Dir,
                        Material = p.Piece.Material.ToString(), Grounded = p.Piece.Grounded, Owner = p.Owner, Damage = p.Damage,
                    }).ToList(),
                }).ToList(),
            };
            JsonStore.Save(_storePath, store, JsonStore.Indented);
        }
        catch (Exception ex) { GD.PushError($"[build] saving: {ex.Message}"); }
    }
}
