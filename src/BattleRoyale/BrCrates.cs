using System.Text.Json;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.BattleRoyale;

/// <summary>What a crate is, for its look and its name. Sent as an int: append only.</summary>
public enum CrateStyle
{
    DeathBox = 0, Supply = 1, Military = 2, Airdrop = 3,
    // the outdoor sites (#198), and what is left of a supply crate shot open
    Bunker = 4, HighSeat = 5, HayStash = 6, SacBox = 7, Wreck = 8, FishingHut = 9, Pile = 10,
}

/// <summary>One crate of a match. <see cref="Alt"/> = <see cref="BrCrates.Ground"/>: on the ground wherever that is.</summary>
public sealed class Crate
{
    public long Id { get; set; }
    public CrateStyle Style { get; set; }
    public double E { get; set; }
    public double N { get; set; }
    public float Alt { get; set; } = BrCrates.Ground;
    /// <summary>Server clock at which it is on the ground and can be opened (an airdrop falls first).</summary>
    public double LandsAt { get; set; }
    public string Label { get; set; } = "";
    /// <summary>Which way it faces (rad, node yaw: see <see cref="BrSites.YawFacing"/>); NaN for "any".</summary>
    public float Yaw { get; set; } = float.NaN;
    /// <summary>Closed with a dial (a bunker door): cracked with the lock-picking dial first.</summary>
    public bool Locked { get; set; }
    public int[] Ids { get; set; } = Array.Empty<int>();
    public int[] Counts { get; set; } = Array.Empty<int>();

    public List<ItemStack> Stacks() => Ids.Zip(Counts, (i, c) => new ItemStack((ItemId)i, c)).ToList();

    public void SetStacks(IEnumerable<ItemStack> stacks)
    {
        var list = stacks.Where(s => !s.IsEmpty).ToList();
        Ids = list.Select(s => (int)s.Id).ToArray();
        Counts = list.Select(s => s.Count).ToArray();
    }
}

/// <summary>
/// The crates of a Battle Royale match (#194), at <c>World/BrCrates</c> on the server and every
/// client (RPCs route by path): death boxes with a player's whole pack, supply crates by the
/// roads, military crates, airdrops. The server owns the list and what is in each crate, and hands
/// every stack out once to a living entrant standing at it; the crates last as long as the match.
/// Clients draw them (snapped to their own terrain) and open them in the loot panel
/// (<c>Loot.LootService.OpenCrate</c>). Docs: <c>docs/notes/br/loot.md</c>. A container for the
/// floating origin (#185): it stays at the identity and the shift moves its crates.
/// </summary>
public partial class BrCrates : Node3D, IOriginContainer
{
    public const string NodeName = "BrCrates";
    /// <summary>"No altitude: on the ground there".</summary>
    public const float Ground = -99999f;
    /// <summary>An airdrop falls this fast under its canopy, m/s, from <see cref="DropHeight"/>.</summary>
    public const float FallSpeed = 6f, DropHeight = 270f;
    private const float Reach = 2.4f;

    public static BrCrates? Instance { get; private set; }

    private bool _server;
    private readonly Dictionary<long, Crate> _crates = new();
    private readonly Dictionary<long, Node3D> _nodes = new();
    private readonly HashSet<long> _snapped = new();
    private long _next = 1;

    public WorldOrigin? Origin { get; set; }
    /// <summary>Server: may this peer loot (a living entrant of the running match)?</summary>
    public Func<long, bool> MayLoot { get; set; } = _ => false;
    /// <summary>Client: the terrain height under a world point, when this client has it.</summary>
    public Func<Vector3, float?> GroundAt { get; set; } = _ => null;

