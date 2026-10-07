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
/// Nothing runs on its own: only the command starts it.
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

    /// <summary>True from the check until it ends, and for good once an update is ready (the server is quitting).</summary>
    public bool Busy { get; private set; }

    /// <summary>The update script, or null when this server was not started by <c>start-server.sh</c>.</summary>
    public static string? Script =>
        System.Environment.GetEnvironmentVariable(ScriptEnv) is { Length: > 0 } path && File.Exists(path) ? path : null;

    /// <summary>
    /// Checks <paramref name="feed"/> and, when a newer release is there, fetches it. <paramref name="say"/>
    /// gets each line to show (true = to everyone), <paramref name="done"/> the tag once it is unpacked and
    /// ready, or null (up to date, or failed: already said). Both are called on the main thread.
    /// </summary>
    public void Start(string script, string current, string feed, Action<string, bool> say, Action<string?> done)
    {
        Busy = true;
        static void Post(Action a) => Callable.From(a).CallDeferred();
        _ = Task.Run(async () =>
        {
            string? ready = null;
            try { ready = await Run(script, current, feed, (line, all) => Post(() => say(line, all))); }
            catch (Exception e) { Post(() => say($"Update failed: {e.Message}", false)); }
            Post(() => { Busy = ready != null; done(ready); });
        });
    }

    private static async Task<string?> Run(string script, string current, string feed, Action<string, bool> say)
    {
        var releases = UpdateInfo.ParseList(await Client.GetStringAsync(feed));
        string running = current.Length > 0 ? $"v{current}" : "a local build";
        if (UpdateInfo.ServerUpdate(releases, current, "Linux") is not { } plan)
        {
            say(releases.Count == 0 ? "Update: could not read the release list." : $"No newer release: this server runs {running}.", false);
            return null;
        }

        var (release, archive) = plan;
        GD.Print($"[update] {running} -> {release.Tag}: fetching {archive.Url}");
        say($"[server] Update {release.Tag} found ({archive.Size / (1024 * 1024)} MB), downloading. "
            + "Everyone is kicked and the server restarts when it is ready.", true);

        var (code, lastLine) = await Exec(script, "fetch", release.Tag, archive.Url);
        if (code != 0)
        {
            GD.PushWarning($"[update] fetch exited {code}: {lastLine}");
            say($"[server] Update {release.Tag} failed ({lastLine}); the server keeps running {running}.", true);
            return null;
        }
        return release.Tag;
    }

    /// <summary>Runs the update script through bash; its exit code and last output line (the reason, on a failure).</summary>
    private static async Task<(int Code, string LastLine)> Exec(string script, params string[] args)
    {
        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
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
