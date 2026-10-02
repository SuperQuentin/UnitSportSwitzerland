using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// The SWISSIMAGE aerial photos under the camera, for a style that drapes them over its terrain
/// (Realistic+; docs/notes/styles/swissimage.md). <c>TerrainPreprocessor --photos</c> leaves one
/// 512x512 JPEG per tile next to the tile's files; this keeps the ones within
/// <see cref="Radius"/> tiles of the camera in one <see cref="Texture2DArray"/>, decoded and
/// compressed on a worker, and tells each tile's ground mesh its layer through the instance
/// uniform <c>photo_slot</c>: the terrain material stays the one shared by every tile.
///
/// <para>
/// Client-local: it reads the local terrain folder. A client streaming its tiles from a server
/// finds no photos there and its terrain keeps the cover textures.
/// </para>
/// </summary>
public partial class PhotoLayer : Node
{
    /// <summary>Tiles around the camera's: 3 is the 7x7 km under and around it.</summary>
    private const int Radius = 3;
    private const int Slots = (2 * Radius + 1) * (2 * Radius + 1);
    private const int Size = 512;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly ShaderMaterial _terrain;
    private readonly string _dir;
    private readonly Texture2DArray _array = new();
    private readonly Dictionary<TileId, (int Slot, ulong Ground)> _shown = new();
    private readonly HashSet<TileId> _loading = new();
    private readonly Stack<int> _free = new();
    private readonly Dictionary<TileId, bool> _hasPhoto = new();
    private readonly ConcurrentQueue<(TileId Id, Image? Image)> _ready = new();
    private readonly List<TileId> _gone = new();

    // polled twice a second: a string would convert to a new StringName each call (#221)
    private static readonly StringName PhotoSlot = "photo_slot";
    private double _sincePoll = double.MaxValue;

    public PhotoLayer(ChunkManager chunks, WorldOrigin origin, ShaderMaterial terrain, string dir)
    {
        Name = "PhotoLayer";
        _chunks = chunks;
        _origin = origin;
        _terrain = terrain;
        _dir = dir;
        var blank = Image.CreateEmpty(Size, Size, true, Image.Format.Rgb8);
        blank.Compress(Image.CompressMode.S3Tc);
        var layers = new Godot.Collections.Array<Image>();
        for (int i = 0; i < Slots; i++) layers.Add(blank);
        _array.CreateFromImages(layers);
        for (int i = Slots - 1; i >= 0; i--) _free.Push(i);
        _terrain.SetShaderParameter("photos", _array);
        _terrain.SetShaderParameter("use_photos", true);
    }

    /// <summary>The photo of a tile, if the preprocessor left one (TerrainPreprocessor PhotoStage).</summary>
    public static string PathOf(string dir, TileId id) => Path.Combine(dir, $"photo_{id}.jpg");

    public override void _ExitTree()
    {
        _terrain.SetShaderParameter("use_photos", false);
        foreach (var (id, _) in _shown)
            if (_chunks.GroundAt(id) is { } ground) ground.SetInstanceShaderParameter(PhotoSlot, -1);
    }

    public override void _Process(double delta)
    {
        while (_ready.TryDequeue(out var r))
        {
            _loading.Remove(r.Id);
            if (r.Image == null || _free.Count == 0) continue;
            int slot = _free.Pop();
            _array.UpdateLayer(r.Image, slot);
            _shown[r.Id] = (slot, 0);
        }

        _sincePoll += delta;
        if (_sincePoll < 0.5 || GetViewport()?.GetCamera3D() is not { } cam) return;
        _sincePoll = 0;

        var centre = _origin.TileAt(cam.GlobalPosition);
        // gone out of range: free the slot
        _gone.Clear();
        foreach (var (id, _) in _shown)
            if (LodPolicy.Distance(id, centre) > Radius) _gone.Add(id);
        foreach (var id in _gone)
        {
            if (_chunks.GroundAt(id) is { } ground) ground.SetInstanceShaderParameter(PhotoSlot, -1);
            _free.Push(_shown[id].Slot);
            _shown.Remove(id);
        }
        for (int dn = -Radius; dn <= Radius; dn++)
            for (int de = -Radius; de <= Radius; de++)
            {
                var id = new TileId(centre.E + de, centre.N + dn);
                if (_shown.TryGetValue(id, out var shown))
                {
                    // a tile rebuilt or reloaded has a new ground node: tell it its slot again
                    if (_chunks.GroundAt(id) is { } ground && ground.GetInstanceId() != shown.Ground)
                    {
                        ground.SetInstanceShaderParameter(PhotoSlot, shown.Slot);
                        _shown[id] = (shown.Slot, ground.GetInstanceId());
                    }
                    continue;
                }
                if (_loading.Contains(id) || _free.Count <= _loading.Count) continue;
                if (!_hasPhoto.TryGetValue(id, out bool has))
                    _hasPhoto[id] = has = File.Exists(PathOf(_dir, id));
                if (!has) continue;
                _loading.Add(id);
                string path = PathOf(_dir, id);
                Task.Run(() => _ready.Enqueue((id, Decode(path))));
            }
    }

    /// <summary>The JPEG as the array's layer format: 512x512, mipmapped, DXT1. Any thread.</summary>
    private static Image? Decode(string path)
    {
        try
        {
            var image = new Image();
            if (image.LoadJpgFromBuffer(File.ReadAllBytes(path)) != Error.Ok) return null;
            image.Convert(Image.Format.Rgb8);
            if (image.GetWidth() != Size || image.GetHeight() != Size)
                image.Resize(Size, Size, Image.Interpolation.Bilinear);
            image.GenerateMipmaps();
            image.Compress(Image.CompressMode.S3Tc);
            return image;
        }
        catch (System.Exception e)
        {
            GD.PushWarning($"[photos] {path}: {e.Message}");
            return null;
        }
    }
}
