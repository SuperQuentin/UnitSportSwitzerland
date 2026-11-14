namespace UnitSport.World;

/// <summary>
/// How much of the earth world's far band is furnished (#1657 phase 3): a scale 0..1 on the far leaves' reach (their
/// cover, trees and buildings), past which the pyramid's baked features draw the ground's look. The detail preset caps it
/// (<c>Globe.FarCap</c>: Very low none, Low half), and in <c>auto</c> the frame budget moves it under the cap: frames over
/// budget for <see cref="ShrinkAfterS"/> take <see cref="ShrinkStep"/> off, frames well under it for
/// <see cref="GrowAfterS"/> give <see cref="GrowStep"/> back. Slow to grow, quick to shrink, so it does not hunt.
/// Godot-free (the tests drive it with made-up frame times).
/// </summary>
public sealed class FarBandGovernor
{
    public const double ShrinkAfterS = 2, GrowAfterS = 6, ShrinkStep = 0.2, GrowStep = 0.1;

    /// <summary>Over budget: the frames run this much longer than the budget.</summary>
    public const double OverRatio = 1.15;

    /// <summary>Room to grow: the frames run this much shorter than the budget.</summary>
    public const double UnderRatio = 0.75;

    /// <summary>The far band's reach, as a share of the full one.</summary>
    public double Scale { get; private set; } = 1;

    /// <summary>The preset's cap; <see cref="Scale"/> never goes over it.</summary>
    public double Cap { get; private set; } = 1;

    private double _over, _under;

    /// <summary>A new cap (the preset changed): the scale follows it down at once, and up in <see cref="Step"/>.</summary>
    public bool SetCap(double cap)
    {
        Cap = Math.Clamp(cap, 0, 1);
        if (Scale <= Cap) return false;
        Scale = Cap;
        return true;
    }

    /// <summary>Full: the cap, no budget (<c>--farband full</c>).</summary>
    public void Pin(double scale)
    {
        Scale = Math.Clamp(scale, 0, Cap);
        _over = _under = 0;
    }

    /// <summary>
    /// A frame went by (<paramref name="dt"/> seconds) at <paramref name="frameMs"/> against <paramref name="budgetMs"/>;
    /// true when the scale changed.
    /// </summary>
    public bool Step(double dt, double frameMs, double budgetMs)
    {
        if (budgetMs <= 0 || frameMs <= 0) return false;
        if (frameMs > budgetMs * OverRatio) { _over += dt; _under = 0; }
        else if (frameMs < budgetMs * UnderRatio) { _under += dt; _over = 0; }
        else _over = _under = 0;
        double before = Scale;
        if (_over >= ShrinkAfterS)
        {
            Scale = Math.Max(0, Scale - ShrinkStep);
            _over = 0;
        }
        else if (_under >= GrowAfterS && Scale < Cap)
        {
            Scale = Math.Min(Cap, Scale + GrowStep);
            _under = 0;
        }
        return Scale != before;
    }
}
