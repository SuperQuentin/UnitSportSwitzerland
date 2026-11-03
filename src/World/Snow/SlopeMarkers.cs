using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Styles;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Snow;

/// <summary>
/// The poles along a ski slope's edges (#887), on the tiles near the camera (those
/// <see cref="ChunkManager.TileFurnished"/> dresses). One every <see cref="Spacing"/> m on both edges, just inside them,
/// of a line slope, and round an area's outline. The pole is painted in the slope's colour: blue,
/// red, black, or yellow for an itinerary. The pole on the right-hand edge going down also has an
/// orange top, as on Swiss slopes. Drawn only: no collision, a skier goes through them as through a twig.
/// One mesh per colour and side, one MultiMesh per tile for each.
/// </summary>
public partial class SlopeMarkers : Node
{
    private const string NodeName = "SlopeMarkers";
    public const float Spacing = 30f;
    public const float Height = 1.8f;
    /// <summary>How far outside the slope's edge a pole stands: a little inside it, in front of the trees that line the run.</summary>
    private const float Outside = -0.4f;

    private readonly ChunkManager _chunks;
    private readonly SnowService _snow;
    private readonly Dictionary<TileId, ChunkNode> _waiting = new();
    private readonly List<TileId> _done = new();
    private ShaderMaterial? _material;
    private double _since;

    private static readonly Color Orange = new(1f, 0.42f, 0.05f);
    private static readonly Dictionary<(int Level, bool Right), ArrayMesh> Meshes = new();

    public SlopeMarkers(ChunkManager chunks, SnowService snow)
    {
        Name = NodeName;
        _chunks = chunks;
        _snow = snow;
    }

    public override void _Ready()
    {
        _material = StyleKit.Material(MaterialRole.Prop);
        _material.SetShaderParameter("flicker", 0f);
        _chunks.TileFurnished += OnFurnished;
        _chunks.TileUnfurnished += OnUnfurnished;
    }

    public override void _ExitTree()
    {
        _chunks.TileFurnished -= OnFurnished;
        _chunks.TileUnfurnished -= OnUnfurnished;
    }

    private void OnFurnished(TileId id, ChunkNode node, Interiors.DoorSpot[] _) => _waiting[id] = node;

    private void OnUnfurnished(TileId id, ChunkNode node)
    {
        _waiting.Remove(id);
        node.GetNodeOrNull(NodeName)?.QueueFree();
    }

    /// <summary>A furnished tile waits here until its slopes have loaded (or turn out to be none).</summary>
    public override void _Process(double delta)
    {
        _since += delta;
        if (_since < 0.5 || _waiting.Count == 0 || _snow.Index is not { } index) return;
        _since = 0;
        _done.Clear();
        foreach (var (id, node) in _waiting)
        {
            if (!IsInstanceValid(node)) { _done.Add(id); continue; }
            if (!index.Loaded(id, out var tile)) continue;
            _done.Add(id);
            if (tile != null) Dress(node, tile);
        }
        foreach (var id in _done) _waiting.Remove(id);
    }

    private void Dress(ChunkNode node, PisteTile tile)
    {
        node.GetNodeOrNull(NodeName)?.QueueFree();
        var spots = new Dictionary<(int, bool), List<Transform3D>>();
        foreach (var r in tile.Records)
            if (r.Type is PisteType.Downhill or PisteType.Connection or PisteType.SnowPark)
                Place(node, tile, r, spots);
        if (spots.Count == 0) return;
        var root = new Node3D { Name = NodeName };
        foreach (var (key, list) in spots)
        {
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = MeshOf(key.Item1, key.Item2), InstanceCount = list.Count };
            for (int i = 0; i < list.Count; i++) mm.SetInstanceTransform(i, list[i]);
            root.AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = _material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
        node.AddChild(root);
    }

    /// <summary>The poles of one slope that stand in this tile, by colour and side.</summary>
    private void Place(ChunkNode node, PisteTile tile, PisteRecord r, Dictionary<(int, bool), List<Transform3D>> spots)
    {
        var path = r.Path;
        int n = path.Length / 2;
        if (n < 2) return;
        int level = Level(r.Difficulty);
        // the travel direction down the slope: whichever end of the path is lower
        float first = Ground(node, path[0], path[1]), last = Ground(node, path[^2], path[^1]);
        float down = float.IsNaN(first) || float.IsNaN(last) || first >= last ? 1f : -1f;
        float carry = 0;
        int segments = r.IsArea ? n : n - 1;
        for (int k = 0; k < segments; k++)
        {
            float ax = path[k * 2], ay = path[k * 2 + 1];
            int j = (k + 1) % n;
            float bx = path[j * 2], by = path[j * 2 + 1];
            float len = MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            if (len < 0.01f) continue;
            float tx = (bx - ax) / len, ty = (by - ay) / len;
            for (float s = carry; s < len; s += Spacing)
            {
                float px = ax + tx * s, py = ay + ty * s;
                if (r.IsArea)
                {
                    // round the outline, just outside it: the side away from the area's inside
                    float ox = ty, oy = -tx;
                    if (tile.At(tile.Id.MinE + px + ox * 2, tile.Id.MinN + py + oy * 2)?.Id == r.Id) { ox = -ox; oy = -oy; }
                    Add(node, tile, r, level, false, px + ox * Outside, py + oy * Outside, spots);
                }
                else
                {
                    float half = r.WidthM / 2 + Outside;
                    // left of the path is (-ty, tx); "right going down" flips with the travel direction
                    Add(node, tile, r, level, down < 0, px - ty * half, py + tx * half, spots);
                    Add(node, tile, r, level, down > 0, px + ty * half, py - tx * half, spots);
                }
            }
            carry = (carry - len) % Spacing;
            if (carry < 0) carry += Spacing;
        }
    }

