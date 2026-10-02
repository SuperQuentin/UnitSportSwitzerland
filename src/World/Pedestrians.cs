using System.IO;
using Godot;
using UnitSport.Birds;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using static UnitSport.World.PedestrianRules;

namespace UnitSport.World;

/// <summary>
/// Pedestrians generated around players where they look, kept by the server (#217,
/// docs/notes/world/pedestrians.md). At <c>World/Pedestrians</c> on the server and every client.
/// <list type="bullet">
/// <item>The server (or an offline client) keeps every pedestrian as a light <see cref="Ped"/> record and
/// walks it from street spot to street spot (<see cref="TownPerches"/>, the town birds' pavements), with
/// no physics and no animation. New ones appear only where a player is about to look
/// (<see cref="PedestrianRules.SpawnOk"/>), in towns, until each player has its share around it.</item>
/// <item>4 times a second each peer gets the ones in its view cone (drawn, at most
/// <see cref="PedestrianRules.VisibleCap"/>) and the ones near it whatever the view (a capsule only):
/// a person behind you still bumps you. A puppet not mentioned for 1.5 s is gone on the client.</item>
/// <item>Clients report their camera's yaw (<see cref="ViewRpc"/>) and their bumps (<see cref="BumpRpc"/>).</item>
/// </list>
/// </summary>
public partial class Pedestrians : Node
{
    public const string NodeName = "Pedestrians";
    private const double SendInterval = 0.25, PuppetLife = 1.5;
    public const byte FlagVisible = 1, FlagKnocked = 2, FlagDirty = 4;

    private readonly WorldOrigin _origin;
    private readonly Terrain.ChunkManager? _chunks;
    private bool _server;
    private double _acc, _clock;

    public static Pedestrians? Instance { get; private set; }

    public static Pedestrians Create(Node world, WorldOrigin origin, Terrain.ChunkManager? chunks, bool server)
    {
        var p = new Pedestrians(origin, chunks) { Name = NodeName, _server = server };
        world.AddChild(p);
        return p;
    }

    private Pedestrians(WorldOrigin origin, Terrain.ChunkManager? chunks)
    {
        _origin = origin;
        _chunks = chunks;
    }

    public Pedestrians() : this(null!, null) { }

    private bool _stub;

    /// <summary>A swarm bot's (src/Net/Swarm): takes the snapshots, draws nothing, keeps nothing.</summary>
    public static Pedestrians Stub() => new() { Name = NodeName, _stub = true };

    public bool Online => NetLink.Online(this);
    /// <summary>Keeps the records: the server, or a client playing offline.</summary>
    public bool Authority => _server || !Online;

