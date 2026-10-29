// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-chalandamarz.md).

using UnitSport.Concert;

namespace UnitSport.Occasions.Festivals.Chalanda;

/// <summary>How near a Treichel ring came to the march beat (<see cref="PogoBeat"/>'s windows).</summary>
public enum RingGrade : byte { Miss = 0, Good = 1, Perfect = 2 }

/// <summary>
/// The Chalandamarz's rules (#1356, part of #1269): the children of the Engadin chase the winter out on 1 March, walking through the village with
/// cowbells. A function of the site alone (the server, every client and the props agree with nothing sent): a loop of eight legs round the site's
/// middle, a march beat that is the same moment for everybody (server time), and the grading of a ring against it with the concert pogo's windows.
/// Pure.
/// </summary>
public static class ChalandaRules
{
    public const string FestivalId = "chalandamarz";

    /// <summary>The one game: the march (<c>umzug</c>), open on the festival's one day.</summary>
    public const string UmzugGame = "umzug";

    // ---- the beat ---------------------------------------------------------------------------------------------

    /// <summary>The march beat: 120 per minute, a step of a walking child.</summary>
    public const double SecondsPerBeat = 0.5;

    /// <summary>The beat a server-time moment is nearest to.</summary>
    public static long BeatOf(double serverTime) => (long)Math.Round(serverTime / SecondsPerBeat);

    /// <summary>The server-time moment of a beat.</summary>
    public static double BeatTime(long beat) => beat * SecondsPerBeat;

    /// <summary>The first beat strictly after a moment.</summary>
    public static long NextBeat(double serverTime) => (long)Math.Floor(serverTime / SecondsPerBeat) + 1;

    /// <summary>A ring at <paramref name="stamp"/> graded against the nearest beat with the pogo's windows (Perfect 70 ms, Good 140 ms).</summary>
    public static RingGrade Grade(double stamp)
    {
        double off = Math.Abs(stamp - BeatTime(BeatOf(stamp))), window = PogoBeat.Window(SecondsPerBeat);
        return off > window ? RingGrade.Miss : off <= Math.Min(PogoBeat.PerfectSeconds, window * 0.5) ? RingGrade.Perfect : RingGrade.Good;
    }

    /// <summary>A stamp is believed when it is not from the future (0.3 s of network slack) nor older than 2 s.</summary>
    public static bool BelievableStamp(double stamp, double now) => double.IsFinite(stamp) && stamp <= now + 0.3 && stamp >= now - 2.0;

    // ---- the ground -------------------------------------------------------------------------------------------

    /// <summary>The loop's radius round the site's middle, the corridor of the walk either side of it, and how far out a child is left behind, m.</summary>
    public const double LoopRadius = 45, LoopWidth = 12, LeaveMargin = 40;

    /// <summary>Seven stations round the loop, then the stand again as the finish.</summary>
    public const int Stations = 7;

    /// <summary>How near a body must be to the stand and to a station, m.</summary>
    public const double StandReach = 5, StationReach = 7;

    /// <summary>The Treichel stand, on the loop's west side; the march starts and ends there.</summary>
    public static (double E, double N) StandAt(ResolvedSite site) => (site.E - LoopRadius, site.N);

    /// <summary>
    /// The march's points in order: seven stations counter-clockwise round the loop (a leg is 45 degrees), then the stand again.
    /// Index <see cref="Stations"/> is the finish.
    /// </summary>
    public static (double E, double N)[] Path(ResolvedSite site)
    {
        var path = new (double, double)[Stations + 1];
        for (int k = 0; k < Stations; k++)
        {
            double a = Math.PI + (k + 1) * (2 * Math.PI / (Stations + 1));
            path[k] = (site.E + LoopRadius * Math.Cos(a), site.N + LoopRadius * Math.Sin(a));
        }
        path[Stations] = StandAt(site);
        return path;
    }

    /// <summary>The length of the whole march from the stand, m.</summary>
    public static double Length(ResolvedSite site)
    {
        var (e, n) = StandAt(site);
        double len = 0;
        foreach (var (pe, pn) in Path(site))
        {
            len += Dist(pe, pn, (e, n));
            (e, n) = (pe, pn);
        }
        return len;
    }

    /// <summary>The march at a child's pace (1.4 m/s), s.</summary>
    public static double ParSeconds(ResolvedSite site) => Length(site) / 1.4;

    /// <summary>A march ends unfinished after this long, s.</summary>
    public static double MaxSeconds(ResolvedSite site) => Math.Max(300, ParSeconds(site) * 2.5);

    /// <summary>The fastest anyone is believed to cover the march, m/s: a finish quicker than that was a jump through the map.</summary>
    public const double SprintSpeed = 8;

    public static bool Plausible(ResolvedSite site, double seconds, double metresPerSecond = SprintSpeed) => seconds >= Length(site) / metresPerSecond;

    public static double Dist(double e, double n, (double E, double N) at) => Math.Sqrt((e - at.E) * (e - at.E) + (n - at.N) * (n - at.N));

    public static bool NearStand(ResolvedSite site, double e, double n) => Dist(e, n, StandAt(site)) <= StandReach;

    /// <summary>On the walk: within <see cref="LoopWidth"/> of the loop's circle.</summary>
    public static bool OnLoop(ResolvedSite site, double e, double n) => Math.Abs(Dist(e, n, (site.E, site.N)) - LoopRadius) <= LoopWidth;

    /// <summary>Gone: so far out that the child has left the procession.</summary>
    public static bool Left(ResolvedSite site, double e, double n) => Dist(e, n, (site.E, site.N)) > LoopRadius + LeaveMargin;

    // ---- walking ---------------------------------------------------------------------------------------------

    /// <summary>A ring counts only for a body that moved at least this far (m) within the last <see cref="MoveWindow"/> seconds: standing still and ringing is no march.</summary>
    public const double MinStep = 0.3, MoveWindow = 1.5;

    // ---- the score ---------------------------------------------------------------------------------------------

    /// <summary>Points of a Perfect and of a Good ring, and the bonus a ring earns when another of the party rang on the same beat.</summary>
    public const int PerfectPoints = 3, GoodPoints = 2, SyncBonus = 2;

    public static int RingPoints(RingGrade g) => g switch { RingGrade.Perfect => PerfectPoints, RingGrade.Good => GoodPoints, _ => 0 };

    /// <summary>The march's pay: 15 Batzen for walking the whole loop and a tenth of the points, up to the day's cap.</summary>
    public const int FinishBatzen = 15, UmzugDayCap = 100;

    public static int Batzen(int points) => Math.Min(UmzugDayCap, FinishBatzen + Math.Max(0, points) / 10);

    /// <summary>What the finish hands out: Nusstorte pieces every time, a Treichel the first time of the day.</summary>
    public const int PastryPieces = 2;

    /// <summary>What the children sing as the winter leaves (Romansh): "March and April, let the cows out of the stable".</summary>
    public const string Rhyme = "Chalanda Marz, chalanda Avrigl, laschè las vachas our d'stalla!";
}
