using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Styles;
using UnitSport.Terrain;

namespace UnitSport.World.Ropeways;

/// <summary>
/// The lifts' carriers round the camera (#912): the lines within <see cref="DrawRange"/> m, one
/// MultiMesh per part per line, each carrier where <see cref="RopewayMotion"/> puts it on the shared
/// clock. Lines within <see cref="NearRange"/> m are posed every frame, the others every tenth.
/// Nothing is allocated per frame: the instance transforms are written in place.
///
/// <para>
/// The carriers are placed in world space from LV95 every pose, so the layer itself stays at the
/// origin: the floating origin moves every node under the world by each shift (#185), which would
/// count it twice. Each line's culling box is redone on a shift.
/// </para>
/// </summary>
public partial class RopewayLayer : Node3D, IOriginShiftAware
{
    private const float DrawRange = 3000f, NearRange = 1200f;
    /// <summary>A T-bar's bar sits this high above the snow while it pulls (it is at the rider's hip).</summary>
    private const float BarFloor = 0f;

    private readonly RopewayService _service;
    private readonly ChunkManager _chunks;
    private readonly ShaderMaterial _material;
    private readonly Dictionary<long, Drawn> _drawn = new();
    private readonly List<long> _gone = new();
    private readonly HashSet<long> _want = new();
    private double _sincePoll = double.MaxValue;
    private int _rebuilt;
    private int _frame;

    private sealed class Drawn
    {
        public required RopewayLine Line;
        public required MultiMeshInstance3D Main;
        public MultiMeshInstance3D? Bar;
        /// <summary>The two stations (#915), placed once: they do not move.</summary>
        public required Node3D Stations;
        /// <summary>The carrier's scale: a chair as wide as its seats, a cable car's big cabin.</summary>
        public Vector3 Scale = Vector3.One;
        public bool Near;
    }

    public RopewayLayer(RopewayService service, ChunkManager chunks)
    {
        Name = "RopewayLayer";
        _service = service;
        _chunks = chunks;
        _material = StyleKit.Material(MaterialRole.Prop);
        _material.SetShaderParameter("flicker", 0f);
    }

    /// <summary>This client's ropeways: a bottom station is what E at it outlines (#1286).</summary>
    public static RopewayLayer? Instance { get; private set; }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>A line's bottom station shed, while it is drawn (its stations are drawn again after an origin shift).</summary>
    public Node3D? BottomStation(long line) =>
        _drawn.TryGetValue(line, out var d) && d.Stations.GetChildCount() > 0 ? d.Stations.GetChildOrNull<Node3D>(0) : null;

    public override void _Process(double delta)
    {
        if (_chunks.Origin is not { } origin || GetViewport()?.GetCamera3D() is not { } cam) return;
        _sincePoll += delta;
        if (_sincePoll >= 1.0)
        {
            _sincePoll = 0;
            Poll(origin, cam.GlobalPosition);
        }
        _frame++;
        Position = Vector3.Zero;
        double sim = RopewayService.Sim;
        foreach (var (_, d) in _drawn)
            if (d.Near || _frame % 10 == 0) Pose(d, origin, sim);
    }

    private void Poll(WorldOrigin origin, Vector3 camera)
    {
        var (e, n) = origin.ToLv95(camera);
        // the OSM details arrived and the lines were built again: drop the old ones
        if (_service.Rebuilt != _rebuilt)
        {
            _rebuilt = _service.Rebuilt;
            foreach (var (_, old) in _drawn) Free(old);
            _drawn.Clear();
        }
        _want.Clear();
        foreach (var line in _service.Near(e, n))
        {
            double nearest = double.MaxValue;
            for (int i = 0; i < line.E.Length; i++)
                nearest = Math.Min(nearest, (line.E[i] - e) * (line.E[i] - e) + (line.N[i] - n) * (line.N[i] - n));
            // between vertices a line may pass closer: a vertex within range plus the longest span is enough
            if (nearest > (DrawRange + 500) * (DrawRange + 500)) continue;
            _want.Add(line.Key);
            if (!_drawn.TryGetValue(line.Key, out var d)) _drawn[line.Key] = d = Make(line);
            d.Near = nearest < NearRange * NearRange;
        }
        _gone.Clear();
        foreach (var (key, d) in _drawn)
            if (!_want.Contains(key)) _gone.Add(key);
        foreach (var key in _gone)
        {
            Free(_drawn[key]);
            _drawn.Remove(key);
        }
    }

    private static void Free(Drawn d)
    {
        d.Main.QueueFree();
        d.Bar?.QueueFree();
        d.Stations.QueueFree();
    }

