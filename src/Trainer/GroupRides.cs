using System.Globalization;
using System.Text;
using Godot;
using UnitSport.Core;

namespace UnitSport.Trainer;

/// <summary>
/// Group rides (#1467, <c>docs/plans/home-trainer-rides.md</c> phase 4): trainer riders online on one course together.
/// One node at <c>World/GroupRides</c> on the server and on every client.
/// <list type="bullet">
/// <item>A rider with a course loaded <b>hosts</b> a group ride: its GPX goes to the server (<see cref="MaxGpxBytes"/> at
/// most), which lists it to everyone.</item>
/// <item>Another <b>joins</b> it from the list: the server sends it the same GPX, its <see cref="TrainingSession"/> loads it
/// and starts at the course's start, as a solo ride does.</item>
/// <item>Each rider sends its course metres once a second; the server sends each group's <b>standings</b> to its riders
/// (the HUD's lines and the Training app's).</item>
/// </list>
/// The riders are ordinary players on the roads: each sees the others' bikes as replicated, and drafts them
/// (<c>RideGround.WheelDraftBehind</c>). The server keeps only who rides what and how far along; it never steers anyone.
/// Bots: <see cref="ServerHost"/>, <see cref="ServerJoin"/>, <see cref="ServerProgress"/> and <see cref="ServerLeave"/> are
/// what a server-run rider would call; riding a course on the server is the gap (<c>bots/bot-capability-map</c>).
/// </summary>
public partial class GroupRides : Node
{
    public const string NodeName = "GroupRides";

    /// <summary>The largest GPX a group ride carries, bytes: a 100 km course is ~300 KB of points; this keeps the RPC sane.</summary>
    public const int MaxGpxBytes = 512 * 1024;

    /// <summary>How often a rider sends where it is on the course, and the server the standings, s.</summary>
    public const double Every = 1.0;

    public static GroupRides? Instance { get; private set; }

    /// <summary>A group ride as the list shows it.</summary>
    public readonly record struct Listed(int Id, string Name, string Host, double Km, int Riders);

    /// <summary>A rider of a group, as the standings show it: course metres and speed (m/s).</summary>
    public readonly record struct Standing(long Peer, string Name, float Along, float Speed);

    private sealed class Group
    {
        public int Id;
        public string Name = "";
        public long Host;
        public string Gpx = "";
        public double Km;
        public readonly Dictionary<long, (float Along, float Speed)> Riders = new();
    }

    // ---- the server's ----
    private readonly Dictionary<int, Group> _groups = new();
    private readonly Dictionary<long, int> _groupOf = new();
    private int _nextId = 1;
    private double _standingsAt;
    private Func<long, string>? _nameOf;

    // ---- a client's ----
    /// <summary>The group rides open on the server, as last listed.</summary>
    public IReadOnlyList<Listed> Open => _open;
    private readonly List<Listed> _open = new();

    /// <summary>The group ride this client rides in, 0 when none.</summary>
    public int Joined { get; private set; }

    /// <summary>The standings of the group this client rides in, first the furthest along.</summary>
    public IReadOnlyList<Standing> Standings => _standings;
    private readonly List<Standing> _standings = new();

    /// <summary>A line to show the player (a refusal, a group closed).</summary>
    public event Action<string>? Said;

    private double _progressAt;

    /// <summary>Puts the node under <paramref name="world"/>; on the server, <paramref name="nameOf"/> names a peer.</summary>
    public static GroupRides Create(Node world, Func<long, string>? nameOf = null)
    {
        var g = new GroupRides { Name = NodeName, _nameOf = nameOf };
        world.AddChild(g);
        return g;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        if (Multiplayer.IsServer()) Multiplayer.PeerDisconnected += peer => ServerLeave(peer);
    }

    private bool Online => Net.NetLink.Online(this);

    public override void _Process(double delta)
    {
        if (!Online) return;
        double now = RealClock.Now;
        if (Multiplayer.IsServer())
        {
            if (now < _standingsAt) return;
            _standingsAt = now + Every;
            foreach (var g in _groups.Values) SendStandings(g);
            return;
        }
        // a rider in a group: where it is on the course, once a second
        if (Joined == 0 || now < _progressAt || TrainingSession.Instance is not { } s) return;
        _progressAt = now + Every;
        if (s.State == TrainingSession.Phase.Riding || s.State == TrainingSession.Phase.Finished)
            RpcId(1, MethodName.ProgressRpc, (float)s.Along, s.Speed);
    }

    // ================================================================ a client's side

