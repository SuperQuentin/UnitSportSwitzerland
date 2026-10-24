using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using FileAccess = System.IO.FileAccess;
using UnitSport.Core;
using UnitSport.Movie;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Playtest.TopView;
using UnitSport.Vehicles;

namespace UnitSport.Playtest.Recording;

/// <summary>
/// The playtest recorder (#844, docs/notes/general/playtest-recorder.md): <c>--playtest-record [dir]</c>
/// in a Debug build, client or dedicated server. It writes one bundle per session to
/// <c>test_output/playtest/sessions/&lt;yyyyMMdd_HHmmss&gt;/</c> (a server's to <c>.../server/</c>):
/// a 10 Hz <see cref="TraceFormat">trace</see> of every player and every watched world object
/// (<see cref="ReplayWatch"/>), <c>events.jsonl</c> (scenarios, notes, chat, rides, teleports, joins,
/// every log line), the notes with their screenshot and top view, the PerfRecorder's output, the
/// movie studio's recording of the same session, and at the end <c>report.md</c>.
///
/// <para>
/// Reads only, at 10 Hz, on the main thread; every file is written on a worker (a
/// <c>Task.ContinueWith</c> chain, as <see cref="SessionSpool"/>), flushed every second so a crash
/// keeps the session. Times are wall-clock seconds since the session began, so a relaunch (another
/// course, a rebuild) appends to the same session and a server's trace lines up with a client's.
/// </para>
/// </summary>
public partial class PlaytestRecorder : Node
{
    public const double Rate = 10, RingSeconds = 60;
    /// <summary>World objects are traced within this many metres (east or north) of a player or, on a client, the camera.</summary>
    public const double ObjectRange = 400;
    private const int MaxObjects = 400;

    public static bool Requested => OS.IsDebugBuild() && CmdArgs.Has("--playtest-record");
    public static PlaytestRecorder? Instance { get; private set; }
    public static string SessionsDir => Path.Combine(PlaytestDirector.Root, "test_output", "playtest", "sessions");

    private readonly bool _server;
    private readonly Func<WorldOrigin?> _origin;
    private readonly Func<FootPlayer?> _local;
    private readonly ChatManager? _chat;

    /// <summary>The bundle's folder.</summary>
    public string Dir { get; }
    public bool Running => _trace != null && !_stopped;
    /// <summary>The local player's trace id as of the last sample (a respawn is a new node, so a new id); -1 before.</summary>
    public int LocalId { get; private set; } = -1;
    /// <summary>The local player, on a client (the checks drive it).</summary>
    internal FootPlayer? LocalPlayer => _local();
    /// <summary>Seconds since the session began (wall clock): the time of every frame, event and note.</summary>
    public double Now => _zero + _clock.Elapsed.TotalSeconds;
    /// <summary>The last minute in memory, for the live top view and the notes' pictures.</summary>
    public TraceRing Ring { get; } = new((int)(Rate * RingSeconds));
    public Dictionary<int, TraceEntity> Entities { get; } = new();
    public List<TopNote> Notes { get; } = new();
    /// <summary>The world's tiles, for the top view's ground (#851); null when there are none to draw.</summary>
    public GroundSpec? Ground { get; }

    private TraceWriter? _trace;
    private StreamWriter? _events, _notes;
    private Task _work = Task.CompletedTask;
    private readonly object _gate = new();
    private string? _error;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _zero;
    private bool _stopped;
    private double _next, _nextFlush;

    private readonly Dictionary<ulong, int> _ids = new();
    private int _nextId;
    private readonly Dictionary<int, (Node3D Node, string Container, double Since)> _undescribed = new();
    private readonly Dictionary<int, (double E, double N, float Alt, float Heading, double T)> _last = new();
    private readonly Dictionary<int, ActorState> _props = new();
    private readonly ActorState _scratch = new(ActorIo.Names.Length);
    private readonly HashSet<int> _players = new(), _playersBefore = new();
    private readonly List<(double E, double N)> _playerSpots = new();
    private readonly ConcurrentBag<TraceFrame> _pool = new();
    private readonly List<(int Id, double T, EN? At)> _awaitingTop = new();
    private int _noteCount;

    private RecorderLog? _log;
    private PerfRecorder? _perf;
    private bool _perfLooked, _movie;
    private TaskCompletionSource? _finished;

