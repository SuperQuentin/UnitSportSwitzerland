using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// The debug menu's drawings in the world (#339): tile boundaries on the ground, coloured by the
/// stride the tile is drawn at, labels on the near tiles, and the world origin with the radius past
/// which the floating origin (#185) moves.
///
/// <para>
/// Everything is rebuilt from the live origin once a second, when the camera enters another tile,
/// and on every origin shift, into one <see cref="ArrayMesh"/> (a few calls, however many lines),
/// with the node back at the identity: nothing here keeps a world position across frames. Nothing
/// is built or drawn while every drawing is off.
/// </para>
/// </summary>
public partial class DebugOverlay : Node3D, IOriginShiftAware
{
    private const double RefreshSeconds = 1.0;

    /// <summary>Lines float this high over the ground, clear of the flat-shaded facets.</summary>
    private const float Lift = 2f;

    private const float TileSize = (float)ChunkFormat.TileSizeM;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;

    private bool _tiles, _labels, _worldOrigin;

    /// <summary>Tile boundaries on the ground, coloured by stride; generated tiles dashed.</summary>
    public bool Tiles { get => _tiles; set => Set(ref _tiles, value); }

    /// <summary>Tile id, stride and source over the tiles around the camera.</summary>
    public bool Labels { get => _labels; set => Set(ref _labels, value); }

    /// <summary>The world origin's mast, axes and shift radius.</summary>
    public bool WorldOrigin { get => _worldOrigin; set => Set(ref _worldOrigin, value); }

    private void Set(ref bool field, bool value)
    {
        if (field == value) return;
        field = value;
        _dirty = true;
    }

    private bool Any => _tiles || _labels || _worldOrigin;

    private MeshInstance3D _ground = null!, _xray = null!;
    private ArrayMesh _groundMesh = null!, _xrayMesh = null!;
    private readonly List<Vector3> _groundPoints = new(), _xrayPoints = new();
    private readonly List<Color> _groundColours = new(), _xrayColours = new();
    private readonly List<(TileId Id, int Stride)> _loaded = new();
    private readonly List<Label3D> _tileLabels = new();
    private Label3D _originLabel = null!;
    private double _sinceRefresh = RefreshSeconds;
    private bool _dirty = true;
    private TileId _cameraTile;

    public DebugOverlay(ChunkManager chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
        Name = "DebugOverlay";
    }

    public DebugOverlay() : this(null!, null!) { }

    public override void _Ready()
    {
        _groundMesh = new ArrayMesh();
        _xrayMesh = new ArrayMesh();
        _ground = new MeshInstance3D { Mesh = _groundMesh, MaterialOverride = LineMaterial(xray: false), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _xray = new MeshInstance3D { Mesh = _xrayMesh, MaterialOverride = LineMaterial(xray: true), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_ground);
        AddChild(_xray);
        _originLabel = NewLabel(new Color(1f, 0.85f, 0.35f));
        AddChild(_originLabel);
    }

    private static StandardMaterial3D LineMaterial(bool xray) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        NoDepthTest = xray,
        RenderPriority = xray ? 10 : 0,
    };

    private static Label3D NewLabel(Color colour) => new()
    {
        Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
        NoDepthTest = true,
        FixedSize = true,
        PixelSize = 0.0007f,
        FontSize = 28,
        OutlineSize = 10,
        Modulate = colour,
        OutlineModulate = new Color(0, 0, 0, 0.8f),
        Shaded = false,
        Visible = false,
    };

    public void OnOriginShifted(OriginShift shift)
    {
        if (Any) Rebuild();
    }

    public override void _Process(double delta)
    {
        if (!Any)
        {
            if (_dirty) Rebuild();   // once, to clear what was drawn
            return;
        }
        _sinceRefresh += delta;
        var cam = GetViewport().GetCamera3D();
        var tile = cam == null ? _cameraTile : _origin.TileAt(cam.GlobalPosition);
        if (!_dirty && _sinceRefresh < RefreshSeconds && tile == _cameraTile) return;
        Rebuild();
    }

