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
///
/// <para>
/// Zoomed (#1433), the trees inside the view cone are gathered out to the range times the zoom
/// step and a step more (<see cref="Styles.LodScale"/>: the shader takes the new step a frame
/// before this refills), the rest out to the range; a step change or a turn of the camera
/// refills. A cone that holds more trees than the buffers grows them.
/// </para>
/// </summary>
public partial class NearTrees : Node3D, IOriginShiftAware
{
    /// <summary>Trees per kind at first: a dense forest within ~260 m holds ~10k. Grown while zoomed.</summary>
    private const int StartCapacity = 16000;

    /// <summary>Trees per kind at most, however far a zoomed cone reaches.</summary>
    private const int MaxCapacity = 65536;
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
    private int _capacity = StartCapacity;
    private float[] _coniferBuf = new float[StartCapacity * FloatsPerInstance];
    private float[] _broadleafBuf = new float[StartCapacity * FloatsPerInstance];
    private Vector3 _lastAt = new(float.MaxValue, 0, 0);
    private Vector3 _lastDir;
    private int _lastZoom;
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
        InstanceCount = StartCapacity,
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
        int zoom = Styles.LodScale.StepIndex;
        var dir = Styles.LodScale.ViewDirection;
        bool forced = _lastAt.X == float.MaxValue || zoom != _lastZoom;
        // zoomed, the far trees are only gathered in the cone: a turn moves it
        bool turned = zoom > 0 && dir.Dot(_lastDir) < 0.996f;   // ~5 degrees
        if (!forced && (_sinceUpdate < 0.2 || (at.DistanceSquaredTo(_lastAt) < 16f && !_tilesChanged && !turned))) return;
        _sinceUpdate = 0;
        _lastAt = at;
        _lastDir = dir;
        _lastZoom = zoom;
        _tilesChanged = false;

        var cone = new Cone(_range, zoom > 0 ? Styles.LodScale.Step * Styles.LodScale.StepRatio : 1f, at, dir, Styles.LodScale.ConeHalfDeg);
        int nc = 0, nb = 0;
        var self = GlobalPosition;
        foreach (var (tile, b) in Tiles)
        {
            if (!IsInstanceValid(tile) || !tile.IsInsideTree() || !tile.IsVisibleInTree()) continue;
            var o = tile.GlobalPosition;
            // the camera's distance to the tile's square
            float dx = Mathf.Max(0, Mathf.Max(o.X - at.X, at.X - (o.X + 1000f)));
            float dz = Mathf.Max(0, Mathf.Max(o.Z - at.Z, at.Z - (o.Z + 1000f)));
            float d2 = dx * dx + dz * dz;
            if (d2 > cone.Near2 && (d2 > cone.Far2 || !Styles.LodScale.InCone(o + TileCentre, TileHalfDiagonal))) continue;
            // an earth world's leaf (#1510) is turned a little in the world: its trees through its whole transform
            var basis = tile.GlobalBasis;
            Basis? turn = basis.IsEqualApprox(Basis.Identity) ? null : basis;
            nc = Gather(b.Conifers, b.ConiferCount, o, turn, self, cone, _coniferBuf, nc, _capacity);
            nb = Gather(b.Broadleaves, b.BroadleafCount, o, turn, self, cone, _broadleafBuf, nb, _capacity);
        }
        // a zoomed cone over a forest holds more than the buffers: grow them, and refill next frame
        if ((nc >= _capacity || nb >= _capacity) && _capacity < MaxCapacity)
        {
            _capacity = Mathf.Min(MaxCapacity, _capacity * 2);
            _coniferBuf = new float[_capacity * FloatsPerInstance];
            _broadleafBuf = new float[_capacity * FloatsPerInstance];
            _conifers.InstanceCount = _capacity;
            _broadleaves.InstanceCount = _capacity;
            _lastAt = new Vector3(float.MaxValue, 0, 0);
            return;
        }
        _conifers.Buffer = _coniferBuf;
        _conifers.VisibleInstanceCount = nc;
        _broadleaves.Buffer = _broadleafBuf;
        _broadleaves.VisibleInstanceCount = nb;
    }

    private static readonly Vector3 TileCentre = new(500f, 0f, 500f);
    private const float TileHalfDiagonal = 708f;

    /// <summary>
    /// Which trees are gathered: all within the range, and those inside the horizontal view cone
    /// within the range times the zoom.
    /// </summary>
    private readonly struct Cone
    {
        public readonly float Near2, Far2, CosHalf;
        public readonly Vector3 At;
        public readonly float DirX, DirZ;
        public readonly bool All;

        public Cone(float range, float zoom, Vector3 at, Vector3 dir, float halfDeg)
        {
            Near2 = range * range;
            Far2 = Near2 * zoom * zoom;
            At = at;
            float l = Mathf.Sqrt(dir.X * dir.X + dir.Z * dir.Z);
            // nearly straight down or up (or no cone): every heading counts
            All = l < 0.2f || halfDeg >= 180f;
            DirX = l > 0f ? dir.X / l : 0f;
            DirZ = l > 0f ? dir.Z / l : 0f;
            CosHalf = Mathf.Cos(Mathf.DegToRad(Mathf.Min(halfDeg, 180f)));
        }

        public bool Takes(float dx, float dz)
        {
            float d2 = dx * dx + dz * dz;
            if (d2 <= Near2) return true;
            if (d2 > Far2) return false;
            if (All) return true;
            // the angle off the heading within the half angle: dot >= |d| cos(half), squared with the signs kept
            float dot = dx * DirX + dz * DirZ;
            return CosHalf >= 0f
                ? dot >= 0f && dot * dot >= d2 * CosHalf * CosHalf
                : dot >= 0f || dot * dot <= d2 * CosHalf * CosHalf;
        }
    }

    /// <summary>Copies a tile's trees within reach, placed relative to this node. Returns the new count.</summary>
    private static int Gather(float[] src, int count, Vector3 tile, Basis? turn, Vector3 self, in Cone cone, float[] dst, int n, int capacity)
    {
        var at = cone.At;
        for (int i = 0; i < count && n < capacity; i++)
        {
            int s = i * FloatsPerInstance;
            float x, y, z;
            if (turn is { } t)
            {
                var p = tile + t * new Vector3(src[s + 3], src[s + 7], src[s + 11]);
                (x, y, z) = (p.X, p.Y, p.Z);
            }
            else (x, y, z) = (src[s + 3] + tile.X, src[s + 7] + tile.Y, src[s + 11] + tile.Z);
            if (!cone.Takes(x - at.X, z - at.Z)) continue;
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
