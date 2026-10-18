using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Bots;

/// <summary>
/// The server's bots (#837), at <c>World/Bots</c>: spawns and frees their bodies, runs their
/// brains, and tells the mode what happens to them. Nothing here knows a mode: Battle Royale
/// (<c>BrBots</c>) and later modes pick a bot's brain and listen to <see cref="Died"/> / <see cref="Downed"/>.
///
/// <para>
/// A bot's body is a <see cref="FootPlayer"/> the server runs itself (authority 1, spawn data
/// <c>PlayerReplication.BotData</c>, node <c>npc_1_n</c>, id -(1000 + n)): it walks with physics on
/// the server's collision, which the terrain builds only round such bodies
/// (<c>Terrain/CollisionReach</c>), and replicates through the server relays like any player.
/// Brains think less often far from every human (<see cref="FarThink"/>); the body itself always
/// moves at the physics rate.
/// </para>
/// </summary>
public partial class BotDirector : Node
{
    public const string NodeName = "Bots";
    /// <summary>At most this many bots at once on a server.</summary>
    public const int MaxBots = 64;
    /// <summary>A human within this sees a bot act: its brain runs at its own rate. Further, at most <see cref="FarThink"/>.</summary>
    public const float WatchedRange = 300f;
    /// <summary>Seconds between thoughts for a bot no human is near.</summary>
    public const double FarThink = 0.5;

    public static BotDirector? Instance { get; private set; }

    private MultiplayerSpawner _spawner = null!;
    private Node3D _players = null!;
    private readonly Dictionary<long, Bot> _bots = new();
    private BotWorld? _world;
    private double _watchAt;

    /// <summary>A bot fell for good or was knocked out: (bot, killer id or 0, cause). Raised on the server.</summary>
    public event Action<Bot, long, DamageCause>? Died;
    /// <summary>A bot went down, not out (squads, #475): (bot, attacker id).</summary>
    public event Action<Bot, long>? Downed;

    public static BotDirector CreateServer(MultiplayerSpawner spawner, Node3D players) =>
        new() { Name = NodeName, _spawner = spawner, _players = players };

    public override void _EnterTree()
    {
        Instance = this;
        Items.ItemEvents.ServerHeard += OnHeard;
    }

    /// <summary>A shot somewhere: every bot within its earshot hears it (<see cref="BotSenses.Hear"/>).</summary>
    private void OnHeard(long who, Items.ItemEventKind kind, GlobalPos at)
    {
        if (kind != Items.ItemEventKind.Shot || _bots.Count == 0) return;
        var origin = GetParent()?.GetNodeOrNull<Items.ItemEvents>(Items.ItemEvents.NodeName)?.Origin;
        if (origin == null) return;
        var world = origin.ToWorld(at);
        double now = Now;
        // the relay passes the kind, not the gun: a rifle's earshot for every shot
        float earshot = BotSenses.HearShot(Items.ItemId.Rifle);
        foreach (var bot in _bots.Values)
            if (bot.Alive) bot.Senses.Hear(bot, world, who, earshot, now);
    }

    /// <summary>
    /// <c>--bots N [--botsat E,N]</c>: N bots round that point (the default spawn) a second after the
    /// server starts, for the checks and for a quick look; <c>--botlog</c> prints where each one is
    /// every 5 s (<c>[bots] status</c>).
    /// </summary>
    public override void _Ready()
    {
        if (CmdArgs.Int("--bots") is int n && n > 0)
            GetTree().CreateTimer(1.0).Timeout += () => AddAround(SpawnPoint.ParseLv95("--botsat") ?? (SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N), n);
        _log = CmdArgs.Has("--botlog");
    }

    private bool _log;
    private double _logAt;

    /// <summary>
    /// <paramref name="count"/> bots scattered 5-20 m round an LV95 point, normal difficulty. They are
    /// put at sea level: a body drops onto the ground once its collision is built (never below it).
    /// </summary>
    public int AddAround((double E, double N) at, int count, BotDifficulty difficulty = BotDifficulty.Normal)
    {
        var rng = new Random(_seed++);
        var taken = _bots.Values.Select(b => b.Profile.Name).ToHashSet();
        int added = 0;
        for (int i = 0; i < count; i++)
        {
            double a = rng.NextDouble() * Math.Tau, r = 5 + rng.NextDouble() * 15;
            var pos = new GlobalPos(at.E + Math.Cos(a) * r, at.N + Math.Sin(a) * r, 0);
            if (Add(pos, (float)(rng.NextDouble() * Math.Tau), BotProfile.For(_seed, i, difficulty, taken)) != null) added++;
        }
        return added;
    }

    private void Log(double now)
    {
        if (!_log || now < _logAt) return;
        _logAt = now + 5;
        foreach (var bot in _bots.Values)
            if (GodotObject.IsInstanceValid(bot.Body))
                GD.Print($"[bots] status {bot.Body.Name} at {bot.Body.Global} v {bot.Body.Velocity.Length():F1} "
                    + $"floor {bot.Body.IsOnFloor()} {bot.Body.WalkState} hp {bot.Body.Health:F0}{(bot.Watched ? " watched" : "")} {bot.Brain.Status}");
    }
    public override void _ExitTree()
    {
        Items.ItemEvents.ServerHeard -= OnHeard;
        if (Instance == this) Instance = null;
    }

