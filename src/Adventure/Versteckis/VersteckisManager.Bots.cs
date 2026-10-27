using Godot;
using UnitSport.Bots;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Adventure.Versteckis;

/// <summary>
/// Bots in a game (#1268, plan "Bots"): at Gather a game is topped up with server-run players (<c>Bots/BotDirector</c>)
/// that play both roles through <see cref="VersteckisBrain"/>, which acts through the same <c>Serve*</c> paths the RPCs
/// call. The server answers a bot body's disguised walk (<see cref="FootPlayer.PropWalk"/>) and hands a seeker bot what
/// a player would hear and sniff; it never gives a brain a role or a hider's position.
/// </summary>
public partial class VersteckisManager
{
    private readonly Dictionary<FootPlayer, (Game G, long Id)> _botBodies = new();

    /// <summary>The server's ground under a world point (its terrain collision's), for the decor's boxes and the bots' places.</summary>
    public Func<Vector3, float?> GroundWorld { get; set; } = _ => null;

    /// <summary>Where the server's world is anchored (LV95 to world and back), for the brains.</summary>
    public WorldOrigin Origin => _origin;

    /// <summary>A game's state by id (a brain's view of the public game: what the HUD shows a player).</summary>
    public VsState? StateOf(int id)
    {
        foreach (var g in _games) if (g.S.Id == id) return g.S;
        return null;
    }

    /// <summary>A game's decor (every peer computes it; a seeker bot sees the props in its cone).</summary>
    public IReadOnlyList<VsDecor> DecorOf(int id)
    {
        foreach (var g in _games) if (g.S.Id == id && g.Site != null) return g.Site.Decor;
        return Array.Empty<VsDecor>();
    }

    /// <summary>What a body looks like now: its prop when disguised (what any player sees drawn), else none.</summary>
    public VsProp Looks(int game, long peer) => StateOf(game)?.Find(peer) is { Disguised: true } e ? e.Prop : VsProp.None;

    /// <summary>The site a bot of this game went for (one bot at a time on an objective), or -1.</summary>
    private readonly Dictionary<int, long> _objectiveBot = new();

    /// <summary>A hider bot claims the game's objective run: true when no other bot is on one.</summary>
    public bool ClaimObjective(int game, long bot)
    {
        if (_objectiveBot.TryGetValue(game, out long by) && by != bot && StateOf(game)?.Find(by) is { Hiding: true }) return false;
        _objectiveBot[game] = bot;
        return true;
    }

    public void FreeObjective(int game, long bot)
    {
        if (_objectiveBot.TryGetValue(game, out long by) && by == bot) _objectiveBot.Remove(game);
    }

    private void AddBots(Game g, int count)
    {
        if (count <= 0 || BotDirector.Instance is not { } director) return;
        var taken = new HashSet<string>(g.S.Entrants.Select(e => e.Name));
        var level = (BotDifficulty)Math.Clamp(g.S.BotLevel, 0, 2);
        for (int i = 0; i < count; i++)
        {
            double a = Math.Tau * i / Math.Max(1, count);
            double e = g.S.E + Math.Cos(a) * 6, n = g.S.N + Math.Sin(a) * 6;
            var world = _origin.ToWorld(e, n, 0);
            float alt = GroundWorld(world) ?? g.Site?.GroundAt(e, n) ?? 0;
            var profile = BotProfile.For(g.S.Seed, i, level, taken);
            taken.Add(profile.Name);
            if (director.Add(new GlobalPos(e, n, alt + 1.5), (float)a, profile) is not { } bot) break;
            bot.Brain = new VersteckisBrain(this, g.S.Id, g.S.Seed ^ (i * 7919));
            g.S.Entrants.Add(new VsEntrant { Peer = bot.Id, Name = profile.Name, Bot = true, Order = g.S.Entrants.Count });
            _botBodies[bot.Body] = (g, bot.Id);
        }
        GD.Print($"[vs] game {g.S.Id}: {g.S.Entrants.Count(e => e.Bot)} bots ({level})");
    }

    private void RemoveBots(Game g)
    {
        var director = BotDirector.Instance;
        foreach (var e in g.S.Entrants.Where(e => e.Bot))
        {
            if (director?.Get(e.Peer) is { } bot) _botBodies.Remove(bot.Body);
            director?.Remove(e.Peer);
        }
        foreach (var dead in _botBodies.Where(kv => !IsInstanceValid(kv.Key)).Select(kv => kv.Key).ToList()) _botBodies.Remove(dead);
        _objectiveBot.Remove(g.S.Id);
    }

