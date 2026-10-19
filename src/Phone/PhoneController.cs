using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Phone;

/// <summary>
/// The local player's phone (#889): taken out, used folded from its cover screen, opened, put away.
/// While it is out the body stands still and every key and the mouse work the phone; the figure's
/// free hand points its index finger where the cursor is on the screen, and swipes across it on
/// WASD. It drives <see cref="FootPlayer.PhoneState"/> (replicated) and the owner's phone camera:
/// over the shoulder (<see cref="FootPlayer.PhoneLook"/>), behind the raised phone, level, for the
/// camera app (<see cref="FootPlayer.PhoneCameraApp"/>), and square onto it for the map
/// (<see cref="FootPlayer.PhoneCover"/>): there the lens closes in first, then the world fades to
/// black behind the phone (<see cref="FootPlayer.PhoneBackdrop"/>). A teleport asked while the phone
/// is out waits for that black (<see cref="Teleporter.Curtain"/>), then the phone stays on
/// "Travelling…" until the ground there has streamed in, and folds away at the new place.
/// Docs: <c>docs/notes/ui/phone.md</c>.
/// <list type="bullet">
/// <item>Keyboard: Up takes it out. Folded: W or Up opens it, A D or Left Right pick a quick action,
/// Enter / Space / E runs it, S or Down or Esc puts it away. Open: WASD and the arrows move on the
/// screen, Enter / Space / E presses, Esc goes back, then folds.</item>
/// <item>Mouse: the cursor shows; click presses, a drag swipes (up and down scroll a list, a swipe to
/// the right goes back; on the cover, left and right pick).</item>
/// <item>Pad: hold Back → Phone in the quick menu; D-pad and A / B inside, D-pad up opens it.</item>
/// <item>Camera app: the phone is raised and level before the face; the mouse aims, click / Enter / RT
/// shoots, the wheel / LB RB zoom, Esc goes back.</item>
/// <item>VR: not yet (a gap in <c>xr/vr-action-map</c>): no camera moves in VR.</item>
/// </list>
/// </summary>
public partial class PhoneController : Node
{
    private enum Stage { Pocket, Drawing, Folded, Opening, Open, Closing, Stowing }

    /// <summary>Seconds for the camera to come over the shoulder, onto the map, behind the raised phone.</summary>
    private const float LookSeconds = 0.5f, CoverSeconds = 0.55f, CameraAppSeconds = 0.5f;
    /// <summary>After landing at a travel's end: this long on "Travelling…" so the place is built before it is seen.</summary>
    private const double SettleSeconds = 0.9;
    private const double TravelTimeout = 20.0;
    /// <summary>The fingertip above the glass while it hovers, and on it while it touches (metres).</summary>
    private const float Hover = 0.016f, Touching = 0.002f;
    /// <summary>The main camera's field of view for a selfie, up the tall cover screen.</summary>
    private const float SelfieFov = 80f;
    /// <summary>A mouse drag longer than this (window pixels) is a swipe, not a click.</summary>
    private const float SwipePixels = 14f;

    public static PhoneController? Instance { get; private set; }

    private readonly Func<FootPlayer?> _player;
    private readonly Func<bool> _blocked;
    private PhoneScreens _screens = null!;
    private Label _hint = null!;
    private AudioStreamPlayer _sound = null!;
    private readonly Random _rng = new();

    private Stage _stage = Stage.Pocket;
    private float _look, _cover, _fold, _camApp;
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

    // the finger: where the cursor points on the screen in view, or (led by the keys) over what is selected
    private Vector2? _cursorUv;
    private bool _cursorOnCover, _keysLead;
    private float _touch;
    private double _tapUntil;
    /// <summary>The camera app turned round: the main camera films you, the cover shows you.</summary>
    private bool _selfie;
    private PhoneWallpaper.Choice _walls = new();
    // a mouse press waiting to be a click or a swipe
    private bool _pressed, _dragging;
    private Vector2 _pressAt, _lastAt;
    private Vector2? _pressUv;

    /// <summary>The id of the last photo the camera app took, for the checks.</summary>
    public string? LastPhoto { get; private set; }

