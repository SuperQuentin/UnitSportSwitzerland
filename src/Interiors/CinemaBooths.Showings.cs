using System.Text.Json;
using Godot;
using UnitSport.Net;

namespace UnitSport.Interiors;

/// <summary>
/// What a hall shows when nobody chose (#1298, the owner's): a hall the cinema starts with mid-showing
/// (<see cref="Default"/>: screen on, lights off) plays a film drawn from its own seed and showing number among the open
/// films (<see cref="CinemaVideo.OpenFilms"/>) and the server's own list (<c>/cinema pool</c>), the cat slides one entry
/// among them; never a player's file or share. The film loops on the shared clock (<see cref="CinemaVideo.LoopStart"/>,
/// from the hall's own phase), so every peer, a joiner included, shows the same frame with nothing sent but the server's
/// list. Anyone choosing in the panel (a video, the slides, the screen off) replaces it for good.
///
/// <para>
/// The server's list: <c>user://cinema_pool.json</c> on the server, sent to every client on join and on change
/// (<see cref="SetPool"/>). <c>/cinema pool</c> lists it; an admin (offline: the player) adds a web link with
/// <c>/cinema pool add &lt;link&gt;</c> (the server looks up its title and length; not a live stream: a film loops) and
/// removes one with <c>/cinema pool remove &lt;n&gt;</c>. Append and remove shift the picks: halls already showing may
/// change film, as a new programme would.
/// </para>
/// </summary>
public partial class CinemaBooths
{
    private const string PoolFile = "user://cinema_pool.json";

    /// <summary>The server's own films (on a client, the copy it was sent).</summary>
    private readonly List<CinemaVideo.Film> _pool = new();
    private int _poolVersion;

    /// <summary>Per cinema, per hall: the code and list the pick was drawn for, the pick (-1 the slides), the loop's phase.</summary>
    private readonly Dictionary<string, (int Code, int Version, int Pick, double Phase)[]> _showings = new();

    private int FilmCount => CinemaVideo.OpenFilms.Length + _pool.Count;

    private CinemaVideo.Film FilmAt(int i) => i < CinemaVideo.OpenFilms.Length ? CinemaVideo.OpenFilms[i] : _pool[i - CinemaVideo.OpenFilms.Length];

    /// <summary>
    /// The film a hall nobody chose for shows now, and its loop's start on the shared clock; ("", 0) for the slides, the
    /// screen off, or no such hall. Drawn once per showing and list (strings only then): per frame it is arithmetic.
    /// </summary>
    private (string Link, double T0) ShowingOf(string plan, int hall)
    {
        if (hall < 0 || hall >= MaxHalls || plan.Length == 0) return ("", 0);
        int code = HallsOf(plan)[hall];
        if (!Unpack(code).Screen) return ("", 0);
        if (!_showings.TryGetValue(plan, out var halls))
        {
            _showings[plan] = halls = new (int, int, int, double)[MaxHalls];
            for (int h = 0; h < MaxHalls; h++) halls[h].Code = -1;
        }
        ref var s = ref halls[hall];
        if (s.Code != code || s.Version != _poolVersion)
        {
            s.Code = code;
            s.Version = _poolVersion;
            s.Pick = CinemaVideo.PickShowing((uint)Core.Fnv.Hash($"{plan}#{hall}#{ReelOf(code)}#film"), FilmCount);
            s.Phase = Core.Fnv.Unit($"{plan}#{hall}#{ReelOf(code)}#phase");
        }
        if (s.Pick < 0 || s.Pick >= FilmCount) return ("", 0);
        var film = FilmAt(s.Pick);
        return (film.Link, CinemaVideo.LoopStart(ClockSync.ServerNow, film.Seconds, s.Phase * film.Seconds));
    }

    /// <summary>The title of a showing's film, for the panel; "" when the link is no film of the list.</summary>
    public string FilmTitle(string link)
    {
        for (int i = 0; i < FilmCount; i++)
            if (FilmAt(i).Link == link) return FilmAt(i).Title;
        return "";
    }

    // ---- the server's list -----------------------------------------------------------------------

