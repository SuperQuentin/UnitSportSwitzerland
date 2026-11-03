using System.Text.Json;
using Godot;
using UnitSport.Net;

namespace UnitSport.Items;

/// <summary>
/// The player's account: where deposited cash goes, at <c>World/Bank</c> on the server and on every
/// client. RPCs route by node path, so the name matches on both sides, as for <c>World/Loot</c>.
/// Money moves only at a bank's teller desk (#213, <see cref="BankCounterUi"/>): the server checks
/// the player stands in a bank (<see cref="InBank"/>) before every deposit and withdrawal.
///
/// <para>
/// <b>Cash and account.</b> Francs you pick up are <see cref="Inventory.Cash"/>: carried, and lost
/// if you are knocked out. Claiming moves them here, where they are safe. Online the balance lives
/// on the server, keyed by the player's id (<see cref="PlayerIds"/>, #1359), in <c>user://bank/accounts.json</c> — one
/// account per server, like the world it was earned in; offline this client keeps its own in
/// <c>user://account.json</c> through the same methods. A file from before ids keys by name: such an account
/// moves to its player's id when the player is identified (<see cref="ClaimLegacy"/>).
/// </para>
///
/// <para>
/// The server cannot check the amount: the inventory, and so the cash, is the client's (see
/// <see cref="Inventory"/>). What the server guarantees is that a balance only goes up by what a
/// claim says and survives the client. Identity is the player's id, proven by its key or its account,
/// never its name: renaming loses nothing, and taking someone's name reaches nothing. A player not
/// proven yet moves no money.
/// </para>
/// </summary>
public partial class Bank : Node
{
    public const string NodeName = "Bank";

    private const string ServerFile = "user://bank/accounts.json";
    /// <summary>Beside the accounts: the last name seen for each id, a fallback for an id that no longer resolves (#1359).</summary>
    private const string NamesFile = "user://bank/account_names.json";
    private const string LocalFile = "user://account.json";

    public static Bank? Instance { get; private set; }

    /// <summary>A check's run (<c>--adventurecheck</c>): balances move, nothing is written, so the player's own account stays as it was.</summary>
    public static bool Scratch { get; set; }

    /// <summary>Unused since #1359 (accounts are keyed by id); the server's wiring still sets it.</summary>
    public Func<long, string>? NameOf { get; set; }

    /// <summary>Server: whether a peer is inside a bank, where money may move. Null lets it anywhere.</summary>
    public Func<long, Task<bool>>? InBank { get; set; }

    /// <summary>Client: the pocket the cash comes from and goes to.</summary>
    public Inventory? Pocket => _inventory;

    /// <summary>This client's balance as the bank last reported it.</summary>
    public long Balance { get; private set; }

    /// <summary>A deposit or a withdrawal is on its way and has not been answered.</summary>
    public bool Pending => _pending != 0;

    /// <summary>Client: the balance changed; the argument is how much was just deposited (0 for a plain refresh).</summary>
    public event Action<long>? BalanceChanged;

