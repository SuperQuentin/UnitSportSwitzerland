using System.Collections.Generic;
using Godot;

namespace UnitSport.Terrain;

/// <summary>
/// PROTOTYPE (issue #181): the 3D trees around the camera, culled per tree on the CPU.
///
/// <para>
/// A tile's trees are one MultiMesh; a shader can hide the far ones but the GPU still processes
/// every vertex of every instance, which with ~2k-triangle realistic trees and up to 60k trees a
/// tile came to 360M primitives a frame. Here the tiles only draw billboards, and one MultiMesh
/// per kind holds just the trees within the 3D range (plus the crossfade), rebuilt from the tiles'
/// instance buffers whenever the camera has moved a few metres.
/// </para>
/// </summary>
public partial class NearTrees : Node3D
{
    private const int Capacity = 12000;
    private const int FloatsPerInstance = 16;

    private static readonly Dictionary<ChunkNode, ChunkNode.TreeBuffers> _tiles = new();
    public static void Register(ChunkNode tile, ChunkNode.TreeBuffers buffers) => _tiles[tile] = buffers;
    public static void Unregister(ChunkNode tile) => _tiles.Remove(tile);

    private readonly float _range;
    private readonly MultiMesh _conifers, _broadleaves;
    private readonly float[] _coniferBuf = new float[Capacity * FloatsPerInstance];
    private readonly float[] _broadleafBuf = new float[Capacity * FloatsPerInstance];
    private Vector3 _lastAt = new(float.MaxValue, 0, 0);
    private double _sinceUpdate;

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
        // world-space instances anywhere near the camera: never cull the node itself
        CustomAabb = new Aabb(new Vector3(-1e6f, -1e4f, -1e6f), new Vector3(2e6f, 2e4f, 2e6f)),
    };

    public override void _Process(double delta)
    {
        _sinceUpdate += delta;
        if (GetViewport()?.GetCamera3D() is not { } cam) return;
        var at = cam.GlobalPosition;
        if (_sinceUpdate < 0.2 || at.DistanceSquaredTo(_lastAt) < 16f) return;
        _sinceUpdate = 0;
        _lastAt = at;

        int nc = 0, nb = 0;
        float r = _range, r2 = r * r;
        foreach (var (tile, b) in _tiles)
        {
            if (!IsInstanceValid(tile) || !tile.IsInsideTree()) continue;
            var o = tile.GlobalPosition;
            // the tile square, and the camera's distance to it
            float dx = Mathf.Max(0, Mathf.Max(o.X - at.X, at.X - (o.X + 1000f)));
            float dz = Mathf.Max(0, Mathf.Max(o.Z - at.Z, at.Z - (o.Z + 1000f)));
            if (dx * dx + dz * dz > r2) continue;
            nc = Gather(b.Conifers, b.ConiferCount, o, at, r2, _coniferBuf, nc);
            nb = Gather(b.Broadleaves, b.BroadleafCount, o, at, r2, _broadleafBuf, nb);
        }
        _conifers.Buffer = _coniferBuf;
        _conifers.VisibleInstanceCount = nc;
        _broadleaves.Buffer = _broadleafBuf;
        _broadleaves.VisibleInstanceCount = nb;
    }

    private static int Gather(float[] src, int count, Vector3 o, Vector3 at, float r2, float[] dst, int n)
    {
        for (int i = 0; i < count && n < Capacity; i++)
        {
            int s = i * FloatsPerInstance;
            float x = src[s + 3] + o.X, y = src[s + 7] + o.Y, z = src[s + 11] + o.Z;
            float ddx = x - at.X, ddz = z - at.Z;
            if (ddx * ddx + ddz * ddz > r2) continue;
            int d = n * FloatsPerInstance;
            System.Array.Copy(src, s, dst, d, FloatsPerInstance);
            dst[d + 3] = x; dst[d + 7] = y; dst[d + 11] = z;
            n++;
        }
        return n;
    }
}
