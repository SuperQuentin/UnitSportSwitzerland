// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-alphorn.md).

namespace UnitSport.Occasions.Festivals.Horn;

/// <summary>An echo off a valley wall: where it sounds from, how long after the note, how loud and how dull.</summary>
public readonly record struct Echo(double WallE, double WallN, double DistanceM, double DelaySeconds, double Gain, double LowPassHz);

/// <summary>
/// The valley's answer to a horn (#1269): from the blower, five rays in the bell's direction (+/- 20 degrees), 25 m steps out to 4 km,
/// the first sample whose ground rises over the line of sight (the horn's height) by a wall's worth is the wall. The note is replayed from
/// there after (blower to wall + wall to listener) / 343 m/s, 6 dB quieter per doubling of the path and low-passed more the further it
/// went. Pure: the ground is a delegate (LV95 E, N to height or null). Done once per note start, never per frame.
/// </summary>
public static class EchoFinder
{
    public const double SpeedOfSound = 343;
    public const double MaxRangeM = 4000, StepM = 25, SpreadDeg = 20, WallRiseM = 15, HornHeightM = 1.7;
    /// <summary>The path at which the echo is as loud as the note (every doubling of it is 6 dB less).</summary>
    public const double ReferencePathM = 60;

    public static Echo? Find(Func<double, double, double?> ground, double e, double n, double bearingRad, double listenerE, double listenerN)
    {
        if (ground(e, n) is not { } h0) return null;
        double eye = h0 + HornHeightM;
        double bestDist = double.MaxValue, bestE = 0, bestN = 0;
        for (int ray = -2; ray <= 2; ray++)
        {
            double a = bearingRad + ray * SpreadDeg / 2 * Math.PI / 180;
            double dx = Math.Sin(a), dy = Math.Cos(a);   // bearing 0 = north, clockwise
            for (double d = StepM; d <= MaxRangeM && d < bestDist; d += StepM)
            {
                double we = e + dx * d, wn = n + dy * d;
                if (ground(we, wn) is not { } h || h < eye + WallRiseM) continue;
                bestDist = d;
                bestE = we;
                bestN = wn;
                break;
            }
        }
        if (bestDist == double.MaxValue) return null;
        double back = Math.Sqrt((bestE - listenerE) * (bestE - listenerE) + (bestN - listenerN) * (bestN - listenerN));
        double path = bestDist + back;
        double gain = Math.Min(1, ReferencePathM / path);   // 1/path: -6 dB a doubling
        return new Echo(bestE, bestN, bestDist, path / SpeedOfSound, gain, Math.Clamp(7000 - path * 1.5, 900, 7000));
    }
}
