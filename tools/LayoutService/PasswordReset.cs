using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace UnitSport.LayoutService;

/// <summary>
/// Forgotten account passwords (#1257): the two server-to-server RPCs of Nakama's module (<c>password_reset_issue</c>,
/// <c>password_reset_apply</c>, called with its http key). Nakama keeps the single-use token; the service mails the link.
/// </summary>
public sealed class PasswordReset(string nakamaUrl, string httpKey)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ConcurrentDictionary<string, DateTime> _asked = new();

    public bool Configured => nakamaUrl.Length > 0 && httpKey.Length > 0;

    /// <summary>False when this address was asked for less than a minute ago (it keeps its earlier mail's token).</summary>
    public bool MayAsk(string email)
    {
        var now = DateTime.UtcNow;
        foreach (var old in _asked.Where(kv => now - kv.Value > TimeSpan.FromMinutes(1)).Select(kv => kv.Key)) _asked.TryRemove(old, out _);
        return _asked.TryAdd(email, now);
    }

    /// <summary>A fresh token for the account with this email, or null when there is none (or it is banned).</summary>
    public async Task<string?> Issue(string email)
    {
        using var doc = JsonDocument.Parse(await Rpc("password_reset_issue", new { email }) ?? "{}");
        return doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
    }

    /// <summary>True when the password was set; false for a token that is wrong, spent or expired.</summary>
    public async Task<bool> Apply(string token, string password) => await Rpc("password_reset_apply", new { token, password }) != null;

    // null on Nakama's 400 (a bad argument, or a bad token); any other failure throws
    private async Task<string?> Rpc(string id, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{nakamaUrl.TrimEnd('/')}/v2/rpc/{id}?unwrap&http_key={Uri.EscapeDataString(httpKey)}")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        using var res = await Http.SendAsync(req);
        string text = await res.Content.ReadAsStringAsync();
        if (res.StatusCode == System.Net.HttpStatusCode.BadRequest) return null;
        res.EnsureSuccessStatusCode();
        return text;
    }
}
