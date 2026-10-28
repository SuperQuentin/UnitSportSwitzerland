using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Combat;

/// <summary>
/// Aura battles between two players (#907): who looks coolest, the real 2026 street trend. Each
/// plans a combo of moves in secret, then performs it with one key, <c>aura_next</c>, cutting from
/// move to move on the downbeats. <c>World/Aura</c> on the server and every client, like
/// <c>World/Fight</c>, so the RPCs route by path.
/// <list type="bullet">
/// <item>Challenge: <c>/aura &lt;player&gt;</c> or Activities: an invite of kind <c>aura</c> (#840,
/// <see cref="InviteService"/>); two challenges crossing are an accept.</item>
/// <item>The server owns each match (<see cref="AuraMatch"/>): the plan arrives once, is checked
/// (<see cref="AuraRules.Accept"/>), and only then goes to the rival with the bar it starts on.
/// Every Next press is graded here against the server's own downbeats: the press's time is the
/// client's <see cref="ClockSync.ServerNow"/>, taken only while it agrees with its arrival less half
/// the round trip, so a lagging link is not punished and a forged early stamp gains little.</item>
/// <item>The crowd: players within <see cref="AuraRules.CrowdRadius"/> who cheer, clap or fist pump
/// (the emote wheel's replicated <c>DanceId</c>) during a turn add a capped bonus to it.</item>
/// <item>Each battler's client stands its own body on its mark (<c>FootPlayer.Aura.cs</c>) and
/// replicates <c>AuraPose</c>, which every peer draws.</item>
/// </list>
/// </summary>
public partial class AuraManager : Node
{
    public const string NodeName = "Aura";

    /// <summary>The invite kind of a challenge (#840).</summary>
    public const string InviteId = "aura";

    /// <summary>A press stamp further than this from its arrival less half the round trip is not believed.</summary>
    public const double StampSlack = 0.12;
    /// <summary>A press stamped this far after it arrived is forged.</summary>
    public const double FutureSlack = 0.05;

    /// <summary>The emote wheel's entries that count as cheering (Cheer, Clap, Fist pump).</summary>
    private static readonly int[] CheerEmotes = { 1, 4, 6 };

    /// <summary>The client's node, for the player, the HUD and the planner.</summary>
    public static AuraManager? Client { get; private set; }

    private bool _server;
    private ChatManager? _chat;
    private Node3D? _players;

    /// <summary>Server: the fist fights, so nobody is in a fight and a battle at once.</summary>
    public FightManager? Fights { get; set; }
    /// <summary>Server: the Schwingen bouts (#1269), so nobody is in an aura battle and a bout at once.</summary>
    public SchwingManager? Schwing { get; set; }

    // ---- server --------------------------------------------------------------------------

    private sealed class Match
    {
        public int Id;
        public AuraMatch Rules = null!;
        public GlobalPos Centre;
        public float Yaw;
        public FootPlayer? BodyA, BodyB;
        public AuraPhase SentPhase = (AuraPhase)255;
        public int SentTurn = -1;
        public int SentHistory;
        public readonly HashSet<long> Cheered = new();
        public double CrowdScanAt;
        public bool Told;
        /// <summary>The arena's centre in the server's world space (bots' marks, the music, spectators).</summary>
        public Vector3 CentreWorld;
        /// <summary>The CD the battle keeps the beat of (#914), or "" on the fixed 120.</summary>
        public string Music = "";
        /// <summary>Spectators (#914): who was sent the battle last, and when it was last sent.</summary>
        public readonly HashSet<long> Watchers = new();
        public double WatchAt;
        public bool WatchDirty = true;
        /// <summary>A bot battler's side (#914), null for a player.</summary>
        public BotSide? BotA, BotB;
    }

    private readonly Dictionary<int, Match> _matches = new();
    private readonly Dictionary<long, int> _auraOf = new();
    private readonly List<int> _ended = new();
    private readonly List<int> _frozen = new();
    private int _nextId = 1;

    public static AuraManager CreateServer(ChatManager chat, Node3D players)
    {
        var m = new AuraManager { Name = NodeName, _server = true, _chat = chat, _players = players };
        chat.Invites?.Register(new InviteKind
        {
            Id = InviteId, Label = "aura battle", Seconds = AuraRules.ChallengeSeconds, Mutual = true, TellAccepted = false,
            Accept = (peer, invite) => m.Accepted(peer, invite.From),
        });
        return m;
    }

    public static AuraManager CreateClient() => new() { Name = NodeName };

