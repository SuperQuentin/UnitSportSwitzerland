using Godot;
using UnitSport.Core;

namespace UnitSport.Net;

/// <summary>
/// Players who left, asleep where they were (#644), at <c>World/Sleepers</c> on the server and every
/// client (RPCs route by node path).
///
/// <para>
/// <b>Identity.</b> On joining, the server sends the peer a random nonce (<c>Challenge</c>); the
/// client signs it with this install's <see cref="PlayerKey"/> and answers with its public key
/// (<c>Prove</c>). A good signature makes the key's fingerprint that peer's identity for the session.
/// A peer that never proves (an old client, a bot) leaves no sleeper; a second peer with a key already
/// in use (two clients sharing one <c>user://</c>) gets none either.
/// </para>
///
/// <para>
/// <b>Sleeping and waking.</b> When a proven peer leaves, <see cref="PeerLeft"/> lays its body down
/// where it was (on the ground under it if it was riding or indoors), in its outfit and figure, saved
/// in <c>user://sleepers/server.json</c>. When that key proves again, the sleeper goes and the
/// player is sent there (<c>WakeAt</c>: the same teleport as <c>/tp</c>, settling on the terrain).
/// Others only see a sleeper; nothing can be done to it.
/// </para>
/// </summary>
public partial class Sleepers : Node
{
    public const string NodeName = "Sleepers";
    private const string StorePath = "user://sleepers/server.json";
    private const string KeyPath = "user://identity.key";

    /// <summary>How flat a sleeper lies: the knocked-out body's angle (<c>FootPlayer.PublishFootPose</c>).</summary>
    private const float LieAngle = -1.45f;

    private bool _server;
    private WorldOrigin _origin = null!;

    // server
    private SleeperBook _book = new();
    private readonly Dictionary<long, byte[]> _nonces = new();
    private readonly Dictionary<long, string> _keyOf = new();

    /// <summary>Server: the ground's altitude under an LV95 point, null while it is not loaded.</summary>
    public Func<double, double, double?>? GroundAt { get; set; }

    // client
    private PlayerKey? _key;
    private readonly Dictionary<long, Node3D> _visuals = new();

    /// <summary>Client: moves the local player on waking; set by <see cref="ClientWorld"/>.</summary>
    public Teleporter? Teleporter { get; set; }

    private readonly Dictionary<long, Sleeper> _shown = new();

    public static Sleepers Create(Node world, WorldOrigin origin, bool server)
    {
        var s = new Sleepers { Name = NodeName, _server = server, _origin = origin };
        world.AddChild(s);
        if (!server) Instance = s;
        return s;
    }

    public override void _Ready()
    {
        if (!_server) return;
        string path = ProjectSettings.GlobalizePath(StorePath);
        try
        {
            if (File.Exists(path)) _book = SleeperBook.FromJson(File.ReadAllText(path));
            GD.Print($"[sleepers] {_book.All.Count} asleep, from {path}");
        }
        catch (Exception ex) { GD.PushWarning($"[sleepers] {path}: {ex.Message}"); }
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        _key?.Dispose();
    }

    // ---- server ---------------------------------------------------------------------------------

