using Godot;
using UnitSport.Items;

namespace UnitSport.Combat;

/// <summary>The marks of a landing blow (#1371): a spark and a light where it hit, its sound, and the damage number floating up.</summary>
public static class SwordFx
{
    private static readonly Random Rng = new();

    /// <summary>A blow landed at <paramref name="at"/> (world) for <paramref name="damage"/>; <paramref name="heavy"/> for the finisher, the charged blow and the plunge.</summary>
    public static void Strike(Node parent, Vector3 at, bool heavy, float damage)
    {
        if (ItemEvents.Instance is { } fx)
        {
            var (stream, pitch, db) = SwordSounds.Strike.Pick(Rng);
            fx.Sound3D(at, stream, pitch * (heavy ? 0.8f : 1f), db + (heavy ? 3f : 0f), unitSize: 6f, maxDistance: 80f);
            fx.Glow(at, heavy ? new Color(1f, 0.82f, 0.4f) : new Color(1f, 0.95f, 0.8f), heavy ? 0.9f : 0.55f, 0.09f);
            fx.LightPulse(at, new Color(1f, 0.85f, 0.5f), heavy ? 3f : 1.6f, 4f, 0.08f);
        }
        Number(parent, at, damage, heavy);
    }

    /// <summary>A number rising from <paramref name="at"/> and fading, a little to a random side so a combo's do not stack.</summary>
    public static void Number(Node parent, Vector3 at, float damage, bool heavy)
    {
        var label = new Label3D
        {
            Text = Mathf.RoundToInt(damage).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, TopLevel = true, NoDepthTest = true, Shaded = false,
            FixedSize = false, PixelSize = heavy ? 0.0085f : 0.0062f, FontSize = 64, OutlineSize = 14,
            Modulate = heavy ? new Color(1f, 0.78f, 0.25f) : new Color(1f, 1f, 1f), OutlineModulate = new Color(0.16f, 0.08f, 0.04f),
            RenderPriority = 10, DoubleSided = true,
        };
        parent.AddChild(label);
        float side = ((float)Rng.NextDouble() - 0.5f) * 0.7f;
        label.GlobalPosition = at + new Vector3(side, 0.25f, 0);
        var t = label.CreateTween();
        t.SetParallel();
        t.TweenProperty(label, "position:y", label.Position.Y + (heavy ? 1.5f : 1.1f), 0.8).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        t.TweenProperty(label, "modulate:a", 0f, 0.35).SetDelay(0.5);
        t.TweenProperty(label, "outline_modulate:a", 0f, 0.35).SetDelay(0.5);
        t.Chain().TweenCallback(Callable.From(label.QueueFree));
    }
}
