using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Combat;

/// <summary>
/// Fist fights between two players (#495), Tekken / Mortal Kombat style. <c>World/Fight</c> on the
/// server and every client, like <c>World/Race</c>, so the RPCs route by path.
/// <list type="bullet">
/// <item>Challenge: E aimed at another player on foot, or <c>/fight &lt;player&gt;</c>. The other
/// accepts with E aimed back at the challenger, or <c>/fight accept</c>; it lapses after
/// <see cref="FightRules.ChallengeSeconds"/>.</item>
/// <item>The server owns each match (<see cref="FightMatch"/>: HP, rounds, the timer). An attacker's
/// client reports a strike when its move's active frames reach its view of the opponent; the server
/// takes it only inside the match's rules (round live, move rate), within reach of its own copies of
/// the two bodies, and decides block / whiff from the victim's replicated <c>FightPose</c>.</item>
/// <item>Each fighter's client moves its own body (<c>FootPlayer.Fight.cs</c>) on the arena the
/// server picked: the line through the two players' midpoint.</item>
/// </list>
/// Fight HP is the match's own: real health, armour and items are untouched, and nobody dies.
/// </summary>
public partial class FightManager : Node
{
    public const string NodeName = "Fight";

    /// <summary>The client's node, for the player, the HUD and the prompts.</summary>
    public static FightManager? Client { get; private set; }

    private bool _server;
    private ChatManager? _chat;
    private Node3D? _players;
    private double _clock;

    // ---- server --------------------------------------------------------------------------

    private sealed class Match
    {
        public int Id;
        public FightMatch Rules = null!;
        public GlobalPos Centre;
        public float Yaw;
        public FightPhase SentPhase = (FightPhase)255;
        public int SentRound;
        /// <summary>The result has gone to both fighters' chat.</summary>
        public bool Told;
    }

    /// <summary>Open challenges: challenger to (challenged, deadline).</summary>
    private readonly Dictionary<long, (long Target, double Until)> _challenges = new();
    private readonly Dictionary<int, Match> _matches = new();
    private readonly Dictionary<long, int> _fightOf = new();
    private readonly List<long> _scratch = new();
    private readonly List<int> _ended = new();
    private int _nextId = 1;

    public static FightManager CreateServer(ChatManager chat, Node3D players) =>
        new() { Name = NodeName, _server = true, _chat = chat, _players = players };

    public static FightManager CreateClient() => new() { Name = NodeName };

    public override void _EnterTree()
    {
        if (!_server) Client = this;
    }

    public override void _ExitTree()
    {
        if (Client == this) Client = null;
    }

    public override void _Process(double delta)
    {
        if (_server) ServerTick(delta);
        else ClientTick();
    }

    /// <summary>The chat command: <c>/fight &lt;player&gt; | accept [player] | decline | leave</c>.</summary>
    public string Command(long sender, string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        switch (verb)
        {
            case "":
                return _fightOf.ContainsKey(sender) ? "[fight] You are in a fight: /fight leave forfeits it."
                    : "[fight] /fight <player> challenges someone near you; /fight accept takes a challenge.";
            case "accept":
                return Accept(sender, parts.Length > 1 ? _chat?.PeerByName(parts[1]) ?? -1 : 0);
            case "decline":
                return Decline(sender);
            case "leave":
                if (!_fightOf.TryGetValue(sender, out int id) || !_matches.TryGetValue(id, out var m)) return "[fight] You are not in a fight.";
                m.Rules.Forfeit(sender, _clock);
                SendState(m);
                return "[fight] You forfeit the fight.";
            default:
                long peer = _chat?.PeerByName(rest.Trim()) ?? -1;
                return peer <= 0 ? $"[fight] No player called '{rest.Trim()}'." : Challenge(sender, peer);
        }
    }

