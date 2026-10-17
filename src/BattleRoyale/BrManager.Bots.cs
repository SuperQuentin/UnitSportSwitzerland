using Godot;
using UnitSport.Bots;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.BattleRoyale;

/// <summary>
/// Bots in a match (#837, server). At GO the field is topped up to <see cref="BrState.Bots"/> with
/// bots the server runs (<c>Bots/BotDirector</c>); each is an entrant like anyone, whose id is its
/// negative NPC id. What a client reports for its own player (death and its box, down, the zone's
/// damage) the server does here for its bots, through the same methods. Phase 1: bots appear on the
/// ground inside the first circle and stroll (<see cref="WanderBrain"/>); <c>BrBrain</c> (phase 2) makes
/// them board, loot, fight and run from the zone. Docs: <c>docs/notes/bots/</c>.
/// </summary>
public partial class BrManager
{
    /// <summary>
    /// The field a match is topped up to when nobody says otherwise (#837). <c>/br bots n|off</c> or
    /// <c>--brbots n</c> (0 = off) changes it.
    /// </summary>
    public const int DefaultBots = 20;
    /// <summary>A bot is put at least this far inside the first circle's edge.</summary>
    private const float BotEdgeMargin = 0.85f;

    private int _botTarget = Core.CmdArgs.Int("--brbots") is int b && b >= 0 ? Math.Min(b, BotDirector.MaxBots) : DefaultBots;
    private BotDifficulty _botLevel = BotDifficulty.Normal;
    private bool _botsHooked;
    private double _botZoneTick;

    private static BotDirector? Director => BotDirector.Instance;

    private FootPlayer? BodyOf(long id) => _players?.GetNodeOrNull<FootPlayer>(PlayerReplication.NodeName(id));

    /// <summary><c>/br bots [n|off] [easy|normal|hard]</c> (admin): the field bots top a match up to.</summary>
    private string BotsCommand(string[] words)
    {
        if (words.Length == 0)
            return _botTarget == 0 ? "No bots: /br bots <field size> [easy|normal|hard] tops matches up with bots."
                : $"Matches are topped up with {_botLevel.ToString().ToLowerInvariant()} bots to {_botTarget} players.";
        foreach (var w in words)
        {
            if (w.Equals("off", StringComparison.OrdinalIgnoreCase)) _botTarget = 0;
            else if (int.TryParse(w, out int n) && n >= 0) _botTarget = Math.Min(n, BotDirector.MaxBots);
            else if (BotProfile.ParseDifficulty(w) is { } d) _botLevel = d;
            else return "Usage: /br bots [field size|off] [easy|normal|hard]";
        }
        if (!_state.Running && _state.Phase != BrPhase.Idle)
        {
            _state.Bots = _botTarget;
            _state.BotLevel = (int)_botLevel;
            Push();
        }
        return _botTarget == 0 ? "Bots off." : $"Matches will be topped up with {_botLevel.ToString().ToLowerInvariant()} bots to {_botTarget} players.";
    }

    /// <summary>
    /// The match rules the server answers for the bodies it runs (#837): a bot entrant stays down once
    /// out, never regenerates, and goes down rather than out while a team-mate stands, as a client
    /// answers for its own player in <c>EnterMatch</c>. Everyone else's answer is the default.
    /// </summary>
    private void HookBots()
    {
        if (_botsHooked || Director is not { } director) return;
        _botsHooked = true;
        FootPlayer.StayDown = p => BotEntrant(p) is { Alive: true };
        FootPlayer.Regenerates = p => BotEntrant(p) is not { Alive: true };
        FootPlayer.CanBeDowned = p => BotEntrant(p) is { Alive: true } e && _state.MateStanding(e.Peer);
        director.Died += OnBotDied;
        director.Downed += OnBotDowned;
    }

    private BrEntrant? BotEntrant(FootPlayer p) =>
        p.Bot && _state.Phase == BrPhase.Playing && FootPlayer.NetId(p.Name) is long id ? _state.Find(id) : null;

    /// <summary>
    /// GO, before the zone is drawn: how many bots join, so the first circle is sized for the whole
    /// field. They are only spawned once the circle is known (<see cref="PlaceBots"/>).
    /// </summary>
    private int BotsAtGo() => Director == null ? 0 : Math.Min(BrState.BotsToAdd(_state.Entrants.Count, _state.Bots),
        BotDirector.MaxBots - Director.Count);

