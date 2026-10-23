using System.Globalization;
using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Terrain;

/// <summary>
/// The debug menu's lane plan (#1181, <c>--debugview lanes</c>): the junction planner's cross-sections seen from above. Every
/// street, transition piece and junction core near the camera is drawn as its strips, coloured by kind (<see cref="Kind"/>,
/// the legend top right); the arriving lanes' moves as arrows near their mouth; at each transition's mouth and each <c>LANE</c> approach a label of its strips
/// left to right as the arriving driver sees them (<c>walk | path | verge ‖ out | L | T | TR | bTR ‖ walk</c>) under the
/// <c>LANE</c> record's own line (<c>L | bL | T | R | bTR</c>, as the test region's log).
///
/// <para>
/// It reads what the game loads: streets through <see cref="SegmentSections"/> (a <c>TRNS</c> piece's strips straight from
/// its <see cref="RoadTransition"/>, an ordinary segment's from its attributes: carriageway lanes by count and way, painted
/// bike lanes, a pocket's widening, the side's profile), the junction caps, <c>LANE</c> (<see cref="RoadApproach"/>), embedded
/// tram tracks and parking bays. A plain street's lane split is the class's guess where the tile names no lane counts.
/// </para>
///
/// <para>
/// Built once into one <see cref="ArrayMesh"/> when switched on, when the camera has moved 300 m from where it was built, and
/// on an origin shift (from the tiles already loaded, the node back at the identity): nothing is made per frame. Drawn over
/// everything (no depth test), each layer at its own priority: cores, strips, kerbs, arrows.
/// </para>
/// </summary>
public partial class LanePlanDebug : Node3D, IOriginShiftAware
{
    /// <summary>Streets with a point this close to the camera (plan metres) are drawn; labels closer still.</summary>
    private const float Radius = 650f, LabelRadius = 450f;
    private const float MoveToRebuild = 300f;
    private const double PollSeconds = 0.5;
    private const int MaxLabels = 96;
    /// <summary>Labels stand this far out along the arm from its mouth, past the arrows.</summary>
    private const float LabelOut = 14f;
    /// <summary>Above the street's own points, so the drawing reads over the kerbs from any angle.</summary>
    private const float Lift = 0.4f;

    /// <summary>What a strip is drawn as.</summary>
    public enum Kind
    {
        Sidewalk, Paving, Kerb, Buffer, Path, Verge, BikeLane, CarIn, CarOut, CarBoth, Pocket, Hatch, Island, Tram, Core, Parking,
    }

    /// <summary>The colour key, in the legend's order.</summary>
    public static readonly (Kind Kind, string Name, Color Colour)[] Key =
    [
        (Kind.CarIn, "Car lane, arriving at the junction", new Color(0.20f, 0.42f, 0.95f)),
        (Kind.CarOut, "Car lane, leaving it", new Color(0.30f, 0.78f, 0.85f)),
        (Kind.CarBoth, "Car lane, no junction near / both ways", new Color(0.45f, 0.50f, 0.62f)),
        (Kind.Pocket, "Turn pocket (no through move, or opening)", new Color(0.62f, 0.32f, 0.92f)),
        (Kind.BikeLane, "Painted bike lane", new Color(0.98f, 0.84f, 0.12f)),
        (Kind.Path, "Separated bike path", new Color(0.80f, 0.30f, 0.20f)),
        (Kind.Verge, "Verge (grass by the carriageway)", new Color(0.30f, 0.62f, 0.25f)),
        (Kind.Buffer, "Buffer (grass by the sidewalk)", new Color(0.55f, 0.80f, 0.40f)),
        (Kind.Sidewalk, "Sidewalk", new Color(0.86f, 0.84f, 0.78f)),
        (Kind.Paving, "Flush paving", new Color(0.72f, 0.64f, 0.52f)),
        (Kind.Kerb, "Kerb", new Color(0.10f, 0.10f, 0.12f)),
        (Kind.Hatch, "Hatch (striped, no one drives)", new Color(1.00f, 0.62f, 0.78f)),
        (Kind.Island, "Island / refuge (raised)", new Color(1.00f, 0.52f, 0.08f)),
        (Kind.Tram, "Tram track or bed", new Color(0.78f, 0.15f, 0.55f)),
        (Kind.Core, "Junction core", new Color(0.22f, 0.22f, 0.25f)),
        (Kind.Parking, "Parking bay", new Color(0.55f, 0.70f, 0.95f)),
    ];

    private static readonly Color[] Colours = BuildColours();

    private static Color[] BuildColours()
    {
        var c = new Color[Enum.GetValues<Kind>().Length];
        foreach (var (kind, _, colour) in Key) c[(int)kind] = colour;
        return c;
    }

    public static Color ColourOf(Kind k) => Colours[(int)k];

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;

    private bool _on;

