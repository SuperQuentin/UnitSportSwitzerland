using System.Net;
using System.Net.Http;
using Godot;
using HttpClient = System.Net.Http.HttpClient;
using UnitSport.Terrain.Format;

namespace UnitSport.Net;

/// <summary>
/// The HTTP mirror of a server's chunk directory (#651): a static file server (Caddy on the deploy
/// host) serving the same files, so bulk terrain leaves the game process and its ENet link.
///
/// <para>
/// The transfer unit is still the raw file under its ordinary name; the mirror may send it gzip or
/// brotli encoded (pre-compressed copies beside each file) and <see cref="HttpClient"/> inflates it.
/// TLS and the length check stand in for the ENet path's CRC.
/// </para>
///
/// <para>
/// A miss here is never the last word unless the mirror proved itself first: <see cref="ProbeAsync"/>
/// asks for <c>manifest.json</c>, so a 404 afterwards means "this tile has no such file" (most have
/// no .holes) and not "the URL points at the wrong directory". Anything else (timeout, 5xx, a
/// dropped connection) returns null and the caller asks over ENet; after
/// <see cref="MaxFailures"/> in a row the mirror is dropped for the session.
/// </para>
/// </summary>
public sealed class HttpAssetSource
{
    /// <summary>One client for the process: it pools connections and multiplexes on HTTP/2.</summary>
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MaxConnectionsPerServer = 8,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
        // h2 over TLS; a plain-http mirror (LAN, tests) negotiates down to 1.1
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };

    /// <summary>Consecutive transient failures before the mirror is given up for the session.</summary>
    public const int MaxFailures = 8;

    private readonly Uri _base;
    private int _failures;
    private volatile bool _disabled;

    private HttpAssetSource(Uri baseUri) => _base = baseUri;

    /// <summary>The mirror's base URL, for logs.</summary>
    public string BaseUrl => _base.ToString();

    /// <summary>False once the mirror failed <see cref="MaxFailures"/> times in a row.</summary>
    public bool Usable => !_disabled;

    /// <summary>
    /// A mirror at <paramref name="url"/> if it serves <c>manifest.json</c>, else null (logged).
    /// </summary>
    public static async Task<HttpAssetSource?> ProbeAsync(string url, CancellationToken ct = default)
    {
        if (!url.EndsWith('/')) url += "/";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
        {
            GD.PushWarning($"[stream] tiles URL '{url}' is not an http(s) URL; streaming over the game link");
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(baseUri, "manifest.json"));
            using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return new HttpAssetSource(baseUri);
            GD.PushWarning($"[stream] tiles URL {baseUri} answered {(int)response.StatusCode} for manifest.json; streaming over the game link");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            GD.PushWarning($"[stream] tiles URL {baseUri} unreachable ({e.Message}); streaming over the game link");
        }
        return null;
    }

    /// <summary>
    /// Fetches one file. The file's bytes; <c>(null, true)</c> on a 404; null when the caller should
    /// try ENet instead (a transient failure, or the mirror is given up).
    /// </summary>
    public async Task<AssetResult?> FetchAsync(AssetKind kind, TileId id, CancellationToken ct)
    {
        if (_disabled) return null;
        var uri = new Uri(_base, AssetStream.FileNameFor(kind, id));
        try
        {
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Interlocked.Exchange(ref _failures, 0);
                return new AssetResult(null, true);
            }
            if (!response.IsSuccessStatusCode) return Failed($"{(int)response.StatusCode} for {uri}");

            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _failures, 0);
            return new AssetResult(bytes, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new AssetResult(null, false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return Failed($"{e.Message} for {uri}");
        }
    }

    private AssetResult? Failed(string why)
    {
        if (Interlocked.Increment(ref _failures) >= MaxFailures && !_disabled)
        {
            _disabled = true;
            GD.PushWarning($"[stream] tiles URL {_base} failed {MaxFailures} times in a row (last: {why}); streaming over the game link for the rest of the session");
        }
        return null;
    }
}
