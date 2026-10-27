using Godot;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Adventure.Versteckis;

/// <summary>
/// The server's game, phase by phase (#1268, plan section 1): Gather on the village square, Hide (the seeker blind at
/// the church door), Seek, the round's end and its points, the rotation of the seeker, the results, and the release:
/// every human back where it stood, paid the same way for the rounds it played.
/// </summary>
public partial class VersteckisManager
{
    /// <summary>Gather: the area planned for the head count, bots added, everyone lent the round and put on the square.</summary>
    private void Gather(Game g)
    {
        var s = g.S;
        _chat?.Invites?.Withdraw(InviteId, s.Id);
        int humans = s.Humans;
        int bots = s.Bots > 0 ? Math.Min(VsRules.MaxPlayers - humans, Math.Max(0, s.Bots - humans)) : 0;
        s.Radius = VsRules.Radius(humans + bots);
        g.Site!.Plan(s.Radius);
        s.Kinds = g.Site.Kinds;
        s.Rounds = VsRules.RoundsFor(humans, g.AskedRounds);
        AddBots(g, bots);
        var human = s.Entrants.Where(e => !e.Bot).OrderBy(e => e.Order).Select(e => e.Peer).ToList();
        var bot = s.Entrants.Where(e => e.Bot).Select(e => e.Peer).ToList();
        g.Order = VsRules.SeekerOrder(human, bot, s.Rounds);
        s.Phase = VsPhase.Gather;
        s.PhaseStarted = Now;
        s.PhaseEnds = Now + VsRules.GatherSeconds;
        BuildSolid(g);
        // the square: everyone 3-6 m from the centre, in a ring
        int count = s.Entrants.Count, k = 0;
        foreach (var e in s.Entrants)
        {
            double a = Math.Tau * k / Math.Max(1, count), r = 3 + (k % 2) * 2.5;
            k++;
            double pe = s.E + Math.Cos(a) * r, pn = s.N + Math.Sin(a) * r;
            if (e.Bot) PlaceBot(e.Peer, pe, pn);
            else if (Connected(e.Peer))
            {
                RpcId(e.Peer, MethodName.Enter, s.Id);
                RpcId(e.Peer, MethodName.PutAt, pe, pn);
            }
            g.RunOf(e.Peer).Last = null;
        }
        Tell(s, $"Versteckis at {s.Label}: {s.Rounds} rounds, {humans} players" + (bots > 0 ? $" and {bots} bots" : "") + ". To the square!");
        GD.Print(FormattableString.Invariant($"[vs] game {s.Id}: gather, {humans} players + {bots} bots, {s.Rounds} rounds, radius {s.Radius:F0} m, {g.Site.Decor.Count} decor"));
        Push();
    }

