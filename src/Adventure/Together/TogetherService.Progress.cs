using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Together;

/// <summary>A newcomer was mentored through a milestone (#1266 part 6): raised for both, <see cref="Role"/> "newcomer" or "mentor".</summary>
public sealed record Mentored(long Peer, string Key, string Milestone, string Role) : AdventureEvent(Peer, Key);

/// <summary>A roped pair crossed 200 m of glacier together (#1266): the glacier milestone.</summary>
public sealed record GlacierCrossed(long Peer, string Key) : AdventureEvent(Peer, Key);

/// <summary>A player rode 2 km on a mount or a vehicle of its own (#1266): the ride milestone.</summary>
public sealed record RideStarted(long Peer, string Key, int RideKind) : AdventureEvent(Peer, Key);

/// <summary>
/// Played together this week, the punch card and the mentors (#1266 parts 6-7), the authority's: a 1 Hz tick adds a
/// second to every adventuring peer with a party member within 200 m (written to the <c>together</c> section, touched
/// once a minute or when something was earned), the week's Fine chest, the day's stamp, a full card's Precious chest,
/// title and hat; the milestones from the events (a first summit, a roped glacier crossing, a 2 km ride, a ten-piece
/// structure) and, when a veteran is linked to or near a newcomer, both cosmetics.
/// </summary>
public partial class TogetherService
{
    /// <summary><c>--together-day-seconds n</c> (checks): a stamp after n seconds together instead of 20 min, the week's chest after 1.5 n.</summary>
    private static readonly int DaySeconds = int.TryParse(CmdArgs.Value("--together-day-seconds"), out int d) && d > 0 ? d : TogetherProgress.StampSeconds;

    private double _nextSecond;
    private readonly Dictionary<long, double> _touchDue = new();
    private readonly Dictionary<long, (double E, double N, double Metres)> _rides = new();
    private readonly List<long> _peerScratch = new();

    partial void ReadyProgress()
    {
        if (!Authority) return;
        _adventure.Events.Raised += OnAdventureEvent;
        _adventure.Saving += FlushSeconds;
    }

    /// <summary>Before a save: the seconds counted since the last batched Touch are told too, so the save is never a surprise.</summary>
    private void FlushSeconds(PlayerProgress p)
    {
        if (p.Has(TogetherProgress.Section.Id) && p.Untouched().Contains(TogetherProgress.Section.Id)) p.Touch(TogetherProgress.Section);
    }

    private static string PeakTag => PoiId.Tag(PoiKind.Peak);
    private static string CrossTag => PoiId.Tag(PoiKind.SummitCross);

    private void OnAdventureEvent(AdventureEvent e)
    {
        switch (e)
        {
            case PoiDiscovered d when d.Kind == PeakTag || d.Kind == CrossTag: Milestone(e.Peer, MentorRules.Summit); break;
            case GlacierCrossed: Milestone(e.Peer, MentorRules.Glacier); break;
            case RideStarted: Milestone(e.Peer, MentorRules.Ride); break;
            case Build.StructureBuilt: Milestone(e.Peer, MentorRules.Build); break;
        }
    }

    /// <summary>
    /// <paramref name="peer"/> did <paramref name="milestone"/>: with a veteran linked to it or in its party near, a mentorship
    /// (both sections written, then the grants and <see cref="Mentored"/>); in any case the milestone counts for it.
    /// </summary>
    public void Milestone(long peer, string milestone)
    {
        if (!Authority || _adventure.Progress(peer) is not { } p) return;
        var mine = p.Get(TogetherProgress.Section);
        foreach (long mate in Mates(peer))
        {
            if (_adventure.Progress(mate) is not { } q) continue;
            var theirs = q.Get(TogetherProgress.Section);
            if (!MentorRules.Counts(milestone, p.Key, mine, q.Key, theirs)) continue;
            var (newcomerGets, mentorGets, title) = MentorRules.Apply(milestone, p.Key, mine, theirs);
            p.Touch(TogetherProgress.Section);
            q.Touch(TogetherProgress.Section);
            GD.Print($"[together] {Who(mate)} mentors {Who(peer)}: {milestone}");
            _adventure.Grant(peer, new Item(newcomerGets), $"mentored:{milestone}");
            if (mentorGets is { } m) _adventure.Grant(mate, new Item(m), $"mentor:{milestone}");
            if (title != null) _adventure.Grant(mate, new Title(title), $"mentor:{milestone}");
            Tell(peer, $"{Who(mate)} showed you {MentorRules.Of(milestone)!.Name}: a cosmetic of your own.");
            Tell(mate, $"You showed {Who(peer)} {MentorRules.Of(milestone)!.Name}: a mentor's reward.");
            _adventure.Events.Raise(new Mentored(peer, p.Key, milestone, "newcomer"));
            _adventure.Events.Raise(new Mentored(mate, q.Key, milestone, "mentor"));
            return;
        }
        MentorRules.Did(mine, milestone);
        p.Touch(TogetherProgress.Section);
    }

    /// <summary>Who counts as with a peer for a mentorship: its links, then its party within the together radius.</summary>
    private IEnumerable<long> Mates(long peer)
    {
        foreach (long l in Links.Chain(peer)) if (l != peer) yield return l;
        if (Party(peer) is not { } party || PositionOf(peer) is not { } at) yield break;
        foreach (long m in party)
            if (m != peer && !Links.Chain(peer).Contains(m) && PositionOf(m) is { } pos && pos.HorizontalDistanceTo(at) <= TogetherRules.Radius && _adventure.Active(m))
                yield return m;
    }

