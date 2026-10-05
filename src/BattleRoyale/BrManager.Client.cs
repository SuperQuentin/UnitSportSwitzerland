using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.BattleRoyale;

public partial class BrManager
{
    /// <summary>The client side, for the HUD and the map.</summary>
    public static BrManager? Instance { get; private set; }

    /// <summary>Raised on a client after a new state arrived.</summary>
    public event Action? StateChanged;

    /// <summary>One line of the kill feed, with when it arrived (local seconds).</summary>
    public readonly record struct FeedLine(string Text, bool Mine, double At);
    private readonly List<FeedLine> _feed = new();
    public IReadOnlyList<FeedLine> Feed => _feed;

    public Func<FootPlayer?> LocalPlayer { get; set; } = () => null;
    public Func<Inventory?> Inventory { get; set; } = () => null;
    /// <summary>Puts the local player on the ground at an LV95 point (<c>Core/Teleporter</c>).</summary>
    public Func<double, double, string?, bool> Teleport { get; set; } = (_, _, _) => false;
    public Action<Node3D>? AddAnchor { get; set; }
    public Action<Node3D>? RemoveAnchor { get; set; }
    /// <summary>Where the map image's tiles come from: the terrain's own source.</summary>
    public Func<Terrain.IChunkSource?> Source { get; set; } = () => null;
    /// <summary>The place index, for the town names on the map.</summary>
    public Func<IEnumerable<Terrain.Format.Place>> Places { get; set; } = () => Array.Empty<Terrain.Format.Place>();

    /// <summary>The region's map (<see cref="BrMapImage"/>), once built; null before and outside a match.</summary>
    public Texture2D? MapTexture { get; private set; }
    /// <summary>The waypoint this player set on the full map (zone metres); its own, never sent.</summary>
    public Vector2? Waypoint { get; set; }
    /// <summary>Towns inside the region: name, zone position, buildings.</summary>
    public IReadOnlyList<(string Name, Vector2 At, int Buildings)> Towns { get; private set; } = Array.Empty<(string, Vector2, int)>();
    private (double E, double N, float Side) _mapFor;
    private CancellationTokenSource? _mapCancel;
    private BrMap? _map;

    private ZoneSchedule? _zone;
    private (int Seed, float Side, float Pace, int Field) _zoneKey;
    private BrHud? _hud;
    private ZoneWall? _wall;
    private Camera3D? _spectator;
    private int _watchIndex;
    private long _watching;
    private bool _anchored;

    /// <summary>This client was dropped into the running match and has not been released yet.</summary>
    public bool InMatch { get; private set; }
    private double _returnE, _returnN;
    private FootPlayer? _hooked;
    private double _zoneTick;

    /// <summary>"--br" on a client: it joins every Battle Royale lobby by itself.</summary>
    public static readonly bool AutoJoin = OS.GetCmdlineUserArgs().Contains("--br");
    private int _autoJoined;

    public static BrManager CreateClient(WorldOrigin origin)
    {
        var m = new BrManager { Name = NodeName, Origin = origin };
        Instance = m;
        return m;
    }

    public override void _Ready()
    {
        if (_server) return;
        _hud = new BrHud(this);
        AddChild(_hud);
        _hud.AddChild(new BrCompass(this));
        _hud.AddChild(new Minimap(this));
        _map = new BrMap(this);
        AddChild(_map);
        _wall = new ZoneWall { Name = "ZoneWall", Visible = false };
        AddChild(_wall);
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (_server) Combat.PvpRules.HitRelayed -= OnHit;
        FootPlayer.StayDown = null;
        FootPlayer.CanBeDowned = null;
        FootPlayer.Regenerates = null;
        Combat.PvpRules.Override = null;
    }

    public long Me => Multiplayer.GetUniqueId();
    public BrEntrant? MyEntry => _state.Find(Me);
    public bool MeAlive => MyEntry is { Alive: true };

