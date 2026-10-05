using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// The update check (#532): once per session the title screen asks GitHub for the releases; when
/// the latest is newer than this build, a modal offers to update or Later.
/// <list type="bullet">
/// <item>Delta update, when every release since this one has a delta for this platform and they
/// weigh less than the full archive (<see cref="UpdateInfo.DeltaChain"/>): the deltas are
/// downloaded, staged beside the install on a worker thread (<see cref="UpdatePackage.Staging"/>, every
/// hash checked), and <see cref="UpdateScript"/> swaps them in when the game quits, restarting it if asked.</item>
/// <item>Otherwise, or when patching fails: the full archive into the Downloads folder, to unpack by hand.</item>
/// </list>
/// A development build (no stamped <c>config/version</c>), an editor run (never patched) and a headless
/// run never ask. <c>--fakeversion 0.0.1</c> pretends to be an old build, <c>--updatefeed URL</c> reads
/// another release list, <c>--updateaccept</c> answers yes to every question (end-to-end checks).
/// </summary>
public partial class UpdatePrompt : Node
{
    private static readonly string[] Headers = { "User-Agent: UnitSportSwitzerland", "Accept: application/vnd.github+json" };

    private static bool _checked;
    /// <summary>A newer release found but not offered yet (the title was covered when the answer came).</summary>
    private static List<UpdateInfo.Release>? _pending;

    private sealed record Job(string Url, string Path, long Size);

    private TitleScreen _title = null!;
    private HttpRequest? _download;
    private readonly Queue<Job> _jobs = new();
    private Job? _job;
    private long _doneBytes, _totalBytes;
    private Action? _jobsDone;
    private Action<string>? _jobsFailed;
    private Modal? _progress;
    private ProgressBar? _bar;
    private Label? _status;
    private Task? _install;
    private UpdatePackage.Staging? _staging;

    private static string CurrentVersion =>
        CmdArgs.Value("--fakeversion") ?? (string)ProjectSettings.GetSetting("application/config/version", "");
    private static bool AutoAccept => CmdArgs.Has("--updateaccept");

    /// <summary>Starts the check from the title screen (once per session), or offers what it already found.</summary>
    public static UpdatePrompt? Attach(TitleScreen title)
    {
        if (DisplayServer.GetName() == "headless" || CurrentVersion.Length == 0) return null;
        var p = new UpdatePrompt { Name = "UpdatePrompt", _title = title };
        title.AddChild(p);
        if (!_checked)
        {
            _checked = true;
            p.Check();
        }
        return p;
    }

    /// <summary>The title came to the front: offer a release found while it was covered.</summary>
    public void OnTitleShown()
    {
        if (_pending != null) Callable.From(Offer).CallDeferred();
    }

    private void Check()
    {
        var http = new HttpRequest { Timeout = 10 };
        AddChild(http);
        http.RequestCompleted += (result, code, _, body) =>
        {
            http.QueueFree();
            if (result != (long)HttpRequest.Result.Success || code != 200)
            {
                GD.Print($"[update] no answer from GitHub (result {result}, HTTP {code})");
                return;
            }
            var releases = UpdateInfo.ParseList(body.GetStringFromUtf8()).ToList();
            var latest = UpdateInfo.Latest(releases);
            if (latest == null || !UpdateInfo.IsNewer(latest.Tag, CurrentVersion)) return;
            GD.Print($"[update] {latest.Tag} is out (this build is v{CurrentVersion})");
            _pending = releases;
            Offer();
        };
        if (http.Request(CmdArgs.Value("--updatefeed") ?? UpdateInfo.ReleasesUrl, Headers) != Error.Ok) http.QueueFree();
    }

    private static string Size(long bytes) =>
        bytes < 1 << 20 ? $"{Math.Max(1, bytes / 1024)} KB" : $"{bytes / 1048576.0:0.#} MB";

