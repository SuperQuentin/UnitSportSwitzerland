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
    /// <summary>Everything that can be seen: the players, and the race NPCs (negative ids).</summary>
    private readonly List<(long Id, FootPlayer Player)> _targets = new();
    private readonly Dictionary<long, FootPlayer> _byId = new();
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
        viewer == 1 || viewer == target || (_sets.TryGetValue(viewer, out var set) && set.Contains(target))
        || _leaving.ContainsKey((viewer, target));

    /// <summary>
    /// A target leaving a viewer's sight is despawned there a moment AFTER its owner hears the new
    /// audience: despawn first and the owner's packets already in flight arrive for a node that is
    /// gone ("Node not found .../Sync"). The owner stops sending within a round trip; this waits more.
    /// </summary>
    private const double DespawnDelay = 0.4;
    private readonly Dictionary<(long Viewer, long Target), double> _leaving = new();
    private readonly List<(long, long)> _expired = new();

    public void ForgetPeer(long peer)
    {
        _views.Remove(peer);
        _sets.Remove(peer);
        _nearOf.Remove(peer);
        _farOf.Remove(peer);
        foreach (var set in _nearOf.Values) set.Remove(peer);
        foreach (var set in _farOf.Values) set.Remove(peer);
        foreach (var key in new List<(long, long)>(_leaving.Keys))
            if (key.Item1 == peer || key.Item2 == peer) _leaving.Remove(key);
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
        // despawns whose grace ran out (see DespawnDelay)
        _expired.Clear();
        foreach (var (pair, at) in _leaving) if (now >= at) _expired.Add(pair);
        foreach (var (viewer, target) in _expired)
        {
            _leaving.Remove((viewer, target));
            if (_byId.GetValueOrDefault(target) is { } t && IsInstanceValid(t)) t.RefreshNetVisibility(viewer);
        }

        _scratch.Clear();
        _targets.Clear();
        _byId.Clear();
        foreach (var child in Players!.GetChildren())
            if (child is FootPlayer p && FootPlayer.NetId(p.Name) is long id)
            {
                _targets.Add((id, p));
                _byId[id] = p;
                if (id > 0) _scratch.Add((id, p));
            }

        foreach (var (viewer, viewerNode) in _scratch)
        {
            // A new viewer was spawned everyone before its first set existed: its first round
            // re-decides every target, even if the answer is "nobody", or they stay for ever.
            bool first = !_sets.TryGetValue(viewer, out var set);
            if (first) _sets[viewer] = set = new HashSet<long>();
            var view = _views.TryGetValue(viewer, out var v) ? v : Interest.View.Default;
            // someone inside a building is 3 km under it: seen, and seeing, from where the building is
            var eye = Where(viewerNode) + Vector3.Up * 1.7f;
            _changed.Clear();

            foreach (var (target, targetNode) in _targets)
            {
                if (target == viewer) continue;
                bool was = set.Contains(target);
                if (first) _changed.Add(target);
                var at = Where(targetNode) + Vector3.Up;
                float agl = Ground?.Invoke(at) is { } g ? at.Y - g : 0f;
                bool now_ = Interest.Relevant(eye, at, targetNode.Ride, agl, view, was,
                    Together?.Invoke(viewer, target) == true, Ground == null ? null : LineOfSight);
                if (now_ == was) continue;
                if (first) { if (now_) set.Add(target); continue; }
                // an edge case must not blink: each pair flips at most once a second
                if (_flipped.TryGetValue((viewer, target), out double last) && now - last < MinFlipSeconds) continue;
                _flipped[(viewer, target)] = now;
                if (now_) { set.Add(target); _leaving.Remove((viewer, target)); }
                else { set.Remove(target); _leaving[(viewer, target)] = now + DespawnDelay; }
                _changed.Add(target);
            }

            if (_changed.Count == 0) continue;
            // the server's copy of each changed target (a player or a race NPC) decides whether it exists on the viewer
            foreach (var target in _changed) _byId[target].RefreshNetVisibility(viewer);
        }

        // Who gets each player's state, and how often: every viewer whose set holds it, split by
        // distance — within NearRadius (or racing it) the 30 Hz relay, beyond it the 6 Hz one.
        // The SERVER rebroadcasts (FootPlayer's RelayNear/RelayFar); owners send only to it. The
        // audience, not the set: visibility is asymmetric (a plane is seen 8 km away, a walker
        // 0.9 km), so what counts is who sees the target, not whom the target sees.
        // A race NPC is a target like a player (#50): relayed from where IT is, whoever simulates it.
        foreach (var (target, targetNode) in _targets)
        {
            _near.Clear(); _far.Clear();
            var at = Where(targetNode);
            _nearOf.TryGetValue(target, out var lastNear);
            foreach (var (viewer, viewerNode) in _scratch)
            {
                if (viewer == target || !_sets.TryGetValue(viewer, out var set) || !set.Contains(target)) continue;
                float radius = lastNear != null && lastNear.Contains(viewer) ? NearRadius * 1.2f : NearRadius;
                bool near = Together?.Invoke(viewer, target) == true
                    || Where(viewerNode).DistanceSquaredTo(at) < radius * radius;
                (near ? _near : _far).Add(viewer);
            }
            if (lastNear != null && _farOf.TryGetValue(target, out var lastFar)
                && lastNear.SetEquals(_near) && lastFar.SetEquals(_far)) continue;
            _nearOf[target] = new HashSet<long>(_near);
            _farOf[target] = new HashSet<long>(_far);
            targetNode.RefreshRelays();
        }
    }

    /// <summary>Server: whether <paramref name="viewer"/> gets <paramref name="target"/>'s 30 Hz stream.</summary>
    public bool RelaysNear(long viewer, long target) => _nearOf.TryGetValue(target, out var s) && s.Contains(viewer);

    /// <summary>Server: whether <paramref name="viewer"/> gets <paramref name="target"/>'s 6 Hz stream.</summary>
    public bool RelaysFar(long viewer, long target) => _farOf.TryGetValue(target, out var s) && s.Contains(viewer);

    /// <summary>Within this, a viewer gets the full-rate stream; beyond it the reduced one.</summary>
    private const float NearRadius = 300f;

    private readonly HashSet<long> _near = new(), _far = new();
    private readonly Dictionary<long, HashSet<long>> _nearOf = new(), _farOf = new();

    private bool LineOfSight(Vector3 eye, Vector3 target) => Interest.Clear(eye, target, Ground!);

    /// <summary>Where a player is for interest: up in the world, even inside a building (<see cref="Interiors.InteriorManager.SurfacePoint"/>).</summary>
    private Vector3 Where(Node3D player) => Interiors.InteriorManager.SurfacePoint(player.GlobalPosition, Ground);

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
