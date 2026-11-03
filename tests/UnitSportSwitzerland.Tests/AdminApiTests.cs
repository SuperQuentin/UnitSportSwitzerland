using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UnitSport.Net;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The server's admin API (#1211) over real loopback HTTP, with a fake account server and a fake game.</summary>
public sealed class AdminApiTests : IDisposable
{
    private sealed class FakeGame : IAdminBackend
    {
        public readonly List<string> Commands = new();
        public Task<IReadOnlyList<string>> Command(string admin, string line)
        {
            Commands.Add($"{admin}:{line}");
            return Task.FromResult<IReadOnlyList<string>>([$"ran {line}"]);
        }
        public Task<object> Status() => Task.FromResult<object>(new { version = "1.2.3", players = 1 });
        public Task<IReadOnlyList<AdminPlayer>> Players() =>
            Task.FromResult<IReadOnlyList<AdminPlayer>>([new AdminPlayer(7, "Heidi", "heidi_92", false, false, 41, 2600000.5, 1200000.25)]);
        public Task<(string Line, bool Refused)> Kick(string admin, long peer, string reason) =>
            Task.FromResult(peer == 7 ? ($"Heidi was kicked by {admin} ({reason})", false) : ("No such player.", true));
        public Task<(string Line, bool Refused)> Restart(string admin, int minutes) => Task.FromResult(($"restart in {minutes}", false));
    }

    private readonly FakeGame _game = new();
    private readonly AdminLogRing _log = new(keep: 5);
    private readonly string _startup = Path.Combine(Path.GetTempPath(), $"adminapi-{Guid.NewGuid():N}.args");
    private readonly AdminApi _api;
    private readonly HttpClient _http = new();
    private int _verifies;
    private DateTime _now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    public AdminApiTests()
    {
        _api = new AdminApi(0, (token, _) =>
        {
            Interlocked.Increment(ref _verifies);
            return token switch
            {
                "admin-token" => Task.FromResult<string?>("Quentin"),
                "down" => throw new HttpRequestException("unreachable"),
                _ => Task.FromResult<string?>(null),
            };
        }, _game, _log, _startup, now: () => _now);
    }

    public void Dispose()
    {
        _api.Dispose();
        _http.Dispose();
        File.Delete(_startup);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(string method, string path, string? token = "admin-token", string? body = null)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{_api.Port}{path}");
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req);
        string text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task Without_an_admin_token_nothing_answers()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call("GET", "/status", token: null)).Status);
        var (status, body) = await Call("GET", "/status", token: "player-token");
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("not an admin", body.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadGateway, (await Call("GET", "/status", token: "down")).Status);
        Assert.Empty(_game.Commands);
    }

    [Fact]
    public async Task An_admin_token_is_checked_once_a_minute()
    {
        Assert.Equal(HttpStatusCode.OK, (await Call("GET", "/status")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call("GET", "/players")).Status);
        Assert.Equal(1, _verifies);
        _now += AdminApi.CacheFor + TimeSpan.FromSeconds(1);
        Assert.Equal(HttpStatusCode.OK, (await Call("GET", "/status")).Status);
        Assert.Equal(2, _verifies);
    }

    [Fact]
    public async Task Status_players_and_commands_reach_the_game()
    {
        Assert.Equal("1.2.3", (await Call("GET", "/status")).Body.GetProperty("version").GetString());

        var player = (await Call("GET", "/players")).Body.GetProperty("players")[0];
        Assert.Equal(7, player.GetProperty("peer").GetInt64());
        Assert.Equal("heidi_92", player.GetProperty("account").GetString());
        Assert.Equal(41, player.GetProperty("pingMs").GetInt32());

        var (status, body) = await Call("POST", "/command", body: """{"line":"time 14"}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ran time 14", body.GetProperty("lines")[0].GetString());
        Assert.Equal("Quentin:time 14", _game.Commands.Single());

        Assert.Equal(HttpStatusCode.BadRequest, (await Call("POST", "/command", body: """{"line":"  "}""")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call("POST", "/command", body: "not json")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call("POST", "/update")).Status);
        Assert.Equal("Quentin:update", _game.Commands[^1]);
    }

    [Fact]
    public async Task Kick_and_restart_say_what_happened()
    {
        var (status, body) = await Call("POST", "/kick", body: """{"peer":7,"reason":"spam"}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Heidi was kicked by Quentin (spam)", body.GetProperty("line").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Call("POST", "/kick", body: """{"peer":8}""")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call("POST", "/kick", body: "{}")).Status);

        Assert.Equal("restart in 5", (await Call("POST", "/restart", body: """{"minutes":5}""")).Body.GetProperty("line").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Call("POST", "/restart", body: """{"minutes":90}""")).Status);
    }

    [Fact]
    public async Task The_log_is_read_from_a_sequence_number()
    {
        for (int i = 1; i <= 7; i++) _log.Add($"line {i}");
        var all = (await Call("GET", "/log")).Body;
        Assert.Equal(7, all.GetProperty("last").GetInt64());
        // the ring keeps five
        Assert.Equal(["line 3", "line 4", "line 5", "line 6", "line 7"], all.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()));

        var after = (await Call("GET", "/log?after=6")).Body.GetProperty("lines");
        Assert.Equal("line 7", after.EnumerateArray().Single().GetProperty("text").GetString());
        Assert.Equal(7, after[0].GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task Startup_arguments_are_checked_then_saved()
    {
        Assert.Equal("", (await Call("GET", "/startup")).Body.GetProperty("args").GetString());

        var (status, body) = await Call("POST", "/startup", body: """{"args":"  --server-name   Alps  --accounts required "}""");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("--server-name Alps --accounts required", body.GetProperty("args").GetString());
        Assert.Equal("--server-name Alps --accounts required\n", File.ReadAllText(_startup));
        Assert.Contains("Quentin (website) set the startup arguments", _log.Since(0, 10).Lines.Single().Line);

        Assert.Equal(HttpStatusCode.BadRequest, (await Call("POST", "/startup", body: """{"args":"--port 1"}""")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call("POST", "/startup", body: """{"args":"--name x; rm -rf /"}""")).Status);
        Assert.Equal("--server-name Alps --accounts required\n", File.ReadAllText(_startup));
    }

    [Fact]
    public async Task Unknown_calls_and_wrong_methods_are_refused()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Call("GET", "/nope")).Status);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Call("GET", "/command")).Status);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("--server-name Alps", "--server-name Alps")]
    [InlineData("--accounts required --nakama-url http://127.0.0.1:7350", "--accounts required --nakama-url http://127.0.0.1:7350")]
    [InlineData("--spawn 2600000,1200000", "--spawn 2600000,1200000")]
    public void Startup_args_accepted(string args, string tidy)
    {
        var (got, error) = StartupArgs.Check(args);
        Assert.Null(error);
        Assert.Equal(tidy, got);
    }

    [Theory]
    [InlineData("--server-name 'Alps'")]
    [InlineData("--name $(id)")]
    [InlineData("--a\n--b")]
    [InlineData("server-name Alps")]
    [InlineData("--admin-http 1")]
    [InlineData("--headless")]
    public void Startup_args_refused(string args) => Assert.NotNull(StartupArgs.Check(args).Error);
}
