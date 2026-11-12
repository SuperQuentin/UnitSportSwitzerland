using System;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Snow;

/// <summary>
/// The ski map (#887), top right while the local player is on skis: the slopes within
/// <see cref="Span"/> m in their colours, true north up (the oblique stereographic at you, which over 1.2 km is
/// the LV95 grid turned by its convergence: #1514), your arrow in the middle. The picture is
/// <see cref="Pixels"/>² sampled from the slope rasters the client already holds (<see cref="PisteIndex"/>),
/// into one reused buffer, and only when you have moved <see cref="Redraw"/> m. Between rebuilds
/// it is redrawn only when the arrow turns (perf-no-per-frame-allocations).
/// <para>
/// On earth leaves (#1737) a pixel's place is the skier's local east and north on the Earth, its leaf from the cube, and
/// its slope the leaf's stand-in's at that place; the stand-in's axes are its leaf's east and north, within a hundredth
/// of a degree of the skier's over the map.
/// </para>
/// </summary>
public partial class SlopeMap : Control
{
    public const float Span = 1200f;
    private const int Pixels = 200;
    private const float Side = 220f;
    private const float Redraw = 30f;

    private readonly Func<FootPlayer?> _player;
    private readonly IWorldGround _chunks;
    /// <summary>Per leaf on the map (earth): its stand-in, the skier's place in its frame, its slopes. Cleared each paint.</summary>
    private readonly Dictionary<CubeKey, (TileId Id, double E, double N, PisteTile? Tile)> _leaves = new();
    private readonly byte[] _rgba = new byte[Pixels * Pixels * 4];
    private readonly Image _image;
    private readonly ImageTexture _texture;
    private readonly Vector2[] _arrow = new Vector2[3];
    private readonly Color[] _arrowColour = [Colors.White, Colors.White, Colors.White];
    private (double E, double N) _centre = (double.NaN, double.NaN);
    private float _heading = float.NaN;
    private double _since, _sincePaint;
    private bool _incomplete;

    private static readonly Color[] Level =
    [
        new(0.25f, 0.5f, 1f), new(0.95f, 0.25f, 0.22f), new(0.1f, 0.1f, 0.12f), new(1f, 0.82f, 0.2f),
    ];
    private static readonly Color Nordic = new(0.3f, 0.85f, 0.85f), Sled = new(0.75f, 0.45f, 0.9f);

    public SlopeMap(Func<FootPlayer?> player, IWorldGround chunks)
    {
        _player = player;
        _chunks = chunks;
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        _image = Image.CreateFromData(Pixels, Pixels, false, Image.Format.Rgba8, _rgba);
        _texture = ImageTexture.CreateFromImage(_image);
    }

    public override void _Process(double delta)
    {
        _since += delta;
        if (_since < 0.1) return;
        _since = 0;
        var p = _player();
        bool show = p != null && IsInstanceValid(p) && SnowRide.Is(p.RideKindId)
                    && SnowService.Instance?.Index != null && _chunks.Origin != null;
        Visible = show;
        if (!show) return;
        // below the fps readout in the corner
        Position = new Vector2(GetViewportRect().Size.X - Side - 16f, 48f);
        Size = new Vector2(Side, Side);

        var (e, n) = _chunks.Origin!.ToLv95(p!.GlobalPosition);
        var fwd = -p.GlobalTransform.Basis.Z;
        // 0 true north, clockwise
        float heading = -_chunks.Origin.TrueYaw(p.GlobalPosition, Mathf.Atan2(-fwd.X, -fwd.Z));
        _sincePaint += 0.1;
        // a tile still loading at the last paint: paint again once a second until all are in
        bool moved = double.IsNaN(_centre.E) || Math.Abs(e - _centre.E) + Math.Abs(n - _centre.N) > Redraw
                     || (_incomplete && _sincePaint >= 1);
        if (moved)
        {
            _sincePaint = 0;
            _centre = (e, n);
            if (_chunks.TileAt(p.GlobalPosition).IsLeaf) PaintLeaves(SnowService.Instance!.Index!, _chunks.Origin.ToGlobal(p.GlobalPosition));
            else Paint(SnowService.Instance!.Index!, e, n);
        }
        if (moved || Mathf.Abs(Mathf.AngleDifference(heading, _heading)) > 0.05f)
        {
            _heading = heading;
            QueueRedraw();
        }
    }

