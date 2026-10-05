using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Farming;

/// <summary>How one cell is drawn now: what the main thread hands the worker (<see cref="FieldMeshBuilder"/>).</summary>
public struct CellLook
{
    public FieldStage Stage;
    public CropKind Crop;
    /// <summary>0..1 of the way to ripe (quantised by the caller, so a rebuild changes something).</summary>
    public float Growth;
    /// <summary>A field cell at all (the rest is drawn as nothing).</summary>
    public bool Field;
    /// <summary>The field's rows run north-south (else east-west), from its id.</summary>
    public bool RowsNorth;
    /// <summary>A per-cell hash, for colour and height jitter.</summary>
    public byte Jitter;
}

/// <summary>
/// Builds the mesh of one 100 m drawing chunk of fields (#494, <c>docs/notes/farming/fields-runtime.md</c>)
/// on a worker: plain arrays, no Godot object. Draped on the ground the terrain draws
/// (<c>ChunkGrid.SampleMeshHeight</c>), flat triangles, vertex colours raw linear with alpha 0 (the
/// prop shader reads alpha as a lamp mask). Two details: <c>lod 0</c> near the camera (furrows,
/// sprout rows, stubble, swaths, potato ridges, sunflower heads), <c>lod 1</c> further out (flat
/// cover, crop blocks only). Untouched grass is not drawn: the terrain already is grass.
/// </summary>
public sealed class FieldMeshBuilder
{
    public const int Side = FieldTile.ChunkCells;
    /// <summary>The look array is the chunk plus one cell all round (neighbours decide the walls).</summary>
    public const int LookSide = Side + 2;
    private const float Cs = FieldFormat.CellSize;
    private const float Lift = 0.10f;

    private readonly CellLook[] _looks;
    private readonly Func<double, double, double> _ground;
    private readonly double _e0, _n0;
    private readonly int _lod;
    private readonly float[] _corner = new float[(Side + 1) * (Side + 1)];

    public readonly List<Vector3> Verts = new();
    public readonly List<Color> Colors = new();

    /// <param name="looks"><see cref="LookSide"/>² cells, row-major from the south-west, the chunk at (1, 1).</param>
    /// <param name="e0">The chunk's south-west corner, LV95.</param>
    public FieldMeshBuilder(CellLook[] looks, Func<double, double, double> ground, double e0, double n0, int lod)
    {
        _looks = looks;
        _ground = ground;
        _e0 = e0;
        _n0 = n0;
        _lod = lod;
    }

    public static int LookIndex(int col, int row) => (row + 1) * LookSide + col + 1;

    public void Build()
    {
        for (int r = 0; r <= Side; r++)
            for (int c = 0; c <= Side; c++)
                _corner[r * (Side + 1) + c] = (float)_ground(_e0 + c * Cs, _n0 + r * Cs);
        for (int r = 0; r < Side; r++)
            for (int c = 0; c < Side; c++)
                Cell(c, r, _looks[LookIndex(c, r)]);
    }

    // ---- what a cell looks like -------------------------------------------------------------------

    /// <summary>Ripe height of a standing crop drawn as a block (0: drawn as rows or cover).</summary>
    public static float BlockHeight(CropKind crop) => crop switch
    {
        CropKind.Wheat => 0.95f,
        CropKind.Barley => 0.8f,
        CropKind.OtherArable => 0.8f,
        CropKind.Maize => 2.6f,
        CropKind.Rapeseed => 1.3f,
        CropKind.Sunflower => 1.9f,
        CropKind.Legumes => 0.7f,
        _ => 0f,
    };

    private static bool RowCrop(CropKind crop) => crop is CropKind.Potato or CropKind.SugarBeet or CropKind.Vegetables;

    private static bool Standing(FieldStage s) => s is FieldStage.Growing or FieldStage.Ripe;

    /// <summary>The top of a cell's crop block above the ground, 0 when it has none.</summary>
    public static float TopOf(in CellLook l)
    {
        if (!l.Field || !Standing(l.Stage)) return 0f;
        float h = BlockHeight(l.Crop);
        if (h <= 0) return 0f;
        float g = l.Stage == FieldStage.Ripe ? 1f : l.Growth;
        // a little height jitter, under the wall threshold: one even top, not tiles
        return h * (0.25f + 0.75f * g) * (0.98f + l.Jitter / 255f * 0.04f);
    }

    private static Color Lin(float r, float g, float b) => new Color(r, g, b).SrgbToLinear() with { A = 0f };

