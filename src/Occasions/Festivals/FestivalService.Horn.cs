using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Occasions.Festivals.Horn;

namespace UnitSport.Occasions.Festivals;

// The alphorn and yodel game (#1269): the server's side. The first player to sit down opens a song (a seed from the festival
// instance and a count), whoever sits down within HornJoinSeconds plays the same chart (up to four, one voice each); each plays it on their own clock and sends the blows when
// it is over, the server grades them and scores the harmony chart-relative (no clocks to align), then pays everyone together. Three RPCs
// (protocol 67): a seat, the blows, the result. Bots take a seat through the same methods.
public partial class FestivalService
{
    /// <summary>Whoever sits down within this long of the first player joins that player's song; later, a new song opens.</summary>
    public const double HornJoinSeconds = 45;
    /// <summary>The blows are accepted after this share of the song has gone by, and refused sooner.</summary>
    private const double HornMinShare = 0.9;
    /// <summary>A song is paid when all its voices are in, or this long after its last voice sat down plus the song.</summary>
    private const double HornGraceSeconds = 25;

    private sealed class HornVoice
    {
        public long Peer;
        public HornPart Part;
        public double JoinedAt;
        public HornPlay[]? Plays;
    }

    private sealed class HornSession
    {
        public string Festival = "", Instance = "";
        public long Serial;
        public double OpenedAt;
        public AlphornChart Chart = null!;
        public readonly List<HornVoice> Voices = new();
    }

    private readonly List<HornSession> _horn = new();
    private readonly Dictionary<string, long> _hornSongs = new();

    /// <summary>Server (checks, bots): every song paid so far, in order: the peer, its result.</summary>
    public List<(long Peer, HornResult Result)> HornPaid { get; } = new();

    /// <summary>Client: a seat in a song was granted.</summary>
    public event Action<HornSeat>? HornSeated;

    /// <summary>Client: the song this player sang was graded and paid.</summary>
    public event Action<HornResult>? HornGraded;

    /// <summary>The voices in the songs being played now, for checks.</summary>
    public int HornVoices => _horn.Sum(s => s.Voices.Count);

    /// <summary>
    /// Server (and a bot's way in): <paramref name="peer"/> sits down to the row. Needs a running festival with the alphorn game open, the body
    /// at the row and an adventuring player. Returns null when it took a seat (<paramref name="seat"/>), else why not.
    /// </summary>
    public string? ServeHornStart(long peer, out HornSeat? seat)
    {
        seat = null;
        if (!Authority) return "Not here.";
        var run = Running.FirstOrDefault(r => r.Site != null && FestivalCatalog.Get(r.Id) is { } d && d.IsOpen(HornGround.GameId, r.Day));
        if (run?.Site is not { } site) return Tell(peer, "There is no alphorn to play now.");
        if (_adventure.PositionOf?.Invoke(peer) is not { } at || !HornGround.Near(site, at.E, at.N, HornGround.ServerReachM))
            return Tell(peer, "Go to the alphorn row to play.");
        if (peer > 0 && !_adventure.Active(peer)) return Tell(peer, "No adventuring now.");
        if (peer > 0 && Connected(peer) && !Allowed(peer)) return "too fast";

        double now = GameClock.Now;
        DropHornVoice(peer);
        var session = _horn.LastOrDefault(s => s.Instance == run.Instance && now - s.OpenedAt <= HornJoinSeconds);
        if (session == null)
        {
            long serial = _hornSongs.GetValueOrDefault(run.Instance) + 1;
            _hornSongs[run.Instance] = serial;
            session = new HornSession { Festival = run.Id, Instance = run.Instance, Serial = serial, OpenedAt = now, Chart = AlphornChart.Make(HornWire.SeedOf(run.Instance, serial)) };
            _horn.Add(session);
        }
        if (session.Voices.Count >= AlphornRules.MaxVoices) return Tell(peer, "The row is full. Wait for the next song.");
        var part = (HornPart)session.Voices.Count;
        session.Voices.Add(new HornVoice { Peer = peer, Part = part, JoinedAt = now });
        seat = new HornSeat(session.Chart.Seed, part, session.Voices.Count);
        GD.Print($"[festival] alphorn: peer {peer} sits down as {part} of song {session.Chart.Seed} ({session.Voices.Count} voice(s), {session.Chart.Bpm:F0} BPM, {session.Chart.Melody.Length} notes)");
        NoteHornSeat(peer, seat.Value);
        return null;
    }

    /// <summary>
    /// Server (and a bot's way in): the blows of <paramref name="peer"/>'s song, in seconds from its start. Needs a seat taken a song's length
    /// ago (a bot's are taken on trust), the body not far from the row, and well-formed plays.
    /// </summary>
    public string? ServeHornSong(long peer, HornPlay[] plays)
    {
        if (!Authority) return "Not here.";
        var (session, voice) = FindHornVoice(peer);
        if (session == null || voice == null) return Tell(peer, "You have no seat in a song.");
        if (voice.Plays != null) return Tell(peer, "You already played this one.");
        if (peer > 0 && GameClock.Now - voice.JoinedAt < session.Chart.Seconds * HornMinShare) return Tell(peer, "The song is not over yet.");
        if (peer > 0 && Connected(peer) && !Allowed(peer)) return "too fast";
        var run = RunningOf(session.Festival);
        if (run?.Site is not { } site || _adventure.PositionOf?.Invoke(peer) is not { } at || !HornGround.Near(site, at.E, at.N, 40))
            return Tell(peer, "You left the row.");
        if (plays.Length > HornWire.MaxPlays || plays.Any(p => !AlphornRules.IsPartial(p.Partial) || p.On < 0 || p.Off < p.On)) return Tell(peer, "That song cannot be read.");
        voice.Plays = plays;
        // a song of bots alone waits (for a human to sit down, or for the grace to run out): they only make up the field
        if (session.Voices.All(v => v.Plays != null) && session.Voices.Any(v => v.Peer > 0)) FinishHorn(session);
        return null;
    }

