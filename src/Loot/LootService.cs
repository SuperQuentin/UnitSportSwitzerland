using System.Text.Json;
using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Loot;

/// <summary>
/// Scavenging, at <c>World/Loot</c> on the server and on every client. RPCs route by node path,
/// so the name matches on both sides, as for <c>World/Interiors</c>.
///
/// <para>
/// <b>Contents are computed, never stored.</b> What a piece of furniture holds is a roll seeded by
/// building, furniture index and restock epoch (<see cref="LootTables.ContentsOf"/>), so the
/// server remembers only what has been <i>taken</i>: a bitmask over the rolled stacks per
/// container, saved to <c>user://loot/E_N.json</c>. A mask from an older epoch means the
/// container has restocked. A house nobody has touched costs nothing at all.
/// </para>
///
/// <para>
/// <b>The server decides.</b> A take is only granted if the stack is still there and the player is
/// inside that building, so two players cannot both empty one fridge. Offline this client plays
/// the server's part, through the same methods.
/// </para>
/// </summary>
public partial class LootService : Node
{
    public const string NodeName = "Loot";

    /// <summary>How far from a piece of furniture's edge the player can search it.</summary>
    private const float SearchReach = 1.3f;
    /// <summary>Walking this far away closes the panel.</summary>
    private const float CloseReach = 2.6f;

    public static LootService? Instance { get; private set; }

    /// <summary>Client: where granted items go, and where the toasts appear.</summary>
    public ItemController? Items { get; set; }

    private string _storeDir = "";
    private readonly Dictionary<string, TileState> _tiles = new();

    // client state
    private LootUi? _ui;
    private (string Key, int Furniture)? _open;
    private long _openEpoch;
    private List<ItemStack> _openStacks = new();
    private int _openMask;
    private bool _waiting;
    private FootPlayer? _searcher;
    /// <summary>Containers seen empty this session: the prompt says so before you search again.</summary>
    private readonly Dictionary<(string, int), long> _seenEmpty = new();

    public static LootService Create(Node world)
    {
        var s = new LootService { Name = NodeName };
        world.AddChild(s);
        Instance = s;
        return s;
    }