    private string Challenge(long from, long to)
    {
        if (from == to) return "[fight] You cannot fight yourself.";
        if (_fightOf.ContainsKey(from)) return "[fight] You are already in a fight.";
        if (_fightOf.ContainsKey(to)) return $"[fight] {Who(to)} is already in a fight.";
        if (Refusal(from, to) is { } why) return why;
        // they challenged us first: that is an answer, not a second challenge
        if (_challenges.TryGetValue(to, out var theirs) && theirs.Target == from) return Accept(from, to);
        _challenges[from] = (to, _clock + FightRules.ChallengeSeconds);
        RpcId(to, MethodName.Challenged, from, Who(from), FightRules.ChallengeSeconds);
        _chat?.Tell(to, $"[fight] {Who(from)} challenges you to a fight! Look at them and press interact, or /fight accept, within {FightRules.ChallengeSeconds:0} s.", ChatKind.System);
        GD.Print($"[fight] {from} challenges {to}");
        return $"[fight] You challenge {Who(to)}. Waiting for an answer...";
    }

    private string Accept(long sender, long challenger)
    {
        if (_fightOf.ContainsKey(sender)) return "[fight] You are already in a fight.";
        if (challenger <= 0)
        {
            // the newest challenge to the sender
            double best = double.NegativeInfinity;
            foreach (var (from, c) in _challenges)
                if (c.Target == sender && c.Until > best) { best = c.Until; challenger = from; }
        }
        if (challenger <= 0 || !_challenges.TryGetValue(challenger, out var ch) || ch.Target != sender)
            return "[fight] Nobody has challenged you.";
        _challenges.Remove(challenger);
        RpcId(sender, MethodName.ChallengeOver, challenger);
        if (_fightOf.ContainsKey(challenger)) return $"[fight] {Who(challenger)} is already in a fight.";
        if (Refusal(challenger, sender) is { } why) return why;
        Begin(challenger, sender);
        return $"[fight] You accept {Who(challenger)}'s challenge.";
    }

    private string Decline(long sender)
    {
        bool any = false;
        _scratch.Clear();
        foreach (var (from, c) in _challenges)
            if (c.Target == sender) _scratch.Add(from);
        foreach (long from in _scratch)
        {
            _challenges.Remove(from);
            RpcId(sender, MethodName.ChallengeOver, from);
            _chat?.Tell(from, $"[fight] {Who(sender)} declines your challenge.", ChatKind.System);
            any = true;
        }
        return any ? "[fight] Challenge declined." : "[fight] Nobody has challenged you.";
    }

    /// <summary>Why these two cannot fight now, or null: both on foot, up, and close.</summary>
    private string? Refusal(long a, long b)
    {
        var pa = Body(a);
        var pb = Body(b);
        if (pa == null || pb == null) return "[fight] That player is not here.";
        if (pa.Ride != RideKind.OnFoot || pb.Ride != RideKind.OnFoot) return "[fight] Both fighters must be on foot.";
        if (pa.Down != 0 || pb.Down != 0) return "[fight] Not while someone is down.";
        if (pa.Global.HorizontalDistanceTo(pb.Global) > FightRules.ChallengeRange)
            return $"[fight] {Who(b)} is too far away: within {FightRules.ChallengeRange:0} m.";
        return null;
    }

    private void Begin(long a, long b)
    {
        var ga = Body(a)!.Global;
        var gb = Body(b)!.Global;
        double de = gb.E - ga.E, dn = gb.N - ga.N;
        // world yaw of the line from A to B (Godot: -Z is north, +X east)
        float yaw = de * de + dn * dn < 0.01 ? Body(a)!.NetYaw : Mathf.Atan2(-(float)de, (float)dn);
        var match = new Match
        {
            Id = _nextId++,
            Rules = new FightMatch(a, b, _clock),
            Centre = new GlobalPos((ga.E + gb.E) * 0.5, (ga.N + gb.N) * 0.5, (ga.Alt + gb.Alt) * 0.5),
            Yaw = yaw,
        };
        _matches[match.Id] = match;
        _fightOf[a] = _fightOf[b] = match.Id;
        // a challenge either of them still had out is void now
        _challenges.Remove(a);
        _challenges.Remove(b);
        foreach (long peer in new[] { a, b })
            RpcId(peer, MethodName.Begin, match.Id, a, b, Who(a), Who(b), match.Centre.E, match.Centre.N, match.Centre.Alt, yaw);
        SendState(match);
        GD.Print($"[fight] #{match.Id} {a} vs {b}");
    }

