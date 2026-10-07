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
    /// <summary>The owning field: <see cref="FieldTile.Owner"/> (index + 1), 0 off every field.</summary>
    public ushort Owner;
}

/// <summary>
/// Builds the mesh of one 100 m drawing chunk of fields (#494, <c>docs/notes/farming/fields-runtime.md</c>)
/// on a worker: plain arrays, no Godot object. Draped on the ground the terrain draws
/// (<c>ChunkGrid.SampleMeshHeight</c>), flat triangles, vertex colours raw linear with alpha 0 (the
/// prop shader reads alpha as a lamp mask).
/// <para>
/// The cells the field's outline crosses are cut to it (<see cref="FieldClip.Cut"/>), the others
/// stay whole squares, so a crop's border follows the parcel, not the 4 m grid. Rows (furrows,
/// sprouts, stubble, potato ridges, maize rows, sunflower heads) run along the field's long axis,
/// continuous from cell to cell. Heights and colour jitter are shared by the cell corners, so a
/// field's top has no seams; each field gets its own tint from its id.
/// </para>
/// Two details: <c>lod 0</c> near the camera (rows, heads, banded sides), <c>lod 1</c> further out
/// (cover and plain crop slabs). Untouched grass is not drawn: the terrain already is grass.
/// </summary>
public sealed class FieldMeshBuilder
{
    public const int Side = FieldTile.ChunkCells;
    /// <summary>The look array is the chunk plus two cells all round (neighbours decide walls and edge slivers).</summary>
    public const int Ring = 2;
    public const int LookSide = Side + 2 * Ring;
    private const float Cs = FieldFormat.CellSize;
    private const float Lift = 0.10f;
    private const float Sink = 0.15f;
    private const int MaxFields = 64;

    private readonly CellLook[] _looks;
    private readonly FieldTile _data;
    private readonly int _c0, _r0;
    private readonly Func<double, double, double> _ground;
    private readonly double _e0, _n0;
    private readonly int _lod;
    private readonly float[] _corner = new float[(Side + 1) * (Side + 1)];
    private readonly long _gx, _gy;

    public readonly List<Vector3> Verts = new();
    public readonly List<Color> Colors = new();
    /// <summary>Triangles into <see cref="Verts"/>: corners shared by neighbouring quads (same place, same colour) are one vertex.</summary>
    public readonly List<int> Indices = new();
    private readonly Dictionary<(Vector3, Color), int> _shared = new();

    /// <summary>A field seen by this chunk: its outline near the chunk, its rows, its tint.</summary>
    private sealed class Field
    {
        public ushort Owner;
        public readonly List<OutlineSeg> Stripe = new();
        public Vector2 Along, Across;
        public float RowPhase;
        public float Tint, Warm;
    }

    private readonly List<Field> _fields = new();
    private readonly int[] _localOf;
    private readonly ulong[] _edge = new ulong[Side * Side];
    private readonly List<FieldPiece> _pieces = new();
    private readonly List<OutlineSeg> _cellStripe = new();
    private readonly List<float> _xs = new();
    private readonly List<(float, int)> _hits = new();

    /// <param name="looks"><see cref="LookSide"/>² cells, row-major from the south-west, the chunk at (Ring, Ring).</param>
    /// <param name="c0">The chunk's south-west cell in its tile.</param>
    /// <param name="e0">The chunk's south-west corner, LV95.</param>
    public FieldMeshBuilder(CellLook[] looks, FieldTile data, int c0, int r0, Func<double, double, double> ground, double e0, double n0, int lod)
    {
        _looks = looks;
        _data = data;
        _c0 = c0;
        _r0 = r0;
        _ground = ground;
        _e0 = e0;
        _n0 = n0;
        _lod = lod;
        _gx = (long)Math.Round(e0 / Cs);
        _gy = (long)Math.Round(n0 / Cs);
        _localOf = new int[data.Fields.Count + 1];
    }

    public static int LookIndex(int col, int row) => (row + Ring) * LookSide + col + Ring;

    private ref readonly CellLook LookAt(int c, int r) => ref _looks[LookIndex(c, r)];

    public void Build()
    {
        for (int r = 0; r <= Side; r++)
            for (int c = 0; c <= Side; c++)
                _corner[r * (Side + 1) + c] = (float)_ground(_e0 + c * Cs, _n0 + r * Cs);
        GatherFields();
        for (int r = 0; r < Side; r++)
            for (int c = 0; c < Side; c++)
                Cell(c, r);
    }

