namespace UnitSport.Player;

/// <summary>
/// An automatic's selector, worked by a wheel's H-shifter (#290): <see cref="None"/> without one,
/// and the automatic then picks reverse from the pedals as it always has.
/// </summary>
public enum DriveSelector { None, Park, Reverse, Neutral, Drive }

/// <summary>
/// A real H-shifter (#290), as a steering wheel reports it: the gate's button is held down for as
/// long as the lever is in that gate, and released in neutral. Unlike a key, which picks a gate
/// once, the lever's position is the gear: let it out of the gate and the box is in neutral.
///
/// <para>
/// Polled every frame with the gate the lever is in. A move into a gate is tried at once; refused
/// (no clutch: it grinds), the lever is still pushed against the gate, so it goes in silently the
/// moment the clutch goes down, as a real one does.
/// </para>
/// </summary>
public sealed class HeldShifter
{
    /// <summary>The gate the lever was in last frame: 1..6, −1 reverse, 0 neutral.</summary>
    private int _lever;
    /// <summary>The lever is in a gate the box refused: tried again while the clutch is down.</summary>
    private bool _pushing;

    /// <summary>
    /// One frame: the gate to ask the box for (0 neutral), or null for nothing to do.
    /// <paramref name="engaged"/> is the gate the box is in. <paramref name="retry"/>: the lever has
    /// been against this gate since an earlier refusal, which the driver has already heard about.
    /// Report the box's answer with <see cref="Took"/>.
    /// </summary>
    public int? Step(int lever, int engaged, float clutchPedal, out bool retry)
    {
        retry = false;
        if (lever != _lever)
        {
            _lever = lever;
            _pushing = lever != 0 && engaged != lever;
            if (lever == 0) return engaged != 0 ? 0 : null;
            return _pushing ? lever : null;
        }
        if (!_pushing || clutchPedal < 0.75f) return null;
        if (engaged == lever)
        {
            _pushing = false;
            return null;
        }
        retry = true;
        return lever;
    }

    /// <summary>
    /// The H-shifter as an automatic's selector: gate 1 is P, gate 3 and the R gate are R, out of
    /// every gate is N, and every other gate (4 above all) is D.
    /// </summary>
    public static DriveSelector Selector(int lever) => lever switch
    {
        0 => DriveSelector.Neutral,
        1 => DriveSelector.Park,
        3 or -1 => DriveSelector.Reverse,
        _ => DriveSelector.Drive,
    };

    /// <summary>The box took the gate <see cref="Step"/> asked for, or refused it (the lever stays pushed against it).</summary>
    public void Took(bool took)
    {
        if (took) _pushing = false;
    }
}
