using Godot;
using UnitSport.Net;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// Dedicated server: no meshes, no rendering — terrain height data is streamed around
/// every connected player for (future) validation, and the MultiplayerSpawner owns the
/// lifecycle of player nodes. Transforms are client-authoritative and relayed by ENet.
/// Ground with no terrain data is generated here exactly as on the clients.
/// </summary>
public partial class ServerWorld : Node3D, IOriginContainer
{
    private InterestService? _interest;
    private Vehicles.PassengerService? _passengers;
    private ChunkManager? _chunks;
    private Node3D? _players;
    private MultiplayerSpawner? _spawner;
    private Vehicles.VehicleManager? _vehicles;
    private Items.RadioManager? _radios;
    private Items.DroppedItems? _dropped;
    private PlayerRegistry? _registry;
    private ChatManager? _chat;
    private ChunkStreamer? _streamer;
    private Interiors.InteriorManager? _interiors;
    private WorldOrigin? _origin;
    private Items.PlacedObjects? _placed;
    private Occasions.OccasionManager? _occasions;
    private World.RaceNpcs? _npcs;
    private BattleRoyale.BrManager? _br;
    private BattleRoyale.BrCrates? _brCrates;

    public override async void _Ready()
    {
        if (ServerStats.Requested) AddChild(new ServerStats { Name = "ServerStats" });
        string chunkDir = TerrainPaths.FindChunkDir();
        // a test course built in code instead of the map (#221, Core/Systems): the same as the clients'
        IChunkSource local = Systems.FixtureCourse is { } course
            ? Terrain.Fixture.FixtureChunkSource.Create(course, SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N)
                ?? throw new ArgumentException($"no fixture course '{course}'")
            : new LocalChunkSource(chunkDir);
        var manifest = await local.LoadManifestAsync();
        var args = OS.GetCmdlineUserArgs();
        bool generatedWorld = Array.IndexOf(args, "--generated-world") >= 0;

        // Unlike a client, a server cannot shrug this off: it is the authority on where the
        // world is and the only source of terrain for clients that lack it. Starting anyway
        // would hand every client an origin of 0/0 and a world with nothing in it — unless it
        // is asked to serve a generated world, which is then all there is (and says so).
        if (manifest.Tiles.Count == 0 && !generatedWorld)
        {
            GD.PushError(
                $"[server] no terrain data in {chunkDir}. A server has nothing to serve and no "
                + "world origin to hand out. Generate the chunks first (see the README), point at "
                + "an existing set with --chunks <dir>, UNITSPORT_CHUNKS or the terrain_location.json "
                + "MapSetup writes, or run a generated world with --generated-world.");
            GetTree().Quit(1);
            return;
        }

        var origin = manifest.Tiles.Count > 0
            ? new WorldOrigin(manifest.SuggestedOriginLv95.E, manifest.SuggestedOriginLv95.N)
            : new WorldOrigin(SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N);
        _origin = origin;
        GD.Print($"[server] {manifest.Tiles.Count} tiles, origin LV95 {origin.E}/{origin.N}"
            + (manifest.Tiles.Count == 0 ? " (generated world)" : ""));

        // The same generated fill as every client's, anchored at the same point, so height
        // queries, interiors and loot work on generated ground and agree with what players see.
        // "--generated off" turns it off, as on a client.
        var fallback = !Systems.On(Systems.Generated) || local is Terrain.Fixture.FixtureChunkSource ? null
            : new FallbackChunkSource(local,
            new ProceduralWorld(SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N),
            SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N,
            enabled: generatedWorld || !GeneratedOff(args)) { Log = s => GD.Print(s) };
        // The server holds 5 KB coarse grids (ChunkManager, BuildMeshes off), plus whatever an
        // interior plan reads lazily: 32 MB is thousands of tiles, and a fixed ceiling.
        var source = new CachingChunkSource(fallback ?? local, 32L * 1024 * 1024);
        if (fallback != null) fallback.Neighbours = source;

        // A headless server draws nothing, so nothing capped its loop: it spun as fast as a core
        // allows. 60 matches the physics tick and every client's send rate is well under it.
        Engine.MaxFps = 60;

        _chunks = new ChunkManager { Name = "Terrain", BuildMeshes = false, BuildCollision = false };
        _chunks.Initialize(source, origin, manifest, null);
        if (fallback != null) _chunks.UseFallback(fallback, source.Invalidate);
        AddChild(_chunks);
        // the water (#299): the server answers water queries too, and owns the sea state
        World.WaterField.Bind(_chunks);
        if (World.SeaStateCommand.FromArgs(args, out string seaError) is { } sea) World.WaterField.SetSeaState(sea);
        else if (seaError.Length > 0) GD.PushWarning($"[water] {seaError}");
        GD.Print($"[server] sea state {World.SeaStateCommand.Describe(World.WaterField.SeaState)}");

        _players = new Node3D { Name = "Players" };
        _players.AddToGroup(OriginShifter.ContainerGroup);
        AddChild(_players);
        // who may see whom: decided here for everyone, before any player node exists (each
        // player's synchronizer looks it up in _Ready). Line of sight from the 100 m horizon lattice.
        var horizon = await source.LoadHorizonAsync();
        _interest = InterestService.CreateServer(this, _players,
            horizon != null ? InterestService.HorizonGround(horizon, origin) : null);

        _spawner = PlayerReplication.CreateSpawner();
        AddChild(_spawner);
        // race NPCs: spawned here for everyone, simulated on the client that asked (issue #39)
        AddChild(_npcs = World.RaceNpcs.CreateServer(_spawner, _players));

        // vehicles standing in the world; the server spawns and removes them for everyone
        _vehicles = Vehicles.VehicleManager.Create(this, null);
        // who sits in whose vehicle (#158): handed out here
        _passengers = Vehicles.PassengerService.Create(this);
        _passengers.Players = _players;
        _vehicles.PlayerPositions = () => _players!.GetChildren().OfType<Node3D>().Select(p => p.GlobalPosition);
        // radios thrown into the world, and the CDs they play; the clock everyone plays them by
        _radios = Items.RadioManager.Create(this);
        _radios.PlayerPositions = _vehicles.PlayerPositions;
        // items dropped and thrown on the ground (#206), the same spawn-and-claim pattern
        _dropped = Items.DroppedItems.Create(this);
        _dropped.PlayerPositions = _vehicles.PlayerPositions;
        Audio.Cd.CdLibrary.Create(this, server: true);
        Net.ClockSync.Create(this);
        // live stations in cars: tuned here once each, relayed to whoever listens (#179)
        Audio.Live.WebRadio.Create(this);
        // an Africa Twin in front of one building at Riddes, put back each time its tile loads
        AddChild(new World.AfricaTwinEgg(_chunks));
        // the paddle steamer at the Nyon landing (#303), put back each time its tile loads
        AddChild(new World.SteamerBerth(_chunks));

        // gunfire: clients send their rounds here to be relayed; the server flies none of them
        Combat.CombatManager.Create(this, null, server: true);

        // building interiors: planned here on first entry, stored under user://interiors, and
        // handed to everyone who walks in afterwards
        _interiors = Interiors.InteriorManager.Create(this, source, origin);
        _interiors.Players = _players;

        // loot in those interiors: the server rolls it and remembers what was taken
        Loot.LootService.Create(this);

        // occasions run on the server's calendar and are replicated, so every player shares one
        _occasions = Occasions.OccasionManager.Create(this);

        // The server owns the place index too, so /city and /tpall resolve against the same
        // data the client's Tab search uses and a client cannot ask to be moved anywhere else.
        var places = LoadPlaces();

        _registry = new PlayerRegistry(PlayerRegistry.ParseAdminPassword());
        _chat = ChatManager.CreateServer(_registry, _players, origin, places);
        AddChild(_chat);

        // car races between players: World/Race, like World/Chat, so the RPCs find it
        var race = World.RaceManager.CreateServer(_chat, _players, source, origin);
        // racers see each other however far apart the field spreads (Net/InterestService)
        // and everyone aboard one vehicle sees everyone else aboard it, wherever it goes
        var passengers = _passengers;
        AddChild(race);
        _chat.Race = race;

        // Battle Royale (#177): World/BattleRoyale; everyone in a running match sees everyone else in it
        _brCrates = BattleRoyale.BrCrates.Create(this, origin, server: true);
        var br = _br = BattleRoyale.BrManager.CreateServer(_chat, _players, places?.Places ?? new(), manifest.Tiles,
            (SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N), source, _brCrates);
        br.Origin = origin;
        AddChild(br);
        _chat.BattleRoyale = br;
        if (_interest != null) _interest.Together = (a, b) => race.SameRace(a, b) || passengers.Together(a, b) || br.Together(a, b);

        // deposited cash, kept per player name on this server
        var bank = Items.Bank.Create(this, null, server: true);
        bank.NameOf = _chat.NameOfPeer;
        // money moves only at a bank's teller desk (#213)
        bank.InBank = peer => Loot.LootService.Instance?.InBank(peer) ?? Task.FromResult(false);

        // held-item events (a shot, a flash) are relayed through here; placed objects (planted
        // flags, stuck photos) are owned, checked and saved here
        Items.ItemEvents.Create(this, server: true);

        // the birds everybody shares (#143): simulated here around every player, sent to those near
        var birds = new Birds.BirdLife(_chunks, origin, null)
        {
            Headless = true,
            // fills the birds' reused list: no allocation per frame (GC pauses at 16 players)
            Observers = list =>
            {
                for (int i = 0; i < _players!.GetChildCount(); i++)
                    if (_players.GetChild(i) is Player.FootPlayer { Npc: false } p)
                        list.Add(new Birds.BirdLife.Observer(p.GlobalPosition, p.NetVel, p.Ride is Player.RideKind.Plane or Player.RideKind.Helicopter
                            or Player.RideKind.Paraglider or Player.RideKind.Parachute or Player.RideKind.Wingsuit, p.GetMultiplayerAuthority()));
            },
        };
        AddChild(birds);
        Birds.BirdNet.Create(this, birds, server: true);
        // stuck Polaroids' images: uploaded by their owner, kept here, served to the others
        Items.PhotoTransfer.Create(this, server: true);
        _placed = Items.PlacedObjects.Create(this, origin, server: true);
        _placed.NameOf = _chat.NameOfPeer;
        _chat.NameAssigned += bank.SendBalance;

        // a vehicle out of nothing is an admin's, or the one a race put you on (Core/Permissions)
        // (a wreck cannot be driven and burns out: no loophole, and race NPCs' wrecks park through
        // the ordinary client simulating them)
        _vehicles.MayPark = (peer, state) => state.Wrecked || _chat.IsAdminPeer(peer) || race.TakeIssued(peer);

        // The operator's own command line. This is how the first admin gets granted.
        AddChild(new ServerConsole(_chat));

        // Serves generated terrain files to clients that lack them. Reads raw bytes straight
        // off disk, so it costs the server no decoding work.
        _streamer = ChunkStreamer.CreateServer(chunkDir);
        _streamer.CdDirectory = Audio.Cd.CdLibrary.Directory;
        // no manifest.json to serve, but the clients still need the origin to adopt
        if (manifest.Tiles.Count == 0)
            _streamer.ManifestOverride = System.Text.Encoding.UTF8.GetBytes(new TerrainManifest
            {
                SuggestedOriginLv95 = new Lv95Point { E = origin.E, N = origin.N },
            }.ToJson());
        if (ParseStreamBandwidth() is { } megabytesPerSecond)
        {
            _streamer.BytesPerSecondPerPeer = (int)(megabytesPerSecond * 1024 * 1024);
            // fr-CH machine: an uninvariant format renders 0.75 as "0,75"
            GD.Print("[server] terrain streaming capped at "
                + megabytesPerSecond.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                + " MB/s per client");
        }
        AddChild(_streamer);
        _chat.Streamer = _streamer;

        var net = new NetworkManager { Name = "Net" };
        AddChild(net);
        int port = ParsePort();
        if (!net.StartServer(port, NetworkManager.ParseBindArg()))
        {
            GetTree().Quit(1);
            return;
        }

        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;

        // status queries on port + 1: LAN lists find this server, saved lists show it is up
        // and how full it is (Net/QueryResponder, docs/notes/net/server-query.md)
        if (QueryResponder.ParsePort(port) is { } queryPort)
        {
            string name = QueryResponder.ParseServerName();
            string version = (string)ProjectSettings.GetSetting("application/config/version", "");
            string world = manifest.Tiles.Count > 0 ? "real" : "generated";
            var registry = _registry;
            AddChild(new QueryResponder(queryPort, () => new ServerStatus(
                name, port, registry?.Players.Count ?? 0, NetworkManager.MaxClients, version, world), QueryResponder.ParseBind()));
        }
        _parentPid = HostedServer.ParseParentPid();
    }