    /// <summary>The 1 Hz tick: seconds together, the week, the card; rides toward the ride milestone.</summary>
    partial void ProcessProgress(double delta, double now)
    {
        if (now < _nextSecond) return;
        double dt = _nextSecond == 0 ? 1 : now - _nextSecond + 1;
        _nextSecond = now + 1;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var utc = DateTime.UtcNow;
        _peerScratch.Clear();
        foreach (long peer in _adventure.Peers) _peerScratch.Add(peer);
        if (PairPeer != 0 && !_peerScratch.Contains(PairPeer)) _peerScratch.Add(PairPeer);
        foreach (long peer in _peerScratch)
        {
            if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p) continue;
            TrackRide(peer, p);
            if (Near(peer, TogetherRules.PlayRadius) == 0) continue;
            var t = p.Get(TogetherProgress.Section);
            int seconds = (int)Math.Round(Math.Clamp(dt, 0, 5) * TogetherProgress.StampSeconds / DaySeconds);
            var earned = t.Add(seconds, utc);
            MeetParty(peer, t);
            if (earned.WeekChest || earned.Stamp)
            {
                p.Touch(TogetherProgress.Section);
                _touchDue.Remove(peer);
                Grants(peer, earned, t);
            }
            else if (!_touchDue.TryGetValue(peer, out double due)) _touchDue[peer] = now + 60;
            else if (now >= due)
            {
                p.Touch(TogetherProgress.Section);
                _touchDue.Remove(peer);
            }
        }
        if (_server) Net.ServerStats.Ran("together", t0);
    }

    private void Grants(long peer, TogetherProgress.Earned earned, TogetherProgress t)
    {
        var rewards = Rewards.RewardService.Instance;
        if (earned.WeekChest)
        {
            GD.Print($"[together] {Who(peer)}: the week's chest");
            rewards?.GrantRoll(peer, Rewards.ChestTier.Fine, "together:week");
            Tell(peer, "Zäme unterwegs: your weekly chest.");
        }
        if (earned.Stamp)
        {
            GD.Print($"[together] {Who(peer)}: a stamp ({t.Stamps}/{TogetherProgress.StampsPerCard}, {t.Cards} cards)");
            Tell(peer, earned.CardFull ? "Your punch card is full!" : $"A stamp on your punch card ({t.Stamps}/{TogetherProgress.StampsPerCard}).");
        }
        if (earned.CardFull)
        {
            rewards?.GrantRoll(peer, Rewards.ChestTier.Precious, "together:card");
            if (earned.Title != null) _adventure.Grant(peer, new Title(earned.Title), "together:card");
            if (earned.Hat) _adventure.Grant(peer, new Item(TogetherProgress.CardHat), "together:card");
        }
    }

    /// <summary>The proven party members near are remembered by their short keys (never sent to anyone).</summary>
    private void MeetParty(long peer, TogetherProgress t)
    {
        if (Party(peer) is not { } party) return;
        foreach (long m in party)
            if (m != peer && _adventure.KeyOf(m) is { } key && !MentorRules.SessionKey(key) && TogetherProgress.Short(key) is { Length: > 0 } s)
                t.Meet(s);
    }

    /// <summary>A peer on its own mount or vehicle: its metres, and at 2 km the ride milestone (once a ride).</summary>
    private void TrackRide(long peer, PlayerProgress p)
    {
        if (Body(peer) is not { } body || body.RideKindId == 0 || body.RidingWith != 0 || PositionOf(peer) is not { } at)
        {
            _rides.Remove(peer);
            return;
        }
        var r = _rides.GetValueOrDefault(peer, (at.E, at.N, 0));
        double step = Math.Sqrt((at.E - r.E) * (at.E - r.E) + (at.N - r.N) * (at.N - r.N));
        double metres = r.Metres + (step < 150 ? step : 0);
        if (r.Metres < MentorRules.RideMetres && metres >= MentorRules.RideMetres)
            _adventure.Events.Raise(new RideStarted(peer, p.Key, body.RideKindId));
        _rides[peer] = (at.E, at.N, metres);
    }

    // a roped pair's metres on glacier cover, per follower
    private readonly Dictionary<long, (double E, double N, double Metres)> _glacier = new();

    /// <summary>The links' 4 Hz tick: a roped pair on glacier cover adds its metres; at 200 m both crossed a glacier.</summary>
    partial void TickGlacierCrossing(float dt)
    {
        foreach (var l in Links.All)
        {
            if (l.Mode != LinkMode.Rope || PositionOf(l.Follower) is not { } a || PositionOf(l.Leader) is not { } b || !OnGlacier(a) || !OnGlacier(b))
            {
                _glacier.Remove(l.Follower);
                continue;
            }
            var g = _glacier.GetValueOrDefault(l.Follower, (a.E, a.N, 0));
            double step = Math.Sqrt((a.E - g.E) * (a.E - g.E) + (a.N - g.N) * (a.N - g.N));
            double metres = g.Metres + (step < 40 ? step : 0);
            if (g.Metres < MentorRules.GlacierMetres && metres >= MentorRules.GlacierMetres)
            {
                GD.Print($"[together] {Who(l.Leader)} and {Who(l.Follower)} crossed a glacier roped");
                foreach (long peer in new[] { l.Follower, l.Leader })
                    if (_adventure.Progress(peer) is { } p) _adventure.Events.Raise(new GlacierCrossed(peer, p.Key));
            }
            _glacier[l.Follower] = (a.E, a.N, metres);
        }
    }

    /// <summary>The pots shared count on the section when one ends with its feast.</summary>
    private void CountPot(long peer, Pot pot)
    {
        if (_adventure.Progress(peer) is not { } p) return;
        var t = p.Get(TogetherProgress.Section);
        t.Fondues++;
        if (pot.Splitted) t.Splits++;
        p.Touch(TogetherProgress.Section);
    }
}
