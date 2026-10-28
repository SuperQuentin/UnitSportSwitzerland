using Godot;
using static UnitSport.Interiors.Pinball.PinballTable;

namespace UnitSport.Interiors.Pinball;

/// <summary>
/// A pinball machine in a venue (#1325): the cabinet, its playfield and backbox for one of the four
/// tables, in the piece's frame (back to −Z, the player at +Z, centred on the floor). Everything that
/// stands still is one mesh per table, built once and shared by every machine of that table; what moves
/// is a node each (flippers, balls, drop targets, spinners, the plunger) set from poses, nothing rebuilt;
/// the lamps are two MultiMeshes whose instance colours change only when a lamp does; the backbox's
/// display redraws only on change (<see cref="PinballBackbox"/>). Idle, it sleeps.
/// </summary>
public partial class PinballCabinet : Node3D
{
    /// <summary>The playfield's front edge in the piece's frame, and its slope.</summary>
    public static readonly Vector3 FieldFront = new(0, 0.80f, 0.60f);
    public static readonly Basis FieldTilt = new(Vector3.Right, Mathf.DegToRad(SlopeDeg));
    /// <summary>The cabinet: half width, front and back, the lockdown bar's height, the backbox.</summary>
    public const float HalfWidth = 0.28f, Front = 0.69f, Back = -0.62f, BarY = 0.92f, BoxBottom = 1.07f, BoxTop = 1.93f,
        BoxFront = -0.47f, BoxBack = -0.70f, BoxHalf = 0.365f;
    public const int MaxBalls = 6;

    private sealed record Meshes(ArrayMesh Body, ArrayMesh Field, ArrayMesh Plastic, ArrayMesh Flipper, ArrayMesh Drop, ArrayMesh Spinner);
    private static readonly Dictionary<int, Meshes> Built = new();
    private static ArrayMesh? _ball, _lampDisc, _lampArrow, _knob;
    private static StandardMaterial3D? _lampMat, _plasticMat;

    public PinballTable Table { get; private set; } = null!;
    public int TableIndex { get; private set; }
    public PinballBackbox Display { get; private set; } = null!;

    private Node3D _field = null!;
    private Node3D[] _flippers = null!;
    private readonly MeshInstance3D[] _balls = new MeshInstance3D[MaxBalls];
    private MeshInstance3D[] _drops = Array.Empty<MeshInstance3D>();
    private int[] _dropWalls = Array.Empty<int>();
    private (Node3D Node, int Sensor)[] _spinners = Array.Empty<(Node3D, int)>();
    private Node3D _plunger = null!;
    private MultiMeshInstance3D _discs = null!, _arrows = null!;
    private int[] _discLamps = Array.Empty<int>(), _arrowLamps = Array.Empty<int>();
    private byte[] _lampsShown = Array.Empty<byte>();
    private bool _blinkShown;

    /// <summary>A table's playfield point (x across, y up the table, h above it) in the playfield node's frame.</summary>
    public static Vector3 Local(Vector3 table) => new(table.X, table.Z, -table.Y);

    public static Vector3 Local(Vector2 table, float h = 0) => new(table.X, h, -table.Y);

    /// <summary>A playfield point in the cabinet's (piece's) frame.</summary>
    public static Vector3 ToPiece(Vector3 table) => FieldFront + FieldTilt * Local(table);

