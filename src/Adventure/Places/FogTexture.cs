using Godot;

namespace UnitSport.Adventure.Places;

/// <summary>
/// The fog drawn on the adventure map (#1224, places plan 4): one <see cref="Image"/> of
/// <see cref="FogMask.Width"/> x <see cref="FogMask.Height"/> L8 texels (1.3 MB, 255 = fog, row 0 the
/// north), built from <see cref="PlacesService.MyFog"/> the first time a map asks, rewritten on
/// <c>FogFull</c> and patched per <c>FogDelta</c>: <see cref="ImageTexture.Update"/> only then, never per frame.
/// </summary>
public static class FogTexture
{
    private static Image? _image;
    private static ImageTexture? _texture;
    private static PlacesService? _hooked;
    /// <summary>Bumped on every change (a map redraws on it).</summary>
    public static int Version { get; private set; }

    /// <summary>The fog lattice for <c>MapView.DrawSampled</c>: west, north, spacing, columns, rows.</summary>
    public static (double MinE, double MaxN, double Spacing, int Cols, int Rows) Lattice =>
        (FogMask.MinE, FogMask.MinN + FogMask.Height * FogMask.CellM, FogMask.CellM, FogMask.Width, FogMask.Height);

    public static ImageTexture? Get()
    {
        if (PlacesService.Instance is not { } places) return null;
        if (_hooked != places)
        {
            if (_hooked != null && GodotObject.IsInstanceValid(_hooked)) _hooked.FogChanged -= OnChanged;
            _hooked = places;
            places.FogChanged += OnChanged;
            _image = null;
        }
        if (_image == null) Rebuild(places.MyFog);
        return _texture;
    }

    private static void Rebuild(FogMask fog)
    {
        var bytes = new byte[FogMask.Width * FogMask.Height];
        Array.Fill(bytes, (byte)255);
        foreach (var (tile, mask) in fog.Tiles) Write(bytes, tile, mask);
        if (_image == null)
        {
            _image = Image.CreateFromData(FogMask.Width, FogMask.Height, false, Image.Format.L8, bytes);
            if (_texture == null) _texture = ImageTexture.CreateFromImage(_image);
            else _texture.Update(_image);
        }
        else
        {
            _image.SetData(FogMask.Width, FogMask.Height, false, Image.Format.L8, bytes);
            _texture!.Update(_image);
        }
        Version++;
    }

    private static void Write(byte[] bytes, int tile, ushort mask)
    {
        int tx = tile % FogMask.TilesE, ty = tile / FogMask.TilesE;
        for (int bit = 0; bit < 16; bit++)
        {
            int col = tx * FogMask.CellsPerTile + bit % FogMask.CellsPerTile, row = ty * FogMask.CellsPerTile + bit / FogMask.CellsPerTile;
            bytes[(FogMask.Height - 1 - row) * FogMask.Width + col] = (mask & (1 << bit)) != 0 ? (byte)0 : (byte)255;
        }
    }

    private static void OnChanged(IReadOnlyCollection<int>? tiles)
    {
        if (_image == null || _hooked == null) return;   // built on the next Get
        if (tiles == null) { Rebuild(_hooked.MyFog); return; }
        foreach (int tile in tiles)
        {
            ushort mask = _hooked.MyFog.MaskOf(tile);
            int tx = tile % FogMask.TilesE, ty = tile / FogMask.TilesE;
            for (int bit = 0; bit < 16; bit++)
            {
                int col = tx * FogMask.CellsPerTile + bit % FogMask.CellsPerTile, row = ty * FogMask.CellsPerTile + bit / FogMask.CellsPerTile;
                _image.SetPixel(col, FogMask.Height - 1 - row, (mask & (1 << bit)) != 0 ? Colors.Black : Colors.White);
            }
        }
        _texture!.Update(_image);
        Version++;
    }

    /// <summary>Leaving the world: the next one builds its own.</summary>
    public static void Reset()
    {
        _image = null;
        _hooked = null;
        Version++;
    }
}
