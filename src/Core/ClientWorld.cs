using Godot;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Gpx;
using UnitSport.Styles;
using UnitSport.Terrain;

namespace UnitSport.Core;

/// <summary>
/// Client bootstrap: loads the terrain manifest, sets up the chunk manager with the
/// PS1 terrain material, sky/fog environment, and a spectator camera over the valley.
/// </summary>
public partial class ClientWorld : Node3D, IOriginContainer
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
    private Vehicles.GarageUi? _garage;
    private Items.ItemController? _items;
    private Teleporter? _teleporter;
    private ChatManager? _chat;
    private ChatUi? _chatUi;
    private ChunkStreamer? _streamer;
    private NetworkChunkSource? _chunkSource;
    private CachingChunkSource? _cache;
    private ClientTerrainSync? _terrainSync;
    private WorldOrigin? _worldOrigin;
    private ShaderMaterial[] _worldMaterials = Array.Empty<ShaderMaterial>();

    /// <summary>The session this world is built for: the title screen's choice, or the command line's.</summary>
    public WorldLaunch Launch { get; init; } = WorldLaunch.FromArgs();

    /// <summary>Set by <see cref="GameShell"/>: true while a menu owns the screen, so the world ignores the keys.</summary>
    public Func<bool>? MenuOpen { get; set; }

    /// <summary>Esc / Start with nothing else open: the shell shows the pause menu.</summary>
    public event Action? PauseRequested;

    /// <summary>The server dropped this client, or kicked it (the reason, for the menu to show).</summary>
    public event Action<string>? Disconnected;

    // ---- loading progress, read every frame by the shell's loading screen ----
    public LoadStage Stage { get; private set; } = LoadStage.ReadingMap;
    public float LoadFraction { get; private set; }
    public string LoadDetail { get; private set; } = "";
    public string? Failure { get; private set; }
    private bool _bootDone, _connected, _disconnectReported;
    private double _loadClock, _terrainClock;
    private SpawnPoint? _spawn;
    private GameMode _mode = GameMode.Explore;

    /// <summary>Players on the server, for the pause menu's status line (null offline).</summary>
    public int? Players => _networked && _players != null ? _players.GetChildCount() : null;
    /// <summary>The origin the world started with: the server's frame, until positions on the wire are global.</summary>
    private (double E, double N)? _startOrigin;

    public override async void _Ready()
    {
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
            if (Array.IndexOf(scArgs, "--tuningcheck") >= 0)
            {
                GetTree().Quit(Player.GarageProbe.Check());
                return;
            }
            if (Array.IndexOf(scArgs, "--meshcheck") >= 0)
            {
                GetTree().Quit(Avatar.MeshScratch.Check());
                return;
            }
            if (Array.IndexOf(scArgs, "--cockpitcheck") >= 0)
            {
                GetTree().Quit(Player.CockpitCheck.Run());
                return;
            }
            if (Array.IndexOf(scArgs, "--spincheck") >= 0)
            {
                GetTree().Quit(Player.DriftCheck.Spin());
                return;
            }
            if (Array.IndexOf(scArgs, "--setupcheck") >= 0)
            {
                GetTree().Quit(Player.CarSetups.Check());
                return;
            }
            if (Array.IndexOf(scArgs, "--motocheck") >= 0)
            {
                GetTree().Quit(Player.Motorbike.Check());
                return;
            }
            if (Array.IndexOf(scArgs, "--truckcheck") >= 0)
            {
                GetTree().Quit(Player.HeavyCheck.Run());
                return;
            }
        }
        if (Items.IconSheet.Requested)
        {
            GetTree().Quit(Items.IconSheet.Run());
            return;
        }
        if (Loot.LootChanceCheck.Requested)
        {
            GetTree().Quit(Loot.LootChanceCheck.Run());
            return;
        }
        if (Items.InventoryCheck.Requested)
        {
            GetTree().Quit(Items.InventoryCheck.Run());
            return;
        }
        if (OriginCheck.Requested)
        {
            OriginCheck.Run(this);
            return;
        }
        if (ChatCheck.Requested)
        {
            GetTree().Quit(ChatCheck.Run(this));
            return;
        }
        if (StyleKit.ReportRequested)
        {
            GetTree().Quit(StyleKit.Report());
            return;
        }
        if (Occasions.OccasionProbe.Requested)
        {
            GetTree().Quit(Occasions.OccasionProbe.Run());
            return;
        }
        // idempotent: the shell, which owns the window settings, has usually installed it already
        PlayerInput.Install(GetParent());

        // a hand-made street to show the door portals: no terrain, no server
        if (Interiors.PortalDemo.ParseArgs() is { Requested: true } portalDemo)
        {
            MouseCapture.Disabled = true;
            AddChild(new Interiors.PortalDemo(portalDemo.Shot) { Name = "PortalDemo" });
            return;
        }

        var source = new LocalChunkSource(TerrainPaths.FindChunkDir());
        var manifest = await source.LoadManifestAsync();
        if (!IsInsideTree()) return;   // left during the load
        Report(LoadStage.BuildingWorld, 0.06f);

        // Wherever there is no terrain data, it is generated (FallbackChunkSource) and blended
        // into the real tiles beside it: round a partial region, and everywhere on a fresh clone,
        // which has no terrain at all (the data is 5.3 GB and not in the repository). Real tiles
        // take over tile by tile as they become available (ChunkManager.MergeAvailableTiles).
        // The generator is anchored to the default spawn, not to wherever this run starts, so
        // every client and the server generate the same world. With no local terrain the origin
        // goes on the spawn point, so the ground is not tens of kilometres out in float precision.
        bool hasLocalTerrain = manifest.Tiles.Count > 0;
        var (startE, startN) = SpawnPoint.ParseTarget();
        var generated = new ProceduralWorld(SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N);
        var origin = hasLocalTerrain
            ? new WorldOrigin(manifest.SuggestedOriginLv95.E, manifest.SuggestedOriginLv95.N)
            : new WorldOrigin(startE, startN);
        if (SpawnPoint.ParseOrigin() is var (pinE, pinN))
            origin = new WorldOrigin(pinE, pinN);

        _worldOrigin = origin;
        _startOrigin = (origin.E, origin.N);
        GD.Print($"[world] {manifest.Tiles.Count} tiles, origin LV95 {origin.E}/{origin.N}");

        // The floating origin (#185): world space follows the camera, so float32 stays precise
        // however far it goes. Offline only until positions on the wire are origin-independent.
        AddChild(new OriginShifter(origin, () => GetViewport().GetCamera3D()?.GlobalPosition, () => !_networked));

        if (!hasLocalTerrain)
            GD.PushWarning(
                "[world] no terrain data found, showing generated terrain. Generate the real one "
                + "with tools/TerrainPreprocessor, or join a server and it will stream in. "
                + "See the README.");

        GD.Print($"[style] {StyleKit.Style}");
        var material = StyleKit.Material(MaterialRole.Terrain);
        var roadMaterial = StyleKit.Material(MaterialRole.Road);
        var buildingMaterial = StyleKit.Material(MaterialRole.Building);
        var treeMaterial = StyleKit.Material(MaterialRole.Tree);
        var waterMaterial = StyleKit.Material(MaterialRole.Water);
        // far trees as billboards, before the first tile builds them
        var treeFarMaterial = StyleKit.Material(MaterialRole.TreeFar);
        StyleKit.TreeFarMaterial = StyleKit.TreeLod ? treeFarMaterial : null;
        AddChild(new CameraGlobal());

        // Fog is a setting now (off by default: the far horizon is the point). Every world
        // material carries the uniforms, so the toggle just re-pushes two floats to each.
        _worldMaterials = new[] { material, roadMaterial, buildingMaterial, treeMaterial, waterMaterial, treeFarMaterial };
        foreach (var m in _worldMaterials) FogUniforms.Apply(m);
        // a named handler, unsubscribed in _ExitTree: the event is static and outlives this world
        GameSettings.Changed += OnSettingsChanged;

        // The streamer exists even offline. Its fetches short-circuit to null with no peer, so
        // single player is unaffected — but the on-disk cache is still consulted, which means
        // terrain pulled during an earlier multiplayer session stays usable offline.
        _streamer = ChunkStreamer.CreateClient();
        AddChild(_streamer);

        var streamedSource = new NetworkChunkSource(
            source, TerrainPaths.FindChunkDir(), _streamer, TerrainPaths.FindCacheDir());
        _chunkSource = streamedSource;

        // The generated fill answers for the tiles no real data exists for, above the network
        // source so a client never asks a server for one, and under the cache so a generated tile
        // is not generated twice. Built even when switched off, so the setting can turn it on.
        var fallback = new FallbackChunkSource(streamedSource, generated, startE, startN,
            GameSettings.Current.GeneratedFill) { Log = s => GD.Print(s), HorizonCacheDir = TerrainPaths.FindCacheDir() };

        // Outermost, so a tile decoded once is not decoded again when the rings drop it and pick
        // it back up — which a route that doubles back does constantly.
        _cache = new CachingChunkSource(fallback);
        // the blend reads real neighbours through the cache, sharing what the loader decodes
        fallback.Neighbours = _cache;

        _chunks = new ChunkManager { Name = "Terrain" };
        // the auto build cap depends on whether tiles are coming over the wire
        _chunks.Streaming = () => _streamer?.ServerReachable == true;
        _chunks.Initialize(_cache, origin, manifest, material, roadMaterial, buildingMaterial, treeMaterial, waterMaterial);
        _chunks.UseFallback(fallback, _cache.Invalidate);
        // the towns occasion props go in: places.json's, plus the generated villages that stand
        // on generated ground (re-read whenever real tiles replace some, below)
        var fillChunks = _chunks;
        Occasions.OccasionTowns.UseGenerated(generated, (e, n) => fillChunks.IsGenerated(UnitSport.Terrain.Format.TileId.FromLv95(e, n)));

        // Anything streamed in an earlier session is on disk but absent from the local
        // manifest, so without this it would be unreachable until a server was joined again.
        ClientTerrainSync.MergeCachedIndex(_chunks, origin);

        AddChild(_chunks);
        Audio.Surfaces.Origin = origin;
        var chunksForAudio = _chunks;
        AddChild(new Audio.ReverbZones(() => GetViewport().GetCamera3D(), () => LocalPlayer?.Indoors == true, chunksForAudio)
            { Name = "ReverbZones" });
        _ambience = new Audio.Ambience(chunksForAudio, () => GetViewport().GetCamera3D())
            { Name = "Ambience", Origin = origin, Volume = Audio.SfxBus.SliderGain(GameSettings.Current.AmbienceVolume) };
        AddChild(_ambience);
        await Breathe();
        if (!IsInsideTree()) return;
        Report(LoadStage.BuildingWorld, 0.10f);

        // Vehicles left standing in the world. Same node path as on the server, so parking and
        // claiming work over the network; offline it just holds the nodes.
        var vehicles = Vehicles.VehicleManager.Create(this, _chunks);
        // seats in vehicles other players drive (#158): same path as the server's
        Vehicles.PassengerService.Create(this);
        vehicles.PlayerPositions = () =>
        {
            var at = new List<Vector3>();
            if (LocalPlayer is { } lp) at.Add(lp.GlobalPosition);
            if (GetViewport().GetCamera3D() is { } cam) at.Add(cam.GlobalPosition);
            return at;
        };
        // Radios thrown into the world and the CD library they play from, same paths as the
        // server's; the clock the CDs run on (offline: this machine's own).
        var radios = Items.RadioManager.Create(this);
        radios.PlayerPositions = vehicles.PlayerPositions;
        // items dropped and thrown on the ground (#206)
        Items.DroppedItems.Create(this).PlayerPositions = vehicles.PlayerPositions;
        var chunksForDrops = _chunks;
        Items.DroppedItems.GroundHeight = p => chunksForDrops != null && chunksForDrops.TryGetHeight(p, out float y) ? y : null;
        // every body that may hold a radio that plays (#168): the remote players and this one
        radios.Players = () =>
        {
            var all = _players?.GetChildren().OfType<FootPlayer>().ToList() ?? new List<FootPlayer>();
            if (LocalPlayer is { } me && !all.Contains(me)) all.Add(me);
            return all;
        };
        Audio.Cd.CdLibrary.Create(this, server: false);
        Net.ClockSync.Create(this);
        // live stations in cars (#179): offline this machine tunes them itself
        var webRadio = Audio.Live.WebRadio.Create(this);
        webRadio.Players = radios.Players;
        webRadio.Listener = () => GetViewport().GetCamera3D()?.GlobalPosition ?? LocalPlayer?.GlobalPosition;
        if (Audio.Live.WebRadioCheck.Create(() => LocalPlayer, () => _players, networked: false) is { } webRadioOffline) AddChild(webRadioOffline);
        // the Africa Twin at Riddes: placed here offline, by the server online
        AddChild(new World.AfricaTwinEgg(_chunks));
        if (World.EggProbe.Mode() is { } eggMode) AddChild(new World.EggProbe(eggMode, () => LocalPlayer, _chunks, origin));

        // Guns on the plane and helicopter. World/Combat on both sides, like World/Vehicles.
        var combat = Combat.CombatManager.Create(this, _chunks, server: false);
        combat.LocalPlayer = () => _onFoot ? LocalPlayer : null;

        // Building interiors: E opens a front door, and you walk through it. Same node path as the
        // server's, which plans and stores them; offline this client does both.
        var interiors = Interiors.InteriorManager.Create(this, _cache, origin);
        interiors.LocalPlayer = () => _onFoot ? LocalPlayer : null;
        var chunksForDoors = _chunks;
        interiors.BuildingBodies = tile => chunksForDoors.BuildingBodyAt(tile);
        interiors.OccupancySink = chunksForDoors.SetOccupancy;
        interiors.OpenDoorsSink = chunksForDoors.SetOpenDoors;
        interiors.OutsideShownChanged += shown =>
        {
            // indoors with the doors shut, the whole outside world is overhead and out of sight:
            // stop drawing it. An open door shows it again, through the doorway.
            if (_chunks != null) _chunks.Visible = shown;
            vehicles.Visible = shown;
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
        AddChild(new Occasions.OccasionPrecip());

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
            // every player it can meet — the local one, remote racers, race NPCs — with how each moves:
            // the traffic makes way for a race going through it (#85)
            Obstacles = () => GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>()
                .Select(p => (p.GlobalPosition, p.WorldVelocity)),
        };
        AddChild(_traffic);
        if (World.NpcWatch.FromArgs() is { } npcWatch) AddChild(npcWatch);
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

        // Start somewhere with something to look at, not at the world origin — after a
        // large import that is usually empty space. "--at E,N" overrides it (LV95 metres).
        // --shot and --probe place the camera themselves, and a spawn drop would fight
        // them for the height.
        bool placedByTool = ShotRunner.ParseArgs() != null || ShotRunner.ParseQueueArg() != null
            || TunnelProbe.ParseArgs() != null
            || FlightProbe.ParseArgs() != null
            || RideProbe.ParseArgs() != null || TruckProbe.Requested || DriveProbe.ParseArgs().Requested || World.ArrivalProbe.ParseArgs().Requested || World.TreeCheck.ParseArgs().Requested
            || Gpx.Cinema.CinemaProbe.ParseArgs() != null
            || RoadStandProbe.Requested() || MantleProbe.Requested() || VoidProbe.Requested()
            || FlightCheckProbe.ParseArgs() != null || Vehicles.VehicleProbe.ParseArgs().Requested
            || Interiors.InteriorProbe.ParseArgs().Requested || Interiors.DoorWatchProbe.ParseArgs().Requested
            || Loot.LootProbe.ParseArgs() != null
            || Loot.GatherProbe.ParseArgs().Requested
            || Birds.BirdProbe.ParseArgs().Requested
            || World.TrafficProbe.ParseArgs().Requested
            || Combat.CombatProbe.ParseArgs().Requested
            || Birds.BirdStrikeProbe.ParseArgs().Requested
            || SyncProbe.Requested() || HitboxProbe.Requested();
        // a check running in a window must leave the pointer to whoever is using the machine
        MouseCapture.Disabled |= placedByTool;

        _spectator = new SpectatorCamera { Name = "SpectatorCamera" };
        AddChild(_spectator);
        _chunks.AddAnchor(_spectator);

        if (!placedByTool)
        {
            var (spawnE, spawnN) = SpawnPoint.ParseTarget();
            AddChild(_spawn = new SpawnPoint(_spectator, _chunks, origin, spawnE, spawnN));
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

        // T in a stopped car at a garage: the tuning menu (GarageUi.GarageNear says where garages are):
        // in front of one, or parked inside it
        Vehicles.GarageUi.GarageNear = pos =>
            Interiors.DoorIndex.Nearest(pos, 8f, Terrain.Format.BuildingKind.Garage) != null
            || Interiors.InteriorManager.Instance?.LayoutAt(pos)?.DressedKind() == Terrain.Format.BuildingKind.Garage;
        _garage = Vehicles.GarageUi.Create();
        _garage.ActivePlayer = () => _onFoot ? LocalPlayer : null;
        AddChild(_garage);
        if (Player.GarageProbe.ParseArgs() is { } garageRole) AddChild(new Player.GarageProbe(garageRole, () => LocalPlayer));
        if (Player.HeavyNetProbe.ParseArgs() is { } heavyRole) AddChild(new Player.HeavyNetProbe(heavyRole, () => LocalPlayer));
        if (Player.PassengerProbe.ParseArgs() is { } passengerRole) AddChild(new Player.PassengerProbe(passengerRole, () => LocalPlayer));
        if (Player.ExitProbe.Requested) AddChild(new Player.ExitProbe(() => LocalPlayer));

        // The inventory is this machine's, not the player node's: it outlives a respawn or a
        // reconnect, and the player it acts on is resolved per frame like the picker's.
        var inventory = Items.InventoryUiProbe.Requested || Items.EconomyProbe.Password != null
            || Loot.LootSyncProbe.Role != null || Loot.LockSyncProbe.Role != null
            || Items.PlacedProbe.Role != null || Birds.BirdNetProbe.Role != null || Items.PhotoProbe.Requested || Items.UseAnimProbe.Role != null
            || Items.ShotgunProbe.Role != null || Items.PlantProbe.Role != null || Items.DropCheck.Requested
            ? Items.Inventory.Scratch() : Items.Inventory.Load();
        if (Items.PlantProbe.Role != null) inventory.Put(Items.Inventory.HotbarSize - 1, new Items.ItemStack(Items.ItemId.SwissFlag, 1));   // on the hotbar for --hold
        if (Items.ShotgunProbe.Role != null) { inventory.Put(Items.Inventory.HotbarSize - 1, new Items.ItemStack(Items.ItemId.Shotgun, 1)); inventory.Add(Items.ItemId.Shells, 25); }   // on the hotbar for --hold
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
        if (Loot.LootSyncProbe.Role != null) AddChild(new Loot.LootSyncProbe(items, origin));
        if (Loot.LockSyncProbe.Role != null) AddChild(new Loot.LockSyncProbe(items, origin));
        if (Items.PlacedProbe.Role != null) AddChild(new Items.PlacedProbe(items));
        if (Birds.BirdNetProbe.Role != null) AddChild(new Birds.BirdNetProbe(items));
        if (Items.UseAnimProbe.Role != null) AddChild(new Items.UseAnimProbe(items));
        if (Items.PhotoProbe.Requested) AddChild(new Items.PhotoProbe(items));
        if (Items.ShotgunProbe.Role != null) AddChild(new Items.ShotgunProbe(items));
        if (Items.PlantProbe.Role != null) AddChild(new Items.PlantProbe(items));
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "solo") > Array.IndexOf(OS.GetCmdlineUserArgs(), "--dropcheck")
            && Items.DropCheck.Requested && Items.DropCheck.Create(() => LocalPlayer, () => _players, items) is { } soloDrop)
            AddChild(soloDrop);
        Vehicles.VehicleManager.Refused += Toast;
        Vehicles.PassengerService.Said += Toast;

        // Chat exists from boot, not only once connected: offline it runs its commands itself
        // (/city, /spawn ...), and StartNetworking just keeps using it. World/Chat is also the
        // path the server's node routes RPCs to, so the name cannot change between the two.
        _chat = ChatManager.CreateClient();
        _chat.Teleporter = _teleporter;
        _chat.Inventory = inventory;
        _chat.GiveOrDrop = items.Give;
        _chat.PlaceSearch = _places;
        AddChild(_chat);
        _chatUi = ChatUi.Create(_chat, new ChatCompleter
        {
            Places = (query, limit) => _places!.Search(query, limit).Select(p => p.Name),
            Occasions = () => Occasions.OccasionManager.Instance?.Known.Select(e => e.Id) ?? [],
            Players = () => _chat.PlayerNames,
            PlayersWanted = _chat.RequestPlayerNames,
        });
        AddChild(_chatUi);

        // bottom right: the controls that apply here (F1, every control, is the shell's)
        var prompts = PromptBar.Create();
        prompts.Source = Prompts;
        AddChild(prompts);

        // Scavenging: what the furniture in those interiors holds. Same node path as the server's,
        // which decides who gets what; offline this client does both.
        // held-item events (shots, flashes) and placed objects (flags, photos): same node paths
        // as the server's, which relays the first and owns the second; offline this client does both
        Items.ItemEvents.Create(this, server: false);
        // the images of stuck Polaroids, fetched from the server by hash (before the list draws them)
        Items.PhotoTransfer.Create(this, server: false);
        Items.PlacedObjects.Create(this, origin, server: false, networked: Launch.Networked);

        var loot = Loot.LootService.Create(this);
        loot.Items = items;
        // the radio's panel: CDs to play, burn a new one, pick it up (opened from FootPlayer.TryInteract)
        _radioUi = Items.RadioUi.Create(() => LocalPlayer, items.Inventory);
        _radioUi.Give = items.Give;
        AddChild(_radioUi);
        if (Items.CarCdCheck.Create(() => LocalPlayer, () => _players, items.Inventory, networked: false) is { } carCdShots) AddChild(carCdShots);
        // ...and from the land itself: stone, water, firewood (hold G / pad X outdoors)
        var gathering = new Loot.Gathering(_chunks, origin, items);
        AddChild(gathering);
        // birds around the player, from the real land cover; the shotgun hunts them (J: journal)
        var birds = new Birds.BirdLife(_chunks, origin, items);
        AddChild(birds);
        // online the birds are the server's (World/BirdNet: same path as there); offline this client runs them
        Birds.BirdNet.Create(this, birds, server: false);

        // occasions: the treat / gift hunt (taken with the gather hold) and the seasonal hat
        AddChild(new Occasions.OccasionHunt());
        AddChild(new Occasions.OccasionHats(() => LocalPlayer, items.Inventory));

        // solid trunks around whatever asks for collision
        var trees = new World.TreeColliders(_chunks, origin);
        AddChild(trees);

        // Everything that kept what it read from tiles forgets it when the world under them
        // changes: real terrain arriving where generated ground was, or a rebase. The player is
        // put down again only if the ground under them is what went — this fires on every merge,
        // and snapping someone mid-jump to the ground over a change 20 km away would be a bug.
        _chunks.TerrainReplaced += affected =>
        {
            Audio.Surfaces.Forget();
            _ambience?.ForgetTiles();
            gathering.Forget();
            _traffic?.Forget();
            trees.Forget();
            Occasions.OccasionTowns.Reload();
            if (LocalPlayer is { } player
                && (affected == null || affected(origin.TileAt(player.GlobalPosition))))
                player.RequestReplacement();
        };

        // says so when the ground in view is generated rather than surveyed
        AddChild(new GeneratedTerrainNote(_chunks, origin));
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

        // the ride picker is otherwise only reachable by pressing E: "--ridemenu" opens it, for screenshotting it
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--ridemenu") >= 0)
            Callable.From(() => _rides.Open()).CallDeferred();

        await Breathe();
        if (!IsInsideTree()) return;

        // Decide the starting mode before the verification tools run, so a --shot of a
        // --gpx race sees the same world state a player would. ShotRunner then takes the
        // camera back for itself.
        StartLaunch(placedByTool);

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

        if (Interiors.DoorWatchProbe.ParseArgs() is { Requested: true } watch)
        {
            var (wE, wN) = SpawnPoint.ParseTarget();
            _spectator.Position = origin.ToWorld(wE, wN, 1200);
            AddChild(new Interiors.DoorWatchProbe(_chunks, origin, watch.Shot));
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

        if (VoidProbe.Requested())
        {
            var (vE, vN) = SpawnPoint.ParseTarget();
            _spectator.Position = origin.ToWorld(vE, vN, 1200);
            AddChild(new VoidProbe(_chunks, origin));
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

        if (World.ArrivalProbe.ParseArgs() is { Requested: true } arrival)
        {
            var (arrE, arrN) = SpawnPoint.ParseTarget();
            _spectator.Position = origin.ToWorld(arrE, arrN, 1200);
            AddChild(new World.ArrivalProbe(_chunks, origin, arrival.Prefix));
            return;
        }

        if (World.TreeCheck.ParseArgs() is { Requested: true } treeCheck)
        {
            var (treeE, treeN) = SpawnPoint.ParseTarget();
            _spectator.Position = origin.ToWorld(treeE, treeN, 1200);
            AddChild(new World.TreeCheck(_chunks, origin, treeCheck.Shot));
            return;
        }

        if (TruckProbe.Requested)
        {
            var (truckE, truckN) = SpawnPoint.ParseTarget();
            _spectator.Position = origin.ToWorld(truckE, truckN, 1200);
            AddChild(new TruckProbe(_chunks, origin));
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
                float.Parse(shot[3], inv), float.Parse(shot[4], inv), double.Parse(shot[5], inv), shot[6]) { Origin = _worldOrigin });
        }
        else if (ShotRunner.ParseQueueArg() is { } queue)
        {
            _spectator.SetProcess(false);
            _spectator.SetProcessUnhandledInput(false);
            Input.MouseMode = Input.MouseModeEnum.Visible;
            var runner = ShotRunner.ForQueue(_spectator, queue, _worldOrigin);
            runner.GroundHeight = at => _chunks != null && _chunks.TryGetHeight(at, out float h) ? h : null;
            AddChild(runner);
        }
    }

    /// <summary>
    /// Starts the session this world was built for: joins the server, loads the GPX tracks, or
    /// just explores. A tool that places the camera itself (<paramref name="placedByTool"/>) is
    /// left to do so.
    /// </summary>
    private void StartLaunch(bool placedByTool)
    {
        _bootDone = true;
        _loadClock = 0;
        switch (Launch.Mode)
        {
            case GameMode.Multiplayer:
                StartNetworking(Launch.Endpoint);
                if (!Launch.FromCommandLine) Callable.From(() => EnterMode(GameMode.Multiplayer)).CallDeferred();
                break;
            case GameMode.GpxReplay:
                // each track joins the race as another ghost
                foreach (string path in Launch.GpxPaths)
                    Callable.From(() => _gpx!.Load(path)).CallDeferred();
                Callable.From(() => EnterMode(GameMode.GpxReplay)).CallDeferred();
                break;
            default:
                if (!placedByTool) Callable.From(() => EnterMode(GameMode.Explore)).CallDeferred();
                break;
        }
    }

    /// <summary>
    /// One frame for the loading screen to draw, between the heavy steps of building the world.
    /// Not for command-line runs, whose tools expect everything built in one go as before.
    /// </summary>
    private async Task Breathe()
    {
        if (Launch.FromCommandLine) return;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void Report(LoadStage stage, float fraction, string detail = "")
    {
        if (Stage is LoadStage.Ready or LoadStage.Failed) return;
        Stage = stage;
        LoadFraction = Mathf.Max(LoadFraction, fraction);
        LoadDetail = detail;
    }

    /// <summary>
    /// Works out how far the session is from playable: connected, terrain synced, our player
    /// spawned, the spawn on the ground, the tile under the camera drawn (any detail; with its
    /// collision when a body stands there), and the far horizon drawn around it, so you never
    /// land in a void. A world that cannot finish the terrain in 25 s goes ahead anyway: the
    /// tiles around you stream in while you play.
    /// </summary>
    private void TrackLoading(double delta)
    {
        if (!_bootDone || Stage is LoadStage.Ready or LoadStage.Failed || _chunks == null) return;
        _loadClock += delta;

        if (Launch.Mode == GameMode.Multiplayer)
        {
            if (!_connected)
            {
                Report(LoadStage.Connecting, 0.12f, Launch.Endpoint);
                // ENet's own give-up takes half a minute; nobody wants to stare at that
                if (_loadClock > 15) Fail($"No answer from {Launch.Endpoint}. Is the server running, and its port open?");
                return;
            }
            if (_terrainSync is { IndexFinished: false })
            {
                Report(LoadStage.SyncingTerrain, 0.2f, LoadDetail);
                return;
            }
            if (SpawnPending)
            {
                Report(LoadStage.PlacingYou, 0.25f);
                return;
            }
            if (LocalPlayer == null || !_onFoot)
            {
                Report(LoadStage.WaitingForPlayer, 0.28f);
                if (_loadClock > 45) Fail("The server never spawned your player.");
                return;
            }
        }

        if (SpawnPending)
        {
            Report(LoadStage.PlacingYou, 0.32f);
            return;
        }

        var eye = GetViewport().GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
        var (done, total) = _chunks.PlayableNear(eye, 0);
        _terrainClock += delta;
        bool timedOut = _terrainClock > 25;
        if (!timedOut && (total > 0 ? done < total : _terrainClock <= 6))
        {
            Report(LoadStage.BuildingTerrain, total > 0 ? 0.35f + 0.25f * done / total : 0.35f,
                total > 0 ? $"{done} / {total} tiles around you" : "");
            return;
        }

        // The horizon's lattice is generated at boot (or read from its cache) while the steps
        // above run; its blocks then stream nearest first.
        var horizon = _chunks.Horizon?.Progress();
        if (!timedOut && horizon == null)
        {
            Report(LoadStage.DrawingHorizon, 0.6f);
            return;
        }
        if (!timedOut && horizon is { Done: var hd, Total: var ht } && hd < ht)
        {
            Report(LoadStage.DrawingHorizon, 0.6f + 0.4f * hd / ht, $"{hd} / {ht} blocks of horizon");
            return;
        }

        Stage = LoadStage.Ready;
        LoadFraction = 1;
        GD.Print($"[world] ready after {_loadClock:F1} s ({done}/{total} tiles near, "
            + $"horizon {(horizon is { } hp ? $"{hp.Done}/{hp.Total}" : "loading")})");
    }

    /// <summary>A menu closed over this world: hand the pointer back to the mode that wants it.</summary>
    public void ResumeControl()
    {
        if (_mode is GameMode.Explore or GameMode.Multiplayer) MouseCapture.Capture();
    }

    private void OnSettingsChanged()
    {
        foreach (var m in _worldMaterials) FogUniforms.Apply(m);
        _chunks?.ApplySettings(GameSettings.Current);
        _chunks?.SetFallbackEnabled(GameSettings.Current.GeneratedFill);
        if (_ambience != null) _ambience.Volume = Audio.SfxBus.SliderGain(GameSettings.Current.AmbienceVolume);
        SetCameraFar(GameSettings.Current.CameraFar);
    }

    private void Toast(string message) => _items?.Ui.Toast(message);

    /// <summary>
    /// Leaving to the title frees this world: everything static it subscribed to lets go here,
    /// or the next world's events would call into freed nodes (<c>docs/notes/ui/teardown.md</c>).
    /// </summary>
    public override void _ExitTree()
    {
        GameSettings.Changed -= OnSettingsChanged;
        Vehicles.VehicleManager.Refused -= Toast;
        Vehicles.PassengerService.Said -= Toast;
        if (_networked)
        {
            Multiplayer.ConnectedToServer -= OnConnected;
            Multiplayer.ConnectionFailed -= OnConnectionFailed;
            Multiplayer.ServerDisconnected -= OnServerDisconnected;
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
        Items.PlacedObjects.Instance?.Reposition();
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
                // behind the loading screen the pointer stays free for its Cancel button
                if (MenuOpen?.Invoke() != true) MouseCapture.Capture();
                break;

            case GameMode.GpxReplay:
                _gpx.SetReturnCamera(_onFoot && LocalPlayer != null ? LocalPlayer.Camera : _spectator);
                ParkExploreAnchor(true);
                _gpx.Begin();
                break;

            case GameMode.Multiplayer:
                if (!_networked) StartNetworking(Launch.Endpoint);
                if (MenuOpen?.Invoke() != true) MouseCapture.Capture();
                break;
        }

        _mode = mode;
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
        // Positions on the wire are still world space (#185, phase 2), so online every peer must be
        // in the frame the server is in: a game that travelled offline puts its origin back where
        // it started before anything is sent. The shifter is off from here on.
        if (_startOrigin is { } start) OriginShifter.Instance?.ShiftTo(start.E, start.N, exact: true);
        _networked = true;

        // The chat node (World/Chat, made at boot) is already where the server's RPCs route.
        if (Items.EconomyProbe.Password != null && _items != null && _chat != null)
            AddChild(new Items.EconomyProbe(_chat, _items.Inventory));

        // World/Race on both sides; the client side puts this player on the grid and times the run
        var race = World.RaceManager.CreateClient();
        race.LocalPlayer = () => LocalPlayer;
        AddChild(race);
        if (CarSwitchCheck.Create(() => LocalPlayer, () => _players) is { } switchCheck) AddChild(switchCheck);
        if (RadioSyncCheck.Create(() => LocalPlayer, () => _players, _items?.Inventory) is { } radioCheck) AddChild(radioCheck);
        if (_items != null && Items.CarCdCheck.Create(() => LocalPlayer, () => _players, _items.Inventory, networked: true) is { } carCdCheck) AddChild(carCdCheck);
        if (Items.DropCheck.Create(() => LocalPlayer, () => _players, _items) is { } dropCheck) AddChild(dropCheck);
        if (Audio.Live.WebRadioCheck.Create(() => LocalPlayer, () => _players, networked: true) is { } webRadioCheck) AddChild(webRadioCheck);

        _chat!.Kicked += OnKicked;

        // Merges the server's tile list so tiles this client never shipped with become
        // streamable, and refuses to stream at all if the two worlds disagree on the origin.
        _terrainSync = new ClientTerrainSync(_streamer!, _chunks!, _worldOrigin!);
        // the sync runs its continuations on the thread pool, and the chat log is UI
        _terrainSync.Status += line =>
            Callable.From(() =>
            {
                _chatUi?.Append(line, ChatKind.System);
                LoadDetail = line;
            }).CallDeferred();

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

        // before any player arrives: each one's synchronizer asks it whom to send to
        InterestService.CreateClient(this);
        _players = new Node3D { Name = "Players" };
        _players.AddToGroup(OriginShifter.ContainerGroup);
        _players.ChildEnteredTree += node =>
        {
            if (node.Name == Multiplayer.GetUniqueId().ToString() && node is FootPlayer player)
                Callable.From(() => EnterFootWhenGrounded(player)).CallDeferred();
        };
        AddChild(_players);
        AddChild(PlayerReplication.CreateSpawner());
        AddChild(World.RaceNpcs.CreateClient());   // World/Npcs: the path its RPC routes by
        if (NetSmoothProbe.ParseArgs() is { } smooth) AddChild(new NetSmoothProbe(_players, smooth.Seconds, smooth.Label));
        var net = new NetworkManager { Name = "Net" };
        AddChild(net);
        // Handles bare hosts, host:port, and bracketed IPv6 — a plain colon split breaks on
        // the IPv6 address Tailscale hands out alongside the 100.x one.
        var (address, port) = NetworkManager.ParseEndpoint(host);
        net.StartClient(address, port);

        // named handlers: the multiplayer API is the tree's and outlives this world
        Multiplayer.ConnectedToServer += OnConnected;
        Multiplayer.ConnectionFailed += OnConnectionFailed;
        Multiplayer.ServerDisconnected += OnServerDisconnected;
        _mode = GameMode.Multiplayer;
    }

    private void OnConnected()
    {
        _connected = true;
        GD.Print($"[world] connected, peer id {Multiplayer.GetUniqueId()}");

        // The server assigns the final name — it deduplicates and sanitises — so this is
        // a request, not a claim.
        string requested = Launch.PlayerName;
        _chat?.AnnounceName(requested.Length > 0 ? requested : $"Rider{Multiplayer.GetUniqueId()}");

        // Fire and forget: the world is already playable on local tiles while this runs.
        _ = _terrainSync?.SyncAsync();
    }

    private void OnConnectionFailed()
    {
        GD.PushError("[world] connection failed");
        // a dead ENet peer makes every IsServer() on it an error, every frame: go offline instead
        Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
        Fail($"Could not connect to {Launch.Endpoint}. Is the server running, and its port open?");
    }

    private void OnServerDisconnected()
    {
        _chatUi?.Append("Disconnected from the server.", ChatKind.Error);
        Permissions.Reset();
        ReportDisconnect("The server closed the connection.");
    }

    private void OnKicked(string reason)
    {
        GD.Print($"[net] kicked: {reason}");
        ReportDisconnect($"Kicked from the server: {reason}");
    }

    private void ReportDisconnect(string reason)
    {
        if (_disconnectReported) return;
        _disconnectReported = true;
        if (Stage != LoadStage.Ready) Fail(reason);
        else Disconnected?.Invoke(reason);
    }

    private void Fail(string reason)
    {
        if (Stage is LoadStage.Failed) return;
        Failure = reason;
        Stage = LoadStage.Failed;
        // a command-line run has no loading screen to report to: say it in the log
        if (Launch.FromCommandLine) GD.PushError($"[world] {reason}");
    }

    /// <summary>My own networked player node, once the server has spawned it.</summary>
    // not while the link is down: GetUniqueId on a dead peer logs an error, and this runs every frame (#211)
    private FootPlayer? GetLocalNetPlayer() =>
        Net.NetLink.Ready(this) ? _players?.GetNodeOrNull<FootPlayer>(Multiplayer.GetUniqueId().ToString()) : null;

    public override void _UnhandledInput(InputEvent @event)
    {
        // Actions rather than keys, so Start, Y and the D-pad do what Esc, E and T do. Echo is
        // refused: a held key must not re-open the menu it just closed.
        if (!@event.IsPressed() || @event.IsEcho()) return;

        // ChatUi handles Enter, slash and Esc from _UnhandledKeyInput, which runs first; if
        // it is typing, nothing here should fire.
        if (_chatUi is { IsTyping: true }) return;

        // a menu owns the keys while open, Esc included: the shell (a sibling, asked after this) closes it
        if (MenuOpen?.Invoke() == true) return;

        // Esc / Start with nothing else open: the pause menu
        if (@event.IsActionPressed(PlayerInput.Menu))
        {
            GetViewport().SetInputAsHandled();
            PauseRequested?.Invoke();
            return;
        }

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
            // in a vehicle with a stereo R is the radio's (RadioPanel, same key): never both panels
            if (LocalPlayer is { StereoOwner: not null }) return;
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

        // T is also the fly camera: at a garage, in a stopped car, it tunes instead
        if (@event.IsActionPressed(PlayerInput.Tune) && _garage?.TryOpen() == true) return;

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
        if (MenuOpen?.Invoke() == true || _gpx is { Active: true } || _rides is { IsOpen: true }) yield break;

        // whoever owns the camera on screen: the local player, or a body a probe made itself
        var shown = XR.XrSession.Anchor ?? GetViewport().GetCamera3D();
        var viewer = (_onFoot ? LocalPlayer : null) ?? shown?.GetParent() as FootPlayer;
        if (viewer == null && shown == _spectator)
        {
            yield return (PlayerInput.ToggleMode, "Walk");
            yield return (PlayerInput.FlyUp, "Up");
            yield return (PlayerInput.FlyDown, "Down");
            yield return (PlayerInput.Teleport, "Map");
        }
        else if (viewer is { } p && IsInstanceValid(p))
        {
            if (p.RidingAlong && p.StereoOwner != null) yield return (PlayerInput.RadioPanel, "Radio");
            if (p.Vehicle is { IsVehicle: true } vehicle)
            {
                // the engine and "get out" are on the vehicle readout in the same corner
                yield return (PlayerInput.CameraToggle, "Camera");
                if (p.StereoOwner != null) yield return (PlayerInput.RadioPanel, "Radio");
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
            else if (Items.ItemController.Instance?.Throw.Active == true)
            {
                yield return (PlayerInput.UseItem, Items.ItemController.Instance.Throw.Charging ? "Let go to throw" : "Hold to wind up a throw");
                yield return (PlayerInput.AimItem, "Release: put it away");
            }
            else if (Items.Highlight.Pointed is Items.DroppedItem pointed)
                yield return (PlayerInput.InteractMount, $"Pick up {pointed.Label}");
            else if (!p.Indoors)
            {
                if (Items.Highlight.Pointed is Items.RadioBody || Items.RadioManager.Instance?.Nearest(p.GlobalPosition, Items.RadioManager.Reach) != null)
                    yield return (PlayerInput.InteractMount, "Radio");
                else if (Items.RadioManager.Instance?.NearestPlaying(p.GlobalPosition, Items.RadioManager.DanceRadius) != null)
                    yield return (PlayerInput.InteractMount, p.DanceId == 0 ? "Dance" : "Stop dancing");
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

    private Items.RadioUi? _radioUi;
    private double _sinceStatus;

    public override void _Process(double delta)
    {
        // the loader queues what is in front of the live camera first, whichever camera that is
        if (_chunks != null && GetViewport().GetCamera3D() is { } cam)
            _chunks.SetView(cam);
        TrackLoading(delta);
        if (_pendingFoot != null && !SpawnPending)
        {
            var player = _pendingFoot;
            _pendingFoot = null;
            if (IsInstanceValid(player) && player.IsInsideTree()) EnterFootMode(player);
        }

        if (!_networked || _players == null) return;
        _sinceStatus += delta;
        if (_sinceStatus < 5) return;
        _sinceStatus = 0;
        foreach (var child in _players.GetChildren())
            if (child is FootPlayer p)
                GD.Print($"[status] player {p.Name} at {p.GlobalPosition:F1}");
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

    /// <summary>The spawn point has not found the ground under the spawn yet.</summary>
    private bool SpawnPending => _spawn != null && IsInstanceValid(_spawn) && _spawn.IsInsideTree();

    private FootPlayer? _pendingFoot;

    /// <summary>
    /// Our networked player arrived. Joining straight after the world is built (the title screen's
    /// way), it can arrive before the spawn point has found the ground, and stepping onto foot then
    /// would put it at the camera's starting height, kilometres up: wait for the spawn first.
    /// </summary>
    private void EnterFootWhenGrounded(FootPlayer player)
    {
        if (SpawnPending) { _pendingFoot = player; return; }
        EnterFootMode(player);
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
        player.CarRadioTuned -= OnCarRadioTuned;
        player.CarRadioTuned += OnCarRadioTuned;
        GD.Print($"[world] on foot at {player.GlobalPosition}");
    }

    private void OnCarRadioTuned(string station) => _items?.Ui.Toast($"Radio: {station}");
}
