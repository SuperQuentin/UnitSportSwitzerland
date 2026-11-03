using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Interiors.Pinball;

/// <summary>
/// Standing at a pinball machine (#1325): the player's eye at the cabinet, zoomed on the playfield with the
/// backbox in view, or straight down on the playfield (<see cref="PlayerInput.CameraToggle"/>, V / R3). When this
/// peer is up (<see cref="PinballTables.RunHere"/>) it runs the table here: the solver and rules stepped at 1 ms,
/// the flippers, plunger and nudges read every frame, a frame sent to the server 20 times a second. Otherwise it
/// watches what the server streams. E / Y presses Start (a new game, or joining one in its first ball); Esc / B
/// steps back. In VR the head stays the camera: the grips and triggers are the flipper buttons on the
/// cabinet's sides (they are the pad's shoulders, <c>XrPad</c>), A the plunger, X a nudge.
/// </summary>
public partial class PinballPlay : CanvasLayer
{
    public static PinballPlay? Current { get; private set; }

    private static readonly StringName ActLeft = PlayerInput.PinballLeft, ActRight = PlayerInput.PinballRight,
        ActPlunge = PlayerInput.PinballPlunge, ActNudge = PlayerInput.PinballNudge, ActNudgeLeft = PlayerInput.PinballNudgeLeft,
        ActNudgeRight = PlayerInput.PinballNudgeRight, ActView = PlayerInput.CameraToggle, ActStart = PlayerInput.InteractMount,
        ActCancel = "ui_cancel", ActStickX = PlayerInput.MoveRight, ActStickXNeg = PlayerInput.MoveLeft;

    /// <summary>A full pull of the plunger takes this long [s].</summary>
    private const float PullTime = 1.1f;
    /// <summary>A nudge's kick to the balls [m/s] and the least time between two.</summary>
    private const float NudgeKick = 0.16f, NudgeGap = 0.35f;

    private FootPlayer? _player;
    private InteriorNode _node = null!;
    private PinballCabinet _cabinet = null!;
    private string _key = "";
    private int _index;
    private PinballGame? _game;
    private double _acc, _sendAt, _nudgeAt, _clock;
    private int _heard, _cues;
    private bool _top, _wasUp;
    private int _ms;

    /// <summary>A robot plays the flippers and the plunger (/pinball robot): a check's and a shot queue's hands.</summary>
    public bool Robot;

    public bool TopView { get => _top; set => _top = value; }

    /// <summary>The rules of the game this client last ran (the net check compares them with the server's).</summary>
    public PinballRules? LastRules { get; private set; }

    /// <summary>Whether this client runs the table now (it is up).</summary>
    public bool Running => _game != null;

    public void PressStart() => PinballTables.Instance?.AskStart(_key, _index);
    private Camera3D? _cam, _own;
    private Label _hint = null!, _told = null!;
    private double _toldUntil;
    private readonly Vector3[] _poses = new Vector3[PinballCabinet.MaxBalls];

    /// <summary>The local player steps up to machine <paramref name="index"/> of the venue they are in.</summary>
    public static void Begin(FootPlayer? p, string key, int index)
    {
        if (Current != null || PinballTables.Instance is not { } tables) return;
        if (InteriorManager.Instance?.CurrentNode is not { } node || node.Layout.Key != key || node.PinballOf(index) is not { } cabinet) return;
        var play = new PinballPlay { Name = "PinballPlay", Layer = 11, _player = p, _node = node, _cabinet = cabinet, _key = key, _index = index };
        (Engine.GetMainLoop() as SceneTree)?.Root.AddChild(play);
    }

    public override void _Ready()
    {
        Current = this;
        UiFocus.Set(this, true);
        // the phone keeps its controls, in the pinball layout (#1353)
        TouchControls.Takeover = this;
        var tables = PinballTables.Instance!;
        tables.RunHere += OnRun;
        tables.JoinHere += OnJoin;
        tables.StopHere += OnStop;
        tables.Told += OnTold;
        BuildHud();
        if (!XR.XrSession.Active && GetViewport().GetCamera3D() is { } own)
        {
            _own = own;
            _cam = new Camera3D { Name = "PinballCamera", Fov = 52f, Near = 0.03f, Far = own.Far, Environment = own.Environment, Attributes = own.Attributes, CullMask = own.CullMask };
            _cabinet.AddChild(_cam);
            _cam.Transform = EyeView();
            _cam.MakeCurrent();
        }
        PlacePlayer();
        // a machine nobody plays: Start at once (the press that brought us here)
        if (tables.Peek(_key, _index) is not { Runner: not 0 }) tables.AskStart(_key, _index);
    }

    public override void _ExitTree()
    {
        if (Current == this) Current = null;
        UiFocus.Set(this, false);
        if (TouchControls.Takeover == this) TouchControls.Takeover = null;
        if (PinballTables.Instance is { } tables)
        {
            tables.RunHere -= OnRun;
            tables.JoinHere -= OnJoin;
            tables.StopHere -= OnStop;
            tables.Told -= OnTold;
            if (_game != null) tables.AskLeave(_key, _index);
        }
        if (_own != null && IsInstanceValid(_own)) _own.MakeCurrent();
        if (_cam != null && IsInstanceValid(_cam)) _cam.QueueFree();
        if (IsInstanceValid(_cabinet)) _cabinet.Rolling(0, false);
    }

