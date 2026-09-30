using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Building interiors, at <c>World/Interiors</c> on the server and on every client — RPCs route by
/// node path, so the name has to match, as for <c>World/Vehicles</c>.
///
/// <para>
/// <b>An interior is an instance by position, not by world.</b> Each one is built directly under
/// its own building, <see cref="InteriorBaseY"/> below sea level, in the same <see cref="World3D"/>
/// as everything else. Real buildings do not overlap, so neither do their interiors; every peer
/// computes the same place from the plan alone, so nothing has to allocate slots; there is one
/// physics space for a hundred players; and the terrain keeps streaming around a player who is
/// inside, so walking back out is instant. Only the interior the local player is in is ever built.
/// </para>
///
/// <para>
/// <b>The server generates and remembers.</b> The first player through a door makes the server
/// plan that building (<see cref="InteriorGenerator"/>) and save the plan under
/// <c>user://interiors/</c>; everyone who enters afterwards — including the same player, and
/// including after a server restart — is sent that stored plan, so they all stand in the same
/// house. Offline the client plays the server's part for itself.
/// </para>
///
/// <para>
/// <b>Who is where</b> is a server table (<see cref="SetPlayerSpace"/>), broadcast to every client.
/// Clients hide remote players who are not in their space, and each player's synchroniser only
/// sends its position to peers in the same space — the interest management that keeps a hundred
/// players from all streaming to all. The table comes from the server rather than from replicated
/// player state because a filter driven by replicated state stops the very update that would lift
/// it.
/// </para>
/// </summary>
public partial class InteriorManager : Node3D
{
    public const string NodeName = "Interiors";

    /// <summary>Where interiors live: far below the lowest ground in Switzerland (193 m).</summary>
    public const float InteriorBaseY = -3000f;

    private const float DoorReach = 1.6f;
    private const float ExitReach = 1.8f;
    /// <summary>Server-side check: generous, since the player's position is a relayed copy.</summary>
    private const float ServerDoorReach = 7f;

    public static InteriorManager? Instance { get; private set; }

    public IChunkSource? Source { get; set; }
    public WorldOrigin? Origin { get; set; }
    /// <summary>Server: the replicated player nodes, for checking a request comes from the doorstep.</summary>
    public Node3D? Players { get; set; }
    /// <summary>Client: the player this client controls, resolved when asked.</summary>
    public Func<FootPlayer?>? LocalPlayer { get; set; }

    /// <summary>Raised on the client when the local player goes in (true) or comes out (false).</summary>
    public event Action<bool>? LocalInsideChanged;

    private readonly Dictionary<long, string> _spaces = new();
    private readonly Dictionary<string, InteriorLayout> _cache = new();
    /// <summary>Door key to the key its plan is stored under, once looked up.</summary>
    private readonly Dictionary<string, string> _planKeys = new();
    private readonly Dictionary<string, Task<InteriorLayout?>> _pending = new();
    private string _storeDir = "";

    // client state
    private InteriorLayout? _current;
    private InteriorNode? _currentNode;
    private bool _requesting;
    private FootPlayer? _entering;
    private static ShaderMaterial? _material;

    // prompt + fade
    private CanvasLayer? _ui;
    private Label? _prompt;
    private ColorRect? _fade;
    private double _promptTimer;
    private double _hintTimer;

    public static InteriorManager Create(Node world, IChunkSource source, WorldOrigin origin)
    {
        var m = new InteriorManager { Name = NodeName, Source = source, Origin = origin };
        world.AddChild(m);
        Instance = m;
        return m;
    }

