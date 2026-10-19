using Godot;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Ui;

/// <summary>
/// The quick menu (#840): a ring, drawn as the emote wheel is (<see cref="Radial"/>), with the ways
/// into everything that is not walking about: Activities, Invites, Inventory, Vehicles, Map, Phone, Emotes,
/// Bird journal, Controls. Docs: <c>docs/notes/ui/quick-menu.md</c>.
/// <list type="bullet">
/// <item>Keyboard: hold F2, aim with the mouse, let go (a click picks too). A tap opens it until a
/// pick, unless an invite is on screen: then the tap accepts it, and Shift+F2 declines it.</item>
/// <item>Pad: hold Back 0.3 s, aim with the right stick, let go. A tap of Back is the inventory, as
/// it always was, now opened on the release.</item>
/// <item>VR: the hand aims it as it aims the emote wheel; the wrist menu has Activities and Invites.</item>
/// </list>
/// Each entry presses the action it stands for (<see cref="XR.XrPad.Tap"/>, as the wrist menu does),
/// so it opens the same screen the key opens.
/// </summary>
public partial class QuickMenu : CanvasLayer
{
    private const float TapSeconds = 0.25f, PadHoldSeconds = 0.3f;
    private static readonly StringName NLookLeft = PlayerInput.LookLeft, NLookRight = PlayerInput.LookRight,
        NLookUp = PlayerInput.LookUp, NLookDown = PlayerInput.LookDown;

    /// <summary>One wedge: its name, why it cannot be used now (null: it can), and what picking it does.</summary>
    private sealed record Entry(string Name, Func<string?> Refusal, Action Pick, Func<string?>? Sub = null);

    private readonly Entry[] _entries;
    private readonly Func<bool> _blocked;
    private Ring _view = null!;
    private Vector2 _aim;
    private double _openedAt;
    /// <summary>Opened by a tap with nothing on screen to answer: stays until a pick.</summary>
    private bool _latched;
    /// <summary>Pad Back is down since this time (game clock), waiting to tell a tap from a hold; −1 when not.</summary>
    private double _padDownAt = -1;
    private bool _byPad;

    public static QuickMenu? Instance { get; private set; }

    public bool IsOpen => _view.Visible;

    /// <param name="blocked">True while a page, the loading screen or a modal is up: the menu stays shut.</param>
    /// <param name="onFoot">The local body, walking (the inventory and the travel menu are on-foot things).</param>
    /// <param name="emotes">The emote wheel, opened latched from here.</param>
    public QuickMenu(Func<bool> blocked, Func<bool> onFoot, Func<Player.EmoteWheel?> emotes)
    {
        _blocked = blocked;
        string? Online() => Permissions.Online ? null : "Needs a multiplayer game";
        _entries = new Entry[]
        {
            new("Activities", Online, () => ActivitiesScreen.Open(ActivitiesScreen.Tab.BattleRoyale)),
            new("Invites", Online, () => ActivitiesScreen.Open(ActivitiesScreen.Tab.Invites),
                () => InviteService.Client is { IncomingCount: > 0 and var n } ? $"{n} waiting" : null),
            new("Inventory", () => onFoot() ? null : "On foot only", () => Press(PlayerInput.Inventory), () => Key(PlayerInput.Inventory)),
            new("Vehicles", () => !onFoot() ? "On foot only" : Permissions.InMatch ? "Not in a match" : null,
                () => Press(PlayerInput.RideMenu), () => Key(PlayerInput.RideMenu)),
            new("Map", () => null, () => Press(PlayerInput.Teleport), () => Key(PlayerInput.Teleport)),
            new("Phone", () => onFoot() ? null : "On foot only", () => Callable.From(() => Phone.PhoneController.Instance?.TakeOut()).CallDeferred(),
                () => Key(PlayerInput.Phone)),
            new("Emotes", () => emotes() is { CanOpen: true } ? null : "Not now",
                () => emotes()?.Open(latched: true), () => Key(PlayerInput.EmoteWheel)),
            new("Bird journal", () => null, () => Press(PlayerInput.BirdJournal), () => Key(PlayerInput.BirdJournal)),
            new("Controls", () => null, () => Press(PlayerInput.Help), () => Key(PlayerInput.Help)),
        };
    }

    public QuickMenu() : this(() => true, () => false, () => null) { }

    /// <summary>The keyboard key of an action, shown small under its wedge (nothing on a pad: the ring is the way there).</summary>
    private static string? Key(string action) => InputHints.Pad ? null : InputHints.Label(action);