    /// <summary>The server's saved list, read once at start (a client's is replaced by its server's on join).</summary>
    private void LoadPool()
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(PoolFile);
            if (!File.Exists(path)) return;
            var films = JsonSerializer.Deserialize<List<CinemaVideo.Film>>(File.ReadAllText(path));
            if (films == null) return;
            _pool.Clear();
            _pool.AddRange(films.Where(f => CinemaVideo.Allowed(f.Link) && f.Seconds > 0));
            _poolVersion++;
            GD.Print($"[cinema] {_pool.Count} film(s) of this server's own");
        }
        catch (Exception e) { GD.PushWarning($"[cinema] {PoolFile} unreadable: {e.Message}"); }
    }

    private void SavePool()
    {
        try { Core.JsonStore.Save(PoolFile, _pool, Core.JsonStore.Indented); }
        catch (Exception e) { GD.PushWarning($"[cinema] {PoolFile} not saved: {e.Message}"); }
    }

    /// <summary>Server: the list to <paramref name="peer"/> (0: everyone).</summary>
    private void SendPool(long peer = 0)
    {
        if (!Online) return;
        var titles = _pool.Select(f => f.Title).ToArray();
        var links = _pool.Select(f => f.Link).ToArray();
        var seconds = _pool.Select(f => f.Seconds).ToArray();
        if (peer == 0) Rpc(MethodName.SetPool, titles, links, seconds);
        else RpcId(peer, MethodName.SetPool, titles, links, seconds);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetPool(string[] titles, string[] links, double[] seconds)
    {
        _pool.Clear();
        for (int i = 0; i < Math.Min(links.Length, Math.Min(titles.Length, seconds.Length)) && i < 200; i++)
            if (CinemaVideo.Allowed(links[i]) && seconds[i] > 0) _pool.Add(new CinemaVideo.Film(titles[i], links[i], seconds[i]));
        _poolVersion++;
    }

    /// <summary>
    /// Server (and offline): <c>/cinema pool [add &lt;link&gt; | remove &lt;n&gt;]</c> from <paramref name="peer"/>; the answer now,
    /// and <paramref name="later"/> once a link's title and length are looked up (on a worker).
    /// </summary>
    public string PoolCommand(long peer, string args, bool admin, Action<string> later)
    {
        var parts = args.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        if (verb is "" or "list")
        {
            var lines = new List<string> { "Films a cinema can be showing (the cat slides one more):" };
            for (int i = 0; i < FilmCount; i++)
            {
                var f = FilmAt(i);
                bool own = i >= CinemaVideo.OpenFilms.Length;
                lines.Add($"{(own ? $"{i - CinemaVideo.OpenFilms.Length + 1}." : " -")} {f.Title} ({(int)(f.Seconds / 60)} min){(own ? $"  {f.Link}" : "")}");
            }
            if (admin) lines.Add("/cinema pool add <link>, /cinema pool remove <n>");
            return string.Join('\n', lines);
        }
        if (!admin) return "Only an admin changes this server's films.";
        if (verb == "remove" && parts.Length > 2 && int.TryParse(parts[2], out int n) && n >= 1 && n <= _pool.Count)
        {
            var gone = _pool[n - 1];
            _pool.RemoveAt(n - 1);
            _poolVersion++;
            SavePool();
            SendPool();
            return $"Removed {gone.Title}.";
        }
        if (verb == "add" && parts.Length > 2)
        {
            string link = parts[2];
            if (!CinemaVideo.Allowed(link)) return "That is not a web link (https://…).";
            if (_pool.Any(f => f.Link == link)) return "That film is in the list already.";
            if (_pool.Count >= 200) return "The list is full (200 films).";
            Task.Run(() =>
            {
                var found = CinemaLookup.Probe(link, out string why);
                Callable.From(() =>
                {
                    if (found is not { } f) { later($"Not added: {why}"); return; }
                    if (_pool.Any(x => x.Link == link)) return;
                    _pool.Add(f);
                    _poolVersion++;
                    SavePool();
                    SendPool();
                    later($"Added {f.Title} ({(int)(f.Seconds / 60)} min): cinemas may be showing it now.");
                }).CallDeferred();
            });
            return $"Looking up {CinemaVideo.Title(link)}…";
        }
        return "Usage: /cinema pool  |  /cinema pool add <link>  |  /cinema pool remove <n>";
    }
}