    /// <summary>
    /// GO: <paramref name="count"/> bots on the ground inside the first circle, off water where the lattice
    /// tells, each with the match kit; entrants from now on (already out of the plane: phase 1 bots do
    /// not board).
    /// </summary>
    private void PlaceBots(int count)
    {
        if (count <= 0 || Director is not { } director) return;
        HookBots();
        var zone = _state.Zone();
        var centre = zone.CentreOf(0);
        float radius = zone.RadiusOf(0) * BotEdgeMargin;
        var horizon = _horizon is { IsCompletedSuccessfully: true } done ? done.Result : null;
        var rng = new Random(_state.Seed ^ 0x5bd1e995);
        var taken = new HashSet<string>(_state.Entrants.Select(e => e.Name));
        for (int i = 0; i < count; i++)
        {
            double e = 0, n = 0;
            for (int tries = 0; tries < 12; tries++)
            {
                double a = rng.NextDouble() * Math.Tau, r = radius * Math.Sqrt(rng.NextDouble());
                e = _state.AreaE + centre.X + Math.Cos(a) * r;
                n = _state.AreaN + centre.Y + Math.Sin(a) * r;
                if (!BrMapImage.Wet(horizon, e, n)) break;
            }
            var profile = BotProfile.For(_state.Seed, i, _botLevel, taken);
            // at sea level: it drops onto the ground once the server has built its collision
            if (director.Add(new Core.GlobalPos(e, n, 0), (float)(rng.NextDouble() * Math.Tau), profile) is not { } bot) break;
            bot.Inventory.Put(0, new Items.ItemStack(Items.ItemId.Knife, 1));
            bot.Inventory.Add(Items.ItemId.Bandage, 3);
            _state.Entrants.Add(new BrEntrant { Peer = bot.Id, Name = profile.Name, Bot = true, Jumped = true });
        }
        GD.Print($"[br] bots: {_state.Entrants.Count(x => x.Bot)} on the ground ({_botLevel})");
    }

    /// <summary>The match is over: its bots go for everyone.</summary>
    private void RemoveBots(BrState was)
    {
        if (Director is not { } director) return;
        foreach (var e in was.Entrants.Where(e => e.Bot)) director.Remove(e.Peer);
    }

    /// <summary>What a client's <c>OnLocalDied</c> reports for its player, for a bot: its box, then out.</summary>
    private void OnBotDied(Bot bot, long killer, DamageCause cause)
    {
        if (_state.Phase != BrPhase.Playing || _state.Find(bot.Id) is not { Alive: true } e) return;
        var stacks = Enumerable.Range(0, Items.Inventory.Size).Select(i => bot.Inventory[i])
            .Where(s => !s.IsEmpty && s.Data == null).ToList();
        DeathBox(e, stacks.Select(s => (int)s.Id).ToArray(), stacks.Select(s => s.Count).ToArray());
        bot.Inventory.Clear();
        Eliminate(e, killer, OutOf(cause, killer));
    }

    private void OnBotDowned(Bot bot, long attacker)
    {
        if (_state.Phase == BrPhase.Playing && _state.Find(bot.Id) is { Alive: true, Downed: false } e) Downed(e, attacker);
    }

    /// <summary>Why someone left the match, from what their body died of (the client's mapping, shared).</summary>
    public static BrOut OutOf(DamageCause cause, long killer) => cause switch
    {
        DamageCause.Zone => BrOut.Zone,
        DamageCause.Fall or DamageCause.Crash => BrOut.Fell,
        _ => killer != 0 ? BrOut.Killed : BrOut.Other,
    };

    /// <summary>The zone hurts the bots too, every half second, as each client hurts its own player.</summary>
    private void BotZone(ZoneSchedule zone, double delta)
    {
        _botZoneTick += delta;
        if (_botZoneTick < 0.5) return;
        double dt = _botZoneTick;
        _botZoneTick = 0;
        if (Origin == null || zone.At(Now - _state.Started) is not { Dps: > 0 } z) return;
        foreach (var e in _state.Entrants.Where(e => e.Bot && e.Alive))
            if (BodyOf(e.Peer) is { Eliminated: false } body)
            {
                var (pe, pn) = Origin.ToLv95(body.GlobalPosition);
                if (z.Outside(new Vector2((float)(pe - _state.AreaE), (float)(pn - _state.AreaN))))
                    body.TakeDamage(z.Dps * (float)dt, 0, DamageCause.Zone);
            }
    }

    /// <summary>
    /// Players were in it and none is left standing: only bots would play on (#837). The match ends
    /// then while bots only stroll; once <c>BrBrain</c> fights (phase 2), bots play it out to a winner
    /// and spectators watch (decided on #837).
    /// </summary>
    private bool NoPlayerLeft => _state.Entrants.Any(e => !e.Bot) && _state.HumansAlive == 0;
}
