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
    private Handshake? _handshake;
    private WorldOrigin? _worldOrigin;
    private ShaderMaterial[] _worldMaterials = Array.Empty<ShaderMaterial>();
    private WorldEnvironment? _worldEnvironment;
    private World.DayNight? _dayNight;
    private DirectionalLight3D? _sun;
    private ShaderMaterial? _treeMaterial;
    private NearTrees? _nearTrees;
    private PhotoLayer? _photos;

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

    private static bool Has(string flag) => Array.IndexOf(OS.GetCmdlineUserArgs(), flag) >= 0;

    /// <summary>The value after <c>--soundcheck</c> (its output directory), or null.</summary>
    private static string? SoundcheckDir
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--soundcheck");
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }

    private static int Verdict(string tag, bool ok)
    {
        GD.Print(ok ? $"[{tag}] RESULT: ok" : $"[{tag}] RESULT: FAILED");
        return ok ? 0 : 1;
    }

    /// <summary>Self-checks that build no world: the first one requested runs, and the game quits with its exit code.</summary>
    private (Func<bool> Requested, Func<int> Run)[] QuickChecks => new (Func<bool>, Func<int>)[]
    {
        (() => SoundcheckDir != null, () => Audio.Soundcheck.Run(SoundcheckDir!)),
        (() => Has("--driftcheck"), Player.DriftCheck.Run),
        (() => Has("--tuningcheck"), Player.GarageProbe.Check),
        (() => Has("--meshcheck"), Avatar.MeshScratch.Check),
        (() => Has("--outfitcheck"), Avatar.OutfitCheck.Run),
        (() => Has("--emotecheck"), Avatar.EmoteCheck.Run),
        (() => Has("--cockpitcheck"), Player.CockpitCheck.Run),
        (() => Has("--spincheck"), Player.DriftCheck.Spin),
        (() => Has("--setupcheck"), Player.CarSetups.Check),
        (() => Has("--motocheck"), Player.Motorbike.Check),
        (() => Has("--truckcheck"), Player.HeavyCheck.Run),
        (() => Items.IconSheet.Requested, Items.IconSheet.Run),
        (() => Loot.LootChanceCheck.Requested, Loot.LootChanceCheck.Run),
        (() => Loot.ShopCheck.Requested, Loot.ShopCheck.Run),
        (() => Interiors.DoorCheck.Requested, Interiors.DoorCheck.Run),
        (() => Interiors.FlatCheck.Requested, Interiors.FlatCheck.Run),
        (() => Interiors.ShapedCheck.Requested, Interiors.ShapedCheck.Run),
        (() => Terrain.Construction.ConstructionCheck.Requested, Terrain.Construction.ConstructionCheck.Run),
        (() => Items.InventoryCheck.Requested, Items.InventoryCheck.Run),
        (() => ChatCheck.Requested, () => ChatCheck.Run(this)),
        (() => StyleKit.ReportRequested, StyleKit.Report),
        (() => BattleRoyale.BrCheck.Requested, BattleRoyale.BrCheck.Run),
        (() => Occasions.OccasionProbe.Requested, Occasions.OccasionProbe.Run),
        (() => Player.WheelProbe.CheckRequested, () => Player.WheelProbe.Check(GetParent())),
        // the network rules' own self-checks: vision interest and remote interpolation
        (() => Has("--interestcheck"), () => Verdict("interestcheck", Interest.SelfCheck() & RemoteInterpolator.SelfCheck())),
        // the CD beat analyser's self-test: synthetic clicks at known tempos
        (() => Has("--beatcheck"), () => Verdict("beatcheck", Audio.Cd.BeatAnalyzer.SelfCheck())),
        // a VR player's hands packed into the pose and back (#439)
        (() => Has("--vrposecheck"), () => Verdict("vrposecheck", Player.FootPlayer.VrPoseSelfCheck())),
    };

    public override async void _Ready()
    {
        Audio.SfxBus.Ensure();
        foreach (var (requested, run) in QuickChecks)
            if (requested())
            {
                GetTree().Quit(run());
                return;
            }
        if (OriginCheck.Requested)
        {
            OriginCheck.Run(this);
            return;
        }
        // the wave shader against the C# wave field (#299): needs frames and a GPU, builds no world
        if (World.WaterParity.Requested)
        {
            AddChild(new World.WaterParity { Name = "WaterParity" });
            return;
        }
        if (ImpostorBake.Requested)
        {
            AddChild(new ImpostorBake());
            return;
        }
        // idempotent: the shell, which owns the window settings, has usually installed it already
        PlayerInput.Install(GetParent());
        if (Player.WheelProbe.ForceCheckRequested)
        {
            MouseCapture.Disabled = true;
            AddChild(new Player.WheelProbe { Name = "WheelProbe" });
            return;
        }

        // a hand-made street to show the door portals: no terrain, no server
        if (Interiors.PortalDemo.ParseArgs() is { Requested: true } portalDemo)
        {
            MouseCapture.Disabled = true;
            AddChild(new Interiors.PortalDemo(portalDemo.Shot) { Name = "PortalDemo" });
            return;
        }
        // the nine IKEA stores and one store built in code (#501): no terrain, no server
        if (Interiors.IkeaProbe.ParseArgs())
        {
            MouseCapture.Disabled = true;
            AddChild(new Interiors.IkeaProbe { Name = "IkeaProbe" });
            return;
        }
        // whether a block of flats' stairwells can be climbed, from their collision (#571)
        if (Interiors.StairWalkCheck.Requested)
        {
            MouseCapture.Disabled = true;
            AddChild(new Interiors.StairWalkCheck { Name = "StairWalkCheck" });
            return;
        }
        // an apartment block's inside, hand-made (#557): no terrain, no server
        if (Interiors.FlatTour.ParseArgs() is { Requested: true } flatTour)
        {
            MouseCapture.Disabled = true;
            AddChild(new Interiors.FlatTour(flatTour.Shot, Interiors.FlatTour.BlockArg()) { Name = "FlatTour" });
            return;
        }
        // the five industrial sites, hand-made (#497): no terrain, no server
        if (Interiors.SiteProbe.ParseArgs() is { Requested: true } siteCheck)
        {
            MouseCapture.Disabled = true;
            AddChild(new Interiors.SiteProbe(siteCheck.Shot) { Name = "SiteProbe" });
            return;
        }
        // a hand-made church whose radio plays the chess type beat (#370): no terrain, no server
        if (Interiors.ChurchStageProbe.ParseArgs() is { Requested: true } churchStage)
        {
            MouseCapture.Disabled = true;
            AddChild(new Interiors.ChurchStageProbe(churchStage.Shot) { Name = "ChurchStageProbe" });
            return;
        }

        // a test course built in code instead of the map: --chunks fixture:<course>, --world fixture,
        // or --systems without terrain (Terrain/Fixture, docs/notes/general/testing.md)
        IChunkSource source;
        bool fixture = Systems.FixtureCourse != null;
        if (fixture)
        {
            var (fE, fN) = SpawnPoint.ParseTarget(Launch);
            source = Terrain.Fixture.FixtureChunkSource.Create(Systems.FixtureCourse!, fE, fN)
                ?? throw new ArgumentException($"no fixture course '{Systems.FixtureCourse}' (known: {string.Join(", ", Terrain.Fixture.FixtureCourse.Names)})");
            GD.Print($"[world] fixture course {Systems.FixtureCourse}");
        }
        else source = new LocalChunkSource(TerrainPaths.FindChunkDir());
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
        var (startE, startN) = SpawnPoint.ParseTarget(Launch);
        // the generator derives its rivers on a worker as soon as it exists: not made when it is off
        var generated = Systems.On(Systems.Generated) && !fixture
            ? new ProceduralWorld(SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N) : null;
        var origin = hasLocalTerrain
            ? new WorldOrigin(manifest.SuggestedOriginLv95.E, manifest.SuggestedOriginLv95.N)
            : new WorldOrigin(startE, startN);
        if (SpawnPoint.ParseOrigin() is var (pinE, pinN))
            origin = new WorldOrigin(pinE, pinN);

        _worldOrigin = origin;
        GD.Print($"[world] {manifest.Tiles.Count} tiles, origin LV95 {origin.E}/{origin.N}");

        // The floating origin (#185): world space follows the camera, so float32 stays precise
        // however far it goes. Online too: positions on the wire are LV95, never world space.
        AddChild(new OriginShifter(origin, () => GetViewport().GetCamera3D()?.GlobalPosition, () => true));

        if (!hasLocalTerrain && generated != null)
            GD.PushWarning(
                "[world] no terrain data found, showing generated terrain. Generate the real one "
                + "with tools/TerrainPreprocessor, or join a server and it will stream in. "
                + "See the README.");

        // the style chosen since the last world, if it changed at the title screen
        StyleKit.Restyle();
        GD.Print($"[style] {StyleKit.Applied}");
        var material = StyleKit.Material(MaterialRole.Terrain);
        var roadMaterial = StyleKit.Material(MaterialRole.Road);
        var buildingMaterial = StyleKit.Material(MaterialRole.Building);
        var treeMaterial = _treeMaterial = StyleKit.Material(MaterialRole.Tree);
        var waterMaterial = StyleKit.Material(MaterialRole.Water);
        // piers and jetties (#377): vertex-coloured props, never lit up at night
        var pierMaterial = StyleKit.Material(MaterialRole.Prop);
        pierMaterial.SetShaderParameter("flicker", 0f);
        // far trees as billboards, before the first tile builds them
        var treeFarMaterial = StyleKit.Material(MaterialRole.TreeFar);
        StyleKit.TreeFarMaterial = StyleKit.TreeLod ? treeFarMaterial : null;
        AddChild(new CameraGlobal());

        // Fog is a setting now (off by default: the far horizon is the point). Every world
        // material carries the uniforms, so the toggle just re-pushes two floats to each.
        _worldMaterials = new[] { material, roadMaterial, buildingMaterial, treeMaterial, waterMaterial, treeFarMaterial, pierMaterial };
        foreach (var m in _worldMaterials) FogUniforms.Apply(m);
        // a named handler, unsubscribed in _ExitTree: the event is static and outlives this world
        GameSettings.Changed += OnSettingsChanged;
        Permissions.Changed += OnPermissionsChanged;
        StyleCommand.RebuildRequested += OnRebuildRequested;
        StyleKit.Chosen += OnStyleChosen;

        // The streamer exists even offline. Its fetches short-circuit to null with no peer, so
        // single player is unaffected — but the on-disk cache is still consulted, which means
        // terrain pulled during an earlier multiplayer session stays usable offline.
        _streamer = ChunkStreamer.CreateClient();
        AddChild(_streamer);

        // (not under a fixture course: the cache would fill its gaps, and its horizon, with real data)
        IChunkSource streamedSource = fixture ? source : _chunkSource = new NetworkChunkSource(
            source, TerrainPaths.FindChunkDir(), _streamer, TerrainPaths.FindCacheDir());
        if (_chunkSource != null)
        {
            // Settings → Data (#63): the cap is the player's, and Clear reaches the live cache
            _chunkSource.MaxCacheBytes = CacheCapBytes;
            NetworkChunkSource.Active = _chunkSource;
        }

        // The generated fill answers for the tiles no real data exists for, above the network
        // source so a client never asks a server for one, and under the cache so a generated tile
        // is not generated twice. Built even when switched off, so the setting can turn it on.
        var fallback = generated == null ? null : new FallbackChunkSource(streamedSource, generated, startE, startN,
            GameSettings.Current.GeneratedFill) { Log = s => GD.Print(s), HorizonCacheDir = TerrainPaths.FindCacheDir() };

        // Outermost, so a tile decoded once is not decoded again when the rings drop it and pick
        // it back up — which a route that doubles back does constantly.
        // decoded tiles in RAM: a phone has a fraction of a desktop's to spare (#63)
        _cache = Platform.IsMobile
            ? new CachingChunkSource(fallback ?? (IChunkSource)streamedSource, 96L * 1024 * 1024)
            : new CachingChunkSource(fallback ?? (IChunkSource)streamedSource);
        // the blend reads real neighbours through the cache, sharing what the loader decodes
        if (fallback != null) fallback.Neighbours = _cache;

        _chunks = new ChunkManager { Name = "Terrain" };
        // the auto build cap depends on whether tiles are coming over the wire
        _chunks.Streaming = () => _streamer?.ServerReachable == true;
        _chunks.Initialize(_cache, origin, manifest, material, roadMaterial, buildingMaterial, treeMaterial, waterMaterial);
        // occlusion culling with the buildings round the camera as occluders (#553)
        _chunks.ApplyOcclusion();
        _chunks.PierMaterial = pierMaterial;
        // the landings and jetties (#377) before the first tile builds: their piers ride in its build
        World.Landings.Use(await World.Landings.LoadAsync(_cache));
        if (fallback != null) _chunks.UseFallback(fallback, _cache.Invalidate);
        // the towns occasion props go in: places.json's, plus the generated villages that stand
        // on generated ground (re-read whenever real tiles replace some, below)
        var fillChunks = _chunks;
        Occasions.OccasionTowns.UseGenerated(generated, (e, n) => fillChunks.IsGenerated(UnitSport.Terrain.Format.TileId.FromLv95(e, n)));

        // Anything streamed in an earlier session is on disk but absent from the local
        // manifest, so without this it would be unreachable until a server was joined again.
        // A fixture course is all there is: nothing cached joins it.
        if (!fixture) ClientTerrainSync.MergeCachedIndex(_chunks, origin);

        AddChild(_chunks);
        // the water (#299): queries on these tiles, waves pushed to the shaders, the underwater look;
        // --sea-state for an offline world (online the server's replaces it on join)
        World.WaterField.Bind(_chunks);
        if (World.SeaStateCommand.FromArgs(OS.GetCmdlineUserArgs(), out string seaError) is { } sea) World.WaterField.SetSeaState(sea);
        else if (seaError.Length > 0) GD.PushWarning($"[water] {seaError}");
        AddChild(new World.WaterSurface { Name = "WaterSurface" });
        ApplyNearTrees();
        ApplyPhotos();
        Audio.Surfaces.Origin = origin;
        var chunksForAudio = _chunks;
        if (Systems.On(Systems.Audio))
        {
            // the listener is the local body's head, not the camera (#375)
            AddChild(new Audio.Ears(() => LocalPlayer));
            AddChild(new Audio.ReverbZones(EarNode, () => LocalPlayer?.Indoors == true, chunksForAudio)
                { Name = "ReverbZones" });
            _ambience = new Audio.Ambience(chunksForAudio, EarNode)
                { Name = "Ambience", Origin = origin, Volume = Audio.SfxBus.SliderGain(GameSettings.Current.AmbienceVolume) };
            AddChild(_ambience);
            // the hammering, vibrator, grinder, beeper, radio and crane motor of a working building site (#617)
            AddChild(new Terrain.Construction.SiteSounds(chunksForAudio, EarNode));
        }
        await Breathe();
        if (!IsInsideTree()) return;
        Report(LoadStage.BuildingWorld, 0.10f);

        // Vehicles left standing in the world. Same node path as on the server, so parking and
        // claiming work over the network; offline it just holds the nodes.
        var vehicles = Vehicles.VehicleManager.Create(this, _chunks, origin);
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
        var radios = Items.RadioManager.Create(this, origin);
        radios.PlayerPositions = vehicles.PlayerPositions;
        // items dropped and thrown on the ground (#206)
        Items.DroppedItems.Create(this, origin).PlayerPositions = vehicles.PlayerPositions;
        var chunksForDrops = _chunks;
        // on the water where there is some (#299): nothing is dropped onto a lake bed
        Items.DroppedItems.GroundHeight = p => chunksForDrops != null && chunksForDrops.TryGetSurface(p, out float y) ? y : null;
        Audio.Hearing.Ground = Items.DroppedItems.GroundHeight;
        // every body that may hold a radio that plays (#168): the remote players and this one
        radios.Players = () =>
        {
            var all = _players?.GetChildren().OfType<FootPlayer>().ToList() ?? new List<FootPlayer>();
            if (LocalPlayer is { } me && !all.Contains(me)) all.Add(me);
            return all;
        };
        Audio.Cd.CdLibrary.Create(this, server: false);
        // the radio by the pastor rat in every church (#370)
        Interiors.ChurchRadios.Create(this);
        Net.ClockSync.Create(this);
        // live stations in cars (#179): offline this machine tunes them itself
        if (Systems.On(Systems.Audio))
        {
            var webRadio = Audio.Live.WebRadio.Create(this);
            webRadio.Players = radios.Players;
            webRadio.Listener = () => Audio.Ears.Of(this) ?? LocalPlayer?.GlobalPosition;
            if (Audio.Live.WebRadioCheck.Create(() => LocalPlayer, () => _players, networked: false) is { } webRadioOffline) AddChild(webRadioOffline);
        }
        // the Africa Twin at Riddes: placed here offline, by the server online
        AddChild(new World.AfricaTwinEgg(_chunks));
        // the cars already standing in the car parks (#499); the server promotes one when it is
        // touched, every peer works the fleet out for itself from the tile and nothing is sent
        if (Systems.On(Systems.Dormant) && _chunks.Origin is { } dormantOrigin)
            AddChild(new Vehicles.DormantVehicles(_chunks, dormantOrigin));
        // the paddle steamer at the Nyon landing (#303): likewise
        AddChild(new World.SteamerBerth(_chunks));
        // jetskis and speedboats along the harbour jetties (#383): likewise
        // A320s with airstairs, the AN-124 and the freighter at the airports' stands (#422), put back a while after they are taken
        if (Systems.On(Systems.Airports)) AddChild(new World.AirportStands(_chunks));
        if (World.EggProbe.Mode() is { } eggMode) AddChild(new World.EggProbe(eggMode, () => LocalPlayer, _chunks, origin));

        // Guns on the plane and helicopter. World/Combat on both sides, like World/Vehicles.
        var combat = Combat.CombatManager.Create(this, _chunks, origin, server: false);
        combat.LocalPlayer = () => _onFoot ? LocalPlayer : null;

        // Building interiors: E opens a front door, and you walk through it. Same node path as the
        // server's, which plans and stores them; offline this client does both.
        if (Systems.On(Systems.Interiors))
        {
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
        }

        var environment = StyleKit.NewEnvironment();
        _worldEnvironment = new WorldEnvironment { Environment = environment };
        AddChild(_worldEnvironment);

        // which occasions are running (Halloween, Christmas…): the calendar offline, the server's
        // word online. Before the clock, which reads its sun and sky from it.
        if (Systems.On(Systems.Occasions))
        {
            Occasions.OccasionManager.Create(this);
            // their props, dressed onto each tile as its buildings load
            AddChild(new Occasions.OccasionDecor(_chunks, origin, _cache));
            // …the creatures in the air around the camera, and their sounds
            AddChild(new Occasions.OccasionCreatures(_chunks, origin, () => GetViewport().GetCamera3D()));
            AddChild(new Occasions.OccasionAmbience(_chunks, origin, EarNode));
            // …and snow falling round the camera, except indoors
            AddChild(new Occasions.OccasionPrecip());
        }
        // a sign over every bank door (#213)
        if (Systems.On(Systems.Interiors)) AddChild(new Interiors.BankSigns(_chunks));
        // the IKEA totem out by the road (#501), the same tile hook as the door signs
        if (Systems.On(Systems.Interiors)) AddChild(new Interiors.IkeaPylon(_chunks));

        // the clock: sun, light colour, sky and night for every shader and the environment.
        // Off (--systems without sky): no clock, the style's fixed sun and the background colour.
        var chunksForSky = _chunks;
        if (Systems.On(Systems.Sky))
        {
            _dayNight = new World.DayNight(environment)
            {
                GroundHeight = p => chunksForSky.TryGetHeight(p, out float y) ? y : null,
            };
            AddChild(_dayNight);
        }
        ApplySun();

        // cars on the roads and trains on the railway, around wherever the view is
        if (!Systems.On(Systems.Trains)) GameSettings.Current.Trains = false;   // this run only: not committed
        if (Systems.On(Systems.Traffic) || Systems.On(Systems.Trains))
        {
            if (!Systems.On(Systems.Traffic)) GameSettings.Current.TrafficCars = 0;
            var obstacles = new List<(Vector3 Pos, Vector3 Vel)>();
            _traffic = new World.Traffic(_chunks, origin)
            {
                Focus = () => GetViewport().GetCamera3D()?.GlobalPosition,
                // every player it can meet — the local one, remote racers, race NPCs — with how each moves:
                // the traffic makes way for a race going through it (#85)
                // from the tick's shared snapshot, into one reused list (#221)
                Obstacles = () =>
                {
                    obstacles.Clear();
                    foreach (var s in PlayerSnapshot.Of(GetTree())) obstacles.Add((s.Pos, s.Vel));
                    return obstacles;
                },
            };
            AddChild(_traffic);
        }
        if (Systems.On(Systems.Npcs) && World.NpcWatch.FromArgs() is { } npcWatch) AddChild(npcWatch);
        if (_traffic != null && World.TrafficProbe.ParseArgs() is { Requested: true } tcheck)
        {
            var tcam = new Camera3D { Name = "TrafficCam", Far = GameSettings.Current.CameraFar };
            AddChild(tcam);
            tcam.MakeCurrent();
            _chunks.AddAnchor(tcam);
            var (tE, tN) = SpawnPoint.ParseTarget(Launch);
            tcam.Position = origin.ToWorld(tE, tN, 600);
            AddChild(new World.TrafficProbe(_traffic, tcam, tcheck.Shot)
                { Origin = origin });
        }

        var chunks = _chunks;
        var cache = _cache;
        // The verification tools that start in place of a spawn, in the order they are tried: one list
        // says both whether one runs (placedByTool) and how it starts (at the end of _Ready).
        ToolRun[] tools =
        {
            new(() => World.TrafficProbe.ParseArgs().Requested, ToolAnchor.Own, null),   // started above, with its own camera
            // pure analysis: it loads the tiles it needs itself, so it neither waits for streaming
            // nor cares where the spectator is
            new(() => Gpx.Cinema.CinemaProbe.ParseArgs() != null, ToolAnchor.Own, _ => new Gpx.Cinema.CinemaProbe(
                Gpx.Cinema.CinemaProbe.ParseArgs()!, origin, streamedSource, manifest.Tiles.Select(t => t.Id).ToHashSet())),
            new(() => Vehicles.VehicleProbe.ParseArgs().Requested, ToolAnchor.AtTarget,
                _ => new Vehicles.VehicleProbe(chunks, origin, Vehicles.VehicleProbe.ParseArgs().Shot)),
            new(() => Loot.GatherProbe.ParseArgs().Requested, ToolAnchor.Dropped,
                k => new Loot.GatherProbe(chunks, origin, k.Gathering, k.Items, Loot.GatherProbe.ParseArgs().Shot)),
            new(() => Birds.BirdProbe.ParseArgs().Requested, ToolAnchor.Dropped,
                k => new Birds.BirdProbe(chunks, origin, k.Birds, k.Items, Birds.BirdProbe.ParseArgs().Shot)),
            // tables only: no terrain wanted, and quitting mid-stream races the tile workers
            new(() => Loot.LootProbe.ParseArgs() != null, ToolAnchor.Dropped, _ => new Loot.LootProbe(cache, chunks, Loot.LootProbe.ParseArgs()!.Value)),
            new(() => Interiors.InteriorProbe.ParseArgs().Requested, ToolAnchor.AtTarget,
                _ => new Interiors.InteriorProbe(chunks, origin, cache, Interiors.InteriorProbe.ParseArgs().Shot)),
            new(() => Interiors.DoorWatchProbe.ParseArgs().Requested, ToolAnchor.AtTarget,
                _ => new Interiors.DoorWatchProbe(chunks, origin, Interiors.DoorWatchProbe.ParseArgs().Shot)),
            new(() => Interiors.GarageLinkProbe.ParseArgs().Requested, ToolAnchor.AtTarget,
                _ => new Interiors.GarageLinkProbe(chunks, origin, Interiors.GarageLinkProbe.ParseArgs().Shot)),
            new(() => Birds.BirdStrikeProbe.ParseArgs().Requested, ToolAnchor.AtTarget,
                k => new Birds.BirdStrikeProbe(chunks, origin, k.Birds, Birds.BirdStrikeProbe.ParseArgs().Shot)),
            new(() => Combat.CombatProbe.ParseArgs().Requested, ToolAnchor.AtTarget,
                _ => new Combat.CombatProbe(chunks, origin, Combat.CombatProbe.ParseArgs().Shot)),
            new(() => FlightCheckProbe.ParseArgs() != null, ToolAnchor.AtTarget, _ =>
            {
                var f = FlightCheckProbe.ParseArgs()!.Value;
                return new FlightCheckProbe(chunks, origin, f.Kind, f.Shot);
            }),
            new(HitboxProbe.Requested, ToolAnchor.Own, _ => new HitboxProbe(chunks, origin)),
            new(SyncProbe.Requested, ToolAnchor.AtTarget, _ => new SyncProbe(chunks, origin)),
            new(MantleProbe.Requested, ToolAnchor.AtTarget, _ => new MantleProbe(chunks, origin)),
            new(VoidProbe.Requested, ToolAnchor.AtTarget, _ => new VoidProbe(chunks, origin)),
            new(() => RoadPerfProbe.ParseArgs() != null, ToolAnchor.Own, _ =>
            {
                var roadPerf = RoadPerfProbe.ParseArgs()!.Value;
                return new RoadPerfProbe(roadPerf.Dir, roadPerf.Label);
            }),
            new(RoadStandProbe.Requested, ToolAnchor.AtTarget, _ => new RoadStandProbe(chunks, origin)),
            new(() => DriveProbe.ParseArgs().Requested, ToolAnchor.AtTarget, _ =>
            {
                var d = DriveProbe.ParseArgs();
                return new DriveProbe(chunks, origin, d.Shot, d.Car, d.Seconds);
            }),
            new(() => World.ArrivalProbe.ParseArgs().Requested, ToolAnchor.AtTarget,
                _ => new World.ArrivalProbe(chunks, origin, World.ArrivalProbe.ParseArgs().Prefix)),
            new(() => World.TreeCheck.ParseArgs().Requested, ToolAnchor.AtTarget,
                _ => new World.TreeCheck(chunks, origin, World.TreeCheck.ParseArgs().Shot)),
            new(() => TruckProbe.Requested, ToolAnchor.AtTarget, _ => new TruckProbe(chunks, origin)),
            new(() => Terrain.ParkingProbe.ParseArgs().Requested, ToolAnchor.AtTarget,
                _ => new Terrain.ParkingProbe(chunks, origin, Terrain.ParkingProbe.ParseArgs().Shot)),
            new(() => Vehicles.WakeProbe.ParseArgs().Requested, ToolAnchor.AtTarget,
                _ => new Vehicles.WakeProbe(chunks, origin, Vehicles.WakeProbe.ParseArgs().Shot)),
            // the anchor on the spawn, so the tile under the rider arrives with collision: without
            // it the probe drops through an empty world and measures gravity
            new(() => RideProbe.ParseArgs() != null, ToolAnchor.AtTarget, _ =>
            {
                var r = RideProbe.ParseArgs()!.Value;
                return new RideProbe(chunks, origin, r.Kind, r.Seconds, r.Shot);
            }),
            new(() => TunnelProbe.ParseArgs() != null, ToolAnchor.Own, _ =>
            {
                var probe = TunnelProbe.ParseArgs()!;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                double e = double.Parse(probe[0], inv), n = double.Parse(probe[1], inv);
                // park the anchor on the portal so its chunk streams in with collision
                _spectator!.Position = origin.ToWorld(e, n, 1200);
                return new TunnelProbe(chunks, origin, e, n, double.Parse(probe[2], inv));
            }),
            new(() => Terrain.WaterProbe.ParseArgs() != null, ToolAnchor.Own, _ =>
            {
                var w = Terrain.WaterProbe.ParseArgs()!;
                // the anchor on the point, so its tile streams in with collision
                _spectator!.Position = origin.ToWorld(w[0], w[1], 1200);
                return new Terrain.WaterProbe(chunks, origin, w[0], w[1], w[2], w.Length > 3 ? w[3] : 0);
            }),
            new(() => FlightProbe.ParseArgs() != null, ToolAnchor.Own, _ =>
            {
                var fly = FlightProbe.ParseArgs()!;
                FreeSpectator();
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                return new FlightProbe(_spectator!, chunks,
                    new Vector3(float.Parse(fly[0], inv), float.Parse(fly[1], inv), float.Parse(fly[2], inv)),
                    float.Parse(fly[3], inv), float.Parse(fly[4], inv), double.Parse(fly[5], inv));
            }),
            new(() => StreetFlight.ParseArgs() != null, ToolAnchor.Own, _ =>
            {
                var (speed, seconds) = StreetFlight.ParseArgs()!.Value;
                FreeSpectator();
                return new StreetFlight(_spectator!, chunks, origin, speed, seconds);
            }),
            new(() => ShotRunner.ParseArgs() != null, ToolAnchor.Own, _ =>
            {
                var shot = ShotRunner.ParseArgs()!;
                FreeSpectator();
                // InvariantCulture: this project is developed on a fr-CH machine where the
                // default decimal separator would reject "1500.5"
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                return new ShotRunner(_spectator!,
                    new Vector3(float.Parse(shot[0], inv), float.Parse(shot[1], inv), float.Parse(shot[2], inv)),
                    float.Parse(shot[3], inv), float.Parse(shot[4], inv), double.Parse(shot[5], inv), shot[6]) { Origin = _worldOrigin };
            }),
            new(() => ShotRunner.ParseQueueArg() != null, ToolAnchor.Own, _ =>
            {
                FreeSpectator();
                var runner = ShotRunner.ForQueue(_spectator!, ShotRunner.ParseQueueArg()!, _worldOrigin);
                runner.GroundHeight = at => chunks.TryGetSurface(at, out float h) ? h : null;
                runner.RunCommand = line => _chat?.Send(line);
                return runner;
            }),
        };
        // Start somewhere with something to look at, not at the world origin: after a
        // large import that is usually empty space. "--at E,N" overrides it (LV95 metres).
        // --shot and --probe place the camera themselves, and a spawn drop would fight
        // them for the height.
        bool placedByTool = tools.Any(t => t.Requested());
        // --wheelwatch spawns normally, but must not grab the pointer either
        MouseCapture.Disabled |= Player.WheelProbe.WatchRole != null;
        // a check running in a window must leave the pointer to whoever is using the machine
        MouseCapture.Disabled |= placedByTool;

        _spectator = new SpectatorCamera { Name = "SpectatorCamera" };
        AddChild(_spectator);
        _chunks.AddAnchor(_spectator);

        if (!placedByTool)
        {
            var (spawnE, spawnN) = SpawnPoint.ParseTarget(Launch);
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
        // a forklift forks a hall pallet and sets it down in the yard; the other client watches (#583)
        if (Items.PalletNetProbe.ParseArgs() is { } palletRole) AddChild(new Items.PalletNetProbe(palletRole, () => LocalPlayer));
        if (Player.HeavyNetProbe.ParseArgs() is { } heavyRole) AddChild(new Player.HeavyNetProbe(heavyRole, () => LocalPlayer));
        if (Player.CrashNetProbe.ParseArgs() is { } crashRole) AddChild(new Player.CrashNetProbe(crashRole, () => LocalPlayer));
        if (Player.PassengerProbe.ParseArgs() is { } passengerRole) AddChild(new Player.PassengerProbe(passengerRole, () => LocalPlayer));
        if (Player.DeckProbe.ParseArgs() is { } deckRole) AddChild(new Player.DeckProbe(deckRole, () => LocalPlayer));
        if (Player.ExitProbe.Requested) AddChild(new Player.ExitProbe(() => LocalPlayer));
        if (Audio.EarsProbe.Requested) AddChild(new Audio.EarsProbe(() => LocalPlayer));
        if (Items.RadioPanelProbe.Requested) AddChild(new Items.RadioPanelProbe(() => LocalPlayer));
        if (Player.EmoteWheelProbe.Requested) AddChild(new Player.EmoteWheelProbe(() => LocalPlayer));
        if (Items.SparkleProbe.Requested) AddChild(new Items.SparkleProbe(() => LocalPlayer));
        if (World.WaterCheck.Requested) AddChild(new World.WaterCheck(() => LocalPlayer));
        if (World.SignalNetProbe.Requested) AddChild(new World.SignalNetProbe(server: false));
        if (Player.BoatCheck.Role is { } boatRole) AddChild(new Player.BoatCheck(boatRole, () => LocalPlayer));
        if (Player.SteamerCheck.Role is { } steamerRole) AddChild(new Player.SteamerCheck(steamerRole, () => LocalPlayer));
        if (Player.SwimCheck.Requested) AddChild(new Player.SwimCheck(() => LocalPlayer));
        if (Items.Fishing.FishProbe.Requested) AddChild(new Items.Fishing.FishProbe(() => LocalPlayer));
        if (Player.CabinCheck.Requested) AddChild(new Player.CabinCheck(() => LocalPlayer));
        if (Player.FreighterCheck.Requested) AddChild(new Player.FreighterCheck(() => LocalPlayer));
        if (Player.An124Check.Requested) AddChild(new Player.An124Check(() => LocalPlayer));
        if (Player.HoldCheck.Requested) AddChild(new Player.HoldCheck(() => LocalPlayer));
        if (Player.AirstairsCheck.Requested) AddChild(new Player.AirstairsCheck(() => LocalPlayer));
        if (World.AirportCheck.Requested) AddChild(new World.AirportCheck(() => LocalPlayer));

        // The inventory is this machine's, not the player node's: it outlives a respawn or a
        // reconnect, and the player it acts on is resolved per frame like the picker's.
        var inventory = Items.InventoryUiProbe.Requested || Items.EconomyProbe.Password != null
            || Loot.LootSyncProbe.Role != null || Loot.LockSyncProbe.Role != null || Interiors.LiftSyncProbe.Role != null || Loot.BankProbe.Role != null
            || Items.PlacedProbe.Role != null || Birds.BirdNetProbe.Role != null || Birds.PigeonNetProbe.Role != null || Player.AirlinerNetProbe.Role != null || Player.StairsNetProbe.Role != null || Player.ExcavatorNetProbe.Role != null || Items.SitePalletNetProbe.Role != null || Player.HoldNetProbe.Role != null || Player.FreighterNetProbe.Role != null || Player.An124NetProbe.Role != null || Items.PhotoProbe.Requested || Items.UseAnimProbe.Role != null
            || Items.ShotgunProbe.Role != null || Items.PlantProbe.Role != null || Items.DropCheck.Requested
            || Items.PvpProbe.Role != null || BattleRoyale.BrProbe.Role != null || Items.InteractCheck.Requested || Items.RadioPanelProbe.Requested
            || Items.BonkCheck.Requested || Build.BuildProbe.Requested || Build.BuildNetProbe.Role != null || Build.GadgetProbe.Requested || Build.GadgetNetProbe.Role != null || BattleRoyale.PrefabProbe.Requested || Crafting.CampfireProbe.Requested || Crafting.CampfireNetProbe.Role != null || Loot.ShopProbe.Role != null || Player.SwimCheck.Requested || Items.Fishing.FishProbe.Requested || Items.Fishing.FishNetProbe.Role != null || Player.SwimNetProbe.Role != null || Player.BoatNetProbe.Role != null || Player.SteamerNetProbe.Role != null || Vehicles.ParkingNetProbe.Mode() != null
            ? Items.Inventory.Scratch() : Items.Inventory.Load();
        if (Crafting.CampfireProbe.Requested || Crafting.CampfireNetProbe.Role != null) Crafting.CampfireProbe.Stock(inventory);
        if (Items.PlantProbe.Role != null) inventory.Put(Items.Inventory.HotbarSize - 1, new Items.ItemStack(Items.ItemId.SwissFlag, 1));   // on the hotbar for --hold
        if (Items.ShotgunProbe.Role != null) { inventory.Put(Items.Inventory.HotbarSize - 1, new Items.ItemStack(Items.ItemId.Shotgun, 1)); inventory.Add(Items.ItemId.Shells, 25); }   // on the hotbar for --hold
        if (Items.PvpProbe.Role != null) Items.PvpProbe.Stock(inventory);
        if (Build.BuildProbe.Requested || Build.BuildNetProbe.Role != null) Build.BuildProbe.Stock(inventory);
        // the account claimed cash goes to: the server's online, this machine's offline. Made
        // before the items, whose panel shows the balance from its first frame.
        Items.Bank.Create(this, inventory);
        var items = new Items.ItemController(inventory, origin)
        {
            ActivePlayer = () => _onFoot ? LocalPlayer : null,
        };
        AddChild(items);
        _items = items;
        AddChild(new Player.EmoteWheel(() => _onFoot ? LocalPlayer : null) { Name = "EmoteWheel" });
        if (Items.InventoryUiProbe.Requested) AddChild(new Items.InventoryUiProbe(items));
        if (Loot.LootSyncProbe.Role != null) AddChild(new Loot.LootSyncProbe(items, origin));
        if (Loot.LockSyncProbe.Role != null) AddChild(new Loot.LockSyncProbe(items, origin));
        if (Interiors.LiftSyncProbe.Role != null) AddChild(new Interiors.LiftSyncProbe(items, origin));
        if (Loot.BankProbe.Role != null) AddChild(new Loot.BankProbe(items, origin));
        if (Loot.ShopProbe.Role != null) AddChild(new Loot.ShopProbe(items, origin));
        if (Player.WheelProbe.WatchRole != null) AddChild(new Player.WheelProbe { Name = "WheelProbe" });
        if (Items.PlacedProbe.Role != null) AddChild(new Items.PlacedProbe(items));
        if (Birds.BirdNetProbe.Role != null) AddChild(new Birds.BirdNetProbe(items));
        if (Birds.PigeonNetProbe.Role != null) AddChild(new Birds.PigeonNetProbe(items));
        if (Player.AirlinerNetProbe.Role != null) AddChild(new Player.AirlinerNetProbe(items));
        if (Player.StairsNetProbe.Role != null) AddChild(new Player.StairsNetProbe(items));
        if (Player.ExcavatorNetProbe.Role != null) AddChild(new Player.ExcavatorNetProbe(items));
        if (Items.SitePalletNetProbe.Role != null) AddChild(new Items.SitePalletNetProbe(items));
        if (Player.FreighterNetProbe.Role != null) AddChild(new Player.FreighterNetProbe(items));
        if (Player.An124NetProbe.Role != null) AddChild(new Player.An124NetProbe(items));
        if (Player.HoldNetProbe.Role != null) AddChild(new Player.HoldNetProbe(items));
        if (Items.UseAnimProbe.Role != null) AddChild(new Items.UseAnimProbe(items));
        if (Items.PhotoProbe.Requested) AddChild(new Items.PhotoProbe(items));
        if (Items.ShotgunProbe.Role != null) AddChild(new Items.ShotgunProbe(items));
        if (Items.PlantProbe.Role != null) AddChild(new Items.PlantProbe(items));
        if (Items.PvpProbe.Role != null) AddChild(new Items.PvpProbe(items));
        if (Build.BuildProbe.Requested) AddChild(new Build.BuildProbe(items));
        if (Build.BuildNetProbe.Role != null) AddChild(new Build.BuildNetProbe(items));
        if (Build.GadgetProbe.Requested) { Build.GadgetProbe.Stock(items.Inventory); AddChild(new Build.GadgetProbe(items)); }
        if (Build.GadgetNetProbe.Role != null) AddChild(new Build.GadgetNetProbe(items));
        // waking a dormant car over the network (#499), checked on the remote peer
        if (Vehicles.ParkingNetProbe.Mode() is { } parkingNet && _chunks.Origin is { } parkingOrigin)
            AddChild(new Vehicles.ParkingNetProbe(parkingNet, _chunks, parkingOrigin));
        if (BattleRoyale.PrefabProbe.Requested) AddChild(new BattleRoyale.PrefabProbe());
        if (BattleRoyale.BrProbe.Role != null) AddChild(new BattleRoyale.BrProbe(items));
        if (Crafting.CampfireProbe.Requested) AddChild(new Crafting.CampfireProbe(items));
        if (Crafting.CampfireNetProbe.Role != null) AddChild(new Crafting.CampfireNetProbe(items));
        if (Player.SwimNetProbe.Role != null) AddChild(new Player.SwimNetProbe(items));
        if (Player.EmoteNetProbe.Role != null) AddChild(new Player.EmoteNetProbe(items));
        if (Player.FightNetProbe.Role != null) AddChild(new Player.FightNetProbe(items));
        if (Items.SwissNetProbe.Role != null) AddChild(new Items.SwissNetProbe(items));
        if (Items.Fishing.FishNetProbe.Role != null) AddChild(new Items.Fishing.FishNetProbe(items));
        if (World.ClockNetProbe.Role != null) AddChild(new World.ClockNetProbe(items));
        if (SpeedNetProbe.Role != null) AddChild(new SpeedNetProbe(items));
        if (Net.SleeperProbe.Role != null) AddChild(new Net.SleeperProbe(items));
        if (Net.TransferProbe.Role != null) AddChild(new Net.TransferProbe(items));
        if (Player.BoatNetProbe.Role != null) AddChild(new Player.BoatNetProbe(items));
        if (Player.SteamerNetProbe.Role != null) AddChild(new Player.SteamerNetProbe(items));
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
        // the item catalogue types its commands into the chat, so the server checks them (#262)
        items.RunCommand = _chat.Send;
        _chat.CatalogueRequested += () => items.Catalogue.Open();

        // bottom right: the controls that apply here (F1, every control, is the shell's)
        var prompts = PromptBar.Create();
        prompts.Source = Prompts;
        AddChild(prompts);
        // a phone's controls (#63): an on-screen pad labelled from the same prompts
        if (TouchControls.Wanted)
        {
            var touch = TouchControls.Create();
            touch.Source = Prompts;
            AddChild(touch);
            if (TouchCheck.Requested) AddChild(new TouchCheck(() => LocalPlayer, touch));
        }

        // Scavenging: what the furniture in those interiors holds. Same node path as the server's,
        // which decides who gets what; offline this client does both.
        // held-item events (shots, flashes) and placed objects (flags, photos): same node paths
        // as the server's, which relays the first and owns the second; offline this client does both
        Items.ItemEvents.Create(this, origin, server: false);
        // the images of stuck Polaroids, fetched from the server by hash (before the list draws them)
        Items.PhotoTransfer.Create(this, server: false);
        Items.PlacedObjects.Create(this, origin, server: false, networked: Launch.Networked);
        // players who left, asleep where they were, and waking at our own (#644)
        Net.Sleepers.Create(this, origin, server: false).Teleporter = _teleporter;
        // pallets a forklift has moved (#583): the server keeps them for the session, offline this client does
        Items.PalletService.Create(this, origin, server: false).Source = () => _chunks?.Source;
        // built structures (#274): the server owns them, offline this client does
        if (Systems.On(Systems.Build))
        {
            var structures = Build.Structures.Create(this, origin, server: false, networked: Launch.Networked);
            structures.GroundAt = p => _chunks != null && _chunks.TryGetHeight(p, out float h) ? h : null;
        }

        if (Systems.On(Systems.Loot)) Loot.LootService.Create(this).Items = items;
        // shops and PAUSA vending machines (#273): same node path as the server's, which keeps the sold counts
        if (Systems.On(Systems.Loot)) Loot.ShopService.Create(this).Items = items;
        // the radio's panel: CDs to play, burn a new one, pick it up (opened from FootPlayer.TryInteract)
        _radioUi = Items.RadioUi.Create(() => LocalPlayer, items.Inventory);
        _radioUi.Give = items.Give;
        AddChild(_radioUi);
        if (Items.CarCdCheck.Create(() => LocalPlayer, () => _players, items.Inventory, networked: false) is { } carCdShots) AddChild(carCdShots);
        if (Items.InteractCheck.Create(() => LocalPlayer, items.Inventory) is { } interactCheck) AddChild(interactCheck);
        // ...and from the land itself: stone, water, firewood (hold G / pad X outdoors)
        // (null only with loot or birds off, when no probe that needs them runs)
        Loot.Gathering gathering = null!;
        if (Systems.On(Systems.Loot)) AddChild(gathering = new Loot.Gathering(_chunks, origin, items));
        // birds around the player, from the real land cover; the shotgun hunts them (J: journal)
        Birds.BirdLife birds = null!;
        if (Systems.On(Systems.Birds))
        {
            AddChild(birds = new Birds.BirdLife(_chunks, origin, items));
            // online the birds are the server's (World/BirdNet: same path as there); offline this client runs them
            Birds.BirdNet.Create(this, birds, server: false);
        }

        // occasions: the treat / gift hunt (taken with the gather hold) and the seasonal hat
        if (Systems.On(Systems.Occasions))
        {
            AddChild(new Occasions.OccasionHunt());
            AddChild(new Occasions.OccasionHats(() => LocalPlayer, items.Inventory));
        }

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
            gathering?.Forget();
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

        // F9 or /debug: overlays, terrain layers and view modes, alone or as an admin (#339)
        var debug = new DebugMenu(_chunks, origin, () => _nearTrees, Toast,
            scale => _chat?.Send($"/speed {scale.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}"));
        AddChild(debug);
        _chat.DebugRequested += debug.Open;
        if (DebugMenuCheck.Requested) AddChild(new DebugMenuCheck(items, debug, _chunks));

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

        var kit = new ToolKit(items, gathering, birds);
        foreach (var tool in tools)
        {
            if (tool.Start == null || !tool.Requested()) continue;
            if (tool.Anchor == ToolAnchor.AtTarget)
            {
                var (e, n) = SpawnPoint.ParseTarget(Launch);
                _spectator.Position = origin.ToWorld(e, n, 1200);
            }
            else if (tool.Anchor == ToolAnchor.Dropped) _chunks.RemoveAnchor(_spectator);
            AddChild(tool.Start(kit));
            return;
        }
    }

    /// <summary>Where a verification tool wants the streaming anchor: on the <c>--at</c> spot, gone, or left to the tool.</summary>
    private enum ToolAnchor { AtTarget, Dropped, Own }

    /// <summary>A verification tool: whether the command line asks for it, and how it starts (null: started elsewhere).</summary>
    private readonly record struct ToolRun(Func<bool> Requested, ToolAnchor Anchor, Func<ToolKit, Node>? Start);

    /// <summary>What some tools need that only exists once the items are built.</summary>
    private readonly record struct ToolKit(Items.ItemController Items, Loot.Gathering Gathering, Birds.BirdLife Birds);

    /// <summary>A tool that drives the spectator camera itself: no fly controls, the pointer free.</summary>
    private void FreeSpectator()
    {
        _spectator!.SetProcess(false);
        _spectator.SetProcessUnhandledInput(false);
        Input.MouseMode = Input.MouseModeEnum.Visible;
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

        // Explore from the menus starts on foot, on open ground (#517), behind this screen
        if (Launch is { Mode: GameMode.Explore, FromCommandLine: false } && !_groundStarted)
        {
            _groundStarted = true;
            if (!_onFoot && _player == null)
            {
                AddChild(_player = new FootPlayer { Name = "Player", Terrain = _chunks });
                EnterFootMode(_player);
                _groundStart = new GroundStart(_chunks, _player);
            }
        }
        if (_groundStart is { Done: false } ground && !ground.Step(delta))
        {
            Report(LoadStage.PlacingYou, 0.34f);
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

    private static long CacheCapBytes => (long)(GameSettings.Current.CacheGb * 1024 * 1024 * 1024);

    private void OnSettingsChanged()
    {
        if (_chunkSource != null) _chunkSource.MaxCacheBytes = CacheCapBytes;
        foreach (var m in _worldMaterials) FogUniforms.Apply(m);
        // before the terrain takes the settings: its rings and mesh detail are the style's
        if (StyleKit.Restyle()) ApplyStyle();
        _chunks?.ApplySettings(GameSettings.Current);
        _chunks?.SetFallbackEnabled(GameSettings.Current.GeneratedFill);
        if (_ambience != null) _ambience.Volume = Audio.SfxBus.SliderGain(GameSettings.Current.AmbienceVolume);
        SetCameraFar(GameSettings.Current.CameraFar);
    }

    /// <summary>
    /// The rest of the world after <see cref="StyleKit.Restyle"/> moved every material over: the
    /// style's environment and sun. The terrain's rings and mesh detail follow in
    /// <see cref="ChunkManager.ApplySettings"/>, which rebuilds the tiles in place when the detail
    /// changed. The network session, the player and physics are untouched.
    /// </summary>
    private void ApplyStyle()
    {
        GD.Print($"[style] {StyleKit.Applied}");
        if (_worldEnvironment != null)
        {
            var environment = StyleKit.NewEnvironment();
            _worldEnvironment.Environment = environment;
            _dayNight?.SetEnvironment(environment);
        }
        ApplySun();
        ApplyNearTrees();
        ApplyPhotos();
    }

    /// <summary>
    /// The SWISSIMAGE drape (<see cref="PhotoLayer"/>) while the style has one: on the tiles'
    /// terrain material, from the local terrain folder's photos.
    /// </summary>
    private void ApplyPhotos()
    {
        bool want = StyleKit.HasPhotos && _chunks != null && _worldOrigin != null && _worldMaterials.Length > 0;
        if (want == (_photos != null)) return;
        _photos?.QueueFree();
        _photos = null;
        if (!want) return;
        _photos = new PhotoLayer(_chunks!, _worldOrigin!, _worldMaterials[0], TerrainPaths.FindChunkDir());
        AddChild(_photos);
    }

    /// <summary>
    /// The 3D trees near the camera, culled per tree, while the style's trees are too heavy to
    /// leave per tile (<see cref="MeshDetail.High"/>). The tiles hand their trees over as they
    /// rebuild at the new detail (<see cref="ChunkManager.RebuildVisuals"/>).
    /// </summary>
    /// <summary>What the audio systems listen from: the body's ears (#375), else the camera.</summary>
    private Node3D? EarNode() => Audio.Ears.Ready ? Audio.Ears.Instance : GetViewport().GetCamera3D();

    private void ApplyNearTrees()
    {
        // every restyle: each style has its own trees and range
        _nearTrees?.QueueFree();
        _nearTrees = null;
        if (StyleKit.Detail != MeshDetail.High || !StyleKit.TreeLod || _chunks == null || _treeMaterial == null) return;
        var (cone, crown) = ChunkNode.HighDetailTrees(_treeMaterial);
        _nearTrees = new NearTrees(CatalogueTree(ModelCatalog.TreeConifer) ?? cone,
            CatalogueTree(ModelCatalog.TreeBroadleaf) ?? crown, StyleKit.TreeReach);
        // under the terrain, an origin container: the floating origin moves it with the tiles
        _chunks!.AddChild(_nearTrees);
        // the debug menu may have hidden the trees before this style made its own
        _nearTrees.Visible = (_chunks.HiddenLayers & TileLayers.Trees) == 0;
    }

    /// <summary>
    /// The applied style's model for a tree (<see cref="ModelCatalog"/>), with its bark and leaf
    /// materials; null where the style has none and the builders' trees serve.
    /// </summary>
    private static Mesh? CatalogueTree(string id)
    {
        if (ModelCatalog.Mesh(id) is not { } model) return null;
        var mesh = (ArrayMesh)model.Duplicate();
        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            mesh.SurfaceSetMaterial(s, StyleKit.TreeSurface(id, ImpostorBake.IsLeaves(mesh, s)));
        return mesh;
    }

    /// <summary>The style's sun, or none: made here, pointed by <see cref="World.DayNight"/>.</summary>
    private void ApplySun()
    {
        _sun?.QueueFree();
        _sun = StyleKit.NewSun();
        if (_sun != null) AddChild(_sun);
        if (_dayNight != null) _dayNight.Sun = _sun;
    }

    private void OnRebuildRequested() => _chunks?.RebuildVisuals();

    /// <summary><c>/style</c> picked a style for this session.</summary>
    private void OnStyleChosen()
    {
        if (!StyleKit.Restyle()) return;
        ApplyStyle();
        _chunks?.ApplySettings(GameSettings.Current);
    }

    private void Toast(string message) => _items?.Ui.Toast(message);

    /// <summary>Boarding a Battle Royale on the fly camera (#425): back into the body, where it stands.</summary>
    private void OnPermissionsChanged()
    {
        if (Permissions.InMatch && !_onFoot && _chunks != null && LocalPlayer is { } body) EnterFootMode(body, inPlace: true);
    }

    /// <summary>
    /// Leaving to the title frees this world: everything static it subscribed to lets go here,
    /// or the next world's events would call into freed nodes (<c>docs/notes/ui/teardown.md</c>).
    /// </summary>
    public override void _ExitTree()
    {
        World.WaterField.Bind(null);
        World.WaterField.SetSeaState(0f);
        GameSettings.Changed -= OnSettingsChanged;
        if (NetworkChunkSource.Active == _chunkSource) NetworkChunkSource.Active = null;
        Permissions.Changed -= OnPermissionsChanged;
        StyleCommand.RebuildRequested -= OnRebuildRequested;
        StyleKit.Chosen -= OnStyleChosen;
        NearTrees.Forget();
        Vehicles.VehicleManager.Refused -= Toast;
        Vehicles.PassengerService.Said -= Toast;
        if (_networked)
        {
            Multiplayer.ConnectedToServer -= OnConnected;
            Multiplayer.ConnectionFailed -= OnConnectionFailed;
            Multiplayer.ServerDisconnected -= OnServerDisconnected;
        }
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
        if (!Systems.On(Systems.Network)) { Fail("The network is off for this run (--systems)."); return; }
        _networked = true;

        // The chat node (World/Chat, made at boot) is already where the server's RPCs route.
        if (Items.EconomyProbe.Password != null && _items != null && _chat != null)
            AddChild(new Items.EconomyProbe(_chat, _items.Inventory));

        // World/Race on both sides; the client side puts this player on the grid and times the run
        var race = World.RaceManager.CreateClient(_worldOrigin!);
        race.LocalPlayer = () => LocalPlayer;
        AddChild(race);

        // World/Fight on both sides (#495): the challenge, the match state, and its screen
        AddChild(Combat.FightManager.CreateClient());
        AddChild(new Combat.FightHud());

        // World/BattleRoyale (#177): the match HUD, the zone, the drop and the way back
        var br = BattleRoyale.BrManager.CreateClient(_worldOrigin!);
        br.LocalPlayer = () => _onFoot ? LocalPlayer : null;
        br.Inventory = () => _items?.Inventory;
        br.Teleport = (e, n, label) => _teleporter?.TeleportTo(e, n, label) == true;
        br.AddAnchor = node => _chunks?.AddAnchor(node);
        br.RemoveAnchor = node => _chunks?.RemoveAnchor(node);
        br.Source = () => _chunks?.Source;
        // the match's crates (#194): drawn on this client's own ground
        var crates = BattleRoyale.BrCrates.Create(this, _worldOrigin!, server: false);
        crates.GroundAt = at => _chunks != null && _chunks.TryGetSurface(at, out float h) ? h : null;
        br.Places = () => (IEnumerable<Terrain.Format.Place>?)_places?.All ?? Array.Empty<Terrain.Format.Place>();
        AddChild(br);
        if (CarSwitchCheck.Create(() => LocalPlayer, () => _players) is { } switchCheck) AddChild(switchCheck);
        if (RadioSyncCheck.Create(() => LocalPlayer, () => _players, _items?.Inventory) is { } radioCheck) AddChild(radioCheck);
        if (_items != null && Items.CarCdCheck.Create(() => LocalPlayer, () => _players, _items.Inventory, networked: true) is { } carCdCheck) AddChild(carCdCheck);
        if (Items.DropCheck.Create(() => LocalPlayer, () => _players, _items) is { } dropCheck) AddChild(dropCheck);
        if (_items != null && Items.BonkCheck.Create(() => LocalPlayer, () => _players, _items.Inventory) is { } bonkCheck) AddChild(bonkCheck);
        if (Audio.Live.WebRadioCheck.Create(() => LocalPlayer, () => _players, networked: true) is { } webRadioCheck) AddChild(webRadioCheck);

        _chat!.Kicked += OnKicked;

        // Merges the server's tile list so tiles this client never shipped with become streamable.
        _terrainSync = new ClientTerrainSync(_streamer!, _chunks!);
        // the sync runs its continuations on the thread pool, and the chat log is UI
        _terrainSync.Status += line =>
            Callable.From(() =>
            {
                _chatUi?.Append(line, ChatKind.System);
                LoadDetail = line;
            }).CallDeferred();

        // The town index arrives after this UI was built, so it has to be told to re-read.
        _terrainSync.PlacesReceived += () =>
            Callable.From(() =>
            {
                _places?.ReloadIndex();
                _ambience?.ReloadPlaces();
                Occasions.OccasionTowns.Reload();
            }).CallDeferred();

        // The landings (#377): the server's, and the tiles holding a pier build it again
        _terrainSync.LandingsReceived += index =>
            Callable.From(() =>
            {
                var before = World.Landings.Current;
                if (before.ToJson() == index.ToJson()) return;   // the same as this client's own
                World.Landings.Use(index);
                static bool Holds(Terrain.Format.LandingIndex l, Terrain.Format.TileId id) => l.RibbonsOf(id).Any() || l.BollardsOf(id).Any();
                _chunks?.RebuildPiers(id => Holds(before, id) || Holds(index, id));
            }).CallDeferred();

        // Same for the horizon: a client that shipped without one gets it during sync.
        _terrainSync.HorizonReceived += () =>
            Callable.From(() => _chunks?.Horizon?.Reload()).CallDeferred();
        AddChild(_terrainSync);

        // the version check comes first: nothing else is sent before the server welcomes us
        _handshake = Handshake.CreateClient();
        _handshake.Welcomed += OnWelcomed;
        _handshake.Refused += reason =>
        {
            GD.PushWarning($"[net] {reason}");
            Multiplayer.MultiplayerPeer?.Close();
            ReportDisconnect(reason);
        };
        AddChild(_handshake);

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
        AddChild(PlayerReplication.CreateSpawner(_worldOrigin!));
        AddChild(World.RaceNpcs.CreateClient());   // World/Npcs: the path its RPC routes by
        if (NetSmoothProbe.ParseArgs() is { } smooth) AddChild(new NetSmoothProbe(_players, smooth.Seconds, smooth.Label, smooth.MinSpeed));
        if (WallOffProbe.ParseArgs() is { } wallOff) AddChild(new WallOffProbe(_players, _chunks!, _worldOrigin!, wallOff));
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
        _handshake?.Begin();
    }

    /// <summary>The server speaks this client's protocol (<see cref="Handshake"/>): join for real.</summary>
    private void OnWelcomed()
    {
        GD.Print($"[world] the server speaks protocol {Handshake.Protocol}");

        // The server assigns the final name — it deduplicates and sanitises — so this is
        // a request, not a claim.
        string requested = Launch.PlayerName;
        _chat?.AnnounceName(requested.Length > 0 ? requested : $"Rider{Multiplayer.GetUniqueId()}");
        // hosted from the menu: the player who started the server runs it
        if (Launch.HostToken is { } token) _chat?.ClaimHost(token);

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
            // in a Battle Royale match M is the match map, and there is no teleporting anyway
            if (BattleRoyale.BrManager.Instance?.ToggleMap() == true) return;
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
    /// The first-run tutorial (#517) over this world, once: the shell calls it when the loading
    /// screen is gone, Settings when it is played again. Not in a replay.
    /// </summary>
    public void StartTutorial()
    {
        if (Tutorial.Current != null || Launch.Mode == GameMode.GpxReplay) return;
        AddChild(new Tutorial(
            walker: () => _onFoot ? LocalPlayer : null,
            flying: () => !_onFoot && _spectator is { Current: true },
            mapOpen: () => _places is { IsOpen: true },
            covered: () => Covered || _vehicleIntros is { Showing: true }));
    }

    private VehicleIntroCard? _vehicleIntros;

    /// <summary>
    /// The rides' mini tutorials (#517), each shown the first time the player drives that kind:
    /// the shell starts them with the world, in every session from the menus.
    /// </summary>
    public void StartVehicleIntros()
    {
        if (_vehicleIntros != null || Launch.Mode == GameMode.GpxReplay) return;
        AddChild(_vehicleIntros = new VehicleIntroCard(() => Viewer, () => Covered));
    }

    /// <summary>Whoever owns the camera on screen: the local player, or a body a probe made itself; null in the fly camera.</summary>
    private FootPlayer? Viewer =>
        (_onFoot ? LocalPlayer : null) ?? (XR.XrSession.Anchor ?? GetViewport().GetCamera3D())?.GetParent() as FootPlayer;

    /// <summary>Something owns the screen: a menu, the travel menu, the map, a replay.</summary>
    private bool Covered => MenuOpen?.Invoke() == true || _rides is { IsOpen: true } || _places is { IsOpen: true }
        || _gpx is { Active: true };

    /// <summary>
    /// The hints for the prompt bar: what the buttons do in the situation the player is in now.
    /// Short on purpose — the loot, door and gather prompts are already centred on screen, and
    /// the whole list is one F1 away.
    /// </summary>
    private IEnumerable<(string, string)> Prompts()
    {
        if (MenuOpen?.Invoke() == true || _gpx is { Active: true } || _rides is { IsOpen: true }) yield break;

        var shown = XR.XrSession.Anchor ?? GetViewport().GetCamera3D();
        var viewer = Viewer;
        if (viewer == null && shown == _spectator)
        {
            yield return (PlayerInput.ToggleMode, "Walk");
            yield return (PlayerInput.FlyUp, "Up");
            yield return (PlayerInput.FlyDown, "Down");
            yield return (PlayerInput.Teleport, "Map");
        }
        else if (viewer is { } p && IsInstanceValid(p))
        {
            // a bus door's button in reach, inside or out (#162)
            if (p.Vehicle == null && p.ButtonInReach() is { } button)
                yield return (PlayerInput.InteractMount, button.Open ? "Shut the door" : "Open the door");
            if (p.RidingAlong && p.StereoOwner != null) yield return (PlayerInput.RadioPanel, "Radio");
            // walking about in a vehicle, or sat in one somebody else hosts (#158, #162)
            if (p.Aboard)
            {
                if (p.DeckHint is { } deckHint) yield return (PlayerInput.InteractMount, deckHint);
                yield return (PlayerInput.CameraToggle, "Camera");
                yield return (PlayerInput.Inventory, "Inventory");
            }
            else if (p.Host is { } carrier)
            {
                yield return (PlayerInput.InteractMount, p.HostWalkable ? "Stand up" : "Get out");
                if (carrier.SeatIndex != 0) yield return (PlayerInput.TakeWheel, "Take the wheel");
                yield return (PlayerInput.CameraToggle, "Camera");
            }
            else if (p.Vehicle is { IsVehicle: true } vehicle)
            {
                if (p.SeatIndex > 0) yield return (PlayerInput.TakeWheel, "Take the wheel");
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
            else if (p.Fighting)
            {
                // a fist fight (#495): the moves; the specials are on the fight HUD
                yield return (PlayerInput.FightPunch, "Punch");
                yield return (PlayerInput.FightKick, "Kick");
                yield return (PlayerInput.FightBlock, "Block (hold)");
            }
            else if (Items.ItemController.Instance is { Inventory.HeldId: Items.ItemId.FishingRod } rodHand && rodHand.UsablePlayer != null)
            {
                // the rod (#493): every step names its own control on every device
                switch (rodHand.Rod.State)
                {
                    case Items.Fishing.FishingRod.Phase.Idle:
                        yield return (PlayerInput.UseItem, "Hold to wind up a cast");
                        break;
                    case Items.Fishing.FishingRod.Phase.Charging:
                        yield return (PlayerInput.UseItem, "Let go to cast");
                        yield return (PlayerInput.AimItem, "Cancel");
                        break;
                    case Items.Fishing.FishingRod.Phase.Waiting:
                    case Items.Fishing.FishingRod.Phase.Bite:
                        yield return (PlayerInput.UseItem, "Strike when the float dips");
                        yield return (PlayerInput.AimItem, "Wind in");
                        break;
                    case Items.Fishing.FishingRod.Phase.Fighting:
                        yield return (PlayerInput.UseItem, "Hold to reel; let go when it runs");
                        break;
                }
            }
            else if (Items.ItemController.Instance?.Throw.Active == true)
            {
                yield return (PlayerInput.UseItem, Items.ItemController.Instance.Throw.Charging ? "Let go to throw" : "Hold to wind up a throw");
                yield return (PlayerInput.AimItem, "Release: put it away");
            }
            else if (Items.Highlight.Pointed is Items.DroppedItem pointed)
                yield return (PlayerInput.InteractMount, $"Pick up {pointed.Label}");
            else if (Items.Highlight.Pointed is Items.RadioBody)
            {
                if (Items.ItemController.Instance?.Inventory.Held.IsEmpty == true) yield return (PlayerInput.UseItem, "Take the radio");
                yield return (PlayerInput.InteractMount, "Radio");
            }
            else if (Vehicles.VehicleReach.Current == null && Combat.FightManager.Client is { } fights
                     && NetLink.Online(this) && p.PointedFighter() is { } rival && FootPlayer.NetId(rival.Name) is { } rivalId)
                // another player looked at (#495): E challenges, or takes their challenge
                yield return (PlayerInput.InteractMount, fights.Prompt(rivalId));
            else if (p.Indoors)
            {
                // the chess type beat in a church (#370): dance to it, away from its radio and the door
                if (Interiors.ChurchRadios.At(p) == null && Interiors.InteriorManager.Instance?.AtExit(p) != true
                    && (p.DanceId != 0 || p.RatBeatHere(heard: true)))
                    yield return (PlayerInput.InteractMount, p.DanceId == 0 ? "Dance" : "Stop dancing");
                yield return (PlayerInput.Inventory, "Inventory");
            }
            else
            {
                if (Interiors.InteriorManager.Instance?.OutsideDoorInReach(p.GlobalPosition) == null
                    && Items.RadioManager.Instance?.NearestMusic(p.GlobalPosition, Items.RadioManager.DanceRadius, heard: p.DanceId == 0) != null)
                    yield return (PlayerInput.InteractMount, p.DanceId == 0 ? "Dance" : "Stop dancing");
                if (Vehicles.VehicleReach.Current is { } at)
                {
                    yield return (PlayerInput.InteractMount, at.Action);
                    if (at is { HasDoor: true, DoorOpen: true }) yield return (PlayerInput.CarDoor, "Close the door");
                }
                if (!Permissions.InMatch) yield return (PlayerInput.RideMenu, "Travel");
                yield return (PlayerInput.Inventory, "Inventory");
                yield return (PlayerInput.Teleport, "Map");
                if (!Permissions.InMatch) yield return (PlayerInput.ToggleMode, "Fly camera");
            }
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
        if (GameClock.Fixed) GameClock.Pace(Stage != LoadStage.Ready || _chunks is { Settled: false });
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

        // a Battle Royale is played on equal terms (#425): no scouting from the sky, and walking back
        // from the camera must not drop the body where it flew
        if (Permissions.InMatch)
        {
            if (_onFoot) Toast("No fly camera in a Battle Royale match.");
            else if (LocalPlayer is { } body) EnterFootMode(body, inPlace: true);
            return;
        }

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
            // the ground under the body streams out as the camera flies off: left running, it
            // falls, thuds and plays its sounds at the old spot. Frozen and hidden until we return.
            SetBodyParked(player, true);
            _onFoot = false;
            GD.Print($"[world] spectator at {_spectator.GlobalPosition}");
        }
    }

    /// <summary>The spawn point has not found the ground under the spawn yet.</summary>
    private bool _groundStarted;
    private GroundStart? _groundStart;

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

    /// <param name="inPlace">Back into the body where it stands, instead of dropping it under the fly camera.</param>
    private void EnterFootMode(FootPlayer player, bool inPlace = false)
    {
        SetBodyParked(player, false);
        if (!inPlace)
        {
            var pos = _spectator!.GlobalPosition;
            float ground = _chunks!.TryGetSurface(pos, out float h) ? h : pos.Y;
            player.GlobalPosition = new Vector3(pos.X, ground + 1f, pos.Z);
            player.Velocity = Vector3.Zero;
        }
        player.Camera.Current = true;
        _chunks!.RemoveAnchor(_spectator!);
        _chunks.AddAnchor(player);
        _onFoot = true;
        player.CarRadioTuned -= OnCarRadioTuned;
        player.CarRadioTuned += OnCarRadioTuned;
        GD.Print($"[world] on foot at {player.GlobalPosition}");
    }

    /// <summary>The body left behind by the fly camera: no physics, no sounds, not drawn.</summary>
    private static void SetBodyParked(FootPlayer player, bool parked)
    {
        player.ProcessMode = parked ? ProcessModeEnum.Disabled : ProcessModeEnum.Inherit;
        player.Visible = !parked;
    }

    private void OnCarRadioTuned(string station) => _items?.Ui.Toast($"Radio: {station}");
}
