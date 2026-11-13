using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Interiors.Games;

/// <summary>
/// Standing at a playable piece (#1324, #1326): the camera on the game, its controls read here and turned into
/// the server's actions, a HUD line of the game and the keys. Stepping up takes a seat (or watches when the game is
/// full); E / Y with a free seat and nobody else adds a bot opponent; V / R3 changes the view; Esc / B steps back and
/// gives the seat up. Each game is a <see cref="Mode"/>: its camera, its controls, its prompts. In VR the head
/// stays the camera and the game reads the hands (a mode's <see cref="Mode.Vr"/>).
/// </summary>
public partial class TableGamePlay : CanvasLayer
{
    public static TableGamePlay? Current { get; private set; }

    /// <summary>One game's controls and camera.</summary>
    public abstract class Mode
    {
        public TableGamePlay Play { get; internal set; } = null!;
        protected TableGames Games => TableGames.Instance!;
        protected string Key => Play.Key;
        protected int Index => Play.Index;
        protected TableRig Rig => Play.Rig;
        /// <summary>This peer's seat, or -1 watching.</summary>
        protected int Seat => Play.Game?.SeatOf(Games.MyId) ?? -1;

        /// <summary>Where the player's body stands, in the piece's frame (the game's own place for the seat).</summary>
        public abstract Vector3 Stand(int seat);
        /// <summary>The camera in the piece's frame; <paramref name="alt"/> is the other view (V / R3).</summary>
        public abstract Transform3D Camera(TableGame? g, int seat, bool alt);
        /// <summary>Every frame: read the controls, ask the server.</summary>
        public abstract void Process(TableGame? g, int seat, double delta);
        /// <summary>The prompt rows (the prompt bar, a phone's keys): the game's own, the frame's added after.</summary>
        public abstract IEnumerable<(string Action, string Text)> Prompts(TableGame? g, int seat);
        /// <summary>The camera's field of view [°].</summary>
        public virtual float Fov => 60f;
        /// <summary>VR: the hands instead of the stick (when built). False: the pad's buttons through XrPad.</summary>
        public virtual bool Vr => false;
        /// <summary>An action that eats Enter (kept from the chat while here).</summary>
        public virtual bool EatsEnter => false;
        public virtual void Exit() { }
    }

    public string Key { get; private set; } = "";
    public int Index { get; private set; }
    public TableRig Rig { get; private set; } = null!;
    public GameSpec Spec { get; private set; } = null!;
    public TableGame? Game => TableGames.Instance?.GameAt(Key, Index);
    public Mode Controls { get; private set; } = null!;
    public bool AltView { get; set; }

    private FootPlayer? _player;
    private Camera3D? _cam, _own;
    private Label _line = null!, _hint = null!, _told = null!;
    private double _clock, _toldUntil;
    private bool _placed;
    private FreshPress _bot;

    private static readonly StringName ActCancel = "ui_cancel", ActView = PlayerInput.CameraToggle, ActBot = PlayerInput.InteractMount;

    public static void Begin(FootPlayer? p, InteriorNode node, int index, GameSpec spec)
    {
        if (Current != null || TableGames.Instance == null || spec.Play == null || node.TableRigOf(index) is not { } rig) return;
        var play = new TableGamePlay { Name = "TableGamePlay", Layer = 11, _player = p, Key = node.Layout.Key, Index = index, Rig = rig, Spec = spec };
        play.Controls = spec.Play();
        play.Controls.Play = play;
        (Engine.GetMainLoop() as SceneTree)?.Root.AddChild(play);
    }

