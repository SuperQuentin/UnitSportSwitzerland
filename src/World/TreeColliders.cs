using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// Solid tree trunks, only where something can hit them.
///
/// <para>
/// The region has ~40 M trees and a forest tile 100k+, so trees get no collision of their own.
/// Instead a pool of trunk bodies (<see cref="StaticBody3D"/> + <see cref="CylinderShape3D"/>) is
/// laid out around each anchor that already asks <see cref="ChunkManager"/> for collision — the
/// local player, a vehicle rolling on its own — from the same <c>.trees</c> files the renderer
/// draws, bucketed into 10 m cells. Cells are handed out and taken back as the anchor moves, around
/// it and around a point ahead along its velocity (a car at 150 km/h covers the whole radius in a
/// second), and never more than <see cref="BudgetPerFrame"/> trunks are placed in one frame.
/// </para>
///
/// <para>
/// Trunks are on their own layer (<see cref="Layer"/>): bodies include it in their mask, the
/// camera pull-in rays leave it out, so a chase camera in a forest is never shoved into the back of
/// the rider's head by a trunk it could see past. Shrubs (kind 1) stay walk-through. The trunk is a
/// cylinder the girth of the drawn trunk up to the whole tree height — a plane clipping a treetop
/// hits the tree's axis, not its crown, which is the simple choice rather than a crown shape.
/// </para>
/// </summary>
public partial class TreeColliders : Node3D
{
    /// <summary>Collision layer bit of every trunk: layer 2.</summary>
    public const uint Layer = 1u << 1;

    public static TreeColliders? Instance { get; private set; }

    private const float Cell = 10f;
    private const float Radius = 45f;
    private const float LookAhead = 1.2f;   // seconds of travel
    private const int BudgetPerFrame = 64;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;

    private readonly record struct Trunk(Vector3 Base, float Radius, float Height);

    private readonly Dictionary<TileId, Dictionary<long, List<Trunk>>?> _tiles = new();
    private readonly HashSet<TileId> _loading = new();
    private readonly Dictionary<long, List<StaticBody3D>> _live = new();
    private readonly Stack<StaticBody3D> _free = new();
    private readonly HashSet<long> _wanted = new();
    private readonly List<long> _scratch = new();

    public int LiveTrunks { get; private set; }
    /// <summary>Main-thread milliseconds spent in the last physics frame.</summary>
    public double LastMs { get; private set; }
    public double MaxMs { get; set; }

    public TreeColliders(ChunkManager chunks, WorldOrigin origin)
    {
        Name = "TreeColliders";
        _chunks = chunks;
        _origin = origin;
        Instance = this;
    }

