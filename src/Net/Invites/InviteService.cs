using Godot;
using UnitSport.Core;

namespace UnitSport.Net;

/// <summary>
/// What an activity registers to get invites (#840): its name, how long one lasts, and what accepting
/// does. Every callback runs on the server; <see cref="Accept"/> returns the accepter's private reply.
/// </summary>
public sealed class InviteKind
{
    /// <summary>Short and stable: in <c>/accept br</c>, on the wire and in the logs.</summary>
    public required string Id { get; init; }
    /// <summary>For people: "Battle Royale", "teleport", "fist fight".</summary>
    public required string Label { get; init; }
    public double Seconds { get; init; } = 60;
    /// <summary>Sending one back the other way round answers it (two challenges cross: a fight).</summary>
    public bool Mutual { get; init; }
    /// <summary>The accepter's peer and the invite: does it, returns the accepter's reply.</summary>
    public required Func<long, Invite, string> Accept { get; init; }
    /// <summary>Called on a decline; the sender of a direct invite is told in any case.</summary>
    public Action<long, Invite>? Declined { get; init; }
    /// <summary>Called when it lapses or is withdrawn by its sender; the sender is told in any case.</summary>
    public Action<Invite>? Lapsed { get; init; }
    /// <summary>The sender of a direct invite is told it was accepted (off when the kind says so itself).</summary>
    public bool TellAccepted { get; init; } = true;
}

/// <summary>An invite as a client sees it (#840): to the local player, or sent by them.</summary>
public sealed class InviteView
{
    public int Id;
    public string Kind = "";
    /// <summary>The other side: who sent it (incoming) or who it asks (outgoing; 0 = everyone).</summary>
    public long Peer;
    public string PeerName = "";
    public string Title = "", Detail = "";
    /// <summary>Local game time (<see cref="GameClock.Now"/>) at which it lapses, and how long it lasted.</summary>
    public double Until, Seconds;
    public bool Outgoing;

    public double Left => Math.Max(0, Until - GameClock.Now);
}

/// <summary>
/// One invite system for every activity (#840), at <c>World/Invites</c> on the server and every client
/// (RPCs route by path). The server keeps the <see cref="InviteBook"/>, runs each
/// <see cref="InviteKind"/>'s accept, and tells the peers what is open to them; a client shows them
/// (<c>Ui/InviteToasts</c>, the quick menu, the Activities screen) and answers by id.
/// <para>
/// Bots are never offered one: a bot joins an activity through the activity's own server method.
/// Docs: <c>docs/notes/net/invites.md</c>.
/// </para>
/// </summary>
public partial class InviteService : Node
{
    public const string NodeName = "Invites";

    /// <summary>The client's node, for the toasts and the menus.</summary>
    public static InviteService? Client { get; private set; }
    /// <summary>The server's node, for the activities that send invites.</summary>
    public static InviteService? Server { get; private set; }

    private bool _server;
    private ChatManager? _chat;
    private readonly InviteBook _book = new();
    private readonly Dictionary<string, InviteKind> _kinds = new();

    public static InviteService CreateServer(ChatManager chat) => new() { Name = NodeName, _server = true, _chat = chat };
    public static InviteService CreateClient() => new() { Name = NodeName };

    public override void _EnterTree()
    {
        if (_server) Server = this;
        else Client = this;
    }

    public override void _ExitTree()
    {
        if (Server == this) Server = null;
        if (Client == this) Client = null;
    }

    private static double Now => ClockSync.ServerNow;

    // ==== server ================================================================================

    public void Register(InviteKind kind) => _kinds[kind.Id] = kind;

    public InviteKind? KindOf(string id) => _kinds.GetValueOrDefault(id);

    private string Who(long peer) => peer == 0 ? "The server" : _chat?.NameOfPeer(peer) ?? $"#{peer}";

    /// <summary>Who is online past the handshake: nothing goes to a peer before (<c>Net/Handshake</c>).</summary>
    private long[] Peers => _chat?.OnlinePeers ?? Array.Empty<long>();

    /// <summary>
    /// Sends an invite of <paramref name="kind"/> from <paramref name="from"/> to <paramref name="to"/>
    /// (a peer, or <see cref="Invite.Everyone"/>). Returns the sender's reply; <paramref name="sent"/> is
    /// the invite, or null when refused. A mutual kind answers the other way round's invite instead.
    /// </summary>
    public string Send(string kind, long from, long to, string title, string detail, out Invite? sent, long key = 0, double? seconds = null)
    {
        sent = null;
        if (!_kinds.TryGetValue(kind, out var k)) return $"No invites for '{kind}' here.";
        if (to < 0) return "Bots are not invited: they join by themselves.";
        if (to != Invite.Everyone && !Peers.Contains(to)) return "That player is not online.";
        if (k.Mutual && to != Invite.Everyone && _book.Reverse(kind, from, to) is { } theirs)
            return Answer(from, theirs, accept: true);
        sent = _book.Add(kind, from, to, title, detail, key, Now, seconds ?? k.Seconds, out var replaced, out string? why);
        if (sent == null) return why ?? "Not sent.";
        if (replaced != null) SendGone(replaced);
        GD.Print($"[invite] #{sent.Id} {kind} {from} -> {(to == Invite.Everyone ? "everyone" : to.ToString())}: {title}");
        foreach (long peer in Peers)
            if (sent.Asks(peer)) Offer(sent, peer);
        if (from > 0) RpcId(from, MethodName.Outgoing, sent.Id, kind, to, to == Invite.Everyone ? "everyone" : Who(to), title, (float)(sent.Expires - Now));
        return to == Invite.Everyone ? $"Invite sent to everyone online." : $"Invite sent to {Who(to)}.";
    }