    public override void _Ready()
    {
        _storeDir = ProjectSettings.GlobalizePath("user://interiors");
        if (DisplayServer.GetName() == "headless" && Multiplayer.IsServer() && Online) return;

        _ui = new CanvasLayer { Name = "InteriorUi", Layer = 9 };
        AddChild(_ui);
        _fade = new ColorRect
        {
            Color = new Color(0, 0, 0, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _fade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _ui.AddChild(_fade);
        _prompt = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _prompt.AddThemeFontSizeOverride("font_size", 18);
        _prompt.AddThemeColorOverride("font_outline_color", Colors.Black);
        _prompt.AddThemeConstantOverride("outline_size", 6);
        _prompt.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _prompt.Position = new Vector2(-150, -140);
        _prompt.Size = new Vector2(300, 30);
        _ui.AddChild(_prompt);
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        DoorIndex.Clear();
    }

    /// <summary>The interior the local player is in, and its node — for probes and tools.</summary>
    public InteriorLayout? Current => _current;
    public InteriorNode? CurrentNode => _currentNode;

    private bool Online => Multiplayer.MultiplayerPeer is { } peer and not OfflineMultiplayerPeer
        && peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected;

    private long MyId => Multiplayer.MultiplayerPeer is { } p and not OfflineMultiplayerPeer ? Multiplayer.GetUniqueId() : 1;

    // ---- who is where ----------------------------------------------------------------------

    public string SpaceOf(long peer) => _spaces.TryGetValue(peer, out var k) ? k : "";

    /// <summary>Whether a remote peer is somewhere this client can see: the same interior, or both outside.</summary>
    public bool SameSpaceAsLocal(long peer) => SpaceOf(peer) == SpaceOf(MyId);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetPlayerSpace(long peer, string key)
    {
        if (key.Length == 0) _spaces.Remove(peer); else _spaces[peer] = key;
    }

    private void Broadcast(long peer, string key)
    {
        SetPlayerSpace(peer, key);
        Rpc(MethodName.SetPlayerSpace, peer, key);
    }

    /// <summary>Server: tells a newly joined client who is already inside where.</summary>
    public void SendTableTo(long peer)
    {
        foreach (var (p, key) in _spaces) RpcId(peer, MethodName.SetPlayerSpace, p, key);
    }

    /// <summary>Server: a player left; they are no longer inside anything.</summary>
    public void ForgetPeer(long peer)
    {
        if (_spaces.ContainsKey(peer)) Broadcast(peer, "");
    }

    // ---- client: entering and leaving ---------------------------------------------------------

    /// <summary>E at a door. False when there is no door in reach, so the caller can try other things.</summary>
    public bool TryEnter(FootPlayer player)
    {
        if (player.Indoors) return false;
        if (DoorIndex.Nearest(player.GlobalPosition, DoorReach) is not { } door) return false;
        if (_requesting) return true;
        _requesting = true;
        _entering = player;
        string key = door.Key.ToString();
        if (Online) RpcId(1, MethodName.RequestEnter, key);
        else EnterOffline(key);
        return true;
    }

    private async void EnterOffline(string door)
    {
        try
        {
            var layout = await GetOrCreate(door);
            if (layout == null) { Refused("This door is locked."); return; }
            SetPlayerSpace(MyId, layout.Key);
            await Arrive(layout, door);
        }
        catch (Exception e)
        {
            GD.PushError($"[interior] entering {door} failed: {e}");
            Refused("This door is stuck.");
        }
    }

    /// <summary>The entrance the player is standing at, on the ground floor, if any.</summary>
    private EntrancePlan? ExitAt(FootPlayer player)
    {
        if (!player.Indoors || _current == null || _currentNode == null) return null;
        var local = _currentNode.ToLocal(player.GlobalPosition);
        if (local.Y > _current.StoreyHeight - 0.5f) return null;
        var at = new Vector2(local.X, local.Z);
        return _current.AllEntrances()
            .Where(e => at.DistanceTo(new Vector2(e.X, e.Z)) <= ExitReach)
            .MinBy(e => at.DistanceTo(new Vector2(e.X, e.Z)));
    }

    /// <summary>Whether the player is close enough to a way in to walk out (what the prompt calls "Leave").</summary>
    public bool AtExit(FootPlayer player) => ExitAt(player) != null;

    /// <summary>E while inside: out through the door they are at, otherwise a hint. Always consumes the press.</summary>
    public bool TryExit(FootPlayer player)
    {
        if (!player.Indoors || _current == null || _currentNode == null) return player.Indoors;
        if (ExitAt(player) == null)
        {
            Hint(_current.AllEntrances().Count > 1
                ? "The ways out are the doors, on the ground floor."
                : "The way out is the front door, on the ground floor.");
            return true;
        }
        Leave(player, silent: false);
        return true;
    }

    /// <summary>Takes the player out through the door they are at (else the main one), or just drops the state (a teleport).</summary>
    public void Leave(FootPlayer player, bool silent)
    {
        var layout = _current;
        if (!silent && layout != null && Origin != null)
        {
            var exit = ExitAt(player) ?? layout.AllEntrances()[0];
            var tile = new TileId(0, 0);
            BuildingKey.TryParse(layout.Key, out var key);
            tile = key.Tile;
            var tileOrigin = Origin.ToWorld(tile.MinE, tile.MaxN, 0);
            var door = tileOrigin + new Vector3(exit.DoorX, exit.DoorY, exit.DoorZ);
            var outward = new Vector3(exit.DoorOutX, 0, exit.DoorOutZ);
            var at = door + outward * 1.1f + Vector3.Up * 0.3f;
            float yaw = Mathf.Atan2(-outward.X, -outward.Z);
            FadeThrough(() => player.LeaveInterior(at, yaw));
        }
        else player.LeaveInterior(null, 0);

        _current = null;
        _currentNode?.QueueFree();
        _currentNode = null;
        if (Online) RpcId(1, MethodName.RequestExit);
        else SetPlayerSpace(MyId, "");
        LocalInsideChanged?.Invoke(false);
    }

    private async Task Arrive(InteriorLayout layout, string door)
    {
        var player = _entering;
        _requesting = false;
        _entering = null;
        if (player == null || !IsInstanceValid(player) || Origin == null) return;

        // mesh arrays off the main thread; a tall block is a few thousand boxes
        var data = await Task.Run(() => InteriorMeshBuilder.Build(layout));
        _material ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/ps1_interior.gdshader") };

        _currentNode?.QueueFree();
        _current = layout;
        _currentNode = InteriorNode.Create(layout, data, _material, PlacementFor(layout, Origin));
        AddChild(_currentNode);

        var way = layout.EntranceFor(door);
        var entry = _currentNode.GlobalTransform * new Vector3(way.X + way.InX * 1.1f, 0.05f, way.Z + way.InZ * 1.1f);
        var into = _currentNode.GlobalTransform.Basis * new Vector3(way.InX, 0, way.InZ);
        float yaw = Mathf.Atan2(-into.X, -into.Z);
        FadeThrough(() => player.EnterInterior(layout.Key, entry, yaw));
        LocalInsideChanged?.Invoke(true);
    }

    /// <summary>The interior's transform: under its building, turned so its front wall faces the street.</summary>
    public static Transform3D PlacementFor(InteriorLayout l, WorldOrigin origin)
    {
        BuildingKey.TryParse(l.Key, out var key);
        var tile = key.Tile;
        var tileOrigin = origin.ToWorld(tile.MinE, tile.MaxN, 0);
        var at = tileOrigin + new Vector3(l.CenterX, 0, l.CenterZ);
        return new Transform3D(new Basis(Vector3.Up, l.Yaw), new Vector3(at.X, InteriorBaseY, at.Z));
    }

    private void Refused(string why)
    {
        _requesting = false;
        _entering = null;
        Hint(why);
    }

    private void Hint(string text)
    {
        if (_prompt == null) return;
        _prompt.Text = text;
        _prompt.Visible = true;
        _hintTimer = 2.5;
    }

    private void FadeThrough(Action swap)
    {
        if (_fade == null) { swap(); return; }
        var tween = CreateTween();
        tween.TweenProperty(_fade, "color:a", 1f, 0.18f);
        tween.TweenCallback(Callable.From(swap));
        tween.TweenInterval(0.1f);
        tween.TweenProperty(_fade, "color:a", 0f, 0.3f);
    }

    public override void _Process(double delta)
    {
        if (_prompt == null) return;
        if (_hintTimer > 0) { _hintTimer -= delta; return; }
        _promptTimer -= delta;
        if (_promptTimer > 0) return;
        _promptTimer = 0.15;

        var p = LocalPlayer?.Invoke();
        string? text = null;
        if (p != null && IsInstanceValid(p) && p.IsViewing && p.Ride == RideKind.OnFoot && !UiFocus.TextEntryActive)
        {
            if (p.Indoors && _current != null && _currentNode != null)
            {
                if (AtExit(p))
                    text = "[E] Leave";
                else text = Loot.LootService.Instance?.PromptFor(p);
            }
            else if (!p.Indoors && DoorIndex.Nearest(p.GlobalPosition, DoorReach) != null)
                text = "[E] Enter";
        }
        _prompt.Visible = text != null;
        if (text != null) _prompt.Text = text;
    }

    // ---- server ----------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private async void RequestEnter(string door)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        try
        {
            var layout = BuildingKey.TryParse(door, out _) ? await GetOrCreate(door) : null;
            if (layout == null || Origin == null) { RpcId(sender, MethodName.EnterRefused, "This door is locked."); return; }

            // the request must come from the doorstep; the position is the server's relayed copy
            if (Players?.GetNodeOrNull<Node3D>(sender.ToString()) is { } body)
            {
                BuildingKey.TryParse(layout.Key, out var k);
                var way = layout.EntranceFor(door);
                var at = Origin.ToWorld(k.Tile.MinE, k.Tile.MaxN, 0) + new Vector3(way.DoorX, way.DoorY, way.DoorZ);
                var d = body.GlobalPosition - at;
                if (new Vector2(d.X, d.Z).Length() > ServerDoorReach)
                {
                    RpcId(sender, MethodName.EnterRefused, "Too far from the door.");
                    return;
                }
            }
            // the space is the interior's, so everyone in the church sees each other whichever
            // door they came in by
            Broadcast(sender, layout.Key);
            RpcId(sender, MethodName.EnterGranted, layout.ToCompressed(), door);
        }
        catch (Exception e)
        {
            GD.PushError($"[interior] {door} for peer {sender}: {e}");
            RpcId(sender, MethodName.EnterRefused, "This door is stuck.");
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestExit()
    {
        if (!Multiplayer.IsServer()) return;
        Broadcast(Multiplayer.GetRemoteSenderId(), "");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private async void EnterGranted(byte[] data, string door)
    {
        var layout = InteriorLayout.FromCompressed(data);
        if (layout == null) { Refused("This door is stuck."); return; }
        await Arrive(layout, door);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void EnterRefused(string why) => Refused(why);

    // ---- plans: memory, disk, generator --------------------------------------------------------

    /// <summary>
    /// The stored plan for the building behind a door, or a new one generated and stored now.
    /// Every door of a church leads to the church's one plan. Concurrent requests for the same
    /// building share one generation, so two players at one door get one house.
    /// </summary>
    public Task<InteriorLayout?> GetOrCreate(string door)
    {
        // a known plan answers synchronously: offline loot takes a whole container in one loop,
        // and relies on each take completing before the next
        if (_planKeys.TryGetValue(door, out var known) && _cache.TryGetValue(known, out var cached))
            return Task.FromResult<InteriorLayout?>(cached);
        return Resolve(door);
    }

    private async Task<InteriorLayout?> Resolve(string door)
    {
        var source = Source!;
        string key = await Task.Run(() => PlanKey(source, door));
        _planKeys[door] = key;
        if (_cache.TryGetValue(key, out var hit)) return hit;
        if (_pending.TryGetValue(key, out var running)) return await running;
        string dir = _storeDir;
        var task = Task.Run(() => LoadOrGenerate(source, dir, key));
        _pending[key] = task;
        return await Finish(key, task);
    }

    /// <summary>The key a door's interior is stored under: the group's primary building, for a door of a group.</summary>
    public static async Task<string> PlanKey(IChunkSource source, string door)
    {
        if (!BuildingKey.TryParse(door, out var k)) return door;
        var tile = await source.LoadBuildingsAsync(k.Tile);
        if (tile == null || k.Index < 0 || k.Index >= tile.Buildings.Count) return door;
        return BuildingTypes.For(tile).GroupOf(k.Index) is { } g ? new BuildingKey(k.TileE, k.TileN, g.Primary).ToString() : door;
    }

    private async Task<InteriorLayout?> Finish(string key, Task<InteriorLayout?> task)
    {
        try
        {
            var layout = await task;
            if (layout != null)
            {
                _cache[key] = layout;
                _planKeys[layout.Key] = key;
            }
            return layout;
        }
        finally { _pending.Remove(key); }
    }

    public static async Task<InteriorLayout?> LoadOrGenerate(IChunkSource source, string storeDir, string keyText)
    {
        if (!BuildingKey.TryParse(keyText, out var key)) return null;
        var tile = await source.LoadBuildingsAsync(key.Tile);
        if (tile == null || key.Index < 0 || key.Index >= tile.Buildings.Count) return null;
        // a group is planned, and stored, once: under its primary building
        if (BuildingTypes.For(tile).GroupOf(key.Index) is { } group)
            key = new BuildingKey(key.TileE, key.TileN, group.Primary);
        var building = tile.Buildings[key.Index];
        string print = InteriorGenerator.GroupPrint(tile, key.Index);

        string path = Path.Combine(storeDir, $"{key.TileE}_{key.TileN}", $"{key.Index}.json");
        if (File.Exists(path) && InteriorLayout.FromJson(await File.ReadAllTextAsync(path)) is { } stored
            && stored.Matches(building, print))
            return stored;

        var roads = await source.LoadRoadsAsync(key.Tile);
        var grid = await source.LoadChunkAsync(key.Tile);
        var layout = InteriorGenerator.Generate(tile, key.Index, roads, grid);
        if (layout == null) return null;

        var problems = InteriorValidator.Validate(layout);
        if (problems.Count > 0)
            GD.PushWarning($"[interior] {keyText}: {string.Join("; ", problems.Take(3))}");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".part";
        await File.WriteAllTextAsync(tmp, layout.ToJson());
        File.Move(tmp, path, overwrite: true);
        return layout;
    }
}

/// <summary>One built interior: its mesh and its collision, placed under its building.</summary>
public partial class InteriorNode : Node3D
{
    public static InteriorNode Create(InteriorLayout layout, InteriorMeshBuilder.MeshData data, Material material, Transform3D placement)
    {
        var node = new InteriorNode { Name = "Interior_" + layout.Key, Transform = placement };

        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);
        node.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = mesh });

        // BackfaceCollision: every wall here is a single face, approached from whichever side the
        // player is on; one-sided, half of them would be walked straight through
        var body = new StaticBody3D { Name = "Body" };
        body.AddChild(new CollisionShape3D
        {
            Shape = new ConcavePolygonShape3D { Data = data.Collision, BackfaceCollision = true },
        });
        node.AddChild(body);
        return node;
    }
}
