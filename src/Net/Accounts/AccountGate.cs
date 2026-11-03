using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace UnitSport.Net.Accounts;

/// <summary>
/// The account step of a join (#1123), at <c>World/Accounts</c> on the server and every client, right after the
/// version check (<see cref="Handshake"/>, whose RPCs never change). The server either admits at once
/// (<c>--accounts off</c>: <c>Welcome("")</c>) or asks for the player's session token; the client presents it
/// (or nothing, offline); the server reads that account from the account server with the token
/// (<see cref="NakamaRest.Account"/>), then admits or refuses by <see cref="AccountRules.Refusal"/>.
/// The client joins for real (name, terrain sync) only once welcomed here, so nothing of a refused peer is
/// ever acted on.
/// </summary>
public partial class AccountGate : Node
{
    public const string NodeName = "Accounts";

    /// <summary>How long the server waits for a peer's token: the client may first renew its session over HTTP.</summary>
    public const double WaitSeconds = 20;

    private bool _server;
    private ServerAccounts _mode;
    private string _url = "";
    private readonly Dictionary<long, double> _waiting = new();
    private readonly Dictionary<long, NakamaAccount> _accounts = new();

    /// <summary>Server: the peer may have its player and the world.</summary>
    public event Action<long>? Admitted;

    /// <summary>Client: the server let this player in; its account name, or "" as an offline player.</summary>
    public event Action<string>? Welcomed;

    /// <summary>Client: the server refused this player; the reason, for the player.</summary>
    public event Action<string>? Refused;

    public ServerAccounts Mode => _mode;

    /// <summary>Server: the checked account of a peer, or null for an offline player.</summary>
    public NakamaAccount? Of(long peer) => _accounts.GetValueOrDefault(peer);

    public static AccountGate CreateServer(ServerAccounts mode, string url) =>
        new() { Name = NodeName, _server = true, _mode = mode, _url = AccountRules.NormalizeUrl(url) };

    public static AccountGate CreateClient() => new() { Name = NodeName };

    private static double Now => Time.GetTicksMsec() / 1000.0;

    /// <summary>Server: the peer passed the version check.</summary>
    public void Check(long peer)
    {
        if (_mode == ServerAccounts.Off)
        {
            RpcId(peer, MethodName.Welcome, "");
            Admitted?.Invoke(peer);
            return;
        }
        _waiting[peer] = Now;
        RpcId(peer, MethodName.Ask, (int)_mode, _url);
    }

    /// <summary>Server: a peer left, admitted or not.</summary>
    public void PeerLeft(long peer)
    {
        _waiting.Remove(peer);
        _accounts.Remove(peer);
    }

    public override void _Process(double delta)
    {
        if (!_server || _waiting.Count == 0) return;
        foreach (var (peer, since) in _waiting.ToList())
            if (Now - since > WaitSeconds)
            {
                GD.Print($"[accounts] peer {peer} presented no account in {WaitSeconds:0} s");
                Decide(peer, presented: false, null);
            }
    }

    private void Decide(long peer, bool presented, NakamaAccount? account)
    {
        if (!_waiting.Remove(peer)) return;   // decided already, or gone
        if (AccountRules.Refusal(_mode, presented, account != null, _url) is { } reason)
        {
            GD.Print($"[accounts] peer {peer} refused: {reason}");
            RpcId(peer, MethodName.Refuse, reason);
            var tree = GetTree();
            tree.CreateTimer(0.3).Timeout += () =>
            {
                if (tree.Root.Multiplayer.MultiplayerPeer is ENetMultiplayerPeer enet) enet.DisconnectPeer((int)peer);
            };
            return;
        }
        if (account != null) _accounts[peer] = account;
        GD.Print(account != null ? $"[accounts] peer {peer} admitted as {account.Username} ({account.UserId})"
            : $"[accounts] peer {peer} admitted offline");
        RpcId(peer, MethodName.Welcome, account?.Username ?? "");
        Admitted?.Invoke(peer);
    }

    private async Task Verify(long peer, string token)
    {
        NakamaAccount? account = null;
        try { account = await NakamaRest.Account(_url, token); }
        catch (Exception e) when (e is NakamaException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            GD.PushWarning($"[accounts] could not check peer {peer}'s account: {e.Message}");
        }
        // back on the main thread (Godot's synchronization context): the peer may have left meanwhile
        Decide(peer, presented: true, account);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Present(string token)
    {
        if (!_server) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (!_waiting.ContainsKey(peer)) return;
        if (token.Length is 0 or > 4096) Decide(peer, presented: false, null);
        else _ = Verify(peer, token);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private async void Ask(int mode, string url)
    {
        if (_server) return;
        string token = await Account.TokenFor(url);
        GD.Print($"[accounts] the server's accounts are {((ServerAccounts)mode).ToString().ToLowerInvariant()} on {url}: "
            + (token.Length > 0 ? $"presenting {Account.Username}" : $"nothing to present ({Account.Status})"));
        if (Net.NetLink.Ready(this)) RpcId(1, MethodName.Present, token);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Welcome(string username)
    {
        if (_server) return;
        GD.Print(username.Length > 0 ? $"[accounts] admitted as {username}" : "[accounts] admitted");
        Welcomed?.Invoke(username);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Refuse(string reason)
    {
        if (_server) return;
        GD.Print($"[accounts] refused: {reason}");
        Refused?.Invoke(reason);
    }
}
