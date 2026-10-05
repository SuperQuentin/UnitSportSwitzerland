using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Player;

/// <summary>
/// The emote wheel (#404): hold B (D-pad up on a pad) on foot, aim at an emote with the mouse or
/// the right stick, let go to play it. Pages of ten: the mouse wheel, Q / E or the D-pad / left
/// stick left and right turn them. A quick tap with nothing aimed stops the emote being played, or
/// plays the last one again. What is picked is only <see cref="FootPlayer.DanceId"/>, already
/// replicated; every peer draws the move off the shared clock.
/// </summary>
public partial class EmoteWheel : CanvasLayer
{
    private const float TapSeconds = 0.25f;
    private static readonly StringName NLookLeft = PlayerInput.LookLeft, NLookRight = PlayerInput.LookRight,
        NLookUp = PlayerInput.LookUp, NLookDown = PlayerInput.LookDown;

    private readonly Func<FootPlayer?> _player;
    private WheelDrawing _view = null!;
    private Vector2 _aim;
    private double _openedAt;
    private int _lastEmote = -1;

    public EmoteWheel(Func<FootPlayer?> player) => _player = player;
    public EmoteWheel() : this(() => null) { }

    public bool IsOpen => _view.Visible;

    public override void _Ready()
    {
        Layer = 6;
        _view = new WheelDrawing { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
    }

    public override void _ExitTree()
    {
        if (IsOpen) UiFocus.Set(this, false);
    }

    /// <summary>The local body if it may emote now: on foot, upright, not holding the hammer (its D-pad up turns the piece).</summary>
    private FootPlayer? Emoter =>
        _player() is { IsViewing: true } p && p.CanEmote && p.HeldItemId != (int)Items.ItemId.Hammer ? p : null;

    public override void _UnhandledInput(InputEvent e)
    {
        if (IsOpen || UiFocus.TextEntryActive || !e.IsActionPressed(PlayerInput.EmoteWheel) || e.IsEcho()) return;
        if (Emoter is not { } p) return;
        _aim = Vector2.Zero;
        _view.Highlight = -1;
        _view.Playing = p.Emote;
        _view.Page = p.Emote >= 0 ? p.Emote / Avatar.HumanMeshBuilder.EmotesPerPage : _view.Page;
        _view.Visible = true;
        _view.QueueRedraw();
        _openedAt = GameClock.Now;
        // in VR the right hand aims, from where it is now (#437)
        if (XR.XrSession.Active) XR.XrSession.ZeroHandAim();
        UiFocus.Set(this, true);
        GetViewport().SetInputAsHandled();
    }

    public override void _Input(InputEvent e)
    {
        if (!IsOpen) return;
        switch (e)
        {
            case InputEventMouseMotion m:
                // the pointer stays captured; its motion pushes a virtual stick, as the quick wheel's
                _aim = (_aim + m.Relative / 90f).LimitLength(1.2f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                TurnPage(-1);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                TurnPage(1);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }:
                Close(choose: true);
                break;
            case InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Q }:
                TurnPage(-1);
                break;
            case InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.E }:
                TurnPage(1);
                break;
            default:
                if (e.IsActionReleased(PlayerInput.EmoteWheel)) Close(choose: true);
                else if (e.IsActionPressed("ui_left") && !e.IsEcho()) TurnPage(-1);
                else if (e.IsActionPressed("ui_right") && !e.IsEcho()) TurnPage(1);
                else if (e.IsActionPressed(PlayerInput.Menu) || e.IsActionPressed("ui_cancel")) Close(choose: false);
                else return;
                break;
        }
        GetViewport().SetInputAsHandled();
    }

    private void TurnPage(int by)
    {
        _view.Page = Mathf.PosMod(_view.Page + by, Avatar.HumanMeshBuilder.EmotePages.Length);
        _view.Highlight = -1;
        _aim = Vector2.Zero;
        _view.QueueRedraw();
    }

    private void Close(bool choose)
    {
        if (!IsOpen) return;
        _view.Visible = false;
        UiFocus.Set(this, false);
        if (!choose || Emoter is not { } p) return;
        if (_view.Highlight >= 0)
        {
            int emote = _view.Page * Avatar.HumanMeshBuilder.EmotesPerPage + _view.Highlight;
            if (emote >= Avatar.HumanMeshBuilder.EmoteCount) return;
            p.DanceId = FootPlayer.EmoteDanceBase + emote;
            _lastEmote = emote;
        }
        else if (GameClock.Now - _openedAt < TapSeconds)
        {
            // a tap: stop whatever dance is on, else the last emote again
            if (p.DanceId != 0) p.DanceId = 0;
            else if (_lastEmote >= 0) p.DanceId = FootPlayer.EmoteDanceBase + _lastEmote;
        }
    }

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        if (Emoter == null) { Close(choose: false); return; }
        // the right stick aims directly; it has no captured-pointer drift to accumulate
        var stick = Input.GetVector(NLookLeft, NLookRight, NLookUp, NLookDown);
        if (stick.Length() > 0.5f) _aim = stick;
        // in VR the right hand points at a slot, as a stick would (#437)
        else if (XR.XrSession.Active) _aim = XR.XrSession.HandAim.LimitLength(1.2f);
        int count = _view.SlotsOnPage;
        int before = _view.Highlight;
        _view.Highlight = _aim.Length() < 0.35f
            ? -1
            : Mathf.PosMod((int)Mathf.Round(Mathf.Atan2(_aim.X, -_aim.Y) / (Mathf.Tau / count)), count);
        if (_view.Highlight != before) _view.QueueRedraw();
    }

    /// <summary>The ring: one sector per emote of the page, the first at the top, clockwise; the name of the aimed one in the middle.</summary>
    private partial class WheelDrawing : Control
    {
        public int Highlight = -1, Page, Playing = -1;

        public int SlotsOnPage => Mathf.Clamp(Avatar.HumanMeshBuilder.EmoteCount - Page * Avatar.HumanMeshBuilder.EmotesPerPage,
            1, Avatar.HumanMeshBuilder.EmotesPerPage);

        public override void _Draw()
        {
            const float inner = 70f, outer = 210f;
            // lifted clear of the hotbar, which its page line would otherwise sit behind
            var centre = Size * 0.5f - new Vector2(0f, Mathf.Min(70f, Size.Y * 0.5f - outer - 16f));
            int n = SlotsOnPage, first = Page * Avatar.HumanMeshBuilder.EmotesPerPage;
            float step = Mathf.Tau / n;
            var font = UiTheme.Font;
            var shadow = new Color(0, 0, 0, 0.55f);

            DrawCircle(centre, outer + 6f, new Color(UiTheme.Glass, 0.78f));
            for (int i = 0; i < n; i++)
            {
                // sector i is centred on angle i*step from the top, clockwise
                float a0 = (i - 0.5f) * step - Mathf.Pi * 0.5f, a1 = a0 + step;
                if (i == Highlight) DrawSector(centre, inner, outer, a0, a1, new Color(UiTheme.Amber, 0.22f));
                var dir = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0));
                DrawLine(centre + dir * inner, centre + dir * outer, UiTheme.Hairline, 1.5f, true);

                float am = a0 + step * 0.5f;
                var at = centre + new Vector2(Mathf.Cos(am), Mathf.Sin(am)) * (inner + outer) * 0.5f;
                int emote = first + i;
                var color = i == Highlight || emote == Playing ? UiTheme.Amber : UiTheme.Text;
                string name = Avatar.HumanMeshBuilder.EmoteName(emote);
                var pos = at + new Vector2(-60f, 5f);
                DrawString(font, pos + new Vector2(1, 2), name, HorizontalAlignment.Center, 120, UiTheme.FontSmall, shadow);
                DrawString(font, pos, name, HorizontalAlignment.Center, 120, UiTheme.FontSmall, color);
            }
            DrawArc(centre, inner, 0f, Mathf.Tau, 48, UiTheme.Hairline, 1.5f, true);
            DrawArc(centre, outer + 6f, 0f, Mathf.Tau, 96, UiTheme.Hairline, 1.5f, true);

            // the middle: what letting go does
            string middle = Highlight >= 0 ? Avatar.HumanMeshBuilder.EmoteName(first + Highlight)
                : Playing >= 0 ? "Tap: stop" : "Aim, let go";
            DrawString(UiTheme.Bold, centre + new Vector2(-inner, 6f), middle, HorizontalAlignment.Center, inner * 2f,
                UiTheme.FontBody, Highlight >= 0 ? UiTheme.Amber : UiTheme.TextDim);

            // the page under the ring: its name and a dot per page
            var pages = Avatar.HumanMeshBuilder.EmotePages;
            DrawString(UiTheme.Bold, centre + new Vector2(-150f, outer + 34f), pages[Page], HorizontalAlignment.Center, 300,
                UiTheme.FontBody, UiTheme.Text);
            for (int p = 0; p < pages.Length; p++)
                DrawCircle(centre + new Vector2((p - (pages.Length - 1) * 0.5f) * 16f, outer + 48f), 4f,
                    p == Page ? UiTheme.Amber : UiTheme.TextFaint);
            string hint = InputHints.Pad ? InputHints.Format("{ui_left} {ui_right} : page")
                : $"Wheel or {InputHints.Keyboard(Key.Q)} / {InputHints.Keyboard(Key.E)} : page";
            DrawString(font, centre + new Vector2(-150f, outer + 72f), hint, HorizontalAlignment.Center, 300,
                UiTheme.FontTiny, UiTheme.TextFaint);
        }

        private void DrawSector(Vector2 c, float r0, float r1, float a0, float a1, Color color)
        {
            const int segs = 12;
            var pts = new Vector2[(segs + 1) * 2];
            for (int k = 0; k <= segs; k++)
            {
                float a = Mathf.Lerp(a0, a1, k / (float)segs);
                var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                pts[k] = c + d * r1;
                pts[pts.Length - 1 - k] = c + d * r0;
            }
            DrawColoredPolygon(pts, color);
        }

        public override void _Notification(int what)
        {
            if (what == NotificationResized) QueueRedraw();
        }
    }
}