    /// <summary>A round starts: the seeker in turn, blind at the church door; the hiders have the hide time to pick a prop and a spot.</summary>
    private void StartRound(Game g)
    {
        var s = g.S;
        s.Round++;
        // the seeker in turn; one who left gives the round to the next human, else a bot
        long seeker = g.Order.Count >= s.Round ? g.Order[s.Round - 1] : 0;
        if (s.Find(seeker) is not { Left: false })
            seeker = s.Entrants.Where(e => !e.Left).OrderBy(e => e.Seeks).ThenBy(e => e.Bot).ThenBy(e => e.Order).Select(e => e.Peer).FirstOrDefault();
        s.Seeker = seeker;
        var kind0 = s.Kinds.Count > 0 ? s.Kinds[0] : VsProp.HayBale;
        foreach (var e in s.Entrants)
        {
            e.Role = e.Left ? VsRole.Out : e.Peer == seeker ? VsRole.Seeker : VsRole.Hider;
            e.Caught = false;
            e.CaughtAt = 0;
            e.Revealed = false;
            e.RoundScore = 0;
            e.Moos = 0;
            e.DecoyUsed = false;
            e.Sniffs = VsRules.SniffCharges;
            e.SniffBackAt = 0;
            e.SprintReadyAt = 0;
            e.MooReadyAt = 0;
            e.ChangeReadyAt = 0;
            e.Holding = -1;
            e.DisguiseBackAt = 0;
            if (e.Role == VsRole.Hider && (e.Prop == VsProp.None || !s.Kinds.Contains(e.Prop))) e.Prop = kind0;
            if (e.Role == VsRole.Seeker) { e.Prop = VsProp.None; e.Seeks++; }
            if (!e.Left) e.Played++;
            var run = g.RunOf(e.Peer);
            run.OutSince = -1;
            run.HoldFrom = -1;
            run.HoldSite = -1;
            run.CarryMetres = 0;
            run.Last = null;
            run.TagReadyAt = 0;
            run.SprintUntil = 0;
        }
        s.Objectives = g.Site!.Sites.Select(o => new VsObjective { Site = o.Site, E = o.E, N = o.N, Alt = o.Alt, FromE = o.FromE, FromN = o.FromN }).ToList();
        s.Decoys.Clear();
        s.Pins.Clear();
        s.Awake = false;
        s.Phase = VsPhase.Hide;
        s.PhaseStarted = Now;
        s.PhaseEnds = Now + VsRules.HideSeconds(s.Playing.Count());
        foreach (var e in s.Entrants)
        {
            if (e.Left) continue;
            if (!e.Bot && Connected(e.Peer)) RpcId(e.Peer, MethodName.Kit, (int)e.Role);
        }
        PutAtDoor(g, seeker);
        BotsRound(g);
        Tell(s, $"Round {s.Round} of {s.Rounds}: {Who(s, seeker)} seeks. Hide!");
        GD.Print($"[vs] game {s.Id}: round {s.Round}, seeker {Who(s, seeker)}, {s.Entrants.Count(e => e.Role == VsRole.Hider)} hiders");
        Push();
    }

    /// <summary>The seeker to the church door (and back there whenever it strays in Hide).</summary>
    private void PutAtDoor(Game g, long seeker)
    {
        var s = g.S;
        if (s.Find(seeker) is not { } e) return;
        if (e.Bot) PlaceBot(seeker, s.DoorE, s.DoorN);
        else if (Connected(seeker)) RpcId(seeker, MethodName.PutAt, s.DoorE, s.DoorN);
        g.RunOf(seeker).Last = null;
    }

    private void StartSeek(Game g)
    {
        var s = g.S;
        s.Phase = VsPhase.Seek;
        s.PhaseStarted = Now;
        s.PhaseEnds = Now + VsRules.SeekSeconds(s.Entrants.Count(e => e.Role == VsRole.Hider && !e.Left));
        foreach (var e in s.Entrants)
            if (e.Role == VsRole.Hider) g.RunOf(e.Peer).HiddenSince = Now;
        Tell(s, $"Ready or not: {Who(s, s.Seeker)} is coming!");
        GD.Print($"[vs] game {s.Id}: seek, {s.PhaseEnds - Now:F0} s");
        Push();
    }

    /// <summary>Every frame in Seek: decoys run out, sniffs come back, disguises return after a hold.</summary>
    private void SeekTick(Game g, double now)
    {
        var s = g.S;
        bool changed = s.Decoys.RemoveAll(d => now >= d.Until) > 0;
        foreach (var e in s.Entrants)
        {
            if (e.Role == VsRole.Seeker && e.Sniffs < VsRules.SniffCharges && e.SniffBackAt > 0 && now >= e.SniffBackAt)
            {
                e.Sniffs++;
                e.SniffBackAt = e.Sniffs < VsRules.SniffCharges ? now + VsRules.SniffRecharge : 0;
                changed = true;
            }
            if (e.DisguiseBackAt > 0 && now >= e.DisguiseBackAt)
            {
                e.DisguiseBackAt = 0;
                changed = true;
            }
        }
        if (changed) Push();
    }

