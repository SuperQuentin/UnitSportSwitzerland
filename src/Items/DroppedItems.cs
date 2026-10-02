using Godot;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// Every item stack lying in the world, at <c>World/Dropped</c> on the server and on every client
/// (RPCs and the spawner route by node path), spawned through <c>World/DroppedSpawner</c>. The
/// <see cref="RadioManager"/> pattern: offline it adds and frees <see cref="DroppedItem"/> nodes;
/// online a client asks and the server decides — a drop spawns it for everyone with the dropper as
/// the authority over its fall, a pick-up removes it for everyone and only one player wins it.
/// Not saved: like radios, what lies about is gone with the server.
/// </summary>
public partial class DroppedItems : Node3D, Core.IOriginContainer
{
    public const string NodeName = "Dropped";

    /// <summary>How close to a dropped item to pick it up.</summary>
    public const float Reach = 2.6f;

    /// <summary>Most items lying about at once; the oldest goes first.</summary>
    public const int MaxItems = 400;

    private const float LonelyDistance = 3000f;
    private const double LonelyTime = 900;

    public static DroppedItems? Instance { get; private set; }

    /// <summary>Client: the terrain's height under a point, for putting back what fell through it; null where unknown.</summary>
    public static Func<Vector3, float?>? GroundHeight { get; set; }

    /// <summary>Where the players are, for despawning what nobody is near.</summary>
    public Func<IEnumerable<Vector3>>? PlayerPositions { get; set; }

    /// <summary>Client: the server refused something, with a line for the player and the stack to give back (or empty).</summary>
    public static event Action<string, ItemStack>? Refused;
    internal static void ResetEvents()
    {
        Refused = null;
        GroundHeight = null;
    }

    private MultiplayerSpawner? _spawner;
    private int _counter;
    private readonly Dictionary<string, Action<ItemStack>> _pendingPickUps = new();
    /// <summary>Client: drops sent and not yet spawned, by token: what to give back if refused, and the stand-in flying meanwhile.</summary>
    private readonly Dictionary<int, (ItemStack Stack, DroppedItem Proxy)> _pendingDrops = new();
    private int _token;
    private readonly HashSet<string> _claimed = new();
    private double _housekeeping;