    /// <summary>The lane plan is drawn (and its legend shown).</summary>
    public bool On
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            _sincePoll = PollSeconds;
            _built = false;
            if (!value) Clear();
            if (_legend != null) _legend.Visible = value;
        }
    }

    /// <summary>For the checks: what the last build drew.</summary>
    public int StripQuads { get; private set; }
    public int MouthLabels { get; private set; }
    public int Arrows { get; private set; }
    /// <summary>How many times it has been drawn, and whether its legend is on screen.</summary>
    public int Builds { get; private set; }
    public bool LegendShown => _legend is { Visible: true };
    /// <summary>Triangles now in the mesh (0 once switched off).</summary>
    public int Triangles { get; private set; }

    private MeshInstance3D _mesh = null!;
    private ArrayMesh _arrays = null!;
    private CanvasLayer _legendLayer = null!;
    private Control? _legend;
    private readonly List<Label3D> _labels = new();
    private readonly Layer[] _layers = [new(), new(), new(), new()];
    private const int LayerCore = 0, LayerStrips = 1, LayerKerbs = 2, LayerArrows = 3;
    private readonly List<RoadTile> _tiles = new();
    private double _sincePoll = PollSeconds;
    private bool _built, _loading;
    private double _builtE, _builtN;
    private int _generation;

    private sealed class Layer
    {
        public readonly List<Vector3> Points = new();
        public readonly List<Color> Colours = new();
        public void Clear() { Points.Clear(); Colours.Clear(); }
    }

    public LanePlanDebug(ChunkManager chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
        Name = "LanePlanDebug";
    }

    public LanePlanDebug() : this(null!, null!) { }

    public override void _Ready()
    {
        _arrays = new ArrayMesh();
        _mesh = new MeshInstance3D { Mesh = _arrays, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_mesh);
        _legendLayer = new CanvasLayer { Layer = 14 };
        AddChild(_legendLayer);
        _legend = BuildLegend();
        _legend.Visible = _on;
        _legendLayer.AddChild(_legend);
    }

    private static StandardMaterial3D Material(int priority) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        VertexColorIsSrgb = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = true,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        RenderPriority = priority,
    };

    private static readonly StandardMaterial3D[] Materials = [Material(1), Material(2), Material(3), Material(4)];

    public void OnOriginShifted(OriginShift shift)
    {
        // every point is in the old frame: draw it again from the tiles in hand
        if (_on && _built) Build();
    }

    public override void _Process(double delta)
    {
        if (!_on || _loading) return;
        _sincePoll += delta;
        if (_sincePoll < PollSeconds) return;
        _sincePoll = 0;
        if (GetViewport().GetCamera3D() is not { } cam) return;
        var (e, n) = _origin.ToLv95(cam.GlobalPosition);
        if (_built && Math.Abs(e - _builtE) < MoveToRebuild && Math.Abs(n - _builtN) < MoveToRebuild) return;
        _ = Load(e, n);
    }

    /// <summary>The road tiles round (<paramref name="e"/>, <paramref name="n"/>), then the drawing.</summary>
    private async Task Load(double e, double n)
    {
        if (_chunks?.Source is not { } source) return;
        _loading = true;
        int generation = ++_generation;
        try
        {
            var tasks = new List<Task<RoadTile?>>();
            var centre = TileId.FromLv95(e, n);
            for (int de = -1; de <= 1; de++)
                for (int dn = -1; dn <= 1; dn++)
                {
                    var id = new TileId(centre.E + de, centre.N + dn);
                    // only tiles that reach within the radius
                    double ce = Math.Clamp(e, id.MinE, id.MinE + ChunkFormat.TileSizeM), cn = Math.Clamp(n, id.MaxN - ChunkFormat.TileSizeM, id.MaxN);
                    if (Math.Abs(ce - e) > Radius || Math.Abs(cn - n) > Radius) continue;
                    tasks.Add(source.LoadRoadsAsync(id));
                }
            var tiles = await Task.WhenAll(tasks);
            if (!_on || generation != _generation || !IsInsideTree()) return;
            _tiles.Clear();
            foreach (var t in tiles) if (t != null) _tiles.Add(t);
            _builtE = e;
            _builtN = n;
            Build();
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[lanes] loading the road tiles failed: {ex.Message}");
        }
        finally
        {
            _loading = false;
        }
    }

    private void Clear()
    {
        _arrays?.ClearSurfaces();
        Triangles = 0;
        foreach (var l in _labels) l.Visible = false;
        StripQuads = MouthLabels = Arrows = 0;
    }

    // ------------------------------------------------------------------------------------
    // the drawing
    // ------------------------------------------------------------------------------------

    /// <summary>A strip of a street at each sample: its kind, way, moves and its two offsets right of the drawing.</summary>
    private sealed class Strip
    {
        public Kind Kind;
        /// <summary>+1 along the drawing, -1 against, 0 none or both.</summary>
        public sbyte Travel;
        public SignalMoves Moves;
        public bool Carriageway;
        /// <summary>A car lane that opens or closes along the piece (zero wide at one end).</summary>
        public bool Opening;
        public float[] From = [], To = [];
    }

    /// <summary>A junction core's triangles in the build's frame, with their box.</summary>
    private readonly record struct Core(Vector3[] Tris, Rect2 Box, Vector3 Centre);

    /// <summary>A mouth: where a street meets a junction core.</summary>
    /// <summary><paramref name="Planned"/>: a transition's end, as the junction planner wrote it.</summary>
    private readonly record struct Mouth(Vector3 At, Vector3 Outward, string Strips, bool Planned);

    private readonly List<Core> _cores = new();
    private readonly List<Mouth> _mouths = new();

    private void Build()
    {
        _built = true;
        Builds++;
        Transform = Transform3D.Identity;
        foreach (var layer in _layers) layer.Clear();
        _cores.Clear();
        _mouths.Clear();
        _approaches.Clear();
        StripQuads = Arrows = 0;
        var eye = _origin.ToWorld(_builtE, _builtN, 0);
        var near = new Vector2(eye.X, eye.Z);

        foreach (var tile in _tiles)
        {
            var b = _origin.ToWorld(tile.Id.MinE, tile.Id.MaxN, 0);
            foreach (var j in tile.Junctions) AddCore(j, b, near);
        }
        foreach (var tile in _tiles)
        {
            var b = _origin.ToWorld(tile.Id.MinE, tile.Id.MaxN, 0);
            var transitions = SegmentSections.TransitionsOf(tile);
            for (int i = 0; i < tile.Segments.Count; i++)
            {
                var seg = tile.Segments[i];
                if (seg.PointCount < 2 || !Near(seg, b, near)) continue;
                if (seg.Class == RoadClass.Railway)
                {
                    if (seg.Attributes.Has(RoadAttrFlags.Embedded) || seg.Attributes.Has(RoadAttrFlags.PavedBed) || seg.Attributes.Has(RoadAttrFlags.OnStreet))
                        AddTram(seg, b);
                    continue;
                }
                if (!IsStreet(seg.Class)) continue;
                AddStreet(seg, transitions[i], b);
            }
            foreach (var bay in tile.Parking) AddBay(bay, b, near);
        }
        foreach (var tile in _tiles)
        {
            var b = _origin.ToWorld(tile.Id.MinE, tile.Id.MaxN, 0);
            AddApproaches(tile, b, near);
        }
        PlaceLabels(near);
        Upload();
    }

    private static bool IsStreet(RoadClass c) => c is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp or RoadClass.Major
        or RoadClass.Road or RoadClass.Minor or RoadClass.Lane or RoadClass.Link or RoadClass.Square or RoadClass.Unknown;

    private static bool Near(RoadSegment seg, Vector3 b, Vector2 eye)
    {
        var p = seg.Points;
        for (int i = 0; i < seg.PointCount; i++)
            if (new Vector2(b.X + p[i * 3], b.Z + p[i * 3 + 2]).DistanceSquaredTo(eye) < Radius * Radius) return true;
        return false;
    }

    private void AddCore(RoadJunction j, Vector3 b, Vector2 eye)
    {
        var v = j.Vertices;
        if (v.Length < 9) return;
        var first = new Vector2(b.X + v[0], b.Z + v[2]);
        if (first.DistanceSquaredTo(eye) > (Radius + 100) * (Radius + 100)) return;
        var tris = new Vector3[j.Indices.Length];
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        var colour = ColourOf(Kind.Core);
        var layer = _layers[LayerCore];
        for (int k = 0; k < j.Indices.Length; k++)
        {
            int at = j.Indices[k] * 3;
            var p = new Vector3(b.X + v[at], v[at + 1] + Lift, b.Z + v[at + 2]);
            tris[k] = p;
            layer.Points.Add(p);
            layer.Colours.Add(colour);
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
        }
        _cores.Add(new Core(tris, new Rect2(minX, minZ, maxX - minX, maxZ - minZ), new Vector3((minX + maxX) * 0.5f, 0, (minZ + maxZ) * 0.5f)));
    }

    /// <summary>A street end on (or within a metre of) a junction core.</summary>
    private bool OnCore(Vector3 p)
    {
        var q = new Vector2(p.X, p.Z);
        foreach (var c in _cores)
        {
            if (!c.Box.Grow(1.2f).HasPoint(q)) continue;
            for (int k = 0; k + 2 < c.Tris.Length; k += 3)
                if (DistanceToTriangle(q, c.Tris[k], c.Tris[k + 1], c.Tris[k + 2]) < 1.2f) return true;
        }
        return false;
    }

    private static float DistanceToTriangle(Vector2 p, Vector3 a3, Vector3 b3, Vector3 c3)
    {
        Vector2 a = new(a3.X, a3.Z), b = new(b3.X, b3.Z), c = new(c3.X, c3.Z);
        float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        if (!(neg && pos)) return 0;
        return Math.Min(Geometry2D.GetClosestPointToSegment(p, a, b).DistanceTo(p),
            Math.Min(Geometry2D.GetClosestPointToSegment(p, b, c).DistanceTo(p), Geometry2D.GetClosestPointToSegment(p, c, a).DistanceTo(p)));
    }

    private static float Cross(Vector2 u, Vector2 v) => u.X * v.Y - u.Y * v.X;

    // ---- one street

    private void AddStreet(RoadSegment seg, RoadTransition? transition, Vector3 b)
    {
        var at = SegmentSections.Stations(seg);
        float length = at[^1];
        if (length < 0.05f) return;
        // a transition's widths are linear between its own stations: sample at those too
        var samples = new List<float>(at);
        if (transition != null)
            foreach (float s in transition.Stations)
                if (s > 0.01f && s < length - 0.01f) samples.Add(s);
        samples.Sort();
        for (int k = samples.Count - 1; k > 0; k--)
            if (samples[k] - samples[k - 1] < 0.01f) samples.RemoveAt(k);

        int n = samples.Count;
        var centre = new Vector3[n];
        var right = new Vector3[n];
        for (int k = 0; k < n; k++) (centre[k], right[k]) = PointAt(seg, at, samples[k], b);

        var strips = transition != null ? TransitionStrips(transition, samples) : PlainStrips(seg, samples, at);
        bool mouthStart = OnCore(centre[0]), mouthEnd = OnCore(centre[^1]);
        // which way along the drawing the junction this point belongs to lies: the mouth nearer it, else the nearest core
        var towards = new sbyte[n];
        for (int k = 0; k < n; k++)
            towards[k] = mouthStart || mouthEnd
                ? (sbyte)(mouthEnd && (!mouthStart || samples[k] > length * 0.5f) ? 1 : -1)
                : NearestCoreAhead(centre[k], Along(centre, k));

        var layers = _layers;
        foreach (var st in strips)
        {
            bool kerb = st.Kind == Kind.Kerb;
            var layer = layers[kerb ? LayerKerbs : LayerStrips];
            for (int k = 0; k + 1 < n; k++)
            {
                float f0 = st.From[k], t0 = st.To[k], f1 = st.From[k + 1], t1 = st.To[k + 1];
                if (kerb) { f0 -= 0.12f; t0 += 0.12f; f1 -= 0.12f; t1 += 0.12f; }
                if (t0 - f0 < 0.01f && t1 - f1 < 0.01f) continue;
                var c0 = StripColour(st, towards[k]);
                var c1 = StripColour(st, towards[k + 1]);
                Quad(layer, centre[k] + right[k] * f0, centre[k] + right[k] * t0, centre[k + 1] + right[k + 1] * t1, centre[k + 1] + right[k + 1] * f1,
                    c0, c0, c1, c1);
                StripQuads++;
            }
        }

        if (mouthEnd) AddMouth(strips, n - 1, centre[^1], right[^1], -Along(centre, n - 1), +1, transition != null);
        if (mouthStart) AddMouth(strips, 0, centre[0], right[0], Along(centre, 0), -1, transition != null);
    }

    /// <summary>Unit direction along the drawing at sample <paramref name="k"/>.</summary>
    private static Vector3 Along(Vector3[] c, int k)
    {
        int a = Math.Max(0, k - 1), z = Math.Min(c.Length - 1, k + 1);
        var d = c[z] - c[a];
        d.Y = 0;
        return d.LengthSquared() < 1e-8f ? Vector3.Forward : d.Normalized();
    }

    /// <summary>The drawing's point and its right (across the per-vertex bisector at a point, across the piece between) at station <paramref name="s"/>.</summary>
    private static (Vector3 Point, Vector3 Right) PointAt(RoadSegment seg, float[] at, float s, Vector3 b)
    {
        var p = seg.Points;
        int last = seg.PointCount - 1;
        int k = 0;
        while (k < last - 1 && at[k + 1] < s) k++;
        float span = at[k + 1] - at[k];
        float t = span < 1e-6f ? 0 : Math.Clamp((s - at[k]) / span, 0, 1);
        Vector3 P(int i) => new(b.X + p[i * 3], p[i * 3 + 1] + Lift, b.Z + p[i * 3 + 2]);
        var point = P(k).Lerp(P(k + 1), t);
        Vector3 RightOf(int i0, int i1)
        {
            var d = P(i1) - P(i0);
            var r = new Vector3(-d.Z, 0, d.X);
            return r.LengthSquared() < 1e-10f ? Vector3.Right : r.Normalized();
        }
        var piece = RightOf(k, k + 1);
        Vector3 right = piece;
        if (t < 1e-4f && k > 0) right = (RightOf(k - 1, k) + piece).Normalized();
        else if (t > 1 - 1e-4f && k + 1 < last) right = (piece + RightOf(k + 1, k + 2)).Normalized();
        if (right.LengthSquared() < 0.5f) right = piece;
        return (point, right);
    }

    /// <summary>+1 when the nearest junction core (within <see cref="CoreReach"/>) lies ahead along the drawing, -1 behind, 0 none.</summary>
    private sbyte NearestCoreAhead(Vector3 p, Vector3 along)
    {
        float best = CoreReach * CoreReach;
        sbyte result = 0;
        foreach (var c in _cores)
        {
            var d = c.Centre - p;
            d.Y = 0;
            float d2 = d.LengthSquared();
            if (d2 >= best) continue;
            best = d2;
            result = (sbyte)(d.Dot(along) >= 0 ? 1 : -1);
        }
        return result;
    }

    private const float CoreReach = 400f;

    /// <summary>A car lane arriving (its traffic heads for the junction <paramref name="towards"/> says), leaving, or neither.</summary>
    private static Color StripColour(Strip st, sbyte towards)
    {
        if (st.Kind != Kind.CarIn) return ColourOf(st.Kind);
        if (st.Travel == 0 || towards == 0) return ColourOf(Kind.CarBoth);
        bool arriving = st.Travel == towards;
        if (!arriving) return ColourOf(Kind.CarOut);
        bool pocket = st.Opening || (st.Moves != SignalMoves.None && (st.Moves & SignalMoves.Through) == 0);
        return ColourOf(pocket ? Kind.Pocket : Kind.CarIn);
    }

    /// <summary>A transition's strips, as the planner wrote them; a kerb where the carriageway meets a band.</summary>
    private static List<Strip> TransitionStrips(RoadTransition t, List<float> samples)
    {
        int n = samples.Count;
        var result = new List<Strip>(t.Strips.Count + 2);
        for (int k = 0; k < t.Strips.Count; k++)
        {
            var x = t.Strips[k];
            var st = new Strip
            {
                Kind = x.Kind switch
                {
                    StripKind.Sidewalk => Kind.Sidewalk,
                    StripKind.Buffer => Kind.Buffer,
                    StripKind.Path => Kind.Path,
                    StripKind.Verge => Kind.Verge,
                    StripKind.BikeLane => Kind.BikeLane,
                    StripKind.CarLane => Kind.CarIn,
                    StripKind.Hatch => Kind.Hatch,
                    _ => Kind.Island,
                },
                Travel = x.Travel, Moves = x.Moves, Carriageway = x.InCarriageway,
                Opening = x.Kind == StripKind.CarLane && (x.Widths[0] < 0.05f || x.Widths[^1] < 0.05f),
                From = new float[n], To = new float[n],
            };
            for (int i = 0; i < n; i++) (st.From[i], st.To[i]) = t.SpanAt(k, samples[i]);
            if (k > 0 && t.Strips[k - 1].InCarriageway != x.InCarriageway) result.Add(KerbAt(st.From));
            result.Add(st);
        }
        return result;
    }

    private static Strip KerbAt(float[] at) => new() { Kind = Kind.Kerb, From = (float[])at.Clone(), To = (float[])at.Clone() };

    /// <summary>
    /// An ordinary segment's strips: its carriageway split into lanes (the tile's counts, else the class's guess: one way all
    /// along it, two ways half each, a single lane under 5 m), painted bike lanes at its edges, a pocket's widening, then each
    /// side's profile outward (<see cref="SegmentSections"/>).
    /// </summary>
    private static List<Strip> PlainStrips(RoadSegment seg, List<float> samples, float[] stations)
    {
        var sec = SegmentSections.Of(seg);
        var a = seg.Attributes;
        int n = samples.Count;
        // samples are the segment's points here (no transition)
        float half = seg.Width * 0.5f;
        var result = new List<Strip>();
        Strip Constant(Kind kind, float from, float to, sbyte travel = 0, bool carriageway = true)
        {
            var st = new Strip { Kind = kind, Travel = travel, Carriageway = carriageway, From = new float[n], To = new float[n] };
            Array.Fill(st.From, from);
            Array.Fill(st.To, to);
            return st;
        }
        float bl = a.Left.HasLane ? a.Left.BikeDm / 10f : 0, br = a.Right.HasLane ? a.Right.BikeDm / 10f : 0;
        float left = -half + bl, right = half - br;
        if (right - left < 1f) { bl = br = 0; left = -half; right = half; }

        // the left side outward, mirrored into left-to-right order
        var leftSide = new List<Strip>();
        SideStrips(sec, false, n, leftSide);
        for (int k = leftSide.Count - 1; k >= 0; k--) result.Add(leftSide[k]);
        if (sec.LeftProfile[0] != null) result.Add(KerbAt(sec.SideLeft));
        if (Widening(sec.SideLeft, sec.RibbonLeft)) result.Add(new Strip { Kind = Kind.Pocket, Carriageway = true, From = sec.SideLeft, To = sec.RibbonLeft, Travel = 0 });

        if (bl > 0) result.Add(Constant(Kind.BikeLane, -half, left, -1));
        int forward = a.LanesForward, backward = a.LanesBackward;
        float w = right - left;
        if (forward + backward == 0)
        {
            int guess = Math.Max(1, (int)MathF.Round(w / 3.25f));
            if (a.OneWay > 0) forward = guess;
            else if (a.OneWay < 0) backward = guess;
            else if (w < 5f) forward = backward = 0;
            else { forward = Math.Max(1, guess / 2); backward = Math.Max(1, guess - forward); }
        }
        if (forward + backward == 0) result.Add(Constant(Kind.CarIn, left, right, 0));
        else
        {
            float lane = w / (forward + backward);
            for (int k = 0; k < backward; k++) result.Add(Constant(Kind.CarIn, left + k * lane, left + (k + 1) * lane, -1));
            for (int k = 0; k < forward; k++) result.Add(Constant(Kind.CarIn, left + (backward + k) * lane, left + (backward + k + 1) * lane, +1));
        }
        if (br > 0) result.Add(Constant(Kind.BikeLane, right, half, +1));

        if (Widening(sec.RibbonRight, sec.SideRight)) result.Add(new Strip { Kind = Kind.Pocket, Carriageway = true, From = sec.RibbonRight, To = sec.SideRight, Travel = 0 });
        if (sec.RightProfile[0] != null) result.Add(KerbAt(sec.SideRight));
        SideStrips(sec, true, n, result);
        return result;
    }

    private static bool Widening(float[] from, float[] to)
    {
        for (int i = 0; i < from.Length; i++) if (to[i] - from[i] > 0.05f) return true;
        return false;
    }

    /// <summary>One side's profile pieces, outward from its kerb: their surfaces as kinds, a vertical kerb as a line.</summary>
    private static void SideStrips(SegmentSections sec, bool right, int n, List<Strip> into)
    {
        var profile = right ? sec.RightProfile[0] : sec.LeftProfile[0];
        if (profile == null) return;
        var start = right ? sec.SideRight : sec.SideLeft;
        for (int j = 0; j + 1 < profile.Count; j++)
        {
            var kind = profile.Surface[j] switch
            {
                StreetSurface.Kerb => Kind.Kerb,
                StreetSurface.Sidewalk => Kind.Sidewalk,
                StreetSurface.Paving => Kind.Paving,
                StreetSurface.Verge => Kind.Verge,
                StreetSurface.Track => Kind.Path,
                _ => Kind.Buffer,
            };
            // the first kerb is drawn at the side's start already
            if (kind == Kind.Kerb && j == 0 && profile.D[1] - profile.D[0] < 1e-3f) continue;
            var st = new Strip { Kind = kind, Carriageway = false, From = new float[n], To = new float[n] };
            for (int i = 0; i < n; i++)
            {
                float d0 = profile.D[j], d1 = profile.D[j + 1];
                if (right) { st.From[i] = start[i] + d0; st.To[i] = start[i] + d1; }
                else { st.From[i] = start[i] - d1; st.To[i] = start[i] - d0; }
            }
            into.Add(st);
        }
    }

    // ---- mouths, arrows, labels

    /// <summary>
    /// A street's end on a junction core: the moves of its arriving lanes as arrows, and its strips left to right as the
    /// arriving driver sees them. <paramref name="way"/> +1: the mouth is at the drawing's end (arriving is along it).
    /// </summary>
    private void AddMouth(List<Strip> strips, int k, Vector3 at, Vector3 right, Vector3 outward, int way, bool planned)
    {
        var tokens = new List<(string Text, bool Carriageway)>();
        // seen driving in: along the drawing (way +1) the strips' order, against it reversed
        for (int q = 0; q < strips.Count; q++)
        {
            var st = strips[way > 0 ? q : strips.Count - 1 - q];
            float width = st.To[k] - st.From[k];
            if (st.Kind == Kind.Kerb || width < 0.1f) continue;
            bool arriving = st.Travel == way;
            string text = st.Kind switch
            {
                Kind.Sidewalk => "walk",
                Kind.Paving => "paving",
                Kind.Buffer => "buf",
                Kind.Path => "path",
                Kind.Verge => "verge",
                Kind.Hatch => "hatch",
                Kind.Island => "isl",
                Kind.Pocket => "widen",
                Kind.BikeLane => st.Travel == 0 ? "b" : arriving ? "b" + Moves(st.Moves) : "b out",
                _ => st.Travel == 0 ? "both" : arriving ? (st.Moves == SignalMoves.None ? "in" : Moves(st.Moves)) : "out",
            };
            tokens.Add((text, st.Carriageway));
            if (arriving && st.Moves != SignalMoves.None && st.Kind is Kind.CarIn or Kind.BikeLane)
            {
                float mid = (st.From[k] + st.To[k]) * 0.5f;
                var tip = at + right * mid + outward * 1.5f;
                AddArrow(tip, -outward, way > 0 ? right : -right, Math.Min(1f, width / 3.2f), st.Moves,
                    st.Kind == Kind.BikeLane ? new Color(0.12f, 0.12f, 0.12f) : Colors.White);
            }
        }
        if (tokens.Count == 0) return;
        var sb = new StringBuilder();
        for (int q = 0; q < tokens.Count; q++)
        {
            if (q > 0) sb.Append(tokens[q].Carriageway != tokens[q - 1].Carriageway ? " ‖ " : " | ");
            sb.Append(tokens[q].Text);
        }
        _mouths.Add(new Mouth(at, outward, sb.ToString(), planned));
    }

    public static string Moves(SignalMoves m) =>
        (m.HasFlag(SignalMoves.Left) ? "L" : "") + (m.HasFlag(SignalMoves.Through) ? "T" : "") + (m.HasFlag(SignalMoves.Right) ? "R" : "");

    /// <summary>
    /// The <c>LANE</c> records: each approach's lanes as the region log names them, on the mouth label nearest its stop line;
    /// where no drawn street ends there (no core or no transition), its own arrows across the stop line too.
    /// </summary>
    private readonly List<(Vector3 At, string Text, Vector3 Outward)> _approaches = new();

    private void AddApproaches(RoadTile tile, Vector3 b, Vector2 eye)
    {
        foreach (var a in tile.Approaches)
        {
            var at = new Vector3(b.X + a.X, a.Y + Lift, b.Z + a.Z);
            if (new Vector2(at.X, at.Z).DistanceTo(eye) > Radius) continue;
            var outward = new Vector3(MathF.Cos(a.Heading), 0, -MathF.Sin(a.Heading));
            var sb = new StringBuilder();
            foreach (var l in a.Lanes)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(l.Kind switch { ApproachLaneKind.Bike => "b", ApproachLaneKind.Tram => "tram:", _ => "" }).Append(Moves(l.Moves));
                if (l.StopBehind > 3.5f) sb.Append("[box]");
                else if (l.StopBehind > 0.01f) sb.Append("[adv]");
            }
            _approaches.Add((at, sb.ToString(), outward));
            bool drawn = false;
            foreach (var m in _mouths)
                if (m.At.DistanceTo(at) < 12f && m.Outward.Dot(outward) > 0.7f) { drawn = true; break; }
            if (drawn || a.Lanes.Count == 0) continue;
            // no street mouth drawn here: the record's own lanes across its stop line
            var inward = -outward;
            var driverRight = new Vector3(-inward.Z, 0, inward.X);
            float mean = 0;
            foreach (var l in a.Lanes) mean += l.Offset;
            mean /= a.Lanes.Count;
            foreach (var l in a.Lanes)
            {
                if (l.Moves == SignalMoves.None) continue;
                var tip = at + driverRight * (l.Offset - mean) + outward * (1f + l.StopBehind);
                AddArrow(tip, inward, driverRight, 0.8f, l.Moves, l.Kind == ApproachLaneKind.Car ? Colors.White : new Color(0.98f, 0.84f, 0.12f));
            }
        }
    }

    private void PlaceLabels(Vector2 eye)
    {
        // one label per mouth, the LANE record's line over the strips' where one stops there
        var texts = new List<(Vector3 At, string Text, float Distance)>();
        var used = new bool[_approaches.Count];
        foreach (var m in _mouths)
        {
            string text = m.Strips;
            for (int q = 0; q < _approaches.Count; q++)
            {
                var (at, lanes, outward) = _approaches[q];
                if (used[q] || at.DistanceTo(m.At) > 12f || outward.Dot(m.Outward) < 0.7f) continue;
                used[q] = true;
                text = "LANE " + lanes + "\n" + text;
                break;
            }
            // a plain street's end (a driveway, a side street) only where an approach record stops: its section is drawn anyway
            if (!m.Planned && ReferenceEquals(text, m.Strips)) continue;
            var place = m.At + m.Outward * LabelOut;
            texts.Add((place, text, new Vector2(place.X, place.Z).DistanceTo(eye)));
        }
        for (int q = 0; q < _approaches.Count; q++)
        {
            if (used[q]) continue;
            var (at, lanes, outward) = _approaches[q];
            var place = at + outward * LabelOut;
            texts.Add((place, "LANE " + lanes, new Vector2(place.X, place.Z).DistanceTo(eye)));
        }
        _approaches.Clear();
        texts.Sort((x, y) => x.Distance.CompareTo(y.Distance));

        int used3d = 0;
        foreach (var (at, text, distance) in texts)
        {
            if (used3d >= MaxLabels || distance > LabelRadius) break;
            while (_labels.Count <= used3d)
            {
                var label = new Label3D
                {
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    NoDepthTest = true,
                    FixedSize = true,
                    PixelSize = 0.0010f,
                    FontSize = 24,
                    OutlineSize = 8,
                    Modulate = Colors.White,
                    OutlineModulate = new Color(0, 0, 0, 0.9f),
                    Shaded = false,
                    RenderPriority = 10,
                    OutlineRenderPriority = 9,
                    Font = new SystemFont { FontNames = ["Consolas", "DejaVu Sans Mono", "monospace"] },
                };
                AddChild(label);
                _labels.Add(label);
            }
            var l = _labels[used3d++];
            l.Position = at + Vector3.Up * 0.5f;
            if (l.Text != text) l.Text = text;
            l.Visible = true;
        }
        for (int i = used3d; i < _labels.Count; i++) _labels[i].Visible = false;
        MouthLabels = used3d;
    }

    /// <summary>
    /// The moves of one lane as one arrow: a shaft along <paramref name="forward"/> ending at <paramref name="tip"/>, its head
    /// straight on for T, bent to the driver's left or right for L and R (<paramref name="right"/>: the driver's right).
    /// </summary>
    private void AddArrow(Vector3 tip, Vector3 forward, Vector3 right, float scale, SignalMoves moves, Color colour)
    {
        var layer = _layers[LayerArrows];
        scale = Math.Max(0.6f, scale);
        float shaft = 0.18f * scale, head = 0.65f * scale;
        var f = forward * scale;
        var r = right * scale;
        var back = tip - f * 6f;
        bool through = moves.HasFlag(SignalMoves.Through);
        var bend = tip - f * 2.8f;
        var shaftEnd = through ? tip - f * 1.6f : bend;
        Bar(layer, back, shaftEnd, right, shaft, colour);
        if (through) Tri(layer, tip - f * 1.6f + right * head, tip, tip - f * 1.6f - right * head, colour);
        foreach (int side in (int[])[-1, 1])
        {
            if (!moves.HasFlag(side < 0 ? SignalMoves.Left : SignalMoves.Right)) continue;
            var elbow = bend + r * (side * 0.9f);
            Bar(layer, bend, elbow, forward, shaft, colour);
            var point = bend + r * (side * 2.0f);
            Tri(layer, elbow + forward * head, point, elbow - forward * head, colour);
        }
        Arrows++;
    }

    private static void Bar(Layer layer, Vector3 a, Vector3 b, Vector3 across, float half, Color c) =>
        Quad(layer, a - across * half, a + across * half, b + across * half, b - across * half, c, c, c, c);

    private static void Tri(Layer layer, Vector3 a, Vector3 b, Vector3 c, Color colour)
    {
        layer.Points.Add(a); layer.Points.Add(b); layer.Points.Add(c);
        layer.Colours.Add(colour); layer.Colours.Add(colour); layer.Colours.Add(colour);
    }

    private static void Quad(Layer layer, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color ca, Color cb, Color cc, Color cd)
    {
        layer.Points.Add(a); layer.Points.Add(b); layer.Points.Add(c);
        layer.Points.Add(a); layer.Points.Add(c); layer.Points.Add(d);
        layer.Colours.Add(ca); layer.Colours.Add(cb); layer.Colours.Add(cc);
        layer.Colours.Add(ca); layer.Colours.Add(cc); layer.Colours.Add(cd);
    }

    // ---- trams, bays

    private void AddTram(RoadSegment seg, Vector3 b)
    {
        var at = SegmentSections.Stations(seg);
        // the track itself (a metre-gauge pair of rails), see-through over the lane it runs in
        const float half = 0.75f;
        var colour = ColourOf(Kind.Tram) with { A = 0.75f };
        var layer = _layers[LayerKerbs];
        var (p0, r0) = PointAt(seg, at, at[0], b);
        for (int k = 1; k < seg.PointCount; k++)
        {
            var (p1, r1) = PointAt(seg, at, at[k], b);
            Quad(layer, p0 - r0 * half, p0 + r0 * half, p1 + r1 * half, p1 - r1 * half, colour, colour, colour, colour);
            (p0, r0) = (p1, r1);
        }
    }

    private void AddBay(ParkingBay bay, Vector3 b, Vector2 eye)
    {
        var c = new Vector3(b.X + bay.X, bay.Y + Lift, b.Z + bay.Z);
        if (new Vector2(c.X, c.Z).DistanceTo(eye) > Radius) return;
        var nose = new Vector3(-MathF.Sin(bay.Heading), 0, -MathF.Cos(bay.Heading)) * (bay.Depth * 0.5f - 0.15f);
        var side = new Vector3(MathF.Cos(bay.Heading), 0, -MathF.Sin(bay.Heading)) * (bay.Width * 0.5f - 0.15f);
        var colour = ColourOf(Kind.Parking);
        Quad(_layers[LayerStrips], c - nose - side, c - nose + side, c + nose + side, c + nose - side, colour, colour, colour, colour);
    }

    private void Upload()
    {
        _arrays.ClearSurfaces();
        Triangles = 0;
        for (int i = 0; i < _layers.Length; i++)
        {
            var layer = _layers[i];
            if (layer.Points.Count == 0) continue;
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = layer.Points.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = layer.Colours.ToArray();
            _arrays.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            _arrays.SurfaceSetMaterial(_arrays.GetSurfaceCount() - 1, Materials[i]);
            Triangles += layer.Points.Count / 3;
        }
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[lanes] drawn round {_builtE:F0},{_builtN:F0}: {_tiles.Count} tiles, {StripQuads} strip quads, {_mouths.Count} mouths ({MouthLabels} labelled), {Arrows} arrows"));
    }

    // ---- legend

    private static Control BuildLegend()
    {
        var panel = new PanelContainer { Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.82f, 10, 10));
        panel.AnchorLeft = panel.AnchorRight = 1;
        panel.AnchorTop = panel.AnchorBottom = 0;
        panel.GrowHorizontal = Control.GrowDirection.Begin;
        panel.OffsetRight = -16;
        panel.OffsetTop = 16;
        var rows = UiKit.VBox(2);
        rows.MouseFilter = Control.MouseFilterEnum.Ignore;
        panel.AddChild(rows);
        rows.AddChild(UiTheme.Title("Lane plan", 16));
        foreach (var (_, name, colour) in Key)
        {
            var row = UiKit.HBox(8);
            row.MouseFilter = Control.MouseFilterEnum.Ignore;
            row.AddChild(new ColorRect
            {
                Color = colour, CustomMinimumSize = new Vector2(26, 14), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
            row.AddChild(UiKit.Text(name, UiTheme.FontSmall, Colors.White));
            rows.AddChild(row);
        }
        var note = UiKit.Text(
            "White arrows: an arriving lane's moves (dark on a bike lane).\n"
            + "Labels, left to right as the arriving driver sees them:\n"
            + "  LANE  the approach record (b bike, tram:, [box] [adv])\n"
            + "  then its strips: walk path verge buf | L T R in out both,\n"
            + "  b.. bike lane, hatch, isl; ‖ the carriageway's edges.",
            UiTheme.FontTiny, UiTheme.TextDim);
        rows.AddChild(note);
        return panel;
    }
}
