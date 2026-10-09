using HttpClient = System.Net.Http.HttpClient;
using HttpMethod = System.Net.Http.HttpMethod;
using HttpRequestMessage = System.Net.Http.HttpRequestMessage;
using StringContent = System.Net.Http.StringContent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using UnitSport.Core;

namespace UnitSport.Playtest;

/// <summary>
/// <c>--playtestcheck</c> (#751, quick tier, no world): every scenario is found and well-formed (each
/// cover glob matches a file, so a renamed file cannot silently make a verdict immortal), the MCP
/// endpoint answers over real HTTP and refuses browsers, and a verdict round-trips the ledger.
/// The scenarios' setups themselves are <c>--playtest --playtest-smoke</c> on the flat course.
/// </summary>
public static class PlaytestCheck
{
    public static bool Requested => CmdArgs.Has("--playtestcheck");

    public static bool ListRequested => CmdArgs.Has("--playtest-list");

    /// <summary>
    /// <c>--playtest-list [all]</c> (tools/playtest.sh pending): one line per scenario due (pending,
    /// stale, failed), or every one with <c>all</c>, as <c>status id course "title"</c>, then quits.
    /// </summary>
    public static int List()
    {
        bool all = CmdArgs.Value("--playtest-list") == "all";
        int due = 0;
        foreach (var e in PlaytestEntry.Discover())
        {
            var status = PlaytestLedger.StatusOf(PlaytestLedger.Read(PlaytestDirector.LedgerDir, e.Id),
                PlaytestLedger.CurrentHash(PlaytestDirector.Root, e.Scenario.Covers));
            if (status != PlaytestStatus.Validated) due++;
            if (all || status != PlaytestStatus.Validated)
                GD.Print($"[playtest-list] {status.ToString().ToLowerInvariant(),-9} {e.Id,-28} {e.Scenario.Course ?? "any",-9} \"{e.Title}\" [{e.Category}]");
        }
        GD.Print($"[playtest-list] {due} due");
        return 0;
    }

    public static int Run()
    {
        int failed = 0;
        // quiet: printed only when it fails (one line per cover glob would bury the rest)
        void Expect(bool ok, string what, bool quiet = false)
        {
            if (!ok) failed++;
            if (!ok || !quiet) GD.Print($"[playtestcheck] {(ok ? "ok  " : "FAIL")} {what}");
        }

        string root = PlaytestDirector.Root;
        var entries = PlaytestEntry.Discover();
        Expect(entries.Count >= 10, $"{entries.Count} scenarios found");
        foreach (var e in entries)
        {
            Expect(e.Id.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '-'), $"{e.Id}: id is lower-case-dashed (it names a file)", quiet: true);
            var s = e.Scenario;
            Expect(s.Covers.Any(g => !g.StartsWith('!')), $"{e.Id}: covers something", quiet: true);
            foreach (string glob in s.Covers.Where(g => !g.StartsWith('!')))
                Expect(PlaytestLedger.Expand(root, [glob]).Count > 0, $"{e.Id}: '{glob}' matches a file", quiet: true);
            if (s.Course != null) Expect(Terrain.Fixture.FixtureCourse.Names.Contains(s.Course), $"{e.Id}: course '{s.Course}' exists", quiet: true);
        }

        Expect(failed == 0, "every scenario's id, covers and course are valid");

        // the transport and the protocol, over a real socket
        var echo = new McpTool("echo", "echo", McpTool.Schema(("text", "string", "")),
            (args, _) => Task.FromResult(McpToolResult.Text(args.Str("text") ?? "")));
        using (var http = new LoopbackHttp(0, new McpProtocol("check", "1", "", [echo]).Handle))
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        {
            string url = $"http://127.0.0.1:{http.Port}/mcp";
            (int Code, string Body) Post(string json, string? origin = null, string? host = null)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                if (origin != null) request.Headers.Add("Origin", origin);
                if (host != null) request.Headers.Host = host;
                var reply = client.Send(request);
                return ((int)reply.StatusCode, reply.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
            var init = Post("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","clientInfo":{"name":"check"}}}""");
            Expect(init.Code == 200 && init.Body.Contains("\"protocolVersion\":\"2025-06-18\""), $"initialize: {init.Code}");
            Expect(Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""").Code == 202, "a notification gets 202");
            var list = Post("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            Expect(list.Body.Contains("\"echo\""), "tools/list names the tool");
            var call = Post("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"grüezi"}}}""");
            Expect(JsonNode.Parse(call.Body)?["result"]?["content"]?[0]?["text"]?.GetValue<string>() == "grüezi", "tools/call round-trips UTF-8");
            Expect(Post("{}", origin: "https://evil.example").Code == 403, "a web page's Origin is refused");
            Expect(Post("{}", host: "evil.example").Code == 403, "a foreign Host (DNS rebinding) is refused");
        }

        // a verdict and the ledger, in test_output
        string dir = Path.Combine(root, "test_output", "playtest", "ledgercheck");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        PlaytestLedger.Write(dir, "check", "Check", validated: true, "abc", ["src/x.cs"], "check", "0000000", "fine", DateTime.UtcNow);
        var read = PlaytestLedger.Read(dir, "check");
        Expect(read != null && read.Verdict == "validated" && read.Notes.Count == 1, "a verdict is written and read back");
        Expect(PlaytestLedger.StatusOf(read, "abc") == PlaytestStatus.Validated && PlaytestLedger.StatusOf(read, "abd") == PlaytestStatus.Stale,
            "the same hash keeps it validated, another makes it stale");
        PlaytestLedger.Write(dir, "check", "Check", validated: false, "abc", ["src/x.cs"], "check", "0000000", "broke", DateTime.UtcNow);
        Expect(PlaytestLedger.Read(dir, "check") is { Verdict: "failed", Notes.Count: 2 }, "a later verdict keeps the history");

        GD.Print(failed == 0 ? "[playtestcheck] RESULT: ok" : $"[playtestcheck] RESULT: FAILED ({failed})");
        return failed == 0 ? 0 : 1;
    }
}
