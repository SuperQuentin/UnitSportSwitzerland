using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Adventure.Versteckis;

/// <summary>
/// The client side (#1268): every game the server runs (the Activities list, the disguises to draw on any body met), its
/// own game's HUD, the lent round kit, the decor of the area it stands in, the tools in hand, the objectives' holds, the
/// sounds, and the follow camera once caught.
/// </summary>
public partial class VersteckisManager
{
    /// <summary>The client's node, for the HUD, the Activities tab and the item tools.</summary>
    public static VersteckisManager? Instance { get; private set; }

    /// <summary>Raised on a client after the games changed.</summary>
    public event Action? StateChanged;

    public Func<FootPlayer?> LocalPlayer { get; set; } = () => null;
    public Func<Inventory?> Inventory { get; set; } = () => null;
    /// <summary>Puts the local player on the ground at an LV95 point (<c>Core/Teleporter</c>).</summary>
    public Func<double, double, string?, bool> Teleport { get; set; } = (_, _, _) => false;
    public Action<Node3D>? AddAnchor { get; set; }
    public Action<Node3D>? RemoveAnchor { get; set; }
    /// <summary>The terrain source the area's tiles are read from.</summary>
    public Func<Terrain.IChunkSource?> Source { get; set; } = () => null;
    /// <summary>The ground under a world point where this client has it (the decor settles on it).</summary>
    public Func<Vector3, float?> GroundAt { get; set; } = _ => null;

    private List<VsState> _all = new();
    /// <summary>Every game on the server.</summary>
    public IReadOnlyList<VsState> Games => _all;

    /// <summary>The game this player is in (lobby or running), else null.</summary>
    public VsState? Mine { get; private set; }
    public VsEntrant? MyEntry { get; private set; }
    public long Me => Multiplayer.MultiplayerPeer != null ? Multiplayer.GetUniqueId() : 1;

    /// <summary>This client was lent the round and not yet released.</summary>
    public bool InGame { get; private set; }
    private double _returnE, _returnN;

    private VsHud? _hud;
    private VsDecorLayer? _layer;
    private Ui.FollowCam? _follow;
    private VsGround? _site;
    private Task<VsGround>? _siteLoading;
    private (int Id, int Seed, float Radius) _siteFor;
    private double _disguiseTick;
    private readonly Dictionary<long, FootPlayer> _bodies = new();
    private readonly List<long> _undress = new();

    // ---- what the HUD reads ----
    /// <summary>Local game time of the last miss (the 4 s cooldown shown).</summary>
    public double MissedAt { get; private set; } = -100;
    /// <summary>Local game time a local sprint burst runs until.</summary>
    public double SprintUntil { get; private set; }
    /// <summary>The objective the HUD offers (a site index) and how far along a hold is, 0..1.</summary>
    public (int Site, string Prompt, float Progress)? Offer { get; private set; }
    /// <summary>The site whose hold runs now (-1: none) and when it began (local game time).</summary>
    private int _holding = -1;
    private double _holdFrom;
    /// <summary>For probes: hold Interact without a key.</summary>
    public bool ForceHold { get; set; }
    /// <summary>The area this client draws (the HUD's boundary warning, the checks).</summary>
    public VsGround? Site => _site;
    public VsDecorLayer? Layer => _layer;
    public VsHud? Hud => _hud;
    public long Watching => _follow?.Watching ?? 0;

    public static VersteckisManager CreateClient(WorldOrigin origin) => new() { Name = NodeName, _origin = origin };

    public override void _Ready()
    {
        if (_server) return;
        _hud = new VsHud(this);
        AddChild(_hud);
        _layer = new VsDecorLayer { Name = "Decor", Origin = _origin };
        AddChild(_layer);
        _follow = new Ui.FollowCam
        {
            Name = "FollowCam", LocalPlayer = () => LocalPlayer(), AddAnchor = n => AddAnchor?.Invoke(n), RemoveAnchor = n => RemoveAnchor?.Invoke(n),
            BodyOf = id => Body(id),
        };
        AddChild(_follow);
    }