    private Inventory? _inventory;
    private int _pending;
    private readonly Dictionary<string, long> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);

    private bool _server;
    private PlayerIds? _claimFrom;

    /// <summary>
    /// <paramref name="server"/> for the dedicated server's copy, decided up front: the network is
    /// not up yet when the world is built, so asking it would read "offline".
    /// </summary>
    public static Bank Create(Node world, Inventory? inventory, bool server = false)
    {
        var bank = new Bank { Name = NodeName, _inventory = inventory, _server = server };
        world.AddChild(bank);
        Instance = bank;
        return bank;
    }

    public override void _Ready()
    {
        if (_server)
        {
            LoadAccounts(ServerFile);
            LoadNames();
            if (PlayerIds.Server is { } ids) { _claimFrom = ids; ids.Identified += Identified; }
        }
        else
        {
            LoadAccounts(LocalFile);
            Balance = _accounts.GetValueOrDefault(LocalName);
        }
    }

    public override void _ExitTree()
    {
        if (_claimFrom != null) _claimFrom.Identified -= Identified;
        _claimFrom = null;
        if (Instance == this) Instance = null;
    }

    private const string Unproven = "Your identity is not confirmed yet: try again in a moment.";

    /// <summary>Server: a peer has its id and its name; an account still under the name becomes the id's, then the peer is told its balance.</summary>
    private void Identified(long peer, string id, string name)
    {
        ClaimLegacy(peer, id, name);
        Remember(id, name);
        SendBalance(peer);
    }

    /// <summary>
    /// Moves the account still keyed by <paramref name="name"/> (an old file keys by name, case-insensitively) to
    /// <paramref name="id"/>, unless the id has an account of its own. Returns whether one moved.
    /// </summary>
    public bool ClaimLegacy(long peer, string id, string name)
    {
        if (!_server || _accounts.ContainsKey(id)) return false;
        string? old = _accounts.Keys.FirstOrDefault(k => !PlayerIds.IsId(k) && k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (old == null) return false;
        _accounts[id] = _accounts[old];
        _accounts.Remove(old);
        SaveAccounts(ServerFile);
        GD.Print($"[bank] account of {name} now kept under {id}");
        return true;
    }

    private bool Online => NetLink.Online(this);

    private const string LocalName = "local";

    // ---- client ---------------------------------------------------------------------------------

    /// <summary>
    /// Moves all the cash in the pocket to the account. The cash leaves the pocket only once the
    /// bank has answered, so a claim lost with the connection loses nothing.
    /// </summary>
    public void ClaimAll()
    {
        if (_inventory is not { Cash: > 0 } inv || _pending != 0) return;
        int amount = inv.Cash;
        if (!Online)
        {
            inv.TakeCash(amount);
            Balance = _accounts[LocalName] = Balance + amount;
            SaveAccounts(LocalFile);
            BalanceChanged?.Invoke(amount);
            return;
        }
        _pending = amount;
        RpcId(1, MethodName.RequestDeposit, amount);
    }

    /// <summary>
    /// Draws francs from the account into the pocket. Online the cash arrives with the server's
    /// answer, and only if the balance covers it.
    /// </summary>
    public void Withdraw(int amount)
    {
        if (_inventory == null || _pending > 0 || amount <= 0 || amount > Balance) return;
        if (!Online)
        {
            Balance = _accounts[LocalName] = Balance - amount;
            SaveAccounts(LocalFile);
            _inventory.Add(ItemId.Francs, amount);
            BalanceChanged?.Invoke(0);
            return;
        }
        _pending = -amount;
        RpcId(1, MethodName.RequestWithdraw, amount);
    }

    /// <summary>Asks the server for this player's balance again.</summary>
    public void Refresh()
    {
        if (Online && !_server) RpcId(1, MethodName.RequestBalance);
    }

    /// <summary>
    /// Server: sends a peer its balance. Called once the peer is identified (<see cref="PlayerIds.Identified"/>) and
    /// after <c>/name</c>; before that the account is not known and the peer is told 0.
    /// </summary>
    public void SendBalance(long peer)
    {
        if (_server) RpcId(peer, MethodName.Deposited, 0, BalanceOf(peer));
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Deposited(int amount, long balance)
    {
        if (amount > 0 && _pending == amount) _inventory?.TakeCash(Math.Min(amount, _inventory.Cash));
        _pending = 0;
        Balance = balance;
        BalanceChanged?.Invoke(amount);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Withdrawn(int amount, long balance)
    {
        if (amount > 0 && _pending == -amount) _inventory?.Add(ItemId.Francs, amount);
        _pending = 0;
        Balance = balance;
        BalanceChanged?.Invoke(0);
    }

    // ---- server ---------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private async void RequestDeposit(int amount)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (Account(sender) is not { } who)
        {
            GD.Print($"[bank] peer {sender} deposit refused: identity not confirmed");
            RpcId(sender, MethodName.Refused, Unproven);
            return;
        }
        bool there = InBank == null || await InBank(sender);
        // a pocket holds what one session can plausibly pick up; anything past that is not a claim
        if (!there || amount <= 0 || amount > 1_000_000)
        {
            if (!there) GD.Print($"[bank] {Shown(who)} tried to deposit outside a bank");
            RpcId(sender, MethodName.Deposited, 0, _accounts.GetValueOrDefault(who));
            return;
        }
        long balance = _accounts[who] = _accounts.GetValueOrDefault(who) + amount;
        SaveAccounts(ServerFile);
        Touch(sender, who);
        GD.Print($"[bank] {Shown(who)} claimed {amount} CHF, balance {balance}");
        RpcId(sender, MethodName.Deposited, amount, balance);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private async void RequestWithdraw(int amount)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (Account(sender) is not { } who)
        {
            GD.Print($"[bank] peer {sender} withdrawal refused: identity not confirmed");
            RpcId(sender, MethodName.Refused, Unproven);
            return;
        }
        long had = _accounts.GetValueOrDefault(who);
        bool there = InBank == null || await InBank(sender);
        if (!there || amount <= 0 || amount > had)
        {
            GD.Print($"[bank] {Shown(who)} withdrawal of {amount} CHF refused ({(there ? "balance " + had : "not in a bank")})");
            RpcId(sender, MethodName.Withdrawn, 0, had);
            return;
        }
        long balance = _accounts[who] = had - amount;
        SaveAccounts(ServerFile);
        Touch(sender, who);
        GD.Print($"[bank] {Shown(who)} withdrew {amount} CHF, balance {balance}");
        RpcId(sender, MethodName.Withdrawn, amount, balance);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestBalance()

    {
        if (!_server) return;
        SendBalance(Multiplayer.GetRemoteSenderId());
    }

    /// <summary>Keeps the name an id is known by now, as the backup shown if the id stops resolving.</summary>
    private void Remember(string id, string? name)
    {
        if (!_server || Scratch || string.IsNullOrEmpty(name) || id.StartsWith("bot:", StringComparison.Ordinal)
            || (_names.TryGetValue(id, out var was) && was == name)) return;
        _names[id] = name;
        Core.JsonStore.SaveAsync(NamesFile, _names, Core.JsonStore.Indented, e => GD.PushWarning($"[bank] could not write {NamesFile}: {e.Message}"));
    }

    /// <summary>A peer acted on its account: refreshes the backup name.</summary>
    private void Touch(long peer, string id) => Remember(id, PlayerIds.Server?.NameOf(peer));

    /// <summary>Server: the id a peer's account hangs on, or null while the peer is not proven (nothing is kept for it).</summary>
    private string? Account(long peer) => PlayerIds.Server?.IdOf(peer);

    /// <summary>A name for the log; the accounts of an old file are keyed by one.</summary>
    private string Shown(string key) => PlayerIds.IsId(key) ? $"{PlayerIds.Server?.NameOfId(key, _names.GetValueOrDefault(key)) ?? "?"} ({key})" : key;

    /// <summary>Client: the server says why it did not move the money (an unproven identity); the pending claim is dropped.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Refused(string why)
    {
        GD.Print($"[bank] {why}");
        _pending = 0;
        BalanceChanged?.Invoke(0);
        Refusal?.Invoke(why);
    }

    /// <summary>Client: the server refused a deposit or a withdrawal, and says why.</summary>
    public event Action<string>? Refusal;

    // ---- the card (#273, Loot/ShopService) -------------------------------------------------------

    /// <summary>
    /// A card payment in a shop: takes <paramref name="amount"/> from the peer's account, or from
    /// this machine's offline (where the client plays the server). No overdraft: false, and nothing
    /// taken, when the balance does not cover it. Only the server (or an offline client) calls this.
    /// </summary>
    public bool Charge(long peer, int amount, out long balance)
    {
        string? who = _server ? Account(peer) : LocalName;
        long had = who == null ? 0 : _accounts.GetValueOrDefault(who);
        if (who == null || amount <= 0 || amount > had)
        {
            balance = had;
            GD.Print($"[bank] {(who == null ? "peer " + peer : Shown(who))} card payment of {amount} CHF declined (balance {had})");
            return false;
        }
        balance = had - amount;
        if (balance == 0) _accounts.Remove(who);
        else _accounts[who] = balance;
        SaveAccounts(_server ? ServerFile : LocalFile);
        if (_server) Touch(peer, who);
        GD.Print($"[bank] {Shown(who)} paid {amount} CHF by card, balance {balance}");
        if (!_server) Report(balance);
        return true;
    }

    /// <summary>
    /// Pays <paramref name="amount"/> into the peer's account (a quest's reward, #1223): the account, not the
    /// pocket, so a knock-out cannot lose it. On the server the owner is told its new balance; offline this
    /// machine's account. Only the server (or an offline client) calls this. Returns the balance after.
    /// </summary>
    public long Credit(long peer, int amount, string why)
    {
        string? who = _server ? Account(peer) : LocalName;
        if (who == null)
        {
            GD.Print($"[bank] peer {peer} credit of {amount} CHF ({why}) skipped: identity not confirmed");
            return 0;
        }
        long had = _accounts.GetValueOrDefault(who);
        if (amount <= 0) return had;
        long balance = _accounts[who] = Math.Min(Net.AdminArgs.MaxBalance, had + amount);
        SaveAccounts(_server ? ServerFile : LocalFile);
        if (_server) Touch(peer, who);
        GD.Print($"[bank] {Shown(who)} credited {amount} CHF ({why}), balance {balance}");
        // never RpcId an id that is not a connected client's (a bot's: a negative id means "everyone but")
        if (_server) { if (peer > 0 && Multiplayer.GetPeers().Contains((int)peer)) RpcId(peer, MethodName.Credited, amount, balance); }
        else Report(balance);
        return balance;
    }

    /// <summary>A peer's account balance (the server's, offline this machine's): what a fare is checked against (#1226).</summary>
    public long BalanceOf(long peer) => (_server ? Account(peer) : LocalName) is { } who ? _accounts.GetValueOrDefault(who) : 0;

    /// <summary>
    /// Server: tells a peer its balance after a charge it did not ask the bank for (a travel fare, #1226), the way a
    /// reward is told: only the balance moves, never a pending deposit. Offline <see cref="Charge"/> reports it already.
    /// </summary>
    public void Tell(long peer)
    {
        if (_server && peer > 0 && Multiplayer.GetPeers().Contains((int)peer))
            RpcId(peer, MethodName.Credited, 0, BalanceOf(peer));
    }

    /// <summary>The server paid into this player's account (a reward): only the balance moves, never a pending claim.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Credited(int amount, long balance) => Report(balance);

    /// <summary>Client: the server says the account now holds this (after a card payment).</summary>
    public void Report(long balance)
    {
        Balance = balance;
        BalanceChanged?.Invoke(0);
    }

    // ---- admin ----------------------------------------------------------------------------------

    /// <summary>
    /// <c>/bank</c>: reads an account, or sets it (<paramref name="set"/>) or adds to it
    /// (<paramref name="add"/>, negative to take). On the server <paramref name="account"/> is a
    /// player name, looked up to the player's id (online, else the last name seen; an away player's account works
    /// too); a name nobody was ever seen under addresses an old file's name-keyed account. Offline it is ignored,
    /// there is one account. The caller has checked the rights. Returns the balance after.
    /// </summary>
    public long AdminAdjust(string? account, long? set = null, long add = 0)
    {
        string who = _server ? account != null ? KeyOfName(account) : LocalName : LocalName;
        long had = _accounts.GetValueOrDefault(who);
        long now = Math.Clamp(set ?? had + add, 0, Net.AdminArgs.MaxBalance);
        if (now != had)
        {
            // an emptied account is no account: the file keeps only names that hold something
            if (now == 0) _accounts.Remove(who);
            else _accounts[who] = now;
            SaveAccounts(_server ? ServerFile : LocalFile);
            GD.Print($"[bank] admin: {(_server ? Shown(who) : who)} {had} -> {now} CHF");
        }
        if (!_server)
        {
            Balance = now;
            BalanceChanged?.Invoke(0);
        }
        return now;
    }

    /// <summary>The account key of a player named <paramref name="name"/>: its id when it is online or was ever seen, else the name (a legacy account).</summary>
    private string KeyOfName(string name)
    {
        if (PlayerIds.Server is not { } ids) return name;
        foreach (long peer in ids.Online())
            if (ids.NameOf(peer) is { } n && n.Equals(name, StringComparison.OrdinalIgnoreCase) && ids.IdOf(peer) is { } id) return id;
        foreach (var (id, last) in ids.LastNames)
            if (last.Equals(name, StringComparison.OrdinalIgnoreCase) && !id.StartsWith("bot:", StringComparison.Ordinal)) return id;
        foreach (var (id, backup) in _names)
            if (backup.Equals(name, StringComparison.OrdinalIgnoreCase)) return id;
        return name;
    }

    private void LoadNames()
    {
        try
        {
            if (!Godot.FileAccess.FileExists(NamesFile)) return;
            using var f = Godot.FileAccess.Open(NamesFile, Godot.FileAccess.ModeFlags.Read);
            if (JsonSerializer.Deserialize<Dictionary<string, string>>(f.GetAsText()) is { } data)
                foreach (var (id, name) in data) _names[id] = name;
        }
        catch (Exception e) { GD.PushWarning($"[bank] could not read {NamesFile}: {e.Message}"); }
    }

    // ---- storage --------------------------------------------------------------------------------

    private void LoadAccounts(string file)
    {
        try
        {
            if (!Godot.FileAccess.FileExists(file)) return;
            using var f = Godot.FileAccess.Open(file, Godot.FileAccess.ModeFlags.Read);
            var data = JsonSerializer.Deserialize<Dictionary<string, long>>(f.GetAsText());
            if (data != null)
                foreach (var (name, balance) in data) _accounts[name] = balance;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[bank] could not read {file}: {e.Message}");
        }
    }

    // written in the background (json-store): a claim does not wait for the disk
    private void SaveAccounts(string file)
    {
        if (Scratch) return;
        Core.JsonStore.SaveAsync(file, _accounts, Core.JsonStore.Indented,
            e => GD.PushWarning($"[bank] could not write {file}: {e.Message}"));
    }
}
