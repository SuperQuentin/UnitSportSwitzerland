using Godot;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.Terrain.Earth;

/// <summary>
/// The debug menu's hooks on the leaves (#339 on <c>--frame earth</c>, #1743): what <see cref="ChunkManager"/>'s
/// <c>SetDebugHidden</c>, <c>FreezeRings</c>, <c>ListTiles</c> and <c>RebuildVisuals</c> are to the flat tiles.
/// Main thread.
/// </summary>
public sealed partial class EarthWorld
{
    /// <summary>Where a leaf the debug overlay outlines stands in the streaming.</summary>
    public enum LeafState
    {
        /// <summary>Asked for, on a worker or waiting to be added.</summary>
        Loading,

        /// <summary>Its ground drawn (and its heights answered), nothing stands on it (yet, or nothing to stand).</summary>
        Ground,

        /// <summary>Its ground drawn and its roads, buildings, trees or water committed.</summary>
        Furnished,
    }

    private TileLayers _debugHidden;
    private bool _hideReal, _hideFill;

    /// <summary>The tile layers the debug menu hid on every leaf; none in play.</summary>
    public TileLayers HiddenLayers => _debugHidden;

    /// <summary>
    /// The streaming stopped: no leaf is asked for or let go, wherever the camera goes, so it can leave and look at what
    /// is loaded from outside. Leaves already on a worker still land. The collision patches still follow the bodies.
    /// </summary>
    public bool Frozen { get; set; }

    /// <summary>A real leaf (the region's, whole or part) rather than one the fill made.</summary>
    public bool IsReal(CubeKey key) => Store.Has(key);

    /// <summary>
    /// Hides tile layers on every loaded leaf and every leaf added later: the ground is the leaf's own mesh, the rest
    /// its furnished node's (<see cref="ChunkNode.HideLayers"/>). <paramref name="real"/> and <paramref name="fill"/>
    /// hide whole leaves by where they come from.
    /// </summary>
    public void SetDebugHidden(TileLayers layers, bool real, bool fill)
    {
        _debugHidden = layers;
        _hideReal = real;
        _hideFill = fill;
        foreach (var leaf in _near.Values) ApplyDebugHidden(leaf);
    }

    private void ApplyDebugHidden(Near leaf)
    {
        bool shown = !(IsReal(leaf.Key) ? _hideReal : _hideFill);
        if (leaf.Mesh != null) leaf.Mesh.Visible = shown && (_debugHidden & TileLayers.Ground) == 0;
        if (leaf.Node != null)
        {
            leaf.Node.HideLayers(_debugHidden);
            leaf.Node.Visible = shown;
        }
    }

    /// <summary>Every leaf loaded or on its way, and where it stands, for the debug overlay.</summary>
    public void ListLeaves(List<(CubeKey Key, LeafState State)> into)
    {
        into.Clear();
        foreach (var (key, leaf) in _near) into.Add((key, leaf.Node != null ? LeafState.Furnished : LeafState.Ground));
        foreach (var key in _loading)
            if (!_near.ContainsKey(key)) into.Add((key, LeafState.Loading));
    }

    /// <summary>The state of a leaf, or null when it is neither loaded nor asked for.</summary>
    public LeafState? StateOf(CubeKey key) =>
        _near.TryGetValue(key, out var leaf) ? leaf.Node != null ? LeafState.Furnished : LeafState.Ground
        : _loading.Contains(key) ? LeafState.Loading : null;

    /// <summary>Leaves asked for and not added yet.</summary>
    public int LoadingCount => _loading.Count;

    /// <summary>
    /// Every loaded leaf built again, in place (the old one stays drawn until its new one lands): what the wireframe view
    /// needs, since Godot makes a mesh's wireframe only when it is created after the switch. Its heights and collision
    /// are read from the same preparation.
    /// </summary>
    public void RebuildVisuals()
    {
        foreach (var key in _near.Keys) _stale.Add(key);
        _nextPlan = 0;
        GD.Print($"[earth] rebuilding {_near.Count} leaves");
    }

    /// <summary>The road records of the loaded leaves within <paramref name="radiusM"/> of a world point, on their stand-in tiles.</summary>
    public void StandRoadsNear(Vector3 world, double radiusM, List<RoadTile> into)
    {
        into.Clear();
        var at = Origin.ToEcef(world);
        foreach (var key in _near.Keys)
            if (Distance(key, at) <= radiusM && StandOf(key)?.Roads is { } roads) into.Add(roads);
    }

    /// <summary>For <c>--debugcheck</c>: a loaded leaf's ground mesh (a real one, a fill one, or any), or null.</summary>
    internal MeshInstance3D? LeafMesh(bool? real = null)
    {
        foreach (var (key, leaf) in _near)
            if (leaf.Mesh != null && (real == null || IsReal(key) == real)) return leaf.Mesh;
        return null;
    }

    /// <summary>For <c>--debugcheck</c>: a loaded leaf's furnished node with roads drawn, or null.</summary>
    internal ChunkNode? LeafNodeWithRoads()
    {
        foreach (var leaf in _near.Values)
            if (leaf.Node is { RoadMesh: not null } node) return node;
        return null;
    }
}
