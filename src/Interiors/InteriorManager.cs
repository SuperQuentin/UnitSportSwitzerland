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

    private async void EnterOffline(string key)
    {
        try
        {
            var layout = await GetOrCreate(key);
            if (layout == null) { Refused("This door is locked."); return; }
            SetPlayerSpace(MyId, key);
            await Arrive(layout);
        }
        catch (Exception e)
        {
            GD.PushError($"[interior] entering {key} failed: {e}");
            Refused("This door is stuck.");
        }
    }

    /// <summary>Whether the player is close enough to the front door to walk out (what the prompt calls "Leave").</summary>
    public bool AtExit(FootPlayer player)
    {
        if (!player.Indoors || _current == null || _currentNode == null) return false;
        var local = _currentNode.ToLocal(player.GlobalPosition);
        return local.Y < _current.StoreyHeight - 0.5f
            && new Vector2(local.X, local.Z).DistanceTo(new Vector2(_current.EntryX, -_current.Depth / 2)) <= ExitReach;
    }

    /// <summary>E while inside: out through the front door if near it, otherwise a hint. Always consumes the press.</summary>
    public bool TryExit(FootPlayer player)
    {
        if (!player.Indoors || _current == null || _currentNode == null) return player.Indoors;
        var local = _currentNode.ToLocal(player.GlobalPosition);
        var door = new Vector2(_current.EntryX, -_current.Depth / 2);
        if (local.Y > _current.StoreyHeight - 0.5f
            || new Vector2(local.X, local.Z).DistanceTo(door) > ExitReach)
        {
            Hint("The way out is the front door, on the ground floor.");
            return true;
        }
        Leave(player, silent: false);
        return true;
    }

    /// <summary>Takes the player out through the front door, or just drops the state (a teleport).</summary>
    public void Leave(FootPlayer player, bool silent)
    {
        var layout = _current;
        if (!silent && layout != null && Origin != null)
        {
            var tile = new TileId(0, 0);
            BuildingKey.TryParse(layout.Key, out var key);
            tile = key.Tile;
            var tileOrigin = Origin.ToWorld(tile.MinE, tile.MaxN, 0);
            var door = tileOrigin + new Vector3(layout.DoorX, layout.DoorY, layout.DoorZ);
            var outward = new Vector3(layout.DoorOutX, 0, layout.DoorOutZ);
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

    private async Task Arrive(InteriorLayout layout)
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

        var entry = _currentNode.GlobalTransform * new Vector3(layout.EntryX, 0.05f, -layout.Depth / 2 + 1.1f);
        var into = _currentNode.GlobalTransform.Basis * Vector3.Back;
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
                var local = _currentNode.ToLocal(p.GlobalPosition);
                if (local.Y < _current.StoreyHeight - 0.5f
                    && new Vector2(local.X, local.Z).DistanceTo(new Vector2(_current.EntryX, -_current.Depth / 2)) <= ExitReach)
                    text = InputHints.Prompt(PlayerInput.InteractMount, "Leave");
                else text = Loot.LootService.Instance?.PromptFor(p);
            }
            else if (!p.Indoors && DoorIndex.Nearest(p.GlobalPosition, DoorReach) != null)
                text = InputHints.Prompt(PlayerInput.InteractMount, "Enter");
        }
        _prompt.Visible = text != null;
        if (text != null) _prompt.Text = text;
    }

    // ---- server ----------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private async void RequestEnter(string key)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        try
        {
            var layout = BuildingKey.TryParse(key, out _) ? await GetOrCreate(key) : null;
            if (layout == null || Origin == null) { RpcId(sender, MethodName.EnterRefused, "This door is locked."); return; }

            // the request must come from the doorstep; the position is the server's relayed copy
            if (Players?.GetNodeOrNull<Node3D>(sender.ToString()) is { } body)
            {
                BuildingKey.TryParse(key, out var k);
                var door = Origin.ToWorld(k.Tile.MinE, k.Tile.MaxN, 0) + new Vector3(layout.DoorX, layout.DoorY, layout.DoorZ);
                var d = body.GlobalPosition - door;
                if (new Vector2(d.X, d.Z).Length() > ServerDoorReach)
                {
                    RpcId(sender, MethodName.EnterRefused, "Too far from the door.");
                    return;
                }
            }
            Broadcast(sender, key);
            RpcId(sender, MethodName.EnterGranted, layout.ToCompressed());
        }
        catch (Exception e)
        {
            GD.PushError($"[interior] {key} for peer {sender}: {e}");
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
    private async void EnterGranted(byte[] data)
    {
        var layout = InteriorLayout.FromCompressed(data);
        if (layout == null) { Refused("This door is stuck."); return; }
        await Arrive(layout);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void EnterRefused(string why) => Refused(why);

    // ---- plans: memory, disk, generator --------------------------------------------------------

    /// <summary>
    /// The stored plan for a building, or a new one generated and stored now. Concurrent requests
    /// for the same building share one generation, so two players at one door get one house.
    /// </summary>
    public Task<InteriorLayout?> GetOrCreate(string key)
    {
        if (_cache.TryGetValue(key, out var hit)) return Task.FromResult<InteriorLayout?>(hit);
        if (_pending.TryGetValue(key, out var running)) return running;
        var source = Source!;
        string dir = _storeDir;
        var task = Task.Run(() => LoadOrGenerate(source, dir, key));
        _pending[key] = task;
        return Finish(key, task);
    }

    private async Task<InteriorLayout?> Finish(string key, Task<InteriorLayout?> task)
    {
        try
        {
            var layout = await task;
            if (layout != null) _cache[key] = layout;
            return layout;
        }
        finally { _pending.Remove(key); }
    }

    public static async Task<InteriorLayout?> LoadOrGenerate(IChunkSource source, string storeDir, string keyText)
    {
        if (!BuildingKey.TryParse(keyText, out var key)) return null;
        var tile = await source.LoadBuildingsAsync(key.Tile);
        if (tile == null || key.Index < 0 || key.Index >= tile.Buildings.Count) return null;
        var building = tile.Buildings[key.Index];

        string path = Path.Combine(storeDir, $"{key.TileE}_{key.TileN}", $"{key.Index}.json");
        if (File.Exists(path) && InteriorLayout.FromJson(await File.ReadAllTextAsync(path)) is { } stored
            && stored.Matches(building))
            return stored;

        var roads = await source.LoadRoadsAsync(key.Tile);
        var grid = await source.LoadChunkAsync(key.Tile);
        var fp = BuildingFootprint.Compute(tile, key.Index, roads, grid);
        if (fp == null) return null;
        var layout = InteriorGenerator.Generate(fp, building);

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
