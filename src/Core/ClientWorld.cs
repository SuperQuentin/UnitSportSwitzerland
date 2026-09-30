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
    private Audio.Ambience? _ambience;
    private SpectatorCamera? _spectator;
    private FootPlayer? _player;
    private bool _onFoot;
    private bool _networked;
    private World.Traffic? _traffic;
    private Node3D? _players;
    private GpxSession? _gpx;
    private PlaceSearchUi? _places;
    private RideUi? _rides;
    private MainMenu? _menu;
    private ControlsHelp? _help;
    private Items.ItemController? _items;
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
        Audio.SfxBus.Ensure();
        {
            var scArgs = OS.GetCmdlineUserArgs();
            int sc = Array.IndexOf(scArgs, "--soundcheck");
            if (sc >= 0 && sc + 1 < scArgs.Length)
            {
                int code = Audio.Soundcheck.Run(scArgs[sc + 1]);
                GetTree().Quit(code);
                return;
            }
            if (Array.IndexOf(scArgs, "--driftcheck") >= 0)
            {
                GetTree().Quit(Player.DriftCheck.Run());
                return;
            }
        }
        if (Items.InventoryCheck.Requested)
        {
            GetTree().Quit(Items.InventoryCheck.Run());
            return;
        }
        if (Occasions.OccasionProbe.Requested)
        {
            GetTree().Quit(Occasions.OccasionProbe.Run());
            return;
        }
        // after Load, so the saved stick deadzone is what the actions start with
        PlayerInput.Install(this);
        ApplyViewportSettings();
        GameSettings.Changed += ApplyViewportSettings;

        var source = new LocalChunkSource(TerrainPaths.FindChunkDir());
        var manifest = await source.LoadManifestAsync();

        // A fresh clone has no terrain at all: the generated data is 5.3 GB and is not in the
        // repository. That is not fatal. Until the preprocessor is run or a server is joined
        // (which streams everything), a generated valley stands in around the spawn point, so
        // there is something to walk, ride and fly over; real tiles replace it the moment they
        // are available (ChunkManager.RetireFallback). The origin goes on the spawn point too,
        // so the stand-in is not tens of kilometres out in float precision.
        bool hasLocalTerrain = manifest.Tiles.Count > 0;
        ProceduralWorld? generated = null;
        WorldOrigin origin;
        if (hasLocalTerrain)
            origin = new WorldOrigin(manifest.SuggestedOriginLv95.E, manifest.SuggestedOriginLv95.N);
        else
        {
            var (spawnE, spawnN) = SpawnPoint.ParseTarget();
            generated = new ProceduralWorld(spawnE, spawnN);
            origin = new WorldOrigin(spawnE, spawnN);
        }

        _worldOrigin = origin;
        GD.Print($"[world] {manifest.Tiles.Count} tiles, origin LV95 {origin.E}/{origin.N}");

        if (!hasLocalTerrain)
            GD.PushWarning(
                "[world] no terrain data found, showing a generated stand-in world. Generate the "
                + "real one with tools/TerrainPreprocessor, or join a server and it will stream in. "
                + "See the README.");

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

        // The generated stand-in answers only for its own tiles, and only until it is retired;
        // it sits under the cache so a generated tile is not generated twice.
        var fallback = generated != null ? new FallbackChunkSource(streamedSource, generated) : null;

        // Outermost, so a tile decoded once is not decoded again when the rings drop it and pick
        // it back up — which a route that doubles back does constantly.
        _cache = new CachingChunkSource(fallback ?? (IChunkSource)streamedSource);

        _chunks = new ChunkManager { Name = "Terrain" };
        // the auto build cap depends on whether tiles are coming over the wire
        _chunks.Streaming = () => _streamer?.ServerReachable == true;
        _chunks.Initialize(_cache, origin, manifest, material, roadMaterial, buildingMaterial, treeMaterial, waterMaterial);
        if (fallback != null)
        {
            var cache = _cache;
            _chunks.UseFallback(fallback.World.Tiles, retire: () =>
            {
                fallback.Active = false;
                cache.Clear();
                Occasions.OccasionTowns.UseGenerated(null);
            });
        }
        // the towns occasion props go in: places.json, or the generated villages while they stand in
        Occasions.OccasionTowns.UseGenerated(fallback?.World);

        // Anything streamed in an earlier session is on disk but absent from the local
        // manifest, so without this it would be unreachable until a server was joined again.
        ClientTerrainSync.MergeCachedIndex(_chunks, origin);

        AddChild(_chunks);
        Audio.Surfaces.Origin = origin;
        var chunksForAudio = _chunks;
        AddChild(new Audio.ReverbZones(() => GetViewport().GetCamera3D(), () => LocalPlayer?.Indoors == true, chunksForAudio)
            { Name = "ReverbZones" });
        _ambience = new Audio.Ambience(chunksForAudio, () => GetViewport().GetCamera3D())
            { Name = "Ambience", Origin = origin, Volume = GameSettings.Current.AmbienceVolume };
        AddChild(_ambience);
        GameSettings.Changed += () => { if (_ambience != null) _ambience.Volume = GameSettings.Current.AmbienceVolume; };

        // Vehicles left standing in the world. Same node path as on the server, so parking and
        // claiming work over the network; offline it just holds the nodes.
        var vehicles = Vehicles.VehicleManager.Create(this, _chunks);
        vehicles.PlayerPositions = () =>
        {
            var at = new List<Vector3>();
            if (LocalPlayer is { } lp) at.Add(lp.GlobalPosition);
            if (GetViewport().GetCamera3D() is { } cam) at.Add(cam.GlobalPosition);
            return at;
        };

        // Guns on the plane and helicopter. World/Combat on both sides, like World/Vehicles.
        var combat = Combat.CombatManager.Create(this, _chunks, server: false);
        combat.LocalPlayer = () => _onFoot ? LocalPlayer : null;

        // Building interiors: E at a front door. Same node path as the server's, which plans and
        // stores them; offline this client does both.
        var interiors = Interiors.InteriorManager.Create(this, _cache, origin);
        interiors.LocalPlayer = () => _onFoot ? LocalPlayer : null;
        interiors.LocalInsideChanged += inside =>
        {
            // indoors, the whole outside world is overhead and out of sight: stop drawing it
            if (_chunks != null) _chunks.Visible = !inside;
            vehicles.Visible = !inside;
        };

        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.72f, 0.78f, 0.86f),
        };
        AddChild(new WorldEnvironment { Environment = environment });

        // which occasions are running (Halloween, Christmas…): the calendar offline, the server's
        // word online. Before the clock, which reads its sun and sky from it.
        Occasions.OccasionManager.Create(this);
        // their props, dressed onto each tile as its buildings load
        AddChild(new Occasions.OccasionDecor(_chunks, origin, _cache));
        // …the creatures in the air around the camera, and their sounds
        AddChild(new Occasions.OccasionCreatures(_chunks, origin, () => GetViewport().GetCamera3D()));
        AddChild(new Occasions.OccasionAmbience(_chunks, origin, () => GetViewport().GetCamera3D()));
        // …and snow falling round the camera, except indoors
        AddChild(new Occasions.OccasionPrecip(() => LocalPlayer?.Indoors == true));

        // the clock: sun, light colour, sky and night for every shader and the environment
        var chunksForSky = _chunks;
        AddChild(new World.DayNight(environment)
        {
            GroundHeight = p => chunksForSky.TryGetHeight(p, out float y) ? y : null,
        });

        // cars on the roads and trains on the railway, around wherever the view is
        _traffic = new World.Traffic(_chunks, origin)
        {
            Focus = () => GetViewport().GetCamera3D()?.GlobalPosition,
            Obstacles = () => LocalPlayer is { } p ? new[] { p.GlobalPosition } : Array.Empty<Vector3>(),
        };
        AddChild(_traffic);
        if (World.TrafficProbe.ParseArgs() is { Requested: true } tcheck)
        {
            var tcam = new Camera3D { Name = "TrafficCam", Far = GameSettings.Current.CameraFar };
            AddChild(tcam);
            tcam.MakeCurrent();
            _chunks.AddAnchor(tcam);
            var (tE, tN) = SpawnPoint.ParseTarget();
            tcam.Position = origin.ToWorld(tE, tN, 600);
            AddChild(new World.TrafficProbe(_traffic, tcam, tcheck.Shot));
        }

        _spectator = new SpectatorCamera { Name = "SpectatorCamera" };
        AddChild(_spectator);
        _chunks.AddAnchor(_spectator);

        // Start somewhere with something to look at, not at the world origin — after a
        // large import that is usually empty space. "--at E,N" overrides it (LV95 metres).
        // --shot and --probe place the camera themselves, and a spawn drop would fight
        // them for the height.
        bool placedByTool = ShotRunner.ParseArgs() != null || TunnelProbe.ParseArgs() != null
            || FlightProbe.ParseArgs() != null
            || RideProbe.ParseArgs() != null || DriveProbe.ParseArgs().Requested || World.TreeCheck.ParseArgs().Requested
            || Gpx.Cinema.CinemaProbe.ParseArgs() != null
            || RoadStandProbe.Requested() || MantleProbe.Requested()
            || FlightCheckProbe.ParseArgs() != null || Vehicles.VehicleProbe.ParseArgs().Requested
            || Interiors.InteriorProbe.ParseArgs().Requested || Loot.LootProbe.ParseArgs() != null
            || Loot.GatherProbe.ParseArgs().Requested
            || Birds.BirdProbe.ParseArgs().Requested
            || World.TrafficProbe.ParseArgs().Requested
            || Combat.CombatProbe.ParseArgs().Requested
            || Birds.BirdStrikeProbe.ParseArgs().Requested
            || SyncProbe.Requested() || HitboxProbe.Requested();
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

        // M opens the teleport search over any town that has terrain
        _places = PlaceSearchUi.Create(_teleporter);
        AddChild(_places);

        // R picks what you travel as. Same rule as the teleporter: the player node is resolved
        // at the moment of the press, because in multiplayer it is spawned by the server and
        // replaced on a reconnect — holding one from startup would move a node nobody controls.
        _rides = RideUi.Create();
        _rides.ActivePlayer = () => _onFoot ? LocalPlayer : null;
        AddChild(_rides);

        // The inventory is this machine's, not the player node's: it outlives a respawn or a
        // reconnect, and the player it acts on is resolved per frame like the picker's.
        var inventory = Items.InventoryUiProbe.Requested || Items.EconomyProbe.Password != null
            ? Items.Inventory.Scratch() : Items.Inventory.Load();
        // the account claimed cash goes to: the server's online, this machine's offline. Made
        // before the items, whose panel shows the balance from its first frame.
        Items.Bank.Create(this, inventory);
        var items = new Items.ItemController(inventory, origin)
        {
            ActivePlayer = () => _onFoot ? LocalPlayer : null,
        };
        AddChild(items);
        _items = items;
        if (Items.InventoryUiProbe.Requested) AddChild(new Items.InventoryUiProbe(items));
        Vehicles.VehicleManager.Refused += message => items.Ui.Toast(message);

        // F1: every control, from the live bindings; bottom right: the ones that apply here
        _help = ControlsHelp.Create();
        AddChild(_help);
        var prompts = PromptBar.Create();
        prompts.Source = Prompts;
        AddChild(prompts);

        // Scavenging: what the furniture in those interiors holds. Same node path as the server's,
        // which decides who gets what; offline this client does both.
        var loot = Loot.LootService.Create(this);
        loot.Items = items;
        // ...and from the land itself: stone, water, firewood (hold G / pad X outdoors)
        var gathering = new Loot.Gathering(_chunks, origin, items);
        AddChild(gathering);
        // birds around the player, from the real land cover; the shotgun hunts them (J: journal)
        var birds = new Birds.BirdLife(_chunks, origin, items);
        AddChild(birds);

        // occasions: the treat / gift hunt (taken with the gather hold) and the seasonal hat
        AddChild(new Occasions.OccasionHunt());
        AddChild(new Occasions.OccasionHats(() => LocalPlayer, items.Inventory));

        // solid trunks around whatever asks for collision
        var trees = new World.TreeColliders(_chunks, origin);
        AddChild(trees);

        // Everything that kept what it read from the generated stand-in forgets it when real
        // terrain replaces it. The player is put down again: the ground under them just went.
        _chunks.TerrainReplaced += () =>
        {
            Audio.Surfaces.Forget();
            _ambience?.ForgetTiles();
            gathering.Forget();
            _traffic?.Forget();
            trees.Forget();
            LocalPlayer?.RequestReplacement();
        };
        // "--inventory" opens the panel once the player exists, for screenshotting it
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--inventory") >= 0)
            GetTree().CreateTimer(1.5).Timeout += () => items.Ui.Open();

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
        _menu.ControlsRequested += () => _help?.Open();
        // "--controls" opens the F1 overlay, for screenshotting it
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--controls") >= 0)
            GetTree().CreateTimer(1.5).Timeout += () => _help?.Open();
        AddChild(_menu);
        if (MenuCheck.Requested()) AddChild(new MenuCheck(_menu));

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

        if (Vehicles.VehicleProbe.ParseArgs() is { Requested: true } vcheck)
        {
            var (vE, vN) = SpawnPoint.ParseTarget();
            _spectator.Position = origin.ToWorld(vE, vN, 1200);
            AddChild(new Vehicles.VehicleProbe(_chunks, origin, vcheck.Shot));
            return;
        }

        if (Loot.GatherProbe.ParseArgs() is { Requested: true } gcheck)
        {
            _chunks.RemoveAnchor(_spectator);
            AddChild(new Loot.GatherProbe(_chunks, origin, gathering, items, gcheck.Shot));
            return;
        }

        if (Birds.BirdProbe.ParseArgs() is { Requested: true } bcheck)
        {
            _chunks.RemoveAnchor(_spectator);
            AddChild(new Birds.BirdProbe(_chunks, origin, birds, items, bcheck.Shot));
            return;
        }

        if (Loot.LootProbe.ParseArgs() is { } lootEpochs)
        {
            // tables only: no terrain wanted, and quitting mid-stream races the tile workers
            _chunks.RemoveAnchor(_spectator);
            AddChild(new Loot.LootProbe(_cache, _chunks, lootEpochs));
            return;
        }

        if (Interiors.InteriorProbe.ParseArgs() is { Requested: true } icheck)
        {
            var (iE, iN) = SpawnPoint.ParseTarget();
            _spectator.Position = origin.ToWorld(iE, iN, 1200);
            AddChild(new Interiors.InteriorProbe(_chunks, origin, _cache, icheck.Shot));
            return;
        }

        if (Birds.BirdStrikeProbe.ParseArgs() is { Requested: true } scheck)
        {
            var (sE, sN) = SpawnPoint.ParseTarget();
            _spectator.Position = origin.ToWorld(sE, sN, 1200);
            AddChild(new Birds.BirdStrikeProbe(_chunks, origin, birds, scheck.Shot));
            return;
        }

        if (Combat.CombatProbe.ParseArgs() is { Requested: true } ccheck)
        {
            var (cE, cN) = SpawnPoint.ParseTarget();
            _spectator.Position = origin.ToWorld(cE, cN, 1200);
            AddChild(new Combat.CombatProbe(_chunks, origin, ccheck.Shot));
            return;
        }

        if (FlightCheckProbe.ParseArgs() is { } flycheck)
        {
            var (fE, fN) = SpawnPoint.ParseTarget();
            _spectator.Position = origin.ToWorld(fE, fN, 1200);
            AddChild(new FlightCheckProbe(_chunks, origin, flycheck.Kind, flycheck.Shot));
            return;
        }

        if (HitboxProbe.Requested())
        {
            AddChild(new HitboxProbe(_chunks, origin));
            return;
        }

        if (SyncProbe.Requested())
        {
            var (sE, sN) = SpawnPoint.ParseTarget();
            _spectator.Position = origin.ToWorld(sE, sN, 1200);
            AddChild(new SyncProbe(_chunks, origin));
            return;
        }

        if (MantleProbe.Requested())
        {
            var (mE, mN) = SpawnPoint.ParseTarget();
            _spectator.Position = origin.ToWorld(mE, mN, 1200);
            AddChild(new MantleProbe(_chunks, origin));
            return;
        }

        if (RoadStandProbe.Requested())
        {
            var (checkE, checkN) = SpawnPoint.ParseTarget();
            _spectator.Position = origin.ToWorld(checkE, checkN, 1200);
            AddChild(new RoadStandProbe(_chunks, origin));
            return;
        }

        if (DriveProbe.ParseArgs() is { Requested: true } drive)
        {
            var (driveE, driveN) = SpawnPoint.ParseTarget();
            _spectator.Position = origin.ToWorld(driveE, driveN, 1200);
            AddChild(new DriveProbe(_chunks, origin, drive.Shot, drive.Car, drive.Seconds));
            return;
        }

        if (World.TreeCheck.ParseArgs() is { Requested: true } treeCheck)
        {
            var (treeE, treeN) = SpawnPoint.ParseTarget();
            _spectator.Position = origin.ToWorld(treeE, treeN, 1200);
            AddChild(new World.TreeCheck(_chunks, origin, treeCheck.Shot));
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
        if (Items.EconomyProbe.Password != null && _items != null)
            AddChild(new Items.EconomyProbe(_chat, _items.Inventory));

        // World/Race on both sides; the client side puts this player on the grid and times the run
        var race = World.RaceManager.CreateClient();
        race.LocalPlayer = () => LocalPlayer;
        AddChild(race);

        _chatUi = ChatUi.Create(_chat);
        AddChild(_chatUi);

        _chat.Kicked += reason => GD.Print($"[net] kicked: {reason}");

        // Merges the server's tile list so tiles this client never shipped with become
        // streamable, and refuses to stream at all if the two worlds disagree on the origin.
        _terrainSync = new ClientTerrainSync(_streamer!, _chunks!, _worldOrigin!);
        // the sync runs its continuations on the thread pool, and the chat log is UI
        _terrainSync.Status += line =>
            Callable.From(() => _chatUi?.Append(line, ChatKind.System)).CallDeferred();

        // Adopting the server's anchor changes what every world coordinate means, so whatever
        // was placed against the old one has to be put down again.
        _terrainSync.Rebased += () => Callable.From(RespawnAfterRebase).CallDeferred();

        // The town index arrives after this UI was built, so it has to be told to re-read.
        _terrainSync.PlacesReceived += () =>
            Callable.From(() =>
            {
                _places?.ReloadIndex();
                _ambience?.ReloadPlaces();
                Occasions.OccasionTowns.Reload();
            }).CallDeferred();

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
        {
            _chatUi?.Append("Disconnected from the server.", ChatKind.Error);
            Permissions.Reset();
        };

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
        // Actions rather than keys, so Start, Y and the D-pad do what Esc, E and T do. Echo is
        // refused: a held key must not re-open the menu it just closed.
        if (!@event.IsPressed() || @event.IsEcho()) return;

        // ChatUi handles Enter, slash and Esc from _UnhandledKeyInput, which runs first; if
        // it is typing, nothing here should fire.
        if (_chatUi is { IsTyping: true }) return;

        // Esc / Start is the way back to the mode menu. MainMenu consumes it while open, so
        // reaching here means the menu is closed.
        if (@event.IsActionPressed(PlayerInput.Menu))
        {
            _menu?.Open();
            return;
        }
        if (_menu is { IsOpen: true }) return;

        // while the search box has focus, keys belong to it
        if (@event.IsActionPressed(PlayerInput.Teleport))
        {
            _places?.Toggle();
            return;
        }
        if (_places is { IsOpen: true }) return;

        // replay owns the screen and its own keys (R there snaps to roads)
        if (_gpx is { Active: true }) return;

        // RideUi consumes E / R / Y itself while open (from _UnhandledInput, which runs first on
        // its deeper node), so reaching here means it is closed. E acts on what is in front of
        // you: in a vehicle it gets out, beside a parked one it gets in. The picker is R's.
        if (@event.IsActionPressed(PlayerInput.RideMenu))
        {
            _rides?.Open();
            return;
        }
        if (@event.IsActionPressed(PlayerInput.InteractMount))
        {
            if (_onFoot && LocalPlayer is { } lp && lp.TryInteract()) return;
            // A pad has no spare face button for the picker, so Y still opens it when there is
            // nothing to act on. A keyboard player is told where it went instead of being
            // surprised by a menu one step too far from a car.
            if (@event is InputEventJoypadButton) _rides?.Open();
            else if (_onFoot) _items?.Ui.Toast(InputHints.Format("Nothing to interact with here. {ride_menu} opens the travel menu."));
            return;
        }
        if (_rides is { IsOpen: true }) return;

        if (@event.IsActionPressed(PlayerInput.ToggleMode)) ToggleMode();
    }

    private FootPlayer? LocalPlayer => _networked ? GetLocalNetPlayer() : _player;

    /// <summary>
    /// The hints for the prompt bar: what the buttons do in the situation the player is in now.
    /// Short on purpose — the loot, door and gather prompts are already centred on screen, and
    /// the whole list is one F1 away.
    /// </summary>
    private IEnumerable<(string, string)> Prompts()
    {
        if (_menu is { IsOpen: true } || _gpx is { Active: true } || _rides is { IsOpen: true }) yield break;

        // whoever owns the camera on screen: the local player, or a body a probe made itself
        var viewer = (_onFoot ? LocalPlayer : null) ?? GetViewport().GetCamera3D()?.GetParent() as FootPlayer;
        if (viewer == null && GetViewport().GetCamera3D() == _spectator)
        {
            yield return (PlayerInput.ToggleMode, "Walk");
            yield return (PlayerInput.FlyUp, "Up");
            yield return (PlayerInput.FlyDown, "Down");
            yield return (PlayerInput.Teleport, "Map");
        }
        else if (viewer is { } p && IsInstanceValid(p))
        {
            if (p.Vehicle is { IsVehicle: true } vehicle)
            {
                // the engine and "get out" are on the vehicle readout in the same corner
                yield return (PlayerInput.CameraToggle, "Camera");
                if (vehicle is not Flyer && vehicle.CanHop)
                {
                    yield return (PlayerInput.Trick, "Trick (in the air)");
                    if (Rideable.Arcade) yield return (PlayerInput.Boost, "Boost");
                }
            }
            else if (p.Vehicle is { } gear)
            {
                if (p.IsOnFloor()) yield return (PlayerInput.RideMenu, $"Take off the {gear.Label.ToLowerInvariant()}");
                if (gear is not Flyer && gear.CanHop) yield return (PlayerInput.Trick, "Trick (in the air)");
            }
            else if (!p.Indoors)
            {
                if (Vehicles.VehicleManager.Instance?.Nearest(p.GlobalPosition, FootPlayer.EnterReach) is { } parked)
                    yield return (PlayerInput.InteractMount, $"Get in the {parked.Ride.Label.ToLowerInvariant()}");
                yield return (PlayerInput.RideMenu, "Travel");
                yield return (PlayerInput.Inventory, "Inventory");
                yield return (PlayerInput.Teleport, "Map");
                yield return (PlayerInput.ToggleMode, "Fly camera");
            }
            else yield return (PlayerInput.Inventory, "Inventory");
        }
        yield return (PlayerInput.Help, "All controls");
    }

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

    /// <summary>Viewport-level settings: window, 3D render scale and vsync.</summary>
    private void ApplyViewportSettings()
    {
        var s = GameSettings.Current;
        GetViewport().Scaling3DScale = s.RenderScale;
        DisplayServer.WindowSetVsyncMode(s.VSync
            ? DisplayServer.VSyncMode.Enabled
            : DisplayServer.VSyncMode.Disabled);
        ApplyWindow(s);
    }

    private (WindowMode Mode, int W, int H)? _appliedWindow;

    /// <summary>
    /// Window mode and size, touched only when those settings themselves changed: every other
    /// setting also raises <see cref="GameSettings.Changed"/>, and re-applying the saved size then
    /// would snap back a window the player had just dragged to a new size.
    /// </summary>
    private void ApplyWindow(GameSettings s)
    {
        if (DisplayServer.GetName() == "headless") return;
        var wanted = (s.WindowMode, s.WindowWidth, s.WindowHeight);
        if (_appliedWindow == wanted) return;
        _appliedWindow = wanted;

        switch (s.WindowMode)
        {
            case WindowMode.Fullscreen:
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.ExclusiveFullscreen);
                return;
            case WindowMode.Borderless:
                DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
                return;
        }

        if (DisplayServer.WindowGetMode() != DisplayServer.WindowMode.Windowed)
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        if (s.WindowWidth <= 0 || s.WindowHeight <= 0) return;

        int screen = DisplayServer.WindowGetCurrentScreen();
        var usable = DisplayServer.ScreenGetUsableRect(screen);
        var size = new Vector2I(Math.Min(s.WindowWidth, usable.Size.X), Math.Min(s.WindowHeight, usable.Size.Y));
        DisplayServer.WindowSetSize(size);
        DisplayServer.WindowSetPosition(usable.Position + (usable.Size - size) / 2);
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

        // flying out of a house would leave the camera in the void under the terrain
        if (_onFoot && LocalPlayer is { Indoors: true }) return;

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
