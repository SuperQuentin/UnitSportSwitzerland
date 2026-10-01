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
    private (int Seed, float Side, float Pace) _zoneKey;
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
        var key = (s.Seed, s.Side, s.Pace);
        if (s.Phase != BrPhase.Idle && (_zone == null || key != _zoneKey))
        {
            _zone = new ZoneSchedule(s.Seed, s.Side, s.Pace);
            _zoneKey = key;
        }
        if (s.Phase == BrPhase.Idle) _feed.Clear();
        if (s.Phase != BrPhase.Idle && _mapFor != (s.AreaE, s.AreaN, s.Side)) BuildMap(s.Area);
        if (before != BrPhase.Ended && s.Phase == BrPhase.Ended && s.Winner == Me && InMatch)
            LocalPlayer()?.Announce("WINNER WINNER RACLETTE DINNER", true);
        GD.Print($"[br] state {s.Phase}, {s.AliveCount}/{s.Entrants.Count} alive");
        StateChanged?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Drop(double e, double n)
    {
        if (_server || LocalPlayer() is not { } me || Origin == null) return;
        if (!InMatch)
        {
            (_returnE, _returnN) = Origin.ToLv95(me.GlobalPosition);
            InMatch = true;
            // the free-roam pack stays on disk and comes back at the end; a match starts with a knife
            if (Inventory() is { } inv)
            {
                inv.BeginMatch();
                inv.Add(ItemId.Knife, 1);
                inv.Add(ItemId.Bandage, 3);
            }
            FootPlayer.StayDown = _ => InMatch && _state.Phase == BrPhase.Playing;
            Permissions.SetRidesLocked(true);
        }
        GD.Print(FormattableString.Invariant($"[br] dropped at {e:F0}/{n:F0}"));
        Teleport(e, n, _state.AreaName);
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
            _watching = killer;
        }
    }

    /// <summary>The match is over for this client: back where it was, with its own things.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Release()
    {
        if (_server || !InMatch) return;
        InMatch = false;
        FootPlayer.StayDown = null;
        Permissions.SetRidesLocked(false);
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
            if (_hooked != null && IsInstanceValid(_hooked)) _hooked.Died -= OnLocalDied;
            _hooked = me;
            if (me != null) me.Died += OnLocalDied;
        }

        var zone = ZoneNow;
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
        RpcId(1, MethodName.ReportDeath, killer, (int)why);
    }

    // ------------------------------------------------------------------------------------
    // spectating
    // ------------------------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent e)
    {
        if (_server || _spectator is not { Current: true } || !e.IsPressed() || e.IsEcho()) return;
        if (e.IsActionPressed("ui_left")) _watchIndex--;
        else if (e.IsActionPressed("ui_right")) _watchIndex++;
        else return;
        _watching = 0;
        GetViewport().SetInputAsHandled();
    }

    private void Spectate(FootPlayer? me)
    {
        var alive = _state.Entrants.Where(x => x.Alive && x.Peer != Me).Select(x => x.Peer).ToList();
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
