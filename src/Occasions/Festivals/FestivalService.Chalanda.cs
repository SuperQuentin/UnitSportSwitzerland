using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Occasions.Festivals.Chalanda;

namespace UnitSport.Occasions.Festivals;

// The Chalandamarz on the festival service (#1356, protocol 78, docs/notes/occasions/festival-chalandamarz.md): one request with two uses, "begin the march at
// the stand I stand at" and "I rang the Treichel at this server-time stamp". The server follows the marcher's position (the stations in order, the loop's
// corridor, that the body really walks) and grades every ring against the shared march beat from the stamp the client sends, with the pogo's windows. A ring
// that lands on a beat another party member also rang on scores both a bonus. The march pays when the last station and then the stand are reached.
// A bot takes ServeChalanda too (it is counted, never paid).
public partial class FestivalService
{
    public const int ChalandaStart = 0, ChalandaRing = 1;

    /// <summary>What a Chalandamarz request or tick came to.</summary>
    public enum ChalandaEvent { Started = 0, Rang = 1, Synced = 2, Station = 3, Done = 4, Lost = 5 }

    private sealed class March
    {
        public double Start;
        public int Next;
        public int Points, Rings, Perfects, Synced;
        public long LastBeat = long.MinValue, LastGoodBeat = long.MinValue, SyncBeat = long.MinValue;
        public (double E, double N) Ref;
        public double MovedAt;
    }

    private readonly Dictionary<(string Instance, long Peer), March> _marches = new();
    private double _nextChalandaTick;

    /// <summary>The fastest believed march, m/s; <c>--chalandafast</c> (the checks, which teleport from station to station) lifts it.</summary>
    public static double ChalandaSpeedLimit { get; set; } = CmdArgs.Has("--chalandafast") ? 1e9 : ChalandaRules.SprintSpeed;

    /// <summary>Authority: marches begun, finished, rings counted (not Miss), rings in step and bot calls since start (checks).</summary>
    public int ChalandaStarted { get; private set; }

    public int ChalandaFinished { get; private set; }

    public int ChalandaGoodRings { get; private set; }

    public int ChalandaSyncRings { get; private set; }

    public int ChalandaBotActs { get; private set; }

    /// <summary>Client: the Chalandamarz told this player something: what, a number (the grade of a ring, the station, the in-step count), the running points, a line to show.</summary>
    public event Action<ChalandaEvent, int, int, string>? ChalandaTold;

    /// <summary>The Chalandamarz, if it is on and held in this region.</summary>
    public (RunningFestival Run, ResolvedSite Site)? ChalandaNow() =>
        RunningOf(ChalandaRules.FestivalId) is { Site: { } site } run ? (run, site) : null;