    public override void _Ready()
    {
        _storeDir = ProjectSettings.GlobalizePath("user://loot");
        // "--lootepoch N" pretends N restock periods have passed, to test a refill
        var args = OS.GetCmdlineUserArgs();
        int at = Array.IndexOf(args, "--lootepoch");
        if (at >= 0 && at + 1 < args.Length && long.TryParse(args[at + 1].TrimStart('+'), out long offset))
            LootTables.EpochOffset = offset;

        if (DisplayServer.GetName() == "headless" && Multiplayer.IsServer() && Online) return;
        _ui = new LootUi(this) { Name = "LootUi" };
        AddChild(_ui);
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => Multiplayer.MultiplayerPeer is { } peer and not OfflineMultiplayerPeer
        && peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ---- client: finding something to search ---------------------------------------------------

    /// <summary>The lootable piece of furniture the player is facing, if any, as an index into the layout.</summary>
    public static int NearestContainer(FootPlayer p, InteriorLayout layout, InteriorNode node)
    {
        var local = node.ToLocal(p.GlobalPosition);
        var look = node.GlobalTransform.Basis.Inverse() * -p.Camera.GlobalTransform.Basis.Z;
        var lookFlat = new Vector2(look.X, look.Z);
        if (lookFlat.LengthSquared() > 1e-4f) lookFlat = lookFlat.Normalized();

        int best = -1;
        float bestScore = float.MaxValue;
        for (int i = 0; i < layout.Furniture.Count; i++)
        {
            var f = layout.Furniture[i];
            if (!LootTables.IsLootable(f.Type)) continue;
            float floorY = f.Floor * layout.StoreyHeight;
            if (local.Y < floorY - 0.5f || local.Y > floorY + layout.StoreyHeight - 0.5f) continue;

            // distance to the piece's footprint, in its own turned frame
            float a = -f.Turns * Mathf.Pi / 2;
            var rel = new Vector2(local.X - f.X, local.Z - f.Z);
            var r = new Vector2(rel.X * Mathf.Cos(a) + rel.Y * Mathf.Sin(a), -rel.X * Mathf.Sin(a) + rel.Y * Mathf.Cos(a));
            float dx = Mathf.Max(0, Mathf.Abs(r.X) - f.W / 2), dz = Mathf.Max(0, Mathf.Abs(r.Y) - f.D / 2);
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            if (dist > SearchReach) continue;

            // prefer what is in front of the player; behind them only if nothing else is near
            var to = -rel;
            float facing = to.LengthSquared() > 1e-4f ? lookFlat.Dot(to.Normalized()) : 1f;
            if (facing < -0.2f) continue;
            float score = dist - facing * 0.8f;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    /// <summary>The prompt line for the furniture the player faces, or null.</summary>
    public string? PromptFor(FootPlayer p)
    {
        if (_ui?.IsOpen == true) return null;
        if (InteriorManager.Instance is not { Current: { } layout, CurrentNode: { } node }) return null;
        int i = NearestContainer(p, layout, node);
        if (i < 0) return null;
        string what = LootTables.Describe(layout.Furniture[i].Type);
        string key = PlayerInput.LastDevice == InputDevice.Gamepad ? "[Y]" : "[E]";
        bool empty = _seenEmpty.TryGetValue((layout.Key, i), out long ep)
            && ep == LootTables.Epoch(layout.Key, Now);
        return empty ? $"{key} Search the {what} (empty)" : $"{key} Search the {what}";
    }

    /// <summary>E indoors. Toggles the panel on the facing container; false when there is nothing to search.</summary>
    public bool TrySearch(FootPlayer p)
    {
        if (_ui?.IsOpen == true) { Close(); return true; }
        if (InteriorManager.Instance is not { Current: { } layout, CurrentNode: { } node }) return false;
        int i = NearestContainer(p, layout, node);
        if (i < 0) return false;

        _open = (layout.Key, i);
        _searcher = p;
        _openStacks = new();
        _openMask = 0;
        _waiting = true;
        _ui?.Open(LootTables.Describe(layout.Furniture[i].Type));
        Play(SfxSynth.Tick, 0.6f);
        if (Online) RpcId(1, MethodName.RequestContents, layout.Key, i);
        else ServeContents(1, layout.Key, i);
        return true;
    }

    public void Close()
    {
        _open = null;
        _searcher = null;
        _waiting = false;
        _pendingAll = false;
        _ui?.Close();
    }

    public override void _Process(double delta)
    {
        if (_open == null) return;
        var p = _searcher;
        var interior = InteriorManager.Instance;
        if (p == null || !IsInstanceValid(p) || !p.IsViewing || interior?.Current?.Key != _open.Value.Key || interior.CurrentNode == null)
        {
            Close();
            return;
        }
        var f = interior.Current.Furniture[_open.Value.Furniture];
        var local = interior.CurrentNode.ToLocal(p.GlobalPosition);
        if (new Vector2(local.X - f.X, local.Z - f.Z).Length() > CloseReach + Mathf.Max(f.W, f.D) / 2) Close();
    }

    /// <summary>The stacks still in the open container, with their index in the roll.</summary>
    public IEnumerable<(int Index, ItemStack Stack)> OpenContents()
    {
        for (int i = 0; i < _openStacks.Count; i++)
            if ((_openMask & (1 << i)) == 0) yield return (i, _openStacks[i]);
    }

    public bool Waiting => _waiting;
    public bool IsOpen => _open != null;

    /// <summary>Asks for one stack of the open container. Refused locally when it would not fit.</summary>
    public void Take(int index)
    {
        if (_open is not { } open || _waiting || index < 0 || index >= _openStacks.Count) return;
        if ((_openMask & (1 << index)) != 0 || Items == null) return;
        var stack = _openStacks[index];
        if (Items.Inventory.Room(stack.Id) < stack.Count)
        {
            Items.Ui.Toast("No room in your pack.");
            return;
        }
        _waiting = true;
        if (Online) RpcId(1, MethodName.RequestTake, open.Key, open.Furniture, _openEpoch, index);
        else ServeTake(1, open.Key, open.Furniture, _openEpoch, index);
    }

    public void TakeAll()
    {
        foreach (var (i, _) in OpenContents().ToList())
        {
            if (_waiting && Online) { _pendingAll = true; return; }
            Take(i);
        }
    }

    private bool _pendingAll;

    // ---- client: replies ------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Contents(string key, int furniture, long epoch, int[] ids, int[] counts, int mask)
    {
        if (_open is not { } open || open.Key != key || open.Furniture != furniture) return;
        _waiting = false;
        _openEpoch = epoch;
        _openStacks = ids.Zip(counts, (id, c) => new ItemStack((ItemId)id, c)).ToList();
        _openMask = mask;
        if (!OpenContents().Any()) _seenEmpty[(key, furniture)] = epoch;
        _ui?.Refresh();
        if (_pendingAll) { _pendingAll = false; TakeAll(); }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Granted(string key, int furniture, int index, int id, int count)
    {
        _waiting = false;
        if (Items != null)
        {
            int left = Items.Inventory.Add((ItemId)id, count);
            var def = ItemDefs.Get((ItemId)id);
            Items.Ui.Toast(id == (int)ItemId.Francs ? $"+{count - left} CHF" : $"+{count - left} {def?.Name}");
            if (left > 0) GD.PushWarning($"[loot] {left} {(ItemId)id} did not fit and were lost");
        }
        Play(SfxSynth.Chime, 1.5f);
        if (_open is { } open && open.Key == key && open.Furniture == furniture)
        {
            _openMask |= 1 << index;
            if (!OpenContents().Any()) _seenEmpty[(key, furniture)] = _openEpoch;
            _ui?.Refresh();
            if (_pendingAll) { _pendingAll = false; TakeAll(); }
        }
    }

    private void Play(AudioStreamWav? stream, float pitch)
    {
        if (stream == null || Items == null) return;
        var player = new AudioStreamPlayer { Stream = stream, PitchScale = pitch, VolumeDb = -8, Bus = SfxBus.Name };
        AddChild(player);
        player.Finished += player.QueueFree;
        player.Play();
    }

    // ---- server ---------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestContents(string key, int furniture) =>
        ServeContents(Multiplayer.GetRemoteSenderId(), key, furniture);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestTake(string key, int furniture, long epoch, int index) =>
        ServeTake(Multiplayer.GetRemoteSenderId(), key, furniture, epoch, index);

    private async void ServeContents(long peer, string key, int furniture)
    {
        var layout = await LayoutFor(peer, key);
        if (layout == null || furniture < 0 || furniture >= layout.Furniture.Count) return;
        long epoch = LootTables.Epoch(key, Now);
        var stacks = LootTables.ContentsOf(layout, furniture, epoch);
        int mask = MaskOf(key, furniture, epoch);
        Reply(peer, MethodName.Contents, key, furniture, epoch,
            stacks.Select(s => (int)s.Id).ToArray(), stacks.Select(s => s.Count).ToArray(), mask);
    }

    private async void ServeTake(long peer, string key, int furniture, long epoch, int index)
    {
        var layout = await LayoutFor(peer, key);
        if (layout == null || furniture < 0 || furniture >= layout.Furniture.Count) return;
        long now = LootTables.Epoch(key, Now);
        var stacks = LootTables.ContentsOf(layout, furniture, now);
        int mask = MaskOf(key, furniture, now);
        // restocked since the panel opened, or someone else was quicker: send what is there now
        if (epoch != now || index < 0 || index >= stacks.Count || (mask & (1 << index)) != 0)
        {
            Reply(peer, MethodName.Contents, key, furniture, now,
                stacks.Select(s => (int)s.Id).ToArray(), stacks.Select(s => s.Count).ToArray(), mask);
            return;
        }
        SetMask(key, furniture, now, mask | (1 << index));
        Reply(peer, MethodName.Granted, key, furniture, index, (int)stacks[index].Id, stacks[index].Count);
    }

    private void Reply(long peer, StringName method, params Variant[] args)
    {
        if (Online) RpcId(peer, method, args);
        else Call(method, args);
    }

    /// <summary>The layout, if the peer is inside that very building. Offline there is no check to make.</summary>
    private async Task<InteriorLayout?> LayoutFor(long peer, string key)
    {
        var interiors = InteriorManager.Instance;
        if (interiors == null) return null;
        if (Online && interiors.SpaceOf(peer) != key) return null;
        try { return await interiors.GetOrCreate(key); }
        catch (Exception e)
        {
            GD.PushError($"[loot] layout {key}: {e.Message}");
            return null;
        }
    }

    // ---- server: what has been taken -------------------------------------------------------------

    /// <summary>Per tile: building index → furniture index → (epoch, taken mask).</summary>
    private sealed class TileState
    {
        public Dictionary<string, Dictionary<string, long[]>> Buildings { get; set; } = new();
    }

    private int MaskOf(string key, int furniture, long epoch)
    {
        if (!BuildingKey.TryParse(key, out var k)) return 0;
        var tile = TileFor(k);
        return tile.Buildings.TryGetValue(k.Index.ToString(), out var b)
            && b.TryGetValue(furniture.ToString(), out var e) && e.Length == 2 && e[0] == epoch
            ? (int)e[1] : 0;
    }

    private void SetMask(string key, int furniture, long epoch, int mask)
    {
        if (!BuildingKey.TryParse(key, out var k)) return;
        var tile = TileFor(k);
        if (!tile.Buildings.TryGetValue(k.Index.ToString(), out var b))
            tile.Buildings[k.Index.ToString()] = b = new();
        b[furniture.ToString()] = new[] { epoch, mask };
        Save(k, tile);
    }

    private string PathFor(BuildingKey k) => Path.Combine(_storeDir, $"{k.TileE}_{k.TileN}.json");

    private TileState TileFor(BuildingKey k)
    {
        string name = $"{k.TileE}_{k.TileN}";
        if (_tiles.TryGetValue(name, out var t)) return t;
        t = new TileState();
        try
        {
            string path = PathFor(k);
            if (File.Exists(path))
                t = JsonSerializer.Deserialize<TileState>(File.ReadAllText(path)) ?? t;
        }
        catch (Exception e) { GD.PushWarning($"[loot] {name}: {e.Message}"); }
        return _tiles[name] = t;
    }

    private void Save(BuildingKey k, TileState t)
    {
        try
        {
            Directory.CreateDirectory(_storeDir);
            string path = PathFor(k), tmp = path + ".part";
            File.WriteAllText(tmp, JsonSerializer.Serialize(t));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) { GD.PushError($"[loot] saving: {e.Message}"); }
    }
}
