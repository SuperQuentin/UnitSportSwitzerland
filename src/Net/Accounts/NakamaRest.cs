using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

    private static async Task<HttpResponseMessage> Send(HttpRequestMessage req)
    {
        try { return await Http.SendAsync(req); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            throw new NakamaException($"The account server at {req.RequestUri?.GetLeftPart(UriPartial.Authority)} does not answer.");
        }
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
        return status == HttpStatusCode.Unauthorized ? "The account server's key is wrong." : $"The account server answered {(int)status}.";
    }
}
