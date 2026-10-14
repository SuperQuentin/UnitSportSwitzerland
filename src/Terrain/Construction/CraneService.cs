using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Construction;

/// <summary>
/// Cranes players work (#618), at <c>World/Cranes</c>: server-owned, made on both sides like
/// <c>PalletService</c>; offline the client plays the server's part through the same methods.
///
/// <para>
/// A crane nobody has touched is not here at all: it moves by the clocks (<see cref="CraneMotion"/>)
/// with nothing sent. One that is held is a <see cref="Hold"/>: <b>one operator at a time</b>,
/// granted by the server to a body at the mast's foot (it places the crane from the tile's own
/// files, as every peer builds it). The operator steps the pose itself (<see cref="CraneControl"/>)
/// and sends it ten times a second; the server keeps it within the crane's reach and passes it on,
/// and every other peer eases towards it. Nothing goes through the operator's own synchronizer:
/// vision interest can drop a small figure in a cab 40 m up while its jib is still in sight.
/// </para>
///
/// <para>
/// <b>Let go</b>, the server stamps the pose and the simulated time and every peer works out the
/// same <see cref="CraneMotion.Resume"/> from them, so it holds, then eases back into its schedule
/// with no snap, and the hold is forgotten. A crane let go with a pallet on its hook holds it, off
/// its schedule, until somebody sets the pallet down. The pallet on the hook is the hold's
/// (<see cref="Hold.Carrying"/>, a <see cref="Pallets.Carried"/> code), set by
/// <c>PalletService</c> when it grants an operator a take: a crane is a carrier like a machine's
/// forks, its reach measured from the hook. Note: <c>docs/notes/terrain/construction-sites.md</c>.
/// </para>
/// </summary>
public partial class CraneService : Node
{
    public const string NodeName = "Cranes";

    /// <summary>How far from the mast's centre, flat, a body may take a crane over, m: the base is 4.6 m across.</summary>
    public const float FootReach = 4.5f;
    /// <summary>The operator's pose goes out this often, s.</summary>
    public const double SendInterval = 0.1;
    /// <summary>How quickly a peer's drawn crane catches up with the pose it was sent, 1/s.</summary>
    private const float Catch = 8f;

    public static CraneService? Instance { get; private set; }

    /// <summary>A line for the player: why a crane was refused (the client's toast).</summary>
    public static event Action<string>? Said;

    public static void Say(string message) => Said?.Invoke(message);

    /// <summary>A crane somebody works, or has let go and that has not gone back to its schedule yet.</summary>
    public sealed class Hold
    {
        public required string Key;
        /// <summary>Who works it, or 0 once let go.</summary>
        public long Operator;
        /// <summary>The operator's latest pose, or where it was let go.</summary>
        public CranePose Pose;
        /// <summary>Whether <see cref="Pose"/> has been heard yet: a crane just taken over has none.</summary>
        public bool HasPose;
        /// <summary>When it was let go, simulated seconds; NaN while worked.</summary>
        public double LeftAt = double.NaN;
        /// <summary>The pallet on its hook (<see cref="Pallets.Carried"/>), or 0.</summary>
        public int Carrying;
        /// <summary>A hooked pallet's underside while its slings are still slack (tile-local y), or NaN once lifted.</summary>
        public float Rest = float.NaN;
        /// <summary>Client: what is drawn, easing towards <see cref="Pose"/>.</summary>
        public CranePose Shown;
        public bool HasShown;
        /// <summary>When <see cref="Shown"/> was last drawn: older than a moment, it is stale.</summary>
        public double ShownAt;
        /// <summary>Server: where the crane stands, from the tile's files, and its tile.</summary>
        public SiteShellBuilder.CraneStand? Stand;
        public TileId Tile;
    }

    private bool _server;
    private readonly Dictionary<string, Hold> _holds = new();
    private readonly List<string> _forget = new();
    private double _sentAt, _tidyAt;
    private Action<string?>? _operateDone;

    /// <summary>The tiles' files, for where a crane stands: the world's chunk source.</summary>
    public Func<IChunkSource?>? Source { get; set; }

    /// <summary>The held cranes by key.</summary>
    public IReadOnlyDictionary<string, Hold> Holds => _holds;

    /// <summary>The crane this client works, granted, or null.</summary>
    public string? Mine { get; private set; }

