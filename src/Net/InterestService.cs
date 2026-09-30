using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Net;

/// <summary>
/// Who is told about whom. Runs at <c>World/Interest</c> on the server and on every client —
/// the path must match, because Godot routes RPCs by it.
///
/// <para>
/// The server decides, a couple of times a second, which players each client could actually see
/// (<see cref="Interest"/>) and sends every client its audience. Two things follow from that set:
/// the owner's synchronizer only sends its position to peers in their set, and the server's copy
/// of the player is only visible — so only spawned — on those peers. Out of sight, a player is
/// not merely hidden on your screen: its node does not exist there, so it costs no packets, no
/// animation and no memory.
/// </para>
///
/// <para>
/// The server decides rather than each owner, because an owner can only judge players it is
/// being sent: if A stopped sending to B and B to A, neither would ever learn the other had come
/// back into view. The server hears everyone, always.
/// </para>
/// </summary>
public partial class InterestService : Node
{
    public const string NodeName = "Interest";

    /// <summary>How often the server re-evaluates every pair.</summary>
    private const double Period = 0.5;

    /// <summary>A pair does not flip more often than this, whatever the distances do.</summary>
    private const double MinFlipSeconds = 1.0;

    // ---- client side ----------------------------------------------------------------------

    private HashSet<long>? _audience_;

    /// <summary>Raised on a client when the server sends a new set.</summary>
    public event Action? Changed;

    /// <summary>
    /// Whether this client should send its own state to <paramref name="peer"/>: whether that peer
    /// can see this client. Before the first audience arrives — and on servers without an
    /// interest service — everyone is, which is exactly the old behaviour.
    /// </summary>
    public bool SendsTo(long peer) => peer == 1 || _audience_ == null || _audience_.Contains(peer);

    // ---- server side ----------------------------------------------------------------------

    /// <summary>Server: the players container (children named by peer id).</summary>
    public Node? Players { get; set; }

    /// <summary>Server: coarse ground height for line of sight and height above ground.</summary>
    public Func<Vector3, float?>? Ground { get; set; }

    /// <summary>Server: true when two peers race each other; always relevant then.</summary>
    public Func<long, long, bool>? Together { get; set; }

    private readonly Dictionary<long, Interest.View> _views = new();
    private readonly Dictionary<long, HashSet<long>> _sets = new();
    private readonly Dictionary<(long, long), double> _flipped = new();
    private readonly List<(long Id, FootPlayer Player)> _scratch = new();
    private readonly List<long> _changed = new();
    private double _timer;

    public static InterestService CreateServer(Node parent, Node players, Func<Vector3, float?>? ground)
    {
        var s = new InterestService { Name = NodeName, Players = players, Ground = ground };
        parent.AddChild(s);
        return s;
    }

    public static InterestService CreateClient(Node parent)
    {
        var s = new InterestService { Name = NodeName };
        parent.AddChild(s);
        return s;
    }

    /// <summary>
    /// Server: whether <paramref name="viewer"/> may be sent <paramref name="target"/>. Everyone
    /// always has their own player. A viewer with no set yet (just joined) sees nobody else until
    /// its first round, half a second later: sending a newcomer all 31 other players and taking
    /// most of them back is worse.
    /// </summary>
    public bool ServerSees(long viewer, long target) =>
        viewer == 1 || viewer == target || (_sets.TryGetValue(viewer, out var set) && set.Contains(target));

    public void ForgetPeer(long peer)
    {
        _views.Remove(peer);
        _sets.Remove(peer);
        _audienceSent.Remove(peer);
        foreach (var set in _sets.Values) set.Remove(peer);
        _flipped.Clear();   // cheap to rebuild; a stale pair would only delay one flip
    }

    public override void _Process(double delta)
    {
        if (Players == null || !Multiplayer.IsServer()) return;
        _timer += delta;
        if (_timer < Period) return;
        _timer = 0;
        Evaluate(Time.GetTicksMsec() / 1000.0);
    }