    /// <summary>Hosts a group ride on the course loaded now (its GPX file); false (and why, through <see cref="Said"/>) when it cannot.</summary>
    public bool Host(TrainingSession session)
    {
        if (!Online) { Said?.Invoke("Group rides are ridden online."); return false; }
        if (session.CoursePath is not { } path || !System.IO.File.Exists(path)) { Said?.Invoke("Load a course first."); return false; }
        string gpx = System.IO.File.ReadAllText(path);
        if (Encoding.UTF8.GetByteCount(gpx) > MaxGpxBytes) { Said?.Invoke("That course is too large to share."); return false; }
        RpcId(1, MethodName.HostRpc, session.CourseName ?? "Group ride", gpx, session.CourseKm);
        return true;
    }

    /// <summary>Asks to join group <paramref name="id"/>: the server answers with its course (<see cref="JoinedRpc"/>).</summary>
    public void Join(int id)
    {
        if (Online) RpcId(1, MethodName.JoinRpc, id);
    }

    /// <summary>Leaves the group ridden in (the ride itself goes on solo).</summary>
    public void Leave()
    {
        if (Joined == 0) return;
        Joined = 0;
        _standings.Clear();
        if (Online) RpcId(1, MethodName.LeaveRpc);
    }

    /// <summary>The HUD's lines: "Group:" and the standings under it, nothing outside a group.</summary>
    public string HudLines(float mine) => Joined == 0 || _standings.Count == 0 ? "" : "\nGroup:\n" + StandingsText(mine);

