using System;
using System.Text;
using System.Text.Json;

namespace UnitSport.Net.Accounts;

/// <summary>What a server asks of a joining player's online account (#1123, <c>--accounts</c>).</summary>
public enum ServerAccounts
{
    /// <summary>No account step: the join goes on right after the version check, as before #1123.</summary>
    Off,
    /// <summary>A signed-in player joins with their account checked; an offline one joins as before.</summary>
    Optional,
    /// <summary>Only a player signed in to this server's account server joins.</summary>
    Required,
}

/// <summary>Which identity this install joins with (#1123, Settings › Account).</summary>
public enum AccountMode
{
    /// <summary>No account: the install's <see cref="PlayerKey"/> is the only identity.</summary>
    Offline,
    /// <summary>Signed in to a Nakama account server (docs/notes/net/nakama.md).</summary>
    Online,
}

/// <summary>
/// The account rules, without Godot (unit tested): the server mode from its argument, whether a peer is admitted,
/// the account server's address in one spelling, and when a Nakama session token runs out.
/// </summary>
public static class AccountRules
{
    /// <summary><c>off</c>, <c>optional</c> or <c>required</c> (any case); null for anything else.</summary>
    public static ServerAccounts? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" => ServerAccounts.Off,
        "optional" => ServerAccounts.Optional,
        "required" => ServerAccounts.Required,
        _ => null,
    };

    /// <summary>
    /// Null when the peer is admitted, else the reason it reads. <paramref name="presented"/>: it sent a session
    /// token; <paramref name="verified"/>: the account server accepted that token. An optional server admits a
    /// token that failed as an offline player: the account is a bonus there, never a lock.
    /// </summary>
    public static string? Refusal(ServerAccounts mode, bool presented, bool verified, string accountServer)
    {
        if (mode != ServerAccounts.Required || verified) return null;
        return presented
            ? "This server could not check your online account (signed out or expired). Sign in again under Settings › Account, then join again."
            : $"This server requires an online account on {accountServer}. Sign in under Settings › Account, then join again.";
    }

    /// <summary>An account server's address in one spelling: trimmed, no trailing slash, http:// when none is given.</summary>
    public static string NormalizeUrl(string? url)
    {
        string u = (url ?? "").Trim().TrimEnd('/');
        if (u.Length == 0) return "";
        return u.Contains("://", StringComparison.Ordinal) ? u : "http://" + u;
    }

    /// <summary>True when both addresses name the same account server.</summary>
    public static bool SameServer(string? a, string? b) =>
        NormalizeUrl(a).Length > 0 && string.Equals(NormalizeUrl(a), NormalizeUrl(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The expiry (<c>exp</c>) of a Nakama session token, a JWT; null when it is not one.</summary>
    public static DateTimeOffset? TokenExpiry(string? jwt)
    {
        var parts = (jwt ?? "").Split('.');
        if (parts.Length != 3) return null;
        try
        {
            string b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out long s)
                ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException) { return null; }
    }

    /// <summary>A token worth presenting: it runs for at least <paramref name="margin"/> more.</summary>
    public static bool Fresh(string? jwt, DateTimeOffset now, TimeSpan margin) =>
        TokenExpiry(jwt) is { } exp && exp - now > margin;
}
