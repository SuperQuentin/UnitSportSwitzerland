using System.Text.Json;
using Godot;

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
/// on the server, keyed by player name, in <c>user://bank/accounts.json</c> — one account per
/// server, like the world it was earned in; offline this client keeps its own in
/// <c>user://account.json</c> through the same methods.
/// </para>
///
/// <para>
/// The server cannot check the amount: the inventory, and so the cash, is the client's (see
/// <see cref="Inventory"/>). What the server guarantees is that a balance only goes up by what a
/// claim says and survives the client. Identity is the display name, which a server does not
/// password — someone joining under your name reaches your account. Fine among friends; a real
/// economy would need accounts.
/// </para>
/// </summary>
public partial class Bank : Node
{
    public const string NodeName = "Bank";

    private const string ServerFile = "user://bank/accounts.json";
    private const string LocalFile = "user://account.json";

    public static Bank? Instance { get; private set; }

    /// <summary>Server: the display name of a peer, which keys its account.</summary>
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

    private bool _server;

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
        if (_server) LoadAccounts(ServerFile);
        else
        {
            LoadAccounts(LocalFile);
            Balance = _accounts.GetValueOrDefault(LocalName);
        }
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => Multiplayer.MultiplayerPeer is { } peer and not OfflineMultiplayerPeer
        && peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

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
    /// Server: sends a peer its balance. Called once the chat has settled its name — on joining and
    /// after <c>/name</c> — because the name is the account, and on connect it is not known yet.
    /// </summary>
    public void SendBalance(long peer)
    {
        if (_server) RpcId(peer, MethodName.Deposited, 0, _accounts.GetValueOrDefault(Account(peer)));
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
        string who = Account(sender);
        bool there = InBank == null || await InBank(sender);
        // a pocket holds what one session can plausibly pick up; anything past that is not a claim
        if (!there || amount <= 0 || amount > 1_000_000)
        {
            if (!there) GD.Print($"[bank] {who} tried to deposit outside a bank");
            RpcId(sender, MethodName.Deposited, 0, _accounts.GetValueOrDefault(who));
            return;
        }
        long balance = _accounts[who] = _accounts.GetValueOrDefault(who) + amount;
        SaveAccounts(ServerFile);
        GD.Print($"[bank] {who} claimed {amount} CHF, balance {balance}");
        RpcId(sender, MethodName.Deposited, amount, balance);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private async void RequestWithdraw(int amount)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        string who = Account(sender);
        long had = _accounts.GetValueOrDefault(who);
        bool there = InBank == null || await InBank(sender);
        if (!there || amount <= 0 || amount > had)
        {
            GD.Print($"[bank] {who} withdrawal of {amount} CHF refused ({(there ? "balance " + had : "not in a bank")})");
            RpcId(sender, MethodName.Withdrawn, 0, had);
            return;
        }
        long balance = _accounts[who] = had - amount;
        SaveAccounts(ServerFile);
        GD.Print($"[bank] {who} withdrew {amount} CHF, balance {balance}");
        RpcId(sender, MethodName.Withdrawn, amount, balance);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestBalance()

    {
        if (!_server) return;
        SendBalance(Multiplayer.GetRemoteSenderId());
    }

    private string Account(long peer) => NameOf?.Invoke(peer) ?? $"Rider{peer}";

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

    private void SaveAccounts(string file)
    {
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(file.GetBaseDir()));
        using var f = Godot.FileAccess.Open(file, Godot.FileAccess.ModeFlags.Write);
        f?.StoreString(JsonSerializer.Serialize(_accounts, new JsonSerializerOptions { WriteIndented = true }));
    }
}
