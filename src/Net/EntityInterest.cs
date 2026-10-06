using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Net;

/// <summary>A vehicle, dropped item or radio as <see cref="EntityInterest"/> sees it (#689).</summary>
public interface IInterestEntity
{
    /// <summary>Where it is, LV95, as last published.</summary>
    GlobalPos InterestAt { get; }

    /// <summary>How far a viewer with this lens is sent it, m (<see cref="EntityInterestRules"/>).</summary>
    float InterestRange(Interest.View view);

    /// <summary>Big enough to be sent from further than a cell search reaches: checked against every viewer.</summary>
    bool InterestBig { get; }

    /// <summary>Re-asks its synchronizers about <paramref name="peer"/>: spawn or despawn there, start or stop its stream.</summary>
    void RefreshInterest(long peer);

    /// <summary>Server, a client-owned one: its relay at full rate while it moves, slowly once it rests.</summary>
    void RelayRate(bool moving);
}

/// <summary>
/// Entity interest (#689): which peer has which vehicle, dropped item and radio. Before, every one
/// was spawned on every peer, however far: a client held every car parked across the country. Now
/// the server decides per peer, once a second, with <see cref="EntityInterestRules"/> (the range a
/// thing of that size stays visible on that peer's screen, hysteresis, look-ahead, a spawn budget),
/// and each entity's server-owned synchronizers (<see cref="EntityNet"/>) answer from its sets.
///
/// <para>
/// The same split as players (<see cref="InterestService"/>): a client-owned entity's owner sends
/// its state to the server only, and the server relays it to the peers that have it. Leaving, the
/// stream stops first and the node goes <see cref="DespawnDelay"/> later, so nothing in flight
/// arrives for a node that is gone.
/// </para>
/// </summary>
public partial class EntityInterest : Node
{
    public const string NodeName = "EntityInterest";

    /// <summary>How often every viewer's entities are decided, s.</summary>
    private const double Period = 1.0;

    /// <summary>After its stream stopped, a leaving entity is despawned this much later, s.</summary>
    private const double DespawnDelay = 0.4;

    /// <summary>Entities up to this size are found by cell; bigger ones (lorries, ships, aircraft) are checked against every viewer.</summary>
    public const float CellSize = 8f;

    private const double CellM = 1000;

    /// <summary>The server's, or null on a client and offline.</summary>
    public static EntityInterest? Instance { get; private set; }

    private readonly Node _players;
    private readonly InterestService? _views;
    private readonly Node[] _containers;

    /// <summary>Per viewer: the entities it has (instance ids), streams and all.</summary>
    private readonly Dictionary<long, HashSet<ulong>> _sets = new();
    /// <summary>Per viewer: entities whose stream stopped and whose node goes at the time given.</summary>
    private readonly Dictionary<(long Viewer, ulong Entity), double> _leaving = new();
    private readonly Dictionary<long, (double E, double N, double At)> _lastSeen = new();
    private readonly Dictionary<ulong, GlobalPos> _lastAt = new();

    // per round, reused (#221: no allocations per round)
    private readonly List<(ulong Id, IInterestEntity Entity, GlobalPos At)> _entities = new();
    private readonly Dictionary<ulong, int> _index = new();
    private readonly Dictionary<(long, long), List<int>> _cells = new();
    private readonly Stack<List<int>> _spare = new();
    private readonly List<int> _big = new();
    private readonly HashSet<int> _seen = new();
    private readonly List<EntityInterestRules.Candidate> _candidates = new();
    private readonly List<EntityInterestRules.Candidate> _newcomers = new();
    private readonly List<int> _keep = new();
    private readonly HashSet<ulong> _keepIds = new();
    private readonly List<ulong> _drop = new();
    private readonly List<(long, ulong)> _expired = new();
    private double _sinceRound;

    private EntityInterest(Node players, InterestService? views, Node[] containers)
    {
        Name = NodeName;
        _players = players;
        _views = views;
        _containers = containers;
    }