    private void Rebuild()
    {
        _dirty = false;
        _sinceRefresh = 0;
        Transform = Transform3D.Identity;
        _groundPoints.Clear();
        _groundColours.Clear();
        _xrayPoints.Clear();
        _xrayColours.Clear();

        var cam = GetViewport().GetCamera3D();
        var eye = cam?.GlobalPosition ?? Vector3.Zero;
        _cameraTile = _origin.TileAt(eye);
        float groundUnderEye = _chunks.TryGetHeight(eye, out float g) ? g : eye.Y - 100f;

        int labelsUsed = 0;
        if (_tiles || _labels)
        {
            // farther out the higher the camera: from 2 km up a 25 x 25 tile square is in view
            int rings = Math.Clamp(3 + (int)((eye.Y - groundUnderEye) / 400f), 3, 12);
            _chunks.ListTiles(_loaded);
            foreach (var (id, stride) in _loaded)
            {
                int dist = Math.Max(Math.Abs(id.E - _cameraTile.E), Math.Abs(id.N - _cameraTile.N));
                if (dist > rings) continue;
                bool generated = _chunks.IsGenerated(id);
                if (_tiles) AddTile(id, stride, generated, dist, groundUnderEye);
                if (_labels && dist <= 2) PlaceLabel(labelsUsed++, id, stride, generated, groundUnderEye);
            }
        }
        for (int i = labelsUsed; i < _tileLabels.Count; i++) _tileLabels[i].Visible = false;

        _originLabel.Visible = _worldOrigin;
        if (_worldOrigin) AddOrigin(eye);

        Upload(_groundMesh, _groundPoints, _groundColours);
        Upload(_xrayMesh, _xrayPoints, _xrayColours);
    }

    private static void Upload(ArrayMesh mesh, List<Vector3> points, List<Color> colours)
    {
        mesh.ClearSurfaces();
        if (points.Count == 0) return;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = points.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colours.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
    }

    /// <summary>Green at full resolution, through yellow, to red at the coarsest strides; grey before the first build.</summary>
    public static Color StrideColour(int stride)
    {
        if (stride < 0) return new Color(0.6f, 0.6f, 0.6f);
        if (stride == 0) return new Color(0.75f, 0.45f, 1f);   // grid only, no mesh yet
        float t = Math.Clamp(MathF.Log2(stride) / 6f, 0f, 1f);
        return Color.FromHsv(0.33f * (1f - t), 0.85f, 1f);
    }

    /// <summary>
    /// A tile's outline on its own ground, inset a little so two neighbours at different strides
    /// both show; a mast at its north-west corner so the grid reads from far off.
    /// </summary>
    private void AddTile(TileId id, int stride, bool generated, int dist, float fallback)
    {
        var colour = StrideColour(stride);
        var nw = _origin.ToWorld(id.MinE, id.MaxN, 0);
        const float inset = 4f;
        int steps = dist <= 3 ? 20 : 5;
        var a = nw + new Vector3(inset, 0, inset);
        var b = nw + new Vector3(TileSize - inset, 0, inset);
        var c = nw + new Vector3(TileSize - inset, 0, TileSize - inset);
        var d = nw + new Vector3(inset, 0, TileSize - inset);
        float h = fallback;
        Edge(a, b, steps, colour, generated, ref h);
        Edge(b, c, steps, colour, generated, ref h);
        Edge(c, d, steps, colour, generated, ref h);
        Edge(d, a, steps, colour, generated, ref h);

        float foot = _chunks.TryGetHeight(nw, out float at) ? at : fallback;
        Line(_groundPoints, _groundColours, new Vector3(nw.X, foot, nw.Z), new Vector3(nw.X, foot + 120f, nw.Z), colour);
    }

