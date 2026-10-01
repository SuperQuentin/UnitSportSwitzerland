using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Net;

/// <summary>
/// <c>godot --headless --path . -- --swarm N --connect host[:port] [--first F --total T] [--seed s] [--regions a,b,..] [--seconds S]</c>:
/// one process hosting N bot players for a load test (see <c>tools/loadtest.sh</c>). The plan is
/// made for all T bots of the test and this process hosts bots F..F+N-1 of it, so splitting a test
/// over several processes does not change who does what or put two cars on the same spot.
///
/// <para>
/// Every bot is its own <see cref="SceneMultiplayer"/> with its own ENet connection, rooted at
/// <c>/root/BotK</c>, under which the client's paths are mirrored (<c>BotK/Main/World/Players</c>,
/// <c>PlayerSpawner</c>, <c>Chat</c>, ...) so what the server sends resolves exactly as it does in a
/// real client. The server spawns a <see cref="FootPlayer"/> for every peer on every branch; the
/// bot's own one has its physics and feel switched off and is moved kinematically here, the
/// others are only receivers (no animation, no collision), which is what makes 16 bots fit in one
/// process.
/// </para>
///
/// <para>
/// Activities are deterministic from the seed: per region, a pack of cars at their real speed
/// profile along the main road (<see cref="RaceRoute"/>, <see cref="RaceLine.SpeedProfile"/>), a
/// pack of bikes, walkers, a few aircraft circling at altitude and someone standing still. A region
/// with no local road data drives a 700 m circle instead. <b>All bot driving is in
/// <see cref="Drive"/></b>: that is the one place to adapt when player replication changes.
/// </para>
/// </summary>
public partial class Swarm : Node
{
    private sealed record Region(string Name, double E, double N, float FallbackAltitude);

    // the first one gets the biggest share of bots and is where the observer sits (loadtest.sh --at)
    private static readonly Region[] Regions =
    {
        new("mollendruz", 2518038, 1167321, 1180),
        new("lisle", 2521250, 1163750, 680),
        new("montricher", 2518750, 1161750, 720),
        new("labbaye", 2514750, 1169250, 1010),
        new("riddes", 2583250, 1113250, 480),
        new("veigy", 2506500, 1125500, 420),
    };

    private enum Role { Car, Bike, Walker, Aircraft, Idle }

    private static readonly Role[] Pattern =
    {
        Role.Car, Role.Car, Role.Bike, Role.Walker, Role.Car, Role.Aircraft, Role.Bike,
        Role.Walker, Role.Car, Role.Idle, Role.Car, Role.Bike, Role.Walker,
    };

    private sealed class Track
    {
        public required RaceRoute Fwd, Back;
        public float Start;
        public Vector3 Centre;
    }

    private sealed class Bot
    {
        public int Index;
        public required Region Region;
        public Role Role;
        public RideKind Kind;
        public CarSpec? Car;
        public float Speed, Lateral, S, V, Phase, AirRadius, AirAlt, AirAngle;
        public int Dir = 1, Slot;
        public double Wait;
        public float[]? ProfileFwd, ProfileBack;
        public bool Placed;
        public SceneMultiplayer Api = null!;
        public Node3D Players = null!;
        public FootPlayer? Me;
        public bool Connected;
    }

    private readonly int _count, _first, _total;
    private readonly string _host;
    private readonly int _port;
    private readonly int _seed;
    private readonly Region[] _regions;
    private readonly double _seconds;

    private readonly List<Bot> _bots = new();
    private readonly Dictionary<Region, Track> _tracks = new();
    private double _t, _sinceReport, _sinceSpawn, _maxDt;
    private int _frames;
    private bool _quitting;

    private Swarm(int count, int first, int total, string host, int port, int seed, Region[] regions, double seconds)
    {
        Name = "Swarm";
        _count = count;
        _first = first;
        _total = Math.Max(total, first + count);
        _host = host;
        _port = port;
        _seed = seed;
        _regions = regions;
        _seconds = seconds;
    }