    private static readonly Color Soil = Lin(0.40f, 0.29f, 0.19f), SoilDark = Lin(0.33f, 0.24f, 0.16f), SoilRidge = Lin(0.47f, 0.35f, 0.23f);
    private static readonly Color Straw = Lin(0.74f, 0.65f, 0.40f), StrawDark = Lin(0.60f, 0.52f, 0.32f);
    private static readonly Color Sprout = Lin(0.42f, 0.62f, 0.22f), Leaf = Lin(0.27f, 0.50f, 0.18f), LeafDark = Lin(0.20f, 0.40f, 0.14f);
    private static readonly Color Cut = Lin(0.55f, 0.66f, 0.30f), Hay = Lin(0.72f, 0.72f, 0.38f), Grass = Lin(0.36f, 0.58f, 0.22f);
    private static readonly Color Gold = Lin(0.88f, 0.72f, 0.34f), Pale = Lin(0.86f, 0.77f, 0.48f), MaizeDry = Lin(0.74f, 0.64f, 0.40f);
    private static readonly Color MaizeGreen = Lin(0.22f, 0.44f, 0.15f), RapeYellow = Lin(0.96f, 0.86f, 0.16f), RapeRipe = Lin(0.50f, 0.44f, 0.25f);
    private static readonly Color SunHead = Lin(0.96f, 0.76f, 0.10f), SunRipe = Lin(0.40f, 0.30f, 0.16f), Tan = Lin(0.72f, 0.64f, 0.40f);
    private static readonly Color Yellowing = Lin(0.58f, 0.58f, 0.27f);

    /// <summary>The colour of a standing crop's top.</summary>
    public static Color CropColour(CropKind crop, FieldStage stage, float growth)
    {
        float ripe = stage == FieldStage.Ripe ? 1f : Mathf.Clamp((growth - 0.6f) / 0.4f, 0f, 1f);
        return crop switch
        {
            CropKind.Wheat or CropKind.OtherArable => Leaf.Lerp(Gold, ripe),
            CropKind.Barley => Leaf.Lerp(Pale, ripe),
            CropKind.Maize => stage == FieldStage.Ripe ? MaizeDry : MaizeGreen,
            CropKind.Rapeseed => stage == FieldStage.Ripe ? RapeRipe : growth > 0.5f ? RapeYellow : Leaf,
            CropKind.Sunflower => stage == FieldStage.Ripe ? MaizeDry.Darkened(0.2f) : MaizeGreen,
            CropKind.Legumes => Leaf.Lerp(Tan, ripe),
            CropKind.Potato or CropKind.SugarBeet or CropKind.Vegetables => stage == FieldStage.Ripe && crop == CropKind.Potato ? Yellowing : Leaf,
            _ => Grass,
        };
    }

    private static Color Shade(Color c, byte jitter) => (c * (0.94f + jitter / 255f * 0.12f)) with { A = 0f };

    private void Cell(int c, int r, in CellLook l)
    {
        if (!l.Field) return;
        float x0 = c * Cs, z0 = -r * Cs;   // local: x east, z = -north
        switch (l.Stage)
        {
            case FieldStage.Ploughed:
                Cover(c, r, Shade(Soil, l.Jitter));
                if (_lod == 0) Rows(c, r, l.RowsNorth, 2, 0.20f, 1.7f, SoilRidge);
                break;
            case FieldStage.Sown:
                Cover(c, r, Shade(SoilDark, l.Jitter));
                if (_lod == 0)
                {
                    Rows(c, r, l.RowsNorth, 2, 0.12f, 1.5f, SoilRidge);
                    if (l.Growth > 0.05f) Rows(c, r, l.RowsNorth, 2, 0.08f + 0.25f * l.Growth, 0.35f, Sprout, 0.10f);
                }
                break;
            case FieldStage.Stubble:
                Cover(c, r, Shade(Straw, l.Jitter));
                if (_lod == 0) Rows(c, r, l.RowsNorth, 4, 0.14f, 0.4f, StrawDark);
                break;
            case FieldStage.Mown:
                Cover(c, r, Shade(Cut, l.Jitter));
                if (_lod == 0 && l.Growth < 0.5f) Rows(c, r, l.RowsNorth, 1, 0.35f, 1.3f, Hay);
                break;
            case FieldStage.Grass:
                // untouched grass is the terrain's own; regrown grass looks the same
                break;
            case FieldStage.Growing:
            case FieldStage.Ripe:
                if (RowCrop(l.Crop))
                {
                    var leaf = Shade(CropColour(l.Crop, l.Stage, l.Growth), l.Jitter);
                    float g = l.Stage == FieldStage.Ripe ? 1f : l.Growth;
                    float h = (l.Crop == CropKind.SugarBeet ? 0.55f : 0.45f) * (0.35f + 0.65f * g);
                    if (_lod == 0)
                    {
                        Cover(c, r, Shade(SoilDark, l.Jitter));
                        Rows(c, r, l.RowsNorth, 2, h, 1.6f, leaf);
                    }
                    else Cover(c, r, leaf.Lerp(SoilDark, 0.3f));
                }
                else Block(c, r, l, x0, z0);
                break;
        }
    }

    /// <summary>The cell's ground, lifted: a quad over its four corners.</summary>
    private void Cover(int c, int r, Color col)
    {
        float x0 = c * Cs, x1 = x0 + Cs, z0 = -r * Cs, z1 = z0 - Cs;
        var sw = new Vector3(x0, H(c, r) + Lift, z0);
        var se = new Vector3(x1, H(c + 1, r) + Lift, z0);
        var ne = new Vector3(x1, H(c + 1, r + 1) + Lift, z1);
        var nw = new Vector3(x0, H(c, r + 1) + Lift, z1);
        Quad(sw, se, ne, nw, col);
    }