    /// <summary>Server: <paramref name="peer"/> no longer needs <paramref name="kind"/>'s invites for <paramref name="key"/> (they joined by other means).</summary>
    public void Dismiss(string kind, long key, long peer)
    {
        foreach (var i in _book.To(peer).Where(i => i.Kind == kind && i.Key == key).ToList())
        {
            bool gone = _book.Answer(i, peer);
            if (gone) SendGone(i);
            else if (Connected(peer)) RpcId(peer, MethodName.Gone, i.Id);
        }
    }

    /// <summary>Server: the thing <paramref name="kind"/>'s invites for <paramref name="key"/> pointed at is gone (a lobby closed, a race started).</summary>
    public void Withdraw(string kind, long key)
    {
        foreach (var i in _book.Withdraw(kind, key)) SendGone(i);
    }

    /// <summary>Server: <paramref name="kind"/>'s invites <paramref name="peer"/> sent or was asked by are void (they are busy now).</summary>
    public void WithdrawInvolving(string kind, long peer)
    {
        foreach (var i in _book.WithdrawInvolving(kind, peer)) SendGone(i);
    }

    /// <summary>Server: the newest open invite of <paramref name="kind"/> asking <paramref name="to"/>, from <paramref name="from"/> (0: anyone).</summary>
    public Invite? Find(long to, string kind, long from = 0) =>
        _book.To(to).FirstOrDefault(i => i.Kind == kind && (from == 0 || i.From == from));

    /// <summary>Server: every open invite of <paramref name="kind"/> asking <paramref name="to"/>, newest first.</summary>
    public List<Invite> FindAll(long to, string kind) => _book.To(to).Where(i => i.Kind == kind).ToList();

    /// <summary>Server: accepts or declines <paramref name="invite"/> for <paramref name="peer"/>. Returns their reply.</summary>
    public string Answer(long peer, Invite invite, bool accept)
    {
        if (!invite.Asks(peer)) return "That invite is not for you.";
        if (!_kinds.TryGetValue(invite.Kind, out var k)) return "That invite is no longer valid.";
        bool gone = _book.Answer(invite, peer);
        if (gone) SendGone(invite);
        else if (Connected(peer)) RpcId(peer, MethodName.Gone, invite.Id);
        GD.Print($"[invite] #{invite.Id} {invite.Kind} {(accept ? "accepted" : "declined")} by {peer}");
        if (!accept)
        {
            if (!invite.ToEveryone && invite.From > 0) _chat?.Tell(invite.From, $"[{k.Label}] {Who(peer)} declines your invite.", ChatKind.System);
            k.Declined?.Invoke(peer, invite);
            return $"[{k.Label}] Invite declined.";
        }
        if (!invite.ToEveryone && invite.From > 0 && k.TellAccepted) _chat?.Tell(invite.From, $"[{k.Label}] {Who(peer)} accepts your invite.", ChatKind.System);
        return k.Accept(peer, invite);
    }

    /// <summary><c>/invites</c>, <c>/accept [player|kind|#id]</c>, <c>/decline [...]</c>: the reply.</summary>
    public string Command(long sender, string verb, string rest)
    {
        if (verb == "invites")
        {
            var to = _book.To(sender).ToList();
            var from = _book.From(sender).ToList();
            if (to.Count == 0 && from.Count == 0) return "No invites open. /tpa, /party invite, /br invite, /race duel and /fight send one.";
            var lines = to.Select(i => $"#{i.Id} {i.Title}{(i.Detail.Length > 0 ? $" ({i.Detail})" : "")}, {i.Expires - Now:0} s — /accept {i.Kind} or /decline {i.Kind}")
                .Concat(from.Select(i => $"#{i.Id} you → {(i.ToEveryone ? "everyone" : Who(i.To))}: {i.Title}, {i.Expires - Now:0} s"));
            return "Invites:\n" + string.Join("\n", lines);
        }
        var invite = _book.Resolve(sender, rest, name => _chat?.PeerByName(name) ?? -1);
        if (invite == null) return rest.Trim().Length == 0 ? "No invite waiting for you." : $"No invite matching '{rest.Trim()}'. /invites lists them.";
        return Answer(sender, invite, accept: verb == "accept");
    }

    /// <summary>Server: what <paramref name="peer"/> is asked, sent to them when they arrive (invites to everyone, like an open lobby).</summary>
    public void SendTo(long peer)
    {
        foreach (var i in _book.To(peer)) Offer(i, peer);
    }

