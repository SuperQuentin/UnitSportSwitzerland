using Godot;
using UnitSport.Net;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// Dedicated server: no meshes, no rendering — terrain height data is streamed around
/// every connected player for (future) validation, and the MultiplayerSpawner owns the
/// lifecycle of player nodes. Transforms are client-authoritative and relayed by ENet.
/// </summary>
public partial class ServerWorld : Node3D
{
    private ChunkManager? _chunks;
    private Node3D? _players;
    private MultiplayerSpawner? _spawner;
    private PlayerRegistry? _registry;
    private ChatManager? _chat;
    private ChunkStreamer? _streamer;

    public override async void _Ready()
    {
        string chunkDir = TerrainPaths.FindChunkDir();
        var source = new LocalChunkSource(chunkDir);
        var manifest = await source.LoadManifestAsync();

        // Unlike a client, a server cannot shrug this off: it is the authority on where the
        // world is and the only source of terrain for clients that lack it. Starting anyway
        // would hand every client an origin of 0/0 and a world with nothing in it.
        if (manifest.Tiles.Count == 0)
        {
            GD.PushError(
                $"[server] no terrain data in {chunkDir}. A server has nothing to serve and no "
                + "world origin to hand out. Generate the chunks first (see the README), or "
                + "point at an existing set with --chunks <dir>.");
            GetTree().Quit(1);
            return;
        }

        var origin = new WorldOrigin(manifest.SuggestedOriginLv95.E, manifest.SuggestedOriginLv95.N);
        GD.Print($"[server] {manifest.Tiles.Count} tiles, origin LV95 {origin.E}/{origin.N}");

        _chunks = new ChunkManager { Name = "Terrain", BuildMeshes = false, BuildCollision = false };
        _chunks.Initialize(source, origin, manifest, null);
        AddChild(_chunks);

        _players = new Node3D { Name = "Players" };
        AddChild(_players);
        _spawner = PlayerReplication.CreateSpawner();
        AddChild(_spawner);

        // The server owns the place index too, so /city and /tpall resolve against the same
        // data the client's Tab search uses and a client cannot ask to be moved anywhere else.
        var places = LoadPlaces();

        _registry = new PlayerRegistry(PlayerRegistry.ParseAdminPassword());
        _chat = ChatManager.CreateServer(_registry, _players, origin, places);
        AddChild(_chat);

        // The operator's own command line. This is how the first admin gets granted.
        AddChild(new ServerConsole(_chat));

        // Serves generated terrain files to clients that lack them. Reads raw bytes straight
        // off disk, so it costs the server no decoding work.
        _streamer = ChunkStreamer.CreateServer(chunkDir);
        if (ParseStreamBandwidth() is { } megabytesPerSecond)
        {
            _streamer.BytesPerSecondPerPeer = (int)(megabytesPerSecond * 1024 * 1024);
            // fr-CH machine: an uninvariant format renders 0.75 as "0,75"
            GD.Print("[server] terrain streaming capped at "
                + megabytesPerSecond.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                + " MB/s per client");
        }
        AddChild(_streamer);

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
    }

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

    private static int ParsePort()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--port" && int.TryParse(args[i + 1], out int p))
                return p;
        return NetworkManager.DefaultPort;
    }

    private double _sinceStatus;

    public override void _Process(double delta)
    {
        if (_players == null) return;
        _sinceStatus += delta;
        if (_sinceStatus < 5) return;
        _sinceStatus = 0;
        foreach (var child in _players.GetChildren())
            if (child is Node3D p)
                GD.Print($"[server] player {p.Name} at {p.GlobalPosition}");
    }

    private void OnPeerConnected(long id)
    {
        GD.Print($"[server] peer {id} connected");
        _registry?.Add(id);

        var node = _spawner!.Spawn(id);
        if (node is Node3D player)
            _chunks!.AddAnchor(player);
    }

    private void OnPeerDisconnected(long id)
    {
        GD.Print($"[server] peer {id} disconnected");
        _chat?.ReportDisconnect(id);
        _streamer?.ForgetPeer(id);

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