    private void ServerTick(double delta)
    {
        _clock += delta;
        _scratch.Clear();
        foreach (var (from, c) in _challenges)
            if (_clock > c.Until) _scratch.Add(from);
        foreach (long from in _scratch)
        {
            long to = _challenges[from].Target;
            _challenges.Remove(from);
            RpcId(to, MethodName.ChallengeOver, from);
            _chat?.Tell(from, $"[fight] {Who(to)} did not answer your challenge.", ChatKind.System);
        }

        _ended.Clear();
        foreach (var m in _matches.Values)
        {
            // a fighter teleported off or got into something: forfeited
            if (m.Rules.Phase != FightPhase.Done)
                foreach (long who in new[] { m.Rules.A, m.Rules.B })
                    if (Body(who) is not { } body || body.Ride != RideKind.OnFoot
                        || body.Global.HorizontalDistanceTo(m.Centre) > FightRules.HalfLength * 3)
                    {
                        GD.Print($"[fight] #{m.Id} {who} left the arena: forfeit");
                        m.Rules.Forfeit(who, _clock);
                        break;
                    }
            if (m.Rules.Tick(_clock) || m.Rules.Phase != m.SentPhase || m.Rules.Round != m.SentRound) SendState(m);
            if (m.Rules.Expired(_clock)) _ended.Add(m.Id);
        }
        foreach (int id in _ended) EndMatch(id);
    }

    private void EndMatch(int id)
    {
        if (!_matches.Remove(id, out var m)) return;
        foreach (long who in new[] { m.Rules.A, m.Rules.B })
        {
            _fightOf.Remove(who);
            if (Connected(who)) RpcId(who, MethodName.Ended, id);
        }
        GD.Print($"[fight] #{id} over: {m.Rules.LastEnd}, winner {m.Rules.Winner}");
    }

    private void SendState(Match m)
    {
        m.SentPhase = m.Rules.Phase;
        m.SentRound = m.Rules.Round;
        var r = m.Rules;
        foreach (long who in new[] { r.A, r.B })
            if (Connected(who))
                RpcId(who, MethodName.State, m.Id, r.HpA, r.HpB, r.WinsA, r.WinsB, r.Round, (int)r.Phase, (int)r.LastEnd, r.Winner, r.TimeLeft(_clock));
        if (r.Phase == FightPhase.Done && !m.Told)
        {
            m.Told = true;
            _chat?.Tell(r.A, Summary(r), ChatKind.System);
            _chat?.Tell(r.B, Summary(r), ChatKind.System);
        }
    }

    private string Summary(FightMatch r) => r.Winner == 0 ? "[fight] A draw."
        : $"[fight] {Who(r.Winner)} wins {Math.Max(r.WinsA, r.WinsB)}-{Math.Min(r.WinsA, r.WinsB)}{(r.LastEnd == FightEnd.Fatality ? " — FATALITY" : r.LastEnd == FightEnd.Forfeit ? " (forfeit)" : "")}.";

    /// <summary>Server: a peer left; its fight is forfeited and its challenges dropped.</summary>
    public void PeerLeft(long id)
    {
        if (!_server) return;
        _challenges.Remove(id);
        _scratch.Clear();
        foreach (var (from, c) in _challenges)
            if (c.Target == id) _scratch.Add(from);
        foreach (long from in _scratch) _challenges.Remove(from);
        if (_fightOf.TryGetValue(id, out int fid) && _matches.TryGetValue(fid, out var m))
        {
            m.Rules.Forfeit(id, _clock);
            SendState(m);
        }
    }

