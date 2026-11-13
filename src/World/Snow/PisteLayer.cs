using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.World.Snow;

/// <summary>
/// Draws the ski slopes near the camera (#887): each tile with slopes within <see cref="Radius"/>
/// gets a layer of a <see cref="Texture2DArray"/> (2 m a texel: the slope, the edge mask and the
/// groom phase), told to its ground mesh through the instance uniform <c>piste_layer</c>, as
/// <c>Terrain/PhotoLayer</c> does the photos, and to its road mesh, so a forest road under a slope
/// is snowed over too; and the slopes' snow sits in a small state texture
/// that is rewritten only when the snow has moved on (<see cref="StateSeconds"/>), never per
/// frame. The terrain material stays the one every tile shares. shaders/body/piste_snow.gdshaderinc.
/// <para>
/// Under <c>--frame earth</c> (#1735) the tiles are the leaves round the camera, named by their stand-ins: a layer is
/// painted from the stand-in's slopes (<c>LeafStandIn.Pistes</c>) and told to the leaf's ground mesh and its furnished
/// roads. The leaves' ground material reads it at <c>piste_offset</c> from the mesh's anchor frame (the stand-in's square
/// less <see cref="LeafStandIn.Offset"/>), and a slope's altitudes go into the state as the leaf's own heights (over the
/// ellipsoid along the anchor's up), which is what the mesh carries as its local y.
/// </para>
/// </summary>
public partial class PisteLayer : Node
{
    private const int Radius = 4;
    private const int Capacity = 40;
    private const int Size = 512;
    private const int SlotsPerLayer = 256;
    /// <summary>How often the state is looked at: one in-game hour is a real minute at the default pace.</summary>
    private const double StateSeconds = 2;

    private readonly ChunkManager _chunks;
    private readonly Terrain.Earth.EarthWorld? _earth;
    private readonly WorldOrigin _origin;
    private readonly ShaderMaterial _terrain;
    private readonly ShaderMaterial? _roads;
    private readonly SnowService _snow;
    private readonly Texture2DArray _map = new();
    private readonly Image _stateImage;
    private readonly ImageTexture _stateTexture;
    private readonly byte[] _state = new byte[SlotsPerLayer * Capacity * 3 * 4];
    private readonly Dictionary<TileId, (int Layer, ulong Ground, ulong Roads, PisteTile Tile, float Shift)> _shown = new();
    private readonly HashSet<TileId> _building = new();
    private readonly Stack<int> _free = new();
    private readonly ConcurrentQueue<(TileId Id, PisteTile Tile, Image Image, float Shift)> _ready = new();
    private readonly List<TileId> _gone = new();

    public static readonly StringName PisteLayerParam = "piste_layer";
    private static readonly StringName UsePistes = "use_pistes";
    private static readonly StringName PisteMap = "piste_map";
    private static readonly StringName PisteState = "piste_state";
    private static readonly StringName PisteOffset = "piste_offset";

    private double _sincePoll = double.MaxValue, _sinceState = double.MaxValue;
    private int _stateVersion = -1;
    private double _stateHour = -1;
    private bool _stateDirty;

    /// <param name="earth">The earth world (#1735): its leaves are drawn instead of <paramref name="chunks"/>' tiles, and
    /// <paramref name="terrain"/> is its ground material.</param>
    public PisteLayer(ChunkManager chunks, WorldOrigin origin, ShaderMaterial terrain, ShaderMaterial? roads, SnowService snow,
        Terrain.Earth.EarthWorld? earth = null)
    {
        Name = "PisteLayer";
        _chunks = chunks;
        _earth = earth;
        _origin = origin;
        _terrain = terrain;
        _roads = roads;
        _snow = snow;
        var blank = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
        var layers = new Godot.Collections.Array<Image>();
        for (int i = 0; i < Capacity; i++) layers.Add(blank);
        _map.CreateFromImages(layers);
        for (int i = Capacity - 1; i >= 0; i--) _free.Push(i);
        _stateImage = Image.CreateFromData(SlotsPerLayer, Capacity * 3, false, Image.Format.Rgba8, _state);
        _stateTexture = ImageTexture.CreateFromImage(_stateImage);
        Apply();
    }