    /// <summary>One side of a tile, draped over the ground; generated tiles every other step (dashed).</summary>
    private void Edge(Vector3 from, Vector3 to, int steps, Color colour, bool dashed, ref float h)
    {
        var prev = Drape(from, ref h);
        for (int i = 1; i <= steps; i++)
        {
            var next = Drape(from.Lerp(to, i / (float)steps), ref h);
            if (!dashed || i % 2 == 1) Line(_groundPoints, _groundColours, prev, next, colour);
            prev = next;
        }
    }

    /// <summary>A point on the drawn ground, or at the last height known where no grid is loaded.</summary>
    private Vector3 Drape(Vector3 p, ref float h)
    {
        if (_chunks.TryGetHeight(p, out float ground)) h = ground;
        return new Vector3(p.X, h + Lift, p.Z);
    }

    private static void Line(List<Vector3> points, List<Color> colours, Vector3 a, Vector3 b, Color colour)
    {
        points.Add(a);
        points.Add(b);
        colours.Add(colour);
        colours.Add(colour);
    }

    private void PlaceLabel(int index, TileId id, int stride, bool generated, float fallback)
    {
        while (_tileLabels.Count <= index)
        {
            var label = NewLabel(Colors.White);
            AddChild(label);
            _tileLabels.Add(label);
        }
        var l = _tileLabels[index];
        var centre = _origin.ToWorld(id.MinE + TileSize / 2, id.MaxN - TileSize / 2, 0);
        float ground = _chunks.TryGetHeight(centre, out float at) ? at : fallback;
        l.Position = new Vector3(centre.X, ground + 40f, centre.Z);
        string strideText = stride < 0 ? "not built" : stride == 0 ? "grid only" : $"stride {stride}";
        string text = $"{id}\n{strideText}{(generated ? "\ngenerated" : "")}";
        if (l.Text != text) l.Text = text;
        l.Modulate = StrideColour(stride).Lightened(0.35f);
        l.Visible = true;
    }

    /// <summary>
    /// The origin: a mast through it, seen through the ground; east (red) and north (blue) axes
    /// along the ground; and the circle past which the camera moves it.
    /// </summary>
    private void AddOrigin(Vector3 eye)
    {
        var amber = new Color(1f, 0.78f, 0.15f);
        float foot = _chunks.TryGetHeight(Vector3.Zero, out float at) ? at : 0f;
        Line(_xrayPoints, _xrayColours, new Vector3(0, foot - 100f, 0), new Vector3(0, foot + 2500f, 0), amber);
        Line(_xrayPoints, _xrayColours, new Vector3(0, foot + Lift, 0), new Vector3(300f, foot + Lift, 0), new Color(1f, 0.3f, 0.3f));
        Line(_xrayPoints, _xrayColours, new Vector3(0, foot + Lift, 0), new Vector3(0, foot + Lift, -300f), new Color(0.35f, 0.55f, 1f));

        // the shift radius, on the ground where it is loaded
        double threshold = OriginShifter.Instance?.ThresholdM ?? 3000;
        const int segments = 160;
        float h = foot;
        var prev = Drape(new Vector3((float)threshold, 0, 0), ref h);
        for (int i = 1; i <= segments; i++)
        {
            float angle = Mathf.Tau * i / segments;
            var next = Drape(new Vector3(MathF.Cos(angle) * (float)threshold, 0, MathF.Sin(angle) * (float)threshold), ref h);
            Line(_groundPoints, _groundColours, prev, next, amber);
            prev = next;
        }

        float away = new Vector2(eye.X, eye.Z).Length();
        _originLabel.Position = new Vector3(0, foot + 120f, 0);
        var shifter = OriginShifter.Instance;
        _originLabel.Text = $"World origin\nLV95 {_origin.E:F0} / {_origin.N:F0}\n"
            + $"camera {away / 1000f:F2} km out, shifts past {threshold / 1000:F1} km\n"
            + $"{shifter?.ShiftCount ?? 0} shifts";
    }
}