    /// <summary>The zone right now, or null outside a running match.</summary>
    public ZoneState? ZoneNow => _zone != null && _state.Running ? _zone.At(ClockSync.ServerNow - _state.Started) : null;
    public ZoneSchedule? Zone => _zone;

    /// <summary>A world position as metres east/north of the region centre.</summary>
    public Vector2 ZonePoint(Vector3 world)
    {
        var (e, n) = Origin!.ToLv95(world);
        return new Vector2((float)(e - _state.AreaE), (float)(n - _state.AreaN));
    }

    /// <summary>A zone point back in the world, at height <paramref name="y"/>.</summary>
    public Vector3 WorldPoint(Vector2 zone, float y) =>
        Origin!.ToWorld(_state.AreaE + zone.X, _state.AreaN + zone.Y, 0) with { Y = y };

    /// <summary>Your living team-mates where this client draws them (zone metres, heading), for the maps and the compass (#231).</summary>
    public IEnumerable<(string Name, Vector2 At, Vector2 Heading)> Mates()
    {
        if (Origin == null) yield break;
        foreach (var m in _state.MatesOf(Me))
            if (m.Alive && GetNodeOrNull<FootPlayer>("../Players/" + m.Peer) is { } body)
                yield return (m.Downed ? $"{m.Name} (down)" : m.Name, ZonePoint(body.GlobalPosition), new Vector2(-Mathf.Sin(body.NetYaw), Mathf.Cos(body.NetYaw)));
    }

    // ------------------------------------------------------------------------------------
    // pings (#469): a point marked for the team, for a few seconds
    // ------------------------------------------------------------------------------------

    /// <summary>A ping as this client keeps it: who, where (zone metres; altitude, NaN when marked on the map), until when (local seconds).</summary>
    public readonly record struct BrPing(long Peer, string Name, Vector2 At, double Alt, double Until);

    /// <summary>How long a ping shows.</summary>
    public const double PingSeconds = 8;

    private readonly List<BrPing> _pings = new();
    private static readonly Core.RayQuery PingRay = new();

    private static double LocalSeconds => Time.GetTicksMsec() / 1000.0;

    /// <summary>The team's pings still showing, one per player at most.</summary>
    public IReadOnlyList<BrPing> Pings
    {
        get
        {
            _pings.RemoveAll(p => p.Until < LocalSeconds);
            return _pings;
        }
    }

    /// <summary>Whether this player may ping now: alive in a running squad match, out of the plane.</summary>
    public bool CanPing => InMatch && MeAlive && !_aboard && _state.Phase == BrPhase.Playing && MyEntry is { Team: not 0 };

    /// <summary>Marks what the crosshair is on, up to 2 km out (the <c>ping</c> action). False when there is nothing to mark.</summary>
    public bool PingCrosshair()
    {
        if (!CanPing || LocalPlayer() is not { } me || Origin == null) return false;
        // the guns' aim: from the eye at what the crosshair is on, in either view
        var (from, aim) = Items.ItemController.AimFrom(me, 2000f);
        var hit = PingRay.Cast(me.GetWorld3D().DirectSpaceState, from, from + aim * 2000f, uint.MaxValue, me.SelfExclude);
        if (hit.Count == 0) return false;
        var at = Origin.ToGlobal(hit["position"].AsVector3());
        RpcId(1, MethodName.RequestPing, at.E, at.N, at.Alt);
        return true;
    }

    /// <summary>Marks a point of the full map (zone metres): on the ground when its tile is loaded here.</summary>
    public bool PingMap(Vector2 zone)
    {
        if (!CanPing || Origin == null) return false;
        var world = WorldPoint(zone, 0f);
        double alt = LocalPlayer()?.Terrain is { } t && t.TryGetHeight(world, out float h) ? Origin.ToGlobal(world with { Y = h }).Alt : double.NaN;
        RpcId(1, MethodName.RequestPing, _state.AreaE + zone.X, _state.AreaN + zone.Y, alt);
        return true;
    }