    public static BrCrates Create(Node world, WorldOrigin origin, bool server)
    {
        var c = new BrCrates { Name = NodeName, _server = server, Origin = origin };
        world.AddChild(c);
        if (!server) Instance = c;
        return c;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public IEnumerable<Crate> All => _crates.Values;

    // ------------------------------------------------------------------------------------
    // server
    // ------------------------------------------------------------------------------------

    /// <summary>A crate's yaw is NaN for "any way round": plain JSON has no NaN.</summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Server: puts crates into the match and tells everyone.</summary>
    public void Spawn(IEnumerable<Crate> crates)
    {
        var batch = new List<Crate>();
        foreach (var c in crates)
        {
            c.Id = _next++;
            _crates[c.Id] = c;
            batch.Add(c);
        }
        if (batch.Count > 0) Rpc(MethodName.AddMany, JsonSerializer.Serialize(batch, Json));
    }

    /// <summary>Server: every crate gone (the match is over).</summary>
    public void ClearAll()
    {
        _crates.Clear();
        Rpc(MethodName.Cleared);
    }

    /// <summary>Server: a joining peer gets the crates there are.</summary>
    public void SendTo(long peer)
    {
        if (_crates.Count > 0) RpcId(peer, MethodName.AddMany, JsonSerializer.Serialize(_crates.Values.ToList(), Json));
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestTake(long id, int index, int item, int count)
    {
        if (!_server) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (!_crates.TryGetValue(id, out var c) || !MayLoot(peer) || ClockSync.ServerNow < c.LandsAt
            || GetNodeOrNull<Node3D>("../Players/" + peer) is not { } body || !Near(c, body.GlobalPosition, Reach + 2.5f))
        {
            var at = GetNodeOrNull<Node3D>("../Players/" + peer)?.GlobalPosition;
            GD.Print($"[br crates] take from {peer} refused: crate {id} {(c == null ? "gone" : c.Label)}, may loot {MayLoot(peer)}, body at {at}");
            RpcId(peer, MethodName.Refused, id);
            return;
        }
        var stacks = c.Stacks();
        // the panel was out of date (someone else was quicker): it gets the crate as it is now
        if (index < 0 || index >= stacks.Count || (int)stacks[index].Id != item || stacks[index].Count != count)
        {
            RpcId(peer, MethodName.Refused, id);
            RpcId(peer, MethodName.Changed, id, c.Ids, c.Counts);
            return;
        }
        stacks.RemoveAt(index);
        c.SetStacks(stacks);
        // the new contents first, then the grant: the taker's "take all" goes on from the crate as it now is
        if (stacks.Count == 0)
        {
            _crates.Remove(id);
            Rpc(MethodName.Removed, id);
        }
        else Rpc(MethodName.Changed, id, c.Ids, c.Counts);
        RpcId(peer, MethodName.Granted, id, item, count);
    }

    /// <summary>The dial's numbers for a locked crate: the same on the server and every client, never sent.</summary>
    public static int[] Combination(long id, int seed) =>
        Loot.LootTables.Combination($"crate{id}", 0, seed, Interiors.FurnitureType.Safe);

    /// <summary>Server: the match seed, for the combinations.</summary>
    public int Seed { get; set; }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestUnlock(long id, int[] combo)
    {
        if (!_server) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (!_crates.TryGetValue(id, out var c) || !c.Locked || !MayLoot(peer)
            || GetNodeOrNull<Node3D>("../Players/" + peer) is not { } body || !Near(c, body.GlobalPosition, Reach + 2.5f))
            return;
        if (!combo.SequenceEqual(Combination(id, Seed)))
        {
            GD.Print($"[br] peer {peer} gave a wrong combination for crate {id}");
            RpcId(peer, MethodName.UnlockRefused, id);
            return;
        }
        c.Locked = false;
        GD.Print($"[br] crate {id} ({c.Style}) unlocked by peer {peer}");
        Rpc(MethodName.Unlocked, id, peer);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestBreak(long id)
    {
        if (!_server) return;
        long peer = Multiplayer.GetRemoteSenderId();
        // a supply crate within a rifle's reach of a living entrant
        if (!_crates.TryGetValue(id, out var c) || c.Style != CrateStyle.Supply || !MayLoot(peer)
            || GetNodeOrNull<Node3D>("../Players/" + peer) is not { } body || !Near(c, body.GlobalPosition, 320f))
            return;
        c.Style = CrateStyle.Pile;
        c.Label = "the broken crate";
        Rpc(MethodName.Broken, id);
    }

    /// <summary>Whether <paramref name="at"/> stands at the crate: horizontally, and in height when the crate has one.</summary>
    private bool Near(Crate c, Vector3 at, float reach)
    {
        if (Origin == null) return false;
        var (e, n) = Origin.ToLv95(at);
        if ((e - c.E) * (e - c.E) + (n - c.N) * (n - c.N) > reach * reach) return false;
        return c.Alt == Ground || Mathf.Abs(at.Y - c.Alt) < 4f;
    }

    // ------------------------------------------------------------------------------------
    // client: state
    // ------------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AddMany(string json)
    {
        if (_server) return;
        List<Crate>? list;
        try { list = JsonSerializer.Deserialize<List<Crate>>(json, Json); }
        catch (JsonException) { return; }
        foreach (var c in list ?? new())
        {
            _crates[c.Id] = c;
            Draw(c);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Changed(long id, int[] ids, int[] counts)
    {
        if (_server || !_crates.TryGetValue(id, out var c)) return;
        c.Ids = ids;
        c.Counts = counts;
        Loot.LootService.Instance?.CrateChanged(id);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Removed(long id)
    {
        if (_server) return;
        _crates.Remove(id);
        _snapped.Remove(id);
        if (_nodes.Remove(id, out var node)) node.QueueFree();
        Loot.LootService.Instance?.CrateChanged(id);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Cleared()
    {
        if (_server) return;
        foreach (var node in _nodes.Values) node.QueueFree();
        _nodes.Clear();
        _crates.Clear();
        _snapped.Clear();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Unlocked(long id, long by)
    {
        if (_server || !_crates.TryGetValue(id, out var c)) return;
        c.Locked = false;
        if (_nodes.TryGetValue(id, out var node))
        {
            Draw(c);
            Sound(node.GlobalPosition, Audio.SfxSynth.DoorOpenBank.Variants[0], 0.7f, 0f);
        }
        Loot.LootService.Instance?.CrateUnlocked(id, by == Multiplayer.GetUniqueId());
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void UnlockRefused(long id) => Loot.LootService.Instance?.CrateUnlockRefused(id);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Broken(long id)
    {
        if (_server || !_crates.TryGetValue(id, out var c)) return;
        c.Style = CrateStyle.Pile;
        c.Label = "the broken crate";
        if (!_nodes.TryGetValue(id, out var node)) return;
        var at = node.GlobalPosition;
        Draw(c);
        Splinters(at);
        Sound(at, Audio.SfxSynth.ImpactBank.Pick(Rng).Stream, 0.7f, 2f);
    }

    /// <summary>Asks the server to crack a crate's dial with these numbers.</summary>
    public void Unlock(long id, int[] combo) => RpcId(1, MethodName.RequestUnlock, id, combo);

    /// <summary>
    /// A shot or a stab from <paramref name="from"/> along <paramref name="dir"/>: the first supply
    /// crate within <paramref name="range"/> it passes through is broken open (asked of the server).
    /// True when one was hit.
    /// </summary>
    public bool TryBreak(Vector3 from, Vector3 dir, float range)
    {
        long best = 0;
        float bestT = range;
        foreach (var (id, node) in _nodes)
        {
            if (!_crates.TryGetValue(id, out var c) || c.Style != CrateStyle.Supply) continue;
            var centre = node.GlobalPosition + Vector3.Up * 0.25f;
            float t = (centre - from).Dot(dir);
            if (t < 0 || t > bestT || (from + dir * t).DistanceTo(centre) > 0.45f) continue;
            best = id;
            bestT = t;
        }
        if (best == 0) return false;
        RpcId(1, MethodName.RequestBreak, best);
        return true;
    }

    private static readonly Random Rng = new();

    private void Sound(Vector3 at, AudioStream stream, float pitch, float db)
    {
        var s = new AudioStreamPlayer3D { Stream = stream, PitchScale = pitch, VolumeDb = db, UnitSize = 10f, MaxDistance = 300f, Bus = Audio.SfxBus.Name, TopLevel = true };
        AddChild(s);
        s.GlobalPosition = at;
        s.Finished += s.QueueFree;
        s.Play();
    }

    /// <summary>Planks flying off a crate shot open.</summary>
    private void Splinters(Vector3 at)
    {
        var burst = new CpuParticles3D
        {
            Emitting = true, OneShot = true, Amount = 18, Lifetime = 1.4f, Explosiveness = 1f,
            Direction = Vector3.Up, Spread = 70f, InitialVelocityMin = 3f, InitialVelocityMax = 6f, Gravity = new Vector3(0, -9.8f, 0),
            AngularVelocityMin = -400f, AngularVelocityMax = 400f,
            Mesh = new BoxMesh { Size = new Vector3(0.35f, 0.03f, 0.08f), Material = new StandardMaterial3D { AlbedoColor = new Color(0.62f, 0.44f, 0.24f) } },
            TopLevel = true,
        };
        AddChild(burst);
        burst.GlobalPosition = at + Vector3.Up * 0.3f;
        GetTree().CreateTimer(2.0).Timeout += burst.QueueFree;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Granted(long id, int item, int count) =>
        Loot.LootService.Instance?.CrateGranted(id, new ItemStack((ItemId)item, count));

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Refused(long id) => Loot.LootService.Instance?.CrateRefused(id);

    // ------------------------------------------------------------------------------------
    // client: using them
    // ------------------------------------------------------------------------------------

    public List<ItemStack> StacksOf(long id) => _crates.TryGetValue(id, out var c) ? c.Stacks() : new();

    /// <summary>The crate is still there and <paramref name="at"/> is within reach of it (plus <paramref name="slack"/>).</summary>
    public bool InReach(long id, Vector3 at, float slack = 0f) =>
        _nodes.TryGetValue(id, out var node) && IsInstanceValid(node) && Flat(node.GlobalPosition - at) <= Reach + slack
        && Mathf.Abs(node.GlobalPosition.Y - at.Y) < 3f;

    public bool Landed(Crate c) => ClockSync.ServerNow >= c.LandsAt;

    /// <summary>The landed crate this player stands at, nearest first.</summary>
    public Crate? NearestTo(FootPlayer p)
    {
        Crate? best = null;
        float bestD = Reach;
        foreach (var (id, node) in _nodes)
        {
            if (!_crates.TryGetValue(id, out var c) || !Landed(c)) continue;
            float d = Flat(node.GlobalPosition - p.GlobalPosition);
            if (d <= bestD && Mathf.Abs(node.GlobalPosition.Y - p.GlobalPosition.Y) < 3f) { bestD = d; best = c; }
        }
        return best;
    }

    /// <summary>E at a crate: opens it in the loot panel. False when there is none in reach.</summary>
    public bool TryOpen(FootPlayer p)
    {
        if (Loot.LootService.Instance is not { } loot) return false;
        // E again shuts a crate's panel or dial (only a crate's: a house's searches have E of their own)
        if (loot.CrateOpen)
        {
            loot.Close();
            loot.StopPicking();
            return true;
        }
        if (NearestTo(p) is not { } c) return false;
        if (c.Locked) loot.PickCrate(p, c.Id, c.Label, Combination(c.Id, BrManager.Instance?.State.Seed ?? 0));
        else loot.OpenCrate(p, c.Id, c.Label);
        return true;
    }

    public string? PromptFor(FootPlayer p) =>
        NearestTo(p) is { } c ? InputHints.Prompt(PlayerInput.InteractMount, c.Locked ? $"Crack {c.Label}" : $"Search {c.Label}") : null;

    /// <summary>Asks the server for one stack of a crate.</summary>
    public void Take(long id, int index, ItemStack stack) =>
        RpcId(1, MethodName.RequestTake, id, index, (int)stack.Id, stack.Count);

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    // ------------------------------------------------------------------------------------
    // client: drawing them
    // ------------------------------------------------------------------------------------

    private void Draw(Crate c)
    {
        if (_nodes.Remove(c.Id, out var old)) old.QueueFree();
        var node = new Node3D { Name = $"C{c.Id}" };
        node.AddChild(new MeshInstance3D { Mesh = BrSiteMeshes.For(c.Style, c.Locked) ?? MeshOf(c.Style), MaterialOverride = ItemDefs.Material });
        if (c.Style == CrateStyle.Airdrop)
        {
            node.AddChild(new MeshInstance3D { Name = "Canopy", Mesh = Canopy(), MaterialOverride = ItemDefs.Material, Position = Vector3.Up * 6f });
            node.AddChild(Smoke(new Color(0.95f, 0.35f, 0.25f, 0.55f)));
            node.AddChild(Beacon());
            if (!Landed(c)) _falling.Add(c.Id);
        }
        // the wreck still smokes: you see it from far off, and find it by it
        if (c.Style == CrateStyle.Wreck) node.AddChild(Smoke(new Color(0.25f, 0.25f, 0.27f, 0.6f)));
        AddChild(node);
        _nodes[c.Id] = node;
        Place(c, node);
    }

    private double _tick;

    public override void _Process(double delta)
    {
        if (_server) return;
        _tick += delta;
        bool refresh = _tick > 0.4;
        if (refresh) _tick = 0;
        foreach (var (id, node) in _nodes)
        {
            if (!_crates.TryGetValue(id, out var c)) continue;
            bool falling = !Landed(c);
            if (falling || (refresh && !_snapped.Contains(id))) Place(c, node);
            if (node.GetNodeOrNull<Node3D>("Canopy") is { } canopy) canopy.Visible = falling;
            if (node.GetNodeOrNull<Node3D>("Beacon") is { } beacon)
            {
                // the beam once it is down, and a slow pulse on its light
                beacon.Visible = !falling;
                if (!falling && beacon.GetNodeOrNull<OmniLight3D>("Light") is { } light)
                    light.LightEnergy = 2.5f + 1.5f * Mathf.Sin((float)Time.GetTicksMsec() / 260f);
            }
            if (!falling && _falling.Remove(id))
            {
                // down: a heavy thud
                var (thud, pitch, db) = Audio.SfxSynth.ImpactBank.Pick(Rng);
                Sound(node.GlobalPosition, thud, pitch * 0.5f, db + 8f);
            }
        }
    }

    /// <summary>Airdrops still coming down here: their landing is heard (#231).</summary>
    private readonly HashSet<long> _falling = new();

    private static CylinderMesh? _beam;

    /// <summary>
    /// A landed supply drop's beacon (#231): a column of blue light 160 m tall, seen from far off, and a
    /// pulsing light on the crate. Blue like its mark on the map.
    /// </summary>
    private static Node3D Beacon()
    {
        var beacon = new Node3D { Name = "Beacon", Visible = false };
        _beam ??= new CylinderMesh
        {
            TopRadius = 2.0f, BottomRadius = 3.0f, Height = 160f, RadialSegments = 8, Rings = 1, CapTop = false, CapBottom = false,
            // mixed, not added: added light vanishes against a bright sky
            Material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.25f, 0.5f, 1f, 0.45f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                DisableFog = true,
            },
        };
        beacon.AddChild(new MeshInstance3D { Name = "Beam", Mesh = _beam, Position = Vector3.Up * 80f, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        beacon.AddChild(new OmniLight3D { Name = "Light", LightColor = new Color(0.35f, 0.6f, 1f), OmniRange = 14f, LightEnergy = 2.5f, Position = Vector3.Up * 1.6f });
        return beacon;
    }

    /// <summary>On this client's ground (once it has it), and up in the air while an airdrop falls.</summary>
    private void Place(Crate c, Node3D node)
    {
        if (Origin == null) return;
        var at = Origin.ToWorld(c.E, c.N, c.Alt == Ground ? 0 : c.Alt);
        if (c.Alt == Ground || c.Style != CrateStyle.DeathBox)
        {
            if (GroundAt(at) is float h)
            {
                at.Y = h;
                _snapped.Add(c.Id);
            }
            else if (c.Alt == Ground) at.Y = -5000f;   // not here yet: out of sight until the terrain is
        }
        else _snapped.Add(c.Id);
        double left = c.LandsAt - ClockSync.ServerNow;
        if (left > 0) at.Y += Mathf.Min(DropHeight, (float)left * FallSpeed);
        node.GlobalPosition = at;
        node.Rotation = new Vector3(0, float.IsNaN(c.Yaw) ? (c.Id * 0.7f) % Mathf.Tau : c.Yaw, 0);
    }

    private static readonly Dictionary<CrateStyle, ArrayMesh> Meshes = new();

    private static ArrayMesh MeshOf(CrateStyle style)
    {
        if (Meshes.TryGetValue(style, out var m)) return m;
        var s = new MeshScratch();
        switch (style)
        {
            case CrateStyle.DeathBox:
            {
                s.Box(new Vector3(0, 0.25f, 0), new Vector3(0.8f, 0.5f, 0.5f), new Color(0.22f, 0.23f, 0.26f));
                s.Box(new Vector3(0, 0.52f, 0), new Vector3(0.84f, 0.06f, 0.54f), new Color(0.85f, 0.15f, 0.12f));
                break;
            }
            case CrateStyle.Supply:
            {
                var wood = new Color(0.62f, 0.44f, 0.24f);
                s.Box(new Vector3(0, 0.25f, 0), new Vector3(0.7f, 0.5f, 0.5f), wood);
                foreach (float y in new[] { 0.12f, 0.38f })
                    s.Box(new Vector3(0, y, 0), new Vector3(0.72f, 0.05f, 0.52f), new Color(0.45f, 0.30f, 0.16f));
                break;
            }
            case CrateStyle.Military:
            {
                var olive = new Color(0.30f, 0.36f, 0.22f);
                s.Box(new Vector3(0, 0.22f, 0), new Vector3(1.1f, 0.44f, 0.5f), olive);
                s.Box(new Vector3(0, 0.45f, 0), new Vector3(1.12f, 0.04f, 0.52f), new Color(0.22f, 0.27f, 0.16f));
                // the white cross on red, on the lid
                s.Box(new Vector3(0, 0.475f, 0), new Vector3(0.22f, 0.01f, 0.22f), new Color(0.85f, 0.10f, 0.10f));
                s.Box(new Vector3(0, 0.48f, 0), new Vector3(0.14f, 0.01f, 0.04f), Colors.White);
                s.Box(new Vector3(0, 0.48f, 0), new Vector3(0.04f, 0.01f, 0.14f), Colors.White);
                break;
            }
            default:
            {
                var blue = new Color(0.18f, 0.40f, 0.78f);
                s.Box(new Vector3(0, 0.6f, 0), new Vector3(1.2f, 1.2f, 1.2f), blue);
                foreach (float y in new[] { 0.15f, 1.05f })
                    s.Box(new Vector3(0, y, 0), new Vector3(1.24f, 0.1f, 1.24f), new Color(0.95f, 0.75f, 0.15f));
                break;
            }
        }
        return Meshes[style] = s.Build();
    }

    private static ArrayMesh? _canopy;

    /// <summary>A striped canopy on its lines, over the airdrop while it falls.</summary>
    private static ArrayMesh Canopy()
    {
        if (_canopy != null) return _canopy;
        var s = new MeshScratch();
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.Tau / 8f;
            var rim = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 3.2f;
            s.Tube(rim, Vector3.Up * 1.4f, 0.9f, 0.2f, i % 2 == 0 ? new Color(0.9f, 0.2f, 0.15f) : Colors.White, 4);
            s.Tube(rim, Vector3.Down * 5f, 0.02f, Colors.Black, 3);
        }
        return _canopy = s.Build();
    }

    /// <summary>A column of coloured smoke you can see from far off.</summary>
    private static Node3D Smoke(Color colour) => new CpuParticles3D
    {
        Name = "Smoke",
        Amount = 60,
        Lifetime = 9f,
        Direction = Vector3.Up,
        Spread = 8f,
        Gravity = new Vector3(0.6f, 2.2f, 0),
        InitialVelocityMin = 2f,
        InitialVelocityMax = 4f,
        ScaleAmountMin = 2f,
        ScaleAmountMax = 4.5f,
        Color = colour,
        Mesh = new QuadMesh
        {
            Size = Vector2.One,
            Material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            },
        },
        Position = Vector3.Up * 1.2f,
        VisibilityAabb = new Aabb(new Vector3(-40, -5, -40), new Vector3(80, 120, 80)),
    };
}