    /// <summary>
    /// Server (and a bot's way in): <paramref name="peer"/> begins the march at the stand (<see cref="ChalandaStart"/>) or rings the Treichel
    /// (<see cref="ChalandaRing"/>) at server-time <paramref name="stamp"/>. Returns null when it counted, else why not (the peer is told too).
    /// </summary>
    public string? ServeChalanda(long peer, int what, double stamp)
    {
        if (!Authority) return "Not here.";
        if (ChalandaNow() is not { } m || FestivalCatalog.Get(m.Run.Id) is not { } def) return Tell(peer, "The Chalandamarz is not on.");
        if (what is < ChalandaStart or > ChalandaRing) return Tell(peer, "There is nothing like that here.");
        if (peer < 0) { ChalandaBotActs++; return null; }
        var (run, site) = m;
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is null) return Tell(peer, "No adventuring now.");
        if (Connected(peer) && !Allowed(peer)) return "too fast";
        if (_adventure.PositionOf?.Invoke(peer) is not { } at) return Tell(peer, "Go to the Treichel stand.");
        return what == ChalandaStart ? ServeMarchStart(peer, run, site, at.E, at.N) : ServeRing(peer, run, site, stamp, at.E, at.N);
    }

    // ---- the march ------------------------------------------------------------------------------------------

    private string? ServeMarchStart(long peer, RunningFestival run, ResolvedSite site, double e, double n)
    {
        if (!ChalandaRules.NearStand(site, e, n)) return Tell(peer, "Go to the Treichel stand first.");
        double now = GameClock.Now;
        _marches[(run.Instance, peer)] = new March { Start = now, Next = 0, Ref = (e, n), MovedAt = now };
        ChalandaStarted++;
        NoteChalanda(peer, ChalandaEvent.Started, 0, 0, "The procession sets off: walk the loop and ring the Treichel on the beat!");
        return null;
    }

    private string? ServeRing(long peer, RunningFestival run, ResolvedSite site, double stamp, double e, double n)
    {
        if (!_marches.TryGetValue((run.Instance, peer), out var march)) return Tell(peer, "You are not in the procession: start at the Treichel stand.");
        double now = GameClock.Now, serverNow = ClockSync.ServerNow;
        if (!ChalandaRules.BelievableStamp(stamp, serverNow)) return Tell(peer, "That ring makes no sense.");
        NoteMove(march, e, n, now);
        long beat = ChalandaRules.BeatOf(stamp);
        var grade = ChalandaRules.Grade(stamp);
        string line = "";
        if (beat == march.LastBeat) { grade = RingGrade.Miss; line = "Once a beat."; }
        else if (!ChalandaRules.OnLoop(site, e, n)) { grade = RingGrade.Miss; line = "Back onto the loop!"; }
        else if (now - march.MovedAt > ChalandaRules.MoveWindow) { grade = RingGrade.Miss; line = "Keep walking!"; }
        march.LastBeat = beat;
        march.Rings++;
        if (grade != RingGrade.Miss)
        {
            march.Points += ChalandaRules.RingPoints(grade);
            if (grade == RingGrade.Perfect) march.Perfects++;
            march.LastGoodBeat = beat;
            ChalandaGoodRings++;
            SyncWithCrew(peer, run, march, beat);
        }
        NoteChalanda(peer, ChalandaEvent.Rang, (int)grade, march.Points, line);
        return null;
    }

    /// <summary>A body that moved at least <see cref="ChalandaRules.MinStep"/> from where it was last seen walking is walking now.</summary>
    private static void NoteMove(March march, double e, double n, double now)
    {
        if (ChalandaRules.Dist(e, n, march.Ref) < ChalandaRules.MinStep) return;
        march.Ref = (e, n);
        march.MovedAt = now;
    }

    /// <summary>A ring on a beat that another marching member of the party also rang on: each of the two is paid the bonus once for that beat.</summary>
    private void SyncWithCrew(long peer, RunningFestival run, March mine, long beat)
    {
        if (CrewKeys(peer) is not { } keys) return;
        foreach (long other in CrewPeers(keys))
        {
            if (other == peer || !_marches.TryGetValue((run.Instance, other), out var theirs) || theirs.LastGoodBeat != beat) continue;
            foreach (var (p, mm) in new[] { (peer, mine), (other, theirs) })
            {
                if (mm.SyncBeat == beat) continue;
                mm.SyncBeat = beat;
                mm.Points += ChalandaRules.SyncBonus;
                mm.Synced++;
                ChalandaSyncRings++;
                NoteChalanda(p, ChalandaEvent.Synced, mm.Synced, mm.Points, p == peer ? $"In step with {NameOfPeerOr(other)}! +{ChalandaRules.SyncBonus}" : $"In step with {NameOfPeerOr(peer)}! +{ChalandaRules.SyncBonus}");
            }
        }
    }

    private string NameOfPeerOr(long peer) => NameOfPeer(peer) is { Length: > 0 } n ? n : "the party";

    /// <summary>The marchers (every quarter second): the next station within reach is passed; the stand after the last one finishes; far out or too long is lost.</summary>
    private void ChalandaTick(double now)
    {
        if (_marches.Count == 0 || now < _nextChalandaTick) return;
        _nextChalandaTick = now + 0.25;
        var done = new List<(string, long)>();
        foreach (var (key, march) in _marches)
        {
            var (instance, peer) = key;
            if (ChalandaNow() is not { } m || m.Run.Instance != instance || !_adventure.Active(peer) || _adventure.Progress(peer) is not { } p)
            {
                done.Add(key);
                continue;
            }
            var site = m.Site;
            double elapsed = now - march.Start;
            if (elapsed > ChalandaRules.MaxSeconds(site))
            {
                done.Add(key);
                NoteChalanda(peer, ChalandaEvent.Lost, march.Next, march.Points, "The procession is over: too slow. Start again at the stand.");
                continue;
            }
            if (_adventure.PositionOf?.Invoke(peer) is not { } at) continue;
            NoteMove(march, at.E, at.N, now);
            if (ChalandaRules.Left(site, at.E, at.N))
            {
                done.Add(key);
                NoteChalanda(peer, ChalandaEvent.Lost, march.Next, march.Points, "You left the procession. Start again at the stand.");
                continue;
            }
            var path = ChalandaRules.Path(site);
            double reach = march.Next >= ChalandaRules.Stations ? ChalandaRules.StandReach : ChalandaRules.StationReach;
            if (ChalandaRules.Dist(at.E, at.N, path[march.Next]) > reach) continue;
            march.Next++;
            if (march.Next <= ChalandaRules.Stations)
            {
                NoteChalanda(peer, ChalandaEvent.Station, march.Next, march.Points,
                    march.Next == ChalandaRules.Stations ? "Last station: back to the stand!" : $"Station {march.Next}/{ChalandaRules.Stations}");
                continue;
            }
            done.Add(key);
            FinishMarch(peer, p, m.Run, site, march, elapsed);
        }
        foreach (var key in done) _marches.Remove(key);
    }

    private void FinishMarch(long peer, PlayerProgress p, RunningFestival run, ResolvedSite site, March march, double seconds)
    {
        if (!ChalandaRules.Plausible(site, seconds, ChalandaSpeedLimit))
        {
            NoteChalanda(peer, ChalandaEvent.Lost, ChalandaRules.Stations, march.Points, "That was not a march: nobody runs that fast.");
            return;
        }
        bool first = PlaysToday(peer, run.Id, ChalandaRules.UmzugGame) == 0;
        int batzen = ChalandaRules.Batzen(march.Points);
        int granted = Award(peer, run.Id, batzen, $"the procession in {seconds:F0} s, {march.Points} points ({march.Synced} in step)", ChalandaRules.UmzugGame, march.Points);
        ChalandaFinished++;
        _adventure.Grant(peer, new Item(ItemId.EngadinerNusstorte, ChalandaRules.PastryPieces), "chalanda:nusstorte");
        if (first) _adventure.Grant(peer, new Item(ItemId.ChalandaTreichel, 1), "chalanda:treichel");
        NoteChalanda(peer, ChalandaEvent.Done, march.Perfects, march.Points,
            $"\"{ChalandaRules.Rhyme}\"  {march.Points} points ({march.Synced} rings in step), +{granted} Batzen" + (first ? " and a Treichel" : ""));
    }

    private void NoteChalanda(long peer, ChalandaEvent what, int a, int points, string text)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellChalanda, (int)what, a, points, text);
        else if (!_networked && peer > 0) TellChalanda((int)what, a, points, text);
    }

    // ---- the owner's request ---------------------------------------------------------------------------

    /// <summary>Client: begin the march at the stand I stand at. Offline the local player is the authority.</summary>
    public void AskChalandaStart()
    {
        if (Authority) ServeChalanda(AdventureService.LocalPeer, ChalandaStart, 0);
        else RpcId(1, MethodName.RequestChalanda, ChalandaStart, 0.0);
    }

    /// <summary>Client: I rang the Treichel at server-time <paramref name="stamp"/> (<see cref="ClockSync.ServerNow"/> at the press).</summary>
    public void AskChalandaRing(double stamp)
    {
        if (Authority) ServeChalanda(AdventureService.LocalPeer, ChalandaRing, stamp);
        else RpcId(1, MethodName.RequestChalanda, ChalandaRing, stamp);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestChalanda(int what, double stamp)
    {
        if (_server) ServeChalanda(Multiplayer.GetRemoteSenderId(), what, stamp);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellChalanda(int what, int a, int points, string text) => ChalandaTold?.Invoke((ChalandaEvent)what, a, points, text);
}