    /// <summary>Presses <paramref name="action"/> once the ring has let go of the input, so the screen it opens takes it.</summary>
    private static void Press(string action) => Callable.From(() => XR.XrPad.Tap(action)).CallDeferred();

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        if (IsOpen) UiFocus.Set(this, false);
    }

    public override void _Ready()
    {
        Name = "QuickMenu";
        Layer = 6;
        _view = new Ring(this) { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
        // "--quickmenu [aim]": open from boot, aimed at that wedge, for a screenshot
        if (CmdArgs.Has("--quickmenu"))
            GetTree().CreateTimer(CmdArgs.Double("--quickmenu", 1) ?? 3.0).Timeout += () =>
            {
                Open(byPad: false, latched: true);
                if (CmdArgs.Int("--quickmenu", 2) is int aim && aim >= 0)
                {
                    float a = aim * Mathf.Tau / _entries.Length;
                    _aim = new Vector2(Mathf.Sin(a), -Mathf.Cos(a));
                }
            };
    }

    /// <summary>The wedge aimed at, or −1.</summary>
    public int Highlight => _view.Highlight;

    /// <summary>For checks: the wedges' names, top first, clockwise.</summary>
    public IEnumerable<string> Names => _entries.Select(e => e.Name);

    /// <summary>Opens the ring. <paramref name="latched"/>: it stays open until a pick or Esc.</summary>
    public bool Open(bool byPad, bool latched)
    {
        if (IsOpen || _blocked()) return false;
        _byPad = byPad;
        _latched = latched;
        _aim = Vector2.Zero;
        _view.Highlight = -1;
        _view.Visible = true;
        _view.QueueRedraw();
        _openedAt = GameClock.Now;
        if (XR.XrSession.Active) XR.XrSession.ZeroHandAim();
        UiFocus.Set(this, true);
        GD.Print("[quickmenu] open");
        return true;
    }

    public void Close(bool choose)
    {
        if (!IsOpen) return;
        _view.Visible = false;
        UiFocus.Set(this, false);
        if (!choose || _view.Highlight < 0) return;
        var entry = _entries[_view.Highlight];
        if (entry.Refusal() is { } why)
        {
            InviteToasts.Instance?.Flash($"{entry.Name}: {why}");
            return;
        }
        GD.Print($"[quickmenu] picked {entry.Name}");
        entry.Pick();
    }

    /// <summary>
    /// Before the panels' own input: F2 and Shift+F2, and a pad's Back while no panel is up (a tap opens
    /// the inventory on its release, a hold the ring). With a panel up, Back is that panel's, as before.
    /// </summary>
    public override void _Input(InputEvent e)
    {
        if (IsOpen)
        {
            HandleOpen(e);
            return;
        }
        if (_blocked() || UiFocus.TextEntryActive) return;
        if (e is InputEventJoypadButton { ButtonIndex: JoyButton.Back } pad)
        {
            if (pad.Pressed) _padDownAt = GameClock.Now;
            else if (_padDownAt >= 0)
            {
                // released before the hold: the inventory, as a tap of Back always was
                _padDownAt = -1;
                Press(PlayerInput.Inventory);
            }
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is not InputEventKey { Echo: false } key || !key.IsActionPressed(PlayerInput.QuickMenu)) return;
        GetViewport().SetInputAsHandled();
        // Shift+F2 declines the invite on screen; with none, it is F2
        if (key.ShiftPressed && InviteService.Client?.Newest is { } newest)
        {
            InviteService.Client.Decline(newest.Id);
            return;
        }
        Open(byPad: false, latched: false);
    }

    private void HandleOpen(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion m:
                // the pointer stays captured; its motion pushes a virtual stick, as the emote wheel's
                _aim = (_aim + m.Relative / 90f).LimitLength(1.2f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }:
                Close(choose: true);
                break;
            case InputEventJoypadButton { ButtonIndex: JoyButton.Back, Pressed: false } when _byPad:
                Close(choose: true);
                break;
            case InputEventKey { Echo: false } k when k.IsActionReleased(PlayerInput.QuickMenu) && !_byPad:
                Released();
                break;
            case InputEventKey { Pressed: true, Echo: false } k when k.IsActionPressed(PlayerInput.QuickMenu):
                // F2 again on a latched ring closes it
                Close(choose: true);
                break;
            default:
                if (e.IsActionPressed(PlayerInput.Menu) || e.IsActionPressed("ui_cancel")) Close(choose: false);
                else if (e.IsActionPressed("ui_accept") && !e.IsEcho() && _latched) Close(choose: true);
                else if (e is InputEventJoypadButton { ButtonIndex: JoyButton.Back }) { }
                else return;
                break;
        }
        GetViewport().SetInputAsHandled();
    }

    /// <summary>F2 let go: a pick if aimed; a tap accepts the invite on screen, or leaves the ring open.</summary>
    private void Released()
    {
        if (_latched) return;
        if (_view.Highlight >= 0)
        {
            Close(choose: true);
            return;
        }
        if (GameClock.Now - _openedAt >= TapSeconds)
        {
            Close(choose: false);
            return;
        }
        if (InviteService.Client?.Newest is { } newest)
        {
            Close(choose: false);
            InviteService.Client.Accept(newest.Id);
            GD.Print($"[quickmenu] F2 accepts invite #{newest.Id}");
            return;
        }
        _latched = true;
        _view.QueueRedraw();
    }

    public override void _Process(double delta)
    {
        // a pad's Back held long enough: the ring
        if (_padDownAt >= 0 && GameClock.Now - _padDownAt >= PadHoldSeconds)
        {
            _padDownAt = -1;
            if (!Open(byPad: true, latched: false)) return;
        }
        if (!IsOpen) return;
        if (_blocked()) { Close(choose: false); return; }
        var stick = Input.GetVector(NLookLeft, NLookRight, NLookUp, NLookDown);
        if (stick.Length() > 0.5f) _aim = stick;
        else if (XR.XrSession.Active) _aim = XR.XrSession.HandAim.LimitLength(1.2f);
        int before = _view.Highlight;
        _view.Highlight = Radial.Aimed(_aim, _entries.Length);
        if (_view.Highlight != before) _view.QueueRedraw();
    }

    /// <summary>The ring: one wedge per entry, faint when it cannot be used now; the aimed one and why in the middle.</summary>
    private partial class Ring : Control
    {
        private readonly QuickMenu _menu;
        public int Highlight = -1;

        public Ring(QuickMenu menu) => _menu = menu;
        public Ring() : this(null!) { }

        public override void _Draw()
        {
            const float inner = 74f, outer = 214f;
            var centre = Size * 0.5f - new Vector2(0f, Mathf.Min(60f, Size.Y * 0.5f - outer - 16f));
            var entries = _menu._entries;
            var refused = entries.Select(e => e.Refusal()).ToArray();
            Radial.Draw(this, centre, inner, outer, entries.Length, Highlight, i => entries[i].Name,
                i => refused[i] != null ? UiTheme.TextFaint : i == Highlight ? UiTheme.Amber : UiTheme.Text,
                i => i == 1 ? entries[i].Sub?.Invoke() : refused[i] == null ? entries[i].Sub?.Invoke() : null);
            // an invite waiting: an amber dot on the Invites wedge
            if (InviteService.Client is { IncomingCount: > 0 })
            {
                float a = Mathf.Tau / entries.Length - Mathf.Pi * 0.5f;
                DrawCircle(centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (outer - 18f), 5f, UiTheme.Amber);
            }

            // the middle: what letting go does, or why it cannot
            string middle, small = "";
            Color colour;
            if (Highlight >= 0)
            {
                middle = entries[Highlight].Name;
                colour = refused[Highlight] == null ? UiTheme.Amber : UiTheme.TextFaint;
                small = refused[Highlight] ?? "";
            }
            else
            {
                middle = _menu._latched || _menu._byPad ? "Aim, pick" : "Aim, let go";
                colour = UiTheme.TextDim;
            }
            DrawString(UiTheme.Bold, centre + new Vector2(-inner, small.Length > 0 ? 0f : 6f), middle, HorizontalAlignment.Center, inner * 2f,
                UiTheme.FontBody, colour);
            if (small.Length > 0)
                DrawString(UiTheme.Font, centre + new Vector2(-inner, 18f), small, HorizontalAlignment.Center, inner * 2f, UiTheme.FontTiny, UiTheme.TextFaint);

            DrawString(UiTheme.Bold, centre + new Vector2(-150f, outer + 34f), "Quick menu", HorizontalAlignment.Center, 300,
                UiTheme.FontBody, UiTheme.Text);
            string hint = InputHints.Pad ? InputHints.Format("Right stick to aim, let go  ·  {ui_cancel} closes")
                : _menu._latched ? InputHints.Format("Aim, click  ·  {menu} closes") : "Aim with the mouse, let go";
            DrawString(UiTheme.Font, centre + new Vector2(-150f, outer + 54f), hint, HorizontalAlignment.Center, 300,
                UiTheme.FontTiny, UiTheme.TextFaint);
        }

        public override void _Notification(int what)
        {
            if (what == NotificationResized) QueueRedraw();
        }
    }
}
