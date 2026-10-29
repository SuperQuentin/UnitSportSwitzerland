using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Combat;

/// <summary>What a blow can land on: a training dummy, or a body (a player's, a bot's).</summary>
public enum SwordTargetKind : byte { Dummy = 0, Body = 1 }

/// <summary>What the server decided about one blow, as every peer that sees it hears (<see cref="SwordService.Struck"/>).</summary>
public readonly record struct SwordStrike(long Attacker, SwordMove Move, SwordTargetKind Kind, long Id, float Damage, float Hp,
    Vector3 Knock, Vector3 At, bool Down);

/// <summary>
/// The sword's world (#1371, <c>docs/notes/combat/sword-combat.md</c>): <c>World/Sword</c> on the server, every client and offline,
/// so the RPCs route by path, like <c>World/Fight</c>.
/// <list type="bullet">
/// <item><b>Moves.</b> A client announces each move it starts (<see cref="Act"/>); the server checks it against the peer's own
/// stamina and the frame data (<see cref="SwordGuard"/>) and relays it to the peers that see the swinger, who draw the swing
/// (<see cref="Acted"/>): the blade turns on the hand, nothing is rebuilt.</item>
/// <item><b>Hits.</b> The swinger's client traces its blow (reach, cone) against the dummies and the bodies it draws and names
/// each target it reached (<see cref="Hit"/>). The server takes it only when the guard says the blade could be live, the
/// target is within reach and cone of its own copies of the two bodies (plus a lag slack), a dummy always, a body only when
/// PvP allows it and not while that body dodges. The damage is the move's own, never the client's. Then everyone who sees
/// it hears <see cref="Struck"/>: numbers, sound, the dummy's recoil; the victim's own machine takes the health and the push.</item>
/// <item><b>Dummies</b> (<c>/dummy</c>): the server's list, the clients' nodes (<see cref="TrainingDummy"/>). They show every
/// hit, go down at zero and stand back up.</item>
/// <item><b>Offline</b> the same methods run in this one process, as the server and the only client.</item>
/// </list>
/// A bot calls <see cref="ServerAct"/> and <see cref="ServerHit"/> directly (<c>Bots/BotHands</c>): the same checks, no RPC.
/// </summary>
public partial class SwordService : Node
{
    public const string NodeName = "Sword";

    /// <summary>The client's (or offline) service: the player, the HUD and the probes read it.</summary>
    public static SwordService? Instance { get; private set; }

    /// <summary>A dummy never lasts this long without a hit before it is back at full health (server, wall seconds).</summary>
    public const double DummyReset = 6.0, DummyDownSeconds = 3.0;
    public const float DummyHp = 300f;
    private const int MaxDummies = 12, MaxDummiesPerPeer = 4;

    // ---- events, for the player, the HUD and the probes (client side) ----------------------------

    /// <summary>A move of another peer's body arrived (peer, move): its swing is drawn.</summary>
    public static event Action<long, SwordMove>? Swung;
    /// <summary>A blow landed (every peer that sees it).</summary>
    public static event Action<SwordStrike>? Landed;
    internal static void ResetEvents() { Swung = null; Landed = null; }

    private bool _server;
    private WorldOrigin _origin = null!;
    private readonly SwordGuard _guard = new();
    private readonly Dictionary<long, float> _yaw = new();

    // server and offline: the dummies' truth
    private sealed class DummyData
    {
        public int Id;
        public long Owner;
        public GlobalPos Pos;
        public float Yaw, Hp;
        public double DownAt = double.NegativeInfinity, HitAt = double.NegativeInfinity;
    }
    private readonly Dictionary<int, DummyData> _dummyData = new();
    private int _nextDummy = 1;

    // clients and offline: the dummies on screen
    private readonly Dictionary<int, TrainingDummy> _dummies = new();

    /// <summary>The dummies drawn here, for a swing's trace and the probes.</summary>
    public Dictionary<int, TrainingDummy> Dummies => _dummies;   // concrete: a swing's per-frame scan allocates no enumerator

    public static SwordService Create(Node world, WorldOrigin origin, bool server)
    {
        var s = new SwordService { Name = NodeName, _origin = origin, _server = server };
        world.AddChild(s);
        return s;
    }

    private bool Online => Multiplayer.MultiplayerPeer is { } peer and not OfflineMultiplayerPeer
        && peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    /// <summary>The server, or the one process offline: the place the rules run.</summary>
    private bool Authority => _server || !Online;

    private long MyPeer => Online ? Multiplayer.GetUniqueId() : 1;