    private float H(int c, int r) => _corner[r * (Side + 1) + c];

    /// <summary>Ground under a local point (inside the chunk), bilinear over the cell corners.</summary>
    private float GroundAt(float x, float z)
    {
        float u = Math.Clamp(x / Cs, 0, Side - 0.001f), v = Math.Clamp(-z / Cs, 0, Side - 0.001f);
        int c = (int)u, r = (int)v;
        float fu = u - c, fv = v - r;
        float a = H(c, r) + (H(c + 1, r) - H(c, r)) * fu;
        float b = H(c, r + 1) + (H(c + 1, r + 1) - H(c, r + 1)) * fu;
        return a + (b - a) * fv;
    }

    /// <summary>
    /// <paramref name="count"/> ridges across the cell along its rows: a roof of two slopes,
    /// <paramref name="height"/> high and <paramref name="width"/> wide at the foot.
    /// </summary>
    private void Rows(int c, int r, bool north, int count, float height, float width, Color col, float base_ = 0f)
    {
        float x0 = c * Cs, z0 = -r * Cs;
        var dark = col.Darkened(0.18f) with { A = 0f };
        for (int k = 0; k < count; k++)
        {
            float off = (k + 0.5f) * Cs / count;
            float half = Math.Min(width, Cs / count) * 0.5f;
            // along: the ridge runs the whole cell; across: its foot spans +-half around off
            Vector3 P(float along, float across, float up)
            {
                float x = north ? x0 + off + across : x0 + along;
                float z = north ? z0 - along : z0 - off - across;
                return new Vector3(x, GroundAt(x, z) + Lift + base_ + up, z);
            }
            var a0 = P(0, -half, 0); var a1 = P(Cs, -half, 0);
            var t0 = P(0, 0, height); var t1 = P(Cs, 0, height);
            var b0 = P(0, half, 0); var b1 = P(Cs, half, 0);
            Quad(a0, a1, t1, t0, col);
            Quad(t0, t1, b1, b0, dark);
        }
    }

    /// <summary>A standing crop: a top at its height, walls where a neighbour stands lower.</summary>
    private void Block(int c, int r, in CellLook l, float x0, float z0)
    {
        float top = TopOf(l);
        var col = Shade(CropColour(l.Crop, l.Stage, l.Growth), l.Jitter);
        var wall = col.Darkened(0.22f) with { A = 0f };
        float x1 = x0 + Cs, z1 = z0 - Cs;
        var sw = new Vector3(x0, H(c, r) + top, z0);
        var se = new Vector3(x1, H(c + 1, r) + top, z0);
        var ne = new Vector3(x1, H(c + 1, r + 1) + top, z1);
        var nw = new Vector3(x0, H(c, r + 1) + top, z1);
        Quad(sw, se, ne, nw, col);
        // walls: south (r-1), east (c+1), north (r+1), west (c-1)
        Wall(TopOf(_looks[LookIndex(c, r - 1)]), top, sw, se, wall);
        Wall(TopOf(_looks[LookIndex(c + 1, r)]), top, se, ne, wall);
        Wall(TopOf(_looks[LookIndex(c, r + 1)]), top, ne, nw, wall);
        Wall(TopOf(_looks[LookIndex(c - 1, r)]), top, nw, sw, wall);

        if (_lod == 0 && l.Crop == CropKind.Sunflower && l.Growth > 0.6f)
        {
            // four heads a cell, small plates just over the top
            var head = Shade(l.Stage == FieldStage.Ripe ? SunRipe : SunHead, l.Jitter);
            for (int k = 0; k < 4; k++)
            {
                float hx = x0 + (k % 2 + 0.5f) * Cs / 2, hz = z0 - (k / 2 + 0.5f) * Cs / 2;
                float y = GroundAt(hx, hz) + top + 0.12f;
                const float s = 0.45f;
                Quad(new Vector3(hx - s, y, hz + s), new Vector3(hx + s, y, hz + s), new Vector3(hx + s, y, hz - s), new Vector3(hx - s, y, hz - s), head);
            }
        }
    }

    /// <summary>A wall under the top edge a..b down to the neighbour's top (the ground when it has none).</summary>
    private void Wall(float neighbour, float top, Vector3 a, Vector3 b, Color col)
    {
        if (neighbour >= top - 0.05f) return;
        float drop = top - neighbour;
        var a0 = a with { Y = a.Y - drop };
        var b0 = b with { Y = b.Y - drop };
        Quad(a0, b0, b, a, col);
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
    {
        Verts.Add(a); Verts.Add(c); Verts.Add(b);
        Verts.Add(a); Verts.Add(d); Verts.Add(c);
        for (int i = 0; i < 6; i++) Colors.Add(col);
    }
}
