using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;

namespace UnitSport.Net.Accounts;

/// <summary>What <c>user://account.json</c> holds: never the password, only the tokens it bought.</summary>
public sealed class AccountFile
{
    public AccountMode Mode { get; set; } = AccountMode.Offline;
    /// <summary>The account server (Nakama's API, e.g. http://host:7350) and its server key, which is not a secret.</summary>
    public string Url { get; set; } = "";
    public string ServerKey { get; set; } = "";
    public string Email { get; set; } = "";
    public string Username { get; set; } = "";
    public string Token { get; set; } = "";
    public string RefreshToken { get; set; } = "";
}

/// <summary>
/// This install's account (#1123): offline (the default) or signed in to a Nakama account server. Kept in
/// <c>user://account.json</c>, apart from <c>settings.json</c>, so its tokens never travel with a settings file.
/// A server asks for it after the version check (<see cref="AccountGate"/>); <see cref="TokenFor"/> answers.
/// <para>
/// <c>--account-email E --account-password P [--account-url U] [--account-key K]</c> signs in (making the account
/// when needed) for a command-line run, in memory only: checks never touch the saved account.
/// </para>
/// </summary>
public static class Account
{
    private const string File = "user://account.json";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static AccountFile _current = Load();
    private static readonly string? ArgPassword = CmdArgs.Value("--account-password");
    private static readonly bool Transient = ArgPassword != null;

    /// <summary>Raised on the main thread after a sign-in, sign-out or change of mode or server.</summary>
    public static event Action? Changed;

    public static AccountMode Mode => _current.Mode;
    public static string Url => _current.Url;
    public static string ServerKey => _current.ServerKey;
    public static string Email => _current.Email;
    public static string Username => _current.Username;

    /// <summary>Online with a session that can still be refreshed.</summary>
    public static bool SignedIn => _current.Mode == AccountMode.Online && _current.RefreshToken.Length > 0;

    /// <summary>One line for the settings page and the menus.</summary>
    public static string Status => _current.Mode == AccountMode.Offline ? "Offline: no account"
        : SignedIn ? $"Online as {_current.Username} on {_current.Url}"
        : "Online, not signed in";

    public static void SetMode(AccountMode mode)
    {
        if (_current.Mode == mode) return;
        _current.Mode = mode;
        Save();
    }

    /// <summary>A new account server forgets the session of the old one.</summary>
    public static void SetServer(string url, string serverKey)
    {
        url = AccountRules.NormalizeUrl(url);
        serverKey = serverKey.Trim();
        if (url == _current.Url && serverKey == _current.ServerKey) return;
        if (!AccountRules.SameServer(url, _current.Url)) ForgetSession();
        _current.Url = url;
        _current.ServerKey = serverKey;
        Save();
    }

    /// <summary>Signs in, or makes the account first (<paramref name="create"/>); null on success, else what went wrong.</summary>
    public static async Task<string?> SignIn(string email, string password, bool create, string? username = null)
    {
        if (_current.Url.Length == 0) return "Set the account server first.";
        try
        {
            var session = await NakamaRest.SignIn(_current.Url, _current.ServerKey, email.Trim(), password, create, username);
            var account = await NakamaRest.Account(_current.Url, session.Token)
                ?? throw new NakamaException("The account server refused its own new session.");
            _current.Mode = AccountMode.Online;
            _current.Email = email.Trim();
            _current.Username = account.Username;
            _current.Token = session.Token;
            _current.RefreshToken = session.RefreshToken;
            Save();
            return null;
        }
        catch (NakamaException e) { return e.Message; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
        {
            return $"The account server at {_current.Url} did not answer as a Nakama server does.";
        }
    }

    public static void SignOut()
    {
        ForgetSession();
        Save();
    }

    /// <summary>
    /// The session token to present to a server whose accounts live on <paramref name="server"/>, refreshed when it
    /// is about to run out; "" when offline, signed in elsewhere, or the session cannot be renewed.
    /// </summary>
    public static async Task<string> TokenFor(string server)
    {
        if (_current.Mode != AccountMode.Online || !AccountRules.SameServer(server, _current.Url)) return "";
        if (AccountRules.Fresh(_current.Token, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))) return _current.Token;
        if (_current.RefreshToken.Length > 0)
        {
            try
            {
                var s = await NakamaRest.Refresh(_current.Url, _current.ServerKey, _current.RefreshToken);
                _current.Token = s.Token;
                if (s.RefreshToken.Length > 0) _current.RefreshToken = s.RefreshToken;
                Save();
                return _current.Token;
            }
            catch (NakamaException e) { GD.Print($"[accounts] the session could not be renewed: {e.Message}"); }
        }
        // a command-line run signs in on demand; a player signs in again from the settings
        if (Transient && await SignIn(_current.Email, ArgPassword!, create: true) is { } why)
            GD.Print($"[accounts] sign-in failed: {why}");
        return AccountRules.Fresh(_current.Token, DateTimeOffset.UtcNow, TimeSpan.Zero) ? _current.Token : "";
    }

    private static void ForgetSession()
    {
        _current.Token = _current.RefreshToken = _current.Username = "";
    }

    private static AccountFile Load()
    {
        if (CmdArgs.Value("--account-email") is { } email)
            return new AccountFile
            {
                Mode = AccountMode.Online,
                Email = email,
                Url = AccountRules.NormalizeUrl(CmdArgs.Value("--account-url") ?? "http://127.0.0.1:7350"),
                ServerKey = CmdArgs.Value("--account-key") ?? "",
            };
        try
        {
            string path = ProjectSettings.GlobalizePath(File);
            return System.IO.File.Exists(path)
                ? JsonSerializer.Deserialize<AccountFile>(System.IO.File.ReadAllText(path), Options) ?? new AccountFile()
                : new AccountFile();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            GD.PushWarning($"[accounts] {File} unreadable, offline: {e.Message}");
            return new AccountFile();
        }
    }

    private static void Save()
    {
        if (!Transient) JsonStore.Save(File, _current, Options);
        Changed?.Invoke();
    }
}