    public override void _EnterTree()
    {
        if (_server) return;
        Client = this;
        // a challenge is an invite (#840): E at the challenger reads them from there (#914)
        Callable.From(() =>
        {
            if (InviteService.Client is { } invites && !_hooked)
            {
                invites.Changed += OnInvitesChanged;
                _hooked = true;
            }
        }).CallDeferred();
    }

    public override void _ExitTree()
    {
        if (Client == this) Client = null;
        if (_hooked && InviteService.Client is { } invites) invites.Changed -= OnInvitesChanged;
        _hooked = false;
    }

    public override void _Process(double delta)
    {
        if (_server) ServerTick();
    }

    /// <summary>Server: whether <paramref name="peer"/> is in a battle.</summary>
    public bool InMatch(long peer) => _auraOf.ContainsKey(peer);

    /// <summary>The chat command: <c>/aura &lt;player&gt; | accept [player] | decline | leave</c>.</summary>
    public string Command(long sender, string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        switch (verb)
        {
            case "":
                return _auraOf.ContainsKey(sender) ? "[aura] You are in an aura battle: /aura leave forfeits it."
                    : "[aura] /aura <player> challenges someone near you to an aura battle; /aura accept takes a challenge.";
            case "accept":
                return Accept(sender, parts.Length > 1 ? _chat?.PeerByName(parts[1]) ?? -1 : 0);
            case "decline":
                return Decline(sender);
            case "leave":
                if (!_auraOf.TryGetValue(sender, out int id) || !_matches.TryGetValue(id, out var m)) return "[aura] You are not in an aura battle.";
                m.Rules.Forfeit(sender, ClockSync.ServerNow);
                SendState(m);
                return "[aura] You walk away: the battle is forfeited.";
            default:
                string name = rest.Trim();
                long peer = _chat?.PeerByName(name) ?? -1;
                // a bot (#914) is not a chat peer: found by its name
                if (peer <= 0 && BotPeerByName(name) is { } bot) peer = bot;
                return peer == 0 || peer == -1 ? $"[aura] No player called '{name}'." : Challenge(sender, peer);
        }
    }

    private string Challenge(long from, long to)
    {
        if (from == to) return "[aura] You cannot battle yourself.";
        if (Busy(from) is { } me) return me;
        if (Busy(to) is { } them) return them.Replace("You are", $"{Who(to)} is");
        if (Refusal(from, to) is { } why) return why;
        // a bot takes any battle at once (#914): bots are never sent invites
        if (PlayerReplication.IsBotId(to))
        {
            if (PlayerReplication.IsBotId(from)) return "[aura] Bots do not battle each other.";
            Begin(from, to);
            return $"[aura] {Who(to)} accepts at once. Aura battle!";
        }
        if (_chat?.Invites is not { } invites) return "[aura] Invites are not running.";
        string reply = invites.Send(InviteId, from, to, $"{Who(from)} challenges you to an aura battle", "/aura accept, or accept the invite",
            out var sent);
        if (sent == null) return reply.StartsWith('[') ? reply : $"[aura] {reply}";
        _chat.Tell(to, $"[aura] {Who(from)} challenges you to an aura battle! /aura accept, or accept the invite, within {AuraRules.ChallengeSeconds:0} s.", ChatKind.System);
        GD.Print($"[aura] {from} challenges {to}");
        return $"[aura] You challenge {Who(to)} to an aura battle. Waiting for an answer...";
    }

    private string? Busy(long peer) =>
        _auraOf.ContainsKey(peer) ? "[aura] You are already in an aura battle."
        : Fights?.InMatch(peer) == true ? "[aura] You are in a fist fight."
        : Schwing?.InMatch(peer) == true ? "[aura] You are wrestling." : null;

    private string Accept(long sender, long challenger)
    {
        if (challenger < 0) return "[aura] No such player.";
        if (_chat?.Invites is not { } invites || invites.Find(sender, InviteId, challenger) is not { } invite)
            return "[aura] Nobody has challenged you.";
        return invites.Answer(sender, invite, accept: true);
    }

    private string Accepted(long sender, long challenger)
    {
        if (Busy(sender) is { } me) return me;
        if (Busy(challenger) is { } them) return them.Replace("You are", $"{Who(challenger)} is");
        if (Refusal(challenger, sender) is { } why) return why;
        Begin(challenger, sender);
        return $"[aura] You accept {Who(challenger)}'s challenge.";
    }

    private string Decline(long sender)
    {
        if (_chat?.Invites is not { } invites) return "[aura] Nobody has challenged you.";
        var all = invites.FindAll(sender, InviteId);
        foreach (var invite in all) invites.Answer(sender, invite, accept: false);
        return all.Count > 0 ? "[aura] Challenge declined." : "[aura] Nobody has challenged you.";
    }

