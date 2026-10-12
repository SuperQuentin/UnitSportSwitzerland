using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UnitSport.Playtest;

/// <summary>
/// The MCP "Streamable HTTP" transport, just enough of it (#751): <c>POST /mcp</c> carries one
/// JSON-RPC body and gets the reply as <c>application/json</c> (202 for a notification); <c>GET</c>
/// has no event stream to offer, so 405. Plain sockets rather than <c>HttpListener</c>, which on
/// Windows wants a URL reservation (admin) for anything but its own idea of localhost.
///
/// <para>
/// It listens on 127.0.0.1 only. Browsers can still reach a loopback port, so a request whose
/// <c>Host</c> is not this machine (DNS rebinding) or that carries an <c>Origin</c> (a web page)
/// is refused: only a local program such as Claude Code drives the game.
/// </para>
/// </summary>
public sealed class LoopbackHttp : IDisposable
{
    private const int MaxBody = 4 << 20, MaxHeader = 32 << 10;

    private readonly TcpListener _listener;
    private readonly Func<string, CancellationToken, Task<string?>> _handle;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    /// <summary>Listens on 127.0.0.1:<paramref name="port"/> (0: any free port) and answers with <paramref name="handle"/>.</summary>
    public LoopbackHttp(int port, Func<string, CancellationToken, Task<string?>> handle)
    {
        _handle = handle;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(Accept);
    }

    private async Task Accept()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }   // stopped
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            // keep-alive: a client may send its next request on the same connection
            while (!_cts.IsCancellationRequested && await ReadRequest(stream) is { } request)
            {
                var (status, type, body) = await Answer(request);
                await Write(stream, status, type, body);
                if (request.Close) return;
            }
        }
        catch (Exception) { /* the client went away mid-request */ }
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers, string Body, bool Close);

    private async Task<(int Status, string Type, string Body)> Answer(Request r)
    {
        string host = r.Headers.GetValueOrDefault("host", "");
        string hostName = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
        if (hostName is not ("127.0.0.1" or "localhost" or "[::1]")) return (403, "text/plain", "host not allowed");
        if (r.Headers.TryGetValue("origin", out var origin) && origin != "null" && !IsLocalOrigin(origin))
            return (403, "text/plain", "browser origins are not allowed");
        if (r.Path.Split('?')[0] != "/mcp") return (404, "text/plain", "the playtest MCP endpoint is /mcp");
        if (r.Method == "GET") return (405, "text/plain", "no event stream: POST JSON-RPC to /mcp");
        if (r.Method == "DELETE") return (200, "text/plain", "");
        if (r.Method != "POST") return (405, "text/plain", "POST only");
        string? reply = await _handle(r.Body, _cts.Token);
        return reply == null ? (202, "text/plain", "") : (200, "application/json", reply);
    }

    /// <summary>An Origin naming this machine is a local tool's, not a website's (the MCP inspector sends one).</summary>
    private static bool IsLocalOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.Host is "127.0.0.1" or "localhost" or "[::1]";

    private static async Task<Request?> ReadRequest(NetworkStream stream)
    {
        var head = new List<byte>(1024);
        var one = new byte[1];
        // the header, byte by byte up to the blank line: requests are small and few
        while (!EndsWithBlankLine(head))
        {
            if (await stream.ReadAsync(one) == 0) return null;
            head.Add(one[0]);
            if (head.Count > MaxHeader) return null;
        }
        string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
        string[] start = lines[0].Split(' ');
        if (start.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        int length = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out int n) ? n : 0;
        if (length < 0 || length > MaxBody) return null;
        var body = new byte[length];
        for (int read = 0; read < length;)
        {
            int got = await stream.ReadAsync(body.AsMemory(read));
            if (got == 0) return null;
            read += got;
        }
        bool close = headers.TryGetValue("connection", out var c) && c.Equals("close", StringComparison.OrdinalIgnoreCase);
        return new Request(start[0].ToUpperInvariant(), start[1], headers, Encoding.UTF8.GetString(body), close);
    }

    private static bool EndsWithBlankLine(List<byte> b) =>
        b.Count >= 4 && b[^4] == '\r' && b[^3] == '\n' && b[^2] == '\r' && b[^1] == '\n';

    private static async Task Write(NetworkStream stream, int status, string type, string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        string reason = status switch { 200 => "OK", 202 => "Accepted", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {payload.Length}\r\n"
            + (status == 405 ? "Allow: POST\r\n" : "") + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
