using System.Collections.Concurrent;
using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.World;

/// <summary>
/// The earth world's far leaves (#1629, G2 C6): between the leaves <c>EarthWorld</c> draws whole (~900 m) and the flat
/// world's ring radius, the region's real leaves are the globe's last level, as the flat world's real tiles are its rings:
/// each painted by its own cover, its trees as billboards (a share of them, as the flat far rings thin theirs), and
/// within the flat world's building rings its buildings as boxes (<see cref="BuildingMeshBuilder.BuildProxies"/>, what
/// the flat world draws past 1 km). Everything is built on a worker in the leaf's anchor frame, under the tile's node.
/// </summary>
public partial class Globe
{
    /// <summary>Real leaves nearer than this are drawn at the leaves' level, furnished: the flat world's ring radius, metres (0: off).</summary>
    public double FarLeafM { get; set; }

    /// <summary>Far leaves nearer than this draw their buildings as boxes: the flat world's building rings, metres.</summary>
    public double FarBuildingsM { get; set; }

    /// <summary>The share of a far leaf's trees drawn: the flat world's far rings' (<see cref="LodPolicy.TreeDensity"/>).</summary>
    public float FarTreeShare { get; set; } = 0.25f;

    /// <summary>Lattice vertices between a far leaf's drawn ones: 32 cells of 19 m (the flat rings' 20 m at 6-9 km).</summary>
    public const int LeafStride = 16;

    /// <summary>The world's terrain material the tiles take near the ground (<see cref="TerrainBelowM"/>); one of the kit's when unset.</summary>
    public Material? TerrainMaterial { get; set; }

    /// <summary>The world's building and tree materials (the flat streamer's), for the far leaves' boxes and trees.</summary>
    public Material? BuildingMaterial { get; set; }

    public Material? TreeMaterial { get; set; }

    /// <summary>The world's water material (the flat streamer's), for the lakes past the near leaves (#1669).</summary>
    public Material? WaterMaterial { get; set; }

    // each tile's lakes, shown with the terrain material (below TerrainBelowM)
    private readonly Dictionary<CubeKey, MeshInstance3D> _lakes = new();

    private HashSet<CubeKey>? _realAncestors;
    private readonly Dictionary<CubeKey, double> _distance = new();
    private readonly Dictionary<CubeKey, MeshInstance3D> _buildings = new();
    private readonly HashSet<CubeKey> _buildingsLoading = new();
    private readonly ConcurrentQueue<(CubeKey Key, ArrayMesh? Mesh)> _buildingsReady = new();

    private LeafStore? RealLeaves => (Leaves as EarthLeaves)?.Real;

    /// <summary>Whether real leaves lie under <paramref name="key"/>: every ancestor of the region's leaves, once.</summary>
    private bool HoldsReal(CubeKey key)
    {
        if (_realAncestors == null)
        {
            _realAncestors = new HashSet<CubeKey>();
            if (RealLeaves is { } real)
                foreach (var leaf in real.LocalKeys)
                    for (var k = leaf; k.Level > 0 && _realAncestors.Add(k.Parent);) k = k.Parent;
        }
        return _realAncestors.Contains(key);
    }

    /// <summary>Whether <paramref name="key"/> is drawn as a far leaf: a real leaf on this copy's disk (never fetched from the server for the far band), the band on.</summary>
    private bool IsFarLeaf(CubeKey key) =>
        Earth && FarLeafM > 0 && key.Level == LeafFrames.LeafLevel && RealLeaves is { } real && real.IsLocal(key);

