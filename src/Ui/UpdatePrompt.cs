using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// The update check (#532): once per session the title screen asks GitHub for the latest release;
/// when it is newer than this build, a modal offers Download or Later. Download fetches this
/// platform's archive into the Downloads folder with a progress bar, then offers to show it. A
/// development build (no stamped <c>config/version</c>) and a headless run never ask;
/// <c>--fakeversion 0.0.1</c> pretends to be an old build to try it.
/// </summary>
public partial class UpdatePrompt : Node
{
    private static readonly string[] Headers = { "User-Agent: UnitSportSwitzerland", "Accept: application/vnd.github+json" };

    private static bool _checked;
    /// <summary>A newer release found but not offered yet (the title was covered when the answer came).</summary>
    private static UpdateInfo.Release? _pending;

    private TitleScreen _title = null!;
    private HttpRequest? _download;
    private Modal? _progress;
    private ProgressBar? _bar;
    private Label? _status;
    private long _expected;
    private string _file = "";

    private static string CurrentVersion =>
        CmdArgs.Value("--fakeversion") ?? (string)ProjectSettings.GetSetting("application/config/version", "");

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
            var release = UpdateInfo.Parse(body.GetStringFromUtf8());
            if (release == null || !UpdateInfo.IsNewer(release.Tag, CurrentVersion)) return;
            GD.Print($"[update] {release.Tag} is out (this build is v{CurrentVersion})");
            _pending = release;
            Offer();
        };
        if (http.Request(UpdateInfo.LatestUrl, Headers) != Error.Ok) http.QueueFree();
    }

    private void Offer()
    {
        if (_pending is not { } release || !_title.IsVisibleInTree() || Modal.Current != null) return;
        _pending = null;
        var asset = UpdateInfo.AssetFor(release, OS.GetName());
        string size = asset is { Size: > 0 } ? $" ({asset.Size / 1048576.0:0} MB)" : "";
        Modal.Confirm(_title, "New version available",
            $"UnitSport {release.Tag} is out; you are playing v{CurrentVersion}. Download it now{size}?",
            asset != null ? "Download" : "Open release page",
            () => { if (asset != null) Download(release, asset); else OS.ShellOpen(release.PageUrl); },
            cancelText: "Later");
    }

    private void Download(UpdateInfo.Release release, UpdateInfo.Asset asset)
    {
        string dir = OS.GetSystemDir(OS.SystemDir.Downloads);
        if (dir.Length == 0 || !DirAccess.DirExistsAbsolute(dir)) dir = OS.GetUserDataDir();
        _file = dir.PathJoin(asset.Name);
        _expected = asset.Size;
        string part = _file + ".part";

        _download = new HttpRequest { DownloadFile = part, DownloadChunkSize = 1 << 20, Timeout = 0 };
        AddChild(_download);
        _progress = Modal.Progress(_title, $"Downloading {release.Tag}", asset.Name, CancelDownload, out var bar, out var status);
        _bar = bar; _status = status;
        _download.RequestCompleted += (result, code, _, _) =>
        {
            var http = _download; _download = null;
            http?.QueueFree();
            _progress?.CloseModal(); _progress = null;
            if (result == (long)HttpRequest.Result.Success && code == 200)
            {
                DirAccess.RemoveAbsolute(_file);
                if (DirAccess.RenameAbsolute(part, _file) == Error.Ok) { Done(release); return; }
            }
            DirAccess.RemoveAbsolute(part);
            Failed(release, result != (long)HttpRequest.Result.Success ? $"Error {(HttpRequest.Result)result}." : $"HTTP {code}.");
        };
        if (_download.Request(asset.Url, Headers) != Error.Ok)
        {
            _download.QueueFree(); _download = null;
            _progress.CloseModal(); _progress = null;
            Failed(release, "The request could not start.");
        }
    }

    public override void _Process(double delta)
    {
        if (_download == null || _bar == null || _status == null) return;
        long got = _download.GetDownloadedBytes();
        long total = _download.GetBodySize() > 0 ? _download.GetBodySize() : _expected;
        _bar.Value = total > 0 ? (double)got / total : 0;
        _status.Text = total > 0 ? $"{got / 1048576.0:0.0} / {total / 1048576.0:0.0} MB" : $"{got / 1048576.0:0.0} MB";
    }

    private void CancelDownload()
    {
        _progress = null;
        if (_download == null) return;
        _download.CancelRequest();
        _download.QueueFree();
        _download = null;
        DirAccess.RemoveAbsolute(_file + ".part");
    }

    private void Done(UpdateInfo.Release release)
    {
        string mac = OS.GetName() == "macOS" ? " On a Mac, run xattr -cr UnitSportSwitzerland.app once before opening it." : "";
        Modal.Confirm(_title, "Download complete",
            $"{release.Tag} is saved as {_file}. Quit this game, unpack the archive and play the new version.{mac}",
            "Show file", () => OS.ShellShowInFileManager(_file), cancelText: "Close");
    }

    private void Failed(UpdateInfo.Release release, string why) =>
        Modal.Confirm(_title, "Download failed", $"{why} You can get {release.Tag} from the release page instead.",
            "Open release page", () => OS.ShellOpen(release.PageUrl), cancelText: "Close");

    public override void _ExitTree() => CancelDownload();
}