    // ---- the fields round the chunk ---------------------------------------------------------------

    private void GatherFields()
    {
        Array.Fill(_localOf, -1);
        foreach (var l in _looks)
        {
            if (l.Owner == 0 || _localOf[l.Owner] >= 0) continue;
            _localOf[l.Owner] = _fields.Count;
            _fields.Add(MakeField(l.Owner));
        }
        // which cells each outline crosses (only the first MaxFields fields: the rest stay squares)
        for (int f = 0; f < _fields.Count && f < MaxFields; f++)
        {
            ulong bit = 1UL << f;
            foreach (var s in _fields[f].Stripe)
            {
                int ca = Math.Max(0, (int)MathF.Floor(s.MinU / Cs)), cb = Math.Min(Side - 1, (int)MathF.Floor(s.MaxU / Cs));
                int ra = Math.Max(0, (int)MathF.Floor(s.MinV / Cs)), rb = Math.Min(Side - 1, (int)MathF.Floor(s.MaxV / Cs));
                for (int r = ra; r <= rb; r++)
                    for (int c = ca; c <= cb; c++)
                        if ((_edge[r * Side + c] & bit) == 0 && FieldClip.ClipToSquare(s.P, s.Q, c * Cs, r * Cs, (c + 1) * Cs, (r + 1) * Cs, out _, out _))
                            _edge[r * Side + c] |= bit;
            }
        }
    }