    /// <summary>Puts the textures on the terrain material (again after a restyle).</summary>
    public void Apply()
    {
        foreach (var m in new[] { _terrain, _roads })
        {
            m?.SetShaderParameter(PisteMap, _map);
            m?.SetShaderParameter(PisteState, _stateTexture);
            m?.SetShaderParameter(UsePistes, true);
        }
        // a leaf's ground mesh is in its anchor frame, the stand-in's square shifted (its furnished roads are in the stand-in's)
        if (_earth != null) _terrain.SetShaderParameter(PisteOffset, new Vector2((float)LeafStandIn.Offset, (float)LeafStandIn.Offset));
    }

    public override void _ExitTree()
    {
        _terrain.SetShaderParameter(UsePistes, false);
        _roads?.SetShaderParameter(UsePistes, false);
        foreach (var (id, _) in _shown) Tag(id, -1);
    }

    public override void _Process(double delta)
    {
        while (_ready.TryDequeue(out var r))
        {
            _building.Remove(r.Id);
            if (_free.Count == 0) continue;
            int layer = _free.Pop();
            _map.UpdateLayer(r.Image, layer);
            _shown[r.Id] = (layer, 0, 0, r.Tile, r.Shift);
            _stateDirty = true;
        }

        _sincePoll += delta;
        if (_sincePoll >= 0.5 && GetViewport()?.GetCamera3D() is { } cam)
        {
            _sincePoll = 0;
            Poll(_earth is { } earth ? earth.Ground.ContentName(earth.Ground.TileAt(cam.GlobalPosition)) : _origin.TileAt(cam.GlobalPosition));
        }

        _sinceState += delta;
        bool moved = _snow.Version != _stateVersion || Math.Abs(_snow.Hour - _stateHour) > 0.05;
        if (_stateDirty || (_sinceState >= StateSeconds && moved))
        {
            _sinceState = 0;
            _stateDirty = false;
            _stateVersion = _snow.Version;
            _stateHour = _snow.Hour;
            WriteState();
        }
    }

    private void Tag(TileId id, int layer)
    {
        GroundOf(id)?.SetInstanceShaderParameter(PisteLayerParam, layer);
        if (RoadsOf(id) is { } roads) TagRoads(roads, layer);
    }

    /// <summary>A tile's ground mesh: the flat streamer's, or an earth leaf's by its stand-in's name.</summary>
    private MeshInstance3D? GroundOf(TileId id) => _earth is { } earth
        ? LeafStandIn.TryLeaf(id, out var leaf) ? earth.Ground.GroundAt(GroundTile.FromLeaf(leaf)) : null
        : _chunks.GroundMeshAt(id);

    /// <summary>A tile's roads mesh: the flat streamer's, or the one an earth leaf is furnished with.</summary>
    private MeshInstance3D? RoadsOf(TileId id) => _earth is { } earth
        ? LeafStandIn.TryLeaf(id, out var leaf) ? earth.RoadsAt(leaf) : null
        : _chunks.RoadsAt(id);

    /// <summary>The roads mesh and its cells (<see cref="ChunkNode.SetRoadCells"/>).</summary>
    private static void TagRoads(MeshInstance3D roads, int layer)
    {
        roads.SetInstanceShaderParameter(PisteLayerParam, layer);
        foreach (var child in roads.GetChildren())
            if (child is MeshInstance3D cell) cell.SetInstanceShaderParameter(PisteLayerParam, layer);
    }

