using System.Text.Json;
using Godot;
using UnitSport.Adventure;
using UnitSport.Core;

namespace UnitSport.Occasions.Festivals;

// The wire of the festival service (protocol 59): the server tells every client what runs, each member its crew's standing, the
// owner what was converted; clients ask for the check-only festival's bell and gong. Sections travel by the adventure
// service's own SectionSync.
public partial class FestivalService
{
    // ---- the check-only festival's game (--festival test) -----------------------------------------

    private readonly Dictionary<(long Peer, string Game), int> _rings = new();
    private readonly Dictionary<long, (long Second, int Count)> _rate = new();

    /// <summary>
    /// Server (and a bot's way in): <paramref name="peer"/> rang the bell or the gong once. Three rings are a play, paid by
    /// <see cref="FestivalCatalog.TestPay"/> through <see cref="Award"/>. Needs the check-only festival, an open game and
    /// the body at the site. Returns null when the ring counted, else why not.
    /// </summary>
    public string? ServeRing(long peer, string game)
    {
        if (!Authority) return "Not here.";
        if (!FestivalCatalog.TestRequested) return Tell(peer, "There is nothing to ring.");
        if (RunningOf(FestivalCatalog.TestId) is not { } run || FestivalCatalog.Get(FestivalCatalog.TestId) is not { } def)
            return Tell(peer, "The test festival is not on.");
        if (def.Game(game) == null) return Tell(peer, "There is no such thing to ring.");
        if (!def.IsOpen(game, run.Day)) return Tell(peer, $"The {game} opens on day {def.OpensOn(game)}.");
        if (run.Site is not { } site || _adventure.PositionOf?.Invoke(peer) is not { } at
            || site.DistanceTo(at.E, at.N) > site.RadiusM + 10)
            return Tell(peer, "Go to the festival ground to ring it.");
        if (!_adventure.Active(peer)) return Tell(peer, "No adventuring now.");
        if (Connected(peer) && !Allowed(peer)) return "too fast";

        int rings = _rings.GetValueOrDefault((peer, game)) + 1;
        if (rings < FestivalCatalog.Rings)
        {
            _rings[(peer, game)] = rings;
            NoteRing(peer, game, rings, 0);
            return null;
        }
        _rings.Remove((peer, game));
        var (batzen, points) = FestivalCatalog.TestPay(game);
        int granted = Award(peer, def.Id, batzen, $"rang the {game}", game, points);
        NoteRing(peer, game, 0, granted);
        return null;
    }

    private string Tell(long peer, string why)
    {
        Refuse(peer, why);
        return why;
    }

    /// <summary>10 requests a second a peer at most (a remote one: the local player offline has no wire to flood).</summary>
    private bool Allowed(long peer)
    {
        long second = (long)GameClock.Now;
        var (s, n) = _rate.GetValueOrDefault(peer);
        n = s == second ? n + 1 : 1;
        _rate[peer] = (second, n);
        return n <= 10;
    }

    private void NoteRing(long peer, string game, int count, int batzen)
    {
        if (Connected(peer)) RpcId(peer, MethodName.TellRang, game, count, batzen);
        else if (!_networked) TellRang(game, count, batzen);
    }

    // ---- the owner's requests ---------------------------------------------------------------------

    /// <summary>Client: rings the bell or the gong of the check-only festival once.</summary>
    public void AskRing(string game)
    {
        if (Authority) ServeRing(AdventureService.LocalPeer, game);
        else RpcId(1, MethodName.RequestRing, game);
    }

    /// <summary>Checks only, on a server started with <c>--festival test</c>: reads the calendar on another day (the festival's end).</summary>
    public void AskTestDate(DateOnly date)
    {
        if (!FestivalCatalog.TestRequested) return;
        if (Authority) OccasionManager.Instance?.OverrideDate(date);
        else RpcId(1, MethodName.RequestTestDate, date.ToString("yyyy-MM-dd"));
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestRing(string game)
    {
        if (_server) ServeRing(Multiplayer.GetRemoteSenderId(), game);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestTestDate(string date)
    {
        if (_server && FestivalCatalog.TestRequested
            && DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
            OccasionManager.Instance?.OverrideDate(d);
    }

    // ---- server -> owner --------------------------------------------------------------------------

    /// <summary>Client: a ring of the check-only festival counted: the game, the rings so far (0 once a play ended), the Batzen that play granted.</summary>
    public event Action<string, int, int>? Rang;

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellRang(string game, int count, int batzen) => Rang?.Invoke(game, count, batzen);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellRefused(string why)
    {
        GD.Print($"[festival] refused: {why}");
        Refused?.Invoke(why);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellConverted(string festival, int batzen, int chf)
    {
        GD.Print($"[festival] {festival}: {batzen} Batzen paid as CHF {chf}");
        Converted?.Invoke(festival, batzen, chf);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellFestivals(string json)
    {
        List<RunningFestival> list;
        try { list = JsonSerializer.Deserialize<List<RunningFestival>>(json, _json) ?? new(); }
        catch (JsonException e)
        {
            GD.PushWarning($"[festival] unreadable list from the server: {e.Message}");
            return;
        }
        bool dress = false;
        foreach (var gone in Running)
            if (list.All(r => r.Id != gone.Id)) dress |= FestivalOccasion.SetSite(gone.Id, null);
        foreach (var r in list) dress |= FestivalOccasion.SetSite(r.Id, r.Site);
        Running = list;
        GD.Print($"[festival] server runs: {(list.Count == 0 ? "none" : string.Join(", ", list.Select(r => $"{r.Id} day {r.Day}/{r.Length}{(r.Site == null ? "" : " at " + r.Site.Name)}")))}");
        Changed?.Invoke();
        if (dress) OccasionManager.NotifyContentChanged();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellBoard(string json)
    {
        try
        {
            if (JsonSerializer.Deserialize<BoardView>(json, _json) is not { } view) return;
            _views[view.Festival] = view;
            BoardChanged?.Invoke(view.Festival);
        }
        catch (JsonException e) { GD.PushWarning($"[festival] unreadable board from the server: {e.Message}"); }
    }
}