    public static DroppedItems Create(Node world)
    {
        var manager = new DroppedItems { Name = NodeName };
        world.AddChild(manager);
        manager._spawner = new MultiplayerSpawner
        {
            Name = "DroppedSpawner",
            SpawnPath = new NodePath("../" + NodeName),
            SpawnFunction = Callable.From((Variant data) => (Node)DroppedItem.Create(DropState.FromDict(data.AsGodotDictionary()))),
        };
        world.AddChild(manager._spawner);
        manager._spawner.Spawned += manager.OnSpawned;
        Instance = manager;
        return manager;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => NetLink.Online(this);

    // ---- client API ----------------------------------------------------------------------------

    /// <summary>
    /// Drops <paramref name="stack"/> a little ahead of the local player, offline or online, its
    /// <see cref="ItemStack.Data"/> kept (<see cref="ItemController.DropStack"/>). False when there is
    /// no player or no world to drop into: the caller still holds the stack then.
    /// </summary>
    public static bool Drop(ItemStack stack) => ItemController.Instance?.DropStack(null, stack) == true;

    /// <summary>Puts a stack into the world at <paramref name="at"/>, moving at <paramref name="velocity"/>.</summary>
    public void Drop(ItemStack stack, Vector3 at, Vector3 velocity, Vector3 rotation, Vector3 spin)
    {
        if (stack.IsEmpty) return;
        var state = new DropState("", 0, stack, at, rotation, velocity, spin);
        if (!Online)
        {
            AddChild(DroppedItem.Create(state));
            TrimOffline();
            return;
        }
        // it flies here now; the server's spawn takes over from wherever it has got to
        state = state with { Token = ++_token };
        var proxy = DroppedItem.Create(state with { Name = $"proxy_{_token}" }, proxy: true);
        AddChild(proxy);
        _pendingDrops[_token] = (stack, proxy);
        RpcId(1, MethodName.RequestDrop, state.ToDict());
    }

    /// <summary>Asks to take an item. <paramref name="granted"/> runs with its stack if nobody was quicker.</summary>
    public void PickUp(DroppedItem item, Action<ItemStack> granted)
    {
        if (_claimed.Contains(item.Name)) return;
        if (!Online)
        {
            var stack = item.Stack;
            item.QueueFree();
            granted(stack);
            return;
        }
        _claimed.Add(item.Name);   // hidden from pointing here at once; the server decides
        _pendingPickUps[item.Name] = granted;
        RpcId(1, MethodName.RequestPickUp, item.Name);
    }

    /// <summary>Not to be pointed at: a pick-up is in flight for it, or it is only this player's stand-in.</summary>
    public bool IsClaimed(DroppedItem item) => item.Proxy || _claimed.Contains(item.Name);

    /// <summary>Client: a spawn of this player's own drop replaces its stand-in.</summary>
    private void OnSpawned(Node node)
    {
        if (node is DroppedItem item && item.Token != 0 && item.Owner == Multiplayer.GetUniqueId()
            && _pendingDrops.Remove(item.Token, out var pending))
        {
            if (IsInstanceValid(pending.Proxy)) item.TakeOver(pending.Proxy);
        }
    }

    public IEnumerable<DroppedItem> Items => GetChildren().OfType<DroppedItem>();

    // ---- server side ---------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestDrop(Godot.Collections.Dictionary data)
    {
        if (!Multiplayer.IsServer() || _spawner == null) return;
        long sender = Multiplayer.GetRemoteSenderId();
        DropState dropped;
        try { dropped = DropState.FromDict(data); }
        catch (Exception) { return; }
        var def = ItemDefs.Get(dropped.Stack.Id);
        if (def == null || dropped.Stack.Count < 1 || dropped.Stack.Count > def.MaxStack
            || dropped.Stack.Data is { Length: > 512 } || dropped.Stack.Id == ItemId.Francs
            || dropped.Velocity.Length() > 40f || !dropped.Position.IsFinite() || !dropped.Spin.IsFinite())
        {
            GD.Print($"[drop] refused from {sender}: {dropped.Stack.Id} x{dropped.Stack.Count}, {dropped.Velocity.Length():F1} m/s");
            RpcId(sender, MethodName.DropRefused, dropped.Token);
            return;
        }
        // the oldest goes when the world is full of litter
        var all = Items.Where(i => !i.Proxy).ToList();
        for (int i = 0; i <= all.Count - MaxItems; i++) all[i].QueueFree();
        var state = dropped with { Owner = sender, Name = $"drop_{sender}_{++_counter}", Settled = false };
        _spawner.Spawn(state.ToDict());
        GD.Print($"[drop] {state.Name}: {dropped.Stack.Id} x{dropped.Stack.Count}");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPickUp(string name)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (GetNodeOrNull<DroppedItem>(name) is not { } item || !_claimed.Add(name))
        {
            RpcId(sender, MethodName.PickUpRefused, name);
            return;
        }
        item.QueueFree();   // the spawner removes it on every client
        _claimed.Remove(name);
        RpcId(sender, MethodName.PickUpGranted, name, (int)item.Stack.Id, item.Stack.Count, item.Stack.Data ?? "");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PickUpGranted(string name, int id, int count, string data)
    {
        _claimed.Remove(name);
        if (_pendingPickUps.Remove(name, out var granted))
            granted(new ItemStack((ItemId)id, count, data.Length == 0 ? null : data));
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PickUpRefused(string name)
    {
        _claimed.Remove(name);
        // it flew to the hand here already: back where it lies
        if (GetNodeOrNull<DroppedItem>(name)?.Visual is { } visual) visual.Visible = true;
        _pendingPickUps.Remove(name);
        Refused?.Invoke("Someone else picked that up first.", ItemStack.Empty);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DropRefused(int token)
    {
        var back = ItemStack.Empty;
        if (_pendingDrops.Remove(token, out var pending))
        {
            back = pending.Stack;
            if (IsInstanceValid(pending.Proxy)) pending.Proxy.QueueFree();
        }
        Refused?.Invoke("The server refused that; it is back in your pack.", back);
    }

    /// <summary>
    /// Server: a player left, and nobody simulates what they dropped any more. It is put back where it
    /// was last seen, at rest, with the server as its authority.
    /// </summary>
    public void ForgetOwner(long peer)
    {
        if (_spawner == null) return;
        var respawn = new List<DropState>();
        foreach (var item in Items)
            if (item.Owner == peer)
            {
                respawn.Add(item.Capture() with { Owner = 0, Name = $"drop_srv_{++_counter}", Settled = true });
                item.QueueFree();
            }
        foreach (var state in respawn) _spawner.Spawn(state.ToDict());
    }

    private void TrimOffline()
    {
        var all = Items.ToList();
        for (int i = 0; i < all.Count - MaxItems; i++) all[i].QueueFree();
    }

    /// <summary>Clears items nobody has been near for a long time; done by whoever owns the list.</summary>
    public override void _Process(double delta)
    {
        DropFloat.Step(GetViewport().GetCamera3D()?.GlobalPosition, (float)(Time.GetTicksMsec() / 1000.0 % 3600.0));
        if (Online && !Multiplayer.IsServer()) return;
        _housekeeping += delta;
        if (_housekeeping < 2) return;
        double step = _housekeeping;
        _housekeeping = 0;
        var players = PlayerPositions?.Invoke().ToList() ?? new List<Vector3>();
        foreach (var item in Items)
        {
            bool near = players.Count == 0 || players.Any(p => p.DistanceTo(item.GlobalPosition) < LonelyDistance);
            item.LonelyFor = near ? 0 : item.LonelyFor + step;
            if (item.LonelyFor > LonelyTime) item.QueueFree();
        }
    }
}
