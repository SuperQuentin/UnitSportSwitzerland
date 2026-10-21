using System.Diagnostics;
using Godot;
using HttpClient = System.Net.Http.HttpClient;
using UnitSport.Core;

namespace UnitSport.Net;

/// <summary>
/// <c>/update</c> on a dedicated server deployed by <c>tools/deploy-linux.sh</c> (#730). Reads GitHub's
/// release list; when a newer release exists, the host's <c>update-server.sh fetch</c> downloads and
/// unpacks it while everyone keeps playing. <see cref="ChatManager"/> then kicks everyone and quits, and
/// <c>start-server.sh</c>, finding the pending update, switches <c>current</c> and starts the new build.
/// <see cref="ServerAutoUpdate"/> also runs it quietly on a timer (#1088): the release is fetched while people
/// play and waits in <see cref="ReadyTag"/> until the server is empty.
/// </summary>
public sealed class ServerUpdater
{
    /// <summary>The host script's path, exported by <c>start-server.sh</c>; unset = this server cannot update itself.</summary>
    public const string ScriptEnv = "UNITSPORT_UPDATER";

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    static ServerUpdater()
    {
        // GitHub's API refuses a request without a User-Agent
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("UnitSportSwitzerland");
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>True from the check until it ends.</summary>
    public bool Busy { get; private set; }

    /// <summary>A release fetched and unpacked (<c>pending-update</c> written), waiting for a restart; null: none.</summary>
    public string? ReadyTag { get; private set; }

    /// <summary>The release this server runs: <c>config/version</c>, stamped by a release; empty for a local build.
    /// <c>--fakeversion</c> pretends, as on the client (<c>Ui/UpdatePrompt</c>), for the checks.</summary>
    public static string RunningVersion =>
        CmdArgs.Value("--fakeversion") ?? (string)ProjectSettings.GetSetting("application/config/version", "");

    /// <summary>The update script, or null when this server was not started by <c>start-server.sh</c>.</summary>
    public static string? Script =>
        System.Environment.GetEnvironmentVariable(ScriptEnv) is { Length: > 0 } path && File.Exists(path) ? path : null;

    /// <summary>
    /// Checks <paramref name="feed"/> and, when a newer release is there, fetches it. <paramref name="say"/>
    /// gets each line to show (true = to everyone), <paramref name="done"/> the tag once it is unpacked and
    /// ready, or null (up to date, or failed: already said). Both are called on the main thread.
    /// <paramref name="quiet"/> (the automatic check): nothing said when up to date, nothing to everyone.
    /// A release already in <see cref="ReadyTag"/> is not fetched again.
    /// </summary>
    public void Start(string script, string current, string feed, bool quiet, Action<string, bool> say, Action<string?> done)
    {
        Busy = true;
        string? already = ReadyTag;
        static void Post(Action a) => Callable.From(a).CallDeferred();
        _ = Task.Run(async () =>
        {
            string? ready = null;
            try { ready = await Run(script, current, feed, quiet, already, (line, all) => Post(() => say(line, all))); }
            catch (Exception e)
            {
                // the automatic check every few minutes: a feed that is down goes to the log, not to the admins
                if (quiet) GD.PushWarning($"[update] check failed: {e.Message}");
                else Post(() => say($"Update failed: {e.Message}", false));
            }
            Post(() =>
            {
                Busy = false;
                if (ready != null) ReadyTag = ready;
                done(ready);
            });
        });
    }

    private static async Task<string?> Run(string script, string current, string feed, bool quiet, string? already, Action<string, bool> say)
    {
        var releases = UpdateInfo.ParseList(await Client.GetStringAsync(feed));
        string running = current.Length > 0 ? $"v{current}" : "a local build";
        if (UpdateInfo.ServerUpdate(releases, current, "Linux") is not { } plan)
        {
            if (releases.Count == 0 && quiet) GD.PushWarning("[update] could not read the release list");
            else if (releases.Count == 0) say("Update: could not read the release list.", false);
            else if (!quiet) say($"No newer release: this server runs {running}.", false);
            return null;
        }

        var (release, archive) = plan;
        if (release.Tag == already) return already;   // fetched by an earlier check, unpacked and waiting
        GD.Print($"[update] {running} -> {release.Tag}: fetching {archive.Url}");
        if (!quiet)
            say($"[server] Update {release.Tag} found ({archive.Size / (1024 * 1024)} MB), downloading. "
                + "Everyone is kicked and the server restarts when it is ready.", true);

        var (code, lastLine) = await Exec(script, "fetch", release.Tag, archive.Url);
        if (code != 0)
        {
            GD.PushWarning($"[update] fetch exited {code}: {lastLine}");
            say($"[server] Update {release.Tag} failed ({lastLine}); the server keeps running {running}.", !quiet);
            return null;
        }
        return release.Tag;
    }

    /// <summary>Runs the update script through bash; its exit code and last output line (the reason, on a failure).</summary>
    private static async Task<(int Code, string LastLine)> Exec(string script, params string[] args)
    {
        // UNITSPORT_BASH: which bash, for a check on Windows, whose `bash` is WSL's from System32 before any PATH entry
        string bash = System.Environment.GetEnvironmentVariable("UNITSPORT_BASH") is { Length: > 0 } b ? b : "bash";
        var start = new ProcessStartInfo(bash) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(script);
        foreach (string a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start) ?? throw new InvalidOperationException("bash did not start");
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        string all = (await output) + (await errors);
        foreach (string line in all.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            GD.Print($"[update] {line}");
        string last = all.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "no output";
        return (p.ExitCode, last);
    }
}
