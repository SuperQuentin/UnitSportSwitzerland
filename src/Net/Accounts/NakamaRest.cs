using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HttpClient = System.Net.Http.HttpClient;

namespace UnitSport.Net.Accounts;

public sealed record NakamaSession(string Token, string RefreshToken);

public sealed record NakamaAccount(string UserId, string Username);

/// <summary>An account server's refusal or an unreachable one, with the sentence a player reads.</summary>
public sealed class NakamaException(string message) : Exception(message);

/// <summary>
/// The few Nakama REST calls accounts need (#1123, docs/notes/net/nakama.md), on plain HTTP: no client library.
/// The client signs in and refreshes with the server key (basic auth); a game server checks a player's session
/// token by reading that player's account with it, so it needs no secret of Nakama's at all.
/// </summary>
public static class NakamaRest
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Signs in by email and password; <paramref name="create"/> makes the account when it does not exist.</summary>
    public static Task<NakamaSession> SignIn(string url, string serverKey, string email, string password, bool create, string? username = null)
    {
        string q = $"?create={(create ? "true" : "false")}" + (string.IsNullOrEmpty(username) ? "" : "&username=" + Uri.EscapeDataString(username));
        return Session(url, serverKey, "/v2/account/authenticate/email" + q, new { email, password });
    }

    /// <summary>
    /// Asks the site for a mail with a link to choose a new password (#1257); the answer is the same whether or not an
    /// account has this email. Throws <see cref="NakamaException"/> with a sentence when the server has no reset.
    /// </summary>
    public static async Task ForgotPassword(string url, string email)
    {
        string site = AccountRules.SiteOf(url);
        if (site.Length == 0) throw new NakamaException("This account server cannot reset passwords by email.");
        using var req = new HttpRequestMessage(HttpMethod.Post, site + "/api/password/forgot")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { email }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Requested-With", "houses"); // the layout service's CSRF header
        using var res = await Send(req);
        if (res.IsSuccessStatusCode) return;
        throw new NakamaException(Message(await res.Content.ReadAsStringAsync(), res.StatusCode) is var m && m.StartsWith("The account server answered", StringComparison.Ordinal)
            ? "The password reset is not available right now." : m);
    }

    /// <summary>A new session from the refresh token, while it lasts.</summary>
    public static Task<NakamaSession> Refresh(string url, string serverKey, string refreshToken) =>
        Session(url, serverKey, "/v2/account/session/refresh", new { token = refreshToken });

    /// <summary>The account a session token belongs to; null when the account server refuses the token.</summary>
    public static async Task<NakamaAccount?> Account(string url, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, AccountRules.NormalizeUrl(url) + "/v2/account");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await Send(req);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return null;
        string body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new NakamaException(Message(body, res.StatusCode));
        using var doc = JsonDocument.Parse(body);
        var user = doc.RootElement.GetProperty("user");
        return new NakamaAccount(user.GetProperty("id").GetString() ?? "", user.TryGetProperty("username", out var n) ? n.GetString() ?? "" : "");
    }

    /// <summary>
    /// The name of the admin a session token belongs to, from the module's <c>me</c> RPC (#1211: admins are the group
    /// "admins"); null when the token is refused or is no admin's. Throws <see cref="NakamaException"/> when the account
    /// server cannot be reached.
    /// </summary>
    public static async Task<string?> AdminName(string url, string token, CancellationToken cancel = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, AccountRules.NormalizeUrl(url) + "/v2/rpc/me")
        {
            // an RPC's body is a JSON string holding the payload
            Content = new StringContent("\"{}\"", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await Send(req, cancel);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return null;
        string body = await res.Content.ReadAsStringAsync(cancel);
        if (!res.IsSuccessStatusCode) throw new NakamaException(Message(body, res.StatusCode));
        using var doc = JsonDocument.Parse(body);
        using var me = JsonDocument.Parse(doc.RootElement.GetProperty("payload").GetString() ?? "{}");
        bool admin = me.RootElement.TryGetProperty("admin", out var a) && a.ValueKind == JsonValueKind.True;
        return admin && me.RootElement.TryGetProperty("username", out var n) ? n.GetString() ?? "admin" : null;
    }

    /// <summary>
    /// The signed-in player's own storage object <paramref name="collection"/>/<paramref name="key"/> (#1423, the birthday), its
    /// value as JSON text; null when there is none. Throws <see cref="NakamaException"/> when the server refuses or does not answer.
    /// </summary>
    public static async Task<string?> ReadOwn(string url, string token, string collection, string key)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{AccountRules.NormalizeUrl(url)}/v2/storage/{Uri.EscapeDataString(collection)}?limit=100");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await Send(req);
        string body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new NakamaException(Message(body, res.StatusCode));
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Array) return null;
        foreach (var o in objects.EnumerateArray())
            if (o.TryGetProperty("key", out var k) && k.GetString() == key && o.TryGetProperty("value", out var v)) return v.GetString();
        return null;
    }

    /// <summary>Writes the signed-in player's own storage object (#1423), readable and writable by its owner only.</summary>
    public static async Task WriteOwn(string url, string token, string collection, string key, string json)
    {
        var body = new { objects = new[] { new { collection, key, value = json, permission_read = 1, permission_write = 1 } } };
        using var req = new HttpRequestMessage(HttpMethod.Put, AccountRules.NormalizeUrl(url) + "/v2/storage")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await Send(req);
        if (!res.IsSuccessStatusCode) throw new NakamaException(Message(await res.Content.ReadAsStringAsync(), res.StatusCode));
    }

    private static async Task<NakamaSession> Session(string url, string serverKey, string path, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, AccountRules.NormalizeUrl(url) + path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(serverKey + ":")));
        using var res = await Send(req);
        string text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new NakamaException(Message(text, res.StatusCode));
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        return new NakamaSession(root.GetProperty("token").GetString() ?? "",
            root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? "" : "");
    }

    private static async Task<HttpResponseMessage> Send(HttpRequestMessage req, CancellationToken cancel = default)
    {
        try { return await Http.SendAsync(req, cancel); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            throw new NakamaException($"The account server at {req.RequestUri?.GetLeftPart(UriPartial.Authority)} does not answer.");
        }
    }

    private static string? JsonErrorOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Nakama's own sentence when it sent one ({"message": ...}), else the status.</summary>
    private static string Message(string body, HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } s) return s;
        }
        catch (JsonException) { }
        if (JsonErrorOf(body) is { Length: > 0 } e) return char.ToUpperInvariant(e[0]) + e[1..] + (e.EndsWith('.') ? "" : ".");
        return status == HttpStatusCode.Unauthorized ? "The account server's key is wrong." : $"The account server answered {(int)status}.";
    }
}