    /// <summary>Why these two cannot battle now, or null: both on foot, up, and close.</summary>
    private string? Refusal(long a, long b)
    {
        var pa = Body(a);
        var pb = Body(b);
        if (pa == null || pb == null) return "[aura] That player is not here.";
        if (pa.Ride != RideKind.OnFoot || pb.Ride != RideKind.OnFoot) return "[aura] Both must be on foot.";
        if (pa.Down != 0 || pb.Down != 0) return "[aura] Not while someone is down.";
        if (pa.Global.HorizontalDistanceTo(pb.Global) > AuraRules.ChallengeRange)
            return $"[aura] {Who(b)} is too far away: within {AuraRules.ChallengeRange:0} m.";
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
            Rules = new AuraMatch(a, b, ClockSync.ServerNow),
            Centre = new GlobalPos((ga.E + gb.E) * 0.5, (ga.N + gb.N) * 0.5, (ga.Alt + gb.Alt) * 0.5),
            Yaw = yaw,
            BodyA = Body(a),
            BodyB = Body(b),
        };
        match.CentreWorld = (match.BodyA!.GlobalPosition + match.BodyB!.GlobalPosition) * 0.5f;
        // the beat of the music playing here (#914), or the fixed 120
        PickBeat(match);
        _matches[match.Id] = match;
        BotsEnter(match);
        _auraOf[a] = _auraOf[b] = match.Id;
        _chat?.Invites?.WithdrawInvolving(InviteId, a);
        _chat?.Invites?.WithdrawInvolving(InviteId, b);
        _chat?.Invites?.WithdrawInvolving(FightManager.InviteId, a);
        _chat?.Invites?.WithdrawInvolving(FightManager.InviteId, b);
        SendBegin(match, a);
        SendBegin(match, b);
        SendState(match);
        GD.Print($"[aura] #{match.Id} {a} vs {b}");
    }

    private void SendBegin(Match m, long to)
    {
        if (!Connected(to)) return;
        var r = m.Rules;
        RpcId(to, MethodName.Begin, m.Id, r.A, r.B, Who(r.A), Who(r.B), m.Centre.E, m.Centre.N, m.Centre.Alt, m.Yaw,
            r.Beat, r.Anchor, m.Music);
    }

    private void ServerTick()
    {
        double now = ClockSync.ServerNow;
        _ended.Clear();
        foreach (var m in _matches.Values)
        {
            var r = m.Rules;
            if (r.Phase != AuraPhase.Done)
            {
                if (Gone(m, r.A, ref m.BodyA)) { r.Forfeit(r.A, now); GD.Print($"[aura] #{m.Id} {r.A} left: forfeit"); }
                else if (Gone(m, r.B, ref m.BodyB)) { r.Forfeit(r.B, now); GD.Print($"[aura] #{m.Id} {r.B} left: forfeit"); }
            }
            if (r.Phase == AuraPhase.Perform && now >= m.CrowdScanAt)
            {
                m.CrowdScanAt = now + 0.2;
                ScanCrowd(m);
            }
            // the crowd is counted into the turn the moment it is scored
            if (r.Phase == AuraPhase.Perform) r.Crowd = m.Cheered.Count;
            AuraPhase before = r.Phase;
            int turnBefore = r.Turn;
            _frozen.Clear();
            bool changed = r.Tick(now, _frozen);
            foreach (int i in _frozen) SendGraded(m, turnBefore, i, AuraGrade.Miss);
            if (before == AuraPhase.Plan && r.Phase == AuraPhase.Countdown) SendPerform(m);
            if (changed || r.Phase != m.SentPhase || r.Turn != m.SentTurn) SendState(m);
            // don't laugh first (#914): the waiter is tempted; a laugh shows on both
            if (r.TemptOpened) { r.TemptOpened = false; SendTempt(m); }
            if (r.JustLaughed) { r.JustLaughed = false; SendLaughed(m); SendState(m); }
            StepBots(m, now);
            StepWatch(m, now);
            if (r.Expired(now)) _ended.Add(m.Id);
        }
        foreach (int id in _ended) EndMatch(id);
    }

    /// <summary>A battler disconnected, got into something, or walked off the arena.</summary>
    private bool Gone(Match m, long who, ref FootPlayer? body)
    {
        if (body == null || !IsInstanceValid(body)) body = Body(who);
        return body is null || body.Ride != RideKind.OnFoot || body.Global.HorizontalDistanceTo(m.Centre) > AuraRules.LeashRadius;
    }

    private void ScanCrowd(Match m)
    {
        if (GetTree() is not { } tree) return;
        foreach (var s in PlayerSnapshot.Of(tree))
        {
            var p = s.Player;
            if (p == m.BodyA || p == m.BodyB || p.Down != 0) continue;
            int emote = p.DanceId - FootPlayer.EmoteDanceBase;
            if (Array.IndexOf(CheerEmotes, emote) < 0) continue;
            if (p.Global.HorizontalDistanceTo(m.Centre) > AuraRules.CrowdRadius) continue;
            if (NetIdOf(p) is { } peer) m.Cheered.Add(peer);
        }
    }

    private static long? NetIdOf(FootPlayer p) => FootPlayer.NetId(p.Name) is { } id && id != 0 ? id : null;

    private void EndMatch(int id)
    {
        if (!_matches.Remove(id, out var m)) return;
        BotsLeave(m);
        WatchEnd(m);
        foreach (long who in new[] { m.Rules.A, m.Rules.B })
        {
            _auraOf.Remove(who);
            if (Connected(who)) RpcId(who, MethodName.Ended, id);
        }
        GD.Print($"[aura] #{id} over: winner {m.Rules.Winner}, {m.Rules.TotalA}-{m.Rules.TotalB}{(m.Rules.Forfeited ? " (forfeit)" : "")}");
    }

    private void SendState(Match m)
    {
        var r = m.Rules;
        if (r.Phase == AuraPhase.Plan && m.SentTurn != r.Turn) m.Cheered.Clear();
        m.SentPhase = r.Phase;
        m.SentTurn = r.Turn;
        // a turn just scored: its verdict first, so the HUD has it when the phase says so
        while (m.SentHistory < r.History.Count) SendVerdict(m, m.SentHistory++);
        foreach (long who in Both(r))
            if (Connected(who))
                RpcId(who, MethodName.State, m.Id, r.Turn, r.Performer, (int)r.Phase, r.PhaseEnds, r.TotalA, r.TotalB, r.Winner, r.Forfeited,
                    r.LaughsA, r.LaughsB);
        m.WatchDirty = true;
        if (r.Phase == AuraPhase.Done && !m.Told)
        {
            m.Told = true;
            string line = Summary(r);
            _chat?.Tell(r.A, line, ChatKind.System);
            _chat?.Tell(r.B, line, ChatKind.System);
            GD.Print(line);
        }
    }

    private void SendPerform(Match m)
    {
        var r = m.Rules;
        var bytes = new byte[r.Plan.Length];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)r.Plan[i];
        foreach (long who in Both(r))
            if (Connected(who)) RpcId(who, MethodName.Perform, m.Id, r.Turn, r.Performer, r.T0, bytes);
        GD.Print($"[aura] #{m.Id} turn {r.Turn}: {Who(r.Performer)} performs {string.Join(", ", r.Plan)} from {r.T0:F2}");
    }

    private void SendGraded(Match m, int turn, int index, AuraGrade grade)
    {
        foreach (long who in Both(m.Rules))
            if (Connected(who)) RpcId(who, MethodName.Graded, m.Id, turn, index, (int)grade);
        m.WatchDirty = true;
    }

    /// <summary>A Perfect cut tempts the waiter (#914): only they are told, with when it began.</summary>
    private void SendTempt(Match m)
    {
        var r = m.Rules;
        if (Connected(r.Waiter)) RpcId(r.Waiter, MethodName.Tempt, m.Id, r.Turn, r.TemptAt);
    }

    /// <summary>The waiter cracked up (#914): both battlers and the spectators see it.</summary>
    private void SendLaughed(Match m)
    {
        var r = m.Rules;
        foreach (long who in Both(r))
            if (Connected(who)) RpcId(who, MethodName.Laughed, m.Id, r.Turn, r.Waiter);
        BotLaughed(m, r.Waiter);
        GD.Print($"[aura] #{m.Id} turn {r.Turn}: {Who(r.Waiter)} laughed first");
    }

    private void SendVerdict(Match m, int turn)
    {
        var t = m.Rules.History[turn];
        var moves = new byte[t.Moves.Length];
        var grades = new byte[t.Grades.Length];
        for (int i = 0; i < moves.Length; i++) moves[i] = (byte)t.Moves[i];
        for (int i = 0; i < grades.Length; i++) grades[i] = (byte)t.Grades[i];
        var parts = t.Score.ToParts();
        foreach (long who in Both(m.Rules))
            if (Connected(who)) RpcId(who, MethodName.Verdict, m.Id, turn, t.Performer, moves, grades, parts);
        GD.Print($"[aura] #{m.Id} turn {turn}: {Who(t.Performer)} scores {t.Score.Total} ({string.Join(" ", t.Grades)})");
    }

    private string Summary(AuraMatch r)
    {
        if (r.Winner == 0) return $"[aura] A draw, {r.TotalA}-{r.TotalB}.";
        string best = r.BestOf(r.Winner) is { } t ? $" Best combo: {Combo(t.Moves)} ({t.Score.Total})." : "";
        return $"[aura] {Who(r.Winner)} wins the aura battle {Math.Max(r.TotalA, r.TotalB)}-{Math.Min(r.TotalA, r.TotalB)}{(r.Forfeited ? " (forfeit)" : "")}.{best}";
    }

    public static string Combo(AuraMove[] moves)
    {
        var names = new string[moves.Length];
        for (int i = 0; i < moves.Length; i++) names[i] = AuraRules.Name(moves[i]);
        return string.Join(" > ", names);
    }

    /// <summary>Server: a peer left; its battle is forfeited.</summary>
    public void PeerLeft(long id)
    {
        if (!_server) return;
        if (_auraOf.TryGetValue(id, out int mid) && _matches.TryGetValue(mid, out var m))
        {
            m.Rules.Forfeit(id, ClockSync.ServerNow);
            SendState(m);
        }
    }

    /// <summary>
    /// Server: <paramref name="peer"/> locks in its plan for <paramref name="turn"/>. What the RPC
    /// calls, and a bot directly. False (logged) when refused.
    /// </summary>
    public bool SubmitPlanFor(long peer, int id, int turn, byte[] moves)
    {
        if (!_matches.TryGetValue(id, out var m) || !m.Rules.Has(peer)) return false;
        AuraMove[]? plan = null;
        if (moves != null && moves.Length <= AuraRules.MaxMoves)
        {
            plan = new AuraMove[moves.Length];
            for (int i = 0; i < moves.Length; i++) plan[i] = (AuraMove)moves[i];
        }
        if (plan == null || !m.Rules.SubmitPlan(peer, turn, plan, ClockSync.ServerNow))
        {
            GD.Print($"[aura] #{id} refused {peer}'s plan of {moves?.Length ?? 0} for turn {turn} ({m.Rules.Phase}, turn {m.Rules.Turn})");
            return false;
        }
        SendPerform(m);
        SendState(m);
        return true;
    }

    /// <summary>
    /// Server: <paramref name="peer"/> pressed Next for move <paramref name="index"/>, stamped
    /// <paramref name="ts"/> on its synced clock, arriving now over a link of <paramref name="rtt"/>.
    /// </summary>
    public AuraGrade? NextFor(long peer, int id, int turn, int index, double ts, double rtt)
    {
        if (!_matches.TryGetValue(id, out var m) || !m.Rules.Has(peer)) return null;
        double arrival = ClockSync.ServerNow;
        if (ts > arrival + FutureSlack)
        {
            GD.Print($"[aura] #{id} refused {peer}'s press stamped {ts - arrival:F2} s ahead");
            return null;
        }
        double heard = arrival - Math.Max(0, rtt) * 0.5;
        if (Math.Abs(ts - heard) > StampSlack) ts = heard;
        var grade = m.Rules.Next(peer, turn, index, ts);
        if (grade == null)
        {
            GD.Print($"[aura] #{id} refused {peer}'s press {index} for turn {turn} ({m.Rules.Phase}, next {m.Rules.NextIndex})");
            return null;
        }
        SendGraded(m, turn, index, grade.Value);
        return grade;
    }

    /// <summary>
    /// Server: the waiting <paramref name="peer"/> keeps a straight face (#914), stamped like a Next
    /// press. True: it met the tempt; false: nothing tempted them, so they broke character.
    /// </summary>
    public bool? ComposeFor(long peer, int id, int turn, double ts, double rtt)
    {
        if (!_matches.TryGetValue(id, out var m) || !m.Rules.Has(peer)) return null;
        double arrival = ClockSync.ServerNow;
        if (ts > arrival + FutureSlack)
        {
            GD.Print($"[aura] #{id} refused {peer}'s composure stamped {ts - arrival:F2} s ahead");
            return null;
        }
        double heard = arrival - Math.Max(0, rtt) * 0.5;
        if (Math.Abs(ts - heard) > StampSlack) ts = heard;
        var held = m.Rules.Compose(peer, turn, ts, arrival);
        if (held == null) GD.Print($"[aura] #{id} refused {peer}'s composure for turn {turn} ({m.Rules.Phase})");
        return held;
    }

    private static IEnumerable<long> Both(AuraMatch r)
    {
        yield return r.A;
        yield return r.B;
    }

    private FootPlayer? Body(long peer) => _players?.GetNodeOrNull<FootPlayer>(PlayerReplication.NodeName(peer));

    private bool Connected(long peer) => Multiplayer.GetPeers().Contains((int)peer);

    private string Who(long peer) =>
        PlayerReplication.IsBotId(peer) ? Bots.BotDirector.Instance?.Get(peer)?.Profile.Name ?? "Bot"
        : _chat?.NameOfPeer(peer) ?? $"#{peer}";

    private double RttOf(long peer)
    {
        if ((Multiplayer.MultiplayerPeer as ENetMultiplayerPeer)?.GetPeer((int)peer) is { } p)
            return p.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime) / 1000.0;
        return 0;
    }

    // ---- RPCs: client to server ----------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestChallenge(long target)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        _chat?.Tell(sender, Challenge(sender, target), ChatKind.Private);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SubmitPlan(int id, int turn, byte[] moves)
    {
        if (!_server) return;
        SubmitPlanFor(Multiplayer.GetRemoteSenderId(), id, turn, moves);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Next(int id, int turn, int index, double ts)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        NextFor(sender, id, turn, index, ts, RttOf(sender));
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Compose(int id, int turn, double ts)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        ComposeFor(sender, id, turn, ts, RttOf(sender));
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestLeave()
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        _chat?.Tell(sender, Command(sender, "leave"), ChatKind.Private);
    }

    // ---- client --------------------------------------------------------------------------

    /// <summary>The battle the local player is in, as the server last said.</summary>
    public sealed class View
    {
        public int Id;
        public long A, B;
        public string NameA = "", NameB = "";
        public GlobalPos Centre;
        /// <summary>World yaw of the line from A to B.</summary>
        public float Yaw;
        public int Turn;
        public long Performer;
        public AuraPhase Phase = AuraPhase.Intro;
        /// <summary>When the phase ends, on the server clock.</summary>
        public double PhaseEnds;
        public int TotalA, TotalB;
        public long Winner;
        public bool Forfeited;
        /// <summary>The battle's beat (#914): seconds per beat, a downbeat, and the CD it comes from ("" = the fixed 120).</summary>
        public double Beat = AuraRules.BeatSeconds, Anchor;
        public string Music = "";
        /// <summary>Don't laugh first (#914): when the local waiter was tempted (NaN: not now), and each side's laughs.</summary>
        public double TemptAt = double.NaN;
        public int LaughsA, LaughsB;
        /// <summary>When each battler last laughed, on the server clock, for the pose and the HUD.</summary>
        public double LaughedAtA = double.NegativeInfinity, LaughedAtB = double.NegativeInfinity;
        public bool Laughing(long who, double now) => now - (who == A ? LaughedAtA : LaughedAtB) < AuraBody.LaughSeconds;

        /// <summary>The combo being performed (from the countdown on), its first downbeat, and each press's grade so far.</summary>
        public AuraMove[] Plan = Array.Empty<AuraMove>();
        public double T0;
        public AuraGrade[] Grades = Array.Empty<AuraGrade>();
        /// <summary>The local performer's next move to cut (local count, ahead of the server's word).</summary>
        public int NextIndex;
        /// <summary>The turns scored so far, as the server sent them.</summary>
        public readonly List<AuraTurn> History = new(AuraMatch.TurnCount);

        public long Me;
        public bool IsA => Me == A;
        public long Opponent => IsA ? B : A;
        public string OpponentName => IsA ? NameB : NameA;
        public string NameOf(long peer) => peer == A ? NameA : NameB;
        public bool MyTurn => Performer == Me;
        public int MyTotal => IsA ? TotalA : TotalB;
        public int TheirTotal => IsA ? TotalB : TotalA;
        public int Round => Turn / 2 + 1;
        public float Bpm => (float)(60.0 / Beat);

        /// <summary>Seconds left in the phase now.</summary>
        public float Left => (float)Math.Max(0, PhaseEnds - ClockSync.ServerNow);

        /// <summary>The scoring context of the local player's next turn: answer the rival, don't repeat yourself.</summary>
        public AuraContext ContextForMe()
        {
            var m = new AuraMatch(A, B, 0);
            m.History.AddRange(History);
            return m.ContextFor(Me);
        }

        public AuraTurn? BestOf(long who)
        {
            AuraTurn? best = null;
            foreach (var t in History)
                if (t.Performer == who && (best == null || t.Score.Total > best.Score.Total)) best = t;
            return best;
        }
    }

    public View? Current { get; private set; }

    /// <summary>The battle's phase changed (or a new turn began).</summary>
    public event Action<AuraPhase>? PhaseChanged;

    /// <summary>A press was graded by the server: (turn, move index, grade).</summary>
    public event Action<int, int, AuraGrade>? GradedEvent;

    /// <summary>A turn's verdict arrived.</summary>
    public event Action<AuraTurn>? VerdictEvent;

    /// <summary>Don't laugh first (#914): the local waiter is tempted / a battler laughed (who).</summary>
    public event Action? TemptEvent;
    public event Action<long>? LaughEvent;

    private bool Online => NetLink.Online(this);

    public bool InBattle => Current != null;

    /// <summary>Challenge <paramref name="peer"/> (the Activities entry).</summary>
    public void Challenge(long peer)
    {
        if (Online && Current == null) RpcId(1, MethodName.RequestChallenge, peer);
    }

    /// <summary>Lock in the local player's plan for the turn being planned.</summary>
    public void SendPlan(IReadOnlyList<AuraMove> plan)
    {
        if (!Online || Current is not { } v || !v.MyTurn || v.Phase != AuraPhase.Plan) return;
        var bytes = new byte[Math.Min(plan.Count, AuraRules.MaxMoves)];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)plan[i];
        RpcId(1, MethodName.SubmitPlan, v.Id, v.Turn, bytes);
    }

    /// <summary>
    /// The local performer cuts to the next move now. Returns the grade as this side reckons it
    /// (the HUD shows it at once; the server's word follows), or null when nothing is to be cut.
    /// </summary>
    public AuraGrade? PressNext()
    {
        if (!Online || Current is not { } v || !v.MyTurn) return null;
        if (v.Phase is not (AuraPhase.Perform or AuraPhase.Countdown) || v.NextIndex >= v.Plan.Length) return null;
        double now = ClockSync.ServerNow;
        double cut = AuraRules.CutAt(v.Plan, v.NextIndex, v.T0, v.Beat);
        // a beat or more early is a double tap: not sent, as the server would ignore it
        if (now < cut - v.Beat) return null;
        var grade = AuraRules.Grade(now - cut, AuraRules.Def(v.Plan[v.NextIndex]).Difficulty);
        RpcId(1, MethodName.Next, v.Id, v.Turn, v.NextIndex, now);
        if (v.NextIndex < v.Grades.Length && v.Grades[v.NextIndex] == AuraGrade.Pending) v.Grades[v.NextIndex] = grade;
        v.NextIndex++;
        return grade;
    }

    /// <summary>
    /// The local waiter keeps a straight face (#914): pressed while the rival performs. True when a
    /// tempt is open (it is met); false when nothing tempts them, which the server takes as a laugh.
    /// </summary>
    public bool? PressCompose()
    {
        if (!Online || Current is not { } v || v.MyTurn || v.Phase != AuraPhase.Perform) return null;
        if (v.Laughing(v.Me, ClockSync.ServerNow)) return null;
        double now = ClockSync.ServerNow;
        RpcId(1, MethodName.Compose, v.Id, v.Turn, now);
        bool met = !double.IsNaN(v.TemptAt) && now <= v.TemptAt + AuraRules.TemptWindow;
        v.TemptAt = double.NaN;
        return met;
    }

    public void Leave()
    {
        if (Online && Current != null) RpcId(1, MethodName.RequestLeave);
    }

    // ---- E at a player who challenged you (#914) ----------------------------------------

    /// <summary>Aura challenges to the local player: challenger to its deadline (local game time), from the invites.</summary>
    public readonly Dictionary<long, double> Pending = new();
    private readonly Dictionary<long, string> _names = new();
    private bool _hooked;

    private void OnInvitesChanged()
    {
        Pending.Clear();
        if (InviteService.Client is not { } invites) return;
        foreach (var v in invites.Incoming)
        {
            if (v.Kind != InviteId) continue;
            Pending[v.Peer] = v.Until;
            _names[v.Peer] = v.PeerName;
        }
    }

    /// <summary>Whether <paramref name="peer"/> has an aura challenge out to the local player.</summary>
    public bool ChallengedBy(long peer) => Pending.TryGetValue(peer, out double until) && GameClock.Now < until;

    /// <summary>The prompt for E at a player who challenged the local player.</summary>
    public string Prompt(long peer) => _names.TryGetValue(peer, out var n) ? $"Accept {n}'s aura battle" : "Accept the aura battle";

    /// <summary>E at <paramref name="peer"/>: take its aura challenge. False when there is none (E stays the fight's).</summary>
    public bool AcceptFrom(long peer)
    {
        if (!Online || Current != null || !ChallengedBy(peer)) return false;
        if (InviteService.Client?.From(peer, InviteId) is not { } invite) return false;
        InviteService.Client.Accept(invite.Id);
        return true;
    }

    // ---- RPCs: server to client ----------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Begin(int id, long a, long b, string nameA, string nameB, double e, double n, double alt, float yaw,
        double beat, double anchor, string music)
    {
        Pending.Clear();
        Current = new View
        {
            Id = id, A = a, B = b, NameA = nameA, NameB = nameB, Centre = new GlobalPos(e, n, alt), Yaw = yaw,
            Me = Multiplayer.GetUniqueId(), Performer = a, Beat = beat > 0.05 ? beat : AuraRules.BeatSeconds, Anchor = anchor, Music = music,
        };
        GD.Print($"[aura] #{id} begins: {nameA} vs {nameB} at {60.0 / Current.Beat:0} BPM{(music.Length > 0 ? $" ({music})" : "")}");
        PhaseChanged?.Invoke(AuraPhase.Intro);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void State(int id, int turn, long performer, int phase, double phaseEnds, int totalA, int totalB, long winner, bool forfeited,
        int laughsA, int laughsB)
    {
        if (Current is not { } v || v.Id != id) return;
        bool changed = v.Phase != (AuraPhase)phase || v.Turn != turn;
        if (v.Turn != turn || (AuraPhase)phase == AuraPhase.Plan && v.Phase != AuraPhase.Plan)
        {
            v.Plan = Array.Empty<AuraMove>();
            v.Grades = Array.Empty<AuraGrade>();
            v.NextIndex = 0;
        }
        v.Turn = turn; v.Performer = performer; v.Phase = (AuraPhase)phase; v.PhaseEnds = phaseEnds;
        v.TotalA = totalA; v.TotalB = totalB; v.Winner = winner; v.Forfeited = forfeited;
        v.LaughsA = laughsA; v.LaughsB = laughsB;
        if (v.Phase != AuraPhase.Perform) v.TemptAt = double.NaN;
        if (!changed) return;
        GD.Print($"[aura] #{id} turn {turn} {v.Phase}: {v.NameOf(performer)}, {totalA}-{totalB}");
        PhaseChanged?.Invoke(v.Phase);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Perform(int id, int turn, long performer, double t0, byte[] moves)
    {
        if (Current is not { } v || v.Id != id) return;
        var plan = new AuraMove[moves.Length];
        for (int i = 0; i < plan.Length; i++) plan[i] = (AuraMove)moves[i];
        v.Turn = turn;
        v.Performer = performer;
        v.Plan = plan;
        v.T0 = t0;
        v.Grades = new AuraGrade[plan.Length];
        v.NextIndex = 0;
        GD.Print($"[aura] #{id} turn {turn}: {v.NameOf(performer)} performs {Combo(plan)} in {t0 - ClockSync.ServerNow:F1} s");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Graded(int id, int turn, int index, int grade)
    {
        if (Current is not { } v || v.Id != id || turn != v.Turn) return;
        if ((uint)index < (uint)v.Grades.Length) v.Grades[index] = (AuraGrade)grade;
        // the server called a move frozen before this side pressed: the local count moves on with it
        if (v.MyTurn && index >= v.NextIndex) v.NextIndex = index + 1;
        GradedEvent?.Invoke(turn, index, (AuraGrade)grade);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Verdict(int id, int turn, long performer, byte[] moves, byte[] grades, float[] parts)
    {
        if (Current is not { } v || v.Id != id) return;
        var plan = new AuraMove[moves.Length];
        for (int i = 0; i < plan.Length; i++) plan[i] = (AuraMove)moves[i];
        var g = new AuraGrade[grades.Length];
        for (int i = 0; i < g.Length; i++) g[i] = (AuraGrade)grades[i];
        var t = new AuraTurn(performer, plan, g, AuraScore.FromParts(parts));
        while (v.History.Count < turn) v.History.Add(t); // never: the verdicts come in order
        if (v.History.Count == turn) v.History.Add(t);
        GD.Print($"[aura] #{id} verdict turn {turn}: {v.NameOf(performer)} {t.Score.Total}");
        VerdictEvent?.Invoke(t);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Tempt(int id, int turn, double temptAt)
    {
        if (Current is not { } v || v.Id != id || turn != v.Turn || v.MyTurn) return;
        v.TemptAt = temptAt;
        TemptEvent?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Laughed(int id, int turn, long who)
    {
        if (Current is not { } v || v.Id != id) return;
        if (who == v.A) v.LaughedAtA = ClockSync.ServerNow;
        else v.LaughedAtB = ClockSync.ServerNow;
        if (who == v.Me) v.TemptAt = double.NaN;
        GD.Print($"[aura] #{id} turn {turn}: {v.NameOf(who)} laughed first");
        LaughEvent?.Invoke(who);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Ended(int id)
    {
        if (Current is not { } v || v.Id != id) return;
        Current = null;
        GD.Print($"[aura] #{id} ended");
        PhaseChanged?.Invoke(AuraPhase.Done);
    }
}
