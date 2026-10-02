using System.Collections.Generic;
using Godot;
using UnitSport.Core;

namespace UnitSport.Terrain;

/// <summary>
/// The 3D trees around the camera, culled per tree on the CPU, for styles whose trees are too
/// heavy to leave per tile (<see cref="Styles.MeshDetail.High"/>: Cartoon's ~50-120 triangles,
/// later the realistic ones' thousands).
///
/// <para>
/// A tile's trees are one MultiMesh: the shader can collapse the far ones, but the GPU still runs
/// every vertex of every instance, and with heavier trees and up to 60k a tile that is most of
/// the frame (the prototype, #181: Cartoon 66-90 ms per tile, 22-25 ms with this). So the tiles
/// draw only billboards and hand their instance buffers here; one MultiMesh per kind holds just
/// the trees within the 3D range and its crossfade, refilled whenever the camera has moved a few
/// metres. The tree shader's own LOD fade does the rest, against the billboards.
/// </para>
///
/// <para>
/// A child of the terrain (an <see cref="IOriginContainer"/>), so the floating origin moves it
/// with the tiles; instances are placed relative to it, and a shift refills them at once.
/// </para>
/// </summary>
public partial class NearTrees : Node3D, IOriginShiftAware
{
    /// <summary>Trees per kind at most: a dense forest within ~260 m holds ~10k.</summary>
    private const int Capacity = 16000;
    private const int FloatsPerInstance = 16;

    /// <summary>Tiles' instance buffers, registered when their trees commit. Main thread.</summary>
    private static readonly Dictionary<ChunkNode, ChunkNode.TreeBuffers> Tiles = new();

    public static void Register(ChunkNode tile, ChunkNode.TreeBuffers buffers)
    {
        Tiles[tile] = buffers;
        _tilesChanged = true;
    }

    public static void Unregister(ChunkNode tile) => _tilesChanged |= Tiles.Remove(tile);

    /// <summary>A tile's trees came or went: refill even if the camera has not moved.</summary>
    private static bool _tilesChanged;

    private readonly float _range;
    private readonly MultiMesh _conifers, _broadleaves;
    private readonly float[] _coniferBuf = new float[Capacity * FloatsPerInstance];
    private readonly float[] _broadleafBuf = new float[Capacity * FloatsPerInstance];
    private Vector3 _lastAt = new(float.MaxValue, 0, 0);
    private double _sinceUpdate;

    /// <param name="range">The 3D range plus its crossfade (<see cref="Styles.StyleKit.TreeNear"/>).</param>
    public NearTrees(Mesh conifer, Mesh broadleaf, float range)
    {
        Name = "NearTrees";
        _range = range;
        _conifers = Make(conifer);
        _broadleaves = Make(broadleaf);
        AddChild(new MultiMeshInstance3D { Name = "Conifers", Multimesh = _conifers });
        AddChild(new MultiMeshInstance3D { Name = "Broadleaves", Multimesh = _broadleaves });
    }

    private static MultiMesh Make(Mesh mesh) => new()
    {
        TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
        UseColors = true,
        Mesh = mesh,
        InstanceCount = Capacity,
        VisibleInstanceCount = 0,
        // instances anywhere around the camera: never cull the whole set
        CustomAabb = new Aabb(new Vector3(-1e6f, -1e4f, -1e6f), new Vector3(2e6f, 2e4f, 2e6f)),
    };

    public void OnOriginShifted(OriginShift shift) => _lastAt = new Vector3(float.MaxValue, 0, 0);

    /// <summary>
    /// The world is closing: the tiles' buffers go too. Not on this node's own exit: a restyle
    /// replaces it while the tiles stay registered.
    /// </summary>
    public static void Forget() => Tiles.Clear();

    public override void _Ready() => _tilesChanged = true;

    public override void _Process(double delta)
    {
        _sinceUpdate += delta;
        if (GetViewport()?.GetCamera3D() is not { } cam) return;
        var at = cam.GlobalPosition;
        bool forced = _lastAt.X == float.MaxValue;
        if (!forced && (_sinceUpdate < 0.2 || (at.DistanceSquaredTo(_lastAt) < 16f && !_tilesChanged))) return;
        _sinceUpdate = 0;
        _lastAt = at;
        _tilesChanged = false;

        int nc = 0, nb = 0;
        float r2 = _range * _range;
        var self = GlobalPosition;
        foreach (var (tile, b) in Tiles)
        {
            if (!IsInstanceValid(tile) || !tile.IsInsideTree() || !tile.IsVisibleInTree()) continue;
            var o = tile.GlobalPosition;
            // the camera's distance to the tile's square
            float dx = Mathf.Max(0, Mathf.Max(o.X - at.X, at.X - (o.X + 1000f)));
            float dz = Mathf.Max(0, Mathf.Max(o.Z - at.Z, at.Z - (o.Z + 1000f)));
            if (dx * dx + dz * dz > r2) continue;
            nc = Gather(b.Conifers, b.ConiferCount, o, self, at, r2, _coniferBuf, nc);
            nb = Gather(b.Broadleaves, b.BroadleafCount, o, self, at, r2, _broadleafBuf, nb);
        }
        _conifers.Buffer = _coniferBuf;
        _conifers.VisibleInstanceCount = nc;
        _broadleaves.Buffer = _broadleafBuf;
        _broadleaves.VisibleInstanceCount = nb;
    }

    /// <summary>Copies a tile's trees within range, placed relative to this node. Returns the new count.</summary>
    private static int Gather(float[] src, int count, Vector3 tile, Vector3 self, Vector3 at, float r2, float[] dst, int n)
    {
        for (int i = 0; i < count && n < Capacity; i++)
        {
            int s = i * FloatsPerInstance;
            float x = src[s + 3] + tile.X, y = src[s + 7] + tile.Y, z = src[s + 11] + tile.Z;
            float ddx = x - at.X, ddz = z - at.Z;
            if (ddx * ddx + ddz * ddz > r2) continue;
            int d = n * FloatsPerInstance;
            System.Array.Copy(src, s, dst, d, FloatsPerInstance);
            dst[d + 3] = x - self.X;
            dst[d + 7] = y - self.Y;
            dst[d + 11] = z - self.Z;
            n++;
        }
        return n;
    }
}
