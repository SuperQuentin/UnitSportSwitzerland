using Godot;

namespace UnitSport.Interiors.Games.Darts;

/// <summary>
/// A dartboard's face, darts and sounds on a client (#1324). The cabinet is the room's mesh; the face is a disc with
/// the board's beds drawn once into a shared texture (<see cref="Face"/>), in front of a dark backing; each of the
/// visit's three darts is a node (one shared mesh), flown from the oche to where the server says it landed, then left
/// in the board until the visit's darts are pulled. The local player's aim shows as a ring on the face.
/// </summary>
public partial class DartsRig : TableRig
{
    private static ImageTexture? _face;
    private static ArrayMesh? _disc, _dart;

    private readonly MeshInstance3D[] _darts = new MeshInstance3D[DartsGame.Darts];
    private MeshInstance3D _ring = null!;
    private AudioStreamPlayer3D _voice = null!;
    private int _heard = -1, _shownBoard;

    /// <summary>The local player's aim on the face (mm from the bull), or null.</summary>
    public Vector2? Aim { get; set; }

    public DartsRig(FurniturePlan piece) => Init(piece);

    public static DartsRig Create(FurniturePlan piece) => new(piece);

    /// <summary>The bull in the piece's frame: on the cabinet's back board, facing +Z.</summary>
    public Vector3 Bull => new(0, DartBoard.BullHeight, -Piece.D / 2 + 0.095f);

    /// <summary>A point of the face (mm from the bull) in the piece's frame, <paramref name="out"/> metres in front of it.</summary>
    public Vector3 OnFace(Vector2 mm, float @out = 0) => Bull + new Vector3(mm.X / 1000f, mm.Y / 1000f, 0.004f + @out);

    /// <summary>Where a player throws from: the oche, at a hand's height.</summary>
    public Vector3 Hand => new(0.12f, 1.62f, Bull.Z + DartBoard.Oche);

