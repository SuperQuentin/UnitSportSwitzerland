using System.Text.Json;
using Godot;
using UnitSport.Net;
using UnitSport.Terrain.Format;

namespace UnitSport.BattleRoyale;

/// <summary>
/// The Battle Royale mode (#177), at <c>World/BattleRoyale</c> on the server and every client
/// (RPCs route by path). The server owns the match: who is in, the region, the seed, the clock,
/// who is still alive; it sends the whole <see cref="BrState"/> whenever that changes. Each client
/// derives the zone from it and applies the zone's damage to its own player, as it does every
/// other damage (health is the owner's). Docs: <c>docs/notes/br/match.md</c>.
/// <para>
/// Lifecycle: <c>/br open</c> (admin) picks a region and opens the lobby; players <c>/br join</c>;
/// the countdown starts with enough of them (or <c>/br start</c>); at GO everyone is dropped in the
/// region with an empty pack; the last one alive wins; 20 s of results; everyone goes back where they
/// were, with their own inventory.
/// </para>
/// </summary>
public partial class BrManager : Node
{
    public const string NodeName = "BattleRoyale";
    public const int MinPlayers = 2;
    /// <summary>The lobby's countdown once <see cref="MinPlayers"/> have joined; <c>/br start</c> shortens it.</summary>
    public const double LobbyWait = 60, StartWait = 10;
    public const double ResultsSeconds = 20;
    private const string HistoryFile = "user://br/history.json";

    private bool _server;
    private BrState _state = new();
    public BrState State => _state;

    // ---- server ----------------------------------------------------------------------------
    private ChatManager? _chat;
    private Node3D? _players;
    private IReadOnlyList<Place> _places = Array.Empty<Place>();
    private IReadOnlyList<ManifestTile> _tiles = Array.Empty<ManifestTile>();
    private (double E, double N) _fallback;
    private double _endedAt;
    private readonly Random _rng = new();

    // ---- server: the match's loot (#194) ----
    private Terrain.IChunkSource? _source;
    private BrCrates? _crates;
    private readonly HashSet<int> _dropped = new();
    private int _match;

    public static BrManager CreateServer(ChatManager chat, Node3D players, IReadOnlyList<Place> places,
        IReadOnlyList<ManifestTile> tiles, (double E, double N) fallback, Terrain.IChunkSource? source = null, BrCrates? crates = null)
    {
        var m = new BrManager
        {
            Name = NodeName, _server = true, _chat = chat, _players = players, _places = places, _tiles = tiles, _fallback = fallback,
            _source = source, _crates = crates,
        };
        // the living may loot, the winner during the results too
        if (crates != null) crates.MayLoot = peer => m._state.Running && m._state.Find(peer) is { Alive: true };
        Combat.PvpRules.HitRelayed += m.OnHit;
        return m;
    }

    private static double Now => ClockSync.ServerNow;

    /// <summary>"--brpace f" on the server: every match's timings times f (the loopback check runs a match in a minute).</summary>
    private static readonly float PaceScale = ParsePace();

