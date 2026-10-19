using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Phone;

/// <summary>
/// The local player's phone (#889): taken out, used folded from its cover screen while walking,
/// opened in front of the face, put away. It drives <see cref="FootPlayer.PhoneState"/> (replicated)
/// and the owner's phone camera (<see cref="FootPlayer.PhoneLook"/>, <see cref="FootPlayer.PhoneCover"/>,
/// <see cref="FootPlayer.PhoneBackdrop"/>). Open, the phone stays a thing in the hands, its rim in
/// view and the world dimmed behind it; its screen takes the keys and the mouse (mapped onto the 3D
/// screen). A teleport asked while the phone is out waits until the world is hidden
/// (<see cref="Teleporter.Curtain"/>), then the phone stays on "Travelling…" until the ground there
/// has streamed in, and folds away at the new place. Docs: <c>docs/notes/ui/phone.md</c>.
/// <list type="bullet">
/// <item>Keyboard: Up takes it out, Up again opens it; folded, Left / Right pick a quick action,
/// Enter / click runs it, Down or Esc puts it away (open: Esc goes back, then closes). Folded, the
/// body still walks.</item>
/// <item>Pad: hold Back → Phone in the quick menu; D-pad and A / B inside, D-pad up opens it.</item>
/// <item>Camera app: the phone is raised before the face; the mouse aims, click / Enter / RT
/// shoots, the wheel / LB RB zoom, Esc goes back.</item>
/// <item>VR: not yet (a gap in <c>xr/vr-action-map</c>): no camera moves in VR.</item>
/// </list>
/// </summary>
public partial class PhoneController : Node
{
    private enum Stage { Pocket, Drawing, Folded, Opening, Open, Closing, Stowing }

    /// <summary>Seconds for the camera to come over the shoulder, and onto the open phone.</summary>
    private const float LookSeconds = 0.5f, CoverSeconds = 0.6f;
    /// <summary>After landing at a travel's end: this long on "Travelling…" so the place is built before it is seen.</summary>
    private const double SettleSeconds = 0.9;
    private const double TravelTimeout = 20.0;

    public static PhoneController? Instance { get; private set; }

    private readonly Func<FootPlayer?> _player;
    private readonly Func<bool> _blocked;
    private PhoneScreens _screens = null!;
    private Label _hint = null!;
    private AudioStreamPlayer _sound = null!;
    private readonly Random _rng = new();

    private Stage _stage = Stage.Pocket;
    private float _look, _cover, _fold;
    private int _quick;
    private readonly List<PhoneScreens.Quick> _quicks = new();
    private readonly List<Action> _quickActions = new();
    /// <summary>A teleport held back until the world is hidden.</summary>
    private Action? _pendingJump;
    /// <summary>Travelling: since when (real clock), and when the body landed there (−1: not yet).</summary>
    private double _travelSince = -1, _landedAt = -1;
    private bool _arrived;
    /// <summary>Folds and goes back in the pocket once closed (put away while open, or opened by a teleport).</summary>
    private bool _stowWhenFolded;
    private bool _shooting;
    private string _hintShown = "";
    private InviteService? _invites;

    /// <summary>The id of the last photo the camera app took, for the checks.</summary>
    public string? LastPhoto { get; private set; }

    /// <summary>The two screens, for the checks.</summary>
    internal PhoneScreens Screens => _screens;

    public bool IsOut => _stage != Stage.Pocket;
    /// <summary>Open and in front of the face, the world hidden behind it (or the camera app up).</summary>
    public bool IsCovering => _stage == Stage.Open;
    public bool CameraApp => _stage is Stage.Open or Stage.Opening && _screens.Shown == PhoneScreens.Page.Camera;

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
        _screens.ShutterPressed += () => _ = TakePhoto();
        _screens.PageShown += _ => Grip();