    /// <summary>Server: decides for the entities under <paramref name="containers"/> (vehicles, dropped items, radios).</summary>
    public static EntityInterest CreateServer(Node parent, Node players, InterestService? views, params Node[] containers)
    {
        var interest = new EntityInterest(players, views, containers);
        parent.AddChild(interest);
        Instance = interest;
        return interest;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>Whether <paramref name="entity"/> exists on <paramref name="viewer"/> (its spawn), leaving or not.</summary>
    public bool Exists(long viewer, Node entity)
    {
        ulong id = entity.GetInstanceId();
        return _sets.TryGetValue(viewer, out var set) && set.Contains(id) || _leaving.ContainsKey((viewer, id));
    }

    /// <summary>Whether <paramref name="viewer"/> is sent <paramref name="entity"/>'s state now.</summary>
    public bool Sends(long viewer, Node entity) =>
        _sets.TryGetValue(viewer, out var set) && set.Contains(entity.GetInstanceId());

    /// <summary>A peer left.</summary>
    public void ForgetPeer(long peer)
    {
        _sets.Remove(peer);
        _lastSeen.Remove(peer);
        _expired.Clear();
        foreach (var key in _leaving.Keys)
            if (key.Viewer == peer) _expired.Add(key);
        foreach (var key in _expired) _leaving.Remove(key);
    }

    /// <summary>
    /// A new entity, before its spawn goes out: every viewer it is in range of has it at once, not a
    /// round later (a can thrown in front of someone is seen flying). No budget: it is one entity.
    /// </summary>
    public void Admit(Node node)
    {
        if (node is not IInterestEntity entity) return;
        ulong id = node.GetInstanceId();
        var at = entity.InterestAt;
        foreach (var (viewer, set) in _sets)
        {
            if (!_lastSeen.TryGetValue(viewer, out var v)) continue;
            double d = Math.Sqrt((at.E - v.E) * (at.E - v.E) + (at.N - v.N) * (at.N - v.N));
            if (EntityInterestRules.Keep(d, entity.InterestRange(ViewOf(viewer)), was: false)) set.Add(id);
        }
    }

    private Interest.View ViewOf(long viewer) => _views?.ViewOf(viewer) ?? Interest.View.Default;

    public override void _Process(double delta)
    {
        _sinceRound += delta;
        double now = RealClock.Now;
        // despawns whose grace ran out, every frame: the delay is short
        if (_leaving.Count > 0)
        {
            _expired.Clear();
            foreach (var (key, at) in _leaving) if (now >= at) _expired.Add(key);
            foreach (var key in _expired)
            {
                _leaving.Remove(key);
                if (InstanceFromId(key.Item2) is IInterestEntity e) e.RefreshInterest(key.Item1);
            }
        }
        if (_sinceRound < Period) return;
        double dt = _sinceRound;
        _sinceRound = 0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Round(now, dt);
        ServerStats.Ran("entity interest", t0);
    }

    private void Gather()
    {
        _entities.Clear();
        _index.Clear();
        _big.Clear();
        foreach (var list in _cells.Values) { list.Clear(); _spare.Push(list); }
        _cells.Clear();
        foreach (var container in _containers)
            foreach (var child in container.GetChildren())
            {
                if (child is not IInterestEntity entity || child.IsQueuedForDeletion()) continue;
                ulong id = child.GetInstanceId();
                var at = entity.InterestAt;
                int i = _entities.Count;
                _entities.Add((id, entity, at));
                _index[id] = i;
                if (entity.InterestBig) { _big.Add(i); continue; }
                var key = ((long)Math.Floor(at.E / CellM), (long)Math.Floor(at.N / CellM));
                if (!_cells.TryGetValue(key, out var cell)) _cells[key] = cell = _spare.Count > 0 ? _spare.Pop() : new List<int>();
                cell.Add(i);

                // a client-owned one is relayed at full rate only while it moves
                bool moving = _lastAt.TryGetValue(id, out var last) && last.DistanceTo(at) > 0.01;
                _lastAt[id] = at;
                entity.RelayRate(moving);
            }
        // forget what left the world
        if (_lastAt.Count > _entities.Count)
        {
            _drop.Clear();
            foreach (var id in _lastAt.Keys) if (!_index.ContainsKey(id)) _drop.Add(id);
            foreach (var id in _drop) _lastAt.Remove(id);
        }
    }

    private void Round(double now, double dt)
    {
        Gather();
        foreach (var child in _players.GetChildren())
        {
            if (child is not FootPlayer p || FootPlayer.NetId(p.Name) is not long viewer || viewer <= 0) continue;
            var at = p.Global;
            var (aheadE, aheadN) = _lastSeen.TryGetValue(viewer, out var prev)
                ? EntityInterestRules.Ahead(at.E, at.N, prev.E, prev.N, now - prev.At)
                : (at.E, at.N);
            _lastSeen[viewer] = (at.E, at.N, now);
            if (!_sets.TryGetValue(viewer, out var set)) _sets[viewer] = set = new HashSet<ulong>();
            var view = ViewOf(viewer);

            // the cells within reach of a lorry-sized thing for this lens, the big ones, and what it has
            _seen.Clear();
            _candidates.Clear();
            float reach = EntityInterestRules.VehicleRange(CellSize, view.Far, view.FovDeg) * EntityInterestRules.LeaveFactor;
            int cells = (int)Math.Ceiling(reach / CellM);
            long ce = (long)Math.Floor(at.E / CellM), cn = (long)Math.Floor(at.N / CellM);
            for (long de = -cells; de <= cells; de++)
                for (long dn = -cells; dn <= cells; dn++)
                    if (_cells.TryGetValue((ce + de, cn + dn), out var cell))
                        foreach (int i in cell) Consider(i);
            foreach (int i in _big) Consider(i);
            foreach (var id in set)
                if (_index.TryGetValue(id, out int i)) Consider(i);

            void Consider(int i)
            {
                if (!_seen.Add(i)) return;
                var (id, entity, where) = _entities[i];
                double d = Math.Min(Flat(where, at.E, at.N), Flat(where, aheadE, aheadN));
                _candidates.Add(new EntityInterestRules.Candidate(i, d, entity.InterestRange(view), set.Contains(id)));
            }

            EntityInterestRules.Select(_candidates, _keep, _newcomers);
            _keepIds.Clear();
            foreach (int i in _keep) _keepIds.Add(_entities[i].Id);

            // leaving: the stream stops now, the node goes a moment later
            _drop.Clear();
            foreach (var id in set) if (!_keepIds.Contains(id)) _drop.Add(id);
            foreach (var id in _drop)
            {
                set.Remove(id);
                if (!_index.TryGetValue(id, out int i)) continue;   // freed: the spawner removed it everywhere
                _leaving[(viewer, id)] = now + DespawnDelay;
                _entities[i].Entity.RefreshInterest(viewer);
            }
            // coming: spawned with its current state, then streamed
            foreach (var id in _keepIds)
            {
                if (!set.Add(id)) continue;
                _leaving.Remove((viewer, id));
                _entities[_index[id]].Entity.RefreshInterest(viewer);
            }
        }
    }

    private static double Flat(GlobalPos a, double e, double n) => Math.Sqrt((a.E - e) * (a.E - e) + (a.N - n) * (a.N - n));
}

/// <summary>
/// The synchronizers that carry an entity's interest (#689), the same on every peer so their paths
/// match: <c>Vis</c> (server-owned, empty) decides whether it exists on a peer, and for a
/// client-owned one <c>Relay</c> (server-owned) carries its owner's state on to the peers that
/// have it, its owner's <c>Sync</c> going to the server only. Godot composes spawn visibility as the
/// OR of the server-owned synchronizers, so the owner always has its own.
/// </summary>
public static class EntityNet
{
    /// <summary>
    /// Adds <paramref name="sync"/> (the entity's own) to <paramref name="entity"/> with its interest
    /// synchronizers, and <paramref name="serverOwned"/> (server-owned data, a radio's play state)
    /// streamed only to the peers that have it. Returns the relay, or null when the server owns the entity.
    /// </summary>
    public static MultiplayerSynchronizer? Add(Node entity, MultiplayerSynchronizer sync, float relayInterval,
        params MultiplayerSynchronizer[] serverOwned)
    {
        long authority = entity.GetMultiplayerAuthority();
        bool clientOwned = authority != 1;
        bool server = entity.Multiplayer.MultiplayerPeer is not (null or OfflineMultiplayerPeer) && entity.Multiplayer.IsServer();
        var interest = EntityInterest.Instance;

        var vis = new MultiplayerSynchronizer
        {
            Name = "Vis", RootPath = new NodePath(".."), ReplicationConfig = new SceneReplicationConfig(),
            // no data: without this Godot would ask its filter every frame for every peer
            ReplicationInterval = 3600f, DeltaInterval = 3600f,
            VisibilityUpdateMode = MultiplayerSynchronizer.VisibilityUpdateModeEnum.None,
        };
        vis.SetMultiplayerAuthority(1);

        MultiplayerSynchronizer? relay = null;
        if (clientOwned)
        {
            // the owner's state goes to the server only (filters are only asked on the authority)
            sync.AddVisibilityFilter(Callable.From((long peer) => peer == 1));
            relay = Relay(sync.ReplicationConfig, relayInterval);
        }

        if (server)
        {
            // peer 0 is Godot asking "visible to everyone?": no, or it broadcasts and never asks per peer
            vis.AddVisibilityFilter(Callable.From((long peer) =>
                peer != 0 && (peer == authority || interest?.Exists(peer, entity) != false)));
            if (relay != null)
                relay.AddVisibilityFilter(Callable.From((long peer) =>
                    peer != 0 && peer != authority && interest?.Sends(peer, entity) != false));
            else
            {
                sync.VisibilityUpdateMode = MultiplayerSynchronizer.VisibilityUpdateModeEnum.None;
                sync.AddVisibilityFilter(Callable.From((long peer) => peer != 0 && interest?.Sends(peer, entity) != false));
            }
            foreach (var s in serverOwned)
            {
                s.VisibilityUpdateMode = MultiplayerSynchronizer.VisibilityUpdateModeEnum.None;
                s.AddVisibilityFilter(Callable.From((long peer) => peer != 0 && interest?.Sends(peer, entity) != false));
            }
            interest?.Admit(entity);
        }

        entity.AddChild(sync);
        foreach (var s in serverOwned) entity.AddChild(s);
        entity.AddChild(vis);
        if (relay != null) entity.AddChild(relay);
        return relay;
    }