    private void Add(ChunkNode node, PisteTile tile, PisteRecord r, int level, bool right, float east, float north,
        Dictionary<(int, bool), List<Transform3D>> spots)
    {
        // only the poles inside this tile (the path is kept whole in every tile it touches)
        if (east < 0 || east >= 1000 || north <= 0 || north > 1000) return;
        // not in the middle of another slope (two runs side by side, a junction)
        int i = tile.IndexAt(tile.Id.MinE + east, tile.Id.MinN + north);
        if (i >= 0 && tile.Slot[i] != 0 && tile.Records[tile.Slot[i] - 1].Id != r.Id && PisteFormat.MaskOf(tile.Aux[i]) > 0.5f) return;
        float y = Ground(node, east, north);
        if (float.IsNaN(y)) return;
        var key = (level, right);
        if (!spots.TryGetValue(key, out var list)) spots[key] = list = new List<Transform3D>();
        list.Add(new Transform3D(Basis.Identity, new Vector3(east, y, 1000 - north)));
    }

    /// <summary>The ground at a tile-local point (metres east and north of the SW corner), in the tile node's frame.</summary>
    private float Ground(ChunkNode node, float east, float north)
    {
        var world = node.ToGlobal(new Vector3(east, 0, 1000 - north));
        return _chunks.TryGetHeight(world, out float h) ? node.ToLocal(world with { Y = h }).Y : float.NaN;
    }

    /// <summary>0 blue, 1 red, 2 black, 3 yellow (itinerary).</summary>
    public static int Level(PisteDifficulty d) => d switch
    {
        PisteDifficulty.Intermediate or PisteDifficulty.Unknown => 1,
        PisteDifficulty.Advanced or PisteDifficulty.Expert => 2,
        PisteDifficulty.Freeride or PisteDifficulty.Extreme => 3,
        _ => 0,
    };

    private static readonly Color[] Paint =
    [
        new(0.10f, 0.32f, 0.85f), new(0.85f, 0.10f, 0.10f), new(0.06f, 0.06f, 0.07f), new(0.98f, 0.80f, 0.10f),
    ];

    /// <summary>A pole: an octagonal stake in the slope's colour, the right-hand one with an orange top. Cached.</summary>
    public static ArrayMesh MeshOf(int level, bool right)
    {
        if (Meshes.TryGetValue((level, right), out var cached)) return cached;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        const int sides = 8;
        const float radius = 0.06f, top = 0.35f;
        void Ring(float y0, float y1, Color c)
        {
            for (int s = 0; s < sides; s++)
            {
                float a0 = Mathf.Tau * s / sides, a1 = Mathf.Tau * (s + 1) / sides;
                var p0 = new Vector3(Mathf.Cos(a0) * radius, y0, Mathf.Sin(a0) * radius);
                var p1 = new Vector3(Mathf.Cos(a1) * radius, y0, Mathf.Sin(a1) * radius);
                var q0 = p0 with { Y = y1 };
                var q1 = p1 with { Y = y1 };
                st.SetColor(c with { A = 0 });
                st.AddVertex(p0); st.AddVertex(q1); st.AddVertex(q0);
                st.AddVertex(p0); st.AddVertex(p1); st.AddVertex(q1);
            }
        }
        Ring(-0.3f, Height - top, Paint[level]);
        Ring(Height - top, Height, right ? Orange : Paint[level]);
        // a cap, so the stake is not hollow seen from above
        for (int s = 0; s < sides; s++)
        {
            float a0 = Mathf.Tau * s / sides, a1 = Mathf.Tau * (s + 1) / sides;
            st.SetColor((right ? Orange : Paint[level]) with { A = 0 });
            st.AddVertex(new Vector3(0, Height, 0));
            st.AddVertex(new Vector3(Mathf.Cos(a1) * radius, Height, Mathf.Sin(a1) * radius));
            st.AddVertex(new Vector3(Mathf.Cos(a0) * radius, Height, Mathf.Sin(a0) * radius));
        }
        st.GenerateNormals();
        Core.ShowcaseTrace.Mark();
        var mesh = st.Commit();
        Meshes[(level, right)] = mesh;
        return mesh;
    }

    [Showcase("Ski", "Slope marker")]
    private static IEnumerable<(string Name, Func<Node3D> Make)> Showcase()
    {
        string[] names = ["blue", "red", "black", "itinerary"];
        for (int level = 0; level < 4; level++)
            foreach (bool right in new[] { false, true })
            {
                int l = level;
                bool r = right;
                yield return ($"{names[l]}{(r ? ", right-hand" : "")}", () => new MeshInstance3D { Mesh = MeshOf(l, r), MaterialOverride = StyleKit.Material(MaterialRole.Prop) });
            }
    }
}
