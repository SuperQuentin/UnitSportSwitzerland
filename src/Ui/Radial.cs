using Godot;

namespace UnitSport.Ui;

/// <summary>
/// The ring of a radial menu, as the emote wheel (#404) and the quick menu (#840) draw it: a glass
/// disc, one sector per entry with the first at the top and the rest clockwise, the aimed one lit in
/// amber, hairline dividers, a label in each sector. Aiming is <see cref="Aimed"/>, from a stick or a
/// pushed virtual one.
/// </summary>
public static class Radial
{
    /// <summary>The sector a stick at <paramref name="aim"/> points at among <paramref name="count"/>, or −1 near the middle.</summary>
    public static int Aimed(Vector2 aim, int count, float dead = 0.35f) =>
        aim.Length() < dead ? -1 : Mathf.PosMod((int)Mathf.Round(Mathf.Atan2(aim.X, -aim.Y) / (Mathf.Tau / count)), count);

    /// <summary>
    /// Draws the ring on <paramref name="ci"/>: <paramref name="label"/> gives each sector's name,
    /// <paramref name="color"/> its text colour (the aimed one is lit behind it), <paramref name="sub"/> an
    /// optional smaller line under the label.
    /// </summary>
    public static void Draw(CanvasItem ci, Vector2 centre, float inner, float outer, int count, int highlight,
        Func<int, string> label, Func<int, Color> color, Func<int, string?>? sub = null)
    {
        float step = Mathf.Tau / count;
        var font = UiTheme.Font;
        var shadow = new Color(0, 0, 0, 0.55f);

        ci.DrawCircle(centre, outer + 6f, new Color(UiTheme.Glass, 0.78f));
        for (int i = 0; i < count; i++)
        {
            // sector i is centred on angle i*step from the top, clockwise
            float a0 = (i - 0.5f) * step - Mathf.Pi * 0.5f, a1 = a0 + step;
            if (i == highlight) DrawSector(ci, centre, inner, outer, a0, a1, new Color(UiTheme.Amber, 0.22f));
            var dir = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0));
            ci.DrawLine(centre + dir * inner, centre + dir * outer, UiTheme.Hairline, 1.5f, true);

            float am = a0 + step * 0.5f;
            var at = centre + new Vector2(Mathf.Cos(am), Mathf.Sin(am)) * (inner + outer) * 0.5f;
            string name = label(i);
            string? below = sub?.Invoke(i);
            var pos = at + new Vector2(-60f, below == null ? 5f : -2f);
            ci.DrawString(font, pos + new Vector2(1, 2), name, HorizontalAlignment.Center, 120, UiTheme.FontSmall, shadow);
            ci.DrawString(font, pos, name, HorizontalAlignment.Center, 120, UiTheme.FontSmall, color(i));
            if (below != null)
                ci.DrawString(font, pos + new Vector2(0, 15f), below, HorizontalAlignment.Center, 120, UiTheme.FontTiny, UiTheme.TextFaint);
        }
        ci.DrawArc(centre, inner, 0f, Mathf.Tau, 48, UiTheme.Hairline, 1.5f, true);
        ci.DrawArc(centre, outer + 6f, 0f, Mathf.Tau, 96, UiTheme.Hairline, 1.5f, true);
    }

    /// <summary>A filled ring sector between radii <paramref name="r0"/> and <paramref name="r1"/>, angles in radians.</summary>
    public static void DrawSector(CanvasItem ci, Vector2 c, float r0, float r1, float a0, float a1, Color color)
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
        ci.DrawColoredPolygon(pts, color);
    }
}