    private void ClientExit()
    {
        if (_server) return;
        if (InGame) Permissions.SetInMatch(false);
    }

    private FootPlayer? Body(long peer)
    {
        if (_bodies.TryGetValue(peer, out var b) && IsInstanceValid(b) && b.IsInsideTree()) return b;
        b = GetNodeOrNull<FootPlayer>("../Players/" + PlayerReplication.NodeName(peer));
        if (b != null) _bodies[peer] = b;
        else _bodies.Remove(peer);
        return b;
    }

    // ==== the server's messages ==================================================================

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetGames(string json)
    {
        if (_server) return;
        Apply(VsState.FromListJson(json));
    }

    /// <summary>Takes a new list of games (from the server, or a probe's own offline).</summary>
    public void Apply(List<VsState> games)
    {
        var before = Mine;
        _all = games;
        Mine = null;
        MyEntry = null;
        long me = Me;
        foreach (var g in _all)
            if (g.Find(me) is { Left: false } e) { Mine = g; MyEntry = e; break; }
        if (Mine != null && before != null && before.Id == Mine.Id && before.Phase != Mine.Phase) PhaseChanged(before.Phase, Mine);
        // the caught first, then the hiders: who an out player may follow
        if (Mine != null)
            _follow?.SetCandidates(Mine.Entrants.Where(e => e.Hiding && e.Peer != me).Select(e => e.Peer)
                .Concat(Mine.Entrants.Where(e => e.Role == VsRole.Seeker && e.Peer != me).Select(e => e.Peer)));
        _disguiseTick = 0;
        _layer?.Refresh(Mine ?? Nearest());
        StateChanged?.Invoke();
    }

    private void PhaseChanged(VsPhase was, VsState now)
    {
        if (now.Phase == VsPhase.Seek && MyEntry?.Role == VsRole.Seeker) LocalPlayer()?.Announce("READY OR NOT!", true);
        if (now.Phase == VsPhase.Hide)
            LocalPlayer()?.Announce(MyEntry?.Role == VsRole.Seeker ? "YOU SEEK: COUNT!" : "HIDE!", true);
        if (now.Phase == VsPhase.Results && now.Winners.Contains(Me)) LocalPlayer()?.Announce("TOP OF THE VILLAGE", true);
        GD.Print($"[vs] phase {now.Phase}, round {now.Round}/{now.Rounds}, I am {MyEntry?.Role}");
    }