        var hud = new CanvasLayer { Name = "PhoneHints", Layer = 5 };
        AddChild(hud);
        _hint = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center, Theme = UiTheme.Get() };
        _hint.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.7f));
        _hint.AddThemeConstantOverride("shadow_offset_y", 2);
        _hint.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _hint.OffsetTop = -64; _hint.OffsetBottom = -36;
        hud.AddChild(_hint);
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
        Play(PhoneSounds.Draw);
        return true;
    }

    /// <summary>Opens the phone onto <paramref name="page"/>, taking it out first if it is in the pocket.</summary>
    public void Open(PhoneScreens.Page page = PhoneScreens.Page.Home)
    {
        if (_stage == Stage.Pocket && !TakeOut()) return;
        if (_stage is Stage.Open or Stage.Opening) { _screens.ShowPage(page); return; }
        GD.Print($"[phone] open {page}");
        _stage = Stage.Opening;
        _screens.ShowPage(page);
        Grip();
        Play(PhoneSounds.Hinge);
    }

    /// <summary>Back from an app to home; from home, folds the phone.</summary>
    private void Back()
    {
        if (_screens.Shown is PhoneScreens.Page.Map or PhoneScreens.Page.Contacts or PhoneScreens.Page.Camera)
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
        if (_screens.Shown == PhoneScreens.Page.Camera) _screens.ShowPage(PhoneScreens.Page.Home);
        Grip();
        Play(PhoneSounds.Hinge);
    }

    /// <summary>Puts the phone away, folding it first if it is open.</summary>
    public void PutAway()
    {
        if (_stage == Stage.Pocket || _stage == Stage.Stowing) return;
        GD.Print("[phone] away");
        if (_stage is Stage.Open or Stage.Opening or Stage.Closing)
        {
            _stowWhenFolded = true;
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
        _screens.SetActive(false);
        _hint.Visible = false;
        Grip();
    }

    /// <summary>
    /// Who has the keys and the mouse: open on an app, the phone (the body stands, the cursor shows);
    /// folded or in the camera app, the body (it walks; the mouse looks).
    /// </summary>
    private void Grip()
    {
        bool open = _stage is Stage.Open or Stage.Opening;
        bool screen = open && _screens.Shown != PhoneScreens.Page.Camera;
        UiFocus.Set(this, screen);
        if (_blocked()) return;
        if (screen) Input.MouseMode = Input.MouseModeEnum.Visible;
        else MouseCapture.Capture();
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
        // the lens leaves for the open phone once the hinge is a third open, and is off it before it shuts
        float coverWant = coverTo > 0f ? (_fold > 0.3f ? 1f : _cover) : 0f;
        _cover = Mathf.MoveToward(_cover, coverWant, dt / CoverSeconds);

        switch (_stage)
        {
            case Stage.Drawing when _look >= 1f:
                _stage = Stage.Folded;
                break;
            case Stage.Opening when _cover >= 1f && _fold >= 1f:
                _stage = Stage.Open;
                break;
            case Stage.Closing when _cover <= 0f && _fold <= 0.05f:
                _stage = Stage.Folded;
                Grip();
                if (_stowWhenFolded) { _stowWhenFolded = false; _stage = Stage.Stowing; }
                break;
            case Stage.Stowing when _look <= 0f:
                Release(p);
                Drop();
                GD.Print("[phone] in the pocket");
                return;
        }

        bool open = _stage is Stage.Opening or Stage.Open;
        p.PhoneState = !open ? FootPlayer.PhoneFoldedState
            : _screens.Shown == PhoneScreens.Page.Camera ? FootPlayer.PhoneCameraState : FootPlayer.PhoneOpenState;
        p.PhoneLook = _look;
        p.PhoneCover = _cover;
        p.PhoneFold = _fold;
        // the world goes dark behind the open phone, but not behind its camera: you aim at it
        p.PhoneBackdrop = open && _screens.Shown != PhoneScreens.Page.Camera ? 1f : 0f;
        if (!open || _screens.Shown == PhoneScreens.Page.Camera) p.PhoneTilt = Vector2.Zero;

        _screens.SetActive(true);
        // the window's pixels: the viewport's visible rect is in the stretched canvas's units
        _screens.Fit(GetWindow().Size);
        if (p.PhoneModel is { HasLiveScreens: false } model) model.SetScreens(_screens.Cover.GetTexture(), _screens.Inner.GetTexture());
        if (_screens.Shown == PhoneScreens.Page.Camera) AimLens(p);
        _screens.Tick(ClockText(), DateText(), dt);
        RefreshQuick();
        _screens.ShowCover(_quicks, _quick);
        StepTravel();
        ShowHint();
    }

    private static void Release(FootPlayer p)
    {
        p.PhoneState = FootPlayer.PhonePocketState;
        p.PhoneLook = p.PhoneCover = p.PhoneFold = p.PhoneBackdrop = 0f;
        p.PhoneTilt = Vector2.Zero;
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

    // ------------------------------------------------------------------ the camera app

    /// <summary>The back lens where the phone's is, looking out of its back, at the app's zoom.</summary>
    private void AimLens(FootPlayer p)
    {
        if (p.PhoneModel is not { Visible: true } phone || p.CameraOrNull is not { } eye) return;
        var lens = _screens.Lens;
        lens.GlobalTransform = phone.GlobalTransform.Orthonormalized() * new Transform3D(Basis.Identity, PhoneModel.LensPoint);
        lens.Fov = _screens.LensFovNow;
        // what the eye sees, less the HUD-ish layers and the dark sheet behind the phone
        lens.CullMask = eye.CullMask & ~Items.HeldItemVisual.ViewmodelLayer;
        if (lens.Environment != eye.Environment) lens.Environment = eye.Environment;
        if (lens.Attributes != eye.Attributes) lens.Attributes = eye.Attributes;
    }

    /// <summary>The shutter: one frame through the back lens, into the photo album (as the Polaroid's shots go).</summary>
    private async Task TakePhoto()
    {
        if (_shooting || _player() is not { } p || _screens.Shown != PhoneScreens.Page.Camera) return;
        _shooting = true;
        try
        {
            Play(PhoneSounds.Shutter);
            float fov = _screens.LensFovNow;
            var image = await Items.PhotoCapture.Render(this, _screens.Lens, fov);
            // beside the album's prints (--photo-dir moves both, for the checks)
            string dir = System.IO.Path.GetDirectoryName(Items.PhotoStore.LocalDir)!;
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, $"phone_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            image.SavePng(path);
            var at = p.Global;
            float focal = 12f / Mathf.Tan(Mathf.DegToRad(fov) * 0.5f);
            string id = Items.PhotoStore.Save(image, at.E, at.N, at.Alt, focal, path);
            GD.Print($"[phone] photo {id} ({_screens.Zoom:0.#}×, {path})");
            LastPhoto = id;
            _screens.Shot(Items.PhotoStore.Texture(id));
            InviteToasts.Instance?.Flash("Photo saved to your album");
        }
        catch (Exception e)
        {
            GD.PushWarning($"[phone] the photo failed: {e.Message}");
        }
        finally
        {
            _shooting = false;
        }
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
        Add(new("Camera"), () => Open(PhoneScreens.Page.Camera));
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

    /// <summary>The Map app's pick: a teleport, run behind the phone.</summary>
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
    /// <see cref="Teleporter.Curtain"/>: with the phone out, a jump waits for the phone to be open
    /// in front of the face with the world dimmed behind it (and runs at once if it already is). In
    /// the pocket, the jump goes as it always did.
    /// </summary>
    private bool Curtain(string where, Action jump)
    {
        if (_stage == Stage.Pocket || _player() is null) return false;
        GD.Print($"[phone] travel behind the phone to {where}");
        _pendingJump = jump;
        _travelSince = RealClock.Now;
        _landedAt = -1;
        _arrived = false;
        // it goes away again once there, as a call answered from the cover does
        _stowWhenFolded = true;
        _screens.ShowTravel(where);
        Grip();
        // the jump itself runs from StepTravel, once the world is dark behind the open phone
        if (_stage != Stage.Open)
        {
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
        // open on Travel, the jump runs once the backdrop has darkened the world all the way
        if (_pendingJump != null && _stage == Stage.Open && _player() is { PhoneBackdropShown: >= 1f }) RunPendingJump();
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

        if (_stage is Stage.Open && _screens.Shown == PhoneScreens.Page.Camera)
        {
            CameraInput(e);
            return;
        }
        if (_stage is Stage.Open)
        {
            if (e.IsActionPressed("ui_cancel") && !e.IsEcho()) Back();
            else Forward(e);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_stage is Stage.Opening or Stage.Closing or Stage.Stowing)
        {
            // nothing reaches the body while the phone moves but the look
            if (e is not InputEventMouseMotion) GetViewport().SetInputAsHandled();
            return;
        }

        // folded (or coming out): the cover's quick actions; the body walks and looks on
        if (!e.IsPressed() || e.IsEcho()) return;
        bool handled = true;
        if (e.IsActionPressed(PlayerInput.Phone) || e is InputEventJoypadButton && e.IsActionPressed("ui_up")) Open();
        else if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu)
                 || e is InputEventKey { PhysicalKeycode: Key.Down } || e is InputEventJoypadButton && e.IsActionPressed("ui_down")) PutAway();
        else if (e is InputEventKey { PhysicalKeycode: Key.Left } || e is InputEventJoypadButton && e.IsActionPressed("ui_left")
                 || e is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp }) Pick(-1);
        else if (e is InputEventKey { PhysicalKeycode: Key.Right } || e is InputEventJoypadButton && e.IsActionPressed("ui_right")
                 || e is InputEventMouseButton { ButtonIndex: MouseButton.WheelDown }) Pick(1);
        else if (e is InputEventKey { PhysicalKeycode: Key.Enter or Key.KpEnter } || e is InputEventJoypadButton && e.IsActionPressed("ui_accept")
                 || e is InputEventMouseButton { ButtonIndex: MouseButton.Left }) RunQuick();
        else handled = false;
        if (handled) GetViewport().SetInputAsHandled();
    }

    /// <summary>The camera app's keys; the mouse's moves go on to the body, which looks with them.</summary>
    private void CameraInput(InputEvent e)
    {
        if (e is InputEventMouseMotion) return;
        if (!e.IsPressed() || e.IsEcho())
        {
            if (e is InputEventMouseButton) GetViewport().SetInputAsHandled();
            return;
        }
        bool handled = true;
        if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu)) Back();
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } || e is InputEventKey { PhysicalKeycode: Key.Enter or Key.KpEnter }
                 || e.IsActionPressed(PlayerInput.TriggerRight) || e is InputEventJoypadButton && e.IsActionPressed("ui_accept")) _ = TakePhoto();
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp } || e is InputEventJoypadButton { ButtonIndex: JoyButton.RightShoulder }) Zoom(1);
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.WheelDown } || e is InputEventJoypadButton { ButtonIndex: JoyButton.LeftShoulder }) Zoom(-1);
        else handled = false;
        if (handled) GetViewport().SetInputAsHandled();
    }

    private void Zoom(int step)
    {
        _screens.StepZoom(step);
        Play(PhoneSounds.Tap);
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

    /// <summary>
    /// Into the inner screen: keys and pad as they are, the mouse where its ray meets the 3D screen
    /// (off the screen, a click does nothing). The phone leans a little toward the cursor.
    /// </summary>
    private void Forward(InputEvent e)
    {
        if (e is InputEventMouse mouse)
        {
            var p = _player();
            var size = GetViewport().GetVisibleRect().Size;
            if (p != null && size.X > 0) p.PhoneTilt = (mouse.Position / size - new Vector2(0.5f, 0.5f)) * 2f;
            if (p?.PhoneScreenPoint(mouse.Position) is not { } uv) return;
            var copy = (InputEventMouse)mouse.Duplicate();
            copy.Position = uv * (Vector2)_screens.Inner.Size;
            copy.GlobalPosition = copy.Position;
            _screens.Inner.PushInput(copy, true);
            return;
        }
        _screens.Inner.PushInput(e, true);
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
