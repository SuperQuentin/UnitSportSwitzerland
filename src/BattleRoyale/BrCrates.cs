using System.Text.Json;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.BattleRoyale;

/// <summary>What a crate is, for its look and its name. Sent as an int: append only.</summary>
public enum CrateStyle { DeathBox = 0, Supply = 1, Military = 2, Airdrop = 3 }

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
/// (<c>Loot.LootService.OpenCrate</c>). Docs: <c>docs/notes/br/loot.md</c>.
/// </summary>
public partial class BrCrates : Node3D
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

    private static readonly JsonSerializerOptions Json = new();

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
        if (NearestTo(p) is not { } c || Loot.LootService.Instance is not { } loot) return false;
        loot.OpenCrate(p, c.Id, c.Label);
        return true;
    }

    public string? PromptFor(FootPlayer p) =>
        NearestTo(p) is { } c ? InputHints.Prompt(PlayerInput.InteractMount, $"Search {c.Label}") : null;

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
        node.AddChild(new MeshInstance3D { Mesh = MeshOf(c.Style), MaterialOverride = ItemDefs.Material });
        if (c.Style == CrateStyle.Airdrop)
        {
            node.AddChild(new MeshInstance3D { Name = "Canopy", Mesh = Canopy(), MaterialOverride = ItemDefs.Material, Position = Vector3.Up * 6f });
            node.AddChild(Smoke());
        }
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
        }
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
        node.Rotation = new Vector3(0, (c.Id * 0.7f) % Mathf.Tau, 0);
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
    private static Node3D Smoke() => new CpuParticles3D
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
        Color = new Color(0.95f, 0.35f, 0.25f, 0.55f),
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