    public static PinballCabinet Create(int table, Material material)
    {
        var t = PinballLayouts.Get(table);
        var m = MeshesOf(table);
        var c = new PinballCabinet { Name = "Pinball", Table = t, TableIndex = table };
        c.AddChild(new MeshInstance3D { Name = "Body", Mesh = m.Body, MaterialOverride = material });
        c._field = new Node3D { Name = "Playfield", Transform = new Transform3D(FieldTilt, FieldFront) };
        c.AddChild(c._field);
        c._field.AddChild(new MeshInstance3D { Name = "Field", Mesh = m.Field, MaterialOverride = material });
        c._field.AddChild(new MeshInstance3D { Name = "Ramps", Mesh = m.Plastic, MaterialOverride = PlasticMaterial(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        c._flippers = new Node3D[t.Flippers.Count];
        for (int i = 0; i < t.Flippers.Count; i++)
        {
            var f = t.Flippers[i];
            var n = new MeshInstance3D { Name = $"Flipper{i}", Mesh = m.Flipper, MaterialOverride = material, Position = Local(f.Pivot) };
            n.Rotation = new Vector3(0, f.Rest, 0);
            c._field.AddChild(n);
            c._flippers[i] = n;
        }
        _ball ??= BallMesh();
        for (int i = 0; i < MaxBalls; i++)
        {
            c._balls[i] = new MeshInstance3D { Name = $"Ball{i}", Mesh = _ball, MaterialOverride = material, Visible = false };
            c._field.AddChild(c._balls[i]);
        }
        var drops = new List<int>();
        for (int w = 0; w < t.Walls.Count; w++)
            if (t.Walls[w].Kind == WallKind.Drop) drops.Add(w);
        c._dropWalls = drops.ToArray();
        c._drops = new MeshInstance3D[drops.Count];
        for (int i = 0; i < drops.Count; i++)
        {
            var w = t.Walls[drops[i]];
            var mid = (w.A + w.B) / 2;
            float yaw = Mathf.Atan2((w.B - w.A).Y, (w.B - w.A).X);
            c._drops[i] = new MeshInstance3D { Name = $"Drop{i}", Mesh = m.Drop, MaterialOverride = material, Position = Local(mid), Rotation = new Vector3(0, yaw, 0) };
            c._field.AddChild(c._drops[i]);
        }
        var spinners = new List<(Node3D, int)>();
        for (int s = 0; s < t.Sensors.Count; s++)
        {
            var sn = t.Sensors[s];
            if (sn.Kind != SensorKind.Spinner) continue;
            var mid = (sn.A + sn.B) / 2;
            float yaw = Mathf.Atan2((sn.B - sn.A).Y, (sn.B - sn.A).X);
            var pivot = new Node3D { Name = $"Spinner{s}", Position = Local(mid, 0.034f), Rotation = new Vector3(0, yaw, 0) };
            pivot.AddChild(new MeshInstance3D { Name = "Flag", Mesh = m.Spinner, MaterialOverride = material });
            c._field.AddChild(pivot);
            spinners.Add((pivot, s));
        }
        c._spinners = spinners.ToArray();
        _knob ??= KnobMesh();
        c._plunger = new MeshInstance3D { Name = "Plunger", Mesh = _knob, MaterialOverride = material, Position = new Vector3(t.ShooterX, 0.835f, Front) };
        c.AddChild(c._plunger);
        c.AddLamps();
        c.Display = PinballBackbox.Create(t);
        c.Display.Position = new Vector3(0, t.Era == PinballEra.Reels ? 1.30f : 1.20f, BoxFront + 0.004f);
        c.AddChild(c.Display);
        var title = new Label3D
        {
            Name = "Title", Text = t.Title.ToUpperInvariant(), FontSize = 64, PixelSize = 0.0011f, Modulate = t.AccentA * 1.3f,
            OutlineModulate = t.Cabinet * 0.5f, OutlineSize = 10, Shaded = false, DoubleSided = false,
            Position = new Vector3(0, 1.72f, BoxFront + 0.006f),
        };
        c.AddChild(title);
        c.ShowScreen(PinballScreen.Idle(t), 0);
        return c;
    }

    // ---- what moves ----------------------------------------------------------------------------

    /// <summary>The balls (x, y, h on the table) and the flippers' angles, as a solver or a watcher's stream has them.</summary>
    public void ShowPoses(ReadOnlySpan<Vector3> balls, ReadOnlySpan<float> flippers)
    {
        for (int i = 0; i < MaxBalls; i++)
        {
            bool on = i < balls.Length;
            if (_balls[i].Visible != on) _balls[i].Visible = on;
            if (on) _balls[i].Position = Local(balls[i]);
        }
        for (int i = 0; i < _flippers.Length && i < flippers.Length; i++) _flippers[i].Rotation = new Vector3(0, flippers[i], 0);
    }

    /// <summary>The drop targets down or up (a bit a wall index of the table, as the solver keeps them).</summary>
    public void ShowDrops(Func<int, bool> down)
    {
        for (int i = 0; i < _drops.Length; i++)
        {
            float h = down(_dropWalls[i]) ? -0.034f : 0f;
            if (_drops[i].Position.Y != h) _drops[i].Position = _drops[i].Position with { Y = h };
        }
    }

    /// <summary>The spinners' flags turned to their angle (by sensor index), and the plunger pulled (0-1).</summary>
    public void ShowSpinAndPlunger(Func<int, float> spin, float pull)
    {
        foreach (var (node, sensor) in _spinners)
        {
            var r = node.Rotation;
            float a = spin(sensor);
            if (r.X != a) node.Rotation = r with { X = a };
        }
        float z = Front + 0.03f + pull * 0.06f;
        if (_plunger.Position.Z != z) _plunger.Position = _plunger.Position with { Z = z };
    }

    /// <summary>Lamps and backbox for <paramref name="s"/>; blinking lamps at 2.5 Hz on <paramref name="now"/>. Cheap when nothing changed.</summary>
    public void ShowScreen(PinballScreen s, double now)
    {
        Display.Show(s, now);
        bool blink = (int)(now * 5) % 2 == 0;
        bool same = s.Lamps.Length == _lampsShown.Length && blink == _blinkShown;
        if (same)
            for (int i = 0; i < s.Lamps.Length && same; i++) same = s.Lamps[i] == _lampsShown[i];
        if (same) return;
        _blinkShown = blink;
        _lampsShown = (byte[])s.Lamps.Clone();
        Color Of(int lamp)
        {
            var l = Table.Lamps[lamp];
            var state = lamp < s.Lamps.Length ? (PinballRules.Lit)s.Lamps[lamp] : PinballRules.Lit.Off;
            bool on = state == PinballRules.Lit.On || state == PinballRules.Lit.Blink && blink;
            return on ? l.Color * 1.25f : l.Color * 0.16f;
        }
        for (int i = 0; i < _discLamps.Length; i++) _discs.Multimesh.SetInstanceColor(i, Of(_discLamps[i]));
        for (int i = 0; i < _arrowLamps.Length; i++) _arrows.Multimesh.SetInstanceColor(i, Of(_arrowLamps[i]));
    }

    private void AddLamps()
    {
        _lampDisc ??= LampDisc();
        _lampArrow ??= LampArrow();
        var discs = new List<int>();
        var arrows = new List<int>();
        for (int i = 0; i < Table.Lamps.Count; i++) (Table.Lamps[i].Kind == LampKind.Shot ? arrows : discs).Add(i);
        _discLamps = discs.ToArray();
        _arrowLamps = arrows.ToArray();
        MultiMeshInstance3D Make(string name, Mesh mesh, int[] lamps)
        {
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = mesh, InstanceCount = lamps.Length };
            for (int k = 0; k < lamps.Length; k++)
            {
                var l = Table.Lamps[lamps[k]];
                float h = l.Kind == LampKind.BumperCap ? 0.047f : 0.0012f;
                float yaw = l.Dir == Vector2.Zero ? 0 : Mathf.Atan2(l.Dir.Y, l.Dir.X);
                var basis = new Basis(Vector3.Up, yaw).Scaled(new Vector3(l.Size, 1, l.Size) * (l.Kind == LampKind.BumperCap ? 0.85f : 1f));
                mm.SetInstanceTransform(k, new Transform3D(basis, Local(l.At, h)));
                mm.SetInstanceColor(k, l.Color * 0.16f);
            }
            var node = new MultiMeshInstance3D { Name = name, Multimesh = mm, MaterialOverride = LampMaterial(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _field.AddChild(node);
            return node;
        }
        _discs = Make("Inserts", _lampDisc, _discLamps);
        _arrows = Make("Arrows", _lampArrow, _arrowLamps);
    }

    private static StandardMaterial3D LampMaterial() => _lampMat ??= new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    private static StandardMaterial3D PlasticMaterial() => _plasticMat ??= new StandardMaterial3D
    {
        VertexColorUseAsAlbedo = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        Roughness = 0.2f, Metallic = 0f,
    };

    // ---- the shared meshes ------------------------------------------------------------------------

    private static Meshes MeshesOf(int table)
    {
        if (Built.TryGetValue(table, out var m)) return m;
        var t = PinballLayouts.Get(table);
        m = new Meshes(BodyMesh(t), FieldMesh(t), PlasticMesh(t), FlipperMesh(t), DropMesh(t), SpinnerMesh());
        Built[table] = m;
        return m;
    }

    private static readonly Color Chrome = PinballMesh.Col(0.80f, 0.82f, 0.86f), Rubber = PinballMesh.Col(0.95f, 0.95f, 0.92f),
        Black = PinballMesh.Col(0.06f, 0.06f, 0.07f), Wood = PinballMesh.Col(0.55f, 0.40f, 0.24f);

    /// <summary>Legs, the cabinet's sides, front and back, the lockdown bar, buttons and coin door, the backbox.</summary>
    private static ArrayMesh BodyMesh(PinballTable t)
    {
        var s = new PinballMesh { TwoSided = true };
        var paint = t.Cabinet.SrgbToLinear();
        var trim = t.Trim.SrgbToLinear();
        float tan = Mathf.Tan(Mathf.DegToRad(SlopeDeg));
        float Top(float z) => BarY + (Front - z) * tan;
        // legs, a little splayed at the floor
        foreach (var (x, z) in new[] { (-0.25f, 0.62f), (0.25f, 0.62f), (-0.25f, -0.55f), (0.25f, -0.55f) })
        {
            s.Box(new Vector3(x - 0.025f, 0, z - 0.025f), new Vector3(x + 0.025f, 0.73f, z + 0.025f), Chrome);
            s.Box(new Vector3(x - 0.035f, 0, z - 0.035f), new Vector3(x + 0.035f, 0.025f, z + 0.035f), Chrome);
        }
        // the cabinet: bottom, two sloped sides, front with the coin door, back under the backbox
        const float bottom = 0.72f, wall = 0.02f;
        s.Box(new Vector3(-HalfWidth, bottom, Back), new Vector3(HalfWidth, bottom + wall, Front), paint, true);
        foreach (float x in new[] { -HalfWidth, HalfWidth - wall })
        {
            Vector3 P(float px, float y, float z) => new(px, y, z);
            float x1 = x + wall;
            // a side: a quad strip from the front low to the back high
            s.Quad(P(x, bottom, Front), P(x, Top(Front), Front), P(x, Top(Back), Back), P(x, bottom, Back), paint * 0.86f);
            s.Quad(P(x1, bottom, Back), P(x1, Top(Back), Back), P(x1, Top(Front), Front), P(x1, bottom, Front), paint * 0.86f);
            s.Quad(P(x, Top(Front), Front), P(x1, Top(Front), Front), P(x1, Top(Back), Back), P(x, Top(Back), Back), trim);
            // the side rail's art stripe, lit like the backglass
            s.Quad(P(x - 0.0005f * Math.Sign(x), 0.80f, Front - 0.05f), P(x - 0.0005f * Math.Sign(x), 0.84f, Front - 0.05f),
                P(x - 0.0005f * Math.Sign(x), 0.84f + 1.2f * tan, Back + 0.06f), P(x - 0.0005f * Math.Sign(x), 0.80f + 1.2f * tan, Back + 0.06f),
                Math.Sign(x) < 0 ? t.AccentA.SrgbToLinear() : t.AccentA.SrgbToLinear());
        }
        s.Box(new Vector3(-HalfWidth, bottom, Front - wall), new Vector3(HalfWidth, BarY, Front), paint);
        s.Box(new Vector3(-HalfWidth, bottom, Back), new Vector3(HalfWidth, Top(Back), Back + wall), paint);
        // the coin door and its lit coin slots
        s.Box(new Vector3(-0.14f, 0.74f, Front - 0.002f), new Vector3(0.14f, 0.89f, Front + 0.006f), Black);
        s.Box(new Vector3(-0.06f, 0.83f, Front + 0.005f), new Vector3(-0.03f, 0.865f, Front + 0.008f), PinballMesh.Lit(PinballMesh.Col(1f, 0.2f, 0.15f)));
        s.Box(new Vector3(0.03f, 0.83f, Front + 0.005f), new Vector3(0.06f, 0.865f, Front + 0.008f), PinballMesh.Lit(PinballMesh.Col(1f, 0.2f, 0.15f)));
        // the lockdown bar, the start button, the flipper buttons on the sides
        s.Box(new Vector3(-HalfWidth - 0.005f, BarY, Front - 0.07f), new Vector3(HalfWidth + 0.005f, BarY + 0.018f, Front + 0.005f), Chrome);
        s.Box(new Vector3(-0.2f, 0.85f, Front + 0.005f), new Vector3(-0.17f, 0.88f, Front + 0.02f), PinballMesh.Lit(PinballMesh.Col(1f, 0.85f, 0.2f)));
        foreach (float x in new[] { -HalfWidth - 0.012f, HalfWidth })
            s.Box(new Vector3(x, 0.84f, Front - 0.10f), new Vector3(x + 0.012f, 0.875f, Front - 0.065f), PinballMesh.Lit(t.AccentB.SrgbToLinear()));
        // the plunger's housing
        s.Box(new Vector3(t.ShooterX - 0.025f, 0.81f, Front), new Vector3(t.ShooterX + 0.025f, 0.86f, Front + 0.012f), Chrome);
        // the backbox: a cabinet of its own on the back, the backglass lit in the table's colours
        s.Box(new Vector3(-BoxHalf, BoxBottom, BoxBack), new Vector3(BoxHalf, BoxTop, BoxFront), paint, true);
        s.Box(new Vector3(-BoxHalf - 0.003f, BoxTop - 0.03f, BoxBack - 0.003f), new Vector3(BoxHalf + 0.003f, BoxTop + 0.004f, BoxFront + 0.003f), trim);
        // the neck between the cabinet and the backbox
        s.Box(new Vector3(-0.1f, Top(Back) - 0.01f, BoxBack + 0.05f), new Vector3(0.1f, BoxBottom, BoxFront - 0.02f), Black);
        BackglassArt(s, t);
        return s.Build();
    }

    /// <summary>The backglass: a lit picture in the table's theme round the display, the speaker grilles below.</summary>
    private static void BackglassArt(PinballMesh s, PinballTable t)
    {
        float z = BoxFront + 0.002f;
        var ground = PinballMesh.Lit(t.Ground.SrgbToLinear() * 1.6f);
        var a = PinballMesh.Lit(t.AccentA.SrgbToLinear());
        var b = PinballMesh.Lit(t.AccentB.SrgbToLinear());
        void Flat(float x0, float y0, float x1, float y1, Color c, float dz = 0) =>
            s.Quad(new Vector3(x0, y0, z + dz), new Vector3(x1, y0, z + dz), new Vector3(x1, y1, z + dz), new Vector3(x0, y1, z + dz), c);
        Flat(-0.33f, 1.10f, 0.33f, 1.89f, ground);
        // the speaker panel at the bottom
        Flat(-0.33f, 1.10f, 0.33f, 1.16f, Black, 0.0005f);
        for (int i = 0; i < 9; i++) Flat(-0.30f + i * 0.068f, 1.115f, -0.27f + i * 0.068f, 1.145f, PinballMesh.Col(0.2f, 0.2f, 0.22f), 0.001f);
        // the theme's picture over the display: shapes of its world, all lit
        switch (t.Era)
        {
            case PinballEra.Reels:
                // clock faces and gears
                for (int i = 0; i < 3; i++)
                {
                    var c = new Vector3(-0.2f + i * 0.2f, 1.62f, z + 0.0008f);
                    Flat(c.X - 0.07f, c.Y - 0.07f, c.X + 0.07f, c.Y + 0.07f, i == 1 ? a : b, 0.0008f);
                    Flat(c.X - 0.055f, c.Y - 0.055f, c.X + 0.055f, c.Y + 0.055f, PinballMesh.Lit(PinballMesh.Col(0.95f, 0.9f, 0.75f)), 0.0012f);
                    Flat(c.X - 0.004f, c.Y - 0.003f, c.X + 0.004f, c.Y + 0.045f, Black, 0.0016f);
                    Flat(c.X - 0.003f, c.Y - 0.004f, c.X + 0.035f, c.Y + 0.004f, Black, 0.0016f);
                }
                break;
            case PinballEra.Segments:
                // a starfield, a planet and a rocket
                for (int i = 0; i < 40; i++)
                {
                    float x = -0.32f + (i * 0.6180339f % 1f) * 0.64f, y = 1.36f + (i * 0.3819f % 1f) * 0.5f;
                    Flat(x, y, x + 0.006f, y + 0.006f, PinballMesh.Lit(Colors.White), 0.0006f);
                }
                Flat(0.12f, 1.48f, 0.28f, 1.64f, b, 0.001f);
                Flat(-0.22f, 1.40f, -0.18f, 1.62f, PinballMesh.Lit(PinballMesh.Col(0.9f, 0.9f, 0.95f)), 0.001f);
                Flat(-0.225f, 1.38f, -0.175f, 1.42f, a, 0.0012f);
                break;
            case PinballEra.DotMatrix:
                // the castle under a full moon
                Flat(0.10f, 1.62f, 0.24f, 1.76f, PinballMesh.Lit(PinballMesh.Col(1f, 0.95f, 0.7f)), 0.0008f);
                Flat(-0.26f, 1.34f, 0.18f, 1.50f, PinballMesh.Lit(PinballMesh.Col(0.12f, 0.08f, 0.16f)), 0.001f);
                foreach (float x in new[] { -0.26f, -0.08f, 0.10f })
                    Flat(x, 1.34f, x + 0.08f, 1.64f, PinballMesh.Lit(PinballMesh.Col(0.12f, 0.08f, 0.16f)), 0.001f);
                for (int i = 0; i < 4; i++) Flat(-0.22f + i * 0.1f, 1.42f, -0.205f + i * 0.1f, 1.45f, a, 0.0014f);
                break;
            default:
                // peaks and a slalom course
                for (int i = 0; i < 3; i++)
                {
                    float x = -0.28f + i * 0.2f;
                    s.Tri(new Vector3(x, 1.36f, z + 0.0008f), new Vector3(x + 0.24f, 1.36f, z + 0.0008f), new Vector3(x + 0.12f, 1.66f + i % 2 * 0.06f, z + 0.0008f), PinballMesh.Lit(PinballMesh.Col(0.85f, 0.92f, 1f)));
                }
                for (int i = 0; i < 5; i++) Flat(-0.25f + i * 0.12f, 1.38f, -0.24f + i * 0.12f, 1.46f, i % 2 == 0 ? a : b, 0.0012f);
                break;
        }
    }

    /// <summary>The playfield: its art, walls, rubbers, posts, bumpers, slings, targets, scoops, lanes, the apron and wire forms.</summary>
    private static ArrayMesh FieldMesh(PinballTable t)
    {
        var s = new PinballMesh();
        var ground = t.Ground.SrgbToLinear();
        var a = t.AccentA.SrgbToLinear();
        var b = t.AccentB.SrgbToLinear();
        var trim = t.Trim.SrgbToLinear();
        Vector3 P(float x, float y, float h = 0) => Local(new Vector3(x, y, h));
        // the playfield board, from under the apron to the top
        s.Quad(P(Width / 2, -0.07f), P(-Width / 2, -0.07f), P(-Width / 2, Length), P(Width / 2, Length), ground);
        PlayfieldArt(s, t, ground, a, b);
        // walls by kind: guides are the table's trim, rubbers white, slings' kicking faces lit plastic over rubber
        foreach (var w in t.Walls)
        {
            if (!w.Visible || w.Kind is WallKind.Drop) continue;
            var col = w.Kind switch
            {
                WallKind.Rubber => Rubber, WallKind.Sling => Rubber, WallKind.Gate => Chrome, WallKind.Target => b,
                _ => trim,
            };
            float thick = w.Kind is WallKind.Target ? 0.008f : 0.006f;
            s.Slab(P(w.A.X, w.A.Y), P(w.B.X, w.B.Y), thick, 0, w.Height, col);
            if (w.Kind == WallKind.Target)
                s.Slab(P(w.A.X, w.A.Y) + Local(w.Pass, 0) * 0.005f, P(w.B.X, w.B.Y) + Local(w.Pass, 0) * 0.005f, 0.002f, 0.008f, w.Height - 0.004f, PinballMesh.Lit(b));
        }
        // the slingshots' plastic covers: a lit triangle over each
        for (int side = 0; side < 2; side++)
        {
            float sx = side == 0 ? -1 : 1;
            float X(float dx) => PinballLayouts.Xc + sx * dx;
            s.Tri(P(X(0.150f), 0.335f, 0.042f), P(X(0.103f), 0.200f, 0.042f), P(X(0.150f), 0.225f, 0.042f), PinballMesh.Lit(a));
            s.Tri(P(X(0.150f), 0.335f, 0.042f), P(X(0.150f), 0.225f, 0.042f), P(X(0.103f), 0.200f, 0.042f), PinballMesh.Lit(a));
        }
        foreach (var d in t.Discs)
        {
            var at = P(d.C.X, d.C.Y);
            if (d.Kind == DiscKind.Post)
            {
                s.Cylinder(at, d.R, 0, 0.03f, Rubber, 10);
                s.Cylinder(at, d.R * 0.5f, 0.03f, 0.045f, Chrome, 6);
                continue;
            }
            // a pop bumper: base, skirt, white body, the cap (its lamp sits on top)
            s.Cylinder(at, d.R + 0.004f, 0, 0.006f, Black, 16);
            s.Cylinder(at, d.R, 0.006f, 0.012f, Chrome, 16);
            s.Cylinder(at, d.R * 0.82f, 0.012f, 0.036f, Rubber, 16);
            s.Cylinder(at, d.R * 0.95f, 0.036f, 0.046f, a * 0.6f, 16);
        }
        foreach (var sc in t.Scoops)
        {
            s.Disc(P(sc.C.X, sc.C.Y, 0.0005f), sc.R * 0.85f, Black, 14);
            s.Ring(P(sc.C.X, sc.C.Y, 0.0006f), sc.R * 0.85f, sc.R, Chrome, 14);
            if (sc.Subway) s.Ring(P(sc.Exit.X, sc.Exit.Y, 0.0006f), 0.012f, 0.016f, Chrome, 12);
        }
        foreach (var sn in t.Sensors)
        {
            if (sn.Kind == SensorKind.Rollover)
            {
                // a rollover's wire across the lane
                var mid = (sn.A + sn.B) / 2;
                s.Slab(P(mid.X - 0.008f, mid.Y), P(mid.X + 0.008f, mid.Y), 0.002f, 0, 0.004f, Chrome);
            }
            else if (sn.Kind == SensorKind.Spinner)
            {
                // the spinner's bracket either side of the lane
                s.Rod(P(sn.A.X, sn.A.Y), P(sn.A.X, sn.A.Y, 0.04f), 0.002f, Chrome);
                s.Rod(P(sn.B.X, sn.B.Y), P(sn.B.X, sn.B.Y, 0.04f), 0.002f, Chrome);
                s.Rod(P(sn.A.X, sn.A.Y, 0.04f), P(sn.B.X, sn.B.Y, 0.04f), 0.002f, Chrome);
            }
        }
        // the wire forms (and every ramp's supports)
        foreach (var r in t.Ramps)
        {
            var tr = r.Track;
            for (int i = 1; i < tr.Length; i++)
            {
                var p0 = Local(tr[i - 1]);
                var p1 = Local(tr[i]);
                var across = (p1 - p0).Cross(Vector3.Up).Normalized() * 0.013f;
                if (!r.Plastic || i > tr.Length / 2)
                {
                    s.Rod(p0 + across, p1 + across, 0.0015f, Chrome);
                    s.Rod(p0 - across, p1 - across, 0.0015f, Chrome);
                    s.Rod(p0 + Vector3.Down * 0.012f, p1 + Vector3.Down * 0.012f, 0.0015f, Chrome);
                }
                if (tr[i].Z > 0.03f && i % 2 == 0) s.Rod(new Vector3(p1.X, 0, p1.Z), p1 + Vector3.Down * 0.012f, 0.002f, Chrome);
            }
        }
        // the apron over the outhole, the shooter lane's floor stripe
        s.Box(P(PinballLayouts.Left, -0.07f) + new Vector3(0, 0, 0), P(PinballLayouts.InnerShooter, 0.055f, 0.03f), trim);
        s.Quad(P(PinballLayouts.InnerShooter + 0.004f, 0.78f, 0.0005f), P(PinballLayouts.Right - 0.004f, 0.78f, 0.0005f),
            P(PinballLayouts.Right - 0.004f, 0.05f, 0.0005f), P(PinballLayouts.InnerShooter + 0.004f, 0.05f, 0.0005f), ground * 0.7f);
        // the side rails over the playfield's edges
        s.Box(P(-Width / 2 - 0.025f, -0.07f), P(-Width / 2, Length, 0.06f), Wood);
        s.Box(P(Width / 2, -0.07f), P(Width / 2 + 0.025f, Length, 0.06f), Wood);
        s.Box(P(-Width / 2 - 0.025f, Length), P(Width / 2 + 0.025f, Length + 0.02f, 0.10f), Wood);
        return s.Build();
    }

    /// <summary>Each table's printed art: shapes of its world on the playfield, under everything.</summary>
    private static void PlayfieldArt(PinballMesh s, PinballTable t, Color ground, Color a, Color b)
    {
        Vector3 P(float x, float y, float h = 0.0003f) => Local(new Vector3(x, y, h));
        void Rect(float x0, float y0, float x1, float y1, Color c, float h = 0.0003f) => s.Quad(P(x1, y0, h), P(x0, y0, h), P(x0, y1, h), P(x1, y1, h), c);
        void Ring(float x, float y, float r0, float r1, Color c, float h = 0.0004f) => s.Ring(P(x, y, h), r0, r1, c, 24);
        switch (t.Era)
        {
            case PinballEra.Reels:
                // brass gears and a dial under the bumpers
                Ring(0, 0.77f, 0.10f, 0.11f, a * 0.7f);
                for (int i = 0; i < 12; i++)
                {
                    float ang = Mathf.Tau * i / 12;
                    var c = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * 0.105f + new Vector2(0, 0.77f);
                    Rect(c.X - 0.005f, c.Y - 0.005f, c.X + 0.005f, c.Y + 0.005f, a * 0.7f, 0.0005f);
                }
                Ring(-0.12f, 0.45f, 0.05f, 0.056f, b * 0.6f);
                Ring(0.11f, 0.42f, 0.04f, 0.045f, b * 0.6f);
                break;
            case PinballEra.Segments:
                for (int i = 0; i < 60; i++)
                {
                    float x = -0.24f + (i * 0.618034f % 1f) * 0.47f, y = 0.05f + (i * 0.7548777f % 1f) * 1.0f;
                    Rect(x, y, x + 0.003f, y + 0.003f, PinballMesh.Col(0.9f, 0.9f, 1f));
                }
                Ring(-0.02f, 0.42f, 0.06f, 0.07f, b * 0.5f);
                Ring(0.13f, 0.86f, 0.03f, 0.06f, a * 0.4f);
                break;
            case PinballEra.DotMatrix:
                // stone courses
                for (int row = 0; row < 26; row++)
                    for (int col = 0; col < 7; col++)
                    {
                        float x = -0.24f + col * 0.07f + (row % 2) * 0.035f, y = 0.03f + row * 0.04f;
                        Rect(x + 0.002f, y + 0.002f, x + 0.066f, y + 0.037f, ground * 1.5f);
                    }
                Ring(0.12f, 0.92f, 0.0f, 0.045f, PinballMesh.Col(0.9f, 0.85f, 0.5f), 0.0005f);
                break;
            default:
                // the piste: blue and red gate stripes, a start line, the finish
                Rect(-0.03f, 0.28f, 0.0f, 0.95f, PinballMesh.Col(0.98f, 0.99f, 1f));
                for (int i = 0; i < 6; i++)
                {
                    float y = 0.40f + i * 0.09f, x = (i % 2 == 0 ? -0.06f : 0.04f);
                    Rect(x, y, x + 0.02f, y + 0.008f, i % 2 == 0 ? a : b, 0.0004f);
                }
                Rect(-0.20f, 0.30f, 0.18f, 0.305f, a * 0.8f);
                break;
        }
    }

    /// <summary>The plastic ramps: floor and side walls along each track, tinted and see-through.</summary>
    private static ArrayMesh PlasticMesh(PinballTable t)
    {
        var s = new PinballMesh();
        foreach (var r in t.Ramps)
        {
            if (!r.Plastic) continue;
            var tr = r.Track;
            int last = tr.Length / 2 + 1;
            for (int i = 1; i <= last && i < tr.Length; i++)
            {
                var p0 = Local(tr[i - 1]) + Vector3.Down * 0.012f;
                var p1 = Local(tr[i]) + Vector3.Down * 0.012f;
                var across = (p1 - p0).Cross(Vector3.Up).Normalized() * 0.022f;
                var col = r.Tint;
                s.Quad(p0 - across, p0 + across, p1 + across, p1 - across, col);
                var up = Vector3.Up * 0.024f;
                s.Quad(p0 - across, p1 - across, p1 - across + up, p0 - across + up, col);
                s.Quad(p0 + across, p0 + across + up, p1 + across + up, p1 + across, col);
            }
        }
        return s.Build();
    }

    private static ArrayMesh FlipperMesh(PinballTable t)
    {
        // a tapered bat along +X from its pivot, its rubber ring round it
        var s = new PinballMesh { TwoSided = true };
        var body = t.Trim.SrgbToLinear();
        var f = t.Flippers[0];
        const int n = 10;
        var outline = new List<Vector3>();
        for (int i = 0; i <= n; i++)
        {
            float ang = Mathf.Pi / 2 + Mathf.Pi * i / n;
            outline.Add(new Vector3(Mathf.Cos(ang) * f.RBase, 0, -Mathf.Sin(ang) * f.RBase));
        }
        for (int i = 0; i <= n; i++)
        {
            float ang = -Mathf.Pi / 2 + Mathf.Pi * i / n;
            outline.Add(new Vector3(f.Length + Mathf.Cos(ang) * f.RTip, 0, -Mathf.Sin(ang) * f.RTip));
        }
        var top = Vector3.Up * 0.024f;
        var inset = Vector3.Up * 0.004f;
        for (int i = 0; i < outline.Count; i++)
        {
            var p0 = outline[i];
            var p1 = outline[(i + 1) % outline.Count];
            s.Quad(p0 + inset, p1 + inset, p1 + top - inset, p0 + top - inset, Rubber);
            s.Quad(p0 * 0.9f + top, p1 * 0.9f + top, p1 * 0.9f + top + inset * 0.2f, p0 * 0.9f + top + inset * 0.2f, body);
        }
        // the top and bottom: a fan from the middle of the bat
        var centre = new Vector3(f.Length / 2, 0, 0);
        for (int i = 0; i < outline.Count; i++)
        {
            var p0 = outline[i] * 0.97f;
            var p1 = outline[(i + 1) % outline.Count] * 0.97f;
            s.Tri(centre + top, p1 + top, p0 + top, body);
        }
        s.Cylinder(Vector3.Up * 0.024f, 0.005f, 0, 0.003f, Chrome, 8);
        return s.Build();
    }

    private static ArrayMesh DropMesh(PinballTable t)
    {
        var s = new PinballMesh { TwoSided = true };
        s.Box(new Vector3(-0.011f, 0, -0.003f), new Vector3(0.011f, 0.032f, 0.003f), t.AccentB.SrgbToLinear());
        s.Box(new Vector3(-0.008f, 0.012f, -0.0035f), new Vector3(0.008f, 0.026f, 0.0035f), Rubber);
        return s.Build();
    }

    private static ArrayMesh SpinnerMesh()
    {
        // a flag hanging from its axle (the node turns round X)
        var s = new PinballMesh { TwoSided = true };
        s.Box(new Vector3(-0.022f, -0.028f, -0.001f), new Vector3(0.022f, -0.002f, 0.001f), Chrome);
        s.Box(new Vector3(-0.018f, -0.024f, -0.0015f), new Vector3(0.018f, -0.006f, 0.0015f), PinballMesh.Lit(PinballMesh.Col(1f, 0.8f, 0.2f)));
        return s.Build();
    }

    private static ArrayMesh BallMesh()
    {
        var s = new PinballMesh { TwoSided = true };
        const int rings = 6, seg = 10;
        const float r = BallRadius;
        var col = PinballMesh.Col(0.86f, 0.88f, 0.92f);
        for (int i = 0; i < rings; i++)
            for (int j = 0; j < seg; j++)
            {
                Vector3 S(int a, int bb)
                {
                    float th = Mathf.Pi * a / rings, ph = Mathf.Tau * bb / seg;
                    return new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph)) * r;
                }
                var c = (col * (0.75f + 0.25f * Mathf.Cos(Mathf.Pi * i / rings))) with { A = 1 };
                s.Quad(S(i, j), S(i, j + 1), S(i + 1, j + 1), S(i + 1, j), c);
            }
        return s.Build();
    }

    private static ArrayMesh KnobMesh()
    {
        var s = new PinballMesh { TwoSided = true };
        s.Rod(Vector3.Zero, new Vector3(0, 0, 0.05f), 0.004f, Chrome);
        s.Box(new Vector3(-0.012f, -0.012f, 0.05f), new Vector3(0.012f, 0.012f, 0.075f), PinballMesh.Col(0.85f, 0.1f, 0.1f));
        return s.Build();
    }

    private static ArrayMesh LampDisc()
    {
        var s = new PinballMesh();
        s.Disc(Vector3.Zero, 1f, Colors.White, 14);
        return s.Build();
    }

    private static ArrayMesh LampArrow()
    {
        // a triangle pointing along +X (the lamp's direction), unit size
        var s = new PinballMesh();
        s.Tri(new Vector3(1.2f, 0, 0), new Vector3(-0.8f, 0, -0.8f), new Vector3(-0.8f, 0, 0.8f), Colors.White);
        s.Tri(new Vector3(1.2f, 0, 0), new Vector3(-0.8f, 0, 0.8f), new Vector3(-0.8f, 0, -0.8f), Colors.White);
        return s.Build();
    }
}
