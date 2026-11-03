using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Occasions.Festivals.Fasnacht;

namespace UnitSport.Occasions.Festivals;

// The Fasnacht on the festival service (#1357, docs/notes/occasions/festival-fasnacht.md), Luzern's and Basel's alike. Two things on the wire:
// RequestFasnacht(target): the Larven stall (a mask and a bag of Räppli, once a day), Basel's Morgestraich lantern (once, 04:00-05:30 on day 1) and a throw
// of Räppli (counted, every tenth is a play); and the Guggenmusik, shaped like the alphorn game's song: the first player to sit at the stage opens a song, whoever sits
// within 45 s plays the next voice (drum, trumpet, sousaphone), each taps the song on their own clock and sends the taps when it is over, the server grades each
// part and scores how many voices landed the same beats, then pays everyone together. Bots take the same methods and are never paid.
public partial class FestivalService
{
    public const int FasnachtStall = 0, FasnachtLantern = 1, FasnachtRaeppli = 2;

    /// <summary>What a Fasnacht request came to.</summary>
    public enum FasnachtEvent { Sync = 0, Stall = 1, Morgestraich = 2, Raeppli = 3 }

    /// <summary>Whoever sits down within this long of the first player joins that player's song; later, a new song opens.</summary>
    public const double GuggenJoinSeconds = 45;
    private const double GuggenMinShare = 0.9;
    private const double GuggenGraceSeconds = 25;

    private sealed class GuggenVoice
    {
        public long Peer;
        public GuggenPart Part;
        public double JoinedAt;
        public double[]? Taps;
    }

    private sealed class GuggenSession
    {
        public string Festival = "", Instance = "";
        public long Serial;
        public double OpenedAt;
        public GuggenChart Chart = null!;
        public readonly List<GuggenVoice> Voices = new();
    }

    private readonly List<GuggenSession> _guggen = new();
    private readonly Dictionary<string, long> _guggenSongs = new();
    private readonly Dictionary<(string Instance, string Key), int> _throws = new();
    private readonly Dictionary<long, double> _throwAt = new();
    private double _nextGuggenTick;

    /// <summary>Authority: stalls served, Morgestraich lanterns lit, throws counted and bot calls since start (checks).</summary>
    public int FasnachtStalls { get; private set; }

    public int FasnachtLanterns { get; private set; }

    public int FasnachtThrows { get; private set; }

    public int FasnachtBotActs { get; private set; }

    /// <summary>Server (checks, bots): every Guggenmusik song paid so far, in order: the peer, its result.</summary>
    public List<(long Peer, GuggenResult Result)> GuggenPaid { get; } = new();

    /// <summary>Client: the Fasnacht told this player something: what, a count, a line to show.</summary>
    public event Action<FasnachtEvent, int, string>? FasnachtTold;

    /// <summary>Client: a seat in a Guggenmusik song was granted.</summary>
    public event Action<GuggenSeat>? GuggenSeated;

    /// <summary>Client: the song this player played was graded and paid.</summary>
    public event Action<GuggenResult>? GuggenGraded;

    /// <summary>The voices in the songs being played now, for checks.</summary>
    public int GuggenVoices => _guggen.Sum(s => s.Voices.Count);

    /// <summary>The Fasnacht that is on in this region (Luzern's, else Basel's: they run at different times), or null.</summary>
    public (RunningFestival Run, ResolvedSite Site)? FasnachtNow() =>
        (RunningOf(FasnachtRules.FestivalId) ?? RunningOf(FasnachtRules.BaselId)) is { Site: { } site } run ? (run, site) : null;

    /// <summary>The hour of the game's day, for the Morgestraich: the sky on a client, the world clock on a server.</summary>
    private static double FasnachtHour() => World.WorldClock.CurrentHour;

    // ---- the stall, the lantern, the throws --------------------------------------------------------------------