    private Field MakeField(ushort owner)
    {
        var poly = _data.Fields[owner - 1];
        var f = new Field { Owner = owner };
        float ou = _c0 * Cs, ov = _r0 * Cs;
        const float margin = 2 * Cs;
        // the long axis: the outline's direction weighted by length (second moment of its edges)
        double sxx = 0, syy = 0, sxy = 0;
        for (int ri = 0; ri < poly.Rings.Count; ri++)
        {
            var ring = poly.Rings[ri];
            int n = ring.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var p = new Vector2(ring[j * 2] - ou, ring[j * 2 + 1] - ov);
                var q = new Vector2(ring[i * 2] - ou, ring[i * 2 + 1] - ov);
                var d = q - p;
                if (d.LengthSquared() < 1e-6f) continue;
                if (ri == 0)
                {
                    double len = d.Length();
                    sxx += d.X * d.X / len; syy += d.Y * d.Y / len; sxy += d.X * d.Y / len;
                }
                if (Math.Max(p.X, q.X) < -margin || Math.Min(p.X, q.X) > Side * Cs + margin) continue;
                f.Stripe.Add(new OutlineSeg(p, q));
            }
        }
        float angle = 0.5f * (float)Math.Atan2(2 * sxy, sxx - syy);
        f.Along = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        f.Across = new Vector2(-f.Along.Y, f.Along.X);
        uint h = Mix(poly.Id);
        // rows are laid out in tile metres, so they carry on into the next chunk
        f.RowPhase = (h & 0xff) / 255f * 8f - new Vector2(ou, ov).Dot(f.Across);
        f.Tint = 0.90f + ((h >> 8) & 0xff) / 255f * 0.20f;
        f.Warm = ((h >> 16) & 0xff) / 255f * 2f - 1f;
        return f;
    }

    // ---- one cell ---------------------------------------------------------------------------------

    private void Cell(int c, int r)
    {
        ref readonly var own = ref LookAt(c, r);
        ulong mask = _edge[r * Side + c];
        int ownLocal = own.Owner > 0 ? _localOf[own.Owner] : -1;
        bool ownCut = ownLocal >= 0 && ownLocal < MaxFields && (mask & (1UL << ownLocal)) != 0;
        if (own.Owner > 0 && !ownCut)
        {
            // wholly inside its field: one square
            _pieces.Clear();
            _pieces.Add(FieldClip.Square(c * Cs, r * Cs, (c + 1) * Cs, (r + 1) * Cs));
            Draw(c, r, _fields[ownLocal], own);
            return;
        }
        // cut to every outline that crosses it (its own, and a neighbour's that spills into it)
        for (int f = 0; f < _fields.Count && f < MaxFields; f++)
        {
            if ((mask & (1UL << f)) == 0) continue;
            var fl = _fields[f];
            if (!LookFor(c, r, fl.Owner, out var look)) continue;
            float u0 = c * Cs, v0 = r * Cs, u1 = u0 + Cs, v1 = v0 + Cs;
            _cellStripe.Clear();
            foreach (var s in fl.Stripe)
                if (s.MaxU > u0 && s.MinU < u1) _cellStripe.Add(s);
            _pieces.Clear();
            FieldClip.Cut(u0, v0, u1, v1, _cellStripe, _pieces, _xs, _hits);
            if (_pieces.Count > 0) Draw(c, r, fl, look);
        }
    }

    /// <summary>The look of <paramref name="owner"/>'s crop at a cell: the cell's own, else a neighbour's of that field.</summary>
    private bool LookFor(int c, int r, ushort owner, out CellLook look)
    {
        look = default;
        if (c < -Ring || r < -Ring || c >= Side + Ring || r >= Side + Ring) return false;
        if (LookAt(c, r).Owner == owner) { look = LookAt(c, r); return true; }
        ReadOnlySpan<int> dc = stackalloc int[] { -1, 1, 0, 0, -1, 1, -1, 1 };
        ReadOnlySpan<int> dr = stackalloc int[] { 0, 0, -1, 1, -1, -1, 1, 1 };
        for (int k = 0; k < 8; k++)
        {
            int cc = c + dc[k], rr = r + dr[k];
            if (cc < -Ring || rr < -Ring || cc >= Side + Ring || rr >= Side + Ring) continue;
            if (_looks[LookIndex(cc, rr)].Owner == owner) { look = _looks[LookIndex(cc, rr)]; return true; }
        }
        return false;
    }

    // ---- what a crop looks like -------------------------------------------------------------------

    /// <summary>Ripe height of a standing crop drawn as a slab (0: drawn as rows or cover).</summary>
    public static float SlabHeight(CropKind crop) => crop switch
    {
        CropKind.Wheat => 0.95f,
        CropKind.Barley => 0.8f,
        CropKind.OtherArable => 0.8f,
        CropKind.Maize => 2.4f,
        CropKind.Rapeseed => 1.3f,
        CropKind.Sunflower => 1.6f,
        CropKind.Legumes => 0.7f,
        _ => 0f,
    };

    private static bool RowCrop(CropKind crop) => crop is CropKind.Potato or CropKind.SugarBeet or CropKind.Vegetables;

    private static bool Standing(FieldStage s) => s is FieldStage.Growing or FieldStage.Ripe;

    /// <summary>The top of a cell's crop slab above the ground, 0 when it has none.</summary>
    public static float TopOf(in CellLook l)
    {
        if (l.Owner == 0 || !Standing(l.Stage)) return 0f;
        float h = SlabHeight(l.Crop);
        if (h <= 0) return 0f;
        float g = l.Stage == FieldStage.Ripe ? 1f : l.Growth;
        return h * (0.25f + 0.75f * g);
    }

    private static Color Lin(float r, float g, float b) => new Color(r, g, b).SrgbToLinear() with { A = 0f };

    private static readonly Color Soil = Lin(0.42f, 0.30f, 0.19f), SoilDark = Lin(0.32f, 0.23f, 0.15f), SoilRidge = Lin(0.50f, 0.37f, 0.24f);
    private static readonly Color Straw = Lin(0.80f, 0.71f, 0.45f), StubbleGround = Lin(0.55f, 0.46f, 0.30f);
    private static readonly Color Sprout = Lin(0.45f, 0.66f, 0.22f), Leaf = Lin(0.27f, 0.50f, 0.18f), LeafDark = Lin(0.17f, 0.34f, 0.12f);
    private static readonly Color Cut = Lin(0.55f, 0.66f, 0.30f), Hay = Lin(0.74f, 0.73f, 0.40f), Grass = Lin(0.36f, 0.58f, 0.22f);
    private static readonly Color Gold = Lin(0.88f, 0.71f, 0.32f), Pale = Lin(0.80f, 0.70f, 0.42f), StalkDry = Lin(0.58f, 0.47f, 0.26f);
    private static readonly Color MaizeGreen = Lin(0.24f, 0.46f, 0.16f), MaizeTop = Lin(0.30f, 0.50f, 0.19f), MaizeDry = Lin(0.74f, 0.64f, 0.40f), Tassel = Lin(0.66f, 0.62f, 0.34f);
    private static readonly Color RapeYellow = Lin(0.98f, 0.88f, 0.14f), RapeRipe = Lin(0.50f, 0.44f, 0.25f);
    private static readonly Color SunHead = Lin(0.98f, 0.76f, 0.08f), SunCentre = Lin(0.30f, 0.20f, 0.10f), SunRipe = Lin(0.38f, 0.28f, 0.15f);
    private static readonly Color Tan = Lin(0.72f, 0.64f, 0.40f), Yellowing = Lin(0.60f, 0.58f, 0.27f);

    /// <summary>The colour of a standing crop's top.</summary>
    public static Color CropColour(CropKind crop, FieldStage stage, float growth)
    {
        float ripe = stage == FieldStage.Ripe ? 1f : Mathf.Clamp((growth - 0.6f) / 0.4f, 0f, 1f);
        return crop switch
        {
            CropKind.Wheat or CropKind.OtherArable => Leaf.Lerp(Gold, ripe),
            CropKind.Barley => Leaf.Lerp(Pale, ripe),
            CropKind.Maize => stage == FieldStage.Ripe ? MaizeDry : MaizeTop,
            CropKind.Rapeseed => stage == FieldStage.Ripe ? RapeRipe : growth > 0.45f ? RapeYellow : Leaf,
            CropKind.Sunflower => stage == FieldStage.Ripe ? MaizeDry.Darkened(0.25f) : MaizeGreen,
            CropKind.Legumes => Leaf.Lerp(Tan, ripe),
            CropKind.Potato or CropKind.SugarBeet or CropKind.Vegetables => stage == FieldStage.Ripe && crop == CropKind.Potato ? Yellowing : Leaf,
            _ => Grass,
        };
    }

    /// <summary>The colour of a slab's sides below the ear band: stalks.</summary>
    private static Color StalkColour(CropKind crop, FieldStage stage, float growth)
    {
        float ripe = stage == FieldStage.Ripe ? 1f : Mathf.Clamp((growth - 0.6f) / 0.4f, 0f, 1f);
        return crop switch
        {
            CropKind.Maize => stage == FieldStage.Ripe ? StalkDry : LeafDark,
            CropKind.Rapeseed or CropKind.Sunflower => stage == FieldStage.Ripe ? StalkDry.Darkened(0.2f) : LeafDark,
            _ => LeafDark.Lerp(StalkDry, ripe),
        };
    }

    private void Draw(int c, int r, Field f, in CellLook l)
    {
        bool near = _lod == 0;
        switch (l.Stage)
        {
            case FieldStage.Ploughed:
                Cover(f, Soil);
                if (near) Rows(f, 1.6f, 0.24f, 0.75f, SoilRidge, 0f);
                break;
            case FieldStage.Sown:
                Cover(f, SoilDark.Lerp(Sprout, 0.25f * l.Growth));
                if (!near) break;
                if (l.Growth > 0.05f) Rows(f, 1.0f, 0.06f + 0.22f * l.Growth, 0.18f + 0.12f * l.Growth, Sprout, 0f);
                else Rows(f, 1.6f, 0.14f, 0.7f, SoilRidge.Darkened(0.1f), 0f);
                break;
            case FieldStage.Stubble:
                Cover(f, StubbleGround);
                if (near) Rows(f, 1.0f, 0.12f, 0.2f, Straw, 0f);
                break;
            case FieldStage.Mown:
                Cover(f, Cut);
                if (near && l.Growth < 0.5f) Rows(f, 4f, 0.35f, 0.65f, Hay, 0f);
                break;
            case FieldStage.Grass:
                // untouched grass is the terrain's own; regrown grass looks the same
                break;
            case FieldStage.Growing:
            case FieldStage.Ripe:
                if (RowCrop(l.Crop))
                {
                    var leaf = CropColour(l.Crop, l.Stage, l.Growth);
                    float g = l.Stage == FieldStage.Ripe ? 1f : l.Growth;
                    float h = (l.Crop == CropKind.SugarBeet ? 0.55f : 0.45f) * (0.35f + 0.65f * g);
                    if (near)
                    {
                        Cover(f, SoilDark);
                        LeafRows(f, 2f, h, 0.55f + 0.3f * g, leaf);
                    }
                    else Cover(f, leaf.Lerp(SoilDark, 0.35f - 0.25f * g));
                }
                else Slab(c, r, f, l);
                break;
        }
    }

    // ---- covers, slabs, rows ----------------------------------------------------------------------

    /// <summary>The footprint, lifted just over the ground.</summary>
    private void Cover(Field f, Color col)
    {
        col = Tinted(f, col);
        foreach (var p in _pieces)
            Quad(At(p.A, Lift), At(p.B, Lift), At(p.C, Lift), At(p.D, Lift), Jit(col, p.A), Jit(col, p.B), Jit(col, p.C), Jit(col, p.D));
    }

    /// <summary>
    /// A standing crop: the footprint raised to its height (a gently jittered top), sides down to
    /// the ground along the outline and towards lower neighbours, a darker ear band at the top.
    /// </summary>
    private void Slab(int c, int r, Field f, in CellLook l)
    {
        float top = TopOf(l);
        float amp = l.Crop == CropKind.Maize ? 0.10f : 0.07f;
        var col = Tinted(f, CropColour(l.Crop, l.Stage, l.Growth));
        var stalk = Tinted(f, StalkColour(l.Crop, l.Stage, l.Growth));
        _teeth = _lod == 0 ? (l.Crop switch { CropKind.Maize => 0.35f, CropKind.Sunflower => 0f, CropKind.Rapeseed => 0.10f, _ => 0.13f }) * top / SlabHeight(l.Crop) : 0f;
        _tip = l.Crop == CropKind.Maize ? Tinted(f, l.Stage == FieldStage.Ripe ? MaizeDry : Tassel) : (col * 1.12f) with { A = 0f };
        float band = _lod == 0 ? MathF.Min(top * 0.4f, l.Crop == CropKind.Maize ? 0.7f : l.Crop == CropKind.Rapeseed ? 0.4f : 0.28f) : 0f;
        var bandCol = (col * 0.72f) with { A = 0f };
        foreach (var p in _pieces)
        {
            Quad(Top(p.A, top, amp), Top(p.B, top, amp), Top(p.C, top, amp), Top(p.D, top, amp), Jit(col, p.A), Jit(col, p.B), Jit(col, p.C), Jit(col, p.D));
            // the cell's own sides: a wall where the neighbour stands lower
            if ((p.Sides & FieldPiece.South) != 0) SideWall(c, r - 1, f, top, p.A, p.B, amp, band, bandCol, stalk);
            if ((p.Sides & FieldPiece.East) != 0) SideWall(c + 1, r, f, top, p.B, p.C, amp, band, bandCol, stalk);
            if ((p.Sides & FieldPiece.North) != 0) SideWall(c, r + 1, f, top, p.C, p.D, amp, band, bandCol, stalk);
            if ((p.Sides & FieldPiece.West) != 0) SideWall(c - 1, r, f, top, p.D, p.A, amp, band, bandCol, stalk);
        }
        // the outline: a wall down to the ground wherever it crosses the cell
        float u0 = c * Cs, v0 = r * Cs;
        if (_pieces.Count > 0 && !(_pieces.Count == 1 && _pieces[0].Sides == 15))
            foreach (var s in f.Stripe)
                if (s.MaxU > u0 && s.MinU < u0 + Cs && s.MaxV > v0 && s.MinV < v0 + Cs
                    && FieldClip.ClipToSquare(s.P, s.Q, u0, v0, u0 + Cs, v0 + Cs, out var a, out var b) && (b - a).LengthSquared() > 1e-4f)
                    Wall(a, b, top, amp, 0f, band, bandCol, stalk);

        if (_lod == 0)
        {
            var ridge = (CropColour(l.Crop, l.Stage, l.Growth) * 1.1f) with { A = 0f };
            float g = l.Stage == FieldStage.Ripe ? 1f : l.Growth;
            switch (l.Crop)
            {
                case CropKind.Maize: Rows(f, 1.5f, 0.35f, 0.6f, ridge, top, amp); break;
                case CropKind.Rapeseed: Rows(f, 1.6f, 0.18f, 0.8f, ridge, top, amp, saw: true); break;
                case CropKind.Sunflower: break;
                // drill rows, and the tramlines the sprayer drives in, every 18 m
                default: Rows(f, 1.5f, 0.06f + 0.09f * g, 0.75f, ridge, top, amp, tram: 12, saw: true); break;
            }
        }
        if (_lod == 0 && l.Crop == CropKind.Sunflower && (l.Growth > 0.6f || l.Stage == FieldStage.Ripe))
            Heads(f, top, amp, l.Stage == FieldStage.Ripe);
    }

    private void SideWall(int nc, int nr, Field f, float top, Vector2 a, Vector2 b, float amp, float band, Color bandCol, Color stalk)
    {
        float below = LookFor(nc, nr, f.Owner, out var n) ? TopOf(n) : 0f;
        if (below >= top - 0.05f) return;
        Wall(a, b, top, amp, below, band, bandCol, stalk);
    }

    /// <summary>A side under the top edge a..b, from the top down to <paramref name="below"/> (0: into the ground).</summary>
    private void Wall(Vector2 a, Vector2 b, float top, float amp, float below, float band, Color bandCol, Color stalk)
    {
        var ta = Top(a, top, amp);
        var tb = Top(b, top, amp);
        float ga = Ground(a) + (below > 0 ? below : -Sink), gb = Ground(b) + (below > 0 ? below : -Sink);
        var foot = stalk.Darkened(0.35f) with { A = 0f };
        if (band > 0 && ta.Y - band > ga && tb.Y - band > gb)
        {
            var ma = ta with { Y = ta.Y - band };
            var mb = tb with { Y = tb.Y - band };
            Quad(ma, mb, tb, ta, bandCol, bandCol, bandCol, bandCol);
            Quad(ta with { Y = ga }, tb with { Y = gb }, mb, ma, foot, foot, stalk, stalk);
        }
        else Quad(ta with { Y = ga }, tb with { Y = gb }, tb, ta, foot, foot, stalk, stalk);
        if (_teeth <= 0) return;
        // maize: tassels along the top edge, a ragged skyline instead of a ruler-straight one
        int n = Math.Max(1, (int)MathF.Round((b - a).Length() / 0.5f));
        var tip = _tip;
        for (int i = 0; i < n; i++)
        {
            var p = ta.Lerp(tb, (float)i / n);
            var q = ta.Lerp(tb, (float)(i + 1) / n);
            var m = (p + q) * 0.5f;
            float up = _teeth * (0.6f + 0.4f * MathF.Abs(Corner((int)(m.X * 3f), (int)(m.Z * 3f), 0x7a11u)));
            Tri(p, q, m with { Y = m.Y + up }, bandCol, bandCol, tip);
        }
    }

    private float _teeth;
    private Color _tip;

    /// <summary>
    /// Ridges along the field's rows, <paramref name="spacing"/> apart, cut to the footprint: a roof
    /// of two slopes <paramref name="height"/> high, <paramref name="half"/> either side of the row.
    /// On a slab when <paramref name="top"/> &gt; 0.
    /// </summary>
    private void Rows(Field f, float spacing, float height, float half, Color col, float top, float amp = 0f, int tram = 0, bool saw = false)
    {
        col = Tinted(f, col);
        var dark = col.Darkened(0.25f) with { A = 0f };
        var track = col.Darkened(0.45f) with { A = 0f };
        foreach (var p in _pieces)
        {
            if (!RowRange(f, p, spacing, out int k0, out int k1)) continue;
            for (int k = k0; k <= k1; k++)
            {
                var o = f.Across * (f.RowPhase + k * spacing);
                if (!FieldClip.LineInPiece(p, o, f.Along, out float t0, out float t1) || t1 - t0 < 0.05f) continue;
                var a = o + f.Along * t0;
                var b = o + f.Along * t1;
                var side = f.Across * MathF.Min(half, spacing * 0.5f);
                Vector3 P(Vector2 q, float up) => top > 0 ? Top(q, top, amp) + new Vector3(0, up, 0) : At(q, Lift + up);
                if (tram > 0 && ((k % tram) + tram) % tram < 2)
                {
                    // a tramline: a pressed-down wheel track instead of a ridge
                    var w = f.Across * 0.25f;
                    Quad(P(a - w, 0.05f), P(b - w, 0.05f), P(b + w, 0.05f), P(a + w, 0.05f), track, track, track, track);
                    continue;
                }
                var (fa0, fb0) = Foot(p, o - side, f.Along, t0, t1); var (fa1, fb1) = Foot(p, o + side, f.Along, t0, t1);
                var a0 = P(fa0, 0); var b0 = P(fb0, 0);
                var at = P(a, height); var bt = P(b, height);
                var a1 = P(fa1, 0); var b1 = P(fb1, 0);
                if (saw)
                {
                    // one sloped face a row: a sawtooth, half the vertices of a roof
                    var c1 = P(fa1, height); var d1 = P(fb1, height);
                    Quad(a0, b0, d1, c1, dark, dark, col, col);
                    continue;
                }
                Quad(a0, b0, bt, at, dark, dark, col, col);
                Quad(at, bt, b1, a1, col, col, col.Darkened(0.1f) with { A = 0f }, col.Darkened(0.1f) with { A = 0f });
            }
        }
    }

    /// <summary>Potato / beet ridges: soil flanks with a leafy crown over them.</summary>
    private void LeafRows(Field f, float spacing, float height, float half, Color leaf)
    {
        leaf = Tinted(f, leaf);
        var soil = Tinted(f, SoilRidge);
        var leafDark = leaf.Darkened(0.3f) with { A = 0f };
        foreach (var p in _pieces)
        {
            if (!RowRange(f, p, spacing, out int k0, out int k1)) continue;
            for (int k = k0; k <= k1; k++)
            {
                var o = f.Across * (f.RowPhase + k * spacing);
                if (!FieldClip.LineInPiece(p, o, f.Along, out float t0, out float t1) || t1 - t0 < 0.05f) continue;
                var a = o + f.Along * t0;
                var b = o + f.Along * t1;
                var foot = f.Across * MathF.Min(spacing * 0.5f, half * 1.4f);
                var crown = f.Across * half;
                float sh = height * 0.45f;
                var (pa0, pb0) = Foot(p, o - foot, f.Along, t0, t1); var (pa1, pb1) = Foot(p, o + foot, f.Along, t0, t1);
                var (sa0, sb0) = Foot(p, o - crown, f.Along, t0, t1); var (sa1, sb1) = Foot(p, o + crown, f.Along, t0, t1);
                var a0 = At(pa0, Lift); var b0 = At(pb0, Lift);
                var a1 = At(pa1, Lift); var b1 = At(pb1, Lift);
                var as0 = At(sa0, Lift + sh); var bs0 = At(sb0, Lift + sh);
                var as1 = At(sa1, Lift + sh); var bs1 = At(sb1, Lift + sh);
                var at = At(a, Lift + height); var bt = At(b, Lift + height);
                // soil flank one side, the leaves over the ridge, flank the other side
                Quad(a0, b0, bs0, as0, soil, soil, leafDark, leafDark);
                Quad(as0, bs0, bt, at, leafDark, leafDark, leaf, leaf);
                Quad(at, bt, bs1, as1, leaf, leaf, leafDark, leafDark);
                Quad(as1, bs1, b1, a1, leafDark, leafDark, soil, soil);
            }
        }
    }

    /// <summary>Sunflower heads on the rows, a yellow plate with a dark heart, just over the top.</summary>
    private void Heads(Field f, float top, float amp, bool ripe)
    {
        const float spacing = 1.8f, s = 0.42f;
        var petal = Tinted(f, ripe ? SunRipe : SunHead);
        var heart = ripe ? SunRipe.Darkened(0.4f) with { A = 0f } : SunCentre;
        foreach (var p in _pieces)
        {
            if (!RowRange(f, p, spacing, out int k0, out int k1)) continue;
            float a0 = Math.Min(Math.Min(p.A.Dot(f.Along), p.B.Dot(f.Along)), Math.Min(p.C.Dot(f.Along), p.D.Dot(f.Along)));
            float a1 = Math.Max(Math.Max(p.A.Dot(f.Along), p.B.Dot(f.Along)), Math.Max(p.C.Dot(f.Along), p.D.Dot(f.Along)));
            for (int k = k0; k <= k1; k++)
                for (int j = (int)MathF.Ceiling(a0 / spacing - (k & 1) * 0.5f); j <= (int)MathF.Floor(a1 / spacing - (k & 1) * 0.5f); j++)
                {
                    var q = f.Across * (f.RowPhase + k * spacing) + f.Along * ((j + (k & 1) * 0.5f) * spacing);
                    if (!FieldClip.Inside(p, q)) continue;
                    var y = Top(q, top, amp).Y + 0.18f;
                    Vector3 V(Vector2 d, float up) => new(q.X + d.X, y + up, -(q.Y + d.Y));
                    var da = f.Along * s; var dx = f.Across * s;
                    // tilted a little: the side towards the row's start lower
                    Quad(V(-da - dx, -0.12f), V(da - dx, 0.12f), V(da + dx, 0.12f), V(-da + dx, -0.12f), petal, petal, petal, petal);
                    da *= 0.45f; dx *= 0.45f;
                    Quad(V(-da - dx, -0.03f), V(da - dx, 0.08f), V(da + dx, 0.08f), V(-da + dx, -0.03f), heart, heart, heart, heart);
                }
        }
    }

    /// <summary>
    /// A ridge's foot line cut to the piece on its own, so the ridge ends on the piece's edge (no
    /// spike over the outline, and the same cut the next cell makes); the ridge line's span if it misses.
    /// </summary>
    private static (Vector2, Vector2) Foot(in FieldPiece p, Vector2 o, Vector2 along, float t0, float t1)
    {
        if (FieldClip.LineInPiece(p, o, along, out float s0, out float s1)) { t0 = s0; t1 = s1; }
        return (o + along * t0, o + along * t1);
    }

    private static bool RowRange(Field f, in FieldPiece p, float spacing, out int k0, out int k1)
    {
        float a = p.A.Dot(f.Across), b = p.B.Dot(f.Across), c = p.C.Dot(f.Across), d = p.D.Dot(f.Across);
        float lo = Math.Min(Math.Min(a, b), Math.Min(c, d)), hi = Math.Max(Math.Max(a, b), Math.Max(c, d));
        k0 = (int)MathF.Ceiling((lo - f.RowPhase) / spacing);
        k1 = (int)MathF.Floor((hi - f.RowPhase) / spacing);
        return k1 >= k0;
    }

    // ---- ground, noise, colour --------------------------------------------------------------------

    private float H(int c, int r) => _corner[r * (Side + 1) + c];

    /// <summary>Ground under a chunk-local point (u east, v north), bilinear over the cell corners.</summary>
    private float Ground(Vector2 p)
    {
        float u = Math.Clamp(p.X / Cs, 0, Side - 0.001f), v = Math.Clamp(p.Y / Cs, 0, Side - 0.001f);
        int c = (int)u, r = (int)v;
        float fu = u - c, fv = v - r;
        float a = H(c, r) + (H(c + 1, r) - H(c, r)) * fu;
        float b = H(c, r + 1) + (H(c + 1, r + 1) - H(c, r + 1)) * fu;
        return a + (b - a) * fv;
    }

    private Vector3 At(Vector2 p, float up) => new(p.X, Ground(p) + up, -p.Y);

    private Vector3 Top(Vector2 p, float top, float amp) => new(p.X, Ground(p) + top * (1f + amp * Noise(p, 0x9e37u)), -p.Y);

    /// <summary>Smooth noise in -1..1, from a hash at each world cell corner (shared by every cell, every chunk).</summary>
    private float Noise(Vector2 p, uint seed)
    {
        float u = p.X / Cs, v = p.Y / Cs;
        int c = (int)MathF.Floor(u), r = (int)MathF.Floor(v);
        float fu = u - c, fv = v - r;
        float n00 = Corner(c, r, seed), n10 = Corner(c + 1, r, seed), n01 = Corner(c, r + 1, seed), n11 = Corner(c + 1, r + 1, seed);
        float a = n00 + (n10 - n00) * fu, b = n01 + (n11 - n01) * fu;
        return a + (b - a) * fv;
    }

    private float Corner(int c, int r, uint seed) =>
        (Mix((uint)(_gx + c) * 0x8da6b343u ^ (uint)(_gy + r) * 0xd8163841u ^ seed) & 0xffff) / 32767.5f - 1f;

    private static uint Mix(uint h)
    {
        h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
        return h;
    }

    /// <summary>The field's own shade: a little lighter or darker, a little warmer or greener.</summary>
    private static Color Tinted(Field f, Color c) =>
        new Color(c.R * f.Tint * (1f + 0.07f * f.Warm), c.G * f.Tint, c.B * f.Tint * (1f - 0.07f * f.Warm), 0f);

    private Color Jit(Color c, Vector2 p)
    {
        float k = 1f + 0.06f * Noise(p, 0x51edu);
        return new Color(c.R * k, c.G * k, c.B * k, 0f);
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color ca, Color cb, Color cc, Color cd)
    {
        int ia = V(a, ca), ib = V(b, cb), ic = V(c, cc), id = V(d, cd);
        Indices.Add(ia); Indices.Add(ic); Indices.Add(ib);
        Indices.Add(ia); Indices.Add(id); Indices.Add(ic);
    }

    private void Tri(Vector3 a, Vector3 b, Vector3 c, Color ca, Color cb, Color cc)
    {
        Indices.Add(V(a, ca)); Indices.Add(V(c, cc)); Indices.Add(V(b, cb));
    }

    private int V(Vector3 p, Color c)
    {
        if (_shared.TryGetValue((p, c), out int i)) return i;
        i = Verts.Count;
        Verts.Add(p);
        Colors.Add(c);
        _shared.Add((p, c), i);
        return i;
    }
}