    /// <summary>The slopes round (e, n) into the buffer: their colour, darker toward the edge, the rest see-through.</summary>
    private void Paint(PisteIndex index, double e, double n)
    {
        float m = Span / Pixels;
        // a map metre east and north as LV95 metres: the grid turned by its convergence γ (true north is at grid bearing γ)
        double g = SwissGeodesy.GridConvergence(e, n), cos = Math.Cos(g), sin = Math.Sin(g);
        TileId lastId = default;
        PisteTile? tile = null;
        bool haveTile = false;
        _incomplete = false;
        for (int y = 0; y < Pixels; y++)
        {
            double my = Span / 2 - (y + 0.5) * m;
            for (int x = 0; x < Pixels; x++)
            {
                double mx = -Span / 2 + (x + 0.5) * m;
                double pe = e + mx * cos + my * sin, pn = n - mx * sin + my * cos;
                var id = TileId.FromLv95(pe, pn);
                if (!haveTile || id != lastId)
                {
                    if (!index.Loaded(id, out tile)) _incomplete = true;
                    lastId = id;
                    haveTile = true;
                }
                int o = (y * Pixels + x) * 4;
                int i = tile?.IndexAt(pe, pn) ?? -1;
                if (i < 0 || tile!.Slot[i] == 0)
                {
                    _rgba[o] = 12; _rgba[o + 1] = 14; _rgba[o + 2] = 18; _rgba[o + 3] = 150;
                    continue;
                }
                var r = tile.Records[tile.Slot[i] - 1];
                var c = r.Type switch
                {
                    PisteType.Nordic => Nordic,
                    PisteType.Sled => Sled,
                    _ => Level[SlopeMarkers.Level(r.Difficulty)],
                };
                // the slope's edge a shade darker, so two runs side by side stay apart
                c = c.Darkened(PisteFormat.MaskOf(tile.Aux[i]) < 0.6f ? 0.35f : 0f);
                _rgba[o] = (byte)(c.R * 255); _rgba[o + 1] = (byte)(c.G * 255); _rgba[o + 2] = (byte)(c.B * 255); _rgba[o + 3] = 235;
            }
        }
        _image.SetData(Pixels, Pixels, false, Image.Format.Rgba8, _rgba);
        _texture.Update(_image);
    }

    /// <summary>The slopes round a place on earth leaves into the buffer (#1737), as <see cref="Paint"/> does on LV95 tiles.</summary>
    private void PaintLeaves(PisteIndex index, GlobalPos at)
    {
        float m = Span / Pixels;
        var enu = at.Enu;
        _leaves.Clear();
        _incomplete = false;
        CubeKey last = default;
        (TileId Id, double E, double N, PisteTile? Tile) leaf = default;
        bool have = false;
        for (int y = 0; y < Pixels; y++)
        {
            double my = Span / 2 - (y + 0.5) * m;
            for (int x = 0; x < Pixels; x++)
            {
                double mx = -Span / 2 + (x + 0.5) * m;
                // x east, y up, z south
                var key = CubeKey.FromEcef(enu.ToEcef(mx, 0, -my), Terrain.Leaves.LeafFrames.LeafLevel);
                if (!have || key != last)
                {
                    if (!_leaves.TryGetValue(key, out leaf))
                    {
                        var id = Terrain.Leaves.LeafStandIn.Id(key);
                        var (le, ln, _) = at.InTileEn(id);
                        if (!index.Loaded(id, out var tile)) _incomplete = true;
                        _leaves[key] = leaf = (id, le, ln, tile);
                    }
                    last = key;
                    have = true;
                }
                Put(leaf.Tile, leaf.E + mx, leaf.N + my, (y * Pixels + x) * 4);
            }
        }
        _image.SetData(Pixels, Pixels, false, Image.Format.Rgba8, _rgba);
        _texture.Update(_image);
    }