    /// <summary>A bot's body put on the ground at an LV95 point (Gather, the seeker's door).</summary>
    private void PlaceBot(long id, double e, double n)
    {
        if (BotDirector.Instance?.Get(id) is not { } bot || !IsInstanceValid(bot.Body)) return;
        var world = _origin.ToWorld(e, n, 0);
        world.Y = (GroundWorld(world) ?? (float?)(GameOfBot(id)?.Site?.GroundAt(e, n)) ?? world.Y) + 0.6f;
        bot.Body.PlaceAt(world, bot.Body.Rotation.Y);
        bot.Body.Velocity = Vector3.Zero;
    }

    private Game? GameOfBot(long id)
    {
        foreach (var g in _games) if (g.S.Find(id) != null) return g;
        return null;
    }

    /// <summary><see cref="FootPlayer.PropWalk"/> on the server: a disguised bot walks at its prop's cap (a sprint's burst while it runs).</summary>
    private float? BotWalk(FootPlayer body)
    {
        if (_botBodies.Count == 0 || !_botBodies.TryGetValue(body, out var b)) return null;
        if (b.G.S.Phase is not (VsPhase.Hide or VsPhase.Seek) || b.G.S.Find(b.Id) is not { Disguised: true } e) return null;
        return Now < b.G.RunOf(b.Id).SprintUntil ? VsRules.SprintSpeed : VsProps.Of(e.Prop).WalkCap;
    }

    private void BotsRound(Game g)
    {
        _objectiveBot.Remove(g.S.Id);
        foreach (var e in g.S.Entrants.Where(e => e.Bot))
            if (BotDirector.Instance?.Get(e.Peer)?.Brain is VersteckisBrain brain) brain.NewRound(g.S.Round);
    }

    private void BotTick(double now)
    {
        // nothing per frame: the director thinks for the brains at their own pace
    }

    private void BotCaught(Game g, long hider, long by)
    {
        if (BotDirector.Instance?.Get(by)?.Brain is VersteckisBrain seeker) seeker.Caught(hider);
    }

    /// <summary>A seeker bot's sniff: the puffs handed to its brain (never an RPC to a bot's id).</summary>
    private void BotSniffed(long peer, List<double> es, List<double> ns)
    {
        if (BotDirector.Instance?.Get(peer)?.Brain is not VersteckisBrain brain) return;
        for (int i = 0; i < es.Count; i++) brain.Puff(es[i], ns[i]);
    }

    /// <summary>A sound in a game's area: a seeker bot hears it as a player would (Moo 60 m, thumps 25 m, the bell anywhere), placed within 10 m.</summary>
    private void BotHeard(Game g, double e, double n, VsVoice voice)
    {
        var s = g.S;
        if (s.Find(s.Seeker) is not { Bot: true } || BotDirector.Instance?.Get(s.Seeker) is not { Brain: VersteckisBrain brain } bot) return;
        float reach = voice switch { VsVoice.Thump => VsRules.SprintHeard, VsVoice.Bell => 10000f, VsVoice.Poof => 40f, _ => VsRules.MooHeard };
        var (be, bn) = _origin.ToLv95(bot.Body.GlobalPosition);
        if ((be - e) * (be - e) + (bn - n) * (bn - n) > reach * reach) return;
        double a = _rng.NextDouble() * Math.Tau, r = _rng.NextDouble() * 10;
        brain.Heard(e + Math.Cos(a) * r, n + Math.Sin(a) * r);
    }

    /// <summary>The decor's collision on the server, so a bot walks round the same bales a player does (<c>bots/server-collision</c>).</summary>
    private void BuildSolid(Game g)
    {
        g.Solid?.QueueFree();
        if (g.Site == null) return;
        var site = g.Site;
        g.Solid = VsDecorLayer.Solids(site, (e, n) =>
        {
            var w = _origin.ToWorld(e, n, 0);
            w.Y = GroundWorld(w) ?? site.GroundAt(e, n) ?? w.Y;
            return w;
        });
        AddChild(g.Solid);
    }
}
