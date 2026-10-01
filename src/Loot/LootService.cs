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

    /// <summary>
    /// Bit of the saved take mask that says a locked container (gun locker, safe) has been cracked
    /// this restock period. Stacks use the low bits, so a cracked container's mask still works as a
    /// take mask, and a restock (new epoch) relocks it for free.
    /// </summary>
    public const int UnlockedBit = 1 << 30;

    private LockPickUi? _lockUi;
    /// <summary>Client: the building whose lock states <see cref="_unlocked"/> holds, and the cracked containers in it.</summary>
    private string _lockKey = "";
    private InteriorNode? _lockNode;
    private readonly HashSet<int> _unlocked = new();
    /// <summary>The container being cracked: which one, in which restock period.</summary>
    private (string Key, int Furniture, long Epoch)? _picking;

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
        _lockUi = new LockPickUi(this) { Name = "LockPickUi" };
        AddChild(_lockUi);
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
        if (_lockUi?.IsOpen == true) return null;
        string what = LootTables.Describe(layout.Furniture[i].Type);
        string key = InputHints.Tag(PlayerInput.InteractMount);
        if (LootTables.IsLocked(layout.Furniture[i].Type) && !IsUnlocked(layout.Key, i))
            return $"{key} Crack the {what}";
        bool empty = _seenEmpty.TryGetValue((layout.Key, i), out long ep)
            && ep == LootTables.Epoch(layout.Key, Now);
        return empty ? $"{key} Search the {what} (empty)" : $"{key} Search the {what}";
    }

    /// <summary>E indoors. Toggles the panel on the facing container; false when there is nothing to search.</summary>
    public bool TrySearch(FootPlayer p)
    {
        if (_ui?.IsOpen == true) { Close(); return true; }
        if (_lockUi?.IsOpen == true) { StopPicking(); return true; }
        if (InteriorManager.Instance is not { Current: { } layout, CurrentNode: { } node }) return false;
        int i = NearestContainer(p, layout, node);
        if (i < 0) return false;
        if (LootTables.IsLocked(layout.Furniture[i].Type) && !IsUnlocked(layout.Key, i))
        {
            StartPicking(p, layout, i);
            return true;
        }

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

    // ---- client: a Battle Royale crate in the same panel (#194) --------------------------------

    /// <summary>The crate (<c>BattleRoyale.BrCrates</c>) open in the panel instead of furniture, if any.</summary>
    private long? _crate;

    /// <summary>Opens a crate's contents in the loot panel; taking goes to the crate's server.</summary>
    public void OpenCrate(FootPlayer p, long id, string title)
    {
        Close();
        _crate = id;
        _searcher = p;
        _ui?.Open(title);
        Play(SfxSynth.Tick, 0.6f);
    }

    /// <summary>The open crate's contents changed (someone took from it): redraw.</summary>
    public void CrateChanged(long id)
    {
        if (_crate == id) _ui?.Refresh();
    }

    /// <summary>The server gave this player a stack of a crate.</summary>
    public void CrateGranted(long id, ItemStack stack)
    {
        _waiting = false;
        if (Items != null)
        {
            int left = Items.Inventory.Add(stack);
            Items.Ui.Toast($"+{stack.Count - left} {ItemDefs.Get(stack.Id)?.Name}");
        }
        Play(SfxSynth.Chime, 1.5f);
        if (_crate != id) return;
        _ui?.Refresh();
        if (_pendingAll) { _pendingAll = false; TakeAll(); }
    }

    /// <summary>The server said no (someone was quicker): the panel shows what is left.</summary>
    public void CrateRefused(long id)
    {
        _waiting = false;
        _pendingAll = false;
        if (_crate == id) _ui?.Refresh();
    }

    public void Close()
    {
        _crate = null;
        _open = null;
        _searcher = null;
        _waiting = false;
        _pendingAll = false;
        _ui?.Close();
    }

    public override void _Process(double delta)
    {
        SyncLocks();
        if (_crate is long crate)
        {
            if (_searcher is not { } cp || !IsInstanceValid(cp) || !cp.IsViewing
                || BattleRoyale.BrCrates.Instance?.InReach(crate, cp.GlobalPosition, 1.4f) != true) Close();
            return;
        }
        if (_picking is { } pick && (_searcher is not { } sp || !IsInstanceValid(sp) || !sp.IsViewing
            || InteriorManager.Instance?.Current?.Key != pick.Key)) StopPicking();
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
        if (_crate is long crate)
        {
            var stacks = BattleRoyale.BrCrates.Instance?.StacksOf(crate) ?? new List<ItemStack>();
            for (int i = 0; i < stacks.Count; i++) yield return (i, stacks[i]);
            yield break;
        }
        for (int i = 0; i < _openStacks.Count; i++)
            if ((_openMask & (1 << i)) == 0) yield return (i, _openStacks[i]);
    }

    public bool Waiting => _waiting;
    public bool IsOpen => _open != null || _crate != null;

    /// <summary>Asks for one stack of the open container. Refused locally when it would not fit.</summary>
    public void Take(int index)
    {
        if (_crate is long crate)
        {
            if (_waiting || Items == null || BattleRoyale.BrCrates.Instance is not { } crates) return;
            var stacks = crates.StacksOf(crate);
            if (index < 0 || index >= stacks.Count) return;
            if (Items.Inventory.Room(stacks[index].Id, stacks[index].Data) < stacks[index].Count)
            {
                Items.Ui.Toast("No room in your pack.");
                return;
            }
            _waiting = true;
            crates.Take(crate, index, stacks[index]);
            return;
        }
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
            Items.Ui.Toast(id == (int)ItemId.Francs
                ? InputHints.Format($"+{count} CHF cash — claim it to your account in the inventory ({{inventory}})")
                : $"+{count - left} {def?.Name}");
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
        if (LootTables.IsLocked(layout.Furniture[furniture].Type) && (mask & UnlockedBit) == 0)
        {
            Reply(peer, MethodName.Locked, key, furniture);
            return;
        }
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
        if (LootTables.IsLocked(layout.Furniture[furniture].Type) && (mask & UnlockedBit) == 0)
        {
            Reply(peer, MethodName.Locked, key, furniture);
            return;
        }
        // restocked since the panel opened, or someone else was quicker: send what is there now
        if (epoch != now || index < 0 || index >= stacks.Count || (mask & (1 << index)) != 0)
        {
            Reply(peer, MethodName.Contents, key, furniture, now,
                stacks.Select(s => (int)s.Id).ToArray(), stacks.Select(s => s.Count).ToArray(), mask);
            return;
        }
        SetMask(key, furniture, now, mask | (1 << index));
        Reply(peer, MethodName.Granted, key, furniture, index, (int)stacks[index].Id, stacks[index].Count);

        // anyone else in this building may have the same container open: tell them it is gone,
        // or their panel keeps offering it until they click it and are refused
        if (!Online || InteriorManager.Instance is not { } interiors) return;
        foreach (int other in Multiplayer.GetPeers())
            if (other != peer && interiors.SpaceOf(other) == key)
                RpcId(other, MethodName.Taken, key, furniture, now, mask | (1 << index));
    }

    /// <summary>Client: another player took from a container; if it is the one open here, drop those stacks.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Taken(string key, int furniture, long epoch, int mask)
    {
        if (_open is not { } open || open.Key != key || open.Furniture != furniture || epoch != _openEpoch) return;
        _openMask |= mask;
        if (!OpenContents().Any()) _seenEmpty[(key, furniture)] = epoch;
        _ui?.Refresh();
    }

    // ---- gun lockers and safes (#165) -------------------------------------------------------------

    /// <summary>Client: whether this client knows the locked container to be cracked this period.</summary>
    public bool IsUnlocked(string key, int furniture) => key == _lockKey && _unlocked.Contains(furniture);

    public bool Picking => _picking != null;
    public LockPickUi? LockUi => _lockUi;

    private void StartPicking(FootPlayer p, InteriorLayout layout, int furniture)
    {
        long epoch = LootTables.Epoch(layout.Key, Now);
        var type = layout.Furniture[furniture].Type;
        _picking = (layout.Key, furniture, epoch);
        _searcher = p;
        _lockUi?.Open(LootTables.Describe(type), LootTables.Combination(layout.Key, furniture, epoch, type),
            type == FurnitureType.Safe ? 1.0f : 1.6f);
    }

    public void StopPicking()
    {
        _picking = null;
        _lockUi?.Close();
    }

    /// <summary>
    /// Client: the dial has been worked through; ask the server to open it. The server checks the
    /// numbers against its own <see cref="LootTables.Combination"/> and that the player is in the building.
    /// </summary>
    public void SubmitCombination(int[] combo)
    {
        if (_picking is not { } pick) return;
        if (Online) RpcId(1, MethodName.RequestUnlock, pick.Key, pick.Furniture, pick.Epoch, combo);
        else ServeUnlock(1, pick.Key, pick.Furniture, pick.Epoch, combo);
    }

    /// <summary>Client: track the building the player is in, and ask the server which of its locks are open.</summary>
    private void SyncLocks()
    {
        var node = InteriorManager.Instance?.CurrentNode;
        if (_lockNode != null && !IsInstanceValid(_lockNode)) _lockNode = null;   // the interior was freed
        if (node == _lockNode) return;
        _lockNode = node;
        _unlocked.Clear();
        _lockKey = node?.Layout.Key ?? "";
        if (node == null || !node.Layout.Furniture.Any(f => LootTables.IsLocked(f.Type))) return;
        if (Online) RpcId(1, MethodName.RequestLocks, _lockKey);
        else ServeLocks(1, _lockKey);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestLocks(string key) => ServeLocks(Multiplayer.GetRemoteSenderId(), key);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestUnlock(string key, int furniture, long epoch, int[] combo) =>
        ServeUnlock(Multiplayer.GetRemoteSenderId(), key, furniture, epoch, combo);

    /// <summary>Server: which locked containers of a building are cracked this restock period. Only a door's state, so anyone may ask.</summary>
    private void ServeLocks(long peer, string key)
    {
        if (!BuildingKey.TryParse(key, out var k)) return;
        long epoch = LootTables.Epoch(key, Now);
        var open = new List<int>();
        if (TileFor(k).Buildings.TryGetValue(k.Index.ToString(), out var b))
            foreach (var (fi, e) in b)
                if (e.Length == 2 && e[0] == epoch && (e[1] & UnlockedBit) != 0 && int.TryParse(fi, out int idx))
                    open.Add(idx);
        Reply(peer, MethodName.LockStates, key, epoch, open.ToArray());
    }

    private async void ServeUnlock(long peer, string key, int furniture, long epoch, int[] combo)
    {
        var layout = await LayoutFor(peer, key);
        if (layout == null || furniture < 0 || furniture >= layout.Furniture.Count) return;
        var type = layout.Furniture[furniture].Type;
        if (!LootTables.IsLocked(type)) return;
        long now = LootTables.Epoch(key, Now);
        int mask = MaskOf(key, furniture, now);
        bool right = epoch == now && combo.SequenceEqual(LootTables.Combination(key, furniture, now, type));
        if (!right && (mask & UnlockedBit) == 0)
        {
            GD.Print($"[loot] peer {peer} gave a wrong combination for {key} #{furniture}");
            Reply(peer, MethodName.UnlockRefused, key, furniture);
            return;
        }
        if ((mask & UnlockedBit) == 0) SetMask(key, furniture, now, mask | UnlockedBit);
        GD.Print($"[loot] {key} #{furniture} ({type}) cracked by peer {peer}");

        // everyone in the building sees the door swing: the cracker first, then the others
        Reply(peer, MethodName.Unlocked, key, furniture, now, true);
        if (!Online || InteriorManager.Instance is not { } interiors) return;
        foreach (int other in Multiplayer.GetPeers())
            if (other != peer && interiors.SpaceOf(other) == key)
                RpcId(other, MethodName.Unlocked, key, furniture, now, false);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void LockStates(string key, long epoch, int[] open)
    {
        if (key != _lockKey) return;
        _unlocked.Clear();
        foreach (int i in open) _unlocked.Add(i);
        if (_lockNode == null) return;
        for (int i = 0; i < _lockNode.Layout.Furniture.Count; i++)
            if (LootTables.IsLocked(_lockNode.Layout.Furniture[i].Type)) _lockNode.SetLockOpen(i, _unlocked.Contains(i), false);
    }

    /// <summary>Client: a locked container in this building was cracked (by this player when <paramref name="mine"/>).</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Unlocked(string key, int furniture, long epoch, bool mine)
    {
        if (key == _lockKey)
        {
            _unlocked.Add(furniture);
            _lockNode?.SetLockOpen(furniture, true, true);
            PlayAt(SfxSynth.DoorOpenBank.Variants[0], 1.6f, -10);
        }
        if (!mine || _picking is not { } pick || pick.Key != key || pick.Furniture != furniture) return;
        var p = _searcher;
        StopPicking();
        if (p != null && IsInstanceValid(p)) TrySearch(p);   // straight into its contents
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void UnlockRefused(string key, int furniture)
    {
        _lockUi?.Refused();
        Items?.Ui.Toast("The lock does not give.");
    }

    /// <summary>Client: the server says a container this client thought open is still locked.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Locked(string key, int furniture)
    {
        if (key == _lockKey)
        {
            _unlocked.Remove(furniture);
            _lockNode?.SetLockOpen(furniture, false, false);
        }
        if (_open is { } open && open.Key == key && open.Furniture == furniture)
        {
            Close();
            Items?.Ui.Toast("It is locked.");
        }
    }

    /// <summary>A sound for the lock and the dial (client only: the dedicated server has no UI).</summary>
    public void PlayAt(AudioStreamWav? stream, float pitch, float db)
    {
        if (stream == null || _ui == null) return;
        var player = new AudioStreamPlayer { Stream = stream, PitchScale = pitch, VolumeDb = db, Bus = SfxBus.Name };
        AddChild(player);
        player.Finished += player.QueueFree;
        player.Play();
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

    /// <summary>Server: what was taken in a Battle Royale match's buildings, kept in memory only (#194).</summary>
    private readonly Dictionary<(string, int), (long Epoch, int Mask)> _matchMasks = new();

    /// <summary>Server: a match is over; its taken marks go.</summary>
    public void ForgetMatch() => _matchMasks.Clear();

    private int MaskOf(string key, int furniture, long epoch)
    {
        if (LootTables.MatchEpoch?.Invoke(key) != null)
            return _matchMasks.TryGetValue((key, furniture), out var m) && m.Epoch == epoch ? m.Mask : 0;
        if (!BuildingKey.TryParse(key, out var k)) return 0;
        var tile = TileFor(k);
        return tile.Buildings.TryGetValue(k.Index.ToString(), out var b)
            && b.TryGetValue(furniture.ToString(), out var e) && e.Length == 2 && e[0] == epoch
            ? (int)e[1] : 0;
    }

    private void SetMask(string key, int furniture, long epoch, int mask)
    {
        if (LootTables.MatchEpoch?.Invoke(key) != null)
        {
            _matchMasks[(key, furniture)] = (epoch, mask);
            return;
        }
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