    /// <summary>One pixel: the slope at a tile's E/N in its colour, darker toward its edge, else see-through.</summary>
    private void Put(PisteTile? tile, double pe, double pn, int o)
    {
        int i = tile?.IndexAt(pe, pn) ?? -1;
        if (i < 0 || tile!.Slot[i] == 0)
        {
            _rgba[o] = 12; _rgba[o + 1] = 14; _rgba[o + 2] = 18; _rgba[o + 3] = 150;
            return;
        }
        var r = tile.Records[tile.Slot[i] - 1];
        var c = r.Type switch
        {
            PisteType.Nordic => Nordic,
            PisteType.Sled => Sled,
            _ => Level[SlopeMarkers.Level(r.Difficulty)],
        };
        c = c.Darkened(PisteFormat.MaskOf(tile.Aux[i]) < 0.6f ? 0.35f : 0f);
        _rgba[o] = (byte)(c.R * 255); _rgba[o + 1] = (byte)(c.G * 255); _rgba[o + 2] = (byte)(c.B * 255); _rgba[o + 3] = 235;
    }

    /// <summary>
    /// Paints the map round a world point now, as a skier there sees it: true once every tile or leaf it shows had loaded.
    /// A check's (#1737): the map otherwise follows the local skier.
    /// </summary>
    public bool PaintAt(Vector3 world)
    {
        if (SnowService.Instance?.Index is not { } index || _chunks.Origin is not { } origin) return false;
        if (_chunks.TileAt(world).IsLeaf) PaintLeaves(index, origin.ToGlobal(world));
        else
        {
            var (e, n) = origin.ToLv95(world);
            Paint(index, e, n);
        }
        return !_incomplete;
    }

    /// <summary>Whether the map's middle pixel (where the skier is) shows a slope.</summary>
    public bool MiddleIsSlope => _rgba[((Pixels / 2) * Pixels + Pixels / 2) * 4 + 3] == 235;

    /// <summary>How many of the map's pixels show a slope (a check's question, #1737).</summary>
    public int SlopePixels()
    {
        int n = 0;
        for (int o = 3; o < _rgba.Length; o += 4) if (_rgba[o] == 235) n++;
        return n;
    }

    public override void _Draw()
    {
        var size = new Vector2(Side, Side);
        DrawTextureRect(_texture, new Rect2(Vector2.Zero, size), false);
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0, 0, 0, 0.9f), false, 3f);
        // you: an arrow along your heading, in the middle
        var mid = size * 0.5f;
        var fwd = new Vector2(Mathf.Sin(_heading), -Mathf.Cos(_heading));
        var side = new Vector2(-fwd.Y, fwd.X);
        _arrow[0] = mid + fwd * 9f;
        _arrow[1] = mid - fwd * 6f + side * 6f;
        _arrow[2] = mid - fwd * 6f - side * 6f;
        DrawPolygon(_arrow, _arrowColour);
        DrawPolyline(_arrow, Colors.Black, 1.5f);
        DrawString(ThemeDB.FallbackFont, new Vector2(Side * 0.5f - 4f, 16f), "N", HorizontalAlignment.Left, -1, 13, Colors.White);
        float bar = 200f * Side / Span;
        DrawLine(new Vector2(8, Side - 10), new Vector2(8 + bar, Side - 10), Colors.White, 2f);
        DrawString(ThemeDB.FallbackFont, new Vector2(10 + bar, Side - 6), "200 m", HorizontalAlignment.Left, -1, 11, Colors.White);
    }
}
