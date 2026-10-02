using System.Text.RegularExpressions;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.World;

/// <summary>
/// Races between connected players, in anything: on foot, bike, skis, a car, a motorbike, or in
/// the air (plane, helicopter, paraglider, wingsuit). One class on both sides at <c>World/Race</c>
/// — Godot routes RPCs by node path, like <c>World/Chat</c>.
///
/// <para>
/// <b>Any number of races run at once</b> (<c>Dictionary&lt;int, Race&gt;</c> on the server), each
/// with 2..32 entrants; an entrant is in at most one, and every RPC names the race. An entrant is
/// a <c>long</c>: a peer id, or an NPC run by a client, <c>-(ownerPeer * 1000 + n)</c> — the owner
/// reports its NPCs' checkpoints, and the server checks the id encodes the sender.
/// </para>
///
/// <para>
/// <b>The server decides everything that matters</b>: the course (<see cref="RaceCourse"/> — the
/// main road from the host, or a line of gates it builds from its own terrain files), who is in,
/// the grid, the start instant and every finish time, measured on its own clock. A client only
/// reports which checkpoint it passed (accepted in order only — a shortcut misses one) and that it
/// crossed the line. The countdown goes out in SECONDS, never a clock time: the machines' clocks
/// need not agree.
/// </para>
///
/// <para>
/// <b>Every race has its own frame</b> (#185): the server's players can be anywhere and its own
/// origin far from all of them, so a race is built and checked in an <see cref="OriginFrame"/>
/// anchored at its host, and its road and gates go out as float offsets from that anchor. A client
/// maps them into its own origin, which moves while it races: its courses <c>Follow</c> every shift.
/// </para>
/// </summary>
public partial class RaceManager : Node, IOriginShiftAware
{
    public const string NodeName = "Race";
    public const int MinEntrants = 2, MaxEntrants = 32;
    /// <summary>The mount value for "everyone keeps what they have".</summary>
    public const int Open = -1;
    private const double EntryWindow = 15.0, Countdown = 5.0;

    private static bool IsAirMount(int k) => k is (int)RideKind.Plane or (int)RideKind.Helicopter
        or (int)RideKind.Paraglider or (int)RideKind.Wingsuit;

    /// <summary>The peer an entrant id encodes: itself, or the client that asked for that NPC.</summary>
    public static long OwnerOf(long entrant) => entrant > 0 ? entrant : -entrant / 1000;

    /// <summary>
    /// Server: the peer that runs an entrant right now — itself, or the client currently simulating
    /// that NPC, which the server moves between clients (<see cref="RaceNpcs"/>, #50). Reports about
    /// an NPC are accepted from it alone, and its setup and result go to it.
    /// </summary>
    private long SimOf(long entrant) => entrant > 0 ? entrant : Npcs?.SimulatorOf(entrant) ?? OwnerOf(entrant);

    /// <summary>Server: the race an entrant is in (0: none, or finished).</summary>
    public int RaceOf(long entrant) => _raceOf.GetValueOrDefault(entrant);

    // ====================================================================================
    // server
    // ====================================================================================

    private enum Phase { Building, Entry, Running }

    private sealed class Race
    {
        public int Id;
        public long Host;
        /// <summary>A duel: the one peer who may join. 0 for an open race.</summary>
        public long Invited;
        public bool Air;
        public int Mount;
        /// <summary>A car class (<see cref="CarSetups"/> id, #40) every car entrant races in, or −1: any.</summary>
        public int Class = -1;
        /// <summary>The preset NPCs asked for with <c>class=</c> get in a race of any class.</summary>
        public int NpcSetup;
        public float Metres;
        public Phase Phase;
        /// <summary>The frame everything below is in: anchored at the host when it was opened.</summary>
        public OriginFrame Frame = null!;
        public RaceRoute? Route;
        public RaceCourse? Course;
        /// <summary>Ground: the road sent with every setup (kept to set up an NPC that changes simulator).</summary>
        public Vector3[] Centre = System.Array.Empty<Vector3>();
        public float[] Width = System.Array.Empty<float>();
        public double EntryEnds, StartAt, Deadline;
        public readonly List<long> Entrants = new();
        public readonly Dictionary<long, int> Next = new();
        /// <summary>Each NPC's driving skill this race (see <see cref="DrawSkills"/>).</summary>
        public readonly Dictionary<long, float> Skill = new();
        /// <summary>Air: when each entrant flew through gate 0 — its own start.</summary>
        public readonly Dictionary<long, double> Started = new();
        /// <summary>When each entrant's last accepted checkpoint came in, for the pace check.</summary>
        public readonly Dictionary<long, double> LastReport = new();
        public readonly Dictionary<long, double> Finished = new();
        /// <summary>Left, disconnected, or crossed the line with checkpoints missed.</summary>
        public readonly HashSet<long> Out = new();
        /// <summary>Waiting for another race's field to leave a shared start line.</summary>
        public bool Held;
        public double HoldUntil;
        /// <summary>NPCs still driving to their slots (#51): GO waits for them, until <see cref="ArriveBy"/>.</summary>
        public readonly HashSet<long> Arriving = new();
        public double ArriveBy;
        /// <summary>NPCs asked for while the road was still being found: spawned once it is.</summary>
        public readonly List<(long Owner, int Count, int Mount, bool Duel)> PendingNpcs = new();
        /// <summary>Where it was opened: "near the race" before any entrant is placed.</summary>
        public Vector3 Spot;
        public string What => $"{(Air ? "air " : "")}{MountName(Mount)}{(Class >= 0 ? $" {CarSetups.Slug(CarSetups.For(Class))}" : "")} {(Invited != 0 ? "duel" : "race")}";
    }

    private bool _server;
    private ChatManager? _chat;
    private Node3D? _players;
    private IChunkSource? _source;
    /// <summary>Client: this peer's origin, which the courses the server sends are mapped into.</summary>
    private WorldOrigin? _origin;
    private double _clock;
    private int _nextId = 1;
    private readonly Dictionary<int, Race> _races = new();
    /// <summary>The race each entrant is in, while it is in one (not after its finish).</summary>
    private readonly Dictionary<long, int> _raceOf = new();
    private readonly Dictionary<long, string> _npcNames = new();
    private readonly Dictionary<long, string> _names = new();

    /// <summary>
    /// Server: vehicles races handed out, per simulating peer. Conjuring a vehicle is an admin's on a
    /// server (<see cref="Core.Permissions"/>), but a race puts every entrant — player or NPC — on its
    /// machine, so whoever runs it may leave that many in the world afterwards
    /// (<see cref="Vehicles.VehicleManager.MayPark"/> takes one per park, <see cref="TakeIssued"/>).
    /// </summary>
    private readonly Dictionary<long, int> _issued = new();

    /// <summary>Server: uses up one vehicle a race gave this peer. False if it has none left.</summary>
    public bool TakeIssued(long peer)
    {
        if (_issued.GetValueOrDefault(peer) <= 0) return false;
        _issued[peer]--;
        return true;
    }

    public static RaceManager CreateServer(ChatManager chat, Node3D players, IChunkSource source) => new()
    {
        Name = NodeName, _server = true, _chat = chat, _players = players, _source = source,
    };

    public static RaceManager CreateClient(WorldOrigin origin) => new() { Name = NodeName, _origin = origin };

    /// <summary>Both are entrants of the same race, after its entry closed and before they finish.</summary>
    public bool SameRace(long a, long b) =>
        _raceOf.TryGetValue(a, out int ra) && _raceOf.TryGetValue(b, out int rb) && ra == rb
        && _races.TryGetValue(ra, out var race) && race.Phase == Phase.Running;

    /// <summary><c>/race ...</c>, from the chat. Returns the reply for the sender.</summary>
    public string Command(long sender, string args)
    {
        var parts = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "help";
        int? id = parts.Length > 1 && int.TryParse(parts[1].TrimStart('#'), out int n) ? n : null;
        return "[race] " + verb switch
        {
            "start" => Start(sender, parts[1..], 0),
            "duel" when parts.Length > 1 && parts[1].Equals("npc", System.StringComparison.OrdinalIgnoreCase) => Npc(sender, parts[2..], duel: true),
            "duel" => Duel(sender, parts),
            "npc" => Npc(sender, parts[1..], duel: false),
            "join" => Join(sender, id),
            "leave" => Leave(sender),
            "cancel" => Cancel(sender, id),
            "list" => List(),
            _ => "/race start [metres] [mount|open] [class=<preset>]  /race start air <place|metres> [mount]  /race duel <player|npc> [...]  "
                + "/race npc [n] [metres] [mount]  /race join [id]  /race leave  /race cancel [id]  /race list — mounts: foot bike skis car <car> moto monster "
                + "plane heli paraglider wingsuit — classes: " + string.Join(' ', CarSetups.All.Skip(1).Select(CarSetups.Slug)),
        };
    }

