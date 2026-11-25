using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// One motion language for panels and controls (#1831, <c>ui/motion</c>): a spring open, a faster close, a press squash,
/// a pop for a changed count, a highlight that slides to a row, rows arriving a few ms apart. Short, springy, and it never
/// delays input: a caller drops input at once and only the picture takes its 0.1 to 0.25 s.
/// <para>
/// Tweens only, created on the event (no <c>_Process</c>, nothing per frame). Each control keeps its running tween in its
/// metadata, so a new call on the same control kills the old one and a freed control takes its tween with it.
/// With <see cref="GameSettings.ReduceMotion"/> on, or in VR, every call is a plain <see cref="ReducedSeconds"/> fade with
/// no movement or scale (Press, Pop and SlideTo are instant). Timing ignores the game's time scale.
/// </para>
/// </summary>
public static class UiMotion
{
    // ---- durations (seconds) and scales, from the mock-ups' keyframes (menus-hub-rework M06, M07) ----
    public const float OpenSeconds = 0.18f;
    public const float CloseSeconds = 0.10f;
    public const float PressSeconds = 0.08f;
    public const float PressBackSeconds = 0.12f;
    public const float PopSeconds = 0.24f;
    public const float SlideSeconds = 0.12f;
    public const float StaggerStep = 0.02f;
    /// <summary>The plain fade ReduceMotion and VR use for every call that fades.</summary>
    public const float ReducedSeconds = 0.10f;

    public const float OpenFrom = 0.94f;
    /// <summary>The open overshoots this far (at 0.11 s) before settling on 1: the mock-up's 1.022.</summary>
    public const float OpenOvershoot = 1.022f;
    public const float CloseTo = 0.97f;
    public const float PressTo = 0.94f;
    public const float PopFrom = 1.4f;

    /// <summary>The shared spring: an ease-out with overshoot (feels like cubic-bezier(.34,1.56,.64,1)).</summary>
    public const Tween.TransitionType SpringTrans = Tween.TransitionType.Back;
    public const Tween.EaseType SpringEase = Tween.EaseType.Out;

    /// <summary>True when motion must be a plain fade: the player's ReduceMotion setting, or any VR session.</summary>
    public static bool Reduced => GameSettings.Current.ReduceMotion || XR.XrSession.Active;

    private static readonly StringName MotionKey = "uimotion_tween";
    private static readonly StringName ThenKey = "uimotion_then";
    private static readonly StringName KindKey = "uimotion_kind";
    private static readonly StringName SlideKey = "uimotion_slide";
    private static readonly NodePath ScaleP = "scale";
    private static readonly NodePath AlphaP = "modulate:a";
    private static readonly NodePath PositionP = "position";
    private static readonly NodePath SizeP = "size";

    private const int KindOpen = 1, KindClose = 2, KindMinor = 3;

    // ---- the calls ----

