using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;

namespace UnitSport.Net.Accounts;

/// <summary>What <c>user://online_account.json</c> holds: never the password, only the tokens it bought.</summary>
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
/// <c>user://online_account.json</c>, apart from <c>settings.json</c>, so its tokens never travel with a settings file.
/// A server asks for it after the version check (<see cref="AccountGate"/>); <see cref="TokenFor"/> answers.
/// <para>
/// <c>--account-email E --account-password P [--account-url U] [--account-key K]</c> signs in (making the account
/// when needed) for a command-line run, in memory only: checks never touch the saved account.
/// </para>
/// </summary>
public static class Account
{
    private const string File = "user://online_account.json";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static AccountFile _current = Load();
    private static readonly string? ArgPassword = CmdArgs.Value("--account-password");
    private static readonly bool Transient = ArgPassword != null;

    /// <summary>Raised on the main thread after a sign-in, sign-out or change of mode or server.</summary>
    public static event Action? Changed;

    public static AccountMode Mode => _current.Mode;
    /// <summary>The account server: the one set under Advanced, else the build's official one (<see cref="Official"/>).</summary>
    public static string Url => _current.Url.Length > 0 ? _current.Url : Official.Url;
    public static string ServerKey => _current.Url.Length > 0 ? _current.ServerKey : Official.ServerKey;

    /// <summary>
    /// The official account server this build signs in to (#1123): <c>res://assets/account-server.json</c>,
    /// <c>{"url": "...", "serverKey": "..."}</c>, filled in once Nakama is deployed (<c>tools/deploy-linux.sh --nakama</c>
    /// prints both). The server key is not a secret: every client carries it. Empty: the player sets one under Advanced.
    /// </summary>
    public static (string Url, string ServerKey) Official { get; } = LoadOfficial();

    /// <summary>A server set under Advanced, not the official one.</summary>
    public static bool CustomServer => _current.Url.Length > 0 && !AccountRules.SameServer(_current.Url, Official.Url);
    public static string Email => _current.Email;
    public static string Username => _current.Username;

    /// <summary>Online with a session that can still be refreshed.</summary>
    public static bool SignedIn => _current.Mode == AccountMode.Online && _current.RefreshToken.Length > 0;

    /// <summary>The account as the title screen shows it (#1209).</summary>
    public static (bool SignedIn, bool Offline, string Username) Current() => (SignedIn, Mode == AccountMode.Offline, Username);

    /// <summary>One line for the settings page and the menus.</summary>
    public static string Status => _current.Mode == AccountMode.Offline ? "Offline: no account"
        : SignedIn ? $"Online as {_current.Username} on {Url}"
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
        // the official server again: stored empty, so a later build's new official address is followed
        if (url.Length == 0 || (AccountRules.SameServer(url, Official.Url) && serverKey == Official.ServerKey)) url = serverKey = "";
        if (url == _current.Url && serverKey == _current.ServerKey) return;
        if (!AccountRules.SameServer(url.Length > 0 ? url : Official.Url, Url)) ForgetSession();
        _current.Url = url;
        _current.ServerKey = serverKey;
        Save();
    }

    /// <summary>Signs in, or makes the account first (<paramref name="create"/>); null on success, else what went wrong.</summary>
    public static async Task<string?> SignIn(string email, string password, bool create, string? username = null)
    {
        if (Url.Length == 0) return "This build names no account server: set one under Advanced.";
        try
        {
            var session = await NakamaRest.SignIn(Url, ServerKey, email.Trim(), password, create, username);
            var account = await NakamaRest.Account(Url, session.Token)
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
            return $"The account server at {Url} did not answer as a Nakama server does.";
        }
    }

    /// <summary>Asks for the reset mail (#1257); null on success, else what went wrong.</summary>
    public static async Task<string?> ForgotPassword(string email)
    {
        if (Url.Length == 0) return "This build names no account server: set one under Advanced.";
        if (!email.Contains('@')) return "Type the email of your account.";
        try { await NakamaRest.ForgotPassword(Url, email.Trim()); return null; }
        catch (NakamaException e) { return e.Message; }
    }

    /// <summary>
    /// Reads the account's name again (#1359): renamed on the website (<c>account.html</c>), the game follows when its
    /// Account screen opens or the window gets focus back. Quiet when offline, signed out or the server does not answer.
    /// </summary>
    public static async Task RefreshName()
    {
        if (!SignedIn || Transient || _refreshing) return;
        _refreshing = true;
        try
        {
            string server = Url, token = await TokenFor(server);
            if (token.Length == 0 || !AccountRules.SameServer(server, Url)) return;
            if (await NakamaRest.Account(server, token) is { Username.Length: > 0 } account && account.Username != _current.Username && SignedIn)
            {
                _current.Username = account.Username;
                Save();
            }
        }
        catch (Exception e) when (e is NakamaException or JsonException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
        {
            GD.Print($"[accounts] the name could not be read again: {e.Message}");
        }
        finally { _refreshing = false; }
    }

    private static bool _refreshing;

    /// <summary>The website's account page (#1359), or "" when the account server sits behind no site.</summary>
    public static string ManagePage => AccountRules.SiteOf(Url) is { Length: > 0 } site ? site + "/account.html" : "";

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
        if (_current.Mode != AccountMode.Online || !AccountRules.SameServer(server, Url)) return "";
        if (AccountRules.Fresh(_current.Token, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))) return _current.Token;
        if (_current.RefreshToken.Length > 0)
        {
            try
            {
                var s = await NakamaRest.Refresh(Url, ServerKey, _current.RefreshToken);
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

    private static (string, string) LoadOfficial()
    {
        const string path = "res://assets/account-server.json";
        try
        {
            if (!Godot.FileAccess.FileExists(path)) return ("", "");
            using var doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(path));
            var root = doc.RootElement;
            string url = root.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            string key = root.TryGetProperty("serverKey", out var k) ? k.GetString() ?? "" : "";
            return (AccountRules.NormalizeUrl(url), key.Trim());
        }
        catch (JsonException e)
        {
            GD.PushWarning($"[accounts] {path} unreadable: {e.Message}");
            return ("", "");
        }
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