    private string Duel(long sender, string[] parts)
    {
        if (parts.Length < 2) return "Usage: /race duel <player> [metres | air <place|metres>] [mount]";
        long peer = _chat?.PeerByName(parts[1]) ?? -1;
        if (peer <= 0) return $"No player matching '{parts[1]}'.";
        if (peer == sender) return "You cannot duel yourself.";
        if (_raceOf.TryGetValue(peer, out int busy)) return $"{Who(peer)} is already in race #{busy}.";
        return Start(sender, parts[2..], peer);
    }

    private string Start(long sender, string[] args, long invited)
    {
        if (_raceOf.TryGetValue(sender, out int busy)) return $"You are already in race #{busy} — /race leave first.";
        if (_players?.GetNodeOrNull<FootPlayer>(sender.ToString()) is not { } host) return "You have no position yet.";

        bool air = args.Length > 0 && args[0].Equals("air", System.StringComparison.OrdinalIgnoreCase);
        var words = new List<string>(air ? args[1..] : args);
        var (cls, classError) = TakeClass(words);
        if (classError != null) return classError;
        int mount = air ? (int)RideKind.Plane : (int)CarCatalog.All[0].Kind;
        if (words.Count > 0 && ParseMount(words[^1]) is { } m) { mount = m; words.RemoveAt(words.Count - 1); }
        if (mount != Open && IsAirMount(mount) != air)
            return air ? $"{MountName(mount)} is not an air mount." : $"For {MountName(mount)} use /race start air <place|metres> {MountName(mount)}.";
        if (mount != Open && mount != (int)RideKind.OnFoot && Rideable.Create((RideKind)mount) == null)
            return $"There is no {MountName(mount)} on this server yet.";

        string rest = string.Join(' ', words);
        bool isNumber = float.TryParse(rest, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float metres);
        // the race's own frame, at the host: the server's origin may be anywhere (#185)
        var frame = OriginFrame.AnchorNear(host.Global);
        var at = frame.ToWorld(host.Global);
        Vector3 target = default;
        if (air)
        {
            if (rest.Length == 0) return "Usage: /race start air <place|metres> [mount]";
            if (isNumber)
            {
                // along where the host is facing
                var fwd = RaceRoute.Flat(-host.GlobalBasis.Z);
                if (fwd.LengthSquared() < 1e-4f) fwd = Vector3.Forward;
                target = at + fwd.Normalized() * Mathf.Clamp(metres, 800f, RaceCourse.MaxAirDistance);
            }
            else
            {
                var found = _chat?.Places?.Search(rest, limit: 1);
                if (found == null || found.Count == 0) return $"No place matching '{rest}'.";
                target = frame.ToWorld(found[0].E, found[0].N, at.Y);
            }
        }
        else if (rest.Length > 0 && !isNumber) return $"Unknown mount or distance '{rest}'. /race help";

        var race = new Race
        {
            Id = _nextId++, Host = sender, Invited = invited, Air = air, Mount = mount, Phase = Phase.Building, Spot = at, Frame = frame,
            Class = cls?.Id ?? -1, NpcSetup = cls?.Id ?? 0,
            Metres = air ? 0 : isNumber ? Mathf.Clamp(metres, 300f, 8000f) : 2000f,
        };
        _races[race.Id] = race;
        Enter(race, sender);

        var source = _source!;
        int raceId = race.Id;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            if (air)
            {
                var (course, why) = await RaceCourse.BuildAirAsync(source, frame, at, target, (RideKind)(mount == Open ? (int)RideKind.Plane : mount));
                Callable.From(() => Opened(raceId, null, course, why)).CallDeferred();
            }
            else
            {
                var route = await RaceRoute.BuildAsync(source, frame, at);
                Callable.From(() => Opened(raceId, route, null, "")).CallDeferred();
            }
        });
        return $"#{race.Id} {(air ? "plotting the gates" : "finding the road")}…";
    }

    /// <summary>Takes a <c>class=&lt;preset&gt;</c> word out of a command (#40): the preset, and an error for an unknown one.</summary>
    private static (CarSetup? Class, string? Error) TakeClass(List<string> words)
    {
        int i = words.FindIndex(w => w.StartsWith("class=", System.StringComparison.OrdinalIgnoreCase));
        if (i < 0) return (null, null);
        string name = words[i][6..];
        words.RemoveAt(i);
        return CarSetups.Parse(name) is { } c ? (c, null)
            : (null, $"No car class '{name}'. Classes: {string.Join(' ', CarSetups.All.Select(CarSetups.Slug))}");
    }

    private void Opened(int id, RaceRoute? route, RaceCourse? course, string why)
    {
        if (!_races.TryGetValue(id, out var race) || race.Phase != Phase.Building) return;
        if (race.Air ? course == null : route == null || RaceCourse.MaxLength(route, MinEntrants) < 300f)
        {
            End(race, race.Air ? why : "no road long enough here to race on", ChatKind.Error);
            return;
        }
        race.Route = route;
        race.Course = course;
        // NPCs arrive along this road: they could not be placed before it was known (before the duel check: it reads Invited)
        foreach (var (owner, n, mount, duel) in race.PendingNpcs) SpawnNpcs(race, owner, n, mount, duel);
        race.PendingNpcs.Clear();
        race.Phase = Phase.Entry;
        race.EntryEnds = _clock + EntryWindow;
        float km = (race.Air ? course!.Length : Mathf.Min(race.Metres, RaceCourse.MaxLength(route!, MinEntrants))) / 1000f;
        if (race.Invited < 0)
            _chat?.Tell(race.Host, $"[race] #{id} {km:0.0} km {race.What} against {Who(race.Invited)}", ChatKind.System);
        else if (race.Invited != 0)
        {
            _chat?.Tell(race.Invited, $"[race] #{id} {Who(race.Host)} challenges you to a {km:0.0} km {race.What} — /race join {id} within {EntryWindow:0} s", ChatKind.System);
            _chat?.Tell(race.Host, $"[race] #{id} waiting for {Who(race.Invited)} to accept", ChatKind.System);
        }
        else
            _chat?.Broadcast($"[race] #{id} {Who(race.Host)} opens a {km:0.0} km {race.What} — /race join {id} within {EntryWindow:0} s",
                ChatKind.System);
    }

    private string Join(long sender, int? id)
    {
        if (_raceOf.TryGetValue(sender, out int busy)) return $"You are already in race #{busy} — /race leave first.";
        Race? race = id is { } i ? _races.GetValueOrDefault(i)
            : _races.Values.Where(r => r.Phase != Phase.Running && (r.Invited == sender || r.Invited == 0))
                .OrderByDescending(r => r.Invited == sender).ThenByDescending(r => r.Id).FirstOrDefault();
        if (race == null) return id == null ? "No race open. /race start to open one." : $"There is no race #{id}.";
        if (race.Phase == Phase.Running) return $"Race #{race.Id} has started.";
        if (race.Invited != 0 && race.Invited != sender) return $"Race #{race.Id} is a duel between {Who(race.Host)} and {Who(race.Invited)}.";
        if (race.Entrants.Count >= MaxEntrants) return $"Race #{race.Id} is full.";
        Enter(race, sender);
        _chat?.Broadcast($"[race] #{race.Id} {Who(sender)} joins ({race.Entrants.Count} in)", ChatKind.System);
        return $"#{race.Id} you are in{(race.Mount == Open ? "" : $" — {MountName(race.Mount)}")}.";
    }

    private void Enter(Race race, long entrant)
    {
        _names[entrant] = Who(entrant);   // kept: a racer who disconnects still has a name in the results
        race.Entrants.Add(entrant);
        _raceOf[entrant] = race.Id;
    }

    // ---- NPC entrants (issue #39): spawned by World/Npcs next to their owner, simulated on its client ----

    private RaceNpcs? Npcs => GetParent()?.GetNodeOrNull<RaceNpcs>(RaceNpcs.NodeName);

    /// <summary>
    /// <c>/race npc [n] [metres] [mount]</c>: n NPCs into the race the sender has open, or a new
    /// race with them. <c>/race duel npc [metres] [mount]</c>: a duel against one NPC. The mount is
    /// the race's, else the one asked for, else the sender's car, else the AE86.
    /// </summary>
    private string Npc(long sender, string[] args, bool duel)
    {
        int count = 1;
        // a small number is a count; a large one is the distance
        if (!duel && args.Length > 0 && int.TryParse(args[0], out int n) && n <= RaceNpcs.PerOwner) { count = Mathf.Max(1, n); args = args[1..]; }
        if (Npcs is not { } npcs || _players?.GetNodeOrNull<FootPlayer>(sender.ToString()) is not { } host) return "You have no position yet.";
        var race = _raceOf.TryGetValue(sender, out int id) ? _races.GetValueOrDefault(id) : null;
        var argList = args.ToList();
        var (cls, classError) = TakeClass(argList);
        if (classError != null) return classError;
        args = argList.ToArray();
        int? asked = args.Length > 0 ? ParseMount(args[^1]) : null;
        int mount = race is { Mount: not Open } ? race.Mount
            : asked is { } a and not Open ? a
            : CarCatalog.IsCar(host.Ride) ? (int)host.Ride : (int)CarCatalog.All[0].Kind;
        // GatePilot flies by pressing the input actions: it would fly the owner, not its NPC
        if (race?.Air == true || IsAirMount(mount) || args.Any(w => w.Equals("air", System.StringComparison.OrdinalIgnoreCase)))
            return "NPCs cannot fly yet: the air pilot steers through the owner's own keys.";
        if (!AutoPilot.Drives((RideKind)mount)) return $"No NPC pilot for {MountName(mount)} yet.";

        string reply;
        if (race != null)
        {
            if (duel) return $"You are already in race #{race.Id} — /race leave first.";
            if (race.Phase == Phase.Running || race.Invited != 0) return $"Race #{race.Id} is not open to NPCs.";
            reply = $"#{race.Id}";
            if (cls != null && race.Class < 0) race.NpcSetup = cls.Id;
        }
        else
        {
            var words = asked != null ? args[..^1] : args;
            reply = Start(sender, words.Append(MountName(mount)).Concat(cls != null ? new[] { $"class={cls.Id}" } : System.Array.Empty<string>()).ToArray(), 0);
            if (!_raceOf.TryGetValue(sender, out id)) return reply;   // refused
            race = _races[id];
        }
        // they drive in along the race road (#51), so they wait for it to be found
        if (race.Phase == Phase.Building)
        {
            race.PendingNpcs.Add((sender, duel ? 1 : count, mount, duel));
            return reply + " NPCs on their way.";
        }
        int spawned = SpawnNpcs(race, sender, duel ? 1 : count, mount, duel);
        return reply + (spawned == 0 ? " No room for more NPCs." : $" {spawned} NPC(s) on their way.");
    }

    /// <summary>Seconds GO waits at most for NPCs still driving to their slots.</summary>
    private const double ArriveWithin = 75;

    /// <summary>
    /// Spawns NPCs out of sight on the race road and tells their owner how each arrives
    /// (<see cref="NpcArrival"/>); without a road (never, on the ground) they appear behind the owner.
    /// </summary>
    private int SpawnNpcs(Race race, long owner, int count, int mount, bool duel)
    {
        if (Npcs is not { } npcs || _players?.GetNodeOrNull<FootPlayer>(owner.ToString()) is not { } host) return 0;
        List<NpcArrival.Entry>? plan = null;
        (Vector3[] Centre, float[] Width, float Zero) lane = default;
        if (race.Route != null)
        {
            lane = NpcArrival.Lane(race.Route);
            var road = RaceRoute.FromPoints(lane.Centre, lane.Width);
            var all = _players.GetChildren().OfType<FootPlayer>().ToList();
            plan = NpcArrival.Plan(road, lane.Zero, race.Entrants.Count, count, race.Entrants.Count + count,
                all.Where(p => !p.Npc).Select(p => race.Frame.ToWorld(p.Global)).ToList(), all.Select(p => race.Frame.ToWorld(p.Global)).ToList(),
                race.Id * 7919 + (int)(owner % 100000) * 31 + race.Entrants.Count);
        }
        var ids = npcs.Spawn(owner, count, (RideKind)mount, host.Global, host.Rotation.Y,
            plan == null ? null : i => (race.Frame.ToGlobal(plan[i].At), plan[i].Yaw), race.Class >= 0 ? race.Class : race.NpcSetup);
        if (duel && ids.Count == 1) race.Invited = ids[0];   // before the course is built: Opened reads it
        foreach (long npc in ids)
        {
            _npcNames[npc] = npcs.Label(npc);
            Enter(race, npc);
            if (!duel) _chat?.Broadcast($"[race] #{race.Id} {Who(npc)} joins ({race.Entrants.Count} in)", ChatKind.System);
        }
        if (plan == null) return ids.Count;
        for (int i = 0; i < ids.Count; i++)
        {
            var e = plan[i];
            race.Arriving.Add(ids[i]);
            RpcId(owner, MethodName.NpcArrive, race.Id, ids[i], race.Frame.E, race.Frame.N, lane.Centre, lane.Width, lane.Zero,
                (int)e.Style, e.Variant, e.Slot, race.Entrants.Count);
            GD.Print($"[npc] {Who(ids[i])} arrives {e.Style} for slot {e.Slot + 1}");
        }
        if (ids.Count > 0) race.ArriveBy = System.Math.Max(race.ArriveBy, _clock + ArriveWithin);
        return ids.Count;
    }

    /// <summary>The owner: one of its NPCs is in its slot, so GO need not wait for it any longer.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NpcStaged(int raceId, long npc)
    {
        if (!_server || SimOf(npc) != Multiplayer.GetRemoteSenderId() || !_races.TryGetValue(raceId, out var race)) return;
        if (race.Arriving.Remove(npc)) GD.Print($"[npc] {Who(npc)} is in its slot");
    }

    /// <summary>The simulator: one of its NPCs crashed out of the race — a DNF.</summary>
    public void RetireNpc(int raceId, long npc)
    {
        if (_server) NpcRetired(raceId, npc, 1);
        else RpcId(1, MethodName.NpcRetired, raceId, npc, 0);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NpcRetired(int raceId, long npc, int local)
    {
        long sender = local == 1 ? SimOf(npc) : Multiplayer.GetRemoteSenderId();
        if (!_server || SimOf(npc) != sender || !_races.TryGetValue(raceId, out var race) || race.Phase != Phase.Running
            || !race.Entrants.Contains(npc) || race.Out.Contains(npc)) return;
        Drop(race, npc);
        _chat?.Broadcast($"[race] #{raceId} {Who(npc)} crashed out: retires", ChatKind.System);
    }

    private string Leave(long sender)
    {
        if (!_raceOf.TryGetValue(sender, out int id) || !_races.TryGetValue(id, out var race)) return "You are not in a race.";
        Drop(race, sender);
        if (sender == race.Host) MigrateHost(race);
        return $"You are out of race #{id}.";
    }

    /// <summary>Takes an entrant out: off the grid before the start, DNF after it.</summary>
    private void Drop(Race race, long entrant)
    {
        _raceOf.Remove(entrant);
        if (race.Phase == Phase.Running) race.Out.Add(entrant);
        else race.Entrants.Remove(entrant);
        if (race.Phase == Phase.Running && Multiplayer.GetPeers().Contains((int)SimOf(entrant)))
            RpcId(SimOf(entrant), MethodName.Dropped, race.Id, entrant);
    }

    private string Cancel(long sender, int? id)
    {
        var race = id is { } i ? _races.GetValueOrDefault(i)
            : _races.Values.FirstOrDefault(r => r.Host == sender) ?? (sender == ChatManager.ConsolePeerId && _races.Count == 1 ? _races.Values.First() : null);
        if (race == null) return id == null ? "You host no race. /race cancel <id>" : $"There is no race #{id}.";
        if (sender != race.Host && sender != ChatManager.ConsolePeerId) return "Only the host can cancel.";
        End(race, "cancelled", ChatKind.System);
        return $"Race #{race.Id} cancelled.";
    }

    private string List()
    {
        if (_races.Count == 0) return "No races.";
        return string.Join("  |  ", _races.Values.Select(r =>
            $"#{r.Id} {Who(r.Host)}'s {r.What}, {r.Phase.ToString().ToLowerInvariant()}, {r.Entrants.Count} in"));
    }

    private void ServerTick(double delta)
    {
        _clock += delta;
        bool reviewHosts = (_hostReview += delta) >= 1.0;
        if (reviewHosts) _hostReview = 0;
        foreach (var race in _races.Values.ToList())
        {
            if (reviewHosts && (race.Host == 0 || _players?.GetNodeOrNull(race.Host.ToString()) == null))
            {
                MigrateHost(race);
                if (!_races.ContainsKey(race.Id)) continue;   // ended: nobody to take it
            }
            // entrants whose player (or NPC owner) left the server
            foreach (long e in race.Entrants.Where(e => !race.Out.Contains(e) && _players?.GetNodeOrNull(PlayerReplication.NodeName(e)) == null).ToList())
            {
                _raceOf.Remove(e);
                if (race.Phase == Phase.Running) race.Out.Add(e);
                else race.Entrants.Remove(e);
            }
            if (race.Phase == Phase.Entry && _clock >= race.HoldUntil
                && (_clock >= race.ArriveBy || !race.Arriving.Any(race.Entrants.Contains))
                && (_clock >= race.EntryEnds || (race.Invited != 0 && race.Entrants.Contains(race.Invited))))
                Go(race);
            else if (race.Phase == Phase.Running
                && (_clock > race.Deadline || race.Entrants.All(e => race.Finished.ContainsKey(e) || race.Out.Contains(e))))
                Results(race);
        }
    }

    // ---- the host role (#50): a race belongs to nobody either ----

    private double _hostReview;

    /// <summary>
    /// The host left (disconnected, or <c>/race leave</c>): the role passes to the nearest human
    /// entrant still here, else to the nearest player within <see cref="RaceNpcs.Zone"/> of the race
    /// (a spectator). A duel whose host leaves before GO ends. With nobody, a race not started yet
    /// ends; a running one goes on without a host (the console can still cancel it) while someone
    /// still runs an entrant — its NPCs retire with nobody near, and then it ends with its results.
    /// A vacant role is offered again every second.
    /// </summary>
    private void MigrateHost(Race race)
    {
        long old = race.Host;
        if (race.Invited != 0 && race.Phase != Phase.Running)
        {
            End(race, "the host left", ChatKind.System);
            return;
        }
        // "near the race": its entrants still on course, else where it was opened
        var field = race.Entrants.Where(e => e != old && !race.Out.Contains(e) && !race.Finished.ContainsKey(e))
            .Select(e => _players?.GetNodeOrNull<FootPlayer>(PlayerReplication.NodeName(e))).OfType<FootPlayer>()
            .Select(n => n.Global).ToList();
        if (field.Count == 0) field.Add(race.Frame.ToGlobal(race.Spot));
        long best = 0;
        (int, float) bestKey = default;
        foreach (var child in _players?.GetChildren() ?? new Godot.Collections.Array<Node>())
        {
            if (child is not FootPlayer { Npc: false } p || !long.TryParse(p.Name, out long peer) || peer == old) continue;
            bool entrant = race.Entrants.Contains(peer) && !race.Out.Contains(peer);
            float d = field.Min(f => RaceNpcs.Flat(f, p.Global));
            if (!entrant && d > RaceNpcs.Zone) continue;
            var key = (entrant ? 0 : 1, d);
            if (best == 0 || key.CompareTo(bestKey) < 0) { best = peer; bestKey = key; }
        }
        if (best != 0)
        {
            race.Host = best;
            _chat?.Broadcast($"[race] #{race.Id} is now hosted by {Who(best)}", ChatKind.System);
            GD.Print($"[race] #{race.Id} host {(old == 0 ? "vacant" : Who(old))} -> {Who(best)}");
            return;
        }
        if (race.Phase != Phase.Running) { End(race, "the host left", ChatKind.System); return; }
        if (old != 0) GD.Print($"[race] #{race.Id} has no host: nobody near the race");
        race.Host = 0;
    }

    /// <summary>Seconds after its GO a field is taken to have left its grid.</summary>
    private const double GridClearSeconds = 12;

    /// <summary>Grid slots of two races closer than this share tarmac.</summary>
    private const float GridClearance = 25f;

    /// <summary>A race still on (or just off) its grid whose slots overlap this one's, if any.</summary>
    private Race? GridBlockedBy(Race race, RaceCourse course, int count)
    {
        foreach (var other in _races.Values)
        {
            if (other == race || other.Phase != Phase.Running || other.Course == null) continue;
            if (_clock > other.StartAt + GridClearSeconds) continue;
            int theirs = other.Entrants.Count;
            var mine = race.Frame.Since(other.Frame);   // each race is in its own frame
            for (int i = 0; i < count; i++)
            {
                var (a, _) = course.Slot(i, count);
                for (int j = 0; j < theirs; j++)
                    if (a.DistanceTo(mine.Point(other.Course.Slot(j, theirs).At)) < GridClearance) return other;
            }
        }
        return null;
    }

    private void Go(Race race)
    {
        int count = race.Entrants.Count;
        if (count < MinEntrants) { End(race, $"not enough racers ({MinEntrants} needed)", ChatKind.System); return; }
        Vector3[] centre = System.Array.Empty<Vector3>(), gates = System.Array.Empty<Vector3>();
        float[] width = System.Array.Empty<float>();
        if (!race.Air)
        {
            var route = race.Route!;
            float length = Mathf.Min(race.Metres, RaceCourse.MaxLength(route, count));
            if (length < 300f) { End(race, $"the road is too short for {count} racers", ChatKind.System); return; }
            race.Course = RaceCourse.Ground(route, length);
            int last = route.NearestCentreIndexAt(RaceCourse.StartArc(0, count) + length + 60f);
            centre = route.Centre.Take(last + 1).ToArray();
            width = route.Width.Take(last + 1).ToArray();
        }
        else gates = race.Course!.Gates;
        var course = race.Course!;

        // Two races opened at the same spot would put both grids on the same tarmac: cars
        // spawned inside each other. A race is not outside the world's rules — the later one
        // waits until the earlier field has left the line, then lines up on the empty road.
        if (GridBlockedBy(race, course, count) is { } other)
        {
            if (!race.Held)
                _chat?.Broadcast($"[race] #{race.Id} waits for #{other.Id} to clear the start", ChatKind.System);
            race.Held = true;
            race.HoldUntil = other.StartAt + GridClearSeconds;
            return;
        }

        race.Phase = Phase.Running;
        race.StartAt = _clock + Countdown;
        // a generous limit: the whole distance at a slow pace for the class, plus the countdown
        race.Deadline = race.StartAt + (course.Length + RaceCourse.AirGridBack + 300f) / SlowPace(race.Mount) + 60f;
        race.Centre = centre;
        race.Width = width;
        if (race.Mount != Open && Rideable.Create((RideKind)race.Mount) is { IsVehicle: true })
            foreach (long e in race.Entrants)
                _issued[SimOf(e)] = _issued.GetValueOrDefault(SimOf(e)) + 1;
        DrawSkills(race);
        foreach (long e in race.Entrants)
        {
            race.Next[e] = 0;
            SendSetup(race, e, resume: false);
        }
        _chat?.Broadcast($"[race] #{race.Id} {count} on the grid: {string.Join(", ", race.Entrants.Select(Who))} — "
            + $"{course.Length / 1000f:0.0} km, GO in {Countdown:0} s", ChatKind.System);
    }

    /// <summary>
    /// The NPCs' drivers for this race, new every race: skills spread over 0.8..1.1 (see
    /// <see cref="AutoPilot.GridSkills"/>: one per band, shuffled, so a grid is never all slow cars)
    /// with an ace (≥ 1.05) in every grid. The server draws them so a handoff keeps the driver.
    /// </summary>
    private void DrawSkills(Race race)
    {
        var npcs = race.Entrants.Where(e => e < 0).ToList();
        if (npcs.Count == 0) return;
        var skills = AutoPilot.GridSkills(npcs.Count, new System.Random(race.Id * 7919 + (int)(_clock * 1000) % 7919));
        for (int i = 0; i < npcs.Count; i++) race.Skill[npcs[i]] = skills[i];
        GD.Print($"[race] #{race.Id} NPC skills: {string.Join(", ", npcs.Select(e => $"{Who(e)} {race.Skill[e]:F2}"))}");
    }

    /// <summary>
    /// An entrant's grid slot and course, to whoever runs it. <paramref name="resume"/>: an NPC
    /// that changed simulator (#50) — the new one reports from the checkpoint the server expects
    /// next, with the countdown left (negative once running).
    /// </summary>
    private void SendSetup(Race race, long e, bool resume)
    {
        var course = race.Course!;
        int slot = race.Entrants.IndexOf(e), count = race.Entrants.Count;
        RpcId(SimOf(e), MethodName.Setup, race.Id, e, race.Air, race.Frame.E, race.Frame.N, race.Centre, race.Width,
            race.Air ? course.Gates : System.Array.Empty<Vector3>(), course.Length, course.GridAltitude, slot, count,
            race.Mount, resume ? race.StartAt - _clock : Countdown, race.Next.GetValueOrDefault(e), resume, race.Class,
            race.Skill.GetValueOrDefault(e, 1f));
    }

    /// <summary>Server: an NPC changed simulator; the new one gets its race where it stands.</summary>
    public void ResumeNpc(long id)
    {
        if (!_raceOf.TryGetValue(id, out int r) || !_races.TryGetValue(r, out var race)) return;
        // still driving in to its slot (#51): the new simulator has no arrival plan — it waits
        // where it is, GO does not wait for it, and GO's setup puts it in its slot
        race.Arriving.Remove(id);
        if (race.Phase == Phase.Running && !race.Out.Contains(id) && !race.Finished.ContainsKey(id))
            SendSetup(race, id, resume: true);
    }

    private static float SlowPace(int mount) => mount switch
    {
        (int)RideKind.OnFoot or (int)RideKind.Skis or Open => 2f,
        (int)RideKind.RoadBike or (int)RideKind.Paraglider => 4f,
        (int)RideKind.Plane => 25f,
        (int)RideKind.Helicopter or (int)RideKind.Wingsuit => 15f,
        _ => 10f,   // cars, motorbikes
    };

    private void Results(Race race)
    {
        var order = race.Finished.OrderBy(kv => kv.Value).ToList();
        int pos = 0;
        foreach (var (e, time) in order)
            _chat?.Broadcast($"[race] #{race.Id} {++pos}. {Who(e)}  {Format(time)}", ChatKind.System);
        var dnf = race.Entrants.Where(e => !race.Finished.ContainsKey(e)).ToList();
        foreach (long e in dnf)
            _chat?.Broadcast($"[race] #{race.Id} DNF {Who(e)}", ChatKind.System);
        GD.Print($"[race] #{race.Id} {race.What} results: {string.Join(", ", order.Select((kv, i) => $"{i + 1}. {Who(kv.Key)} {Format(kv.Value)}"))}"
            + (dnf.Count > 0 ? $"  DNF: {string.Join(", ", dnf.Select(Who))}" : ""));
        End(race, null, ChatKind.System);
    }

    /// <summary>Frees the race and tells every client still holding a racer in it.</summary>
    private void End(Race race, string? why, ChatKind kind)
    {
        if (why != null) _chat?.Broadcast($"[race] #{race.Id} {why}", kind);
        foreach (long e in race.Entrants)
        {
            if (_raceOf.TryGetValue(e, out int r) && r == race.Id) _raceOf.Remove(e);
            if (!_raceOf.ContainsKey(e)) _names.Remove(e);
        }
        if (race.Phase == Phase.Running)
            foreach (long owner in race.Entrants.Select(SimOf).Distinct().Where(o => Multiplayer.GetPeers().Contains((int)o)))
                RpcId(owner, MethodName.Dropped, race.Id, 0L);
        // its NPCs retire with it: a new race spawns fresh ones where their owner stands
        var npcs = race.Entrants.Where(e => e < 0).ToList();
        Npcs?.Retire(npcs);
        npcs.ForEach(e => _npcNames.Remove(e));
        _races.Remove(race.Id);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Checkpoint(int raceId, long entrant, int index)
    {
        if (!_server || !Valid(raceId, entrant, out var race)) return;
        // in order only: a checkpoint skipped is a shortcut taken
        if (!race.Next.TryGetValue(entrant, out int next) || index != next)
        {
            GD.Print($"[race] #{raceId} {Who(entrant)} reported checkpoint {index}, expected {next} — ignored");
            return;
        }
        if (!Plausible(race, entrant, index)) return;
        race.Next[entrant] = next + 1;
        race.LastReport[entrant] = _clock;
        if (race.Air && index == 0) race.Started[entrant] = _clock;
    }

    /// <summary>
    /// The server checks a report against what it can see itself instead of believing it: the
    /// entrant's body (the server's proxy copy, at its latest replicated position) must be near
    /// that checkpoint, and it must have got there at a pace its mount can do. Without this a
    /// teleport across the course was classified — a client reporting checkpoints is not proof.
    /// </summary>
    private bool Plausible(Race race, long entrant, int index)
    {
        var course = race.Course!;
        int slot = race.Entrants.IndexOf(entrant);
        float startArc = race.Air ? 0f : RaceCourse.StartArc(slot, race.Entrants.Count);
        var at = course.CheckpointAt(index, startArc);
        if (_players?.GetNodeOrNull<FootPlayer>(PlayerReplication.NodeName(entrant)) is { } body)
        {
            float reach = race.Air ? RaceCourse.GateRadius + 60f : 80f;   // the proxy lags a few frames
            float off = RaceRoute.Flat(race.Frame.ToWorld(body.Global) - at).Length();
            if (off > reach)
            {
                GD.Print($"[race] #{race.Id} {Who(entrant)} reported checkpoint {index} {off:F0} m from it — ignored");
                return false;
            }
        }
        double since = _clock - race.LastReport.GetValueOrDefault(entrant, race.StartAt);
        // straight-line distance between consecutive checkpoints: never more than the road, so
        // the check can only be generous
        float metres = index == 0 ? 0f
            : RaceRoute.Flat(at - course.CheckpointAt(index - 1, startArc)).Length() * 0.9f;
        if (index > 0 && since < metres / MaxPace(race.Mount))
        {
            _chat?.Broadcast($"[race] #{race.Id} {Who(entrant)} reached checkpoint {index} impossibly fast — out", ChatKind.Error);
            _raceOf.Remove(entrant);
            race.Out.Add(entrant);
            return false;
        }
        return true;
    }

    /// <summary>A generous ceiling on a mount's speed, m/s: anything faster between checkpoints is a teleport.</summary>
    private static float MaxPace(int mount) => (RideKind)mount switch
    {
        RideKind.OnFoot => 14f,
        RideKind.RoadBike or RideKind.Skis => 45f,
        RideKind.Paraglider or RideKind.Wingsuit or RideKind.Parachute => 70f,
        RideKind.Helicopter or RideKind.Plane => 120f,
        _ => 125f,   // cars and motorbikes: 450 km/h
    };

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Crossed(int raceId, long entrant)
    {
        if (!_server || !Valid(raceId, entrant, out var race) || race.Finished.ContainsKey(entrant)) return;
        int needed = race.Course!.Checkpoints;
        int passed = race.Next.GetValueOrDefault(entrant);
        if (passed >= needed && !Plausible(race, entrant, needed)) return;
        if (passed < needed)
        {
            _chat?.Broadcast($"[race] #{raceId} {Who(entrant)} crossed the line with {needed - passed} checkpoint(s) missed — not counted", ChatKind.Error);
            _raceOf.Remove(entrant);
            race.Out.Add(entrant);
            return;
        }
        // the server's own clock: from its GO on the ground, from the entrant's own pass through gate 0 in the air
        double time = _clock - (race.Air ? race.Started.GetValueOrDefault(entrant, race.StartAt) : race.StartAt);
        race.Finished[entrant] = time;
        _raceOf.Remove(entrant);
        int position = race.Finished.Count(kv => kv.Value <= time);
        _chat?.Broadcast($"[race] #{raceId} {Who(entrant)} finishes P{position} in {Format(time)}", ChatKind.System);
        RpcId(SimOf(entrant), MethodName.Result, raceId, entrant, position, time);
    }

    /// <summary>A report about a running race's entrant, from the peer that owns it, after GO.</summary>
    private bool Valid(int raceId, long entrant, out Race race)
    {
        race = null!;
        long sender = Multiplayer.GetRemoteSenderId();
        if (SimOf(entrant) != sender || !_races.TryGetValue(raceId, out var found)) return false;
        race = found;
        return race.Phase == Phase.Running && _clock >= race.StartAt && race.Entrants.Contains(entrant) && !race.Out.Contains(entrant);
    }

    /// <summary>
    /// A client enters NPCs it runs into an open race. Each id must be <c>-(sender * 1000 + n)</c>,
    /// n in 0..999. Name them first with <see cref="NameNpcEntrants"/>, or they race as "NPC n".
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestNpcEntrants(int raceId, long[] npcIds)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (!_races.TryGetValue(raceId, out var race) || race.Phase == Phase.Running || race.Invited != 0)
        {
            _chat?.Tell(sender, $"[race] #{raceId} is not open to NPCs", ChatKind.Error);
            return;
        }
        foreach (long id in npcIds.Distinct())
        {
            if (id >= 0 || OwnerOf(id) != sender || _raceOf.ContainsKey(id) || race.Entrants.Count >= MaxEntrants) continue;
            Enter(race, id);
            _chat?.Broadcast($"[race] #{raceId} {Who(id)} joins ({race.Entrants.Count} in)", ChatKind.System);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NameNpcEntrants(long[] npcIds, string[] names)
    {
        if (!_server) return;
        long sender = Multiplayer.GetRemoteSenderId();
        for (int i = 0; i < Mathf.Min(npcIds.Length, names.Length); i++)
            if (npcIds[i] < 0 && OwnerOf(npcIds[i]) == sender)
            {
                _npcNames[npcIds[i]] = new string(names[i].Where(c => !char.IsControl(c)).Take(24).ToArray());
                if (_names.ContainsKey(npcIds[i])) _names[npcIds[i]] = _npcNames[npcIds[i]];
            }
    }

    private string Who(long e) => _names.TryGetValue(e, out var known) ? known : e < 0
        ? _npcNames.GetValueOrDefault(e) ?? $"NPC {-e % 1000} ({Who(OwnerOf(e))})"
        : _chat?.NameOfPeer(e) ?? $"#{e}";

    private static string Format(double s) => $"{(int)(s / 60)}:{s % 60:00.00}";

    /// <summary>A mount word: foot, bike, skis, car or a car's name, moto/r1, monster, plane, heli, paraglider, wingsuit, open.</summary>
    public static int? ParseMount(string word)
    {
        switch (word.ToLowerInvariant())
        {
            case "open": return Open;
            case "foot" or "run": return (int)RideKind.OnFoot;
            case "bike": return (int)RideKind.RoadBike;
            case "skis" or "ski": return (int)RideKind.Skis;
            case "car": return (int)CarCatalog.All[0].Kind;
            case "moto" or "r1" or "motorbike": return 64;   // the motorbikes (#38): 64 and 65
            case "monster": return 65;
            case "plane": return (int)RideKind.Plane;
            case "heli" or "helicopter": return (int)RideKind.Helicopter;
            case "paraglider" or "glider": return (int)RideKind.Paraglider;
            case "wingsuit": return (int)RideKind.Wingsuit;
        }
        var car = CarCatalog.All.FirstOrDefault(c => c.Label.Split(' ')[0].Equals(word, System.StringComparison.OrdinalIgnoreCase));
        return car != null ? (int)car.Kind : null;
    }

    public static string MountName(int k) => k switch
    {
        Open => "open",
        (int)RideKind.OnFoot => "foot",
        64 or 65 when Rideable.Create((RideKind)k) == null => k == 64 ? "motorbike" : "monster bike",
        _ => CarCatalog.For((RideKind)k)?.Label.Split(' ')[0] ?? Rideable.Create((RideKind)k)?.Label.ToLowerInvariant() ?? $"#{k}",
    };

    public override void _Process(double delta)
    {
        if (_server) ServerTick(delta);
        else ClientTick(delta);
    }

    // ====================================================================================
    // client
    // ====================================================================================

    /// <summary>One entrant this client runs: its player, or an NPC.</summary>
    private sealed class Runner
    {
        public int RaceId;
        public long Id;
        public RaceCourse Course = null!;
        public int Slot, Count, Mount, Next;
        /// <summary>The race's car class (<see cref="CarSetups"/> id), or −1: any.</summary>
        public int Class = -1;
        public float StartArc;
        public double GoIn, Clock;
        public bool Going, Done, Placed, HasLast;
        public Vector3 Last;
        /// <summary>An NPC's position, given by whoever drives it (<see cref="TrackNpc"/>).</summary>
        public System.Func<Vector3>? Where;
        public string Status = "";
    }

    /// <summary>The local player, resolved when needed (never captured: it is respawned).</summary>
    public System.Func<FootPlayer?>? LocalPlayer { get; set; }
    private Runner? _me;
    private readonly Dictionary<long, Runner> _npcs = new();
    private AutoPilot? _pilot;
    private GatePilot? _air;
    private RaceGates? _gates;
    private Label? _hud;
    private readonly bool _auto = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--raceauto") >= 0;

    /// <summary>Raised on the client when this player finishes (for checks): position, seconds.</summary>
    public event System.Action<int, double>? Finished;

    /// <summary>Where an NPC this client entered starts, sent at the close of entry.</summary>
    /// <summary><c>Resume</c>: the NPC was handed to this client (#50) and is not on the grid — it
    /// reports from checkpoint <c>Next</c>.</summary>
    public readonly record struct NpcGrid(int RaceId, long NpcId, RaceCourse Course, Vector3 At, Vector3 Forward, int Mount, double Countdown,
        int Next = 0, bool Resume = false, float Skill = 1f);
    public event System.Action<NpcGrid>? NpcSetup;
    /// <summary>An NPC finished: id, position, seconds.</summary>
    public event System.Action<long, int, double>? NpcFinished;
    /// <summary>An NPC's race ended or it was taken out.</summary>
    public event System.Action<long>? NpcDropped;

    /// <summary>How one of this client's NPCs arrives (#51): the road, where the start is on it, its style and provisional slot.</summary>
    public readonly record struct Arrival(int RaceId, RaceRoute Lane, float Zero, ArrivalStyle Style, int Variant, int Slot, int Count);
    private readonly Dictionary<long, Arrival> _arrivals = new();

    /// <summary>The arrival sent for an NPC, once (its driver may be created before or after it comes).</summary>
    public Arrival? TakeArrival(long npc) => _arrivals.Remove(npc, out var a) ? a : null;

    /// <summary>Tells the server an NPC is in its slot.</summary>
    public void ReportStaged(int raceId, long npc) => RpcId(1, MethodName.NpcStaged, raceId, npc);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NpcArrive(int raceId, long npc, double frameE, double frameN, Vector3[] centre, float[] width, float zero,
        int style, int variant, int slot, int count) =>
        _arrivals[npc] = new Arrival(raceId, RaceRoute.FromPoints(Here(frameE, frameN, centre), width, frame: _origin!.Frame),
            zero, (ArrivalStyle)style, variant, slot, count);

    /// <summary>Points the server sent as offsets from a race's anchor, in this client's world space now (#185).</summary>
    private Vector3[] Here(double frameE, double frameN, Vector3[] points)
    {
        var shift = _origin!.SinceAnchor(frameE, frameN);
        return points.Select(shift.Point).ToArray();
    }

    /// <summary>The origin moved (#185): every course, lane and last position this client races on moves with it.</summary>
    public void OnOriginShifted(OriginShift shift)
    {
        if (_server || _origin == null) return;
        var now = _origin.Frame;
        foreach (var r in _npcs.Values.Append(_me))
        {
            if (r == null) continue;
            r.Course.Follow(now);
            r.Last = shift.Point(r.Last);
        }
        foreach (var a in _arrivals.Values) a.Lane.Follow(now);
    }

    /// <summary>The latest race id this client saw opened, challenged or joined (from the chat).</summary>
    public int LastRaceSeen { get; private set; }

    /// <summary>Enters NPCs this client runs (ids <c>-(myPeer * 1000 + n)</c>) into an open race, with their display names.</summary>
    public void EnterNpcs(int raceId, long[] npcIds, string[] names)
    {
        RpcId(1, MethodName.NameNpcEntrants, npcIds, names);
        RpcId(1, MethodName.RequestNpcEntrants, raceId, npcIds);
    }

    /// <summary>This client no longer simulates that NPC (#50): it stops reporting it.</summary>
    public void ForgetNpc(long npcId) => _npcs.Remove(npcId);

    /// <summary>Hands the race an NPC's position, so its checkpoints are reported like the player's.</summary>
    public void TrackNpc(long npcId, System.Func<Vector3>? position)
    {
        if (_npcs.TryGetValue(npcId, out var r)) r.Where = position;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Setup(int raceId, long entrant, bool air, double frameE, double frameN, Vector3[] centre, float[] width, Vector3[] gates,
        float length, float gridAltitude, int slot, int count, int mount, double countdown, int next, bool resume, int carClass, float skill)
    {
        var r = new Runner
        {
            RaceId = raceId, Id = entrant, Slot = slot, Count = count, Mount = mount, GoIn = countdown, Next = next, Class = carClass,
            Course = RaceCourse.FromWire(air, Here(frameE, frameN, centre), width, Here(frameE, frameN, gates), length, gridAltitude,
                _origin!.Frame),
            StartArc = air ? 0f : RaceCourse.StartArc(slot, count),
        };
        // the grid is not lined up in the middle of this client's traffic: the cars around it go (a car
        // standing nose to nose with the front row held the whole field up at GO, #85)
        if (!air && !resume) Traffic.Current?.ClearAround(r.Course.Slot(slot, count).Item1, 150f);
        if (entrant < 0)
        {
            _npcs[entrant] = r;
            var (at, fwd) = r.Course.Slot(slot, count);
            NpcSetup?.Invoke(new NpcGrid(raceId, entrant, r.Course, at, fwd, mount, countdown, next, resume, skill));
            return;
        }
        StopLocal();
        _me = r;
        if (air)
        {
            _gates = RaceGates.Build(r.Course.Gates, RaceCourse.GateRadius);
            AddChild(_gates);
        }
        GD.Print($"[race] #{raceId} on the grid, slot {slot + 1} of {count}, {length:F0} m, {MountName(mount)}");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Result(int raceId, long entrant, int position, double time)
    {
        if (entrant < 0) { NpcFinished?.Invoke(entrant, position, time); return; }
        GD.Print($"[race] #{raceId} finished P{position} in {Format(time)}");
        Finished?.Invoke(position, time);
        // an autopiloted car keeps its pilot, now only braking to a stop; a player just drives on
        if (_pilot == null && LocalPlayer?.Invoke() is { } me) me.RideControls = null;
    }

    /// <summary>The race ended (entrant 0: for everyone this client runs in it) or took this entrant out.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Dropped(int raceId, long entrant)
    {
        foreach (var npc in _npcs.Values.Where(n => n.RaceId == raceId && (entrant == 0 || n.Id == entrant)).ToList())
        {
            _npcs.Remove(npc.Id);
            NpcDropped?.Invoke(npc.Id);
        }
        if (_me != null && _me.RaceId == raceId && (entrant == 0 || entrant == _me.Id)) StopLocal();
    }

    /// <summary>Lets go of the local player: no hold, no pilot, no gates.</summary>
    private void StopLocal()
    {
        if (_me == null) return;
        if (LocalPlayer?.Invoke() is { } me && (!_me.Going || _pilot != null)) me.RideControls = null;
        if (_air != null) GatePilot.Release();
        _air = null;
        _pilot = null;
        _gates?.QueueFree();
        _gates = null;
        _me = null;
        ShowHud(null);
    }

    private void ClientTick(double delta)
    {
        AutoOpen(delta);
        foreach (var npc in _npcs.Values)
        {
            npc.GoIn -= delta;
            if (npc.GoIn <= 0 && npc.Where != null) Progress(npc, npc.Where());
        }
        if (_me == null) return;
        var r = _me;
        var me = LocalPlayer?.Invoke();
        if (me == null) return;

        if (!r.Going)
        {
            Hold(me, r);
            r.GoIn -= delta;
            ShowHud(r.Status.Length > 0 ? r.Status : r.GoIn > 0.9 ? $"{Mathf.CeilToInt((float)r.GoIn)}" : "GO!");
            if (r.GoIn > 0) return;
            r.Going = true;
            GD.Print($"[race] #{r.RaceId} GO");
            Release(me, r);
        }
        if (r.Done) { ShowHud(null); return; }
        r.Clock += delta;
        if (_auto && (int)r.Clock != (int)(r.Clock - delta) && (int)r.Clock % 2 == 0)   // a --raceauto trace every 2 s: where the pilot is, for the loopback check
            GD.Print($"[race] (auto) t {r.Clock:F0} left {(r.Course.Air ? 0 : r.Course.Remaining(r.Next, me.GlobalPosition, r.StartArc)):F0} m, off {(r.Course.Air ? 0 : r.Course.Route!.Off(me.GlobalPosition)):F1}, {me.Motion.Speed * 3.6f:F0} km/h, ride {me.Ride}, next {r.Next}, walk {(me.WalkControls != null)}");
        Progress(r, me.GlobalPosition);
        _gates?.Highlight(r.Next);
        string left = $"{r.Course.Remaining(r.Next, me.GlobalPosition, r.StartArc):F0} m";
        ShowHud(r.Course.Air
            ? $"{Format(r.Clock)}   GATE {Mathf.Min(r.Next + 1, r.Course.Gates.Length)}/{r.Course.Gates.Length}   {left}"
            : $"{Format(r.Clock)}   CP {r.Next}/{r.Course.Checkpoints}   {left}");
    }

    public override void _PhysicsProcess(double delta)
    {
        // the air pilot presses input actions: once per physics step, so a one-step press (Jump) is never lost
        if (_server || _air == null || _me == null) return;
        if (!_me.Going) { _air.Prepare(); return; }
        // each slot flies its own lane through the ring: two pilots aimed at its centre fly in
        // formation until they touch (measured: two helicopters 9 m apart, one crashed)
        var c = _me.Course;
        var lane = c.Heading.Cross(Vector3.Up) * ((_me.Slot % 4) - 1.5f) * 10f + Vector3.Up * ((_me.Slot / 4 % 3) - 1) * 8f;
        _air.Fly(c.Gates[Mathf.Min(_me.Next, c.Gates.Length - 1)] + lane, _me.Done);
    }

    /// <summary>Checkpoints in order, then the line, from the last position to this one.</summary>
    private void Progress(Runner r, Vector3 at)
    {
        var from = r.HasLast ? r.Last : at;
        r.Last = at;
        r.HasLast = true;
        while (!r.Done && r.Course.Reached(r.Next, from, at, r.StartArc))
        {
            if (r.Next < r.Course.Checkpoints)
            {
                if (r.Id > 0 && r.Next == _skip) GD.Print($"[race] (test) not reporting checkpoint {r.Next}");
                else RpcId(1, MethodName.Checkpoint, r.RaceId, r.Id, r.Next);
                if (r == _me) GD.Print($"[race] #{r.RaceId} checkpoint {r.Next + 1}/{r.Course.Checkpoints} at {r.Clock:F1} s, y {at.Y:F0}");
                r.Next++;
                continue;
            }
            RpcId(1, MethodName.Crossed, r.RaceId, r.Id);
            r.Done = true;   // the server answers with the result
            if (r == _me && _pilot != null) _pilot.Finished = true;   // brake to a stop past the line
        }
    }

    /// <summary>
    /// Until GO: on the grid, on the race's mount, held there. On the ground the player stands
    /// in its slot (a car on the handbrake, a mount on the brake, anything that drifts put back);
    /// in the air it mounts where it stands, then is held in the air at its slot, moving as
    /// it will at GO — a plane at 45 m/s, a paraglider at trim, a helicopter hovering, a wingsuit
    /// pilot standing on nothing, to base-jump at GO.
    /// </summary>
    private void Hold(FootPlayer me, Runner r)
    {
        var (at, fwd) = r.Course.Slot(r.Slot, r.Count);
        var yaw = new Vector3(0, Mathf.Atan2(-fwd.X, -fwd.Z), 0);
        r.Status = "";
        if (!r.Course.Air)
        {
            if (!r.Placed)
            {
                r.Placed = true;
                me.GlobalPosition = at + Vector3.Up * 1.2f;
                me.Rotation = yaw;
                me.Velocity = Vector3.Zero;
            }
            if (r.Mount != Open && me.Ride != (RideKind)r.Mount && me.IsOnFloor())
            {
                me.Rotation = yaw;
                Mount(me, (RideKind)r.Mount);
            }
            // a class race: every car on the grid in the class's preset (at a standstill, on the grid)
            if (r.Class >= 0 && me.Vehicle is Car && me.CarSetupId != r.Class && me.SetCarSetup(r.Class))
                GD.Print($"[race] #{r.RaceId} class {CarSetups.For(r.Class).Name}: fitted");
            me.RideControls = me.Vehicle is Car ? () => new RideInput(0f, 0f, 0f, false, Handbrake: true)
                : me.Vehicle != null ? () => new RideInput(0f, 1f, 0f, false) : null;
            if (me.Vehicle is not Car && RaceRoute.Flat(me.GlobalPosition - at).Length() > 1.5f)
            {
                me.GlobalPosition = at + Vector3.Up * 1.0f;
                me.Velocity = Vector3.Zero;
            }
            return;
        }

        // a wingsuit is a base jump: the pilot is held on foot
        var craft = r.Mount == Open ? me.Ride : r.Mount == (int)RideKind.Wingsuit ? RideKind.OnFoot : (RideKind)r.Mount;
        if (me.Ride != craft)
        {
            if (!me.IsOnFloor()) { r.Status = $"Land to take the {MountName((int)craft)}"; return; }
            me.Rotation = yaw;   // a craft starts on the body's heading
            Mount(me, craft);
            if (me.Ride != craft) return;
        }
        _air ??= _auto ? new GatePilot(me) : null;
        if (!me.IsFlying) me.Rotation = yaw;
        float speed = me.Vehicle switch { Player.Plane => 45f, Canopy => 10.5f, _ => 0f };
        me.DebugLaunch(at, fwd * speed);
    }

    private static void Mount(FootPlayer me, RideKind kind)
    {
        if (me.Vehicle is { IsVehicle: true } && me.Ride != kind) me.SetRide(RideKind.OnFoot);
        me.SetRide(kind);   // refused off the ground: tried again next frame
    }

    /// <summary>GO: the player drives, or with <c>--raceauto</c> a pilot does.</summary>
    private void Release(FootPlayer me, Runner r)
    {
        me.RideControls = null;
        if (!_auto || r.Course.Air) return;
        // for the checks: the cars on the grid as this peer sees them, remote ones included
        GD.Print($"[race] #{r.RaceId} GO, cars here: " + string.Join(", ", GetTree().GetNodesInGroup(FootPlayer.Group)
            .OfType<FootPlayer>().Where(p => CarCatalog.IsCar(p.Ride)).Select(p => $"{p.Name} {CarSetups.For(p.CarSetupId).Name}")));
        _pilot = me.Vehicle is Car car ? new AutoPilot(r.Course.Route!, me, car.Spec) : AutoPilot.For(r.Course.Route!, me);
        if (_pilot == null) { GD.Print($"[race] #{r.RaceId} no autopilot for {me.Ride} yet"); return; }
        var pilot = _pilot;
        pilot.Log = s => GD.Print($"[race] #{r.RaceId} {s}");
        me.RideControls = () => pilot.Drive((float)GetPhysicsProcessDeltaTime(), true, Others(me));
    }

    /// <summary>Everyone else on the road, for a pilot (a player's or an NPC's).</summary>
    internal static IEnumerable<AutoPilot.Other> Others(FootPlayer me)
    {
        foreach (var node in me.GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer p && p != me)
                // every player, whatever race they are in: a race does not suspend the road.
                // WorldVelocity, because a remote's Velocity is always zero — a pilot reading it
                // took every other car on the road for a parked one
                yield return new AutoPilot.Other(p.GlobalPosition, p.WorldVelocity, false);
    }

    private void ShowHud(string? text)
    {
        if (text == null) { if (_hud != null) _hud.Visible = false; return; }
        if (_hud == null)
        {
            var layer = new CanvasLayer { Layer = 11, Name = "RaceHud" };
            AddChild(layer);
            _hud = new Label
            {
                AnchorLeft = 0.5f, AnchorRight = 0.5f, OffsetLeft = -300, OffsetRight = 300, OffsetTop = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            _hud.AddThemeFontSizeOverride("font_size", 30);
            _hud.AddThemeColorOverride("font_color", new Color(1f, 0.85f, 0.2f));
            _hud.AddThemeColorOverride("font_outline_color", Colors.Black);
            _hud.AddThemeConstantOverride("outline_size", 6);
            layer.AddChild(_hud);
        }
        _hud.Visible = true;
        _hud.Text = text;
    }

    // ---- scripted players, for the loopback check ----
    // --racestart "<args>" sends /race start <args>; --racecmd "<args>" sends /race <args> (a duel);
    // --racejoin [host] joins every race opened (by that host) or any duel it is challenged to;
    // --raceskip N never reports checkpoint N (the server must refuse the finish)
    private readonly string? _autoCmd = Arg("--racestart") is { } s ? $"start {s}".Trim() : Arg("--racecmd");
    private readonly string? _autoJoin = Arg("--racejoin");
    private readonly int _skip = int.TryParse(Arg("--raceskip"), out int k) ? k : -1;
    // --racenpc N: sends /race npc N once, when this client's own race opens
    private readonly string? _autoNpc = Arg("--racenpc");
    // --chatafter "<seconds> <line>[;<line>...]": sends chat lines that long after the first race's grid is
    // announced (spectators too) — e.g. "/city Riddes" to leave the NPCs' zone mid-race (#50)
    private string? _chatAfter = Arg("--chatafter");
    private int _myRace;
    private readonly HashSet<int> _joined = new();
    private double _settled;
    private bool _asked;
    private static readonly Regex RaceId = new(@"#(\d+)");

    private static string? Arg(string flag)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = System.Array.IndexOf(args, flag);
        return i < 0 ? null : i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : "";
    }

    public override void _Ready()
    {
        if (_server) return;
        // every race line goes to the log too, which is what the loopback check reads
        if (GetParent()?.GetNodeOrNull<ChatManager>(ChatManager.NodeName) is { } chat)
            chat.LineReceived += (line, _) =>
            {
                if (!line.Contains("[race]")) return;
                GD.Print(line);
                if (RaceId.Match(line) is not { Success: true } m || !int.TryParse(m.Groups[1].Value, out int id)) return;
                if (line.Contains("opens a") || line.Contains("challenges you") || line.Contains("you are in")) LastRaceSeen = id;
                if (line.Contains("finding the road") || line.Contains("plotting the gates")) _myRace = id;
                if (_chatAfter != null && line.Contains("on the grid:")
                    && _chatAfter.Split(' ', 2) is [var secs, var said]
                    && double.TryParse(secs, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double after))
                {
                    _chatAfter = null;   // once
                    GetTree().CreateTimer(after).Timeout += () => { foreach (var one in said.Split(';')) chat.Send(one.Trim()); };
                }
                if (_autoNpc != null && _myRace > 0 && id == _myRace && line.Contains("opens a"))
                {
                    _myRace = -1;   // once
                    chat.Send($"/race npc {_autoNpc}".TrimEnd());
                }
                bool invite = line.Contains("opens a") || line.Contains("challenges you");
                if (_autoJoin != null && invite && _joined.Add(id) && (_autoJoin.Length == 0 || line.Contains(_autoJoin)))
                    chat.Send($"/race join {id}");
            };
    }

    private void AutoOpen(double delta)
    {
        if (_autoCmd == null || _asked) return;
        if (LocalPlayer?.Invoke() is not { } me || !me.IsOnFloor()) { _settled = 0; return; }
        _settled += delta;
        if (_settled < 4.0) return;
        _asked = true;
        GetParent()?.GetNodeOrNull<ChatManager>(ChatManager.NodeName)?.Send($"/race {_autoCmd}");
    }
}
