using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using UnitSport.Playtest;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The playtest suite's pure parts (#751): the MCP protocol, its loopback transport, the ledger.</summary>
public class PlaytestTests
{
    private static McpProtocol Protocol() => new("test", "1", "hello",
    [
        new McpTool("echo", "echo it", McpTool.Schema([("text", "string", "")], ["text"]),
            (args, _) => Task.FromResult(McpToolResult.Text(args.Str("text") ?? ""))),
        new McpTool("boom", "throws", McpTool.Schema(),
            (_, _) => throw new InvalidDataException("kaput")),
    ]);

    private static JsonNode Call(McpProtocol p, string json) => JsonNode.Parse(p.Handle(json, default).GetAwaiter().GetResult()!)!;

    [Fact]
    public void Initialize_agrees_on_a_revision()
    {
        var p = Protocol();
        string? client = null;
        p.Initialized += c => client = c;
        var r = Call(p, """{"jsonrpc":"2.0","id":7,"method":"initialize","params":{"protocolVersion":"2025-03-26","clientInfo":{"name":"claude-code"}}}""");
        Assert.Equal(7, r["id"]!.GetValue<int>());
        Assert.Equal("2025-03-26", r["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("hello", r["result"]!["instructions"]!.GetValue<string>());
        Assert.Equal("claude-code", client);
        // an unknown revision gets the newest one this server speaks
        var later = Call(p, """{"jsonrpc":"2.0","id":"a","method":"initialize","params":{"protocolVersion":"2099-01-01"}}""");
        Assert.Equal(McpProtocol.Revisions[0], later["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("a", later["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Notifications_get_no_reply()
    {
        Assert.Null(await Protocol().Handle("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", default));
    }

    [Fact]
    public void Tools_are_listed_and_called()
    {
        var p = Protocol();
        var list = Call(p, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        Assert.Equal(["echo", "boom"], list["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
        Assert.Equal("object", list["result"]!["tools"]![0]!["inputSchema"]!["type"]!.GetValue<string>());

        var echo = Call(p, """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hoi"}}}""");
        Assert.Equal("hoi", echo["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.False(echo["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void A_throwing_tool_is_a_tool_error_not_a_protocol_error()
    {
        var r = Call(Protocol(), """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"boom"}}""");
        Assert.True(r["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("kaput", r["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Bad_requests_get_json_rpc_errors()
    {
        var p = Protocol();
        Assert.Equal(-32700, Call(p, "{nope")["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32601, Call(p, """{"jsonrpc":"2.0","id":1,"method":"resources/list"}""")["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32602, Call(p, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"nope"}}""")["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void A_batch_answers_each_request()
    {
        var r = Call(Protocol(), """[{"jsonrpc":"2.0","id":1,"method":"ping"},{"jsonrpc":"2.0","method":"notifications/x"},{"jsonrpc":"2.0","id":2,"method":"ping"}]""");
        Assert.Equal(2, r.AsArray().Count);
    }

    [Fact]
    public async Task The_transport_serves_loopback_and_refuses_browsers()
    {
        using var http = new LoopbackHttp(0, Protocol().Handle);
        using var client = new HttpClient();
        string url = $"http://127.0.0.1:{http.Port}/mcp";
        var ok = await client.PostAsync(url, new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(200, (int)ok.StatusCode);
        // keep-alive: a second request on the same client
        var again = await client.PostAsync(url, new StringContent("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Equal(202, (int)again.StatusCode);

        var web = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}") };
        web.Headers.Add("Origin", "https://example.com");
        Assert.Equal(403, (int)(await client.SendAsync(web)).StatusCode);
        var local = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""") };
        local.Headers.Add("Origin", "http://localhost:6274");
        Assert.Equal(200, (int)(await client.SendAsync(local)).StatusCode);
        Assert.Equal(405, (int)(await client.GetAsync(url)).StatusCode);
        Assert.Equal(404, (int)(await client.PostAsync($"http://127.0.0.1:{http.Port}/other", new StringContent("{}"))).StatusCode);
    }

    [Theory]
    [InlineData("src/Player/Car*.cs", "src/Player/CarCatalog.cs", true)]
    [InlineData("src/Player/Car*.cs", "src/Player/Sub/Car.cs", false)]
    [InlineData("src/Player/**", "src/Player/Sub/Car.cs", true)]
    [InlineData("src/**/Car.cs", "src/Car.cs", true)]
    [InlineData("src/**/Car.cs", "src/a/b/Car.cs", true)]
    [InlineData("**/*Check.cs", "src/Player/CarCheck.cs", true)]
    [InlineData("src/Player", "src/Player/Car.cs", true)]
    [InlineData("src/Player", "src/PlayerX/Car.cs", false)]
    [InlineData("src/Player/Car.cs", "src/Player/Car.cs.uid", false)]
    [InlineData("src/Player/Car?.cs", "src/Player/Cars.cs", true)]
    public void Globs(string glob, string path, bool match) => Assert.Equal(match, PlaytestLedger.GlobMatch(glob, path));

    private static string Repo()
    {
        string dir = Path.Combine(Path.GetTempPath(), "playtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src", "Player"));
        File.WriteAllText(Path.Combine(dir, "src", "Player", "Car.cs"), "class Car {}\n");
        File.WriteAllText(Path.Combine(dir, "src", "Player", "CarCheck.cs"), "class CarCheck {}\n");
        File.WriteAllText(Path.Combine(dir, "src", "Player", "Car.cs.uid"), "uid://x\n");
        File.WriteAllText(Path.Combine(dir, "src", "Player", "Bus.cs"), "class Bus {}\n");
        return dir;
    }

    [Fact]
    public void Expand_honours_exclusions_and_skips_uids()
    {
        string root = Repo();
        try
        {
            Assert.Equal(["src/Player/Car.cs", "src/Player/CarCheck.cs"], PlaytestLedger.Expand(root, ["src/Player/Car*"]));
            Assert.Equal(["src/Player/Car.cs"], PlaytestLedger.Expand(root, ["src/Player/Car*", "!**/*Check.cs"]));
            Assert.Equal(["src/Player/Bus.cs"], PlaytestLedger.Expand(root, ["src/Player/Bus.cs"]));
            Assert.Empty(PlaytestLedger.Expand(root, ["src/Nowhere/*.cs"]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_verdict_goes_stale_only_when_its_files_change()
    {
        string root = Repo(), ledger = Path.Combine(root, "tests", "playtests");
        try
        {
            string[] covers = ["src/Player/Car.cs"];
            string hash = PlaytestLedger.CurrentHash(root, covers);
            Assert.Equal(PlaytestStatus.Pending, PlaytestLedger.StatusOf(PlaytestLedger.Read(ledger, "car"), hash));

            PlaytestLedger.Write(ledger, "car", "Car", validated: true, hash, covers, "me", "abc1234", "", DateTime.UtcNow);
            Assert.Equal(PlaytestStatus.Validated, PlaytestLedger.StatusOf(PlaytestLedger.Read(ledger, "car"), PlaytestLedger.CurrentHash(root, covers)));

            // another file changing does not matter; the covered one's line endings do not either
            File.WriteAllText(Path.Combine(root, "src", "Player", "Bus.cs"), "class Bus { int x; }\n");
            File.WriteAllText(Path.Combine(root, "src", "Player", "Car.cs"), "class Car {}\r\n");
            Assert.Equal(PlaytestStatus.Validated, PlaytestLedger.StatusOf(PlaytestLedger.Read(ledger, "car"), PlaytestLedger.CurrentHash(root, covers)));

            File.WriteAllText(Path.Combine(root, "src", "Player", "Car.cs"), "class Car { float grip; }\n");
            Assert.Equal(PlaytestStatus.Stale, PlaytestLedger.StatusOf(PlaytestLedger.Read(ledger, "car"), PlaytestLedger.CurrentHash(root, covers)));

            PlaytestLedger.Write(ledger, "car", "Car", validated: false, PlaytestLedger.CurrentHash(root, covers), covers, "me", "abc1235", "too grippy", DateTime.UtcNow);
            var record = PlaytestLedger.Read(ledger, "car")!;
            Assert.Equal("failed", record.Verdict);
            Assert.Equal(2, record.Notes.Count);
            Assert.Equal("too grippy", record.Notes[^1].Text);
            Assert.DoesNotContain("\r", File.ReadAllText(PlaytestLedger.FileOf(ledger, "car")));
        }
        finally { Directory.Delete(root, true); }
    }
}
