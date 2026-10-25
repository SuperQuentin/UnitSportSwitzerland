using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Snow;

/// <summary>
/// Draws the ski slopes near the camera (#887): each tile with slopes within <see cref="Radius"/>
/// gets a layer of a <see cref="Texture2DArray"/> (2 m a texel: the slope, the edge mask and the
/// groom phase), told to its ground mesh through the instance uniform <c>piste_layer</c>, as
/// <c>Terrain/PhotoLayer</c> does the photos, and to its road mesh, so a forest road under a slope
/// is snowed over too; and the slopes' snow sits in a small state texture
/// that is rewritten only when the snow has moved on (<see cref="StateSeconds"/>), never per
/// frame. The terrain material stays the one every tile shares. shaders/body/piste_snow.gdshaderinc.
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
    private readonly WorldOrigin _origin;
    private readonly ShaderMaterial _terrain;
    private readonly ShaderMaterial? _roads;
    private readonly SnowService _snow;
    private readonly Texture2DArray _map = new();
    private readonly Image _stateImage;
    private readonly ImageTexture _stateTexture;
    private readonly byte[] _state = new byte[SlotsPerLayer * Capacity * 3 * 4];
    private readonly Dictionary<TileId, (int Layer, ulong Ground, ulong Roads, PisteTile Tile)> _shown = new();
    private readonly HashSet<TileId> _building = new();
    private readonly Stack<int> _free = new();
    private readonly ConcurrentQueue<(TileId Id, PisteTile Tile, Image Image)> _ready = new();
    private readonly List<TileId> _gone = new();

    public static readonly StringName PisteLayerParam = "piste_layer";
    private static readonly StringName UsePistes = "use_pistes";
    private static readonly StringName PisteMap = "piste_map";
    private static readonly StringName PisteState = "piste_state";

    private double _sincePoll = double.MaxValue, _sinceState = double.MaxValue;
    private int _stateVersion = -1;
    private double _stateHour = -1;
    private bool _stateDirty;

    public PisteLayer(ChunkManager chunks, WorldOrigin origin, ShaderMaterial terrain, ShaderMaterial? roads, SnowService snow)
    {
        Name = "PisteLayer";
        _chunks = chunks;
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
            _shown[r.Id] = (layer, 0, 0, r.Tile);
            _stateDirty = true;
        }

        _sincePoll += delta;
        if (_sincePoll >= 0.5 && GetViewport()?.GetCamera3D() is { } cam)
        {
            _sincePoll = 0;
            Poll(_origin.TileAt(cam.GlobalPosition));
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
        _chunks.GroundAt(id)?.SetInstanceShaderParameter(PisteLayerParam, layer);
        if (_chunks.RoadsAt(id) is { } roads) TagRoads(roads, layer);
    }

    /// <summary>The roads mesh and its cells (a standalone headset's, <see cref="ChunkNode.SetRoadCells"/>).</summary>
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
            for (int dn = -ring; dn <= ring; dn++)
                for (int de = -ring; de <= ring; de++)
                {
                    if (Math.Max(Math.Abs(de), Math.Abs(dn)) != ring) continue;
                    var id = new TileId(centre.E + de, centre.N + dn);
                    if (_shown.TryGetValue(id, out var shown))
                    {
                        // a tile rebuilt or reloaded has new ground or road nodes: tell them its layer again
                        if (_chunks.GroundAt(id) is { } ground && ground.GetInstanceId() != shown.Ground)
                        {
                            ground.SetInstanceShaderParameter(PisteLayerParam, shown.Layer);
                            _shown[id] = shown = shown with { Ground = ground.GetInstanceId() };
                        }
                        if (_chunks.RoadsAt(id) is { } roads && roads.GetInstanceId() != shown.Roads)
                        {
                            TagRoads(roads, shown.Layer);
                            _shown[id] = shown with { Roads = roads.GetInstanceId() };
                        }
                        continue;
                    }
                    if (_building.Contains(id) || _free.Count <= _building.Count) continue;
                    if (_snow.Index?.Tile(id) is not { } tile) continue; // none there, or still loading
                    _building.Add(id);
                    Task.Run(() => _ready.Enqueue((id, tile, Paint(tile))));
                }
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
                Put(shown.Layer * 3 + 2, slot, (low.Bumps + high.Bumps) * 0.5f, r.AltLow / 4700f, r.AltHigh / 4700f, 0);
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