    public override void _Ready()
    {
        if (!_server && !_stub) Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    // =========================================================================================
    // server: the records
    // =========================================================================================

    /// <summary>One pedestrian as the server keeps it: where it is, where it walks to, how it looks, what happened to it.</summary>
    public sealed class Ped
    {
        public int Id, Seed, Leg;
        public Vector3 Pos, Goal;
        public float Yaw, Knocked;
        public byte State;
        public double Seen;
    }

    private readonly List<Ped> _peds = new();
    private readonly Dictionary<int, Ped> _byId = new();
    private int _nextId = 1;
    private readonly Random _rng = new(217);
    private readonly Dictionary<long, (float Yaw, float Rate, double At)> _views = new();
    private readonly Dictionary<(int, int), List<Ped>> _grid = new();
    private readonly List<Vector3> _spots = new();
    private readonly List<(long Peer, View View)> _viewers = new();

    /// <summary>Records kept (probes and the load test read it).</summary>
    public int Records => _peds.Count;
    public IReadOnlyList<Ped> All => _peds;

    public override void _Process(double delta)
    {
        if (_stub) return;
        _clock += delta;
        if (!_server && Online) ClientProcess((float)delta);
        if (!Authority) { MovePuppets((float)delta); return; }
        _acc += delta;
        if (_acc >= SendInterval)
        {
            float dt = (float)_acc;
            _acc = 0;
            Tick(dt);
        }
        MovePuppets((float)delta);
    }

    private void Tick(float dt)
    {
        GatherViewers();
        Index();
        foreach (var (_, v) in _viewers) Populate(v);
        Advance(dt);
        Forget();
        foreach (var (peer, v) in _viewers)
        {
            var data = Pack(v);
            if (peer == 0) ApplySnapshot(data);
            else RpcId(peer, MethodName.Snapshot, data);
        }
    }

    /// <summary>Each player's view: its body and velocity, and the camera yaw it last reported (else its heading).</summary>
    private void GatherViewers()
    {
        _viewers.Clear();
        if (!_server)
        {
            if (Camera() is { } cam && LocalPlayer() is { } me)
            {
                float yaw = YawOf(cam.GlobalBasis);
                _viewers.Add((0, new View(cam.GlobalPosition, yaw, TurnRate(yaw), me.Velocity)));
            }
            return;
        }
        if (GetNodeOrNull("../Players") is not { } players) return;
        for (int i = 0; i < players.GetChildCount(); i++)
        {
            if (players.GetChild(i) is not FootPlayer { Npc: false } p || FootPlayer.NetId(p.Name) is not long peer || peer <= 0) continue;
            var (yaw, rate) = _views.TryGetValue(peer, out var r) && _clock - r.At < 2 ? (r.Yaw, r.Rate) : (p.NetYaw, 0f);
            _viewers.Add((peer, new View(p.GlobalPosition + Vector3.Up * 1.6f, yaw, rate, p.NetVel)));
        }
    }

    private void Index()
    {
        foreach (var list in _grid.Values) list.Clear();
        foreach (var p in _peds)
        {
            var key = TownPerches.CellOf(p.Pos, TownPerches.Cell);
            if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<Ped>();
            list.Add(p);
        }
    }

    private bool Crowded(Vector3 at)
    {
        var (cx, cz) = TownPerches.CellOf(at, TownPerches.Cell);
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                if (_grid.TryGetValue((cx + dx, cz + dz), out var list))
                    foreach (var p in list)
                        if (Flat(p.Pos - at) < MinGap) return true;
        return false;
    }

    /// <summary>Tops up the people around one player: in a town, where it is about to look, a couple per tick.</summary>
    private void Populate(in View v)
    {
        if (BirdLife.Instance?.Town(v.Pos) is not { } town) return;
        int wanted = (int)(WantedPerPlayer * Mathf.Clamp((town.BuildingsAround(v.Pos) - 10) / 40f, 0f, 1f));
        int around = 0;
        foreach (var p in _peds)
        {
            if (Flat(p.Pos - v.Pos) > ViewRange) continue;
            around++;
            if (InView(v, p.Pos)) p.Seen = _clock;
        }
        if (around >= wanted || _peds.Count >= RecordCap) return;

        _spots.Clear();
        int r = (int)Mathf.Ceil(ViewRange / TownPerches.Cell);
        var (cx, cz) = TownPerches.CellOf(v.Pos, TownPerches.Cell);
        for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (!town.Cells.TryGetValue((cx + dx, cz + dz), out var cell)) continue;
                foreach (var perch in cell)
                    if (perch.Kind == PerchKind.Street && SpawnOk(v, perch.At)) _spots.Add(perch.At);
            }
        for (int n = 0; n < 2 && _spots.Count > 0 && around < wanted; n++)
        {
            int i = _rng.Next(_spots.Count);
            var at = _spots[i];
            _spots.RemoveAt(i);
            if (Crowded(at)) continue;
            var ped = new Ped { Id = _nextId++, Seed = _rng.Next(), Pos = at, Goal = at, Yaw = (float)(_rng.NextDouble() * Mathf.Tau), Seen = _clock };
            _peds.Add(ped);
            _byId[ped.Id] = ped;
            var key = TownPerches.CellOf(at, TownPerches.Cell);
            if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<Ped>();
            list.Add(ped);
            around++;
        }
    }

    /// <summary>Walks every record along its pavement: from one street spot to the next, nothing more.</summary>
    private void Advance(float dt)
    {
        var life = BirdLife.Instance;
        foreach (var p in _peds)
        {
            if (p.Knocked > 0f) { p.Knocked -= dt; if (p.Knocked <= 0f) p.State &= unchecked((byte)~FlagKnocked); continue; }
            var from = p.Pos;
            if (!PedestrianRules.Walk(ref p.Pos, p.Goal, Speed(p.Seed), dt)) { Face(p, from); continue; }
            if (life?.Town(p.Pos) is not { } town) continue;
            _spots.Clear();
            var (cx, cz) = TownPerches.CellOf(p.Pos, TownPerches.Cell);
            for (int dx = -2; dx <= 2; dx++)
                for (int dz = -2; dz <= 2; dz++)
                    if (town.Cells.TryGetValue((cx + dx, cz + dz), out var cell))
                        foreach (var perch in cell)
                            if (perch.Kind == PerchKind.Street) _spots.Add(perch.At);
            int next = PickNext(p.Pos, p.Yaw, _spots, p.Seed, ++p.Leg, town.Blocked);
            // nowhere to go on: turn round and pick again next time
            if (next < 0) p.Yaw += Mathf.Pi;
            else p.Goal = _spots[next];
        }
    }