    public IReadOnlyCollection<Bot> All => _bots.Values;
    public int Count => _bots.Count;
    public Bot? Get(long id) => _bots.GetValueOrDefault(id);

    private static double Now => GameClock.Now;
    private InterestService? Interest => GetParent()?.GetNodeOrNull<InterestService>(InterestService.NodeName);

    /// <summary>Spawns a bot at <paramref name="at"/> (it drops onto the ground once its collision is built); null when full.</summary>
    public Bot? Add(GlobalPos at, float yaw, BotProfile profile)
    {
        if (_bots.Count >= MaxBots) return null;
        int n = 1;
        while (n <= 999 && _bots.ContainsKey(PlayerReplication.BotId(n))) n++;
        if (n > 999) return null;
        long id = PlayerReplication.BotId(n);
        if (_spawner.Spawn(PlayerReplication.BotData(n, at, yaw, profile.Name)) is not FootPlayer body) return null;

        var bot = new Bot(id, n, body, profile) { NextThink = Now, World = _world ??= new BotWorld(_players) };
        // the body reads no keys: its walk, jump, crouch come from the brain's intent
        body.WalkControls = () => (bot.Intent.Wish, bot.Intent.Run);
        body.FootActions = () => (bot.Intent.Jump, bot.Intent.Crouch);
        // in a car or on a bike: the pilot, else the handbrake (never the keys: a null RideControls reads them)
        body.RideControls = () => BotDriving.Controls(bot, (float)body.GetPhysicsProcessDeltaTime());
        // in the air: the wingsuit follows where it faces (LookYaw/LookPitch), the canopy its stick; the
        // deploy or cut-away press only while the ride is not the one wanted (a held press toggles every step)
        body.FlyControls = () => new Player.FlightInput(bot.Intent.Steer, 0f, 0f, 0f, 0f,
            Action: bot.Intent.Canopy ? body.Ride == RideKind.Wingsuit : body.Ride == RideKind.Parachute,
            Effort: false, ViewYaw: body.LookYaw, ViewPitch: body.LookPitch);
        body.Died += (killer, cause) =>
        {
            GD.Print($"[bots] {body.Name} \"{profile.Name}\" {(body.Eliminated ? "eliminated" : "knocked out")} by {killer} ({cause})");
            Died?.Invoke(bot, killer, cause);
        };
        body.WentDown += attacker => Downed?.Invoke(bot, attacker);
        _bots[id] = bot;
        GD.Print($"[bots] spawned {body.Name} \"{profile.Name}\" ({profile.Difficulty}) at {at}");
        return bot;
    }

    /// <summary>Frees a bot's body for everyone.</summary>
    public void Remove(long id)
    {
        if (!_bots.Remove(id, out var bot)) return;
        if (GodotObject.IsInstanceValid(bot.Body)) bot.Body.QueueFree();
        Interest?.ForgetPeer(id);
        GD.Print($"[bots] removed {PlayerReplication.NodeName(id)} \"{bot.Profile.Name}\"");
    }