    public static CraneService Create(Node world, bool server)
    {
        var s = new CraneService { Name = NodeName, _server = server };
        world.AddChild(s);
        Instance = s;
        return s;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => NetLink.Online(this);
    private long MyPeer => NetLink.Ready(this) ? Multiplayer.GetUniqueId() : 1;

    // ---- client: drawing ------------------------------------------------------------------------

    /// <summary>
    /// Where a crane is drawn this frame if somebody holds it, or false: it moves by the clocks.
    /// The local operator's own pose as it steps it, another's eased towards the last one sent,
    /// one let go by <see cref="CraneMotion.Resume"/> (or held still with a pallet on its hook).
    /// </summary>
    public bool PoseOf(CraneJob job, double sim, double hour, long day, float dt, out CranePose pose, out Hold? hold)
    {
        pose = default;
        if (!_holds.TryGetValue(job.Key, out hold)) return false;
        CranePose target;
        if (hold.Operator == MyPeer && hold.HasPose)
        {
            // our own: drawn exactly as stepped
            hold.Shown = hold.Pose;
            hold.HasShown = true;
            hold.ShownAt = GameClock.Now;
            pose = hold.Pose;
            return true;
        }
        double now = GameClock.Now;
        bool fresh = !hold.HasShown || now - hold.ShownAt > 0.5;
        if (hold.Operator != 0 && !hold.HasPose)
            // taken over, and nothing heard yet: where the clocks had it
            target = fresh ? CraneMotion.Pose(job, sim, hour, day) with { Loaded = false } : hold.Shown;
        else if (hold.Operator != 0 || hold.Carrying != 0) target = hold.Pose with { Loaded = false };
        else if (!CraneMotion.Resuming(job.Key, hold.LeftAt, sim))
        {
            // back on its schedule: forget it, nothing changes on screen
            _holds.Remove(job.Key);
            hold = null;
            return false;
        }
        else target = CraneMotion.Resume(job, hold.Pose, hold.LeftAt, sim, hour, day);
        if (fresh)
        {
            // not drawn a moment ago (taken over just now, or only now in sight): from where the
            // clocks had it if it is being worked, else straight where it is
            hold.Shown = hold.Operator != 0 ? CraneMotion.Pose(job, sim, hour, day) with { Loaded = false } : target;
            hold.HasShown = true;
        }
        hold.ShownAt = now;
        float k = 1f - MathF.Exp(-Catch * dt);
        hold.Shown = new CranePose(CraneMotion.Turn(hold.Shown.Yaw, target.Yaw, k), Mathf.Lerp(hold.Shown.Trolley, target.Trolley, k),
            Mathf.Lerp(hold.Shown.HookY, target.HookY, k), target.Loaded);
        pose = hold.Shown;
        return true;
    }

    // ---- client: working one --------------------------------------------------------------------

    /// <summary>
    /// Asks to work a crane. <paramref name="done"/> gets null when granted, else why not. Online the
    /// asker's body must be at the mast's foot.
    /// </summary>
    public void RequestOperate(string key, Action<string?> done)
    {
        _operateDone = done;
        if (Online) RpcId(1, MethodName.AskOperate, key);
        else ServeOperate(1, key);
    }

    /// <summary>The local operator's pose this frame: drawn at once, and sent at <see cref="SendInterval"/>.</summary>
    public void Steer(CranePose pose)
    {
        if (Mine is not { } key || !_holds.TryGetValue(key, out var hold)) return;
        hold.Pose = pose;
        hold.HasPose = true;
        double now = GameClock.Now;
        if (now - _sentAt < SendInterval) return;
        _sentAt = now;
        if (Online) RpcId(1, MethodName.Send, key, pose.Yaw, pose.Trolley, pose.HookY);
    }

    /// <summary>Lets go of the crane this client works: it holds where it is, then goes back to its schedule.</summary>
    public void Release()
    {
        if (Mine is not { } key) return;
        Mine = null;
        if (Online)
        {
            // the last pose first, so the server stamps where it really stopped
            if (_holds.TryGetValue(key, out var hold)) RpcId(1, MethodName.Send, key, hold.Pose.Yaw, hold.Pose.Trolley, hold.Pose.HookY);
            RpcId(1, MethodName.AskRelease);
        }
        else ServeRelease(1);
    }

    /// <summary>The hold of the crane this client works, or null.</summary>
    public Hold? MyHold => Mine is { } key && _holds.TryGetValue(key, out var hold) ? hold : null;

    // ---- client: from the server ----------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Operated(string key, long by)
    {
        if (!_holds.TryGetValue(key, out var hold)) _holds[key] = hold = new Hold { Key = key };
        hold.Operator = by;
        hold.LeftAt = double.NaN;
        hold.HasPose = false;
        GD.Print($"[cranes] {key} worked by peer {by}");
        if (by != MyPeer) return;
        Mine = key;
        var done = _operateDone;
        _operateDone = null;
        done?.Invoke(null);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void OperateRefused(string key, string why)
    {
        GD.Print($"[cranes] working {key} refused: {why}");
        var done = _operateDone;
        _operateDone = null;
        done?.Invoke(why);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void Posed(string key, float yaw, float trolley, float hookY)
    {
        if (_holds.TryGetValue(key, out var hold) && hold.Operator != 0 && hold.Operator != MyPeer)
        {
            hold.Pose = new CranePose(yaw, trolley, hookY, false);
            hold.HasPose = true;
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Released(string key, float yaw, float trolley, float hookY, double leftAt)
    {
        if (!_holds.TryGetValue(key, out var hold)) _holds[key] = hold = new Hold { Key = key };
        hold.Operator = 0;
        hold.Pose = new CranePose(yaw, trolley, hookY, false);
        hold.HasPose = true;
        hold.LeftAt = leftAt;
        if (Mine == key) Mine = null;
        GD.Print(FormattableString.Invariant($"[cranes] {key} let go at yaw {yaw:F2} trolley {trolley:F1} hook {hookY:F1}, sim {leftAt:F1}"));
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Carry(string key, int carrying, float rest)
    {
        if (!_holds.TryGetValue(key, out var hold)) return;
        hold.Carrying = carrying;
        hold.Rest = rest;
        GD.Print($"[cranes] {key} hook carrying {carrying}");
    }

    /// <summary>Every held crane, on joining: replaces whatever this client knew.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Snapshot(string[] keys, long[] operators, float[] poses, double[] leftAt, int[] carrying, float[] rest)
    {
        _holds.Clear();
        for (int i = 0; i < keys.Length; i++)
            _holds[keys[i]] = new Hold
            {
                Key = keys[i], Operator = operators[i], LeftAt = leftAt[i], Carrying = carrying[i], Rest = rest[i],
                // NaN: taken over and nothing heard yet
                HasPose = float.IsFinite(poses[3 * i]),
                Pose = new CranePose(poses[3 * i], poses[3 * i + 1], poses[3 * i + 2], false),
            };
    }

    // ---- server ---------------------------------------------------------------------------------

    /// <summary>Server: hands a joining peer every held crane.</summary>
    public void SendTo(long peer)
    {
        var holds = _holds.Values.ToList();
        RpcId(peer, MethodName.Snapshot, holds.Select(h => h.Key).ToArray(), holds.Select(h => h.Operator).ToArray(),
            holds.SelectMany(h => new[] { h.HasPose ? h.Pose.Yaw : float.NaN, h.Pose.Trolley, h.Pose.HookY }).ToArray(),
            holds.Select(h => h.LeftAt).ToArray(), holds.Select(h => h.Carrying).ToArray(), holds.Select(h => h.Rest).ToArray());
    }

    /// <summary>Server: a peer left; its crane is let go where it was.</summary>
    public void PeerLeft(long peer) => ServeRelease(peer);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskOperate(string key) => ServeOperate(Multiplayer.GetRemoteSenderId(), key);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskRelease() => ServeRelease(Multiplayer.GetRemoteSenderId());

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void Send(string key, float yaw, float trolley, float hookY)
    {
        long peer = Multiplayer.GetRemoteSenderId();
        if (!_holds.TryGetValue(key, out var hold) || hold.Operator != peer || hold.Stand is not { } stand) return;
        if (!float.IsFinite(yaw) || !float.IsFinite(trolley) || !float.IsFinite(hookY)) return;
        // within the crane's reach, whatever was sent
        trolley = Math.Clamp(trolley, CraneMotion.MinTrolley, stand.Spot.JibLength - 1f);
        hookY = Math.Clamp(hookY, stand.Ground - 30f, stand.JibY - 1.2f);
        hold.Pose = new CranePose(CraneMotion.Wrap(yaw), trolley, hookY, false);
        hold.HasPose = true;
        foreach (int other in Multiplayer.GetPeers())
            if (other != peer) RpcId(other, MethodName.Posed, key, hold.Pose.Yaw, trolley, hookY);
    }

    private async void ServeOperate(long peer, string key)
    {
        if (OperatorOf(peer) is { } already && already != key) { RefuseOperate(peer, key, "You are working another crane."); return; }
        _holds.TryGetValue(key, out var hold);
        if (hold is { Operator: not 0 } && hold.Operator != peer) { RefuseOperate(peer, key, "Somebody is working this crane."); return; }

        var stand = hold?.Stand;
        var tile = hold?.Tile ?? default;
        if (stand == null && Source?.Invoke() is { } source)
            try
            {
                (stand, tile) = await Task.Run(() =>
                {
                    var s = SitePlans.CraneAt(source, key, out var t);
                    return (s, t);
                });
            }
            catch (Exception e) { GD.PushError($"[cranes] {key}: {e.Message}"); }
        // offline there is nobody to doubt: a hand-made site in a check has no tile files
        if (stand == null && Online) { RefuseOperate(peer, key, "No such crane."); return; }

        // taken by somebody else while the tile loaded
        _holds.TryGetValue(key, out hold);
        if (hold is { Operator: not 0 } && hold.Operator != peer) { RefuseOperate(peer, key, "Somebody is working this crane."); return; }
        if (Online && stand is { } at && (GetNodeOrNull<FootPlayer>("../Players/" + peer) is not { } body
                       || Flat(body.Global, Lv95(tile, new Vector3(at.Spot.Base.X, at.Ground, at.Spot.Base.Y))) > FootReach))
        {
            RefuseOperate(peer, key, "Too far from the crane.");
            return;
        }
        if (hold == null) _holds[key] = hold = new Hold { Key = key };
        hold.Stand = stand;
        hold.Tile = tile;
        if (Online)
            foreach (int other in Multiplayer.GetPeers())
                RpcId(other, MethodName.Operated, key, peer);
        Operated(key, peer);
    }

    private void RefuseOperate(long peer, string key, string why)
    {
        if (Online && peer != 1) RpcId(peer, MethodName.OperateRefused, key, why);
        else OperateRefused(key, why);
    }

    private void ServeRelease(long peer)
    {
        if (OperatorOf(peer) is not { } key) return;
        var hold = _holds[key];
        double leftAt = SimClock.SimAt(ClockSync.ServerNow);
        if (Online)
            foreach (int other in Multiplayer.GetPeers())
                RpcId(other, MethodName.Released, key, hold.Pose.Yaw, hold.Pose.Trolley, hold.Pose.HookY, leftAt);
        Released(key, hold.Pose.Yaw, hold.Pose.Trolley, hold.Pose.HookY, leftAt);
    }

    /// <summary>The crane a peer works, or null.</summary>
    public string? OperatorOf(long peer)
    {
        foreach (var (key, hold) in _holds)
            if (hold.Operator == peer) return key;
        return null;
    }

    /// <summary>
    /// Server, for <c>PalletService</c>: the hook of the crane <paramref name="peer"/> works, as a
    /// carrier — what is on it, and where its origin is in LV95 — or null when it works none.
    /// Offline (no stand known) the hook is where the client says it is: nobody to doubt.
    /// </summary>
    public (int Carrying, GlobalPos? Hook)? HookOf(long peer)
    {
        if (OperatorOf(peer) is not { } key) return null;
        var hold = _holds[key];
        if (hold.Stand is not { } stand) return (hold.Carrying, null);
        var job = new CraneJob(key, stand.Spot.Base, stand.JibY, stand.Spot.JibLength, 0, default, Array.Empty<Vector3>());
        return (hold.Carrying, Lv95(hold.Tile, CraneControl.HookAt(job, hold.Pose)));
    }

    /// <summary>
    /// Server, from <c>PalletService</c>: the hook of the crane <paramref name="peer"/> works took a
    /// pallet (<paramref name="carrying"/>, its underside at <paramref name="rest"/>), or set it
    /// down (0). Everyone is told.
    /// </summary>
    public void Hooked(long peer, int carrying, float rest)
    {
        if (OperatorOf(peer) is not { } key) return;
        if (Online)
            foreach (int other in Multiplayer.GetPeers())
                RpcId(other, MethodName.Carry, key, carrying, rest);
        Carry(key, carrying, rest);
    }

    public override void _Process(double delta)
    {
        if (!_server) return;
        // a crane let go and back on its schedule is forgotten, as every client forgets it
        double now = GameClock.Now;
        if (now < _tidyAt) return;
        _tidyAt = now + 5;
        double sim = SimClock.SimAt(ClockSync.ServerNow);
        foreach (var (key, hold) in _holds)
            if (hold.Operator == 0 && hold.Carrying == 0 && !CraneMotion.Resuming(key, hold.LeftAt, sim - 1)) _forget.Add(key);
        foreach (var key in _forget) _holds.Remove(key);
        _forget.Clear();
    }

    private static GlobalPos Lv95(TileId tile, Vector3 local) => new(tile.MinE + local.X, tile.MaxN - local.Z, local.Y);

    private static double Flat(GlobalPos a, GlobalPos b) => Math.Sqrt((a.E - b.E) * (a.E - b.E) + (a.N - b.N) * (a.N - b.N));
}
