// Plain C#, no Godot: linked into the unit tests (docs/notes/occasions/festival-montreuxjazz.md).

using UnitSport.Concert;
using UnitSport.Terrain.Stadiums;

namespace UnitSport.Occasions.Festivals.MontreuxJazz;

/// <summary>What the server tells a client about the jazz games.</summary>
public enum JazzEvent { Sync = 0, Marked = 1, Song = 2, Clap = 3, Bis = 4, NoBis = 5, Finale = 6, Left = 7 }

/// <summary>
/// The Montreux Jazz Festival's rules (#1394), pure: the window (<c>07-fri#1 .. +15</c>, sixteen days), the venue's frame (the stage front, the rail and the
/// audience zone from a site's centre), the Listening game (a song pays when it ends if the player stood in the zone for 80 % of it), the Encore
/// (a crowd meter of Perfect claps on the call's beats, the party's claps and the strangers' alike) and the pay of both. The set's clock is
/// <see cref="JazzSet"/>, shared with the light desk. See <c>docs/notes/occasions/festival-montreuxjazz.md</c>.
/// </summary>
public static class MontreuxJazzRules
{
    public const string FestivalId = "montreuxjazz", ListenGame = "listen", EncoreGame = "bis";

    /// <summary>Sixteen days: the first Friday of July and fifteen on (3-18 July 2026, 2-17 July 2027); the Jazz Night and the closing night.</summary>
    public const int Length = 16, JazzNight = 8, ClosingNight = 16;

    public const int ListenDayCap = 60, EncoreDayCap = 40, TokenCap = 400;
    public const int SongBatzen = 6, SongPoints = 15, EncoreBatzen = 10, EncorePoints = 25, FinaleBatzen = 20, FinalePoints = 40;
    public const int MaxSongsPerNight = 12;

    /// <summary>The Jazz Night pays the Batzen of both games half as much again.</summary>
    public const double JazzNightFactor = 1.5;

    /// <summary>Perfect claps the crowd needs on the call's eight beats, and the least of them a player's own that count for pay.</summary>
    public const int EncoreNeed = 12, EncoreOwnMin = 2;

    /// <summary>A song counts when the player stood in the zone for at least this share of it.</summary>
    public const double MinShare = 0.8;

    /// <summary>The deck's depth, m (the jazz stage is a small one).</summary>
    public const float DeckDepth = 7f;

    /// <summary>The audience zone's radius from the stage front, m; the rail's reach; the server's slack on top of a reach.</summary>
    public const float ZoneRadius = 35f, RailReach = 3.5f, ServerSlack = 3f;

    /// <summary>The longest a clap's own timestamp may differ from the server's now before the server uses its own, s.</summary>
    public const double StampSlack = 0.35;

    public static readonly (string From, string To)[] Schedule = [("07-fri#1", "+15")];

    /// <summary>The songs in a night's set: two, four on the Jazz Night, six on the closing night.</summary>
    public static int SetSongs(int night) => night == ClosingNight ? 6 : night == JazzNight ? 4 : 2;

    /// <summary>The 1-based festival night of a date, or 0 outside the window.</summary>
    public static int NightOf(DateOnly date) => FestivalCalendar.Day(Schedule, date);

    /// <summary>A song's Batzen on a night, before the group yield: 6, half as much again on the Jazz Night.</summary>
    public static int SongPay(int night) => night == JazzNight ? (int)Math.Round(SongBatzen * JazzNightFactor) : SongBatzen;

    /// <summary>The bis's Batzen on a night, before the group yield: 10, 15 on the Jazz Night.</summary>
    public static int EncorePay(int night) => night == JazzNight ? (int)Math.Round(EncoreBatzen * JazzNightFactor) : EncoreBatzen;

    // ---- the venue's frame at a site ------------------------------------------------------------------

    private static StadiumSpec Venue => StadiumCatalog.MontreuxJazzOpenAir;