    /// <summary>The group's riders, the furthest first (six at most), each's gap to this one, a line each.</summary>
    public string StandingsText(float mine)
    {
        var sb = new StringBuilder();
        int me = Multiplayer.GetUniqueId();
        for (int i = 0; i < _standings.Count && i < 6; i++)
        {
            var r = _standings[i];
            string gap = r.Peer == me ? "(you)" : string.Create(CultureInfo.InvariantCulture, $"{r.Along - mine:+0;-0} m");
            if (i > 0) sb.Append('\n');
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1}. {r.Name}  {r.Along / 1000:0.00} km  {r.Speed * 3.6f:0} km/h  {gap}");
        }
        return sb.ToString();
    }

    // ================================================================ the server's side (callable for a bot)

    /// <summary>Server: <paramref name="peer"/> hosts a group ride on <paramref name="gpx"/>; the group's id, 0 when refused.</summary>
    public int ServerHost(long peer, string name, string gpx, double km)
    {
        if (string.IsNullOrWhiteSpace(gpx) || Encoding.UTF8.GetByteCount(gpx) > MaxGpxBytes || !gpx.Contains("<trkpt", StringComparison.Ordinal)) return 0;
        ServerLeave(peer);
        var g = new Group { Id = _nextId++, Name = Clean(name), Host = peer, Gpx = gpx, Km = Math.Max(0, km) };
        g.Riders[peer] = (0f, 0f);
        _groups[g.Id] = g;
        _groupOf[peer] = g.Id;
        GD.Print(FormattableString.Invariant($"[group] {NameOf(peer)} hosts \"{g.Name}\" ({g.Km:0.0} km), group {g.Id}"));
        SendList();
        return g.Id;
    }

    /// <summary>Server: <paramref name="peer"/> joins group <paramref name="id"/>; false when there is none.</summary>
    public bool ServerJoin(long peer, int id)
    {
        if (!_groups.TryGetValue(id, out var g)) return false;
        if (_groupOf.TryGetValue(peer, out int was) && was != id) ServerLeave(peer);
        g.Riders[peer] = (0f, 0f);
        _groupOf[peer] = id;
        GD.Print($"[group] {NameOf(peer)} joins group {id} ({g.Riders.Count} riders)");
        SendList();
        return true;
    }

    /// <summary>Server: where <paramref name="peer"/> is on its group's course.</summary>
    public void ServerProgress(long peer, float along, float speed)
    {
        if (!_groupOf.TryGetValue(peer, out int id) || !_groups.TryGetValue(id, out var g)) return;
        if (!float.IsFinite(along) || !float.IsFinite(speed)) return;
        g.Riders[peer] = (Mathf.Clamp(along, 0f, (float)(g.Km * 1000 + 100)), Mathf.Clamp(speed, 0f, 40f));
    }

    /// <summary>Server: <paramref name="peer"/> leaves its group (left the game, stopped); an empty group closes.</summary>
    public void ServerLeave(long peer)
    {
        if (!_groupOf.Remove(peer, out int id) || !_groups.TryGetValue(id, out var g)) return;
        g.Riders.Remove(peer);
        if (g.Riders.Count == 0)
        {
            _groups.Remove(id);
            GD.Print($"[group] group {id} closed");
        }
        else if (g.Host == peer) g.Host = g.Riders.Keys.First();
        SendList();
    }

    /// <summary>Server: the group a peer rides in (0 none), for a check.</summary>
    public int GroupOf(long peer) => _groupOf.GetValueOrDefault(peer);

    private string NameOf(long peer) => _nameOf?.Invoke(peer) ?? $"Player {peer}";

    private static string Clean(string name)
    {
        name = name.Replace("|", " ").Replace("\n", " ").Trim();
        return name.Length == 0 ? "Group ride" : name.Length > 40 ? name[..40] : name;
    }

    private void SendList()
    {
        if (!Online) return;
        var sb = new StringBuilder();
        foreach (var g in _groups.Values)
            sb.Append(CultureInfo.InvariantCulture, $"{g.Id}|{g.Name}|{Clean(NameOf(g.Host))}|{g.Km:0.###}|{g.Riders.Count}\n");
        Rpc(MethodName.ListRpc, sb.ToString());
    }

    private void SendStandings(Group g)
    {
        var sb = new StringBuilder();
        foreach (var (peer, (along, speed)) in g.Riders.OrderByDescending(r => r.Value.Along))
            sb.Append(CultureInfo.InvariantCulture, $"{peer}|{Clean(NameOf(peer))}|{along:0.#}|{speed:0.##}\n");
        string packed = sb.ToString();
        foreach (long peer in g.Riders.Keys)
            if (Multiplayer.GetPeers().Contains((int)peer)) RpcId(peer, MethodName.StandingsRpc, g.Id, packed);
    }

    // ================================================================ the wire

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void HostRpc(string name, string gpx, double km)
    {
        if (!Multiplayer.IsServer()) return;
        long peer = Multiplayer.GetRemoteSenderId();
        int id = ServerHost(peer, name, gpx, km);
        if (id == 0) RpcId(peer, MethodName.RefusedRpc, "The server would not take that course.");
        else RpcId(peer, MethodName.HostedRpc, id);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void JoinRpc(int id)
    {
        if (!Multiplayer.IsServer()) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (!ServerJoin(peer, id) || !_groups.TryGetValue(id, out var g))
        {
            RpcId(peer, MethodName.RefusedRpc, "That group ride has ended.");
            return;
        }
        RpcId(peer, MethodName.JoinedRpc, id, g.Name, g.Gpx);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void LeaveRpc()
    {
        if (Multiplayer.IsServer()) ServerLeave(Multiplayer.GetRemoteSenderId());
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void ProgressRpc(float along, float speed)
    {
        if (Multiplayer.IsServer()) ServerProgress(Multiplayer.GetRemoteSenderId(), along, speed);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ListRpc(string packed)
    {
        _open.Clear();
        foreach (var line in packed.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('|');
            if (f.Length < 5 || !int.TryParse(f[0], CultureInfo.InvariantCulture, out int id)) continue;
            double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double km);
            int.TryParse(f[4], CultureInfo.InvariantCulture, out int riders);
            _open.Add(new Listed(id, f[1], f[2], km, riders));
        }
        // the group this client rode in has closed
        if (Joined != 0 && !_open.Any(g => g.Id == Joined))
        {
            Joined = 0;
            _standings.Clear();
        }
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void HostedRpc(int id)
    {
        Joined = id;
        Said?.Invoke("Group ride open: others can join it from their Training app.");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void JoinedRpc(int id, string name, string gpx)
    {
        // a ride under way ends first: its stop leaves the group it was in, not this one
        TrainingSession.Instance?.Stop("joining a group ride");
        Joined = id;
        _standings.Clear();
        // the group's course, ridden as any course: loaded from a file, started once it is ready
        string dir = ProjectSettings.GlobalizePath("user://training/groups");
        System.IO.Directory.CreateDirectory(dir);
        string safe = string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
        string path = System.IO.Path.Combine(dir, $"{safe}_{id}.gpx");
        System.IO.File.WriteAllText(path, gpx);
        Said?.Invoke($"Joined the group ride \"{name}\": off to its start.");
        TrainingSession.Instance?.LoadAndStart(path);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RefusedRpc(string why) => Said?.Invoke(why);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void StandingsRpc(int id, string packed)
    {
        if (id != Joined) return;
        _standings.Clear();
        foreach (var line in packed.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('|');
            if (f.Length < 4 || !long.TryParse(f[0], CultureInfo.InvariantCulture, out long peer)) continue;
            float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float along);
            float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float speed);
            _standings.Add(new Standing(peer, f[1], along, speed));
        }
    }
}
