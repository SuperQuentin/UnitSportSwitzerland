using System.Collections.Concurrent;
using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Leaves;

/// <summary>
/// Collision patches round bodies, on client and server alike (#1511, G2 C2): every patch (a level-16 cube cell, ~150 m, <c>LeafCollision</c>) a body or its look-ahead point is within <see cref="CollisionReach.RadiusM"/> of is built
/// on a worker; a patch is released once every body is past <see cref="CollisionReach.ReleaseM"/>; an airborne body wants
/// none (<see cref="CollisionReach"/>'s rule, in metres on the Earth). Nearest patches are built first, at most
/// <c>workers</c> at once.
///
/// <para>
/// The owner of the physics bodies (C1's earth streamer on a client, the server's world for its players and bots) calls
/// <see cref="Update"/> with its bodies, then on its own thread takes the finished patches (<see cref="TryTakeReady"/>:
/// faces in the patch's leaf's anchor frame, null for a patch with no ground) and the released ones
/// (<see cref="TryTakeReleased"/>), ready first: a patch released while waiting in the ready queue comes out of both, in
/// that order. Thread-safe.
/// </para>
/// </summary>
public sealed class CollisionPatches(Func<CubeKey, Vector3[]?> build, int workers = 2)
{
    /// <summary>A patch's level: two below a leaf's, 128 of its 512 lattice cells a side (~150 m).</summary>
    public const int PatchLevel = LeafFrames.LeafLevel + 2;

    /// <summary>A body: where it is and where it will be (<see cref="CollisionReach.Ahead"/>), and whether both are airborne.</summary>
    public readonly record struct Body(EcefPos At, EcefPos Ahead, bool Airborne);

    private readonly object _gate = new();
    private readonly HashSet<CubeKey> _held = new(), _building = new(), _dropped = new();
    private readonly List<(CubeKey Key, double Distance)> _waiting = new();
    private readonly ConcurrentQueue<(CubeKey Key, Vector3[]? Faces)> _ready = new();
    private readonly ConcurrentQueue<CubeKey> _released = new();
    private int _running;

    /// <summary>Told when a build throws (the patch is handed out with no faces).</summary>
    public Action<CubeKey, Exception>? Failed { get; set; }

    /// <summary>Patches built and not released.</summary>
    public int Held { get { lock (_gate) return _held.Count; } }

    /// <summary>Whether a build is running or waiting.</summary>
    public bool Busy { get { lock (_gate) return _running > 0 || _waiting.Count > 0; } }

    public bool TryTakeReady(out CubeKey key, out Vector3[]? faces)
    {
        bool ok = _ready.TryDequeue(out var r);
        (key, faces) = r;
        return ok;
    }

    public bool TryTakeReleased(out CubeKey key) => _released.TryDequeue(out key);

    /// <summary>The bodies now: asks for the patches they want, releases those none holds.</summary>
    public void Update(IReadOnlyList<Body> bodies)
    {
        var want = new Dictionary<CubeKey, double>();
        var keep = new HashSet<CubeKey>();
        foreach (var b in bodies)
        {
            if (b.Airborne) continue;
            var at = Near(b.At, CollisionReach.ReleaseM);
            var ahead = Near(b.Ahead, CollisionReach.ReleaseM);
            foreach (var k in at.Keys.Union(ahead.Keys))
            {
                double d = at.GetValueOrDefault(k, double.MaxValue), da = ahead.GetValueOrDefault(k, double.MaxValue);
                if (CollisionReach.Wants(d, da, false, holding: true)) keep.Add(k);
                if (CollisionReach.Wants(d, da, false)) want[k] = Math.Min(want.GetValueOrDefault(k, double.MaxValue), Math.Min(d, da));
            }
        }
        lock (_gate)
        {
            foreach (var k in _held.Where(k => !keep.Contains(k)).ToList())
            {
                _held.Remove(k);
                _released.Enqueue(k);
            }
            foreach (var k in _building) if (!keep.Contains(k)) _dropped.Add(k);
            _waiting.RemoveAll(w => !keep.Contains(w.Key));
            foreach (var (k, d) in want)
            {
                _dropped.Remove(k);
                if (_held.Contains(k) || _building.Contains(k)) continue;
                int i = _waiting.FindIndex(w => w.Key == k);
                if (i >= 0) _waiting[i] = (k, d);
                else _waiting.Add((k, d));
            }
            _waiting.Sort((a, b) => b.Distance.CompareTo(a.Distance));   // nearest last: taken from the end
            Pump();
        }
    }

    private void Pump()
    {
        while (_running < workers && _waiting.Count > 0)
        {
            var (key, _) = _waiting[^1];
            _waiting.RemoveAt(_waiting.Count - 1);
            _building.Add(key);
            _running++;
            Task.Run(() =>
            {
                Vector3[]? faces = null;
                try { faces = build(key); }
                catch (Exception e) { Failed?.Invoke(key, e); }
                lock (_gate)
                {
                    _running--;
                    _building.Remove(key);
                    if (!_dropped.Remove(key))
                    {
                        _held.Add(key);
                        _ready.Enqueue((key, faces));
                    }
                    Pump();
                }
            });
        }
    }

    /// <summary>
    /// The patches within <paramref name="radiusM"/> of a point and their horizontal distance from it (0 inside): a
    /// breadth-first walk over cube neighbours (across face edges), a patch's distance the chord from the point to its
    /// centre less its circumradius, both on the ellipsoid.
    /// </summary>
    public static Dictionary<CubeKey, double> Near(EcefPos point, double radiusM)
    {
        var g = Ellipsoid.Wgs84.ToGeodetic(point);
        var ground = Ellipsoid.Wgs84.ToEcef(g.LatDeg * Math.PI / 180, g.LonDeg * Math.PI / 180, 0);
        var start = CubeKey.FromLatLon(g.LatDeg, g.LonDeg, PatchLevel);
        var found = new Dictionary<CubeKey, double>();
        var seen = new HashSet<CubeKey> { start };
        var queue = new Queue<CubeKey>([start]);
        while (queue.Count > 0)
        {
            var k = queue.Dequeue();
            double d = Distance(ground, k);
            if (d > radiusM && k != start) continue;
            found[k] = d;
            foreach (var n in k.Neighbours())
                if (seen.Add(n)) queue.Enqueue(n);
        }
        return found;
    }

    private static double Distance(EcefPos ground, CubeKey patch)
    {
        var centre = patch.Vertex(1, 1, 1, 0);
        double radius = 0;
        foreach (var (i, j) in new[] { (0, 0), (2, 0), (0, 2), (2, 2) })
            radius = Math.Max(radius, centre.DistanceTo(patch.Vertex(1, i, j, 0)));
        return Math.Max(0, ground.DistanceTo(centre) - radius);
    }
}
