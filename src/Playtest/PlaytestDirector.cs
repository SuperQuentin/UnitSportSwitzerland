using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Playtest;

/// <summary>One line of the playtest conversation: who said it (player, Claude, system) and what.</summary>
public sealed record PlaytestLine(string Who, string Text);

/// <summary>
/// The playtest suite (#751, docs/notes/general/playtest.md): <c>--playtest</c> in a Debug build.
/// It runs the <c>[PlaytestScenario]</c>s one after another in this one game, keeps the verdicts in
/// the committed ledger (<see cref="PlaytestLedger"/>), and serves an MCP endpoint
/// (<c>http://127.0.0.1:7801/mcp</c>, the repo's <c>.mcp.json</c>) through which a Claude Code session
/// hears the player, answers in the panel, and changes the running game: scenario knobs, tunables,
/// node properties, chat commands, and a rebuild that relaunches straight back into the scenario.
///
/// <para>
/// Tool calls arrive on socket threads; anything touching the scene tree is queued to the main
/// thread (<see cref="OnMain{T}"/>), drained in <c>_Process</c>.
/// </para>
/// </summary>
public partial class PlaytestDirector : Node
{
    public const int DefaultPort = 7801;
    private const string ResumeFile = "user://playtest_resume.json";

    public static bool Requested => OS.IsDebugBuild() && CmdArgs.Has("--playtest");

    public static PlaytestDirector? Instance { get; private set; }

    private readonly Func<FootPlayer?> _local;
    private readonly PlaytestContext _ctx;
    private readonly ConcurrentQueue<Func<Task>> _main = new();
    private readonly Channel<string> _toClaude = Channel.CreateUnbounded<string>();
    private readonly PlaytestLog _log = new();
    private LoopbackHttp? _http;
    private bool _stageSet;
    private double _setupStarted = -1;

    /// <summary>The scenarios, ordered as the panel lists them.</summary>
    public List<PlaytestEntry> Entries { get; private set; } = [];

    /// <summary>Each scenario's standing, by id (recomputed after a verdict and on <see cref="RefreshStatus"/>).</summary>
    public Dictionary<string, PlaytestStatus> Status { get; } = [];

    public PlaytestEntry? Current { get; private set; }

    /// <summary>The current scenario failed to set up: why.</summary>
    public string? SetupError { get; private set; }

    public List<PlaytestLine> Conversation { get; } = [];

    /// <summary>The MCP client's name once one has connected (Claude Code says "claude-code").</summary>
    public string? Client { get; private set; }

    /// <summary>Wall-clock time Claude last asked for the player's messages; it is listening while one is pending.</summary>
    private long _lastPollMs;
    private int _waiting;

    /// <summary>Anything the panel shows changed.</summary>
    public event Action? Changed;

    public static string Root => ProjectSettings.GlobalizePath("res://");
    public static string LedgerDir => Path.Combine(Root, "tests", "playtests");

    private string _by = "", _commit = "";

    internal PlaytestDirector(Func<FootPlayer?> local, Action<string> command, (double E, double N)? courseStart)
    {
        Name = "Playtest";
        _local = local;
        _ctx = new PlaytestContext(this, local, command) { CourseStart = courseStart };
    }

