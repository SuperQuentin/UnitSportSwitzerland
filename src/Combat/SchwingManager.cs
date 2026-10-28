using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Occasions.Festivals;
using UnitSport.Player;

namespace UnitSport.Combat;

/// <summary>
/// Schwingen, Swiss wrestling (#1269), the Älplerfest's third game. <c>World/Schwing</c> on the server and every client, like
/// <c>World/Fight</c>, so the RPCs route by path. It copies the fist fight's shell (an invite of kind <c>schwing</c>, marks on a line,
/// forfeits, a pose field the dance layer draws) but not its strike rules: the pure <see cref="SchwingMatch"/> runs the grapple.
/// <list type="bullet">
/// <item>Off a festival: <c>/schwing &lt;player or bot&gt;</c> (and <c>accept</c>, <c>decline</c>, <c>leave</c>), anywhere flat.</item>
/// <item>At the Älplerfest: E at the sawdust ring joins its queue; two in the queue wrestle, one alone for
/// <see cref="FillSeconds"/> gets a bot. A bout there pays Batzen and party points through <c>FestivalService.Award</c>.</item>
/// <item>The server decides everything: leans and throws arrive as <c>Act</c>s (budgeted, from a wrestler in its bout), it answers them
/// at the end of the telegraph and sends <c>State</c> at 10 Hz. Each client puts its own body on the mark the server picked and
/// replicates the pose the server worked out; a bot's pose is written here.</item>
/// </list>
/// </summary>
public partial class SchwingManager : Node
{
    public const string NodeName = "Schwing";
    public const string InviteId = "schwing";
    /// <summary>The festival and the game this bout belongs to when it is fought at the ring.</summary>
    public const string FestivalId = "aelplerfest", GameId = "schwingen";
    /// <summary>A queued wrestler with nobody to meet gets a bot after this long.</summary>
    public const float FillSeconds = 8f;
    /// <summary>A bout shorter than this pays nothing (a quick forfeit is not a play).</summary>
    public const double MinPaidSeconds = 20;
    /// <summary>From this festival day on, the ring's bouts are the Schlussgang.</summary>
    public const int SchlussgangDay = 9;
    public const float RingReach = 10f;

    public static SchwingManager? Client { get; private set; }

    private bool _server;
    private ChatManager? _chat;
    private Node3D? _players;
    private double _clock;
    private double _sendAt;

    /// <summary>Server: the other duels, so nobody is in two at once.</summary>
    public FightManager? Fights { get; set; }
    public AuraManager? Auras { get; set; }

    /// <summary>Server: whether <paramref name="peer"/> is wrestling.</summary>
    public bool InMatch(long peer) => _schwingOf.ContainsKey(peer);

    // ---- server --------------------------------------------------------------------------

    private sealed class Match
    {
        public int Id;
        public SchwingMatch Rules = null!;
        public GlobalPos Centre;
        public Vector3 CentreWorld;
        public float Yaw;
        public bool Festival;
        public double LiveSince = double.NaN;
        public double SentAt;
        public bool Told, Paid;
        public SchwingPhase SentPhase = (SchwingPhase)255;
        public FootPlayer? BodyA, BodyB;
        public BotSide? BotA, BotB;
    }

    private readonly Dictionary<int, Match> _matches = new();
    private readonly Dictionary<long, int> _schwingOf = new();
    private readonly List<int> _ended = new();
    private readonly List<(long Peer, double Since)> _queue = new();
    private int _nextId = 1;

    public static SchwingManager CreateServer(ChatManager chat, Node3D players)
    {
        var m = new SchwingManager { Name = NodeName, _server = true, _chat = chat, _players = players };
        chat.Invites?.Register(new InviteKind
        {
            Id = InviteId, Label = "Schwingen", Seconds = SchwingRules.ChallengeSeconds, Mutual = true, TellAccepted = false,
            Accept = (peer, invite) => m.Accepted(peer, invite.From),
        });
        return m;
    }