    public override void _Ready()
    {
        Current = this;
        UiFocus.Set(this, true);
        TouchControls.Takeover = this;
        var games = TableGames.Instance!;
        games.Told += OnTold;
        BuildHud();
        if (!XR.XrSession.Active && GetViewport().GetCamera3D() is { } own)
        {
            _own = own;
            _cam = new Camera3D { Name = "GameCamera", Fov = Controls.Fov, Near = 0.03f, Far = own.Far, Environment = own.Environment, Attributes = own.Attributes, CullMask = own.CullMask };
            Rig.AddChild(_cam);
            _cam.Transform = Controls.Camera(Game, -1, false);
            _cam.MakeCurrent();
        }
        games.AskSit(Key, Index);
    }

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        UiFocus.Set(this, false);
        if (TouchControls.Takeover == this) TouchControls.Takeover = null;
        Controls.Exit();
        if (TableGames.Instance is { } games)
        {
            games.Told -= OnTold;
            games.AskLeave(Key, Index);
        }
        if (_own != null && IsInstanceValid(_own)) _own.MakeCurrent();
        if (_cam != null && IsInstanceValid(_cam)) _cam.QueueFree();
    }

    private void OnTold(string why)
    {
        _told.Text = why;
        _toldUntil = _clock + 3;
    }

    public override void _Input(InputEvent e)
    {
        if (Controls.EatsEnter && e is InputEventKey { PhysicalKeycode: Godot.Key.Enter or Godot.Key.KpEnter }) GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        if (!IsInstanceValid(Rig))
        {
            QueueFree();
            return;
        }
        if (Input.IsActionJustPressed(ActCancel))
        {
            QueueFree();
            return;
        }
        var g = Game;
        int seat = g?.SeatOf(TableGames.Instance!.MyId) ?? -1;
        if (Input.IsActionJustPressed(ActView)) AltView = !AltView;
        // E stepped up to the table: that press does not also sit down or add a bot (#1813)
        if (_bot.Take(ActBot) && g != null)
        {
            if (seat < 0) TableGames.Instance!.AskSit(Key, Index);
            else if (g.Bots && g.FreeSeat() >= 0) TableGames.Instance!.AskBot(Key, Index);
        }
        if (!_placed && seat >= 0) { PlacePlayer(seat); _placed = true; }
        Controls.Process(g, seat, delta);
        if (_cam != null) _cam.Transform = _cam.Transform.InterpolateWith(Controls.Camera(g, seat, AltView), (float)Math.Min(1, delta * 6));
        string line = g?.Summary(TableGames.Instance!.NameOf) ?? Spec.Title;
        if (_line.Text != line) _line.Text = line;
        if (_told.Visible != _clock < _toldUntil) _told.Visible = _clock < _toldUntil;
        UpdateHint(g, seat);
    }

    /// <summary>The body at its seat's place, facing the piece.</summary>
    private void PlacePlayer(int seat)
    {
        if (_player == null || !IsInstanceValid(_player)) return;
        var at = Rig.GlobalTransform * Controls.Stand(seat);
        var centre = Rig.GlobalTransform * Vector3.Zero;
        _player.GlobalPosition = at with { Y = _player.GlobalPosition.Y };
        var dir = centre - at;
        if (dir.LengthSquared() < 1e-4f) return;
        float yaw = Mathf.Atan2(-dir.X, -dir.Z);
        _player.LookYaw = yaw;
        _player.Rotation = new Vector3(0, yaw, 0);
    }

    // ---- the HUD -------------------------------------------------------------------------------

    private void BuildHud()
    {
        var top = new VBoxContainer { AnchorLeft = 0.5f, AnchorRight = 0.5f, OffsetLeft = -400, OffsetRight = 400, OffsetTop = 20 };
        _line = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        Style(_line, 22, Colors.White);
        top.AddChild(_line);
        AddChild(top);
        var box = new VBoxContainer { AnchorLeft = 0, AnchorTop = 1, AnchorBottom = 1, OffsetLeft = 24, OffsetTop = -120, OffsetBottom = -24 };
        _hint = new Label();
        Style(_hint, 18, Colors.White);
        _told = new Label { Visible = false };
        Style(_told, 22, new Color(1f, 0.8f, 0.3f));
        box.AddChild(_told);
        box.AddChild(_hint);
        AddChild(box);
    }

    private static void Style(Label l, int size, Color c)
    {
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", c);
        l.AddThemeColorOverride("font_outline_color", Colors.Black);
        l.AddThemeConstantOverride("outline_size", 6);
    }

    /// <summary>The prompt rows while here: the game's, then a bot, the view, stepping back.</summary>
    public IEnumerable<(string, string)> Prompts()
    {
        var g = Game;
        int seat = g?.SeatOf(TableGames.Instance?.MyId ?? 0) ?? -1;
        foreach (var row in Controls.Prompts(g, seat)) yield return row;
        if (seat < 0 && g?.FreeSeat() >= 0) yield return (PlayerInput.InteractMount, "Take a place");
        else if (seat >= 0 && g is { Bots: true } && g.FreeSeat() >= 0) yield return (PlayerInput.InteractMount, "Add a bot opponent");
        yield return (PlayerInput.CameraToggle, AltView ? "Player's view" : "Other view");
        yield return ("ui_cancel", "Step back");
    }

    private string _hintShown = "";

    private void UpdateHint(TableGame? g, int seat)
    {
        string text = "";
        if (!TouchControls.Hinting)
        {
            var parts = new List<string>();
            foreach (var (action, words) in Prompts()) parts.Add(InputHints.Prompt(action, words.ToLowerInvariant()));
            text = string.Join("   ", parts);
        }
        if (text == _hintShown) return;
        _hintShown = text;
        _hint.Text = text;
    }
}