    /// <summary>This client is up: the game to run (new, or taken over from the log).</summary>
    private void OnRun(string key, int index, PinballGame game)
    {
        if (key != _key || index != _index) return;
        _game = game;
        LastRules = game.Rules;
        _acc = 0;
        _sendAt = 0;
        _heard = 0;
        _cues = game.Rules.Cues.Count;
        _wasUp = true;
        // the Start pressed for a new game is already in its outbox: send it at once
        PinballTables.Instance?.SendFrame(_key, _index, game);
    }

    private void OnJoin(string key, int index)
    {
        if (key == _key && index == _index && _game != null) _game.Start();
    }

    private void OnStop(string key, int index)
    {
        if (key == _key && index == _index) _game = null;
    }

    private void OnTold(string why)
    {
        _told.Text = why;
        _toldUntil = _clock + 3;
    }

    /// <summary>Enter is the plunger here: kept from the chat, which opens on it (the action still sees it).</summary>
    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { PhysicalKeycode: Key.Enter or Key.KpEnter }) GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        if (Input.IsActionJustPressed(ActCancel))
        {
            QueueFree();
            return;
        }
        if (Input.IsActionJustPressed(ActView)) _top = !_top;
        if (Input.IsActionJustPressed(ActStart)) PinballTables.Instance?.AskStart(_key, _index);
        if (_game is { } g) Step(g, delta);
        if (_cam != null) _cam.Transform = _cam.Transform.InterpolateWith(_top ? TopCamera() : EyeView(), (float)Math.Min(1, delta * 8));
        if (_told.Visible != _clock < _toldUntil) _told.Visible = _clock < _toldUntil;
        UpdateHint();
    }

    /// <summary>The table run here: the inputs onto it, the 1 ms steps, the sounds, the frames to the server.</summary>
    private void Step(PinballGame g, double delta)
    {
        int me = PinballTables.Instance?.PlayersOf(_key, _index) is { Count: > 0 } players ? Math.Max(0, IndexOf(players)) : 0;
        bool up = g.Rules.State == PinballRules.Phase.Playing && g.Rules.Up == me;
        if (up)
        {
            g.Button(0, Input.IsActionPressed(ActLeft));
            g.Button(1, Input.IsActionPressed(ActRight));
            // the plunger: pulled back while held, let go to launch
            g.Solver.Pull = Input.IsActionPressed(ActPlunge) ? Mathf.Min(1, g.Solver.Pull + (float)delta / PullTime) : 0;
            if (_clock - _nudgeAt > NudgeGap)
            {
                float side = Input.GetActionStrength(ActStickX) - Input.GetActionStrength(ActStickXNeg);
                Vector2? kick = Input.IsActionJustPressed(ActNudge) ? new Vector2(Mathf.Abs(side) > 0.5f ? Mathf.Sign(side) * 0.6f : 0, 1)
                    : Input.IsActionJustPressed(ActNudgeLeft) ? new Vector2(-1, 0.3f)
                    : Input.IsActionJustPressed(ActNudgeRight) ? new Vector2(1, 0.3f) : null;
                if (kick is { } k)
                {
                    g.Nudge(k.Normalized() * NudgeKick);
                    _nudgeAt = _clock;
                    if (XR.XrSession.Active) Input.StartJoyVibration(XR.XrPad.DeviceId, 0.3f, 0.6f, 0.08f);
                }
            }
            _acc += Math.Min(delta, 0.1);
            int steps = (int)(_acc / PinballSolver.Dt);
            _acc -= steps * PinballSolver.Dt;
            if (!Robot) g.Run(steps);
            else
                for (int i = 0; i < steps; i++)
                {
                    PinballCabinet.RobotDriver.Play(g, _ms++);
                    g.Run(1);
                }
        }
        // the sounds of what happened since last frame, and the rules' cues
        for (; _heard < g.Outbox.Count; _heard++) _cabinet.Hear(g.Outbox[_heard]);
        for (; _cues < g.Rules.Cues.Count; _cues++) _cabinet.HearCue(g.Rules.Cues[_cues]);
        float fastest = 0;
        bool wire = false;
        foreach (var b in g.Solver.Balls)
        {
            float v = b.Ramp >= 0 ? Mathf.Abs(b.Vs) : b.Held >= 0 ? 0 : b.V.Length();
            if (v > fastest) { fastest = v; wire = b.Ramp >= 0 && !g.Table.Ramps[b.Ramp].Plastic; }
        }
        _cabinet.Rolling(fastest, wire);
        _cabinet.Show(g, _clock, _poses);
        // the frames: 20 a second while up, and once more as the turn ends
        if (up && _clock >= _sendAt || !up && _wasUp)
        {
            _sendAt = _clock + PinballTables.FrameEvery;
            PinballTables.Instance?.SendFrame(_key, _index, g);
            _heard = 0;
        }
        _wasUp = up;
        // the turn passed (or the game is over): the server shows this machine from now on, and hands it back when it is our turn
        if (!up) _game = null;
    }

    private long MyId => Multiplayer.MultiplayerPeer is { } p and not OfflineMultiplayerPeer ? Multiplayer.GetUniqueId() : 1;

    private int IndexOf(IReadOnlyList<long> players)
    {
        for (int i = 0; i < players.Count; i++)
            if (players[i] == MyId) return i;
        return -1;
    }

    // ---- where the player and the camera are -----------------------------------------------------

    private Transform3D CabinetFrame => _cabinet.Transform;

    /// <summary>The player at the lockdown bar, facing the backbox.</summary>
    private void PlacePlayer()
    {
        if (_player == null || !IsInstanceValid(_player)) return;
        var at = _cabinet.GlobalTransform * new Vector3(0, 0, PinballCabinet.Front + 0.32f);
        var ahead = _cabinet.GlobalTransform * new Vector3(0, 0, 0);
        _player.GlobalPosition = at with { Y = _player.GlobalPosition.Y };
        var dir = ahead - at;
        float yaw = Mathf.Atan2(-dir.X, -dir.Z);
        _player.LookYaw = yaw;
        _player.Rotation = new Vector3(0, yaw, 0);
    }

    /// <summary>A player's eye over the lockdown bar, the playfield filling the view and the backbox's display at its top.</summary>
    private static Transform3D EyeView()
    {
        var eye = new Vector3(0, 1.50f, PinballCabinet.Front + 0.40f);
        var look = new Vector3(0, 1.0f, -0.18f);
        return new Transform3D(Basis.Identity, eye).LookingAt(look, Vector3.Up);
    }

    /// <summary>Straight down on the playfield, the top of the table at the top of the screen.</summary>
    private static Transform3D TopCamera()
    {
        var centre = PinballCabinet.ToPiece(new Vector3(-0.0f, 0.52f, 0));
        var normal = PinballCabinet.FieldTilt * Vector3.Up;
        var upTable = PinballCabinet.FieldTilt * Vector3.Forward;
        return new Transform3D(Basis.Identity, centre + normal * 1.2f).LookingAt(centre, upTable);
    }

    // ---- the HUD ---------------------------------------------------------------------------------

    private void BuildHud()
    {
        var box = new VBoxContainer { AnchorLeft = 0, AnchorTop = 1, AnchorBottom = 1, OffsetLeft = 24, OffsetTop = -120, OffsetBottom = -24 };
        _hint = new Label();
        _hint.AddThemeFontSizeOverride("font_size", 18);
        _hint.AddThemeColorOverride("font_outline_color", Colors.Black);
        _hint.AddThemeConstantOverride("outline_size", 6);
        _told = new Label { Visible = false };
        _told.AddThemeFontSizeOverride("font_size", 22);
        _told.AddThemeColorOverride("font_color", new Color(1f, 0.8f, 0.3f));
        _told.AddThemeColorOverride("font_outline_color", Colors.Black);
        _told.AddThemeConstantOverride("outline_size", 6);
        box.AddChild(_told);
        box.AddChild(_hint);
        AddChild(box);
    }

    /// <summary>The prompt bar's rows while at the machine: on a phone, Start becomes a pill (#1353).</summary>
    public IEnumerable<(string, string)> Prompts()
    {
        if (_game != null)
        {
            yield return (PlayerInput.PinballPlunge, "Hold to pull the plunger");
            yield return (PlayerInput.PinballNudge, "Nudge");
        }
        yield return (PlayerInput.InteractMount, _game != null ? "Start / add a player" : "Start a game");
        yield return (PlayerInput.CameraToggle, _top ? "Player's view" : "Top view");
        yield return ("ui_cancel", "Step back");
    }

    private string _hintShown = "";

    private void UpdateHint()
    {
        // on a phone the keys and pills say it all (#1353)
        string text = TouchControls.Hinting ? ""
            : _game != null
            ? $"{InputHints.Prompt(PlayerInput.PinballLeft, "")}{InputHints.Prompt(PlayerInput.PinballRight, "flippers (or Left / Right Shift)")}   " +
              $"{InputHints.Prompt(PlayerInput.PinballPlunge, "hold to pull the plunger (or Enter)")}   {InputHints.Prompt(PlayerInput.PinballNudge, "nudge")}\n" +
              $"{InputHints.Prompt(PlayerInput.CameraToggle, _top ? "player's view" : "top view")}   {InputHints.Prompt(PlayerInput.InteractMount, "start / add a player")}   " +
              $"{InputHints.Prompt("ui_cancel", "step back")}"
            : $"{InputHints.Prompt(PlayerInput.InteractMount, "start a game, or join this one")}   {InputHints.Prompt(PlayerInput.CameraToggle, _top ? "player's view" : "top view")}   " +
              $"{InputHints.Prompt("ui_cancel", "step back")}";
        if (text == _hintShown) return;
        _hintShown = text;
        _hint.Text = text;
    }
}