    /// <summary>
    /// Drops every trunk and every tree tile read so far: called when real terrain replaces the
    /// generated stand-in, whose trees are not the real ones. Cells are re-filled as anchors move.
    /// </summary>
    public void Forget()
    {
        foreach (var bodies in _live.Values)
            foreach (var b in bodies) { b.ProcessMode = ProcessModeEnum.Disabled; _free.Push(b); }
        _live.Clear();
        _tiles.Clear();
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _PhysicsProcess(double delta)
    {
        var watch = Stopwatch.StartNew();
        _wanted.Clear();
        foreach (var anchor in _chunks.CollisionAnchors)
        {
            var at = anchor.GlobalPosition;
            Want(at);
            var v = anchor is CharacterBody3D body ? body.Velocity with { Y = 0 } : Vector3.Zero;
            if (v.LengthSquared() > 4f) Want(at + v.LimitLength(40f / LookAhead) * LookAhead);
        }

        // release cells nobody wants any more
        _scratch.Clear();
        foreach (var key in _live.Keys) if (!_wanted.Contains(key)) _scratch.Add(key);
        foreach (var key in _scratch)
        {
            foreach (var b in _live[key]) { b.ProcessMode = ProcessModeEnum.Disabled; _free.Push(b); }
            LiveTrunks -= _live[key].Count;
            _live.Remove(key);
        }

        // place new ones, nearest first is not worth sorting for: the budget is a whole radius
        int placed = 0;
        foreach (var key in _wanted)
        {
            if (placed >= BudgetPerFrame) break;
            if (_live.ContainsKey(key)) continue;
            var trunks = TrunksIn(key, out bool ready);
            if (!ready) continue;   // its tile is still loading: try again next frame
            var list = new List<StaticBody3D>(trunks?.Count ?? 0);
            if (trunks != null)
                foreach (var t in trunks) { list.Add(Place(t)); placed++; }
            _live[key] = list;
            LiveTrunks += list.Count;
        }

        LastMs = watch.Elapsed.TotalMilliseconds;
        MaxMs = System.Math.Max(MaxMs, LastMs);
    }

    private void Want(Vector3 at)
    {
        int cx = Mathf.FloorToInt(at.X / Cell), cz = Mathf.FloorToInt(at.Z / Cell);
        int r = Mathf.CeilToInt(Radius / Cell);
        for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
                if (dx * dx + dz * dz <= r * r) _wanted.Add(Key(cx + dx, cz + dz));
    }

    private static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

    /// <summary>
    /// Every trunk in a cell, from whichever tiles it overlaps (world cells need not line up with
    /// the kilometre lattice). <paramref name="ready"/> is false while any of them is loading.
    /// </summary>
    private List<Trunk>? TrunksIn(long key, out bool ready)
    {
        ready = true;
        int cx = (int)(key >> 32), cz = (int)(uint)key;
        List<Trunk>? all = null;
        var a = _origin.TileAt(new Vector3(cx * Cell, 0, cz * Cell));
        var b = _origin.TileAt(new Vector3((cx + 1) * Cell - 0.01f, 0, (cz + 1) * Cell - 0.01f));
        // a cell straddles at most a 2x2 block of tiles
        for (int e = System.Math.Min(a.E, b.E); e <= System.Math.Max(a.E, b.E); e++)
            for (int n = System.Math.Min(a.N, b.N); n <= System.Math.Max(a.N, b.N); n++)
            {
                var tile = new TileId(e, n);
                if (!_tiles.TryGetValue(tile, out var cells)) { Load(tile); ready = false; continue; }
                if (cells != null && cells.TryGetValue(key, out var list)) (all ??= new()).AddRange(list);
            }
        return all;
    }

    private async void Load(TileId tile)
    {
        if (!_loading.Add(tile) || _chunks.Source is not { } source) return;
        try
        {
            var trees = await source.LoadTreesAsync(tile);
            var origin = _origin;
            _tiles[tile] = trees == null ? null : await Task.Run(() =>
            {
                var cells = new Dictionary<long, List<Trunk>>();
                foreach (var t in trees)
                {
                    if (t.Kind == 1) continue;   // shrubs: walk through them
                    // the drawn trunk: 0.10 of the crown radius (ChunkNode.ConeMesh/CrownMesh),
                    // clamped to what a real trunk measures
                    float slender = t.Kind switch { 2 => 0.34f, 3 => 0.40f, _ => 0.26f };
                    float r = Mathf.Clamp(t.Height * slender * 0.10f, 0.12f, 0.5f);
                    var w = origin.ToWorld(tile.MinE + t.X, tile.MaxN - t.Z, t.Y);
                    long key = Key(Mathf.FloorToInt(w.X / Cell), Mathf.FloorToInt(w.Z / Cell));
                    if (!cells.TryGetValue(key, out var list)) cells[key] = list = new();
                    list.Add(new Trunk(w, r, Mathf.Max(t.Height, 1.5f)));
                }
                return cells;
            });
        }
        catch (System.Exception e)
        {
            GD.PushWarning($"[trees] tile {tile}: {e.Message}");
            _tiles[tile] = null;
        }
        finally { _loading.Remove(tile); }
        // ponytail: loaded tiles are never evicted; ~50 bytes a trunk, a forest tile ~5 MB. Evict when a long drive shows it.
    }

    private StaticBody3D Place(Trunk t)
    {
        if (!_free.TryPop(out var body))
        {
            body = new StaticBody3D { CollisionLayer = Layer, CollisionMask = 0 };
            body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D() });
            AddChild(body);
        }
        var shape = (CylinderShape3D)body.GetChild<CollisionShape3D>(0).Shape;
        shape.Radius = t.Radius;
        shape.Height = t.Height;
        body.GlobalPosition = t.Base + Vector3.Up * (t.Height * 0.5f - 0.3f);   // a little into the ground on a slope
        body.ProcessMode = ProcessModeEnum.Inherit;
        return body;
    }

    /// <summary>The live trunk nearest a point, for checks.</summary>
    public (Vector3 Base, float Radius)? NearestLive(Vector3 at, float minDistance = 0f)
    {
        (Vector3, float)? best = null;
        float bestD = float.MaxValue;
        foreach (var list in _live.Values)
            foreach (var b in list)
            {
                var p = b.GlobalPosition;
                float d = new Vector2(p.X - at.X, p.Z - at.Z).Length();
                if (d < minDistance || d >= bestD) continue;
                var shape = (CylinderShape3D)b.GetChild<CollisionShape3D>(0).Shape;
                bestD = d;
                best = (p - Vector3.Up * (shape.Height * 0.5f - 0.3f), shape.Radius);
            }
        return best;
    }
}