    /// <summary>Server: a joining peer gets everyone asleep, and the challenge that proves who it is.</summary>
    public void SendTo(long peer)
    {
        var list = _book.All.OrderBy(s => s.Id).ToList();
        RpcId(peer, MethodName.Snapshot,
            list.Select(s => s.Id).ToArray(),
            list.Select(s => s.Name).ToArray(),
            list.SelectMany(s => new[] { s.E, s.N, s.Alt }).ToArray(),
            list.Select(s => s.Yaw).ToArray(),
            list.Select(s => s.Rider).ToArray(),
            list.Select(s => s.Outfit).ToArray(),
            list.Select(s => s.Appearance).ToArray(),
            list.Select(s => s.Headwear).ToArray());
        var nonce = PlayerKey.NewNonce();
        _nonces[peer] = nonce;
        RpcId(peer, MethodName.Challenge, nonce);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Prove(byte[] publicKey, byte[] signature)
    {
        if (!_server) return;
        long peer = Multiplayer.GetRemoteSenderId();
        // one answer per challenge: a replay or a second try finds no nonce
        if (!_nonces.Remove(peer, out var nonce)) return;
        if (!PlayerKey.Verify(publicKey, nonce, signature))
        {
            GD.Print($"[sleepers] peer {peer}: bad identity proof");
            return;
        }
        string key = PlayerKey.Fingerprint(publicKey);
        if (_keyOf.ContainsValue(key))
        {
            GD.Print($"[sleepers] peer {peer}: identity {key[..8]} already connected, no sleeper for it");
            return;
        }
        _keyOf[peer] = key;
        GD.Print($"[sleepers] peer {peer} is {key[..8]}");

        if (_book.Wake(key) is not { } s) return;
        Unshow(s.Id);
        Save();
        GD.Print($"[sleepers] {s.Name} wakes at {s.E:F1}/{s.N:F1}/{s.Alt:F1}");
        RpcId(peer, MethodName.WakeAt, s.E, s.N, s.Alt, s.Yaw);
    }

    /// <summary>
    /// Server: a peer left; <paramref name="body"/> is its player (before it is freed), <paramref name="name"/>
    /// its display name (before the registry forgets it). A proven peer lies down where it was.
    /// </summary>
    public void PeerLeft(long peer, Player.FootPlayer? body, string name)
    {
        _nonces.Remove(peer);
        if (!_keyOf.Remove(peer, out var key) || body is null || body.Npc) return;

        var at = body.NetGlobal;
        double alt = at.Alt;
        // riding (a vehicle goes on without it) or inside (interiors are far below): on the ground there
        if (body.Ride != Player.RideKind.OnFoot || body.Indoors)
        {
            if (GroundAt?.Invoke(at.E, at.N) is not { } ground)
            {
                GD.Print($"[sleepers] {name}: no ground known under {at.E:F0}/{at.N:F0}, no sleeper");
                return;
            }
            alt = ground;
        }

        var s = _book.Lay(new Sleeper(0, key, name, at.E, at.N, alt, body.NetYaw, (int)peer,
            body.OutfitBits, body.AppearanceBits, body.HeadwearId), out var replaced);
        if (replaced != null) Unshow(replaced.Id);
        Show(s);
        Save();
        GD.Print($"[sleepers] {name} falls asleep at {s.E:F1}/{s.N:F1}/{s.Alt:F1}");
    }

    private void Show(Sleeper s)
    {
        foreach (int p in Multiplayer.GetPeers())
            RpcId(p, MethodName.Add, s.Id, s.Name, s.E, s.N, s.Alt, s.Yaw, s.Rider, s.Outfit, s.Appearance, s.Headwear);
    }

    private void Unshow(long id)
    {
        foreach (int p in Multiplayer.GetPeers()) RpcId(p, MethodName.Remove, id);
    }

    private void Save()
    {
        try
        {
            JsonStore.SaveAsync(StorePath, _book.Snapshot(), JsonStore.Indented, e => GD.PushError($"[sleepers] saving: {e.Message}"));
        }
        catch (Exception ex) { GD.PushError($"[sleepers] saving: {ex.Message}"); }
    }

    // ---- client: from the server ----------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Challenge(byte[] nonce)
    {
        try
        {
            _key ??= PlayerKey.LoadOrCreate(ProjectSettings.GlobalizePath(KeyPath));
            RpcId(1, MethodName.Prove, _key.PublicKey, _key.Sign(nonce));
        }
        catch (Exception ex) { GD.PushWarning($"[sleepers] identity key: {ex.Message}"); }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Snapshot(long[] ids, string[] names, double[] pos, float[] yaws, int[] riders, long[] outfits,
        int[] appearances, int[] hats)
    {
        foreach (long id in _shown.Keys.ToList()) Drop(id);
        for (int i = 0; i < ids.Length; i++)
            Put(new Sleeper(ids[i], "", names[i], pos[3 * i], pos[3 * i + 1], pos[3 * i + 2], yaws[i], riders[i],
                outfits[i], appearances[i], hats[i]));
        GD.Print($"[sleepers] snapshot: {ids.Length} asleep");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Add(long id, string name, double e, double n, double alt, float yaw, int rider, long outfit,
        int appearance, int hat) => Put(new Sleeper(id, "", name, e, n, alt, yaw, rider, outfit, appearance, hat));

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Remove(long id) => Drop(id);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void WakeAt(double e, double n, double alt, float yaw)
    {
        GD.Print($"[sleepers] waking at {e:F1}/{n:F1}/{alt:F1}");
        LastWake = (e, n);
        Teleporter?.TeleportTo(e, n, "where you fell asleep");
    }

    /// <summary>The client's node (checks).</summary>
    public static Sleepers? Instance { get; private set; }

    /// <summary>Client: how many sleepers this client shows (checks).</summary>
    public int Count => _shown.Count;

    /// <summary>Client: where a shown sleeper lies, in world space (checks).</summary>
    public Vector3? AnyShown => _visuals.Values.FirstOrDefault(IsInstanceValid)?.GlobalPosition;

    /// <summary>Client: where the server last woke this player, LV95 E/N (checks).</summary>
    public (double E, double N)? LastWake { get; private set; }

    private void Put(Sleeper s)
    {
        Drop(s.Id);
        _shown[s.Id] = s;
        var node = Draw(s);
        node.Position = _origin.ToWorld(s.E, s.N, s.Alt);
        node.Rotation = new Vector3(0, s.Yaw, 0);
        AddChild(node);
        _visuals[s.Id] = node;
    }

    private void Drop(long id)
    {
        _shown.Remove(id);
        if (_visuals.Remove(id, out var node) && IsInstanceValid(node)) node.QueueFree();
    }

    /// <summary>A body lying on its back in the sleeper's clothes, its name over it.</summary>
    private static Node3D Draw(Sleeper s)
    {
        var root = new Node3D { Name = $"S{s.Id}" };
        var palette = Avatar.HumanPalette.ForRider(s.Rider)
            .With(Avatar.Appearance.Unpack(s.Appearance) ?? Avatar.Appearance.ForSeed(s.Rider))
            with { Outfit = new Avatar.Outfit(s.Outfit) };
        root.AddChild(new MeshInstance3D
        {
            Name = "Body",
            Mesh = Avatar.HumanMeshBuilder.Build(palette, Avatar.HumanPose.Standing, hat: (Avatar.Headwear)s.Headwear),
            MaterialOverride = Avatar.HumanMeshBuilder.FigureMaterial(),
            // as the knocked-out walker lies (FootPlayer.PublishFootPose), the feet where they stood
            Transform = new Transform3D(new Basis(Vector3.Right, LieAngle), new Vector3(0, -LieAngle * 0.12f, 0)),
        });
        root.AddChild(new Label3D
        {
            Name = "Tag",
            Text = $"{s.Name} (asleep)\nz z z",
            FontSize = 40,
            PixelSize = 0.006f,
            OutlineSize = 8,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Shaded = false,
            VisibilityRangeEnd = 60f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // over the middle of the body: lying, the head points forward (-Z) from the feet
            Position = new Vector3(0, 0.8f, -0.85f),
        });
        return root;
    }
}