    /// <summary>Where the game is installed: the executable's folder, the .app bundle on macOS.</summary>
    private static string InstallRoot()
    {
        string exeDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(OS.GetExecutablePath()))!;
        return OS.GetName() == "macOS" ? System.IO.Path.GetFullPath(System.IO.Path.Combine(exeDir, "..", "..")) : exeDir;
    }

    /// <summary>An exported build whose install folder we may write to; never the editor's own folder.</summary>
    private static bool CanPatch()
    {
        if (!OS.HasFeature("template")) return false;
        try
        {
            string probe = System.IO.Path.Combine(InstallRoot(), ".update-probe");
            System.IO.File.WriteAllText(probe, "");
            System.IO.File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private void Ask(string title, string message, string okText, Action ok, string cancelText = "Later", Action? cancel = null)
    {
        if (AutoAccept) { GD.Print($"[update] {title}: {okText} (--updateaccept)"); ok(); return; }
        Modal.Confirm(_title, title, message, okText, ok, cancel, cancelText: cancelText);
    }

    private void Offer()
    {
        if (_pending is not { } releases || !_title.IsVisibleInTree() || Modal.Current != null) return;
        _pending = null;
        var latest = UpdateInfo.Latest(releases)!;
        var full = UpdateInfo.AssetFor(latest, OS.GetName());
        var chain = UpdateInfo.DeltaChain(releases, CurrentVersion, OS.GetName());
        long deltaBytes = chain?.Sum(c => c.Delta.Size) ?? long.MaxValue;
        string intro = $"UnitSport {latest.Tag} is out; you are playing v{CurrentVersion}.";

        if (chain != null && full != null && deltaBytes < full.Size * UpdateInfo.DeltaWorthIt && CanPatch())
        {
            string steps = chain.Count > 1 ? $" in {chain.Count} steps" : "";
            Ask("New version available",
                $"{intro} Update now{steps}? {Size(deltaBytes)} to download instead of {Size(full.Size)}; the game restarts to finish.",
                "Update", () => Patch(latest, chain, full));
        }
        else if (full != null)
            Ask("New version available", $"{intro} Download it now ({Size(full.Size)})?", "Download", () => DownloadFull(latest, full));
        else
            Ask("New version available", intro, "Open release page", () => OS.ShellOpen(latest.PageUrl));
    }

    // ---- downloads: a queue of files behind one progress modal

    private void Fetch(string title, IEnumerable<Job> jobs, Action done, Action<string> failed)
    {
        _jobs.Clear();
        foreach (var j in jobs) _jobs.Enqueue(j);
        _doneBytes = 0;
        _totalBytes = _jobs.Sum(j => j.Size);
        _jobsDone = done; _jobsFailed = failed;
        _progress = Modal.Progress(_title, title, null, CancelAll, out var bar, out var status);
        _bar = bar; _status = status;
        NextJob();
    }

    private void NextJob()
    {
        if (!_jobs.TryDequeue(out var job))
        {
            _progress?.CloseModal(); _progress = null;
            _jobsDone?.Invoke();
            return;
        }
        _job = job;
        string part = job.Path + ".part";
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(job.Path)!);
        _download = new HttpRequest { DownloadFile = part, DownloadChunkSize = 1 << 20, Timeout = 0 };
        AddChild(_download);
        _download.RequestCompleted += (result, code, _, _) =>
        {
            var http = _download; _download = null;
            http?.QueueFree();
            if (result == (long)HttpRequest.Result.Success && code == 200)
            {
                try
                {
                    System.IO.File.Move(part, job.Path, overwrite: true);
                    _doneBytes += job.Size;
                    NextJob();
                    return;
                }
                catch (IOException e) { GD.PushWarning($"[update] {e.Message}"); }
            }
            System.IO.File.Delete(part);
            JobsFailed(result != (long)HttpRequest.Result.Success ? $"Error {(HttpRequest.Result)result}." : $"HTTP {code}.");
        };
        if (_download.Request(job.Url, Headers) != Error.Ok)
        {
            _download.QueueFree(); _download = null;
            JobsFailed("The request could not start.");
        }
    }

    private void JobsFailed(string why)
    {
        _jobs.Clear();
        _progress?.CloseModal(); _progress = null;
        _jobsFailed?.Invoke(why);
    }

    private void CancelAll()
    {
        _progress = null;
        _jobs.Clear();
        if (_download == null) return;
        _download.CancelRequest();
        _download.QueueFree();
        _download = null;
        if (_job != null) System.IO.File.Delete(_job.Path + ".part");
    }

    public override void _Process(double delta)
    {
        if (_bar == null || _status == null) return;
        if (_download != null)
        {
            long got = _doneBytes + _download.GetDownloadedBytes();
            _bar.Value = _totalBytes > 0 ? (double)got / _totalBytes : 0;
            _status.Text = $"{Size(got)} / {Size(_totalBytes)}";
        }
        else if (_install != null && _staging != null)
            _status.Text = $"{Size(_staging.Done)} written";
        if (_install is { IsCompleted: true } done) Installed(done);
    }

    // ---- full archive

    private void DownloadFull(UpdateInfo.Release release, UpdateInfo.Asset asset)
    {
        string dir = OS.GetSystemDir(OS.SystemDir.Downloads);
        if (dir.Length == 0 || !DirAccess.DirExistsAbsolute(dir)) dir = OS.GetUserDataDir();
        string file = System.IO.Path.Combine(dir, asset.Name);
        Fetch($"Downloading {release.Tag}", new[] { new Job(asset.Url, file, asset.Size) },
            () =>
            {
                string mac = OS.GetName() == "macOS" ? " On a Mac, run xattr -cr UnitSportSwitzerland.app once before opening it." : "";
                Ask("Download complete",
                    $"{release.Tag} is saved as {file}. Quit this game, unpack the archive and play the new version.{mac}",
                    "Show file", () => OS.ShellShowInFileManager(file), cancelText: "Close");
            },
            why => Ask("Download failed", $"{why} You can get {release.Tag} from the release page instead.",
                "Open release page", () => OS.ShellOpen(release.PageUrl), cancelText: "Close"));
    }

    // ---- delta update

    private IReadOnlyList<(string From, string To, string File)> _steps = Array.Empty<(string, string, string)>();
    private UpdateInfo.Release? _target;
    private UpdateInfo.Asset? _full;

    private static string DeltaDir => System.IO.Path.Combine(OS.GetUserDataDir(), "updates");

    private void Patch(UpdateInfo.Release latest, IReadOnlyList<(string From, string To, UpdateInfo.Asset Delta)> chain, UpdateInfo.Asset full)
    {
        _target = latest; _full = full;
        _steps = chain.Select(c => (c.From, c.To, System.IO.Path.Combine(DeltaDir, c.Delta.Name))).ToList();
        Fetch($"Downloading {latest.Tag}", chain.Select(c => new Job(c.Delta.Url, System.IO.Path.Combine(DeltaDir, c.Delta.Name), c.Delta.Size)),
            Install, PatchFailed);
    }

    private void Install()
    {
        string root = InstallRoot();
        var steps = _steps;
        _progress = Modal.Progress(_title, $"Installing {_target!.Tag}", "Checking and staging the new files…", () => { }, out var bar, out var status);
        _bar = bar; _status = status;
        bar.Visible = false;
        _install = Task.Run(() =>
        {
            _staging = new UpdatePackage.Staging(root);
            foreach (var (from, to, file) in steps)
            {
                using var f = System.IO.File.OpenRead(file);
                _staging.Apply(f, from, to);
            }
            _staging.Finish();
        });
    }

    private void Installed(Task task)
    {
        _install = null;
        _progress?.CloseModal(); _progress = null;
        try { System.IO.Directory.Delete(DeltaDir, true); } catch (IOException) { }
        if (task.Exception?.InnerException is { } e)
        {
            GD.PushWarning($"[update] patching failed: {e.Message}");
            if (_staging != null) try { System.IO.Directory.Delete(_staging.Dir, true); } catch (IOException) { }
            _staging = null;
            PatchFailed(e.Message);
            return;
        }
        GD.Print($"[update] {_target!.Tag} staged in {_staging!.Dir}");
        Ask("Update ready", $"{_target.Tag} is ready. Restart now, or it is installed when you quit.",
            "Restart now", () => Swap(true), cancelText: "When I quit", cancel: () => Swap(false));
    }

    /// <summary>Starts the swap script, which waits for this process to end; then quits if restarting.</summary>
    private void Swap(bool restart)
    {
        bool windows = OS.GetName() == "Windows";
        string script = System.IO.Path.Combine(_staging!.Dir, UpdateScript.FileName(windows));
        System.IO.File.WriteAllText(script, UpdateScript.Text(windows, OS.GetProcessId(), InstallRoot(),
            restart ? System.IO.Path.GetFullPath(OS.GetExecutablePath()) : null), new System.Text.UTF8Encoding(windows));
        var (program, args) = UpdateScript.Command(windows, script);
        int pid = OS.CreateProcess(program, args);
        GD.Print($"[update] swap script {script}: pid {pid}");
        if (pid <= 0) { PatchFailed("The installer script could not start."); return; }
        if (restart) _title.Shell.Quit();
    }

    private void PatchFailed(string why)
    {
        if (_target is not { } release) return;
        if (_full is { } full)
            Ask("Update failed", $"{why} Download the full {release.Tag} instead ({Size(full.Size)})?",
                "Download", () => DownloadFull(release, full), cancelText: "Close");
        else
            Ask("Update failed", why, "Open release page", () => OS.ShellOpen(release.PageUrl), cancelText: "Close");
    }

    public override void _ExitTree() => CancelAll();
}
