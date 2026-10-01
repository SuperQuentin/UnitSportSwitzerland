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
/// draws, bucketed into 10 m cells of the LV95 grid, so neither a cell nor a trunk changes when
/// the origin moves (#185): a cell lies in exactly one tile, and a trunk is kept where the file
/// puts it, relative to its tile. Cells are handed out and taken back as the anchor moves, around
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
public partial class TreeColliders : Node3D, Core.IOriginContainer
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

    /// <summary>A trunk, relative to its tile's NW corner (east, altitude, south), as the .trees file has it.</summary>
    private readonly record struct Trunk(float X, float Y, float Z, float Radius, float Height);

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
            var trunks = TrunksIn(key, out var tile, out bool ready);
            if (!ready) continue;   // its tile is still loading: try again next frame
            var list = new List<StaticBody3D>(trunks?.Count ?? 0);
            if (trunks != null)
                foreach (var t in trunks) { list.Add(Place(tile, t)); placed++; }
            _live[key] = list;
            LiveTrunks += list.Count;
        }

        LastMs = watch.Elapsed.TotalMilliseconds;
        MaxMs = System.Math.Max(MaxMs, LastMs);
    }

    private void Want(Vector3 at)
    {
        var (e, n) = _origin.ToLv95(at);
        long ce = (long)System.Math.Floor(e / Cell), cn = (long)System.Math.Floor(n / Cell);
        int r = Mathf.CeilToInt(Radius / Cell);
        for (int de = -r; de <= r; de++)
            for (int dn = -r; dn <= r; dn++)
                if (de * de + dn * dn <= r * r) _wanted.Add(Key(ce + de, cn + dn));
    }

    /// <summary>A 10 m cell of the LV95 grid, by its south-west corner's indices.</summary>
    private static long Key(long ce, long cn) => (ce << 32) ^ (uint)cn;

    private static (long E, long N) Unkey(long key) => (key >> 32, (int)(uint)key);

    /// <summary>
    /// Every trunk in a cell, and the tile they are relative to. A cell lies in exactly one tile
    /// (10 m divides the kilometre); <paramref name="ready"/> is false while that tile is loading.
    /// </summary>
    private List<Trunk>? TrunksIn(long key, out TileId tile, out bool ready)
    {
        var (ce, cn) = Unkey(key);
        tile = TileId.FromLv95((ce + 0.5) * Cell, (cn + 0.5) * Cell);
        ready = true;
        if (!_tiles.TryGetValue(tile, out var cells)) { Load(tile); ready = false; return null; }
        return cells != null && cells.TryGetValue(key, out var list) ? list : null;
    }

    private async void Load(TileId tile)
    {
        if (!_loading.Add(tile) || _chunks.Source is not { } source) return;
        try
        {
            var trees = await source.LoadTreesAsync(tile);
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
                    long key = Key((long)System.Math.Floor((tile.MinE + t.X) / Cell),
                        (long)System.Math.Floor((tile.MaxN - t.Z) / Cell));
                    if (!cells.TryGetValue(key, out var list)) cells[key] = list = new();
                    list.Add(new Trunk(t.X, t.Y, t.Z, r, Mathf.Max(t.Height, 1.5f)));
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

    private StaticBody3D Place(TileId tile, Trunk t)
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
        var at = _origin.ToWorld(tile.MinE + t.X, tile.MaxN - t.Z, t.Y);
        body.GlobalPosition = at + Vector3.Up * (t.Height * 0.5f - 0.3f);   // a little into the ground on a slope
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
