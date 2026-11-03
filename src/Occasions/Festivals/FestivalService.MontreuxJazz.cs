using Godot;
using UnitSport.Adventure;
using UnitSport.Adventure.Together;
using UnitSport.Concert;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Occasions.Festivals.MontreuxJazz;

namespace UnitSport.Occasions.Festivals;

// Montreux Jazz on the festival service (#1394, docs/notes/occasions/festival-montreuxjazz.md): one request, "mark me as listening" (E at the stage-front rail) or
// "I clapped" (jump on the beat of the call for the bis, stamped with the shared clock). The server judges everything from the shared clock and the pure
// set clock (JazzSet, the light desk's own): a song pays when it ends if the marked player stood in the audience zone for 80 % of it, the call's Perfect claps
// of everyone fill the crowd meter and a full meter pays every clapper with at least two of their own, with the group yield for friends of a party.
public partial class FestivalService
{
    /// <summary>The request's kinds: mark "I am listening" at the rail, a clap.</summary>
    public const int MontreuxMark = 0, MontreuxClap = 1;

    /// <summary>Check seam: <c>--jazzbpm N</c> replaces the night's tempo on the server and every client (a check's songs last seconds, not minutes). Never set in the game.</summary>
    public static float? JazzBpm { get; set; } = CmdArgs.Value("--jazzbpm") is { } v && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b) ? b : null;

    /// <summary>Authority: listeners marked at the rail (a peer leaving the zone loses the mark), per-song seconds in the zone, and the call's meter (checks and the tick).</summary>
    private readonly HashSet<long> _jazzMarked = new();

    private sealed class JazzTally
    {
        public long Set = -1;
        public int Song = -1;
        public double Seconds;
    }

    private readonly Dictionary<long, JazzTally> _jazzTally = new();
    private (bool Valid, string Instance, int Day, long Set, JazzPart Part, int Song) _jazzLast;
    private MontreuxJazzRules.Meter? _jazzMeter;
    private long _jazzMeterSet = -1;
    private int _jazzMeterSent = -1;
    private double _nextJazzTick, _jazzLastTick;

    /// <summary>Authority: songs paid, bis paid, claps counted and bot uses since start (checks).</summary>
    public int MontreuxSongsPaid { get; private set; }

    public int MontreuxBisPaid { get; private set; }

    public int MontreuxClaps { get; private set; }

    public int MontreuxBotActs { get; private set; }

    /// <summary>Authority: calls whose meter was full / not full when the call ended (checks).</summary>
    public int MontreuxBisReturned { get; private set; }

    public int MontreuxBisFailed { get; private set; }

    /// <summary>Client: the server told this player something: what, a number, the meter's total, a line to show.</summary>
    public event Action<JazzEvent, int, int, string>? MontreuxTold;

    /// <summary>Client: whether the server has this player marked as listening, and the call's meter as last told.</summary>
    public bool MontreuxListening { get; private set; }

    public int MontreuxMeter { get; private set; }

    /// <summary>Montreux Jazz, if it is on and held in this region (it has no stand-in town).</summary>
    public (RunningFestival Run, ResolvedSite Site)? MontreuxNow() =>
        RunningOf(MontreuxJazzRules.FestivalId) is { Site: { } site } run ? (run, site) : null;

    /// <summary>The night's event as everyone builds it from the instance's year and the day (the director's own <see cref="ConcertPlanner.Tonight"/> in the real world).</summary>
    public static ConcertEvent MontreuxEvent(RunningFestival run)
    {
        var e = ConcertPlanner.Jazz(Terrain.Stadiums.StadiumCatalog.MontreuxJazzOpenAir, FestivalCalendar.YearOf(run.Instance), run.Day);
        return JazzBpm is { } bpm ? e with { Bpm = bpm } : e;
    }

    /// <summary>Whether the show is on at this game hour.</summary>
    private bool MontreuxShowOn(ConcertEvent e)
    {
        double h = Hour();
        return h >= e.StartHour && h < e.EndHour;
    }

    // ---- the server's side ------------------------------------------------------------------------------

    /// <summary>
    /// Server (and a bot's way in): <paramref name="peer"/> marks itself as listening at the rail (<see cref="MontreuxMark"/>) or claps (<see cref="MontreuxClap"/>) at
    /// <paramref name="stamp"/>, its own reading of the shared clock. Returns null when it counted, else why not (a refusal is told to the peer too).
    /// A bot is counted and its claps join the meter, but it is never paid.
    /// </summary>
    public string? ServeMontreux(long peer, int kind, double stamp = 0)
    {
        if (!Authority) return "Not here.";
        if (kind is not (MontreuxMark or MontreuxClap)) return Tell(peer, "No such request.");
        if (MontreuxNow() is not { } m) return Tell(peer, "Montreux Jazz is not on.");
        var (run, site) = m;
        var e = MontreuxEvent(run);
        if (peer < 0) return ServeJazzBot(run, e, kind);
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) return Tell(peer, "No adventuring now.");
        if (Connected(peer) && !Allowed(peer)) return "too fast";
        if (_adventure.PositionOf?.Invoke(peer) is not { } at) return Tell(peer, "Go to the quay.");
        if (kind == MontreuxMark) return ServeJazzMark(peer, p, run, site, e, at.E, at.N);
        return ServeJazzClap(peer, run, site, e, at.E, at.N, stamp);
    }

    private string? ServeJazzMark(long peer, PlayerProgress p, RunningFestival run, ResolvedSite site, ConcertEvent e, double east, double north)
    {
        if (!MontreuxJazzRules.AtRail(site, east, north, MontreuxJazzRules.ServerSlack)) return Tell(peer, "Go to the rail in front of the stage.");
        if (!MontreuxShowOn(e)) return Tell(peer, "Nothing is playing now: the band plays in the evening, 20:30 to 23:30.");
        _jazzMarked.Add(peer);
        var inst = p.Get(FestivalBook.Section).Of(run.Instance, run.Id);
        string line = "You are listening: stay near the stage for the song and it pays when it ends.";
        // the wristband, once a festival, on the first mark
        if (!inst.Played.ContainsKey("jazzpass"))
        {
            inst.Played["jazzpass"] = 1;
            _adventure.Touch(peer, FestivalBook.Section);
            _adventure.Grant(peer, new Item(ItemId.JazzPass, 1), "jazz");
            line = "Here is your Jazz pass. " + line;
        }
        NoteMontreux(peer, JazzEvent.Marked, run.Day, 0, line);
        return null;
    }

    private string? ServeJazzClap(long peer, RunningFestival run, ResolvedSite site, ConcertEvent e, double east, double north, double stamp)
    {
        if (!MontreuxJazzRules.InZone(site, east, north, MontreuxJazzRules.ServerSlack)) return "Too far from the stage.";
        if (!MontreuxShowOn(e)) return "No set is on.";
        double now = Net.ClockSync.ServerNow;
        // the client's own reading of the shared clock, when it is near the server's: its press, not the packet's arrival, is what is judged
        double t = stamp > 0 && Math.Abs(stamp - now) <= MontreuxJazzRules.StampSlack ? stamp : now;
        var mom = MontreuxJazzRules.Moment(e, t);
        if (mom.Part != JazzPart.Call) return "Not the call.";
        var meter = JazzMeter(run.Instance, mom.Set);
        double callBeat = JazzSet.BeatOf(t % 86400.0, e.Bpm) - JazzSet.CallStart(mom.Set, e.SetSongs);
        var grade = meter.Clap(peer, callBeat, MontreuxJazzRules.BeatSeconds(e));
        if (grade == PogoGrade.None) return null;
        if (grade == PogoGrade.Perfect) MontreuxClaps++;
        NoteMontreux(peer, JazzEvent.Clap, (int)grade, meter.Total, "");
        BroadcastMeter(run, site, meter);
        return null;
    }

    private string? ServeJazzBot(RunningFestival run, ConcertEvent e, int kind)
    {
        MontreuxBotActs++;
        if (kind != MontreuxClap || !MontreuxShowOn(e)) return null;
        double now = Net.ClockSync.ServerNow;
        var mom = MontreuxJazzRules.Moment(e, now);
        if (mom.Part != JazzPart.Call) return null;
        // a bot claps on the beat that is about to land, never twice on one (the meter drops a repeat), and is never paid
        var meter = JazzMeter(run.Instance, mom.Set);
        double callBeat = JazzSet.BeatOf(now % 86400.0, e.Bpm) - JazzSet.CallStart(mom.Set, e.SetSongs);
        meter.Clap(-1, Math.Round(callBeat), MontreuxJazzRules.BeatSeconds(e));
        return null;
    }

    private MontreuxJazzRules.Meter JazzMeter(string instance, long set)
    {
        string key = instance;
        if (_jazzMeter == null || _jazzMeterSet != set || _jazzMeterKey != key)
        {
            _jazzMeter = new MontreuxJazzRules.Meter();
            _jazzMeterSet = set;
            _jazzMeterKey = key;
            _jazzMeterSent = -1;
        }
        return _jazzMeter;
    }

    private string _jazzMeterKey = "";

    private void BroadcastMeter(RunningFestival run, ResolvedSite site, MontreuxJazzRules.Meter meter)
    {
        if (meter.Total == _jazzMeterSent) return;
        _jazzMeterSent = meter.Total;
        foreach (long other in _adventure.Peers.ToArray())
            if (other > 0 && _adventure.Active(other) && _adventure.PositionOf?.Invoke(other) is { } at && MontreuxJazzRules.InZone(site, at.E, at.N, MontreuxJazzRules.ServerSlack))
                NoteMontreux(other, JazzEvent.Sync, run.Day, meter.Total, "");
    }

    /// <summary>
    /// The party members who are with <paramref name="peer"/> now (adventuring, in the zone, within the together radius): what the group yield counts. A stranger
    /// never does, so a lone player or a crowd of strangers is paid the base alone.
    /// </summary>
    private int JazzFriends(long peer, ResolvedSite site, double e, double n)
    {
        if (PartyOf?.Invoke(peer) is not { } members) return 0;
        int count = 0;
        foreach (long other in members)
            if (other != peer && other > 0 && _adventure.Active(other) && _adventure.PositionOf?.Invoke(other) is { } at
                && MontreuxJazzRules.InZone(site, at.E, at.N) && MontreuxJazzRules.Dist(at.E, at.N, (e, n)) <= TogetherRules.Radius)
                count++;
        return Math.Min(count, TogetherRules.MaxCounted);
    }

    /// <summary>The authority's clock for the games: every quarter second.</summary>
    private void MontreuxTick(double now)
    {
        if (now < _nextJazzTick) return;
        double dt = Math.Min(now - _jazzLastTick, 1.0);
        _jazzLastTick = now;
        _nextJazzTick = now + 0.25;
        if (MontreuxNow() is not { } m)
        {
            _jazzMarked.Clear();
            _jazzTally.Clear();
            _jazzLast = default;
            _jazzMeter = null;
            return;
        }
        var (run, site) = m;
        var e = MontreuxEvent(run);
        if (!MontreuxShowOn(e))
        {
            // a show that is not on pays nothing, and a song cut short by the end of the evening was never finished
            _jazzTally.Clear();
            _jazzLast = default;
            _jazzMeter = null;
            return;
        }
        var cur = MontreuxJazzRules.Moment(e, Net.ClockSync.ServerNow);

        // who is in the zone, and for how long of this song
        foreach (long peer in _adventure.Peers.ToArray())
        {
            if (peer <= 0 || !_adventure.Active(peer) || _adventure.PositionOf?.Invoke(peer) is not { } at) continue;
            if (!MontreuxJazzRules.InZone(site, at.E, at.N))
            {
                if (_jazzMarked.Remove(peer)) NoteMontreux(peer, JazzEvent.Left, 0, 0, "You left the audience: stand near the stage and press the interact key at the rail again.");
                _jazzTally.Remove(peer);
                continue;
            }
            if (cur.Part != JazzPart.Song || !_jazzMarked.Contains(peer)) continue;
            if (!_jazzTally.TryGetValue(peer, out var tally)) _jazzTally[peer] = tally = new JazzTally();
            if (tally.Set != cur.Set || tally.Song != cur.Song) { tally.Set = cur.Set; tally.Song = cur.Song; tally.Seconds = 0; }
            tally.Seconds += dt;
        }

        var last = _jazzLast;
        // a day that moved on (a date set by hand) is another night with another set: nothing of the old one is finished here
        if (last.Valid && (last.Instance != run.Instance || last.Day != run.Day)) _jazzTally.Clear();
        if (last.Valid && last.Instance == run.Instance && last.Day == run.Day)
        {
            if (last.Part == JazzPart.Song && (cur.Part != JazzPart.Song || cur.Set != last.Set || cur.Song != last.Song)) EndSong(run, site, e, last.Set, last.Song);
            if (last.Part == JazzPart.Call && (cur.Part != JazzPart.Call || cur.Set != last.Set)) EndCall(run, site, e, last.Set);
        }
        _jazzLast = (true, run.Instance, run.Day, cur.Set, cur.Part, cur.Song);
    }

    /// <summary>A song ended: everyone marked who stood in the zone for 80 % of it is paid; on the closing night the last song adds the finale bonus.</summary>
    private void EndSong(RunningFestival run, ResolvedSite site, ConcertEvent e, long set, int song)
    {
        double length = MontreuxJazzRules.SongSeconds(e);
        bool finale = e.FinaleNight && song == e.SetSongs - 1;
        foreach (var (peer, tally) in _jazzTally.ToArray())
        {
            if (tally.Set != set || tally.Song != song) continue;
            _jazzTally.Remove(peer);
            if (!MontreuxJazzRules.Counts(tally.Seconds, length) || !_adventure.Active(peer) || _adventure.PositionOf?.Invoke(peer) is not { } at) continue;
            if (!MontreuxJazzRules.InZone(site, at.E, at.N, MontreuxJazzRules.ServerSlack)) continue;
            if (PlaysToday(peer, run.Id, MontreuxJazzRules.ListenGame) >= MontreuxJazzRules.MaxSongsPerNight) continue;
            int friends = JazzFriends(peer, site, at.E, at.N);
            int batzen = TogetherRules.Yield(MontreuxJazzRules.SongPay(run.Day), friends, Random.Shared.NextDouble());
            int granted = Award(peer, run.Id, batzen, "listened to a song", MontreuxJazzRules.ListenGame, MontreuxJazzRules.SongPoints);
            MontreuxSongsPaid++;
            string line = $"A song heard: +{granted} Batzen{(friends > 0 ? $" ({friends} friend{(friends > 1 ? "s" : "")} with you)" : "")}";
            if (finale)
            {
                int bonus = Award(peer, run.Id, TogetherRules.Yield(MontreuxJazzRules.FinaleBatzen, friends, Random.Shared.NextDouble()), "the closing concert", MontreuxJazzRules.ListenGame, MontreuxJazzRules.FinalePoints);
                line = $"The closing concert: +{granted + bonus} Batzen";
                NoteMontreux(peer, JazzEvent.Finale, run.Day, bonus, line);
            }
            else NoteMontreux(peer, JazzEvent.Song, run.Day, granted, line);
        }
    }

    /// <summary>The call ended: a full meter brings the band back and pays everyone with at least two Perfect claps of their own; a short one does not.</summary>
    private void EndCall(RunningFestival run, ResolvedSite site, ConcertEvent e, long set)
    {
        var meter = _jazzMeter;
        _jazzMeter = null;
        if (meter == null || _jazzMeterSet != set) return;
        bool reached = meter.Reached;
        if (reached) MontreuxBisReturned++; else MontreuxBisFailed++;
        string line = reached ? "The band is back!" : $"The crowd was too quiet ({MontreuxJazzRules.MeterText(meter.Total)}): no bis this time.";
        foreach (long peer in _adventure.Peers.ToArray())
        {
            if (peer <= 0 || !_adventure.Active(peer) || _adventure.PositionOf?.Invoke(peer) is not { } at) continue;
            bool near = MontreuxJazzRules.InZone(site, at.E, at.N, MontreuxJazzRules.ServerSlack);
            int own = meter.Own(peer);
            if (!near && own == 0) continue;
            if (!reached) { NoteMontreux(peer, JazzEvent.NoBis, run.Day, meter.Total, line); continue; }
            if (own < MontreuxJazzRules.EncoreOwnMin)
            {
                NoteMontreux(peer, JazzEvent.Bis, run.Day, meter.Total, own == 0 ? line : $"{line} Only {own} of your claps counted: clap along next time.");
                continue;
            }
            int friends = JazzFriends(peer, site, at.E, at.N);
            int batzen = TogetherRules.Yield(MontreuxJazzRules.EncorePay(run.Day), friends, Random.Shared.NextDouble());
            int granted = Award(peer, run.Id, batzen, "the bis", MontreuxJazzRules.EncoreGame, MontreuxJazzRules.EncorePoints);
            MontreuxBisPaid++;
            NoteMontreux(peer, JazzEvent.Bis, run.Day, meter.Total, $"{line} +{granted} Batzen");
        }
    }

    private void NoteMontreux(long peer, JazzEvent what, int a, int b, string text)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellMontreux, (int)what, a, b, text);
        else if (!_networked && peer > 0) TellMontreux((int)what, a, b, text);
    }

    /// <summary>A joiner is told the state (nothing marked yet).</summary>
    private void MontreuxWelcome(long peer)
    {
        if (MontreuxNow() is not { } m || peer <= 0) return;
        NoteMontreux(peer, JazzEvent.Sync, m.Run.Day, 0, "");
    }

    // ---- the owner's request ----------------------------------------------------------------------------

    /// <summary>Client: mark at the rail, or clap at <paramref name="stamp"/> (its reading of <c>ClockSync.ServerNow</c>). Offline the local player is the authority.</summary>
    public void AskMontreux(int kind, double stamp = 0)
    {
        if (Authority) ServeMontreux(AdventureService.LocalPeer, kind, stamp);
        else RpcId(1, MethodName.RequestMontreux, kind, stamp);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestMontreux(int kind, double stamp)
    {
        if (_server) ServeMontreux(Multiplayer.GetRemoteSenderId(), kind, stamp);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellMontreux(int what, int a, int b, string text)
    {
        var ev = (JazzEvent)what;
        if (ev == JazzEvent.Marked) MontreuxListening = true;
        else if (ev == JazzEvent.Left) MontreuxListening = false;
        if (ev is JazzEvent.Sync or JazzEvent.Clap) MontreuxMeter = b;
        else if (ev is JazzEvent.Bis or JazzEvent.NoBis) MontreuxMeter = 0;
        MontreuxTold?.Invoke(ev, a, b, text);
    }
}
