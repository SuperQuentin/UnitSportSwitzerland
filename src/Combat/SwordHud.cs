using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Combat;

/// <summary>
/// The sword's screen (#1371), drawn while one is in the local player's hand: the stamina bar low in the centre, above the
/// hotbar (it blinks when a dodge or a charge was refused for want of it, and shows the charge as a second thin bar), and the
/// lock-on's marker over the locked target. Nothing else: the numbers float in the world (<c>SwordFx</c>). In VR the bar waits
/// for a wrist display (<c>docs/notes/combat/sword-combat.md</c>, not done).
/// </summary>
public partial class SwordHud : CanvasLayer
{
    private readonly View _view;

    public SwordHud()
    {
        Layer = 8;
        Name = "SwordHud";
        _view = new View { MouseFilter = Control.MouseFilterEnum.Ignore };
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_view);
    }

    public override void _Process(double delta)
    {
        var me = SwordService.Instance != null ? Mine() : null;
        bool show = me is { SwordDrawn: true, Downed: false } && !XR.XrSession.Active;
        _view.Visible = show;
        if (!show) return;
        _view.Step((float)delta, me!);
        _view.QueueRedraw();
    }

    /// <summary>The local player: the body whose camera is on screen.</summary>
    private FootPlayer? Mine() => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    private sealed partial class View : Control
    {
        private static readonly (int, int)[] Corners = { (1, 1), (1, -1), (-1, 1), (-1, -1) };
        private FootPlayer? _me;
        private float _share = 1f, _trail = 1f, _charge, _blink;
        private Vector2 _lock;
        private bool _locked;

        public void Step(float dt, FootPlayer me)
        {
            _me = me;
            var m = me.SwordState;
            double now = GameClock.Now;
            float share = m.Stamina.Share;
            _share = share;
            _trail = _trail < share ? share : Mathf.MoveToward(_trail, share, dt * 0.5f);
            _charge = m.ChargeShare(now);
            _blink = Mathf.Max(0f, 0.4f - (float)(now - me.SwordRefusedAt));
            var cam = me.CameraOrNull;
            _locked = false;
            if (cam != null && me.SwordLockPoint() is { } p && !cam.IsPositionBehind(p))
            {
                _lock = cam.UnprojectPosition(p);
                _locked = true;
            }
        }

        public override void _Draw()
        {
            if (_me == null) return;
            var size = Size;
            float w = Mathf.Clamp(size.X * 0.22f, 180f, 320f), h = 9f;
            var at = new Vector2((size.X - w) * 0.5f, size.Y - 132f);
            var back = new Color(0, 0, 0, 0.55f);
            DrawRect(new Rect2(at - new Vector2(2, 2), new Vector2(w + 4, h + 4)), back);
            DrawRect(new Rect2(at, new Vector2(w * _trail, h)), new Color(1f, 1f, 1f, 0.45f));
            var fill = _share < 0.25f ? UiTheme.Bad : _share < 0.5f ? UiTheme.Warn : UiTheme.Good;
            if (_blink > 0f) fill = UiTheme.Bad.Lerp(Colors.White, _blink);
            DrawRect(new Rect2(at, new Vector2(w * _share, h)), fill);
            if (_charge > 0f)
            {
                var c = _charge >= (float)(SwordRules.ChargeMin / SwordRules.ChargeMax) ? UiTheme.Amber : UiTheme.TextDim;
                DrawRect(new Rect2(at + new Vector2(0, h + 4), new Vector2(w * _charge, 3f)), c);
            }
            if (_locked)
            {
                // four corner ticks round the target, in the amber that marks the selected thing
                float r = 26f;
                foreach (var (x, y) in Corners)
                {
                    var corner = _lock + new Vector2(x * r, y * r);
                    DrawLine(corner, corner - new Vector2(x * 9f, 0), UiTheme.Amber, 2f);
                    DrawLine(corner, corner - new Vector2(0, y * 9f), UiTheme.Amber, 2f);
                }
            }
        }
    }
}