    /// <summary>Claude is connected and waiting for the player right now (or was, a moment ago).</summary>
    public bool Listening => Volatile.Read(ref _waiting) > 0 || System.Environment.TickCount64 - Interlocked.Read(ref _lastPollMs) < 5000;

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        _http?.Dispose();
        OS.RemoveLogger(_log);
    }

    public override void _Ready()
    {
        OS.AddLogger(_log);
        Entries = PlaytestEntry.Discover();
        _by = Git("config", "user.name");
        _commit = Git("rev-parse", "--short", "HEAD");
        RefreshStatus();

        var protocol = new McpProtocol("unitsport-playtest", "1", Instructions, Tools());
        protocol.Initialized += client => OnMain(() => { Client = client; Say("system", $"{client} connected."); return Task.FromResult(0); });
        protocol.ToolCalled += tool => { if (Client == null) _ = Sync(() => { Client ??= "Claude"; Changed?.Invoke(); return 0; }); };
        // the smoke run must not take the port a real session (or another check) is using
        int port = Smoke ? 0 : CmdArgs.Int("--playtest-port") ?? DefaultPort;
        try
        {
            _http = new LoopbackHttp(port, protocol.Handle);
            GD.Print($"[playtest] MCP server on http://127.0.0.1:{_http.Port}/mcp, {Entries.Count} scenarios");
        }
        catch (System.Net.Sockets.SocketException e)
        {
            GD.PushError($"[playtest] port {port} is taken ({e.Message}): run with --playtest-port N and point .mcp.json at it");
        }

        AddChild(PlaytestPanel.Create(this));
        if (_http == null || !CmdArgs.Has("--playtest-resume")) Say("system", _http == null
            ? "The playtest server could not start (port taken): Claude cannot reach this game."
            : "Playtest mode. In Claude Code, run /playtest (or ask it to listen to the game), then write here.");
        if (CmdArgs.Has("--playtest-resume")) _ = Resume();
        if (Smoke) _ = RunSmoke();
    }

    /// <summary><c>--playtest-smoke</c>: set up every scenario this course can run, one after another, and fail on any setup error (tools/test.sh quick).</summary>
    private static bool Smoke => CmdArgs.Has("--playtest-smoke");

    private async Task RunSmoke()
    {
        MouseCapture.Disabled = true;
        int failed = 0, ran = 0;
        foreach (var e in Entries.Where(Playable).ToList())
        {
            ran++;
            string result = await Start(e);
            await _ctx.Wait(1.0);
            bool ok = SetupError == null && _local() != null;
            if (!ok) failed++;
            GD.Print($"[playtest] smoke {e.Id}: {(ok ? "ok" : "FAILED " + (SetupError ?? result))}");
        }
        await _ctx.Clear();
        GD.Print(failed == 0 && ran > 0 ? $"[playtest] RESULT: ok, {ran} scenarios set up" : $"[playtest] RESULT: FAILED {failed} of {ran} setups");
        GetTree().Quit(failed == 0 && ran > 0 ? 0 : 1);
    }

    public override void _Process(double delta)
    {
        while (_main.TryDequeue(out var work)) _ = work();
        if (Current?.Scenario.Tick is { } tick && SetupError == null && _setupStarted < 0) tick(_ctx, delta);
    }

    // ---- main thread ---------------------------------------------------------------------

    /// <summary>Runs <paramref name="work"/> on the main thread and hands back its result.</summary>
    private Task<T> OnMain<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _main.Enqueue(async () =>
        {
            try { done.SetResult(await work()); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task;
    }

    /// <summary><see cref="OnMain{T}"/> for work that finishes at once.</summary>
    private Task<T> Sync<T>(Func<T> work) => OnMain(() => Task.FromResult(work()));

    // ---- what the player and Claude do ---------------------------------------------------

    /// <summary>A line in the conversation; the player's (and verdicts, scenario changes) also go to Claude.</summary>
    public void Say(string who, string text)
    {
        Conversation.Add(new PlaytestLine(who, text));
        if (Conversation.Count > 200) Conversation.RemoveAt(0);
        Changed?.Invoke();
    }

    /// <summary>The player wrote in the panel.</summary>
    public void PlayerSays(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Say("you", text);
        Tell($"player: {text}");
    }

    private void Tell(string evt)
    {
        string where = Current == null ? "no scenario" : $"scenario {Current.Id}";
        _toClaude.Writer.TryWrite($"[{where}] {evt}");
    }

    public void RefreshStatus()
    {
        foreach (var e in Entries)
        {
            string hash;
            try { hash = PlaytestLedger.CurrentHash(Root, e.Scenario.Covers); }
            catch (IOException) { hash = "unreadable"; }
            Status[e.Id] = PlaytestLedger.StatusOf(PlaytestLedger.Read(LedgerDir, e.Id), hash);
        }
        Changed?.Invoke();
    }

    /// <summary>Whether this run can play <paramref name="e"/> without a relaunch (its course is the one loaded).</summary>
    public bool Playable(PlaytestEntry e) => e.Scenario.Course == null || e.Scenario.Course == Systems.FixtureCourse;

    /// <summary>The next scenario due (pending, stale, then failed), after the current one.</summary>
    public PlaytestEntry? NextDue()
    {
        int from = Current == null ? -1 : Entries.IndexOf(Current);
        foreach (var want in new[] { PlaytestStatus.Pending, PlaytestStatus.Stale, PlaytestStatus.Failed })
            for (int k = 1; k <= Entries.Count; k++)
            {
                var e = Entries[(from + k + Entries.Count) % Entries.Count];
                if (Status.GetValueOrDefault(e.Id) == want && e != Current) return e;
            }
        return null;
    }

    /// <summary>Tears the current scenario down and sets up <paramref name="e"/> (relaunching for another course).</summary>
    public async Task<string> Start(PlaytestEntry e, Dictionary<string, double>? parameters = null)
    {
        if (!Playable(e))
        {
            Say("system", $"'{e.Title}' needs the {e.Scenario.Course} course: relaunching on it.");
            Relaunch(e, rebuild: false, course: e.Scenario.Course);
            return $"relaunching on the {e.Scenario.Course} course for {e.Id}";
        }
        await _ctx.Clear();
        Current = e;
        SetupError = null;
        _ctx.Params = new Dictionary<string, double>(e.Scenario.Params);
        if (parameters != null) foreach (var (k, v) in parameters) _ctx.Params[k] = v;
        _setupStarted = Time.GetTicksMsec();
        Say("system", $"▶ {e.Title}");
        Changed?.Invoke();
        try
        {
            if (!_stageSet)
            {
                var me = await _ctx.OnFoot();
                _ctx.Stage = me.GlobalPosition;
                _ctx.StageYaw = me.Rotation.Y;
                _stageSet = true;
            }
            // a frame for the cleared vehicles to leave before the new ones take their places
            await _ctx.Wait(0.2);
            await e.Scenario.Setup(_ctx);
            Tell($"scenario started: {e.Id} \"{e.Title}\"");
            return $"started {e.Id}";
        }
        catch (Exception x)
        {
            SetupError = x.Message;
            Say("system", $"Setup failed: {x.Message}");
            Tell($"scenario {e.Id} setup FAILED: {x}");
            return $"setup of {e.Id} failed: {x}";
        }
        finally
        {
            _setupStarted = -1;
            Changed?.Invoke();
        }
    }

    /// <summary>The player's verdict on the current scenario, written to the ledger.</summary>
    public PlaytestRecord? Verdict(bool validated, string note)
    {
        if (Current is not { } e) return null;
        string hash = PlaytestLedger.CurrentHash(Root, e.Scenario.Covers);
        var record = PlaytestLedger.Write(LedgerDir, e.Id, e.Title, validated, hash, e.Scenario.Covers,
            _by.Length > 0 ? _by : "unknown", _commit, note, DateTime.UtcNow);
        Status[e.Id] = PlaytestLedger.StatusOf(record, hash);
        Say("system", validated ? $"✔ Validated: {e.Title}" : $"✖ Failed: {e.Title}{(note.Length > 0 ? " — " + note : "")}");
        Tell(validated ? $"VALIDATED {e.Id}{(note.Length > 0 ? ": " + note : "")}" : $"FAILED {e.Id}: {note}");
        return record;
    }

    // ---- relaunch ------------------------------------------------------------------------

    private sealed record ResumeState(string? Scenario, Dictionary<string, double>? Params, List<PlaytestLine> Conversation, string Reason);

    /// <summary>
    /// Saves where the session is and starts the game again, rebuilding first when asked, then quits:
    /// a helper (tools/playtest.sh relaunch) waits for this process to end, so the build can replace
    /// the assembly this one has loaded. The new game reopens the scenario and the conversation.
    /// </summary>
    public void Relaunch(PlaytestEntry? scenario, bool rebuild, string? course = null)
    {
        var state = new ResumeState(scenario?.Id ?? Current?.Id, Current == scenario ? _ctx.Params : null,
            Conversation.TakeLast(60).ToList(), rebuild ? "rebuilt" : "relaunched");
        using (var f = Godot.FileAccess.Open(ResumeFile, Godot.FileAccess.ModeFlags.Write))
            f?.StoreString(JsonSerializer.Serialize(state));

        var user = CmdArgs.All.Where(a => a != "--playtest-resume").ToList();
        if (course != null)
        {
            // the course is fixed at boot: --world fixture --chunks fixture:<course>
            int c = user.IndexOf("--chunks");
            if (c >= 0 && c + 1 < user.Count) user[c + 1] = Systems.FixturePrefix + course;
            else user.AddRange(["--chunks", Systems.FixturePrefix + course]);
            int w = user.IndexOf("--world");
            if (w >= 0 && w + 1 < user.Count) user[w + 1] = "fixture";
            else user.AddRange(["--world", "fixture"]);
        }
        user.Add("--playtest-resume");
        var engine = OS.GetCmdlineArgs().ToList();
        if (!engine.Contains("--path")) engine.InsertRange(0, ["--path", Root]);
        var game = engine.Append("--").Concat(user).ToArray();

        // forward slashes: Git Bash on Windows reads C:/x/y, not backslashed paths
        string script = Root.TrimEnd('/') + "/tools/playtest.sh";
        var helper = new List<string> { script, "relaunch", OS.GetProcessId().ToString(CultureInfo.InvariantCulture), rebuild ? "build" : "nobuild", OS.GetExecutablePath() };
        helper.AddRange(game);
        int pid = OS.CreateProcess(Bash(), helper.ToArray());
        if (pid <= 0)
        {
            Say("system", "Could not start the relaunch helper (bash tools/playtest.sh): restart the game by hand.");
            return;
        }
        GD.Print($"[playtest] relaunching ({(rebuild ? "rebuild" : "no rebuild")}), helper pid {pid}");
        GetTree().Quit();
    }

    /// <summary>bash: Git for Windows' on Windows (tools/*.sh are bash scripts), else the one on PATH.</summary>
    private static string Bash()
    {
        if (!OS.HasFeature("windows")) return "bash";
        foreach (string p in new[] { @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files (x86)\Git\bin\bash.exe" })
            if (File.Exists(p)) return p;
        return "bash";
    }

    private async Task Resume()
    {
        if (!Godot.FileAccess.FileExists(ResumeFile)) return;
        string json = Godot.FileAccess.GetFileAsString(ResumeFile);
        DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(ResumeFile));
        ResumeState? state;
        try { state = JsonSerializer.Deserialize<ResumeState>(json); }
        catch (JsonException) { return; }
        if (state == null) return;
        Conversation.AddRange(state.Conversation);
        string buildLog = Path.Combine(Root, "test_output", "playtest", "build.log");
        bool buildFailed = state.Reason == "rebuilt" && File.Exists(buildLog) && File.ReadAllText(buildLog).Contains("Build FAILED");
        Say("system", buildFailed ? "Back, but the BUILD FAILED: this is the previous build (test_output/playtest/build.log)." : $"Back ({state.Reason}).");
        Tell(buildFailed ? $"game restarted but the build FAILED, previous build running; see test_output/playtest/build.log" : $"game {state.Reason} and back");
        if (state.Scenario != null && Entries.FirstOrDefault(e => e.Id == state.Scenario) is { } entry && Playable(entry))
            await Start(entry, state.Params);
    }

    // ---- MCP tools -----------------------------------------------------------------------

    private const string Instructions =
        "This is a running UnitSportSwitzerland game in playtest mode. A human is playing scenarios and talks to you " +
        "through the in-game playtest panel. Loop: call wait_for_player (it blocks until the player writes, validates, " +
        "fails or changes scenario), act, answer with say, repeat. You can change the running game live with set_param " +
        "(scenario knobs, then reset_scenario), set_tunable ([Tunable] statics), node_set (any node property), run_command " +
        "(chat commands), and see it with screenshot, status, node_get and log_tail. For a code change: edit the source, " +
        "then restart(rebuild=true): the game rebuilds, relaunches and reopens the same scenario. Read " +
        "docs/notes/general/playtest.md in the repo first.";

    private static JsonElement Obj(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private List<McpTool> Tools() =>
    [
        new("wait_for_player",
            "Blocks until the player does something in the playtest panel (a message, Validate, Fail, a scenario change), then returns everything that happened since the last call. Call it again after each answer: while it waits, the panel shows that Claude is listening. Returns 'nothing yet' after timeout_s (default 120, max 600).",
            McpTool.Schema(("timeout_s", "number", "seconds to wait, default 120")),
            async (args, token) =>
            {
                double timeout = Math.Clamp(args.Num("timeout_s") ?? 120, 1, 600);
                Interlocked.Increment(ref _waiting);
                _ = Sync(() => { Changed?.Invoke(); return 0; });
                try
                {
                    var lines = new List<string>();
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cts.CancelAfter(TimeSpan.FromSeconds(timeout));
                    try
                    {
                        lines.Add(await _toClaude.Reader.ReadAsync(cts.Token));
                        // whatever else came with it (a message right after a verdict)
                        await Task.Delay(300, token);
                        while (_toClaude.Reader.TryRead(out var more)) lines.Add(more);
                    }
                    catch (OperationCanceledException) { }
                    return McpToolResult.Text(lines.Count == 0 ? "nothing yet (call wait_for_player again)" : string.Join("\n", lines));
                }
                finally
                {
                    Interlocked.Decrement(ref _waiting);
                    Interlocked.Exchange(ref _lastPollMs, System.Environment.TickCount64);
                    _ = Sync(() => { Changed?.Invoke(); return 0; });
                }
            }),
        new("say", "Shows your message to the player in the playtest panel. Keep it short: they are playing.",
            McpTool.Schema([("text", "string", "what to tell the player")], ["text"]),
            (args, _) => Sync(() => { Say("claude", args.Str("text") ?? ""); return McpToolResult.Text("shown"); })),
        new("status",
            "The current scenario (instructions, checklist, knobs, setup error), every scenario with its standing (pending, stale, validated, failed) and whether it can run without relaunching, and where the player is.",
            McpTool.Schema(), (_, _) => Sync(() => McpToolResult.Text(StatusText()))),
        new("start_scenario", "Tears down the current scenario and sets up another, without reloading (a scenario on another fixture course relaunches the game on it). 'next' takes the next one due.",
            McpTool.Schema([("id", "string", "scenario id, or 'next'")], ["id"]),
            (args, _) => OnMain(async () =>
            {
                string id = args.Str("id") ?? "";
                var e = id == "next" ? NextDue() : Entries.FirstOrDefault(x => x.Id == id);
                return e == null ? McpToolResult.Error($"no scenario '{id}'") : McpToolResult.Text(await Start(e));
            })),
        new("reset_scenario", "Sets the current scenario up again from scratch (after set_param, or when the player asks).",
            McpTool.Schema(),
            (_, _) => OnMain(async () => Current == null ? McpToolResult.Error("no scenario running") : McpToolResult.Text(await Start(Current, _ctx.Params)))),
        new("set_param", "Sets one of the current scenario's knobs (status lists them). Takes effect at the next reset_scenario.",
            McpTool.Schema([("name", "string", "knob name"), ("value", "number", "new value")], ["name", "value"]),
            (args, _) => Sync(() =>
            {
                string name = args.Str("name") ?? "";
                if (Current == null || !Current.Scenario.Params.ContainsKey(name)) return McpToolResult.Error($"no knob '{name}' on the current scenario");
                _ctx.Params[name] = args.Num("value") ?? 0;
                return McpToolResult.Text($"{name} = {_ctx.Params[name].ToString(CultureInfo.InvariantCulture)} (reset_scenario to apply)");
            })),
        new("list_tunables", "Every [Tunable] static field or property in the game with its current value and hint. A const can't be tuned: make it a [Tunable] static, restart(rebuild=true), then tune it.",
            McpTool.Schema(("filter", "string", "only names containing this")),
            (args, _) => Sync(() => McpToolResult.Text(ListTunables(args.Str("filter"))))),
        new("set_tunable", "Changes a [Tunable] static live, effective immediately. Name as list_tunables prints it (Type.Member).",
            McpTool.Schema([("name", "string", "Type.Member"), ("value", "string", "new value (number, true/false or text)")], ["name", "value"]),
            (args, _) => Sync(() => SetTunable(args.Str("name") ?? "", args.Str("value") ?? args.Raw("value")?.ToString() ?? ""))),
        new("node_get", "Reads a node: its class, children, and the listed properties (all script/editor properties when none are listed). Path from the scene root (e.g. 'ClientWorld/Player'), or 'player' for the local player, or 'scenario:N' for the Nth vehicle the scenario placed.",
            McpTool.Schema([("path", "string", "node path, 'player' or 'scenario:N'"), ("properties", "string", "comma-separated property names (optional)")], ["path"]),
            (args, _) => Sync(() => NodeGet(args.Str("path") ?? "", args.Str("properties")))),
        new("node_set", "Sets a property on a node live (Godot properties: position, rotation, scale, velocity, visible, [Export]ed fields...). Value as JSON: a number, true/false, a string, or [x,y,z] for a vector.",
            McpTool.Schema([("path", "string", "as node_get"), ("property", "string", "property name"), ("value", "string", "JSON value")], ["path", "property", "value"]),
            (args, _) => Sync(() => NodeSet(args.Str("path") ?? "", args.Str("property") ?? "", args.Str("value") ?? args.Raw("value")?.GetRawText() ?? ""))),
        new("run_command", "Runs a chat command as the player would type it, offline as admin (/time 18:00, /tp <place>, /give <item>, /help lists them).",
            McpTool.Schema([("line", "string", "the command line, starting with /")], ["line"]),
            (args, _) => Sync(() => { _ctx.Command(args.Str("line") ?? ""); return McpToolResult.Text("sent; see log_tail for its answer"); })),
        new("screenshot", "What the player sees right now, as a PNG (the playtest panel hidden unless with_panel).",
            McpTool.Schema(("with_panel", "boolean", "keep the playtest panel in the picture"), ("width", "number", "max width in px, default 1280")),
            (args, _) => OnMain(() => Screenshot(args.Bool("with_panel") ?? false, (int)(args.Num("width") ?? 1280)))),
        new("log_tail", "The game's last log lines (prints, warnings, errors).",
            McpTool.Schema(("lines", "number", "how many, default 60"), ("filter", "string", "only lines containing this")),
            (args, _) => Task.FromResult(McpToolResult.Text(_log.Tail((int)(args.Num("lines") ?? 60), args.Str("filter"))))),
        new("restart", "Relaunches the game and reopens the current scenario and conversation. rebuild=true runs dotnet build first, after this game has quit (the build log goes to test_output/playtest/build.log; a failed build relaunches the previous one and says so). Use it after editing C#. You lose this connection for ~20-60 s: call wait_for_player again until it answers.",
            McpTool.Schema(("rebuild", "boolean", "dotnet build first (default true)")),
            (args, _) => Sync(() =>
            {
                bool rebuild = args.Bool("rebuild") ?? true;
                Say("system", rebuild ? "Claude is rebuilding the game: back in a moment…" : "Relaunching…");
                // after this reply has left: the quit would cut it off
                GetTree().CreateTimer(0.5).Timeout += () => Relaunch(Current, rebuild);
                return McpToolResult.Text("relaunching; call wait_for_player until the game answers again");
            })),
    ];

    private string StatusText()
    {
        var sb = new StringBuilder();
        var me = _local();
        sb.AppendLine($"course: {Systems.FixtureCourse ?? "real map"}; player: {(me == null ? "none (free camera)" : $"{PlaytestContext.Label(me.Ride)} at {me.GlobalPosition}")}");
        if (Current is { } c)
        {
            var s = c.Scenario;
            sb.AppendLine($"CURRENT {c.Id} \"{c.Title}\" [{c.Category}] ({c.Where})");
            sb.AppendLine($"  instructions: {s.Instructions}");
            foreach (string item in s.Checklist) sb.AppendLine($"  - {item}");
            if (_ctx.Params.Count > 0) sb.AppendLine("  knobs: " + string.Join(", ", _ctx.Params.Select(p => $"{p.Key}={p.Value.ToString(CultureInfo.InvariantCulture)}")));
            sb.AppendLine($"  covers: {string.Join(", ", s.Covers)}");
            if (SetupError != null) sb.AppendLine($"  SETUP ERROR: {SetupError}");
        }
        else sb.AppendLine("no scenario running");
        sb.AppendLine("scenarios:");
        foreach (var e in Entries)
            sb.AppendLine($"  {Status.GetValueOrDefault(e.Id).ToString().ToLowerInvariant(),-9} {e.Id} \"{e.Title}\" [{e.Category}]{(Playable(e) ? "" : $" (needs course {e.Scenario.Course})")}");
        return sb.ToString();
    }

    // ---- tunables ------------------------------------------------------------------------

    private static List<(string Name, MemberInfo Member, TunableAttribute Tag)>? _tunables;

    private static List<(string Name, MemberInfo Member, TunableAttribute Tag)> Tunables() => _tunables ??=
        typeof(PlaytestDirector).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("UnitSport", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetMembers(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => m is FieldInfo { IsLiteral: false, IsInitOnly: false } or PropertyInfo { CanWrite: true })
            .Select(m => (Member: m, Tag: m.GetCustomAttribute<TunableAttribute>()))
            .Where(x => x.Tag != null)
            .Select(x => ($"{x.Member.DeclaringType!.Name}.{x.Member.Name}", x.Member, x.Tag!))
            .OrderBy(x => x.Item1, StringComparer.Ordinal)
            .ToList();

    private static object? Value(MemberInfo m) => m is FieldInfo f ? f.GetValue(null) : ((PropertyInfo)m).GetValue(null);

    private static Type TypeOf(MemberInfo m) => m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType;

    private static string ListTunables(string? filter)
    {
        var list = Tunables().Where(t => filter == null || t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Count == 0) return "no [Tunable] members" + (filter == null ? " yet: tag a static field with [Tunable] (src/Core/TunableAttribute.cs)" : $" matching '{filter}'");
        return string.Join("\n", list.Select(t => $"{t.Name} = {Format(Value(t.Member))} ({TypeOf(t.Member).Name}){(t.Tag.Hint is { } h ? " — " + h : "")}"));
    }

    private static McpToolResult SetTunable(string name, string value)
    {
        var t = Tunables().FirstOrDefault(x => x.Name == name);
        if (t.Member == null) return McpToolResult.Error($"no tunable '{name}' (list_tunables)");
        object converted = Convert.ChangeType(value, TypeOf(t.Member), CultureInfo.InvariantCulture);
        if (t.Member is FieldInfo f) f.SetValue(null, converted); else ((PropertyInfo)t.Member).SetValue(null, converted);
        GD.Print($"[playtest] {name} = {Format(converted)}");
        return McpToolResult.Text($"{name} = {Format(Value(t.Member))}");
    }

    private static string Format(object? v) => v switch
    {
        null => "null",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    // ---- nodes ---------------------------------------------------------------------------

    private Node? Find(string path)
    {
        if (path == "player") return _local();
        if (path.StartsWith("scenario:", StringComparison.Ordinal) && int.TryParse(path[9..], out int n))
            return _ctx.Placed().ElementAtOrDefault(n);
        var root = GetTree().Root;
        return root.GetNodeOrNull(path) ?? root.GetNodeOrNull("/root/" + path.TrimStart('/'))
            ?? root.FindChild(path, true, false);
    }

    private McpToolResult NodeGet(string path, string? properties)
    {
        if (Find(path) is not { } node) return McpToolResult.Error($"no node '{path}'");
        var sb = new StringBuilder($"{node.GetPath()} ({node.GetClass()}{(node.GetScript().Obj is Script s ? ", " + s.ResourcePath : "")})\n");
        var names = properties?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? node.GetPropertyList()
                .Where(p => (p["usage"].AsInt32() & (int)(PropertyUsageFlags.ScriptVariable | PropertyUsageFlags.Editor)) != 0)
                .Select(p => p["name"].AsString())
                .Where(n => n.Length > 0 && !n.Contains('/'))
                .Take(80)
                .ToArray();
        foreach (string name in names) sb.AppendLine($"  {name} = {node.Get(name)}");
        var children = node.GetChildren();
        sb.AppendLine($"  children ({children.Count}): {string.Join(", ", children.Take(40).Select(c => $"{c.Name} ({c.GetClass()})"))}");
        return McpToolResult.Text(sb.ToString());
    }

    private McpToolResult NodeSet(string path, string property, string json)
    {
        if (Find(path) is not { } node) return McpToolResult.Error($"no node '{path}'");
        var current = node.Get(property);
        var value = JsonSerializer.Deserialize<JsonElement>(json);
        Variant v = current.VariantType switch
        {
            Variant.Type.Float => value.GetDouble(),
            Variant.Type.Int => value.GetInt64(),
            Variant.Type.Bool => value.GetBoolean(),
            Variant.Type.String or Variant.Type.StringName => value.GetString() ?? "",
            Variant.Type.Vector3 => new Vector3(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle()),
            Variant.Type.Vector2 => new Vector2(value[0].GetSingle(), value[1].GetSingle()),
            Variant.Type.Color => new Color(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle(), value.GetArrayLength() > 3 ? value[3].GetSingle() : 1f),
            Variant.Type.Nil => throw new ArgumentException($"{node.GetClass()} has no property '{property}' Godot can see (only engine and [Export] properties)"),
            _ => throw new ArgumentException($"'{property}' is a {current.VariantType}: not settable from JSON here"),
        };
        node.Set(property, v);
        GD.Print($"[playtest] {node.GetPath()}.{property} = {node.Get(property)}");
        return McpToolResult.Text($"{property} = {node.Get(property)}");
    }

    // ---- screenshot ----------------------------------------------------------------------

    private async Task<McpToolResult> Screenshot(bool withPanel, int width)
    {
        var panel = GetNodeOrNull<CanvasLayer>(PlaytestPanel.NodeName);
        bool shown = panel?.Visible ?? false;
        if (!withPanel && panel != null) panel.Visible = false;
        // two frames: one to draw without the panel, one for that frame to reach the texture
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        if (panel != null) panel.Visible = shown;
        width = Math.Clamp(width, 320, 1920);
        if (image.GetWidth() > width) image.Resize(width, image.GetHeight() * width / image.GetWidth(), Image.Interpolation.Bilinear);
        return McpToolResult.Image(image.SavePngToBuffer(), $"{image.GetWidth()}x{image.GetHeight()}, scenario {Current?.Id ?? "none"}");
    }

    // ---- git -----------------------------------------------------------------------------

    private static string Git(params string[] args)
    {
        var output = new Godot.Collections.Array();
        var all = new List<string> { "-C", Root };
        all.AddRange(args);
        return OS.Execute("git", all.ToArray(), output) == 0 && output.Count > 0 ? output[0].AsString().Trim() : "";
    }
}