    /// <summary>A bot plays a voice of the song of the moment: sits down, and its blows are what its skill makes of the part.</summary>
    public string? ServeHornBot(long peer, double skill, Random rng)
    {
        if (peer >= 0) return "Not a bot.";
        if (ServeHornStart(peer, out var seat) is { } why) return why;
        var (session, _) = FindHornVoice(peer);
        return ServeHornSong(peer, AlphornRules.BotPlays(session!.Chart, seat!.Value.Part, skill, rng));
    }

    private (HornSession? Session, HornVoice? Voice) FindHornVoice(long peer)
    {
        foreach (var s in _horn)
            foreach (var v in s.Voices)
                if (v.Peer == peer) return (s, v);
        return (null, null);
    }

    private void DropHornVoice(long peer)
    {
        foreach (var s in _horn.ToArray())
            if (s.Voices.RemoveAll(v => v.Peer == peer) > 0 && s.Voices.Count == 0) _horn.Remove(s);
    }

    /// <summary>Every 2 s: a song whose voices did not all send their blows is paid for those that did, once the grace has run out.</summary>
    private void HornTick(double now)
    {
        foreach (var s in _horn.ToArray())
        {
            double last = s.Voices.Max(v => v.JoinedAt);
            if (now <= last + s.Chart.Seconds + HornGraceSeconds) continue;
            FinishHorn(s);
        }
    }

    /// <summary>Grades every voice that played, scores the harmony between them, pays the humans and tells each its result.</summary>
    private void FinishHorn(HornSession s)
    {
        _horn.Remove(s);
        var played = s.Voices.Where(v => v.Plays != null).ToList();
        if (played.Count == 0) return;
        var scores = played.Select(v => AlphornRules.Grade(s.Chart, v.Part, v.Plays!)).ToList();
        double harmony = AlphornRules.Harmony(s.Chart, scores.Select(x => (IReadOnlyList<HornPlay>)x.Sounded).ToList());
        for (int i = 0; i < played.Count; i++)
        {
            var v = played[i];
            var sc = scores[i];
            int batzen = AlphornRules.Batzen(sc, harmony), points = AlphornRules.BoardPoints(sc, harmony);
            int granted = v.Peer > 0 ? Award(v.Peer, s.Festival, batzen, $"played the alphorn game ({sc.Points}/{sc.Max}, harmony x{harmony:F2})", HornGround.GameId, points) : 0;
            var result = new HornResult(v.Part, sc.Points, sc.Max, (int)Math.Round(harmony * 100), played.Count, granted, v.Peer > 0 ? points : 0);
            HornPaid.Add((v.Peer, result));
            GD.Print($"[festival] alphorn: peer {v.Peer} ({v.Part}) {sc.Points}/{sc.Max}, harmony x{harmony:F2} with {played.Count}: +{granted} Batzen");
            NoteHornResult(v.Peer, result);
        }
    }

    // ---- the wire ---------------------------------------------------------------------------------

    private void NoteHornSeat(long peer, HornSeat seat)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellHornSeat, seat.Seed, (int)seat.Part, seat.Voices);
        else if (!_networked && peer > 0) TellHornSeat(seat.Seed, (int)seat.Part, seat.Voices);
    }

    private void NoteHornResult(long peer, HornResult r)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellHornResult, (int)r.Part, r.Points, r.Max, r.Harmony100, r.Voices, r.Batzen, r.BoardPoints);
        else if (!_networked && peer > 0) TellHornResult((int)r.Part, r.Points, r.Max, r.Harmony100, r.Voices, r.Batzen, r.BoardPoints);
    }

    /// <summary>Client: asks for a seat at the row (offline: the local server's).</summary>
    public void AskHornStart()
    {
        if (Authority) ServeHornStart(AdventureService.LocalPeer, out _);
        else RpcId(1, MethodName.RequestHornStart);
    }

    /// <summary>Client: sends the blows of the song just played.</summary>
    public void AskHornSong(HornPlay[] plays)
    {
        if (Authority) ServeHornSong(AdventureService.LocalPeer, plays);
        else RpcId(1, MethodName.RequestHornSong, HornWire.Encode(plays));
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestHornStart()
    {
        if (_server) ServeHornStart(Multiplayer.GetRemoteSenderId(), out _);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestHornSong(string plays)
    {
        if (!_server) return;
        long peer = Multiplayer.GetRemoteSenderId();
        var decoded = HornWire.Decode(plays);
        if (decoded == null) Refuse(peer, "That song cannot be read.");
        else ServeHornSong(peer, decoded);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellHornSeat(int seed, int part, int voices) => HornSeated?.Invoke(new HornSeat(seed, (HornPart)Math.Clamp(part, 0, 3), voices));

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellHornResult(int part, int points, int max, int harmony100, int voices, int batzen, int boardPoints) =>
        HornGraded?.Invoke(new HornResult((HornPart)Math.Clamp(part, 0, 3), points, max, harmony100, voices, batzen, boardPoints));
}