    public static Swarm? ParseArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        string? Arg(string flag)
        {
            int i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        if (Arg("--swarm") is not { } n || !int.TryParse(n, out int count) || count < 1) return null;
        var (host, port) = NetworkManager.ParseEndpoint(Arg("--connect") ?? "127.0.0.1");
        int seed = int.TryParse(Arg("--seed"), out int s) ? s : 1;
        var regions = Arg("--regions") is { } list
            ? list.Split(',').Select(r => Regions.FirstOrDefault(x => x.Name == r.Trim())).OfType<Region>().ToArray()
            : Regions;
        if (regions.Length == 0) regions = Regions;
        double seconds = double.TryParse(Arg("--seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) ? sec : 0;
        int first = int.TryParse(Arg("--first"), out int f) ? f : 0;
        int total = int.TryParse(Arg("--total"), out int t) ? t : 0;
        return new Swarm(count, first, total, host, port, seed, regions, seconds);
    }

    public override void _Ready()
    {
        // a real client runs at display rate; uncapped, a headless process would send every frame it can
        Engine.MaxFps = 60;
        PlanBots();
        _ = BuildTracksAsync();
    }

    /// <summary>Who does what where. Deterministic from the seed and the bot count.</summary>
    private void PlanBots()
    {
        var rng = new Random(_seed);
        // the first region takes ~40 %, the rest share the remainder round-robin
        int first = _regions.Length == 1 ? _total : Math.Max(1, (int)Math.Ceiling(_total * 0.4));
        var perRegion = new int[_regions.Length];
        var all = new List<Bot>();
        for (int k = 0; k < _total; k++)
        {
            int r = k < first ? 0 : 1 + (k - first) % (_regions.Length - 1);
            int j = perRegion[r]++;
            // each region starts at a different point of the pattern, so five bots are not five cars
            var role = Pattern[(j + r * 3) % Pattern.Length];
            var bot = new Bot { Index = k, Region = _regions[r], Role = role };
            switch (role)
            {
                case Role.Car:
                    bot.Car = CarCatalog.All[rng.Next(CarCatalog.All.Count)];
                    bot.Kind = bot.Car.Kind;
                    break;
                case Role.Bike:
                    bot.Kind = RideKind.RoadBike;
                    bot.Speed = (35f + 10f * (float)rng.NextDouble()) / 3.6f;
                    break;
                case Role.Walker:
                    bot.Speed = 1.5f + 3.5f * (float)rng.NextDouble();
                    bot.Lateral = rng.Next(2) == 0 ? -4f : 4f;
                    bot.S = -300f + 600f * (float)rng.NextDouble();
                    bot.Dir = rng.Next(2) == 0 ? 1 : -1;
                    break;
                case Role.Aircraft:
                    (bot.Kind, bot.Speed) = rng.Next(3) switch
                    {
                        0 => (RideKind.Plane, 55f),
                        1 => (RideKind.Helicopter, 35f),
                        _ => (RideKind.Paraglider, 11f),
                    };
                    bot.AirRadius = 800f + 700f * (float)rng.NextDouble();
                    bot.AirAlt = 300f + 400f * (float)rng.NextDouble();
                    bot.AirAngle = Mathf.Tau * (float)rng.NextDouble();
                    break;
                case Role.Idle:
                    bot.S = -50f + 100f * (float)rng.NextDouble();
                    bot.Lateral = 6f;
                    break;
            }
            all.Add(bot);
        }
        // the pack slot is the rank among the whole test's bots of that role and region
        foreach (var g in all.GroupBy(b => (b.Region, b.Role)))
        {
            int j = 0;
            foreach (var b in g) b.Slot = j++;
        }
        _bots.AddRange(all.Skip(_first).Take(_count));
        GD.Print($"[swarm] hosting bots {_first}..{_first + _count - 1} of {_total}");
        foreach (var g in _bots.GroupBy(b => b.Region))
            GD.Print($"[swarm] {g.Key.Name}: " + string.Join(", ", g.GroupBy(b => b.Role).Select(r => $"{r.Count()} {r.Key}")));
    }

    /// <summary>The main road of every region, or a circle where there is no local road data.</summary>
    private async Task BuildTracksAsync()
    {
        var source = new LocalChunkSource(TerrainPaths.FindChunkDir());
        var manifest = await source.LoadManifestAsync();
        var origin = manifest.Tiles.Count > 0
            ? new WorldOrigin(manifest.SuggestedOriginLv95.E, manifest.SuggestedOriginLv95.N)
            : WorldOrigin.SwissDefault();
        var tracks = new Dictionary<Region, Track>();
        foreach (var region in _bots.Select(b => b.Region).Distinct())
        {
            var centre = origin.ToWorld(region.E, region.N, region.FallbackAltitude);
            RaceRoute? route = null;
            try { route = await Task.Run(() => RaceRoute.BuildAsync(source, origin, centre)); }
            catch (Exception e) { GD.PushWarning($"[swarm] {region.Name}: route failed: {e.Message}"); }
            if (route == null || route.Length < 500f)
            {
                var pts = new List<Vector3>();
                const float R = 700f;
                for (int i = 0; i <= 2200; i++)
                {
                    float a = Mathf.Tau * i / 2200f;
                    pts.Add(centre + new Vector3(R * Mathf.Cos(a), 0, R * Mathf.Sin(a)));
                }
                route = RaceRoute.FromPoints(pts, pts.Select(_ => 8f).ToList());
                GD.Print($"[swarm] {region.Name}: no local roads, driving a {R:F0} m circle");
            }
            else GD.Print($"[swarm] {region.Name}: {route.Class} route, {route.Length:F0} m");
            var back = RaceRoute.FromPoints(Enumerable.Reverse(route.Centre).ToList(), Enumerable.Reverse(route.Width).ToList(), route.Class);
            float start = route.Line.Arc[route.Line.Points.Select((p, i) => (d: RaceRoute.Flat(p - centre).Length(), i)).Min().i];
            tracks[region] = new Track { Fwd = route, Back = back, Start = start, Centre = route.Line.PointAt(start) };
        }
        Callable.From(() =>
        {
            foreach (var (r, t) in tracks) _tracks[r] = t;
            foreach (var b in _bots.Where(b => b.Car != null))
            {
                var t = _tracks[b.Region];
                // the top speed comes out of the car's own power, gearing and drag now
                b.ProfileFwd = t.Fwd.Line.SpeedProfile(b.Car!, false, 0.85f);
                b.ProfileBack = t.Back.Line.SpeedProfile(b.Car!, false, 0.85f);
            }
        }).CallDeferred();
    }

    public override void _Process(double delta)
    {
        _t += delta;
        _frames++;
        _maxDt = Math.Max(_maxDt, delta);
        _sinceSpawn += delta;
        // one connection every 50 ms: sixteen handshakes at once is not what a server sees in practice
        if (_bots.Count(b => b.Api != null) < _bots.Count && _sinceSpawn >= 0.05)
        {
            _sinceSpawn = 0;
            Connect(_bots.First(b => b.Api == null));
        }

        _sinceReport += delta;
        if (_sinceReport >= 10)
        {
            GD.Print(string.Format(CultureInfo.InvariantCulture,
                "[swarm] t={0:F0}s connected={1}/{2} driving={3} fps={4:F0} max frame={7:F0}ms ws={5:F0}MB heap={6:F0}MB",
                _t, _bots.Count(b => b.Connected), _bots.Count, _bots.Count(b => b.Placed),
                _frames / _sinceReport, System.Environment.WorkingSet / 1048576.0, GC.GetTotalMemory(false) / 1048576.0, _maxDt * 1000));
            _sinceReport = 0;
            _maxDt = 0;
            _frames = 0;
        }

        if (_seconds > 0 && _t >= _seconds && !_quitting)
        {
            _quitting = true;
            GD.Print("[swarm] --seconds reached, disconnecting");
            // one at a time: the server announces each departure to everyone still connected,
            // and a peer that closed in the same instant is not connected any more
            for (int i = 0; i < _bots.Count; i++)
            {
                var bot = _bots[i];
                GetTree().CreateTimer(0.1 * i).Timeout += () =>
                {
                    if (GetTree().Root.GetNodeOrNull($"Bot{bot.Index}") is { } branch) branch.ProcessMode = ProcessModeEnum.Disabled;
                    bot.Api?.MultiplayerPeer?.Close();
                };
            }
            GetTree().CreateTimer(0.1 * _bots.Count + 0.5).Timeout += () => GetTree().Quit();
        }
    }

    /// <summary>Builds bot K's branch of the tree and connects it.</summary>
    private void Connect(Bot bot)
    {
        string rootName = $"Bot{bot.Index}";
        bot.Api = new SceneMultiplayer();
        // before the branch enters the tree: spawners and synchronizers register with the
        // multiplayer that owns their path at that moment
        GetTree().SetMultiplayer(bot.Api, "/root/" + rootName);

        var root = new Node { Name = rootName };
        var main = new Node { Name = "Main" };
        var world = new Node3D { Name = "World" };
        root.AddChild(main);
        main.AddChild(world);
        bot.Players = new Node3D { Name = "Players" };
        bot.Players.ChildEnteredTree += node =>
        {
            if (node is FootPlayer p) p.Ready += () => Tame(bot, p);
        };
        // before the players: each one's synchronizer asks it whom to send to
        InterestService.CreateClient(world);
        world.AddChild(World.RaceNpcs.CreateClient());
        world.AddChild(bot.Players);
        world.AddChild(PlayerReplication.CreateSpawner());
        // the real client-side managers where they are plain per-branch nodes, stubs where the
        // class is a per-process singleton (Instance) that a second copy would clobber
        world.AddChild(ChatManager.CreateClient());
        world.AddChild(World.RaceManager.CreateClient());
        // ChunkStream only answers requests, and a bot makes none
        foreach (string stub in new[] { "Vehicles", "Combat", "Interiors", "Loot", "ChunkStream" })
            world.AddChild(new Node { Name = stub });
        world.AddChild(new SwarmOccasions { Name = "Occasions" });
        world.AddChild(new Birds.BirdNet { Name = Birds.BirdNet.NodeName });   // takes the bird snapshots, draws nothing
        world.AddChild(new MultiplayerSpawner { Name = "VehicleSpawner", SpawnPath = new NodePath("../Vehicles") });
        GetTree().Root.AddChild(root);

        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateClient(_host, _port);
        if (err != Error.Ok) { GD.PushError($"[swarm] bot {bot.Index}: cannot connect: {err}"); return; }
        bot.Api.MultiplayerPeer = peer;
        bot.Api.ConnectedToServer += () => bot.Connected = true;
        bot.Api.ConnectionFailed += () => GD.PushError($"[swarm] bot {bot.Index}: connection failed");
        bot.Api.ServerDisconnected += () =>
        {
            bot.Connected = false;
            bot.Me = null;
            // what is left of the branch would keep asking a closed peer who it is
            root.ProcessMode = ProcessModeEnum.Disabled;
            // loadtest.sh counts these lines
            if (!_quitting) GD.Print($"[swarm] bot {bot.Index}: server disconnected");
        };
    }

    /// <summary>
    /// Once a player node is ready: its own player is moved from here, so no physics, camera
    /// feel or sound; everyone else's is only a receiver, so no animation and no collision.
    /// </summary>
    private void Tame(Bot bot, FootPlayer p)
    {
        p.SetProcess(false);
        p.SetPhysicsProcess(false);
        p.CollisionLayer = 0;
        p.CollisionMask = 0;
        p.GetNodeOrNull("HeldItem")?.SetProcess(false);
        if (!p.IsMultiplayerAuthority()) return;
        foreach (var child in p.GetChildren())
            if (child is PlayerFeel) child.QueueFree();
        bot.Me = p;
        p.RideKindId = (int)bot.Kind;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_quitting) return;
        foreach (var bot in _bots)
            if (bot.Me != null && GodotObject.IsInstanceValid(bot.Me) && _tracks.TryGetValue(bot.Region, out var track))
                Drive(bot, track, (float)delta);
    }

    /// <summary>
    /// What a real owner's FootPlayer._Process writes for the synchronizer: the transform travels
    /// as NetPos/NetVel/NetYaw stamped with NetTime (last), not as position/rotation.
    /// </summary>
    private static void PublishNet(FootPlayer me, Vector3 before, float dt)
    {
        me.NetPos = me.Position;
        me.NetVel = dt > 0 ? (me.Position - before) / dt : Vector3.Zero;
        me.NetYaw = me.Rotation.Y;
        me.NetTime = Time.GetTicksUsec() / 1e6;
    }

    /// <summary>
    /// One physics step of one bot: moves its player and writes every replicated field the owner
    /// of a real player would. The one place to change when player replication changes.
    /// </summary>
    private void Drive(Bot bot, Track track, float dt)
    {
        var me = bot.Me!;
        var before = me.Position;
        if (!bot.Placed)
        {
            bot.Placed = true;
            int j = bot.Slot;
            bot.S = bot.Role switch
            {
                Role.Car => track.Start - 30f * j,             // single file, 30 m apart
                Role.Bike => track.Start + 60f + 5f * j,       // a bunch just ahead of the cars
                _ => track.Start + bot.S,
            };
            if (bot.Role == Role.Bike) bot.Lateral = (j % 2 == 0 ? -1f : 1f) * 0.8f;
            bot.S = Mathf.Clamp(bot.S, 5f, track.Fwd.Length - 5f);
            // a walker heading back starts on the reversed line, at the same spot
            if (bot.Dir < 0) bot.S = track.Back.Length - bot.S;
        }

        if (bot.Role == Role.Aircraft)
        {
            bot.AirAngle += bot.Speed / bot.AirRadius * dt;
            var c = track.Centre;
            me.Position = new Vector3(c.X + bot.AirRadius * Mathf.Cos(bot.AirAngle), c.Y + bot.AirAlt,
                c.Z + bot.AirRadius * Mathf.Sin(bot.AirAngle));
            var tangent = new Vector3(-Mathf.Sin(bot.AirAngle), 0, Mathf.Cos(bot.AirAngle));
            me.Rotation = new Vector3(0, Mathf.Atan2(-tangent.X, -tangent.Z), 0);
            PublishNet(me, before, dt);
            return;
        }

        var line = (bot.Dir > 0 ? track.Fwd : track.Back).Line;
        if (bot.Role != Role.Idle)
        {
            if (bot.Wait > 0)
            {
                bot.Wait -= dt;
                if (bot.Wait <= 0)
                {
                    // turn round at the end of the road: the other line starts where this one ended
                    bot.Dir = -bot.Dir;
                    line = (bot.Dir > 0 ? track.Fwd : track.Back).Line;
                    bot.S = 0;
                }
            }
            else
            {
                int i = line.IndexAt(bot.S);
                float target = bot.Role switch
                {
                    Role.Car => (bot.Dir > 0 ? bot.ProfileFwd : bot.ProfileBack)?[i] ?? 20f,
                    Role.Bike => Mathf.Min(bot.Speed, Mathf.Sqrt(3.5f / Mathf.Max(1e-4f, Mathf.Abs(line.Curvature[i])))),
                    _ => bot.Speed,
                };
                // brake to a stop at the end of the road, pull away gently at the start
                target = Mathf.Min(target, Mathf.Sqrt(2f * 5f * Mathf.Max(0f, line.Length - bot.S)));
                bot.V = Mathf.Min(target, bot.V + (bot.Role == Role.Car ? 5f : 1.5f) * dt);
                bot.S += bot.V * dt;
                if (line.Length - bot.S < 0.5f || (bot.V < 0.05f && line.Length - bot.S < 3f))
                {
                    bot.V = 0;
                    bot.Wait = 3;
                }
            }
        }

        var at = line.PointAt(bot.S);
        var fwd = RaceRoute.Flat(line.PointAt(bot.S + 2f) - line.PointAt(bot.S - 2f));
        fwd = fwd.LengthSquared() > 1e-6f ? fwd.Normalized() : Vector3.Forward;
        var right = fwd.Cross(Vector3.Up);
        me.Position = at + right * bot.Lateral + Vector3.Up * 0.1f;
        me.Rotation = new Vector3(0, Mathf.Atan2(-fwd.X, -fwd.Z), 0);
        PublishNet(me, before, dt);

        if (bot.Kind == RideKind.OnFoot)
        {
            // what PublishFootPose sends: speed and gait phase
            if (bot.V > 0.1f) bot.Phase = Avatar.HumanMeshBuilder.AdvancePhase(bot.Phase, bot.V, dt);
            me.PoseKind = FootPlayer.PoseStride;
            me.Anim = new Vector4(bot.V, bot.Phase, 0f, 0f);
        }
        else me.Anim = new Vector4(bot.V, 0f, 0f, 0f);
    }
}