    /// <param name="origin">Where the world's origin is (objects other than vehicles are placed through it).</param>
    /// <param name="local">The local player, on a client: its notes are stamped with its position.</param>
    /// <param name="ground">The world's tiles, for the top view's ground (#851), and how to find them again offline.</param>
    public PlaytestRecorder(bool server, Func<WorldOrigin?> origin, Func<FootPlayer?>? local = null, ChatManager? chat = null, GroundSpec? ground = null)
    {
        Ground = ground;
        Name = "PlaytestRecorder";
        _server = server;
        _origin = origin;
        _local = local ?? (() => null);
        _chat = chat;
        ProcessMode = ProcessModeEnum.Always;
        string? given = CmdArgs.Value("--playtest-record", notFlag: true);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string dir = given != null
            ? Path.IsPathRooted(given) ? given : Path.Combine(PlaytestDirector.Root, given)
            : server ? Path.Combine(SessionsDir, stamp, "server") : Path.Combine(SessionsDir, stamp);
        Dir = Path.GetFullPath(dir);
        // the check starts from nothing: a session left by the last run would be appended to
        if (RecorderCheck.Requested && Directory.Exists(Dir)) Directory.Delete(Dir, true);
    }

    public override void _EnterTree() => Instance = this;

    public override void _Ready()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(Dir, "notes"));
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _trace = new TraceWriter(Dir, new TraceHeader(nowMs, _server ? "server" : "client"));
            _zero = (nowMs - _trace.Header.StartUnixMs) / 1000.0;
            _events = Append(SessionReport.EventsFile);
            _notes = Append(SessionReport.NotesFile);
            _noteCount = SessionReport.ReadLines(Path.Combine(Dir, SessionReport.NotesFile)).Count;
            // a relaunch goes on numbering after the things the first part traced: an id never means two things
            if (_zero > 0) _nextId = new TraceReader(Dir).Entities.Keys.DefaultIfEmpty(0).Max();
        }
        catch (Exception e)
        {
            GD.PushError($"[playtest] cannot record to {Dir}: {e.Message}");
            _trace = null;
            return;
        }
        _log = new RecorderLog(this);
        OS.AddLogger(_log);
        if (_chat != null)
        {
            _chat.LineReceived += OnChat;
            _chat.NoteRequested += OnChatNote;
        }
        if (!_server)
        {
            AddChild(new NoteTaker(this));
            AddChild(new TopViewOverlay(this));
            if (RecorderCheck.Requested) AddChild(new RecorderCheck(this));
            if (RecorderNetCheck.Requested) AddChild(new RecorderNetCheck(this));
        }
        (string, object?)[] start =
        [
            ("what", "start"), ("role", _server ? "server" : "client"), ("wall", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            ("commit", Git("rev-parse", "--short", "HEAD")), ("args", string.Join(' ', CmdArgs.All)),
            .. Ground is { } g ? TopViewImage.Describe(g.Spec, (g.StartE, g.StartN)) : [],
        ];
        Event("session", start);
        GD.Print($"[playtest] recording the session to {Dir}");
        // the movie studio's recording of the same session, to watch a moment again in 3D (#775)
        if (!_server && !CmdArgs.Has("--playtest-nomovie") && ReplayRecorder.Instance is { } replay && replay.StartSession()) _movie = true;
    }

    private StreamWriter Append(string file) =>
        new(new FileStream(Path.Combine(Dir, file), FileMode.Append, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false));

    public override void _ExitTree()
    {
        _ = Stop(exiting: true);
        if (_log != null) OS.RemoveLogger(_log);
        if (_chat != null)
        {
            _chat.LineReceived -= OnChat;
            _chat.NoteRequested -= OnChatNote;
        }
        if (Instance == this) Instance = null;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) _ = Stop(exiting: true);
    }

    // ---- the worker ----------------------------------------------------------------------

    /// <summary>Queues a write on the worker, in order. Any thread: the logger calls in from wherever the engine logs.</summary>
    private void Queue(Action write)
    {
        lock (_gate)
            _work = _work.ContinueWith(_ =>
            {
                if (_error != null) return;
                try { write(); }
                catch (Exception e) { _error = e.Message; }
            }, TaskScheduler.Default);
    }

    /// <summary>Everything queued so far written and flushed (for a report or a top view read from the files).</summary>
    public void Sync()
    {
        Queue(FlushAll);
        _work.Wait(TimeSpan.FromSeconds(10));
    }

    private void FlushAll()
    {
        _trace?.Flush();
        _events?.Flush();
        _notes?.Flush();
    }

    // ---- events --------------------------------------------------------------------------

    /// <summary>One line of <c>events.jsonl</c>: <paramref name="type"/> and its fields, at <see cref="Now"/>. Any thread.</summary>
    public void Event(string type, params (string Key, object? Value)[] fields) => EventAt(Now, type, fields);

    private void EventAt(double t, string type, (string Key, object? Value)[] fields)
    {
        if (!Running || _events is not { } w) return;
        var o = new Dictionary<string, object?> { ["t"] = Math.Round(t, 3), ["type"] = type };
        foreach (var (k, v) in fields) o[k] = v;
        string line = JsonSerializer.Serialize(o);
        Queue(() => w.WriteLine(line));
    }

    private void OnChat(string line, ChatKind kind) => Event("chat", ("kind", kind.ToString()), ("text", line));

    private void OnChatNote(string text)
    {
        int id = Note(text.Length > 0 ? text : "(no text)", "chat", NoteTaker.Grab(GetViewport()));
        if (id > 0) GD.Print($"[playtest] /note saved as note {id}");
    }

    /// <summary>Every engine log line, tagged with its <c>[tag]</c>; from any thread, so only queued.</summary>
    private sealed partial class RecorderLog(PlaytestRecorder owner) : Logger
    {
        private static readonly Regex Tag = new(@"^\[([\w\- ]+)\]", RegexOptions.Compiled);

        public override void _LogMessage(string message, bool error) => Add(error ? "error" : "info", message.TrimEnd());

        public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify,
            int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces) =>
            Add(errorType == (int)ErrorType.Warning ? "warn" : "error",
                $"{(string.IsNullOrEmpty(rationale) ? code : rationale)} ({System.IO.Path.GetFileName(file)}:{line} {function})");

        private void Add(string level, string text)
        {
            if (text.Length == 0) return;
            var m = Tag.Match(text);
            owner.Event("log", ("level", level), ("tag", m.Success ? m.Groups[1].Value : ""), ("text", text));
        }
    }

    // ---- sampling ------------------------------------------------------------------------

    public override void _PhysicsProcess(double delta)
    {
        if (!Running) return;
        double now = Now;
        if (!_perfLooked) StartPerf();
        if (now < _next) return;
        _next = Math.Max(_next + 1 / Rate, now);
        if (now - _next > 1) _next = now;

        var frame = Ring.Next(now);
        SamplePlayers(frame, now);
        SampleObjects(frame, now);
        DescribeWaiting(now, force: false);

        var copy = _pool.TryTake(out var pooled) ? pooled : new TraceFrame();
        copy.CopyFrom(frame);
        var trace = _trace!;
        Queue(() =>
        {
            trace.Frame(copy);
            _pool.Add(copy);
        });
        if (now >= _nextFlush)
        {
            _nextFlush = now + 1;
            Queue(FlushAll);
        }
        for (int i = _awaitingTop.Count - 1; i >= 0; i--)
            if (now >= _awaitingTop[i].T + SessionReport.NoteWindow)
            {
                RenderNoteTop(_awaitingTop[i].Id, _awaitingTop[i].T, _awaitingTop[i].At);
                _awaitingTop.RemoveAt(i);
            }
    }

    private int IdOf(Node node)
    {
        ulong key = node.GetInstanceId();
        if (_ids.TryGetValue(key, out int id)) return id;
        _ids[key] = id = ++_nextId;
        return id;
    }

    private void SamplePlayers(TraceFrame frame, double now)
    {
        _playersBefore.Clear();
        _playersBefore.UnionWith(_players);
        _players.Clear();
        _playerSpots.Clear();
        var local = _local();
        foreach (var sample in PlayerSnapshot.Of(GetTree()))
        {
            var p = sample.Player;
            if (p.Puppet || !IsInstanceValid(p)) continue;
            bool known = _ids.ContainsKey(p.GetInstanceId());
            int id = IdOf(p);
            if (!known) Describe(new TraceEntity
            {
                Id = id, Kind = "player", Class = nameof(FootPlayer), Name = p.Name, Path = p.GetPath(),
                Label = p == local ? "you" : p.BotName.Length > 0 ? p.BotName : $"peer {p.GetMultiplayerAuthority()}",
                Size = [0.6f, 1.8f, 0.6f], Peer = p.GetMultiplayerAuthority(), Local = p == local, First = Math.Round(now, 3),
            });
            _players.Add(id);
            if (p == local) LocalId = id;
            var g = p.Global;
            var v = sample.Vel;
            ref var s = ref frame.Add();
            s.Id = id;
            s.E = g.E; s.N = g.N; s.Alt = (float)g.Alt;
            s.Heading = Wrap(-p.NetYaw);
            s.VE = v.X; s.VN = -v.Z; s.VUp = v.Y;
            s.HasPlayer = true;
            s.Ride = (short)p.RideKindId; s.Seat = (sbyte)Math.Clamp(p.SeatIndex, sbyte.MinValue, sbyte.MaxValue);
            s.Held = p.HeldItemId; s.RidingWith = p.RidingWith;
            (s.RideAcross, s.RideAlong) = RideSize(p, id);
            if (_last.TryGetValue(id, out var was))
            {
                s.Spin = (float)(Wrap(s.Heading - was.Heading) / Math.Max(1e-3, now - was.T));
                double jump = Math.Sqrt((g.E - was.E) * (g.E - was.E) + (g.N - was.N) * (g.N - was.N));
                if (jump > 60 && jump > 4 * v.Length() * (now - was.T) + 20)
                    Event("teleport", ("player", Entities.GetValueOrDefault(id)?.Label ?? p.Name.ToString()), ("metres", Math.Round(jump, 1)),
                        ("from", FormattableString.Invariant($"{was.E:F1},{was.N:F1}")), ("to", FormattableString.Invariant($"{g.E:F1},{g.N:F1}")));
            }
            _last[id] = (g.E, g.N, s.Alt, s.Heading, now);
            _playerSpots.Add((g.E, g.N));
            Diff(p, id);
        }
        foreach (int id in _players) if (!_playersBefore.Contains(id)) Event("join", ("player", Entities.GetValueOrDefault(id)?.Label ?? $"#{id}"), ("id", id));
        foreach (int id in _playersBefore) if (!_players.Contains(id)) Event("leave", ("player", Entities.GetValueOrDefault(id)?.Label ?? $"#{id}"), ("id", id));
    }

    /// <summary>The player's change-only properties (ActorIo: ride, seat, held item, pose, dance...) against the last sample: one event per change.</summary>
    private void Diff(FootPlayer p, int id)
    {
        ActorIo.Read(p, _scratch);
        // the first second of a player is its own setup (outfit, held item, ride): not news
        bool settling = Entities.TryGetValue(id, out var seen) && Now - seen.First < 1.5;
        if (!_props.TryGetValue(id, out var was))
        {
            was = new ActorState(ActorIo.Names.Length);
            Array.Copy(_scratch.Num, was.Num, was.Num.Length);
            Array.Copy(_scratch.Str, was.Str, was.Str.Length);
            _props[id] = was;
            return;
        }
        for (int i = 0; i < ActorIo.Names.Length; i++)
        {
            bool numChanged = was.Num[i] != _scratch.Num[i], strChanged = was.Str[i] != _scratch.Str[i];
            if (!numChanged && !strChanged) continue;
            if (settling)
            {
                was.Num[i] = _scratch.Num[i];
                was.Str[i] = _scratch.Str[i];
                continue;
            }
            string name = ActorIo.Names[i];
            string label = Entities.GetValueOrDefault(id)?.Label ?? $"#{id}";
            object? from = strChanged ? was.Str[i] : was.Num[i], to = strChanged ? _scratch.Str[i] : _scratch.Num[i];
            if (i == ActorIo.RideKind)
                Event("ride", ("player", label), ("from", RideLabel((RideKind)was.Num[i])), ("to", RideLabel((RideKind)_scratch.Num[i])));
            else Event("prop", ("player", label), ("name", name), ("from", from), ("to", to));
            was.Num[i] = _scratch.Num[i];
            was.Str[i] = _scratch.Str[i];
        }
    }

    private void SampleObjects(TraceFrame frame, double now)
    {
        var origin = _origin();
        // the camera is a centre too: flying free, there is no body, but the world around is what is seen
        if (!_server && origin != null && GetViewport().GetCamera3D() is { } cam) _playerSpots.Add(origin.ToLv95(cam.GlobalPosition));
        int count = 0;
        foreach (var container in ReplayWatch.Containers)
        {
            if (!IsInstanceValid(container)) continue;
            foreach (var child in container.GetChildren())
                if (child is Node3D n && count < MaxObjects && Sample(frame, n, container.GetType().Name, origin, now)) count++;
        }
        foreach (var o in ReplayWatch.Objects)
            if (count < MaxObjects && IsInstanceValid(o) && Sample(frame, o, "", origin, now)) count++;
    }

    private bool Sample(TraceFrame frame, Node3D n, string container, WorldOrigin? origin, double now)
    {
        // an instancer (a car park's parked cars) is many things at one place: not one thing; a sound, a light, sparks are not things
        if (n is MultiMeshInstance3D or AudioStreamPlayer3D or Light3D or Camera3D or GpuParticles3D or CpuParticles3D
            || n.IsQueuedForDeletion() || !n.IsInsideTree()) return false;
        double e, nn;
        float alt;
        if (n is VehicleBody vb)
        {
            var g = vb.Global;
            (e, nn, alt) = (g.E, g.N, (float)g.Alt);
        }
        else if (origin != null)
        {
            var w = n.GlobalPosition;
            (e, nn) = origin.ToLv95(w);
            alt = w.Y;
        }
        else return false;
        if (!Near(e, nn)) return false;

        bool known = _ids.ContainsKey(n.GetInstanceId());
        int id = IdOf(n);
        if (!known) _undescribed[id] = (n, container, now);
        var f = -n.GlobalBasis.Z;
        ref var s = ref frame.Add();
        s.Id = id;
        s.E = e; s.N = nn; s.Alt = alt;
        s.Heading = MathF.Atan2(f.X, -f.Z);
        if (_last.TryGetValue(id, out var was) && now - was.T is > 1e-3 and < 1)
        {
            float dt = (float)(now - was.T);
            s.VE = (float)(e - was.E) / dt; s.VN = (float)(nn - was.N) / dt; s.VUp = (alt - was.Alt) / dt;
            s.Spin = Wrap(s.Heading - was.Heading) / dt;
        }
        _last[id] = (e, nn, alt, s.Heading, now);
        return true;
    }

    private bool Near(double e, double n)
    {
        foreach (var (pe, pn) in _playerSpots)
            if (Math.Abs(pe - e) < ObjectRange && Math.Abs(pn - n) < ObjectRange) return true;
        return false;
    }

    /// <summary>
    /// What a new world object is, written once its size can be measured (a vehicle's model may come
    /// a moment after its body) or after 3 s.
    /// </summary>
    private void DescribeWaiting(double now, bool force)
    {
        if (_undescribed.Count == 0) return;
        var done = new List<int>();
        foreach (var (id, (node, container, since)) in _undescribed)
        {
            if (!IsInstanceValid(node)) { done.Add(id); continue; }
            var size = SizeOf(node);
            if (size[0] + size[2] <= 0 && now - since < 3 && !force) continue;
            done.Add(id);
            string cls = node.GetType().Name;
            string name = node.Name;
            // a plain node is what its manager holds: a bird, a traffic car
            string kind = node is VehicleBody vb ? VehicleLabel(vb.Kind)
                : container == "BirdLife" ? "bird"
                : cls == nameof(Node3D) && container.Length > 0 ? container : cls;
            Describe(new TraceEntity
            {
                Id = id, Kind = kind,
                Class = container.Length > 0 ? $"{container}/{cls}" : cls,
                Name = name, Path = node.GetPath(),
                Label = node is VehicleBody || name.StartsWith('@') ? kind : name,
                Size = size, First = Math.Round(since, 3),
            });
        }
        foreach (int id in done) _undescribed.Remove(id);
    }

    private readonly Dictionary<int, (int Ride, float Across, float Along)> _rideSizes = new();

    /// <summary>
    /// The footprint of what a player rides, measured from its meshes when the ride changes (and once
    /// more a second later, when a model loads late); nothing on foot or as a passenger.
    /// </summary>
    private (float, float) RideSize(FootPlayer p, int id)
    {
        int ride = p.RideKindId;
        if (ride == (int)RideKind.OnFoot || p.RidingWith != 0) return (0, 0);
        if (_rideSizes.TryGetValue(id, out var known) && known.Ride == ride && known.Along > 0) return (known.Across, known.Along);
        var size = SizeOf(p);
        _rideSizes[id] = (ride, size[0], size[2]);
        return (size[0], size[2]);
    }

    private static readonly Dictionary<RideKind, string> VehicleLabels = new();

    /// <summary>A vehicle's name as the ride menu gives it ("City bus"); catalogue kinds have no enum name.</summary>
    private static string VehicleLabel(RideKind kind)
    {
        if (!VehicleLabels.TryGetValue(kind, out string? label)) VehicleLabels[kind] = label = PlaytestContext.Label(kind);
        return label;
    }

    private static string RideLabel(RideKind kind) => kind == RideKind.OnFoot ? "on foot" : VehicleLabel(kind);

    private void Describe(TraceEntity e)
    {
        Entities[e.Id] = e;
        var trace = _trace!;
        Queue(() => trace.Entity(e));
    }

    /// <summary>A node's box in its own axes (across, up, along), from its meshes, else its collision shapes.</summary>
    private static float[] SizeOf(Node3D root)
    {
        var inv = root.GlobalTransform.AffineInverse();
        Aabb? meshes = null, shapes = null;
        int visited = 0;
        void Visit(Node node, int depth)
        {
            if (++visited > 400 || depth > 8) return;
            if (node is GeometryInstance3D gi and not (GpuParticles3D or CpuParticles3D) && gi.Visible)
            {
                var b = inv * gi.GlobalTransform * Avatar.SkinnedFigure.BoundsOf(gi);
                meshes = meshes?.Merge(b) ?? b;
            }
            else if (node is CollisionShape3D { Disabled: false, Shape: { } shape } cs)
            {
                var b = inv * cs.GlobalTransform * shape.GetDebugMesh().GetAabb();
                shapes = shapes?.Merge(b) ?? b;
            }
            foreach (var c in node.GetChildren()) Visit(c, depth + 1);
        }
        Visit(root, 0);
        var box = meshes ?? shapes;
        return box is { } a ? [Round(a.Size.X), Round(a.Size.Y), Round(a.Size.Z)] : [0, 0, 0];
    }

    private static float Round(float v) => MathF.Round(v * 100) / 100;

    private static float Wrap(float a)
    {
        while (a > MathF.PI) a -= 2 * MathF.PI;
        while (a < -MathF.PI) a += 2 * MathF.PI;
        return a;
    }

    // ---- perf ----------------------------------------------------------------------------

    private void StartPerf()
    {
        _perfLooked = true;
        if (_server || GetParent()?.GetNodeOrNull<PerfRecorder>("PerfRecorder") is not { Recording: false } perf) return;
        string dir = Path.Combine(Dir, "perf");
        for (int k = 2; Directory.Exists(dir); k++) dir = Path.Combine(Dir, $"perf-{k}");
        perf.Start(dir);
        _perf = perf;
    }

    // ---- notes ---------------------------------------------------------------------------

    /// <summary>
    /// A note (#844): "what's wrong" with the time, the local player's position and the scenario, a
    /// screenshot when one is given, and the top view of 10 s either side, drawn once those 10 s
    /// have passed. <paramref name="source"/>: key, chat, fail, mcp, check.
    /// </summary>
    public int Note(string text, string source, Image? shot = null, double? when = null)
    {
        if (!Running) return 0;
        int id = ++_noteCount;
        double t = when ?? Now;
        var me = _local();
        GlobalPos? at = LocalAt(t) ?? me?.Global;
        string shotFile = "";
        if (shot != null && !shot.IsEmpty())
        {
            shotFile = $"notes/{id:000}.png";
            var err = shot.SavePng(Path.Combine(Dir, shotFile));
            if (err != Error.Ok) { GD.PushWarning($"[playtest] note screenshot not saved: {err}"); shotFile = ""; }
        }
        string top = $"notes/{id:000}_top.png";
        string scenario = PlaytestDirector.Instance?.Current?.Id ?? "";
        var line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["t"] = Math.Round(t, 3), ["type"] = "note", ["id"] = id, ["text"] = text, ["source"] = source, ["scenario"] = scenario,
            ["e"] = at?.E, ["n"] = at?.N, ["alt"] = at?.Alt,
            ["ride"] = me == null ? "" : RideLabel(me.Ride),
            ["wall"] = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), ["shot"] = shotFile, ["top"] = top,
        });
        var notes = _notes!;
        Queue(() => notes.WriteLine(line));
        Event("note", ("id", id), ("text", text), ("source", source), ("scenario", scenario));
        var pin = at is { } a ? new EN(a.E, a.N) : (EN?)null;
        if (pin is { } pp) Notes.Add(new TopNote(t, pp.E, pp.N, text));
        _awaitingTop.Add((id, t, pin));
        GD.Print($"[playtest] note {id}: {text}");
        return id;
    }

    /// <summary>Where the local player was at <paramref name="t"/>, from the ring: a note is stamped where its key was pressed.</summary>
    private GlobalPos? LocalAt(double t)
    {
        int me = LocalId;
        TraceFrame? best = null;
        for (int i = 0; i < Ring.Count; i++)
            if (Ring[i].TryGet(me, out _) && (best == null || Math.Abs(Ring[i].T - t) < Math.Abs(best.T - t))) best = Ring[i];
        return best != null && best.TryGet(me, out var s) ? new GlobalPos(s.E, s.N, s.Alt) : null;
    }

    /// <summary>A note's top view: the ring's 10 s either side of it, centred on it, as a PNG beside its screenshot.</summary>
    private void RenderNoteTop(int id, double t, EN? at, bool exiting = false)
    {
        double w = SessionReport.NoteWindow;
        var frames = Ring.Window(t - w, t + w);
        double? radius = null;
        if (at is { } c)
        {
            // as far as the local player went in the window, and whatever stood within 120 m of the note
            double far = 0;
            int me = LocalId;
            foreach (var f in frames)
                for (int i = 0; i < f.Count; i++)
                {
                    ref var s = ref f.States[i];
                    double d = Math.Max(Math.Abs(s.E - c.E), Math.Abs(s.N - c.N));
                    bool minor = Entities.TryGetValue(s.Id, out var e) && e.IsMinor;
                    if (s.Id == me || !minor && d < 120) far = Math.Max(far, d);
                }
            radius = Math.Clamp(far + 12, 30, 400);
        }
        var shapes = TopViewScene.Build(new TopViewInput
        {
            Frames = frames, Entities = Entities, T0 = t - w, T1 = t + w, At = t, Around = at, Radius = radius, Notes = Notes,
        });
        shapes.Title = $"note {id} at {t.ToString("0.0", CultureInfo.InvariantCulture)} s (±{w:0} s)";
        string path = Path.Combine(Dir, $"notes/{id:000}_top.png");
        // the ground is read off the main thread (a client online streams it); the game quitting cannot wait for it
        if (exiting || Ground?.Source == null) Saved(TopViewImage.Save(shapes, null, NotePx, path));
        else _ = WithGround(shapes, NotePx, ground => Saved(TopViewImage.Save(shapes, ground, NotePx, path)));

        void Saved(Error err)
        {
            if (err != Error.Ok) GD.PushWarning($"[playtest] note {id}'s top view not drawn: {err}");
        }
    }

    private const int NotePx = 800;

    /// <summary>Reads the ground under <paramref name="shapes"/> on a worker, then runs <paramref name="then"/> on the main thread, with null if there is none (or it took over 10 s).</summary>
    private async Task WithGround(TopViewShapes shapes, int px, Action<TopGround?> then)
    {
        TopGround? ground = null;
        if (Ground?.Source is { } source)
            try
            {
                var load = Task.Run(() => TopViewGround.LoadAsync(source, shapes.Min, shapes.Side, px));
                if (await Task.WhenAny(load, Task.Delay(10_000)) == load && load.Result.Tiles > 0) ground = load.Result;
            }
            catch (Exception e) { GD.PushWarning($"[playtest] top view ground: {e.GetBaseException().Message}"); }
        // back on the main thread: Godot's synchronisation context resumes the await there
        then(ground);
    }

    /// <summary>A top view straight from memory or the files, as PNG bytes (MCP <c>topview</c>), over its ground.</summary>
    public async Task<byte[]?> TopView(double t0, double t1, double? at, EN? around, double? radius, int px)
    {
        List<TraceFrame> frames;
        IReadOnlyDictionary<int, TraceEntity> entities = Entities;
        if (Ring.Count > 0 && Ring[0].T <= t0) frames = Ring.Window(t0, t1);
        else
        {
            Sync();
            var reader = new TraceReader(Dir);
            frames = reader.Window(t0, t1);
            entities = reader.Entities;
        }
        var shapes = TopViewScene.Build(new TopViewInput
        {
            Frames = frames, Entities = entities, T0 = t0, T1 = t1, At = at, Around = around, Radius = radius, Notes = Notes,
        });
        byte[]? png = null;
        await WithGround(shapes, px, ground => png = TopViewImage.Render(shapes, ground, px)?.SavePngToBuffer());
        return png;
    }

    // ---- the end -------------------------------------------------------------------------

    /// <summary>
    /// Ends the session: the notes still waiting get their top view, the perf and movie recordings
    /// stop, every file is closed and <c>report.md</c> written. Done once the movie is saved (it is
    /// joined on its spool's worker); when the game is <paramref name="exiting"/> nothing can wait for
    /// that, so the movie is left out and the files are closed at once.
    /// </summary>
    public Task Stop(bool exiting = false)
    {
        if (_finished != null) return _finished.Task;
        _finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_trace == null) { _finished.SetResult(); return _finished.Task; }
        DescribeWaiting(Now, force: true);
        foreach (var (id, t, at) in _awaitingTop) RenderNoteTop(id, t, at, exiting);
        _awaitingTop.Clear();
        if (_perf is { Recording: true } perf) perf.Stop();
        if (_movie && !exiting && ReplayRecorder.Instance is { Recording: true } replay)
        {
            void Saved(MovieProject? p, string? error)
            {
                replay.SessionStopped -= Saved;
                string name = $"Playtest {Path.GetFileName(Dir.TrimEnd('/', '\\'))}";
                if (p != null)
                {
                    p.Name = name;
                    error = MovieSession.Write(p);
                }
                Event("movie", ("name", p == null ? "" : name), ("error", error ?? ""), ("where", "user://movies, open it in the movie studio"));
                Finish(exiting: false);
            }
            replay.SessionStopped += Saved;
            replay.StopSession();
        }
        else
        {
            if (_movie) Event("movie", ("name", ""), ("error", "the game closed before the recording could be saved"));
            Finish(exiting);
        }
        return _finished.Task;
    }

    private void Finish(bool exiting)
    {
        Event("session", ("what", "stop"));
        _stopped = true;
        var trace = _trace;
        var events = _events;
        var notes = _notes;
        string dir = Dir;
        Task work;
        lock (_gate)
            work = _work = _work.ContinueWith(_ =>
            {
                try
                {
                    trace?.Dispose();
                    events?.Dispose();
                    notes?.Dispose();
                    SessionReport.Write(dir);
                }
                catch (Exception e) { _error ??= e.Message; }
            }, TaskScheduler.Default);
        _trace = null;
        var done = _finished!;
        void Said() => GD.Print(_error == null ? $"[playtest] session saved: {Path.Combine(dir, SessionReport.ReportFile)}" : $"[playtest] session saved with an error: {_error}");
        if (exiting)
        {
            work.Wait(TimeSpan.FromSeconds(10));
            Said();
            done.TrySetResult();
        }
        else work.ContinueWith(_ => Callable.From(() => { Said(); done.TrySetResult(); }).CallDeferred(), TaskScheduler.Default);
    }

    private static string Git(params string[] args)
    {
        var output = new Godot.Collections.Array();
        var all = new List<string> { "-C", PlaytestDirector.Root };
        all.AddRange(args);
        return OS.Execute("git", all.ToArray(), output) == 0 && output.Count > 0 ? output[0].AsString().Trim() : "";
    }
}