    public void Clear()
    {
        foreach (long id in _bots.Keys.ToList()) Remove(id);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_bots.Count == 0) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        using (BodyProfiler.Time(BodyProfiler.Slot.Brains, true)) RunBots();
        ServerStats.Ran("bots", t0);
        BodyProfiler.Report(Now);
    }

    private void RunBots()
    {
        double now = Now;
        Log(now);
        if (now >= _watchAt)
        {
            _watchAt = now + 0.5;
            UpdateWatched();
        }
        foreach (var bot in _bots.Values)
        {
            if (!GodotObject.IsInstanceValid(bot.Body)) continue;
            if (now >= bot.NextThink)
            {
                double wait = bot.Brain.Think(bot, now);
                bot.NextThink = now + (bot.Watched ? wait : Math.Max(wait, FarThink));
            }
            // the facing: where it aims or looks, else where it walks (third person's FaceTravel)
            if (bot.Intent.FaceYaw is { } yaw)
            {
                bot.Body.LookYaw = yaw;
                bot.Body.LookPitch = bot.Intent.Pitch;
                bot.Body.SquareToView = true;
            }
            else bot.Body.SquareToView = false;
            // the hands: a shot when the brain asks for one and the weapon is ready; a use runs its time
            BotHands.Tick(bot, now);
            if (bot.Intent.FireAt is { } target) BotHands.Fire(bot, target);
            else if (bot.Body.ItemAction == 1 && now >= bot.NextShotAt + 1.5) bot.Body.ItemAction = 0;   // arms down after a while
        }
        // a body freed under us (a teardown, a kick of the node): forget it
        foreach (var gone in _bots.Where(kv => !GodotObject.IsInstanceValid(kv.Value.Body)).Select(kv => kv.Key).ToList())
            _bots.Remove(gone);
    }

    /// <summary>Which bots a human is near (players' proxies, never other bots or race NPCs).</summary>
    private void UpdateWatched()
    {
        foreach (var bot in _bots.Values) bot.Watched = false;
        foreach (var node in _players.GetChildren())
        {
            if (node is not FootPlayer human || human.Npc) continue;
            var at = human.GlobalPosition;
            foreach (var bot in _bots.Values)
                if (!bot.Watched && GodotObject.IsInstanceValid(bot.Body)
                    && bot.Body.GlobalPosition.DistanceSquaredTo(at) < WatchedRange * WatchedRange)
                    bot.Watched = true;
        }
    }

    // ---- /bot (admin): bots in free roam, for trying them out and for the checks ----

    private int _seed = System.Environment.TickCount;

    /// <summary>
    /// <c>/bot add [n] [easy|normal|hard]</c> round the sender, <c>/bot clear</c>, <c>/bot list</c>.
    /// Battle Royale fills its field with <c>/br bots</c> instead.
    /// </summary>
    public string Command(long sender, string rest, bool admin, Func<long, FootPlayer?> bodyOf)
    {
        var args = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        if (verb == "list")
            return _bots.Count == 0 ? "No bots." : $"{_bots.Count} bot(s): " + string.Join(", ", _bots.Values.Select(b =>
                $"{b.Profile.Name} ({PlayerReplication.NodeName(b.Id)}{(b.Alive ? "" : ", out")})"));
        if (!admin) return "Only an admin can add or clear bots.";
        switch (verb)
        {
            case "clear":
                int had = _bots.Count;
                Clear();
                return $"Removed {had} bot(s).";
            case "add":
                int count = 1;
                var difficulty = BotDifficulty.Normal;
                foreach (string a in args.Skip(1))
                {
                    if (int.TryParse(a, out int c)) count = Math.Clamp(c, 1, 16);
                    else if (BotProfile.ParseDifficulty(a) is { } d) difficulty = d;
                    else return "Usage: /bot add [n] [easy|normal|hard]";
                }
                if (bodyOf(sender) is not { } me || me.Origin is not { } origin) return "'/bot add' needs your body in the world.";
                var rng = new Random(_seed++);
                var taken = _bots.Values.Select(b => b.Profile.Name).ToHashSet();
                int added = 0;
                for (int i = 0; i < count; i++)
                {
                    double a = rng.NextDouble() * Math.Tau, r = 6 + rng.NextDouble() * 10;
                    var at = origin.ToGlobal(me.GlobalPosition + new Vector3((float)(Math.Cos(a) * r), 1.5f, (float)(Math.Sin(a) * r)));
                    if (Add(at, (float)(rng.NextDouble() * Math.Tau), BotProfile.For(_seed, i, difficulty, taken)) != null) added++;
                }
                return added == count ? $"Added {added} bot(s)." : $"Added {added} of {count} bot(s) (at most {MaxBots}).";
            case "arm":
            {
                // /bot arm <weapon> [rounds]: every bot takes it (a check, a test of the hands)
                string name = args.Length > 1 ? args[1] : "pistol";
                if (!Enum.TryParse<Items.ItemId>(name, true, out var weapon) || Items.Weapons.Get(weapon) == null)
                    return $"'{name}' is not a weapon (pistol, rifle, huntingrifle, shotgun, knife).";
                int rounds = args.Length > 2 && int.TryParse(args[2], out int r) ? Math.Clamp(r, 0, 300) : 30;
                foreach (var bot in _bots.Values) BotHands.Arm(bot, weapon, rounds);
                return $"{_bots.Count} bot(s) armed with a {weapon}.";
            }
            case "fire":
            {
                // /bot fire: every bot in reach shoots once at the sender's chest (a check of the hands)
                if (bodyOf(sender) is not { } target) return "'/bot fire' needs your body in the world.";
                int fired = 0;
                foreach (var bot in _bots.Values)
                    if (bot.Alive && BotHands.Pick(bot) is var (_, w)
                        && bot.Body.GlobalPosition.DistanceTo(target.GlobalPosition) <= w.Range
                        && BotHands.Fire(bot, target.GlobalPosition + Vector3.Up * 1.2f))
                        fired++;
                return $"{fired} bot(s) fired.";
            }
            case "drive":
            {
                // /bot drive [metres]: every bot to a point that far ahead of the sender, by car or bike if one is in sight (#837)
                if (bodyOf(sender) is not { } driver) return "'/bot drive' needs your body in the world.";
                float metres = args.Length > 1 && float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float m) ? Math.Clamp(m, 50f, 3000f) : 400f;
                var ahead = -driver.GlobalTransform.Basis.Z with { Y = 0 };
                var goal = driver.GlobalPosition + (ahead.LengthSquared() > 0.01f ? ahead.Normalized() : Vector3.Forward) * metres;
                foreach (var bot in _bots.Values) bot.Brain = new DriveBrain(goal);
                return $"{_bots.Count} bot(s) off to a point {metres:F0} m ahead of you.";
            }
            default:
                return "Usage: /bot add [n] [easy|normal|hard] | arm <weapon> [rounds] | fire | drive [metres] | clear | list";
        }
    }
}