    private void Evaluate(double now)
    {
        _scratch.Clear();
        foreach (var child in Players!.GetChildren())
            if (child is FootPlayer p && long.TryParse(p.Name, out long id))
                _scratch.Add((id, p));

        foreach (var (viewer, viewerNode) in _scratch)
        {
            // A new viewer was spawned everyone before its first set existed: its first round
            // re-decides every target, even if the answer is "nobody", or they stay for ever.
            bool first = !_sets.TryGetValue(viewer, out var set);
            if (first) _sets[viewer] = set = new HashSet<long>();
            var view = _views.TryGetValue(viewer, out var v) ? v : Interest.View.Default;
            var eye = viewerNode.GlobalPosition + Vector3.Up * 1.7f;
            _changed.Clear();

            foreach (var (target, targetNode) in _scratch)
            {
                if (target == viewer) continue;
                bool was = set.Contains(target);
                if (first) _changed.Add(target);
                var at = targetNode.GlobalPosition + Vector3.Up;
                float agl = Ground?.Invoke(at) is { } g ? at.Y - g : 0f;
                bool now_ = Interest.Relevant(eye, at, targetNode.Ride, agl, view, was,
                    Together?.Invoke(viewer, target) == true, Ground == null ? null : LineOfSight);
                if (now_ == was) continue;
                if (first) { if (now_) set.Add(target); continue; }
                // an edge case must not blink: each pair flips at most once a second
                if (_flipped.TryGetValue((viewer, target), out double last) && now - last < MinFlipSeconds) continue;
                _flipped[(viewer, target)] = now;
                if (now_) set.Add(target); else set.Remove(target);
                _changed.Add(target);
            }

            if (_changed.Count == 0) continue;
            // the server's copy of each changed target decides whether it exists on the viewer —
            // and so do the target's race NPCs, which are shown to whoever sees their owner
            foreach (var child in Players.GetChildren())
                if (child is FootPlayer t && FootPlayer.NetOwner(t.Name) is long owner && _changed.Contains(owner))
                    t.RefreshNetVisibility(viewer);
            foreach (var target in _changed) _dirty.Add(target);
        }

        // Each client is told its AUDIENCE — who can see it — because that is whom it must send
        // to. Not whom it can see: visibility is not symmetric. A plane is visible 8 km away, a
        // walker 0.9 km; sending by "whom I see" left the plane spawned on a walker's screen and
        // never updated (measured: a frozen remote plane at 1.5 km in the load test).
        foreach (var (target, _) in _scratch)
            if (!_audienceSent.Contains(target)) _dirty.Add(target);
        foreach (var target in _dirty)
        {
            if (!_sets.ContainsKey(target)) continue;   // not a player (or gone)
            _audience.Clear();
            foreach (var (viewer, set) in _sets)
                if (set.Contains(target)) _audience.Add(viewer);
            RpcId(target, MethodName.SetAudience, _audience.ToArray());
            _audienceSent.Add(target);
        }
        _dirty.Clear();
    }

    private readonly HashSet<long> _dirty = new(), _audienceSent = new();
    private readonly List<long> _audience = new();

    private bool LineOfSight(Vector3 eye, Vector3 target) => Interest.Clear(eye, target, Ground!);

    /// <summary>Client → server: the lens this client views the world through.</summary>
    public void ReportView(float far, float fovDeg)
    {
        if (Multiplayer.MultiplayerPeer == null || Multiplayer.IsServer()) return;
        RpcId(1, MethodName.ReceiveView, far, fovDeg);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveView(float far, float fovDeg)
    {
        if (!Multiplayer.IsServer()) return;
        // clamped: a client may not ask to be sent the whole country
        _views[Multiplayer.GetRemoteSenderId()] = new Interest.View(Mathf.Clamp(far, 500f, 150000f), Mathf.Clamp(fovDeg, 20f, 150f));
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetAudience(long[] peers)
    {
        _audience_ = new HashSet<long>(peers);
        Changed?.Invoke();
    }

    /// <summary>A coarse ground function over <c>horizon.bin</c>'s 100 m lattice, for the server.</summary>
    public static Func<Vector3, float?> HorizonGround(HorizonIndex index, Core.WorldOrigin origin) => world =>
    {
        var (e, n) = origin.ToLv95(world);
        var id = TileId.FromLv95(e, n);
        if (!index.TryGet(id, out _)) return null;
        double fc = (e - id.MinE) / HorizonFormat.SpacingM, fr = (id.MaxN - n) / HorizonFormat.SpacingM;
        int last = HorizonFormat.SamplesPerSide - 1;
        int c0 = Math.Clamp((int)fc, 0, last - 1), r0 = Math.Clamp((int)fr, 0, last - 1);
        double tx = Math.Clamp(fc - c0, 0, 1), ty = Math.Clamp(fr - r0, 0, 1);
        double h00 = index.HeightMetersAt(id, c0, r0), h10 = index.HeightMetersAt(id, c0 + 1, r0);
        double h01 = index.HeightMetersAt(id, c0, r0 + 1), h11 = index.HeightMetersAt(id, c0 + 1, r0 + 1);
        return (float)((h00 * (1 - tx) + h10 * tx) * (1 - ty) + (h01 * (1 - tx) + h11 * tx) * ty);
    };
}