    /// <summary>Server: re-asks every interest synchronizer of <paramref name="entity"/> about one peer.</summary>
    public static void Refresh(Node entity, long peer)
    {
        foreach (var name in Names)
            if (entity.GetNodeOrNull<MultiplayerSynchronizer>(name) is { } s && s.GetMultiplayerAuthority() == 1)
                s.UpdateVisibility((int)peer);
    }

    private static readonly NodePath[] Names = { "Vis", "Relay", "Sync", "State" };

    /// <summary>A server-owned copy of an owner's synchronizer: the same properties, modes and spawn flags.</summary>
    private static MultiplayerSynchronizer Relay(SceneReplicationConfig source, float interval)
    {
        var config = new SceneReplicationConfig();
        foreach (var prop in source.GetProperties())
        {
            config.AddProperty(prop);
            config.PropertySetSpawn(prop, source.PropertyGetSpawn(prop));
            config.PropertySetReplicationMode(prop, source.PropertyGetReplicationMode(prop));
        }
        var relay = new MultiplayerSynchronizer
        {
            Name = "Relay", RootPath = new NodePath(".."), ReplicationConfig = config,
            ReplicationInterval = interval, DeltaInterval = 0.1f,
            VisibilityUpdateMode = MultiplayerSynchronizer.VisibilityUpdateModeEnum.None,
        };
        relay.SetMultiplayerAuthority(1);
        return relay;
    }
}