    /// <summary>The game whose area is nearest this player (a bystander sees the props too), within 1.5 km.</summary>
    private VsState? Nearest()
    {
        if (LocalPlayer() is not { } me || _all.Count == 0) return null;
        var (e, n) = _origin.ToLv95(me.GlobalPosition);
        return _all.Where(g => g.Phase is not (VsPhase.Lobby or VsPhase.Closed))
            .Select(g => (g, d: (g.E - e) * (g.E - e) + (g.N - n) * (g.N - n))).Where(x => x.d < 1500 * 1500)
            .OrderBy(x => x.d).Select(x => x.g).FirstOrDefault();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Enter(int id)
    {
        if (_server || InGame || LocalPlayer() is not { } me) return;
        (_returnE, _returnN) = _origin.ToLv95(me.GlobalPosition);
        InGame = true;
        Inventory()?.BeginMatch();
        Permissions.SetInMatch(true);
        GD.Print($"[vs] entered game {id}");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Kit(int role)
    {
        if (_server || Inventory() is not { } inv) return;
        GiveKit(inv, (VsRole)role);
    }

    /// <summary>The round kit on the hotbar: the seeker's tag and sniff, the hiders' disguise, decoy, sprint and voice.</summary>
    public static void GiveKit(Inventory inv, VsRole role)
    {
        inv.Clear();
        var kit = role == VsRole.Seeker ? new[] { ItemId.VsTag, ItemId.VsSniff } : new[] { ItemId.VsDisguise, ItemId.VsDecoy, ItemId.VsSprint, ItemId.VsMoo };
        foreach (var id in kit) inv.Add(id, 1);
        inv.Select(0);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PutAt(double e, double n)
    {
        if (_server) return;
        Teleport(e, n, null);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Release()
    {
        if (_server || !InGame) return;
        InGame = false;
        _holding = -1;
        _follow?.Stop();
        Permissions.SetInMatch(false);
        Inventory()?.EndMatch();
        if (LocalPlayer() is { } me && VsDisguise.Of(me) is { } d) d.Set(VsProp.None);
        _hud?.CloseRadial();
        Teleport(_returnE, _returnN, "back from Versteckis");
        GD.Print("[vs] released");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Caught(long hider, long by)
    {
        if (_server) return;
        string h = Mine?.Find(hider)?.Name ?? "?";
        if (hider == Me)
        {
            LocalPlayer()?.Announce("GFUNDE! YOU WERE FOUND", false);
            _holding = -1;
            _hud?.CloseRadial();
        }
        else if (by == Me) LocalPlayer()?.Announce($"GFUNDE! {h.ToUpperInvariant()}", true);
        Effect(VsSounds.Found);
        _hud?.Feed($"{Mine?.Find(by)?.Name ?? "?"} found {h}");
        GD.Print($"[vs] caught {h} by {Mine?.Find(by)?.Name}");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Missed()
    {
        if (_server) return;
        MissedAt = GameClock.Now;
        _hud?.Feed("Nothing there: 4 s");
        GD.Print("[vs] missed");
    }

    /// <summary>The puffs of a sniff, at LV95 points: drawn for 3 s, to this seeker only.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Sniffed(double[] es, double[] ns)
    {
        if (_server) return;
        _layer?.Puffs(es, ns, VsRules.SniffSeconds);
        _hud?.Feed(es.Length == 0 ? "Nobody within 30 m" : $"{es.Length} {(es.Length == 1 ? "puff" : "puffs")} of pollen");
        GD.Print($"[vs] sniffed {es.Length} puffs");
    }

    /// <summary>A sound in the game's area: a Moo, a sprint's thumps, a poof, the bell.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Sounded(long from, int voice, double e, double n)
    {
        if (_server) return;
        var v = (VsVoice)voice;
        var at = _origin.ToWorld(e, n, 0);
        at.Y = GroundAt(at) ?? LocalPlayer()?.GlobalPosition.Y ?? 0;
        if (v == VsVoice.Bell) Effect(VsSounds.Toll, -4f);
        else if (VsSounds.Of(v) is { } bank) Spatial(bank, at + Vector3.Up, v == VsVoice.Thump ? VsRules.SprintHeard : VsRules.MooHeard);
        GD.Print($"[vs] heard {v} from {Mine?.Find(from)?.Name ?? from.ToString()}");
    }

    private AudioStreamPlayer? _fx;

    private void Effect(AudioStream stream, float db = 0f)
    {
        if (DisplayServer.GetName() == "headless") return;
        if (_fx == null) AddChild(_fx = new AudioStreamPlayer { Name = "Effect", Bus = Audio.SfxBus.Name });
        _fx.Stream = stream;
        _fx.VolumeDb = db;
        _fx.Play();
    }

    private void Spatial(Audio.SfxBank bank, Vector3 at, float reach)
    {
        if (DisplayServer.GetName() == "headless" || _layer == null) return;
        var p = new AudioStreamPlayer3D { Stream = bank.Variants[_rng.Next(bank.Variants.Length)], Bus = Audio.SfxBus.Name, MaxDistance = reach, UnitSize = 8 };
        _layer.AddChild(p);
        p.GlobalPosition = at;
        p.Finished += p.QueueFree;
        p.Play();
    }

    // ==== every frame ============================================================================

    private void ClientTick(double delta)
    {
        LoadSite();
        DisguiseTick(delta);
        var me = LocalPlayer();
        var s = Mine;
        var mine = MyEntry;
        // out: follow the hiders, then the seeker
        bool spectate = InGame && s is { Phase: VsPhase.Hide or VsPhase.Seek } && mine is { Caught: true };
        if (spectate) _follow?.Follow(delta);
        else if (_follow is { Active: true }) _follow.Stop();
        ObjectiveTick(me, s, mine);
        SprintTick(me, s, mine);
        // the blind seeker counts aloud, a tick a second
        if (InGame && s is { Phase: VsPhase.Hide } && mine?.Role == VsRole.Seeker)
        {
            int left = (int)Math.Ceiling(s.PhaseEnds - ClockSync.SimNow);
            if (left != _counted && left >= 0)
            {
                _counted = left;
                if (VsPrefs.Counting) Effect(VsSounds.Tick, -8f);
            }
        }
    }

    private int _counted = -1;

    /// <summary>Reads the area of this client's game (or the nearest running one) once its radius is known.</summary>
    private void LoadSite()
    {
        if (_siteLoading is { IsCompleted: true } done)
        {
            _siteLoading = null;
            if (done.IsCompletedSuccessfully)
            {
                _site = done.Result;
                _layer?.Build(_site, GroundAt);
                GD.Print($"[vs] area read: {_site.Decor.Count} decor, {_site.Buildings.Count} buildings, kinds {string.Join(",", _site.Kinds)}");
                _layer?.Refresh(Mine ?? Nearest());
            }
        }
        var g = Mine is { Phase: not VsPhase.Lobby } m ? m : Nearest();
        if (g == null)
        {
            if (_site != null && _siteLoading == null)
            {
                _site = null;
                _siteFor = default;
                _layer?.Clear();
            }
            return;
        }
        if (g.Phase is VsPhase.Lobby or VsPhase.Closed || _siteLoading != null) return;
        var key = (g.Id, g.Seed, g.Radius);
        if (key == _siteFor) return;
        _siteFor = key;
        _siteLoading = VsGround.LoadAsync(Source(), g.E, g.N, g.Radius, g.Seed);
    }

    /// <summary>Four times a second: every body of every game drawn as its prop (or itself again), bystanders included.</summary>
    private void DisguiseTick(double delta)
    {
        if ((_disguiseTick -= delta) > 0) return;
        _disguiseTick = 0.25;
        _undress.Clear();
        foreach (var (peer, body) in _bodies)
            if (IsInstanceValid(body) && VsDisguise.Of(body) is { Kind: not VsProp.None }) _undress.Add(peer);
        foreach (var g in _all)
            foreach (var e in g.Entrants)
            {
                if (!e.Disguised || g.Phase is not (VsPhase.Hide or VsPhase.Seek)) continue;
                if (Body(e.Peer) is not { } body) continue;
                VsDisguise.For(body).Set(e.Prop);
                _undress.Remove(e.Peer);
            }
        foreach (long peer in _undress)
            if (Body(peer) is { } body && VsDisguise.Of(body) is { } d) d.Set(VsProp.None);
    }

    /// <summary>The walk of the local body while disguised: the prop's cap, a sprint's burst; null when it is itself.</summary>
    private float? LocalWalk(FootPlayer body)
    {
        if (!InGame || MyEntry is not { } e || !e.Disguised || Mine is not { Phase: VsPhase.Hide or VsPhase.Seek }) return null;
        if (body != LocalPlayer()) return null;
        return GameClock.Now < SprintUntil ? VsRules.SprintSpeed : VsProps.Of(e.Prop).WalkCap;
    }

    /// <summary><see cref="FootPlayer.PropWalk"/>: the server's answer for its bots, else this client's for its own body.</summary>
    private static float? PropWalkOf(FootPlayer body) => Server?.BotWalk(body) ?? Instance?.LocalWalk(body);

    // ---- sprint: the Sprint key while disguised ----
    private bool _sprintHeld;

    private void SprintTick(FootPlayer? me, VsState? s, VsEntrant? mine)
    {
        bool down = PlayerInput.Held(PlayerInput.Sprint);
        bool pressed = down && !_sprintHeld;
        _sprintHeld = down;
        if (pressed && me != null && mine is { Disguised: true } && s?.Phase == VsPhase.Seek) Sprint();
    }

    /// <summary>A disguised sprint (the key, the hotbar's Gump): the burst starts here at once; the server checks the charge.</summary>
    public bool Sprint()
    {
        if (MyEntry is not { Disguised: true } e || Mine?.Phase != VsPhase.Seek) return false;
        if (ClockSync.SimNow < e.SprintReadyAt) { _hud?.Feed($"Gump again in {e.SprintReadyAt - ClockSync.SimNow:F0} s"); return false; }
        SprintUntil = GameClock.Now + VsRules.SprintSeconds;
        RpcId(1, MethodName.RequestSprint);
        return true;
    }

    // ==== the tools in hand (ItemUse.Round) ======================================================

    /// <summary>
    /// The round kit's Use (<c>ItemController.UseSlot</c>, <see cref="ItemUse.Round"/>): tag where the crosshair is, sniff,
    /// leave a decoy, sprint, moo, or the Disguise radial (with Aim held: copy the decor prop aimed at).
    /// </summary>
    public void UseTool(FootPlayer player, ItemId tool, bool aim)
    {
        if (Mine is not { } s || MyEntry is not { } e) { player.Announce("Versteckis only", false); return; }
        switch (tool)
        {
            case ItemId.VsTag:
                if (TagPoint(player) is { } at) RpcId(1, MethodName.RequestTag, at.E, at.N, (float)at.Alt);
                break;
            case ItemId.VsSniff:
                RpcId(1, MethodName.RequestSniff);
                break;
            case ItemId.VsDecoy:
                RpcId(1, MethodName.RequestDecoy);
                break;
            case ItemId.VsSprint:
                Sprint();
                break;
            case ItemId.VsMoo:
                RpcId(1, MethodName.RequestMoo);
                break;
            case ItemId.VsDisguise:
                if (aim && CopyAimed(player) is { } kind) Disguise(kind);
                else _hud?.OpenRadial(s.Kinds, e.Prop);
                break;
        }
    }

    /// <summary>Asks to become <paramref name="kind"/>.</summary>
    public void Disguise(VsProp kind)
    {
        if (Mine == null) return;
        RpcId(1, MethodName.RequestDisguise, (int)kind);
        GD.Print($"[vs] asking to be {VsProps.A(kind)}");
    }

    private static readonly RayQuery AimRay = new();

    /// <summary>Where a tag lands: what the crosshair is on within reach, else the ground at reach along the aim.</summary>
    public Core.GlobalPos? TagPoint(FootPlayer player)
    {
        var (from, aim) = ItemController.AimFrom(player, VsRules.TagReach + 1.5f);
        var hit = AimRay.Cast(player.GetWorld3D().DirectSpaceState, from, from + aim * (VsRules.TagReach + 1.5f), uint.MaxValue, player.SelfExclude);
        Vector3 at;
        if (hit.Count > 0) at = hit["position"].AsVector3();
        else
        {
            var flat = new Vector3(aim.X, 0, aim.Z);
            at = player.GlobalPosition + (flat.LengthSquared() > 1e-4f ? flat.Normalized() : -player.GlobalTransform.Basis.Z) * VsRules.TagReach;
        }
        return _origin.ToGlobal(at);
    }

    /// <summary>The decor prop aimed at within 4 m (Aim + Use copies it), or null.</summary>
    private VsProp? CopyAimed(FootPlayer player)
    {
        if (_site == null) return null;
        var (from, aim) = ItemController.AimFrom(player, VsRules.CopyReach);
        VsDecor? best = null;
        float bestScore = float.MaxValue;
        foreach (var d in _site.Decor)
        {
            var w = _origin.ToWorld(d.E, d.N, 0) with { Y = from.Y };
            var to = w - from;
            float along = to.Dot(aim);
            if (along < 0 || along > VsRules.CopyReach + 1) continue;
            float off = (to - aim * along).Length();
            if (off < 1.2f && off < bestScore) { bestScore = off; best = d; }
        }
        return best?.Kind;
    }

    // ==== objectives: hold E / Y at the bell, the fire; carry the can =============================

    /// <summary>
    /// <see cref="FootPlayer.TryInteract"/>, outdoors (#1268): a hider in Seek at an objective starts its hold; a disguised
    /// hider does nothing else (no door, no vehicle, no picker). False when this player is not in a round.
    /// </summary>
    public static bool TryInteract(FootPlayer player)
    {
        if (Instance is not { InGame: true } m || m.Mine is not { Phase: VsPhase.Hide or VsPhase.Seek } s || m.MyEntry is not { } e || player != m.LocalPlayer())
            return false;
        if (e.Role != VsRole.Hider || e.Caught) return false;
        if (m.Offer is { } offer && s.Phase == VsPhase.Seek)
        {
            m.BeginHold(s, offer.Site);
            return true;
        }
        // a prop opens no door and drives no car
        return e.Disguised;
    }

    private void BeginHold(VsState s, int site)
    {
        var o = s.Objectives[site];
        if (o.Site == VsSite.Can && o.Carrier == Me)
        {
            RpcId(1, MethodName.RequestObjective, site, 1);   // on the bench
            _holding = -1;
            return;
        }
        _holding = site;
        _holdFrom = GameClock.Now;
        RpcId(1, MethodName.RequestObjective, site, 0);
        GD.Print($"[vs] hold {o.Site}");
    }

    private void ObjectiveTick(FootPlayer? me, VsState? s, VsEntrant? mine)
    {
        Offer = null;
        if (me == null || !InGame || s is not { Phase: VsPhase.Seek } || mine is not { Role: VsRole.Hider, Caught: false }) { _holding = -1; return; }
        var (e, n) = _origin.ToLv95(me.GlobalPosition);
        float reach = VsRules.SiteReach;
        for (int i = 0; i < s.Objectives.Count; i++)
        {
            var o = s.Objectives[i];
            if (o.Done) continue;
            bool carrying = o.Site == VsSite.Can && o.Carrier == Me;
            if (o.Site == VsSite.Can && o.Carrier != 0 && !carrying) continue;
            double se = o.Site == VsSite.Can && !carrying ? o.FromE : o.E, sn = o.Site == VsSite.Can && !carrying ? o.FromN : o.N;
            if ((se - e) * (se - e) + (sn - n) * (sn - n) > reach * reach) continue;
            string act = o.Site switch
            {
                VsSite.Bell => "Hold {interact_mount} to ring the church bell",
                VsSite.Fire => "Hold {interact_mount} to light the Höhenfeuer",
                _ => carrying ? "{interact_mount} sets the milk can on the bench" : "{interact_mount} takes the milk can (carry it to the Milchbänkli)",
            };
            float progress = _holding == i && o.Site != VsSite.Can ? (float)Math.Clamp((GameClock.Now - _holdFrom) / VsRules.HoldSeconds(o.Site), 0, 1) : 0f;
            Offer = (i, act, progress);
            break;
        }
        if (_holding < 0 || _holding >= s.Objectives.Count) return;
        var held = s.Objectives[_holding];
        if (held.Site == VsSite.Can) return;   // a carry is not a hold: it goes on until the bench
        bool down = ForceHold || PlayerInput.Held(PlayerInput.InteractMount);
        if (!down || Offer?.Site != _holding)
        {
            RpcId(1, MethodName.RequestObjective, _holding, 2);
            _holding = -1;
            return;
        }
        if (GameClock.Now - _holdFrom >= VsRules.HoldSeconds(held.Site))
        {
            RpcId(1, MethodName.RequestObjective, _holding, 1);
            _holding = -1;
        }
    }

    /// <summary>For probes: a hold started at a site without the key.</summary>
    public void ProbeHold(int site)
    {
        if (Mine is { } s) BeginHold(s, site);
    }
}
