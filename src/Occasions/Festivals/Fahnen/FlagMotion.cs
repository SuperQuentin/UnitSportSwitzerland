// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-fahnenschwingen.md).

namespace UnitSport.Occasions.Festivals.Fahnen;

/// <summary>Where the flag is in the swinger's hands: the pivot's offset (metres, right and up), the staff's roll from upright (radians, + = to the right) and the swing 0..1 (how hard the cloth streams).</summary>
public readonly record struct FlagPose(double X, double Y, double Roll, double Swing);

/// <summary>
/// The flag's motion for each figure as a function of the figure's phase (0 at its first beat, 1 at its last), so a flag is a rigid node
/// moved by a formula and the cloth is the shader's (#1269 part 5, <c>docs/notes/general/no-mesh-rebuild-per-frame.md</c>).
/// </summary>
public static class FlagMotion
{
    /// <summary>The flag held ready: upright in front of the swinger.</summary>
    public static readonly FlagPose Rest = new(0, 0, 0.12, 0);

    private static double Ease(double p) => p * p * (3 - 2 * p);

    public static FlagPose Of(FahnenFigure kind, double phase)
    {
        double p = Math.Clamp(phase, 0, 1), tau = Math.Tau;
        return kind switch
        {
            // up from the hip to overhead, upright
            FahnenFigure.Ufzug => new(0, 0.55 * Ease(p), 0.12 * (1 - Ease(p)), 0.35 * Math.Sin(Math.PI * p)),
            // the staff turns a full circle in front of the body, the hand stays
            FahnenFigure.Muehlirad => new(0, 0.2, tau * p, 0.9),
            // a lying eight: the hand traces it, the staff leans with it
            FahnenFigure.Achter => new(0.45 * Math.Sin(tau * p), 0.2 + 0.18 * Math.Sin(2 * tau * p), 0.8 * Math.Cos(tau * p), 0.75),
            // a sweep from one side to the other, the staff laid over
            FahnenFigure.Lufthieb => new(-0.6 + 1.2 * Ease(p), 0.1, -1.1 + 2.2 * Ease(p), 0.8),
            // down and under the arm, then up again
            FahnenFigure.Unterschwung => new(0.15 * Math.Sin(Math.PI * p), -0.25 * Math.Sin(Math.PI * p), 1.4 - 2.8 * Ease(p), 0.7),
            _ => Rest,
        };
    }

    /// <summary>
    /// The throw: the flag is wound back while it is held (<paramref name="heldBeats"/>, 0 before the press), flies for
    /// <paramref name="flight"/> beats after the release (<paramref name="sinceRelease"/>, in beats) turning end over end, and is
    /// caught on the beat it lands. 1.1 m of height for each beat of flight, a parabola.
    /// </summary>
    public static FlagPose Throw(double heldBeats, double sinceRelease, int flight)
    {
        if (sinceRelease < 0)
            return heldBeats <= 0 ? Rest : new(-0.1, -0.05, -0.5 * Math.Min(1, heldBeats / 1.5), 0.3);
        double u = Math.Clamp(sinceRelease / Math.Max(1, flight), 0, 1);
        double height = 1.1 * flight * 4 * u * (1 - u);
        return new(0, height, Math.Tau * flight * 0.5 * u, u < 1 ? 1 : 0.2);
    }
}
