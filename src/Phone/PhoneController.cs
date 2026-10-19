using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Phone;

/// <summary>
/// The local player's phone (#889): taken out, used folded from its cover screen, opened onto the
/// inner screen, put away. It drives <see cref="FootPlayer.PhoneState"/> (replicated) and the
/// owner's phone camera (<see cref="FootPlayer.PhoneLook"/>, <see cref="FootPlayer.PhoneCover"/>);
/// once the camera is on the open screen, that screen's picture is shown full screen and takes the
/// input. A teleport asked while the phone is out waits until the screen covers the view
/// (<see cref="Teleporter.Curtain"/>), then the phone stays on "Travelling…" until the ground there
/// has streamed in, and folds away at the new place. Docs: <c>docs/notes/ui/phone.md</c>.
/// <list type="bullet">
/// <item>Keyboard: Up arrow takes it out, Up again opens it; Left / Right pick a quick action, Enter
/// / Space / click runs it; Down or Esc puts it away (open: Esc goes back, then closes).</item>
/// <item>Pad: hold Back → Phone in the quick menu; D-pad and A / B inside, D-pad up opens it.</item>
/// <item>VR: not yet (a gap in <c>xr/vr-action-map</c>): no camera moves in VR.</item>
/// </list>
/// </summary>
public partial class PhoneController : Node
{
    private enum Stage { Pocket, Drawing, Folded, Opening, Open, Closing, Stowing }

    /// <summary>Seconds for the camera to come over the shoulder, and onto the screen.</summary>
    private const float LookSeconds = 0.5f, CoverSeconds = 0.6f;
    /// <summary>After landing at a travel's end: this long on "Travelling…" so the place is built before it is seen.</summary>
    private const double SettleSeconds = 0.9;
    private const double TravelTimeout = 20.0;

    public static PhoneController? Instance { get; private set; }

    private readonly Func<FootPlayer?> _player;
    private readonly Func<bool> _blocked;
    private PhoneScreens _screens = null!;
    private CanvasLayer _overlay = null!;
    private TextureRect _full = null!;
    private Label _hint = null!;
    private AudioStreamPlayer _sound = null!;
    private readonly Random _rng = new();

    private Stage _stage = Stage.Pocket;
    private float _look, _cover, _fold;
    private int _quick;
    private readonly List<PhoneScreens.Quick> _quicks = new();
    private readonly List<Action> _quickActions = new();
    /// <summary>The page to open on once open (a quick action that needs the big screen).</summary>
    private PhoneScreens.Page _openOn = PhoneScreens.Page.Home;
    /// <summary>A teleport held back until the screen covers the view.</summary>
    private Action? _pendingJump;
    /// <summary>Travelling: since when (real clock), and when the body landed there (−1: not yet).</summary>
    private double _travelSince = -1, _landedAt = -1;
    private bool _arrived;
    /// <summary>Opened by a teleport (not by the player): it folds away by itself once there.</summary>
    private bool _stowWhenFolded;
    private string _hintShown = "";
    private InviteService? _invites;

    /// <summary>The two screens, for the checks.</summary>
    internal PhoneScreens Screens => _screens;

    public bool IsOut => _stage != Stage.Pocket;
    public bool IsCovering => _stage == Stage.Open;

    /// <param name="player">The local body, on foot; null when there is none.</param>
    /// <param name="blocked">A menu or the loading screen owns the screen.</param>
    public PhoneController(Func<FootPlayer?> player, Func<bool> blocked, Func<PlaceSearchUi?> places, Func<ChatManager?> chat)
    {
        _player = player;
        _blocked = blocked;
        Name = "Phone";
        _screens = new PhoneScreens(places, chat);
    }

    public PhoneController() : this(() => null, () => true, () => null, () => null) { }

    public override void _EnterTree()
    {
        Instance = this;
        Teleporter.Curtain = Curtain;
        Teleporter.Arrived += OnArrived;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (Teleporter.Curtain == Curtain) Teleporter.Curtain = null;
        Teleporter.Arrived -= OnArrived;
        if (_invites != null) _invites.Arrived -= OnCall;
        UiFocus.Set(this, false);
    }