    public override void _EnterTree()
    {
        if (!_server) Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        if (!_server && Online) RpcId(1, MethodName.RequestDummies);
        if (_server) Multiplayer.PeerDisconnected += OnPeerGone;
    }

    private void OnPeerGone(long peer)
    {
        _guard.Forget(peer);
        _yaw.Remove(peer);
    }

    public override void _Process(double delta)
    {
        if (!Authority) return;
        double now = RealClock.Now;
        foreach (var d in _dummyData.Values)
        {
            // down for a few seconds, then up; hit and left alone, healed
            bool revive = d.Hp <= 0f && now - d.DownAt >= DummyDownSeconds;
            bool heal = d.Hp > 0f && d.Hp < DummyHp && now - d.HitAt >= DummyReset;
            if (!revive && !heal) continue;
            d.Hp = DummyHp;
            Broadcast(0, MethodName.Healed, d.Id, DummyHp);
        }
    }

    // ================================================================================================
    // moves
    // ================================================================================================

    /// <summary>
    /// Owner side: this body starts <paramref name="move"/> facing <paramref name="yaw"/>. The server checks it; the peers that
    /// see the body draw it.
    /// </summary>
    public void SendAct(SwordMove move, float yaw, int code = 0)
    {
        if (!Online) { ServerAct(1, move, yaw, code); return; }
        RpcId(1, MethodName.Act, (int)move, yaw, code);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Act(int move, float yaw, int code)
    {
        if (!_server || !float.IsFinite(yaw) || move is < 0 or > 255) return;
        ServerAct(Multiplayer.GetRemoteSenderId(), (SwordMove)move, yaw, code & 0x3F);
    }

    /// <summary>
    /// Server (and a bot's hands, and offline): <paramref name="who"/> starts <paramref name="move"/>. Taken when its body is up
    /// and the guard allows it (rate, stamina); then relayed. Returns whether it was taken.
    /// </summary>
    public bool ServerAct(long who, SwordMove move, float yaw, int code = 0)
    {
        if (!Authority) return false;
        var body = BodyOf(who);
        if (body == null || body.Down != 0 || body.Eliminated) return false;
        var verdict = _guard.TryAct(who, move, RealClock.Now);
        if (verdict != SwordVerdict.Ok)
        {
            GD.Print(FormattableString.Invariant($"[sword] refused {who}: {move} ({verdict})"));
            return false;
        }
        _yaw[who] = yaw;
        if (!Online)
            return true;
        foreach (int peer in Multiplayer.GetPeers())
            if (peer != who && Sees(peer, who))
                RpcId(peer, MethodName.Acted, who, (int)move, yaw, code);
        return true;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Acted(long who, int move, float yaw, int code)
    {
        if (BodyOf(who) is not { } body) return;
        body.SwordSeen((SwordMove)move, yaw, code);
        Swung?.Invoke(who, (SwordMove)move);
    }

    // ================================================================================================
    // hits
    // ================================================================================================

    /// <summary>Owner side: the last announced <paramref name="move"/> reached this target.</summary>
    public void SendHit(SwordMove move, SwordTargetKind kind, long id)
    {
        if (!Online) { ServerHit(1, move, kind, id); return; }
        RpcId(1, MethodName.Hit, (int)move, (int)kind, id);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Hit(int move, int kind, long id)
    {
        if (!_server || kind is < 0 or > 1) return;
        ServerHit(Multiplayer.GetRemoteSenderId(), (SwordMove)move, (SwordTargetKind)kind, id);
    }

    /// <summary>
    /// Server (a bot's hands, offline): <paramref name="who"/>'s <paramref name="move"/> reached the target. Returns whether it
    /// landed. Refusals log <c>[sword] refused ...</c>.
    /// </summary>
    public bool ServerHit(long who, SwordMove move, SwordTargetKind kind, long id)
    {
        if (!Authority || !SwordRules.IsBlow(move)) return false;
        var attacker = BodyOf(who);
        if (attacker == null || attacker.Down != 0 || attacker.Eliminated) return false;
        double now = RealClock.Now;
        if (!_guard.TryHit(who, move, Key(kind, id), now))
        {
            GD.Print(FormattableString.Invariant($"[sword] refused {who}: a {move} hit on {kind} {id} out of turn"));
            return false;
        }
        var def = SwordRules.Def(move);
        var from = attacker.Global;
        GlobalPos to;
        FootPlayer? victim = null;
        DummyData? dummy = null;
        if (kind == SwordTargetKind.Dummy)
        {
            if (!_dummyData.TryGetValue((int)id, out dummy) || dummy.Hp <= 0f) return false;
            to = dummy.Pos;
        }
        else
        {
            victim = BodyOf(id);
            if (victim == null || victim == attacker || victim.Down == 1 || victim.Eliminated || victim.Npc && !victim.Bot) return false;
            if (!PvpRules.Allows(who, id))
            {
                GD.Print(FormattableString.Invariant($"[sword] refused {who}: PvP is off for {id}"));
                return false;
            }
            if (_guard.Invulnerable(id, now)) return false;   // dodged
            to = victim.Global;
        }
        // east is +x, north is -z, as the world's axes: a yaw is the same in every frame
        float dx = (float)(to.E - from.E), dz = (float)(from.N - to.N);
        float yaw = _yaw.GetValueOrDefault(who, attacker.Rotation.Y);
        float fx = -Mathf.Sin(yaw), fz = -Mathf.Cos(yaw);
        if (!SwordRules.InCone(0, 0, fx, fz, dx, dz, def.Reach + SwordRules.LagSlack, Mathf.Min(180f, def.ConeDeg + SwordRules.ConeSlackDeg), out float dist)
            || !SwordRules.InHeight((float)(to.Alt - from.Alt)))
        {
            GD.Print(FormattableString.Invariant($"[sword] refused {who}: {kind} {id} out of reach ({dist:F1} m, {move})"));
            return false;
        }
        var flat = new Vector3(dx, 0, dz);
        var dir = flat.LengthSquared() > 1e-4f ? flat.Normalized() : new Vector3(fx, 0, fz);
        var knock = dir * SwordRules.KnockAt(move, dist);
        float hp = 0f;
        bool down = false;
        if (dummy != null)
        {
            dummy.Hp = Mathf.Max(0f, dummy.Hp - def.Damage);
            dummy.HitAt = now;
            hp = dummy.Hp;
            if (hp <= 0f) { down = true; dummy.DownAt = now; }
        }
        else if (victim != null && victim.Bot && victim.IsMultiplayerAuthority())
        {
            // a bot is run here: the server is its victim side
            victim.ShotHit(def.Damage, who);
            victim.SwordKnocked(knock);
            hp = victim.Health;
        }
        GD.Print(FormattableString.Invariant($"[sword] {who} hit {kind} {id} for {def.Damage:F0} ({move})"));
        var at = new GlobalPos(to.E, to.N, to.Alt + 1.2);
        var strike = new SwordStrike(who, move, kind, id, def.Damage, hp, knock, _origin.ToWorld(at), down);
        if (victim != null && !victim.Bot) PvpRules.RaiseHit(who, id, def.Damage);
        BroadcastStruck(strike, at, victim != null && !victim.Bot ? id : 0);
        return true;
    }

    private void BroadcastStruck(SwordStrike s, GlobalPos at, long victimPeer)
    {
        if (!Online)
        {
            Apply(s);
            return;
        }
        foreach (int peer in Multiplayer.GetPeers())
            if (peer == s.Attacker || peer == victimPeer || Sees(peer, s.Attacker) || s.Kind == SwordTargetKind.Body && Sees(peer, s.Id) || s.Kind == SwordTargetKind.Dummy)
                RpcId(peer, MethodName.Struck, s.Attacker, (int)s.Move, (int)s.Kind, s.Id, s.Damage, s.Hp, s.Knock, at.E, at.N, at.Alt, s.Down);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Struck(long attacker, int move, int kind, long id, float damage, float hp, Vector3 knock, double e, double n, double alt, bool down)
    {
        Apply(new SwordStrike(attacker, (SwordMove)move, (SwordTargetKind)kind, id, damage, hp, knock, _origin.ToWorld(e, n, alt), down));
    }

    /// <summary>Every peer that hears a blow: the numbers, the sound, the dummy's recoil, the swinger's feel, the victim's health.</summary>
    private void Apply(SwordStrike s)
    {
        var def = SwordRules.Def(s.Move);
        bool heavy = s.Move is SwordMove.Slash5 or SwordMove.Charged or SwordMove.Plunge;
        TrainingDummy? dummy = null;
        FootPlayer? victim = null;
        if (s.Kind == SwordTargetKind.Dummy) _dummies.TryGetValue((int)s.Id, out dummy);
        else victim = BodyOf(s.Id);
        // the blow's mark: a spark where it landed, a sound, a number
        SwordFx.Strike(this, s.At, heavy, s.Damage);
        if (dummy != null) dummy.Struck(s.Knock, s.Damage, s.Hp, s.Down);
        if (victim != null)
        {
            if (victim.IsMultiplayerAuthority() && !victim.Bot && !victim.Npc) victim.SwordHurt(s.Attacker, s.Damage, s.Knock, s.Move);
            else victim.Flinch(s.Knock, heavy ? 1.1f : 0.6f);   // every other copy rocks with it
        }
        // the swinger feels it: the hands stop for a moment, the screen shakes
        if (BodyOf(s.Attacker) is { } swinger && swinger.IsMultiplayerAuthority() && !swinger.Bot)
            swinger.SwordLanded(s.Move, s.Down);
        Landed?.Invoke(s);
    }

    // ================================================================================================
    // dummies
    // ================================================================================================

    /// <summary>
    /// <c>/dummy</c> (server or offline): puts a training dummy in front of <paramref name="sender"/>; <c>/dummy clear</c> takes
    /// theirs away. The line to show.
    /// </summary>
    public string Command(long sender, string rest)
    {
        if (!Authority) return "[dummy] Not available here.";
        var arg = rest.Trim().ToLowerInvariant();
        if (arg == "clear")
        {
            var mine = new List<int>();
            foreach (var d in _dummyData.Values) if (d.Owner == sender) mine.Add(d.Id);
            foreach (int id in mine) RemoveDummy(id);
            return mine.Count == 0 ? "[dummy] You have no dummy." : $"[dummy] Removed {mine.Count}.";
        }
        if (arg.Length > 0) return "[dummy] /dummy puts a training dummy in front of you; /dummy clear takes yours away.";
        var body = BodyOf(sender);
        if (body == null) return "[dummy] You need a body to put it in front of.";
        int owned = 0;
        foreach (var d in _dummyData.Values) if (d.Owner == sender) owned++;
        if (_dummyData.Count >= MaxDummies || owned >= MaxDummiesPerPeer)
            return $"[dummy] Enough dummies ({Math.Min(MaxDummiesPerPeer, MaxDummies)} each); /dummy clear takes yours away.";
        float yaw = body.Rotation.Y;
        var g = body.Global;
        // 2.6 m ahead, facing the one who put it there
        var at = new GlobalPos(g.E - Mathf.Sin(yaw) * 2.6, g.N + Mathf.Cos(yaw) * 2.6, g.Alt);
        AddDummy(sender, at, yaw + Mathf.Pi);
        return "[dummy] A training dummy stands in front of you.";
    }

    private void AddDummy(long owner, GlobalPos at, float yaw)
    {
        int id = _nextDummy++;
        _dummyData[id] = new DummyData { Id = id, Owner = owner, Pos = at, Yaw = yaw, Hp = DummyHp };
        Broadcast(0, MethodName.DummyAdd, id, at.E, at.N, at.Alt, yaw, DummyHp);
    }

    private void RemoveDummy(int id)
    {
        if (!_dummyData.Remove(id)) return;
        Broadcast(0, MethodName.DummyGone, id);
    }

    /// <summary>Calls <paramref name="method"/> on every peer (RPC) and here when offline.</summary>
    private void Broadcast(long except, StringName method, params Variant[] args)
    {
        if (!Online)
        {
            if (method == MethodName.DummyAdd) DummyAdd(args[0].AsInt32(), args[1].AsDouble(), args[2].AsDouble(), args[3].AsDouble(), args[4].AsSingle(), args[5].AsSingle());
            else if (method == MethodName.DummyGone) DummyGone(args[0].AsInt32());
            else if (method == MethodName.Healed) Healed(args[0].AsInt32(), args[1].AsSingle());
            return;
        }
        foreach (int peer in Multiplayer.GetPeers())
            if (peer != except)
                RpcId(peer, method, args);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestDummies()
    {
        if (!_server) return;
        int peer = Multiplayer.GetRemoteSenderId();
        foreach (var d in _dummyData.Values)
            RpcId(peer, MethodName.DummyAdd, d.Id, d.Pos.E, d.Pos.N, d.Pos.Alt, d.Yaw, d.Hp);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DummyAdd(int id, double e, double n, double alt, float yaw, float hp)
    {
        if (_server || _dummies.ContainsKey(id)) return;
        var dummy = TrainingDummy.Create(id, _origin, new GlobalPos(e, n, alt), yaw, hp);
        _dummies[id] = dummy;
        AddChild(dummy);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DummyGone(int id)
    {
        if (_server || !_dummies.Remove(id, out var dummy)) return;
        dummy.QueueFree();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Healed(int id, float hp)
    {
        if (!_server && _dummies.TryGetValue(id, out var dummy)) dummy.Reset(hp);
    }

    // ================================================================================================
    // what the swinger's client asks
    // ================================================================================================

    /// <summary>Server: the standing dummy nearest <paramref name="world"/> (this origin's world space), or null.</summary>
    public Vector3? NearestDummy(Vector3 world)
    {
        Vector3? best = null;
        float bestD = float.MaxValue;
        foreach (var d in _dummyData.Values)
        {
            var p = _origin.ToWorld(d.Pos);
            float dist = p.DistanceTo(world);
            if (d.Hp > 0f && dist < bestD) { bestD = dist; best = p; }
        }
        return best;
    }

    /// <summary>
    /// Server (a bot's blade gone live): <paramref name="who"/>'s <paramref name="move"/> against every dummy and body the server holds
    /// within its reach and cone, each through <see cref="ServerHit"/> (the same checks as a player's named hit). The number landed.
    /// </summary>
    public int ServerTrace(long who, SwordMove move, float yaw)
    {
        if (!Authority || BodyOf(who) is not { } attacker || !SwordRules.IsBlow(move)) return 0;
        var def = SwordRules.Def(move);
        var from = attacker.Global;
        float fx = -Mathf.Sin(yaw), fz = -Mathf.Cos(yaw);
        int landed = 0;
        bool Reaches(GlobalPos to) =>
            SwordRules.InCone(0, 0, fx, fz, (float)(to.E - from.E), (float)(from.N - to.N), def.Reach, def.ConeDeg, out _)
            && SwordRules.InHeight((float)(to.Alt - from.Alt));
        foreach (var d in _dummyData.Values.ToArray())
            if (d.Hp > 0f && Reaches(d.Pos) && !_guard.Reached(who, Key(SwordTargetKind.Dummy, d.Id)) && ServerHit(who, move, SwordTargetKind.Dummy, d.Id)) landed++;
        if (GetNodeOrNull("../Players") is { } players)
            foreach (var n in players.GetChildren())
                if (n is FootPlayer p && p != attacker && (p.Bot || !p.Npc) && p.Down != 1 && Reaches(p.Global)
                    && FootPlayer.NetId(p.Name) is { } id && !_guard.Reached(who, Key(SwordTargetKind.Body, id)) && ServerHit(who, move, SwordTargetKind.Body, id)) landed++;
        return landed;
    }

    /// <summary>A target a trace may reach, as this peer sees it.</summary>
    public readonly record struct Reach(SwordTargetKind Kind, long Id, Vector3 Position);

    /// <summary>
    /// Every target near <paramref name="attacker"/> as this peer draws it: the dummies standing and the other bodies up (bots,
    /// other players; not race NPCs, not a downed body that cannot be finished). Appended to <paramref name="into"/>.
    /// </summary>
    public void Targets(FootPlayer attacker, List<Reach> into)
    {
        foreach (var d in _dummies.Values)
            if (d.Standing) into.Add(new Reach(SwordTargetKind.Dummy, d.Id, d.GlobalPosition));
        foreach (var n in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (n is FootPlayer p && p != attacker && p.IsInsideTree() && (p.Bot || !p.Npc && FootPlayer.NetId(p.Name) is > 0)
                && p.Down != 1 && !p.Eliminated && p.Ride == RideKind.OnFoot)
                into.Add(new Reach(SwordTargetKind.Body, FootPlayer.NetId(p.Name) ?? 0, p.GlobalPosition));
    }

    /// <summary>Where a target is drawn now, or null when it is gone (a lock lets go).</summary>
    public Vector3? PositionOf(SwordTargetKind kind, long id)
    {
        if (kind == SwordTargetKind.Dummy) return _dummies.TryGetValue((int)id, out var d) && d.Standing ? d.GlobalPosition : null;
        return BodyOf(id) is { Down: not 1, Eliminated: false } p ? p.GlobalPosition : null;
    }

    // ---- shared ----

    private static long Key(SwordTargetKind kind, long id) => kind == SwordTargetKind.Dummy ? -1_000_000 - id : id;

    private FootPlayer? BodyOf(long id) => GetNodeOrNull<FootPlayer>("../Players/" + PlayerReplication.NodeName(id));

    private bool Sees(long viewer, long target) =>
        GetNodeOrNull<InterestService>("../" + InterestService.NodeName)?.ServerSees(viewer, target) != false;
}