    private static float ParsePace()
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--brpace");
        return i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float f) && f > 0 ? f : 1f;
    }

    /// <summary>
    /// <c>/br ...</c> from <paramref name="sender"/>. Returns the private reply; public news goes
    /// out as system chat.
    /// </summary>
    public string Command(long sender, string args, bool admin)
    {
        var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = words.Length > 0 ? words[0].ToLowerInvariant() : "status";
        bool player = sender != ChatManager.ConsolePeerId;
        switch (verb)
        {
            case "status": return Status();
            case "join":
                if (!player) return "'/br join' needs a player.";
                return Join(sender);
            case "leave":
                if (!player) return "'/br leave' needs a player.";
                return Leave(sender);
        }
        if (!admin) return $"'/br {verb}' is an admin command.";
        switch (verb)
        {
            case "open": return Open(sender, words[1..]);
            case "start":
                if (_state.Phase is not (BrPhase.Lobby or BrPhase.Countdown)) return "No lobby is open.";
                if (_state.Entrants.Count < 1) return "Nobody has joined.";
                _state.CountdownEnds = _state.Phase == BrPhase.Countdown ? Math.Min(_state.CountdownEnds, Now + StartWait) : Now + StartWait;
                _state.Phase = BrPhase.Countdown;
                Broadcast($"Battle Royale starts in {StartWait:F0} s!");
                Push();
                return "Starting.";
            case "cancel":
                if (_state.Phase == BrPhase.Idle) return "No match.";
                Broadcast("The Battle Royale was cancelled.");
                Finish();
                return "Cancelled.";
            default:
                return "Usage: /br open [town|here] [5|6|7] [short|normal|long] · join · leave · start · cancel · status";
        }
    }

    private string Status()
    {
        var s = _state;
        return s.Phase switch
        {
            BrPhase.Idle => "No Battle Royale. An admin opens one with /br open.",
            BrPhase.Lobby or BrPhase.Countdown =>
                $"Lobby: {s.AreaName}, {s.Side / 1000:F0} × {s.Side / 1000:F0} km. {s.Entrants.Count} joined: "
                + string.Join(", ", s.Entrants.Select(e => e.Name)) + ". /br join to play.",
            _ => $"Match in {s.AreaName}: {s.AliveCount} of {s.Entrants.Count} alive.",
        };
    }

    private string Open(long sender, string[] words)
    {
        if (_state.Phase != BrPhase.Idle) return "A match is already open (/br cancel first).";
        float? side = null;
        float pace = 1f;
        var place = new List<string>();
        foreach (var w in words)
        {
            switch (w.ToLowerInvariant())
            {
                case "short": pace = 0.8f; continue;
                case "normal": pace = 1f; continue;
                case "long": pace = 1.3f; continue;
                case "5" or "6" or "7": side = (w[0] - '0') * 1000f; continue;
                case "auto" or "random": continue;
            }
            place.Add(w);
        }

        pace *= PaceScale;
        int seed = _rng.Next(1, int.MaxValue);
        // the side follows the field, which is not known yet: sized for the players online now
        float size = side ?? BrRegion.SideFor(Multiplayer.GetPeers().Length);
        BrArea area;
        string query = string.Join(' ', place);
        if (query.Equals("here", StringComparison.OrdinalIgnoreCase))
        {
            if (_players?.GetNodeOrNull<Node3D>(sender.ToString()) is not { } body || Origin == null) return "'/br open here' needs a player.";
            var (e, n) = Origin.ToLv95(body.GlobalPosition);
            area = new BrArea(Math.Round(e / 50) * 50, Math.Round(n / 50) * 50, size, NearestTown(e, n) ?? "here");
        }
        else if (query.Length > 0)
        {
            var hit = new PlaceIndex { Places = _places.ToList() }.Search(query, 1);
            if (hit.Count == 0) return $"No town matching '{query}'.";
            area = new BrArea(hit[0].E, hit[0].N, size, hit[0].Name);
        }
        else area = BrRegion.Pick(seed, size, _places, _tiles, LoadHistory(), _fallback);

        _state = new BrState
        {
            Phase = BrPhase.Lobby, AreaE = area.E, AreaN = area.N, Side = area.Side, AreaName = area.Name, Seed = seed, Pace = pace,
        };
        double minutes = new ZoneSchedule(seed, area.Side, pace).Duration / 60.0;
        GD.Print(FormattableString.Invariant($"[br] lobby open: {area.Name} at {area.E:F0}/{area.N:F0}, {area.Side:F0} m, pace {pace}, zone {minutes:F0} min"));
        Broadcast($"Battle Royale lobby open: {area.Name} ({area.Side / 1000:F0} × {area.Side / 1000:F0} km, about {minutes + 2:F0} min). Type /br join to play!");
        Push();
        return "Lobby open.";
    }

    private string? NearestTown(double e, double n) =>
        _places.Where(p => p.Kind == PlaceKind.Town).MinBy(p => (p.E - e) * (p.E - e) + (p.N - n) * (p.N - n))?.Name;

    /// <summary>Server: the world origin, for <c>/br open here</c>.</summary>
    public Core.WorldOrigin? Origin { get; set; }

    private string Join(long peer)
    {
        if (_state.Phase is not (BrPhase.Lobby or BrPhase.Countdown)) return _state.Running ? "A match is running; wait for the next lobby." : "No lobby is open.";
        if (_state.Find(peer) != null) return "You are already in.";
        _state.Entrants.Add(new BrEntrant { Peer = peer, Name = _chat?.NameOfPeer(peer) ?? peer.ToString() });
        Broadcast($"{_chat?.NameOfPeer(peer)} joined the Battle Royale ({_state.Entrants.Count}).");
        if (_state.Phase == BrPhase.Lobby && _state.Entrants.Count >= MinPlayers)
        {
            _state.Phase = BrPhase.Countdown;
            _state.CountdownEnds = Now + LobbyWait;
            Broadcast($"Battle Royale starts in {LobbyWait:F0} s. Last call: /br join");
        }
        Push();
        return $"You are in. {_state.AreaName}, {_state.Side / 1000:F0} km.";
    }

    private string Leave(long peer)
    {
        if (_state.Find(peer) is not { } e) return "You are not in the match.";
        if (_state.Running)
        {
            if (e.Alive) Eliminate(e, 0, BrOut.Other);
            return "You left the match.";
        }
        _state.Entrants.Remove(e);
        Push();
        return "You left the lobby.";
    }

    public override void _Process(double delta)
    {
        if (_server) ServerTick();
        else ClientTick(delta);
    }

    private void ServerTick()
    {
        switch (_state.Phase)
        {
            case BrPhase.Countdown when Now >= _state.CountdownEnds:
                Go();
                break;
            case BrPhase.Playing:
                // the zone has closed and someone is still standing (a draw cannot linger)
                var zone = new ZoneSchedule(_state.Seed, _state.Side, _state.Pace);
                Airdrops(zone);
                if (_state.AliveCount <= 1 || Now - _state.Started > zone.Duration + 120) End();
                break;
            case BrPhase.Ended when Now - _endedAt >= ResultsSeconds:
                Finish();
                break;
        }
    }

    private void Go()
    {
        // those who left since joining are not in it
        _state.Entrants.RemoveAll(e => !Multiplayer.GetPeers().Contains((int)e.Peer));
        if (_state.Entrants.Count == 0)
        {
            Broadcast("Nobody is left in the lobby: the Battle Royale is called off.");
            Finish();
            return;
        }
        _state.Phase = BrPhase.Playing;
        _state.Started = Now;
        Combat.PvpRules.Override = Allowed;
        SetMatchLoot(_state);
        SpawnLoot(_state.Area, _state.Seed);
        // a ground drop until the cargo plane (part 5): spread over the first circle, seeded
        var zone = new ZoneSchedule(_state.Seed, _state.Side, _state.Pace);
        var rng = new Random(_state.Seed ^ 0x5bd1e995);
        float r = Math.Min(zone.RadiusOf(0), _state.Side * 0.5f) * 0.85f;
        Push();
        foreach (var e in _state.Entrants)
        {
            float d = r * MathF.Sqrt((float)rng.NextDouble()), a = (float)(rng.NextDouble() * Math.Tau);
            RpcId(e.Peer, MethodName.Drop, _state.AreaE + d * MathF.Cos(a), _state.AreaN + d * MathF.Sin(a));
        }
        Broadcast($"GO! {_state.Entrants.Count} players in {_state.AreaName}. The zone shows in {ZoneSchedule.LootSeconds * zone.Scale / 60:F0} min.");
        GD.Print($"[br] go: {_state.Entrants.Count} players");
    }

    /// <summary>PvP in a match: between two living entrants; whoever is not in the match leaves it alone.</summary>
    private bool? Allowed(long shooter, long victim)
    {
        var a = _state.Find(shooter);
        var b = _state.Find(victim);
        if (a == null && b == null) return null;
        return a is { Alive: true } && b is { Alive: true } && (a.Team == 0 || a.Team != b.Team);
    }

    private void OnHit(long shooter, long victim, float damage)
    {
        if (_state.Phase == BrPhase.Playing && _state.Find(shooter) is { Alive: true } e) e.Damage += damage;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReportDeath(long killer, int cause, int[] ids, int[] counts)
    {
        if (!_server || _state.Phase != BrPhase.Playing) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (_state.Find(sender) is not { Alive: true } e) return;
        DeathBox(e, ids, counts);
        Eliminate(e, killer, (BrOut)Math.Clamp(cause, 0, (int)BrOut.Other));
    }

    // ------------------------------------------------------------------------------------
    // server: loot (#194)
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Buildings whose tile touches the region roll match loot, under the match's own epoch: they
    /// restock for every match, and free-roam loot (and its files) is left alone.
    /// </summary>
    public static void SetMatchLoot(BrState? s)
    {
        if (s == null || s.Phase is BrPhase.Idle or BrPhase.Lobby or BrPhase.Countdown)
        {
            Loot.LootTables.MatchEpoch = null;
            return;
        }
        double half = s.Side * 0.5, w = s.AreaE - half, e = s.AreaE + half, so = s.AreaN - half, no = s.AreaN + half;
        long epoch = 1_000_000_000L + s.Seed;
        Loot.LootTables.MatchEpoch = key =>
            Interiors.BuildingKey.TryParse(key, out var k)
            && k.TileE * 1000.0 < e && k.TileE * 1000.0 + 1000 > w && k.TileN * 1000.0 < no && k.TileN * 1000.0 + 1000 > so
                ? epoch : null;
    }

    private async void SpawnLoot(BrArea area, int seed)
    {
        int match = ++_match;
        _dropped.Clear();
        if (_source == null || Origin == null) return;
        try
        {
            var roads = await BrLoot.RoadPoints(_source, area);
            if (match != _match || _state.Phase != BrPhase.Playing) return;
            var crates = BrLoot.RoadsideCrates(roads, area, seed);
            _crates?.Spawn(crates);
            int vehicles = BrLoot.ParkVehicles(roads, area, seed, Origin);
            GD.Print($"[br] loot: {roads.Count} road points, {crates.Count} crates, {vehicles} vehicles");
        }
        catch (Exception ex) { GD.PushWarning($"[br] loot: {ex.Message}"); }
    }

    /// <summary>At the start of phases 2, 4 and 6 supply drops come down inside the next circle.</summary>
    private void Airdrops(ZoneSchedule zone)
    {
        var z = zone.At(Now - _state.Started);
        if (z.Shrinking || _dropped.Contains(z.Phase)) return;
        _dropped.Add(z.Phase);
        int n = BrLoot.DropsAt(z.Phase, _state.Side);
        if (n == 0 || _crates == null) return;
        var rng = new Random(_state.Seed + z.Phase * 7727);
        double fall = BrCrates.DropHeight / BrCrates.FallSpeed;
        var drops = Enumerable.Range(0, n).Select(_ => BrLoot.Airdrop(_state.Area, z, rng, Now, fall)).ToList();
        _crates.Spawn(drops);
        foreach (var d in drops)
            Broadcast($"A supply drop is coming down in {Cell(d.E - _state.AreaE, d.N - _state.AreaN)}!");
    }

    /// <summary>The full map's grid square of a zone point ("C4").</summary>
    private string Cell(double x, double y)
    {
        float half = _state.Side * 0.5f, step = _state.Side / Mathf.Ceil(_state.Side / 1000f);
        int col = (int)Math.Floor((x + half) / step), row = (int)Math.Floor((half - y) / step);
        return $"{(char)('A' + Math.Clamp(col, 0, 25))}{row + 1}";
    }

    /// <summary>Everything the player carried, in a box where they fell.</summary>
    private void DeathBox(BrEntrant e, int[] ids, int[] counts)
    {
        if (_crates == null || Origin == null || _players?.GetNodeOrNull<Node3D>(e.Peer.ToString()) is not { } body) return;
        var stacks = new List<Items.ItemStack>();
        for (int i = 0; i < Math.Min(Math.Min(ids.Length, counts.Length), Items.Inventory.Size); i++)
            if (Items.ItemDefs.Get((Items.ItemId)ids[i]) is { } def && def.Id != Items.ItemId.Francs && counts[i] > 0)
                stacks.Add(new Items.ItemStack(def.Id, Math.Min(counts[i], def.MaxStack)));
        if (stacks.Count == 0) return;
        var (pe, pn) = Origin.ToLv95(body.GlobalPosition);
        var box = new Crate { Style = CrateStyle.DeathBox, E = pe, N = pn, Alt = body.GlobalPosition.Y, Label = $"{e.Name}'s things" };
        box.SetStacks(stacks);
        _crates.Spawn(new[] { box });
    }

    /// <summary>The match's crates and vehicles go; its buildings go back to free-roam loot.</summary>
    private void ClearLoot()
    {
        _match++;
        _crates?.ClearAll();
        BrLoot.RemoveVehicles();
        Loot.LootTables.MatchEpoch = null;
        Loot.LootService.Instance?.ForgetMatch();
    }

    private void Eliminate(BrEntrant e, long killer, BrOut cause)
    {
        e.Alive = false;
        e.Survived = Now - _state.Started;
        e.Place = _state.AliveCount + 1;
        var k = killer != e.Peer ? _state.Find(killer) : null;
        if (k is { Alive: true }) k.Kills++;
        else k = null;
        string what = cause switch
        {
            BrOut.Killed when k != null => $"{k.Name} eliminated {e.Name}",
            BrOut.Zone => k != null ? $"{e.Name} died in the zone, running from {k.Name}" : $"{e.Name} died in the zone",
            BrOut.Fell => k != null ? $"{e.Name} fell, chased by {k.Name}" : $"{e.Name} fell to their death",
            BrOut.Disconnected => $"{e.Name} left the match",
            _ => k != null ? $"{k.Name} eliminated {e.Name}" : $"{e.Name} is out",
        };
        Broadcast($"{what}. {_state.AliveCount} left.");
        GD.Print($"[br] out: {e.Name} ({cause}), place {e.Place}, killer {k?.Name ?? "-"}");
        Rpc(MethodName.Eliminated, e.Peer, k?.Peer ?? 0, (int)cause, e.Place);
        Push();
        if (_state.AliveCount <= 1) End();
    }

    private void End()
    {
        if (_state.Phase != BrPhase.Playing) return;
        var winner = _state.Entrants.FirstOrDefault(e => e.Alive)
                     ?? _state.Entrants.OrderBy(e => e.Place).FirstOrDefault();
        foreach (var e in _state.Entrants.Where(e => e.Alive))
        {
            e.Survived = Now - _state.Started;
            e.Place = 1;
            e.Alive = e == winner;
        }
        if (winner != null) winner.Place = 1;
        _state.Winner = winner?.Peer ?? 0;
        _state.Phase = BrPhase.Ended;
        _endedAt = Now;
        Combat.PvpRules.Override = null;
        Broadcast(winner != null
            ? $"{winner.Name} WINS the Battle Royale in {_state.AreaName}! ({BrHud.Kills(winner.Kills)})"
            : "The Battle Royale ended without a winner.");
        GD.Print($"[br] ended, winner {winner?.Name ?? "-"}");
        SaveHistory();
        Push();
    }

    /// <summary>Everyone back where they were, with their own things; the server back to idle.</summary>
    private void Finish()
    {
        var was = _state;
        Combat.PvpRules.Override = null;
        ClearLoot();
        _state = new BrState();
        foreach (var e in was.Entrants)
            if (Multiplayer.GetPeers().Contains((int)e.Peer)) RpcId(e.Peer, MethodName.Release);
        Push();
    }

    /// <summary>Server: <paramref name="peer"/> is alive in a running match (no teleporting out of it).</summary>
    public bool Playing(long peer) => _state.Phase == BrPhase.Playing && _state.Find(peer) is { Alive: true };

    /// <summary>Server: a peer left the game. Out of the lobby, or out of the match.</summary>
    public void PeerLeft(long peer)
    {
        if (_state.Find(peer) is not { } e) return;
        if (_state.Phase == BrPhase.Playing)
        {
            if (e.Alive) Eliminate(e, 0, BrOut.Disconnected);
        }
        else if (!_state.Running)
        {
            _state.Entrants.Remove(e);
            Push();
        }
    }

    /// <summary>Server: a joining peer gets the state (it spectates a running match through it).</summary>
    public void SendTo(long peer) => RpcId(peer, MethodName.SetState, _state.ToJson());

    private void Push()
    {
        if (_server) Rpc(MethodName.SetState, _state.ToJson());
    }

    private void Broadcast(string line) => _chat?.Broadcast(line, ChatKind.System);

    private static List<(double E, double N)> LoadHistory()
    {
        try
        {
            if (!Godot.FileAccess.FileExists(HistoryFile)) return new();
            using var f = Godot.FileAccess.Open(HistoryFile, Godot.FileAccess.ModeFlags.Read);
            var list = JsonSerializer.Deserialize<List<double[]>>(f.GetAsText()) ?? new();
            return list.Where(p => p.Length == 2).Select(p => (p[0], p[1])).ToList();
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[br] {HistoryFile}: {ex.Message}");
            return new();
        }
    }

    private void SaveHistory()
    {
        var list = LoadHistory();
        list.Add((_state.AreaE, _state.AreaN));
        if (list.Count > 5) list.RemoveRange(0, list.Count - 5);
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath("user://br"));
        using var f = Godot.FileAccess.Open(HistoryFile, Godot.FileAccess.ModeFlags.Write);
        f?.StoreString(JsonSerializer.Serialize(list.Select(p => new[] { p.E, p.N })));
    }
}