    /// <summary>A point of the venue's frame (u across, v along the pitch, m from its centre) as LV95 from a centre (the real one, or a check's field).</summary>
    public static (double E, double N) FromFrame(double e0, double n0, float azimuthDeg, float u, float v)
    {
        double a = azimuthDeg * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a);
        return (e0 + u * c - v * s, n0 - (u * s + v * c));
    }

    /// <summary>An LV95 point in the venue's frame (u, v) from a centre.</summary>
    public static (double U, double V) ToFrame(double e0, double n0, float azimuthDeg, double e, double n)
    {
        double a = azimuthDeg * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a), dx = e - e0, dz = -(n - n0);
        return (dx * c + dz * s, -dx * s + dz * c);
    }

    /// <summary>The v of the deck's front edge (the stage frame's z = 0), as <c>StagePlan</c> places an end stage in the north.</summary>
    public static float FrontV => -Venue.FrontHalfV + 1.5f + DeckDepth;

    /// <summary>The stage's front (the middle of the deck's front edge) at a site, LV95.</summary>
    public static (double E, double N) StageFront(ResolvedSite site) => FromFrame(site.E, site.N, Venue.AzimuthDeg, 0, FrontV);

    /// <summary>The rail: the crew barrier 2.5 m ahead of the front, where E marks "I am listening".</summary>
    public static (double E, double N) Rail(ResolvedSite site) => FromFrame(site.E, site.N, Venue.AzimuthDeg, 0, FrontV + 2.5f);

    public static double Dist(double e, double n, (double E, double N) p) => Math.Sqrt((e - p.E) * (e - p.E) + (n - p.N) * (n - p.N));

    /// <summary>Whether a point is in the audience zone: within <see cref="ZoneRadius"/> of the stage front, ahead of it (not on or behind the deck).</summary>
    public static bool InZone(ResolvedSite site, double e, double n, float slack = 0f)
    {
        if (Dist(e, n, StageFront(site)) > ZoneRadius + slack) return false;
        return ToFrame(site.E, site.N, Venue.AzimuthDeg, e, n).V > FrontV + 1f - slack;
    }

    public static bool AtRail(ResolvedSite site, double e, double n, float slack = 0f) => Dist(e, n, Rail(site)) <= RailReach + slack;

    // ---- the clock -------------------------------------------------------------------------------------

    /// <summary>
    /// A jazz night's event on the show's clock: the desk's beat is <c>show seconds x bpm / 60</c>, show seconds = the shared clock mod a day. The moment
    /// at server time <paramref name="serverNow"/>.
    /// </summary>
    public static JazzMoment Moment(ConcertEvent e, double serverNow) =>
        JazzSet.At(JazzSet.BeatOf(serverNow % 86400.0, e.Bpm), e.SetSongs);

    /// <summary>The seconds one song lasts (its sections, without the dark bar): what the 80 % is of.</summary>
    public static double SongSeconds(ConcertEvent e) => (JazzSet.SongBars - 1) * 4.0 * 60.0 / e.Bpm;

    /// <summary>The seconds one beat lasts.</summary>
    public static double BeatSeconds(ConcertEvent e) => 60.0 / e.Bpm;

    /// <summary>Whether a player's seconds in the zone make the song count.</summary>
    public static bool Counts(double zoneSeconds, double songSeconds) => zoneSeconds >= MinShare * songSeconds - 1e-9;

    // ---- the encore --------------------------------------------------------------------------------------

    /// <summary>
    /// The crowd meter of one call: the Perfect claps of everyone, party or stranger, on the call's eight beats (a player's own beat once). The band returns at
    /// <see cref="EncoreNeed"/>. Pure; a fresh one per call.
    /// </summary>
    public sealed class Meter
    {
        private readonly Dictionary<long, PogoBeat> _judges = new();
        private readonly Dictionary<long, int> _perfects = new();
        private readonly HashSet<(long, int)> _beats = new();

        public int Total { get; private set; }
        public bool Reached => Total >= EncoreNeed;

        /// <summary>A clap at call beat <paramref name="callBeat"/> (beats from the call's start, fractional) of a beat <paramref name="secondsPerBeat"/> long; the grade.</summary>
        public PogoGrade Clap(long who, double callBeat, double secondsPerBeat)
        {
            double ring = callBeat - JazzSet.CallLeadBeats;
            if (ring < -0.5 || ring >= JazzSet.CallBeats - 0.5) return PogoGrade.None;
            if (!_judges.TryGetValue(who, out var judge)) _judges[who] = judge = new PogoBeat();
            var grade = judge.Press(callBeat, secondsPerBeat);
            int beat = (int)Math.Round(callBeat) - JazzSet.CallLeadBeats;
            // one clap a beat, whatever order they come in (the judge only knows the last one)
            if (!_beats.Add((who, beat))) return PogoGrade.Miss;
            // a Perfect on one of the ring's eight beats counts; one on a beat of the lead or the tail is only Good
            if (grade != PogoGrade.Perfect) return grade;
            if (beat < 0 || beat >= JazzSet.CallBeats) return PogoGrade.Good;
            _perfects[who] = _perfects.GetValueOrDefault(who) + 1;
            Total++;
            return grade;
        }

        public int Own(long who) => _perfects.GetValueOrDefault(who);

        public IEnumerable<long> Clappers => _perfects.Keys;
    }

    /// <summary>"3 of 12" for the meter's text.</summary>
    public static string MeterText(int total) => $"{Math.Min(total, EncoreNeed)} of {EncoreNeed}";
}
