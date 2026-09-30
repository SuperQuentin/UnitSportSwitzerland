using Godot;

namespace UnitSport.Combat;

/// <summary>
/// Gunsight while flying an armed craft: a ring where the guns point, a diamond where to put it
/// to hit the target nearest the line of fire, an X on every hit, and the rounds left. Drawn from
/// the 3D points <see cref="CombatManager"/> works out each physics step, projected through
/// whichever camera is live.
/// </summary>
public partial class CombatHud : Control
{
    private static readonly Color Sight = new(0.55f, 1f, 0.6f, 0.9f);
    private static readonly Color Lead = new(1f, 0.85f, 0.3f, 0.95f);
    private static readonly Color HitColor = new(1f, 0.3f, 0.2f, 1f);

    private readonly CombatManager _combat;

    public CombatHud(CombatManager combat) => _combat = combat;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        if (!_combat.Armed || GetViewport().GetCamera3D() is not { } cam) return;
        var p = _combat.LocalPlayer?.Invoke();
        if (p == null || !p.IsViewing) return;

        if (!cam.IsPositionBehind(_combat.AimPoint))
        {
            var c = cam.UnprojectPosition(_combat.AimPoint);
            DrawArc(c, 14f, 0f, Mathf.Tau, 20, Sight, 1.5f);
            DrawRect(new Rect2(c - Vector2.One, Vector2.One * 2f), Sight);

            if (_combat.HitFlash > 0f)
                foreach (var a in new[] { new Vector2(-1, -1), new Vector2(1, 1), new Vector2(-1, 1), new Vector2(1, -1) })
                    DrawLine(c + a * 6f, c + a * 13f, HitColor, 2f);
        }

        if (_combat.LeadPoint is { } lead && !cam.IsPositionBehind(lead))
        {
            var d = cam.UnprojectPosition(lead);
            DrawPolyline(new[] { d + new Vector2(0, -9), d + new Vector2(9, 0), d + new Vector2(0, 9),
                d + new Vector2(-9, 0), d + new Vector2(0, -9) }, Lead, 1.5f);
        }

        var font = ThemeDB.FallbackFont;
        string ammo = $"GUN  {_combat.Ammo}";
        var size = font.GetStringSize(ammo, HorizontalAlignment.Left, -1, 16);
        // above the feel layer's speed readout, which sits at the bottom centre
        var screen = GetViewportRect().Size;
        DrawString(font, new Vector2((screen.X - size.X) * 0.5f, screen.Y - 88f), ammo,
            HorizontalAlignment.Left, -1, 16, _combat.Ammo > 0 ? Sight : HitColor);
    }
}
