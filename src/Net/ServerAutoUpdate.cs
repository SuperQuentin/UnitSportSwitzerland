using Godot;
using UnitSport.Core;

namespace UnitSport.Net;

/// <summary>
/// The deployed server keeps itself on the newest release (#1088). The wire protocol moves several times a
/// day and a client of another protocol is refused before it can type <c>/update</c>, so waiting for an admin
/// locked everyone out. Every <see cref="IntervalMinutes"/>, and at once when <see cref="Handshake"/> turns
/// away a client of a newer protocol, the release feed is checked quietly; a newer release is fetched while
/// people play (<see cref="ServerUpdater"/>), then the server restarts on it as soon as nobody is connected.
/// It never interrupts a game: the connected admins are told once per release, with <c>/update</c> to restart now.
///
/// <para>
/// Only on a server started by <c>start-server.sh</c> (<see cref="ServerUpdater.Script"/>), only with a
/// release's version (a <c>tools/deploy-linux.sh</c> build has none and is left as deployed), and not with
/// <c>--no-auto-update</c>.
/// </para>
/// </summary>
public partial class ServerAutoUpdate : Node
{
    /// <summary>Minutes between two checks; <c>--auto-update-minutes N</c>.</summary>
    public const double IntervalMinutes = 10;

    /// <summary>A refused newer client checks at once, but not more often than this.</summary>
    private const double RefusalCheckGap = 60;

    private readonly ChatManager _chat;
    private readonly string _script, _current, _feed;
    private readonly double _interval;
    private double _nextCheck, _lastCheck = double.NegativeInfinity;
    private string? _told;                       // the release the admins were told about
    private readonly HashSet<long> _toldPeers = new();

    private ServerAutoUpdate(ChatManager chat, string script, string current, string feed, double interval)
    {
        Name = "AutoUpdate";
        _chat = chat;
        _script = script;
        _current = current;
        _feed = feed;
        _interval = interval;
    }

    /// <summary>The node, or null when this server does not update itself (and why, in the log).</summary>
    public static ServerAutoUpdate? Create(ChatManager chat, Handshake handshake)
    {
        if (CmdArgs.Has("--no-auto-update")) return Off("--no-auto-update");
        if (ServerUpdater.Script is not { } script) return Off("not started by start-server.sh");
        string current = ServerUpdater.RunningVersion;
        if (current.Length == 0) return Off("a local build, no release version");
        double minutes = CmdArgs.Value("--auto-update-minutes") is { } m
            && double.TryParse(m, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0
            ? v : IntervalMinutes;
        string feed = CmdArgs.Value("--updatefeed", notFlag: true) ?? UpdateInfo.ReleasesUrl;
        var node = new ServerAutoUpdate(chat, script, current, feed, minutes * 60);
        handshake.NewerPeerRefused += node.CheckSoon;
        handshake.NewerHint = node.Hint;
        PlayerInfo.AdminChanged += node.OnAdminChanged;
        GD.Print($"[update] automatic: every {minutes:0.#} min, restarts when nobody plays (v{current})");
        return node;
    }

    private static ServerAutoUpdate? Off(string why)
    {
        GD.Print($"[update] automatic update off: {why}");
        return null;
    }

    private static double Now => Time.GetTicksMsec() / 1000.0;

    public override void _Ready() => _nextCheck = Now + 30;   // the first check once the server has settled

    public override void _ExitTree() => PlayerInfo.AdminChanged -= OnAdminChanged;

    public override void _Process(double delta)
    {
        // a release is waiting and nobody plays: now is the time
        if (_chat.Updater.ReadyTag is { } tag && !_chat.Updater.Busy && _chat.PlayerCount == 0)
        {
            _chat.RestartForUpdate(tag);
            return;
        }
        if (Now >= _nextCheck) Check();
    }

    /// <summary>A client of a newer protocol was refused: a release is surely out.</summary>
    private void CheckSoon()
    {
        if (Now - _lastCheck >= RefusalCheckGap) Check();
    }

    private void Check()
    {
        _nextCheck = Now + _interval;
        if (_chat.Updater.Busy) return;
        _lastCheck = Now;
        _chat.Updater.Start(_script, _current, _feed, quiet: true, (line, _) => TellAdmins(line), Fetched);
    }

    private void Fetched(string? tag)
    {
        if (tag == null)
        {
            if (_chat.RestartRequested) TellAdmins("No update to install: no newer release, or its download failed.");
            _chat.RestartRequested = false;
            return;
        }
        if (_chat.RestartRequested || _chat.PlayerCount == 0)
        {
            _chat.RestartForUpdate(tag);
            return;
        }
        if (_told != tag)
        {
            _told = tag;
            _toldPeers.Clear();
        }
        foreach (long peer in _chat.AdminPeers) TellOnce(peer);
    }

    /// <summary>An admin who joins, or logs in, while a release waits hears about it too.</summary>
    private void OnAdminChanged(PlayerInfo player)
    {
        if (player.IsAdmin && _told != null && _chat.Updater.ReadyTag == _told)
            Callable.From(() => TellOnce(player.PeerId)).CallDeferred();   // after the join's own lines
    }

    private void TellOnce(long peer)
    {
        if (_told == null || !_toldPeers.Add(peer)) return;
        _chat.Tell(peer, $"[update] {_told} is out and downloaded: this server restarts on it once nobody is playing. "
            + "/update restarts it now (everyone is kicked).", ChatKind.Admin);
    }

    private void TellAdmins(string line)
    {
        GD.Print($"[update] {line}");
        foreach (long peer in _chat.AdminPeers) _chat.Tell(peer, line, ChatKind.Admin);
    }

    /// <summary>What a refused newer client reads: this server catches up by itself.</summary>
    private string Hint() => _chat.Updater.ReadyTag is { } tag
        ? $"It installs {tag} as soon as nobody is playing."
        : "It updates itself within a few minutes once nobody is playing.";
}