    public static SchwingManager CreateClient() => new() { Name = NodeName };

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
    }

    /// <summary>The chat command: <c>/schwing &lt;player or bot&gt; | accept [player] | decline | leave</c>.</summary>
    public string Command(long sender, string rest)
    {
        var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        switch (verb)
        {
            case "":
                return Busy(sender) != null ? "[schwing] You are wrestling: /schwing leave forfeits it."
                    : "[schwing] /schwing <player or bot> asks someone near you to wrestle; /schwing accept takes it.";
            case "accept":
                return Accept(sender, parts.Length > 1 ? _chat?.PeerByName(parts[1]) ?? -1 : 0);
            case "decline":
                return Decline(sender);
            case "leave":
                return Leave(sender);
            default:
                string name = rest.Trim();
                long peer = _chat?.PeerByName(name) ?? -1;
                if (peer <= 0 && BotPeerByName(name) is { } bot) peer = bot;
                // "bot": the nearest free one
                if (peer <= 0 && name.Equals("bot", StringComparison.OrdinalIgnoreCase) && Body(sender) is { } me
                    && FreeBot(me.Global.E, me.Global.N, 20) is { } near) peer = near.Id;
                return peer == 0 || peer == -1 ? $"[schwing] No player called '{name}'." : Challenge(sender, peer);
        }
    }

    private string Leave(long sender)
    {
        if (_queue.RemoveAll(q => q.Peer == sender) > 0) { QueueTell(sender, false); return "[schwing] You leave the queue."; }
        if (!_schwingOf.TryGetValue(sender, out int id) || !_matches.TryGetValue(id, out var m)) return "[schwing] You are not wrestling.";
        m.Rules.Forfeit(sender, _clock);
        SendState(m);
        return "[schwing] You forfeit the bout.";
    }

    private string? Busy(long peer) =>
        _schwingOf.ContainsKey(peer) ? "[schwing] You are already wrestling."
        : _queue.Exists(q => q.Peer == peer) ? "[schwing] You are in the ring's queue: /schwing leave first."
        : Fights?.InMatch(peer) == true ? "[schwing] You are in a fist fight."
        : Auras?.InMatch(peer) == true ? "[schwing] You are in an aura battle." : null;

    private string Challenge(long from, long to)
    {
        if (from == to) return "[schwing] You cannot wrestle yourself.";
        if (Busy(from) is { } me) return me;
        if (Busy(to) is { } them) return them.Replace("You are", $"{Who(to)} is");
        if (Refusal(from, to) is { } why) return why;
        // a bot takes any bout at once: bots are never sent invites
        if (PlayerReplication.IsBotId(to))
        {
            if (PlayerReplication.IsBotId(from)) return "[schwing] Bots do not wrestle each other.";
            Begin(from, to, festival: false, schlussgang: false);
            return $"[schwing] {Who(to)} accepts at once. Schwingen!";
        }
        if (_chat?.Invites is not { } invites) return "[schwing] Invites are not running.";
        string reply = invites.Send(InviteId, from, to, $"{Who(from)} asks you to wrestle (Schwingen)", "/schwing accept, or accept the invite",
            out var sent);
        if (sent == null) return reply.StartsWith('[') ? reply : $"[schwing] {reply}";
        _chat.Tell(to, $"[schwing] {Who(from)} asks you to wrestle! /schwing accept, or accept the invite, within {SchwingRules.ChallengeSeconds:0} s.", ChatKind.System);
        return $"[schwing] You ask {Who(to)} to wrestle. Waiting for an answer...";
    }

    private string Accept(long sender, long challenger)
    {
        if (challenger < 0) return "[schwing] No such player.";
        if (_chat?.Invites is not { } invites || invites.Find(sender, InviteId, challenger) is not { } invite)
            return "[schwing] Nobody has asked you.";
        return invites.Answer(sender, invite, accept: true);
    }

    private string Accepted(long sender, long challenger)
    {
        if (Busy(sender) is { } me) return me;
        if (Busy(challenger) is { } them) return them.Replace("You are", $"{Who(challenger)} is");
        if (Refusal(challenger, sender) is { } why) return why;
        Begin(challenger, sender, festival: false, schlussgang: false);
        return $"[schwing] You accept {Who(challenger)}.";
    }

    private string Decline(long sender)
    {
        if (_chat?.Invites is not { } invites) return "[schwing] Nobody has asked you.";
        var all = invites.FindAll(sender, InviteId);
        foreach (var invite in all) invites.Answer(sender, invite, accept: false);
        return all.Count > 0 ? "[schwing] Declined." : "[schwing] Nobody has asked you.";
    }

    /// <summary>Why these two cannot wrestle now, or null: both on foot, up, close, and on ground that is not a roof or a wall.</summary>
    private string? Refusal(long a, long b)
    {
        var pa = Body(a);
        var pb = Body(b);
        if (pa == null || pb == null) return "[schwing] That player is not here.";
        if (pa.Ride != RideKind.OnFoot || pb.Ride != RideKind.OnFoot) return "[schwing] Both must be on foot.";
        if (pa.Down != 0 || pb.Down != 0) return "[schwing] Not while someone is down.";
        if (pa.Global.HorizontalDistanceTo(pb.Global) > SchwingRules.ChallengeRange)
            return $"[schwing] {Who(b)} is too far away: within {SchwingRules.ChallengeRange:0} m.";
        return null;
    }

    private void Begin(long a, long b, bool festival, bool schlussgang)
    {
        var ga = Body(a)!.Global;
        var gb = Body(b)!.Global;
        double de = gb.E - ga.E, dn = gb.N - ga.N;
        // world yaw of the line from A to B (Godot: -Z is north, +X east)
        float yaw = de * de + dn * dn < 0.01 ? Body(a)!.NetYaw : Mathf.Atan2(-(float)de, (float)dn);
        var centre = new GlobalPos((ga.E + gb.E) * 0.5, (ga.N + gb.N) * 0.5, (ga.Alt + gb.Alt) * 0.5);
        var match = new Match
        {
            Id = _nextId++,
            Rules = new SchwingMatch(a, b, _clock, schlussgang),
            Centre = centre,
            Yaw = yaw,
            Festival = festival,
            BodyA = Body(a),
            BodyB = Body(b),
        };
        match.CentreWorld = (match.BodyA!.GlobalPosition + match.BodyB!.GlobalPosition) * 0.5f;
        _matches[match.Id] = match;
        BotsEnter(match);
        _schwingOf[a] = _schwingOf[b] = match.Id;
        _queue.RemoveAll(q => q.Peer == a || q.Peer == b);
        foreach (long who in new[] { a, b })
        {
            QueueTell(who, false);
            _chat?.Invites?.WithdrawInvolving(InviteId, who);
            _chat?.Invites?.WithdrawInvolving(FightManager.InviteId, who);
            _chat?.Invites?.WithdrawInvolving(AuraManager.InviteId, who);
        }
        foreach (long who in new[] { a, b })
            if (Connected(who))
                RpcId(who, MethodName.Begin, match.Id, a, b, Who(a), Who(b), centre.E, centre.N, centre.Alt, yaw, festival, schlussgang);
        SendState(match);
        GD.Print($"[schwing] #{match.Id} {a} vs {b}{(festival ? " at the ring" : "")}{(schlussgang ? " (Schlussgang)" : "")}");
    }

    private void ServerTick(double delta)
    {
        _clock += delta;
        TickQueue();
        _ended.Clear();
        foreach (var m in _matches.Values)
        {
            var r = m.Rules;
            if (r.Phase != SchwingPhase.Done)
            {
                if (Gone(m, r.A, ref m.BodyA)) { r.Forfeit(r.A, _clock); GD.Print($"[schwing] #{m.Id} {r.A} left: forfeit"); }
                else if (Gone(m, r.B, ref m.BodyB)) { r.Forfeit(r.B, _clock); GD.Print($"[schwing] #{m.Id} {r.B} left: forfeit"); }
            }
            bool changed = r.Tick(_clock);
            if (r.Phase == SchwingPhase.Live && double.IsNaN(m.LiveSince)) m.LiveSince = _clock;
            StepBots(m, _clock);
            foreach (var e in r.Events) SendEvent(m, e);
            bool phase = r.Phase != m.SentPhase;
            r.Events.Clear();
            if (phase || (changed && _clock - m.SentAt >= 0.1)) SendState(m);
            if (r.Phase == SchwingPhase.Done && !m.Paid) Finished(m);
            if (r.Expired(_clock)) _ended.Add(m.Id);
        }
        foreach (int id in _ended) EndMatch(id);
    }

    /// <summary>A wrestler disconnected, got into something, or walked off the bout.</summary>
    private bool Gone(Match m, long who, ref FootPlayer? body)
    {
        if (body == null || !IsInstanceValid(body)) body = Body(who);
        return body is null || body.Ride != RideKind.OnFoot || body.Global.HorizontalDistanceTo(m.Centre) > SchwingRules.LeaveDistance;
    }

    private void SendEvent(Match m, SchwingEvent e)
    {
        if (e.Kind is not (SchwingEventKind.Telegraph or SchwingEventKind.Resolved)) return;
        foreach (long who in new[] { m.Rules.A, m.Rules.B })
            if (Connected(who)) RpcId(who, MethodName.Thrown, m.Id, e.Who, (int)e.Throw, (int)e.Answer);
    }

    private void SendState(Match m)
    {
        m.SentAt = _clock;
        m.SentPhase = m.Rules.Phase;
        var r = m.Rules;
        int poseA = r.PoseOf(r.A, _clock), poseB = r.PoseOf(r.B, _clock);
        // a bot's body is written here; a human's client replicates the pose it is sent
        if (m.BotA != null && IsInstanceValid(m.BotA.Bot.Body)) m.BotA.Bot.Body.SchwingPose = poseA;
        if (m.BotB != null && IsInstanceValid(m.BotB.Bot.Body)) m.BotB.Bot.Body.SchwingPose = poseB;
        foreach (long who in new[] { r.A, r.B })
            if (Connected(who))
                RpcId(who, MethodName.State, m.Id, (int)r.Phase, (int)r.End, r.Winner, (int)r.BalanceA, (int)r.BalanceB, (int)r.GripA, (int)r.GripB,
                    poseA, poseB, (int)r.Pending, r.ThrowBy, (int)r.Tug, r.Top, r.TimeLeft(_clock));
        if (r.Phase == SchwingPhase.Done && !m.Told)
        {
            m.Told = true;
            string line = Summary(r);
            _chat?.Tell(r.A, line, ChatKind.System);
            _chat?.Tell(r.B, line, ChatKind.System);
        }
    }

    private string Summary(SchwingMatch r) => r.Winner == 0 ? "[schwing] Gestellt: a draw, 9 points each."
        : $"[schwing] {Who(r.Winner)} wins{(r.End == SchwingEnd.Forfeit ? " (forfeit)" : "")}: 10 points to {SchwingRules.Score(false, false)}.";

    /// <summary>The bout ended: Batzen and party points for each human at the ring (never for a bot, a short bout or a forfeiter).</summary>
    private void Finished(Match m)
    {
        m.Paid = true;
        if (!m.Festival || FestivalService.Instance is not { } fest || double.IsNaN(m.LiveSince) || _clock - m.LiveSince < MinPaidSeconds) return;
        var r = m.Rules;
        foreach (long who in new[] { r.A, r.B })
        {
            if (who <= 0 || r.End == SchwingEnd.Forfeit && r.Winner != who) continue;
            var (batzen, points) = SchwingRules.Reward(r.Winner == who, r.Winner == 0, r.Schlussgang);
            int granted = fest.Award(who, FestivalId, batzen, "schwingen", GameId, points);
            _chat?.Tell(who, granted > 0 ? $"[schwing] +{granted} Batzen, +{points} points for the party." : $"[schwing] +{points} points for the party (no more Batzen today).", ChatKind.System);
        }
    }

    private void EndMatch(int id)
    {
        if (!_matches.Remove(id, out var m)) return;
        BotsLeave(m);
        foreach (long who in new[] { m.Rules.A, m.Rules.B })
        {
            _schwingOf.Remove(who);
            if (Connected(who)) RpcId(who, MethodName.Ended, id);
        }
        GD.Print($"[schwing] #{id} over: {m.Rules.End}, winner {m.Rules.Winner}");
    }

    /// <summary>Server: a peer left; its bout is forfeited and its place in the queue given up.</summary>
    public void PeerLeft(long id)
    {
        if (!_server) return;
        _queue.RemoveAll(q => q.Peer == id);
        if (_schwingOf.TryGetValue(id, out int mid) && _matches.TryGetValue(mid, out var m))
        {
            m.Rules.Forfeit(id, _clock);
            SendState(m);
        }
    }

    private FootPlayer? Body(long peer) => _players?.GetNodeOrNull<FootPlayer>(PlayerReplication.NodeName(peer));

    private bool Connected(long peer) => !PlayerReplication.IsBotId(peer) && Multiplayer.GetPeers().Contains((int)peer);

    private string Who(long peer) => PlayerReplication.IsBotId(peer) ? Bots.BotDirector.Instance?.Get(peer)?.Profile.Name ?? "Bot" : _chat?.NameOfPeer(peer) ?? $"#{peer}";

    // ---- the ring's queue (server) ------------------------------------------------------------

    /// <summary>The running Älplerfest with the Schwingen open, and where its ring is (LV95), or null.</summary>
    public static (RunningFestival Run, double E, double N)? RingNow()
    {
        if (FestivalService.Instance is not { } fs) return null;
        if (fs.RunningOf(FestivalId) is not { Site: { } site } run) return null;
        if (FestivalCatalog.Get(FestivalId) is not { } def || !def.IsOpen(GameId, run.Day)) return null;
        var (e, n) = SchwingRing.Of(site);
        return (run, e, n);
    }

    private void TickQueue()
    {
        if (_queue.Count == 0) return;
        if (RingNow() is not { } ring) { foreach (var q in _queue.ToArray()) QueueTell(q.Peer, false); _queue.Clear(); return; }
        // two humans in the queue wrestle; one alone long enough gets a bot
        for (int i = _queue.Count - 1; i >= 0; i--)
            if (Body(_queue[i].Peer) is not { } b || !AtRing(b, ring.E, ring.N)) { QueueTell(_queue[i].Peer, false); _queue.RemoveAt(i); }
        while (_queue.Count >= 2)
        {
            long a = _queue[0].Peer, b = _queue[1].Peer;
            _queue.RemoveRange(0, 2);
            StartAtRing(a, b, ring);
        }
        if (_queue.Count == 1 && _clock - _queue[0].Since >= FillSeconds && FreeBot(ring.E, ring.N) is { } bot)
        {
            long a = _queue[0].Peer;
            _queue.Clear();
            StartAtRing(a, bot.Id, ring);
        }
    }

    private static bool AtRing(FootPlayer body, double e, double n) =>
        body.Ride == RideKind.OnFoot && body.Down == 0 && body.Global.HorizontalDistanceTo(new GlobalPos(e, n, body.Global.Alt)) <= SchwingRules.RingDiameter * 0.5f + RingReach;

    private Bots.Bot? FreeBot(double e, double n, double within = 400)
    {
        Bots.Bot? best = null;
        double bestD = within;
        if (Bots.BotDirector.Instance is not { } bots) return null;
        foreach (var b in bots.All)
        {
            if (!b.Alive || b.Body.Ride != RideKind.OnFoot || b.Body.Down != 0 || InMatch(b.Id) || Fights?.InMatch(b.Id) == true || Auras?.InMatch(b.Id) == true) continue;
            double d = b.Body.Global.HorizontalDistanceTo(new GlobalPos(e, n, b.Body.Global.Alt));
            if (d < bestD) { bestD = d; best = b; }
        }
        return best;
    }

    private void StartAtRing(long a, long b, (RunningFestival Run, double E, double N) ring)
    {
        if (Body(a) is not { } ba || Body(b) is not { } bb) return;
        // a bot is called to the human's side; the humans' clients set themselves on their marks on Begin
        if (PlayerReplication.IsBotId(b)) bb.GlobalPosition = ba.GlobalPosition;
        if (PlayerReplication.IsBotId(a)) ba.GlobalPosition = bb.GlobalPosition;
        Begin(a, b, festival: true, schlussgang: ring.Run.Day >= SchlussgangDay);
    }

    // ---- RPCs: client to server ----------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestJoin()
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        _chat?.Tell(sender, JoinRing(sender), ChatKind.Private);
    }

    /// <summary>E at the ring: into the queue. Public for the checks and for bots.</summary>
    public string JoinRing(long peer)
    {
        if (Busy(peer) is { } busy) return busy;
        if (RingNow() is not { } ring) return "[schwing] The Schwingen is not on now.";
        if (Body(peer) is not { } body || !AtRing(body, ring.E, ring.N)) return "[schwing] Come to the ring first.";
        _queue.Add((peer, _clock));
        QueueTell(peer, true);
        return "[schwing] You are in the ring's queue. The next wrestler meets you; if nobody comes, a Schwinger will.";
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestLeave()
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        _chat?.Tell(sender, Leave(sender), ChatKind.Private);
    }

    /// <summary>A wrestler's lean and brace, and a throw if <paramref name="throwId"/> is one. Budgeted, from a wrestler in its bout.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Act(int fightId, int lean, bool brace, int throwId)
    {
        if (!_server) return;
        ActFor(Multiplayer.GetRemoteSenderId(), fightId, lean, brace, throwId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Tug(int fightId)
    {
        if (_server) TugFor(Multiplayer.GetRemoteSenderId(), fightId);
    }

    /// <summary>A press of the Bodenarbeit tug, for a player's RPC and for a bot alike.</summary>
    public bool TugFor(long who, int fightId)
    {
        if (!_matches.TryGetValue(fightId, out var m) || !m.Rules.Has(who) || !m.Rules.Allow(who, _clock) || !m.Rules.PressTug(who, _clock)) return false;
        SendState(m);
        return true;
    }

    /// <summary>
    /// The server method a bot calls as a player's <c>Act</c> RPC does (bots have no client): the same checks, the same rules.
    /// Returns whether the act was taken.
    /// </summary>
    public bool ActFor(long who, int fightId, int lean, bool brace, int throwId)
    {
        if (!_matches.TryGetValue(fightId, out var m) || !m.Rules.Has(who)) return false;
        if (!m.Rules.Allow(who, _clock)) { GD.Print($"[schwing] #{fightId} refused {who}'s act: over the budget"); return false; }
        if (Body(who) is not { } body || body.Global.HorizontalDistanceTo(m.Centre) > SchwingRules.LeaveDistance * 0.5)
        {
            GD.Print($"[schwing] #{fightId} refused {who}'s act: away from the bout");
            return false;
        }
        var l = (SchwingLean)Math.Clamp(lean, 0, 4);
        bool took = m.Rules.SetLean(who, l, brace);
        if (SchwingRules.Valid(throwId) && !m.Rules.StartThrow(who, (SchwingThrow)throwId, _clock))
            GD.Print($"[schwing] #{fightId} refused {who}'s throw {throwId} ({m.Rules.Phase}, grip {m.Rules.Grip(who):0})");
        return took;
    }

    // ---- client --------------------------------------------------------------------------

    /// <summary>The bout the local player is in, as the server last said.</summary>
    public sealed class View
    {
        public int Id;
        public long A, B;
        public string NameA = "", NameB = "";
        public GlobalPos Centre;
        /// <summary>World yaw of the line from A to B.</summary>
        public float Yaw;
        public bool Festival, Schlussgang;
        public SchwingPhase Phase = SchwingPhase.Intro;
        public SchwingEnd End;
        public long Winner;
        public int BalanceA = 100, BalanceB = 100, GripA = 100, GripB = 100, PoseA, PoseB, Tug = 50;
        public SchwingThrow Pending;
        public long ThrowBy, Top;
        public float TimeLeft = SchwingRules.GangSeconds;
        /// <summary>Local game time of the last state, and of the last phase change and telegraph start.</summary>
        public double StateAt, PhaseAt, WindAt = double.NegativeInfinity;
        /// <summary>The last throw and how it was answered, for the HUD's flash.</summary>
        public SchwingThrow LastThrow;
        public SchwingAnswer LastAnswer;
        public long LastBy;
        public double LastAt = double.NegativeInfinity;

        public long Me;
        public bool IsA => Me == A;
        public long Opponent => IsA ? B : A;
        public string OpponentName => IsA ? NameB : NameA;
        public int MyBalance => IsA ? BalanceA : BalanceB;
        public int TheirBalance => IsA ? BalanceB : BalanceA;
        public int MyGrip => IsA ? GripA : GripB;
        public int TheirGrip => IsA ? GripB : GripA;
        public int MyPose => IsA ? PoseA : PoseB;
        public bool IAmTop => Top == Me;
        public float Clock => Phase is SchwingPhase.Live or SchwingPhase.Ground ? Math.Max(0f, TimeLeft - (float)(GameClock.Now - StateAt)) : TimeLeft;
        /// <summary>How far through the telegraph, 0..1 (0 when nothing winds up).</summary>
        public float Winding => Pending == SchwingThrow.None ? 0f : Mathf.Clamp((float)(GameClock.Now - WindAt) / SchwingRules.Telegraph, 0f, 1f);
    }

    public View? Current { get; private set; }

    /// <summary>Whether the local player waits in the ring's queue.</summary>
    public bool Queued { get; private set; }

    /// <summary>A throw wound up or answered: (who threw, which, the answer; None while it is only winding up).</summary>
    public event Action<long, SchwingThrow, SchwingAnswer>? ThrownEvent;

    public event Action<SchwingPhase, SchwingEnd>? PhaseChanged;

    public event Action? QueueChanged;

    private bool Online => NetLink.Online(this);

    public bool InBout => Current != null;

    /// <summary>E at the ring: join the queue, or give the place up.</summary>
    public void EngageRing()
    {
        if (Current != null) return;
        if (Online) RpcId(1, Queued ? MethodName.RequestLeave : MethodName.RequestJoin);
    }

    public void SendAct(int lean, bool brace, int throwId)
    {
        if (Online && Current is { } v) RpcId(1, MethodName.Act, v.Id, lean, brace, throwId);
    }

    public void SendTug()
    {
        if (Online && Current is { } v) RpcId(1, MethodName.Tug, v.Id);
    }

    public void Leave()
    {
        if (Online && (Current != null || Queued)) RpcId(1, MethodName.RequestLeave);
    }

    // ---- RPCs: server to client ----------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellQueue(bool queued)
    {
        if (Queued == queued) return;
        Queued = queued;
        QueueChanged?.Invoke();
    }

    private void QueueTell(long peer, bool queued)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellQueue, queued);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Begin(int id, long a, long b, string nameA, string nameB, double e, double n, double alt, float yaw, bool festival, bool schlussgang)
    {
        Queued = false;
        Current = new View
        {
            Id = id, A = a, B = b, NameA = nameA, NameB = nameB, Centre = new GlobalPos(e, n, alt), Yaw = yaw,
            Festival = festival, Schlussgang = schlussgang, TimeLeft = schlussgang ? SchwingRules.SchlussgangSeconds : SchwingRules.GangSeconds,
            Me = Multiplayer.GetUniqueId(), StateAt = GameClock.Now, PhaseAt = GameClock.Now,
        };
        GD.Print($"[schwing] #{id} begins: {nameA} vs {nameB}");
        QueueChanged?.Invoke();
        PhaseChanged?.Invoke(SchwingPhase.Intro, SchwingEnd.None);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void State(int id, int phase, int end, long winner, int balA, int balB, int gripA, int gripB, int poseA, int poseB,
        int pending, long throwBy, int tug, long top, float timeLeft)
    {
        if (Current is not { } v || v.Id != id) return;
        bool changed = v.Phase != (SchwingPhase)phase;
        var wasPending = v.Pending;
        v.Phase = (SchwingPhase)phase; v.End = (SchwingEnd)end; v.Winner = winner;
        v.BalanceA = balA; v.BalanceB = balB; v.GripA = gripA; v.GripB = gripB; v.PoseA = poseA; v.PoseB = poseB;
        v.Pending = (SchwingThrow)pending; v.ThrowBy = throwBy; v.Tug = tug; v.Top = top; v.TimeLeft = timeLeft;
        v.StateAt = GameClock.Now;
        if (v.Pending != SchwingThrow.None && wasPending == SchwingThrow.None) v.WindAt = GameClock.Now;
        if (!changed) return;
        v.PhaseAt = GameClock.Now;
        GD.Print($"[schwing] #{id} {v.Phase} {v.End} balance {balA}/{balB} winner {winner}");
        PhaseChanged?.Invoke(v.Phase, v.End);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Thrown(int id, long by, int throwId, int answer)
    {
        if (Current is not { } v || v.Id != id) return;
        if (answer == 0) v.WindAt = GameClock.Now;
        else { v.LastThrow = (SchwingThrow)throwId; v.LastAnswer = (SchwingAnswer)answer; v.LastBy = by; v.LastAt = GameClock.Now; }
        ThrownEvent?.Invoke(by, (SchwingThrow)throwId, (SchwingAnswer)answer);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Ended(int id)
    {
        if (Current is not { } v || v.Id != id) return;
        Current = null;
        GD.Print($"[schwing] #{id} ended");
        PhaseChanged?.Invoke(SchwingPhase.Done, SchwingEnd.None);
    }
}
