using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UnitSport.Net;

/// <summary>One HTTP request as <see cref="LoopbackServer"/> read it: header names lower case, the path with its query.</summary>
public sealed record LoopbackRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body, bool Close)
{
    /// <summary>The path without its query string.</summary>
    public string Route => Path.Split('?')[0];

    /// <summary>A query parameter, or null.</summary>
    public string? Query(string name)
    {
        int q = Path.IndexOf('?');
        if (q < 0) return null;
        foreach (string pair in Path[(q + 1)..].Split('&'))
        {
            int eq = pair.IndexOf('=');
            string key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            if (key == name) return eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return null;
    }
}

/// <summary>An answer: status, media type (charset utf-8 added) and body.</summary>
public sealed record LoopbackResponse(int Status, string Type, string Body)
{
    public static LoopbackResponse Text(int status, string body) => new(status, "text/plain", body);
    public static LoopbackResponse Json(string json) => new(200, "application/json", json);
}

/// <summary>
/// Just enough of HTTP/1.1 on a plain <see cref="TcpListener"/> bound to 127.0.0.1: requests with a Content-Length body,
/// keep-alive, a header and body cap. Plain sockets rather than <c>HttpListener</c>, which on Windows wants a URL
/// reservation (admin) for anything but its own idea of localhost. Shared by the playtest MCP endpoint
/// (<c>Playtest/LoopbackHttp</c>, #751) and the server's admin API (<see cref="AdminApi"/>, #1211). No Godot in it.
/// </summary>
public sealed class LoopbackServer : IDisposable
{
    private readonly int _maxBody;
    private const int MaxHeader = 32 << 10;

    private readonly TcpListener _listener;
    private readonly Func<LoopbackRequest, CancellationToken, Task<LoopbackResponse>> _answer;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    /// <summary>Listens on 127.0.0.1:<paramref name="port"/> (0: any free port) and answers with <paramref name="answer"/>.</summary>
    public LoopbackServer(int port, Func<LoopbackRequest, CancellationToken, Task<LoopbackResponse>> answer, int maxBody = 4 << 20)
    {
        _answer = answer;
        _maxBody = maxBody;
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
                LoopbackResponse response;
                try { response = await _answer(request, _cts.Token); }
                catch (Exception e) { response = LoopbackResponse.Text(500, e.Message); }
                await Write(stream, response);
                if (request.Close) return;
            }
        }
        catch (Exception) { /* the client went away mid-request */ }
    }

    private async Task<LoopbackRequest?> ReadRequest(NetworkStream stream)
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
        if (length < 0 || length > _maxBody) return null;
        var body = new byte[length];
        for (int read = 0; read < length;)
        {
            int got = await stream.ReadAsync(body.AsMemory(read));
            if (got == 0) return null;
            read += got;
        }
        bool close = headers.TryGetValue("connection", out var c) && c.Equals("close", StringComparison.OrdinalIgnoreCase);
        return new LoopbackRequest(start[0].ToUpperInvariant(), start[1], headers, Encoding.UTF8.GetString(body), close);
    }

    private static bool EndsWithBlankLine(List<byte> b) =>
        b.Count >= 4 && b[^4] == '\r' && b[^3] == '\n' && b[^2] == '\r' && b[^1] == '\n';

    private static async Task Write(NetworkStream stream, LoopbackResponse r)
    {
        byte[] payload = Encoding.UTF8.GetBytes(r.Body);
        string reason = r.Status switch
        {
            200 => "OK", 202 => "Accepted", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
            405 => "Method Not Allowed", 409 => "Conflict", 502 => "Bad Gateway", 503 => "Service Unavailable", _ => "Error",
        };
        string head = $"HTTP/1.1 {r.Status} {reason}\r\nContent-Type: {r.Type}; charset=utf-8\r\nContent-Length: {payload.Length}\r\n"
            + "Cache-Control: no-store\r\n" + (r.Status == 405 ? "Allow: POST\r\n" : "") + "\r\n";
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