    /// <summary>
    /// Server (and a bot's way in): <paramref name="peer"/> uses the Larven stall, the Morgestraich lantern it stands at, or throws Räppli
    /// (<see cref="FasnachtStall"/>, <see cref="FasnachtLantern"/>, <see cref="FasnachtRaeppli"/>). Returns null when it counted, else why not (a refusal is told
    /// to the peer too, except a throw of Räppli away from any festival, which is nobody's business).
    /// </summary>
    public string? ServeFasnacht(long peer, int target)
    {
        if (!Authority) return "Not here.";
        if (target is < FasnachtStall or > FasnachtRaeppli) return Tell(peer, "There is nothing like that here.");
        if (FasnachtNow() is not { } m || FestivalCatalog.Get(m.Run.Id) is not { } def) return target == FasnachtRaeppli ? "Not on." : Tell(peer, "The Fasnacht is not on.");
        if (peer < 0) { FasnachtBotActs++; return null; }
        var (run, site) = m;
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) return target == FasnachtRaeppli ? "Not adventuring." : Tell(peer, "No adventuring now.");
        if (Connected(peer) && !Allowed(peer)) return "too fast";
        if (_adventure.PositionOf?.Invoke(peer) is not { } at) return target == FasnachtRaeppli ? "No position." : Tell(peer, "Go to the square.");
        return target switch
        {
            FasnachtStall => ServeStall(peer, p, run, def, site, at.E, at.N),
            FasnachtLantern => ServeLantern(peer, p, run, def, site, at.E, at.N),
            _ => ServeThrow(peer, p, run, def, site, at.E, at.N),
        };
    }

    private string? ServeStall(long peer, PlayerProgress p, RunningFestival run, FestivalDef def, ResolvedSite site, double e, double n)
    {
        if (!FasnachtRules.NearStall(site, e, n, FasnachtRules.ServerSlack)) return Tell(peer, "Go to the Larven stall first.");
        if (PlayedToday(p, run, def, FasnachtRules.LarvenGame) > 0) return Tell(peer, "You have had your Larve today.");
        int mask = FasnachtRules.MaskFor(p.Key, run.Day);
        int granted = Award(peer, run.Id, FasnachtRules.StallBatzen, "a Larve from the stall", FasnachtRules.LarvenGame, FasnachtRules.StallPoints);
        _adventure.Grant(peer, new Item(ItemId.LarveWaggis + mask, 1), "fasnacht:larve");
        _adventure.Grant(peer, new Item(ItemId.Raeppli, FasnachtRules.StallRaeppli), "fasnacht:raeppli");
        FasnachtStalls++;
        NoteFasnacht(peer, FasnachtEvent.Stall, mask, $"A Larve ({ItemDefs.Get(ItemId.LarveWaggis + mask)?.Name}) and {FasnachtRules.StallRaeppli} Räppli: +{granted} Batzen. Put the Larve on from your pack.");
        return null;
    }

    private string? ServeLantern(long peer, PlayerProgress p, RunningFestival run, FestivalDef def, ResolvedSite site, double e, double n)
    {
        if (!def.IsOpen(FasnachtRules.MorgestraichGame, run.Day)) return Tell(peer, "The Morgestraich is Basel's.");
        if (!FasnachtRules.NearLantern(site, e, n, FasnachtRules.ServerSlack)) return Tell(peer, "Go to the Vorlaarve lantern first.");
        if (!FasnachtRules.Morgestraich(run.Id, run.Day, FasnachtHour())) return Tell(peer, "The Morgestraich is on the first day at four in the morning.");
        if (PlayedToday(p, run, def, FasnachtRules.MorgestraichGame) > 0) return Tell(peer, "You marched at the Morgestraich today.");
        int granted = Award(peer, run.Id, FasnachtRules.MorgestraichBatzen, "the Morgestraich", FasnachtRules.MorgestraichGame, FasnachtRules.MorgestraichPoints);
        FasnachtLanterns++;
        NoteFasnacht(peer, FasnachtEvent.Morgestraich, 0, $"\"{FasnachtRules.Saying}\"  +{granted} Batzen");
        return null;
    }

    private string? ServeThrow(long peer, PlayerProgress p, RunningFestival run, FestivalDef def, ResolvedSite site, double e, double n)
    {
        if (FasnachtRules.Dist(e, n, (site.E, site.N)) > FasnachtRules.RaeppliReach + FasnachtRules.ServerSlack) return "Too far from the Fasnacht.";
        double now = GameClock.Now;
        if (_throwAt.TryGetValue(peer, out double last) && now - last < FasnachtRules.ThrowGap) return "Too fast.";
        _throwAt[peer] = now;
        var key = (run.Instance, p.Key);
        int count = _throws.GetValueOrDefault(key) + 1;
        _throws[key] = count;
        FasnachtThrows++;
        if (count % FasnachtRules.ThrowsPerPlay != 0) return null;
        int granted = Award(peer, run.Id, FasnachtRules.RaeppliBatzen, "ten handfuls of Räppli", FasnachtRules.RaeppliGame, FasnachtRules.RaeppliPoints);
        NoteFasnacht(peer, FasnachtEvent.Raeppli, count, granted > 0 ? $"Räppli over the street: +{granted} Batzen" : "Räppli over the street (today's Batzen are in)");
        return null;
    }

    private void NoteFasnacht(long peer, FasnachtEvent what, int a, string text)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellFasnacht, (int)what, a, text);
        else if (!_networked && peer > 0) TellFasnacht((int)what, a, text);
    }

    // ---- the Guggenmusik ------------------------------------------------------------------------------------------

    /// <summary>
    /// Server (and a bot's way in): <paramref name="peer"/> sits down at the stage. Needs a running Fasnacht with the Guggenmusik open, the body at the stage and an
    /// adventuring player. Returns null when it took a seat (<paramref name="seat"/>), else why not.
    /// </summary>
    public string? ServeGuggenStart(long peer, out GuggenSeat? seat)
    {
        seat = null;
        if (!Authority) return "Not here.";
        if (FasnachtNow() is not { } m || FestivalCatalog.Get(m.Run.Id) is not { } def || !def.IsOpen(FasnachtRules.GuggenGame, m.Run.Day))
            return Tell(peer, "There is no Guggenmusik to play with now.");
        var (run, site) = m;
        if (_adventure.PositionOf?.Invoke(peer) is not { } at || !FasnachtRules.NearStage(site, at.E, at.N, FasnachtRules.ServerSlack))
            return Tell(peer, "Go to the Guggenmusik's stage to play.");
        if (peer > 0 && !_adventure.Active(peer)) return Tell(peer, "No adventuring now.");
        if (peer > 0 && Connected(peer) && !Allowed(peer)) return "too fast";

        double now = GameClock.Now;
        DropGuggenVoice(peer);
        var session = _guggen.LastOrDefault(s => s.Instance == run.Instance && now - s.OpenedAt <= GuggenJoinSeconds && s.Voices.Count < GuggenRules.MaxVoices);
        if (session == null)
        {
            long serial = _guggenSongs.GetValueOrDefault(run.Instance) + 1;
            _guggenSongs[run.Instance] = serial;
            session = new GuggenSession { Festival = run.Id, Instance = run.Instance, Serial = serial, OpenedAt = now, Chart = GuggenChart.Make(Horn.HornWire.SeedOf(run.Instance + ":guggen", serial)) };
            _guggen.Add(session);
        }
        var part = (GuggenPart)session.Voices.Count;
        session.Voices.Add(new GuggenVoice { Peer = peer, Part = part, JoinedAt = now });
        seat = new GuggenSeat(session.Chart.Seed, part, session.Voices.Count);
        GD.Print($"[festival] guggen: peer {peer} sits down as {part} of song {session.Chart.Seed} ({session.Voices.Count} voice(s), {session.Chart.Bpm:F0} BPM)");
        NoteGuggenSeat(peer, seat.Value);
        return null;
    }

    /// <summary>
    /// Server (and a bot's way in): the taps of <paramref name="peer"/>'s song, in seconds from its start. Needs a seat taken a song's length ago (a bot's are taken on
    /// trust), the body not far from the stage, and well-formed taps.
    /// </summary>
    public string? ServeGuggenSong(long peer, double[] taps)
    {
        if (!Authority) return "Not here.";
        var (session, voice) = FindGuggenVoice(peer);
        if (session == null || voice == null) return Tell(peer, "You have no seat in a song.");
        if (voice.Taps != null) return Tell(peer, "You already played this one.");
        if (peer > 0 && GameClock.Now - voice.JoinedAt < session.Chart.Seconds * GuggenMinShare) return Tell(peer, "The song is not over yet.");
        if (peer > 0 && Connected(peer) && !Allowed(peer)) return "too fast";
        if (RunningOf(session.Festival)?.Site is not { } site || _adventure.PositionOf?.Invoke(peer) is not { } at || !FasnachtRules.NearStage(site, at.E, at.N, 33))
            return Tell(peer, "You left the stage.");
        if (taps.Length > GuggenWire.MaxTaps || taps.Any(t => t < 0 || t > session.Chart.Seconds + 3)) return Tell(peer, "That song cannot be read.");
        voice.Taps = taps;
        // a song of bots alone waits (for a human to sit down, or for the grace to run out): they only make up the band
        if (session.Voices.All(v => v.Taps != null) && session.Voices.Any(v => v.Peer > 0)) FinishGuggen(session);
        return null;
    }

    /// <summary>A bot plays a voice of the song of the moment: sits down, and its taps are what its skill makes of the part.</summary>
    public string? ServeGuggenBot(long peer, double skill, Random rng)
    {
        if (peer >= 0) return "Not a bot.";
        if (ServeGuggenStart(peer, out var seat) is { } why) return why;
        var (session, _) = FindGuggenVoice(peer);
        return ServeGuggenSong(peer, GuggenRules.BotTaps(session!.Chart, seat!.Value.Part, skill, rng));
    }

    private (GuggenSession? Session, GuggenVoice? Voice) FindGuggenVoice(long peer)
    {
        foreach (var s in _guggen)
            foreach (var v in s.Voices)
                if (v.Peer == peer) return (s, v);
        return (null, null);
    }

    private void DropGuggenVoice(long peer)
    {
        foreach (var s in _guggen.ToArray())
            if (s.Voices.RemoveAll(v => v.Peer == peer) > 0 && s.Voices.Count == 0) _guggen.Remove(s);
    }

    /// <summary>Every 2 s: a song whose voices did not all send their taps is paid for those that did, once the grace has run out.</summary>
    private void GuggenTick(double now)
    {
        if (_guggen.Count == 0 || now < _nextGuggenTick) return;
        _nextGuggenTick = now + 2;
        foreach (var s in _guggen.ToArray())
        {
            double last = s.Voices.Max(v => v.JoinedAt);
            if (now <= last + s.Chart.Seconds + GuggenGraceSeconds) continue;
            FinishGuggen(s);
        }
    }

    /// <summary>Grades every voice that played, scores how together they were, pays the humans and tells each its result.</summary>
    private void FinishGuggen(GuggenSession s)
    {
        _guggen.Remove(s);
        var played = s.Voices.Where(v => v.Taps != null).ToList();
        if (played.Count == 0) return;
        var scores = played.Select(v => GuggenRules.Grade(s.Chart, v.Part, v.Taps!)).ToList();
        double sync = GuggenRules.Sync(s.Chart, scores);
        double din = GuggenRules.Din(sync, played.Count);
        for (int i = 0; i < played.Count; i++)
        {
            var v = played[i];
            var sc = scores[i];
            int batzen = GuggenRules.Batzen(sc, din), points = GuggenRules.BoardPoints(sc, din);
            int granted = v.Peer > 0 ? Award(v.Peer, s.Festival, batzen, $"played with the Guggenmusik ({sc.Points}/{sc.Max}, din x{din:F2})", FasnachtRules.GuggenGame, points) : 0;
            var result = new GuggenResult(v.Part, sc.Points, sc.Max, (int)Math.Round(sync * 100), played.Count, (int)Math.Round(din * 100), granted, v.Peer > 0 ? points : 0);
            GuggenPaid.Add((v.Peer, result));
            GD.Print($"[festival] guggen: peer {v.Peer} ({v.Part}) {sc.Points}/{sc.Max}, sync {sync:P0}, din x{din:F2} with {played.Count}: +{granted} Batzen");
            NoteGuggenResult(v.Peer, result);
        }
    }

    // ---- the wire -------------------------------------------------------------------------------------------------

    private void NoteGuggenSeat(long peer, GuggenSeat seat)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellGuggenSeat, seat.Seed, (int)seat.Part, seat.Voices);
        else if (!_networked && peer > 0) TellGuggenSeat(seat.Seed, (int)seat.Part, seat.Voices);
    }

    private void NoteGuggenResult(long peer, GuggenResult r)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellGuggenResult, (int)r.Part, r.Points, r.Max, r.Sync100, r.Voices, r.Din100, r.Batzen, r.BoardPoints);
        else if (!_networked && peer > 0) TellGuggenResult((int)r.Part, r.Points, r.Max, r.Sync100, r.Voices, r.Din100, r.Batzen, r.BoardPoints);
    }

    /// <summary>Client: use the stall, the lantern or throw Räppli (<see cref="FasnachtStall"/> and the others). Offline the local player is the authority.</summary>
    public void AskFasnacht(int target)
    {
        if (Authority) ServeFasnacht(AdventureService.LocalPeer, target);
        else RpcId(1, MethodName.RequestFasnacht, target);
    }

    /// <summary>Client: asks for a seat at the stage (offline: the local server's).</summary>
    public void AskGuggenStart()
    {
        if (Authority) ServeGuggenStart(AdventureService.LocalPeer, out _);
        else RpcId(1, MethodName.RequestGuggenStart);
    }

    /// <summary>Client: sends the taps of the song just played.</summary>
    public void AskGuggenSong(double[] taps)
    {
        if (Authority) ServeGuggenSong(AdventureService.LocalPeer, taps);
        else RpcId(1, MethodName.RequestGuggenSong, GuggenWire.Encode(taps));
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestFasnacht(int target)
    {
        if (_server) ServeFasnacht(Multiplayer.GetRemoteSenderId(), target);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestGuggenStart()
    {
        if (_server) ServeGuggenStart(Multiplayer.GetRemoteSenderId(), out _);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestGuggenSong(string taps)
    {
        if (!_server) return;
        long peer = Multiplayer.GetRemoteSenderId();
        var decoded = GuggenWire.Decode(taps);
        if (decoded == null) Refuse(peer, "That song cannot be read.");
        else ServeGuggenSong(peer, decoded);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellFasnacht(int what, int a, string text) => FasnachtTold?.Invoke((FasnachtEvent)Math.Clamp(what, 0, 3), a, text);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellGuggenSeat(int seed, int part, int voices) => GuggenSeated?.Invoke(new GuggenSeat(seed, (GuggenPart)Math.Clamp(part, 0, 2), voices));

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellGuggenResult(int part, int points, int max, int sync100, int voices, int din100, int batzen, int boardPoints) =>
        GuggenGraded?.Invoke(new GuggenResult((GuggenPart)Math.Clamp(part, 0, 2), points, max, sync100, voices, din100, batzen, boardPoints));
}