    /// <summary>Fade in and scale .94 to 1 with a slight overshoot, 0.18 s about the centre. Ends at scale 1, alpha 1.</summary>
    public static void Open(Control panel, float delay = 0f)
    {
        if (!Begin(panel, KindOpen)) return;
        panel.Modulate = Alpha(panel, 0f);
        if (!panel.IsInsideTree()) { Settle(panel); return; }
        var tw = Make(panel);
        if (Reduced)
        {
            panel.Scale = Vector2.One;
            tw.TweenProperty(panel, AlphaP, 1f, ReducedSeconds).SetDelay(delay);
        }
        else
        {
            panel.PivotOffset = panel.Size / 2;
            panel.Scale = Vector2.One * OpenFrom;
            const float rise = 0.11f;
            tw.TweenProperty(panel, ScaleP, Vector2.One * OpenOvershoot, rise).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out).SetDelay(delay);
            tw.TweenProperty(panel, ScaleP, Vector2.One, OpenSeconds - rise).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut).SetDelay(delay + rise);
            tw.TweenProperty(panel, AlphaP, 1f, rise).SetDelay(delay);
        }
        tw.Finished += () => Settle(panel);
    }

    /// <summary>
    /// Scale to .97 and fade out in 0.10 s, then <paramref name="then"/> runs exactly once (callers free or hide there).
    /// The caller drops input first, never after the tween. If a later Open, Close or <see cref="Stop"/> on the same control
    /// cuts this one short, <paramref name="then"/> runs at that moment instead: a caller waiting on it is never left hanging.
    /// </summary>
    public static void Close(Control panel, Action? then = null)
    {
        // Begin runs the callback of a Close this one replaces; ours is pending after it
        if (!Begin(panel, KindClose)) { then?.Invoke(); return; }
        if (then != null) panel.SetMeta(ThenKey, Callable.From(then));
        if (!panel.IsInsideTree()) { Fire(panel); return; }
        var tw = Make(panel);
        if (Reduced) tw.TweenProperty(panel, AlphaP, 0f, ReducedSeconds);
        else
        {
            panel.PivotOffset = panel.Size / 2;
            tw.TweenProperty(panel, ScaleP, Vector2.One * CloseTo, CloseSeconds).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
            tw.TweenProperty(panel, AlphaP, 0f, CloseSeconds).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        }
        tw.Finished += () => Fire(panel);
    }

    /// <summary>Squash to .94 in 0.08 s and spring back to 1. Nothing when reduced. Ignored while the control opens or closes.</summary>
    public static void Press(Control button)
    {
        if (!Minor(button)) return;
        var tw = Make(button);
        button.PivotOffset = button.Size / 2;
        tw.SetParallel(false);
        tw.TweenProperty(button, ScaleP, Vector2.One * PressTo, PressSeconds).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(button, ScaleP, Vector2.One, PressBackSeconds).SetTrans(SpringTrans).SetEase(SpringEase);
        tw.Finished += () => button.Scale = Vector2.One;
    }

    /// <summary>Scale <paramref name="from"/> to 1 with the spring, 0.24 s: a changed count, a landed item. Instant when reduced.</summary>
    public static void Pop(Control c, float from = PopFrom)
    {
        if (!Minor(c)) return;
        var tw = Make(c);
        c.PivotOffset = c.Size / 2;
        c.Scale = Vector2.One * from;
        tw.TweenProperty(c, ScaleP, Vector2.One, PopSeconds).SetTrans(SpringTrans).SetEase(SpringEase);
        tw.Finished += () => c.Scale = Vector2.One;
    }

    /// <summary>Move and resize a selection highlight to <paramref name="target"/> (its parent's coordinates) with the spring, 0.12 s. Instant when reduced.</summary>
    public static void SlideTo(Control highlight, Rect2 target)
    {
        Kill(highlight, SlideKey);
        if (Reduced || !highlight.IsInsideTree() || !highlight.Visible)
        {
            highlight.Position = target.Position;
            highlight.Size = target.Size;
            return;
        }
        var tw = highlight.CreateTween().SetIgnoreTimeScale(true).SetParallel();
        highlight.SetMeta(SlideKey, tw);
        tw.TweenProperty(highlight, PositionP, target.Position, SlideSeconds).SetTrans(SpringTrans).SetEase(SpringEase);
        tw.TweenProperty(highlight, SizeP, target.Size, SlideSeconds).SetTrans(SpringTrans).SetEase(SpringEase);
        tw.Finished += () => { highlight.Position = target.Position; highlight.Size = target.Size; };
    }

    /// <summary>Rows arriving <paramref name="step"/> seconds apart (an <see cref="Open"/> each, delayed). Reduced: all fade together.</summary>
    public static void Stagger(IEnumerable<Control> children, float step = StaggerStep)
    {
        float delay = 0;
        foreach (var c in children)
        {
            Open(c, delay);
            if (!Reduced) delay += step;
        }
    }

    /// <summary>True while a motion tween of this control still runs (for checks and callers that must wait for the end).</summary>
    public static bool Moving(Control c) =>
        (c.HasMeta(MotionKey) && c.GetMeta(MotionKey).As<Tween>() is { } t && GodotObject.IsInstanceValid(t) && t.IsRunning())
        || (c.HasMeta(SlideKey) && c.GetMeta(SlideKey).As<Tween>() is { } s && GodotObject.IsInstanceValid(s) && s.IsRunning());

    /// <summary>Cut a running motion short and put the control at rest (scale 1, alpha 1); a pending Close callback runs.</summary>
    public static void Stop(Control c)
    {
        Kill(c, MotionKey);
        Kill(c, SlideKey);
        Fire(c);
        Settle(c);
    }

    // ---- internals ----

    private static Color Alpha(Control c, float a) { var m = c.Modulate; m.A = a; return m; }

    private static void Settle(Control c)
    {
        if (!GodotObject.IsInstanceValid(c)) return;
        c.Scale = Vector2.One;
        c.Modulate = Alpha(c, 1f);
    }

    private static void Kill(Control c, StringName key)
    {
        if (!c.HasMeta(key)) return;
        if (c.GetMeta(key).As<Tween>() is { } t && GodotObject.IsInstanceValid(t)) t.Kill();
        c.RemoveMeta(key);
    }

    /// <summary>Runs and clears the pending Close callback, once.</summary>
    private static void Fire(Control c)
    {
        if (!GodotObject.IsInstanceValid(c) || !c.HasMeta(ThenKey)) return;
        var cb = c.GetMeta(ThenKey).AsCallable();
        c.RemoveMeta(ThenKey);
        cb.Call();
    }

    /// <summary>Kills what runs on the control (running a cut-short Close's callback); false when that freed the control.</summary>
    private static bool Begin(Control c, int kind)
    {
        Kill(c, MotionKey);
        Fire(c);
        if (!GodotObject.IsInstanceValid(c) || c.IsQueuedForDeletion()) return false;
        c.SetMeta(KindKey, kind);
        return true;
    }

    private static Tween Make(Control c)
    {
        var tw = c.CreateTween().SetIgnoreTimeScale(true).SetParallel();
        c.SetMeta(MotionKey, tw);
        return tw;
    }

    /// <summary>Press and Pop: skipped while an Open or Close runs; instant when reduced; else the running minor tween is replaced.</summary>
    private static bool Minor(Control c)
    {
        if (c.HasMeta(MotionKey) && c.HasMeta(KindKey) && (int)c.GetMeta(KindKey) is KindOpen or KindClose
            && c.GetMeta(MotionKey).As<Tween>() is { } t && GodotObject.IsInstanceValid(t) && t.IsRunning()) return false;
        Kill(c, MotionKey);
        c.Scale = Vector2.One;
        if (Reduced || !c.IsInsideTree()) return false;
        c.SetMeta(KindKey, KindMinor);
        return true;
    }
}