    /// <summary>Server: a peer left; their invites go, both ways.</summary>
    public void PeerLeft(long peer)
    {
        foreach (var i in _book.PeerLeft(peer))
        {
            SendGone(i);
            if (_kinds.TryGetValue(i.Kind, out var k)) k.Lapsed?.Invoke(i);
        }
    }

    public override void _Process(double delta)
    {
        if (!_server || _book.Count == 0) return;
        _expireIn -= delta;
        if (_expireIn > 0) return;
        _expireIn = 0.5;
        foreach (var i in _book.Expire(Now))
        {
            SendGone(i);
            if (!_kinds.TryGetValue(i.Kind, out var k)) continue;
            if (!i.ToEveryone && i.From > 0) _chat?.Tell(i.From, $"[{k.Label}] {Who(i.To)} did not answer your invite.", ChatKind.System);
            k.Lapsed?.Invoke(i);
        }
    }

    private double _expireIn;

    private bool Connected(long peer) => peer > 0 && Peers.Contains(peer);

    private void Offer(Invite i, long peer) =>
        RpcId(peer, MethodName.Offered, i.Id, i.Kind, i.From, Who(i.From), i.Title, i.Detail, (float)(i.Expires - Now));

    /// <summary>The invite is gone: its sender's outgoing row and every recipient's toast.</summary>
    private void SendGone(Invite i)
    {
        foreach (long peer in Peers)
            if (peer == i.From || i.To == peer || i.ToEveryone)
                RpcId(peer, MethodName.Gone, i.Id);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestAnswer(int id, bool accept)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        string reply = _book.Find(id) is { } i ? Answer(sender, i, accept) : "That invite has lapsed.";
        _chat?.Tell(sender, reply, ChatKind.Private);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestWithdraw(int id)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (_book.Find(id) is not { } i || i.From != sender) return;
        _book.Withdraw(id);
        SendGone(i);
        if (_kinds.TryGetValue(i.Kind, out var k)) k.Lapsed?.Invoke(i);
        _chat?.Tell(sender, "Invite withdrawn.", ChatKind.Private);
    }

    // ==== client ================================================================================

    private readonly List<InviteView> _views = new();

    /// <summary>Invites to the local player, newest first.</summary>
    public IEnumerable<InviteView> Incoming => _views.Where(v => !v.Outgoing && v.Left > 0).OrderByDescending(v => v.Id);
    /// <summary>Invites the local player sent and are still open, newest first.</summary>
    public IEnumerable<InviteView> Sent => _views.Where(v => v.Outgoing && v.Left > 0).OrderByDescending(v => v.Id);
    public InviteView? Newest => Incoming.FirstOrDefault();
    public int IncomingCount => Incoming.Count();

    /// <summary>Something was offered, answered, withdrawn or lapsed.</summary>
    public event Action? Changed;
    /// <summary>A new invite to the local player (for the chime and the toast).</summary>
    public event Action<InviteView>? Arrived;

    /// <summary>An open invite of <paramref name="kind"/> from <paramref name="peer"/> to the local player.</summary>
    public InviteView? From(long peer, string kind) => Incoming.FirstOrDefault(v => v.Peer == peer && v.Kind == kind);

    private bool Online => NetLink.Online(this);

    public void Accept(int id) => Answer(id, true);
    public void Decline(int id) => Answer(id, false);

    private void Answer(int id, bool accept)
    {
        if (!Online) return;
        RpcId(1, MethodName.RequestAnswer, id, accept);
        // shown gone at once; the server's Gone confirms it
        if (_views.RemoveAll(v => v.Id == id && !v.Outgoing) > 0) Changed?.Invoke();
    }

    public void Withdraw(int id)
    {
        if (Online) RpcId(1, MethodName.RequestWithdraw, id);
    }

    private double _sweepIn;

    public override void _PhysicsProcess(double delta)
    {
        if (_server || _views.Count == 0) return;
        _sweepIn -= delta;
        if (_sweepIn > 0) return;
        _sweepIn = 0.5;
        if (_views.RemoveAll(v => v.Left <= 0) > 0) Changed?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Offered(int id, string kind, long from, string fromName, string title, string detail, float seconds)
    {
        _views.RemoveAll(v => v.Id == id);
        var view = new InviteView
        {
            Id = id, Kind = kind, Peer = from, PeerName = fromName, Title = title, Detail = detail,
            Until = GameClock.Now + seconds, Seconds = seconds,
        };
        _views.Add(view);
        GD.Print($"[invite] offered #{id} {kind} from {fromName}: {title}");
        Arrived?.Invoke(view);
        Changed?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Outgoing(int id, string kind, long to, string toName, string title, float seconds)
    {
        _views.RemoveAll(v => v.Id == id);
        _views.Add(new InviteView
        {
            Id = id, Kind = kind, Peer = to, PeerName = toName, Title = title, Until = GameClock.Now + seconds, Seconds = seconds, Outgoing = true,
        });
        Changed?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Gone(int id)
    {
        if (_views.RemoveAll(v => v.Id == id) > 0) Changed?.Invoke();
    }
}
