using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// The dedicated server's ground on another body (#1852, C3b of docs/plans/planets.md section 10): the terrain of a server
/// region whose frame is on the Moon (any landable body), what its bots stand on. Its heights are the clients'
/// (<see cref="BodyGround.HeightsOf"/>: the pyramid of <c>--bodies</c>, the generated craters, the sites' stamps), its solid
/// squares the clients' near square (<see cref="BodyGround.NearGrid"/>: 512 m of 2 m cells), one round each collision
/// anchor, built on a worker when an anchor is a move distance from every square's centre, dropped when no anchor is on it.
/// Heights are answered anywhere (they are a function of the place), so a bot is put down before its square is in.
/// Nothing is drawn; nothing of the Earth's (cover, water, tunnels, buildings) is there.
/// </summary>
public partial class ServerBodyGround : Node, IWorldGround
{
    private readonly BodyHeights _heights;
    private readonly WorldOrigin _origin;
    private readonly CelestialBody _body;
    private readonly List<Node3D> _anchors = new();
    private readonly HashSet<Node3D> _solid = new();
    private readonly List<Square> _squares = new();
    // the squares being built, with the place each was asked for (an anchor there is not given a second one meanwhile)
    private readonly List<(Task<(EnuFrame Frame, float[] Y)> Task, EcefPos At)> _building = new();

    private sealed class Square(StaticBody3D node, EnuFrame frame)
    {
        public readonly StaticBody3D Node = node;
        public readonly EnuFrame Frame = frame;
    }

    /// <summary>How many squares were built (a check waits for them).</summary>
    public int Built { get; private set; }

    public ServerBodyGround(CelestialBody body, BodyHeights heights, WorldOrigin origin)
    {
        _body = body;
        _heights = heights;
        _origin = origin;
        Name = $"{body.Name}Ground";
    }

    public WorldOrigin? Origin => _origin;

    public IChunkSource? Content => null;

    public IReadOnlyList<Node3D> Anchors => _anchors;

    // the Earth's tile events: there are no tiles here
    public event System.Action<GroundTile>? TileEntered { add { } remove { } }
    public event System.Action<GroundTile>? TileUnloaded { add { } remove { } }
    public event System.Action<GroundTile, ChunkNode, Interiors.DoorSpot[]>? TileFurnished { add { } remove { } }
    public event System.Action<GroundTile, ChunkNode>? TileUnfurnished { add { } remove { } }
    public event System.Action<System.Func<GroundTile, bool>?>? TerrainReplaced { add { } remove { } }

    private FrameAxes Axes => _origin.Frame.Axes!;

    /// <summary>The ground's world Y under a world point, along the frame's up: the body's heights there.</summary>
    public bool TryGetHeight(Vector3 worldPos, out float height)
    {
        var n = _heights.NormalUnder(Axes.ToEcef(worldPos.X, worldPos.Y, worldPos.Z));
        var (_, y, _) = Axes.FromEcef(_heights.Ground(n));
        height = (float)y;
        return true;
    }

    public bool TryGetSurface(Vector3 worldPos, out float height) => TryGetHeight(worldPos, out height);

    public bool TryGetCover(Vector3 worldPos, out CoverClass cover)
    {
        cover = default;
        return false;
    }

    public bool TryGetWaterLevel(Vector3 worldPos, out float stillLevel)
    {
        stillLevel = 0;
        return false;
    }

    public bool TryGetWater(Vector3 worldPos, out float stillLevel, out float waveScale)
    {
        stillLevel = waveScale = 0;
        return false;
    }

    public bool InTunnel(Vector3 worldPos) => false;

    public bool InPit(Vector3 worldPos) => false;

    public bool FloorBelow(Node3D body, Vector3 worldPos, Rid self, float depth = 4f) =>
        HasCollisionAt(worldPos) && TryGetHeight(worldPos, out float h) && worldPos.Y - h <= depth;

    /// <summary>A built square holds the true ground there (inside its eased band).</summary>
    public bool HasCollisionAt(Vector3 worldPos)
    {
        var p = Axes.ToEcef(worldPos.X, worldPos.Y, worldPos.Z);
        foreach (var s in _squares)
            if (Inside(s.Frame, p, BodyGround.TrueHalfM)) return true;
        return false;
    }

    public bool BuildingCollisionDoneAt(Vector3 worldPos) => true;