    /// <summary>Where a ping stands in this world: its altitude, else the ground here; null when neither is known.</summary>
    public Vector3? PingWorld(BrPing p)
    {
        if (Origin == null) return null;
        if (double.IsFinite(p.Alt)) return Origin.ToWorld(_state.AreaE + p.At.X, _state.AreaN + p.At.Y, p.Alt);
        var world = WorldPoint(p.At, 0f);
        return LocalPlayer()?.Terrain is { } t && t.TryGetHeight(world, out float h) ? world with { Y = h } : null;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Pinged(long from, double e, double n, double alt)
    {
        if (_server) return;
        string name = _state.Find(from)?.Name ?? "?";
        // one ping a player: a new one replaces the last
        _pings.RemoveAll(p => p.Peer == from);
        _pings.Add(new BrPing(from, name, new Vector2((float)(e - _state.AreaE), (float)(n - _state.AreaN)), alt, LocalSeconds + PingSeconds));
        Effect(BrSounds.Ping, -6f);
        GD.Print(FormattableString.Invariant($"[br] ping from {name} at {e:F0}/{n:F0}"));
    }

    /// <summary>How far the minimap's radar picks up other entrants (#359).</summary>
    public const float RadarRange = 80f;

    /// <summary>
    /// Living opponents within <see cref="RadarRange"/> of me, as map points, for the minimap's radar
    /// (#359): not team-mates (they have their arrows), and not anyone hidden under a camo net or
    /// inside a hay hideout (<see cref="Build.Gadgets.Hidden"/>).
    /// </summary>
    public IEnumerable<Vector2> Nearby()
    {
        if (Origin == null || !InMatch || GetNodeOrNull<FootPlayer>("../Players/" + Me) is not { } me) yield break;
        var mates = _state.MatesOf(Me).Select(m => m.Peer).ToHashSet();
        foreach (var e in _state.Entrants)
        {
            if (!e.Alive || e.Peer == Me || mates.Contains(e.Peer)) continue;
            if (GetNodeOrNull<FootPlayer>("../Players/" + e.Peer) is not { } body) continue;
            var d = body.GlobalPosition - me.GlobalPosition;
            if (new Vector2(d.X, d.Z).Length() > RadarRange || Build.Gadgets.Hidden(body.GlobalPosition)) continue;
            yield return ZonePoint(body.GlobalPosition);
        }
    }

    /// <summary>Spectating: the player watched, else 0.</summary>
    public long Watching => _spectator is { Current: true } ? _watching : 0;

    // ------------------------------------------------------------------------------------
    // RPCs from the server
    // ------------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetState(string json)
    {
        if (_server || BrState.FromJson(json) is not { } s) return;
        var before = _state.Phase;
        _state = s;
        var key = (s.Seed, s.Side, s.Pace, s.Field);
        if (s.Phase != BrPhase.Idle && (_zone == null || key != _zoneKey))
        {
            _zone = s.Zone();
            _zoneKey = key;
        }
        if (s.Phase == BrPhase.Idle) { _feed.Clear(); _pings.Clear(); }
        // "--br" (#231): join every lobby as it opens, and the one already open on arrival
        if (AutoJoin && s.Phase is BrPhase.Lobby or BrPhase.Countdown && s.Find(Me) == null && _autoJoined != s.Seed
            && GetNodeOrNull<ChatManager>("../" + ChatManager.NodeName) is { } chat)
        {
            _autoJoined = s.Seed;
            GD.Print($"[br] --br: joining the lobby in {s.AreaName}");
            chat.Send("/br join");
        }
        SetMatchLoot(s);
        // the server's rule for who may hurt whom, here too: aerial rounds are applied by their
        // victim's own client (Combat/CombatManager), which must not take a team-mate's (#455)
        Combat.PvpRules.Override = s.Phase == BrPhase.Playing ? Allowed : null;   // as the server sets it
        if (s.Phase != BrPhase.Idle && _mapFor != (s.AreaE, s.AreaN, s.Side)) BuildMap(s.Area);
        if (before != BrPhase.Ended && s.Phase == BrPhase.Ended && InMatch && (s.Winner == Me || s.WinnerTeam != 0 && s.Find(Me)?.Team == s.WinnerTeam))
        {
            LocalPlayer()?.Announce("WINNER WINNER RACLETTE DINNER", true);
            Sting(BrSounds.Win, 0f);
        }
        GD.Print($"[br] state {s.Phase}, {s.AliveCount}/{s.Entrants.Count} alive");
        StateChanged?.Invoke();
    }

    /// <summary>Into the match at GO (<see cref="Board"/>): the lent pack, no revive, no travel menu.</summary>
    private void EnterMatch(FootPlayer me)
    {
        if (!InMatch)
        {
            (_returnE, _returnN) = Origin!.ToLv95(me.GlobalPosition);
            InMatch = true;
            // the free-roam pack stays on disk and comes back at the end; a match starts with a knife
            if (Inventory() is { } inv)
            {
                inv.BeginMatch();
                inv.Add(ItemId.Knife, 1);
                inv.Add(ItemId.Bandage, 3);
                // building (#274): a hammer and enough planks for a first wall or two
                inv.Add(ItemId.Hammer, 1);
                inv.Add(ItemId.WoodPlanks, 15);
            }
            FootPlayer.StayDown = _ => InMatch && _state.Phase == BrPhase.Playing;
            // squads (#475): down, not out, while a team-mate stands to pick you up
            FootPlayer.CanBeDowned = _ => InMatch && _state.Phase == BrPhase.Playing && _state.MateStanding(Me);
            // no regeneration in a match (#455): bandages and kits are the only way back
            FootPlayer.Regenerates = _ => !(InMatch && _state.Phase == BrPhase.Playing);
            Permissions.SetInMatch(true);
        }
    }

    // ------------------------------------------------------------------------------------
    // down, not out (#475)
    // ------------------------------------------------------------------------------------

    /// <summary>Seconds a team-mate holds Interact over a downed one to stand them up.</summary>
    public const float ReviveSeconds = 5f;

    /// <summary>How close to a downed team-mate to revive them, m.</summary>
    public const float ReviveReach = 2.2f;

    /// <summary>The downed team-mate being revived now and how far along, 0..1; null when nobody is.</summary>
    public (string Name, float Progress)? Reviving { get; private set; }

    /// <summary>For probes: hold Interact without a key (<c>BrProbe</c>).</summary>
    public bool ForceInteract { get; set; }

    private long _reviveTarget;
    private float _reviveHeld;
    private bool _reviveSent;

    private void OnLocalDowned(long attacker) => RpcId(1, MethodName.ReportDowned, attacker);

    /// <summary>Holding Interact over a downed team-mate: after <see cref="ReviveSeconds"/>, ask the server.</summary>
    private void ReviveTick(FootPlayer? me, double delta)
    {
        long target = 0;
        string name = "";
        if (InMatch && MeAlive && me is { Downed: false } && _state.Phase == BrPhase.Playing
            && (ForceInteract || PlayerInput.Held(PlayerInput.InteractMount)))
            foreach (var m in _state.MatesOf(Me))
                if (m is { Alive: true, Downed: true } && GetNodeOrNull<FootPlayer>("../Players/" + m.Peer) is { } body
                    && body.GlobalPosition.DistanceTo(me.GlobalPosition) <= ReviveReach)
                {
                    target = m.Peer;
                    name = m.Name;
                    break;
                }
        if (target == 0 || target != _reviveTarget)
        {
            _reviveTarget = target;
            _reviveHeld = 0f;
            _reviveSent = false;
        }
        if (target == 0)
        {
            Reviving = null;
            return;
        }
        _reviveHeld += (float)delta;
        Reviving = (name, Mathf.Clamp(_reviveHeld / ReviveSeconds, 0f, 1f));
        if (_reviveHeld >= ReviveSeconds && !_reviveSent)
        {
            _reviveSent = true;
            RpcId(1, MethodName.RequestRevive, target);
        }
    }

    /// <summary>A team-mate stood this player up (the server checked it).</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Revived()
    {
        if (_server) return;
        LocalPlayer()?.ReviveInPlace();
        GD.Print("[br] revived");
    }

    /// <summary>The whole team is down: out now, as if bled out.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void OutNow()
    {
        if (_server) return;
        if (LocalPlayer() is { Downed: true } me) me.FinishDowned();
        GD.Print("[br] out: the whole team is down");
    }

    /// <summary>The feed: someone went down, or was picked up.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void DownNews(long victim, long other, bool revived)
    {
        if (_server) return;
        string v = _state.Find(victim)?.Name ?? "?", o = _state.Find(other)?.Name ?? "";
        string text = revived ? $"{o} revived {v}" : o.Length > 0 ? $"{o} downed {v}" : $"{v} is down";
        _feed.Add(new FeedLine(text, victim == Me || other == Me, Time.GetTicksMsec() / 1000.0));
        if (_feed.Count > 8) _feed.RemoveAt(0);
        if (!revived && other == Me && victim != Me) LocalPlayer()?.Announce($"KNOCKED {v.ToUpperInvariant()} DOWN", true);
        GD.Print($"[br] {(revived ? "revive" : "down")}: {text}");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Eliminated(long victim, long killer, int cause, int place)
    {
        if (_server) return;
        string v = _state.Find(victim)?.Name ?? "?", k = _state.Find(killer)?.Name ?? "";
        string text = (BrOut)cause switch
        {
            BrOut.Zone => k.Length > 0 ? $"{v} ✕ zone (fleeing {k})" : $"{v} ✕ zone",
            BrOut.Fell => $"{v} ✕ fall",
            BrOut.Disconnected => $"{v} left",
            _ => k.Length > 0 ? $"{k} ✕ {v}" : $"{v} ✕",
        };
        _feed.Add(new FeedLine(text, victim == Me || killer == Me, Time.GetTicksMsec() / 1000.0));
        if (_feed.Count > 8) _feed.RemoveAt(0);
        if (killer == Me && victim != Me) LocalPlayer()?.Announce($"ELIMINATED {v.ToUpperInvariant()}", true);
        if (victim == Me)
        {
            LocalPlayer()?.Announce($"YOU PLACED #{place}", false);
            Sting(BrSounds.Out);
            _watching = killer;
        }
    }

    /// <summary>The match is over for this client: back where it was, with its own things.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Release()
    {
        if (_server || !InMatch) return;
        InMatch = false;
        LeaveHold(LocalPlayer());
        ShowHidden();
        FootPlayer.StayDown = null;
        FootPlayer.CanBeDowned = null;
        FootPlayer.Regenerates = null;
        Permissions.SetInMatch(false);
        StopSpectating();
        Inventory()?.EndMatch();
        Waypoint = null;
        _map?.SetOpen(false);
        if (LocalPlayer() is { } me && me.Eliminated) me.Respawn(me.GlobalPosition);
        Teleport(_returnE, _returnN, "back from the Battle Royale");
        GD.Print("[br] released");
    }

    // ------------------------------------------------------------------------------------
    // the map
    // ------------------------------------------------------------------------------------

    /// <summary>Starts building the region's map image; it shows up in the minimap and on M once done.</summary>
    private async void BuildMap(BrArea area)
    {
        _mapFor = (area.E, area.N, area.Side);
        MapTexture = null;
        Waypoint = null;
        Towns = Places().Where(p => p.Kind == Terrain.Format.PlaceKind.Town && area.Contains(p.E, p.N))
            .Select(p => (p.Name, new Vector2((float)(p.E - area.E), (float)(p.N - area.N)), p.Buildings)).ToList();
        _mapCancel?.Cancel();
        var cancel = _mapCancel = new CancellationTokenSource();
        if (Source() is not { } source) return;
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var bytes = await BrMapImage.BuildAsync(source, area, cancel.Token);
            // back on the main thread (Godot's synchronization context resumes awaits there)
            if (cancel.IsCancellationRequested || !IsInsideTree()) return;
            var image = Image.CreateFromData(BrMapImage.Size, BrMapImage.Size, false, Image.Format.Rgba8, bytes);
            MapTexture = ImageTexture.CreateFromImage(image);
            GD.Print($"[br] map of {area.Name} built in {watch.ElapsedMilliseconds} ms");
            if (OS.GetCmdlineUserArgs().Contains("--brmapsave"))
                image.SavePng(ProjectSettings.GlobalizePath("res://test_output/br_map.png"));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { GD.PushWarning($"[br] map: {e.Message}"); }
    }

    /// <summary>A flare fired (#198): asks the server for a drop here. False when not alive in a running match.</summary>
    public bool CallDrop()
    {
        if (!InMatch || _state.Phase != BrPhase.Playing || !MeAlive) return false;
        RpcId(1, MethodName.RequestDrop);
        return true;
    }

    /// <summary>M during a match: the full map instead of the place search. False when not in a match.</summary>
    public bool ToggleMap()
    {
        if (!InMatch || !_state.Running || _map == null) return false;
        _map.Toggle();
        return true;
    }

    public bool MapOpen => _map?.IsOpen == true;

    /// <summary>
    /// Where the map is centred and which way it faces: the player you are watching, or yourself
    /// with your camera's heading. Zone metres; heading is a direction (east, north).
    /// </summary>
    public (Vector2 Position, Vector2 Heading)? ViewPoint()
    {
        if (Origin == null) return null;
        if (Watching != 0 && GetNodeOrNull<FootPlayer>("../Players/" + Watching) is { } other)
            return (ZonePoint(other.GlobalPosition), new Vector2(-Mathf.Sin(other.NetYaw), Mathf.Cos(other.NetYaw)));
        if (LocalPlayer() is not { } me) return null;
        var fwd = GetViewport().GetCamera3D() is { } cam ? -cam.GlobalTransform.Basis.Z : -me.GlobalTransform.Basis.Z;
        var heading = new Vector2(fwd.X, -fwd.Z);
        return (ZonePoint(me.GlobalPosition), heading.LengthSquared() > 1e-6f ? heading.Normalized() : Vector2.Up);
    }

    // ------------------------------------------------------------------------------------
    // every frame
    // ------------------------------------------------------------------------------------

    private void ClientTick(double delta)
    {
        var me = LocalPlayer();
        if (me != _hooked)
        {
            if (_hooked != null && IsInstanceValid(_hooked))
            {
                _hooked.Died -= OnLocalDied;
                _hooked.WentDown -= OnLocalDowned;
            }
            _hooked = me;
            if (me != null)
            {
                me.Died += OnLocalDied;
                me.WentDown += OnLocalDowned;
            }
        }
        ReviveTick(me, delta);

        FlightTick(me);

        var zone = ZoneNow;
        SoundTick(me, zone);
        if (_wall != null)
        {
            _wall.Visible = zone != null;
            if (zone is { } z && GetViewport().GetCamera3D() is { } cam)
                _wall.Place(WorldPoint(z.Centre, cam.GlobalPosition.Y), z.Radius);
        }

        // the zone hurts this client's own player, like every other damage (health is the owner's)
        _zoneTick += delta;
        if (_zoneTick >= 0.5)
        {
            if (InMatch && me != null && !me.Eliminated && MeAlive && zone is { Dps: > 0 } zz && zz.Outside(ZonePoint(me.GlobalPosition)))
                me.TakeDamage(zz.Dps * (float)_zoneTick, 0, DamageCause.Zone);
            _zoneTick = 0;
        }

        // out: watch who is still in it
        bool spectate = InMatch && _state.Phase == BrPhase.Playing && (me?.Eliminated == true || !MeAlive);
        if (spectate) Spectate(me);
        else if (_spectator is { Current: true }) StopSpectating();

    }

    private void OnLocalDied(long killer, DamageCause cause)
    {
        if (!InMatch || _state.Phase != BrPhase.Playing || !MeAlive) return;
        var why = cause switch
        {
            DamageCause.Zone => BrOut.Zone,
            DamageCause.Fall or DamageCause.Crash => BrOut.Fell,
            _ => killer != 0 ? BrOut.Killed : BrOut.Other,
        };
        GD.Print($"[br] died ({why}), killer {killer}");
        // what you carried goes into your death box (#194): it leaves your pack now
        var inv = Inventory();
        var stacks = new List<ItemStack>();
        if (inv != null)
            for (int i = 0; i < Items.Inventory.Size; i++)
                if (!inv[i].IsEmpty && inv[i].Data == null) stacks.Add(inv[i]);
        RpcId(1, MethodName.ReportDeath, killer, (int)why, stacks.Select(s => (int)s.Id).ToArray(), stacks.Select(s => s.Count).ToArray());
        inv?.Clear();
    }

    // ------------------------------------------------------------------------------------
    // spectating
    // ------------------------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent e)
    {
        if (_server || !e.IsPressed() || e.IsEcho()) return;
        // E in the plane's hold jumps (#207); Space is left alone, it opens the parachute a moment later
        if (_aboard && e.IsActionPressed(PlayerInput.InteractMount))
        {
            JumpOut();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e.IsActionPressed(PlayerInput.Ping) && PingCrosshair())
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_spectator is not { Current: true }) return;
        if (e.IsActionPressed("ui_left")) _watchIndex--;
        else if (e.IsActionPressed("ui_right")) _watchIndex++;
        else return;
        _watching = 0;
        GetViewport().SetInputAsHandled();
    }

    private void Spectate(FootPlayer? me)
    {
        // team-mates first (#231): you follow your own side before the others
        int team = MyEntry?.Team ?? 0;
        var alive = _state.Entrants.Where(x => x.Alive && x.Peer != Me).OrderBy(x => team != 0 && x.Team == team ? 0 : 1)
            .Select(x => x.Peer).ToList();
        if (alive.Count == 0) return;
        if (!alive.Contains(_watching))
        {
            _watchIndex = ((_watchIndex % alive.Count) + alive.Count) % alive.Count;
            _watching = alive[_watchIndex];
        }
        var target = GetNodeOrNull<FootPlayer>("../Players/" + _watching);
        if (_spectator == null)
        {
            _spectator = new Camera3D { Name = "Spectator", Fov = 70, Far = 20000 };
            AddChild(_spectator);
        }
        if (!_spectator.Current)
        {
            _spectator.GlobalPosition = (me?.GlobalPosition ?? Vector3.Zero) + Vector3.Up * 4f;
            _spectator.Current = true;
        }
        if (!_anchored && AddAnchor != null)
        {
            AddAnchor(_spectator);
            _anchored = true;
        }
        if (target == null) return;   // not streamed here yet: hold the view until it arrives
        var behind = new Vector3(Mathf.Sin(target.NetYaw), 0, Mathf.Cos(target.NetYaw)) * 6f + Vector3.Up * 2.8f;
        var want = target.GlobalPosition + behind;
        float k = 1f - Mathf.Exp(-6f * (float)GetProcessDeltaTime());
        _spectator.GlobalPosition = _spectator.GlobalPosition.DistanceTo(want) > 200f ? want : _spectator.GlobalPosition.Lerp(want, k);
        _spectator.LookAt(target.GlobalPosition + Vector3.Up * 1.5f, Vector3.Up);
    }

    private void StopSpectating()
    {
        if (_spectator == null) return;
        if (_anchored) RemoveAnchor?.Invoke(_spectator);
        _anchored = false;
        _spectator.Current = false;
        if (LocalPlayer() is { } me) me.Camera.Current = true;
        _watching = 0;
    }

    /// <summary>Server: two players the interest service must show each other (everyone in a running match).</summary>
    public bool Together(long a, long b) =>
        _state.Running && _state.Find(a) != null && _state.Find(b) != null;
}