    /// <summary>The client that started this server from its menu, if any: the server goes when it does.</summary>
    private int? _parentPid;
    private double _sinceParentCheck;

    /// <summary>
    /// Reads "--stream-bandwidth &lt;MB/s&gt;", the per-client terrain streaming cap.
    /// <para>
    /// The 3 MB/s default is sized for a LAN. Over the internet it is 24 Mbit/s <i>per
    /// client</i>, which will saturate most home uplinks with two players on it, so a server
    /// exposed through Tailscale or a forwarded port usually wants this set.
    /// </para>
    /// </summary>
    private static double? ParseStreamBandwidth()
    {
        var args = OS.GetCmdlineUserArgs();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--stream-bandwidth"
                && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, inv, out double v)
                && v > 0)
                return v;
        return null;
    }

    /// <summary>"--generated off": no generated fill (a server does not read the client's settings file).</summary>
    private static bool GeneratedOff(string[] args)
    {
        int i = Array.IndexOf(args, "--generated");
        return i >= 0 && i + 1 < args.Length && args[i + 1] is "off" or "0" or "false";
    }

    private static int ParsePort()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--port" && int.TryParse(args[i + 1], out int p))
                return p;
        return NetworkManager.DefaultPort;
    }

    private double _sinceStatus;
    // one line per player every 5 s: ~6 ms per line on Windows, ~100 ms frames at 16 players (#221)
    private static readonly bool PlayerStatus = OS.GetCmdlineUserArgs().Contains("--player-status");

    public override void _Process(double delta)
    {
        if (_parentPid is { } parent && (_sinceParentCheck += delta) >= 1)
        {
            _sinceParentCheck = 0;
            if (!HostedServer.Alive(parent))
            {
                GD.Print($"[server] the client that hosted this server (pid {parent}) is gone; stopping");
                GetTree().Quit(0);
                return;
            }
        }
        if (_players == null || !PlayerStatus) return;
        _sinceStatus += delta;
        if (_sinceStatus < 5) return;
        _sinceStatus = 0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var child in _players.GetChildren())
            if (child is Node3D p)
            {
                // the ground this server holds under them: what any server-side check would use
                string ground = _chunks != null && _chunks.TryGetHeight(p.GlobalPosition, out float h)
                    ? $"ground {h:F1} m" + (_chunks.IsGenerated(_origin!.TileAt(p.GlobalPosition)) ? " (generated)" : "")
                    : "ground not loaded";
                GD.Print($"[server] player {p.Name} at {p.GlobalPosition}, {ground}");
            }
        ServerStats.Ran("player status", t0);
    }

    private void OnPeerConnected(long id)
    {
        GD.Print($"[server] peer {id} connected");
        _registry?.Add(id);

        var node = _spawner!.Spawn(id);
        if (node is Node3D player)
            _chunks!.AddAnchor(player);
        _interiors?.SendTableTo(id);
        _passengers?.SendTo(id);
        _occasions?.SendTo(id);
        _placed?.SendTo(id);
        _br?.SendTo(id);
        _brCrates?.SendTo(id);
        _chat?.SendWorldTimeTo(id);
        _chat?.SendSeaStateTo(id);
    }

    private void OnPeerDisconnected(long id)
    {
        GD.Print($"[server] peer {id} disconnected");
        _chat?.ReportDisconnect(id);
        // before the vehicles: a host's passengers go on in its vehicle, which it no longer simulates
        _passengers?.PeerLeft(id);
        _br?.PeerLeft(id);
        _vehicles?.ForgetOwner(id);
        _radios?.ForgetOwner(id);
        _dropped?.ForgetOwner(id);
        _interiors?.ForgetPeer(id);
        _streamer?.ForgetPeer(id);
        _interest?.ForgetPeer(id);
        _npcs?.PeerLeft(id);   // its race NPCs go to someone near them, or retire

        if (_players!.GetNodeOrNull<Node3D>(id.ToString()) is { } player)
        {
            _chunks!.RemoveAnchor(player);
            player.QueueFree();
        }
    }

    /// <summary>
    /// Reads places.json from the chunk directory. Absent is not fatal — chat still works,
    /// only /city and /tpall report that the index was never built.
    /// </summary>
    private static PlaceIndex? LoadPlaces()
    {
        string path = System.IO.Path.Combine(TerrainPaths.FindChunkDir(), PlaceIndex.FileName);
        if (!System.IO.File.Exists(path))
        {
            GD.PushWarning($"[server] {PlaceIndex.FileName} not found; /city and /tpall disabled");
            return null;
        }

        var index = PlaceIndex.FromJson(System.IO.File.ReadAllText(path));
        GD.Print($"[server] {index.Places.Count} places available to /city");
        return index;
    }
}