    /// <summary>The round ends: the hiders' time and survival, the seeker's bonus, the pins on the map.</summary>
    private void EndRound(Game g, bool seekerLeft)
    {
        var s = g.S;
        if (s.Phase is not (VsPhase.Hide or VsPhase.Seek)) return;
        double now = Now;
        bool seeking = s.Phase == VsPhase.Seek;
        double left = seeking ? s.PhaseEnds - now : 0;
        int hiders = s.Entrants.Count(e => e.Role == VsRole.Hider);
        foreach (var e in s.Entrants.Where(e => e.Role == VsRole.Hider))
        {
            var run = g.RunOf(e.Peer);
            double hidden = seeking ? (e.Caught ? e.CaughtAt : now) - run.HiddenSince : 0;
            int points = VsRules.HiddenPoints(hidden);
            if (!e.Caught && !e.Left && seeking && !seekerLeft)
            {
                points += VsRules.SurvivePoints;
                e.Survived++;
                if (e.Prop != VsProp.None) e.SurvivedAs[(int)e.Prop - 1]++;
            }
            e.BestHide = Math.Max(e.BestHide, (int)hidden);
            e.Score += points;
            e.RoundScore += points;
            var at = PositionOf(e.Peer);
            if (at is { } p) s.Pins.Add(new VsPin(e.Peer, e.Name, e.Prop, p.E, p.N, e.Caught));
            e.Holding = -1;
            e.DisguiseBackAt = 0;
        }
        if (!seekerLeft && seeking && hiders > 0 && s.HidersLeft == 0 && s.Find(s.Seeker) is { } seeker)
        {
            int bonus = VsRules.AllCaughtPoints(left);
            seeker.Score += bonus;
            seeker.RoundScore += bonus;
        }
        foreach (var o in s.Objectives) o.Carrier = 0;
        s.Decoys.Clear();
        s.Phase = VsPhase.RoundEnd;
        s.PhaseStarted = now;
        s.PhaseEnds = now + VsRules.RoundEndSeconds;
        Tell(s, seekerLeft ? "The seeker left: the round ends here, nobody loses a point."
            : $"Round {s.Round} over: " + string.Join(", ", s.Entrants.Where(e => !e.Left).OrderByDescending(e => e.RoundScore).Select(e => $"{e.Name} +{e.RoundScore}")));
        GD.Print($"[vs] game {s.Id}: round {s.Round} over ({s.HidersLeft} of {hiders} hidden), scores "
            + string.Join(", ", s.Entrants.Select(e => $"{e.Name} {e.Score}")));
        Push();
    }

    private void Results(Game g)
    {
        var s = g.S;
        s.Phase = VsPhase.Results;
        s.PhaseStarted = Now;
        s.PhaseEnds = Now + VsRules.ResultsSeconds;
        s.Winners = VsRules.Winners(s.Entrants);
        Tell(s, $"Versteckis at {s.Label} is over! Top: {string.Join(" and ", s.Winners.Select(w => Who(s, w)))}.");
        GD.Print($"[vs] game {s.Id}: results, top {string.Join(", ", s.Winners.Select(w => Who(s, w)))}");
        Push();
    }

    /// <summary>The game ends: every human released and paid for its rounds, the bots gone. A null reason: it ended as it should.</summary>
    private void Close(Game g, string? reason)
    {
        if (!_games.Remove(g)) return;
        var s = g.S;
        _chat?.Invites?.Withdraw(InviteId, s.Id);
        // out of the registry first, so the rewards' events reach the Adventure layer
        foreach (var e in s.Entrants) if (!e.Left) e.Left = true;
        foreach (var e in s.Entrants.Where(e => !e.Bot)) ReleasePeer(g, e);
        RemoveBots(g);
        g.Solid?.QueueFree();
        g.Solid = null;
        if (s.Round > 0) SaveHistory(s.E, s.N);
        s.Phase = VsPhase.Closed;
        if (reason != null) Tell(s, reason);
        GD.Print($"[vs] game {s.Id} closed{(reason != null ? ": " + reason : "")}");
        Push();
    }

