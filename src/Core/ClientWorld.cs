using Godot;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Gpx;
using UnitSport.Terrain;

namespace UnitSport.Core;

/// <summary>
/// Client bootstrap: loads the terrain manifest, sets up the chunk manager with the
/// PS1 terrain material, sky/fog environment, and a spectator camera over the valley.
/// </summary>
public partial class ClientWorld : Node3D
{
    private ChunkManager? _chunks;
    private SpectatorCamera? _spectator;
    private FootPlayer? _player;
    private bool _onFoot;
    private bool _networked;
    private Node3D? _players;
    private GpxSession? _gpx;
    private PlaceSearchUi? _places;
    private RideUi? _rides;
    private MainMenu? _menu;
    private Teleporter? _teleporter;
    private ChatManager? _chat;
    private ChatUi? _chatUi;
    private ChunkStreamer? _streamer;
    private NetworkChunkSource? _chunkSource;
    private CachingChunkSource? _cache;
    private ClientTerrainSync? _terrainSync;
    private WorldOrigin? _worldOrigin;

    public override async void _Ready()
    {
        GameSettings.Load();
        ApplyViewportSettings();
        GameSettings.Changed += ApplyViewportSettings;

        var source = new LocalChunkSource(TerrainPaths.FindChunkDir());
        var manifest = await source.LoadManifestAsync();

        // A fresh clone has no terrain at all: the generated data is 5.3 GB and is not in the
        // repository. That is not fatal — the world is simply empty until either the
        // preprocessor is run or a server is joined, which streams everything.
        bool hasLocalTerrain = manifest.Tiles.Count > 0;

        var origin = hasLocalTerrain
            ? new WorldOrigin(manifest.SuggestedOriginLv95.E, manifest.SuggestedOriginLv95.N)
            : WorldOrigin.SwissDefault();

        _worldOrigin = origin;
        GD.Print($"[world] {manifest.Tiles.Count} tiles, origin LV95 {origin.E}/{origin.N}");

        if (!hasLocalTerrain)
            GD.PushWarning(
                "[world] no terrain data found. Generate it with tools/TerrainPreprocessor, "
                + "or join a server and it will stream in. See the README.");

        var material = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://shaders/ps1_terrain.gdshader"),
        };
        var roadMaterial = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://shaders/ps1_road.gdshader"),
        };
        var buildingMaterial = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://shaders/ps1_building.gdshader"),
        };

        var treeMaterial = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://shaders/ps1_tree.gdshader"),
        };

        var waterMaterial = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://shaders/ps1_water.gdshader"),
        };

        // Fog is a setting now (off by default: the far horizon is the point). Every world
        // material carries the uniforms, so the toggle just re-pushes two floats to each.
        var worldMaterials = new[] { material, roadMaterial, buildingMaterial, treeMaterial, waterMaterial };
        foreach (var m in worldMaterials) FogUniforms.Apply(m);
        GameSettings.Changed += () =>
        {
            foreach (var m in worldMaterials) FogUniforms.Apply(m);
            _chunks?.ApplySettings(GameSettings.Current);
            SetCameraFar(GameSettings.Current.CameraFar);
        };

        // The streamer exists even offline. Its fetches short-circuit to null with no peer, so
        // single player is unaffected — but the on-disk cache is still consulted, which means
        // terrain pulled during an earlier multiplayer session stays usable offline.
        _streamer = ChunkStreamer.CreateClient();
        AddChild(_streamer);

        var streamedSource = new NetworkChunkSource(
            source, TerrainPaths.FindChunkDir(), _streamer, TerrainPaths.FindCacheDir());
        _chunkSource = streamedSource;

        // Outermost, so a tile decoded once is not decoded again when the rings drop it and pick
        // it back up — which a route that doubles back does constantly.
        _cache = new CachingChunkSource(streamedSource);

        _chunks = new ChunkManager { Name = "Terrain" };
        // the auto build cap depends on whether tiles are coming over the wire
        _chunks.Streaming = () => _streamer?.ServerReachable == true;
        _chunks.Initialize(_cache, origin, manifest, material, roadMaterial, buildingMaterial, treeMaterial, waterMaterial);

        // Anything streamed in an earlier session is on disk but absent from the local
        // manifest, so without this it would be unreachable until a server was joined again.
        ClientTerrainSync.MergeCachedIndex(_chunks, origin);

        AddChild(_chunks);

        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.72f, 0.78f, 0.86f),
            },
        });

        _spectator = new SpectatorCamera { Name = "SpectatorCamera" };
        AddChild(_spectator);
        _chunks.AddAnchor(_spectator);

        // Start somewhere with something to look at, not at the world origin — after a
        // large import that is usually empty space. "--at E,N" overrides it (LV95 metres).
        // --shot and --probe place the camera themselves, and a spawn drop would fight
        // them for the height.
        bool placedByTool = ShotRunner.ParseArgs() != null || TunnelProbe.ParseArgs() != null
            || FlightProbe.ParseArgs() != null
            || RideProbe.ParseArgs() != null || Gpx.Cinema.CinemaProbe.ParseArgs() != null;
        if (!placedByTool)
        {
            var (spawnE, spawnN) = SpawnPoint.ParseTarget();
            AddChild(new SpawnPoint(_spectator, _chunks, origin, spawnE, spawnN));
        }

        // The teleporter resolves what to move at the moment of the jump — fly camera, local
        // player, or the networked player — rather than capturing one target at startup.
        _teleporter = new Teleporter(_chunks, origin)
        {
            ActiveTarget = () => _onFoot && LocalPlayer is { } player ? player : _spectator,
        };
        AddChild(_teleporter);

        // Tab opens the teleport search over any town that has terrain
        _places = PlaceSearchUi.Create(_teleporter);
        AddChild(_places);

        // E picks what you travel as. Same rule as the teleporter: the player node is resolved
        // at the moment of the press, because in multiplayer it is spawned by the server and
        // replaced on a reconnect — holding one from startup would move a node nobody controls.
        _rides = RideUi.Create();
        _rides.ActivePlayer = () => _onFoot ? LocalPlayer : null;
        AddChild(_rides);

        // F4 records a session's performance to user://perf_logs; F3 shows the live numbers
        var recorder = new PerfRecorder(_chunks, origin,
            () => (_gpx?.Active == true ? "replay" : _onFoot ? "foot" : "fly") + (_networked ? "+net" : ""));
        AddChild(recorder);
        AddChild(new PerfOverlay(_chunks, _cache, recorder));

        // G opens a GPX track for playback; the session owns its own camera and HUD
        _gpx = GpxSession.Create(_chunks, origin, _spectator);
        _gpx.ExitRequested += () => EnterMode(GameMode.Explore);
        AddChild(_gpx);

        _menu = MainMenu.Create();
        _menu.ModeChosen += EnterMode;
        _menu.QuitRequested += () => GetTree().Quit();
        AddChild(_menu);

        // "--settings" opens the settings panel straight away, for screenshotting it
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--settings") >= 0)
            Callable.From(() => _menu.OpenSettings()).CallDeferred();

        // "--menu" forces the picker open even when a mode was named on the command line,
        // which is also how the menu itself gets screenshotted with --shot.
        bool forceMenu = Array.IndexOf(OS.GetCmdlineUserArgs(), "--menu") >= 0;
        if (forceMenu) Callable.From(() => _menu.Open()).CallDeferred();

        // same trick for the ride picker, which is otherwise only reachable by pressing E
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--ridemenu") >= 0)
            Callable.From(() => _rides.Open()).CallDeferred();

        // --gpx <path> may be repeated; each one joins the race as another ghost
        var args = OS.GetCmdlineUserArgs();
        bool gpxFromCommandLine = false;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--gpx")
            {
                string gpxPath = args[i + 1];
                gpxFromCommandLine = true;
                Callable.From(() => _gpx.Load(gpxPath)).CallDeferred();
            }

        // Decide the starting mode before the verification tools run, so a --shot of a
        // --gpx race sees the same world state a player would. ShotRunner then takes the
        // camera back for itself.
        string? host = ParseConnectArg();
        if (host != null) StartNetworking(host);
        else if (gpxFromCommandLine) Callable.From(() => EnterMode(GameMode.GpxReplay)).CallDeferred();
        else if (!forceMenu && !placedByTool) _menu.Open();

        // Pure analysis: it loads the tiles it needs itself, so it neither waits for streaming
        // nor cares where the spectator is.
        if (Gpx.Cinema.CinemaProbe.ParseArgs() is { } cinemaTrack)
        {
            AddChild(new Gpx.Cinema.CinemaProbe(cinemaTrack, origin, streamedSource,
                manifest.Tiles.Select(t => t.Id).ToHashSet()));
            return;
        }

        if (RideProbe.ParseArgs() is { } ride)
        {
            // park the streaming anchor on the spawn so the tile under the rider arrives with
            // collision — without it the probe drops through an empty world and measures gravity
            var (rideE, rideN) = SpawnPoint.ParseTarget();
            _spectator.Position = origin.ToWorld(rideE, rideN, 1200);
            AddChild(new RideProbe(_chunks, origin, ride.Kind, ride.Seconds, ride.Shot));
            return;
        }

        if (TunnelProbe.ParseArgs() is { } probe)
        {
            var inv0 = System.Globalization.CultureInfo.InvariantCulture;
            // park the anchor on the portal so its chunk streams in with collision
            _spectator.Position = origin.ToWorld(
                double.Parse(probe[0], inv0), double.Parse(probe[1], inv0), 1200);
            AddChild(new TunnelProbe(_chunks, origin,
                double.Parse(probe[0], inv0), double.Parse(probe[1], inv0),
                double.Parse(probe[2], inv0)));
            return;
        }

        if (FlightProbe.ParseArgs() is { } fly)
        {
            _spectator.SetProcess(false);
            _spectator.SetProcessUnhandledInput(false);
            Input.MouseMode = Input.MouseModeEnum.Visible;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            AddChild(new FlightProbe(_spectator, _chunks,
                new Vector3(float.Parse(fly[0], inv), float.Parse(fly[1], inv), float.Parse(fly[2], inv)),
                float.Parse(fly[3], inv), float.Parse(fly[4], inv), double.Parse(fly[5], inv)));
            return;
        }

        if (ShotRunner.ParseArgs() is { } shot)
        {
            _spectator.SetProcess(false);
            _spectator.SetProcessUnhandledInput(false);
            Input.MouseMode = Input.MouseModeEnum.Visible;
            // InvariantCulture: this project is developed on a fr-CH machine where the
            // default decimal separator would reject "1500.5"
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            AddChild(new ShotRunner(_spectator,
                new Vector3(float.Parse(shot[0], inv), float.Parse(shot[1], inv), float.Parse(shot[2], inv)),
                float.Parse(shot[3], inv), float.Parse(shot[4], inv), double.Parse(shot[5], inv), shot[6]));
        }
    }

    /// <summary>
    /// Puts the player down again after the world origin moved.
    ///
    /// <para>
    /// Only happens on a client that had no terrain of its own and adopted the server's
    /// anchor. Its position was an offset from a placeholder origin and now means somewhere
    /// else entirely, so the spawn is simply re-run against the new one.
    /// </para>
    /// </summary>
    private void RespawnAfterRebase()
    {
        if (_chunks == null || _worldOrigin == null || _teleporter == null) return;

        var (spawnE, spawnN) = SpawnPoint.ParseTarget();
        _teleporter.TeleportTo(spawnE, spawnN, "spawn");
        _chatUi?.Append("Adopted the server's world; terrain will stream in.", ChatKind.System);
    }

    /// <summary>Switches mode, tearing down whatever the previous one owned.</summary>
    private void EnterMode(GameMode mode)
    {
        if (_chunks == null || _spectator == null || _gpx == null) return;

        // leaving replay always disposes the race; nothing else holds state to drop
        if (_gpx.Active && mode != GameMode.GpxReplay)
        {
            _gpx.SetReturnCamera(_onFoot && LocalPlayer != null ? LocalPlayer.Camera : _spectator);
            _gpx.End();
            ParkExploreAnchor(false);
        }

        switch (mode)
        {
            case GameMode.Explore:
                if (_onFoot && LocalPlayer != null) LocalPlayer.Camera.Current = true;
                else _spectator.Current = true;
                Input.MouseMode = Input.MouseModeEnum.Captured;
                break;

            case GameMode.GpxReplay:
                _gpx.SetReturnCamera(_onFoot && LocalPlayer != null ? LocalPlayer.Camera : _spectator);
                ParkExploreAnchor(true);
                _gpx.Begin();
                break;

            case GameMode.Multiplayer:
                if (!_networked) StartNetworking(_menu!.Host);
                Input.MouseMode = Input.MouseModeEnum.Captured;
                break;
        }

        _menu?.NoteMode(mode);
        GD.Print($"[world] mode: {mode}");
    }

    /// <summary>Whether replay has taken the exploring anchor off the streamer.</summary>
    private bool _exploreAnchorParked;

    /// <summary>
    /// Takes the exploring camera off the streamer while a replay owns the screen.
    ///
    /// <para>
    /// The spectator is registered as a streaming anchor at boot and stays wherever it was left
    /// — the Riddes spawn, usually. A GPX track can be a hundred kilometres away, and every
    /// anchor pulls its own nine-ring box, so leaving it registered meant streaming <b>722</b>
    /// tiles for a run that needs 361, with the other 361 permanently off camera. Worse for the
    /// video exporter, which waits for the world to settle before every frame and was therefore
    /// waiting on terrain nobody would ever see.
    /// </para>
    /// </summary>
    private void ParkExploreAnchor(bool parked)
    {
        if (_chunks == null || _spectator == null || parked == _exploreAnchorParked) return;
        _exploreAnchorParked = parked;

        // exactly one of the two is registered at a time — see EnterFoot/LeaveFoot
        Node3D anchor = _onFoot && LocalPlayer != null ? LocalPlayer : _spectator;
        if (parked) _chunks.RemoveAnchor(anchor);
        else _chunks.AddAnchor(anchor);
    }

    private void StartNetworking(string host)
    {
        if (_networked) return;
        _networked = true;

        // Chat lives at World/Chat on both sides: Godot's high-level multiplayer routes RPCs
        // by node path, so the names have to agree with ServerWorld exactly.
        _chat = ChatManager.CreateClient();
        _chat.Teleporter = _teleporter;
        AddChild(_chat);

        _chatUi = ChatUi.Create(_chat);
        AddChild(_chatUi);

        _chat.Kicked += reason => GD.Print($"[net] kicked: {reason}");

        // Merges the server's tile list so tiles this client never shipped with become
        // streamable, and refuses to stream at all if the two worlds disagree on the origin.
        _terrainSync = new ClientTerrainSync(_streamer!, _chunks!, _worldOrigin!);
        _terrainSync.Status += line => _chatUi?.Append(line, ChatKind.System);

        // Adopting the server's anchor changes what every world coordinate means, so whatever
        // was placed against the old one has to be put down again.
        _terrainSync.Rebased += () => Callable.From(RespawnAfterRebase).CallDeferred();

        // The town index arrives after this UI was built, so it has to be told to re-read.
        _terrainSync.PlacesReceived += () =>
            Callable.From(() => _places?.ReloadIndex()).CallDeferred();

        // Same for the horizon: a client that shipped without one gets it during sync.
        _terrainSync.HorizonReceived += () =>
            Callable.From(() => _chunks?.Horizon?.Reload()).CallDeferred();
        AddChild(_terrainSync);

        _players = new Node3D { Name = "Players" };
        _players.ChildEnteredTree += node =>
        {
            if (node.Name == Multiplayer.GetUniqueId().ToString() && node is FootPlayer player)
                Callable.From(() => EnterFootMode(player)).CallDeferred();
        };
        AddChild(_players);
        AddChild(PlayerReplication.CreateSpawner());
        var net = new NetworkManager { Name = "Net" };
        AddChild(net);
        // Handles bare hosts, host:port, and bracketed IPv6 — a plain colon split breaks on
        // the IPv6 address Tailscale hands out alongside the 100.x one.
        var (address, port) = NetworkManager.ParseEndpoint(host);
        net.StartClient(address, port);

        Multiplayer.ConnectedToServer += () =>
        {
            GD.Print($"[world] connected, peer id {Multiplayer.GetUniqueId()}");

            // The server assigns the final name — it deduplicates and sanitises — so this is
            // a request, not a claim.
            string requested = PlayerRegistry.ParseRequestedName();
            _chat?.AnnounceName(requested.Length > 0 ? requested : $"Rider{Multiplayer.GetUniqueId()}");

            // Fire and forget: the world is already playable on local tiles while this runs.
            _ = _terrainSync?.SyncAsync();
        };

        Multiplayer.ConnectionFailed += () => GD.PushError("[world] connection failed");
        Multiplayer.ServerDisconnected += () =>
            _chatUi?.Append("Disconnected from the server.", ChatKind.Error);

        _menu?.NoteMode(GameMode.Multiplayer);
    }

    private static string? ParseConnectArg()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--connect")
                return i + 1 < args.Length && !args[i + 1].StartsWith("--")
                    ? args[i + 1]
                    : "127.0.0.1";
        return null;
    }

    /// <summary>My own networked player node, once the server has spawned it.</summary>
    private FootPlayer? GetLocalNetPlayer() =>
        _players?.GetNodeOrNull<FootPlayer>(Multiplayer.GetUniqueId().ToString());

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;

        // ChatUi handles Enter, slash and Esc from _UnhandledKeyInput, which runs first; if
        // it is typing, nothing here should fire.
        if (_chatUi is { IsTyping: true }) return;

        // Esc is the way back to the mode menu. MainMenu consumes it while open, so
        // reaching here means the menu is closed.
        if (key.PhysicalKeycode == Key.Escape)
        {
            _menu?.Open();
            return;
        }
        if (_menu is { IsOpen: true }) return;

        // while the search box has focus, keys belong to it
        if (key.PhysicalKeycode == Key.Tab)
        {
            _places?.Toggle();
            return;
        }
        if (_places is { IsOpen: true }) return;

        // RideUi consumes E itself while open (from _UnhandledKeyInput, which runs first), so
        // reaching here means it is closed.
        if (key.PhysicalKeycode == Key.E)
        {
            _rides?.Open();
            return;
        }
        if (_rides is { IsOpen: true }) return;

        if (key.PhysicalKeycode == Key.T) ToggleMode();
    }

    private FootPlayer? LocalPlayer => _networked ? GetLocalNetPlayer() : _player;

    private double _sinceStatus;

    public override void _Process(double delta)
    {
        // the loader queues what is in front of the live camera first, whichever camera that is
        if (_chunks != null && GetViewport().GetCamera3D() is { } cam)
            _chunks.ViewDirection = -cam.GlobalTransform.Basis.Z;

        if (!_networked || _players == null) return;
        _sinceStatus += delta;
        if (_sinceStatus < 5) return;
        _sinceStatus = 0;
        foreach (var child in _players.GetChildren())
            if (child is FootPlayer p)
                GD.Print($"[status] player {p.Name} at {p.GlobalPosition:F1}");
    }

    /// <summary>Viewport-level settings: 3D render scale and vsync.</summary>
    private void ApplyViewportSettings()
    {
        var s = GameSettings.Current;
        GetViewport().Scaling3DScale = s.RenderScale;
        DisplayServer.WindowSetVsyncMode(s.VSync
            ? DisplayServer.VSyncMode.Enabled
            : DisplayServer.VSyncMode.Disabled);
    }

    /// <summary>Every camera in the tree, whichever mode owns it: the horizon must not be clipped.</summary>
    private void SetCameraFar(float far)
    {
        foreach (var node in FindChildren("*", "Camera3D", recursive: true, owned: false))
            if (node is Camera3D cam) cam.Far = far;
    }

    /// <summary>Switches between the free spectator camera and the on-foot player (T key).</summary>
    public void ToggleMode()
    {
        if (_chunks == null || _spectator == null) return;

        // Replay owns the camera and has deliberately taken the exploring anchor off the
        // streamer; swapping underneath it would both steal the view and put a second
        // nine-ring box back on the loader.
        if (_gpx is { Active: true }) return;

        if (!_onFoot)
        {
            FootPlayer? player = LocalPlayer;
            if (player == null)
            {
                if (_networked) return; // our player hasn't been spawned by the server yet
                _player = player = new FootPlayer { Name = "Player", Terrain = _chunks };
                AddChild(player);
            }
            EnterFootMode(player);
        }
        else
        {
            var player = LocalPlayer;
            if (player == null) return;
            _spectator.GlobalPosition = player.GlobalPosition + new Vector3(0, 2, 0);
            _spectator.Current = true;
            _chunks.RemoveAnchor(player);
            _chunks.AddAnchor(_spectator);
            _onFoot = false;
            GD.Print($"[world] spectator at {_spectator.GlobalPosition}");
        }
    }

    private void EnterFootMode(FootPlayer player)
    {
        var pos = _spectator!.GlobalPosition;
        float ground = _chunks!.TryGetHeight(pos, out float h) ? h : pos.Y;
        player.GlobalPosition = new Vector3(pos.X, ground + 1f, pos.Z);
        player.Velocity = Vector3.Zero;
        player.Camera.Current = true;
        _chunks.RemoveAnchor(_spectator);
        _chunks.AddAnchor(player);
        _onFoot = true;
        GD.Print($"[world] on foot at {player.GlobalPosition}");
    }
}
