using System.Text;
using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Net;
using UnitSport.Terrain.Stadiums;

namespace UnitSport.Concert;

/// <summary>
/// The setlist by hand (#994): <c>/concert song</c> says what plays and what comes next, lists the
/// set, skips, goes back, plays a song by number or title, stops and starts the PA. A cue is a song of
/// the setlist and the shared-clock instant it started (<see cref="SongCue"/>): the server keeps one per
/// venue and tells every peer and late joiner, and every peer plays the set on from it
/// (<see cref="ConcertMusic.Position"/>), so the song and the second stay the same everywhere.
/// The show's operator moves a hosted set; an admin tonight's show; offline, the player.
/// </summary>
public partial class ConcertService
{
    private readonly Dictionary<int, SongCue> _songs = new();

    /// <summary>The song cue in force at a venue, or null: its set runs on the clock.</summary>
    public SongCue? SongCueOf(int stadium) => _songs.TryGetValue(stadium, out var c) ? c : null;

    /// <summary>The setlist a venue plays today, as this peer's CD library makes it (the server's, mirrored).</summary>
    private static CdInfo[] SetlistAt(int stadium) =>
        ConcertDirector.EventFor(StadiumCatalog.All[stadium], stadium) is { } e && CdLibrary.Instance is { } lib
            ? ConcertMusic.SetlistOf(e, lib.All.Values)
            : Array.Empty<CdInfo>();

    private const string SongUsage = "/concert song [list | next | prev | restart | <n | title> | stop | play | auto]";

    /// <summary>Server (and offline): <c>/concert song ...</c> at the venue nearest the sender; returns the reply.</summary>
    private string SongCommand(long sender, int stadium, string[] args, bool admin)
    {
        var venue = StadiumCatalog.All[stadium];
        if (ConcertDirector.EventFor(venue, stadium) is not { } e) return "Concerts are off on this server.";
        var set = SetlistAt(stadium);
        if (set.Length == 0) return $"{venue.Name}: no CDs to play, the desk makes its songs up. Upload some from the phone's CD app.";

        string verb = args.Length == 0 ? "" : args[0].ToLowerInvariant();
        double now = ClockSync.ServerNow;
        var cue = SongCueOf(stadium);
        var (index, into, gap) = ConcertMusic.At(set, ConcertMusic.Position(set, now, cue));
        if (verb is "" or "status" or "now") return NowPlaying(venue, e, set, cue, index, into, gap);
        if (verb == "list") return SetlistText(venue, set, cue is { Off: true } ? -1 : index);

        if (_hosted.TryGetValue(stadium, out var h) && h.Seed >= 0 ? h.Operator != sender && !admin : !admin)
            return "Only the show's operator or an admin changes the songs ('/concert host' takes a show).";

        SongCue next;
        switch (verb)
        {
            case "next" or "skip": next = new SongCue((index + 1) % set.Length, now, false); break;
            // back to the start of this song when it has played a while, as a player's previous button
            case "prev" or "previous" or "back": next = new SongCue(!gap && into > 5 ? index : (index + set.Length - 1) % set.Length, now, false); break;
            case "restart": next = new SongCue(index, now, false); break;
            case "stop" or "off": next = new SongCue(index, now, true); break;
            case "play" or "on": next = new SongCue(index, now, false); break;
            case "auto":
                SetSongs(stadium, null);
                return $"{venue.Name}: the set runs on the clock again.";
            default:
                int pick = FindSong(set, string.Join(' ', args));
                if (pick < 0) return $"No song '{string.Join(' ', args)}' in the set. {SongUsage}";
                next = new SongCue(pick, now, false);
                break;
        }
        SetSongs(stadium, next);
        string live = ShowLive(stadium, e) ? "" : $" (the PA plays {Clock(e.StartHour)}-{Clock(e.EndHour)}, or on a hosted set)";
        return next.Off
            ? $"{venue.Name}: the PA stops; the lights run on their own. '/concert song play' starts it again."
            : $"{venue.Name}: now {next.Index + 1}/{set.Length} \"{set[next.Index].Title}\"{live}.";
    }

    /// <summary>The song a typed word names: its number in the set, else the title holding every word (case and accents ignored); -1 none.</summary>
    private static int FindSong(CdInfo[] set, string query)
    {
        string q = query.Trim().TrimStart('#');
        if (int.TryParse(q, out int n)) return n >= 1 && n <= set.Length ? n - 1 : -1;
        string[] words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(StadiumLookup.Fold).Where(w => w.Length > 0).ToArray();
        if (words.Length == 0) return -1;
        for (int i = 0; i < set.Length; i++)
            if (words.All(StadiumLookup.Fold(set[i].Title).Contains)) return i;
        return -1;
    }

    private string NowPlaying(StadiumSpec venue, ConcertEvent e, CdInfo[] set, SongCue? cue, int index, double into, bool gap)
    {
        string by = cue is null ? "on the clock" : "moved by hand";
        if (cue is { Off: true }) return $"{venue.Name}: the PA is stopped; '/concert song play' starts it.";
        string when = ShowLive(Array.IndexOf(StadiumCatalog.All, venue), e) ? "" : $"; the PA plays {Clock(e.StartHour)}-{Clock(e.EndHour)}";
        var song = set[index];
        string now = gap
            ? $"between songs, after {index + 1}/{set.Length} \"{song.Title}\""
            : $"{index + 1}/{set.Length} \"{song.Title}\" {Time(into)} of {Time(song.Duration)}";
        int after = (index + 1) % set.Length;
        return $"{venue.Name}, {e.Artist}: {now}; next \"{set[after].Title}\" (set {by}{when}).";
    }

    private static string SetlistText(StadiumSpec venue, CdInfo[] set, int playing)
    {
        var sb = new StringBuilder($"{venue.Name} setlist:");
        for (int i = 0; i < set.Length; i++)
            sb.Append(i == playing ? $"  >{i + 1} {set[i].Title} ({Time(set[i].Duration)})" : $"  {i + 1} {set[i].Title} ({Time(set[i].Duration)})");
        return sb.ToString();
    }

    /// <summary>Whether the PA plays now: in the show's hours, or on a hosted set at any hour.</summary>
    private bool ShowLive(int stadium, ConcertEvent e)
    {
        double hour = World.WorldClock.CurrentHour;
        return SeedFor(stadium) != null || (hour >= e.StartHour && hour < e.EndHour);
    }

    private static string Time(double s) => $"{(int)s / 60}:{(int)s % 60:00}";
    private static string Clock(float hour) => $"{(int)hour:00}:{(int)Math.Round(hour % 1 * 60):00}";

    /// <summary>Server (and offline): a venue's song cue changed, told to everyone.</summary>
    private void SetSongs(int stadium, SongCue? cue)
    {
        var c = cue ?? new SongCue(-1, 0, false);
        foreach (int peer in Peers()) RpcId(peer, MethodName.SongState, stadium, c.Index, c.At, c.Off);
        SongState(stadium, c.Index, c.At, c.Off);
    }

    /// <summary>Server: the song cues a joining player must know.</summary>
    private void SendSongsTo(long peer)
    {
        foreach (var (stadium, c) in _songs) RpcId(peer, MethodName.SongState, stadium, c.Index, c.At, c.Off);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SongState(int stadium, int index, double at, bool off)
    {
        if (stadium < 0 || stadium >= StadiumCatalog.All.Length) return;
        if (index < 0) _songs.Remove(stadium);
        else _songs[stadium] = new SongCue(index, at, off);
        if (RigOf(stadium) is { } rig) rig.Music.Cue = SongCueOf(stadium);
    }
}