    private static void Face(Ped p, Vector3 from)
    {
        var d = p.Pos - from;
        if (d.X * d.X + d.Z * d.Z > 1e-6f) p.Yaw = Mathf.Atan2(-d.X, -d.Z);
    }

    private void Forget()
    {
        for (int i = _peds.Count - 1; i >= 0; i--)
        {
            var p = _peds[i];
            float nearest = float.MaxValue;
            foreach (var (_, v) in _viewers) nearest = Mathf.Min(nearest, Flat(p.Pos - v.Pos));
            if (!PedestrianRules.Forget(nearest, (float)(_clock - p.Seen))) continue;
            _byId.Remove(p.Id);
            _peds.RemoveAt(i);
        }
    }

    private readonly MemoryStream _buf = new();
    private readonly List<(Ped P, float D)> _send = new();

    /// <summary>What one viewer gets: the nearest in view (drawn, up to the cap), and every one near enough to bump.</summary>
    private byte[] Pack(in View v)
    {
        _send.Clear();
        foreach (var p in _peds)
        {
            float d = Flat(p.Pos - v.Pos);
            if (d > ViewRange) continue;
            if (InView(v, p.Pos, 0.3f) || NeedsBody(v, p.Pos)) _send.Add((p, d));
        }
        _send.Sort((a, b) => a.D.CompareTo(b.D));
        _buf.SetLength(0);
        var w = new BinaryWriter(_buf);
        var anchor = _origin.ToGlobal(v.Pos);
        w.Write(anchor.E); w.Write(anchor.N); w.Write(anchor.Alt);
        int drawn = 0, count = 0;
        long countAt = _buf.Position;
        w.Write((ushort)0);
        foreach (var (p, _) in _send)
        {
            bool visible = drawn < VisibleCap && InView(v, p.Pos, 0.3f);
            if (!visible && !NeedsBody(v, p.Pos)) continue;
            if (visible) drawn++;
            var rel = p.Pos - v.Pos;
            w.Write(p.Id);
            w.Write(rel.X); w.Write(rel.Y); w.Write(rel.Z);
            w.Write((byte)(Mathf.PosMod(p.Yaw, Mathf.Tau) / Mathf.Tau * 256f));
            w.Write((ushort)(p.Seed & 0xFFFF));
            w.Write((byte)(p.State | (visible ? FlagVisible : 0)));
            count++;
        }
        _buf.Position = countAt;
        w.Write((ushort)count);
        return _buf.ToArray();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void Snapshot(byte[] data)
    {
        if (!_server && !_stub) ApplySnapshot(data);
    }

    /// <summary>Client: the camera's yaw and turn rate, 4 times a second, for the server's view cone.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void ViewRpc(float yaw, float rate)
    {
        if (!_server || !float.IsFinite(yaw) || !float.IsFinite(rate)) return;
        _views[Multiplayer.GetRemoteSenderId()] = (yaw, Mathf.Clamp(rate, -6f, 6f), _clock);
    }

    /// <summary>Client: its player ran into pedestrian <paramref name="id"/> (seen or not). The record falls over.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BumpRpc(int id)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (GetNodeOrNull<FootPlayer>("../Players/" + sender) is { } body && _byId.TryGetValue(id, out var p)
            && Flat(p.Pos - body.GlobalPosition) < 6f)
            Knock(p);
    }

    private static void Knock(Ped p)
    {
        if (p.Knocked > 0f) return;
        p.Knocked = 5f;
        p.State |= FlagKnocked;
        GD.Print($"[peds] #{p.Id} knocked over");
    }

    /// <summary>Server: marks a record dirty (a dropping, part 3 of #217); false if no such record.</summary>
    public bool Soil(int id)
    {
        if (!_byId.TryGetValue(id, out var p)) return false;
        p.State |= FlagDirty;
        return true;
    }

    // =========================================================================================
    // client: the puppets
    // =========================================================================================

    private sealed class Puppet
    {
        public int Id, Seed;
        public GlobalPos At;
        public Vector3 Vel;
        public float Yaw, Phase;
        public byte Flags;
        public double Heard, Bumped;
        public Node3D Root = null!;
        public MeshInstance3D? Mesh;
        public AnimatableBody3D? Body;
    }

    private readonly Dictionary<int, Puppet> _puppets = new();
    private readonly List<int> _gone = new();
    private double _viewAcc;
    private float _lastYaw;

    /// <summary>Puppets this client has (probes): drawn ones and body-only ones.</summary>
    public int Drawn { get; private set; }
    public int Bodies { get; private set; }
    public IEnumerable<(int Id, Vector3 Pos, bool Visible, bool Body, byte Flags)> Seen()
    {
        foreach (var p in _puppets.Values)
            yield return (p.Id, p.Root.GlobalPosition, p.Mesh is { Visible: true }, p.Body != null, p.Flags);
    }

    /// <summary>The drawn puppet of pedestrian <paramref name="id"/>, or null when this client does not draw it.</summary>
    public Node3D? Drawn3D(int id) => _puppets.TryGetValue(id, out var p) && p.Mesh is { Visible: true } ? p.Root : null;

    /// <summary>Server: the pedestrian nearly straight under <paramref name="at"/> (within <paramref name="reach"/> flat, 1.7–60 m below), 0 for none.</summary>
    public int Under(Vector3 at, float reach)
    {
        int best = 0;
        foreach (var p in _peds)
        {
            float d = Flat(p.Pos - at), above = at.Y - p.Pos.Y;
            if (d < reach && above is > 1.7f and < 60f) { reach = d; best = p.Id; }
        }
        return best;
    }

    private Camera3D? Camera() => GetViewport().GetCamera3D();
    private FootPlayer? LocalPlayer() => Camera()?.GetParent() as FootPlayer
        ?? GetNodeOrNull<FootPlayer>("../Players/" + Multiplayer.GetUniqueId());

    private static float YawOf(Basis b)
    {
        var f = -b.Z;
        return Mathf.Atan2(-f.X, -f.Z);
    }

    private float TurnRate(float yaw)
    {
        float rate = Mathf.AngleDifference(_lastYaw, yaw) / (float)SendInterval;
        _lastYaw = yaw;
        return rate;
    }

    private void ClientProcess(float dt)
    {
        _viewAcc += dt;
        if (_viewAcc < SendInterval || Camera() is not { } cam) return;
        _viewAcc = 0;
        float yaw = YawOf(cam.GlobalBasis);
        RpcId(1, MethodName.ViewRpc, yaw, TurnRate(yaw));
    }

    private void ApplySnapshot(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        var anchor = new GlobalPos(r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
        int count = r.ReadUInt16();
        for (int i = 0; i < count; i++)
        {
            int id = r.ReadInt32();
            var rel = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float yaw = r.ReadByte() / 256f * Mathf.Tau;
            int seed = r.ReadUInt16();
            byte flags = r.ReadByte();
            var at = new GlobalPos(anchor.E + rel.X, anchor.N - rel.Z, anchor.Alt + rel.Y);
            if (!_puppets.TryGetValue(id, out var p))
            {
                p = new Puppet { Id = id, Seed = seed, At = at, Root = new Node3D { Name = "Ped" + id } };
                AddChild(p.Root);
                _puppets[id] = p;
            }
            // the walk from the last word to this one, carried on until the next
            double heardFor = _clock - p.Heard;
            p.Vel = (flags & FlagKnocked) == 0 && heardFor < 1.0 ? _origin.ToWorld(at) - _origin.ToWorld(p.At) : Vector3.Zero;
            if (heardFor < 1.0 && heardFor > 0) p.Vel /= (float)SendInterval;
            if (p.Vel.Length() > 3f) p.Vel = Vector3.Zero;   // a jump (a new record, an origin shift): no slide
            p.At = at;
            p.Yaw = yaw;
            p.Flags = flags;
            p.Heard = _clock;
            Dress(p);
        }
    }

    private static CapsuleShape3D? _capsule;

    /// <summary>The mesh while it is seen, the capsule while it is near: each made or dropped on change only.</summary>
    private void Dress(Puppet p)
    {
        bool visible = (p.Flags & FlagVisible) != 0;
        if (visible && p.Mesh == null)
        {
            p.Mesh = new MeshInstance3D { MaterialOverride = Avatar.HumanMeshBuilder.FigureMaterial() };
            p.Root.AddChild(p.Mesh);
        }
        if (p.Mesh != null) p.Mesh.Visible = visible;
        // a dropping's mark stays as long as the record (part 3 of #217): drawn with the figure
        if ((p.Flags & FlagDirty) != 0 && p.Mesh != null && p.Mesh.GetChildCount() == 0) p.Mesh.AddChild(Birds.Droppings.Stain());
        if (p.Body == null)
        {
            p.Body = new AnimatableBody3D { CollisionLayer = TreeColliders.Layer, CollisionMask = 0, SyncToPhysics = false };
            p.Body.AddChild(new CollisionShape3D
            {
                Shape = _capsule ??= new CapsuleShape3D { Radius = 0.3f, Height = 1.75f },
                Position = new Vector3(0, 0.875f, 0),
            });
            p.Root.AddChild(p.Body);
        }
    }

    private void MovePuppets(float dt)
    {
        _gone.Clear();
        int drawn = 0, bodies = 0;
        var me = LocalPlayer();
        foreach (var p in _puppets.Values)
        {
            if (_clock - p.Heard > PuppetLife) { _gone.Add(p.Id); continue; }
            var pos = _origin.ToWorld(p.At) + p.Vel * (float)Mathf.Min(_clock - p.Heard, 0.5);
            // they stand on this client's ground, not the server's coarse one
            if (_chunks != null && _chunks.TryGetHeight(pos, out float g)) pos.Y = g;
            bool knocked = (p.Flags & FlagKnocked) != 0;
            p.Root.GlobalTransform = new Transform3D(
                new Basis(Vector3.Up, p.Yaw) * (knocked ? new Basis(Vector3.Right, -Mathf.Pi / 2) : Basis.Identity), pos);
            if (p.Body != null) bodies++;
            if (p.Mesh is { Visible: true } mesh)
            {
                drawn++;
                float speed = knocked ? 0f : p.Vel.Length();
                p.Phase = Mathf.PosMod(p.Phase + dt * speed / 1.5f, 1f);
                mesh.Mesh = Walker(p.Seed, speed > 0.2f ? p.Phase : -1f);
            }
            if (me != null && p.Body != null && !knocked && _clock - p.Bumped > 1.0) Bump(me, p, pos);
        }
        foreach (int id in _gone)
        {
            _puppets[id].Root.QueueFree();
            _puppets.Remove(id);
        }
        Drawn = drawn;
        Bodies = bodies;
    }

    /// <summary>The local player running or driving into a pedestrian knocks it over (reported to the server).</summary>
    private void Bump(FootPlayer me, Puppet p, Vector3 pos)
    {
        var d = me.GlobalPosition - pos;
        // ponytail: a vehicle's reach is a 1.2 m radius, not its hull; measure the hull if cars clip people
        float reach = me.Ride == RideKind.OnFoot ? 0.35f : 1.2f;
        if (Mathf.Abs(d.Y) > 1.6f || Flat(d) > 0.4f + reach || MathX.FlatLength(me.Velocity) < 2.5f) return;
        p.Bumped = _clock;
        if (Authority && _byId.TryGetValue(p.Id, out var rec)) Knock(rec);
        else if (Online) RpcId(1, MethodName.BumpRpc, p.Id);
    }

    // a few looks, a few stride frames each, shared by every puppet: no mesh built per frame
    private const int Looks = 12, Frames = 8;
    private static readonly ArrayMesh?[] Meshes = new ArrayMesh?[Looks * (Frames + 1)];

    private static ArrayMesh Walker(int seed, float phase)
    {
        int look = (int)((uint)seed % Looks);
        int frame = phase < 0 ? Frames : (int)(phase * Frames) % Frames;
        ref var m = ref Meshes[look * (Frames + 1) + frame];
        if (m != null) return m;
        var palette = Avatar.HumanPalette.ForRider(look * 5 + 3);
        return m = phase < 0 ? Avatar.HumanMeshBuilder.Build(palette) : Avatar.HumanMeshBuilder.BuildStride(palette, 1.3f, (float)frame / Frames);
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();
}
