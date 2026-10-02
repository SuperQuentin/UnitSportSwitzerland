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
    private SimonUi? _simonUi;
    private BankCounterUi? _counterUi;
    /// <summary>The dial's numbers of a vault safe, kept while its Simon panel is played.</summary>
    private int[]? _dialDone;
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
        _simonUi = new SimonUi(this) { Name = "SimonUi" };
        AddChild(_simonUi);
        _counterUi = new BankCounterUi { Name = "BankCounterUi" };
        AddChild(_counterUi);
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
    public static int NearestContainer(FootPlayer p, InteriorLayout layout, InteriorNode node) =>
        NearestOf(p, layout, node, LootTables.IsLootable);

    /// <summary>The bank's teller desk the player is facing, if any (#213).</summary>
    public static int NearestCounter(FootPlayer p, InteriorLayout layout, InteriorNode node) =>
        layout.IsBank ? NearestOf(p, layout, node, t => t == FurnitureType.TellerDesk) : -1;

    private static int NearestOf(FootPlayer p, InteriorLayout layout, InteriorNode node, Func<FurnitureType, bool> wanted)
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
            if (!wanted(f.Type)) continue;
            float floorY = layout.FloorY(f.Floor);
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
        if (_ui?.IsOpen == true || _simonUi?.IsOpen == true || _counterUi?.IsOpen == true) return null;
        if (InteriorManager.Instance is not { Current: { } layout, CurrentNode: { } node }) return null;
        string key = InputHints.Tag(PlayerInput.InteractMount);
        if (NearestCounter(p, layout, node) >= 0) return $"{key} Bank counter: deposit or withdraw";
        int i = NearestContainer(p, layout, node);
        if (i < 0) return null;
        if (_lockUi?.IsOpen == true) return null;
        string what = LootTables.Describe(layout.Furniture[i].Type);
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
        if (_lockUi?.IsOpen == true || _simonUi?.IsOpen == true) { StopPicking(); return true; }
        if (_counterUi?.IsOpen == true) { _counterUi.Close(); return true; }
        if (InteriorManager.Instance is not { Current: { } layout, CurrentNode: { } node }) return false;
        if (NearestCounter(p, layout, node) is var counter and >= 0)
        {
            _counterUi?.Open(p, node, layout.Furniture[counter]);
            return true;
        }
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

    /// <summary>The locked crate whose dial is being worked (a bunker door), if any.</summary>
    private long? _pickingCrate;

    /// <summary>Opens the dial on a locked crate; the numbers it settles on go to the crate's server.</summary>
    public void PickCrate(FootPlayer p, long id, string title, int[] combo)
    {
        Close();
        StopPicking();
        _pickingCrate = id;
        _searcher = p;
        _lockUi?.Open(title, combo, 1.0f);
    }

    /// <summary>A crate's dial was cracked; by this player: straight into its contents.</summary>
    public void CrateUnlocked(long id, bool mine)
    {
        if (!mine || _pickingCrate != id) return;
        var p = _searcher;
        StopPicking();
        if (p != null && IsInstanceValid(p)) BattleRoyale.BrCrates.Instance?.TryOpen(p);
    }

    public void CrateUnlockRefused(long id)
    {
        if (_pickingCrate == id) _lockUi?.Refused();
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

    /// <summary>
    /// Server: whether a peer stands in a bank (#213), where its cash can be deposited and drawn.
    /// The bank checks this before it moves any money. Offline there is no one to check.
    /// </summary>
    public async Task<bool> InBank(long peer)
    {
        if (!Online) return true;
        if (InteriorManager.Instance is not { } interiors || interiors.SpaceOf(peer) is not { Length: > 0 } key) return false;
        return await LayoutFor(peer, key) is { IsBank: true };
    }

    public override void _Process(double delta)
    {
        SyncLocks();
        if (_pickingCrate is long locked && (_searcher is not { } lp || !IsInstanceValid(lp) || !lp.IsViewing
            || BattleRoyale.BrCrates.Instance?.InReach(locked, lp.GlobalPosition, 1.4f) != true)) StopPicking();
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
    /// <summary>A Battle Royale crate's panel or dial is open (not a house's container).</summary>
    public bool CrateOpen => _crate != null || _pickingCrate != null;

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
                ? $"+{count} CHF cash: deposit it at a bank counter, or lose it if you are knocked out"
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
        _dialDone = null;
        _lockUi?.Open(LootTables.Describe(type), LootTables.Combination(layout.Key, furniture, epoch, type),
            type switch { FurnitureType.GunLocker => 1.6f, FurnitureType.Safe => 1.0f, _ => 0.8f });
    }

    public void StopPicking()
    {
        _picking = null;
        _pickingCrate = null;
        _dialDone = null;
        _lockUi?.Close();
        _simonUi?.Close();
    }

    /// <summary>The Simon panel of a vault safe being cracked, if open (for probes).</summary>
    public SimonUi? Simon => _simonUi;

    /// <summary>
    /// Client: the vault safe's Simon panel has been played through; send the dial's numbers and
    /// the sequence together. The server checks both against what it derives itself.
    /// </summary>
    public void SubmitSimon(int[] sequence)
    {
        if (_picking is not { } pick || _dialDone is not { } dial) return;
        var all = dial.Concat(sequence).ToArray();
        if (Online) RpcId(1, MethodName.RequestUnlock, pick.Key, pick.Furniture, pick.Epoch, all);
        else ServeUnlock(1, pick.Key, pick.Furniture, pick.Epoch, all);
    }

    /// <summary>
    /// Client: the dial has been worked through; ask the server to open it. The server checks the
    /// numbers against its own <see cref="LootTables.Combination"/> and that the player is in the building.
    /// </summary>
    public void SubmitCombination(int[] combo)
    {
        if (_pickingCrate is long crate)
        {
            BattleRoyale.BrCrates.Instance?.Unlock(crate, combo);
            return;
        }
        if (_picking is not { } pick) return;
        // a vault safe's dial only lets you at its code panel: that is checked with it, at the end
        if (InteriorManager.Instance?.Current is { } layout && layout.Key == pick.Key
            && LootTables.NeedsSimon(layout.Furniture[pick.Furniture].Type))
        {
            _dialDone = combo;
            _lockUi?.Close();
            _simonUi?.Open(LootTables.Describe(layout.Furniture[pick.Furniture].Type),
                LootTables.SimonSequence(layout, pick.Furniture, pick.Epoch));
            return;
        }
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
                if (Current(e, epoch) && (e[1] & UnlockedBit) != 0 && int.TryParse(fi, out int idx))
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
        var expected = LootTables.Combination(key, furniture, now, type);
        if (LootTables.NeedsSimon(type)) expected = expected.Concat(LootTables.SimonSequence(layout, furniture, now)).ToArray();
        bool right = epoch == now && combo.SequenceEqual(expected);
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
        if (_simonUi?.IsOpen == true)
        {
            // a new restock period began while it was played: start again with the new numbers
            StopPicking();
            Items?.Ui.Toast("The lock reset itself. Try again.");
            return;
        }
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

    /// <summary>
    /// Per tile: building index → furniture index → (epoch, taken mask, plan version). The masks
    /// count stacks by the furniture's index in the plan, so a record written against an older
    /// <see cref="InteriorLayout.CurrentVersion"/> (furniture renumbered when the plans were
    /// regenerated) is ignored, as if never written: no container starts wrongly emptied, wrongly
    /// full or wrongly cracked after an update. Records without a version are from before #213.
    /// </summary>
    private sealed class TileState
    {
        public Dictionary<string, Dictionary<string, long[]>> Buildings { get; set; } = new();
    }

    /// <summary>Server: what was taken in a Battle Royale match's buildings, kept in memory only (#194).</summary>
    private readonly Dictionary<(string, int), (long Epoch, int Mask)> _matchMasks = new();

    /// <summary>Server: a match is over; its taken marks go.</summary>
    public void ForgetMatch() => _matchMasks.Clear();

    /// <summary>A saved record that still applies: this restock period, and plans of the current version.</summary>
    private static bool Current(long[] e, long epoch) =>
        e.Length >= 3 && e[0] == epoch && e[2] == InteriorLayout.CurrentVersion;

    private int MaskOf(string key, int furniture, long epoch)
    {
        if (LootTables.MatchEpoch?.Invoke(key) != null)
            return _matchMasks.TryGetValue((key, furniture), out var m) && m.Epoch == epoch ? m.Mask : 0;
        if (!BuildingKey.TryParse(key, out var k)) return 0;
        var tile = TileFor(k);
        return tile.Buildings.TryGetValue(k.Index.ToString(), out var b)
            && b.TryGetValue(furniture.ToString(), out var e) && Current(e, epoch)
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
        b[furniture.ToString()] = new[] { epoch, mask, InteriorLayout.CurrentVersion };
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
