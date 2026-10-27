using Godot;

namespace UnitSport.Net;

/// <summary>
/// Parties (#840): friends who play together for the session, at <c>World/Parties</c> on the server and
/// every client. The rules are <see cref="PartyBook"/>'s; joining goes through an invite
/// (<c>/party invite &lt;player&gt;</c>, kind <c>party</c>), and each member is sent the party's names.
/// A Battle Royale entrant who picked no team name plays with their party
/// (<see cref="Party.TeamName"/>). Docs: <c>docs/notes/net/invites.md</c>.
/// </summary>
public partial class Parties : Node
{
    public const string NodeName = "Parties";
    public const string Kind = "party";

    public static Parties? Server { get; private set; }
    public static Parties? Client { get; private set; }

    private bool _server;
    private ChatManager? _chat;
    private InviteService? _invites;
    private readonly PartyBook _book = new();

    public static Parties CreateServer(ChatManager chat, InviteService invites)
    {
        var p = new Parties { Name = NodeName, _server = true, _chat = chat, _invites = invites };
        invites.Register(new InviteKind
        {
            Id = Kind, Label = "party", Seconds = 60, TellAccepted = false,
            Accept = (peer, invite) => p.Accept(peer, invite.From),
        });
        return p;
    }

    public static Parties CreateClient() => new() { Name = NodeName };

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

    // ---- server -------------------------------------------------------------------------

    private string Who(long peer) => _chat?.NameOfPeer(peer) ?? $"#{peer}";

    /// <summary>Server: the party <paramref name="peer"/> is in, or null.</summary>
    public Party? Of(long peer) => _book.Of(peer);

    /// <summary><c>/party [list] | invite &lt;player&gt; | leave | kick &lt;player&gt;</c>: the reply.</summary>
    public string Command(long sender, string rest)
    {
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = words.Length > 0 ? words[0].ToLowerInvariant() : "list";
        string arg = string.Join(' ', words.Skip(1));
        switch (verb)
        {
            case "list":
                return _book.Of(sender) is { } p
                    ? $"[party] {string.Join(", ", p.Members.Select(m => m == p.Leader ? $"{Who(m)} (leader)" : Who(m)))}"
                    : "[party] You are not in a party. /party invite <player> makes one.";
            case "invite":
            {
                long peer = _chat?.PeerByName(arg) ?? -1;
                if (peer <= 0) return $"[party] No player matching '{arg}'.";
                if (_book.MayInvite(sender, peer) is { } why) return $"[party] {why}";
                int size = _book.Of(sender)?.Members.Count ?? 1;
                return "[party] " + (_invites?.Send(Kind, sender, peer, $"{Who(sender)} invites you to their party",
                    size > 1 ? $"{size} in it" : "", out _) ?? "Invites are not running.");
            }
            case "leave":
                return Leave(sender) ? "[party] You left the party." : "[party] You are not in a party.";
            case "kick":
            {
                long peer = _chat?.PeerByName(arg) ?? -1;
                if (_book.MayKick(sender, peer) is { } why) return $"[party] {why}";
                Leave(peer);
                _chat?.Tell(peer, $"[party] {Who(sender)} put you out of the party.", ChatKind.System);
                return $"[party] {Who(peer)} is out.";
            }
            default:
                return "[party] /party, /party invite <player>, /party leave, /party kick <player>";
        }
    }

    private string Accept(long peer, long host)
    {
        if (!_chat?.OnlinePeers.Contains(host) == true) return "[party] They are no longer here.";
        var party = _book.Join(host, peer, out var left, out string? why);
        if (party == null) return $"[party] {why}";
        if (left != null) Push(left);
        Push(party);
        foreach (long m in party.Members)
            if (m != peer) _chat?.Tell(m, $"[party] {Who(peer)} joins the party.", ChatKind.System);
        return $"[party] You join {Who(party.Leader)}'s party.";
    }

    /// <summary>Takes <paramref name="peer"/> out of their party and tells the rest. False if they were in none.</summary>
    private bool Leave(long peer)
    {
        if (_book.Of(peer) is not { } party) return false;
        var others = party.Members.Where(m => m != peer).ToList();
        _book.Leave(peer, out bool disbanded);
        SendState(peer, null);
        foreach (long m in others)
        {
            _chat?.Tell(m, disbanded ? "[party] The party is over." : $"[party] {Who(peer)} left the party.", ChatKind.System);
            if (disbanded) SendState(m, null);
        }
        if (!disbanded) Push(party);
        return true;
    }

    /// <summary>Server: a peer left the server, and so their party.</summary>
    public void PeerLeft(long peer) => Leave(peer);

    private void Push(Party party)
    {
        foreach (long m in party.Members) SendState(m, party);
    }

    private void SendState(long peer, Party? party)
    {
        if (!_chat?.OnlinePeers.Contains(peer) == true) return;
        RpcId(peer, MethodName.State, party?.Members.ToArray() ?? Array.Empty<long>(),
            party?.Members.Select(Who).ToArray() ?? Array.Empty<string>());
        StateSent?.Invoke(peer);
    }

    /// <summary>Server: a peer was sent its party state (it joined, left, or its party changed): shared pins follow (#1226).</summary>
    public event Action<long>? StateSent;

    // ---- client -------------------------------------------------------------------------

    /// <summary>The local player's party: peers and names, the leader first. Empty when in none.</summary>
    public IReadOnlyList<(long Peer, string Name)> Members => _members;
    private (long, string)[] _members = Array.Empty<(long, string)>();

    public bool InParty => _members.Length > 0;
    public bool IsLeader => _members.Length > 0 && _members[0].Item1 == Multiplayer.GetUniqueId();

    public event Action? Changed;

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void State(long[] peers, string[] names)
    {
        _members = peers.Zip(names, (p, n) => (p, n)).ToArray();
        GD.Print($"[party] {(_members.Length == 0 ? "none" : string.Join(", ", names))}");
        Changed?.Invoke();
    }
}
