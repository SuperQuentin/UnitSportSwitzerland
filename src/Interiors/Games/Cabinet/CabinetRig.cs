using Godot;
using UnitSport.Interiors.Pinball;

namespace UnitSport.Interiors.Games.Cabinet;

/// <summary>
/// An arcade machine's screen on a client (#1326): a plate a little in front of the room mesh's lit screen, its picture a
/// 160 x 120 texture. Nobody playing here: the attract screen (the title, "INSERT BATZEN", who plays, the high scores),
/// drawn when the state changes and once a second for its blink. The local player's game: drawn into the same texture
/// every frame by <see cref="CabinetPlay"/>. Nothing rebuilds a mesh; only the image changes.
/// </summary>
public partial class CabinetRig : TableRig, IScreen
{
    /// <summary>What a machine plays: a racer's cockpit races, a dance or drum machine keeps the beat, an upright shoots or races by its place.</summary>
    public static MiniGame.Kind PlaysOf(FurniturePlan f) => f.Type switch
    {
        FurnitureType.Racer => MiniGame.Kind.Racer,
        FurnitureType.DanceMachine or FurnitureType.DrumGame => MiniGame.Kind.Rhythm,
        _ => (int)Mathf.PosMod(Mathf.Floor(f.X * 1.7f) + Mathf.Floor(f.Z * 2.3f), 3) == 1 ? MiniGame.Kind.Racer : MiniGame.Kind.Shooter,
    };

    /// <summary>The screen's middle and size in the piece's frame (the room mesh's lit screen of each machine), facing +Z.</summary>
    public static (Vector3 Centre, Vector2 Size) Screen(FurniturePlan f)
    {
        float w = f.W / 2, d = f.D / 2;
        // the racer's, dance machine's and drums' screens are lit plates 12 mm proud of their cabinet's face (the mesh's sink)
        const float proud = 0.012f;
        return f.Type switch
        {
            FurnitureType.Racer => (new Vector3((-w + 0.06f - 0.03f) / 2, 1.25f, -d + 0.75f + proud), new Vector2(w - 0.09f, 0.6f)),
            FurnitureType.DanceMachine => (new Vector3(0, 1.4f, -d + 0.7f + proud), new Vector2(0.76f, 0.6f)),
            FurnitureType.DrumGame => (new Vector3(0, 1.25f, -d + 0.65f + proud), new Vector2(2 * w - 0.16f, 0.5f)),
            _ => (new Vector3(0, 1.35f, d - 0.285f), new Vector2(2 * w - 0.16f, 0.46f)),
        };
    }

    private Image _image = null!;
    private ImageTexture _texture = null!;
    private CabinetGame? _shown;
    private int _blink = -1;
    private string _drawn = "";

    /// <summary>The local player's game is drawn here: the attract screen stays off.</summary>
    public bool Live { get; set; }

    public CabinetRig(FurniturePlan piece) => Init(piece);

    public static CabinetRig Create(FurniturePlan piece) => new(piece);

    public override void _Ready()
    {
        _image = Image.CreateEmpty(MiniGame.Width, MiniGame.Height, false, Image.Format.Rgb8);
        _texture = ImageTexture.CreateFromImage(_image);
        var material = new StandardMaterial3D { AlbedoTexture = _texture, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest };
        var (centre, size) = Screen(Piece);
        // 9 mm in front of the lit screen: a plate of its own (zfight)
        AddChild(new MeshInstance3D { Name = "Screen", Mesh = new QuadMesh { Size = size }, MaterialOverride = material, Position = centre + new Vector3(0, 0, 0.009f) });
        // a racer's twin screens show the same race
        if (Piece.Type == FurnitureType.Racer)
            AddChild(new MeshInstance3D { Name = "Screen2", Mesh = new QuadMesh { Size = size }, MaterialOverride = material, Position = new Vector3(-centre.X, centre.Y, centre.Z + 0.009f) });
        Attract(null, 0);
    }

    public override void OnState(TableGame game, double now)
    {
        _shown = game as CabinetGame;
        _drawn = "";
    }

    public override void Show(TableGame? game, double now, double delta)
    {
        if (Live) return;
        var g = game as CabinetGame;
        int blink = (int)(now * 1.5) % 6;
        if (blink != _blink || !ReferenceEquals(g, _shown)) { _blink = blink; _shown = g; Attract(g, blink); }
    }

    /// <summary>The attract screen: the title, then by turns "INSERT BATZEN", who plays, the high scores.</summary>
    private void Attract(CabinetGame? g, int blink)
    {
        var kind = g?.Plays ?? PlaysOf(Piece);
        string key = $"{kind}|{blink}|{g?.Playing}|{g?.Call}|{(g == null ? "" : string.Join(",", g.Highs))}";
        if (key == _drawn) return;
        _drawn = key;
        var bg = kind switch { MiniGame.Kind.Racer => new Color(0.05f, 0.10f, 0.05f), MiniGame.Kind.Rhythm => new Color(0.10f, 0.02f, 0.12f), _ => new Color(0.02f, 0.03f, 0.10f) };
        Clear(bg);
        string title = MiniGame.TitleOf(kind);
        var gold = new Color(1f, 0.85f, 0.2f);
        Center(title, 10, title.Length > 12 ? 1 : 2, gold);
        if (g is { Playing: true })
        {
            Center("NOW PLAYING", 50, 1, Colors.White);
            Center(g.Call.Replace(" is playing", "").ToUpperInvariant(), 62, 1, new Color(0.5f, 0.9f, 1f));
        }
        else if (blink < 3 || g == null || g.Highs.Count == 0)
        {
            if (blink % 2 == 0) Center("INSERT BATZEN", 54, 1, Colors.White);
            if (g is { LastScore: >= 0 }) Center($"LAST {g.LastScore}", 80, 1, new Color(0.7f, 0.7f, 0.7f));
        }
        else
        {
            Center("HIGH SCORES", 36, 1, Colors.White);
            for (int i = 0; i < g.Highs.Count; i++)
            {
                var (name, score) = g.Highs[i];
                string n = name.ToUpperInvariant();
                if (n.Length > 12) n = n[..12];
                Text($"{i + 1} {n}", 14, 50 + i * 12, 1, new Color(0.5f, 0.9f, 1f));
                string s = score.ToString();
                Text(s, MiniGame.Width - 14 - (s.Length * 6 - 1), 50 + i * 12, 1, gold);
            }
        }
        Present();
    }

    private void Center(string text, int y, int scale, Color c) => Text(text, (MiniGame.Width - PinballFont.Width(text, scale)) / 2, y, scale, c);

    /// <summary>The local player's game: drawn and shown.</summary>
    public void DrawGame(MiniGame game)
    {
        game.Draw(this);
        Present();
    }

    private void Present() => _texture.Update(_image);

    /// <summary>What the screen shows now (the check reads its pixels).</summary>
    public Image Picture => _image;

    // ---- IScreen: the 160 x 120 picture --------------------------------------------------------

    public void Clear(Color c) => _image.Fill(c);

    public void Rect(int x, int y, int w, int h, Color c)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(MiniGame.Width, x + w), y1 = Math.Min(MiniGame.Height, y + h);
        if (x1 > x0 && y1 > y0) _image.FillRect(new Rect2I(x0, y0, x1 - x0, y1 - y0), c);
    }

    public void Text(string text, int x, int y, int scale, Color c) =>
        PinballFont.Draw(text, x, y, scale, (px, py) => { if (px >= 0 && py >= 0 && px < MiniGame.Width && py < MiniGame.Height) _image.SetPixel(px, py, c); });
}