    public GroundTile TileAt(Vector3 worldPos) => default;

    public MeshInstance3D? GroundAt(GroundTile tile) => null;

    public void AddAnchor(Node3D anchor, bool? collision = null)
    {
        if (!_anchors.Contains(anchor)) _anchors.Add(anchor);
        if (collision != false) _solid.Add(anchor);
        else _solid.Remove(anchor);
    }

    public void RemoveAnchor(Node3D anchor)
    {
        _anchors.Remove(anchor);
        _solid.Remove(anchor);
    }

    public bool HasAnchor(Node3D anchor) => _anchors.Contains(anchor);

    private static bool Inside(EnuFrame f, EcefPos p, double half)
    {
        var (x, _, z) = f.ToLocal(p);
        return System.Math.Abs(x) < half && System.Math.Abs(z) < half;
    }

    public override void _PhysicsProcess(double delta)
    {
        // the squares built on workers, in
        for (int i = _building.Count - 1; i >= 0; i--)
        {
            var (t, _) = _building[i];
            if (!t.IsCompleted) continue;
            _building.RemoveAt(i);
            if (t.IsCompletedSuccessfully) Commit(t.Result.Frame, t.Result.Y);
            else GD.PrintErr($"[bodies] the server's {_body.Name} ground: {t.Exception?.GetBaseException().Message}");
        }
        _anchors.RemoveAll(a => !IsInstanceValid(a));
        _solid.RemoveWhere(a => !IsInstanceValid(a));

        // a square for each solid anchor a move distance from every square's centre (and from those being built)
        foreach (var a in _solid)
        {
            if (!a.IsInsideTree()) continue;
            var p = Axes.ToEcef(a.GlobalPosition.X, a.GlobalPosition.Y, a.GlobalPosition.Z);
            if (Near(p)) continue;
            var heights = _heights;
            _building.Add((Task.Run(() => BodyGround.NearGrid(heights, p)), p));
        }

        // a square no solid anchor stands on any more goes (a body passing back keeps it while it is on it)
        for (int i = _squares.Count - 1; i >= 0; i--)
        {
            var s = _squares[i];
            bool used = false;
            foreach (var a in _solid)
                if (a.IsInsideTree() && Inside(s.Frame, Axes.ToEcef(a.GlobalPosition.X, a.GlobalPosition.Y, a.GlobalPosition.Z), BodyGround.TrueHalfM))
                {
                    used = true;
                    break;
                }
            if (used) continue;
            s.Node.QueueFree();
            _squares.RemoveAt(i);
        }
    }

    /// <summary>A square (built or being built) has its centre within the near square's move distance of <paramref name="p"/>.</summary>
    private bool Near(EcefPos p)
    {
        foreach (var s in _squares)
            if (Inside(s.Frame, p, BodyGround.MoveM)) return true;
        foreach (var (_, q) in _building)
        {
            double dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
            if (dx * dx + dy * dy + dz * dz < BodyGround.MoveM * BodyGround.MoveM) return true;
        }
        return false;
    }

    private void Commit(EnuFrame frame, float[] y)
    {
        int side = BodyGround.Cells + 1;
        var node = new StaticBody3D { Name = $"Square{Built}" };
        node.AddChild(new CollisionShape3D
        {
            Shape = new HeightMapShape3D { MapWidth = side, MapDepth = side, MapData = y },
            Scale = new Vector3(BodyGround.CellM, 1f, BodyGround.CellM),
        });
        AddChild(node);
        // from the square's frame into the region's, in doubles (the region's frame never moves)
        var axes = Axes;
        Vector3 Turn(EcefPos v) => new((float)FrameAxes.Dot(v, axes.X), (float)FrameAxes.Dot(v, axes.Y), (float)FrameAxes.Dot(v, axes.Z));
        var (ox, oy, oz) = axes.FromEcef(frame.Origin);
        var south = new EcefPos(-frame.North.X, -frame.North.Y, -frame.North.Z);
        node.GlobalTransform = new Transform3D(new Basis(Turn(frame.East), Turn(frame.Up), Turn(south)), new Vector3((float)ox, (float)oy, (float)oz));
        _squares.Add(new Square(node, frame));
        Built++;
        GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[bodies] the server's {_body.Name} ground: square {Built} at {_body.Shape.ToGeodetic(frame.Origin).H:F0} m over the reference ({_squares.Count} in place)"));
    }
}
