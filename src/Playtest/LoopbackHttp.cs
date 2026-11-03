using UnitSport.Net;

namespace UnitSport.Playtest;

/// <summary>
/// The MCP "Streamable HTTP" transport, just enough of it (#751): <c>POST /mcp</c> carries one
/// JSON-RPC body and gets the reply as <c>application/json</c> (202 for a notification); <c>GET</c>
/// has no event stream to offer, so 405. The transport is <see cref="LoopbackServer"/>, shared with the
/// server's admin API.
///
/// <para>
/// It listens on 127.0.0.1 only. Browsers can still reach a loopback port, so a request whose
/// <c>Host</c> is not this machine (DNS rebinding) or that carries an <c>Origin</c> (a web page)
/// is refused: only a local program such as Claude Code drives the game.
/// </para>
/// </summary>
public sealed class LoopbackHttp : IDisposable
{
    private readonly LoopbackServer _server;
    private readonly Func<string, CancellationToken, Task<string?>> _handle;

    public int Port => _server.Port;

    /// <summary>Listens on 127.0.0.1:<paramref name="port"/> (0: any free port) and answers with <paramref name="handle"/>.</summary>
    public LoopbackHttp(int port, Func<string, CancellationToken, Task<string?>> handle)
    {
        _handle = handle;
        _server = new LoopbackServer(port, Answer);
    }

    private async Task<LoopbackResponse> Answer(LoopbackRequest r, CancellationToken token)
    {
        string host = r.Headers.GetValueOrDefault("host", "");
        string hostName = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
        if (hostName is not ("127.0.0.1" or "localhost" or "[::1]")) return LoopbackResponse.Text(403, "host not allowed");
        if (r.Headers.TryGetValue("origin", out var origin) && origin != "null" && !IsLocalOrigin(origin))
            return LoopbackResponse.Text(403, "browser origins are not allowed");
        if (r.Route != "/mcp") return LoopbackResponse.Text(404, "the playtest MCP endpoint is /mcp");
        if (r.Method == "GET") return LoopbackResponse.Text(405, "no event stream: POST JSON-RPC to /mcp");
        if (r.Method == "DELETE") return LoopbackResponse.Text(200, "");
        if (r.Method != "POST") return LoopbackResponse.Text(405, "POST only");
        string? reply = await _handle(r.Body, token);
        return reply == null ? LoopbackResponse.Text(202, "") : LoopbackResponse.Json(reply);
    }

    /// <summary>An Origin naming this machine is a local tool's, not a website's (the MCP inspector sends one).</summary>
    private static bool IsLocalOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.Host is "127.0.0.1" or "localhost" or "[::1]";

    public void Dispose() => _server.Dispose();
}