    public override void _Ready()
    {
        var backing = new MeshInstance3D
        {
            Name = "Backing",
            Mesh = new CylinderMesh { TopRadius = 0.235f, BottomRadius = 0.235f, Height = 0.03f, RadialSegments = 40 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.06f, 0.06f, 0.06f) },
            Transform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), Bull - new Vector3(0, 0, 0.012f)),
        };
        AddChild(backing);
        var face = new MeshInstance3D
        {
            Name = "Face", Mesh = Disc(),
            MaterialOverride = new StandardMaterial3D { AlbedoTexture = Face(), Roughness = 0.95f, TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps },
            Position = Bull + new Vector3(0, 0, 0.004f),
        };
        AddChild(face);
        for (int i = 0; i < _darts.Length; i++)
        {
            _darts[i] = new MeshInstance3D { Name = $"Dart{i}", Mesh = Dart(), Visible = false };
            AddChild(_darts[i]);
        }
        _ring = new MeshInstance3D
        {
            Name = "AimRing", Visible = false,
            Mesh = new TorusMesh { InnerRadius = 0.006f, OuterRadius = 0.009f, Rings = 16, RingSegments = 6 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.95f, 0.2f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true },
        };
        AddChild(_ring);
        _voice = new AudioStreamPlayer3D { Name = "Voice", Bus = Audio.SfxBus.Name, UnitSize = 4f, MaxDistance = 20f, Position = Bull };
        AddChild(_voice);
    }

    public override void Show(TableGame? game, double now, double delta)
    {
        var g = game as DartsGame;
        int n = 0;
        if (g != null)
        {
            int count = g.Board.Count;
            for (int i = 0; i < count && n < _darts.Length; i++)
            {
                var (at, _) = g.Board[i];
                if (at.X > 9000) continue;
                bool newest = i == count - 1;
                double t = newest ? (now - g.ThrowAt) / DartsGame.Flight : 1;
                var d = _darts[n++];
                d.Visible = t >= 0;
                d.Transform = Flying(at, Mathf.Clamp((float)t, 0, 1));
            }
            // the newest dart lands: its thunk, once
            if (g.ThrowSeq != _heard && now - g.ThrowAt >= DartsGame.Flight)
            {
                bool first = _heard < 0;
                _heard = g.ThrowSeq;
                if (!first && g.Board.Count > 0 && g.Board[^1].At.X < 9000)
                {
                    _voice.Stream = g.Board[^1].Hit.Multiplier == 0 ? DartsSounds.Miss : DartsSounds.Thunk;
                    _voice.Position = OnFace(g.Board[^1].At);
                    _voice.Play();
                }
            }
            // the darts pulled from the board: a quick rustle
            int inBoard = g.InBoard().Count();
            if (inBoard < _shownBoard && _shownBoard > 0) { _voice.Stream = DartsSounds.Pull; _voice.Position = Bull; _voice.Play(); }
            _shownBoard = inBoard;
        }
        for (int i = n; i < _darts.Length; i++) _darts[i].Visible = false;
        _ring.Visible = Aim != null;
        if (Aim is { } aim) _ring.Transform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), OnFace(aim, 0.004f));
    }

    /// <summary>A dart <paramref name="t"/> of its way from the hand to <paramref name="at"/> on the face: an arc, the tip leading; stuck in at the end.</summary>
    private Transform3D Flying(Vector2 at, float t)
    {
        var to = OnFace(at, -0.012f);
        var from = Hand;
        // a lob: 0.18 m over the straight line at its middle
        var p = from.Lerp(to, t) + Vector3.Up * 0.72f * t * (1 - t) * 0.25f * 4;
        var ahead = (to - from) + Vector3.Up * 0.72f * (1 - 2 * t);
        if (t >= 1)
        {
            // in the board: a little droop, the same for the same place
            float droop = 0.12f + 0.08f * Mathf.Sin(at.X * 0.37f + at.Y * 0.11f);
            ahead = new Vector3(0, -droop, -1);
            p = to;
        }
        return new Transform3D(Basis.LookingAt(ahead.Normalized(), Vector3.Up), p);
    }

    // ---- shared meshes and the face's picture ------------------------------------------------------

    /// <summary>The face's disc (the double ring's outside and the numbers' band), facing +Z, UVs over the face's picture.</summary>
    private static ArrayMesh Disc()
    {
        if (_disc != null) return _disc;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float r = DartBoard.Radius / 1000f;
        const int seg = 80;
        for (int i = 0; i < seg; i++)
        {
            float a0 = Mathf.Tau * i / seg, a1 = Mathf.Tau * (i + 1) / seg;
            void V(float a, float rr)
            {
                var p = new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * rr;
                st.SetNormal(Vector3.Back);
                st.SetUV(new Vector2(0.5f + p.X / (2 * r), 0.5f - p.Y / (2 * r)));
                st.AddVertex(new Vector3(p.X, p.Y, 0));
            }
            // clockwise as seen from the front (+Z): Godot's front faces
            V(0, 0);
            V(a0, r);
            V(a1, r);
        }
        return _disc = st.Commit();
    }

    /// <summary>The board's picture, drawn once: black and cream beds, red and green rings, the bulls, the wire, a black numbers band.</summary>
    public static ImageTexture Face()
    {
        if (_face != null) return _face;
        const int size = 512;
        var img = Image.CreateEmpty(size, size, true, Image.Format.Rgb8);
        var black = new Color(0.07f, 0.07f, 0.07f);
        var cream = new Color(0.90f, 0.84f, 0.66f);
        var red = new Color(0.78f, 0.10f, 0.10f);
        var green = new Color(0.07f, 0.48f, 0.20f);
        var wire = new Color(0.78f, 0.78f, 0.80f);
        float[] rings = { DartBoard.Bull, DartBoard.OuterBull, DartBoard.TrebleIn, DartBoard.TrebleOut, DartBoard.DoubleIn, DartBoard.DoubleOut };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var mm = new Vector2((x + 0.5f - size / 2f) / (size / 2f), -(y + 0.5f - size / 2f) / (size / 2f)) * DartBoard.Radius;
                float r = mm.Length();
                var hit = DartBoard.Score(mm);
                int idx = Array.IndexOf(DartBoard.Order, hit.Value);
                bool even = idx % 2 == 0;
                Color c = r > DartBoard.DoubleOut ? black
                    : hit.Value == 25 ? hit.Multiplier == 2 ? red : green
                    : hit.Multiplier >= 2 ? even ? red : green
                    : even ? black : cream;
                // the spider: the rings' wires and the segments' radial ones
                bool onWire = false;
                foreach (float ring in rings) if (Mathf.Abs(r - ring) < 0.9f) onWire = true;
                if (r > DartBoard.OuterBull && r < DartBoard.DoubleOut)
                {
                    float deg = (Mathf.RadToDeg(Mathf.Atan2(mm.X, mm.Y)) + 9 + 360) % 18;
                    if (Mathf.Min(deg, 18 - deg) * Mathf.Pi / 180f * r < 0.8f) onWire = true;
                }
                img.SetPixel(x, y, onWire ? wire : c);
            }
        DrawNumbers(img, size);
        img.GenerateMipmaps();
        return _face = ImageTexture.CreateFromImage(img);
    }

    /// <summary>The numbers round the double ring, in a 3 x 5 pixel font scaled up, white on the black band.</summary>
    private static void DrawNumbers(Image img, int size)
    {
        string[] glyphs =
        {
            "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
            "111100111001111", "111100111101111", "111001010010010", "111101111101111", "111101111001111",
        };
        float rMid = (DartBoard.DoubleOut + DartBoard.Radius) / 2f;
        const int px = 3;
        for (int i = 0; i < 20; i++)
        {
            string text = DartBoard.Order[i].ToString();
            float a = Mathf.DegToRad(i * 18f);
            var centre = new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * rMid;
            int cx = (int)(size / 2f + centre.X / DartBoard.Radius * size / 2f), cy = (int)(size / 2f - centre.Y / DartBoard.Radius * size / 2f);
            int w = text.Length * 4 * px - px, h = 5 * px;
            for (int k = 0; k < text.Length; k++)
            {
                string g = glyphs[text[k] - '0'];
                for (int gy = 0; gy < 5; gy++)
                    for (int gx = 0; gx < 3; gx++)
                    {
                        if (g[gy * 3 + gx] != '1') continue;
                        for (int sy = 0; sy < px; sy++)
                            for (int sx = 0; sx < px; sx++)
                            {
                                int x = cx - w / 2 + k * 4 * px + gx * px + sx, y = cy - h / 2 + gy * px + sy;
                                if (x >= 0 && y >= 0 && x < size && y < size) img.SetPixel(x, y, Colors.White);
                            }
                    }
            }
        }
    }

    /// <summary>A steel-tip dart, its tip at the origin pointing −Z: the point, a tungsten barrel, the shaft, two crossed flights.</summary>
    private static ArrayMesh Dart()
    {
        if (_dart != null) return _dart;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        void Box(Vector3 min, Vector3 max, Color c)
        {
            st.SetColor(c);
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++) corners[i] = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
            int[][] faces = { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } };
            Vector3[] normals = { Vector3.Forward, Vector3.Back, Vector3.Down, Vector3.Up, Vector3.Left, Vector3.Right };
            for (int f = 0; f < 6; f++)
            {
                var q = faces[f];
                foreach (int k in new[] { q[0], q[1], q[2], q[0], q[2], q[3] })
                {
                    st.SetNormal(normals[f]);
                    st.AddVertex(corners[k]);
                }
            }
        }
        Box(new Vector3(-0.001f, -0.001f, 0), new Vector3(0.001f, 0.001f, 0.03f), new Color(0.8f, 0.8f, 0.82f));
        Box(new Vector3(-0.0035f, -0.0035f, 0.03f), new Vector3(0.0035f, 0.0035f, 0.075f), new Color(0.45f, 0.45f, 0.5f));
        Box(new Vector3(-0.002f, -0.002f, 0.075f), new Vector3(0.002f, 0.002f, 0.11f), new Color(0.1f, 0.1f, 0.12f));
        var flight = new Color(0.9f, 0.15f, 0.2f);
        Box(new Vector3(-0.0005f, -0.017f, 0.095f), new Vector3(0.0005f, 0.017f, 0.135f), flight);
        Box(new Vector3(-0.017f, -0.0005f, 0.095f), new Vector3(0.017f, 0.0005f, 0.135f), flight);
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D { VertexColorUseAsAlbedo = true, Metallic = 0.3f, Roughness = 0.5f });
        return _dart = mesh;
    }
}