    /// <summary>A far leaf's mesh and trees, on a worker: heights, water, cover and trees read alone.</summary>
    private (Godot.Collections.Array Arrays, ChunkNode.TreeMeshes? Trees, WaterMeshBuilder.MeshData? Water)? BuildFarLeaf(CubeKey key, LeafStore real)
    {
        if (real.Sections(key, LeafKind.Heights, LeafKind.Water, LeafKind.Cover, LeafKind.Trees) is not { Heights: { } heights } content)
            return null;
        var lakes = new WaterOut();
        // a leaf with no cover (heights at the build's edge only: 15 of B4's) is painted from its ancestor's palette as a
        // globe tile is; with neither it had no vertex colours and drew a white square (#1746)
        var (palette, rect) = content.Cover == null ? AncestorPalette(key) : (null, default);
        // no ancestor's palette either (not loaded yet): open ground by its altitude, as the near leaf paints it
        var cover = content.Cover ?? (palette == null ? new byte[((1 << heights.Bits) + 1) * ((1 << heights.Bits) + 1)] : null);
        if (Build(heights, palette, rect, content.Water, LeafStride, cover, AncestorTile, lakes) is not { } arrays) return null;
        ChunkNode.TreeMeshes? trees = null;
        if (content.Trees is { Count: > 0 } all && TreeMaterial is { } treeMaterial)
        {
            // an even share, the same trees every time (the flat world draws a prefix of shuffled instances)
            var kept = new List<TreeInstance>(all.Count / 3);
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < all.Count; i++)
            {
                if ((uint)(i * 2654435761u) % 1000u >= FarTreeShare * 1000) continue;
                var t = all[i];
                kept.Add(t);
                minY = Math.Min(minY, t.Y);
                maxY = Math.Max(maxY, t.Y + t.Height);
            }
            if (kept.Count > 0)
            {
                const float half = 500;   // a leaf is ~610 m across round its anchor
                var bounds = new Aabb(new Vector3(-half, minY - 10, -half), new Vector3(2 * half, maxY - minY + 20, 2 * half));
                trees = ChunkNode.BuildTreeMeshes(ChunkNode.BuildTreeBuffers(kept), treeMaterial, bounds, Styles.StyleKit.Detail);
            }
        }
        return (arrays, trees, lakes.Mesh);
    }

    /// <summary>A far leaf's trees under its tile's node: the billboards (all 3D where the style has none), no shadow.</summary>
    private static void AddTrees(MeshInstance3D tile, ChunkNode.TreeMeshes trees)
    {
        bool billboards = trees.ConifersFar != null || trees.BroadleavesFar != null;
        foreach (var multi in billboards ? new[] { trees.ConifersFar, trees.BroadleavesFar } : new[] { trees.Conifers, trees.Broadleaves })
            if (multi != null)
                tile.AddChild(new MultiMeshInstance3D { Multimesh = multi, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    /// <summary>
    /// The far leaves' buildings by distance, after each plan: asked for a drawn far leaf within
    /// <see cref="FarBuildingsM"/>, dropped once it is no longer drawn or 500 m past it.
    /// </summary>
    private void PlanBuildings()
    {
        if (!Earth || FarBuildingsM <= 0 || BuildingMaterial is not { } material || RealLeaves is not { } real) return;
        List<CubeKey>? drop = null;
        foreach (var (key, node) in _buildings)
            if (!_want.Contains(key) || _distance.GetValueOrDefault(key, double.MaxValue) > FarBuildingsM + 500)
                (drop ??= new()).Add(key);
        if (drop != null)
            foreach (var key in drop)
            {
                _buildings[key].QueueFree();
                _buildings.Remove(key);
            }

        foreach (var key in _want)
        {
            if (key.Level != LeafFrames.LeafLevel || _buildings.ContainsKey(key) || _buildingsLoading.Contains(key)) continue;
            if (_distance.GetValueOrDefault(key, double.MaxValue) > FarBuildingsM || !real.IsLocal(key)) continue;
            _buildingsLoading.Add(key);
            _ = Spawn(async () =>
            {
                await _workers.WaitAsync().ConfigureAwait(false);
                try
                {
                    ArrayMesh? mesh = null;
                    if (real.Sections(key, LeafKind.Buildings) is { Buildings: { Buildings.Count: > 0 } b }
                        && BuildingMeshBuilder.BuildProxies(new BuildingTile { Id = LeafStandIn.Id(key), Buildings = b.Buildings }) is { } data)
                        mesh = ChunkNode.ToArrayMesh(data, material);
                    _buildingsReady.Enqueue((key, mesh));
                }
                catch (Exception e)
                {
                    GD.PushError($"[globe] buildings {key}: {e.Message}");
                    _buildingsReady.Enqueue((key, null));
                }
                finally
                {
                    Done();
                }
            });
        }
    }

    /// <summary>A far leaf's boxes under its tile's node (an empty one where it has none, so it is not asked again).</summary>
    private void CommitBuildings(CubeKey key, ArrayMesh? mesh)
    {
        _buildingsLoading.Remove(key);
        _quiet = false;
        if (!_patches.TryGetValue(key, out var tile) || _buildings.ContainsKey(key)) return;
        var node = new MeshInstance3D { Name = "Buildings", Mesh = mesh };
        tile.AddChild(node);
        _buildings[key] = node;
    }
}