    /// <summary>Where the cursor is on the screen in view (0..1, unclamped), for the checks; null off it.</summary>
    internal Vector2? CursorUv => _cursorUv;

    /// <summary>The two screens, for the checks.</summary>
    internal PhoneScreens Screens => _screens;

    public bool IsOut => _stage != Stage.Pocket;
    /// <summary>Open and settled (on whatever page: square on the map with the world black behind it, or in the hands).</summary>
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
            if (cmd.StartsWith("/tp", StringComparison.Ordinal) || cmd.StartsWith("/party", StringComparison.Ordinal) || cmd.StartsWith("/fight", StringComparison.Ordinal))
                InviteToasts.Instance?.Flash("Calling…");
        };
        _screens.CallAnswered += Answer;
        _screens.ActivitiesPicked += () => ActivitiesScreen.Open(ActivitiesScreen.Tab.BattleRoyale);
        _screens.ShutterPressed += () => _ = TakePhoto();
        _screens.SelfiePressed += () => SetSelfie(!_selfie);
        _screens.WallpaperPicked += SetWallpaper;
        _screens.PageShown += page =>
        {
            if (page != PhoneScreens.Page.Camera && _selfie) SetSelfie(false);
            Grip();
        };
        _walls = PhoneWallpaper.Load();
        Callable.From(ApplyWallpapers).CallDeferred();

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
        Grip();
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
        if (_screens.Shown == PhoneScreens.Page.Photo)
        {
            _screens.ShowPage(PhoneScreens.Page.Photos);
            Play(PhoneSounds.Tap);
            return;
        }
        if (_selfie)
        {
            SetSelfie(false);
            return;
        }
        if (_screens.Shown is PhoneScreens.Page.Map or PhoneScreens.Page.Contacts or PhoneScreens.Page.Camera or PhoneScreens.Page.Activities
            or PhoneScreens.Page.Photos)
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
        _look = _cover = _fold = _camApp = 0f;
        _pendingJump?.Invoke();
        _pendingJump = null;
        _travelSince = -1;
        _stowWhenFolded = false;
        _cursorUv = null;
        _pressed = _dragging = false;
        if (_selfie) SetSelfie(false);
        _screens.SetActive(false);
        _hint.Visible = false;
        Grip();
    }

    /// <summary>
    /// Who has the keys and the mouse: with the phone out, the phone (the body stands, the cursor
    /// shows); in the camera app, the mouse looks (the phone films where the view points).
    /// </summary>
    private void Grip()
    {
        // VR (#920): the body walks on while you read, as with a real phone; the fingertip is the pointer
        if (XR.XrSession.Active)
        {
            UiFocus.Set(this, false);
            return;
        }
        bool phone = _stage != Stage.Pocket && !(_stage is Stage.Open or Stage.Opening && _screens.Shown == PhoneScreens.Page.Camera);
        UiFocus.Set(this, _stage != Stage.Pocket);
        if (_blocked()) return;
        if (phone) Input.MouseMode = Input.MouseModeEnum.Visible;
        else MouseCapture.Capture();
    }

    private bool CanUse(out string? why)
    {
        why = null;
        var p = _player();
        if (p == null || _blocked()) { why = "Not now"; return false; }
        if (p is not { Ride: RideKind.OnFoot, RidingAlong: false, IsSwimming: false, Ragdolled: false, Downed: false, Fighting: false })
        {
            why = "On foot only";
            return false;
        }
        return true;
    }

    /// <summary>The map and the travelling page are seen square on, close, the world black behind; the rest in the hands.</summary>
    private bool FullView => _screens.Shown is PhoneScreens.Page.Map or PhoneScreens.Page.Travel;

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
        bool open = _stage is Stage.Opening or Stage.Open;
        float lookTo = _stage == Stage.Stowing ? 0f : inHands ? 1f : _look;
        _look = Mathf.MoveToward(_look, lookTo, dt / LookSeconds);
        _fold = Mathf.MoveToward(_fold, open ? 1f : 0f, dt / PhoneModel.UnfoldSeconds);
        // the map: the lens closes in once the hinge is open, the world fades out only once it is there
        float coverWant = open && FullView && _fold >= 1f ? 1f : 0f;
        _cover = Mathf.MoveToward(_cover, coverWant, dt / CoverSeconds);
        _camApp = Mathf.MoveToward(_camApp, open && _screens.Shown == PhoneScreens.Page.Camera && _fold >= 1f ? 1f : 0f, dt / CameraAppSeconds);

        switch (_stage)
        {
            case Stage.Drawing when _look >= 1f:
                _stage = Stage.Folded;
                break;
            case Stage.Opening when _fold >= 1f && _cover == coverWant:
                _stage = Stage.Open;
                break;
            case Stage.Closing when _cover <= 0f && _camApp <= 0f && _fold <= 0.05f:
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

        p.PhoneState = !open ? FootPlayer.PhoneFoldedState
            : _screens.Shown == PhoneScreens.Page.Camera ? _selfie ? FootPlayer.PhoneSelfieState : FootPlayer.PhoneCameraState
            : FootPlayer.PhoneOpenState;
        p.PhoneLook = _look;
        p.PhoneCover = _cover;
        p.PhoneFold = _fold;
        p.PhoneCameraApp = _camApp;
        // black behind the phone only once the lens is all the way in: first closer, then the fade
        p.PhoneBackdrop = _cover >= 1f ? 1f : 0f;
        p.PhoneTilt = open && !FullView && _screens.Shown != PhoneScreens.Page.Camera && _cursorUv is { } c
            ? new Vector2(Mathf.Clamp(c.X * 2f - 1f, -1f, 1f), Mathf.Clamp(c.Y * 2f - 1f, -1f, 1f)) * 0.6f : Vector2.Zero;
        StepFinger(p, dt);

        _screens.SetActive(true);
        _screens.Fit(GetWindow().Size);
        if (p.PhoneModel is { HasLiveScreens: false } model) model.SetScreens(_screens.Cover.GetTexture(), _screens.Inner.GetTexture());
        if (_screens.Shown == PhoneScreens.Page.Camera) AimLens(p, dt);
        _screens.Tick(ClockText(), DateText(), dt);
        RefreshQuick();
        _screens.ShowCover(_quicks, _quick);
        StepTravel();
        ShowHint();
    }

    private static void Release(FootPlayer p)
    {
        p.PhoneState = FootPlayer.PhonePocketState;
        p.PhoneLook = p.PhoneCover = p.PhoneFold = p.PhoneBackdrop = p.PhoneCameraApp = 0f;
        p.PhoneTilt = Vector2.Zero;
        p.PhonePointTip = null;
        p.PhonePointDir = null;
    }

    private void ShowHint()
    {
        string text = _stage is Stage.Folded or Stage.Drawing
            ? XR.XrSession.Active ? "X  open     swipe  choose     tap  select     B  put away"
            : InputHints.Pad ? "D-pad ↑ open     ← →  choose     A  select     B  put away"
                : "W  open     A D  choose     Enter  select     S  put away     ·     mouse: click, swipe"
            : "";
        _hint.Visible = text.Length > 0 && !_blocked();
        if (text != _hintShown) _hint.Text = _hintShown = text;
    }

    // ------------------------------------------------------------------ the finger

    /// <summary>
    /// The free hand's index fingertip: over the cursor's point on the screen (and a little round it),
    /// down onto the glass while the mouse presses or a WASD swipe runs; off the phone when there is
    /// nothing to point at, the hand back on it. Only in the hands: square on the map or raised as a
    /// camera, the hand is out of the picture.
    /// </summary>
    private void StepFinger(FootPlayer p, float dt)
    {
        // VR: the real hand points, not the figure's (#920)
        if (XR.XrSession.Active)
        {
            p.PhonePointTip = p.PhonePointDir = null;
            return;
        }
        bool hands = _stage is Stage.Folded or Stage.Open && _cover <= 0f && _camApp <= 0f;
        Vector2? uv = null;
        bool cover = _cursorOnCover;
        if (_keysLead)
        {
            // led by the keys: over what is selected (the cover's quick action; the inner screen's focus)
            cover = _stage == Stage.Folded;
            uv = cover ? _screens.CoverCardUv() : _screens.FocusUv();
        }
        else if (_cursorUv is { } c && c.X is > -0.2f and < 1.2f && c.Y is > -0.2f and < 1.2f) uv = c;
        bool down = _pressed || RealClock.Now < _tapUntil;
        _touch = Mathf.MoveToward(_touch, down ? 1f : 0f, dt / 0.08f);
        p.PhonePointTip = hands && uv is { } at ? p.PhoneScreenToWorld(at, cover, Mathf.Lerp(Hover, Touching, _touch)) : null;
        p.PhonePointDir = p.PhonePointTip != null ? p.PhoneFingerWay() : null;
    }

    /// <summary>A key pressed what is selected: the finger taps it.</summary>
    private void Tap() => _tapUntil = RealClock.Now + 0.16;

    // ------------------------------------------------------------------ photos and wallpapers

    private void SetWallpaper(string id, bool lockScreen)
    {
        _walls = lockScreen ? _walls with { Lock = id } : _walls with { Home = id };
        PhoneWallpaper.Save(_walls);
        ApplyWallpapers();
        Play(PhoneSounds.Tap);
        InviteToasts.Instance?.Flash(lockScreen ? "Set as the lock screen" : "Set as the home screen");
        GD.Print($"[phone] wallpaper {(lockScreen ? "lock" : "home")} = {id}");
    }

    private void ApplyWallpapers() => _screens.SetWallpapers(
        _walls.Lock is { } l ? PhoneWallpaper.FullOf(l) : null,
        _walls.Home is { } h ? PhoneWallpaper.FullOf(h) : null);

    /// <summary>The wallpapers chosen now, for the checks.</summary>
    internal PhoneWallpaper.Choice Wallpapers => _walls;

    // ------------------------------------------------------------------ the camera app

    /// <summary>
    /// The back lens where the phone's is, looking out of its back, its zoom gliding to the step picked.
    /// Turned for a selfie it films its owner, whose body the eye's camera leaves out (drawn from the eye).
    /// </summary>
    private void AimLens(FootPlayer p, float dt)
    {
        if (p.PhoneModel is not { Visible: true } phone || p.CameraOrNull is not { } eye) return;
        var lens = _screens.Lens;
        lens.GlobalTransform = phone.GlobalTransform.Orthonormalized() * new Transform3D(Basis.Identity, PhoneModel.LensPoint);
        // a selfie frames the face, as a phone's face tracking does: the lens turned onto the head, the phone's up kept
        if (_selfie)
        {
            var face = p.GlobalPosition + Vector3.Up * 1.58f;
            var at = lens.GlobalPosition;
            if (at.DistanceSquaredTo(face) > 0.01f) lens.GlobalTransform = new Transform3D(Basis.LookingAt(face - at, phone.GlobalBasis.Y), at);
        }
        // a zoom eases in, as a phone's does between its lenses
        // a selfie wide, at arm's length the face and what is behind it; a zoom eases in, as a phone's does between its lenses
        lens.Fov = Mathf.Lerp(lens.Fov, _selfie ? SelfieFov : _screens.LensFovNow, MathX.Damp(7f, dt));
        // what the eye sees, less the viewmodel layer; a selfie also sees the body the eye leaves out
        lens.CullMask = (eye.CullMask | (_selfie ? Avatar.OwnBody.Layer : 0u)) & ~Items.HeldItemVisual.ViewmodelLayer;
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
            float fov = _screens.Lens.Fov;
            var image = await Items.PhotoCapture.Render(this, _screens.Lens, fov);
            // what the screen showed, no more: the lens's frame cropped to the viewfinder's shape (the tall
            // cover for a selfie, the inner screen otherwise), the same height of view
            var finder = _screens.Finder.Size;
            image = CropTo(image, (float)finder.X / finder.Y);
            // beside the album's prints (--photo-dir moves both, for the checks)
            string dir = System.IO.Path.GetDirectoryName(Items.PhotoStore.LocalDir)!;
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, $"phone_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            image.SavePng(path);
            var at = p.Global;
            float focal = 12f / Mathf.Tan(Mathf.DegToRad(fov) * 0.5f);
            string id = Items.PhotoStore.Save(image, at.E, at.N, at.Alt, focal, path);
            GD.Print($"[phone] {(_selfie ? "selfie" : "photo")} {id} ({_screens.Zoom:0.#}×, {path})");
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

    /// <summary>The middle of <paramref name="image"/> at <paramref name="aspect"/> (width over height), as a screen covered by it shows.</summary>
    internal static Image CropTo(Image image, float aspect)
    {
        int w = image.GetWidth(), h = image.GetHeight();
        if (w <= 0 || h <= 0 || aspect <= 0f) return image;
        var region = (float)w / h > aspect
            ? new Rect2I((w - Mathf.RoundToInt(h * aspect)) / 2, 0, Mathf.RoundToInt(h * aspect), h)
            : new Rect2I(0, (h - Mathf.RoundToInt(w / aspect)) / 2, w, Mathf.RoundToInt(w / aspect));
        return region.Size == new Vector2I(w, h) ? image : image.GetRegion(region);
    }

    /// <summary>
    /// A selfie, the foldable's way: the open phone turned round so its main camera and its cover screen
    /// face you; the cover shows the main camera's view (the lens resized to the cover's shape).
    /// </summary>
    private void SetSelfie(bool on)
    {
        if (on && _screens.Shown != PhoneScreens.Page.Camera) return;
        _selfie = on;
        _screens.SetSelfie(on);
        var size = on ? new Vector2I(496, 734) : (Vector2I)((Vector2)_screens.Inner.Size * 0.6f);
        if (_screens.Finder.Size != size) _screens.Finder.Size = size;
        Play(PhoneSounds.Hinge);
        GD.Print($"[phone] selfie {(on ? "on" : "off")}");
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
        Add(new("Photos"), () => Open(PhoneScreens.Page.Photos));
        Add(new("Map"), () => Open(PhoneScreens.Page.Map));
        Add(new("Contacts"), () => Open(PhoneScreens.Page.Contacts));
        Add(new("Activities"), () => Open(PhoneScreens.Page.Activities));
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
    /// <see cref="Teleporter.Curtain"/>: with the phone out, a jump waits until the lens is square on
    /// the phone and the world black behind it (and runs then from <see cref="StepTravel"/>). In the
    /// pocket, the jump goes as it always did.
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
        // the jump runs once the world is black behind the phone; in VR, where the eye never moves, behind a blink
        if (_pendingJump != null && XR.XrSession.Active && _stage == Stage.Open)
        {
            XR.XrSession.Rig?.Blink();
            RunPendingJump();
        }
        else if (_pendingJump != null && _player() is { PhoneBackdropShown: >= 1f }) RunPendingJump();
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

    /// <summary>A key's direction on the phone: WASD and the arrows, or the pad's D-pad (as ui_*).</summary>
    private static Vector2? Direction(InputEvent e)
    {
        if (e is InputEventKey k)
            return k.PhysicalKeycode switch
            {
                Key.W or Key.Up => Vector2.Up, Key.S or Key.Down => Vector2.Down,
                Key.A or Key.Left => Vector2.Left, Key.D or Key.Right => Vector2.Right, _ => null,
            };
        if (e is InputEventMouse) return null;
        if (e.IsActionPressed("ui_up")) return Vector2.Up;
        if (e.IsActionPressed("ui_down")) return Vector2.Down;
        if (e.IsActionPressed("ui_left")) return Vector2.Left;
        if (e.IsActionPressed("ui_right")) return Vector2.Right;
        return null;
    }

    private static bool IsSelect(InputEvent e) =>
        e is InputEventKey { PhysicalKeycode: Key.Enter or Key.KpEnter or Key.Space or Key.E }
        || e is not InputEventKey and not InputEventMouse && e.IsActionPressed("ui_accept");

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
        if (_blocked()) return;   // a page over the phone (Activities menu) takes its own keys
        if (XR.XrSession.Active)
        {
            VrButtons(e);
            return;
        }

        if (_stage is Stage.Open && _screens.Shown == PhoneScreens.Page.Camera)
        {
            CameraInput(e);
            return;
        }
        if (e is InputEventMouse mouse)
        {
            Mouse(mouse);
            GetViewport().SetInputAsHandled();
            return;
        }
        // nothing reaches the body while the phone is out: it stands, every key is the phone's
        GetViewport().SetInputAsHandled();
        if (_stage is Stage.Opening or Stage.Closing or Stage.Stowing or Stage.Drawing && !e.IsActionPressed(PlayerInput.Phone)) return;
        if (_stage is Stage.Open) OpenKey(e);
        else FoldedKey(e);
    }

    /// <summary>Folded (or coming out): W opens, S puts away, A D pick, Enter runs; the finger swipes along.</summary>
    private void FoldedKey(InputEvent e)
    {
        if (!e.IsPressed() || e.IsEcho()) return;
        if (e.IsActionPressed(PlayerInput.Phone)) { Open(); return; }
        if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu)) { PutAway(); return; }
        if (Direction(e) is { } dir)
        {
            if (dir == Vector2.Up) Open();
            else if (dir == Vector2.Down) PutAway();
            else
            {
                _keysLead = true;
                Pick(dir.X < 0 ? -1 : 1);
            }
            return;
        }
        if (IsSelect(e))
        {
            _keysLead = true;
            RunQuick();
        }
    }

    /// <summary>Open: WASD and the arrows move on the screen (typing letters while a text box has the keys), Enter presses, Esc goes back.</summary>
    private void OpenKey(InputEvent e)
    {
        bool typing = _screens.Inner.GuiGetFocusOwner() is LineEdit;
        if (e.IsPressed() && !e.IsEcho() && (e.IsActionPressed("ui_cancel") || e is InputEventKey { PhysicalKeycode: Key.Escape }))
        {
            Back();
            return;
        }
        if (!typing && Direction(e) is { } dir && e is InputEventKey or InputEventJoypadButton or InputEventJoypadMotion or InputEventAction)
        {
            if (!e.IsPressed()) return;
            _keysLead = true;
            Push(dir == Vector2.Up ? "ui_up" : dir == Vector2.Down ? "ui_down" : dir == Vector2.Left ? "ui_left" : "ui_right");
            return;
        }
        if (!typing && IsSelect(e))
        {
            if (!e.IsPressed() || e.IsEcho()) return;
            _keysLead = true;
            Tap();
            Push("ui_accept");
            return;
        }
        _screens.Inner.PushInput(e, true);
    }

    /// <summary>
    /// VR's buttons on the phone (#920): X (the pad's X, the holding hand's lower face button) opens and
    /// folds it, B goes back, then folds, then puts it away. Everything else is the body's: the stick walks.
    /// </summary>
    private void VrButtons(InputEvent e)
    {
        if (e is not InputEventJoypadButton { Pressed: true } b) return;
        if (b.ButtonIndex == JoyButton.X)
        {
            if (_stage is Stage.Open or Stage.Opening) Fold();
            else Open();
            GetViewport().SetInputAsHandled();
        }
        else if (b.ButtonIndex == JoyButton.B)
        {
            if (_stage is Stage.Open) Back();
            else PutAway();
            GetViewport().SetInputAsHandled();
        }
    }

    private bool _vrDown, _vrDrag, _vrCover;
    private Vector2 _vrFrom, _vrLast;

    /// <summary>
    /// The real fingertip on the glass (#920, from <c>XR.XrPhone</c>): <paramref name="uv"/> where it is
    /// over a screen (null off it), <paramref name="down"/> while it touches. A touch and lift in place
    /// is a click there (on the cover, it runs the quick action); a slide is a drag: up and down
    /// scroll the list it started on, a sideways swipe picks on the cover and goes back on the inner
    /// screen. Hovering moves the pointer over the inner screen and shows the ring.
    /// </summary>
    public void VrPointer(Vector2? uv, bool cover, bool down)
    {
        _screens.ShowRing(_stage == Stage.Pocket ? null : uv, cover, down);
        if (uv is not { } at)
        {
            _vrDown = false;
            return;
        }
        if (down && !_vrDown)
        {
            _vrDown = true;
            _vrDrag = false;
            _vrFrom = _vrLast = at;
            _vrCover = cover;
            return;
        }
        if (down)
        {
            // past about 8 mm across the glass, a slide
            if (!_vrDrag && (at - _vrFrom).Length() > 0.05f) _vrDrag = true;
            if (_vrDrag && !cover && _stage == Stage.Open && _screens.ScrollAt(ToPixels(_vrFrom)) is { } list)
                list.ScrollVertical -= Mathf.RoundToInt((at.Y - _vrLast.Y) * _screens.Inner.Size.Y / _screens.InnerScale);
            _vrLast = at;
            return;
        }
        if (_vrDown)
        {
            _vrDown = false;
            var moved = at - _vrFrom;
            if (_vrDrag)
            {
                if (Mathf.Abs(moved.X) > Mathf.Abs(moved.Y) * 1.5f && Mathf.Abs(moved.X) > 0.15f)
                {
                    if (_stage == Stage.Folded) Pick(moved.X < 0 ? 1 : -1);
                    else if (!_vrCover && moved.X > 0) Back();
                }
                return;
            }
            if (_vrCover && _stage == Stage.Folded) RunQuick();
            else if (!_vrCover && _stage == Stage.Open && _vrFrom.X is >= 0f and <= 1f && _vrFrom.Y is >= 0f and <= 1f)
            {
                foreach (bool press in new[] { true, false })
                    PushMouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = press }, _vrFrom);
                Play(PhoneSounds.Tap);
            }
            return;
        }
        // hovering: the inner screen's buttons light under the finger
        if (!cover && _stage == Stage.Open && at.X is >= 0f and <= 1f && at.Y is >= 0f and <= 1f)
            PushMouse(new InputEventMouseMotion(), at);
    }

    /// <summary>A ui action pressed and released into the inner screen.</summary>
    private void Push(string action)
    {
        foreach (bool down in new[] { true, false })
            _screens.Inner.PushInput(new InputEventAction { Action = action, Pressed = down }, true);
        Play(PhoneSounds.Tap);
    }

    /// <summary>
    /// The mouse on the phone: the cursor's point on the screen moves the finger; a press and release
    /// in place is a click; a drag is a swipe (a list scrolls with it; a swipe right goes back; on the
    /// cover, left and right pick). The wheel scrolls.
    /// </summary>
    private void Mouse(InputEventMouse m)
    {
        var p = _player();
        if (p == null) return;
        _cursorUv = p.PhoneScreenPoint(m.Position, out _cursorOnCover);
        _keysLead = false;
        bool inner = _stage == Stage.Open && !_cursorOnCover;

        if (m is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel)
        {
            if (inner && _cursorUv is { } wu) PushMouse(wheel, wu);
            else if (wheel.Pressed && _stage == Stage.Folded) Pick(wheel.ButtonIndex == MouseButton.WheelUp ? -1 : 1);
            return;
        }
        if (m is InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
        {
            if (b.Pressed)
            {
                _pressed = true;
                _dragging = false;
                _pressAt = _lastAt = m.Position;
                _pressUv = _cursorUv;
                return;
            }
            if (!_pressed) return;
            _pressed = false;
            var moved = m.Position - _pressAt;
            if (_dragging)
            {
                // a sideways swipe: on the cover it picks, on an app's page a swipe right goes back
                if (Mathf.Abs(moved.X) > Mathf.Abs(moved.Y) * 1.5f && Mathf.Abs(moved.X) > 40f)
                {
                    if (_stage == Stage.Folded) Pick(moved.X < 0 ? 1 : -1);
                    else if (inner && moved.X > 0) Back();
                }
                _dragging = false;
                return;
            }
            // a click: where it was pressed
            if (_pressUv is not { } at || at.X is < 0f or > 1f || at.Y is < 0f or > 1f) return;
            if (_stage == Stage.Folded) { RunQuick(); return; }
            if (_stage == Stage.Open && !_cursorOnCover)
            {
                var down = (InputEventMouseButton)b.Duplicate();
                down.Pressed = true;
                PushMouse(down, at);
                PushMouse(b, at);
            }
            return;
        }
        if (m is InputEventMouseMotion)
        {
            if (_pressed && !_dragging && m.Position.DistanceTo(_pressAt) > SwipePixels) _dragging = true;
            // a list follows the finger up and down
            if (_dragging && inner && _pressUv is { } from && _screens.ScrollAt(ToPixels(from)) is { } list)
            {
                float dy = (m.Position.Y - _lastAt.Y) * ScreenPixelsPerWindowPixel(p);
                list.ScrollVertical -= Mathf.RoundToInt(dy);
            }
            _lastAt = m.Position;
            if (inner && _cursorUv is { } mu && !_dragging) PushMouse(m, mu);
        }
    }

    private Vector2 ToPixels(Vector2 uv) => uv * (Vector2)_screens.Inner.Size;

    /// <summary>How many of the inner screen's pixels one window pixel spans where the phone is in view.</summary>
    private float ScreenPixelsPerWindowPixel(FootPlayer p)
    {
        var size = GetViewport().GetVisibleRect().Size;
        var a = p.PhoneScreenPoint(size * 0.5f, out _);
        var b = p.PhoneScreenPoint(size * 0.5f + new Vector2(0f, 10f), out _);
        if (a is not { } ua || b is not { } ub) return 1f;
        return Mathf.Abs(ub.Y - ua.Y) * _screens.Inner.Size.Y / 10f;
    }

    /// <summary>A mouse event into the inner screen at <paramref name="uv"/>.</summary>
    private void PushMouse(InputEventMouse m, Vector2 uv)
    {
        var copy = (InputEventMouse)m.Duplicate();
        copy.Position = ToPixels(uv);
        copy.GlobalPosition = copy.Position;
        _screens.Inner.PushInput(copy, true);
    }

    /// <summary>
    /// The camera app's keys and aim: the body stands (the phone holds the keys), so the mouse turns the
    /// view here, at the game's own look rate, slower with the zoom.
    /// </summary>
    private void CameraInput(InputEvent e)
    {
        GetViewport().SetInputAsHandled();
        if (e is InputEventMouseMotion motion)
        {
            if (Input.MouseMode == Input.MouseModeEnum.Captured && _player() is { } p)
            {
                float rate = 0.0022f / _screens.Zoom;
                p.LookYaw -= motion.Relative.X * rate;
                p.LookPitch -= motion.Relative.Y * rate;
            }
            return;
        }
        if (!e.IsPressed() || e.IsEcho()) return;
        if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu) || e is InputEventKey { PhysicalKeycode: Key.Escape }) Back();
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right } || e is InputEventKey { PhysicalKeycode: Key.F or Key.Tab }
                 || e is InputEventJoypadButton { ButtonIndex: JoyButton.Y }) SetSelfie(!_selfie);
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } || IsSelect(e) || e.IsActionPressed(PlayerInput.TriggerRight)) _ = TakePhoto();
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.WheelUp } || e is InputEventJoypadButton { ButtonIndex: JoyButton.RightShoulder }
                 || e is InputEventKey { PhysicalKeycode: Key.W or Key.Up }) Zoom(1);
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.WheelDown } || e is InputEventJoypadButton { ButtonIndex: JoyButton.LeftShoulder }
                 || e is InputEventKey { PhysicalKeycode: Key.S or Key.Down }) Zoom(-1);
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
        if (_keysLead) Tap();
        Play(PhoneSounds.Tap);
    }

    private void RunQuick()
    {
        if (_quick < 0 || _quick >= _quickActions.Count) return;
        GD.Print($"[phone] quick {_quicks[_quick].Label}");
        Tap();
        Play(PhoneSounds.Tap);
        _quickActions[_quick]();
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