    /// <summary>One human out of the game (it left, or the game ended): back where it stood, then paid for the rounds it played.</summary>
    private void ReleasePeer(Game g, VsEntrant e)
    {
        if (e.Bot || !g.Runs.TryGetValue(e.Peer, out var run) && e.Played == 0 && g.S.Phase == VsPhase.Lobby) return;
        if (!_released.Add((g.S.Id, e.Peer))) return;
        if (g.S.Phase != VsPhase.Lobby && Connected(e.Peer)) RpcId(e.Peer, MethodName.Release);
        if (e.Played > 0) Reward(g, e);
    }

    private readonly HashSet<(int, long)> _released = new();

    /// <summary>Records kept for players with no Adventure progress (the layer off): this session only.</summary>
    private readonly Dictionary<string, VersteckisProgress> _session = new();

    private VersteckisProgress SessionRecord(long peer)
    {
        string name = _chat?.NameOfPeer(peer) ?? peer.ToString();
        return _session.TryGetValue(name, out var p) ? p : _session[name] = new VersteckisProgress();
    }

    /// <summary>
    /// The pay (#1268, plan 5): the same for every human who played, host or guest, winner or not, party or not, but for
    /// the top scorer's small bonus; a credit to the account (never a harvest: no group yield in a round). The
    /// progress, titles and cosmetics through the Adventure layer, then its events, once the player is out of the round.
    /// </summary>
    private void Reward(Game g, VsEntrant e)
    {
        var s = g.S;
        var adventure = AdventureService.Instance;
        var progress = adventure?.Progress(e.Peer);
        var record = progress?.Get(VersteckisProgress.Section) ?? SessionRecord(e.Peer);
        bool top = s.Winners.Contains(e.Peer);
        var tally = new VsGameTally(e.Played, e.Seeks, e.Catches, e.Survived, e.SurvivedAs, e.Bells, e.Fires, e.Cans, e.MooTotal, e.BestHide, s.Canton);
        var (francs, titles, items) = VersteckisRewards.Fold(record, tally, top,
            DateTimeOffset.FromUnixTimeSeconds((long)ClockSync.ServerUnixNow).UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        progress?.Touch(VersteckisProgress.Section);
        if (francs > 0) Items.Bank.Instance?.Credit(e.Peer, francs, "versteckis");
        if (adventure != null && progress != null)
        {
            foreach (var t in titles) adventure.Grant(e.Peer, new Title(t), "versteckis");
            foreach (var item in items) adventure.Grant(e.Peer, new Item(item), "versteckis");
            adventure.Events.Raise(new VersteckisPlayed(e.Peer, progress.Key, e.Played, e.Score, top, s.Canton));
            if (e.Catches > 0) adventure.Events.Raise(new VersteckisCaught(e.Peer, progress.Key, e.Catches));
            for (int i = 0; i < e.Bells; i++) adventure.Events.Raise(new VersteckisObjective(e.Peer, progress.Key, "bell"));
            for (int i = 0; i < e.Fires; i++) adventure.Events.Raise(new VersteckisObjective(e.Peer, progress.Key, "fire"));
            for (int i = 0; i < e.Cans; i++) adventure.Events.Raise(new VersteckisObjective(e.Peer, progress.Key, "can"));
        }
        if (Connected(e.Peer))
            _chat?.Tell(e.Peer, $"Versteckis: {e.Played} rounds, {e.Score} points" + (francs > 0 ? $", CHF {francs} to your account" : ", no francs (five paid games a day)")
                + (titles.Count > 0 ? $". Title: {string.Join(", ", titles)}" : "") + (items.Count > 0 ? $". Earned: {string.Join(", ", items.Select(i => Items.ItemDefs.Get(i)?.Name ?? i.ToString()))}" : "") + ".",
                ChatKind.System);
        GD.Print(FormattableString.Invariant($"[vs] paid {e.Name} {francs} CHF ({e.Played} rounds, {e.Score} points, top {top}, progress {(progress != null ? "saved" : "session")})"));
    }
}