    public override void _Ready()
    {
        AddChild(_screens);
        _screens.TravelPicked += place => Travel(place.E, place.N, place.Name);
        _screens.CommandPicked += cmd =>
        {
            GD.Print($"[phone] {cmd}");
            GameShell.Instance?.World?.GetNodeOrNull<ChatManager>(ChatManager.NodeName)?.Send(cmd);
            InviteToasts.Instance?.Flash("Calling…");
        };
        _screens.CallAnswered += Answer;
        _screens.ActivitiesPicked += () => ActivitiesScreen.Open(ActivitiesScreen.Tab.BattleRoyale);

        // over the HUD and the chat line, under the shell's pages (40) that open over the phone
        _overlay = new CanvasLayer { Name = "PhoneOverlay", Layer = 30 };
        AddChild(_overlay);
        _full = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false,
        };
        _full.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _overlay.AddChild(_full);
        _hint = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center, Theme = UiTheme.Get() };
        _hint.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.7f));
        _hint.AddThemeConstantOverride("shadow_offset_y", 2);
        _hint.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _hint.OffsetTop = -64; _hint.OffsetBottom = -36;
        _overlay.AddChild(_hint);
        _sound = new AudioStreamPlayer { Name = "PhoneSound", Bus = Audio.SfxBus.Name, VolumeDb = -8f };
        AddChild(_sound);
    }

    // ------------------------------------------------------------------ states

    /// <summary>Takes the phone out (folded). False when it cannot come out now.</summary>
    public bool TakeOut()
    {
        if (_stage != Stage.Pocket || !CanUse(out _)) return false;
        GD.Print("[phone] out");
        _stage = Stage.Drawing;
        _quick = 0;
        _screens.ShowPage(PhoneScreens.Page.Home);
        UiFocus.Set(this, true);
        Play(PhoneSounds.Draw);
        return true;
    }

    /// <summary>Opens the phone onto <paramref name="page"/>, taking it out first if it is in the pocket.</summary>
    public void Open(PhoneScreens.Page page = PhoneScreens.Page.Home)
    {
        if (_stage == Stage.Pocket && !TakeOut()) return;
        _openOn = page;
        if (_stage == Stage.Open) { _screens.ShowPage(page); return; }
        if (_stage is Stage.Opening) return;
        GD.Print($"[phone] open {page}");
        _stage = Stage.Opening;
        _screens.ShowPage(page);
        Play(PhoneSounds.Hinge);
    }

    /// <summary>Back from an app to home; from home, folds the phone.</summary>
    private void Back()
    {
        if (_screens.Shown is PhoneScreens.Page.Map or PhoneScreens.Page.Contacts)
        {
            _screens.ShowPage(PhoneScreens.Page.Home);
            Play(PhoneSounds.Tap);
            return;
        }
        if (_screens.Shown == PhoneScreens.Page.Travel) return;   // travelling: it folds by itself once there
        Fold();
    }

    /// <summary>Folds the open phone back to its cover screen.</summary>
    public void Fold()
    {
        if (_stage is not (Stage.Open or Stage.Opening)) return;
        GD.Print("[phone] fold");
        _stage = Stage.Closing;
        ShowFull(false);
        Play(PhoneSounds.Hinge);
    }

    /// <summary>Puts the phone away, folding it first if it is open.</summary>
    public void PutAway()
    {
        if (_stage == Stage.Pocket || _stage == Stage.Stowing) return;
        GD.Print("[phone] away");
        if (_stage is Stage.Open or Stage.Opening or Stage.Closing)
        {
            _stowWhenFolded = true;   // the closing hands over to stowing
            Fold();
            return;
        }
        _stage = Stage.Stowing;
    }

    /// <summary>Into the pocket at once, without the camera's way back (no body, a vehicle, swimming).</summary>
    private void Drop()
    {
        _stage = Stage.Pocket;
        _look = _cover = _fold = 0f;
        _pendingJump?.Invoke();
        _pendingJump = null;
        _travelSince = -1;
        _stowWhenFolded = false;
        ShowFull(false);
        UiFocus.Set(this, false);
        _screens.SetActive(false);
        _hint.Visible = false;
    }

    private bool CanUse(out string? why)
    {
        why = null;
        var p = _player();
        if (p == null || _blocked() || XR.XrSession.Active) { why = "Not now"; return false; }
        if (p is not { Ride: RideKind.OnFoot, RidingAlong: false, IsSwimming: false, Ragdolled: false, Downed: false, Fighting: false })
        {
            why = "On foot only";
            return false;
        }
        return true;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        // the client's invite service comes with the connection, after this node: follow whichever is current
        if (InviteService.Client != _invites)
        {
            if (_invites != null) _invites.Arrived -= OnCall;
            _invites = InviteService.Client;
            if (_invites != null) _invites.Arrived += OnCall;
        }
        var p = _player();
        if (_stage != Stage.Pocket && (p == null || !CanUse(out _) && !_blocked()))
        {
            if (p != null) Release(p);
            Drop();
            return;
        }
        if (_stage == Stage.Pocket || p == null) return;

        // the camera moves only once the phone is in the hands
        bool inHands = p.PhoneModel is { Visible: true };
        float lookTo = _stage == Stage.Stowing ? 0f : inHands ? 1f : _look;
        float coverTo = _stage is Stage.Opening or Stage.Open ? 1f : 0f;
        _look = Mathf.MoveToward(_look, lookTo, dt / LookSeconds);
        _fold = Mathf.MoveToward(_fold, coverTo, dt / PhoneModel.UnfoldSeconds);
        // the lens leaves for the screen once the hinge is a third open, and is off it before it shuts
        float coverWant = coverTo > 0f ? (_fold > 0.3f ? 1f : _cover) : 0f;
        _cover = Mathf.MoveToward(_cover, coverWant, dt / CoverSeconds);

        switch (_stage)
        {
            case Stage.Drawing when _look >= 1f:
                _stage = Stage.Folded;
                break;
            case Stage.Opening when _cover >= 1f && _fold >= 1f:
                _stage = Stage.Open;
                ShowFull(true);
                if (_pendingJump != null) Callable.From(RunPendingJump).CallDeferred();
                break;
            case Stage.Closing when _cover <= 0f && _fold <= 0.05f:
                _stage = Stage.Folded;
                if (_stowWhenFolded) { _stowWhenFolded = false; _stage = Stage.Stowing; }
                break;
            case Stage.Stowing when _look <= 0f:
                Release(p);
                Drop();
                GD.Print("[phone] in the pocket");
                return;
        }

        p.PhoneState = _stage switch
        {
            Stage.Opening or Stage.Open => FootPlayer.PhoneOpenState,
            _ => FootPlayer.PhoneFoldedState,
        };
        p.PhoneLook = _look;
        p.PhoneCover = _cover;
        p.PhoneFold = _fold;

        _screens.SetActive(true);
        _screens.Fit(GetViewport().GetVisibleRect().Size);
        if (p.PhoneModel is { HasLiveScreens: false } model) model.SetScreens(_screens.Cover.GetTexture(), _screens.Inner.GetTexture());
        _screens.Tick(ClockText(), DateText());
        RefreshQuick();
        _screens.ShowCover(_quicks, _quick);
        StepTravel();
        // the full-screen picture fades in over the same picture on the 3D screen: any colour step
        // between the two (the world's tonemap) is a fade, not a jump
        if (_full.Visible && _full.Modulate.A < 1f) _full.Modulate = new Color(1, 1, 1, Mathf.MoveToward(_full.Modulate.A, 1f, dt / 0.15f));
        ShowHint();
    }

    private static void Release(FootPlayer p)
    {
        p.PhoneState = FootPlayer.PhonePocketState;
        p.PhoneLook = p.PhoneCover = p.PhoneFold = 0f;
    }

    private void ShowFull(bool on)
    {
        _full.Texture = _screens.Inner.GetTexture();
        _full.Visible = on;
        _full.Modulate = new Color(1, 1, 1, on ? 0f : 1f);
        if (on) Input.MouseMode = Input.MouseModeEnum.Visible;
        else if (!_blocked()) MouseCapture.Capture();
    }

    private void ShowHint()
    {
        string text = _stage is Stage.Folded or Stage.Drawing
            ? InputHints.Pad ? "D-pad ↑ open     ← →  choose     A  select     B  put away"
                : "↑ open     ← →  choose     Enter  select     ↓  put away"
            : "";
        _hint.Visible = text.Length > 0 && !_blocked();
        if (text != _hintShown) _hint.Text = _hintShown = text;
    }

    // ------------------------------------------------------------------ quick actions

    private void RefreshQuick()
    {
        _quicks.Clear();
        _quickActions.Clear();
        if (InviteService.Client?.Newest is { } call)
        {
            int id = call.Id;
            Add(new("Answer", $"{call.PeerName}: {call.Title}", new Color(0.16f, 0.55f, 0.28f, 0.85f)), () => Answer(id, true));
            Add(new("Decline", $"{call.PeerName}: {call.Title}", new Color(0.62f, 0.18f, 0.16f, 0.85f)), () => Answer(id, false));
        }
        Add(new("Map"), () => Open(PhoneScreens.Page.Map));
        Add(new("Contacts"), () => Open(PhoneScreens.Page.Contacts));
        if (Permissions.Online) Add(new("Activities"), () => { Open(); ActivitiesScreen.Open(ActivitiesScreen.Tab.BattleRoyale); });
        Add(new("Open"), () => Open());
        _quick = Mathf.Clamp(_quick, 0, _quicks.Count - 1);

        void Add(PhoneScreens.Quick q, Action run)
        {
            _quicks.Add(q);
            _quickActions.Add(run);
        }
    }

    private void Answer(int id, bool accept)
    {
        if (InviteService.Client is not { } invites) return;
        GD.Print($"[phone] {(accept ? "answer" : "decline")} {id}");
        if (accept) invites.Accept(id);
        else invites.Decline(id);
        Play(PhoneSounds.Tap);
    }

    /// <summary>A call came in: the phone buzzes in the pocket (the toast chimes on screen).</summary>
    private void OnCall(InviteView call) => Play(PhoneSounds.Buzz);

    // ------------------------------------------------------------------ travel

    /// <summary>The Map app's pick: a teleport, run behind the screen.</summary>
    private void Travel(double e, double n, string label)
    {
        if (!Permissions.CanTeleport)
        {
            InviteToasts.Instance?.Flash("This server does not allow travelling.");
            return;
        }
        Teleporter.Instance?.TeleportTo(e, n, label);
    }

    /// <summary>
    /// <see cref="Teleporter.Curtain"/>: with the phone out, a jump waits for the screen to cover the
    /// view (and runs at once if it already does). In the pocket, the jump goes as it always did.
    /// </summary>
    private bool Curtain(string where, Action jump)
    {
        if (_stage == Stage.Pocket || _player() is null) return false;
        GD.Print($"[phone] travel behind the screen to {where}");
        _pendingJump = jump;
        _travelSince = RealClock.Now;
        _landedAt = -1;
        _arrived = false;
        // opened by the jump itself (a call answered from the cover): it goes away again once there
        if (_stage is Stage.Drawing or Stage.Folded or Stage.Closing or Stage.Stowing) _stowWhenFolded = true;
        if (_stage == Stage.Open) _stowWhenFolded = true;
        _screens.ShowTravel(where);
        if (_stage == Stage.Open) RunPendingJump();
        else
        {
            if (_stage == Stage.Stowing) _stage = Stage.Folded;
            _stage = Stage.Opening;
            Play(PhoneSounds.Hinge);
        }
        return true;
    }

    private void RunPendingJump()
    {
        var jump = _pendingJump;
        _pendingJump = null;
        jump?.Invoke();
    }

    private void OnArrived()
    {
        if (_travelSince < 0) return;
        _arrived = true;
    }

    /// <summary>On "Travelling…": once landed and settled there, the phone folds away and the camera shows the new place.</summary>
    private void StepTravel()
    {
        if (_travelSince < 0 || _pendingJump != null) return;
        var p = _player();
        if (_arrived && _landedAt < 0 && p != null && p.IsOnFloor()) _landedAt = RealClock.Now;
        bool settled = _landedAt >= 0 && RealClock.Now - _landedAt > SettleSeconds;
        if (!settled && RealClock.Now - _travelSince < TravelTimeout) return;
        GD.Print($"[phone] arrived ({RealClock.Now - _travelSince:F1} s)");
        _travelSince = -1;
        _screens.ShowPage(PhoneScreens.Page.Home);
        if (_stowWhenFolded) PutAway();
        else Fold();
    }

    // ------------------------------------------------------------------ input

    public override void _Input(InputEvent e)
    {
        if (_stage == Stage.Pocket)
        {
            if (e.IsActionPressed(PlayerInput.Phone) && !e.IsEcho() && !UiFocus.TextEntryActive && !_blocked())
            {
                if (TakeOut()) GetViewport().SetInputAsHandled();
                // Up in a cab is the excavator's boom: only a body on foot hears why not
                else if (_player() is { Ride: RideKind.OnFoot } && !CanUse(out var why) && why != "Not now") InviteToasts.Instance?.Flash($"Phone: {why}");
            }
            return;
        }
        if (_blocked()) return;   // a page over the phone (Activities) takes its own keys

        if (_stage is Stage.Open)
        {
            if (e.IsActionPressed("ui_cancel") && !e.IsEcho()) Back();
            else Forward(e);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_stage is Stage.Opening or Stage.Closing or Stage.Stowing)
        {
            // nothing reaches the body while the phone moves; a mouse move would turn the view under it
            if (e is not InputEventMouseMotion) GetViewport().SetInputAsHandled();
            return;
        }

        // folded (or coming out): the cover's quick actions
        bool handled = true;
        if (!e.IsPressed() || e.IsEcho()) handled = e is InputEventKey or InputEventJoypadButton;
        else if (e.IsActionPressed(PlayerInput.Phone) || e.IsActionPressed("ui_up")) Open();
        else if (e.IsActionPressed("ui_down") || e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu)) PutAway();
        else if (e.IsActionPressed("ui_left") || e is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp }) Pick(-1);
        else if (e.IsActionPressed("ui_right") || e is InputEventMouseButton { ButtonIndex: MouseButton.WheelDown }) Pick(1);
        else if (e.IsActionPressed("ui_accept") || e is InputEventKey { PhysicalKeycode: Key.Space }
                 || e is InputEventMouseButton { ButtonIndex: MouseButton.Left }) RunQuick();
        else handled = false;
        if (handled) GetViewport().SetInputAsHandled();
    }

    private void Pick(int step)
    {
        if (_quicks.Count == 0) return;
        _quick = (_quick + step + _quicks.Count) % _quicks.Count;
        Play(PhoneSounds.Tap);
    }

    private void RunQuick()
    {
        if (_quick < 0 || _quick >= _quickActions.Count) return;
        GD.Print($"[phone] quick {_quicks[_quick].Label}");
        Play(PhoneSounds.Tap);
        _quickActions[_quick]();
    }

    /// <summary>The full-screen picture's input, into the inner screen: a mouse event mapped to its pixels.</summary>
    private void Forward(InputEvent e)
    {
        if (e is InputEventMouse mouse)
        {
            var copy = (InputEventMouse)mouse.Duplicate();
            copy.Position = ToScreen(mouse.Position);
            copy.GlobalPosition = copy.Position;
            _screens.Inner.PushInput(copy, true);
            return;
        }
        _screens.Inner.PushInput(e, true);
    }

    /// <summary>A window point into the inner screen's pixels, through the "cover" fit of <see cref="_full"/>.</summary>
    private Vector2 ToScreen(Vector2 window)
    {
        var size = _full.Size;
        float n = _screens.Inner.Size.X;
        float scale = Mathf.Max(size.X / n, size.Y / n);
        var offset = (size - new Vector2(n, n) * scale) * 0.5f;
        return (window - _full.GlobalPosition - offset) / scale;
    }

    // ------------------------------------------------------------------ bits

    private void Play(AudioStreamWav stream)
    {
        _sound.Stream = stream;
        _sound.PitchScale = 1f + ((float)_rng.NextDouble() - 0.5f) * 0.04f;
        _sound.Play();
    }

    private static string ClockText()
    {
        double h = World.WorldClock.CurrentHour;
        int minutes = (int)(h * 60) % (24 * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    private static string DateText() => DateTime.Now.ToString("dddd d MMMM", System.Globalization.CultureInfo.InvariantCulture);
}