    private Drawn Make(RopewayLine line)
    {
        int count = RopewayMotion.Count(line);
        MultiMeshInstance3D Instance(RopewayMeshBuilder.Part part)
        {
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = RopewayMeshBuilder.Mesh(part), InstanceCount = count };
            // the carriers move every frame: a box round the whole line, or the renderer culls by the stale one
            mm.CustomAabb = Bounds(line);
            var node = new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = _material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(node);
            return node;
        }
        var main = Instance(line.Kind switch
        {
            RopewayKind.Chair => RopewayMeshBuilder.Part.Chair,
            RopewayKind.Gondola or RopewayKind.CableCar => RopewayMeshBuilder.Part.Cabin,
            RopewayKind.TBar => RopewayMeshBuilder.Part.Rod,
            _ => RopewayMeshBuilder.Part.Handle,
        });
        var bar = line.Kind == RopewayKind.TBar ? Instance(RopewayMeshBuilder.Part.Bar) : null;
        var scale = line.Kind switch
        {
            RopewayKind.Chair => new Vector3(Mathf.Clamp(line.Seats, 1, 8) / 4f, 1, 1),
            // the cabin floor is 3.22 m below its grip: 1.4 high puts it at the cable car hang of 4.5 m
            RopewayKind.CableCar => new Vector3(2.2f, 1.4f, 2.2f),
            _ => Vector3.One,
        };
        var stations = new Node3D { Name = $"Stations_{line.Key}" };
        AddChild(stations);
        PlaceStations(line, stations);
        return new Drawn { Line = line, Main = main, Bar = bar, Stations = stations, Scale = scale };
    }

    /// <summary>
    /// An open shed at each end (#915): posts outside both tracks, a roof over the bull wheel
    /// housing, the bottom one facing up the line and the top one down it. Drawn only: a skier
    /// glides between the posts.
    /// </summary>
    private void PlaceStations(RopewayLine line, Node3D into)
    {
        if (_chunks.Origin is not { } origin) return;
        bool drag = line.Kind is RopewayKind.TBar or RopewayKind.RopeTow;
        float width = line.Side + (drag ? 3f : 5f);
        float length = line.Kind == RopewayKind.CableCar ? 14f : drag ? 4f : 9f;
        foreach (bool top in new[] { false, true })
        {
            var (e, n, alt, de, dn) = line.At(top ? line.Length : 0f);
            // the side track runs to the left of the way up: the shed is centred between the two
            double ce = e - dn * line.Side * 0.5, cn = n + de * line.Side * 0.5;
            var at = origin.ToWorld(ce, cn, alt);
            float ground = _chunks.TryGetHeight(at, out float g) ? g : alt - 4f;
            float roof = Mathf.Max(alt + 1.2f, ground + 3f) - ground;
            float yaw = Mathf.Atan2(top ? de : -de, top ? -dn : dn);
            into.AddChild(new MeshInstance3D
            {
                Mesh = RopewayMeshBuilder.Station(width, length, roof),
                MaterialOverride = _material,
                Transform = new Transform3D(new Basis(Vector3.Up, yaw), at with { Y = ground }),
            });
        }
    }

    public void OnOriginShifted(OriginShift shift)
    {
        Position = Vector3.Zero;
        foreach (var (_, d) in _drawn)
        {
            var box = Bounds(d.Line);
            d.Main.Multimesh.CustomAabb = box;
            if (d.Bar != null) d.Bar.Multimesh.CustomAabb = box;
            d.Near = true;   // posed this frame, whatever its range
            // the stations were placed in world space: place them again
            foreach (var child in d.Stations.GetChildren()) child.QueueFree();
            PlaceStations(d.Line, d.Stations);
        }
    }

    /// <summary>The line's box in world space, generous: the carriers hang below it and the down side runs beside it.</summary>
    private Aabb Bounds(RopewayLine line)
    {
        var origin = _chunks.Origin!;
        var box = new Aabb(origin.ToWorld(line.E[0], line.N[0], line.Alt[0]), Vector3.Zero);
        for (int i = 1; i < line.E.Length; i++) box = box.Expand(origin.ToWorld(line.E[i], line.N[i], line.Alt[i]));
        return box.Grow(15f);
    }

    private void Pose(Drawn d, WorldOrigin origin, double sim)
    {
        var line = d.Line;
        var main = d.Main.Multimesh;
        int count = main.InstanceCount;
        for (int i = 0; i < count; i++)
        {
            var (e, n, alt, de, dn, up) = RopewayMotion.Place(line, i, sim);
            var at = origin.ToWorld(e, n, alt);
            var basis = new Basis(Vector3.Up, Mathf.Atan2(-de, dn)).Scaled(d.Scale);
            if (d.Bar is { } barNode)
            {
                // a T-bar: the rod reaches down to the rider's hip on the way up, wound in on the way down
                float ground = _chunks.TryGetHeight(at, out float h) ? h : at.Y - 6f;
                float reach = up ? Mathf.Max(0.5f, at.Y - (ground + BarFloor) - 0.75f) : 0.8f;
                main.SetInstanceTransform(i, new Transform3D(basis.Scaled(new Vector3(1, reach, 1)), at));
                barNode.Multimesh.SetInstanceTransform(i, new Transform3D(basis, at - new Vector3(0, reach + 0.75f, 0)));
            }
            else main.SetInstanceTransform(i, new Transform3D(basis, at));
        }
    }
}