    private FootPlayer? Body(long peer) => _players?.GetNodeOrNull<FootPlayer>(PlayerReplication.NodeName(peer));

    private bool Connected(long peer) => Multiplayer.GetPeers().Contains((int)peer);

    private string Who(long peer) => _chat?.NameOfPeer(peer) ?? $"#{peer}";

    // ---- RPCs: client to server ----------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestChallenge(long target)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        _chat?.Tell(sender, Challenge(sender, target), ChatKind.Private);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestAccept(long challenger)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        _chat?.Tell(sender, Accept(sender, challenger), ChatKind.Private);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Strike(int fightId, int move)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (!_matches.TryGetValue(fightId, out var m) || !m.Rules.Has(sender)) return;
        long victim = m.Rules.Other(sender);
        if (Body(sender) is not { } attacker || Body(victim) is not { } body) return;
        var fm = (FightMove)Math.Clamp(move, 0, 255);
        float distance = (float)attacker.Global.HorizontalDistanceTo(body.Global);
        if (!FightRules.InReach(fm, distance))
        {
            GD.Print($"[fight] #{fightId} refused {sender}'s {fm}: {distance:0.0} m away");
            return;
        }
        var outcome = m.Rules.Strike(sender, fm, (byte)Math.Clamp(body.FightPose, 0, 255), _clock);
        if (outcome == StrikeOutcome.Refused)
        {
            GD.Print($"[fight] #{fightId} refused {sender}'s {fm} ({m.Rules.Phase})");
            return;
        }
        foreach (long who in new[] { m.Rules.A, m.Rules.B })
            if (Connected(who)) RpcId(who, MethodName.Struck, fightId, sender, move, (int)outcome);
        SendState(m);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestLeave()
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        _chat?.Tell(sender, Command(sender, "leave"), ChatKind.Private);
    }

    // ---- client --------------------------------------------------------------------------

    /// <summary>The fight the local player is in, as the server last said.</summary>
    public sealed class View
    {
        public int Id;
        public long A, B;
        public string NameA = "", NameB = "";
        public GlobalPos Centre;
        /// <summary>World yaw of the line from A to B.</summary>
        public float Yaw;
        public int HpA = FightRules.MaxHp, HpB = FightRules.MaxHp, WinsA, WinsB, Round = 1;
        public FightPhase Phase = FightPhase.Intro;
        public FightEnd End;
        public long Winner;
        public float TimeLeft = FightRules.RoundSeconds;
        /// <summary>Local game time of the last state, for the timer and the banners.</summary>
        public double StateAt, PhaseAt;

        public long Me;
        public bool IsA => Me == A;
        public long Opponent => IsA ? B : A;
        public string OpponentName => IsA ? NameB : NameA;
        public int MyHp => IsA ? HpA : HpB;
        public int TheirHp => IsA ? HpB : HpA;

        /// <summary>Seconds left on the round clock now.</summary>
        public float Clock => Phase == FightPhase.Live ? Math.Max(0f, TimeLeft - (float)(GameClock.Now - StateAt)) : TimeLeft;
    }

    public View? Current { get; private set; }

    /// <summary>Challenges to the local player: challenger to its deadline (local game time).</summary>
    public readonly Dictionary<long, double> Pending = new();

    /// <summary>Who the local player last challenged, and until when, so the prompt can say "waiting".</summary>
    public (long Target, double Until) Outgoing { get; private set; }

    /// <summary>A strike the server judged, for the local fighter's reactions and the HUD: (attacker, move, outcome).</summary>
    public event Action<long, FightMove, StrikeOutcome>? StruckEvent;

    /// <summary>The fight's phase changed: (new phase, how the last round ended).</summary>
    public event Action<FightPhase, FightEnd>? PhaseChanged;

    private bool Online => NetLink.Online(this);

    public bool InFight => Current != null;

    /// <summary>Names the server gave with challenges, for the prompts.</summary>
    private readonly Dictionary<long, string> _names = new();

    public string? NameOf(long peer) => _names.TryGetValue(peer, out var n) ? n : null;

    /// <summary>The prompt for E at <paramref name="peer"/>: accept, wait, or challenge.</summary>
    public string Prompt(long peer) =>
        ChallengedBy(peer) ? NameOf(peer) is { } n ? $"Accept {n}'s fight" : "Accept the fight"
        : Challenging(peer) ? "Challenge sent..."
        : "Challenge to a fight";

    public bool ChallengedBy(long peer) => Pending.TryGetValue(peer, out double until) && GameClock.Now < until;

    public bool Challenging(long peer) => Outgoing.Target == peer && GameClock.Now < Outgoing.Until;

    /// <summary>E at another player: challenge it, or accept its challenge.</summary>
    public void Engage(long peer)
    {
        if (!Online || Current != null) return;
        if (ChallengedBy(peer)) RpcId(1, MethodName.RequestAccept, peer);
        else
        {
            Outgoing = (peer, GameClock.Now + FightRules.ChallengeSeconds);
            RpcId(1, MethodName.RequestChallenge, peer);
        }
    }

    public void SendStrike(FightMove move)
    {
        if (Online && Current is { } v) RpcId(1, MethodName.Strike, v.Id, (int)move);
    }

    public void Leave()
    {
        if (Online && Current != null) RpcId(1, MethodName.RequestLeave);
    }

    private void ClientTick()
    {
        if (Pending.Count == 0) return;
        _scratch.Clear();
        foreach (var (peer, until) in Pending)
            if (GameClock.Now >= until) _scratch.Add(peer);
        foreach (long peer in _scratch) Pending.Remove(peer);
    }

    // ---- RPCs: server to client ----------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Challenged(long from, string name, float seconds)
    {
        Pending[from] = GameClock.Now + seconds;
        _names[from] = name;
        GD.Print($"[fight] challenged by {name} ({from})");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ChallengeOver(long from) => Pending.Remove(from);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Begin(int id, long a, long b, string nameA, string nameB, double e, double n, double alt, float yaw)
    {
        Pending.Clear();
        Outgoing = default;
        Current = new View
        {
            Id = id, A = a, B = b, NameA = nameA, NameB = nameB, Centre = new GlobalPos(e, n, alt), Yaw = yaw,
            Me = Multiplayer.GetUniqueId(), StateAt = GameClock.Now, PhaseAt = GameClock.Now,
        };
        GD.Print($"[fight] #{id} begins: {nameA} vs {nameB}");
        PhaseChanged?.Invoke(FightPhase.Intro, FightEnd.None);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void State(int id, int hpA, int hpB, int winsA, int winsB, int round, int phase, int end, long winner, float timeLeft)
    {
        if (Current is not { } v || v.Id != id) return;
        bool changed = v.Phase != (FightPhase)phase || v.Round != round;
        v.HpA = hpA; v.HpB = hpB; v.WinsA = winsA; v.WinsB = winsB; v.Round = round;
        v.Phase = (FightPhase)phase; v.End = (FightEnd)end; v.Winner = winner; v.TimeLeft = timeLeft;
        v.StateAt = GameClock.Now;
        if (!changed) return;
        v.PhaseAt = GameClock.Now;
        GD.Print($"[fight] #{id} round {round} {v.Phase} {v.End} hp {hpA}/{hpB} wins {winsA}-{winsB}");
        PhaseChanged?.Invoke(v.Phase, v.End);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Struck(int id, long attacker, int move, int outcome)
    {
        if (Current is not { } v || v.Id != id) return;
        StruckEvent?.Invoke(attacker, (FightMove)move, (StrikeOutcome)outcome);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Ended(int id)
    {
        if (Current is not { } v || v.Id != id) return;
        Current = null;
        GD.Print($"[fight] #{id} ended");
        PhaseChanged?.Invoke(FightPhase.Done, FightEnd.None);
    }
}