    private void Poll(TileId centre)
    {
        _gone.Clear();
        foreach (var (id, _) in _shown)
            if (LodPolicy.Distance(id, centre) > Radius) _gone.Add(id);
        foreach (var id in _gone)
        {
            Tag(id, -1);
            _free.Push(_shown[id].Layer);
            _shown.Remove(id);
        }
        // nearest first, so a full array keeps the slopes under the camera
        for (int ring = 0; ring <= Radius; ring++)
            for (int dz = ring; dz >= -ring; dz--)
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != ring) continue;
                    var id = centre.Neighbour(dx, dz);
                    if (_shown.TryGetValue(id, out var shown))
                    {
                        // a tile rebuilt or reloaded has new ground or road nodes: tell them its layer again
                        if (GroundOf(id) is { } ground && ground.GetInstanceId() != shown.Ground)
                        {
                            ground.SetInstanceShaderParameter(PisteLayerParam, shown.Layer);
                            _shown[id] = shown = shown with { Ground = ground.GetInstanceId() };
                        }
                        if (RoadsOf(id) is { } roads && roads.GetInstanceId() != shown.Roads)
                        {
                            TagRoads(roads, shown.Layer);
                            _shown[id] = shown with { Roads = roads.GetInstanceId() };
                        }
                        continue;
                    }
                    if (_building.Contains(id) || _free.Count <= _building.Count) continue;
                    // no ground drawn there (#1762: the earth world draws leaves only near the ground, none from an
                    // airliner at 10 km): nothing to paint, so its slopes are not asked for (an earth leaf's generate it)
                    if (GroundOf(id) == null) continue;
                    if (_snow.Index?.Tile(id) is not { } tile) continue; // none there, or still loading
                    _building.Add(id);
                    Task.Run(() => _ready.Enqueue((id, tile, Paint(tile), ShiftOf(id))));
                }
    }

    /// <summary>
    /// What a leaf's drawn heights add to an altitude (#1735): its mesh's local y is the height over the ellipsoid along
    /// its anchor's up, so the geoid's undulation there (~50 m); 0 on an LV95 tile, whose local y is the altitude. Any thread.
    /// </summary>
    private static float ShiftOf(TileId id)
    {
        if (!LeafStandIn.TryLeaf(id, out var leaf)) return 0;
        return (float)GlobalPos.FromEcef(leaf.Anchor.ToEcef(0, 0, 0)).WithAlt(0).InTile(id).Y;
    }

    /// <summary>The tile's layer: every other grid vertex, so texel (x, y) is the vertex (2x, 2y). Any thread.</summary>
    private static Image Paint(PisteTile tile)
    {
        var data = new byte[Size * Size * 4];
        int grid = PisteFormat.Size;
        for (int y = 0; y < Size; y++)
        {
            int row = Math.Min(grid - 1, y * 2);
            for (int x = 0; x < Size; x++)
            {
                int col = Math.Min(grid - 1, x * 2), i = row * grid + col, o = (y * Size + x) * 4;
                byte slot = tile.Slot[i];
                data[o] = slot;
                if (slot == 0) { data[o + 2] = 128; data[o + 3] = 128; continue; }
                ushort aux = tile.Aux[i];
                float a = PisteFormat.PhaseOf(aux) / PisteFormat.PhaseM * MathF.Tau;
                data[o + 1] = (byte)(aux >> 8);
                data[o + 2] = (byte)Math.Clamp((int)MathF.Round((MathF.Cos(a) * 0.5f + 0.5f) * 255), 0, 255);
                data[o + 3] = (byte)Math.Clamp((int)MathF.Round((MathF.Sin(a) * 0.5f + 0.5f) * 255), 0, 255);
            }
        }
        return Image.CreateFromData(Size, Size, false, Image.Format.Rgba8, data);
    }

    /// <summary>Every shown slope's snow at its low and high altitude, into the state texture.</summary>
    private void WriteState()
    {
        foreach (var (_, shown) in _shown)
        {
            var records = shown.Tile.Records;
            for (int k = 0; k < records.Count && k + 1 < SlotsPerLayer; k++)
            {
                var r = records[k];
                var low = _snow.StateAt(r, r.AltLow);
                var high = _snow.StateAt(r, r.AltHigh);
                int slot = k + 1;
                Put(shown.Layer * 3, slot, low.Fresh, low.Loose, low.Ice, low.Wet);
                Put(shown.Layer * 3 + 1, slot, high.Fresh, high.Loose, high.Ice, high.Wet);
                Put(shown.Layer * 3 + 2, slot, (low.Bumps + high.Bumps) * 0.5f, (r.AltLow + shown.Shift) / 4700f, (r.AltHigh + shown.Shift) / 4700f, 0);
            }
        }
        _stateImage.SetData(SlotsPerLayer, Capacity * 3, false, Image.Format.Rgba8, _state);
        _stateTexture.Update(_stateImage);
    }

    private void Put(int row, int slot, float r, float g, float b, float a)
    {
        int o = (row * SlotsPerLayer + slot) * 4;
        _state[o] = Byte(r);
        _state[o + 1] = Byte(g);
        _state[o + 2] = Byte(b);
        _state[o + 3] = Byte(a);
    }

    private static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255), 0, 255);
}
